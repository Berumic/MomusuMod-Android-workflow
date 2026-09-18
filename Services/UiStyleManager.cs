using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Services;

internal static class UiStyleManager
{
    private static readonly List<StyleRule> Rules = new();
    private static readonly Dictionary<int, Material> Materials = new();
    private static readonly Dictionary<int, int> MaterialFontIds = new();
    private static readonly Dictionary<int, Color> MaterialBaseFaceColors = new();
    private static readonly Dictionary<int, TextMeshProUGUI> UnderlayTexts = new();
    private static readonly Dictionary<int, TextMeshProUGUI> UnderlaySources = new();
    private static readonly Dictionary<int, Material> UnderlayMaterials = new();
    private static readonly List<int> InvalidUnderlayIds = new();
    private static StyleValues _defaults = new();
    private static string _stylePath;
    [ThreadStatic]
    private static bool _applying;

    internal static bool IsApplying => _applying;

    public static void Initialize(string pluginRoot)
    {
        _stylePath = Path.Combine(pluginRoot, "MonsterMusumeTDMod", "translations", "styles.json");
        Reload();
    }

    public static void Reload()
    {
        foreach (var material in Materials.Values)
            if (material != null)
                UnityEngine.Object.Destroy(material);
        Materials.Clear();
        MaterialFontIds.Clear();
        MaterialBaseFaceColors.Clear();
        foreach (var underlayText in UnderlayTexts.Values)
            if (underlayText != null)
                UnityEngine.Object.Destroy(underlayText.gameObject);
        UnderlayTexts.Clear();
        UnderlaySources.Clear();
        foreach (var underlayMaterial in UnderlayMaterials.Values)
            if (underlayMaterial != null)
                UnityEngine.Object.Destroy(underlayMaterial);
        UnderlayMaterials.Clear();
        Rules.Clear();
        _defaults = new StyleValues();
        if (string.IsNullOrEmpty(_stylePath) || !File.Exists(_stylePath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_stylePath));
            var root = document.RootElement;
            if (root.TryGetProperty("$global", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
                _defaults = ReadValues(defaults);

            if (root.TryGetProperty("$styles", out var styles) && styles.ValueKind == JsonValueKind.Object)
                foreach (var property in styles.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object)
                        Rules.Add(new StyleRule(NormalizePath(property.Name), ReadValues(property.Value)));

            Rules.Sort((left, right) => right.Path.Length.CompareTo(left.Path.Length));
            Core.Plugin.Log?.LogInfo($"Loaded {Rules.Count} UI style rule(s) from {_stylePath}");
        }
        catch (Exception exception)
        {
            Core.Plugin.Log?.LogWarning($"读取 UI 样式失败 {_stylePath}: {exception.Message}");
        }
    }

    public static bool Apply(
        TMP_Text text,
        float fallbackOutlineWidth,
        float fallbackFaceDilate,
        TMP_FontAsset alimamaFont,
        bool isTranslated)
    {
        if (_applying)
            return false;

        _applying = true;
        try
        {
            return ApplyCore(
                text,
                fallbackOutlineWidth,
                fallbackFaceDilate,
                alimamaFont,
                isTranslated);
        }
        finally
        {
            _applying = false;
        }
    }

    private static bool ApplyCore(
        TMP_Text text,
        float fallbackOutlineWidth,
        float fallbackFaceDilate,
        TMP_FontAsset alimamaFont,
        bool isTranslated)
    {
        if (text?.transform == null)
            return false;

        var path = NormalizePath(TmpFontInstaller.GetHierarchyPath(text));
        StyleRule matched = null;
        foreach (var rule in Rules)
        {
            if (PathContains(path, rule.Path))
            {
                matched = rule;
                break;
            }
        }
        if (matched == null)
            return false;

        var values = matched.Values;
        var translatedOnly = values.TranslatedOnly ?? _defaults.TranslatedOnly ?? false;
        if (translatedOnly && !isTranslated)
            return false;
        var fontColor = values.FontColor ?? _defaults.FontColor;
        var outlineColor = values.OutlineColor ?? _defaults.OutlineColor ?? new Color32(0x54, 0x4A, 0x4A, 255);
        var outlineWidth = values.OutlineWidth ?? _defaults.OutlineWidth ?? fallbackOutlineWidth;
        var lineSpacing = values.LineSpacing ?? _defaults.LineSpacing;
        var outline = values.Outline ?? _defaults.Outline ?? true;
        var faceDilate = values.FaceDilate ?? _defaults.FaceDilate ?? fallbackFaceDilate;
        var font = values.Font ?? _defaults.Font;
        var underlay = values.Underlay ?? _defaults.Underlay;
        var underlayColor = values.UnderlayColor ?? _defaults.UnderlayColor ??
                            new Color32(0x54, 0x4A, 0x4A, 255);
        var underlayOffsetX = values.UnderlayOffsetX ?? _defaults.UnderlayOffsetX ?? 0f;
        var underlayOffsetY = values.UnderlayOffsetY ?? _defaults.UnderlayOffsetY ?? -0.5f;
        var underlayDilate = values.UnderlayDilate ?? _defaults.UnderlayDilate ?? 0f;
        var underlaySoftness = values.UnderlaySoftness ?? _defaults.UnderlaySoftness ?? 0f;
        var changed = false;

        // TMP font assignment can replace the material preset and, depending
        // on the game-side setter, reset the component's vertex colours.
        // Capture both sources of tint before changing the font.
        var originalColor = text.color;
        var originalGradientEnabled = text.enableVertexGradient;
        var originalGradient = text.colorGradient;
        var originalGradientPreset = text.colorGradientPreset;
        var originalMaterial = text.fontSharedMaterial;

        var selectedFont = TmpFontInstaller.ResolveStyleFont(font, alimamaFont);
        if (selectedFont != null && text.font != selectedFont)
        {
            text.font = selectedFont;
            text.color = originalColor;
            text.enableVertexGradient = originalGradientEnabled;
            text.colorGradientPreset = originalGradientPreset;
            text.colorGradient = originalGradient;
            changed = true;
        }

        if (fontColor.HasValue && text.color != fontColor.Value)
        {
            text.color = fontColor.Value;
            changed = true;
        }
        if (lineSpacing.HasValue && !Mathf.Approximately(text.lineSpacing, lineSpacing.Value))
        {
            text.lineSpacing = lineSpacing.Value;
            changed = true;
        }

        var instanceId = text.GetInstanceID();
        var materialFont = selectedFont ?? text.font;
        var selectedFontId = materialFont == null ? 0 : materialFont.GetInstanceID();
        var materialUsesSelectedFont = MaterialFontIds.TryGetValue(instanceId, out var materialFontId) &&
                                       materialFontId == selectedFontId;
        if (!Materials.TryGetValue(instanceId, out var material) || material == null ||
            !materialUsesSelectedFont)
        {
            if (material != null)
                UnityEngine.Object.Destroy(material);
            // A TMP material must belong to the selected font asset. Copying
            // the old FOT material after switching to Alimama leaves the text
            // bound to the wrong atlas and makes the font override ineffective.
            var baseMaterial = materialFont != null ? materialFont.material : text.fontSharedMaterial;
            if (baseMaterial == null)
                return changed;
            material = new Material(baseMaterial)
            {
                name = $"{baseMaterial.name}_PathStyle_{instanceId}"
            };
            if (!fontColor.HasValue && material.HasProperty("_FaceColor"))
            {
                if (TmpFontInstaller.TryGetOriginalFaceColor(text, out var translatedFaceColor))
                    material.SetColor("_FaceColor", translatedFaceColor);
                else if (originalMaterial != null && originalMaterial.HasProperty("_FaceColor"))
                    material.SetColor("_FaceColor", originalMaterial.GetColor("_FaceColor"));
            }
            Materials[instanceId] = material;
            MaterialFontIds[instanceId] = selectedFontId;
            if (material.HasProperty("_FaceColor"))
                MaterialBaseFaceColors[instanceId] = material.GetColor("_FaceColor");
            changed = true;
        }
        changed |= SyncButtonFaceColor(text, material, instanceId);
        if (material.HasProperty("_OutlineColor") && material.GetColor("_OutlineColor") != outlineColor)
        {
            material.SetColor("_OutlineColor", outlineColor);
            changed = true;
        }
        var targetWidth = outline ? Mathf.Clamp01(outlineWidth) : 0f;
        if (material.HasProperty("_OutlineWidth") &&
            !Mathf.Approximately(material.GetFloat("_OutlineWidth"), targetWidth))
        {
            material.SetFloat("_OutlineWidth", targetWidth);
            changed = true;
        }
        if (material.HasProperty("_FaceDilate") &&
            !Mathf.Approximately(material.GetFloat("_FaceDilate"), faceDilate))
        {
            material.SetFloat("_FaceDilate", Mathf.Clamp(faceDilate, -1f, 1f));
            changed = true;
        }
        if (outline && !material.HasShaderKeyword("OUTLINE_ON"))
        {
            material.EnableKeyword("OUTLINE_ON");
            changed = true;
        }
        else if (!outline && material.HasShaderKeyword("OUTLINE_ON"))
        {
            material.DisableKeyword("OUTLINE_ON");
            changed = true;
        }
        if (underlay.HasValue)
        {
            if (underlay.Value)
            {
                if (!material.HasProperty("_UnderlayColor") ||
                    !material.HasProperty("_UnderlayOffsetX") ||
                    !material.HasProperty("_UnderlayOffsetY") ||
                    !material.HasProperty("_UnderlayDilate") ||
                    !material.HasProperty("_UnderlaySoftness"))
                {
                    Core.Plugin.Log?.LogWarning(
                        $"TMP font material does not expose full underlay properties: " +
                        $"font={selectedFont?.name ?? text.font?.name ?? "<none>"}, " +
                        $"shader={material.shader?.name ?? "<none>"}");
                }
                changed |= SetMaterialColor(material, "_UnderlayColor", underlayColor);
                changed |= SetMaterialFloat(material, "_UnderlayOffsetX", underlayOffsetX, -1f, 1f);
                changed |= SetMaterialFloat(material, "_UnderlayOffsetY", underlayOffsetY, -1f, 1f);
                changed |= SetMaterialFloat(material, "_UnderlayDilate", underlayDilate, -1f, 1f);
                changed |= SetMaterialFloat(material, "_UnderlaySoftness", underlaySoftness, 0f, 1f);
                if (!material.HasShaderKeyword("UNDERLAY_ON"))
                {
                    material.EnableKeyword("UNDERLAY_ON");
                    changed = true;
                }
#if !ANDROID
                // Baked lighting flags are irrelevant to UI and stripped on Android.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
#endif
                changed |= ApplyUnderlayLayer(
                    text,
                    materialFont,
                    underlayColor,
                    underlayOffsetX,
                    underlayOffsetY,
                    underlayDilate,
                    underlaySoftness);
            }
            else
            {
                if (material.HasShaderKeyword("UNDERLAY_ON"))
                {
                    material.DisableKeyword("UNDERLAY_ON");
                    changed = true;
                }
                changed |= RemoveUnderlayLayer(text);
            }
        }
        if (text.fontSharedMaterial != material)
        {
            text.fontSharedMaterial = material;
            changed = true;
        }
        if (changed)
            text.SetAllDirty();
        return true;
    }

    private static bool ApplyUnderlayLayer(
        TMP_Text text,
        TMP_FontAsset font,
        Color color,
        float offsetX,
        float offsetY,
        float dilate,
        float softness)
    {
        if (text is not TextMeshProUGUI source || source.gameObject == null || font?.material == null)
            return false;

        var id = text.GetInstanceID();
        if (!UnderlayTexts.TryGetValue(id, out var layer) || layer == null)
        {
            var layerObject = new GameObject(
                $"{source.gameObject.name}__MMTDUnderlay",
                new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
            layerObject.transform.SetParent(source.transform.parent, false);
            layer = layerObject.AddComponent<TextMeshProUGUI>();
            if (layer == null)
            {
                UnityEngine.Object.Destroy(layerObject);
                return false;
            }
            layer.enabled = false;
            var layout = layerObject.AddComponent<LayoutElement>();
            if (layout != null)
                layout.ignoreLayout = true;
            layer.raycastTarget = false;
            UnderlayTexts[id] = layer;
        }
        UnderlaySources[id] = source;

        var changed = false;
        if (!UnderlayMaterials.TryGetValue(id, out var layerMaterial) || layerMaterial == null ||
            layer.font != font)
        {
            if (layerMaterial != null)
                UnityEngine.Object.Destroy(layerMaterial);
            layerMaterial = new Material(font.material)
            {
                name = $"{font.material.name}_PathUnderlay_{id}"
            };
            UnderlayMaterials[id] = layerMaterial;
            layer.font = font;
            layer.fontSharedMaterial = layerMaterial;
            changed = true;
        }

        var targetColor = color;
        targetColor.a = color.a <= 0f ? 1f : color.a;
        changed |= SetMaterialColor(layerMaterial, "_FaceColor", targetColor);
        changed |= SetMaterialFloat(layerMaterial, "_FaceDilate", dilate, -1f, 1f);
        changed |= SetMaterialFloat(layerMaterial, "_OutlineWidth", softness, 0f, 1f);
        changed |= SetMaterialColor(layerMaterial, "_OutlineColor", targetColor);
        if (softness > 0f && !layerMaterial.HasShaderKeyword("OUTLINE_ON"))
        {
            layerMaterial.EnableKeyword("OUTLINE_ON");
            changed = true;
        }
        else if (softness <= 0f && layerMaterial.HasShaderKeyword("OUTLINE_ON"))
        {
            layerMaterial.DisableKeyword("OUTLINE_ON");
            changed = true;
        }
        if (layerMaterial.HasShaderKeyword("UNDERLAY_ON"))
        {
            layerMaterial.DisableKeyword("UNDERLAY_ON");
            changed = true;
        }

        var sourceRect = source.rectTransform;
        var layerRect = layer.rectTransform;
        layerRect.anchorMin = sourceRect.anchorMin;
        layerRect.anchorMax = sourceRect.anchorMax;
        layerRect.pivot = sourceRect.pivot;
        layerRect.sizeDelta = sourceRect.sizeDelta;
        layerRect.localScale = sourceRect.localScale;
        layerRect.localRotation = sourceRect.localRotation;
        var distance = new Vector2(offsetX, offsetY) * 6f;
        if (distance.sqrMagnitude < 0.01f)
            distance = new Vector2(1f, -4f);
        layerRect.anchoredPosition = sourceRect.anchoredPosition + distance;
        // Keep the projection immediately behind the source. Setting both to
        // the same index on every refresh swaps their order back and forth,
        // which presents as flicker and can leave the dark layer in front.
        var layerIndex = layer.transform.GetSiblingIndex();
        var sourceIndex = source.transform.GetSiblingIndex();
        if (layerIndex > sourceIndex)
        {
            layer.transform.SetSiblingIndex(sourceIndex);
            changed = true;
        }

        layer.text = source.text;
        layer.fontSize = source.fontSize;
        layer.fontStyle = source.fontStyle;
        layer.alignment = source.alignment;
        layer.enableAutoSizing = source.enableAutoSizing;
        layer.fontSizeMin = source.fontSizeMin;
        layer.fontSizeMax = source.fontSizeMax;
        layer.enableWordWrapping = source.enableWordWrapping;
        layer.overflowMode = source.overflowMode;
        layer.richText = source.richText;
        layer.characterSpacing = source.characterSpacing;
        layer.wordSpacing = source.wordSpacing;
        layer.lineSpacing = source.lineSpacing;
        layer.paragraphSpacing = source.paragraphSpacing;
        layer.margin = source.margin;
        layer.color = Color.white;
        layer.enableVertexGradient = false;
        layer.maskable = source.maskable;
        layer.SetAllDirty();
        layer.enabled = source.enabled;
        return changed;
    }

    private static bool RemoveUnderlayLayer(TMP_Text text)
    {
        if (text == null)
            return false;
        var id = text.GetInstanceID();
        var removed = false;
        if (UnderlayTexts.Remove(id, out var layer) && layer != null)
        {
            UnityEngine.Object.Destroy(layer.gameObject);
            removed = true;
        }
        UnderlaySources.Remove(id);
        if (UnderlayMaterials.Remove(id, out var material) && material != null)
        {
            UnityEngine.Object.Destroy(material);
            removed = true;
        }
        return removed;
    }

    internal static void SyncUnderlayLayer(TMP_Text text)
    {
        if (text is not TextMeshProUGUI source ||
            !UnderlayTexts.TryGetValue(text.GetInstanceID(), out var layer) || layer == null)
            return;

        // Run after translation processing so the projection never retains
        // the source-language value seen by an earlier TMP setter hook.
        layer.enabled = false;
        layer.text = source.text;
        layer.fontSize = source.fontSize;
        layer.fontStyle = source.fontStyle;
        layer.alignment = source.alignment;
        layer.enableAutoSizing = source.enableAutoSizing;
        layer.fontSizeMin = source.fontSizeMin;
        layer.fontSizeMax = source.fontSizeMax;
        layer.enableWordWrapping = source.enableWordWrapping;
        layer.overflowMode = source.overflowMode;
        layer.richText = source.richText;
        layer.characterSpacing = source.characterSpacing;
        layer.wordSpacing = source.wordSpacing;
        layer.lineSpacing = source.lineSpacing;
        layer.paragraphSpacing = source.paragraphSpacing;
        layer.margin = source.margin;
        layer.maskable = source.maskable;
        if (Materials.TryGetValue(source.GetInstanceID(), out var sourceMaterial) && sourceMaterial != null)
            SyncButtonFaceColor(source, sourceMaterial, source.GetInstanceID());
        var layerColor = Color.white;
        layerColor.a = source.color.a;
        layer.color = layerColor;

        if (layer.transform.GetSiblingIndex() > source.transform.GetSiblingIndex())
            layer.transform.SetSiblingIndex(source.transform.GetSiblingIndex());

        layer.SetAllDirty();
        layer.enabled = CanRenderUnderlay(source);
    }

    internal static void PrepareUnderlayForTextChange(object component)
    {
        if (component is TMP_Text text &&
            UnderlayTexts.TryGetValue(text.GetInstanceID(), out var layer) && layer != null)
            layer.enabled = false;
    }

    internal static void SyncAllUnderlayLayers()
    {
        if (UnderlaySources.Count == 0)
            return;

        InvalidUnderlayIds.Clear();
        foreach (var entry in UnderlaySources)
        {
            var source = entry.Value;
            if (source == null || source.gameObject == null)
            {
                InvalidUnderlayIds.Add(entry.Key);
                continue;
            }
            SyncUnderlayLayer(source);
        }

        foreach (var id in InvalidUnderlayIds)
        {
            if (UnderlayTexts.Remove(id, out var layer) && layer != null)
                UnityEngine.Object.Destroy(layer.gameObject);
            if (UnderlayMaterials.Remove(id, out var material) && material != null)
                UnityEngine.Object.Destroy(material);
            UnderlaySources.Remove(id);
        }
    }

    private static bool CanRenderUnderlay(TextMeshProUGUI source)
    {
        if (!source.enabled || !source.gameObject.activeInHierarchy || source.color.a <= 0.001f)
            return false;

        // A disabled Button still renders its label and its shadow/underlay;
        // only the graphic color changes. Do not hide the projection here.
        var groups = source.GetComponentsInParent<CanvasGroup>(true);
        foreach (var group in groups)
            if (group != null && (!group.interactable || group.alpha <= 0.001f))
                return false;

        return true;
    }

    private static bool SyncButtonFaceColor(TMP_Text text, Material material, int instanceId)
    {
        if (text == null || material == null || !material.HasProperty("_FaceColor") ||
            !MaterialBaseFaceColors.TryGetValue(instanceId, out var baseColor))
            return false;

        var targetColor = baseColor;
        var button = text.GetComponentInParent<Button>();
        if (button != null && !button.interactable)
        {
            var disabledColor = button.colors.disabledColor;
            targetColor = new Color(
                baseColor.r * disabledColor.r,
                baseColor.g * disabledColor.g,
                baseColor.b * disabledColor.b,
                baseColor.a * disabledColor.a);
        }

        if (material.GetColor("_FaceColor") == targetColor)
            return false;
        material.SetColor("_FaceColor", targetColor);
        text.SetMaterialDirty();
        return true;
    }

    private static StyleValues ReadValues(JsonElement element)
    {
        var values = new StyleValues();
        if (TryReadColor(element, "fontColor", out var fontColor)) values.FontColor = fontColor;
        if (TryReadColor(element, "outlineColor", out var outlineColor)) values.OutlineColor = outlineColor;
        if (TryReadColor(element, "underlayColor", out var underlayColor)) values.UnderlayColor = underlayColor;
        if (element.TryGetProperty("outlineWidth", out var width) && width.TryGetSingle(out var widthValue))
            values.OutlineWidth = widthValue;
        if (element.TryGetProperty("lineSpacing", out var spacing) && spacing.TryGetSingle(out var spacingValue))
            values.LineSpacing = spacingValue;
        if (element.TryGetProperty("faceDilate", out var dilate) && dilate.TryGetSingle(out var dilateValue))
            values.FaceDilate = dilateValue;
        if (element.TryGetProperty("underlayOffsetX", out var offsetX) && offsetX.TryGetSingle(out var offsetXValue))
            values.UnderlayOffsetX = offsetXValue;
        if (element.TryGetProperty("underlayOffsetY", out var offsetY) && offsetY.TryGetSingle(out var offsetYValue))
            values.UnderlayOffsetY = offsetYValue;
        if (element.TryGetProperty("underlayDilate", out var underlayDilate) && underlayDilate.TryGetSingle(out var underlayDilateValue))
            values.UnderlayDilate = underlayDilateValue;
        if (element.TryGetProperty("underlaySoftness", out var softness) && softness.TryGetSingle(out var softnessValue))
            values.UnderlaySoftness = softnessValue;
        if (element.TryGetProperty("outline", out var outline) &&
            (outline.ValueKind == JsonValueKind.True || outline.ValueKind == JsonValueKind.False))
            values.Outline = outline.GetBoolean();
        if (element.TryGetProperty("underlay", out var underlay) &&
            (underlay.ValueKind == JsonValueKind.True || underlay.ValueKind == JsonValueKind.False))
            values.Underlay = underlay.GetBoolean();
        if (element.TryGetProperty("translatedOnly", out var translatedOnly) &&
            (translatedOnly.ValueKind == JsonValueKind.True || translatedOnly.ValueKind == JsonValueKind.False))
            values.TranslatedOnly = translatedOnly.GetBoolean();
        if (element.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.String)
            values.Font = font.GetString();
        return values;
    }

    private static bool SetMaterialColor(Material material, string property, Color value)
    {
        if (!material.HasProperty(property) || material.GetColor(property) == value)
            return false;
        material.SetColor(property, value);
        return true;
    }

    private static bool SetMaterialFloat(
        Material material,
        string property,
        float value,
        float minimum,
        float maximum)
    {
        if (!material.HasProperty(property))
            return false;
        var clamped = Mathf.Clamp(value, minimum, maximum);
        if (Mathf.Approximately(material.GetFloat(property), clamped))
            return false;
        material.SetFloat(property, clamped);
        return true;
    }

    private static bool TryReadColor(JsonElement element, string name, out Color color)
    {
        color = default;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
               ColorUtility.TryParseHtmlString(value.GetString(), out color);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
            parts[index] = parts[index].Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join('/', parts);
    }

    private static bool PathContains(string fullPath, string rulePath) =>
        string.Equals(fullPath, rulePath, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(rulePath + "/", StringComparison.OrdinalIgnoreCase) ||
        fullPath.EndsWith("/" + rulePath, StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains("/" + rulePath + "/", StringComparison.OrdinalIgnoreCase);

    private sealed class StyleRule
    {
        public StyleRule(string path, StyleValues values) { Path = path; Values = values; }
        public string Path { get; }
        public StyleValues Values { get; }
    }

    private sealed class StyleValues
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
}
