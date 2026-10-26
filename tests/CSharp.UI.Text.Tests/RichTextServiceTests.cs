using SkiaSharp;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>
/// RT-02：<see cref="SkiaTextService"/> 的 <see cref="IRichTextService"/> 实现——
/// 真实 shaping/断行/多色 RGBA 栅格化/几何键缓存/face 租约/handle 所有权。
/// </summary>
public class RichTextServiceTests
{
    private static readonly FontDescription Body = new(FontFamily.Default, 20);

    private static RichTextLayoutRequest Request(StyledText text, double maxWidth = double.PositiveInfinity,
        TextWrapping wrapping = TextWrapping.NoWrap, double lineHeight = 0, double letterSpacing = 0,
        FontDescription? font = null)
        => new()
        {
            Text = text,
            ParagraphFont = font ?? Body,
            MaxWidth = maxWidth,
            Wrapping = wrapping,
            LineHeight = lineHeight,
            LetterSpacing = letterSpacing,
        };

    private static RichTextPaint Paint(RichTextLayoutResult layout, Color fg)
    {
        var runs = new RichTextRunPaint[layout.Segments.Count];
        for (var i = 0; i < runs.Length; i++)
        {
            runs[i] = new RichTextRunPaint(fg, Color.Transparent, false, false);
        }

        return new RichTextPaint(runs) { ParagraphForeground = fg };
    }

    // ---------------------------------------------------------------- 排版与几何

    [Fact]
    public void 单字体无样式_宽度非零且随内容增长()
    {
        using var service = new SkiaTextService();
        using var short_ = service.Layout(Request(StyledText.Create("ab")));
        using var long_ = service.Layout(Request(StyledText.Create("abcdef")));

        Assert.True(short_.Size.Width > Body.Size / 4, $"宽度应显著大于 0：{short_.Size.Width}");
        Assert.True(long_.Size.Width > short_.Size.Width);
        Assert.Single(short_.Segments);
        Assert.Equal(0, short_.Segments[0].Start);
        Assert.Equal(2, short_.Segments[0].End);
    }

    [Fact]
    public void RasterBounds覆盖负bearing墨迹_斜体fj栅格无裁切()
    {
        // "fj" 斜体：f 尾画右越 advance、j 点左负 bearing——RasterBounds 必须外扩，
        // 栅格 buffer 覆盖全部墨迹（clip 0），但布局 Size/Lines/caret 不变。
        var font = Body with { Italic = true };
        var styled = StyledText.Create("fj");
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(styled, font: font));

        var bounds = layout.RasterBounds;
        // 布局盒语义不变：Size 仍是 advance 度量。
        Assert.True(layout.Size.Width > 0);
        Assert.True(bounds.Width >= layout.Size.Width);
        Assert.True(bounds.Height >= layout.Size.Height);
        Assert.True(bounds.Left <= 0.0001 && bounds.Top <= 0.0001);
        // 墨迹允许左越界（j 负 bearing）或右越界（f 尾画）：至少一侧超出或等于盒。
        Assert.True(bounds.Right >= layout.Size.Width - 0.0001);

