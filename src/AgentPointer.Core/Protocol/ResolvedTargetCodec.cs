using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AgentPointer.Core.Targeting;

namespace AgentPointer.Core.Protocol;

/// <summary>
/// <c>start --element</c> 的父进程解析好的元素目标，经环境变量交给 <c>--keep-alive</c> 子进程。
/// 全局物理像素矩形 + 目标顶层窗口句柄 + 命中的元素（仅用于 --json 回显）。
/// </summary>
public sealed record ResolvedTarget(
    int X,
    int Y,
    int Width,
    int Height,
    IntPtr Window,
    ElementInfo? Element,
    bool? Raised,
    bool Truncated);

/// <summary>
/// 已解析目标的环境变量编解码。父进程写、子进程读，两端共用这一份实现，
/// 免得两边的变量名和格式各自漂移（曾经就是这样丢掉 HWND，让 <c>--flash</c> 静默失效）。
/// </summary>
public static class ResolvedTargetCodec
{
    public const string RectVariable = "AGENTPOINTER_RESOLVED_RECT";
    public const string WindowVariable = "AGENTPOINTER_RESOLVED_HWND";
    public const string ElementVariable = "AGENTPOINTER_RESOLVED_ELEMENT";
    public const string RaisedVariable = "AGENTPOINTER_RESOLVED_RAISED";
    public const string TruncatedVariable = "AGENTPOINTER_RESOLVED_TRUNCATED";

    private static readonly JsonSerializerOptions ElementJson = new() { WriteIndented = false };

    /// <summary>编码成"环境变量名 → 值"的字典。元素缺失时不下发元素变量。</summary>
    public static Dictionary<string, string> Encode(ResolvedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RectVariable] = string.Join(",", new[] { target.X, target.Y, target.Width, target.Height }
                .Select(v => v.ToString(CultureInfo.InvariantCulture))),
            [WindowVariable] = target.Window.ToInt64().ToString(CultureInfo.InvariantCulture),
            [RaisedVariable] = target.Raised is { } raised ? (raised ? "true" : "false") : string.Empty,
            [TruncatedVariable] = target.Truncated ? "true" : "false",
        };

        if (target.Element is not null)
        {
            environment[ElementVariable] = JsonSerializer.Serialize(target.Element, ElementJson);
        }

        return environment;
    }

    /// <summary>
    /// 解码。矩形缺失或格式不对就返回 false（子进程自己去做 UI Automation）；
    /// 句柄、元素、截断标记坏掉只影响那一个字段，目标本身照旧可用。
    /// </summary>
    public static bool TryDecode(Func<string, string?> read, out ResolvedTarget target)
    {
        ArgumentNullException.ThrowIfNull(read);
        target = null!;

        var raw = read(RectVariable);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split(',');
        if (parts.Length != 4 ||
            !TryInt(parts[0], out var x) || !TryInt(parts[1], out var y) ||
            !TryInt(parts[2], out var width) || !TryInt(parts[3], out var height))
        {
            return false;
        }

        var window = IntPtr.Zero;
        if (long.TryParse(read(WindowVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle))
        {
            window = new IntPtr(handle);
        }

        ElementInfo? element = null;
        var elementJson = read(ElementVariable);
        if (!string.IsNullOrWhiteSpace(elementJson))
        {
            try
            {
                element = JsonSerializer.Deserialize<ElementInfo>(elementJson, ElementJson);
            }
            catch (JsonException)
            {
                // 解析不出来只影响 --json 里回显的元素，目标本身照旧
            }
        }

        target = new ResolvedTarget(
            x, y, width, height,
            window,
            element,
            bool.TryParse(read(RaisedVariable), out var raised) ? raised : null,
            bool.TryParse(read(TruncatedVariable), out var truncated) && truncated);

        return true;
    }

    private static bool TryInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
