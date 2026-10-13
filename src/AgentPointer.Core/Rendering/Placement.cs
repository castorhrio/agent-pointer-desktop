// Adapted from bigarrow by Franz Enzenhofer.
// Copyright (c) 2026 Franz Enzenhofer. MIT License; see THIRD_PARTY_NOTICES.md.
using System.Drawing;

namespace AgentPointer.Core.Rendering;

/// <summary>
/// 箭头从哪一侧接近目标。移植上游 Placement.swift 的 ApproachDirection——8 方向 + auto。
/// </summary>
public enum ApproachDirection
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// 牌子摆放算法。逐行移植上游 Placement.swift：选"剩余空间最大"的一侧，
/// 永远不遮挡目标（keep-out 禁区），放不下时按 reach 缩放表逐档收缩，最后兜底钳制。
/// 纯函数、确定性——与上游相同的输入给相同的输出。
/// </summary>
public static class Placement
{
    /// <summary>目标点周围牌子永不进入的方形禁区边长。</summary>
    public const float KeepOutSide = 120;

    /// <summary>牌子与可用区边缘的间隙——给投影（半径 10、下移 3、约 23pt 模糊）留足衰减空间。</summary>
    public const float Margin = 24;

    /// <summary>reach 的收缩/放大尝试表：偏好长度放不下先缩，再放长换空间。</summary>
    public static readonly float[] ReachScales = { 1.0f, 0.75f, 1.4f, 0.55f, 1.9f, 2.5f };

    /// <summary>reach 每偏离 1 一分的代价：挤或过长只有在偏好长度放不下时才会赢。</summary>
    public const float ShortcutPenalty = 400;

    /// <summary>本箭头牌子与其他活跃箭头牌子之间的间隙。</summary>
    public const float OtherGap = 12;

    /// <summary>与其他牌子重叠时的每平方分代价。</summary>
    public const float OverlapPenalty = 0.05f;

    public sealed record Result(ApproachDirection Direction, RectangleF SignRect, RectangleF KeepOut);

    /// <summary>
    /// 目标点朝牌子方向的单位向量（y 向下），对角方向已归一化。
    /// </summary>
    public static (float Dx, float Dy) Unit(ApproachDirection direction) => direction switch
    {
        ApproachDirection.TopLeft => Normalize(-1, -1),
        ApproachDirection.TopRight => Normalize(1, -1),
        ApproachDirection.BottomLeft => Normalize(-1, 1),
        ApproachDirection.BottomRight => Normalize(1, 1),
        ApproachDirection.Left => (-1, 0),
        ApproachDirection.Right => (1, 0),
        ApproachDirection.Top => (0, -1),
        ApproachDirection.Bottom => (0, 1),
        _ => (1, 0),
    };

    private static (float, float) Normalize(float x, float y)
    {
        var len = MathF.Sqrt(x * x + y * y);
        return (x / len, y / len);
    }

    /// <summary>上游 --from 的 9 个合法值 + auto（返回 null = 自动选边）。</summary>
    public static ApproachDirection? ParseDirection(string? raw)
    {
        if (raw is null || raw.ToLowerInvariant() == "auto") return null;

        return raw.ToLowerInvariant() switch
        {
            "top-left" => ApproachDirection.TopLeft,
            "top-right" => ApproachDirection.TopRight,
            "bottom-left" => ApproachDirection.BottomLeft,
            "bottom-right" => ApproachDirection.BottomRight,
            "left" => ApproachDirection.Left,
            "right" => ApproachDirection.Right,
            "top" => ApproachDirection.Top,
            "bottom" => ApproachDirection.Bottom,
            _ => throw new ArgumentException(
                $"--from '{raw}' is not one of auto, top-left, top, top-right, right, bottom-right, bottom, bottom-left, left"),
        };
    }

    /// <summary>
    /// 所有 rect 都是显示器局部左上原点坐标；<paramref name="area"/> 是显示器的可用区。
    /// marked 是被指的矩形（点目标传 1×1）。
    /// </summary>
    public static Result Place(
        SizeF signSize,
        RectangleF marked,
        RectangleF area,
        float reach,
        ApproachDirection? forced = null,
        IReadOnlyList<RectangleF>? others = null)
    {
        var keepOut = KeepOutZone(marked);
        var usable = RectangleF.Inflate(area, -Margin, -Margin);
        var otherRects = others?.Select(r => RectangleF.Inflate(r, OtherGap, OtherGap)).ToArray()
            ?? Array.Empty<RectangleF>();

        // 强制方向只是偏好：牌子在那里放不下时，空间最富余的一侧获胜——
        // 箭头永远不能挡住它指的东西。其他箭头的牌子先避让，实在不行才忽略。
        var attempts = new[]
        {
            (Tolerant: false, Sides: forced is { } f ? new[] { f } : Array.Empty<ApproachDirection>()),
            (Tolerant: false, Sides: AllDirections()),
            (Tolerant: true, Sides: forced is { } f2 ? new[] { f2 } : Array.Empty<ApproachDirection>()),
            (Tolerant: true, Sides: AllDirections()),
        };

        foreach (var (tolerant, sides) in attempts)
        {
            if (sides.Length == 0) continue;
            if (Best(signSize, keepOut, usable, reach, sides, otherRects, tolerant) is { } result)
            {
                return result;
            }
        }

        return FallbackClamped(signSize, keepOut, usable, reach);
    }

