using System.Globalization;
using SkiaSharp;

namespace CSharp.UI.Skia;

/// <summary>shaping 之后的单个字形。</summary>
/// <param name="Cluster">该字形对应文本簇的绝对 UTF-16 起始索引。</param>
/// <param name="Glyph">字形索引（字体内部 id）。</param>
/// <param name="Advance">水平推进量（逻辑像素，已含 kerning/连字调整）。</param>
/// <param name="OffsetX">字形水平偏移（逻辑像素，用于组合标记定位）。</param>
/// <param name="OffsetY">字形垂直偏移（逻辑像素，已转换为 Y 轴向下）。</param>
internal readonly record struct ShapedGlyph(int Cluster, ushort Glyph, double Advance, double OffsetX, double OffsetY);

/// <summary>文本形变（HarfBuzz shaping）与字素簇工具。</summary>
internal static class Shaping
{
    /// <summary>
    /// 对 <paramref name="text"/> 的 [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>) 区间做 shaping。
    /// </summary>
    /// <remarks>
    /// HarfBuzz 的位置以字体的固定单位返回（与本机字号无关），这里按 shaper 报告的总宽度统一归一化为逻辑像素，
    /// 从而保证推进量之和与排版宽度完全一致，光标位置与绘制位置同源。
    /// </remarks>
    public static List<ShapedGlyph> Shape(FontInstance instance, string text, int start, int length)
    {
        var glyphs = new List<ShapedGlyph>();
        if (length <= 0 || start < 0 || start + length > text.Length)
        {
            return glyphs;
        }

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text, start, length);
        buffer.GuessSegmentProperties();
        var result = instance.Shaper.Shape(buffer, instance.Font);
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        if (infos.Length == 0)
        {
            return glyphs;
        }

        var sum = 0.0;
        for (var i = 0; i < infos.Length && i < positions.Length; i++)
        {
            sum += positions[i].XAdvance;
        }

        var scale = sum > 0 && result.Width > 0 ? result.Width / sum : 1.0;
        for (var i = 0; i < infos.Length && i < positions.Length; i++)
        {
            var position = positions[i];
            glyphs.Add(new ShapedGlyph(
                (int)infos[i].Cluster,
                (ushort)infos[i].Codepoint,
                position.XAdvance * scale,
                position.XOffset * scale,
                -position.YOffset * scale));
        }

        // 拉丁/中文均为从左到右，簇索引天然升序；这里排序一次以防御性覆盖
        // 组合标记重排等边界情况（完整双向文本不在首版范围内）。
        glyphs.Sort(static (left, right) => left.Cluster.CompareTo(right.Cluster));
        return glyphs;
    }
}

/// <summary>Unicode 字素簇边界工具。所有索引均为 UTF-16 索引。</summary>
/// <remarks>
/// 使用 <see cref="StringInfo"/>（.NET 的字素簇实现）而不是 UTF-16 码元，
/// 保证代理对（Emoji）与组合字符（é = U+0065 U+0301）不会被拆开。
/// </remarks>
internal static class Graphemes
{
    /// <summary>返回文本的字素簇起始索引（升序，首项为 0，末项为 <c>text.Length</c>）。</summary>
    public static int[] Starts(string text)
    {
        var starts = new List<int>(Math.Max(1, text.Length / 2));
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            starts.Add(enumerator.ElementIndex);
        }

        if (starts.Count == 0 || starts[0] != 0)
        {
            starts.Insert(0, 0);
        }

        if (starts[^1] != text.Length)
        {
            starts.Add(text.Length);
        }

