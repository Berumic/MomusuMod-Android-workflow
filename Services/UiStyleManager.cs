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
    private static readonly Dictionary<int, TMP_Text> StyledTexts = new();
    private static readonly Dictionary<int, TextMeshProUGUI> UnderlayTexts = new();
    private static readonly Dictionary<int, TextMeshProUGUI> UnderlaySources = new();
    private static readonly Dictionary<int, Material> UnderlayMaterials = new();
    private static readonly List<int> InvalidUnderlayIds = new();
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

    internal static bool SubmitBaseline(TMP_Text text, TMP_FontAsset font, float width,
        float dilate, Color outlineColor, float? spacing, bool outline = true)
    {
        if (_batchText == null || _batchText != text) return false;
        _batchBaseline = new StyleValues { Font = font.name, OutlineWidth = width,
            FaceDilate = dilate, OutlineColor = outlineColor, LineSpacing = spacing ?? _batchBaseline?.LineSpacing, Outline = outline };
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
        foreach (var material in Materials.Values)
            if (material != null)
                UnityEngine.Object.Destroy(material);
        Materials.Clear();
        MaterialFontIds.Clear();
        MaterialBaseFaceColors.Clear();
        StyledTexts.Clear();
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

        var path = UiStylePolicy.NormalizePath(TmpFontInstaller.GetHierarchyPath(text));
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
            StyledTexts[instanceId] = text;
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
        // A reused slot may move from an underlay rule to a plain rule.
        // Absence of underlay in the resolved style must retire the old layer.
        if (!underlay.HasValue && UnderlayTexts.ContainsKey(instanceId))
        {
            changed |= RemoveUnderlayLayer(text);
            if (material.HasShaderKeyword("UNDERLAY_ON"))
            {
                material.DisableKeyword("UNDERLAY_ON");
                changed = true;
            }
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

        if (!string.Equals(layer.text, source.text, StringComparison.Ordinal))
            layer.text = source.text;
        if (layer.fontSize != source.fontSize)
            layer.fontSize = source.fontSize;
        if (layer.fontStyle != source.fontStyle)
            layer.fontStyle = source.fontStyle;
        if (layer.alignment != source.alignment)
            layer.alignment = source.alignment;
        if (layer.enableAutoSizing != source.enableAutoSizing)
            layer.enableAutoSizing = source.enableAutoSizing;
        if (layer.fontSizeMin != source.fontSizeMin)
            layer.fontSizeMin = source.fontSizeMin;
        if (layer.fontSizeMax != source.fontSizeMax)
            layer.fontSizeMax = source.fontSizeMax;
        if (layer.enableWordWrapping != source.enableWordWrapping)
            layer.enableWordWrapping = source.enableWordWrapping;
        if (layer.overflowMode != source.overflowMode)
            layer.overflowMode = source.overflowMode;
        if (layer.richText != source.richText)
            layer.richText = source.richText;
        if (layer.characterSpacing != source.characterSpacing)
            layer.characterSpacing = source.characterSpacing;
        if (layer.wordSpacing != source.wordSpacing)
            layer.wordSpacing = source.wordSpacing;
        if (layer.lineSpacing != source.lineSpacing)
            layer.lineSpacing = source.lineSpacing;
        if (layer.paragraphSpacing != source.paragraphSpacing)
            layer.paragraphSpacing = source.paragraphSpacing;
        if (layer.margin != source.margin)
            layer.margin = source.margin;
        layer.color = source.color * source.canvasRenderer.GetColor() * GetButtonTint(source);
        layer.enableVertexGradient = false;
        if (layer.maskable != source.maskable)
            layer.maskable = source.maskable;
        // TMP property setters invalidate only the data that actually changed.
        // Do not force a layout/mesh rebuild for an unchanged projection.
        var visible = CanRenderUnderlay(source);
        if (layer.enabled != visible)
            layer.enabled = visible;
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

    internal static void ReleasePresentation(TMP_Text text)
    {
        if (text == null) return;
        var id = text.GetInstanceID();
        RemoveUnderlayLayer(text);
        StyledTexts.Remove(id);
        MaterialBaseFaceColors.Remove(id);
        MaterialFontIds.Remove(id);
        if (Materials.Remove(id, out var material) && material != null)
            UnityEngine.Object.Destroy(material);
    }

    internal static void SyncUnderlayLayer(TMP_Text text)
    {
        if (text is not TextMeshProUGUI source ||
            !UnderlayTexts.TryGetValue(text.GetInstanceID(), out var layer) || layer == null)
            return;

        // Run after translation processing so the projection never retains
        // the source-language value seen by an earlier TMP setter hook.
        if (!string.Equals(layer.text, source.text, StringComparison.Ordinal))
            layer.text = source.text;
        if (layer.fontSize != source.fontSize)
            layer.fontSize = source.fontSize;
        if (layer.fontStyle != source.fontStyle)
            layer.fontStyle = source.fontStyle;
        if (layer.alignment != source.alignment)
            layer.alignment = source.alignment;
        if (layer.enableAutoSizing != source.enableAutoSizing)
            layer.enableAutoSizing = source.enableAutoSizing;
        if (layer.fontSizeMin != source.fontSizeMin)
            layer.fontSizeMin = source.fontSizeMin;
        if (layer.fontSizeMax != source.fontSizeMax)
            layer.fontSizeMax = source.fontSizeMax;
        if (layer.enableWordWrapping != source.enableWordWrapping)
            layer.enableWordWrapping = source.enableWordWrapping;
        if (layer.overflowMode != source.overflowMode)
            layer.overflowMode = source.overflowMode;
        if (layer.richText != source.richText)
            layer.richText = source.richText;
        if (layer.characterSpacing != source.characterSpacing)
            layer.characterSpacing = source.characterSpacing;
        if (layer.wordSpacing != source.wordSpacing)
            layer.wordSpacing = source.wordSpacing;
        if (layer.lineSpacing != source.lineSpacing)
            layer.lineSpacing = source.lineSpacing;
        if (layer.paragraphSpacing != source.paragraphSpacing)
            layer.paragraphSpacing = source.paragraphSpacing;
        if (layer.margin != source.margin)
            layer.margin = source.margin;
        if (layer.maskable != source.maskable)
            layer.maskable = source.maskable;
        if (Materials.TryGetValue(source.GetInstanceID(), out var sourceMaterial) && sourceMaterial != null)
            SyncButtonFaceColor(source, sourceMaterial, source.GetInstanceID());
        var layerColor = source.color * source.canvasRenderer.GetColor() * GetButtonTint(source);
        if (layer.color != layerColor)
            layer.color = layerColor;

        if (layer.transform.GetSiblingIndex() > source.transform.GetSiblingIndex())
            layer.transform.SetSiblingIndex(source.transform.GetSiblingIndex());

        // This runs before rendering every frame. Toggling enabled here would
        // unregister/register the graphic and rebuild even completely static UI.
        var visible = CanRenderUnderlay(source);
        if (layer.enabled != visible)
            layer.enabled = visible;
    }

    internal static void PrepareUnderlayForTextChange(object component)
    {
        if (component is TMP_Text text &&
            UnderlayTexts.TryGetValue(text.GetInstanceID(), out var layer) && layer != null)
            layer.enabled = false;
    }

    internal static void SyncAllUnderlayLayers()
    {
        // Animated targets can change colour without a Selectable state event.
        // Include styled labels without projection layers as well.
        InvalidUnderlayIds.Clear();
        foreach (var entry in StyledTexts)
        {
            var text = entry.Value;
            if (text == null || text.gameObject == null)
            {
                InvalidUnderlayIds.Add(entry.Key);
                continue;
            }
            if (text.gameObject.activeInHierarchy && !UnderlaySources.ContainsKey(entry.Key) &&
                Materials.TryGetValue(entry.Key, out var material) && material != null)
                SyncButtonFaceColor(text, material, entry.Key);
        }
        foreach (var id in InvalidUnderlayIds)
        {
            StyledTexts.Remove(id);
            MaterialBaseFaceColors.Remove(id);
            MaterialFontIds.Remove(id);
            if (Materials.Remove(id, out var material) && material != null)
                UnityEngine.Object.Destroy(material);
        }
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
            if (!source.gameObject.activeInHierarchy)
            {
                if (UnderlayTexts.TryGetValue(entry.Key, out var hiddenLayer) && hiddenLayer != null && hiddenLayer.enabled)
                    hiddenLayer.enabled = false;
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
        // The sibling projection inherits the same CanvasGroups naturally.
        // Enumerating all parent groups here allocates an IL2CPP array for
        // every label on every rendered frame.
        return true;
    }

    private static bool SyncButtonFaceColor(TMP_Text text, Material material, int instanceId)
    {
        if (text == null || material == null || !material.HasProperty("_FaceColor") ||
            !MaterialBaseFaceColors.TryGetValue(instanceId, out var baseColor))
            return false;

        var targetColor = baseColor * GetButtonTint(text);

        if (material.GetColor("_FaceColor") == targetColor)
            return false;
        material.SetColor("_FaceColor", targetColor);
        text.SetMaterialDirty();
        return true;
    }

    private static Color GetButtonTint(TMP_Text text)
    {
        var selectable = text.GetComponentInParent<Selectable>();
        if (selectable == null)
            return Color.white;

        // A label which is itself the target graphic already receives Unity's
        // colour transition. Multiplying it again would darken it twice.
        if (selectable.targetGraphic == text)
            return Color.white;
        var labelColor = text.color * text.canvasRenderer.GetColor();
        if (MaterialBaseFaceColors.TryGetValue(text.GetInstanceID(), out var baseFace))
            labelColor *= baseFace;
        var target = selectable.targetGraphic;
        if (selectable.transition == Selectable.Transition.Animation && target != null)
        {
            var tint = target.color * target.canvasRenderer.GetColor();
            // Transfer neutral dimming, not decorative background hues.
            // Read the actual animated value, never multiply yesterday's tint.
            if (Mathf.Abs(tint.r - tint.g) < 0.005f &&
                Mathf.Abs(tint.g - tint.b) < 0.005f)
                return UiStylePolicy.InheritButtonGray(labelColor, tint);
        }
        return selectable.IsInteractable() ? Color.white :
            UiStylePolicy.InheritButtonGray(labelColor, selectable.colors.disabledColor);
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
