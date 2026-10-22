using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

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
}

/// <summary>
/// R29B opt-in 几何场景的宿主抽象。真实实现（ScenarioRunner 内的包装）与托管测试替身
/// 走<b>同一套</b>执行器控制流——纯托管测试驱动与真实窗口相同的
/// 安全点→不变量→动作→复核→截图→恢复 顺序，因此测试覆盖的就是生产控制流。
/// 所有方法都保证在自有 UI 线程被调用（由 <see cref="InvokeOnUiThread"/> 派发的 body 内）。
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
    /// 返回 false 表示截图不作为有效证据，执行器必须停止后续阶段。
    /// </summary>
    bool Capture(string name);

    /// <summary>暂停指定毫秒。</summary>
    void Wait(int milliseconds);

    /// <summary>当前垂直滚动量（真实路径 = <c>_scrollY</c>，物理像素）。</summary>
    int GetScrollY();

    /// <summary>按**保存的**滚动偏移恢复（scroll-follow 精确回滚，不重算 delta）。</summary>
    void SetScrollOffset(int x, int y);

    /// <summary>按逻辑单位滚动，返回实际物理像素（scroll-follow）。</summary>
    int ScrollByLogical(int logicalDelta);

    /// <summary>按物理像素滚动（clip-out 把 input client 推出 viewport）。</summary>
    void ScrollByPhysical(int deltaY);

    /// <summary>整窗平移（move-window 正向控制；SWP_NOACTIVATE，不改焦点/前台）。</summary>
    void MoveRootBy(int dx, int dy);

    /// <summary>发布布局中的文本行屏幕矩形（scroll-follow 跟随证据）。</summary>
    TsfRect GetTextRowScreenRect();

    /// <summary>真实可见区域 = input ∩ 全链裁剪祖先的屏幕矩形（与 TSF 查询同源）。</summary>
    TsfRect GetInputClientScreenRect();

    /// <summary>clip 祖先（viewport）client 屏幕矩形；clip-out 的固定参照。</summary>
    TsfRect GetViewportClientScreenRect();

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
/// 每个阶段都在 UI 线程的**单个**安全点内完成：先核验自有前台/焦点/锁/重入与完整
/// 组合不变量（text/span/composing/writes/profile/连续性计数），再执行真实几何动作，
/// 动作后再做一次同样的安全点读取。任何偏差立即停止并发回如实失败——
/// 不夺焦、不补发键、不假装动作成功。
/// </para>
/// <para>
/// 截图只在安全点+不变量同时成立时采集；任一复核失败即返回 false，证据不记为有效。
/// 所有恢复（scroll 精确回滚 / clip-out 反向 / move-window 反向）都在 <c>finally</c>
/// 中执行——即使验证失败或抛异常也恢复最初几何，且恢复期间不做焦点变更。
/// </para>
/// </summary>
internal sealed class ChildSurfaceGeometryExecutor
{
    /// <summary>clip-out 要求 input client bottom 至少高出 viewport top 的物理像素。</summary>
    internal const int ClipOutMarginPx = 40;

    /// <summary>move-window 正向控制位移（物理像素）。</summary>
    internal const int MoveWindowDeltaPx = 24;

    private readonly IChildSurfaceGeometryHost _host;
    private readonly Action<LifecycleSnapshot> _baselineSink;

    internal ChildSurfaceGeometryExecutor(IChildSurfaceGeometryHost host,
        Action<LifecycleSnapshot> baselineSink)
    {
        _host = host;
        _baselineSink = baselineSink;
    }

    internal LifecycleSnapshot? Baseline { get; private set; }

    internal ChildSurfaceStop Stop { get; private set; } = ChildSurfaceStop.None;

    internal string StopDetail { get; private set; } = "(none)";

    internal string StoppedAtStage { get; private set; } = "(none)";

