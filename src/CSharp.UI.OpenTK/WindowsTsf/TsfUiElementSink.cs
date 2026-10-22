using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// TI-07 P3b：ITfUIElementSink 的生产实现（独立于宿主接线）。
/// </summary>
/// <remarks>
/// <para>Begin 只识别候选类型并抑制系统候选，不读内容；Update 才按页读取并发布不可变快照；
/// End/替换/失焦/epoch 立即撤销。所有回调返回 S_OK，绝不把异常穿过原生边界。</para>
/// <para>Element RCW 来自 <c>ITfUIElementMgr::GetUIElement</c>，由本对象持有并在借用结束后
/// 恰好释放一次；Begin 读取期间可能同步重入 End/同 id Begin，提交点用代次与结束墓碑复核。</para>
/// </remarks>
internal sealed class TsfUiElementSink : ITfUIElementSink, IDisposable
{
    private sealed class Entry
    {
        internal Entry(uint id, uint generation, ITfCandidateListUIElement candidate,
            ICandidateListOperations ops)
        {
            Id = id;
            Generation = generation;
            Candidate = candidate;
            Ops = ops;
        }

        internal uint Id { get; }
        internal uint Generation { get; }
        internal ITfCandidateListUIElement Candidate { get; }
        internal ICandidateListOperations Ops { get; }
        internal bool Ended { get; private set; }
        internal int InFlight { get; set; }
        internal bool Released { get; private set; }

        internal void MarkEnded() => Ended = true;

        internal void MarkReleased() => Released = true;
    }

    private readonly ITfUIElementMgr? _manager;
    private readonly ITfSource? _source;
    private readonly int _epoch;
    private readonly Func<bool> _isCurrentOwner;
    private readonly Action<CandidateSnapshot?> _publish;
    private readonly Action _requestRedraw;
    private readonly Action<object> _releaseElement;
    private readonly Dictionary<uint, Entry> _entries = [];

    /// <summary>仅存在于 <see cref="BeginUIElement"/> 调用栈内的尝试；返回时按引用移除，绝不保留永久墓碑。</summary>
    private readonly Dictionary<uint, PendingBegin> _pendingBegins = [];

    private uint _cookie = TsfFlags.TF_INVALID_COOKIE;
    private bool _advised;
    private bool _disposed;
    private bool _clearing;
    private uint _nextGeneration = 1;
    private uint _lifeSerial;
    private uint _currentId;
    private uint _currentGeneration;
    private CandidateSnapshot? _published;
    private long _nextUpdateSerial;
    private long _lastCommittedUpdateSerial;

    private sealed class PendingBegin
    {
    }

    internal TsfUiElementSink(
        ITfUIElementMgr? manager,
        ITfSource? source,
        int epoch,
        Func<bool> isCurrentOwner,
        Action<CandidateSnapshot?> publish,
        Action requestRedraw,
        Action<object>? releaseElement = null)
    {
        _manager = manager;
        _source = source;
        _epoch = epoch;
        _isCurrentOwner = isCurrentOwner;
        _publish = publish;
        _requestRedraw = requestRedraw;
        _releaseElement = releaseElement ?? ReleaseCom;
    }

    internal bool IsAdvised => _advised;
    internal uint Cookie => _cookie;
    internal int Epoch => _epoch;
    internal int BeginCount { get; private set; }
    internal int UpdateCount { get; private set; }
    internal int EndCount { get; private set; }
    internal int AdviseCount { get; private set; }
    internal int UnadviseCount { get; private set; }
    internal int UnadviseFailureCount { get; private set; }
    internal int ReleasedElementCount { get; private set; }
    internal int PublishedCount { get; private set; }
    internal int RedrawCount { get; private set; }
    internal int LiveEntryCount => _entries.Count;
    internal uint CurrentElementId => _currentId;
    internal uint CurrentGeneration => _currentGeneration;
    internal CandidateSnapshot? LastPublished => _published;

    /// <summary>注册 sink；只有真实 HRESULT 成功且 cookie 非 <c>TF_INVALID_COOKIE</c> 才算已连接。</summary>
    internal int Advise()
    {
        if (_disposed)
        {
            return TsfHResult.CO_E_OBJNOTCONNECTED;
        }

        if (_advised)
        {
            return TsfHResult.S_OK;
        }

        if (_source is null || _manager is null)
        {
            return TsfHResult.E_NOINTERFACE;
        }

        var hr = _source.AdviseSink(TsfIids.UiElementSink, this, out var cookie);
        AdviseCount++;
        if (hr >= 0 && cookie != TsfFlags.TF_INVALID_COOKIE)
        {
            _cookie = cookie;
            _advised = true;
            return hr;
        }

        _cookie = TsfFlags.TF_INVALID_COOKIE;
        return hr >= 0 ? TsfHResult.E_FAIL : hr;
    }

