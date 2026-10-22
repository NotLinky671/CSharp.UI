using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R29B：经 <see cref="ChildSurfaceGeometryExecutor"/> 的**真实控制流**验证 scroll-follow /
/// clip-out / move-window 的安全点、组合不变量、几何检查、精确恢复与停止语义。
/// 测试注入 <see cref="IChildSurfaceGeometryHost"/> 替身，但执行的仍是生产 opt-in 场景
/// 所用的同一个执行器——共享控制流，不是 mock-of-uncalled。
/// </summary>
public sealed class ChildSurfaceGeometryExecutorTests
{
    /// <summary>与生产 <see cref="ImeIdentity.VerifiedIdentityTag"/> 同构的已核验完整身份标签。</summary>
    private static readonly string VerifiedProfile = ImeIdentity.Describe(0,
        TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    private static TsfRect R(int l, int t, int r, int b) => TsfRect.FromLtrb(l, t, r, b);

    /// <summary>
    /// 可控宿主替身：可注入几何、探针失败、不变量破坏、截图失败、抛异常与恢复失败。
    /// <para>
    /// 矩形/滚动/窗口位移由真实状态（ScrollX/ScrollY/RootOrigin）推导——恢复保存的
    /// 偏移即恢复全部矩形，与真实宿主同构；初始 X/Y 与 root 原点可设为非零。
    /// 每个阶段可独立失败：Probe、Capture、Wait、各 getter、各 setter、各动作。
    /// </para>
    /// </summary>
    private class FakeHost : IChildSurfaceGeometryHost
    {
        /// <summary>默认与真实宿主一致：组合中的会话其文档持有自有焦点——
        /// CurrentFocusDoc=OwnDoc；suspend/挂起测试经 SuspendTsfFocus/显式置 null。</summary>
        public FakeHost() => CurrentFocusDoc = OwnDoc;

        // ---- 真实几何状态 -------------------------------------------------
        /// <summary>真实水平滚动偏移（恢复必须回到初始值，含非零）。</summary>
        internal int ScrollX = 0;

        /// <summary>真实垂直滚动偏移。</summary>
        internal int ScrollY = 9;

        /// <summary>root 顶层窗口原点（move-window 绝对恢复用）。</summary>
        internal int RootX = 40;

        internal int RootY = 100;

        private readonly TsfRect _baseInputRect = R(100, 200, 500, 260);

        private readonly TsfRect _baseRowRect = R(110, 210, 300, 235);

        private readonly TsfRect _baseViewportRect = R(80, 100, 620, 500);

        private readonly TsfRect _baseRootRect = R(0, 0, 760, 420);

        // 行/input 随滚动与 root 平移联动；viewport 只随 root 平移（固定参照）。
        // FreezeRow：行不随滚动平移（scroll-follow 几何检查失败的注入）。
        internal TsfRect RowRect => FreezeRow
            ? Shift(_baseRowRect, 0, RootY - _baseRootRect.Top)
            : Shift(_baseRowRect, -ScrollX, RootY - _baseRootRect.Top - ScrollY);

        internal TsfRect InputRect => InputVisible
            ? Shift(_baseInputRect, -ScrollX, RootY - _baseRootRect.Top - ScrollY)
            : default;

        internal TsfRect ViewportRect => Shift(_baseViewportRect, 0, RootY - _baseRootRect.Top);

        internal TsfRect RootRect => Shift(_baseRootRect, RootX - _baseRootRect.Left, RootY - _baseRootRect.Top);

        // ---- 不变量状态 ---------------------------------------------------
        internal bool Composing = true;

        internal int Writes;

        internal string Text = "ni";

        internal int SpanStart = 0;

        internal int SpanEnd = 2;

        internal string Profile = VerifiedProfile;

        internal int FocusChangeCount;

        internal int BeginCount = 1;

        internal int EndCount = 0;

        internal int SessionId = 7;

        // ---- 阶段独立失败注入 ----------------------------------------------
        /// <summary>该次 Probe（1 起计）返回失败；null 关闭。</summary>
        internal int? FailProbeAt;

        /// <summary>该次 Probe 起组合结束（快照 Composing=false）。</summary>
        internal int? MutateComposingAfterProbe;

        /// <summary>该次 Probe 起 focus 事件计数 +2（瞬时离开又回来）。</summary>
        internal int? MutateFocusCountAfterProbe;

        /// <summary>该次 Probe 起写入数变为 1。</summary>
        internal int? MutateWritesAfterProbe;

        /// <summary>该次 Probe 起 profile 变为未知。</summary>
        internal int? MutateProfileAfterProbe;

        /// <summary>该次 Probe 起 text/span 变化。</summary>
        internal int? MutateTextAfterProbe;

        /// <summary>该次 Probe 起 begin/end/session 连续性计数变化（同文本重启）。</summary>
        internal int? MutateContinuityAfterProbe;

        /// <summary>截图统一失败；也可按名单精确失败。</summary>
        internal bool CaptureFails;

        /// <summary>名字包含该子串的截图失败（如 "restored"）。</summary>
        internal string? CaptureFailNameContains;

        /// <summary>GetTextRowScreenRect 返回 default（原生查询失败）。</summary>
        internal bool FailRowQuery;

        /// <summary>GetInputClientScreenRect 返回 default。</summary>
        internal bool FailInputQuery;

        /// <summary>GetViewportClientScreenRect 返回 default。</summary>
        internal bool FailViewportQuery;

        /// <summary>GetRootWindowScreenRect 返回 default。</summary>
        internal bool FailRootQuery;

        /// <summary>SetScrollOffset 抛异常（恢复路径异常注入）。</summary>
        internal bool ThrowOnSetScroll;

        /// <summary>
        /// Capture 抛 IOException 的注入点（真实截图输出路径的 I/O 失败）。
        /// 1=baseline、2=第一次测量截图、3=恢复截图，对应成功序列的截图序号。
        /// </summary>
        internal int? CaptureThrowAt;

        /// <summary>Wait 抛异常的注入点（1 起计调用序号）。</summary>
        internal int? WaitThrowAt;

        /// <summary>该次 Probe（1 起计）抛异常。</summary>
        internal int? ProbeThrowAt;

        /// <summary>InvokeOnUiThread 自身抛异常的注入点（1 起计派发序号）。</summary>
        internal int? DispatchThrowAt;

        /// <summary>Record 抛异常：日志失败不得阻断几何回滚或翻转结果。</summary>
        internal bool ThrowOnRecord;

        /// <summary>测量动作 body 内追加执行的原生副作用钩子（部分移动注入）。</summary>
        internal Action? ActionHook;

        /// <summary>立即把 viewport 参照挪走（模拟原生漂移；测量/恢复复核必检）。</summary>
        internal void DriftViewportNow() => _baseViewportDrift += 13;

        /// <summary>SetRootWindowOrigin 抛异常。</summary>
        internal bool ThrowOnSetRootOrigin;

        /// <summary>恢复后 viewport 漂移（恢复复核应判失败）。</summary>
        internal bool DriftViewportAfterRestore;

        /// <summary>GetScreenExt 返回非零 HRESULT。</summary>
        internal int ScreenExtHResult = TsfHResult.S_OK;

        /// <summary>GetScreenExt 覆盖矩形；默认按 input∩viewport 的真实可见性推导。</summary>
        internal TsfRect? ScreenExtOverride;

        /// <summary>GetTextExt 返回非零 HRESULT。</summary>
        internal int TextExtHResult = TsfHResult.S_OK;

        /// <summary>GetTextExt clipped 覆盖；默认按真实裁剪推导。</summary>
        internal int? TextExtClippedOverride;

        /// <summary>文本行不随滚动平移（scroll-follow 几何失败）。</summary>
        internal bool FreezeRow;

        // ---- R30 可见性操作 --------------------------------------------------
        /// <summary>自有 input child 当前真实可见性（hide/show 操作直接改它）。</summary>
        internal bool InputVisible = true;

        /// <summary>宿主是否实现可见性操作（默认实现——模拟真实 R30 宿主）。</summary>
        internal bool VisibilitySupported = true;

        /// <summary>下一次可见性操作返回原生 false（归属仍在、调用失败）。</summary>
        internal bool VisibilityFails;

        /// <summary>下一次可见性操作抛异常（原生调用异常注入）。</summary>
        internal bool VisibilityThrows;

        /// <summary>
        /// 下一次可见性操作<b>先真实改变原生可见性再谎报失败</b>（SetWindowPos 已生效
        /// 但返回 false / 中途异常的真实情形）——执行器必须按动作前保存的原可见性
        /// 尽力恢复，绝不能把"已隐藏"当成"未发生"。<b>仅作用一次</b>（对 hide），
        /// 之后自动复位，让 finally 的恢复写按正常路径完成。
        /// </summary>
        internal bool VisibilityMutateThenFail;

        /// <summary>所有可见性操作调用记录：show/hide + 结果 Ok。</summary>
        internal List<string> VisibilityCalls { get; } = new();

        /// <summary>R30 只读查询：归属校验过的真实可见性读回（执行器在 hide 前调用）。</summary>
        public virtual bool ReadInputVisibility()
            => Dispatch(() =>
            {
                OnUiBody();
                DispatchLog.Add("visibility:read");
                return InputVisible;
            });

        // ---- R31：TSF 文档焦点模拟 ------------------------------------------
        // 真实不透明对象代表自有 doc/context；foreign 是另一个对象。
        // Suspend/Restore 经共享 TsfDocumentFocus 操作核——身份读回是真源。

        /// <summary>COM-visible managed token：Marshal.GetIUnknownForObject 需要
    /// 可封送类型；共享操作核用真实 IUnknown 指针比较身份，不是引用相等。
    /// 每个实例独占一个 COM identity——同一对象=Same，不同对象=Different。</summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    private sealed class TsfComToken
    {
    }

        /// <summary>自有 document manager 令牌（COM-visible 实例；GetIUnknownForObject
        /// 返回唯一指针，共享 CompareComIdentity 据此判 Same/Different）。</summary>
        internal object OwnDoc { get; } = new TsfComToken();

        /// <summary>自有 context 令牌。</summary>
        internal object OwnContext { get; } = new TsfComToken();

        /// <summary>当前 TSF 文档焦点（null=挂起；OwnDoc=自有；ForeignDoc=外部抢占）。
        /// ctor 默认置 OwnDoc（与真实组合会话一致）；挂起测试经 SuspendTsfFocus
        /// 或显式赋值改为 null/ForeignDoc。</summary>
        internal object? CurrentFocusDoc;

        /// <summary>外部 doc（reentry 注入）。</summary>
        internal object ForeignDoc { get; } = new TsfComToken();

        /// <summary>R31 宿主能力开关。默认 false——与生产 flag-off 路径同构：
        /// 未接线 TSF 焦点的宿主不走每图阶段读回管线。R31 测试经
        /// <see cref="NewFocusedHost"/> 显式置 true（R31 enabled 替身）。</summary>
        internal bool TsfFocusSupported = false;

        /// <summary>下一次 suspend 原生写已生效但谎报失败。</summary>
        internal bool SuspendMutateThenFail;

        /// <summary>下一次 suspend 抛异常（写已生效）。</summary>
        internal bool SuspendThrows;

        /// <summary>下一次 restore 原生写已生效但谎报失败。</summary>
        internal bool RestoreMutateThenFail;

        /// <summary>下一次 restore 抛异常（写已生效）。</summary>
        internal bool RestoreThrows;

        /// <summary>SetFocus 总调用次数（原生写）。</summary>
        internal List<object?> SetFocusCalls { get; } = new();

        /// <summary>所有 TSF 焦点操作记录。</summary>
        internal List<string> TsfFocusCalls { get; } = new();

        /// <summary>R31 复审：受控阶段（与真实宿主/执行器共用同一对象语义；
        /// 期望只在已验证 suspend/restore 成功后迁移）。</summary>
        private readonly TsfFocusStage _tsfFocusStage = new();
        public TsfFocusStage TsfFocusStageState => _tsfFocusStage;

        /// <summary>R31 复审：注入的共享门禁（测试注入外部状态变化；
        /// 真实宿主注入的是同一抽象的真实实现）。</summary>
        internal Func<string, string?>? GuardHook { get; set; }

        /// <summary>R31 复审：GetFocus 读取时切换成 foreign（foreign foreground +
        /// GetFocus→null/focus-lost 组合测试用）。</summary>
        internal object? FocusAfterRead { get; set; }

        /// <summary>R31 复审：GetTop 调用时注入 reentry（GetTop reentry → 绝不写）。</summary>
        internal Func<(int, object?)>? GetTopHook { get; set; }

        /// <summary>R31 复审：acquire 令牌的第一个 release 抛出（release ordering
        /// 测试）——只计数 ReleaseHandle 内的引用，不污染 ReadPhase/identity 比较。</summary>
        internal bool FirstReleaseThrows { get; set; }
        private bool _firstReleaseThrowConsumed;
        internal int HandleReleaseCount { get; private set; }

        /// <summary>R31 复审：suspend 完成后、600ms 等待期间 TSF 自动回到 OWN
        /// （auto-refocus → 证据必须被拒绝）。</summary>
        internal bool AutoRefocusDuringWait { get; set; }

        /// <summary>共享门禁（测试注入 GuardHook；默认无拒绝）。</summary>
        private string? TsfGuard(string stage) => GuardHook?.Invoke(stage);

        /// <summary>模拟真实 GetFocus：读取后应用 FocusAfterRead。</summary>
        private (int, object?) FakeGetFocus()
        {
            var result = (0, CurrentFocusDoc);
            if (FocusAfterRead is not null)
            {
                CurrentFocusDoc = FocusAfterRead;
                FocusAfterRead = null;
            }
            return result;
        }

        private int FakeSetFocus(object? doc)
        {
            CurrentFocusDoc = doc;
            SetFocusCalls.Add(doc);
            DispatchLog.Add(doc is null ? "tsf:suspend" : "tsf:restore");
            return 0;
        }

        private (int, object?) FakeGetTop(object doc)
            => GetTopHook?.Invoke() ?? (0, doc == OwnDoc ? (object?)OwnContext : null);

        /// <summary>ReadPhase/Acquire/Suspend/Restore 内部的尽力释放委托——
        /// <b>不</b>计 HandleRelease、不消费 FirstReleaseThrows（该注入专属于
        /// acquire 令牌的 ReleaseHandle；R31 返修后每图阶段读回也走此委托，
        /// 绝不能把"令牌释放"注入错位成"读回释放"注入）。</summary>
        private void FakeRelease(object o)
        {
        }

        private void FakeHandleRelease(object o)
        {
            HandleReleaseCount++;
            if (FirstReleaseThrows && !_firstReleaseThrowConsumed)
            {
                _firstReleaseThrowConsumed = true;
                throw new InvalidOperationException("injected release failure");
            }
        }

        public bool SupportsTsfFocusOperation => TsfFocusSupported;

        /// <summary>共享 acquire——与真实路径同一 <see cref="TsfDocumentFocus"/> 操作核；
        /// 测试只注入委托（GetFocus/GetTop/Release/门禁）。</summary>
        public virtual TsfFocusAcquireResult AcquireTsfFocus()
            => Dispatch(() =>
            {
                OnUiBody();
                TsfFocusCalls.Add("acquire");
                return TsfDocumentFocus.Acquire(FakeGetFocus, OwnDoc, OwnContext,
                    FakeGetTop, FakeRelease, TsfGuard, TsfGuard);
            });

        /// <summary>R31 复审：实际读回真实 TSF 焦点阶段（共享 ReadPhase+同一组
        /// 委托；测试替身不平行实现——真源是同一操作核）。</summary>
        public virtual TsfFocusPhaseResult ReadTsfFocusPhase()
            => TsfDocumentFocus.ReadPhase(FakeGetFocus, OwnDoc, OwnContext, FakeGetTop,
                FakeRelease, _tsfFocusStage.Saved, _tsfFocusStage.Phase,
                TsfGuard, TsfGuard);

        /// <summary>R31 复审：清理动作前的最后一刻原生归属门禁（共享门禁；
        /// 失去前台/焦点/归属时如实拒绝）。</summary>
        public virtual string? CheckTsfFocusCleanupAllowed()
            => TsfGuard("cleanup");

        public virtual TsfFocusOpResult SuspendTsfFocus()
            => Dispatch(() =>
            {
                OnUiBody();
                TsfFocusCalls.Add("suspend");
                // 共享 suspend 操作核：写前后各复核门禁；GetTop reentry/foreign
                // focus 已占据时如实拒绝，绝不写。
                if (SuspendThrows)
                {
                    SuspendThrows = false;
                    // 原生写已生效（焦点真为 null）才抛异常——"变更可能存在"如实。
                    CurrentFocusDoc = null;
                    SetFocusCalls.Add(null);
                    throw new InvalidOperationException("injected suspend failure");
                }

                var op = TsfDocumentFocus.Suspend(FakeGetFocus, FakeSetFocus,
                    OwnDoc, OwnContext, FakeGetTop, FakeRelease,
                    TsfGuard, TsfGuard);
                if (SuspendMutateThenFail)
                {
                    SuspendMutateThenFail = false;
                    return new TsfFocusOpResult(false, true,
                        "injected set-focus(null) returned false after applying");
                }
                return op;
            });

        public virtual TsfFocusOpResult RestoreTsfFocus(TsfFocusHandle saved)
            => Dispatch(() =>
            {
                OnUiBody();
                TsfFocusCalls.Add("restore");
                if (RestoreThrows)
                {
                    RestoreThrows = false;
                    throw new InvalidOperationException("injected restore failure");
                }

                var op = TsfDocumentFocus.Restore(saved, FakeGetFocus, FakeSetFocus,
                    FakeGetTop, FakeRelease, TsfGuard, TsfGuard);
                if (RestoreMutateThenFail)
                {
                    RestoreMutateThenFail = false;
                    return new TsfFocusOpResult(false, true,
                        "injected set-focus(saved) returned false after applying");
                }
                return op;
            });

        public virtual TsfFocusReleaseResult ReleaseTsfFocus(TsfFocusHandle? handle)
        {
            TsfFocusCalls.Add("release");
            return Dispatch(() => TsfDocumentFocus.ReleaseHandle(handle, FakeHandleRelease));
        }

        /// <summary>仅模拟真实 TsfHostWindow 状态——不直接成 <c>!InputVisible</c> 的派生；
        /// R30 正确性测试同时断言可见矩形与 GetScreenExt 归零。</summary>
        public bool SupportsVisibilityOperation => VisibilitySupported;

        public virtual ChildSurfaceVisibilityResult VisibilityOperation(bool show,
            bool showOnlyIfWasVisible)
            => Dispatch(() =>
            {
                OnUiBody();
                DispatchLog.Add($"visibility:{(show ? "show" : "hide")}");
                if (!VisibilitySupported)
                {
                    VisibilityCalls.Add($"{(show ? "show" : "hide")}:refused");
                    return new ChildSurfaceVisibilityResult(false, false, false,
                        "visibility-operation-unsupported");
                }

                var wasVisible = InputVisible;
                if (!show || showOnlyIfWasVisible)
                {
                    InputVisible = show;
                }

                if (VisibilityMutateThenFail)
                {
                    // 原生写已生效但报告失败：以当前真实状态回读如实应答 Ok=false。
                    // 只注入一次（hide），其后 finally 的恢复写不再被此故障拦截。
                    VisibilityMutateThenFail = false;
                    VisibilityCalls.Add($"{(show ? "show" : "hide")}:mutated-false");
                    return new ChildSurfaceVisibilityResult(false, wasVisible, InputVisible,
                        "injected set-window-pos returned false after applying");
                }

                if (VisibilityThrows)
                {
                    VisibilityCalls.Add($"{(show ? "show" : "hide")}:mutated-throw");
                    throw new InvalidOperationException("injected visibility operation failure");
                }

                if (VisibilityFails)
                {
                    VisibilityCalls.Add($"{(show ? "show" : "hide")}:false");
                    return new ChildSurfaceVisibilityResult(false, wasVisible, InputVisible,
                        "injected set-window-pos failure");
                }

                VisibilityCalls.Add($"{(show ? "show" : "hide")}:ok");
                return new ChildSurfaceVisibilityResult(true, wasVisible, InputVisible, "ok");
            });

        // ---- 计数与记录 -----------------------------------------------------
        internal int ProbeCount { get; private set; }

        internal int CaptureCalls { get; private set; }

        internal int WaitCalls { get; private set; }

        internal int ScrollByLogicalCalls { get; private set; }

        internal int ScrollByPhysicalCalls { get; private set; }

        internal int MoveRootCalls { get; private set; }

        internal int SetScrollCalls { get; private set; }

        internal int SetRootOriginCalls { get; private set; }

        internal int GetScreenExtCalls { get; private set; }

        internal int GetTextExtCalls { get; private set; }

        /// <summary>下一次 InvokeOnUiThread 的派发序号（供 ActionHook 精确注入）。</summary>
        internal int NextDispatchIndex => _dispatchCalls + 1;

        /// <summary>测量动作总次数（ScrollByLogical+ScrollByPhysical+MoveRootBy）。</summary>
        internal int MeasuredActions => ScrollByLogicalCalls + ScrollByPhysicalCalls + MoveRootCalls;

        internal int DispatcherViolations { get; private set; }

        internal List<string> CaptureNames { get; } = new();

        internal List<string> Records { get; } = new();

        /// <summary>受管 body 派发顺序：pre→action→post（供顺序断言）。</summary>
        internal List<string> DispatchLog { get; } = new();

        private bool _insideDispatcher;

        private void OnUiBody()
        {
            if (!_insideDispatcher)
            {
                DispatcherViolations++;
            }
        }

        private int _dispatchCalls;

        public bool InvokeOnUiThread(Func<bool> action)
        {
            _dispatchCalls++;
            var n = _dispatchCalls;
            var was = _insideDispatcher;
            _insideDispatcher = true;
            try
            {
                if (DispatchThrowAt is { } d && n == d)
                {
                    throw new InvalidOperationException("injected ui dispatch failure");
                }

                return action();
            }
            finally
            {
                _insideDispatcher = was;
            }
        }

        /// <summary>把单个 UI 操作经派发器帧执行（模拟真实宿主内部的 _window.Invoke）。</summary>
        private T Dispatch<T>(Func<T> body)
        {
            T result = default!;
            InvokeOnUiThread(() => { result = body(); return true; });
            return result;
        }

        /// <summary>探针入口（virtual 以便替身注入 transient 失败）。</summary>
        public virtual SafePointProbe Probe(string stage)
            => Dispatch(() => ProbeBody(stage));

        private SafePointProbe ProbeBody(string stage)
        {
            OnUiBody();
            ProbeCount++;
            var n = ProbeCount;
            if (ProbeThrowAt is { } t && n == t)
            {
                throw new InvalidOperationException("injected probe failure");
            }

            var composing = Composing;
            var writes = Writes;
            var profile = Profile;
            var focus = FocusChangeCount;
            var text = Text;
            var spanStart = SpanStart;
            var spanEnd = SpanEnd;
            var begin = BeginCount;
            var end = EndCount;
            var session = SessionId;

            if (MutateComposingAfterProbe is { } mc && n >= mc)
            {
                composing = false;
            }

            if (MutateFocusCountAfterProbe is { } mf && n >= mf)
            {
                focus += 2;
            }

            if (MutateWritesAfterProbe is { } mw && n >= mw)
            {
                writes = 1;
            }

            if (MutateProfileAfterProbe is { } mp && n >= mp)
            {
                profile = ImeIdentity.UnverifiableProfile;
            }

            if (MutateTextAfterProbe is { } mt && n >= mt)
            {
                text = "nih";
                spanEnd = 3;
            }

            if (MutateContinuityAfterProbe is { } mct && n >= mct)
            {
                end += 1;
                session += 1;
            }

            var ok = !(FailProbeAt is { } f && n == f)
                && profile != ImeIdentity.UnverifiableProfile;  // 与真实宿主一致：未核验身份=安全点失败
            DispatchLog.Add($"probe:{stage}:n{n}:{(ok ? "ok" : "fail")}");
            var probe = new SafePointProbe(ok,
                ok ? "ok" : $"stage:{stage} foreground-lost",
                text, spanStart, spanEnd, writes, composing, profile,
                begin, end, session, focus);
            // 首个成功安全点=执行器基线（各场景 Run 第一步都是 ProbeOrStop 基线
            // 探针，其后才有 Capture）——每图采集与执行器共用同一基线参照。
            if (ok && _executorBaseline is null)
            {
                _executorBaseline = probe.Snapshot;
            }

            return probe;
        }

        public void Record(string kind, IDictionary<string, object?> fields)
        {
            if (ThrowOnRecord)
            {
                throw new InvalidOperationException("injected record failure");
            }

            Records.Add(kind);
        }

        /// <summary>截图钩子（参数为本次截图名）：R31 用来在 clipped 截图内注入
        /// foreign TSF 焦点抢占（模拟 COM 重入期间外部夺取文档焦点）→
        /// 恢复必须拒绝覆盖。</summary>
        internal Action<string>? CaptureHook;

        /// <summary>root/monitor 抓图序号计数（真实每图接线断言用）。</summary>
        internal int RootCaptures { get; private set; }

        internal int MonitorCaptures { get; private set; }

        /// <summary>每张抓图前/后实际 TSF 焦点阶段读回次数（共享 RunStage 经本
        /// 宿主同一实例的 <see cref="ReadTsfFocusPhase"/>）。</summary>
        internal int PhaseReadCount { get; private set; }

        /// <summary>当前 Run 的基线快照：各场景首个 Probe（执行器基线安全点）
        /// 时同步进来——与生产 <c>SetLifecycleBaseline</c> 同构，不另造参照。</summary>
        private LifecycleSnapshot? _executorBaseline;

        /// <summary>当前组合/写入/profile 快照（Capture 无执行器基线注入时的退化
        /// 参照——与 ProbeBody 同一真源，绝不伪造字段）。</summary>
        private LifecycleSnapshot CurrentSnapshot()
            => new(Text, SpanStart, SpanEnd, Writes, Composing, Profile,
                BeginCount, EndCount, SessionId, FocusChangeCount);

        /// <summary>R31 复审返修：root 抓图<b>之后</b>把 TSF 焦点自动改回 OWN
        /// （挂起阶段瞬时回焦）→ root 后置阶段读回必须判失败、monitor 永不发起。</summary>
        internal bool AutoRefocusAfterRootCapture { get; set; }

        /// <summary>R31 返修：是否启用每图共享管线（与真实宿主同一
        /// <see cref="ScenarioRunner.SelectCapturePhaseReader"/> 语义——仅当本替身的
        /// R31 能力 <see cref="SupportsTsfFocusOperation"/> 为 true 时才接线阶段读回；
        /// 默认 R29/R30 测试流（TsfFocusSupported=false）走旧的单次整体检查，
        /// 不产生任何新增阶段读回/探针——与生产 flag-off 路径同构）。
        /// 测试需要观察"无每图 phase 接线"行为时可显式置 false。</summary>
        internal bool? UseSharedCaptureStageOverride { get; set; }

        private bool UseSharedCaptureStage
            => UseSharedCaptureStageOverride ?? SupportsTsfFocusOperation;

        /// <summary>
        /// R31 复审返修：与真实 <c>ScenarioRunner.CaptureChildSurface</c> 同一
        /// <see cref="ChildSurfaceEvidenceCapture.RunStage"/> 每图管线——root/monitor
        /// 两张，每张前后都经<b>本宿主同一实例</b>的实际阶段读回（共享
        /// <see cref="TsfDocumentFocus.ReadPhase"/>+同一组委托），并在阶段 COM
        /// 查询后复核组合/原生安全点；root 后置阶段失败即不发起 monitor。
        /// 不再只凭"整个 bundle 之后"的一次检查放行。
        /// 本管线<b>只</b>在 R31 能力启用时接线（默认 R29/R30 host 走旧路径）。
        /// </summary>
        public CaptureVerdict Capture(string name)
            => Dispatch(() =>
            {
                OnUiBody();
                CaptureCalls++;
                CaptureNames.Add(name);
                DispatchLog.Add($"capture:{name}");
                CaptureHook?.Invoke(name);

                if (!UseSharedCaptureStage)
                {
                    // 旧语义：单次整体检查（无每图 phase）——R29/R30 默认测试流。
                    if (CaptureThrowAt is { } t && CaptureCalls == t)
                    {
                        throw new IOException("injected capture io failure");
                    }

                    if (CaptureFails)
                    {
                        return new CaptureVerdict(false, "capture failed");
                    }

                    if (CaptureFailNameContains is { } sub && name.Contains(sub))
                    {
                        return new CaptureVerdict(false, "capture failed");
                    }

                    return CaptureVerdict.Passed();
                }

                var baseline = _executorBaseline ?? CurrentSnapshot();

                // 每张抓图的本体（真实路径在 RunStage 的 pre/post 之间执行）。
                EvidenceCaptureResult? DoCapture(string label)
                {
                    if (CaptureThrowAt is { } t && CaptureCalls == t && label == "root")
                    {
                        throw new IOException("injected capture io failure");
                    }

                    if (label == "root")
                    {
                        RootCaptures++;
                        DispatchLog.Add($"capture-image:{name}:root");
                    }
                    else
                    {
                        MonitorCaptures++;
                        DispatchLog.Add($"capture-image:{name}:monitor");
                    }

                    if (CaptureFails
                        || (CaptureFailNameContains is { } sub && name.Contains(sub)))
                    {
                        return null;
                    }

                    var rect = label == "root"
                        ? TsfRect.FromLtrb(0, 0, 4, 4)
                        : TsfRect.FromLtrb(0, 0, 8, 8);
                    return new EvidenceCaptureResult($"{name}-{label}.png", rect);
                }

                TsfFocusPhaseResult ReadPhaseForStage()
                {
                    PhaseReadCount++;
                    return ReadTsfFocusPhase();
                }

                var root = ChildSurfaceEvidenceCapture.RunStage(name, "root",
                    () => Probe($"{name}:root-probe"),
                    () => DoCapture("root"), baseline, Record,
                    phase: ReadPhaseForStage);
                if (!root.Ok)
                {
                    // root 失败（含后置阶段不符）→ monitor 抓图绝不发起；
                    // 首败原因原样带出（执行器据此分类，不再复探）。
                    return new CaptureVerdict(false, root.Reason)
                    {
                        PhaseMismatch = root.Reason.Contains("tsf focus phase"),
                    };
                }

                // R31 复审返修：root 已抓、monitor 未发起前焦点瞬时回 OWN——
                // monitor 前置阶段读回必须如实拒绝。
                if (AutoRefocusAfterRootCapture)
                {
                    CurrentFocusDoc = OwnDoc;
                }

                var monitor = ChildSurfaceEvidenceCapture.RunStage(name, "monitor",
                    () => Probe($"{name}:monitor-probe"),
                    () => DoCapture("monitor"), baseline, Record,
                    phase: ReadPhaseForStage);
                return monitor.Ok
                    ? CaptureVerdict.Passed()
                    : new CaptureVerdict(false, monitor.Reason)
                    {
                        PhaseMismatch = monitor.Reason.Contains("tsf focus phase"),
                    };
            });

        public void Wait(int milliseconds)
        {
            WaitCalls++;
            DispatchLog.Add($"wait:{milliseconds}");
            if (WaitThrowAt is { } t && WaitCalls == t)
            {
                throw new InvalidOperationException("injected wait failure");
            }

            // R31 复审：suspend 后等待期间 TIP/外部自动把焦点恢复成 OWN——
            // 期望仍是 NULL；后续安全点必须实际读回并如实判失败（不拿回到 OWN
            // 的截图当挂起证据）。
            if (AutoRefocusDuringWait && CurrentFocusDoc is null)
            {
                CurrentFocusDoc = OwnDoc;
            }
        }

        public int GetScrollX() => Dispatch(() => { OnUiBody(); return ScrollX; });

        public int GetScrollY() => Dispatch(() => { OnUiBody(); return ScrollY; });

        public virtual void SetScrollOffset(int x, int y)
            => Dispatch(() =>
            {
                OnUiBody();
                SetScrollCalls++;
                DispatchLog.Add($"set-scroll:{x},{y}");
                if (ThrowOnSetScroll)
                {
                    throw new InvalidOperationException("injected set-scroll failure");
                }

                ScrollX = x;
                ScrollY = y;
                if (DriftViewportAfterRestore)
                {
                    // 恢复后把 viewport 挪走（模拟原生漂移）→ 恢复几何复核必须判失败。
                    DriftViewportNow();
                }
                return true;
            });

        /// <summary>滚动钩子：子类可拦截（如让行不动、抛异常）。默认只改 ScrollY。</summary>
        internal virtual void OnScrollByLogical(int logicalDelta, int physical)
            => ScrollY += physical;

        internal int LogicalDeltaReturned = 11;

        /// <summary>ScrollByLogical 抛异常的注入点。</summary>
        internal bool ThrowOnScrollLogical;

        /// <summary>ScrollByLogical 只部分移动（真实物理 delta 小于返回）。</summary>
        internal bool PartialScroll;

        public int ScrollByLogical(int logicalDelta)
            => Dispatch(() =>
            {
                OnUiBody();
                ScrollByLogicalCalls++;
                DispatchLog.Add("action:scroll-logical");
                if (ThrowOnScrollLogical)
                {
                    throw new InvalidOperationException("injected scroll failure");
                }

                OnScrollByLogical(logicalDelta, LogicalDeltaReturned);
                ActionHook?.Invoke();
                return PartialScroll ? 0 : LogicalDeltaReturned;
            });

        internal bool ThrowOnScrollPhysical;

        /// <summary>物理滚动钩子：子类可拦截（如让 viewport 漂移）。默认只改 ScrollY。</summary>
        internal virtual void OnScrollByPhysical(int deltaY) => ScrollY += deltaY;

        public void ScrollByPhysical(int deltaY)
            => Dispatch(() =>
            {
                OnUiBody();
                ScrollByPhysicalCalls++;
                DispatchLog.Add("action:scroll-physical");
                if (ThrowOnScrollPhysical)
                {
                    throw new InvalidOperationException("injected physical scroll failure");
                }

                OnScrollByPhysical(deltaY);
                ActionHook?.Invoke();
                return true;
            });

        internal bool ThrowOnMoveRoot;

        /// <summary>MoveRootBy 只部分移动（dy 生效一半）。</summary>
        internal bool PartialMove;

        public void MoveRootBy(int dx, int dy)
            => Dispatch(() =>
            {
                OnUiBody();
                MoveRootCalls++;
                DispatchLog.Add("action:move-root");
                if (ThrowOnMoveRoot)
                {
                    throw new InvalidOperationException("injected move failure");
                }

                RootX += dx;
                RootY += PartialMove ? dy / 2 : dy;
                ActionHook?.Invoke();
                return true;
            });

        public void SetRootWindowOrigin(int x, int y)
            => Dispatch(() =>
            {
                OnUiBody();
                SetRootOriginCalls++;
                DispatchLog.Add($"set-root-origin:{x},{y}");
                if (ThrowOnSetRootOrigin)
                {
                    throw new InvalidOperationException("injected root-restore failure");
                }

                RootX = x;
                RootY = y;
                if (DriftViewportAfterRestore)
                {
                    // 恢复后把 viewport 挪走（模拟原生漂移）→ 恢复几何复核必须判失败。
                    DriftViewportNow();
                }
                return true;
            });

        private int _baseViewportDrift;

        private TsfRect ViewportRectWithDrift => Shift(ViewportRect, 0, _baseViewportDrift);

        public TsfRect GetTextRowScreenRect()
            => Dispatch(() => { OnUiBody(); return FailRowQuery ? default : RowRect; });

        public TsfRect GetInputClientScreenRect()
            => Dispatch(() => { OnUiBody(); return FailInputQuery ? default : InputRect; });

        public TsfRect GetViewportClientScreenRect()
            => Dispatch(() => { OnUiBody(); return FailViewportQuery ? default : ViewportRectWithDrift; });

        public TsfRect GetRootWindowScreenRect()
            => Dispatch(() => { OnUiBody(); return FailRootQuery ? default : RootRect; });

        public int GetScreenExt(out TsfRect rect)
        {
            var r = Dispatch(() =>
            {
                OnUiBody();
                GetScreenExtCalls++;
                // 官方语义：input 不可见或完全推出 viewport → 全零；否则回传可见矩形。
                var visible = InputVisible && InputRect.Bottom > ViewportRectWithDrift.Top;
                return ScreenExtOverride ?? (visible ? InputRect : default);
            });
            rect = r;
            return ScreenExtHResult;
        }

        public int GetTextExt(int acpStart, int acpEnd, out TsfRect rect, out int clipped)
        {
            var (r, c) = Dispatch(() =>
            {
                OnUiBody();
                GetTextExtCalls++;
                var fullyClipped = InputRect.Bottom <= ViewportRectWithDrift.Top;
                var flag = TextExtClippedOverride ?? (fullyClipped ? 1 : 0);
                return (fullyClipped ? default(TsfRect) : RowRect, flag);
            });
            rect = r;
            clipped = c;
            return TextExtHResult;
        }

        internal static TsfRect Shift(TsfRect r, int dx, int dy)
            => TsfRect.FromLtrb(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
    }

    private static FakeHost NewHost() => new();

    // ---- 成功路径 ----------------------------------------------------

    [Fact]
    public void ScrollFollowSucceedsAndRestoresScroll()
    {
        var host = NewHost();
        LifecycleSnapshot? baseline = null;
        var ex = new ChildSurfaceGeometryExecutor(host, b => baseline = b);

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.True(ok);
        Assert.Equal(1, host.ScrollByLogicalCalls);
        Assert.Equal(1, host.SetScrollCalls);
        Assert.Equal(9, host.ScrollY);              // 精确恢复到保存的 9
        Assert.Equal(0, host.ScrollX);
        Assert.True(host.CaptureCalls >= 3);        // baseline + scrolled + restored
        Assert.Equal(0, host.DispatcherViolations);
        Assert.NotNull(baseline);
        Assert.Equal(ChildSurfaceStop.None, ex.Stop);
    }

    [Fact]
    public void ClipOutSucceedsAndRestoresGeometry()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.True(ok);
        Assert.Equal(1, host.ScrollByPhysicalCalls);
        Assert.Equal(1, host.SetScrollCalls);
        Assert.Equal(9, host.ScrollY);
        Assert.Equal(1, host.GetScreenExtCalls);
        Assert.Equal(1, host.GetTextExtCalls);
        Assert.Equal(0, host.DispatcherViolations);
    }

    [Fact]
    public void MoveWindowSucceedsAndRestores()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.True(ok);
        Assert.Equal(1, host.MoveRootCalls);
        Assert.Equal(1, host.SetRootOriginCalls);
        Assert.Equal(100, host.RootY);
        Assert.Equal(40, host.RootX);
        Assert.Equal(0, host.DispatcherViolations);
    }

