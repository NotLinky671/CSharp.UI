using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>一次 context/view 只读查询使用的 ACP 范围（半开区间，单位 UTF-16 码元）。</summary>
internal readonly record struct ContextViewRange(int Start, int End, string Source);

/// <summary>
/// 探针状态：拒绝/不一致/会话未执行都如实保留，任何状态都不能当作候选视觉通过。
/// <see cref="Ok"/> 只在 R20 要求的全部条件同时成立时产生（见 <see cref="ContextViewProbe.Classify"/>）。
/// </summary>
internal static class ContextViewProbeStatus
{
    internal const string Ok = "ok";
    internal const string Mismatch = "mismatch";

    /// <summary>会话在 TSF 侧确实执行了，但临时范围与请求范围不一致（range mismatch）：不比较几何。</summary>
    internal const string RangeMismatch = "range-mismatch";

    /// <summary>会话已执行但某一几何项被拒（HRESULT&lt;0）或失败：不能据此宣称几何一致。</summary>
    internal const string Refused = "refused";

    /// <summary>RequestEditSession 成功返回，但 DoEditSession 未被进入（会话未执行）。</summary>
    internal const string SessionNotExecuted = "session-not-executed";

    /// <summary>会话执行了但返回失败 HRESULT（*phrSession&lt;0）。</summary>
    internal const string SessionFailed = "session-failed";
    internal const string RequestFailed = "request-failed";
    internal const string ContextUnavailable = "context-unavailable";
    internal const string Exception = "exception";
}

/// <summary>探针对外结果：保留全部原始 HRESULT 与布尔项，便于宿主/日志逐项复核。</summary>
internal readonly record struct ContextViewProbeResult(
    string Status, int RequestHResult, int SessionHResult, bool RectMatch)
{
    internal bool SessionRan { get; init; }

    internal bool RangeMatch { get; init; }

    internal bool ScreenMatch { get; init; }

    internal bool WndMatch { get; init; }
}

/// <summary>可变结果载体：编辑会话在同步回调内填充，调用方在 RequestEditSession 返回后统一落盘一次。</summary>
internal sealed class ContextViewProbeRecord
{
    internal string Label = "(none)";
    internal ContextViewRange Range;
    internal string Status = ContextViewProbeStatus.SessionNotExecuted;
    internal bool SessionRan;
    internal int RequestHResult = TsfHResult.E_FAIL;
    internal int SessionHResult = TsfHResult.E_FAIL;
    internal uint Ec;
    internal int? GetStartHResult;
    internal int? QueryRangeAcpHResult;
    internal int? SetExtentHResult;
    internal int? GetExtentHResult;
    internal int? ActualAcp;
    internal int? ActualCch;

    /// <summary>
    /// 范围是否精确等于请求范围。null = 尚未取得 extent（`GetExtent` 失败或未执行到该步），
    /// 此时不能断言“范围一致”也**不能**断言“范围不一致”：会话确实失败，由会话 HRESULT 表达。
    /// </summary>
    internal bool? RangeMatch;

    /// <summary>因范围不一致而停止几何比较（不拿其矩形证明同一范围）。</summary>
    internal bool GeometrySkipped;

    internal int? GetActiveViewHResult;
    internal int? FirstQueryHResult;
    internal int? ViewTextExtHResult;
    internal TsfRect? ViewRect;
    internal int? ViewClipped;
    internal int? StoreTextExtHResult;
    internal TsfRect? StoreRect;
    internal int? StoreClipped;
    internal int? ViewScreenHResult;
    internal TsfRect? ViewScreen;
    internal int? StoreScreenHResult;
    internal TsfRect? StoreScreen;
    internal int? ViewWndHResult;
    internal IntPtr ViewHwnd;
    internal int? StoreWndHResult;
    internal IntPtr StoreHwnd;
    internal bool RectMatch;
    internal bool ScreenMatch;
    internal bool WndMatch;
    internal uint StoreViewCookie;
    internal uint Win32ThreadId;
    internal int ManagedThreadId;
    internal long LayoutGeneration;
    internal long DocumentVersion;
    internal bool Composing;
    internal string? ExceptionName;
    internal string? ExceptionMessage;

    /// <summary>DoEditSession 返回给 TSF 的 HRESULT：失败必须如实上报，不能被调用方吞掉。</summary>
    internal int DoEditSessionHResult = TsfHResult.S_OK;

