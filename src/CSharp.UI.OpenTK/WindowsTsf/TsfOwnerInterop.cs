using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 组合 owner 与编辑完成观察所需的补充 COM 契约（TI-07 P2b 第二批）。
/// </summary>
/// <remarks>
/// <para>
/// <b>签名权威：</b>本机 Windows SDK <c>10.0.26100.0</c> 的 <c>um/msctf.idl</c>，逐项核对：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ITfTextEditSink</c>（uuid <c>8127d409-ccd3-4683-967a-b43d5b482bf7</c>）：
/// <c>HRESULT OnEndEdit([in] ITfContext *pic, [in] TfEditCookie ecReadOnly, [in] ITfEditRecord *pEditRecord)</c>。
/// </description></item>
/// <item><description>
/// <c>ITfEditRecord</c>（uuid <c>42d4d099-7c1a-4a89-b836-6c6f22160df0</c>）：
/// <c>GetSelectionStatus(BOOL*)</c>、<c>GetTextAndPropertyUpdates(DWORD, const GUID**, ULONG, IEnumTfRanges**)</c>，
/// 常量 <c>TF_GTP_INCL_TEXT = 0x1</c>。
/// </description></item>
/// <item><description>
/// <c>ITfRange</c>（uuid <c>aa80e7ff-2021-11d2-93e0-0060b067b86e</c>）：<c>GetText(TfEditCookie, DWORD, WCHAR*, ULONG, ULONG*)</c>。
/// </description></item>
/// <item><description>
/// <c>ITfRangeACP : ITfRange</c>（uuid <c>057a6296-029b-4154-b79a-0d461d4ea94c</c>）：
/// <c>GetExtent(LONG* pacpAnchor, LONG* pcch)</c>、<c>SetExtent(LONG, LONG)</c>。
/// </description></item>
/// <item><description>
/// <c>ITfComposition</c>（uuid <c>20168D64-5A8F-4A5A-B7BD-CFA29F4D0FD9</c>）：<c>GetRange(ITfRange**)</c>、
/// <c>EndComposition(TfEditCookie)</c>。
/// </description></item>
/// <item><description>
/// <c>ITfSource</c>（uuid <c>4ea48a35-60ae-446f-8fd6-e6a8d82459f7</c>）：<c>AdviseSink(REFIID, IUnknown*, DWORD*)</c>、
/// <c>UnadviseSink(DWORD)</c>。
/// </description></item>
/// </list>
/// <para>
/// <b>vtable 注意：</b><c>ITfRangeACP</c> 继承 <c>ITfRange</c>。与 <c>ComImport</c> 的既有约定一致，
/// 本模块**只**声明实际调用的方法，并按 <c>ITfRange</c> 的真实槽位顺序把 <c>GetText</c> 放在最前，
/// 随后才是 <c>ITfRangeACP</c> 的 <c>GetExtent</c>/<c>SetExtent</c>——但**不使用**该声明做跨接口调用；
/// 需要 extent 时统一走 <see cref="TsfRangeAcp.GetExtentViaVtable"/> 的显式槽位调用（已验证槽位 25/26），
/// 避免为 22 个不使用的方法编造签名。
/// </para>
/// </remarks>
[ComImport]
[Guid("8127d409-ccd3-4683-967a-b43d5b482bf7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfTextEditSink
{
    /// <summary>一次 TSF 编辑事务结束（已验证的“编辑完成”边界）。</summary>
    [PreserveSig]
    int OnEndEdit(ITfContext pic, uint ecReadOnly, ITfEditRecord pEditRecord);
}

[ComImport]
[Guid("42d4d099-7c1a-4a89-b836-6c6f22160df0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfEditRecord
{
    [PreserveSig]
    int GetSelectionStatus(out int pfChanged);

    [PreserveSig]
    int GetTextAndPropertyUpdates(uint dwFlags, IntPtr prgProperties, uint cProperties,
        out IntPtr ppEnum);
}

[ComImport]
[Guid("20168D64-5A8F-4A5A-B7BD-CFA29F4D0FD9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfComposition
{
    /// <summary>
    /// 取组合范围。
    /// </summary>
    /// <remarks>
    /// <b>刻意返回原始指针</b>：<c>ITfRange</c> 有 22 个方法，用托管接口接收会产生 RCW，
    /// 其 <c>GetIUnknownForObject</c> 身份对 <c>ITfRangeACP</c> 的 QI 不可靠（实测 E_NOINTERFACE）。
    /// 原始指针可直接 QI 到 <c>ITfRangeACP</c> 并走显式 vtable 槽位读取 extent。
    /// </remarks>
    [PreserveSig]
    int GetRange(out IntPtr ppRange);

    [PreserveSig]
    int ShiftStart(uint ecWrite, ITfRange pNewStart);

    [PreserveSig]
    int ShiftEnd(uint ecWrite, ITfRange pNewEnd);

    [PreserveSig]
    int EndComposition(uint ecWrite);
}

/// <summary>
/// <c>ITfRange</c> 的最小可用子集：只声明 <c>GetText</c>（槽位 3）。
/// </summary>
/// <remarks>
/// 刻意只声明 1 个方法：本模块不需要 <c>ITfRange</c> 的其余 21 个方法，
/// 因此 extent 一律通过 <see cref="TsfRangeAcp"/> 的显式槽位调用获取，不依赖本接口的完整 vtable。
/// </remarks>
[ComImport]
[Guid("aa80e7ff-2021-11d2-93e0-0060b067b86e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfRange
{
    /// <summary>读取范围内文本（UTF-16 码元）。</summary>
    [PreserveSig]
    int GetText(uint ec, uint dwFlags, [Out] char[] pchText, uint cchMax, out uint pcch);

    /// <summary>写入范围内文本（UTF-16 码元；需有效写 cookie）。</summary>
    /// <remarks>
    /// <c>size_is(cch)</c> 必须用 <see cref="SizeParamIndexAttribute"/> 表达，否则 marshaler
    /// 只传第一个元素（实测只写入 1 个码元）。
    /// </remarks>
    [PreserveSig]
    int SetText(uint ec, uint dwFlags,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] char[] pchText, int cch);

}

[ComImport]
[Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfSource
{
    [PreserveSig]
    int AdviseSink([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);

    [PreserveSig]
    int UnadviseSink(uint dwCookie);
}

/// <summary>
/// <c>ITfCompositionSink</c>（msctf.idl：<c>5F20AA40-B57A-4F34-96AB-3576F377CC79</c> 的 TIP 侧对应接口，
/// uuid <c>DF8CC377-D535-4ACC-A2E4-59BF4FED4F70</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是 <c>StartComposition</c> 第三参的真实类型：</b>官方签名是
/// <c>HRESULT StartComposition(TfEditCookie, ITfRange*, ITfCompositionSink*, ITfComposition**)</c>。
/// </para>
/// <para>
/// <b>绝不能用 <c>ITfContextOwnerCompositionSink</c> 冒充：</b>两者方法签名不同
/// （本接口是 <c>OnCompositionTerminated(TfEditCookie, ITfComposition*)</c>）。
/// 传错对象会让 TSF 在终止时按不匹配的 vtable 调用，实测触发 CLR fatal（<c>0x80131506</c>）。
/// </para>
/// </remarks>
[ComImport]
[Guid("DF8CC377-D535-4ACC-A2E4-59BF4FED4F70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCompositionSink
{
    /// <summary>组合被终止（owner 侧 <c>TerminateComposition</c> 会同步调用它）。</summary>
    [PreserveSig]
    int OnCompositionTerminated(uint ecWrite, ITfComposition pComposition);
}

/// <summary>
/// 空的 <see cref="ITfCompositionSink"/>：只为满足 <c>StartComposition</c> 的第三参。
/// </summary>
/// <remarks>
/// <b>为什么不传 null：</b>实测本机 TSF 对 <c>pSink == NULL</c> 返回 <c>E_INVALIDARG</c>（虽然文档标注可选），
/// 因此必须给一个类型正确的对象。终止通知本身不需要本对象做任何事——owner 侧
/// <c>ITfContextOwnerCompositionSink</c> 由 <c>AcpTextStore.CompositionObserver</c> 独立安装。
/// </remarks>
internal sealed class NullCompositionSink : ITfCompositionSink
{
    /// <summary>共享实例（无状态）。</summary>
    internal static NullCompositionSink Instance { get; } = new();

    public int OnCompositionTerminated(uint ecWrite, ITfComposition pComposition) => TsfHResult.S_OK;
}

/// <summary>
/// <c>ITfRangeACP</c> 的 extent 读取（显式 vtable 槽位，不依赖托管接口继承布局）。
/// </summary>
/// <remarks>
/// 槽位依据本机 SDK <c>um/msctf.idl</c>：<c>IUnknown</c> 占 0-2；<c>ITfRange</c> 的 22 个方法占 3-24；
/// <c>GetExtent</c> = 25、<c>SetExtent</c> = 26。与已验收原型一致。
/// </remarks>
internal static class TsfRangeAcp
{
    /// <summary><c>ITfRangeACP::GetExtent</c> 的 vtable 槽位。</summary>
    internal const int GetExtentSlot = 25;

    /// <summary>
    /// <c>ITfRange::ShiftEnd</c> 的 vtable 槽位。
    /// </summary>
    /// <remarks>
    /// 依据本机 SDK <c>um/msctf.idl</c> 逐个枚举 <c>ITfRange</c> 方法：IUnknown 占 0-2，
    /// <c>GetText</c>=3、<c>SetText</c>=4、<c>GetFormattedText</c>=5、<c>GetEmbedded</c>=6、
    /// <c>InsertEmbedded</c>=7、<c>ShiftStart</c>=8、<b><c>ShiftEnd</c>=9</b>。
    /// </remarks>
    internal const int ShiftEndSlot = 9;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetExtentDelegate(IntPtr self, out int pacpAnchor, out int pcch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShiftEndDelegate(IntPtr self, uint ec, int cchReq, out int pcch, IntPtr pHaltCond);

    /// <summary>
    /// 调用 <c>ITfRange::ShiftEnd</c>（显式槽位，避免为 22 个方法编造签名）。
    /// </summary>
    /// <param name="range">原生 <c>ITfRange*</c>（借用）。</param>
    /// <param name="ec">有效写 cookie。</param>
    /// <param name="cchReq">移动码元数。</param>
    /// <param name="pcch">实际移动数。</param>
    /// <returns>HRESULT。</returns>
    internal static int ShiftEndViaVtable(IntPtr range, uint ec, int cchReq, out int pcch)
    {
        pcch = 0;
        if (range == IntPtr.Zero)
        {
            return TsfHResult.E_INVALIDARG;
        }

        try
        {
            var vtable = Marshal.ReadIntPtr(range);
            if (vtable == IntPtr.Zero)
            {
                return TsfHResult.E_FAIL;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<ShiftEndDelegate>(
                Marshal.ReadIntPtr(vtable, ShiftEndSlot * IntPtr.Size));
            return fn(range, ec, cchReq, out pcch, IntPtr.Zero);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    /// <summary>
    /// 读取 ACP 范围的 anchor 与 cch。
    /// </summary>
    /// <param name="rangeAcp">已 QI 到 <c>ITfRangeACP</c> 的指针（<b>借用</b>，调用方负责生命周期）。</param>
    /// <param name="anchor">起点（ACP，UTF-16 码元）。</param>
    /// <param name="cch">长度（UTF-16 码元）。</param>
    /// <returns>HRESULT。</returns>
    internal static int GetExtentViaVtable(IntPtr rangeAcp, out int anchor, out int cch)
    {
        anchor = 0;
        cch = 0;
        if (rangeAcp == IntPtr.Zero)
        {
            return TsfHResult.E_INVALIDARG;
        }

        try
        {
            var vtable = Marshal.ReadIntPtr(rangeAcp);
            if (vtable == IntPtr.Zero)
            {
                return TsfHResult.E_FAIL;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<GetExtentDelegate>(
                Marshal.ReadIntPtr(vtable, GetExtentSlot * IntPtr.Size));
            return fn(rangeAcp, out anchor, out cch);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }
}

/// <summary>
/// <c>ITfContextOwnerCompositionServices::TerminateComposition</c> 的显式 vtable 调用
/// （owner 侧终止原生组合的唯一官方入口）。
/// </summary>
/// <remarks>
/// <para>
/// <b>官方语义（Microsoft Learn，msctf.h）：</b><c>HRESULT TerminateComposition([in] ITfCompositionView *pComposition)</c>，
/// <c>pComposition</c> 为 <c>NULL</c> 时终止该 context 上的<b>全部</b>组合。备注明确：
/// “If the context owner implements the text store, the context owner must be able to grant a synchronous
/// write lock before calling this method.”，并且对每个被终止的组合<b>同步</b>调用
/// <c>ITfContextOwnerCompositionSink::OnEndComposition</c> 与各 <c>ITfCompositionSink::OnCompositionTerminated</c>。
/// </para>
/// <para>
/// <b>槽位依据：</b><c>IUnknown</c> 占 0-2；<c>ITfContextComposition</c> 的 4 个方法占 3-6；
/// <b><c>TerminateComposition</c> = 7</b>。这里<b>不</b>通过托管接口继承去调它，
/// 以免把“托管继承布局”当成原生 ABI。
/// </para>
/// <para>
/// <b>只报告真实 HRESULT：</b>失败值原样返回（<c>TF_E_NOLOCK</c> 表示此刻拿不到同步写锁、
/// <c>TF_E_DISCONNECTED</c> 表示 context 不在文档栈上、<c>E_UNEXPECTED</c> 表示正处于另一组合操作内），
/// 不吞掉、不转成托管异常，也绝不把“没有抛异常”当作成功。
/// </para>
/// </remarks>
internal static class TsfOwnerCompositionServices
{
    /// <summary><c>ITfContextOwnerCompositionServices::TerminateComposition</c> 的 vtable 槽位。</summary>
    /// <remarks>IUnknown 0-2 + <c>ITfContextComposition</c> 4 个方法 3-6 → 7。</remarks>
    internal const int TerminateCompositionSlot = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TerminateCompositionDelegate(IntPtr self, IntPtr pComposition);

    /// <summary>
    /// 在给定 context 上终止组合。
    /// </summary>
    /// <param name="context">真实 <c>ITfContext</c>（借用；本方法只做一次 QI）。</param>
    /// <param name="compositionView">
    /// 要终止的 <c>ITfCompositionView</c> 指针（借用）；<see cref="IntPtr.Zero"/> 表示终止该 context 上的全部组合。
    /// </param>
    /// <returns>真实 HRESULT；QI 失败或非 Windows 时返回明确的失败值（绝不返回成功）。</returns>
    internal static int TerminateComposition(object? context, IntPtr compositionView)
    {
        if (context is null)
        {
            return TsfHResult.E_INVALIDARG;
        }

        if (!OperatingSystem.IsWindows())
        {
            return TsfHResult.E_NOINTERFACE;
        }

        var unknown = IntPtr.Zero;
        var servicesPtr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(context);
            var iid = TsfOwnerIids.ContextOwnerCompositionServicesIid;
            var qiHr = Marshal.QueryInterface(unknown, in iid, out servicesPtr);
            if (qiHr < 0)
            {
                return qiHr;
            }

            if (servicesPtr == IntPtr.Zero)
            {
                return TsfHResult.E_NOINTERFACE;
            }

            return TerminateCompositionViaVtable(servicesPtr, compositionView);
        }
        catch (Exception)
        {
            // 托管异常绝不穿过宿主调用边界。
            return TsfHResult.E_FAIL;
        }
        finally
        {
            if (servicesPtr != IntPtr.Zero)
            {
                Marshal.Release(servicesPtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>对已取得的 <c>ITfContextOwnerCompositionServices</c> 指针做显式槽位调用。</summary>
    /// <param name="services">已 QI 到 <c>ITfContextOwnerCompositionServices</c> 的指针（借用）。</param>
    /// <param name="compositionView">要终止的组合视图指针（借用）；0 = 全部。</param>
    /// <returns>真实 HRESULT。</returns>
    internal static int TerminateCompositionViaVtable(IntPtr services, IntPtr compositionView)
    {
        if (services == IntPtr.Zero)
        {
            return TsfHResult.E_INVALIDARG;
        }

        try
        {
            var vtable = Marshal.ReadIntPtr(services);
            if (vtable == IntPtr.Zero)
            {
                return TsfHResult.E_FAIL;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<TerminateCompositionDelegate>(
                Marshal.ReadIntPtr(vtable, TerminateCompositionSlot * IntPtr.Size));
            return fn(services, compositionView);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }
}

/// <summary>
/// <c>ITfContextComposition</c>（msctf.idl：D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B）的托管子集。
/// </summary>
/// <remarks>
/// <para>
/// <b>vtable 事实（本机 SDK <c>10.0.26100.0</c> 的 <c>um/msctf.idl</c>）：</b>该接口继承 <c>IUnknown</c>，
/// 只有 <c>StartComposition</c>、<c>EnumCompositions</c>、<c>FindComposition</c>、<c>TakeOwnership</c> 四个方法。
/// 这里按声明顺序完整列出，<b>不</b>额外声明 SDK 中不存在的方法（那会算错后续接口的槽位）。
/// </para>
/// <para>
/// <b>不借它的继承布局去调 <c>ITfContextOwnerCompositionServices</c>：</b>子接口方法统一走
/// <see cref="TsfOwnerCompositionServices"/> 的显式槽位调用，避免托管继承布局与原生 ABI 的偏差。
/// </para>
/// </remarks>
[ComImport]
[Guid("D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfContextComposition
{
    /// <summary>
    /// 建立组合。
    /// </summary>
    /// <param name="ecWrite">有效写 cookie。</param>
    /// <param name="pCompositionRange">初始组合范围（<c>ITfRange*</c>）。</param>
    /// <param name="pSink">
    /// <b><c>ITfCompositionSink*</c></b>（官方 msctf.idl）。可选，<c>null</c> 表示不要该通知。
    /// </param>
    /// <param name="ppComposition">新组合；context owner 拒绝时为 null。</param>
    /// <remarks>
    /// <para>
    /// <b>参数类型是硬约束：</b>第三参是 <c>ITfCompositionSink*</c>，<b>不是</b>
    /// <c>ITfContextOwnerCompositionSink</c>。传错对象会让 TSF 在终止路径上按不匹配的 vtable 调用
    /// <c>OnCompositionTerminated</c>，实测触发 CLR fatal（<c>0x80131506</c>）。
    /// </para>
    /// <para>
    /// <b>为什么必须是 <c>object?</c> 而不是 <see cref="IntPtr"/>：</b>实测传 <see cref="IntPtr.Zero"/>
    /// 会被 TSF 判为 <c>E_INVALIDARG</c>（封送后不是“接口为 NULL”的语义）；
    /// <c>object?</c> + <c>UnmanagedType.IUnknown</c> 传 <c>null</c> 才是真正的 NULL 接口指针。
    /// owner 侧通知由 <c>AcpTextStore.CompositionObserver</c> 独立安装，不依赖本参数。
    /// </para>
    /// </remarks>
    [PreserveSig]
    int StartComposition(uint ecWrite, ITfRange pCompositionRange,
        [MarshalAs(UnmanagedType.Interface)] ITfCompositionSink? pSink, out ITfComposition ppComposition);

    [PreserveSig]
    int EnumCompositions(out IntPtr ppEnum);

    [PreserveSig]
    int FindComposition(uint ecRead, ITfRange pTestRange, out IntPtr ppEnum);

    [PreserveSig]
    int TakeOwnership(uint ecWrite, ITfCompositionView pComposition,
        [MarshalAs(UnmanagedType.Interface)] ITfCompositionSink? pSink, out ITfComposition ppComposition);

    // 注意：本机 SDK msctf.idl 中 ITfContextComposition **只有**上述 4 个方法。
    // 早期版本在这里声明了 GetCompositionForView——SDK 中不存在该方法，它会把 vtable 槽位算多一格。
    // 因此 ITfContextOwnerCompositionServices::TerminateComposition 一律走显式槽位（见 TsfOwnerCompositionServices）。
}

/// <summary>
/// <c>ITextStoreACPServices</c>（msctf.idl：aa80e901-2021-11d2-93e0-0060b067b86e）的最小可用子集。
/// </summary>
/// <remarks>
/// 只声明 <c>CreateRange</c>（槽位 6：IUnknown 3 + Serialize/Unserialize/ForceLoadProperty 3）。
/// 该接口由 <c>ITextStoreACP::AdviseSink</c> 通过 QI 提供给 store。
/// </remarks>
[ComImport]
[Guid("aa80e901-2021-11d2-93e0-0060b067b86e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITextStoreAcpServices
{
    [PreserveSig]
    int Serialize(IntPtr pProp, ITfRange pRange, IntPtr pHdr, IntPtr pStream);

    [PreserveSig]
    int Unserialize(IntPtr pProp, IntPtr pHdr, IntPtr pStream, IntPtr pLoader);

    [PreserveSig]
    int ForceLoadProperty(IntPtr pProp);

    [PreserveSig]
    int CreateRange(int acpStart, int acpEnd, out ITfRangeACP ppRange);
}

/// <summary>
/// <c>ITfRangeACP</c> 的完整声明（仅用于需要托管接口的场景，例如 <c>CreateRange</c> 的返回值）。
/// </summary>
/// <remarks>
/// <b>vtable 注意：</b>本接口继承 <c>ITfRange</c>。ComImport 的继承布局无法精确表达 22 个未声明方法，
/// 因此 extent 读取仍统一走 <see cref="TsfRangeAcp.GetExtentViaVtable"/> 的显式槽位调用。
/// 这里只把它当作“可传递的 range 句柄”使用。
/// </remarks>
[ComImport]
[Guid("057a6296-029b-4154-b79a-0d461d4ea94c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfRangeACP
{
}

/// <summary>组合 owner / 编辑完成观察用到的 IID。</summary>
internal static class TsfOwnerIids
{
    /// <summary><c>ITfTextEditSink</c>（msctf.idl：8127d409-ccd3-4683-967a-b43d5b482bf7）。</summary>
    internal static readonly Guid TextEditSink = new("8127d409-ccd3-4683-967a-b43d5b482bf7");

    /// <summary><c>ITfRangeACP</c>（msctf.idl：057a6296-029b-4154-b79a-0d461d4ea94c）。</summary>
    internal static readonly Guid RangeAcp = new("057a6296-029b-4154-b79a-0d461d4ea94c");

    /// <summary><c>ITfRange</c>（msctf.idl：aa80e7ff-2021-11d2-93e0-0060b067b86e）。</summary>
    internal static readonly Guid Range = new("aa80e7ff-2021-11d2-93e0-0060b067b86e");

    /// <summary><c>ITfCompositionView</c>（msctf.idl：D7540241-F9A1-4364-BEFC-DBCD2C4395B7）。</summary>
    internal static readonly Guid CompositionView = new("D7540241-F9A1-4364-BEFC-DBCD2C4395B7");

    /// <summary><c>ITfSource</c>（msctf.idl：4ea48a35-60ae-446f-8fd6-e6a8d82459f7）。</summary>
    internal static readonly Guid SourceIid = new("4ea48a35-60ae-446f-8fd6-e6a8d82459f7");

    /// <summary><c>ITfContextComposition</c>（msctf.idl：D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B）。</summary>
    internal static readonly Guid ContextCompositionIid = new("D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B");

    /// <summary>
    /// <c>ITfContextOwnerCompositionServices</c>（msctf.idl：<c>86462810-593B-4916-9764-19C08E9CE110</c>）。
    /// </summary>
    /// <remarks>
    /// 官方：由 <c>ITfContext::QueryInterface</c> 取得；它继承 <c>ITfContextComposition</c> 并只新增
    /// <c>TerminateComposition</c>（槽位 7）。
    /// </remarks>
    internal static readonly Guid ContextOwnerCompositionServicesIid
        = new("86462810-593B-4916-9764-19C08E9CE110");

    /// <summary><c>ITextStoreACPServices</c>（msctf.idl：aa80e901-2021-11d2-93e0-0060b067b86e）。</summary>
    internal static readonly Guid TextStoreAcpServicesIid = new("aa80e901-2021-11d2-93e0-0060b067b86e");

    /// <summary><c>ITfContextOwnerCompositionSink</c>（msctf.idl：5F20AA40-B57A-4F34-96AB-3576F377CC79）。</summary>
    internal static readonly Guid ContextOwnerCompositionSink = new("5F20AA40-B57A-4F34-96AB-3576F377CC79");
}
