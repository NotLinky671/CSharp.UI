namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// ACP 锁状态机（TI-07 P2b）：读/读写锁、同步重入拒绝、有界合并的异步升级与公平排空。
/// </summary>
/// <remarks>
/// <para>
/// <b>语义（Windows SDK <c>TextStor.idl</c> 与 TSF 文档）：</b>
/// 未持锁时同步/异步请求都立即授予；持锁时同步请求把 <c>*phrSession</c> 置为
/// <see cref="TsfHResult.TS_E_SYNCHRONOUS"/>（方法本身仍返回 S_OK），异步入队并返回
/// <see cref="TsfHResult.TS_S_ASYNC"/>。队列在最外层授予释放后逐个排空。
/// </para>
/// <para>
/// <b>请求位 vs 授予位：</b><see cref="TsfFlags.TS_LF_SYNC"/> 只是请求位，绝不进入授予类型；
/// <c>OnLockGranted</c> 收到的始终是 <c>TS_LF_READ</c> 或 <c>TS_LF_READWRITE</c>。
/// </para>
/// <para>
/// <b>有界：</b>pending 至多一个读 + 一个写升级（写包含读，因此写请求合并掉 pending 读）；
/// 单次排空最多 <see cref="MaxDrainIterations"/> 轮，超出即交回调用方在安全阶段继续，避免无限递归。
/// </para>
/// <para>
/// <b>线程：</b>授予与回调都发生在调用 <see cref="RequestLock"/> 的线程上（UI 所有者线程）。
/// 本类型不创建线程、不排队到线程池、不使用计时器。
/// </para>
/// </remarks>
internal sealed class TextStoreLockManager
{
    /// <summary>单次排空允许的最大授予轮数（有界重入保护）。</summary>
    internal const int MaxDrainIterations = 16;

    private readonly object _gate = new();
    private readonly Func<uint, int> _grant;

    private uint _lockType;
    private long _session;
    private bool _pendingRead;
    private bool _pendingWrite;

    /// <param name="grant">授予回调：参数为<b>授予类型</b>（READ/READWRITE），返回 sink 的 HRESULT。</param>
    internal TextStoreLockManager(Func<uint, int> grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grant = grant;
    }

    /// <summary>是否已安装 sink；未安装时不授予任何锁。</summary>
    internal bool HasSink { get; set; }

    /// <summary>当前持有的锁类型（0 表示未持锁）。</summary>
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

    /// <summary>未授予的 pending 请求数量（读、写各至多一个）。</summary>
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

    /// <summary>是否存在待授予的写升级。</summary>
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

    /// <summary>已授予次数（单调会话计数）。</summary>
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

    /// <summary>是否持有满足 <paramref name="required"/> 的锁。</summary>
    internal bool IsLocked(uint required)
    {
        lock (_gate)
        {
            return (_lockType & required) == required;
        }
    }

    /// <summary>是否持有读写锁（写包含读）。</summary>
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

    /// <summary>
    /// 处理一次 <c>ITextStoreACP::RequestLock</c>。
    /// </summary>
    /// <param name="flags">请求的锁标志。</param>
    /// <param name="sessionHr">输出 <c>*phrSession</c>：授予结果或 <c>TS_E_SYNCHRONOUS</c>/<c>TS_S_ASYNC</c>。</param>
    /// <returns>方法自身的 HRESULT（正常路径恒为 S_OK）。</returns>
    internal int RequestLock(uint flags, out int sessionHr)
    {
        lock (_gate)
        {
            if (!HasSink)
            {
                // 没有 sink 就没有接收者：如实失败，不授予。
                sessionHr = TsfHResult.E_FAIL;
                return TsfHResult.S_OK;
            }

            // 严格白名单：只允许 SYNC/READ/WRITE，且必须包含 READ。
            // READ|0x100、只有写位（0x4）等未知/非法组合一律拒绝，绝不授予“看起来有效”的读锁。
            if ((flags & ~TsfFlags.TS_LF_ALLOWED) != 0 || (flags & TsfFlags.TS_LF_READ) == 0)
            {
                sessionHr = TsfHResult.E_INVALIDARG;
                return TsfHResult.S_OK;
            }

            if (_lockType != 0)
            {
                if ((flags & TsfFlags.TS_LF_SYNC) != 0)
                {
                    // 持锁期间的同步请求：拒绝重入，但<b>不清除</b>已排队的异步升级。
                    sessionHr = TsfHResult.TS_E_SYNCHRONOUS;
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

    /// <summary>合并入队：写升级包含读，因此写请求丢弃 pending 读。</summary>
    private void QueueLocked(uint flags)
    {
        var wantsWrite = (flags & TsfFlags.TS_LF_WRITE) != 0;
        if (wantsWrite)
        {
            _pendingWrite = true;
            _pendingRead = false;
        }
        else if (!_pendingWrite)
        {
            _pendingRead = true;
        }
    }

    /// <summary>有界排空：逐个授予 pending，超过上限即返回由调用方继续。</summary>
    private void DrainLocked()
    {
        var iterations = 0;
        while (_pendingRead || _pendingWrite)
        {
            if (++iterations > MaxDrainIterations)
            {
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
    /// 在锁外安全阶段继续排空 pending；返回是否仍有未授予请求。
    /// </summary>
    /// <remarks>授予期间可能重入 <see cref="RequestLock"/> 再次入队，因此调用方应按返回值继续调度。</remarks>
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

    /// <summary>Unadvise/关闭：丢弃尚未授予的 pending，避免回调已移除的 sink。</summary>
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
        try
        {
            // 授予回调收到的是授予类型（0x2/0x6），绝不是请求 flags（例如 0x3）。
            return _grant(_lockType);
        }
        finally
        {
            _lockType = 0;
        }
    }
}
