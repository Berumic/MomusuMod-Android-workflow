using System;
using System.Collections.Generic;
using System.Threading;

namespace MonsterMusumeTDMod.Services;

/// <summary>Preloading must never inherit the scene currently being played.</summary>
public static class StorySceneContext
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal);
    private static readonly object CandidateLock = new();
    private static HashSet<string> _candidateScenes;
    [ThreadStatic] private static Scope _scope;
    [ThreadStatic] private static string _pendingParsedText;
    [ThreadStatic] private static string _pendingParsedScene;
    private static string _playbackId = string.Empty;
    private static string _inferredPlaybackId = string.Empty;

    public static string PlaybackId => Volatile.Read(ref _playbackId);
    public static string InferredPlaybackId => Volatile.Read(ref _inferredPlaybackId);
    public static string EffectivePlaybackId
    {
        get
        {
            var explicitId = PlaybackId;
            return !string.IsNullOrEmpty(explicitId) ? explicitId : InferredPlaybackId;
        }
    }

    public static string ParsingId => _scope == null ? PlaybackId : _scope.Scene;

    public static string CurrentScopeInferredScene
    {
        get
        {
            for (var current = _scope; current != null; current = current.Previous)
            {
                if (!string.IsNullOrEmpty(current.Scene))
                    return current.Scene;
                if (!current.AllowInference)
                    return string.Empty;
                if (!string.IsNullOrEmpty(current.InferredScene))
                    return current.InferredScene;
            }
            return InferredPlaybackId;
        }
    }

    public static IDisposable EnterParsing(string scene) => new Scope(scene, allowInference: false);

    public static IDisposable EnterScriptParsing(string scene) => new Scope(scene, allowInference: true);

    public static void PropagateSceneToCurrentScope(string scene)
    {
        if (string.IsNullOrEmpty(scene) || _scope == null)
            return;

        for (var current = _scope; current != null; current = current.Previous)
        {
            if (current.AllowInference && string.IsNullOrEmpty(current.Scene))
                current.Scene = scene;
        }
    }

    public static void SetPendingParsedCell(string text, string scene)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(scene))
            return;
        _pendingParsedText = text;
        _pendingParsedScene = scene;
    }

    public static bool AllowsInference => _scope == null || _scope.AllowInference;

    public static string GetParsingScene(string source)
    {
        if (_scope != null)
        {
            if (!_scope.AllowInference)
                return _scope.Scene;

            if (!string.IsNullOrEmpty(_pendingParsedScene) &&
                string.Equals(_pendingParsedText, source, StringComparison.Ordinal))
            {
                return _pendingParsedScene;
            }

            for (var current = _scope; current != null; current = current.Previous)
            {
                if (!string.IsNullOrEmpty(current.Scene))
                    return current.Scene;
                if (!current.AllowInference)
                    return string.Empty;
                if (!string.IsNullOrEmpty(current.InferredScene))
                    return current.InferredScene;
            }

            return EffectivePlaybackId;
        }

        if (!string.IsNullOrEmpty(_pendingParsedScene) &&
            string.Equals(_pendingParsedText, source, StringComparison.Ordinal))
        {
            var scene = _pendingParsedScene;
            _pendingParsedText = null;
            _pendingParsedScene = null;
            return scene;
        }

        return EffectivePlaybackId;
    }

    public static void NotifyUniqueSceneMatched(string scene)
    {
        if (string.IsNullOrEmpty(scene))
            return;

        if (_scope != null)
        {
            for (var current = _scope; current != null; current = current.Previous)
            {
                if (current.AllowInference)
                    current.InferredScene = scene;
            }
        }

        if (_scope == null || _scope.AllowInference)
        {
            Volatile.Write(ref _inferredPlaybackId, scene);
            lock (CandidateLock)
            {
                _candidateScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scene };
            }
        }
    }

    public static IReadOnlyCollection<string> NarrowCandidateScenes(IReadOnlyCollection<string> sourceScenes)
    {
        if (sourceScenes == null || sourceScenes.Count == 0 || !AllowsInference)
            return Array.Empty<string>();

        string resolvedUnique = null;
        HashSet<string> snapshot;
        lock (CandidateLock)
        {
            if (_candidateScenes == null || _candidateScenes.Count == 0)
            {
                _candidateScenes = new HashSet<string>(sourceScenes, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var intersection = new HashSet<string>(_candidateScenes, StringComparer.OrdinalIgnoreCase);
                intersection.IntersectWith(sourceScenes);
                _candidateScenes = intersection.Count > 0
                    ? intersection
                    : new HashSet<string>(sourceScenes, StringComparer.OrdinalIgnoreCase);
            }

            if (_candidateScenes.Count == 1)
            {
                foreach (var single in _candidateScenes)
                    resolvedUnique = single;
            }

            snapshot = new HashSet<string>(_candidateScenes, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(resolvedUnique))
            NotifyUniqueSceneMatched(resolvedUnique);

        return snapshot;
    }

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
        lock (Labels)
        {
            if (Labels.TryGetValue(label, out var scene) && !string.IsNullOrEmpty(scene))
                return scene;
            if (label.StartsWith("*", StringComparison.Ordinal) &&
                Labels.TryGetValue(label[1..], out scene) && !string.IsNullOrEmpty(scene))
                return scene;
            if (!label.StartsWith("*", StringComparison.Ordinal) &&
                Labels.TryGetValue("*" + label, out scene) && !string.IsNullOrEmpty(scene))
                return scene;
            return string.Empty;
        }
    }

    public static void SetPlayback(string scene)
    {
        Volatile.Write(ref _playbackId, scene ?? string.Empty);
        if (!string.IsNullOrEmpty(scene))
        {
            Volatile.Write(ref _inferredPlaybackId, scene);
            lock (CandidateLock)
            {
                _candidateScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scene };
            }
        }
    }

    public static void ClearInferredPlayback()
    {
        Volatile.Write(ref _inferredPlaybackId, string.Empty);
        lock (CandidateLock)
        {
            _candidateScenes = null;
        }
    }

    public static void ClearPlayback()
    {
        Volatile.Write(ref _playbackId, string.Empty);
        Volatile.Write(ref _inferredPlaybackId, string.Empty);
        _pendingParsedText = null;
        _pendingParsedScene = null;
        lock (CandidateLock)
        {
            _candidateScenes = null;
        }
    }

    private sealed class Scope : IDisposable
    {
        public string Scene;
        public string InferredScene = string.Empty;
        public readonly bool AllowInference;
        public readonly Scope Previous;
        private bool _disposed;

        public Scope(string scene, bool allowInference)
        {
            Scene = scene ?? string.Empty;
            AllowInference = allowInference;
            Previous = _scope;
            _scope = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _scope = Previous;
            _disposed = true;
        }
    }
}
