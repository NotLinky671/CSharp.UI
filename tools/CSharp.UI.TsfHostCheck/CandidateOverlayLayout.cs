namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R36：一次候选 overlay 摆放的<b>真实结果</b>。<see cref="Show"/> 为 false 时
/// <see cref="Rect"/> 是空矩形且 <see cref="Failure"/> 说明确实放不下的原因——
/// 绝不用"夹到某个角落"伪造一次可见。
/// </summary>
internal readonly record struct CandidateOverlayPlacement(
    bool Show, TsfRect Rect, bool Above, string? Failure)
{
    internal static CandidateOverlayPlacement Hidden(string failure)
        => new(false, default, false, failure);
}

/// <summary>
/// R36：候选 overlay 的<b>几何摆放与文案</b>——纯函数，真实窗口
/// <c>TsfHostWindow.ComputeOverlayFacts</c> 与单元测试调用同一实现（不是平行逻辑）。
/// </summary>
/// <remarks>
/// 不变量：
/// <list type="bullet">
/// <item>尺寸来自<b>真实字体度量</b>：<c>rowHeight</c> 是候选字体 <c>TEXTMETRICW</c> 行高，
/// <c>contentWidth</c> 是同一字体实测的"页内序号 + 候选"与页脚宽度上限；
/// <c>footerHeight</c> 是一整行同字体高度。三者都由绘制所用的同一个 <see cref="GdiFont"/> 产出。</item>
/// <item>边界是 root 的<b>真实客户区</b>（GetClientRect + ClientToScreen），不含标题栏。</item>
/// <item>y 只取"行下方"或"行上方"两种真实解，且与组合行之间留 <see cref="RowGap"/>——
/// 任何情况下都不夹到客户区顶部去覆盖正在组合的文字行。</item>
/// <item>两种解都放不下时如实返回 <see cref="CandidateOverlayPlacement.Hidden"/>，
/// 由调用方隐藏 overlay 并记录失败；不放大锚定容差、不降级成"部分可见"。</item>
/// </list>
/// </remarks>
internal static class CandidateOverlayLayout
{
    /// <summary>候选矩形内边距（上下左右同一值，计入总高/总宽）。</summary>
    internal const int OuterPadding = 6;

    /// <summary>候选矩形与组合文字行之间的最小真实间距。</summary>
    internal const int RowGap = 4;

    /// <summary>候选矩形最小宽度；仍受客户区宽度上限约束。</summary>
    internal const int MinimumWidth = 96;

    /// <summary>
    /// 计算候选矩形。参数全部是<b>实测</b>物理像素：
    /// <paramref name="clientScreen"/> root 真实客户区（屏幕坐标）、
    /// <paramref name="rowScreen"/> 已发布文本行（屏幕坐标）、
    /// <paramref name="entryCount"/> 本页实际条目数（1..<see cref="HostDrawnCandidatePresenter.MaxPageEntries"/>）、
    /// <paramref name="rowHeight"/> 候选字体行高、
    /// <paramref name="contentWidth"/> 候选字体实测的最宽"序号 + 候选"（或页脚）宽度、
    /// <paramref name="footerHeight"/> 一整行同字体页脚高度（0 = 无页脚）。
    /// </summary>
    internal static CandidateOverlayPlacement Place(
        TsfRect clientScreen, TsfRect rowScreen, int entryCount,
        int rowHeight, int contentWidth, int footerHeight)
    {
        if (entryCount <= 0)
        {
            return CandidateOverlayPlacement.Hidden("no-entries");
        }

        if (rowHeight <= 0)
        {
            return CandidateOverlayPlacement.Hidden($"invalid-row-height({rowHeight})");
        }

        if (footerHeight < 0)
        {
            return CandidateOverlayPlacement.Hidden($"invalid-footer-height({footerHeight})");
        }

        if (clientScreen.IsEmpty)
        {
            return CandidateOverlayPlacement.Hidden("client-empty");
        }

        var maxWidth = clientScreen.Width - OuterPadding * 2;
        if (maxWidth < MinimumWidth)
        {
            return CandidateOverlayPlacement.Hidden(
                $"client-too-narrow(width={clientScreen.Width} usable={maxWidth} min={MinimumWidth})");
        }

        // 宽度：内容实测 + 左右内边距，收进客户区；下限是最小宽度（受可用宽度约束）。
        var width = Math.Clamp(Math.Max(contentWidth, 0) + OuterPadding * 2,
            Math.Min(MinimumWidth, maxWidth), maxWidth);

        // 高度：真实行高 * 条目数 + 一整行页脚 + 上下内边距。
        var height = entryCount * rowHeight + footerHeight + OuterPadding * 2;
        var maxHeight = clientScreen.Height - OuterPadding * 2;
        if (height > maxHeight)
        {
            return CandidateOverlayPlacement.Hidden(
                $"client-too-short(height={height} usable={maxHeight})");
        }

        var x = Math.Clamp(rowScreen.Left, clientScreen.Left + OuterPadding,
            clientScreen.Right - OuterPadding - width);

        // 优先贴组合行下方；下方放不下才改上方；两者都放不下就如实失败。
        var belowTop = rowScreen.Bottom + RowGap;
        if (belowTop + height <= clientScreen.Bottom - OuterPadding)
        {
            return Visible(x, belowTop, width, height, above: false);
        }

        var aboveTop = rowScreen.Top - RowGap - height;
        if (aboveTop >= clientScreen.Top + OuterPadding)
        {
            return Visible(x, aboveTop, width, height, above: true);
        }

        return CandidateOverlayPlacement.Hidden(
            $"no-room-below-or-above(row={rowScreen} client={clientScreen} "
            + $"height={height} below_top={belowTop} above_top={aboveTop})");
    }

    /// <summary>页内可见序号标签（1..N，用户看到的当前页序号）+ 候选原文。</summary>
    internal static string EntryLabel(int pageLocalIndex, string text)
        => $"{pageLocalIndex + 1}. {text}";

    /// <summary>
    /// 页脚文案：<b>1 基</b>页码（<c>GetCurrentPage</c> 原值 0 基，展示时 +1）。
    /// 不用 <c>sel=</c>/<c>n=</c> 这类调试缩写——页脚是给用户看的。
    /// </summary>
    internal static string FooterText(uint currentPage)
        => currentPage == uint.MaxValue ? "页码不可用" : $"第 {currentPage + 1} 页";

    /// <summary>候选矩形是否与组合行真实重叠（测试/诊断用；真实摆放必须为 false）。</summary>
    internal static bool OverlapsRow(TsfRect rect, TsfRect rowScreen)
        => rect.Left < rowScreen.Right && rowScreen.Left < rect.Right
            && rect.Top < rowScreen.Bottom && rowScreen.Top < rect.Bottom;

    /// <summary>候选矩形是否完整落在客户区内（含内边距）。</summary>
    internal static bool WithinClient(TsfRect rect, TsfRect clientScreen)
        => rect.Left >= clientScreen.Left + OuterPadding
            && rect.Top >= clientScreen.Top + OuterPadding
            && rect.Right <= clientScreen.Right - OuterPadding
            && rect.Bottom <= clientScreen.Bottom - OuterPadding;

    private static CandidateOverlayPlacement Visible(int x, int y, int width, int height, bool above)
        => new(true, TsfRect.FromLtrb(x, y, x + width, y + height), above, null);
}
