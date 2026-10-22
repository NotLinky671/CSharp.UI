using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// TSF/TextStore COM 契约。全部签名逐项核对本机 Windows SDK 10.0.26100.0：
/// <c>um/TextStor.idl</c>（ITextStoreACP 181-303、ITextStoreACPSink 448-469）与
/// <c>um/msctf.idl</c>（ITfThreadMgr 535-561、ITfDocumentMgr 701-720、ITfContext 927-1010、
/// ITfContextView 881-896、ITfSource 2612-2619、ITfKeystrokeMgr 2163-2216、
/// ITfContextOwnerCompositionSink 862-871、ITfUIElementMgr 2648-2655、ITfUIElement 2702-2708、
/// ITfUIElementSink 2686-2691、ITfCandidateListUIElement 2719-2733、ITfCompositionView 752-757、
/// ITfEditSession 1402-1405）。
/// </summary>
/// <remarks>
/// 所有方法使用 <see cref="PreserveSigAttribute"/>：实现侧返回原始 HRESULT，绝不把托管异常
/// 穿过原生边界；调用侧也按 HRESULT 逐项判定，不把 S_OK 当业务成功。
/// </remarks>
internal static class TsfIids
{
    internal static readonly Guid TextStoreAcp = new("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D");
    internal static readonly Guid TextStoreAcpSink = new("22D44C94-A419-4542-A272-AE26093ECECF");
    internal static readonly Guid ThreadMgr = new("AA80E801-2021-11D2-93E0-0060B067B86E");

    /// <summary>ITfThreadMgrEx（R26）：msctf.idl:569 uuid(3e90ade3-7594-4cb0-bb58-69628f5f458c)。</summary>
    internal static readonly Guid ThreadMgrEx = new("3e90ade3-7594-4cb0-bb58-69628f5f458c");
    internal static readonly Guid DocumentMgr = new("AA80E7F4-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid Context = new("AA80E7FD-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid ContextView = new("2433BF8E-0F9B-435C-BA2C-180611978C30");
    internal static readonly Guid Source = new("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");
    internal static readonly Guid KeystrokeMgr = new("AA80E7F0-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid ContextOwnerCompositionSink = new("5F20AA40-B57A-4F34-96AB-3576F377CC79");
    internal static readonly Guid CompositionView = new("D7540241-F9A1-4364-BEFC-DBCD2C4395B7");
    internal static readonly Guid RangeAcp = new("057A6296-029B-4154-B79A-0D461D4EA94C");
    internal static readonly Guid EditSession = new("AA80E803-2021-11D2-93E0-0060B067B86E");
    internal static readonly Guid UiElementMgr = new("EA1EA135-19DF-11D7-A6D2-00065B84435C");
    internal static readonly Guid UiElement = new("EA1EA137-19DF-11D7-A6D2-00065B84435C");
    internal static readonly Guid UiElementSink = new("EA1EA136-19DF-11D7-A6D2-00065B84435C");
    internal static readonly Guid CandidateListUiElement = new("EA1EA138-19DF-11D7-A6D2-00065B84435C");
}

/// <summary>HRESULT 常量（TextStor.h:110-120、TextStor.idl:39-40、msctf.h:728-748）。</summary>
internal static class TsfHResult
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);

    internal const int TS_E_INVALIDPOS = unchecked((int)0x80040200);
    internal const int TS_E_NOLOCK = unchecked((int)0x80040201);
    internal const int TS_E_NOOBJECT = unchecked((int)0x80040202);
    internal const int TS_E_NOSERVICE = unchecked((int)0x80040203);
    internal const int TS_E_NOINTERFACE = unchecked((int)0x80040204);
    internal const int TS_E_NOSELECTION = unchecked((int)0x80040205);
    internal const int TS_E_NOLAYOUT = unchecked((int)0x80040206);
    internal const int TS_E_INVALIDPOINT = unchecked((int)0x80040207);
    internal const int TS_E_SYNCHRONOUS = unchecked((int)0x80040208);
    internal const int TS_E_READONLY = unchecked((int)0x80040209);
    internal const int TS_E_FORMAT = unchecked((int)0x8004020A);
    internal const int TS_S_ASYNC = 0x00040300;

    internal const int TF_E_LOCKED = unchecked((int)0x80040500);
    internal const int TF_E_DISCONNECTED = unchecked((int)0x80040504);
    internal const int TF_E_INVALIDVIEW = unchecked((int)0x80040505);

