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
/// Event-driven UI translation for active UICanvas labels with deduplicated end-of-frame refresh.
/// </summary>
public sealed class UiCanvasTranslationScanner : MonoBehaviour
{
    private const float TransitionWatchDurationSeconds = 0.35f;
    private const int DiscoveryFastCheckBudget = 64;
    private const int DiscoveryProcessBudget = 16;
    private const int MaxUnparentedRetries = 30;

    private static UiCanvasTranslationScanner _instance;
    private readonly Dictionary<int, string> _observedValues = new(1024);
    private readonly Dictionary<int, ProcessedTextState> _processedStates = new(1024);
    private readonly Dictionary<int, (TMP_Text Text, float ExpireTime)> _watchedTransitionTexts = new();
    private readonly Dictionary<int, int> _unparentedRetryCounts = new();
    private readonly List<int> _expiredWatchIds = new();
    private readonly List<int> _removedIds = new();
    private readonly HashSet<int> _mappedDiagnosticIds = new();
    private readonly HashSet<int> _currentActiveTextIds = new();
    private readonly List<TMP_Text> _discoveryTexts = new();
    private int _discoveryCursor;
    private bool _discoveryActive;

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
    private double _maxRefreshMilliseconds;
    private double _maxDiscoveryMilliseconds;

    internal static bool IsApplying(object component) =>
        ApplyingTexts.Count > 0 &&
        component is TMP_Text text &&
        !ReferenceEquals(text, null) &&
        ApplyingTexts.Contains(text.GetInstanceID());

    internal static bool IsApplyingId(int instanceId) =>
        ApplyingTexts.Count > 0 && ApplyingTexts.Contains(instanceId);

    private int _lastRenderRetryFrame = -1;
    private float _nextInstancePruneTime = 30f;
    private Canvas.WillRenderCanvases _willRenderCanvasesHandler;

    public UiCanvasTranslationScanner(IntPtr pointer)
        : base(pointer)
    {
    }

    internal static void CancelPendingRefresh(int instanceId)
    {
        if (instanceId == 0)
            return;
        PendingTextRefreshes.Remove(instanceId);
        _instance?._unparentedRetryCounts.Remove(instanceId);
    }

    internal static void RecordObservedValue(int instanceId, string translated)
    {
        var scanner = _instance;
        if (scanner != null && instanceId != 0)
            scanner._observedValues[instanceId] = translated;
    }

    internal static void RecordFastProcessedText(int instanceId, string translated, TMP_Text text = null)
    {
        if (instanceId == 0)
            return;
        PendingTextRefreshes.Remove(instanceId);
        var scanner = _instance;
        if (scanner == null)
            return;

        scanner._unparentedRetryCounts.Remove(instanceId);
        scanner._observedValues[instanceId] = translated;
        if (text != null)
        {
            scanner._processedStates[instanceId] = new ProcessedTextState(
                translated,
                UiHierarchyClassifier.GetHierarchyPath(text),
                GetInstanceId(text.font),
                GetInstanceId(text.fontSharedMaterial),
                _processingVersion,
                text.gameObject != null && text.gameObject.activeInHierarchy);
        }
        else if (scanner._processedStates.TryGetValue(instanceId, out var existing))
        {
            scanner._processedStates[instanceId] = new ProcessedTextState(
                translated,
                existing.HierarchyPath,
                existing.FontId,
                existing.MaterialId,
                _processingVersion,
                existing.ActiveInHierarchy);
        }
    }

