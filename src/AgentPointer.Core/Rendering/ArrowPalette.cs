// Adapted from bigarrow by Franz Enzenhofer.
// Copyright (c) 2026 Franz Enzenhofer. MIT License; see THIRD_PARTY_NOTICES.md.
using System.Globalization;

namespace AgentPointer.Core.Rendering;

/// <summary>
/// sRGB 颜色：预置名或十六进制解析。移植上游 ArrowAppearance.swift 的 ArrowColor——
/// presets 色值、Rec.709 亮度、对比色与描边色的阈值全部与上游一致。
/// </summary>
public readonly record struct ArrowColor(float R, float G, float B)
{
    /// <summary>上游十个预置色，hex 逐项一致。</summary>
    public static readonly (string Name, string Hex)[] Presets =
    {
        ("red", "FF3B1F"), ("orange", "FF8A00"), ("yellow", "FFD60A"), ("green", "21B24B"),
        ("teal", "00B5AD"), ("blue", "0A84FF"), ("purple", "8E44FF"), ("pink", "FF2D78"),
        ("black", "1C1C1E"), ("white", "FFFFFF"),
    };

    /// <summary>亮度高于该阈值时，描边与牌子文字转为深色。</summary>
    public const float LightThreshold = 0.6f;

    /// <summary>低于该亮度时黑描边会沉进箭头里，转为白色。</summary>
    public const float DarkThreshold = 0.2f;

    public static ArrowColor Parse(string? raw)
    {
        var key = raw?.Trim().ToLowerInvariant() ?? "red";

        foreach (var (name, hex) in Presets)
        {
            if (name == key) return FromHex(hex);
        }

        return FromHex(key);
    }

    /// <summary>RRGGBB 或短 RGB，带或不带 #。非法值抛 <see cref="ArgumentException"/>。</summary>
    public static ArrowColor FromHex(string raw)
    {
        var digits = raw.StartsWith('#') ? raw[1..] : raw;
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => $"{c}{c}"));

        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"colour '{raw}' is not valid, use a preset or hex like #FF3B1F or #F31");
        }

        return new ArrowColor(
            ((value >> 16) & 0xFF) / 255f,
            ((value >> 8) & 0xFF) / 255f,
            (value & 0xFF) / 255f);
    }

    /// <summary>Rec. 709 相对亮度——上游用它选对比色，足够准。</summary>
    public float Luminance => 0.2126f * R + 0.7152f * G + 0.0722f * B;

    public bool IsLight => Luminance > LightThreshold;

    /// <summary>牌子文字色：深箭头配白字，浅箭头（黄/白）配近黑。</summary>
    public ArrowColor Contrast => IsLight
        ? new ArrowColor(0.11f, 0.11f, 0.12f)
        : new ArrowColor(1f, 1f, 1f);

    /// <summary>--border black 的细描边：近黑箭头配白，其余配黑。</summary>
    public ArrowColor Outline => Luminance < DarkThreshold
        ? new ArrowColor(1f, 1f, 1f)
        : new ArrowColor(0f, 0f, 0f);

    public (byte Rb, byte Gb, byte Bb) ToBytes() => ((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
}
