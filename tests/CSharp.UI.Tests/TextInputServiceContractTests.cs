using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-02：文本输入客户端/服务契约、能力查询与已提交文本回退/注入接缝。全部为无窗口 CPU 测试。
/// </summary>
public class TextInputServiceContractTests
{
    [Fact]
    public void NoTextInputService_DeclaresCommittedTextOnly_AndReportsNoExecutedActions()
    {
        var service = NoTextInputService.Instance;
        Assert.True(service.Capabilities.CommittedText);
        Assert.False(service.Capabilities.Composition);
        Assert.False(service.Capabilities.CandidateAnchor);
        Assert.Null(service.ActiveClient);
        Assert.Same(service, TextInputForwarder.EffectiveService(null));

        var client = new RecordingTextInputClient();
        Assert.False(service.StartSession(client));
        Assert.False(service.EndSession());
        Assert.False(service.RequestCancel());
        Assert.False(service.PublishGeometry(default));

        // R3：只报告拒绝，不把请求阶段映射成已执行动作。
        var result = service.ApplyComposition([CompositionEvent.Result(0, 0, 0, 0, "你好")]);
        Assert.Equal(TextInputOutcome.Rejected, result.Outcome);
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.Started));
        Assert.False(result.Handled);
        Assert.True(result.IsNoOp);
        Assert.Contains("未执行", result.RejectionReason!);
        Assert.NotNull(result.ToString());
    }

    [Fact]
    public void TextInputCapabilities_RejectsCapabilityInconsistency()
    {
        Assert.Throws<ArgumentException>(() => new TextInputCapabilities(committedText: false, composition: true));
        Assert.Throws<ArgumentException>(() => new TextInputCapabilities(committedText: false, candidateAnchor: true));

        Assert.False(TextInputCapabilities.None.CommittedText);
        Assert.False(TextInputCapabilities.None.Composition);
        Assert.NotNull(TextInputCapabilities.None.Reason);

        var full = TextInputCapabilities.Full;
        Assert.True(full.CommittedText && full.Composition && full.CandidateAnchor);

        var custom = new TextInputCapabilities(true, composition: true, candidateAnchor: false, reason: "XIM 预编辑未暴露");
        Assert.True(custom.Composition);
        Assert.False(custom.CandidateAnchor);
        Assert.Equal("XIM 预编辑未暴露", custom.Reason);
    }

    [Fact]
    public void RuntimeServices_DefaultInjectionIsNull_AndFallsBackToCommittedTextOnly()
    {
        using var app = new TestApp(new Text("hi"));
        Assert.Null(app.Runtime.Services.TextInputService);
        Assert.Same(NoTextInputService.Instance, TextInputForwarder.EffectiveService(app.Runtime.Services.TextInputService));
    }

    [Fact]
    public void InjectedService_IsObservableThroughRuntimeServices()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        using var runtime = new UiRuntime(new Text("hi"), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            TextInputService = service,
        });

        Assert.Same(service, runtime.Services.TextInputService);
        Assert.True(runtime.Services.TextInputService!.Capabilities.Composition);
    }

    [Fact]
    public void StartSessionAndEndSession_AreIdempotentAndReflectedInActiveClient()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        var client = new RecordingTextInputClient();
        Assert.True(service.StartSession(client));
        Assert.Same(client, service.ActiveClient);
        Assert.Equal(1, service.StartCount);

        Assert.True(service.EndSession());            // 第一次真的结束会话
        Assert.Null(service.ActiveClient);
        Assert.Equal(1, service.EndCallCount);
        Assert.Equal(1, service.EndedSessionCount);

        Assert.False(service.EndSession());           // 幂等：没有会话时返回 false
        Assert.Equal(2, service.EndCallCount);
        Assert.Equal(1, service.EndedSessionCount);
    }

    [Fact]
    public void Forwarder_AppliesCompositionThroughService_WhenCapableClientIsActive()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        var client = new RecordingTextInputClient();
        service.StartSession(client);

        var events = new[] { CompositionEvent.Result(0, 0, 0, 0, "你好", new TextRange(0, 0)) };
        var result = TextInputForwarder.ApplyComposition(service, events);

        Assert.Same(events, service.LastBatch);
        Assert.Equal(1, service.ApplyCount);
        Assert.True(result.Handled);
        Assert.Equal(TextInputOutcome.ResultCommitted, result.Outcome);
    }

    [Fact]
    public void Forwarder_EmptyBatchIsNotHandled_AndDoesNotCallService()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        service.StartSession(new RecordingTextInputClient());

        var result = TextInputForwarder.ApplyComposition(service, []);
        Assert.False(result.Handled);
        Assert.Equal(TextInputOutcome.None, result.Outcome);
        Assert.Equal(0, service.ApplyCount);
    }

    // R3：无组合能力只报告拒绝；不因为批次含 Result 就声称已执行或要求重投。
    [Fact]
    public void Forwarder_NoCompositionCapability_RejectsWithoutReplay()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.CommittedTextOnly);

        foreach (var events in new IReadOnlyList<CompositionEvent>[]
        {
            [CompositionEvent.Result(0, 0, 0, 0, "你好", new TextRange(0, 0))],
            [CompositionEvent.Start(0, 0, 0, 0, default, "n")],
        })
        {
            var result = TextInputForwarder.ApplyComposition(service, events);
            Assert.False(result.Handled);
            Assert.Equal(TextInputOutcome.Rejected, result.Outcome);
            Assert.True(result.IsNoOp);
            Assert.Contains("没有组合能力", result.RejectionReason!);
        }

        Assert.Equal(0, service.ApplyCount); // 无能力时不把批次交给服务
    }

    // R3：有组合能力但没有活动客户端 = 归属不明确；不执行也不重投。
    [Fact]
    public void Forwarder_CompositionCapableButNoClient_RejectsWithoutReplay()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        var result = TextInputForwarder.ApplyComposition(
            service,
            [CompositionEvent.Result(0, 0, 0, 0, "你好", new TextRange(0, 0))]);

        Assert.False(result.Handled);
        Assert.Equal(TextInputOutcome.Rejected, result.Outcome);
        Assert.Contains("没有活动客户端", result.RejectionReason!);
        Assert.Equal(0, service.ApplyCount);
    }

    [Fact]
    public void Forwarder_NullService_RejectsWithoutReplay()
    {
        var result = TextInputForwarder.ApplyComposition(null, [CompositionEvent.Result(0, 0, 0, 0, "你好")]);

        Assert.False(result.Handled);
        Assert.Equal(TextInputOutcome.Rejected, result.Outcome);
        Assert.Contains("没有文本输入服务", result.RejectionReason!);
    }

    // R3：归属消失（会话结束）后，旧批次不得被重投到新焦点。
    [Fact]
    public void Forwarder_OldClientResultIsNotReroutedAfterOwnershipIsLost()
    {
        var service = new RecordingTextInputService(TextInputCapabilities.Full);
        var oldClient = new RecordingTextInputClient { Document = new TextSnapshot("old", TextSelection.Caret(3), 1) };
        service.StartSession(oldClient);

        var batch = new[] { CompositionEvent.Result(0, 0, 1, 0, "你好", new TextRange(0, 0)) };
        var applied = TextInputForwarder.ApplyComposition(service, batch);
        Assert.True(applied.Handled);
        Assert.Same(batch, service.LastBatch);

        service.EndSession();
        var lost = TextInputForwarder.ApplyComposition(service, batch);
        Assert.False(lost.Handled);
        Assert.Equal(TextInputOutcome.Rejected, lost.Outcome);
    }

    [Fact]
    public void CommittedTextFallback_StillReachesTheFocusedTextField_Unchanged()
    {
        // 既有已提交文本通道必须保持原样：新契约不改变 TextInputEvent/TextField 行为。
        var host = new FieldHost();
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();
        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        TextInputForwarder.DispatchCommittedText(app.Runtime, "中文abc");
        app.Runtime.ProcessPendingWork();

        Assert.Equal("中文abc", host.Text!.PeekValue());

        TextInputForwarder.DispatchCommittedText(app.Runtime, null);
        TextInputForwarder.DispatchCommittedText(app.Runtime, string.Empty);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("中文abc", host.Text.PeekValue());
    }

    [Fact]
    public void CommittedTextFallback_ThrowsForNullRuntime()
    {
        Assert.Throws<ArgumentNullException>(() => TextInputForwarder.DispatchCommittedText(null!, "x"));
    }

    [Fact]
    public void TextInputGeometryAndState_ReportCaretAndPreeditPresence()
    {
        var empty = TextInputGeometry.Unavailable;
        Assert.False(empty.HasCaret);
        Assert.False(empty.HasComposition);

        var geometry = new TextInputGeometry(new Rect(10, 4, 1.5, 18), new Rect(10, 4, 30, 18), new Rect(12, 26, 60, 120));
        Assert.True(geometry.HasCaret);
        Assert.True(geometry.HasComposition);
        Assert.Equal(60, geometry.ExclusionBounds.Width);

        var document = new TextSnapshot("ni", TextSelection.Caret(2), 3);
        var state = new TextInputState(document, "hao", geometry, composing: true);
        Assert.True(state.IsComposing);
        Assert.Equal("hao", state.Preedit);             // 预编辑与文档分离
        Assert.Equal("ni", state.Document.Text);        // 文档只含已提交文本
        Assert.Equal(geometry, state.Geometry);

        var idle = new TextInputState(document);
        Assert.False(idle.IsComposing);
        Assert.Equal(string.Empty, idle.Preedit);
        // 显示串必须按组合身份的替换区构造，本阶段不提供拼接快捷属性。
        Assert.Null(typeof(TextInputState).GetProperty("DisplayText"));
    }

    [Fact]
    public void RecordingClient_ReportsDocumentAndApplyResult()
    {
        var client = new RecordingTextInputClient();
        var batch = new[] { CompositionEvent.Start(0, 0, 0, 0, default, "n") };

        var result = client.ApplyComposition(batch);

        Assert.Equal(1, client.ApplyCount);
        Assert.Same(batch, client.LastBatch);
        Assert.True(result.Handled);
        Assert.Equal(TextInputOutcome.Started, result.Outcome);
        Assert.True(client.IsComposing);              // Start 事件使客户端进入组合状态

        Assert.True(client.CancelComposition());      // 取消未提交组合成功
        Assert.False(client.IsComposing);
        Assert.False(client.CancelComposition());     // 重复取消幂等
    }

    [Fact]
    public void ContractsDoNotMutateUiFromBuildOrMeasure_AndDoNotTouchDocument()
    {
        // 契约类型不认识 View/ViewNode/LayoutContext；唯一写入 UI 的入口是运行时输入分发。
        // 因此“不得在 Build/Measure 里改 UI”由类型边界保证：这里验证构建/重建/测量前后文档不变。
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        var binding = ((TextField)node.View).Text;

        Assert.Equal("abc", binding.Value);
        Assert.Equal(1, host.BuildCount);

        // 再跑一轮布局（Measure/Arrange）与重建：文档与绑定不得被契约类型改写。
        app.Runtime.InvalidateLayout(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("abc", binding.Value);
        Assert.Equal("abc", host.Text!.PeekValue());
    }

    [Fact]
    public void FailingCapabilities_FocusAndCommittedTextStillWork_NoRetryOrSelfWake()
    {
        // TI-08 §5b（主审收敛版）：注入能力查询抛错的服务——
        // 焦点照常建立、异常不穿透 SetFocus、已提交字符按真实事务写绑定（setter 精确计数），
        // 此后无输入的帧推进/待办处理不产生新诊断、不改文本——不自动重试/不自激。
        var writes = 0;
        var value = "ab";
        var binding = new Binding<string>(() => value, v => { writes++; value = v; });
        var service = new ThrowingCapabilitiesService();
        using var runtime = new UiRuntime(new TextField(binding), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            TextInputService = service,
        });
        runtime.SetViewport(new Size(400, 60), 1);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        var node = runtime.Root;

        var diagnosticsBefore = runtime.Diagnostics.Count;
        runtime.SetFocus(node);                                     // 异常不穿透
        runtime.ProcessPendingWork();

        Assert.True(node.IsFocused);
        // 恰好一条有界诊断（能力查询失败已记录；不重复、不静默）。
        Assert.Equal(diagnosticsBefore + 1, runtime.Diagnostics.Count);

        // 已提交字符按真实编辑事务写绑定：恰好一次 setter、值精确。
        runtime.DispatchTextInput(new TextInputEvent("Z"));
        runtime.ProcessPendingWork();
        Assert.Equal(1, writes);
        Assert.Equal("Zab", value);

        // 无新输入的多轮待办/帧推进：诊断不再增长、文本不变、无待办自激。
        var stableDiagnostics = runtime.Diagnostics.Count;
        for (var i = 0; i < 3; i++)
        {
            runtime.ProcessPendingWork();
            runtime.BuildFrame();
        }

        Assert.Equal(stableDiagnostics, runtime.Diagnostics.Count);
        Assert.Equal("Zab", value);
        Assert.Equal(1, writes);
        Assert.False(runtime.HasPendingBuild);
    }

    [Fact]
    public void Forwarder_FailingCapabilities_RejectWithoutThrowingOrWriting()
    {
        // ApplyComposition 对抛错 getter 返回显式 Rejected：异常不泄漏、不写目标、不重投、不伪报已执行。
        var service = new ThrowingCapabilitiesService();
        var result = TextInputForwarder.ApplyComposition(
            service,
            [CompositionEvent.Result(0, 0, 0, 0, "你好", new TextRange(0, 0))]);

        Assert.False(result.Handled);
        Assert.Equal(TextInputOutcome.Rejected, result.Outcome);
        Assert.True(result.IsNoOp);
        Assert.Equal(TextSnapshot.Empty, result.Document);            // 未写任何目标
        Assert.Equal(0, result.AppliedEventCount);                    // 未执行任何事件
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
    }
}

