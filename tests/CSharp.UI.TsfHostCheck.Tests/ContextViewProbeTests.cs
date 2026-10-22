using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// context/view 只读几何对照探针的纯策略测试：只读会话标志、临时范围选择、范围精确匹配、
/// 状态分类（含“会话未执行”与“真实几何不一致”的区分）、默认关闭、缺少 context 的错误传播，
/// 以及 R21 的 RequestEditSession 参数 ABI 声明。真实 TSF 会话与截图仍由 Codex 在隔离工具中运行。
/// 这些测试不初始化 COM、不创建窗口、不发送输入。
/// </summary>
public sealed class ContextViewProbeTests
{
    [Fact]
    public void ReadOnlySessionFlagsAreSyncReadOnly()
    {
        var flags = ContextViewProbe.ReadOnlySessionFlags;

        Assert.Equal(TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READ, flags);
        // 绝不能请求写位或异步会话：探针只读，不得改文档/组合/选区。
        // TF_ES_READWRITE == 0x6 含 READ 位，因此这里单独检查写位（0x4）。
        const uint writeBit = 0x4;
        Assert.Equal(0u, flags & writeBit);
        Assert.NotEqual(TsfFlags.TF_ES_READWRITE, flags & TsfFlags.TF_ES_READWRITE);
        Assert.Equal(0u, flags & TsfFlags.TF_ES_ASYNC);
        Assert.Equal(TsfFlags.TF_ES_READ, flags & TsfFlags.TF_ES_READ);
    }

    [Fact]
    public void ResolveRangePrefersCompositionSpanThenCaret()
    {
        Assert.Equal(new ContextViewRange(2, 5, "composition"),
            ContextViewProbe.ResolveRange(true, 2, 5, 5));
        Assert.Equal(new ContextViewRange(7, 7, "caret"),
            ContextViewProbe.ResolveRange(false, 0, 0, 7));
        Assert.Equal(new ContextViewRange(3, 3, "composition-empty"),
            ContextViewProbe.ResolveRange(true, 3, 3, 3));
    }

    [Fact]
    public void ResolveRangeClampsNegativeCaretAndIgnoresInvalidSpan()
    {
        Assert.Equal(new ContextViewRange(0, 0, "caret"),
            ContextViewProbe.ResolveRange(false, 0, 0, -4));
        // 组合标记为 true 但 span 非法（负起点/倒序）时退回 caret，不构造非法范围。
        Assert.Equal(new ContextViewRange(6, 6, "composition-empty"),
            ContextViewProbe.ResolveRange(true, -1, 4, 6));
        Assert.Equal(new ContextViewRange(6, 6, "composition-empty"),
            ContextViewProbe.ResolveRange(true, 5, 2, 6));
    }

    [Fact]
    public void RangeMatchRequiresSameStartAndSameLength()
    {
        var requested = new ContextViewRange(2, 5, "composition");

        Assert.True(ContextViewProbe.RangeMatches(requested, 2, 3));
        // 起点相同但长度不同（只比较起点会误判）。
        Assert.False(ContextViewProbe.RangeMatches(requested, 2, 2));
        Assert.False(ContextViewProbe.RangeMatches(requested, 2, 4));
        // 长度相同但起点不同（只比较长度会误判）。
        Assert.False(ContextViewProbe.RangeMatches(requested, 3, 3));
        Assert.False(ContextViewProbe.RangeMatches(requested, 0, 3));
        // 空范围（caret）也必须精确相同。
        Assert.True(ContextViewProbe.RangeMatches(new ContextViewRange(4, 4, "caret"), 4, 0));
        Assert.False(ContextViewProbe.RangeMatches(new ContextViewRange(4, 4, "caret"), 5, 0));
    }

    [Fact]
    public void OkRequiresEveryGeometryItemAndAnExecutedSession()
    {
        Assert.Equal(ContextViewProbeStatus.Ok,
            ContextViewProbe.Classify(0, true, 0, true, 0, true, true, true));

        // 任何一项不一致都不能是 ok（R20：总体判定覆盖全部几何项）。
        Assert.Equal(ContextViewProbeStatus.Mismatch,
            ContextViewProbe.Classify(0, true, 0, true, 0, false, true, true));
        Assert.Equal(ContextViewProbeStatus.Mismatch,
            ContextViewProbe.Classify(0, true, 0, true, 0, true, false, true));
        Assert.Equal(ContextViewProbeStatus.Mismatch,
            ContextViewProbe.Classify(0, true, 0, true, 0, true, true, false));
    }

