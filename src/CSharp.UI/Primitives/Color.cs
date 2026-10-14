namespace CSharp.UI;

/// <summary>
/// 非预乘 alpha 的 8 位 sRGB 颜色（0—255）。
/// </summary>
/// <remarks>
/// 框架内的颜色始终是非预乘形式；只有在写入 <see cref="PixelBuffer"/> 或上传 GPU 时才转换为
/// <see cref="PixelFormat.Rgba8888Premultiplied"/>，避免透明边缘出现色晕（dark fringe）。
/// </remarks>
public readonly record struct Color(byte R, byte G, byte B, byte A)
{
    /// <summary>完全透明。</summary>
    public static readonly Color Transparent = new(0, 0, 0, 0);

    /// <summary>不透明黑。</summary>
    public static readonly Color Black = new(0, 0, 0, 255);

    /// <summary>不透明白。</summary>
    public static readonly Color White = new(255, 255, 255, 255);

    /// <summary>不透明红。</summary>
    public static readonly Color Red = new(255, 0, 0, 255);

    /// <summary>不透明绿。</summary>
    public static readonly Color Green = new(0, 128, 0, 255);

    /// <summary>不透明蓝。</summary>
    public static readonly Color Blue = new(0, 0, 255, 255);

    /// <summary>中灰。</summary>
    public static readonly Color Gray = new(128, 128, 128, 255);

    /// <summary>浅灰。</summary>
    public static readonly Color LightGray = new(211, 211, 211, 255);

    /// <summary>深灰。</summary>
    public static readonly Color DarkGray = new(64, 64, 64, 255);

    /// <summary>不透明颜色，alpha 为 255。</summary>
    public Color(byte r, byte g, byte b)
        : this(r, g, b, 255)
    {
    }

    /// <summary>RGB 与 alpha 分量构造。</summary>
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

    /// <summary>使用 0—1 浮点分量构造（自动截断到合法范围）。</summary>
    public static Color FromFloat(double r, double g, double b, double a = 1)
        => new(ToByte(r), ToByte(g), ToByte(b), ToByte(a));

    /// <summary>解析 <c>#RGB</c>、<c>#RRGGBB</c>、<c>#RRGGBBAA</c>、<c>#AARRGGBB</c> 形式；非法输入抛出 <see cref="FormatException"/>。</summary>
    public static Color Parse(string hex)
    {
        if (!TryParse(hex, out var color))
        {
            throw new FormatException($"无法把 \"{hex}\" 解析为颜色；支持 #RGB、#RRGGBB、#RRGGBBAA、#AARRGGBB。");
        }

        return color;
    }

    /// <summary>尝试解析十六进制颜色。</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Transparent;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.Trim().TrimStart('#');
        static bool Hex(string s) => s.All(static c => Uri.IsHexDigit(c));
        if (!Hex(text))
        {
            return false;
        }

        static byte Pair(string s, int index) => Convert.ToByte(s.Substring(index, 2), 16);

        switch (text.Length)
        {
            case 3:
                color = new Color(
                    (byte)(Convert.ToByte(text[0].ToString(), 16) * 17),
                    (byte)(Convert.ToByte(text[1].ToString(), 16) * 17),
                    (byte)(Convert.ToByte(text[2].ToString(), 16) * 17));
                return true;
            case 6:
                color = new Color(Pair(text, 0), Pair(text, 2), Pair(text, 4));
                return true;
            case 8:
                // #RRGGBBAA，与 CSS 一致。
                color = new Color(Pair(text, 0), Pair(text, 2), Pair(text, 4), Pair(text, 6));
                return true;
            default:
                return false;
        }
    }

    /// <summary>替换 alpha 分量。</summary>
    public Color WithAlpha(byte alpha) => new(R, G, B, alpha);

    /// <summary>按比例缩放 alpha（0—1 之外的值被截断）。</summary>
    public Color ScaleAlpha(double factor) => WithAlpha(ToByte((A / 255.0) * factor));

    /// <summary>与另一种颜色按 <paramref name="amount"/>（0—1）线性插值，包含 alpha。</summary>
    public Color Lerp(Color other, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        static byte Mix(byte a, byte b, double t) => ToByte(((a / 255.0) * (1 - t)) + ((b / 255.0) * t));
        return new Color(Mix(R, other.R, t), Mix(G, other.G, t), Mix(B, other.B, t), Mix(A, other.A, t));
    }

    /// <summary>转换为 #RRGGBBAA 文本。</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
