namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 同一时刻、同一线程内读取的组合与观察快照（不可变）。
/// R29B：连续性计数（组合 begin/end/session 与输入焦点事件数）以可选默认参数追加，
/// 既有 6 元组构造/测试语义完全不变；真实宿主路径总是填充它们。
/// </summary>
internal readonly record struct LifecycleSnapshot(
    string Text, int SpanStart, int SpanEnd, int Writes, bool Composing, string Profile,
    int CompositionBeginCount = 0, int CompositionEndCount = 0, int CompositionSessionId = 0,
    int InputFocusChangeCount = 0)
{
    internal bool SameComposition(LifecycleSnapshot other)
        => string.Equals(Text, other.Text, StringComparison.Ordinal)
            && SpanStart == other.SpanStart
            && SpanEnd == other.SpanEnd;

    internal bool SameProfile(LifecycleSnapshot other)
        => string.Equals(Profile, other.Profile, StringComparison.Ordinal);

    /// <summary>
    /// 组合连续性单调计数全部一致：text/span 相同但 begin/end/session 变化即
    /// “组合曾结束又以相同文本重启”；输入焦点事件计数变化即“期间焦点曾离开又回来”。
    /// 两者都是 text/span+writes 无法检测的不连续。
    /// </summary>
    internal bool SameContinuity(LifecycleSnapshot other)
        => CompositionBeginCount == other.CompositionBeginCount
            && CompositionEndCount == other.CompositionEndCount
            && CompositionSessionId == other.CompositionSessionId
            && InputFocusChangeCount == other.InputFocusChangeCount;

    /// <summary>
    /// 完整不变量：文本、组合范围、组合中（基线与当前都必须组合中）、绑定写入（必须仍为 0）、
    /// 实际 profile 与全部连续性计数一致。只比较 text/span 会漏掉“组合已结束”
    /// “期间已有写入”“profile 被切换”“组合同文本重启”“焦点曾瞬时离开”。
    /// </summary>
    internal bool SameInvariant(LifecycleSnapshot baseline)
        => SameComposition(baseline)
            && SameProfile(baseline)
            && SameContinuity(baseline)
            && Composing
            && baseline.Composing
            && Writes == 0
            && baseline.Writes == 0;

    /// <summary>返回第一个不满足的不变量的可读描述；全部满足时返回 null。</summary>
    internal string? DescribeInvariantViolation(LifecycleSnapshot baseline)
    {
        if (!SameComposition(baseline))
        {
            return $"composition text/span changed ('{Text}' {SpanStart}..{SpanEnd} vs "
                + $"'{baseline.Text}' {baseline.SpanStart}..{baseline.SpanEnd})";
        }

        if (!SameProfile(baseline))
        {
            return $"profile changed ('{Profile}' vs '{baseline.Profile}')";
        }

        if (!Composing || !baseline.Composing)
        {
            return $"composition is no longer active (composing={Composing}, "
                + $"baseline={baseline.Composing})";
        }

        if (CompositionBeginCount != baseline.CompositionBeginCount
            || CompositionEndCount != baseline.CompositionEndCount
            || CompositionSessionId != baseline.CompositionSessionId)
        {
            return $"composition continuity counters changed "
                + $"(begin {CompositionBeginCount} vs {baseline.CompositionBeginCount}, "
                + $"end {CompositionEndCount} vs {baseline.CompositionEndCount}, "
                + $"session {CompositionSessionId} vs {baseline.CompositionSessionId}) — "
                + "composition ended and restarted with identical text/span";
        }

        if (InputFocusChangeCount != baseline.InputFocusChangeCount)
        {
            return $"input focus events changed ({InputFocusChangeCount} vs "
                + $"{baseline.InputFocusChangeCount}) — focus transiently left and returned";
        }

        if (Writes != 0 || baseline.Writes != 0)
        {
            return $"binding writes changed (now={Writes}, baseline={baseline.Writes})";
        }

        return null;
    }

    internal string Describe()
        => $"text='{Text}' span={SpanStart}..{SpanEnd} writes={Writes} composing={Composing} "
            + $"profile='{Profile}' "
            + $"begin={CompositionBeginCount} end={CompositionEndCount} "
            + $"session={CompositionSessionId} focus_events={InputFocusChangeCount}";
}

