using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

// TI-07 P3b：UI-less 候选（UIElement）COM ABI。
//
// 签名权威：本机 Windows SDK 10.0.26100.0 的 um/msctf.idl；逐项对照已验证隔离原型
// 标签 archive/tools-before-removal 中的 tools/CSharp.UI.TsfHostCheck/TsfInterop.cs，仅剥离诊断。所有方法保留原始 HRESULT，
// 绝不把托管异常穿过原生边界。
//
// 这些声明只是 ABI：本批不激活 TSF、不注册 sink、不读取元素。ActivateEx/GetActiveFlags
// 的配对激活与失败回退由 P3b 宿主接线阶段使用。

/// <summary>
/// <c>ITfThreadMgrEx</c>（msctf.idl:566-573，uuid 3e90ade3-7594-4cb0-bb58-69628f5f458c）。
/// </summary>
/// <remarks>
/// <para>
/// <c>interface ITfThreadMgrEx : ITfThreadMgr</c>，自身只新增
/// <c>ActivateEx</c> 与 <c>GetActiveFlags</c>。
/// </para>
/// <para>
/// <b>vtable 布局：</b>IUnknown 占 0–2；<c>ITfThreadMgr</c> 的 11 个方法占 3–13；
/// 因此 <c>ActivateEx</c> = slot 14、<c>GetActiveFlags</c> = slot 15。
/// </para>
/// <para>
/// <b>必须显式重声明全部 11 个基方法：</b>对 <c>[ComImport]</c> 接口，C# 基接口列表
/// <b>不影响</b> CLR 生成的 COM vtable——CLR 只把本接口声明的方法从 slot 3 起依次排布。
/// 只声明两个扩展方法会让它们落到 slot 3/4，误调用 <c>Activate</c>/<c>Deactivate</c>。
/// 官方：https://learn.microsoft.com/en-us/dotnet/standard/native-interop/qualify-net-types-for-interoperation
/// </para>
/// <para>
/// <b>激活纪律（P3b）：</b>QI 到本接口失败、<c>ActivateEx</c> 失败或 <c>GetActiveFlags</c>
/// 未包含 <see cref="TsfFlags.TF_TMF_ACTIVATED"/>（以及请求的 UIElement-only 位）时必须
/// 逆序释放并回退，不得在普通 <c>Activate</c> 之后叠加第二次激活。
/// </para>
/// </remarks>
[ComImport]
[Guid("3e90ade3-7594-4cb0-bb58-69628f5f458c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgrEx : ITfThreadMgr
{
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

/// <summary>
/// <c>ITfUIElementSink</c>（msctf.idl:2686-2691）：应用实现的 UIElement 生命周期回调。
/// </summary>
/// <remarks>
/// <b>回调纪律：</b>这些方法在原生回调栈上执行——不排版、不重建视图、不生成帧、不抛托管异常。
/// <c>BeginUIElement</c> 的 <c>pbShow</c> 是 in/out：应用必须明确写回；只有确属候选元素且
/// 自绘路径可用时才返回 FALSE（抑制系统候选），内容未就绪不是抑制条件。
/// <c>dwUIElementId</c> 的 <c>0</c> 是合法 id。
/// </remarks>
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

/// <summary>
/// <c>ITfUIElementMgr</c>（msctf.idl:2648-2655）：UIElement 管理器。
/// </summary>
/// <remarks>
/// 应用通过 <c>ITfSource</c> 在该管理器上 Advise/Unadvise <see cref="ITfUIElementSink"/>，
/// 成对释放；<see cref="GetUIElement"/> 只用于借用存活元素，返回的 RCW 由调用方释放。
/// </remarks>
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

/// <summary>
/// <c>ITfUIElement</c>（msctf.idl:2702-2708）。
/// </summary>
/// <remarks>
/// 自绘候选路径<b>不调用</b> <see cref="Show"/>：它只用于读取描述/GUID/显示状态，
/// 不得用 Show 把系统候选重新放出来，也不得据此改写组合。
/// </remarks>
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

/// <summary>
/// <c>ITfCandidateListUIElement</c>（msctf.idl:2719-2733）：候选列表的只读快照来源。
/// </summary>
/// <remarks>
/// <para>
/// <b>COM 接口继承是 vtable 拼接：</b>必须在同一接口里显式重声明 <see cref="ITfUIElement"/>
/// 的槽 3–6，否则 CLR 会把 <c>GetUpdatedFlags</c> 放到 slot 3，与真实 TIP 的
/// <c>GetDescription</c> 错位。本机 IDL：ITfUIElement(4) + 自身(8) = slot 3..14。
/// </para>
/// <para>
/// 页面读取只通过 <see cref="CandidatePageReader"/>（有界、逐调用结束复核）；
/// 不在回调内直接循环原生调用。
/// </para>
/// </remarks>
[ComImport]
[Guid("EA1EA138-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCandidateListUIElement
{
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
