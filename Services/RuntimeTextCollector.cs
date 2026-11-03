using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if ANDROID
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace MonsterMusumeTDMod.Services;

/// <summary>
/// Captures sub-skill names/descriptions from the complete runtime catalog
/// and appends untranslated UI strings on F8 without overwriting existing JSON structures.
/// </summary>
public sealed class RuntimeTextCollector
{
    private static readonly string[] UiControlMarkers =
    {
        "button", "btn", "toggle", "tab", "scrollbar"
    };

    private static readonly string[] UiOnlyPathMarkers =
    {
        "categorybutton", "removebutton", "legendimage/rarity", "cooltime", "firstcooltime",
        "lockimage", "posstext", "/windowbase/title/", "/content/title/"
    };

    private static readonly string[] DescriptionMarkers =
    {
        "description", "effect", "explanation", "summary", "info"
    };

    private static readonly string[] NameMarkers =
    {
        "name", "title"
    };

    private static readonly HashSet<string> UiLabels = new(StringComparer.Ordinal)
    {
        "EXスキル", "スキル", "スキル詳細", "副スキル", "サブスキル", "副スキル詳細",
        "詳細", "閉じる", "戻る", "決定", "キャンセル", "装備", "装備する", "外す",
        "強化", "合成", "変更", "解除", "選択", "全て", "フィルター", "レベル",
        "スキルレベル", "サブスキル名", "所持数", "効果", "なし", "あり", "攻撃力", "物理防御",
        "魔法防御", "射程", "攻撃速度", "出撃コスト", "ブロック数", "移動速度"
    };

    private static readonly Regex RichTextTag = new("<[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex NumberOrRichTextToken = new(@"<[^>]*>|\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);

    private readonly string _snapshotPath;
    private readonly string _missingPath;
    private readonly string _subSkillScanPath;
    private readonly string _missingIndexPath;
    private readonly object _lock = new();
    private readonly HashSet<string> _subSkillScanObserved = new(StringComparer.Ordinal);
    private bool _subSkillScanActive;

    public RuntimeTextCollector(string translationsRoot)
    {
        _snapshotPath = Path.Combine(translationsRoot, "ui_snapshot.tsv");
        _missingPath = Path.Combine(translationsRoot, "missing.json");
        _subSkillScanPath = Path.Combine(translationsRoot, "subskill_scan.tsv");
        _missingIndexPath = Path.Combine(translationsRoot, "missing_index.json");
    }

