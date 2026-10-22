using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 正式宿主的按键拦截钩子（TI-07 P3a）：在 <c>TranslateMessage</c> 之前按 TSF 结论处理按键。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 <c>WH_GETMESSAGE</c>：</b>它在 <c>GetMessage</c>/<c>PeekMessage</c> 取到消息之后、
/// 返回给调用方之前被调用，因此早于 <c>TranslateMessage</c> 生成 <c>WM_CHAR</c>。
/// 不替换 GLFW 的 <c>PollEvents</c>/<c>WaitEvents</c>，卸载即可完全恢复原行为。
/// </para>
/// <para>
/// <b>消费必须改写 MSG：</b>钩子“可以修改消息”，返回后系统把<b>连同修改</b>的消息交回调用方；
/// <b>返回值不负责消费</b>。因此本类在确认消费时把 <c>MSG</c> 改写成 <c>WM_NULL</c>，
/// 并照常链式调用下一个钩子。
/// </para>
/// <para>
/// <b>线程本地：</b>钩子只装到当前线程（<c>dwThreadId</c>），因此只影响自有 UI 线程的消息；
/// 非自有 HWND 的消息一律原样传递。
/// </para>
/// <para>
/// <b>委托必须被钉住：</b>钩子委托由本类字段持有，避免 GC 回收导致原生回调悬空。
/// 官方文档明确要求 .NET 调用方保证回调不被 GC 移动，否则进程会以
/// <c>ExecutionEngineException</c> 崩溃
/// （<see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowshookexw"/>）。
/// </para>
/// <para>
/// <b>退役（retirement）语义：</b><c>UnhookWindowsHookEx</c> 可能失败，而且官方明确
/// “钩子过程在 <c>UnhookWindowsHookEx</c> 返回之后仍可能正被另一个线程调用”
/// （<see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-unhookwindowshookex"/>）。
/// 因此本类把“要求退役”与“真正解绑成功”分开：
/// </para>
/// <list type="number">
/// <item><b>只有所属线程能改变生命周期状态：</b>非所属线程调用 <see cref="Dispose"/>/<see cref="Uninstall"/>/
/// <see cref="TryCompleteRetirement"/> 一律<b>被拒绝且不产生任何副作用</b>（不置 <c>_disposed</c>、不登记退役意图、
/// 不让回调静默失效），活动钩子继续正常工作并继续消费按键；解绑只能由所属线程完成。</item>
/// <item>解绑真正成功之前，实例被<b>待清理强根注册表</b>持有，使原生回调引用的托管委托不可能被 GC 回收。</item>
/// <item>退役期间（<see cref="RetireRequested"/>，仅在所属线程进入）回调<b>只调用 <c>CallNextHookEx</c></b>：
/// 不访问会话、不执行消费策略、不改写 <c>MSG</c>，避免触碰已释放对象。</item>
/// <item>失败可重试（<see cref="Uninstall"/> / <see cref="TryCompleteRetirement"/> / 再次 <see cref="Dispose"/>），
/// 或由宿主在安全阶段/teardown 调用 <see cref="RetryPendingRetirementsForCurrentThread"/>；成功后移除强根。</item>
/// </list>
/// </remarks>
internal sealed class TsfMessageHook : IDisposable
{
    private const int WhGetMessage = 3;
    private const int HcAction = 0;

    /// <summary>
    /// 当前线程上被注入的原生接缝（<b>仅测试用</b>，生产为 null → 使用真实 <c>user32</c>）。
    /// </summary>
    /// <remarks>
    /// 钩子回调总是在安装线程（= 调用 <c>CallProcForTesting</c> 的线程）同步执行，因此线程静态接缝不会串线程。
    /// </remarks>
    [ThreadStatic]
    private static ITsfMessageHookNative? _nativeForTesting;

    private readonly IntPtr _ownHwnd;
    private readonly uint _ownerThreadId;
    /// <summary>安装时所在的托管线程 id（= 钩子回调实际执行的线程；生命周期操作必须在此线程）。</summary>
    private int _ownerManagedThreadId = -1;
    private int _installCount;
    /// <summary>
    /// 判定回调：<c>(hwnd, message, wParam, lParam) → 是否被 TSF 消费</c>。
    /// </summary>
    /// <remarks>
    /// <b>必须传真实 lParam：</b><c>TestKeyDown</c>/<c>KeyDown</c>/<c>KeyUp</c> 需要原始 scan code、
    /// repeat count、extended/previous/transition 位；不得用 wParam 冒充或重新拼装。
    /// </remarks>
    private readonly Func<IntPtr, uint, uint, IntPtr, bool> _isConsumed;

