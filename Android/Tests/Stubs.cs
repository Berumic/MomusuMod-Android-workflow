namespace UnityEngine
{
    public readonly record struct Color(float r, float g, float b, float a);
    public class MonoBehaviour { public MonoBehaviour(IntPtr pointer) { } }
    public class Material
    {
        public string[] shaderKeywords { get; set; }
        public bool IsKeywordEnabled(string keyword) => throw new NotSupportedException("Stripped Android ICall");
    }
}
namespace MonsterMusumeTDMod.Android
{
    internal static class AndroidPaths { public static string PluginPath => Path.GetTempPath(); }
}
namespace MonsterMusumeTDMod.Core
{
    public sealed class TestLog
    {
        public void LogInfo(object value) { }
        public void LogWarning(object value) { }
        public void LogError(object value) { }
    }
    public static class Plugin
    {
        public static TestLog Log { get; } = new();
        public static ModConfig Settings { get; set; }
        public static Services.TranslationManager Translations { get; set; }
    }
}
namespace MonsterMusumeTDMod.Services
{
    public sealed class RuntimeTextCollector
    {
        public RuntimeTextCollector(string root) { }
        public static string CreateStableUiCategory(string path) => path;
        public bool IsSubSkillScanActive => false;
        public void Observe(object instance, string source) { }
        public void CaptureActiveUiSnapshot(Func<string, string, bool> translated, Func<string, string> category) { }
        public void BeginSubSkillScan() { }
        public int EndSubSkillScan() => 0;
        public void CaptureVisibleSubSkillScanText() { }
    }
    public static class UiStyleManager { public static void Reload() { } }
    public static class TmpFontInstaller
    {
        public static int ReloadVisibleUiTranslations(TranslationManager translations) => 0;
        public static int RefreshVisibleSubSkillPresentation() => 0;
        public static int RefreshVisibleUnitDetailPresentation() => 0;
    }
}
namespace MonsterMusumeTDMod.Patches
{
    public static class UiCanvasTranslationScanner { public static void InvalidateProcessingCache() { } public static void RequestFastScan() { } }
    public static class R18DialogueBackgroundController { public static void InvalidatePresentation() { } }
}
