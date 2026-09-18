using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// Captures sub-skill names/descriptions from the complete runtime catalog.
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

    private static readonly Regex RichTextTag = new("<[^>]*>", RegexOptions.Compiled);

    private readonly string _snapshotPath;
    private readonly string _missingPath;
    private readonly string _subSkillScanPath;
    private readonly object _lock = new();
    private readonly HashSet<string> _subSkillScanObserved = new(StringComparer.Ordinal);
    private bool _subSkillScanActive;

    public RuntimeTextCollector(string translationsRoot)
    {
        _snapshotPath = Path.Combine(translationsRoot, "ui_snapshot.tsv");
        _missingPath = Path.Combine(translationsRoot, "missing.json");
        _subSkillScanPath = Path.Combine(translationsRoot, "subskill_scan.tsv");
    }

    public void Observe(object instance, string source)
    {
        if (instance is not Component component || !ContainsJapanese(source))
            return;

        var hierarchy = GetHierarchy(component);
        if (!IsCompleteSubSkillHierarchy(hierarchy) ||
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
                IsCompleteSubSkillHierarchy(GetHierarchy(text)))
                Observe(text, text.text);
        }

        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (text != null && text.gameObject != null && text.gameObject.activeInHierarchy &&
                IsCompleteSubSkillHierarchy(GetHierarchy(text)))
                Observe(text, text.text);
        }
    }

    /// <summary>
    /// Writes every active Japanese UI string for diagnosis and appends rows
    /// not covered by a loaded translation rule to missing.json.
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
        var missingCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = new List<string>
            {
                "renderer\tui_path\tfont_name\tmaterial_name\ttext\tfallback_material\t" +
                "fallback_source_material\trendering_material\tmaterial_index\t" +
                "outline_width\tface_dilate\toutline_on\tsibling_index\tcanvas_name\t" +
                "canvas_sorting_order\tresource_name\tenabled\traycast_target"
            };
            var observed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            AddSnapshotLine(lines, observed, missingCandidates, hasTranslation, resolveCategory, "tmp", text, text?.text);
            foreach (var subMesh in Resources.FindObjectsOfTypeAll<TMP_SubMeshUI>())
                AddSubMeshSnapshotLine(lines, observed, subMesh);
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            AddSnapshotLine(lines, observed, missingCandidates, hasTranslation, resolveCategory, "legacy", text, text?.text);
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

    private static void AddSnapshotLine(
        ICollection<string> lines,
        ISet<string> observed,
        IDictionary<string, HashSet<string>> missingCandidates,
        Func<string, string, bool> hasTranslation,
        Func<string, string> resolveCategory,
        string renderer,
        Component component,
        string source)
    {
        if (component == null || component.gameObject == null ||
            !component.gameObject.activeInHierarchy || !ContainsJapanese(source))
            return;

        var (fontName, materialName) = GetFontMetadata(component);
        var material = component is TMP_Text tmp ? tmp.fontSharedMaterial : null;
        var line = $"{renderer}\t{GetHierarchy(component, 64)}\t{EscapeForTsv(fontName)}\t" +
                   $"{EscapeForTsv(materialName)}\t{EscapeForTsv(source)}\t\t\t\t\t" +
                   GetMaterialDiagnostics(material) + "\t\t\t\t\t\t";
        if (observed.Add(line))
            lines.Add(line);

        var normalizedSource = NormalizeNewlines(source);
        var path = GetHierarchy(component, 64);
        if (hasTranslation == null || !hasTranslation(normalizedSource, path))
        {
            var category = resolveCategory?.Invoke(path) ?? CreateStableUiCategory(path);
            if (!missingCandidates.TryGetValue(category, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                missingCandidates[category] = values;
            }
            values.Add(normalizedSource);
        }
    }

    private void UpdateMissingFile(IReadOnlyDictionary<string, HashSet<string>> candidates)
    {
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathMissing = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_missingPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_missingPath, Encoding.UTF8));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("$paths") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var pathProperty in property.Value.EnumerateObject())
                        {
                            var table = new Dictionary<string, string>(StringComparer.Ordinal);
                            if (pathProperty.Value.ValueKind == JsonValueKind.Object)
                                foreach (var row in pathProperty.Value.EnumerateObject())
                                    if (row.Value.ValueKind == JsonValueKind.String)
                                        table[row.Name] = row.Value.GetString() ?? string.Empty;
                            pathMissing[pathProperty.Name] = table;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        missing[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Plugin.Log?.LogWarning(
                    $"读取现有 missing.json 失败，已停止更新以避免覆盖: {ex.Message}");
                return;
            }
        }
        else
        {
            missing = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var added = 0;
        foreach (var group in candidates.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            var category = string.IsNullOrEmpty(group.Key) ? "$global" : group.Key;
            if (category == "$global")
            {
                foreach (var source in group.Value.OrderBy(value => value, StringComparer.Ordinal))
                    if (missing.TryAdd(source, string.Empty)) added++;
            }
            else
            {
                if (!pathMissing.TryGetValue(category, out var table))
                {
                    table = new Dictionary<string, string>(StringComparer.Ordinal);
                    pathMissing[category] = table;
                }
                foreach (var source in group.Value.OrderBy(value => value, StringComparer.Ordinal))
                    if (table.TryAdd(source, string.Empty)) added++;
            }
        }

        if (added == 0)
        {
            Core.Plugin.Log?.LogInfo(
                $"Missing UI translations: added=0, total={missing.Count} -> {_missingPath}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_missingPath)!);
        var temporaryPath = _missingPath + ".tmp";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        try
        {
            File.WriteAllText(
                temporaryPath,
                SerializeMissingFile(missing, pathMissing, options) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _missingPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        Core.Plugin.Log?.LogInfo(
            $"Missing UI translations: added={added}, total={missing.Count} -> {_missingPath}");
    }

    private static string SerializeMissingFile(
        IReadOnlyDictionary<string, string> global,
        IReadOnlyDictionary<string, Dictionary<string, string>> paths,
        JsonSerializerOptions options)
    {
        var output = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pair in global)
            output[pair.Key] = pair.Value;
        if (paths.Count > 0)
            output["$paths"] = paths;
        return JsonSerializer.Serialize(output, options);
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
        var line = $"tmp-submesh\t{GetHierarchy(subMesh, 64)}\t{EscapeForTsv(fontName)}\t" +
                   $"{EscapeForTsv(GetMaterialName(sharedMaterial))}\t{EscapeForTsv(source)}\t" +
                   $"{EscapeForTsv(GetMaterialName(fallbackMaterial))}\t" +
                   $"{EscapeForTsv(GetMaterialName(fallbackSourceMaterial))}\t" +
                   $"{EscapeForTsv(GetMaterialName(renderingMaterial))}\t" +
                   $"{subMesh.m_materialReferenceIndex}\t{GetMaterialDiagnostics(renderingMaterial)}" +
                   "\t\t\t\t\t\t";
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

        var path = GetHierarchy(component, 64);
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
                   $"{enabled}\t{raycast}";
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

    private static bool IsCompleteSubSkillHierarchy(string hierarchy) =>
        !string.IsNullOrEmpty(hierarchy) &&
        ((hierarchy.Contains("unit_detail_rework", StringComparison.OrdinalIgnoreCase) &&
          hierarchy.Contains("skilldialog", StringComparison.OrdinalIgnoreCase) &&
          hierarchy.Contains("subskilltextarea", StringComparison.OrdinalIgnoreCase)) ||
         IsAttachAbilitySubSkillHierarchy(hierarchy));

    private static bool IsAttachAbilitySubSkillHierarchy(string hierarchy) =>
        !string.IsNullOrEmpty(hierarchy) &&
        hierarchy.Contains("unit_detail_attachability", StringComparison.OrdinalIgnoreCase) &&
        hierarchy.Contains("windowbase/scrollview", StringComparison.OrdinalIgnoreCase);

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

    private static string GetHierarchy(Component component, int maxNodes = 12)
    {
        try
        {
            var names = new List<string>();
            var current = component.transform;
            while (current != null && names.Count < maxNodes)
            {
                names.Add(current.gameObject.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string EscapeForTsv(string source) =>
        source.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string NormalizeNewlines(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal)
              .Replace("\r", "\n", StringComparison.Ordinal);
}
