using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MonsterMusumeTDMod.Core;

namespace MonsterMusumeTDMod.Services;

public sealed class TranslationManager
{
    private const string NumberPlaceholder = "<num>";
    private const string ExPlaceholder = "<ex>";
    private const int UiResultCapacity = 1024;
    private const int MinSubSkillFragmentLength = 4;
    private const int MinStoryFragmentLength = 4;

    private readonly string _root;
    private readonly ModConfig _config;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uiTexts = new(StringComparer.Ordinal);

    // Two-generation cache: when primary reaches capacity, demote to secondary
    // instead of wiping all cached UI translations at once.
    private Dictionary<(string Source, string Path), (bool Matched, string Value)> _uiResultsPrimary = new(UiResultCapacity);
    private Dictionary<(string Source, string Path), (bool Matched, string Value)> _uiResultsSecondary = new(UiResultCapacity);
    private Dictionary<string, (bool Matched, string Value)> _uiSourceOnlyPrimary = new(UiResultCapacity, StringComparer.Ordinal);
    private Dictionary<string, (bool Matched, string Value)> _uiSourceOnlySecondary = new(UiResultCapacity, StringComparer.Ordinal);
    private Dictionary<(string Source, string Path), (bool Matched, string Value)> _subSkillResultsPrimary = new(UiResultCapacity);
    private Dictionary<(string Source, string Path), (bool Matched, string Value)> _subSkillResultsSecondary = new(UiResultCapacity);

    private readonly List<UiPathTable> _uiPathTables = new();
    private readonly Dictionary<string, List<UiPathTable>>
        _uiPathTablesByLeaf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _subSkills = new(StringComparer.Ordinal);
    private readonly List<SceneTemplate> _nameSceneTemplates = new();
    private readonly List<SceneTemplate> _uiSceneTemplates = new();
    private readonly List<SceneTemplate> _subSkillSceneTemplates = new();
    private readonly List<ExTemplate> _nameExTemplates = new();
    private readonly List<NumberTemplate> _uiNumberTemplates = new();
    private readonly List<ExTemplate> _uiExTemplates = new();
    private readonly List<NumberTemplate> _subSkillNumberTemplates = new();
    private readonly List<ExTemplate> _subSkillExTemplates = new();
    private readonly Dictionary<char, List<KeyValuePair<string, string>>> _subSkillFragmentIndex = new();
    private readonly HashSet<string> _knownTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownNameTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownUiTextTranslationValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownSubSkillTranslationValues = new(StringComparer.Ordinal);
    private readonly RuntimeTextCollector _runtimeTextCollector;

    // Immutable copy-on-write snapshot for background scenario loading.
    // Main thread reads Volatile.Read(ref _scenarioSnapshot) without locking or mutating live dictionaries.
    private ScenarioSnapshot _scenarioSnapshot = new();
    private Task _scenarioLoadTask;

    private sealed class ScenarioSnapshot
    {
        public readonly Dictionary<string, string> ScenarioPaths = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> ResourceScenarioIds = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Fallback = new(StringComparer.Ordinal);
        public readonly StoryTranslationIndex StoryParseIndex = new();
        public readonly StoryTranslationIndex StoryDisplayIndex = new();
        public readonly Dictionary<char, List<KeyValuePair<string, string>>> StoryFragmentIndex = new();
        public readonly HashSet<string> KnownStoryTranslationValues = new(StringComparer.Ordinal);
        public readonly object FragmentIndexLock = new();
        public bool IsFragmentIndexBuilt;
        public bool IsLoaded;
    }

    private static readonly Regex ScenarioResourceIdPattern = new(
        @"^(?<base>\d+)[a-z]\d+_(?<kind>nml|r18)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex ResourceNumberPattern = new(
        @"(?<kind>r18|nml)_(?<base>\d+)(?:_|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex UtageDisplayControlTag = new(
        @"</?(?:interval|speed|sound)(?:=[^>]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    public TranslationManager(string pluginRoot, ModConfig config)
    {
        _root = Path.Combine(pluginRoot, "MonsterMusumeTDMod", "translations");
        _config = config;
        _runtimeTextCollector = new RuntimeTextCollector(_root);
    }

    public void LoadStatic()
    {
        var loadTimer = Stopwatch.StartNew();

        // Wait for any previous in-flight background scenario load before starting a new one.
        var previousTask = Volatile.Read(ref _scenarioLoadTask);
        if (previousTask != null && !previousTask.IsCompleted)
        {
            try { previousTask.GetAwaiter().GetResult(); }
            catch { }
        }

        UiHierarchyClassifier.ClearCache();
        _uiResultsPrimary.Clear();
        _uiResultsSecondary.Clear();
        _uiSourceOnlyPrimary.Clear();
        _uiSourceOnlySecondary.Clear();
        _subSkillResultsPrimary.Clear();
        _subSkillResultsSecondary.Clear();
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
        _knownUiTextTranslationValues.Clear();
        _knownSubSkillTranslationValues.Clear();

        var language = _config.Language.Value;

        foreach (var pair in ReadTable(Path.Combine(_root, "names", $"{language}.json")))
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
        SortExTemplates(_nameExTemplates);

        var uiCanvasRoot = Path.Combine(_root, "UICanvas");
        if (Directory.Exists(uiCanvasRoot))
        {
            foreach (var path in Directory.EnumerateFiles(
                uiCanvasRoot,
                $"{language}.json",
                SearchOption.AllDirectories))
            {
                ReadUiFile(path, out var globalEntries, out var pathTables);
                foreach (var pair in globalEntries)
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

                foreach (var pathTable in pathTables)
                {
                    _uiPathTables.Add(pathTable);
                    foreach (var pair in pathTable.Exact)
                    {
                        if (string.IsNullOrEmpty(pair.Value))
                            continue;
                        if (_uiTexts.TryAdd(pair.Key, pair.Value))
                        {
                            AddNumberTemplate(_uiNumberTemplates, pair.Key, pair.Value);
                            AddExTemplate(_uiExTemplates, pair.Key, pair.Value);
                        }
                        AddKnownTranslationValue(pair.Value);
                        _knownUiTextTranslationValues.Add(pair.Value);
                        var normalizedVal = NormalizeSubSkillKey(pair.Value);
                        if (!ReferenceEquals(normalizedVal, pair.Value))
                            _knownUiTextTranslationValues.Add(normalizedVal);
                    }
                }
            }
        }
        _uiSceneTemplates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
        SortNumberTemplates(_uiNumberTemplates);
        SortExTemplates(_uiExTemplates);
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
        foreach (var pair in ReadTable(Path.Combine(_root, "subskills", $"{language}.json")))
        {
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
                AddFragmentRule(_subSkillFragmentIndex, normalizedKey, pair.Value, MinSubSkillFragmentLength);
                AddKnownTranslationValue(pair.Value);
                _knownSubSkillTranslationValues.Add(pair.Value);
            }
        }
        _subSkillSceneTemplates.Sort((left, right) => right.SourceLength.CompareTo(left.SourceLength));
        SortNumberTemplates(_subSkillNumberTemplates);
        SortExTemplates(_subSkillExTemplates);
        SortFragmentIndex(_subSkillFragmentIndex);

        Core.Plugin.Log?.LogInfo(
            $"Loaded UI/names/sub-skill tables in {loadTimer.ElapsedMilliseconds} ms " +
            "and starting background scenario indexing " +
            $"(names={_names.Count}, ui={_uiTexts.Count}, subskills={_subSkills.Count})");

        var scenarioRoot = Path.Combine(_root, "scenarios");
        // Publish an unloaded snapshot before starting the replacement task. This
        // prevents readers during a reload from accepting the previous generation
        // as complete and observing stale story translations.
        Volatile.Write(ref _scenarioSnapshot, new ScenarioSnapshot());
        var loadTask = Task.Run(() => LoadScenariosCore(scenarioRoot, language));
        Volatile.Write(ref _scenarioLoadTask, loadTask);
    }