    private HookProc? _proc;
    private IntPtr _hook;
    private bool _installed;
    private bool _disposed;
    private int _retireRequested; // 0/1，跨线程只写、回调用 Volatile.Read
    private int _disposeRequestCount;
    private int _retentionRetryCount;
    private int _retiredCallbackCount;

    private int _observedKeyCount;
    private int _consumedCount;
    private int _rewriteCount;
    private int _foreignWindowCount;
    private int _noremoveKeyCount;
    private int _installFailureCode;
    private int _uninstallFailureCount;

    /// <summary>
    /// <b>仅测试用</b>：注入受控原生接缝（不产生真实永久钩子）。
    /// </summary>
    /// <remarks>必须在调用 <c>CallProcForTesting</c> / <see cref="Install"/> / <see cref="Uninstall"/> 的同一线程上设置。</remarks>
    internal static ITsfMessageHookNative? NativeSeamForTesting
    {
        get => _nativeForTesting;
        set => _nativeForTesting = value;
    }

    /// <summary>本线程真正使用的原生实现（测试接缝优先）。</summary>
    private static ITsfMessageHookNative Native => _nativeForTesting ?? WindowsNative.Instance;

    /// <summary>是否已登记退役意图（跨线程可见；为 true 后回调不再触碰会话）。</summary>
    internal bool RetireRequested => Volatile.Read(ref _retireRequested) != 0;

    /// <summary>退役状态。</summary>
    internal TsfHookRetirementStatus RetirementStatus
    {
        get
        {
            if (_installed)
            {
                return RetireRequested ? TsfHookRetirementStatus.RetireRequested : TsfHookRetirementStatus.Active;
            }

            return RetireRequested ? TsfHookRetirementStatus.Retired : TsfHookRetirementStatus.NotInstalled;
        }
    }

    /// <summary>该实例当前是否被待清理强根注册表持有（诊断：&gt;0 表示原生可能仍会回调）。</summary>
    internal bool RetainedForTest => PendingRetirementRegistry.IsRetained(this);

    /// <summary>要求退役/释放的次数（诊断）。</summary>
    internal int DisposeRequestCount => _disposeRequestCount;

    /// <summary>因解绑失败而重新登记强根的次数（诊断；= 需所属线程重试的次数）。</summary>
    internal int RetentionRetryCount => _retentionRetryCount;

    /// <summary>退役期间仍被原生调用的次数（诊断；此时只链式传递）。</summary>
    internal int RetiredCallbackCount => _retiredCallbackCount;

    /// <summary>当前进程内等待原生真正解绑的钩子实例数（诊断）。</summary>
    internal static int PendingRetirementCount => PendingRetirementRegistry.Count;

    /// <summary>
    /// <b>仅测试用</b>：清空待清理强根注册表（避免测试之间互相干扰）。
    /// </summary>
    internal static void ClearPendingRetirementForTesting() => PendingRetirementRegistry.ClearForTesting();

    /// <summary>
    /// <b>仅测试用</b>：禁用 MSG 改写，用于负向对照（证明“无回声”确实来自改写而不是别的机制）。
    /// </summary>
    internal bool SuppressRewriteForTesting { get; set; }

    /// <summary>
    /// 创建钩子（不安装）。
    /// </summary>
    /// <param name="ownHwnd">自有窗口句柄；只有它的消息可被消费改写。</param>
    /// <param name="ownerThreadId">OS 线程 id（<c>GetCurrentThreadId</c>，<b>不是</b>托管线程 id），透传给原生安装。</param>
    /// <param name="isConsumed">
    /// 判定回调：<c>(hwnd, message, wParam, lParam) → 是否已被 TSF 消费</c>。
    /// 回调内<b>不得</b>排版、生成帧或写文档。
    /// </param>
    /// <remarks>
    /// 构造不安装、不触碰原生；真正的“所属线程”在 <see cref="Install"/> 时按当前托管线程记录，
    /// 后续原生解绑只允许在该线程进行。
    /// </remarks>
    internal TsfMessageHook(IntPtr ownHwnd, uint ownerThreadId, Func<IntPtr, uint, uint, IntPtr, bool> isConsumed)
    {
        ArgumentNullException.ThrowIfNull(isConsumed);
        _ownHwnd = ownHwnd;
        _ownerThreadId = ownerThreadId;
        _isConsumed = isConsumed;
    }