/// <summary>
/// 一次**原子**安全点读取：环境检查与快照在同一个 UI 线程、同一个临界区内取得，
/// 避免从多个线程的属性拼接出非原子快照。
/// R29B：连续性计数以可选默认参数追加；默认构造的探针（计数为 0）与既有测试兼容。
/// </summary>
internal readonly record struct SafePointProbe(
    bool Ok, string Detail, string Text, int SpanStart, int SpanEnd, int Writes, bool Composing,
    string Profile, int CompositionBeginCount = 0, int CompositionEndCount = 0,
    int CompositionSessionId = 0, int InputFocusChangeCount = 0)
{
    internal LifecycleSnapshot Snapshot => new(Text, SpanStart, SpanEnd, Writes, Composing, Profile,
        CompositionBeginCount, CompositionEndCount, CompositionSessionId, InputFocusChangeCount);
}

/// <summary>
/// 生命周期执行的宿主抽象。真实实现（<see cref="ScenarioLifecycleHost"/>）与测试替身
/// 走<b>同一套</b>执行器逻辑，因此测试覆盖的就是生产控制流。
/// </summary>
internal interface ILifecycleHost
{
    /// <summary>在 UI 线程执行；返回 false 表示动作未执行（例如元素已 End）。</summary>
    bool InvokeOnUiThread(Func<bool> action);

    /// <summary>
    /// 在 UI 线程的**单个**安全点内同时完成：自有前台/焦点/窗口/运行状态检查，
    /// 以及文本/组合范围/写入/profile 快照读取。任一项不满足时 <c>Ok=false</c>。
    /// </summary>
    SafePointProbe Probe(string stage);

    void Record(string kind, IDictionary<string, object?> fields);

    /// <summary>截图；返回 false 表示截图失败。</summary>
    bool Capture(string name);

    /// <summary>暂停指定毫秒（用于让 TIP 渲染）。</summary>
    void Wait(int milliseconds);

    /// <summary>是否仍有存活候选；返回其 id（无则 null）。</summary>
    uint? LiveCandidateId();

    /// <summary>对指定存活候选执行 Show；元素非存活时返回 null。</summary>
    UiElementShowResult? ShowOnCandidate(uint id, bool show);

    /// <summary>指定 id 是否仍**存活且是同一实例**。</summary>
    bool IsLive(uint id);
}

internal enum LifecycleStop
{
    None,
    SafePointLost,
    ElementEnded,
    ShowFailed,
    CompositionChanged,
    CaptureFailed,
    Unsupported,
    ActionNotExecuted,
}

/// <summary>
/// R27 原生 UIElement 生命周期执行器（真实场景与测试共用，不另造流程）。
/// <para>
/// 每个关键点都在 UI 线程的**单个**安全点内完成：先核验自有前台/焦点/窗口/运行状态，
/// 再读取不可变快照；动作后再做一次同样的安全点读取并比对。
/// 任何偏差立即停止：不夺焦点、不继续截图、不调用下一次 Show。
/// </para>
/// </summary>
internal sealed class NativeUiLifecycleExecutor
{
    /// <summary>等待存活候选的有界上限（毫秒）。</summary>
    internal const int LiveWaitBudgetMs = 5_000;

    private const int WaitSliceMs = 50;

    private readonly ILifecycleHost _host;
    private readonly Action<LifecycleSnapshot> _baselineSink;

    internal NativeUiLifecycleExecutor(ILifecycleHost host, Action<LifecycleSnapshot> baselineSink)
    {
        _host = host;
        _baselineSink = baselineSink;
    }

    internal LifecycleSnapshot? Baseline { get; private set; }

    internal LifecycleStop Stop { get; private set; } = LifecycleStop.None;

    internal string StopDetail { get; private set; } = "(none)";

    internal int SuccessfulShows { get; private set; }

    internal string StoppedAtStage { get; private set; } = "(none)";

