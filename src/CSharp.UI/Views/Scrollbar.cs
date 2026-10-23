namespace CSharp.UI;

/// <summary>滚动条轨道命中区域（指针沿滚动轴在轨道内的三段判定）。</summary>
internal enum ScrollbarTrackHit
{
    /// <summary>不在轨道内。</summary>
    None,

    /// <summary>滑块之前的轨道区（点击向负向翻页）。</summary>
    Before,

    /// <summary>滑块。</summary>
    Thumb,

    /// <summary>滑块之后的轨道区（点击向正向翻页）。</summary>
    After,
}

/// <summary>
/// 滚动条几何与交互换算（内部共享）：轨道/滑块矩形、命中判定、拖拽→偏移、track 点击方向。
/// </summary>
/// <remarks>
/// <para>纯计算无 owner 状态；所有坐标为绝对逻辑像素，轨道起点 <c>s</c> 已含 inset。</para>
/// <para>公式：T=max(0,轨道长−2×inset)，M=max(0,C−V)，thumbLength=clamp(T×V/C, min(T,24), T)，
/// travel=T−thumbLength，thumbLeading=s+clamp(offset,0,M)/M×travel。travel≤0 显示满 thumb 但 drag 无行程。</para>
/// </remarks>
internal static class ScrollbarMetrics
{
    /// <summary>轨道槽宽（逻辑像素）。</summary>
    internal const double Thickness = 12;

    /// <summary>滑块最小长度（逻辑像素）。</summary>
    internal const double MinThumbLength = 24;

    /// <summary>轨道/滑块内缩（逻辑像素）。</summary>
    internal const double Inset = 2;

    /// <summary>是否显示滚动条（内容溢出视口且几何有效）。</summary>
    internal static bool ShowScrollbar(double viewportExtent, double contentExtent, double trackLength)
        => contentExtent > viewportExtent
            && viewportExtent > 0
            && trackLength > 0
            && double.IsFinite(viewportExtent)
            && double.IsFinite(contentExtent)
            && double.IsFinite(trackLength);

    /// <summary>轨道起点（绝对坐标，含 inset）。</summary>
    internal static double TrackStart(Rect trackRect, bool horizontal)
        => horizontal ? trackRect.X + Inset : trackRect.Y + Inset;

    /// <summary>轨道可用长度（扣两端 inset）。</summary>
    internal static double TrackLength(Rect trackRect, bool horizontal)
        => Math.Max(0, (horizontal ? trackRect.Width : trackRect.Height) - (Inset * 2));

    /// <summary>滑块长度；不显示时为 0。</summary>
    internal static double ThumbLength(double viewportExtent, double contentExtent, double trackLength)
    {
        if (!ShowScrollbar(viewportExtent, contentExtent, trackLength))
        {
            return 0;
        }

        // V/C 先算比例再乘 T：避免 T*V 溢出；C→∞ 时比例趋 0 由 MinThumbLength 保底。
        var ratio = viewportExtent / contentExtent;
        return Math.Clamp(trackLength * ratio, Math.Min(trackLength, MinThumbLength), trackLength);
    }

    /// <summary>滑块起点（绝对坐标）；不显示时返回轨道起点。</summary>
    internal static double ThumbLeading(
        Rect trackRect,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset)
    {
        var s = TrackStart(trackRect, horizontal);
        var t = TrackLength(trackRect, horizontal);
        // 无效几何（非有限 V/C/T、无溢出、零/负尺寸）→ 轨道起点，不产生 NaN/Inf。
        if (!ShowScrollbar(viewportExtent, contentExtent, t))
        {
            return s;
        }

        var thumbLen = ThumbLength(viewportExtent, contentExtent, t);
        var maxOffset = contentExtent - viewportExtent;
        var travel = t - thumbLen;
        if (travel <= 0)
        {
            return s;
        }

        var ratio = Math.Clamp(offset, 0, maxOffset) / maxOffset;
        return s + (ratio * travel);
    }

    /// <summary>滑块矩形（cross 向居中于轨道内）。</summary>
    internal static Rect ThumbRect(
        Rect trackRect,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset)
    {
        var t = TrackLength(trackRect, horizontal);
        var thumbLen = ThumbLength(viewportExtent, contentExtent, t);
        var leading = ThumbLeading(trackRect, horizontal, viewportExtent, contentExtent, offset);
        return horizontal
            ? new Rect(leading, trackRect.Y + Inset, thumbLen, Math.Max(0, trackRect.Height - (Inset * 2)))
            : new Rect(trackRect.X + Inset, leading, Math.Max(0, trackRect.Width - (Inset * 2)), thumbLen);
    }