    [Fact]
    public void SessionNotExecutedIsDistinctFromGeometryMismatch()
    {
        // RequestEditSession 成功但 DoEditSession 没被进入：会话未执行，不能说成几何不一致。
        Assert.Equal(ContextViewProbeStatus.SessionNotExecuted,
            ContextViewProbe.Classify(0, false, 0, false, 0, false, false, false));
        // 即使各 match 布尔碰巧为真，未执行也绝不能判 ok。
        Assert.Equal(ContextViewProbeStatus.SessionNotExecuted,
            ContextViewProbe.Classify(0, false, 0, true, 0, true, true, true));
        // 会话执行了、范围一致、几何确实不同 → mismatch。
        Assert.Equal(ContextViewProbeStatus.Mismatch,
            ContextViewProbe.Classify(0, true, 0, true, 0, false, true, true));
    }

    [Fact]
    public void UnknownExtentIsSessionFailureNotRangeMismatch()
    {
        // extent 从未取得（GetExtent 失败）：范围事实未知，不能断言“范围不一致”。
        Assert.Equal(ContextViewProbeStatus.SessionFailed,
            ContextViewProbe.Classify(0, true, TsfHResult.E_FAIL, null, TsfHResult.E_FAIL,
                false, false, false));
        // 即便会话 HRESULT 是成功、只是没有 extent 事实，也不能编造成 range-mismatch。
        Assert.Equal(ContextViewProbeStatus.SessionFailed,
            ContextViewProbe.Classify(0, true, 0, null, 0, false, false, false));
        // 只有“确实取得 extent 且与请求不同”才是 range-mismatch。
        Assert.Equal(ContextViewProbeStatus.RangeMismatch,
            ContextViewProbe.Classify(0, true, 0, false, 0, false, false, false));
    }

    [Theory]
    [InlineData(TsfHResult.E_FAIL, true, 0, true, 0, true, ContextViewProbeStatus.RequestFailed)]
    [InlineData(0, false, 0, true, 0, true, ContextViewProbeStatus.SessionNotExecuted)]
    [InlineData(0, true, TsfHResult.E_FAIL, true, 0, true, ContextViewProbeStatus.SessionFailed)]
    [InlineData(0, true, 0, false, 0, true, ContextViewProbeStatus.RangeMismatch)]
    [InlineData(0, true, 0, true, TsfHResult.TS_E_NOLAYOUT, true, ContextViewProbeStatus.Refused)]
    [InlineData(0, true, 0, true, TsfHResult.TS_E_NOLOCK, true, ContextViewProbeStatus.Refused)]
    [InlineData(0, true, 0, true, 0, true, ContextViewProbeStatus.Ok)]
    [InlineData(0, true, 0, true, 0, false, ContextViewProbeStatus.Mismatch)]
    public void ClassifyPreservesRefusalsAndMismatch(
        int requestHr, bool sessionRan, int sessionHr, bool rangeMatch, int firstQueryHr,
        bool rectMatch, string expected)
    {
        // 分类只看传入项：screen/wnd 与 rect 同取 rectMatch，避免 Theory 行噪声。
        Assert.Equal(expected, ContextViewProbe.Classify(requestHr, sessionRan, sessionHr, rangeMatch,
            firstQueryHr, rectMatch, rectMatch, rectMatch));
    }

    [Fact]
    public void ClassifyPrecedencePrefersRootCauseOverGeometry()
    {
        // 请求失败优先于一切（没有会话就谈不上范围/几何）。
        Assert.Equal(ContextViewProbeStatus.RequestFailed,
            ContextViewProbe.Classify(TsfHResult.E_FAIL, true, TsfHResult.E_FAIL, false,
                TsfHResult.E_FAIL, false, false, false));
        // 会话失败优先于范围不一致。
        Assert.Equal(ContextViewProbeStatus.SessionFailed,
            ContextViewProbe.Classify(0, true, TsfHResult.E_FAIL, false, TsfHResult.E_FAIL,
                false, false, false));
        // 范围不一致优先于几何拒绝：不能拿不同范围的矩形继续谈几何。
        Assert.Equal(ContextViewProbeStatus.RangeMismatch,
            ContextViewProbe.Classify(0, true, 0, false, TsfHResult.TS_E_NOLAYOUT, false, false, false));
    }

