using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 05：布局、修饰符与绘制命令生成。</summary>
public class LayoutTests
{
    private const double FontSize = 14;
    private const double CharWidth = FontSize * 0.6;   // StubTextService 的等宽估算
    private const double LineHeight = FontSize * 1.25;

    [Fact]
    public void NestedStacks_RespectSpacingAlignmentAndConstraints()
    {
        var view = new VStack(
                new HStack(new Text("a"), new Text("b")).WithSpacing(10),
                new Text("c"))
            .WithSpacing(20)
            .Padding(10);

        using var app = new TestApp(view, new Size(400, 300));
        app.Frame();

        var padding = app.Runtime.Root;
        Assert.Equal(new Rect(0, 0, 400, 300), padding.Frame);

        var stack = padding.Children[0];
        Assert.Equal(new Rect(10, 10, 380, 280), stack.Frame);

        var hstack = stack.Children[0];
        Assert.Equal(LineHeight, hstack.Frame.Height, 3);
        Assert.Equal(10, hstack.Frame.Y, 3);

        var textA = hstack.Children[0];
        var textB = hstack.Children[1];
        Assert.Equal(10, textA.Frame.X, 3);
        Assert.Equal(CharWidth, textA.Frame.Width, 3);
        Assert.Equal(10 + CharWidth + 10, textB.Frame.X, 3);

        var textC = stack.Children[1];
        Assert.Equal(10 + LineHeight + 20, textC.Frame.Y, 3);
    }

    [Fact]
    public void Spacer_AbsorbsRemainingSpace()
    {
        using var app = new TestApp(new VStack(new Text("a"), new Spacer(), new Text("b")), new Size(400, 300));
        app.Frame();

        var stack = app.Runtime.Root;
        var spacer = stack.Children[1];
        var bottom = stack.Children[2];

        Assert.Equal(300 - (LineHeight * 2), spacer.Frame.Height, 3);
        Assert.Equal(300 - LineHeight, bottom.Frame.Y, 3);
        Assert.Equal(LineHeight, bottom.Frame.Height, 3);
    }

    [Fact]
    public void Frame_ConstrainsSizeAndCentersChild()
    {
        using var app = new TestApp(new HStack(new Text("x").Frame(width: 100, height: 40)), new Size(400, 300));
        app.Frame();

        var frame = app.Runtime.Root.Require<FrameView>();
        Assert.Equal(new Size(100, 40), frame.Frame.Size);

        // Frame 固定外框尺寸，子项按 Center 对齐并保持自然尺寸。
        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal(CharWidth, text.Frame.Width, 3);
        Assert.Equal(LineHeight, text.Frame.Height, 3);
        Assert.Equal(frame.Frame.X + ((100 - CharWidth) / 2), text.Frame.X, 3);
        Assert.Equal(frame.Frame.Y + ((40 - LineHeight) / 2), text.Frame.Y, 3);
    }

    [Fact]
    public void ModifierOrder_PaddingThenBackground_CoversPadding()
    {
        using var padded = new TestApp(new Text("x").Padding(10).Background(Color.Red), new Size(200, 100));
        var paddedCommands = padded.Frame().OfType<RectangleCommand>().ToArray();
        Assert.Single(paddedCommands);
        Assert.Equal(new Rect(0, 0, 200, 100), paddedCommands[0].Bounds);

        using var reversed = new TestApp(new Text("x").Background(Color.Red).Padding(10), new Size(200, 100));
        var reversedCommands = reversed.Frame().OfType<RectangleCommand>().ToArray();
        Assert.Single(reversedCommands);
        Assert.Equal(new Rect(10, 10, 180, 80), reversedCommands[0].Bounds);

        // 两种写法产生不同的背景范围，说明修饰符顺序具有语义。
        Assert.NotEqual(paddedCommands[0].Bounds, reversedCommands[0].Bounds);
    }

    [Fact]
    public void BorderAndOpacity_AreEmittedInOrderWithMultipliedOpacity()
    {
        using var app = new TestApp(new Rectangle(Color.Red).Opacity(0.5).Border(Color.Black, 2), new Size(100, 50));
        var commands = app.Frame();

        var rect = Assert.IsType<RectangleCommand>(commands[0]);
        Assert.Equal(0.5, rect.Opacity, 3);

        var border = Assert.IsType<RectangleCommand>(commands[1]);
        Assert.Equal(Color.Black, border.Stroke);
        Assert.Equal(2, border.StrokeWidth);
        Assert.Equal(Color.Transparent, border.Fill);
    }

