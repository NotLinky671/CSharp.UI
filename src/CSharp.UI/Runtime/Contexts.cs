namespace CSharp.UI;

/// <summary>
/// 测量/排列阶段上下文：提供服务访问、失效标记与子节点递归入口。
/// </summary>
internal sealed class LayoutContext
{
    public LayoutContext(UiRuntime runtime, ITextService textService, IImageService? imageService, double pixelScale)
    {
        Runtime = runtime;
        TextService = textService;
        ImageService = imageService;
        PixelScale = pixelScale;
        MeasureCount = 0;
        ArrangeCount = 0;
    }

    /// <summary>所属运行时，用于在布局阶段申请失效（例如图片尺寸变化）。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>文本服务。</summary>
    public ITextService TextService { get; }

    /// <summary>图片服务；无图片能力时为 null。</summary>
    public IImageService? ImageService { get; }

    /// <summary>DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>本次布局中实际执行的 Measure 次数（用于验证增量布局）。</summary>
    public int MeasureCount { get; internal set; }

    /// <summary>本次布局中实际执行的 Arrange 次数。</summary>
    public int ArrangeCount { get; internal set; }

    /// <summary>测量子节点（遵循测量缓存）。</summary>
    public void Measure(ViewNode node, LayoutConstraints constraints)
    {
        var parentEnvironment = node.Parent?.Environment ?? Runtime.Environment;
        var environment = node.View.ResolveEnvironment(node, parentEnvironment);
        if (!Equals(environment, node.Environment))
        {
            node.Environment = environment;
        }

        if (node.MeasureValid && node.LastConstraints == constraints)
        {
            return;
        }

        MeasureCount++;
        node.View.Measure(node, constraints, this);

        // 只按上限收缩：最小值由各视图自己决定（例如 Frame 的 MinWidth / Stack 填满父级），
        // 否则父级的紧约束会掩盖子项的自然尺寸。
        node.DesiredSize = new Size(
            Math.Clamp(node.DesiredSize.Width, 0, constraints.MaxWidth),
            Math.Clamp(node.DesiredSize.Height, 0, constraints.MaxHeight));
        node.LastConstraints = constraints;
        node.MeasureValid = true;
    }

    /// <summary>测量子节点，但忽略缓存（用于需要重新测量的场景）。</summary>
    public void MeasureFresh(ViewNode node, LayoutConstraints constraints)
    {
        node.MeasureValid = false;
        Measure(node, constraints);
    }

    /// <summary>排列子节点到指定绝对矩形。</summary>
    public void Arrange(ViewNode node, Rect bounds)
    {
        ArrangeCount++;
        node.ArrangeValid = true;
        node.View.Arrange(node, bounds, this);
    }
}

/// <summary>
/// 绘制命令生成上下文：维护当前裁剪区、透明度与应用服务，并按顺序输出命令。
/// </summary>
internal sealed class DrawContext
{
    private readonly DrawCommandList _commands = new();

    public DrawContext(UiRuntime runtime, ITextService textService, ITextureService? textureService, IImageService? imageService, double pixelScale, RenderFrameOptions options)
    {
        Runtime = runtime;
        TextService = textService;
        TextureService = textureService;
        ImageService = imageService;
        PixelScale = pixelScale;
        Options = options;
        CurrentClip = new Rect(0, 0, options.ViewportSize.Width, options.ViewportSize.Height);
    }

    /// <summary>所属运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>文本服务。</summary>
    public ITextService TextService { get; }

    /// <summary>纹理服务；无 GPU 能力时（无窗口测试）为 null，文本与图片会被跳过。</summary>
    public ITextureService? TextureService { get; }

    /// <summary>图片服务。</summary>
    public IImageService? ImageService { get; }

    /// <summary>DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>本帧配置。</summary>
    public RenderFrameOptions Options { get; }

    /// <summary>当前有效裁剪区（逻辑像素，已与祖先裁剪区相交）。</summary>
    public Rect CurrentClip { get; private set; }

    /// <summary>已输出的命令。</summary>
    public DrawCommandList Commands => _commands;

    /// <summary>被裁剪剔除的节点数量（诊断用）。</summary>
    public int CulledNodeCount { get; private set; }

    /// <summary>节点矩形是否与当前裁剪区相交。</summary>
    public bool IsVisible(ViewNode node)
    {
        var bounds = node.View.GetClipBounds(node);
        var visible = bounds.IntersectsWith(CurrentClip);
        if (!visible)
        {
            CulledNodeCount++;
        }

        return visible;
    }

    /// <summary>递归输出子节点命令。</summary>
    public void EmitChild(ViewNode child) => child.View.Emit(child, this);

    /// <summary>按绝对矩形压入裁剪区；返回 false 表示裁剪区为空，调用方应跳过内容。</summary>
    public bool PushClip(Rect bounds)
    {
        var intersection = bounds.Intersect(CurrentClip);
        if (intersection.IsEmpty)
        {
            return false;
        }

        _commands.Add(new PushClipCommand(intersection));
        CurrentClip = intersection;
        return true;
    }

    /// <summary>弹出最近压入的裁剪区。</summary>
    public void PopClip(Rect restoreTo)
    {
        _commands.Add(new PopClipCommand());
        CurrentClip = restoreTo;
    }

    /// <summary>取回本帧命令快照。</summary>
    public IReadOnlyList<DrawCommand> ToArray() => _commands.ToArray();
}

/// <summary>一帧的生成配置。</summary>
/// <param name="ViewportSize">逻辑像素视口尺寸。</param>
/// <param name="PixelScale">DPI 缩放。</param>
/// <param name="ClearColor">清屏色。</param>
public readonly record struct RenderFrameOptions(Size ViewportSize, double PixelScale, Color ClearColor);

/// <summary>输入分发上下文：向视图暴露运行时能力并记录事件是否被消费。</summary>
internal sealed class InputContext
{
    public InputContext(UiRuntime runtime)
    {
        Runtime = runtime;
    }

    /// <summary>所属运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>本次事件是否已被处理。</summary>
    public bool Handled { get; private set; }

    /// <summary>标记事件已处理。</summary>
    public void MarkHandled() => Handled = true;

    /// <summary>请求重绘。</summary>
    public void RequestRedraw() => Runtime.InvalidateRender();
}
