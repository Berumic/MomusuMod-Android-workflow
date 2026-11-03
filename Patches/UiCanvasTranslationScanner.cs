using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

/// <summary>
/// Event-driven UI translation with a low-frequency full scan for static
/// prefab labels that never invoke a managed text setter after parenting.
/// </summary>
public sealed class UiCanvasTranslationScanner : MonoBehaviour
{
    private const float FastScanIntervalSeconds = 0.25f;
    private const float IdleScanIntervalSeconds = 5f;
    private const float FastScanDurationSeconds = 0.6f;
    private const int DiscoveryBatchSize = 12;

    private static UiCanvasTranslationScanner _instance;
    private readonly Dictionary<int, string> _observedValues = new();
    private readonly Dictionary<int, ProcessedTextState> _processedStates = new();
    private readonly List<int> _removedIds = new();
    private readonly HashSet<int> _mappedDiagnosticIds = new();
    private readonly HashSet<int> _activeTextIds = new();
    private readonly HashSet<int> _currentActiveTextIds = new();
    private readonly List<TMP_Text> _discoveryTexts = new();
    private int _discoveryCursor;
    private bool _discoveryActive;
    private bool _discoveryChanged;

    private static readonly UiRefreshQueue<TMP_Text> PendingTextRefreshes = new();
    private readonly List<int> _readyPendingIds = new();
    private readonly List<TMP_Text> _pendingTextSnapshot = new();
    private static int _processingVersion = 1;
    private static bool _fastScanRequested;
    private static readonly HashSet<int> ApplyingTexts = new();
    private static int _queuedRequests;
    private static int _mergedRequests;
    private int _processedCount;
    private double _processingMilliseconds;
    private float _nextMetricsTime;
    private int _pendingPeak;
    private double _projectionMilliseconds;
    private double _maxRefreshMilliseconds;
    private double _maxDiscoveryMilliseconds;

    internal static bool IsApplying(object component) =>
        component is TMP_Text text && text != null && ApplyingTexts.Contains(text.GetInstanceID());

    private float _nextScanTime;
    private float _fastScanUntil;
    private int _lastRenderRetryFrame = -1;
    private float _nextInstancePruneTime = 30f;
    private Canvas.WillRenderCanvases _willRenderCanvasesHandler;

    public UiCanvasTranslationScanner(IntPtr pointer)
        : base(pointer)
    {
    }

    internal static void RecordFastProcessedText(int instanceId, string translated)
    {
        var scanner = _instance;
        if (scanner == null)
            return;

        scanner._observedValues[instanceId] = translated;
        if (scanner._processedStates.TryGetValue(instanceId, out var existing))
        {
            scanner._processedStates[instanceId] = new ProcessedTextState(
                translated,
                existing.HierarchyPath,
                existing.FontId,
                existing.MaterialId,
                _processingVersion);
        }
    }

    public static void QueueImmediateRefresh(object component) =>
        QueueRefresh(component, isActivation: false);

    public static void QueueActivationRefresh(object component) =>
        QueueRefresh(component, isActivation: true);

    private static void QueueRefresh(object component, bool isActivation)
    {
        if (component is not TMP_Text text || text == null ||
            UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text))
            return;

