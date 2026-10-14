namespace CSharp.UI.Skia;

/// <summary>文本服务诊断类别。</summary>
public enum TextDiagnosticKind
{
    /// <summary><see cref="FontFamily.FromFile"/> 指定的字体文件不存在或无法解析。</summary>
    MissingFontFile,

    /// <summary>按家族名请求的字体不存在，已回退到其他家族或平台默认字体。</summary>
    MissingFontFamily,

    /// <summary>某个字符在首选字体中缺失，已使用缺字回退字体。</summary>
    CharacterFallback,

    /// <summary>所有候选字体都无法覆盖某个字符，该字符将绘制为缺字方框。</summary>
    MissingGlyph,
}

/// <summary>
/// 一条字体/文本诊断。字体解析失败不会中断排版（除非显式要求抛出），
/// 而是记录在这里供宿主日志与测试断言使用。
/// </summary>
/// <param name="Kind">诊断类别。</param>
/// <param name="Message">面向用户的中文说明。</param>
/// <param name="Source">相关来源（字体文件路径或家族名）；无关时为 null。</param>
public sealed record TextDiagnostic(TextDiagnosticKind Kind, string Message, string? Source = null)
{
    /// <inheritdoc />
    public override string ToString() => Source is { Length: > 0 } source ? $"{Kind}: {Message} [{source}]" : $"{Kind}: {Message}";
}