    /// <summary>是否已成功安装。</summary>
    internal bool Installed => _installed;

    /// <summary>解绑失败次数（&gt;0 表示原生仍可能回调，需所属线程重试）。</summary>
    internal int UninstallFailureCount => _uninstallFailureCount;

    /// <summary>安装失败的原生错误码（0 表示没有失败）。</summary>
    internal int InstallFailureCode => _installFailureCode;

    /// <summary>观察到的按键消息数（诊断）。</summary>
    internal int ObservedKeyCount => _observedKeyCount;

    /// <summary>TSF 判定为消费的次数（诊断）。</summary>
    internal int ConsumedCount => _consumedCount;

    /// <summary>实际改写 MSG 的次数（诊断；必须与消费次数一致）。</summary>
    internal int RewriteCount => _rewriteCount;

    /// <summary>观察到的非自有窗口消息数（必须不被改写）。</summary>
    internal int ForeignWindowCount => _foreignWindowCount;

    /// <summary>观察到的 <c>PM_NOREMOVE</c> 按键数（必须不被改写）。</summary>
    internal int NoRemoveKeyCount => _noremoveKeyCount;

    /// <summary>真实原生安装次数（&gt;0 表示确实产生过原生钩子）。</summary>
    internal int InstallCount => _installCount;

    /// <summary>本实例的钩子回调是否由当前线程执行（= 安装线程）。</summary>
    private bool IsOwnerThread => _ownerManagedThreadId == Environment.CurrentManagedThreadId;

    /// <summary>
    /// 安装线程本地钩子。
    /// </summary>
    /// <returns>true 表示成功；失败时 <see cref="InstallFailureCode"/> 给出原生错误码。</returns>
    /// <remarks>
    /// <b>线程归属在此确定：</b><c>SetWindowsHookEx</c> 的 <c>dwThreadId</c> 就是本线程，回调也由本线程执行，
    /// 因此把当前托管线程记为“所属线程”，后续原生解绑只允许在该线程进行。
    /// </remarks>
    internal bool Install()
    {
        if (_disposed)
        {
            _installFailureCode = unchecked((int)0x80004005);
            return false;
        }

        if (_installed)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            _installFailureCode = unchecked((int)0x80004001); // E_NOTIMPL
            return false;
        }

        _ownerManagedThreadId = Environment.CurrentManagedThreadId;
        _proc = HookCallback;
        _hook = Native.SetWindowsHookEx(WhGetMessage, _proc, IntPtr.Zero, _ownerThreadId);
        if (_hook == IntPtr.Zero)
        {
            _installFailureCode = Native.LastError();
            _proc = null;
            _ownerManagedThreadId = -1;
            return false;
        }

