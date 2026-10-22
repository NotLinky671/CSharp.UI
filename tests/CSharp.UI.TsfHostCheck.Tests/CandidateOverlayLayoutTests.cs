using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R36：候选 overlay 的几何摆放/文案——真实窗口 <c>TsfHostWindow.ComputeOverlayFacts</c>
/// 调用的<b>同一</b> <see cref="CandidateOverlayLayout"/> 实现，不是测试专用副本。
/// 覆盖审核要求的六个事实：9 行候选字体在真实客户区内放得下且贴行、下方不够时改上方、
/// 页脚真实计入高度、客户区左右边界与最宽候选、放不下时如实不展示且绝不覆盖组合行、
/// 150% DPI（144）逻辑。
/// </summary>
public sealed class CandidateOverlayLayoutTests
{
    // ---- 真实度量常量（GDI 实测 Microsoft YaHei UI，非估算）----
    // 14 逻辑 px @ DPI144 → 21 物理 px → TEXTMETRICW.Height = 28。
    private const int CandidateRowHeight144 = 28;
    // 输入行字号 20 逻辑 px @ DPI144 → 30 物理 px → TEXTMETRICW.Height = 39（实测）。
    // 审核实测行 (123,139)-(211,178) 高 39，正是这个值。
    private const int InputRowHeight144 = 39;
    // TextPaddingY = 14（逻辑常量，直接用作物理像素）。
    private const int RowTopOffset = 14;
    // 760x420 外框 @ DPI144：非客户区 22x56（AdjustWindowRectExForDpi 实测）。
    private const int NonClientWidth144 = 22;
    private const int NonClientHeight144 = 56;
    // 最长候选实测宽度（"9. 中华人民共和国" @ 21px = 170）+ 左右内边距。
    private const int LongestCandidateWidth144 = 170;
    private const int FooterWidth144 = 66;

    private static TsfRect Client144()
        => TsfRect.FromLtrb(80, 125, 80 + 760 - NonClientWidth144,
            125 + 420 - NonClientHeight144);

    private static TsfRect Row144()
    {
        var client = Client144();
        var top = client.Top + RowTopOffset;
        return TsfRect.FromLtrb(client.Left + 16, top, client.Left + 200, top + InputRowHeight144);
    }

    [Fact]
    public void NineEntriesPlusFooterFitBelowRowAt150PercentDpi()
    {
        var client = Client144();
        var row = Row144();

        var placement = CandidateOverlayLayout.Place(client, row, entryCount: 9,
            rowHeight: CandidateRowHeight144, contentWidth: LongestCandidateWidth144,
            footerHeight: CandidateRowHeight144);

        Assert.True(placement.Show, placement.Failure);
        Assert.False(placement.Above, "空间充足时必须优先贴组合行下方");
        Assert.True(CandidateOverlayLayout.WithinClient(placement.Rect, client));
        Assert.False(CandidateOverlayLayout.OverlapsRow(placement.Rect, row));

        // 贴行下方：候选顶边正好在行底边 + RowGap。
        Assert.Equal(row.Bottom + CandidateOverlayLayout.RowGap, placement.Rect.Top);

        // 高度 = 9 行 + 1 行页脚 + 上下内边距（页脚真实计入，不是 9 行高度）。
        Assert.Equal(9 * CandidateRowHeight144 + CandidateRowHeight144
            + CandidateOverlayLayout.OuterPadding * 2, placement.Rect.Height);

        // 宽度 = 最长候选实测 + 左右内边距。
        Assert.Equal(LongestCandidateWidth144 + CandidateOverlayLayout.OuterPadding * 2,
            placement.Rect.Width);
    }

