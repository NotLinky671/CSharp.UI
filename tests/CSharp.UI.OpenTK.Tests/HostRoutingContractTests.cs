using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a 的<b>纯托管</b>核心：<c>ITfKeystrokeMgr</c> 判定顺序、宿主有序协调、单一写入来源。
/// </summary>
/// <remarks>
/// 这些用例不触碰 COM/窗口，因此在任何平台都能确定性运行；
/// 真实原生顺序与线程证据由 <c>CSharp.UI.TsfHostBridgeCheck</c> 以 <c>[STAThread]</c> 进程入口给出。
/// </remarks>
public sealed class HostRoutingContractTests
{
    // ---------------------------------------------------------------- ITfKeystrokeMgr 官方顺序

    [Theory]
    // TestKeyDown 成功且 eaten，KeyDown 也成功且 eaten → 消费。
    [InlineData(0, true, 0, true, (int)TsfKeyRoutingDecision.Consumed)]
    // TestKeyDown 失败（例如 E_INVALIDARG）：即使 KeyDown 报 eaten 也不能消费。
    [InlineData(unchecked((int)0x80070057), true, 0, true, (int)TsfKeyRoutingDecision.PassToApplication)]
    // TestKeyDown 返回 S_FALSE（没有 key event sink）：不得调用 KeyDown、不得消费。
    [InlineData(1, true, 0, true, (int)TsfKeyRoutingDecision.PassToApplication)]
    // TestKeyDown 成功但 eaten=false：输入法不关心该键 → 交给应用。
    [InlineData(0, false, 0, true, (int)TsfKeyRoutingDecision.PassToApplication)]
    // TestKeyDown 说会处理，但 KeyDown 未 eaten（输入法改变主意）→ 应用仍须收到。
    [InlineData(0, true, 0, false, (int)TsfKeyRoutingDecision.PassToApplication)]
    // KeyDown 失败 → 不得消费。
    [InlineData(0, true, unchecked((int)0x80004005), true, (int)TsfKeyRoutingDecision.PassToApplication)]
    public void 按键消费必须同时满足测试与转发两次确认(
        int testHr, bool testEaten, int applyHr, bool applyEaten, int expected)
    {
        var actual = TsfKeyRouting.Decide(testHr, testEaten, applyHr, applyEaten);
        Assert.Equal((TsfKeyRoutingDecision)expected, actual);
    }

    [Fact]
    public void 未经KeyDown转发时绝不消费()
    {
        // 只做了 TestKeyDown（applyHr = null）不能消费：必须真的把键交给键盘管理器。
        Assert.Equal(
            TsfKeyRoutingDecision.PassToApplication,
            TsfKeyRouting.Decide(0, testEaten: true, applyHr: null, applyEaten: false));
    }

    [Fact]
    public void 只有按键消息进入TSF键盘判定()
    {
        Assert.True(TsfKeyRouting.IsKeyMessage(0x0100)); // WM_KEYDOWN
        Assert.True(TsfKeyRouting.IsKeyMessage(0x0101)); // WM_KEYUP
        Assert.True(TsfKeyRouting.IsKeyMessage(0x0104)); // WM_SYSKEYDOWN
        Assert.True(TsfKeyRouting.IsKeyMessage(0x0105)); // WM_SYSKEYUP

        // 翻译产物与其它输入不得被当作按键判定，避免“顺手吞掉”。
        Assert.False(TsfKeyRouting.IsKeyMessage(0x0102)); // WM_CHAR
        Assert.False(TsfKeyRouting.IsKeyMessage(0x0200)); // WM_MOUSEMOVE
        Assert.False(TsfKeyRouting.IsKeyMessage(0x00FF)); // WM_IME_SETCONTEXT

        Assert.True(TsfKeyRouting.IsKeyUp(0x0101));
        Assert.True(TsfKeyRouting.IsKeyUp(0x0105));
        Assert.False(TsfKeyRouting.IsKeyUp(0x0100));
    }