    // COM 套间初始化结果（R17）：宿主线程套间必须在创建 HWND/COM 之前确定，失败原因必须可读。
    internal const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    internal static string Describe(int hr) => hr switch
    {
        S_OK => "S_OK",
        S_FALSE => "S_FALSE",
        E_FAIL => "E_FAIL",
        E_INVALIDARG => "E_INVALIDARG",
        E_NOTIMPL => "E_NOTIMPL",
        E_NOINTERFACE => "E_NOINTERFACE",
        TS_E_INVALIDPOS => "TS_E_INVALIDPOS",
        TS_E_NOLOCK => "TS_E_NOLOCK",
        TS_E_NOOBJECT => "TS_E_NOOBJECT",
        TS_E_NOSERVICE => "TS_E_NOSERVICE",
        TS_E_NOINTERFACE => "TS_E_NOINTERFACE",
        TS_E_NOSELECTION => "TS_E_NOSELECTION",
        TS_E_NOLAYOUT => "TS_E_NOLAYOUT",
        TS_E_INVALIDPOINT => "TS_E_INVALIDPOINT",
        TS_E_SYNCHRONOUS => "TS_E_SYNCHRONOUS",
        TS_E_READONLY => "TS_E_READONLY",
        TS_E_FORMAT => "TS_E_FORMAT",
        TS_S_ASYNC => "TS_S_ASYNC",
        TF_E_LOCKED => "TF_E_LOCKED",
        TF_E_DISCONNECTED => "TF_E_DISCONNECTED",
        TF_E_INVALIDVIEW => "TF_E_INVALIDVIEW",
        CO_E_NOTINITIALIZED => "CO_E_NOTINITIALIZED",
        RPC_E_CHANGED_MODE => "RPC_E_CHANGED_MODE",
        _ => $"0x{hr:X8}",
    };
}

/// <summary>TextStor/msctf 常量（逐项来自 SDK IDL/头文件，行号见设计稿）。</summary>
internal static class TsfFlags
{
    // TextStor.idl:43-49
    internal const uint TS_AS_TEXT_CHANGE = 0x01;
    internal const uint TS_AS_SEL_CHANGE = 0x02;
    internal const uint TS_AS_LAYOUT_CHANGE = 0x04;
    internal const uint TS_AS_ATTR_CHANGE = 0x08;
    internal const uint TS_AS_STATUS_CHANGE = 0x10;
    internal const uint TS_AS_ALL_SINKS = 0x1F;

    // TextStor.idl:52-54
    internal const uint TS_LF_SYNC = 0x1;
    internal const uint TS_LF_READ = 0x2;
    internal const uint TS_LF_WRITE = 0x4;
    internal const uint TS_LF_READWRITE = 0x6;

    /// <summary>合法锁标志白名单：SYNC 可选，READ 或 READWRITE 必须包含，其它位一律拒绝。</summary>
    internal const uint TS_LF_ALLOWED = TS_LF_SYNC | TS_LF_READ | TS_LF_WRITE;

    // TextStor.idl:86-90
    internal const uint TS_TC_CORRECTION = 0x1;
    internal const uint TS_ST_CORRECTION = 0x1;
    internal const uint TS_IAS_NOQUERY = 0x1;
    internal const uint TS_IAS_QUERYONLY = 0x2;

    // TextStor.idl:130-131
    internal const uint GXFPF_ROUND_NEAREST = 0x1;
    internal const uint GXFPF_NEAREST = 0x2;

    // TextStor.idl:127, 155, 157
    internal const uint TS_DEFAULT_SELECTION = 0xFFFFFFFF;
    internal const uint TS_VCOOKIE_NUL = 0xFFFFFFFF;
    internal const int TS_LC_CREATE = 0;
    internal const int TS_LC_CHANGE = 1;
    internal const int TS_LC_DESTROY = 2;

    // TextStor.idl:105
    internal const int TS_AE_NONE = 0;
    internal const int TS_AE_START = 1;
    internal const int TS_AE_END = 2;

    // msctf.idl:929-933, 711
    internal const uint TF_ES_SYNC = 0x1;
    internal const uint TF_ES_READ = 0x2;
    internal const uint TF_ES_READWRITE = 0x6;
    internal const uint TF_ES_ASYNC = 0x8;
    internal const uint TF_POPF_ALL = 0x1;

