using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Dictionary<string, StyleRule> ResolvedRulesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ColorTagRegex = new(@"</?color(?:=[^>]*)?>", RegexOptions.IgnoreCase);

    // Shared material pool keyed by visual style parameters so all scroll-list
    // items with identical styling share a single Material instance and batch
    // into one UGUI Canvas draw call.
    private static readonly Dictionary<MaterialStyleKey, Material> SharedMaterials = new();
    private static readonly Dictionary<int, MaterialStyleKey> InstanceStyleKeys = new();
    private static readonly Dictionary<int, StyleRule> InstanceAppliedRules = new();
    private static readonly HashSet<int> ModMaterialIds = new();
    private static readonly Dictionary<int, Material> OriginalGameMaterials = new();
    private static readonly Dictionary<int, Color> MaterialBaseFaceColors = new();
    private static readonly Dictionary<int, (int Frame, bool HasProp, Color Color)> MaterialFaceColorFrameCache = new();
    private static readonly Dictionary<int, Color> PreStyleVertexColors = new();

    // Sibling __MMTDUnderlay projection layers for explicit "underlay": true style rules.
    private static readonly Dictionary<int, UnderlayLayerState> UnderlayLayers = new();
    private static readonly List<int> UnderlayCleanupBuffer = new();
    private static readonly Dictionary<int, (int ChildCount, TMP_Text[] Texts)> SelectableTextsCache = new();

    private static StyleValues _defaults = new();
    private static string _stylePath;
    [ThreadStatic]
    private static bool _applying;
    private static int _presentationDepth;
    private static TMP_Text _batchText;
    private static TMP_Text _activePresentationText;
    private static string _pendingTextOverride;
    private static bool _batchRequested;
    private static bool _batchTranslated;
    private static float _batchWidth;
    private static float _batchDilate;
    private static TMP_FontAsset _batchFont;
    private static StyleValues _batchBaseline;
    private static StyleValues _resolvedBaseline;

    private sealed class UnderlayLayerState
    {
        public TMP_Text SourceText;
        public GameObject UnderlayObject;
        public RectTransform UnderlayRect;
        public TextMeshProUGUI UnderlayText;
        public TMP_FontAsset Font;
        public Material Material;
        public float OffsetX;
        public float OffsetY;
        public string LastSourceText;
        public float LastAlpha = -1f;
        public float LastFontSize = -1f;
        public Vector2 LastAnchoredPosition;
        public Vector2 LastSizeDelta;
        public bool LastActive;
        public bool Dirty = true;
    }

    private readonly struct MaterialStyleKey : IEquatable<MaterialStyleKey>
    {
        public readonly int FontId;
        public readonly int BaseMaterialId;
        public readonly uint PackedFaceColor;
        public readonly uint PackedOutlineColor;
        public readonly short OutlineWidthMilli;
        public readonly short OutlineSoftnessMilli;
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
            float underlaySoftness,
            float outlineSoftness = 0f)
        {
            FontId = fontId;
            BaseMaterialId = baseMaterialId;
            PackedFaceColor = PackColor(faceColor);
            PackedOutlineColor = PackColor(outlineColor);
            OutlineWidthMilli = Quantize(outline ? Mathf.Clamp01(outlineWidth) : 0f);
            OutlineSoftnessMilli = Quantize(outline ? Mathf.Clamp01(outlineSoftness) : 0f);
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

        public static uint PackColor(Color c)
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
            OutlineSoftnessMilli == other.OutlineSoftnessMilli &&
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
            hash.Add(OutlineSoftnessMilli);
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

    internal static void RegisterModMaterial(Material material)
    {
        if (material != null)
            ModMaterialIds.Add(material.GetInstanceID());
    }

    internal static bool IsModMaterial(Material material) =>
        material != null && ModMaterialIds.Contains(material.GetInstanceID());

    internal static void InvalidateInstanceStyle(TMP_Text text)
    {
        if (text == null)
            return;
        var id = text.GetInstanceID();
        InstanceStyleKeys.Remove(id);
        InstanceAppliedRules.Remove(id);
        var material = text.fontSharedMaterial;
        if (material != null && !IsModMaterial(material))
            OriginalGameMaterials[id] = material;
    }

    internal static void ObserveGameMaterial(TMP_Text text)
    {
        if (text == null)
            return;
        var material = text.fontSharedMaterial;
        if (material != null && !IsModMaterial(material))
            OriginalGameMaterials[text.GetInstanceID()] = material;
    }

    private static void ObserveGameMaterial(Material material, int instanceId)
    {
        if (material != null && !IsModMaterial(material))
            OriginalGameMaterials[instanceId] = material;
    }

    internal static bool SubmitBaseline(TMP_Text text, TMP_FontAsset font, float width,
        float dilate, Color outlineColor, float? spacing, bool outline = true, bool isTranslated = true)
    {
        if (_batchText == null || _batchText != text) return false;
        _batchBaseline = new StyleValues
        {
            Font = font?.name,
            OutlineWidth = width,
            FaceDilate = dilate,
            OutlineColor = outlineColor,
            LineSpacing = spacing ?? _batchBaseline?.LineSpacing,
            Outline = outline
        };
        _batchRequested = true;
        _batchTranslated |= isTranslated;
        _batchFont = font;
        _batchWidth = width;
        _batchDilate = dilate;
        return true;
    }

    internal static void BeginPresentation(TMP_Text text, string pendingText = null)
    {
        if (_presentationDepth > 0 && _batchText == text)
        {
            _presentationDepth++;
            if (!string.IsNullOrEmpty(pendingText))
                _pendingTextOverride = pendingText;
            return;
        }

        _presentationDepth = 1;
        _batchText = text;
        _activePresentationText = text;
        _pendingTextOverride = pendingText;
        _batchRequested = false;
        _batchTranslated = false;
        _batchBaseline = null;
    }

    internal static void EndPresentation()
    {
        if (_presentationDepth > 1)
        {
            _presentationDepth--;
            return;
        }

        _presentationDepth = 0;
        var text = _batchText;
        _batchText = null;
        _resolvedBaseline = _batchBaseline;
        try
        {
            if (_batchRequested && text != null)
                Apply(text, _batchWidth, _batchDilate, _batchFont, _batchTranslated);
        }
        finally
        {
            _batchRequested = false;
            _resolvedBaseline = null;
            _batchBaseline = null;
            _activePresentationText = null;
            _pendingTextOverride = null;
        }
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
        {
            if (material != null)
            {
                ModMaterialIds.Remove(material.GetInstanceID());
                UnityEngine.Object.Destroy(material);
            }
        }
        SharedMaterials.Clear();
        InstanceStyleKeys.Clear();
        InstanceAppliedRules.Clear();
        MaterialBaseFaceColors.Clear();
        MaterialFaceColorFrameCache.Clear();
        SelectableTextsCache.Clear();
        ResolvedRulesByPath.Clear();
        Rules.Clear();
        _defaults = new StyleValues();
        if (string.IsNullOrEmpty(_stylePath) || !File.Exists(_stylePath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_stylePath));
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
        bool isTranslated,
        string pendingText = null)
    {
        if (!string.IsNullOrEmpty(pendingText))
        {
            _activePresentationText = text;
            _pendingTextOverride = pendingText;
        }

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
            if (_batchText == null)
            {
                _activePresentationText = null;
                _pendingTextOverride = null;
            }
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
        var matched = ResolveRuleForPath(path);
        var values = UiStylePolicy.Resolve(matched?.Values, _defaults, _resolvedBaseline, isTranslated);
        if (values == null)
        {
            if (UnderlayLayers.Count > 0)
                DisableUnderlayLayer(text.GetInstanceID());
            return false;
        }

        var instanceId = text.GetInstanceID();
        var originalMaterial = text.fontSharedMaterial;
        ObserveGameMaterial(originalMaterial, instanceId);

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

        var selectedFont = TmpFontInstaller.ResolveStyleFont(font, alimamaFont, isTranslated);
        if (selectedFont != null && text.font != selectedFont)
        {
            TmpFontInstaller.CaptureOriginalPresentation(text);
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

        if (fontColor.HasValue)
        {
            PreStyleVertexColors.TryAdd(instanceId, text.color);
            if (text.color != fontColor.Value)
            {
                text.color = fontColor.Value;
                changed = true;
            }
        }
        else if (PreStyleVertexColors.TryGetValue(instanceId, out var savedVertexColor))
        {
            PreStyleVertexColors.Remove(instanceId);
            if (text.color != savedVertexColor)
            {
                text.color = savedVertexColor;
                changed = true;
            }
        }

        if (lineSpacing.HasValue && !Mathf.Approximately(text.lineSpacing, lineSpacing.Value))
        {
            text.lineSpacing = lineSpacing.Value;
            changed = true;
        }

        var materialFont = selectedFont ?? text.font;
        var selectedFontId = materialFont == null ? 0 : materialFont.GetInstanceID();
        var baseMaterial = materialFont != null ? materialFont.material : originalMaterial;
        if (baseMaterial == null)
            return changed;

        RegisterModMaterial(baseMaterial);

        // When fontColor is not explicitly specified in styles.json, always inherit
        // the original game material's _FaceColor and any subsequent changes made by the game.
        var baseFaceColor = ResolveLiveBaseFaceColor(text, instanceId, fontColor.HasValue, originalMaterial, baseMaterial);
        var targetFaceColor = fontColor.HasValue ? Color.white : baseFaceColor;
        var useUnderlay = underlay.HasValue && underlay.Value;
        var effectiveUnderlayDilate = useUnderlay
            ? Mathf.Clamp(underlayDilate + (outline ? Mathf.Clamp01(outlineWidth) : 0f), -1f, 1f)
            : 0f;

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
            effectiveUnderlayDilate,
            underlaySoftness);

        var material = GetOrCreateSharedMaterial(
            styleKey,
            baseMaterial,
            targetFaceColor,
            outline,
            outlineColor,
            outlineWidth,
            faceDilate,
            useUnderlay,
            underlayColor,
            underlayOffsetX,
            underlayOffsetY,
            effectiveUnderlayDilate,
            underlaySoftness,
            outlineSoftness: 0f);
        InstanceStyleKeys[instanceId] = styleKey;
        InstanceAppliedRules[instanceId] = matched;

        if (text.fontSharedMaterial != material)
        {
            text.fontSharedMaterial = material;
            text.UpdateMeshPadding();
            changed = true;
        }

        if (useUnderlay)
        {
            EnsureAndSyncUnderlayLayer(
                text,
                instanceId,
                materialFont,
                baseMaterial,
                targetFaceColor,
                outline,
                outlineWidth,
                faceDilate,
                underlayColor,
                underlayOffsetX,
                underlayOffsetY,
                underlayDilate,
                underlaySoftness);
        }
        else if (UnderlayLayers.Count > 0)
        {
            DisableUnderlayLayer(instanceId);
        }

        if (changed)
            text.SetAllDirty();
        return true;
    }

    private static StyleRule ResolveRuleForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!ResolvedRulesByPath.TryGetValue(path, out var matched))
        {
            foreach (var rule in Rules)
            {
                if (UiStylePolicy.PathContains(path, rule.Path))
                {
                    matched = rule;
                    break;
                }
            }
            if (ResolvedRulesByPath.Count >= 2048)
                ResolvedRulesByPath.Clear();
            ResolvedRulesByPath[path] = matched;
        }
        return matched;
    }

    internal static bool HasMatchingAppliedRule(TMP_Text text, int instanceId)
    {
        if (text?.transform == null || !InstanceAppliedRules.TryGetValue(instanceId, out var appliedRule))
            return false;
        var path = UiHierarchyClassifier.GetNormalizedHierarchyPath(text.transform);
        return ReferenceEquals(appliedRule, ResolveRuleForPath(path));
    }

    internal static bool TryGetMaterialFaceColor(Material material, out Color faceColor)
    {
        if (material == null)
        {
            faceColor = default;
            return false;
        }

        var matId = material.GetInstanceID();
        var currentFrame = Time.frameCount;
        if (MaterialFaceColorFrameCache.TryGetValue(matId, out var cached) &&
            cached.Frame == currentFrame)
        {
            faceColor = cached.Color;
            return cached.HasProp;
        }

        var hasProp = cached.Frame != 0
            ? cached.HasProp
            : material.HasProperty("_FaceColor");
        faceColor = hasProp ? material.GetColor("_FaceColor") : default;
        if (MaterialFaceColorFrameCache.Count >= 512)
            MaterialFaceColorFrameCache.Clear();
        MaterialFaceColorFrameCache[matId] = (currentFrame, hasProp, faceColor);
        return hasProp;
    }

    private static Color ResolveLiveBaseFaceColor(
        TMP_Text text,
        int instanceId,
        bool hasExplicitFontColor,
        Material originalMaterial,
        Material baseMaterial)
    {
        if (hasExplicitFontColor)
            return Color.white;

        if (OriginalGameMaterials.TryGetValue(instanceId, out var gameMaterial) &&
            TryGetMaterialFaceColor(gameMaterial, out var liveColor))
        {
            MaterialBaseFaceColors[instanceId] = liveColor;
            return liveColor;
        }

        if (originalMaterial != null &&
            !IsModMaterial(originalMaterial) &&
            TryGetMaterialFaceColor(originalMaterial, out var origColor))
        {
            MaterialBaseFaceColors[instanceId] = origColor;
            return origColor;
        }

        if (TmpFontInstaller.TryGetOriginalFaceColor(text, out var translatedFaceColor))
        {
            MaterialBaseFaceColors[instanceId] = translatedFaceColor;
            return translatedFaceColor;
        }

        if (MaterialBaseFaceColors.TryGetValue(instanceId, out var cachedFaceColor))
            return cachedFaceColor;

        if (TryGetMaterialFaceColor(baseMaterial, out var fallbackColor))
        {
            MaterialBaseFaceColors[instanceId] = fallbackColor;
            return fallbackColor;
        }

        return Color.white;
    }

    private static Material GetOrCreateSharedMaterial(
        MaterialStyleKey key,
        Material baseMaterial,
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
        float underlaySoftness,
        float outlineSoftness)
    {
        if (SharedMaterials.TryGetValue(key, out var material) && material != null)
            return material;

        material = new Material(baseMaterial)
        {
            name = $"{baseMaterial.name}_SharedStyle_{SharedMaterials.Count}"
        };
        RegisterModMaterial(material);

        if (material.HasProperty("_FaceColor"))
            material.SetColor("_FaceColor", targetFaceColor);
        if (material.HasProperty("_OutlineColor"))
            material.SetColor("_OutlineColor", outlineColor);
        var targetWidth = outline ? Mathf.Clamp01(outlineWidth) : 0f;
        if (material.HasProperty("_OutlineWidth"))
            material.SetFloat("_OutlineWidth", targetWidth);
        if (material.HasProperty("_OutlineSoftness"))
            material.SetFloat("_OutlineSoftness", outline ? Mathf.Clamp01(outlineSoftness) : 0f);
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

    private static void EnsureAndSyncUnderlayLayer(
        TMP_Text text,
        int instanceId,
        TMP_FontAsset materialFont,
        Material baseMaterial,
        Color targetFaceColor,
        bool outline,
        float outlineWidth,
        float faceDilate,
        Color underlayColor,
        float underlayOffsetX,
        float underlayOffsetY,
        float underlayDilate,
        float underlaySoftness)
    {
        if (text == null || text.gameObject == null || text.rectTransform == null || text.transform?.parent == null)
            return;

        var tintedUnderlayColor = new Color(
            underlayColor.r,
            underlayColor.g,
            underlayColor.b,
            underlayColor.a * targetFaceColor.a);
        var layerOutlineWidth = outline ? Mathf.Clamp01(outlineWidth) : 0f;
        var layerHasOutline = layerOutlineWidth > 0.0001f;
        var layerFaceDilate = Mathf.Clamp(faceDilate + underlayDilate, -1f, 1f);
        var selectedFontId = materialFont == null ? 0 : materialFont.GetInstanceID();

        var underlayKey = new MaterialStyleKey(
            selectedFontId,
            baseMaterial.GetInstanceID(),
            tintedUnderlayColor,
            layerHasOutline,
            tintedUnderlayColor,
            layerOutlineWidth,
            layerFaceDilate,
            underlay: false,
            default,
            0f,
            0f,
            0f,
            0f,
            outlineSoftness: underlaySoftness);

        var underlayMaterial = GetOrCreateSharedMaterial(
            underlayKey,
            baseMaterial,
            tintedUnderlayColor,
            layerHasOutline,
            tintedUnderlayColor,
            layerOutlineWidth,
            layerFaceDilate,
            useUnderlay: false,
            default,
            0f,
            0f,
            0f,
            0f,
            outlineSoftness: underlaySoftness);

        if (!UnderlayLayers.TryGetValue(instanceId, out var state) ||
            state == null ||
            state.UnderlayObject == null ||
            state.UnderlayText == null)
        {
            var underlayName = $"{text.gameObject.name}__MMTDUnderlay";
            var existingTransform = text.transform.parent.Find(underlayName);
            GameObject underlayObject;
            if (existingTransform != null && existingTransform.gameObject != null)
            {
                underlayObject = existingTransform.gameObject;
                UiHierarchyClassifier.RegisterInternalLayer(underlayObject, existingTransform);
            }
            else
            {
                underlayObject = new GameObject(
                    underlayName,
                    new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
                UiHierarchyClassifier.RegisterInternalLayer(underlayObject, underlayObject.transform);
                underlayObject.SetActive(false);
                underlayObject.transform.SetParent(text.transform.parent, worldPositionStays: false);
            }

            var layoutElement = underlayObject.GetComponent<LayoutElement>();
            if (layoutElement == null)
                layoutElement = underlayObject.AddComponent<LayoutElement>();
            if (layoutElement != null)
            {
                layoutElement.ignoreLayout = true;
                UiHierarchyClassifier.RegisterInternalLayer(underlayObject, layoutElement);
            }

            var underlayText = underlayObject.GetComponent<TextMeshProUGUI>();
            if (underlayText == null)
            {
                if (underlayObject.activeSelf)
                    underlayObject.SetActive(false);
                underlayText = underlayObject.AddComponent<TextMeshProUGUI>();
            }
            if (underlayText == null)
                return;

            UiHierarchyClassifier.RegisterInternalLayer(underlayObject, underlayText);
            underlayText.raycastTarget = false;

            state = new UnderlayLayerState
            {
                SourceText = text,
                UnderlayObject = underlayObject,
                UnderlayRect = underlayObject.GetComponent<RectTransform>(),
                UnderlayText = underlayText,
                Dirty = true
            };
            UnderlayLayers[instanceId] = state;
        }

        state.SourceText = text;
        state.Font = materialFont;
        state.Material = underlayMaterial;
        state.OffsetX = underlayOffsetX;
        state.OffsetY = underlayOffsetY;
        state.Dirty = true;

        SyncUnderlayState(state, force: true);
    }

    private static void DisableUnderlayLayer(int instanceId)
    {
        if (!UnderlayLayers.TryGetValue(instanceId, out var state) || state == null)
            return;

        UnderlayLayers.Remove(instanceId);
        if (state.UnderlayObject != null)
        {
            UiHierarchyClassifier.UnregisterInternalLayer(state.UnderlayObject, state.UnderlayText);
            UnityEngine.Object.Destroy(state.UnderlayObject);
        }
    }

    private static void SyncUnderlayState(UnderlayLayerState state, bool force)
    {
        if (state == null)
            return;
        var source = state.SourceText;
        var underlayObject = state.UnderlayObject;
        var underlayText = state.UnderlayText;
        if (source == null || source.gameObject == null || underlayObject == null || underlayText == null)
            return;

        var parent = source.transform?.parent;
        if (parent == null)
        {
            if (underlayObject.activeSelf)
                underlayObject.SetActive(false);
            return;
        }

        var shouldBeActive = source.gameObject.activeInHierarchy && source.gameObject.activeSelf && source.enabled;
        var sourceRect = source.rectTransform;
        var anchoredPos = sourceRect != null ? sourceRect.anchoredPosition : Vector2.zero;
        var sizeDelta = sourceRect != null ? sourceRect.sizeDelta : Vector2.zero;
        var currentText = (_pendingTextOverride != null && source == _activePresentationText)
            ? _pendingTextOverride
            : (source.text ?? string.Empty);
        var currentAlpha = source.color.a;
        var currentFontSize = source.fontSize;
        var targetFont = state.Font ?? source.font;

        if (!force &&
            !state.Dirty &&
            state.LastActive == shouldBeActive &&
            (!shouldBeActive ||
             (underlayObject.activeSelf &&
              underlayText.font == targetFont &&
              underlayText.fontSharedMaterial == state.Material)) &&
            Mathf.Approximately(state.LastAlpha, currentAlpha) &&
            Mathf.Approximately(state.LastFontSize, currentFontSize) &&
            state.LastAnchoredPosition == anchoredPos &&
            state.LastSizeDelta == sizeDelta &&
            string.Equals(state.LastSourceText, currentText, StringComparison.Ordinal))
        {
            return;
        }

        var previousApplying = _applying;
        _applying = true;
        try
        {
            state.Dirty = false;
            state.LastActive = shouldBeActive;
            state.LastAlpha = currentAlpha;
            state.LastFontSize = currentFontSize;
            state.LastAnchoredPosition = anchoredPos;
            state.LastSizeDelta = sizeDelta;
            state.LastSourceText = currentText;

            var underlayTransform = underlayObject.transform;
            if (underlayTransform.parent != parent)
                underlayTransform.SetParent(parent, worldPositionStays: false);

            var mainSiblingIndex = source.transform.GetSiblingIndex();
            var underlaySiblingIndex = underlayTransform.GetSiblingIndex();
            var desiredIndex = underlaySiblingIndex < mainSiblingIndex ? mainSiblingIndex - 1 : mainSiblingIndex;
            if (desiredIndex >= 0 && underlaySiblingIndex != desiredIndex)
                underlayTransform.SetSiblingIndex(desiredIndex);

            var underlayRect = state.UnderlayRect;
            if (sourceRect != null && underlayRect != null)
            {
                underlayRect.anchorMin = sourceRect.anchorMin;
                underlayRect.anchorMax = sourceRect.anchorMax;
                underlayRect.pivot = sourceRect.pivot;
                underlayRect.sizeDelta = sizeDelta;
                underlayRect.localScale = sourceRect.localScale;
                underlayRect.localRotation = sourceRect.localRotation;

                var offsetScale = Mathf.Max(2f, currentFontSize * 0.16f);
                var pixelOffset = new Vector2(state.OffsetX * offsetScale, state.OffsetY * offsetScale);
                underlayRect.anchoredPosition = anchoredPos + pixelOffset;
            }

            // Activate the GameObject BEFORE assigning fontSharedMaterial so that
            // TextMeshProUGUI.Awake()/LoadFontAsset() runs first and cannot overwrite
            // our custom projection material back to the font asset default material.
            if (underlayObject.activeSelf != shouldBeActive)
                underlayObject.SetActive(shouldBeActive);
            if (underlayText.enabled != source.enabled)
                underlayText.enabled = source.enabled;

            if (targetFont != null && underlayText.font != targetFont)
                underlayText.font = targetFont;
            if (state.Material != null && underlayText.fontSharedMaterial != state.Material)
            {
                underlayText.fontSharedMaterial = state.Material;
                underlayText.UpdateMeshPadding();
            }

            var alphaColor = new Color(1f, 1f, 1f, currentAlpha);
            if (underlayText.color != alphaColor)
                underlayText.color = alphaColor;
            if (underlayText.enableVertexGradient)
                underlayText.enableVertexGradient = false;
            if (underlayText.raycastTarget)
                underlayText.raycastTarget = false;
            if (underlayText.maskable != source.maskable)
                underlayText.maskable = source.maskable;
            if (underlayText.richText != source.richText)
                underlayText.richText = source.richText;
            if (underlayText.enableAutoSizing != source.enableAutoSizing)
                underlayText.enableAutoSizing = source.enableAutoSizing;
            if (!Mathf.Approximately(underlayText.fontSize, currentFontSize))
                underlayText.fontSize = currentFontSize;
            if (!Mathf.Approximately(underlayText.fontSizeMin, source.fontSizeMin))
                underlayText.fontSizeMin = source.fontSizeMin;
            if (!Mathf.Approximately(underlayText.fontSizeMax, source.fontSizeMax))
                underlayText.fontSizeMax = source.fontSizeMax;
            if (underlayText.alignment != source.alignment)
                underlayText.alignment = source.alignment;
            if (underlayText.enableWordWrapping != source.enableWordWrapping)
                underlayText.enableWordWrapping = source.enableWordWrapping;
            if (underlayText.overflowMode != source.overflowMode)
                underlayText.overflowMode = source.overflowMode;
            if (!Mathf.Approximately(underlayText.lineSpacing, source.lineSpacing))
                underlayText.lineSpacing = source.lineSpacing;
            if (!Mathf.Approximately(underlayText.characterSpacing, source.characterSpacing))
                underlayText.characterSpacing = source.characterSpacing;
            if (!Mathf.Approximately(underlayText.paragraphSpacing, source.paragraphSpacing))
                underlayText.paragraphSpacing = source.paragraphSpacing;
            if (underlayText.margin != source.margin)
                underlayText.margin = source.margin;

            var sanitizedText = currentText.IndexOf("<color", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                currentText.IndexOf("</color", StringComparison.OrdinalIgnoreCase) >= 0
                ? ColorTagRegex.Replace(currentText, string.Empty)
                : currentText;
            if (!string.Equals(underlayText.text, sanitizedText, StringComparison.Ordinal))
                underlayText.text = sanitizedText;

            if (state.Material != null && underlayText.fontSharedMaterial != state.Material)
                underlayText.fontSharedMaterial = state.Material;
            underlayText.SetAllDirty();
        }
        finally
        {
            _applying = previousApplying;
        }
    }

    internal static bool HasUnderlay(int instanceId) => UnderlayLayers.ContainsKey(instanceId);

    internal static bool IsInstanceStyled(int instanceId) => InstanceStyleKeys.ContainsKey(instanceId);

    internal static bool IsInstanceStyled(TMP_Text text, int instanceId) =>
        text != null &&
        InstanceStyleKeys.ContainsKey(instanceId) &&
        TmpFontInstaller.IsModFont(text.font) &&
        IsModMaterial(text.fontSharedMaterial);

    internal static void NotifySelectableStateChanged(object instance)
    {
        if (_applying || UnderlayLayers.Count == 0 ||
            instance is not Selectable selectable || selectable == null ||
            selectable.gameObject == null || !selectable.gameObject.activeInHierarchy)
            return;

        SyncAllUnderlayLayers();
    }

    private static TMP_Text[] GetOrCollectSelectableTexts(Selectable selectable)
    {
        var selectableId = selectable.GetInstanceID();
        var transform = selectable.transform;
        var childCount = transform != null ? transform.childCount : 0;
        if (!SelectableTextsCache.TryGetValue(selectableId, out var cached) ||
            cached.ChildCount != childCount ||
            cached.Texts == null)
        {
            if (SelectableTextsCache.Count > 512)
                SelectableTextsCache.Clear();
            var il2cppArray = selectable.GetComponentsInChildren<TMP_Text>(true);
            var managedArray = new TMP_Text[il2cppArray.Length];
            for (var i = 0; i < il2cppArray.Length; i++)
                managedArray[i] = il2cppArray[i];
            cached = (childCount, managedArray);
            SelectableTextsCache[selectableId] = cached;
        }
        return cached.Texts;
    }

    internal static void ReleasePresentation(TMP_Text text)
    {
        if (text == null) return;
        var id = text.GetInstanceID();
        InstanceStyleKeys.Remove(id);
        InstanceAppliedRules.Remove(id);
        MaterialBaseFaceColors.Remove(id);
        OriginalGameMaterials.Remove(id);
        if (PreStyleVertexColors.TryGetValue(id, out var savedColor))
        {
            PreStyleVertexColors.Remove(id);
            text.color = savedColor;
        }
        if (UnderlayLayers.Count > 0)
            DisableUnderlayLayer(id);
    }

    internal static void SyncUnderlayLayer(TMP_Text text)
    {
        if (text == null || UnderlayLayers.Count == 0)
            return;
        if (UnderlayLayers.TryGetValue(text.GetInstanceID(), out var state))
            SyncUnderlayState(state, force: false);
    }

    internal static void PrepareUnderlayForTextChange(object instance)
    {
        if (UnderlayLayers.Count == 0)
            return;
        if (instance is TMP_Text text && text != null &&
            UnderlayLayers.TryGetValue(text.GetInstanceID(), out var state) &&
            state != null)
        {
            state.Dirty = true;
        }
    }

    internal static void SyncAllUnderlayLayers()
    {
        if (UnderlayLayers.Count == 0)
            return;

        UnderlayCleanupBuffer.Clear();
        foreach (var entry in UnderlayLayers)
        {
            var state = entry.Value;
            if (state == null || state.SourceText == null || state.SourceText.gameObject == null ||
                state.UnderlayObject == null || state.UnderlayText == null)
            {
                UnderlayCleanupBuffer.Add(entry.Key);
                continue;
            }
            SyncUnderlayState(state, force: false);
        }

        for (var i = 0; i < UnderlayCleanupBuffer.Count; i++)
            DisableUnderlayLayer(UnderlayCleanupBuffer[i]);
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
