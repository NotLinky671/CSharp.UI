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
/// 滚动条样式：<see cref="Standard"/> 为 persistent 12 逻辑像素 gutter（overflow 恒显，默认）；
/// <see cref="Floating"/> 为覆盖内容边缘的 overlay（不占布局槽、实际滚动显示、约 1 秒无活动隐藏、
/// hover/drag 续命、hidden 完全不拦命中）。
/// </summary>
public enum ScrollbarStyle
{
    /// <summary>persistent gutter 样式：槽位固定占宽、overflow 时常显 track+thumb。</summary>
    Standard,

    /// <summary>overlay 样式：不占布局槽、不画 track、更细 thumb；实际滚动触发显示，无活动约 1 秒后隐藏。</summary>
    Floating,
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

    /// <summary>Floating thumb 的 cross 向宽度（逻辑像素）；比 Standard thumb 更细，命中区仍是整条 track。</summary>
    internal const double FloatingThumbThickness = 4;

    /// <summary>Floating bar 停止滚动活动后的隐藏延迟（内部固定值，不公开）。</summary>
    internal static readonly TimeSpan FloatingHideDelay = TimeSpan.FromMilliseconds(1000);

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

    /// <summary>最近一次 Arrange 同步的几何签名（axis/track/content/viewport）；offset 不入签名。</summary>
    public (ScrollAxis Axis, Rect Track, double Content, double Viewport)? LastGeometry { get; private set; }

    /// <summary>最后观测到的指针位置（point；非 gesture 状态，Cancel/几何取消不清）。</summary>
    public Point? LastPointerPoint { get; set; }

    // ------------------------------------------------------------------ Floating 可见性（最少字段；deadline 归 runtime TimedWakes）

    /// <summary>Floating bar 当前是否可绘/可命中；Standard 下为死值。</summary>
    public bool IsFloatingVisible { get; private set; }

    /// <summary>Floating 隐藏截止时刻（<see cref="IUiClock.Now"/> 域）；null = 未挂 deadline（抑制/隐藏/idle）。</summary>
    public TimeSpan? FloatingHideDeadline { get; private set; }

    /// <summary>hover/drag 抑制中（deadline 不挂）；与 <see cref="FloatingHideDeadline"/> 正交不互镜像。</summary>
    public bool FloatingSuppressed { get; private set; }

    /// <summary>真实滚动到达（Floating）：置可见并（非抑制时）挂/重挂 1000ms deadline。</summary>
    public void NotifyScrolled(ViewNode node, TimeSpan now)
    {
        ShowFloating(node, now);
    }

    /// <summary>置可见并按需挂 deadline；suppress 中只置可见不动计时。</summary>
    public void ShowFloating(ViewNode node, TimeSpan now)
    {
        IsFloatingVisible = true;
        if (!FloatingSuppressed)
        {
            FloatingHideDeadline = now + ScrollbarMetrics.FloatingHideDelay;
            node.Runtime.RequestTimedWake(node, FloatingHideDeadline.Value);
        }
    }

    /// <summary>
    /// 到期回调（owner <c>OnTimedWake</c> 唯一入口；runtime 已剔除槽位）：
    /// 复核 now 仍到期且未被 hover/drag 抑制才隐藏——不满足则不动 deadline/可见性。
    /// </summary>
    public void OnTimedWakeDue(ViewNode node, TimeSpan now)
    {
        if (FloatingHideDeadline is not { } deadline
            || now < deadline
            || FloatingSuppressed
            || PressKind != ScrollbarPressKind.None)
        {
            return;
        }
        FloatingHideDeadline = null;
        IsFloatingVisible = false;
        node.Runtime.InvalidateRender();
    }

    /// <summary>hover 进 bar/drag 开始：抑制计时（deadline 不挂），可见性不动。</summary>
    public void SuppressFloatingTimer(ViewNode node)
    {
        FloatingSuppressed = true;
        if (FloatingHideDeadline is not null)
        {
            FloatingHideDeadline = null;
            node.Runtime.CancelTimedWake(node);
        }
    }

    /// <summary>hover 离开/drag 结束：仍可见则重挂 1000ms deadline。</summary>
    public void RearmFloatingTimer(ViewNode node, TimeSpan now)
    {
        FloatingSuppressed = false;
        if (IsFloatingVisible)
        {
            FloatingHideDeadline = now + ScrollbarMetrics.FloatingHideDelay;
            node.Runtime.RequestTimedWake(node, FloatingHideDeadline.Value);
        }
    }

