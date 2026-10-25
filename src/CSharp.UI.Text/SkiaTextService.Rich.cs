using SkiaSharp;

namespace CSharp.UI.Skia;

public sealed partial class SkiaTextService : IRichTextService
{
    private readonly LruCache<RichTextLayoutRequest, RichLayoutCore> _richLayouts = CreateRichLayoutCache();
    private readonly LruCache<RichRasterKey, PixelBuffer> _richRasters = new(DefaultRasterCacheCapacity);

    /// <summary>富文本排版缓存：淘汰/清空时只归还缓存持有的那份 core 引用（调用方句柄不受影响）。</summary>
    private static LruCache<RichTextLayoutRequest, RichLayoutCore> CreateRichLayoutCache()
    {
        var cache = new LruCache<RichTextLayoutRequest, RichLayoutCore>(DefaultLayoutCacheCapacity);
        cache.OnEvicted = (_, core) => core.Release();
        return cache;
    }
    private int _richLayoutMisses;
    private int _richLayoutHits;
    private int _richRasterMisses;
    private int _richRasterHits;

    /// <summary>富文本实际排版次数（缓存命中不计数）。</summary>
    public int RichLayoutMisses => _richLayoutMisses;

    /// <summary>富文本排版缓存命中次数。</summary>
    public int RichLayoutHits => _richLayoutHits;

    /// <summary>富文本实际栅格化次数。</summary>
    public int RichRasterMisses => _richRasterMisses;

    /// <summary>富文本栅格缓存命中次数。</summary>
    public int RichRasterHits => _richRasterHits;

    /// <summary>
    /// 富文本排版缓存容量；与 plain <see cref="ITextService.LayoutCacheCapacity"/> 共用同一属性——
    /// 修改会同时应用到两个缓存（语义即“本服务排版缓存容量上限”）。
    /// </summary>
    int IRichTextService.LayoutCacheCapacity
    {
        get => LayoutCacheCapacity;
        set => LayoutCacheCapacity = value;
    }

    /// <summary>当前缓存的富文本排版结果数量（共享 core 数）。</summary>
    int IRichTextService.CachedLayoutCount => _richLayouts.Count;

    /// <summary>当前缓存的富文本栅格化结果数量。</summary>
    int IRichTextService.CachedRasterCount => _richRasters.Count;

    /// <summary>
    /// 排版富文本。相同 GeometryKey 命中缓存：共享 core 加一引用并返回新的调用方句柄；
    /// 未命中：排版一次，缓存持 1 份 + 句柄持 1 份。每次调用返回独立的新句柄对象。
    /// </summary>
    public RichTextLayoutResult Layout(RichTextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_richLayouts.TryGet(request, out var cached))
        {
            _richLayoutHits++;
            cached.AddRef();
            return new RichTextLayoutResult(cached);
        }