    [Fact]
    public void DisabledProbeReturnsNullWithoutTouchingContext()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);

        var result = ContextViewProbe.RunIfEnabled(false, "test", null, 0, store,
            StoreTestHelpers.NoTrace, new ContextViewRange(0, 0, "caret"));

        Assert.Null(result);
    }

    [Fact]
    public void MissingContextIsReportedWithoutRetry()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);

        var result = ContextViewProbe.RunIfEnabled(true, "test", null, 0, store,
            StoreTestHelpers.NoTrace, new ContextViewRange(0, 0, "caret"));

        Assert.NotNull(result);
        Assert.Equal(ContextViewProbeStatus.ContextUnavailable, result!.Value.Status);
        Assert.True(result.Value.RequestHResult < 0);
        Assert.True(result.Value.SessionHResult < 0);
        Assert.False(result.Value.RectMatch);
        Assert.False(result.Value.SessionRan);
        Assert.False(result.Value.RangeMatch);
    }

    [Fact]
    public void ScenarioOptionsProbeIsOffByDefault()
    {
        var options = new ScenarioOptions { OutDir = "unused" };

        Assert.False(options.ProbeContextView);
        Assert.False(options.HiddenHost);
        Assert.False(options.Sta);
    }

    [Fact]
    public void SameRectComparesAllEdges()
    {
        var rect = TsfRect.FromLtrb(10, 20, 30, 40);

        Assert.True(ContextViewProbe.SameRect(rect, TsfRect.FromLtrb(10, 20, 30, 40)));
        Assert.False(ContextViewProbe.SameRect(rect, TsfRect.FromLtrb(10, 20, 31, 40)));
        Assert.False(ContextViewProbe.SameRect(rect, TsfRect.FromLtrb(10, 21, 30, 40)));
    }

    [Fact]
    public void ProbeRecordExposesReleaseFailuresInsteadOfSwallowingThem()
    {
        var record = new ContextViewProbeRecord();

        // 尚未释放：不是失败，但也不能声称成功。
        Assert.Null(record.ReleaseFailure);
        Assert.False(record.AllReleasesSucceeded);

        record.ReleaseViewOutcome = "released";
        record.ReleaseRangeAcpOutcome = "released";
        record.ReleaseRangeOutcome = "not-created";
        record.ReleaseAttempted = true;
        Assert.True(record.AllReleasesSucceeded);
        Assert.Null(record.ReleaseFailure);

        record.ReleaseRangeAcpOutcome = "failed:InvalidComObjectException";
        Assert.False(record.AllReleasesSucceeded);
        Assert.Contains("range-acp", record.ReleaseFailure, StringComparison.Ordinal);
    }

    /// <summary>
    /// R21：`ITfContext::RequestEditSession` 的第二个参数必须是精确的 `ITfEditSession`
    /// （SDK msctf.idl：`[in] ITfEditSession *pes`）。`[MarshalAs(UnmanagedType.IUnknown)] object`
    /// 只传 IUnknown 身份，TSF 仍按契约调用 slot 3(DoEditSession)，真实隐藏会话因此原生崩溃。
    /// </summary>
    [Fact]
    public void RequestEditSessionDeclaresExactEditSessionParameter()
    {
        var method = typeof(ITfContext).GetMethod(nameof(ITfContext.RequestEditSession));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(uint), parameters[0].ParameterType);
        Assert.Equal(typeof(ITfEditSession), parameters[1].ParameterType);
        Assert.Equal(typeof(uint), parameters[2].ParameterType);
        Assert.Equal(typeof(int).MakeByRefType(), parameters[3].ParameterType);
        Assert.True(parameters[3].IsOut);

        var marshals = parameters[1].GetCustomAttributes<MarshalAsAttribute>().ToArray();
        Assert.Empty(marshals);
    }

    [Fact]
    public void EditSessionInterfaceHasSingleDoEditSessionSlot()
    {
        // IUnknown 占 0-2，DoEditSession 是 slot 3：CLR 必须把本接口排成 1 个方法。
        var methods = typeof(ITfEditSession).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Single(methods);
        Assert.Equal(nameof(ITfEditSession.DoEditSession), methods[0].Name);
        Assert.Equal(typeof(uint), methods[0].GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int), methods[0].ReturnType);
    }

    // ---------------------------------------------------------------------
    // 真实控制流回归：从 ContextViewProbe.Run → RequestEditSession →
    // ContextViewEditSession.DoEditSession 的实际分支一路到最终 status。
    // 不使用手工拼装的 Classify 参数，而是让真实代码路径产出这些事实。
    // ---------------------------------------------------------------------

    /// <summary>
    /// R20 剩余控制流：`RunSteps` 在“确实取得 extent 但范围不同”时返回 S_OK（检查完成），
    /// 最终 status 必须是 range-mismatch；若返回 E_FAIL，TSF 会传成 sessionHr&lt;0，
    /// 只会得到 session-failed。本用例从 Run 入口覆盖该实际分支。
    /// </summary>
    [Fact]
    public void RealRangeMismatchBranchEndsAsRangeMismatchNotSessionFailed()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var requested = new ContextViewRange(2, 5, "composition");
        var fake = new ProbeContextDouble(requested: requested, actualAcp: 3, actualCch: 2);
        var rangeAcp = new FakeRangeAcpAccess(TsfHResult.S_OK, actualAcp: 3, actualCch: 2);

        var result = ContextViewProbe.Run("test", fake, 7, store, StoreTestHelpers.NoTrace,
            requested, rangeAcp);

        // 会话确实执行、RequestEditSession 与 *phrSession 都是 S_OK。
        Assert.True(result.SessionRan);
        Assert.Equal(TsfHResult.S_OK, result.RequestHResult);
        Assert.Equal(TsfHResult.S_OK, result.SessionHResult);
        // 但范围事实是“不同”：必须是 range-mismatch，不能退化成 session-failed。
        Assert.False(result.RangeMatch);
        Assert.Equal(ContextViewProbeStatus.RangeMismatch, result.Status);
        Assert.Equal(TsfHResult.S_OK, fake.DoEditSessionHResult);
        // 范围不一致后不得继续查询几何（也不得拿其矩形证明同一范围）。
        Assert.False(fake.GeometryQueried);
        Assert.Equal(0, fake.StoreTextExtCalls);
        // SetExtent 确实按请求范围设置过（证明走的是真实范围设置分支）。
        Assert.Equal((2, 3), rangeAcp.LastSetExtent);
    }

    /// <summary>
    /// 同一入口的另一侧：`GetExtent` 真实失败必须如实停止（不再查询几何），
    /// 且最终状态是 session-failed 而不是 range-mismatch —— 一般会话失败不得被错判成范围不一致。
    /// </summary>
    [Fact]
    public void RealExtentFailureStopsAndStaysSessionFailed()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var requested = new ContextViewRange(2, 5, "composition");
        var fake = new ProbeContextDouble(requested: requested, actualAcp: 2, actualCch: 3);
        var rangeAcp = new FakeRangeAcpAccess(TsfHResult.TS_E_NOLOCK, actualAcp: 2, actualCch: 3);

        var result = ContextViewProbe.Run("test", fake, 7, store, StoreTestHelpers.NoTrace,
            requested, rangeAcp);

        Assert.True(result.SessionRan);
        Assert.Equal(TsfHResult.S_OK, result.RequestHResult);
        Assert.Equal(TsfHResult.TS_E_NOLOCK, result.SessionHResult);
        Assert.Equal(ContextViewProbeStatus.SessionFailed, result.Status);
        Assert.False(result.RangeMatch);
        // extent 失败即停止：不再查询几何。
        Assert.False(fake.GeometryQueried);
        Assert.Equal(0, fake.StoreTextExtCalls);
        Assert.Equal(1, rangeAcp.GetExtentCalls);
    }

    /// <summary>
    /// 成功路径的对照：范围一致且几何全部一致时才是 ok，证明上面的用例不是靠“永远不 ok”通过。
    /// </summary>
    [Fact]
    public void RealMatchingRangeStillReachesOk()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        // 发布真实布局：store 侧 GetTextExt/GetScreenExt/GetWnd 才可能成功（空文档 → 空 advances）。
        var client = TsfRect.FromLtrb(91, 125, 829, 489);
        StoreTestHelpers.Publish(store, Array.Empty<int>(), rowHeight: 39, client: client,
            originX: 107, originY: 139);
        store.AttachWindow(new IntPtr(0x1234));
        // R24：显示表面改为独立查询；假 HWND 无真实客户区，故注入表面供探针对照。
        store.DisplaySurfaceProvider = () => new DisplaySurfaceResult(
            TsfHResult.S_OK, client, true, null);
        var requested = new ContextViewRange(0, 0, "caret");
        var fake = new ProbeContextDouble(requested: requested, actualAcp: 0, actualCch: 0,
            viewRect: TsfRect.FromLtrb(107, 139, 108, 178),
            viewScreen: client,
            viewHwnd: new IntPtr(0x1234),
            storeHwnd: new IntPtr(0x1234));
        var rangeAcp = new FakeRangeAcpAccess(TsfHResult.S_OK, actualAcp: 0, actualCch: 0);

        // 探针的 store 侧查询要求读锁：在真实 RequestLock 授予的读锁内执行（模拟 TSF 会话）。
        var result = default(ContextViewProbeResult);
        var probeHr = StoreTestHarness.UnderReadLock(store, sink, () =>
        {
            result = ContextViewProbe.Run("test", fake, 7, store, StoreTestHelpers.NoTrace,
                requested, rangeAcp);
            return TsfHResult.S_OK;
        }, out var requestHr, out _);

        Assert.Equal(TsfHResult.S_OK, requestHr);
        Assert.Equal(TsfHResult.S_OK, probeHr);
        Assert.Equal(ContextViewProbeStatus.Ok, result.Status);
        Assert.True(result.RangeMatch);
        Assert.True(result.RectMatch && result.ScreenMatch && result.WndMatch);
        Assert.True(fake.GeometryQueried);
        // 每个 out/QI 指针各释放一次（临时 range 与 QI 出的 ITfRangeACP）。
        Assert.Equal(2, rangeAcp.ReleaseCalls);
    }
}