        return [.. starts];
    }

    /// <summary>返回升序字素边界列表中等于 <paramref name="index"/> 的位置；不存在时返回按位取反的插入位置。</summary>
    public static int IndexOf(int[] starts, int index) => TextLayoutResult.FindStop(starts, index);

    /// <summary>返回 <paramref name="index"/> 之前的字素边界索引（已是边界时返回它自身）。</summary>
    public static int BoundaryAtOrBefore(int[] starts, int index)
    {
        var found = IndexOf(starts, index);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    /// <summary>返回 <paramref name="index"/> 之前的字素起始索引（严格小于 <paramref name="index"/>）。</summary>
    public static int PreviousBoundary(int[] starts, int index)
    {
        var found = IndexOf(starts, index);
        var position = found >= 0 ? found - 1 : ~found - 1;
        return starts[Math.Max(0, position)];
    }

    /// <summary>读取索引处的第一个码点（自动处理代理对）。</summary>
    public static int CodepointAt(string text, int index)
    {
        if ((uint)index >= (uint)text.Length)
        {
            return -1;
        }

        var current = text[index];
        if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(current, text[index + 1]);
        }

        return current;
    }

    /// <summary>判断码点是否为空白（空格、制表符、全角空格等），用于行尾空白裁剪与断行机会。</summary>
    public static bool IsWhitespace(int codepoint)
        => codepoint is ' ' or '\t' or '\u3000' or '\u00A0' or '\u2007' or '\u202F';

    /// <summary>判断码点是否为换行符。</summary>
    public static bool IsLineBreak(char value) => value is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085';

    /// <summary>
    /// 判断码点是否属于可逐字断行的东亚文字（CJK、假名、谚文与全角形式）。
    /// 拉丁文本只在空白与连字符处断行，东亚文字按字素断行。
    /// </summary>
    public static bool IsBreakableScript(int codepoint) => codepoint switch
    {
        >= 0x1100 and <= 0x11FF => true,   // 谚文字母
        >= 0x2E80 and <= 0x303F => true,   // CJK 部首与 CJK 标点（含 U+3000 全角空格）
        >= 0x3040 and <= 0x30FF => true,   // 平假名、片假名
        >= 0x3130 and <= 0x318F => true,   // 谚文兼容字母
        >= 0x3400 and <= 0x4DBF => true,   // CJK 扩展 A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK 基本区
        >= 0xA960 and <= 0xA97F => true,   // 谚文字母扩展 A
        >= 0xAC00 and <= 0xD7AF => true,   // 谚文音节
        >= 0xF900 and <= 0xFAFF => true,   // CJK 兼容表意
        >= 0xFF00 and <= 0xFF60 => true,   // 全角 ASCII
        >= 0xFFE0 and <= 0xFFE6 => true,   // 全角符号
        >= 0x20000 and <= 0x3FFFF => true, // CJK 扩展 B 及以后
        _ => false,
    };
}

/// <summary>同一字体覆盖的一段连续文本与其 shaping 结果。</summary>
/// <param name="Start">起始 UTF-16 索引（字素边界）。</param>
/// <param name="End">结束 UTF-16 索引（字素边界，不含）。</param>
/// <param name="Glyphs">该段的字形序列（簇索引为绝对 UTF-16 索引）。</param>
internal readonly record struct ShapedRun(int Start, int End, List<ShapedGlyph> Glyphs);

/// <summary>按“首选字体优先、缺字回退”把文本切分为同字体运行段并立即 shaping。</summary>
/// <remarks>
/// 切分与 shaping 交错进行：字体实例在解析后立刻使用，避免同一次排版中解析大量回退字体时，
/// 先前的字体实例被有界缓存淘汰而失效。
/// </remarks>
internal static class FontRunSplitter
{
    /// <summary>把 [<paramref name="start"/>, <paramref name="end"/>) 切分成同字体运行段并 shaping。</summary>
    public static List<ShapedRun> Shape(
        string text,
        int start,
        int end,
        int[] graphemeStarts,
        SkiaFontResolver resolver,
        SKTypeface primary,
        FontDescription font)
    {
        var runs = new List<ShapedRun>();
        FontInstance? current = null;
        var runStart = start;
        var runEnd = start;

        void Flush()
        {
            if (current is null || runEnd <= runStart)
            {
                return;
            }

            var glyphs = Shaping.Shape(current, text, runStart, runEnd - runStart);
            runs.Add(new ShapedRun(runStart, runEnd, glyphs));
        }

        var index = Graphemes.BoundaryAtOrBefore(graphemeStarts, start);
        for (; index + 1 < graphemeStarts.Length; index++)
        {
            var clusterStart = graphemeStarts[index];
            if (clusterStart >= end)
            {
                break;
            }

            var clusterEnd = Math.Min(graphemeStarts[index + 1], end);
            var instance = resolver.GetFontInstanceForCharacter(primary, font, Graphemes.CodepointAt(text, clusterStart));
            if (current is null)
            {
                runStart = clusterStart;
            }
            else if (!ReferenceEquals(current, instance))
            {
                Flush();
                runStart = clusterStart;
            }

            current = instance;
            runEnd = clusterEnd;
        }

        Flush();
        return runs;
    }
}
