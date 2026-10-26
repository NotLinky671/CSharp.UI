namespace CSharp.UI.Markdown;

/// <summary>
/// 链接激活策略：决定哪些 url 产 <c>TextInteractionRange</c>（命中即激活）。
/// </summary>
/// <remarks>
/// 默认仅 http/https 与无 scheme 相对路径可激活；<c>javascript:</c>/<c>data:</c>/<c>vbscript:</c> 等
/// 危险 scheme 不产 interaction（样式照常渲染，点击不激活）。相对 URL 原样进 payload。
/// </remarks>
public sealed record MarkdownLinkPolicy
{
    /// <summary>
    /// 链接是否可激活；null 或默认实现只放行 http/https/无 scheme 相对路径。
    /// </summary>
    public Func<string, bool>? AllowActivation { get; init; }

    /// <summary>默认实现：http/https + 无 scheme 相对路径。</summary>
    public static readonly MarkdownLinkPolicy Default = new();

    internal bool IsActivationAllowed(string url)
    {
        if (AllowActivation is { } check)
        {
            return check(url);
        }

        return IsSafeUrl(url);
    }

    internal static bool IsSafeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // 无 scheme 相对路径（不含 ':'）放行；http/https 显式放行；其余 scheme 拒绝。
        var colon = url.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        var scheme = url[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }
}
