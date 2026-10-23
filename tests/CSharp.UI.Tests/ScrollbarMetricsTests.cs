using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

public class ScrollbarMetricsTests
{
    // 固定几何：非零原点轨道、纵/横对称
    private static Rect VTrack => new(90, 50, 12, 200);   // s=52, T=196
    private static Rect HTrack => new(50, 90, 200, 12);   // s=52, T=196

    [Fact]
    public void ShowScrollbar_RequiresOverflowAndFiniteExtents()
    {
        // C=400 > V=100 → 显示
        Assert.True(ScrollbarMetrics.ShowScrollbar(100, 400, 196));

        // C<=V 不显示
        Assert.False(ScrollbarMetrics.ShowScrollbar(100, 100, 196));
        Assert.False(ScrollbarMetrics.ShowScrollbar(100, 50, 196));

        // V<=0 / T<=0 不显示
        Assert.False(ScrollbarMetrics.ShowScrollbar(0, 400, 196));
        Assert.False(ScrollbarMetrics.ShowScrollbar(100, 400, 0));

        // 非有限不显示
        Assert.False(ScrollbarMetrics.ShowScrollbar(100, double.PositiveInfinity, 196));
        Assert.False(ScrollbarMetrics.ShowScrollbar(double.PositiveInfinity, 400, 196));
        Assert.False(ScrollbarMetrics.ShowScrollbar(100, double.NaN, 196));
    }

    [Fact]
    public void ThumbLength_ProportionalWithMinimum()
    {
        // C=2V → thumb=T/2
        Assert.Equal(98, ScrollbarMetrics.ThumbLength(100, 200, 196), 6);

        // 有限大 C → thumb=MinThumbLength
        Assert.Equal(24, ScrollbarMetrics.ThumbLength(100, 10000, 196), 6);

        // 不显示 → 0
        Assert.Equal(0, ScrollbarMetrics.ThumbLength(100, 100, 196));
    }

    [Fact]
    public void ThumbLeading_MapsOffsetToTrackPosition()
    {
        // offset=0 → thumbLeading=s
        Assert.Equal(52, ScrollbarMetrics.ThumbLeading(VTrack, horizontal: false, 100, 200, 0), 6);

        // offset=max → thumbLeading = s+travel = 52+(196-98)=150
        Assert.Equal(150, ScrollbarMetrics.ThumbLeading(VTrack, horizontal: false, 100, 200, 100), 6);

        // 横向对称
        Assert.Equal(52, ScrollbarMetrics.ThumbLeading(HTrack, horizontal: true, 100, 200, 0), 6);
        Assert.Equal(150, ScrollbarMetrics.ThumbLeading(HTrack, horizontal: true, 100, 200, 100), 6);
    }

    [Fact]
    public void HitTest_TrackContainsFirstThenAxisRegion()
    {
        // 轨道外点 → None（不误判 click page）
        Assert.Equal(ScrollbarTrackHit.None, ScrollbarMetrics.HitTest(VTrack, false, 100, 200, 0, new Point(89, 52)));

        // thumb 上
        Assert.Equal(ScrollbarTrackHit.Thumb, ScrollbarMetrics.HitTest(VTrack, false, 100, 200, 0, new Point(96, 60)));

        // thumb 之前
        Assert.Equal(ScrollbarTrackHit.Before, ScrollbarMetrics.HitTest(VTrack, false, 100, 200, 0, new Point(96, 51)));

        // thumb 之后（thumb 在 52-150，点 160）
        Assert.Equal(ScrollbarTrackHit.After, ScrollbarMetrics.HitTest(VTrack, false, 100, 200, 0, new Point(96, 160)));
    }

    [Fact]
    public void HitTest_FullThumbWhenNoTravel()
    {
        // C>V 但 T<=MinThumbLength 全满 → 全域 Thumb，无 Before/After
        var tiny = new Rect(90, 50, 12, 20); // T=16 < 24
        Assert.True(ScrollbarMetrics.ShowScrollbar(10, 100, 16));
        Assert.Equal(ScrollbarTrackHit.Thumb, ScrollbarMetrics.HitTest(tiny, false, 10, 100, 0, new Point(96, 51)));
        Assert.Equal(ScrollbarTrackHit.Thumb, ScrollbarMetrics.HitTest(tiny, false, 10, 100, 0, new Point(96, 69)));
    }

