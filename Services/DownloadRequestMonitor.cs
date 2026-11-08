using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonsterMusumeTDMod.Core;

namespace MonsterMusumeTDMod.Services;

/// <summary>
/// Monitors UnityWebRequest, UnityWebRequestAssetBundle, DownloadHandlerAssetBundle,
/// Addressables ResourceManager, Utage, and CriWare download requests and logs the
/// resource description and full source URL in the background console and optional log file.
/// </summary>
public static class DownloadRequestMonitor
{
    private const long DeduplicateWindowMs = 2500;
    private const int MaxRecentUrls = 2048;

    private static readonly object SyncLock = new();
    private static readonly Dictionary<string, long> RecentUrls = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> KnownAssetKeysByUrl = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, PropertyInfo> UrlPropertyCache = new();
    private static readonly Dictionary<Type, PropertyInfo> MethodPropertyCache = new();
    private static readonly Dictionary<Type, PropertyInfo> PrimaryKeyPropertyCache = new();
    private static readonly Dictionary<Type, PropertyInfo> InternalIdPropertyCache = new();

    private static ModConfig _config;
    private static string _logFilePath;
    private static int _patchedCount;

    public static void Install(HarmonyLib.Harmony harmony, ModConfig config, string pluginRoot = null)
    {
        _config = config;
        _patchedCount = 0;

        if (!string.IsNullOrEmpty(pluginRoot))
        {
            var modDir = Path.Combine(pluginRoot, "MonsterMusumeTDMod");
            if (!Directory.Exists(modDir))
                modDir = pluginRoot;
            _logFilePath = Path.Combine(modDir, "download_requests.log");
        }

        if (harmony == null)
            return;

        EnsureDownloadAssembliesLoaded();

        // 1. Addressables / ResourceManager internal ID transformation (captures logical PrimaryKey -> remote bundle URL)
        PatchTransformInternalId(harmony, "UnityEngine.ResourceManagement.ResourceManager");
        PatchTransformInternalId(harmony, "Il2CppUnityEngine.ResourceManagement.ResourceManager");

        // 2. High-level AssetBundle / Multimedia / Texture web request factories (Prefix so high-level source logs first)
        PatchUrlFactoryMethods(
            harmony,
            "UnityEngine.Networking.UnityWebRequestAssetBundle",
            new[] { "GetAssetBundle" },
            usePrefix: true);
        PatchUrlFactoryMethods(
            harmony,
            "UnityEngine.Networking.UnityWebRequestMultimedia",
            new[] { "GetAudioClip" },
            usePrefix: true);
        PatchUrlFactoryMethods(
            harmony,
            "UnityEngine.Networking.UnityWebRequestTexture",
            new[] { "GetTexture" },
            usePrefix: true);

        // 3. DownloadHandlerAssetBundle native creation helpers (second parameter is string url)
        PatchDownloadHandlerAssetBundle(harmony, "UnityEngine.Networking.DownloadHandlerAssetBundle");

        // 4. Addressables AssetBundleResource web request creation
        PatchAssetBundleResource(harmony, "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleResource");
        PatchAssetBundleResource(harmony, "Il2CppUnityEngine.ResourceManagement.ResourceProviders.AssetBundleResource");

        // 5. UnityWebRequest static factories, URL setters, and SendWebRequest execution
        PatchUrlFactoryMethods(
            harmony,
            "UnityEngine.Networking.UnityWebRequest",
            new[] { "Get", "Post", "Put", "Head", "Delete" },
            usePrefix: true);
        PatchSendWebRequest(
            harmony,
            "UnityEngine.Networking.UnityWebRequest",
            new[] { "SendWebRequest", "BeginWebRequest", "Send" });
        PatchUrlFactoryMethods(
            harmony,
            "UnityEngine.Networking.UnityWebRequest",
            new[] { "set_url", "SetUrl", "set_uri", "SetUri" },
            usePrefix: false);

        // 6. Utage & CriWare download entry points if present
        PatchUrlFactoryMethods(
            harmony,
            "Utage.AssetFileManager",
            new[] { "Download", "Load", "Preload", "BackGroundDownload" },
            usePrefix: true);
        PatchUrlFactoryMethods(
            harmony,
            "CriWare.CriFsWebInstaller",
            new[] { "Copy" },
            usePrefix: true);
        PatchUrlFactoryMethods(
            harmony,
            "CriFsWebInstaller",
            new[] { "Copy" },
            usePrefix: true);

        Plugin.Log?.LogInfo($"Installed {_patchedCount} download request monitor patches");
    }

    public static void Reset()
    {
        lock (SyncLock)
        {
            RecentUrls.Clear();
            KnownAssetKeysByUrl.Clear();
        }
    }

