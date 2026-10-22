using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 真实 ACP 文本存储：UTF-16 文档、单一选区、同步/异步锁、真实布局快照几何、组合归属与通知 mask。
/// 所有 COM 方法整体 try/catch，托管异常不穿过原生边界。
/// </summary>
internal sealed class AcpTextStore : ITextStoreACP, ITfContextOwnerCompositionSink
{
    private readonly object _stateGate = new();
    private readonly AcpTextDocument _document = new();
    private readonly TextStoreLockManager _locks;
    private readonly TsfTrace? _trace;
    private readonly LayoutChainTrace _chain;

    private ITextStoreACPSink? _sink;
    private object? _sinkPunk;
    private uint _sinkMask;
    private AcpLayoutSnapshot? _layout;
    private long _layoutGeneration;
    private bool _layoutCreatedNotified;
    private int _layoutNotifyDepth;

    /// <summary>
    /// 当前是否正处于 <c>OnLayoutChange</c> 通知的同步调用栈内（TSF 回调重入）。
    /// 生命周期安全点利用它拒绝在原生回调内部读取 profile/store。
    /// </summary>
    internal int LayoutNotifyDepth => _layoutNotifyDepth;

    /// <summary>独立于文本布局的显示表面快照（客户端屏幕矩形）。</summary>
    private DisplaySurfaceResult _surfaceSnapshot;

    private uint _viewCookie = 1;
    private IntPtr _hwnd;

    private bool _compositionActive;
    private int _compositionStart;
    private int _compositionEnd;

    // R29B：组合连续性单调计数。BeginComposition 每次成功都 +1；EndComposition 每次真正结束活动组合 +1；
    // _compositionSessionId 在每次 Begin 时分配一个新的单调 id。text/span 相同但 begin/end/session 变化
    // 即证明"组合曾结束又以相同文本重启"——仅靠 text/span+writes 无法检测这种不连续。
    private int _compositionBeginCount;
    private int _compositionEndCount;
    private int _compositionSessionId;

    private readonly List<DeferredNotification> _deferredNotifications = new();
    private readonly object _flushGate = new();
    private bool _flushingNotifications;
    private bool _flushScheduled;
    private bool _lockContinuationScheduled;

    private enum NotificationKind
    {
        TextChange,
        SelectionChange,
    }

    private readonly record struct DeferredNotification(
        NotificationKind Kind, uint Flags, AcpTextChange Change);

    internal AcpTextStore(TsfTrace? trace = null)
        : this(trace, LayoutChainTrace.Disabled)
    {
    }

    /// <summary>
    /// 带诊断的构造。<paramref name="chain"/> 默认禁用；启用后只增加可观测性，
    /// 不改变锁、通知时序与几何返回值。诊断标签按本 store 实例隔离，不跨 store 共享。
    /// </summary>
    internal AcpTextStore(TsfTrace? trace, LayoutChainTrace chain)
    {
        _trace = trace;
        _chain = chain ?? LayoutChainTrace.Disabled;
        _locks = new TextStoreLockManager(GrantSinkLock, trace, _chain);
    }

    /// <summary>
    /// 显示表面数据源（可注入，供无 HWND 的单元测试使用）。
    /// 默认实现查真实 HWND；返回 <c>null</c> 时退回真实 HWND 查询。
    /// </summary>
    internal Func<DisplaySurfaceResult>? DisplaySurfaceProvider { get; set; }

