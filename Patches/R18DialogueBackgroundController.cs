using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MonsterMusumeTDMod.Services;
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Patches;

/// <summary>Hides only the dialogue window's background while an R18 script is active.</summary>
public sealed class R18DialogueBackgroundController : MonoBehaviour
{
    private const float ScanIntervalSeconds = 0.1f;
    private const float R18StateCacheSeconds = 0.25f;
    private const float BackgroundDiscoverySeconds = 1f;
    private const float PresentationDiscoverySeconds = 0.25f;
    private const int PresentationStablePassesRequired = 2;

    private static readonly Color32 DarkOutlineColor = new(0x54, 0x4A, 0x4A, 0xFF);
    private readonly Dictionary<int, (Graphic Graphic, Color Color, bool Enabled)> _originalColors = new();
    private readonly Dictionary<int, Text> _closeButtonLabels = new();
    private readonly Dictionary<int, OutlineState> _buttonOutlines = new();
    private readonly Dictionary<int, OutlineState> _storyOutlines = new();
    private readonly Dictionary<int, ShadowState> _storyShadows = new();

    private float _nextScanTime = 1.2f;
    private float _nextBackgroundDiscoveryTime;
    private float _nextPresentationDiscoveryTime;
    private bool _isTransparent;
    private bool _presentationPending;
    private int _presentationStablePasses;
    private bool _lastR18Scene;
    private bool _hasR18State;

    private static float _nextR18StateProbeTime;
    private static string _cachedScenarioId;
    private static bool _cachedR18State;
    private static bool _forcePresentationRefresh;

    public R18DialogueBackgroundController(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        if (Time.unscaledTime < _nextScanTime)
            return;

        _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;
        var isR18Scene = IsR18SceneActive();
        var forcePresentationRefresh = _forcePresentationRefresh;
        var stateChanged = !_hasR18State || isR18Scene != _lastR18Scene;
        var presentationStateChanged = stateChanged || forcePresentationRefresh;
        _forcePresentationRefresh = false;
        _hasR18State = true;
        _lastR18Scene = isR18Scene;

        if (isR18Scene && presentationStateChanged)
        {
            _presentationPending = true;
            _presentationStablePasses = 0;
            _nextPresentationDiscoveryTime = 0f;
        }
        else if (!isR18Scene && presentationStateChanged)
        {
            _presentationPending = false;
            _presentationStablePasses = 0;
        }

        var shouldHide = Core.Plugin.Settings.HideR18DialogueBackground.Value && isR18Scene;
        if (shouldHide)
        {
            if (presentationStateChanged || Time.unscaledTime >= _nextBackgroundDiscoveryTime)
            {
                _nextBackgroundDiscoveryTime = Time.unscaledTime + BackgroundDiscoverySeconds;
                HideBackgrounds();
            }
            else
            {
                MaintainHiddenBackgrounds();
            }
        }
        else if (_isTransparent)
            RestoreBackgrounds();

        if (isR18Scene && _presentationPending &&
            Time.unscaledTime >= _nextPresentationDiscoveryTime)
        {
            _nextPresentationDiscoveryTime = Time.unscaledTime + PresentationDiscoverySeconds;
            var buttonsReady = ApplyButtonOutlines();
            var storyReady = ApplyStoryOutlines();
            TmpFontInstaller.RefreshR18StoryPresentation(force: true);

            if (buttonsReady && storyReady)
                _presentationStablePasses++;
            else
                _presentationStablePasses = 0;

            if (_presentationStablePasses >= PresentationStablePassesRequired)
            {
                _presentationPending = false;
                Core.Plugin.Log.LogInfo(
                    "R18 dialogue presentation applied; discovery stopped until the next R18 scene");
            }
        }
        else if (presentationStateChanged)
        {
            RestoreButtonOutlines();
            RestoreStoryOutlines();
        }
    }

    public void OnDestroy()
    {
        RestoreBackgrounds();
        RestoreButtonOutlines();
        RestoreStoryOutlines();
    }

