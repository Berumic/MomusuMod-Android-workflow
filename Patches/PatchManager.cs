using System;
using System.Collections.Generic;
using HarmonyLib;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;

namespace MonsterMusumeTDMod.Patches;

public static class PatchManager
{
    private static HarmonyLib.Harmony _harmony;
    private static readonly List<System.Reflection.MethodBase> PatchedMethods = new();
    private static TranslationManager _translations;
    private static ModConfig _config;
    private static bool _processingDiscoveredText;
    private static bool _storyTargetLogged;
    private static bool _storyMatchLogged;
    private static bool _storyMissLogged;
    private static bool _storyPreParseHitLogged;
    private static readonly Dictionary<string, Type> RuntimeTypeCache = new(StringComparer.Ordinal);
    // The game writes the translated argument back through the same setter.
    // Track that value per UI component so a composed translation is not
    // mistaken for a new untranslated Japanese string on the nested pass.
    private static readonly Dictionary<int, string> AppliedTextValues = new();

    public static void Install(TranslationManager translations, ModConfig config)
    {
        _translations = translations;
        _config = config;
        _harmony = new HarmonyLib.Harmony("MonsterMusumeTDMod");

        PatchTypeMethods("TMPro.TMP_Text", new[] { "set_text", "SetText" });
        PatchTypeMethods("TMPro.TextMeshProUGUI", new[] { "set_text", "SetText" });
        PatchTypeMethods("UnityEngine.UI.Text", new[] { "set_text" });
        PatchStoryTextParserEntryPoints();
        // Utage's full-screen history view uses this custom Text-derived control.
        PatchTypeMethods("UguiNovelText", new[] { "set_text", "SetText", "UpdateText" });
        // The attach-ability window first stores the master-data description
        // through this method, then binds it to a TMP child. Translating here
        // prevents that later binding pass from restoring the Japanese text.
        PatchTypeMethods("AttachAbilityContent", new[] { "SetSkillText" });
        PatchTypeMethods("SubSkillRecipeBannerBase", new[] { "SetSkillText" });
        PatchContextPostfixMethods(
            "AttachAbilityContent",
            new[] { "UpdateSkillText" },
            nameof(Hooks.ApplySubSkillCardFont)
        );
        PatchFontSetters("TMPro.TMP_Text");
        PatchFontSetters("TMPro.TextMeshProUGUI");
        PatchFontSetters("UnityEngine.UI.Text");
        PatchTmpRefreshTriggers("TMPro.TMP_Text", new[] { "set_fontSharedMaterial", "set_fontMaterial" });
        PatchTmpRefreshTriggers("TMPro.TextMeshProUGUI", new[] { "OnEnable" }, requestFastScan: true);
        // Some detail labels are concrete TMP_Text subclasses other than
        // TextMeshProUGUI. Catch their activation too so first-entry binding
        // gets a deferred hierarchy-aware translation pass.
        PatchTmpRefreshTriggers("TMPro.TMP_Text", new[] { "OnEnable" }, requestFastScan: true);
        // A pooled UI subtree can be populated before it joins its final
        // Canvas. Apply path styles as soon as the hierarchy becomes valid,
        // in the same frame and before rendering.
        PatchTmpRefreshTriggers(
            "TMPro.TextMeshProUGUI",
            new[] { "OnTransformParentChanged", "OnCanvasHierarchyChanged" },
            requestFastScan: true);
        // UTAGE 类型名和方法签名会随游戏版本变化；这些探针失败时只跳过，不阻止 TMP 补丁加载。
        foreach (var typeName in new[]
        {
            "Utage.AdvCommandText", "Utage.MessageControl", "Utage.AdvEngine"
        })
        {
            PatchTypeMethods(typeName, new[]
            {
                "OnPlayMessage", "PlayScenario", "PlayAdvScenario", "Execute"
            }, contextOnly: true);
        }

        Plugin.Log.LogInfo($"Installed {PatchedMethods.Count} runtime patches");
    }

    public static void Uninstall()
    {
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { Plugin.Log?.LogWarning($"Unpatch failed: {ex.Message}"); }
        PatchedMethods.Clear();
    }

    public static void ProcessDiscoveredLegacyText(UnityEngine.UI.Text text)
    {
        if (_processingDiscoveredText || text == null || _config == null || !_config.Enabled.Value)
            return;

        var content = text.text;
        if (string.IsNullOrEmpty(content))
            return;

        _processingDiscoveredText = true;
        try
        {
            Hooks.TranslateString(text, ref content);
            if (!string.Equals(content, text.text, StringComparison.Ordinal))
                text.text = content;

            // The game's Utage binding can restore its original UGUI font
            // after the translated value has been written. Reassert the
            // bundled Legacy font here even when the translation writeback
            // was intentionally ignored by the nested setter guard.
            TmpFontInstaller.ApplyToLegacyText(text);
        }
        finally
        {
            _processingDiscoveredText = false;
        }
    }

