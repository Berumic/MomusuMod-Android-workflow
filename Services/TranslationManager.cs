using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonsterMusumeTDMod.Core;

namespace MonsterMusumeTDMod.Services;

public sealed class TranslationManager
{
    private const string NumberPlaceholder = "<num>";
    private const string ExPlaceholder = "<ex>";
    private readonly string _root;
    private readonly ModConfig _config;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _scenarios = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uiTexts = new(StringComparer.Ordinal);
    private readonly List<UiPathTable> _uiPathTables = new();
    private readonly Dictionary<string, List<UiPathTable>>
        _uiPathTablesByLeaf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _subSkills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private readonly List<SceneTemplate> _nameSceneTemplates = new();
    private readonly List<SceneTemplate> _uiSceneTemplates = new();
    private readonly List<SceneTemplate> _subSkillSceneTemplates = new();
    private readonly List<ExTemplate> _nameExTemplates = new();
    private readonly List<NumberTemplate> _uiNumberTemplates = new();
    private readonly List<ExTemplate> _uiExTemplates = new();
    private readonly List<NumberTemplate> _subSkillNumberTemplates = new();
    private readonly List<ExTemplate> _subSkillExTemplates = new();
    private readonly Dictionary<char, List<KeyValuePair<string, string>>> _subSkillFragmentIndex = new();
    private readonly Dictionary<char, List<KeyValuePair<string, string>>> _storyFragmentIndex = new();
    private readonly ConcurrentDictionary<string, Dictionary<char, List<KeyValuePair<string, string>>>>
        _scenarioFragmentIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _scenarioPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _r18ResourceScenarioIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<StorySourceEntry>> _storySourceIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownNameTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownStoryTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownUiTextTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownSubSkillTranslationValues = new(StringComparer.Ordinal);
    private readonly RuntimeTextCollector _runtimeTextCollector;
    private List<string> _inferredScenarioIds;
    private bool _inferenceStopped;
    private string _lastLoggedInferredScenarioId;
    private static readonly Regex R18ScenarioIdPattern = new(
        @"^(?<base>\d+)[a-z]\d+_(?:nml|r18)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex ResourceNumberPattern = new(
        @"(?:r18|nml)_(?<base>\d+)(?:_|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex UtageDisplayControlTag = new(
        @"</?(?:interval|speed|sound)(?:=[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public TranslationManager(string pluginRoot, ModConfig config)
    {
        _root = Path.Combine(pluginRoot, "MonsterMusumeTDMod", "translations");
        _config = config;
        _runtimeTextCollector = new RuntimeTextCollector(_root);
    }

    public void LoadStatic()
    {
        _names.Clear();
        _nameExTemplates.Clear();
        _nameSceneTemplates.Clear();
        _uiTexts.Clear();
        _uiPathTables.Clear();
        _uiPathTablesByLeaf.Clear();
        _uiNumberTemplates.Clear();
        _uiExTemplates.Clear();
        _uiSceneTemplates.Clear();
        _knownTranslationValues.Clear();
        _knownNameTranslationValues.Clear();
        _knownStoryTranslationValues.Clear();
        _knownUiTextTranslationValues.Clear();
        _knownSubSkillTranslationValues.Clear();
        foreach (var pair in ReadTable(Path.Combine(_root, "names", $"{_config.Language.Value}.json")))
        {
            if (TryParseSceneMarker(pair.Key, out var scenePath, out var sceneSource))
            {
                if (!string.IsNullOrEmpty(pair.Value))
                {
                    _nameSceneTemplates.Add(new SceneTemplate(scenePath, sceneSource, pair.Value));
                    AddKnownTranslationValue(pair.Value);
                    _knownNameTranslationValues.Add(pair.Value);
                }
                continue;
            }

            _names[pair.Key] = pair.Value;
            AddExTemplate(_nameExTemplates, pair.Key, pair.Value);
            AddKnownTranslationValue(pair.Value);
            if (!string.IsNullOrEmpty(pair.Value))
                _knownNameTranslationValues.Add(pair.Value);
        }
        _nameSceneTemplates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
        var uiCanvasRoot = Path.Combine(_root, "UICanvas");
        if (Directory.Exists(uiCanvasRoot))
        {
            foreach (var path in Directory.EnumerateFiles(
                uiCanvasRoot,
                $"{_config.Language.Value}.json",
                SearchOption.AllDirectories))
            {
                foreach (var pair in ReadTable(path))
                {
                    if (TryParseSceneMarker(pair.Key, out var scenePath, out var sceneSource))
                    {
                        if (!string.IsNullOrEmpty(pair.Value))
                        {
                            _uiSceneTemplates.Add(new SceneTemplate(scenePath, sceneSource, pair.Value));
                            AddKnownTranslationValue(pair.Value);
                            _knownUiTextTranslationValues.Add(pair.Value);
                        }
                        continue;
                    }

                    // Complete rows win first at lookup time; remaining rows
                    // are also indexed for longest-first UI fragments.
                    if (!string.IsNullOrEmpty(pair.Value))
                    {
                        if (_uiTexts.ContainsKey(pair.Key))
                            Core.Plugin.Log?.LogWarning($"重复的 UI 翻译 key，保留先读取的条目: {pair.Key} ({path})");
                        else
                        {
                            _uiTexts.Add(pair.Key, pair.Value);
                            AddNumberTemplate(_uiNumberTemplates, pair.Key, pair.Value);
                            AddExTemplate(_uiExTemplates, pair.Key, pair.Value);
                        }
                        AddKnownTranslationValue(pair.Value);
                        _knownUiTextTranslationValues.Add(pair.Value);
                    }
                }

                foreach (var pathTable in ReadUiPathTables(path))
                {
                    _uiPathTables.Add(pathTable);
                    // Path names are metadata/categories only. Register the
                    // child rows in the global UI index as well so a TMP
                    // setter can translate immediately, before Unity has
                    // finished attaching the final hierarchy.
                    foreach (var pair in pathTable.Exact)
                    {
                        if (string.IsNullOrEmpty(pair.Value))
                            continue;
                        if (!_uiTexts.ContainsKey(pair.Key))
                        {
                            _uiTexts.Add(pair.Key, pair.Value);
                            AddNumberTemplate(_uiNumberTemplates, pair.Key, pair.Value);
                            AddExTemplate(_uiExTemplates, pair.Key, pair.Value);
                        }
                    }
                    foreach (var value in pathTable.Exact.Values)
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            AddKnownTranslationValue(value);
                            _knownUiTextTranslationValues.Add(value);
                        }
                    }
                }
            }
        }
        _uiSceneTemplates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
        _uiPathTables.Sort((left, right) => right.Path.Length.CompareTo(left.Path.Length));
        foreach (var pathTable in _uiPathTables)
        {
            var leaf = GetUiPathLeaf(pathTable.Path);
            if (!_uiPathTablesByLeaf.TryGetValue(leaf, out var candidates))
            {
                candidates = new List<UiPathTable>();
                _uiPathTablesByLeaf.Add(leaf, candidates);
            }
            candidates.Add(pathTable);
        }
        _subSkills.Clear();
        _subSkillNumberTemplates.Clear();
        _subSkillExTemplates.Clear();
        _subSkillSceneTemplates.Clear();
        _subSkillFragmentIndex.Clear();
        foreach (var pair in ReadTable(Path.Combine(_root, "subskills", $"{_config.Language.Value}.json")))
        {
            // A blank entry is a worklist placeholder, never a usable
            // translation.
            if (!string.IsNullOrEmpty(pair.Value))
            {
                if (TryParseSceneMarker(pair.Key, out var scenePath, out var sceneSource))
                {
                    _subSkillSceneTemplates.Add(new SceneTemplate(scenePath, sceneSource, pair.Value));
                    AddKnownTranslationValue(pair.Value);
                    _knownSubSkillTranslationValues.Add(pair.Value);
                    continue;
                }

                var normalizedKey = NormalizeSubSkillKey(pair.Key);
                _subSkills[normalizedKey] = pair.Value;
                AddNumberTemplate(_subSkillNumberTemplates, normalizedKey, pair.Value);
                AddExTemplate(_subSkillExTemplates, normalizedKey, pair.Value);
                AddFragmentRule(_subSkillFragmentIndex, normalizedKey, pair.Value);
                AddKnownTranslationValue(pair.Value);
                _knownSubSkillTranslationValues.Add(pair.Value);
            }
        }
        _subSkillSceneTemplates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
        SortFragmentIndex(_subSkillFragmentIndex);
        _scenarios.Clear();
        _scenarioFragmentIndexes.Clear();
        _scenarioPaths.Clear();
        _r18ResourceScenarioIds.Clear();
        _storySourceIndex.Clear();
        _fallback.Clear();
        _storyFragmentIndex.Clear();
        ResetInferredStoryScenario();
        var scenarioRoot = Path.Combine(_root, "scenarios");
        if (Directory.Exists(scenarioRoot))
        {
            var scenarioIdsWithTranslations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var promotedStoryTranslations = 0;
            foreach (var path in Directory.EnumerateFiles(scenarioRoot, $"{_config.Language.Value}.json", SearchOption.AllDirectories))
            {
                var table = ReadTable(path);
                // Translation sets can be arranged below category folders
                // (for example qinshi/<character>/<scenario-id>). Keep an
                // ID index that prefers a populated categorized file over an
                // empty generated file with the same scenario ID.
                var scenarioId = Path.GetFileName(Path.GetDirectoryName(path));
                if (!string.IsNullOrEmpty(scenarioId))
                {
                    var hasTranslations = table.Values.Any(value => !string.IsNullOrEmpty(value));
                    if (!_scenarioPaths.ContainsKey(scenarioId) ||
                        (hasTranslations && !scenarioIdsWithTranslations.Contains(scenarioId)))
                        _scenarioPaths[scenarioId] = path;
                    if (hasTranslations)
                        scenarioIdsWithTranslations.Add(scenarioId);
                    IndexR18ResourceScenario(scenarioId);
                }

                foreach (var pair in table)
                {
                    // Values are registered only to identify nested setter
                    // writebacks. Preserve the first usable global mapping,
                    // but never let an empty duplicate shadow a later
                    // translated row from a categorized directory.
                    AddKnownTranslationValue(pair.Value);
                    AddKnownStoryTranslationValue(pair.Value);
                    if (!string.IsNullOrEmpty(scenarioId))
                        IndexStorySource(scenarioId, pair.Key, pair.Value);
                    if (_fallback.TryAdd(pair.Key, pair.Value))
                    {
                        AddStoryFragment(pair.Key, pair.Value);
                    }
                    else if (string.IsNullOrEmpty(_fallback[pair.Key]) &&
                             !string.IsNullOrEmpty(pair.Value))
                    {
                        _fallback[pair.Key] = pair.Value;
                        AddStoryFragment(pair.Key, pair.Value);
                        promotedStoryTranslations++;
                    }
                }
            }

            foreach (var candidates in _storyFragmentIndex.Values)
                candidates.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));

            Core.Plugin.Log?.LogInfo(
                $"Loaded {_fallback.Count} unique story row(s); " +
                $"promoted {promotedStoryTranslations} translated duplicate(s) over empty rows");
        }
    }

    public bool TryTranslateName(string source, out string translated)
        => TryTranslateName(source, null, out translated);

    public bool TryTranslateName(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (TryTranslateSceneTemplate(source, scenePath, _nameSceneTemplates, out translated))
            return true;

        if (_names.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated))
            return true;

        if (TryTranslateJoinedNames(source, out translated))
            return true;

        if (_knownNameTranslationValues.Contains(source))
        {
            translated = source;
            return true;
        }

        if (!ContainsJapanese(source))
            return false;

        return TryTranslateExTemplate(source, _nameExTemplates, TryTranslateCapturedName, out translated);
    }

    private bool TryTranslateJoinedNames(string source, out string translated)
    {
        translated = source;
        if (source.IndexOf('&') < 0 && source.IndexOf('＆') < 0)
            return false;

        var result = new StringBuilder(source.Length);
        var segmentStart = 0;
        var translatedSegments = 0;
        for (var index = 0; index <= source.Length; index++)
        {
            var atEnd = index == source.Length;
            if (!atEnd && source[index] != '&' && source[index] != '＆')
                continue;

            var segment = source.Substring(segmentStart, index - segmentStart);
            if (segment.Length == 0 ||
                !_names.TryGetValue(segment, out var segmentTranslation) ||
                string.IsNullOrEmpty(segmentTranslation))
                return false;

            result.Append(segmentTranslation);
            translatedSegments++;
            if (!atEnd)
            {
                result.Append(source[index]);
                segmentStart = index + 1;
            }
        }

        if (translatedSegments < 2)
            return false;
        translated = result.ToString();
        return true;
    }

    /// <summary>Translates only complete rows from translations/UICanvas.</summary>
    public bool TryTranslateUiText(string source, out string translated)
        => TryTranslateUiText(source, null, out translated);

    public bool TryTranslateUiText(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (TryTranslateSceneTemplate(source, scenePath, _uiSceneTemplates, out translated))
            return true;

        if (TryTranslateUiPath(source, scenePath, out translated))
            return true;

        if (_uiTexts.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated))
            return true;

        if (_knownUiTextTranslationValues.Contains(source))
        {
            translated = source;
            return true;
        }

        if (!ContainsJapanese(source))
            return false;

        if (TryTranslateNumberTemplate(source, _uiNumberTemplates, out translated))
            return true;

        if (TryTranslateExTemplate(source, _uiExTemplates, TryTranslateCapturedUiText, out translated))
            return true;

        // TMP descriptions may be populated with CRLF while JSON source
        // tables use LF. Normalize only for a second complete-row lookup.
        var normalizedSource = NormalizeSubSkillKey(source);
        if (!string.Equals(normalizedSource, source, StringComparison.Ordinal) &&
            TryTranslateSceneTemplate(normalizedSource, scenePath, _uiSceneTemplates, out translated))
            return true;

        if (TryTranslateUiPath(normalizedSource, scenePath, out translated))
            return true;

        if (_uiTexts.TryGetValue(normalizedSource, out translated) && !string.IsNullOrEmpty(translated))
            return true;

        if (_knownUiTextTranslationValues.Contains(normalizedSource))
        {
            translated = normalizedSource;
            return true;
        }

        if (!ContainsJapanese(normalizedSource))
        {
            translated = source;
            return false;
        }

        if (TryTranslateNumberTemplate(normalizedSource, _uiNumberTemplates, out translated))
            return true;

        if (TryTranslateExTemplate(normalizedSource, _uiExTemplates, TryTranslateCapturedUiText, out translated))
            return true;

        translated = source;
        return false;
    }

    private bool TryTranslateUiPath(string source, string scenePath, out string translated)
    {
        translated = source;
        if (string.IsNullOrEmpty(scenePath))
            return false;

        var normalizedPath = NormalizeUiPath(scenePath);
        if (!_uiPathTablesByLeaf.TryGetValue(GetUiPathLeaf(normalizedPath), out var candidates))
            return false;
        foreach (var pathTable in candidates)
        {
            if (!PathContainsSegmentSequence(normalizedPath, pathTable.Path))
                continue;
            if (pathTable.Exact.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value))
            {
                translated = value;
                return true;
            }
            if (TryTranslateNumberTemplate(source, pathTable.NumberTemplates, out translated))
                return true;
            if (TryTranslateExTemplate(source, pathTable.ExTemplates, TryTranslateCapturedUiText, out translated))
                return true;
        }
        return false;
    }

    public string ResolveUiPathCategory(string scenePath)
    {
        var normalizedPath = NormalizeUiPath(scenePath);
        if (_uiPathTablesByLeaf.TryGetValue(GetUiPathLeaf(normalizedPath), out var candidates))
            foreach (var pathTable in candidates)
                if (PathContainsSegmentSequence(normalizedPath, pathTable.Path))
                    return pathTable.Path;
        return RuntimeTextCollector.CreateStableUiCategory(normalizedPath);
    }

    private static string GetUiPathLeaf(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static bool PathContainsSegmentSequence(string fullPath, string categoryPath) =>
        string.Equals(fullPath, categoryPath, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(categoryPath + "/", StringComparison.OrdinalIgnoreCase) ||
        fullPath.EndsWith("/" + categoryPath, StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains("/" + categoryPath + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join('/', parts);
    }

    public bool TryTranslateSubSkill(string source, out string translated)
        => TryTranslateSubSkill(source, null, out translated);

    public bool TryTranslateSubSkill(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        var lookupKey = NormalizeSubSkillKey(source);
        if (TryTranslateSceneTemplate(lookupKey, scenePath, _subSkillSceneTemplates, out translated))
            return true;

        if (_subSkills.TryGetValue(lookupKey, out var directTranslation) && !string.IsNullOrEmpty(directTranslation))
        {
            translated = directTranslation;
            return true;
        }

        if (_knownSubSkillTranslationValues.Contains(lookupKey))
        {
            translated = lookupKey;
            return true;
        }

        // Exact ASCII entries such as "HP" are handled above. More expensive
        // wildcard and embedded matching is useful only for Japanese source
        // text, so unrelated UI assignments can return without scanning rules.
        if (!ContainsJapanese(lookupKey))
            return false;

        if (TryTranslateNumberTemplate(lookupKey, _subSkillNumberTemplates, out translated))
            return true;

        if (TryTranslateExTemplate(lookupKey, _subSkillExTemplates, TryTranslateCapturedSubSkill, out translated))
            return true;

        return TryTranslateIndexedFragments(lookupKey, _subSkillFragmentIndex, out translated);
    }

    public bool IsKnownSubSkillTranslationValue(string value) =>
        !string.IsNullOrEmpty(value) &&
        (_knownSubSkillTranslationValues.Contains(value) ||
         MatchesSceneTemplateTranslation(value, _subSkillSceneTemplates) ||
         MatchesNumberTemplateTranslation(value, _subSkillNumberTemplates) ||
         MatchesExTemplateTranslation(value, _subSkillExTemplates));

    public bool IsKnownUiTextTranslationValue(string value) =>
        !string.IsNullOrEmpty(value) &&
        (_knownUiTextTranslationValues.Contains(value) ||
         MatchesSceneTemplateTranslation(value, _uiSceneTemplates) ||
         MatchesNumberTemplateTranslation(value, _uiNumberTemplates) ||
         MatchesExTemplateTranslation(value, _uiExTemplates));

    /// <summary>Story and log text use one global source-to-translation table.</summary>
    public bool TryTranslateStoryExact(
        string source,
        string scenarioId,
        string resourceId,
        out string translated
    )
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (_fallback.TryGetValue(source, out var fallbackTranslation) &&
            !string.IsNullOrEmpty(fallbackTranslation))
        {
            translated = fallbackTranslation;
            return true;
        }

        return TryTranslateIndexedStoryFragments(source, out translated);
    }

    /// <summary>
    /// Replaces a complete raw UTAGE row before its control tags are parsed.
    /// Story wildcards are intentionally unsupported here.
    /// </summary>
    public bool TryTranslateStoryBeforeParse(string source, out string translated)
    {
        translated = source;
        return _config.Enabled.Value &&
               _config.TranslateScenarios.Value &&
               !string.IsNullOrEmpty(source) &&
               _fallback.TryGetValue(source, out translated) &&
               !string.IsNullOrEmpty(translated);
    }

    public bool IsKnownStoryTranslationValue(string value) =>
        !string.IsNullOrEmpty(value) &&
        (_knownStoryTranslationValues.Contains(value) ||
         _knownStoryTranslationValues.Contains(NormalizeStoryDisplayValue(value)));

    private bool TryTranslateFromCandidates(
        string source,
        IEnumerable<string> candidateScenarioIds,
        out string translated
    )
    {
        translated = source;
        string candidateTranslation = null;
        foreach (var candidateScenarioId in candidateScenarioIds)
        {
            if (!TryTranslateFromScenario(source, candidateScenarioId, out var localTranslation))
                continue;

            if (candidateTranslation == null)
            {
                candidateTranslation = localTranslation;
                continue;
            }

            if (!string.Equals(candidateTranslation, localTranslation, StringComparison.Ordinal))
                return false;
        }

        if (candidateTranslation == null)
            return false;

        translated = candidateTranslation;
        return true;
    }

    /// <summary>
    /// Some normal Utage scenes expose no script or resource ID. In that case,
    /// narrow candidate tables using consecutive source rows. A row is shown
    /// only when all remaining candidates agree on its translation.
    /// </summary>
    private bool TryTranslateByInference(string source, out string translated)
    {
        translated = source;
        if (_inferenceStopped || !_storySourceIndex.TryGetValue(source, out var sourceEntries))
        {
            _inferenceStopped = true;
            _inferredScenarioIds = null;
            return false;
        }

        var sourceScenarioIds = new HashSet<string>(
            sourceEntries.Select(entry => entry.ScenarioId),
            StringComparer.OrdinalIgnoreCase
        );
        var candidates = _inferredScenarioIds == null
            ? sourceScenarioIds
            : new HashSet<string>(_inferredScenarioIds, StringComparer.OrdinalIgnoreCase);
        candidates.IntersectWith(sourceScenarioIds);
        if (candidates.Count == 0)
        {
            // Ordinary scenes can reuse MessageText without first assigning
            // an empty value. Once a prior inference was fully resolved, a
            // source row belonging only to other tables marks a new scene.
            // Restart from that row; an entirely unknown row still stops the
            // inference session as requested.
            if (_inferredScenarioIds?.Count == 1)
            {
                candidates = sourceScenarioIds;
                _lastLoggedInferredScenarioId = null;
                Core.Plugin.Log?.LogInfo("Inferred story context reset for a new scene");
            }
            else
            {
                _inferenceStopped = true;
                _inferredScenarioIds = null;
                return false;
            }
        }

        _inferredScenarioIds = candidates.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sourceEntries)
        {
            if (candidates.Contains(entry.ScenarioId) && !string.IsNullOrEmpty(entry.Translation))
                values.Add(entry.Translation);
        }

        if (_inferredScenarioIds.Count == 1 &&
            !string.Equals(_lastLoggedInferredScenarioId, _inferredScenarioIds[0], StringComparison.OrdinalIgnoreCase))
        {
            _lastLoggedInferredScenarioId = _inferredScenarioIds[0];
            Core.Plugin.Log?.LogInfo($"Inferred story context: {_inferredScenarioIds[0]}");
        }

        if (values.Count != 1)
            return false;

        translated = values.First();
        return true;
    }

    public void ResetInferredStoryScenario()
    {
        _inferredScenarioIds = null;
        _inferenceStopped = false;
        _lastLoggedInferredScenarioId = null;
    }

    private bool TryTranslateFromScenario(string source, string scenarioId, out string translated)
    {
        translated = source;
        var table = _scenarios.GetOrAdd(scenarioId, LoadScenario);
        if (table.TryGetValue(source, out var scenarioTranslation) && !string.IsNullOrEmpty(scenarioTranslation))
        {
            translated = scenarioTranslation;
            return true;
        }

        if (TryTranslateNumberTemplate(source, table, out translated))
            return true;

        var fragmentIndex = _scenarioFragmentIndexes.GetOrAdd(
            scenarioId,
            _ => BuildFragmentIndex(table));
        return TryTranslateIndexedFragments(source, fragmentIndex, out translated);
    }

    private bool HasScenarioTable(string scenarioId) =>
        _scenarioPaths.ContainsKey(scenarioId) ||
        File.Exists(Path.Combine(_root, "scenarios", scenarioId, $"{_config.Language.Value}.json"));

    private void IndexR18ResourceScenario(string scenarioId)
    {
        var match = R18ScenarioIdPattern.Match(scenarioId);
        if (!match.Success)
            return;

        var resourceNumber = match.Groups["base"].Value;
        if (!_r18ResourceScenarioIds.TryGetValue(resourceNumber, out var scenarios))
        {
            scenarios = new List<string>();
            _r18ResourceScenarioIds.Add(resourceNumber, scenarios);
        }

        scenarios.Add(scenarioId);
    }

    private void IndexStorySource(string scenarioId, string source, string translation)
    {
        if (string.IsNullOrEmpty(source))
            return;

        if (!_storySourceIndex.TryGetValue(source, out var entries))
        {
            entries = new List<StorySourceEntry>();
            _storySourceIndex.Add(source, entries);
        }

        entries.Add(new StorySourceEntry(scenarioId, translation));
    }

    private static string ExtractSceneNumber(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId))
            return string.Empty;

        var match = ResourceNumberPattern.Match(resourceId);
        return match.Success ? match.Groups["base"].Value : string.Empty;
    }

    private readonly struct StorySourceEntry
    {
        public StorySourceEntry(string scenarioId, string translation)
        {
            ScenarioId = scenarioId;
            Translation = translation;
        }

        public string ScenarioId { get; }
        public string Translation { get; }
    }

    private sealed class SceneTemplate
    {
        private readonly string _scenePath;
        private readonly string _source;
        private readonly string _translation;
        private readonly NumberTemplate _numberTemplate;
        private readonly ExTemplate _exTemplate;

        public SceneTemplate(string scenePath, string source, string translation)
        {
            _scenePath = scenePath;
            _source = source;
            _translation = translation;
            SourceLength = source.Length;
            if (source.Contains(NumberPlaceholder, StringComparison.Ordinal))
                _numberTemplate = new NumberTemplate(source, translation);
            else if (source.Contains(ExPlaceholder, StringComparison.Ordinal))
                _exTemplate = new ExTemplate(source, translation);
        }

        public int SourceLength { get; }

        public bool TryTranslate(string source, string currentScenePath, out string translated)
        {
            translated = source;
            if (string.IsNullOrEmpty(currentScenePath) ||
                currentScenePath.IndexOf(_scenePath, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (string.Equals(source, _source, StringComparison.Ordinal))
            {
                translated = _translation;
                return true;
            }

            if (_numberTemplate != null && _numberTemplate.TryTranslate(source, out translated))
                return true;
            if (_exTemplate != null && _exTemplate.TryTranslate(source, out translated))
                return true;

            return false;
        }

        public bool MatchesTranslation(string value) =>
            string.Equals(value, _translation, StringComparison.Ordinal) ||
            (_numberTemplate != null && _numberTemplate.MatchesTranslation(value)) ||
            (_exTemplate != null && _exTemplate.MatchesTranslation(value));
    }

    /// <summary>
    /// An anchored template whose &lt;ex&gt; segments preserve arbitrary runtime
    /// text. Captures are inserted into target placeholders in source order.
    /// </summary>
    private sealed class ExTemplate
    {
        private readonly Regex _sourcePattern;
        private readonly Regex _translationPattern;
        private readonly string[] _translationPieces;
        private readonly string _requiredPrefix;
        private readonly string _requiredSuffix;
        private readonly string _translationPrefix;
        private readonly string _translationSuffix;

        public ExTemplate(string source, string translation)
        {
            Source = source;
            SourceLength = source.Length;
            var sourcePieces = source.Split(ExPlaceholder, StringSplitOptions.None);
            _translationPieces = translation.Split(ExPlaceholder, StringSplitOptions.None);
            WildcardCount = sourcePieces.Length - 1;
            LiteralLength = source.Length - WildcardCount * ExPlaceholder.Length;
            _requiredPrefix = sourcePieces[0];
            _requiredSuffix = sourcePieces[^1];
            _translationPrefix = _translationPieces[0];
            _translationSuffix = _translationPieces[^1];
            _sourcePattern = BuildPattern(sourcePieces);
            _translationPattern = BuildPattern(_translationPieces);
        }

        public string Source { get; }
        public int SourceLength { get; }
        public int LiteralLength { get; }
        public int WildcardCount { get; }

        public bool TryTranslate(string source, out string translated)
            => TryTranslate(source, null, out translated);

        public bool TryTranslate(
            string source,
            TryTranslateCapture translateCapture,
            out string translated)
        {
            if ((!string.IsNullOrEmpty(_requiredPrefix) &&
                 !source.StartsWith(_requiredPrefix, StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(_requiredSuffix) &&
                 !source.EndsWith(_requiredSuffix, StringComparison.Ordinal)))
            {
                translated = source;
                return false;
            }

            var match = _sourcePattern.Match(source);
            if (!match.Success)
            {
                translated = source;
                return false;
            }

            var captures = match.Groups["excluded"].Captures;
            var result = new StringBuilder(source.Length + 16);
            for (var index = 0; index < _translationPieces.Length; index++)
            {
                result.Append(_translationPieces[index]);
                if (index < captures.Count)
                {
                    var captured = captures[index].Value;
                    if (translateCapture != null &&
                        translateCapture(captured, out var capturedTranslation))
                        result.Append(capturedTranslation);
                    else
                        result.Append(captured);
                }
            }

            translated = result.ToString();
            return true;
        }

        public bool MatchesTranslation(string value) =>
            (string.IsNullOrEmpty(_translationPrefix) ||
             value.StartsWith(_translationPrefix, StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(_translationSuffix) ||
             value.EndsWith(_translationSuffix, StringComparison.Ordinal)) &&
            _translationPattern.IsMatch(value);

        private static Regex BuildPattern(IReadOnlyList<string> pieces)
        {
            var pattern = new StringBuilder(@"\A");
            for (var index = 0; index < pieces.Count; index++)
            {
                pattern.Append(Regex.Escape(pieces[index]));
                if (index < pieces.Count - 1)
                    pattern.Append("(?<excluded>.*?)");
            }
            pattern.Append(@"\z");
            return new Regex(
                pattern.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);
        }
    }

    /// <summary>
    /// A complete-row template such as "回復&lt;num&gt;HP". Values captured from
    /// each source placeholder are inserted into the matching target
    /// placeholder in order, without converting their original formatting.
    /// </summary>
    private sealed class NumberTemplate
    {
        private const string NumberPattern = @"(?<number>[+\-−＋]?\d+(?:[.,．]\d+)?)";
        private readonly Regex _sourcePattern;
        private readonly Regex _translationPattern;
        private readonly string _translation;

        public NumberTemplate(string source, string translation)
        {
            SourceLength = source.Length;
            _sourcePattern = BuildPattern(source);
            _translationPattern = BuildPattern(translation);
            _translation = translation;
        }

        public int SourceLength { get; }

        public bool TryTranslate(string source, out string translated)
        {
            var match = _sourcePattern.Match(source);
            if (!match.Success)
            {
                translated = source;
                return false;
            }

            var value = _translation;
            foreach (Capture capture in match.Groups["number"].Captures)
            {
                var placeholderIndex = value.IndexOf(NumberPlaceholder, StringComparison.Ordinal);
                if (placeholderIndex < 0)
                    break;
                value = value[..placeholderIndex] + capture.Value +
                        value[(placeholderIndex + NumberPlaceholder.Length)..];
            }

            translated = value;
            return true;
        }

        public bool MatchesTranslation(string value) => _translationPattern.IsMatch(value);

        private static Regex BuildPattern(string template)
        {
            var pieces = template.Split(NumberPlaceholder, StringSplitOptions.None);
            var pattern = new StringBuilder("^");
            for (var index = 0; index < pieces.Length; index++)
            {
                pattern.Append(Regex.Escape(pieces[index]));
                if (index < pieces.Length - 1)
                    pattern.Append(NumberPattern);
            }
            pattern.Append('$');
            return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
    }

    public void ObserveCharacterOrSkillText(object instance, string source)
    {
        if (_config.Enabled.Value && _runtimeTextCollector.IsSubSkillScanActive)
            _runtimeTextCollector.Observe(instance, source);
    }

    public void CaptureActiveUiSnapshot()
    {
        if (_config.EnableUiSnapshotHotkey.Value)
            _runtimeTextCollector.CaptureActiveUiSnapshot(
                (source, path) => HasStrictRuntimeTranslation(source, path),
                ResolveUiPathCategory);
    }

    public void BeginSubSkillScan() => _runtimeTextCollector.BeginSubSkillScan();

    public int EndSubSkillScan() => _runtimeTextCollector.EndSubSkillScan();

    public void CaptureVisibleSubSkillScanText() =>
        _runtimeTextCollector.CaptureVisibleSubSkillScanText();

    public bool IsKnownTranslationValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var normalized = NormalizeSubSkillKey(value);
        if (_knownTranslationValues.Contains(value) ||
            _knownTranslationValues.Contains(normalized))
            return true;
        if (!ContainsJapanese(normalized))
            return false;

        return MatchesNumberTemplateTranslation(normalized, _uiNumberTemplates) ||
               MatchesNumberTemplateTranslation(normalized, _subSkillNumberTemplates) ||
               MatchesExTemplateTranslation(normalized, _nameExTemplates) ||
               MatchesExTemplateTranslation(normalized, _uiExTemplates) ||
               MatchesExTemplateTranslation(normalized, _subSkillExTemplates);
    }

    private bool HasRuntimeTranslation(string source)
    {
        if (string.IsNullOrEmpty(source) || IsKnownTranslationValue(source))
            return !string.IsNullOrEmpty(source);

        if ((_names.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value)) ||
            (_uiTexts.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)) ||
            (_subSkills.TryGetValue(NormalizeSubSkillKey(source), out value) && !string.IsNullOrEmpty(value)) ||
            (_fallback.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)))
            return true;

        if (TryTranslateNumberTemplate(source, _uiNumberTemplates, out _) ||
            TryTranslateNumberTemplate(source, _subSkillNumberTemplates, out _) ||
            TryTranslateExTemplate(source, _nameExTemplates, out _) ||
            TryTranslateExTemplate(source, _uiExTemplates, out _) ||
            TryTranslateExTemplate(source, _subSkillExTemplates, out _))
            return true;

        var normalized = NormalizeSubSkillKey(source);
        if (!string.Equals(normalized, source, StringComparison.Ordinal) &&
            ((_uiTexts.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
             (_subSkills.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
             TryTranslateNumberTemplate(normalized, _uiNumberTemplates, out _) ||
             TryTranslateNumberTemplate(normalized, _subSkillNumberTemplates, out _) ||
             TryTranslateExTemplate(normalized, _uiExTemplates, out _) ||
             TryTranslateExTemplate(normalized, _subSkillExTemplates, out _)))
            return true;

        return TryTranslateIndexedFragments(normalized, _subSkillFragmentIndex, out _) ||
               TryTranslateIndexedStoryFragments(source, out _);
    }

    public bool HasRuntimeTranslation(string source, string scenePath) =>
        HasRuntimeTranslation(source) ||
        TryTranslateUiText(source, scenePath, out _);

    /// <summary>
    /// F8 missing-text collection must only accept complete-row matches.
    /// Runtime fragment translation remains available elsewhere, but a short
    /// story/sub-skill fragment must not suppress collection of an otherwise
    /// untranslated UI sentence.
    /// </summary>
    private bool HasStrictRuntimeTranslation(string source, string scenePath)
    {
        if (string.IsNullOrEmpty(source) || IsKnownTranslationValue(source))
            return !string.IsNullOrEmpty(source);

        var normalized = NormalizeSubSkillKey(source);
        if ((_names.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value)) ||
            (_uiTexts.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)) ||
            (_subSkills.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
            (_fallback.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)))
            return true;

        if (TryTranslateUiPath(source, scenePath, out _) ||
            TryTranslateSceneTemplate(source, scenePath, _uiSceneTemplates, out _) ||
            TryTranslateNumberTemplate(source, _uiNumberTemplates, out _) ||
            TryTranslateNumberTemplate(normalized, _subSkillNumberTemplates, out _) ||
            TryTranslateExTemplate(source, _nameExTemplates, out _) ||
            TryTranslateExTemplate(source, _uiExTemplates, out _) ||
            TryTranslateExTemplate(normalized, _subSkillExTemplates, out _))
            return true;

        if (string.Equals(normalized, source, StringComparison.Ordinal))
            return false;

        return (_uiTexts.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
               TryTranslateUiPath(normalized, scenePath, out _) ||
               TryTranslateSceneTemplate(normalized, scenePath, _uiSceneTemplates, out _) ||
               TryTranslateNumberTemplate(normalized, _uiNumberTemplates, out _) ||
               TryTranslateExTemplate(normalized, _uiExTemplates, out _);
    }

    private static void AddNumberTemplate(List<NumberTemplate> templates, string source, string translated)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated) ||
            !source.Contains(NumberPlaceholder, StringComparison.Ordinal))
            return;

        templates.Add(new NumberTemplate(source, translated));
        templates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
    }

    private static bool TryTranslateSceneTemplate(
        string source,
        string scenePath,
        IEnumerable<SceneTemplate> templates,
        out string translated)
    {
        translated = source;
        if (string.IsNullOrEmpty(scenePath))
            return false;

        foreach (var template in templates)
        {
            if (template.TryTranslate(source, scenePath, out translated))
                return true;
        }

        return false;
    }

    private static bool MatchesSceneTemplateTranslation(
        string value,
        IEnumerable<SceneTemplate> templates)
    {
        foreach (var template in templates)
        {
            if (template.MatchesTranslation(value))
                return true;
        }

        return false;
    }

    private static bool TryParseSceneMarker(
        string key,
        out string scenePath,
        out string source)
    {
        scenePath = null;
        source = key;
        if (string.IsNullOrEmpty(key) || !key.StartsWith("<sc=", StringComparison.Ordinal))
            return false;

        if (key.Length < 7)
            return false;

        var quote = key[4];
        if (quote != '\'' && quote != '"')
            return false;

        var closingQuote = key.IndexOf(quote, 5);
        if (closingQuote <= 5 || closingQuote + 1 >= key.Length || key[closingQuote + 1] != '>')
            return false;

        scenePath = key[5..closingQuote];
        source = key[(closingQuote + 2)..];
        return !string.IsNullOrEmpty(source);
    }

    private static void AddExTemplate(List<ExTemplate> templates, string source, string translated)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated) ||
            !source.Contains(ExPlaceholder, StringComparison.Ordinal))
            return;

        var sourceCount = CountPlaceholder(source, ExPlaceholder);
        var translationCount = CountPlaceholder(translated, ExPlaceholder);
        if (sourceCount != translationCount)
        {
            Core.Plugin.Log?.LogWarning(
                $"忽略 <ex> 数量不一致的翻译规则: {source} -> {translated}");
            return;
        }

        templates.Add(new ExTemplate(source, translated));
        templates.Sort((left, right) =>
        {
            var comparison = right.LiteralLength.CompareTo(left.LiteralLength);
            if (comparison != 0)
                return comparison;
            comparison = left.WildcardCount.CompareTo(right.WildcardCount);
            if (comparison != 0)
                return comparison;
            comparison = right.SourceLength.CompareTo(left.SourceLength);
            return comparison != 0
                ? comparison
                : string.Compare(left.Source, right.Source, StringComparison.Ordinal);
        });
    }

    private static int CountPlaceholder(string value, string placeholder)
    {
        var count = 0;
        for (var index = 0;;)
        {
            index = value.IndexOf(placeholder, index, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            index += placeholder.Length;
        }
    }

    private static bool TryTranslateNumberTemplate(
        string source,
        IEnumerable<NumberTemplate> templates,
        out string translated)
    {
        foreach (var template in templates)
            if (template.TryTranslate(source, out translated))
                return true;

        translated = source;
        return false;
    }

    private static bool TryTranslateExTemplate(
        string source,
        IEnumerable<ExTemplate> templates,
        out string translated)
    {
        foreach (var template in templates)
            if (template.TryTranslate(source, out translated))
                return true;

        translated = source;
        return false;
    }

    private static bool TryTranslateExTemplate(
        string source,
        IEnumerable<ExTemplate> templates,
        TryTranslateCapture translateCapture,
        out string translated)
    {
        foreach (var template in templates)
            if (template.TryTranslate(source, translateCapture, out translated))
                return true;

        translated = source;
        return false;
    }

    private delegate bool TryTranslateCapture(string source, out string translated);

    private bool TryTranslateCapturedName(string source, out string translated) =>
        _names.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated);

    private bool TryTranslateCapturedUiText(string source, out string translated) =>
        _uiTexts.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated);

    private bool TryTranslateCapturedSubSkill(string source, out string translated) =>
        _subSkills.TryGetValue(NormalizeSubSkillKey(source), out translated) &&
        !string.IsNullOrEmpty(translated);

    private static bool TryTranslateNumberTemplate(
        string source,
        Dictionary<string, string> table,
        out string translated)
    {
        foreach (var pair in table
            .Where(pair => pair.Key.Contains(NumberPlaceholder, StringComparison.Ordinal) &&
                           !string.IsNullOrEmpty(pair.Value))
            .OrderByDescending(pair => pair.Key.Length))
        {
            if (new NumberTemplate(pair.Key, pair.Value).TryTranslate(source, out translated))
                return true;
        }

        translated = source;
        return false;
    }

    private static bool MatchesNumberTemplateTranslation(string value, IEnumerable<NumberTemplate> templates)
    {
        foreach (var template in templates)
            if (template.MatchesTranslation(value))
                return true;
        return false;
    }

    private static bool MatchesExTemplateTranslation(string value, IEnumerable<ExTemplate> templates)
    {
        foreach (var template in templates)
            if (template.MatchesTranslation(value))
                return true;
        return false;
    }

    private static Dictionary<char, List<KeyValuePair<string, string>>> BuildFragmentIndex(
        Dictionary<string, string> table)
    {
        var index = new Dictionary<char, List<KeyValuePair<string, string>>>();
        foreach (var pair in table)
            AddFragmentRule(index, pair.Key, pair.Value);
        SortFragmentIndex(index);
        return index;
    }

    private static void AddFragmentRule(
        Dictionary<char, List<KeyValuePair<string, string>>> index,
        string source,
        string translated)
    {
        if (source.Length < 4 || string.IsNullOrEmpty(translated) ||
            source.Contains(NumberPlaceholder, StringComparison.Ordinal) ||
            source.Contains(ExPlaceholder, StringComparison.Ordinal))
            return;

        if (!index.TryGetValue(source[0], out var candidates))
        {
            candidates = new List<KeyValuePair<string, string>>();
            index.Add(source[0], candidates);
        }

        candidates.Add(new KeyValuePair<string, string>(source, translated));
    }

    private static void SortFragmentIndex(
        Dictionary<char, List<KeyValuePair<string, string>>> index)
    {
        foreach (var candidates in index.Values)
            candidates.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
    }

    private static bool TryTranslateIndexedFragments(
        string source,
        Dictionary<char, List<KeyValuePair<string, string>>> index,
        out string translated)
    {
        translated = source;
        var changed = false;
        for (var characterIndex = 0; characterIndex < translated.Length; characterIndex++)
        {
            if (!index.TryGetValue(translated[characterIndex], out var candidates))
                continue;

            foreach (var candidate in candidates)
            {
                if (!translated.AsSpan(characterIndex).StartsWith(
                        candidate.Key.AsSpan(),
                        StringComparison.Ordinal))
                    continue;

                translated = translated.Remove(characterIndex, candidate.Key.Length)
                    .Insert(characterIndex, candidate.Value);
                characterIndex += candidate.Value.Length - 1;
                changed = true;
                break;
            }
        }

        return changed;
    }

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

    private void AddStoryFragment(string source, string translated)
    {
        if (source.Length < 4 || string.IsNullOrEmpty(translated) ||
            source.Contains(NumberPlaceholder, StringComparison.Ordinal) ||
            source.Contains(ExPlaceholder, StringComparison.Ordinal))
            return;

        if (!_storyFragmentIndex.TryGetValue(source[0], out var candidates))
        {
            candidates = new List<KeyValuePair<string, string>>();
            _storyFragmentIndex.Add(source[0], candidates);
        }

        candidates.Add(new KeyValuePair<string, string>(source, translated));
    }

    private bool TryTranslateIndexedStoryFragments(string source, out string translated)
    {
        translated = source;
        var changed = false;

        for (var index = 0; index < translated.Length; index++)
        {
            if (!_storyFragmentIndex.TryGetValue(translated[index], out var candidates))
                continue;

            foreach (var candidate in candidates)
            {
                if (!translated.AsSpan(index).StartsWith(candidate.Key.AsSpan(), StringComparison.Ordinal))
                    continue;

                translated = translated.Remove(index, candidate.Key.Length)
                    .Insert(index, candidate.Value);
                index += candidate.Value.Length - 1;
                changed = true;
                break;
            }
        }

        return changed;
    }

    private static string NormalizeSubSkillKey(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace("\r", "\n", StringComparison.Ordinal)
             .Replace("\\r\\n", "\n", StringComparison.Ordinal)
             .Replace("\\n", "\n", StringComparison.Ordinal);

    private void AddKnownTranslationValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        _knownTranslationValues.Add(value);
        _knownTranslationValues.Add(NormalizeSubSkillKey(value));
    }

    private void AddKnownStoryTranslationValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        _knownStoryTranslationValues.Add(value);
        _knownStoryTranslationValues.Add(NormalizeStoryDisplayValue(value));
    }

    private static string NormalizeStoryDisplayValue(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : NormalizeSubSkillKey(UtageDisplayControlTag.Replace(value, string.Empty));

    private Dictionary<string, string> LoadScenario(string scenarioId)
    {
        var directPath = Path.Combine(_root, "scenarios", scenarioId, $"{_config.Language.Value}.json");
        if (File.Exists(directPath))
            return ReadTable(directPath);

        return _scenarioPaths.TryGetValue(scenarioId, out var indexedPath)
            ? ReadTable(indexedPath)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadTable(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    table[property.Name] = property.Value.GetString() ?? string.Empty;
                else if ((property.NameEquals("$global") || property.NameEquals("$globe")) &&
                         property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var row in property.Value.EnumerateObject())
                    {
                        if (row.Value.ValueKind == JsonValueKind.String)
                        {
                            table[row.Name] = row.Value.GetString() ?? string.Empty;
                            continue;
                        }

                        // A path/category key inside $global is metadata only;
                        // its child rows are still global translations.
                        if (row.Value.ValueKind == JsonValueKind.Object)
                            foreach (var child in row.Value.EnumerateObject())
                                if (child.Value.ValueKind == JsonValueKind.String)
                                    table[child.Name] = child.Value.GetString() ?? string.Empty;
                    }
                }
            }
            return table;
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"读取翻译表失败 {path}: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<UiPathTable> ReadUiPathTables(string path)
    {
        var result = new List<UiPathTable>();
        try
        {
            if (!File.Exists(path))
                return result;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("$paths") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pathProperty in property.Value.EnumerateObject())
                        AddUiPathTable(result, pathProperty);
                }
                else if ((property.NameEquals("$global") || property.NameEquals("$globe")) &&
                         property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var globalProperty in property.Value.EnumerateObject())
                        if (globalProperty.Value.ValueKind == JsonValueKind.Object)
                            AddUiPathTable(result, globalProperty);
                }
                else if (!property.Name.StartsWith('$') && property.Value.ValueKind == JsonValueKind.Object)
                {
                    AddUiPathTable(result, property);
                }
            }
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"读取 UI 路径分类失败 {path}: {ex.Message}");
        }
        return result;
    }

    private static void AddUiPathTable(
        ICollection<UiPathTable> result,
        JsonProperty pathProperty)
    {
        if (pathProperty.Value.ValueKind != JsonValueKind.Object)
            return;
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in pathProperty.Value.EnumerateObject())
            if (row.Value.ValueKind == JsonValueKind.String)
                table[row.Name] = row.Value.GetString() ?? string.Empty;
        var normalizedPath = NormalizeUiPath(pathProperty.Name);
        if (table.Count > 0 && !string.IsNullOrEmpty(normalizedPath))
            result.Add(new UiPathTable(normalizedPath, table));
    }

    private sealed class UiPathTable
    {
        public UiPathTable(string path, Dictionary<string, string> exact)
        {
            Path = path;
            Exact = exact;
            NumberTemplates = new List<NumberTemplate>();
            ExTemplates = new List<ExTemplate>();
            foreach (var pair in exact)
            {
                if (string.IsNullOrEmpty(pair.Value))
                    continue;
                AddNumberTemplate(NumberTemplates, pair.Key, pair.Value);
                AddExTemplate(ExTemplates, pair.Key, pair.Value);
            }
        }

        public string Path { get; }
        public Dictionary<string, string> Exact { get; }
        public List<NumberTemplate> NumberTemplates { get; }
        public List<ExTemplate> ExTemplates { get; }
    }

}
