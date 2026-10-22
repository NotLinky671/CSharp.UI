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
    /// 返回 false 表示截图不作为有效证据，执行器必须停止后续阶段。
    /// </summary>
    bool Capture(string name);

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
/// 首个失败不可恢复：任一安全点/不变量/截图/几何/恢复检查失败即置
/// <see cref="Stop"/>；之后只允许恢复自有几何与测试结束清理，不再做任何
/// 测量/TSF 查询/等待/截图。<see cref="Run"/> 只有 <see cref="Stop"/>==
/// <see cref="ChildSurfaceStop.None"/> 且全部检查成功才返回 true——恢复截图失败、
/// 恢复几何偏差同样如实判失败，首个停止原因不被后续恢复覆盖。
/// </para>
/// <para>
/// 恢复使用保存的**原始** X/Y 滚动偏移与 root <b>绝对原点</b>（非盲目反向增量）——
/// 动作抛异常或只部分移动时反向增量会把窗口移到错误位置。恢复期间不做焦点恢复、
/// 不操作已不属于本进程/UI 线程的 HWND。
/// </para>
/// </summary>
internal sealed class ChildSurfaceGeometryExecutor
{
    /// <summary>clip-out 要求 input client bottom 至少高出 viewport top 的物理像素。</summary>
    internal const int ClipOutMarginPx = 40;

    /// <summary>move-window 正向控制位移（物理像素）。</summary>
    internal const int MoveWindowDeltaPx = 24;

    /// <summary>恢复后让原生事实沉降的等待（UI 派发器之外）。</summary>
    private const int RestoreSettleMs = 400;

    private readonly IChildSurfaceGeometryHost _host;
    private readonly Action<LifecycleSnapshot> _baselineSink;

