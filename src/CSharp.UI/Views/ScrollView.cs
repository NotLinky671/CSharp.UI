namespace CSharp.UI;

/// <summary>滚动轴向；首版每个 <see cref="ScrollView"/> 只选择一个滚动轴。</summary>
public enum ScrollAxis
{
    /// <summary>垂直滚动。</summary>
    Vertical,

    /// <summary>水平滚动。</summary>
    Horizontal,
}

/// <summary>
/// 单轴滚动容器：在滚动方向测量内容、在交叉方向使用视口约束，并把内容裁剪到视口。
/// </summary>
/// <remarks>
/// <para>
/// 滚动偏移保存在运行时节点上，因此组件重新构建后位置保持不变；内容或视口变化后偏移会被重新限制到有效范围。
/// 首版不显示滚动条、不做列表虚拟化。
/// </para>
/// <para>
/// 滚轮事件默认以归一化步数（一格为 1）表示，本视图按
/// <see cref="WheelScrollSettings"/>（系统每格行数/字符数）换算为逻辑像素距离：每行 16、每字符 8 个逻辑像素，
/// 单个滚轮步的距离按视口大小封顶；单位是逻辑像素，因此 DPI 不影响滚动距离。触摸板等设备的小数步数会被保留。
/// 显式标记为 <see cref="WheelDeltaMode.Pixels"/> 的事件按逻辑像素直接使用，不再换算。
/// </para>
/// </remarks>
public sealed class ScrollView : View
{
    private readonly View[] _children;
    private readonly double _wheelSensitivity = 1.0;

    /// <summary>由内容与滚动轴创建。</summary>
    public ScrollView(View content, ScrollAxis axis = ScrollAxis.Vertical)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        Axis = axis;
        _children = [content];
    }

    /// <summary>被滚动的内容。</summary>
    public View Content { get; }

    /// <summary>滚动轴。</summary>
    public ScrollAxis Axis { get; init; }

    /// <summary>
    /// 滚轮灵敏度：乘在换算后的滚动距离上。
    /// </summary>
    /// <remarks>必须为有限非负值；<see cref="double.NaN"/>、无穷大或负数回退为默认值 1，不会产生反向滚动。</remarks>
    public double WheelSensitivity
    {
        get => _wheelSensitivity;
        init => _wheelSensitivity = NormalizeSensitivity(value);
    }

    /// <inheritdoc />
    public override string DebugName => $"ScrollView({Axis})";

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        var childConstraints = Axis == ScrollAxis.Vertical
            ? new LayoutConstraints(constraints.MinWidth, constraints.MaxWidth, 0, double.PositiveInfinity)
            : new LayoutConstraints(0, double.PositiveInfinity, constraints.MinHeight, constraints.MaxHeight);

        context.Measure(child, childConstraints);
        node.DesiredSize = constraints.Constrain(child.DesiredSize);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        var child = node.ChildList[0];
        var contentSize = child.DesiredSize;

        double maxOffsetX = Axis == ScrollAxis.Horizontal ? Math.Max(0, contentSize.Width - bounds.Width) : 0;
        double maxOffsetY = Axis == ScrollAxis.Vertical ? Math.Max(0, contentSize.Height - bounds.Height) : 0;

        node.ScrollOffsetX = Math.Clamp(node.ScrollOffsetX, 0, maxOffsetX);
        node.ScrollOffsetY = Math.Clamp(node.ScrollOffsetY, 0, maxOffsetY);

        var childWidth = Axis == ScrollAxis.Horizontal ? Math.Max(contentSize.Width, bounds.Width) : bounds.Width;
        var childHeight = Axis == ScrollAxis.Vertical ? Math.Max(contentSize.Height, bounds.Height) : bounds.Height;

        context.Arrange(child, new Rect(
            bounds.X - node.ScrollOffsetX,
            bounds.Y - node.ScrollOffsetY,
            childWidth,
            childHeight));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var restore = context.CurrentClip;
        if (!context.PushClip(node.Frame))
        {
            return;
        }

        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }

        context.PopClip(restore);
    }

    internal override Rect? GetChildClip(ViewNode node) => node.Frame;

    // 滚动容器在滚动轴上吸收父级剩余空间（fill 语义）；否则在不定尺寸的堆叠中会撑到内容高度而永远无法滚动。
    internal override double FlexGrow(ViewNode node, bool horizontal)
        => horizontal == (Axis == ScrollAxis.Horizontal) ? 1 : 0;

    internal override bool OnWheel(InputContext context, ViewNode node, WheelEvent e)
    {
        var horizontal = Axis == ScrollAxis.Horizontal;

        // 单轴容器：垂直轴只消费纵向增量；水平轴优先横向增量，没有横向倾斜时回退到纵向（普通鼠标没有横向滚轮）。
        var axis = horizontal
            ? (Math.Abs(e.DeltaX) > 0 ? e.DeltaX : e.DeltaY)
            : e.DeltaY;

        if (Math.Abs(axis) < 0.0001)
        {
            return false;
        }

        var child = node.ChildList.Count > 0 ? node.ChildList[0] : null;
        var contentSize = child?.DesiredSize ?? Size.Zero;
        var viewportExtent = horizontal ? node.Frame.Width : node.Frame.Height;
        var contentExtent = horizontal ? contentSize.Width : contentSize.Height;
        var maxOffset = Math.Max(0, contentExtent - viewportExtent);

        if (maxOffset <= 0)
        {
            // 内容没有超出视口：不消费事件，交给外层滚动容器。
            return false;
        }

        var distance = ResolveScrollDistance(context, e, horizontal, viewportExtent, axis);
        if (distance == 0 || !double.IsFinite(distance))
        {
            // 系统设置禁用该方向（步距离为 0）：不消费事件。
            return false;
        }

        var offset = horizontal ? node.ScrollOffsetX : node.ScrollOffsetY;
        var next = Math.Clamp(offset - distance, 0, maxOffset);
        if (Math.Abs(next - offset) < 0.0001)
        {
            // 已到达边界：如果还有其它方向可以利用空间则由外层处理。
            return false;
        }

        if (horizontal)
        {
            node.ScrollOffsetX = next;
        }
        else
        {
            node.ScrollOffsetY = next;
        }

        // 滚动只改变偏移，需要重新排列子项与重绘。
        node.Runtime.InvalidateLayout(node);
        context.MarkHandled();
        return true;
    }

    /// <summary>
    /// 把一次滚轮事件的增量换算为带符号的逻辑像素距离（正值表示内容向上/向左滚动）。
    /// </summary>
    /// <remarks>
    /// 步数单位：单个滚轮步的距离由系统设置换算并按视口封顶，再乘以实际（可含小数的）步数；
    /// 像素单位：增量已是逻辑像素，直接使用。两者最后都乘以 <see cref="WheelSensitivity"/>，保留小数不取整。
    /// </remarks>
    private double ResolveScrollDistance(
        InputContext context,
        WheelEvent e,
        bool horizontal,
        double viewportExtent,
        double axis)
    {
        if (e.Mode == WheelDeltaMode.Pixels)
        {
            return axis * _wheelSensitivity;
        }

        var stepDistance = context.Runtime.Services.WheelScrollSettings.Settings.StepDistance(horizontal, viewportExtent);
        return stepDistance * axis * _wheelSensitivity;
    }

    private static double NormalizeSensitivity(double value)
        => double.IsFinite(value) && value >= 0 ? value : 1.0;

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is ScrollView scroll && scroll.Axis == Axis;
}

