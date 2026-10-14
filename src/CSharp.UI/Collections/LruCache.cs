namespace CSharp.UI;

/// <summary>
/// 有界最近最少使用缓存：容量耗尽时淘汰最久未访问的条目，并通过回调释放被淘汰的资源。
/// </summary>
/// <typeparam name="TKey">键类型。</typeparam>
/// <typeparam name="TValue">值类型。</typeparam>
internal sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = [];
    private int _capacity;

    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    private readonly record struct Entry(TKey Key, TValue Value);

    /// <summary>条目被淘汰或移除时调用（用于释放资源）。</summary>
    public Action<TKey, TValue>? OnEvicted { get; set; }

    /// <summary>当前条目数量。</summary>
    public int Count => _map.Count;

    /// <summary>容量上限；缩小时立即淘汰多余条目。</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _capacity = value;
            Trim();
        }
    }

    /// <summary>尝试取回并标记为最近使用。</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>取回或创建；<paramref name="created"/> 表示是否新建。</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, out bool created)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet(key, out var existing))
        {
            created = false;
            return existing;
        }

        var value = factory(key);
        var node = _order.AddFirst(new Entry(key, value));
        _map[key] = node;
        created = true;
        Trim();
        return value;
    }

    /// <summary>移除指定键并触发淘汰回调。</summary>
    public bool Remove(TKey key)
    {
        if (!_map.TryGetValue(key, out var node))
        {
            return false;
        }

        _map.Remove(key);
        _order.Remove(node);
        OnEvicted?.Invoke(node.Value.Key, node.Value.Value);
        return true;
    }

    /// <summary>清空并释放全部条目。</summary>
    public void Clear()
    {
        var callback = OnEvicted;
        foreach (var entry in _order)
        {
            callback?.Invoke(entry.Key, entry.Value);
        }

        _map.Clear();
        _order.Clear();
    }

    private void Trim()
    {
        var callback = OnEvicted;
        while (_map.Count > _capacity && _order.Last is { } last)
        {
            _map.Remove(last.Value.Key);
            _order.RemoveLast();
            callback?.Invoke(last.Value.Key, last.Value.Value);
        }
    }
}