/// <summary>记录调用的文本输入服务替身；能力可配置，不引用任何平台类型。</summary>
internal sealed class RecordingTextInputService(TextInputCapabilities capabilities) : ITextInputService
{
    public TextInputCapabilities Capabilities { get; } = capabilities;

    public ITextInputClient? ActiveClient { get; private set; }

    public int StartCount { get; private set; }

    /// <summary>调用 <see cref="EndSession"/> 的总次数（含没有会话时的幂等调用）。</summary>
    public int EndCallCount { get; private set; }

    /// <summary>真正结束了会话的次数。</summary>
    public int EndedSessionCount { get; private set; }

    public int ApplyCount { get; private set; }

    public int CancelCount { get; private set; }

    public int PublishCount { get; private set; }

    public IReadOnlyList<CompositionEvent>? LastBatch { get; private set; }

    public TextInputGeometry LastGeometry { get; private set; }

    public bool StartSession(ITextInputClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        StartCount++;
        ActiveClient = client;
        return true;
    }

    public bool EndSession()
    {
        EndCallCount++;
        if (ActiveClient is null)
        {
            return false;
        }

        ActiveClient = null;
        EndedSessionCount++;
        return true;
    }

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
    {
        ApplyCount++;
        LastBatch = events;
        var phases = TextInputOutcome.None;
        foreach (var e in events)
        {
            phases |= e.Kind switch
            {
                CompositionEventKind.Start => TextInputOutcome.Started,
                CompositionEventKind.Update => TextInputOutcome.Updated,
                CompositionEventKind.Result => TextInputOutcome.ResultCommitted,
                CompositionEventKind.Cancel => TextInputOutcome.Cancelled,
                CompositionEventKind.End => TextInputOutcome.Ended,
                _ => TextInputOutcome.Rejected,
            };
        }

        var client = ActiveClient;
        var document = client?.Document ?? TextSnapshot.Empty;
        return new TextInputApplyResult(phases, document, true);
    }

