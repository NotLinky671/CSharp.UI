using CSharp.UI;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using global::OpenTK.Windowing.Common;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a R10：<b>真实安全边界</b>通知宿主的接线。
/// </summary>
/// <remarks>
/// <para>
/// <b>要关闭的缺陷：</b>宿主原先只订阅 <see cref="WindowsTextInputRouter.Coordinator"/>，
/// 而真实 TSF apply 发生在 <see cref="TsfInputBridge"/> <b>自己</b>的协调器上，
/// 因此 TSF 安全阶段退出永远通知不到宿主，无后续输入时已排队输入永久滞留。
/// </para>
/// <para>
/// <b>驱动的是生产同一条绑定路径：</b>路由器把宿主回调跟随“当前活动协调器”搬迁。
/// 测试只替换活动协调器实例（等价于 TSF 接管后真实桥自己的协调器），
/// 不另造旁路，也不用 <c>RaiseSafePhaseExitedForTesting</c> 冒充真实调用——本文件里真实退出一律
/// 走 <see cref="HostInputCoordinator.ExitSafePhase"/>（生产 apply 的唯一退出点）。
/// </para>
/// <para>
/// <b>本文件不证明什么：</b>不构成真实微软拼音或完整宿主循环的验收。真实宿主循环端到端由独立审计
/// <c>artifacts/ime-work/p3a-safe-signal-audit</c> 覆盖（该审计在真实 <c>UiApplication.Run</c> 下
/// 观察 <c>WakeUpCount</c> 变化）。
/// </para>
/// </remarks>
public sealed class SafeBoundarySignalTests
{
    /// <summary>真实运行时 + 真实输入适配器（与生产相同的 <see cref="OpenTkInputAdapter"/> 交付路径）。</summary>
    private sealed class Harness : IDisposable
    {
        public UiRuntime Runtime { get; }

        public OpenTkInputAdapter Adapter { get; }

        public Harness(WindowsTextInputRouter router)
        {
            var text = "ABCD";
            var field = new TextField(new Binding<string>(() => text, value => text = value));
            Runtime = new UiRuntime(new VStack(field), new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
                TextureService = new RecordingTextureService(),
                TextInputService = router,
            });

            Runtime.SetViewport(new Size(400, 200), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.MoveFocus();
            Runtime.BuildFrame();

            Adapter = new OpenTkInputAdapter(Runtime, () => { }, ime: router);
        }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>
    /// 宿主唤醒处理器：与 <c>OpenTkHost.OnDeferredInputSignal</c> 相同的公开契约——
    /// 仅在确有排队输入时请求<b>一次</b>唤醒。
    /// </summary>
    private static Action LazyWake(OpenTkUiDispatcher dispatcher, Harness harness)
        => () =>
        {
            if (harness.Adapter.HasDeferredInput)
            {
                dispatcher.RequestWakeup(TimeSpan.Zero);
            }
        };

    /// <summary>
    /// 核心接线：真实活动协调器（TSF 桥自己的协调器）退出安全阶段必须通知宿主并请求一次唤醒，
    /// 且队列身份仍是路由器协调器（单一有序队列）。
    /// </summary>
    [Fact]
    public void 真实活动协调器安全边界退出会通知宿主并请求一次唤醒()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);
        var dispatcher = new OpenTkUiDispatcher();

        // 宿主接线：订阅一次，跟随活动协调器。
        router.AttachHostSafePhaseSignal(LazyWake(dispatcher, h));

