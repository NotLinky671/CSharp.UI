namespace CSharp.UI.TsfHostCheck;

internal readonly record struct AcpCharCell(int AcpStart, int AcpEnd, TsfRect Rect);

/// <summary>
/// 稳定布局快照：真实字体度量后的每个 UTF-16 码元屏幕单元、视口与滚动偏移。
/// 快照不可变；查询只读它，不触发排版、不修改文档。
/// </summary>
/// <remarks>
/// 快照绑定“布局代数 + 文档版本”。任何文本变化、窗口移动/缩放、滚动或字体/DPI 变化都会让快照失效；
/// 失效后几何查询必须返回 <see cref="TsfHResult.TS_E_NOLAYOUT"/>，绝不返回旧矩形。
/// </remarks>
internal sealed class AcpLayoutSnapshot
{
    internal required long LayoutGeneration { get; init; }

    internal required long DocumentVersion { get; init; }

    internal uint ViewCookie { get; init; } = 1;

    internal required TsfRect ClientScreen { get; init; }

    internal required TsfRect TextRowScreen { get; init; }

    internal int ScrollX { get; init; }

    internal int ScrollY { get; init; }

    internal required IReadOnlyList<AcpCharCell> Cells { get; init; }

    internal int TextLength => Cells.Count;

    internal bool IsCurrent(long layoutGeneration, long documentVersion)
        => LayoutGeneration == layoutGeneration && DocumentVersion == documentVersion;

    /// <summary>范围矩形的完整/可见矩形；返回 false 表示完全裁出（没有任何可见部分）。</summary>
    internal bool TryGetRangeRect(int start, int end, out TsfRect visible, out bool clipped)
    {
        visible = default;
        clipped = false;

        TsfRect full;
        if (start == end)
        {
            full = CaretRectUnclipped(start);
        }
        else
        {
            if (start < 0 || end > Cells.Count || end <= start)
            {
                return false;
            }

            var first = Cells[start].Rect;
            var left = first.Left;
            var top = first.Top;
            var right = first.Right;
            var bottom = first.Bottom;
            for (var i = start + 1; i < end; i++)
            {
                var cell = Cells[i].Rect;
                left = Math.Min(left, cell.Left);
                top = Math.Min(top, cell.Top);
                right = Math.Max(right, cell.Right);
                bottom = Math.Max(bottom, cell.Bottom);
            }

            full = TsfRect.FromLtrb(left, top, right, bottom);
        }

        var intersection = TsfRect.Intersect(full, ClientScreen);
        if (intersection.IsEmpty)
        {
            return false;
        }

        visible = intersection;
        clipped = intersection.Left != full.Left || intersection.Top != full.Top
            || intersection.Right != full.Right || intersection.Bottom != full.Bottom;
        return true;
    }

    /// <summary>空范围的插入点矩形（1px 宽）；完全裁出时返回空矩形。</summary>
    internal TsfRect CaretRect(int acp) => TsfRect.Intersect(CaretRectUnclipped(acp), ClientScreen);

    internal TsfRect CaretRectUnclipped(int acp)
    {
        var x = BoundaryX(acp);
        return TsfRect.FromLtrb(x, TextRowScreen.Top, x + 1, TextRowScreen.Bottom);
    }

    internal int BoundaryX(int acp)
    {
        if (acp <= 0)
        {
            return Cells.Count == 0 ? TextRowScreen.Left : Cells[0].Rect.Left;
        }

        if (acp >= Cells.Count)
        {
            return Cells.Count == 0 ? TextRowScreen.Left : Cells[^1].Rect.Right;
        }

        return Cells[acp].Rect.Left;
    }

    /// <summary>
    /// 屏幕点命中 ACP 边界。行外/视口外且未给 <c>GXFPF_NEAREST</c> 时返回 false；
    /// <c>GXFPF_ROUND_NEAREST</c> 就近取整，否则向下取整到码元起点。
    /// </summary>
    internal bool TryHitTest(TsfPoint point, uint flags, out int acp)
    {
        acp = 0;
        var nearest = (flags & TsfFlags.GXFPF_NEAREST) != 0;
        var round = (flags & TsfFlags.GXFPF_ROUND_NEAREST) != 0;

        var row = TextRowScreen;
        var insideRow = point.Y >= row.Top && point.Y < row.Bottom;
        var insideView = point.X >= ClientScreen.Left && point.X < ClientScreen.Right
            && point.Y >= ClientScreen.Top && point.Y < ClientScreen.Bottom;
        if (!insideView && !nearest)
        {
            return false;
        }

        if (Cells.Count == 0)
        {
            return insideRow || nearest;
        }

        var firstLeft = Cells[0].Rect.Left;
        var lastRight = Cells[^1].Rect.Right;
        if (point.X < firstLeft)
        {
            acp = 0;
            return nearest;
        }

        if (point.X >= lastRight)
        {
            acp = Cells[^1].AcpEnd;
            return nearest;
        }

        if (!insideRow && !nearest)
        {
            return false;
        }

        foreach (var cell in Cells)
        {
            if (cell.Rect.Width <= 0)
            {
                continue;
            }

            if (point.X >= cell.Rect.Left && point.X < cell.Rect.Right)
            {
                acp = round && point.X - cell.Rect.Left >= cell.Rect.Right - point.X
                    ? cell.AcpEnd
                    : cell.AcpStart;
                return true;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 用真实字体前缀宽度构造快照。<paramref name="advances"/> 与文本等长（每个 UTF-16 码元一个宽度）。
    /// </summary>
    internal static AcpLayoutSnapshot Build(
        long layoutGeneration,
        long documentVersion,
        string text,
        int[] advances,
        int rowHeight,
        TsfRect clientScreen,
        int textOriginX,
        int textOriginY,
        int scrollX,
        int scrollY,
        uint viewCookie = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(advances);
        if (advances.Length != text.Length)
        {
            throw new ArgumentException("advances must have one entry per UTF-16 code unit", nameof(advances));
        }

        var cells = new AcpCharCell[text.Length];
        var x = textOriginX - scrollX;
        var top = textOriginY - scrollY;
        var bottom = top + rowHeight;
        for (var i = 0; i < text.Length; i++)
        {
            var right = x + advances[i];
            cells[i] = new AcpCharCell(i, i + 1, TsfRect.FromLtrb(x, top, right, bottom));
            x = right;
        }

        var row = TsfRect.FromLtrb(textOriginX - scrollX, top, x, bottom);
        return new AcpLayoutSnapshot
        {
            LayoutGeneration = layoutGeneration,
            DocumentVersion = documentVersion,
            ViewCookie = viewCookie,
            ClientScreen = clientScreen,
            TextRowScreen = row,
            ScrollX = scrollX,
            ScrollY = scrollY,
            Cells = cells,
        };
    }
}
