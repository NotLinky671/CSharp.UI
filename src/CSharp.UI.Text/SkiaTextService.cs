using SkiaSharp;

namespace CSharp.UI.Skia;

/// <summary>
/// 基于 SkiaSharp + HarfBuzz 的文本服务：字体解析与缺字回退、shaping 排版、字素级光标映射与 CPU 栅格化。
/// </summary>
/// <remarks>
/// <para>实现 <see cref="ITextService"/>，不向调用方暴露 SKTypeface/SKShaper 等后端对象。</para>
/// <para>
/// 缓存约定：
/// <list type="bullet">
/// <item>排版缓存：键为 <see cref="TextLayoutRequest"/>（record 值相等；文本、字体、宽度、换行、省略、对齐、行高、DPI、字距、<see cref="TextLayoutRequest.Editing"/> 都在键里），
/// 容量由 <see cref="LayoutCacheCapacity"/> 控制。</item>
/// <item>栅格缓存：键为 <see cref="TextLayoutResult"/> 实例引用 + 文字颜色 RGB，
/// 容量由 <see cref="RasterCacheCapacity"/> 控制。颜色参与键是正确性要求：同一排版在不同前景色下
/// 的 Skia 字形遮罩边缘值不同；alpha 不参与键，它在绘制端统一乘一次。</item>
/// <item>字体缓存：SKTypeface/SKFont/SKShaper 在内部按容量缓存，淘汰或 <see cref="ClearCaches"/> 时释放。</item>
/// </list>
/// </para>
/// <para>
/// 线程约定：与核心一致，只在 UI 线程使用。实例不是线程安全的。
/// </para>
/// </remarks>
public sealed class SkiaTextService : ITextService, IDisposable
{
    /// <summary>排版缓存默认容量。</summary>
    public const int DefaultLayoutCacheCapacity = 128;

    /// <summary>栅格缓存默认容量。</summary>
    public const int DefaultRasterCacheCapacity = 64;

    private const int MaxDiagnostics = 64;

    private readonly LruCache<TextLayoutRequest, TextLayoutResult> _layouts;
    private readonly LruCache<TextRasterKey, PixelBuffer> _rasters;
    private readonly SkiaFontResolver _fonts;
    private readonly TextLayoutBuilder _builder;
    private readonly List<TextDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private int _layoutMisses;
    private int _layoutHits;
    private int _rasterMisses;
    private int _rasterHits;
    private bool _disposed;

    /// <summary>创建文本服务。</summary>
    /// <param name="layoutCacheCapacity">排版缓存容量（至少 1）。</param>
    /// <param name="rasterCacheCapacity">栅格缓存容量（至少 1）。</param>
    public SkiaTextService(int layoutCacheCapacity = DefaultLayoutCacheCapacity, int rasterCacheCapacity = DefaultRasterCacheCapacity)
    {
        _layouts = new LruCache<TextLayoutRequest, TextLayoutResult>(Math.Max(1, layoutCacheCapacity));
        _rasters = new LruCache<TextRasterKey, PixelBuffer>(Math.Max(1, rasterCacheCapacity));
        _fonts = new SkiaFontResolver(ReportDiagnostic);
        _builder = new TextLayoutBuilder(_fonts);
    }

    /// <summary>字体解析或字形回退的诊断记录（按需累积，最多 64 条，去重）。</summary>
    public IReadOnlyList<TextDiagnostic> Diagnostics => _diagnostics;

    /// <summary>新诊断产生时触发；宿主可据此写入日志。</summary>
    public event EventHandler<TextDiagnostic>? DiagnosticReported;

    /// <summary>实际执行排版的次数（缓存命中不计数）。</summary>
    public int LayoutMisses => _layoutMisses;

    /// <summary>排版缓存命中次数。</summary>
    public int LayoutHits => _layoutHits;

    /// <summary>实际执行栅格化的次数（缓存命中不计数）。</summary>
    public int RasterMisses => _rasterMisses;

    /// <summary>栅格缓存命中次数。</summary>
    public int RasterHits => _rasterHits;

    /// <summary>
    /// <see cref="FontFamily.FromFile"/> 指定的文件不存在或无效时：
    /// false（默认）记录诊断并回退到平台默认字体，true 抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public bool ThrowOnMissingFontFile
    {
        get => _fonts.ThrowOnMissingFontFile;
        set => _fonts.ThrowOnMissingFontFile = value;
    }

