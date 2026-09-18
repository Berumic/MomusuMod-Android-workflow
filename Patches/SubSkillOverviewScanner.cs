using System;
using System.Collections.Generic;
using MonsterMusumeTDMod.Core;
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
            // Let the game's binding setters fire for this virtualised page.
            // The translation prefix records their original arguments. Do not
            // rely on it alone: some cards update their visible renderer
            // without invoking a string setter.
            Plugin.Translations?.CaptureVisibleSubSkillScanText();
            _waitingForBinding = false;
            // Some list entries complete their nested data binding one frame
            // later. Keep a second dwell before moving to avoid missed rows.
            _nextActionAt = Time.unscaledTime + BindDelaySeconds;
            return;
        }

        if (_step > Steps)
        {
            StopScan("完成");
            return;
        }

        // Capture once more after the second dwell before moving away from
        // this position. The collector deduplicates repeated text.
        Plugin.Translations?.CaptureVisibleSubSkillScanText();
        var progress = (float)_step / Steps;
        _scrollRect.velocity = Vector2.zero;
        _scrollRect.verticalNormalizedPosition = 1f - progress;
        _step++;
        // ScrollRect rebuilds its virtualised children during the next Unity
        // frame. The following delayed pass deliberately waits for that work.
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
        Plugin.Log?.LogInfo($"开始采集副技能列表: {GetHierarchy(_scrollRect)}");
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
        // The current game uses the AttachAbility dialog for the complete
        // assignable sub-skill list. Keep the older Rework dialog compatible.
        // The recipe window is intentionally excluded: it only has crafting
        // and ownership entries.
        foreach (var scrollRect in Resources.FindObjectsOfTypeAll<ScrollRect>())
        {
            if (scrollRect == null || scrollRect.gameObject == null ||
                !scrollRect.gameObject.activeInHierarchy)
                continue;
            if (IsLegacySubSkillListPath(GetHierarchy(scrollRect)))
                return scrollRect;
        }

        foreach (var scrollRect in Resources.FindObjectsOfTypeAll<ScrollRect>())
        {
            if (scrollRect == null || scrollRect.gameObject == null ||
                !scrollRect.gameObject.activeInHierarchy)
                continue;
            if (IsAttachAbilitySubSkillListPath(GetHierarchy(scrollRect)))
                return scrollRect;
        }

        // If the component itself is renamed, walk back from a visible
        // SubSkillTextArea child to its parent ScrollRect.
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy)
                continue;
            var hierarchy = GetHierarchy(text);
            if (!hierarchy.Contains("subskilltextarea", StringComparison.OrdinalIgnoreCase) &&
                !IsAttachAbilitySubSkillListPath(hierarchy))
                continue;
            var parent = text.GetComponentInParent<ScrollRect>();
            if (parent != null && parent.gameObject != null && parent.gameObject.activeInHierarchy &&
                (IsLegacySubSkillListPath(hierarchy) || IsAttachAbilitySubSkillListPath(hierarchy)))
                return parent;
        }

        return null;
    }

    private static bool IsLegacySubSkillListPath(string hierarchy) =>
        hierarchy.Contains("unit_detail_rework", StringComparison.OrdinalIgnoreCase) &&
         hierarchy.Contains("skilldialog", StringComparison.OrdinalIgnoreCase) &&
         hierarchy.Contains("scroll view", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachAbilitySubSkillListPath(string hierarchy) =>
        hierarchy.Contains("unit_detail_attachability", StringComparison.OrdinalIgnoreCase) &&
        hierarchy.Contains("windowbase/scrollview", StringComparison.OrdinalIgnoreCase);

    private static string GetHierarchy(Component component)
    {
        var names = new List<string>();
        for (var current = component.transform; current != null && names.Count < 32; current = current.parent)
            names.Add(current.gameObject.name);
        names.Reverse();
        return string.Join("/", names);
    }
}
