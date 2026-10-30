namespace CSharp.UI;

/// <summary>
/// 弹层定位纯计算：输入锚矩形、已按宽约束测量后的内容尺寸、已内缩 margin 的可用边界，
/// 返回 popup 的绝对 Frame；无有效空间时返回 <c>null</c>（调用方应走关闭路径，绝不挂零尺寸 popup）。
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="popupBounds"/> 已是“视口内缩 margin 后的可用矩形”，本函数不再二次内缩；
/// 锚祖先 clip 不参与该边界（只用于调用方的锚可见资格判定），否则弹层无法逃出 ScrollView clip。
/// </para>
/// <para>
/// 两阶段测量契约：本函数不调用 Measure；调用方在最终的 <c>(w,h)</c> 与已测 DesiredSize 不同时，
/// 最多一次以 <c>(w,h)</c> 为上限重新 Measure，再按返回 Frame Arrange。
/// </para>
/// </remarks>
internal static class PopupPlacementCalculator
{
    /// <summary>计算 popup 的最终 Frame；返回 <c>null</c> 表示无有效空间可定位。</summary>
    /// <param name="anchorRect">锚节点的 Frame（绝对坐标，可为部分越出 bounds）。</param>
    /// <param name="contentDesired">popup 内容在 <paramref name="popupBounds"/> 宽约束下测量得到的尺寸。</param>
    /// <param name="popupBounds">已内缩 margin 的可用边界（宽或高非正视为无空间）。</param>
    /// <param name="preference">Auto 可翻转；Above/Below 强制方向并按该侧可用高度截断。</param>
    /// <param name="maxHeight">高度上限（正数或正无穷）；参与 <c>h=min(desired,maxHeight,sideAvail,bounds.H)</c>。</param>
    internal static Rect? Resolve(
        Rect anchorRect,
        Size contentDesired,
        Rect popupBounds,
        PopupPlacement preference,
        double maxHeight)
    {
        if (!IsFinite(anchorRect) || !IsFinite(contentDesired) || !IsFinite(popupBounds))
        {
            return null;
        }

        if (preference is not (PopupPlacement.Auto or PopupPlacement.Above or PopupPlacement.Below))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (popupBounds.IsEmpty || double.IsNaN(maxHeight) || maxHeight <= 0)
        {
            return null;
        }

        var width = Math.Clamp(contentDesired.Width, 0, popupBounds.Width);
        if (width <= 0)
        {
            return null;
        }

        var spaceBelow = Math.Max(0, popupBounds.Bottom - anchorRect.Bottom);
        var spaceAbove = Math.Max(0, anchorRect.Top - popupBounds.Top);

        var side = preference switch
        {
            PopupPlacement.Above => PopupPlacement.Above,
            PopupPlacement.Below => PopupPlacement.Below,
            // Auto：内容原始高度放得下就保留下方；否则选空间更大的一侧（相等时保留下方）。
            _ => contentDesired.Height <= spaceBelow || spaceBelow >= spaceAbove
                ? PopupPlacement.Below
                : PopupPlacement.Above,
        };

        var sideAvailable = side == PopupPlacement.Below ? spaceBelow : spaceAbove;
        var height = Math.Min(contentDesired.Height, Math.Min(maxHeight, Math.Min(sideAvailable, popupBounds.Height)));
        if (height <= 0 || double.IsNaN(height))
        {
            return null;
        }

        var left = Math.Clamp(anchorRect.X, popupBounds.Left, Math.Max(popupBounds.Left, popupBounds.Right - width));
        var unclampedTop = side == PopupPlacement.Below ? anchorRect.Bottom : anchorRect.Top - height;
        var top = Math.Clamp(unclampedTop, popupBounds.Top, Math.Max(popupBounds.Top, popupBounds.Bottom - height));

        var result = new Rect(left, top, width, height);
        return IsFinite(result) ? result : null;
    }

    private static bool IsFinite(Rect rect)
        => double.IsFinite(rect.X) && double.IsFinite(rect.Y)
           && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    private static bool IsFinite(Size size)
        => double.IsFinite(size.Width) && double.IsFinite(size.Height);
}