    [Fact]
    public void 连续两次被消费的按键各自独立判定不共享标志()
    {
        // 模拟“两个缓冲的按键都被消费”：每次调用都重新判定，没有 _lastKeyEaten 之类的残留状态。
        var first = TsfKeyRouting.Decide(0, true, 0, true);
        var second = TsfKeyRouting.Decide(0, true, 0, true);
        Assert.Equal(TsfKeyRoutingDecision.Consumed, first);
        Assert.Equal(TsfKeyRoutingDecision.Consumed, second);

        // 紧接着一个不被处理的按键必须立刻回到应用路径（不被前一次消费影响）。
        var third = TsfKeyRouting.Decide(0, false, null, false);
        Assert.Equal(TsfKeyRoutingDecision.PassToApplication, third);
    }

    // ---------------------------------------------------------------- R2：MSG 改写语义

    [Fact]
    public void 消费必须改写MSG而不是只返回1()
    {
        // 只有 PM_REMOVE + 自有 HWND + 按键消息才允许改写。
        Assert.Equal(TsfHookMessageAction.Consumed,
            TsfMessageHookPolicy.Decide(0x0001, 0x0100, isOwnWindow: true, consumedByIme: true));

        // 改写结果必须是 WM_NULL（不参与翻译，因此不产生 WM_CHAR 回声）。
        var rewritten = TsfMessageHookPolicy.ConsumedMessage((IntPtr)0x1234, 42);
        Assert.Equal(0u, rewritten.Message);
        Assert.Equal(IntPtr.Zero, rewritten.WParam);
        Assert.Equal((IntPtr)0x1234, rewritten.Hwnd);
    }

    [Fact]
    public void PM_NOREMOVE与无关窗口绝不被改写()
    {
        // PM_NOREMOVE 只是窥视：不得消费、不得改写。
        Assert.Equal(TsfHookMessageAction.PassThrough,
            TsfMessageHookPolicy.Decide(0x0000, 0x0100, isOwnWindow: true, consumedByIme: true));

        // 非自有窗口：即使被判定消费也不得改写。
        Assert.Equal(TsfHookMessageAction.PassThrough,
            TsfMessageHookPolicy.Decide(0x0001, 0x0100, isOwnWindow: false, consumedByIme: true));

        // 非按键消息（例如 WM_CHAR 自身）不得被改写。
        Assert.Equal(TsfHookMessageAction.PassThrough,
            TsfMessageHookPolicy.Decide(0x0001, 0x0102, isOwnWindow: true, consumedByIme: true));
    }

    [Fact]
    public void WM_CHAR按Unicode字符判定而不是虚拟键()
    {
        // 未按 Shift 的 C 产生 U+0063；VK 是 0x43。混用会得到假通过。
        Assert.True(TsfMessageHookPolicy.IsCharacterEchoOf(0x43, (IntPtr)0x63));
        Assert.False(TsfMessageHookPolicy.IsCharacterEchoOf(0x43, (IntPtr)0x43));

        // A → U+0061。
        Assert.True(TsfMessageHookPolicy.IsCharacterEchoOf(0x41, (IntPtr)0x61));
        Assert.Equal(0x61, TsfMessageHookPolicy.UnshiftedCharacterFor(0x41));
        Assert.Equal(0x63, TsfMessageHookPolicy.UnshiftedCharacterFor(0x43));
    }
    // ---------------------------------------------------------------- 有序协调

    [Fact]
    public void 安全阶段可用时立即应用()
    {
        var coordinator = new HostInputCoordinator();
        var attempt = coordinator.TryBeginSafePhase(isOnOwnerThread: true, storeSafe: true, hostWorking: false);

        Assert.True(attempt.Applied);
        Assert.Equal(HostInputDeferralReason.None, attempt.Deferral);
        Assert.True(coordinator.IsInsideApply);
        coordinator.ExitSafePhase();
        Assert.False(coordinator.IsInsideApply);
        Assert.Equal(1, coordinator.AppliedTotal);
    }

    [Theory]
    [InlineData(false, true, false, (int)HostInputDeferralReason.ForeignThread)]
    [InlineData(true, false, false, (int)HostInputDeferralReason.InsideLock)]
    [InlineData(true, true, true, (int)HostInputDeferralReason.InsideHostWork)]
    public void 安全阶段不可用时给出明确原因(
        bool onOwnerThread, bool storeSafe, bool hostWorking, int expected)
    {
        var coordinator = new HostInputCoordinator();
        var attempt = coordinator.TryBeginSafePhase(onOwnerThread, storeSafe, hostWorking);

        Assert.False(attempt.Applied);
        Assert.Equal((HostInputDeferralReason)expected, attempt.Deferral);
        Assert.False(coordinator.IsInsideApply);
    }

