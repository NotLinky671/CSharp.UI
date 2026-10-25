namespace CSharp.UI;

/// <summary>字体笔画粗细。</summary>
public enum FontWeight
{
    /// <summary>100。</summary>
    Thin = 100,

    /// <summary>200。</summary>
    ExtraLight = 200,

    /// <summary>300。</summary>
    Light = 300,

    /// <summary>400，常规正文。</summary>
    Normal = 400,

    /// <summary>500。</summary>
    Medium = 500,

    /// <summary>600。</summary>
    SemiBold = 600,

    /// <summary>700，粗体。</summary>
    Bold = 700,

    /// <summary>800。</summary>
    ExtraBold = 800,

    /// <summary>900。</summary>
    Black = 900,
}

/// <summary>
/// 字体来源描述：应用自带的字体文件、按家族名请求的系统字体，或平台默认字体。
/// </summary>
/// <remarks>
/// 核心模块只保存描述，不进行字体解析；解析由 <c>CSharp.UI.Skia</c> 的文本服务完成。
/// </remarks>
public sealed record FontFamily
{
    /// <summary>平台默认字体（由文本服务选择，并按缺字回退覆盖中英文）。</summary>
    public static readonly FontFamily Default = new();

    /// <summary>
    /// 返回与本描述相等、但 <see cref="Fallbacks"/> 已冻结为独立只读集合的副本。
    /// 供不可变模型（<see cref="StyledText"/>）快照样式时调用——<see cref="Fallbacks"/> 的公开类型
    /// <see cref="IReadOnlyList{String}"/> 可能由调用方持有可变的底层引用，不改既有公开语义。
    /// </summary>
    internal FontFamily FrozenSnapshot() => new()
    {
        FilePath = FilePath,
        FamilyName = FamilyName,
        Fallbacks = new System.Collections.ObjectModel.ReadOnlyCollection<string>([.. Fallbacks]),
    };

    private FontFamily()
    {
    }

    /// <summary>应用指定的字体文件绝对或相对路径；为 null 时按 <see cref="FamilyName"/> 或平台默认解析。</summary>
    public string? FilePath { get; private init; }

    /// <summary>系统字体家族名；为 null 时使用平台默认字体。</summary>
    public string? FamilyName { get; private init; }

    /// <summary>该字体缺失时按顺序尝试的备选家族名。</summary>
    public IReadOnlyList<string> Fallbacks { get; private init; } = [];

    /// <summary>由字体文件创建；文件不存在或无法解析时由文本服务报告诊断。</summary>
    public static FontFamily FromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new FontFamily { FilePath = filePath };
    }

    /// <summary>按系统字体家族名创建。</summary>
    public static FontFamily FromName(string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        return new FontFamily { FamilyName = familyName };
    }

    /// <summary>返回追加了备选家族名的新描述。</summary>
    public FontFamily WithFallbacks(params string[] fallbacks)
        => this with { Fallbacks = [.. Fallbacks, .. fallbacks] };

    /// <summary>
    /// 结构相等：文件路径、家族名与回退列表逐项比较。
    /// （记录默认比较对 <see cref="Fallbacks"/> 使用引用相等，会让内容相同但分别构造的请求无法复用排版缓存。）
    /// </summary>
    public bool Equals(FontFamily? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!string.Equals(FilePath, other.FilePath, StringComparison.Ordinal) ||
            !string.Equals(FamilyName, other.FamilyName, StringComparison.Ordinal) ||
            Fallbacks.Count != other.Fallbacks.Count)
        {
            return false;
        }

        for (var i = 0; i < Fallbacks.Count; i++)
        {
            if (!string.Equals(Fallbacks[i], other.Fallbacks[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FilePath, StringComparer.Ordinal);
        hash.Add(FamilyName, StringComparer.Ordinal);
        foreach (var fallback in Fallbacks)
        {
            hash.Add(fallback, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>用于诊断与日志的显示名。</summary>
    public string DisplayName => FilePath is { Length: > 0 } path
        ? Path.GetFileNameWithoutExtension(path)
        : FamilyName ?? "default";

    public override string ToString() => DisplayName;
}

/// <summary>
/// 字体描述：家族 + 字号（逻辑像素）+ 粗细 + 斜体。
/// </summary>
public readonly record struct FontDescription(FontFamily Family, double Size, FontWeight Weight, bool Italic)
{
    /// <summary>默认正文：平台默认字体、14 逻辑像素、常规、非斜体。</summary>
    public static readonly FontDescription Default = new(FontFamily.Default, 14, FontWeight.Normal, false);

    /// <summary>指定家族与字号，使用常规粗细。</summary>
    public FontDescription(FontFamily family, double size)
        : this(family, size, FontWeight.Normal, false)
    {
    }

    /// <summary>按系统字体名创建描述。</summary>
    public static FontDescription FromName(string familyName, double size, FontWeight weight = FontWeight.Normal, bool italic = false)
        => new(FontFamily.FromName(familyName), size, weight, italic);

    /// <summary>返回替换字号后的描述。</summary>
    public FontDescription WithSize(double size) => this with { Size = size };

    /// <summary>返回替换粗细后的描述。</summary>
    public FontDescription WithWeight(FontWeight weight) => this with { Weight = weight };

    /// <summary>返回加粗描述（保持已有粗细或 Normal→Bold）。</summary>
    public FontDescription Bold() => this with { Weight = FontWeight.Bold };

    public override string ToString() => $"{Family.DisplayName} {Size:0.##}{(Weight == FontWeight.Normal ? string.Empty : " " + Weight)}{(Italic ? " italic" : string.Empty)}";
}

/// <summary>水平文本对齐。</summary>
public enum TextAlignment
{
    /// <summary>左对齐。</summary>
    Left,

    /// <summary>居中。</summary>
    Center,

    /// <summary>右对齐。</summary>
    Right,
}

/// <summary>换行方式。</summary>
public enum TextWrapping
{
    /// <summary>不换行；文本宽度可以超过约束宽度。</summary>
    NoWrap,

    /// <summary>在可用宽度内按字素/单词边界换行。</summary>
    Wrap,
}

/// <summary>超出可用空间时的省略方式。</summary>
public enum TextTrimming
{
    /// <summary>不省略，直接溢出（由裁剪决定可见范围）。</summary>
    None,

    /// <summary>用单字符省略号截断（首版固定为 …，不实现按字符自定义）。</summary>
    Ellipsis,
}

/// <summary>字体纵向度量（逻辑像素）。Ascent/Descent 为正；LineGap 反映字体真实 leading，可能为 0。</summary>
public readonly record struct FontMetrics(double Ascent, double Descent, double LineGap)
{
    /// <summary>单行默认行高（ascent + descent + lineGap）。</summary>
    public double LineHeight => Ascent + Descent + LineGap;
}
