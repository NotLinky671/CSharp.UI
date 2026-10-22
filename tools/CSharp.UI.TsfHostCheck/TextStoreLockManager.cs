namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// ACP 锁状态机。语义按 Windows SDK TextStor.idl 与 TSF 文档：
/// 未持锁时同步/异步请求都立即授予；持锁时同步请求返回 TS_E_SYNCHRONOUS、异步入队，
/// 队列在最外层授予释放后逐个排空。授予期间的同步重入同样拒绝，异步重入入队。
/// 请求位（含 TS_LF_SYNC）与授予位（仅 TS_LF_READ/READWRITE）按 SDK 契约分开处理；
/// 该区分是协议一致性要求，不是任何原生崩溃的已证明根因。
/// </summary>
internal sealed class TextStoreLockManager
{
    private const int MaxDrainIterations = 16;

    private readonly object _gate = new();
    private readonly Func<uint, int> _grant;
    private readonly TsfTrace? _trace;
    private readonly LayoutChainTrace _chain;
    private uint _lockType;
    private long _session;
    private bool _pendingRead;
    private bool _pendingWrite;

    internal TextStoreLockManager(Func<uint, int> grant, TsfTrace? trace)
        : this(grant, trace, LayoutChainTrace.Disabled)
    {
    }

    /// <summary>
    /// 带诊断的构造：<paramref name="chain"/> 只用于给既有锁事件补充归因字段，
    /// 不参与任何锁判定、授予顺序或时序决策。
    /// </summary>
    internal TextStoreLockManager(Func<uint, int> grant, TsfTrace? trace, LayoutChainTrace chain)
    {
        _grant = grant;
        _trace = trace;
        _chain = chain ?? LayoutChainTrace.Disabled;
    }

    internal bool HasSink { get; set; }

    internal uint LockType
    {
        get
        {
            lock (_gate)
            {
                return _lockType;
            }
        }
    }