    private static ApproachDirection[] AllDirections() =>
        (ApproachDirection[])Enum.GetValues(typeof(ApproachDirection));

    /// <summary>目标周围的方形禁区，长到盖住被指矩形本身。</summary>
    public static RectangleF KeepOutZone(RectangleF marked)
    {
        var square = new RectangleF(
            marked.X + marked.Width / 2 - KeepOutSide / 2,
            marked.Y + marked.Height / 2 - KeepOutSide / 2,
            KeepOutSide,
            KeepOutSide);

        var grown = RectangleF.Inflate(marked, 12, 12);
        return RectangleF.Union(square, grown);
    }

    private static Result? Best(
        SizeF size,
        RectangleF keepOut,
        RectangleF usable,
        float reach,
        ApproachDirection[] directions,
        RectangleF[] others,
        bool tolerant)
    {
        Result? best = null;
        var bestScore = float.NegativeInfinity;

        for (var rank = 0; rank < directions.Length; rank++)
        {
            var direction = directions[rank];
            foreach (var scale in ReachScales)
            {
                var rect = SignRect(size, keepOut, direction, reach * scale);
                var overlap = OverlapWith(rect, others);

                if (!usable.Contains(rect) || rect.IntersectsWith(keepOut)) continue;
                if (!tolerant && overlap > 0) continue;

                var score = Clearance(rect, usable)
                    - MathF.Abs(scale - 1) * ShortcutPenalty
                    - rank * 4
                    - overlap * OverlapPenalty;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Result(direction, rect, keepOut);
                }
            }
        }

        return best;
    }

    private static RectangleF SignRect(SizeF size, RectangleF keepOut, ApproachDirection direction, float reach)
    {
        var (dx, dy) = Unit(direction);
        var distance = Support(keepOut.Size, dx, dy) + reach + Support(size, dx, dy);

        var center = new PointF(
            keepOut.X + keepOut.Width / 2 + dx * distance,
            keepOut.Y + keepOut.Height / 2 + dy * distance);

        return new RectangleF(
            center.X - size.Width / 2,
            center.Y - size.Height / 2,
            size.Width,
            size.Height);
    }

    /// <summary>矩形沿单位向量的半幅投影（支撑函数）。</summary>
    private static float Support(SizeF size, float dx, float dy) =>
        MathF.Abs(dx) * size.Width / 2 + MathF.Abs(dy) * size.Height / 2;

    /// <summary>牌子四边到可用区边缘的最小距离，封顶 200。</summary>
    private static float Clearance(RectangleF rect, RectangleF area)
    {
        var edges = new[]
        {
            rect.X - area.X,
            area.Right - rect.Right,
            rect.Y - area.Y,
            area.Bottom - rect.Bottom,
        };
        return MathF.Min(MathF.Min(edges[0], edges[1]), MathF.Min(edges[2], edges[3]));
    }

    private static float OverlapWith(RectangleF rect, RectangleF[] others)
    {
        var total = 0f;
        foreach (var other in others)
        {
            var shared = RectangleF.Intersect(rect, other);
            if (!shared.IsEmpty) total += shared.Width * shared.Height;
        }

        return total;
    }

    /// <summary>没有任何候选干净放下（极小屏或巨大牌子）：保住屏内，选与禁区重叠最小的。</summary>
    private static Result FallbackClamped(SizeF size, RectangleF keepOut, RectangleF usable, float reach)
    {
        Result? best = null;
        var bestArea = float.PositiveInfinity;

        foreach (var direction in AllDirections())
        {
            var rect = SignRect(size, keepOut, direction, reach);
            var clamped = ClampInto(rect, usable);
            var overlap = RectangleF.Intersect(clamped, keepOut);
            var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;

            if (area < bestArea)
            {
                bestArea = area;
                best = new Result(direction, clamped, keepOut);
            }
        }

        return best ?? new Result(ApproachDirection.Right, ClampInto(SignRect(size, keepOut, ApproachDirection.Right, reach), usable), keepOut);
    }

    private static RectangleF ClampInto(RectangleF rect, RectangleF bounds)
    {
        var x = Math.Clamp(rect.X, bounds.X, Math.Max(bounds.X, bounds.Right - rect.Width));
        var y = Math.Clamp(rect.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - rect.Height));
        return new RectangleF(x, y, rect.Width, rect.Height);
    }
}