    internal ChildSurfaceGeometryExecutor(IChildSurfaceGeometryHost host,
        Action<LifecycleSnapshot> baselineSink)
    {
        _host = host;
        _baselineSink = baselineSink;
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
    /// </summary>
    internal bool Run(ChildSurfaceScenario scenario, int logicalDelta)
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
        if (!_host.Capture("r29b-baseline"))
        {
            return StopWith(ChildSurfaceStop.CaptureFailed, "baseline capture failed", "capture-baseline");
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

        // 测量：前置复核 + 真实滚动 + 同 body 后置复核，三者不可拆分。
        var physical = 0;
        var measured = DispatchMeasured("scroll-action-before", "scroll-action-after", b, () =>
        {
            physical = _host.ScrollByLogical(logicalDelta);
            return true;
        });
        if (!measured)
        {
            RestoreScrollBestEffort(savedX, savedY, "scroll-after-guard");
            return false;
        }

        if (!WaitProbed("scroll-settle", 500, b))
        {
            RestoreScrollBestEffort(savedX, savedY, "scroll-after-wait");
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
            RestoreScrollBestEffort(savedX, savedY, "scroll-after-check");
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                $"row/viewport/offset mismatch after scroll (physical={physical})", "scroll-verify");
        }

        if (!_host.Capture("r29b-scrolled"))
        {
            RestoreScrollBestEffort(savedX, savedY, "scroll-after-capture");
            return StopWith(ChildSurfaceStop.CaptureFailed, "post-scroll capture failed", "capture-scrolled");
        }

        var restored = RestoreScrollVerified(savedX, savedY, b, row0, viewport0, input0, "scroll-restore");
        var restoreCapture = _host.Capture("r29b-scroll-restored");
        if (!restoreCapture)
        {
            RecordRestoreDiagnostic("scroll-restore-capture", "restore capture failed");
        }
        return restored && restoreCapture;
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
        if (!_host.Capture("r29b-baseline"))
        {
            return StopWith(ChildSurfaceStop.CaptureFailed, "baseline capture failed", "capture-baseline");
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

        var measured = DispatchMeasured("clip-action-before", "clip-action-after", b, () =>
        {
            _host.ScrollByPhysical(delta);
            return true;
        });
        if (!measured)
        {
            RestoreScrollBestEffort(savedX, savedY, "clip-after-guard");
            return false;
        }

        if (!WaitProbed("clip-settle", 500, b))
        {
            RestoreScrollBestEffort(savedX, savedY, "clip-after-wait");
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
            RestoreScrollBestEffort(savedX, savedY, "clip-after-check");
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                "clipped geometry/zero-rect/viewport check failed", "clip-verify");
        }

        if (!_host.Capture("r29b-clipped"))
        {
            RestoreScrollBestEffort(savedX, savedY, "clip-after-capture");
            return StopWith(ChildSurfaceStop.CaptureFailed, "post-clip capture failed", "capture-clipped");
        }

        var restored = RestoreScrollVerified(savedX, savedY, b, row0, viewport0, input0, "clip-restore");
        var restoreCapture = _host.Capture("r29b-clip-restored");
        if (!restoreCapture)
        {
            RecordRestoreDiagnostic("clip-restore-capture", "restore capture failed");
        }
        return restored && restoreCapture;
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
        if (!_host.Capture("r29b-baseline"))
        {
            return StopWith(ChildSurfaceStop.CaptureFailed, "baseline capture failed", "capture-baseline");
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

        var measured = DispatchMeasured("move-action-before", "move-action-after", b, () =>
        {
            _host.MoveRootBy(0, -MoveWindowDeltaPx);
            return true;
        });
        if (!measured)
        {
            // 绝对原点回滚：未移动时是无害 no-op；部分移动时恢复到保存位置。
            RestoreRootOriginBestEffort(root0, "move-after-guard");
            return false;
        }

        if (!WaitProbed("move-settle", 500, b))
        {
            RestoreRootOriginBestEffort(root0, "move-after-wait");
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
            RestoreRootOriginBestEffort(root0, "move-after-check");
            return StopIfNone(ChildSurfaceStop.GeometryCheckFailed,
                "row/root/viewport did not move by the applied delta", "move-verify");
        }

        if (!_host.Capture("r29b-moved"))
        {
            RestoreRootOriginBestEffort(root0, "move-after-capture");
            return StopWith(ChildSurfaceStop.CaptureFailed, "post-move capture failed", "capture-moved");
        }

        var restored = RestoreRootOriginVerified(root0, b, row0, "move-restore");
        var restoreCapture = _host.Capture("r29b-move-restored");
        if (!restoreCapture)
        {
            RecordRestoreDiagnostic("move-restore-capture", "restore capture failed");
        }
        return restored && restoreCapture;
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

    /// <summary>读取一次安全点；失败即置 SafePointLost 并返回 null。</summary>
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
    /// UI 派发器之外的等待：等待后重新复核安全点与完整不变量。
    /// 已停止（首个失败）时不再等待。
    /// </summary>
    private bool WaitProbed(string stage, int ms, LifecycleSnapshot baseline)
    {
        _host.Wait(ms);

        var probe = _host.Probe(stage + "-after");
        if (!probe.Ok)
        {
            return StopWith(ChildSurfaceStop.SafePointLost, probe.Detail, stage);
        }

        var violation = probe.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            return StopWith(ChildSurfaceStop.CompositionChanged, violation, stage);
        }

        return true;
    }

    // ---- 恢复 ----------------------------------------------------------

    /// <summary>
    /// 失败路径的尽力而为几何回滚（允许的安全自有恢复）：恢复保存的原始 X/Y
    /// 偏移并读回复核，结果记恢复诊断；不覆盖首个停止原因，绝不抛出中断清理。
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
                _ = _host.Probe(stage + "-after"); // 仅诊断，不改停止状态。
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

        _host.Wait(RestoreSettleMs);

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
    /// 部分移动时恢复到保存原点；不操作已不属于本进程/UI 线程的 HWND。
    /// </summary>
    private void RestoreRootOriginBestEffort(TsfRect saved, string stage)
    {
        try
        {
            var ok = _host.InvokeOnUiThread(() =>
            {
                _host.SetRootWindowOrigin(saved.Left, saved.Top);
                var after = _host.GetRootWindowScreenRect();
                _ = _host.Probe(stage + "-after"); // 仅诊断。
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

        _host.Wait(RestoreSettleMs);

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

    /// <summary>记录恢复阶段的尽力而为诊断；不改变已置的首个停止原因。</summary>
    private void RecordRestoreDiagnostic(string stage, string detail)
    {
        _host.Record("child-surface-restore-diagnostic", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["detail"] = detail,
            ["existing_stop"] = Stop.ToString(),
        });
    }

    /// <summary>首个失败置停止；已停止时只记录后续诊断，不覆盖首个停止原因。</summary>
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