    public void Observe(object instance, string source)
    {
        if (instance is not Component component || !ContainsJapanese(source))
            return;

        var hierarchy = UiHierarchyClassifier.GetHierarchyPath(component, 12);
        if (!UiHierarchyClassifier.IsCompleteSubSkillHierarchy(hierarchy) ||
            !LooksLikeSkillNameOrDescription(hierarchy, source))
            return;

        var line = $"subskill\t{hierarchy}\t{source.Replace("\r", "").Replace("\n", "\\n")}";
        lock (_lock)
        {
            try
            {
                if (_subSkillScanActive && _subSkillScanObserved.Add(line))
                    File.AppendAllText(_subSkillScanPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Core.Plugin.Log?.LogWarning($"写入副技能文案日志失败: {ex.Message}");
            }
        }
    }

    public void BeginSubSkillScan()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_subSkillScanPath)!);
                File.WriteAllText(_subSkillScanPath, "category\tui_hierarchy\ttext" + Environment.NewLine);
                _subSkillScanObserved.Clear();

                // Always begin with an empty file. Older observed logs may
                // contain the rendered Chinese value from a previous build;
                // this scan instead records only current binding arguments.
                _subSkillScanActive = true;
            }
            catch (Exception ex)
            {
                _subSkillScanActive = false;
                Core.Plugin.Log?.LogWarning($"初始化副技能扫描文件失败: {ex.Message}");
            }
        }
    }

    public int EndSubSkillScan()
    {
        lock (_lock)
        {
            _subSkillScanActive = false;
            return _subSkillScanObserved.Count;
        }
    }

    public bool IsSubSkillScanActive
    {
        get
        {
            lock (_lock)
                return _subSkillScanActive;
        }
    }

    public void CaptureVisibleSubSkillScanText()
    {
        if (!IsSubSkillScanActive)
            return;

        // Some virtualised cards update their visible renderer without
        // calling a string setter. Preserve the original F9 fallback so those
        // entries remain discoverable; the offline importer filters known
        // translated values when Chinese is currently displayed.
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text != null && text.gameObject != null && text.gameObject.activeInHierarchy &&
                UiHierarchyClassifier.IsCompleteSubSkillHierarchy(UiHierarchyClassifier.GetHierarchyPath(text, 12)))
                Observe(text, text.text);
        }

        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (text != null && text.gameObject != null && text.gameObject.activeInHierarchy &&
                UiHierarchyClassifier.IsCompleteSubSkillHierarchy(UiHierarchyClassifier.GetHierarchyPath(text, 12)))
                Observe(text, text.text);
        }
    }

    /// <summary>
    /// Writes every active Japanese UI string for diagnosis and appends rows
    /// not covered by a loaded translation rule to missing.json or indexed files.
    /// </summary>
    public void CaptureActiveUiSnapshot(Func<string, bool> hasTranslation)
    {
        CaptureActiveUiSnapshot(
            (source, _) => hasTranslation == null || hasTranslation(source), null);
    }

    public void CaptureActiveUiSnapshot(Func<string, string, bool> hasTranslation)
        => CaptureActiveUiSnapshot(hasTranslation, null);

    public void CaptureActiveUiSnapshot(
        Func<string, string, bool> hasTranslation,
        Func<string, string> resolveCategory)
    {
        // Preserves insertion order of discovered categories and strings.
        var missingCandidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var routedCandidates = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        var missingIndexRules = LoadMissingIndexRules();

        try
        {
            var lines = new List<string>
            {
                "renderer\tui_path\tfont_name\tmaterial_name\ttext\tfallback_material\t" +
                "fallback_source_material\trendering_material\tmaterial_index\t" +
                "outline_width\tface_dilate\toutline_on\tsibling_index\tcanvas_name\t" +
                "canvas_sorting_order\tresource_name\tenabled\traycast_target\t" +
                "vertex_gradient_enabled\tgradient_top_left\tgradient_top_right\t" +
                "gradient_bottom_left\tgradient_bottom_right\tgradient_preset\t" +
                "first_visible_vertex_colors\tselectable_type\tselectable_interactable\t" +
                "selectable_effective_interactable\tselectable_transition\tdisabled_color\t" +
                "target_graphic_path\ttarget_graphic_color\ttarget_renderer_color\t" +
                "text_color\ttext_renderer_color\tmaterial_face_color\toriginal_face_color"
            };
            var observed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                AddSnapshotLine(
                    lines,
                    observed,
                    missingCandidates,
                    routedCandidates,
                    missingIndexRules,
                    hasTranslation,
                    resolveCategory,
                    "tmp",
                    text,
                    text?.text);
            foreach (var subMesh in Resources.FindObjectsOfTypeAll<TMP_SubMeshUI>())
                AddSubMeshSnapshotLine(lines, observed, subMesh);
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
                AddSnapshotLine(
                    lines,
                    observed,
                    missingCandidates,
                    routedCandidates,
                    missingIndexRules,
                    hasTranslation,
                    resolveCategory,
                    "legacy",
                    text,
                    text?.text);
            AddStoryRenderDiagnostics(lines, observed);

            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            File.WriteAllLines(_snapshotPath, lines);
            Core.Plugin.Log?.LogInfo($"UI diagnostic snapshot: {lines.Count - 1} strings -> {_snapshotPath}");
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"写入 UI 诊断快照失败: {ex.Message}");
            return;
        }

        lock (_lock)
        {
            try
            {
                UpdateMissingFile(missingCandidates);
                UpdateRoutedFiles(routedCandidates);
            }
            catch (Exception ex)
            {
                Core.Plugin.Log?.LogWarning($"更新未翻译文本失败: {ex.Message}");
            }
        }
    }

    private static bool ContainsJapanese(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var character in text)
            if ((character >= '\u3040' && character <= '\u30ff') ||
                (character >= '\u3400' && character <= '\u9fff'))
                return true;
        return false;
    }

    private void AddSnapshotLine(
        ICollection<string> lines,
        ISet<string> observed,
        IDictionary<string, List<string>> missingCandidates,
        IDictionary<string, Dictionary<string, List<string>>> routedCandidates,
        IReadOnlyList<KeyValuePair<string, string>> missingIndexRules,
        Func<string, string, bool> hasTranslation,
        Func<string, string> resolveCategory,
        string renderer,
        Component component,
        string source)
    {
        if (component == null || component.gameObject == null ||
            !component.gameObject.activeInHierarchy ||
            UiHierarchyClassifier.IsUiTextSoftOutlineLayer(component) ||
            !ContainsJapanese(source))
            return;

        var path = UiHierarchyClassifier.GetHierarchyPath(component, 64);
        var (fontName, materialName) = GetFontMetadata(component);
        var material = component is TMP_Text tmp ? tmp.fontSharedMaterial : null;
        var gradientDiagnostics = component is TMP_Text tmpText
            ? GetGradientDiagnostics(tmpText)
            : "\t\t\t\t\t\t";
        var line = $"{renderer}\t{path}\t{EscapeForTsv(fontName)}\t" +
                   $"{EscapeForTsv(materialName)}\t{EscapeForTsv(source)}\t\t\t\t\t" +
                   GetMaterialDiagnostics(material) + "\t\t\t\t\t\t\t" + gradientDiagnostics +
                   "\t" + GetSelectableDiagnostics(component) + "\t" + GetFaceDiagnostics(component);
        if (observed.Add(line))
            lines.Add(line);

        var normalizedSource = NormalizeNumbers(NormalizeNewlines(source));
        // Skip components that are already displaying a translated value on screen
        // or whose raw/numeric-template form is covered by loaded translation rules.
        var alreadyTranslated =
            TmpFontInstaller.IsCurrentlyTranslatedComponent(component, source) ||
            (hasTranslation != null &&
             (hasTranslation(source, path) || hasTranslation(normalizedSource, path)));
        if (alreadyTranslated)
            return;

        var category = resolveCategory?.Invoke(path) ?? CreateStableUiCategory(path);
        var routedFile = ResolveIndexedFile(path, missingIndexRules);
        if (routedFile != null)
        {
            if (!routedCandidates.TryGetValue(routedFile, out var byCategory))
            {
                byCategory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                routedCandidates[routedFile] = byCategory;
            }
            AddCandidate(byCategory, category, normalizedSource);
        }
        else
        {
            AddCandidate(missingCandidates, category, normalizedSource);
        }
    }

    private static void AddCandidate(
        IDictionary<string, List<string>> target,
        string category,
        string source)
    {
        var key = string.IsNullOrEmpty(category) ? "$global" : category;
        if (!target.TryGetValue(key, out var list))
        {
            list = new List<string>();
            target[key] = list;
        }
        if (!list.Contains(source))
            list.Add(source);
    }

    private void UpdateRoutedFiles(IReadOnlyDictionary<string, Dictionary<string, List<string>>> candidates)
    {
        foreach (var group in candidates)
        {
            var relative = group.Key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var rootName = new DirectoryInfo(Path.GetDirectoryName(_missingPath)!).Name;
            var rootPrefix = rootName + Path.DirectorySeparatorChar;
            if (relative.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                relative = relative[rootPrefix.Length..];
            var translationsPrefix = "translations" + Path.DirectorySeparatorChar;
            if (relative.StartsWith(translationsPrefix, StringComparison.OrdinalIgnoreCase))
                relative = relative[translationsPrefix.Length..];
            relative = relative.TrimStart(Path.DirectorySeparatorChar);
            if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                relative = Path.Combine(relative, "zh_Hans.json");
            var path = Path.Combine(Path.GetDirectoryName(_missingPath)!, relative);

            AppendCandidatesToJsonFile(path, group.Value, isMissingFile: false);
        }
    }

    private void UpdateMissingFile(IReadOnlyDictionary<string, List<string>> candidates)
    {
        AppendCandidatesToJsonFile(_missingPath, candidates, isMissingFile: true);
    }

    /// <summary>
    /// Non-destructively parses an existing translation/missing JSON file (supporting
    /// "$global", "$globe", "$paths", nested path objects, and flat key-value tables)
    /// and appends only new untranslated entries at the very end of the file.
    /// </summary>
    private static void AppendCandidatesToJsonFile(
        string filePath,
        IReadOnlyDictionary<string, List<string>> candidatesByCategory,
        bool isMissingFile)
    {
        JsonObject root = null;
        JsonObject originalRoot = null;
        var hadExistingFile = File.Exists(filePath);
        if (File.Exists(filePath))
        {
            try
            {
                var existingText = File.ReadAllText(filePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(existingText))
                {
                    var parsed = JsonNode.Parse(
                        existingText,
                        nodeOptions: null,
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        });
                    root = parsed as JsonObject;
                    if (root == null)
                    {
                        Core.Plugin.Log?.LogWarning(
                            $"读取现有 JSON 失败（根节点不是对象），已停止更新以避免覆盖: {filePath}");
                        return;
                    }

                    // Keep an untouched copy of the parsed document.  F8 is an
                    // append-only operation; this lets us verify below that a
                    // merge never replaced an existing value or path table.
                    originalRoot = JsonNode.Parse(
                        existingText,
                        nodeOptions: null,
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        }) as JsonObject;
                }
            }
            catch (Exception ex)
            {
                Core.Plugin.Log?.LogWarning(
                    $"读取现有 JSON 失败，已停止更新以避免覆盖 ({filePath}): {ex.Message}");
                return;
            }
        }

        var isNewFile = root == null || root.Count == 0;
        if (isNewFile)
        {
            root = new JsonObject
            {
                ["$global"] = new JsonObject()
            };
        }

        var globalObj = (root["$global"] as JsonObject) ?? (root["$globe"] as JsonObject);
        var pathsObj = root["$paths"] as JsonObject;

        // Index all existing keys and path tables so existing translations or placeholders are never overwritten.
        var existingGlobalKeys = new HashSet<string>(StringComparer.Ordinal);
        var existingAllKeys = new HashSet<string>(StringComparer.Ordinal);
        var existingPathObjects = new Dictionary<string, (JsonObject Parent, string PropertyName, JsonObject Table)>(
            StringComparer.OrdinalIgnoreCase);

        var hasAnyNestedPathObject = false;

        void IndexContainer(JsonObject container, bool isDedicatedPathsContainer)
        {
            if (container == null)
                return;

            foreach (var pair in container)
            {
                if (pair.Value is JsonValue val && val.TryGetValue<string>(out _))
                {
                    if (!isDedicatedPathsContainer)
                        existingGlobalKeys.Add(pair.Key);
                    existingAllKeys.Add(pair.Key);
                }
                else if (pair.Value is JsonObject childObj &&
                         !string.Equals(pair.Key, "$global", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(pair.Key, "$globe", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(pair.Key, "$paths", StringComparison.OrdinalIgnoreCase))
                {
                    hasAnyNestedPathObject = true;
                    var normalizedKey = UiStylePolicy.NormalizePath(pair.Key);
                    if (!string.IsNullOrEmpty(normalizedKey))
                        existingPathObjects[normalizedKey] = (container, pair.Key, childObj);
                    existingPathObjects[pair.Key] = (container, pair.Key, childObj);

                    foreach (var childPair in childObj)
                    {
                        if (childPair.Value is JsonValue)
                            existingAllKeys.Add(childPair.Key);
                    }
                }
            }
        }

        IndexContainer(root, isDedicatedPathsContainer: false);
        IndexContainer(globalObj, isDedicatedPathsContainer: false);
        IndexContainer(pathsObj, isDedicatedPathsContainer: true);

        var flatContainer = globalObj ?? root;
        var pathContainer = pathsObj ?? globalObj ?? root;
        // Use path-object grouping for missing.json, new files, or any file that uses $global/$paths/nested path objects (like zh_Hans.json).
        var usePathGrouping = isMissingFile || isNewFile || globalObj != null || pathsObj != null || hasAnyNestedPathObject;

        var added = 0;
        foreach (var group in candidatesByCategory)
        {
            var category = string.IsNullOrEmpty(group.Key) ? "$global" : group.Key;
            if (!usePathGrouping || string.Equals(category, "$global", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var source in group.Value)
                {
                    if (string.IsNullOrEmpty(source) ||
                        existingAllKeys.Contains(source) ||
                        flatContainer.ContainsKey(source))
                        continue;

                    flatContainer[source] = string.Empty;
                    existingGlobalKeys.Add(source);
                    existingAllKeys.Add(source);
                    added++;
                }
                continue;
            }

            var normalizedCategory = UiStylePolicy.NormalizePath(category);
            if (existingPathObjects.TryGetValue(category, out var existingPathEntry) ||
                (!string.IsNullOrEmpty(normalizedCategory) &&
                 existingPathObjects.TryGetValue(normalizedCategory, out existingPathEntry)))
            {
                var addedToExistingPath = 0;
                foreach (var source in group.Value)
                {
                    if (string.IsNullOrEmpty(source) ||
                        existingGlobalKeys.Contains(source) ||
                        existingPathEntry.Table.ContainsKey(source))
                        continue;

                    existingPathEntry.Table[source] = string.Empty;
                    existingAllKeys.Add(source);
                    added++;
                    addedToExistingPath++;
                }

                // Move the updated path object to the very end of its parent container
                // so newly appended entries always appear at the bottom of the file.
                if (addedToExistingPath > 0)
                {
                    existingPathEntry.Parent.Remove(existingPathEntry.PropertyName);
                    existingPathEntry.Parent[existingPathEntry.PropertyName] = existingPathEntry.Table;
                }
            }
            else
            {
                // Never replace an existing scalar/object with a new path
                // table.  A path collision can otherwise make a valid table
                // disappear when the JsonObject indexer is assigned to.
                if (pathContainer.ContainsKey(category) &&
                    pathContainer[category] is not JsonObject)
                {
                    Core.Plugin.Log?.LogWarning(
                        $"F8 skipped path collision to preserve existing JSON: {filePath} ({category})");
                    continue;
                }

                JsonObject newPathTable = null;
                foreach (var source in group.Value)
                {
                    if (string.IsNullOrEmpty(source) || existingAllKeys.Contains(source))
                        continue;

                    newPathTable ??= new JsonObject();
                    if (newPathTable.ContainsKey(source))
                        continue;

                    newPathTable[source] = string.Empty;
                    existingAllKeys.Add(source);
                    added++;
                }

                if (newPathTable != null && newPathTable.Count > 0)
                {
                    pathContainer[category] = newPathTable;
                    existingPathObjects[category] = (pathContainer, category, newPathTable);
                    if (!string.IsNullOrEmpty(normalizedCategory))
                        existingPathObjects[normalizedCategory] = (pathContainer, category, newPathTable);
                }
            }
        }

        if (added == 0)
        {
            if (isMissingFile)
                Core.Plugin.Log?.LogInfo(
                    $"Missing UI translations: added=0, existing={existingAllKeys.Count} -> {filePath}");
            else
                Core.Plugin.Log?.LogInfo(
                    $"F8 indexed missing: added=0, existing={existingAllKeys.Count}, file={filePath}");
            return;
        }

        // Defensive invariant: if the file already contained an object, every
        // original property (including nested path tables and non-string JSON
        // values) must still be present with the same value before we replace
        // it on disk.  This turns any future merge regression into a safe
        // no-op instead of silently clearing translations.
        if (hadExistingFile && originalRoot != null &&
            !PreservesExistingJson(originalRoot, root))
        {
            Core.Plugin.Log?.LogError(
                $"F8 merge rejected because existing JSON would be changed: {filePath}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporaryPath = filePath + ".tmp";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        try
        {
            File.WriteAllText(
                temporaryPath,
                root.ToJsonString(options) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        if (isMissingFile)
            Core.Plugin.Log?.LogInfo(
                $"Missing UI translations: added={added}, total={existingAllKeys.Count} -> {filePath}");
        else
            Core.Plugin.Log?.LogInfo(
                $"F8 indexed missing: added={added}, total={existingAllKeys.Count}, file={filePath}");
    }

    private static bool PreservesExistingJson(JsonObject original, JsonObject merged)
    {
        foreach (var pair in original)
        {
            if (!merged.TryGetPropertyValue(pair.Key, out var mergedValue) ||
                !PreservesExistingNode(pair.Value, mergedValue))
                return false;
        }

        return true;
    }

    private static bool PreservesExistingNode(JsonNode original, JsonNode merged)
    {
        if (original is JsonObject originalObject && merged is JsonObject mergedObject)
        {
            foreach (var pair in originalObject)
            {
                if (!mergedObject.TryGetPropertyValue(pair.Key, out var mergedValue) ||
                    !PreservesExistingNode(pair.Value, mergedValue))
                    return false;
            }
            return true;
        }

        if (original is JsonArray originalArray && merged is JsonArray mergedArray)
        {
            if (originalArray.Count != mergedArray.Count)
                return false;
            for (var index = 0; index < originalArray.Count; index++)
                if (!PreservesExistingNode(originalArray[index], mergedArray[index]))
                    return false;
            return true;
        }

        if (original == null || merged == null)
            return original == merged;

        return string.Equals(
            original.ToJsonString(),
            merged.ToJsonString(),
            StringComparison.Ordinal);
    }

    private IReadOnlyList<KeyValuePair<string, string>> LoadMissingIndexRules()
    {
        var indexPath = File.Exists(_missingIndexPath)
            ? _missingIndexPath
            : Path.Combine(Path.GetDirectoryName(_missingPath)!, "..", "missing_index.json");
        if (!File.Exists(indexPath))
            return Array.Empty<KeyValuePair<string, string>>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexPath, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<KeyValuePair<string, string>>();

            var rules = new List<KeyValuePair<string, string>>();
            foreach (var row in document.RootElement.EnumerateObject())
            {
                if (row.Value.ValueKind != JsonValueKind.String)
                    continue;
                var key = row.Name.Replace('\\', '/').Trim('/');
                var target = row.Value.GetString();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(target))
                    rules.Add(new KeyValuePair<string, string>(key, target));
            }

            rules.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
            return rules;
        }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"读取 missing_index.json 失败 ({indexPath}): {ex.Message}");
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    private static string ResolveIndexedFile(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> missingIndexRules)
    {
        if (missingIndexRules == null || missingIndexRules.Count == 0 || string.IsNullOrEmpty(path))
            return null;

        var normalizedPath = UiStylePolicy.NormalizePath(path);
        foreach (var rule in missingIndexRules)
        {
            var key = rule.Key;
            if (normalizedPath.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Contains("/" + key + "/", StringComparison.OrdinalIgnoreCase))
            {
                Core.Plugin.Log?.LogInfo($"F8 index matched: {normalizedPath} -> {rule.Value}");
                return rule.Value;
            }
        }

        return null;
    }

    private static string NormalizeNumbers(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Preserve all rich-text tags verbatim. Only numeric text outside
        // tags becomes the reusable <num> placeholder; otherwise values such
        // as <cspace=-2> would be corrupted into <cspace=-<num>>.
        return NumberOrRichTextToken.Replace(value, match =>
            match.Value.Length > 0 && match.Value[0] == '<'
                ? match.Value
                : "<num>");
    }

    public static string CreateStableUiCategory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Replace("(Clone)", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (parts[i].StartsWith("TMP UI SubObject [", StringComparison.OrdinalIgnoreCase))
                parts[i] = "TMP UI SubObject";
        }
        var normalized = string.Join('/', parts);
        var canvasIndex = normalized.IndexOf("UICanvas", StringComparison.OrdinalIgnoreCase);
        if (canvasIndex >= 0)
        {
            var slash = normalized.IndexOf('/', canvasIndex);
            if (slash >= 0 && slash + 1 < normalized.Length)
                normalized = normalized[(slash + 1)..];
        }
        return normalized;
    }

    private static void AddSubMeshSnapshotLine(
        ICollection<string> lines,
        ISet<string> observed,
        TMP_SubMeshUI subMesh)
    {
        if (subMesh == null || subMesh.gameObject == null || !subMesh.gameObject.activeInHierarchy)
            return;

        var parentText = subMesh.GetComponentInParent<TMP_Text>();
        var source = parentText?.text;
        if (!ContainsJapanese(source))
            return;

        var fontName = subMesh.fontAsset != null ? subMesh.fontAsset.name : "<none>";
        var sharedMaterial = subMesh.sharedMaterial;
        var fallbackMaterial = subMesh.fallbackMaterial;
        var fallbackSourceMaterial = subMesh.fallbackSourceMaterial;
        var renderingMaterial = subMesh.materialForRendering;
        var line = $"tmp-submesh\t{UiHierarchyClassifier.GetHierarchyPath(subMesh, 64)}\t{EscapeForTsv(fontName)}\t" +
                   $"{EscapeForTsv(GetMaterialName(sharedMaterial))}\t{EscapeForTsv(source)}\t" +
                   $"{EscapeForTsv(GetMaterialName(fallbackMaterial))}\t" +
                   $"{EscapeForTsv(GetMaterialName(fallbackSourceMaterial))}\t" +
                   $"{EscapeForTsv(GetMaterialName(renderingMaterial))}\t" +
                   $"{subMesh.m_materialReferenceIndex}\t{GetMaterialDiagnostics(renderingMaterial)}" +
                   "\t\t\t\t\t\t\t\t\t\t\t\t\t";
        if (observed.Add(line))
            lines.Add(line);
    }

    private static void AddStoryRenderDiagnostics(ICollection<string> lines, ISet<string> observed)
    {
        foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
            AddStoryRenderDiagnostic(lines, observed, "image", image);
        foreach (var image in Resources.FindObjectsOfTypeAll<RawImage>())
            AddStoryRenderDiagnostic(lines, observed, "raw-image", image);
        foreach (var renderer in Resources.FindObjectsOfTypeAll<CanvasRenderer>())
            AddStoryRenderDiagnostic(lines, observed, "canvas-renderer", renderer);
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            AddStoryRenderDiagnostic(lines, observed, "canvas", canvas);
    }

    private static void AddStoryRenderDiagnostic(
        ICollection<string> lines,
        ISet<string> observed,
        string renderer,
        Component component)
    {
        if (component == null || component.gameObject == null ||
            !component.gameObject.activeInHierarchy)
            return;

        var path = UiHierarchyClassifier.GetHierarchyPath(component, 64);
        if (!path.Contains(
                "AdvEngine/UI/MessageWindowManager",
                StringComparison.OrdinalIgnoreCase))
            return;

        var canvas = component is Canvas ownCanvas
            ? ownCanvas
            : component.GetComponentInParent<Canvas>();
        var material = component switch
        {
            Graphic graphic => graphic.material,
            _ => null
        };
        var resourceName = component switch
        {
            Image image => image.sprite != null ? image.sprite.name : "<none>",
            RawImage rawImage => rawImage.texture != null ? rawImage.texture.name : "<none>",
            _ => "<none>"
        };
        var enabled = component switch
        {
            Behaviour behaviour => behaviour.enabled,
            CanvasRenderer canvasRenderer => !canvasRenderer.cull,
            _ => true
        };
        var raycast = component is Graphic graphicComponent
            ? graphicComponent.raycastTarget.ToString()
            : "<none>";
        var line = $"{renderer}\t{path}\t\t{EscapeForTsv(GetMaterialName(material))}\t\t\t\t\t\t" +
                   $"<none>\t<none>\tfalse\t{component.transform.GetSiblingIndex()}\t" +
                   $"{EscapeForTsv(canvas != null ? canvas.name : "<none>")}\t" +
                   $"{(canvas != null ? canvas.sortingOrder : 0)}\t{EscapeForTsv(resourceName)}\t" +
                   $"{enabled}\t{raycast}\t\t\t\t\t\t\t";
        if (observed.Add(line))
            lines.Add(line);
    }

    private static string GetMaterialName(Material material) =>
        material != null ? material.name : "<none>";

    private static string GetMaterialDiagnostics(Material material)
    {
        if (material == null)
            return "<none>\t<none>\tfalse";

        var outlineWidth = material.HasProperty("_OutlineWidth")
            ? material.GetFloat("_OutlineWidth").ToString("0.###", CultureInfo.InvariantCulture)
            : "<none>";
        var faceDilate = material.HasProperty("_FaceDilate")
            ? material.GetFloat("_FaceDilate").ToString("0.###", CultureInfo.InvariantCulture)
            : "<none>";
        return $"{outlineWidth}\t{faceDilate}\t{material.HasShaderKeyword("OUTLINE_ON")}";
    }

    private static string GetGradientDiagnostics(TMP_Text text)
    {
        try { return ReadGradientDiagnostics(text); }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"Gradient diagnostic unavailable: {ex}");
            return "<unavailable>\t\t\t\t\t\t";
        }
    }

    private static string GetFaceDiagnostics(Component component)
    {
        if (component is not TMP_Text text)
            return "\t\t\t";
        try
        {
            var material = text.fontSharedMaterial;
            var face = material != null && material.HasProperty("_FaceColor")
                ? FormatColor(material.GetColor("_FaceColor")) : "<none>";
            var original = TmpFontInstaller.TryGetOriginalFaceColor(text, out var color)
                ? FormatColor(color) : "<none>";
            return $"{FormatColor(text.color)}\t{FormatColor(text.canvasRenderer.GetColor())}\t{face}\t{original}";
        }
        catch (Exception ex)
        {
            return $"<unavailable:{EscapeForTsv(ex.Message)}>\t\t\t";
        }
    }

    private static string ReadGradientDiagnostics(TMP_Text text)
    {
        var gradient = text.colorGradient;
        var preset = text.colorGradientPreset != null ? text.colorGradientPreset.name : "<none>";
        return $"{text.enableVertexGradient}\t{FormatColor(gradient.topLeft)}\t" +
               $"{FormatColor(gradient.topRight)}\t{FormatColor(gradient.bottomLeft)}\t" +
               $"{FormatColor(gradient.bottomRight)}\t{EscapeForTsv(preset)}\t" +
               GetFirstVisibleVertexColors(text);
    }

    private static string GetSelectableDiagnostics(Component component)
    {
        try { return ReadSelectableDiagnostics(component); }
        catch (Exception ex)
        {
            Core.Plugin.Log?.LogWarning($"Button diagnostic unavailable: {ex}");
            return "<unavailable>\t\t\t\t\t\t\t";
        }
    }

    private static string ReadSelectableDiagnostics(Component component)
    {
        var selectable = component.GetComponentInParent<Selectable>();
        if (selectable == null)
            return "\t\t\t\t\t\t\t";
        var graphic = selectable.targetGraphic;
        return $"{selectable.GetIl2CppType().Name}\t{selectable.interactable}\t" +
               $"{selectable.IsInteractable()}\t{selectable.transition}\t" +
               $"{FormatColor(selectable.colors.disabledColor)}\t" +
               $"{(graphic != null ? EscapeForTsv(UiHierarchyClassifier.GetHierarchyPath(graphic, 64)) : "<none>")}\t" +
               $"{(graphic != null ? FormatColor(graphic.color) : "<none>")}\t" +
               $"{(graphic != null ? FormatColor(graphic.canvasRenderer.GetColor()) : "<none>")}";
    }

    private static string GetFirstVisibleVertexColors(TMP_Text text)
    {
        try
        {
            var textInfo = text.textInfo;
            if (textInfo == null || textInfo.characterCount <= 0)
                return "<none>";

            for (var i = 0; i < textInfo.characterCount; i++)
            {
                var character = textInfo.characterInfo[i];
                if (!character.isVisible)
                    continue;
                var materialIndex = character.materialReferenceIndex;
                if (materialIndex < 0 || materialIndex >= textInfo.meshInfo.Length)
                    return "<none>";
                var colors = textInfo.meshInfo[materialIndex].colors32;
                var vertexIndex = character.vertexIndex;
                if (colors == null || vertexIndex < 0 || vertexIndex + 3 >= colors.Length)
                    return "<none>";
                return $"{FormatColor(colors[vertexIndex])}," +
                       $"{FormatColor(colors[vertexIndex + 1])}," +
                       $"{FormatColor(colors[vertexIndex + 2])}," +
                       FormatColor(colors[vertexIndex + 3]);
            }
        }
        catch
        {
            // Snapshot diagnostics must never disturb live UI rendering.
        }
        return "<none>";
    }

    private static string FormatColor(Color color) =>
        $"#{ColorByte(color.r):X2}{ColorByte(color.g):X2}{ColorByte(color.b):X2}{ColorByte(color.a):X2}";

    private static byte ColorByte(float value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);

    private static string FormatColor(Color32 color) =>
        $"#{color.r:X2}{color.g:X2}{color.b:X2}{color.a:X2}";

    private static (string FontName, string MaterialName) GetFontMetadata(Component component)
    {
        if (component is TMP_Text tmp)
        {
            var fontName = tmp.font != null ? tmp.font.name : "<none>";
            var materialName = tmp.fontSharedMaterial != null
                ? tmp.fontSharedMaterial.name
                : "<none>";
            return (fontName, materialName);
        }

        if (component is Text legacy)
        {
            var fontName = legacy.font != null ? legacy.font.name : "<none>";
            var materialName = legacy.material != null ? legacy.material.name : "<none>";
            return (fontName, materialName);
        }

        return ("<unknown>", "<unknown>");
    }

    private static bool LooksLikeSkillNameOrDescription(string hierarchy, string source)
    {
        var candidate = source.Trim();
        var visibleText = RichTextTag.Replace(candidate, string.Empty);
        if (candidate.Length < 2 || candidate.Length > 1000 || ContainsAny(hierarchy, UiOnlyPathMarkers) ||
            UiLabels.Contains(visibleText) ||
            visibleText.StartsWith("所持数:", StringComparison.Ordinal) ||
            visibleText.StartsWith("所持:", StringComparison.Ordinal) ||
            visibleText.StartsWith("残り:", StringComparison.Ordinal) ||
            visibleText.Contains("サブスキルを選択", StringComparison.Ordinal) ||
            visibleText.Contains("セットする", StringComparison.Ordinal))
            return false;

        // Prefer named data fields when the prefab exposes them. Some game
        // versions put a SkillName inside a button, so this check must happen
        // before excluding generic control ancestors.
        if (ContainsAny(hierarchy, DescriptionMarkers) || ContainsAny(hierarchy, NameMarkers))
            return true;
        if (ContainsAny(hierarchy, UiControlMarkers))
            return false;

        // Some game versions use generic Text nodes, so content is a
        // conservative fallback for those instances.
        if (candidate.IndexOf('\n') >= 0 || candidate.Length >= 24)
            return true;
        if (candidate.Contains("。", StringComparison.Ordinal) ||
            candidate.Contains("、", StringComparison.Ordinal) ||
            candidate.Contains("！", StringComparison.Ordinal) ||
            candidate.Contains("？", StringComparison.Ordinal) ||
            candidate.Contains("%", StringComparison.Ordinal) ||
            candidate.Contains("+", StringComparison.Ordinal))
            return true;

        // Short, non-control text under an explicitly identified skill panel
        // is normally a skill name (for example, ポイズンエンチャント).
        return candidate.Length <= 60;
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
    {
        foreach (var marker in markers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string EscapeForTsv(string source) =>
        source.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string NormalizeNewlines(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal)
              .Replace("\r", "\n", StringComparison.Ordinal);
}
