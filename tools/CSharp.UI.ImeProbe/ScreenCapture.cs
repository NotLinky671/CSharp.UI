using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CSharp.UI.ImeProbe;

/// <summary>最小 PNG 编码器（RGBA8，无过滤），避免为证据截图引入新的图像库依赖。</summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, width * height * 4);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter: adaptive
        header[12] = 0; // interlace: none
        WriteChunk(stream, "IHDR", header);

        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 4);
            raw[rowStart] = 0; // filter type 0 (None)
            Buffer.BlockCopy(rgba, y * width * 4, raw, rowStart + 1, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(byte[] typeBytes, byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in typeBytes)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}

/// <summary>证据截图：GDI BitBlt 只抓取探针自有窗口矩形，不抓整个桌面，也不向外做任何任意边距扩张。</summary>
internal static class ScreenCapture
{
    /// <summary>抓取探针自有窗口矩形（可见时）并写成 PNG。</summary>
    /// <remarks>
    /// 不接受任意 margin：窗口外区域未必可归属候选窗口。需要窗口外候选证据时，调用方必须先用系统 API
    /// 确认候选窗口的归属与边界，再用 <see cref="CaptureRegion"/> 按确证矩形单独采集。
    /// 返回实际抓取到的屏幕矩形，调用方据此记录证据范围。
    /// </remarks>
    internal static (int X, int Y, int Width, int Height, string Path)? CaptureWindowRegion(
        IntPtr hwnd, string path)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            // 不可见的窗口没有可核对的自有绘制内容，不做任何推测性抓取。
            return null;
        }

        return CaptureRegion(rect.Left, rect.Top, rect.Width, rect.Height, path) is { } result
            ? (rect.Left, rect.Top, result.Width, result.Height, result.Path)
            : null;
    }

    /// <summary>抓取指定屏幕矩形（桌面坐标）并写成 PNG。</summary>
    internal static (int Width, int Height, string Path)? CaptureRegion(int x, int y, int width, int height, string path)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SRCCOPY))
            {
                return null;
            }

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
                biSizeImage = (uint)(width * height * 4),
            };

            var bgra = new byte[width * height * 4];
            if (NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, bgra, ref header, NativeMethods.DIB_RGB_COLORS) == 0)
            {
                return null;
            }

            var rgba = new byte[bgra.Length];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = 0xFF;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".");
            PngWriter.WriteRgba(path, width, height, rgba);
            return (width, height, System.IO.Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