    private static void EnsureDownloadAssembliesLoaded()
    {
        var candidateAssemblyNames = new[]
        {
            "UnityEngine.UnityWebRequestModule",
            "UnityEngine.UnityWebRequestAssetBundleModule",
            "UnityEngine.UnityWebRequestAudioModule",
            "UnityEngine.UnityWebRequestTextureModule",
            "UnityEngine.UnityWebRequestWWWModule",
            "Unity.ResourceManager",
            "Unity.Addressables",
            "Il2CppUnity.ResourceManager",
            "Il2CppUnity.Addressables",
            "CriMw.CriWare.Runtime"
        };

        string interopDir = null;
        try
        {
            var coreLocation = typeof(UnityEngine.Object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLocation))
                interopDir = Path.GetDirectoryName(coreLocation);
        }
        catch
        {
            // Ignore when assembly location is unavailable.
        }

        foreach (var asmName in candidateAssemblyNames)
        {
            try
            {
                if (IsAssemblyLoaded(asmName))
                    continue;

                if (!string.IsNullOrEmpty(interopDir))
                {
                    var candidatePath = Path.Combine(interopDir, asmName + ".dll");
                    if (File.Exists(candidatePath))
                    {
                        Assembly.LoadFrom(candidatePath);
                        continue;
                    }
                }

                Assembly.Load(asmName);
            }
            catch
            {
                // Optional module may not exist in all game builds.
            }
        }
    }

    private static bool IsAssemblyLoaded(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (string.Equals(name, shortName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static Type FindTypeAnyAssembly(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = assembly.GetName().Name ?? string.Empty;
            if (asmName.Equals("0Harmony", StringComparison.Ordinal) ||
                asmName.StartsWith("BepInEx", StringComparison.Ordinal) ||
                asmName.StartsWith("MelonLoader", StringComparison.Ordinal) ||
                asmName.StartsWith("MonoMod", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var type = assembly.GetType(typeName, false, false);
                if (type != null)
                    return type;

                type = assembly.GetType("Il2Cpp" + (typeName.Contains('.') ? "" : ".") + typeName, false, false);
                if (type != null)
                    return type;

                if (!typeName.Contains('.'))
                {
                    type = assembly.GetType("Utage." + typeName, false, false) ??
                           assembly.GetType("Il2CppUtage." + typeName, false, false);
                    if (type != null)
                        return type;
                }
            }
            catch
            {
                // Ignore reflection errors on dynamic assemblies.
            }
        }
        return null;
    }

    private static void PatchUrlFactoryMethods(
        HarmonyLib.Harmony harmony,
        string typeName,
        string[] methodNames,
        bool usePrefix)
    {
        var type = FindTypeAnyAssembly(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!MatchesMethodName(method.Name, methodNames))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
                continue;

            var firstParamType = parameters[0].ParameterType;
            HarmonyMethod hookMethod = null;
            if (firstParamType == typeof(string))
            {
                hookMethod = new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnUrlStringArg));
            }
            else if (!firstParamType.IsValueType &&
                     (firstParamType.Name.Equals("Uri", StringComparison.Ordinal) ||
                      firstParamType.Name.Contains("AssetFile", StringComparison.Ordinal)))
            {
                hookMethod = new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnUriOrAssetObjectArg));
            }

            if (hookMethod == null)
                continue;

            try
            {
                if (usePrefix)
                    harmony.Patch(method, prefix: hookMethod);
                else
                    harmony.Patch(method, postfix: hookMethod);
                _patchedCount++;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Skip download monitor patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchDownloadHandlerAssetBundle(HarmonyLib.Harmony harmony, string typeName)
    {
        var type = FindTypeAnyAssembly(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name != "Create" && method.Name != "CreateCached")
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length >= 2 && parameters[1].ParameterType == typeof(string))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnUrlSecondStringArg)));
                    _patchedCount++;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug($"Skip download monitor patch {type.FullName}.{method.Name}: {ex.Message}");
                }
            }
        }
    }

    private static void PatchSendWebRequest(
        HarmonyLib.Harmony harmony,
        string typeName,
        string[] methodNames)
    {
        var type = FindTypeAnyAssembly(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (!MatchesMethodName(method.Name, methodNames) || method.GetParameters().Length != 0)
                continue;

            try
            {
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnSendWebRequest)));
                _patchedCount++;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Skip download monitor patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchTransformInternalId(HarmonyLib.Harmony harmony, string typeName)
    {
        var type = FindTypeAnyAssembly(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name != "TransformInternalId" ||
                method.ReturnType != typeof(string) ||
                method.GetParameters().Length != 1 ||
                method.GetParameters()[0].ParameterType.IsValueType)
            {
                continue;
            }

            try
            {
                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnTransformInternalId)));
                _patchedCount++;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Skip download monitor patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static void PatchAssetBundleResource(HarmonyLib.Harmony harmony, string typeName)
    {
        var type = FindTypeAnyAssembly(typeName);
        if (type == null)
            return;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name != "CreateWebRequest")
                continue;

            var parameters = method.GetParameters();
            try
            {
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnUrlStringArg)));
                    _patchedCount++;
                }
                else if (parameters.Length == 1 && !parameters[0].ParameterType.IsValueType)
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnCreateWebRequestFromLocation)));
                    _patchedCount++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug($"Skip download monitor patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private static bool MatchesMethodName(string name, string[] candidates)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(name, candidates[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void ReportDownload(string url, string callerSource, string assetKey = null)
    {
        if (_config != null && !_config.EnableDownloadMonitor.Value)
            return;

        if (string.IsNullOrWhiteSpace(url))
            return;

        url = url.Trim();
        if (!IsInterestingDownloadUrl(url))
            return;

        // Ignore the mod's own GitHub translation sync URLs if they ever pass through Unity.
        if (url.Contains("githubusercontent.com/Berumic/MonsterMusumeTDChineseTranslation", StringComparison.OrdinalIgnoreCase))
            return;

        if (_config != null &&
            _config.DownloadMonitorOnlyBundles.Value &&
            !IsBundleOrResourceUrl(url))
        {
            return;
        }

        var now = Environment.TickCount64;
        string resolvedAssetKey = assetKey;

        lock (SyncLock)
        {
            if (!string.IsNullOrEmpty(assetKey))
            {
                if (KnownAssetKeysByUrl.Count >= MaxRecentUrls)
                    KnownAssetKeysByUrl.Clear();
                KnownAssetKeysByUrl[url] = assetKey;
            }
            else if (KnownAssetKeysByUrl.TryGetValue(url, out var cachedKey))
            {
                resolvedAssetKey = cachedKey;
            }

            if (RecentUrls.TryGetValue(url, out var lastLoggedTick) &&
                now - lastLoggedTick < DeduplicateWindowMs)
            {
                return;
            }

            if (RecentUrls.Count >= MaxRecentUrls)
                RecentUrls.Clear();
            RecentUrls[url] = now;
        }

        var category = ClassifyResourceCategory(url, resolvedAssetKey);
        var fileName = ExtractResourceFileName(url);
        var activeScene = StorySceneContext.EffectivePlaybackId;
        if (string.IsNullOrEmpty(activeScene))
            activeScene = ScenarioContext.CurrentId;
        var activeResource = ScenarioContext.CurrentResourceId;

        var contextSuffix = string.Empty;
        if (!string.IsNullOrEmpty(resolvedAssetKey) &&
            !string.Equals(resolvedAssetKey, fileName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolvedAssetKey, url, StringComparison.OrdinalIgnoreCase))
        {
            contextSuffix += $" | 资产键: {resolvedAssetKey}";
        }
        if (!string.IsNullOrEmpty(activeScene))
            contextSuffix += $" | 剧情: {activeScene}";
        else if (!string.IsNullOrEmpty(activeResource))
            contextSuffix += $" | 资源ID: {activeResource}";

        var message =
            $"[下载监视] 正在下载{category}: {fileName} (来源: {callerSource}){contextSuffix}\n" +
            $"url : {url}";

        Plugin.Log?.LogInfo(message);

        if (_config == null || _config.SaveDownloadMonitorLog.Value)
            AppendToDownloadLogFile(category, fileName, callerSource, contextSuffix, url);
    }

    private static void AppendToDownloadLogFile(
        string category,
        string fileName,
        string callerSource,
        string contextSuffix,
        string url)
    {
        if (string.IsNullOrEmpty(_logFilePath))
            return;

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line =
                $"[{timestamp}] [{category}] {fileName} (来源: {callerSource}){contextSuffix}{Environment.NewLine}" +
                $"url : {url}{Environment.NewLine}";
            lock (SyncLock)
            {
                File.AppendAllText(_logFilePath, line);
            }
        }
        catch
        {
            // Ignore disk write contention.
        }
    }

    private static bool IsInterestingDownloadUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/aas/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/resources/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBundleOrResourceUrl(string url) =>
        url.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".acb", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".awb", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".usm", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/aas/", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/resources/", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyResourceCategory(string url, string assetKey)
    {
        var combined = string.IsNullOrEmpty(assetKey) ? url : (url + " " + assetKey);

        if (combined.Contains("novel", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("story", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("scenario", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("adv", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("utage", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("r18_", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("nml_", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("still", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("spine", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("stand", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("varianteffect", StringComparison.OrdinalIgnoreCase))
        {
            return "场景/剧情资源";
        }

        if (combined.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("bgm", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(".acb", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(".awb", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return "音频/语音资源";
        }

        if (combined.Contains("battle", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("unit", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("chara", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("skill", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("dungeon", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("enemy", StringComparison.OrdinalIgnoreCase))
        {
            return "战斗/角色资源";
        }

        if (IsBundleOrResourceUrl(url))
            return "场景/资源包";

        return "网络请求";
    }

    private static string ExtractResourceFileName(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        var clean = url;
        var queryIndex = clean.IndexOf('?');
        if (queryIndex >= 0)
            clean = clean[..queryIndex];

        var slashIndex = clean.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < clean.Length)
            return clean[(slashIndex + 1)..];

        return clean;
    }

    private static string FormatCallerSource(MethodBase method)
    {
        if (method == null)
            return "Unknown";
        var typeName = method.DeclaringType?.Name ?? "Runtime";
        return $"{typeName}.{method.Name}";
    }

    private static string TryReadUrlFromWebRequest(object webRequestInstance, out string httpMethod)
    {
        httpMethod = null;
        if (webRequestInstance == null)
            return null;

        try
        {
            var type = webRequestInstance.GetType();
            PropertyInfo urlProp;
            PropertyInfo methodProp;
            lock (SyncLock)
            {
                if (!UrlPropertyCache.TryGetValue(type, out urlProp))
                {
                    urlProp = type.GetProperty("url", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    UrlPropertyCache[type] = urlProp;
                }
                if (!MethodPropertyCache.TryGetValue(type, out methodProp))
                {
                    methodProp = type.GetProperty("method", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    MethodPropertyCache[type] = methodProp;
                }
            }

            httpMethod = methodProp?.GetValue(webRequestInstance) as string;
            return urlProp?.GetValue(webRequestInstance) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void TryReadResourceLocationInfo(object location, out string primaryKey, out string internalId)
    {
        primaryKey = null;
        internalId = null;
        if (location == null)
            return;

        try
        {
            var type = location.GetType();
            PropertyInfo pkProp;
            PropertyInfo idProp;
            lock (SyncLock)
            {
                if (!PrimaryKeyPropertyCache.TryGetValue(type, out pkProp))
                {
                    pkProp = type.GetProperty("PrimaryKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    PrimaryKeyPropertyCache[type] = pkProp;
                }
                if (!InternalIdPropertyCache.TryGetValue(type, out idProp))
                {
                    idProp = type.GetProperty("InternalId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    InternalIdPropertyCache[type] = idProp;
                }
            }

            primaryKey = pkProp?.GetValue(location) as string;
            internalId = idProp?.GetValue(location) as string;
        }
        catch
        {
            // Ignore reflection errors on non-standard IResourceLocation implementations.
        }
    }

    private static class Hooks
    {
        public static void OnUrlStringArg(string __0, MethodBase __originalMethod)
        {
            if (string.IsNullOrEmpty(__0))
                return;
            ReportDownload(__0, FormatCallerSource(__originalMethod));
        }

        public static void OnUrlSecondStringArg(string __1, MethodBase __originalMethod)
        {
            if (string.IsNullOrEmpty(__1))
                return;
            ReportDownload(__1, FormatCallerSource(__originalMethod));
        }

        public static void OnUriOrAssetObjectArg(object __0, MethodBase __originalMethod)
        {
            if (__0 == null)
                return;

            try
            {
                var text = __0.ToString();
                if (!string.IsNullOrEmpty(text))
                    ReportDownload(text, FormatCallerSource(__originalMethod));
            }
            catch
            {
                // Ignore ToString failure on disposed IL2CPP objects.
            }
        }

        public static void OnSendWebRequest(object __instance, MethodBase __originalMethod)
        {
            var url = TryReadUrlFromWebRequest(__instance, out var httpMethod);
            if (string.IsNullOrEmpty(url))
                return;

            var source = string.IsNullOrEmpty(httpMethod)
                ? FormatCallerSource(__originalMethod)
                : $"{FormatCallerSource(__originalMethod)} [{httpMethod}]";
            ReportDownload(url, source);
        }

        public static void OnTransformInternalId(object __0, string __result, MethodBase __originalMethod)
        {
            if (string.IsNullOrEmpty(__result))
                return;

            TryReadResourceLocationInfo(__0, out var primaryKey, out _);
            ReportDownload(__result, FormatCallerSource(__originalMethod), primaryKey);
        }

        public static void OnCreateWebRequestFromLocation(object __0, object __result, MethodBase __originalMethod)
        {
            TryReadResourceLocationInfo(__0, out var primaryKey, out var internalId);
            var url = TryReadUrlFromWebRequest(__result, out _);
            if (string.IsNullOrEmpty(url))
                url = internalId;
            if (!string.IsNullOrEmpty(url))
                ReportDownload(url, FormatCallerSource(__originalMethod), primaryKey);
        }
    }
}
