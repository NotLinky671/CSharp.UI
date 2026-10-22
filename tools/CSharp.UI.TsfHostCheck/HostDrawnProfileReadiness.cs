using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R35：显式 <c>--host-drawn-candidates</c> 模式下、InitializeInputFocus 之后、任何
/// 场景/输入之前的有界只读就绪等待——等待用户把当前活动输入法手动切到
/// Microsoft Pinyin。程序绝不激活/启用/禁用 profile、不发键、不模拟鼠标、不抢焦点；
/// 外部前台/焦点丢失时不查询 COM、不发送输入，只继续有界等待。
/// </summary>
/// <remarks>
/// 生产与测试共享同一步骤/门禁：<see cref="Run"/> 由 <see cref="ScenarioRunner"/> 在
/// runner 线程上驱动，runner 线程每 ~200ms 把一次<b>只读原生守卫事实采样</b>投递到
/// owning UI 线程（不阻塞 UI）；仅当守卫成立且距上次 profile 查询 ≥500ms 时，才投递
/// 一次完整 <see cref="PollOnceOnUiThread"/>（guard-before → 只读
/// <c>TsfProfile.ReadActiveProfile</c> → guard-after）。外部前台/焦点时既不查询 COM
/// 也不输入，只继续等待；post-guard 失败的结果不采信。日志只在状态迁移或每 500ms
/// 记录一次（waiting 一次 / 迁移 / ready / timeout），绝不每轮刷 profile 日志。
/// 就绪后原 <c>PrepareComposition</c> 严格门禁照旧执行；超时如实失败，绝不把环境
/// 未就绪标通过。默认 off（未启用 HostDrawnCandidates）时本路径完全不运行——见
/// <see cref="RequiresWait"/>，<see cref="ScenarioRunner.Run"/> 经它绕过整段等待。
/// </remarks>
internal static class HostDrawnProfileReadiness
{
    /// <summary>runner 线程轮询间隔；UI 线程不被阻塞。</summary>
    internal const int PollIntervalMs = 200;

    /// <summary>COM profile 查询的最小间隔——日志不每轮刷。</summary>
    internal const int ProfileQueryIntervalMs = 500;

    /// <summary>
    /// 默认 off 旁路：只有显式 <c>--host-drawn-candidates</c> 才需要就绪等待；
    /// 其他任何路径一律跳过（<see cref="ScenarioRunner.Run"/> 的唯一判定点）。
    /// </summary>
    internal static bool RequiresWait(ScenarioOptions options)
        => options.HostDrawnCandidates;

    /// <summary>单轮 UI body 内一次只读 profile 尝试的结果。</summary>
    internal readonly record struct PollResult(
        bool Ready,
        bool GuardFailed,
        string Detail,
        string ProfileTag);

    /// <summary>
    /// 原生守卫事实：root/input HWND 归属（本进程 + 本 UI 线程）、前台==root、
    /// Win32 焦点==input、store 无锁、无布局回调重入。COM 查询前后各采样一次；
    /// 两侧都必须通过才算一次可信查询。
    /// </summary>
    internal readonly record struct GuardFacts(
        bool RootValid,
        bool InputValid,
        uint RootTid,
        uint RootPid,
        uint InputTid,
        uint InputPid,
        IntPtr Foreground,
        IntPtr Focus,
        uint LockType,
        int LayoutDepth);

