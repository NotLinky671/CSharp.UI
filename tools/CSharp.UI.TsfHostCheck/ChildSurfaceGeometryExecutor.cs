using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R31 返修：一次完整证据截图的判定。宿主把管线内<b>首个失败阶段已捕获的原因</b>
/// （安全点/不变量/抓图/每张阶段读回）如实带出——执行器据此分类停止，绝不在
/// <c>Ok=false</c> 之后再向宿主发起任何新的 TSF/profile 复探。
/// </summary>
internal readonly record struct CaptureVerdict(bool Ok, string Reason)
{
    /// <summary>失败原因是否为实际 TSF 焦点阶段读回不符（管线已读，非推断）。</summary>
    internal bool PhaseMismatch { get; init; }

    internal static CaptureVerdict Passed() => new(true, "ok");
}

/// <summary>R29B opt-in 真实 input child 几何场景的种类（真实路径与测试共用同一枚举）。</summary>
internal enum ChildSurfaceScenario
{
    ScrollFollow,
    ClipOut,
    MoveWindow,
}

/// <summary>R29B opt-in 几何执行器的停止原因；每个原因都对应一条如实日志，绝不泛称成功。</summary>
internal enum ChildSurfaceStop
{
    None,
    SafePointLost,
    CompositionChanged,
    CaptureFailed,
    GeometryCheckFailed,
    RestoreFailed,
    ActionException,
    Unsupported,

    /// <summary>R30：自有 input child 可见性操作（hide/show）返回 false 或抛出。</summary>
    VisibilityOperationFailed,

    /// <summary>R31：TSF 文档焦点 acquire/suspend/restore 返回失败或抛出。</summary>
    TsfFocusOperationFailed,

    /// <summary>R31 复审：实际 TSF 焦点读回与受控阶段期望不符（OWN/NULL/SAME_OWN）。</summary>
    TsfFocusPhaseMismatch,
}

/// <summary>
/// R29B opt-in 几何场景的宿主抽象。真实实现（ScenarioRunner 内的包装）与托管测试替身
/// 走<b>同一套</b>执行器控制流——纯托管测试驱动与真实窗口相同的
/// 安全点→不变量→动作→复核→截图→恢复 顺序，因此测试覆盖的就是生产控制流。
/// <para>
/// 测量/恢复动作与原始几何查询只从执行器在 <see cref="InvokeOnUiThread"/> 派发的受管
/// body 内调用；查询失败必须以空/失败值如实回传，绝不允许用伪造的合法矩形冒充一次
/// 成功的原生查询——执行器对空/倒置/漂移矩形显式判失败。
/// </para>
/// </summary>
internal interface IChildSurfaceGeometryHost
{
    /// <summary>在 UI 线程执行；返回 false 表示动作未执行（例如窗口已销毁）。</summary>
    bool InvokeOnUiThread(Func<bool> action);

    /// <summary>
    /// 在 UI 线程的**单个**安全点内同时完成：自有 root/input HWND、PID/TID 归属、
    /// 前台==root、焦点==input（opt-in）、无锁/无布局重入，以及 text/span/composing/
    /// writes/profile/连续性计数快照读取。任一项不满足时 <c>Ok=false</c>。
    /// </summary>
    SafePointProbe Probe(string stage);

    void Record(string kind, IDictionary<string, object?> fields);

    /// <summary>
    /// 一次完整证据截图（同一 UI 动作内先安全点+不变量复核、真实抓图、再复核）。
    /// 返回的判定携带<b>管线内已捕获的首败原因</b>（每张抓图的前置/后置复核与
    /// 阶段读回失败如实落在 Reason）；执行器只消费该结果，绝不在失败后再发
    /// 任何 TSF/profile 复探。Ok=false 即截图不作为有效证据，必须停止后续阶段。
    /// </summary>
    CaptureVerdict Capture(string name);

    /// <summary>暂停指定毫秒（在自有 UI 线程之外）。</summary>
    void Wait(int milliseconds);

    /// <summary>当前水平滚动偏移（物理像素）。</summary>
    int GetScrollX();

    /// <summary>当前垂直滚动偏移（物理像素）。</summary>
    int GetScrollY();

    /// <summary>按**保存的**滚动偏移恢复（scroll-follow/clip-out 精确回滚，不重算 delta）。</summary>
    void SetScrollOffset(int x, int y);

    /// <summary>按逻辑单位滚动，返回实际物理像素（scroll-follow）。</summary>
    int ScrollByLogical(int logicalDelta);

    /// <summary>按物理像素滚动（clip-out 把 input client 推出 viewport）。</summary>
    void ScrollByPhysical(int deltaY);

    /// <summary>整窗平移（move-window 正向控制；SWP_NOACTIVATE，不改焦点/前台）。</summary>
    void MoveRootBy(int dx, int dy);

    /// <summary>把 root 顶层窗口绝对恢复到保存的原点（NOACTIVATE|NOSIZE|NOZORDER）。</summary>
    void SetRootWindowOrigin(int x, int y);

    /// <summary>发布布局中的文本行屏幕矩形；查询失败回传默认矩形（执行器按无效判失败）。</summary>
    TsfRect GetTextRowScreenRect();

    /// <summary>真实可见区域 = input ∩ 全链裁剪祖先的屏幕矩形（与 TSF 查询同源）。</summary>
    TsfRect GetInputClientScreenRect();

    /// <summary>clip 祖先（viewport）client 屏幕矩形；clip-out 的固定参照。</summary>
    TsfRect GetViewportClientScreenRect();

    /// <summary>root 顶层窗口的真实屏幕矩形（move-window 绝对恢复用）。</summary>
    TsfRect GetRootWindowScreenRect();

    /// <summary>
    /// R30 opt-in：宿主是否实现对自有 input child 的可见性变更（仅 clip-out +
    /// 显式 <c>--child-hide-when-clipped</c>）。默认 false——既有宿主不改编译/行为；
    /// R30 路径在派发前先读本属性，false 即按 <see cref="ChildSurfaceStop.Unsupported"/>
    /// 失败，绝不调用 <see cref="VisibilityOperation"/>。
    /// </summary>
    bool SupportsVisibilityOperation => false;

    /// <summary>
    /// R30 opt-in：对自有 input child 做一次可见性变更（show/hide，NOACTIVATE 系
    /// SetWindowPos/ShowWindow）。只在 <see cref="SupportsVisibilityOperation"/> 为 true
    /// 且仍在受管 UI body 内被调用；默认实现返回失败结果，任何归属拒绝/原生
    /// false/异常都必须如实反映在返回的 <see cref="ChildSurfaceVisibilityResult"/> 中。
    /// <para>
    /// 宿主实现必须如实读回操作前后真实可见性：原生写已生效但返回 false/抛异常时，
    /// 结果必须让执行器区分"变更可能发生"与"操作成功"——执行器在动作前已单独保存
    /// 原始可见性，finally 以<b>保存值</b>恢复，绝不信任何一次返回值的乐观叙述。
    /// </para>
    /// </summary>
    ChildSurfaceVisibilityResult VisibilityOperation(bool show, bool showOnlyIfWasVisible)
        => new(false, false, false, "visibility-operation-unsupported");

    /// <summary>
    /// R30 opt-in：在归属校验过的自有 UI 上下文内<b>只读</b>自有 input child 的当前
    /// 真实可见性（不写、不夺焦）。默认实现返回 false——未接线的既有宿主与旧独立
    /// 审核程序不加新成员也能编译；R30 路径只在
    /// <see cref="SupportsVisibilityOperation"/> 为 true 时依赖本读数，读取失败即如实
    /// 停止，绝不用假定的可见性驱动一次真实 hide。
    /// </summary>
    bool ReadInputVisibility() => false;

    /// <summary>
    /// R31 opt-in：宿主是否实现自有 TSF 文档焦点的 suspend/restore（仅 clip-out +
    /// 显式 <c>--child-suspend-tsf-focus-when-clipped</c>）。默认 false——既有宿主不
    /// 改编译/行为；R31 路径在派发前先读本属性，false 即按
    /// <see cref="ChildSurfaceStop.Unsupported"/> 失败，绝不调用
    /// <see cref="AcquireTsfFocus"/>/<see cref="SuspendTsfFocus"/>/<see cref="RestoreTsfFocus"/>。
    /// </summary>
    bool SupportsTsfFocusOperation => false;

