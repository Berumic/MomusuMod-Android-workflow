using System;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MonsterMusumeTDMod.Services;

public static class ScenarioContext
{
    private static readonly Regex ScenarioIdPattern = new(
        @"(?:scenario|script|book)[_:/\\-]?([A-Za-z0-9_-]{5,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex SceneResourceIdPattern = new(
        @"(?:^|[/\\])(?<id>(?:r18|nml)_\d+_\d+)(?:[./\\]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static string _current = string.Empty;
    private static string _resource = string.Empty;
    private static float _nextActiveResourceProbe;
    public static string CurrentId => _current;
    public static string CurrentResourceId => _resource;

    public static void ClearResourceContext()
    {
        _resource = string.Empty;
    }

    public static void ObserveResourcePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var match = SceneResourceIdPattern.Match(path);
        if (!match.Success)
            return;

        var resourceId = match.Groups["id"].Value;
        if (string.Equals(_resource, resourceId, StringComparison.OrdinalIgnoreCase))
            return;

        _resource = resourceId;
        Core.Plugin.Log?.LogInfo($"Story resource context: {_resource}");
    }

    public static void ProbeActiveAdvSceneResource()
    {
        // Legacy text setters run often while Utage reveals characters. A
        // periodic hierarchy probe is enough to update scene context without
        // placing a full Resources scan on every setter invocation.
        if (Time.unscaledTime < _nextActiveResourceProbe)
            return;
        _nextActiveResourceProbe = Time.unscaledTime + 0.5f;

        foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (transform == null || transform.gameObject == null ||
                !transform.gameObject.activeInHierarchy)
                continue;

            var path = GetAdvScenePath(transform);
            if (path != null)
            {
                ObserveResourcePath(path);
                return;
            }
        }

        // Do not let an R18 resource ID leak into a subsequent ordinary
        // scene which has no discoverable resource object.
        ClearResourceContext();
    }

    public static void Observe(object instance)
    {
        if (instance == null)
            return;

        var fullName = instance.GetType().FullName ?? string.Empty;
        if (!fullName.Contains("Utage", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var name in new[]
        {
            "scenarioId", "ScenarioId", "scriptId", "ScriptId",
            "bookName", "BookName", "name", "Name"
        })
        {
            try
            {
                var property = instance.GetType().GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                var field = instance.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                var value = property?.GetValue(instance)?.ToString() ?? field?.GetValue(instance)?.ToString();
                if (string.IsNullOrEmpty(value))
                    continue;

                ObserveResourcePath(value);

                var match = ScenarioIdPattern.Match(value);
                var candidate = match.Success ? match.Groups[1].Value : value;

                // Utage may expose a resource path instead of the bare book
                // ID (for example category/character/10399a0_nml). Translation
                // tables are keyed by the leaf scenario directory, so reduce
                // slash-delimited values to that final component.
                candidate = candidate.Replace('\\', '/');
                var separator = candidate.LastIndexOf('/');
                if (separator >= 0 && separator + 1 < candidate.Length)
                    candidate = candidate[(separator + 1)..];

                // Resource names can include an extension while the table
                // directory does not.
                var extension = candidate.LastIndexOf('.');
                if (extension > 0)
                    candidate = candidate[..extension];

                _current = candidate;
                return;
            }
            catch { }
        }
    }

    private static string GetAdvScenePath(Transform transform)
    {
        var names = new System.Collections.Generic.List<string>();
        var containsAdvEngine = false;
        var containsAdvScene = false;
        for (var current = transform; current != null; current = current.parent)
        {
            names.Add(current.name);
            containsAdvEngine |= string.Equals(current.name, "AdvEngine", StringComparison.Ordinal);
            containsAdvScene |= string.Equals(current.name, "AdvScene", StringComparison.Ordinal);
        }

        if (!containsAdvEngine || !containsAdvScene)
            return null;

        names.Reverse();
        var path = string.Join("/", names);
        return SceneResourceIdPattern.IsMatch(path) ? path : null;
    }
}
