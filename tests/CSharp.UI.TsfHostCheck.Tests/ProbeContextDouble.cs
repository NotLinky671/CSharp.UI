using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// 托管 ITfContext 替身：只实现探针真实使用的那条路径，让
/// `ContextViewProbe.Run → RequestEditSession → ContextViewEditSession.DoEditSession`
/// 的实际控制流在纯托管环境跑通（不初始化 COM、不创建窗口、不调用真实 TSF）。
/// <para>
/// 关键点：它**不是**真正的原生实现，CLR 会为这个 CCW 提供它自己声明的接口 vtable。
/// 因此它只用于验证“分支 → *phrSession → Classify”的控制流，
/// 不能证明原生 `RequestEditSession` 的 ABI 或真实 TSF 行为（那须由 Codex 真实运行复核）。
/// </para>
/// </summary>
internal sealed class ProbeContextDouble : ITfContext
{
    private readonly int _actualAcp;
    private readonly int _actualCch;
    private readonly TsfRect _viewRect;
    private readonly TsfRect _storeRect;
    private readonly TsfRect _viewScreen;
    private readonly TsfRect _storeScreen;
    private readonly IntPtr _viewHwnd;
    private readonly IntPtr _storeHwnd;
    private readonly int _viewClipped;
    private readonly int _storeClipped;

    internal ProbeContextDouble(ContextViewRange requested,
        int actualAcp, int actualCch, TsfRect? viewRect = null, TsfRect? storeRect = null,
        TsfRect? viewScreen = null, TsfRect? storeScreen = null, IntPtr? viewHwnd = null,
        IntPtr? storeHwnd = null, int viewClipped = 0, int storeClipped = 0)
    {
        Requested = requested;
        _actualAcp = actualAcp;
        _actualCch = actualCch;
        _viewRect = viewRect ?? TsfRect.FromLtrb(10, 20, 30, 40);
        _storeRect = storeRect ?? _viewRect;
        _viewScreen = viewScreen ?? TsfRect.FromLtrb(0, 0, 400, 100);
        _storeScreen = storeScreen ?? _viewScreen;
        _viewHwnd = viewHwnd ?? new IntPtr(0x100);
        _storeHwnd = storeHwnd ?? _viewHwnd;
        _viewClipped = viewClipped;
        _storeClipped = storeClipped;
    }

    internal ContextViewRange Requested { get; }

    /// <summary>DoEditSession 的真实返回值（即 TSF 会收到的 *phrSession）。</summary>
    internal int DoEditSessionHResult { get; private set; }

    /// <summary>是否进入了 view/store 几何查询阶段。</summary>
    internal bool GeometryQueried { get; private set; }

    internal int StoreTextExtCalls { get; private set; }

    public int RequestEditSession(uint tid, ITfEditSession pes, uint dwFlags, out int phrSession)
    {
        // 模拟 TSF：直接调用会话（真实 TSF 在授予锁后调用 slot 3），并把返回值作为 *phrSession。
        phrSession = pes.DoEditSession(0);
        DoEditSessionHResult = phrSession;
        return TsfHResult.S_OK;
    }

    public int GetStart(uint ec, out IntPtr ppStart)
    {
        // 返回非空伪指针：探针随后按 IID 查询 ITfRangeACP。
        ppStart = new IntPtr(0x4000);
        return TsfHResult.S_OK;
    }

    public int GetActiveView(out ITfContextView ppView)
    {
        GeometryQueried = true;
        ppView = new ProbeContextViewDouble(_viewRect, _viewClipped, _viewScreen, _viewHwnd);
        return TsfHResult.S_OK;
    }

    // ---- 其余成员：探针路径不会调用，如实返回 E_NOTIMPL（不伪造成功）。 ----

    public int InWriteSession(uint tid, out int pfWriteSession)
    {
        pfWriteSession = 0;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetSelection(uint ec, uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
    {
        pcFetched = 0;
        return TsfHResult.E_NOTIMPL;
    }

    public int SetSelection(uint ec, uint ulCount, IntPtr pSelection) => TsfHResult.E_NOTIMPL;

    public int GetEnd(uint ec, out IntPtr ppEnd)
    {
        ppEnd = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int EnumViews(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetStatus(out TsStatus pdcs)
    {
        pdcs = default;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetProperty([MarshalAs(UnmanagedType.LPStruct)] Guid guidProp, out IntPtr ppProp)
    {
        ppProp = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetAppProperty([MarshalAs(UnmanagedType.LPStruct)] Guid guidProp, out IntPtr ppProp)
    {
        ppProp = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int TrackProperties(IntPtr prgProp, uint cProp, IntPtr prgAppProp, uint cAppProp,
        out IntPtr ppProperty)
    {
        ppProperty = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int EnumProperties(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetDocumentMgr(out ITfDocumentMgr ppDm)
    {
        ppDm = null!;
        return TsfHResult.E_NOTIMPL;
    }

    public int CreateRangeBackup(uint ec, IntPtr pRange, out IntPtr ppBackup)
    {
        ppBackup = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }
}

/// <summary>view 侧几何替身：只提供探针实际查询的三个方法。</summary>
internal sealed class ProbeContextViewDouble : ITfContextView
{
    private readonly TsfRect _rect;
    private readonly int _clipped;
    private readonly TsfRect _screen;
    private readonly IntPtr _hwnd;

    internal ProbeContextViewDouble(TsfRect rect, int clipped, TsfRect screen, IntPtr hwnd)
    {
        _rect = rect;
        _clipped = clipped;
        _screen = screen;
        _hwnd = hwnd;
    }

    public int GetRangeFromPoint(uint ec, IntPtr ppt, uint dwFlags, out IntPtr ppRange)
    {
        ppRange = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    public int GetTextExt(uint ec, IntPtr pRange, out TsfRect prc, out int pfClipped)
    {
        prc = _rect;
        pfClipped = _clipped;
        return TsfHResult.S_OK;
    }

    public int GetScreenExt(out TsfRect prc)
    {
        prc = _screen;
        return TsfHResult.S_OK;
    }

    public int GetWnd(out IntPtr phwnd)
    {
        phwnd = _hwnd;
        return TsfHResult.S_OK;
    }
}
