using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;

namespace MonsterMusumeTDMod.Patches;

public static class PatchManager
{
    private const int MaxAppliedValuesCapacity = 4096;

    private static HarmonyLib.Harmony _harmony;
    private static readonly List<MethodBase> PatchedMethods = new();
    private static TranslationManager _translations;
    private static ModConfig _config;
    private static bool _processingDiscoveredText;
    [ThreadStatic] internal static bool DeferringTmpPresentation;
    private static bool _storyTargetLogged;
    private static bool _storyMatchLogged;
    private static bool _storyMissLogged;
    private static bool _storyPreParseHitLogged;
    private static Il2CppSystem.Func<string, Utage.TextParserBase> _previousStoryParser;
    private static Il2CppSystem.Func<string, Utage.TextParserBase> _storyParser;
    private static readonly Dictionary<string, Type> RuntimeTypeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, List<MethodInfo>> DeclaredMethodsCache = new();

    // The game writes the translated argument back through the same setter.
    // Track that value per UI component so a composed translation is not
    // mistaken for a new untranslated Japanese string on the nested pass.
    private static readonly Dictionary<int, string> AppliedTextValues = new();
    private static readonly Dictionary<int, (string Source, string NormalizedPath, string Translated, bool IsTranslated)> SlotBindingCache = new(1024);

    public static void ClearSlotBindingCache()
    {
        AppliedTextValues.Clear();
        SlotBindingCache.Clear();
        DownloadRequestMonitor.Reset();
    }

    public static void Install(TranslationManager translations, ModConfig config, string pluginRoot = null)
    {
        _translations = translations;
        _config = config;
        _harmony = new HarmonyLib.Harmony("MonsterMusumeTDMod");

        PatchTypeMethods("TMPro.TMP_Text", new[] { "set_text", "SetText" });
        PatchTypeMethods("TMPro.TextMeshProUGUI", new[] { "set_text", "SetText" });
        PatchTypeMethods("UnityEngine.UI.Text", new[] { "set_text" });
        PatchStoryTextParserEntryPoints();
        PatchStorySceneEntryPoints();
        // Utage's full-screen history view uses this custom Text-derived control.
        PatchTypeMethods("Utage.UguiNovelText", new[] { "set_text", "SetText", "UpdateText" });
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
        PatchFontSetters("UnityEngine.UI.Text");
        PatchTmpRefreshTriggers("TMPro.TMP_Text", new[] { "set_fontSharedMaterial", "set_fontMaterial" });
        PatchTmpRefreshTriggers("TMPro.TMP_Text", new[] { "OnEnable" }, requestFastScan: true);
        // TextMeshProUGUI declares OnEnable and OnCanvasHierarchyChanged (TMP_Text does not).
        // When a tab/button toggles On/Off containers via SetActive(true) or when a page
        // prefab is moved out of WindowPageStockObject under UICanvas, Unity invokes these.
        PatchContextPostfixMethods(
            "TMPro.TextMeshProUGUI",
            new[] { "OnEnable", "OnCanvasHierarchyChanged" },
            nameof(Hooks.OnCanvasHierarchyChanged));
        PatchContextPostfixMethods(
            "TMPro.TextMeshProUGUI",
            new[] { "OnTransformParentChanged" },
            nameof(Hooks.OnHierarchyChanged));
        PatchContextPostfixMethods(
            "UnityEngine.UI.Selectable",
            new[] { "DoStateTransition", "OnPointerDown", "OnPointerUp", "OnSelect", "OnDeselect" },
            nameof(Hooks.ApplyButtonState));
        // UTAGE 类型名和方法签名会随游戏版本变化；这些探针失败时只跳过，不阻止 TMP 补丁加载。
        foreach (var typeName in new[]
        {
            "Utage.AdvCommandText", "Utage.AdvEngine", "Utage.MessageControl"
        })
        {
            PatchTypeMethods(typeName, new[]
            {
                "OnPlayMessage", "PlayScenario", "PlayAdvScenario", "Execute"
            }, contextOnly: true);
        }

        DownloadRequestMonitor.Install(_harmony, _config, pluginRoot);
        DeclaredMethodsCache.Clear();
        Plugin.Log.LogInfo($"Installed {PatchedMethods.Count} runtime patches");
    }

