using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    // Shared material pool keyed by visual style parameters so all scroll-list
    // items with identical styling share a single Material instance and batch
    // into one UGUI Canvas draw call.
    private static readonly Dictionary<MaterialStyleKey, Material> SharedMaterials = new();
    private static readonly Dictionary<int, MaterialStyleKey> InstanceStyleKeys = new();
    private static readonly Dictionary<int, Color> MaterialBaseFaceColors = new();

    // Cache parent Selectable lookups per TMP_Text instanceId so we never call
    // GetComponentInParent<Selectable>() across IL2CPP during scrolling.
    private static readonly HashSet<int> CheckedSelectables = new();
    private static readonly Dictionary<int, Selectable> ButtonSelectables = new();
    private static readonly Dictionary<int, List<TMP_Text>> SelectableToTexts = new();
    private static readonly Dictionary<int, bool> SelectableInteractableStates = new();

    private static StyleValues _defaults = new();
    private static string _stylePath;
    [ThreadStatic]
    private static bool _applying;
    private static TMP_Text _batchText;
    private static bool _batchRequested;
    private static bool _batchTranslated;
    private static float _batchWidth;
    private static float _batchDilate;
    private static TMP_FontAsset _batchFont;
    private static StyleValues _batchBaseline;
    private static StyleValues _resolvedBaseline;

    private readonly struct MaterialStyleKey : IEquatable<MaterialStyleKey>
    {
        public readonly int FontId;
        public readonly int BaseMaterialId;
        public readonly uint PackedFaceColor;
        public readonly uint PackedOutlineColor;
        public readonly short OutlineWidthMilli;
        public readonly short FaceDilateMilli;
        public readonly byte Flags; // bit 0: outline, bit 1: underlay
        public readonly uint PackedUnderlayColor;
        public readonly short UnderlayOffsetXMilli;
        public readonly short UnderlayOffsetYMilli;
        public readonly short UnderlayDilateMilli;
        public readonly short UnderlaySoftnessMilli;

        public MaterialStyleKey(
            int fontId,
            int baseMaterialId,
            Color faceColor,
            bool outline,
            Color outlineColor,
            float outlineWidth,
            float faceDilate,
            bool underlay,
            Color underlayColor,
            float underlayOffsetX,
            float underlayOffsetY,
            float underlayDilate,
            float underlaySoftness)
        {
            FontId = fontId;
            BaseMaterialId = baseMaterialId;
            PackedFaceColor = PackColor(faceColor);
            PackedOutlineColor = PackColor(outlineColor);
            OutlineWidthMilli = Quantize(outline ? Mathf.Clamp01(outlineWidth) : 0f);
            FaceDilateMilli = Quantize(Mathf.Clamp(faceDilate, -1f, 1f));
            Flags = (byte)((outline ? 1 : 0) | (underlay ? 2 : 0));
            PackedUnderlayColor = underlay ? PackColor(underlayColor) : 0u;
            UnderlayOffsetXMilli = underlay ? Quantize(Mathf.Clamp(underlayOffsetX, -1f, 1f)) : (short)0;
            UnderlayOffsetYMilli = underlay ? Quantize(Mathf.Clamp(underlayOffsetY, -1f, 1f)) : (short)0;
            UnderlayDilateMilli = underlay ? Quantize(Mathf.Clamp(underlayDilate, -1f, 1f)) : (short)0;
            UnderlaySoftnessMilli = underlay ? Quantize(Mathf.Clamp01(underlaySoftness)) : (short)0;
        }

        private static short Quantize(float value) =>
            (short)Mathf.Clamp(Mathf.RoundToInt(value * 1000f), short.MinValue, short.MaxValue);

        private static uint PackColor(Color c)
        {
            Color32 c32 = c;
            return ((uint)c32.r << 24) | ((uint)c32.g << 16) | ((uint)c32.b << 8) | c32.a;
        }

        public bool Equals(MaterialStyleKey other) =>
            FontId == other.FontId &&
            BaseMaterialId == other.BaseMaterialId &&
            PackedFaceColor == other.PackedFaceColor &&
            PackedOutlineColor == other.PackedOutlineColor &&
            OutlineWidthMilli == other.OutlineWidthMilli &&
            FaceDilateMilli == other.FaceDilateMilli &&
            Flags == other.Flags &&
            PackedUnderlayColor == other.PackedUnderlayColor &&
            UnderlayOffsetXMilli == other.UnderlayOffsetXMilli &&
            UnderlayOffsetYMilli == other.UnderlayOffsetYMilli &&
            UnderlayDilateMilli == other.UnderlayDilateMilli &&
            UnderlaySoftnessMilli == other.UnderlaySoftnessMilli;

        public override bool Equals(object obj) => obj is MaterialStyleKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(FontId);
            hash.Add(BaseMaterialId);
            hash.Add(PackedFaceColor);
            hash.Add(PackedOutlineColor);
            hash.Add(OutlineWidthMilli);
            hash.Add(FaceDilateMilli);
            hash.Add(Flags);
            if ((Flags & 2) != 0)
            {
                hash.Add(PackedUnderlayColor);
                hash.Add(UnderlayOffsetXMilli);
                hash.Add(UnderlayOffsetYMilli);
                hash.Add(UnderlayDilateMilli);
                hash.Add(UnderlaySoftnessMilli);
            }
            return hash.ToHashCode();
        }
    }

    internal static bool SubmitBaseline(TMP_Text text, TMP_FontAsset font, float width,
        float dilate, Color outlineColor, float? spacing, bool outline = true)
    {
        if (_batchText == null || _batchText != text) return false;
        _batchBaseline = new StyleValues
        {
            Font = font.name,
            OutlineWidth = width,
            FaceDilate = dilate,
            OutlineColor = outlineColor,
            LineSpacing = spacing ?? _batchBaseline?.LineSpacing,
            Outline = outline
        };
        _batchRequested = true;
        _batchFont = font;
        _batchWidth = width;
        _batchDilate = dilate;
        return true;
    }

    internal static void BeginPresentation(TMP_Text text)
    {
        _batchText = text;
        _batchRequested = false;
        _batchTranslated = false;
        _batchBaseline = null;
    }

    internal static void EndPresentation()
    {
        var text = _batchText;
        _batchText = null;
        _resolvedBaseline = _batchBaseline;
        try
        {
            if (_batchRequested && text != null)
                Apply(text, _batchWidth, _batchDilate, _batchFont, _batchTranslated);
        }
        finally { _batchRequested = false; _resolvedBaseline = null; _batchBaseline = null; }
    }

    internal static bool IsApplying => _applying;
    internal static bool IsCollecting(TMP_Text text) => _batchText != null && _batchText == text;

    public static void Initialize(string pluginRoot)
    {
        _stylePath = Path.Combine(pluginRoot, "MonsterMusumeTDMod", "translations", "styles.json");
        Reload();
    }

    public static void Reload()
    {
        foreach (var material in SharedMaterials.Values)
            if (material != null)
                UnityEngine.Object.Destroy(material);
        SharedMaterials.Clear();
        InstanceStyleKeys.Clear();
        MaterialBaseFaceColors.Clear();
        CheckedSelectables.Clear();
        ButtonSelectables.Clear();
        SelectableToTexts.Clear();
        SelectableInteractableStates.Clear();
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
                        Rules.Add(new StyleRule(UiStylePolicy.NormalizePath(property.Name), ReadValues(property.Value)));

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
        if (_batchText != null && text == _batchText)
        {
            _batchRequested = true;
            _batchTranslated |= isTranslated;
            _batchWidth = fallbackOutlineWidth;
            _batchDilate = fallbackFaceDilate;
            _batchFont = alimamaFont;
            return false;
        }
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

        var path = UiHierarchyClassifier.GetNormalizedHierarchyPath(text.transform);
        StyleRule matched = null;
        foreach (var rule in Rules)
        {
            if (UiStylePolicy.PathContains(path, rule.Path))
            {
                matched = rule;
                break;
            }
        }
        var values = UiStylePolicy.Resolve(matched?.Values, _defaults, _resolvedBaseline, isTranslated);
        if (values == null) return false;
        var fontColor = values.FontColor;
        var outlineColor = values.OutlineColor ?? new Color32(0x54, 0x4A, 0x4A, 255);
        var outlineWidth = values.OutlineWidth ?? fallbackOutlineWidth;
        var lineSpacing = values.LineSpacing;
        var outline = values.Outline ?? true;
        var faceDilate = values.FaceDilate ?? fallbackFaceDilate;
        var font = values.Font;
        var underlay = values.Underlay;
        var underlayColor = values.UnderlayColor ?? new Color32(0x54, 0x4A, 0x4A, 255);
        var underlayOffsetX = values.UnderlayOffsetX ?? 0f;
        var underlayOffsetY = values.UnderlayOffsetY ?? -0.5f;
        var underlayDilate = values.UnderlayDilate ?? 0f;
        var underlaySoftness = values.UnderlaySoftness ?? 0f;
        var changed = false;

        var originalMaterial = text.fontSharedMaterial;
        var selectedFont = TmpFontInstaller.ResolveStyleFont(font, alimamaFont);
        if (selectedFont != null && text.font != selectedFont)
        {
            var originalColor = text.color;
            var originalGradientEnabled = text.enableVertexGradient;
            var originalGradient = text.colorGradient;
            var originalGradientPreset = text.colorGradientPreset;
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
        var baseMaterial = materialFont != null ? materialFont.material : originalMaterial;
        if (baseMaterial == null)
            return changed;

        if (!MaterialBaseFaceColors.TryGetValue(instanceId, out var baseFaceColor))
        {
            baseFaceColor = Color.white;
            if (!fontColor.HasValue)
            {
                if (TmpFontInstaller.TryGetOriginalFaceColor(text, out var translatedFaceColor))
                    baseFaceColor = translatedFaceColor;
                else if (originalMaterial != null && originalMaterial.HasProperty("_FaceColor"))
                    baseFaceColor = originalMaterial.GetColor("_FaceColor");
                else if (baseMaterial.HasProperty("_FaceColor"))
                    baseFaceColor = baseMaterial.GetColor("_FaceColor");
            }
            MaterialBaseFaceColors[instanceId] = baseFaceColor;
        }
        else if (fontColor.HasValue)
        {
            baseFaceColor = Color.white;
        }

        var targetFaceColor = baseFaceColor * GetButtonTintCached(text, instanceId, baseFaceColor);
        var useUnderlay = underlay.HasValue && underlay.Value;
        var styleKey = new MaterialStyleKey(
            selectedFontId,
            baseMaterial.GetInstanceID(),
            targetFaceColor,
            outline,
            outlineColor,
            outlineWidth,
            faceDilate,
            useUnderlay,
            underlayColor,
            underlayOffsetX,
            underlayOffsetY,
            underlayDilate,
            underlaySoftness);

        var material = GetOrCreateSharedMaterial(
            styleKey,
            baseMaterial,
            selectedFont,
            text,
            targetFaceColor,
            outline,
            outlineColor,
            outlineWidth,
            faceDilate,
            useUnderlay,
            underlayColor,
            underlayOffsetX,
            underlayOffsetY,
            underlayDilate,
            underlaySoftness);
        InstanceStyleKeys[instanceId] = styleKey;

        // Rely exclusively on TMP's native SDF shader UNDERLAY_ON keyword
        // configured on the shared material above, instead of instantiating
        // duplicate __MMTDUnderlay TextMeshProUGUI GameObjects that double
        // vertex counts and trigger per-frame OnEnable/OnDisable rebuilds.
        if (text.fontSharedMaterial != material)
        {
            text.fontSharedMaterial = material;
            changed = true;
        }
        if (changed)
            text.SetAllDirty();
        return true;
    }

    private static Material GetOrCreateSharedMaterial(
        MaterialStyleKey key,
        Material baseMaterial,
        TMP_FontAsset selectedFont,
        TMP_Text text,
        Color targetFaceColor,
        bool outline,
        Color outlineColor,
        float outlineWidth,
        float faceDilate,
        bool useUnderlay,
        Color underlayColor,
        float underlayOffsetX,
        float underlayOffsetY,
        float underlayDilate,
        float underlaySoftness)
    {
        if (SharedMaterials.TryGetValue(key, out var material) && material != null)
            return material;

        material = new Material(baseMaterial)
        {
            name = $"{baseMaterial.name}_SharedStyle_{SharedMaterials.Count}"
        };
        if (material.HasProperty("_FaceColor"))
            material.SetColor("_FaceColor", targetFaceColor);
        if (material.HasProperty("_OutlineColor"))
            material.SetColor("_OutlineColor", outlineColor);
        var targetWidth = outline ? Mathf.Clamp01(outlineWidth) : 0f;
        if (material.HasProperty("_OutlineWidth"))
            material.SetFloat("_OutlineWidth", targetWidth);
        if (material.HasProperty("_FaceDilate"))
            material.SetFloat("_FaceDilate", Mathf.Clamp(faceDilate, -1f, 1f));
        if (outline && !material.HasShaderKeyword("OUTLINE_ON"))
            material.EnableKeyword("OUTLINE_ON");
        else if (!outline && material.HasShaderKeyword("OUTLINE_ON"))
            material.DisableKeyword("OUTLINE_ON");

        if (useUnderlay)
        {
            SetMaterialColor(material, "_UnderlayColor", underlayColor);
            SetMaterialFloat(material, "_UnderlayOffsetX", underlayOffsetX, -1f, 1f);
            SetMaterialFloat(material, "_UnderlayOffsetY", underlayOffsetY, -1f, 1f);
            SetMaterialFloat(material, "_UnderlayDilate", underlayDilate, -1f, 1f);
            SetMaterialFloat(material, "_UnderlaySoftness", underlaySoftness, 0f, 1f);
            if (!material.HasShaderKeyword("UNDERLAY_ON"))
                material.EnableKeyword("UNDERLAY_ON");
#if !ANDROID
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
#endif
        }
        else if (material.HasShaderKeyword("UNDERLAY_ON"))
        {
            material.DisableKeyword("UNDERLAY_ON");
        }

        SharedMaterials[key] = material;
        return material;
    }

    internal static bool HasUnderlay(int _) => false;

    internal static bool IsInstanceStyled(int instanceId) => InstanceStyleKeys.ContainsKey(instanceId);

    internal static void NotifySelectableStateChanged(object instance)
    {
        if (instance is not Selectable selectable || selectable == null)
            return;

        var selectableId = selectable.GetInstanceID();
        if (!SelectableToTexts.TryGetValue(selectableId, out var texts) || texts.Count == 0)
            return;

        var isInteractable = selectable.IsInteractable();
        if (SelectableInteractableStates.TryGetValue(selectableId, out var previousInteractable) &&
            previousInteractable == isInteractable)
        {
            return;
        }

        SelectableInteractableStates[selectableId] = isInteractable;
        for (var i = texts.Count - 1; i >= 0; i--)
        {
            var text = texts[i];
            if (text == null || text.gameObject == null)
            {
                texts.RemoveAt(i);
                continue;
            }
            MonsterMusumeTDMod.Patches.UiCanvasTranslationScanner.QueueActivationRefresh(text);
        }
    }

    internal static void ReleasePresentation(TMP_Text text)
    {
        if (text == null) return;
        var id = text.GetInstanceID();
        InstanceStyleKeys.Remove(id);
        MaterialBaseFaceColors.Remove(id);
        CheckedSelectables.Remove(id);
        ButtonSelectables.Remove(id);
    }

    internal static void SyncUnderlayLayer(TMP_Text _)
    {
        // Native SDF shader UNDERLAY_ON requires no sibling GameObject syncing.
    }

    internal static void PrepareUnderlayForTextChange(object _)
    {
        // Native SDF shader UNDERLAY_ON updates automatically with the main text mesh.
    }

    internal static void SyncAllUnderlayLayers()
    {
        // Native SDF shader UNDERLAY_ON + event-driven Selectable state transitions
        // require zero per-frame polling in willRenderCanvases.
    }

    private static Color GetButtonTintCached(TMP_Text text, int instanceId, Color baseFaceColor)
    {
        if (!CheckedSelectables.Contains(instanceId))
        {
            CheckedSelectables.Add(instanceId);
            // Scroll-list items sit inside a ScrollRect; their labels do not need
            // per-button disabled-tint material splitting unless directly on a non-scroll button.
            var parentSelectable = text.GetComponentInParent<Selectable>();
            if (parentSelectable != null &&
                parentSelectable.targetGraphic != text &&
                parentSelectable.transition == Selectable.Transition.ColorTint)
            {
                ButtonSelectables[instanceId] = parentSelectable;
                var selectableId = parentSelectable.GetInstanceID();
                if (!SelectableToTexts.TryGetValue(selectableId, out var children))
                {
                    children = new List<TMP_Text>(2);
                    SelectableToTexts[selectableId] = children;
                }
                if (!children.Contains(text))
                    children.Add(text);
                SelectableInteractableStates[selectableId] = parentSelectable.IsInteractable();
            }
        }

        if (!ButtonSelectables.TryGetValue(instanceId, out var selectable) || selectable == null)
            return Color.white;

        if (selectable.IsInteractable())
            return Color.white;

        var labelColor = text.color * baseFaceColor;
        return UiStylePolicy.InheritButtonGray(labelColor, selectable.colors.disabledColor);
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

    private sealed class StyleRule
    {
        public StyleRule(string path, StyleValues values) { Path = path; Values = values; }
        public string Path { get; }
        public StyleValues Values { get; }
    }
}