    /// <summary>命中判定：先查轨道矩形包含，再沿滚动轴分三段。</summary>
    internal static ScrollbarTrackHit HitTest(
        Rect trackRect,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset,
        Point point)
    {
        if (!trackRect.Contains(point))
        {
            return ScrollbarTrackHit.None;
        }

        if (!ShowScrollbar(viewportExtent, contentExtent, TrackLength(trackRect, horizontal)))
        {
            return ScrollbarTrackHit.None;
        }

        var p = horizontal ? point.X : point.Y;
        var thumbLeading = ThumbLeading(trackRect, horizontal, viewportExtent, contentExtent, offset);
        var thumbLength = ThumbLength(viewportExtent, contentExtent, TrackLength(trackRect, horizontal));
        var travel = TrackLength(trackRect, horizontal) - thumbLength;
        if (travel <= 0)
        {
            // 全满：整轨即 thumb。
            return ScrollbarTrackHit.Thumb;
        }

        if (p < thumbLeading)
        {
            return ScrollbarTrackHit.Before;
        }

        if (p > thumbLeading + thumbLength)
        {
            return ScrollbarTrackHit.After;
        }

        return ScrollbarTrackHit.Thumb;
    }

    /// <summary>拖拽换算：按下点距 thumb 起点的偏移（grab anchor）含轨道起点。</summary>
    internal static double DragGrabAnchor(
        Rect trackRect,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset,
        Point downPoint)
    {
        var thumbLeading = ThumbLeading(trackRect, horizontal, viewportExtent, contentExtent, offset);
        var p = horizontal ? downPoint.X : downPoint.Y;
        return p - thumbLeading;
    }

    /// <summary>拖拽→目标偏移；travel≤0 或无滚动范围时保持当前 offset。</summary>
    internal static double DragToOffset(
        Rect trackRect,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double grabAnchor,
        Point pointer,
        double currentOffset)
    {
        var t = TrackLength(trackRect, horizontal);
        // 无效几何 → 保持当前 offset（owner 管理 offset 合法性，不产生 NaN/Inf）。
        if (!ShowScrollbar(viewportExtent, contentExtent, t))
        {
            return currentOffset;
        }

        var thumbLen = ThumbLength(viewportExtent, contentExtent, t);
        var maxOffset = contentExtent - viewportExtent;
        var travel = t - thumbLen;
        if (travel <= 0)
        {
            return currentOffset;
        }

        var s = TrackStart(trackRect, horizontal);
        var p = horizontal ? pointer.X : pointer.Y;
        var ratio = (p - s - grabAnchor) / travel;
        return Math.Clamp(ratio, 0, 1) * maxOffset;
    }

    /// <summary>track 点击目标偏移（Before→−V、After→+V）；一次性，无 auto-repeat。</summary>
    internal static double PageOffset(
        ScrollbarTrackHit hit,
        double viewportExtent,
        double currentOffset,
        double maxOffset)
    {
        return hit switch
        {
            ScrollbarTrackHit.Before => Math.Max(0, currentOffset - viewportExtent),
            ScrollbarTrackHit.After => Math.Min(maxOffset, currentOffset + viewportExtent),
            _ => currentOffset,
        };
    }
}

/// <summary>滚动条按压类型（单一枚举，取代 bool 冗余）。</summary>
internal enum ScrollbarPressKind
{
    /// <summary>无按压。</summary>
    None,

    /// <summary>滑块拖拽中。</summary>
    Thumb,

    /// <summary>轨道按压（点击翻页视觉）。</summary>
    Track,
}

/// <summary>
/// 滚动条 owner 交互状态（存 <see cref="ViewNode.InteractionState"/>；随节点卸载经 <see cref="ViewNode.ResetInteractionState"/> 清）。
/// </summary>
/// <remarks>
/// <para><see cref="LastPointerPoint"/> 是 pointer 观测（非 gesture anchor）：Up/Cancel/geometry-cancel 不清，仅卸载时随 state 重置。</para>
/// <para>hover kind 为当次 <c>Emit</c> 的绘制值，不持久存——由 <c>HoveredNode</c> + <see cref="LastPointerPoint"/> 重算。</para>
/// </remarks>
internal sealed class ScrollbarState
{
    /// <summary>当前按压类型（None/Thumb/Track）。</summary>
    public ScrollbarPressKind PressKind { get; private set; }

    /// <summary>拖拽锚点：按下点距 thumb 起点的偏移（含轨道起点 s）。</summary>
    public double BarDragAnchor { get; private set; }

    /// <summary>gesture 起始的滚动轴（axis 变化取消）。</summary>
    public ScrollAxis DragAxis { get; private set; }

    /// <summary>gesture 起始的轨道矩形（track 位置/尺寸变化取消）。</summary>
    public Rect DragTrackRect { get; private set; }

    /// <summary>gesture 起始的内容 extent（内容 shrink 取消）。</summary>
    public double DragContentExtent { get; private set; }

    /// <summary>gesture 起始的视口 extent。</summary>
    public double DragViewportExtent { get; private set; }

    /// <summary>最后观测到的指针位置（point；非 gesture 状态，Cancel/几何取消不清）。</summary>
    public Point? LastPointerPoint { get; set; }

    /// <summary>是否处于 thumb 拖拽 gesture。</summary>
    public bool IsDraggingBar => PressKind == ScrollbarPressKind.Thumb;

