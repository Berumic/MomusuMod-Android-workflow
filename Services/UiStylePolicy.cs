using System;
using UnityEngine;

namespace MonsterMusumeTDMod.Services;

internal sealed class StyleValues
{
    public Color? FontColor { get; set; }
    public Color? OutlineColor { get; set; }
    public Color? UnderlayColor { get; set; }
    public float? OutlineWidth { get; set; }
    public float? LineSpacing { get; set; }
    public float? FaceDilate { get; set; }
    public float? UnderlayOffsetX { get; set; }
    public float? UnderlayOffsetY { get; set; }
    public float? UnderlayDilate { get; set; }
    public float? UnderlaySoftness { get; set; }
    public bool? Outline { get; set; }
    public bool? Underlay { get; set; }
    public bool? TranslatedOnly { get; set; }
    public string Font { get; set; }

    public bool HasAnyValue =>
        FontColor.HasValue ||
        OutlineColor.HasValue ||
        UnderlayColor.HasValue ||
        OutlineWidth.HasValue ||
        LineSpacing.HasValue ||
        FaceDilate.HasValue ||
        UnderlayOffsetX.HasValue ||
        UnderlayOffsetY.HasValue ||
        UnderlayDilate.HasValue ||
        UnderlaySoftness.HasValue ||
        Outline.HasValue ||
        Underlay.HasValue ||
        !string.IsNullOrEmpty(Font);
}

internal static class UiStylePolicy
{
    // Button backgrounds may be invisible hit targets. Their alpha does not
    // describe label visibility; coloured/already dimmed labels own their tint.
    public static Color InheritButtonGray(Color label, Color target)
    {
        if (label.r < 0.99f || label.g < 0.99f || label.b < 0.99f ||
            Math.Abs(target.r - target.g) >= 0.005f || Math.Abs(target.g - target.b) >= 0.005f)
            return new Color(1f, 1f, 1f, 1f);
        return new Color(target.r, target.g, target.b, 1f);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) < 0 &&
            path.IndexOf('\\') < 0 &&
            path.IndexOf("//", StringComparison.Ordinal) < 0 &&
            path[0] != '/' &&
            path[^1] != '/')
        {
            return path;
        }
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join('/', parts);
    }

    public static bool PathContains(string fullPath, string rulePath)
    {
        if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(rulePath))
            return false;
        if (string.Equals(fullPath, rulePath, StringComparison.OrdinalIgnoreCase))
            return true;

        var searchIndex = 0;
        var limit = fullPath.Length - rulePath.Length;
        while (searchIndex <= limit)
        {
            var idx = fullPath.IndexOf(rulePath, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;

            var validStart = idx == 0 || fullPath[idx - 1] == '/';
            var endIdx = idx + rulePath.Length;
            var validEnd = endIdx == fullPath.Length || fullPath[endIdx] == '/';
            if (validStart && validEnd)
                return true;

            searchIndex = idx + 1;
        }

        return false;
    }

    public static StyleValues Resolve(StyleValues rule, StyleValues defaults, StyleValues baseline, bool translated)
    {
        var hasDefaults = defaults != null && defaults.HasAnyValue;
        if (rule == null && !hasDefaults)
            return baseline;

        var translatedOnly = rule?.TranslatedOnly ?? defaults?.TranslatedOnly ?? false;
        if (translatedOnly && !translated)
            return baseline;

        rule ??= new StyleValues();
        defaults ??= new StyleValues();
        baseline ??= new StyleValues();
        return new StyleValues
        {
            FontColor = rule.FontColor ?? defaults.FontColor ?? baseline.FontColor,
            OutlineColor = rule.OutlineColor ?? defaults.OutlineColor ?? baseline.OutlineColor,
            UnderlayColor = rule.UnderlayColor ?? defaults.UnderlayColor ?? baseline.UnderlayColor,
            OutlineWidth = rule.OutlineWidth ?? defaults.OutlineWidth ?? baseline.OutlineWidth,
            LineSpacing = rule.LineSpacing ?? defaults.LineSpacing ?? baseline.LineSpacing,
            FaceDilate = rule.FaceDilate ?? defaults.FaceDilate ?? baseline.FaceDilate,
            UnderlayOffsetX = rule.UnderlayOffsetX ?? defaults.UnderlayOffsetX ?? baseline.UnderlayOffsetX,
            UnderlayOffsetY = rule.UnderlayOffsetY ?? defaults.UnderlayOffsetY ?? baseline.UnderlayOffsetY,
            UnderlayDilate = rule.UnderlayDilate ?? defaults.UnderlayDilate ?? baseline.UnderlayDilate,
            UnderlaySoftness = rule.UnderlaySoftness ?? defaults.UnderlaySoftness ?? baseline.UnderlaySoftness,
            Outline = rule.Outline ?? defaults.Outline ?? baseline.Outline,
            Underlay = rule.Underlay ?? defaults.Underlay ?? baseline.Underlay,
            Font = rule.Font ?? defaults.Font ?? baseline.Font,
        };
    }
}