    /// <summary>
    /// R31 opt-in：在拥有窗口与 COM 对象的 UI 线程内，只读取得当前 TSF 文档焦点
    /// 并返回<b>已验证为自有</b>的令牌（经共享 <see cref="TsfDocumentFocus.Acquire"/>）。
    /// 只在 <see cref="SupportsTsfFocusOperation"/> 为 true 且仍在受管 UI body 内被调用；
    /// 默认实现返回失败结果。令牌 ownsRefs=true 时调用方在 finally 经
    /// <see cref="ReleaseTsfFocus"/> 成对释放——COM 所有权跨多次派发也严格成对。
    /// </summary>
    TsfFocusAcquireResult AcquireTsfFocus()
        => new(false, null, "tsf-focus-operation-unsupported");

    /// <summary>
    /// R31 opt-in：真实 <c>ITfThreadMgr.SetFocus(null)</c>（经共享
    /// <see cref="TsfDocumentFocus.Suspend"/>：前置自有验证 → 原生写 → null 读回）。
    /// 只在受管 UI body 内被调用；默认实现返回失败结果。
    /// </summary>
    TsfFocusOpResult SuspendTsfFocus()
        => new(false, false, "tsf-focus-operation-unsupported");

    /// <summary>
    /// R31 opt-in：把保存的同一个自有 doc/context 重新 SetFocus（经共享
    /// <see cref="TsfDocumentFocus.Restore"/>：前置无焦点/自有验证 → 原生写 →
    /// doc+top-context 严格同一读回）。foreign focus 占据时如实失败，绝不覆盖。
    /// </summary>
    TsfFocusOpResult RestoreTsfFocus(TsfFocusHandle saved)
        => new(false, false, "tsf-focus-operation-unsupported");

    /// <summary>
    /// R31 opt-in：在 UI 线程内成对释放 <see cref="AcquireTsfFocus"/> 令牌持有的 RCW
    /// （经 <see cref="TsfDocumentFocus.ReleaseHandle"/>）。默认实现是 no-op——
    /// 未接线宿主没有可释放的引用。<b>释放异常也如实反映在返回值</b>——调用方把它
    /// 计入最终诊断，绝不"释放失败仍报 ok"；同时不得用它覆盖更早的首败。
    /// </summary>
    TsfFocusReleaseResult ReleaseTsfFocus(TsfFocusHandle? handle)
        => TsfFocusReleaseResult.Success;

    /// <summary>
    /// R31 复审：当前受控 TSF 焦点阶段（真实路径与测试替身共用
    /// <see cref="TsfFocusStage"/>）。期望阶段只在一次<b>已验证</b>的 suspend/restore
    /// 成功后迁移；读回不符/中途失去归属时阶段保持不动——之后的 postguard/清理
    /// 按<b>实际仍生效</b>的阶段核对。
    /// </summary>
    TsfFocusStage TsfFocusStageState => new();

    /// <summary>
    /// R31 复审：实际读回 TSF 焦点并按 <see cref="TsfFocusStageState"/> 的<b>当前</b>
    /// 期望阶段判定。每个真实安全点（探针、等待前后、每张证据截图前后、焦点操作
    /// 各步骤）都必须实际读回并如实记录——600ms 内焦点自动回到 OWN 也能被发现。
    /// 默认实现如实失败（未接线宿主不做 TSF 读回）。
    /// </summary>
    TsfFocusPhaseResult ReadTsfFocusPhase()
        => new(false, TsfFocusStageState.Phase, "tsf-focus-phase-unsupported");

    /// <summary>
    /// R31 复审：清理动作前的原生归属门禁（UI 线程内）。前台仍在自有 root、
    /// Win32 焦点仍在自有 input、HWND 归属未变才允许清理；返回 null 允许，
    /// 非 null 为跳过原因。<b>清理绝不夺焦、绝不覆盖外部焦点</b>。
    /// </summary>
    string? CheckTsfFocusCleanupAllowed() => "cleanup-check-unsupported";

    /// <summary>无锁 GetScreenExt：input 不可见时按官方语义返回 S_OK+全零矩形。</summary>
    int GetScreenExt(out TsfRect rect);

    /// <summary>
    /// 在<b>持真实读锁</b>的只读编辑会话内查询 GetTextExt（组合 span）。
    /// 已完全裁剪时返回 S_OK+全零矩形+clipped；未发布布局返回 TS_E_NOLAYOUT。
    /// </summary>
    int GetTextExt(int acpStart, int acpEnd, out TsfRect rect, out int clipped);
}

/// <summary>
/// R29B 真实 input child 可见几何执行器：真实 opt-in 场景与托管测试共用，不另造流程。
/// <para>
/// 每个测量动作都在<b>同一个</b> UI 派发 body 内完成：安全点+完整组合不变量复核 →
/// 真实几何动作 → 同 body 再复核。基线截图后、测量动作前若组合/焦点/前台已丢失，
/// body 直接返回 false，动作绝不执行。等待在 UI 派发器之外，前后均重新验证。
/// </para>
/// <para>
/// 初始几何保存之后的所有阶段都包在<b>真实 try/finally</b> 内：任何返回、停止或
/// 异常（Capture/Wait/Probe/UI 派发/受管回调抛出）都会先执行自有几何的尽力而为
/// 回滚，再让失败如实落地——截图 I/O 或等待异常绝不会带着被改动的几何逃出
/// <see cref="Run"/>。已进入正常恢复路径时 finally 不重复回滚。
/// </para>
/// <para>
/// 首个失败不可恢复：任一安全点/不变量/截图/几何/恢复检查失败即置
/// <see cref="Stop"/>；之后只允许恢复自有几何与测试结束清理，不再做任何
/// 测量/TSF 查询/等待/截图。<see cref="Run"/> 只有 <see cref="Stop"/>==
/// <see cref="ChildSurfaceStop.None"/> 且全部检查成功才返回 true——恢复截图失败、
/// 恢复几何偏差同样如实判失败，首个停止原因不被后续恢复覆盖。
/// </para>
/// <para>
/// 恢复使用保存的**原始** X/Y 滚动偏移与 root <b>绝对原点</b>（非盲目反向增量）——
/// 动作抛异常或只部分移动时反向增量会把窗口移到错误位置。恢复期间不做焦点恢复、
/// 不操作已不属于本进程/UI 线程的 HWND；失败路径的尽力恢复只回写+读回自有
/// 几何，不再调用 _host.Probe（真实 Probe 会查询活动 profile COM）。
/// </para>
/// </summary>
internal sealed class ChildSurfaceGeometryExecutor
{
    /// <summary>clip-out 要求 input client bottom 至少高出 viewport top 的物理像素。</summary>
    internal const int ClipOutMarginPx = 40;

    /// <summary>move-window 正向控制位移（物理像素）。</summary>
    internal const int MoveWindowDeltaPx = 24;

    /// <summary>scroll-follow 测量后让原生事实沉降的等待（UI 派发器之外）。</summary>
    private const int ScrollSettleMs = 500;

    /// <summary>clip-out 测量后沉降的既定等待（UI 派发器之外）。</summary>
    private const int ClipSettleMs = 600;

    /// <summary>R30：hide 生效且后置复核通过后、clipped 截图前的原生 UI 沉降
    /// 等待（UI 派发器之外，前后各一次安全点+不变量复核）。</summary>
    private const int HideSettleMs = 600;

    /// <summary>R31：TSF suspend 生效且后置复核通过后、clipped 截图前的原生 UI
    /// 沉降等待（UI 派发器之外，前后各一次安全点+不变量复核）。</summary>
    private const int SuspendSettleMs = 600;

    /// <summary>move-window 测量后沉降的既定等待（UI 派发器之外）。</summary>
    private const int MoveSettleMs = 600;

    /// <summary>恢复后让原生事实沉降的等待（UI 派发器之外）。</summary>
    private const int RestoreSettleMs = 600;

    private readonly IChildSurfaceGeometryHost _host;
    private readonly Action<LifecycleSnapshot> _baselineSink;
    private readonly bool _hideWhenClipped;
    private readonly bool _suspendTsfFocusWhenClipped;

