using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AgentPointer.Cli.Interop;

namespace AgentPointer.Cli.Targeting;

/// <summary>一个匹配上的窗口。</summary>
public sealed record WindowMatch(IntPtr Handle, string? ProcessName, string Title, Rectangle Bounds);

/// <summary>
/// <c>--window</c> / <c>--app</c> 的解析。
///
/// <para>Windows 上的 <c>App</c> 是<strong>进程名</strong>（<c>WindowsTerminal</c>、<c>Code</c>），
/// 不是 macOS 的 App bundle 名。支持 <c>App:标题</c> 语法来进一步缩小范围。</para>
/// </summary>
public static class WindowFinder
{
    /// <summary>列出所有可见且有标题的顶层窗口。</summary>
    public static IReadOnlyList<WindowMatch> Enumerate()
    {
        var results = new List<WindowMatch>();

        bool Callback(IntPtr hWnd, IntPtr lParam)
        {
            // 回调里禁止抛异常——异常穿过原生边界会直接终止进程
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                if (GetWindowTitle(hWnd) is not { Length: > 0 } title) return true;
                if (NativeMethods.GetWindowRect(hWnd, out var rect) is false) return true;
                if (rect.Width <= 0 || rect.Height <= 0) return true;

                var processName = GetProcessName(hWnd);

                results.Add(new WindowMatch(
                    hWnd, processName, title,
                    new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height)));
            }
            catch
            {
                // 单个窗口查不出来不影响其它窗口
            }

            return true;
        }

        NativeMethods.EnumWindows(Callback, IntPtr.Zero);
        return results;
    }

    /// <summary>
    /// 按 <c>App</c> 或 <c>App:title</c> 找窗口。找不到返回 null。
    /// 多个匹配时优先取标题最接近的，其次取面积最大的。
    /// </summary>
    public static WindowMatch? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var (appPart, titlePart) = SplitQuery(query);
        var windows = Enumerate();

        var candidates = windows
            .Where(w => ProcessMatches(w.ProcessName, appPart))
            .Where(w => titlePart is null || w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            // 标题匹配不上时，退一步只按进程名匹配，别直接放弃
            if (titlePart is not null)
            {
                candidates = windows
                    .Where(w => ProcessMatches(w.ProcessName, appPart))
                    .ToList();
            }
        }

        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(w => titlePart is not null &&
                                    w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .First();
    }

    /// <summary>把 <c>App:标题</c> 拆开。标题里可以含冒号，所以只拆第一个。</summary>
    public static (string App, string? Title) SplitQuery(string query)
    {
        var trimmed = query.Trim();
        var idx = trimmed.IndexOf(':');

        return idx < 0
            ? (trimmed, null)
            : (trimmed[..idx].Trim(), trimmed[(idx + 1)..].Trim() is { Length: > 0 } t ? t : null);
    }

    /// <summary>进程名匹配：忽略大小写、忽略 .exe 后缀。</summary>
    public static bool ProcessMatches(string? processName, string query)
    {
        if (string.IsNullOrEmpty(processName)) return false;

        var normalize = (string s) =>
            s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

        return string.Equals(normalize(processName), normalize(query), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把目标窗口置前。后台进程直接调 SetForegroundWindow 经常失败（Windows 的前台锁），
    /// 所以要 AllowSetForegroundWindow + AttachThreadInput 组合拳。
    /// </summary>
    public static bool TryRaise(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var targetThread = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
            var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);

            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, Win32.SW_RESTORE);
            }

            NativeMethods.AllowSetForegroundWindow(-1);
            NativeMethods.BringWindowToTop(hWnd);

            var attached = false;
            if (foregroundThread != 0 && foregroundThread != targetThread)
            {
                attached = NativeMethods.AttachThreadInput(
                    NativeMethods.GetCurrentThreadId(), foregroundThread, true);
            }

            try
            {
                var ok = NativeMethods.SetForegroundWindow(hWnd);
                return ok;
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(
                        NativeMethods.GetCurrentThreadId(), foregroundThread, false);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? GetWindowTitle(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0) return null;

        var sb = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    private static string? GetProcessName(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0) return null;

        var handle = NativeMethods.OpenProcess(
            Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);

        if (handle == IntPtr.Zero) return null;

        try
        {
            var size = (uint)1024;
            var sb = new StringBuilder((int)size);
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size)) return null;

            var full = sb.ToString();
            var slash = full.LastIndexOf('\\');
            return slash >= 0 ? full[(slash + 1)..] : full;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