    private void HideBackgrounds()
    {
        var changed = 0;
        // Query concrete IL2CPP types. Querying Graphic itself does not
        // reliably include derived UI components in this game build.
#if ANDROID
        var images = Resources.FindObjectsOfTypeAll<Image>();
        var rawImages = Resources.FindObjectsOfTypeAll<RawImage>();
#else
        var images = UnityEngine.Object.FindObjectsByType<Image>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        var rawImages = UnityEngine.Object.FindObjectsByType<RawImage>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var image in images)
            changed += HideBackground(image) ? 1 : 0;
        foreach (var rawImage in rawImages)
            changed += HideBackground(rawImage) ? 1 : 0;

        if (changed > 0)
            Core.Plugin.Log.LogInfo($"R18 dialogue background hidden: {changed} image(s)");
        _isTransparent = _originalColors.Count > 0;
    }

    private bool HideBackground(Graphic graphic)
    {
        if (graphic == null || graphic.gameObject == null || !graphic.gameObject.activeInHierarchy ||
            !UiHierarchyClassifier.IsDialogueBackground(graphic))
            return false;

        var id = graphic.GetInstanceID();
        var changed = false;
        if (!_originalColors.ContainsKey(id))
        {
            _originalColors[id] = (graphic, graphic.color, graphic.enabled);
            changed = true;
        }

        // Unlike Auto/Skip, BackLogButton and CloseButton use their own Image
        // as the Button target graphic. Their transitions can write the colour
        // back every frame, so alpha alone is not sufficient to keep them off.
        if (UiHierarchyClassifier.IsBackLogButtonBackground(graphic) && graphic.enabled)
            graphic.enabled = false;

        if (UiHierarchyClassifier.IsCloseButtonBackground(graphic))
            EnsureCloseButtonLabel(graphic);

        if (!Mathf.Approximately(graphic.color.a, 0f))
        {
            var transparent = graphic.color;
            transparent.a = 0f;
            graphic.color = transparent;
        }
        return changed;
    }

    private void RestoreBackgrounds()
    {
        foreach (var label in _closeButtonLabels.Values)
        {
            if (label != null && label.gameObject != null)
                label.gameObject.SetActive(false);
        }

        foreach (var entry in _originalColors.Values)
        {
            if (entry.Graphic != null)
            {
                entry.Graphic.color = entry.Color;
                entry.Graphic.enabled = entry.Enabled;
            }
        }

        if (_isTransparent)
            Core.Plugin.Log.LogInfo("R18 dialogue background restored");
        _originalColors.Clear();
        _isTransparent = false;
    }

    private void MaintainHiddenBackgrounds()
    {
        foreach (var entry in _originalColors.Values)
        {
            var graphic = entry.Graphic;
            if (graphic == null)
                continue;
            if (UiHierarchyClassifier.IsBackLogButtonBackground(graphic) && graphic.enabled)
                graphic.enabled = false;
            if (UiHierarchyClassifier.IsCloseButtonBackground(graphic))
                EnsureCloseButtonLabel(graphic);
            if (!Mathf.Approximately(graphic.color.a, 0f))
            {
                var transparent = graphic.color;
                transparent.a = 0f;
                graphic.color = transparent;
            }
        }
    }

    private void EnsureCloseButtonLabel(Graphic closeButtonGraphic)
    {
        if (closeButtonGraphic == null || closeButtonGraphic.transform == null)
            return;

        var id = closeButtonGraphic.GetInstanceID();
        if (_closeButtonLabels.TryGetValue(id, out var existingLabel) && existingLabel != null)
        {
            if (!existingLabel.gameObject.activeSelf)
                existingLabel.gameObject.SetActive(true);
            if (!existingLabel.enabled)
                existingLabel.enabled = true;
            if (Core.Plugin.Settings.EnableR18ButtonTextOutline.Value)
                ApplyOutlineToComponent(existingLabel);
            return;
        }

        try
        {
            var existingChild = closeButtonGraphic.transform.Find("Label");
            var labelObject = existingChild != null ? existingChild.gameObject : null;
            if (labelObject == null)
            {
                labelObject = new GameObject(
                    "Label",
                    new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
                labelObject.transform.SetParent(closeButtonGraphic.transform, worldPositionStays: false);
            }

            var rect = labelObject.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = Vector2.zero;
                rect.localScale = Vector3.one;
            }

            var label = labelObject.GetComponent<Text>();
            if (label == null)
                label = labelObject.AddComponent<Text>();
            if (label == null)
                return;

            UiHierarchyClassifier.RegisterInternalLayer(labelObject, label);

            Text referenceLabel = null;
            var rootChildren = closeButtonGraphic.transform.parent;
            if (rootChildren != null)
            {
                referenceLabel = rootChildren.Find("BackLogButton/Label")?.GetComponent<Text>() ??
                                 rootChildren.Find("Auto/Background/Label")?.GetComponent<Text>() ??
                                 rootChildren.Find("Skip/Background/Label")?.GetComponent<Text>();
            }

            label.font = referenceLabel != null && referenceLabel.font != null
                ? referenceLabel.font
                : Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = referenceLabel != null ? referenceLabel.fontSize : 22;
            label.fontStyle = referenceLabel != null ? referenceLabel.fontStyle : FontStyle.Bold;
            label.color = referenceLabel != null
                ? referenceLabel.color
                : new Color32(0xEF, 0xDC, 0xBE, 0xFF);
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = true;
            label.text = "×";
            label.enabled = true;
            labelObject.SetActive(true);

            _closeButtonLabels[id] = label;
            if (Core.Plugin.Settings.EnableR18ButtonTextOutline.Value)
                ApplyOutlineToComponent(label);
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"Failed to create CloseButton label: {ex.Message}");
        }
    }

    private bool ApplyOutlineToComponent(Graphic graphic)
    {
        if (graphic == null || graphic.gameObject == null)
            return false;

        var id = graphic.GetInstanceID();
        var outline = graphic.GetComponent<Outline>();
        var added = outline == null;
        if (added)
            outline = graphic.gameObject.AddComponent<Outline>();
        if (outline == null)
            return false;

        if (!_buttonOutlines.ContainsKey(id))
            _buttonOutlines[id] = new OutlineState(outline, added);

        if (outline.effectColor != DarkOutlineColor)
            outline.effectColor = DarkOutlineColor;
        var targetDistance = new Vector2(1f, 1f);
        if (outline.effectDistance != targetDistance)
            outline.effectDistance = targetDistance;
        if (!outline.useGraphicAlpha)
            outline.useGraphicAlpha = true;
        if (!outline.enabled)
            outline.enabled = true;
        return true;
    }

    private bool ApplyButtonOutlines()
    {
        if (!Core.Plugin.Settings.EnableR18ButtonTextOutline.Value)
        {
            RestoreButtonOutlines();
            return true;
        }

        var foundTarget = false;
#if ANDROID
        var texts = Resources.FindObjectsOfTypeAll<Text>();
        var images = Resources.FindObjectsOfTypeAll<Image>();
#else
        var texts = UnityEngine.Object.FindObjectsByType<Text>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        var images = UnityEngine.Object.FindObjectsByType<Image>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var text in texts)
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsDialogueControlLabel(text))
                continue;

            if (ApplyOutlineToComponent(text))
                foundTarget = true;
        }

        foreach (var image in images)
        {
            if (image == null || image.gameObject == null || !image.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsDialogueCheckmark(image))
                continue;

            ApplyOutlineToComponent(image);
        }

        return foundTarget;
    }

    private void RestoreButtonOutlines()
    {
        foreach (var entry in _buttonOutlines.Values)
            entry.Restore();

        _buttonOutlines.Clear();
    }

    private bool ApplyStoryOutlines()
    {
        if (!Core.Plugin.Settings.EnableR18WhiteTextOutline.Value)
        {
            RestoreStoryOutlines();
            return true;
        }

        var foundMessageText = false;
        var foundNameText = false;
#if ANDROID
        var texts = Resources.FindObjectsOfTypeAll<Text>();
#else
        var texts = UnityEngine.Object.FindObjectsByType<Text>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var text in texts)
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.TryGetStoryOutlineColor(text, DarkOutlineColor, out var outlineColor))
                continue;

            var id = text.GetInstanceID();
            var outline = text.GetComponent<Outline>();
            var added = outline == null;
            if (added)
                outline = text.gameObject.AddComponent<Outline>();
            if (outline == null)
                continue;

            if (string.Equals(text.transform.name, "MessageText", StringComparison.OrdinalIgnoreCase))
                foundMessageText = true;
            else if (string.Equals(text.transform.name, "NameText", StringComparison.OrdinalIgnoreCase))
                foundNameText = true;

            if (!_storyOutlines.ContainsKey(id))
                _storyOutlines[id] = new OutlineState(outline, added);

            if (outline.effectColor != outlineColor)
                outline.effectColor = outlineColor;
            var targetDistance = new Vector2(1f, 1f);
            if (outline.effectDistance != targetDistance)
                outline.effectDistance = targetDistance;
            if (!outline.useGraphicAlpha)
                outline.useGraphicAlpha = true;
            if (!outline.enabled)
                outline.enabled = true;

            // Some story prefabs use a Shadow rather than an Outline for the
            // visible edge. Recolour it too so a pre-existing white effect
            // cannot remain behind the R18 name outline.
            foreach (var shadow in text.GetComponents<Shadow>())
            {
                if (shadow == null || shadow is Outline)
                    continue;

                var shadowId = shadow.GetInstanceID();
                if (!_storyShadows.ContainsKey(shadowId))
                    _storyShadows[shadowId] = new ShadowState(shadow);
                if (shadow.effectColor != outlineColor)
                    shadow.effectColor = outlineColor;
                if (!shadow.enabled)
                    shadow.enabled = true;
            }
        }

        return foundMessageText && foundNameText;
    }

    private void RestoreStoryOutlines()
    {
        foreach (var entry in _storyOutlines.Values)
            entry.Restore();

        foreach (var entry in _storyShadows.Values)
            entry.Restore();

        _storyOutlines.Clear();
        _storyShadows.Clear();
    }

    private readonly struct OutlineState
    {
        public OutlineState(Outline outline, bool added)
        {
            Outline = outline;
            Added = added;
            Enabled = outline.enabled;
            Color = outline.effectColor;
            Distance = outline.effectDistance;
            UseGraphicAlpha = outline.useGraphicAlpha;
        }

        private Outline Outline { get; }
        private bool Added { get; }
        private bool Enabled { get; }
        private Color Color { get; }
        private Vector2 Distance { get; }
        private bool UseGraphicAlpha { get; }

        public void Restore()
        {
            if (Outline == null)
                return;

            if (Added)
            {
                UnityEngine.Object.Destroy(Outline);
                return;
            }

            Outline.effectColor = Color;
            Outline.effectDistance = Distance;
            Outline.useGraphicAlpha = UseGraphicAlpha;
            Outline.enabled = Enabled;
        }
    }

    private readonly struct ShadowState
    {
        public ShadowState(Shadow shadow)
        {
            Shadow = shadow;
            Enabled = shadow.enabled;
            Color = shadow.effectColor;
        }

        private Shadow Shadow { get; }
        private bool Enabled { get; }
        private Color Color { get; }

        public void Restore()
        {
            if (Shadow == null)
                return;

            Shadow.effectColor = Color;
            Shadow.enabled = Enabled;
        }
    }

    public static bool IsR18SceneActive()
    {
        var scenarioId = ScenarioContext.CurrentId ?? string.Empty;
        if (scenarioId.EndsWith("_r18", StringComparison.OrdinalIgnoreCase))
        {
            _cachedScenarioId = scenarioId;
            _cachedR18State = true;
            _nextR18StateProbeTime = Time.unscaledTime + R18StateCacheSeconds;
            return true;
        }

        var now = Time.unscaledTime;
        if (string.Equals(_cachedScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase) &&
            now < _nextR18StateProbeTime)
            return _cachedR18State;

        _cachedScenarioId = scenarioId;
        _cachedR18State = MosaicSuppressor.HasActiveR18Character();
        _nextR18StateProbeTime = now + R18StateCacheSeconds;
        return _cachedR18State;
    }

    public static void InvalidatePresentation()
    {
        _nextR18StateProbeTime = 0f;
        _forcePresentationRefresh = true;
    }
}
