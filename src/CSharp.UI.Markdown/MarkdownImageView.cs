using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CSharp.UI;

namespace CSharp.UI.Markdown;

/// <summary>
/// 块级唯一 inline 图片的宿主视图：静态子节点 <c>[Image(resolved), RichText(alt)]</c>。
/// Build 期（<see cref="TryCreate"/>）完成 AST 读取、remote 判定与
/// <see cref="MarkdownImagePolicy.Resolve"/> 调用；<see cref="Measure"/> 只做
/// <see cref="IImageService.TryLoad"/> 探测并按成败激活一个子节点——不在
/// Measure/Emit 中创建 View 或解析 AST。
/// </summary>
/// <remarks>
/// 尺寸契约（task 08 §图片）：自然尺寸 ≤ 可用宽度 → 自然尺寸（不放大）；超出可用宽度
/// → 按宽等比缩；宽度无界 → 自然尺寸。TryLoad 失败 → alt 为 active 子项。
/// </remarks>
internal sealed class MarkdownImageView : View
{
    private sealed record Cache(bool LoadOk, Size Natural);

    private readonly View[] _children;
    private readonly string _source;
    private readonly StyledText _altText;

    private MarkdownImageView(string source, StyledText altText)
    {
        _children =
        [
            new Image(source),
            new RichText(altText) { Wrapping = TextWrapping.Wrap },
        ];
        _source = source;
        _altText = altText;
    }

    internal override IReadOnlyList<View> Children => _children;

    /// <summary>
    /// 段落内唯一 inline image → 创建图片视图（或 alt 兜底）；混排返回 false 走既有 inline 路径。
    /// </summary>
    internal static bool TryCreate(ParagraphBlock para, MarkdownBlockView host,
                                   MarkdownStyleOptions style, out View? view)
    {
        view = null;
        if (para.Inline is null)
        {
            return false;
        }

        LinkInline? image = null;
        var count = 0;
        foreach (var child in para.Inline)
        {
            // 空白 literal 不构成"混排"——块级图片段可能带尾随空白。
            if (child is LiteralInline lit && string.IsNullOrWhiteSpace(lit.Content.ToString()))
            {
                continue;
            }

            count++;
            if (child is LinkInline { IsImage: true } link)
            {
                image = link;
            }
        }

        if (count != 1 || image is null)
        {
            return false;
        }

        var alt = CollectAlt(image);
        var url = image.Url ?? string.Empty;
        var policy = host.ImagePolicy;

        // remote 默认拒绝：不调 Resolve（MD-04 契约）。
        if (IsRemote(url) && policy?.AllowRemote != true)
        {
            view = AltView(alt);
            return true;
        }

        var resolved = policy?.Resolve?.Invoke(url);
        if (resolved is null)
        {
            view = AltView(alt);
            return true;
        }

        view = new MarkdownImageView(resolved, StyledText.Create(alt.Length == 0 ? "[image]" : alt));
        return true;
    }

    private static RichText AltView(string alt)
        => new(StyledText.Create(alt.Length == 0 ? "[image]" : alt))
        { Wrapping = TextWrapping.Wrap };

    private static bool IsRemote(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string CollectAlt(LinkInline image)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in image)
        {
            CollectLiteral(child, sb);
        }
        return sb.Length > 0 ? sb.ToString() : image.Url ?? string.Empty;
    }

    private static void CollectLiteral(Inline node, System.Text.StringBuilder sb)
    {
        switch (node)
        {
            case LiteralInline lit: sb.Append(lit.Content); break;
            case HtmlEntityInline e: sb.Append(e.Transcoded.ToString()); break;
            case CodeInline c: sb.Append(c.Content); break;
            case LineBreakInline lb: sb.Append(lb.IsHard ? '\n' : ' '); break;
            case ContainerInline c:
                foreach (var child in c)
                {
                    CollectLiteral(child, sb);
                }
                break;
        }
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var imageNode = node.ChildList[0];
        var altNode = node.ChildList[1];

        var cache = node.ViewCache as Cache;
        if (cache is null)
        {
            ImageResource? res = null;
            var ok = context.ImageService is not null
                     && context.ImageService.TryLoad(_source, out res)
                     && res is not null;
            cache = new Cache(ok, ok ? res!.NaturalSize : Size.Zero);
            node.ViewCache = cache;
        }

        if (!cache.LoadOk)
        {
            context.Measure(altNode, constraints);
            node.DesiredSize = altNode.DesiredSize;
            return;
        }

        var target = TargetSize(cache.Natural, constraints);
        context.Measure(imageNode, LayoutConstraints.Tight(target.Width, target.Height));
        node.DesiredSize = constraints.Constrain(target);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        var cache = node.ViewCache as Cache;
        if (cache is null || !cache.LoadOk)
        {
            context.Arrange(node.ChildList[0], Rect.Empty);
            context.Arrange(node.ChildList[1], bounds);
            return;
        }

        context.Arrange(node.ChildList[1], Rect.Empty);
        var scale = ComputeScale(cache.Natural, bounds.Width);
        var w = cache.Natural.Width * scale;
        var h = cache.Natural.Height * scale;
        context.Arrange(node.ChildList[0],
            new Rect(bounds.X, bounds.Y, Math.Min(w, bounds.Width), Math.Min(h, bounds.Height)));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        var ok = node.ViewCache is Cache { LoadOk: true };
        context.EmitChild(node.ChildList[ok ? 0 : 1]);
    }

    /// <summary>
    /// 新视图描述（source / alt 语义变化）复用同一节点时：失效 measure 期缓存的
    /// TryLoad 结果与自然尺寸，保证旧 fail 不再永久 alt、旧成功不锁旧 aspect。
    /// 同 source 下真实资源内容变化沿用 <see cref="Image"/> 既有语义——服务层
    /// LruCache / <see cref="ImageResource.PixelsDisposed"/> 通知纹理失效，本视图不重试。
    /// </summary>
    internal override void OnUpdate(ViewNode node, View previous)
    {
        if (previous is not MarkdownImageView prev
            || !string.Equals(prev._source, _source, StringComparison.Ordinal)
            || !prev._altText.Equals(_altText))
        {
            node.ViewCache = null;
        }
    }


    internal override bool LayoutPropertiesEquivalent(View other)
        => other is MarkdownImageView v
           && string.Equals(v._source, _source, StringComparison.Ordinal)
           && v._altText.Equals(_altText);

    // —— 尺寸：不放大；仅当自然宽 > 可用宽时按宽等比缩；宽无界 → 自然。 ——

    private static double ComputeScale(Size natural, double availableWidth)
        => natural.Width <= 0 || !double.IsFinite(availableWidth) || natural.Width <= availableWidth
            ? 1.0
            : availableWidth / natural.Width;

    private static Size TargetSize(Size natural, LayoutConstraints constraints)
    {
        if (natural.IsEmpty)
        {
            return Size.Zero;
        }

        var scale = ComputeScale(natural, constraints.MaxWidth);
        return new Size(natural.Width * scale, natural.Height * scale);
    }
}