    /// <summary>完整复位（style 切走/disabled/无 overflow 翻转）。</summary>
    public void ResetFloating(ViewNode node)
    {
        IsFloatingVisible = false;
        FloatingHideDeadline = null;
        FloatingSuppressed = false;
        node.Runtime.CancelTimedWake(node);
    }

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
        // state 惰性创建于首个指针事件，晚于节点首次 Arrange——LastGeometry 可能尚无基线。
        // gesture 起始即写当前签名：首次拖拽不再被下一次 Arrange 的 SyncGeometry 误判为
        // 几何变化而取消；gesture 期间真实 resize/内容漂移仍因签名不同被检出取消。
        LastGeometry = (axis, trackRect, contentExtent, viewportExtent);
    }


    /// <summary>释放当前 gesture（Up/Cancel/几何失效共用）。</summary>
    public void ReleaseGesture()
    {
        PressKind = ScrollbarPressKind.None;
        BarDragAnchor = 0;
    }

    /// <summary>
    /// 几何同步：每次 Arrange 比对 axis/track/content/viewport 签名（offset 不入）——
    /// 真实几何变化才更新签名；有 gesture 时变化额外取消 gesture。无 gesture 仅 hover/idle 也会检出变化。
    /// </summary>
    /// <returns><see cref="ScrollbarSyncResult"/>：无变化 / 仅几何变（未按下）/ 几何变且取消了 gesture。</returns>
    public ScrollbarSyncResult SyncGeometry(ScrollAxis axis, Rect trackRect, double contentExtent, double viewportExtent)
    {
        var signature = (axis, trackRect, contentExtent, viewportExtent);
        if (LastGeometry is { } previous
            && previous.Axis == signature.axis
            && previous.Track == signature.trackRect
            && previous.Content == signature.contentExtent
            && previous.Viewport == signature.viewportExtent)
        {
            return ScrollbarSyncResult.Unchanged;
        }

        LastGeometry = signature;
        if (PressKind == ScrollbarPressKind.None)
        {
            return ScrollbarSyncResult.GeometryChanged;
        }
        ReleaseGesture();
        return ScrollbarSyncResult.GestureCancelled;
    }
}

/// <summary><see cref="ScrollbarState.SyncGeometry"/> 的结果。</summary>
internal enum ScrollbarSyncResult
{
    /// <summary>几何签名一致。</summary>
    Unchanged,

    /// <summary>几何变化且无 gesture 被取消（仅 hover/idle 也检出）。</summary>
    GeometryChanged,

    /// <summary>几何变化且取消了进行中的 gesture。</summary>
    GestureCancelled,
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
    /// <summary>绘制滚动条；<paramref name="style"/>=Floating 时不画 track、thumb 更细且仅在 <see cref="ScrollbarState.IsFloatingVisible"/> 时画。</summary>
    public static void Draw(
        ViewNode node,
        DrawContext context,
        ScrollbarState? state,
        Rect track,
        bool horizontal,
        double viewportExtent,
        double contentExtent,
        double offset,
        ScrollbarStyle style = ScrollbarStyle.Standard)
    {
        var trackLength = ScrollbarMetrics.TrackLength(track, horizontal);
        if (!ScrollbarMetrics.ShowScrollbar(viewportExtent, contentExtent, trackLength))
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = node.IsEffectivelyDisabled;

        // Floating：hidden 完全不画（含 disabled——ResetFloating 已把可见性清掉）。
        if (style == ScrollbarStyle.Floating && state?.IsFloatingVisible != true)
        {
            return;
        }

        var thumb = ScrollbarMetrics.ThumbRect(track, horizontal, viewportExtent, contentExtent, offset);
        if (style == ScrollbarStyle.Floating)
        {
            // 更细 thumb：cross 向改为 FloatingThumbThickness、沿 track cross 居中；命中区仍是整条 track。
            var cross = Math.Min(ScrollbarMetrics.FloatingThumbThickness,
                Math.Max(0, (horizontal ? track.Height : track.Width) - (ScrollbarMetrics.Inset * 2)));
            thumb = horizontal
                ? new Rect(thumb.X, track.Y + ((track.Height - cross) / 2), thumb.Width, cross)
                : new Rect(track.X + ((track.Width - cross) / 2), thumb.Y, cross, thumb.Height);
        }

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

        if (style == ScrollbarStyle.Standard)
        {
            context.Commands.Add(new RectangleCommand(track, trackColor, 0, null, 0, node.Environment.Opacity));
        }
        var thumbRadius = style == ScrollbarStyle.Floating
            ? Math.Min(thumb.Width, thumb.Height) / 2
            : Math.Min(track.Width, track.Height) / 2;
        context.Commands.Add(new RectangleCommand(
            thumb, thumbColor, thumbRadius, null, 0, node.Environment.Opacity));
    }
}
