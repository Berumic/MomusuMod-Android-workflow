using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MonsterMusumeTDMod.Services;

/// <summary>Exact scene lookup with a unique-only global fallback for timing-sensitive rows and promoted fallback for plain dialogue.</summary>
public sealed class StoryTranslationIndex
{
    private static readonly Regex UtageControlTagPattern = new(
        @"</?(?:interval|speed|sound)(?:=[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private readonly Dictionary<string, Dictionary<string, string>> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _global = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fallbackGlobal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _globalCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uniqueSceneBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _scenesBySource = new(StringComparer.Ordinal);

    public void Clear()
    {
        _scenes.Clear();
        _global.Clear();
        _fallbackGlobal.Clear();
        _globalCounts.Clear();
        _uniqueSceneBySource.Clear();
        _scenesBySource.Clear();
    }

    public void RecordSceneOccurrence(string scene, string source)
    {
        if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(source))
            return;

        if (!_uniqueSceneBySource.TryGetValue(source, out var existingScene))
            _uniqueSceneBySource.Add(source, scene);
        else if (existingScene != null && !string.Equals(existingScene, scene, StringComparison.OrdinalIgnoreCase))
            _uniqueSceneBySource[source] = null;

        if (!_scenesBySource.TryGetValue(source, out var set))
            _scenesBySource.Add(source, set = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        set.Add(scene);
    }

    public void Add(string scene, string source, string translation)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation))
            return;

        RecordSceneOccurrence(scene, source);

        var isSelfMapped = IsUntranslatedSelfMapping(source, translation);
        if (isSelfMapped)
            return;

        if (!string.IsNullOrEmpty(scene))
        {
            if (!_scenes.TryGetValue(scene, out var table))
                _scenes.Add(scene, table = new Dictionary<string, string>(StringComparer.Ordinal));

            if (!table.ContainsKey(source))
                table.Add(source, translation);
        }

        // Track the most frequent translation across all scenes so common short sentences
        // ("えっ？", "うん。", "ああ……", etc.) resolve to the dominant translation when a scene
        // leaves duplicate rows empty or when no timing-tag conflict exists.
        if (!_globalCounts.TryGetValue(source, out var counts))
            _globalCounts.Add(source, counts = new Dictionary<string, int>(StringComparer.Ordinal));
        var newCount = counts.TryGetValue(translation, out var prevCount) ? prevCount + 1 : 1;
        counts[translation] = newCount;

        var sourceHasControlTag = HasUtageControlTag(source);
        var translationHasControlTag = HasUtageControlTag(translation);

        if (!_fallbackGlobal.TryGetValue(source, out var currentBest))
        {
            _fallbackGlobal[source] = translation;
        }
        else
        {
            var bestHasControlTag = HasUtageControlTag(currentBest);
            var bestCount = counts.TryGetValue(currentBest, out var c) ? c : 0;
            if (!sourceHasControlTag && bestHasControlTag && !translationHasControlTag)
            {
                // For a tag-free source, always prefer a tag-free translation over a tagged one.
                _fallbackGlobal[source] = translation;
            }
            else if ((sourceHasControlTag || bestHasControlTag == translationHasControlTag) &&
                     newCount > bestCount)
            {
                _fallbackGlobal[source] = translation;
            }
        }

        if (!_global.TryGetValue(source, out var previous))
        {
            _global.Add(source, translation);
        }
        else if (!string.Equals(previous, translation, StringComparison.Ordinal))
        {
            var best = _fallbackGlobal[source];
            // Only mark a cross-scene conflict as unresolvable when the source itself has UTAGE
            // timing tags (<interval>, <speed>, <sound>), or when every translation for this source
            // contains timing tags. Plain short sentences with at least one tag-free translation
            // safely resolve to the dominant tag-free translation in _fallbackGlobal.
            if (sourceHasControlTag || HasUtageControlTag(best))
                _global[source] = null;
            else
                _global[source] = best;
        }
    }

    public bool TryGetInScene(string source, string scene, out string translation, out string uniqueScene)
    {
        translation = source;
        uniqueScene = _uniqueSceneBySource.TryGetValue(source, out var u) && !string.IsNullOrEmpty(u)
            ? u
            : string.Empty;

        if (!string.IsNullOrEmpty(scene) &&
            _scenes.TryGetValue(scene, out var table) &&
            table.TryGetValue(source, out var value) &&
            !string.IsNullOrEmpty(value))
        {
            translation = value;
            return true;
        }

        return false;
    }

    public bool TryGetFromCandidates(
        string source,
        IReadOnlyCollection<string> candidateScenes,
        out string translation)
    {
        translation = source;
        if (candidateScenes == null || candidateScenes.Count == 0)
            return false;

        string agreed = null;
        foreach (var candidate in candidateScenes)
        {
            if (!_scenes.TryGetValue(candidate, out var table) ||
                !table.TryGetValue(source, out var val) ||
                string.IsNullOrEmpty(val))
                continue;

            if (agreed == null)
                agreed = val;
            else if (!string.Equals(agreed, val, StringComparison.Ordinal))
                return false;
        }

        if (string.IsNullOrEmpty(agreed))
            return false;

        translation = agreed;
        return true;
    }

    public IReadOnlyCollection<string> GetScenesForSource(string source) =>
        !string.IsNullOrEmpty(source) && _scenesBySource.TryGetValue(source, out var scenes)
            ? scenes
            : Array.Empty<string>();

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

    private static bool IsUntranslatedSelfMapping(string source, string translation)
    {
        if (!ContainsJapanese(source))
            return false;
        if (string.Equals(source, translation, StringComparison.Ordinal))
            return true;
        if (!HasUtageControlTag(source) && !HasUtageControlTag(translation))
            return false;
        return string.Equals(
            StripUtageControlTags(source),
            StripUtageControlTags(translation),
            StringComparison.Ordinal);
    }

    private static string StripUtageControlTags(string value) =>
        !HasUtageControlTag(value)
            ? value
            : UtageControlTagPattern.Replace(value, string.Empty);

    private static bool ContainsJapanese(string value)
    {
        foreach (var character in value)
        {
            if ((character >= '\u3040' && character <= '\u30ff') ||
                (character >= '\u3400' && character <= '\u9fff') ||
                (character >= '\uf900' && character <= '\ufaff') ||
                (character >= '\uff66' && character <= '\uff9f'))
                return true;
        }
        return false;
    }

    private static bool HasUtageControlTag(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf('<') >= 0 &&
        (value.Contains("interval", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("speed", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("sound", StringComparison.OrdinalIgnoreCase));
}
