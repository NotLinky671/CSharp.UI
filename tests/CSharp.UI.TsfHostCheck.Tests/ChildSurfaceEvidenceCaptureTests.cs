using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R29B review-6 新增覆盖：<see cref="ChildSurfaceEvidenceCapture.RunStage"/> 共享逐张
/// 证据采集操作 + <see cref="ChildSafePointOwnershipGate"/> 归属门禁 + 部分恢复
/// 故障下 finally 仍尽力回滚。全托管替身驱动——无 native 调用。
/// </summary>
public sealed class ChildSurfaceEvidenceCaptureTests
{
    private const int Pid = 4242;
    private const uint UiTid = 77;
    private static readonly IntPtr Root = new(0x100);
    private static readonly IntPtr Input = new(0x200);

    private static LifecycleWindowFacts GoodFacts(
        bool rootValid = true, bool inputValid = true,
        IntPtr? foreground = null, IntPtr? focus = null)
        => new(rootValid, inputValid, UiTid, Pid, UiTid, Pid,
            foreground ?? Root, focus ?? Input);

    private static readonly string VerifiedProfile = ImeIdentity.Describe(0,
        TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    private static LifecycleSnapshot Baseline()
        => new("ni", 0, 2, 0, true, VerifiedProfile, 1, 0, 7, 3);

    private static SafePointProbe MakeProbe(bool ok, string detail, LifecycleSnapshot snap)
        => new(ok, detail, snap.Text, snap.SpanStart, snap.SpanEnd, snap.Writes,
            snap.Composing, snap.Profile, snap.CompositionBeginCount,
            snap.CompositionEndCount, snap.CompositionSessionId,
            snap.InputFocusChangeCount);

    private static SafePointProbe OkProbe() => MakeProbe(true, "ok", Baseline());

    // ---- ChildSafePointOwnershipGate --------------------------------------

    [Fact]
    public void OwnershipGateAcceptsOwnValidChildSurface()
    {
        Assert.True(ChildSafePointOwnershipGate.OwnsChildSurface(
            GoodFacts(), Root, Input, UiTid, Pid));
    }

    [Theory]
    [InlineData("root-invalid")]
    [InlineData("input-invalid")]
    [InlineData("root-pid-foreign")]
    [InlineData("input-pid-foreign")]
    [InlineData("root-tid-foreign")]
    [InlineData("input-tid-foreign")]
    [InlineData("foreground-not-root")]
    [InlineData("focus-not-input")]
    public void OwnershipGateRejectsEachViolation(string which)
    {
        var f = GoodFacts();
        f = which switch
        {
            "root-invalid" => f with { RootValid = false },
            "input-invalid" => f with { InputValid = false },
            "root-pid-foreign" => f with { RootPid = 9999 },
            "input-pid-foreign" => f with { InputPid = 9999 },
            "root-tid-foreign" => f with { RootTid = 999 },
            "input-tid-foreign" => f with { InputTid = 999 },
            "foreground-not-root" => f with { Foreground = new IntPtr(0xDEAD) },
            "focus-not-input" => f with { Focus = new IntPtr(0xBEEF) },
            _ => f,
        };
        Assert.False(ChildSafePointOwnershipGate.OwnsChildSurface(
            f, Root, Input, UiTid, Pid), which);
    }

    [Fact]
    public void OwnershipGateRejectsZeroHandles()
    {
        // HWND 为零（已回收/未创建）绝不允许进入 profile 查询。
        Assert.False(ChildSafePointOwnershipGate.OwnsChildSurface(
            GoodFacts(), IntPtr.Zero, Input, UiTid, Pid));
        Assert.False(ChildSafePointOwnershipGate.OwnsChildSurface(
            GoodFacts(), Root, IntPtr.Zero, UiTid, Pid));
    }

    [Fact]
    public void FactSampleEqualityDetectsTransientChange()
    {
        var a = new LifecycleFactSample(Root, Input, 0, 0);
        var same = a;
        Assert.True(ChildSafePointOwnershipGate.SameFacts(a, same));

        var focusMoved = a with { Focus = new IntPtr(0x99) };
        Assert.False(ChildSafePointOwnershipGate.SameFacts(a, focusMoved));

        var lockHeld = a with { LockType = 1 };
        Assert.False(ChildSafePointOwnershipGate.SameFacts(a, lockHeld));

        var reentrant = a with { LayoutDepth = 1 };
        Assert.False(ChildSafePointOwnershipGate.SameFacts(a, reentrant));
    }

    // ---- RunStage：每张抓图 pre→capture→post --------------------------------

    [Fact]
    public void SuccessfulStageRunsPreCapturePostInOrder()
    {
        var calls = new List<string>();
        var records = new List<(string kind, IDictionary<string, object?> fields)>();
        var baseline = Baseline();
        var rect = TsfRect.FromLtrb(10, 20, 110, 220);

        var result = ChildSurfaceEvidenceCapture.RunStage("s1", "root",
            () => { calls.Add("probe"); return OkProbe(); },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("p.png", rect); },
            baseline,
            (k, f) => { calls.Add("record"); records.Add((k, f)); });

        Assert.True(result.Ok);
        Assert.Equal("p.png", result.Path);
        Assert.Equal(rect, result.Rect);
        Assert.Equal(new[] { "probe", "capture", "probe", "record" }, calls);
    }

    [Fact]
    public void PreProbeFailureSkipsCaptureAndPost()
    {
        var calls = new List<string>();
        var baseline = Baseline();

        var result = ChildSurfaceEvidenceCapture.RunStage("s2", "root",
            () => { calls.Add("probe"); return MakeProbe(false, "fg-lost", Baseline()); },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("p.png", default); },
            baseline,
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("pre-root safe point failed", result.Reason);
        Assert.Equal(new[] { "probe", "record" }, calls);
    }

