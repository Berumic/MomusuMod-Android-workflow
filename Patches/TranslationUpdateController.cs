using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
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
    private const float AutoStartupSyncDelaySeconds = 3f;

    private static TranslationUpdateController _instance;
    private Task<UpdateResult> _updateTask;
    private Task<AssetUpdateResult> _assetUpdateTask;
    private bool _handled;
    private bool _assetHandled;
    private bool _startupSyncPending;
    private float _startupSyncAt;

#if ANDROID
    private const string FontAssetName = "alimama-android";
#else
    private const string FontAssetName = "alimama";
#endif
    private const string PendingSuffix = ".pending";
    private const int RequestTotalTimeoutSeconds = 600;

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
        _instance._startupSyncPending = false;
        _instance.BeginTranslationSync(true);
    }

    public void Start()
    {
        _instance = this;
        if (Plugin.Settings?.EnableTranslationAutoUpdate.Value != true)
            return;

        // Defer automatic background sync slightly so it does not compete with
        // Unity/IL2CPP cold-start asset initialization on the first frames.
        _startupSyncPending = true;
        _startupSyncAt = Time.unscaledTime + AutoStartupSyncDelaySeconds;
    }

    private void BeginTranslationSync(bool manual)
    {
        // Do not replace a completed task before Update() has consumed its
        // result. This matters when F6 is pressed while the font download is
        // still finishing; otherwise the old task can be overwritten and its
        // staged asset result is never applied on the next launch.
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
        var concurrency = Math.Clamp(Plugin.Settings.TranslationUpdateConcurrentDownloads.Value, 1, 16);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Plugin.Log?.LogWarning("Translation sync skipped: remote base URL is empty");
            return;
        }

        _handled = false;
        Plugin.Log?.LogInfo(manual
            ? "F6 requested translation synchronization in the background"
            : "Checking for translation updates in the background");
        // Android always follows the device/runtime system proxy.  The desktop
        // implementation supports an explicit proxy, but exposing a hard-coded
        // address here breaks users whose LemonLoader process inherits a VPN or
        // per-device proxy configuration.
#if ANDROID
        const string proxyUrl = null;
        const bool directConnection = false;
        Plugin.Log?.LogInfo("Update connection: system proxy");
#else
        var proxyUrl = Plugin.Settings.TranslationUpdateProxyUrl.Value?.Trim();
        var directConnection = Plugin.Settings.TranslationUpdateDirectConnection.Value;
        Plugin.Log?.LogInfo(directConnection
            ? "Update connection: direct (system proxy disabled)"
            : string.IsNullOrEmpty(proxyUrl) ? "Update connection: system proxy"
            : "Update connection: explicit proxy (system proxy discovery disabled)");
