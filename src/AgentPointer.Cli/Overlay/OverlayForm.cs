using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using AgentPointer.Cli.Interop;
using AgentPointer.Core.Protocol;
using AgentPointer.Core.Rendering;

namespace AgentPointer.Cli.Overlay;

/// <summary>牌子上要画的东西。颜色用 <see cref="ArrowColor"/>（上游 Rec.709 语义）。</summary>
public sealed record SignContent(string? Text, ArrowColor Color)
{
    public string DisplayText => !string.IsNullOrWhiteSpace(Text) ? Text.Trim()
        : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "请查看箭头所指位置" : "Look at the indicated location";
}

/// <summary>
/// 覆盖层窗口。
///
/// <para><b>逐像素透明</b>走 <c>UpdateLayeredWindow</c> + 32bpp PArgb 位图，而不是
/// WinForms 的 <c>BackColor = Transparent</c>（Form 上直接抛"控件不支持透明的背景色"），
/// 也不是 <c>TransparencyKey</c>（抗锯齿边缘会和色键混出毛边）。
/// 淡出使用分层合成的 SourceConstantAlpha，不混用 <c>Form.Opacity</c>。</para>
///
/// <para><b>点击规则</b>（对齐上游 bigarrow）：只有牌子、箭杆和关闭按钮可点，点下去就收场；
/// 目标及其周围一圈、箭头头部和空白像素一律穿透到下层应用。</para>
///
/// <para><b>穿透只能靠 <c>WS_EX_TRANSPARENT</c>，不能只靠 <c>WM_NCHITTEST</c></b>：
/// <c>HTTRANSPARENT</c> 只把点击转给<em>同一线程</em>的下层窗口；发给别的进程的点击
/// 仍会被分层窗口在 alpha&gt;0 的像素上吞掉——而箭头头部、杆子、投影正好都画在
/// 目标之上。所以这里默认<strong>整窗穿透</strong>：<c>_hoverTimer</c> 每
/// <c>HoverPollMs</c> 毫秒轮询一次指针位置，只有指针真正落在可点区域里时才摘掉
/// <c>WS_EX_TRANSPARENT</c>（并压暗示意可点），离开后立刻装回去。
/// 按住鼠标左键期间一律不切换，免得拖动中途这次点击落到错误的窗口上。
/// <c>WM_NCHITTEST</c> 保留为第二道防线（同一线程内更精确）。</para>
///
/// <para><c>WS_EX_NOACTIVATE</c> + <c>MA_NOACTIVATE</c> 保证焦点不动。</para>
///
/// <para>窗口只覆盖箭头 + 牌子的包围盒，而不是整块显示器：小得多、绘制快得多，
/// 而且包围盒之外的点击天然就落不到我们身上。</para>
/// </summary>
internal sealed class OverlayForm : Form
{
    private Placement.Result? _layout;
    private SignContent? _sign;
    private PointerCommand? _command;
    private Point _target;
    private Rectangle _windowRect;
    private ArrowMetrics _metrics = ArrowMetrics.For(ArrowSize.Medium);
    private float _fontSize;

    /// <summary>动画相位：入场弹出 → 常驻呼吸 → 淡出。</summary>
    private enum AnimPhase { Idle, Intro, Pulse, FadeOut }

    private readonly System.Windows.Forms.Timer _animTimer;
    private AnimPhase _phase = AnimPhase.Idle;
    private long _phaseStart;
    private int _fadeDurationMs = PointerStyle.FadeOutMs;
    private bool _fadingOut;
    private double _fadeFrom = 1.0;

    private double _scale = 1.0;
    private double _alpha = 1.0;
    private double _animScale = 1.0;

    /// <summary>
    /// 帧缓冲复用：呼吸动画 30fps 全场景重绘时，每帧 new 一个 ~1.6MB 位图会产生
    /// ~48MB/s 的 GC 压力。这里只在窗口尺寸变化时重新分配。
    /// </summary>
    private Bitmap? _frameBuffer;

