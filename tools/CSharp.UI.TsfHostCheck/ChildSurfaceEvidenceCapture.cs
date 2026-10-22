namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R29B review-6：生命周期安全点在 opt-in child 模式下、profile COM 查询**之前**必须验证
/// 并**之后**复核的原生归属事实。所有值由单个注入的原生事实读取器在同一次 UI 派发
/// body 内读出；测试以替身事实驱动完全相同的门禁路径，绝不绕过真实判定。
/// </summary>
internal readonly record struct LifecycleWindowFacts(
    bool RootValid,
    bool InputValid,
    uint RootTid,
    uint RootPid,
    uint InputTid,
    uint InputPid,
    IntPtr Foreground,
    IntPtr Focus);

/// <summary>
/// 一次受控原生查询（profile 查询、root crop、monitor 位块传输）前后必须不变的
/// 环境事实。post 复核只比对事实——重入可能改变 store，这部分由快照/不变量另行覆盖。
/// </summary>
internal readonly record struct LifecycleFactSample(
    IntPtr Foreground,
    IntPtr Focus,
    uint LockType,
    int LayoutDepth);

/// <summary>
/// R29B review-6：child 安全点在 profile COM 查询**前后**共享的可测试门禁判定。
/// 生产 <see cref="ScenarioRunner.ProbeLifecycleSafePoint"/> 在读取原生事实后调用本判定；
/// 测试直接注入替身事实驱动<b>同一个</b>门禁——不是只在测试里使用的平行谓词。
/// </summary>
internal static class ChildSafePointOwnershipGate
{
    /// <summary>
    /// 校验一次原生事实采样：root 与 input HWND 存活、其 owning 线程同为
    /// <paramref name="uiThreadId"/> 且 owner 进程为 <paramref name="expectedPid"/>、
    /// 前台==root、焦点==<paramref name="expectedFocus"/>。child 模式必需；
    /// 默认 off 由 <see cref="LifecycleSafePointGate"/> 覆盖（root 即 input）。
    /// </summary>
    internal static bool OwnsChildSurface(in LifecycleWindowFacts facts,
        IntPtr root, IntPtr expectedFocus, uint uiThreadId, int expectedPid)
        => facts.RootValid && facts.InputValid
            && root != IntPtr.Zero && expectedFocus != IntPtr.Zero
            && facts.RootPid == expectedPid && facts.InputPid == expectedPid
            && facts.RootTid == uiThreadId && facts.InputTid == uiThreadId
            && facts.Foreground == root
            && facts.Focus == expectedFocus;

    /// <summary>
    /// 受控原生操作前后的环境事实复核：前台、焦点、store 锁、布局通知深度
    /// 都必须与操作前一致（焦点/前台瞬时离开再回来即不等——fail closed）。
    /// </summary>
    internal static bool SameFacts(in LifecycleFactSample before, in LifecycleFactSample after)
        => before.Foreground == after.Foreground
            && before.Focus == after.Focus
            && before.LockType == after.LockType
            && before.LayoutDepth == after.LayoutDepth;
}

/// <summary>真实捕获结果（路径 + 实际抓取矩形）；失败为 null。</summary>
internal readonly record struct EvidenceCaptureResult(string Path, TsfRect Rect);

/// <summary>
/// R29B review-6：单张证据抓图的一次性结果。任一前置/抓图/后置失败都如实记录
/// <see cref="Reason"/>——绝不会把一次失败的抓图当成证据，也绝不继续下一张。
/// </summary>
internal readonly record struct EvidenceStageResult(
    bool Ok,
    string Reason,
    string Stage,
    string? Path,
    TsfRect Rect);

