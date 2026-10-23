using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// TSF/TextStore COM 契约（TI-07 P2b 生产模块）。
/// </summary>
/// <remarks>
/// <para>
/// <b>签名权威：</b>本机 Windows SDK <c>10.0.26100.0</c> 的 <c>um/TextStor.idl</c> 与 <c>um/msctf.idl</c>。
/// 全部声明从标签 <c>archive/tools-before-removal</c> 中的 <c>tools/CSharp.UI.TsfHostCheck/TsfInterop.cs</c> 迁移，仅剥离诊断；
/// <b>没有</b>按记忆或推测改写任何 ABI、IID、vtable 顺序或封送属性。
/// </para>
/// <para>
/// <b>PreserveSig：</b>所有方法都保留原始 HRESULT，绝不把托管异常穿过原生边界。
/// </para>
/// </remarks>
internal static class TsfIids
{
    internal static readonly Guid TextStoreAcp = new("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D");
    internal static readonly Guid TextStoreAcpSink = new("22D44C94-A419-4542-A272-AE26093ECECF");
    internal static readonly Guid ThreadMgr = new("AA80E801-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid ThreadMgrEx = new("3e90ade3-7594-4cb0-bb58-69628f5f458c");
    internal static readonly Guid DocumentMgr = new("AA80E7F4-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid Context = new("AA80E7FD-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid ContextView = new("2433BF8E-0F9B-435C-BA2C-180611978C30");
    internal static readonly Guid Source = new("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");
    internal static readonly Guid ContextOwnerCompositionSink = new("5F20AA40-B57A-4F34-96AB-3576F377CC79");
    internal static readonly Guid EditSession = new("AA80E803-2021-11D2-93E0-0060B067B86E");

    /// <summary><c>ITfUIElementMgr</c>（msctf.idl:2648）：UIElement 管理器，应用据此注册 sink。</summary>
    internal static readonly Guid UiElementMgr = new("EA1EA135-19DF-11D7-A6D2-00065B84435C");

    /// <summary><c>ITfUIElement</c>（msctf.idl:2702）。</summary>
    internal static readonly Guid UiElement = new("EA1EA137-19DF-11D7-A6D2-00065B84435C");

    /// <summary><c>ITfUIElementSink</c>（msctf.idl:2686）：Begin/Update/EndUIElement 回调。</summary>
    internal static readonly Guid UiElementSink = new("EA1EA136-19DF-11D7-A6D2-00065B84435C");

    /// <summary><c>ITfCandidateListUIElement</c>（msctf.idl:2719）：候选列表只读快照来源。</summary>
    internal static readonly Guid CandidateListUiElement = new("EA1EA138-19DF-11D7-A6D2-00065B84435C");

    /// <summary><c>ITfKeystrokeMgr</c>（msctf.idl）：TSF 键盘管理器，应用用它询问/转发按键。</summary>
    internal static readonly Guid KeystrokeMgr = new("AA80E7F0-2021-11D2-93E0-0060B067B86E");
}

/// <summary>HRESULT 常量（TextStor.h、TextStor.idl、msctf.h）。</summary>
internal static class TsfHResult
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_FAIL = unchecked((int)0x80004005);

    /// <summary>
    /// <c>CO_E_OBJNOTCONNECTED</c>（<c>shared/winerror.h</c>：0x800401FD）。
    /// 用于“连接对象已释放/未连接”的显式拒绝。
    /// </summary>
    internal const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);

    /// <summary>
    /// <c>RPC_E_WRONG_THREAD</c>（<c>shared/winerror.h</c>：0x8001010E）。
    /// 用于“非所属线程”的显式拒绝。
    /// </summary>
    internal const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    // OLE 连接错误（olectl.h：CONNECT_E_FIRST = MAKE_SCODE(SEVERITY_ERROR, FACILITY_ITF, 0x0200)）。
    // 重要：它们与 TextStor 的 TS_E_* 数值重叠（同一 FACILITY_ITF 区间），这是 SDK 事实：
    //   CONNECT_E_NOCONNECTION = 0x80040200 == TS_E_INVALIDPOS
    //   CONNECT_E_ADVISELIMIT  = 0x80040201 == TS_E_NOLOCK
    // 二者靠“哪个方法返回”区分，不靠数值。Describe 会按 TS_E_* 命名，仅影响诊断文字，不影响协议。
    /// <summary>UnadviseSink：没有活动 sink 连接（官方 CONNECT_E_NOCONNECTION，与 TS_E_INVALIDPOS 同值）。</summary>
    internal const int CONNECT_E_NOCONNECTION = unchecked((int)0x80040200);

