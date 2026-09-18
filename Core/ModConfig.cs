#if ANDROID
using MonsterMusumeTDMod.Android;
#else
using BepInEx.Configuration;
#endif

namespace MonsterMusumeTDMod.Core;

public sealed class ModConfig
{
    private readonly ConfigFile _config;
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> TranslateScenarios { get; }
    public ConfigEntry<bool> TranslateSubSkills { get; }
    public ConfigEntry<bool> TranslateNames { get; }
    public ConfigEntry<bool> TranslateUiTexts { get; }
    public ConfigEntry<bool> EnableUiSnapshotHotkey { get; }
    public ConfigEntry<bool> EnableSubSkillScanHotkey { get; }
    public ConfigEntry<string> Language { get; }
    public ConfigEntry<string> FontAssetPath { get; }
    public ConfigEntry<bool> EnableSpineMosaicReplacement { get; }
    public ConfigEntry<bool> HideR18DialogueBackground { get; }
    public ConfigEntry<bool> EnableR18WhiteTextOutline { get; }
    public ConfigEntry<bool> EnableR18ButtonTextOutline { get; }
    public ConfigEntry<float> UiTextOutlineWidth { get; }
    public ConfigEntry<float> UiTextFaceDilate { get; }
    public ConfigEntry<bool> EnableTranslationAutoUpdate { get; }
    public ConfigEntry<string> TranslationUpdateBaseUrl { get; }
    public ConfigEntry<string> TranslationAssetUpdateBaseUrl { get; }
    public ConfigEntry<int> TranslationUpdateTimeoutSeconds { get; }

    public ModConfig(ConfigFile config)
    {
        _config = config;
        Enabled = config.Bind("Translation", "Enabled", true, "启用运行时翻译");
        TranslateScenarios = config.Bind("Translation", "TranslateScenarios", true, "翻译剧情正文和剧情 Log");
        TranslateSubSkills = config.Bind("Translation", "TranslateSubSkills", true, "翻译副技能名称和说明");
        TranslateNames = config.Bind("Translation", "TranslateNames", true, "翻译剧情角色名称并替换名称字体");
        TranslateUiTexts = config.Bind("Translation", "TranslateUiTexts", true, "翻译 UICanvas 目录下 zh_Hans.json 中的手动 UI 文案");
        EnableUiSnapshotHotkey = config.Bind(
            "Debug",
            "EnableUiSnapshotHotkey",
            true,
            "按 F8 写入 ui_snapshot.tsv，并将当前未翻译日文追加到 missing.json");
        EnableSubSkillScanHotkey = config.Bind(
            "Debug",
            "EnableSubSkillScanHotkey",
            true,
            "按 F9 自动滚动副技能列表并写入 subskill_scan.tsv");
        Language = config.Bind("Translation", "Language", "zh_Hans", "翻译语言目录名");
        FontAssetPath = config.Bind("Translation.Font", "FontAssetPath", "", "剧情 TMP Font Asset 路径；留空不替换字体");
        EnableSpineMosaicReplacement = config.Bind(
            "Mosaic",
            "EnableSpineMosaicReplacement",
            true,
            "启用运行时 Spine 马赛克透明替换");
        HideR18DialogueBackground = config.Bind(
            "R18",
            "HideDialogueBackground",
            true,
            "在 _r18 剧情中隐藏对话框背景，保留文字和控制按钮");
        EnableR18WhiteTextOutline = config.Bind(
            "R18",
            "EnableWhiteTextOutline",
            true,
            "在 R18 剧情中为正文添加白色描边、为名称添加深棕色描边");
        EnableR18ButtonTextOutline = config.Bind(
            "R18",
            "EnableButtonTextOutline",
            true,
            "在 R18 剧情中为 Auto、Skip、Log 按钮文字添加 #544A4A 描边");
        UiTextOutlineWidth = config.Bind(
            "Translation.UIAppearance",
            "UiTextOutlineWidth",
            0.3f,
            "UICanvas 汉化文本描边宽度（建议 0 至 1）");
        UiTextFaceDilate = config.Bind(
            "Translation.UIAppearance",
            "UiTextFaceDilate",
            0.2f,
            "UICanvas 汉化字体增厚（建议 -1 至 1）");
        EnableTranslationAutoUpdate = config.Bind(
            "Translation.Update",
            "Enabled",
            true,
            "启动后在后台从翻译仓库同步新增、修改和已删除的受管文件");
        TranslationUpdateBaseUrl = config.Bind(
            "Translation.Update",
            "BaseUrl",
            "https://raw.githubusercontent.com/Berumic/MonsterMusumeTDChineseTranslation/main/translations",
            "翻译文件与 manifest.json 所在的远程基础地址");
        TranslationAssetUpdateBaseUrl = config.Bind(
            "Translation.Update",
            "AssetBaseUrl",
            "https://raw.githubusercontent.com/Berumic/MonsterMusumeTDChineseTranslation/main/assets",
            "字体资源与 assets/manifest.json 所在的远程基础地址");
        TranslationUpdateTimeoutSeconds = config.Bind(
            "Translation.Update",
            "TimeoutSeconds",
            10,
            "单次网络请求超时秒数（建议 3 至 60）");
    }

    public void Reload() => _config.Reload();

    public void Save() => _config.Save();
}