    [Fact]
    public void PreProbeInvariantViolationSkipsCapture()
    {
        var calls = new List<string>();
        var baseline = Baseline();
        var drifted = MakeProbe(true, "ok",
            new LifecycleSnapshot("changed", 0, 2, 0, true,
                VerifiedProfile, 1, 0, 7, 3));

        var result = ChildSurfaceEvidenceCapture.RunStage("s3", "root",
            () => { calls.Add("probe"); return drifted; },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("p.png", default); },
            baseline,
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("pre-root invariant violated", result.Reason);
        Assert.Equal(new[] { "probe", "record" }, calls);
    }

    [Fact]
    public void PreProbeThrowSkipsCapture()
    {
        var calls = new List<string>();
        var result = ChildSurfaceEvidenceCapture.RunStage("s4", "root",
            () => { calls.Add("probe"); throw new InvalidOperationException("native"); },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("p.png", default); },
            Baseline(),
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("pre-root safe point threw", result.Reason);
        Assert.Equal(new[] { "probe", "record" }, calls);
    }

    [Fact]
    public void CaptureNullFailsAndSkipsPost()
    {
        var calls = new List<string>();
        var result = ChildSurfaceEvidenceCapture.RunStage("s5", "monitor",
            () => { calls.Add("probe"); return OkProbe(); },
            () => { calls.Add("capture"); return null; },
            Baseline(),
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Equal("monitor capture failed", result.Reason);
        // 抓图返回 null → 不执行后置 probe。
        Assert.Equal(new[] { "probe", "capture", "record" }, calls);
    }

    [Fact]
    public void CaptureThrowFailsAndSkipsPost()
    {
        var calls = new List<string>();
        var result = ChildSurfaceEvidenceCapture.RunStage("s6", "monitor",
            () => { calls.Add("probe"); return OkProbe(); },
            () => { calls.Add("capture"); throw new IOException("disk"); },
            Baseline(),
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("monitor capture threw", result.Reason);
        Assert.Equal(new[] { "probe", "capture", "record" }, calls);
    }

    [Fact]
    public void PostProbeFailureMarksCaptureInvalid()
    {
        var calls = new List<string>();
        var n = 0;
        var result = ChildSurfaceEvidenceCapture.RunStage("s7", "root",
            () =>
            {
                calls.Add("probe");
                n++;
                return n == 1 ? OkProbe()
                    : MakeProbe(false, "fg-lost-after", Baseline());
            },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("p.png", TsfRect.FromLtrb(1,2,3,4)); },
            Baseline(),
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("post-root safe point failed", result.Reason);
        // 抓图发生但被判无效——Path 不得当有效证据返回。
        Assert.Null(result.Path);
        Assert.Equal(new[] { "probe", "capture", "probe", "record" }, calls);
    }

    [Fact]
    public void PostInvariantViolationMarksCaptureInvalid()
    {
        var calls = new List<string>();
        var n = 0;
        var drifted = MakeProbe(true, "ok",
            new LifecycleSnapshot("ni", 0, 2, 1, true,   // writes=1 漂移
                VerifiedProfile, 1, 0, 7, 3));

        var result = ChildSurfaceEvidenceCapture.RunStage("s8", "monitor",
            () => { calls.Add("probe"); n++; return n == 1 ? OkProbe() : drifted; },
            () => { calls.Add("capture"); return new EvidenceCaptureResult("m.png", default); },
            Baseline(),
            (k, f) => calls.Add("record"));

        Assert.False(result.Ok);
        Assert.Contains("post-monitor invariant violated", result.Reason);
        Assert.Null(result.Path);
    }

    /// <summary>root 失败时调用方必须停：模拟 RunStage 返回值驱动的顺序编排。</summary>
    [Fact]
    public void RootFailureMeansMonitorIsNeverCaptured()
    {
        var calls = new List<string>();
        var baseline = Baseline();
        var records = new List<string>();

        EvidenceStageResult Root()
            => ChildSurfaceEvidenceCapture.RunStage("s9", "root",
                () => { calls.Add("root-probe"); return MakeProbe(false, "fg-lost", Baseline()); },
                () => { calls.Add("root-capture"); return new EvidenceCaptureResult("r.png", default); },
                baseline, (k, f) => records.Add(k));

        EvidenceStageResult Monitor()
            => ChildSurfaceEvidenceCapture.RunStage("s9", "monitor",
                () => { calls.Add("mon-probe"); return OkProbe(); },
                () => { calls.Add("mon-capture"); return new EvidenceCaptureResult("m.png", default); },
                baseline, (k, f) => records.Add(k));

        // 生产编排：root 失败 → 直接返回，不调用 Monitor。
        var root = Root();
        var monitorOk = root.Ok ? Monitor().Ok : false;

        Assert.False(root.Ok);
        Assert.False(monitorOk);
        Assert.DoesNotContain("mon-capture", calls);
        Assert.DoesNotContain("mon-probe", calls);
    }

    [Fact]
    public void RecordThrowDoesNotFlipResultOrHalt()
    {
        var result = ChildSurfaceEvidenceCapture.RunStage("s10", "root",
            () => OkProbe(),
            () => new EvidenceCaptureResult("p.png", TsfRect.FromLtrb(0, 0, 4, 4)),
            Baseline(),
            (k, f) => throw new InvalidOperationException("log down"));

        Assert.True(result.Ok);   // 抓图本身成功；日志失败不反转结果。
    }
}

/// <summary>
/// R29B review-6：部分恢复故障覆盖在 <see cref="ChildSurfaceGeometryExecutorTests"/> 内
/// （需要访问私有 FakeHost）；本文件只覆盖共享门禁/共享采集操作——二者为
/// production/test 共用的真实控制流。
/// </summary>
