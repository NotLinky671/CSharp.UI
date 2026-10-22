namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// TI-07 P3b：候选 overlay 的帧末 OpenGL 呈现，只消费不可变 <see cref="CandidateSnapshot"/>。
/// </summary>
/// <remarks>
/// <para>
/// 快照与几何是<b>两个独立来源</b>：sink 通过 <see cref="SetSnapshot"/>/<see cref="ClearSnapshot"/>
/// 管理候选内容；bridge/host 通过 <see cref="SetGeometry"/>/<see cref="Clear"/> 管理逻辑客户区与
/// 锚点行。几何移动不会使候选重排（测量缓存按快照/字体/DPI/可用宽度复用）。
/// </para>
/// <para>
/// 几何入口只接受<b>逻辑像素</b>（窗口内容区坐标）；屏幕物理像素换算属于 bridge 的 ACP 提供方，
/// 不在这里进行。缺失/非有限/完全裁出的几何返回零命令，绝不伪造原点。
/// </para>
/// <para>
/// 宿主把 <see cref="Draw"/> 接到 <c>UiRuntime.FrameOverlay</c>：它在内容之后、命令快照之前、
/// 同一纹理帧保护内执行，不额外 <c>BeginFrame</c>。
/// </para>
/// </remarks>
internal sealed class CandidateOverlay
{
    internal const double OuterPadding = 6;
    internal const double RowGap = 4;
    internal const double MinimumWidth = 96;
    internal const double CornerRadius = 6;

    private CandidateSnapshot? _snapshot;

    /// <summary>逻辑客户区（窗口内容区坐标）。</summary>
    private Rect _clientBounds;

    /// <summary>锚点行（逻辑像素）；null 表示几何缺失。</summary>
    private Rect? _anchorRow;

    private MeasureCache? _measure;

    private sealed class MeasureCache
    {
        internal CandidateSnapshot? Snapshot;
        internal FontDescription Font;
        internal double PixelScale;
        internal double TextMaxWidth;
        internal TextLayoutResult[] Entries = [];
        internal TextLayoutResult Footer = default;
        internal double RowHeight;
        internal double ContentWidth;
        internal int EntryCount;
    }

    /// <summary>发布当前候选快照；null 表示隐藏候选（End/替换/失焦）。</summary>
    /// <remarks>只影响内容，不改变已设置的几何；相同引用不重复失效测量缓存。</remarks>
    internal void SetSnapshot(CandidateSnapshot? snapshot)
    {
        if (ReferenceEquals(_snapshot, snapshot))
        {
            return;
        }

        _snapshot = snapshot;
        _measure = null;
    }

    /// <summary>发布逻辑几何；<paramref name="anchorRow"/> 为 null 表示几何缺失（隐藏，不伪造）。</summary>
    internal void SetGeometry(Rect clientBounds, Rect? anchorRow)
    {
        _clientBounds = clientBounds;
        _anchorRow = anchorRow;
    }

    /// <summary>End：只撤销候选内容，保留几何（同一输入框随后可能再次出现候选）。</summary>
    internal void ClearSnapshot()
    {
        _snapshot = null;
        _measure = null;
    }

    /// <summary>失焦/epoch/卸载：几何与候选都撤销。</summary>
    internal void Clear()
    {
        ClearSnapshot();
        _clientBounds = default;
        _anchorRow = null;
    }

