using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using GlfwKeyModifiers = OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P1 宿主输入桥契约：输入适配器只依赖 <see cref="IWindowsInputBridge"/>，
/// 按键身份 token 与 IME 消费标记按契约传递（用假桥确定性地声明归属），
/// 并且现有具体实现 <see cref="WindowsImeBridge"/> 经契约观察到的值与内部成员一致。
/// </summary>
public sealed class WindowsInputBridgeContractTests
{
    // ------------------------------------------------------------ 假桥

    /// <summary>
    /// 只实现契约的假桥：不接触 HWND/IMM32，按键归属由测试显式声明。
    /// </summary>
    private sealed class FakeWindowsInputBridge : IWindowsInputBridge
    {
        public TextInputCapabilities Capabilities { get; set; } = TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; set; }

        public object? CurrentKeyToken { get; private set; }

        public bool CurrentKeyConsumedByIme { get; private set; }

        public UiRuntime? AttachedRuntime { get; private set; }

        public int AttachRuntimeCalls { get; private set; }

        public int GeometryInvalidatedCalls { get; private set; }

        public List<(double X, double Y)> PointerTransforms { get; } = [];

        public int DisposeCount { get; private set; }

        /// <summary>
        /// 只设置契约读到的按键状态，<b>不</b>向运行时声明 token 归属。
        /// </summary>
        /// <remarks>
        /// 用于把“适配器读取消费标记”的分支与“运行时按 token 判定归属”的分支隔离开：
        /// 这样断言失败只可能来自适配器自己忽略了标记，而不是运行时的控件消费恰好生效。
        /// </remarks>
        public void SetKeyState(object? token, bool consumedByIme)
        {
            CurrentKeyToken = token;
            CurrentKeyConsumedByIme = consumedByIme;
        }

        /// <summary>与生产桥一致的按键入口：登记身份/消费标记并声明给运行时。</summary>
        public void BeginKey(object? token, bool consumedByIme)
        {
            SetKeyState(token, consumedByIme);
            if (token is not null)
            {
                AttachedRuntime?.NotifyCompositionKeyOrigin(token, consumedByIme);
            }
        }

        public void EndKey()
        {
            CurrentKeyToken = null;
            CurrentKeyConsumedByIme = false;
        }

        public void AttachRuntime(UiRuntime runtime)
        {
            AttachRuntimeCalls++;
            AttachedRuntime = runtime;
        }

        public void NotifyGeometryInvalidated() => GeometryInvalidatedCalls++;

        public void UpdatePointerTransform(double scaleX, double scaleY)
            => PointerTransforms.Add((scaleX, scaleY));

        public bool StartSession(ITextInputClient client)
        {
            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            if (ActiveClient is null)
            {
                return false;
            }

            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient is { } client
                ? client.ApplyComposition(events)
                : TextInputApplyResult.Rejected(TextSnapshot.Empty, "假桥没有活动客户端。");

        public bool RequestCancel() => false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;

        public void Dispose() => DisposeCount++;
    }

    private sealed class TwoFields : IDisposable
    {
        public string First = string.Empty;
        public string Second = string.Empty;
        public FakeWindowsInputBridge Bridge { get; } = new();
        public UiRuntime Runtime { get; }

        public TwoFields()
        {
            Runtime = new UiRuntime(
                new VStack(
                    new TextField(new Binding<string>(() => First, v => First = v)),
                    new TextField(new Binding<string>(() => Second, v => Second = v))),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = Bridge,
                });
            Bridge.AttachRuntime(Runtime);
            Runtime.SetViewport(new Size(400, 120), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.SetFocus(Runtime.Root.Children[0]);
            Runtime.BuildFrame();
        }

        public ViewNode FirstNode => Runtime.Root.Children[0];

        public ViewNode SecondNode => Runtime.Root.Children[1];

        public void Dispose() => Runtime.Dispose();
    }

    private static KeyboardKeyEventArgs Key(Keys key, GlfwKeyModifiers modifiers = GlfwNoModifiers.Value)
        => new(key, 0, modifiers, false);

    // ------------------------------------------------------------ 契约驱动的按键归属

