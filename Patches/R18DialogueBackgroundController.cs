using System;
using System.Collections.Generic;
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
    private readonly Dictionary<int, OutlineState> _buttonOutlines = new();
    private readonly Dictionary<int, OutlineState> _storyOutlines = new();
    private readonly Dictionary<int, ShadowState> _storyShadows = new();
    private float _nextScanTime;
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
        foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
            changed += HideBackground(image) ? 1 : 0;
        foreach (var rawImage in Resources.FindObjectsOfTypeAll<RawImage>())
            changed += HideBackground(rawImage) ? 1 : 0;

        if (changed > 0)
            Core.Plugin.Log.LogInfo($"R18 dialogue background hidden: {changed} image(s)");
        _isTransparent = _originalColors.Count > 0;
    }

    private bool HideBackground(Graphic graphic)
    {
        if (graphic == null || graphic.gameObject == null || !graphic.gameObject.activeInHierarchy ||
            !IsDialogueBackground(graphic))
            return false;

        var id = graphic.GetInstanceID();
        var changed = false;
        if (!_originalColors.ContainsKey(id))
        {
            _originalColors[id] = (graphic, graphic.color, graphic.enabled);
            changed = true;
        }

        // Unlike Auto/Skip, BackLogButton uses its own Image as the Button
        // target graphic. Its transition can write the colour back every
        // frame, so alpha alone is not sufficient to keep its background off.
        if (IsBackLogButtonBackground(graphic) && graphic.enabled)
            graphic.enabled = false;

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
            if (IsBackLogButtonBackground(graphic) && graphic.enabled)
                graphic.enabled = false;
            if (!Mathf.Approximately(graphic.color.a, 0f))
            {
                var transparent = graphic.color;
                transparent.a = 0f;
                graphic.color = transparent;
            }
        }
    }

    private bool ApplyButtonOutlines()
    {
        if (!Core.Plugin.Settings.EnableR18ButtonTextOutline.Value)
        {
            RestoreButtonOutlines();
            return true;
        }

        var foundTarget = false;
        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !IsDialogueControlLabel(text))
                continue;

            var id = text.GetInstanceID();
            var outline = text.GetComponent<Outline>();
            var added = outline == null;
            if (added)
                outline = text.gameObject.AddComponent<Outline>();
            if (outline == null)
                continue;

            foundTarget = true;
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
        }

        return foundTarget;
    }

    private void RestoreButtonOutlines()
    {
        foreach (var entry in _buttonOutlines.Values)
        {
            entry.Restore();
        }

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
        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !TryGetStoryOutlineColor(text, out var outlineColor))
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

    private static bool IsDialogueBackground(Graphic graphic)
    {
        if (graphic is not Image && graphic is not RawImage)
            return false;

        var names = new List<string>();
        for (var current = graphic.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        var path = string.Join("/", names);

        if (!path.Contains("AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/", StringComparison.Ordinal))
            return false;

        // The dialogue renderer itself is Text/UguiNovelText, never an
        // Image or RawImage. Therefore every image beneath RootChildren is
        // window chrome, a name plate, or a control-button background.
        return true;
    }

    private static bool IsDialogueControlLabel(Text text)
    {
        var names = new List<string>();
        for (var current = text.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        var path = string.Join("/", names);
        return path.Contains("AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/", StringComparison.Ordinal) &&
               IsDialogueControlPath(path);
    }

    private static bool TryGetStoryOutlineColor(Text text, out Color outlineColor)
    {
        var names = new List<string>();
        for (var current = text.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        var path = string.Join("/", names);

        if (!path.Contains("AdvEngine/UI/MessageWindowManager/MessageWindow/RootChildren/MessageTexts/", StringComparison.Ordinal))
        {
            outlineColor = default;
            return false;
        }

        if (string.Equals(text.transform.name, "MessageText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = Color.white;
            return true;
        }

        if (string.Equals(text.transform.name, "NameText", StringComparison.OrdinalIgnoreCase))
        {
            outlineColor = DarkOutlineColor;
            return true;
        }

        outlineColor = default;
        return false;
    }

    private static bool IsDialogueControlPath(string path) =>
        path.Contains("/Auto/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Skip/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Log/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/BackLogButton/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/LogButton/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/BackLog/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Backlog/", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackLogButtonBackground(Graphic graphic)
    {
        var names = new List<string>();
        for (var current = graphic.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        var path = string.Join("/", names);
        return path.EndsWith("/BackLogButton", StringComparison.OrdinalIgnoreCase);
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
        _cachedR18State = HasActiveR18Character();
        _nextR18StateProbeTime = now + R18StateCacheSeconds;
        return _cachedR18State;
    }

    public static void InvalidatePresentation()
    {
        _nextR18StateProbeTime = 0f;
        _forcePresentationRefresh = true;
    }

    private static bool HasActiveR18Character()
    {
        foreach (var mosaic in Resources.FindObjectsOfTypeAll<SpineMosaic>())
        {
            if (mosaic == null || mosaic.gameObject == null || !mosaic.gameObject.activeInHierarchy)
                continue;

            var names = new List<string>();
            for (var current = mosaic.transform; current != null; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            var path = string.Join("/", names);
            if (path.Contains("/layer_R18/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/r18_scenes/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