    /// <summary>AdviseSink：无法取得 sink 接口指针（官方 CONNECT_E_ADVISELIMIT，与 TS_E_NOLOCK 同值）。</summary>
    internal const int CONNECT_E_ADVISELIMIT = unchecked((int)0x80040201);

    internal const int TS_E_INVALIDPOS = unchecked((int)0x80040200);
    internal const int TS_E_NOLOCK = unchecked((int)0x80040201);
    internal const int TS_E_NOOBJECT = unchecked((int)0x80040202);
    internal const int TS_E_NOINTERFACE = unchecked((int)0x80040204);
    internal const int TS_E_NOSELECTION = unchecked((int)0x80040205);
    internal const int TS_E_NOLAYOUT = unchecked((int)0x80040206);
    internal const int TS_E_INVALIDPOINT = unchecked((int)0x80040207);
    internal const int TS_E_SYNCHRONOUS = unchecked((int)0x80040208);
    internal const int TS_E_FORMAT = unchecked((int)0x8004020A);
    internal const int TS_S_ASYNC = 0x00040300;

    internal const int TF_E_INVALIDVIEW = unchecked((int)0x80040505);

    /// <summary>
    /// <c>TF_E_NOLOCK</c>（msctf.h）：<c>TerminateComposition</c> 等要求同步写锁的方法此刻拿不到锁。
    /// </summary>
    /// <remarks>
    /// <b>数值与 <c>TS_E_NOLOCK</c> 相同</b>（同为 <c>FACILITY_ITF 0x0201</c>），这是 SDK 事实；
    /// 二者靠“哪个方法返回”区分。取消路径据此判定“可重试”，因此必须单独命名而不复用 TS_ 常量。
    /// </remarks>
    internal const int TF_E_NOLOCK = TS_E_NOLOCK;

    /// <summary><c>TF_E_DISCONNECTED</c>（msctf.h 0x80040507）：context 不在文档栈上。</summary>
    internal const int TF_E_DISCONNECTED = unchecked((int)0x80040507);

    internal static string Describe(int hr) => hr switch
    {
        S_OK => "S_OK",
        S_FALSE => "S_FALSE",
        E_FAIL => "E_FAIL",
        E_INVALIDARG => "E_INVALIDARG",
        E_NOTIMPL => "E_NOTIMPL",
        E_NOINTERFACE => "E_NOINTERFACE",
        E_UNEXPECTED => "E_UNEXPECTED",
        TS_E_INVALIDPOS => "TS_E_INVALIDPOS",
        TS_E_NOLOCK => "TS_E_NOLOCK",
        TS_E_NOOBJECT => "TS_E_NOOBJECT",
        TS_E_NOINTERFACE => "TS_E_NOINTERFACE",
        TS_E_NOSELECTION => "TS_E_NOSELECTION",
        TS_E_NOLAYOUT => "TS_E_NOLAYOUT",
        TS_E_INVALIDPOINT => "TS_E_INVALIDPOINT",
        TS_E_SYNCHRONOUS => "TS_E_SYNCHRONOUS",
        TS_E_FORMAT => "TS_E_FORMAT",
        TS_S_ASYNC => "TS_S_ASYNC",
        TF_E_INVALIDVIEW => "TF_E_INVALIDVIEW",
        TF_E_DISCONNECTED => "TF_E_DISCONNECTED",
        _ => $"0x{hr:X8}",
    };
}

/// <summary>TextStor/msctf 常量（逐项来自 SDK IDL/头文件）。</summary>
internal static class TsfFlags
{
    internal const uint TS_AS_TEXT_CHANGE = 0x01;
    internal const uint TS_AS_SEL_CHANGE = 0x02;
    internal const uint TS_AS_LAYOUT_CHANGE = 0x04;
    internal const uint TS_AS_ALL_SINKS = 0x1F;