    /// <summary>
    /// 执行完整序列：等待存活候选 → 基线截图 → Show(TRUE)/Show(FALSE)/Show(TRUE)。
    /// 返回 true 表示全部检查通过（视觉仍 pending）。
    /// </summary>
    internal bool Run()
    {
        // 1) 有界等待存活候选；等待期间每次抽检都在 UI 线程核对安全点。
        var candidateId = WaitForLiveCandidate();
        if (candidateId is null)
        {
            return Stop == LifecycleStop.None
                ? StopWith(LifecycleStop.Unsupported,
                    $"no live candidate element within {LiveWaitBudgetMs}ms", "wait-live")
                : false;
        }

        // 2) 初始有效组合的基线：安全点 + 真实 profile + 快照在同一安全点取得。
        var baselineProbe = _host.Probe("baseline");
        if (!baselineProbe.Ok)
        {
            return StopWith(LifecycleStop.SafePointLost, baselineProbe.Detail, "baseline");
        }

        var baseline = baselineProbe.Snapshot;

        // 基线本身就必须是“有组合、零写入、profile 已确认为微软拼音”的合法起点；
        // 否则后续所有不变量比较都没有意义。
        if (!baseline.Composing)
        {
            return StopWith(LifecycleStop.CompositionChanged,
                "no active composition at baseline", "baseline");
        }

        if (baseline.Writes != 0)
        {
            return StopWith(LifecycleStop.CompositionChanged,
                $"binding writes already non-zero at baseline ({baseline.Writes})", "baseline");
        }

        // 基线必须携带**已核验**的完整 profile 身份（HResult==0 的简体中文 Microsoft Pinyin
        // input processor + 双 GUID）：空值、未核验标签或任何非完整身份标签一律拒绝。
        // 这样“查询成功但身份不符”“查询失败”“根本没查询”都无法被接纳为基线。
        if (!ImeIdentity.IsVerifiableMicrosoftPinyin(baseline.Profile))
        {
            return StopWith(LifecycleStop.SafePointLost,
                $"active profile not verifiable at baseline ('{baseline.Profile}')", "baseline");
        }

        Baseline = baseline;
        _baselineSink(baseline);

        // 3) 初始截图必须发生在确认安全点之后（不允许在失焦状态取有效证据）。
        if (!_host.Capture("r27a-initially-suppressed"))
        {
            return StopWith(LifecycleStop.CaptureFailed,
                "initially-suppressed capture failed", "initially-suppressed");
        }

        // 4) 三个阶段：同一存活候选。
        if (!RunStage(candidateId.Value, show: true, "native-shown"))
        {
            return false;
        }

        if (!RunStage(candidateId.Value, show: false, "native-hidden"))
        {
            return false;
        }

        return RunStage(candidateId.Value, show: true, "native-restored");
    }

    private bool RunStage(uint candidateId, bool show, string stage)
    {
        // 动作前：安全点与快照、存活复核、Show 在同一 UI 线程动作内完成。
        var before = default(SafePointProbe);
        UiElementShowResult? result = null;
        var invariantViolation = (string?)null;
        var executed = _host.InvokeOnUiThread(() =>
        {
            before = _host.Probe(stage + ":before");
            if (!before.Ok)
            {
                return false;
            }

            // 动作前必须**完整**核对基线不变量：text/span/composing/writes/profile。
            // 否则阶段之间被替换的文本或已发生的写入会先被 Show 掩盖。
            if (Baseline is { } baselineBefore)
            {
                invariantViolation = before.Snapshot.DescribeInvariantViolation(baselineBefore);
                if (invariantViolation is not null)
                {
                    return false;
                }
            }
            else
            {
                invariantViolation = "baseline not captured";
                return false;
            }

            if (!_host.IsLive(candidateId))
            {
                return false;
            }

            result = _host.ShowOnCandidate(candidateId, show);
            return result is not null;
        });

        if (!executed || result is null)
        {
            if (!before.Ok)
            {
                return StopWith(LifecycleStop.SafePointLost, before.Detail, stage);
            }

            if (invariantViolation is not null)
            {
                _host.Record("native-ui-lifecycle-mutation", new Dictionary<string, object?>
                {
                    ["stage"] = stage,
                    ["when"] = "before-show",
                    ["baseline"] = Baseline?.Describe(),
                    ["observed"] = before.Snapshot.Describe(),
                    ["violation"] = invariantViolation,
                });
                return StopWith(LifecycleStop.CompositionChanged,
                    $"{stage}: invariant violated before Show — {invariantViolation}", stage);
            }

            return StopWith(LifecycleStop.ElementEnded,
                $"{stage}: element not live or Show not executed", stage);
        }

        var value = result.Value;

        // HResult 只有在 ShowHrAvailable 时才代表系统应答；为 false 表示没有可用的
        // 原生返回 HRESULT（调用前已 End，或抛出异常、未取得返回值），此时 hresult
        // 是工具合成哨兵——不得据此断言“系统返回失败”，也不能推断系统从未执行 Show。
        if (value.ShowHrAvailable && value.HResult < 0)
        {
            _host.Record("native-ui-lifecycle-step", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["requested_show"] = show,
                ["id"] = value.Id,
                ["hresult"] = TsfHResult.Describe(value.HResult),
                ["hr_available"] = true,
                ["shown_before"] = value.ShownBefore,
                ["shown_after"] = value.ShownAfter,
            });
            return StopWith(LifecycleStop.ShowFailed,
                $"{stage}: Show returned {TsfHResult.Describe(value.HResult)}", stage);
        }

