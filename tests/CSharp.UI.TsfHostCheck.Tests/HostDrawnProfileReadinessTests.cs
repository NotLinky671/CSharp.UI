using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R35 host-drawn 就绪等待的注入式单元测试：替身事实/替身 profile 读取驱动与生产
/// 完全相同的 <see cref="HostDrawnProfileReadiness"/> 门禁与等待循环——不发键、不激活
/// profile、不建窗口、不初始化 COM。
/// </summary>
public sealed class HostDrawnProfileReadinessTests
{
    private static readonly IntPtr Root = new(0x1111);
    private static readonly IntPtr Input = new(0x2222);
    private static readonly int Pid = Environment.ProcessId;
    private static readonly uint UiTid = NativeMethods.CurrentWin32ThreadId();

    private static HostDrawnProfileReadiness.GuardFacts OkFacts(
        IntPtr? foreground = null, IntPtr? focus = null,
        uint lockType = 0, int layoutDepth = 0)
        => new(true, true, UiTid, (uint)Pid, UiTid, (uint)Pid,
            foreground ?? Root, focus ?? Input, lockType, layoutDepth);

    private static (int, uint, ushort, Guid, Guid) MsPinyin()
        => (0, TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    private static (int, uint, ushort, Guid, Guid) WeType()
        => (0, TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
            new Guid("86598fb9-66a2-463e-b9c2-aeb906d477ad"), Guid.NewGuid());

    private static object? InlineInvoke(Func<object?> action) => action();

    private static List<(string Kind, IDictionary<string, object?> Fields)> Sink()
        => new();

    private static Action<string, IDictionary<string, object?>> Trace(
        List<(string Kind, IDictionary<string, object?> Fields)> sink)
        => (kind, fields) => sink.Add((kind, fields));

    // ---- RequiresWait（默认 off 旁路）------------------------------------

    [Fact]
    public void RequiresWaitDefaultsToOff()
    {
        var options = new ScenarioOptions { OutDir = "." };
        Assert.False(HostDrawnProfileReadiness.RequiresWait(options));
        Assert.True(HostDrawnProfileReadiness.RequiresWait(
            new ScenarioOptions { OutDir = ".", HostDrawnCandidates = true }));
    }

    // ---- CheckGuard ------------------------------------------------------

    [Fact]
    public void GuardRejectsForeignForegroundFocusOwnershipLockAndReentry()
    {
        // 外来前台：不查询不输入。
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            OkFacts(foreground: new IntPtr(0x9999)), Root, Input, UiTid, Pid));