    internal const uint TS_LF_SYNC = 0x1;
    internal const uint TS_LF_READ = 0x2;
    internal const uint TS_LF_WRITE = 0x4;
    internal const uint TS_LF_READWRITE = 0x6;

    /// <summary>合法锁标志白名单：SYNC 可选，READ 必须包含，其它位一律拒绝。</summary>
    internal const uint TS_LF_ALLOWED = TS_LF_SYNC | TS_LF_READ | TS_LF_WRITE;

    internal const uint TS_TC_CORRECTION = 0x1;
    internal const uint TS_ST_CORRECTION = 0x1;
    internal const uint TS_IAS_NOQUERY = 0x1;
    internal const uint TS_IAS_QUERYONLY = 0x2;

    internal const uint GXFPF_ROUND_NEAREST = 0x1;
    internal const uint GXFPF_NEAREST = 0x2;

    internal const uint TS_DEFAULT_SELECTION = 0xFFFFFFFF;
    internal const uint TS_VCOOKIE_NUL = 0xFFFFFFFF;
    internal const int TS_LC_CREATE = 0;
    internal const int TS_LC_CHANGE = 1;
    internal const int TS_LC_DESTROY = 2;

    internal const int TS_AE_NONE = 0;
    internal const int TS_AE_START = 1;
    internal const int TS_AE_END = 2;

    internal const uint TF_ES_SYNC = 0x1;
    internal const uint TF_ES_READ = 0x2;
    internal const uint TF_ES_READWRITE = 0x6;
    internal const uint TF_ES_ASYNC = 0x8;

    // P3b：ITfThreadMgrEx::ActivateEx / GetActiveFlags 标志（msctf.idl:268-284）。
    // ActivateEx 与 GetActiveFlags 的这些位一一对应（TF_TMAE_* == TF_TMF_*）。
    internal const uint TF_TMAE_NOACTIVATETIP = 0x00000001;
    internal const uint TF_TMAE_SECUREMODE = 0x00000002;

    /// <summary>仅激活具备 UIElement 能力的 TIP；P3b 唯一使用的激活变量。</summary>
    internal const uint TF_TMAE_UIELEMENTENABLEDONLY = 0x00000004;

    internal const uint TF_TMAE_COMLESS = 0x00000008;
    internal const uint TF_TMAE_WOW16 = 0x00000010;

    /// <summary>GetActiveFlags/DWORD 专有位：TSF 当前确实处于激活状态（msctf.idl:284）。</summary>
    internal const uint TF_TMF_ACTIVATED = 0x80000000;

    /// <summary><c>TF_INVALID_COOKIE</c>（msctf.idl:2602 / msctf.h）：无效 sink cookie。</summary>
    internal const uint TF_INVALID_COOKIE = 0xFFFFFFFF;

    internal const uint TS_SD_READONLY = 0x1;
    internal const uint TS_SD_LOADING = 0x2;
    internal const uint TS_SS_DISJOINTSEL = 0x1;
    internal const uint TS_SS_REGIONS = 0x2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsfPoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsfRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;

    public readonly bool IsEmpty => Right <= Left || Bottom <= Top;

