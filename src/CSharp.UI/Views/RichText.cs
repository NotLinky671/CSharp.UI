namespace CSharp.UI;

/// <summary>
/// 只读富文本视图：同一文本块内的多样式区间（字体/颜色/装饰）与交互区间。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StyledText.HasRichContent"/> 为 false（无任何区间）时走 <see cref="ITextService"/>
/// 的 plain 路径，与 <see cref="Text"/> 的测量与绘制等价，不产生富文本 handle 或 paint 分配。
/// 富文本路径需要宿主提供 <see cref="UiRuntimeServices.RichTextService"/>（默认装配复用
/// 同时实现两接口的 <c>CSharp.UI.Skia.SkiaTextService</c>）。
/// </para>
/// <para>
/// 排版结果（<see cref="RichTextLayoutResult"/> handle）由视图节点持有：内容、环境字体、
/// 排版参数或宽度变化时重新排版；仅颜色/装饰/payload/环境前景变化时复用同一 handle 并只
/// 重算绘制属性（<see cref="RichTextPaint"/>）——不重排版。背景、下划线与删除线以矩形
/// 命令绘制，文字本体是单张预乘 RGBA 纹理（按 <see cref="RichLayoutIdentity"/> + paint 缓存）。
/// </para>
/// </remarks>
public sealed class RichText : View
{
    /// <summary>由展示文本模型创建。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> 为 null。</exception>
    public RichText(StyledText content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>展示文本与样式/交互区间。修改内容或区间端点会使布局失效；仅颜色/payload 变化不重排版。</summary>
    public StyledText Content { get; init; }

    /// <summary>段落默认字体；null 表示继承环境字体。</summary>
    public FontDescription? Font { get; init; }

    /// <summary>水平对齐（在排列矩形内）。</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>换行方式；默认 <see cref="TextWrapping.Wrap"/>（富文本以多行展示为主场景）。</summary>
    public TextWrapping Wrapping { get; init; } = TextWrapping.Wrap;

    /// <summary>行高（逻辑像素）；0 表示按行内最大字高。显式值小于行内字高时按字高钳制。</summary>
    public double LineHeight { get; init; }

    /// <summary>字距（逻辑像素）。</summary>
    public double LetterSpacing { get; init; }

    /// <summary>
    /// 链接悬停时的 paint 覆盖（只含颜色与装饰字段，不改字体因此不重排版）；
    /// 命中由 RT-04 的交互状态机驱动，悬停 id 命中时对应区间按本样式重算 paint。
    /// </summary>
    public TextPaintStyle? LinkHoverStyle { get; init; }

    /// <summary>链接悬停进入/离开回调（RT-04 驱动；payload 为 null 表示离开）。</summary>
    public Action<object?>? OnLinkHover { get; init; }
    /// <summary>链接激活回调（按下并释放于同一交互 Id 时触发；payload 即 <see cref="TextInteractionRange.Payload"/>）。</summary>
    public Action<object?>? OnLinkActivate { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"RichText(\"{Text.Ellipsis(Content.Text, 24)}\")";

    /// <summary>返回设置了段落字体的副本。</summary>
    public RichText WithFont(FontDescription font) => Copy(font: font);

    /// <summary>返回指定对齐的副本。</summary>
    public RichText Align(TextAlignment alignment) => Copy(alignment: alignment);

    /// <summary>返回指定换行方式的副本。</summary>
    public RichText WithWrapping(TextWrapping wrapping) => Copy(wrapping: wrapping);

    /// <summary>返回指定行高的副本。</summary>
    public RichText WithLineHeight(double lineHeight) => Copy(lineHeight: lineHeight);

    /// <summary>返回指定字距的副本。</summary>
    public RichText WithLetterSpacing(double letterSpacing) => Copy(letterSpacing: letterSpacing);

    private RichText Copy(
        StyledText? content = null,
        FontDescription? font = null,
        TextAlignment? alignment = null,
        TextWrapping? wrapping = null,
        double? lineHeight = null,
        double? letterSpacing = null,
        TextPaintStyle? linkHoverStyle = null,
        Action<object?>? onLinkHover = null,
        Action<object?>? onLinkActivate = null)
        => new(content ?? Content)
        {
            Font = font ?? Font,
            Alignment = alignment ?? Alignment,
            Wrapping = wrapping ?? Wrapping,
            LineHeight = lineHeight ?? LineHeight,
            LetterSpacing = letterSpacing ?? LetterSpacing,
            LinkHoverStyle = linkHoverStyle ?? LinkHoverStyle,
            OnLinkHover = onLinkHover ?? OnLinkHover,
            OnLinkActivate = onLinkActivate ?? OnLinkActivate,
        };

    // 布局属性等价 = GeometryKey 等价：分段拓扑 + 每段几何签名（TopologyEquals 已含
    // Family/Size/Weight/Italic 签名，颜色/装饰/payload 值不参与）+ 段落字体声明 + 排版参数。
    // 仅换色/payload 的新描述通过 OnUpdate 走 paint 刷新，不触发重排版。
    internal override bool LayoutPropertiesEquivalent(View other)
        => other is RichText rich
            && Equals(rich.Font, Font)
            && rich.Alignment == Alignment
            && rich.Wrapping == Wrapping
            && rich.LineHeight == LineHeight
            && rich.LetterSpacing == LetterSpacing
            && rich.Content.Text == Content.Text
            && rich.Content.TopologyEquals(Content);

    /// <summary>构造富文本排版请求（GeometryKey；颜色/payload 不参与）。</summary>
    private RichTextLayoutRequest CreateRichRequest(ViewNode node, LayoutConstraints constraints, double pixelScale)
        => new()
        {
            Text = Content,
            ParagraphFont = Font ?? node.Environment.Font ?? FontDescription.Default,
            MaxWidth = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity,
            Wrapping = Wrapping,
            Alignment = Alignment,
            LineHeight = LineHeight,
            PixelScale = pixelScale,
            LetterSpacing = LetterSpacing,
        };

    /// <summary>构造 plain 排版请求（与 <see cref="Text"/> 的请求规则一致）。</summary>
    private TextLayoutRequest CreatePlainRequest(ViewNode node, LayoutConstraints constraints, double pixelScale)
        => new(Content.Text)
        {
            Font = Font ?? node.Environment.Font ?? FontDescription.Default,
            MaxWidth = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity,
            MaxLines = Wrapping == TextWrapping.NoWrap ? 1 : int.MaxValue,
            Wrapping = Wrapping,
            Trimming = TextTrimming.None,
            Alignment = Alignment,
            LineHeight = LineHeight,
            LetterSpacing = LetterSpacing,
            PixelScale = pixelScale,
        };

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        // plain 快速路径：ViewCache 直接持 TextLayoutResult（union），无任何富文本对象分配。
        if (!Content.HasRichContent)
        {
            var plain = context.TextService.Layout(CreatePlainRequest(node, constraints, context.PixelScale));
            node.ViewCache = plain;
            node.DesiredSize = plain.Size;
            return;
        }

        var service = context.Runtime.Services.RichTextService
            ?? throw new InvalidOperationException(
                "RichText 含有样式或交互区间，需要富文本排版服务；" +
                "请通过 UiRuntimeServices.RichTextService 注入（默认装配复用同实例的 SkiaTextService）。");

        var request = CreateRichRequest(node, constraints, context.PixelScale);
        var paragraphForeground = node.Environment.ResolvedForeground;

        // 探测（BeginProbe）的结果只在本作用域内有效：退出时测量快照把原 ViewCache 引用
        // 原样放回节点（Contexts.cs 快照语义），绝不能在这里替换或 Dispose 已提交的
        // handle（否则恢复后句柄是死引用），也不能把探测新建的 handle 留在节点上（泄漏
        // caller 份额）。本路径用临时 handle 取尺寸后立即归还——命中服务缓存时返回的
        // 是同 core 的新份额，不会重复排版也不泄漏 lease。
        // 自然尺寸测量（Intrinsic，Stacks 的基准遍）不是探测：结果无快照恢复，照常提交
        // cache——与 plain Text 在 intrinsic 遍缓存排版结果同一语义。
        if (context.IsProbing)
        {
            var probeHandle = service.Layout(request);
            try
            {
                node.DesiredSize = probeHandle.Size;
            }
            finally
            {
                probeHandle.Dispose();
            }

            return;
        }
        if (node.ViewCache is RichTextViewCache cache
            && cache.Request is { } cachedRequest
            && cache.Layout is { } handle
            && request.Equals(cachedRequest))
        {
            // GeometryKey 相同：复用现有 handle，不重复 Layout/AddRef。
            // 环境前景可能在缓存复用路径上变化（命中分支不重跑 Measure 前的环境同步仍由
            // 框架保证 node.Environment 最新）；paint 语义变化在提交路径即时重算。
            if (cache.ParagraphForeground != paragraphForeground)
            {
                cache.ParagraphForeground = paragraphForeground;
                cache.Paint = ResolvePaint(node, handle, HoveredId(node));
            }

            node.DesiredSize = handle.Size;
            return;
        }


        // 提交路径：换 handle 或首次建立。旧 handle（本节点持有份额）先归还再覆盖槽位。
        var layout = service.Layout(request);
        var paint = ResolvePaint(node, layout, HoveredId(node));
        if (node.ViewCache is RichTextViewCache existing)
        {
            existing.Set(layout, request, paint, paragraphForeground);
        }
        else
        {
            node.ViewCache = new RichTextViewCache(layout, request, paint, paragraphForeground);
        }

        node.DesiredSize = layout.Size;
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node))
        {
            return;
        }

