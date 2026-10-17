using System.Text.Json;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 组合现场的实际输入法身份佐证。
/// </summary>
/// <remarks>
/// <para>
/// 只证明“某个组合消息到达过”不足以把组合归因给目标输入法：必须在<b>同一时刻</b>记录系统报告的
/// 实际活动 profile（CLSID / ProfileGuid / 类型 / 语言 / flags）。本类只做只读查询，
/// 不激活、不切换、不修改任何输入法配置。
/// </para>
/// <para>
/// 安全边界：只读查询（<c>CoCreateInstance</c> + <c>GetActiveProfile</c>）<b>绝不</b>在原生窗口过程回调里执行。
/// 回调内只把一个标签登记进待处理队列；实际查询发生在消息返回、窗口过程已经退出之后
/// （<see cref="RequestPendingFlush"/> / <see cref="FlushPending"/>）。因此这里没有递归泵、
/// 没有在回调内新建窗口或收发消息。
/// </para>
/// <para>
/// 本类不实现任何 TSF 文本存储/组合接收器（那属于 TI-07 范围）。
/// </para>
/// </remarks>
internal static class IdentityProbe
{
    private const int MaxPending = 16;

    private static readonly LinkedList<PendingProbe> Pending = new();
    private static bool _flushRequested;
    private static int _snapshotCount;
    private static ProbeTargetProfile? _target;

    /// <summary>
    /// 本进程期望的输入法目标（由启动参数决定：mspinyin / wetype / current）。
    /// 未设置时（例如单元测试或 info 之前）按“无可比目标”处理，不假装匹配。
    /// </summary>
    internal static ProbeTargetProfile? Target
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>已记录的身份快照数量（供场景断言/汇总使用）。</summary>
    internal static int SnapshotCount => _snapshotCount;

    /// <summary>立即（在消息过程之外）记录一次身份快照，并把结论写入日志。</summary>
    internal static bool Record(IProbeEventLog log, ImeProbeWindow window, string label)
        => Record(log, window, label, out _);

    /// <summary>立即记录一次身份快照；<paramref name="details"/> 为写进日志的字段。</summary>
    internal static bool Record(IProbeEventLog log, ImeProbeWindow window, string label, out IdentitySnapshot snapshot)
    {
        snapshot = Query(log, window, label, registeredAtSeq: -1, registeredCompositionId: -1);
        return snapshot.MatchesConfiguredProfile;
    }

    /// <summary>
    /// 原生回调内调用：只登记一个待查标签（连同当时的 seq 与 compositionId），
    /// 不执行任何 COM/窗口调用。
    /// </summary>
    internal static void RequestPending(string label, long eventSeq, int compositionId)
    {
        if (Pending.Count >= MaxPending)
        {
            return;
        }

        Pending.AddLast(new PendingProbe(label, eventSeq, compositionId));
        _flushRequested = true;
    }

    /// <summary>消息过程外调用：在窗口过程已退出后执行已登记的身份查询。</summary>
    /// <remarks>
    /// 查询发生在“消息过程退出之后”，日志会同时写出“登记时刻的 seq/compositionId”和“查询时刻的
    /// seq/compositionId”。两者之间的间隔可能发生 profile 切换，因此**不能**把晚查到的身份写成
    /// “事件瞬间的身份”，只能写成“事件后紧邻的一次只读查询”。
    /// </remarks>
    internal static void FlushPending(IProbeEventLog log, ImeProbeWindow window)
    {
        if (!_flushRequested)
        {
            return;
        }

        _flushRequested = false;
        while (Pending.First is { } node)
        {
            Pending.RemoveFirst();
            var pending = node.Value;
            Query(log, window, pending.Label, pending.EventSeq, pending.CompositionId);
        }
    }

    /// <summary>是否还有未执行的登记（调用方据此决定要不要泵一次消息循环）。</summary>
    internal static bool HasPending => Pending.Count > 0;

