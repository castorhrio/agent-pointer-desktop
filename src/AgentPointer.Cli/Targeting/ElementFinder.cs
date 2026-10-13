using System.Windows.Automation;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Targeting;

namespace AgentPointer.Cli.Targeting;

/// <summary>
/// 用 UI Automation 收集窗口里的可访问性元素，并交给平台无关的
/// <see cref="ElementMatcher"/> 排序。
///
/// <para>只用 <c>System.Windows.Automation</c>，不引入 <c>System.Windows</c>：
/// WPF 的 Rect/Point 会和 WinForms、System.Drawing 的同名类型打架。</para>
/// </summary>
internal static class ElementFinder
{
    internal const int DefaultMaxDepth = 25;
    internal const int DefaultMaxCount = 3000;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 深度优先收集 <paramref name="hwnd"/> 下的元素。
    ///
    /// <para>单个元素取不到属性就跳过，绝不让一个坏节点毁掉整棵树；
    /// 超过 <paramref name="maxDepth"/> / <paramref name="maxCount"/> / 超时都返回
    /// 已经收集到的部分——agent 宁可拿到残缺列表，也不要一个挂住的进程。</para>
    /// </summary>
    public static IReadOnlyList<ElementInfo> Collect(
        IntPtr hwnd,
        int maxDepth = DefaultMaxDepth,
        int maxCount = DefaultMaxCount,
        TimeSpan? timeout = null)
    {
        var results = new List<ElementInfo>();
        if (hwnd == IntPtr.Zero) return results;

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException
                                       or ArgumentException
                                       or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return results;
        }

        if (root is null) return results;

        var deadline = Environment.TickCount64 + (long)(timeout ?? DefaultTimeout).TotalMilliseconds;
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0 && results.Count < maxCount)
        {
            if (Environment.TickCount64 > deadline) break;

            var (element, depth) = stack.Pop();

            try
            {
                if (TryDescribe(element) is { } info)
                {
                    results.Add(info);
                }

                if (depth >= maxDepth) continue;

                // 先收齐兄弟再逆序压栈，这样输出仍是文档顺序（深度优先）
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(element);
                while (child is not null)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push((children[i], depth + 1));
                }
            }
            catch (ElementNotAvailableException)
            {
                // 树在遍历途中变化（窗口关闭、列表重建）——跳过这个节点
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }

        return results;
    }

    /// <summary>
    /// 按 label（可选的 role 限定）找元素，找不到就抛 <see cref="TargetNotFoundException"/>，
    /// 消息里带上最相近的候选名，让 agent 自己改查询串。
    /// </summary>
    public static ElementInfo Find(IntPtr hwnd, string label, string? role, string? windowTitle = null)
    {
        var elements = Collect(hwnd);
        var ranked = ElementMatcher.Rank(elements, label, role);
        if (ranked.Count > 0)
        {
            return ranked[0];
        }

        var suggestions = ElementMatcher.Suggestions(elements, label);
        var hint = suggestions.Count > 0
            ? Lang.T(
                $"相近的元素：{string.Join("、", suggestions)}。 ",
                $"Close matches: {string.Join(", ", suggestions)}. ")
            : Lang.T(
                "该窗口没有暴露可用于匹配的元素。 ",
                "The window exposes no element to match. ");

        var window = string.IsNullOrWhiteSpace(windowTitle)
            ? Lang.T("前台窗口", "the foreground window")
            : windowTitle;

        throw new TargetNotFoundException(Lang.T(
            $"未找到元素 \"{label}\"（窗口 \"{window}\"）。{hint}用 agentpointer elements --app ... 查看全部。",
            $"No element \"{label}\" in window \"{window}\". {hint}"
            + "Run agentpointer elements --app ... to list them all."));
    }

    private static ElementInfo? TryDescribe(AutomationElement element)
    {
        var current = element.Current;

        var name = (current.Name ?? string.Empty).Trim();
        var automationId = string.IsNullOrWhiteSpace(current.AutomationId) ? null : current.AutomationId.Trim();

        // 没名字又没 AutomationId 的元素对 --element 毫无意义，只会污染候选列表
        if (name.Length == 0 && automationId is null) return null;

        var bounds = current.BoundingRectangle;
        if (bounds.IsEmpty || !IsUsable(bounds)) return null;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new ElementInfo(
            name,
            ElementMatcher.NormalizeRole(current.ControlType?.ProgrammaticName),
            automationId,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            current.IsEnabled,
            current.IsOffscreen);
    }

    /// <summary>NaN / Infinity 的矩形算不出来也画不出来，直接丢掉。</summary>
    private static bool IsUsable(System.Windows.Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height);
}