    /// <summary>帧末追加候选命令；无效状态或几何不可放置时零命令。</summary>
    internal void Draw(DrawContext context)
    {
        var snapshot = _snapshot;
        if (snapshot is not { Valid: true } || snapshot.Entries.Count == 0)
        {
            return;
        }

        if (_anchorRow is not { } row || !IsFinite(row) || row.Height <= 0)
        {
            return;
        }

        var client = _clientBounds;
        if (!IsFinite(client) || client.IsEmpty)
        {
            return;
        }

        // 锚点行必须真实可见：完全裁出（视口/祖先裁剪）的行不能把候选留在屏幕上。
        if (!row.IntersectsWith(client))
        {
            return;
        }

        var entryCount = Math.Min(snapshot.Entries.Count, CandidateSnapshotLimits.MaxPageEntries);
        if (entryCount <= 0)
        {
            return;
        }

        var usableWidth = client.Width - (OuterPadding * 2);
        if (!double.IsFinite(usableWidth) || usableWidth < MinimumWidth)
        {
            return;
        }

        var theme = context.Runtime.Environment.ResolvedTheme;
        var font = context.Runtime.Environment.Font ?? FontDescription.Default;
        var textMaxWidth = Math.Max(0, usableWidth - (OuterPadding * 2));

        var measure = GetMeasure(context, snapshot, font, textMaxWidth, entryCount);
        var rowHeight = measure.RowHeight;
        var footerLayout = measure.Footer;
        if (!double.IsFinite(rowHeight) || rowHeight <= 0
            || !double.IsFinite(footerLayout.Size.Height) || footerLayout.Size.Height <= 0)
        {
            return;
        }

        var width = Math.Clamp(measure.ContentWidth + (OuterPadding * 2),
            Math.Min(MinimumWidth, usableWidth), usableWidth);
        var height = (entryCount * rowHeight) + footerLayout.Size.Height + (OuterPadding * 2);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        var maxHeight = client.Height - (OuterPadding * 2);
        if (!double.IsFinite(maxHeight) || height > maxHeight)
        {
            return;
        }

        var minX = client.Left + OuterPadding;
        var maxX = client.Right - OuterPadding - width;
        if (maxX < minX)
        {
            return;
        }

        var x = Math.Clamp(row.Left, minX, maxX);
        var belowTop = row.Bottom + RowGap;
        double top;
        if (belowTop + height <= client.Bottom - OuterPadding)
        {
            top = belowTop;
        }
        else
        {
            var aboveTop = row.Top - RowGap - height;
            if (aboveTop < client.Top + OuterPadding)
            {
                return;
            }

            top = aboveTop;
        }

        // 最终 client 约束：所有输出都落在客户区内（不继承 TextField 的文字裁剪）。
        var bounds = new Rect(x, top, width, height).Intersect(client);
        if (bounds.IsEmpty || !IsFinite(bounds))
        {
            return;
        }

        var previousClip = context.CurrentClip;
        if (!context.PushClip(bounds))
        {
            return;
        }

        // 异常/提前返回也必须恢复裁剪，绝不泄漏 PushClip。
        try
        {
            context.Commands.Add(new RectangleCommand(
                bounds, theme.Surface, CornerRadius, theme.Border, 1));

            var textWidth = width - (OuterPadding * 2);
            var rowTop = bounds.Y + OuterPadding;
            for (var i = 0; i < entryCount; i++)
            {
                if (i == snapshot.SelectionOnPage)
                {
                    context.Commands.Add(new RectangleCommand(
                        new Rect(bounds.X, rowTop, bounds.Width, rowHeight).Intersect(bounds),
                        theme.SelectionBackground, 0));
                }

                TextPainter.Paint(context, measure.Entries[i], theme.Foreground,
                    new Rect(bounds.X + OuterPadding, rowTop, textWidth, rowHeight));
                rowTop += rowHeight;
            }

            TextPainter.Paint(context, footerLayout, theme.MutedForeground,
                new Rect(bounds.X + OuterPadding, rowTop, textWidth, footerLayout.Size.Height));
        }
        finally
        {
            context.PopClip(previousClip);
        }
    }

    private MeasureCache GetMeasure(DrawContext context, CandidateSnapshot snapshot,
        FontDescription font, double textMaxWidth, int entryCount)
    {
        var cached = _measure;
        if (cached is not null
            && ReferenceEquals(cached.Snapshot, snapshot)
            && cached.Font.Equals(font)
            && cached.PixelScale.Equals(context.PixelScale)
            && cached.TextMaxWidth.Equals(textMaxWidth)
            && cached.EntryCount == entryCount)
        {
            return cached;
        }

        var layouts = new TextLayoutResult[entryCount];
        var rowHeight = 0.0;
        var contentWidth = 0.0;
        for (var i = 0; i < entryCount; i++)
        {
            var layout = TextPainter.Layout(context, EntryLabel(i, snapshot.Entries[i].Text),
                font, textMaxWidth, context.PixelScale);
            layouts[i] = layout;
            rowHeight = Math.Max(rowHeight, layout.Size.Height);
            contentWidth = Math.Max(contentWidth, layout.Size.Width);
        }

        var footer = TextPainter.Layout(context, FooterText(snapshot.CurrentPage),
            font, textMaxWidth, context.PixelScale);
        contentWidth = Math.Max(contentWidth, footer.Size.Width);

        cached = new MeasureCache
        {
            Snapshot = snapshot,
            Font = font,
            PixelScale = context.PixelScale,
            TextMaxWidth = textMaxWidth,
            Entries = layouts,
            Footer = footer,
            RowHeight = rowHeight,
            ContentWidth = contentWidth,
            EntryCount = entryCount,
        };
        _measure = cached;
        return cached;
    }

    private static bool IsFinite(Rect rect)
        => double.IsFinite(rect.X)
            && double.IsFinite(rect.Y)
            && double.IsFinite(rect.Width)
            && double.IsFinite(rect.Height);

    /// <summary>页内可见序号（1 基）+ 候选原文；真实序号只用于数据诊断。</summary>
    internal static string EntryLabel(int pageLocalIndex, string text)
        => $"{pageLocalIndex + 1}. {text}";

    /// <summary>页脚页码（<c>GetCurrentPage</c> 是 0 基，展示 +1）。</summary>
    internal static string FooterText(uint currentPage)
        => currentPage == uint.MaxValue ? "页码不可用" : $"第 {currentPage + 1} 页";
}