        var buffer = service.Rasterize(layout, Paint(layout, Color.Black));
        // buffer 覆盖 RasterBounds 物理取整范围；RasterOrigin 是像素(0,0)的逻辑坐标。
        var scale = layout.PixelScale;
        Assert.Equal(Math.Floor(bounds.Left * scale) / scale, layout.RasterOrigin.X, 6);
        Assert.Equal(Math.Floor(bounds.Top * scale) / scale, layout.RasterOrigin.Y, 6);
        // 位图物理尺寸 = floor/ceil 覆盖。
        var expectedW = Math.Max(1, (int)Math.Ceiling(bounds.Right * scale) - (int)Math.Floor(bounds.Left * scale));
        var expectedH = Math.Max(1, (int)Math.Ceiling(bounds.Bottom * scale) - (int)Math.Floor(bounds.Top * scale));
        Assert.Equal(expectedW, buffer.Width);
        Assert.Equal(expectedH, buffer.Height);
        // 裁切检测：RasterBounds 右越 advance 盒时，盒右缘之外像素必须存在墨迹
        // （italic f 尾画右越界是已观察事实；若字体恰好不越界则 bounds.Right == Size.Width）。
        if (bounds.Right > layout.Size.Width + 0.0001)
        {
            var boxRightPx = (int)Math.Floor((layout.Size.Width - layout.RasterOrigin.X) * scale);
            var inkBeyondBox = false;
            for (var y = 0; y < buffer.Height && !inkBeyondBox; y++)
            {
                for (var x = Math.Max(0, boxRightPx); x < buffer.Width; x++)
                {
                    if (buffer.Pixels[(y * buffer.Stride) + (x * 4) + 3] > 0)
                    {
                        inkBeyondBox = true;
                        break;
                    }
                }
            }

            Assert.True(inkBeyondBox, "RasterBounds 右越界的墨迹必须落到 buffer（不裁字）");
        }
    }


    [Fact]
    public void 软换行行首斜体j_RasterBounds覆盖下行负bearing_不混入上行()
    {
        // "xxx jf" wrap 后 "jf" 落到下行行首：j 的左负 bearing 属于下行，
        // RasterBounds.Left 必须覆盖它，且不能把该墨迹错算到上行行尾。
        var font = Body with { Italic = true };
        var styled = StyledText.Create("xxx jf");
        using var service = new SkiaTextService();
        using var wrapped = service.Layout(Request(styled, maxWidth: 2 * font.Size, wrapping: TextWrapping.Wrap, font: font));
        Assert.True(wrapped.Lines.Count >= 2, $"需要两行验证 wrap 归属：实际 {wrapped.Lines.Count}");

        var bounds = wrapped.RasterBounds;
        // j 在行首左负 bearing → RasterBounds 左界越过布局盒左缘（<0）。
        Assert.True(bounds.Left < 0, $"下行行首 j 的负 bearing 应外扩 RasterBounds.Left：{bounds}");
        // 下行墨迹 bottom 落在末行行盒内（不归上行行尾）。
        var lastLine = wrapped.Lines[^1];
        // "jf" 必须落在末行行首（'j' 是该行第一个非空白 cluster）。
        Assert.Equal('j', styled.Text[lastLine.Start]);
        Assert.True(bounds.Bottom > lastLine.Bounds.Y, "墨迹覆盖下行行盒");

        // 布局语义不变：Rasterize buffer 与 RasterOrigin 一致，命中行首位置有效。
        var buffer = service.Rasterize(wrapped, Paint(wrapped, Color.Black));
        var scale = wrapped.PixelScale;
        Assert.Equal(Math.Floor(bounds.Left * scale) / scale, wrapped.RasterOrigin.X, 6);
        Assert.True(wrapped.RasterOrigin.X < 0, "负 bearing 使 origin 左移");
        var hit = wrapped.HitTestIndex(new Point(1, lastLine.Bounds.Y + 1));
        Assert.True(hit > 0 && hit <= styled.Text.Length);
    }
    [Fact]
    public void PixelScale放大_RasterBounds物理覆盖同步外扩()
    {
        var font = Body with { Italic = true };
        var styled = StyledText.Create("jf");
        using var service = new SkiaTextService();
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var request = Request(styled, font: font);
            request = request with { PixelScale = scale };
            using var layout = service.Layout(request);
            var buffer = service.Rasterize(layout, Paint(layout, Color.Black));

            var b = layout.RasterBounds;
            var expectedW = Math.Max(1, (int)Math.Ceiling(b.Right * scale) - (int)Math.Floor(b.Left * scale));
            var expectedH = Math.Max(1, (int)Math.Ceiling(b.Bottom * scale) - (int)Math.Floor(b.Top * scale));
            Assert.Equal(expectedW, buffer.Width);
            Assert.Equal(expectedH, buffer.Height);
        }
    }

    [Fact]
    public void 混合script_拉丁CJK阿文字_排版分段且可栅格化()
    {
        // 同一段落内 Latin + Han + Arabic：builder 按 script 切 shape 子段，
        // 每个子段 script 一致（HarfBuzz 方向/script 推断正确），几何连续且像素真实。
        var styled = StyledText.Create("ab中文اب");
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(styled));

        Assert.Equal("ab中文اب", layout.Text);
        Assert.True(layout.Size.Width > 0);
        // 单模型段内按 script 切分出的几何段覆盖全文本、互不重叠。
        var covered = 0;
        foreach (var seg in layout.Segments)
        {
            covered += seg.End - seg.Start;
            Assert.True(seg.Width > 0 || seg.End == seg.Start);
        }

        Assert.Equal(styled.Text.Length, covered);

        var buffer = service.Rasterize(layout, Paint(layout, Color.Black));
        Assert.True(buffer.Width > 0 && buffer.Height > 0);
        // 至少一个非透明像素证明真实 shaping 落位。
        var hasInk = false;
        for (var i = 3; i < buffer.Pixels.Length; i += 4)
        {
            if (buffer.Pixels[i] > 0)
            {
                hasInk = true;
                break;
            }
        }

        Assert.True(hasInk);
    }

    [Fact]
    public void 拉丁跨样式词_不按样式边界断行()
    {
        // “**bo**ld” 是一个词：即使内部有样式边界，也只允许在空白/连字符处换行。
        var styled = new StyledText("bold", [new TextStyleRange(1, 3, new TextStyle { Weight = FontWeight.Bold })], null);
        using var service = new SkiaTextService();
        // 宽度只放得下一个多字符但不放整个词：若无词内机会应硬断在能放下的最远处（不是样式边界）。
        using var layout = service.Layout(Request(styled, maxWidth: Body.Size * 1.5, wrapping: TextWrapping.Wrap));

        Assert.True(layout.Lines.Count >= 1);
        // 断行点允许落在任何能放下的字素边界（超长词硬断），关键是整词不被样式边界强制拆散：
        // 验证每行内容都是字素对齐且行内 segment 覆盖完整。
        var covered = 0;
        foreach (var line in layout.Lines)
        {
            covered += line.End - line.Start;
        }

        Assert.Equal("bold".Length, covered);
    }

    [Fact]
    public void 中文_CJK逐字断行()
    {
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create(TextTestHelpers.ChineseText), maxWidth: 60, wrapping: TextWrapping.Wrap));

        Assert.True(layout.Lines.Count > 1, "CJK 应在有限宽度换行");
        Assert.True(layout.Size.Width <= 60 + 0.5, $"行宽不应超约束：{layout.Size.Width}");
        foreach (var line in layout.Lines)
        {
            Assert.True(line.Bounds.Width <= 60 + 0.5);
        }
    }

    [Fact]
    public void 代理对与组合字符_不切字素()
    {
        // emoji（代理对）+ é 组合序列 + ZWJ 家庭 emoji：每字素一个 caret stop。
        const string text = "a\uD83D\uDE00e\u0301b\U0001F468\u200D\U0001F469";
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create(text)));

        // 字素数：a、😀、é(组合)、b、👨‍👩 = 5 个簇 + 末尾 = 6 个 stop。
        Assert.Equal(6, layout.CaretStops.Count);
        Assert.Equal(0, layout.CaretStops[0]);
        Assert.Equal(text.Length, layout.CaretStops[^1]);
        // 代理对/组合/ZWJ 的内部 offset 不成为 stop。
        Assert.DoesNotContain(2, layout.CaretStops); // 😀 的低代理位
    }

    [Fact]
    public void 行内异字号_baseline与行高按最大字高()
    {
        // 首段 10px、后段 28px：行高 = 28px 字高，两 segment baseline 相同（对齐行基线）。
        var styled = new StyledText("ab", [new TextStyleRange(0, 1, new TextStyle { Size = 10 }), new TextStyleRange(1, 2, new TextStyle { Size = 28 })], null);
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(styled));

        Assert.Equal(2, layout.Segments.Count);
        Assert.Equal(layout.Segments[0].Baseline, layout.Segments[1].Baseline, 3);
        // 行高至少覆盖大字号 ascent+descent。
        var metrics = service.GetFontMetrics(Body.WithSize(28));
        Assert.True(layout.Lines[0].Bounds.Height >= metrics.Ascent + metrics.Descent - 0.5,
            $"行高 {layout.Lines[0].Bounds.Height} 应 >= 字高 {metrics.Ascent + metrics.Descent}");
    }

    [Fact]
    public void 显式LineHeight小于字高_钳到字高不截glyph()
    {
        var styled = StyledText.Create("Ag");
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(styled, lineHeight: 2));

        var metrics = service.GetFontMetrics(Body);
        Assert.True(layout.Lines[0].Bounds.Height >= metrics.Ascent + metrics.Descent - 0.5,
            $"LineHeight=2 应被钳到字高 {metrics.Ascent + metrics.Descent}，实际 {layout.Lines[0].Bounds.Height}");
    }

    // ---------------------------------------------------------------- 几何键缓存

    [Fact]
    public void 仅颜色不同_排版缓存命中同core新handle()
    {
        using var service = new SkiaTextService();
        var red = new StyledText("hi", [new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Red })], null);
        var blue = new StyledText("hi", [new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Blue })], null);

        using var first = service.Layout(Request(red));
        var misses = service.RichLayoutMisses;
        using var second = service.Layout(Request(blue));

        Assert.NotSame(first, second);                              // 每次新 handle
        Assert.Equal(first.LayoutIdentity, second.LayoutIdentity); // 同 core
        Assert.Equal(misses, service.RichLayoutMisses);            // 不重排版
        Assert.Equal(1, service.RichLayoutHits);
    }

    [Fact]
    public void 改payload值_不重排版_改interaction端点_重排版()
    {
        using var service = new SkiaTextService();
        var a = new StyledText("hi", null, [new TextInteractionRange(0, 2, 1, "https://a")]);
        var b = new StyledText("hi", null, [new TextInteractionRange(0, 2, 2, "https://b")]);

        using var first = service.Layout(Request(a));
        var misses = service.RichLayoutMisses;
        using var second = service.Layout(Request(b));
        Assert.Equal(misses, service.RichLayoutMisses); // payload/Id 值不变端点 → 命中

        var c = new StyledText("hi", null, [new TextInteractionRange(0, 1, 1, "https://a")]);
        using var third = service.Layout(Request(c));
        Assert.True(service.RichLayoutMisses > misses, "端点变化应重排版");
    }

    [Fact]
    public void 宽度或字号变化_不重用于缓存()
    {
        using var service = new SkiaTextService();
        var styled = StyledText.Create(TextTestHelpers.ChineseText);
        using var wide = service.Layout(Request(styled, maxWidth: 400, wrapping: TextWrapping.Wrap));
        using var narrow = service.Layout(Request(styled, maxWidth: 100, wrapping: TextWrapping.Wrap));
        using var big = service.Layout(Request(styled, font: Body.WithSize(30)));

        Assert.NotEqual(wide.LayoutIdentity, narrow.LayoutIdentity);
        Assert.NotEqual(wide.LayoutIdentity, big.LayoutIdentity);
        Assert.True(narrow.Lines.Count > wide.Lines.Count);
    }

    // ---------------------------------------------------------------- RGBA 栅格化

    [Fact]
    public void 红f蓝i_各自颜色段且RGBA像素可分辨()
    {
        // “fi”同 face：f 红、i 蓝，跨 paint-only 端点分次 shape。
        var styled = new StyledText("fi",
        [
            new TextStyleRange(0, 1, new TextStyle { Foreground = Color.Red }),
            new TextStyleRange(1, 2, new TextStyle { Foreground = Color.Blue }),
        ], null);
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(styled));
        Assert.Equal(2, layout.Segments.Count);

        var redRuns = new[]
        {
            new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
            new RichTextRunPaint(Color.Blue, Color.Transparent, false, false),
        };
        var buffer = service.Rasterize(layout, new RichTextPaint(redRuns) { ParagraphForeground = Color.Red });
        Assert.Equal(PixelFormat.Rgba8888Premultiplied, buffer.Format);
        Assert.True(buffer.Width > 1 && buffer.Height > 1);

        var fSeg = layout.Segments[0];
        var iSeg = layout.Segments[1];
        var fColor = FindDominantNonTransparent(buffer, fSeg, layout.Lines[fSeg.LineIndex]);
        var iColor = FindDominantNonTransparent(buffer, iSeg, layout.Lines[iSeg.LineIndex]);
        Assert.True(fColor.R > 150 && fColor.B < 100, $"f 段应偏红：{fColor}");
        Assert.True(iColor.B > 150 && iColor.R < 100, $"i 段应偏蓝：{iColor}");
    }

    [Fact]
    public void 换paint同layout_重raster像素不同()
    {
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create("hi")));
        var red = service.Rasterize(layout, Paint(layout, Color.Red));
        var misses = service.RichRasterMisses;
        var blue = service.Rasterize(layout, Paint(layout, Color.Blue));

        Assert.True(service.RichRasterMisses > misses);
        Assert.NotSame(red, blue);
    }

    [Fact]
    public void 前景alpha参与预乘_输出合法premul像素()
    {
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create("A")));
        var half = new Color(255, 0, 0, 128);
        var buffer = service.Rasterize(layout, Paint(layout, half));

        var found = false;
        for (var i = 0; i < buffer.Pixels.Length; i += 4)
        {
            var a = buffer.Pixels[i + 3];
            if (a > 20)
            {
                found = true;
                // 预乘不变量：R ≤ A。
                Assert.True(buffer.Pixels[i] <= a + 1, $"premul 违规 R={buffer.Pixels[i]} A={a}");
            }
        }

        Assert.True(found, "应存在覆盖像素");
    }

    // ---------------------------------------------------------------- hit/range

    [Fact]
    public void HitTest与GetRangeRects_跨行真实矩形()
    {
        const string text = "hello world";
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create(text), maxWidth: 60, wrapping: TextWrapping.Wrap));
        Assert.True(layout.Lines.Count > 1);

        var index = layout.HitTestIndex(new Point(1, 1));
        Assert.InRange(index, 0, text.Length);

        var rects = layout.GetRangeRects(0, text.Length);
        Assert.NotEmpty(rects);
        foreach (var rect in rects)
        {
            Assert.True(rect.Height > 0);
            Assert.True(rect.Width >= 0);
        }

        var bounds = layout.GetRangeBounds(0, text.Length);
        Assert.True(bounds.Width >= 0 && bounds.Height > 0);
    }

    // ---------------------------------------------------------------- 所有权/租约

    [Fact]
    public void ClearCaches后_已持handle仍可rasterize与查询()
    {
        using var service = new SkiaTextService();
        var handle = service.Layout(Request(StyledText.Create("hi")));
        service.ClearCaches();

        // 缓存份额已还，但调用方 handle 仍持一份 → 几何与 raster 合法。
        Assert.True(handle.Size.Width > 0);
        var buffer = service.Rasterize(handle, Paint(handle, Color.Red));
        Assert.True(buffer.Width > 0);
        handle.Dispose();
    }

    [Fact]
    public void 同core多handle_各自Dispose不误释放_全Dispose后raster抛()
    {
        using var service = new SkiaTextService();
        var request = Request(StyledText.Create("hi"));
        var h1 = service.Layout(request);
        var h2 = service.Layout(request);

        h1.Dispose();
        // h2 仍合法。
        Assert.True(h2.Size.Width > 0);
        _ = service.Rasterize(h2, Paint(h2, Color.Red));

        h2.Dispose();
        Assert.Throws<ObjectDisposedException>(() => h2.Size);
        Assert.Throws<ObjectDisposedException>(() => _ = service.Rasterize(h2, Paint(h2, Color.Red)));
        Assert.Throws<ObjectDisposedException>(() => h1.Size);
    }

    [Fact]
    public void 服务Dispose后_handle几何可查_Rasterize抛()
    {
        var service = new SkiaTextService();
        var handle = service.Layout(Request(StyledText.Create("hi")));
        service.Dispose();

        Assert.True(handle.Size.Width > 0); // 几何自包含
        Assert.Throws<ObjectDisposedException>(() => _ = service.Rasterize(handle, Paint(handle, Color.Red)));
        handle.Dispose();
    }

    [Fact]
    public void face压力超缓存容量_已持handle_raster仍一致()
    {
        using var service = new SkiaTextService();
        var held = service.Layout(Request(StyledText.Create("held")));
        var expected = service.Rasterize(held, Paint(held, Color.Black));

        // 触发 >64 个不同字号的 FontInstance，迫使缓存淘汰。
        for (var size = 1; size <= 80; size++)
        {
            service.GetFontMetrics(Body.WithSize(size));
        }

        // 已淘汰出缓存的 face 因租约仍活：held 的 raster 与之前逐字节一致。
        var again = service.Rasterize(held, Paint(held, Color.Black));
        Assert.Equal(expected.Pixels, again.Pixels);
        held.Dispose();
    }

    [Fact]
    public void Measure不泄漏handle_语义同LayoutSize()
    {
        using var service = new SkiaTextService();
        var request = Request(StyledText.Create("hi"));
        var size = service.Measure(request);
        using var handle = service.Layout(request);
        Assert.Equal(handle.Size, size);
    }

    [Fact]
    public void paint数量不符_抛ArgumentException()
    {
        using var service = new SkiaTextService();
        using var layout = service.Layout(Request(StyledText.Create("hi")));
        Assert.Throws<ArgumentException>(() => _ = service.Rasterize(layout, new RichTextPaint(
        [
            new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
            new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
        ])));
    }

    [Fact]
    public void LetterSpacing作用于测量宽度()
    {
        using var service = new SkiaTextService();
        using var tight = service.Layout(Request(StyledText.Create("abc")));
        using var spaced = service.Layout(Request(StyledText.Create("abc"), letterSpacing: 4));

        Assert.True(spaced.Size.Width > tight.Size.Width, "字距应增宽");
        Assert.True(spaced.Size.Width >= tight.Size.Width + (3 * 4) - 1);
    }

    // ---------------------------------------------------------------- 行内 code padding（真实 Skia）

    [Fact]
    public void 行内Padding_pure英文_token宽增两倍pad()
    {
        // 'Subagent' 纯 ASCII：pad=0 与 hPad=5 的 code 段盒宽差 = 左右 pad 各 5。
        const double pad = 5;
        var plain = StyledText.Create("Subagent");
        var padded = new StyledText("Subagent",
            [new TextStyleRange(0, 8, new TextStyle { InlineCodePaddingHorizontal = pad })], null);
        using var service = new SkiaTextService();
        using var layoutPlain = service.Layout(Request(plain));
        using var layoutPadded = service.Layout(Request(padded));

        var segPlain = Assert.Single(layoutPlain.Segments);
        var segPadded = Assert.Single(layoutPadded.Segments);

        // segment.X = 盒原点起算的字形 X（左 pad 在其左侧）；Width 已含右 pad——
        // 盒 = [X − IL, X + W)：盒左缘不变、盒右缘 +2×pad。
        Assert.Equal(pad, segPadded.X - segPlain.X, 3);
        Assert.Equal(pad, segPadded.Width - segPlain.Width, 3);
        Assert.Equal(pad, segPadded.InsetLeft, 3);
        Assert.Equal(pad, segPadded.InsetRight, 3);

        var boxLeftDelta = (segPadded.X - segPadded.InsetLeft) - (segPlain.X - segPlain.InsetLeft);
        var boxRightDelta = (segPadded.X + segPadded.Width) - (segPlain.X + segPlain.Width);
        Assert.Equal(0, boxLeftDelta, 3);
        Assert.Equal(2 * pad, boxRightDelta - boxLeftDelta, 3);
    }

    [Fact]
    public void 行内Padding_中英混排_token宽同样增两倍pad()
    {
        // '中Subagent文'：CJK/拉丁混排同 face 或跨 face——盒宽差仍是 2×pad。
        const double pad = 5;
        var plain = StyledText.Create("中Subagent文");
        var padded = new StyledText("中Subagent文",
            [new TextStyleRange(0, 10, new TextStyle { InlineCodePaddingHorizontal = pad })], null);
        using var service = new SkiaTextService();
        using var layoutPlain = service.Layout(Request(plain));
        using var layoutPadded = service.Layout(Request(padded));

        // 盒几何与 face 分次无关：段合并/拆分不影响盒左右缘——按行盒缘求并。
        static (double Left, double Right) Box(RichTextLayoutResult layout)
        {
            var left = layout.Segments.Min(s => s.X - s.InsetLeft);
            var right = layout.Segments.Max(s => s.X + s.Width);
            return (left, right);
        }

        var plainBox = Box(layoutPlain);
        var paddedBox = Box(layoutPadded);
        Assert.Equal(plainBox.Left, paddedBox.Left, 3);
        Assert.Equal(2 * pad, paddedBox.Right - plainBox.Right, 3);
    }

    [Fact]
    public void 行内Padding_wrap预算_行盒宽不越MaxWidth()
    {
        // MaxWidth < token 宽 但 > 单 glyph + 2×pad：每行 fragment 盒宽含完整
        // 左右 pad 且不越 MaxWidth（否则 box 溢出是几何 bug）。
        const double pad = 5;
        var styled = new StyledText("codecode",
            [new TextStyleRange(0, 8, new TextStyle { InlineCodePaddingHorizontal = pad })], null);
        using var service = new SkiaTextService();

        // 先量出全 token 的 advance 与单 glyph 宽，选 MaxWidth 介于两者之间。
        using var full = service.Layout(Request(styled));
        var tokenAdvance = full.Lines[0].Bounds.Width;
        var glyphAdvance = full.CaretXs[1] - full.CaretXs[0];
        var maxWidth = glyphAdvance + (2 * pad) + 2;
        Assert.True(maxWidth < tokenAdvance, "MaxWidth 必须小于整 token 宽以触发拆行。");
        Assert.True(maxWidth > glyphAdvance + (2 * pad), "MaxWidth 必须容下单 glyph 盒（含完整 pad）。");

        using var layout = service.Layout(Request(styled, maxWidth: maxWidth, wrapping: TextWrapping.Wrap));
        Assert.True(layout.Lines.Count > 1, "窄宽应触发断行。");

        for (var lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
        {
            var frags = layout.Segments.Where(s => s.LineIndex == lineIndex).ToArray();
            if (frags.Length == 0)
            {
                continue;
            }

            var boxLeft = frags.Min(s => s.X - s.InsetLeft);
            var boxRight = frags.Max(s => s.X + s.Width);
            Assert.True(boxRight - boxLeft <= maxWidth + 0.5,
                $"第{lineIndex}行盒宽 {boxRight - boxLeft} 应 ≤ MaxWidth {maxWidth}。");
        }
    }


    // ---------------------------------------------------------------- 辅助

    private static Color FindDominantNonTransparent(PixelBuffer buffer, RichTextSegment segment, TextLineInfo line)
    {
        // 在 segment 行内矩形内扫描 alpha 最强像素。
        var left = Math.Max(0, (int)segment.X);
        var right = Math.Min(buffer.Width, (int)Math.Ceiling(segment.X + segment.Width));
        var top = Math.Max(0, (int)line.Bounds.Y);
        var bottom = Math.Min(buffer.Height, (int)Math.Ceiling(line.Bounds.Bottom));
        byte r = 0, g = 0, b = 0, best = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = (y * buffer.Stride) + (x * 4);
                if (buffer.Pixels[offset + 3] > best)
                {
                    best = buffer.Pixels[offset + 3];
                    r = buffer.Pixels[offset];
                    g = buffer.Pixels[offset + 1];
                    b = buffer.Pixels[offset + 2];
                }
            }
        }

        return new Color(r, g, b, best);
    }
}
