namespace CSharp.UI;

/// <summary>CPU 位图像素格式。</summary>
public enum PixelFormat
{
    /// <summary>预乘 alpha 的 8 位 RGBA，字节顺序 R、G、B、A。纹理与图片统一使用该格式。</summary>
    Rgba8888Premultiplied,

    /// <summary>预乘 alpha 的 8 位 BGRA，字节顺序 B、G、R、A。Windows 上 Skia 的原生顺序。</summary>
    Bgra8888Premultiplied,

    /// <summary>8 位单通道覆盖率，用于文本字形遮罩；绘制时通过 tint 着色。</summary>
    Alpha8,
}

/// <summary>
/// 平台无关的 CPU 位图。像素以行为单位紧密或按 <see cref="Stride"/> 排列，行优先、原点左上。
/// </summary>
/// <remarks>
/// 所有权：<see cref="PixelBuffer"/> 由创建它的服务负责释放；框架把它交给纹理服务上传后不再持有。
/// </remarks>
public sealed class PixelBuffer
{
    /// <summary>创建位图并分配像素数组。</summary>
    public PixelBuffer(int width, int height, PixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Format = format;
        BytesPerPixel = format == PixelFormat.Alpha8 ? 1 : 4;
        Stride = width * BytesPerPixel;
        Pixels = new byte[Stride * height];
    }

    /// <summary>包装已有像素数组。</summary>
    public PixelBuffer(int width, int height, PixelFormat format, byte[] pixels, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Width = width;
        Height = height;
        Format = format;
        BytesPerPixel = format == PixelFormat.Alpha8 ? 1 : 4;
        Stride = stride > 0 ? stride : width * BytesPerPixel;
        if (pixels.Length < Stride * height)
        {
            throw new ArgumentException($"像素数组长度 {pixels.Length} 小于所需 {Stride * height}。", nameof(pixels));
        }

        Pixels = pixels;
    }

    /// <summary>像素宽度（物理像素）。</summary>
    public int Width { get; }

    /// <summary>像素高度（物理像素）。</summary>
    public int Height { get; }

    /// <summary>像素格式。</summary>
    public PixelFormat Format { get; }

    /// <summary>每行字节数。</summary>
    public int Stride { get; }

    /// <summary>每像素字节数。</summary>
    public int BytesPerPixel { get; }

    /// <summary>像素数据。</summary>
    public byte[] Pixels { get; }

    /// <summary>按 alpha 与其他通道的预乘约定判断是否为预乘位图。</summary>
    public bool IsPremultiplied => Format is PixelFormat.Rgba8888Premultiplied or PixelFormat.Bgra8888Premultiplied;

    /// <summary>读取指定像素的非预乘颜色（Alpha8 返回白色并按覆盖率设置 alpha）。</summary>
    public Color GetColor(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) 超出 {Width}×{Height} 位图范围。");
        }

        var offset = (y * Stride) + (x * BytesPerPixel);
        switch (Format)
        {
            case PixelFormat.Alpha8:
                return new Color(255, 255, 255, Pixels[offset]);
            case PixelFormat.Bgra8888Premultiplied:
            {
                var b = Pixels[offset];
                var g = Pixels[offset + 1];
                var r = Pixels[offset + 2];
                var a = Pixels[offset + 3];
                return Unpremultiply(r, g, b, a);
            }
            default:
            {
                var r = Pixels[offset];
                var g = Pixels[offset + 1];
                var b = Pixels[offset + 2];
                var a = Pixels[offset + 3];
                return Unpremultiply(r, g, b, a);
            }
        }
    }

    private static Color Unpremultiply(byte r, byte g, byte b, byte a)
    {
        if (a == 0)
        {
            return new Color(0, 0, 0, 0);
        }

        if (a == 255)
        {
            return new Color(r, g, b, 255);
        }

        static byte Scale(byte value, byte alpha) => (byte)Math.Clamp((int)Math.Round(value * 255.0 / alpha), 0, 255);
        return new Color(Scale(r, a), Scale(g, a), Scale(b, a), a);
    }
}

/// <summary>
/// 平台无关的纹理句柄。只保存标识与元数据，不暴露 OpenGL 纹理名或任何后端对象。
/// </summary>
/// <remarks>
/// 句柄由纹理服务创建和释放；把无效句柄（<see cref="IsValid"/> 为 false）交给渲染器时该绘制命令会被忽略。
/// </remarks>
public readonly record struct TextureHandle(long Id, int PixelWidth, int PixelHeight, bool IsAlphaMask)
{
    /// <summary>无效句柄，可用于表示尚未创建或已释放的纹理。</summary>
    public static readonly TextureHandle Invalid = default;

    /// <summary>句柄是否有效。</summary>
    public bool IsValid => Id != 0;

    /// <summary>纹理逻辑尺寸：宽度除以缩放。</summary>
    public Size LogicalSize(double scale) => new(PixelWidth / Math.Max(scale, 0.0001), PixelHeight / Math.Max(scale, 0.0001));

    public override string ToString() => IsValid ? $"Texture#{Id} {PixelWidth}×{PixelHeight}{(IsAlphaMask ? " alpha" : string.Empty)}" : "Texture(invalid)";
}