    /// <summary>各指针释放结果（成功也必须可见，失败绝不静默）。</summary>
    internal string? ReleaseViewOutcome;
    internal string? ReleaseRangeAcpOutcome;
    internal string? ReleaseRangeOutcome;

    /// <summary>finally 是否已执行完三个释放步骤；未执行前不能声称“全部释放成功”。</summary>
    internal bool ReleaseAttempted;

    internal bool AllReleasesSucceeded => ReleaseAttempted
        && IsReleaseSuccess(ReleaseViewOutcome)
        && IsReleaseSuccess(ReleaseRangeAcpOutcome)
        && IsReleaseSuccess(ReleaseRangeOutcome);

    internal string? ReleaseFailure
    {
        get
        {
            var failures = new List<string>();
            AddIfFailed(failures, "view", ReleaseViewOutcome);
            AddIfFailed(failures, "range-acp", ReleaseRangeAcpOutcome);
            AddIfFailed(failures, "range", ReleaseRangeOutcome);
            return failures.Count == 0 ? null : string.Join(",", failures);
        }
    }

    private static bool IsReleaseSuccess(string? outcome)
        => outcome is "released" or "not-created";

    private static void AddIfFailed(List<string> failures, string name, string? outcome)
    {
        if (outcome is null || IsReleaseSuccess(outcome))
        {
            return;
        }

        failures.Add($"{name}={outcome}");
    }
}

/// <summary>
/// 只读 TSF context/view 几何对照探针（默认关闭，`--probe-context-view`）。
/// 通过真实 `ITfContext::RequestEditSession(TF_ES_SYNC | TF_ES_READ)` 在自有 UI 线程安全阶段
/// 申请只读编辑会话，用真实 edit cookie 对同一临时 ACP 范围查询
/// `ITfContextView::GetTextExt/GetScreenExt/GetWnd`，再与 store 当前几何逐项对照。
/// 只移动本进程新建的临时 range（`ITfRangeACP::SetExtent`），绝不改文本、组合或真实 selection；
/// 不刷新布局、不重试、不把探针结果当作候选视觉结论。
/// </summary>
internal static class ContextViewProbe
{
    /// <summary>只读编辑会话标志：同步 + READ；绝不包含写锁或异步位。</summary>
    internal static uint ReadOnlySessionFlags => TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READ;

    /// <summary>
    /// 选择与 TIP 查询一致的临时范围：组合中优先用真实组合 span；否则折叠到 caret（隐藏宿主空文档为 0）。
    /// 纯函数：不触碰 store/COM。
    /// </summary>
    internal static ContextViewRange ResolveRange(bool composing, int compositionStart, int compositionEnd,
        int caret)
    {
        if (composing && compositionEnd > compositionStart && compositionStart >= 0)
        {
            return new ContextViewRange(compositionStart, compositionEnd, "composition");
        }

        var position = Math.Max(0, caret);
        return new ContextViewRange(position, position, composing ? "composition-empty" : "caret");
    }

    /// <summary>
    /// 临时 range 的 extent 是否精确等于请求范围（start 与 length 都相同）。纯函数。
    /// 只比较长度或只比较起点都会把不同范围误判为同一范围。
    /// </summary>
    internal static bool RangeMatches(ContextViewRange requested, int actualAcp, int actualCch)
        => actualAcp == requested.Start && actualCch == requested.End - requested.Start;

