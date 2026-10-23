using CSharp.UI.Skia;
using CSharp.UI.Testing;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08 集成测试共用宿主：直接实例化真实示例视图（<c>GalleryApp</c> / <c>NestedResponsiveExample</c>），
/// 使用真实 <see cref="SkiaTextService"/> 排版，只替换纹理 / 图片 / 时钟 / 滚轮设置等平台服务。
/// </summary>
/// <remarks>
/// 布局、滚动、焦点与绘制命令全部来自真实实现；不复制简化视图树，也不读取窗口全局尺寸。
/// </remarks>
internal sealed class GalleryIntegrationHost : IDisposable
{
    /// <summary>逻辑几何比较容差。</summary>
    public const double Epsilon = 0.001;

    private readonly StubImageService _images = CreateImages();

    /// <summary>用真实示例视图创建一个无窗口运行时。</summary>
    public GalleryIntegrationHost(View root, Size viewport, double pixelScale)
    {
        TextService = new SkiaTextService();
        Textures = new RecordingTextureService();
        Clipboard = new StubClipboardService();
        Runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = TextService,
            TextureService = Textures,
            ImageService = _images,
            Clipboard = Clipboard,
            Clock = new TestUiClock(),
            WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
        });

        Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        Runtime.SetViewport(viewport, pixelScale);
        Runtime.Start();
        Runtime.ProcessPendingWork();
    }

    /// <summary>被测试运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>真实 SkiaSharp 文本服务（排版 + 栅格化）。</summary>
    public SkiaTextService TextService { get; }

    /// <summary>记录上传命令的纹理服务替身。</summary>
    public RecordingTextureService Textures { get; }

    /// <summary>剪贴板替身。</summary>
    public StubClipboardService Clipboard { get; }

    /// <summary>更新视口尺寸与 DPI 并处理待办工作。</summary>
    public void Resize(Size viewport, double pixelScale)
    {
        Runtime.SetViewport(viewport, pixelScale);
        Runtime.ProcessPendingWork();
    }

    /// <summary>投递一格（或小数格）滚轮并处理待办工作。</summary>
    public void Wheel(Point position, double notches)
    {
        Runtime.DispatchWheel(new WheelEvent(position, 0, notches, KeyModifiers.None, WheelDeltaMode.Steps));
        Runtime.ProcessPendingWork();
    }

    /// <summary>在节点中心按下并抬起鼠标。</summary>
    public void Click(ViewNode node)
    {
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, node.Frame.Center));
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, node.Frame.Center));
        Runtime.ProcessPendingWork();
    }

    /// <summary>在给定逻辑点按下并抬起鼠标。</summary>
    public void Click(Point position)
    {
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, position));
        Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, position));
        Runtime.ProcessPendingWork();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Runtime.Dispose();
        TextService.Dispose();
    }

    private static StubImageService CreateImages()
    {
        var images = new StubImageService();
        images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
        return images;
    }
}

/// <summary>
/// 控件示例（<see cref="GalleryApp"/>）的关键滚动视图定位助手：按示例里注入的 <see cref="ViewExtensions.Key"/> 身份键查找，
/// 示例新增 demo 区块（更多 ScrollView）不影响命中。
/// </summary>
internal static class GalleryScrollLookup
{
    /// <summary>整页外层滚动视图（"gallery-root-scroll"）。</summary>
    public static ViewNode RootScroll(UiRuntime runtime)
        => FindScrollByKey(runtime, "gallery-root-scroll", "整页外层 ScrollView");

    /// <summary>"滚动容器" 节中 120px FrameView 内的内层滚动视图（"gallery-nested-scroll"）。</summary>
    public static ViewNode NestedScroll(UiRuntime runtime)
        => FindScrollByKey(runtime, "gallery-nested-scroll", "滚动容器内层 ScrollView");

    /// <summary>按示例注入的 Key 定位节点；若键落在外层修饰符上，向下取首个 ScrollView。</summary>
    private static ViewNode FindScrollByKey(UiRuntime runtime, string key, string description)
    {
        var keyed = runtime.Root.Descendants()
            .FirstOrDefault(n => n.HasIdentityKey && key.Equals(n.IdentityKey as string, StringComparison.Ordinal));
        if (keyed is null)
        {
            throw new InvalidOperationException($"示例页面找不到身份键 {key} 的 {description}。");
        }
        return keyed.View is ScrollView
            ? keyed
            : keyed.FindDescendants<ScrollView>().FirstOrDefault()
              ?? throw new InvalidOperationException($"身份键 {key} 的 {description} 下没有 ScrollView 节点。");
    }
}

/// <summary>
/// 真实示例的逐节点逻辑布局快照：把节点路径、帧矩形、期望尺寸与自适应模式序列化为可比较文本。
/// 用于把真实宿主窗口的布局与同逻辑尺寸下的无窗口布局做逐节点对照。
/// </summary>
internal static class GalleryLayoutSnapshot
{
    /// <summary>捕获当前树的逻辑布局签名。</summary>
    public static string Capture(UiRuntime runtime)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var node in runtime.Root.Descendants())
        {
            builder.Append(node.DebugPath).Append('|')
                .Append(Format(node.Frame)).Append('|')
                .Append(Format(node.DesiredSize)).Append('|')
                .Append(node.AdaptiveIsHorizontal?.ToString() ?? "-")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Format(Rect rect)
        => $"{rect.X:0.###},{rect.Y:0.###},{rect.Width:0.###},{rect.Height:0.###}";

    private static string Format(Size size)
        => $"{size.Width:0.###}x{size.Height:0.###}";
}
