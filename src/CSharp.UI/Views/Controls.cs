namespace CSharp.UI;

/// <summary>
/// 文本绘制的共享辅助：构造排版请求、缓存键并输出纹理绘制命令。
/// </summary>
internal static class TextPainter
{
    /// <summary>排版文本（沿用文本服务缓存）。</summary>
    public static TextLayoutResult Layout(
        DrawContext context,
        ViewNode node,
        string text,
        FontDescription font,
        double maxWidth,
        double pixelScale,
        TextWrapping wrapping = TextWrapping.NoWrap,
        TextTrimming trimming = TextTrimming.None,
        TextAlignment alignment = TextAlignment.Left,
        int maxLines = 1,
        double lineHeight = 0)
        => context.TextService.Layout(CreateRequest(text, font, maxWidth, pixelScale, wrapping, trimming, alignment, maxLines, lineHeight));

    /// <summary>测量阶段的排版入口。</summary>
    public static TextLayoutResult Layout(
        LayoutContext context,
        string text,
        FontDescription font,
        double maxWidth,
        double pixelScale,
        TextWrapping wrapping = TextWrapping.NoWrap,
        TextTrimming trimming = TextTrimming.None,
        TextAlignment alignment = TextAlignment.Left,
        int maxLines = 1,
        double lineHeight = 0)
        => context.TextService.Layout(CreateRequest(text, font, maxWidth, pixelScale, wrapping, trimming, alignment, maxLines, lineHeight));

    /// <summary>构造排版请求。</summary>
    public static TextLayoutRequest CreateRequest(
        string text,
        FontDescription font,
        double maxWidth,
        double pixelScale,
        TextWrapping wrapping = TextWrapping.NoWrap,
        TextTrimming trimming = TextTrimming.None,
        TextAlignment alignment = TextAlignment.Left,
        int maxLines = 1,
        double lineHeight = 0)
        => new(text)
        {
            Font = font,
            MaxWidth = maxWidth,
            Wrapping = wrapping,
            Trimming = trimming,
            Alignment = alignment,
            MaxLines = maxLines,
            LineHeight = lineHeight,
            PixelScale = pixelScale,
        };

    /// <summary>
    /// 文本块在给定矩形内垂直居中后的顶边 Y（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 文本纹理、光标、选择高亮与点击命中必须共用这一原点：文本框高度或字号变化时，
    /// 若光标/选区仍以内容区顶边为原点，就会与垂直居中的文字行错位。
    /// 高度小于文本时偏移为负，由调用方的裁剪负责截断。
    /// 该值仍是逻辑坐标；落到物理像素必须先经过 <see cref="ResolveOrigin"/> 的吸附。
    /// </remarks>
    public static double VerticalOrigin(Rect bounds, Size textSize)
        => bounds.Y + ((bounds.Height - textSize.Height) / 2);

    /// <summary>
    /// 把文本原点吸附到物理像素网格：返回的逻辑坐标满足 <c>value * pixelScale</c> 为整数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文字遮罩由 <see cref="ITextService.Rasterize"/> 按 <c>ceil(逻辑尺寸 × DPI)</c> 分配，
    /// 再整张映射到目标矩形。只有让遮罩左上角落在整数物理像素上，1 个纹素才恰好覆盖 1 个物理像素；
    /// 否则线性过滤会把整张遮罩二次混合（字迹变糊），半像素偏移还会改变笔画覆盖率。
    /// </para>
    /// <para>
    /// 这里只吸附原点，不改变排版尺寸：布局、换行、对齐与裁剪继续使用小数逻辑坐标。
    /// 文字纹理、光标、选区与点击命中必须共用同一个吸附后的原点，避免出现半个物理像素的相对错位。
    /// </para>
    /// </remarks>
    public static Point SnapToPhysicalPixel(Point origin, double pixelScale)
    {
        var scale = NormalizeScale(pixelScale);
        return new Point(
            Math.Round(origin.X * scale, MidpointRounding.AwayFromZero) / scale,
            Math.Round(origin.Y * scale, MidpointRounding.AwayFromZero) / scale);
    }

