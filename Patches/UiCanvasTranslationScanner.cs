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
    private const float FastScanIntervalSeconds = 0.2f;
    private const float IdleScanIntervalSeconds = 5f;
    private const float FastScanDurationSeconds = 0.8f;

    private readonly Dictionary<int, string> _observedValues = new();
    private readonly Dictionary<int, ProcessedTextState> _processedStates = new();
    private readonly Dictionary<int, TMP_Text> _unitDetailTexts = new();
    private readonly List<TMP_Text> _unitDetailTextSnapshot = new();
    private readonly List<int> _removedIds = new();
    private readonly HashSet<int> _mappedDiagnosticIds = new();
    private readonly HashSet<int> _activeTextIds = new();
    private readonly HashSet<int> _currentActiveTextIds = new();
    private readonly List<TMP_Text> _discoveryTexts = new();
    private int _discoveryCursor;
    private bool _discoveryActive;
    private bool _discoveryChanged;
    private const int DiscoveryBatchSize = 32;

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
    private Canvas.WillRenderCanvases _willRenderCanvasesHandler;

    public UiCanvasTranslationScanner(IntPtr pointer)
        : base(pointer)
    {
    }

    public static void QueueImmediateRefresh(object component) =>
        QueueRefresh(component, isActivation: false);

    public static void QueueActivationRefresh(object component) =>
        QueueRefresh(component, isActivation: true);

    private static void QueueRefresh(object component, bool isActivation)
    {
        if (component is not TMP_Text text || text == null ||
            TmpFontInstaller.IsUiTextSoftOutlineLayer(text))
            return;

        var instanceId = text.GetInstanceID();
        if (ApplyingTexts.Contains(instanceId))
            return;
        _queuedRequests++;
        // OnEnable runs after the object has joined its active hierarchy, so
        // it can be processed in willRenderCanvases during this same frame.
        // Setter callbacks may still happen before parenting and retain the
        // one-frame deferred fallback.
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
        _fastScanRequested = true;
    }

    public static void RequestFastScan() => _fastScanRequested = true;

    public void OnEnable()
    {
        _fastScanUntil = Time.unscaledTime + FastScanDurationSeconds;
        _willRenderCanvasesHandler = DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(
            new Action(RetryMappedTextsBeforeRender));
        Canvas.add_willRenderCanvases(_willRenderCanvasesHandler);
    }

    public void OnDisable()
    {
        if (_willRenderCanvasesHandler != null)
            Canvas.remove_willRenderCanvases(_willRenderCanvasesHandler);
        _willRenderCanvasesHandler = null;
        PendingTextRefreshes.Clear();
        _discoveryTexts.Clear();
        _discoveryActive = false;
        _processedStates.Clear();
        _activeTextIds.Clear();
        _unitDetailTexts.Clear();
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
        // FindObjectsByType is absent from the stripped Android player.
        // Keep the active hierarchy filter below when using the resource API.
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
                !TmpFontInstaller.IsUnderUiCanvas(text) ||
                TmpFontInstaller.IsUiTextSoftOutlineLayer(text))
                continue;

            var instanceId = text.GetInstanceID();
            _currentActiveTextIds.Add(instanceId);
            var isNew = !_activeTextIds.Contains(instanceId);
            var unchanged = IsProcessingStateCurrent(text, instanceId);
            if (isNew || !unchanged)
                _discoveryChanged = true;
            if (!isNew && unchanged)
                continue;

            QueueImmediateRefresh(text);
        }

        if (_discoveryCursor < _discoveryTexts.Count)
            return;
        _discoveryActive = false;
        _discoveryTexts.Clear();

        if (!_discoveryChanged && _activeTextIds.Count != _currentActiveTextIds.Count)
            _discoveryChanged = true;

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
            ProcessTextCore(text, allowDiagnostics, scheduleRetry);
            UiStyleManager.Apply(text, Plugin.Settings.UiTextOutlineWidth.Value,
                Plugin.Settings.UiTextFaceDilate.Value, TmpFontInstaller.LoadedFont,
                Plugin.Translations.IsKnownTranslationValue(text.text));
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

    private void ProcessTextCore(TMP_Text text, bool allowDiagnostics, bool scheduleRetry)
    {
        if (TmpFontInstaller.IsUnitDetailFontOverride(text))
        {
            _unitDetailTexts[text.GetInstanceID()] = text;
            TmpFontInstaller.ApplyUnitDetailFont(text);
        }
        else
            TmpFontInstaller.ApplyHomeDeckSubSkillNamePresentation(text);

        if (Plugin.Settings?.TranslateUiTexts.Value != true || Plugin.Translations == null)
            return;

        var instanceId = text.GetInstanceID();
        if (TmpFontInstaller.IsUiTranslationExcluded(text))
        {
            TmpFontInstaller.RestoreTranslatedUiText(text);
            return;
        }

        if (TmpFontInstaller.IsSubSkillText(text))
        {
            ApplySubSkillTranslation(text, scheduleRetry);
            return;
        }

        if (TmpFontInstaller.IsUnitDetailUiDescription(text))
            TmpFontInstaller.RestoreTranslatedSubSkillText(text);

        var source = text.text;
        if (string.IsNullOrEmpty(source))
            return;

        if (_observedValues.TryGetValue(instanceId, out var observedTranslated) &&
            string.Equals(observedTranslated, source, StringComparison.Ordinal) &&
            Plugin.Translations.IsKnownUiTextTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            return;
        }

        if (Plugin.Translations.TryTranslateUiText(
                source,
                TmpFontInstaller.GetHierarchyPath(text),
                out var translated))
        {
            if (allowDiagnostics && source.Contains('\n') && _mappedDiagnosticIds.Add(instanceId))
                Plugin.Log?.LogInfo($"UI description mapping detected: {GetHierarchy(text.transform)}");

            if (!string.Equals(source, translated, StringComparison.Ordinal))
                text.text = translated;
            TmpFontInstaller.ApplyToTranslatedUiText(text, translated, source);
            _observedValues[instanceId] = translated;
            return;
        }

        if (Plugin.Translations.IsKnownUiTextTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedUiText(text, source);
            _observedValues[instanceId] = source;
            return;
        }

        // Some shop labels receive a subskill translation in the setter but
        // are outside the dedicated subskill hierarchy. Preserve their owner.
        if (Plugin.Settings.TranslateSubSkills.Value &&
            Plugin.Translations.IsKnownSubSkillTranslationValue(source))
        {
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, source);
            return;
        }

        // Virtualized UI slots keep the same TMP instance while their source
        // changes. An untranslated Japanese value must not retain the font or
        // path style from the previously translated value.
        TmpFontInstaller.RestoreTranslatedUiText(text);
        // Path styles are independent from translation. This permits an
        // explicit font/style rule for untranslated UI text; translatedOnly
        // rules still decline the call inside UiStyleManager.
        UiStyleManager.Apply(
            text,
            Plugin.Settings?.UiTextOutlineWidth.Value ?? 0.3f,
            Plugin.Settings?.UiTextFaceDilate.Value ?? 0.35f,
            TmpFontInstaller.LoadedFont,
            isTranslated: false);
        _observedValues[instanceId] = source;
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
        RefreshTrackedUnitDetailTexts();
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

    private void RefreshTrackedUnitDetailTexts()
    {
        if (_unitDetailTexts.Count == 0)
            return;

        _unitDetailTextSnapshot.Clear();
        foreach (var text in _unitDetailTexts.Values)
            _unitDetailTextSnapshot.Add(text);

        foreach (var text in _unitDetailTextSnapshot)
        {
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                !TmpFontInstaller.IsUnitDetailFontOverride(text))
                continue;

            var instanceId = text.GetInstanceID();
            if (IsProcessingStateCurrent(text, instanceId))
                continue;

            ProcessText(text, allowDiagnostics: false, scheduleRetry: false);
            RecordProcessingState(text);
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
            // Activation already provides the exact target. Starting another
            // global discovery for each event duplicates this queue's work;
            // periodic discovery and explicit invalidation remain fallbacks.
        }

        foreach (var instanceId in _readyPendingIds)
            PendingTextRefreshes.Remove(instanceId);

        foreach (var text in _pendingTextSnapshot)
        {
            if (text == null || text.gameObject == null ||
                !text.gameObject.activeInHierarchy ||
                !TmpFontInstaller.IsUnderUiCanvas(text) ||
                TmpFontInstaller.IsUiTextSoftOutlineLayer(text))
                continue;

            ProcessText(text, allowDiagnostics: false, scheduleRetry: true);
            RecordProcessingState(text);
        }
    }

    private void ApplySubSkillTranslation(TMP_Text text, bool scheduleRetry)
    {
        if (text == null || Plugin.Translations == null)
            return;

        var content = text.text;
        if (string.IsNullOrEmpty(content))
            return;

        TmpFontInstaller.RestoreTranslatedUiText(text);
        if (Plugin.Translations.TryTranslateSubSkill(
                content,
                TmpFontInstaller.GetHierarchyPath(text),
                out var translated))
        {
            if (!string.Equals(content, translated, StringComparison.Ordinal))
                text.text = translated;
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, translated);
            return;
        }

        if (Plugin.Translations.IsKnownSubSkillTranslationValue(content))
        {
            TmpFontInstaller.ApplyToTranslatedSubSkillText(text, content);
        }
        else
        {
            TmpFontInstaller.RestoreTranslatedSubSkillText(text);
        }
    }

    private bool IsProcessingStateCurrent(TMP_Text text, int instanceId)
    {
        if (!_processedStates.TryGetValue(instanceId, out var state))
            return false;

        return state.Version == _processingVersion &&
               string.Equals(
                   state.HierarchyPath,
                   TmpFontInstaller.GetHierarchyPath(text),
                   StringComparison.Ordinal) &&
               state.FontId == GetInstanceId(text.font) &&
               state.MaterialId == GetInstanceId(text.fontSharedMaterial) &&
               Mathf.Approximately(state.LineSpacing, text.lineSpacing) &&
               state.Color == text.color &&
               state.Enabled == text.enabled &&
               string.Equals(state.Text, text.text, StringComparison.Ordinal);
    }

    private void RecordProcessingState(TMP_Text text)
    {
        if (text == null)
            return;

        UiStyleManager.SyncUnderlayLayer(text);
        _processedStates[text.GetInstanceID()] = new ProcessedTextState(
            text.text,
            TmpFontInstaller.GetHierarchyPath(text),
            GetInstanceId(text.font),
            GetInstanceId(text.fontSharedMaterial),
            text.lineSpacing,
            text.color,
            text.enabled,
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
            _unitDetailTexts.Remove(instanceId);
        }
    }

    private static int GetInstanceId(UnityEngine.Object value) =>
        value == null ? 0 : value.GetInstanceID();

    private static string GetHierarchy(Transform transform)
    {
        var parts = new List<string>();
        for (var current = transform; current != null && parts.Count < 24; current = current.parent)
            parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private readonly struct ProcessedTextState
    {
        public ProcessedTextState(
            string text,
            string hierarchyPath,
            int fontId,
            int materialId,
            float lineSpacing,
            Color color,
            bool enabled,
            int version)
        {
            Text = text;
            HierarchyPath = hierarchyPath;
            FontId = fontId;
            MaterialId = materialId;
            LineSpacing = lineSpacing;
            Color = color;
            Enabled = enabled;
            Version = version;
        }

        public string Text { get; }
        public string HierarchyPath { get; }
        public int FontId { get; }
        public int MaterialId { get; }
        public float LineSpacing { get; }
        public Color Color { get; }
        public bool Enabled { get; }
        public int Version { get; }
    }
}
