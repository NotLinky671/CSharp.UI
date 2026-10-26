namespace CSharp.UI.Markdown;

/// <summary>
/// 链接激活回调的 payload（<c>RichText.OnLinkActivate</c> 回调参数）。
/// </summary>
public sealed record MarkdownLinkPayload(string Url, string? Title);