    public static TsfRect FromLtrb(int left, int top, int right, int bottom)
        => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsStatus
{
    public uint DynamicFlags;
    public uint StaticFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsSelectionStyle
{
    public int ActiveSelEnd;
    public int InterimChar;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsSelectionAcp
{
    public int AcpStart;
    public int AcpEnd;
    public TsSelectionStyle Style;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsTextChange
{
    public int AcpStart;
    public int AcpOldEnd;
    public int AcpNewEnd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TsRunInfo
{
    public uint Count;
    public int Type;
}

internal static class TsRunType
{
    internal const int Plain = 0;
}

// ---------------------------------------------------------------------------
// 应用实现（CCW 交给 msctf）
// ---------------------------------------------------------------------------

[ComImport]
[Guid("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITextStoreACP
{
    [PreserveSig]
    int AdviseSink([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwMask);

    [PreserveSig]
    int UnadviseSink([MarshalAs(UnmanagedType.IUnknown)] object punk);

    [PreserveSig]
    int RequestLock(uint dwLockFlags, out int phrSession);

    [PreserveSig]
    int GetStatus(out TsStatus pdcs);

    [PreserveSig]
    int QueryInsert(int acpTestStart, int acpTestEnd, uint cch,
        out int pacpResultStart, out int pacpResultEnd);

    [PreserveSig]
    int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);

    [PreserveSig]
    int SetSelection(uint ulCount, IntPtr pSelection);

    [PreserveSig]
    int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq,
        out uint pcchPlainRet, IntPtr prgRunInfo, uint cRunInfoReq,
        out uint pcRunInfoRet, out int pacpNext);

    [PreserveSig]
    int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch,
        out TsTextChange pChange);

    [PreserveSig]
    int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject);

    [PreserveSig]
    int GetEmbedded(int acpPos, IntPtr rguidService, IntPtr riid, out IntPtr ppunk);

    [PreserveSig]
    int QueryInsertEmbedded(IntPtr pguidService, IntPtr pFormatEtc, out int pfInsertable);

    [PreserveSig]
    int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd,
        [MarshalAs(UnmanagedType.IUnknown)] object pDataObject, out TsTextChange pChange);

    [PreserveSig]
    int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
        IntPtr pacpStart, IntPtr pacpEnd, IntPtr pChange);

    [PreserveSig]
    int InsertEmbeddedAtSelection(uint dwFlags,
        [MarshalAs(UnmanagedType.IUnknown)] object pDataObject,
        IntPtr pacpStart, IntPtr pacpEnd, IntPtr pChange);

    [PreserveSig]
    int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs);

    [PreserveSig]
    int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);

    [PreserveSig]
    int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags);

    [PreserveSig]
    int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags, out int pacpNext, out int pfFound,
        out int plFoundOffset);

    [PreserveSig]
    int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched);

    [PreserveSig]
    int GetEndACP(out int pacp);

    [PreserveSig]
    int GetActiveView(out uint pvcView);

    [PreserveSig]
    int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp);

    [PreserveSig]
    int GetTextExt(uint vcView, int acpStart, int acpEnd, out TsfRect prc, out int pfClipped);

    [PreserveSig]
    int GetScreenExt(uint vcView, out TsfRect prc);

    [PreserveSig]
    int GetWnd(uint vcView, out IntPtr phwnd);
}