    /// <summary>
    /// 解析文本遮罩的目标逻辑矩形：左上角是吸附到物理像素的文本原点，
    /// 尺寸为纹理像素数 ÷ DPI，即 1 个纹素对应 1 个物理像素。
    /// </summary>
    /// <remarks>
    /// 尺寸可能比排版尺寸大不足 1 物理像素（栅格化的 ceil 余量）。该余量必须留在纹理里
    /// 1:1 绘制，不能被当作整张纹理的缩放系数；否则采样相位会随文字位置变化。
    /// </remarks>
    public static Rect ResolveTextureBounds(TextureHandle handle, Point origin, double pixelScale)
    {
        var scale = NormalizeScale(pixelScale);
        return new Rect(origin.X, origin.Y, handle.PixelWidth / scale, handle.PixelHeight / scale);
    }

    /// <summary>
    /// 解析文本块在目标矩形内的绘制原点：先按 <paramref name="alignment"/> 与垂直居中算出逻辑原点，
    /// 再吸附到物理像素（DPR 由 <paramref name="pixelScale"/> 给出）。
    /// </summary>
    /// <remarks>文字纹理、光标、选区与点击命中都必须使用本方法返回的原点。</remarks>
    public static Point ResolveOrigin(Rect bounds, Size textSize, TextAlignment alignment, double pixelScale)
    {
        var x = alignment switch
        {
            TextAlignment.Center => bounds.X + ((bounds.Width - textSize.Width) / 2),
            TextAlignment.Right => bounds.Right - textSize.Width,
            _ => bounds.X,
        };
        return SnapToPhysicalPixel(new Point(x, VerticalOrigin(bounds, textSize)), pixelScale);
    }

    /// <summary>把排版结果作为纹理绘制到指定矩形（按 <paramref name="alignment"/> 水平对齐，垂直居中）。</summary>
    public static void Paint(
        DrawContext context,
        ViewNode node,
        TextLayoutResult layout,
        Color color,
        Rect bounds,
        TextAlignment alignment = TextAlignment.Left,
        double opacity = 1.0)
    {
        if (layout.Size.IsEmpty || color.A == 0)
        {
            return;
        }

        var textureService = context.TextureService;
        if (textureService is null)
        {
            return;
        }

        var origin = ResolveOrigin(bounds, layout.Size, alignment, layout.PixelScale);

        // 遮罩依赖文字颜色（Skia 的颜色相关 gamma/对比度处理），因此颜色必须进入 GPU 纹理缓存键；
        // 只保留 RGB：透明度由 TextureCommand 的 tint alpha / opacity 在 GPU 统一乘一次。
        var maskColor = color.WithAlpha(255);
        var key = new TextPaintKey(layout.Text, layout.Font, layout.PixelScale, layout.Size, alignment, maskColor);
        var handle = textureService.GetOrCreate(key, () => context.TextService.Rasterize(layout, maskColor));
        if (!handle.IsValid)
        {
            return;
        }

        context.Commands.Add(new TextureCommand(
            handle,
            ResolveTextureBounds(handle, origin, layout.PixelScale),
            new Rect(0, 0, handle.PixelWidth, handle.PixelHeight),
            color,
            node.Environment.Opacity * opacity));
    }

    private static double NormalizeScale(double pixelScale)
        => pixelScale is > 0 and < double.PositiveInfinity ? pixelScale : 1.0;

    /// <summary>控件文本纹理缓存键（<see cref="Color"/> 只以 RGB 参与，alpha 由绘制端统一处理）。</summary>
    private readonly record struct TextPaintKey(string Text, FontDescription Font, double PixelScale, Size Size, TextAlignment Alignment, Color Color);
}

/// <summary>
/// 按钮：显示标签、响应鼠标点击与 Enter/Space 键盘激活，并区分正常、悬停、按下、焦点与禁用状态。
/// </summary>
public sealed class Button : View
{
    /// <summary>由标签与点击回调创建。</summary>
    public Button(string label, Action? onClick = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        Label = label;
        OnClick = onClick;
    }

    /// <summary>按钮标签。</summary>
    public string Label { get; init; }

    /// <summary>点击回调；协调更新后会替换为最新的回调，旧回调不会被调用。</summary>
    public Action? OnClick { get; init; }

    /// <summary>显式字体；null 表示继承环境字体。</summary>
    public FontDescription? Font { get; init; }

    /// <summary>水平内边距（逻辑像素）。</summary>
    public double HorizontalPadding { get; init; } = 14;

