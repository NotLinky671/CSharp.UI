using CSharp.UI;
using CSharp.UI.Testing;

namespace CSharp.UI.Tests;

/// <summary>
/// 无窗口测试宿主：把运行时与全部测试替身（调度器、文本、纹理、图片、剪贴板、时钟）组装在一起。
/// </summary>
internal sealed class TestApp : IDisposable
{
    public TestApp(
        View root,
        Size? viewport = null,
        double pixelScale = 1.0,
        Theme? theme = null,
        Action<StubImageService>? configureImages = null,
        WheelScrollSettings? wheelScrollSettings = null)
    {
        Dispatcher = new HeadlessUiDispatcher();
        Text = new StubTextService();
        Textures = new RecordingTextureService();
        Images = new StubImageService();
        Clipboard = new StubClipboardService();
        Clock = new TestUiClock();
        WheelScroll = new StubWheelScrollSettingsService(wheelScrollSettings ?? WheelScrollSettings.Default);
        configureImages?.Invoke(Images);
        Runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = Dispatcher,
            TextService = Text,
            TextureService = Textures,
            ImageService = Images,
            Clipboard = Clipboard,
            Clock = Clock,
            WheelScrollSettings = WheelScroll,
        });

        Runtime.SetEnvironment(new EnvironmentValues { Theme = theme ?? Theme.Light });
        Runtime.SetViewport(viewport ?? new Size(400, 300), pixelScale);
        Runtime.Start();
        Runtime.ProcessPendingWork();
    }

    public HeadlessUiDispatcher Dispatcher { get; }

    public StubTextService Text { get; }

    public RecordingTextureService Textures { get; }

    public StubImageService Images { get; }

    public StubClipboardService Clipboard { get; }

    public TestUiClock Clock { get; }

    /// <summary>滚轮设置服务替身；测试可替换设置值。</summary>
    public StubWheelScrollSettingsService WheelScroll { get; }

    public UiRuntime Runtime { get; }

    /// <summary>生成一帧并返回绘制命令。</summary>
    public IReadOnlyList<DrawCommand> Frame() => Runtime.BuildFrame().Commands;

    /// <summary>分发一次左键点击（按下 + 释放到同一位置）。</summary>
    public void Click(Point position)
    {
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, position));
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, position, PointerButton.Left));
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, position, PointerButton.Left));
    }

    /// <summary>
    /// 分发一次滚轮。默认单位是归一化步数；像素式的辅助调用必须显式传 <see cref="WheelDeltaMode.Pixels"/>，
    /// 不能依赖增量大小猜测单位。
    /// </summary>
    public void Wheel(Point position, double deltaY, WheelDeltaMode mode = WheelDeltaMode.Steps)
        => Runtime.DispatchWheel(new WheelEvent(position, 0, deltaY, KeyModifiers.None, mode));

    /// <summary>分发一次横向滚轮；单位语义同 <see cref="Wheel"/>。</summary>
    public void WheelX(Point position, double deltaX, WheelDeltaMode mode = WheelDeltaMode.Steps)
        => Runtime.DispatchWheel(new WheelEvent(position, deltaX, 0, KeyModifiers.None, mode));

    /// <summary>分发按键按下 + 释放。</summary>
    public void Key(UiKey key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, key, modifiers));
        Runtime.DispatchKey(new KeyEvent(KeyEventKind.Up, key, modifiers));
    }

    public void Dispose() => Runtime.Dispose();
}

/// <summary>测试用简单组件：便于验证组件重建与状态。</summary>
internal sealed class CounterComponent : Component
{
    public static int BuildCalls;

    protected override View Build()
    {
        BuildCalls++;
        var count = UseState("count", 0);
        return new VStack(
            new Text($"count={count.Value}"),
            new Button("add", () => count.Value++));
    }
}

/// <summary>读取组件状态值的测试辅助。</summary>
internal static class NodeExtensions
{
    public static ViewNode Require<TView>(this UiRuntime runtime)
        where TView : View
        => runtime.Root.FindDescendant<TView>() ?? throw new InvalidOperationException($"未找到 {typeof(TView).Name} 节点。");

    public static ViewNode Require<TView>(this ViewNode node)
        where TView : View
        => node.FindDescendant<TView>() ?? throw new InvalidOperationException($"未找到 {typeof(TView).Name} 节点。");

    public static IEnumerable<ViewNode> All<TView>(this UiRuntime runtime)
        where TView : View
        => runtime.Root.FindDescendants<TView>();
}