    // R26：ITfThreadMgrEx::ActivateEx / GetActiveFlags 标志（msctf.idl:268-283）。
    // 这些值在 ActivateEx 与 GetActiveFlags 之间一一对应（TF_TMF_* == TF_TMAE_*）。
    internal const uint TF_TMAE_NOACTIVATETIP = 0x00000001;
    internal const uint TF_TMAE_SECUREMODE = 0x00000002;

    /// <summary>仅激活具备 UIElement 能力的 TIP；本任务唯一使用的激活变量。</summary>
    internal const uint TF_TMAE_UIELEMENTENABLEDONLY = 0x00000004;

    internal const uint TF_TMAE_COMLESS = 0x00000008;
    internal const uint TF_TMAE_WOW16 = 0x00000010;

    /// <summary>GetActiveFlags 专有位：TSF 当前确实处于激活状态（msctf.idl:284）。</summary>
    internal const uint TF_TMF_ACTIVATED = 0x80000000;

    // TextStor.idl:57-58, 68-69（原型：只读/加载/单选区/无 region）
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

    public static TsfRect Intersect(TsfRect a, TsfRect b) => FromLtrb(
        Math.Max(a.Left, b.Left),
        Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right),
        Math.Min(a.Bottom, b.Bottom));

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
[Guid("EA1EA136-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElementSink
{
    [PreserveSig]
    int BeginUIElement(uint dwUIElementId, ref int pbShow);

    [PreserveSig]
    int UpdateUIElement(uint dwUIElementId);

    [PreserveSig]
    int EndUIElement(uint dwUIElementId);
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
// msctf 提供、原型消费
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
/// ITfThreadMgrEx（R26）。逐项核对本机 SDK `msctf.idl:566-573`：
/// <c>uuid(3e90ade3-7594-4cb0-bb58-69628f5f458c)</c>，<c>interface ITfThreadMgrEx : ITfThreadMgr</c>，
/// 仅新增两个方法：
/// <code>
/// HRESULT ActivateEx([out] TfClientId *ptid, [in] DWORD dwFlags);
/// HRESULT GetActiveFlags([out] DWORD *lpdwFlags);
/// </code>
/// <para>
/// vtable 布局：IUnknown 占 0–2；ITfThreadMgr 的 11 个方法占 **3–13**
/// （Activate/Deactivate/CreateDocumentMgr/EnumDocumentMgrs/GetFocus/SetFocus/AssociateFocus/
/// IsThreadFocus/GetFunctionProvider/EnumFunctionProviders/GetGlobalCompartment）；
/// 因此 **ActivateEx = slot 14、GetActiveFlags = slot 15**。
/// </para>
/// <para>
/// **必须显式重声明全部 11 个基方法**：对于 `[ComImport]` 接口，C# 基接口列表
/// **不影响** CLR 生成的 COM vtable——CLR 只把本接口 `DeclaredOnly` 的方法从 slot 3 起依次排布。
/// 若只声明 2 个扩展方法，它们会落在 slot 3/4（`ActivateEx` 顶替 `Activate`、
/// `GetActiveFlags` 顶替 `Deactivate`），造成灾难性误调用。
/// 官方：https://learn.microsoft.com/en-us/dotnet/standard/native-interop/qualify-net-types-for-interoperation
/// （“the base interface list has no impact on the vtable”）。
/// 注意：`GeneratedComInterface` 源生成器才会自动拼接基接口，本项目仍用 `ComImport`，故不适用。
/// </para>
/// <para>
/// 因此这里用 `new` 重新声明 11 个基方法（签名与 <see cref="ITfThreadMgr"/> 逐字一致），
/// 再追加 2 个扩展方法。保留 `[PreserveSig]` 与精确 `MarshalAs`。
/// </para>
/// </summary>
[ComImport]
[Guid("3e90ade3-7594-4cb0-bb58-69628f5f458c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgrEx : ITfThreadMgr
{
    // ---- slot 3..13：ITfThreadMgr 的 11 个方法，必须显式重声明 ----

    [PreserveSig]
    new int Activate(out uint ptid);

    [PreserveSig]
    new int Deactivate();

    [PreserveSig]
    new int CreateDocumentMgr(out ITfDocumentMgr ppdim);

    [PreserveSig]
    new int EnumDocumentMgrs(out IntPtr ppEnum);

    [PreserveSig]
    new int GetFocus(out ITfDocumentMgr ppdimFocus);

    [PreserveSig]
    new int SetFocus(ITfDocumentMgr? pdimFocus);

    [PreserveSig]
    new int AssociateFocus(IntPtr hwnd, ITfDocumentMgr? pdimNew, out ITfDocumentMgr? ppdimPrev);

    [PreserveSig]
    new int IsThreadFocus(out int pfThreadFocus);

    [PreserveSig]
    new int GetFunctionProvider([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, out IntPtr ppFuncProv);

    [PreserveSig]
    new int EnumFunctionProviders(out IntPtr ppEnum);

    [PreserveSig]
    new int GetGlobalCompartment(out IntPtr ppCompMgr);

    // ---- slot 14..15：ITfThreadMgrEx 自身新增 ----

    [PreserveSig]
    int ActivateEx(out uint ptid, uint dwFlags);

    [PreserveSig]
    int GetActiveFlags(out uint pdwFlags);
}

[ComImport]
[Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfDocumentMgr
{
    [PreserveSig]
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
    // R21：参数类型必须是精确的 ITfEditSession（msctf.idl:927-935：
    // RequestEditSession([in] TfClientId tid, [in] ITfEditSession *pes, [in] DWORD dwFlags, [out] HRESULT *phrSession)）。
    // 早先声明为 [MarshalAs(UnmanagedType.IUnknown)] object 时 CLR 只传 IUnknown 身份，TSF 仍按契约把该指针
    // 当 ITfEditSession* 调用 slot 3(DoEditSession)，真实隐藏 MTA 只读会话因此原生崩溃（0xC0000005 /
    // Internal CLR error 0x80131506）。改为精确接口后 CLR 会按该接口的 COM 封送提供正确接口指针。
    // 独立托管测试只能证明 CLR 侧 vtable/封送形状；真实 MTA/STA 隐藏入口须由运行日志复核。
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
[Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfSource
{
    [PreserveSig]
    int AdviseSink([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);

    [PreserveSig]
    int UnadviseSink(uint dwCookie);
}

[ComImport]
[Guid("AA80E7F0-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfKeystrokeMgr
{
    [PreserveSig]
    int AdviseKeyEventSink(uint tid, [MarshalAs(UnmanagedType.IUnknown)] object pSink,
        int fForeground);

    [PreserveSig]
    int UnadviseKeyEventSink(uint tid);

    [PreserveSig]
    int GetForeground(out Guid pclsid);

    [PreserveSig]
    int TestKeyDown(IntPtr wParam, IntPtr lParam, out int pfEaten);

    [PreserveSig]
    int TestKeyUp(IntPtr wParam, IntPtr lParam, out int pfEaten);

    [PreserveSig]
    int KeyDown(IntPtr wParam, IntPtr lParam, out int pfEaten);

    [PreserveSig]
    int KeyUp(IntPtr wParam, IntPtr lParam, out int pfEaten);

    [PreserveSig]
    int GetPreservedKey(ITfContext pic, IntPtr pprekey, out Guid pguid);

    [PreserveSig]
    int IsPreservedKey([MarshalAs(UnmanagedType.LPStruct)] Guid rguid, IntPtr pprekey,
        out int pfRegistered);

    [PreserveSig]
    int PreserveKey(uint tid, [MarshalAs(UnmanagedType.LPStruct)] Guid rguid, IntPtr prekey,
        IntPtr pchDesc, uint cchDesc);

    [PreserveSig]
    int UnpreserveKey([MarshalAs(UnmanagedType.LPStruct)] Guid rguid, IntPtr pprekey);

    [PreserveSig]
    int SetPreservedKeyDescription([MarshalAs(UnmanagedType.LPStruct)] Guid rguid,
        IntPtr pchDesc, uint cchDesc);

    [PreserveSig]
    int GetPreservedKeyDescription([MarshalAs(UnmanagedType.LPStruct)] Guid rguid,
        out IntPtr pbstrDesc);

    [PreserveSig]
    int SimulatePreservedKey(ITfContext pic, [MarshalAs(UnmanagedType.LPStruct)] Guid rguid,
        out int pfEaten);
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

[ComImport]
[Guid("EA1EA135-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElementMgr
{
    [PreserveSig]
    int BeginUIElement(ITfUIElement pElement, ref int pbShow, out uint pdwUIElementId);

    [PreserveSig]
    int UpdateUIElement(uint dwUIElementId);

    [PreserveSig]
    int EndUIElement(uint dwUIElementId);

    [PreserveSig]
    int GetUIElement(uint dwUIElementId, out ITfUIElement ppElement);

    [PreserveSig]
    int EnumUIElements(out IntPtr ppEnum);
}

[ComImport]
[Guid("EA1EA137-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElement
{
    [PreserveSig]
    int GetDescription([MarshalAs(UnmanagedType.BStr)] out string pbstrDescription);

    [PreserveSig]
    int GetGUID(out Guid pguid);

    [PreserveSig]
    int Show(int bShow);

    [PreserveSig]
    int IsShown(out int pbShow);
}

[ComImport]
[Guid("EA1EA138-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCandidateListUIElement
{
    // COM 接口继承是 vtable 拼接：必须在同一接口里显式重声明 ITfUIElement 的槽 3-6，
    // 否则 CLR 会把 GetUpdatedFlags 放到 slot 3，与真实 TIP 的 GetDescription 错位。
    // 本机 msctf.idl:2719-2733：ITfUIElement(4) + 自身(8) = slot 3..14。

    [PreserveSig]
    int GetDescription([MarshalAs(UnmanagedType.BStr)] out string pbstrDescription);

    [PreserveSig]
    int GetGUID(out Guid pguid);

    [PreserveSig]
    int Show(int bShow);

    [PreserveSig]
    int IsShown(out int pbShow);

    [PreserveSig]
    int GetUpdatedFlags(out uint pdwFlags);

    [PreserveSig]
    int GetDocumentMgr(out ITfDocumentMgr ppdim);

    [PreserveSig]
    int GetCount(out uint puCount);

    [PreserveSig]
    int GetSelection(out uint puIndex);

    [PreserveSig]
    int GetString(uint uIndex, [MarshalAs(UnmanagedType.BStr)] out string pstr);

    [PreserveSig]
    int GetPageIndex(IntPtr pIndex, uint uSize, out uint puPageCnt);

    [PreserveSig]
    int SetPageIndex(IntPtr pIndex, uint uPageCnt);

    [PreserveSig]
    int GetCurrentPage(out uint puPage);
}

internal static class TsfNative
{
    /// <summary>TF_CreateThreadMgr（msctf.h:759）：在当前线程创建/激活 TSF 线程管理器。</summary>
    [DllImport("msctf.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int TF_CreateThreadMgr(
        [MarshalAs(UnmanagedType.Interface)] out ITfThreadMgr ppTim);

    /// <summary>
    /// ITfRangeACP 的槽位（msctf.idl:1535-1544）：IUnknown 占 0-2；ITfRange 的 22 个方法占 3-24
    /// （GetText/SetText/GetFormattedText/GetEmbedded/InsertEmbedded/ShiftStart/ShiftEnd/
    /// ShiftStartToRange/ShiftEndToRange/ShiftStartRegion/ShiftEndRegion/IsEmpty/Collapse/
    /// IsEqualStart/IsEqualEnd/CompareStart/CompareEnd/AdjustForInsert/GetGravity/SetGravity/
    /// Clone/GetContext）；GetExtent=25、SetExtent=26。
    /// 只按槽调用这两个方法，避免为 22 个不使用的方法编造签名。
    /// </summary>
    internal const int RangeAcpGetExtentSlot = 25;

    internal const int RangeAcpSetExtentSlot = 26;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetExtentDelegate(IntPtr self, out int pacpAnchor, out int pcch);

    /// <summary>HRESULT SetExtent([in] LONG acpAnchor, [in] LONG cch)；只移动本进程新建的临时 range。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetExtentDelegate(IntPtr self, int acpAnchor, int cch);

    internal static int GetRangeAcpExtent(IntPtr rangeAcp, out int anchor, out int cch)
    {
        var vtable = Marshal.ReadIntPtr(rangeAcp);
        var fn = Marshal.GetDelegateForFunctionPointer<GetExtentDelegate>(
            Marshal.ReadIntPtr(vtable, RangeAcpGetExtentSlot * IntPtr.Size));
        return fn(rangeAcp, out anchor, out cch);
    }

    /// <summary>
    /// 移动新建临时 range 的 extent。只作用于该 range 对象，不改文本、不改真实 selection；
    /// 调用失败时由调用方如实记录 HRESULT 并放弃本次查询（不重试、不伪更新）。
    /// </summary>
    internal static int SetRangeAcpExtent(IntPtr rangeAcp, int acpAnchor, int cch)
    {
        var vtable = Marshal.ReadIntPtr(rangeAcp);
        var fn = Marshal.GetDelegateForFunctionPointer<SetExtentDelegate>(
            Marshal.ReadIntPtr(vtable, RangeAcpSetExtentSlot * IntPtr.Size));
        return fn(rangeAcp, acpAnchor, cch);
    }
}
