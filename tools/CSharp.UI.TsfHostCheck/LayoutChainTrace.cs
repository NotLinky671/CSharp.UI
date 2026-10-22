using System.Collections.Concurrent;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 诊断归因分类。只有确实位于已知应用调用范围内才能标为 probe；
/// 其余只能称 TSF callback 或 unknown，绝不声称识别了具体 TIP 调用者。
/// </summary>
internal static class LayoutChainScope
{
    /// <summary>已知应用探针：只读编辑会话 request-session 阶段。</summary>
    internal const string RequestSession = "request-session";

    /// <summary>已知应用探针：context/view 侧 GetTextExt/GetScreenExt/GetWnd 查询。</summary>
    internal const string ViewQuery = "view-query";

    /// <summary>已知应用探针：探针在会话内直接查询 store。</summary>
    internal const string StoreDirectQuery = "store-direct-query";

    /// <summary>位于 ITextStoreACPSink 回调（含 OnLayoutChange 嵌套）内，但非应用探针。</summary>
    internal const string TsfCallback = "tsf-callback";

    /// <summary>没有任何已知归属：既非应用探针也不在 sink 回调内。</summary>
    internal const string Unknown = "unknown";
}

/// <summary>
/// 默认关闭的布局调用链诊断（`--trace-layout-chain`）。
/// <para>
/// 只增加可观测性：不改变锁逻辑、通知时序、几何返回值或任何文本语义。
/// </para>
/// <para>
/// 隔离边界（R23）：
/// <list type="bullet">
/// <item>通知 ID / 锁请求 ID 的**计数**按 store 递增（同一 store 内单调唯一）；</item>
/// <item>通知/锁/应用阶段的**当前标签栈按真实 OS 线程**保存（以 <c>GetCurrentThreadId</c> 为键），
///   同一 store 在别的线程读到的是该线程自己的栈；新线程从空栈开始；</item>
/// <item>每个 enter 都压栈、每个 exit 都出栈，因此内层退出恢复外层 ID，嵌套异常同样成立；</item>
/// <item>栈严格 LIFO：租约必须在压栈的同一线程上、按逆序释放，且每个租约只释放一次。
///   同名嵌套的重复释放会弹掉外层，不提供幂等保证（见 <see cref="Scope"/>）。</item>
/// </list>
/// 未启用时全部方法立即返回，不执行任何原生查询。
/// </para>
/// </summary>
internal sealed class LayoutChainTrace
{
    private readonly bool _enabled;

    // 计数按 store：Interlocked 保证同 store 内唯一，不需要锁。
    private long _notificationSequence;
    private long _lockRequestSequence;

    /// <summary>
    /// 按**真实 OS 线程 ID** 保存诊断状态。
    /// <para>
    /// 以 <c>GetCurrentThreadId</c> 为键而不是使用托管线程身份：诊断描述的是**原生 UI 线程**上的
    /// 调用嵌套，而托管线程/线程池线程可被复用；原生线程 ID 也不会被任何逻辑流上下文带过线程边界。
    /// </para>
    /// <para>
    /// 说明（勘误）：早先返修报告曾称 <see cref="ThreadLocal{T}"/> 的值会随
    /// <see cref="ExecutionContext"/> 流动——这不正确，会随 <see cref="ExecutionContext"/> 流动的是
    /// <see cref="AsyncLocal{T}"/>。<see cref="ThreadLocal{T}"/> 本就是按线程存储；当前选用
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> + OS 线程键，是为了让“按原生线程隔离”这一语义
    /// 与诊断对象的实际键完全对应，而不是因为已证实 <see cref="ThreadLocal{T}"/> 泄漏。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<uint, ThreadState> _states = new();

    internal LayoutChainTrace(bool enabled) => _enabled = enabled;

    internal static LayoutChainTrace Disabled { get; } = new(false);

    internal bool Enabled => _enabled;

    /// <summary>当前线程最内层已知应用阶段；不在任何应用范围内时为 unknown。</summary>
    internal string CurrentScope => _enabled ? Peek().Scope : LayoutChainScope.Unknown;

    /// <summary>当前线程最内层 OnLayoutChange 的配对 ID；不在任何通知内时为 0。</summary>
    internal long CurrentNotificationId => _enabled ? Peek().NotificationId : 0;

    /// <summary>当前线程最内层 RequestLock 的配对 ID；不在任何锁请求内时为 0。</summary>
    internal long CurrentLockRequestId => _enabled ? Peek().LockRequestId : 0;

    /// <summary>当前线程通知栈深度（诊断用，便于断言嵌套恢复）。</summary>
    internal int NotificationDepth => _enabled ? Peek().NotificationDepth : 0;

    /// <summary>当前线程锁请求栈深度（诊断用，便于断言嵌套恢复）。</summary>
    internal int LockRequestDepth => _enabled ? Peek().LockRequestDepth : 0;

    /// <summary>当前线程应用阶段栈深度（诊断用）。</summary>
    internal int ScopeDepth => _enabled ? Peek().ScopeDepth : 0;

    /// <summary>
    /// 为一次 sink 回调分配配对 ID。enter/exit 共用该 ID，便于日志复原一次通知的完整范围。
    /// 计数按 store 递增；即使诊断关闭也保持单调，避免 ID 复用造成误配对。
    /// </summary>
    internal long BeginNotification() => Interlocked.Increment(ref _notificationSequence);

    internal void EnterNotification(long id)
    {
        if (!_enabled)
        {
            return;
        }

        Peek().PushNotification(id);
    }

    internal void ExitNotification()
    {
        if (!_enabled)
        {
            return;
        }

        Peek().PopNotification();
    }

