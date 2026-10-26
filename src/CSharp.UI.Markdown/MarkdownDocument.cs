using Markdig.Syntax;

namespace CSharp.UI.Markdown;

/// <summary>
/// 已解析的 Markdown 文档：源文本 + Markdig AST（internal 持有，公开面只露 <see cref="Source"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 实例不可变；AST 仅供本程序集 adapter 只读遍历（不跨实例共享、不跨线程 mutate）。
/// <see cref="Markdown.Parse"/> 对同 source + 同 <see cref="MarkdownPipelineOptions"/> 在缓存命中时
/// 返回同一实例；值相等不依赖缓存存续（LRU evict 后重 Parse 产出值相等的新实例）。
/// </para>
/// </remarks>
public sealed class MarkdownDocument : IEquatable<MarkdownDocument>
{
    internal MarkdownDocument(string source, MarkdownPipelineOptions options, Markdig.Syntax.MarkdownDocument ast)
    {
        Source = source;
        Options = options;
        Ast = ast;
    }

    /// <summary>Markdown 源文本。</summary>
    public string Source { get; }

    /// <summary>本次解析使用的管线选项（解析后即固定；policy/resolver 属 adapter 输入不入本键）。</summary>
    public MarkdownPipelineOptions Options { get; }

    /// <summary>Markdig AST 根；internal——公开面不暴露 Markdig 可变对象。</summary>
    internal Markdig.Syntax.MarkdownDocument Ast { get; }

    /// <inheritdoc />
    public bool Equals(MarkdownDocument? other)
        => other is not null
            && (ReferenceEquals(this, other) || (Source == other.Source && Options == other.Options));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MarkdownDocument);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Source, Options);
}
