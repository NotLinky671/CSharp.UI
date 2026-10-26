namespace CSharp.UI.Markdown;

/// <summary>
/// HTML 内联/块在 Markdown 渲染中的处理策略。
/// </summary>
public enum HtmlPolicy
{
    /// <summary>原文 literal 渲染（可见，不解释 HTML）。</summary>
    Literal,

    /// <summary>丢弃 HTML 节点。</summary>
    Strip,
}