    /// <summary>可点区域（牌子 / 箭杆 / 关闭按钮），布局确定后算一次。</summary>
    private HitRegions? _hitRegions;
    private OverlayHit _hover = OverlayHit.None;
    private double _hoverDim = 1.0;

    /// <summary>
    /// 悬停轮询计时器。窗口默认全程带 <c>WS_EX_TRANSPARENT</c>（完全穿透），
    /// 只有指针进入可点区域时由它临时关掉穿透。
    /// </summary>
    private readonly System.Windows.Forms.Timer _hoverTimer;

    /// <summary>当前是否整窗穿透。初值 true：刚显示的一瞬间不该吃掉任何点击。</summary>
    private bool _clickThrough = true;

    /// <summary>悬停轮询间隔。30ms ≈ 33Hz：察觉不到延迟，开销可以忽略。</summary>
    private const int HoverPollMs = 30;

    /// <summary>悬停时的整体不透明度：压暗一点，示意"点这里可以收掉"。</summary>
    private const double HoverDim = 0.85;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += OnAnimTick;

        _hoverTimer = new System.Windows.Forms.Timer { Interval = HoverPollMs };
        _hoverTimer.Tick += (_, _) => PollHover();
    }

    /// <summary>当前 DPI 比例。</summary>
    public double DpiScale => _scale;

    public Rectangle WindowRect => _windowRect;

    /// <summary>WinForms 里"显示时不抢焦点"的正确开关（对应 WPF 的 ShowActivated）。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// 用户点了牌子 / 箭杆 / 关闭按钮时请求收场。参数即停止原因
    /// （<see cref="StopReason.Clicked"/> 或 <see cref="StopReason.Closed"/>）。
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<StopReason>? DismissRequested { get; set; }

    /// <summary>牌子的中心（全局物理像素），doctor 自检用。</summary>
    public Point SignCenterScreen => _layout is null
        ? Point.Empty
        : new Point(
            (int)Math.Round(_layout.SignRect.X + _layout.SignRect.Width / 2f),
            (int)Math.Round(_layout.SignRect.Y + _layout.SignRect.Height / 2f));

    // ---------------- 窗口样式 ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var ex = NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_LAYERED;      // 逐像素透明
        ex |= Win32.WS_EX_TOOLWINDOW;   // 不进任务栏 / Alt-Tab
        ex |= Win32.WS_EX_NOACTIVATE;   // 点击时不激活
        // 默认整窗穿透：WS_EX_TRANSPARENT 让鼠标消息（包括同进程外的窗口）
        // 完全绕开我们——这正是"点目标要送达应用"所必需的，HTTRANSPARENT 做不到。
        // 可点区域由 _hoverTimer 轮询后临时摘掉这一位来"解冻"。
        ex |= Win32.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, new IntPtr(ex));

        _clickThrough = true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            // 第二道防线：可点区域之外返回 HTTRANSPARENT。
            // 它只对同一线程的下层窗口有效——跨进程穿透靠 _hoverTimer 动态摘掉
            // WS_EX_TRANSPARENT 才真正生效，这里只是同一线程内的补充。
            case Win32.WM_NCHITTEST:
            {
                var hit = ClientPoint(m.LParam) is { } point ? HitAt(point) : OverlayHit.None;
                m.Result = new IntPtr(hit == OverlayHit.None
                    ? NativeMethods.HTTRANSPARENT
                    : NativeMethods.HTCLIENT);
                return;
            }

            case NativeMethods.WM_MOUSEMOVE:
                UpdateHover(ClientPoint(m.LParam));
                m.Result = IntPtr.Zero;
                return;

            case NativeMethods.WM_MOUSELEAVE:
                SetHover(OverlayHit.None);
                m.Result = IntPtr.Zero;
                return;

            case NativeMethods.WM_LBUTTONDOWN:
            {
                var hit = ClientPoint(m.LParam) is { } down ? HitAt(down) : OverlayHit.None;
                if (hit == OverlayHit.Close) DismissRequested?.Invoke(StopReason.Closed);
                else if (hit != OverlayHit.None) DismissRequested?.Invoke(StopReason.Clicked);

                // 不交给 base：这里不再是"点击穿透"，点了就算数
                m.Result = IntPtr.Zero;
                return;
            }

            case Win32.WM_MOUSEACTIVATE:
                m.Result = new IntPtr(Win32.MA_NOACTIVATE);
                return;

            case Win32.WM_DPICHANGED:
                base.WndProc(ref m);
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>鼠标消息的 LPARAM 是屏幕坐标，命中判定要的是客户区坐标。</summary>
    private Point? ClientPoint(IntPtr lParam)
    {
        if (_hitRegions is null) return null;

        var raw = lParam.ToInt64();
        var screen = new Point(unchecked((short)(raw & 0xFFFF)), unchecked((short)((raw >> 16) & 0xFFFF)));
        return PointToClient(screen);
    }

    private OverlayHit HitAt(Point client) =>
        OverlayRenderer.HitTest(_hitRegions, new PointF(client.X, client.Y));

    private void UpdateHover(Point? client)
    {
        TrackMouseLeave();
        SetHover(client is { } point ? HitAt(point) : OverlayHit.None);
    }

    private void SetHover(OverlayHit hit)
    {
        if (hit == _hover) return;

        _hover = hit;
        _hoverDim = hit == OverlayHit.None ? 1.0 : HoverDim;
        Render();
    }

    /// <summary>
    /// 轮询指针位置，决定窗口此刻要不要接收点击。
    ///
    /// <para>为什么是轮询而不是 <c>WM_MOUSEMOVE</c>：窗口默认带
    /// <c>WS_EX_TRANSPARENT</c>，那时它<strong>收不到任何鼠标消息</strong>，
    /// "指针进没进牌子"只能主动查询。进入可点区域、消息恢复之后，
    /// <c>WM_MOUSEMOVE</c> 仍然是有用的即时补充。</para>
    /// </summary>
    private void PollHover()
    {
        if (_hitRegions is null || !Visible || !IsHandleCreated) return;

        // 左键按住期间绝不切换穿透：拖动、圈选，或从牌子一路拖到目标的中途切换，
        // 会让这一次点击落到错误的窗口上
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0) return;

        if (!NativeMethods.GetCursorPos(out var screen)) return;

        var client = PointToClient(new Point(screen.X, screen.Y));
        var hit = HitAt(client);

        SetClickThrough(hit == OverlayHit.None);
        SetHover(hit);
    }

    /// <summary>
    /// 设置整窗穿透。<c>WS_EX_TRANSPARENT</c> 的变更必须再走一次
    /// <c>SetWindowPos(SWP_FRAMECHANGED)</c> 才会被系统真正采纳。
    /// </summary>
    private void SetClickThrough(bool clickThrough)
    {
        if (clickThrough == _clickThrough) return;

        _clickThrough = clickThrough;
        ApplyTransparentStyle();

        NativeMethods.SetWindowPos(
            Handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_FRAMECHANGED
            | NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>按当前状态重写扩展样式位（含 <c>WS_EX_TRANSPARENT</c> 的开与关）。</summary>
    private void ApplyTransparentStyle()
    {
        if (!IsHandleCreated) return;

        var ex = NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        ex = _clickThrough
            ? ex | Win32.WS_EX_TRANSPARENT
            : ex & ~(long)Win32.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>收场时停表、复位悬停态并回到默认穿透，免得这次的临时状态带到下一支箭头。</summary>
    private void StopHoverPoll()
    {
        _hoverTimer.Stop();
        _hover = OverlayHit.None;
        _hoverDim = 1.0;

        if (!_clickThrough)
        {
            _clickThrough = true;
            ApplyTransparentStyle();
        }
    }

    private void TrackMouseLeave()
    {
        var track = new NativeMethods.TRACKMOUSEEVENT
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
            dwFlags = NativeMethods.TME_LEAVE,
            hwndTrack = Handle,
            dwHoverTime = 0,
        };

        NativeMethods.TrackMouseEvent(ref track);
    }

    /// <summary>确保窗口句柄已创建。定位由 <see cref="ShowPointer"/> 按内容包围盒决定。</summary>
    public void EnsureHandle()
    {
        _ = Handle;

        // UpdateLayeredWindow 之前窗口必须是不可见且尺寸为 0 的"干净"状态
        NativeMethods.ShowWindow(Handle, Win32.SW_HIDE);
    }

    // ---------------- 显示 / 隐藏 ----------------

    /// <summary>显示箭头。target 与 monitorBounds 都是全局物理像素。</summary>
    public void ShowPointer(
        Point targetGlobal,
        Rectangle monitorBounds,
        uint monitorDpi,
        SignContent sign,
        PointerCommand command)
    {
        _sign = sign;
        _target = targetGlobal;
        _command = command;

        _fadingOut = false;
        _scale = monitorDpi / 96d;
        EnsureHandle();

        // WinForms 在若干路径下会重置扩展样式，UpdateLayeredWindow 之前再钉一遍
        ReapplyStyles();

        // 1) 上游 Placement 算法：8 方向评分选边（--from 只是偏好），牌子永不遮挡目标
        var metrics = ArrowMetrics.For(ArrowMetrics.ParseSize(command.Look.Size));
        // 保留上游尺寸表，Windows 覆盖层采用更紧凑的视觉比例。
        metrics = metrics with
        {
            Stroke = metrics.Stroke * 0.45f,
            HeadLength = metrics.HeadLength * 0.5f,
            HeadWidth = metrics.HeadWidth * 0.5f,
            FontSize = metrics.FontSize * 0.5f,
            Reach = metrics.Reach * 0.65f,
        };
        _metrics = metrics;

        var forced = SafeParseDirection(command.Look.From);
        var (signSize, fontSize) = MeasureSign(metrics, monitorBounds);

        var placement = Placement.Place(
            new SizeF(
                (float)(signSize.Width * _scale),
                (float)(signSize.Height * _scale)),
            new RectangleF(targetGlobal.X, targetGlobal.Y, 1, 1),
            monitorBounds,
            (float)(metrics.Reach * _scale),
            forced);

        _layout = placement;
        _fontSize = fontSize;

        // 2) 窗口只覆盖内容包围盒
        _windowRect = ComputeContentBounds(monitorBounds);
        NativeMethods.SetWindowPos(
            Handle, Win32.HWND_TOPMOST,
            _windowRect.X, _windowRect.Y,
            _windowRect.Width, _windowRect.Height,
            Win32.SWP_NOACTIVATE);

        // 2b) 可点区域（牌子 / 箭杆 / 关闭按钮）。布局定下来就不变了：
        //     动画只改呈现缩放，不改命中判定，每帧重算纯属浪费。
        _hitRegions?.Dispose();
        _hitRegions = OverlayRenderer.ComputeHitRegions(BuildFrame(1.0), (float)_scale);
        _hover = OverlayHit.None;
        _hoverDim = 1.0;

        // 一出来就是全穿透；指针真的落到牌子上之后，轮询才会临时解冻点击
        _clickThrough = true;
        ApplyTransparentStyle();
        _hoverTimer.Start();

        // 3) 画到分层表面并交给 UpdateLayeredWindow；--no-animation 直出终帧
        if (command.Behavior.NoAnimation)
        {
            _alpha = 1;
            _animScale = 1;
            Render();
        }
        else
        {
            _alpha = 0;
            _animScale = 0.75;
            Render();
            EnterPhase(AnimPhase.Intro);
        }
    }

    private static ApproachDirection? SafeParseDirection(string? from)
    {
        try
        {
            return Placement.ParseDirection(from);
        }
        catch (ArgumentException)
        {
            // 解析层已校验过；这里只防御默认值
            return null;
        }
    }

    private static Rectangle ToRectangle(RectangleF r) =>
        new((int)Math.Floor(r.X), (int)Math.Floor(r.Y),
            (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));

    /// <summary>内容包围盒 = 牌子 ∪ 目标禁区，再留出描边、弧线垂度和投影的余量。</summary>
    private Rectangle ComputeContentBounds(Rectangle monitorBounds)
    {
        if (_layout is null) return monitorBounds;

        var margin = (int)Math.Ceiling(PointerStyle.ContentMargin * _scale);
        var union = Rectangle.Union(
            ToRectangle(_layout.KeepOut),
            ToRectangle(_layout.SignRect));

        var bounds = Rectangle.Inflate(union, margin, margin);

        // 夹在显示器内，避免内容跑到别的屏上去（尤其是负坐标显示器）
        var clamped = Rectangle.Intersect(monitorBounds, bounds);
        return clamped.Width > 8 && clamped.Height > 8 ? clamped : bounds;
    }

    /// <summary>量说明卡片尺寸（逻辑像素），包含提示标题和最多三行正文。</summary>
    private (Size Sign, float FontSize) MeasureSign(ArrowMetrics metrics, Rectangle monitorBounds)
    {
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        var dpiFactor = (float)(g.DpiX / 96d);
        var maxWidth = (int)Math.Min(
            monitorBounds.Width * ArrowMetrics.MaximumSignWidthShare / _scale,
            PointerStyle.SignMaxWidth);

        var text = _sign!.DisplayText;
        var preferCjk = OverlayRenderer.HasCjk(text);

        // 长文优先缩小字号，达到下限后按三行省略，避免卡片吞掉屏幕。
        var fontSize = metrics.FontSize;
        var minimumFontSize = Math.Min(PointerStyle.SignFontPx, fontSize);
        Size measured = default;

        while (true)
        {
            using var font = OverlayRenderer.ResolveFont(fontSize / dpiFactor, preferCjk);
            var layoutWidth = (int)((maxWidth - PointerStyle.SignPadX * 2) * dpiFactor);
            var size = g.MeasureString(text, font, layoutWidth);
            var maximumHeight = font.GetHeight(g) / dpiFactor * ArrowMetrics.MaximumLines;

            measured = new Size(
                (int)Math.Ceiling(size.Width / dpiFactor) + (int)Math.Ceiling(PointerStyle.SignPadX * 2),
                (int)Math.Ceiling(Math.Min(size.Height / dpiFactor, maximumHeight))
                + (int)Math.Ceiling(PointerStyle.SignPadY * 2 + PointerStyle.SignHeaderHeight));

            if (size.Height / dpiFactor <= maximumHeight || fontSize <= minimumFontSize)
            {
                break;
            }

            fontSize = MathF.Max(minimumFontSize, fontSize - 2);
        }

        // --close-button 在牌子右端占位：不预留的话按钮会压在文字上
        var closeReserve = _command?.Behavior.CloseButton == true
            ? (int)Math.Ceiling(OverlayRenderer.CloseButtonDiameter + OverlayRenderer.CloseButtonPadding * 2)
            : 0;

        return (new Size(Math.Max(80, measured.Width + closeReserve), Math.Max(40, measured.Height)), fontSize);
    }

    /// <summary>把布局、目标、外观打成一帧。<paramref name="animScale"/> 只影响呈现，不影响命中。</summary>
    private RenderFrame BuildFrame(double animScale) => new(
        SignRect: new RectangleF(
            _layout!.SignRect.X - _windowRect.X,
            _layout.SignRect.Y - _windowRect.Y,
            _layout.SignRect.Width,
            _layout.SignRect.Height),
        SignCenter: new PointF(
            _layout.SignRect.X + _layout.SignRect.Width / 2 - _windowRect.X,
            _layout.SignRect.Y + _layout.SignRect.Height / 2 - _windowRect.Y),
        Target: new PointF(
            _target.X - _windowRect.X,
            _target.Y - _windowRect.Y),
        Color: _sign!.Color,
        SignFontSize: _fontSize,
        Text: _sign.DisplayText,
        Look: _command?.Look ?? new LookSpec(),
        Metrics: _metrics,
        DpiScale: _scale,
        AnimScale: animScale,
        CloseButton: _command?.Behavior.CloseButton == true);

    /// <summary>把内容画进 32bpp PArgb 位图，再作为分层窗口交给系统。位图跨帧复用。</summary>
    private void Render()
    {
        if (_layout is null || _sign is null) return;

        var w = Math.Max(1, _windowRect.Width);
        var h = Math.Max(1, _windowRect.Height);

        if (_frameBuffer is null || _frameBuffer.Width != w || _frameBuffer.Height != h)
        {
            _frameBuffer?.Dispose();
            _frameBuffer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        using (var g = Graphics.FromImage(_frameBuffer))
        {
            OverlayRenderer.DrawFrame(g, BuildFrame(_animScale));
        }

        PresentLayered(_frameBuffer);
    }

    private void PresentLayered(Bitmap bitmap)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;

        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            // SelectObject 返回的是"原来的"对象（DIB 句柄或 stock 对象），不是 GDI 绘制对象，
            // 千万别 DeleteObject 它——那会崩。真正需要释放的是下面那个 memDc 里选的 hBitmap。
            var oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
            try
            {
                var dst = new NativeMethods.POINTL { x = _windowRect.X, y = _windowRect.Y };
                var size = new NativeMethods.SIZE { cx = _windowRect.Width, cy = _windowRect.Height };
                var src = new NativeMethods.POINTL { x = 0, y = 0 };
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    // 悬停在牌子 / 箭杆上时整体压暗一点，示意"点这里可以收掉"
                    SourceConstantAlpha = (byte)Math.Clamp(Math.Round(_alpha * _hoverDim * 255), 0, 255),
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                };

                if (!NativeMethods.UpdateLayeredWindow(
                    Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA))
                {
                    throw new System.ComponentModel.Win32Exception();
                }

                NativeMethods.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------- 动画 ----------------

    private void EnterPhase(AnimPhase phase)
    {
        _phase = phase;
        _phaseStart = Environment.TickCount64;

        switch (phase)
        {
            case AnimPhase.Idle:
                _animTimer.Stop();
                break;

            // 入场 60fps：200ms 的弹出要跟手
            case AnimPhase.Intro:
                _animTimer.Interval = Math.Max(1, 1000 / PointerStyle.AnimFpsFast);
                _animTimer.Start();
                break;

            // 常驻呼吸 30fps：位图足够小，长驻也不该烧 CPU
            case AnimPhase.Pulse:
                _animScale = 1.0;
                _alpha = 1.0;
                _animTimer.Interval = Math.Max(1, 1000 / PointerStyle.AnimFpsCalm);
                _animTimer.Start();
                break;

            case AnimPhase.FadeOut:
                _animTimer.Interval = Math.Max(1, 1000 / PointerStyle.AnimFpsFast);
                if (!_animTimer.Enabled) _animTimer.Start();
                break;
        }
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _phaseStart;

        switch (_phase)
        {
            case AnimPhase.Intro:
            {
                var t = Math.Min(1.0, (double)elapsed / PointerStyle.IntroMs);
                _animScale = 0.75 + 0.25 * PointerStyle.EaseOutBack(t);
                _alpha = Math.Clamp(PointerStyle.EaseOutCubic(t), 0d, 1d);

                if (t >= 1.0)
                {
                    EnterPhase(AnimPhase.Pulse);
                }

                Render();
                break;
            }

            case AnimPhase.Pulse:
            {
                var cycles = 2d * Math.PI * elapsed / PointerStyle.PulsePeriodMs;
                _animScale = 1.0 + PointerStyle.PulseAmplitude * Math.Sin(cycles);
                Render();
                break;
            }

            case AnimPhase.FadeOut:
            {
                var t = Math.Min(1.0, (double)elapsed / _fadeDurationMs);

                if (t >= 1.0)
                {
                    // 停表 + 复位缺一不可：不停表会常驻空转烧 CPU，
                    // 不复位 IsFading 调用方的等待循环就永不结束
                    _animTimer.Stop();
                    _phase = AnimPhase.Idle;
                    _fadingOut = false;
                    _alpha = 0;
                    Render();
                    StopHoverPoll();
                    NativeMethods.ShowWindow(Handle, Win32.SW_HIDE);
                    return;
                }

                _alpha = _fadeFrom * PointerStyle.EaseInFade(t);
                _animScale = 1.0 - 0.015 * t;
                Render();
                break;
            }
        }
    }

    /// <summary>开始淡出。淡出完成后定时器必须停，否则会常驻空转烧 CPU。</summary>
    public void BeginFadeOut(int durationMs = PointerStyle.FadeOutMs)
    {
        if (_fadingOut) return;

        _fadingOut = true;
        _fadeFrom = Math.Max(0.01, _alpha);
        _fadeDurationMs = Math.Max(16, durationMs);

        EnterPhase(AnimPhase.FadeOut);
    }

    public void HideNow()
    {
        StopHoverPoll();
        EnterPhase(AnimPhase.Idle);
        _fadingOut = false;
        _alpha = 1.0;
        _animScale = 1.0;
        _layout = null;
        _sign = null;
        NativeMethods.ShowWindow(Handle, Win32.SW_HIDE);
    }

    public bool IsFading => _fadingOut;

    public bool IsEntering => _phase == AnimPhase.Intro;

    /// <summary>
    /// 强制结束淡出（调用方超时兜底时用）。停表、清状态、隐藏窗口，保证不会留下可见残留。
    /// </summary>
    public void ForceFinishFade()
    {
        if (!_fadingOut) return;

        StopHoverPoll();
        EnterPhase(AnimPhase.Idle);
        _fadingOut = false;
        _alpha = 1.0;
        NativeMethods.ShowWindow(Handle, Win32.SW_HIDE);
    }

    // ---------------- 自检 ----------------

    /// <summary>
    /// 把真实的 WM_NCHITTEST 发给窗口自己，拿回系统实际看到的返回值。
    /// 返回 -1（HTTRANSPARENT）仅证明消息处理器的返回值，不能证明跨进程点击已经送达。
    /// </summary>
    public int ProbeHitTest(Point screenPoint)
    {
        var lParam = NativeMethods.PackPoint(screenPoint.X, screenPoint.Y);
        return (int)NativeMethods.SendMessage(
            Handle, Win32.WM_NCHITTEST, IntPtr.Zero, lParam).ToInt64();
    }

    /// <summary>读取当前窗口的实际扩展样式位，供 doctor 自检。</summary>
    public OverlayStyleBits StyleBits()
    {
        var ex = NativeMethods.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        return new OverlayStyleBits(
            Raw: ex,
            Layered: (ex & Win32.WS_EX_LAYERED) != 0,
            ToolWindow: (ex & Win32.WS_EX_TOOLWINDOW) != 0,
            NoActivate: (ex & Win32.WS_EX_NOACTIVATE) != 0,
            Transparent: (ex & Win32.WS_EX_TRANSPARENT) != 0);
    }

    /// <summary>强制重新写一遍扩展样式。WinForms 在某些路径下会重置它们。</summary>
    public void ReapplyStyles() => ApplyTransparentStyle();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Stop();
            _animTimer.Dispose();
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            _frameBuffer?.Dispose();
            _frameBuffer = null;
            _hitRegions?.Dispose();
            _hitRegions = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>覆盖层无法创建（锁屏 / UAC 安全桌面 / 权限不足）。映射为退出码 4。</summary>
public sealed class OverlayUnavailableException : Exception
{
    public OverlayUnavailableException(string message) : base(message) { }
}

/// <summary>覆盖层窗口的实际扩展样式位，供 doctor 自检。</summary>
public sealed record OverlayStyleBits(
    long Raw,
    bool Layered,
    bool ToolWindow,
    bool NoActivate,
    bool Transparent);