    /// <summary>
    /// 状态判定（R20）。判定顺序固定，先暴露“为什么没有可比对的几何”，再谈一致/不一致：
    /// 请求失败 → 会话未执行 → 会话失败 → 范围不一致 → 几何项被拒/失败 → 一致 / 不一致。
    /// <see cref="ContextViewProbeStatus.Ok"/> 要求：请求 S_OK、会话确实执行、会话 HRESULT 成功、
    /// 范围精确一致、文本 RECT 与 clipped 一致、屏幕 RECT 一致、HWND 一致。
    /// <para>
    /// <paramref name="rangeMatch"/> 为 null 表示 extent 从未成功取得：该分支按会话失败处理，
    /// 绝不把一般的会话失败错判成 <see cref="ContextViewProbeStatus.RangeMismatch"/>。
    /// 只有“确实取得 extent 且与请求范围不同”才是范围不一致。
    /// </para>
    /// 纯函数。
    /// </summary>
    internal static string Classify(int requestHResult, bool sessionRan, int sessionHResult,
        bool? rangeMatch, int firstQueryHResult, bool rectMatch, bool screenMatch, bool wndMatch)
    {
        if (requestHResult < 0)
        {
            return ContextViewProbeStatus.RequestFailed;
        }

        if (!sessionRan)
        {
            return ContextViewProbeStatus.SessionNotExecuted;
        }

        if (sessionHResult < 0)
        {
            return ContextViewProbeStatus.SessionFailed;
        }

        if (rangeMatch is null)
        {
            // 会话报告成功却没有 extent 事实：同样不能宣称几何可比，按会话失败如实上报。
            return ContextViewProbeStatus.SessionFailed;
        }

        if (!rangeMatch.Value)
        {
            return ContextViewProbeStatus.RangeMismatch;
        }

        if (firstQueryHResult < 0)
        {
            return ContextViewProbeStatus.Refused;
        }

        return rectMatch && screenMatch && wndMatch
            ? ContextViewProbeStatus.Ok
            : ContextViewProbeStatus.Mismatch;
    }

    /// <summary>默认关闭：未显式启用时直接返回 null，不接触 context/COM，也不写任何日志。</summary>
    internal static ContextViewProbeResult? RunIfEnabled(bool enabled, string label, ITfContext? context,
        uint tid, AcpTextStore store, TsfTrace trace, ContextViewRange range)
        => enabled ? Run(label, context, tid, store, trace, range) : null;

    /// <summary>执行一次只读会话查询并落盘一条 `context-view/probe` 记录；任何失败都不重试。</summary>
    internal static ContextViewProbeResult Run(string label, ITfContext? context, uint tid,
        AcpTextStore store, TsfTrace trace, ContextViewRange range)
        => Run(label, context, tid, store, trace, range, null);

    /// <summary>
    /// 同上，可注入 `ITfRangeACP` 访问实现。生产路径传 null（真实 vtable 槽 25/26）；
    /// 纯托管测试注入替身以覆盖“extent 失败 / 范围不一致”等真实分支。
    /// </summary>
    internal static ContextViewProbeResult Run(string label, ITfContext? context, uint tid,
        AcpTextStore store, TsfTrace trace, ContextViewRange range,
        ContextViewEditSession.IRangeAcpAccess? rangeAcp)
    {
        var record = new ContextViewProbeRecord { Label = label, Range = range };
        if (context is null)
        {
            record.Status = ContextViewProbeStatus.ContextUnavailable;
            TraceRecord(trace, record);
            return Result(record);
        }

        try
        {
            var session = rangeAcp is null
                ? new ContextViewEditSession(context, store, record, trace)
                : new ContextViewEditSession(context, store, record, trace, rangeAcp);
            // 应用阶段标记：这是已知的应用只读会话请求，不是 TSF/TIP 发起的查询。
            using var scope = store.Chain.PushScope(LayoutChainScope.RequestSession);
            var requestHr = context.RequestEditSession(tid, session, ReadOnlySessionFlags,
                out var sessionHr);
            record.RequestHResult = requestHr;
            record.SessionHResult = sessionHr;
        }
        catch (Exception ex)
        {
            // 异常不穿过 COM 边界；也不重试。
            record.ExceptionName = ex.GetType().Name;
            record.ExceptionMessage = ex.Message;
        }

        record.Status = record.ExceptionName is null
            ? Classify(record.RequestHResult, record.SessionRan, record.SessionHResult,
                record.RangeMatch, record.FirstQueryHResult ?? TsfHResult.E_FAIL, record.RectMatch,
                record.ScreenMatch, record.WndMatch)
            : ContextViewProbeStatus.Exception;

        TraceRecord(trace, record);
        return Result(record);
    }

    private static ContextViewProbeResult Result(ContextViewProbeRecord record) => new(
        record.Status, record.RequestHResult, record.SessionHResult, record.RectMatch)
    {
        SessionRan = record.SessionRan,
        RangeMatch = record.RangeMatch ?? false,
        ScreenMatch = record.ScreenMatch,
        WndMatch = record.WndMatch,
    };