    public static void Uninstall()
    {
        if (_storyParser != null)
        {
            Utage.TextData.CreateCustomTextParser = _previousStoryParser;
            _storyParser = null;
            _previousStoryParser = null;
        }
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { Plugin.Log?.LogWarning($"Unpatch failed: {ex.Message}"); }
        PatchedMethods.Clear();
        DeclaredMethodsCache.Clear();
        AppliedTextValues.Clear();
        SlotBindingCache.Clear();
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

            TmpFontInstaller.ApplyToLegacyText(text);
        }
        finally
        {
            _processingDiscoveredText = false;
        }
    }

    private static List<MethodInfo> GetDeclaredMethodsCached(Type type)
    {
        if (!DeclaredMethodsCache.TryGetValue(type, out var methods))
        {
            methods = AccessTools.GetDeclaredMethods(type);
            DeclaredMethodsCache[type] = methods;
        }
        return methods;
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

        foreach (var method in GetDeclaredMethodsCached(type))
        {
            if (!Matches(method.Name, methodNames))
                continue;
            var parameters = method.GetParameters();
            if (!contextOnly)
            {
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                    continue;
            }

            try
            {
                var prefixName = contextOnly
                    ? nameof(Hooks.ObserveContext)
                    : nameof(Hooks.TranslateString);
                var needsFormattedPostfix = !contextOnly &&
                    typeName.StartsWith("TMPro.", StringComparison.Ordinal) &&
                    string.Equals(method.Name, "SetText", StringComparison.Ordinal) &&
                    parameters.Length > 1;
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(Hooks), prefixName),
                    postfix: needsFormattedPostfix
                        ? new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnFormattedSetText))
                        : null
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
        try
        {
            _previousStoryParser = Utage.TextData.CreateCustomTextParser;
            _storyParser = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string, Utage.TextParserBase>>(
                new Func<string, Utage.TextParserBase>(ParseStoryText));
            Utage.TextData.CreateCustomTextParser = _storyParser;
            Plugin.Log.LogInfo("Installed UTAGE CreateCustomTextParser translation callback");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning($"Failed to install UTAGE translation parser callback: {exception.Message}");
        }
    }

    private static Utage.TextParserBase ParseStoryText(string source)
    {
        var text = source;
        if (_translations != null &&
            _translations.TryTranslateStoryBeforeParse(source, out var translated))
        {
            text = translated;
            if (!_storyPreParseHitLogged)
            {
                _storyPreParseHitLogged = true;
                Plugin.Log.LogInfo(
                    $"UTAGE translation parser callback matched; scene={StorySceneContext.ParsingId}; " +
                    "interval timing is parsed from translated text");
            }
        }

        return _previousStoryParser != null
            ? _previousStoryParser.Invoke(text)
            : new Utage.TextParser(text, false);
    }

    private static void PatchStorySceneEntryPoints()
    {
        PatchStorySceneMethod("Utage.AdvScenarioData", "Init", nameof(Hooks.BeginStoryScript),
            nameof(Hooks.RegisterStoryLabels), scoped: true);
        PatchStorySceneMethod("Utage.AdvCommand", "ParseCellLocalizedText", nameof(Hooks.BeginStoryCommand),
            nameof(Hooks.EndParseCellLocalizedText), scoped: true);
        PatchStorySceneMethod("Utage.AdvCommandText", "DoCommand", nameof(Hooks.BeginStoryCommand),
            null, scoped: true);
        PatchStorySceneMethod("Utage.AdvScenarioPlayer", "StartScenario", nameof(Hooks.StartStoryScene), null);
        PatchStorySceneMethod("Utage.AdvScenarioThread", "StartScenario", nameof(Hooks.StartStoryScene), null);
        PatchStorySceneMethod("Utage.AdvScenarioPlayer", "EndScenario", nameof(Hooks.EndStoryScene), null);
    }

    private static void PatchStorySceneMethod(string typeName, string name, string prefix, string postfix,
        bool scoped = false)
    {
        var type = FindRuntimeType(typeName);
        if (type == null) return;
        foreach (var method in GetDeclaredMethodsCached(type))
        {
            if (method.Name != name) continue;
            try
            {
                _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(Hooks), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Hooks), postfix),
                    finalizer: scoped ? new HarmonyMethod(typeof(Hooks), nameof(Hooks.EndStoryScope)) : null);
                PatchedMethods.Add(method);
                Plugin.Log.LogInfo($"Patched story scene context: {typeName}.{name}");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning($"Story scene hook unavailable: {typeName}.{name}: {exception.Message}");
            }
        }
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

        // Fast-path known assemblies before iterating AppDomain assemblies.
        Type directType = typeName switch
        {
            "TMPro.TMP_Text" => typeof(TMPro.TMP_Text),
            "TMPro.TextMeshProUGUI" => typeof(TMPro.TextMeshProUGUI),
            "UnityEngine.UI.Text" => typeof(UnityEngine.UI.Text),
            "UnityEngine.UI.Selectable" => typeof(UnityEngine.UI.Selectable),
            "UnityEngine.UI.Button" => typeof(UnityEngine.UI.Button),
            "UnityEngine.UI.Toggle" => typeof(UnityEngine.UI.Toggle),
            "Utage.AdvScenarioData" => typeof(Utage.AdvScenarioData),
            "Utage.AdvCommand" => typeof(Utage.AdvCommand),
            "Utage.AdvCommandText" => typeof(Utage.AdvCommandText),
            _ => null
        };
        if (directType != null)
        {
            RuntimeTypeCache[typeName] = directType;
            return directType;
        }

        var utageAssembly = typeof(Utage.TextData).Assembly;
        directType = TryResolveInAssembly(utageAssembly, typeName);
        if (directType != null)
        {
            RuntimeTypeCache[typeName] = directType;
            return directType;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = assembly.GetName().Name ?? string.Empty;
            if (asmName.StartsWith("System", StringComparison.Ordinal) ||
                asmName.StartsWith("Microsoft", StringComparison.Ordinal) ||
                asmName.StartsWith("BepInEx", StringComparison.Ordinal) ||
                asmName.StartsWith("MelonLoader", StringComparison.Ordinal) ||
                asmName.StartsWith("Il2CppInterop", StringComparison.Ordinal) ||
                asmName.StartsWith("MonoMod", StringComparison.Ordinal) ||
                asmName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                asmName.Equals("0Harmony", StringComparison.Ordinal) ||
                asmName.Equals("netstandard", StringComparison.Ordinal) ||
                asmName.Equals("mscorlib", StringComparison.Ordinal) ||
                asmName.Equals("Il2Cppmscorlib", StringComparison.Ordinal))
            {
                continue;
            }

            var type = TryResolveInAssembly(assembly, typeName);
            if (type == null)
                continue;

            RuntimeTypeCache[typeName] = type;
            return type;
        }

        RuntimeTypeCache[typeName] = null;
        return null;
    }

    private static Type TryResolveInAssembly(Assembly assembly, string typeName)
    {
        if (assembly == null)
            return null;
        try
        {
            var type = assembly.GetType(typeName, false, false);
#if ANDROID
            type ??= assembly.GetType("Il2Cpp" + (typeName.Contains('.') ? "" : ".") + typeName, false, false);
            if (type == null && !typeName.Contains('.'))
                type = assembly.GetType("Il2CppUtage." + typeName, false, false);
#else
            if (type == null && !typeName.Contains('.'))
                type = assembly.GetType("Utage." + typeName, false, false);
#endif
            return type;
        }
        catch
        {
            return null;
        }
    }

    private static void PatchFontSetters(string typeName)
    {
        var type = FindRuntimeType(typeName);
        if (type == null)
            return;

        foreach (var method in GetDeclaredMethodsCached(type))
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

        foreach (var method in GetDeclaredMethodsCached(type))
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

        foreach (var method in GetDeclaredMethodsCached(type))
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
                if (typeName == "AttachAbilityContent")
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
        public static void BeginStoryScript(Utage.AdvScenarioData __instance, out IDisposable __state)
        {
            var scene = string.Empty;
            try
            {
                scene = _translations.ResolveStoryScenarioId(__instance.Name);
                if (scene.Length == 0) scene = _translations.ResolveStoryScenarioId(__instance.DataGridName);
            }
            catch (Exception exception) { Plugin.Log.LogDebug($"Story script context: {exception.Message}"); }
            __state = StorySceneContext.EnterScriptParsing(scene);
        }

        public static void RegisterStoryLabels(Utage.AdvScenarioData __instance)
        {
            try
            {
                var scene = StorySceneContext.ParsingId;
                if (string.IsNullOrEmpty(scene))
                {
                    scene = _translations.ResolveStoryScenarioId(__instance.Name);
                    if (scene.Length == 0) scene = _translations.ResolveStoryScenarioId(__instance.DataGridName);
                    if (scene.Length == 0) scene = StorySceneContext.CurrentScopeInferredScene;
                }
                if (string.IsNullOrEmpty(scene)) return;
                foreach (var entry in __instance.ScenarioLabels)
                    StorySceneContext.RegisterLabel(entry.Key, scene);
                Plugin.Log.LogInfo($"Story script indexed: {scene}");
            }
            catch (Exception exception) { Plugin.Log.LogDebug($"Story label indexing: {exception.Message}"); }
        }

        public static void BeginStoryCommand(Utage.AdvCommand __instance, out IDisposable __state)
        {
            var scene = string.Empty;
            try
            {
                var grid = __instance.RowData?.Grid;
                if (grid != null)
                {
                    scene = _translations.ResolveStoryScenarioId(grid.Name);
                    if (scene.Length == 0) scene = _translations.ResolveStoryScenarioId(grid.SheetName);
                }
#if ANDROID
                var command = __instance as Utage.AdvCommandText ?? __instance?.TryCast<Utage.AdvCommandText>();
#else
                var command = __instance as Utage.AdvCommandText;
#endif
                if (command != null)
                {
                    var label = command.PageData?.ScenarioLabelData?.ScenarioLabel;
                    if (scene.Length == 0) scene = StorySceneContext.ResolveLabel(label);
                    if (scene.Length == 0) scene = _translations.ResolveStoryScenarioId(label);
                    if (scene.Length > 0) StorySceneContext.SetPlayback(scene);
                }
                if (scene.Length > 0)
                    StorySceneContext.PropagateSceneToCurrentScope(scene);
            }
            catch (Exception exception) { Plugin.Log.LogDebug($"Story command context: {exception.Message}"); }
            if (scene.Length == 0) scene = StorySceneContext.ParsingId;
            if (scene.Length == 0) scene = StorySceneContext.CurrentScopeInferredScene;
            __state = StorySceneContext.EnterScriptParsing(scene);
        }

        public static void EndParseCellLocalizedText(string __result)
        {
            if (!string.IsNullOrEmpty(__result))
            {
                var scene = StorySceneContext.ParsingId;
                if (scene.Length == 0)
                    scene = StorySceneContext.CurrentScopeInferredScene;
                if (scene.Length > 0)
                    StorySceneContext.SetPendingParsedCell(__result, scene);
            }
        }

        public static Exception EndStoryScope(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();
            return __exception;
        }

        public static void StartStoryScene(string __0)
        {
            var scene = StorySceneContext.ResolveLabel(__0);
            if (scene.Length == 0) scene = _translations.ResolveStoryScenarioId(__0);
            StorySceneContext.SetPlayback(scene);
            Plugin.Log.LogInfo($"Story playback context: label={__0}, scene={scene}");
        }

        public static void EndStoryScene()
        {
            StorySceneContext.ClearPlayback();
            ScenarioContext.ClearResourceContext();
        }

        public static void ObserveContext(object __instance) => ScenarioContext.Observe(__instance);

        public static void OnHierarchyChanged(object __instance)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying || UiCanvasTranslationScanner.IsApplying(__instance))
                return;
            if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(__instance))
                return;
            if (__instance is UnityEngine.Component component && component != null && component.transform != null)
            {
                UiHierarchyClassifier.InvalidateTransform(component.transform);
                SlotBindingCache.Remove(component.GetInstanceID());
            }
            UiCanvasTranslationScanner.QueueActivationRefresh(__instance);
        }

        public static void OnCanvasHierarchyChanged(object __instance)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying)
                return;
            if (__instance is not TMPro.TMP_Text tmpText || ReferenceEquals(tmpText, null))
                return;
            var instanceId = tmpText.GetInstanceID();
            if (UiHierarchyClassifier.IsKnownInternalLayerId(instanceId) ||
                UiCanvasTranslationScanner.IsApplyingId(instanceId))
                return;
            if (tmpText.gameObject == null || !tmpText.gameObject.activeInHierarchy)
                return;
            SlotBindingCache.Remove(instanceId);
            UiStyleManager.MarkUnderlayDirty(instanceId);
            UiCanvasTranslationScanner.QueueActivationRefreshById(instanceId, tmpText);
        }

        public static void ApplyButtonState(object __instance) =>
            UiStyleManager.NotifySelectableStateChanged(__instance);

        public static void ApplyFont(object __instance)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying || UiCanvasTranslationScanner.IsApplying(__instance))
                return;
            if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(__instance)) return;
            if (__instance is TMPro.TMP_Text tmpText && tmpText != null)
            {
                UiStyleManager.InvalidateInstanceStyle(tmpText);
                UiCanvasTranslationScanner.QueueActivationRefresh(tmpText);
                return;
            }
            TmpFontInstaller.ApplyToCurrentText(__instance);
            TmpFontInstaller.ApplyConfiguredUiStyle(__instance);
        }

        public static void QueueUiRefresh(object __instance)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying || UiCanvasTranslationScanner.IsApplying(__instance))
                return;
            if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(__instance)) return;
            if (__instance is TMPro.TMP_Text tmpText && tmpText != null)
            {
                UiStyleManager.InvalidateInstanceStyle(tmpText);
                UiCanvasTranslationScanner.QueueActivationRefresh(tmpText);
                return;
            }
            UiCanvasTranslationScanner.QueueImmediateRefresh(__instance);
        }

        public static void QueueUiActivationRefresh(object __instance)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying || UiCanvasTranslationScanner.IsApplying(__instance))
                return;
            if (UiHierarchyClassifier.IsUiTextSoftOutlineLayer(__instance)) return;
            if (__instance is TMPro.TMP_Text tmpText && tmpText != null)
            {
                if (tmpText.gameObject == null || !tmpText.gameObject.activeInHierarchy)
                    return;
                UiStyleManager.MarkUnderlayDirty(tmpText.GetInstanceID());
            }
            UiCanvasTranslationScanner.QueueActivationRefresh(__instance);
        }

        public static void OnFormattedSetText(object __instance, string __0)
        {
            if (UiStyleManager.IsApplying || TmpFontInstaller.IsApplying || UiCanvasTranslationScanner.IsApplying(__instance))
                return;
            if (string.IsNullOrEmpty(__0) || __0.IndexOf('{') < 0)
                return;
            if (__instance is TMPro.TMP_Text tmpText && tmpText != null)
                UiCanvasTranslationScanner.QueueActivationRefresh(tmpText);
        }

        public static void ApplyLegacyFont(object __instance) => TmpFontInstaller.ApplyToLegacyText(__instance);

        public static void ApplySubSkillCardFont(object __instance) =>
            TmpFontInstaller.ApplyToSubSkillCard(__instance);

        public static void TranslateString(object __instance, ref string __0)
        {
            if (UiCanvasTranslationScanner.IsApplying(__instance)) return;
            var previous = DeferringTmpPresentation;
            DeferringTmpPresentation = __instance is TMPro.TMP_Text;
            try { TranslateStringCore(__instance, ref __0); }
            finally { DeferringTmpPresentation = previous; }
        }

        private static void TranslateStringCore(object __instance, ref string __0)
        {
            if (_config == null || !_config.Enabled.Value)
                return;

            var instanceId = GetInstanceId(__instance);
            if (instanceId != 0 && UiHierarchyClassifier.IsKnownInternalLayerId(instanceId))
                return;

            UiStyleManager.PrepareUnderlayForTextChange(__instance);

            if (string.IsNullOrEmpty(__0))
                return;

            var tmpInstance = __instance as TMPro.TMP_Text;
            if (instanceId != 0 &&
                AppliedTextValues.TryGetValue(instanceId, out var appliedValue) &&
                string.Equals(appliedValue, __0, StringComparison.Ordinal))
            {
                if (tmpInstance != null)
                {
                    if (!UiStyleManager.IsInstanceStyled(tmpInstance, instanceId))
                        UiCanvasTranslationScanner.QueueActivationRefreshById(instanceId, tmpInstance);
                    else
                        UiStyleManager.MarkUnderlayDirty(instanceId);
                }
                return;
            }

            if (__instance is UnityEngine.UI.Text storyText &&
                UiHierarchyClassifier.IsStoryMessageText(storyText))
            {
                StoryLegacyFontReplacer.NotifyStoryTextActive(storyText);
                TranslateStoryString(storyText, ref __0);
                return;
            }

            var isTmpText = tmpInstance != null;
            if (isTmpText &&
                !_translations.CanPossiblyNeedTranslation(__0) &&
                !_translations.IsKnownTranslationValue(__0) &&
                !TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
            {
                if (instanceId != 0)
                {
                    AppliedTextValues.Remove(instanceId);
                    SlotBindingCache.Remove(instanceId);
                    UiCanvasTranslationScanner.CancelPendingRefresh(instanceId);
                }
                return;
            }

            var path = UiHierarchyClassifier.GetHierarchyPathAndFlags(__instance, out var normalizedPath, out var flags);
            var isUnderUiCanvas = (flags & UiPathFlags.UnderUiCanvas) != 0;

            if (instanceId != 0 &&
                isUnderUiCanvas &&
                SlotBindingCache.TryGetValue(instanceId, out var slotBinding) &&
                string.Equals(slotBinding.Source, __0, StringComparison.Ordinal) &&
                string.Equals(slotBinding.NormalizedPath, normalizedPath, StringComparison.Ordinal))
            {
                if (!slotBinding.IsTranslated)
                    return;
                if (UiStyleManager.IsInstanceStyled(tmpInstance, instanceId))
                {
                    __0 = slotBinding.Translated;
                    AppliedTextValues[instanceId] = __0;
                    UiStyleManager.MarkUnderlayDirty(instanceId);
                    UiCanvasTranslationScanner.RecordFastProcessedText(instanceId, __0, tmpInstance);
                    return;
                }
            }

            _translations.ObserveCharacterOrSkillText(__instance, __0);

            if ((_config.TranslateUiTexts.Value || _config.TranslateSubSkills.Value) &&
                _translations.TryTranslateUiText(
                    __0,
                    normalizedPath,
                    out var uiTextTranslation))
            {
                var uiSource = __0;
                SetTranslatedArgument(__instance, ref __0, uiTextTranslation, normalizedPath);
                if (!TmpFontInstaller.TryFastCompleteTranslatedTmpText(__instance, __0, uiSource, isSubSkill: false))
                    TmpFontInstaller.ApplyToTranslatedUiText(__instance, __0, uiSource);
                return;
            }

            if (_translations.IsKnownTranslationValue(__0))
            {
                if (isTmpText)
                {
                    if (!TmpFontInstaller.TryFastCompleteTranslatedTmpText(__instance, __0, null, isSubSkill: false))
                        TmpFontInstaller.ApplyToTranslatedUiText(__instance, __0);
                }
                return;
            }

            var isUnformattedTemplate = __0.IndexOf('{') >= 0;
            if (instanceId != 0)
            {
                AppliedTextValues.Remove(instanceId);
                if (isUnderUiCanvas && !isUnformattedTemplate && !string.IsNullOrEmpty(normalizedPath))
                {
                    if (SlotBindingCache.Count >= MaxAppliedValuesCapacity)
                        SlotBindingCache.Clear();
                    SlotBindingCache[instanceId] = (__0, normalizedPath, __0, false);
                }
                else
                {
                    SlotBindingCache.Remove(instanceId);
                }
            }
            if (TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
            {
                TmpFontInstaller.RestoreTranslatedUiText(__instance);
                TmpFontInstaller.RestoreTranslatedSubSkillText(__instance);
            }

            if (isTmpText)
            {
                UiCanvasTranslationScanner.QueueActivationRefreshById(instanceId, tmpInstance);
            }
        }

        private static void TranslateStoryString(UnityEngine.UI.Text storyText, ref string source)
        {
            if (!_storyTargetLogged)
            {
                _storyTargetLogged = true;
                Plugin.Log.LogInfo(
                    $"Legacy story translation target active: " +
                    $"{UiHierarchyClassifier.GetHierarchyPath(storyText)}");
            }

            if (UiHierarchyClassifier.IsStorySpeakerNameText(storyText))
            {
                if (_config.TranslateNames.Value &&
                    _translations.TryTranslateName(source, out var nameTranslation))
                {
                    SetTranslatedArgument(storyText, ref source, nameTranslation, null);
                    TmpFontInstaller.ReplaceKnownStoryTextWithTmp(storyText, source);
                    TmpFontInstaller.SyncKnownStoryText(storyText, source);
                }
                else if (_translations.IsKnownTranslationValue(source))
                {
                    TmpFontInstaller.ApplyToStoryText(storyText, source);
                    TmpFontInstaller.ReplaceKnownStoryTextWithTmp(storyText, source);
                    TmpFontInstaller.SyncKnownStoryText(storyText, source);
                }
                else
                {
                    TmpFontInstaller.RestoreOriginalStoryPresentation(storyText);
                }
                return;
            }

            var activeScene = StorySceneContext.EffectivePlaybackId;
            if (activeScene.Length == 0)
                activeScene = ScenarioContext.CurrentId;

            if (_config.TranslateScenarios.Value &&
                _translations.TryTranslateStoryExact(
                    source,
                    activeScene,
                    ScenarioContext.CurrentResourceId,
                    out var translated))
            {
                SetTranslatedArgument(storyText, ref source, translated, null);
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

            if (_translations.IsKnownStoryTranslationValue(source) ||
                _translations.IsKnownTranslationValue(source))
            {
                TmpFontInstaller.ApplyToStoryText(storyText, source);
                TmpFontInstaller.ReplaceKnownStoryTextWithTmp(storyText, source);
                TmpFontInstaller.SyncKnownStoryText(storyText, source);
                return;
            }

            if (!_storyMissLogged)
            {
                _storyMissLogged = true;
                Plugin.Log.LogInfo(
                    $"Legacy story translation first miss: length={source.Length}, " +
                    $"scenario={activeScene}, " +
                    $"resource={ScenarioContext.CurrentResourceId ?? "<none>"}");
            }
            TmpFontInstaller.RestoreOriginalStoryPresentation(storyText);
        }
    }

    private static void SetTranslatedArgument(object instance, ref string argument, string translated, string normalizedPath)
    {
        var instanceId = GetInstanceId(instance);
        if (instanceId != 0)
        {
            if (AppliedTextValues.Count >= MaxAppliedValuesCapacity)
            {
                AppliedTextValues.Clear();
                SlotBindingCache.Clear();
            }
            AppliedTextValues[instanceId] = translated;
            if (!string.IsNullOrEmpty(normalizedPath))
                SlotBindingCache[instanceId] = (argument, normalizedPath, translated, true);
        }
        argument = translated;
    }

    private static int GetInstanceId(object instance) =>
        instance is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : 0;
}
