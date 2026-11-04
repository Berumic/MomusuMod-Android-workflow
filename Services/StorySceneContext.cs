using System;
using System.Collections.Generic;
using System.Threading;

namespace MonsterMusumeTDMod.Services;

/// <summary>Preloading must never inherit the scene currently being played.</summary>
public static class StorySceneContext
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal);
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
    public static string CurrentScopeInferredScene => _scope?.InferredScene ?? string.Empty;

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

    public static string GetParsingScene(string source)
    {
        if (_scope != null)
        {
            if (!string.IsNullOrEmpty(_scope.Scene))
                return _scope.Scene;

            if (_scope.AllowInference)
            {
                if (!string.IsNullOrEmpty(_pendingParsedScene) &&
                    string.Equals(_pendingParsedText, source, StringComparison.Ordinal))
                {
                    return _pendingParsedScene;
                }
                if (!string.IsNullOrEmpty(_scope.InferredScene))
                    return _scope.InferredScene;
            }
            return string.Empty;
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
                if (current.AllowInference && string.IsNullOrEmpty(current.InferredScene))
                    current.InferredScene = scene;
            }
        }
        else if (string.IsNullOrEmpty(PlaybackId))
        {
            Volatile.Write(ref _inferredPlaybackId, scene);
        }
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
            Volatile.Write(ref _inferredPlaybackId, scene);
    }

    public static void ClearInferredPlayback()
    {
        Volatile.Write(ref _inferredPlaybackId, string.Empty);
    }

    public static void ClearPlayback()
    {
        Volatile.Write(ref _playbackId, string.Empty);
        Volatile.Write(ref _inferredPlaybackId, string.Empty);
        _pendingParsedText = null;
        _pendingParsedScene = null;
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
