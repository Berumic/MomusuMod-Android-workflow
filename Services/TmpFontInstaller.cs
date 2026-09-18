using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Patches;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Services;

/// <summary>Loads an optional TMP_FontAsset AssetBundle for explicit translated-text rendering.</summary>
public static class TmpFontInstaller
{
    private const float SubSkillMinimumLineSpacing = 8f;
    private const float DefaultUiTextOutlineWidth = 0.3f;
    private const float DefaultUiTextFaceDilate = 0.2f;
    private const float UiTextSoftOutlineWidth = 0.2f;
    private const float UiTextSoftOutlineSoftness = 0.06f;
    private const float EquipmentSubSkillFaceDilate = 0.2f;
    private const float R18StoryOutlineWidth = 0.15f;
    private static readonly Color32 SubSkillOutlineColor = new(0x54, 0x4A, 0x4A, 255);
    private static readonly Color32 UiTextOutlineColor = new(0x54, 0x4A, 0x4A, 255);
    private static readonly Regex SoundTag = new(@"</?sound(?:=[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LegacyChineseFontFamilies =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "SimHei", "SimSun"
    };
    private static AssetBundle _loadedBundle;
    private static TMP_FontAsset _loadedFont;
    private static readonly Dictionary<string, TMP_FontAsset> LoadedFonts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingStyleFonts =
        new(StringComparer.OrdinalIgnoreCase);
    internal static TMP_FontAsset LoadedFont => _loadedFont;
    [System.ThreadStatic]
    private static bool _applyingConfiguredUiStyle;
    private static Font _loadedLegacyFont;
    private static Material _r18StoryOutlineMaterial;
    private static Material _unitDetailOutlineMaterial;
    private static Material _unitDetailNoOutlineMaterial;
    private static bool _unitDetailMaterialsLogged;
    private static bool _replaceStoryNames = true;
    private static bool? _lastR18StoryOutlineState;
    private static readonly HashSet<int> StoryTextInstanceIds = new();
    private static readonly HashSet<int> SubSkillTextInstanceIds = new();
    private static readonly Dictionary<int, string> SubSkillTranslatedValues = new();
    private static readonly HashSet<int> UiTextInstanceIds = new();
    private static readonly Dictionary<int, string> UiTextTranslatedValues = new();
    private static readonly Dictionary<int, string> UiTextSourceValues = new();
    private static readonly Dictionary<int, TMP_Text> UiTextComponents = new();
    private static readonly Dictionary<int, Material> UiTextMaterials = new();
    private static readonly Dictionary<int, TextMeshProUGUI> UiTextSoftOutlineTexts = new();
    private static readonly Dictionary<int, Material> UiTextSoftOutlineMaterials = new();
    private static readonly Dictionary<int, TmpPresentationState> OriginalTranslatedTmpFonts = new();
    private static readonly HashSet<int> SubSkillFontLoggedIds = new();
    private static readonly Dictionary<int, TextMeshProUGUI> StoryTmpTexts = new();
    private static readonly Dictionary<int, Font> StoryOriginalFonts = new();
    private static readonly HashSet<int> StoryTmpFailures = new();

    public static void ApplyToStoryText(object component, string content)
    {
        if (component is TMP_Text text)
        {
            StoryTextInstanceIds.Add(text.GetInstanceID());
            ApplyTranslatedTmpText(text, content, useSubSkillPresentation: false);
            return;
        }

        if (component is Text legacyText)
        {
            StoryTextInstanceIds.Add(legacyText.GetInstanceID());
            // Cache the game's Japanese font before any translated line
            // replaces it. The same text component is reused by later lines.
            ApplyLegacyStoryFont(legacyText);
        }
    }

    public static void ApplyToCurrentText(object component)
    {
        // UiStyleManager deliberately assigns non-default fonts. The patched
        // TMP font setter re-enters here; applying the default font during
        // that callback would pair one font asset with another font's atlas.
        if (_applyingConfiguredUiStyle || UiStyleManager.IsApplying ||
            _loadedFont == null || component is not TMP_Text text)
            return;
        if (IsUiTextSoftOutlineLayer(text) || IsUiTranslationExcluded(text))
            return;

        if (IsHomeDeckExSkillSlotName(text.transform))
        {
            ApplyForcedTmpTextPresentation(text, useSubSkillPresentation: true);
            return;
        }

        if (IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text);
            return;
        }

        var instanceId = text.GetInstanceID();
        if (StoryTextInstanceIds.Contains(instanceId))
        {
            ApplyTo(text, text.text);
            return;
        }

        if (SubSkillTranslatedValues.TryGetValue(instanceId, out var translated) &&
            string.Equals(text.text, translated, StringComparison.Ordinal))
        {
            ApplyTo(text, text.text);
            ApplySubSkillPresentationIfNeeded(text);
            return;
        }

        if (UiTextTranslatedValues.TryGetValue(instanceId, out translated) &&
            string.Equals(text.text, translated, StringComparison.Ordinal))
        {
            ApplyTo(text, text.text);
            ApplyUiTextPresentation(text);
            return;
        }

        // The game may restore a recycled TMP component's original font
        // without assigning its text again. Do not depend on the previous
        // registration surviving that rebind; the translated value itself is
        // enough to restore the Alimama presentation.
        if (!IsSubSkillText(text) && IsUnderUiCanvas(text) &&
            Plugin.Translations != null &&
            Plugin.Translations.IsKnownUiTextTranslationValue(text.text))
        {
            UiTextInstanceIds.Add(instanceId);
            UiTextTranslatedValues[instanceId] = text.text;
            UiTextComponents[instanceId] = text;
            ApplyTo(text, text.text);
            ApplyUiTextPresentation(text);
            return;
        }