    private static void TraceRecord(TsfTrace trace, ContextViewProbeRecord record)
    {
        try
        {
            trace("context-view", "probe", new Dictionary<string, object?>
            {
                ["label"] = record.Label,
                ["status"] = record.Status,
                ["range_start"] = record.Range.Start,
                ["range_end"] = record.Range.End,
                ["range_source"] = record.Range.Source,
                ["read_only_flags"] = $"0x{ReadOnlySessionFlags:X}",
                ["session_ran"] = record.SessionRan,
                ["request_hresult"] = TsfHResult.Describe(record.RequestHResult),
                ["session_hresult"] = TsfHResult.Describe(record.SessionHResult),
                ["do_edit_session_hresult"] = TsfHResult.Describe(record.DoEditSessionHResult),
                ["ec"] = record.Ec,
                ["get_start_hresult"] = Hr(record.GetStartHResult),
                ["query_range_acp_hresult"] = Hr(record.QueryRangeAcpHResult),
                ["set_extent_hresult"] = Hr(record.SetExtentHResult),
                ["get_extent_hresult"] = Hr(record.GetExtentHResult),
                ["actual_acp"] = record.ActualAcp,
                ["actual_cch"] = record.ActualCch,
                ["range_match"] = record.RangeMatch,
                ["geometry_skipped"] = record.GeometrySkipped,
                ["get_active_view_hresult"] = Hr(record.GetActiveViewHResult),
                ["first_query_hresult"] = Hr(record.FirstQueryHResult),
                ["view_text_ext_hresult"] = Hr(record.ViewTextExtHResult),
                ["view_rect"] = Rect(record.ViewRect),
                ["view_clipped"] = record.ViewClipped,
                ["store_text_ext_hresult"] = Hr(record.StoreTextExtHResult),
                ["store_rect"] = Rect(record.StoreRect),
                ["store_clipped"] = record.StoreClipped,
                ["rect_match"] = record.RectMatch,
                ["view_screen_hresult"] = Hr(record.ViewScreenHResult),
                ["view_screen"] = Rect(record.ViewScreen),
                ["store_screen_hresult"] = Hr(record.StoreScreenHResult),
                ["store_screen"] = Rect(record.StoreScreen),
                ["screen_match"] = record.ScreenMatch,
                ["view_wnd_hresult"] = Hr(record.ViewWndHResult),
                ["view_hwnd"] = $"0x{record.ViewHwnd.ToInt64():X}",
                ["store_wnd_hresult"] = Hr(record.StoreWndHResult),
                ["store_hwnd"] = $"0x{record.StoreHwnd.ToInt64():X}",
                ["wnd_match"] = record.WndMatch,
                ["release_view"] = record.ReleaseViewOutcome,
                ["release_range_acp"] = record.ReleaseRangeAcpOutcome,
                ["release_range"] = record.ReleaseRangeOutcome,
                ["release_attempted"] = record.ReleaseAttempted,
                ["release_failure"] = record.ReleaseFailure,
                ["all_releases_succeeded"] = record.AllReleasesSucceeded,
                ["store_view_cookie"] = record.StoreViewCookie,
                ["win32_thread_id"] = record.Win32ThreadId,
                ["managed_thread_id"] = record.ManagedThreadId,
                ["layout_generation"] = record.LayoutGeneration,
                ["document_version"] = record.DocumentVersion,
                ["composing"] = record.Composing,
                ["exception"] = record.ExceptionName,
                ["message"] = record.ExceptionMessage,
            });
        }
        catch (Exception)
        {
            // 日志失败不能影响宿主；探针本来就不改变任何状态。
        }
    }

    private static string? Hr(int? hr) => hr is null ? null : TsfHResult.Describe(hr.Value);

    private static string? Rect(TsfRect? rect) => rect is null ? null : rect.Value.ToString();

    internal static bool SameRect(TsfRect left, TsfRect right)
        => left.Left == right.Left && left.Top == right.Top
            && left.Right == right.Right && left.Bottom == right.Bottom;
}

