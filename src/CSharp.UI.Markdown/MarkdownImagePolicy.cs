namespace CSharp.UI.Markdown;

/// <summary>
/// 图片解析与远程策略（MD-04 块级图片落地；本记录属 MD-02 公共契约层）。
/// </summary>
/// <remarks>
/// 解析输出即 <c>IImageService</c> 缓存键——policy 变化自然失效旧条目；框架自身不加载 URL。
/// </remarks>
public sealed record MarkdownImagePolicy
{
    /// <summary>
    /// url → <see cref="IImageService"/> source 的解析器；null 或返回 null → alt fallback。
    /// </summary>
    public Func<string, string?>? Resolve { get; init; }

    /// <summary>
    /// 允许远程（http/https）图片经 <see cref="Resolve"/> 处理；默认 false——远程一律 alt fallback，不调 <see cref="Resolve"/>。
    /// </summary>
    public bool AllowRemote { get; init; }
}