    /// <summary>垂直内边距（逻辑像素）。</summary>
    public double VerticalPadding { get; init; } = 8;

    /// <summary>圆角半径。</summary>
    public double CornerRadius { get; init; } = 6;

    /// <summary>最小高度。</summary>
    public double MinHeight { get; init; } = 32;

    /// <summary>用作主操作按钮（使用强调色）。</summary>
    public bool IsPrimary { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"Button(\"{Label}\")";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    /// <summary>返回替换标签与回调的副本。</summary>
    public Button With(string label, Action? onClick) => new(label, onClick ?? OnClick)
    {
        Font = Font,
        HorizontalPadding = HorizontalPadding,
        VerticalPadding = VerticalPadding,
        CornerRadius = CornerRadius,
        MinHeight = MinHeight,
        IsPrimary = IsPrimary,
    };

    /// <summary>返回主操作按钮副本。</summary>
    public Button Primary() => new(Label, OnClick)
    {
        Font = Font,
        HorizontalPadding = HorizontalPadding,
        VerticalPadding = VerticalPadding,
        CornerRadius = CornerRadius,
        MinHeight = MinHeight,
        IsPrimary = true,
    };

    internal static FontDescription ResolveFont(ViewNode node, FontDescription? explicitFont)
        => explicitFont ?? node.Environment.Font ?? FontDescription.Default;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var font = ResolveFont(node, Font);
        var available = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth - (HorizontalPadding * 2)) : double.PositiveInfinity;
        var layout = TextPainter.Layout(context, Label, font, available, context.PixelScale);
        node.ViewCache = layout;
        var width = layout.Size.Width + (HorizontalPadding * 2);
        var height = Math.Max(MinHeight, layout.Size.Height + (VerticalPadding * 2));
        node.DesiredSize = new Size(width, height);
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = node.IsEffectivelyDisabled;
        var fill = disabled
            ? theme.DisabledBackground
            : node.IsPressed
                ? (IsPrimary ? theme.Accent.Lerp(Color.Black, 0.2) : theme.ControlPressedBackground)
                : node.IsHovered
                    ? (IsPrimary ? theme.Accent.Lerp(Color.White, 0.12) : theme.ControlHoverBackground)
                    : IsPrimary ? theme.Accent : theme.ControlBackground;

        var stroke = node.IsFocused ? theme.FocusRing : theme.Border;
        var strokeWidth = node.IsFocused ? 2 : 1;

        context.Commands.Add(new RectangleCommand(node.Frame, fill, CornerRadius, stroke, strokeWidth, node.Environment.Opacity));

        var font = ResolveFont(node, Font);
        var request = TextPainter.CreateRequest(Label, font, node.Frame.Width - (HorizontalPadding * 2), context.PixelScale);
        var layout = context.TextService.Layout(request);
        var color = disabled
            ? theme.DisabledForeground
            : IsPrimary ? theme.AccentForeground : theme.Foreground;
        TextPainter.Paint(context, node, layout, color, node.Frame, TextAlignment.Center);
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        context.Runtime.SetFocus(node);
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled || !node.IsPressed)
        {
            return false;
        }

        if (node.Frame.Contains(e.Position))
        {
            Invoke(node);
        }

        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        node.IsPressed = false;
        return false;
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        if (e.Key is UiKey.Enter or UiKey.Space)
        {
            if (!e.Repeat)
            {
                node.IsPressed = true;
                context.Runtime.InvalidateRender();
            }

            context.MarkHandled();
            return true;
        }

        return false;
    }

    internal override bool OnKeyUp(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        if (e.Key is UiKey.Enter or UiKey.Space)
        {
            node.IsPressed = false;
            Invoke(node);
            context.MarkHandled();
            return true;
        }

        return false;
    }

    private void Invoke(ViewNode node)
    {
        node.IsPressed = false;
        OnClick?.Invoke();
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Button button && button.Label == Label;
}

/// <summary>
/// 开关控件：绑定布尔值，鼠标点击或 Space/Enter 切换；外部状态变化会更新显示。
/// </summary>
public sealed class Toggle : View
{
    /// <summary>由绑定与可选标签创建。</summary>
    public Toggle(Binding<bool> value, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Label = label;
    }