/// <summary>
/// 只读编辑会话：`DoEditSession` 内用真实 edit cookie 建临时 range、移动 extent、查询 view 与 store 几何。
/// 只读：不调用任何文本/选区/组合写入；所有 out/QI 指针成对释放且释放结果落盘；异常不穿过 COM。
/// </summary>
internal sealed class ContextViewEditSession : ITfEditSession
{
    /// <summary>
    /// `ITfRangeACP` 的 `GetExtent`/`SetExtent` 调用。真实实现按 vtable 槽 25/26 调用原生接口；
    /// 纯托管测试可注入替身，以便在不初始化 COM 的前提下跑通真实控制流。
    /// </summary>
    internal interface IRangeAcpAccess
    {
        int Query(IntPtr range, out IntPtr rangeAcp);

        int SetExtent(IntPtr rangeAcp, int acpAnchor, int cch);

        int GetExtent(IntPtr rangeAcp, out int acpAnchor, out int cch);

        void Release(IntPtr pointer);
    }

    private sealed class NativeRangeAcpAccess : IRangeAcpAccess
    {
        internal static readonly NativeRangeAcpAccess Instance = new();

        public int Query(IntPtr range, out IntPtr rangeAcp)
        {
            var iid = TsfIids.RangeAcp;
            return Marshal.QueryInterface(range, in iid, out rangeAcp);
        }

        public int SetExtent(IntPtr rangeAcp, int acpAnchor, int cch)
            => TsfNative.SetRangeAcpExtent(rangeAcp, acpAnchor, cch);

        public int GetExtent(IntPtr rangeAcp, out int acpAnchor, out int cch)
            => TsfNative.GetRangeAcpExtent(rangeAcp, out acpAnchor, out cch);

        public void Release(IntPtr pointer) => Marshal.Release(pointer);
    }

    private readonly ITfContext _context;
    private readonly AcpTextStore _store;
    private readonly ContextViewProbeRecord _record;
    private readonly TsfTrace _trace;
    private readonly IRangeAcpAccess _rangeAcp;

    internal ContextViewEditSession(ITfContext context, AcpTextStore store,
        ContextViewProbeRecord record, TsfTrace trace)
        : this(context, store, record, trace, NativeRangeAcpAccess.Instance)
    {
    }

    internal ContextViewEditSession(ITfContext context, AcpTextStore store,
        ContextViewProbeRecord record, TsfTrace trace, IRangeAcpAccess rangeAcp)
    {
        _context = context;
        _store = store;
        _record = record;
        _trace = trace;
        _rangeAcp = rangeAcp;
    }

