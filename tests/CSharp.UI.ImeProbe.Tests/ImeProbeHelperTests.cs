using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 探针纯托管辅助功能的规格测试：Win32 数据单位、CANDIDATELIST 布局、INPUT 结构布局与 PNG 编码。
/// 这些测试不调用任何原生入口，因此在任何平台都能运行。
/// </summary>
public sealed class ImeProbeHelperTests
{
    [Fact]
    public void EscapeUtf16_EmitsOneEscapePerCodeUnit()
    {
        Assert.Equal("\\u4F60\\u597D", ImeFormat.EscapeUtf16("你好"));
        Assert.Equal("\\uD83D\\uDE00", ImeFormat.EscapeUtf16("\U0001F600"));
        Assert.Equal("(null)", ImeFormat.EscapeUtf16(null));
    }

    [Fact]
    public void CodeUnitAndCodePointCounts_DifferForSurrogatePair()
    {
        const string emoji = "\U0001F600";
        Assert.Equal(2, ImeFormat.CodeUnitCount(emoji));
        Assert.Equal(1, ImeFormat.CodePointCount(emoji));
        Assert.Equal(4, ImeFormat.BytesForUtf16(emoji));

        Assert.Equal(2, ImeFormat.CodeUnitCount("你好"));
        Assert.Equal(2, ImeFormat.CodePointCount("你好"));
        Assert.Equal(0, ImeFormat.CodePointCount(string.Empty));
    }

    [Fact]
    public void ToHex_FormatsBytesAndDwords()
    {
        Assert.Equal("0001FF", ImeFormat.ToHex([0x00, 0x01, 0xFF]));
        Assert.Equal(string.Empty, ImeFormat.ToHex((byte[]?)null));
        Assert.Equal("00000001,00000002", ImeFormat.ToHex([1u, 2u]));
    }

    [Fact]
    public void GcsFlags_DecodeCompositionAndResultBits()
    {
        var flags = NativeMethods.GcsFlags(new IntPtr(0x0800 | 0x0008));
        Assert.Contains("GCS_COMPSTR", flags);
        Assert.Contains("GCS_RESULTSTR", flags);
        Assert.DoesNotContain("GCS_CURSORPOS", flags);
        Assert.Empty(NativeMethods.GcsFlags(IntPtr.Zero));
    }

    [Fact]
    public void MessageNames_AreStableForImeAndKeyMessages()
    {
        Assert.Equal("WM_IME_STARTCOMPOSITION", NativeMethods.MessageName(0x010D));
        Assert.Equal("WM_IME_COMPOSITION", NativeMethods.MessageName(0x010F));
        Assert.Equal("WM_IME_ENDCOMPOSITION", NativeMethods.MessageName(0x010E));
        Assert.Equal("WM_CHAR", NativeMethods.MessageName(0x0102));
        Assert.Equal("0xFFFF", NativeMethods.MessageName(0xFFFF));
    }

    [Fact]
    public void CandidateListParser_ReadsHandcraftedStructure()
    {
        // 按 winuser.h CANDIDATELIST 手工构造：6 个 DWORD 头 + 3 个偏移 + NUL 结尾 WCHAR 字符串。
        var blob = BuildCandidateList(["你好", "nihao", "hello"], selection: 1);
        var candidates = CandidateListParser.Parse(blob);

        Assert.Equal(3, candidates.Count);
        Assert.Equal("你好", candidates[0].Text);
        Assert.Equal("nihao", candidates[1].Text);
        Assert.Equal("hello", candidates[2].Text);
        Assert.True(candidates[1].IsSelected);
        Assert.False(candidates[0].IsSelected);
    }

    [Fact]
    public void CandidateListParser_RejectsTruncatedOrEmptyInput()
    {
        Assert.Empty(CandidateListParser.Parse(null));
        Assert.Empty(CandidateListParser.Parse([]));
        Assert.Empty(CandidateListParser.Parse(new byte[8]));
    }