        if (!value.ShowHrAvailable)
        {
            _host.Record("native-ui-lifecycle-step", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["requested_show"] = show,
                ["id"] = value.Id,
                ["hr_available"] = false,
                ["hresult"] = TsfHResult.Describe(value.HResult),
                ["exception_type"] = value.ShowExceptionType,
                ["exception_message"] = value.ShowExceptionMessage,
                ["exception_stage"] = value.ShowExceptionStage,
                ["ended_during_show"] = value.EndedDuringShow,
                ["shown_before_hr"] = value.IsShownBeforeHr,
                ["shown_after_hr"] = value.IsShownAfterHr,
                ["note"] = "native HRESULT unavailable (ended before call or call threw before returning a usable HRESULT); hresult is a tool-synthesized sentinel, not a system failure and not proof the system never ran",
            });
            // 元素已 End（同步回调中死亡）→ ElementEnded；否则是调用异常 → ShowFailed。
            return StopWith(
                value.EndedDuringShow || !_host.IsLive(candidateId)
                    ? LifecycleStop.ElementEnded
                    : LifecycleStop.ShowFailed,
                $"{stage}: native Show HRESULT unavailable "
                + $"(exception={value.ShowExceptionType ?? "none"} stage={value.ShowExceptionStage ?? "none"})",
                stage);
        }

        // Show 返回 S_OK，但元素在调用期间同步 End：不能再对已 End 对象做 hide/restore。
        // 按 R27 允许且要求在同等安全守卫下做**一次**诊断性截图，确认原生 UI 是否已接管；
        // 整体仍判 FAIL —— 失去可控元素不是通过。
        if (value.EndedDuringShow || !IsLiveOnUiThread(candidateId))
        {
            return RunEndedDiagnostic(candidateId, show, stage, value, before);
        }

        // 让 TIP 渲染。
        _host.Wait(400);

        // 动作后：同一 UI 线程内再取一次安全点 + 快照；失焦/提交/内容变化立即停止。
        var after = _host.Probe(stage + ":after");
        if (!after.Ok)
        {
            return StopWith(LifecycleStop.SafePointLost, after.Detail, stage);
        }

        var stillLive = false;
        _host.InvokeOnUiThread(() => stillLive = _host.IsLive(candidateId));
        if (!stillLive)
        {
            return StopWith(LifecycleStop.ElementEnded, $"{stage}: element ended after Show", stage);
        }

        var afterSnapshot = after.Snapshot;
        if (Baseline is not { } baselineAfter)
        {
            return StopWith(LifecycleStop.CompositionChanged,
                $"{stage}: baseline not captured", stage);
        }

        // 动作后同样做**完整**比较。
        var afterViolation = afterSnapshot.DescribeInvariantViolation(baselineAfter);
        if (afterViolation is not null)
        {
            _host.Record("native-ui-lifecycle-mutation", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["when"] = "after-show",
                ["baseline"] = baselineAfter.Describe(),
                ["before"] = before.Snapshot.Describe(),
                ["after"] = afterSnapshot.Describe(),
                ["violation"] = afterViolation,
            });
            return StopWith(LifecycleStop.CompositionChanged,
                $"{stage}: invariant violated after Show — {afterViolation}", stage);
        }

        // 截图前后的安全点与不变量复核由宿主在 UI 线程内完成；失败即不记为有效证据。
        if (!_host.Capture(stage))
        {
            return StopWith(LifecycleStop.CaptureFailed, $"{stage}: capture failed", stage);
        }

        SuccessfulShows++;
        _host.Record("native-ui-lifecycle-step", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["requested_show"] = show,
            ["id"] = value.Id,
            ["hresult"] = TsfHResult.Describe(value.HResult),
            ["shown_before"] = value.ShownBefore,
            ["shown_after"] = value.ShownAfter,
            ["still_live"] = true,
            ["snapshot"] = afterSnapshot.Describe(),
            ["note"] = "Show success does not imply the image is visible; visual is pending",
        });

        return true;
    }

    /// <summary>在 UI 线程上复核元素是否仍存活。</summary>
    private bool IsLiveOnUiThread(uint candidateId)
    {
        var live = false;
        _host.InvokeOnUiThread(() => live = _host.IsLive(candidateId));
        return live;
    }

    /// <summary>
    /// R27 专属诊断分支：Show 已返回 S_OK，但元素在调用期间同步 End（调用返回后
    /// 已不存活）。不得再对该元素做 hide/restore；允许且要求在**与正常阶段相同**的
    /// 组合有效/零写入/自有前台守卫下等待 ~400ms 并截一张 <c>native-shown-ended</c>，
    /// 只为确认原生 UI 是否已接管——整体仍判 FAIL。
    /// </summary>
    private bool RunEndedDiagnostic(uint candidateId, bool show, string stage,
        UiElementShowResult value, SafePointProbe before)
    {
        _host.Record("native-ui-lifecycle-step", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["requested_show"] = show,
            ["id"] = value.Id,
            ["hr_available"] = value.ShowHrAvailable,
            ["hresult"] = TsfHResult.Describe(value.HResult),
            ["ended_during_show"] = value.EndedDuringShow,
            ["shown_before"] = value.ShownBefore,
            ["shown_after"] = value.ShownAfter,
            ["note"] = "element ended during a successful Show; capturing diagnostic only — overall still FAIL",
        });

        // 与正常路径相同的守卫：让 TIP 渲染，然后在 UI 线程上复核安全点+完整不变量。
        _host.Wait(400);

        var after = default(SafePointProbe);
        var stillLive = false;
        string? violation = null;
        _host.InvokeOnUiThread(() =>
        {
            after = _host.Probe(stage + ":ended-after");
            stillLive = _host.IsLive(candidateId);
            if (after.Ok && Baseline is { } b)
            {
                violation = after.Snapshot.DescribeInvariantViolation(b);
            }

            return true;
        });

        var captured = false;
        var captureName = stage + "-ended"; // e.g. "native-shown-ended"
        if (after.Ok && violation is null)
        {
            // 基线不变量仍成立（组合有效/零写入/自有前台焦点）→ 允许且要求截一次诊断图。
            captured = _host.Capture(captureName);
            _host.Record("native-ui-lifecycle-step", new Dictionary<string, object?>
            {
                ["stage"] = captureName,
                ["requested_show"] = show,
                ["id"] = value.Id,
                ["diagnostic_capture"] = captured,
                ["still_live"] = stillLive,
                ["snapshot"] = after.Snapshot.Describe(),
            });
        }

        var detail = !after.Ok
            ? $"{stage}: element ended during Show; post-end safe point lost ({after.Detail})"
            : violation is not null
                ? $"{stage}: element ended during Show; post-end invariant violated — {violation}"
                : $"{stage}: element ended during Show (diagnostic captured={captured}); "
                  + "element no longer controllable — not a pass";

        return StopWith(LifecycleStop.ElementEnded, detail, stage);
    }

    private uint? WaitForLiveCandidate()
    {
        for (var elapsed = 0; elapsed < LiveWaitBudgetMs; elapsed += WaitSliceMs)
        {
            // 等待期间也必须核对安全点；Probe 在 UI 线程内读取前台与焦点，
            // 因此不会在 worker 线程误读 GetFocus。
            var probe = _host.Probe("wait-live:" + elapsed);
            if (!probe.Ok)
            {
                StopWith(LifecycleStop.SafePointLost, probe.Detail, "wait-live");
                return null;
            }

            var id = _host.LiveCandidateId();
            if (id is not null)
            {
                return id;
            }

            _host.Wait(WaitSliceMs);
        }

        return null;
    }

    private bool StopWith(LifecycleStop stop, string detail, string stage)
    {
        Stop = stop;
        StopDetail = detail;
        StoppedAtStage = stage;
        _host.Record("native-ui-lifecycle-stop", new Dictionary<string, object?>
        {
            ["stop"] = stop.ToString(),
            ["stage"] = stage,
            ["detail"] = detail,
        });
        return false;
    }
}