    /// <summary>
    /// 可注入门禁判定：检查一组守卫事实是否允许发起/采信 profile 查询。
    /// 生产以真实 Win32 事实驱动；测试注入替身事实走<b>同一条</b>判定。
    /// </summary>
    internal static string? CheckGuard(in GuardFacts facts,
        IntPtr root, IntPtr input, uint uiThreadId, int expectedPid)
    {
        if (root == IntPtr.Zero || input == IntPtr.Zero || !facts.RootValid || !facts.InputValid)
        {
            return "own root/input hwnd not valid";
        }

        if (facts.RootPid != expectedPid || facts.InputPid != expectedPid)
        {
            return $"hwnd owned by foreign process (root_pid={facts.RootPid} input_pid={facts.InputPid} own={expectedPid})";
        }

        if (facts.RootTid != uiThreadId || facts.InputTid != uiThreadId
            || uiThreadId == 0 || NativeMethods.CurrentWin32ThreadId() != uiThreadId)
        {
            return $"hwnd owned by foreign ui thread (root_tid={facts.RootTid} input_tid={facts.InputTid} ui_tid={uiThreadId})";
        }

        if (facts.Foreground != root)
        {
            return $"own root foreground lost (foreground=0x{facts.Foreground.ToInt64():X})";
        }

        if (facts.Focus != input)
        {
            return $"input focus not on input surface (focus=0x{facts.Focus.ToInt64():X})";
        }

        if (facts.LockType != 0)
        {
            return $"store lock held (lock_type=0x{facts.LockType:X})";
        }

        if (facts.LayoutDepth != 0)
        {
            return $"native layout callback reentry (depth={facts.LayoutDepth})";
        }

        return null;
    }

    /// <summary>
    /// 单轮就绪尝试（在 UI 线程内执行）：guard-before → 只读 COM 查询 → guard-after。
    /// pre-guard 失败直接不查询；post-guard 失败说明 COM 重入破坏了环境，本结果不采信。
    /// 就绪 = 两侧 guard 均成立且查询到完整 Microsoft Pinyin 身份。
    /// </summary>
    internal static PollResult PollOnceOnUiThread(
        Func<GuardFacts> readFacts,
        Func<(int HResult, uint ProfileType, ushort LangId, Guid Clsid, Guid ProfileGuid)> readProfile,
        IntPtr root, IntPtr input, uint uiThreadId, int expectedPid)
    {
        var pre = readFacts();
        var preFailure = CheckGuard(pre, root, input, uiThreadId, expectedPid);
        if (preFailure is not null)
        {
            return new PollResult(false, true, "pre-guard: " + preFailure,
                ImeIdentity.UnverifiableProfile);
        }

        var (hr, profileType, langId, clsid, profileGuid) = readProfile();
        var identity = ImeIdentity.Describe(hr, profileType, langId, clsid, profileGuid);
        var snapshot = new ActiveProfileSnapshot(hr, profileType, langId, clsid, profileGuid,
            true, pre.Focus, NativeMethods.CurrentWin32ThreadId());
        var tag = ImeIdentity.VerifiedIdentityTag(snapshot);

        var post = readFacts();
        var postFailure = CheckGuard(post, root, input, uiThreadId, expectedPid);
        if (postFailure is not null)
        {
            return new PollResult(false, true, "post-guard: " + postFailure,
                ImeIdentity.UnverifiableProfile);
        }

        if (hr != 0)
        {
            return new PollResult(false, false,
                $"active profile query failed: {identity}", ImeIdentity.UnverifiableProfile);
        }

        if (!ImeIdentity.IsMicrosoftPinyin(hr, profileType, langId, clsid, profileGuid))
        {
            return new PollResult(false, false,
                $"active profile is not Microsoft Pinyin: {identity}", tag);
        }

        return new PollResult(true, false, "ready: Microsoft Pinyin active and stable", tag);
    }

