using System;
using System.Collections.Generic;

namespace MonsterMusumeTDMod.Services;

// Main-thread cache: retain hot labels when changing numeric labels fill it.
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _recency = new();
    public BoundedLruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public int Count => _entries.Count;
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default;
            return false;
        }
        _recency.Remove(node);
        _recency.AddLast(node);
        value = node.Value.Value;
        return true;
    }
    public void Set(TKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out var node))
            _recency.Remove(node);
        else if (_entries.Count >= _capacity)
        {
            node = _recency.First;
            _entries.Remove(node.Value.Key);
            _recency.RemoveFirst();
        }
        else
            node = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
        node.Value = (key, value);
        _recency.AddLast(node);
        _entries[key] = node;
    }
    public void Clear()
    {
        _entries.Clear();
        _recency.Clear();
    }
}
