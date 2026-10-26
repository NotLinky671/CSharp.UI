using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.IntegrationTests;

/// <summary>
/// 行内 code padding 的真实绘制几何：同一逻辑 `TextStyleRange` 盒内 face/script
/// 子段（CJK→Latin→CJK fallback 切段）背景合并为一个无缝 pill——不 pin 具体
/// fallback 段数（随环境字体而异），只断言可见命令层结果。
/// </summary>
public class InlineCodePillTests
{
    private static UiRuntime CreateRuntime(View root, Size viewport, double pixelScale = 1.0)
    {
        var text = new SkiaTextService();
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = text,
            RichTextService = text,
            TextureService = new RecordingTextureService(),
            ImageService = null,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });

        runtime.SetViewport(viewport, pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    [Fact]
    public void 同一逻辑code盒_CJK拉丁混排_背景只有一个无缝pill()
    {
        // '中Subagent文' 同一段 code style range：fallback 切成多个 canonical segment，
        // 但背景必须合成一个覆盖整体盒的 pill——中间不许有圆角裂缝/接缝矩形。
        var codeBg = new Color(128, 128, 128, 60);
        const double pad = 5;
        // 'x中Subagent文y'：code range [1,11) = 中(1) + Subagent(2..9) + 文(10)——
        // CJK/Latin/CJK 保证 fallback 切段，覆盖整盒的 pill 才是真合并。
        var styled = new StyledText(
            "x中Subagent文y",
            [new TextStyleRange(1, 11, new TextStyle
            {
                Background = codeBg,
                CornerRadius = 4,
                InlineCodePaddingHorizontal = pad,
                InlineCodePaddingVertical = 2,
            })],
            null);

        using var runtime = CreateRuntime(new RichText(styled), new Size(400, 120));
        var commands = runtime.BuildFrame().Commands;
        var pill = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == codeBg);

        var node = runtime.Root.Require<RichText>();
        var layout = ((RichText.RichTextViewCache)node.ViewCache!).Layout!;
        var codeSegs = layout.Segments.Where(s => s.Start >= 1 && s.Start < 11).ToArray();

        // pill 盒 = 首段盒左缘 .. 末段盒右缘（本地坐标 + 排版原点）。
        var origin = TextPainter.ResolveOrigin(node.Frame, layout.Size, TextAlignment.Left, layout.PixelScale);
        var line = layout.Lines[codeSegs[0].LineIndex];
        var expectedLeft = codeSegs.Min(s => s.X - s.InsetLeft);
        var expectedRight = codeSegs.Max(s => s.X + s.Width);
        Assert.Equal(origin.X + expectedLeft, pill.Bounds.X, 1);
        Assert.Equal(origin.X + expectedRight, pill.Bounds.X + pill.Bounds.Width, 1);
        Assert.Equal(line.Bounds.Height, pill.Bounds.Height, 1);
    }

    [Fact]
    public void 两个相邻独立code盒_不合并为一个pill()
    {
        // 两个相邻（端点相接）各自带 padding 的 range——同色同 radius 也是两个
        // 逻辑盒，背景不得误合并成一片。
        var codeBg = new Color(128, 128, 128, 60);
        var styled = new StyledText(
            "aabb",
            [
                new TextStyleRange(0, 2, new TextStyle { Background = codeBg, InlineCodePaddingHorizontal = 5 }),
                new TextStyleRange(2, 4, new TextStyle { Background = codeBg, InlineCodePaddingHorizontal = 5 }),
            ],
            null);

        using var runtime = CreateRuntime(new RichText(styled), new Size(400, 120));
        var commands = runtime.BuildFrame().Commands;
        var pills = commands.OfType<RectangleCommand>().Where(c => c.Fill == codeBg).ToArray();
        Assert.Equal(2, pills.Length);
        Assert.True(pills[0].Bounds.X + pills[0].Bounds.Width <= pills[1].Bounds.X + 0.5,
            $"两个独立 code 盒的 pill 不应重叠：{pills[0].Bounds} vs {pills[1].Bounds}。");
    }

    [Fact]
    public void 同盒多子段_hit覆盖左pad条带()
    {
        // HitLink 与 pill 同盒几何：左 pad 条带（盒左缘 .. 首字形 X）可激活链接。
        object? activated = null;
        var styled = new StyledText(
            "go",
            [new TextStyleRange(0, 2, new TextStyle { InlineCodePaddingHorizontal = 6 })],
            [new TextInteractionRange(0, 2, 7, "payload")]);

        using var runtime = CreateRuntime(
            new RichText(styled) { OnLinkActivate = p => activated = p },
            new Size(400, 120));
        var node = runtime.Root.Require<RichText>();
        var layout = ((RichText.RichTextViewCache)node.ViewCache!).Layout!;
        var seg = Assert.Single(layout.Segments);
        var origin = TextPainter.ResolveOrigin(node.Frame, layout.Size, TextAlignment.Left, layout.PixelScale);
        var line = layout.Lines[seg.LineIndex];

        // 左 pad 条带中点：盒左缘 + pad/2（seg.X 是字形起点，盒左缘 = X − InsetLeft）。
        var leftBand = new Point(origin.X + seg.X - (seg.InsetLeft / 2), origin.Y + line.Bounds.Y + (line.Bounds.Height / 2));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, leftBand, PointerButton.Left));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, leftBand, PointerButton.Left));
        Assert.Equal("payload", activated);

        // 右 pad 条带：盒右缘内（X + Width 是盒右缘，Width 已含右 pad）。
        activated = null;
        var rightBand = new Point(origin.X + seg.X + seg.Width - (seg.InsetRight / 2), leftBand.Y);
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, rightBand, PointerButton.Left));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, rightBand, PointerButton.Left));
        Assert.Equal("payload", activated);
    }
}
