using System;
using System.Collections.Generic;

namespace MonsterMusumeTDMod.Services;

/// <summary>Exact scene lookup with a unique-only global fallback for timing-sensitive rows and promoted fallback for plain dialogue.</summary>
public sealed class StoryTranslationIndex
{
    private readonly Dictionary<string, Dictionary<string, string>> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _global = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fallbackGlobal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _globalCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uniqueSceneBySource = new(StringComparer.Ordinal);

    public void Clear()
    {
        _scenes.Clear();
        _global.Clear();
        _fallbackGlobal.Clear();
        _globalCounts.Clear();
        _uniqueSceneBySource.Clear();
    }

    public void Add(string scene, string source, string translation)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation))
            return;

        var isSelfMapped = string.Equals(source, translation, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(scene))
        {
            if (!_scenes.TryGetValue(scene, out var table))
                _scenes.Add(scene, table = new Dictionary<string, string>(StringComparer.Ordinal));

            if (!table.TryGetValue(source, out var existingInScene))
            {
                table.Add(source, translation);
            }
            else if (!isSelfMapped && string.Equals(existingInScene, source, StringComparison.Ordinal))
            {
                // Promote a real translation over an untranslated self-mapped row in the same scene.
                table[source] = translation;
            }

            if (!isSelfMapped)
            {
                if (!_uniqueSceneBySource.TryGetValue(source, out var existingScene))
                    _uniqueSceneBySource.Add(source, scene);
                else if (existingScene != null && !string.Equals(existingScene, scene, StringComparison.OrdinalIgnoreCase))
                    _uniqueSceneBySource[source] = null;
            }
        }

        if (isSelfMapped)
        {
            _fallbackGlobal.TryAdd(source, translation);
            _global.TryAdd(source, translation);
            return;
        }

        // Track the most frequent translation across all scenes so common short sentences
        // ("えっ？", "うん。", "ああ……", etc.) resolve to the dominant translation when a scene
        // leaves duplicate rows empty or when no timing-tag conflict exists.
        if (!_globalCounts.TryGetValue(source, out var counts))
            _globalCounts.Add(source, counts = new Dictionary<string, int>(StringComparer.Ordinal));
        var newCount = counts.TryGetValue(translation, out var prevCount) ? prevCount + 1 : 1;
        counts[translation] = newCount;

        if (!_fallbackGlobal.TryGetValue(source, out var currentBest) ||
            string.Equals(currentBest, source, StringComparison.Ordinal) ||
            (counts.TryGetValue(currentBest, out var bestCount) && newCount > bestCount))
        {
            _fallbackGlobal[source] = translation;
        }

        if (!_global.TryGetValue(source, out var previous))
        {
            _global.Add(source, translation);
        }
        else if (previous != null)
        {
            if (string.Equals(previous, source, StringComparison.Ordinal))
            {
                _global[source] = translation;
            }
            else if (!string.Equals(previous, translation, StringComparison.Ordinal))
            {
                // Only mark a cross-scene conflict as unresolvable when UTAGE display control tags
                // (<interval>, <speed>, <sound>) are present; plain short sentences without timing
                // tags safely use the dominant translation in _fallbackGlobal.
                if (HasUtageControlTag(source) || HasUtageControlTag(previous) || HasUtageControlTag(translation))
                    _global[source] = null;
                else
                    _global[source] = _fallbackGlobal[source];
            }
        }
    }

    public bool TryGet(string source, string scene, out string translation, out bool conflict)
        => TryGet(source, scene, out translation, out conflict, out _);

    public bool TryGet(string source, string scene, out string translation, out bool conflict, out string uniqueScene)
    {
        conflict = false;
        translation = source;
        uniqueScene = _uniqueSceneBySource.TryGetValue(source, out var u) && !string.IsNullOrEmpty(u)
            ? u
            : string.Empty;
        string value;

        if (!string.IsNullOrEmpty(scene) && _scenes.TryGetValue(scene, out var table) &&
            table.TryGetValue(source, out value) && !string.IsNullOrEmpty(value))
        {
            translation = value;
            return true;
        }

        if (!_global.TryGetValue(source, out value))
            return false;

        if (value == null)
        {
            // If the caller knows the active scene, a missing entry in _scenes[scene] means
            // that scene's JSON file left the duplicate row empty ("") expecting promotion
            // from _fallbackGlobal. Only report a conflict when scene identity is unknown.
            if (!string.IsNullOrEmpty(scene) &&
                _fallbackGlobal.TryGetValue(source, out var promoted) &&
                !string.IsNullOrEmpty(promoted))
            {
                translation = promoted;
                return true;
            }

            conflict = true;
            return false;
        }

        translation = value;
        return true;
    }

    private static bool HasUtageControlTag(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf('<') >= 0 &&
        (value.Contains("interval", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("speed", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("sound", StringComparison.OrdinalIgnoreCase));
}