    private void LoadScenariosCore(string scenarioRoot, string language)
    {
        var scenarioTimer = Stopwatch.StartNew();
        var snapshot = new ScenarioSnapshot();
        try
        {
            ResetInferredStoryScenario();

            if (!Directory.Exists(scenarioRoot))
                return;

            var scenarioIdsWithTranslations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var promotedStoryTranslations = 0;
            foreach (var path in Directory.EnumerateFiles(scenarioRoot, $"{language}.json", SearchOption.AllDirectories))
            {
                var scenarioId = Path.GetFileName(Path.GetDirectoryName(path));
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    using var document = JsonDocument.Parse(bytes);
                    var hasTranslations = false;
                    foreach (var (key, rawVal) in EnumerateScenarioRows(document.RootElement))
                    {
                        var parseKey = NormalizeSubSkillKey(key);
                        var displayKey = NormalizeStoryDisplayValue(parseKey);
                        if (string.IsNullOrEmpty(rawVal))
                        {
                            snapshot.StoryParseIndex.RecordSceneOccurrence(scenarioId, parseKey);
                            snapshot.StoryDisplayIndex.RecordSceneOccurrence(scenarioId, displayKey);
                            snapshot.Fallback.TryAdd(key, string.Empty);
                            continue;
                        }

                        hasTranslations = true;
                        var val = NormalizeSubSkillKey(rawVal);
                        snapshot.StoryParseIndex.Add(scenarioId, parseKey, val);
                        snapshot.StoryDisplayIndex.Add(scenarioId, displayKey, val);
                        if (!ReferenceEquals(displayKey, parseKey) &&
                            !string.Equals(displayKey, parseKey, StringComparison.Ordinal))
                        {
                            snapshot.StoryDisplayIndex.Add(scenarioId, parseKey, val);
                        }

                        var isUntranslatedCopy =
                            ContainsJapanese(parseKey) &&
                            (string.Equals(parseKey, val, StringComparison.Ordinal) ||
                             string.Equals(displayKey, NormalizeStoryDisplayValue(val), StringComparison.Ordinal));
                        if (!isUntranslatedCopy)
                            AddKnownStoryTranslationValue(snapshot.KnownStoryTranslationValues, val);

                        if (!snapshot.Fallback.TryGetValue(key, out var existingFallback))
                        {
                            snapshot.Fallback.Add(key, val);
                        }
                        else if (string.IsNullOrEmpty(existingFallback))
                        {
                            snapshot.Fallback[key] = val;
                            promotedStoryTranslations++;
                        }

                        if (!ReferenceEquals(parseKey, key) &&
                            !string.Equals(parseKey, key, StringComparison.Ordinal))
                        {
                            if (!snapshot.Fallback.TryGetValue(parseKey, out var existingNormalized) ||
                                string.IsNullOrEmpty(existingNormalized))
                            {
                                snapshot.Fallback[parseKey] = val;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(scenarioId))
                    {
                        if (!snapshot.ScenarioPaths.ContainsKey(scenarioId) ||
                            (hasTranslations && !scenarioIdsWithTranslations.Contains(scenarioId)))
                            snapshot.ScenarioPaths[scenarioId] = path;
                        if (hasTranslations)
                            scenarioIdsWithTranslations.Add(scenarioId);
                        IndexResourceScenario(snapshot, scenarioId);
                    }
                }
                catch (Exception fileEx)
                {
                    Core.Plugin.Log?.LogWarning($"读取剧情翻译表失败 {path}: {fileEx.Message}");
                }
            }

            Core.Plugin.Log?.LogInfo(
                $"Loaded {snapshot.Fallback.Count} unique story row(s) in {scenarioTimer.ElapsedMilliseconds} ms; " +
                $"promoted {promotedStoryTranslations} translated duplicate(s) over empty rows");
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"加载剧情翻译表失败: {ex.Message}");
        }
        finally
        {
            snapshot.IsLoaded = true;
            Volatile.Write(ref _scenarioSnapshot, snapshot);
        }
    }