/// <summary>
/// 纹理服务契约：按稳定键缓存 GPU 纹理，位图由工厂按需产生。
/// </summary>
/// <remarks>
/// 所有权：纹理由服务创建、缓存并释放；调用方只持有 <see cref="TextureHandle"/>。
/// 同一键重复请求返回同一句柄，避免重建时重复上传。
/// </remarks>
public interface ITextureService
{
    /// <summary>
    /// 取回（必要时创建）键对应的纹理。工厂返回的位图在方法返回后可由调用方丢弃。
    /// 键使用 <c>Equals</c> 比较：值类型键按键值，引用类型键（如 <see cref="ImageResource"/>）按引用。
    /// </summary>
    TextureHandle GetOrCreate(object key, Func<PixelBuffer> factory);

    /// <summary>释放并移除指定键的纹理。</summary>
    void Release(object key);

    /// <summary>清除全部缓存的纹理（例如 DPI 变化后）。</summary>
    void Clear();

    /// <summary>当前存活纹理数量，用于验证资源不持续增长。</summary>
    int LiveTextureCount { get; }

    /// <summary>缓存容量上限；超出后按最近最少使用淘汰。</summary>
    int Capacity { get; set; }
}

/// <summary>绘制命令基类。命令集合按提交顺序表达层叠次序。</summary>
public abstract record DrawCommand
{
    /// <summary>该命令影响的逻辑区域（逻辑像素），用于裁剪剔除与测试断言。</summary>
    public Rect GetBounds() => this switch
    {
        RectangleCommand rectangle => rectangle.Bounds,
        TextureCommand texture => texture.Bounds,
        PushClipCommand clip => clip.Bounds,
        PopClipCommand => Rect.Empty,
        _ => Rect.Empty,
    };
}

/// <summary>填充（可选描边/圆角）矩形。</summary>
/// <param name="Bounds">逻辑像素矩形。</param>
/// <param name="Fill">填充色；<see cref="Color.Transparent"/> 表示不填充。</param>
/// <param name="CornerRadius">圆角半径，0 表示直角。</param>
/// <param name="Stroke">描边色；为 null 表示无边框。</param>
/// <param name="StrokeWidth">描边宽度（逻辑像素），描边以 <paramref name="Bounds"/> 边界为中心线向内绘制。</param>
/// <param name="Opacity">附加透明度倍数（0—1），与颜色自身 alpha 相乘。</param>
public sealed record RectangleCommand(
    Rect Bounds,
    Color Fill,
    double CornerRadius = 0,
    Color? Stroke = null,
    double StrokeWidth = 0,
    double Opacity = 1) : DrawCommand;

/// <summary>绘制纹理（文本遮罩或图片）。</summary>
/// <param name="Texture">平台无关纹理句柄。</param>
/// <param name="Bounds">目标逻辑矩形。</param>
/// <param name="Source">纹理内的源区域，使用纹理像素坐标；为空表示整张纹理。</param>
/// <param name="Tint">着色与 alpha 相乘；文本遮罩通过它上色。</param>
/// <param name="Opacity">附加透明度倍数（0—1）。</param>
public sealed record TextureCommand(
    TextureHandle Texture,
    Rect Bounds,
    Rect Source,
    Color Tint,
    double Opacity = 1) : DrawCommand;

/// <summary>压入矩形裁剪区；与当前裁剪区取交集，直到匹配的 <see cref="PopClipCommand"/>。</summary>
public sealed record PushClipCommand(Rect Bounds) : DrawCommand;

/// <summary>弹出最近一次压入的裁剪区。</summary>
public sealed record PopClipCommand : DrawCommand;

/// <summary>有序绘制命令列表；不修改已加入的命令对象。</summary>
public sealed class DrawCommandList : IReadOnlyList<DrawCommand>
{
    private readonly List<DrawCommand> _commands = [];

    /// <summary>命令数量。</summary>
    public int Count => _commands.Count;

    /// <summary>按索引访问命令。</summary>
    public DrawCommand this[int index] => _commands[index];

    /// <summary>添加命令。</summary>
    public void Add(DrawCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command);
    }

    /// <summary>清空列表，复用已分配的存储。</summary>
    public void Clear() => _commands.Clear();

    /// <summary>按顺序枚举命令。</summary>
    public IEnumerator<DrawCommand> GetEnumerator() => _commands.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>返回与另一命令列表内容相同的快照，便于测试比较。</summary>
    public IReadOnlyList<DrawCommand> ToArray() => [.. _commands];
}

/// <summary>一帧的渲染输入：有序绘制命令、视口尺寸（逻辑像素）与 DPI 缩放。</summary>
/// <param name="Commands">按层叠顺序排列的绘制命令。</param>
/// <param name="ViewportSize">逻辑像素视口尺寸。</param>
/// <param name="PixelScale">逻辑像素到 framebuffer 像素的缩放（DPI）。</param>
/// <param name="ClearColor">清屏色。</param>
public sealed record RenderFrame(
    IReadOnlyList<DrawCommand> Commands,
    Size ViewportSize,
    double PixelScale,
    Color ClearColor)
{
    /// <summary>framebuffer 像素尺寸。</summary>
    public Size FramebufferSize => new(ViewportSize.Width * PixelScale, ViewportSize.Height * PixelScale);
}

/// <summary>绘制命令接收端；用于把一帧交给渲染后端，或在测试中记录命令。</summary>
public interface IRenderCommandTarget
{
    /// <summary>提交一帧。</summary>
    void Submit(RenderFrame frame);
}