    public static void ProcessTextImmediate(TMP_Text text)
    {
        var scanner = _instance;
        if (scanner == null || text == null || text.gameObject == null ||
            !text.gameObject.activeInHierarchy ||
            Plugin.Settings?.Enabled.Value != true || Plugin.Translations == null ||
            UiHierarchyClassifier.IsUiTextSoftOutlineLayer(text) ||
            !UiHierarchyClassifier.IsUnderUiCanvas(text))
            return;

        var instanceId = text.GetInstanceID();
        if (ApplyingTexts.Contains(instanceId))
            return;

        PendingTextRefreshes.Remove(instanceId);
        scanner._unparentedRetryCounts.Remove(instanceId);
        var currentText = text.text;
        if (string.IsNullOrEmpty(currentText))
            return;

        if (!Plugin.Translations.CanPossiblyNeedTranslation(currentText) &&
            !Plugin.Translations.IsKnownTranslationValue(currentText) &&
            !TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
            return;

        if (scanner.IsProcessingStateCurrent(text, instanceId, currentText))
            return;

        scanner.ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
        scanner.RecordProcessingState(text);
    }

    internal static void NotifySelectableTransition(TMP_Text[] texts)
    {
        var scanner = _instance;
        if (scanner == null || texts == null || texts.Length == 0 ||
            Plugin.Settings?.Enabled.Value != true || Plugin.Translations == null)
            return;

        var expireAt = Time.unscaledTime + TransitionWatchDurationSeconds;
        var currentFrame = Time.frameCount;
        for (var i = 0; i < texts.Length; i++)
        {
            var text = texts[i];
            if (text == null || text.gameObject == null)
                continue;

            var instanceId = text.GetInstanceID();
            if (UiHierarchyClassifier.IsKnownInternalLayerId(instanceId))
                continue;

            scanner._watchedTransitionTexts[instanceId] = (text, expireAt);
            if (text.gameObject.activeInHierarchy)
            {
                _queuedRequests++;
                if (PendingTextRefreshes.Enqueue(instanceId, text, currentFrame, activation: true))
                    _mergedRequests++;
            }
        }
    }

    public static void QueueImmediateRefresh(object component) =>
        QueueRefresh(component, isActivation: false);

    public static void QueueActivationRefresh(object component) =>
        QueueRefresh(component, isActivation: true);

    internal static void QueueActivationRefreshById(int instanceId, TMP_Text text)
    {
        if (instanceId == 0 || ReferenceEquals(text, null) || ApplyingTexts.Contains(instanceId))
            return;

        _instance?._unparentedRetryCounts.Remove(instanceId);
        _queuedRequests++;
        if (PendingTextRefreshes.Enqueue(instanceId, text, Time.frameCount, activation: true))
            _mergedRequests++;
    }

    private static void QueueRefresh(object component, bool isActivation)
    {
        if (component is not TMP_Text text || text == null)
            return;

        var instanceId = text.GetInstanceID();
        if (UiHierarchyClassifier.IsKnownInternalLayerId(instanceId) || ApplyingTexts.Contains(instanceId))
            return;
        if (isActivation)
            _instance?._unparentedRetryCounts.Remove(instanceId);
        _queuedRequests++;
        var readyFrame = Time.frameCount;
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
        _fastScanRequested = true;
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
        _watchedTransitionTexts.Clear();
        _unparentedRetryCounts.Clear();
        _discoveryTexts.Clear();
        _discoveryActive = false;
        _processedStates.Clear();
        _observedValues.Clear();
    }

    public void LateUpdate()
    {
        if (Plugin.Settings?.Enabled.Value == true && Plugin.Translations != null)
        {
            if (PendingTextRefreshes.Count > 0 || _watchedTransitionTexts.Count > 0)
            {
                var refreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _pendingPeak = Math.Max(_pendingPeak, PendingTextRefreshes.Count);
                RefreshPendingTexts(inWillRenderCanvases: false);
                RefreshWatchedTransitions(inWillRenderCanvases: false);
                _maxRefreshMilliseconds = Math.Max(_maxRefreshMilliseconds,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - refreshStarted) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
            }
        }

        if (_fastScanRequested || _discoveryActive)
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

        UiStyleManager.SyncAllUnderlayLayers();
    }

    private void DiscoverTexts()
    {
        if (Plugin.Settings?.Enabled.Value != true || Plugin.Translations == null)
        {
            _fastScanRequested = false;
            _discoveryActive = false;
            _discoveryTexts.Clear();
            return;
        }

        if (_fastScanRequested)
        {
            _fastScanRequested = false;
            _discoveryTexts.Clear();
            _discoveryCursor = 0;
            _currentActiveTextIds.Clear();
            UiHierarchyClassifier.CollectUiCanvasTexts(_discoveryTexts);
            _discoveryActive = _discoveryTexts.Count > 0;
        }

        if (!_discoveryActive)
            return;

        var checkedCount = 0;
        var processedCount = 0;
        for (; _discoveryCursor < _discoveryTexts.Count &&
               checkedCount < DiscoveryFastCheckBudget &&
               processedCount < DiscoveryProcessBudget;
             _discoveryCursor++)
        {
            var text = _discoveryTexts[_discoveryCursor];
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy)
                continue;

            var instanceId = text.GetInstanceID();
            if (UiHierarchyClassifier.IsKnownInternalLayerId(instanceId))
                continue;

            checkedCount++;
            _currentActiveTextIds.Add(instanceId);
            if (PendingTextRefreshes.Contains(instanceId))
                continue;

            var currentText = text.text;
            if (string.IsNullOrEmpty(currentText))
                continue;

            if (!Plugin.Translations.CanPossiblyNeedTranslation(currentText) &&
                !Plugin.Translations.IsKnownTranslationValue(currentText) &&
                !TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
                continue;

            if (!UiHierarchyClassifier.IsUnderUiCanvas(text) ||
                IsProcessingStateCurrent(text, instanceId, currentText))
                continue;

            ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
            RecordProcessingState(text);
            processedCount++;
        }

        if (_discoveryCursor < _discoveryTexts.Count)
            return;

        _discoveryActive = false;
        _discoveryTexts.Clear();
        CleanupInactiveState();
    }

    private void ProcessText(TMP_Text text, bool allowDiagnostics, bool scheduleRetry)
    {
        var id = text.GetInstanceID();
        if (!ApplyingTexts.Add(id)) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        UiStyleManager.BeginPresentation(text);
        try
        {
            ProcessTextCore(text, allowDiagnostics, scheduleRetry);
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
        var hierarchyPath = UiHierarchyClassifier.GetHierarchyPathAndFlags(text, out var normalizedPath, out var flags);
        if ((flags & UiPathFlags.UnitDetailFontOverride) != 0)
            TmpFontInstaller.ApplyUnitDetailFont(text);
        else
            TmpFontInstaller.ApplyHomeDeckSubSkillNamePresentation(text);

        if ((Plugin.Settings?.TranslateUiTexts.Value != true &&
             Plugin.Settings?.TranslateSubSkills.Value != true) ||
            Plugin.Translations == null)
            return false;

        var instanceId = text.GetInstanceID();
        var source = text.text;
        if (string.IsNullOrEmpty(source))
            return false;

        if (_observedValues.TryGetValue(instanceId, out var observedTranslated) &&
            string.Equals(observedTranslated, source, StringComparison.Ordinal) &&
            (Plugin.Translations.IsKnownUiTextTranslationValue(source) ||
             Plugin.Translations.IsKnownSubSkillTranslationValue(source)))
        {
            if (!TmpFontInstaller.TryFastCompleteTranslatedTmpText(text, source, null, isSubSkill: false))
                TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            UiStyleManager.SyncUnderlayLayer(text);
            return true;
        }

        if (Plugin.Translations.TryTranslateUiText(
                source,
                normalizedPath,
                out var translated))
        {
            if (allowDiagnostics && source.Contains('\n') && _mappedDiagnosticIds.Add(instanceId))
                Plugin.Log?.LogInfo($"UI description mapping detected: {UiHierarchyClassifier.GetHierarchyPath(text, 24)}");

            if (!TmpFontInstaller.TryFastCompleteTranslatedTmpText(text, translated, source, isSubSkill: false))
                TmpFontInstaller.ApplyToTranslatedUiText(text, translated, source);
            if (!string.Equals(source, translated, StringComparison.Ordinal))
                text.text = translated;
            _observedValues[instanceId] = translated;
            UiStyleManager.SyncUnderlayLayer(text);
            return true;
        }

        if (Plugin.Translations.IsKnownUiTextTranslationValue(source) ||
            (Plugin.Settings.TranslateSubSkills.Value &&
             Plugin.Translations.IsKnownSubSkillTranslationValue(source)))
        {
            if (!TmpFontInstaller.TryFastCompleteTranslatedTmpText(text, source, null, isSubSkill: false))
                TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            _observedValues[instanceId] = source;
            UiStyleManager.SyncUnderlayLayer(text);
            return true;
        }

        if (TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
        {
            TmpFontInstaller.RestoreTranslatedUiText(text);
            TmpFontInstaller.RestoreTranslatedSubSkillText(text);
        }
        _observedValues[instanceId] = source;
        return false;
    }

    private void RetryMappedTextsBeforeRender()
    {
        if (Plugin.Settings?.Enabled.Value != true ||
            Plugin.Translations == null)
            return;

        if (PendingTextRefreshes.Count > 0 || _watchedTransitionTexts.Count > 0)
        {
            var refreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _pendingPeak = Math.Max(_pendingPeak, PendingTextRefreshes.Count);
            RefreshPendingTexts(inWillRenderCanvases: true);
            RefreshWatchedTransitions(inWillRenderCanvases: true);
            _maxRefreshMilliseconds = Math.Max(_maxRefreshMilliseconds,
                (System.Diagnostics.Stopwatch.GetTimestamp() - refreshStarted) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency);
        }

        UiStyleManager.SyncAllUnderlayLayers();

        if (_lastRenderRetryFrame == Time.frameCount)
            return;
        _lastRenderRetryFrame = Time.frameCount;

        if (Time.unscaledTime >= _nextMetricsTime)
        {
            _nextMetricsTime = Time.unscaledTime + 5f;
            if (Plugin.Settings.EnableUiPerformanceLog.Value && _queuedRequests > 0)
                Plugin.Log?.LogInfo($"UI work (5s): requests={_queuedRequests}, merged={_mergedRequests}, " +
                    $"processed={_processedCount}, processingMs={_processingMilliseconds:F2}, " +
                    $"peakRefreshMs={_maxRefreshMilliseconds:F2}, peakDiscoveryMs={_maxDiscoveryMilliseconds:F2}, " +
                    $"peakQueue={_pendingPeak}");
            _queuedRequests = _mergedRequests = _processedCount = 0;
            _processingMilliseconds = 0;
            _maxRefreshMilliseconds = 0;
            _maxDiscoveryMilliseconds = 0;
            _pendingPeak = 0;
        }
    }

    private void RefreshWatchedTransitions(bool inWillRenderCanvases)
    {
        if (_watchedTransitionTexts.Count == 0)
            return;

        var now = Time.unscaledTime;
        _expiredWatchIds.Clear();
        foreach (var pair in _watchedTransitionTexts)
        {
            var instanceId = pair.Key;
            var text = pair.Value.Text;
            if (now > pair.Value.ExpireTime || text == null || text.gameObject == null)
            {
                _expiredWatchIds.Add(instanceId);
                continue;
            }

            if (!text.gameObject.activeInHierarchy ||
                UiHierarchyClassifier.IsKnownInternalLayerId(instanceId) ||
                !UiHierarchyClassifier.IsUnderUiCanvas(text))
                continue;

            var currentText = text.text;
            if (IsProcessingStateCurrent(text, instanceId, currentText))
                continue;

            var prevFontId = GetInstanceId(text.font);
            var prevMatId = GetInstanceId(text.fontSharedMaterial);

            ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
            RecordProcessingState(text);

            if (inWillRenderCanvases &&
                (prevFontId != GetInstanceId(text.font) ||
                 prevMatId != GetInstanceId(text.fontSharedMaterial) ||
                 !string.Equals(currentText, text.text, StringComparison.Ordinal)))
            {
                text.UpdateMeshPadding();
                text.SetAllDirty();
            }
        }

        for (var i = 0; i < _expiredWatchIds.Count; i++)
            _watchedTransitionTexts.Remove(_expiredWatchIds[i]);
    }

    private void RefreshPendingTexts(bool inWillRenderCanvases)
    {
        if (PendingTextRefreshes.Count == 0)
            return;

        var currentFrame = Time.frameCount;
        _readyPendingIds.Clear();
        _pendingTextSnapshot.Clear();
        foreach (var entry in PendingTextRefreshes)
        {
            if (entry.Value.ReadyFrame > currentFrame)
                continue;
            _readyPendingIds.Add(entry.Key);
            _pendingTextSnapshot.Add(entry.Value.Text);
        }

        for (var i = 0; i < _readyPendingIds.Count; i++)
            PendingTextRefreshes.Remove(_readyPendingIds[i]);

        for (var i = 0; i < _pendingTextSnapshot.Count; i++)
        {
            var text = _pendingTextSnapshot[i];
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy)
                continue;

            var instanceId = text.GetInstanceID();
            if (UiHierarchyClassifier.IsKnownInternalLayerId(instanceId))
                continue;

            var currentText = text.text;
            if (string.IsNullOrEmpty(currentText))
                continue;

            if (!Plugin.Translations.CanPossiblyNeedTranslation(currentText) &&
                !Plugin.Translations.IsKnownTranslationValue(currentText) &&
                !TmpFontInstaller.IsTrackedTranslatedComponent(instanceId))
            {
                _unparentedRetryCounts.Remove(instanceId);
                continue;
            }

            if (!UiHierarchyClassifier.IsUnderUiCanvas(text))
            {
                UiHierarchyClassifier.InvalidateTransform(text.transform);
                var retries = _unparentedRetryCounts.TryGetValue(instanceId, out var count) ? count + 1 : 1;
                if (retries <= MaxUnparentedRetries)
                {
                    _unparentedRetryCounts[instanceId] = retries;
                    var delay = retries <= 4 ? 1 : 2;
                    PendingTextRefreshes.Enqueue(instanceId, text, currentFrame + delay, activation: true);
                }
                else
                {
                    _unparentedRetryCounts.Remove(instanceId);
                }
                continue;
            }
            _unparentedRetryCounts.Remove(instanceId);

            if (IsProcessingStateCurrent(text, instanceId, currentText))
                continue;

            var prevFontId = GetInstanceId(text.font);
            var prevMatId = GetInstanceId(text.fontSharedMaterial);

            ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
            RecordProcessingState(text);

            if (inWillRenderCanvases &&
                (prevFontId != GetInstanceId(text.font) ||
                 prevMatId != GetInstanceId(text.fontSharedMaterial) ||
                 !string.Equals(currentText, text.text, StringComparison.Ordinal)))
            {
                text.UpdateMeshPadding();
                text.SetAllDirty();
            }
        }
    }

    private bool IsProcessingStateCurrent(TMP_Text text, int instanceId, string currentText = null)
    {
        if (!_processedStates.TryGetValue(instanceId, out var state))
            return false;

        if (TmpFontInstaller.IsTrackedTranslatedComponent(instanceId) &&
            !UiStyleManager.IsInstanceStyled(text, instanceId))
        {
            return false;
        }

        currentText ??= text.text;
        if (state.Version != _processingVersion ||
            !string.Equals(state.Text, currentText, StringComparison.Ordinal) ||
            state.FontId != GetInstanceId(text.font) ||
            state.MaterialId != GetInstanceId(text.fontSharedMaterial) ||
            !string.Equals(state.HierarchyPath, UiHierarchyClassifier.GetHierarchyPath(text), StringComparison.Ordinal))
        {
            return false;
        }

        var active = text.gameObject != null && text.gameObject.activeInHierarchy;
        if (state.ActiveInHierarchy != active)
        {
            _processedStates[instanceId] = new ProcessedTextState(
                state.Text,
                state.HierarchyPath,
                state.FontId,
                state.MaterialId,
                state.Version,
                active);
        }

        return true;
    }

    private void RecordProcessingState(TMP_Text text)
    {
        if (text == null)
            return;

        _processedStates[text.GetInstanceID()] = new ProcessedTextState(
            text.text,
            UiHierarchyClassifier.GetHierarchyPath(text),
            GetInstanceId(text.font),
            GetInstanceId(text.fontSharedMaterial),
            _processingVersion,
            text.gameObject != null && text.gameObject.activeInHierarchy);
    }

    private void CleanupInactiveState()
    {
        if (Time.unscaledTime >= _nextInstancePruneTime)
        {
            _nextInstancePruneTime = Time.unscaledTime + 30f;
            TmpFontInstaller.PruneDestroyedInstances();
            if (_processedStates.Count > 2048)
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
            }
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
            int version,
            bool activeInHierarchy)
        {
            Text = text;
            HierarchyPath = hierarchyPath;
            FontId = fontId;
            MaterialId = materialId;
            Version = version;
            ActiveInHierarchy = activeInHierarchy;
        }

        public string Text { get; }
        public string HierarchyPath { get; }
        public int FontId { get; }
        public int MaterialId { get; }
        public int Version { get; }
        public bool ActiveInHierarchy { get; }
    }
}