    // R2：消费标记必须用<b>本身会触发退出</b>的按键验证（Escape / 命令 Q）。
    // 若用 Keys.Unknown 断言“不退出”，适配器完全忽略消费标记也会通过——那不能证明接口替换保留了抑制。
    // 每条消费用例都配一条同键无消费对照，并且用 SetKeyState 隔离适配器的标记分支：
    // 不向运行时声明 token 归属，焦点控件也不会消费这些按键，因此唯一能阻止退出的就是适配器读到的标记。

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 假桥消费标记决定Escape是否触发宿主退出(bool consumedByIme)
    {
        var exits = 0;
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        // 无活动文本客户端、不声明运行时 token 归属：控件不会消费 Escape，只有适配器的标记分支能阻止退出。
        host.Bridge.EndSession();
        Assert.Null(host.Bridge.ActiveClient);

        host.Bridge.SetKeyState(new object(), consumedByIme);
        adapter.HandleKeyDown(Key(Keys.Escape));
        host.Bridge.EndKey();

        Assert.Equal(1, adapter.KeyEventCount);
        Assert.Equal(consumedByIme ? 0 : 1, exits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 假桥消费标记决定命令Q是否触发宿主退出(bool consumedByIme)
    {
        var exits = 0;
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        host.Bridge.EndSession();

        host.Bridge.SetKeyState(new object(), consumedByIme);
        adapter.HandleKeyDown(Key(Keys.Q, GlfwKeyModifiers.Control));
        host.Bridge.EndKey();

        Assert.Equal(1, adapter.KeyEventCount);
        Assert.Equal(consumedByIme ? 0 : 1, exits);
    }

    [Fact]
    public void 假桥未声明消费时Esc保持既有退出行为()
    {
        var exits = 0;
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        host.Bridge.BeginKey(new object(), consumedByIme: false);
        adapter.HandleKeyDown(Key(Keys.Escape));
        host.Bridge.EndKey();

        Assert.Equal(1, exits);
    }

    [Fact]
    public void 假桥token按次生效不因上一次消费而误吞后续按键()
    {
        var exits = 0;
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);
        host.Bridge.EndSession();

        // 第一次按键：输入法消费 → 不退出。
        host.Bridge.SetKeyState(new object(), consumedByIme: true);
        adapter.HandleKeyDown(Key(Keys.Escape));
        host.Bridge.EndKey();

        // 第二次按键：新的 token、未消费 → 按既有行为退出（标记不是永久生效）。
        host.Bridge.SetKeyState(new object(), consumedByIme: false);
        adapter.HandleKeyDown(Key(Keys.Escape));
        host.Bridge.EndKey();

        Assert.Equal(1, exits);
    }

    [Fact]
    public void 假桥声明的token让同一按键被控件消费且下一次按键恢复焦点遍历()
    {
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => { }, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        // 组合在同一原生按键内结束：桥声明该 token 已由输入法消费 → Tab 不再移动焦点。
        var consumedToken = new object();
        host.Bridge.BeginKey(consumedToken, consumedByIme: true);
        adapter.HandleKeyDown(Key(Keys.Tab));
        host.Bridge.EndKey();
        Assert.Same(host.FirstNode, host.Runtime.FocusedNode);

        // 下一次按键是新的身份：未被声明 → 焦点遍历按既有行为继续。
        host.Bridge.BeginKey(new object(), consumedByIme: false);
        adapter.HandleKeyDown(Key(Keys.Tab));
        host.Bridge.EndKey();
        Assert.Same(host.SecondNode, host.Runtime.FocusedNode);
    }

    [Fact]
    public void 无桥时适配器保持完全既有的按键路径()
    {
        var exits = 0;
        using var host = new TwoFields();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true);

        adapter.HandleKeyDown(Key(Keys.Escape));

        Assert.Equal(1, exits);
        Assert.Equal(1, adapter.KeyEventCount);
        Assert.NotNull(adapter.LastKeyEvent);
    }

    // ------------------------------------------------------------ 契约成员接线

    [Fact]
    public void 契约的宿主操作可直接接线到假桥()
    {
        using var host = new TwoFields();
        IWindowsInputBridge bridge = host.Bridge;

        bridge.AttachRuntime(host.Runtime);
        bridge.UpdatePointerTransform(2.0 / 3.0, 2.0 / 3.0);
        bridge.NotifyGeometryInvalidated();
        bridge.Dispose();

        Assert.Equal(2, host.Bridge.AttachRuntimeCalls);
        Assert.Equal([(2.0 / 3.0, 2.0 / 3.0)], host.Bridge.PointerTransforms);
        Assert.Equal(1, host.Bridge.GeometryInvalidatedCalls);
        Assert.Equal(1, host.Bridge.DisposeCount);
    }