/// <summary>图片缩放方式。</summary>
public enum ImageResizeMode
{
    /// <summary>保持比例缩放到完全可见（不放大超过自然尺寸）。</summary>
    Fit,

    /// <summary>保持比例填满并裁剪（不放大超过自然尺寸）。</summary>
    Fill,

    /// <summary>拉伸到目标尺寸，不保持比例。</summary>
    Stretch,

    /// <summary>按自然尺寸居中，不做缩放。</summary>
    Center,
}

/// <summary>
/// 图片视图：显示本地文件或应用资源中的图片；解码由 <see cref="IImageService"/> 完成。
/// </summary>
/// <remarks>首版不下载网络图片；同一来源复用解码结果与纹理。</remarks>
public sealed class Image : View
{
    /// <summary>由来源创建（文件路径或应用资源标识）。</summary>
    public Image(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
    }

    /// <summary>图片来源。</summary>
    public string Source { get; init; }

    /// <summary>缩放方式。</summary>
    public ImageResizeMode ResizeMode { get; init; } = ImageResizeMode.Fit;

    /// <inheritdoc />
    public override string DebugName => $"Image({Path.GetFileName(Source)})";

    /// <summary>返回指定缩放方式的副本。</summary>
    public Image Resize(ImageResizeMode mode) => new(Source) { ResizeMode = mode };

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var natural = GetNaturalSize(node, context);
        var desired = ResolveDesiredSize(natural, constraints);
        node.ViewCache = natural;
        node.DesiredSize = desired;
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node) || node.Frame.IsEmpty)
        {
            return;
        }

        var textureService = context.TextureService;
        var imageService = context.ImageService;
        if (textureService is null || imageService is null)
        {
            return;
        }

        if (!imageService.TryLoad(Source, out var resource) || resource is null)
        {
            return;
        }

        // 自然尺寸优先取测量阶段的结果；若图片是在测量之后才可用（例如延迟注册），则退回资源自身的尺寸。
        var natural = node.ViewCache is Size size && !size.IsEmpty ? size : resource.NaturalSize;
        var destination = ComputeDestination(node.Frame, natural);
        if (destination.IsEmpty)
        {
            return;
        }

        var handle = textureService.GetOrCreate(resource, () => resource.Pixels);
        if (!handle.IsValid)
        {
            return;
        }

        context.Commands.Add(new TextureCommand(
            handle,
            destination,
            new Rect(0, 0, handle.PixelWidth, handle.PixelHeight),
            Color.White,
            node.Environment.Opacity));
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Image image && image.Source == Source && image.ResizeMode == ResizeMode;

    private Size GetNaturalSize(ViewNode node, LayoutContext context)
    {
        if (context.ImageService is null)
        {
            context.Runtime.ReportDiagnostic($"Image(\"{Source}\") 需要图片服务；当前运行时未提供 IImageService。");
            return Size.Zero;
        }

        if (context.ImageService.TryLoad(Source, out var resource) && resource is not null)
        {
            return resource.NaturalSize;
        }

        context.Runtime.ReportDiagnostic($"Image(\"{Source}\") 加载失败：文件不存在或格式无效。");
        return Size.Zero;
    }

    private Size ResolveDesiredSize(Size natural, LayoutConstraints constraints)
    {
        if (natural.IsEmpty)
        {
            // 无有效图片时退化为零尺寸，但仍受显式 Frame 约束。
            return constraints.Constrain(Size.Zero);
        }

        if (constraints.IsWidthTight && constraints.IsHeightTight)
        {
            return new Size(constraints.MaxWidth, constraints.MaxHeight);
        }

        var scale = 1.0;
        if (ResizeMode is ImageResizeMode.Fit or ImageResizeMode.Fill && constraints.HasBoundedWidth && constraints.HasBoundedHeight)
        {
            var scaleX = constraints.MaxWidth / natural.Width;
            var scaleY = constraints.MaxHeight / natural.Height;
            scale = ResizeMode == ImageResizeMode.Fit ? Math.Min(scaleX, scaleY) : Math.Max(scaleX, scaleY);
        }
        else if (ResizeMode == ImageResizeMode.Stretch && constraints.HasBoundedWidth && constraints.HasBoundedHeight)
        {
            return constraints.Constrain(new Size(constraints.MaxWidth, constraints.MaxHeight));
        }

        var width = natural.Width * scale;
        var height = natural.Height * scale;
        if (constraints.HasBoundedWidth)
        {
            width = Math.Min(width, constraints.MaxWidth);
        }

        if (constraints.HasBoundedHeight)
        {
            height = Math.Min(height, constraints.MaxHeight);
        }

        return constraints.Constrain(new Size(width, height));
    }

    private Rect ComputeDestination(Rect frame, Size natural)
    {
        if (natural.IsEmpty)
        {
            return Rect.Empty;
        }

        return ResizeMode switch
        {
            ImageResizeMode.Stretch => frame,
            ImageResizeMode.Fill => Cover(frame, natural),
            ImageResizeMode.Center => new Rect(
                frame.X + ((frame.Width - natural.Width) / 2),
                frame.Y + ((frame.Height - natural.Height) / 2),
                Math.Min(natural.Width, frame.Width),
                Math.Min(natural.Height, frame.Height)),
            _ => Contain(frame, natural),
        };
    }

    private static Rect Contain(Rect frame, Size natural)
    {
        var scale = Math.Min(frame.Width / natural.Width, frame.Height / natural.Height);
        var width = natural.Width * scale;
        var height = natural.Height * scale;
        return new Rect(
            Math.Round(frame.X + ((frame.Width - width) / 2), 3),
            Math.Round(frame.Y + ((frame.Height - height) / 2), 3),
            Math.Round(width, 3),
            Math.Round(height, 3));
    }

    private static Rect Cover(Rect frame, Size natural)
    {
        var scale = Math.Max(frame.Width / natural.Width, frame.Height / natural.Height);
        var width = natural.Width * scale;
        var height = natural.Height * scale;
        return new Rect(
            Math.Round(frame.X + ((frame.Width - width) / 2), 3),
            Math.Round(frame.Y + ((frame.Height - height) / 2), 3),
            Math.Round(width, 3),
            Math.Round(height, 3));
    }
}
