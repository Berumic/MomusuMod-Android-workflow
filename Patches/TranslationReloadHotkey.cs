using System;
using MonsterMusumeTDMod.Core;
using MonsterMusumeTDMod.Services;
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

/// <summary>Reloads translation tables and refreshes active manual UI text on F6, or toggles translation on F7.</summary>
public sealed class TranslationReloadHotkey : MonoBehaviour
{
    public TranslationReloadHotkey(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.F7) && Plugin.Settings != null)
        {
            var enabled = !Plugin.Settings.Enabled.Value;
            Plugin.Settings.Enabled.Value = enabled;
            Plugin.Settings.Save();
            UiCanvasTranslationScanner.InvalidateProcessingCache();
            R18DialogueBackgroundController.InvalidatePresentation();

            if (enabled && Plugin.Translations != null)
            {
                Plugin.Translations.LoadStatic();
                UiStyleManager.Reload();
                var enabledRefreshed = TmpFontInstaller.ReloadVisibleUiTranslations(Plugin.Translations);
                var enabledSubSkillsRefreshed = TmpFontInstaller.RefreshVisibleSubSkillPresentation();
                var enabledUnitDetailRefreshed = TmpFontInstaller.RefreshVisibleUnitDetailPresentation();
                Plugin.Log?.LogInfo(
                    $"Translation enabled with F7; refreshed {enabledRefreshed} active UI text(s), " +
                    $"{enabledSubSkillsRefreshed} sub-skill text(s), and {enabledUnitDetailRefreshed} unit-detail text(s)");
            }
            else
            {
                var restored = TmpFontInstaller.RestoreAllVisibleTranslations();
                Plugin.Log?.LogInfo(
                    $"Translation disabled with F7; restored {restored} active text(s) to original language and font");
            }

            return;
        }

        if (!Input.GetKeyDown(KeyCode.F6) || Plugin.Translations == null)
            return;

        // Reload config first so any manual edits to ManualSyncWithF6 or UI outline settings take effect immediately.
        Plugin.Settings?.Reload();
        if (Plugin.Settings?.ManualSyncWithF6.Value == true)
            TranslationUpdateController.RequestManualSync();
        else
            Plugin.Log?.LogInfo("F6 manual translation sync is disabled; reloading local files only");

        Plugin.Translations.LoadStatic();
        UiStyleManager.Reload();
        UiCanvasTranslationScanner.InvalidateProcessingCache();
        R18DialogueBackgroundController.InvalidatePresentation();
        var refreshed = TmpFontInstaller.ReloadVisibleUiTranslations(Plugin.Translations);
        var subSkillsRefreshed = TmpFontInstaller.RefreshVisibleSubSkillPresentation();
        var unitDetailRefreshed = TmpFontInstaller.RefreshVisibleUnitDetailPresentation();
        Plugin.Log?.LogInfo(
            $"Translation tables and UI appearance reloaded with F6; " +
            $"refreshed {refreshed} active UI text(s), {subSkillsRefreshed} sub-skill text(s), " +
            $"and {unitDetailRefreshed} unit-detail text(s)");
    }
}