    [Fact]
    public void 应用期间重入被拒绝而不是递归应用()
    {
        var coordinator = new HostInputCoordinator();
        Assert.True(coordinator.TryBeginSafePhase(true, true, false).Applied);

        // 仍在应用内部：再次进入必须被拒绝（COM 通知重入的典型情形）。
        var nested = coordinator.TryBeginSafePhase(true, true, false);
        Assert.False(nested.Applied);
        Assert.Equal(HostInputDeferralReason.InsideApply, nested.Deferral);

        coordinator.ExitSafePhase();
        Assert.True(coordinator.TryBeginSafePhase(true, true, false).Applied);
    }

    [Fact]
    public void 同一窗口只允许一个写入来源()
    {
        // 唯一假句柄（与其他用例/类不冲突）：xUnit 类间并行时不得清空全局注册表。
        var hwnd = (IntPtr)0x1234;

        Assert.True(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Imm, out var immLease, out _));
        try
        {
            Assert.Equal(HostWriterKind.Imm, HostWriterRegistry.CurrentOwner(hwnd));

            // 另一种来源必须失败，且给出可解释原因。
            Assert.False(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out _, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Equal(HostWriterKind.Imm, HostWriterRegistry.CurrentOwner(hwnd));
        }
        finally
        {
            Assert.True(HostWriterRegistry.Release(immLease));
        }

        Assert.Null(HostWriterRegistry.CurrentOwner(hwnd));
    }

    [Fact]
    public void 两个独立TSF会话不能同时占用同一窗口()
    {
        var hwnd = (IntPtr)0x9999;

        // 第一个真实 TSF 会话成功。
        Assert.True(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var first, out _));
        try
        {
            // 第二个**独立** TSF 会话（同 kind）必须被拒绝：同 kind 不等于同 owner。
            Assert.False(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var second, out var reason));
            Assert.Null(second);
            Assert.False(string.IsNullOrWhiteSpace(reason));

            // 旧租约仍可正常使用/释放。
            Assert.Equal(HostWriterKind.Tsf, HostWriterRegistry.CurrentOwner(hwnd));
        }
        finally
        {
            Assert.True(HostWriterRegistry.Release(first));
        }

        Assert.Null(HostWriterRegistry.CurrentOwner(hwnd));
    }

    [Fact]
    public void 只有真实持有者能释放租约()
    {
        var hwnd = (IntPtr)0x7777;

        Assert.True(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var holder, out _));
        try
        {
            // 用一个“别人的”租约（不同实例）尝试释放：必须失败，不能凭 kind 抢占。
            var foreign = new HostWriterLease(hwnd, HostWriterKind.Tsf, leaseId: 999_999);
            Assert.False(HostWriterRegistry.Release(foreign));
            Assert.Equal(HostWriterKind.Tsf, HostWriterRegistry.CurrentOwner(hwnd));
        }
        finally
        {
            // 真实持有者仍能释放。
            Assert.True(HostWriterRegistry.Release(holder));
        }

        Assert.Null(HostWriterRegistry.CurrentOwner(hwnd));

        // 重复释放：如实返回 false（不产生负计数）。
        Assert.False(HostWriterRegistry.Release(holder));
    }

    [Fact]
    public void 不同窗口互不影响()
    {
        var a = (IntPtr)0xA;
        var b = (IntPtr)0xB;

        Assert.True(HostWriterRegistry.TryAcquire(a, HostWriterKind.Imm, out var la, out _));
        Assert.True(HostWriterRegistry.TryAcquire(b, HostWriterKind.Tsf, out var lb, out _));
        try
        {
            Assert.Equal(HostWriterKind.Imm, HostWriterRegistry.CurrentOwner(a));
            Assert.Equal(HostWriterKind.Tsf, HostWriterRegistry.CurrentOwner(b));
        }
        finally
        {
            HostWriterRegistry.Release(la);
            HostWriterRegistry.Release(lb);
        }
    }

    [Fact]
    public void 空句柄不能被占用()
    {
        Assert.False(HostWriterRegistry.TryAcquire(IntPtr.Zero, HostWriterKind.Tsf, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // ---------------------------------------------------------------- 有序交付闸门

}