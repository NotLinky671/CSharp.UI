using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

[StructLayout(LayoutKind.Sequential)]
internal struct GdiSize
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TextMetricW
{
    public int Height;
    public int Ascent;
    public int Descent;
    public int InternalLeading;
    public int ExternalLeading;
    public int AveCharWidth;
    public int MaxCharWidth;
    public int Weight;
    public int Overhang;
    public int DigitizedAspectX;
    public int DigitizedAspectY;
    public char FirstChar;
    public char LastChar;
    public char DefaultChar;
    public char BreakChar;
    public byte Italic;
    public byte Underlined;
    public byte StruckOut;
    public byte PitchAndFamily;
    public byte CharsetValue;
}

/// <summary>GDI 文本度量与绘制入口；只用于自有原型窗口。</summary>
internal static class Gdi
{
    internal const int DefaultCharset = 1;
    internal const int OutTtPrecis = 4;
    internal const int ClipDefaultPrecis = 0;
    internal const int ClearTypeQuality = 5;
    internal const int DefaultPitch = 0;
    internal const int FfDontCare = 0;
    internal const int FwNormal = 400;
    internal const int Transparent = 1;
    internal const uint Dithered = 1;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextExtentExPointW(
        IntPtr hdc, string lpszStr, int cchString, int nMaxExtent,
        IntPtr lpnFit, [Out] int[]? lpnDx, out GdiSize lpSize);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextMetricsW(IntPtr hdc, out TextMetricW lptm);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr hDC, ref TsfRect lprc, IntPtr hbr);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}

/// <summary>真实字体的稳定度量：行高来自 <c>TEXTMETRICW</c>，逐码元前缀宽度来自 <c>GetTextExtentExPointW</c>。</summary>
internal sealed class GdiFont : IDisposable
{
    private IntPtr _dc;
    private IntPtr _font;
    private IntPtr _previousFont;

    private GdiFont(IntPtr dc, IntPtr font, IntPtr previousFont, int rowHeight, int ascent, string family)
    {
        _dc = dc;
        _font = font;
        _previousFont = previousFont;
        RowHeight = rowHeight;
        Ascent = ascent;
        Family = family;
    }

    internal int RowHeight { get; }

    internal int Ascent { get; }

    internal string Family { get; }

    internal static GdiFont? TryCreate(string preferredFamily, int pixelHeight)
    {
        var screen = Gdi.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var dc = IntPtr.Zero;
        var font = IntPtr.Zero;
        try
        {
            dc = Gdi.CreateCompatibleDC(screen);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            var height = Math.Max(8, pixelHeight);
            foreach (var family in new[] { preferredFamily, "Microsoft YaHei UI", "Segoe UI" })
            {
                font = Gdi.CreateFontW(
                    -height, 0, 0, 0, Gdi.FwNormal, 0, 0, 0, Gdi.DefaultCharset,
                    Gdi.OutTtPrecis, Gdi.ClipDefaultPrecis, Gdi.ClearTypeQuality,
                    Gdi.DefaultPitch | Gdi.FfDontCare, family);
                if (font == IntPtr.Zero)
                {
                    continue;
                }

                var previous = Gdi.SelectObject(dc, font);
                if (!Gdi.GetTextMetricsW(dc, out var metrics))
                {
                    Gdi.SelectObject(dc, previous);
                    Gdi.DeleteObject(font);
                    font = IntPtr.Zero;
                    continue;
                }

                return new GdiFont(dc, font, previous, metrics.Height, metrics.Ascent, family);
            }

            Gdi.DeleteDC(dc);
            return null;
        }
        finally
        {
            Gdi.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal int[] MeasureAdvances(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var advances = new int[text.Length];
        if (text.Length == 0)
        {
            return advances;
        }

        var dx = new int[text.Length];
        if (!Gdi.GetTextExtentExPointW(_dc, text, text.Length, 0, IntPtr.Zero, dx, out _))
        {
            // 度量失败时退化为等宽；日志由调用方记录，不在原生回调内重试。
            Array.Fill(advances, Math.Max(1, RowHeight / 2));
            return advances;
        }

        var previous = 0;
        for (var i = 0; i < text.Length; i++)
        {
            advances[i] = Math.Max(0, dx[i] - previous);
            previous = dx[i];
        }

        return advances;
    }

    /// <summary>
    /// 用<b>本字体实例</b>实测整串宽度（物理像素）。overlay 的矩形与页脚宽度、
    /// 以及随后 <c>TextOutW</c> 的绘制必须来自同一个 <see cref="GdiFont"/>——
    /// 测量与绘制绝不各建一个字体。度量失败返回 false（调用方如实失败，不猜宽度）。
    /// </summary>
    internal bool TryMeasureText(string text, out int width)
    {
        width = 0;
        if (string.IsNullOrEmpty(text) || _dc == IntPtr.Zero)
        {
            return !string.IsNullOrEmpty(text);
        }

        if (!Gdi.GetTextExtentExPointW(_dc, text, text.Length, 0, IntPtr.Zero, null, out var size))
        {
            width = 0;
            return false;
        }

        width = size.Cx;
        return true;
    }

    internal IntPtr Dc => _dc;

    internal IntPtr Handle => _font;

    public void Dispose()
    {
        if (_dc == IntPtr.Zero)
        {
            return;
        }

        if (_previousFont != IntPtr.Zero)
        {
            Gdi.SelectObject(_dc, _previousFont);
            _previousFont = IntPtr.Zero;
        }

        if (_font != IntPtr.Zero)
        {
            Gdi.DeleteObject(_font);
            _font = IntPtr.Zero;
        }

        Gdi.DeleteDC(_dc);
        _dc = IntPtr.Zero;
    }
}