    /// <summary>有界 pending：读、写升级各最多一个请求。</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return (_pendingRead ? 1 : 0) + (_pendingWrite ? 1 : 0);
            }
        }
    }

    internal bool HasPendingWriteUpgrade
    {
        get
        {
            lock (_gate)
            {
                return _pendingWrite;
            }
        }
    }

    internal long LastSession
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    internal bool IsLocked(uint required)
    {
        lock (_gate)
        {
            return (_lockType & required) == required;
        }
    }

    internal bool IsWriteLocked
    {
        get
        {
            lock (_gate)
            {
                return (_lockType & TsfFlags.TS_LF_READWRITE) == TsfFlags.TS_LF_READWRITE;
            }
        }
    }

    /// <summary>RequestLock 的方法 HRESULT；<paramref name="sessionHr"/> 是 *phrSession。</summary>
    internal int RequestLock(uint flags, out int sessionHr)
    {
        lock (_gate)
        {
            if (!HasSink)
            {
                sessionHr = TsfHResult.E_FAIL;
                return TsfHResult.S_OK;
            }

            // 严格白名单：只允许 SYNC/READ/WRITE 位，且必须包含 READ；
            // READ|0x100、0x4（只有写位）等未知或非法组合一律拒绝，不能授予“看起来有效”的读锁。
            if ((flags & ~TsfFlags.TS_LF_ALLOWED) != 0 || (flags & TsfFlags.TS_LF_READ) == 0)
            {
                sessionHr = TsfHResult.E_INVALIDARG;
                Trace("request-lock-invalid-flags", new Dictionary<string, object?>
                {
                    ["flags"] = $"0x{flags:X}",
                    ["allowed"] = $"0x{TsfFlags.TS_LF_ALLOWED:X}",
                });
                return TsfHResult.S_OK;
            }

            if (_lockType != 0)
            {
                if ((flags & TsfFlags.TS_LF_SYNC) != 0)
                {
                    sessionHr = TsfHResult.TS_E_SYNCHRONOUS;
                    Trace("request-lock-sync-rejected", new Dictionary<string, object?>
                    {
                        ["flags"] = $"0x{flags:X}",
                        ["held"] = $"0x{_lockType:X}",
                        ["pending_read"] = _pendingRead,
                        ["pending_write"] = _pendingWrite,
                    });
                    // 失败的同步请求不清除已排队的异步写升级。
                    return TsfHResult.S_OK;
                }

                QueueLocked(flags);
                sessionHr = TsfHResult.TS_S_ASYNC;
                return TsfHResult.S_OK;
            }

            sessionHr = GrantLocked(flags);
            DrainLocked();
            return TsfHResult.S_OK;
        }
    }

    private void QueueLocked(uint flags)
    {
        var wantsWrite = (flags & TsfFlags.TS_LF_WRITE) != 0;
        if (wantsWrite)
        {
            // 读锁上的异步写升级：写锁包含读，合并掉 pending read。
            _pendingWrite = true;
            _pendingRead = false;
        }
        else if (!_pendingWrite)
        {
            _pendingRead = true;
        }

        Trace("request-lock-merged", new Dictionary<string, object?>
        {
            ["flags"] = $"0x{flags:X}",
            ["pending_read"] = _pendingRead,
            ["pending_write"] = _pendingWrite,
        });
    }

    private void DrainLocked()
    {
        var iterations = 0;
        while (_pendingRead || _pendingWrite)
        {
            if (++iterations > MaxDrainIterations)
            {
                Trace("request-lock-drain-limit", new Dictionary<string, object?>
                {
                    ["pending_read"] = _pendingRead,
                    ["pending_write"] = _pendingWrite,
                });
                return;
            }

            if (_pendingWrite)
            {
                _pendingWrite = false;
                _pendingRead = false;
                GrantLocked(TsfFlags.TS_LF_READWRITE);
            }
            else
            {
                _pendingRead = false;
                GrantLocked(TsfFlags.TS_LF_READ);
            }
        }
    }

    /// <summary>
    /// 在锁外安全阶段继续排空 pending；返回是否仍有未授予的读/写升级。
    /// 授予过程可能重入 RequestLock 再次入队，因此调用方应按返回值继续调度。
    /// </summary>
    internal bool DrainPending()
    {
        lock (_gate)
        {
            if (_lockType != 0)
            {
                return _pendingRead || _pendingWrite;
            }

            DrainLocked();
            return _pendingRead || _pendingWrite;
        }
    }

    /// <summary>Unadvise/关闭：丢弃尚未授予的 pending 请求，避免回调已移除的 sink。</summary>
    internal void ClearPending()
    {
        lock (_gate)
        {
            _pendingRead = false;
            _pendingWrite = false;
        }
    }

    private int GrantLocked(uint flags)
    {
        _lockType = flags & TsfFlags.TS_LF_READWRITE;
        _session++;
        var session = _session;
        Trace("lock-granted", new Dictionary<string, object?>
        {
            ["flags"] = $"0x{flags:X}",
            ["lock_type"] = $"0x{_lockType:X}",
            ["session"] = session,
            ["win32_thread_id"] = ImeProbe.NativeMethods.CurrentWin32ThreadId(),
        });

        try
        {
            // SDK 契约：ITextStoreACPSink::OnLockGranted 的 dwLockFlags 是**授予的锁类型**
            // （TS_LF_READ / TS_LF_READWRITE），TS_LF_SYNC 只是 RequestLock 的请求位，不属于授予位。
            // 因此这里按授予类型（_lockType，0x2/0x6）回传，绝不把请求 flags 原样（例如 0x3）交给 TSF。
            // 说明：这只是请求位/授予位的一致性修正，本身不等于任何原生崩溃的根因；
            // 真实只读会话崩溃（R21）的根因是 RequestEditSession 的 ITfEditSession 参数封送，另行修复。
            return _grant(_lockType);
        }
        catch (Exception ex)
        {
            Trace("lock-grant-exception", new Dictionary<string, object?>
            {
                ["session"] = session,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return TsfHResult.E_FAIL;
        }
        finally
        {
            _lockType = 0;
            Trace("lock-released", new Dictionary<string, object?>
            {
                ["session"] = session,
            });
        }
    }

    private void Trace(string kind, IDictionary<string, object?>? fields)
    {
        try
        {
            _trace?.Invoke("lock", kind, _chain.Enrich(fields));
        }
        catch (Exception)
        {
            // 追踪失败不影响锁协议。
        }
    }
}