/// <summary>
/// R29B review-6：真实 opt-in 路径与托管测试<b>共用</b>的逐张证据采集操作。
/// 每张实际截图（root crop、monitor）都由 <see cref="RunStage"/> 在同一个顺序内完成
/// 前置安全点 + 基线不变量复核 → 真实抓图 → 后置复核；任一失败/抛出即如实返回
/// <see cref="EvidenceStageResult"/> 失败，调用方立即停止，绝不发起后续抓图
/// （root 失败后永不执行 monitor 抓图）。真实宿主把 probe/capture 投递到 UI 线程；
/// 测试以替身注入同一函数——共享的是控制流本身，不是平行实现。
/// </summary>
internal static class ChildSurfaceEvidenceCapture
{
    /// <summary>
    /// 单张证据抓图。<paramref name="probe"/> 是真实安全点读取器（含 profile 查询与快照）；
    /// <paramref name="capture"/> 是真实抓图执行器（返回 null 表示抓图失败）。
    /// 二者都可抛出；抛出如实转成失败结果，绝不外逃。
    /// <para>
    /// R31 复审返修：<paramref name="phase"/> 是<b>当前执行器同一实例</b>的实际 TSF
    /// 焦点阶段读取器（共享 <see cref="TsfDocumentFocus.ReadPhase"/>），可选；
    /// 默认 null 时保持既有"仅组合/原生复核"行为。注入时：每张抓图<b>前</b>先读
    /// 实际阶段 → 组合/原生 probe；抓图<b>后</b>先读实际阶段 → 再做组合/原生
    /// 复核——COM 查询后的环境事实重验不被 stale 采样覆盖。任一阶段读回
    /// <c>Ok=false</c> 即本张证据失败，调用方停止后续抓图。
    /// </para>
    /// </summary>
    internal static EvidenceStageResult RunStage(
        string stage, string label,
        Func<SafePointProbe> probe,
        Func<EvidenceCaptureResult?> capture,
        LifecycleSnapshot baseline,
        Action<string, IDictionary<string, object?>> record,
        Func<TsfFocusPhaseResult>? phase = null)
    {
        // ---- 前置：实际 TSF 焦点阶段 → 安全点 + 与基线的完整不变量 ----
        if (phase is not null)
        {
            if (!ProbePhase(stage, label, "pre", phase, out var prePhaseFail))
            {
                Record(record, label, prePhaseFail);
                return prePhaseFail;
            }
        }

        SafePointProbe pre;
        try
        {
            pre = probe();
        }
        catch (Exception ex)
        {
            var failed = new EvidenceStageResult(false,
                $"pre-{label} safe point threw: {ex.GetType().Name}: {ex.Message}",
                stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        if (!pre.Ok)
        {
            var failed = new EvidenceStageResult(false,
                $"pre-{label} safe point failed: {pre.Detail}", stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        var violation = pre.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            var failed = new EvidenceStageResult(false,
                $"pre-{label} invariant violated: {violation}", stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        // ---- 抓图 ----
        EvidenceCaptureResult? captured;
        try
        {
            captured = capture();
        }
        catch (Exception ex)
        {
            var failed = new EvidenceStageResult(false,
                $"{label} capture threw: {ex.GetType().Name}: {ex.Message}",
                stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        if (captured is null)
        {
            var failed = new EvidenceStageResult(false,
                $"{label} capture failed", stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        // ---- 后置：先读实际 TSF 焦点阶段，再复核组合/原生安全点 + 不变量 ----
        // 顺序刻意为 phase→probe：阶段读回内部做真实 COM 查询（GetFocus/GetTop/
        // identity 指针比较），可能经 COM 重入改变前台/焦点；原生事实必须在
        // <b>阶段 COM 查询之后</b>采样，才能如实反映重入后的环境。
        if (phase is not null)
        {
            if (!ProbePhase(stage, label, "post", phase, out var postPhaseFail))
            {
                Record(record, label, postPhaseFail);
                return postPhaseFail;
            }
        }

        SafePointProbe post;
        try
        {
            post = probe();
        }
        catch (Exception ex)
        {
            var failed = new EvidenceStageResult(false,
                $"post-{label} safe point threw: {ex.GetType().Name}: {ex.Message}",
                stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        if (!post.Ok)
        {
            var failed = new EvidenceStageResult(false,
                $"post-{label} safe point failed: {post.Detail}", stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        violation = post.Snapshot.DescribeInvariantViolation(baseline);
        if (violation is not null)
        {
            var failed = new EvidenceStageResult(false,
                $"post-{label} invariant violated: {violation}", stage, null, default);
            Record(record, label, failed);
            return failed;
        }

        var done = new EvidenceStageResult(true, "ok", stage, captured.Value.Path, captured.Value.Rect);
        Record(record, label, done);
        return done;
    }

    /// <summary>
    /// R31 复审返修：一次实际 TSF 焦点阶段读取（root/monitor 每张前后各一次）。
    /// 读回 <c>Ok=false</c> 或读回阶段与<b>当前受控期望</b>不符都如实产出失败
    /// 结果（reason 含期望与实际）；阶段读取自身抛异常如实转成失败，绝不外逃。
    /// </summary>
    private static bool ProbePhase(string stage, string label, string when,
        Func<TsfFocusPhaseResult> phase, out EvidenceStageResult fail)
    {
        TsfFocusPhaseResult result;
        try
        {
            result = phase();
        }
        catch (Exception ex)
        {
            fail = new EvidenceStageResult(false,
                $"{when}-{label} tsf focus phase read threw: {ex.GetType().Name}: {ex.Message}",
                stage, null, default);
            return false;
        }

        if (!result.Ok)
        {
            fail = new EvidenceStageResult(false,
                $"{when}-{label} tsf focus phase failed: expected={result.Expected} "
                    + $"actual={result.Actual} ({result.Reason})",
                stage, null, default);
            return false;
        }

        fail = default;
        return true;
    }

    /// <summary>每张抓图都如实记有效/无效与原因；记录异常不影响控制流。</summary>
    private static void Record(Action<string, IDictionary<string, object?>> record,
        string label, EvidenceStageResult result)
    {
        try
        {
            record("child-capture-stage", new Dictionary<string, object?>
            {
                ["stage"] = result.Stage,
                ["label"] = label,
                ["valid"] = result.Ok,
                ["reason"] = result.Reason,
                ["path"] = result.Path ?? "(none)",
                ["rect"] = result.Ok ? result.Rect.ToString() : "(none)",
            });
        }
        catch
        {
            // 诊断记录失败绝不反转结果，也不阻断后续停止。
        }
    }
}