    /// <summary>
    /// 执行 opt-in 几何场景：基线安全点 → 基线截图 → 动作+复核+截图 → 恢复+复核+截图。
    /// 返回 true 表示全部几何/组合检查通过（视觉仍 pending）。
    /// </summary>
    internal bool Run(ChildSurfaceScenario scenario, int logicalDelta)
    {
        // 1) 基线：同一安全点取得组合快照；必须是“组合中、零写入、已核验微软拼音”的合法起点。
        var baselineProbe = _host.Probe("baseline");
        if (!baselineProbe.Ok)
        {
            return StopWith(ChildSurfaceStop.SafePointLost, baselineProbe.Detail, "baseline");
        }

        var baseline = baselineProbe.Snapshot;
        if (!baseline.Composing)
        {
            return StopWith(ChildSurfaceStop.CompositionChanged,
                "no active composition at baseline", "baseline");
        }

        if (baseline.Writes != 0)
        {
            return StopWith(ChildSurfaceStop.CompositionChanged,
                $"binding writes already non-zero at baseline ({baseline.Writes})", "baseline");
        }

        if (!ImeIdentity.IsVerifiableMicrosoftPinyin(baseline.Profile))
        {
            return StopWith(ChildSurfaceStop.SafePointLost,
                $"active profile not verifiable at baseline ('{baseline.Profile}')", "baseline");
        }

        Baseline = baseline;
        _baselineSink(baseline);

        // 2) 基线证据：安全点+不变量成立后才截图。
        if (!_host.Capture("r29b-baseline"))
        {
            return StopWith(ChildSurfaceStop.CaptureFailed,
                "baseline capture failed", "baseline");
        }

        try
        {
            return scenario switch
            {
                ChildSurfaceScenario.ScrollFollow => RunScrollFollow(baseline, logicalDelta),
                ChildSurfaceScenario.ClipOut => RunClipOut(baseline),
                ChildSurfaceScenario.MoveWindow => RunMoveWindow(baseline),
                _ => StopWith(ChildSurfaceStop.Unsupported,
                    $"unsupported scenario '{scenario}'", "dispatch"),
            };
        }
        catch (Exception ex)
        {
            return StopWith(ChildSurfaceStop.ActionException,
                $"{scenario} threw {ex.GetType().Name}: {ex.Message}", "action");
        }
    }

    // ---------------------------------------------------------------
    // scroll-follow：ScrollByLogical → input client 与文本行同量位移、viewport 不动。
    // ---------------------------------------------------------------
    private bool RunScrollFollow(LifecycleSnapshot baseline, int logicalDelta)
    {
        var savedScrollY = _host.GetScrollY();
        var inputBefore = _host.GetInputClientScreenRect();
        var rowBefore = _host.GetTextRowScreenRect();
        var viewportBefore = _host.GetViewportClientScreenRect();

        var restored = false;
        var ok = false;
        string detail;
        try
        {
            var physical = _host.ScrollByLogical(logicalDelta);
            _host.Wait(500);

            var after = GuardedProbe("scroll-after", baseline, out var probeDetail);
            var inputAfter = _host.GetInputClientScreenRect();
            var rowAfter = _host.GetTextRowScreenRect();
            var viewportAfter = _host.GetViewportClientScreenRect();

            var inputMoved = SameRectShift(inputBefore, inputAfter, 0, -physical);
            var rowMoved = SameRectShift(rowBefore, rowAfter, 0, -physical);
            var viewportStable = SameRect(viewportBefore, viewportAfter);

            var captureOk = _host.Capture("r29b-scrolled");
            var post = GuardedProbe("scroll-post", baseline, out var postDetail);

            ok = post is not null && captureOk && inputMoved && rowMoved && viewportStable;
            detail = $"input {Rect(inputBefore)}->{Rect(inputAfter)} row {Rect(rowBefore)}->{Rect(rowAfter)} "
                + $"logical={logicalDelta} physical={physical} input_moved={inputMoved} "
                + $"row_moved={rowMoved} viewport_stable={viewportStable} capture={captureOk} "
                + (post is null ? $"probe_fail={postDetail} " : string.Empty)
                + (after is null ? $"probe_after_fail={probeDetail} " : string.Empty);

            if (!ok && Stop == ChildSurfaceStop.None)
            {
                StopWith(ChildSurfaceStop.GeometryCheckFailed, detail, "scroll-after");
            }
        }
        finally
        {
            // 精确回滚到**保存的**原始滚动偏移（不是按 delta 反推）；失败/异常也恢复，
            // 恢复本身不改焦点/前台。
            _host.SetScrollOffset(0, savedScrollY);
            var restoreProbe = GuardedProbe("scroll-restored", baseline, out _);
            restored = restoreProbe is not null;
            _host.Capture("r29b-scroll-restored");
        }

        return ok && restored;
    }

