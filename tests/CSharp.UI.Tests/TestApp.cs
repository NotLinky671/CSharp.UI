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
        Action<StubImageService>? configureImages = null)
    {
        Dispatcher = new HeadlessUiDispatcher();
        Text = new StubTextService();
        Textures = new RecordingTextureService();
        Images = new StubImageService();
        Clipboard = new StubClipboardService();
        Clock = new TestUiClock();
        configureImages?.Invoke(Images);
        Runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = Dispatcher,
            TextService = Text,
            TextureService = Textures,
            ImageService = Images,
            Clipboard = Clipboard,
            Clock = Clock,
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

    /// <summary>分发一次滚轮。</summary>
    public void Wheel(Point position, double deltaY) => Runtime.DispatchWheel(new WheelEvent(position, 0, deltaY));

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