    /// <summary>R31 复审：首个失败原因（<see cref="StopDetail"/> 已置时保持原值；
    /// 恢复/清理错误只追加诊断，绝不覆盖首败）。</summary>
    private string? _firstFailureDetail;

    internal ChildSurfaceGeometryExecutor(IChildSurfaceGeometryHost host,
        Action<LifecycleSnapshot> baselineSink, bool hideWhenClipped = false,
        bool suspendTsfFocusWhenClipped = false)
    {
        // R30 与 R31 互斥：同一时间只允许一种"裁剪后动作"假设（解析层已拒绝，
        // 此处再兜一次——构造时显式冲突同样拒绝，绝不静默选一个）。
        if (hideWhenClipped && suspendTsfFocusWhenClipped)
        {
            throw new ArgumentException(
                "hide-when-clipped and suspend-tsf-focus-when-clipped are mutually exclusive",
                nameof(suspendTsfFocusWhenClipped));
        }

        _host = host;
        _baselineSink = baselineSink;
        _hideWhenClipped = hideWhenClipped;
        _suspendTsfFocusWhenClipped = suspendTsfFocusWhenClipped;
    }

    /// <summary>实验停止原因；<see cref="ChildSurfaceStop.None"/> = 未提前停止。</summary>
    internal ChildSurfaceStop Stop { get; private set; } = ChildSurfaceStop.None;

    /// <summary>已建立的基线快照；未建立为 null。</summary>
    internal LifecycleSnapshot? Baseline { get; private set; }

    internal string StopDetail { get; private set; } = string.Empty;

    internal string StoppedAtStage { get; private set; } = string.Empty;

    /// <summary>
    /// 执行一次 opt-in 可见几何场景。返回 true 仅当未提前停止且
    /// 全部安全点/不变量/动作/截图/恢复检查通过——任何失败都如实返回 false。
    /// 捕获/等待/探针/派发/回调抛出的异常<b>绝不逃出本方法</b>：先由场景内
    /// try/finally 回滚自有几何，再在此如实记为失败（保留首个停止原因）。
    /// </summary>
    internal bool Run(ChildSurfaceScenario scenario, int logicalDelta)
    {
        try
        {
            var ok = scenario switch
            {
                ChildSurfaceScenario.ScrollFollow => RunScrollFollow(logicalDelta),
                ChildSurfaceScenario.ClipOut => RunClipOut(),
                ChildSurfaceScenario.MoveWindow => RunMoveWindow(),
                _ => StopWith(ChildSurfaceStop.Unsupported, $"unsupported scenario {scenario}", "dispatch"),
            };
            return ok && Stop == ChildSurfaceStop.None;
        }
        catch (Exception ex)
        {
            // 兜底：场景主体逃出的异常一律如实判失败，绝不向外抛出。
            // 几何回滚已由各场景的 finally 完成；这里只记录原始原因。
            StopIfNone(ChildSurfaceStop.ActionException,
                $"executor threw: {ex.GetType().Name}: {ex.Message}", "run");
            return false;
        }
    }

    /// <summary>
    /// scroll-follow：验证小滚动后文本行按<b>实际物理 delta</b>（非零）上移，
    /// 同时保持同一组合；恢复保存的原始 X/Y 偏移并逐矩形复核。
    /// </summary>
    private bool RunScrollFollow(int logicalDelta)
    {
        var probe = ProbeOrStop("scroll-before");
        if (probe is not { } baseline)
        {
            return false;
        }

        var b = baseline.Snapshot;
        if (!RequireComposingBaseline(b, "scroll-baseline"))
        {
            return false;
        }

        Baseline = b;
        _baselineSink(b);
        if (!CaptureOrStop("r29b-baseline", "capture-baseline"))
        {
            return false;
        }

        // 初始精确几何：原始 X/Y 滚动偏移 + 行/viewport/input 矩形，全部在受管 body 内读取。
        var savedX = 0;
        var savedY = 0;
        var row0 = default(TsfRect);
        var viewport0 = default(TsfRect);
        var input0 = default(TsfRect);
        var initial = DispatchMeasured("scroll-initial-before", "scroll-initial-after", b, () =>
        {
            savedX = _host.GetScrollX();
            savedY = _host.GetScrollY();
            row0 = _host.GetTextRowScreenRect();
            viewport0 = _host.GetViewportClientScreenRect();
            input0 = _host.GetInputClientScreenRect();
            return NonEmpty(row0) && NonEmpty(viewport0) && NonEmpty(input0);
        });
        if (!initial)
        {
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                "initial scroll/geometry query returned empty or invalid rects", "scroll-initial");
        }

        // 自此任何返回/异常都必须先回滚自有几何：动作可能已执行或部分执行。
        var mutationMayExist = false;
        // 仅当正常恢复被**复核确认**后才跳过 finally 回滚；恢复动作/等待/验证
        // 在中途失败或抛出时（部分应用）finally 仍须尽力恢复保存的原几何。
        var restoreConfirmed = false;
        try
        {
            // 测量：前置复核 + 真实滚动 + 同 body 后置复核，三者不可拆分。
            mutationMayExist = true;
            var physical = 0;
            var measured = DispatchMeasured("scroll-action-before", "scroll-action-after", b, () =>
            {
                physical = _host.ScrollByLogical(logicalDelta);
                return true;
            });
            if (!measured)
            {
                return false;
            }

            if (!WaitProbed("scroll-settle", ScrollSettleMs, b))
            {
                return false;
            }

            // 沉降后复核：物理 delta 非零、行精确上移物理 delta、viewport 参照不漂移、
            // 滚动偏移读数与实际一致。
            var verified = DispatchMeasured("scroll-verify-before", "scroll-verify-after", b, () =>
            {
                if (physical == 0)
                {
                    return false;
                }

                var rowAfter = _host.GetTextRowScreenRect();
                var viewportAfter = _host.GetViewportClientScreenRect();
                var scrollX = _host.GetScrollX();
                var scrollY = _host.GetScrollY();
                return NonEmpty(rowAfter)
                    && NonEmpty(viewportAfter)
                    && SameRectShift(row0, rowAfter, 0, -physical)
                    && SameRect(viewport0, viewportAfter)
                    && scrollX == savedX
                    && scrollY == savedY + physical;
            });
            if (!verified)
            {
                return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                    $"row/viewport/offset mismatch after scroll (physical={physical})", "scroll-verify");
            }

            if (!CaptureOrStop("r29b-scrolled", "capture-scrolled"))
            {
                return false;
            }

            if (Stop != ChildSurfaceStop.None)
            {
                return false;
            }

            // 正常恢复路径：动作+前后验证的沉降等待+读回复核全部通过才置
            // restoreConfirmed——部分应用后失败/抛出时 finally 仍执行尽力回滚。
            var restored = RestoreScrollVerified(savedX, savedY, b, row0, viewport0, input0, "scroll-restore");
            if (!restored)
            {
                // 恢复验证失败即停：不再产出恢复证据截图。
                return false;
            }