    // ---------------------------------------------------------------
    // clip-out：把**整个** input client 推出 viewport client 顶边 ≥40 物理像素，
    // 验证 GetScreenExt/GetTextExt 在无锁/读锁下如实报告零矩形；再恢复原几何。
    // ---------------------------------------------------------------
    private bool RunClipOut(LifecycleSnapshot baseline)
    {
        var savedScrollY = _host.GetScrollY();
        var viewportBefore = _host.GetViewportClientScreenRect();
        var inputBefore = _host.GetInputClientScreenRect();
        var rowBefore = _host.GetTextRowScreenRect();

        var restored = false;
        var ok = false;
        string detail;
        try
        {
            // 使整个 input client（不只是文本行）完全高于 viewport 顶边。
            var delta = Math.Max(1, inputBefore.Bottom - viewportBefore.Top + ClipOutMarginPx);
            _host.ScrollByPhysical(delta);
            _host.Wait(600);

            var after = GuardedProbe("clip-after", baseline, out var probeDetail);
            var inputAfter = _host.GetInputClientScreenRect();
            var viewportAfter = _host.GetViewportClientScreenRect();

            var clippedOut = inputAfter.Bottom <= viewportAfter.Top
                && viewportAfter.Top - inputAfter.Bottom >= ClipOutMarginPx;

            var screenHr = _host.GetScreenExt(out var screenRect);
            var spanStart = baseline.SpanStart;
            var spanEnd = baseline.SpanEnd;
            var textHr = _host.GetTextExt(spanStart, spanEnd, out var textRect, out var clipped);

            var screenZero = screenHr == 0 && screenRect.IsEmpty;
            var textClipped = textHr == 0 && textRect.IsEmpty && clipped != 0;

            var captureOk = _host.Capture("r29b-clipped");
            var post = GuardedProbe("clip-post", baseline, out var postDetail);

            ok = post is not null && captureOk && clippedOut && screenZero && textClipped;
            detail = $"input {Rect(inputBefore)}->{Rect(inputAfter)} viewport_top {viewportBefore.Top}->{viewportAfter.Top} "
                + $"delta={delta} clipped_out={clippedOut} screen_hr=0x{screenHr:X8} "
                + $"screen_rect={Rect(screenRect)} text_hr=0x{textHr:X8} "
                + $"text_rect={Rect(textRect)} clipped={clipped} "
                + $"screen_zero={screenZero} text_clipped={textClipped} capture={captureOk} "
                + (post is null ? $"probe_fail={postDetail} " : string.Empty)
                + (after is null ? $"probe_after_fail={probeDetail} " : string.Empty);

            if (!ok && Stop == ChildSurfaceStop.None)
            {
                StopWith(ChildSurfaceStop.GeometryCheckFailed, detail, "clip-after");
            }
        }
        finally
        {
            // 用与动作**相同**的组合路径反向滚动恢复最初几何；失败/异常也恢复，
            // 恢复期间绝不触碰原生候选/不抑制/不改焦点。
            _host.SetScrollOffset(0, savedScrollY);
            _host.Wait(600);
            var restoreProbe = GuardedProbe("clip-restored", baseline, out _);
            var inputRestored = _host.GetInputClientScreenRect();
            restored = restoreProbe is not null && SameRect(inputBefore, inputRestored);
            _host.Capture("r29b-clip-restored");
        }

        return ok && restored;
    }