        var instanceId = text.GetInstanceID();
        if (ApplyingTexts.Contains(instanceId))
            return;
        _queuedRequests++;
        var readyFrame = isActivation ? Time.frameCount : Time.frameCount + 1;
        if (PendingTextRefreshes.Enqueue(instanceId, text, readyFrame, isActivation))
            _mergedRequests++;
    }

    public static void InvalidateProcessingCache()
    {
        unchecked
        {
            _processingVersion++;
            if (_processingVersion == 0)
                _processingVersion = 1;
        }
        UiHierarchyClassifier.ClearCache();
        PatchManager.ClearSlotBindingCache();
        _fastScanRequested = true;
    }

    public static void RequestFastScan() => _fastScanRequested = true;

    public void OnEnable()
    {
        _instance = this;
        _fastScanUntil = Time.unscaledTime + FastScanDurationSeconds;
        _willRenderCanvasesHandler = DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(
            new Action(RetryMappedTextsBeforeRender));
        Canvas.add_willRenderCanvases(_willRenderCanvasesHandler);
    }

    public void OnDisable()
    {
        if (_instance == this)
            _instance = null;
        if (_willRenderCanvasesHandler != null)
            Canvas.remove_willRenderCanvases(_willRenderCanvasesHandler);
        _willRenderCanvasesHandler = null;
        PendingTextRefreshes.Clear();
        _discoveryTexts.Clear();
        _discoveryActive = false;
        _processedStates.Clear();
        _activeTextIds.Clear();
    }

    public void LateUpdate()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { DiscoverTexts(); }
        finally
        {
            _maxDiscoveryMilliseconds = Math.Max(_maxDiscoveryMilliseconds,
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private void DiscoverTexts()
    {
        var now = Time.unscaledTime;
        if (_fastScanRequested)
        {
            _fastScanRequested = false;
            _fastScanUntil = now + FastScanDurationSeconds;
            _nextScanTime = now;
        }
        if (!_discoveryActive && now < _nextScanTime)
            return;

        if (Plugin.Settings?.Enabled.Value != true || Plugin.Translations == null)
        {
            _discoveryActive = false;
            _discoveryTexts.Clear();
            _nextScanTime = now + IdleScanIntervalSeconds;
            return;
        }

        if (!_discoveryActive)
        {
            _discoveryTexts.Clear();
            _discoveryCursor = 0;
            _discoveryChanged = false;
            _currentActiveTextIds.Clear();
#if ANDROID
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
#else
            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(
                         FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
#endif
            {
                _discoveryTexts.Add(text);
            }
            _discoveryActive = true;
        }

        var batchEnd = Math.Min(_discoveryTexts.Count, _discoveryCursor + DiscoveryBatchSize);
        for (; _discoveryCursor < batchEnd; _discoveryCursor++)
        {
            var text = _discoveryTexts[_discoveryCursor];
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
                !UiHierarchyClassifier.IsUnderUiCanvas(text))
                continue;

            var instanceId = text.GetInstanceID();
            _currentActiveTextIds.Add(instanceId);
            if (PendingTextRefreshes.Contains(instanceId))
                continue;

            var isNew = !_activeTextIds.Contains(instanceId);
            if (isNew)
                _discoveryChanged = true;
            if (!isNew && IsProcessingStateCurrent(text, instanceId))
                continue;

            QueueImmediateRefresh(text);
        }

        if (_discoveryCursor < _discoveryTexts.Count)
            return;
        _discoveryActive = false;
        _discoveryTexts.Clear();

        CleanupInactiveState();
        _activeTextIds.Clear();
        foreach (var instanceId in _currentActiveTextIds)
            _activeTextIds.Add(instanceId);

        if (_discoveryChanged)
            _fastScanUntil = now + FastScanDurationSeconds;
        _nextScanTime = now + (now < _fastScanUntil
            ? FastScanIntervalSeconds
            : IdleScanIntervalSeconds);
    }

    private void ProcessText(TMP_Text text, bool allowDiagnostics, bool scheduleRetry)
    {
        var id = text.GetInstanceID();
        if (!ApplyingTexts.Add(id)) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        UiStyleManager.BeginPresentation(text);
        try
        {
            var isTranslated = ProcessTextCore(text, allowDiagnostics, scheduleRetry);
            UiStyleManager.Apply(text, Plugin.Settings.UiTextOutlineWidth.Value,
                Plugin.Settings.UiTextFaceDilate.Value, TmpFontInstaller.LoadedFont,
                isTranslated);
        }
        finally
        {
            try { UiStyleManager.EndPresentation(); }
            finally
            {
                ApplyingTexts.Remove(id);
                _processedCount++;
                _processingMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                    1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }
    }

    private bool ProcessTextCore(TMP_Text text, bool allowDiagnostics, bool scheduleRetry)
    {
        var hierarchyPath = UiHierarchyClassifier.GetHierarchyPathAndFlags(text, out var flags);
        if ((flags & UiPathFlags.UnitDetailFontOverride) != 0)
            TmpFontInstaller.ApplyUnitDetailFont(text);
        else
            TmpFontInstaller.ApplyHomeDeckSubSkillNamePresentation(text);

        if (Plugin.Settings?.TranslateUiTexts.Value != true || Plugin.Translations == null)
            return false;

        var instanceId = text.GetInstanceID();
        if ((flags & UiPathFlags.SubSkillScrollText) != 0)
            return ApplySubSkillTranslation(text, instanceId, hierarchyPath);

        if ((flags & UiPathFlags.UnitDetailUiDescription) != 0)
            TmpFontInstaller.RestoreTranslatedSubSkillText(text);

        var source = text.text;
        if (string.IsNullOrEmpty(source))
            return false;

        if (_observedValues.TryGetValue(instanceId, out var observedTranslated) &&
            string.Equals(observedTranslated, source, StringComparison.Ordinal) &&
            Plugin.Translations.IsKnownUiTextTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            return true;
        }

        if (Plugin.Translations.TryTranslateUiText(
                source,
                hierarchyPath,
                out var translated))
        {
            if (allowDiagnostics && source.Contains('\n') && _mappedDiagnosticIds.Add(instanceId))
                Plugin.Log?.LogInfo($"UI description mapping detected: {UiHierarchyClassifier.GetHierarchyPath(text, 24)}");

            if (!string.Equals(source, translated, StringComparison.Ordinal))
                text.text = translated;
            TmpFontInstaller.ApplyToTranslatedUiText(text, translated, source);
            _observedValues[instanceId] = translated;
            return true;
        }

        if (Plugin.Translations.IsKnownUiTextTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            _observedValues[instanceId] = source;
            return true;
        }

        if (Plugin.Settings.TranslateSubSkills.Value &&
            Plugin.Translations.IsKnownSubSkillTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, source);
            return true;
        }

        TmpFontInstaller.RestoreTranslatedUiText(text);
        UiStyleManager.Apply(
            text,
            Plugin.Settings?.UiTextOutlineWidth.Value ?? 0.3f,
            Plugin.Settings?.UiTextFaceDilate.Value ?? 0.35f,
            TmpFontInstaller.LoadedFont,
            isTranslated: false);
        _observedValues[instanceId] = source;
        return false;
    }

    private void RetryMappedTextsBeforeRender()
    {
        if (_lastRenderRetryFrame == Time.frameCount ||
            Plugin.Settings?.Enabled.Value != true ||
            Plugin.Translations == null)
            return;

        _lastRenderRetryFrame = Time.frameCount;
        var refreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _pendingPeak = Math.Max(_pendingPeak, PendingTextRefreshes.Count);
        RefreshPendingTexts();
        _maxRefreshMilliseconds = Math.Max(_maxRefreshMilliseconds,
            (System.Diagnostics.Stopwatch.GetTimestamp() - refreshStarted) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency);
        if (Plugin.Settings.TranslateUiTexts.Value != true)
            return;

        var projectionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        UiStyleManager.SyncAllUnderlayLayers();
        _projectionMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - projectionStarted) *
            1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (Time.unscaledTime >= _nextMetricsTime)
        {
            _nextMetricsTime = Time.unscaledTime + 5f;
            if (Plugin.Settings.EnableUiPerformanceLog.Value && _queuedRequests > 0)
                Plugin.Log?.LogInfo($"UI work (5s): requests={_queuedRequests}, merged={_mergedRequests}, " +
                    $"processed={_processedCount}, processingMs={_processingMilliseconds:F2}, " +
                    $"peakRefreshMs={_maxRefreshMilliseconds:F2}, peakDiscoveryMs={_maxDiscoveryMilliseconds:F2}, " +
                    $"projectionMs={_projectionMilliseconds:F2}, peakQueue={_pendingPeak}");
            _queuedRequests = _mergedRequests = _processedCount = 0;
            _processingMilliseconds = 0;
            _projectionMilliseconds = _maxRefreshMilliseconds = 0;
            _maxDiscoveryMilliseconds = 0;
            _pendingPeak = 0;
        }
    }

    private void RefreshPendingTexts()
    {
        if (PendingTextRefreshes.Count == 0)
            return;

        _readyPendingIds.Clear();
        _pendingTextSnapshot.Clear();
        foreach (var entry in PendingTextRefreshes)
        {
            if (entry.Value.ReadyFrame > Time.frameCount)
                continue;
            _readyPendingIds.Add(entry.Key);
            _pendingTextSnapshot.Add(entry.Value.Text);
        }

        foreach (var instanceId in _readyPendingIds)
            PendingTextRefreshes.Remove(instanceId);

        foreach (var text in _pendingTextSnapshot)
        {
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
                !UiHierarchyClassifier.IsUnderUiCanvas(text))
                continue;

            ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
            RecordProcessingState(text);
        }
    }

    private bool ApplySubSkillTranslation(TMP_Text text, int instanceId, string hierarchyPath)
    {
        var content = text.text;
        if (string.IsNullOrEmpty(content))
            return false;

        TmpFontInstaller.RestoreTranslatedUiText(text);

        if (_observedValues.TryGetValue(instanceId, out var observed) &&
            string.Equals(observed, content, StringComparison.Ordinal) &&
            Plugin.Translations.IsKnownSubSkillTranslationValue(content))
        {
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, content);
            return true;
        }

        if (Plugin.Translations.IsKnownSubSkillTranslationValue(content))
        {
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, content);
            _observedValues[instanceId] = content;
            return true;
        }

        if (Plugin.Translations.TryTranslateSubSkill(
                content,
                hierarchyPath,
                out var translated))
        {
            if (!string.Equals(content, translated, StringComparison.Ordinal))
                text.text = translated;
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, translated);
            _observedValues[instanceId] = translated;
            return true;
        }

        TmpFontInstaller.RestoreTranslatedSubSkillText(text);
        _observedValues[instanceId] = content;
        return false;
    }

    private bool IsProcessingStateCurrent(TMP_Text text, int instanceId)
    {
        if (!_processedStates.TryGetValue(instanceId, out var state))
            return false;

        if (UiStyleManager.IsInstanceStyled(instanceId) && !UiStyleManager.IsInstanceStyled(text))
            return false;

        return state.Version == _processingVersion &&
               state.FontId == GetInstanceId(text.font) &&
               state.MaterialId == GetInstanceId(text.fontSharedMaterial) &&
               string.Equals(state.Text, text.text, StringComparison.Ordinal);
    }

    private void RecordProcessingState(TMP_Text text)
    {
        if (text == null)
            return;

        UiStyleManager.SyncUnderlayLayer(text);
        _processedStates[text.GetInstanceID()] = new ProcessedTextState(
            text.text,
            UiHierarchyClassifier.GetHierarchyPath(text),
            GetInstanceId(text.font),
            GetInstanceId(text.fontSharedMaterial),
            _processingVersion);
    }

    private void CleanupInactiveState()
    {
        _removedIds.Clear();
        foreach (var instanceId in _processedStates.Keys)
            if (!_currentActiveTextIds.Contains(instanceId))
                _removedIds.Add(instanceId);

        foreach (var instanceId in _removedIds)
        {
            _processedStates.Remove(instanceId);
            _observedValues.Remove(instanceId);
        }

        if (Time.unscaledTime >= _nextInstancePruneTime)
        {
            _nextInstancePruneTime = Time.unscaledTime + 30f;
            TmpFontInstaller.PruneDestroyedInstances();
        }
    }

    private static int GetInstanceId(UnityEngine.Object value) =>
        value == null ? 0 : value.GetInstanceID();

    private readonly struct ProcessedTextState
    {
        public ProcessedTextState(
            string text,
            string hierarchyPath,
            int fontId,
            int materialId,
            int version)
        {
            Text = text;
            HierarchyPath = hierarchyPath;
            FontId = fontId;
            MaterialId = materialId;
            Version = version;
        }

        public string Text { get; }
        public string HierarchyPath { get; }
        public int FontId { get; }
        public int MaterialId { get; }
        public int Version { get; }
    }
}
