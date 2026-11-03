using System;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Patches;

/// <summary>
/// Sweeps the virtualised sub-skill list so every reused entry receives
/// its runtime data binding at least once. This does not alter game data.
/// </summary>
public sealed class SubSkillOverviewScanner : MonoBehaviour
{
    private const int Steps = 160;
    private const float BindDelaySeconds = 0.12f;

    private ScrollRect _scrollRect;
    private int _step;
    private bool _waitingForBinding;
    private float _nextActionAt;

    public SubSkillOverviewScanner(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        if (Plugin.Settings == null || !Plugin.Settings.EnableSubSkillScanHotkey.Value)
            return;

        if (Input.GetKeyDown(KeyCode.F9))
        {
            if (_scrollRect != null)
                StopScan("已取消");
            else
                StartScan();
        }

        if (_scrollRect == null || Time.unscaledTime < _nextActionAt)
            return;

        if (_waitingForBinding)
        {
            Plugin.Translations?.CaptureVisibleSubSkillScanText();
            _waitingForBinding = false;
            _nextActionAt = Time.unscaledTime + BindDelaySeconds;
            return;
        }

        if (_step > Steps)
        {
            StopScan("完成");
            return;
        }

        Plugin.Translations?.CaptureVisibleSubSkillScanText();
        var progress = (float)_step / Steps;
        _scrollRect.velocity = Vector2.zero;
        _scrollRect.verticalNormalizedPosition = 1f - progress;
        _step++;
        _waitingForBinding = true;
        _nextActionAt = Time.unscaledTime + BindDelaySeconds;
    }

    private void StartScan()
    {
        _scrollRect = FindSubSkillList();
        if (_scrollRect == null)
        {
            Plugin.Log?.LogWarning("未找到副技能列表。请打开“选择要设置的副技能”滚动列表后再按 F9。");
            return;
        }

        _step = 0;
        _waitingForBinding = false;
        _nextActionAt = Time.unscaledTime;
        Plugin.Translations?.BeginSubSkillScan();
        Plugin.Log?.LogInfo($"开始采集副技能列表: {UiHierarchyClassifier.GetHierarchyPath(_scrollRect, 32)}");
        Plugin.Log?.LogInfo("请保持当前页面打开，约 40 秒内不要操作滚动条。");
    }

    private void StopScan(string result)
    {
        var count = Plugin.Translations?.EndSubSkillScan() ?? 0;
        _scrollRect = null;
        _waitingForBinding = false;
        Plugin.Log?.LogInfo($"副技能列表采集{result}：{count} 条文本 -> subskill_scan.tsv");
    }

    private static ScrollRect FindSubSkillList()
    {
        foreach (var scrollRect in Resources.FindObjectsOfTypeAll<ScrollRect>())
        {
            if (scrollRect == null || scrollRect.gameObject == null ||
                !scrollRect.gameObject.activeInHierarchy)
                continue;
            if (UiHierarchyClassifier.IsLegacySubSkillListPath(UiHierarchyClassifier.GetHierarchyPath(scrollRect, 32)))
                return scrollRect;
        }

        foreach (var scrollRect in Resources.FindObjectsOfTypeAll<ScrollRect>())
        {
            if (scrollRect == null || scrollRect.gameObject == null ||
                !scrollRect.gameObject.activeInHierarchy)
                continue;
            if (UiHierarchyClassifier.IsAttachAbilitySubSkillHierarchy(UiHierarchyClassifier.GetHierarchyPath(scrollRect, 32)))
                return scrollRect;
        }

        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy)
                continue;
            var hierarchy = UiHierarchyClassifier.GetHierarchyPath(text, 32);
            if (!hierarchy.Contains("subskilltextarea", StringComparison.OrdinalIgnoreCase) &&
                !UiHierarchyClassifier.IsAttachAbilitySubSkillHierarchy(hierarchy))
                continue;
            var parent = text.GetComponentInParent<ScrollRect>();
            if (parent != null && parent.gameObject != null && parent.gameObject.activeInHierarchy &&
                (UiHierarchyClassifier.IsLegacySubSkillListPath(hierarchy) ||
                 UiHierarchyClassifier.IsAttachAbilitySubSkillHierarchy(hierarchy)))
                return parent;
        }

        return null;
    }
}