    private static void PatchTypeMethods(
        string typeName,
        IEnumerable<string> methodNames,
        bool contextOnly = false
    )
    {
        var type = FindRuntimeType(typeName);
        if (type == null)
        {
            Plugin.Log.LogDebug($"Runtime type not found: {typeName}");
            return;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!Matches(method.Name, methodNames))
                continue;
            if (!contextOnly)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                    continue;
            }

            try
            {
                var prefixName = contextOnly
                    ? nameof(Hooks.ObserveContext)
                    : nameof(Hooks.TranslateString);
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(Hooks), prefixName),
                    postfix: contextOnly || !typeName.StartsWith("TMPro.", StringComparison.Ordinal)
                        ? null
                        : new HarmonyMethod(typeof(Hooks), nameof(Hooks.ReapplyTranslatedText))
                );
                PatchedMethods.Add(method);
                if (typeName == "AttachAbilityContent" || typeName == "SubSkillRecipeBannerBase")
                    Plugin.Log.LogInfo($"Patched sub-skill runtime method: {type.FullName}.{method.Name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"Skip {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchStoryTextParserEntryPoints()
    {
        var type = FindRuntimeType("Utage.TextData");
        if (type == null)
        {
            Plugin.Log.LogWarning(
                "UTAGE TextData was not found; " +
                "story interval tags will use the original timing");
            return;
        }

        var targets = new (System.Reflection.MethodBase Method, string Name)[]
        {
            (
                Method: AccessTools.Constructor(type, new[] { typeof(string) }),
                Name: "TextData(string) constructor"
            ),
            (
                Method: AccessTools.Method(type, "CreateTextParser", new[] { typeof(string) }),
                Name: "TextData.CreateTextParser(string)"
            )
        };

        var patchedAny = false;
        foreach (var target in targets)
        {
            if (target.Method == null)
            {
                Plugin.Log.LogWarning($"UTAGE {target.Name} was not found");
                continue;
            }

            try
            {
                _harmony.Patch(
                    target.Method,
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.TranslateStoryBeforeParse))
                );
                PatchedMethods.Add(target.Method);
                patchedAny = true;
                Plugin.Log.LogInfo(
                    $"Patched UTAGE {target.Name} for translated story timing");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning(
                    $"Failed to patch UTAGE {target.Name}: {exception.Message}");
            }
        }

        if (!patchedAny)
            Plugin.Log.LogWarning("No UTAGE story pre-parse entry point was patched");
    }

    private static bool Matches(string name, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
            if (string.Equals(name, candidate, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static Type FindRuntimeType(string typeName)
    {
        if (RuntimeTypeCache.TryGetValue(typeName, out var cached))
            return cached;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try
            {
                // Assembly.GetType performs an exact lookup without enumerating every
                // generated IL2CPP type, some of which cannot be reflected safely.
                type = assembly.GetType(typeName, false, false);
#if ANDROID
                type ??= assembly.GetType("Il2Cpp" + (typeName.Contains('.') ? "" : ".") + typeName, false, false);
                if (type == null && !typeName.Contains('.'))
                    type = assembly.GetType("Il2CppUtage." + typeName, false, false);
#endif
            }
            catch
            {
                continue;
            }

            if (type == null)
                continue;

            RuntimeTypeCache[typeName] = type;
            return type;
        }

        return null;
    }

    private static void PatchFontSetters(string typeName)
    {
        var type = FindRuntimeType(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name != "set_font" || method.GetParameters().Length != 1)
                continue;

            try
            {
                var hook = typeName == "UnityEngine.UI.Text"
                    ? nameof(Hooks.ApplyLegacyFont)
                    : nameof(Hooks.ApplyFont);
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Hooks), hook));
                PatchedMethods.Add(method);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"Skip {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchTmpRefreshTriggers(
        string typeName,
        IEnumerable<string> methodNames,
        bool requestFastScan = false)
    {
        var type = FindRuntimeType(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!Matches(method.Name, methodNames))
                continue;

            try
            {
                _harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(
                        typeof(Hooks),
                        requestFastScan
                            ? nameof(Hooks.QueueUiActivationRefresh)
                            : nameof(Hooks.QueueUiRefresh))
                );
                PatchedMethods.Add(method);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"Skip {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchContextPostfixMethods(
        string typeName,
        IEnumerable<string> methodNames,
        string postfixName
    )
    {
        var type = FindRuntimeType(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!Matches(method.Name, methodNames))
                continue;

            try
            {
                _harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(Hooks), postfixName)
                );
                PatchedMethods.Add(method);
                Plugin.Log.LogInfo($"Patched sub-skill font method: {type.FullName}.{method.Name}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"Skip {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static class Hooks
    {
        public static void ObserveContext(object __instance) => ScenarioContext.Observe(__instance);

        public static void TranslateStoryBeforeParse(
            System.Reflection.MethodBase __originalMethod,
            ref string __0
        )
        {
            if (_translations == null || string.IsNullOrEmpty(__0) ||
                !_translations.TryTranslateStoryBeforeParse(__0, out var translated))
                return;

            __0 = translated;
            if (!_storyPreParseHitLogged)
            {
                _storyPreParseHitLogged = true;
                Plugin.Log.LogInfo(
                    "UTAGE pre-parse story translation active via " +
                    $"{__originalMethod?.Name ?? "unknown"}; control-tag timing uses translated text");
            }
        }

        public static void ApplyFont(object __instance)
        {
            TmpFontInstaller.ApplyToCurrentText(__instance);
            TmpFontInstaller.ApplyConfiguredUiStyle(__instance);
            UiCanvasTranslationScanner.QueueImmediateRefresh(__instance);
        }

        public static void QueueUiRefresh(object __instance)
        {
            TmpFontInstaller.ApplyConfiguredUiStyle(__instance);
            UiCanvasTranslationScanner.QueueImmediateRefresh(__instance);
        }

        public static void QueueUiActivationRefresh(object __instance)
        {
            TmpFontInstaller.ApplyConfiguredUiStyle(__instance);
            UiCanvasTranslationScanner.QueueActivationRefresh(__instance);
        }

        public static void ApplyLegacyFont(object __instance) => TmpFontInstaller.ApplyToLegacyText(__instance);

        public static void ApplySubSkillCardFont(object __instance) =>
            TmpFontInstaller.ApplyToSubSkillCard(__instance);

        public static void ReapplyTranslatedText(object __instance)
        {
            TmpFontInstaller.ReapplyTranslatedText(__instance);
            UiCanvasTranslationScanner.QueueImmediateRefresh(__instance);
        }

        public static void TranslateString(object __instance, ref string __0)
        {
            if (_config == null || !_config.Enabled.Value)
                return;

            // A virtualized slot can receive its next source value before the
            // translation scanner runs. Hide the previous projection now so
            // an old Japanese/Chinese layer is never rendered with new text.
            UiStyleManager.PrepareUnderlayForTextChange(__instance);

            // Detail panels often assign TMP.text before the object is parented
            // under UICanvasTopUnitDetail. Defer one refresh so path-based UI
            // translation runs after Unity finishes attaching the hierarchy.
            if (__instance is TMPro.TMP_Text)
                UiCanvasTranslationScanner.QueueImmediateRefresh(__instance);

            if (string.IsNullOrEmpty(__0))
            {
                if (__instance is UnityEngine.UI.Text clearedStoryText &&
                    TmpFontInstaller.IsStoryMessageText(clearedStoryText) &&
                    !TmpFontInstaller.IsStorySpeakerNameText(clearedStoryText))
                {
                    _translations.ResetInferredStoryScenario();
                    ScenarioContext.ClearResourceContext();
                }
                return;
            }

            if (TmpFontInstaller.IsUiTextSoftOutlineLayer(__instance))
                return;

            // TMP can enter a nested setter after this prefix has changed the
            // argument to Chinese. Ignore that second pass so F9 remains a
            // source-text collector even with translation enabled.
            // Assigning the translated value to a legacy Text re-enters this
            // prefix. It is already the final Chinese value, so do not run it
            // through the unmatched-source path that restores the Japanese
            // font on the same reused component.
            if (IsAppliedTranslationWriteback(__instance, __0))
                return;

            // Keep UTAGE's Legacy Text path independent from the later UI and
            // sub-skill matching layers. This is the v0.1.23 story pipeline:
            // exact global story row first, then indexed story fragments.
            if (__instance is UnityEngine.UI.Text storyText &&
                TmpFontInstaller.IsStoryMessageText(storyText))
            {
                TranslateStoryString(storyText, ref __0);
                return;
            }

            // UICanvas entries must be considered before the known-value
            // guard. A manual row such as "HP": "HP" deliberately retains
            // its text while still requiring the same translated UI font.
            if (_config.TranslateUiTexts.Value &&
                TmpFontInstaller.IsUnderUiCanvas(__instance) &&
                !TmpFontInstaller.IsSubSkillText(__instance) &&
                !TmpFontInstaller.IsUiTranslationExcluded(__instance) &&
                _translations.TryTranslateUiText(
                    __0,
                    TmpFontInstaller.GetHierarchyPath(__instance),
                    out var uiTextTranslation))
            {
                var uiSource = __0;
                SetTranslatedArgument(__instance, ref __0, uiTextTranslation);
                TmpFontInstaller.ApplyToTranslatedUiText(__instance, __0, uiSource);
                return;
            }

            if (_translations.IsKnownTranslationValue(__0))
            {
                // AttachAbilityContent may receive the source first, then bind
                // the translated value to a TMP child. This second setter must
                // still apply the sub-skill font and its presentation.
                if (_config.TranslateSubSkills.Value &&
                    _translations.IsKnownSubSkillTranslationValue(__0) &&
                    !TmpFontInstaller.IsUnitDetailUiDescription(__instance))
                    TmpFontInstaller.ApplyToTranslatedSubSkillText(__instance, __0);
                return;
            }

            _translations.ObserveCharacterOrSkillText(__instance, __0);

            if (_config.TranslateSubSkills.Value &&
                !TmpFontInstaller.IsUnitDetailUiDescription(__instance) &&
                _translations.TryTranslateSubSkill(
                    __0,
                    TmpFontInstaller.GetHierarchyPath(__instance),
                    out var subSkillTranslation))
            {
                SetTranslatedArgument(__instance, ref __0, subSkillTranslation);
                TmpFontInstaller.ApplyToTranslatedSubSkillText(__instance, __0);
                return;
            }

            // Virtualized shop and list cards reuse the same TMP component.
            // Restore its original Japanese font when a previously translated
            // sub-skill/item slot receives an untranslated source string.
            TmpFontInstaller.RestoreTranslatedUiText(__instance);
            TmpFontInstaller.RestoreTranslatedSubSkillText(__instance);
        }

        private static void TranslateStoryString(UnityEngine.UI.Text storyText, ref string source)
        {
            if (!_storyTargetLogged)
            {
                _storyTargetLogged = true;
                Plugin.Log.LogInfo(
                    $"Legacy story translation target active: " +
                    $"{TmpFontInstaller.GetHierarchyPath(storyText)}");
            }

            // This also catches the nested setter/writeback path used by the
            // September 4 build without running translated Chinese as source.
            if (_translations.IsKnownStoryTranslationValue(source) ||
                _translations.IsKnownTranslationValue(source))
            {
                TmpFontInstaller.ApplyToStoryText(storyText, source);
                return;
            }

            if (TmpFontInstaller.IsStorySpeakerNameText(storyText))
            {
                if (_config.TranslateNames.Value &&
                    _translations.TryTranslateName(source, out var nameTranslation))
                {
                    SetTranslatedArgument(storyText, ref source, nameTranslation);
                    TmpFontInstaller.ReplaceKnownStoryTextWithTmp(storyText, source);
                    TmpFontInstaller.SyncKnownStoryText(storyText, source);
                }
                else
                {
                    TmpFontInstaller.RestoreOriginalStoryPresentation(storyText);
                }
                return;
            }

            if (_config.TranslateScenarios.Value &&
                _translations.TryTranslateStoryExact(
                    source,
                    ScenarioContext.CurrentId,
                    ScenarioContext.CurrentResourceId,
                    out var translated))
            {
                SetTranslatedArgument(storyText, ref source, translated);
                TmpFontInstaller.ApplyToStoryText(storyText, source);
                TmpFontInstaller.ReplaceKnownStoryTextWithTmp(storyText, source);
                TmpFontInstaller.SyncKnownStoryText(storyText, source);
                if (!_storyMatchLogged)
                {
                    _storyMatchLogged = true;
                    Plugin.Log.LogInfo("Legacy story translation matched through the September 4 pipeline");
                }
                return;
            }

            if (!_storyMissLogged)
            {
                _storyMissLogged = true;
                Plugin.Log.LogInfo(
                    $"Legacy story translation first miss: length={source.Length}, " +
                    $"scenario={ScenarioContext.CurrentId ?? "<none>"}, " +
                    $"resource={ScenarioContext.CurrentResourceId ?? "<none>"}");
            }
            TmpFontInstaller.RestoreOriginalStoryPresentation(storyText);
        }
    }

    private static void SetTranslatedArgument(object instance, ref string argument, string translated)
    {
        var instanceId = GetInstanceId(instance);
        if (instanceId != 0)
            AppliedTextValues[instanceId] = translated;
        argument = translated;
    }

    private static bool IsAppliedTranslationWriteback(object instance, string value)
    {
        var instanceId = GetInstanceId(instance);
        if (instanceId == 0 || !AppliedTextValues.TryGetValue(instanceId, out var appliedValue))
            return false;

        if (string.Equals(appliedValue, value, StringComparison.Ordinal))
            return true;

        // The reused UI component has received a new source string.
        AppliedTextValues.Remove(instanceId);
        return false;
    }

    private static int GetInstanceId(object instance) =>
        instance is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : 0;
}
