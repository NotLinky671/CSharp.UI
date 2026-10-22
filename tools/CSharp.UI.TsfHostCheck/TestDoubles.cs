namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 正式测试与 --selftest 共用的 ACP sink 替身。只记录调用与 mask 行为，不模拟输入法。
/// 它不是产品路径的一部分；真实运行由 msctf 提供 sink。
/// </summary>
internal sealed class RecordingTextStoreSink : ITextStoreACPSink
{
    internal Func<uint, int>? LockGranted { get; set; }

    /// <summary>回调期间查询当前锁类型，用于验证通知是否发生在持锁状态。</summary>
    internal Func<uint>? LockTypeProbe { get; set; }

    /// <summary>OnTextChange 回调钩子；用于验证回调内可重新取得锁。</summary>
    internal Action? OnTextChangedHook { get; set; }

    /// <summary>OnLayoutChange 回调钩子；用于模拟 TSF 在布局通知内同步重入查询。</summary>
    internal Action<int, uint>? LayoutChangeHook { get; set; }

    internal readonly List<uint> LockTypesDuringNotifications = new();

    internal int TextChangeCount { get; private set; }

    internal int SelectionChangeCount { get; private set; }

    internal int LayoutChangeCount { get; private set; }

    internal int StatusChangeCount { get; private set; }

    internal int LockGrantedCount { get; private set; }

    internal uint LastLockFlags { get; private set; }

    internal readonly List<(uint Flags, TsTextChange Change)> TextChanges = new();

    internal readonly List<(int Code, uint View)> LayoutChanges = new();

    private void CaptureLockType() => LockTypesDuringNotifications.Add(LockTypeProbe?.Invoke() ?? 0);

    public int OnTextChange(uint dwFlags, ref TsTextChange pChange)
    {
        CaptureLockType();
        TextChangeCount++;
        TextChanges.Add((dwFlags, pChange));
        OnTextChangedHook?.Invoke();
        return TsfHResult.S_OK;
    }

    public int OnSelectionChange()
    {
        CaptureLockType();
        SelectionChangeCount++;
        return TsfHResult.S_OK;
    }

    public int OnLayoutChange(int lcode, uint vcView)
    {
        CaptureLockType();
        LayoutChangeCount++;
        LayoutChanges.Add((lcode, vcView));
        LayoutChangeHook?.Invoke(lcode, vcView);
        return TsfHResult.S_OK;
    }

    public int OnStatusChange(uint dwFlags)
    {
        StatusChangeCount++;
        return TsfHResult.S_OK;
    }

    public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => TsfHResult.S_OK;

    public int OnLockGranted(uint dwLockFlags)
    {
        LockGrantedCount++;
        LastLockFlags = dwLockFlags;
        return LockGranted?.Invoke(dwLockFlags) ?? TsfHResult.S_OK;
    }

    public int OnStartEditTransaction() => TsfHResult.S_OK;

    public int OnEndEditTransaction() => TsfHResult.S_OK;
}

/// <summary>组合 sink 替身：验证 CreateContext owner 的接口委托，不模拟输入法。</summary>
internal sealed class RecordingCompositionSink : ITfContextOwnerCompositionSink
{
    internal int StartCount { get; private set; }

    internal int UpdateCount { get; private set; }

    internal int EndCount { get; private set; }

    internal int LastPfOk { get; private set; }

    public int OnStartComposition(ITfCompositionView pComposition, out int pfOk)
    {
        StartCount++;
        pfOk = 1;
        LastPfOk = pfOk;
        return TsfHResult.S_OK;
    }

    public int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew)
    {
        UpdateCount++;
        return TsfHResult.S_OK;
    }

    public int OnEndComposition(ITfCompositionView pComposition)
    {
        EndCount++;
        return TsfHResult.S_OK;
    }
}

internal static class StoreTestHarness
{
    /// <summary>在真实 RequestLock 路径下执行写/读操作；返回操作 HRESULT。</summary>
    internal static int UnderLock(AcpTextStore store, RecordingTextStoreSink sink, uint lockFlags,
        Func<int> body, out int requestHr, out int sessionHr)
    {
        var result = TsfHResult.E_FAIL;
        var previous = sink.LockGranted;
        sink.LockGranted = _ =>
        {
            result = body();
            return result;
        };

        try
        {
            requestHr = store.RequestLock(lockFlags | TsfFlags.TS_LF_SYNC, out sessionHr);
        }
        finally
        {
            sink.LockGranted = previous;
        }

        // RequestLock 已真正返回：测试/自检在此模拟宿主安全阶段，按 mask 送达延迟通知。
        store.FlushDeferredNotifications();
        return result;
    }

    /// <summary>
    /// 在真实 RequestLock 授予的**读锁内**执行 <paramref name="body"/>：模拟 TSF 在只读编辑会话中
    /// 持有读锁时探针查询 store 几何的真实前提（store 侧要求 RequireRead）。
    /// </summary>
    internal static int UnderReadLock(AcpTextStore store, RecordingTextStoreSink sink, Func<int> body,
        out int requestHr, out int sessionHr)
        => UnderLock(store, sink, TsfFlags.TS_LF_READ, body, out requestHr, out sessionHr);
}