    [Fact]
    public void 现有WindowsImeBridge经契约暴露与内部成员相同的按键状态()
    {
        // 兼容性：具体实现不改内部签名也能满足契约，且契约读到的就是同一份状态。
        using var host = new BridgeHost();
        IWindowsInputBridge contract = host.Bridge;

        Assert.Null(contract.CurrentKeyToken);
        Assert.False(contract.CurrentKeyConsumedByIme);

        host.Bridge.BeginKeyDispatch(new IntPtr(Win32Ime.VK_PROCESSKEY));
        Assert.Same(host.Bridge.CurrentKeyToken, contract.CurrentKeyToken);
        Assert.NotNull(contract.CurrentKeyToken);
        Assert.True(contract.CurrentKeyConsumedByIme);
        host.Bridge.EndKeyDispatch();

        Assert.Null(contract.CurrentKeyToken);
        Assert.False(contract.CurrentKeyConsumedByIme);

        host.Bridge.BeginKeyDispatch(new IntPtr(0x1B));
        Assert.False(contract.CurrentKeyConsumedByIme);
        host.Bridge.EndKeyDispatch();
    }

    [Fact]
    public void 现有WindowsImeBridge经契约的几何失效与换算走同一实现()
    {
        using var host = new BridgeHost();
        IWindowsInputBridge contract = host.Bridge;

        contract.UpdatePointerTransform(1, 1);
        Assert.True(contract.PublishGeometry(new TextInputGeometry(new Rect(10, 10, 2, 20))));
        var publishesBefore = host.Bridge.GeometryPublishCount;

        // 换算变化按契约入口生效：缓存逻辑几何被重新发布（与内部成员同一路径）。
        // 系数 2 = 窗口坐标 × 2 = 逻辑坐标，因此客户区 = 逻辑 ÷ 2：caret(10,10,2,20) → (5, 15)。
        contract.UpdatePointerTransform(2, 2);
        Assert.True(host.Bridge.GeometryPublishCount > publishesBefore);
        Assert.Equal((5, 15), host.Ime.CandidatePositions[^1]);

        // 几何失效通知同样经契约到达平台（清缓存后强制重发同值）。
        var publishesAfterTransform = host.Bridge.GeometryPublishCount;
        contract.NotifyGeometryInvalidated();
        Assert.True(host.Bridge.GeometryPublishCount > publishesAfterTransform);
    }

    [Fact]
    public void 现有WindowsImeBridge可赋给适配器使用的契约类型()
    {
        using var host = new BridgeHost();
        var adapter = new OpenTkInputAdapter(
            host.Runtime, () => { }, exitOnEscape: true, exitOnCloseShortcut: true, (IWindowsInputBridge)host.Bridge);

        Assert.NotNull(adapter);
        Assert.True(((IWindowsInputBridge)host.Bridge).Capabilities.Composition);
    }

    /// <summary>与 <see cref="WindowsImeBridgeTests"/> 同构的最小宿主：真实运行时 + 假 IMM 互操作。</summary>
    private sealed class BridgeHost : IDisposable
    {
        public FakeIme Ime { get; } = new();
        public WindowsImeBridge Bridge { get; }
        public UiRuntime Runtime { get; }

        public BridgeHost()
        {
            Bridge = new WindowsImeBridge(
                IntPtr.Zero, Ime, new OpenTkUiDispatcher(() => { }), () => (800, 600));
            Bridge.SimulateInstalled();
            Runtime = new UiRuntime(
                new TextField(new Binding<string>(() => string.Empty, _ => { })),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = Bridge,
                });
            Bridge.AttachRuntime(Runtime);
            Runtime.SetViewport(new Size(400, 100), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.SetFocus(Runtime.Root);
            Runtime.BuildFrame();
        }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>最小 IMM 互操作替身（只记录候选窗发布位置）。</summary>
    private sealed class FakeIme : IImeInterop
    {
        private static int _nextHandle = 0x2000;
        private readonly IntPtr _context = new(Interlocked.Increment(ref _nextHandle));

        public List<(int X, int Y)> CandidatePositions { get; } = [];

        public IntPtr GetContext() => _context;

        public void ReleaseContext(IntPtr himc)
        {
        }

        public IntPtr EnsureContext() => _context;

        public ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags) => new(flags, null, null, -1, []);

        public bool SetCandidatePosition(IntPtr himc, int x, int y)
        {
            CandidatePositions.Add((x, y));
            return true;
        }

        public bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom) => true;

        public bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom) => true;

        public bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style)
        {
            x = y = 0;
            style = 0;
            return false;
        }

        public bool NotifyCancelComposition(IntPtr himc) => true;

        public bool GetNativeInputState(IntPtr himc, out bool open, out uint conversion, out uint sentence)
        {
            open = true;
            conversion = Win32Ime.IME_CMODE_NATIVE;
            sentence = 0;
            return himc != IntPtr.Zero;
        }

        public bool SetNativeInputState(IntPtr himc, bool open, uint conversion, uint sentence)
            => himc != IntPtr.Zero;
    }
}