    [Fact]
    public void FooterWidthCountsButMinimumWidthStillApplies()
    {
        var client = Client144();
        var row = Row144();

        // 页脚实测 66 + 内边距 12 = 78，低于最小宽度 96 → 取最小宽度。
        var placement = CandidateOverlayLayout.Place(client, row, entryCount: 9,
            rowHeight: CandidateRowHeight144,
            contentWidth: FooterWidth144, footerHeight: CandidateRowHeight144);

        Assert.True(placement.Show, placement.Failure);
        Assert.Equal(CandidateOverlayLayout.MinimumWidth, placement.Rect.Width);

        // 内容比最小宽度宽时按实测内容走（页脚/候选哪个更宽取哪个）。
        var wider = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, contentWidth: 200, footerHeight: CandidateRowHeight144);
        Assert.Equal(200 + CandidateOverlayLayout.OuterPadding * 2, wider.Rect.Width);
    }

    [Fact]
    public void ZeroFooterHeightAccountsForNoFooter()
    {
        var client = Client144();
        var row = Row144();

        var withFooter = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, CandidateRowHeight144);
        var withoutFooter = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, 0);

        Assert.True(withFooter.Show);
        Assert.True(withoutFooter.Show);
        Assert.Equal(CandidateRowHeight144,
            withFooter.Rect.Height - withoutFooter.Rect.Height);
    }

    [Fact]
    public void GoesAboveRowWhenBelowDoesNotFit()
    {
        var client = Client144();

        // 行被滚到客户区下部（真实可能：长文档滚动后组合行靠底）：
        // 9 行 + 页脚 + 内边距 = 292，下方只剩 14px，上方还有足够空间。
        var row = TsfRect.FromLtrb(client.Left + 16, client.Top + 310,
            client.Left + 200, client.Top + 310 + InputRowHeight144);

        var placement = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, CandidateRowHeight144);

        Assert.True(placement.Show, placement.Failure);
        Assert.True(placement.Above, "下方放不下时必须改到行上方");
        Assert.True(CandidateOverlayLayout.WithinClient(placement.Rect, client));
        Assert.False(CandidateOverlayLayout.OverlapsRow(placement.Rect, row));
        Assert.Equal(row.Top - CandidateOverlayLayout.RowGap, placement.Rect.Bottom);
    }

    [Fact]
    public void ImpossibleFitFailsInsteadOfCoveringRow()
    {
        // 客户区本身够高（>= 292+12），但组合行正好在中部：上下都塞不下 292。
        // 任何"夹到客户区顶部"的旧做法都会得到一个压在组合行上的矩形。
        var client = TsfRect.FromLtrb(80, 125, 80 + 738, 125 + 400);
        var row = TsfRect.FromLtrb(client.Left + 16, client.Top + 150,
            client.Left + 200, client.Top + 150 + InputRowHeight144);

        var placement = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, CandidateRowHeight144);

        Assert.False(placement.Show);
        Assert.Contains("no-room-below-or-above", placement.Failure);
        Assert.True(placement.Rect.IsEmpty, "不展示时必须返回空矩形，不伪造位置");
        // 关键回归：绝不返回一个压在组合行上的矩形。
        Assert.False(CandidateOverlayLayout.OverlapsRow(placement.Rect, row));
    }

    [Fact]
    public void LongestCandidateIsClampedToClientWidthNotOverflowing()
    {
        var client = Client144();
        var row = Row144();

        // 内容远比客户区宽：宽度收进客户区，绝不溢出右边界。
        var placement = CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, contentWidth: 5000, footerHeight: CandidateRowHeight144);

        Assert.True(placement.Show, placement.Failure);
        Assert.True(CandidateOverlayLayout.WithinClient(placement.Rect, client));
        Assert.Equal(client.Width - CandidateOverlayLayout.OuterPadding * 2, placement.Rect.Width);
    }

    [Fact]
    public void ClientTooNarrowOrShortFailsHonestly()
    {
        var row = Row144();

        var narrow = CandidateOverlayLayout.Place(
            TsfRect.FromLtrb(0, 0, 100, 600), row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, CandidateRowHeight144);
        Assert.False(narrow.Show);
        Assert.Contains("client-too-narrow", narrow.Failure);

        var short_ = CandidateOverlayLayout.Place(
            TsfRect.FromLtrb(0, 0, 700, 100), row, 9,
            CandidateRowHeight144, LongestCandidateWidth144, CandidateRowHeight144);
        Assert.False(short_.Show);
        Assert.Contains("client-too-short", short_.Failure);
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        var client = Client144();
        var row = Row144();

        Assert.False(CandidateOverlayLayout.Place(client, row, 0,
            CandidateRowHeight144, 100, CandidateRowHeight144).Show);
        Assert.False(CandidateOverlayLayout.Place(client, row, 9,
            0, 100, CandidateRowHeight144).Show);
        Assert.False(CandidateOverlayLayout.Place(client, row, 9,
            CandidateRowHeight144, 100, -1).Show);
        Assert.False(CandidateOverlayLayout.Place(default, row, 9,
            CandidateRowHeight144, 100, CandidateRowHeight144).Show);
    }

    [Fact]
    public void HundredPercentDpiAlsoFitsBelowWithCompactFont()
    {
        // DPI96：候选字体 14px → 行高 20；客户区 760x420 外框 - 16x39。
        var client = TsfRect.FromLtrb(80, 111, 80 + 760 - 16, 111 + 420 - 39);
        var row = TsfRect.FromLtrb(client.Left + 16, client.Top + RowTopOffset,
            client.Left + 200, client.Top + RowTopOffset + 20);

        var placement = CandidateOverlayLayout.Place(client, row, 9,
            rowHeight: 20, contentWidth: 113, footerHeight: 20);

        Assert.True(placement.Show, placement.Failure);
        Assert.False(placement.Above);
        Assert.True(CandidateOverlayLayout.WithinClient(placement.Rect, client));
        Assert.False(CandidateOverlayLayout.OverlapsRow(placement.Rect, row));
    }

    /// <summary>
    /// R36 关键回归：审核实测的旧失败——9 行沿用输入字号（39px 行高）时高 377，
    /// 下方放不下就被夹到客户区顶部 (123,80)-(243,457)，覆盖了 (123,139)-(211,178)
    /// 的组合行。新实现用 21px 候选字体，同一 760x420/DPI144 几何必须<b>贴行下方</b>
    /// 且不碰组合行。
    /// </summary>
    [Fact]
    public void OldFailureGeometryNowFitsBelowWithoutCoveringRow()
    {
        // 真实 760x420 外框 @ DPI144 → 客户区 (91,125)-(829,489)。
        var client = TsfRect.FromLtrb(91, 125, 829, 489);
        // 真实文本行 (123,139)-(211,178)，高 39 = 输入字号行高。
        var row = TsfRect.FromLtrb(123, 139, 211, 178);

        // 旧行为：entryCount * (39 + 2) + 8 = 377 → 下方放不下 → 夹到顶部覆盖行。
        // 新行为：entryCount * 28 + 28 + 12 = 292，下方仍有 311px 空间。
        var placement = CandidateOverlayLayout.Place(client, row, 9,
            rowHeight: CandidateRowHeight144, contentWidth: LongestCandidateWidth144,
            footerHeight: CandidateRowHeight144);

        Assert.True(placement.Show, placement.Failure);
        Assert.False(placement.Above, "下方空间充足，不得上翻");
        Assert.Equal(292, placement.Rect.Height);
        Assert.Equal(row.Bottom + CandidateOverlayLayout.RowGap, placement.Rect.Top);
        Assert.Equal(182, placement.Rect.Top);

        // 绝不覆盖组合行，且完整落在真实客户区内。
        Assert.False(CandidateOverlayLayout.OverlapsRow(placement.Rect, row));
        Assert.True(CandidateOverlayLayout.WithinClient(placement.Rect, client));
        Assert.True(placement.Rect.Bottom <= client.Bottom);
    }

    [Fact]
    public void XIsClampedToClientLeftAndRightEdges()
    {
        var client = Client144();
        var row = Row144();

        // 行从客户区左边界之外开始：候选左边界必须收进内边距内。
        var farLeft = CandidateOverlayLayout.Place(client,
            TsfRect.FromLtrb(client.Left - 500, row.Top, client.Left - 400, row.Bottom),
            9, CandidateRowHeight144, 100, CandidateRowHeight144);
        Assert.True(farLeft.Show, farLeft.Failure);
        Assert.Equal(client.Left + CandidateOverlayLayout.OuterPadding, farLeft.Rect.Left);

        // 行超出右边界：候选右边界必须收进客户区。
        var farRight = CandidateOverlayLayout.Place(client,
            TsfRect.FromLtrb(client.Right + 100, row.Top, client.Right + 300, row.Bottom),
            9, CandidateRowHeight144, 100, CandidateRowHeight144);
        Assert.True(farRight.Show, farRight.Failure);
        Assert.True(CandidateOverlayLayout.WithinClient(farRight.Rect, client));
    }

    [Fact]
    public void EntryLabelUsesPageLocalOneBasedIndex()
    {
        // 页内序号 1..9（用户看到的当前页顺序），不把真实候选序号当可见标签。
        Assert.Equal("1. 你好", CandidateOverlayLayout.EntryLabel(0, "你好"));
        Assert.Equal("9. 你好", CandidateOverlayLayout.EntryLabel(8, "你好"));
    }

    [Fact]
    public void FooterTextIsOneBasedAndNotDebugJargon()
    {
        // GetCurrentPage 原值 0 基 → 页脚展示 1 基。
        Assert.Equal("第 1 页", CandidateOverlayLayout.FooterText(0));
        Assert.Equal("第 9 页", CandidateOverlayLayout.FooterText(8));

        // 不可用页码如实说明，不显示 uint.MaxValue。
        Assert.Equal("页码不可用", CandidateOverlayLayout.FooterText(uint.MaxValue));

        var footer = CandidateOverlayLayout.FooterText(0);
        Assert.DoesNotContain("sel=", footer);
        Assert.DoesNotContain("n=", footer);
    }

    [Fact]
    public void CandidateFontIsCompactAndSeparateFromInputFont()
    {
        // 候选字号独立且更紧凑：14 逻辑 px；DPI144 → 21 物理 px（不是输入行的 30）。
        Assert.Equal(14, DpiUnits.CandidateFontLogicalPx);
        Assert.Equal(21, DpiUnits.CandidateFontPixelHeight(144));
        Assert.Equal(14, DpiUnits.CandidateFontPixelHeight(96));
        Assert.Equal(28, DpiUnits.CandidateFontPixelHeight(192));

        Assert.NotEqual(DpiUnits.FontPixelHeight(144), DpiUnits.CandidateFontPixelHeight(144));
        Assert.True(DpiUnits.CandidateFontPixelHeight(144) < DpiUnits.FontPixelHeight(144));
    }
}
