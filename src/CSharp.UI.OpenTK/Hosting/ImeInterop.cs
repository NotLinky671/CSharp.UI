using System.Text;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 一次 WM_IME_COMPOSITION 当场复制的完整数据快照（宽接口读数已换算为 UTF-16 字符串）。
/// </summary>
/// <remarks>
/// <see cref="ImmGetCompositionStringW"/> 的返回量与缓冲长度均以<b>字节</b>计；读取发生在
/// <see cref="IImeInterop.ReadComposition"/>，此后 WndProc 只持有这份托管快照。
/// </remarks>
internal readonly record struct ImmCompositionSnapshot(
    long Flags,
    string? CompositionString,
    string? ResultString,
    int CursorPosition,
    IReadOnlyList<int> ClauseOffsets)
{
    /// <summary>本条消息是否声明了新的结果串（GCS_RESULTSTR）。</summary>
    public bool HasResultFlag => (Flags & Win32Ime.GCS_RESULTSTR) != 0;

    /// <summary>本条消息是否更新了预编辑相关数据（组合串/属性/条款/光标）。</summary>
    public bool HasPreeditData => (Flags & Win32Ime.GcsPreeditMask) != 0;
}

/// <summary>
/// IMM32 上下文操作抽象：生产实现为 <see cref="Imm32Interop"/>，测试用替身注入。
/// </summary>
/// <remarks>只封装“对 HIMC 的读/写”，不含窗口过程与子类化（那部分只在真实 Windows 路径执行）。</remarks>
internal interface IImeInterop
{
    /// <summary>窗口当前关联的输入上下文；没有时为 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr GetContext();

    /// <summary>释放 <see cref="GetContext"/> 取回的上下文。</summary>
    void ReleaseContext(IntPtr himc);

    /// <summary>确保窗口有关联的输入上下文（必要时关联系统默认上下文）；返回可用上下文或 Zero。</summary>
    IntPtr EnsureContext();

    /// <summary>读取组合上下文的当前快照（字节长度已在此换算为 UTF-16 串与 UTF-16 偏移）。</summary>
    ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags);

    /// <summary>发布候选窗位置（客户区像素）。</summary>
    bool SetCandidatePosition(IntPtr himc, int x, int y);

    /// <summary>发布候选排除区（客户区像素）。</summary>
    bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom);

    /// <summary>发布组合窗位置/区域（客户区像素）。</summary>
    bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom);

    /// <summary>读回 IME 报告的候选窗位置（诊断回执）。</summary>
    bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style);

    /// <summary>请求平台取消未提交组合。</summary>
    bool NotifyCancelComposition(IntPtr himc);

    /// <summary>读取自有上下文的开启状态与转换模式（验收工具保存/恢复用；只作用于本窗口 HIMC）。</summary>
    bool GetNativeInputState(IntPtr himc, out bool open, out uint conversion, out uint sentence);

    /// <summary>设置自有上下文的开启状态与转换模式；返回平台调用是否成功。</summary>
    bool SetNativeInputState(IntPtr himc, bool open, uint conversion, uint sentence);
}

/// <summary>IMM32 生产实现：宽接口按字节长度读取，换算为 UTF-16 字符串与码元偏移。</summary>
internal sealed class Imm32Interop : IImeInterop
{
    private readonly IntPtr _hwnd;

    internal Imm32Interop(IntPtr hwnd) => _hwnd = hwnd;

    public IntPtr GetContext() => Win32Ime.ImmGetContext(_hwnd);

    public void ReleaseContext(IntPtr himc)
    {
        if (himc != IntPtr.Zero)
        {
            Win32Ime.ImmReleaseContext(_hwnd, himc);
        }
    }

    public IntPtr EnsureContext()
    {
        var himc = GetContext();
        if (himc == IntPtr.Zero)
        {
            // 与 TI-01 探针一致：窗口没有上下文时关联系统默认上下文（只影响本窗口）。
            Win32Ime.ImmAssociateContextEx(_hwnd, IntPtr.Zero, Win32Ime.IACE_DEFAULT);
            himc = GetContext();
        }

        return himc;
    }