            restoreConfirmed = true;
            return CaptureOrStop("r29b-scroll-restored", "scroll-restore-capture");
        }
        finally
        {
            // 覆盖测量期间的一切退出/异常：正常恢复未被确认时尽力回滚到保存值。
            // 恢复失败已如实记入恢复诊断/停止原因，绝不让实验以被改动几何"成功"结束。
            if (mutationMayExist && !restoreConfirmed)
            {
                RestoreScrollBestEffort(savedX, savedY, "scroll-finally-restore");
            }
        }
    }

    /// <summary>
    /// clip-out：把 input client 完全推出固定 viewport——TSF GetScreenExt/GetTextExt
    /// 必须如实报告<b>四边严格全零</b>矩形（不是 IsEmpty）且 clipped 标记成立；
    /// viewport 参照在测量期间不得漂移。
    /// </summary>
    private bool RunClipOut()
    {
        var probe = ProbeOrStop("clip-before");
        if (probe is not { } baseline)
        {
            return false;
        }

        var b = baseline.Snapshot;
        if (!RequireComposingBaseline(b, "clip-baseline"))
        {
            return false;
        }

        Baseline = b;
        _baselineSink(b);
        if (!CaptureOrStop("r29b-baseline", "capture-baseline"))
        {
            return false;
        }

        var savedX = 0;
        var savedY = 0;
        var row0 = default(TsfRect);
        var viewport0 = default(TsfRect);
        var input0 = default(TsfRect);
        var delta = 0;
        var initial = DispatchMeasured("clip-initial-before", "clip-initial-after", b, () =>
        {
            savedX = _host.GetScrollX();
            savedY = _host.GetScrollY();
            viewport0 = _host.GetViewportClientScreenRect();
            input0 = _host.GetInputClientScreenRect();
            row0 = _host.GetTextRowScreenRect();
            if (!NonEmpty(viewport0) || !NonEmpty(input0) || !NonEmpty(row0))
            {
                return false;
            }

            delta = Math.Max(1, input0.Bottom - viewport0.Top + ClipOutMarginPx);
            return true;
        });
        if (!initial)
        {
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                "initial clip geometry query returned empty or invalid rects", "clip-initial");
        }

        var mutationMayExist = false;
        var restoreConfirmed = false;
        // R30：原可见性必须在 hide 的原生动作之前单独读取并保存——"动作可能已经发生"
        // 与"操作成功"是两回事：原生 hide 已生效但返回 false/抛异常时，op.WasVisible
        // 不可信（甚至还没赋值），finally 必须按<b>保存的</b>原可见性写回。此旗标在
        // 进入动作 body 之前置位；finally 只做归属安全的自有窗口清理写，不做探针/
        // profile/等待/截图。
        var savedVisibility = false;
        var visibilitySaved = false;
        var visibilityMutationMayExist = false;
        // 仅当正常恢复路径的 show 读回<b>且</b>派发后置复核都通过才置位——
        // 置位后 finally 绝不再触碰可见性。
        var visibilityRestoreConfirmed = false;
        // R31：原 TSF 文档焦点必须在 suspend 的原生动作之前经 acquire 单独读回并保存
        // （doc+top-context 严格同一令牌）——"COM 写可能已发生"与"操作成功"同样是两回事；
        // finally 必须按<b>保存的同一令牌</b>尽力恢复。mutation 旗标在进入动作 body 之前
        // 置位；令牌 ownsRefs 时 finally 成对释放。
        TsfFocusHandle? savedFocus = null;
        var focusSaved = false;
        var focusMutationMayExist = false;
        // 仅当正常恢复路径的 restore 读回<b>且</b>派发后置复核都通过才置位——
        // 置位后 finally 绝不再写焦点。
        var focusRestoreConfirmed = false;
        try
        {
            mutationMayExist = true;
            var measured = DispatchMeasured("clip-action-before", "clip-action-after", b, () =>
            {
                _host.ScrollByPhysical(delta);
                return true;
            });
            if (!measured)
            {
                return false;
            }

            if (!WaitProbed("clip-settle", ClipSettleMs, b))
            {
                return false;
            }

            var spanStart = b.SpanStart;
            var spanEnd = b.SpanEnd;
            var verified = DispatchMeasured("clip-verify-before", "clip-verify-after", b, () =>
            {
                var viewportAfter = _host.GetViewportClientScreenRect();
                var inputAfter = _host.GetInputClientScreenRect();
                if (!NonEmpty(viewportAfter) || !SameRect(viewport0, viewportAfter))
                {
                    // 固定 viewport 漂移 = 测量参照失效。
                    return false;
                }

                if (inputAfter.Bottom > viewportAfter.Top)
                {
                    // input 未完全推出 viewport（含零 delta / 查询失败回传的 default）。
                    return false;
                }

                // 无锁 GetScreenExt：S_OK + 四边严格全零（{7,8,7,8} 这类非零空矩形不算）。
                var screenHr = _host.GetScreenExt(out var screenRect);
                if (screenHr != 0 || !IsExactZero(screenRect))
                {
                    return false;
                }

                // 真读锁内 GetTextExt：HRESULT 成功 + clipped + 四边严格全零。
                var textHr = _host.GetTextExt(spanStart, spanEnd, out var textRect, out var clipped);
                return textHr == 0 && clipped != 0 && IsExactZero(textRect);
            });
            if (!verified)
            {
                return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                    "clipped geometry/zero-rect/viewport check failed", "clip-verify");
            }

            // R30 opt-in：几何已严格全零验证后、clipped 截图前，对自有 input child
            // 做一次真实 WS_VISIBLE 变更（NOACTIVATE）。若 OS 自然丢焦/组合中断，
            // 后置复核如实判失败且绝不 SetFocus/恢复；恢复分支把原可见性写回。
            if (_hideWhenClipped)
            {
                var hide = DispatchMeasured("clip-hide-before", "clip-hide-after", b, () =>
                {
                    if (!_host.SupportsVisibilityOperation)
                    {
                        return false;
                    }

                    // 先读并保存原始可见性（归属校验过的只读查询），再执行原生
                    // hide——保存值是 finally 恢复的唯一真源，不受之后任何
                    // false/异常影响。旗标在动作之前置位：哪怕读取之后原生调用
                    // 抛异常，恢复也以"变更可能存在"对待。
                    savedVisibility = _host.ReadInputVisibility();
                    visibilitySaved = true;
                    visibilityMutationMayExist = true;

                    var op = _host.VisibilityOperation(show: false, showOnlyIfWasVisible: true);
                    if (!op.Ok)
                    {
                        // 原生 false/异常：变更可能已部分生效——由 finally 按
                        // savedVisibility 尽力恢复；这里如实判失败，绝不继续截图。
                        _host.Record("clip-hide-op", new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["reason"] = op.Reason,
                            ["visible_after"] = op.VisibleAfter,
                        });
                        return false;
                    }

                    return true;
                });
                if (!hide)
                {
                    // DispatchMeasured 内前置复核/后置复核失败或动作体异常时已置停止
                    // 原因；这里只兜底"宿主不支持/原生调用失败"这种动作体返回 false
                    // 但 Stop 仍为 None 的情形。
                    if (Stop == ChildSurfaceStop.None)
                    {
                        return StopIfNone(ChildSurfaceStop.VisibilityOperationFailed,
                            "own input child hide operation failed or unsupported",
                            "clip-hide");
                    }

                    return false;
                }

                // hide 生效且后置复核通过：原生候选 UI 可能异步沉降——UI 派发器之外
                // 等待 600ms（前后各一次安全点+完整不变量复核），再产出 clipped 证据。
                if (!WaitProbed("clip-hide-settle", HideSettleMs, b))
                {
                    return false;
                }
            }

            // R31 opt-in：几何已严格全零验证后、clipped 截图前，把自有 TSF 文档焦点
            // 经真实 ITfThreadMgr.SetFocus(null) 挂起（Win32 前台/焦点/关联保持，
            // 不做 HWND SetFocus/AssociateFocus/Pop/Push）。acquire→suspend→读回
            // null 全在同一派发 body 内；COM 写已生效但返回失败/抛异常时由
            // finally 按保存的同一令牌尽力恢复。若 TIP 因此结束组合或 profile/
            // 焦点事实变化，后置复核如实判失败，绝不补输入。
            // R31 复审：写前最后一刻受控阶段复核（期望 OWN）——COM 查询重入可能
            // 已夺焦/失前台；已验证的 suspend 成功后阶段迁到 NULL，之后每个真实
            // 安全点/等待边界/证据截图都实际读回并按当前期望核对。
            if (_suspendTsfFocusWhenClipped)
            {
                var suspended = DispatchMeasured("clip-suspend-before", "clip-suspend-after", b, () =>
                {
                    if (!_host.SupportsTsfFocusOperation)
                    {
                        return false;
                    }

                    // R31 复审：acquire/写前的最后一次阶段核对（期望 OWN）——
                    // GetFocus/GetTop/identity 的 COM 重入可能已把前台/焦点切走；
                    // 此时如实拒绝，绝不 suspend、绝不写。
                    if (!GuardTsfPhase("clip-suspend-pre", TsfFocusPhase.OwnDocument))
                    {
                        return false;
                    }

                    // 先读并保存原焦点（归属校验过的自有 doc/context 令牌），再执行
                    // 原生 suspend——保存令牌是恢复的唯一真源，不受之后任何
                    // false/异常影响。
                    var acquire = _host.AcquireTsfFocus();
                    if (!acquire.Ok || acquire.Focus is null)
                    {
                        _host.Record("clip-suspend-acquire", new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["reason"] = acquire.Reason,
                        });
                        return false;
                    }

                    savedFocus = acquire.Focus;
                    focusSaved = true;
                    // "COM 写可能已发生"先置位，再执行原生调用——suspend 返回
                    // false/抛异常时 finally 必须按保存令牌尽力恢复（共享操作核内的
                    // pre/post 读回不改变这一保守语义）。
                    focusMutationMayExist = true;

                    var op = _host.SuspendTsfFocus();
                    if (!op.Ok)
                    {
                        // 原生 false/异常：变更可能已部分生效——由 finally 按
                        // savedFocus 尽力恢复；这里如实判失败，绝不继续截图。
                        _host.Record("clip-suspend-op", new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["reason"] = op.Reason,
                            ["mutation_may_exist"] = op.MutationMayExist,
                        });
                        return false;
                    }

                    // suspend 已验证成功：阶段迁移到 NULL——后续每个真实安全点/等待
                    // 边界/证据截图都按"期望 NULL"核对，焦点自动回 OWN 即被识别。
                    Stage.MarkSuspended(savedFocus);
                    return true;
                });
                if (!suspended)
                {
                    // DispatchMeasured 内前置复核/后置复核失败或动作体异常时已置停止
                    // 原因；这里只兜底"宿主不支持/原生调用失败"这种动作体返回 false
                    // 但 Stop 仍为 None 的情形。
                    if (Stop == ChildSurfaceStop.None)
                    {
                        return StopIfNone(ChildSurfaceStop.TsfFocusOperationFailed,
                            "tsf document focus suspend failed or unsupported",
                            "clip-suspend");
                    }

                    return false;
                }

                // suspend 生效且后置复核通过：TIP 原生 UI 可能异步沉降——UI 派发器之外
                // 等待 600ms（前后各一次安全点+完整不变量复核），再产出 clipped 证据。
                if (!WaitProbed("clip-suspend-settle", SuspendSettleMs, b))
                {
                    return false;
                }
            }

            if (!CaptureOrStop("r29b-clipped", "capture-clipped"))
            {
                return false;
            }

            if (Stop != ChildSurfaceStop.None)
            {
                return false;
            }

            // R30：恢复分支——clipped 截图已成立，先把 input child 的可见性按
            // <c>savedVisibility</c>（hide 前保存的<b>原始</b>状态）写回（仍
            // NOACTIVATE、不动几何/焦点），再继续既有的滚动几何恢复。show 失败如实
            // 停止；原本就不可见时宿主只读回不写，绝不伪造一次显示。
            // visibilityRestoreConfirmed 只在 show 读回与派发后置复核<b>都</b>通过后
            // 置位——其后 finally 不再补一次恢复。
            if (visibilityMutationMayExist)
            {
                var shown = DispatchMeasured("clip-show-before", "clip-show-after", b, () =>
                {
                    var op = _host.VisibilityOperation(show: true,
                        showOnlyIfWasVisible: savedVisibility);
                    if (!op.Ok || op.VisibleAfter != savedVisibility)
                    {
                        // 原生 show 失败或读回与保存的原状态不符：如实失败，
                        // finally 仍会按保存值尽力再恢复一次。
                        _host.Record("clip-show-op", new Dictionary<string, object?>
                        {
                            ["ok"] = op.Ok,
                            ["reason"] = op.Reason,
                            ["visible_after"] = op.VisibleAfter,
                            ["expected"] = savedVisibility,
                        });
                        return false;
                    }

                    return true;
                });
                if (!shown)
                {
                    if (Stop == ChildSurfaceStop.None)
                    {
                        return StopIfNone(ChildSurfaceStop.VisibilityOperationFailed,
                            "own input child show restore failed",
                            "clip-show");
                    }

                    return false;
                }

                visibilityRestoreConfirmed = true;
            }

            // R31：恢复分支——clipped 截图已成立，先把保存的同一个 TSF 文档焦点写回
            // （仍只做 SetFocus(保存令牌)，不动 Win32 焦点/前台/关联），再继续既有的
            // 滚动几何恢复。restore 失败如实停止；focusRestoreConfirmed 只在读回与
            // 派发后置复核<b>都</b>通过后置位——其后 finally 不再补一次写。
            // R31 复审：写前最后一刻期望 NULL（COM 重入/外部自动回 OWN 即拒绝写），
            // 已验证 restore 成功后阶段迁到 SAME_OWN——之后每个安全点按
            // "与保存令牌同一 doc/context"核对。
            if (focusMutationMayExist && savedFocus is { } focusToRestore)
            {
                var restoredFocus = DispatchMeasured("clip-focus-restore-before",
                    "clip-focus-restore-after", b, () =>
                {
                    // R31 复审：restore 写前的最后一次阶段核对（期望 NULL）——
                    // 外部已自动恢复成 OWN 或前台/焦点已切走时如实拒绝，绝不
                    // 覆盖外部焦点、绝不补写。
                    if (!GuardTsfPhase("clip-restore-pre", TsfFocusPhase.None))
                    {
                        return false;
                    }

                    var op = _host.RestoreTsfFocus(focusToRestore);
                    if (!op.Ok)
                    {
                        // 原生 restore 失败或读回与保存令牌不符（含 foreign focus
                        // 占据后拒绝覆盖）：如实失败，finally 仍会按保存令牌尽力恢复。
                        _host.Record("clip-focus-restore-op", new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["reason"] = op.Reason,
                        });
                        return false;
                    }

                    // restore 已验证成功：阶段迁到 SAME_OWN——之后安全点按"与保存
                    // 令牌同一 doc/context"核对，焦点再被切走即被识别。
                    Stage.MarkRestored();
                    return true;
                });
                if (!restoredFocus)
                {
                    if (Stop == ChildSurfaceStop.None)
                    {
                        return StopIfNone(ChildSurfaceStop.TsfFocusOperationFailed,
                            "tsf document focus restore failed",
                            "clip-focus-restore");
                    }

                    return false;
                }

                focusRestoreConfirmed = true;
            }

            var restored = RestoreScrollVerified(savedX, savedY, b, row0, viewport0, input0, "clip-restore");
            if (!restored)
            {
                return false;
            }

            restoreConfirmed = true;
            return CaptureOrStop("r29b-clip-restored", "clip-restore-capture");
        }
        finally
        {
            if (mutationMayExist && !restoreConfirmed)
            {
                RestoreScrollBestEffort(savedX, savedY, "clip-finally-restore");
            }

            // R30：hide 的原生动作可能已生效（哪怕它返回 false/抛异常）而正常 show
            // 恢复未确认时，finally 必须把<b>保存的原可见性</b>尽力写回——恢复失败
            // 如实记录，绝不吞成成功，也绝不在已确认恢复后再补一次写。
            // best-effort 只派发自有归属校验+清理写：不做 Probe/profile/等待/截图。
            if (visibilityMutationMayExist && visibilitySaved && !visibilityRestoreConfirmed)
            {
                try
                {
                    var applied = _host.InvokeOnUiThread(() =>
                    {
                        // 共享操作的归属门禁是唯一防线：外来/已回收 HWND 一律拒绝写；
                        // 写回的是 hide 前保存的精确状态（原本不可见则绝不伪造显示）。
                        var restored = _host.VisibilityOperation(show: savedVisibility,
                            showOnlyIfWasVisible: true);
                        return restored.Ok && restored.VisibleAfter == savedVisibility;
                    });
                    _host.Record("clip-finally-visibility", new Dictionary<string, object?>
                    {
                        ["restored_to_saved"] = applied,
                        ["saved_visible"] = savedVisibility,
                    });
                    if (!applied)
                    {
                        // 恢复未读回保存值：如实记诊断（首败原因不被覆盖）。
                        RecordRestoreDiagnostic("clip-finally-visibility",
                            $"visibility restore could not confirm saved state {savedVisibility}");
                    }
                }
                catch (Exception ex)
                {
                    // 尽力恢复自身抛异常：如实记录，绝不掩盖首个失败、绝不向外传播。
                    try
                    {
                        _host.Record("clip-finally-visibility", new Dictionary<string, object?>
                        {
                            ["restored_to_saved"] = false,
                            ["threw"] = true,
                            ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                        });
                    }
                    catch
                    {
                    }

                    RecordRestoreDiagnostic("clip-finally-visibility",
                        $"visibility restore threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // R31：suspend 的 COM 写可能已生效（哪怕它返回 false/抛异常）而正常
            // restore 未确认时，finally 必须把<b>保存的同一令牌</b>尽力写回——恢复
            // 失败如实记录，绝不吞成成功，也绝不在已确认恢复后再补一次写。
            // foreign focus 已占据时宿主拒绝覆盖（不操作别人的文档焦点）。
            // R31 复审：清理前的<b>最后一刻</b>原生门禁（前台仍在自有 root/Win32
            // 焦点仍在自有 input/HWND 归属未变/无锁无重入）由宿主注入——
            // 失去归属/前台/焦点时<b>跳过</b>清理写，绝不夺焦、绝不覆盖外部状态。
            if (focusMutationMayExist && focusSaved && savedFocus is { } focusHandle
                && !focusRestoreConfirmed)
            {
                try
                {
                    var applied = _host.InvokeOnUiThread(() =>
                    {
                        // R31 复审：清理动作前的原生归属复核——返回非 null 即跳过，
                        // 绝不调用恢复、绝不写。
                        var refuse = _host.CheckTsfFocusCleanupAllowed();
                        if (refuse is not null)
                        {
                            _host.Record("clip-finally-focus", new Dictionary<string, object?>
                            {
                                ["skipped"] = true,
                                ["reason"] = refuse,
                            });
                            return false;
                        }

                        // 共享恢复操作的 foreign-gate 是唯一防线：外部已抢占文档焦点
                        // 时拒绝写；写回的是 suspend 前保存的精确同一令牌。
                        var restored = _host.RestoreTsfFocus(focusHandle);
                        return restored.Ok;
                    });
                    _host.Record("clip-finally-focus", new Dictionary<string, object?>
                    {
                        ["restored_to_saved"] = applied,
                    });
                    if (!applied)
                    {
                        // 恢复未读回保存身份：如实记诊断（首败原因不被覆盖）。
                        RecordRestoreDiagnostic("clip-finally-focus",
                            "tsf focus restore could not confirm saved identity");
                    }
                }
                catch (Exception ex)
                {
                    // 尽力恢复自身抛异常：如实记录，绝不掩盖首个失败、绝不向外传播。
                    try
                    {
                        _host.Record("clip-finally-focus", new Dictionary<string, object?>
                        {
                            ["restored_to_saved"] = false,
                            ["threw"] = true,
                            ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                        });
                    }
                    catch
                    {
                    }

                    RecordRestoreDiagnostic("clip-finally-focus",
                        $"tsf focus restore threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // R31：acquire 令牌 ownsRefs 时（真实路径）成对释放 RCW——跨多次派发
            // 也严格成对；测试替身 ownsRefs=false 时是 no-op。释放自身失败如实记，
            // 绝不掩盖首个失败。R31 复审：ReleaseTsfFocus 返回值如实进入诊断——
            // 任一释放抛异常都不再被静默吞掉。
            if (focusSaved && savedFocus is { } handleToRelease)
            {
                try
                {
                    var releaseResult = TsfFocusReleaseResult.Success;
                    _host.InvokeOnUiThread(() =>
                    {
                        releaseResult = _host.ReleaseTsfFocus(handleToRelease);
                        return true;
                    });
                    if (!releaseResult.Ok)
                    {
                        RecordRestoreDiagnostic("clip-finally-focus-release",
                            $"focus handle release failed: {releaseResult.Reason}");
                    }
                }
                catch (Exception ex)
                {
                    RecordRestoreDiagnostic("clip-finally-focus-release",
                        $"focus handle release threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// move-window：正向控制平移后行与 root 均按相同 delta 移动；恢复以保存的
    /// root <b>绝对原点</b>回滚——动作抛异常或只部分移动时，盲目反向 +24 会把
    /// 窗口移到错误位置。
    /// </summary>
    private bool RunMoveWindow()
    {
        var probe = ProbeOrStop("move-before");
        if (probe is not { } baseline)
        {
            return false;
        }

        var b = baseline.Snapshot;
        if (!RequireComposingBaseline(b, "move-baseline"))
        {
            return false;
        }

        Baseline = b;
        _baselineSink(b);
        if (!CaptureOrStop("r29b-baseline", "capture-baseline"))
        {
            return false;
        }

        var root0 = default(TsfRect);
        var row0 = default(TsfRect);
        var viewport0 = default(TsfRect);
        var input0 = default(TsfRect);
        var initial = DispatchMeasured("move-initial-before", "move-initial-after", b, () =>
        {
            root0 = _host.GetRootWindowScreenRect();
            row0 = _host.GetTextRowScreenRect();
            viewport0 = _host.GetViewportClientScreenRect();
            input0 = _host.GetInputClientScreenRect();
            return NonEmpty(root0) && NonEmpty(row0) && NonEmpty(viewport0) && NonEmpty(input0);
        });
        if (!initial)
        {
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                "initial root/row geometry query returned empty or invalid rects", "move-initial");
        }

        var mutationMayExist = false;
        var restoreConfirmed = false;
        try
        {
            // 绝对原点回滚由 finally 保证：未移动时是无害 no-op；部分移动时恢复到保存位置。
            mutationMayExist = true;
            var measured = DispatchMeasured("move-action-before", "move-action-after", b, () =>
            {
                _host.MoveRootBy(0, -MoveWindowDeltaPx);
                return true;
            });
            if (!measured)
            {
                return false;
            }

            if (!WaitProbed("move-settle", MoveSettleMs, b))
            {
                return false;
            }

            var verified = DispatchMeasured("move-verify-before", "move-verify-after", b, () =>
            {
                var rowAfter = _host.GetTextRowScreenRect();
                var rootAfter = _host.GetRootWindowScreenRect();
                var viewportAfter = _host.GetViewportClientScreenRect();
                var inputAfter = _host.GetInputClientScreenRect();
                return NonEmpty(rowAfter) && NonEmpty(rootAfter)
                    && NonEmpty(viewportAfter) && NonEmpty(inputAfter)
                    && SameRectShift(row0, rowAfter, 0, -MoveWindowDeltaPx)
                    && SameRectShift(root0, rootAfter, 0, -MoveWindowDeltaPx)
                    && SameRectShift(viewport0, viewportAfter, 0, -MoveWindowDeltaPx)
                    && SameRectShift(input0, inputAfter, 0, -MoveWindowDeltaPx);
            });
            if (!verified)
            {
                return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                    "row/root/viewport did not move by the applied delta", "move-verify");
            }

            if (!CaptureOrStop("r29b-moved", "capture-moved"))
            {
                return false;
            }

            if (Stop != ChildSurfaceStop.None)
            {
                return false;
            }

            var restored = RestoreRootOriginVerified(root0, b, row0, "move-restore");
            if (!restored)
            {
                return false;
            }

            restoreConfirmed = true;
            return CaptureOrStop("r29b-move-restored", "move-restore-capture");
        }
        finally
        {
            if (mutationMayExist && !restoreConfirmed)
            {
                RestoreRootOriginBestEffort(root0, "move-finally-restore");
            }
        }
    }

    // ---- 受管 body 内的共享判定 ---------------------------------------

    /// <summary>
    /// 在已派发的 UI body 内执行安全点+不变量复核；通过返回 null，否则返回
    /// 可读违规描述（首个失败已记入 <see cref="Stop"/>）。三个场景的受管动作体共用。
    /// </summary>
    private string? GuardInsideBody(string stage, LifecycleSnapshot baseline)
    {
        var probe = _host.Probe(stage);
        if (!probe.Ok)
        {
            StopWith(ChildSurfaceStop.SafePointLost, probe.Detail, stage);
            return probe.Detail;
        }

        var violation = probe.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            StopWith(ChildSurfaceStop.CompositionChanged, violation, stage);
            return violation;
        }

        return null;
    }

    /// <summary>
    /// R31 复审：在实际安全点/等待边界/证据截图的<b>同一次 UI body</b>内，
    /// 读回真实 TSF 焦点并与<b>当前受控阶段</b>期望比对。期望阶段只在一次已验证
    /// 的 suspend/restore 成功后由本执行器经
    /// <see cref="IChildSurfaceGeometryHost.TsfFocusStageState"/> 迁移；读回不符
    /// 或中途失去归属时阶段保持不动，使 postguard/清理按<b>实际</b>阶段核对。
    /// 不注入时宿主默认如实失败（未接线宿主不进入本路径）。
    /// </summary>
    /// <param name="stage">诊断阶段名（写入日志/停止原因）。</param>
    /// <param name="expected">本安全点允许的阶段——通常与阶段跟踪器当前值一致；
    /// suspend/restore 动作体内的复核才允许跨阶段（NULL→SAME_OWN）。</param>
    private bool GuardTsfPhase(string stage, TsfFocusPhase expected)
    {
        var actual = _host.ReadTsfFocusPhase();
        _host.Record("tsf-focus-phase", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["expected"] = expected.ToString(),
            ["actual"] = actual.Actual.ToString(),
            ["ok"] = actual.Ok && actual.Actual == expected,
            ["reason"] = actual.Reason,
            ["tracked"] = _host.TsfFocusStageState.Describe(),
        });
        if (!actual.Ok)
        {
            return StopWith(ChildSurfaceStop.TsfFocusPhaseMismatch,
                $"tsf focus phase read failed at {stage}: {actual.Reason}", stage);
        }

        if (actual.Actual != expected)
        {
            return StopWith(ChildSurfaceStop.TsfFocusPhaseMismatch,
                $"tsf focus phase mismatch at {stage}: expected={expected} actual={actual.Actual} "
                    + $"({actual.Reason})", stage);
        }

        return true;
    }

    /// <summary>当前受控阶段跟踪器（宿主注入；测试替身与真实路径共用同一对象）。</summary>
    private TsfFocusStage Stage => _host.TsfFocusStageState;

    /// <summary>当前阶段期望的 TSF 焦点阶段。</summary>
    private TsfFocusPhase ExpectedPhase => Stage.Phase;

    /// <summary>
    /// <b>共享的受管测量/验证派发</b>：在<b>同一个</b> <see cref="IChildSurfaceGeometryHost.InvokeOnUiThread"/>
    /// 调用内依次完成 前置安全点+不变量复核 → <paramref name="body"/>（几何读取或真实动作）
    /// → 后置复核。任一前置失效即不执行 body；body 抛异常如实记
    /// <see cref="ChildSurfaceStop.ActionException"/> 且不会触发后置复核。
    /// 真实宿主与测试替身走的就是这个函数——不是各自实现。
    /// </summary>
    private bool DispatchMeasured(string preStage, string postStage,
        LifecycleSnapshot baseline, Func<bool> body)
    {
        try
        {
            return _host.InvokeOnUiThread(() =>
            {
                if (GuardInsideBody(preStage, baseline) is not null)
                {
                    return false;
                }

                bool acted;
                try
                {
                    acted = body();
                }
                catch (Exception ex)
                {
                    StopWith(ChildSurfaceStop.ActionException,
                        $"{ex.GetType().Name}: {ex.Message}", preStage);
                    return false;
                }

                if (!acted)
                {
                    return false;
                }

                return GuardInsideBody(postStage, baseline) is null;
            });
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.ActionException,
                $"ui dispatch threw: {ex.GetType().Name}: {ex.Message}", preStage);
        }
    }

    /// <summary>读取一次安全点；失败即置 SafePointLost 并返回 null。Probe 自身抛出由 Run 兜底。</summary>
    private SafePointProbe? ProbeOrStop(string stage)
    {
        var probe = _host.Probe(stage);
        if (!probe.Ok)
        {
            StopWith(ChildSurfaceStop.SafePointLost, probe.Detail, stage);
            return null;
        }

        return probe;
    }

    private bool RequireComposingBaseline(LifecycleSnapshot snapshot, string stage)
    {
        if (snapshot.Composing)
        {
            return true;
        }

        return StopWith(ChildSurfaceStop.CompositionChanged,
            "baseline probe is not composing", stage);
    }

    /// <summary>
    /// 证据截图：返回 false 记 <see cref="ChildSurfaceStop.CaptureFailed"/>；
    /// 截图底层抛异常（真实路径的 I/O 失败）如实记
    /// <see cref="ChildSurfaceStop.ActionException"/>——两种都判失败，绝不继续。
    /// </summary>
    private bool CaptureOrStop(string name, string stage)
    {
        CaptureVerdict verdict;
        try
        {
            verdict = _host.Capture(name);
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.ActionException,
                $"capture '{name}' threw: {ex.GetType().Name}: {ex.Message}", stage);
        }

        if (!verdict.Ok)
        {
            // 停止于首个失败：管线内每张抓图的前置/后置复核与阶段读回已把根因
            // 捕获在 verdict（并记入 child-capture-stage 日志）——这里只消费该
            // 结构化首败原因做分类，绝不再发任何 TSF/profile 复探。
            if (verdict.PhaseMismatch)
            {
                return StopWith(ChildSurfaceStop.TsfFocusPhaseMismatch,
                    $"capture '{name}' rejected: {verdict.Reason}", stage);
            }

            return StopWith(ChildSurfaceStop.CaptureFailed,
                $"capture '{name}' failed: {verdict.Reason}", stage);
        }

        // R31 复审：每张证据截图之后立刻读回真实 TSF 焦点阶段——
        // 挂起期间焦点自动回到 OWN 的截图不作为有效证据。
        if (_suspendTsfFocusWhenClipped && !GuardTsfPhase(stage + "-phase", ExpectedPhase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// UI 派发器之外的等待：<b>等待前</b>先复核安全点与完整不变量，再等待，
    /// <b>等待后</b>再复核一次——任一失效如实停止，等待本身不搬进 UI 线程。
    /// Wait/Probe 抛出的异常由调用方场景的 try/finally 与 <see cref="Run"/> 兜底；
    /// 已停止（首个失败）时调用方保证不再进入本方法。
    /// </summary>
    private bool WaitProbed(string stage, int ms, LifecycleSnapshot baseline)
    {
        var before = _host.Probe(stage + "-before");
        if (!before.Ok)
        {
            return StopWith(ChildSurfaceStop.SafePointLost, before.Detail, stage);
        }

        var violation = before.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            return StopWith(ChildSurfaceStop.CompositionChanged, violation, stage);
        }

        // R31 复审：等待前的最后一次阶段核对（等待期间焦点可能自动回 OWN）。
        if (_suspendTsfFocusWhenClipped && !GuardTsfPhase(stage + "-before", ExpectedPhase))
        {
            return false;
        }

        _host.Wait(ms);

        var after = _host.Probe(stage + "-after");
        if (!after.Ok)
        {
            return StopWith(ChildSurfaceStop.SafePointLost, after.Detail, stage);
        }

        violation = after.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            return StopWith(ChildSurfaceStop.CompositionChanged, violation, stage);
        }

        // R31 复审：等待后的阶段核对——600ms 内 TSF 自动回 OWN 即如实判失败，
        // 绝不把回到 OWN 的截图当挂起证据。
        if (_suspendTsfFocusWhenClipped && !GuardTsfPhase(stage + "-after", ExpectedPhase))
        {
            return false;
        }

        return true;
    }

    // ---- 恢复 ----------------------------------------------------------

    /// <summary>
    /// 失败路径的尽力而为几何回滚（允许的安全自有恢复）：恢复保存的原始 X/Y
    /// 偏移并读回复核，结果记恢复诊断；不覆盖首个停止原因，绝不抛出中断清理。
    /// 只做自有几何回写+读回——<b>不调用 _host.Probe</b>（真实 Probe 会查询
    /// 活动 profile COM，首个失败后不得再发 TSF/profile 探测）。
    /// </summary>
    private void RestoreScrollBestEffort(int savedX, int savedY, string stage)
    {
        try
        {
            var ok = _host.InvokeOnUiThread(() =>
            {
                _host.SetScrollOffset(savedX, savedY);
                var x = _host.GetScrollX();
                var y = _host.GetScrollY();
                return x == savedX && y == savedY;
            });
            if (!ok)
            {
                RecordRestoreDiagnostic(stage,
                    $"best-effort scroll restore could not confirm ({savedX},{savedY})");
            }
        }
        catch (Exception ex)
        {
            RecordRestoreDiagnostic(stage,
                $"best-effort scroll restore threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 成功路径的恢复：恢复保存的原始 X/Y → 沉降等待 → 精确复核
    /// 滚动偏移与行/viewport/input 矩形逐一等于初始值。任一失败如实记
    /// RestoreFailed 并返回 false——恢复失败同样使实验失败。
    /// </summary>
    private bool RestoreScrollVerified(int savedX, int savedY, LifecycleSnapshot baseline,
        TsfRect row0, TsfRect viewport0, TsfRect input0, string stage)
    {
        var applied = false;
        try
        {
            applied = _host.InvokeOnUiThread(() =>
            {
                _host.SetScrollOffset(savedX, savedY);
                return true;
            });
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"scroll restore threw: {ex.GetType().Name}: {ex.Message}", stage);
        }

        if (!applied)
        {
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"SetScrollOffset({savedX},{savedY}) could not run (window lost)", stage);
        }

        // 恢复沉降等待与测量等待同等待遇：前后都复核安全点+完整不变量，
        // 等待期间丢失组合/前台/焦点如实停止（raw Wait 无法发现）。
        try
        {
            if (!WaitProbed(stage + "-settle", RestoreSettleMs, baseline))
            {
                return StopIfNone(ChildSurfaceStop.RestoreFailed,
                    "scroll restore settle probe failed", stage);
            }
        }
        catch (Exception ex)
        {
            // 恢复已写回但沉降等待/复核抛异常——恢复阶段异常同样如实判失败。
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"scroll restore settle wait threw: {ex.GetType().Name}: {ex.Message}", stage);
        }

        var verified = DispatchMeasured(stage + "-verify-pre", stage + "-verify-post", baseline, () =>
        {
            var x = _host.GetScrollX();
            var y = _host.GetScrollY();
            var row = _host.GetTextRowScreenRect();
            var viewport = _host.GetViewportClientScreenRect();
            var input = _host.GetInputClientScreenRect();
            return x == savedX && y == savedY
                && SameRect(row, row0)
                && SameRect(viewport, viewport0)
                && SameRect(input, input0);
        });
        if (!verified)
        {
            return StopIfNone(ChildSurfaceStop.RestoreFailed,
                $"restored geometry != saved originals (x={savedX},y={savedY})", stage);
        }

        return true;
    }

    /// <summary>
    /// 失败路径的 root 绝对原点回滚（尽力而为）：未移动时是无害 no-op，
    /// 部分移动时恢复到保存原点；不操作已不属于本进程/UI 线程的 HWND，
    /// 不调用 _host.Probe——首个失败后只做自有几何写回+读回。
    /// </summary>
    private void RestoreRootOriginBestEffort(TsfRect saved, string stage)
    {
        try
        {
            var ok = _host.InvokeOnUiThread(() =>
            {
                _host.SetRootWindowOrigin(saved.Left, saved.Top);
                var after = _host.GetRootWindowScreenRect();
                return NonEmpty(after) && after.Left == saved.Left && after.Top == saved.Top;
            });
            if (!ok)
            {
                RecordRestoreDiagnostic(stage,
                    $"best-effort root restore could not confirm ({saved.Left},{saved.Top})");
            }
        }
        catch (Exception ex)
        {
            RecordRestoreDiagnostic(stage,
                $"best-effort root restore threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 成功路径的 root 恢复：写回保存的绝对原点 → 沉降等待 → 复核 root 原点
    /// 与文本行矩形精确等于初始值。失败记 RestoreFailed。
    /// </summary>
    private bool RestoreRootOriginVerified(TsfRect saved, LifecycleSnapshot baseline,
        TsfRect row0, string stage)
    {
        var applied = false;
        try
        {
            applied = _host.InvokeOnUiThread(() =>
            {
                _host.SetRootWindowOrigin(saved.Left, saved.Top);
                return true;
            });
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"root restore threw: {ex.GetType().Name}: {ex.Message}", stage);
        }

        if (!applied)
        {
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"SetRootWindowOrigin({saved.Left},{saved.Top}) could not run (window lost)", stage);
        }

        // 恢复沉降等待与测量等待同等待遇：前后都复核安全点+完整不变量。
        try
        {
            if (!WaitProbed(stage + "-settle", RestoreSettleMs, baseline))
            {
                return StopIfNone(ChildSurfaceStop.RestoreFailed,
                    "root restore settle probe failed", stage);
            }
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.RestoreFailed,
                $"root restore settle wait threw: {ex.GetType().Name}: {ex.Message}", stage);
        }

        var verified = DispatchMeasured(stage + "-verify-pre", stage + "-verify-post", baseline, () =>
        {
            var after = _host.GetRootWindowScreenRect();
            var row = _host.GetTextRowScreenRect();
            return NonEmpty(after)
                && after.Left == saved.Left && after.Top == saved.Top
                && after.Right == saved.Right && after.Bottom == saved.Bottom
                && SameRect(row, row0);
        });
        if (!verified)
        {
            return StopIfNone(ChildSurfaceStop.RestoreFailed,
                $"restored root origin != saved ({saved.Left},{saved.Top})", stage);
        }

        return true;
    }

    /// <summary>
    /// 记录恢复阶段的尽力而为诊断；不改变已置的首个停止原因。
    /// 记录自身失败不得中断几何回滚或把失败翻成成功——吞掉并继续。
    /// </summary>
    private void RecordRestoreDiagnostic(string stage, string detail)
    {
        try
        {
            _host.Record("child-surface-restore-diagnostic", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["detail"] = detail,
                ["existing_stop"] = Stop.ToString(),
            });
        }
        catch
        {
            // 日志通道故障不升级、不掩盖：实验结果已由 Stop/返回值决定。
        }
    }

    /// <summary>首个失败置停止；已停止时只记录后续诊断，不覆盖首个停止原因。
    /// R31 复审：<see cref="_firstFailureDetail"/> 在 Stop 首次非 None 时固定——
    /// 之后的恢复/清理错误只追加诊断，绝不覆盖首个失败。
    /// 日志写入自身抛异常绝不向外传播（finally 回滚与结果判定优先于日志）。</summary>
    private bool StopWith(ChildSurfaceStop stop, string detail, string stage)
    {
        if (Stop == ChildSurfaceStop.None)
        {
            Stop = stop;
            StopDetail = detail;
            StoppedAtStage = stage;
            _firstFailureDetail = detail;
        }
        try
        {
            _host.Record("child-surface-stop", new Dictionary<string, object?>
            {
                ["stop"] = stop.ToString(),
                ["stage"] = stage,
                ["detail"] = detail,
            });
        }
        catch
        {
            // 记录失败不得把已判失败翻成成功，也不得阻断 finally 的几何回滚。
        }
        return false;
    }

    /// <summary>仅当尚未停止时按该原因停止；已停止则保持首个失败。</summary>
    private bool StopIfNone(ChildSurfaceStop stop, string detail, string stage)
    {
        if (Stop != ChildSurfaceStop.None)
        {
            return false;
        }

        return StopWith(stop, detail, stage);
    }

    private static bool SameRect(TsfRect a, TsfRect b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static bool SameRectShift(TsfRect a, TsfRect b, int dx, int dy)
        => b.Left == a.Left + dx && b.Top == a.Top + dy
            && b.Right == a.Right + dx && b.Bottom == a.Bottom + dy;

    /// <summary>非空且正向的矩形（宽高都 >0）；空/倒置矩形不是有效几何。</summary>
    private static bool NonEmpty(TsfRect r)
        => r.Right > r.Left && r.Bottom > r.Top;

    /// <summary>四边严格全零——IsEmpty 会把 {7,8,7,8} 这类非零空矩形误当合法。</summary>
    private static bool IsExactZero(TsfRect r)
        => r.Left == 0 && r.Top == 0 && r.Right == 0 && r.Bottom == 0;
}