    [Fact]
    public void DragGrabAnchor_PreventsJump()
    {
        // 在 thumb 中点按下 → grab = 按点 - thumbLeading
        var grab = ScrollbarMetrics.DragGrabAnchor(VTrack, false, 100, 200, 0, new Point(96, 101));
        // thumbLeading=52, p=101 → grab=49
        Assert.Equal(49, grab, 6);

        // 移动后 offset 连续：p=151 → ratio=(151-52-49)/98 = 50/98 → offset≈51
        var next = ScrollbarMetrics.DragToOffset(VTrack, false, 100, 200, grab, new Point(96, 151), 0);
        Assert.Equal(50.0 / 98.0 * 100, next, 6);
    }

    [Fact]
    public void DragToOffset_ClampedToRange()
    {
        // 上越界 → 0
        var grab = ScrollbarMetrics.DragGrabAnchor(VTrack, false, 100, 200, 50, new Point(96, 125));
        var under = ScrollbarMetrics.DragToOffset(VTrack, false, 100, 200, grab, new Point(96, 0), 50);
        Assert.Equal(0, under);

        // 下越界 → maxOffset=100
        var over = ScrollbarMetrics.DragToOffset(VTrack, false, 100, 200, grab, new Point(96, 300), 50);
        Assert.Equal(100, over);
    }

    [Fact]
    public void DragToOffset_NoTravelKeepsCurrentOffset()
    {
        // 全满 thumb（travel=0）→ drag 不变
        var tiny = new Rect(90, 50, 12, 20);
        var grab = ScrollbarMetrics.DragGrabAnchor(tiny, false, 10, 100, 0, new Point(96, 55));
        var next = ScrollbarMetrics.DragToOffset(tiny, false, 10, 100, grab, new Point(96, 60), 30);
        Assert.Equal(30, next);
    }

    [Fact]
    public void DragToOffset_GeometryGoesInvalidDuringDrag_ReturnsCurrent()
    {
        // 拖拽中内容/视口/轨道变无效 → 保持当前 offset，不产生 NaN/Inf
        var grab = ScrollbarMetrics.DragGrabAnchor(VTrack, false, 100, 200, 50, new Point(96, 125));

        // C→Inf：内容无限 → 无效
        Assert.Equal(50, ScrollbarMetrics.DragToOffset(VTrack, false, 100, double.PositiveInfinity, grab, new Point(96, 160), 50));

        // C→NaN：无效
        Assert.Equal(50, ScrollbarMetrics.DragToOffset(VTrack, false, 100, double.NaN, grab, new Point(96, 160), 50));

        // V→NaN：无效
        Assert.Equal(50, ScrollbarMetrics.DragToOffset(VTrack, false, double.NaN, 200, grab, new Point(96, 160), 50));

        // T→Inf：轨道无界 → 无效
        var infTrack = new Rect(90, 50, 12, double.PositiveInfinity);
        Assert.Equal(50, ScrollbarMetrics.DragToOffset(infTrack, false, 100, 200, grab, new Point(96, 160), 50));

        // ThumbLeading 同步稳定：无效 → s
        Assert.Equal(52, ScrollbarMetrics.ThumbLeading(VTrack, false, 100, double.PositiveInfinity, 50));
        Assert.Equal(52, ScrollbarMetrics.ThumbLeading(VTrack, false, 100, double.NaN, 50));
        Assert.Equal(52, ScrollbarMetrics.ThumbLeading(infTrack, false, 100, 200, 50));

        // HitTest 同步 None
        Assert.Equal(ScrollbarTrackHit.None, ScrollbarMetrics.HitTest(VTrack, false, 100, double.PositiveInfinity, 50, new Point(96, 60)));
    }

    [Fact]
    public void PageOffset_BeforeAfterOneViewport()
    {
        // Before → -V
        Assert.Equal(50, ScrollbarMetrics.PageOffset(ScrollbarTrackHit.Before, 100, 150, 300));

        // After → +V
        Assert.Equal(250, ScrollbarMetrics.PageOffset(ScrollbarTrackHit.After, 100, 150, 300));

        // 夹取边界
        Assert.Equal(0, ScrollbarMetrics.PageOffset(ScrollbarTrackHit.Before, 100, 50, 300));
        Assert.Equal(300, ScrollbarMetrics.PageOffset(ScrollbarTrackHit.After, 100, 250, 300));

        // Thumb/None 不变
        Assert.Equal(150, ScrollbarMetrics.PageOffset(ScrollbarTrackHit.Thumb, 100, 150, 300));
    }
}