[ComImport]
[Guid("22D44C94-A419-4542-A272-AE26093ECECF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITextStoreACPSink
{
    [PreserveSig]
    int OnTextChange(uint dwFlags, ref TsTextChange pChange);

    [PreserveSig]
    int OnSelectionChange();

    [PreserveSig]
    int OnLayoutChange(int lcode, uint vcView);

    [PreserveSig]
    int OnStatusChange(uint dwFlags);

    [PreserveSig]
    int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs);

    [PreserveSig]
    int OnLockGranted(uint dwLockFlags);

    [PreserveSig]
    int OnStartEditTransaction();

    [PreserveSig]
    int OnEndEditTransaction();
}

[ComImport]
[Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfContextOwnerCompositionSink
{
    [PreserveSig]
    int OnStartComposition(ITfCompositionView pComposition, out int pfOk);

    [PreserveSig]
    int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew);

    [PreserveSig]
    int OnEndComposition(ITfCompositionView pComposition);
}

[ComImport]
[Guid("AA80E803-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfEditSession
{
    [PreserveSig]
    int DoEditSession(uint ec);
}

// ---------------------------------------------------------------------------
// msctf 提供、本模块消费（仅上下文/文档管理器；不启用 TIP、不接键鼠）
// ---------------------------------------------------------------------------

[ComImport]
[Guid("AA80E801-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgr
{
    [PreserveSig]
    int Activate(out uint ptid);

    [PreserveSig]
    int Deactivate();

    [PreserveSig]
    int CreateDocumentMgr(out ITfDocumentMgr ppdim);

    [PreserveSig]
    int EnumDocumentMgrs(out IntPtr ppEnum);

    [PreserveSig]
    int GetFocus(out ITfDocumentMgr ppdimFocus);

    [PreserveSig]
    int SetFocus(ITfDocumentMgr? pdimFocus);

    [PreserveSig]
    int AssociateFocus(IntPtr hwnd, ITfDocumentMgr? pdimNew, out ITfDocumentMgr? ppdimPrev);

    [PreserveSig]
    int IsThreadFocus(out int pfThreadFocus);

    [PreserveSig]
    int GetFunctionProvider([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, out IntPtr ppFuncProv);

    [PreserveSig]
    int EnumFunctionProviders(out IntPtr ppEnum);

    [PreserveSig]
    int GetGlobalCompartment(out IntPtr ppCompMgr);
}

/// <summary>
/// <c>ITfKeystrokeMgr</c>（msctf.h）：TSF 键盘管理器。
/// </summary>
/// <remarks>
/// <para>
/// <b>官方契约（必须严格遵守）：</b>先 <see cref="TestKeyDown"/>，只有返回 <c>S_OK</c> <b>并且</b>
/// <c>pfEaten</c> 为 TRUE 才调用 <see cref="KeyDown"/>；只有 <c>KeyDown</c> 也返回 <c>S_OK</c> 且
/// <c>pfEaten</c> 为 TRUE 才<b>消费</b>该按键（不再交给应用）。任一条件不满足都必须把按键原样交回应用。
/// </para>
/// <para>
/// <b>不能提前消费：</b><c>TestKeyDown</c> 只是询问，若在它返回前就吞掉按键，会破坏输入法/应用双方的
/// 语义；同理 <c>S_FALSE</c>（没有 key event sink）表示没有任何输入法关心该键。
/// </para>
/// <para>
/// <b>方法顺序：</b>按 msctf.idl 声明顺序（IUnknown 之外）：AdviseKeyEventSink、UnadviseKeyEventSink、
/// GetForeground、TestKeyDown、TestKeyUp、KeyDown、KeyUp、GetPreservedKey、IsPreservedKey、
/// PreserveKey、UnpreserveKey、SetPreservedKeyDescription、GetPreservedKeyDescription、
/// SimulatePreservedKey。本类型只声明需要的前 7 个，其余不参与 ABI 调用。
/// </para>
/// </remarks>
[ComImport]
[Guid("AA80E7F0-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfKeystrokeMgr
{
    /// <summary>注册按键事件 sink（本批不使用；保留槽位以维持 ABI 顺序）。</summary>
    [PreserveSig]
    int AdviseKeyEventSink(uint tid, [MarshalAs(UnmanagedType.IUnknown)] object pSink, int fForeground);

    /// <summary>撤销按键事件 sink（本批不使用；保留槽位以维持 ABI 顺序）。</summary>
    [PreserveSig]
    int UnadviseKeyEventSink(uint tid);

    /// <summary>
    /// 取得前台 TSF 文本服务的 CLSID（本批不使用；保留槽位以维持 ABI 顺序）。
    /// </summary>
    /// <remarks>
    /// 官方签名是 <c>HRESULT GetForeground([out] CLSID *pclsid)</c>：<b>没有</b>输入 CLSID 参数，
    /// <b>不</b>返回 <c>IUnknown</c>。声明成“输入 Guid + out IntPtr”会让原生侧把我们的指针当
    /// <c>pclsid</c> 写入 16 字节，属于真实的缓冲区覆盖缺陷。
    /// </remarks>
    [PreserveSig]
    int GetForeground(out Guid pclsid);

    /// <summary>
    /// 询问键盘管理器是否会处理该 key down。
    /// </summary>
    /// <returns><c>S_OK</c> 且 <paramref name="eaten"/> 非 0 时才可调用 <see cref="KeyDown"/>。</returns>
    [PreserveSig]
    int TestKeyDown(IntPtr wParam, IntPtr lParam, out int eaten);

    /// <summary>询问键盘管理器是否会处理该 key up。</summary>
    [PreserveSig]
    int TestKeyUp(IntPtr wParam, IntPtr lParam, out int eaten);

    /// <summary>把 key down 交给键盘管理器；只有 <c>S_OK</c> 且 <paramref name="eaten"/> 非 0 才算消费。</summary>
    [PreserveSig]
    int KeyDown(IntPtr wParam, IntPtr lParam, out int eaten);

    /// <summary>把 key up 交给键盘管理器；只有 <c>S_OK</c> 且 <paramref name="eaten"/> 非 0 才算消费。</summary>
    [PreserveSig]
    int KeyUp(IntPtr wParam, IntPtr lParam, out int eaten);
}

[ComImport]
[Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfDocumentMgr
{    [PreserveSig]
    int CreateContext(uint tidOwner, uint dwFlags,
        [MarshalAs(UnmanagedType.IUnknown)] object? punk,
        out ITfContext ppic, out uint pecTextStore);

    [PreserveSig]
    int Push(ITfContext pic);

    [PreserveSig]
    int Pop(uint dwFlags);

    [PreserveSig]
    int GetTop(out ITfContext ppic);

    [PreserveSig]
    int GetBase(out ITfContext ppic);

    [PreserveSig]
    int EnumContexts(out IntPtr ppEnum);
}

[ComImport]
[Guid("AA80E7FD-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfContext
{
    // RequestEditSession 的参数必须是精确的 ITfEditSession：声明成 IUnknown 时 CLR 只传 IUnknown 身份，
    // TSF 仍按契约调用 slot 3(DoEditSession)，真实会话会原生崩溃（已验证）。
    [PreserveSig]
    int RequestEditSession(uint tid, ITfEditSession pes, uint dwFlags, out int phrSession);

    [PreserveSig]
    int InWriteSession(uint tid, out int pfWriteSession);

    [PreserveSig]
    int GetSelection(uint ec, uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);

    [PreserveSig]
    int SetSelection(uint ec, uint ulCount, IntPtr pSelection);

    [PreserveSig]
    int GetStart(uint ec, out IntPtr ppStart);

    [PreserveSig]
    int GetEnd(uint ec, out IntPtr ppEnd);

    [PreserveSig]
    int GetActiveView(out ITfContextView ppView);

    [PreserveSig]
    int EnumViews(out IntPtr ppEnum);

    [PreserveSig]
    int GetStatus(out TsStatus pdcs);

    [PreserveSig]
    int GetProperty([MarshalAs(UnmanagedType.LPStruct)] Guid guidProp, out IntPtr ppProp);

    [PreserveSig]
    int GetAppProperty([MarshalAs(UnmanagedType.LPStruct)] Guid guidProp, out IntPtr ppProp);

    [PreserveSig]
    int TrackProperties(IntPtr prgProp, uint cProp, IntPtr prgAppProp, uint cAppProp,
        out IntPtr ppProperty);

    [PreserveSig]
    int EnumProperties(out IntPtr ppEnum);

    [PreserveSig]
    int GetDocumentMgr(out ITfDocumentMgr ppDm);

    [PreserveSig]
    int CreateRangeBackup(uint ec, IntPtr pRange, out IntPtr ppBackup);
}

[ComImport]
[Guid("2433BF8E-0F9B-435C-BA2C-180611978C30")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfContextView
{
    [PreserveSig]
    int GetRangeFromPoint(uint ec, IntPtr ppt, uint dwFlags, out IntPtr ppRange);

    [PreserveSig]
    int GetTextExt(uint ec, IntPtr pRange, out TsfRect prc, out int pfClipped);

    [PreserveSig]
    int GetScreenExt(out TsfRect prc);

    [PreserveSig]
    int GetWnd(out IntPtr phwnd);
}

[ComImport]
[Guid("D7540241-F9A1-4364-BEFC-DBCD2C4395B7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCompositionView
{
    [PreserveSig]
    int GetOwnerClsid(out Guid pclsid);

    [PreserveSig]
    int GetRange(out IntPtr ppRange);
}

internal static class TsfNative
{
    /// <summary>TF_CreateThreadMgr（msctf.h）：在当前线程创建/激活 TSF 线程管理器。</summary>
    [DllImport("msctf.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int TF_CreateThreadMgr(
        [MarshalAs(UnmanagedType.Interface)] out ITfThreadMgr ppTim);
}
