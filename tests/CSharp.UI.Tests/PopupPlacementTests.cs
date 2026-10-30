using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// P-02：弹层定位纯计算与两阶段测量的消费者可见几何。
/// 纯函数部分只断言返回矩形；运行时部分只断言最终 Frame/挂载状态/内部 ScrollView 可见尺寸，
/// 不 pin 私有字段或内部计数。
/// </summary>
public class PopupPlacementTests
{
    private static readonly Rect Bounds = new(0, 0, 300, 200);

    [Fact]
    public void B1_FitsBelow_AlignsAnchorLeft()
    {
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(20, 20, 80, 30), new Size(120, 50), Bounds, PopupPlacement.Auto, double.PositiveInfinity);

        Assert.Equal(new Rect(20, 50, 120, 50), frame);
    }

    [Fact]
    public void B2_BelowInsufficient_FlipsAboveAndRestsOnAnchorTop()
    {
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(20, 150, 80, 30), new Size(120, 100), Bounds, PopupPlacement.Auto, double.PositiveInfinity);

        Assert.NotNull(frame);
        Assert.Equal(20, frame!.Value.X);
        Assert.Equal(50, frame.Value.Y);          // anchor.Top(150) - h(100)
        Assert.Equal(100, frame.Value.Height);
    }

    [Fact]
    public void B3_BothSidesInsufficient_ChoosesLargerSideAndClampsInsideBounds()
    {
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(20, 90, 80, 20), new Size(120, 150), Bounds, PopupPlacement.Auto, double.PositiveInfinity);

        Assert.NotNull(frame);
        Assert.Equal(90, frame!.Value.Height);    // sideAvail 90（下方/上方相等，保留下方）
        Assert.True(frame.Value.Bottom <= Bounds.Bottom);
        Assert.True(frame.Value.Top >= Bounds.Top);
    }

    [Fact]
    public void B4_AnchorAtBoundsEdges_ClampsIntoBounds()
    {
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(-50, -40, 80, 20), new Size(500, 500), Bounds, PopupPlacement.Auto, double.PositiveInfinity);

        Assert.NotNull(frame);
        Assert.True(frame!.Value.X >= Bounds.X && frame.Value.Y >= Bounds.Y);
        Assert.True(frame.Value.Right <= Bounds.Right && frame.Value.Bottom <= Bounds.Bottom);
    }

    [Fact]
    public void B5_OversizeContent_UsesBoundsWidthAndSideAvailableHeightBeforeMaxHeight()
    {
        var bounds = new Rect(0, 0, 120, 80);
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(0, 0, 50, 20), new Size(500, 500), bounds, PopupPlacement.Auto, 400);

        Assert.NotNull(frame);
        Assert.Equal(120, frame!.Value.Width);
        Assert.Equal(60, frame.Value.Height);     // spaceBelow 60 优先于 maxHeight 400 / bounds 80
        Assert.True(frame.Value.Bottom <= bounds.Bottom);
    }

    [Fact]
    public void B5b_MaxHeightLimitsWhenBelowSideIsLarger()
    {
        var frame = PopupPlacementCalculator.Resolve(
            new Rect(0, 0, 50, 10), new Size(100, 500), Bounds, PopupPlacement.Below, 70);

        Assert.NotNull(frame);
        Assert.Equal(70, frame!.Value.Height);   // spaceBelow 190 > maxHeight 70
    }

    [Fact]
    public void B6_EmptyOrNegativeBounds_ReturnsNull()
    {
        Assert.Null(PopupPlacementCalculator.Resolve(
            new Rect(10, 10, 20, 20), new Size(50, 50), new Rect(0, 0, 0, 100),
            PopupPlacement.Auto, double.PositiveInfinity));
        Assert.Null(PopupPlacementCalculator.Resolve(
            new Rect(10, 10, 20, 20), new Size(50, 50), new Rect(0, 0, 100, -1),
            PopupPlacement.Auto, double.PositiveInfinity));
    }

    [Fact]
    public void B7_ForcedSide_DoesNotFlip()
    {
        // 下方空间充足，但强制 Above：按上方可用高度截断，底边贴锚顶。
        var above = PopupPlacementCalculator.Resolve(
            new Rect(20, 20, 80, 30), new Size(100, 80), Bounds, PopupPlacement.Above, double.PositiveInfinity);
        Assert.NotNull(above);
        Assert.Equal(20, above!.Value.Height);    // spaceAbove = 20
        Assert.Equal(0, above.Value.Y);
        Assert.Equal(20, above.Value.Bottom);

        // 上方空间充足，但强制 Below：按下方可用高度截断。
        var below = PopupPlacementCalculator.Resolve(
            new Rect(20, 170, 80, 20), new Size(100, 80), Bounds, PopupPlacement.Below, double.PositiveInfinity);
        Assert.NotNull(below);
        Assert.Equal(10, below!.Value.Height);
        Assert.Equal(190, below.Value.Y);
    }

    [Fact]
    public void B8_ZeroSizedContent_ReturnsNull()
    {
        Assert.Null(PopupPlacementCalculator.Resolve(
            new Rect(10, 10, 20, 20), new Size(0, 40), Bounds, PopupPlacement.Auto, double.PositiveInfinity));
        Assert.Null(PopupPlacementCalculator.Resolve(
            new Rect(10, 10, 20, 20), new Size(40, 0), Bounds, PopupPlacement.Auto, double.PositiveInfinity));
        Assert.Null(PopupPlacementCalculator.Resolve(
            new Rect(10, 10, 20, 20), new Size(double.NaN, 40), Bounds, PopupPlacement.Auto, double.PositiveInfinity));
    }

    // ---- runtime (two-stage measure / arrange) ---------------------------

    [Fact]
    public void B9_WideText_IsMeasuredWithinBoundsWidth_AndFrameStaysInside()
    {
        var anchor = new Button("anchor", null);
        using var app = new TestApp(new VStack(anchor).Padding(8), new Size(300, 240));
        var anchorNode = app.Runtime.Root.FindDescendant<Button>()!;
        var content = new Text(string.Join(' ', Enumerable.Repeat("WIDE", 60))).Wrap();
        app.Runtime.Overlay.Show(anchorNode, content, double.PositiveInfinity);
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var surface = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        var bounds = new Rect(4, 4, 292, 232);
        Assert.True(surface.Frame.Width > 0 && surface.Frame.Width <= bounds.Width);
        Assert.True(surface.Frame.X >= bounds.X && surface.Frame.Right <= bounds.Right);
        var textNode = surface.FindDescendants<Text>().First();
        Assert.True(textNode.Frame.Width <= surface.Frame.Width + 0.01);
    }

    [Fact]
    public void B9b_MaxHeightWithScrollView_GetsFiniteViewportAndMounts()
    {
        var anchor = new Button("anchor", null);
        using var app = new TestApp(new VStack(anchor).Padding(8), new Size(300, 500));
        var anchorNode = app.Runtime.Root.FindDescendant<Button>()!;
        var list = new VStack(Enumerable.Range(0, 40).Select(i => new Text($"row {i}")).ToArray()).Spacing(2);
        var content = new ScrollView(list, ScrollAxis.Vertical);
        app.Runtime.Overlay.Show(anchorNode, content, maxHeight: 120);
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var surface = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        Assert.True(surface.Frame.Height > 0 && surface.Frame.Height <= 120);
        var scroll = surface.FindDescendants<ScrollView>().First();
        Assert.True(scroll.Frame.Height > 0 && scroll.Frame.Height <= surface.Frame.Height);
    }

    [Fact]
    public void B9c_ForcedAbove_DoesNotFlipAndCapsAtSpaceAbove()
    {
        var anchor = new Button("anchor", null);
        using var app = new TestApp(new VStack(anchor, new Spacer(400)).Spacing(4).Padding(8), new Size(300, 240));
        var anchorNode = app.Runtime.Root.FindDescendant<Button>()!;
        var content = new Text(string.Join(' ', Enumerable.Repeat("row", 40))).Wrap();
        app.Runtime.Overlay.Show(anchorNode, content, double.PositiveInfinity,
            preference: PopupPlacement.Above);
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var surface = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        Assert.True(surface.Frame.Height > 0);
        Assert.True(surface.Frame.Bottom <= anchorNode.Frame.Y + 0.01);
    }

    [Fact]
    public void B9d_NoAvailableSpace_ClosesAfterSafePhase()
    {
        var anchor = new Button("anchor", null);
        using var app = new TestApp(new VStack(anchor).Padding(8), new Size(300, 240));
        var anchorNode = app.Runtime.Root.FindDescendant<Button>()!;
        app.Runtime.Overlay.Show(anchorNode, new Text("popup").Frame(width: 80, height: 60), 100);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        app.Runtime.SetViewport(new Size(0, 0), 1);
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup.IsMounted);
    }

    [Fact]
    public void B9e_ZeroSizedContent_ClosesAfterSafePhase()
    {
        var anchor = new Button("anchor", null);
        using var app = new TestApp(new VStack(anchor).Padding(8), new Size(300, 240));
        var anchorNode = app.Runtime.Root.FindDescendant<Button>()!;
        app.Runtime.Overlay.Show(anchorNode, new Spacer(), double.PositiveInfinity);
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }
}