/// <summary>ScrollView 滚动条 owner 接线：persistent gutter、真实 drag/track/capture 全流程（非 metrics 纯函数）。</summary>
public class ScrollbarInteractionTests
{
    private static (TestApp App, ViewNode Scroll) MakeScroll(double contentHeight = 400)
    {
        // 固定高内容 > viewport → persistent gutter 激活。
        var app = new TestApp(
            new ScrollView(new Spacer(contentHeight)),
            new Size(200, 100));
        app.Frame();
        return (app, app.Runtime.Root.Require<ScrollView>());
    }

    private static Rect TrackRect(ViewNode scroll, bool horizontal)
        => horizontal
            ? new Rect(scroll.Frame.X, scroll.Frame.Bottom - ScrollbarMetrics.Thickness,
                       scroll.Frame.Width, ScrollbarMetrics.Thickness)
            : new Rect(scroll.Frame.Right - ScrollbarMetrics.Thickness, scroll.Frame.Y,
                       ScrollbarMetrics.Thickness, scroll.Frame.Height);

    [Fact]
    public void TrackClick_PagesOnce_AndReleases()
    {
        var (app, scroll) = MakeScroll();
        using var _ = app;

        var track = TrackRect(scroll, horizontal: false);
        // 点击轨道下方（After 区域）→ offset += viewport
        var belowThumb = new Point(track.Center.X, track.Bottom - 5);
        app.Click(belowThumb);

        Assert.True(scroll.ScrollOffsetY > 0, "track After 应翻页一次。");
    }

    [Fact]
    public void ThumbDrag_MovesOffset_ThenReleaseStops()
    {
        var (app, scroll) = MakeScroll();
        using var _ = app;

        var track = TrackRect(scroll, horizontal: false);
        var thumb = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: scroll.Frame.Height,
            contentExtent: 400,
            offset: scroll.ScrollOffsetY);

        var grab = thumb.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));

        // 拖拽 → offset 跟随 thumb
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 30)));
        var dragged = scroll.ScrollOffsetY;
        Assert.True(dragged > 0, "thumb drag 应产生正 offset。");

        // 松开 → 再 Move 不再改变
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(grab.X, grab.Y + 30), PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 80)));
        Assert.Equal(dragged, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void PointerCancel_ReleasesCaptureAndStopsDrag()
    {
        var (app, scroll) = MakeScroll();
        using var _ = app;

        var track = TrackRect(scroll, horizontal: false);
        var thumb = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: scroll.Frame.Height,
            contentExtent: 400,
            offset: scroll.ScrollOffsetY);

        var grab = thumb.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 20)));
        var before = scroll.ScrollOffsetY;

        // Cancel（宿主捕获丢失）→ 后续 Move 不再写入 offset
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Cancel, new Point(grab.X, grab.Y + 20)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 90)));
        Assert.Equal(before, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void GutterHit_LandsOnScrollView_NotChild()
    {
        var (app, scroll) = MakeScroll();
        using var _ = app;

        var track = TrackRect(scroll, horizontal: false);
        var hit = app.Runtime.HitTest(new Point(track.Center.X, track.Center.Y));
        Assert.True(hit == scroll, $"scroll={scroll.Frame} track={track} hit={hit?.ToString() ?? "null"}");
    }

    [Fact]
    public void GeometryChangeDuringDrag_CancelsGesture()
    {
        var (app, scroll) = MakeScroll();
        using var _ = app;

        var track = TrackRect(scroll, horizontal: false);
        var thumb = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: scroll.Frame.Height,
            contentExtent: 400,
            offset: scroll.ScrollOffsetY);

        var grab = thumb.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));

        // 视口变化 → Arrange 发现 geometry != DragTrackRect → 取消 gesture
        app.Runtime.SetViewport(new Size(250, 150), 1.0);
        app.Runtime.ProcessPendingWork();

        var before = scroll.ScrollOffsetY;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 60)));
        Assert.Equal(before, scroll.ScrollOffsetY, 3);
    }
}
