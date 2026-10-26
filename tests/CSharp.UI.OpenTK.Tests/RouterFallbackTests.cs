using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a 路由器如实报告与失败回退：能力必须来自现存真实后端，失败绝不虚报接受。
/// </summary>
/// <remarks>
/// 全部在真实 <see cref="UiRuntime"/> + 真实 <see cref="TextField"/> 上进行；只把 IMM 子桥换成记录型替身
/// （它转发真实客户端，不复制编辑算法），不接触 HWND/COM，可在任何平台确定性运行。
/// </remarks>
public sealed class RouterFallbackTests
{
    /// <summary>
    /// 记录调用并按真实契约转发客户端的 IMM 替身（<see cref="Accept"/> 为 false 时与真实桥一致地不绑定客户端）。
    /// </summary>
    private sealed class RecordingService : IWindowsInputBridge
    {
        public RecordingService(TextInputCapabilities capabilities, bool accept = true)
        {
            Capabilities = capabilities;
            Accept = accept;
        }

        public TextInputCapabilities Capabilities { get; }

        public bool Accept { get; }

        public ITextInputClient? ActiveClient { get; private set; }

        public int StartCalls { get; private set; }

        public object? CurrentKeyToken => null;

        public bool CurrentKeyConsumedByIme => false;

        public void AttachRuntime(UiRuntime runtime)
        {
        }

        public void NotifyGeometryInvalidated()
        {
        }

        public void UpdatePointerTransform(double scaleX, double scaleY)
        {
        }

        public bool StartSession(ITextInputClient client)
        {
            StartCalls++;
            if (!Accept)
            {
                return false;
            }

            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => TextInputApplyResult.Rejected(TextSnapshot.Empty, "替身不执行组合。");

        public bool RequestCancel() => false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;

        public void Dispose()
        {
        }
    }

    /// <summary>真实运行时 + 真实 TextField（绑定记录真实写入），路由器同时充当文本输入服务与输入桥。</summary>
    private sealed class Harness : IDisposable
    {
        public string Text { get; private set; } = "ABCD";

        public int Writes { get; private set; }

        public UiRuntime Runtime { get; }

        public Harness(WindowsTextInputRouter router)
        {
            Runtime = new UiRuntime(
                new VStack(new TextField(new Binding<string>(() => Text, v =>
                {
                    Writes++;
                    Text = v;
                }))),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = router,
                });

            Runtime.SetViewport(new Size(400, 200), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.MoveFocus();
            Runtime.BuildFrame();
        }

        public void Dispose() => Runtime.Dispose();
    }

    [Fact]
    public void 默认路由完整透传IMM既有能力()
    {
        var imm = new RecordingService(TextInputCapabilities.Full);
        var router = new WindowsTextInputRouter(imm, enableTsf: false);

        var capabilities = router.Capabilities;

        Assert.True(capabilities.CommittedText);
        Assert.True(capabilities.Composition);
        Assert.True(capabilities.CandidateAnchor);
        Assert.Equal(TextInputCapabilities.Full, capabilities);
    }

    [Fact]
    public void 只有已提交文本的IMM不会被路由虚报为组合()
    {
        var imm = new RecordingService(new TextInputCapabilities(true, reason: "本窗口没有可用的 IMM32 输入上下文。"));
        var router = new WindowsTextInputRouter(imm, enableTsf: false);

        Assert.True(router.Capabilities.CommittedText);
        Assert.False(router.Capabilities.Composition);
    }

    [Fact]
    public void 未启用TSF且没有IMM时只有已提交文本()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        Assert.Equal(TextInputCapabilities.CommittedTextOnly, router.Capabilities);
    }

    [Fact]
    public void 惰性TSF尚未建立时能力仍保留组合以维持启动握手()
    {
        // 没有窗口句柄：第一次 StartSession 只能失败，但能力必须仍声明组合，
        // 否则核心 TextInputServiceSession 永远不会调用 StartSession，TSF 永远没有建立机会。
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: true);