    /// <summary>由当前值与回调创建（每次构建读取最新值）。</summary>
    public Toggle(bool value, Action<bool>? onChange, string? label = null)
        : this(new Binding<bool>(() => value, v => onChange?.Invoke(v)), label)
    {
    }

    /// <summary>布尔绑定。</summary>
    public Binding<bool> Value { get; }

    /// <summary>可选标签。</summary>
    public string? Label { get; }

    /// <summary>开关宽度。</summary>
    public double TrackWidth { get; init; } = 38;

    /// <summary>开关高度。</summary>
    public double TrackHeight { get; init; } = 20;

    /// <inheritdoc />
    public override string DebugName => $"Toggle({Label ?? "value"})";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var width = TrackWidth;
        var height = Math.Max(TrackHeight, 24);
        if (!string.IsNullOrEmpty(Label))
        {
            var font = node.Environment.Font ?? FontDescription.Default;
            var available = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth - TrackWidth - 8) : double.PositiveInfinity;
            var layout = TextPainter.Layout(context, Label, font, available, context.PixelScale);
            node.ViewCache = layout;
            width += 8 + layout.Size.Width;
            height = Math.Max(height, layout.Size.Height);
        }

        node.DesiredSize = new Size(width, height);
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var isOn = ReadValue(node);
        var disabled = node.IsEffectivelyDisabled;
        var trackHeight = TrackHeight;
        var track = new Rect(node.Frame.X, node.Frame.Y + ((node.Frame.Height - trackHeight) / 2), TrackWidth, trackHeight);

        var trackColor = disabled
            ? theme.DisabledBackground
            : isOn
                ? theme.Accent
                : node.IsHovered ? theme.ControlHoverBackground : theme.ControlBackground;
        var strokeColor = node.IsFocused ? theme.FocusRing : theme.Border;
        context.Commands.Add(new RectangleCommand(track, trackColor, trackHeight / 2, strokeColor, node.IsFocused ? 2 : 1, node.Environment.Opacity));

        var knobSize = trackHeight - 4;
        var knobX = isOn ? track.Right - knobSize - 2 : track.X + 2;
        var knob = new Rect(knobX, track.Y + 2, knobSize, knobSize);
        var knobColor = disabled ? theme.DisabledForeground : isOn ? theme.AccentForeground : theme.MutedForeground;
        context.Commands.Add(new RectangleCommand(knob, knobColor, knobSize / 2, null, 0, node.Environment.Opacity));

        if (!string.IsNullOrEmpty(Label))
        {
            var font = node.Environment.Font ?? FontDescription.Default;
            var label = TextPainter.Layout(context, node, Label, font, double.PositiveInfinity, context.PixelScale);
            var bounds = new Rect(track.Right + 8, node.Frame.Y, Math.Max(0, node.Frame.Right - track.Right - 8), node.Frame.Height);
            TextPainter.Paint(context, node, label, disabled ? theme.DisabledForeground : theme.Foreground, bounds);
        }
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        context.Runtime.SetFocus(node);
        node.IsPressed = true;
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled || !node.IsPressed)
        {
            return false;
        }

        node.IsPressed = false;
        if (node.Frame.Contains(e.Position))
        {
            Value.Value = !Value.Read(node);
        }

        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        node.IsPressed = false;
        return false;
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        if (e.Key is UiKey.Space or UiKey.Enter && !e.Repeat)
        {
            Value.Value = !Value.Read(node);
            context.MarkHandled();
            return true;
        }

        return false;
    }

    private bool ReadValue(ViewNode node)
    {
        // 通过绑定读取来源状态：既登记依赖（绑定来自 State 时），也取得最新值。
        return Value.Read(node);
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Toggle toggle && toggle.Label == Label && toggle.TrackWidth == TrackWidth && toggle.TrackHeight == TrackHeight;
}

/// <summary>
/// 数值滑块：绑定数值，支持范围限制、拖动捕获与方向键步进。
/// </summary>
public sealed class Slider : View
{
    /// <summary>由绑定与范围创建。</summary>
    public Slider(Binding<double> value, double min = 0, double max = 1, double step = 0.01, double? width = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max <= min)
        {
            throw new ArgumentOutOfRangeException(nameof(max), $"滑块上界 {max} 必须大于下界 {min}。");
        }

