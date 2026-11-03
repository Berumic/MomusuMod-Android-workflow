using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace MonsterMusumeTDMod.Services;

public static class ScenarioContext
{
    private static readonly Regex ScenarioIdPattern = new(
        @"(?:scenario|script|book)[_:/\\-]?([A-Za-z0-9_-]{5,})",
        RegexOptions.IgnoreCase
    );
    private static readonly Regex SceneResourceIdPattern = new(
        @"(?:^|[/\\])(?<id>(?:r18|nml)_\d+_\d+)(?:[./\\]|$)",
        RegexOptions.IgnoreCase
    );

    private static string _current = string.Empty;
    private static string _resource = string.Empty;
    public static string CurrentId => Volatile.Read(ref _current);
    public static string CurrentResourceId => Volatile.Read(ref _resource);

    public static void ClearResourceContext()
    {
        Volatile.Write(ref _resource, string.Empty);
    }

    public static void ObserveResourcePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var match = SceneResourceIdPattern.Match(path);
        if (!match.Success)
            return;

        var resourceId = match.Groups["id"].Value;
        var previous = Volatile.Read(ref _resource);
        if (string.Equals(previous, resourceId, StringComparison.OrdinalIgnoreCase))
            return;

        Volatile.Write(ref _resource, resourceId);
        Core.Plugin.Log?.LogInfo($"Story resource context: {resourceId}");
    }

    public static void Observe(object instance)
    {
        if (instance == null)
            return;

        var type = instance.GetType();
        var fullName = type.FullName ?? string.Empty;
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
                var property = type.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                var field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                var value = property?.GetValue(instance)?.ToString() ?? field?.GetValue(instance)?.ToString();
                if (string.IsNullOrEmpty(value))
                    continue;

                ObserveResourcePath(value);

                var match = ScenarioIdPattern.Match(value);
                var candidate = match.Success ? match.Groups[1].Value : value;

                candidate = candidate.Replace('\\', '/');
                var separator = candidate.LastIndexOf('/');
                if (separator >= 0 && separator + 1 < candidate.Length)
                    candidate = candidate[(separator + 1)..];

                var extension = candidate.LastIndexOf('.');
                if (extension > 0)
                    candidate = candidate[..extension];

                Volatile.Write(ref _current, candidate);
                return;
            }
            catch { }
        }
    }
}
