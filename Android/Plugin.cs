using System;
using System.IO;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MonsterMusumeTDMod.Android;
using MonsterMusumeTDMod.Patches;
using MonsterMusumeTDMod.Services;
using UnityEngine;

[assembly: MelonInfo(typeof(MonsterMusumeTDMod.Core.Plugin), "MonsterMusumeTDMod.Android", "0.1.134", "MonsterMusumeTDMod")]

namespace MonsterMusumeTDMod.Core;

public sealed class AndroidLog
{
    private readonly MelonLogger.Instance _logger;
    public AndroidLog(MelonLogger.Instance logger) => _logger = logger;
    public void LogInfo(object message) => _logger.Msg(message);
    public void LogWarning(object message) => _logger.Warning(message);
    public void LogError(object message) => _logger.Error(message);
    public void LogDebug(object message) => _logger.Msg(message);
}

public sealed class Plugin : MelonMod
{
    public static AndroidLog Log { get; private set; }
    public static TranslationManager Translations { get; private set; }
    public static ModConfig Settings { get; private set; }
    private GameObject _host;
    private float _nextReloadCheck;

    public override void OnInitializeMelon()
    {
        Log = new AndroidLog(LoggerInstance);
        Directory.CreateDirectory(AndroidPaths.ModDirectory);
        Settings = new ModConfig(new ConfigFile(Path.Combine(AndroidPaths.ModDirectory, "config.json")));
        Settings.Save();
        Log.LogInfo($"UI face dilate: {Settings.UiTextFaceDilate.Value}; matching styles.json rules may override this value");
        TranslationUpdateController.ApplyPendingAssetUpdate(AndroidPaths.PluginPath);
        Translations = new TranslationManager(AndroidPaths.PluginPath, Settings);
        Translations.LoadStatic();
        UiStyleManager.Initialize(AndroidPaths.PluginPath);
        // Force the generated assemblies into the domain before reflection-based patch discovery.
        _ = typeof(Il2CppUtage.TextData).Assembly;
        _ = typeof(Il2CppTMPro.TMP_Text).Assembly;
        _ = typeof(UnityEngine.UI.Text).Assembly;
        PatchManager.Install(Translations, Settings);
        Log.LogInfo($"Android translations: {AndroidPaths.ModDirectory}");
    }

    public override void OnLateInitializeMelon()
    {
        _host = new GameObject("MonsterMusumeTDMod.Android");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        TmpFontInstaller.TryInstall(AndroidPaths.PluginPath, Settings);
        Add<MosaicSuppressor>();
        Add<R18DialogueBackgroundController>();
        Add<StoryLegacyFontReplacer>();
        Add<UiCanvasTranslationScanner>();
        Add<TranslationUpdateController>();
        Add<UiSnapshotDumper>();
        Add<SubSkillOverviewScanner>();
        Add<TranslationReloadHotkey>();
        Log.LogInfo("Android translation components initialized");
    }

    private void Add<T>() where T : MonoBehaviour
    {
        ClassInjector.RegisterTypeInIl2Cpp<T>();
        _host.AddComponent<T>();
    }

    public override void OnUpdate()
    {
        if (_host == null || Time.unscaledTime < _nextReloadCheck)
            return;
        _nextReloadCheck = Time.unscaledTime + 2f;
        var marker = Path.Combine(AndroidPaths.ModDirectory, "reload.request");
        if (!File.Exists(marker))
            return;
        try
        {
            Settings.Reload();
            if (Settings.ManualSyncWithF6.Value)
                TranslationUpdateController.RequestManualSync();
            Translations.LoadStatic();
            UiStyleManager.Reload();
            UiCanvasTranslationScanner.InvalidateProcessingCache();
            R18DialogueBackgroundController.InvalidatePresentation();
            TmpFontInstaller.ReloadVisibleUiTranslations(Translations);
            TmpFontInstaller.RefreshVisibleSubSkillPresentation();
            TmpFontInstaller.RefreshVisibleUnitDetailPresentation();
            File.Delete(marker);
            Log.LogInfo("Android configuration and translations reloaded");
        }
        catch (Exception ex) { Log.LogError($"Android reload failed: {ex}"); }
    }

    public override void OnDeinitializeMelon()
    {
        PatchManager.Uninstall();
        if (_host != null)
            UnityEngine.Object.Destroy(_host);
    }
}