    /// <inheritdoc />
    public int CachedLayoutCount => _layouts.Count;

    /// <inheritdoc />
    public int CachedRasterCount => _rasters.Count;

    /// <inheritdoc />
    public int LayoutCacheCapacity
    {
        get => _layouts.Capacity;
        set => _layouts.Capacity = Math.Max(1, value);
    }

    /// <summary>栅格缓存容量上限；超出后按最近最少使用淘汰。</summary>
    public int RasterCacheCapacity
    {
        get => _rasters.Capacity;
        set => _rasters.Capacity = Math.Max(1, value);
    }

    /// <inheritdoc />
    public TextLayoutResult Layout(TextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_layouts.TryGet(request, out var cached))
        {
            _layoutHits++;
            return cached;
        }

        _layoutMisses++;
        var result = _builder.Build(request);
        _layouts.GetOrAdd(request, _ => result, out _);
        return result;
    }

    /// <inheritdoc />
    public Size Measure(TextLayoutRequest request) => Layout(request).Size;

    /// <inheritdoc />
    public FontMetrics GetFontMetrics(FontDescription font)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metrics = _fonts.GetFontInstance(font).Font.Metrics;
        // 契约要求返回正数量值：ascent 取绝对值，line gap 不小于 0（不少字体的 line gap 为 0，此时行高即 ascent + descent）。
        return new FontMetrics(Math.Max(0.1, -metrics.Ascent), Math.Max(0.1, metrics.Descent), Math.Max(0, metrics.Leading));
    }

    /// <inheritdoc />
    public PixelBuffer Rasterize(TextLayoutResult layout) => Rasterize(layout, Color.White);

    /// <inheritdoc />
    /// <remarks>
    /// 颜色只使用 RGB，并固定按不透明绘制（alpha=255）：Skia 的 Alpha8 字形遮罩会随绘制颜色
    /// 应用颜色相关的 gamma/对比度处理，只有用实际颜色绘制才能得到与同一路径直接颜色绘制一致的覆盖率；
    /// 若同时传入 paint alpha，遮罩会被 alpha 预先乘一次，绘制端再乘 tint alpha 就成了双重混合。
    /// </remarks>
    public PixelBuffer Rasterize(TextLayoutResult layout, Color color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new TextRasterKey(layout, MaskColor(color));
        if (_rasters.TryGet(key, out var cached))
        {
            _rasterHits++;
            return cached;
        }

        _rasterMisses++;
        var buffer = BuildRaster(layout, color);
        _rasters.GetOrAdd(key, _ => buffer, out _);
        return buffer;
    }

    /// <summary>遮罩颜色键：只保留 RGB。</summary>
    private static Color MaskColor(Color color) => color.WithAlpha(255);

    /// <inheritdoc />
    public void ClearCaches()
    {
        _layouts.Clear();
        _rasters.Clear();
        _fonts.Clear();
    }

    /// <summary>清空诊断记录。</summary>
    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
        _diagnosticKeys.Clear();
    }

    /// <summary>
    /// 返回首选字体解析后的家族名（诊断与测试辅助；不暴露 SKTypeface/SKFont 等后端对象）。
    /// 字体文件无效或家族名不存在并回退时，返回实际使用的字体家族名。
    /// </summary>
    public string? ResolvePrimaryFamilyName(FontDescription font)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fonts.ResolvePrimary(font).FamilyName;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearCaches();
        _fonts.Dispose();
    }

    private PixelBuffer BuildRaster(TextLayoutResult layout, Color color)
    {
        var scale = layout.PixelScale <= 0 ? 1.0 : layout.PixelScale;
        var width = Math.Max(1, (int)Math.Ceiling(layout.Size.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Size.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        var graphemeStarts = Graphemes.Starts(layout.Text);
        var primary = _fonts.ResolvePrimary(layout.Font);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale, (float)scale);
            // Alpha8 只保存覆盖率，但 Skia 的字形遮罩带颜色相关的 gamma/对比度处理：
            // 必须传入实际不透明 RGB，得到的边缘值与同一路径直接颜色绘制一致，绘制端再按同一 tint 着色。
            // alpha 固定 255：透明度由绘制端 tint 统一乘一次，避免双重混合。
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, 255),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            foreach (var line in layout.Lines)
            {
                DrawLine(canvas, paint, layout, line, graphemeStarts, primary);
            }
        }

        var buffer = new PixelBuffer(width, height, PixelFormat.Alpha8);
        var span = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            span.Slice(y * rowBytes, width).CopyTo(buffer.Pixels.AsSpan(y * width, width));
        }

        return buffer;
    }

    private void DrawLine(SKCanvas canvas, SKPaint paint, TextLayoutResult layout, TextLineInfo line, int[] graphemeStarts, SKTypeface primary)
    {
        var text = layout.Text;
        var contentEnd = line.End;
        while (contentEnd > line.Start && Graphemes.IsLineBreak(text[contentEnd - 1]))
        {
            contentEnd--;
        }

        if (contentEnd <= line.Start)
        {
            return;
        }

        var spacing = (float)layout.LetterSpacing;
        var advanceX = 0.0;
        var runBaseOrdinal = 0;
        FontInstance? current = null;
        var runStart = line.Start;
        var runEnd = line.Start;

        // 解析字体后立即 shaping 并绘制：字体实例始终是最近使用项，不会被有界缓存淘汰。
        void DrawCurrent()
        {
            if (current is null || runEnd <= runStart)
            {
                return;
            }

            var glyphs = Shaping.Shape(current, text, runStart, runEnd - runStart);
            if (glyphs.Count > 0)
            {
                var ids = new ushort[glyphs.Count];
                var points = new SKPoint[glyphs.Count];

                // 字形位置与 TextLayoutBuilder 的 xAt 同源：推进量之和 + 每个字素簇的字距。
                // 只累计 advance 会让字距只影响测量宽度而不影响实际绘制。
                var ordinal = runBaseOrdinal;
                var cursor = Graphemes.BoundaryAtOrBefore(graphemeStarts, runStart);
                for (var i = 0; i < glyphs.Count; i++)
                {
                    while (cursor + 1 < graphemeStarts.Length && graphemeStarts[cursor + 1] <= glyphs[i].Cluster)
                    {
                        cursor++;
                        ordinal++;
                    }

                    ids[i] = glyphs[i].Glyph;
                    points[i] = new SKPoint(
                        (float)(advanceX + (spacing * ordinal) + glyphs[i].OffsetX),
                        (float)glyphs[i].OffsetY);
                    advanceX += glyphs[i].Advance;
                }

                using var builder = new SKTextBlobBuilder();
                builder.AddPositionedRun(ids, current.Font, points);
                using var blob = builder.Build();
                if (blob is not null)
                {
                    canvas.DrawText(blob, (float)line.Bounds.X, (float)line.Baseline, paint);
                }
            }
        }

        var index = Graphemes.BoundaryAtOrBefore(graphemeStarts, line.Start);
        var clusterOrdinal = 0;
        for (; index + 1 < graphemeStarts.Length; index++)
        {
            var clusterStart = graphemeStarts[index];
            if (clusterStart >= contentEnd)
            {
                break;
            }

            var clusterEnd = Math.Min(graphemeStarts[index + 1], contentEnd);
            var instance = _fonts.GetFontInstanceForCharacter(primary, layout.Font, Graphemes.CodepointAt(text, clusterStart));
            if (current is null)
            {
                runStart = clusterStart;
                runBaseOrdinal = clusterOrdinal;
            }
            else if (!ReferenceEquals(current, instance))
            {
                DrawCurrent();
                runStart = clusterStart;
                runBaseOrdinal = clusterOrdinal;
            }

            current = instance;
            runEnd = clusterEnd;
            clusterOrdinal++;
        }

        DrawCurrent();
    }

    private void ReportDiagnostic(TextDiagnostic diagnostic)
    {
        if (!_diagnosticKeys.Add(diagnostic.ToString()))
        {
            return;
        }

        if (_diagnostics.Count >= MaxDiagnostics)
        {
            _diagnosticKeys.Remove(_diagnostics[0].ToString());
            _diagnostics.RemoveAt(0);
        }

        _diagnostics.Add(diagnostic);
        DiagnosticReported?.Invoke(this, diagnostic);
    }

    /// <summary>
    /// 栅格缓存键：同一排版结果 + 参与遮罩生成的颜色。
    /// 排版结果按实例引用比较（<see cref="Layout"/> 会复用缓存实例）；颜色只含 RGB，alpha 已被归一化为 255。
    /// </summary>
    private readonly record struct TextRasterKey(TextLayoutResult Layout, Color Color);
}