    /// <summary>
    /// 有界等待循环（runner 线程）：每 <see cref="PollIntervalMs"/> 投递一次守卫采样；
    /// 守卫成立且距上次 COM 查询 ≥<paramref name="profileQueryIntervalMs"/> 时才做完整
    /// profile 查询轮。UI 派发抛错（窗口已销毁）如实返回 false。日志：开始时
    /// waiting 一条、状态每次迁移一条、等待期间每 500ms 至多一条、ready/timeout
    /// 各一条。返回 true=已就绪（调用方继续原严格门禁）；false=超时/窗口失效。
    /// </summary>
    /// <param name="invokeOnUiThread">把工厂委托投递到 owning UI 线程执行并返回其结果。</param>
    /// <param name="readFacts">UI 线程内只读原生守卫事实采样（生产=真实 Win32 读取；测试注入替身）。</param>
    /// <param name="checkGuard">守卫门禁（生产=<see cref="CheckGuard"/> 的部分应用）。</param>
    /// <param name="poll">完整一轮 guard+COM+guard（生产=<see cref="PollOnceOnUiThread"/> 的部分应用；测试注入替身）。</param>
    /// <param name="timeoutMs">总上限；整个等待受此 bound 约束，超时不等待。</param>
    /// <param name="trace">结构化日志出口（waiting/ready/timeout/状态迁移）。</param>
    /// <param name="profileQueryIntervalMs">两次 COM profile 查询的最小间隔；默认 500ms。</param>
    internal static bool Run(
        Func<Func<object?>, object?> invokeOnUiThread,
        Func<GuardFacts> readFacts,
        Func<GuardFacts, string?> checkGuard,
        Func<PollResult> poll,
        int timeoutMs,
        Action<string, IDictionary<string, object?>> trace,
        int profileQueryIntervalMs = ProfileQueryIntervalMs)
    {
        var budget = Math.Max(1, timeoutMs);
        var queryBudget = Math.Max(0, profileQueryIntervalMs);
        var started = Environment.TickCount64;
        var deadline = started + budget;
        var guardChecks = 0;
        var queries = 0;
        // R35A：null = 尚未查询过，第一次守卫成立即查询。绝不能用 long.MinValue 哨兵——
        // now - long.MinValue 会溢出成负数，使第一次查询永远被跳过。
        long? lastQueryTick = null;
        var lastLogTick = 0L;
        string? lastState = null;
        string? lastDetail = null;
        var emittedWaiting = false;

        void Emit(string state, string detail)
        {
            trace("host-drawn-profile-readiness", new Dictionary<string, object?>
            {
                ["state"] = state,
                ["detail"] = detail,
                ["guard_checks"] = guardChecks,
                ["profile_queries"] = queries,
                ["waited_ms"] = Math.Max(0L, Environment.TickCount64 - started),
            });
            lastState = state;
            lastDetail = detail;
            lastLogTick = Environment.TickCount64;
        }

        void EmitTransition(string state, string detail, bool force)
        {
            // 状态迁移必记；同状态等待期间每 500ms 至多一条；开始先记一次 waiting。
            if (!emittedWaiting)
            {
                emittedWaiting = true;
                Emit(state, detail);
                return;
            }

            if (force || state != lastState || detail != lastDetail
                || Environment.TickCount64 - lastLogTick >= 500)
            {
                Emit(state, detail);
            }
        }

        while (Environment.TickCount64 < deadline)
        {
            // ---- 200ms tick：UI 线程内只读守卫采样+判定（无 COM/无日志）----
            // checkGuard 依赖 UI 线程当前 TID，必须与事实采样同一次派发。
            string? guardFailure;
            try
            {
                guardFailure = (string?)invokeOnUiThread(() => checkGuard(readFacts()));
            }
            catch (Exception ex)
            {
                Emit("timeout", $"ui dispatch failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            guardChecks++;
            if (guardFailure is not null)
            {
                // 外部前台/焦点/归属失守：不查询 COM、不输入，只继续有界等待。
                EmitTransition("waiting", "guard: " + guardFailure, false);
                Thread.Sleep(PollIntervalMs);
                continue;
            }

            var now = Environment.TickCount64;
            if (lastQueryTick is { } lastTick && now - lastTick < queryBudget)
            {
                // 守卫成立但未到下一次 COM 查询窗口；保持等待节奏。
                EmitTransition("waiting", "guard ok; profile query pending", false);
                Thread.Sleep(PollIntervalMs);
                continue;
            }

            // ---- ~500ms tick：完整一轮 guard → COM → guard（仍在 UI 线程内）----
            PollResult result;
            try
            {
                result = (PollResult)invokeOnUiThread(() => poll())!;
            }
            catch (Exception ex)
            {
                Emit("timeout", $"ui dispatch failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            queries++;
            lastQueryTick = Environment.TickCount64;

            if (result.Ready)
            {
                Emit("ready", result.Detail + $" profile={result.ProfileTag}");
                return true;
            }

            EmitTransition("waiting", result.Detail, false);
            Thread.Sleep(PollIntervalMs);
        }

        Emit("timeout", lastDetail ?? "(no poll completed)");
        return false;
    }
}