        if (node.ViewCache is RichTextViewCache richCache && richCache.Layout is { } layout && richCache.Paint is { } paint)
        {
            EmitRich(node, context, layout, paint);
        }
        else if (node.ViewCache is TextLayoutResult plain)
        {
            EmitPlain(node, context, plain);
        }
    }

    // plain 快速路径：与 Text.Emit 等价（Alpha8 遮罩 + tint）。
    private void EmitPlain(ViewNode node, DrawContext context, TextLayoutResult layout)
    {
        var textureService = context.TextureService;
        if (textureService is null || layout.Size.IsEmpty)
        {
            return;
        }

        var frame = node.Frame;
        var origin = TextPainter.ResolveOrigin(frame, layout.Size, Alignment, layout.PixelScale);
        var color = node.Environment.ResolvedForeground;
        var maskColor = color.WithAlpha(255);
        var key = new TextTextureKey(
            layout.Text,
            layout.Font,
            CreatePlainRequest(node, node.LastConstraints, context.PixelScale).MaxWidth,
            int.MaxValue,
            Wrapping,
            TextTrimming.None,
            Alignment,
            LineHeight,
            context.PixelScale,
            LetterSpacing,
            maskColor);
        var handle = textureService.GetOrCreate(key, () => context.TextService.Rasterize(layout, maskColor));
        if (!handle.IsValid)
        {
            return;
        }

        context.Commands.Add(new TextureCommand(
            handle,
            TextPainter.ResolveTextureBounds(handle, origin, layout.PixelScale),
            new Rect(0, 0, handle.PixelWidth, handle.PixelHeight),
            color,
            node.Environment.Opacity));
    }

    // 富文本路径：背景矩形 → 整段 RGBA 纹理 → underline → strikethrough。
    // paint 已在 ViewCache 中解算完毕，本方法不重建任何 runs/paint/glyph 集合。
    // segment 区间不跨模型端点；行内矩形用 segment 的行内 advance（X/Width 已 trim——
    // 排版器以行 TrimEnd 为段终点，行尾空白不产生 segment）。
    private void EmitRich(ViewNode node, DrawContext context, RichTextLayoutResult layout, RichTextPaint paint)
    {
        var textureService = context.TextureService;
        if (textureService is null || layout.Size.IsEmpty)
        {
            return;
        }

        var service = context.Runtime.Services.RichTextService;
        if (service is null)
        {
            return;
        }

        var frame = node.Frame;
        var opacity = node.Environment.Opacity;
        var lines = layout.Lines;
        var segments = layout.Segments;
        var fonts = layout.ResolvedFonts;
        var runs = paint.Runs;
        var origin = TextPainter.ResolveOrigin(frame, layout.Size, Alignment, layout.PixelScale);

        // 1. 背景：同一行内相邻同色 segment 合并为一次矩形命令。
        var pendingStart = -1;
        var pendingColor = Color.Transparent;
        var pendingLine = -1;
        for (var i = 0; i <= segments.Count; i++)
        {
            var current = Color.Transparent;
            var lineIndex = -1;
            if (i < segments.Count)
            {
                var candidate = runs[i].Background;
                lineIndex = segments[i].LineIndex;
                if (candidate.A > 0)
                {
                    current = candidate;
                }
            }

            // 在行边界或颜色变化处收尾（相邻定义 = 同一行内连续）。
            if (pendingStart >= 0 && (lineIndex != pendingLine || current != pendingColor))
            {
                var line = lines[pendingLine];
                var left = segments[pendingStart].X;
                var last = segments[i - 1];
                var right = last.X + last.Width;
                context.Commands.Add(new RectangleCommand(
                    new Rect(origin.X + left, origin.Y + line.Bounds.Y, Math.Max(0, right - left), line.Bounds.Height),
                    pendingColor,
                    Opacity: opacity));
                pendingStart = -1;
            }

            if (i < segments.Count && current != Color.Transparent)
            {
                if (pendingStart < 0)
                {
                    pendingStart = i;
                    pendingColor = current;
                    pendingLine = lineIndex;
                }
            }
        }

        // 2. 文本纹理：墨迹可越出 advance 盒（负 bearing/斜体尾画），纹理按 RasterBounds
        // 栅格化并放在排版原点 + RasterOrigin——位图像素 (0,0) 对应逻辑坐标 RasterOrigin。
        var key = new RichTextTextureKey(layout.LayoutIdentity, paint, context.PixelScale);
        var handle = textureService.GetOrCreate(key, () => service.Rasterize(layout, paint));
        if (handle.IsValid)
        {
            var textureOrigin = new Point(origin.X + layout.RasterOrigin.X, origin.Y + layout.RasterOrigin.Y);
            context.Commands.Add(new TextureCommand(
                handle,
                TextPainter.ResolveTextureBounds(handle, textureOrigin, layout.PixelScale),
                new Rect(0, 0, handle.PixelWidth, handle.PixelHeight),
                Color.White,
                opacity));
        }

        // 3. underline → 4. strikethrough：装饰按段字号在 baseline 附近定位，行高只作上限。
        for (var i = 0; i < segments.Count; i++)
        {
            var run = runs[i];
            if (!run.Underline || run.Foreground.A == 0)
            {
                continue;
            }

            var segment = segments[i];
            var size = fonts[segment.FontId].Size;
            var thickness = Math.Max(1.0, Math.Round(size / 16.0));
            var top = Math.Min(
                segment.Baseline + Math.Max(thickness, size / 14.0),
                lines[segment.LineIndex].Bounds.Bottom - thickness);
            context.Commands.Add(new RectangleCommand(
                new Rect(origin.X + segment.X, origin.Y + top, segment.Width, thickness),
                run.Foreground,
                Opacity: opacity));
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var run = runs[i];
            if (!run.Strikethrough || run.Foreground.A == 0)
            {
                continue;
            }

            var segment = segments[i];
            var size = fonts[segment.FontId].Size;
            var thickness = Math.Max(1.0, Math.Round(size / 16.0));
            var line = lines[segment.LineIndex];
            var top = Math.Clamp(
                segment.Baseline - (size / 3.0),
                line.Bounds.Top,
                line.Bounds.Bottom - thickness);
            context.Commands.Add(new RectangleCommand(
                new Rect(origin.X + segment.X, origin.Y + top, segment.Width, thickness),
                run.Foreground,
                Opacity: opacity));
        }
    }

    /// <summary>
    /// 按当前 <see cref="StyledText.Ranges"/>/<see cref="StyledText.Interactions"/> 与 layout 的
    /// canonical segment 文本区间交集解算 paint（segment 不跨任何模型端点，映射唯一）。
    /// <paramref name="hoveredId"/> 命中的交互区间覆盖的 segment 用 <see cref="LinkHoverStyle"/> 覆盖。
    /// </summary>
    internal RichTextPaint ResolvePaint(ViewNode node, RichTextLayoutResult layout, int? hoveredId)
    {
        var ranges = Content.Ranges;
        var interactions = Content.Interactions;
        var segments = layout.Segments;
        var paragraphForeground = node.Environment.ResolvedForeground;
        var runs = new RichTextRunPaint[segments.Count];
        var rangeIndex = 0;
        var interactionIndex = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            while (rangeIndex < ranges.Count && ranges[rangeIndex].End <= segment.Start)
            {
                rangeIndex++;
            }

            var style = rangeIndex < ranges.Count && ranges[rangeIndex].Start <= segment.Start
                ? ranges[rangeIndex].Style
                : null;

            while (interactionIndex < interactions.Count && interactions[interactionIndex].End <= segment.Start)
            {
                interactionIndex++;
            }

            var hovered = hoveredId is { } id
                && interactionIndex < interactions.Count
                && interactions[interactionIndex].Start <= segment.Start
                && interactions[interactionIndex].Id == id;
            var hover = hovered ? LinkHoverStyle : null;

            var foreground = hover?.Foreground ?? style?.Foreground ?? paragraphForeground;
            var background = hover?.Background ?? style?.Background ?? Color.Transparent;
            var underline = hover?.Underline ?? style?.Underline ?? false;
            var strikethrough = hover?.Strikethrough ?? style?.Strikethrough ?? false;
            runs[i] = new RichTextRunPaint(foreground, background, underline, strikethrough);
        }

        return new RichTextPaint(runs) { ParagraphForeground = paragraphForeground };
    }

    /// <summary>当前悬停的交互 id（RT-04 状态机维护；无悬停为 null）。</summary>
    internal static int? HoveredId(ViewNode node)
        => (node.InteractionState as RichTextInteractionState)?.HoveredId;

    /// <summary>
    /// 使已解算 paint 失效：立即按当前区间与悬停状态重算并请求重绘（RT-04 hover 边使用）。
    /// 只在真正持富文本 handle 时生效。
    /// </summary>
    internal void InvalidateRichPaint(ViewNode node)
    {
        if (node.ViewCache is RichTextViewCache { Layout: { } layout } cache)
        {
            cache.Paint = ResolvePaint(node, layout, HoveredId(node));
            node.InvalidateRender();
        }
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        // RT-04：内容替换即清链接交互态（pressed/hover 都绑定旧 Id 区间，继续悬在
        // 可能已不存在/端点已变的 Id 上会形成 stale 激活）。hover 收敛发一次 null。
        // hover 状态清掉后，即使 Content/LinkHoverStyle 值未变，解算出的 paint 也变了——
        // 必须纳入 paint 刷新条件，否则 cache.Paint 停留在 hover 覆盖色。
        var hadHover = false;
        if (node.InteractionState is RichTextInteractionState state)
        {
            state.PressedId = null;
            state.Cancelled = false;
            hadHover = state.HoveredId is not null;
            // 清 hover 仅收敛回调一次 null；paint 重算走下方条件（几何等价时即时，
            // 不等时 Measure 重建——不用旧 layout 对新 Content 解算）。
            ClearHoverState(state);
        }

        if (node.ViewCache is not RichTextViewCache cache || previous is not RichText old)
        {
            return;
        }

        if (old.Content.HasRichContent != Content.HasRichContent)
        {
            // plain↔rich 切换：释放旧 handle（plain 侧 ViewCache 是裸 TextLayoutResult，无 handle）。
            // 下一次 Measure 按新路径重建；GeometryKey 相同的纯视觉变化走下方 paint 刷新。
            cache.Dispose();
            node.ViewCache = null;
            return;
        }

        var paintSemanticsChanged =
            hadHover
            || !Equals(old.Content, Content)
            || !Equals(old.LinkHoverStyle, LinkHoverStyle)
            || (old.Font is null) != (Font is null);

        // Reconciler 不论 IsLayoutEquivalent 与否都会调用 OnUpdate；几何已变的更新
        // 不能用旧 layout 的 segment 区间去解算新 Ranges（边界不同会错位）——
        // 交给紧随的 Measure 用新 layout 重建 paint。仅几何等价时即时重算。
        if (cache.Layout is { } layout && paintSemanticsChanged && LayoutPropertiesEquivalent(old))
        {
            cache.Paint = ResolvePaint(node, layout, HoveredId(node));
        }
    }

    internal override void OnUnmount(ViewNode node)
    {
        if (node.InteractionState is RichTextInteractionState state)
        {
            state.PressedId = null;
            state.Cancelled = false;
            // 卸载路径回调已脱离输入事件流——收敛发 null 由调用方在组件仍可响应时收到；
            // 不重算 paint（该 handle 即将 Dispose，paint 无消费者）。
            ClearHoverState(state);
        }

        if (node.ViewCache is RichTextViewCache cache)
        {
            cache.Dispose();
            node.ViewCache = null;
        }
    }

    /// <summary>
    /// 富文本测量缓存：handle + GeometryKey + 已解算 paint + 解算时的段落前景。
    /// 仅在富文本路径下创建；plain 路径 <see cref="ViewNode.ViewCache"/> 直接持
    /// <see cref="TextLayoutResult"/>（union，无富文本分配）。
    /// </summary>
    /// <remarks>
    /// 探测一致性：仅探测（BeginProbe）不替换本对象（见 <see cref="Measure"/>）——探测快照恢复的
    /// ViewCache 引用始终指向同一批存活句柄；不存在"探测 Dispose 提交句柄"的路径。
    /// 自然尺寸测量（Intrinsic）不是探测，照常提交/替换本对象，与 plain Text 同语义。
    /// </remarks>
    internal sealed class RichTextViewCache : IDisposable
    {
        public RichTextViewCache(
            RichTextLayoutResult layout,
            RichTextLayoutRequest request,
            RichTextPaint paint,
            Color paragraphForeground)
        {
            Layout = layout;
            Request = request;
            Paint = paint;
            ParagraphForeground = paragraphForeground;
        }

        /// <summary>产生当前 handle 的排版请求（GeometryKey 复用判定）。</summary>
        public RichTextLayoutRequest Request { get; private set; }

        /// <summary>富文本排版 handle；本节点持有一份 core 引用。</summary>
        public RichTextLayoutResult Layout { get; private set; }

        /// <summary>已解算 paint；仅在排版/描述更新/悬停边重算，Emit 不重建。</summary>
        public RichTextPaint Paint { get; set; }

        /// <summary>解算 paint 时的段落前景（缓存复用路径上据此检测环境前景变化）。</summary>
        public Color ParagraphForeground { get; set; }

        /// <summary>替换为新的排版结果；旧 handle（本节点份额）先 Dispose。</summary>
        public void Set(RichTextLayoutResult layout, RichTextLayoutRequest request, RichTextPaint paint, Color paragraphForeground)
        {
            Layout.Dispose();
            Layout = layout;
            Request = request;
            Paint = paint;
            ParagraphForeground = paragraphForeground;
        }

        /// <summary>释放持有的 handle；幂等。</summary>
        public void Dispose() => Layout.Dispose();
    }

    /// <summary>链接交互状态：悬停 Id + 按下一次后的按压 Id 与离区取消标记。</summary>
    internal sealed class RichTextInteractionState
    {
        /// <summary>当前悬停的 interaction Id；null 表示无悬停。</summary>
        public int? HoveredId { get; set; }

        /// <summary>按下时的 interaction Id；null 表示当前不在按压中。</summary>
        public int? PressedId { get; set; }

        /// <summary>按压后已离区（<see cref="OnPointerMove"/> 命中 Id 离开 <see cref="PressedId"/> 后永久置位，直到 Up/Cancel 清态）。</summary>
        public bool Cancelled { get; set; }
    }

    // ---------- RT-04：链接 hit（行内矩形命中 + Id 身份） ----------
    //
    // 命中几何：TextInteractionRange 的逐行矩形并集（layout.GetRangeRects——行内真实 X、
    // 行盒高、TrimEnd 外无矩形）；pointer local 落在任一矩形内 → 命中该 Id。不用 caret 吸附。
    // pointer 坐标是 frame 绝对位置：layout 局部坐标 = 位置 − 文本排版原点（同 Emit 的
    // TextPainter.ResolveOrigin，纹理/命中共用同一吸附后原点）。

    /// <summary>返回排版局部坐标 <paramref name="local"/> 命中的 interaction Id；未命中 null。
    /// 无堆分配：canonical segment 不跨任何模型端点（Ranges∪Interactions 端点已并入分段边界），
    /// 逐 segment 构造行内矩形（segment.X/Width 已按行 TrimEnd 截断）与 local 直接比较。</summary>
    private static int? HitLink(RichTextLayoutResult layout, StyledText content, Point local)
    {
        var interactions = content.Interactions;
        var segments = layout.Segments;
        var lines = layout.Lines;
        for (var i = 0; i < interactions.Count; i++)
        {
            var range = interactions[i];
            for (var s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                // segment 是 interaction 端点的子区间：起点落在 range 内即整段属于该 range。
                if (seg.Start >= range.Start && seg.Start < range.End)
                {
                    var line = lines[seg.LineIndex];
                    var rect = new Rect(seg.X, line.Bounds.Y, seg.Width, line.Bounds.Height);
                    if (rect.Contains(local))
                    {
                        return range.Id;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>返回 <paramref name="position"/>（frame 绝对）命中的 interaction Id；未命中或
    /// 指针落在节点可见 clip 外（含祖先 clip 截断的 link 矩形部分）→ null。</summary>
    private static int? HitLinkAt(ViewNode node, Point position)
    {
        if (node.ViewCache is not RichTextViewCache { Layout: { } layout } || layout.Size.IsEmpty)
        {
            return null;
        }

        // 捕获期间 pointer 会穿过祖先 clip 边界继续发到本 view——若该点已不在节点的
        // 有效命中区域（router 视角下不是本节点），按 clip 外对待，不计入任何 link 命中。
        // 用 router 的 HitTest 重新判：它完整走祖先 clip 累积 + IsHitTestVisible + disabled 检查。
        if (node.Runtime.HitTest(position) is not { } hitNode || !ReferenceEquals(hitNode, node))
        {
            return null;
        }

        var origin = TextPainter.ResolveOrigin(node.Frame, layout.Size, ((RichText)node.View).Alignment, layout.PixelScale);
        var local = new Point(position.X - origin.X, position.Y - origin.Y);
        return HitLink(layout, ((RichText)node.View).Content, local);
    }

    /// <summary>清 hover 态并回调一次 null；不触发 paint 重算——由调用方按场景决定
    /// （OnUpdate/OnUnmount 不复用旧 layout 对新 Content 解算 paint，几何变延迟到 Measure）。</summary>
    private void ClearHoverState(RichTextInteractionState state)
    {
        if (state.HoveredId is not null)
        {
            state.HoveredId = null;
            OnLinkHover?.Invoke(null);
        }
    }

    /// <summary>悬停边：命中 Id 变化 → 回调（payload 或 null）并按需重 paint。</summary>
    private void UpdateHoverState(ViewNode node, RichTextInteractionState state, int? hitId)
    {
        if (state.HoveredId == hitId)
        {
            return;
        }

        state.HoveredId = hitId;
        if (LinkHoverStyle is not null)
        {
            InvalidateRichPaint(node);
        }

        OnLinkHover?.Invoke(hitId is { } id ? PayloadForId(id) : null);
    }

    /// <summary>按 Id 取 payload；Id 来源必是已命中区间，未匹配时返回 null（防御）。</summary>
    private object? PayloadForId(int id)
    {
        var interactions = Content.Interactions;
        for (var i = 0; i < interactions.Count; i++)
        {
            if (interactions[i].Id == id)
            {
                return interactions[i].Payload;
            }
        }

        return null;
    }

    internal override bool WantsPointerCapture(ViewNode node, PointerEvent e)
    {
        // 仅"有激活回调且按下命中 interaction 行内矩形"时申请捕获；否则冒泡给祖先
        // （父 Button 等仍可正常激活），且 Move/Up 按非捕获路径路由不锁定本节点。
        if (node.IsEffectivelyDisabled || OnLinkActivate is null)
        {
            return false;
        }

        return HitLinkAt(node, e.Position) is not null;
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        // 无激活回调时按下不 capture（区间照常渲染，仅装饰不消费事件）。
        if (OnLinkActivate is null)
        {
            return false;
        }

        var hit = HitLinkAt(node, e.Position);
        if (hit is not { } id)
        {
            return false;
        }

        var state = node.InteractionState as RichTextInteractionState ?? (RichTextInteractionState)(node.InteractionState = new RichTextInteractionState());
        state.PressedId = id;
        state.Cancelled = false;
        return true;
    }

    internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = node.InteractionState as RichTextInteractionState;
        var hit = node.IsEffectivelyDisabled ? null : HitLinkAt(node, e.Position);

        if (state?.PressedId is { } pressedId)
        {
            // 按压中：命中离开按压 Id → 永久取消（移回不恢复），直到 Up/Cancel 清态。
            if (hit != pressedId)
            {
                state.Cancelled = true;
            }

            return true;
        }

        if (state is null)
        {
            state = (RichTextInteractionState)(node.InteractionState = new RichTextInteractionState());
        }

        UpdateHoverState(node, state, hit);
        return false;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = node.InteractionState as RichTextInteractionState;
        if (state?.PressedId is not { } pressedId)
        {
            return false;
        }

        var hit = node.IsEffectivelyDisabled ? null : HitLinkAt(node, e.Position);
        var activate = !state.Cancelled && hit == pressedId ? pressedId : (int?)null;

        state.PressedId = null;
        state.Cancelled = false;

        if (activate is { } id && OnLinkActivate is { } callback)
        {
            callback(PayloadForId(id));
        }

        return true;
    }

    internal override bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = node.InteractionState as RichTextInteractionState ?? (RichTextInteractionState)(node.InteractionState = new RichTextInteractionState());
        UpdateHoverState(node, state, HitLinkAt(node, e.Position));
        return false;
    }

    internal override bool OnPointerLeave(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.InteractionState is RichTextInteractionState state)
        {
            // 离开：paint 还原 + 回调 null（此时 layout/Content 仍匹配，可安全重算）。
            if (state.HoveredId is not null)
            {
                state.HoveredId = null;
                if (LinkHoverStyle is not null)
                {
                    InvalidateRichPaint(node);
                }

                OnLinkHover?.Invoke(null);
            }
        }

        return false;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        if (node.InteractionState is RichTextInteractionState state)
        {
            state.PressedId = null;
            state.Cancelled = false;
            // 捕获取消（失焦/禁用/卸载前）：清态并发一次 hover null；layout/Content 未变
            // 时可重 paint，变时由 OnUpdate/Measure 重建——用 InvalidateRichPaint 安全：
            // 它按当前 cache.Layout + 当前 Content 重算，不引入旧 layout 错位。
            if (state.HoveredId is not null)
            {
                state.HoveredId = null;
                if (LinkHoverStyle is not null)
                {
                    InvalidateRichPaint(node);
                }

                OnLinkHover?.Invoke(null);
            }
        }

        return false;
    }
}
