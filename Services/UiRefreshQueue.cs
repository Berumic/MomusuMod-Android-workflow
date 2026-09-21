using System;
using System.Collections;
using System.Collections.Generic;

namespace MonsterMusumeTDMod.Services;

// Pure scheduling state: Unity references are opaque to the merge policy.
internal sealed class UiRefreshQueue<T> : IEnumerable<KeyValuePair<int, UiRefreshQueue<T>.Entry>>
{
    private readonly Dictionary<int, Entry> _entries = new();
    public int Count => _entries.Count;
    public bool Enqueue(int id, T text, int readyFrame, bool activation)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Text = text;
            entry.ReadyFrame = Math.Min(entry.ReadyFrame, readyFrame);
            entry.IsActivation |= activation;
            return true;
        }
        _entries[id] = new Entry(text, readyFrame, activation);
        return false;
    }
    public void Remove(int id) => _entries.Remove(id);
    public void Clear() => _entries.Clear();
    public Dictionary<int, Entry>.Enumerator GetEnumerator() => _entries.GetEnumerator();
    IEnumerator<KeyValuePair<int, Entry>> IEnumerable<KeyValuePair<int, Entry>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal sealed class Entry
    {
        internal Entry(T text, int frame, bool activation)
        { Text = text; ReadyFrame = frame; IsActivation = activation; }
        public T Text { get; internal set; }
        public int ReadyFrame { get; internal set; }
        public bool IsActivation { get; internal set; }
    }
}