    /// <summary>只读查询实际活动 profile 并写日志；任何异常都按“查询失败”如实记录，绝不抛给调用方。</summary>
    private static IdentitySnapshot Query(
        IProbeEventLog log, ImeProbeWindow window, string label, long registeredAtSeq, int registeredCompositionId)
    {
        var (hr, profile) = TsfProfile.ReadActiveProfile(log, $"identity:{label}");
        _snapshotCount++;

        var clsid = profile.Clsid;
        var profileGuid = profile.ProfileGuid;
        var isInputProcessor = profile.ProfileType == TsfProfile.TfProfileTypeInputProcessor;
        // 身份核对按“本次启动配置的目标”判定，而不是写死微软拼音：
        // --profile wetype / --profile current 下把 MS 匹配当成唯一成功判据在语义上是错的。
        var target = _target;
        var matchesTarget = hr >= 0 && target is not null && target.Matches(profile);
        var matchesMicrosoftPinyin = isInputProcessor
            && clsid == TsfProfile.MicrosoftPinyinClsid
            && profileGuid == TsfProfile.MicrosoftPinyinProfile;
        var matchesWeType = isInputProcessor
            && clsid == TsfProfile.WeTypeClsid
            && profileGuid == TsfProfile.WeTypeProfile;
        var foreground = NativeMethods.GetForegroundWindow() == window.Handle;
        var (_, imeFileName) = window.ReadImeIdentityForDiagnostics();
        // 同一时刻的 IMM 模式只读对照：TSF 侧说“目标输入法是活动 profile”，不代表 IMM 上下文处于中文模式。
        // 只产生英文字流时，必须能区分“profile 没生效”和“IMM 上下文仍是英文/非 native 模式”。
        var imm = window.ReadImmModeDiagnostics();

        var identityName = isInputProcessor
            ? (matchesMicrosoftPinyin
                ? "microsoft-pinyin"
                : matchesWeType
                    ? "wetype"
                    : "other-input-processor")
            : profile.ProfileTypeName;

        log.Record("tsf", "msctf-api", "identity-snapshot", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["snapshot_index"] = _snapshotCount,
            ["stage"] = "GetActiveProfile",
            ["hresult"] = $"0x{hr:X8}",
            ["query_ok"] = hr >= 0,
            ["identity_name"] = identityName,
            ["profile_type"] = profile.ProfileTypeName,
            ["langid"] = $"0x{profile.LangId:X4}",
            ["clsid"] = clsid.ToString(),
            ["profile_guid"] = profileGuid.ToString(),
            ["flags"] = $"0x{profile.Flags:X}",
            ["flags_active"] = profile.IsActive,
            ["flags_enabled"] = profile.IsEnabled,
            ["is_microsoft_pinyin"] = matchesMicrosoftPinyin,
            ["is_wetype"] = matchesWeType,
            ["configured_target_name"] = target?.Name ?? "(none configured)",
            ["configured_target_clsid"] = target?.Clsid.ToString() ?? "(n/a)",
            ["configured_target_profile_guid"] = target?.ProfileGuid.ToString() ?? "(n/a)",
            ["configured_target_langid"] = target is null ? "(n/a)" : $"0x{target.LangId:X4}",
            ["matches_configured_profile"] = matchesTarget,
            // 登记时刻（回调内）与查询时刻（消息过程外）必须分开记录：两者之间的间隔可能发生 profile 切换，
            // 晚查到的值不能被写成“事件瞬间的身份”。
            ["registered_at_event_seq"] = registeredAtSeq,
            ["registered_composition_id"] = registeredCompositionId,
            ["queried_at_event_seq"] = window.LastLoggedSequence,
            ["query_is_after_the_event_not_at_it"] = registeredAtSeq >= 0,
            ["foreground_is_own_window"] = foreground,
            ["imm_default_ime_window"] = DescribeDefaultImeWindow(window),
            ["imm_ime_file_name"] = imeFileName,
            ["imm_himc"] = imm.Himc,
            ["imm_query_ok"] = imm.Ok,
            ["imm_open_status"] = imm.Open,
            ["imm_conversion"] = $"0x{imm.Conversion:X8}",
            ["imm_cmode_native"] = imm.Native,
            ["imm_cmode_roman"] = imm.Roman,
            // 只在“配置的目标 profile 就是当前活动项”时才谈一致性：其它输入法活动时两者本来就不该一致。
            ["configured_profile_active_and_imm_native"] = matchesTarget && imm.Native,
            ["identity_note"] = matchesTarget
                ? $"the configured target ({target?.Name}) is the actual active profile at this instant"
                : $"the actual active profile is NOT the configured target ({target?.Name ?? "(none)"}) at this instant",
            ["unit"] = "TSF TF_INPUTPROCESSORPROFILE (read-only)",
            ["scope"] = "read-only GetActiveProfile; no activation, no configuration change",
            ["identity_source"] = "system-reported active profile; the --profile argument is not used as identity",
        });

        return new IdentitySnapshot(
            label, matchesTarget, matchesWeType, hr >= 0, foreground, profile.ProfileTypeName,
            clsid.ToString(), profileGuid.ToString(), profile.LangId, profile.Flags, identityName);
    }

    /// <summary>
    /// 回调内登记的一条待查身份：只带标签与当时的 seq / compositionId，不带任何原生句柄。
    /// </summary>
    /// <param name="Label">查询时刻的标签。</param>
    /// <param name="EventSeq">登记时最后一条已写出日志的 seq（事件时刻）。</param>
    /// <param name="CompositionId">登记时的组合会话序号。</param>
    private sealed record PendingProbe(string Label, long EventSeq, int CompositionId);

    private static string DescribeDefaultImeWindow(ImeProbeWindow window)
    {
        var handle = NativeMethods.ImmGetDefaultIMEWnd(window.Handle);
        return handle == IntPtr.Zero ? "(null: no IME UI window for this hwnd)" : $"0x{handle.ToInt64():X}";
    }

    /// <summary>把已记录的身份快照序列化成 JSONL，供独立核对。</summary>
    internal static string SerializeSnapshots(IEnumerable<IdentitySnapshot> snapshots)
        => JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = false });
}

/// <summary>一次身份快照的只读结论。</summary>
/// <param name="Label">查询时刻的标签（场景起点/首次组合/结果时刻等）。</param>
/// <param name="MatchesConfiguredProfile">系统报告的实际活动项是否就是本探针配置的微软拼音。</param>
/// <param name="MatchesWeType">实际活动项是否是微信输入法（对照用）。</param>
/// <param name="QueryOk">GetActiveProfile 是否返回成功。</param>
/// <param name="ForegroundIsOwnWindow">查询时刻本探针窗口是否仍是前台窗口。</param>
/// <param name="ProfileTypeName">TF_PROFILETYPE_* 名称。</param>
/// <param name="Clsid">实际活动 CLSID。</param>
/// <param name="ProfileGuid">实际活动 ProfileGuid。</param>
/// <param name="LangId">实际活动语言 ID。</param>
/// <param name="Flags">TF_INPUTPROCESSORPROFILE.Flags 原始值。</param>
/// <param name="IdentityName">便于阅读的身份归类。</param>
internal sealed record IdentitySnapshot(
    string Label,
    bool MatchesConfiguredProfile,
    bool MatchesWeType,
    bool QueryOk,
    bool ForegroundIsOwnWindow,
    string ProfileTypeName,
    string Clsid,
    string ProfileGuid,
    ushort LangId,
    uint Flags,
    string IdentityName);