        // 模拟 TSF 接管：真实 TSF 桥使用它自己的协调器。
        var tsfCoordinator = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(tsfCoordinator);
        Assert.NotSame(tsfCoordinator, router.Coordinator);

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('x'));

        // 队列是同一个（路由器协调器），不是两套队列。
        Assert.Equal(1, router.Coordinator.DeferredCount);
        Assert.Equal(0, tsfCoordinator.DeferredCount);

        var beforeWake = dispatcher.WakeUpCount;
        var beforeSignals = router.HostSignalNotificationCount;

        // 真实安全边界退出（TSF 协调器）→ 必须到达宿主。
        router.ForceUnsafeForTesting = false;
        tsfCoordinator.ExitSafePhase();

        Assert.Equal(beforeWake + 1, dispatcher.WakeUpCount);
        Assert.Equal(beforeSignals + 1, router.HostSignalNotificationCount);

        // 单次唤醒的实际效果：无后续输入也主动交付。
        Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Equal(0, router.Coordinator.DeferredCount);
    }

    /// <summary>路由器自身的 <see cref="WindowsTextInputRouter.SafePhaseExited"/> 同样跟随真实活动协调器。</summary>
    [Fact]
    public void 路由器安全边界事件转发当前活动协调器的真实退出()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        var tsfCoordinator = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(tsfCoordinator);

        var forwarded = 0;
        router.SafePhaseExited += () => forwarded++;

        // 真实退出路径（不是测试钩子）。
        tsfCoordinator.ExitSafePhase();
        Assert.Equal(1, forwarded);

        // 不存在第二套“看起来像”的实现：Coordinator 就是当前活动协调器。
        Assert.NotSame(tsfCoordinator, router.Coordinator);
    }

    /// <summary>客户端替换/回退 IMM：旧协调器退订，新协调器接管；旧 epoch 不得再撞唤醒宿主。</summary>
    [Fact]
    public void 活动协调器切换后旧协调器不再唤醒宿主()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        var oldCoordinator = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(oldCoordinator);

        var signals = 0;
        router.AttachHostSafePhaseSignal(() => signals++);

        oldCoordinator.ExitSafePhase();
        Assert.Equal(1, signals);

        // 回退/新 epoch：活动协调器切换。
        var newCoordinator = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(newCoordinator);

        // 旧 epoch 的迟到退出**不得**再进入宿主。
        oldCoordinator.ExitSafePhase();
        Assert.Equal(1, signals);

        // 新活动协调器必须已经接上（否则安全边界通知会再次断链）。
        newCoordinator.ExitSafePhase();
        Assert.Equal(2, signals);
    }

    /// <summary>
    /// 解绑后宿主回调不再被调用；而路由器级 <c>SafePhaseExited</c> 订阅仍然有效
    /// （它按设计转发当前活动协调器）。释放后同样不得再进入宿主回调。
    /// </summary>
    [Fact]
    public void 解绑与释放后不再通知宿主()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        var coordinator = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(coordinator);

        var signals = 0;
        router.AttachHostSafePhaseSignal(() => signals++);

        coordinator.ExitSafePhase();
        Assert.Equal(1, signals);
        Assert.Equal(1, router.HostSignalNotificationCount);

        // 解绑：宿主回调不再被调用。
        router.DetachHostSafePhaseSignal();
        coordinator.ExitSafePhase();
        Assert.Equal(1, signals);

        // 释放后：宿主回调仍不得被调用（已释放 host 不会被旧协调器唤醒）。
        router.Dispose();
        coordinator.ExitSafePhase();
        Assert.Equal(1, signals);
    }

    /// <summary>解绑后再切换活动协调器，不得残留旧订阅（否则双触发）。</summary>
    [Fact]
    public void 解绑后重新绑定的活动协调器成对搬迁移除旧订阅()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        var clientA = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(clientA);

        router.AttachHostSafePhaseSignal(() => { });
        router.DetachHostSafePhaseSignal();

        // 解绑后再切协调器：旧订阅（若未成对摘除）会残留并在下面双触发。
        var clientB = new HostInputCoordinator();
        router.AttachTsfCoordinatorForTesting(clientB);

        var signals = 0;
        router.AttachHostSafePhaseSignal(() => signals++);

        clientA.ExitSafePhase();
        Assert.Equal(0, signals);

        clientB.ExitSafePhase();
        Assert.Equal(1, signals);
        Assert.Equal(1, router.HostSignalNotificationCount);
    }

    /// <summary>
    /// 队列非空时，一次真实 drain 内的通知次数必须与真实边界次数同阶（有界），
    /// 不得“队列不空就无限自激唤醒”；排空后不得再请求唤醒。
    /// </summary>
    [Fact]
    public void 队列非空时安全边界通知有界且排空后不再唤醒()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);
        var dispatcher = new OpenTkUiDispatcher();

        router.AttachHostSafePhaseSignal(LazyWake(dispatcher, h));

        // 三条延后输入（默认 IMM 协调器即活动协调器）。
        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('1'));
        h.Adapter.HandleTextInput(new TextInputEventArgs('2'));
        h.Adapter.HandleTextInput(new TextInputEventArgs('3'));
        Assert.Equal(3, router.Coordinator.DeferredCount);

        var beforeSignals = router.HostSignalNotificationCount;

        // 一次主动交付排空队列：每次真实退出至多一次通知，因此通知数 ≤ 实际交付条数。
        router.ForceUnsafeForTesting = false;
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();

        Assert.Equal(3, delivered);
        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.InRange(router.HostSignalNotificationCount - beforeSignals, 0, delivered);

        // 排空后：主动交付为 no-op，且不得再请求任何唤醒（不持续重绘/不自激）。
        var wakeAfterDrain = dispatcher.WakeUpCount;
        Assert.Equal(0, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Equal(wakeAfterDrain, dispatcher.WakeUpCount);
    }

    /// <summary>持续不安全时不产生周期性通知/唤醒；只有真实边界退出各通知一次。</summary>
    [Fact]
    public void 持续不安全时不周期轮询只有真实边界通知()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);
        var dispatcher = new OpenTkUiDispatcher();

        router.AttachHostSafePhaseSignal(LazyWake(dispatcher, h));

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('w'));

        var wakeAfterQueue = dispatcher.WakeUpCount;
        var signalsAfterQueue = router.HostSignalNotificationCount;

        // 持续不安全：反复尝试交付都不产生额外通知或唤醒（证明不是周期轮询）。
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(0, h.Adapter.DeliverDeferredAtSafePhase());
        }

        Assert.Equal(wakeAfterQueue, dispatcher.WakeUpCount);
        Assert.Equal(signalsAfterQueue, router.HostSignalNotificationCount);
        Assert.Equal(1, router.Coordinator.DeferredCount);

        // 真实边界退出 → 恰好一次通知 + 一次唤醒（有界的一次重试机会）。
        router.ForceUnsafeForTesting = false;
        router.Coordinator.ExitSafePhase();
        Assert.Equal(signalsAfterQueue + 1, router.HostSignalNotificationCount);
        Assert.Equal(wakeAfterQueue + 1, dispatcher.WakeUpCount);
    }
}
