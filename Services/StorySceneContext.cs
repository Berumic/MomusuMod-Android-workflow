using System;
using System.Collections.Generic;
using System.Threading;

namespace MonsterMusumeTDMod.Services;

/// <summary>Preloading must never inherit the scene currently being played.</summary>
public static class StorySceneContext
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal);
    [ThreadStatic] private static Scope _scope;
    private static string _playbackId = string.Empty;

    public static string PlaybackId => Volatile.Read(ref _playbackId);
    public static string ParsingId => _scope == null ? PlaybackId : _scope.Scene;

    public static IDisposable EnterParsing(string scene) => new Scope(scene);

    public static void RegisterLabel(string label, string scene)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(scene)) return;
        lock (Labels)
        {
            if (Labels.TryGetValue(label, out var previous) && previous != scene)
                Labels[label] = string.Empty;
            else if (!Labels.ContainsKey(label))
                Labels.Add(label, scene);
        }
    }

    public static string ResolveLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return string.Empty;
        lock (Labels) return Labels.TryGetValue(label, out var scene) ? scene : string.Empty;
    }

    public static void SetPlayback(string scene) => Volatile.Write(ref _playbackId, scene ?? string.Empty);
    public static void ClearPlayback() => Volatile.Write(ref _playbackId, string.Empty);

    private sealed class Scope : IDisposable
    {
        public readonly string Scene;
        private readonly Scope _previous;
        private bool _disposed;

        public Scope(string scene)
        {
            Scene = scene ?? string.Empty;
            _previous = _scope;
            _scope = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _scope = _previous;
            _disposed = true;
        }
    }
}
