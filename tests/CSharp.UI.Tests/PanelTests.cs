using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// PS-02：<see cref="Panel"/> 消费者可观察行为——内缩布局（padding+border 双侧）、
/// 绘制次序（背景→子内容→边框）、零厚度无边框、主题 null 色现值与 paint-only 纪律。
/// 断言只走 DesiredSize/Frame/绘制命令；不 pin 内部字段、私有状态或默认常量字面量。
/// </summary>
public class PanelTests
{
    private static Rectangle FixedBox(double width, double height)
        => new Rectangle(new Color(0x12, 0x34, 0x56)) { FixedSize = new Size(width, height) };

    [Fact]
    public void P1_DesiredSizeIncludesPaddingAndBorderBothSides()
    {
        var panel = new Panel(FixedBox(40, 20)) { ContentPadding = new Thickness(10), BorderThickness = 3 };
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Require<Panel>();
        Assert.Equal(40 + 26, node.DesiredSize.Width, 3);   // (10+3) 双侧
        Assert.Equal(20 + 26, node.DesiredSize.Height, 3);
    }

    [Fact]
    public void P2_ChildFrameIsInsetByPaddingAndBorder()
    {
        var panel = new Panel(FixedBox(40, 20)) { ContentPadding = new Thickness(10), BorderThickness = 3 };
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Require<Panel>();
        var child = node.Children[0];
        Assert.Equal(node.Frame.X + 13, child.Frame.X, 3);
        Assert.Equal(node.Frame.Y + 13, child.Frame.Y, 3);
        Assert.Equal(node.Frame.Right - 13, child.Frame.Right, 3);
        Assert.Equal(node.Frame.Bottom - 13, child.Frame.Bottom, 3);
    }

    [Fact]
    public void P3_AsymmetricInsetsDeflateChildOnEachSide()
    {
        var panel = new Panel(FixedBox(40, 20))
        {
            ContentPadding = new Thickness(2, 4, 6, 8),
            BorderThickness = 1,
        };
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Require<Panel>();
        var child = node.Children[0];
        Assert.Equal(node.Frame.X + 3, child.Frame.X, 3);           // left 2 + border 1
        Assert.Equal(node.Frame.Y + 5, child.Frame.Y, 3);           // top 4 + border 1
        Assert.Equal(node.Frame.Right - 7, child.Frame.Right, 3);   // right 6 + border 1
        Assert.Equal(node.Frame.Bottom - 9, child.Frame.Bottom, 3); // bottom 8 + border 1
    }

    [Fact]
    public void P4_ZeroPaddingAndZeroBorder_PlacesChildAtEdgeAndDrawsNoStroke()
    {
        var panel = new Panel(FixedBox(40, 20)) { ContentPadding = Thickness.Zero, BorderThickness = 0 };
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        var commands = app.Frame();

        var node = app.Runtime.Require<Panel>();
        var child = node.Children[0];
        Assert.Equal(node.Frame, child.Frame);

        var strokes = commands.OfType<RectangleCommand>()
            .Count(r => r.Bounds == node.Frame && r.Stroke is not null);
        Assert.Equal(0, strokes); // BorderThickness=0：无边框命令
        Assert.Contains(commands, c =>
            c is RectangleCommand { Stroke: null } r && r.Fill == Theme.Light.Surface && r.Bounds == node.Frame);
    }

    [Fact]
    public void P5_ExplicitColorsDrawBackgroundThenChildThenStroke()
    {
        var background = new Color(10, 20, 30);
        var border = new Color(200, 100, 50);
        var childFill = new Color(0xAA, 0xBB, 0xCC);
        var panel = new Panel(new Rectangle(childFill) { FixedSize = new Size(40, 20) })
        {
            BackgroundColor = background,
            BorderColor = border,
            BorderThickness = 2,
            CornerRadius = 5,
        };
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        var commands = app.Frame();

        var node = app.Runtime.Require<Panel>();
        var child = node.Children[0];
        // 单次遍历定位三条矩形命令：panel 背景、子矩形填充、panel 描边。
        var fillIndex = -1;
        var childIndex = -1;
        var strokeIndex = -1;
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i] is not RectangleCommand rect)
            {
                continue;
            }

            if (fillIndex < 0 && rect.Stroke is null && rect.Fill == background && rect.Bounds == node.Frame)
            {
                fillIndex = i;
            }
            else if (childIndex < 0 && rect.Stroke is null && rect.Fill == childFill && rect.Bounds == child.Frame)
            {
                childIndex = i;
            }
            else if (strokeIndex < 0 && rect.Stroke == border && rect.StrokeWidth == 2 && rect.Bounds == node.Frame)
            {
                strokeIndex = i;
            }
        }

        Assert.True(fillIndex >= 0, "未找到背景填充命令。");
        Assert.True(childIndex > fillIndex, "子内容必须在背景之后绘制。");
        Assert.True(strokeIndex > childIndex, "边框描边必须在子内容之后绘制。");
    }

    [Fact]
    public void P6_NullColorsReadCurrentThemePerFrame()
    {
        var panel = new Panel(new Text("ab"));
        using var app = new TestApp(panel, new Size(400, 300), theme: Theme.Light);
        var light = app.Frame().OfType<RectangleCommand>()
            .Where(r => r.Bounds == app.Runtime.Require<Panel>().Frame).ToArray();
        Assert.Equal(Theme.Light.Surface, light.Single(r => r.Stroke is null).Fill);
        Assert.Equal(Theme.Light.Border, light.Single(r => r.Stroke is not null).Stroke);

        app.Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
        app.Runtime.ProcessPendingWork();
        var dark = app.Frame().OfType<RectangleCommand>()
            .Where(r => r.Bounds == app.Runtime.Require<Panel>().Frame).ToArray();
        Assert.Equal(Theme.Dark.Surface, dark.Single(r => r.Stroke is null).Fill);
        Assert.Equal(Theme.Dark.Border, dark.Single(r => r.Stroke is not null).Stroke);
    }

    [Fact]
    public void P7_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new Panel(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { BorderThickness = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { BorderThickness = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { CornerRadius = -2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { CornerRadius = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { ContentPadding = new Thickness(-1, 0, 0, 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Panel(new Text("x")) { ContentPadding = new Thickness(double.NaN) });
    }

    [Fact]
    public void P8_PanelIsNotFocusable_AndChildInteractionFlowsNormally()
    {
        var writes = 0;
        var panel = new Panel(new Button("ok", () => writes++));
        using var app = new TestApp(panel.Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Require<Panel>();
        Assert.False(node.View.IsFocusable);

        var button = app.Runtime.Require<Button>();
        app.Click(new Point(button.Frame.X + button.Frame.Width / 2, button.Frame.Y + button.Frame.Height / 2));
        Assert.Equal(1, writes);
    }
}
