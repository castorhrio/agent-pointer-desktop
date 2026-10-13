using System.Windows.Automation;
using AgentPointer.Core.Cli;
using AgentPointer.Core.Targeting;

namespace AgentPointer.Cli.Targeting;

/// <summary>
/// 一次元素收集的结果：元素列表，以及是否因为超时或数量上限被截断
/// （截断时列表仍是一份可用的部分结果）。
/// </summary>
internal readonly record struct ElementScan(IReadOnlyList<ElementInfo> Elements, bool Truncated);

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

    /// <summary><c>elements</c> 要的是整份清单，多给两秒比只看半张强。</summary>
    internal static readonly TimeSpan ElementsTimeout = TimeSpan.FromSeconds(5);

    /// <summary><see cref="CollectDetailed"/> 的简写：只要列表，丢掉截断标记。</summary>
    public static IReadOnlyList<ElementInfo> Collect(
        IntPtr hwnd,
        int maxDepth = DefaultMaxDepth,
        int maxCount = DefaultMaxCount,
        TimeSpan? timeout = null) =>
        CollectDetailed(hwnd, maxDepth, maxCount, timeout).Elements;

    /// <summary>
    /// 深度优先收集 <paramref name="hwnd"/> 下的元素。
    ///
    /// <para><b>为什么另开线程</b>：UI Automation 的每一次属性读取都是同步的跨进程 COM 调用，
    /// Chrome、Electron 这类应用能让一次 <c>FromHandle</c> / <c>GetFirstChild</c> 卡住好几秒。
    /// 在节点之间检查截止时间是拦不住这种卡顿的，所以整条遍历跑在一条独立的 STA 后台线程上，
    /// 调用方只等 <paramref name="timeout"/>：等到了就是完整结果，等不到就拿走已经收集到的部分，
    /// 把线程原地丢掉（<c>IsBackground</c>，不会拖住进程退出）。</para>
    ///
    /// <para>单个元素取不到属性就跳过，绝不让一个坏节点毁掉整棵树。</para>
    /// </summary>
    public static ElementScan CollectDetailed(
        IntPtr hwnd,
        int maxDepth = DefaultMaxDepth,
        int maxCount = DefaultMaxCount,
        TimeSpan? timeout = null)
    {
        var gate = new object();
        var results = new List<ElementInfo>();
        if (hwnd == IntPtr.Zero) return new ElementScan(results, false);

        var budget = timeout ?? DefaultTimeout;
        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var done = new ManualResetEventSlim(false);
        var completed = false;

        var worker = new Thread(() =>
        {
            try
            {
                completed = Walk(hwnd, results, gate, maxDepth, maxCount, deadline);
            }
            catch (Exception)
            {
                // 结果不完整而已，绝不能让这条线程把进程带崩
            }
            finally
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                    // 调用方已经放弃这条线程并放掉了事件
                }
            }
        })
        {
            IsBackground = true,
            Name = "agentpointer-uia",
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        var finishedInTime = done.Wait(budget);
        if (finishedInTime) done.Dispose();

        List<ElementInfo> snapshot;
        lock (gate)
        {
            snapshot = new List<ElementInfo>(results);
        }

        return new ElementScan(snapshot, !finishedInTime || !completed);
    }

    /// <returns>true 表示整棵树都走完了；false 表示撞上了 <paramref name="maxCount"/> 或超时。</returns>
    private static bool Walk(
        IntPtr hwnd, List<ElementInfo> results, object gate, int maxDepth, int maxCount, long deadline)
    {
        // CacheRequest：节点和它要用的属性在跨进程抓取时一起取回，每个节点从十几次往返
        // 降到一次。GetFirstChild / GetNextSibling 的带缓存重载必须显式拿到这个 request。
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);

        using (cache.Activate())
        {
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
                return true;
            }

            if (root is null) return true;

            var walker = TreeWalker.ControlViewWalker;
            var stack = new Stack<(AutomationElement Element, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                if (Environment.TickCount64 > deadline) return false;
                lock (gate)
                {
                    if (results.Count >= maxCount) return false;
                }

                var (element, depth) = stack.Pop();

                try
                {
                    if (TryDescribe(element) is { } info)
                    {
                        lock (gate)
                        {
                            results.Add(info);
                        }
                    }

                    if (depth >= maxDepth) continue;

                    // 先收齐兄弟再逆序压栈，这样输出仍是文档顺序（深度优先）
                    var children = new List<AutomationElement>();
                    var child = walker.GetFirstChild(element, cache);
                    while (child is not null)
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child, cache);
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
        }

        return true;
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
        var cached = element.Cached;

        // CacheRequest 生效时属性已经在手里，一次跨进程调用都不用发。万一没生效
        // （例如 FromHandle 拿到的窗口根节点），只为这一个节点退回 Current。
        if (cached.ControlType is null && cached.BoundingRectangle.IsEmpty && string.IsNullOrEmpty(cached.Name))
        {
            var current = element.Current;

            return Describe(
                (current.Name ?? string.Empty).Trim(),
                current.ControlType,
                current.AutomationId,
                current.BoundingRectangle,
                current.IsEnabled,
                current.IsOffscreen);
        }

        return Describe(
            (cached.Name ?? string.Empty).Trim(),
            cached.ControlType,
            cached.AutomationId,
            cached.BoundingRectangle,
            cached.IsEnabled,
            cached.IsOffscreen);
    }

    private static ElementInfo? Describe(
        string name,
        ControlType? controlType,
        string? automationId,
        System.Windows.Rect bounds,
        bool enabled,
        bool offscreen)
    {
        var id = string.IsNullOrWhiteSpace(automationId) ? null : automationId.Trim();

        // 没名字又没 AutomationId 的元素对 --element 毫无意义，只会污染候选列表
        if (name.Length == 0 && id is null) return null;

        if (bounds.IsEmpty || !IsUsable(bounds)) return null;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new ElementInfo(
            name,
            ElementMatcher.NormalizeRole(controlType?.ProgrammaticName),
            id,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            enabled,
            offscreen);
    }

    /// <summary>NaN / Infinity 的矩形算不出来也画不出来，直接丢掉。</summary>
    private static bool IsUsable(System.Windows.Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height);
}