    /// <summary>成对撤销；未连接或已失效视为成功，其它失败如实返回并保留待重试标记。</summary>
    internal int Unadvise()
    {
        if (!_advised || _source is null)
        {
            return TsfHResult.S_OK;
        }

        var hr = _source.UnadviseSink(_cookie);
        UnadviseCount++;
        if (hr >= 0 || hr == TsfHResult.E_INVALIDARG || hr == TsfHResult.CONNECT_E_NOCONNECTION)
        {
            _advised = false;
            _cookie = TsfFlags.TF_INVALID_COOKIE;
            return hr >= 0 ? hr : TsfHResult.S_OK;
        }

        UnadviseFailureCount++;
        return hr;
    }

    public int BeginUIElement(uint dwUIElementId, ref int pbShow)
    {
        BeginCount++;
        var incoming = pbShow;
        object? ownedElement = null;
        var committed = false;
        PendingBegin? attempt = null;
        try
        {
            if (!CanUse)
            {
                return TsfHResult.S_OK;
            }

            var life = _lifeSerial;
            attempt = new PendingBegin();
            _pendingBegins[dwUIElementId] = attempt;

            // 先退休同 id 旧元素并撤销旧快照，再做可能失败/重入的 GetUIElement。
            if (_entries.Remove(dwUIElementId, out var old))
            {
                old.MarkEnded();
                ClearCurrentIf(old);
                ReleaseIfIdle(old);
            }

            if (_manager is null)
            {
                return TsfHResult.S_OK;
            }

            var hr = _manager.GetUIElement(dwUIElementId, out var element);
            if (hr < 0 || element is null)
            {
                return TsfHResult.S_OK;
            }

            // GetUIElement 成功即由本方法拥有该 RCW：QI 本身抛异常也必须在 finally 释放一次。
            ownedElement = element;

            if (element is not ITfCandidateListUIElement candidate)
            {
                // 非候选不抑制，也不注册；finally 释放本次取得的 RCW。
                return TsfHResult.S_OK;
            }

            // 提交点：本次 Begin 仍是该 id 的最新尝试（未在 GetUIElement 期间被 End 取消或被
            // 同 id 新 Begin 取代），生命周期也未失效。
            if (!CanUse || life != _lifeSerial
                || !_pendingBegins.TryGetValue(dwUIElementId, out var current)
                || !ReferenceEquals(current, attempt))
            {
                return TsfHResult.S_OK;
            }

            var entry = new Entry(dwUIElementId, ++_nextGeneration,
                candidate, new ComCandidateListOperations(candidate));
            _entries[dwUIElementId] = entry;
            committed = true;
            ownedElement = null;
            pbShow = 0;
        }
        catch (Exception)
        {
            // 原生回调边界：保持 pbShow 原值，不让异常越界。
            pbShow = incoming;
        }
        finally
        {
            if (attempt is not null
                && _pendingBegins.TryGetValue(dwUIElementId, out var pending)
                && ReferenceEquals(pending, attempt))
            {
                // 只移除本次尝试；绝不留下按 id 的永久墓碑。
                _pendingBegins.Remove(dwUIElementId);
            }

            if (!committed && ownedElement is not null)
            {
                ReleaseElement(ownedElement);
            }
        }

        return TsfHResult.S_OK;
    }

    public int UpdateUIElement(uint dwUIElementId)
    {
        UpdateCount++;
        try
        {
            if (!CanUse || !_entries.TryGetValue(dwUIElementId, out var entry) || entry.Ended)
            {
                return TsfHResult.S_OK;
            }

            var life = _lifeSerial;
            var serial = ++_nextUpdateSerial;
            entry.InFlight++;
            try
            {
                CandidateSnapshot snapshot;
                try
                {
                    var read = CandidatePageReader.Run(entry.Ops,
                        () => !CanUse || entry.Ended);
                    snapshot = read.Ok
                        ? BuildSnapshot(entry, read)
                        : BuildInvalid(entry, read.Failure);
                }
                catch (Exception ex)
                {
                    snapshot = BuildInvalid(entry, $"{ex.GetType().Name}: {ex.Message}");
                }

                if (!CanUse || life != _lifeSerial || entry.Ended
                    || !_entries.TryGetValue(dwUIElementId, out var current)
                    || !ReferenceEquals(current, entry))
                {
                    return TsfHResult.S_OK;
                }

                // 嵌套 Update 可能同步重入：较旧的外层读取绝不能在较新的内层提交之后覆盖显示。
                if (serial <= _lastCommittedUpdateSerial)
                {
                    return TsfHResult.S_OK;
                }

                _lastCommittedUpdateSerial = serial;
                _currentId = entry.Id;
                _currentGeneration = entry.Generation;
                Publish(snapshot);
            }
            finally
            {
                entry.InFlight--;
                if (entry.Ended)
                {
                    ReleaseIfIdle(entry);
                }
            }
        }
        catch (Exception)
        {
            // 保持零异常越界。
        }

        return TsfHResult.S_OK;
    }

