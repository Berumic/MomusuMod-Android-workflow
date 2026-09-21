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
    var method = typeof(TranslationUpdateController).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
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

    var workspace = args.FirstOrDefault() ?? @"D:\mms\MomusuMod-Android";
    using var game = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Interop", "Assembly-CSharp.dll"));
    using var tmp = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Interop", "Unity.TextMeshPro.dll"));
    Check(game.MainModule.Types.Any(t => t.FullName == "Il2Cpp.SpineMosaic"), "Android APK contains SpineMosaic");
    var utage = game.MainModule.Types.Single(t => t.FullName == "Il2CppUtage.TextData");
    Check(utage.Methods.Any(m => m.Name == ".ctor" && m.Parameters.Any(p => p.ParameterType.FullName == "System.String")), "Android UTAGE has raw string constructor hook");
    Check(tmp.MainModule.Types.Single(t => t.FullName == "Il2CppTMPro.TMP_Text").Methods.Any(m => m.Name == "set_text"), "Android TMP setter hook exists");
    using var mod = AssemblyDefinition.ReadAssembly(Path.Combine(workspace, "Build", "MonsterMusumeTDMod.Android.dll"));
    Check(!mod.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("BepInEx")), "Android DLL has no BepInEx dependency");
    Check(mod.CustomAttributes.Any(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute"), "Android DLL declares MelonLoader entry point");
    IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
    var methodsCalled = AllTypes(mod.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
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
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var path = Uri.UnescapeDataString(request!.Split(' ')[1]);
                    var found = Files.TryGetValue(path, out var body);
                    body ??= Array.Empty<byte>();
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(body);
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
