using Markdig;

namespace CSharp.UI.Markdown;

/// <summary>
/// Markdown 解析入口：源文本 + 管线选项 → <see cref="MarkdownDocument"/>（含 Markdig AST）。
/// </summary>
/// <remarks>
/// 解析结果按 <c>(source, options)</c> 有界 LRU 缓存（同实例返回，零重复解析）。
/// resolver/link/image 等 adapter 期输入不进缓存键——options 值相等即同实例。
/// </remarks>
public static class Markdown
{
    private const int ParseCacheCapacity = 64;

    private static readonly MarkdownPipelineOptions DefaultOptions = new();

    private static readonly LruCache<(string Source, MarkdownPipelineOptions Options), MarkdownDocument> Cache
        = new(ParseCacheCapacity);

    /// <summary>静态缓存互斥——<see cref="LruCache{TKey,TValue}"/> 假定 UI 线程，本公开静态入口跨线程可达。</summary>
    private static readonly object CacheLock = new();

    /// <summary>解析缓存容量（诊断/测试可观察）。</summary>
    internal static int ParseCacheCount
    {
        get { lock (CacheLock) { return Cache.Count; } }
    }

    /// <summary>
    /// 解析 Markdown 源文本。同 source + 同 options → 缓存命中返回同一实例；
    /// options 任一字段变化 → 重新解析（cache 键 = 值相等）。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
    public static MarkdownDocument Parse(string source, MarkdownPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var opts = options ?? DefaultOptions;
        lock (CacheLock)
        {
            return Cache.GetOrAdd((source, opts), static key => ParseCore(key.Source, key.Options), out _);
        }
    }

    /// <summary>清空解析缓存（测试隔离用；非公开宿主重置 API）。</summary>
    internal static void ClearParseCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    private static MarkdownDocument ParseCore(string source, MarkdownPipelineOptions options)
    {
        var builder = new MarkdownPipelineBuilder();
        if (options.PipeTables)
        {
            builder.UsePipeTables();
        }

        if (options.EmphasisExtras)
        {
            // 只启用 Strikethrough——`UseEmphasisExtras()`默认(EmphasisExtraOptions.Default)还启用
            // `^`superscript/`~`subscript/`++`inserted/`==`marked,而 InlineAdapter 只对 `~~` 双字符
            // 与 `*`/`_` 有真实映射;启用无 renderer 的语义会把 `^x^` 当 italic、`++x++`/`==x==`
            // 当 bold 渲染,违反"不启用无 renderer 的开关"。
            builder.UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough);
        }

        if (options.TaskLists)
        {
            builder.UseTaskLists();
        }

        if (options.AutoLinks)
        {
            builder.UseAutoLinks();
        }

        // 不启用 UseAdvancedExtensions()/UsePreciseSourceLocation()/DisableHtml——
        // HTML 节点结构保留供 adapter 按 HtmlPolicy 处理。

        var ast = Markdig.Markdown.Parse(source, builder.Build());
        return new MarkdownDocument(source, options, ast);
    }
}