    private static IEnumerable<(string Key, string Value)> EnumerateScenarioRows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                yield return (property.Name, property.Value.GetString() ?? string.Empty);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var row in property.Value.EnumerateObject())
                {
                    if (row.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return (row.Name, row.Value.GetString() ?? string.Empty);
                    }
                    else if (row.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var child in row.Value.EnumerateObject())
                        {
                            if (child.Value.ValueKind == JsonValueKind.String)
                                yield return (child.Name, child.Value.GetString() ?? string.Empty);
                        }
                    }
                }
            }
        }
    }

    private static void IndexResourceScenario(ScenarioSnapshot snapshot, string scenarioId)
    {
        var match = ScenarioResourceIdPattern.Match(scenarioId);
        if (!match.Success)
            return;

        var baseNum = match.Groups["base"].Value;
        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        foreach (var key in new[] { $"{kind}_{baseNum}", baseNum })
        {
            if (!snapshot.ResourceScenarioIds.TryGetValue(key, out var list))
                snapshot.ResourceScenarioIds.Add(key, list = new List<string>());
            if (!list.Contains(scenarioId))
                list.Add(scenarioId);
        }
    }

    private static void EnsureStoryFragmentIndex(ScenarioSnapshot snapshot)
    {
        if (snapshot == null || snapshot.IsFragmentIndexBuilt)
            return;

        lock (snapshot.FragmentIndexLock)
        {
            if (snapshot.IsFragmentIndexBuilt)
                return;

            foreach (var pair in snapshot.Fallback)
            {
                if (string.IsNullOrEmpty(pair.Value))
                    continue;
                var parseKey = NormalizeSubSkillKey(pair.Key);
                var cleanVal = NormalizeSubSkillKey(pair.Value);
                if (!ContainsJapanese(parseKey) || string.Equals(parseKey, cleanVal, StringComparison.Ordinal))
                    continue;
                if (snapshot.StoryParseIndex.TryGet(parseKey, null, out _, out _))
                {
                    AddFragmentRule(snapshot.StoryFragmentIndex, pair.Key, cleanVal, MinStoryFragmentLength);
                    if (!ReferenceEquals(parseKey, pair.Key) && !string.Equals(parseKey, pair.Key, StringComparison.Ordinal))
                        AddFragmentRule(snapshot.StoryFragmentIndex, parseKey, cleanVal, MinStoryFragmentLength);
                }
            }
            SortFragmentIndex(snapshot.StoryFragmentIndex);
            snapshot.IsFragmentIndexBuilt = true;
        }
    }

    private ScenarioSnapshot GetLoadedScenarioSnapshot()
    {
        var snapshot = Volatile.Read(ref _scenarioSnapshot);
        if (snapshot.IsLoaded)
            return snapshot;

        var task = Volatile.Read(ref _scenarioLoadTask);
        if (task != null && !task.IsCompleted)
            task.GetAwaiter().GetResult();

        return Volatile.Read(ref _scenarioSnapshot);
    }

    public bool TryTranslateName(string source, out string translated)
        => TryTranslateName(source, null, out translated);

    public bool TryTranslateName(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (TryTranslateNameCore(source, scenePath, out translated))
        {
            if (!string.IsNullOrEmpty(translated))
            {
                AddKnownTranslationValue(translated);
                _knownNameTranslationValues.Add(translated);
            }
            return true;
        }
        return false;
    }

    private bool TryTranslateNameCore(string source, string scenePath, out string translated)
    {
        translated = source;
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
        if (source.Length > 8192 || (scenePath?.Length ?? 0) > 4096)
        {
            var matchedLong = TryTranslateUiTextCore(source, scenePath, out translated);
            if (matchedLong && !string.IsNullOrEmpty(translated))
            {
                AddKnownTranslationValue(translated);
                _knownUiTextTranslationValues.Add(translated);
                _knownUiTextTranslationValues.Add(NormalizeSubSkillKey(translated));
            }
            return matchedLong;
        }

        var key = (source, scenePath ?? string.Empty);
        if (_uiResultsPrimary.TryGetValue(key, out var cached))
        {
            translated = cached.Value;
            return cached.Matched;
        }

        if (_uiResultsSecondary.TryGetValue(key, out cached))
        {
            PromoteOrInsertUiCache(key, cached);
            translated = cached.Value;
            return cached.Matched;
        }

        var matched = TryTranslateUiTextCore(source, scenePath, out translated);
        if (matched && !string.IsNullOrEmpty(translated))
        {
            AddKnownTranslationValue(translated);
            _knownUiTextTranslationValues.Add(translated);
            _knownUiTextTranslationValues.Add(NormalizeSubSkillKey(translated));
        }
        PromoteOrInsertUiCache(key, (matched, translated));
        return matched;
    }

    private void PromoteOrInsertUiCache(
        (string Source, string Path) key,
        (bool Matched, string Value) entry)
    {
        if (_uiResultsPrimary.Count >= UiResultCapacity)
        {
            _uiResultsSecondary.Clear();
            (_uiResultsPrimary, _uiResultsSecondary) = (_uiResultsSecondary, _uiResultsPrimary);
        }
        _uiResultsPrimary[key] = entry;
    }

    private void PromoteOrInsertUiSourceOnlyCache(string source, (bool Matched, string Value) entry)
    {
        if (_uiSourceOnlyPrimary.Count >= UiResultCapacity)
        {
            _uiSourceOnlySecondary.Clear();
            (_uiSourceOnlyPrimary, _uiSourceOnlySecondary) = (_uiSourceOnlySecondary, _uiSourceOnlyPrimary);
        }
        _uiSourceOnlyPrimary[source] = entry;
    }

    private bool TryTranslateUiTextCore(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (_uiSceneTemplates.Count > 0 &&
            TryTranslateSceneTemplate(source, scenePath, _uiSceneTemplates, out translated))
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

        var normalizedSource = NormalizeSubSkillKey(source);
        var hasNormalizedVariant =
            !ReferenceEquals(normalizedSource, source) &&
            !string.Equals(normalizedSource, source, StringComparison.Ordinal);

        if (hasNormalizedVariant)
        {
            if (_uiSceneTemplates.Count > 0 &&
                TryTranslateSceneTemplate(normalizedSource, scenePath, _uiSceneTemplates, out translated))
                return true;

            if (TryTranslateUiPath(normalizedSource, scenePath, out translated))
                return true;
        }

        if (source.Length <= 1024)
        {
            if (_uiSourceOnlyPrimary.TryGetValue(source, out var sourceCached))
            {
                translated = sourceCached.Value;
                return sourceCached.Matched;
            }
            if (_uiSourceOnlySecondary.TryGetValue(source, out sourceCached))
            {
                PromoteOrInsertUiSourceOnlyCache(source, sourceCached);
                translated = sourceCached.Value;
                return sourceCached.Matched;
            }
        }

        var matchedSourceOnly = TryTranslateUiSourceOnlyTemplates(
            source,
            normalizedSource,
            hasNormalizedVariant,
            out translated);
        if (source.Length <= 1024)
            PromoteOrInsertUiSourceOnlyCache(source, (matchedSourceOnly, translated));
        return matchedSourceOnly;
    }

    private bool TryTranslateUiSourceOnlyTemplates(
        string source,
        string normalizedSource,
        bool hasNormalizedVariant,
        out string translated)
    {
        if (TryTranslateNumberTemplate(source, _uiNumberTemplates, out translated))
            return true;

        if (TryTranslateExTemplate(source, _uiExTemplates, TryTranslateCapturedUiText, out translated))
            return true;

        if (!hasNormalizedVariant)
        {
            translated = source;
            return false;
        }

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
        if (TryTranslateUiPathNormalized(source, normalizedPath, out translated))
            return true;

        // Many toggle/tab buttons store their translation table under .../Off/Text or .../On/Text
        // depending on which state was active when F8 captured the snapshot. Fall back to the
        // opposite On/Off state path so button state switches always resolve the same table.
        ReadOnlySpan<(string A, string B)> statePairs = stackalloc (string, string)[]
        {
            ("/On/", "/Off/"),
            ("/Active/", "/Inactive/"),
            ("/Select/", "/Unselect/"),
            ("/Selected/", "/Unselected/"),
            ("/Enable/", "/Disable/")
        };
        foreach (var (segA, segB) in statePairs)
        {
            if (normalizedPath.Contains(segA, StringComparison.OrdinalIgnoreCase))
            {
                if (TryTranslateUiPathNormalized(source, SwapPathSegment(normalizedPath, segA, segB), out translated))
                    return true;
            }
            else if (normalizedPath.Contains(segB, StringComparison.OrdinalIgnoreCase))
            {
                if (TryTranslateUiPathNormalized(source, SwapPathSegment(normalizedPath, segB, segA), out translated))
                    return true;
            }
        }

        return false;
    }

    private bool TryTranslateUiPathNormalized(string source, string normalizedPath, out string translated)
    {
        translated = source;
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

    private static string SwapPathSegment(string path, string fromSegment, string toSegment)
    {
        var idx = path.LastIndexOf(fromSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return path;
        return string.Concat(path.AsSpan(0, idx), toSegment, path.AsSpan(idx + fromSegment.Length));
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

    public bool CanPossiblyNeedTranslation(string source) =>
        !string.IsNullOrEmpty(source) &&
        (ContainsJapanese(source) ||
         _uiTexts.ContainsKey(source) ||
         _subSkills.ContainsKey(source) ||
         _names.ContainsKey(source));

    private static bool PathContainsSegmentSequence(string fullPath, string categoryPath) =>
        UiStylePolicy.PathContains(fullPath, categoryPath);

    private static string NormalizeUiPath(string path) =>
        UiStylePolicy.NormalizePath(path);

    public bool TryTranslateSubSkill(string source, out string translated)
        => TryTranslateSubSkill(source, null, out translated);

    public bool TryTranslateSubSkill(string source, string scenePath, out string translated)
    {
        translated = source;
        if (!_config.Enabled.Value || string.IsNullOrEmpty(source))
            return false;

        if (source.Length <= 8192 && (scenePath?.Length ?? 0) <= 4096)
        {
            var cacheKey = (source, scenePath ?? string.Empty);
            if (_subSkillResultsPrimary.TryGetValue(cacheKey, out var cached))
            {
                translated = cached.Value;
                return cached.Matched;
            }
            if (_subSkillResultsSecondary.TryGetValue(cacheKey, out cached))
            {
                PromoteOrInsertSubSkillCache(cacheKey, cached);
                translated = cached.Value;
                return cached.Matched;
            }

            var matched = TryTranslateSubSkillCore(source, scenePath, out translated);
            if (matched && !string.IsNullOrEmpty(translated))
            {
                AddKnownTranslationValue(translated);
                _knownSubSkillTranslationValues.Add(translated);
                _knownSubSkillTranslationValues.Add(NormalizeSubSkillKey(translated));
            }
            PromoteOrInsertSubSkillCache(cacheKey, (matched, translated));
            return matched;
        }

        return TryTranslateSubSkillCore(source, scenePath, out translated);
    }

    private void PromoteOrInsertSubSkillCache(
        (string Source, string Path) key,
        (bool Matched, string Value) entry)
    {
        if (_subSkillResultsPrimary.Count >= UiResultCapacity)
        {
            _subSkillResultsSecondary.Clear();
            (_subSkillResultsPrimary, _subSkillResultsSecondary) = (_subSkillResultsSecondary, _subSkillResultsPrimary);
        }
        _subSkillResultsPrimary[key] = entry;
    }

    private bool TryTranslateSubSkillCore(string source, string scenePath, out string translated)
    {
        translated = source;
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
         _knownSubSkillTranslationValues.Contains(NormalizeSubSkillKey(value)));

    public bool IsKnownUiTextTranslationValue(string value) =>
        !string.IsNullOrEmpty(value) &&
        (_knownUiTextTranslationValues.Contains(value) ||
         _knownUiTextTranslationValues.Contains(NormalizeSubSkillKey(value)));

    /// <summary>Story and log text use scene-indexed lookup with unique global fallback.</summary>
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

        var snapshot = GetLoadedScenarioSnapshot();
        var scene = ResolveStoryScenarioIdCore(snapshot, scenarioId);
        if (scene.Length == 0 && !string.IsNullOrEmpty(resourceId))
            scene = ResolveStoryScenarioIdCore(snapshot, resourceId);

        var normalized = NormalizeSubSkillKey(source);
        if (TryTranslateStoryExactSingle(snapshot, normalized, scene, resourceId, out translated, out var conflict, out var uniqueScene))
        {
            if (!string.IsNullOrEmpty(uniqueScene))
                StorySceneContext.NotifyUniqueSceneMatched(uniqueScene);
            return true;
        }
        if (conflict)
            return false;

        if (TryTranslateConcatenatedStoryExact(snapshot, normalized, scene, resourceId, out translated))
        {
            AddKnownTranslationValue(translated);
            return true;
        }

        if (IsKnownStoryTranslationValueCore(snapshot, source))
            return false;

        EnsureStoryFragmentIndex(snapshot);
        if (TryTranslateIndexedFragments(source, snapshot.StoryFragmentIndex, out var fragmentTranslated))
        {
            if (TryTranslateConcatenatedStoryExact(snapshot, NormalizeSubSkillKey(fragmentTranslated), scene, resourceId, out var completedTranslated))
                translated = completedTranslated;
            else
                translated = fragmentTranslated;
            AddKnownTranslationValue(translated);
            return true;
        }

        translated = source;
        return false;
    }

    private static bool TryTranslateStoryExactSingle(
        ScenarioSnapshot snapshot,
        string normalized,
        string scene,
        string resourceId,
        out string translated,
        out bool conflict,
        out string uniqueScene)
    {
        translated = normalized;
        conflict = false;
        uniqueScene = string.Empty;

        var displayNormalized = NormalizeStoryDisplayValue(normalized);
        var hasDifferentDisplay =
            !ReferenceEquals(displayNormalized, normalized) &&
            !string.Equals(displayNormalized, normalized, StringComparison.Ordinal);

        // 1. Check active scene table first so a global conflict never blocks an exact scene match.
        if (!string.IsNullOrEmpty(scene))
        {
            if (TryLookupStoryIndexInScene(snapshot.StoryParseIndex, normalized, scene, out translated, out uniqueScene) ||
                TryLookupStoryIndexInScene(snapshot.StoryDisplayIndex, normalized, scene, out translated, out uniqueScene) ||
                (hasDifferentDisplay &&
                 (TryLookupStoryIndexInScene(snapshot.StoryParseIndex, displayNormalized, scene, out translated, out uniqueScene) ||
                  TryLookupStoryIndexInScene(snapshot.StoryDisplayIndex, displayNormalized, scene, out translated, out uniqueScene))))
            {
                return true;
            }
        }
        else if (!string.IsNullOrEmpty(resourceId) &&
                 TryGetResourceCandidateScenes(snapshot, resourceId, out var resourceCandidates))
        {
            if (snapshot.StoryParseIndex.TryGetFromCandidates(normalized, resourceCandidates, out translated) ||
                snapshot.StoryDisplayIndex.TryGetFromCandidates(displayNormalized, resourceCandidates, out translated))
            {
                return true;
            }
        }

        // 2. Global / fallback lookup across parse and display indexes.
        if (TryLookupStoryIndex(snapshot.StoryParseIndex, normalized, scene, out translated, out var parseConflict, out uniqueScene))
            return true;

        if (TryLookupStoryIndex(snapshot.StoryDisplayIndex, normalized, scene, out translated, out var displayConflict, out uniqueScene))
            return true;

        if (hasDifferentDisplay)
        {
            if (TryLookupStoryIndex(snapshot.StoryParseIndex, displayNormalized, scene, out translated, out var dispParseConflict, out uniqueScene))
                return true;
            if (!parseConflict && !dispParseConflict &&
                TryLookupStoryIndex(snapshot.StoryDisplayIndex, displayNormalized, scene, out translated, out var dispDisplayConflict, out uniqueScene))
            {
                return true;
            }
            if (parseConflict || displayConflict || dispParseConflict)
            {
                conflict = true;
                return false;
            }
        }

        if (parseConflict || displayConflict)
        {
            conflict = true;
            return false;
        }

        translated = normalized;
        return false;
    }

    /// <summary>
    /// Replaces a complete raw UTAGE row before its control tags are parsed.
    /// Story wildcards are intentionally unsupported here.
    /// </summary>
    public bool TryTranslateStoryBeforeParse(string source, out string translated)
    {
        var scene = StorySceneContext.GetParsingScene(source);
        if (TryTranslateStoryBeforeParseCore(source, scene, allowCandidateInference: StorySceneContext.AllowsInference, out translated, out var uniqueScene))
        {
            if (!string.IsNullOrEmpty(uniqueScene))
                StorySceneContext.NotifyUniqueSceneMatched(uniqueScene);
            return true;
        }
        return false;
    }

    public bool TryTranslateStoryBeforeParse(string source, string scenarioId, out string translated)
        => TryTranslateStoryBeforeParseCore(source, scenarioId, allowCandidateInference: false, out translated, out _);

    private bool TryTranslateStoryBeforeParseCore(
        string source,
        string scenarioId,
        bool allowCandidateInference,
        out string translated,
        out string uniqueScene)
    {
        translated = source;
        uniqueScene = string.Empty;
        if (!_config.Enabled.Value || !_config.TranslateScenarios.Value ||
            string.IsNullOrEmpty(source))
            return false;

        var snapshot = GetLoadedScenarioSnapshot();
        var lookup = NormalizeSubSkillKey(source);
        if (IsSingleKnownStoryValue(snapshot, source) || IsSingleKnownStoryValue(snapshot, lookup))
            return false;

        var scene = ResolveStoryScenarioIdCore(snapshot, scenarioId);
        if (scene.Length == 0 && allowCandidateInference && !string.IsNullOrEmpty(ScenarioContext.CurrentResourceId))
            scene = ResolveStoryScenarioIdCore(snapshot, ScenarioContext.CurrentResourceId);

        if (TryTranslateStoryBeforeParseSingle(snapshot, lookup, scene, allowCandidateInference, out translated, out var conflict, out uniqueScene))
            return true;
        if (conflict)
            return false;

        if (TryTranslateConcatenatedStoryBeforeParse(snapshot, lookup, scene, allowCandidateInference, out translated, out uniqueScene))
        {
            AddKnownTranslationValue(translated);
            return true;
        }

        translated = source;
        return false;
    }

    private static bool TryTranslateStoryBeforeParseSingle(
        ScenarioSnapshot snapshot,
        string lookup,
        string scene,
        bool allowCandidateInference,
        out string translated,
        out bool conflict,
        out string uniqueScene)
    {
        translated = lookup;
        conflict = false;
        uniqueScene = string.Empty;

        var displayLookup = NormalizeStoryDisplayValue(lookup);
        var hasControlTag = HasUtageDisplayControlTag(lookup);
        var hasDifferentDisplay =
            !ReferenceEquals(displayLookup, lookup) &&
            !string.Equals(displayLookup, lookup, StringComparison.Ordinal);

        // 1. Check active scene table first before any global conflict check.
        if (!string.IsNullOrEmpty(scene))
        {
            if (TryLookupStoryIndexInScene(snapshot.StoryParseIndex, lookup, scene, out translated, out uniqueScene) ||
                (!hasControlTag && TryLookupStoryIndexInScene(snapshot.StoryDisplayIndex, lookup, scene, out translated, out uniqueScene)) ||
                (hasDifferentDisplay &&
                 (TryLookupStoryIndexInScene(snapshot.StoryParseIndex, displayLookup, scene, out translated, out uniqueScene) ||
                  TryLookupStoryIndexInScene(snapshot.StoryDisplayIndex, displayLookup, scene, out translated, out uniqueScene))))
            {
                return true;
            }
        }
        else if (allowCandidateInference && !hasControlTag)
        {
            var scenesForSource = snapshot.StoryParseIndex.GetScenesForSource(lookup);
            if (scenesForSource.Count == 0 && hasDifferentDisplay)
                scenesForSource = snapshot.StoryParseIndex.GetScenesForSource(displayLookup);
            if (scenesForSource.Count > 0 && scenesForSource.Count <= 16)
            {
                var narrowed = StorySceneContext.NarrowCandidateScenes(scenesForSource);
                if (narrowed.Count > 0 &&
                    snapshot.StoryParseIndex.TryGetFromCandidates(lookup, narrowed, out translated) &&
                    !string.Equals(lookup, translated, StringComparison.Ordinal))
                {
                    if (narrowed.Count == 1)
                    {
                        foreach (var single in narrowed)
                            uniqueScene = single;
                    }
                    return true;
                }
            }
        }

        // 2. Global / fallback lookup.
        if (TryLookupStoryIndex(snapshot.StoryParseIndex, lookup, scene, out translated, out conflict, out uniqueScene))
            return true;

        if (!hasControlTag)
        {
            if (conflict)
                return false;
            if (TryLookupStoryIndex(snapshot.StoryDisplayIndex, lookup, scene, out translated, out conflict, out uniqueScene))
                return true;
            if (conflict)
                return false;
        }
        else if (hasDifferentDisplay)
        {
            if (TryLookupStoryIndex(snapshot.StoryParseIndex, displayLookup, scene, out translated, out var displayConflict, out uniqueScene))
                return true;
            if (!conflict && !displayConflict &&
                TryLookupStoryIndex(snapshot.StoryDisplayIndex, displayLookup, scene, out translated, out displayConflict, out uniqueScene))
            {
                return true;
            }
            if (conflict || displayConflict)
            {
                conflict = true;
                return false;
            }
        }

        translated = lookup;
        return false;
    }

    private bool TryTranslateConcatenatedStoryExact(
        ScenarioSnapshot snapshot,
        string normalized,
        string scene,
        string resourceId,
        out string translated)
    {
        translated = normalized;
        var clauses = SplitIntoStoryClauses(normalized);
        if (clauses.Count < 2)
            return false;

        var activeScene = scene;
        var builder = new StringBuilder(normalized.Length + 16);
        if (clauses[0].Start > 0)
            builder.Append(normalized, 0, clauses[0].Start);

        var translatedCount = 0;
        var idx = 0;
        while (idx < clauses.Count)
        {
            if (idx > 0 && clauses[idx].Start > clauses[idx - 1].End)
            {
                builder.Append(
                    normalized,
                    clauses[idx - 1].End,
                    clauses[idx].Start - clauses[idx - 1].End);
            }

            var maxEnd = idx == 0 ? clauses.Count - 2 : clauses.Count - 1;
            var matched = false;
            for (var endIdx = maxEnd; endIdx >= idx; endIdx--)
            {
                var startPos = clauses[idx].Start;
                var length = clauses[endIdx].End - startPos;
                var spanText = normalized.Substring(startPos, length);

                if (TryTranslateStoryExactSingle(
                        snapshot,
                        spanText,
                        activeScene,
                        resourceId,
                        out var spanTranslated,
                        out var conflict,
                        out var spanUniqueScene))
                {
                    if (!string.IsNullOrEmpty(spanUniqueScene))
                    {
                        StorySceneContext.NotifyUniqueSceneMatched(spanUniqueScene);
                        if (string.IsNullOrEmpty(activeScene))
                            activeScene = spanUniqueScene;
                    }
                    builder.Append(spanTranslated);
                    translatedCount++;
                    idx = endIdx + 1;
                    matched = true;
                    break;
                }
                if (conflict)
                    return false;

                if (IsSingleKnownStoryValue(snapshot, spanText))
                {
                    builder.Append(spanText);
                    idx = endIdx + 1;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                var singleSpan = normalized.Substring(clauses[idx].Start, clauses[idx].End - clauses[idx].Start);
                var trimmedSingle = singleSpan.Trim(' ', '\t', '\u3000', '\n');
                if (snapshot.Fallback.ContainsKey(singleSpan) ||
                    (trimmedSingle.Length > 0 && snapshot.Fallback.ContainsKey(trimmedSingle)))
                {
                    builder.Append(singleSpan);
                    idx++;
                    continue;
                }
                return false;
            }
        }

        if (clauses[^1].End < normalized.Length)
            builder.Append(normalized, clauses[^1].End, normalized.Length - clauses[^1].End);

        if (translatedCount <= 0)
            return false;

        translated = builder.ToString();
        return !string.Equals(normalized, translated, StringComparison.Ordinal);
    }

    private bool TryTranslateConcatenatedStoryBeforeParse(
        ScenarioSnapshot snapshot,
        string lookup,
        string scene,
        bool allowCandidateInference,
        out string translated,
        out string uniqueScene)
    {
        translated = lookup;
        uniqueScene = string.Empty;
        var clauses = SplitIntoStoryClauses(lookup);
        if (clauses.Count < 2)
            return false;

        var activeScene = scene;
        var builder = new StringBuilder(lookup.Length + 16);
        if (clauses[0].Start > 0)
            builder.Append(lookup, 0, clauses[0].Start);

        var translatedCount = 0;
        var idx = 0;
        while (idx < clauses.Count)
        {
            if (idx > 0 && clauses[idx].Start > clauses[idx - 1].End)
            {
                builder.Append(
                    lookup,
                    clauses[idx - 1].End,
                    clauses[idx].Start - clauses[idx - 1].End);
            }

            var maxEnd = idx == 0 ? clauses.Count - 2 : clauses.Count - 1;
            var matched = false;
            for (var endIdx = maxEnd; endIdx >= idx; endIdx--)
            {
                var startPos = clauses[idx].Start;
                var length = clauses[endIdx].End - startPos;
                var spanText = lookup.Substring(startPos, length);

                if (TryTranslateStoryBeforeParseSingle(
                        snapshot,
                        spanText,
                        activeScene,
                        allowCandidateInference,
                        out var spanTranslated,
                        out var conflict,
                        out var spanUniqueScene))
                {
                    if (!string.IsNullOrEmpty(spanUniqueScene))
                    {
                        uniqueScene = spanUniqueScene;
                        if (allowCandidateInference)
                        {
                            StorySceneContext.NotifyUniqueSceneMatched(spanUniqueScene);
                            if (string.IsNullOrEmpty(activeScene))
                                activeScene = spanUniqueScene;
                        }
                    }
                    builder.Append(spanTranslated);
                    translatedCount++;
                    idx = endIdx + 1;
                    matched = true;
                    break;
                }
                if (conflict)
                    return false;

                if (IsSingleKnownStoryValue(snapshot, spanText))
                {
                    builder.Append(spanText);
                    idx = endIdx + 1;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                var singleSpan = lookup.Substring(clauses[idx].Start, clauses[idx].End - clauses[idx].Start);
                var trimmedSingle = singleSpan.Trim(' ', '\t', '\u3000', '\n');
                if (snapshot.Fallback.ContainsKey(singleSpan) ||
                    (trimmedSingle.Length > 0 && snapshot.Fallback.ContainsKey(trimmedSingle)))
                {
                    builder.Append(singleSpan);
                    idx++;
                    continue;
                }
                return false;
            }
        }

        if (clauses[^1].End < lookup.Length)
            builder.Append(lookup, clauses[^1].End, lookup.Length - clauses[^1].End);

        if (translatedCount <= 0)
            return false;

        translated = builder.ToString();
        return !string.Equals(lookup, translated, StringComparison.Ordinal);
    }

    private static List<(int Start, int End)> SplitIntoStoryClauses(string text)
    {
        var clauses = new List<(int Start, int End)>(4);
        if (string.IsNullOrEmpty(text))
            return clauses;

        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            while (i < n && text[i] == '\n')
                i++;
            if (i >= n)
                break;

            var start = i;
            while (i < n)
            {
                var ch = text[i];
                if (ch == '<')
                {
                    var close = text.IndexOf('>', i);
                    if (close > i)
                    {
                        i = close + 1;
                        continue;
                    }
                }

                if (ch == '\n')
                    break;

                if (IsStoryClauseEndPunctuation(ch))
                {
                    while (i < n && IsStoryClauseEndPunctuation(text[i]))
                        i++;
                    while (i < n && text[i] == '<' && TryAdvancePastUtageControlTag(text, i, out var nextIdx))
                        i = nextIdx;
                    break;
                }

                i++;
            }

            if (i > start)
                clauses.Add((start, i));
        }

        return clauses;
    }

    private static bool IsStoryClauseEndPunctuation(char c) =>
        c == '。' || c == '？' || c == '！' || c == '?' || c == '!' ||
        c == '…' || c == '」' || c == '』' || c == '）' || c == '】' ||
        c == '～' || c == '〜' || c == '―' || c == '─';

    private static bool TryAdvancePastUtageControlTag(string text, int index, out int nextIndex)
    {
        nextIndex = index;
        if (index >= text.Length || text[index] != '<')
            return false;

        var close = text.IndexOf('>', index);
        if (close <= index)
            return false;

        var tagSpan = text.AsSpan(index, close - index + 1);
        if (tagSpan.Contains("interval".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagSpan.Contains("speed".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagSpan.Contains("sound".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            nextIndex = close + 1;
            return true;
        }

        return false;
    }

    private static bool TryLookupStoryIndexInScene(
        StoryTranslationIndex index,
        string lookup,
        string scene,
        out string translated,
        out string uniqueScene)
    {
        if (index.TryGetInScene(lookup, scene, out translated, out uniqueScene) &&
            !string.Equals(lookup, translated, StringComparison.Ordinal))
        {
            return true;
        }

        var trimmed = lookup.Trim(' ', '\t', '\u3000', '\n');
        if (trimmed.Length > 0 && trimmed.Length != lookup.Length)
        {
            if (index.TryGetInScene(trimmed, scene, out var trimmedTranslated, out uniqueScene) &&
                !string.Equals(trimmed, trimmedTranslated, StringComparison.Ordinal))
            {
                var startIdx = lookup.IndexOf(trimmed, StringComparison.Ordinal);
                translated = startIdx >= 0
                    ? string.Concat(
                        lookup.AsSpan(0, startIdx),
                        trimmedTranslated,
                        lookup.AsSpan(startIdx + trimmed.Length))
                    : trimmedTranslated;
                return true;
            }
        }

        if (TryLookupPunctuationOrQuoteVariants(
                trimmed.Length > 0 ? trimmed : lookup,
                scene,
                (string candidate, string sc, out string tr, out bool cf, out string us) =>
                {
                    cf = false;
                    return index.TryGetInScene(candidate, sc, out tr, out us);
                },
                out var variantTranslated,
                out _,
                out uniqueScene))
        {
            translated = variantTranslated;
            return true;
        }

        translated = lookup;
        return false;
    }

    private delegate bool StoryCandidateTryGet(
        string candidate,
        string scene,
        out string translated,
        out bool conflict,
        out string uniqueScene);

    private static bool TryLookupStoryIndex(
        StoryTranslationIndex index,
        string lookup,
        string scene,
        out string translated,
        out bool conflict,
        out string uniqueScene)
    {
        if (index.TryGet(lookup, scene, out translated, out conflict, out uniqueScene))
        {
            if (!string.Equals(lookup, translated, StringComparison.Ordinal))
                return true;
            translated = lookup;
            return false;
        }
        if (conflict)
            return false;

        var trimmed = lookup.Trim(' ', '\t', '\u3000', '\n');
        if (trimmed.Length > 0 && trimmed.Length != lookup.Length)
        {
            if (index.TryGet(trimmed, scene, out var trimmedTranslated, out conflict, out uniqueScene) &&
                !string.Equals(trimmed, trimmedTranslated, StringComparison.Ordinal))
            {
                var startIdx = lookup.IndexOf(trimmed, StringComparison.Ordinal);
                translated = startIdx >= 0
                    ? string.Concat(
                        lookup.AsSpan(0, startIdx),
                        trimmedTranslated,
                        lookup.AsSpan(startIdx + trimmed.Length))
                    : trimmedTranslated;
                return true;
            }
            if (conflict)
                return false;
        }

        var target = trimmed.Length > 0 ? trimmed : lookup;
        if (TryLookupPunctuationOrQuoteVariants(
                target,
                scene,
                index.TryGet,
                out var variantTranslated,
                out conflict,
                out uniqueScene))
        {
            if (trimmed.Length > 0 && trimmed.Length != lookup.Length)
            {
                var startIdx = lookup.IndexOf(trimmed, StringComparison.Ordinal);
                translated = startIdx >= 0
                    ? string.Concat(
                        lookup.AsSpan(0, startIdx),
                        variantTranslated,
                        lookup.AsSpan(startIdx + trimmed.Length))
                    : variantTranslated;
            }
            else
            {
                translated = variantTranslated;
            }
            return true;
        }

        translated = lookup;
        return false;
    }

    private static bool TryLookupPunctuationOrQuoteVariants(
        string target,
        string scene,
        StoryCandidateTryGet tryGet,
        out string translated,
        out bool conflict,
        out string uniqueScene)
    {
        translated = target;
        conflict = false;
        uniqueScene = string.Empty;
        if (string.IsNullOrEmpty(target))
            return false;

        // 1. Trailing period after ellipsis/question/exclamation (e.g. "……。" <-> "……")
        if (target.Length > 2 && target[^1] == '。' &&
            (target[^2] == '…' || target[^2] == '？' || target[^2] == '！'))
        {
            var withoutPeriod = target[..^1];
            if (tryGet(withoutPeriod, scene, out var tr, out conflict, out uniqueScene) &&
                !string.Equals(withoutPeriod, tr, StringComparison.Ordinal))
            {
                translated = tr;
                return true;
            }
            if (conflict)
                return false;
        }
        else if (target.Length > 1 && target[^1] == '…')
        {
            var withPeriod = target + "。";
            if (tryGet(withPeriod, scene, out var tr, out conflict, out uniqueScene) &&
                !string.Equals(withPeriod, tr, StringComparison.Ordinal))
            {
                translated = tr;
                return true;
            }
            if (conflict)
                return false;
        }

        // 2. Outer quote/bracket unwrapping ("「...」", "『...』", "（...）")
        if (target.Length > 2 &&
            ((target[0] == '「' && target[^1] == '」') ||
             (target[0] == '『' && target[^1] == '』') ||
             (target[0] == '（' && target[^1] == '）')))
        {
            var inner = target[1..^1];
            if (tryGet(inner, scene, out var innerTr, out conflict, out uniqueScene) &&
                !string.Equals(inner, innerTr, StringComparison.Ordinal))
            {
                translated = string.Concat(target.AsSpan(0, 1), innerTr, target.AsSpan(target.Length - 1, 1));
                return true;
            }
            if (conflict)
                return false;
        }

        // 3. Wave dash ("〜" <-> "～") normalization
        if (target.IndexOf('\u301c') >= 0 || target.IndexOf('\uff5e') >= 0)
        {
            var waveNormalized = target.IndexOf('\u301c') >= 0
                ? target.Replace('\u301c', '\uff5e')
                : target.Replace('\uff5e', '\u301c');
            if (tryGet(waveNormalized, scene, out var tr, out conflict, out uniqueScene) &&
                !string.Equals(waveNormalized, tr, StringComparison.Ordinal))
            {
                translated = tr;
                return true;
            }
        }

        return false;
    }

    public string ResolveStoryScenarioId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return ResolveStoryScenarioIdCore(GetLoadedScenarioSnapshot(), value);
    }

    private static string ResolveStoryScenarioIdCore(ScenarioSnapshot snapshot, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var candidate = value.Trim().Replace('\\', '/');
        if (snapshot.ScenarioPaths.ContainsKey(candidate))
            return candidate;

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var resolved = TryResolveScenarioSegment(snapshot, segments[i]);
            if (resolved.Length > 0)
                return resolved;
        }

        if (TryGetResourceCandidateScenes(snapshot, candidate, out var resourceCandidates) &&
            resourceCandidates.Count == 1)
        {
            return resourceCandidates[0];
        }

        return string.Empty;
    }

    private static bool TryGetResourceCandidateScenes(
        ScenarioSnapshot snapshot,
        string value,
        out List<string> candidates)
    {
        candidates = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = ResourceNumberPattern.Match(value);
        if (!match.Success)
            return false;

        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        var baseNum = match.Groups["base"].Value;
        if (snapshot.ResourceScenarioIds.TryGetValue($"{kind}_{baseNum}", out candidates) &&
            candidates.Count > 0)
        {
            return true;
        }

        return snapshot.ResourceScenarioIds.TryGetValue(baseNum, out candidates) &&
               candidates.Count > 0;
    }

    private static string TryResolveScenarioSegment(ScenarioSnapshot snapshot, string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return string.Empty;

        var leaf = segment.Trim();
        var sheetSeparator = leaf.IndexOf(':');
        if (sheetSeparator > 0)
        {
            var beforeSheet = TryResolveSingleLeaf(snapshot, leaf[..sheetSeparator]);
            if (beforeSheet.Length > 0)
                return beforeSheet;
            var afterSheet = TryResolveSingleLeaf(snapshot, leaf[(sheetSeparator + 1)..]);
            if (afterSheet.Length > 0)
                return afterSheet;
            return string.Empty;
        }

        return TryResolveSingleLeaf(snapshot, leaf);
    }

    private static string TryResolveSingleLeaf(ScenarioSnapshot snapshot, string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf))
            return string.Empty;

        leaf = leaf.Trim();
        var dot = leaf.LastIndexOf('.');
        if (dot > 0)
            leaf = leaf[..dot];
        if (leaf.StartsWith("*", StringComparison.Ordinal))
            leaf = leaf[1..];

        if (snapshot.ScenarioPaths.ContainsKey(leaf))
            return leaf;

        for (var underscore = leaf.LastIndexOf('_'); underscore > 0; underscore = leaf.LastIndexOf('_', underscore - 1))
        {
            var prefix = leaf[..underscore];
            if (snapshot.ScenarioPaths.ContainsKey(prefix))
                return prefix;
        }

        return string.Empty;
    }

    public bool IsKnownStoryTranslationValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return IsKnownStoryTranslationValueCore(GetLoadedScenarioSnapshot(), value);
    }

    private bool IsSingleKnownStoryValue(ScenarioSnapshot snapshot, string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (_knownTranslationValues.Contains(value) ||
            snapshot.KnownStoryTranslationValues.Contains(value) ||
            snapshot.KnownStoryTranslationValues.Contains(NormalizeStoryDisplayValue(value)))
            return true;
        var trimmed = value.Trim(' ', '\t', '\u3000', '\n');
        return trimmed.Length > 0 &&
               trimmed.Length != value.Length &&
               (_knownTranslationValues.Contains(trimmed) ||
                snapshot.KnownStoryTranslationValues.Contains(trimmed) ||
                snapshot.KnownStoryTranslationValues.Contains(NormalizeStoryDisplayValue(trimmed)));
    }

    private bool IsKnownStoryTranslationValueCore(ScenarioSnapshot snapshot, string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        if (IsSingleKnownStoryValue(snapshot, value))
            return true;

        var normalized = NormalizeSubSkillKey(value);
        if (!ReferenceEquals(normalized, value) && IsSingleKnownStoryValue(snapshot, normalized))
            return true;

        var clauses = SplitIntoStoryClauses(normalized);
        if (clauses.Count < 2)
            return false;

        var idx = 0;
        while (idx < clauses.Count)
        {
            var matched = false;
            var maxEnd = idx == 0 ? clauses.Count - 2 : clauses.Count - 1;
            for (var endIdx = maxEnd; endIdx >= idx; endIdx--)
            {
                var spanText = normalized.Substring(clauses[idx].Start, clauses[endIdx].End - clauses[idx].Start);
                if (IsSingleKnownStoryValue(snapshot, spanText))
                {
                    idx = endIdx + 1;
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return false;
        }

        return true;
    }

    public void ResetInferredStoryScenario()
    {
        StorySceneContext.ClearInferredPlayback();
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
            if (source.Contains(ExPlaceholder, StringComparison.Ordinal))
                _exTemplate = new ExTemplate(source, translation);
            else if (source.Contains(NumberPlaceholder, StringComparison.Ordinal))
                _numberTemplate = new NumberTemplate(source, translation);
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

    private sealed class ExTemplate
    {
        private readonly string[] _sourcePieces;
        private readonly string[] _translationPieces;
        private readonly string _requiredPrefix;
        private readonly string _requiredSuffix;
        private readonly string _translationPrefix;
        private readonly string _translationSuffix;
        private readonly bool _hasNumberPlaceholders;
        private Regex _sourcePattern;
        private Regex _translationPattern;

        public ExTemplate(string source, string translation)
        {
            Source = source;
            SourceLength = source.Length;
            _sourcePieces = source.Split(ExPlaceholder, StringSplitOptions.None);
            _translationPieces = translation.Split(ExPlaceholder, StringSplitOptions.None);
            WildcardCount = _sourcePieces.Length - 1;
            var numberCount = CountPlaceholder(source, NumberPlaceholder);
            _hasNumberPlaceholders = numberCount > 0 || translation.Contains(NumberPlaceholder, StringComparison.Ordinal);
            LiteralLength = source.Length - WildcardCount * ExPlaceholder.Length - numberCount * NumberPlaceholder.Length;
            _requiredPrefix = GetLiteralPrefix(_sourcePieces[0]);
            _requiredSuffix = GetLiteralSuffix(_sourcePieces[^1]);
            _translationPrefix = GetLiteralPrefix(_translationPieces[0]);
            _translationSuffix = GetLiteralSuffix(_translationPieces[^1]);
        }

        public string Source { get; }
        public int SourceLength { get; }
        public int LiteralLength { get; }
        public int WildcardCount { get; }

        private static string GetLiteralPrefix(string piece)
        {
            if (string.IsNullOrEmpty(piece))
                return string.Empty;
            var idx = piece.IndexOf(NumberPlaceholder, StringComparison.Ordinal);
            return idx < 0 ? piece : piece[..idx];
        }

        private static string GetLiteralSuffix(string piece)
        {
            if (string.IsNullOrEmpty(piece))
                return string.Empty;
            var idx = piece.LastIndexOf(NumberPlaceholder, StringComparison.Ordinal);
            return idx < 0 ? piece : piece[(idx + NumberPlaceholder.Length)..];
        }

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

            _sourcePattern ??= BuildPattern(_sourcePieces);
            var match = _sourcePattern.Match(source);
            if (!match.Success)
            {
                translated = source;
                return false;
            }

            var captures = match.Groups["excluded"].Captures;
            var numberCaptures = _hasNumberPlaceholders ? match.Groups["number"].Captures : null;
            var numberIndex = 0;
            var result = new StringBuilder(source.Length + 16);
            for (var index = 0; index < _translationPieces.Length; index++)
            {
                AppendTranslatedPiece(result, _translationPieces[index], numberCaptures, ref numberIndex);
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

        private static void AppendTranslatedPiece(
            StringBuilder result,
            string piece,
            CaptureCollection numberCaptures,
            ref int numberIndex)
        {
            if (numberCaptures == null || !piece.Contains(NumberPlaceholder, StringComparison.Ordinal))
            {
                result.Append(piece);
                return;
            }

            var subPieces = piece.Split(NumberPlaceholder, StringSplitOptions.None);
            result.Append(subPieces[0]);
            for (var i = 0; i < subPieces.Length - 1; i++)
            {
                result.Append(numberIndex < numberCaptures.Count ? numberCaptures[numberIndex].Value : NumberPlaceholder);
                numberIndex++;
                result.Append(subPieces[i + 1]);
            }
        }

        public bool MatchesTranslation(string value)
        {
            if ((!string.IsNullOrEmpty(_translationPrefix) &&
                 !value.StartsWith(_translationPrefix, StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(_translationSuffix) &&
                 !value.EndsWith(_translationSuffix, StringComparison.Ordinal)))
            {
                return false;
            }

            _translationPattern ??= BuildPattern(_translationPieces);
            return _translationPattern.IsMatch(value);
        }

        private static Regex BuildPattern(IReadOnlyList<string> pieces)
        {
            var pattern = new StringBuilder(@"\A");
            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                if (!piece.Contains(NumberPlaceholder, StringComparison.Ordinal))
                {
                    pattern.Append(Regex.Escape(piece));
                }
                else
                {
                    var subPieces = piece.Split(NumberPlaceholder, StringSplitOptions.None);
                    for (var subIndex = 0; subIndex < subPieces.Length; subIndex++)
                    {
                        pattern.Append(Regex.Escape(subPieces[subIndex]));
                        if (subIndex < subPieces.Length - 1)
                            pattern.Append(NumberTemplate.NumberPattern);
                    }
                }

                if (index < pieces.Count - 1)
                    pattern.Append("(?<excluded>.*?)");
            }
            pattern.Append(@"\z");
            return new Regex(
                pattern.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
    }

    private sealed class NumberTemplate
    {
        internal const string NumberPattern = @"(?<number>[+\-−＋]?(?:\d+(?:[.,．]\d+)?|\{\d+(?::[^}]*)?\}))";
        private readonly string _translation;
        private readonly string[] _sourcePieces;
        private readonly string[] _translationPieces;
        private readonly string _requiredPrefix;
        private readonly string _requiredSuffix;
        private readonly string _translationPrefix;
        private readonly string _translationSuffix;
        private Regex _sourcePattern;
        private Regex _translationPattern;

        public NumberTemplate(string source, string translation)
        {
            _translation = translation;
            SourceLength = source.Length;
            _sourcePieces = source.Split(NumberPlaceholder, StringSplitOptions.None);
            _translationPieces = translation.Split(NumberPlaceholder, StringSplitOptions.None);
            _requiredPrefix = _sourcePieces[0];
            _requiredSuffix = _sourcePieces[^1];
            _translationPrefix = _translationPieces[0];
            _translationSuffix = _translationPieces[^1];
        }

        public int SourceLength { get; }

        public bool TryTranslate(string source, out string translated)
        {
            if ((!string.IsNullOrEmpty(_requiredPrefix) &&
                 !source.StartsWith(_requiredPrefix, StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(_requiredSuffix) &&
                 !source.EndsWith(_requiredSuffix, StringComparison.Ordinal)))
            {
                translated = source;
                return false;
            }

            _sourcePattern ??= BuildPattern(_sourcePieces);
            var match = _sourcePattern.Match(source);
            if (!match.Success)
            {
                translated = source;
                return false;
            }

            var captures = match.Groups["number"].Captures;
            var count = Math.Min(captures.Count, _translationPieces.Length - 1);
            if (count == 0)
            {
                translated = _translation;
                return true;
            }
            var result = new StringBuilder(_translation.Length + 16);
            result.Append(_translationPieces[0]);
            for (var index = 0; index < _translationPieces.Length - 1; index++)
            {
                result.Append(index < count ? captures[index].Value : NumberPlaceholder);
                result.Append(_translationPieces[index + 1]);
            }
            translated = result.ToString();
            return true;
        }

        public bool MatchesTranslation(string value)
        {
            if ((!string.IsNullOrEmpty(_translationPrefix) &&
                 !value.StartsWith(_translationPrefix, StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(_translationSuffix) &&
                 !value.EndsWith(_translationSuffix, StringComparison.Ordinal)))
            {
                return false;
            }

            _translationPattern ??= BuildPattern(_translationPieces);
            return _translationPattern.IsMatch(value);
        }

        private static Regex BuildPattern(IReadOnlyList<string> pieces)
        {
            var pattern = new StringBuilder("^");
            for (var index = 0; index < pieces.Count; index++)
            {
                pattern.Append(Regex.Escape(pieces[index]));
                if (index < pieces.Count - 1)
                    pattern.Append(NumberPattern);
            }
            pattern.Append('$');
            return new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
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

        if (_knownTranslationValues.Contains(value))
            return true;

        var normalized = NormalizeSubSkillKey(value);
        if (!ReferenceEquals(normalized, value) && _knownTranslationValues.Contains(normalized))
            return true;

        var snapshot = Volatile.Read(ref _scenarioSnapshot);
        if (snapshot.KnownStoryTranslationValues.Contains(value) ||
            (!ReferenceEquals(normalized, value) && snapshot.KnownStoryTranslationValues.Contains(normalized)))
            return true;

        return false;
    }

    private bool IsRenderedTemplateTranslation(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return MatchesNumberTemplateTranslation(value, _uiNumberTemplates) ||
               MatchesNumberTemplateTranslation(value, _subSkillNumberTemplates) ||
               MatchesExTemplateTranslation(value, _uiExTemplates) ||
               MatchesExTemplateTranslation(value, _subSkillExTemplates) ||
               MatchesExTemplateTranslation(value, _nameExTemplates) ||
               MatchesSceneTemplateTranslation(value, _uiSceneTemplates) ||
               MatchesSceneTemplateTranslation(value, _subSkillSceneTemplates) ||
               MatchesSceneTemplateTranslation(value, _nameSceneTemplates);
    }

    private bool HasRuntimeTranslation(string source)
    {
        var snapshot = GetLoadedScenarioSnapshot();
        if (string.IsNullOrEmpty(source) || IsKnownTranslationValue(source) || IsRenderedTemplateTranslation(source))
            return !string.IsNullOrEmpty(source);

        if ((_names.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value)) ||
            (_uiTexts.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)) ||
            (_subSkills.TryGetValue(NormalizeSubSkillKey(source), out value) && !string.IsNullOrEmpty(value)) ||
            (snapshot.Fallback.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)))
            return true;

        if (TryTranslateNumberTemplate(source, _uiNumberTemplates, out _) ||
            TryTranslateNumberTemplate(source, _subSkillNumberTemplates, out _) ||
            TryTranslateExTemplate(source, _nameExTemplates, out _) ||
            TryTranslateExTemplate(source, _uiExTemplates, out _) ||
            TryTranslateExTemplate(source, _subSkillExTemplates, out _))
            return true;

        var normalized = NormalizeSubSkillKey(source);
        if (!ReferenceEquals(normalized, source) &&
            !string.Equals(normalized, source, StringComparison.Ordinal) &&
            ((_uiTexts.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
             (_subSkills.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
             TryTranslateNumberTemplate(normalized, _uiNumberTemplates, out _) ||
             TryTranslateNumberTemplate(normalized, _subSkillNumberTemplates, out _) ||
             TryTranslateExTemplate(normalized, _uiExTemplates, out _) ||
             TryTranslateExTemplate(normalized, _subSkillExTemplates, out _)))
            return true;

        if (TryTranslateIndexedFragments(normalized, _subSkillFragmentIndex, out _))
            return true;

        EnsureStoryFragmentIndex(snapshot);
        return TryTranslateIndexedFragments(source, snapshot.StoryFragmentIndex, out _);
    }

    public bool HasRuntimeTranslation(string source, string scenePath) =>
        HasRuntimeTranslation(source) ||
        TryTranslateUiText(source, scenePath, out _);

    private bool HasStrictRuntimeTranslation(string source, string scenePath)
    {
        var snapshot = GetLoadedScenarioSnapshot();
        if (string.IsNullOrEmpty(source) || IsKnownTranslationValue(source) || IsRenderedTemplateTranslation(source))
            return !string.IsNullOrEmpty(source);

        var normalized = NormalizeSubSkillKey(source);
        if ((_names.TryGetValue(source, out var value) && !string.IsNullOrEmpty(value)) ||
            (_uiTexts.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)) ||
            (_subSkills.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
            (snapshot.Fallback.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)))
            return true;

        if (TryTranslateUiPath(source, scenePath, out _) ||
            TryTranslateSceneTemplate(source, scenePath, _uiSceneTemplates, out _) ||
            TryTranslateNumberTemplate(source, _uiNumberTemplates, out _) ||
            TryTranslateNumberTemplate(normalized, _subSkillNumberTemplates, out _) ||
            TryTranslateExTemplate(source, _nameExTemplates, out _) ||
            TryTranslateExTemplate(source, _uiExTemplates, out _) ||
            TryTranslateExTemplate(normalized, _subSkillExTemplates, out _))
            return true;

        if (ReferenceEquals(normalized, source) ||
            string.Equals(normalized, source, StringComparison.Ordinal))
            return false;

        return (_uiTexts.TryGetValue(normalized, out value) && !string.IsNullOrEmpty(value)) ||
               IsRenderedTemplateTranslation(normalized) ||
               TryTranslateUiPath(normalized, scenePath, out _) ||
               TryTranslateSceneTemplate(normalized, scenePath, _uiSceneTemplates, out _) ||
               TryTranslateNumberTemplate(normalized, _uiNumberTemplates, out _) ||
               TryTranslateExTemplate(normalized, _uiExTemplates, out _);
    }

    private static void AddNumberTemplate(List<NumberTemplate> templates, string source, string translated)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated) ||
            !source.Contains(NumberPlaceholder, StringComparison.Ordinal) ||
            source.Contains(ExPlaceholder, StringComparison.Ordinal))
            return;

        templates.Add(new NumberTemplate(source, translated));
    }

    private static void SortNumberTemplates(List<NumberTemplate> templates)
    {
        if (templates.Count > 1)
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

    private static bool MatchesNumberTemplateTranslation(
        string value,
        IEnumerable<NumberTemplate> templates)
    {
        foreach (var template in templates)
            if (template.MatchesTranslation(value))
                return true;
        return false;
    }

    private static bool MatchesExTemplateTranslation(
        string value,
        IEnumerable<ExTemplate> templates)
    {
        foreach (var template in templates)
            if (template.MatchesTranslation(value))
                return true;
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
    }

    private static void SortExTemplates(List<ExTemplate> templates)
    {
        if (templates.Count <= 1)
            return;

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

    private static bool ContainsDigitOrFormatPlaceholder(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((c >= '0' && c <= '9') || (c >= '０' && c <= '９') || c == '{')
                return true;
        }
        return false;
    }

    private static bool TryTranslateNumberTemplate(
        string source,
        IEnumerable<NumberTemplate> templates,
        out string translated)
    {
        if (!ContainsDigitOrFormatPlaceholder(source))
        {
            translated = source;
            return false;
        }

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

    private bool TryTranslateCapturedUiText(string source, out string translated)
    {
        if (_uiTexts.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated))
            return true;
        if (_names.TryGetValue(source, out translated) && !string.IsNullOrEmpty(translated))
            return true;
        var normalized = NormalizeSubSkillKey(source);
        if (_subSkills.TryGetValue(normalized, out translated) && !string.IsNullOrEmpty(translated))
            return true;
        if (TryTranslateNumberTemplate(source, _uiNumberTemplates, out translated))
            return true;
        if (TryTranslateIndexedFragments(normalized, _subSkillFragmentIndex, out translated))
            return true;

        translated = source;
        return false;
    }

    private bool TryTranslateCapturedSubSkill(string source, out string translated) =>
        _subSkills.TryGetValue(NormalizeSubSkillKey(source), out translated) &&
        !string.IsNullOrEmpty(translated);

    private static void AddFragmentRule(
        Dictionary<char, List<KeyValuePair<string, string>>> index,
        string source,
        string translated,
        int minLength)
    {
        if (source.Length < minLength || string.IsNullOrEmpty(translated) ||
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
        if (string.IsNullOrEmpty(source) || index.Count == 0)
            return false;

        StringBuilder builder = null;
        var lastCopiedIndex = 0;
        var sourceSpan = source.AsSpan();

        for (var characterIndex = 0; characterIndex < source.Length; characterIndex++)
        {
            if (!index.TryGetValue(source[characterIndex], out var candidates))
                continue;

            var remaining = sourceSpan[characterIndex..];
            foreach (var candidate in candidates)
            {
                if (!remaining.StartsWith(candidate.Key.AsSpan(), StringComparison.Ordinal))
                    continue;

                builder ??= new StringBuilder(source.Length + 16);
                if (characterIndex > lastCopiedIndex)
                    builder.Append(source, lastCopiedIndex, characterIndex - lastCopiedIndex);
                builder.Append(candidate.Value);

                characterIndex += candidate.Key.Length - 1;
                lastCopiedIndex = characterIndex + 1;
                break;
            }
        }

        if (builder == null)
            return false;

        if (lastCopiedIndex < source.Length)
            builder.Append(source, lastCopiedIndex, source.Length - lastCopiedIndex);

        translated = builder.ToString();
        return true;
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

    private static string NormalizeSubSkillKey(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            (value.IndexOf('\r') < 0 && value.IndexOf('\\') < 0))
            return value;

        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal)
                    .Replace("\\r\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private void AddKnownTranslationValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        _knownTranslationValues.Add(value);
        var normalized = NormalizeSubSkillKey(value);
        if (!ReferenceEquals(normalized, value))
            _knownTranslationValues.Add(normalized);
    }

    private static void AddKnownStoryTranslationValue(HashSet<string> targetSet, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        targetSet.Add(value);
        var displayValue = NormalizeStoryDisplayValue(value);
        if (!ReferenceEquals(displayValue, value))
            targetSet.Add(displayValue);
    }

    private static bool HasUtageDisplayControlTag(string value) =>
        value.IndexOf('<') >= 0 &&
        (value.Contains("interval", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("speed", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("sound", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeStoryDisplayValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var normalized = NormalizeSubSkillKey(value);
        return !HasUtageDisplayControlTag(normalized)
            ? normalized
            : UtageDisplayControlTag.Replace(normalized, string.Empty);
    }

    private static Dictionary<string, string> ReadTable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var val = property.Value.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(val) || !table.ContainsKey(property.Name))
                        table[property.Name] = val;
                }
                else if ((property.NameEquals("$global") || property.NameEquals("$globe")) &&
                         property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var row in property.Value.EnumerateObject())
                    {
                        if (row.Value.ValueKind == JsonValueKind.String)
                        {
                            var val = row.Value.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(val) || !table.ContainsKey(row.Name))
                                table[row.Name] = val;
                            continue;
                        }

                        if (row.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var child in row.Value.EnumerateObject())
                            {
                                if (child.Value.ValueKind == JsonValueKind.String)
                                {
                                    var val = child.Value.GetString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(val) || !table.ContainsKey(child.Name))
                                        table[child.Name] = val;
                                }
                            }
                        }
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

    private static void ReadUiFile(
        string path,
        out Dictionary<string, string> globalTable,
        out List<UiPathTable> pathTables)
    {
        globalTable = new Dictionary<string, string>(StringComparer.Ordinal);
        pathTables = new List<UiPathTable>();
        try
        {
            if (!File.Exists(path))
                return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var val = property.Value.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(val) || !globalTable.ContainsKey(property.Name))
                        globalTable[property.Name] = val;
                }
                else if (property.NameEquals("$paths") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pathProperty in property.Value.EnumerateObject())
                        AddUiPathTable(pathTables, pathProperty);
                }
                else if ((property.NameEquals("$global") || property.NameEquals("$globe")) &&
                         property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var row in property.Value.EnumerateObject())
                    {
                        if (row.Value.ValueKind == JsonValueKind.String)
                        {
                            var val = row.Value.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(val) || !globalTable.ContainsKey(row.Name))
                                globalTable[row.Name] = val;
                            continue;
                        }
                        if (row.Value.ValueKind == JsonValueKind.Object)
                        {
                            AddUiPathTable(pathTables, row);
                        }
                    }
                }
                else if (!property.Name.StartsWith('$') && property.Value.ValueKind == JsonValueKind.Object)
                {
                    AddUiPathTable(pathTables, property);
                }
            }
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"读取 UI 翻译表失败 {path}: {ex.Message}");
        }
    }

    private static void AddUiPathTable(
        ICollection<UiPathTable> result,
        JsonProperty pathProperty)
    {
        if (pathProperty.Value.ValueKind != JsonValueKind.Object)
            return;
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in pathProperty.Value.EnumerateObject())
        {
            if (row.Value.ValueKind == JsonValueKind.String)
            {
                var val = row.Value.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(val) || !table.ContainsKey(row.Name))
                    table[row.Name] = val;
            }
        }
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
            SortNumberTemplates(NumberTemplates);
            SortExTemplates(ExTemplates);
        }

        public string Path { get; }
        public Dictionary<string, string> Exact { get; }
        public List<NumberTemplate> NumberTemplates { get; }
        public List<ExTemplate> ExTemplates { get; }
    }
}