#endif
        _updateTask = Task.Run(() => SynchronizeAsync(translationRoot, baseUrl, timeoutSeconds, proxyUrl, directConnection, concurrency));
        if (!string.IsNullOrWhiteSpace(assetBaseUrl))
        {
            _assetHandled = false;
            Plugin.Log?.LogInfo("Checking for font asset updates in the background");
            _assetUpdateTask = Task.Run(() => SynchronizeFontAssetAsync(
                Paths.PluginPath, assetBaseUrl, timeoutSeconds, proxyUrl, directConnection));
        }
        else
        {
            Plugin.Log?.LogWarning("Font asset auto-update skipped: remote asset base URL is empty");
        }
    }

    public void Update()
    {
        if (_startupSyncPending && Time.unscaledTime >= _startupSyncAt)
        {
            _startupSyncPending = false;
            if (Plugin.Settings?.EnableTranslationAutoUpdate.Value == true)
                BeginTranslationSync(false);
        }

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
            R18DialogueBackgroundController.InvalidatePresentation();
            var refreshed = Plugin.Translations == null
                ? 0
                : TmpFontInstaller.ReloadVisibleUiTranslations(Plugin.Translations);
            var subSkillsRefreshed = TmpFontInstaller.RefreshVisibleSubSkillPresentation();
            var unitDetailRefreshed = TmpFontInstaller.RefreshVisibleUnitDetailPresentation();
            Plugin.Log?.LogInfo(
                $"Translation update applied: downloaded={result.Downloaded}, reused={result.Resumed}, deleted={result.Deleted}, " +
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

    // Keep the original three-argument reflection/API shape for Android test
    // harnesses and older callers; the overload delegates to the configurable
    // implementation below and still uses the platform's system proxy.
    private static Task<AssetUpdateResult> SynchronizeFontAssetAsync(
        string pluginRoot, string baseUrl, int timeoutSeconds) =>
        SynchronizeFontAssetAsync(pluginRoot, baseUrl, timeoutSeconds, null, false);

    private static async Task<AssetUpdateResult> SynchronizeFontAssetAsync(
        string pluginRoot,
        string baseUrl,
        int timeoutSeconds, string proxyUrl = null, bool directConnection = false)
    {
        var diagnostics = new UpdateDiagnostics("Font", timeoutSeconds);
        var stage = "manifest.fetch";
        try
        {
            using var client = CreateUpdateClient(timeoutSeconds, proxyUrl, directConnection);
            var manifestBytes = await FetchManifestAsync(client, baseUrl, diagnostics).ConfigureAwait(false);
            stage = "manifest.parse";
            var manifest = ParseManifest(manifestBytes, "remote asset manifest");
            diagnostics.Info($"manifest ready: files={manifest.Files.Count}, commit={ManifestCommit(manifest)}");
            if (!manifest.Files.TryGetValue(FontAssetName, out var expected))
                throw new InvalidDataException($"Remote asset manifest does not contain {FontAssetName}");

            var targetPath = Path.Combine(pluginRoot, FontAssetName);
            var pendingPath = targetPath + PendingSuffix;
            var installedManifestPath = Path.Combine(
                pluginRoot, "MonsterMusumeTDMod", "assets-manifest.json");
            var pendingManifestPath = Path.Combine(
                pluginRoot, "MonsterMusumeTDMod", "assets-manifest.pending.json");
            var cacheRoot = Path.Combine(pluginRoot, "MonsterMusumeTDMod", ".font-update-cache");
            stage = "local-check";
            diagnostics.Info($"checking installed font: file={FontAssetName}, expected_bytes={expected.Size}");
            var installedManifest = TryReadManifest(installedManifestPath);
            var matchesInstalledManifest =
                installedManifest != null &&
                installedManifest.Files.TryGetValue(FontAssetName, out var localFontEntry) &&
                string.Equals(localFontEntry.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase);

            if (File.Exists(targetPath) &&
                new FileInfo(targetPath).Length == expected.Size &&
                (matchesInstalledManifest || HashMatches(targetPath, expected.Sha256)))
            {
                TryDeleteFile(pendingPath);
                TryDeleteFile(pendingManifestPath);
                PruneDownloadCache(cacheRoot, Array.Empty<string>());
                diagnostics.Info("complete: installed font is up to date; no font download needed");
                return new AssetUpdateResult { Commit = ManifestCommit(manifest) };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(pendingManifestPath)!);
            if (!IsValidCachedFile(pendingPath, FontAssetName, expected))
            {
                diagnostics.Info($"font download: file={FontAssetName}, url={SafeLogUrl(BuildFileUrl(baseUrl, FontAssetName))}, idle_timeout={timeoutSeconds}s, total_timeout={RequestTotalTimeoutSeconds}s");
                var (cachedPath, _) = await DownloadVerifiedAsync(client, BuildFileUrl(baseUrl, FontAssetName),
                    cacheRoot, FontAssetName, expected, diagnostics).ConfigureAwait(false);
                stage = "pending-install";
                var tempPath = pendingPath + ".tmp";
                File.Copy(cachedPath, tempPath, true);
                File.Move(tempPath, pendingPath, true);
            }
            else diagnostics.Info("reusing verified pending font; no font download needed");
            stage = "pending-manifest.commit";
            var manifestTempPath = pendingManifestPath + ".tmp";
            await File.WriteAllBytesAsync(manifestTempPath, manifestBytes).ConfigureAwait(false);
            File.Move(manifestTempPath, pendingManifestPath, true);
            PruneDownloadCache(cacheRoot, Array.Empty<string>());
            diagnostics.Info("complete: verified font staged; installation on next game start");
            return new AssetUpdateResult { Downloaded = true, Commit = ManifestCommit(manifest) };
        }
        catch (Exception ex)
        {
            return new AssetUpdateResult { Error = diagnostics.Failure(stage, null, null, ex) };
        }
    }

    internal static HttpClientHandler CreateUpdateHandler(string proxyUrl, bool directConnection = false)
    {
        if (directConnection)
            return new HttpClientHandler { UseProxy = false };
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return new HttpClientHandler { UseProxy = true };

        if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https" &&
             uri.Scheme != "socks4" && uri.Scheme != "socks4a" && uri.Scheme != "socks5") ||
            string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Invalid update ProxyUrl; use an explicit http/https/socks proxy URL or leave it empty for system proxy settings.");

        return new HttpClientHandler
        {
            Proxy = new WebProxy(uri),
            UseProxy = true
        };
    }

    private static HttpClient CreateUpdateClient(int timeoutSeconds, string proxyUrl, bool directConnection) =>
        new HttpClient(CreateUpdateHandler(proxyUrl, directConnection), disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };

    private static string ManifestCommit(TranslationManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.Commit) ? "unknown" : manifest.Commit;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    // Compatibility overload retained for the pre-refactor Android caller.
    private static Task<UpdateResult> SynchronizeAsync(
        string translationRoot, string baseUrl, int timeoutSeconds) =>
        SynchronizeAsync(translationRoot, baseUrl, timeoutSeconds, null, false, 8);

    private static async Task<UpdateResult> SynchronizeAsync(
        string translationRoot,
        string baseUrl,
        int timeoutSeconds, string proxyUrl = null, bool directConnection = false, int concurrency = 8)
    {
        var cacheRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(translationRoot))!,
            ".translation-update-cache");
        var diagnostics = new UpdateDiagnostics("Translation", timeoutSeconds);
        var stage = "initialize";
        string currentFile = null;
        try
        {
            Directory.CreateDirectory(translationRoot);

            using var client = CreateUpdateClient(timeoutSeconds, proxyUrl, directConnection);
            stage = "manifest.fetch";
            var manifestBytes = await FetchManifestAsync(client, baseUrl, diagnostics).ConfigureAwait(false);
            stage = "manifest.parse";
            var remoteManifest = ParseManifest(manifestBytes, "remote manifest");
            diagnostics.Info($"manifest ready: files={remoteManifest.Files.Count}, commit={ManifestCommit(remoteManifest)}");
            var localManifestPath = Path.Combine(translationRoot, "manifest.json");
            var localManifest = TryReadManifest(localManifestPath);
            var localFilesByNormalizedPath = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            if (localManifest?.Files != null)
            {
                foreach (var localPair in localManifest.Files)
                {
                    try { localFilesByNormalizedPath[NormalizeRelativePath(localPair.Key)] = localPair.Value; }
                    catch { }
                }
            }

            var downloads = new List<(string RelativePath, ManifestFile File)>();
            var unchanged = 0;
            var checkedFiles = 0;
            stage = "local-check";
            diagnostics.Info($"checking local files: total={remoteManifest.Files.Count}");
            foreach (var pair in remoteManifest.Files)
            {
                var relativePath = NormalizeRelativePath(pair.Key);
                currentFile = relativePath;
                var localPath = ResolveManagedPath(translationRoot, relativePath);
                if (IsLocalFileUpToDate(localPath, relativePath, pair.Value, localFilesByNormalizedPath))
                    unchanged++;
                else
                    downloads.Add((relativePath, pair.Value));
                diagnostics.Progress($"local-check: {++checkedFiles}/{remoteManifest.Files.Count}, unchanged={unchanged}, needs_update={downloads.Count}");
            }

            concurrency = Math.Clamp(concurrency, 1, 16);
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            currentFile = null;
            var resumed = 0;
            var downloaded = 0;
            var groups = downloads.GroupBy(item => CacheKey(item.File)).ToArray();
            var verified = 0;
            if (groups.Length > 0)
            {
                diagnostics.Info($"download plan: files={downloads.Count}, unique_contents={groups.Length}, concurrency={concurrency}, idle_timeout={timeoutSeconds}s, total_timeout={RequestTotalTimeoutSeconds}s, cache={cacheRoot}");
                stage = "download-batch";
                var downloadTasks = groups.Select(async group =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var item = group.First();
                        var (_, reused) = await DownloadVerifiedAsync(client, BuildFileUrl(baseUrl, item.RelativePath),
                            cacheRoot, item.RelativePath, item.File, diagnostics).ConfigureAwait(false);
                        if (reused) Interlocked.Increment(ref resumed);
                        else Interlocked.Increment(ref downloaded);
                        var finished = Interlocked.Increment(ref verified);
                        diagnostics.Progress($"download: verified={finished}/{groups.Length}, downloaded={Volatile.Read(ref downloaded)}, reused={Volatile.Read(ref resumed)}",
                            force: finished == groups.Length);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(downloadTasks).ConfigureAwait(false);
            }

            var deleted = 0;
            var installed = 0;
            stage = "install";
            if (downloads.Count > 0)
            {
                diagnostics.Info($"installing verified files: total={downloads.Count}");
                foreach (var item in downloads)
                {
                    currentFile = item.RelativePath;
                    var stagedPath = Path.Combine(cacheRoot, CacheKey(item.File) + ".bin");
                    var localPath = ResolveManagedPath(translationRoot, item.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    var installPath = localPath + ".update-install";
                    File.Copy(stagedPath, installPath, true);
                    File.Move(installPath, localPath, true);
                    diagnostics.Progress($"install: {++installed}/{downloads.Count}");
                }
            }

            stage = "delete-obsolete";
            currentFile = null;
            if (localManifest != null)
            {
                var remotePaths = new HashSet<string>(
                    remoteManifest.Files.Keys.Select(NormalizeRelativePath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var oldPathValue in localManifest.Files.Keys)
                {
                    var oldPath = NormalizeRelativePath(oldPathValue);
                    currentFile = oldPath;
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
            stage = "manifest.commit";
            currentFile = "manifest.json";
            await File.WriteAllBytesAsync(manifestTempPath, manifestBytes).ConfigureAwait(false);
            File.Move(manifestTempPath, localManifestPath, true);
            PruneDownloadCache(cacheRoot, Array.Empty<string>());
            diagnostics.Info($"complete: installed={installed}, downloaded={downloaded}, reused={resumed}, deleted={deleted}, unchanged={unchanged}, commit={ManifestCommit(remoteManifest)}");

            return new UpdateResult
            {
                Updated = downloads.Count > 0 || deleted > 0,
                Downloaded = downloaded,
                Resumed = resumed,
                Deleted = deleted,
                Unchanged = unchanged,
                Commit = string.IsNullOrWhiteSpace(remoteManifest.Commit) ? "unknown" : remoteManifest.Commit
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult
            {
                Error = diagnostics.Failure(stage, currentFile, null, ex) +
                $"; failed_files={diagnostics.FailedFiles}; download cache retained at {cacheRoot}"
            };
        }
    }

    private static bool IsLocalFileUpToDate(
        string localPath,
        string relativePath,
        ManifestFile remoteFile,
        IReadOnlyDictionary<string, ManifestFile> localManifestFiles)
    {
        var fileInfo = new FileInfo(localPath);
        if (!fileInfo.Exists)
            return false;
        if (remoteFile.Size >= 0 && fileInfo.Length != remoteFile.Size)
            return false;

        // If local manifest.json already verified this exact SHA-256 and file size matches,
        // skip reopening and hashing thousands of scenario JSON files during startup.
        if (remoteFile.Size >= 0 &&
            localManifestFiles.TryGetValue(relativePath, out var localRecorded) &&
            string.Equals(localRecorded.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HashMatches(localPath, remoteFile.Sha256);
    }

    private static async Task<byte[]> FetchManifestAsync(HttpClient client, string baseUrl, UpdateDiagnostics diagnostics)
    {
        var url = $"{baseUrl}/manifest.json";
        var stage = "manifest.headers";
        diagnostics.Info($"manifest.fetch: url={SafeLogUrl(url)}, idle_timeout={diagnostics.IdleTimeoutSeconds}s, total_timeout={RequestTotalTimeoutSeconds}s");
        using var timeout = new RequestTimeout(TimeSpan.FromSeconds(diagnostics.IdleTimeoutSeconds),
            TimeSpan.FromSeconds(RequestTotalTimeoutSeconds));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            timeout.DataReceived();
            stage = "manifest.body";
            var total = response.Content.Headers.ContentLength ?? -1;
            diagnostics.Info($"manifest response: HTTP={(int)response.StatusCode}, expected_bytes={total}");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                timeout.DataReceived();
                output.Write(buffer, 0, count);
                diagnostics.Received(count, "manifest.json", output.Length, total, "manifest.body");
            }
            var bytes = output.ToArray();
            diagnostics.Info($"manifest received: bytes={bytes.Length}");
            return bytes;
        }
        catch (Exception ex)
        {
            var failure = timeout.DescribeFailure(ex);
            throw new UpdateStageException(diagnostics.Failure(stage, "manifest.json", url, failure), failure);
        }
    }

    internal sealed class RequestTimeout : IDisposable
    {
        private readonly TimeSpan _idleDuration;
        private readonly CancellationTokenSource _idle = new();
        private readonly CancellationTokenSource _total;
        private readonly CancellationTokenSource _linked;
        public CancellationToken Token => _linked.Token;

        public RequestTimeout(TimeSpan idleDuration, TimeSpan totalDuration)
        {
            _idleDuration = idleDuration;
            _total = new CancellationTokenSource(totalDuration);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_idle.Token, _total.Token);
            DataReceived();
        }

        public void DataReceived() => _idle.CancelAfter(_idleDuration);
        public Exception DescribeFailure(Exception ex) => ex is OperationCanceledException
            ? new TimeoutException(_total.IsCancellationRequested ? "total-timeout: request duration limit reached"
                : _idle.IsCancellationRequested ? "idle-timeout: no response data received within the idle limit"
                : "request canceled", ex)
            : ex;
        public void Dispose()
        {
            _linked.Dispose();
            _idle.Dispose();
            _total.Dispose();
        }
    }

    private static string SafeLogUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<invalid URL>";
        var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return safe.Uri.AbsoluteUri;
    }

    private sealed class UpdateStageException : Exception
    {
        public UpdateStageException(string message, Exception inner) : base(message, inner) { }
    }

    private sealed class UpdateDiagnostics
    {
        private readonly string _kind;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _lastProgressMs;
        private int _failures;
        private long _receivedBytes;
        private readonly int _timeoutSeconds;
        public int IdleTimeoutSeconds => _timeoutSeconds;

        public int FailedFiles => Volatile.Read(ref _failures);
        public UpdateDiagnostics(string kind, int timeoutSeconds) { _kind = kind; _timeoutSeconds = timeoutSeconds; }
        public void Info(string message) => Plugin.Log?.LogInfo($"{_kind} update [{_timer.Elapsed.TotalSeconds:0.0}s]: {message}");
        public void Progress(string message, bool force = false)
        {
            lock (_gate)
            {
                if (!force && _timer.ElapsedMilliseconds - _lastProgressMs < 5000) return;
                _lastProgressMs = _timer.ElapsedMilliseconds;
                Info(message + $", session_received_bytes={Interlocked.Read(ref _receivedBytes)}");
            }
        }
        public void Received(int bytes, string file, long offset, long total, string phase = "download.body")
        {
            Interlocked.Add(ref _receivedBytes, bytes);
            Progress($"{phase}: file={file}, file_bytes={offset}/{(total < 0 ? "unknown" : total.ToString())}");
        }
        public string Failure(string stage, string file, string url, Exception ex)
        {
            if (ex is UpdateStageException) return ex.Message;
            var reason = ex is TimeoutException ? ex.Message.Split(':')[0] :
                ex is OperationCanceledException ? "timeout/canceled" :
                ex is HttpRequestException http && http.StatusCode.HasValue ? $"HTTP {(int)http.StatusCode.Value}" :
                ex is HttpRequestException ? "network/proxy/DNS/TLS" :
                ex is InvalidDataException || ex is JsonException ? "validation" :
                ex is UnauthorizedAccessException ? "file-access-denied" :
                ex is IOException ? "file-or-stream-IO" : ex.GetType().Name;
            var detail = ex.Message;
            var inner = ex.InnerException;
            for (var depth = 0; inner != null && depth < 3; depth++, inner = inner.InnerException)
                detail += $"; inner={inner.GetType().Name}: {inner.Message}";
            if (!string.IsNullOrEmpty(url)) detail = detail.Replace(url, SafeLogUrl(url));
            return $"stage={stage}, file={file ?? "<none>"}, url={(url == null ? "<none>" : SafeLogUrl(url))}, " +
                $"elapsed={_timer.Elapsed.TotalSeconds:0.0}s, idle_timeout={_timeoutSeconds}s, total_timeout={RequestTotalTimeoutSeconds}s, reason={reason}, exception={ex.GetType().Name}: {detail}";
        }
        public void FileFailure(string detail)
        {
            var failures = Interlocked.Increment(ref _failures);
            if (failures <= 5) Plugin.Log?.LogWarning($"{_kind} update file failed: {detail}");
            else if (failures == 6) Plugin.Log?.LogWarning($"{_kind} update: further file failures suppressed; cache is retained");
        }
    }

    private static string CacheKey(ManifestFile file)
    {
        if (file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Manifest contains an invalid SHA-256");
        return file.Sha256.ToLowerInvariant();
    }

    private static void ValidateCachedFile(string path, string relativePath, ManifestFile expected)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new InvalidDataException($"Cached file missing for {relativePath}");
        if (expected.Size >= 0 && fileInfo.Length != expected.Size)
            throw new InvalidDataException($"Size check failed for {relativePath}");

        if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDownloadedFile(relativePath, expected, File.ReadAllBytes(path));
            return;
        }

        if (!HashMatches(path, expected.Sha256))
            throw new InvalidDataException($"SHA-256 check failed for {relativePath}");
    }

    private static bool IsValidCachedFile(string path, string relativePath, ManifestFile expected)
    {
        if (!File.Exists(path)) return false;
        try { ValidateCachedFile(path, relativePath, expected); return true; }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    private static async Task<(string Path, bool Reused)> DownloadVerifiedAsync(HttpClient client, string url, string cacheRoot,
        string relativePath, ManifestFile expected, UpdateDiagnostics diagnostics)
    {
        var stage = "cache-check";
        using var timeout = new RequestTimeout(TimeSpan.FromSeconds(diagnostics.IdleTimeoutSeconds),
            TimeSpan.FromSeconds(RequestTotalTimeoutSeconds));
        try
        {
            Directory.CreateDirectory(cacheRoot);
            var completedPath = Path.Combine(cacheRoot, CacheKey(expected) + ".bin");
            var partialPath = Path.Combine(cacheRoot, CacheKey(expected) + ".part");
            if (IsValidCachedFile(completedPath, relativePath, expected))
                return (completedPath, true);
            if (IsValidCachedFile(partialPath, relativePath, expected))
            {
                File.Move(partialPath, completedPath, true);
                return (completedPath, true);
            }
            var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (expected.Size >= 0 && offset >= expected.Size)
            {
                File.Delete(partialPath);
                offset = 0;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            stage = "download.headers";
            if (offset > 0) diagnostics.Progress($"resume: file={relativePath}, offset={offset}, expected_bytes={expected.Size}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partialPath);
                diagnostics.Progress($"server rejected Range; restarting file={relativePath}");
                return await DownloadVerifiedAsync(client, url, cacheRoot, relativePath, expected, diagnostics).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            timeout.DataReceived();
            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (offset > 0 && !append) diagnostics.Progress($"server ignored Range; restarting file={relativePath}");
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange?.From != (append ? offset : 0))
                throw new InvalidDataException($"Invalid download range for {relativePath}");
            stage = "download.body";
            using (var output = new FileStream(partialPath, append ? FileMode.Append : FileMode.Create,
                       FileAccess.Write, FileShare.Read, 81920, useAsync: true))
            using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                var received = append ? offset : 0;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    timeout.DataReceived();
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    diagnostics.Received(count, relativePath, received += count, expected.Size);
                }
            }
            stage = "download.verify";
            try { ValidateCachedFile(partialPath, relativePath, expected); }
            catch (InvalidDataException)
            {
                TryDeleteFile(partialPath);
                throw;
            }
            stage = "cache-save";
            File.Move(partialPath, completedPath, true);
            return (completedPath, false);
        }
        catch (Exception ex)
        {
            var detail = diagnostics.Failure(stage, relativePath, url, timeout.DescribeFailure(ex));
            if (ex is not UpdateStageException) diagnostics.FileFailure(detail);
            throw new UpdateStageException(detail, ex);
        }
    }

    private static void PruneDownloadCache(string root, IEnumerable<string> activeKeys)
    {
        var keys = new HashSet<string>(activeKeys, StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                var extension = Path.GetExtension(path);
                if (extension != ".bin" && extension != ".part") continue;
                if (!keys.Contains(Path.GetFileNameWithoutExtension(path))) TryDeleteFile(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        public int Resumed { get; set; }
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
