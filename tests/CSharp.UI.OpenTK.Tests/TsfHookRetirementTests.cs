using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a R6 增量：<see cref="TsfMessageHook"/> 的<b>退役 / 待清理强根保活</b>语义。
/// </summary>
/// <remarks>
/// <para>
/// <b>用受控原生接缝而不是真实永久钩子：</b>真实 <c>UnhookWindowsHookEx</c> 失败无法在受控环境下稳定制造，
/// 所以这些用例经 <see cref="TsfMessageHook.NativeSeamForTesting"/> 注入失败：失败 → 强根保留 → 迟到原生回调
/// → 所属线程重试 → 强根移除。测试本身不安装任何真实钩子（真实安装/卸载由
/// <see cref="真实原生安装后卸载成功并移除强根"/> 覆盖）。
/// </para>
/// <para>
/// <b>调用时机：</b>接缝是线程静态的，而钩子回调总是在安装线程同步执行，因此每个用例都必须在同一线程内
/// 安装、模拟回调与退役。
/// </para>
/// </remarks>
public sealed class TsfHookRetirementTests
{
    private static readonly IntPtr OwnHwnd = (IntPtr)0x1234;
    private static readonly IntPtr NextResult = (IntPtr)0x5A5A;

    [Fact]
    public void 解绑失败保留强根_迟到回调只链式传递_所属线程重试成功后移除根()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailUnhookAttempts = 1 }; // 第 1 次解绑失败，第 2 次（重试）成功
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var ownerThreadId = (uint)Environment.CurrentManagedThreadId;
            var conversations = new List<(IntPtr Hwnd, uint Message, uint WParam, IntPtr LParam)>();
            var hook = new TsfMessageHook(
                OwnHwnd, ownerThreadId, (hwnd, message, wParam, lParam) =>
                {
                    conversations.Add((hwnd, message, wParam, lParam));
                    return true;
                });

            Assert.True(hook.Install());
            Assert.Equal(1, hook.InstallCount);
            Assert.Equal(1, seam.InstallCount);

            // 安装后：自有窗口的 PM_REMOVE 按键消息被判定消费 → 改写为 WM_NULL。
            var liveKey = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyDown,
                (IntPtr)0x41, (IntPtr)0x001E0001);
            try
            {
                Invoke(hook, liveKey, TsfMessageHookPolicy.PmRemove);
                Assert.Equal(1, conversations.Count);
                Assert.Equal(TsfMessageHookPolicy.WmNull, ReadMessage(liveKey).Message);
                Assert.Equal(TsfHookRetirementStatus.Active, hook.RetirementStatus);

                // 首次释放：原生解绑失败 → 如实报告失败，但必须保留原生状态与强根。
                hook.Dispose();
                Assert.Equal(1, seam.UnhookAttempts);
                Assert.Equal(1, hook.UninstallFailureCount);
                Assert.True(hook.Installed);
                Assert.True(hook.RetireRequested);
                Assert.True(hook.RetainedForTest);
                Assert.Equal(TsfHookRetirementStatus.RetireRequested, hook.RetirementStatus);

                // 迟到回调（原生仍持有委托）：只链式传递，不得访问会话/改写 MSG/计入观察。
                // 有意使用 WM_KEYUP 等“本可被消费”的按键消息，避免把“消息已被改写”误当作退役守卫生效。
                var lateKey = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyUp,
                    (IntPtr)0x43, (IntPtr)0xC02E0001);
                try
                {
                    var result = Invoke(hook, lateKey, TsfMessageHookPolicy.PmRemove);

                    Assert.Equal(NextResult, result);
                    Assert.Equal(1, hook.RetiredCallbackCount);
                    Assert.Equal(1, conversations.Count); // 没有再次触碰会话（live 那次之后再无调用）
                    Assert.Equal(1, hook.ConsumedCount); // 保持 live 那次累计，不因退役清零
                    Assert.Equal(1, hook.ObservedKeyCount); // 同上：迟到回调节点未再计数
                    Assert.Equal(1, hook.RewriteCount); // 只有第一枚 live 键被改写
                    Assert.Equal(TsfMessageHookPolicy.WmKeyUp, ReadMessage(lateKey).Message); // 未改写
                }
                finally
                {
                    Marshal.FreeHGlobal(lateKey);
                }

                // 所属线程重试（幂等）：成功后才能移除强根并清掉回调引用。
                Assert.True(hook.Uninstall());
                Assert.Equal(2, seam.UnhookAttempts);
                Assert.False(hook.RetainedForTest);
                Assert.Equal(TsfHookRetirementStatus.Retired, hook.RetirementStatus);
                Assert.False(hook.Installed);
            }
            finally
            {
                Marshal.FreeHGlobal(liveKey);
            }
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 移除外层引用并强制GC后迟到回调仍安全且能重试成功()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailUnhookAttempts = 1 };
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var ownerThreadId = (uint)Environment.CurrentManagedThreadId;
            var sessionTouches = new StrongBox<int>(0);

            // 失败 → 强制 GC（强根必须仍持有）→ 迟到回调安全 → 重试成功 → 根移除：
            // 全部在 NoInlining 帧内完成，本帧只保留 weak。
            var weak = RunRetirementLifecycleUnderGc(
                ownerThreadId, seam, sessionTouches, () => Interlocked.Increment(ref sessionTouches.Value));

            // 本帧没有任何 TsfMessageHook 强引用；若最终回收断言失败，说明强根没被移除。
            // 读取与强制回收都在独立 NoInlining 帧内完成（本帧的隐式临时引用不会跨帧保活）。
            Assert.True(
                IsCollectedInIsolatedFrame(weak),
                "解绑成功并移除强根后，实例应可被回收（本帧未持有任何强引用）。");
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 已要求退役的仍活动钩子不改写消息且不调用消费策略()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailUnhookAttempts = 1 };
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var policyCalls = 0;
            var hook = new TsfMessageHook(
                OwnHwnd,
                (uint)Environment.CurrentManagedThreadId,
                (_, _, _, _) =>
                {
                    policyCalls++;
                    return true;
                });

            Assert.True(hook.Install());

            // 先确认真实消费路径有效（改写发生）。
            var consumedPointer = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyDown,
                (IntPtr)0x41, (IntPtr)0x001E0001);
            try
            {
                Invoke(hook, consumedPointer, TsfMessageHookPolicy.PmRemove);
                Assert.Equal(1, policyCalls);
                Assert.Equal(1, hook.RewriteCount);
                Assert.Equal(TsfMessageHookPolicy.WmNull, ReadMessage(consumedPointer).Message);
            }
            finally
            {
                Marshal.FreeHGlobal(consumedPointer);
            }

            // 进入退役（解绑失败，钩子仍在原生链上）。
            hook.Dispose();
            Assert.True(hook.Installed);
            Assert.True(hook.RetireRequested);

            var latePointer = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyUp,
                (IntPtr)0x41, (IntPtr)0xC01E0001);
            try
            {
                // 负向对照：若退役期间仍走消费路径，这里会被改写成 WM_NULL。
                Invoke(hook, latePointer, TsfMessageHookPolicy.PmRemove);

                Assert.Equal(1, policyCalls); // 不再调用消费策略
                Assert.Equal(1, hook.RewriteCount); // 不再改写
                Assert.Equal(TsfMessageHookPolicy.WmKeyUp, ReadMessage(latePointer).Message);
                Assert.Equal(1, hook.RetiredCallbackCount);
            }
            finally
            {
                Marshal.FreeHGlobal(latePointer);
            }
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 解绑失败后重复Dispose会重试而不是永久跳过()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailUnhookAttempts = 1 };
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var hook = new TsfMessageHook(
                OwnHwnd, (uint)Environment.CurrentManagedThreadId, (_, _, _, _) => false);
            Assert.True(hook.Install());

            hook.Dispose();
            Assert.Equal(1, seam.UnhookAttempts);
            Assert.True(hook.Installed);
            Assert.True(hook.RetainedForTest);
            Assert.Equal(1, hook.DisposeRequestCount);

            // 第二次 Dispose 必须重试原生解绑（旧实现会在 _disposed 上直接 return，永久放弃）。
            hook.Dispose();
            Assert.Equal(2, seam.UnhookAttempts);
            Assert.False(hook.Installed);
            Assert.False(hook.RetainedForTest);
            Assert.Equal(TsfHookRetirementStatus.Retired, hook.RetirementStatus);

            // 幂等：已经真正退役后不再触碰原生（owner 记录已清 → 后续 Dispose 不再计数）。
            hook.Dispose();
            hook.Dispose();
            Assert.Equal(2, seam.UnhookAttempts);
            Assert.Equal(2, hook.DisposeRequestCount);

            // 仍可直接重试入口：已退役的实例是幂等成功，不触碰原生。
            Assert.True(hook.TryCompleteRetirement());
            Assert.Equal(2, seam.UnhookAttempts);

            // Dispose 之后不得再次安装。
            Assert.False(hook.Install());
            Assert.Equal(unchecked((int)0x80004005), hook.InstallFailureCode);
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 跨线程Dispose被拒绝且活动钩子继续消费按键()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam();
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var ownerThreadId = (uint)Environment.CurrentManagedThreadId;
            var policyCalls = 0;
            var hook = new TsfMessageHook(
                OwnHwnd,
                ownerThreadId,
                (_, _, _, _) =>
                {
                    policyCalls++;
                    return true;
                });

            Assert.True(hook.Install());
            var disposeRequestCountBefore = hook.DisposeRequestCount;

            // 另一个线程释放：Dispose / Uninstall / 重试入口都必须被拒绝，且不得改动任何活动状态。
            Exception? crossThreadFailure = null;
            var foreignRejections = true;
            var disposeThread = new Thread(() =>
            {
                try
                {
                    hook.Dispose();
                    foreignRejections &= !hook.Uninstall();
                    foreignRejections &= !hook.TryCompleteRetirement();
                }
                catch (Exception ex)
                {
                    crossThreadFailure = ex;
                }
            });
            disposeThread.Start();
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(30)), "跨线程 Dispose 未返回。");

            Assert.Null(crossThreadFailure);
            Assert.True(foreignRejections, "外线程的 Dispose/Uninstall/TryCompleteRetirement 必须全部被拒绝。");

            // 被拒绝的证据：没有原生解绑、没有进入退役、没有强根、计数也未增加。
            Assert.Equal(0, seam.UnhookAttempts);
            Assert.True(hook.Installed);
            Assert.False(hook.RetireRequested);
            Assert.False(hook.RetainedForTest);
            Assert.Equal(disposeRequestCountBefore, hook.DisposeRequestCount);
            Assert.Equal(TsfHookRetirementStatus.Active, hook.RetirementStatus);

            // 关键：活动钩子仍必须真正走消费策略并改写 MSG（不能静默变成只链式传递）。
            var liveKey = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyDown,
                (IntPtr)0x41, (IntPtr)0x001E0001);
            try
            {
                Assert.Equal(NextResult, Invoke(hook, liveKey, TsfMessageHookPolicy.PmRemove));

                Assert.Equal(1, policyCalls); // 消费策略仍被调用
                Assert.Equal(1, hook.ConsumedCount);
                Assert.Equal(1, hook.RewriteCount);
                Assert.Equal(TsfMessageHookPolicy.WmNull, ReadMessage(liveKey).Message); // 仍改写
                Assert.Equal(0, hook.RetiredCallbackCount);
            }
            finally
            {
                Marshal.FreeHGlobal(liveKey);
            }

            // 所属线程随后仍可正常清理。
            Assert.True(hook.TryCompleteRetirement());
            Assert.Equal(1, seam.UnhookAttempts);
            Assert.False(hook.Installed);
            Assert.Equal(TsfHookRetirementStatus.Retired, hook.RetirementStatus);
            Assert.False(hook.RetainedForTest);
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 外线程Uninstall被拒绝后所属线程仍可正常工作与清理()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam();
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var policyCalls = 0;
            var hook = new TsfMessageHook(
                OwnHwnd,
                (uint)Environment.CurrentManagedThreadId,
                (_, _, _, _) =>
                {
                    policyCalls++;
                    return false;
                });

            Assert.True(hook.Install());

            var rejected = false;
            var foreignThread = new Thread(() => rejected = !hook.Uninstall());
            foreignThread.Start();
            Assert.True(foreignThread.Join(TimeSpan.FromSeconds(30)), "外线程 Uninstall 未返回。");

            Assert.True(rejected, "外线程 Uninstall 必须返回 false。");
            Assert.Equal(0, seam.UnhookAttempts);
            Assert.True(hook.Installed);
            Assert.False(hook.RetireRequested);
            Assert.False(hook.RetainedForTest);

            // 所属线程路径未被污染：仍正常工作（未被消费 → 不改写）。
            var liveKey = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyDown,
                (IntPtr)0x41, (IntPtr)0x001E0001);
            try
            {
                Invoke(hook, liveKey, TsfMessageHookPolicy.PmRemove);
                Assert.Equal(1, policyCalls);
                Assert.Equal(0, hook.RewriteCount);
                Assert.Equal(TsfMessageHookPolicy.WmKeyDown, ReadMessage(liveKey).Message);
            }
            finally
            {
                Marshal.FreeHGlobal(liveKey);
            }

            // 所属线程仍可正常清理。
            Assert.True(hook.Uninstall());
            Assert.Equal(1, seam.UnhookAttempts);
            Assert.False(hook.Installed);
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 所属线程重试入口只清理本线程的待清理强根()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailUnhookAttempts = 1 };
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            // 本线程的待清理钩子（首次解绑失败 → 有强根）。
            var owned = new TsfMessageHook(
                OwnHwnd, (uint)Environment.CurrentManagedThreadId, (_, _, _, _) => false);
            Assert.True(owned.Install());
            owned.Dispose();
            Assert.True(owned.RetainedForTest);
            Assert.Equal(1, owned.UninstallFailureCount);

            // 另一个线程的待清理钩子（该线程有自己的接缝实例）：其 owner 线程已结束，强根必须原样保留。
            TsfMessageHook? foreign = null;
            var foreignThread = new Thread(() =>
            {
                var threadSeam = new FakeNativeSeam { FailUnhookAttempts = 1 };
                TsfMessageHook.NativeSeamForTesting = threadSeam;
                try
                {
                    foreign = new TsfMessageHook(
                        OwnHwnd, (uint)Environment.CurrentManagedThreadId, (_, _, _, _) => false);
                    Assert.True(foreign.Install());
                    foreign.Dispose();
                    Assert.True(foreign.RetainedForTest);
                }
                finally
                {
                    TsfMessageHook.NativeSeamForTesting = null;
                }
            });
            foreignThread.Start();
            Assert.True(foreignThread.Join(TimeSpan.FromSeconds(30)), "外线程安装/释放未返回。");
            Assert.NotNull(foreign);

            var baselinePending = TsfMessageHook.PendingRetirementCount;
            Assert.True(baselinePending >= 2);

            // 本线程重试入口：只完成自己的那个，外线程强根不动。
            var completed = TsfMessageHook.RetryPendingRetirementsForCurrentThread();

            Assert.Equal(1, completed);
            Assert.False(owned.RetainedForTest);
            Assert.Equal(TsfHookRetirementStatus.Retired, owned.RetirementStatus);

            Assert.True(foreign.RetainedForTest);
            Assert.True(foreign.Installed);
            Assert.True(foreign.RetireRequested);
            Assert.Equal(baselinePending - 1, TsfMessageHook.PendingRetirementCount);

            // 幂等：没有本线程待清理项时是空操作。
            Assert.Equal(0, TsfMessageHook.RetryPendingRetirementsForCurrentThread());
            Assert.Equal(baselinePending - 1, TsfMessageHook.PendingRetirementCount);
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 安装失败不产生强根且Dispose不触碰原生()
    {
        TsfMessageHook.ClearPendingRetirementForTesting();
        var seam = new FakeNativeSeam { FailInstall = true, InstallErrorCode = 5 }; // ERROR_ACCESS_DENIED
        TsfMessageHook.NativeSeamForTesting = seam;
        try
        {
            var hook = new TsfMessageHook(
                OwnHwnd, (uint)Environment.CurrentManagedThreadId, (_, _, _, _) => false);

            Assert.False(hook.Install());
            Assert.Equal(5, hook.InstallFailureCode);
            Assert.Equal(0, hook.InstallCount);
            Assert.False(hook.Installed);
            Assert.False(hook.RetainedForTest);
            Assert.Equal(TsfHookRetirementStatus.NotInstalled, hook.RetirementStatus);

            // 未安装过 → 无原生句柄可解：Dispose 不得调用原生、不得留下强根、必须幂等。
            hook.Dispose();
            hook.Dispose();
            Assert.Equal(0, seam.UnhookAttempts);
            Assert.Equal(0, seam.CallNextCount);
            Assert.False(hook.RetainedForTest);
            Assert.Equal(0, TsfMessageHook.PendingRetirementCount);
            Assert.True(hook.Uninstall());
        }
        finally
        {
            TsfMessageHook.NativeSeamForTesting = null;
            TsfMessageHook.ClearPendingRetirementForTesting();
        }
    }

    [Fact]
    public void 真实原生安装后卸载成功并移除强根()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 不使用受控接缝：真实 user32 SetWindowsHookEx/UnhookWindowsHookEx。
        TsfMessageHook.NativeSeamForTesting = null;

        // 真实安装必须用 OS 线程 id（GetCurrentThreadId），托管线程号不是同一编号空间（会得到 Win32=87）。
        var hook = new TsfMessageHook(
            OwnHwnd, GetCurrentThreadId(), (_, _, _, _) => false);

        Assert.True(hook.Install(), $"真实 SetWindowsHookEx 安装失败，Win32={hook.InstallFailureCode}");
        Assert.Equal(1, hook.InstallCount);
        Assert.True(hook.Installed);
        Assert.Equal(TsfHookRetirementStatus.Active, hook.RetirementStatus);

        hook.Dispose();
        Assert.Equal(0, hook.UninstallFailureCount);
        Assert.False(hook.Installed);
        Assert.False(hook.RetainedForTest);
        Assert.Equal(TsfHookRetirementStatus.Retired, hook.RetirementStatus);
    }

    // ---------------------------------------------------------------- 测试辅助

    /// <summary>
    /// 在隔离（NoInlining）帧内完成整条退役生命周期：失败 → 强制 GC → 迟到回调 → 重试成功 → 移除强根。
    /// </summary>
    /// <returns>只返回弱引用；本方法返回后其全部局部变量随帧一起失效，调用方不得持有实例。</returns>
    /// <remarks>
    /// <para>
    /// <b>为什么必须隔离：</b>Debug 配置下 JIT 不裁剪局部变量，本帧里任何一次
    /// <c>hook.Xxx</c> 访问都会在表达式求值时产生隐式临时引用，并<b>保活到帧结束</b>；
    /// 显式 <c>hook = null</c> 也清不掉这些隐式临时。因此“解绑成功后应可回收”的读取必须放到
    /// <b>另一帧</b>（<see cref="IsCollectedInIsolatedFrame"/>），调用方只传弱引用。
    /// </para>
    /// <para>
    /// <b>为什么接缝不是产品强根：</b>钩子调用的是 <c>Native</c>（= 本线程的
    /// <see cref="TsfMessageHook.NativeSeamForTesting"/>）上的方法组，委托目标是<b>接缝实例</b>，
    /// 不是钩子自身；接缝也不反向持有钩子。因此实例与接缝之间没有环，移除注册表强根后
    /// 只要本帧结束，两者都能被回收。产品侧除此之外没有任何静态持有（注册表就是唯一的强根），
    /// 本用例失败过只能是本帧保活，而不是产品泄漏。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TsfMessageHook> RunRetirementLifecycleUnderGc(
        uint ownerThreadId, FakeNativeSeam seam, StrongBox<int> sessionTouches, Action onSessionTouched)
    {
        var hook = new TsfMessageHook(
            OwnHwnd,
            ownerThreadId,
            (_, _, _, _) =>
            {
                onSessionTouched();
                return true;
            });

        Assert.True(hook.Install());

        // 首次解绑失败：进入“待清理”状态并登记强根。
        hook.Dispose();
        Assert.True(hook.RetainedForTest);
        Assert.Equal(1, hook.UninstallFailureCount);

        // 卸载外层引用后强制 GC：没有强根时实例此刻就已被回收（悬空回调）。
        ForceFullCollection();
        Assert.True(hook.RetainedForTest, "解绑失败期间强根必须仍在。");

        // 迟到原生回调：仍能安全执行（只链式传递）。有意用“本可被消费”的按键消息，
        // 避免把“消息已被改写”误当成退役守卫生效。
        var lateKey = NewMessagePointer(OwnHwnd, TsfMessageHookPolicy.WmKeyUp,
            (IntPtr)0x43, (IntPtr)0xC02E0001);
        try
        {
            Assert.Equal(NextResult, Invoke(hook, lateKey, TsfMessageHookPolicy.PmRemove));
            Assert.Equal(1, hook.RetiredCallbackCount);
            Assert.Equal(0, Volatile.Read(ref sessionTouches.Value));
            Assert.Equal(TsfMessageHookPolicy.WmKeyUp, ReadMessage(lateKey).Message);
        }
        finally
        {
            Marshal.FreeHGlobal(lateKey);
        }

        // 所属线程重试成功 → 强根移除。
        Assert.True(hook.TryCompleteRetirement());
        Assert.Equal(2, seam.UnhookAttempts);
        Assert.False(hook.RetainedForTest);
        Assert.Equal(TsfHookRetirementStatus.Retired, hook.RetirementStatus);

        // 失败期间存活与成功后可回收是同一个门槛的两侧：这里只交出弱引用，
        // 之后本帧不再触碰 hook（连 GC.KeepAlive 也不做，否则会人为延长保活）。
        return new WeakReference<TsfMessageHook>(hook);
    }

    /// <summary>强根存活门槛与最终回收门槛共用的完整回收序列。</summary>
    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// 独立 NoInlining 帧：整轮强制回收后判断目标是否已被回收。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>调用方不得在本帧外再取强引用：</b>参数只有 <see cref="WeakReference{T}"/>，
    /// 本帧内的 <c>TryGetTarget</c> 临时随本帧结束失效，不会像调用方帧那样保活到断言之后。
    /// </para>
    /// <para>
    /// <b>失败期间存活由另一条断言保证：</b>强根仍在时同一序列不可能回收
    /// （<c>RunRetirementLifecycleUnderGc</c> 里的存活断言与这里的回收断言共用
    /// <see cref="ForceFullCollection"/>，构成同一个门槛的两侧）。
    /// 因此这里返回 true 就同时证明“成功后可回收”；返回 false 则如实失败，不做任何弱化。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollectedInIsolatedFrame(WeakReference<TsfMessageHook> weak)
    {
        ForceFullCollection();
        return !weak.TryGetTarget(out _);
    }

    /// <summary>
    /// 在非托管内存中分配一个可写 <c>MSG</c>（钩子回调会原地改写它，因此不能用栈/托管内存）。
    /// </summary>
    /// <remarks>调用方负责 <see cref="Marshal.FreeHGlobal"/>；本辅助刻意放在测试程序集内，不改产品类型。</remarks>
    private static IntPtr NewMessagePointer(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMsg>());
        Marshal.StructureToPtr(
            new NativeMsg
            {
                Hwnd = hwnd,
                Message = message,
                WParam = wParam,
                LParam = lParam,
                Time = 1,
                PointX = 0,
                PointY = 0,
            },
            pointer,
            fDeleteOld: false);
        return pointer;
    }

    private static NativeMsg ReadMessage(IntPtr pointer) => Marshal.PtrToStructure<NativeMsg>(pointer);

    private static IntPtr Invoke(TsfMessageHook hook, IntPtr messagePointer, uint removal)
        => hook.CallProcForTesting(0, (IntPtr)removal, messagePointer);

    /// <summary>
    /// 受控原生接缝：只把“原生钩子链会调用本回调”这件事显式化，不安装任何真实钩子。
    /// </summary>
    private sealed class FakeNativeSeam : TsfMessageHook.ITsfMessageHookNative
    {
        internal IntPtr HookHandle { get; private set; } = (IntPtr)0x1000;

        internal bool FailInstall { get; set; }

        internal int InstallErrorCode { get; set; } = 5;

        internal int FailUnhookAttempts { get; set; }

        internal int InstallCount { get; private set; }

        internal int UnhookAttempts { get; private set; }

        internal int CallNextCount { get; private set; }

        public IntPtr SetWindowsHookEx(int idHook, Delegate proc, IntPtr module, uint threadId)
        {
            InstallCount++;
            if (FailInstall)
            {
                return IntPtr.Zero;
            }

            HookHandle = (IntPtr)(0x1000 + InstallCount);
            return HookHandle;
        }

        public bool UnhookWindowsHookEx(IntPtr hook)
        {
            UnhookAttempts++;
            return UnhookAttempts > FailUnhookAttempts;
        }

        public IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam)
        {
            CallNextCount++;
            return NextResult;
        }

        public int LastError() => InstallErrorCode;
    }

    /// <summary>真实安装路径需要 OS 线程 id（与产品 <c>WindowsImeBridge</c> 的用法一致）。</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}