    /// <summary>分步留痕：崩溃/拒绝时能看出停在哪一步（诊断用途，不改变控制流）。</summary>
    private void Step(string step, int? hr = null)
    {
        try
        {
            _trace("context-view", "step", new Dictionary<string, object?>
            {
                ["label"] = _record.Label,
                ["step"] = step,
                ["hresult"] = hr is null ? null : TsfHResult.Describe(hr.Value),
                ["ec"] = _record.Ec,
                ["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId(),
            });
        }
        catch (Exception)
        {
            // 日志失败不影响只读查询。
        }
    }

    public int DoEditSession(uint ec)
    {
        var hr = RunSteps(ec);
        _record.DoEditSessionHResult = hr;
        Step("exit", hr);
        return hr;
    }

    private int RunSteps(uint ec)
    {
        Step("entered");
        IntPtr range = IntPtr.Zero;
        IntPtr rangeAcp = IntPtr.Zero;
        ITfContextView? view = null;
        try
        {
            _record.SessionRan = true;
            _record.Ec = ec;
            _record.Win32ThreadId = NativeMethods.CurrentWin32ThreadId();
            _record.ManagedThreadId = Environment.CurrentManagedThreadId;
            _record.LayoutGeneration = _store.LayoutGeneration;
            _record.DocumentVersion = _store.Document.Version;
            _record.Composing = _store.IsComposing;
            _record.StoreViewCookie = _store.ViewCookie;
            Step("begin");

            // 1) 新建临时 range（文档起点），随后只移动它自己的 extent。
            var startHr = _context.GetStart(ec, out range);
            _record.GetStartHResult = startHr;
            Step("get-start", startHr);
            if (startHr < 0)
            {
                return startHr;
            }

            if (range == IntPtr.Zero)
            {
                Step("get-start-null-pointer");
                return TsfHResult.E_FAIL;
            }

            var qiHr = _rangeAcp.Query(range, out rangeAcp);
            _record.QueryRangeAcpHResult = qiHr;
            Step("query-range-acp", qiHr);
            if (qiHr < 0)
            {
                return qiHr;
            }

            if (rangeAcp == IntPtr.Zero)
            {
                Step("query-range-acp-null-pointer");
                return TsfHResult.E_NOINTERFACE;
            }

            var setHr = _rangeAcp.SetExtent(rangeAcp, _record.Range.Start,
                _record.Range.End - _record.Range.Start);
            _record.SetExtentHResult = setHr;
            Step("set-extent", setHr);
            if (setHr < 0)
            {
                // 范围不可信就不查询；如实返回，调用方不会重试。
                return setHr;
            }

            // 2) 先证明临时 range 确实落在请求范围上：GetExtent 失败即停止，绝不继续查询。
            var extentHr = _rangeAcp.GetExtent(rangeAcp, out var actualAcp, out var actualCch);
            _record.GetExtentHResult = extentHr;
            Step("get-extent", extentHr);
            if (extentHr < 0)
            {
                // extent 事实不存在：RangeMatch 保持 null，由会话 HRESULT 表达失败（不是 range-mismatch）。
                return extentHr;
            }

            _record.ActualAcp = actualAcp;
            _record.ActualCch = actualCch;
            _record.RangeMatch = ContextViewProbe.RangeMatches(_record.Range, actualAcp, actualCch);
            Step("range-match", _record.RangeMatch.Value ? TsfHResult.S_OK : TsfHResult.E_FAIL);
            if (!_record.RangeMatch.Value)
            {
                // R20：范围不同就不得继续查询几何，更不能用其矩形证明“同一范围”。
                // 但“已取得 extent 且范围不同”是检查本身的结论，不是会话失败：
                // 返回 S_OK 让 TSF 不把它变成 *phrSession 失败，状态由 RangeMatch=false 判定为 range-mismatch。
                // 若这里返回 E_FAIL，TSF 会传成 sessionHr<0，Classify 只能判成 session-failed。
                _record.GeometrySkipped = true;
                _record.FirstQueryHResult = TsfHResult.S_OK;
                Step("range-mismatch-stop");
                return TsfHResult.S_OK;
            }

            // 3) 真实 context view。
            var viewHr = _context.GetActiveView(out view);
            _record.GetActiveViewHResult = viewHr;
            Step("get-active-view", viewHr);
            if (viewHr < 0)
            {
                return viewHr;
            }

            if (view is null)
            {
                Step("get-active-view-null-pointer");
                return TsfHResult.E_FAIL;
            }

            // 4) view 侧几何（TIP 实际可查询的同一路径）。
            int viewTextHr;
            TsfRect viewRect;
            int viewClipped;
            using (var viewScope = _store.Chain.PushScope(LayoutChainScope.ViewQuery))
            {
                viewTextHr = view.GetTextExt(ec, range, out viewRect, out viewClipped);
            }

            _record.ViewTextExtHResult = viewTextHr;
            _record.ViewRect = viewRect;
            _record.ViewClipped = viewClipped;
            Step("view-text-ext", viewTextHr);

            // 5) store 侧几何（同一读锁内的当前快照）：这是应用探针直接查询，不是 TSF 回调。
            int storeTextHr;
            TsfRect storeRect;
            int storeClipped;
            using (var storeScope = _store.Chain.PushScope(LayoutChainScope.StoreDirectQuery))
            {
                storeTextHr = _store.GetTextExt(_store.ViewCookie, _record.Range.Start,
                    _record.Range.End, out storeRect, out storeClipped);
            }

            _record.StoreTextExtHResult = storeTextHr;
            _record.StoreRect = storeRect;
            _record.StoreClipped = storeClipped;
            Step("store-text-ext", storeTextHr);

            int viewScreenHr;
            TsfRect viewScreen;
            using (var viewScreenScope = _store.Chain.PushScope(LayoutChainScope.ViewQuery))
            {
                viewScreenHr = view.GetScreenExt(out viewScreen);
            }

            _record.ViewScreenHResult = viewScreenHr;
            _record.ViewScreen = viewScreen;
            Step("view-screen-ext", viewScreenHr);

            int storeScreenHr;
            TsfRect storeScreen;
            using (var storeScreenScope = _store.Chain.PushScope(LayoutChainScope.StoreDirectQuery))
            {
                storeScreenHr = _store.GetScreenExt(_store.ViewCookie, out storeScreen);
            }

            _record.StoreScreenHResult = storeScreenHr;
            _record.StoreScreen = storeScreen;
            Step("store-screen-ext", storeScreenHr);

            int viewWndHr;
            IntPtr viewHwnd;
            using (var viewWndScope = _store.Chain.PushScope(LayoutChainScope.ViewQuery))
            {
                viewWndHr = view.GetWnd(out viewHwnd);
            }

            _record.ViewWndHResult = viewWndHr;
            _record.ViewHwnd = viewHwnd;
            Step("view-wnd", viewWndHr);

            int storeWndHr;
            IntPtr storeHwnd;
            using (var storeWndScope = _store.Chain.PushScope(LayoutChainScope.StoreDirectQuery))
            {
                storeWndHr = _store.GetWnd(_store.ViewCookie, out storeHwnd);
            }

            _record.StoreWndHResult = storeWndHr;
            _record.StoreHwnd = storeHwnd;
            Step("store-wnd", storeWndHr);

            // 每项都必须真实成功才可能一致：被拒的查询不能当成“矩形相等”。
            _record.RectMatch = viewTextHr >= 0 && storeTextHr >= 0
                && ContextViewProbe.SameRect(viewRect, storeRect)
                && (viewClipped != 0) == (storeClipped != 0);
            _record.ScreenMatch = viewScreenHr >= 0 && storeScreenHr >= 0
                && ContextViewProbe.SameRect(viewScreen, storeScreen);
            _record.WndMatch = viewWndHr >= 0 && storeWndHr >= 0 && viewHwnd != IntPtr.Zero
                && viewHwnd == storeHwnd;

            // 会话本身成功；各查询的 HRESULT 已逐项记录（拒绝由调用方分类，不重试）。
            var firstFailure = FirstFailure(viewTextHr, storeTextHr, viewScreenHr, storeScreenHr,
                viewWndHr, storeWndHr);
            _record.FirstQueryHResult = firstFailure;
            Step("done", firstFailure);
            return TsfHResult.S_OK;
        }
        catch (Exception ex)
        {
            _record.ExceptionName = ex.GetType().Name;
            _record.ExceptionMessage = ex.Message;
            _record.FirstQueryHResult = TsfHResult.E_FAIL;
            Step("exception");
            return TsfHResult.E_FAIL;
        }
        finally
        {
            // 每个 out/QI 指针各释放一次：view 是 RCW，rangeAcp 是 QI 新引用，range 是 GetStart 的引用。
            // 释放结果（含成功）都写进记录，绝不静默吞掉异常后声称“全部释放成功”。
            _record.ReleaseViewOutcome = ReleaseComObject(view, "view");
            _record.ReleaseRangeAcpOutcome = ReleasePointer(ref rangeAcp, "range-acp");
            _record.ReleaseRangeOutcome = ReleasePointer(ref range, "range");
            _record.ReleaseAttempted = true;
        }
    }

    private string ReleaseComObject(ITfContextView? view, string name)
    {
        if (view is null)
        {
            return "not-created";
        }

        try
        {
            Marshal.ReleaseComObject(view);
            return "released";
        }
        catch (Exception ex)
        {
            var outcome = $"failed:{ex.GetType().Name}";
            Step($"release-{name}-failed");
            _trace("context-view", "release-failed", new Dictionary<string, object?>
            {
                ["label"] = _record.Label,
                ["target"] = name,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return outcome;
        }
    }

    private string ReleasePointer(ref IntPtr pointer, string name)
    {
        if (pointer == IntPtr.Zero)
        {
            return "not-created";
        }

        var value = pointer;
        pointer = IntPtr.Zero;
        try
        {
            _rangeAcp.Release(value);
            return "released";
        }
        catch (Exception ex)
        {
            var outcome = $"failed:{ex.GetType().Name}";
            Step($"release-{name}-failed");
            _trace("context-view", "release-failed", new Dictionary<string, object?>
            {
                ["label"] = _record.Label,
                ["target"] = name,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return outcome;
        }
    }

    private static int FirstFailure(params int[] hresults)
    {
        foreach (var hr in hresults)
        {
            if (hr < 0)
            {
                return hr;
            }
        }

        return TsfHResult.S_OK;
    }
}