        SubSkillTextInstanceIds.Remove(instanceId);
        SubSkillTranslatedValues.Remove(instanceId);
        UiTextInstanceIds.Remove(instanceId);
        UiTextTranslatedValues.Remove(instanceId);
    }

    public static void ApplyToVisibleSubSkillText(TMP_Text text)
    {
        if (_loadedFont == null || text == null || text.transform == null ||
            !IsSubSkillScrollText(text.transform) || Plugin.Translations == null ||
            !Plugin.Translations.IsKnownSubSkillTranslationValue(text.text))
            return;

        var instanceId = text.GetInstanceID();
        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = text.text;
        ApplyTranslatedTmpText(text, text.text, useSubSkillPresentation: true);
        if (SubSkillFontLoggedIds.Add(instanceId))
            Plugin.Log.LogInfo($"Applied TMP font to sub-skill text: {GetHierarchyPath(text.transform)}");
    }

    /// <summary>
    /// Applies the Chinese TMP font only to the card just rebound by the
    /// game's AttachAbilityContent, avoiding a global visible-UI scan.
    /// </summary>
    public static void ApplyToSubSkillCard(object component)
    {
        if (_loadedFont == null || component is not Component root ||
            root.gameObject == null || !root.gameObject.activeInHierarchy)
            return;

        foreach (var text in root.GetComponentsInChildren<TMP_Text>())
            ApplyToVisibleSubSkillText(text);
    }

    /// <summary>
    /// A sub-skill description can be rendered in many windows, not only the
    /// attach-ability ScrollView. Mark the actual translation target so every
    /// TMP instance receives the same line spacing, outline, and weight.
    /// </summary>
    public static void ApplyToTranslatedSubSkillText(object component, string content)
    {
        if (_loadedFont == null || component is not TMP_Text text)
            return;

        var instanceId = text.GetInstanceID();
        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = content;
        ApplyTranslatedTmpText(text, content, useSubSkillPresentation: ShouldUseSubSkillPresentation(text));

        if (SubSkillFontLoggedIds.Add(instanceId))
            Plugin.Log.LogInfo($"Applied TMP presentation to translated sub-skill: {GetHierarchyPath(text.transform)}");
    }

    /// <summary>Marks a translated UICanvas label so a recycled TMP component keeps its Chinese font.</summary>
    public static void ApplyToTranslatedUiText(object component, string content, string source = null)
    {
        if (_loadedFont == null || component is not TMP_Text text)
            return;
        if (IsUiTextSoftOutlineLayer(text) || IsUiTranslationExcluded(text))
            return;

        var instanceId = text.GetInstanceID();
        UiTextInstanceIds.Add(instanceId);
        UiTextTranslatedValues[instanceId] = content;
        if (!string.IsNullOrEmpty(source))
            UiTextSourceValues[instanceId] = source;
        UiTextComponents[instanceId] = text;
        ApplyTranslatedTmpText(text, content, useSubSkillPresentation: false);
        if (content.Contains('\n') &&
            !Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing))
        {
            text.lineSpacing = SubSkillMinimumLineSpacing;
            text.SetAllDirty();
        }
        ApplyUiTextPresentation(text);
        // Unit-detail descriptions use a dedicated material and fixed line
        // spacing. The generic UI presentation above must not win when a
        // translated value is registered for F6/rebind handling.
        if (IsUnitDetailFontOverride(text))
            ApplyUnitDetailFont(text);
    }

    /// <summary>Refreshes visible manual UI translations after F6 reloads JSON tables.</summary>
    public static int ReloadVisibleUiTranslations(TranslationManager translations)
    {
        if (_loadedFont == null || translations == null)
            return 0;

        var refreshed = 0;
        var removedIds = new List<int>();
        var textsToRestore = new List<TMP_Text>();
        var processedIds = new HashSet<int>();
        foreach (var entry in UiTextComponents)
        {
            var text = entry.Value;
            if (text == null || text.gameObject == null)
            {
                removedIds.Add(entry.Key);
                continue;
            }
            if (IsUiTranslationExcluded(text))
            {
                textsToRestore.Add(text);
                processedIds.Add(entry.Key);
                continue;
            }
            if (!UiTextSourceValues.TryGetValue(entry.Key, out var source))
                continue;

            // Keep the hierarchy in the lookup so $paths categories and their
            // number/ex templates are applied during an F6 reload as well.
            if (!translations.TryTranslateUiText(source, GetHierarchyPath(text), out var translated))
            {
                text.text = source;
                textsToRestore.Add(text);
                continue;
            }

            if (!string.Equals(text.text, translated, StringComparison.Ordinal))
                text.text = translated;
            ApplyToTranslatedUiText(text, translated, source);
            refreshed++;
            processedIds.Add(entry.Key);
        }

        foreach (var instanceId in removedIds)
        {
            UiTextComponents.Remove(instanceId);
            UiTextSourceValues.Remove(instanceId);
            UiTextTranslatedValues.Remove(instanceId);
            UiTextInstanceIds.Remove(instanceId);
        }
        foreach (var text in textsToRestore)
            RestoreTranslatedUiText(text);

        // A newly opened detail panel may have been populated before its TMP
        // setter saw the final UICanvas hierarchy, so it is not in
        // UiTextComponents yet. Include every visible UICanvas TMP in F6's
        // retry pass so first-entry path translations can be applied directly.
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                !IsUnderUiCanvas(text) || IsUiTextSoftOutlineLayer(text))
                continue;

            var instanceId = text.GetInstanceID();
            if (!processedIds.Add(instanceId) || IsUiTranslationExcluded(text))
                continue;

            var source = text.text;
            if (string.IsNullOrEmpty(source))
                continue;
            if (!translations.TryTranslateUiText(source, GetHierarchyPath(text), out var translated))
                continue;

            if (!string.Equals(source, translated, System.StringComparison.Ordinal))
                text.text = translated;
            ApplyToTranslatedUiText(text, translated, source);
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>Reapplies configurable presentation settings to active sub-skill text.</summary>
    public static int RefreshVisibleSubSkillPresentation()
    {
        var refreshed = 0;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !SubSkillTextInstanceIds.Contains(text.GetInstanceID()))
                continue;

            ApplySubSkillPresentationIfNeeded(text);
            refreshed++;
        }

        return refreshed;
    }

    public static void RestoreTranslatedSubSkillText(object component)
    {
        if (component is not TMP_Text text)
            return;

        var instanceId = text.GetInstanceID();
        if (!SubSkillTextInstanceIds.Remove(instanceId))
            return;

        SubSkillTranslatedValues.Remove(instanceId);
        if (IsUnitDetailFontOverride(text))
        {
            OriginalTranslatedTmpFonts.Remove(instanceId);
            ApplyUnitDetailFont(text);
            return;
        }

        if (!OriginalTranslatedTmpFonts.TryGetValue(instanceId, out var original))
            return;

        OriginalTranslatedTmpFonts.Remove(instanceId);
        var changed = false;
        if (original.Font != null && text.font != original.Font)
        {
            text.font = original.Font;
            changed = true;
        }
        if (original.Material != null && text.fontSharedMaterial != original.Material)
        {
            text.fontSharedMaterial = original.Material;
            changed = true;
        }
        original.RestoreVertexColors(text);
        if (changed)
            text.SetAllDirty();
    }

    public static void RestoreTranslatedUiText(object component)
    {
        if (component is not TMP_Text text)
            return;

        var instanceId = text.GetInstanceID();
        if (!UiTextInstanceIds.Remove(instanceId))
            return;

        UiTextTranslatedValues.Remove(instanceId);
        UiTextSourceValues.Remove(instanceId);
        UiTextComponents.Remove(instanceId);
        ReleaseUiTextMaterial(instanceId);
        ReleaseUiTextSoftOutline(instanceId);

        if (IsUnitDetailFontOverride(text))
        {
            OriginalTranslatedTmpFonts.Remove(instanceId);
            ApplyUnitDetailFont(text);
            return;
        }

        if (SubSkillTextInstanceIds.Contains(instanceId) ||
            !OriginalTranslatedTmpFonts.TryGetValue(instanceId, out var original))
            return;

        OriginalTranslatedTmpFonts.Remove(instanceId);
        var changed = false;
        if (original.Font != null && text.font != original.Font)
        {
            text.font = original.Font;
            changed = true;
        }
        if (original.Material != null && text.fontSharedMaterial != original.Material)
        {
            text.fontSharedMaterial = original.Material;
            changed = true;
        }
        original.RestoreVertexColors(text);
        if (changed)
            text.SetAllDirty();
    }

    /// <summary>
    /// Runs after TMP has accepted a text assignment. Some skill windows set
    /// their default font while rebinding a card, after the translation prefix
    /// has already run. Match the final visible value instead of retaining a
    /// window-specific component marker.
    /// </summary>
    public static void ReapplyTranslatedSubSkillText(object component)
    {
        if (_loadedFont == null || component is not TMP_Text text)
            return;

        var instanceId = text.GetInstanceID();
        var visibleText = text.text;
        var isMappedValue = SubSkillTranslatedValues.TryGetValue(instanceId, out var mappedValue) &&
                            string.Equals(visibleText, mappedValue, StringComparison.Ordinal);
        var isKnownValue = Plugin.Translations != null &&
                           Plugin.Translations.IsKnownSubSkillTranslationValue(visibleText);
        if (!isMappedValue && !isKnownValue)
        {
            SubSkillTextInstanceIds.Remove(instanceId);
            SubSkillTranslatedValues.Remove(instanceId);
            return;
        }

        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = visibleText;
        ApplyTranslatedTmpText(text, visibleText, useSubSkillPresentation: ShouldUseSubSkillPresentation(text));
    }

    public static void ReapplyTranslatedText(object component)
    {
        if (_loadedFont == null || component is not TMP_Text text)
            return;
        if (IsUiTextSoftOutlineLayer(text))
            return;
        if (IsUiTranslationExcluded(text))
        {
            RestoreTranslatedUiText(text);
            return;
        }

        if (IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text);
            return;
        }

        var instanceId = text.GetInstanceID();
        var visibleText = text.text;
        var isMappedValue = UiTextTranslatedValues.TryGetValue(instanceId, out var mappedValue) &&
                            string.Equals(visibleText, mappedValue, StringComparison.Ordinal);
        var isKnownUiText = !IsSubSkillText(text) && IsUnderUiCanvas(text) &&
                            Plugin.Translations != null &&
                            Plugin.Translations.IsKnownUiTextTranslationValue(visibleText);
        if (isMappedValue | isKnownUiText)
        {
            UiTextInstanceIds.Add(instanceId);
            UiTextTranslatedValues[instanceId] = visibleText;
            UiTextComponents[instanceId] = text;
            ApplyTranslatedTmpText(text, visibleText, useSubSkillPresentation: false);
            ApplyUiTextPresentation(text);
        }
        else
        {
            RestoreTranslatedUiText(text);
            ReapplyTranslatedSubSkillText(text);

            // Path styles are presentation rules, not translation rules.
            // Apply them synchronously after TMP accepts a new value so a
            // recycled active slot does not wait for the 0.2-second scanner.
            if (IsUnderUiCanvas(text) && !IsSubSkillText(text))
            {
                UiStyleManager.Apply(
                    text,
                    GetUiTextOutlineWidth(),
                    GetUiTextFaceDilate(),
                    _loadedFont,
                    isTranslated: false);
            }
        }
    }

    public static bool IsUnderUiCanvas(object component)
    {
        if (component is not Component unityComponent || unityComponent.transform == null)
            return false;

        var isGameStartLoadingScreen = false;
        for (var current = unityComponent.transform; current != null; current = current.parent)
        {
            if (current.name.StartsWith("UICanvas", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(current.name, "GameStartLoadingScreen", StringComparison.OrdinalIgnoreCase))
                isGameStartLoadingScreen = true;
            if (isGameStartLoadingScreen &&
                string.Equals(current.name, "ExtraCanvas", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Applies a loaded path style immediately after UI hierarchy events.</summary>
    public static void ApplyConfiguredUiStyle(object component)
    {
        if (_applyingConfiguredUiStyle || _loadedFont == null || component is not TMP_Text text ||
            text.transform == null || !IsUnderUiCanvas(text) ||
            IsUiTextSoftOutlineLayer(text) || IsUiTranslationExcluded(text))
            return;

        var isTranslated = Plugin.Translations != null &&
                           Plugin.Translations.IsKnownUiTextTranslationValue(text.text);
        _applyingConfiguredUiStyle = true;
        try
        {
            UiStyleManager.Apply(
                text,
                GetUiTextOutlineWidth(),
                GetUiTextFaceDilate(),
                _loadedFont,
                isTranslated);
        }
        finally
        {
            _applyingConfiguredUiStyle = false;
        }
    }

    /// <summary>
    /// Actual sub-skill cards share UICanvas with normal UI labels, but must
    /// use the dedicated subskills table rather than generic UI fragments.
    /// </summary>
    public static bool IsSubSkillText(object component) =>
        component is Component unityComponent && unityComponent.transform != null &&
        IsSubSkillScrollText(unityComponent.transform);

    /// <summary>
    /// These character-detail descriptions use the UICanvas translation table.
    /// They can happen to share a value with a sub-skill entry, but applying
    /// both presentations to one reused TMP component corrupts its restore
    /// state when the selected unit changes.
    /// </summary>
    public static bool IsUnitDetailUiDescription(object component)
    {
        if (component is not Component unityComponent || unityComponent.transform == null)
            return false;

        var path = GetHierarchyPath(unityComponent.transform);
        return path.Contains("/Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
               (path.Contains("/ExSkillArea/ExSkillTextArea/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/StatusDialog/UnitTribeTextArea/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Unit-detail panels reuse their TMP renderers while rebinding a unit.
    /// One complete font asset and two stable presentation materials own this
    /// canvas, while decorative native titles retain the game's own styling.
    /// </summary>
    public static bool IsUnitDetailFontOverride(object component)
    {
        if (component is not Component unityComponent || unityComponent.transform == null)
            return false;

        var path = GetHierarchyPath(unityComponent.transform);
        return path.Contains("/UICanvasTopUnitDetail/", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyUnitDetailFont(TMP_Text text)
    {
        if (_loadedFont == null || _loadedFont.material == null || text == null ||
            !IsUnitDetailFontOverride(text))
            return;

        EnsureUnitDetailMaterials();
        var material = IsUnitDetailNoOutlineText(text)
            ? _unitDetailNoOutlineMaterial
            : _unitDetailOutlineMaterial;
        if (material == null)
            return;

        var changed = false;
        if (text.font != _loadedFont)
        {
            text.font = _loadedFont;
            changed = true;
        }
        if (text.fontSharedMaterial != material)
        {
            text.fontSharedMaterial = material;
            changed = true;
        }
        var requiresDescriptionSpacing = IsUnitDetailFixedSpacingText(text) ||
                                         (!string.IsNullOrEmpty(text.text) && text.text.Contains('\n'));
        if (requiresDescriptionSpacing &&
            !Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing))
        {
            text.lineSpacing = SubSkillMinimumLineSpacing;
            changed = true;
        }

        UiStyleManager.Apply(text, GetUiTextOutlineWidth(), GetUiTextFaceDilate(), _loadedFont,
            Plugin.Translations?.IsKnownUiTextTranslationValue(text.text) == true);
        if (changed)
            text.SetAllDirty();
    }

    /// <summary>Reapplies the shared character-detail style after F6 reloads its values.</summary>
    public static int RefreshVisibleUnitDetailPresentation()
    {
        if (_loadedFont == null || _loadedFont.material == null)
            return 0;

        EnsureUnitDetailMaterials();
        ConfigureUnitDetailMaterial(_unitDetailOutlineMaterial, disableOutline: false);
        ConfigureUnitDetailMaterial(_unitDetailNoOutlineMaterial, disableOutline: true);

        var refreshed = 0;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !IsUnitDetailFontOverride(text))
                continue;

            ApplyUnitDetailFont(text);
            refreshed++;
        }
        return refreshed;
    }

    private static void EnsureUnitDetailMaterials()
    {
        if (_loadedFont?.material == null)
            return;

        if (_unitDetailOutlineMaterial == null)
        {
            _unitDetailOutlineMaterial = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UnitDetailOutline"
            };
            ConfigureUnitDetailMaterial(_unitDetailOutlineMaterial, disableOutline: false);
        }
        if (_unitDetailNoOutlineMaterial == null)
        {
            _unitDetailNoOutlineMaterial = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UnitDetailNoOutline"
            };
            ConfigureUnitDetailMaterial(_unitDetailNoOutlineMaterial, disableOutline: true);
        }

        if (!_unitDetailMaterialsLogged && Plugin.Log != null)
        {
            Plugin.Log.LogInfo(
                $"Unit-detail TMP materials ready: font={_loadedFont.name}, " +
                "using the font asset's own atlases");
            _unitDetailMaterialsLogged = true;
        }
    }

    private static void ConfigureUnitDetailMaterial(Material material, bool disableOutline)
    {
        if (material == null)
            return;

        if (material.HasProperty("_OutlineColor") &&
            material.GetColor("_OutlineColor") != (Color)UiTextOutlineColor)
            material.SetColor("_OutlineColor", UiTextOutlineColor);
        var outlineWidth = disableOutline ? 0f : GetUiTextOutlineWidth();
        if (material.HasProperty("_OutlineWidth") &&
            !Mathf.Approximately(material.GetFloat("_OutlineWidth"), outlineWidth))
            material.SetFloat("_OutlineWidth", outlineWidth);
        var faceDilate = disableOutline ? 0f : GetUiTextFaceDilate();
        if (material.HasProperty("_FaceDilate") &&
            !Mathf.Approximately(material.GetFloat("_FaceDilate"), faceDilate))
            material.SetFloat("_FaceDilate", faceDilate);
        if (disableOutline && material.HasShaderKeyword("OUTLINE_ON"))
            material.DisableKeyword("OUTLINE_ON");
        else if (!disableOutline && !material.HasShaderKeyword("OUTLINE_ON"))
            material.EnableKeyword("OUTLINE_ON");
        if (material.HasShaderKeyword("UNDERLAY_ON"))
            material.DisableKeyword("UNDERLAY_ON");
    }

    public static bool IsUiTranslationExcluded(object component) => false;

    public static bool IsUiTextSoftOutlineLayer(object component) =>
        component is Component unityComponent && unityComponent.gameObject != null &&
        (unityComponent.gameObject.name.EndsWith("__MMTDSoftOutline", StringComparison.Ordinal) ||
         unityComponent.gameObject.name.EndsWith("__MMTDUnderlay", StringComparison.Ordinal));

    public static void ApplyToLegacyText(object component)
    {
        if (_loadedLegacyFont == null || component is not Text text ||
            !IsStoryMessageText(text) || Plugin.Translations == null ||
            (!Plugin.Translations.IsKnownTranslationValue(text.text) &&
             !Plugin.Translations.IsKnownStoryTranslationValue(text.text)))
            return;

        // Utage can assign its original font after writing a translated line.
        // Reassert the bundled legacy font only for an actual translated story
        // value, so normal Japanese UI and unmatched dialogue remain untouched.
        ApplyLegacyStoryFont(text);
    }

    public static void ReplaceKnownStoryTextWithTmp(Text legacyText, string content = null)
    {
        if (legacyText == null || !IsStoryMessageText(legacyText))
            return;

        var instanceId = legacyText.GetInstanceID();
        if (!_replaceStoryNames && IsStorySpeakerNameText(legacyText))
        {
            RestoreOriginalText(legacyText);
            return;
        }

        // Utage parses <speed> and <interval> on its original UGUI text
        // component. A Legacy Font retains that component and its typewriter
        // state instead of bypassing it with a TMP overlay.
        if (_loadedLegacyFont != null)
        {
            ApplyLegacyStoryFont(legacyText);
            RestoreOriginalText(legacyText);
            return;
        }

        if (_loadedFont == null)
            return;

        var textContent = content ?? legacyText.text;
        if (!Supports(textContent))
        {
            // Keep the game's original renderer for Japanese/unmapped names or log entries.
            if (StoryTmpTexts.TryGetValue(instanceId, out var unsupportedOverlay) && unsupportedOverlay != null)
                unsupportedOverlay.enabled = false;
            legacyText.enabled = true;
            return;
        }

        if (StoryTmpTexts.TryGetValue(instanceId, out var existingText) && existingText != null)
        {
            existingText.enabled = true;
            return;
        }

        try
        {
            var gameObject = legacyText.gameObject;
            if (gameObject == null)
                return;

            var overlayObject = gameObject.transform.Find("MonsterMusumeTDMod_StoryText")?.gameObject;
            if (overlayObject != null && overlayObject.GetComponent<RectTransform>() == null)
            {
                // A prior build created this as a regular Transform. It cannot host a UI Graphic.
                UnityEngine.Object.Destroy(overlayObject);
                overlayObject = null;
            }

            if (overlayObject == null)
            {
                overlayObject = new GameObject(
                    "MonsterMusumeTDMod_StoryText",
                    new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
                overlayObject.transform.SetParent(gameObject.transform, worldPositionStays: false);
            }

            var overlayRect = overlayObject.GetComponent<RectTransform>();
            if (overlayRect == null)
                throw new InvalidOperationException("Story TMP overlay has no RectTransform");
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.pivot = new Vector2(0.5f, 0.5f);
            overlayRect.anchoredPosition = Vector2.zero;
            overlayRect.sizeDelta = Vector2.zero;
            overlayRect.localScale = Vector3.one;
            overlayRect.SetAsLastSibling();

            var text = overlayObject.GetComponent<TextMeshProUGUI>();
            if (text == null)
                text = overlayObject.AddComponent<TextMeshProUGUI>();
            if (text == null)
                throw new InvalidOperationException("AddComponent<TextMeshProUGUI> returned null");

            text.font = _loadedFont;
            if (_loadedFont.material != null)
                text.fontSharedMaterial = _loadedFont.material;
            text.fontSize = legacyText.fontSize;
            text.color = legacyText.color;
            text.richText = legacyText.supportRichText;
            text.raycastTarget = legacyText.raycastTarget;
            text.maskable = legacyText.maskable;
            text.enableAutoSizing = false;
            text.enableWordWrapping = legacyText.horizontalOverflow != HorizontalWrapMode.Overflow;
            text.overflowMode = legacyText.verticalOverflow == VerticalWrapMode.Overflow
                ? TextOverflowModes.Overflow
                : TextOverflowModes.Truncate;
            text.alignment = ToTmpAlignment(legacyText.alignment);
            text.text = PrepareForTmp(textContent);
            ApplyR18StoryPresentation(text);
            text.enabled = true;
            text.SetAllDirty();

            StoryTmpTexts[instanceId] = text;
            legacyText.enabled = false;
            Plugin.Log.LogInfo($"Replaced story LegacyText with TMP overlay: {gameObject.name}");
        }
        catch (Exception exception)
        {
            if (StoryTmpFailures.Add(instanceId))
                Plugin.Log.LogWarning($"Failed to replace story LegacyText with TMP: {exception.Message}");
        }
    }

    public static void SyncKnownStoryText(Text legacyText, string content)
    {
        if (legacyText != null && _loadedLegacyFont != null && IsStoryMessageText(legacyText))
        {
            if (!_replaceStoryNames && IsStorySpeakerNameText(legacyText))
            {
                RestoreOriginalText(legacyText);
                return;
            }

            ApplyLegacyStoryFont(legacyText);
            RestoreOriginalText(legacyText);
            return;
        }

        if (legacyText == null || !StoryTmpTexts.TryGetValue(legacyText.GetInstanceID(), out var text) || text == null)
            return;

        if (!Supports(content))
        {
            text.enabled = false;
            legacyText.enabled = true;
            return;
        }

        var changed = false;
        var preparedContent = PrepareForTmp(content);
        if (!string.Equals(text.text, preparedContent, StringComparison.Ordinal))
        {
            text.text = preparedContent;
            changed = true;
        }
        ApplyR18StoryPresentation(text);
        if (!text.enabled)
        {
            text.enabled = true;
            changed = true;
        }
        if (legacyText.enabled)
            legacyText.enabled = false;
        if (changed)
            text.SetAllDirty();
    }

    public static void RestoreOriginalText(Text legacyText)
    {
        if (legacyText == null)
            return;

        if (StoryTmpTexts.TryGetValue(legacyText.GetInstanceID(), out var overlay) && overlay != null)
            overlay.enabled = false;
        legacyText.enabled = true;
    }

    public static void RefreshR18StoryPresentation(bool force = false)
    {
        var useWhiteOutline = Plugin.Settings.EnableR18WhiteTextOutline.Value &&
                              R18DialogueBackgroundController.IsR18SceneActive();
        if (!force && _lastR18StoryOutlineState == useWhiteOutline)
            return;
        _lastR18StoryOutlineState = useWhiteOutline;

        foreach (var text in StoryTmpTexts.Values)
        {
            if (text != null)
                ApplyR18StoryPresentation(text, useWhiteOutline);
        }
    }

    private static void ApplyR18StoryPresentation(
        TextMeshProUGUI text,
        bool? useWhiteOutlineOverride = null)
    {
        if (text == null || _loadedFont == null || _loadedFont.material == null)
            return;

        var useWhiteOutline = useWhiteOutlineOverride ??
                              (Plugin.Settings.EnableR18WhiteTextOutline.Value &&
                               R18DialogueBackgroundController.IsR18SceneActive());
        var material = useWhiteOutline ? GetR18StoryOutlineMaterial() : _loadedFont.material;
        if (material == null || text.fontSharedMaterial == material)
            return;

        text.fontSharedMaterial = material;
        text.SetAllDirty();
    }

    private static Material GetR18StoryOutlineMaterial()
    {
        if (_r18StoryOutlineMaterial != null)
            return _r18StoryOutlineMaterial;
        if (_loadedFont == null || _loadedFont.material == null)
            return null;

        var material = new Material(_loadedFont.material)
        {
            name = $"{_loadedFont.material.name}_R18WhiteOutline"
        };
        if (!material.HasProperty("_OutlineColor") || !material.HasProperty("_OutlineWidth"))
        {
            Plugin.Log.LogWarning("TMP font shader does not support outline properties; R18 white outline was skipped");
            UnityEngine.Object.Destroy(material);
            return null;
        }

        material.SetColor("_OutlineColor", Color.white);
        material.SetFloat("_OutlineWidth", R18StoryOutlineWidth);
        _r18StoryOutlineMaterial = material;
        Plugin.Log.LogInfo("R18 story white-outline material created");
        return _r18StoryOutlineMaterial;
    }

    public static void RestoreOriginalStoryPresentation(Text legacyText)
    {
        if (legacyText == null)
            return;

        RestoreOriginalText(legacyText);
        if (StoryOriginalFonts.TryGetValue(legacyText.GetInstanceID(), out var originalFont) &&
            originalFont != null && legacyText.font != originalFont)
            legacyText.font = originalFont;
    }

    private static void ApplyTranslatedTmpText(TMP_Text source, string content, bool useSubSkillPresentation)
    {
        if (source == null || string.IsNullOrEmpty(content))
            return;

        // TMP_Text can only use TMP_FontAsset. The bundled UnityEngine.Font is
        // reserved for the game's native UGUI story component; replacing a
        // TMP text with a UGUI overlay breaks masks and layout in several UI.
        // Translated and explicitly managed TMP slots must switch away from
        // the game's Japanese-only font even when HasCharacter() does not
        // search the bundled font's fallback tables. Otherwise the translated
        // value is accepted first and remains blank until a later UI scan.
        if (_loadedFont == null)
            return;

        ApplyForcedTmpTextPresentation(source, useSubSkillPresentation);
    }

    private static void ApplyForcedTmpTextPresentation(TMP_Text source, bool useSubSkillPresentation)
    {
        if (_loadedFont == null || source == null)
            return;

        ApplyTo(source);
        if (useSubSkillPresentation)
            ApplySubSkillPresentationIfNeeded(source);
        else
            ApplyTranslatedShopAndPopupLayout(source);
    }

    private static bool ApplyTo(TMP_Text text, string content)
    {
        if (!Supports(content))
            return false;
        return ApplyTo(text);
    }

    private static bool ApplyTo(TMP_Text text)
    {
        if (text == null)
            return false;

        var fontChanged = text.font != _loadedFont;
        var changed = fontChanged;
        if (fontChanged)
        {
            var instanceId = text.GetInstanceID();
            if (SubSkillTextInstanceIds.Contains(instanceId) || UiTextInstanceIds.Contains(instanceId))
                OriginalTranslatedTmpFonts.TryAdd(instanceId, new TmpPresentationState(text));
            text.font = _loadedFont;

            if (OriginalTranslatedTmpFonts.TryGetValue(instanceId, out var originalPresentation))
                originalPresentation.RestoreVertexColors(text);

        }

        // A reused TMP component can retain the Alimama font while the game
        // clears its material. Restore only a missing material here; valid
        // per-slot outline materials remain untouched.
        if (text.font == _loadedFont && _loadedFont.material != null &&
            (fontChanged || text.fontSharedMaterial == null))
        {
            text.fontSharedMaterial = _loadedFont.material;
            changed = true;
        }

        if (changed)
            text.SetAllDirty();
        return changed;
    }

    internal static bool TryGetOriginalFaceColor(TMP_Text text, out Color faceColor)
    {
        faceColor = default;
        return text != null &&
               OriginalTranslatedTmpFonts.TryGetValue(text.GetInstanceID(), out var original) &&
               original.TryGetFaceColor(out faceColor);
    }

    internal static TMP_FontAsset ResolveStyleFont(string name, TMP_FontAsset fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.Equals(name, "alimama", StringComparison.OrdinalIgnoreCase))
            return _loadedFont ?? fallback;
        if (LoadedFonts.TryGetValue(name.Trim(), out var font) && font != null)
            return font;

        if (MissingStyleFonts.Add(name))
            Plugin.Log?.LogWarning($"styles.json requested unknown TMP font: {name}");
        return null;
    }

    private readonly struct TmpPresentationState
    {
        public TmpPresentationState(TMP_Text text)
        {
            Font = text.font;
            Material = text.fontSharedMaterial;
            Color = text.color;
            EnableVertexGradient = text.enableVertexGradient;
            Gradient = text.colorGradient;
            GradientPreset = text.colorGradientPreset;
            HasFaceColor = Material != null && Material.HasProperty("_FaceColor");
            FaceColor = HasFaceColor ? Material.GetColor("_FaceColor") : default;
        }

        public TMP_FontAsset Font { get; }
        public Material Material { get; }
        private Color Color { get; }
        private bool EnableVertexGradient { get; }
        private VertexGradient Gradient { get; }
        private TMP_ColorGradient GradientPreset { get; }
        private bool HasFaceColor { get; }
        private Color FaceColor { get; }

        public void RestoreVertexColors(TMP_Text text)
        {
            text.color = Color;
            text.enableVertexGradient = EnableVertexGradient;
            text.colorGradientPreset = GradientPreset;
            text.colorGradient = Gradient;
        }

        public bool TryGetFaceColor(out Color faceColor)
        {
            faceColor = FaceColor;
            return HasFaceColor;
        }
    }

    private static void ApplySubSkillPresentationIfNeeded(TMP_Text text)
    {
        if (!ShouldUseSubSkillPresentation(text))
            return;

        if (IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text);
            return;
        }

        var changed = false;
        if (!Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing))
        {
            text.lineSpacing = SubSkillMinimumLineSpacing;
            changed = true;
        }

        var material = text.fontMaterial;
        if (material != null)
        {
            var materialChanged = false;
            var faceDilate = GetUiTextFaceDilate();
            var outlineWidth = GetUiTextOutlineWidth();
            if (material.HasProperty("_FaceDilate") &&
                !Mathf.Approximately(material.GetFloat("_FaceDilate"), faceDilate))
            {
                material.SetFloat("_FaceDilate", faceDilate);
                materialChanged = true;
            }
            if (material.HasProperty("_OutlineColor") &&
                material.GetColor("_OutlineColor") != (Color)SubSkillOutlineColor)
            {
                material.SetColor("_OutlineColor", SubSkillOutlineColor);
                materialChanged = true;
            }
            if (material.HasProperty("_OutlineWidth") &&
                !Mathf.Approximately(material.GetFloat("_OutlineWidth"), outlineWidth))
            {
                material.SetFloat("_OutlineWidth", outlineWidth);
                materialChanged = true;
            }
            if (!material.HasShaderKeyword("OUTLINE_ON"))
            {
                material.EnableKeyword("OUTLINE_ON");
                materialChanged = true;
            }
            if (materialChanged)
            {
                text.fontMaterial = material;
                changed = true;
            }
        }

        if ((Color)text.outlineColor != (Color)SubSkillOutlineColor)
        {
            text.outlineColor = SubSkillOutlineColor;
            changed = true;
        }
        var targetOutlineWidth = GetUiTextOutlineWidth();
        if (!Mathf.Approximately(text.outlineWidth, targetOutlineWidth))
        {
            text.outlineWidth = targetOutlineWidth;
            changed = true;
        }
        UiStyleManager.Apply(text, GetUiTextOutlineWidth(), GetUiTextFaceDilate(), _loadedFont,
            Plugin.Translations?.IsKnownTranslationValue(text.text) == true);
        if (changed)
            text.SetAllDirty();
    }

    private static void ApplyUiTextPresentation(TMP_Text text)
    {
        if (text == null || _loadedFont?.material == null)
            return;

        if (IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text);
            return;
        }

        var instanceId = text.GetInstanceID();
        var changed = false;
        if (!UiTextMaterials.TryGetValue(instanceId, out var material) || material == null)
        {
            material = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UiOutline_{instanceId}"
            };
            UiTextMaterials[instanceId] = material;
            changed = true;
        }

        var outlineColor = GetUiTextOutlineColor(text);
        if (material.HasProperty("_OutlineColor") && material.GetColor("_OutlineColor") != outlineColor)
        {
            material.SetColor("_OutlineColor", outlineColor);
            changed = true;
        }
        var outlineWidth = GetUiTextOutlineWidth();
        if (material.HasProperty("_OutlineWidth") &&
            !Mathf.Approximately(material.GetFloat("_OutlineWidth"), outlineWidth))
        {
            material.SetFloat("_OutlineWidth", outlineWidth);
            changed = true;
        }
        var faceDilate = GetUiTextFaceDilate();
        if (material.HasProperty("_FaceDilate") &&
            !Mathf.Approximately(material.GetFloat("_FaceDilate"), faceDilate))
        {
            material.SetFloat("_FaceDilate", faceDilate);
            changed = true;
        }
        if (!material.HasShaderKeyword("OUTLINE_ON"))
        {
            material.EnableKeyword("OUTLINE_ON");
            changed = true;
        }
        if (text.fontSharedMaterial != material)
        {
            text.fontSharedMaterial = material;
            changed = true;
        }
        UiStyleManager.Apply(text, GetUiTextOutlineWidth(), GetUiTextFaceDilate(), _loadedFont,
            Plugin.Translations?.IsKnownUiTextTranslationValue(text.text) == true);
        if (changed)
            text.SetAllDirty();
    }

    private static Color GetUiTextOutlineColor(TMP_Text text)
    {
        var path = GetHierarchyPath(text.transform);
        return path.Contains("/Common_SortFilter", StringComparison.OrdinalIgnoreCase)
            ? Color.white
            : UiTextOutlineColor;
    }

    /// <summary>
    /// This compact EX-skill description sits over a dark textured panel.
    /// Its dense Chinese lines become too heavy with the global UI outline,
    /// so retain alimama and face weight while disabling only the outline.
    /// </summary>
    private static bool IsUnitDetailNoOutlineText(TMP_Text text)
    {
        if (text?.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        return IsUnitDetailCompactDescription(path);
    }

    private static bool IsUnitDetailCompactDescription(TMP_Text text) =>
        text?.transform != null && IsUnitDetailCompactDescription(GetHierarchyPath(text.transform));

    public static bool ShouldRefreshUnitDetailDescription(TMP_Text text) =>
        text != null && IsUnitDetailFontOverride(text) && IsUnitDetailFixedSpacingText(text);

    private static bool IsUnitDetailFixedSpacingText(TMP_Text text)
    {
        if (text?.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        if (path.Contains("/UnitTribeTextArea/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/UnitClassTextArea/", StringComparison.OrdinalIgnoreCase))
            return true;
        return IsUnitDetailCompactDescription(path) ||
               (path.Contains("/UICanvasTopUnitDetail/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("/TextArea/Text", StringComparison.OrdinalIgnoreCase)) ||
               (path.Contains("/Unit_Detail_ExSkill", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(
                    "/WindowBase/ExSkillChangeView/SkillView1/ExSkillTextArea/TextArea/Text",
                    StringComparison.OrdinalIgnoreCase)) ||
               (path.Contains("/Bom_Detail", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(
                    "/Main/UI/Bom_Detail_StatusView/Skill1Btn/TextArea/Text",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnitDetailCompactDescription(string path)
    {
        var isExSkillDescription =
            path.Contains("/Unit_Detail_ExSkill", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/WindowBase/Rein/Desc", StringComparison.OrdinalIgnoreCase);
        var isUniqueWeaponAbility =
            path.Contains("/UniqueWeapon_CreateDetailView", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(
                "/UniqueWeapon_StatusView/LayoutGroup/Ability/AbilityText",
                StringComparison.OrdinalIgnoreCase);

        return isExSkillDescription || isUniqueWeaponAbility;
    }

    public static void ApplyHomeDeckSubSkillNamePresentation(TMP_Text text)
    {
        if (_loadedFont == null || text?.transform == null)
            return;

        if (IsHomeDeckExSkillSlotName(text.transform) ||
            IsQuestDungeonSkillName(text.transform))
        {
            // This compact slot can show either a Japanese or translated EX
            // skill name. Quest cards use the same compact presentation for
            // their featured and unique sub-skill names.
            ApplyForcedTmpTextPresentation(text, useSubSkillPresentation: true);
            return;
        }

        if (!IsHomeDeckSubSkillName(text.transform) || Plugin.Translations == null ||
            !Plugin.Translations.IsKnownSubSkillTranslationValue(text.text))
            return;

        var instanceId = text.GetInstanceID();
        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = text.text;
        ApplyTranslatedTmpText(text, text.text, useSubSkillPresentation: true);
    }

    private static float GetUiTextOutlineWidth() => Mathf.Clamp(
        Plugin.Settings?.UiTextOutlineWidth.Value ?? DefaultUiTextOutlineWidth,
        0f,
        1f);

    private static float GetUiTextFaceDilate() => Mathf.Clamp(
        Plugin.Settings?.UiTextFaceDilate.Value ?? DefaultUiTextFaceDilate,
        -1f,
        1f);

    private static void ApplyUiTextSoftOutline(TMP_Text text)
    {
        if (text is not TextMeshProUGUI || text.gameObject == null)
            return;

        var instanceId = text.GetInstanceID();
        if (!UiTextSoftOutlineTexts.TryGetValue(instanceId, out var outlineText) || outlineText == null)
        {
            var outlineObject = UnityEngine.Object.Instantiate(text.gameObject, text.transform.parent);
            outlineObject.name = $"{text.gameObject.name}__MMTDSoftOutline";
            outlineObject.transform.SetSiblingIndex(text.transform.GetSiblingIndex());
            outlineText = outlineObject.GetComponent<TextMeshProUGUI>();
            if (outlineText == null)
            {
                UnityEngine.Object.Destroy(outlineObject);
                return;
            }

            outlineText.raycastTarget = false;
            UiTextSoftOutlineTexts[instanceId] = outlineText;
        }

        if (!UiTextSoftOutlineMaterials.TryGetValue(instanceId, out var material) || material == null)
        {
            material = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UiSoftOutline_{instanceId}"
            };
            UiTextSoftOutlineMaterials[instanceId] = material;
        }

        // The background layer renders only the soft SDF outline. Its fill is
        // transparent and the foreground TMP text draws over the inner edge.
        var changed = false;
        var transparentFace = new Color(1f, 1f, 1f, 0f);
        if (material.HasProperty("_FaceColor") && material.GetColor("_FaceColor") != transparentFace)
        {
            material.SetColor("_FaceColor", new Color(1f, 1f, 1f, 0f));
            changed = true;
        }
        if (material.HasProperty("_OutlineColor") &&
            material.GetColor("_OutlineColor") != (Color)UiTextOutlineColor)
        {
            material.SetColor("_OutlineColor", UiTextOutlineColor);
            changed = true;
        }
        if (material.HasProperty("_OutlineWidth") &&
            !Mathf.Approximately(material.GetFloat("_OutlineWidth"), UiTextSoftOutlineWidth))
        {
            material.SetFloat("_OutlineWidth", UiTextSoftOutlineWidth);
            changed = true;
        }
        if (material.HasProperty("_OutlineSoftness") &&
            !Mathf.Approximately(material.GetFloat("_OutlineSoftness"), UiTextSoftOutlineSoftness))
        {
            material.SetFloat("_OutlineSoftness", UiTextSoftOutlineSoftness);
            changed = true;
        }
        if (!material.HasShaderKeyword("OUTLINE_ON"))
        {
            material.EnableKeyword("OUTLINE_ON");
            changed = true;
        }
        if (material.HasShaderKeyword("UNDERLAY_ON"))
        {
            material.DisableKeyword("UNDERLAY_ON");
            changed = true;
        }

        if (outlineText.font != text.font)
        {
            outlineText.font = text.font;
            changed = true;
        }
        if (outlineText.fontSharedMaterial != material)
        {
            outlineText.fontSharedMaterial = material;
            changed = true;
        }
        if (outlineText.color != Color.white)
        {
            outlineText.color = Color.white;
            changed = true;
        }
        if (!string.Equals(outlineText.text, text.text, StringComparison.Ordinal))
        {
            outlineText.text = text.text;
            changed = true;
        }
        var siblingIndex = text.transform.GetSiblingIndex();
        if (outlineText.transform.GetSiblingIndex() != siblingIndex)
        {
            outlineText.transform.SetSiblingIndex(siblingIndex);
            changed = true;
        }
        if (changed)
            outlineText.SetAllDirty();
    }

    private static void ReleaseUiTextSoftOutline(int instanceId)
    {
        if (UiTextSoftOutlineMaterials.Remove(instanceId, out var material) && material != null)
            UnityEngine.Object.Destroy(material);
        if (UiTextSoftOutlineTexts.Remove(instanceId, out var outlineText) && outlineText != null)
            UnityEngine.Object.Destroy(outlineText.gameObject);
    }

    private static void ReleaseUiTextMaterial(int instanceId)
    {
        if (UiTextMaterials.Remove(instanceId, out var material) && material != null)
            UnityEngine.Object.Destroy(material);
    }

    private static void ApplyTranslatedShopAndPopupLayout(TMP_Text text)
    {
        if (text == null || text.transform == null)
            return;

        var path = GetHierarchyPath(text.transform);
        var isPopupDescription =
            path.Contains("Common_SimpleDetailPopup", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/Text_Explanation", StringComparison.OrdinalIgnoreCase);
        var isShopItemName =
            path.Contains("Home_Shop", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Shop_SaleContent", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/Name", StringComparison.OrdinalIgnoreCase);
        if (!isPopupDescription && !isShopItemName)
            return;

        // The item-detail prefab uses spacing tuned for the original Japanese
        // face. Alimama's Chinese glyph metrics need additional separation in
        // translated descriptions and wrapped shop item names, but no
        // sub-skill outline.
        if (!Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing))
        {
            text.lineSpacing = SubSkillMinimumLineSpacing;
            text.SetAllDirty();
        }
    }

    private static bool Supports(string content)
    {
        if (_loadedFont == null || string.IsNullOrEmpty(content))
            return false;

        var insideTag = false;
        foreach (var character in content)
        {
            if (character == '<')
            {
                insideTag = true;
                continue;
            }
            if (insideTag)
            {
                if (character == '>')
                    insideTag = false;
                continue;
            }
            if (char.IsControl(character) || char.IsWhiteSpace(character))
                continue;
            if (!_loadedFont.HasCharacter(character))
                return false;
        }
        return true;
    }

    private static string PrepareForTmp(string content) =>
        string.IsNullOrEmpty(content) ? content : SoundTag.Replace(content, string.Empty);

    public static void TryInstall(string pluginRoot, ModConfig config)
    {
        _replaceStoryNames = config.TranslateNames.Value;
        var configuredPath = config.FontAssetPath.Value;
        if (string.IsNullOrWhiteSpace(configuredPath))
            return;

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(pluginRoot, configuredPath);
        if (!File.Exists(path))
        {
            Plugin.Log.LogWarning($"TMP font bundle not found: {path}");
            return;
        }

        try
        {
            _loadedBundle = AssetBundle.LoadFromFile(path);
            if (_loadedBundle == null)
                throw new InvalidOperationException("AssetBundle.LoadFromFile returned null");

            _loadedFont = null;
            TMP_FontAsset firstFont = null;
            LoadedFonts.Clear();
            MissingStyleFonts.Clear();
            foreach (var asset in _loadedBundle.LoadAllAssets<TMP_FontAsset>())
            {
                if (asset == null)
                    continue;
                asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                LoadedFonts[asset.name] = asset;
                firstFont ??= asset;
                if (_loadedFont == null &&
                    asset.name.Contains("alimama", StringComparison.OrdinalIgnoreCase))
                    _loadedFont = asset;
            }

            _loadedFont ??= firstFont;

            if (_loadedFont == null)
                throw new InvalidOperationException("No TMP_FontAsset was found in the configured AssetBundle");

            // The game runs Resources.UnloadUnusedAssets while changing into
            // an ADV scene. Keep bundle fonts alive before any text component
            // references them, otherwise the Legacy font compares equal to
            // null and the obsolete TMP story overlay is selected.
            _loadedFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            foreach (var asset in _loadedBundle.LoadAllAssets<Font>())
            {
                _loadedLegacyFont = asset;
                _loadedLegacyFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                break;
            }

            if (_loadedLegacyFont == null)
                TryCreateDynamicLegacyFont();

            RegisterGlobalFallbackFont();
            EnsureUnitDetailMaterials();

            Plugin.Log.LogInfo(
                $"Loaded {LoadedFonts.Count} TMP font asset(s) from {path}; default={_loadedFont.name}");
            Plugin.Log.LogInfo($"Available styles.json fonts: {string.Join(", ", LoadedFonts.Keys)}");
            if (_loadedLegacyFont != null)
                Plugin.Log.LogInfo($"Legacy UI font loaded from {path}: {_loadedLegacyFont.name}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"TMP font load failed: {ex.Message}");
        }
    }

    private static void RegisterGlobalFallbackFont()
    {
        if (_loadedFont == null)
            return;

        try
        {
            var fallbackFonts = TMP_Settings.fallbackFontAssets;
            if (fallbackFonts == null || fallbackFonts.Contains(_loadedFont))
                return;

            fallbackFonts.Add(_loadedFont);
            Plugin.Log.LogInfo($"TMP fallback font registered: {_loadedFont.name}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"TMP fallback font registration failed: {ex.Message}");
        }
    }

    private static void TryCreateDynamicLegacyFont()
    {
#if ANDROID
        Plugin.Log.LogWarning("Android requires a Legacy Font embedded in alimama-android; OS font creation is unsupported.");
        return;
#else
        foreach (var family in LegacyChineseFontFamilies)
        {
            try
            {
                var font = Font.CreateDynamicFontFromOSFont(family, 16);
                if (font == null)
                    continue;

                _loadedLegacyFont = font;
                _loadedLegacyFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Plugin.Log.LogInfo($"Created dynamic Legacy UI font: {family}");
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"Could not create Legacy UI font {family}: {ex.Message}");
            }
        }

        Plugin.Log.LogWarning("No Legacy UI font is available; story text will use the TMP overlay.");
#endif
    }

    private static void ApplyLegacyStoryFont(Text text)
    {
        StoryOriginalFonts.TryAdd(text.GetInstanceID(), text.font);
        if (_loadedLegacyFont != null && text.font != _loadedLegacyFont)
            text.font = _loadedLegacyFont;
    }

    public static bool IsStoryMessageText(Text text)
    {
        var transform = text.transform;
        if (transform == null)
            return false;
        var componentName = transform.name;

        var fullPath = GetHierarchyPath(transform);
        if (IsCurrentStoryDialogueText(text))
            return true;

        // Backlog entries are created under a separate runtime container. Avoid
        // replacing the BackLog button's short "Log" label itself.
        if (!fullPath.Contains("AdvEngine/", StringComparison.OrdinalIgnoreCase))
            return false;

        var isStoryHistoryText =
            fullPath.Contains("/BackLog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/Backlog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/FullScreenLog", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/History", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/Log", StringComparison.OrdinalIgnoreCase);
        var isStorySelectionText =
            fullPath.Contains("/Selection/Content/SelectionItem", StringComparison.OrdinalIgnoreCase);

        return (isStoryHistoryText || isStorySelectionText) &&
               !fullPath.Contains("/BackLogButton/", StringComparison.OrdinalIgnoreCase) &&
               !fullPath.Contains("/Skip/", StringComparison.OrdinalIgnoreCase) &&
               !fullPath.Contains("/Auto/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentStoryDialogueText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var componentName = text.transform.name;
        return (componentName == "MessageText" || componentName == "NameText") &&
               GetHierarchyPath(text.transform).Contains(
                   "AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/MessageTexts/",
                   StringComparison.Ordinal);
    }

    public static bool IsStorySpeakerNameText(Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var name = text.transform.name;
        if (!string.Equals(name, "NameText", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "CharacterName", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = GetHierarchyPath(text.transform);
        return path.Contains("AdvEngine/", StringComparison.OrdinalIgnoreCase) &&
               (path.Contains("/MessageWindow", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/BackLog", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/History", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Log", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var path = new List<string>();
        for (; transform != null; transform = transform.parent)
            path.Add(transform.name);
        path.Reverse();
        return string.Join("/", path);
    }

    public static string GetHierarchyPath(object component) =>
        component is Component unityComponent && unityComponent.transform != null
            ? GetHierarchyPath(unityComponent.transform)
            : string.Empty;

    private static bool IsSubSkillScrollText(Transform transform)
    {
        var path = GetHierarchyPath(transform);
        var nodeName = transform?.name ?? string.Empty;
        // Menu and quest pages use a compact Subskill(Clone) prefab rather
        // than the detail-window paths below. Its Name/Text children are still
        // auxiliary-skill content and must receive the same presentation.
        var isGenericSubSkillEntry =
            path.Contains("/Subskill", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(nodeName, "Name", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "Text", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "Description", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(nodeName, "SkillNameText", StringComparison.OrdinalIgnoreCase));

        // Runtime windows are instantiated as Unit_Detail_AttachAbility(Clone),
        // so matching the exact prefab path silently skipped every card.
        var isAttachAbilityList =
            path.Contains("Unit_Detail_AttachAbility", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/WindowBase/ScrollView/", StringComparison.OrdinalIgnoreCase);

        // The unit-detail SkillDialog uses a separate SubSkillTextArea prefab.
        // It is a sub-skill presentation too, even though it is not under the
        // AttachAbility WindowBase path used by the overview list.
        var isUnitDetailSkillDialog =
            path.Contains("Unit_Detail_Rework", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SkillDialog/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillTextArea", StringComparison.OrdinalIgnoreCase);

        var isSubSkillCombineText =
            path.Contains("SubSkillCombine_Popup", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/CombineIcon/Name", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/CombineIcon/Title", StringComparison.OrdinalIgnoreCase) ||
             (path.Contains("/MaterialIconContent/", StringComparison.OrdinalIgnoreCase) &&
              path.EndsWith("/GrayoutObj/Title", StringComparison.OrdinalIgnoreCase)));

        var isSubSkillRecipeName =
            path.Contains("/ScrollView_SubSkillRecipe/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/SubSkillRecipeBanner", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/SubSkillName_Text", StringComparison.OrdinalIgnoreCase);

        var isBattleResultAcquiredSkill =
            path.Contains("/UICanvasBattleResult/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Result_Summary_skip", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/AcquiredSkillSkip/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/Ef_GetSkill", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/Skill/Name", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/Skill/Text", StringComparison.OrdinalIgnoreCase));

        var isEquippedSubSkillAttach =
            IsEquippedSubSkillAttachPath(path) &&
            path.Contains("/AbilityRoot/Image", StringComparison.OrdinalIgnoreCase);

        return isGenericSubSkillEntry || isAttachAbilityList || isUnitDetailSkillDialog || isSubSkillCombineText ||
               isSubSkillRecipeName || isBattleResultAcquiredSkill || isEquippedSubSkillAttach;
    }

    private static bool IsHomeDeckSubSkillName(Transform transform)
    {
        var path = GetHierarchyPath(transform);
        return IsEquippedSubSkillAttachPath(path) &&
               path.Contains("/AbilityRoot/Image", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEquippedSubSkillAttachPath(string path) =>
        (path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase)) &&
        path.Contains("/AttachAbilityButton", StringComparison.OrdinalIgnoreCase);

    private static bool IsHomeDeckExSkillSlotName(Transform transform)
    {
        var path = GetHierarchyPath(transform);
        return path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) &&
               path.Contains("/ExSkillChangeButton/SkillName/", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith("/SkillNameText", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuestDungeonSkillName(Transform transform)
    {
        var path = GetHierarchyPath(transform);
        return path.Contains("Home_Quest", StringComparison.OrdinalIgnoreCase) &&
               path.Contains("/Quest_Dungeon", StringComparison.OrdinalIgnoreCase) &&
               path.Contains("/QuestContentDungeon", StringComparison.OrdinalIgnoreCase) &&
               (path.EndsWith("/Back/FeaturedSkillName", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/Back/UniqueSkillName", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldUseSubSkillPresentation(TMP_Text text)
    {
        if (text == null || text.transform == null)
            return false;

        var path = GetHierarchyPath(text.transform);
        // Only actual sub-skill browsing and equipped-skill slots need the
        // readability outline. Shop item cards can share a translation string
        // with a sub-skill, but must keep their ordinary visual treatment.
        return IsSubSkillScrollText(text.transform) ||
               IsQuestDungeonSkillName(text.transform) ||
               (IsEquippedSubSkillAttachPath(path) ||
                (path.Contains("Home_Deck", StringComparison.OrdinalIgnoreCase) &&
                 path.Contains("ExSkillChangeButton/SkillName/", StringComparison.OrdinalIgnoreCase)));
    }

    private static TextAnchor ToLegacyAlignment(TextAlignmentOptions alignment) => alignment switch
    {
        TextAlignmentOptions.TopLeft => TextAnchor.UpperLeft,
        TextAlignmentOptions.Top => TextAnchor.UpperCenter,
        TextAlignmentOptions.TopRight => TextAnchor.UpperRight,
        TextAlignmentOptions.Left => TextAnchor.MiddleLeft,
        TextAlignmentOptions.Center => TextAnchor.MiddleCenter,
        TextAlignmentOptions.Right => TextAnchor.MiddleRight,
        TextAlignmentOptions.BottomLeft => TextAnchor.LowerLeft,
        TextAlignmentOptions.Bottom => TextAnchor.LowerCenter,
        TextAlignmentOptions.BottomRight => TextAnchor.LowerRight,
        _ => TextAnchor.MiddleLeft
    };

    private static TextAlignmentOptions ToTmpAlignment(TextAnchor alignment) => alignment switch
    {
        TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
        TextAnchor.UpperCenter => TextAlignmentOptions.Top,
        TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
        TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
        TextAnchor.MiddleCenter => TextAlignmentOptions.Center,
        TextAnchor.MiddleRight => TextAlignmentOptions.Right,
        TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
        TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
        TextAnchor.LowerRight => TextAlignmentOptions.BottomRight,
        _ => TextAlignmentOptions.TopLeft
    };

}
