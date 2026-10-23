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
/// 内容溢出时沿滚动轴在 persistent 12 逻辑像素轨道槽内绘制滚动条（滑块可拖拽、轨道点击按视口翻页一次、无自动重复）；
/// 无溢出时轨道槽仍占宽留空，避免显隐切换引起布局回流。不做列表虚拟化。
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
        var gutter = ScrollbarMetrics.Thickness;
        var childConstraints = Axis == ScrollAxis.Vertical
            ? new LayoutConstraints(
                Math.Max(0, constraints.MinWidth - gutter),
                Math.Max(0, constraints.MaxWidth - gutter),
                0, double.PositiveInfinity)
            : new LayoutConstraints(
                0, double.PositiveInfinity,
                Math.Max(0, constraints.MinHeight - gutter),
                Math.Max(0, constraints.MaxHeight - gutter));

        context.Measure(child, childConstraints);

        // 期望尺寸 = 内容 + gutter（cross 方向）；父约束最后收敛。
        var desired = Axis == ScrollAxis.Vertical
            ? new Size(child.DesiredSize.Width + gutter, child.DesiredSize.Height)
            : new Size(child.DesiredSize.Width, child.DesiredSize.Height + gutter);
        node.DesiredSize = constraints.Constrain(desired);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        var child = node.ChildList[0];
        var contentSize = child.DesiredSize;

        // 几何变化取消 bar-gesture（自身 offset 写回除外——Arrange 在 drag 写 offset 后也会跑）。
        var state = ScrollbarStateOf(node);
        if (state is not null)
        {
            var contentR = ContentRect(bounds);
            var track = TrackRect(bounds);
            var contentExtent = Axis == ScrollAxis.Horizontal ? contentSize.Width : contentSize.Height;
            var viewportExtent = Axis == ScrollAxis.Horizontal ? contentR.Width : contentR.Height;
            state.SyncGeometry(Axis, track, contentExtent, viewportExtent);
        }

        double maxOffsetX = Axis == ScrollAxis.Horizontal ? Math.Max(0, contentSize.Width - bounds.Width) : 0;
        double maxOffsetY = Axis == ScrollAxis.Vertical ? Math.Max(0, contentSize.Height - bounds.Height) : 0;

        node.ScrollOffsetX = Math.Clamp(node.ScrollOffsetX, 0, maxOffsetX);
        node.ScrollOffsetY = Math.Clamp(node.ScrollOffsetY, 0, maxOffsetY);

        var contentRect = ContentRect(bounds);
        var childWidth = Axis == ScrollAxis.Horizontal ? Math.Max(contentSize.Width, contentRect.Width) : contentRect.Width;
        var childHeight = Axis == ScrollAxis.Vertical ? Math.Max(contentSize.Height, contentRect.Height) : contentRect.Height;

        context.Arrange(child, new Rect(
            contentRect.X - node.ScrollOffsetX,
            contentRect.Y - node.ScrollOffsetY,
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

        // 记录 frame clip（含祖先 intersection）供内容区 pop 后恢复；
        // 不能直接用 node.Frame（祖先 clip 收窄后 node.Frame 反而扩大）。
        var frameClip = context.CurrentClip;
        var contentRect = ContentRect(node.Frame);
        if (context.PushClip(contentRect))
        {
            foreach (var child in node.ChildList)
            {
                context.EmitChild(child);
            }

            context.PopClip(frameClip);
        }

        EmitScrollbar(node, context);

        context.PopClip(restore);
    }

    internal override Rect? GetChildClip(ViewNode node) => ContentRect(node.Frame);

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

    // ------------------------------------------------------------------ 滚动条（persistent gutter，owner 自绘）

    /// <summary>内容区（frame 扣掉 gutter；纵滚右侧、横滚底部）。</summary>
    private Rect ContentRect(Rect frame)
    {
        var gutter = Math.Min(ScrollbarMetrics.Thickness, Math.Max(0, Axis == ScrollAxis.Vertical ? frame.Width : frame.Height));
        return Axis == ScrollAxis.Vertical
            ? new Rect(frame.X, frame.Y, Math.Max(0, frame.Width - gutter), frame.Height)
            : new Rect(frame.X, frame.Y, frame.Width, Math.Max(0, frame.Height - gutter));
    }

    /// <summary>轨道区（gutter 槽）。</summary>
    private Rect TrackRect(Rect frame)
    {
        var gutter = Math.Min(ScrollbarMetrics.Thickness, Math.Max(0, Axis == ScrollAxis.Vertical ? frame.Width : frame.Height));
        return Axis == ScrollAxis.Vertical
            ? new Rect(frame.Right - gutter, frame.Y, gutter, frame.Height)
            : new Rect(frame.X, frame.Bottom - gutter, frame.Width, gutter);
    }

    private static ScrollbarState? ScrollbarStateOf(ViewNode node)
        => node.InteractionState as ScrollbarState;

    private static ScrollbarState ScrollbarStateOrCreate(ViewNode node)
        => node.InteractionState as ScrollbarState ?? (ScrollbarState)(node.InteractionState = new ScrollbarState());

    private static void CancelBarGesture(ScrollbarState state) => state.ReleaseGesture();

    private void EmitScrollbar(ViewNode node, DrawContext context)
    {
        var horizontal = Axis == ScrollAxis.Horizontal;
        var track = TrackRect(node.Frame);
        var contentRect = ContentRect(node.Frame);
        var child = node.ChildList[0];
        var contentExtent = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
        var viewportExtent = horizontal ? contentRect.Width : contentRect.Height;
        var offset = horizontal ? node.ScrollOffsetX : node.ScrollOffsetY;
        ScrollbarPainter.Draw(node, context, ScrollbarStateOf(node), track, horizontal,
            viewportExtent, contentExtent, offset);
    }

    internal override bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e)
    {
        ScrollbarStateOrCreate(node).LastPointerPoint = e.Position;
        return false;
    }

    internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = ScrollbarStateOrCreate(node);
        state.LastPointerPoint = e.Position;

        if (state.IsDraggingBar)
        {
            var horizontal = Axis == ScrollAxis.Horizontal;
            var track = TrackRect(node.Frame);
            var contentRect = ContentRect(node.Frame);
            var child = node.ChildList[0];
            var contentExtent = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var viewportExtent = horizontal ? contentRect.Width : contentRect.Height;
            var next = ScrollbarMetrics.DragToOffset(
                track, horizontal, viewportExtent, contentExtent,
                state.BarDragAnchor, e.Position,
                horizontal ? node.ScrollOffsetX : node.ScrollOffsetY);

            if (horizontal)
            {
                node.ScrollOffsetX = next;
            }
            else
            {
                node.ScrollOffsetY = next;
            }

            node.Runtime.InvalidateLayout(node);
            context.MarkHandled();
            return true;
        }

        return false;
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = ScrollbarStateOrCreate(node);
        state.LastPointerPoint = e.Position;

        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var horizontal = Axis == ScrollAxis.Horizontal;
        var track = TrackRect(node.Frame);
        var contentRect = ContentRect(node.Frame);
        var child = node.ChildList[0];
        var contentExtent = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
        var viewportExtent = horizontal ? contentRect.Width : contentRect.Height;
        var hit = ScrollbarMetrics.HitTest(track, horizontal, viewportExtent, contentExtent,
            horizontal ? node.ScrollOffsetX : node.ScrollOffsetY, e.Position);

        if (!ScrollbarMetrics.ShowScrollbar(viewportExtent, contentExtent,
                ScrollbarMetrics.TrackLength(track, horizontal)))
        {
            // 空槽仍属 owner 区域：消费不冒泡、不夺焦、不进子路径。
            if (track.Contains(e.Position))
            {
                context.MarkHandled();
                return true;
            }
            return false;
        }

        switch (hit)
        {
            case ScrollbarTrackHit.Thumb:
                state.BeginThumbDrag(
                    Axis, track, contentExtent, viewportExtent,
                    ScrollbarMetrics.DragGrabAnchor(
                        track, horizontal, viewportExtent, contentExtent,
                        horizontal ? node.ScrollOffsetX : node.ScrollOffsetY, e.Position));
                context.MarkHandled();
                return true;

            case ScrollbarTrackHit.Before:
            case ScrollbarTrackHit.After:
                state.BeginTrackPress(Axis, track, contentExtent, viewportExtent);
                var maxOffset = Math.Max(0, contentExtent - viewportExtent);
                var next = ScrollbarMetrics.PageOffset(hit, viewportExtent,
                    horizontal ? node.ScrollOffsetX : node.ScrollOffsetY, maxOffset);
                if (horizontal)
                {
                    node.ScrollOffsetX = next;
                }
                else
                {
                    node.ScrollOffsetY = next;
                }
                node.Runtime.InvalidateLayout(node);
                context.MarkHandled();
                return true;
        }

        return false;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = ScrollbarStateOrCreate(node);
        state.LastPointerPoint = e.Position;

        if (state.PressKind != ScrollbarPressKind.None)
        {
            state.ReleaseGesture();
            context.MarkHandled();
            return true;
        }

        return false;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        var state = ScrollbarStateOf(node);
        if (state is not null && state.PressKind != ScrollbarPressKind.None)
        {
            state.ReleaseGesture();
        }

        return false;
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        // Axis 变化（immutable View 重建可换值）→ 取消 bar-gesture。
        if (previous is ScrollView prev && prev.Axis != Axis)
        {
            var state = ScrollbarStateOf(node);
            if (state is not null)
            {
                CancelBarGesture(state);
            }
        }
    }
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