    public bool RequestCancel()
    {
        CancelCount++;
        return ActiveClient?.CancelComposition() ?? false;
    }

    public bool PublishGeometry(TextInputGeometry geometry)
    {
        PublishCount++;
        LastGeometry = geometry;
        return Capabilities.CandidateAnchor;
    }
}

/// <summary>记录调用的文本输入客户端替身；不接触任何 UI 节点。</summary>
internal sealed class RecordingTextInputClient : ITextInputClient
{
    public TextSnapshot Document { get; set; } = TextSnapshot.Empty;

    public TextInputSession Session { get; set; } = new(0);

    public bool IsComposing { get; set; }

    public string Preedit { get; set; } = string.Empty;

    public int ApplyCount { get; private set; }

    public IReadOnlyList<CompositionEvent>? LastBatch { get; private set; }

    public TextInputState GetState() => new(Document, Preedit, default, IsComposing);

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
    {
        ApplyCount++;
        LastBatch = events;
        var phases = TextInputOutcome.None;
        foreach (var e in events)
        {
            phases |= e.Kind switch
            {
                CompositionEventKind.Start => TextInputOutcome.Started,
                CompositionEventKind.Update => TextInputOutcome.Updated,
                CompositionEventKind.Result => TextInputOutcome.ResultCommitted,
                CompositionEventKind.Cancel => TextInputOutcome.Cancelled,
                CompositionEventKind.End => TextInputOutcome.Ended,
                _ => TextInputOutcome.Rejected,
            };

            if (e.Kind is CompositionEventKind.Update or CompositionEventKind.Start)
            {
                Preedit = e.Preedit;
                IsComposing = true;
            }
        }

        return new TextInputApplyResult(phases, Document, true);
    }

    public bool CancelComposition()
    {
        if (!IsComposing)
        {
            return false;
        }

        IsComposing = false;
        Preedit = string.Empty;
        return true;
    }
}

/// <summary>能力查询 getter 抛错的替身服务（TI-08 §5b）；其余成员返回显式拒绝。</summary>
internal sealed class ThrowingCapabilitiesService : ITextInputService
{
    public TextInputCapabilities Capabilities => throw new InvalidOperationException("能力查询失败（注入）");

    public ITextInputClient? ActiveClient => null;

    public bool StartSession(ITextInputClient client) => false;

    public bool EndSession() => false;

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => TextInputApplyResult.Rejected(TextSnapshot.Empty, "注入服务不执行组合。");

    public bool RequestCancel() => false;

    public bool PublishGeometry(TextInputGeometry geometry) => false;
}
