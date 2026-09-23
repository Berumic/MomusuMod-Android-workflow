using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#if ANDROID
using Paths = MonsterMusumeTDMod.Android.AndroidPaths;
#else
using BepInEx;
#endif
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

/// <summary>Synchronizes repository-managed translation files without blocking game startup.</summary>
public sealed class TranslationUpdateController : MonoBehaviour
{
    private static TranslationUpdateController _instance;
    private Task<UpdateResult> _updateTask;
    private Task<AssetUpdateResult> _assetUpdateTask;
    private bool _handled;
    private bool _assetHandled;

#if ANDROID
    private const string FontAssetName = "alimama-android";
#else
    private const string FontAssetName = "alimama";
#endif
    private const string PendingSuffix = ".pending";

    public TranslationUpdateController(IntPtr pointer)
        : base(pointer)
    {
    }

    public static void RequestManualSync()
    {
        if (_instance == null)
        {
            Plugin.Log?.LogWarning("F6 translation sync skipped: update controller is not ready");
            return;
        }
        _instance.BeginTranslationSync(true);
    }

    public void Start()
    {
        _instance = this;
        if (Plugin.Settings?.EnableTranslationAutoUpdate.Value != true)
            return;

        BeginTranslationSync(false);
    }

    private void BeginTranslationSync(bool manual)
    {
        if ((_updateTask != null && (!_updateTask.IsCompleted || !_handled)) ||
            (_assetUpdateTask != null && (!_assetUpdateTask.IsCompleted || !_assetHandled)))
        {
            Plugin.Log?.LogInfo("Translation sync is already running");
            return;
        }

        var translationRoot = Path.Combine(Paths.PluginPath, "MonsterMusumeTDMod", "translations");
        var baseUrl = Plugin.Settings.TranslationUpdateBaseUrl.Value?.Trim().TrimEnd('/');
        var assetBaseUrl = Plugin.Settings.TranslationAssetUpdateBaseUrl.Value?.Trim().TrimEnd('/');
        var timeoutSeconds = Math.Clamp(Plugin.Settings.TranslationUpdateTimeoutSeconds.Value, 3, 60);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Plugin.Log?.LogWarning($"Translation sync skipped: remote base URL is empty");
            return;
        }