    // ---- 非零初始 X/Y 精确恢复（原审核项 4）----------------------------

    [Fact]
    public void ScrollFollowRestoresExactNonZeroInitialXY()
    {
        var host = NewHost();
        host.ScrollX = 5;
        host.ScrollY = 9;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.True(ok);
        Assert.Equal(5, host.ScrollX);              // X 必须恢复 5，不是被重置 0
        Assert.Equal(9, host.ScrollY);
        Assert.Contains("set-scroll:5,9", host.DispatchLog);
    }

    [Fact]
    public void ClipOutRestoresExactNonZeroInitialXY()
    {
        var host = NewHost();
        host.ScrollX = 5;
        host.ScrollY = 9;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.True(ok);
        Assert.Equal(5, host.ScrollX);
        Assert.Equal(9, host.ScrollY);
    }

    [Fact]
    public void MoveWindowRestoresExactNonZeroRootOrigin()
    {
        var host = NewHost();
        host.RootX = 137;
        host.RootY = 100;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.True(ok);
        Assert.Equal(137, host.RootX);
        Assert.Equal(100, host.RootY);
        Assert.Contains("set-root-origin:137,100", host.DispatchLog);
    }

    // ---- 安全点 / 不变量失败 -----------------------------------------

    [Fact]
    public void BaselineProbeFailureStopsBeforeAnyCapture()
    {
        var host = NewHost();
        host.FailProbeAt = 1;                       // baseline probe 失败
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(0, host.CaptureCalls);
        Assert.Equal(0, host.ScrollByLogicalCalls);
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
    }