    /// <summary>
    /// 独立的显示表面快照：仅由窗口接线更新（移动/resize/DPI/显隐），
    /// **不**由文本布局失效或编辑清空。用于无真实 HWND 时提供当前表面。
    /// </summary>
    internal DisplaySurfaceResult SurfaceSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _surfaceSnapshot;
            }
        }

        set
        {
            lock (_stateGate)
            {
                _surfaceSnapshot = value;
            }
        }
    }

    /// <summary>由宿主在真实窗口几何变化后更新独立表面快照；不影响文本布局版本。</summary>
    internal void UpdateDisplaySurface(DisplaySurfaceResult surface)
    {
        lock (_stateGate)
        {
            _surfaceSnapshot = surface;
        }
    }

    /// <summary>布局调用链诊断（默认禁用）。</summary>
    internal LayoutChainTrace Chain => _chain;

    internal AcpTextDocument Document => _document;

    internal TextStoreLockManager Locks => _locks;

    internal uint SinkMask
    {
        get
        {
            lock (_stateGate)
            {
                return _sinkMask;
            }
        }
    }

    internal bool HasSink
    {
        get
        {
            lock (_stateGate)
            {
                return _sink is not null;
            }
        }
    }

    internal long LayoutGeneration => _layoutGeneration;

    internal uint ViewCookie => _viewCookie;

    /// <summary>
    /// R27：在同一线程内一次性读取组合生命周期相关的不可变快照
    /// （文本、组合范围、是否组合中、当前锁类型）。
    /// R29B：同一把 <c>_stateGate</c> 锁内**同时**返回组合连续性单调计数
    /// （begin/end/session）——text/span 与计数原子一致，绝不是两次锁读取的拼接。
    /// 只读：不申请/释放锁，不改文档、选区或组合。
    /// </summary>
    internal (string Text, int Start, int End, bool Composing, uint LockType,
        int BeginCount, int EndCount, int SessionId) SnapshotForLifecycle()
    {
        lock (_stateGate)
        {
            return (_document.Text, _compositionStart, _compositionEnd, _compositionActive,
                _locks.LockType, _compositionBeginCount, _compositionEndCount,
                _compositionSessionId);
        }
    }

    internal bool IsComposing
    {
        get
        {
            lock (_stateGate)
            {
                return _compositionActive;
            }
        }
    }

    internal (int Start, int End) CompositionSpan
    {
        get
        {
            lock (_stateGate)
            {
                return (_compositionStart, _compositionEnd);
            }
        }
    }

    /// <summary>每次成功文本写入后回调：变更、来源、写入时组合是否活动、写入后的文本片段。</summary>
    internal Action<AcpTextChange, AcpEditOrigin, bool, string>? ChangeObserver { get; set; }

    /// <summary>组合结束回调；非空文本表示一次提交，空文本表示取消。只会触发一次。</summary>
    internal Action<string>? CommitObserver { get; set; }

    /// <summary>文本变化后请求窗口在安全点重新布局（实现只投递消息，不在此排版）。</summary>
    internal Action? RequestRelayout { get; set; }

    /// <summary>
    /// 应用侧通知的“安全阶段”调度器：必须把 action 投递到 RequestLock 返回之后（窗口消息/宿主泵）。
    /// 未设置时（纯托管测试/审计）退化为稍后定时投递，仍保证不在 RequestLock 栈内发送。
    /// </summary>
    internal Action<Action>? NotificationDispatcher { get; set; }

    /// <summary>
    /// CreateContext 的 owner 对象同时暴露 ITfContextOwnerCompositionSink；TSF 会在创建上下文时
    /// 对该对象 QI 并安装组合 sink（官方注册方式，不依赖事后 ITfSource advice）。
    /// </summary>
    internal ITfContextOwnerCompositionSink? CompositionObserver { get; set; }

    public int OnStartComposition(ITfCompositionView pComposition, out int pfOk)
    {
        pfOk = 1;
        var observer = CompositionObserver;
        if (observer is null)
        {
            Trace("composition", "owner-sink-missing", null);
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnStartComposition(pComposition, out pfOk);
        }
        catch (Exception ex)
        {
            pfOk = 0;
            Trace("composition", "owner-on-start-exception", ExceptionFields(ex));
            return TsfHResult.E_FAIL;
        }
    }

    public int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew)
    {
        var observer = CompositionObserver;
        if (observer is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnUpdateComposition(pComposition, pRangeNew);
        }
        catch (Exception ex)
        {
            Trace("composition", "owner-on-update-exception", ExceptionFields(ex));
            return TsfHResult.E_FAIL;
        }
    }

    public int OnEndComposition(ITfCompositionView pComposition)
    {
        var observer = CompositionObserver;
        if (observer is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnEndComposition(pComposition);
        }
        catch (Exception ex)
        {
            Trace("composition", "owner-on-end-exception", ExceptionFields(ex));
            return TsfHResult.E_FAIL;
        }
    }

    internal void AttachWindow(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    internal void ResetDocument(string text)
    {
        lock (_stateGate)
        {
            _document.Reset(text);
            _deferredNotifications.Clear();
            InvalidateLayoutLocked("document-reset");
        }
    }

    // -----------------------------------------------------------------------
    // 布局快照
    // -----------------------------------------------------------------------

    internal void InvalidateLayout(string reason)
    {
        lock (_stateGate)
        {
            InvalidateLayoutLocked(reason);
        }
    }

    private void InvalidateLayoutLocked(string reason)
    {
        _layoutGeneration++;
        _layout = null;
        Trace("layout", "invalidated", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["generation"] = _layoutGeneration,
            ["document_version"] = _document.Version,
        });
    }

    internal void PublishLayout(AcpLayoutSnapshot snapshot, bool notify)
    {
        lock (_stateGate)
        {
            _layout = snapshot;
            _viewCookie = snapshot.ViewCookie;

            // 兼容既有测试：PublishLayout 同时提供显示表面数据（客户端屏幕矩形）。
            // 真实宿主仍由 UI 线程在客户区变化时调用 UpdateDisplaySurface 刷新；
            // 文本布局失效（InvalidateLayout）**不**清空该表面。
            _surfaceSnapshot = new DisplaySurfaceResult(TsfHResult.S_OK, snapshot.ClientScreen,
                true, null);

            Trace("layout", "published", new Dictionary<string, object?>
            {
                ["generation"] = snapshot.LayoutGeneration,
                ["document_version"] = snapshot.DocumentVersion,
                ["client"] = snapshot.ClientScreen.ToString(),
                ["row"] = snapshot.TextRowScreen.ToString(),
                ["scroll_x"] = snapshot.ScrollX,
                ["scroll_y"] = snapshot.ScrollY,
                ["cells"] = snapshot.Cells.Count,
            });
        }

        if (notify)
        {
            // 首次向已安装 sink 发布视图时发送 TS_LC_CREATE；之后每次布局变化发送 TS_LC_CHANGE。
            // TSF 文档要求视图创建/销毁有独立 layout code，不能只发 CHANGE。
            if (!_layoutCreatedNotified && NotifyLayoutChange(TsfFlags.TS_LC_CREATE))
            {
                _layoutCreatedNotified = true;
            }

            NotifyLayoutChange(TsfFlags.TS_LC_CHANGE);
        }
    }

    /// <summary>视图销毁：发送 TS_LC_DESTROY（若 sink 仍存在）。</summary>
    internal void NotifyLayoutDestroyed()
    {
        if (_layoutCreatedNotified)
        {
            NotifyLayoutChange(TsfFlags.TS_LC_DESTROY);
            _layoutCreatedNotified = false;
        }
    }

    internal bool NotifyLayoutChange(int layoutCode)
    {
        ITextStoreACPSink? sink;
        uint mask;
        uint view;
        long generation;
        long documentVersion;
        lock (_stateGate)
        {
            sink = _sink;
            mask = _sinkMask;
            view = _viewCookie;
            generation = _layoutGeneration;
            documentVersion = _document.Version;
        }

        if (sink is null || (mask & TsfFlags.TS_AS_LAYOUT_CHANGE) == 0)
        {
            Trace("notify", "layout-suppressed", new Dictionary<string, object?>
            {
                ["has_sink"] = sink is not null,
                ["mask"] = $"0x{mask:X}",
            });
            return false;
        }

        // 诊断配对 ID：enter/exit 共用同一 ID，便于从日志复原一次通知的完整范围。
        var notificationId = _chain.BeginNotification();
        var entered = false;
        int? exitHResult = null;
        Exception? exitException = null;
        try
        {
            // 深度标记：TSF 在本次 OnLayoutChange 内可能同步回调 RequestLock→GetTextExt。
            // 该深度只说明“查询发生在我们的 OnLayoutChange 同步回调内”，即 TSF 层回调；
            // 它不证明是 TIP（更不是微软拼音）主动消费了该布局，也不说明候选是否移动。
            _layoutNotifyDepth++;
            entered = true;
            if (_chain.Enabled)
            {
                _chain.EnterNotification(notificationId);
                Trace("notify", "layout-enter", new Dictionary<string, object?>
                {
                    ["layout_code"] = layoutCode,
                    ["view"] = view,
                    ["layout_generation"] = generation,
                    ["document_version"] = documentVersion,
                    ["lock_type"] = $"0x{_locks.LockType:X}",
                    ["diag_notification_id"] = notificationId,
                });
            }

            var hr = sink.OnLayoutChange(layoutCode, view);
            exitHResult = hr;
            Trace("notify", "layout", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(hr),
                ["layout_code"] = layoutCode,
                ["view"] = view,
                ["layout_notify_depth"] = _layoutNotifyDepth,
                ["diag_notification_id"] = notificationId,
            });
            return hr >= 0;
        }
        catch (Exception ex)
        {
            exitException = ex;
            Trace("notify", "layout-exception", ExceptionFields(ex));
            return false;
        }
        finally
        {
            if (entered)
            {
                _layoutNotifyDepth--;
            }

            if (_chain.Enabled)
            {
                // exit 必须在 finally：enter 之后无论正常返回、返回失败 HRESULT 还是异常都成对写出，
                // 并保留原 id、sink 返回的 HRESULT 或异常名；日志本身不改变控制流。
                Trace("notify", "layout-exit", new Dictionary<string, object?>
                {
                    ["layout_code"] = layoutCode,
                    ["view"] = view,
                    ["layout_notify_depth"] = _layoutNotifyDepth,
                    ["diag_notification_id"] = notificationId,
                    ["hresult"] = exitHResult is null ? null : TsfHResult.Describe(exitHResult.Value),
                    ["exception"] = exitException?.GetType().Name,
                    ["message"] = exitException?.Message,
                    ["outcome"] = exitException is not null
                        ? "exception"
                        : exitHResult is null ? "unknown" : exitHResult.Value >= 0 ? "ok" : "failed",
                });
                _chain.ExitNotification();
            }
        }
    }

    private bool TryGetCurrentLayout(out AcpLayoutSnapshot layout)
    {
        lock (_stateGate)
        {
            var snapshot = _layout;
            if (snapshot is null || !snapshot.IsCurrent(_layoutGeneration, _document.Version)
                || snapshot.ViewCookie != _viewCookie)
            {
                layout = null!;
                return false;
            }

            layout = snapshot;
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // 组合与归属
    // -----------------------------------------------------------------------

    internal bool BeginComposition(int start, int length)
    {
        lock (_stateGate)
        {
            if (!_document.IsValidRange(start, start + length))
            {
                Trace("composition", "begin-invalid", RangeFields(start, start + length));
                return false;
            }

            _compositionActive = true;
            _compositionStart = start;
            _compositionEnd = start + length;
            _compositionBeginCount++;
            _compositionSessionId++;
            Trace("composition", "begin", RangeFields(start, start + length));
            return true;
        }
    }

    internal bool UpdateComposition(int start, int length)
    {
        lock (_stateGate)
        {
            if (!_compositionActive)
            {
                Trace("composition", "update-without-active", RangeFields(start, start + length));
                return false;
            }

            if (!_document.IsValidRange(start, start + length))
            {
                Trace("composition", "update-invalid", RangeFields(start, start + length));
                return false;
            }

            _compositionStart = start;
            _compositionEnd = start + length;
            Trace("composition", "update", RangeFields(start, start + length));
            return true;
        }
    }

    /// <summary>结束组合并返回组合范围最终文本；非空即一次提交。重复调用幂等。</summary>
    internal string EndComposition()
    {
        string text;
        bool wasActive;
        lock (_stateGate)
        {
            wasActive = _compositionActive;
            text = wasActive ? _document.GetText(_compositionStart, _compositionEnd) : string.Empty;
            _compositionActive = false;
            _compositionStart = 0;
            _compositionEnd = 0;
            if (wasActive)
            {
                _compositionEndCount++;
            }
            Trace("composition", wasActive ? "end" : "end-without-active", new Dictionary<string, object?>
            {
                ["text_utf16_units"] = text.Length,
                ["text"] = ImeFormat.EscapeUtf16(text),
                ["commit"] = text.Length > 0,
            });
        }

        if (wasActive)
        {
            try
            {
                CommitObserver?.Invoke(text);
            }
            catch (Exception ex)
            {
                Trace("composition", "commit-observer-exception", ExceptionFields(ex));
            }
        }

        return text;
    }

    internal void CancelCompositionTracking()
    {
        lock (_stateGate)
        {
            if (!_compositionActive)
            {
                return;
            }

            _compositionActive = false;
            _compositionStart = 0;
            _compositionEnd = 0;
            Trace("composition", "tracking-cleared", null);
        }
    }

    private void AdjustCompositionSpan(in AcpTextChange change)
    {
        if (!_compositionActive)
        {
            return;
        }

        if (change.NewEnd < change.OldEnd)
        {
            var removed = change.OldEnd - change.NewEnd;
            if (change.Start < _compositionStart)
            {
                _compositionStart = Math.Max(change.Start, _compositionStart - removed);
            }

            if (change.Start < _compositionEnd)
            {
                _compositionEnd = Math.Max(_compositionStart, _compositionEnd - removed);
            }
        }
        else if (change.NewEnd > change.OldEnd)
        {
            var delta = change.NewEnd - change.OldEnd;
            if (change.Start < _compositionStart)
            {
                _compositionStart += delta;
            }

            if (change.Start <= _compositionEnd)
            {
                _compositionEnd += delta;
            }
        }
        // 等长替换不改变组合范围长度；权威范围仍以 OnUpdateComposition 的 ITfRangeACP extent 为准。

        _compositionStart = Math.Clamp(_compositionStart, 0, _document.Length);
        _compositionEnd = Math.Clamp(_compositionEnd, _compositionStart, _document.Length);
    }

    // -----------------------------------------------------------------------
    // 应用侧写入（必须在 ITfEditSession::DoEditSession 内、写锁持有）
    // -----------------------------------------------------------------------

    internal int ApplyAppText(string text)
    {
        lock (_stateGate)
        {
            if (!_locks.IsWriteLocked)
            {
                Trace("app", "write-without-lock", null);
                return TsfHResult.TS_E_NOLOCK;
            }

            var (start, end) = _document.Selection;
            var hr = ApplyChangeLocked(start, end, text, AcpEditOrigin.App, 0);
            if (hr >= 0)
            {
                _document.TrySetSelection(start + text.Length, start + text.Length);
                NotifySelectionChangeLocked();
            }

            return hr;
        }
    }

    internal int ApplyAppBackspace()
    {
        lock (_stateGate)
        {
            if (!_locks.IsWriteLocked)
            {
                Trace("app", "backspace-without-lock", null);
                return TsfHResult.TS_E_NOLOCK;
            }

            var (start, end) = _document.Selection;
            if (start == end)
            {
                if (start == 0)
                {
                    return TsfHResult.S_OK;
                }

                start--;
            }

            return ApplyChangeLocked(start, end, string.Empty, AcpEditOrigin.App, 0);
        }
    }

    internal int ApplyAppSelection(int start, int end)
    {
        lock (_stateGate)
        {
            if (!_locks.IsWriteLocked)
            {
                Trace("app", "selection-without-lock", null);
                return TsfHResult.TS_E_NOLOCK;
            }

            if (!_document.TrySetSelection(start, end))
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            NotifySelectionChangeLocked();
            return TsfHResult.S_OK;
        }
    }

    private int ApplyChangeLocked(int start, int end, string text, AcpEditOrigin origin, uint textChangeFlags)
    {
        if (!_document.TryReplace(start, end, text, origin, out var change))
        {
            Trace("store", "replace-invalid", RangeFields(start, end));
            return TsfHResult.TS_E_INVALIDPOS;
        }

        AdjustCompositionSpan(change);
        var inserted = change.NewEnd > change.Start
            ? _document.GetText(change.Start, change.NewEnd)
            : string.Empty;

        Trace("store", "text-change", new Dictionary<string, object?>
        {
            ["origin"] = origin.ToString(),
            ["start"] = change.Start,
            ["old_end"] = change.OldEnd,
            ["new_end"] = change.NewEnd,
            ["version"] = change.Version,
            ["composition_active"] = _compositionActive,
            ["inserted_utf16_units"] = inserted.Length,
            ["inserted"] = ImeFormat.EscapeUtf16(inserted),
        });

        // 官方约定：应用不得在响应 SetText/InsertTextAtSelection 时回送 OnTextChange；
        // TSF 自己已知道该变更。应用外部修改才在安全点按 mask 通知。
        if (origin == AcpEditOrigin.App)
        {
            NotifyTextChangeLocked(textChangeFlags, change);
        }
        else
        {
            Trace("notify", "text-change-suppressed-tsf-origin", new Dictionary<string, object?>
            {
                ["start"] = change.Start,
                ["old_end"] = change.OldEnd,
                ["new_end"] = change.NewEnd,
                ["lock_type"] = $"0x{_locks.LockType:X}",
            });
        }

        try
        {
            ChangeObserver?.Invoke(change, origin, _compositionActive, inserted);
        }
        catch (Exception ex)
        {
            Trace("store", "change-observer-exception", ExceptionFields(ex));
        }

        try
        {
            RequestRelayout?.Invoke();
        }
        catch (Exception ex)
        {
            Trace("store", "relayout-request-exception", ExceptionFields(ex));
        }

        return TsfHResult.S_OK;
    }

    // -----------------------------------------------------------------------
    // 通知（mask 门控）
    // -----------------------------------------------------------------------

    private void NotifyTextChangeLocked(uint flags, in AcpTextChange change)
    {
        if (_locks.LockType != 0)
        {
            // 持锁时不能回送 sink 通知（会返回 TS_E_NOLOCK/重入）；释放后在安全点统一发送。
            _deferredNotifications.Add(new DeferredNotification(
                NotificationKind.TextChange, flags, change));
            Trace("notify", "text-change-deferred-until-unlock", new Dictionary<string, object?>
            {
                ["flags"] = $"0x{flags:X}",
                ["lock_type"] = $"0x{_locks.LockType:X}",
            });
            return;
        }

        SendTextChangeLocked(flags, change);
    }

    private void SendTextChangeLocked(uint flags, in AcpTextChange change)
    {
        var sink = _sink;
        if (sink is null || (_sinkMask & TsfFlags.TS_AS_TEXT_CHANGE) == 0)
        {
            return;
        }

        var native = new TsTextChange
        {
            AcpStart = change.Start,
            AcpOldEnd = change.OldEnd,
            AcpNewEnd = change.NewEnd,
        };
        try
        {
            var hr = sink.OnTextChange(flags, ref native);
            Trace("notify", "text-change", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(hr),
                ["flags"] = $"0x{flags:X}",
                ["start"] = change.Start,
                ["old_end"] = change.OldEnd,
                ["new_end"] = change.NewEnd,
                ["lock_type"] = $"0x{_locks.LockType:X}",
            });
        }
        catch (Exception ex)
        {
            Trace("notify", "text-change-exception", ExceptionFields(ex));
        }
    }

    private void NotifySelectionChangeLocked()
    {
        if (_locks.LockType != 0)
        {
            _deferredNotifications.Add(new DeferredNotification(
                NotificationKind.SelectionChange, 0, default));
            Trace("notify", "selection-change-deferred-until-unlock", new Dictionary<string, object?>
            {
                ["lock_type"] = $"0x{_locks.LockType:X}",
            });
            return;
        }

        SendSelectionChangeLocked();
    }

    private void SendSelectionChangeLocked()
    {
        var sink = _sink;
        if (sink is null || (_sinkMask & TsfFlags.TS_AS_SEL_CHANGE) == 0)
        {
            return;
        }

        try
        {
            var hr = sink.OnSelectionChange();
            Trace("notify", "selection-change", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(hr),
                ["lock_type"] = $"0x{_locks.LockType:X}",
            });
        }
        catch (Exception ex)
        {
            Trace("notify", "selection-change-exception", ExceptionFields(ex));
        }
    }

    /// <summary>锁释放后按 mask 发送延迟的应用侧通知；持锁或正在发送时不做任何事。</summary>
    internal void FlushDeferredNotifications()
    {
        if (!Monitor.TryEnter(_flushGate))
        {
            return;
        }

        try
        {
            lock (_stateGate)
            {
                _flushScheduled = false;
            }

            if (_flushingNotifications)
            {
                return;
            }

            if (_locks.LockType != 0)
            {
                // 仍在锁内：把 flush 再投递到下一个安全阶段，绝不在此发送。
                ScheduleFlush();
                return;
            }

            _flushingNotifications = true;
            try
            {
                while (true)
                {
                    lock (_stateGate)
                    {
                        if (_deferredNotifications.Count == 0 || _locks.LockType != 0)
                        {
                            return;
                        }

                        var item = _deferredNotifications[0];
                        _deferredNotifications.RemoveAt(0);
                        if (item.Kind == NotificationKind.TextChange)
                        {
                            SendTextChangeLocked(item.Flags, item.Change);
                        }
                        else
                        {
                            SendSelectionChangeLocked();
                        }
                    }
                }
            }
            finally
            {
                _flushingNotifications = false;
            }
        }
        finally
        {
            var remaining = false;
            lock (_stateGate)
            {
                remaining = _deferredNotifications.Count > 0;
            }

            Monitor.Exit(_flushGate);
            if (remaining)
            {
                ScheduleFlush();
            }
        }
    }

    /// <summary>
    /// 把 flush 投递到 RequestLock 方法真正返回之后的安全阶段；coalesced post 保证同一批只调度一次。
    /// </summary>
    private void ScheduleFlush()
    {
        lock (_stateGate)
        {
            if (_deferredNotifications.Count == 0 || _flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        var dispatcher = NotificationDispatcher;
        if (dispatcher is null)
        {
            // 没有宿主 dispatcher：保留 pending，等调用方在原线程安全阶段显式 flush。
            // 绝不创建线程池 Timer 跨线程回调 COM sink（会脱离原套间且不能证明 RequestLock 已返回）。
            lock (_stateGate)
            {
                _flushScheduled = false;
            }

            Trace("notify", "flush-pending-no-dispatcher", null);
            return;
        }

        try
        {
            dispatcher(() => FlushDeferredNotifications());
            Trace("notify", "flush-posted", null);
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                _flushScheduled = false;
            }

            Trace("notify", "flush-post-failed", ExceptionFields(ex));
        }
    }

    /// <summary>
    /// 锁排空达到重入上限时，把后续 pending grant 投递到宿主安全阶段；无 dispatcher 时保留 pending
    /// 由调用方在同一线程调用 <see cref="ContinuePendingLocks"/>。
    /// </summary>
    private void ScheduleLockContinuation()
    {
        lock (_stateGate)
        {
            if (_lockContinuationScheduled || _locks.PendingCount == 0)
            {
                return;
            }

            _lockContinuationScheduled = true;
        }

        var dispatcher = NotificationDispatcher;
        if (dispatcher is null)
        {
            lock (_stateGate)
            {
                _lockContinuationScheduled = false;
            }

            Trace("lock", "continuation-pending-no-dispatcher", null);
            return;
        }

        try
        {
            dispatcher(ContinuePendingLocks);
            Trace("lock", "continuation-posted", null);
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                _lockContinuationScheduled = false;
            }

            Trace("lock", "continuation-post-failed", ExceptionFields(ex));
        }
    }

    /// <summary>在锁外安全阶段继续授予已承诺的 pending 读/写升级；仍有 pending 时再次调度。</summary>
    internal void ContinuePendingLocks()
    {
        lock (_stateGate)
        {
            _lockContinuationScheduled = false;
        }

        if (_locks.LockType != 0)
        {
            ScheduleLockContinuation();
            return;
        }

        var remaining = _locks.DrainPending();
        if (remaining)
        {
            ScheduleLockContinuation();
        }
    }

    /// <summary>Unadvise/关闭：丢弃尚未发送的通知与已排队的锁请求，绝不回调旧 sink。</summary>
    internal void CancelPendingNotifications()
    {
        lock (_stateGate)
        {
            _deferredNotifications.Clear();
            _flushScheduled = false;
            _lockContinuationScheduled = false;
        }

        _locks.ClearPending();
    }

    // -----------------------------------------------------------------------
    // ITextStoreACP
    // -----------------------------------------------------------------------

    public int AdviseSink(Guid riid, object punk, uint dwMask)
    {
        return Guard(nameof(AdviseSink), () =>
        {
            if (riid != TsfIids.TextStoreAcpSink)
            {
                Trace("store", "advise-wrong-iid", new Dictionary<string, object?>
                {
                    ["riid"] = riid.ToString(),
                });
                return TsfHResult.TS_E_NOINTERFACE;
            }

            if (punk is not ITextStoreACPSink sink)
            {
                Trace("store", "advise-not-sink", null);
                return TsfHResult.E_NOINTERFACE;
            }

            lock (_stateGate)
            {
                if (_sink is not null)
                {
                    Trace("store", "advise-already-advised", null);
                    return TsfHResult.E_INVALIDARG;
                }

                _sink = sink;
                _sinkPunk = punk;
                _sinkMask = dwMask;
                _locks.HasSink = true;
            }

            Trace("store", "advise", new Dictionary<string, object?>
            {
                ["mask"] = $"0x{dwMask:X}",
                ["iid"] = "ITextStoreACPSink",
            });
            return TsfHResult.S_OK;
        });
    }

    public int UnadviseSink(object punk)
    {
        return Guard(nameof(UnadviseSink), () =>
        {
            bool removed;
            lock (_stateGate)
            {
                removed = _sink is not null && SameComIdentity(punk, _sinkPunk);
                if (removed)
                {
                    _sink = null;
                    _sinkPunk = null;
                    _sinkMask = 0;
                    _locks.HasSink = false;
                    _deferredNotifications.Clear();
                    _flushScheduled = false;
                    _lockContinuationScheduled = false;
                    _layoutCreatedNotified = false;
                    _locks.ClearPending();
                }
            }

            Trace("store", "unadvise", new Dictionary<string, object?>
            {
                ["matched"] = removed,
            });
            return removed ? TsfHResult.S_OK : TsfHResult.E_INVALIDARG;
        });
    }

    private static bool SameComIdentity(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr left = IntPtr.Zero;
        IntPtr right = IntPtr.Zero;
        try
        {
            left = Marshal.GetIUnknownForObject(a);
            right = Marshal.GetIUnknownForObject(b);
            return left == right;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (left != IntPtr.Zero)
            {
                Marshal.Release(left);
            }

            if (right != IntPtr.Zero)
            {
                Marshal.Release(right);
            }
        }
    }

    public int RequestLock(uint dwLockFlags, out int phrSession)
    {
        // 诊断：请求进入/返回各一条，带方法 HRESULT 与 *phrSession（授予结果另由 lock-granted 记录）。
        // 未启用诊断时 BeginLockRequest 仍返回单调 ID，但 EnterLockRequest/ExitLockRequest 立即返回，
        // 不改变任何锁逻辑与通知时序。
        var requestId = _chain.BeginLockRequest();
        if (_chain.Enabled)
        {
            _chain.EnterLockRequest(requestId);
            Trace("lock", "request-lock-enter", new Dictionary<string, object?>
            {
                ["flags"] = $"0x{dwLockFlags:X}",
                ["diag_lock_request_id"] = requestId,
            });
        }

        var session = 0;
        int hr;
        try
        {
            hr = Guard(nameof(RequestLock), () => _locks.RequestLock(dwLockFlags, out session));
            phrSession = session;

            if (_chain.Enabled)
            {
                Trace("lock", "request-lock-return", new Dictionary<string, object?>
                {
                    ["flags"] = $"0x{dwLockFlags:X}",
                    ["hresult"] = TsfHResult.Describe(hr),
                    ["session_hresult"] = TsfHResult.Describe(session),
                    ["diag_lock_request_id"] = requestId,
                    ["outcome"] = hr >= 0 ? "ok" : "failed",
                });
            }
        }
        catch (Exception ex)
        {
            // Guard 已兜住锁实现异常；这里只保证诊断栈在异常路径同样恢复。
            phrSession = TsfHResult.E_FAIL;
            Trace("lock", "request-lock-exception", ExceptionFields(ex));
            throw;
        }
        finally
        {
            // 无论正常返回、失败 HRESULT 还是异常，都恰好出栈一次，保留外层请求 ID。
            if (_chain.Enabled)
            {
                _chain.ExitLockRequest();
            }
        }

        // 同步授予在 _locks.RequestLock 返回前已释放；这里只调度，绝不在此调用 sink 通知。
        ScheduleFlush();
        if (_locks.PendingCount > 0)
        {
            // 排空达到重入上限时仍有已承诺的 pending 读/写升级：投递到安全阶段继续 grant。
            ScheduleLockContinuation();
        }

        return hr;
    }

    public int GetStatus(out TsStatus pdcs)
    {
        pdcs = new TsStatus
        {
            // 非只读、非加载；静态位：单选区、无 region（TS_SS_DISJOINTSEL/TS_SS_REGIONS 均不置位）。
            DynamicFlags = 0,
            StaticFlags = 0,
        };
        return TsfHResult.S_OK;
    }

    public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch,
        out int pacpResultStart, out int pacpResultEnd)
    {
        var resultStart = 0;
        var resultEnd = 0;
        var hr = Guard(nameof(QueryInsert), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (!_document.IsValidRange(acpTestStart, acpTestEnd) || cch > int.MaxValue)
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            resultStart = acpTestStart;
            resultEnd = acpTestEnd;
            return TsfHResult.S_OK;
        });

        pacpResultStart = resultStart;
        pacpResultEnd = resultEnd;
        return hr;
    }

    public int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
    {
        var fetched = 0u;
        var hr = Guard(nameof(GetSelection), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (ulCount == 0)
            {
                return TsfHResult.S_OK;
            }

            if (ulIndex != 0 && ulIndex != TsfFlags.TS_DEFAULT_SELECTION)
            {
                return TsfHResult.S_OK;
            }

            var (start, end) = _document.Selection;
            var selection = new TsSelectionAcp
            {
                AcpStart = start,
                AcpEnd = end,
                Style = new TsSelectionStyle
                {
                    ActiveSelEnd = start == end ? TsfFlags.TS_AE_NONE : TsfFlags.TS_AE_END,
                    InterimChar = 0,
                },
            };

            if (pSelection != IntPtr.Zero)
            {
                Marshal.StructureToPtr(selection, pSelection, false);
                fetched = 1;
            }

            return TsfHResult.S_OK;
        });

        pcFetched = fetched;
        return hr;
    }

    public int SetSelection(uint ulCount, IntPtr pSelection)
    {
        return Guard(nameof(SetSelection), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (ulCount != 1 || pSelection == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            var selection = Marshal.PtrToStructure<TsSelectionAcp>(pSelection);
            lock (_stateGate)
            {
                if (!_document.TrySetSelection(selection.AcpStart, selection.AcpEnd))
                {
                    return TsfHResult.TS_E_INVALIDPOS;
                }

                Trace("store", "set-selection", RangeFields(selection.AcpStart, selection.AcpEnd));
                // 响应 SetSelection 时不回送 OnSelectionChange（官方约定）。
                return TsfHResult.S_OK;
            }
        });
    }

    public int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq,
        out uint pcchPlainRet, IntPtr prgRunInfo, uint cRunInfoReq,
        out uint pcRunInfoRet, out int pacpNext)
    {
        var plainRet = 0u;
        var runRet = 0u;
        var next = acpStart;
        var hr = Guard(nameof(GetText), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            // 明确拒绝“非零容量 + 空缓冲区”，不报告写入了不存在的缓冲区。
            if (cchPlainReq > 0 && pchPlain == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            if (cRunInfoReq > 0 && prgRunInfo == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            var end = acpEnd == -1 ? _document.Length : acpEnd;
            if (!_document.IsValidRange(acpStart, end))
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            var available = end - acpStart;
            var plainCount = cchPlainReq == 0
                ? 0
                : (int)Math.Min(cchPlainReq, (uint)available);
            if (plainCount > 0)
            {
                var chars = _document.GetText(acpStart, acpStart + plainCount).ToCharArray();
                Marshal.Copy(chars, 0, pchPlain, plainCount);
            }

            plainRet = (uint)plainCount;

            // run-only 调用（pchPlain=NULL,cchPlainReq=0）必须覆盖请求范围并推进 pacpNext，
            // 否则调用方无法扫描文档。
            var runChars = cchPlainReq == 0 ? available : plainCount;
            if (cRunInfoReq > 0 && runChars > 0)
            {
                var run = new TsRunInfo
                {
                    Count = (uint)runChars,
                    Type = TsRunType.Plain,
                };
                Marshal.StructureToPtr(run, prgRunInfo, false);
                runRet = 1;
            }

            next = acpStart + runChars;
            return TsfHResult.S_OK;
        });

        pcchPlainRet = plainRet;
        pcRunInfoRet = runRet;
        pacpNext = next;
        return hr;
    }

    public int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch,
        out TsTextChange pChange)
    {
        var change = default(TsTextChange);
        var hr = Guard(nameof(SetText), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (cch > int.MaxValue || (cch > 0 && pchText == IntPtr.Zero))
            {
                return TsfHResult.E_INVALIDARG;
            }

            var text = cch == 0 ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch) ?? string.Empty;
            lock (_stateGate)
            {
                var flags = (dwFlags & TsfFlags.TS_ST_CORRECTION) != 0 ? TsfFlags.TS_TC_CORRECTION : 0u;
                var result = ApplyChangeLocked(acpStart, acpEnd, text, AcpEditOrigin.Tsf, flags);
                if (result >= 0)
                {
                    change = new TsTextChange
                    {
                        AcpStart = acpStart,
                        AcpOldEnd = acpEnd,
                        AcpNewEnd = acpStart + text.Length,
                    };
                }

                return result;
            }
        });

        pChange = change;
        return hr;
    }

    public int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject)
    {
        ppDataObject = IntPtr.Zero;
        Trace("store", "unsupported", new Dictionary<string, object?> { ["method"] = nameof(GetFormattedText) });
        return TsfHResult.E_NOTIMPL;
    }

    public int GetEmbedded(int acpPos, IntPtr rguidService, IntPtr riid, out IntPtr ppunk)
    {
        ppunk = IntPtr.Zero;
        Trace("store", "unsupported", new Dictionary<string, object?> { ["method"] = nameof(GetEmbedded) });
        return TsfHResult.TS_E_NOOBJECT;
    }

    public int QueryInsertEmbedded(IntPtr pguidService, IntPtr pFormatEtc, out int pfInsertable)
    {
        pfInsertable = 0;
        Trace("store", "unsupported-explicit", new Dictionary<string, object?>
        {
            ["method"] = nameof(QueryInsertEmbedded),
            ["insertable"] = false,
        });
        return TsfHResult.S_OK;
    }

    public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, object pDataObject,
        out TsTextChange pChange)
    {
        pChange = default;
        Trace("store", "unsupported", new Dictionary<string, object?> { ["method"] = nameof(InsertEmbedded) });
        return TsfHResult.E_NOTIMPL;
    }

    public int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
        IntPtr pacpStart, IntPtr pacpEnd, IntPtr pChange)
    {
        return Guard(nameof(InsertTextAtSelection), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (cch > int.MaxValue || (cch > 0 && pchText == IntPtr.Zero))
            {
                return TsfHResult.E_INVALIDARG;
            }

            lock (_stateGate)
            {
                var (start, end) = _document.Selection;

                if ((dwFlags & TsfFlags.TS_IAS_QUERYONLY) != 0)
                {
                    WriteOptionalInt32(pacpStart, start);
                    WriteOptionalInt32(pacpEnd, end);
                    Trace("store", "insert-query-only", RangeFields(start, end));
                    return TsfHResult.S_OK;
                }

                var text = cch == 0 ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch) ?? string.Empty;
                var hr = ApplyChangeLocked(start, end, text, AcpEditOrigin.Tsf, 0);
                if (hr < 0)
                {
                    return hr;
                }

                // 插入后把选区移到插入文本末尾，后续预编辑/普通字符才不会反向堆叠。
                // 这是响应 InsertTextAtSelection 的内部选区调整，不回送 OnSelectionChange。
                _document.TrySetSelection(start + text.Length, start + text.Length);

                WriteOptionalInt32(pacpStart, start);
                WriteOptionalInt32(pacpEnd, start + text.Length);
                if (pChange != IntPtr.Zero)
                {
                    var change = new TsTextChange
                    {
                        AcpStart = start,
                        AcpOldEnd = end,
                        AcpNewEnd = start + text.Length,
                    };
                    Marshal.StructureToPtr(change, pChange, false);
                }

                return TsfHResult.S_OK;
            }
        });
    }

    public int InsertEmbeddedAtSelection(uint dwFlags, object pDataObject, IntPtr pacpStart,
        IntPtr pacpEnd, IntPtr pChange)
    {
        Trace("store", "unsupported", new Dictionary<string, object?>
        {
            ["method"] = nameof(InsertEmbeddedAtSelection),
        });
        return TsfHResult.E_NOTIMPL;
    }

    public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs)
        => Unsupported(nameof(RequestSupportedAttrs));

    public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags)
        => Unsupported(nameof(RequestAttrsAtPosition));

    public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags)
        => Unsupported(nameof(RequestAttrsTransitioningAtPosition));

    public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags, out int pacpNext, out int pfFound, out int plFoundOffset)
    {
        pacpNext = 0;
        pfFound = 0;
        plFoundOffset = 0;
        return Unsupported(nameof(FindNextAttrTransition));
    }

    public int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched)
    {
        pcFetched = 0;
        return TsfHResult.S_OK;
    }

    public int GetEndACP(out int pacp)
    {
        var end = 0;
        var hr = Guard(nameof(GetEndACP), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            end = _document.Length;
            return TsfHResult.S_OK;
        });

        pacp = end;
        return hr;
    }

    public int GetActiveView(out uint pvcView)
    {
        pvcView = _viewCookie;
        return TsfHResult.S_OK;
    }

    public int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp)
    {
        var result = 0;
        var hr = Guard(nameof(GetACPFromPoint), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                TraceGeometryRejected(nameof(GetACPFromPoint), lockHr, vcView);
                return lockHr;
            }

            if (vcView != _viewCookie)
            {
                TraceGeometryRejected(nameof(GetACPFromPoint), TsfHResult.TF_E_INVALIDVIEW, vcView);
                return TsfHResult.TF_E_INVALIDVIEW;
            }

            if (ptScreen == IntPtr.Zero)
            {
                TraceGeometryRejected(nameof(GetACPFromPoint), TsfHResult.TS_E_INVALIDPOINT, vcView);
                return TsfHResult.TS_E_INVALIDPOINT;
            }

            if (!TryGetCurrentLayout(out var layout))
            {
                TraceGeometryRejected(nameof(GetACPFromPoint), TsfHResult.TS_E_NOLAYOUT, vcView);
                return TsfHResult.TS_E_NOLAYOUT;
            }

            var point = Marshal.PtrToStructure<TsfPoint>(ptScreen);
            if (!layout.TryHitTest(point, dwFlags, out var acp))
            {
                TraceGeometryRejected(nameof(GetACPFromPoint), TsfHResult.TS_E_INVALIDPOINT, vcView);
                return TsfHResult.TS_E_INVALIDPOINT;
            }

            result = acp;
            Trace("geometry", "acp-from-point", new Dictionary<string, object?>
            {
                ["x"] = point.X,
                ["y"] = point.Y,
                ["flags"] = $"0x{dwFlags:X}",
                ["acp"] = acp,
                ["lock_type"] = $"0x{_locks.LockType:X}",
                ["layout_notify_depth"] = _layoutNotifyDepth,
            });
            return TsfHResult.S_OK;
        });

        pacp = result;
        return hr;
    }

    public int GetTextExt(uint vcView, int acpStart, int acpEnd, out TsfRect prc, out int pfClipped)
    {
        var rectResult = default(TsfRect);
        var clippedResult = 0;
        var hr = Guard(nameof(GetTextExt), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                TraceGeometryRejected(nameof(GetTextExt), lockHr, vcView, acpStart, acpEnd);
                return lockHr;
            }

            if (vcView != _viewCookie)
            {
                TraceGeometryRejected(nameof(GetTextExt), TsfHResult.TF_E_INVALIDVIEW, vcView,
                    acpStart, acpEnd);
                return TsfHResult.TF_E_INVALIDVIEW;
            }

            var end = acpEnd == -1 ? _document.Length : acpEnd;
            if (!_document.IsValidRange(acpStart, end) || end < acpStart)
            {
                TraceGeometryRejected(nameof(GetTextExt), TsfHResult.TS_E_INVALIDPOS, vcView,
                    acpStart, acpEnd);
                return TsfHResult.TS_E_INVALIDPOS;
            }

            if (!TryGetCurrentLayout(out var layout))
            {
                TraceGeometryRejected(nameof(GetTextExt), TsfHResult.TS_E_NOLAYOUT, vcView,
                    acpStart, acpEnd);
                return TsfHResult.TS_E_NOLAYOUT;
            }

            if (!layout.TryGetRangeRect(acpStart, end, out var rect, out var clipped))
            {
                // 官方语义：已布局但当前完全不可见（含最小化）时返回 S_OK + 全零 RECT，
                // 不能返回 E_FAIL；失效/未布局仍由上面的 TS_E_NOLAYOUT 区分。
                rectResult = default;
                clippedResult = 1;
                Trace("geometry", "text-ext-fully-invisible-zero-rect", RangeFields(acpStart, end));
                return TsfHResult.S_OK;
            }

            rectResult = rect;
            clippedResult = clipped ? 1 : 0;
            Trace("geometry", "text-ext", new Dictionary<string, object?>
            {
                ["start"] = acpStart,
                ["end"] = end,
                ["rect"] = rect.ToString(),
                ["clipped"] = clipped,
                ["generation"] = layout.LayoutGeneration,
                ["document_version"] = layout.DocumentVersion,
                ["lock_type"] = $"0x{_locks.LockType:X}",
                ["layout_notify_depth"] = _layoutNotifyDepth,
            });
            return TsfHResult.S_OK;
        });

        prc = rectResult;
        pfClipped = clippedResult;
        return hr;
    }

    // -----------------------------------------------------------------------
    // GetScreenExt：显示表面查询（独立于文档读锁与文本布局版本）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 返回显示表面的屏幕矩形。
    /// <para>
    /// 契约依据（R24）：官方 Windows-classic-samples `tsfapp/textstor.cpp` 的
    /// `CTSFEditWnd::GetScreenExt` 明确写有 “no lock is necessary for this method.”，
    /// 直接取客户区并映射到屏幕坐标；非法 view 返回 `E_INVALIDARG`；
    /// 最小化返回零矩形。因此本方法**不**要求读锁，也**不**依赖文本布局版本。
    /// </para>
    /// <para>
    /// 只读：不排版、不发通知、不 `SetFocus`、不申请文档锁。
    /// 未显示/最小化 → `S_OK` + 零矩形；原生查询失败 → 负 HRESULT + 零矩形（绝不用旧坐标伪装成功）。
    /// </para>
    /// </summary>
    public int GetScreenExt(uint vcView, out TsfRect prc)
    {
        var rectResult = default(TsfRect);
        var hr = Guard(nameof(GetScreenExt), () =>
        {
            // 非法 view：官方为 E_INVALIDARG（不是 TF_E_INVALIDVIEW），且输出已清零。
            if (vcView != _viewCookie)
            {
                TraceGeometryRejected(nameof(GetScreenExt), TsfHResult.E_INVALIDARG, vcView);
                return TsfHResult.E_INVALIDARG;
            }

            var surface = ResolveDisplaySurface();
            if (!surface.Ok)
            {
                // 原生查询失败：输出零矩形并如实返回失败 HRESULT，不沿用任何旧坐标。
                rectResult = default;
                TraceGeometryRejected(nameof(GetScreenExt), surface.HResult, vcView);
                Trace("geometry", "screen-ext-failed", new Dictionary<string, object?>
                {
                    ["hresult"] = TsfHResult.Describe(surface.HResult),
                    ["failure"] = surface.Failure,
                    ["window_available"] = surface.WindowAvailable,
                    ["lock_type"] = $"0x{_locks.LockType:X}",
                    ["layout_notify_depth"] = _layoutNotifyDepth,
                });
                return surface.HResult;
            }

            rectResult = surface.Rect;
            Trace("geometry", "screen-ext", new Dictionary<string, object?>
            {
                ["rect"] = rectResult.ToString(),
                // 表面独立于文本布局：不报告布局代数，避免把表面误读为布局结果。
                ["surface_only"] = true,
                ["window_available"] = surface.WindowAvailable,
                ["hidden_or_minimized"] = surface.Rect.IsEmpty,
                ["failure"] = surface.Failure,
                ["lock_type"] = $"0x{_locks.LockType:X}",
                ["layout_notify_depth"] = _layoutNotifyDepth,
            });
            return TsfHResult.S_OK;
        });

        prc = rectResult;
        return hr;
    }

    /// <summary>
    /// 取当前显示表面：优先注入的 provider（单元测试），否则查真实 HWND，
    /// 最后退回独立表面快照。查询本身只读，不触碰布局版本或锁。
    /// </summary>
    private DisplaySurfaceResult ResolveDisplaySurface()
    {
        if (DisplaySurfaceProvider is { } provider)
        {
            var injected = provider();
            // 注入结果也统一清零语义：失败或不可见时矩形必须是零。
            return injected.Ok
                ? injected
                : injected with { Rect = default };
        }

        IntPtr hwnd;
        DisplaySurfaceResult snapshot;
        lock (_stateGate)
        {
            hwnd = _hwnd;
            snapshot = _surfaceSnapshot;
        }

        if (hwnd != IntPtr.Zero)
        {
            // 真实 HWND：以当前客户区屏幕坐标为准，绝不用过期快照。
            return DisplaySurface.FromWindow(hwnd);
        }

        // 无真实窗口（纯托管测试/隐藏宿主早期阶段）：退回独立表面快照。
        return snapshot;
    }

    /// <summary>
    /// 几何查询被拒绝时也必须留痕：否则 JSONL 无法区分“查询读到新矩形”与
    /// “查询被 TS_E_NOLOCK / TS_E_NOLAYOUT / 非法 view 拒绝后调用方沿用旧位置”。
    /// 字段包含锁类型、view、布局代数/文档版本与 OnLayoutChange 重入深度。
    /// 注意：调用者身份不在此断定——位于 OnLayoutChange 同步回调内只说明是 TSF 层回调，
    /// 不证明是 TIP 或微软拼音发起的查询，也不证明候选窗因此未移动。
    /// </summary>
    private void TraceGeometryRejected(string method, int hr, uint vcView,
        int acpStart = -1, int acpEnd = -1)
    {
        Trace("geometry", "rejected", new Dictionary<string, object?>
        {
            ["method"] = method,
            ["hresult"] = TsfHResult.Describe(hr),
            ["view"] = vcView,
            ["expected_view"] = _viewCookie,
            ["lock_type"] = $"0x{_locks.LockType:X}",
            ["layout_generation"] = _layoutGeneration,
            ["document_version"] = _document.Version,
            ["layout_notify_depth"] = _layoutNotifyDepth,
            ["start"] = acpStart,
            ["end"] = acpEnd,
        });
    }

    public int GetWnd(uint vcView, out IntPtr phwnd)
    {
        phwnd = _hwnd;
        return vcView == _viewCookie ? TsfHResult.S_OK : TsfHResult.TF_E_INVALIDVIEW;
    }

    // -----------------------------------------------------------------------
    // 内部
    // -----------------------------------------------------------------------

    private int RequireRead() => _locks.IsLocked(TsfFlags.TS_LF_READ) ? TsfHResult.S_OK : TsfHResult.TS_E_NOLOCK;

    private int RequireWrite() => _locks.IsWriteLocked ? TsfHResult.S_OK : TsfHResult.TS_E_NOLOCK;

    private int GrantSinkLock(uint flags)
    {
        ITextStoreACPSink? sink;
        lock (_stateGate)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return TsfHResult.E_FAIL;
        }

        try
        {
            return sink.OnLockGranted(flags);
        }
        catch (Exception ex)
        {
            Trace("lock", "sink-onlockgranted-exception", ExceptionFields(ex));
            return TsfHResult.E_FAIL;
        }
    }

    private int Unsupported(string method)
    {
        Trace("store", "unsupported", new Dictionary<string, object?> { ["method"] = method });
        return TsfHResult.E_NOTIMPL;
    }

    private int Guard(string method, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            Trace("store", "exception", new Dictionary<string, object?>
            {
                ["method"] = method,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return TsfHResult.E_FAIL;
        }
    }

    private static void WriteOptionalInt32(IntPtr target, int value)
    {
        if (target != IntPtr.Zero)
        {
            Marshal.WriteInt32(target, value);
        }
    }

    private static Dictionary<string, object?> RangeFields(int start, int end) => new()
    {
        ["start"] = start,
        ["end"] = end,
    };

    private static Dictionary<string, object?> ExceptionFields(Exception ex) => new()
    {
        ["exception"] = ex.GetType().Name,
        ["message"] = ex.Message,
    };

    private void Trace(string stage, string kind, IDictionary<string, object?>? fields)
    {
        try
        {
            // 集中增强：启用 --trace-layout-chain 时，所有既有事件（含 fully-invisible-zero-rect、
            // GetWnd 等未逐处改动的路径）自动带上 diag_scope/diag_notification_id/diag_lock_request_id；
            // 未启用时原样透传，旧字段完全兼容。
            _trace?.Invoke(stage, kind, _chain.Enrich(fields));
        }
        catch (Exception)
        {
            // 日志失败不影响协议。
        }
    }
}
