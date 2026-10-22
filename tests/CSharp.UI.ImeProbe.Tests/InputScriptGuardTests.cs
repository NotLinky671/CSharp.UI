using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// R29B：<see cref="InputScript.BeforeSendGuard"/> 与共享发送门的托管测试。
/// 经 <see cref="InputScript.NativeFacts"/> 注入桩体执行<b>真实</b> <c>SendRaw</c>
/// 控制流：守卫在前、本窗前台复核在后、MapVirtualKey/SendInput 只在两道门
/// 都通过后才发生。任何路径都不触达真实 user32。
/// </summary>
public sealed class InputScriptGuardTests : IDisposable
{
    private static readonly IntPtr OwnHwnd = new(0x1234);
    private static readonly IntPtr ForeignHwnd = new(0x5678);

    private readonly ProbeLog _log;

    public InputScriptGuardTests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inputscript-guard-{Guid.NewGuid():N}.jsonl");
        _log = new ProbeLog(path, echo: false);
    }

    public void Dispose() => _log.Dispose();

    /// <summary>计数桩体：生产真实 user32 委托的全部被测替身。</summary>
    private sealed class StubFacts
    {
        internal IntPtr Foreground { get; set; } = OwnHwnd;
        internal int GetForegroundCalls { get; private set; }
        internal int MapVirtualKeyCalls { get; private set; }
        internal int SendInputCalls { get; private set; }
        internal int DescribeWindowCalls { get; private set; }
        internal uint SendResult { get; set; } = 1;

        internal InputScript.SendNativeFacts Build()
            => new()
            {
                GetForegroundWindow = () =>
                {
                    GetForegroundCalls++;
                    return Foreground;
                },
                MapVirtualKeyToScanCode = _ =>
                {
                    MapVirtualKeyCalls++;
                    return 0x1E;
                },
                SendInput = input =>
                {
                    SendInputCalls++;
                    return SendResult;
                },
                GetLastError = () => 5,
                DescribeWindow = hwnd =>
                {
                    DescribeWindowCalls++;
                    return $"stub-describe:0x{hwnd.ToInt64():X}";
                },
            };
    }

    private InputScript NewScript(StubFacts stub, Func<string?>? guard = null)
        => new(_log, OwnHwnd, "guard-test")
        {
            NativeFacts = stub.Build(),
            BeforeSendGuard = guard,
        };

    [Fact]
    public void NullGuardPreservesSendPath()
    {
        var stub = new StubFacts();
        var script = NewScript(stub);

        Assert.True(script.PressVirtualKey(0x41, "a"));

        // 无守卫：foreground 复核 + 两次真实 send（down/up）照常发生。
        Assert.Equal(2, stub.SendInputCalls);
        Assert.Equal(2, stub.GetForegroundCalls);
        Assert.False(script.Aborted);
    }

    [Fact]
    public void GuardRefusalAbortsBeforeAnyNativeKeyCall()
    {
        var stub = new StubFacts();
        var guardCalls = 0;
        var script = NewScript(stub, () =>
        {
            guardCalls++;
            return "not-own-focus";
        });

        Assert.False(script.PressVirtualKey(0x41, "a"));

        // 拒绝发生在任何原生键调用之前：无 MapVirtualKey、无 SendInput，
        // 连前台查询都没有（守卫是第一道门）。
        Assert.True(script.Aborted);
        Assert.Equal(1, guardCalls);
        Assert.Equal(0, stub.SendInputCalls);
        Assert.Equal(0, stub.MapVirtualKeyCalls);
        Assert.Equal(0, stub.GetForegroundCalls);
    }

    [Fact]
    public void GuardExceptionFailsClosed()
    {
        var stub = new StubFacts();
        var script = NewScript(stub, () => throw new InvalidOperationException("injected"));

        Assert.False(script.PressVirtualKey(0x41, "a"));

        Assert.True(script.Aborted);
        Assert.Equal(0, stub.SendInputCalls);
        Assert.Equal(0, stub.MapVirtualKeyCalls);
    }

    [Fact]
    public void ScanCodeMappingIsSkippedOnGuardRefusal()
    {
        var stub = new StubFacts();
        var script = NewScript(stub, () => "refused");
        script.UseScanCodes = true; // 使能 MapVirtualKey 路径：守卫拒绝也必须不调用它

        Assert.False(script.PressVirtualKey(0x41, "a"));

        Assert.Equal(0, stub.MapVirtualKeyCalls);
        Assert.Equal(0, stub.SendInputCalls);
    }

    [Fact]
    public void GuardAllowThenSendCallsNativeInOrder()
    {
        var stub = new StubFacts();
        var order = new List<string>();
        var script = new InputScript(_log, OwnHwnd, "order")
        {
            BeforeSendGuard = () =>
            {
                order.Add("guard");
                return null;
            },
            NativeFacts = new InputScript.SendNativeFacts
            {
                GetForegroundWindow = () =>
                {
                    order.Add("foreground");
                    return OwnHwnd;
                },
                MapVirtualKeyToScanCode = _ => 0x1E,
                SendInput = input =>
                {
                    order.Add("send");
                    return 1;
                },
            },
            UseScanCodes = true,
        };

        Assert.True(script.PressVirtualKey(0x41, "a"));

        // 每次发送：guard → foreground → send（MapVirtualKey 在 build 内、send 前）。
        Assert.Equal(new[] { "guard", "foreground", "send", "guard", "foreground", "send" }, order);
    }

    [Fact]
    public void DownAndUpShareTheSameGate()
    {
        var stub = new StubFacts();
        var refusals = 0;
        // 第一次发送（down）放行，第二次（up）拒绝：keyup 也走同一个门。
        var script = NewScript(stub, () => ++refusals == 2 ? "second-refused" : null);

        Assert.False(script.PressVirtualKey(0x41, "a"));

        // down 真的发出了；up 被同一个守卫拒绝且没有任何原生调用。
        Assert.True(script.Aborted);
        Assert.Equal(1, stub.SendInputCalls);
        Assert.Equal(2, refusals);
    }

    [Fact]
    public void ForeignForegroundStillAbortsAfterGuardPasses()
    {
        var stub = new StubFacts { Foreground = ForeignHwnd };
        var script = NewScript(stub, () => null); // 守卫放行

        Assert.False(script.PressVirtualKey(0x41, "a"));

        // 守卫通过但本窗前台复核失败：拒绝向其他窗口发送（根前台守卫保持）。
        Assert.True(script.Aborted);
        Assert.Equal(0, stub.SendInputCalls);
        Assert.Equal(0, stub.MapVirtualKeyCalls);
        Assert.Equal(1, stub.GetForegroundCalls);
        // 拒绝日志的窗口描述必须走注入桩体，绝不回退到真实 user32（R29B 修正：
        // 注入 NativeFacts 后整条控制流不得有任何未注入的原生诊断调用）。
        Assert.Equal(1, stub.DescribeWindowCalls);
    }

    [Fact]
    public void InjectedFactsWithoutDescribeWindowNeverCallsNativeDescribe()
    {
        // 不提供 DescribeWindow：SendRaw 拒绝路径退化为纯句柄描述，
        // 绝不静默回退到真实 user32 GetWindowThreadProcessId/GetClassNameW。
        var stub = new StubFacts { Foreground = ForeignHwnd };
        var script = new InputScript(_log, OwnHwnd, "no-describe")
        {
            NativeFacts = new InputScript.SendNativeFacts
            {
                GetForegroundWindow = () => ForeignHwnd,
                MapVirtualKeyToScanCode = _ => 0x1E,
                SendInput = _ => 0,
                GetLastError = () => 5,
                // DescribeWindow 故意缺省
            },
            BeforeSendGuard = () => null,
        };

        Assert.False(script.PressVirtualKey(0x41, "a"));

        Assert.True(script.Aborted);
        Assert.Equal(0, stub.SendInputCalls);
        Assert.Equal(0, stub.MapVirtualKeyCalls);
    }

    [Fact]
    public void SendInputFailureAborts()
    {
        var stub = new StubFacts { SendResult = 0 };
        var script = NewScript(stub, () => null);

        Assert.False(script.PressVirtualKey(0x41, "a"));

        Assert.True(script.Aborted);
        Assert.Equal(1, stub.SendInputCalls);
    }

    [Fact]
    public void SendRepeatBurstStopsAtFirstRefusal()
    {
        var stub = new StubFacts();
        var guardCalls = 0;
        var script = NewScript(stub, () => ++guardCalls >= 3 ? "burst-stop" : null);

        Assert.False(script.SendRepeatBurst(0x41, "x", repeatCount: 5, holdMs: 1));

        Assert.True(script.Aborted);
        // 前两次发送成功，第三次被同一门拒绝。
        Assert.Equal(2, stub.SendInputCalls);
    }
}