    /// <summary>Thumb 拖拽开始（OnPointerDown）。</summary>
    public void BeginThumbDrag(
        ScrollAxis axis, Rect trackRect, double contentExtent, double viewportExtent, double grabAnchor)
    {
        PressKind = ScrollbarPressKind.Thumb;
        BarDragAnchor = grabAnchor;
        SetBaseline(axis, trackRect, contentExtent, viewportExtent);
    }

    /// <summary>Track 按压（点击翻页视觉）。</summary>
    public void BeginTrackPress(
        ScrollAxis axis, Rect trackRect, double contentExtent, double viewportExtent)
    {
        PressKind = ScrollbarPressKind.Track;
        SetBaseline(axis, trackRect, contentExtent, viewportExtent);
    }

    private void SetBaseline(ScrollAxis axis, Rect trackRect, double contentExtent, double viewportExtent)
    {
        DragAxis = axis;
        DragTrackRect = trackRect;
        DragContentExtent = contentExtent;
        DragViewportExtent = viewportExtent;
    }


    /// <summary>释放当前 gesture（Up/Cancel/几何失效共用）。</summary>
    public void ReleaseGesture()
    {
        PressKind = ScrollbarPressKind.None;
        BarDragAnchor = 0;
    }

    /// <summary>
    /// 几何同步：Arrange 发现 axis/track/content/viewport 任一变化 → 取消 gesture（本节点自身 offset 写回除外——由调用方在写 offset 前比较）。
    /// </summary>
    /// <returns>true=已取消；false=无 gesture 或几何一致。</returns>
    public bool SyncGeometry(ScrollAxis axis, Rect trackRect, double contentExtent, double viewportExtent)
    {
        if (PressKind == ScrollbarPressKind.None)
        {
            return false;
        }
        if (axis == DragAxis
            && trackRect == DragTrackRect
            && contentExtent == DragContentExtent
            && viewportExtent == DragViewportExtent)
        {
            return false;
        }
        ReleaseGesture();
        return true;
    }
}

/// <summary>
/// 共享 owner 滚动条绘制：在 <c>node</c> 的 frame clip 内画 track+thumb；
/// thumb 为中性灰（`ControlBackground` 向 `Foreground` 混色 0.40/0.48/0.56），不继承 Accent——
/// 滚动条是导航辅助而非主操作。track 沿用 Control* 系列；disabled 走 Disabled*。
/// </summary>
/// <remarks>
/// 调用方已确保在 frame clip 内；本方法在 <see cref="ScrollbarMetrics.ShowScrollbar"/> 为 false 时不画。
/// 不分配 lambda/闭包；hover 由 owner 是否最深命中 + state.LastPointerPoint 现算。
/// </remarks>
internal static class ScrollbarPainter
{
    /// <summary>绘制 track+thumb 两条 <see cref="RectangleCommand"/>。</summary>
    public static void Draw(
        ViewNode node,
        DrawContext context,
        ScrollbarState? state,
        Rect track,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset)
    {
        var trackLength = ScrollbarMetrics.TrackLength(track, horizontal);
        if (!ScrollbarMetrics.ShowScrollbar(viewportExtent, contentExtent, trackLength))
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = node.IsEffectivelyDisabled;
        var thumb = ScrollbarMetrics.ThumbRect(track, horizontal, viewportExtent, contentExtent, offset);

        // hover：owner 是最深命中节点才按缓存 point 重算；子消费/出窗自然 None。
        var hoverKind = ScrollbarTrackHit.None;
        if (!disabled
            && ReferenceEquals(context.Runtime.HoveredNode, node)
            && state?.LastPointerPoint is { } p)
        {
            hoverKind = ScrollbarMetrics.HitTest(track, horizontal, viewportExtent, contentExtent, offset, p);
        }

        var thumbPressed = state?.PressKind == ScrollbarPressKind.Thumb;
        var trackPressed = state?.PressKind == ScrollbarPressKind.Track;
        var trackHovered = hoverKind is ScrollbarTrackHit.Before or ScrollbarTrackHit.After;
        var trackColor = disabled
            ? theme.DisabledBackground
            : trackPressed
                ? theme.ControlPressedBackground
                : trackHovered
                    ? theme.ControlHoverBackground
                    : theme.ControlBackground;
        // 滑块为柔和中性灰——滚动条是导航辅助，非主操作，不继承 Accent。normal 用
        // ControlBackground 与 Foreground 按 0.40 混色；hover/press 依次提高前景混合
        // 比例（0.48/0.56），增强交互对比，保持三态可辨但始终中性。
        var thumbColor = disabled
            ? theme.DisabledForeground
            : thumbPressed
                ? theme.ControlBackground.Lerp(theme.Foreground, 0.56)
                : hoverKind == ScrollbarTrackHit.Thumb
                    ? theme.ControlBackground.Lerp(theme.Foreground, 0.48)
                    : theme.ControlBackground.Lerp(theme.Foreground, 0.40);

        context.Commands.Add(new RectangleCommand(track, trackColor, 0, null, 0, node.Environment.Opacity));
        context.Commands.Add(new RectangleCommand(
            thumb, thumbColor, Math.Min(track.Width, track.Height) / 2, null, 0, node.Environment.Opacity));
    }
}
