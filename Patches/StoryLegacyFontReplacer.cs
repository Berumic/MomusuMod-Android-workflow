using System;
using System.Collections.Generic;
using MonsterMusumeTDMod.Services;
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Patches;

public sealed class StoryLegacyFontReplacer : MonoBehaviour
{
    private const float ScanIntervalSeconds = 0.1f;
    private const float FallbackDiscoveryIntervalSeconds = 2.5f;
    private const float IdleDiscoveryIntervalSeconds = 1.5f;

    private static StoryLegacyFontReplacer _instance;
    private float _nextScanTime = 1.0f;
    private float _nextDiscoveryTime = 1.0f;
    private readonly List<Text> _storyTexts = new();
    private readonly HashSet<int> _storyTextIds = new();

    public StoryLegacyFontReplacer(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Awake()
    {
        _instance = this;
    }

    public void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    internal static void NotifyStoryTextActive(Text text)
    {
        var instance = _instance;
        if (instance == null || text == null)
            return;
        var id = text.GetInstanceID();
        if (instance._storyTextIds.Add(id))
            instance._storyTexts.Add(text);
    }

    public void Update()
    {
        if (Time.unscaledTime < _nextScanTime)
            return;

        for (var index = _storyTexts.Count - 1; index >= 0; index--)
        {
            var text = _storyTexts[index];
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsStoryMessageText(text))
            {
                if (text != null)
                    _storyTextIds.Remove(text.GetInstanceID());
                _storyTexts.RemoveAt(index);
            }
        }

        var isStoryActive = _storyTexts.Count > 0 ||
                            !string.IsNullOrEmpty(StorySceneContext.EffectivePlaybackId) ||
                            !string.IsNullOrEmpty(ScenarioContext.CurrentId) ||
                            !string.IsNullOrEmpty(ScenarioContext.CurrentResourceId);
        if (!isStoryActive)
        {
            _nextScanTime = Time.unscaledTime + IdleDiscoveryIntervalSeconds;
            return;
        }

        _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;

        // Only fall back to a global Text scan when a story scene is active but no
        // story Text component has been registered via NotifyStoryTextActive yet.
        if (_storyTexts.Count == 0 && Time.unscaledTime >= _nextDiscoveryTime)
        {
            RefreshStoryTextTargets();
            _nextDiscoveryTime = Time.unscaledTime + FallbackDiscoveryIntervalSeconds;
        }

        if (_storyTexts.Count == 0)
            return;

        TmpFontInstaller.RefreshR18StoryPresentation();
        for (var index = _storyTexts.Count - 1; index >= 0; index--)
        {
            var text = _storyTexts[index];
            if (text != null && text.enabled)
                PatchManager.ProcessDiscoveredLegacyText(text);
        }
    }

    private void RefreshStoryTextTargets()
    {
        _storyTexts.Clear();
        _storyTextIds.Clear();
#if ANDROID
        var texts = Resources.FindObjectsOfTypeAll<Text>();
#else
        var texts = UnityEngine.Object.FindObjectsByType<Text>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
#endif
        foreach (var text in texts)
        {
            if (text != null && text.gameObject != null && text.gameObject.activeInHierarchy &&
                UiHierarchyClassifier.IsStoryMessageText(text))
            {
                if (_storyTextIds.Add(text.GetInstanceID()))
                    _storyTexts.Add(text);
            }
        }
    }
}
