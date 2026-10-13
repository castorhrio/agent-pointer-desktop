namespace AgentPointer.Core.Rendering;

/// <summary>
/// 集中设计 token——整个产品视觉的单一来源。
/// 箭头覆盖层（OverlayRenderer）与托盘预览共用这一组数值；
/// 改字号、圆角、描边或动画节奏只需要改这里，不会出现两处规格漂移。
///
/// <para>所有尺寸均为<strong>逻辑像素</strong>（96dpi 基准），渲染时乘显示器 DPI。
/// 数值经过多轮截图验证：目标是上游 bigarrow 的观感——
/// 饱满的弧形大箭头、白描边 + 柔和投影、克制的圆角与留白。</para>
/// </summary>
public static class PointerStyle
{
    // ---------------- 牌子排版 ----------------

    /// <summary>牌子正文字号。</summary>
    public const float SignFontPx = 16f;

    /// <summary>牌子水平内边距。</summary>
    public const float SignPadX = 16f;

    /// <summary>牌子垂直内边距。</summary>
    public const float SignPadY = 12f;

    public const float SignHeaderHeight = 20f;

    /// <summary>牌子最大宽度，超过则折行。</summary>
    public const int SignMaxWidth = 420;

    /// <summary>牌子圆角半径。</summary>
    public const float SignRadius = 12f;

    /// <summary>牌子内容与窗口包围盒之间的余量（容纳投影、描边、弧线垂度）。</summary>
    public const int ContentMargin = 80;

    /// <summary>牌子宽度的绝对上限（上游另按 60% 屏宽取小）。</summary>
    public const int SignAbsoluteMaxWidth = 900;

    // ---------------- 箭头几何（M 档基准，随 DPI 缩放） ----------------

    /// <summary>箭杆线宽。</summary>
    public const float ShaftWidth = 12f;

    /// <summary>箭头头部（沿指向方向的长度）。</summary>
    public const float HeadLength = 34f;

    /// <summary>箭头头部底边宽。</summary>
    public const float HeadWidth = 30f;

    /// <summary>弧形杆垂度 = min(杆长 × SagRatio, SagMax)。</summary>
    public const float SagRatio = 0.08f;
    public const float SagMax = 16f;

    // ---------------- 描边与投影 ----------------

    /// <summary>白描边宽度（默认 border=shadow 档）。</summary>
    public const float OutlineWidth = 1.5f;

    /// <summary>white-black 档的最外层细黑边宽度。</summary>
    public const float OutlineThin = 1.25f;

    /// <summary>投影垂直偏移与不透明度。</summary>
    public const float ShadowOffsetY = 3f;
    public const int ShadowAlpha = 64;

    // ---------------- 动画节奏 ----------------

    /// <summary>入场时长：从目标点弹出（轻微过冲）。</summary>
    public const int IntroMs = 200;

    /// <summary>常驻呼吸周期与幅度。幅度刻意很小：是"活着"，不是"闪"。</summary>
    public const int PulsePeriodMs = 1800;
    public const float PulseAmplitude = 0.012f;

    /// <summary>淡出时长（ease-in）。</summary>
    public const int FadeOutMs = 320;

    /// <summary>入场用 60fps；常驻呼吸 20fps——实测占大头的成本是每帧 GDI+ 场景重绘，
    /// 1.8% 幅度的正弦在 20fps 下依旧平滑，CPU 按比例下降约三分之一。</summary>
    public const int AnimFpsFast = 60;
    public const int AnimFpsCalm = 20;

    // ---------------- 缓动函数 ----------------

    /// <summary>标准 ease-out cubic。</summary>
    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>轻微过冲的 ease-out back（c1 压到 0.7 倍，过冲约 3%）。</summary>
    public static double EaseOutBack(double t)
    {
        const double c1 = 1.70158 * 0.7;
        const double c3 = c1 + 1;
        var u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>淡出的 ease-in：比线性稍慢起步、更快收尾。</summary>
    public static double EaseInFade(double t) => Math.Pow(1 - t, 1.6);
}
