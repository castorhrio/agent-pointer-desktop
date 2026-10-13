// Adapted from bigarrow by Franz Enzenhofer.
// Copyright (c) 2026 Franz Enzenhofer. MIT License; see THIRD_PARTY_NOTICES.md.
namespace AgentPointer.Core.Rendering;

/// <summary>箭头尺寸档位。移植上游 ArrowAppearance.swift 的 ArrowMetrics——数值不改。</summary>
public enum ArrowSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// 一档尺寸的全部度量（逻辑 pt，96dpi 基准，渲染时乘 DPI）。
/// 与上游 S/M/L presets 逐项一致：所有随尺寸变化的量都住在这里，别处不许私自缩放。
/// </summary>
public sealed record ArrowMetrics(
    float Stroke,
    float HeadLength,
    float HeadWidth,
    float FontSize,
    /// <summary>目标点与牌子近缘之间的期望距离（reach）。</summary>
    float Reach)
{
    /// <summary>牌子文字在放弃行数限制前允许缩到的最小字号。</summary>
    public const float MinimumFontSize = 24f;

    /// <summary>牌子最多折的行数。</summary>
    public const int MaximumLines = 3;

    /// <summary>牌子允许占用的屏宽比例。</summary>
    public const float MaximumSignWidthShare = 0.6f;

    public static ArrowMetrics For(ArrowSize size) => size switch
    {
        ArrowSize.Small => new ArrowMetrics(Stroke: 10, HeadLength: 46, HeadWidth: 48, FontSize: 30, Reach: 130),
        ArrowSize.Large => new ArrowMetrics(Stroke: 21, HeadLength: 84, HeadWidth: 88, FontSize: 60, Reach: 220),
        _ => new ArrowMetrics(Stroke: 15, HeadLength: 62, HeadWidth: 64, FontSize: 44, Reach: 170),
    };

    /// <summary>上游 S/M/L 字符串解析：大小写不敏感，非法值抛 <see cref="ArgumentException"/>。</summary>
    public static ArrowSize ParseSize(string? raw)
    {
        if (raw is null) return ArrowSize.Medium;
        return raw.ToUpperInvariant() switch
        {
            "S" => ArrowSize.Small,
            "M" => ArrowSize.Medium,
            "L" => ArrowSize.Large,
            _ => throw new ArgumentException($"size '{raw}' is not one of S, M, L"),
        };
    }
}
