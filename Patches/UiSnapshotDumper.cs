using System;
using MonsterMusumeTDMod.Core;
using UnityEngine;

namespace MonsterMusumeTDMod.Patches;

/// <summary>Writes a UI diagnostic and appends untranslated text to missing.json.</summary>
public sealed class UiSnapshotDumper : MonoBehaviour
{
    public UiSnapshotDumper(IntPtr pointer)
        : base(pointer)
    {
    }

    public void Update()
    {
        if (Plugin.Settings?.EnableUiSnapshotHotkey.Value == true &&
            Input.GetKeyDown(KeyCode.F8))
            Plugin.Translations?.CaptureActiveUiSnapshot();
    }
}