        Value = value;
        Min = min;
        Max = max;
        Step = step <= 0 ? (max - min) / 100 : step;
        Width = width;
    }

    /// <summary>数值绑定。</summary>
    public Binding<double> Value { get; }

    /// <summary>下界。</summary>
    public double Min { get; }

    /// <summary>上界。</summary>
    public double Max { get; }

    /// <summary>键盘步进与拖动量化步长。</summary>
    public double Step { get; }

    /// <summary>显式宽度；null 表示使用默认宽度 160。</summary>
    public double? Width { get; }

    /// <summary>拖动条的视觉高度。</summary>
    public double TrackThickness { get; init; } = 4;

    /// <summary>滑块圆点半径。</summary>
    public double KnobRadius { get; init; } = 8;

    /// <summary>控件总高度。</summary>
    public double Height { get; init; } = 28;

    /// <inheritdoc />
    public override string DebugName => $"Slider({Min:0.##}–{Max:0.##})";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var width = Width ?? (constraints.IsWidthTight ? constraints.MaxWidth : 160);
        node.DesiredSize = constraints.Constrain(new Size(width, Height));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = node.IsEffectivelyDisabled;
        var value = Clamp(Value.Read(node));
        var ratio = (value - Min) / (Max - Min);

        var trackRect = TrackRect(node.Frame);
        var fillRect = new Rect(trackRect.X, trackRect.Y, trackRect.Width * ratio, trackRect.Height);
        var trackColor = disabled ? theme.DisabledBackground : theme.ControlBackground;
        var fillColor = disabled ? theme.DisabledForeground : theme.Accent;

        context.Commands.Add(new RectangleCommand(trackRect, trackColor, trackRect.Height / 2, theme.Border, 1, node.Environment.Opacity));
        if (fillRect.Width > 0)
        {
            context.Commands.Add(new RectangleCommand(fillRect, fillColor, trackRect.Height / 2, null, 0, node.Environment.Opacity));
        }

        var knobX = trackRect.X + (trackRect.Width * ratio);
        var knob = new Rect(knobX - KnobRadius, node.Frame.Y + ((node.Frame.Height - (KnobRadius * 2)) / 2), KnobRadius * 2, KnobRadius * 2);
        var knobColor = disabled ? theme.DisabledBackground : node.IsHovered || node.IsPressed ? theme.Accent.Lerp(Color.White, 0.15) : theme.Accent;
        context.Commands.Add(new RectangleCommand(
            knob,
            knobColor,
            KnobRadius,
            node.IsFocused ? theme.FocusRing : theme.ControlBackground,
            node.IsFocused ? 2 : 1,
            node.Environment.Opacity));
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        context.Runtime.SetFocus(node);
        node.IsPressed = true;
        SetFromPosition(node, e.Position.X);
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
    {
        if (!node.IsPressed)
        {
            return false;
        }

        SetFromPosition(node, e.Position.X);
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (!node.IsPressed)
        {
            return false;
        }

        SetFromPosition(node, e.Position.X);
        node.IsPressed = false;
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        node.IsPressed = false;
        return false;
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var delta = e.Key switch
        {
            UiKey.Left or UiKey.Down => -Step,
            UiKey.Right or UiKey.Up => Step,
            _ => 0.0,
        };

        if (delta == 0)
        {
            return false;
        }

        Value.Value = Clamp(Quantize(Value.Read(node) + delta));
        context.MarkHandled();
        return true;
    }

    private Rect TrackRect(Rect frame)
    {
        var inset = KnobRadius;
        return new Rect(
            frame.X + inset,
            frame.Y + ((frame.Height - TrackThickness) / 2),
            Math.Max(0, frame.Width - (inset * 2)),
            TrackThickness);
    }

    private void SetFromPosition(ViewNode node, double x)
    {
        var track = TrackRect(node.Frame);
        if (track.Width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp((x - track.X) / track.Width, 0, 1);
        Value.Value = Clamp(Quantize(Min + (ratio * (Max - Min))));
    }

    private double Clamp(double value) => Math.Clamp(double.IsNaN(value) ? Min : value, Min, Max);

    private double Quantize(double value)
    {
        if (Step <= 0)
        {
            return value;
        }

        var steps = Math.Round((value - Min) / Step, MidpointRounding.AwayFromZero);
        return Min + (steps * Step);
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Slider slider && slider.Min == Min && slider.Max == Max && slider.Width == Width && slider.Height == Height;
}
