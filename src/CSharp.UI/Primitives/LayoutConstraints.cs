namespace CSharp.UI;

/// <summary>
/// 布局约束：父级允许子项使用的尺寸范围（逻辑像素）。
/// 最大值可以是 <see cref="double.PositiveInfinity"/>，表示该方向不限制（例如滚动容器在滚动方向的测量）。
/// </summary>
public readonly record struct LayoutConstraints(double MinWidth, double MaxWidth, double MinHeight, double MaxHeight)
{
    /// <summary>完全不限制的约束：最小 0，最大正无穷。</summary>
    public static LayoutConstraints Unbounded => new(0, double.PositiveInfinity, 0, double.PositiveInfinity);

    /// <summary>宽高均为固定值。</summary>
    public static LayoutConstraints Tight(Size size) => new(size.Width, size.Width, size.Height, size.Height);

    /// <summary>宽高固定值。</summary>
    public static LayoutConstraints Tight(double width, double height) => new(width, width, height, height);

    /// <summary>宽高上限固定、下限为 0。</summary>
    public static LayoutConstraints Loose(Size size) => new(0, size.Width, 0, size.Height);

    /// <summary>宽高上限固定、下限为 0。</summary>
    public static LayoutConstraints Loose(double width, double height) => new(0, width, 0, height);

    /// <summary>宽度固定、高度不限制。</summary>
    public static LayoutConstraints TightWidth(double width) => new(width, width, 0, double.PositiveInfinity);

    /// <summary>高度固定、宽度不限制。</summary>
    public static LayoutConstraints TightHeight(double height) => new(0, double.PositiveInfinity, height, height);

    /// <summary>宽度有上限、高度不限制，用于测量换行文本。</summary>
    public static LayoutConstraints BoundedWidth(double maxWidth) => new(0, maxWidth, 0, double.PositiveInfinity);

    /// <summary>宽度是否有限。</summary>
    public bool HasBoundedWidth => !double.IsPositiveInfinity(MaxWidth);

    /// <summary>高度是否有限。</summary>
    public bool HasBoundedHeight => !double.IsPositiveInfinity(MaxHeight);

    /// <summary>把尺寸限制到约束范围内。</summary>
    public Size Constrain(Size size) => new(
        Math.Clamp(size.Width, MinWidth, MaxWidth),
        Math.Clamp(size.Height, MinHeight, MaxHeight));

    /// <summary>宽度是否被固定（最小值等于最大值）。</summary>
    public bool IsWidthTight => MinWidth == MaxWidth && !double.IsInfinity(MinWidth);

    /// <summary>高度是否被固定。</summary>
    public bool IsHeightTight => MinHeight == MaxHeight && !double.IsInfinity(MinHeight);

    /// <summary>收缩内边距后得到的子项约束。</summary>
    public LayoutConstraints Deflate(Thickness thickness) => new(
        Math.Max(0, MinWidth - thickness.Horizontal),
        Math.Max(0, MaxWidth - thickness.Horizontal),
        Math.Max(0, MinHeight - thickness.Vertical),
        Math.Max(0, MaxHeight - thickness.Vertical));

    /// <summary>返回替换上限宽度后的约束，同时保证最小值不超过上限。</summary>
    public LayoutConstraints WithMaxWidth(double maxWidth) => new(Math.Min(MinWidth, maxWidth), maxWidth, MinHeight, MaxHeight);

    /// <summary>返回替换上限高度后的约束。</summary>
    public LayoutConstraints WithMaxHeight(double maxHeight) => new(MinWidth, MaxWidth, Math.Min(MinHeight, maxHeight), maxHeight);

    /// <summary>返回替换下限尺寸后的约束。</summary>
    public LayoutConstraints WithMin(double minWidth, double minHeight) => new(minWidth, Math.Max(minWidth, MaxWidth), minHeight, Math.Max(minHeight, MaxHeight));

    /// <summary>返回主方向不限制、交叉方向沿用当前约束的约束（用于堆叠测量）。</summary>
    public LayoutConstraints UnboundedHeight() => new(MinWidth, MaxWidth, 0, double.PositiveInfinity);

    /// <summary>返回宽度不限制、高度沿用当前约束的约束。</summary>
    public LayoutConstraints UnboundedWidth() => new(0, double.PositiveInfinity, MinHeight, MaxHeight);

    public override string ToString()
        => $"[{MinWidth:0.##}/{Fmt(MaxWidth)} × {MinHeight:0.##}/{Fmt(MaxHeight)}]";

    private static string Fmt(double value) => double.IsPositiveInfinity(value) ? "∞" : value.ToString("0.##");
}
