namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R31 复审：期望的 TSF 文档焦点阶段。实验的每个受控安全点（探针、等待前后、
/// 每张证据截图前后、焦点操作各步骤）都必须实际读回 TSF 焦点并与<b>当前阶段</b>
/// 的期望值比较——OWN（自有 doc+top-context）、NULL（挂起后确实无焦点）、
/// SAME_OWN（恢复后与保存令牌同一 doc/context）。
/// </summary>
internal enum TsfFocusPhase
{
    /// <summary>期望当前持有自有 doc/context（实验基线/操作前）。</summary>
    OwnDocument,

    /// <summary>期望线程无任何文档焦点（suspend 之后）。</summary>
    None,

    /// <summary>期望仍持有<b>保存令牌所指同一个</b> doc/context（restore 之后）。</summary>
    SameAsSaved,
}

/// <summary>一次实际 TSF 焦点读回的阶段判定结果。</summary>
internal readonly record struct TsfFocusPhaseResult(
    bool Ok, TsfFocusPhase Expected, string Reason)
{
    /// <summary>实际读回的阶段（仅当 <see cref="Ok"/> 时有效；Ok=false 时为
    /// <see cref="TsfFocusPhase.None"/> 占位，真实原因见 <see cref="Reason"/>）。</summary>
    internal TsfFocusPhase Actual { get; init; } = TsfFocusPhase.None;
}

/// <summary>
/// R31 复审：受控 TSF 焦点阶段跟踪器（真实路径与测试替身注入<b>同一个</b>类型）。
/// 期望阶段只在一次<b>已验证</b>的 suspend/restore 成功后迁移：写失败、读回不符
/// 或中途失去自有归属时阶段保持不动——后续 postguard/清理仍按<b>实际仍生效</b>的
/// 阶段核对，绝不按"打算做"的阶段放行证据。
/// </summary>
internal sealed class TsfFocusStage
{
    /// <summary>当前生效的期望阶段。</summary>
    internal TsfFocusPhase Phase { get; private set; } = TsfFocusPhase.OwnDocument;

    /// <summary>SAME_OWN 期望所参照的保存令牌（suspend 前 acquire 取得）。</summary>
    internal TsfFocusHandle? Saved { get; private set; }

    /// <summary>迁移到"挂起"阶段；<paramref name="saved"/> 供之后 restore/SAME_OWN 核对。</summary>
    internal void MarkSuspended(TsfFocusHandle saved)
    {
        Saved = saved;
        Phase = TsfFocusPhase.None;
    }

    /// <summary>迁移到"恢复后与保存令牌同一"阶段。</summary>
    internal void MarkRestored()
        => Phase = TsfFocusPhase.SameAsSaved;

    /// <summary>当前阶段的只读描述（日志用）。</summary>
    internal string Describe()
        => Phase switch
        {
            TsfFocusPhase.OwnDocument => "OWN",
            TsfFocusPhase.None => "NULL",
            _ => "SAME_OWN",
        };
}

/// <summary>
/// R31：一次已验证的 TSF 文档焦点持有（不透明令牌）。只有
/// <see cref="TsfDocumentFocus.Acquire"/> 能产出有效实例——真实路径返回的
/// doc/context RCW 已按真实 COM identity 复核为自有；测试替身返回任意不透明
/// 对象，执行器与共享操作只用 <see cref="FocusSnapshot.CompareComIdentity"/>
/// （真实 IUnknown 指针比较）判同。外部代码无法伪造"已验证持有"。
/// </summary>
internal sealed class TsfFocusHandle
{
    /// <summary>GetFocus 取得的自有 document manager RCW；仅作身份比较用途。</summary>
    internal object Doc { get; }

    /// <summary>doc.GetTop 取得的自有 context RCW（可能为 null；同样只读身份用）。</summary>
    internal object? Context { get; }

    /// <summary>共享操作是否拥有本次取得的 RCW（真实路径 true→由操作成对释放；
    /// 测试替身 false→不释放测试对象）。</summary>
    internal bool OwnsRefs { get; }

    /// <summary>GetFocus 成功但返回 null（线程无文档焦点）时为 true——suspend 语义下
    /// 不能作为恢复目标。</summary>
    internal bool NoFocus { get; }

    internal TsfFocusHandle(object doc, object? context, bool ownsRefs, bool noFocus = false)
    {
        Doc = doc;
        Context = context;
        OwnsRefs = ownsRefs;
        NoFocus = noFocus;
    }
}

/// <summary>
/// R31：共享 acquire 结果。<see cref="Focus"/> 仅当 <see cref="Ok"/> 为 true 时有效。
/// </summary>
internal readonly record struct TsfFocusAcquireResult(
    bool Ok, TsfFocusHandle? Focus, string Reason);

