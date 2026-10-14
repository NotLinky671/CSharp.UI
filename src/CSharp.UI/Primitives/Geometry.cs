namespace CSharp.UI;

/// <summary>
/// 逻辑像素坐标系中的点。X 向右、Y 向下，原点为窗口内容区左上角。
/// </summary>
/// <remarks>
/// 逻辑像素与 framebuffer 像素的区别：布局、命中测试和输入事件始终使用逻辑像素；
/// 只有渲染器在提交绘制时需要把逻辑像素乘以 <see cref="PixelScale"/>（DPI 缩放）得到 framebuffer 像素。
/// </remarks>
public readonly record struct Point(double X, double Y)
{
    /// <summary>原点 (0, 0)。</summary>
    public static readonly Point Zero = new(0, 0);

    /// <summary>返回按偏移量移动后的点。</summary>
    public Point Offset(double dx, double dy) => new(X + dx, Y + dy);

    /// <summary>返回按向量移动后的点。</summary>
    public Point Offset(Point delta) => new(X + delta.X, Y + delta.Y);

    /// <summary>返回两点之间的向量。</summary>
    public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y);

    /// <summary>返回点与向量的和。</summary>
    public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>到另一个点的欧氏距离。</summary>
    public double DistanceTo(Point other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>逻辑像素尺寸。零尺寸使用 <see cref="Zero"/>，无界约束使用 <see cref="Infinity"/>。</summary>
public readonly record struct Size(double Width, double Height)
{
    /// <summary>零尺寸。</summary>
    public static readonly Size Zero = new(0, 0);

    /// <summary>正无穷尺寸，用于表示“不限制”的可用空间。</summary>
    public static readonly Size Infinity = new(double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>宽或高不为正。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>宽高均为有限值。</summary>
    public bool IsFinite => !double.IsInfinity(Width) && !double.IsInfinity(Height);

    /// <summary>宽高比；宽或高不正时返回 0。</summary>
    public double AspectRatio => IsEmpty ? 0 : Width / Height;

    /// <summary>把尺寸的每个分量限制在 <paramref name="max"/> 之内。</summary>
    public Size Clamp(Size max) => new(Math.Min(Width, max.Width), Math.Min(Height, max.Height));

    /// <summary>逐分量相加。</summary>
    public static Size operator +(Size left, Size right) => new(left.Width + right.Width, left.Height + right.Height);

    /// <summary>逐分量相减，结果不为负。</summary>
    public static Size operator -(Size left, Size right) => new(Math.Max(0, left.Width - right.Width), Math.Max(0, left.Height - right.Height));

    /// <summary>展开为以原点为左上角的矩形。</summary>
    public Rect ToRect() => new(0, 0, Width, Height);

    public override string ToString() => $"{Width:0.###}×{Height:0.###}";
}

/// <summary>轴对齐矩形，使用逻辑像素、Y 轴向下。</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    /// <summary>空矩形（位于原点、零尺寸）。</summary>
    public static readonly Rect Empty = new(0, 0, 0, 0);

    /// <summary>由左上角与尺寸构造。</summary>
    public Rect(Point origin, Size size)
        : this(origin.X, origin.Y, size.Width, size.Height)
    {
    }

    /// <summary>由两个角点构造（自动规范化为非负宽高）。</summary>
    public static Rect FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Point TopLeft => new(X, Y);

    public Point TopRight => new(Right, Y);

    public Point BottomLeft => new(X, Bottom);

    public Point BottomRight => new(Right, Bottom);

    public Point Center => new(X + (Width / 2), Y + (Height / 2));

    public Size Size => new(Width, Height);

    /// <summary>宽或高不为正；空矩形不包含任何点，也不与任何图形相交。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>是否包含指定点。右边界和下边界视为开区间。</summary>
    public bool Contains(Point point)
        => !IsEmpty && point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    /// <summary>是否包含指定点，包含右/下边界。</summary>
    public bool ContainsInclusive(Point point)
        => !IsEmpty && point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    /// <summary>是否完全包含另一个矩形。</summary>
    public bool Contains(Rect other)
        => !IsEmpty && !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>是否与另一个矩形相交（空矩形不算相交）。</summary>
    public bool IntersectsWith(Rect other)
        => !IsEmpty && !other.IsEmpty && other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>返回两个矩形的相交区域；不相交或任一为空时返回 <see cref="Empty"/>。</summary>
    public Rect Intersect(Rect other)
    {
        if (!IntersectsWith(other))
        {
            return Empty;
        }

        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>返回包含两个矩形的最小矩形。</summary>
    public Rect Union(Rect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>向内收缩（负值表示向外扩张），宽高不为负。</summary>
    public Rect Deflate(Thickness thickness)
        => new(X + thickness.Left, Y + thickness.Top, Math.Max(0, Width - thickness.Horizontal), Math.Max(0, Height - thickness.Vertical));

    /// <summary>四边等量向外扩张（负值为收缩）。</summary>
    public Rect Inflate(double amount) => Inflate(new Thickness(amount));

    /// <summary>四边按 <paramref name="thickness"/> 向外扩张（负值为收缩）。</summary>
    public Rect Inflate(Thickness thickness) => Deflate(new Thickness(-thickness.Left, -thickness.Top, -thickness.Right, -thickness.Bottom));

    /// <summary>平移矩形。</summary>
    public Rect Translate(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>平移矩形到指定左上角。</summary>
    public Rect WithOrigin(double x, double y) => new(x, y, Width, Height);

    /// <summary>以指定尺寸为上限的居中矩形（Frame 的 Center 对齐）。</summary>
    public Rect AlignInside(Size childSize, Alignment alignment)
    {
        var width = alignment == Alignment.Stretch ? Width : Math.Min(childSize.Width, Width);
        var height = alignment == Alignment.Stretch ? Height : Math.Min(childSize.Height, Height);
        var x = alignment switch
        {
            Alignment.Center or Alignment.Stretch => X + ((Width - width) / 2),
            Alignment.End => X + Width - width,
            _ => X,
        };
        var y = alignment switch
        {
            Alignment.Center or Alignment.Stretch => Y + ((Height - height) / 2),
            Alignment.End => Y + Height - height,
            _ => Y,
        };
        return new Rect(Math.Round(x, 3), Math.Round(y, 3), width, height);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Width:0.###}×{Height:0.###})";
}

/// <summary>四边内边距（或边框宽度），单位逻辑像素。</summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    /// <summary>零内边距。</summary>
    public static readonly Thickness Zero = new(0, 0, 0, 0);

    /// <summary>四边相同。</summary>
    public Thickness(double uniform)
        : this(uniform, uniform, uniform, uniform)
    {
    }

    /// <summary>水平与垂直方向分别相同。</summary>
    public Thickness(double horizontal, double vertical)
        : this(horizontal, vertical, horizontal, vertical)
    {
    }

    /// <summary>水平方向总量。</summary>
    public double Horizontal => Left + Right;

    /// <summary>垂直方向总量。</summary>
    public double Vertical => Top + Bottom;

    /// <summary>四边是否都为零。</summary>
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public static Thickness operator +(Thickness left, Thickness right)
        => new(left.Left + right.Left, left.Top + right.Top, left.Right + right.Right, left.Bottom + right.Bottom);

    /// <summary>对每个分量取最大值（用于合并边框与内边距）。</summary>
    public static Thickness Max(Thickness left, Thickness right)
        => new(Math.Max(left.Left, right.Left), Math.Max(left.Top, right.Top), Math.Max(left.Right, right.Right), Math.Max(left.Bottom, right.Bottom));

    public override string ToString() => IsZero ? "0" : $"({Left:0.###}, {Top:0.###}, {Right:0.###}, {Bottom:0.###})";
}

/// <summary>子项在容器内的对齐方式。</summary>
public enum Alignment
{
    /// <summary>靠起始边。</summary>
    Start,

    /// <summary>居中。</summary>
    Center,

    /// <summary>靠结束边。</summary>
    End,

    /// <summary>拉伸填满容器。</summary>
    Stretch,
}