        _installed = true;
        _installCount++;
        return true;
    }

    /// <summary>
    /// 解绑钩子（幂等；失败时保留句柄与委托根供所属线程重试）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>解绑失败不能清空状态：</b>若 <c>UnhookWindowsHookEx</c> 失败，原生侧仍可能回调该委托；
    /// 清掉 <c>_hook</c>/<c>_proc</c> 会让委托失去最后托管引用 → 悬空回调。
    /// 因此失败时<b>保留</b>句柄与委托根、保持已安装状态，把本实例登记到待清理强根注册表，如实返回失败，
    /// 由所属线程稍后重试。
    /// </para>
    /// <para>
    /// <b>非所属线程一律拒绝：</b>返回 false 且<b>不产生任何副作用</b>（不登记退役、不改活动状态），
    /// 活动钩子继续正常工作；解绑只允许由所属线程完成。
    /// </para>
    /// </remarks>
    internal bool Uninstall() => TryCompleteRetirement();

    /// <summary>
    /// 由<b>所属线程</b>完成退役：真正调用原生解绑，成功后移除强根。
    /// </summary>
    /// <returns>
    /// true 表示当前不存在仍挂在原生钩子链上的本实例（未安装、或本次解绑成功、或已在别处完成）；
    /// false 表示未成功（非所属线程，或原生解绑失败需稍后重试）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>非所属线程调用无副作用：</b>直接返回 false，不置退役标志、不登记强根、不触碰原生，
    /// 以免把仍在活动的钩子静默变成“只链式传递”。
    /// </para>
    /// <para>
    /// <b>强根生命周期：</b>只有所属线程首次尝试解绑时才进入退役；解绑成功移除强根，失败则登记强根并记一次重试计数。
    /// 因此“失败后被上层丢弃实例”不会让原生回调失去托管委托。
    /// </para>
    /// </remarks>
    internal bool TryCompleteRetirement()
    {
        if (_hook == IntPtr.Zero && !_installed && _proc is null)
        {
            // 已经真正解绑过（含未安装过）：幂等成功。
            return true;
        }

        if (!IsOwnerThread)
        {
            // 非所属线程：拒绝且不改动任何状态（活动钩子必须继续按原样工作）。
            return false;
        }

        _disposed = true; // 所属线程已进入退役：之后不得再 Install
        Volatile.Write(ref _retireRequested, 1);

        if (!_installed)
        {
            // 从未安装或已被其它路径解绑：这里没有原生句柄可解，清掉残留引用并移除强根。
            ClearResidualReferences();
            return true;
        }

        if (Native.UnhookWindowsHookEx(_hook))
        {
            _hook = IntPtr.Zero;
            _installed = false;
            ClearResidualReferences();
            return true;
        }

        // 解绑失败：原生仍可能回调 → 保留句柄/委托，并保证有强根持有本实例（进而持有 _proc）。
        _uninstallFailureCount++;
        _retentionRetryCount++;
        PendingRetirementRegistry.Retain(this);
        return false;
    }

    /// <summary>清除已解绑后的残留引用并移除强根。</summary>
    private void ClearResidualReferences()
    {
        _proc = null;
        _ownerManagedThreadId = -1;
        PendingRetirementRegistry.Release(this);
    }

    /// <summary>
    /// <b>所属线程重试入口：</b>遍历“等待原生解绑”的强根，只重试属于当前线程的实例。
    /// </summary>
    /// <returns>本次成功完成退役（强根已移除）的实例数。</returns>
    /// <remarks>
    /// <para>
    /// <b>用途：</b>宿主可在自有安全阶段（loop 边界）或 teardown 时调用一次，把之前 <c>UnhookWindowsHookEx</c> 失败
    /// 而滞留的钩子清理掉。本方法只处理 <b>owner 线程 == 当前线程</b> 的实例，其它线程的强根原样保留，
    /// 不会越权解绑别人的钩子。
    /// </para>
    /// <para>
    /// <b>幂等且有界：</b>没有待清理实例时是空操作；每个实例最多尝试一次；必须在真实 UI 线程（loop/teardown）调用，
    /// 不要放在消息回调内部递归调用。
    /// </para>
    /// </remarks>
    internal static int RetryPendingRetirementsForCurrentThread()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var pending = PendingRetirementRegistry.SnapshotPending();
        if (pending.Length == 0)
        {
            return 0;
        }

        var completed = 0;
        foreach (var hook in pending)
        {
            if (hook._ownerManagedThreadId != currentThreadId)
            {
                // 只处理本 owner 的残余根；别人的由它们自己的 owner 线程清理。
                continue;
            }

            if (hook.TryCompleteRetirement())
            {
                completed++;
            }
        }

        return completed;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_proc is null)
        {
            // 已经真正解绑：理论上不会再被原生调用；防御性原样转发。
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 约定：code < 0 必须原样转发给下一个钩子（不得进一步处理）。
        if (nCode < HcAction || RetireRequested)
        {
            // 退役期间只链式传递：不访问会话、不执行消费策略、不改写 MSG。
            return RetiredPassThrough(nCode, wParam, lParam);
        }

        try
        {
            var msg = Marshal.PtrToStructure<NativeMsg>(lParam);
            var removal = (uint)wParam;

            if (msg.Hwnd != _ownHwnd)
            {
                if (TsfMessageHookPolicy.IsKeyMessage(msg.Message) || msg.Message == TsfMessageHookPolicy.WmChar)
                {
                    _foreignWindowCount++;
                }

                // 非自有窗口：绝不改写。
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (!TsfMessageHookPolicy.IsKeyMessage(msg.Message))
            {
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (removal == TsfMessageHookPolicy.PmNoRemove)
            {
                // 窥视：不判定、不改写。
                _noremoveKeyCount++;
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            _observedKeyCount++;

            var consumed = false;
            try
            {
                // 传真实 MSG.lParam（不重新拼装）。
                consumed = _isConsumed(msg.Hwnd, msg.Message, (uint)msg.WParam, msg.LParam);
            }
            catch (Exception)
            {
                // 回调异常绝不穿过原生边界：按“未消费”处理（保守，不吞用户按键）。
                consumed = false;
            }

            if (TsfMessageHookPolicy.Decide(removal, msg.Message, isOwnWindow: true, consumed)
                == TsfHookMessageAction.Consumed)
            {
                _consumedCount++;

                // 关键：改写 lParam 指向的真实 MSG，而不是只 return 1。
                // 负向对照可禁用改写，用以证明“无回声”确实由改写产生。
                if (!SuppressRewriteForTesting)
                {
                    var rewritten = TsfMessageHookPolicy.ConsumedMessage(msg.Hwnd, msg.Time);
                    Marshal.StructureToPtr(rewritten, lParam, false);
                    _rewriteCount++;
                }
            }

            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch (Exception)
        {
            // 任何托管异常都不得越过原生边界。
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    /// <summary>
    /// 退役期间的回调路径：只链式传递，绝不触碰会话/消费策略/MSG。
    /// </summary>
    private IntPtr RetiredPassThrough(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _retiredCallbackCount++;
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 释放：由所属线程登记退役并真正解绑；非所属线程调用被拒绝且无副作用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>幂等且可重试：</b>重复调用是安全的；<b>解绑失败后再次调用会重试</b>，
    /// 不会因为“已经 Dispose 过”而永久放弃（否则原生回调会悬空）。
    /// </para>
    /// <para>
    /// <b>非所属线程调用：</b>直接返回，不置 <c>_disposed</c>、不登记退役意图、不改动任何状态。
    /// 活动钩子继续保持“正常处理消息并消费按键”，生命周期清理留给所属线程
    /// （再次 <see cref="Dispose"/>、<see cref="Uninstall"/> 或
    /// <see cref="RetryPendingRetirementsForCurrentThread"/>）。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (!IsOwnerThread)
        {
            // 跨线程释放：拒绝且无副作用（不得让仍在活动的钩子静默失效）。
            return;
        }

        _disposeRequestCount++;

        // 所属线程已决定退役：之后不得再 Install（幂等；与 TryCompleteRetirement 的重复赋值无害）。
        _disposed = true;

        // 自身直接持有委托（即使已被注册表强根，也不依赖它来保证本次调用期间 _proc 存活）。
        GC.KeepAlive(_proc);

        TryCompleteRetirement();
    }

    /// <summary>只输出计数，不含用户内容。</summary>
    public override string ToString()
        => $"tsf-hook installed={_installed} status={RetirementStatus} retained={RetainedForTest} "
            + $"observed={_observedKeyCount} consumed={_consumedCount} "
            + $"rewritten={_rewriteCount} foreign={_foreignWindowCount} noremove={_noremoveKeyCount} "
            + $"uninstallFailures={_uninstallFailureCount} retiredCallbacks={_retiredCallbackCount}";

    // ---------------------------------------------------------------- 仅测试用的受控原生接缝

    /// <summary><b>仅测试用</b>：返回受控原生接缝（生产/已解绑时抛错，避免假通过）。</summary>
    internal ITsfMessageHookNative NativeSeam => Native;

    /// <summary>
    /// <b>仅测试用</b>：以产品回调委托模拟原生钩子链的调用（不产生真实钩子）。
    /// </summary>
    /// <param name="nCode">钩子代码（<c>HC_ACTION</c> = 0）。</param>
    /// <param name="wParam">消息移除标志（PM_REMOVE / PM_NOREMOVE）。</param>
    /// <param name="message">指向 <see cref="NativeMsg"/> 的指针。</param>
    /// <returns>回调返回值（应为 <c>CallNextHookEx</c> 的返回值）。</returns>
    /// <remarks>必须在安装线程调用（钩子回调同时在该线程同步执行）。</remarks>
    internal IntPtr CallProcForTesting(int nCode, IntPtr wParam, IntPtr message)
    {
        var proc = _proc ?? throw new InvalidOperationException(
            "钩子回调已随成功解绑清除；不能在真正解绑后模拟原生回调。");

        return proc(nCode, wParam, message);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 受控原生接缝：安装/解绑/链式转发。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要：</b>真实 <c>UnhookWindowsHookEx</c> 失败几乎无法在受控环境下制造，
    /// 而“解绑失败后原生仍回调”的悬空委托后果又必须被验证。接缝让测试可以精确注入失败、
    /// 观察迟到回调并在同一实例上重试，而不必留下真实永久钩子。
    /// </remarks>
    internal interface ITsfMessageHookNative
    {
        /// <summary><c>SetWindowsHookEx</c>；返回 0 表示失败（此时 <see cref="LastError"/> 给出错误码）。</summary>
        IntPtr SetWindowsHookEx(int idHook, Delegate proc, IntPtr module, uint threadId);

        /// <summary><c>UnhookWindowsHookEx</c>；返回 false 表示解绑失败（原生仍可能回调该委托）。</summary>
        bool UnhookWindowsHookEx(IntPtr hook);

        /// <summary><c>CallNextHookEx</c>；链式传递并返回下一个钩子的结果。</summary>
        IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>最近一次原生失败错误码。</summary>
        int LastError();
    }

    /// <summary>真实 <c>user32</c> 实现。</summary>
    private sealed class WindowsNative : ITsfMessageHookNative
    {
        internal static readonly WindowsNative Instance = new();

        public IntPtr SetWindowsHookEx(int idHook, Delegate proc, IntPtr module, uint threadId)
            => SetWindowsHookExW(idHook, (HookProc)proc, module, threadId);

        public bool UnhookWindowsHookEx(IntPtr hook) => TsfMessageHook.UnhookWindowsHookEx(hook);

        public IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam)
            => TsfMessageHook.CallNextHookEx(hook, nCode, wParam, lParam);

        public int LastError() => Marshal.GetLastWin32Error();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    }

    /// <summary>
    /// 待清理强根注册表：持有“原生解绑尚未成功”的钩子实例。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须是强根：</b>上层（router/bridge）在 Dispose 失败后可能直接丢弃钩子引用；
    /// 只要原生钩子链里还有本实例的回调指针，托管委托就必须活着，否则原生回调会跳进已回收内存。
    /// 注册表以静态 <see cref="Dictionary{TKey,TValue}"/> 持有实例引用，成功解绑时移除。
    /// </para>
    /// <para>
    /// <b>为什么注册实例而不是只注册委托：</b>实例字段 <c>_proc</c> 是委托的持有者；
    /// 保住实例即保住委托，同时保留 <c>_hook</c> 句柄供所属线程重试解绑、保留会话用于状态诊断。
    /// </para>
    /// </remarks>
    private static class PendingRetirementRegistry
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<TsfMessageHook, byte> Retained =
            new(ReferenceEqualityComparer.Instance);

        internal static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Retained.Count;
                }
            }
        }

        internal static void Retain(TsfMessageHook hook)
        {
            lock (Gate)
            {
                Retained[hook] = 0;
            }
        }

        internal static void Release(TsfMessageHook hook)
        {
            lock (Gate)
            {
                Retained.Remove(hook);
            }
        }

        /// <summary>当前所有等待原生解绑的强根快照（在锁外使用，避免遍历时锁被其它线程修改）。</summary>
        internal static TsfMessageHook[] SnapshotPending()
        {
            lock (Gate)
            {
                var snapshot = new TsfMessageHook[Retained.Count];
                Retained.Keys.CopyTo(snapshot, 0);
                return snapshot;
            }
        }

        internal static bool IsRetained(TsfMessageHook hook)
        {
            lock (Gate)
            {
                return Retained.ContainsKey(hook);
            }
        }

        internal static void ClearForTesting()
        {
            lock (Gate)
            {
                Retained.Clear();
            }
        }
    }
}

/// <summary>
/// 钩子退役状态（诊断用；与“原生是否仍可能回调”对应）。
/// </summary>
internal enum TsfHookRetirementStatus
{
    /// <summary>从未成功安装。</summary>
    NotInstalled = 0,

    /// <summary>已安装且正常处理消息。</summary>
    Active = 1,

    /// <summary>已登记退役意图，但原生解绑尚未成功：回调只链式传递，实例仍被强根持有。</summary>
    RetireRequested = 2,

    /// <summary>原生解绑已成功，回调引用已清除、强根已移除。</summary>
    Retired = 3,
}