    // ---------------------------------------------------------------
    // move-window 正向控制：整窗平移，同守卫/截图/恢复，不强制激活。
    // ---------------------------------------------------------------
    private bool RunMoveWindow(LifecycleSnapshot baseline)
    {
        var inputBefore = _host.GetInputClientScreenRect();
        var rowBefore = _host.GetTextRowScreenRect();

        var restored = false;
        var ok = false;
        string detail;
        try
        {
            _host.MoveRootBy(0, -MoveWindowDeltaPx);
            _host.Wait(600);

            var after = GuardedProbe("move-after", baseline, out var probeDetail);
            var inputAfter = _host.GetInputClientScreenRect();
            var rowAfter = _host.GetTextRowScreenRect();

            var inputMoved = SameRectShift(inputBefore, inputAfter, 0, -MoveWindowDeltaPx);
            var rowMoved = SameRectShift(rowBefore, rowAfter, 0, -MoveWindowDeltaPx);

            var captureOk = _host.Capture("r29b-moved");
            var post = GuardedProbe("move-post", baseline, out var postDetail);

            ok = post is not null && captureOk && inputMoved && rowMoved;
            detail = $"input {Rect(inputBefore)}->{Rect(inputAfter)} row {Rect(rowBefore)}->{Rect(rowAfter)} "
                + $"dy={MoveWindowDeltaPx} input_moved={inputMoved} row_moved={rowMoved} "
                + $"capture={captureOk} "
                + (post is null ? $"probe_fail={postDetail} " : string.Empty)
                + (after is null ? $"probe_after_fail={probeDetail} " : string.Empty);

            if (!ok && Stop == ChildSurfaceStop.None)
            {
                StopWith(ChildSurfaceStop.GeometryCheckFailed, detail, "move-after");
            }
        }
        finally
        {
            _host.MoveRootBy(0, MoveWindowDeltaPx);
            var restoreProbe = GuardedProbe("move-restored", baseline, out _);
            var inputRestored = _host.GetInputClientScreenRect();
            restored = restoreProbe is not null && SameRect(inputBefore, inputRestored);
            _host.Capture("r29b-move-restored");
        }

        return ok && restored;
    }

    /// <summary>
    /// 安全点 + 基线不变量复核的组合查询：返回 null 表示安全点或不变量失败（并置 Stop）。
    /// </summary>
    private LifecycleSnapshot? GuardedProbe(string stage, LifecycleSnapshot baseline,
        out string detail)
    {
        var probe = _host.Probe(stage);
        if (!probe.Ok)
        {
            detail = probe.Detail;
            if (Stop == ChildSurfaceStop.None)
            {
                StopWith(ChildSurfaceStop.SafePointLost, detail, stage);
            }
            return null;
        }

        var violation = probe.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            detail = violation;
            _host.Record("child-surface-invariant", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["baseline"] = baseline.Describe(),
                ["observed"] = probe.Snapshot.Describe(),
                ["violation"] = violation,
            });
            if (Stop == ChildSurfaceStop.None)
            {
                StopWith(ChildSurfaceStop.CompositionChanged,
                    $"{stage}: invariant violated — {violation}", stage);
            }
            return null;
        }

        detail = probe.Detail;
        return probe.Snapshot;
    }

    private bool StopWith(ChildSurfaceStop stop, string detail, string stage)
    {
        if (Stop == ChildSurfaceStop.None)
        {
            Stop = stop;
            StopDetail = detail;
            StoppedAtStage = stage;
        }
        _host.Record("child-surface-stop", new Dictionary<string, object?>
        {
            ["stop"] = stop.ToString(),
            ["stage"] = stage,
            ["detail"] = detail,
        });
        return false;
    }

    private static bool SameRect(TsfRect a, TsfRect b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static bool SameRectShift(TsfRect a, TsfRect b, int dx, int dy)
        => b.Left == a.Left + dx && b.Top == a.Top + dy
            && b.Right == a.Right + dx && b.Bottom == a.Bottom + dy;

    private static string Rect(TsfRect r) => r.ToString();
}
