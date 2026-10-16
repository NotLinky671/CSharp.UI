using System.Reflection;
using CSharp.UI.Skia;
using SkiaSharp;

namespace CSharp.UI.Testing;

/// <summary>
/// R17 测试专用参考：用与生产栅格化完全相同的字形定位（<c>SkiaTextService</c> 的私有 <c>DrawLine</c>），
/// 但把文字<b>直接</b>绘制到真实颜色表面，得到颜色相关的最终像素/覆盖率。
/// </summary>
/// <remarks>
/// <para>
/// 为什么用反射：生产定位逻辑不对外暴露后端点阵对象，也不应向测试扩大公开 API；本参考只读取字形位置，
/// 不调用 <see cref="ITextService.Rasterize(TextLayoutResult, Color)"/>，因此不是“用同一个 helper 自证”。
/// </para>
/// <para>
/// 参考表面的混合由 Skia 自己完成（Bgra8888 预乘），与 GPU 路径是两条独立实现，比较的是最终 RGB。
/// </para>
/// </remarks>
internal static class TextDirectReference
{
    private static readonly Type ServiceType = typeof(SkiaTextService);

    private static readonly FieldInfo FontsField =
        ServiceType.GetField("_fonts", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("SkiaTextService._fonts 不存在。");

    private static readonly MethodInfo ResolvePrimary =
        FontsField.FieldType.GetMethod("ResolvePrimary")
        ?? throw new InvalidOperationException("SkiaFontResolver.ResolvePrimary 不存在。");

    private static readonly MethodInfo DrawLine =
        ServiceType.GetMethod("DrawLine", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("SkiaTextService.DrawLine 不存在。");

    private static readonly MethodInfo GraphemeStarts =
        ServiceType.Assembly.GetType("CSharp.UI.Skia.Graphemes")?.GetMethod("Starts")
        ?? throw new InvalidOperationException("CSharp.UI.Skia.Graphemes.Starts 不存在。");

    /// <summary>
    /// 把 <paramref name="layout"/> 按 <paramref name="color"/> 直接绘制到不透明 <paramref name="background"/> 上，
    /// 画布左上角对应 <paramref name="originX"/>/<paramref name="originY"/> 物理像素。
    /// 返回 RGBA8 预乘像素，与 <c>GlOffscreenSurface.ReadPixels</c> 的行序/格式一致。
    /// </summary>
    /// <remarks>
    /// 颜色半透明时，整段文字按<b>不透明 RGB</b> 画进一个图层，Restore 时统一乘以 alpha：
    /// 框架的契约是“先得到整块不透明文字遮罩，再统一应用 tint alpha × opacity”。
    /// 若把 alpha 传给每个字形/字体 run 的 paint，多 run（例如中英文回退）在边缘会各自 source-over，
    /// 与纹理整体透明度不是同一语义，不能作为参考。
    /// </remarks>
    public static PixelBuffer RenderOnBackground(
        SkiaTextService service,
        TextLayoutResult layout,
        Color color,
        Color background,
        int width,
        int height,
        int originX = 0,
        int originY = 0)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var scale = layout.PixelScale is > 0 and < double.PositiveInfinity ? layout.PixelScale : 1.0;
        var starts = (int[])GraphemeStarts.Invoke(null, [layout.Text])!;
        var resolver = FontsField.GetValue(service)!;
        var primary = (SKTypeface)ResolvePrimary.Invoke(resolver, [layout.Font])!;

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(background.R, background.G, background.B, background.A));
            canvas.Translate(originX, originY);
            canvas.Scale((float)scale, (float)scale);
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, 255),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            var translucent = color.A < 255;
            if (translucent)
            {
                using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(color.A) };
                canvas.SaveLayer(layerPaint);
            }

            foreach (var line in layout.Lines)
            {
                DrawLine.Invoke(service, [canvas, paint, layout, line, starts, primary]);
            }

            if (translucent)
            {
                canvas.Restore();
            }
        }

        return ToRgba(bitmap, width, height);
    }

    /// <summary>
    /// 把文字直接绘制到全透明表面，返回真实覆盖率（alpha 通道）的 Alpha8 缓冲；
    /// 尺寸与 <see cref="ITextService.Rasterize(TextLayoutResult, Color)"/> 的栅格尺寸一致。
    /// </summary>
    public static PixelBuffer RenderCoverage(SkiaTextService service, TextLayoutResult layout, Color color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var scale = layout.PixelScale is > 0 and < double.PositiveInfinity ? layout.PixelScale : 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(layout.Size.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Size.Height * scale));
        var rgba = RenderOnBackground(service, layout, color, Color.Transparent, width, height);
        var buffer = new PixelBuffer(width, height, PixelFormat.Alpha8);
        for (var i = 0; i < buffer.Pixels.Length; i++)
        {
            buffer.Pixels[i] = rgba.Pixels[(i * 4) + 3];
        }

        return buffer;
    }

    private static PixelBuffer ToRgba(SKBitmap bitmap, int width, int height)
    {
        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        var span = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var row = span.Slice(y * bitmap.RowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                var target = (y * width * 4) + (x * 4);
                buffer.Pixels[target] = row[(x * 4) + 2];
                buffer.Pixels[target + 1] = row[(x * 4) + 1];
                buffer.Pixels[target + 2] = row[(x * 4) + 0];
                buffer.Pixels[target + 3] = row[(x * 4) + 3];
            }
        }

        return buffer;
    }
}
