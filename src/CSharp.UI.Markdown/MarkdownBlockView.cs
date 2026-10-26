namespace CSharp.UI.Markdown;

/// <summary>
/// Markdown 文档的视图根组件：<see cref="Build"/> 内完成 AST→<see cref="View"/> 转换；
/// <c>Measure</c>/<c>Emit</c> 零 AST 接触。
/// </summary>
/// <remarks>
/// 文档值变化 → reconciler 重建 → <see cref="Build"/> 重跑（不做 AST diff）。
/// </remarks>
public sealed class MarkdownBlockView : Component
{
    /// <summary>已解析的 Markdown 文档（<see cref="Markdown.Parse"/> 产物）。</summary>
    public required MarkdownDocument Document { get; init; }

    /// <summary>图片解析与远程策略（MD-04 落地）。</summary>
    public MarkdownImagePolicy? ImagePolicy { get; init; }

    /// <summary>链接激活策略；null → <see cref="MarkdownLinkPolicy.Default"/>。</summary>
    public MarkdownLinkPolicy? LinkPolicy { get; init; }

    /// <summary>样式选项；null → 默认 <see cref="MarkdownStyleOptions"/>。</summary>
    public MarkdownStyleOptions? Style { get; init; }

    /// <summary>链接激活回调（参数为 <see cref="MarkdownLinkPayload"/>）。</summary>
    public Action<object>? OnLinkActivate { get; init; }

    /// <summary>链接悬停回调。</summary>
    public Action<object?>? OnLinkHover { get; init; }

    /// <inheritdoc />
    protected override View Build() => BlockAdapter.BuildDocument(Document, this);

    /// <summary>测试桥接：直接执行 <see cref="Build"/>（不入运行时管线）。</summary>
    internal View BuildForTest() => Build();
}