    [Fact]
    public void BuildKeyboardInput_UsesDocumentedInputLayout()
    {
        var x64 = InputScript.BuildKeyboardInput(0x41, 0, 0x0002, pointerSize: 8);
        Assert.Equal(40, x64.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(x64.AsSpan(0, 4)));
        Assert.Equal(0x41, BinaryPrimitives.ReadUInt16LittleEndian(x64.AsSpan(8, 2)));
        Assert.Equal(0x0002u, BinaryPrimitives.ReadUInt32LittleEndian(x64.AsSpan(12, 4)));

        var x86 = InputScript.BuildKeyboardInput(0x41, 0x001E, 0, pointerSize: 4);
        Assert.Equal(28, x86.Length);
        Assert.Equal(0x41, BinaryPrimitives.ReadUInt16LittleEndian(x86.AsSpan(4, 2)));
        Assert.Equal(0x001E, BinaryPrimitives.ReadUInt16LittleEndian(x86.AsSpan(6, 2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(x86.AsSpan(8, 4)));
    }

    [Fact]
    public void BuildKeyboardInput_EncodesRealScanCodeWithoutVirtualKey()
    {
        // scancode 驱动：wVk=0、wScan=真实扫描码、dwFlags 含 KEYEVENTF_SCANCODE（0x8）。
        var scan = InputScript.BuildKeyboardInput(0, 0x1E, NativeMethods.KEYEVENTF_SCANCODE, pointerSize: 8);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(scan.AsSpan(8, 2)));
        Assert.Equal(0x1E, BinaryPrimitives.ReadUInt16LittleEndian(scan.AsSpan(10, 2)));
        Assert.Equal(NativeMethods.KEYEVENTF_SCANCODE, BinaryPrimitives.ReadUInt32LittleEndian(scan.AsSpan(12, 4)));
    }

    [Fact]
    public void PngWriter_ProducesDecodableRgbaImage()
    {
        const int width = 3;
        const int height = 2;
        var pixels = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
            1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255,
        };

        var path = Path.Combine(Path.GetTempPath(), $"imeprobe-png-{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.WriteRgba(path, width, height, pixels);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

            var offset = 8;
            var idat = new MemoryStream();
            var seenIhdr = false;
            var seenIend = false;
            while (offset < bytes.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                var data = bytes.AsSpan(offset + 8, length).ToArray();
                var crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));
                Assert.Equal(crc, PngWriter.Crc32.Compute(Encoding.ASCII.GetBytes(type), data));

                if (type == "IHDR")
                {
                    seenIhdr = true;
                    Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)));
                    Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)));
                    Assert.Equal(8, data[8]);
                    Assert.Equal(6, data[9]);
                }
                else if (type == "IDAT")
                {
                    idat.Write(data);
                }
                else if (type == "IEND")
                {
                    seenIend = true;
                }

                offset += 12 + length;
            }

            Assert.True(seenIhdr);
            Assert.True(seenIend);
            Assert.Equal(offset, bytes.Length);

            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new MemoryStream();
            inflate.CopyTo(raw);
            var scanlines = raw.ToArray();
            Assert.Equal(height * (1 + width * 4), scanlines.Length);

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * (1 + width * 4);
                Assert.Equal(0, scanlines[rowStart]);
                for (var x = 0; x < width * 4; x++)
                {
                    Assert.Equal(pixels[y * width * 4 + x], scanlines[rowStart + 1 + x]);
                }
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] BuildCandidateList(string[] candidates, uint selection)
    {
        var headerSize = 24 + candidates.Length * 4;
        var offsets = new uint[candidates.Length];
        var payload = new MemoryStream();
        for (var i = 0; i < candidates.Length; i++)
        {
            offsets[i] = (uint)(headerSize + payload.Length);
            payload.Write(Encoding.Unicode.GetBytes(candidates[i]));
            payload.Write([0, 0]);
        }

        var total = headerSize + (int)payload.Length;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), (uint)candidates.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), selection);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), (uint)candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24 + i * 4, 4), offsets[i]);
        }

        payload.ToArray().CopyTo(buffer, headerSize);
        return buffer;
    }
}
