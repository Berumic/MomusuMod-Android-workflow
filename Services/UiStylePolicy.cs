using UnityEngine;
using System;
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
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join('/', parts);
    }

    public static bool PathContains(string fullPath, string rulePath) =>
        !string.IsNullOrEmpty(rulePath) &&
        (string.Equals(fullPath, rulePath, StringComparison.OrdinalIgnoreCase) ||
         fullPath.StartsWith(rulePath + "/", StringComparison.OrdinalIgnoreCase) ||
         fullPath.EndsWith("/" + rulePath, StringComparison.OrdinalIgnoreCase) ||
         fullPath.Contains("/" + rulePath + "/", StringComparison.OrdinalIgnoreCase));

    public static StyleValues Resolve(StyleValues rule, StyleValues defaults, StyleValues baseline, bool translated)
    {
        if (rule == null || ((rule.TranslatedOnly ?? defaults?.TranslatedOnly ?? false) && !translated))
            return baseline;
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