        _handled = false;
        Plugin.Log?.LogInfo(manual
            ? "F6 requested translation synchronization in the background"
            : "Checking for translation updates in the background");
        _updateTask = Task.Run(() => SynchronizeAsync(translationRoot, baseUrl, timeoutSeconds));
        if (!string.IsNullOrWhiteSpace(assetBaseUrl))
        {
            _assetHandled = false;
            Plugin.Log?.LogInfo("Checking for font asset updates in the background");
            _assetUpdateTask = Task.Run(() => SynchronizeFontAssetAsync(
                Paths.PluginPath, assetBaseUrl, timeoutSeconds));
        }
        else
        {
            Plugin.Log?.LogWarning("Font asset auto-update skipped: remote asset base URL is empty");
        }
    }

    public void Update()
    {
        HandleAssetUpdateCompletion();
        if (_handled || _updateTask == null || !_updateTask.IsCompleted)
            return;

        _handled = true;
        UpdateResult result;
        try
        {
            result = _updateTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"Translation auto-update failed; existing translations remain active: {ex.Message}");
            return;
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            Plugin.Log?.LogWarning($"Translation auto-update failed; existing translations remain active: {result.Error}");
            return;
        }

        if (!result.Updated)
        {
            Plugin.Log?.LogInfo($"Translations are up to date ({result.Unchanged} files, commit {result.Commit})");
            return;
        }

        try
        {
            Plugin.Translations?.LoadStatic();
            UiStyleManager.Reload();
            UiCanvasTranslationScanner.InvalidateProcessingCache();
            UiCanvasTranslationScanner.RequestFastScan();
            R18DialogueBackgroundController.InvalidatePresentation();
            var refreshed = Plugin.Translations == null
                ? 0
                : TmpFontInstaller.ReloadVisibleUiTranslations(Plugin.Translations);
            var subSkillsRefreshed = TmpFontInstaller.RefreshVisibleSubSkillPresentation();
            var unitDetailRefreshed = TmpFontInstaller.RefreshVisibleUnitDetailPresentation();
            UiCanvasTranslationScanner.RequestFastScan();
            Plugin.Log?.LogInfo(
                $"Translation update applied: downloaded={result.Downloaded}, deleted={result.Deleted}, " +
                $"unchanged={result.Unchanged}, commit={result.Commit}; refreshed {refreshed} UI, " +
                $"{subSkillsRefreshed} sub-skill and {unitDetailRefreshed} unit-detail text(s)");
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError($"Translation files updated, but live reload failed: {ex}");
        }
    }

    public static void ApplyPendingAssetUpdate(string pluginRoot)
    {
        var targetPath = Path.Combine(pluginRoot, FontAssetName);
        var pendingPath = targetPath + PendingSuffix;
        var pendingManifestPath = Path.Combine(
            pluginRoot, "MonsterMusumeTDMod", "assets-manifest.pending.json");
        if (!File.Exists(pendingPath))
            return;

        try
        {
            if (!File.Exists(pendingManifestPath))
                throw new InvalidDataException("pending asset manifest is missing");
            var manifest = ParseManifest(File.ReadAllBytes(pendingManifestPath), "pending asset manifest");
            if (!manifest.Files.TryGetValue(FontAssetName, out var expected))
                throw new InvalidDataException($"pending asset manifest does not contain {FontAssetName}");
            if (new FileInfo(pendingPath).Length != expected.Size ||
                !HashMatches(pendingPath, expected.Sha256))
                throw new InvalidDataException("pending font asset failed size or SHA-256 validation");

            File.Move(pendingPath, targetPath, true);
            var installedManifestPath = Path.Combine(
                pluginRoot, "MonsterMusumeTDMod", "assets-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(installedManifestPath)!);
            File.Move(pendingManifestPath, installedManifestPath, true);
            Plugin.Log?.LogInfo($"Installed pending font asset update: {targetPath}");
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"Pending font asset update was not installed; existing font remains active: {ex.Message}");
        }
    }

    private void HandleAssetUpdateCompletion()
    {
        if (_assetHandled || _assetUpdateTask == null || !_assetUpdateTask.IsCompleted)
            return;
        _assetHandled = true;
        AssetUpdateResult result;
        try
        {
            result = _assetUpdateTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"Font asset auto-update failed; existing font remains active: {ex.Message}");
            return;
        }

        if (!string.IsNullOrEmpty(result.Error))
            Plugin.Log?.LogWarning($"Font asset auto-update failed; existing font remains active: {result.Error}");
        else if (result.Downloaded)
            Plugin.Log?.LogInfo(
                $"Font asset update downloaded and verified (commit {result.Commit}); it will be installed on the next game start");
        else
            Plugin.Log?.LogInfo($"Font asset is up to date (commit {result.Commit})");
    }

    private static async Task<AssetUpdateResult> SynchronizeFontAssetAsync(
        string pluginRoot,
        string baseUrl,
        int timeoutSeconds)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var manifestBytes = await client.GetByteArrayAsync($"{baseUrl}/manifest.json").ConfigureAwait(false);
            var manifest = ParseManifest(manifestBytes, "remote asset manifest");
            if (!manifest.Files.TryGetValue(FontAssetName, out var expected))
                throw new InvalidDataException($"Remote asset manifest does not contain {FontAssetName}");

            var targetPath = Path.Combine(pluginRoot, FontAssetName);
            var pendingPath = targetPath + PendingSuffix;
            var pendingManifestPath = Path.Combine(
                pluginRoot, "MonsterMusumeTDMod", "assets-manifest.pending.json");
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == expected.Size &&
                HashMatches(targetPath, expected.Sha256))
            {
                TryDeleteFile(pendingPath);
                TryDeleteFile(pendingManifestPath);
                return new AssetUpdateResult { Commit = ManifestCommit(manifest) };
            }

            var bytes = await client.GetByteArrayAsync(BuildFileUrl(baseUrl, FontAssetName)).ConfigureAwait(false);
            ValidateDownloadedFile(FontAssetName, expected, bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(pendingManifestPath)!);
            var tempPath = pendingPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
            File.Move(tempPath, pendingPath, true);
            var manifestTempPath = pendingManifestPath + ".tmp";
            await File.WriteAllBytesAsync(manifestTempPath, manifestBytes).ConfigureAwait(false);
            File.Move(manifestTempPath, pendingManifestPath, true);
            return new AssetUpdateResult { Downloaded = true, Commit = ManifestCommit(manifest) };
        }
        catch (Exception ex)
        {
            return new AssetUpdateResult { Error = ex.Message };
        }
    }

    private static string ManifestCommit(TranslationManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.Commit) ? "unknown" : manifest.Commit;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static async Task<UpdateResult> SynchronizeAsync(
        string translationRoot,
        string baseUrl,
        int timeoutSeconds)
    {
        var stagingRoot = Path.Combine(translationRoot, $".update-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(translationRoot);
            RemoveStaleStagingDirectories(translationRoot);
            Directory.CreateDirectory(stagingRoot);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var manifestBytes = await client.GetByteArrayAsync($"{baseUrl}/manifest.json").ConfigureAwait(false);
            var remoteManifest = ParseManifest(manifestBytes, "remote manifest");
            var localManifestPath = Path.Combine(translationRoot, "manifest.json");
            var localManifest = TryReadManifest(localManifestPath);

            var downloads = new List<(string RelativePath, ManifestFile File)>();
            var unchanged = 0;
            foreach (var pair in remoteManifest.Files)
            {
                var relativePath = NormalizeRelativePath(pair.Key);
                var localPath = ResolveManagedPath(translationRoot, relativePath);
                if (File.Exists(localPath) && HashMatches(localPath, pair.Value.Sha256))
                    unchanged++;
                else
                    downloads.Add((relativePath, pair.Value));
            }

            using var gate = new SemaphoreSlim(4, 4);
            var downloadTasks = downloads.Select(async item =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var url = BuildFileUrl(baseUrl, item.RelativePath);
                    var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
                    ValidateDownloadedFile(item.RelativePath, item.File, bytes);
                    var stagedPath = ResolveManagedPath(stagingRoot, item.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                    await File.WriteAllBytesAsync(stagedPath, bytes).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(downloadTasks).ConfigureAwait(false);

            var deleted = 0;
            foreach (var item in downloads)
            {
                var stagedPath = ResolveManagedPath(stagingRoot, item.RelativePath);
                var localPath = ResolveManagedPath(translationRoot, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                File.Move(stagedPath, localPath, true);
            }

            if (localManifest != null)
            {
                var remotePaths = new HashSet<string>(
                    remoteManifest.Files.Keys.Select(NormalizeRelativePath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var oldPathValue in localManifest.Files.Keys)
                {
                    var oldPath = NormalizeRelativePath(oldPathValue);
                    if (remotePaths.Contains(oldPath) || IsRuntimeOwnedFile(oldPath))
                        continue;

                    var localPath = ResolveManagedPath(translationRoot, oldPath);
                    if (!File.Exists(localPath))
                        continue;

                    File.Delete(localPath);
                    deleted++;
                    RemoveEmptyParents(Path.GetDirectoryName(localPath), translationRoot);
                }
            }

            var manifestTempPath = Path.Combine(translationRoot, "manifest.json.tmp");
            await File.WriteAllBytesAsync(manifestTempPath, manifestBytes).ConfigureAwait(false);
            File.Move(manifestTempPath, localManifestPath, true);

            return new UpdateResult
            {
                Updated = downloads.Count > 0 || deleted > 0,
                Downloaded = downloads.Count,
                Deleted = deleted,
                Unchanged = unchanged,
                Commit = string.IsNullOrWhiteSpace(remoteManifest.Commit) ? "unknown" : remoteManifest.Commit
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult { Error = ex.Message };
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, true);
            }
            catch
            {
                // A stale staging directory is harmless and can be removed on the next manual cleanup.
            }
        }
    }

    private static TranslationManifest ParseManifest(byte[] bytes, string description)
    {
        var manifest = JsonSerializer.Deserialize<TranslationManifest>(bytes, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (manifest == null || manifest.SchemaVersion != 1 || manifest.Files == null)
            throw new InvalidDataException($"Invalid {description}");
        return manifest;
    }

    private static TranslationManifest TryReadManifest(string path)
    {
        try
        {
            return File.Exists(path) ? ParseManifest(File.ReadAllBytes(path), "local manifest") : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateDownloadedFile(string relativePath, ManifestFile expected, byte[] bytes)
    {
        if (expected.Size >= 0 && bytes.LongLength != expected.Size)
            throw new InvalidDataException($"Size check failed for {relativePath}");
        if (!HashMatches(bytes, expected.Sha256))
            throw new InvalidDataException($"SHA-256 check failed for {relativePath}");
        if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            try
            {
                using var stream = new MemoryStream(bytes, offset, bytes.Length - offset, false);
                using var document = JsonDocument.Parse(stream);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"JSON validation failed for {relativePath}: {ex.Message}", ex);
            }
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return string.Equals(Convert.ToHexString(sha256.ComputeHash(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashMatches(byte[] bytes, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("Manifest contains an invalid empty or rooted path");

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"Manifest contains an unsafe path: {path}");
        return string.Join('/', parts);
    }

    private static string ResolveManagedPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest path escapes translations directory: {relativePath}");
        return fullPath;
    }

    private static string BuildFileUrl(string baseUrl, string relativePath) =>
        baseUrl + "/" + string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));

    private static bool IsRuntimeOwnedFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals("missing.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ui_snapshot.tsv", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("subskill_scan.tsv", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveStaleStagingDirectories(string translationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(translationRoot, ".update-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // A locked stale directory does not prevent a new update attempt.
            }
        }
    }

    private static void RemoveEmptyParents(string directory, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(directory) &&
               !string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), fullRoot,
                   StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private sealed class TranslationManifest
    {
        public int SchemaVersion { get; set; }
        public string Commit { get; set; }
        public Dictionary<string, ManifestFile> Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string Sha256 { get; set; }
        public long Size { get; set; } = -1;
    }

    private sealed class UpdateResult
    {
        public bool Updated { get; set; }
        public int Downloaded { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public string Commit { get; set; } = "unknown";
        public string Error { get; set; }
    }

    private sealed class AssetUpdateResult
    {
        public bool Downloaded { get; set; }
        public string Commit { get; set; } = "unknown";
        public string Error { get; set; }
    }
}
