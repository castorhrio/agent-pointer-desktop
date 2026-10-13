using System.Drawing;
using System.Windows.Forms;

namespace AgentPointer.Cli.Overlay;

/// <summary>
/// <c>--notify</c>：额外弹一条系统通知。
///
/// <para>用 <see cref="NotifyIcon.ShowBalloonTip"/>；Windows 10/11 把它显示成标准 toast。
/// 图标取当前可执行文件里带的图标，取不到就退回系统信息图标。</para>
///
/// <para>点击通知不做任何特殊处理：给它绑一个"点了就等于批准"的回调，和
/// "箭头只负责指路、决定权在人"的定位直接冲突。覆盖层收场时整个
/// <see cref="NotifyIcon"/> 一起释放。</para>
/// </summary>
internal sealed class ToastNotifier : IDisposable
{
    /// <summary>请求的停留时间（毫秒）；实际时长由系统决定。</summary>
    private const int BalloonMs = 8000;

    private readonly NotifyIcon _icon;
    private readonly Icon? _ownedIcon;

    private ToastNotifier(NotifyIcon icon, Icon? ownedIcon)
    {
        _icon = icon;
        _ownedIcon = ownedIcon;
    }

    /// <summary>弹一条通知。文本为空或通知不可用时返回 null。</summary>
    public static ToastNotifier? Show(string? text, bool json)
    {
        var body = text?.Trim();
        if (string.IsNullOrEmpty(body)) return null;

        var owned = TryLoadIcon();

        try
        {
            var icon = new NotifyIcon
            {
                Icon = owned ?? SystemIcons.Information,
                Text = "AgentPointer",
                Visible = true,
            };

            var notifier = new ToastNotifier(icon, owned);
            icon.ShowBalloonTip(BalloonMs, "AgentPointer", body, ToolTipIcon.Info);
            return notifier;
        }
        catch (Exception ex)
        {
            owned?.Dispose();
            Warn(json, $"通知未能显示：{ex.Message}");
            return null;
        }
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Warn(bool json, string message)
    {
        if (json) return;

        try
        {
            Console.Error.WriteLine($"agentpointer: {message}");
            Console.Error.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch (Exception)
        {
            // 释放阶段的异常没有有意义的处理方式
        }

        _ownedIcon?.Dispose();
    }
}
