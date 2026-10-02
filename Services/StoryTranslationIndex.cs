using System;
using System.Collections.Generic;

namespace MonsterMusumeTDMod.Services;

/// <summary>Exact scene lookup with a unique-only global fallback.</summary>
public sealed class StoryTranslationIndex
{
    private readonly Dictionary<string, Dictionary<string, string>> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _global = new(StringComparer.Ordinal);

    public void Clear()
    {
        _scenes.Clear();
        _global.Clear();
    }

    public void Add(string scene, string source, string translation)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation))
            return;
        if (!_scenes.TryGetValue(scene, out var table))
            _scenes.Add(scene, table = new Dictionary<string, string>(StringComparer.Ordinal));
        AddValue(table, source, translation);
        AddValue(_global, source, translation);
    }

    private static void AddValue(Dictionary<string, string> table, string source, string translation)
    {
        if (!table.TryGetValue(source, out var previous))
            table.Add(source, translation);
        else if (!string.Equals(previous, translation, StringComparison.Ordinal))
            table[source] = null; // A conflict stays ambiguous regardless of file enumeration order.
    }

    public bool TryGet(string source, string scene, out string translation, out bool conflict)
    {
        conflict = false;
        translation = source;
        string value;
        if (!string.IsNullOrEmpty(scene) && _scenes.TryGetValue(scene, out var table) &&
            table.TryGetValue(source, out value))
        {
            conflict = value == null;
            if (!conflict) translation = value;
            return !conflict;
        }
        if (!_global.TryGetValue(source, out value))
            return false;
        conflict = value == null;
        if (!conflict) translation = value;
        return !conflict;
    }
}
