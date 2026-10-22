using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 生产原生组合 owner 的 COM 适配层（TI-07 P2b 第二批返修）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是真实产品回调</b>：<see cref="ITfContextOwnerCompositionSink"/> 与 <see cref="ITfTextEditSink"/>
/// 由本类实现并 advise 到真实 <c>ITfContext</c>。回调内只捕获不可变事实并入队，绝不写文档。
/// </para>
/// <para>
/// <b>连接生命周期：</b>advise 得到 cookie 由本类持有；<see cref="Dispose"/> 与
/// <see cref="Unadvise"/> 只 unadvise <b>自己</b>取得的 cookie，不对借用/共享 RCW 调
/// <c>FinalReleaseComObject</c>。
/// </para>
/// <para>
/// <b>回调内禁止：</b>不排版、不生成帧、不重建组件、不写 Binding、不调 sink。
/// </para>
/// </remarks>
internal sealed class NativeCompositionOwnerSink :
    ITfContextOwnerCompositionSink,
    ITfTextEditSink,
    IDisposable
{
    private readonly NativeCompositionOwner _owner;
    private readonly AcpTextStore _store;

    private readonly object _gate = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    private ITfSource? _source;
    private uint _editSinkCookie;
    private bool _editSinkAdvised;

    private bool _disposed;

    internal NativeCompositionOwnerSink(NativeCompositionOwner owner, AcpTextStore store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(store);

        _owner = owner;
        _store = store;
    }

    /// <summary>是否已成功 advise 编辑完成 sink。</summary>
    internal bool EditSinkAdvised => _editSinkAdvised;

    /// <summary>编辑完成 sink 的 cookie（0 表示未 advise）。</summary>
    internal uint EditSinkCookie => _editSinkCookie;

    /// <summary>是否仍持有 advise 时取得的 <c>ITfSource</c> 引用（测试核对成对回收）。</summary>
    internal bool HoldsSource => _source is not null;

    /// <summary>
    /// 为一次<b>真实回调</b>分配来源操作 ticket。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一个 owner 一个 ticket 域：</b>所有入口（原生 Start/Update/End/OnEndEdit 与宿主显式取消）
    /// 都从这里取号，因此不会出现“sink 局部序列 + owner 兜底序列”各自从 1 开始而撞号
    /// （那会把一次合法取消当作重放丢弃）。
    /// </para>
    /// <para>
    /// <b>每次真实回调分配一次：</b>同一原生操作的真实重试必须复用<b>最初捕获</b>的 ticket
    /// （调用方保存并原样传递），而不是按内容重建；因此这里不再保留字符串→ticket 映射，
    /// 也就不会出现 <c>update-1</c> 在同一组合内多次更新间重复、进而丢弃后续合法更新的缺陷。
    /// </para>
    /// <para><b>epoch 变化退休旧 ticket：</b>ticket 由 owner 分配，owner 释放/重建即换域。</para>
    /// </remarks>
    private long NextOperationTicket() => _owner.AllocateOperationTicket();



    /// <summary>
    /// 是否可在当前线程进入原生回调（所属线程且未释放）。
    /// </summary>
    /// <remarks>
    /// <b>必须最先判定</b>：任何借用 COM 视图/记录、计数器或 owner 入队都不得在
    /// 非所属线程或已释放后发生。
    /// </remarks>
    private bool CanEnterCallback => !_disposed && Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <summary>已收到的组合开始回调次数（测试核对真实回调到达）。</summary>
    internal int StartCallbacks { get; private set; }

    /// <summary>已收到的组合更新回调次数。</summary>
    internal int UpdateCallbacks { get; private set; }

    /// <summary>已收到的组合结束回调次数。</summary>
    internal int EndCallbacks { get; private set; }

    /// <summary>已收到的完成编辑回调次数（真实 <c>ITfTextEditSink::OnEndEdit</c>）。</summary>
    internal int EndEditCallbacks { get; private set; }

    /// <summary>最近一次捕获的组合范围（原生 ACP 坐标；无则 null）。</summary>
    internal (int Anchor, int Length)? LastRange { get; private set; }

    /// <summary>范围读取失败次数（无有效范围 → 显式失败，不伪造）。</summary>
    internal int RangeFailureCount { get; private set; }

    /// <summary>最近一次范围捕获的诊断（脱敏，无用户文本）。</summary>
    internal string? LastRangeDiagnostic { get; private set; }

    /// <summary>
    /// 把编辑完成 sink advise 到真实 context（需要 <c>ITfSource</c>）。
    /// </summary>
    /// <param name="context">真实 TSF 上下文。</param>
    /// <returns>advise 的 HRESULT；失败时如实返回（调用方必须当作失败）。</returns>
    /// <remarks>
    /// <para>
    /// <b>托管守卫先于平台分支：</b>释放状态、所属线程、已有连接、参数校验都是纯托管检查，
    /// 在<b>任何</b>平台分支或 COM 调用之前完成。这样跨平台测试断言的生命周期语义
    /// （<c>CO_E_OBJNOTCONNECTED</c> / <c>RPC_E_WRONG_THREAD</c> / <c>E_INVALIDARG</c>）与平台无关，
    /// 只有“真正不支持的原生注册”才返回 <c>E_NOINTERFACE</c>。
    /// </para>
    /// </remarks>
    internal int AdviseEditSink(ITfContext context)
    {
        lock (_gate)
        {
            // 已释放：不得新建连接。
            if (_disposed)
            {
                return TsfHResult.CO_E_OBJNOTCONNECTED;
            }

            // 非所属线程：拒绝（不触碰 COM），保留 owner 线程清理机会。
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                return TsfHResult.RPC_E_WRONG_THREAD;
            }

            // 已有连接：幂等成功，绝不覆盖尚未清理的 cookie。
            if (_editSinkAdvised)
            {
                return TsfHResult.S_OK;
            }

            if (context is null)
            {
                return TsfHResult.E_INVALIDARG;
            }

            // 到这里才需要真实 TSF 注册：非 Windows 没有可用的 ITfSource，如实报告不支持。
            if (!OperatingSystem.IsWindows())
            {
                return TsfHResult.E_NOINTERFACE;
            }

            var hr = QueryForSource(context, out var source);
            if (hr < 0 || source is null)
            {
                return hr < 0 ? hr : TsfHResult.E_NOINTERFACE;
            }

            var adviseHr = source.AdviseSink(TsfOwnerIids.TextEditSink, this, out var cookie);
            if (adviseHr < 0)
            {
                ReleaseCom(source);
                return adviseHr;
            }

            // 本类 advise 得到的 source 与 cookie 必须成对保存：
            // 若上一次连接未清理就覆盖，会让旧 cookie 永远无法撤销、且用旧 source 撤销新 cookie。
            // 走到这里时 _editSinkAdvised 已为 false 且 _source 为 null（Unadvise/Dispose 都会成对清空）。
            _source = source;
            _editSinkCookie = cookie;
            _editSinkAdvised = true;
            return adviseHr;
        }
    }

    /// <summary>unadvise 自己取得的编辑完成 sink（幂等）。</summary>
    /// <remarks>
    /// <para>
    /// <b>连接状态在调用原生 <c>UnadviseSink</c> 之前摘除</b>：原生撤销可能同步重入
    /// <see cref="Dispose"/>，若此时状态仍标记“已连接”，就会对同一个 cookie 重复撤销并重复释放引用。
    /// </para>
    /// <para>
    /// <b>同时释放本类持有的 <c>ITfSource</c>：</b>成功撤销后必须成对回收该 RCW，否则连接泄漏；
    /// 且下一次注册到<b>不同</b> context 时，旧 source 与新 cookie 必须不混用。
    /// </para>
    /// </remarks>
    internal int UnadviseEditSink()
    {
        ITfSource? source;
        uint cookie;

        lock (_gate)
        {
            // 非所属线程：先拒绝（不触碰 COM），保留 owner 线程清理机会。
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                return TsfHResult.RPC_E_WRONG_THREAD;
            }

            if (!_editSinkAdvised || _source is null)
            {
                return TsfHResult.S_OK;
            }

            // 先摘除连接状态（防重入重复撤销），再调原生。
            source = _source;
            cookie = _editSinkCookie;
            _source = null;
            _editSinkCookie = 0;
            _editSinkAdvised = false;
        }

        var hr = TsfHResult.S_OK;
        try
        {
            hr = source.UnadviseSink(cookie);
        }
        catch (Exception)
        {
            hr = TsfHResult.E_FAIL;
        }
        finally
        {
            // 成对回收：本类 advise 时取得、此处释放。
            ReleaseCom(source);
        }

        return hr;
    }

    // ------------------------------------------------------------------
    // ITfContextOwnerCompositionSink
    // ------------------------------------------------------------------

    /// <summary>
    /// 组合开始：捕获真实范围（<c>pComposition.GetRange()</c> → <c>ITfRangeACP::GetExtent</c>）。
    /// </summary>
    public int OnStartComposition(ITfCompositionView pComposition, out int pfOk)
    {
        pfOk = 0;

        // 最先判定：非所属线程或已释放 → 不触碰借用视图、不计数、不入队。
        if (!CanEnterCallback)
        {
            return TsfHResult.S_OK;
        }

        pfOk = 1;
        try
        {
            StartCallbacks++;
            CaptureCompositionRange(pComposition, NativeCompositionFactKind.Start, "start", out var ok);
            pfOk = ok ? 1 : 0;
            return TsfHResult.S_OK;
        }
        catch (Exception)
        {
            // 托管异常绝不穿过 COM 边界。
            pfOk = 0;
            return TsfHResult.E_FAIL;
        }
    }

    /// <summary>
    /// 组合更新：<b>捕获 pRangeNew</b>（回调期间 <c>pComposition.GetRange()</c> 仍是 OLD 范围）。
    /// </summary>
    public int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew)
    {
        // 最先判定：不读范围、不计数、不入队。
        if (!CanEnterCallback)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            UpdateCallbacks++;

            // pRangeNew 非 null：直接读取本次的新范围。
            if (pRangeNew != IntPtr.Zero)
            {
                var hr = ReadRangeExtent(pRangeNew, out var anchor, out var length);
                if (hr >= 0)
                {
                    LastRange = (anchor, length);
                    _owner.Capture(
                        NativeCompositionFactKind.Update,
                        anchor,
                        length,
                        preedit: null,
                        hasRange: true,
                        sourceTicket: NextOperationTicket());
                    return TsfHResult.S_OK;
                }
            }

            // pRangeNew 为 null 是**合法**情形（范围未变）：从组合视图读取当前范围，
            // 不把它当作无效更新。既有真实输入日志（activation-scroll-real seq99/135）即为该形态。
            if (pComposition is not null)
            {
                var viewHr = pComposition.GetRange(out var viewRangePtr);
                if (viewHr >= 0 && viewRangePtr != IntPtr.Zero)
                {
                    try
                    {
                        var extentHr = ReadRangeExtent(viewRangePtr, out var anchor, out var length);
                        if (extentHr >= 0)
                        {
                            LastRange = (anchor, length);
                            _owner.Capture(
                                NativeCompositionFactKind.Update,
                                anchor,
                                length,
                                preedit: null,
                                hasRange: true,
                                sourceTicket: NextOperationTicket());
                            return TsfHResult.S_OK;
                        }
                    }
                    finally
                    {
                        Marshal.Release(viewRangePtr);
                    }
                }
            }

            // 两个来源都不可用：显式失败（不伪造范围）。
            RangeFailureCount++;
            _owner.Capture(
                NativeCompositionFactKind.Update,
                anchor: 0,
                length: 0,
                preedit: null,
                hasRange: false,
                sourceTicket: NextOperationTicket());
            return TsfHResult.S_OK;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    /// <summary>
    /// 组合结束：<b>只捕获终止事实</b>（无取消标志，绝不在此提交）。
    /// </summary>
    public int OnEndComposition(ITfCompositionView pComposition)
    {
        if (!CanEnterCallback)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            EndCallbacks++;
            _owner.Capture(
                NativeCompositionFactKind.End,
                anchor: LastRange?.Anchor ?? 0,
                length: LastRange?.Length ?? 0,
                preedit: null,
                hasRange: LastRange is not null,
                sourceTicket: NextOperationTicket());
            return TsfHResult.S_OK;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    // ------------------------------------------------------------------
    // ITfTextEditSink
    // ------------------------------------------------------------------

    /// <summary>
    /// 编辑事务结束（已验证的完成边界）：捕获完成编辑事实，分类留给安全阶段。
    /// </summary>
    public int OnEndEdit(ITfContext pic, uint ecReadOnly, ITfEditRecord pEditRecord)
    {
        // 最先判定：不得触碰借用 ITfEditRecord/ITfContext，也不计数/入队。
        if (!CanEnterCallback)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            EndEditCallbacks++;

            var selectionChanged = false;
            var textChanged = true;
            if (pEditRecord is not null)
            {
                if (pEditRecord.GetSelectionStatus(out var changed) >= 0)
                {
                    selectionChanged = changed != 0;
                }
            }

            _owner.CaptureCompletedEdit(
                selectionChanged,
                textChanged,
                sourceTicket: NextOperationTicket());
            return TsfHResult.S_OK;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    // ------------------------------------------------------------------
    // 范围捕获
    // ------------------------------------------------------------------

    private void CaptureCompositionRange(
        ITfCompositionView composition,
        NativeCompositionFactKind kind,
        string operationPrefix,
        out bool ok)
    {
        ok = false;
        var rangePtr = IntPtr.Zero;

        // 一次真实回调 = 一个 ticket：失败分支也必须带同一个 ticket（否则失败事实无身份）。
        var ticket = NextOperationTicket();
        try
        {
            var hr = composition.GetRange(out rangePtr);
            LastRangeDiagnostic = $"GetRange hr=0x{hr:X8}";
            if (hr < 0 || rangePtr == IntPtr.Zero)
            {
                RangeFailureCount++;
                _owner.Capture(kind, 0, 0, null, hasRange: false, sourceTicket: ticket);
                return;
            }

            // 原始指针直接 QI 到 ITfRangeACP（ITfRangeACP 继承 ITfRange，原生对象支持）。
            var acpIid = TsfOwnerIids.RangeAcp;
            var qiHr = Marshal.QueryInterface(rangePtr, in acpIid, out var rangeAcpPtr);
            LastRangeDiagnostic = $"GetRange=ok QI(RangeACP) hr=0x{qiHr:X8}";
            if (qiHr < 0 || rangeAcpPtr == IntPtr.Zero)
            {
                RangeFailureCount++;
                _owner.Capture(kind, 0, 0, null, hasRange: false, sourceTicket: ticket);
                return;
            }

            try
            {
                var extentHr = TsfRangeAcp.GetExtentViaVtable(rangeAcpPtr, out var anchor, out var cch);
                LastRangeDiagnostic = $"QI=ok GetExtent hr=0x{extentHr:X8} anchor={anchor} cch={cch}";
                if (extentHr < 0)
                {
                    RangeFailureCount++;
                    _owner.Capture(kind, 0, 0, null, hasRange: false, sourceTicket: ticket);
                    return;
                }

                LastRange = (anchor, cch);
                ok = true;
                _owner.Capture(kind, anchor, cch, preedit: null, hasRange: true, sourceTicket: ticket);
            }
            finally
            {
                Marshal.Release(rangeAcpPtr);
            }
        }
        finally
        {
            if (rangePtr != IntPtr.Zero)
            {
                Marshal.Release(rangePtr);
            }
        }
    }
    /// <summary>
    /// 读取范围真实文本（<c>ITfRange::GetText</c>，只读）。
    /// </summary>
    /// <remarks>
    /// 捕获阶段必须取得真实预编辑文本，不能永远向 owner 传 null。
    /// 读取失败返回 null（不伪造内容），由 owner 按“无文本”处理。
    /// </remarks>
    private static string? ReadRangeText(IntPtr range, int anchor, int length)
    {
        if (range == IntPtr.Zero || length <= 0)
        {
            return null;
        }

        try
        {
            var rangeIid = TsfOwnerIids.Range;
            if (Marshal.QueryInterface(range, in rangeIid, out var tfRangePtr) < 0
                || tfRangePtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var tfRange = (ITfRange)Marshal.GetObjectForIUnknown(tfRangePtr);
                try
                {
                    var buffer = new char[length];
                    // 只读上下文 cookie：GetText 在只读回调中亦可用（TSF 允许只读读取）。
                    var hr = tfRange.GetText(0u, 0u, buffer, (uint)length, out var read);
                    if (hr < 0 || read == 0)
                    {
                        return null;
                    }

                    return new string(buffer, 0, (int)Math.Min(read, (uint)length));
                }
                finally
                {
                    Marshal.ReleaseComObject(tfRange);
                }
            }
            finally
            {
                Marshal.Release(tfRangePtr);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ReadRangeExtent(IntPtr range, out int anchor, out int length)
    {
        anchor = 0;
        length = 0;
        if (range == IntPtr.Zero)
        {
            return TsfHResult.E_INVALIDARG;
        }

        try
        {
            var rangeAcpIid = TsfOwnerIids.RangeAcp;
            var hr = Marshal.QueryInterface(range, in rangeAcpIid, out var rangeAcpPtr);
            if (hr < 0 || rangeAcpPtr == IntPtr.Zero)
            {
                return hr < 0 ? hr : TsfHResult.E_NOINTERFACE;
            }

            try
            {
                return TsfRangeAcp.GetExtentViaVtable(rangeAcpPtr, out anchor, out length);
            }
            finally
            {
                Marshal.Release(rangeAcpPtr);
            }
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int QueryForSource(ITfContext context, out ITfSource? source)
    {
        source = null;
        var unknown = IntPtr.Zero;
        var sourcePtr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(context);
            var iid = TsfOwnerIids.SourceIid;
            var hr = Marshal.QueryInterface(unknown, in iid, out sourcePtr);
            if (hr < 0 || sourcePtr == IntPtr.Zero)
            {
                return hr;
            }

            source = (ITfSource)Marshal.GetObjectForIUnknown(sourcePtr);
            return hr;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
        finally
        {
            if (sourcePtr != IntPtr.Zero)
            {
                Marshal.Release(sourcePtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception)
        {
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只 unadvise <b>自己</b>取得的 cookie，并释放自己持有的 <c>ITfSource</c>；
    /// 不对借用/共享 RCW 调 <c>FinalReleaseComObject</c>。
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            // 幂等：重复释放不重复撤销。
            if (_disposed)
            {
                return;
            }

            // 非所属线程：在**修改任何状态/触碰 COM 之前**拒绝，
            // 保留 owner 线程继续使用与清理连接的机会。
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                return;
            }

            _disposed = true;

            // 在锁内一次性摘除连接状态，避免递归 unadvise（Unadvise* 会再次取锁）。
            var source = _source;
            var editCookie = _editSinkCookie;
            var hadEdit = _editSinkAdvised;

            _source = null;
            _editSinkCookie = 0;
            _editSinkAdvised = false;

            // 每个自有连接恰好撤销一次（不递归、不重复）。
            if (source is not null)
            {

                if (hadEdit)
                {
                    try
                    {
                        source.UnadviseSink(editCookie);
                    }
                    catch (Exception)
                    {
                    }
                }

                ReleaseCom(source);
            }
        }

        _owner.ClearPending();
    }
}