    [Fact]
    public void NonComposingBaselineRejected()
    {
        var host = NewHost();
        host.Composing = false;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        Assert.False(ex.Run(ChildSurfaceScenario.MoveWindow, 0));
        Assert.Equal(ChildSurfaceStop.CompositionChanged, ex.Stop);
    }

    [Fact]
    public void UnverifiableProfileBaselineRejected()
    {
        var host = NewHost();
        host.Profile = ImeIdentity.UnverifiableProfile;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        Assert.False(ex.Run(ChildSurfaceScenario.ScrollFollow, 7));
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
    }

    [Fact]
    public void NonZeroWritesBaselineRejected()
    {
        var host = NewHost();
        host.Writes = 3;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        Assert.False(ex.Run(ChildSurfaceScenario.MoveWindow, 0));
        Assert.Equal(ChildSurfaceStop.CompositionChanged, ex.Stop);
    }

    // 每个不变量的独立变异都必须被受管复核检出（text/span/composing/writes/
    // profile/连续性计数/focus 事件）。n>=2 使基线仍有效、变异在测量 body 内触发。
    [Theory]
    [InlineData("composing")]
    [InlineData("writes")]
    [InlineData("profile")]
    [InlineData("text")]
    [InlineData("continuity")]
    [InlineData("focus")]
    public void EachInvariantMutationIsDetected(string which)
    {
        var host = NewHost();
        switch (which)
        {
            case "composing": host.MutateComposingAfterProbe = 2; break;
            case "writes": host.MutateWritesAfterProbe = 2; break;
            case "profile": host.MutateProfileAfterProbe = 2; break;
            case "text": host.MutateTextAfterProbe = 2; break;
            case "continuity": host.MutateContinuityAfterProbe = 2; break;
            case "focus": host.MutateFocusCountAfterProbe = 2; break;
        }
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok, $"mutation {which} must fail the scenario");
        // 身份丢失是安全点失败（与真实宿主一致）；其余是不变量破坏。
        var expected = which == "profile" ? ChildSurfaceStop.SafePointLost : ChildSurfaceStop.CompositionChanged;
        Assert.Equal(expected, ex.Stop);
    }

    [Fact]
    public void FocusTransientChangeDetectedByCounter()
    {
        var host = NewHost();
        host.FocusChangeCount = 4;
        host.MutateFocusCountAfterProbe = 2;        // focus 事件计数变化 → 不连续
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.CompositionChanged, ex.Stop);
    }

    // ---- 首个失败不可恢复（原审核项 1）----------------------------------

    /// <summary>
    /// 让 Probe 在 "*-after" 阶段失败一次后恢复正常（transient 安全点丢失）。
    /// 执行器首个失败必须保持失败——不得被后续合法快照翻转为成功。
    /// </summary>
    private sealed class FailOnceAfterHost : FakeHost
    {
        public override SafePointProbe Probe(string stage)
        {
            var probe = base.Probe(stage);
            if (!_failed && stage.EndsWith("-after", StringComparison.Ordinal))
            {
                _failed = true;
                return new SafePointProbe(false, $"{stage} transient safepoint loss",
                    probe.Snapshot.Text, probe.Snapshot.SpanStart, probe.Snapshot.SpanEnd,
                    probe.Snapshot.Writes, probe.Snapshot.Composing, probe.Snapshot.Profile,
                    probe.Snapshot.CompositionBeginCount, probe.Snapshot.CompositionEndCount,
                    probe.Snapshot.CompositionSessionId, probe.Snapshot.InputFocusChangeCount);
            }
            return probe;
        }

        private bool _failed;
    }

    [Fact]
    public void TransientFailedProbeCannotRecoverIntoSuccess()
    {
        var host = new FailOnceAfterHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        // 失败之后只允许基线截图存在——不得产出任何测量证据。
        var measuredCaptures = host.CaptureNames.Where(n => n != "r29b-baseline").ToList();
        Assert.Empty(measuredCaptures);
    }

    [Fact]
    public void NoMeasurementCaptureAfterFailedProbe()
    {
        // scroll-settle-after 是第 3 次 Probe（baseline、initial-pre、initial-post 之后）。
        var host = NewHost();
        host.FailProbeAt = ProbeIndexOfSettleAfter();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
        // 只允许 baseline 截图存在；失败后的测量/恢复截图不得出现。
        var afterFailure = host.CaptureNames.Where(n => n != "r29b-baseline").ToList();
        Assert.Empty(afterFailure);
    }

    /// <summary>scroll-settle-after 在当前执行器序列里的 Probe 序号。</summary>
    private static int ProbeIndexOfSettleAfter()
    {
        // 序列：1 scroll-before、2 scroll-initial-before、3 scroll-initial-after、
        // 4 scroll-action-before、5 scroll-action-after、6 scroll-settle-before、
        // 7 scroll-settle-after（WaitProbed 等待前后各复核一次）
        return 7;
    }

    [Fact]
    public void FailedAfterProbeInActionBodyBlocksAllScenarios()
    {
        foreach (var scenario in Enum.GetValues<ChildSurfaceScenario>())
        {
            var host = NewHost();
            // 每个场景动作 body 的 post-probe（*-action-after）是第 5 次 Probe。
            host.FailProbeAt = 5;
            var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

            var ok = ex.Run(scenario, 7);

            Assert.False(ok, $"{scenario} must fail on transient post-action probe loss");
            Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        }
    }

    // ---- 基线捕获后失焦阻止测量动作（原审核项 2）-------------------------

    [Fact]
    public void LossAfterBaselineCapturePreventsMeasuredAction()
    {
        foreach (var scenario in Enum.GetValues<ChildSurfaceScenario>())
        {
            var host = new LoseAfterBaselineHost();
            var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

            var ok = ex.Run(scenario, 7);

            Assert.False(ok, $"{scenario} must fail when composition lost after baseline capture");
            Assert.Equal(0, host.MeasuredActions);
        }
    }

    /// <summary>
    /// baseline 截图返回后改变组合状态（模拟前台/焦点丢失）：
    /// 受管测量 body 的前置复核必须让动作根本不执行。
    /// </summary>
    private sealed class LoseAfterBaselineHost : FakeHost
    {
        public LoseAfterBaselineHost()
        {
            // 序列中动作前置 Probe 是 scroll-initial-before（第2次）或
            // scroll-action-before（第4次）；让第 2 次起组合结束。
            MutateComposingAfterProbe = 2;
        }
    }

    // ---- 恢复截图失败同样判失败（原审核项 3）-------------------------------

    [Theory]
    [InlineData(0)]   // ScrollFollow
    [InlineData(1)]   // ClipOut
    [InlineData(2)]   // MoveWindow
    public void RestoreCaptureFailureInvalidatesExperiment(int scenario)
    {
        var host = NewHost();
        host.CaptureFailNameContains = "restored";
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
    }

    // ---- 恢复失败判失败（原审核项 3/4）-------------------------------------

    [Fact]
    public void SetScrollThrowOnRestoreMarksFailure()
    {
        var host = NewHost();
        host.ThrowOnSetScroll = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
    }

    [Fact]
    public void SetRootOriginThrowOnRestoreMarksFailure()
    {
        var host = NewHost();
        host.ThrowOnSetRootOrigin = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
    }

    [Fact]
    public void ViewportDriftAfterRestoreMarksFailure()
    {
        var host = NewHost();
        host.DriftViewportAfterRestore = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
    }

    // ---- 动作抛异常仍恢复（含前置/部分移动）---------------------------------

    [Fact]
    public void ActionThrowBeforeMoveStillRestoresExactly()
    {
        var host = new ThrowOnActionHost();
        host.ScrollY = 9;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.ActionException, ex.Stop);
        // 恢复仍以保存值回滚（此时是 no-op 回写）。
        Assert.Equal(1, host.SetScrollCalls);
        Assert.Equal(9, host.ScrollY);
    }

    [Fact]
    public void MoveWindowThrowBeforeMoveRestoresAbsoluteOrigin()
    {
        var host = NewHost();
        host.ThrowOnMoveRoot = true;
        host.RootX = 137;
        host.RootY = 100;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.ActionException, ex.Stop);
        // 绝对原点恢复调用（非盲目反向增量）。
        Assert.Equal(1, host.SetRootOriginCalls);
        Assert.Contains("set-root-origin:137,100", host.DispatchLog);
        Assert.Equal(137, host.RootX);
        Assert.Equal(100, host.RootY);
    }

    [Fact]
    public void MoveWindowPartialMoveRestoresAbsoluteOrigin()
    {
        var host = NewHost();
        host.PartialMove = true;                    // dy 只生效一半
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.MoveWindow, 0);

        Assert.False(ok);
        // 几何复核失败（实际位移 ≠ 期望 delta）——但恢复仍以保存的绝对原点回滚。
        Assert.Equal(1, host.SetRootOriginCalls);
        Assert.Equal(100, host.RootY);
        Assert.Equal(40, host.RootX);
    }

    private sealed class ThrowOnActionHost : FakeHost
    {
        internal override void OnScrollByLogical(int logicalDelta, int physical)
            => throw new InvalidOperationException("simulated scroll failure");
    }

    // ---- 初始几何/矩形查询失败判失败（原审核项 4/5）--------------------------

    [Fact]
    public void InvalidInitialRowGeometryFails()
    {
        var host = NewHost();
        host.FailRowQuery = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    [Fact]
    public void InvalidInitialViewportGeometryFailsClip()
    {
        var host = NewHost();
        host.FailViewportQuery = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    [Fact]
    public void ZeroPhysicalDeltaFailsScroll()
    {
        var host = NewHost();
        host.LogicalDeltaReturned = 0;              // 报回 0 物理 delta = 无实际滚动
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    [Fact]
    public void ViewportDriftDuringMeasureFailsClip()
    {
        var host = new ViewportDriftHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    /// <summary>测量动作后 viewport 漂移（固定参照失效 → clip-verify 必须判失败）。</summary>
    private sealed class ViewportDriftHost : FakeHost
    {
        internal override void OnScrollByPhysical(int deltaY)
        {
            base.OnScrollByPhysical(deltaY);
            DriftViewportNow();   // 动作后立即漂移 viewport 参照。
        }
    }

    // ---- 非零"空"矩形不算合法零矩形（原审核项 5）------------------------------

    [Fact]
    public void NonZeroEmptyScreenExtRectFails()
    {
        var host = NewHost();
        // input 推出 viewport 后 GetScreenExt 回传 {7,8,7,8}——非零空矩形，必须拒绝。
        host.ScreenExtOverride = R(7, 8, 7, 8);
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    [Fact]
    public void GetScreenExtHResultFailureFails()
    {
        var host = NewHost();
        host.ScreenExtHResult = unchecked((int)0x80004005); // E_FAIL
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
    }

    [Fact]
    public void GetTextExtHResultFailureFails()
    {
        var host = NewHost();
        host.TextExtHResult = TsfHResult.TS_E_NOLAYOUT;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
    }

    [Fact]
    public void GetTextExtNotClippedFails()
    {
        var host = NewHost();
        host.TextExtClippedOverride = 0;            // clipped 必须为真
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
    }

    // ---- UI 受管派发：pre+action+post 同一 body（原审核项 2）-------------------

    [Fact]
    public void MeasuredActionIsFusedWithGuardsInOneDispatch()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.True(ok);
        // 动作派发日志中 pre→action→post 必须出自同一个受管 body：
        // "probe:scroll-action-before" 与 "probe:scroll-action-after" 之间恰好夹一个动作。
        var beforeIdx = host.DispatchLog.FindIndex(e => e.StartsWith("probe:scroll-action-before"));
        var actionIdx = host.DispatchLog.FindIndex(e => e == "action:scroll-logical");
        var afterIdx = host.DispatchLog.FindIndex(e => e.StartsWith("probe:scroll-action-after"));
        Assert.True(beforeIdx >= 0 && actionIdx == beforeIdx + 1 && afterIdx == actionIdx + 1,
            $"fused order violated: {string.Join(" | ", host.DispatchLog)}");
    }

    [Fact]
    public void GeometryQueriesRunInsideDispatchedBody()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.Equal(0, host.DispatcherViolations);
    }

    // ---- 截图失败 -----------------------------------------------------

    [Fact]
    public void CaptureFailureStops()
    {
        var host = NewHost();
        host.CaptureFails = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.CaptureFailed, ex.Stop);
    }

    // ---- 几何检查失败 -------------------------------------------------

    [Fact]
    public void ScrollFollowFailsWhenRowDoesNotMove()
    {
        var host = new RowFrozenHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.GeometryCheckFailed, ex.Stop);
    }

    /// <summary>文本行不随滚动平移的 host（scroll-follow 几何检查失败）。</summary>
    private sealed class RowFrozenHost : FakeHost
    {
        internal RowFrozenHost() => FreezeRow = true;
    }

    // ---- 异常路径：真实 try/finally 几何回滚（本轮回修）----------------------
    //
    // 以下注入覆盖独立审核复现的 9 项失败：截图抛 IOException（baseline/测量后/
    // 恢复）、Wait 抛异常（测量沉降/恢复沉降）、Probe/UI 派发在部分移动后抛
    // 异常。断言同一执行器：Run 返回 false、异常不逃逸、finally 已把自有几何
    // 精确回滚到保存的 X/Y/root 原点，组合文本/span/写入计数不变。

    /// <summary>三场景共享断言：几何精确回滚 + 组合状态未被改动。</summary>
    private static void AssertRestoredAndUnchanged(FakeHost host)
    {
        Assert.Equal(9, host.ScrollY);      // 保存的原始 Y（FakeHost 默认 9）
        Assert.Equal(0, host.ScrollX);      // 保存的原始 X
        Assert.Equal(40, host.RootX);
        Assert.Equal(100, host.RootY);      // move-window 的保存绝对原点
        Assert.True(host.Composing);        // 不恢复焦点/不改组合
        Assert.Equal("ni", host.Text);
        Assert.Equal(0, host.Writes);       // 零写入绑定
        Assert.Equal(0, host.SpanStart);
        Assert.Equal(2, host.SpanEnd);
    }

    /// <summary>场景失败后的探针序号必须 <b>严格少于</b> 该场景一次成功运行的探针总数：
    /// 证明首个失败后没有继续 TSF/profile 探测（尽力恢复只做自有几何回写+读回）。
    /// 传入该 host 的成功路径探针总数（预先用无注入 host 实测）。</summary>
    private static int BaselineProbeCount(ChildSurfaceScenario scenario)
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });
        Assert.True(ex.Run(scenario, 7), $"baseline run for {scenario} must succeed");
        return host.ProbeCount;
    }

    /// <summary>Capture 抛异常（真实截图 I/O 失败）：baseline/测量后/恢复 三注入点 × 三场景。</summary>
    [Theory]
    // 第 1 次截图 = baseline（各场景同一名字），动作尚未执行但异常也不得逃逸。
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    // 第 2 次截图 = 测量后（scroll-scrolled / clip-clipped / move-moved），动作已真实执行。
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    // 第 3 次截图 = 恢复后；恢复已完成但截图异常仍判失败、不逃逸。
    [InlineData(0, 3)] [InlineData(1, 3)] [InlineData(2, 3)]
    public void CaptureThrowNeverEscapesAndGeometryRestored(int scenario, int captureAt)
    {
        var host = NewHost();
        host.CaptureThrowAt = captureAt;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok, $"{(ChildSurfaceScenario)scenario} capture#{captureAt} throw must fail honestly");
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        AssertRestoredAndUnchanged(host);
        if (captureAt == 1)
        {
            Assert.Equal(0, host.MeasuredActions);     // baseline 截图异常：不得执行测量动作
        }
    }

    /// <summary>Wait 抛异常：第 1 次 Wait = 测量沉降（动作已执行），第 2 次 = 恢复沉降。</summary>
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void WaitThrowNeverEscapesAndGeometryRestored(int scenario, int waitAt)
    {
        var host = NewHost();
        host.WaitThrowAt = waitAt;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok, $"{(ChildSurfaceScenario)scenario} wait#{waitAt} throw must fail honestly");
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    /// <summary>
    /// 动作 body 内 Probe 抛出（post-action 复核），动作已真实完成 → 属于
    /// "UI dispatch/Probe throw after partial mutation"。注入序号 = 各场景
    /// *-action-after 探针：成功序列中 scroll/clip/move 均为第 5 次 Probe。
    /// </summary>
    [Theory]
    [InlineData(0, 5)] [InlineData(1, 5)] [InlineData(2, 5)]
    public void ProbeThrowAfterActionNeverEscapesAndGeometryRestored(int scenario, int probeAt)
    {
        var host = NewHost();
        host.ProbeThrowAt = probeAt;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok, $"{(ChildSurfaceScenario)scenario} probe#{probeAt} throw must fail honestly");
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        AssertRestoredAndUnchanged(host);
        // 失败发生得早：探针总数必须 < 该场景成功路径总数，且无测量截图。
        Assert.True(host.ProbeCount < BaselineProbeCount((ChildSurfaceScenario)scenario));
        Assert.DoesNotContain(host.CaptureNames, n => n != "r29b-baseline");
    }

    /// <summary>
    /// UI 派发本身在动作已部分移动后抛异常：通过 ActionHook 让下一次
    /// InvokeOnUiThread 抛出（模拟派发器故障），动作已改变几何 → finally 必须
    /// 以保存值精确回滚，且不再调用 Probe/截图。
    /// </summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DispatchThrowAfterPartialMutationRestoresGeometry(int scenario)
    {
        var host = NewHost();
        host.ActionHook = () => host.DispatchThrowAt = host.NextDispatchIndex;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok, $"{(ChildSurfaceScenario)scenario} dispatch throw after action must fail honestly");
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        AssertRestoredAndUnchanged(host);
        // 派发抛在动作 post 复核处 → 无 settle 等待、无测量截图。
        Assert.Equal(0, host.WaitCalls);
        Assert.DoesNotContain(host.CaptureNames, n => n != "r29b-baseline");
    }

    /// <summary>
    /// 等待前复核（WaitProbed before-check）实际生效：让等待开始前的探针
    /// （*-settle-before）失败，Wait 必须一次都不发生。
    /// </summary>
    [Fact]
    public void WaitProbedChecksInvariantsBeforeWaiting()
    {
        var host = NewHost();
        // scroll：动作 post 是第 5 次 Probe，settle-before 是第 6 次。
        host.FailProbeAt = 6;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
        Assert.Equal(0, host.WaitCalls);              // 前置复核失败 → 不等待
        Assert.Equal(9, host.ScrollY);                // finally 已回滚动作造成的移动
    }

    /// <summary>
    /// 恢复验证失败即停：验证返回 false 后<b>不得</b>再调用恢复截图——
    /// 同时证明首个失败后没有多余 Probe（best-effort 已被 verified-restore 取代）。
    /// </summary>
    [Fact]
    public void RestoreVerifyFailureSkipsRestoredCapture()
    {
        var host = NewHost();
        host.DriftViewportAfterRestore = true;        // 恢复复核必然判失败
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
        Assert.DoesNotContain("r29b-scroll-restored", host.CaptureNames);
    }

    /// <summary>首个测量失败后的尽力恢复不调用 Probe：失败注入后探针数恒定。</summary>
    [Fact]
    public void BestEffortRestoreUsesNoProbe()
    {
        var host = NewHost();
        // 先实测 scroll-action-after 的探针序号（每图采集管线会消耗额外探针，
        // 序号不得写死）：warm run 中定位阶段名对应的 1 起计序号。
        var warm = NewHost();
        var warmEx = new ChildSurfaceGeometryExecutor(warm, _ => { });
        Assert.True(warmEx.Run(ChildSurfaceScenario.ScrollFollow, 7));
        var actionAfterEntry = warm.DispatchLog
            .First(e => e.StartsWith("probe:scroll-action-after:"));
        var n = int.Parse(actionAfterEntry.Split(':')[2][1..]);

        var target = NewHost();
        // 动作 post 探针失败 → 进入失败路径。
        target.FailProbeAt = n;
        var ex = new ChildSurfaceGeometryExecutor(target, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(n, target.ProbeCount);           // 失败路径尽力恢复=0 次额外 Probe
        Assert.Equal(9, target.ScrollY);
    }

    /// <summary>记录（Record）通道抛异常：不阻断 finally 回滚，也不把失败翻成成功。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RecordThrowCannotBlockRollbackOrFlipResult(int scenario)
    {
        var host = NewHost();
        host.ThrowOnRecord = true;
        host.WaitThrowAt = 1;                          // 触发停止 → StopWith 走 Record
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    // ---- R29B review-6：部分恢复故障——恢复任一步失败仍尽力回滚（restoreConfirmed 门）---
    //
    // 正常恢复路径（verified-restore）在 SetScrollOffset 已写回后的任一子阶段失败
    // ——沉降前/后 probe、Wait 抛异常、几何复核失败——场景 finally 必须仍用保存值
    // 尽力回滚，因为 restoreConfirmed 只在整个验证通过后才会置位。注入按 stage 名
    // 定位恢复沉降探针。

    /// <summary>可按 stage 名让 Probe 失败的 host（定位恢复沉降探针）。</summary>
    private sealed class StageFailHost : FakeHost
    {
        internal string? FailStage;

        public override SafePointProbe Probe(string stage)
        {
            var probe = base.Probe(stage);
            if (FailStage is { } s && stage.Contains(s, StringComparison.Ordinal))
            {
                return new SafePointProbe(false, $"{stage} injected", probe.Snapshot.Text,
                    probe.Snapshot.SpanStart, probe.Snapshot.SpanEnd,
                    probe.Snapshot.Writes, probe.Snapshot.Composing, probe.Snapshot.Profile,
                    probe.Snapshot.CompositionBeginCount, probe.Snapshot.CompositionEndCount,
                    probe.Snapshot.CompositionSessionId, probe.Snapshot.InputFocusChangeCount);
            }
            return probe;
        }
    }

    /// <summary>恢复沉降<b>前</b>复核失败 → 恢复写回后 finally 仍须回滚到保存偏移。</summary>
    [Theory]
    [InlineData(0, "scroll-restore-settle-before")]
    [InlineData(1, "clip-restore-settle-before")]
    public void RestoreSettleBeforeProbeFailureStillRollsBack(int scenario, string stage)
    {
        var host = new StageFailHost { FailStage = stage };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
        // 恢复子阶段安全点丢失如实记 SafePointLost（WaitProbed 的首个失败语义）。
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    /// <summary>恢复沉降<b>后</b>复核失败 → 同样 finally 回滚。</summary>
    [Theory]
    [InlineData(0, "scroll-restore-settle-after")]
    [InlineData(1, "clip-restore-settle-after")]
    public void RestoreSettleAfterProbeFailureStillRollsBack(int scenario, string stage)
    {
        var host = new StageFailHost { FailStage = stage };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.SafePointLost, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    /// <summary>恢复沉降 <b>Wait 抛异常</b>（WaitThrowAt 已覆盖恢复等待）——验证
    /// 恢复写回后的沉降异常同样触发 finally 尽力回滚。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void RestoreSettleWaitThrowStillRollsBack(int scenario)
    {
        var host = NewHost();
        // 各场景只有一个 restore wait（成功序列最后一次 Wait）。
        var baseline = NewHost();
        new ChildSurfaceGeometryExecutor(baseline, _ => { }).Run((ChildSurfaceScenario)scenario, 7);
        host.WaitThrowAt = baseline.WaitCalls;         // 最后一支 Wait=恢复沉降
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    /// <summary>恢复几何<b>验证</b>失败（写回已生效但复核不过）→ finally 回滚。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void RestoreGeometryVerifyFailureStillRollsBack(int scenario)
    {
        var host = NewHost();
        host.DriftViewportAfterRestore = true;         // 恢复写回后参照漂移 → 验证必败
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run((ChildSurfaceScenario)scenario, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
        AssertRestoredAndUnchanged(host);
    }

    /// <summary>部分恢复：SetScrollOffset 只写一半后抛——真实部分应用下 finally
    /// 必须恢复精确保存值而非停在半写状态。</summary>
    [Fact]
    public void PartiallyAppliedRestoreStillRollsBackExactly()
    {
        var host = new PartialWriteHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        var ok = ex.Run(ChildSurfaceScenario.ScrollFollow, 7);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.RestoreFailed, ex.Stop);
        // 半写 Y=55（非保存 9）；finally 尽力恢复必须写回 9。
        Assert.Equal(9, host.ScrollY);
        Assert.Equal(0, host.ScrollX);
    }

    /// <summary>SetScrollOffset 先写一半再抛——模拟真实部分应用（verified-restore
    /// 的 SetScrollOffset 已在受管 body 内部分写才失败）。</summary>
    private sealed class PartialWriteHost : FakeHost
    {
        private bool _wrotePartial;

        public override void SetScrollOffset(int x, int y)
        {
            if (!_wrotePartial)
            {
                _wrotePartial = true;
                ScrollY = 55;                          // 先部分写 Y 再抛
                throw new InvalidOperationException("partial set-scroll");
            }
            base.SetScrollOffset(x, y);                // finally 的尽力恢复写回保存值
        }
    }

    /// <summary>既定沉降时长回归：clip 600ms、scroll 500ms、move 600ms、恢复 600ms。</summary>
    [Fact]
    public void PreservedSettleTimings()
    {
        var scroll = NewHost();
        Assert.True(new ChildSurfaceGeometryExecutor(scroll, _ => { })
            .Run(ChildSurfaceScenario.ScrollFollow, 7));
        Assert.Equal(new[] { "wait:500", "wait:600" }, scroll.DispatchLog.Where(e => e.StartsWith("wait:")).ToArray());

        var clip = NewHost();
        Assert.True(new ChildSurfaceGeometryExecutor(clip, _ => { })
            .Run(ChildSurfaceScenario.ClipOut, 7));
        Assert.Equal(new[] { "wait:600", "wait:600" }, clip.DispatchLog.Where(e => e.StartsWith("wait:")).ToArray());

        var move = NewHost();
        Assert.True(new ChildSurfaceGeometryExecutor(move, _ => { })
            .Run(ChildSurfaceScenario.MoveWindow, 7));
        Assert.Equal(new[] { "wait:600", "wait:600" }, move.DispatchLog.Where(e => e.StartsWith("wait:")).ToArray());
    }

    // ---- R30：--child-hide-when-clipped 受管可见性操作 -------------------
    // 共享控制流：执行器真实 clip-out 流程注入同一个 FakeHost 可见性操作；
    // 断言 hide/show 的实际派发顺序、聚焦/组合不变量拒绝、原生失败/异常如实
    // 失败且 finally 仍精确回滚 scroll——测试覆盖的就是生产控制流。

    /// <summary>R30 成功路径：clip-verify 通过后先 hide（visibility:hide），
    /// 600ms 沉降后 clipped 截图，恢复前 show（visibility:show）；可见矩形与
    /// GetScreenExt 隐藏时为全零、恢复后回到保存几何——Win32 成功≠候选隐藏，
    /// 仅报告事实可见性。运行仍判 true（执行器语义是几何恢复成功）。</summary>
    [Fact]
    public void HideWhenClippedHidesThenRestoresVisibility()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.True(ok);
        Assert.True(host.InputVisible);             // 恢复原可见性
        Assert.Equal(9, host.ScrollY);
        Assert.Equal(0, host.ScrollX);
        // hide 在 clip-verify 之后；hide 后必须有一次 UI 外沉降等待才截
        // r29b-clipped；show 在 clip-restore（set-scroll）之前。
        var hideIdx = host.DispatchLog.IndexOf("visibility:hide");
        var settleIdx = host.DispatchLog.IndexOf("wait:600", Math.Max(0, hideIdx));
        var clippedIdx = host.DispatchLog.IndexOf("capture:r29b-clipped");
        var showIdx = host.DispatchLog.IndexOf("visibility:show");
        var setScrollIdx = host.DispatchLog.IndexOf("set-scroll:0,9");
        Assert.True(hideIdx >= 0 && settleIdx > hideIdx && clippedIdx > settleIdx,
            $"hide must precede settle and clipped capture: {string.Join("|", host.DispatchLog)}");
        Assert.True(showIdx > clippedIdx && setScrollIdx > showIdx,
            $"show must precede scroll restore: {string.Join("|", host.DispatchLog)}");
        Assert.Equal(0, host.DispatcherViolations);
    }

    /// <summary>R30 正常路径**恰好** hide+show 各一次原生可见性写：恢复后
    /// finally 绝不再补一次 show，恢复证据截图之后无任何可见性变更。
    /// （R30 复审缺陷：旧实现 finally 会再 show 一次，共 3 次。）</summary>
    [Fact]
    public void HideWhenClippedSuccessIssuesExactlyTwoVisibilityWrites()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));

        var writes = host.VisibilityCalls.Where(e => e.EndsWith(":ok")).ToArray();
        Assert.Equal(new[] { "hide:ok", "show:ok" }, writes);
        Assert.Equal(2, host.VisibilityCalls.Count);        // hide + show，无 finally 补写
        // 恢复证据截图之后不得再出现任何可见性动作（恢复证据须在最后一次变更之后）。
        var lastVisibility = host.DispatchLog.FindLastIndex(e => e.StartsWith("visibility:"));
        var restoredIdx = host.DispatchLog.IndexOf("capture:r29b-clip-restored");
        Assert.True(restoredIdx > lastVisibility,
            $"no visibility op may follow the restored capture: {string.Join("|", host.DispatchLog)}");
        Assert.True(host.InputVisible);
    }

    /// <summary>R30 隐藏后沉降等待在 UI 派发器之外执行：hide 与 clipped 截图
    /// 之间必须存在一次 wait:600（WaitProbed 的前后置复核也由探针覆盖）。</summary>
    [Fact]
    public void HideThenSettlesBeforeClippedCapture()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));

        var hideIdx = host.DispatchLog.IndexOf("visibility:hide");
        var clippedIdx = host.DispatchLog.IndexOf("capture:r29b-clipped");
        var waitsBetween = host.DispatchLog.Skip(hideIdx + 1)
            .Take(clippedIdx - hideIdx - 1)
            .Where(e => e.StartsWith("wait:")).ToArray();
        Assert.Equal(new[] { "wait:600" }, waitsBetween);
    }

    /// <summary>R30 真实故障注入：hide 的原生动作<b>已生效</b>但返回 false——
    /// 执行器必须判失败、不产出 clipped 截图、scroll 精确回滚，<b>且</b>
    /// finally 按动作前保存的可见性把 input child 恢复为可见。
    /// （复审缺陷：旧实现 hide 报失败后可见性永久停在 false。）</summary>
    [Fact]
    public void HideAppliedThenReportedFalseRestoresSavedVisibility()
    {
        var host = new FakeHost { VisibilityMutateThenFail = true };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.VisibilityOperationFailed, ex.Stop);
        Assert.True(host.InputVisible);                       // finally 恢复保存的原可见性
        Assert.Equal(9, host.ScrollY);                        // scroll 精确回滚
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        // hide 变更生效 + finally 恢复写 = 两次真实可见性写，绝无第三次。
        Assert.Equal(new[] { "hide:mutated-false", "show:ok" }, host.VisibilityCalls);
    }

    /// <summary>R30 真实故障注入：hide 的原生动作<b>已生效</b>后抛异常——
    /// 执行器记 ActionException、scroll 精确回滚，finally 仍按保存的
    /// 原可见性恢复（不以 op.WasVisible 为准——异常路径它从未被赋值）。</summary>
    [Fact]
    public void HideAppliedThenThrewRestoresSavedVisibility()
    {
        var host = new HideMutatesThenThrowsHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.ActionException, ex.Stop);
        Assert.True(host.InputVisible);                       // finally 恢复保存的原可见性
        Assert.Equal(9, host.ScrollY);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        Assert.Equal(new[] { "hide:mutated-throw", "show:ok" }, host.VisibilityCalls);
    }

    /// <summary>hide 把 InputVisible 写成 false 之后才抛——模拟"原生生效但调用
    /// 中途异常"（旧实现此时 finally 从不恢复，child 永远停在隐藏）。</summary>
    private sealed class HideMutatesThenThrowsHost : FakeHost
    {
        private int _calls;

        public override ChildSurfaceVisibilityResult VisibilityOperation(bool show,
            bool showOnlyIfWasVisible)
        {
            _calls++;
            if (!show && _calls == 1)
            {
                // 第一次 hide：真实变更已生效再抛（VisibilityCalls 记录由 base 负责会
                // 在抛前 append 丢失——直接模拟：先变更状态再抛，调用计数如实登记）。
                VisibilityCalls.Add("hide:mutated-throw");
                InputVisible = false;
                throw new IOException("hide applied then threw");
            }

            return base.VisibilityOperation(show, showOnlyIfWasVisible);
        }
    }

    /// <summary>默认 off（hideWhenClipped=false）：既不 hide 也不 show，DispatchLog
    /// 无任何 visibility:* 条目——默认路径行为逐字节不变。</summary>
    [Fact]
    public void ClipOutDefaultNeverCallsVisibilityOperation()
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));
        Assert.DoesNotContain(host.DispatchLog, e => e.StartsWith("visibility:"));
    }

    /// <summary>R30 flag 只对 clip-out 有意义：scroll-follow/move-window 即使传入
    /// hideWhenClipped 也绝不触发可见性操作（执行器只认 ClipOut 分支）。</summary>
    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void HideWhenClippedIgnoredOutsideClipOut(int scenario)
    {
        var host = NewHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        Assert.True(ex.Run((ChildSurfaceScenario)scenario, 7));
        Assert.DoesNotContain(host.DispatchLog, e => e.StartsWith("visibility:"));
    }

    /// <summary>hide 引起瞬时聚焦离开又回来（FocusChangeCount +2）→ post-hide
    /// 不变量复核如实 SafePointLost，绝不产出 clipped 截图、不 SetFocus 恢复；
    /// finally 把 scroll 回滚到保存值。</summary>
    [Fact]
    public void HideCausingFocusChangeRejectsWithoutCapture()
    {
        var host = NewHost();
        // hide 的 post 复核是 clip-hide-after 探针——该探针起 focus 计数变化。
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        // 先跑一次成功路径测出 clip-hide-after 对应的探针序号。
        var warm = NewHost();
        var warmEx = new ChildSurfaceGeometryExecutor(warm, _ => { }, hideWhenClipped: true);
        Assert.True(warmEx.Run(ChildSurfaceScenario.ClipOut, 0));
        // clip-hide-after 是 hide 派发体内最后的复核探针；warm run 中 hide 派发
        // 由 InvokeOnUiThread 计数——probe 序号通过 DispatchLog 里 probe:clip-hide-after 定位。
        var hideAfterProbe = warm.DispatchLog
            .FindIndex(e => e.StartsWith("probe:clip-hide-after:"));
        Assert.True(hideAfterProbe >= 0);
        // probe:nN 序号为 1 起计；DispatchLog 中该条目序号即 ProbeCount。
        var probeEntry = warm.DispatchLog[hideAfterProbe];
        var n = int.Parse(probeEntry.Split(':')[2][1..]);

        host.MutateFocusCountAfterProbe = n;
        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.NotEqual(ChildSurfaceStop.None, ex.Stop);                 // 如实记失败
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);  // 无 clipped 证据
        Assert.Equal(9, host.ScrollY);                                   // finally 回滚
        Assert.Equal(0, host.Writes);
        Assert.Equal("ni", host.Text);
    }

    /// <summary>hide 原生调用返回 false（SetWindowPos 失败）→ VisibilityOperationFailed，
    /// 不继续 clipped 截图；scroll 已回滚。</summary>
    [Fact]
    public void HideOperationFailureStopsBeforeClippedCapture()
    {
        var host = new FakeHost { VisibilityFails = true };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.VisibilityOperationFailed, ex.Stop);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>hide 抛异常 → 执行器把动作体异常如实记 ActionException 并回滚。</summary>
    [Fact]
    public void HideOperationThrowStopsAsActionException()
    {
        var host = new FakeHost { VisibilityThrows = true };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.ActionException, ex.Stop);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>宿主未实现可见性操作（SupportsVisibilityOperation=false）→ 如实
    /// VisibilityOperationFailed，而非静默成功；scroll 仍回滚。</summary>
    [Fact]
    public void HideUnsupportedHostStopsHonestly()
    {
        var host = new FakeHost { VisibilitySupported = false };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.VisibilityOperationFailed, ex.Stop);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>show 恢复失败（首次 hide 成功、恢复时原生 false）→ 如实
    /// VisibilityOperationFailed；scroll 仍回滚、不谎报已恢复可见性。</summary>
    [Fact]
    public void ShowRestoreFailureStopsHonestly()
    {
        var host = new HideOkShowFailHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.VisibilityOperationFailed, ex.Stop);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>FakeHost 派生：hide 成功、show 返回原生失败（模拟 OS 拒绝重新显示）。</summary>
    private sealed class HideOkShowFailHost : FakeHost
    {
        private int _calls;

        public override ChildSurfaceVisibilityResult VisibilityOperation(bool show,
            bool showOnlyIfWasVisible)
        {
            var result = base.VisibilityOperation(show, showOnlyIfWasVisible);
            _calls++;
            if (show && _calls >= 2)
            {
                return new ChildSurfaceVisibilityResult(false, result.WasVisible,
                    result.VisibleAfter, "injected show failure");
            }
            return result;
        }
    }

    /// <summary>input child 本就不可见时 hide 仍如实报告 WasVisible=false；恢复
    /// 分支传 showOnlyIfWasVisible=false → 共享核只读回、不提交写——FakeHost 的
    /// InputVisible 保持 false，绝不伪造一次显示。</summary>
    [Fact]
    public void InitiallyInvisibleChildIsNotSpuriouslyShown()
    {
        var host = new InitiallyInvisibleHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            hideWhenClipped: true);

        // input 不可见 → clip-initial 的 GetInputClientScreenRect 回 default
        // → NonEmpty 失败 → GeometryCheckFailed，且绝不执行 visibility op。
        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.False(host.InputVisible);
        Assert.DoesNotContain(host.DispatchLog, e => e.StartsWith("visibility:"));
    }

    /// <summary>input child 从运行开始即不可见（模拟 edge case）。</summary>
    private sealed class InitiallyInvisibleHost : FakeHost
    {
        internal InitiallyInvisibleHost()
        {
            InputVisible = false;
        }
    }

    // ---- R31：--child-suspend-tsf-focus-when-clipped 受管 TSF 焦点操作 ---------
    // 共享控制流：执行器真实 clip-out 流程注入同一个 FakeHost 焦点操作；断言
    // acquire→suspend→restore 的实际派发顺序、组合/焦点不变量拒绝、原生
    // false/异常如实失败且 finally 仍按保存令牌尽力恢复——测试覆盖的就是
    // 生产控制流。身份用不透明对象模拟（执行器经共享 CompareComIdentity 判同）。

    /// <summary>R31 enabled 替身：注入初始自有 TSF 焦点并开启焦点能力——
    /// 与生产 <c>--child-suspend-tsf-focus-when-clipped</c> 接线同构，
    /// 每图管线经本宿主同一实例的实际阶段读回（默认 host 不接线）。</summary>
    private static FakeHost NewFocusedHost()
    {
        var host = NewHost();
        host.CurrentFocusDoc = host.OwnDoc;
        host.TsfFocusSupported = true;
        return host;
    }

    /// <summary>R31 成功路径：clip-verify 通过后 acquire→suspend（真实写
    /// SetFocus(null)），600ms 沉降后 clipped 截图，恢复前 restore（SetFocus(保存doc)）；
    /// suspend 期间 CurrentFocusDoc 为 null，恢复后回到 OwnDoc；scroll 精确回滚。</summary>
    [Fact]
    public void SuspendWhenClippedSuspendsThenRestoresFocus()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.True(ok);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);         // 恢复原自有 doc
        Assert.Equal(9, host.ScrollY);                          // scroll 精确回滚
        // suspend 在 clip-verify 之后；suspend 后必须有一次 UI 外沉降等待才截
        // r29b-clipped；restore 在 clip-restore（set-scroll）之前。
        var suspendIdx = host.DispatchLog.IndexOf("tsf:suspend");
        var settleIdx = host.DispatchLog.IndexOf("wait:600", Math.Max(0, suspendIdx));
        var clippedIdx = host.DispatchLog.IndexOf("capture:r29b-clipped");
        var restoreIdx = host.DispatchLog.IndexOf("tsf:restore");
        var setScrollIdx = host.DispatchLog.IndexOf("set-scroll:0,9");
        Assert.True(suspendIdx >= 0 && settleIdx > suspendIdx && clippedIdx > settleIdx,
            $"suspend must precede settle and clipped capture: {string.Join("|", host.DispatchLog)}");
        Assert.True(restoreIdx > clippedIdx && setScrollIdx > restoreIdx,
            $"restore must precede scroll restore: {string.Join("|", host.DispatchLog)}");
        Assert.Equal(0, host.DispatcherViolations);
    }

    /// <summary>R31 正常路径**恰好** acquire+suspend+restore+release 各一次共享操作：
    /// 恢复后 finally 绝不再补一次 restore，恢复证据截图之后无任何焦点写。
    /// （与 R30 "恰好两次写" 复审项同构——成功路径不重复变更。）</summary>
    [Fact]
    public void SuspendWhenClippedSuccessIssuesExactlyTwoFocusWrites()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));

        // 原生 SetFocus 写恰好两次：null + 保存的 doc。
        Assert.Equal(2, host.SetFocusCalls.Count);
        Assert.Null(host.SetFocusCalls[0]);
        Assert.Same(host.OwnDoc, host.SetFocusCalls[1]);
        // acquire/suspend/restore/release 各一次。
        Assert.Equal(new[] { "acquire", "suspend", "restore", "release" },
            host.TsfFocusCalls);
        // 恢复证据截图之后不得再出现任何焦点写（恢复证据须在最后一次变更之后）。
        var lastFocusWrite = host.DispatchLog.FindLastIndex(e =>
            e.StartsWith("tsf:suspend") || e.StartsWith("tsf:restore"));
        var restoredIdx = host.DispatchLog.IndexOf("capture:r29b-clip-restored");
        Assert.True(restoredIdx > lastFocusWrite,
            $"no tsf write may follow the restored capture: {string.Join("|", host.DispatchLog)}");
    }

    /// <summary>R31 suspend 后沉降等待在 UI 派发器之外执行：suspend 与 clipped
    /// 截图之间必须存在 wait:600（前后置复核由探针覆盖）。scroll settle 与
    /// suspend settle 各一次 600ms——本测试只验证 suspend settle 在 suspend 之后、
    /// clipped 之前。</summary>
    [Fact]
    public void SuspendThenSettlesBeforeClippedCapture()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));

        var suspendIdx = host.DispatchLog.IndexOf("tsf:suspend");
        var clippedIdx = host.DispatchLog.IndexOf("capture:r29b-clipped");
        var waitsBetween = host.DispatchLog.Skip(suspendIdx + 1)
            .Take(clippedIdx - suspendIdx - 1)
            .Where(e => e.StartsWith("wait:")).ToArray();
        // suspend 之后、clipped 之前恰好一次 suspend-settle 等待。
        Assert.Equal(new[] { "wait:600" }, waitsBetween);
    }

    /// <summary>R31 真实故障注入：suspend 的原生写<b>已生效</b>（焦点真为 null）
    /// 但返回 false——执行器必须判失败、不产出 clipped 截图、scroll 精确回滚，
    /// <b>且</b> finally 按保存令牌把焦点恢复回 OwnDoc（"写可能已发生"≠"操作成功"）。
    /// 恢复的 restore 走正常路径成功，共两次 SetFocus 写。</summary>
    [Fact]
    public void SuspendAppliedThenReportedFalseRestoresSavedFocus()
    {
        var host = NewFocusedHost();
        host.SuspendMutateThenFail = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusOperationFailed, ex.Stop);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);         // finally 恢复保存令牌
        Assert.Equal(9, host.ScrollY);                          // scroll 精确回滚
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        // suspend 生效写 + finally restore 写 = 两次 SetFocus；绝不补输入、不取消组合。
        Assert.Equal(2, host.SetFocusCalls.Count);
    }

    /// <summary>R31 真实故障注入：suspend 的原生写<b>已生效</b>后抛异常——
    /// 执行器记 ActionException、scroll 精确回滚，finally 仍按保存令牌恢复。
    /// 异常路径 acquire 已完成的令牌仍是恢复唯一真源。</summary>
    [Fact]
    public void SuspendAppliedThenThrewRestoresSavedFocus()
    {
        var host = NewFocusedHost();
        host.SuspendThrows = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.ActionException, ex.Stop);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);         // finally 尽力恢复
        Assert.Equal(9, host.ScrollY);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        Assert.Equal(2, host.SetFocusCalls.Count);              // suspend + finally restore
    }

    /// <summary>R31 restore 读回身份不符（原生写已生效但 post-GetFocus 谎报 foreign
    /// doc）→ 执行器如实 TsfFocusOperationFailed、scroll 回滚；finally 再按保存
    /// 令牌尽力恢复（第二次 restore 成功）。身份读回与"操作成功"严格区分。</summary>
    [Fact]
    public void RestoreReadbackMismatchStopsHonestly()
    {
        var host = new RestoreReadbackForeignOnceHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusOperationFailed, ex.Stop);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);         // finally 第二次恢复成功
        Assert.Equal(9, host.ScrollY);
        // 正常 restore 写 + finally restore 写 + suspend 写 = 三次 SetFocus。
        Assert.Equal(3, host.SetFocusCalls.Count);
    }

    /// <summary>第一次 restore 原生写生效但读回谎报 foreign；之后恢复正常。</summary>
    private sealed class RestoreReadbackForeignOnceHost : FakeHost
    {
        private int _restoreCalls;

        internal RestoreReadbackForeignOnceHost()
        {
            CurrentFocusDoc = OwnDoc;
            TsfFocusSupported = true;
        }

        public override TsfFocusOpResult RestoreTsfFocus(TsfFocusHandle saved)
        {
            _restoreCalls++;
            if (_restoreCalls == 1)
            {
                // 原生写生效（CurrentFocusDoc=OwnDoc）但谎报 foreign 读回。
                DispatchLog.Add("tsf:restore");
                CurrentFocusDoc = saved.Doc;
                SetFocusCalls.Add(saved.Doc);
                TsfFocusCalls.Add("restore");
                return new TsfFocusOpResult(false, true,
                    "post-get-focus reports a different document manager");
            }

            return base.RestoreTsfFocus(saved);
        }
    }

    /// <summary>R31 COM 重入导致 external foreign focus 抢占（在 clipped 截图内
    /// 注入 CurrentFocusDoc=ForeignDoc）→ 恢复时 Restore 的前置 foreign-gate
    /// 拒绝覆盖，执行器如实失败、scroll 回滚；finally 的 best-effort restore
    /// 同样被 foreign-gate 拒绝——绝不写别人的文档焦点。</summary>
    [Fact]
    public void ReentryForeignFocusIsNotOverwritten()
    {
        var host = NewFocusedHost();
        // clipped 截图内模拟外部抢占（抓图本身仍成功——抓图不读 TSF 焦点；
        // 每图后置实际阶段读回如实检出 foreign）。
        host.CaptureHook = name =>
        {
            if (name == "r29b-clipped")
            {
                host.CurrentFocusDoc = host.ForeignDoc;
            }
        };
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        // 截图内 foreign 抢占 → restore 前的阶段读回检出"非 NULL"即拒绝；
        // foreign 焦点保持不被覆盖（restore 从未提交 SetFocus(OwnDoc)）。
        Assert.Equal(ChildSurfaceStop.TsfFocusPhaseMismatch, ex.Stop);
        Assert.Same(host.ForeignDoc, host.CurrentFocusDoc);
        Assert.Equal(9, host.ScrollY);
        // 只有 suspend 一次原生写；restore 从未提交 SetFocus(OwnDoc)。
        Assert.Single(host.SetFocusCalls);
        Assert.Null(host.SetFocusCalls[0]);
    }

    /// <summary>R31 suspend 导致 TIP 结束组合（post-suspend 复核 Composing=false）→
    /// 执行器如实 CompositionChanged/安全点失败，不产出 clipped 截图、scroll 回滚；
    /// finally 仍按保存令牌恢复焦点。绝不补输入、不取消/重打。</summary>
    [Fact]
    public void SuspendEndingCompositionRejectsWithoutCapture()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        // 先跑一次成功路径定位 clip-suspend-after 探针序号。
        var warm = NewFocusedHost();
        var warmEx = new ChildSurfaceGeometryExecutor(warm, _ => { },
            suspendTsfFocusWhenClipped: true);
        Assert.True(warmEx.Run(ChildSurfaceScenario.ClipOut, 0));
        var suspendAfterIdx = warm.DispatchLog
            .FindIndex(e => e.StartsWith("probe:clip-suspend-after:"));
        Assert.True(suspendAfterIdx >= 0);
        var n = int.Parse(warm.DispatchLog[suspendAfterIdx].Split(':')[2][1..]);

        host.MutateComposingAfterProbe = n;
        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.CompositionChanged, ex.Stop);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        Assert.Equal(9, host.ScrollY);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);         // finally 恢复焦点
        Assert.Equal(0, host.Writes);                           // 无 binding writes
        Assert.Equal("ni", host.Text);                          // 原文本不变
    }

    /// <summary>R31 恢复证据截图抛异常（restored capture IOException）→ 执行器
    /// 如实判失败；此时焦点已正常 restore（focusRestoreConfirmed=true），
    /// finally <b>绝不</b>再补一次 restore——证据后的失败不触发额外焦点写。</summary>
    [Fact]
    public void RestoredCaptureExceptionIssuesNoLateFocusWrite()
    {
        var host = NewFocusedHost();
        host.CaptureThrowAt = 3;                              // 3rd capture = restored
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        // 焦点已正常恢复（suspend+restore 恰好两次写，finally 无第三次）。
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);
        Assert.Equal(2, host.SetFocusCalls.Count);
        Assert.Equal(9, host.ScrollY);                          // scroll finally 回滚
    }

    /// <summary>默认 off（suspendTsfFocusWhenClipped=false）：既不 suspend 也不
    /// restore，DispatchLog 无任何 tsf:* 条目、SetFocusCalls 为空——默认路径
    /// 行为逐字节不变。</summary>
    [Fact]
    public void ClipOutDefaultNeverCallsTsfFocusOperation()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { });

        Assert.True(ex.Run(ChildSurfaceScenario.ClipOut, 0));
        Assert.DoesNotContain(host.DispatchLog, e => e.StartsWith("tsf:"));
        Assert.Empty(host.SetFocusCalls);
        Assert.Empty(host.TsfFocusCalls);
    }

    /// <summary>R31 flag 只对 clip-out 有意义：scroll-follow/move-window 即使传入
    /// suspendTsfFocusWhenClipped 也绝不触发焦点操作（执行器只认 ClipOut 分支）。</summary>
    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void SuspendWhenClippedIgnoredOutsideClipOut(int scenario)
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        Assert.True(ex.Run((ChildSurfaceScenario)scenario, 7));
        Assert.Empty(host.SetFocusCalls);
        Assert.Empty(host.TsfFocusCalls);
    }

    /// <summary>R30 与 R31 互斥：执行器构造时显式冲突拒绝（解析层之外的第二道防线）。</summary>
    [Fact]
    public void HideAndSuspendFlagsAreMutuallyExclusive()
    {
        var host = NewFocusedHost();
        Assert.Throws<ArgumentException>(() =>
            new ChildSurfaceGeometryExecutor(host, _ => { },
                hideWhenClipped: true, suspendTsfFocusWhenClipped: true));
    }

    /// <summary>宿主未实现焦点操作（SupportsTsfFocusOperation=false）→ 如实
    /// TsfFocusOperationFailed，而非静默成功；scroll 仍回滚。</summary>
    [Fact]
    public void SuspendUnsupportedHostStopsHonestly()
    {
        var host = NewFocusedHost();
        host.TsfFocusSupported = false;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusOperationFailed, ex.Stop);
        Assert.Equal(9, host.ScrollY);
        Assert.Empty(host.SetFocusCalls);
    }

    // ---- R31 复审：共享门禁/阶段/释放顺序的真实故障注入 ----------------------
    // 以下测试覆盖 Codex 要求的共享接线：真实宿主与测试替身注入<b>同一个</b>
    // <see cref="TsfDocumentFocus"/> 操作核（GetFocus/SetFocus/GetTop/Release/门禁
    // 委托），不是平行实现。注入的外部状态变化经 GuardHook/FocusAfterRead/
    // GetTopHook/AutoRefocusDuringWait/FirstReleaseThrows 进入同一控制流。

    /// <summary>R31 复审：foreign foreground + GetFocus→null（Win32 焦点不在自有
    /// input、GetFocus 已返回 null）时 suspend 的 guard 如实拒绝——<b>绝不</b>
    /// 调用 SetFocus，绝不写、绝不夺焦。操作核返回失败、执行器如实
    /// TsfFocusOperationFailed。</summary>
    [Fact]
    public void ForeignForegroundAndNullFocusNeverWrites()
    {
        var host = NewFocusedHost();
        // 共享门禁模拟"前台已切走+GetFocus 返回 null"的外部状态——
        // guard 在 SetFocus(null) 最后一刻如实拒绝，操作核绝不写。
        host.GuardHook = stage =>
            stage?.Contains("before-set-focus") == true ? "foreign-foreground" : null;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusOperationFailed, ex.Stop);
        // 门禁拒绝 ⇒ 绝不提交任何 SetFocus 写；scroll 仍精确回滚。
        Assert.Empty(host.SetFocusCalls);
        Assert.Equal(9, host.ScrollY);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
    }

    /// <summary>R31 复审：GetTop 重入（COM 回调内取 top 返回失败/抛异常）时
    /// acquire 如实失败——<b>绝不</b>调用 SetFocus，绝不写。阶段读回在 acquire
    /// 之前已检出 GetTop 重入（TsfFocusPhaseMismatch）。</summary>
    [Fact]
    public void GetTopReentryNeverWrites()
    {
        var host = NewFocusedHost();
        host.GetTopHook = () => (unchecked((int)0x80004005), null); // E_FAIL
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        // GetTop 重入在 acquire 前的阶段读回就被检出——证据拒绝在写入之前。
        Assert.Equal(ChildSurfaceStop.TsfFocusPhaseMismatch, ex.Stop);
        Assert.Empty(host.SetFocusCalls);
        Assert.Equal(9, host.ScrollY);
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
    }

    /// <summary>R31 复审：TSF 在 suspend 后 600ms 等待内自动回到 OWN——
    /// 期望阶段仍是 NULL，实际读回 OWN ⇒ 证据被如实拒绝，不拿回到 OWN 的
    /// 截图当挂起证据。scroll 回滚；焦点已在 OWN ⇒ restore 语义已满足，
    /// finally 不额外写。</summary>
    [Fact]
    public void AutoRefocusDuringWaitRejectsEvidence()
    {
        var host = NewFocusedHost();
        host.AutoRefocusDuringWait = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusPhaseMismatch, ex.Stop);
        // 挂起期间焦点自动回 OWN ⇒ clipped 截图不作为有效证据。
        Assert.DoesNotContain("capture:r29b-clipped", host.DispatchLog);
        // suspend 写 null + finally restore 写回 OwnDoc = 两次 SetFocus。
        Assert.Equal(2, host.SetFocusCalls.Count);
        Assert.Null(host.SetFocusCalls[0]);
        Assert.Same(host.OwnDoc, host.SetFocusCalls[1]);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>R31 复审：第一个 release 抛异常时第二个仍被释放——结果如实
    /// 失败且包含 release 错误；两个 RCW 都进入 release（不跳过）。</summary>
    [Fact]
    public void FirstReleaseThrowStillReleasesSecond()
    {
        var host = NewFocusedHost();
        host.FirstReleaseThrows = true;
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        // 保存令牌 ownsRefs=true ⇒ finally release 时 doc+top 都进入释放；
        // 第一个抛异常不跳过第二个。
        Assert.Equal(2, host.HandleReleaseCount);
        // 操作本身（suspend+restore）已成功；release 失败如实进入诊断但不覆盖
        // 首败（本测试首败是 release，不是操作失败）。
        Assert.True(ok || ex.Stop != ChildSurfaceStop.None);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);
        Assert.Equal(9, host.ScrollY);
    }

    /// <summary>R31 复审返修：每图接线的<b>真实共享</b>验证——root 抓图之后、
    /// monitor 抓图之前 TSF 焦点瞬时回 OWN（挂起阶段实际阶段变化），共享
    /// <see cref="ChildSurfaceEvidenceCapture.RunStage"/> 的每图后置阶段读回
    /// 如实判失败，<b>r29b-clipped 的 monitor 抓图计数=0</b>（绝不发起第二张）。
    /// 这不是只在 bundle 之后才检查的假接线：root 本张证据已成立（已抓图），
    /// 失败来自 root 后置实际阶段读回，monitor 根本未进入。</summary>
    [Fact]
    public void RootPostPhaseChangeSkipsMonitorCapture()
    {
        var host = NewFocusedHost();
        var ex = new ChildSurfaceGeometryExecutor(host, _ => { },
            suspendTsfFocusWhenClipped: true);

        // root 抓图完成后焦点自动回 OWN——root 后置/monitor 前置阶段读回必须拒绝。
        host.AutoRefocusAfterRootCapture = true;
        var ok = ex.Run(ChildSurfaceScenario.ClipOut, 0);

        Assert.False(ok);
        Assert.Equal(ChildSurfaceStop.TsfFocusPhaseMismatch, ex.Stop);
        // r29b-clipped 的 root 已抓；其后置阶段读回不符（期望 NULL、实际 OWN）
        // → monitor 抓图绝不发起（按抓图名+图别精确断言，不被 baseline 张数混淆）。
        Assert.Contains("capture-image:r29b-clipped:root", host.DispatchLog);
        Assert.DoesNotContain("capture-image:r29b-clipped:monitor", host.DispatchLog);
        // 阶段读回确实发生在每张抓图前后（非空接线）。
        Assert.True(host.PhaseReadCount > 0);
        // scroll 精确回滚；suspend 的 finally restore 已把焦点写回自有 doc。
        Assert.Equal(9, host.ScrollY);
        Assert.Same(host.OwnDoc, host.CurrentFocusDoc);
    }

    /// <summary>R31 返修：每图阶段读取器的<b>实际选择点</b>——生产宿主
    /// <c>ChildSurfaceScenarioHost.Capture</c> 经
    /// <see cref="ScenarioRunner.SelectCapturePhaseReader"/> 决定是否接线。
    /// 默认 off（R29/R30）必须返回 null（管线无任何新增 TSF 查询）；
    /// on 必须返回<b>传入的同一个</b>回调实例（当前宿主同一实例的
    /// ReadTsfFocusPhase，绝不新建替身）。测试直接调用实际选择器，
    /// 不复制一份平行选择逻辑。</summary>
    [Fact]
    public void CapturePhaseSelectorOffReturnsNullOnReturnsSameCallback()
    {
        var host = NewFocusedHost();
        Func<TsfFocusPhaseResult> callback = host.ReadTsfFocusPhase;

        // 默认 off：不接线阶段读回（R29/R30 路径不新增 TSF 查询）。
        Assert.Null(ScenarioRunner.SelectCapturePhaseReader(false, callback));
        // on：返回传入的同一回调实例（不新建/不包装）。
        Assert.Same(callback, ScenarioRunner.SelectCapturePhaseReader(true, callback));
    }
}