/// <summary>
/// R31：一次 SetFocus 操作结果。<see cref="Ok"/> 仅当原生调用成功且读回身份与
/// 目标严格一致（suspend→null；restore→与保存令牌同一 doc/context）。
/// <see cref="MutationMayExist"/> 表示原生写可能已发生（包括返回失败/抛异常）——
/// 恢复语义以它为据，绝不信单次调用的乐观叙述。
/// </summary>
internal readonly record struct TsfFocusOpResult(
    bool Ok, bool MutationMayExist, string Reason);

/// <summary>R31 复审：令牌 RCW 成对释放的结果；任何释放异常都如实进入
/// <see cref="Reason"/>，绝不"释放失败仍报 ok"。</summary>
internal readonly record struct TsfFocusReleaseResult(bool Ok, string Reason)
{
    internal static readonly TsfFocusReleaseResult Success = new(true, "ok");
    internal static readonly TsfFocusReleaseResult Noop = new(true, "no-refs");
}

/// <summary>
/// R31 opt-in 实验（<c>--child-suspend-tsf-focus-when-clipped</c>）的<b>共享
/// TSF 文档焦点操作核</b>。真实 ITfThreadMgr 实现（<see cref="ScenarioRunner"/> 内的
/// 宿主包装）与托管测试替身注入同一组委托——SetFocus 可能已生效但返回失败/抛异常
/// 的情形在本层如实区分：操作前后各一次真实 GetFocus 读回身份才是真源。
/// <para>
/// R31 复审：每次 COM 查询/写入都被<b>可注入共享门禁</b>包住——
/// <paramref name="guard"/>（GetFocus/GetTop/identity/SetFocus 委托之后的<b>最后一刻</b>
/// 复核）与 <paramref name="guardAfterCom"/>（同一门禁的别名；COM 重入可能改变
/// 前台/焦点/窗口归属）在每次原生调用前<b>与</b>后各执行一次。失去前台/自有
/// HWND 焦点/窗口归属、或持有 store 锁/处于原生回调重入时，操作如实失败且
/// <b>绝不执行 SetFocus</b>——不抢回焦点、不覆盖外部状态。
/// </para>
/// <para>
/// 不做 HWND SetFocus/AssociateFocus/Pop/Push，不结束组合、不重输文本、不碰候选窗口；
/// 读回身份不符（readback mismatch）与"操作成功"严格区分，绝不伪造。
/// </para>
/// </summary>
internal static class TsfDocumentFocus
{
    /// <summary>共享门禁委托：返回 null 表示环境仍允许触碰 TSF 焦点；
    /// 非 null 为拒绝原因（前台已切走/焦点不在自有窗口/锁或重入中等）。</summary>
    internal delegate string? FocusGuard(string stage);

    private static readonly FocusGuard NoGuard = _ => null;

    private static string GuardReason(FocusGuard? guard, string stage)
        => guard is null ? string.Empty : guard(stage) ?? string.Empty;

