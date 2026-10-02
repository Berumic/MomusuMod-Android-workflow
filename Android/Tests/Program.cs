using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using MonsterMusumeTDMod.Android;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Patches;
using MonsterMusumeTDMod.Services;

var root = Path.Combine(Path.GetTempPath(), "monmusu-android-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
    checks++;
}
void Write(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(value));
}
byte[] Manifest(Dictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new {
    schemaVersion = 1, commit = "test", files = files.ToDictionary(p => p.Key, p => new {
        sha256 = Convert.ToHexString(SHA256.HashData(p.Value)).ToLowerInvariant(), size = p.Value.Length
    })
});
async Task<object> InvokeUpdate(string name, params object[] arguments)
{
    var method = typeof(TranslationUpdateController).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == name && m.GetParameters().Length == arguments.Length);
    var task = (Task)method.Invoke(null, arguments)!;
    await task;
    return task.GetType().GetProperty("Result")!.GetValue(task)!;
}
T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
try
{
    var material = new UnityEngine.Material { shaderKeywords = new[] { "OUTLINE_ON", "UNDERLAY_ON" } };
    Check(material.HasShaderKeyword("OUTLINE_ON") && material.HasShaderKeyword("UNDERLAY_ON"), "Android material reads native keyword list without stripped IsKeywordEnabled");
    Check(!material.HasShaderKeyword("UNDERLAY_INNER") && !material.HasShaderKeyword("outline_on"), "Shader keyword lookup uses exact case-sensitive matches");
    material.shaderKeywords = null;
    Check(!material.HasShaderKeyword("OUTLINE_ON"), "Empty material keyword list is supported");
    var settingsPath = Path.Combine(root, "config.json");
    var queue = new UiRefreshQueue<string>();
    Check(!queue.Enqueue(1, "first", 12, false) && queue.Enqueue(1, "latest", 10, true),
        "Repeated UI refresh requests merge");
    var pending = queue.Single().Value;
    Check(queue.Count == 1 && pending.Text == "latest" && pending.ReadyFrame == 10 && pending.IsActivation,
        "Merged refresh preserves latest slot and earliest activation");
    queue.Enqueue(1, "later", 20, false);
    Check(queue.Single().Value.ReadyFrame == 10 && queue.Single().Value.IsActivation,
        "Late setters cannot delay an activation refresh");
    var baseline = new StyleValues { FaceDilate = 0.35f, Outline = true };
    var style = UiStylePolicy.Resolve(new StyleValues { FaceDilate = 0f, Outline = false },
        new StyleValues { FaceDilate = 0.5f }, baseline, true);
    Check(style.FaceDilate == 0f && style.Outline == false, "Explicit zero and false styles override defaults");
    Check(ReferenceEquals(UiStylePolicy.Resolve(new StyleValues { TranslatedOnly = true }, null, baseline, false), baseline),
        "Translation-only styles preserve untranslated baseline");
    Check(UiStylePolicy.PathContains(UiStylePolicy.NormalizePath("Canvas/Card(Clone)/Text"), "Card/Text"),
        "Style paths match recycled clone slots");
    var gray = UiStylePolicy.InheritButtonGray(new UnityEngine.Color(0.5f, 0.5f, 0.5f, 1f),
        new UnityEngine.Color(0.4f, 0.4f, 0.4f, 0f));
    Check(gray.r == 1f && gray.a == 1f, "Already dimmed labels are not darkened twice");
    var config = new ModConfig(new ConfigFile(settingsPath));
    Check(config.UiTextFaceDilate.Value == 0.35f, "New UI face dilate default is 0.35");
    Check(config.FontAssetPath.Value == "alimama-android", "Android font filename is distinct from PC");
    Check(config.TranslationAssetUpdateBaseUrl.Value.EndsWith("/assets/android"), "Android font update channel");
    Check(!config.EnableUiSnapshotHotkey.Value && !config.EnableSubSkillScanHotkey.Value, "Desktop diagnostics disabled by default");
    config.Language.Value = "zh_Hans";
    config.UiTextOutlineWidth.Value = 0.42f;
    config.UiTextFaceDilate.Value = 0.2f;
    config.Save();
    var reloaded = new ModConfig(new ConfigFile(settingsPath));
    Check(reloaded.UiTextOutlineWidth.Value == 0.42f, "Configuration persists and reloads");
    Check(reloaded.UiTextFaceDilate.Value == 0.2f, "Saved UI face dilate remains user-controlled");
    var legacyPath = Path.Combine(root, "legacy-config.json");
    Write(legacyPath, new Dictionary<string, object> {
        ["Translation.UIAppearance.UiTextFaceDilate"] = 0.2f,
        ["Translation.UIAppearance.UiTextOutlineWidth"] = 0.42f
    });
    var legacy = new ModConfig(new ConfigFile(legacyPath));
    Check(legacy.UiTextFaceDilate.Value == 0.35f && legacy.UiTextOutlineWidth.Value == 0.42f,
        "Legacy default migrates without changing unrelated settings");
    legacy.Save();
    Check(new ModConfig(new ConfigFile(legacyPath)).UiTextFaceDilate.Value == 0.35f,
        "Migrated face dilate persists across restarts");
    legacy.UiTextFaceDilate.Value = 0.2f;
    legacy.Save();
    Check(new ModConfig(new ConfigFile(legacyPath)).UiTextFaceDilate.Value == 0.2f,
        "Migration runs only once and permits a later custom 0.2");
    Write(legacyPath, new Dictionary<string, object> { ["Translation.UIAppearance.UiTextFaceDilate"] = 0.27f });
    Check(new ModConfig(new ConfigFile(legacyPath)).UiTextFaceDilate.Value == 0.27f,
        "Legacy nondefault face dilate is preserved");
    File.WriteAllText(settingsPath, "{invalid");
    reloaded.Reload();
    Check(reloaded.UiTextOutlineWidth.Value == 0.42f, "Invalid JSON preserves active configuration");

    var translationRoot = Path.Combine(root, "MonsterMusumeTDMod", "translations");
    Write(Path.Combine(translationRoot, "names", "zh_Hans.json"), new Dictionary<string,string> {
        ["\u30a2\u30ea\u30b9"] = "Alice", ["\u30dc\u30d6"] = "Bob"
    });
    Write(Path.Combine(translationRoot, "UICanvas", "zh_Hans.json"), new Dictionary<string,string> {
        ["\u653b\u6483<num>%"] = "Attack <num>%",
        ["\u7372\u5f97<ex>\u5b8c\u4e86"] = "Acquired <ex>",
        ["\u653b\u648399%"] = "Exact99"
    });
    Write(Path.Combine(translationRoot, "subskills", "zh_Hans.json"), new Dictionary<string,string> {
        ["\u653b\u6483\u529b<num>%\u4e0a\u6607"] = "Attack +<num>%"
    });
    var storySource = "\u3042<interval=0.5><color=red>\u3044</color>";
    var storyTarget = "A<interval=0.5><color=red>B</color>";
    Write(Path.Combine(translationRoot, "scenarios", "test", "zh_Hans.json"), new Dictionary<string,string> { [storySource] = storyTarget });
    var translations = new TranslationManager(root, config);
    translations.LoadStatic();
    Check(translations.TryTranslateName("\u30a2\u30ea\u30b9&\u30dc\u30d6", out var result) && result == "Alice&Bob", "Ampersand name splitting");
    Check(translations.TryTranslateName("\u30a2\u30ea\u30b9\uff06\u30dc\u30d6", out result) && result == "Alice\uff06Bob", "Full-width ampersand splitting");
    Check(!translations.TryTranslateName("\u30a2\u30ea\u30b9&unknown", out _), "Unknown joined names are preserved");
    Check(translations.TryTranslateUiText("\u653b\u648312.5%", out result) && result == "Attack 12.5%", "Numeric placeholder preserves decimal value");
    Check(translations.TryTranslateUiText("\u653b\u648399%", out result) && result == "Exact99", "Exact match wins over numeric template");
    Check(translations.TryTranslateUiText("\u7372\u5f97<color=red>ITEM</color>\u5b8c\u4e86", out result) && result == "Acquired <color=red>ITEM</color>", "Arbitrary text placeholder preserves rich text");
    Check(translations.TryTranslateSubSkill("\u653b\u6483\u529b25%\u4e0a\u6607", out result) && result == "Attack +25%", "Sub-skill numeric placeholder");
    Check(translations.TryTranslateStoryBeforeParse(storySource, out result) && result == storyTarget, "UTAGE pre-parse translation preserves interval and rich text");
    config.Enabled.Value = false;
    Check(!translations.TryTranslateUiText("\u653b\u648312.5%", out _), "Master switch disables translation");
    config.Enabled.Value = true;

    var lru = new BoundedLruCache<string, bool>(2);
    lru.Set("hot", false);
    lru.Set("cold", true);
    Check(lru.TryGetValue("hot", out var negative) && !negative, "Negative cache hits are retained");
    lru.Set("new", true);
    Check(lru.Count == 2 && !lru.TryGetValue("cold", out _) && lru.TryGetValue("hot", out _), "Eviction preserves hot entries");
    lru.Set("hot", true);
    Check(lru.Count == 2 && lru.TryGetValue("hot", out var replaced) && replaced, "Cached values can be replaced");
    lru.Clear();
    Check(lru.Count == 0 && !lru.TryGetValue("hot", out _), "Cache clear removes results");
    Check(translations.IsKnownUiTextTranslationValue("Attack 12.5%") && translations.IsKnownUiTextTranslationValue("Attack 12.5%"), "Repeated UI template recognition");
    Check(translations.IsKnownSubSkillTranslationValue("Attack +25%") && translations.IsKnownSubSkillTranslationValue("Attack +25%"), "Repeated subskill template recognition");
    Check(!translations.IsKnownTranslationValue("new target") && !translations.IsKnownUiTextTranslationValue("new target") && !translations.IsKnownSubSkillTranslationValue("new target"), "Unknown values are cached by category");
    Check(!translations.TryTranslateSubSkill("missing", out _) && !translations.TryTranslateSubSkill("missing", out _), "Repeated subskill misses");
    Write(Path.Combine(translationRoot, "subskills", "zh_Hans.json"), new Dictionary<string,string> { ["missing"] = "new target" });
    Write(Path.Combine(translationRoot, "UICanvas", "zh_Hans.json"), new Dictionary<string,string> { ["missing"] = "new target" });
    translations.LoadStatic();
    Check(translations.TryTranslateSubSkill("missing", out result) && result == "new target", "Reload invalidates subskill misses");
    Check(translations.IsKnownTranslationValue("new target") && translations.IsKnownUiTextTranslationValue("new target") && translations.IsKnownSubSkillTranslationValue("new target"), "Reload invalidates all known-value misses");
    Check(!translations.IsKnownUiTextTranslationValue("Attack 12.5%") && !translations.IsKnownSubSkillTranslationValue("Attack +25%"), "Reload removes cached template hits");
    config.Enabled.Value = false;
    Check(!translations.TryTranslateSubSkill("missing", out _), "Master switch bypasses subskill cache");
    config.Enabled.Value = true;

    const string sharedStory = "\u3042<interval=0.5>\u3044";
    const string sceneATarget = "Long translated A<interval=0.2>tail";
    const string sceneBTarget = "B<interval=0.8>different";
    Write(Path.Combine(translationRoot, "scenarios", "nested", "sceneA", "zh_Hans.json"), new Dictionary<string,string> {
        [sharedStory] = sceneATarget,
        ["line\nnext<interval=1>end"] = "translated\nlong next<interval=2>end",
        ["unique"] = "unique translated"
    });
    Write(Path.Combine(translationRoot, "scenarios", "sceneB", "zh_Hans.json"), new Dictionary<string,string> { [sharedStory] = sceneBTarget });
    translations.LoadStatic();
    Check(translations.TryTranslateStoryBeforeParse(sharedStory, "sceneA", out result) && result == sceneATarget,
        "Scene A supplies its own translated timing positions");
    Check(translations.TryTranslateStoryBeforeParse(sharedStory, "sceneB", out result) && result == sceneBTarget,
        "Scene B supplies a different translation of the same source");
    Check(!translations.TryTranslateStoryBeforeParse(sharedStory, "", out result) && result == sharedStory,
        "Unknown scene preserves ambiguous raw story");
    Check(translations.TryTranslateStoryExact("\u3042\u3044", "sceneB", null, out result) && result == sceneBTarget,
        "Display text without interval tags uses scene index");
    Check(!translations.TryTranslateStoryExact("\u3042\u3044", "", null, out _),
        "Display fallback refuses ambiguous story rows");
    Check(!translations.TryTranslateStoryExact("prefix " + sharedStory, "", null, out _),
        "Fragment fallback cannot reintroduce ambiguous translations");
    Check(translations.TryTranslateStoryBeforeParse("unique", "", out result) && result == "unique translated",
        "Unique global row remains available without scene identity");
    Check(translations.TryTranslateStoryBeforeParse("line\r\nnext<interval=1>end", "sceneA", out result) && result == "translated\nlong next<interval=2>end",
        "CRLF lookup preserves translated interval tags before parsing");
    Check(!translations.TryTranslateStoryBeforeParse(sceneATarget, "sceneA", out _),
        "Translated parser input is not translated again");
    Check(translations.ResolveStoryScenarioId("book/sceneA.bytes:sheet") == "sceneA" &&
        translations.ResolveStoryScenarioId("*sceneB") == "sceneB", "Book names and labels resolve scene identity");
    StorySceneContext.RegisterLabel("label-A", "sceneA");
    StorySceneContext.RegisterLabel("shared-label", "sceneA");
    StorySceneContext.RegisterLabel("shared-label", "sceneB");
    Check(StorySceneContext.ResolveLabel("label-A") == "sceneA" && StorySceneContext.ResolveLabel("shared-label") == "",
        "Conflicting labels cannot select an arbitrary scene");
    StorySceneContext.SetPlayback("sceneA");
    Check(translations.TryTranslateStoryBeforeParse(sharedStory, out result) && result == sceneATarget,
        "Playback context reaches parser translation");
    using (StorySceneContext.EnterParsing("sceneB"))
    {
        Check(translations.TryTranslateStoryBeforeParse(sharedStory, out result) && result == sceneBTarget,
            "Preloaded script overrides the playing scene");
        using (StorySceneContext.EnterParsing(""))
            Check(!translations.TryTranslateStoryBeforeParse(sharedStory, out _), "Unknown preload suppresses playing scene");
        Check(StorySceneContext.ParsingId == "sceneB", "Nested parsing scope restores enclosing scene");
    }
    Check(StorySceneContext.ParsingId == "sceneA", "Parsing scope restores playback context");
    StorySceneContext.ClearPlayback();
    Check(StorySceneContext.ParsingId == "", "Playback end clears story context");
    config.TranslateScenarios.Value = false;
    Check(!translations.TryTranslateStoryBeforeParse(sharedStory, "sceneA", out _), "Scenario toggle disables preparse translation");
    config.TranslateScenarios.Value = true;
    Write(Path.Combine(translationRoot, "scenarios", "sceneB", "zh_Hans.json"), new Dictionary<string,string> { [sharedStory] = sceneATarget });
    translations.LoadStatic();
    Check(translations.TryTranslateStoryBeforeParse(sharedStory, "", out result) && result == sceneATarget,
        "Translation reload rebuilds conflict indexes");

    using var server = new FixtureServer();
    var updateRoot = Path.Combine(root, "updates");
    Directory.CreateDirectory(updateRoot);
    var old = Encoding.UTF8.GetBytes("{\"old\":\"value\"}");
    var current = Encoding.UTF8.GetBytes("{\"new\":\"value\"}");
    File.WriteAllBytes(Path.Combine(updateRoot, "removed.json"), old);
    File.WriteAllBytes(Path.Combine(updateRoot, "manifest.json"), Manifest(new() { ["removed.json"] = old }));
    File.WriteAllText(Path.Combine(updateRoot, "missing.json"), "{}");
    server.Files["/manifest.json"] = Manifest(new() { ["names/zh_Hans.json"] = current });
    server.Files["/names/zh_Hans.json"] = current;
    var update = await InvokeUpdate("SynchronizeAsync", updateRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null && Property<int>(update, "Downloaded") == 1 && Property<int>(update, "Deleted") == 1, "Incremental update adds files and removes obsolete managed files");
    Check(File.Exists(Path.Combine(updateRoot, "missing.json")), "Unmanaged diagnostics survive updates");
    update = await InvokeUpdate("SynchronizeAsync", updateRoot, server.Url, 3);
    Check(!Property<bool>(update, "Updated") && Property<int>(update, "Unchanged") == 1, "Unchanged update avoids re-downloading");
    server.Files["/manifest.json"] = Manifest(new() { ["names/zh_Hans.json"] = old });
    update = await InvokeUpdate("SynchronizeAsync", updateRoot, server.Url, 3);
    Check(Property<string>(update, "Error") != null && File.ReadAllBytes(Path.Combine(updateRoot, "names/zh_Hans.json")).SequenceEqual(current), "Bad SHA-256 cannot replace working translations");
    server.Files["/manifest.json"] = Manifest(new() { ["../escaped.json"] = current });
    update = await InvokeUpdate("SynchronizeAsync", updateRoot, server.Url, 3);
    Check(Property<string>(update, "Error") != null && !File.Exists(Path.Combine(root, "escaped.json")), "Manifest path traversal rejected");

    var resumeRoot = Path.Combine(root, "resume", "translations");
    var goodBytes = Encoding.UTF8.GetBytes("{\"ready\":\"cached\"}");
    var nextBytes = Encoding.UTF8.GetBytes("{\"next\":\"download\"}");
    var rangeBytes = Encoding.UTF8.GetBytes("{\"range\":\"resume a partially downloaded file\"}");
    server.Files["/manifest.json"] = Manifest(new() { ["ready.json"] = goodBytes, ["next.json"] = nextBytes });
    server.Files["/ready.json"] = goodBytes;
    update = await InvokeUpdate("SynchronizeAsync", resumeRoot, server.Url, 3);
    Check(Property<string>(update, "Error") != null && !File.Exists(Path.Combine(resumeRoot, "ready.json")),
        "Interrupted batch keeps existing installation unchanged");
    Check(Property<string>(update, "Error").Contains("stage=download.headers") &&
        Property<string>(update, "Error").Contains("file=next.json") &&
        Property<string>(update, "Error").Contains("reason=HTTP 404") &&
        Property<string>(update, "Error").Contains("url=" + server.Url),
        "File failure identifies request phase, path, URL and HTTP status");
    var readyRequests = server.RequestCounts["/ready.json"];
    server.Files["/next.json"] = nextBytes;
    update = await InvokeUpdate("SynchronizeAsync", resumeRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null && Property<int>(update, "Resumed") == 1 &&
        server.RequestCounts["/ready.json"] == readyRequests &&
        File.ReadAllBytes(Path.Combine(resumeRoot, "ready.json")).SequenceEqual(goodBytes),
        "Next sync reuses completed files instead of downloading the batch again");
    var partialRoot = Path.Combine(root, "partial", "translations");
    server.Files["/manifest.json"] = Manifest(new() { ["range.json"] = rangeBytes });
    server.Files["/range.json"] = rangeBytes;
    server.TruncateOnce["/range.json"] = 17;
    update = await InvokeUpdate("SynchronizeAsync", partialRoot, server.Url, 3);
    var cacheRoot = Path.Combine(root, "partial", ".translation-update-cache");
    Check(Property<string>(update, "Error") != null && Directory.GetFiles(cacheRoot, "*.part").Length == 1 &&
        new FileInfo(Directory.GetFiles(cacheRoot, "*.part")[0]).Length == 17,
        "Connection interruption retains downloaded bytes on disk");
    Check(Property<string>(update, "Error").Contains("stage=download.body") &&
        Property<string>(update, "Error").Contains("idle_timeout=3s"),
        "Interrupted response reports body phase and effective timeout");
    server.ServeRanges = true;
    update = await InvokeUpdate("SynchronizeAsync", partialRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null && server.RangeStarts["/range.json"] == 17 &&
        File.ReadAllBytes(Path.Combine(partialRoot, "range.json")).SequenceEqual(rangeBytes),
        "HTTP Range resumes a partial file from its previous byte offset");
    var fallbackRoot = Path.Combine(root, "range-fallback", "translations");
    server.ServeRanges = false;
    server.TruncateOnce["/range.json"] = 11;
    update = await InvokeUpdate("SynchronizeAsync", fallbackRoot, server.Url, 3);
    update = await InvokeUpdate("SynchronizeAsync", fallbackRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null &&
        File.ReadAllBytes(Path.Combine(fallbackRoot, "range.json")).SequenceEqual(rangeBytes),
        "Servers ignoring Range restart only the partial file without appending duplicate bytes");
    // Repository changed during the interrupted update: stale cached content
    // must never be installed in place of the latest manifest's hash.
    var changedRoot = Path.Combine(root, "changed", "translations");
    server.Files["/manifest.json"] = Manifest(new() { ["ready.json"] = goodBytes, ["next.json"] = nextBytes });
    server.Files.TryRemove("/next.json", out _);
    update = await InvokeUpdate("SynchronizeAsync", changedRoot, server.Url, 3);
    server.Files["/manifest.json"] = Manifest(new() { ["ready.json"] = rangeBytes });
    server.Files["/ready.json"] = rangeBytes;
    update = await InvokeUpdate("SynchronizeAsync", changedRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null &&
        File.ReadAllBytes(Path.Combine(changedRoot, "ready.json")).SequenceEqual(rangeBytes),
        "New manifest hash replaces stale cached content after interrupted update");
    var corruptRoot = Path.Combine(root, "corrupt-cache", "translations");
    var corruptCache = Path.Combine(root, "corrupt-cache", ".translation-update-cache");
    Directory.CreateDirectory(corruptCache);
    var goodHash = Convert.ToHexString(SHA256.HashData(goodBytes)).ToLowerInvariant();
    File.WriteAllBytes(Path.Combine(corruptCache, goodHash + ".bin"), rangeBytes);
    server.Files["/manifest.json"] = Manifest(new() { ["ready.json"] = goodBytes });
    server.Files["/ready.json"] = goodBytes;
    update = await InvokeUpdate("SynchronizeAsync", corruptRoot, server.Url, 3);
    Check(Property<string>(update, "Error") == null && Property<int>(update, "Resumed") == 0 &&
        File.ReadAllBytes(Path.Combine(corruptRoot, "ready.json")).SequenceEqual(goodBytes),
        "Corrupt completed cache is re-downloaded and never installed");
    Check(!Directory.EnumerateFiles(corruptCache).Any(), "Successful install releases redundant download cache");

    server.Files.TryRemove("/manifest.json", out _);
    update = await InvokeUpdate("SynchronizeAsync", Path.Combine(root, "missing-manifest"), server.Url, 3);
    Check(Property<string>(update, "Error").Contains("stage=manifest.headers") &&
        Property<string>(update, "Error").Contains("file=manifest.json") &&
        Property<string>(update, "Error").Contains("reason=HTTP 404"),
        "Manifest failure is distinguished from translation file failure");
    server.Files["/manifest.json"] = Manifest(new() { ["ready.json"] = goodBytes });
    server.DelayOnce["/manifest.json"] = 3500;
    update = await InvokeUpdate("SynchronizeAsync", Path.Combine(root, "timeout-manifest"), server.Url, 3);
    Check(Property<string>(update, "Error").Contains("stage=manifest.headers") &&
        Property<string>(update, "Error").Contains("reason=idle-timeout") &&
        Property<string>(update, "Error").Contains("idle_timeout=3s"),
        "Timeout identifies manifest request phase and configured request limit");
    server.StreamDelayOnce["/manifest.json"] = 1200;
    server.StreamDelayOnce["/ready.json"] = 1200;
    update = await InvokeUpdate("SynchronizeAsync", Path.Combine(root, "slow-stream"), server.Url, 3);
    Check(Property<string>(update, "Error") == null && Property<bool>(update, "Updated"),
        "Continuous manifest and file streams can exceed the idle timeout in total duration");
    using (var stalledServer = new FixtureServer())
    {
        stalledServer.Files["/manifest.json"] = server.Files["/manifest.json"];
        stalledServer.StreamDelayOnce["/manifest.json"] = 3500;
        update = await InvokeUpdate("SynchronizeAsync", Path.Combine(root, "stalled-stream"), stalledServer.Url, 3);
        Check(Property<string>(update, "Error").Contains("stage=manifest.body") &&
            Property<string>(update, "Error").Contains("reason=idle-timeout"),
            "Stalled response body triggers idle timeout");
    }
    using (var limit = new TranslationUpdateController.RequestTimeout(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300)))
    {
        try { await Task.Delay(2000, limit.Token); Check(false, "Total request deadline cancels the request"); }
        catch (OperationCanceledException ex)
        {
            Check(limit.DescribeFailure(ex).Message.StartsWith("total-timeout"),
                "Total request deadline is distinguishable from idle timeout");
        }
    }
    var safeUrl = (string)typeof(TranslationUpdateController).GetMethod("SafeLogUrl", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { "https://user:secret@example.com/translations/manifest.json?token=private#fragment" })!;
    Check(safeUrl == "https://example.com/translations/manifest.json", "Logged URLs strip credentials, query tokens and fragments");
    var fonts = Path.Combine(root, "fonts");
    Directory.CreateDirectory(fonts);
    File.WriteAllBytes(Path.Combine(fonts, "alimama-android"), old);
    server.Files["/manifest.json"] = Manifest(new() { ["alimama-android"] = current });
    server.Files["/alimama-android"] = current;
    var fontUpdate = await InvokeUpdate("SynchronizeFontAssetAsync", fonts, server.Url, 3);
    Check(Property<bool>(fontUpdate, "Downloaded") && File.ReadAllBytes(Path.Combine(fonts, "alimama-android")).SequenceEqual(old), "Font download is staged without replacing the loaded font");
    TranslationUpdateController.ApplyPendingAssetUpdate(fonts);
    Check(File.ReadAllBytes(Path.Combine(fonts, "alimama-android")).SequenceEqual(current) && !File.Exists(Path.Combine(fonts, "alimama-android.pending")), "Verified pending font installs at next startup");
    File.WriteAllBytes(Path.Combine(fonts, "alimama-android.pending"), old);
    File.WriteAllBytes(Path.Combine(fonts, "MonsterMusumeTDMod", "assets-manifest.pending.json"), Manifest(new() { ["alimama-android"] = current }));
    TranslationUpdateController.ApplyPendingAssetUpdate(fonts);
    Check(File.ReadAllBytes(Path.Combine(fonts, "alimama-android")).SequenceEqual(current), "Corrupt pending font cannot overwrite installed font");
    server.Files["/manifest.json"] = Manifest(new() { ["alimama"] = current });
    fontUpdate = await InvokeUpdate("SynchronizeFontAssetAsync", fonts, server.Url, 3);
    Check(Property<string>(fontUpdate, "Error") != null, "PC-only font manifest is rejected");

    var resumeFonts = Path.Combine(root, "resume-fonts");
    server.Files["/manifest.json"] = Manifest(new() { ["alimama-android"] = rangeBytes });
    server.Files["/alimama-android"] = rangeBytes;
    server.TruncateOnce["/alimama-android"] = 13;
    server.ServeRanges = true;
    fontUpdate = await InvokeUpdate("SynchronizeFontAssetAsync", resumeFonts, server.Url, 3);
    Check(Property<string>(fontUpdate, "Error") != null, "Interrupted font download reports failure without installing it");
    fontUpdate = await InvokeUpdate("SynchronizeFontAssetAsync", resumeFonts, server.Url, 3);
    Check(Property<string>(fontUpdate, "Error") == null && server.RangeStarts["/alimama-android"] == 13 &&
        File.ReadAllBytes(Path.Combine(resumeFonts, "alimama-android.pending")).SequenceEqual(rangeBytes),
        "Font download also resumes previously received bytes");
    var fontRequests = server.RequestCounts["/alimama-android"];
    fontUpdate = await InvokeUpdate("SynchronizeFontAssetAsync", resumeFonts, server.Url, 3);
    Check(server.RequestCounts["/alimama-android"] == fontRequests,
        "Completed pending font is reused without re-downloading");

    var workspace = args.FirstOrDefault() ?? @"D:\mms\MomusuMod-Android";
    using var game = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Interop", "Assembly-CSharp.dll"));
    using var tmp = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Interop", "Unity.TextMeshPro.dll"));
    Check(game.MainModule.Types.Any(t => t.FullName == "Il2Cpp.SpineMosaic"), "Android APK contains SpineMosaic");
    var utage = game.MainModule.Types.Single(t => t.FullName == "Il2CppUtage.TextData");
    Check(utage.Methods.Any(m => m.Name == "get_CreateCustomTextParser") &&
        utage.Methods.Any(m => m.Name == "set_CreateCustomTextParser"), "Android UTAGE exposes custom parser callback");
    bool InvokesNative(MethodDefinition method) => method.HasBody && method.Body.Instructions.Any(i =>
        i.Operand is MethodReference called && called.Name == "il2cpp_runtime_invoke");
    var parserConstructor = game.MainModule.Types.Single(t => t.FullName == "Il2CppUtage.TextParser").Methods.Single(m =>
        m.Name == ".ctor" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String");
    Check(InvokesNative(parserConstructor), "Translated input reaches native UTAGE parser");
    foreach (var (typeName, methodName) in new[] {
        ("AdvScenarioData", "Init"), ("AdvCommand", "ParseCellLocalizedText"), ("AdvCommandText", "DoCommand"),
        ("AdvScenarioPlayer", "StartScenario"), ("AdvScenarioThread", "StartScenario"), ("AdvScenarioPlayer", "EndScenario") })
    {
        var method = game.MainModule.Types.Single(t => t.FullName == "Il2CppUtage." + typeName).Methods.Single(m => m.Name == methodName);
        Check(InvokesNative(method) && (methodName != "StartScenario" || method.Parameters[0].ParameterType.FullName == "System.String"),
            "Android scene hook has native entry point: " + typeName + "." + methodName);
    }
    Check(utage.Methods.Any(m => m.Name == ".ctor" && m.Parameters.Any(p => p.ParameterType.FullName == "System.String")), "Android UTAGE has raw string constructor hook");
    Check(tmp.MainModule.Types.Single(t => t.FullName == "Il2CppTMPro.TMP_Text").Methods.Any(m => m.Name == "set_text"), "Android TMP setter hook exists");
    using var mod = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Build", "MonsterMusumeTDMod.Android.dll"));
    Check(!mod.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("BepInEx")), "Android DLL has no BepInEx dependency");
    Check(mod.CustomAttributes.Any(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute"), "Android DLL declares MelonLoader entry point");
    IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
    var methodsCalled = AllTypes(mod.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
    Check(methodsCalled.Any(m => m.DeclaringType.FullName == "Il2CppUtage.TextData" && m.Name == "set_CreateCustomTextParser"),
        "Built plugin installs native custom parser callback");
    var parserCallback = mod.MainModule.Types.Single(t => t.Name == "PatchManager").Methods.Single(m => m.Name == "ParseStoryText");
    Check(!parserCallback.Body.Instructions.Any(i => i.Operand is MethodReference called &&
        called.DeclaringType.FullName == "Il2CppUtage.TextData" && called.Name == ".ctor"), "Parser callback cannot recursively construct TextData");
    Check(!methodsCalled.Any(m => m.DeclaringType.FullName == "UnityEngine.Material" && (m.Name == "IsKeywordEnabled" || m.Name == "set_globalIlluminationFlags")), "Built Android DLL avoids stripped material APIs in every style path");
    Check(!methodsCalled.Any(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name == "FindObjectsByType"), "Built Android scanner avoids stripped discovery API");
    using var unity = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Interop", "UnityEngine.CoreModule.dll"));
    var nativeMaterial = unity.MainModule.Types.Single(t => t.FullName == "UnityEngine.Material");
    var keywordGetter = nativeMaterial.Methods.Single(m => m.Name == "get_shaderKeywords");
    Check(keywordGetter.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "il2cpp_runtime_invoke"), "Replacement keyword API has a real native method in Android APK");
    Console.WriteLine($"All {checks} checks passed. Unity rendering and ARM64 execution require a device.");
}
finally { Directory.Delete(root, true); }

sealed class FixtureServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, int> RequestCounts { get; } = new();
    public ConcurrentDictionary<string, long> RangeStarts { get; } = new();
    public ConcurrentDictionary<string, int> TruncateOnce { get; } = new();
    public ConcurrentDictionary<string, int> DelayOnce { get; } = new();
    public ConcurrentDictionary<string, int> StreamDelayOnce { get; } = new();
    public bool ServeRanges { get; set; }
    public string Url { get; }
    public FixtureServer()
    {
        _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(async () => {
            try {
                while (!_stop.IsCancellationRequested) {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var request = await reader.ReadLineAsync();
                    long rangeStart = 0;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) {
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                            long.TryParse(line[13..].TrimEnd('-'), out rangeStart);
                    }
                    var path = Uri.UnescapeDataString(request!.Split(' ')[1]);
                    if (DelayOnce.TryRemove(path, out var delay)) await Task.Delay(delay, _stop.Token);
                    RequestCounts.AddOrUpdate(path, 1, (_, count) => count + 1);
                    if (rangeStart > 0) RangeStarts[path] = rangeStart;
                    var found = Files.TryGetValue(path, out var body);
                    body ??= Array.Empty<byte>();
                    var partial = found && ServeRanges && rangeStart > 0;
                    var offset = partial ? (int)rangeStart : 0;
                    var contentRange = partial ? $"Content-Range: bytes {offset}-{body.Length - 1}/{body.Length}\r\n" : "";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? partial ? "206 Partial Content" : "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length - offset}\r\n{contentRange}Connection: close\r\n\r\n");
                    try {
                        await stream.WriteAsync(header);
                        var count = TruncateOnce.TryRemove(path, out var truncated) ? truncated : body.Length - offset;
                        if (StreamDelayOnce.TryRemove(path, out var streamDelay)) {
                            var chunkSize = Math.Max(1, (count + 3) / 4);
                            for (var sent = 0; sent < count; sent += chunkSize) {
                                await stream.WriteAsync(body.AsMemory(offset + sent, Math.Min(chunkSize, count - sent)));
                                if (sent + chunkSize < count) await Task.Delay(streamDelay, _stop.Token);
                            }
                        } else await stream.WriteAsync(body.AsMemory(offset, count));
                    } catch (IOException) {
                        // Timeout tests intentionally disconnect before the response.
                    }
                }
            } catch (OperationCanceledException) { }
        });
    }
    public void Dispose()
    {
        _stop.Cancel();
        _loop.GetAwaiter().GetResult();
        _listener.Stop();
        _stop.Dispose();
    }
}