        // Win32 焦点不在 input child。
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            OkFacts(focus: new IntPtr(0x8888)), Root, Input, UiTid, Pid));

        // HWND 属外进程。
        var foreignPid = OkFacts();
        foreignPid = foreignPid with { RootPid = (uint)Pid + 1 };
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            foreignPid, Root, Input, UiTid, Pid));

        // HWND 属外 UI 线程。
        var foreignTid = OkFacts();
        foreignTid = foreignTid with { InputTid = UiTid + 1 };
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            foreignTid, Root, Input, UiTid, Pid));

        // store 持锁 / 布局回调重入：COM 查询不允许。
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            OkFacts(lockType: 1), Root, Input, UiTid, Pid));
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            OkFacts(layoutDepth: 1), Root, Input, UiTid, Pid));

        // HWND 失效。
        Assert.NotNull(HostDrawnProfileReadiness.CheckGuard(
            OkFacts() with { InputValid = false }, Root, Input, UiTid, Pid));
    }

    [Fact]
    public void GuardAcceptsOwnForegroundAndFocus()
    {
        Assert.Null(HostDrawnProfileReadiness.CheckGuard(
            OkFacts(), Root, Input, UiTid, Pid));
    }

    // ---- PollOnceOnUiThread ----------------------------------------------

    [Fact]
    public void PollReadyOnlyWhenBothGuardsStableAndProfileIsMsPinyin()
    {
        var result = HostDrawnProfileReadiness.PollOnceOnUiThread(
            () => OkFacts(), MsPinyin, Root, Input, UiTid, Pid);
        Assert.True(result.Ready);
        Assert.False(result.GuardFailed);
        Assert.True(ImeIdentity.IsVerifiableMicrosoftPinyin(result.ProfileTag));
    }

    [Fact]
    public void PollWrongProfileStaysNotReadyWithoutGuardFailure()
    {
        var result = HostDrawnProfileReadiness.PollOnceOnUiThread(
            () => OkFacts(), WeType, Root, Input, UiTid, Pid);
        Assert.False(result.Ready);
        Assert.False(result.GuardFailed); // 守卫成立：是身份不符，不是环境失守
        Assert.Contains("not Microsoft Pinyin", result.Detail);
    }

    [Fact]
    public void PollForeignForegroundSkipsComQueryEntirely()
    {
        var queried = false;
        var result = HostDrawnProfileReadiness.PollOnceOnUiThread(
            () => OkFacts(foreground: new IntPtr(0x9999)),
            () => { queried = true; return MsPinyin(); },
            Root, Input, UiTid, Pid);

        Assert.False(result.Ready);
        Assert.True(result.GuardFailed);
        Assert.False(queried); // pre-guard 失败 → 绝不发起 profile COM 查询
    }

    [Fact]
    public void PollForegroundLostDuringComQueryIsRejectedNotCredited()
    {
        var calls = 0;
        var result = HostDrawnProfileReadiness.PollOnceOnUiThread(
            () =>
            {
                calls++;
                // 第一次（pre）守卫成立；COM 查询后（post）前台失守 → 结果不采信。
                return calls == 1 ? OkFacts() : OkFacts(foreground: new IntPtr(0x9999));
            },
            MsPinyin, Root, Input, UiTid, Pid);

        Assert.False(result.Ready);
        Assert.True(result.GuardFailed);
        Assert.Contains("post-guard", result.Detail);
        Assert.Equal(2, calls); // pre + post 各采样一次
    }

    // ---- Run（有界等待循环）-----------------------------------------------

    [Fact]
    public void RunWrongProfileKeepsWaitingAndTimesOutWithoutAnyAction()
    {
        var sink = Sink();
        var queries = 0;
        var ok = HostDrawnProfileReadiness.Run(
            InlineInvoke,
            () => OkFacts(),
            facts => HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid),
            () =>
            {
                queries++;
                return HostDrawnProfileReadiness.PollOnceOnUiThread(
                    () => OkFacts(), WeType, Root, Input, UiTid, Pid);
            },
            timeoutMs: 1_300,
            trace: Trace(sink),
            profileQueryIntervalMs: 400);

        Assert.False(ok); // 错误 profile：一直等待、永不 ready、如实超时
        Assert.True(queries >= 1);
        Assert.DoesNotContain(sink, e => e.Fields.TryGetValue("state", out var s)
            && (string?)s == "ready");
        Assert.Contains(sink, e => (string?)e.Fields["state"] == "timeout");
        // 无激活/无键/无焦点操作——替身 profile 读取之外不存在任何动作通道可断言。
    }

    [Fact]
    public void RunBecomesReadyWhenOwnMsPinyinArrives()
    {
        var sink = Sink();
        var useMsPinyin = false;
        var guardTicks = 0;
        var ok = HostDrawnProfileReadiness.Run(
            InlineInvoke,
            () => OkFacts(),
            facts =>
            {
                guardTicks++;
                // 前两个 tick 前台失守（用户在切输入法），第三个 tick 起前台归位。
                return guardTicks <= 2
                    ? "simulated foreign foreground"
                    : HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid);
            },
            () =>
            {
                var profile = useMsPinyin ? MsPinyin() : WeType();
                var result = HostDrawnProfileReadiness.PollOnceOnUiThread(
                    () => OkFacts(), () => profile, Root, Input, UiTid, Pid);
                useMsPinyin = true; // 用户下一次查询前已切到微软拼音
                return result;
            },
            timeoutMs: 5_000,
            trace: Trace(sink),
            profileQueryIntervalMs: 100);

        Assert.True(ok);
        Assert.Contains(sink, e => (string?)e.Fields["state"] == "ready");
    }

    [Fact]
    public void RunForeignForegroundNeverIssuesComQuery()
    {
        var sink = Sink();
        var queries = 0;
        var ok = HostDrawnProfileReadiness.Run(
            InlineInvoke,
            () => OkFacts(foreground: new IntPtr(0x9999)),
            facts => HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid),
            () =>
            {
                queries++;
                return HostDrawnProfileReadiness.PollOnceOnUiThread(
                    () => OkFacts(), MsPinyin, Root, Input, UiTid, Pid);
            },
            timeoutMs: 600,
            trace: Trace(sink),
            profileQueryIntervalMs: 0);

        Assert.False(ok);
        Assert.Equal(0, queries); // 前台失守期间绝不发起 profile COM 查询
        Assert.Contains(sink, e => (string?)e.Fields["state"] == "timeout");
    }

    [Fact]
    public void RunIssuesFirstProfileQueryImmediatelyOnFirstGuardPass()
    {
        // R35A 回归：第一次守卫成立就必须查询，不能因"距上次查询不足间隔"被跳过
        // （旧实现用 long.MinValue 哨兵，now - lastQueryTick 溢出成负数 → 首次永远不查）。
        var sink = Sink();
        var queries = 0;
        var ok = HostDrawnProfileReadiness.Run(
            InlineInvoke,
            () => OkFacts(),
            facts => HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid),
            () =>
            {
                queries++;
                return HostDrawnProfileReadiness.PollOnceOnUiThread(
                    () => OkFacts(), WeType, Root, Input, UiTid, Pid);
            },
            timeoutMs: 900,
            trace: Trace(sink),
            profileQueryIntervalMs: 60_000); // 远大于总预算：只有"首次立即查询"才可能 >0

        Assert.False(ok);
        Assert.Equal(1, queries); // 首次立即查询，之后被间隔正确拦住
    }

    [Fact]
    public void RunLogsWaitingOnceThenReadyOnceWithoutSpammingEveryPoll()
    {
        var sink = Sink();
        var ok = HostDrawnProfileReadiness.Run(
            InlineInvoke,
            () => OkFacts(),
            facts => HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid),
            () => HostDrawnProfileReadiness.PollOnceOnUiThread(
                () => OkFacts(), MsPinyin, Root, Input, UiTid, Pid),
            timeoutMs: 3_000,
            trace: Trace(sink),
            profileQueryIntervalMs: 0);

        Assert.True(ok);
        var states = sink.Select(e => (string?)e.Fields["state"]).ToList();
        Assert.Equal("ready", states.Last());
        Assert.Equal(1, states.Count(s => s == "ready"));
        Assert.DoesNotContain("timeout", states);
        // 快速就绪路径最多 waiting+ready 两条；绝不每轮一条。
        Assert.True(sink.Count <= 2, $"expected <=2 log entries, got {sink.Count}");
    }

    [Fact]
    public void RunUiDispatchFailureFailsClosedNotPasses()
    {
        var sink = Sink();
        var ok = HostDrawnProfileReadiness.Run(
            _ => throw new InvalidOperationException("window gone"),
            () => OkFacts(),
            facts => HostDrawnProfileReadiness.CheckGuard(facts, Root, Input, UiTid, Pid),
            () => default,
            timeoutMs: 1_000,
            trace: Trace(sink));

        Assert.False(ok);
        Assert.Contains(sink, e => (string?)e.Fields["state"] == "timeout");
    }
}