        _richLayoutMisses++;
        var core = _richBuilder.Build(request);
        try
        {
            // 缓存持 1 份。
            core.AddRef();
            _richLayouts.GetOrAdd(request, _ => core, out _);
            return new RichTextLayoutResult(core);
        }
        catch
        {
            // 极端路径（GetOrAdd 不得抛，防御）：归还缓存份额后把调用方份额也还给 core。
            core.Release();
            core.Release();
            throw;
        }
    }

    /// <summary>测量：等价于 Layout 后读取 Size，并归还调用方份额（缓存份额保留）。</summary>
    public Size Measure(RichTextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = Layout(request);
        try
        {
            return handle.Size;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// 按 <paramref name="paint"/> 栅格化：直接用排版期存储的 glyph id/偏移经
    /// <see cref="SKTextBlob"/> 绘到预乘 RGBA 表面，不重新 shape。
    /// 前景色按真实 alpha 预乘（不强制不透明）；背景/装饰字段进缓存键，装饰像素由
    /// 视图层用矩形命令绘制（不在此处烘入位图，避免双层绘制）。
    /// </summary>
    public PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(paint);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // LayoutIdentity/Size/Segments 在已 Dispose 句柄上访问会抛 ObjectDisposedException——
        // 先取一次强制检查句柄合法性。
        var identity = layout.LayoutIdentity;
        if (paint.Runs.Count != layout.Segments.Count)
        {
            throw new ArgumentException(
                $"paint.Runs 数量（{paint.Runs.Count}）必须与 layout.Segments（{layout.Segments.Count}）一一对应。",
                nameof(paint));
        }

        var key = new RichRasterKey(identity, paint);
        if (_richRasters.TryGet(key, out var cached))
        {
            _richRasterHits++;
            return cached;
        }

        _richRasterMisses++;
        var buffer = BuildRichRaster(layout, paint);
        _richRasters.GetOrAdd(key, _ => buffer, out _);
        return buffer;
    }

    /// <summary>清空富文本缓存（plain 部分由 <see cref="ClearCaches"/> 统一负责）。</summary>
    private void ClearRichCaches()
    {
        _richLayouts.Clear();
        _richRasters.Clear();
    }

    private PixelBuffer BuildRichRaster(RichTextLayoutResult layout, RichTextPaint paint)
    {
        var scale = layout.PixelScale <= 0 ? 1.0 : layout.PixelScale;
        // buffer 覆盖 RasterBounds 在物理像素网上向外取整的范围（左/上 floor、右/下 ceil），
        // 保证负 bearing/斜体墨迹不被裁；位图 (0,0) 对应 layout.RasterOrigin。
        var bounds = layout.RasterBounds;
        var pxLeft = (int)Math.Floor(bounds.Left * scale);
        var pxTop = (int)Math.Floor(bounds.Top * scale);
        var pxRight = (int)Math.Ceiling(bounds.Right * scale);
        var pxBottom = (int)Math.Ceiling(bounds.Bottom * scale);
        var width = Math.Max(1, pxRight - pxLeft);
        var height = Math.Max(1, pxBottom - pxTop);
        var originX = pxLeft / scale;
        var originY = pxTop / scale;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale, (float)scale);
            canvas.Translate((float)-originX, (float)-originY);
            var backend = (RichTextBackend)layout.CoreBackend;
            var segmentIndex = 0;
            foreach (var segment in layout.Segments)
            {
                var foreground = paint.Runs[segmentIndex].Foreground;
                segmentIndex++;
                var line = layout.Lines[segment.LineIndex];
                // glyph Points 是段落绝对 X（相对段落内容起点）；该行段落内容起点的画布 X =
                // 行 OffsetX - xAt[line.Start]（xAt 段落绝对）。
                var paragraphOriginX = (float)(line.Bounds.X - backend.XAt[line.Start]);
                for (var r = 0; r < backend.GlyphRuns.Count; r++)
                {
                    var run = backend.GlyphRuns[r];
                    if (run.End <= segment.Start)
                    {
                        continue;
                    }

                    if (run.Start >= segment.End)
                    {
                        break;
                    }

                    DrawGlyphRun(canvas, run, segment, paragraphOriginX, (float)segment.Baseline, foreground);
                }
            }
        }

        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        var span = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            span.Slice(y * rowBytes, width * 4).CopyTo(buffer.Pixels.AsSpan(y * width * 4, width * 4));
        }

        return buffer;
    }

    /// <summary>
    /// 把一个 face run 中落在 segment 区间内的 glyph 按真实前景色直绘。
    /// 按 glyph 的 cluster（绝对 UTF-16 偏移）取 [segment.Start, segment.End) 覆盖的子序列——
    /// 直接用 <see cref="ReadOnlySpan{T}"/> 切片进 text blob，不复制数组。glyph Points 为段落
    /// 绝对 X（相对段落内容起点）；<paramref name="paragraphOriginX"/> 是该行段落内容起点在画布上的
    /// X（blob 原点），glyph 最终画布 X = paragraphOriginX + Points.x。
    /// </summary>
    private static void DrawGlyphRun(SKCanvas canvas, GlyphRun run, RichTextSegment segment, float paragraphOriginX, float baseline, Color color)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < run.Clusters.Length; i++)
        {
            if (run.Clusters[i] >= segment.Start && run.Clusters[i] < segment.End)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0)
        {
            return;
        }

        var count = last - first + 1;
        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(run.GlyphIds.AsSpan(first, count), run.Instance.Font, run.Points.AsSpan(first, count));
        using var blob = builder.Build();
        if (blob is not null)
        {
            canvas.DrawText(blob, paragraphOriginX, baseline, paint);
        }
    }

    /// <summary>富文本栅格缓存键：core 稳定身份 + paint 值相等。</summary>
    private readonly record struct RichRasterKey(RichLayoutIdentity Identity, RichTextPaint Paint);
}