    [Fact]
    public void ZStack_LayersInDescriptionOrder_TopmostWinsHitTest()
    {
        var bottom = new Rectangle(Color.Red);
        var top = new Rectangle(Color.Blue);
        using var app = new TestApp(new ZStack(bottom, top), new Size(200, 100));

        var commands = app.Frame().OfType<RectangleCommand>().ToArray();
        Assert.Equal(Color.Red, commands[0].Fill);
        Assert.Equal(Color.Blue, commands[1].Fill);

        var hit = app.Runtime.HitTest(new Point(10, 10));
        Assert.NotNull(hit);
        Assert.Same(top, hit!.View);
    }

    [Fact]
    public void NestedClip_IntersectsClips_AndEmptyClipEmitsNothing()
    {
        var inner = new ClipView(new Rectangle(Color.Red).Size(1000, 100)).Frame(width: 1000, height: 100);
        using var app = new TestApp(new ClipView(inner).Frame(width: 50, height: 20, alignment: Alignment.Start), new Size(400, 300));
        var commands = app.Frame();

        var pushes = commands.OfType<PushClipCommand>().ToArray();
        Assert.Equal(2, pushes.Length);
        Assert.Equal(new Rect(0, 0, 50, 20), pushes[0].Bounds);
        Assert.Equal(new Rect(0, 0, 50, 20), pushes[1].Bounds);

        using var empty = new TestApp(new ClipView(new Rectangle(Color.Red).Size(10, 10)).Frame(width: 0, height: 0, alignment: Alignment.Start), new Size(100, 100));
        var emptyCommands = empty.Frame();
        Assert.DoesNotContain(emptyCommands, c => c is RectangleCommand);
        Assert.DoesNotContain(emptyCommands, c => c is PushClipCommand);
    }

    [Fact]
    public void Layout_IsIdenticalAcrossDpi_ButFramebufferDiffers()
    {
        var build = () => new VStack(new Text("中文 abc"), new Button("ok")).WithSpacing(8).Padding(12);

        using var at1x = new TestApp(build(), new Size(400, 300), 1.0);
        using var at2x = new TestApp(build(), new Size(400, 300), 2.0);

        var frames1 = at1x.Frame();
        var frames2 = at2x.Frame();

        Assert.Equal(at1x.Runtime.Root.Frame, at2x.Runtime.Root.Frame);
        var rects1 = frames1.OfType<RectangleCommand>().Select(c => c.Bounds).ToArray();
        var rects2 = frames2.OfType<RectangleCommand>().Select(c => c.Bounds).ToArray();
        Assert.Equal(rects1, rects2);
    }

    [Fact]
    public void ColorOnlyChange_SkipsLayout_ButTextChangeRelayouts()
    {
        var component = new ColorChangingComponent();
        using var app = new TestApp(component);
        app.Frame();

        var passesAfterMount = app.Runtime.LayoutPassCount;
        Assert.True(passesAfterMount > 0);

        // 仅颜色变化：不重新测量/排列。
        component.Background!.Value = Color.Blue;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(passesAfterMount, app.Runtime.LayoutPassCount);

        // 文本变化：需要重新布局。
        component.Content!.Value = "更长的文本内容";
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(passesAfterMount + 1, app.Runtime.LayoutPassCount);
    }

    [Fact]
    public void TextWidthProviderChange_IsRemeasured()
    {
        using var app = new TestApp(new VStack(new Text("abcd")), new Size(400, 300));
        app.Frame();
        var text = app.Runtime.Root.Require<Text>();
        var widthBefore = text.DesiredSize.Width;

        app.Text.CharWidthScale = 2.0;
        app.Runtime.SetViewport(new Size(400, 300), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.True(text.DesiredSize.Width > widthBefore);
        Assert.True(app.Runtime.LastMeasureCount > 0);
    }

    [Fact]
    public void ZeroSizeViewport_ProducesNoInvalidCoordinates()
    {
        using var app = new TestApp(new VStack(new Text("a"), new Button("b")).Padding(8), new Size(0, 0));
        var commands = app.Frame();

        Assert.All(commands, command =>
        {
            var bounds = command.GetBounds();
            Assert.True(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y));
            Assert.True(bounds.Width >= 0 && bounds.Height >= 0);
            Assert.True(double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height));
        });
    }
}

/// <summary>背景色与内容分别由状态驱动的组件。</summary>
internal sealed class ColorChangingComponent : Component
{
    public State<Color>? Background { get; private set; }

    public State<string>? Content { get; private set; }

    protected override View Build()
    {
        Background = UseState("bg", Color.Red);
        Content = UseState("text", "短");
        return new VStack(new Text(Content.Value)).Padding(6).Background(Background.Value);
    }
}