    public int EndUIElement(uint dwUIElementId)
    {
        EndCount++;
        try
        {
            // 已释放/正在清理时不存在合法元素：不写任何按 id 的状态。
            if (_disposed || _clearing)
            {
                return TsfHResult.S_OK;
            }

            // 取消仍在 GetUIElement 调用栈内的本次 Begin 尝试（存在才移除，不留墓碑）。
            _pendingBegins.Remove(dwUIElementId);

            if (_entries.Remove(dwUIElementId, out var entry))
            {
                entry.MarkEnded();
                ClearCurrentIf(entry);
                ReleaseIfIdle(entry);
            }
        }
        catch (Exception)
        {
            // 保持零异常越界。
        }

        return TsfHResult.S_OK;
    }

    /// <summary>失焦/epoch/卸载：立即隐藏并终止所有元素；迟到回调因生命周期代次变化而拒绝。</summary>
    internal void Clear()
    {
        // 嵌套 Clear（RCW 释放/发布可能同步重入）不得在外层仍然清理时把 _clearing 提前复位，
        // 否则外层的释放窗口会重新允许注册。
        if (_clearing)
        {
            return;
        }

        _lifeSerial++;
        _clearing = true;
        try
        {
            _pendingBegins.Clear();
            var entries = new List<Entry>(_entries.Values);
            _entries.Clear();
            _currentId = 0;
            _currentGeneration = 0;

            // 释放 RCW / 发布隐藏可能同步重入 Begin：_clearing 期间 CanUse=false，
            // 新的注册会被拒绝，因此本方法返回后不可能残留 Clear 之后才建立的 Entry。
            foreach (var entry in entries)
            {
                entry.MarkEnded();
                ReleaseIfIdle(entry);
            }

            Publish(null);
        }
        finally
        {
            _clearing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 先置 disposed（迟到回调立即拒绝），再清空/释放并隐藏。
        _disposed = true;
        Clear();
    }

    private bool CanUse
    {
        get
        {
            if (_disposed || _clearing)
            {
                return false;
            }

            try
            {
                return _isCurrentOwner();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static CandidateSnapshot BuildSnapshot(Entry entry, CandidatePageRead read)
    {
        var count = Math.Min(read.Entries.Count, CandidateSnapshotLimits.MaxPageEntries);
        var entries = new CandidateSnapshotEntry[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = new CandidateSnapshotEntry(
                read.PageStart + (uint)i, read.Entries[i] ?? string.Empty);
        }

        return new CandidateSnapshot(entry.Id, entry.Generation, entries,
            CandidateSnapshot.PageLocalSelection(read.Selection, read.PageStart, count),
            read.Selection, read.Count, read.PageStart, read.CurrentPage, true, null,
            takeEntriesOwnership: true);
    }

    private static CandidateSnapshot BuildInvalid(Entry entry, string? failure)
        => new(entry.Id, entry.Generation, Array.Empty<CandidateSnapshotEntry>(),
            -1, 0, 0, 0, uint.MaxValue, false, failure ?? "read-failed");

    private void ClearCurrentIf(Entry entry)
    {
        if (_currentId != entry.Id || _currentGeneration != entry.Generation)
        {
            return;
        }

        _currentId = 0;
        _currentGeneration = 0;
        Publish(null);
    }

    private void Publish(CandidateSnapshot? snapshot)
    {
        if (ReferenceEquals(_published, snapshot)
            || SnapshotEquals(_published, snapshot))
        {
            return;
        }

        _published = snapshot;
        PublishedCount++;
        try
        {
            _publish(snapshot);
            _requestRedraw();
            RedrawCount++;
        }
        catch (Exception)
        {
            // 呈现回调失败不得穿过原生边界。
        }
    }

    private void ReleaseIfIdle(Entry entry)
    {
        if (!entry.Ended || entry.InFlight > 0 || entry.Released)
        {
            return;
        }

        entry.MarkReleased();
        ReleaseElement(entry.Candidate);
    }

    private void ReleaseElement(object element)
    {
        ReleasedElementCount++;
        try
        {
            _releaseElement(element);
        }
        catch (Exception)
        {
            // RCW 已失效/非 COM 替身：释放失败不得越界。
        }
    }

    private static void ReleaseCom(object element)
    {
        try
        {
            if (Marshal.IsComObject(element))
            {
                Marshal.ReleaseComObject(element);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool SnapshotEquals(CandidateSnapshot? left, CandidateSnapshot? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.Valid != right.Valid)
        {
            return false;
        }

        if (left.ElementId != right.ElementId || left.Generation != right.Generation)
        {
            return false;
        }

        // 失败快照只需保证“仍然隐藏”，不因失败文案变化重绘。
        if (!left.Valid)
        {
            return true;
        }

        if (left.TotalCount != right.TotalCount || left.PageStart != right.PageStart
            || left.CurrentPage != right.CurrentPage
            || left.SelectionIndex != right.SelectionIndex
            || left.SelectionOnPage != right.SelectionOnPage
            || left.Entries.Count != right.Entries.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Entries.Count; i++)
        {
            if (left.Entries[i].Index != right.Entries[i].Index
                || !string.Equals(left.Entries[i].Text, right.Entries[i].Text,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