    internal long BeginLockRequest() => Interlocked.Increment(ref _lockRequestSequence);

    internal void EnterLockRequest(long id)
    {
        if (!_enabled)
        {
            return;
        }

        Peek().PushLockRequest(id);
    }

    internal void ExitLockRequest()
    {
        if (!_enabled)
        {
            return;
        }

        Peek().PopLockRequest();
    }

    /// <summary>
    /// 进入一个已知应用阶段（可嵌套）。返回的作用域必须在 <c>finally</c> 中 Dispose，
    /// 这样异常路径也能恢复上一层标签，不会把标签遗留到后续无关查询。
    /// </summary>
    internal Scope PushScope(string scope)
    {
        if (!_enabled)
        {
            return default;
        }

        var state = Peek();
        state.PushScope(scope);
        return new Scope(this, scope);
    }

    /// <summary>归因标签：应用阶段优先，其次是 sink 回调，最后 unknown。</summary>
    internal string DescribeScope()
    {
        if (!_enabled)
        {
            return LayoutChainScope.Unknown;
        }

        var state = Peek();
        if (state.Scope != LayoutChainScope.Unknown)
        {
            return state.Scope;
        }

        return state.NotificationId != 0 ? LayoutChainScope.TsfCallback : LayoutChainScope.Unknown;
    }

    /// <summary>
    /// 给任意事件补充诊断字段。未启用时返回原字典（不复制、不增加字段），
    /// 保持旧字段完全兼容。
    /// </summary>
    internal IDictionary<string, object?>? Enrich(IDictionary<string, object?>? fields)
    {
        if (!_enabled)
        {
            return fields;
        }

        var state = Peek();
        var scope = state.Scope != LayoutChainScope.Unknown
            ? state.Scope
            : state.NotificationId != 0 ? LayoutChainScope.TsfCallback : LayoutChainScope.Unknown;

        var target = fields ?? new Dictionary<string, object?>();
        // 不覆盖既有同名字段，保持旧日志兼容。
        target.TryAdd("diag_scope", scope);
        target.TryAdd("diag_notification_id", state.NotificationId == 0 ? null : state.NotificationId);
        target.TryAdd("diag_lock_request_id", state.LockRequestId == 0 ? null : state.LockRequestId);
        return target;
    }

    private ThreadState Peek() => _states.GetOrAdd(CurrentThreadId(), static _ => new ThreadState());

    /// <summary>
    /// 当前**真实原生线程**身份。使用 <c>GetCurrentThreadId</c> 而非 <see cref="Environment.CurrentManagedThreadId"/>，
    /// 因为托管线程身份可能被线程池复用，且诊断描述的是原生 UI 线程上的调用嵌套。
    /// 非 Windows（纯托管测试以外的场景）退回托管线程 ID。
    /// </summary>
    private static uint CurrentThreadId()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return GetCurrentThreadId();
            }
            catch (Exception)
            {
                // 原生查询失败时退回托管身份，绝不因此中断诊断。
            }
        }

        return unchecked((uint)Environment.CurrentManagedThreadId);
    }

    // 本文件被独立审核工程直接链接编译，因此不引用 ImeProbe 程序集，自行声明该 P/Invoke。
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// 可嵌套阶段租约。**契约是严格 LIFO**：必须在压栈的同一线程上、按逆序释放，且每个租约只释放一次
    /// （`using`/`finally` 路径天然满足）。本类型**不**提供幂等保证：同名阶段嵌套时重复释放
    /// 会把外层也弹掉。诊断标签正确性优先于重复释放的宽容度。
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly LayoutChainTrace? _owner;
        private readonly string _scope;

        internal Scope(LayoutChainTrace owner, string scope)
        {
            _owner = owner;
            _scope = scope;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
            {
                return;
            }

            // 严格 LIFO 出栈：与 PushScope 成对，每次 Dispose 弹出本租约压入的那一层。
            // 仅当栈顶恰为同名阶段时才弹出（防止跨线程/乱序释放破坏他人栈）；
            // 这**不是**幂等实现——同名嵌套下重复 Dispose 仍会继续弹掉外层。
            var state = owner.Peek();
            state.PopScopeIfTop(_scope);
        }
    }

    /// <summary>单线程的诊断状态：通知/锁请求/应用阶段三个栈。</summary>
    private sealed class ThreadState
    {
        private readonly List<long> _notifications = new();
        private readonly List<long> _lockRequests = new();
        private readonly List<string> _scopes = new();

        internal int NotificationDepth => _notifications.Count;

        internal int LockRequestDepth => _lockRequests.Count;

        internal int ScopeDepth => _scopes.Count;

        internal long NotificationId => _notifications.Count == 0 ? 0 : _notifications[^1];

        internal long LockRequestId => _lockRequests.Count == 0 ? 0 : _lockRequests[^1];

        internal string Scope => _scopes.Count == 0 ? LayoutChainScope.Unknown : _scopes[^1];

        internal void PushNotification(long id) => _notifications.Add(id);

        internal void PopNotification()
        {
            if (_notifications.Count > 0)
            {
                _notifications.RemoveAt(_notifications.Count - 1);
            }
        }

        internal void PushLockRequest(long id) => _lockRequests.Add(id);

        internal void PopLockRequest()
        {
            if (_lockRequests.Count > 0)
            {
                _lockRequests.RemoveAt(_lockRequests.Count - 1);
            }
        }

        internal void PushScope(string scope) => _scopes.Add(scope);

        internal void PopScopeIfTop(string scope)
        {
            if (_scopes.Count > 0 && string.Equals(_scopes[^1], scope, StringComparison.Ordinal))
            {
                _scopes.RemoveAt(_scopes.Count - 1);
            }
        }
    }
}