    public ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags)
    {
        // 无结果标志时上下文里可能仍保留旧结果数据：照常读取但只作快照，不作为新提交（TI-01 判定规则）。
        var comp = ReadString(himc, Win32Ime.GCS_COMPSTR);
        var result = ReadString(himc, Win32Ime.GCS_RESULTSTR);
        var cursor = (flags & Win32Ime.GCS_CURSORPOS) != 0 ? ReadCursor(himc) : -1;
        var clauses = (flags & Win32Ime.GCS_COMPCLAUSE) != 0 ? ReadDwords(himc, Win32Ime.GCS_COMPCLAUSE) : null;
        return new ImmCompositionSnapshot(flags, comp, result, cursor, clauses ?? []);
    }

    public bool SetCandidatePosition(IntPtr himc, int x, int y)
    {
        var form = new Win32Ime.CANDIDATEFORM
        {
            dwIndex = 0,
            dwStyle = Win32Ime.CFS_CANDIDATEPOS,
            ptCurrentPos = new Win32Ime.POINT { X = x, Y = y },
        };
        return Win32Ime.ImmSetCandidateWindow(himc, ref form);
    }

    public bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom)
    {
        var form = new Win32Ime.CANDIDATEFORM
        {
            dwIndex = 0,
            dwStyle = Win32Ime.CFS_EXCLUDE,
            ptCurrentPos = new Win32Ime.POINT { X = left, Y = top },
            rcArea = new Win32Ime.RECT { Left = left, Top = top, Right = right, Bottom = bottom },
        };
        return Win32Ime.ImmSetCandidateWindow(himc, ref form);
    }

    public bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom)
    {
        var form = new Win32Ime.COMPOSITIONFORM
        {
            dwStyle = Win32Ime.CFS_RECT,
            ptCurrentPos = new Win32Ime.POINT { X = x, Y = y },
            rcArea = new Win32Ime.RECT { Left = left, Top = top, Right = right, Bottom = bottom },
        };
        return Win32Ime.ImmSetCompositionWindow(himc, ref form);
    }

    public bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style)
    {
        if (Win32Ime.ImmGetCandidateWindow(himc, 0, out var form))
        {
            x = form.ptCurrentPos.X;
            y = form.ptCurrentPos.Y;
            style = form.dwStyle;
            return true;
        }

        x = y = 0;
        style = 0;
        return false;
    }

    public bool NotifyCancelComposition(IntPtr himc)
        => Win32Ime.ImmNotifyIME(himc, Win32Ime.NI_COMPOSITIONSTR, Win32Ime.CPS_CANCEL, 0);

    public bool GetNativeInputState(IntPtr himc, out bool open, out uint conversion, out uint sentence)
    {
        open = Win32Ime.ImmGetOpenStatus(himc);
        var ok = Win32Ime.ImmGetConversionStatus(himc, out conversion, out sentence);
        return ok;
    }

    public bool SetNativeInputState(IntPtr himc, bool open, uint conversion, uint sentence)
    {
        var openOk = Win32Ime.ImmSetOpenStatus(himc, open);
        var convOk = Win32Ime.ImmSetConversionStatus(himc, conversion, sentence);
        return openOk && convOk;
    }

    /// <summary>宽接口字符串读取的字节上限：声明值异常时安全拒绝，不分配无界缓冲。</summary>
    private const int MaxImmStringBytes = 64 * 1024;

    /// <summary>严格 UTF-16 解码器：未配对代理项/截断序列直接拒绝，绝不静默替换成 U+FFFD 提交。</summary>
    private static readonly Encoding StrictUtf16 =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private static string? ReadString(IntPtr himc, uint index)
    {
        // 宽接口的返回量与缓冲长度均以字节计（TI-01 证据）：负数=查询失败；奇数=截断的 UTF-16 码元；
        // 超过上限的声明值直接拒绝，不分配可疑大小的缓冲。
        var reported = Win32Ime.ImmGetCompositionStringW(himc, index, null, 0);
        if (reported <= 0 || reported > MaxImmStringBytes || (reported & 1) != 0)
        {
            return null;
        }

        var buffer = new byte[reported];
        var written = Win32Ime.ImmGetCompositionStringW(himc, index, buffer, reported);
        // 二次读取只允许 0 < written <= reported 且仍是偶数字节：越界写入会破坏缓冲并拿到脏数据。
        if (written <= 0 || written > reported || (written & 1) != 0)
        {
            return null;
        }

        return DecodeUtf16(buffer, written);
    }

    /// <summary>
    /// 把 <paramref name="byteCount"/> 字节的 UTF-16LE 数据严格解码为字符串；
    /// 奇数长度、未配对代理项或任何解码错误一律返回 null（拒绝），不产生 U+FFFD 替换。
    /// </summary>
    internal static string? DecodeUtf16(byte[] buffer, int byteCount)
    {
        if (buffer is null || byteCount < 0 || byteCount > buffer.Length || (byteCount & 1) != 0)
        {
            return null;
        }

        try
        {
            return StrictUtf16.GetString(buffer, 0, byteCount);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static int ReadCursor(IntPtr himc)
    {
        var value = Win32Ime.ImmGetCompositionStringW(himc, Win32Ime.GCS_CURSORPOS, null, 0);
        return value < 0 ? -1 : value;
    }

    private static int[]? ReadDwords(IntPtr himc, uint index)
    {
        var reported = Win32Ime.ImmGetCompositionStringW(himc, index, null, 0);
        if (reported < 4 || reported > MaxImmStringBytes)
        {
            return null;
        }

        var buffer = new byte[reported];
        var written = Win32Ime.ImmGetCompositionStringW(himc, index, buffer, reported);
        if (written < 4 || written > reported || (written & 3) != 0)
        {
            return null;
        }

        // GCS_COMPCLAUSE 返回的是 UTF-16 码元偏移的 DWORD 数组（宽接口版本）。
        var count = written / 4;
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = unchecked((int)BitConverter.ToUInt32(buffer, i * 4));
        }

        return result;
    }
}
