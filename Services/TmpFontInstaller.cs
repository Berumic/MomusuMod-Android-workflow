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

/// <summary>Loads an optional TMP_FontAsset AssetBundle and manages translated-text TMP/Legacy presentation state.</summary>
public static class TmpFontInstaller
{
    private const float SubSkillMinimumLineSpacing = 8f;
    private const float DefaultUiTextOutlineWidth = 0.3f;
    private const float DefaultUiTextFaceDilate = 0.35f;
    private const float R18StoryOutlineWidth = 0.15f;

    private static readonly Color32 SubSkillOutlineColor = new(0x54, 0x4A, 0x4A, 255);
    private static readonly Color32 UiTextOutlineColor = new(0x54, 0x4A, 0x4A, 255);
    private static readonly Regex SoundTag = new(@"</?sound(?:=[^>]*)?>", RegexOptions.IgnoreCase);
    private static readonly string[] LegacyChineseFontFamilies =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "SimHei", "SimSun"
    };

    private static AssetBundle _loadedBundle;
    private static TMP_FontAsset _loadedFont;
    private static readonly Dictionary<string, TMP_FontAsset> LoadedFonts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> LoadedFontIds = new();
    private static readonly HashSet<string> MissingStyleFonts =
        new(StringComparer.OrdinalIgnoreCase);
    internal static TMP_FontAsset LoadedFont => _loadedFont;

    [ThreadStatic]
    private static bool _applying;
    [ThreadStatic]
    private static bool _applyingConfiguredUiStyle;
    internal static bool IsApplying => _applying || _applyingConfiguredUiStyle;

    private static Font _loadedLegacyFont;
    private static Material _r18StoryOutlineMaterial;
    private static Material _unitDetailOutlineMaterial;
    private static Material _unitDetailNoOutlineMaterial;
    private static readonly Dictionary<char, bool> SupportedCharCache = new(1024);
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
    private static readonly Dictionary<int, TmpPresentationState> OriginalTranslatedTmpFonts = new();
    private static readonly HashSet<int> SubSkillFontLoggedIds = new();
    private static readonly Dictionary<int, TMP_Text[]> SubSkillCardTextsCache = new();
    private static readonly Dictionary<int, TextMeshProUGUI> StoryTmpTexts = new();
    private static readonly Dictionary<int, Font> StoryOriginalFonts = new();
    private static readonly HashSet<int> StoryTmpFailures = new();
    private static readonly List<int> PruneIdsBuffer = new(64);

    // Forwarding classifiers for API compatibility with existing callers
    public static string GetHierarchyPath(object component) =>
        UiHierarchyClassifier.GetHierarchyPath(component);

    public static bool IsUnderUiCanvas(object component) =>
        UiHierarchyClassifier.IsUnderUiCanvas(component);

    public static bool IsUiTranslationExcluded(object component) =>
        UiHierarchyClassifier.IsUiTranslationExcluded(component);

    public static bool IsUiTextSoftOutlineLayer(object component) =>
        UiHierarchyClassifier.IsUiTextSoftOutlineLayer(component);

    public static bool IsSubSkillText(object component) =>
        UiHierarchyClassifier.IsSubSkillText(component);

    public static bool IsUnitDetailUiDescription(object component) =>
        UiHierarchyClassifier.IsUnitDetailUiDescription(component);

    public static bool IsUnitDetailFontOverride(object component) =>
        UiHierarchyClassifier.IsUnitDetailFontOverride(component);

    public static bool ShouldRefreshUnitDetailDescription(TMP_Text text) =>
        text != null &&
        UiHierarchyClassifier.IsUnitDetailFontOverride(text) &&
        UiHierarchyClassifier.IsUnitDetailFixedSpacingText(text);

    public static bool IsStoryMessageText(Text text) =>
        UiHierarchyClassifier.IsStoryMessageText(text);

    public static bool IsCurrentStoryDialogueText(Text text) =>
        UiHierarchyClassifier.IsCurrentStoryDialogueText(text);

    public static bool IsStorySpeakerNameText(Text text) =>
        UiHierarchyClassifier.IsStorySpeakerNameText(text);

    internal static bool IsTrackedTranslatedComponent(int instanceId) =>
        instanceId != 0 &&
        (UiTextInstanceIds.Contains(instanceId) ||
         SubSkillTextInstanceIds.Contains(instanceId) ||
         StoryTextInstanceIds.Contains(instanceId));

    public static bool IsCurrentlyTranslatedComponent(Component component, string currentText)
    {
        if (component == null || string.IsNullOrEmpty(currentText))
            return false;
        var id = component.GetInstanceID();
        if (UiTextTranslatedValues.TryGetValue(id, out var uiTranslated) &&
            string.Equals(uiTranslated, currentText, StringComparison.Ordinal))
            return true;
        if (SubSkillTranslatedValues.TryGetValue(id, out var subSkillTranslated) &&
            string.Equals(subSkillTranslated, currentText, StringComparison.Ordinal))
            return true;
        return StoryTextInstanceIds.Contains(id);
    }

    private static bool IsTextTranslated(TMP_Text text, string pendingText = null)
    {
        if (text == null)
            return false;
        var current = pendingText ?? text.text;
        return IsCurrentlyTranslatedComponent(text, current) ||
               (Plugin.Translations != null && Plugin.Translations.IsKnownTranslationValue(current));
    }

    public static void PruneDestroyedInstances()
    {
        PruneIdsBuffer.Clear();
        foreach (var pair in UiTextComponents)
        {
            if (pair.Value == null || pair.Value.gameObject == null)
                PruneIdsBuffer.Add(pair.Key);
        }
        foreach (var id in PruneIdsBuffer)
        {
            UiTextComponents.Remove(id);
            UiTextTranslatedValues.Remove(id);
            UiTextSourceValues.Remove(id);
            UiTextInstanceIds.Remove(id);
            OriginalTranslatedTmpFonts.Remove(id);
        }

        PruneIdsBuffer.Clear();
        foreach (var pair in StoryTmpTexts)
        {
            if (pair.Value == null || pair.Value.gameObject == null)
                PruneIdsBuffer.Add(pair.Key);
        }
        foreach (var id in PruneIdsBuffer)
        {
            StoryTmpTexts.Remove(id);
            StoryOriginalFonts.Remove(id);
            StoryTextInstanceIds.Remove(id);
            StoryTmpFailures.Remove(id);
        }

        if (SubSkillCardTextsCache.Count > 256)
            SubSkillCardTextsCache.Clear();
    }

    public static void Unload()
    {
        if (_r18StoryOutlineMaterial != null)
        {
            UnityEngine.Object.Destroy(_r18StoryOutlineMaterial);
            _r18StoryOutlineMaterial = null;
        }
        if (_unitDetailOutlineMaterial != null)
        {
            UnityEngine.Object.Destroy(_unitDetailOutlineMaterial);
            _unitDetailOutlineMaterial = null;
        }
        if (_unitDetailNoOutlineMaterial != null)
        {
            UnityEngine.Object.Destroy(_unitDetailNoOutlineMaterial);
            _unitDetailNoOutlineMaterial = null;
        }
        if (_loadedBundle != null)
        {
            _loadedBundle.Unload(false);
            _loadedBundle = null;
        }
        _loadedFont = null;
        LoadedFonts.Clear();
        LoadedFontIds.Clear();
        MissingStyleFonts.Clear();
        SupportedCharCache.Clear();
    }

    /// <summary>Restores all active translated UI, sub-skill, and story components when F7 disables translation.</summary>
    public static int RestoreAllVisibleTranslations()
    {
        var restored = 0;
        var uiSnapshot = new List<KeyValuePair<int, TMP_Text>>(UiTextComponents);
        foreach (var entry in uiSnapshot)
        {
            var text = entry.Value;
            if (text == null || text.gameObject == null)
                continue;

            if (UiTextSourceValues.TryGetValue(entry.Key, out var source) &&
                !string.IsNullOrEmpty(source) &&
                !string.Equals(text.text, source, StringComparison.Ordinal))
            {
                text.text = source;
            }
            RestoreTranslatedUiText(text);
            restored++;
        }

        foreach (var text in FindActiveTmpTexts())
        {
            if (text == null || text.gameObject == null)
                continue;
            if (SubSkillTextInstanceIds.Contains(text.GetInstanceID()))
            {
                RestoreTranslatedSubSkillText(text);
                restored++;
            }
        }

#if ANDROID
        var legacyTexts = Resources.FindObjectsOfTypeAll<Text>();
#else
        var legacyTexts = UnityEngine.Object.FindObjectsByType<Text>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var legacyText in legacyTexts)
        {
            if (legacyText == null || legacyText.gameObject == null)
                continue;
            var id = legacyText.GetInstanceID();
            if (StoryTextInstanceIds.Contains(id) || StoryOriginalFonts.ContainsKey(id))
            {
                RestoreOriginalStoryPresentation(legacyText);
                restored++;
            }
        }

        return restored;
    }

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
        if (_applying || _applyingConfiguredUiStyle || UiStyleManager.IsApplying ||
            _loadedFont == null || component is not TMP_Text text)
            return;
        if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
            UiHierarchyClassifier.IsUiTranslationExcluded(text))
            return;

        if (UiHierarchyClassifier.IsHomeDeckExSkillSlotName(text.transform))
        {
            ApplyForcedTmpTextPresentation(text, useSubSkillPresentation: true);
            return;
        }

        if (UiHierarchyClassifier.IsUnitDetailFontOverride(text))
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
            if (UiHierarchyClassifier.ShouldUseSubSkillPresentation(text))
                ApplySubSkillPresentationIfNeeded(text, isTranslatedOverride: true, pendingText: translated);
            else if (UiHierarchyClassifier.IsUnderUiCanvas(text))
                ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: translated);
            return;
        }

        if (UiTextTranslatedValues.TryGetValue(instanceId, out translated) &&
            string.Equals(text.text, translated, StringComparison.Ordinal))
        {
            ApplyTo(text, text.text);
            ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: translated);
            return;
        }

        // The game may restore a recycled TMP component's original font
        // without assigning its text again. Do not depend on the previous
        // registration surviving that rebind; the translated value itself is
        // enough to restore the Alimama presentation.
        if (UiHierarchyClassifier.IsUnderUiCanvas(text) &&
            Plugin.Translations != null)
        {
            if (!UiHierarchyClassifier.IsSubSkillText(text) &&
                Plugin.Translations.IsKnownUiTextTranslationValue(text.text))
            {
                UiTextInstanceIds.Add(instanceId);
                UiTextTranslatedValues[instanceId] = text.text;
                UiTextComponents[instanceId] = text;
                ApplyTo(text, text.text);
                ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: text.text);
                return;
            }

            if (Plugin.Settings?.TranslateSubSkills.Value == true &&
                Plugin.Translations.IsKnownSubSkillTranslationValue(text.text))
            {
                SubSkillTextInstanceIds.Add(instanceId);
                SubSkillTranslatedValues[instanceId] = text.text;
                ApplyTo(text, text.text);
                if (UiHierarchyClassifier.ShouldUseSubSkillPresentation(text))
                    ApplySubSkillPresentationIfNeeded(text, isTranslatedOverride: true, pendingText: text.text);
                else
                    ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: text.text);
                return;
            }
        }

        SubSkillTextInstanceIds.Remove(instanceId);
        SubSkillTranslatedValues.Remove(instanceId);
        UiTextInstanceIds.Remove(instanceId);
        UiTextTranslatedValues.Remove(instanceId);
    }

    public static void ApplyToVisibleSubSkillText(TMP_Text text)
    {
        if (_loadedFont == null || text == null || text.transform == null ||
            !UiHierarchyClassifier.IsSubSkillScrollText(text.transform) ||
            Plugin.Translations == null ||
            !Plugin.Translations.IsKnownSubSkillTranslationValue(text.text))
            return;

        var instanceId = text.GetInstanceID();
        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = text.text;
        ApplyTranslatedTmpText(text, text.text, useSubSkillPresentation: true);
        if (SubSkillFontLoggedIds.Add(instanceId))
            Plugin.Log.LogInfo($"Applied TMP font to sub-skill text: {UiHierarchyClassifier.GetHierarchyPath(text.transform)}");
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

        var cardId = root.GetInstanceID();
        if (!SubSkillCardTextsCache.TryGetValue(cardId, out var texts) || texts == null)
        {
            var il2cppArray = root.GetComponentsInChildren<TMP_Text>(true);
            texts = new TMP_Text[il2cppArray.Length];
            for (var i = 0; i < il2cppArray.Length; i++)
                texts[i] = il2cppArray[i];
            SubSkillCardTextsCache[cardId] = texts;
        }

        for (var i = 0; i < texts.Length; i++)
        {
            var text = texts[i];
            if (text == null)
                continue;
            var textId = text.GetInstanceID();
            if (SubSkillTextInstanceIds.Contains(textId) && UiStyleManager.IsInstanceStyled(text, textId))
                continue;
            UiCanvasTranslationScanner.QueueActivationRefresh(text);
        }
    }

    /// <summary>
    /// A sub-skill description can be rendered in many windows, not only the
    /// attach-ability ScrollView. Mark the actual translation target so every
    /// TMP instance receives the same line spacing, outline, and weight.
    /// </summary>
    internal static bool TryFastCompleteTranslatedTmpText(
        object component,
        string translated,
        string source,
        bool isSubSkill)
    {
        if (_loadedFont == null || component is not TMP_Text text || text == null)
            return false;

        var instanceId = text.GetInstanceID();
        var flags = UiHierarchyClassifier.GetFlags(text);
        if ((flags & UiPathFlags.UnderUiCanvas) == 0 ||
            !UiStyleManager.IsInstanceStyled(text, instanceId) ||
            UiStyleManager.HasUnderlay(instanceId) ||
            !UiStyleManager.HasMatchingAppliedRule(text, instanceId) ||
            (flags & (UiPathFlags.UnitDetailFontOverride | UiPathFlags.CommonSortFilter)) != 0 ||
            ((flags & UiPathFlags.ShopOrPopupLayout) != 0 &&
             !Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing)))
            return false;

        if (isSubSkill)
        {
            if (!SubSkillTextInstanceIds.Contains(instanceId))
                return false;
            SubSkillTranslatedValues[instanceId] = translated;
            UiCanvasTranslationScanner.RecordFastProcessedText(instanceId, translated, text);
            return true;
        }

        if (!UiTextInstanceIds.Contains(instanceId))
            return false;
        if (translated.IndexOf('\n') >= 0 &&
            (!UiTextTranslatedValues.TryGetValue(instanceId, out var prevUi) || prevUi.IndexOf('\n') < 0))
            return false;
        UiTextTranslatedValues[instanceId] = translated;
        if (!string.IsNullOrEmpty(source))
            UiTextSourceValues[instanceId] = source;
        UiCanvasTranslationScanner.RecordFastProcessedText(instanceId, translated, text);
        return true;
    }

    public static void ApplyToTranslatedSubSkillText(object component, string content)
    {
        if (_loadedFont == null || component is not TMP_Text text || text == null)
            return;

        var instanceId = text.GetInstanceID();
        SubSkillTextInstanceIds.Add(instanceId);
        SubSkillTranslatedValues[instanceId] = content;
        CaptureOriginalPresentation(text);

        var prevApplying = _applying;
        _applying = true;
        UiStyleManager.BeginPresentation(text, content);
        try
        {
            var useSubSkillPresentation = UiHierarchyClassifier.ShouldUseSubSkillPresentation(text);
            ApplyTranslatedTmpText(
                text,
                content,
                useSubSkillPresentation: useSubSkillPresentation);
            if (!useSubSkillPresentation)
                ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: content);
        }
        finally
        {
            try { UiStyleManager.EndPresentation(); }
            finally { _applying = prevApplying; }
        }

        if (UiHierarchyClassifier.IsUnderUiCanvas(text))
        {
            UiCanvasTranslationScanner.RecordFastProcessedText(instanceId, content, text);
        }
        else
        {
            UiCanvasTranslationScanner.RecordObservedValue(instanceId, content);
            UiCanvasTranslationScanner.QueueActivationRefresh(text);
        }

        if (SubSkillFontLoggedIds.Add(instanceId))
            Plugin.Log.LogInfo($"Applied TMP presentation to translated sub-skill: {UiHierarchyClassifier.GetHierarchyPath(text.transform)}");
    }

    /// <summary>Marks a translated UICanvas label so a recycled TMP component keeps its Chinese font.</summary>
    public static void ApplyToTranslatedUiText(object component, string content, string source = null)
    {
        if (_loadedFont == null || component is not TMP_Text text || text == null)
            return;
        if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
            UiHierarchyClassifier.IsUiTranslationExcluded(text))
            return;

        var instanceId = text.GetInstanceID();
        UiTextInstanceIds.Add(instanceId);
        var repeatedValue = UiTextTranslatedValues.TryGetValue(instanceId, out var previousValue) &&
                            string.Equals(previousValue, content, StringComparison.Ordinal);
        UiTextTranslatedValues[instanceId] = content;
        // The queued pass sees the already translated text. Do not replace
        // the original Japanese key with that value: F6 needs the original.
        if (!string.IsNullOrEmpty(source) &&
            (!string.Equals(source, content, StringComparison.Ordinal) ||
             !repeatedValue || !UiTextSourceValues.ContainsKey(instanceId)))
            UiTextSourceValues[instanceId] = source;
        UiTextComponents[instanceId] = text;
        CaptureOriginalPresentation(text);

        var prevApplying = _applying;
        _applying = true;
        UiStyleManager.BeginPresentation(text, content);
        try
        {
            ApplyTranslatedTmpText(text, content, useSubSkillPresentation: false);
            if (content.Contains('\n') &&
                !Mathf.Approximately(text.lineSpacing, SubSkillMinimumLineSpacing))
            {
                text.lineSpacing = SubSkillMinimumLineSpacing;
                text.SetAllDirty();
            }
            ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: content);
        }
        finally
        {
            try { UiStyleManager.EndPresentation(); }
            finally { _applying = prevApplying; }
        }

        if (UiHierarchyClassifier.IsUnderUiCanvas(text))
        {
            UiCanvasTranslationScanner.RecordFastProcessedText(instanceId, content, text);
        }
        else
        {
            UiCanvasTranslationScanner.RecordObservedValue(instanceId, content);
            UiCanvasTranslationScanner.QueueActivationRefresh(text);
        }
    }

    internal static void ApplyUntranslatedCanvasPresentation(TMP_Text text, UiPathFlags flags, string pendingText)
    {
        if (text == null || _loadedFont == null ||
            (flags & (UiPathFlags.UnitDetailFontOverride |
                      UiPathFlags.HomeDeckExSkillSlotName |
                      UiPathFlags.QuestDungeonSkillName)) == 0)
        {
            return;
        }

        var prevApplying = _applying;
        _applying = true;
        try
        {
            if ((flags & UiPathFlags.UnitDetailFontOverride) != 0)
            {
                ApplyUnitDetailFont(text, isTranslatedOverride: false, pendingText: pendingText);
                return;
            }

            ApplyForcedTmpTextPresentation(text, useSubSkillPresentation: true, pendingText: pendingText);
        }
        finally
        {
            _applying = prevApplying;
        }
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
            if (UiHierarchyClassifier.IsUiTranslationExcluded(text))
            {
                textsToRestore.Add(text);
                processedIds.Add(entry.Key);
                continue;
            }
            if (!UiTextSourceValues.TryGetValue(entry.Key, out var source))
                continue;

            // Keep the hierarchy in the lookup so $paths categories and their
            // number/ex templates are applied during an F6 reload as well.
            if (!translations.TryTranslateUiText(source, UiHierarchyClassifier.GetHierarchyPath(text), out var translated))
            {
                text.text = source;
                textsToRestore.Add(text);
                continue;
            }

            if (!string.Equals(text.text, translated, StringComparison.Ordinal))
                text.text = translated;
            UiTextTranslatedValues[entry.Key] = translated;
            UiCanvasTranslationScanner.QueueActivationRefresh(text);
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
        foreach (var text in FindActiveTmpTexts())
        {
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsUnderUiCanvas(text) ||
                UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text))
                continue;

            var instanceId = text.GetInstanceID();
            if (!processedIds.Add(instanceId) || UiHierarchyClassifier.IsUiTranslationExcluded(text))
                continue;

            var source = text.text;
            if (string.IsNullOrEmpty(source))
                continue;
            if (!translations.TryTranslateUiText(source, UiHierarchyClassifier.GetHierarchyPath(text), out var translated))
                continue;

            if (!string.Equals(source, translated, StringComparison.Ordinal))
                text.text = translated;
            UiTextSourceValues[instanceId] = source;
            UiCanvasTranslationScanner.QueueActivationRefresh(text);
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>Reapplies configurable presentation settings to active sub-skill text.</summary>
    public static int RefreshVisibleSubSkillPresentation()
    {
        var refreshed = 0;
        foreach (var text in FindActiveTmpTexts())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !SubSkillTextInstanceIds.Contains(text.GetInstanceID()))
                continue;

            UiCanvasTranslationScanner.QueueActivationRefresh(text);
            refreshed++;
        }

        return refreshed;
    }

    public static void RestoreTranslatedSubSkillText(object component)
    {
        if (component is not TMP_Text text || text == null)
            return;

        var instanceId = text.GetInstanceID();
        if (!SubSkillTextInstanceIds.Contains(instanceId))
            return;
        SubSkillTextInstanceIds.Remove(instanceId);

        var prevApplying = _applying;
        _applying = true;
        try
        {
            UiStyleManager.ReleasePresentation(text);
            SubSkillTranslatedValues.Remove(instanceId);
            if (UiHierarchyClassifier.IsUnitDetailFontOverride(text))
            {
                OriginalTranslatedTmpFonts.Remove(instanceId);
                ApplyUnitDetailFont(text, isTranslatedOverride: false);
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
                text.UpdateMeshPadding();
                changed = true;
            }
            original.RestoreVertexColors(text);
            if (changed)
                text.SetAllDirty();
        }
        finally
        {
            _applying = prevApplying;
        }
    }

    public static void RestoreTranslatedUiText(object component)
    {
        if (component is not TMP_Text text || text == null)
            return;

        var instanceId = text.GetInstanceID();
        if (!UiTextInstanceIds.Contains(instanceId))
            return;
        UiTextInstanceIds.Remove(instanceId);

        var prevApplying = _applying;
        _applying = true;
        try
        {
            UiStyleManager.ReleasePresentation(text);
            UiTextTranslatedValues.Remove(instanceId);
            UiTextSourceValues.Remove(instanceId);
            UiTextComponents.Remove(instanceId);

            if (UiHierarchyClassifier.IsUnitDetailFontOverride(text))
            {
                OriginalTranslatedTmpFonts.Remove(instanceId);
                ApplyUnitDetailFont(text, isTranslatedOverride: false);
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
                text.UpdateMeshPadding();
                changed = true;
            }
            original.RestoreVertexColors(text);
            if (changed)
                text.SetAllDirty();
        }
        finally
        {
            _applying = prevApplying;
        }
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
        var useSubSkillPresentation = UiHierarchyClassifier.ShouldUseSubSkillPresentation(text);
        ApplyTranslatedTmpText(
            text,
            visibleText,
            useSubSkillPresentation: useSubSkillPresentation);
        if (!useSubSkillPresentation && UiHierarchyClassifier.IsUnderUiCanvas(text))
            ApplyUiTextPresentation(text, isTranslatedOverride: true, pendingText: visibleText);
    }

    /// <summary>Applies a loaded path style immediately after UI hierarchy events.</summary>
    public static void ApplyConfiguredUiStyle(
        object component,
        bool? isTranslatedOverride = null,
        string pendingText = null)
    {
        if (_applyingConfiguredUiStyle || _loadedFont == null || component is not TMP_Text text ||
            text.transform == null || !UiHierarchyClassifier.IsUnderUiCanvas(text) ||
            UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
            UiHierarchyClassifier.IsUiTranslationExcluded(text))
            return;

        var isTranslated = isTranslatedOverride ?? IsTextTranslated(text, pendingText);
        _applyingConfiguredUiStyle = true;
        try
        {
            UiStyleManager.Apply(
                text,
                GetUiTextOutlineWidth(),
                GetUiTextFaceDilate(),
                _loadedFont,
                isTranslated,
                pendingText);
        }
        finally
        {
            _applyingConfiguredUiStyle = false;
        }
    }

    public static void ApplyUnitDetailFont(
        TMP_Text text,
        bool? isTranslatedOverride = null,
        string pendingText = null)
    {
        if (_loadedFont == null || _loadedFont.material == null || text == null ||
            !UiHierarchyClassifier.IsUnitDetailFontOverride(text))
            return;

        CaptureOriginalPresentation(text);

        var currentText = pendingText ?? text.text;
        var compact = UiHierarchyClassifier.IsUnitDetailNoOutlineText(text);
        var requiresSpacing = UiHierarchyClassifier.IsUnitDetailFixedSpacingText(text) ||
                              (!string.IsNullOrEmpty(currentText) && currentText.Contains('\n'));
        var width = compact ? 0f : GetUiTextOutlineWidth();
        var dilate = compact ? 0f : GetUiTextFaceDilate();
        var spacing = requiresSpacing ? (float?)SubSkillMinimumLineSpacing : null;

        if (UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                UiTextOutlineColor,
                spacing,
                !compact))
            return;

        EnsureUnitDetailMaterials();
        var isTranslated = isTranslatedOverride ?? IsTextTranslated(text, currentText);
        UiStyleManager.BeginPresentation(text, currentText);
        try
        {
            UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                UiTextOutlineColor,
                spacing,
                !compact);
            UiStyleManager.Apply(
                text,
                width,
                dilate,
                _loadedFont,
                isTranslated,
                currentText);
        }
        finally
        {
            UiStyleManager.EndPresentation();
        }
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
        foreach (var text in FindActiveTmpTexts())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsUnitDetailFontOverride(text))
                continue;

            UiCanvasTranslationScanner.QueueActivationRefresh(text);
            refreshed++;
        }
        return refreshed;
    }

    private static List<TMP_Text> FindActiveTmpTexts()
    {
        var buffer = new List<TMP_Text>(256);
        UiHierarchyClassifier.CollectUiCanvasTexts(buffer);
        return buffer;
    }

    private static void EnsureUnitDetailMaterials()
    {
        if (_loadedFont?.material == null)
            return;

        UiStyleManager.RegisterModMaterial(_loadedFont.material);

        if (_unitDetailOutlineMaterial == null)
        {
            _unitDetailOutlineMaterial = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UnitDetailOutline"
            };
            UiStyleManager.RegisterModMaterial(_unitDetailOutlineMaterial);
            ConfigureUnitDetailMaterial(_unitDetailOutlineMaterial, disableOutline: false);
        }
        if (_unitDetailNoOutlineMaterial == null)
        {
            _unitDetailNoOutlineMaterial = new Material(_loadedFont.material)
            {
                name = $"{_loadedFont.material.name}_UnitDetailNoOutline"
            };
            UiStyleManager.RegisterModMaterial(_unitDetailNoOutlineMaterial);
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

    public static void ApplyToLegacyText(object component)
    {
        if (_loadedLegacyFont == null || component is not Text text ||
            !UiHierarchyClassifier.IsStoryMessageText(text) || Plugin.Translations == null ||
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
        if (legacyText == null || !UiHierarchyClassifier.IsStoryMessageText(legacyText))
            return;

        var instanceId = legacyText.GetInstanceID();
        // UTAGE owns the reveal state of the live dialogue. A separate TMP
        // renderer cannot reproduce it from Text.text (which is the full line).
        if (UiHierarchyClassifier.IsCurrentStoryDialogueText(legacyText))
        {
            ApplyLegacyStoryFont(legacyText);
            RestoreOriginalText(legacyText);
            return;
        }
        if (!_replaceStoryNames && UiHierarchyClassifier.IsStorySpeakerNameText(legacyText))
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
        if (legacyText != null && UiHierarchyClassifier.IsCurrentStoryDialogueText(legacyText))
        {
            ApplyLegacyStoryFont(legacyText);
            RestoreOriginalText(legacyText);
            return;
        }
        if (legacyText != null && _loadedLegacyFont != null && UiHierarchyClassifier.IsStoryMessageText(legacyText))
        {
            if (!_replaceStoryNames && UiHierarchyClassifier.IsStorySpeakerNameText(legacyText))
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
        var orphan = legacyText.transform.Find("MonsterMusumeTDMod_StoryText");
        if (orphan != null)
            orphan.gameObject.SetActive(false);
        legacyText.enabled = true;
    }

    public static void RefreshR18StoryPresentation(bool force = false)
    {
        var useWhiteOutline = Plugin.Settings.EnableR18WhiteTextOutline.Value &&
                              R18DialogueBackgroundController.IsR18SceneActive();
        var weightChanged = useWhiteOutline && _r18StoryOutlineMaterial != null &&
            _r18StoryOutlineMaterial.HasProperty("_FaceDilate") &&
            !Mathf.Approximately(_r18StoryOutlineMaterial.GetFloat("_FaceDilate"), GetUiTextFaceDilate());
        if (!force && !weightChanged && _lastR18StoryOutlineState == useWhiteOutline)
            return;
        _lastR18StoryOutlineState = useWhiteOutline;

        foreach (var text in StoryTmpTexts.Values)
        {
            if (text != null)
            {
                ApplyR18StoryPresentation(text, useWhiteOutline);
                if (weightChanged)
                {
                    text.UpdateMeshPadding();
                    text.SetAllDirty();
                }
            }
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
        text.UpdateMeshPadding();
        text.SetAllDirty();
    }

    private static Material GetR18StoryOutlineMaterial()
    {
        if (_r18StoryOutlineMaterial != null)
        {
            if (_r18StoryOutlineMaterial.HasProperty("_FaceDilate") &&
                !Mathf.Approximately(_r18StoryOutlineMaterial.GetFloat("_FaceDilate"), GetUiTextFaceDilate()))
                _r18StoryOutlineMaterial.SetFloat("_FaceDilate", GetUiTextFaceDilate());
            return _r18StoryOutlineMaterial;
        }
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

        UiStyleManager.RegisterModMaterial(material);
        material.SetColor("_OutlineColor", Color.white);
        material.SetFloat("_OutlineWidth", R18StoryOutlineWidth);
        if (material.HasProperty("_FaceDilate"))
            material.SetFloat("_FaceDilate", GetUiTextFaceDilate());
        if (!material.HasShaderKeyword("OUTLINE_ON"))
            material.EnableKeyword("OUTLINE_ON");
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

    internal static bool IsModFont(TMP_FontAsset font) =>
        font != null && (font == _loadedFont || LoadedFontIds.Contains(font.GetInstanceID()));

    internal static void CaptureOriginalPresentation(TMP_Text text)
    {
        if (text == null)
            return;

        var id = text.GetInstanceID();
        if (!OriginalTranslatedTmpFonts.ContainsKey(id))
        {
            UiStyleManager.ObserveGameMaterial(text);
            OriginalTranslatedTmpFonts[id] = new TmpPresentationState(text);
            return;
        }

        if (text.font != null && !IsModFont(text.font))
        {
            UiStyleManager.ObserveGameMaterial(text);
            OriginalTranslatedTmpFonts[id] = new TmpPresentationState(text);
        }
    }

    private static void ApplyTranslatedTmpText(TMP_Text source, string content, bool useSubSkillPresentation)
    {
        if (source == null || string.IsNullOrEmpty(content) || _loadedFont == null)
            return;

        ApplyForcedTmpTextPresentation(source, useSubSkillPresentation, content);
    }

    private static void ApplyForcedTmpTextPresentation(
        TMP_Text source,
        bool useSubSkillPresentation,
        string pendingText = null)
    {
        if (_loadedFont == null || source == null)
            return;

        var prevApplying = _applying;
        _applying = true;
        try
        {
            ApplyTo(source);
            if (useSubSkillPresentation)
                ApplySubSkillPresentationIfNeeded(source, isTranslatedOverride: true, pendingText: pendingText);
            else
                ApplyTranslatedShopAndPopupLayout(source);
        }
        finally
        {
            _applying = prevApplying;
        }
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

        CaptureOriginalPresentation(text);

        if (UiStyleManager.IsCollecting(text))
        {
            UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                GetUiTextOutlineWidth(),
                GetUiTextFaceDilate(),
                GetUiTextOutlineColor(text),
                null,
                true);
            return false;
        }

        var prevApplying = _applying;
        _applying = true;
        try
        {
            var fontChanged = text.font != _loadedFont;
            var changed = fontChanged;
            if (fontChanged)
            {
                var currentPresentation = new TmpPresentationState(text);
                text.font = _loadedFont;
                currentPresentation.RestoreVertexColors(text);
            }

            // A reused TMP component can retain the Alimama font while the game
            // clears its material. Restore only a missing material here; valid
            // per-slot outline materials remain untouched.
            if (text.font == _loadedFont && _loadedFont.material != null &&
                (fontChanged || text.fontSharedMaterial == null))
            {
                text.fontSharedMaterial = _loadedFont.material;
                text.UpdateMeshPadding();
                changed = true;
            }

            if (changed)
                text.SetAllDirty();
            return changed;
        }
        finally
        {
            _applying = prevApplying;
        }
    }

    internal static bool TryGetOriginalFaceColor(TMP_Text text, out Color faceColor)
    {
        faceColor = default;
        return text != null &&
               OriginalTranslatedTmpFonts.TryGetValue(text.GetInstanceID(), out var original) &&
               original.TryGetFaceColor(out faceColor);
    }

    internal static TMP_FontAsset ResolveStyleFont(string name, TMP_FontAsset fallback, bool isTranslated = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return isTranslated ? (_loadedFont ?? fallback) : null;
        if (string.Equals(name, "alimama", StringComparison.OrdinalIgnoreCase))
            return _loadedFont ?? fallback;
        if (LoadedFonts.TryGetValue(name.Trim(), out var font) && font != null)
            return font;

        if (MissingStyleFonts.Add(name))
            Plugin.Log?.LogWarning($"styles.json requested unknown TMP font: {name}; falling back to default TMP font");
        return _loadedFont ?? fallback;
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
            HasFaceColor = UiStyleManager.TryGetMaterialFaceColor(Material, out var faceColor);
            FaceColor = faceColor;
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
            if (UiStyleManager.TryGetMaterialFaceColor(Material, out faceColor))
                return true;
            faceColor = FaceColor;
            return HasFaceColor;
        }
    }

    private static void ApplySubSkillPresentationIfNeeded(
        TMP_Text text,
        bool? isTranslatedOverride = null,
        string pendingText = null)
    {
        if (!UiHierarchyClassifier.ShouldUseSubSkillPresentation(text))
            return;

        if (UiHierarchyClassifier.IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text, isTranslatedOverride, pendingText);
            return;
        }

        var width = GetUiTextOutlineWidth();
        var dilate = GetUiTextFaceDilate();
        if (UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                SubSkillOutlineColor,
                SubSkillMinimumLineSpacing))
            return;

        var isTranslated = isTranslatedOverride ?? IsTextTranslated(text, pendingText);
        UiStyleManager.BeginPresentation(text, pendingText);
        try
        {
            UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                SubSkillOutlineColor,
                SubSkillMinimumLineSpacing);
            UiStyleManager.Apply(
                text,
                width,
                dilate,
                _loadedFont,
                isTranslated,
                pendingText);
        }
        finally
        {
            UiStyleManager.EndPresentation();
        }
    }

    private static void ApplyUiTextPresentation(
        TMP_Text text,
        bool? isTranslatedOverride = null,
        string pendingText = null)
    {
        if (text == null || _loadedFont?.material == null)
            return;

        if (UiHierarchyClassifier.IsUnitDetailFontOverride(text))
        {
            ApplyUnitDetailFont(text, isTranslatedOverride, pendingText);
            return;
        }

        var width = GetUiTextOutlineWidth();
        var dilate = GetUiTextFaceDilate();
        var outlineColor = GetUiTextOutlineColor(text);
        if (UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                outlineColor,
                null))
            return;

        var isTranslated = isTranslatedOverride ?? IsTextTranslated(text, pendingText);
        UiStyleManager.BeginPresentation(text, pendingText);
        try
        {
            UiStyleManager.SubmitBaseline(
                text,
                _loadedFont,
                width,
                dilate,
                outlineColor,
                null);
            UiStyleManager.Apply(
                text,
                width,
                dilate,
                _loadedFont,
                isTranslated,
                pendingText);
        }
        finally
        {
            UiStyleManager.EndPresentation();
        }
    }

    private static Color GetUiTextOutlineColor(TMP_Text text) =>
        UiHierarchyClassifier.IsCommonSortFilterText(text)
            ? Color.white
            : UiTextOutlineColor;

    public static void ApplyHomeDeckSubSkillNamePresentation(TMP_Text text)
    {
        if (_loadedFont == null || text?.transform == null)
            return;

        if (UiHierarchyClassifier.IsHomeDeckExSkillSlotName(text.transform) ||
            UiHierarchyClassifier.IsQuestDungeonSkillName(text.transform))
        {
            ApplyForcedTmpTextPresentation(text, useSubSkillPresentation: true);
            return;
        }

        if (!UiHierarchyClassifier.IsHomeDeckSubSkillName(text.transform) ||
            Plugin.Translations == null ||
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

    private static void ApplyTranslatedShopAndPopupLayout(TMP_Text text)
    {
        if (!UiHierarchyClassifier.IsShopOrPopupLayoutText(text))
            return;

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
            if (!SupportedCharCache.TryGetValue(character, out var supported))
            {
                supported = _loadedFont.HasCharacter(character);
                SupportedCharCache[character] = supported;
            }
            if (!supported)
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
            LoadedFontIds.Clear();
            MissingStyleFonts.Clear();
            SupportedCharCache.Clear();
            foreach (var asset in _loadedBundle.LoadAllAssets<TMP_FontAsset>())
            {
                if (asset == null)
                    continue;
                asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                LoadedFontIds.Add(asset.GetInstanceID());
                if (asset.material != null)
                    UiStyleManager.RegisterModMaterial(asset.material);
                LoadedFonts[asset.name] = asset;
                firstFont ??= asset;
                if (_loadedFont == null &&
                    asset.name.Contains("alimama", StringComparison.OrdinalIgnoreCase))
                    _loadedFont = asset;
            }

            _loadedFont ??= firstFont;

            if (_loadedFont == null)
                throw new InvalidOperationException("No TMP_FontAsset was found in the configured AssetBundle");

            _loadedFont.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            LoadedFontIds.Add(_loadedFont.GetInstanceID());
            if (_loadedFont.material != null)
                UiStyleManager.RegisterModMaterial(_loadedFont.material);

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

        Plugin.Log.LogWarning("No Legacy UI font is available; live dialogue retains UTAGE's original font and reveal state. Bundle a Legacy Font for complete Chinese glyph coverage.");
#endif
    }

    private static void ApplyLegacyStoryFont(Text text)
    {
        StoryOriginalFonts.TryAdd(text.GetInstanceID(), text.font);
        if (_loadedLegacyFont != null && text.font != _loadedLegacyFont)
            text.font = _loadedLegacyFont;
    }

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
