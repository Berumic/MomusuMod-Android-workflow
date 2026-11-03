using System;
using System.Collections.Generic;
using MonsterMusumeTDMod.Services;
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Patches;

public sealed class StoryLegacyFontReplacer : MonoBehaviour
{
    private const float ScanIntervalSeconds = 0.05f;
    private const float ActiveDiscoveryIntervalSeconds = 0.5f;
    private const float IdleDiscoveryIntervalSeconds = 1.5f;

    private float _nextScanTime = 1.0f;
    private float _nextDiscoveryTime = 1.0f;
    private readonly List<Text> _storyTexts = new();

    public StoryLegacyFontReplacer(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        if (Time.unscaledTime < _nextScanTime)
            return;

        _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;
        if (Time.unscaledTime >= _nextDiscoveryTime)
        {
            RefreshStoryTextTargets();
            var isStoryActive = _storyTexts.Count > 0 ||
                                !string.IsNullOrEmpty(StorySceneContext.PlaybackId) ||
                                !string.IsNullOrEmpty(ScenarioContext.CurrentId);
            _nextDiscoveryTime = Time.unscaledTime + (isStoryActive
                ? ActiveDiscoveryIntervalSeconds
                : IdleDiscoveryIntervalSeconds);
        }

        if (_storyTexts.Count == 0)
            return;

        TmpFontInstaller.RefreshR18StoryPresentation();
        for (var index = _storyTexts.Count - 1; index >= 0; index--)
        {
            var text = _storyTexts[index];
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !UiHierarchyClassifier.IsStoryMessageText(text))
            {
                _storyTexts.RemoveAt(index);
                continue;
            }

            if (text.enabled)
                PatchManager.ProcessDiscoveredLegacyText(text);
        }
    }

    private void RefreshStoryTextTargets()
    {
        _storyTexts.Clear();
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
                _storyTexts.Add(text);
        }
    }
}
