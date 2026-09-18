using System;
using System.Collections.Generic;
using MonsterMusumeTDMod.Patches;
using MonsterMusumeTDMod.Services;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Patches;

public sealed class StoryLegacyFontReplacer : MonoBehaviour
{
    private const float ScanIntervalSeconds = 0.05f;
    private const float DiscoveryIntervalSeconds = 0.5f;

    private float _nextScanTime;
    private float _nextDiscoveryTime;
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
            _nextDiscoveryTime = Time.unscaledTime + DiscoveryIntervalSeconds;
            RefreshStoryTextTargets();
        }

        if (_storyTexts.Count == 0)
            return;

        TmpFontInstaller.RefreshR18StoryPresentation();
        for (var index = _storyTexts.Count - 1; index >= 0; index--)
        {
            var text = _storyTexts[index];
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy ||
                !TmpFontInstaller.IsStoryMessageText(text))
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
        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (text != null && text.gameObject != null && text.gameObject.activeInHierarchy &&
                TmpFontInstaller.IsStoryMessageText(text))
                _storyTexts.Add(text);
        }
    }
}
