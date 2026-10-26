namespace CSharp.UI.Markdown;

/// <summary>
/// Markdown 解析管线选项。每个开关都对应 adapter 已实现的节点映射；
/// 不设"启用但无 renderer"的开关。禁用 <c>UseAdvancedExtensions()</c> /
/// <c>UsePreciseSourceLocation()</c> 的隐式集合。
/// </summary>
/// <remarks>所有字段参与值相等——任一字段变化即构成不同缓存键。</remarks>
public sealed record MarkdownPipelineOptions
{
    /// <summary>Pipe 表格（Markdig <c>UsePipeTables</c>）。默认 true。</summary>
    public bool PipeTables { get; init; } = true;

    /// <summary>Strikethrough 解析(Markdig `UseEmphasisExtras(Strikethrough)`——`~~strike~~`)。
    /// 默认 true。本模块仅映射 strikethrough;superscript/subscript/inserted/marked 等额外 delimiter
    /// 未实现 renderer,**不启用**——`^x^`/`~x~`/`++x++`/`==x==` 保持 literal 文本可见。</summary>
    public bool EmphasisExtras { get; init; } = true;

    /// <summary>任务列表（Markdig <c>UseTaskLists</c>——checkbox → <c>[ ]</c>/<c>[x]</c> literal 前缀）。默认 false。</summary>
    public bool TaskLists { get; init; }

    /// <summary>自动链接（Markdig <c>UseAutoLinks</c>）。默认 true。</summary>
    public bool AutoLinks { get; init; } = true;

    /// <summary>HTML 内联/块处理策略。默认 <see cref="Markdown.HtmlPolicy.Literal"/>。</summary>
    public HtmlPolicy Html { get; init; } = HtmlPolicy.Literal;
}