    /// <summary>
    /// 在拥有窗口与 COM 对象的 UI 线程内，只读取得当前 TSF 文档焦点并返回验证过的
    /// <see cref="TsfFocusHandle"/>。GetFocus 返回失败/抛异常、取得 doc 与自有不符、
    /// GetTop 失败或 top-context 与自有不符——一律如实失败，绝不产出有效令牌。
    /// 成功时 ownsRefs=true：调用方在操作完成后经 <see cref="ReleaseHandle"/> 成对释放。
    /// <paramref name="guard"/>（注入的原生前台/焦点/归属/锁/重入门禁）在
    /// GetFocus/GetTop/identity 每一次真实 COM 读之前与之后都复核——COM 重入期间
    /// 环境被破坏时操作如实失败，绝不拿重入前的缓存事实当证据。
    /// </summary>
    internal static TsfFocusAcquireResult Acquire(
        Func<(int Hr, object? Doc)> getFocus,
        object? ownDocumentMgr,
        object? ownContext,
        Func<object, (int Hr, object? Top)> getTop,
        Action<object> release,
        FocusGuard? guard = null,
        FocusGuard? guardAfterCom = null)
    {
        guard ??= NoGuard;
        // guardAfterCom 未单独注入时与 guard 同一门禁（真实宿主两参数同一实现）。
        guardAfterCom ??= guard;

        object? focusDoc = null;
        object? topContext = null;
        var releaseErrors = new List<string>();

        string FailReason(string reason)
            => releaseErrors.Count > 0
                ? $"{reason} (+release errors: {string.Join("; ", releaseErrors)})"
                : reason;

        TsfFocusAcquireResult Fail(string reason)
        {
            // R31 复审修复：逐个尽力释放已取得的引用——第一个释放抛异常
            // 不再跳过第二个；错误全部并入最终诊断，首个失败原因仍居首。
            if (focusDoc is not null)
            {
                ReleaseOne(focusDoc, "focus-doc", release, releaseErrors);
                focusDoc = null;
            }

            if (topContext is not null)
            {
                ReleaseOne(topContext, "top-context", release, releaseErrors);
                topContext = null;
            }

            return new TsfFocusAcquireResult(false, null, FailReason(reason));
        }

        // ---- GetFocus 之前的最后一刻复核（本操作内第一次原生查询前）----
        if (GuardReason(guard, "acquire:before-get-focus") is { Length: > 0 } pre0)
        {
            return Fail($"guard refused before get-focus: {pre0}");
        }

        int hr;
        try
        {
            (hr, focusDoc) = getFocus();
        }
        catch (Exception ex)
        {
            return Fail($"get-focus threw {ex.GetType().Name}: {ex.Message}");
        }

        // ---- GetFocus 之后立即复核：COM 重入期间前台/焦点/归属可能已变 ----
        if (GuardReason(guardAfterCom, "acquire:after-get-focus") is { Length: > 0 } post0)
        {
            return Fail($"guard failed after get-focus: {post0}");
        }

        if (hr < 0)
        {
            return Fail($"get-focus hr={TsfHResult.Describe(hr)}");
        }

        if (focusDoc is null)
        {
            return Fail("tsf thread has no document focus (cannot suspend our document)");
        }

        if (ownDocumentMgr is null)
        {
            return Fail("own document manager unavailable");
        }

        if (GuardReason(guard, "acquire:before-doc-identity") is { Length: > 0 } preId)
        {
            return Fail($"guard refused before doc identity: {preId}");
        }

        var docComparison = FocusSnapshot.CompareComIdentity(
            focusDoc, ownDocumentMgr, releaseErrors);

        if (GuardReason(guardAfterCom, "acquire:after-doc-identity") is { Length: > 0 } postId)
        {
            return Fail($"guard failed after doc identity: {postId}");
        }

        if (docComparison.Errored)
        {
            return Fail($"doc identity check errored: {docComparison.Detail}");
        }

        if (!docComparison.Same)
        {
            // 焦点在别人的 document 上：绝不夺取/篡改。
            return Fail("tsf focus is a foreign document manager");
        }

        if (GuardReason(guard, "acquire:before-get-top") is { Length: > 0 } preTop)
        {
            return Fail($"guard refused before get-top: {preTop}");
        }

        int topHr;
        try
        {
            (topHr, topContext) = getTop(focusDoc);
        }
        catch (Exception ex)
        {
            return Fail($"get-top threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "acquire:after-get-top") is { Length: > 0 } postTop)
        {
            return Fail($"guard failed after get-top: {postTop}");
        }

        if (topHr < 0)
        {
            return Fail($"get-top hr={TsfHResult.Describe(topHr)}");
        }

        if (topContext is null)
        {
            return Fail("own document has no top context");
        }

        if (ownContext is null)
        {
            return Fail("own context unavailable");
        }

        if (GuardReason(guard, "acquire:before-top-identity") is { Length: > 0 } preTopId)
        {
            return Fail($"guard refused before top identity: {preTopId}");
        }

        var topComparison = FocusSnapshot.CompareComIdentity(
            topContext, ownContext, releaseErrors);

        if (GuardReason(guardAfterCom, "acquire:after-top-identity") is { Length: > 0 } postTopId)
        {
            return Fail($"guard failed after top identity: {postTopId}");
        }

        if (topComparison.Errored)
        {
            return Fail($"top-context identity check errored: {topComparison.Detail}");
        }

        if (!topComparison.Same)
        {
            return Fail("top context is not the own context");
        }

        if (releaseErrors.Count > 0)
        {
            // 释放异常绝不静默：如实失败（已取得的 RCW 由 Fail 逐个尽力释放）。
            return Fail($"release errors: {string.Join("; ", releaseErrors)}");
        }

        return new TsfFocusAcquireResult(true,
            new TsfFocusHandle(focusDoc, topContext, ownsRefs: true), "ok");
    }

    /// <summary>尽力释放单个 RCW；异常记入 <paramref name="errors"/>，绝不抛出中断
    /// 后续释放（R31 复审：连续释放时首异常不得跳过其余引用）。</summary>
    private static void ReleaseOne(object target, string name, Action<object> release,
        List<string> errors)
    {
        try
        {
            release(target);
        }
        catch (Exception ex)
        {
            errors.Add($"{name}={ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放一个 <see cref="Acquire"/> 产出的令牌持有的 RCW（ownsRefs=true 时）。
    /// <b>逐个尽力</b>释放 doc 与 top context——第一个释放抛异常不再跳过第二个；
    /// 全部释放错误如实聚进返回值的 <see cref="TsfFocusReleaseResult.Reason"/>，
    /// 调用方必须把它计入最终诊断（不得当成功），同时不得用它覆盖更早的首败。
    /// </summary>
    internal static TsfFocusReleaseResult ReleaseHandle(TsfFocusHandle? handle,
        Action<object> release)
    {
        if (handle is null || !handle.OwnsRefs || handle.NoFocus)
        {
            return TsfFocusReleaseResult.Noop;
        }

        var errors = new List<string>();
        ReleaseOne(handle.Doc, "focus-doc", release, errors);
        if (handle.Context is not null)
        {
            ReleaseOne(handle.Context, "top-context", release, errors);
        }

        return errors.Count == 0
            ? TsfFocusReleaseResult.Success
            : new TsfFocusReleaseResult(false,
                $"release errors: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// 实际读回当前 TSF 焦点并判定它是否符合 <paramref name="expected"/> 阶段：
    /// OWN=自有 doc+top-context；NULL=无线程焦点；SAME_OWN=与保存令牌同一
    /// doc/context。每次 COM 查询前后都过共享门禁——真实宿主用它把"环境事实"
    /// （前台/焦点/归属/锁/重入）并入阶段判定，而不是只凭身份读回。
    /// </summary>
    internal static TsfFocusPhaseResult ReadPhase(
        Func<(int Hr, object? Doc)> getFocus,
        object? ownDocumentMgr,
        object? ownContext,
        Func<object, (int Hr, object? Top)> getTop,
        Action<object> release,
        TsfFocusHandle? saved,
        TsfFocusPhase expected,
        FocusGuard? guard = null,
        FocusGuard? guardAfterCom = null)
    {
        guard ??= NoGuard;
        guardAfterCom ??= guard;
        var releaseErrors = new List<string>();
        object? focusDoc = null;
        object? topContext = null;

        string FailReason(string reason)
            => releaseErrors.Count > 0
                ? $"{reason} (+release errors: {string.Join("; ", releaseErrors)})"
                : reason;

        TsfFocusPhaseResult Fail(string reason, TsfFocusPhase actual = TsfFocusPhase.None)
        {
            if (focusDoc is not null)
            {
                ReleaseOne(focusDoc, "focus-doc", release, releaseErrors);
                focusDoc = null;
            }

            if (topContext is not null)
            {
                ReleaseOne(topContext, "top-context", release, releaseErrors);
                topContext = null;
            }

            return new TsfFocusPhaseResult(false, expected, FailReason(reason))
            {
                Actual = actual,
            };
        }

        if (GuardReason(guard, "phase:before-get-focus") is { Length: > 0 } pre)
        {
            return Fail($"guard refused before get-focus: {pre}");
        }

        int hr;
        try
        {
            (hr, focusDoc) = getFocus();
        }
        catch (Exception ex)
        {
            return Fail($"get-focus threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "phase:after-get-focus") is { Length: > 0 } post)
        {
            return Fail($"guard failed after get-focus: {post}");
        }

        if (hr < 0)
        {
            return Fail($"get-focus hr={TsfHResult.Describe(hr)}");
        }

        // ---- 实际阶段读回 ----
        if (focusDoc is null)
        {
            // 实际为 NULL：仅 NULL 期望可接受；OWN/SAME_OWN 期望下如实失败。
            // 本次 GetFocus 未返回引用——无取得引用需释放，直接返回。
            var ok = expected == TsfFocusPhase.None;
            return new TsfFocusPhaseResult(ok, expected,
                ok ? "ok" : "expected own document focus but read null")
            {
                Actual = TsfFocusPhase.None,
            };
        }

        // 实际非 NULL：必须判同。OWN → 与自有 doc/context 比；SAME_OWN → 与保存令牌比。
        var referenceDoc = expected == TsfFocusPhase.SameAsSaved
            ? saved?.Doc
            : ownDocumentMgr;
        var referenceContext = expected == TsfFocusPhase.SameAsSaved
            ? saved?.Context
            : ownContext;

        if (referenceDoc is null)
        {
            return Fail(expected == TsfFocusPhase.SameAsSaved
                ? "saved focus handle missing (cannot verify SAME_OWN)"
                : "own document manager unavailable");
        }

        if (GuardReason(guard, "phase:before-doc-identity") is { Length: > 0 } preDocId)
        {
            return Fail($"guard refused before doc identity: {preDocId}");
        }

        var docSame = FocusSnapshot.CompareComIdentity(focusDoc, referenceDoc, releaseErrors);

        if (GuardReason(guardAfterCom, "phase:after-doc-identity") is { Length: > 0 } postDocId)
        {
            return Fail($"guard failed after doc identity: {postDocId}");
        }

        if (docSame.Errored)
        {
            return Fail($"doc identity errored: {docSame.Detail}");
        }

        if (!docSame.Same)
        {
            var actual = expected == TsfFocusPhase.SameAsSaved
                ? "a different document manager than the saved token"
                : "a foreign document manager";
            return Fail($"phase mismatch: focus holds {actual}",
                // 实际持有某个 doc 但非期望对象——如实标为与期望不同的实际阶段。
                expected == TsfFocusPhase.OwnDocument ? TsfFocusPhase.None : TsfFocusPhase.OwnDocument);
        }

        // doc 同：继续核 top-context（NULL 期望在前面已返回，这里实际必为 OWN/SAME_OWN 候选）。
        if (GuardReason(guard, "phase:before-get-top") is { Length: > 0 } preTop)
        {
            return Fail($"guard refused before get-top: {preTop}");
        }

        int topHr;
        try
        {
            (topHr, topContext) = getTop(focusDoc);
        }
        catch (Exception ex)
        {
            return Fail($"get-top threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "phase:after-get-top") is { Length: > 0 } postTop)
        {
            return Fail($"guard failed after get-top: {postTop}");
        }

        if (topHr < 0)
        {
            return Fail($"get-top hr={TsfHResult.Describe(topHr)}");
        }

        var expectedOwnPhase = expected == TsfFocusPhase.SameAsSaved
            ? TsfFocusPhase.SameAsSaved
            : TsfFocusPhase.OwnDocument;
        if (topContext is null)
        {
            // top-context 缺失：OWN/SAME_OWN 期望都不成立。
            return Fail("phase mismatch: focused document has no top context",
                expectedOwnPhase);
        }

        if (referenceContext is null)
        {
            // 自有/保存 context 不存在但 doc 却有 top context——不能判同。
            return Fail("phase mismatch: top context exists but no reference context",
                expectedOwnPhase);
        }

        if (GuardReason(guard, "phase:before-top-identity") is { Length: > 0 } preTopId)
        {
            return Fail($"guard refused before top identity: {preTopId}");
        }

        var topSame = FocusSnapshot.CompareComIdentity(topContext, referenceContext,
            releaseErrors);

        if (GuardReason(guardAfterCom, "phase:after-top-identity") is { Length: > 0 } postTopId)
        {
            return Fail($"guard failed after top identity: {postTopId}");
        }

        if (topSame.Errored)
        {
            return Fail($"top-context identity errored: {topSame.Detail}");
        }

        if (!topSame.Same)
        {
            return Fail("phase mismatch: top context is not the expected context",
                expectedOwnPhase);
        }

        if (releaseErrors.Count > 0)
        {
            return Fail($"release errors: {string.Join("; ", releaseErrors)}");
        }

        // 实际阶段 = OWN/SAME_OWN（两者结构上同一：doc+top-context 与参照同一）。
        // R31 复审返修：成功/期望不符出口也逐一释放本次取得的 doc+top-context——
        // 每个出口都恰好释放取得引用一次；先释放再据 releaseErrors 如实判失败，
        // 绝不把泄漏或释放异常吞成"ok"。
        var actualPhase = expected == TsfFocusPhase.SameAsSaved
            ? TsfFocusPhase.SameAsSaved
            : TsfFocusPhase.OwnDocument;
        var expectedOk = expected != TsfFocusPhase.None;
        var reason = expectedOk ? "ok" : "phase mismatch: unexpected document focus";

        // 逐一释放本次 GetFocus/GetTop 取得的 RCW（与 Fail 相同的尽力释放语义；
        // focusDoc/topContext 清空保证绝不二次释放）。
        if (focusDoc is not null)
        {
            ReleaseOne(focusDoc, "focus-doc", release, releaseErrors);
            focusDoc = null;
        }

        if (topContext is not null)
        {
            ReleaseOne(topContext, "top-context", release, releaseErrors);
            topContext = null;
        }

        if (releaseErrors.Count > 0)
        {
            return new TsfFocusPhaseResult(false, expected,
                $"{reason} (+release errors: {string.Join("; ", releaseErrors)})")
            {
                Actual = actualPhase,
            };
        }

        return new TsfFocusPhaseResult(expectedOk, expected, reason)
        {
            Actual = actualPhase,
        };
    }

    /// <summary>
    /// 共享 suspend 操作：先读回 GetFocus 验证当前确实持有自有 doc/context
    /// （foreign focus 如实拒绝，绝不夺取/篡改），再 SetFocus(null)，
    /// 最后 GetFocus 读回确认确实为 null。任一失败都如实返回；
    /// <see cref="TsfFocusOpResult.MutationMayExist"/> 表示原生写可能已发生。
    /// <para>
    /// R31 复审：<paramref name="guard"/> 在 SetFocus(null) 的<b>最后一刻</b>复核
    /// 自有 root 前台/input Win32 焦点/窗口归属/无锁无重入；<paramref name="guardAfterCom"/>
    /// 在每次 COM 读回之后复核——COM 重入夺焦时操作如实失败且绝不补写。
    /// </para>
    /// </summary>
    internal static TsfFocusOpResult Suspend(
        Func<(int Hr, object? Doc)> getFocus,
        Func<object?, int> setFocus,
        object? ownDocumentMgr,
        object? ownContext,
        Func<object, (int Hr, object? Top)> getTop,
        Action<object> release,
        FocusGuard? guard = null,
        FocusGuard? guardAfterCom = null)
    {
        guard ??= NoGuard;
        guardAfterCom ??= guard;

        // 1) 操作前读回：必须确实持有自有 doc/context——否则拒绝，绝不夺取/篡改 foreign focus。
        //    本次内部 acquire 只用于前置验证；取得的 RCW 在函数出口成对释放。
        var acquire = Acquire(getFocus, ownDocumentMgr, ownContext, getTop, release,
            guard, guardAfterCom);
        if (!acquire.Ok || acquire.Focus is null)
        {
            return new TsfFocusOpResult(false, false,
                $"pre-read failed: {acquire.Reason}");
        }

        TsfFocusOpResult result;

        try
        {
            // 2) SetFocus(null) 的最后一刻复核：GetFocus/GetTop/identity 之后、原生写
            //    之前再验一次——COM 查询重入可能已把前台/焦点切走；此时绝不再写。
            if (GuardReason(guard, "suspend:before-set-focus") is { Length: > 0 } pre)
            {
                result = new TsfFocusOpResult(false, false,
                    $"guard refused before set-focus(null): {pre}");
            }
            else
            {
                int hr;
                try
                {
                    hr = setFocus(null);
                }
                catch (Exception ex)
                {
                    result = new TsfFocusOpResult(false, true,
                        $"set-focus(null) threw {ex.GetType().Name}: {ex.Message}");
                    goto ReleaseAndReturn;
                }

                // 原生写之后立即复核（COM 重入可能让归属在写之后失效——如实反映在
                // 结果里，但写已经发生；由读回阶段判定真实焦点）。
                if (GuardReason(guardAfterCom, "suspend:after-set-focus") is { Length: > 0 } post)
                {
                    result = new TsfFocusOpResult(false, true,
                        $"guard failed after set-focus(null): {post}");
                }
                else if (hr < 0)
                {
                    result = new TsfFocusOpResult(false, true,
                        $"set-focus(null) hr={TsfHResult.Describe(hr)}");
                }
                else
                {
                    result = DoSuspendPostReadback(getFocus, guard, guardAfterCom,
                        release);
                }
            }
        }
        catch (Exception ex)
        {
            result = new TsfFocusOpResult(false, true,
                $"unexpected {ex.GetType().Name}: {ex.Message}");
        }

    ReleaseAndReturn:
        {
            // 内部验证用的 RCW（ownsRefs=true）在函数出口逐个尽力释放；
            // 释放失败如实并入结果（成功路径 release 失败同样如实失败）。
            var releaseResult = ReleaseHandle(acquire.Focus, release);
            return !releaseResult.Ok
                ? new TsfFocusOpResult(false, true, releaseResult.Reason)
                : result;
        }
    }

    /// <summary>Suspend 的 post-write 读回辅助（提取出来使 finally 只负责释放）。</summary>
    private static TsfFocusOpResult DoSuspendPostReadback(
        Func<(int Hr, object? Doc)> getFocus,
        FocusGuard guard,
        FocusGuard guardAfterCom,
        Action<object> release)
    {
        // 3) 操作后读回：必须确实为 null（不是"还持有"，也不是"已 foreign"）。
        if (GuardReason(guard, "suspend:before-post-get-focus") is { Length: > 0 } prePost)
        {
            return new TsfFocusOpResult(false, true,
                $"guard refused before post-get-focus: {prePost}");
        }

        int postHr;
        object? postDoc = null;
        try
        {
            (postHr, postDoc) = getFocus();
        }
        catch (Exception ex)
        {
            return new TsfFocusOpResult(false, true,
                $"post-get-focus threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "suspend:after-post-get-focus") is { Length: > 0 } postPost)
        {
            return new TsfFocusOpResult(false, true,
                $"guard failed after post-get-focus: {postPost}");
        }

        var releaseErrors = new List<string>();
        try
        {
            if (postHr < 0)
            {
                return new TsfFocusOpResult(false, true,
                    $"post-get-focus hr={TsfHResult.Describe(postHr)}");
            }

            if (postDoc is not null)
            {
                return new TsfFocusOpResult(false, true,
                    "post-get-focus still reports a document manager (suspend not applied)");
            }
        }
        finally
        {
            if (postDoc is not null)
            {
                ReleaseOne(postDoc, "post-doc", release, releaseErrors);
            }
        }

        // 读回释放失败如实并入诊断（不改变已判失败的结论）。
        if (releaseErrors.Count > 0)
        {
            return new TsfFocusOpResult(false, true,
                $"post-get-focus release errors: {string.Join("; ", releaseErrors)}");
        }

        return new TsfFocusOpResult(true, true, "ok");
    }

    /// <summary>
    /// 共享 restore 操作：把<b>保存的同一个</b>自有 doc/context 重新 SetFocus，
    /// 读回身份必须与保存令牌严格一致（doc+top context 同时同）。foreign focus
    /// 已占据时如实失败——绝不覆盖别人的文档焦点。
    /// <para>
    /// R31 复审：SetFocus(saved) 前最后一刻复核自有前台/焦点/归属/无锁无重入；
    /// 每次 COM 读回之后再复核——重入夺焦时如实失败且<b>绝不写</b>（不覆盖外部焦点）。
    /// </para>
    /// </summary>
    internal static TsfFocusOpResult Restore(
        TsfFocusHandle saved,
        Func<(int Hr, object? Doc)> getFocus,
        Func<object?, int> setFocus,
        Func<object, (int Hr, object? Top)> getTop,
        Action<object> release,
        FocusGuard? guard = null,
        FocusGuard? guardAfterCom = null)
    {
        guard ??= NoGuard;
        guardAfterCom ??= guard;

        if (saved.NoFocus)
        {
            return new TsfFocusOpResult(false, false,
                "saved handle is no-focus; cannot restore a foreign/null focus");
        }

        // 恢复前读回：必须当前确实无焦点（suspend 语义），或仍是自有 doc
        // （外部未改变时恢复语义也合法）；已 foreign 即拒绝——绝不覆盖外部焦点。
        if (GuardReason(guard, "restore:before-pre-get-focus") is { Length: > 0 } preGuard)
        {
            return new TsfFocusOpResult(false, false,
                $"guard refused before pre-get-focus: {preGuard}");
        }

        int preHr;
        object? preDoc = null;
        try
        {
            (preHr, preDoc) = getFocus();
        }
        catch (Exception ex)
        {
            return new TsfFocusOpResult(false, false,
                $"pre-get-focus threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "restore:after-pre-get-focus") is { Length: > 0 } postGuard)
        {
            return new TsfFocusOpResult(false, false,
                $"guard failed after pre-get-focus: {postGuard}");
        }

        try
        {
            if (preHr < 0)
            {
                return new TsfFocusOpResult(false, false,
                    $"pre-get-focus hr={TsfHResult.Describe(preHr)}");
            }

            if (preDoc is not null)
            {
                if (GuardReason(guard, "restore:before-pre-doc-identity") is { Length: > 0 } preId)
                {
                    return new TsfFocusOpResult(false, false,
                        $"guard refused before pre-doc-identity: {preId}");
                }

                var preReleaseErrors = new List<string>();
                var same = FocusSnapshot.CompareComIdentity(preDoc, saved.Doc, preReleaseErrors);

                if (GuardReason(guardAfterCom, "restore:after-pre-doc-identity") is { Length: > 0 } postId)
                {
                    return new TsfFocusOpResult(false, false,
                        $"guard failed after pre-doc-identity: {postId}");
                }

                if (same.Errored)
                {
                    return new TsfFocusOpResult(false, false,
                        $"pre-doc identity errored: {same.Detail}");
                }

                if (!same.Same)
                {
                    // 外部已抢占文档焦点：绝不覆盖。
                    return new TsfFocusOpResult(false, false,
                        "tsf focus already foreign-owned; refusing to overwrite");
                }
                // 仍是自有 doc：重写一次也无害（同一对象），继续走到 SetFocus+读回。
            }
        }
        finally
        {
            if (preDoc is not null)
            {
                var errs = new List<string>();
                ReleaseOne(preDoc, "pre-doc", release, errs);
            }
        }

        // SetFocus(saved) 前最后一刻复核——GetFocus 之后可能已丢前台/焦点。
        if (GuardReason(guard, "restore:before-set-focus") is { Length: > 0 } lastGuard)
        {
            return new TsfFocusOpResult(false, false,
                $"guard refused before set-focus(saved): {lastGuard}");
        }

        // 变更可能已经发生——旗标先置位。
        var mutationMayExist = true;
        int hr;
        try
        {
            hr = setFocus(saved.Doc);
        }
        catch (Exception ex)
        {
            return new TsfFocusOpResult(false, true,
                $"set-focus(saved) threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "restore:after-set-focus") is { Length: > 0 } postWrite)
        {
            return new TsfFocusOpResult(false, true,
                $"guard failed after set-focus(saved): {postWrite}");
        }

        if (hr < 0)
        {
            return new TsfFocusOpResult(false, true,
                $"set-focus(saved) hr={TsfHResult.Describe(hr)}");
        }

        // 读回必须仍是同一个 doc + top context（strict identity）。
        if (GuardReason(guard, "restore:before-post-get-focus") is { Length: > 0 } prePostRead)
        {
            return new TsfFocusOpResult(false, true,
                $"guard refused before post-get-focus: {prePostRead}");
        }

        int postHr;
        object? postDoc = null;
        try
        {
            (postHr, postDoc) = getFocus();
        }
        catch (Exception ex)
        {
            return new TsfFocusOpResult(false, true,
                $"post-get-focus threw {ex.GetType().Name}: {ex.Message}");
        }

        if (GuardReason(guardAfterCom, "restore:after-post-get-focus") is { Length: > 0 } postPostRead)
        {
            return new TsfFocusOpResult(false, true,
                $"guard failed after post-get-focus: {postPostRead}");
        }

        try
        {
            if (postHr < 0)
            {
                return new TsfFocusOpResult(false, true,
                    $"post-get-focus hr={TsfHResult.Describe(postHr)}");
            }

            if (postDoc is null)
            {
                return new TsfFocusOpResult(false, true,
                    "post-get-focus reports null (restore not applied)");
            }

            if (GuardReason(guard, "restore:before-post-doc-identity") is { Length: > 0 } prePostId)
            {
                return new TsfFocusOpResult(false, true,
                    $"guard refused before post-doc-identity: {prePostId}");
            }

            var releaseErrors = new List<string>();
            var docSame = FocusSnapshot.CompareComIdentity(postDoc, saved.Doc, releaseErrors);

            if (GuardReason(guardAfterCom, "restore:after-post-doc-identity") is { Length: > 0 } postPostId)
            {
                return new TsfFocusOpResult(false, true,
                    $"guard failed after post-doc-identity: {postPostId}");
            }

            if (docSame.Errored)
            {
                return new TsfFocusOpResult(false, true,
                    $"post-doc identity errored: {docSame.Detail}");
            }

            if (!docSame.Same)
            {
                return new TsfFocusOpResult(false, true,
                    "post-get-focus reports a different document manager");
            }

            if (GuardReason(guard, "restore:before-post-get-top") is { Length: > 0 } prePostTop)
            {
                return new TsfFocusOpResult(false, true,
                    $"guard refused before post-get-top: {prePostTop}");
            }

            int topHr;
            object? topContext = null;
            try
            {
                (topHr, topContext) = getTop(postDoc);
            }
            catch (Exception ex)
            {
                return new TsfFocusOpResult(false, true,
                    $"post-get-top threw {ex.GetType().Name}: {ex.Message}");
            }

            if (GuardReason(guardAfterCom, "restore:after-post-get-top") is { Length: > 0 } postPostTop)
            {
                return new TsfFocusOpResult(false, true,
                    $"guard failed after post-get-top: {postPostTop}");
            }

            try
            {
                if (topHr < 0)
                {
                    return new TsfFocusOpResult(false, true,
                        $"post-get-top hr={TsfHResult.Describe(topHr)}");
                }

                if (topContext is null)
                {
                    return new TsfFocusOpResult(false, true,
                        "post-get-top reports no context (restore incomplete)");
                }

                if (GuardReason(guard, "restore:before-post-top-identity") is { Length: > 0 } prePostTopId)
                {
                    return new TsfFocusOpResult(false, true,
                        $"guard refused before post-top-identity: {prePostTopId}");
                }

                var topSame = saved.Context is null
                    // 保存时 doc 就没有 top context：恢复后读回同样无 context 即同。
                    ? new ComIdentityResult(ComIdentity.Same, "both-contexts-null")
                    : FocusSnapshot.CompareComIdentity(topContext, saved.Context, releaseErrors);

                if (GuardReason(guardAfterCom, "restore:after-post-top-identity") is { Length: > 0 } postPostTopId)
                {
                    return new TsfFocusOpResult(false, true,
                        $"guard failed after post-top-identity: {postPostTopId}");
                }

                if (topSame.Errored)
                {
                    return new TsfFocusOpResult(false, true,
                        $"post-top-context identity errored: {topSame.Detail}");
                }

                if (!topSame.Same)
                {
                    return new TsfFocusOpResult(false, true,
                        "post-get-top reports a different context");
                }

                if (releaseErrors.Count > 0)
                {
                    return new TsfFocusOpResult(false, true,
                        $"release errors: {string.Join("; ", releaseErrors)}");
                }
            }
            finally
            {
                if (topContext is not null)
                {
                    var errs = new List<string>();
                    ReleaseOne(topContext, "post-top-context", release, errs);
                }
            }
        }
        finally
        {
            if (postDoc is not null)
            {
                var errs = new List<string>();
                ReleaseOne(postDoc, "post-doc", release, errs);
            }
        }

        return new TsfFocusOpResult(true, mutationMayExist, "ok");
    }
}