        Assert.True(router.Capabilities.Composition);
        Assert.Null(router.TsfBridge);
        Assert.Null(router.ActiveClient);
    }

    [Fact]
    public void 没有窗口句柄且IMM工厂返回null时会话被拒绝且普通输入仍只写一次()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: true, immFactory: () => null);
        using var h = new Harness(router);

        // 能力保留组合 → 核心真的调用 StartSession；路由器必须如实拒绝，绝不虚报接受。
        Assert.Null(router.ActiveClient);
        Assert.Null(router.TsfBridge);
        Assert.False(router.Capabilities.Composition);

        // 已提交字符通道与路由器无关：焦点字段仍收到一次真实写入。
        h.Runtime.DispatchTextInput(new TextInputEvent("x"));
        Assert.Equal(1, h.Writes);
        Assert.Equal("xABCD", h.Text);

        // 路由器既没有 TSF 也没有 IMM：没有可延后的原生事实，普通输入必须直通（不压队列丢弃）。
        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.True(router.DrainSafePhase());
    }

    [Fact]
    public void IMM拒绝建立会话时路由器返回false并清空归属()
    {
        // 真实桥在“没有可用输入上下文”时返回 false：路由器必须原样报告，绝不 ?? true 虚报接受。
        var imm = new RecordingService(TextInputCapabilities.Full, accept: false);
        var router = new WindowsTextInputRouter(imm, enableTsf: false);
        using var h = new Harness(router);

        Assert.Null(router.ActiveClient);
        Assert.Equal(1, imm.StartCalls);

        // 没有归属也不能阻断已提交字符通道。
        h.Runtime.DispatchTextInput(new TextInputEvent("x"));
        Assert.Equal(1, h.Writes);
        Assert.Equal("xABCD", h.Text);
    }

    [Fact]
    public void 释放后能力保守且不再接受会话()
    {
        var imm = new RecordingService(TextInputCapabilities.Full);
        var router = new WindowsTextInputRouter(imm, enableTsf: true);
        var before = new Harness(router);
        var client = router.ActiveClient;
        before.Dispose();
        router.Dispose();

        Assert.Equal(TextInputCapabilities.CommittedTextOnly, router.Capabilities);
        if (client is not null)
        {
            Assert.False(router.StartSession(client));
            Assert.Null(router.ActiveClient);
        }
    }

    [Fact]
    public void IMM子桥能力查询抛错时如实回退_不造Full不穿透_committed仍落地()
    {
        // TI-08 §5b（主审收敛版）：子桥 getter 失败 → 诚实保守快照；
        // enableTsf=false 排除 TSF 惰性握手。可见边界：能力守恒、后端未被构建/切换、
        // 真实 TextField 字符恰一次落地（不 pin Reason 文案，诊断内容交 smoke 原始输出）。
        var imm = new ThrowingCapabilitiesBridge();
        var router = new WindowsTextInputRouter(imm, enableTsf: false);

        var capabilities = router.Capabilities;
        Assert.True(capabilities.CommittedText);
        Assert.False(capabilities.Composition);
        Assert.False(capabilities.CandidateAnchor);

        // 能力查询失败不穿透焦点建立、不写客户端归属（不虚报接受）。
        using var h = new Harness(router);
        Assert.Null(router.TsfBridge);
        Assert.Null(router.ActiveClient);

        // 已提交字符通道不受影响：真实 TextField 恰一次写入。
        h.Runtime.DispatchTextInput(new TextInputEvent("x"));
        Assert.Equal(1, h.Writes);
        Assert.Equal("xABCD", h.Text);
    }
}

/// <summary>Capabilities getter 抛错的 IMM 子桥替身（TI-08 §5b）。</summary>
internal sealed class ThrowingCapabilitiesBridge : IWindowsInputBridge
{
    public TextInputCapabilities Capabilities => throw new InvalidOperationException("注入能力查询失败");

    public ITextInputClient? ActiveClient => null;

    public object? CurrentKeyToken => null;

    public bool CurrentKeyConsumedByIme => false;

    public void AttachRuntime(UiRuntime runtime) { }

    public void NotifyGeometryInvalidated() { }

    public void UpdatePointerTransform(double scaleX, double scaleY) { }

    public bool StartSession(ITextInputClient client) => false;

    public bool EndSession() => false;

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => TextInputApplyResult.Rejected(TextSnapshot.Empty, "注入桥不执行组合。");

    public bool RequestCancel() => false;

    public bool PublishGeometry(TextInputGeometry geometry) => false;

    public void NotifyCommittedBaselineChanged() { }

    public void Dispose() { }
}

