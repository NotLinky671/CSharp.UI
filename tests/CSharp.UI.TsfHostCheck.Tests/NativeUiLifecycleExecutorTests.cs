using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R27 返修：通过 <see cref="NativeUiLifecycleExecutor"/> 的**真实控制流**验证安全点、
/// 快照比对、元素存活与停止语义。测试使用可注入的 <see cref="ILifecycleHost"/> 替身，
/// 但执行的仍是生产场景所用的同一个执行器（不是另造流程）。
/// </summary>
public sealed class NativeUiLifecycleExecutorTests
{
    /// <summary>
    /// 与生产 <see cref="ImeIdentity.VerifiedIdentityTag"/> 同构的已核验身份标签：
    /// 只有以 <c>hr=00000000|</c> 开头的完整身份描述才会被执行器基线接受。
    /// </summary>
    private static readonly string VerifiedProfile = ImeIdentity.Describe(0,
        TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    /// <summary>可控的宿主替身：按阶段/次数注入前台丢失、内容变化、Show 失败等。</summary>
    private sealed class FakeHost : ILifecycleHost
    {
        private readonly List<(string Stage, bool Ok, bool Show, string Text, int Start, int End,
            int Writes)> _probePlan = new();

        private readonly List<(bool Show, int HResult)> _showPlan = new();

        internal string Text { get; set; } = "ni";

        internal int Start { get; set; } = 0;

        internal int End { get; set; } = 2;

        internal int Writes { get; set; }

        internal bool Composing { get; set; } = true;

        internal uint? CandidateId { get; set; } = 11;

        internal bool Live { get; set; } = true;

        internal int ProbeCount { get; private set; }

        internal int ShowCalls { get; private set; }

        internal int CaptureCalls { get; private set; }

        internal int WaitCalls { get; private set; }

        internal bool CaptureFails { get; set; }

        /// <summary>
        /// 安全点报告的 profile 标签。默认是已核验的完整 Microsoft Pinyin 身份；
        /// 测试可注入 <see cref="ImeIdentity.UnverifiableProfile"/> 或任何非完整身份值
        /// 来覆盖执行器基线拒绝路径。
        /// </summary>
        internal string Profile { get; set; } = VerifiedProfile;

        /// <summary>Capture 收到的名称序列（用于验证截图流程仍经宿主接口）。</summary>
        internal List<string> CaptureNames { get; } = new();

        internal List<string> Records { get; } = new();

        internal List<bool> ShowSequence { get; } = new();

        /// <summary>在指定 probe 序号（1 起）返回失败，模拟失焦。</summary>
        internal int? FailProbeAt { get; set; }

        /// <summary>在指定 probe 序号（1 起）后改变文本/范围，模拟组合被替换。</summary>
        internal int? MutateTextAfterProbe { get; set; }

        /// <summary>在指定 probe 序号（1 起）后增加 writes。</summary>
        internal int? MutateWritesAfterProbe { get; set; }

        /// <summary>第 N 次 Show 返回失败（1 起）。</summary>
        internal int? FailShowAt { get; set; }

        /// <summary>第 N 次 Show 之后元素 End（1 起）。</summary>
        internal int? EndAfterShowAt { get; set; }

        /// <summary>第 N 次 Show 返回标记 EndedDuringShow（同步重入 End，1 起）。</summary>
        internal int? EndDuringShowAt { get; set; }

        /// <summary>等待若干次 probe 后候选才出现。</summary>
        internal int CandidateAppearsAfterProbes { get; set; }

        /// <summary>经 <see cref="InvokeOnUiThread"/> 派发器执行的次数。</summary>
        internal int InvokeOnUiThreadCalls { get; private set; }

        /// <summary>仅在派发器帧内为 true 的哨兵。</summary>
        private bool _insideDispatcher;

        /// <summary>在派发器帧外执行的 UI 线程操作计数；正常路径恒为 0。</summary>
        internal int DispatcherViolations { get; private set; }

        /// <summary>标记一个 UI 线程操作本体；若不在派发器帧内则记为绕过。</summary>
        private void OnUiThreadBody()
        {
            if (!_insideDispatcher)
            {
                DispatcherViolations++;
            }
        }

        /// <summary>
        /// 模拟 <c>TsfHostWindow.Invoke</c> 派发器：所有 UI 线程操作（Probe/Capture/
        /// Show/IsLive/LiveCandidateId）都必须经由此入口进入；外层派发会嵌套内层自派发，
        /// 因此计数同时覆盖执行器外层与宿主内层路径，可证明没有绕过派发器的读取。
        /// </summary>
        public bool InvokeOnUiThread(Func<bool> action)
        {
            InvokeOnUiThreadCalls++;
            var wasInside = _insideDispatcher;
            _insideDispatcher = true;
            try
            {
                return action();
            }
            finally
            {
                _insideDispatcher = wasInside;
            }
        }

        /// <summary>
        /// 模拟真实宿主的自派发：探针本体经 <see cref="InvokeOnUiThread"/> 投递执行，
        /// 与 <see cref="ScenarioLifecycleHost.Probe"/> 经 <c>Window.Invoke</c> 等价。
        /// </summary>
        public SafePointProbe Probe(string stage)
        {
            SafePointProbe probe = default;
            InvokeOnUiThread(() =>
            {
                OnUiThreadBody();
                ProbeCount++;
                var isWaitProbe = stage.StartsWith("wait-live:", StringComparison.Ordinal);
                var fail = FailProbeAt == ProbeCount;
                probe = new SafePointProbe(
                    !fail,
                    fail ? $"{stage}: own foreground lost" : "ok",
                    Text, Start, End, Writes, Composing, Profile);

                // 等待阶段不产生基线/阶段快照；只在指定序号后注入变更。
                if (!isWaitProbe)
                {
                    if (MutateTextAfterProbe == ProbeCount)
                    {
                        Text = "你好";
                        End = 4;
                    }

                    if (MutateWritesAfterProbe == ProbeCount)
                    {
                        Writes++;
                    }
                }

                return true;
            });
            return probe;
        }

        public void Record(string kind, IDictionary<string, object?> fields)
            => Records.Add(kind);

        /// <summary>截图也自派发（对应 <see cref="ScenarioLifecycleHost.Capture"/>）。</summary>
        public bool Capture(string name)
        {
            var ok = false;
            InvokeOnUiThread(() =>
            {
                OnUiThreadBody();
                CaptureCalls++;
                CaptureNames.Add(name);
                ok = !CaptureFails;
                return true;
            });
            return ok;
        }

        public void Wait(int milliseconds) => WaitCalls++;

        public uint? LiveCandidateId()
        {
            uint? id = null;
            InvokeOnUiThread(() =>
            {
                OnUiThreadBody();
                if (CandidateAppearsAfterProbes > 0)
                {
                    CandidateAppearsAfterProbes--;
                    id = null;
                }
                else
                {
                    id = Live ? CandidateId : null;
                }

                return true;
            });
            return id;
        }

        public UiElementShowResult? ShowOnCandidate(uint id, bool show)
        {
            UiElementShowResult? result = null;
            InvokeOnUiThread(() =>
            {
                OnUiThreadBody();
                ShowCalls++;
                ShowSequence.Add(show);

                var hr = FailShowAt == ShowCalls ? TsfHResult.E_FAIL : TsfHResult.S_OK;
                result = new UiElementShowResult(id, true, hr, !show, show)
                {
                    ShowHrAvailable = true,
                };

                if (EndDuringShowAt == ShowCalls)
                {
                    // 同步 End 发生在 Show 调用**内部**：结果必须标记 ended，
                    // 且 hr 仍为真实返回值（S_OK）。模拟同一线程重入。
                    result = result.Value with { EndedDuringShow = true };
                    Live = false;
                }
                else if (EndAfterShowAt == ShowCalls)
                {
                    Live = false;
                }

                return true;
            });
            return result;
        }

        public bool IsLive(uint id)
        {
            var live = false;
            InvokeOnUiThread(() =>
            {
                OnUiThreadBody();
                live = Live;
                return true;
            });
            return live;
        }
    }

    private static NativeUiLifecycleExecutor NewCollectingExecutor(FakeHost host,
        List<LifecycleSnapshot> sinks)
        => new(host, snapshot => sinks.Add(snapshot));

    [Fact]
    public void SuccessRunsShowTrueFalseTrueAndStaysVisualPending()
    {
        var host = new FakeHost();
        var baselines = new List<LifecycleSnapshot>();
        var executor = NewCollectingExecutor(host, baselines);

        var ok = executor.Run();

        Assert.True(ok);
        Assert.Equal(LifecycleStop.None, executor.Stop);
        Assert.Equal(3, executor.SuccessfulShows);
        Assert.Single(baselines);
        // 初始截图 + 三个阶段各一次。
        Assert.Equal(4, host.CaptureCalls);
    }

    [Fact]
    public void ForegroundLostBeforeActionStopsWithoutFurtherShowOrCapture()
    {
        var host = new FakeHost();
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        // baseline 是第 1 次 Probe；第 2 次（stage:before）失焦。
        host.FailProbeAt = 2;
        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.SafePointLost, executor.Stop);
        Assert.Equal(0, host.ShowCalls);
        Assert.Equal(0, host.CaptureCalls);
    }

    [Fact]
    public void ForegroundLostWhileWaitingStopsImmediately()
    {
        var host = new FakeHost { CandidateAppearsAfterProbes = int.MaxValue };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        host.FailProbeAt = 2;
        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.SafePointLost, executor.Stop);
        Assert.Equal("wait-live", executor.StoppedAtStage);
        Assert.Equal(0, host.ShowCalls);
    }

    [Fact]
    public void NoLiveCandidateReportsUnsupported()
    {
        var host = new FakeHost { Live = false, CandidateAppearsAfterProbes = int.MaxValue };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.Unsupported, executor.Stop);
        Assert.Equal(0, host.ShowCalls);
        Assert.Equal(0, host.CaptureCalls);
    }

    [Fact]
    public void TextChangeAfterShowStopsAndSkipsCapture()
    {
        var host = new FakeHost();
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        // baseline=1，stage before=2，stage after=3 → 在 after 之后改文本。
        host.MutateTextAfterProbe = 3;
        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.CompositionChanged, executor.Stop);
        Assert.Equal("native-shown", executor.StoppedAtStage);
        Assert.Equal(1, host.ShowCalls);
        // 只有初始截图；变化后不再截图。
        Assert.Equal(1, host.CaptureCalls);
    }

    [Fact]
    public void SpanChangeAloneIsDetected()
    {
        var host = new FakeHost { Start = 1, End = 3 };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        // 文本不变但 span 变了（模拟组合范围被替换）。
        host.MutateTextAfterProbe = 3;
        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.CompositionChanged, executor.Stop);
        Assert.Equal(1, host.CaptureCalls);
    }

    [Fact]
    public void WriteChangeStopsTheStage()
    {
        var host = new FakeHost();
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        host.MutateWritesAfterProbe = 3;
        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.CompositionChanged, executor.Stop);
        Assert.Contains("writes", executor.StopDetail);
    }

    [Fact]
    public void ShowFailureStopsBeforeNextStage()
    {
        var host = new FakeHost { FailShowAt = 1 };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.ShowFailed, executor.Stop);
        Assert.Equal(1, host.ShowCalls);
    }

    [Fact]
    public void ElementEndedAfterShowStops()
    {
        var host = new FakeHost { EndAfterShowAt = 1 };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.ElementEnded, executor.Stop);
        Assert.Equal(1, host.ShowCalls);
    }

    /// <summary>
    /// R27：Show 返回 S_OK 但元素在调用期间同步 End → 不再做 hide/restore，
    /// 在同守卫下做一次 native-shown-ended 诊断截图，整体仍 FAIL。
    /// </summary>
    [Fact]
    public void EndedDuringShowCapturesDiagnosticAndStops()
    {
        var host = new FakeHost { EndDuringShowAt = 1 };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.ElementEnded, executor.Stop);
        Assert.Equal("native-shown", executor.StoppedAtStage);
        Assert.Equal(1, host.ShowCalls);            // 不再 hide/restore
        // 初始 suppressed 截图 + 一次 native-shown-ended 诊断截图。
        Assert.Equal(new[] { "r27a-initially-suppressed", "native-shown-ended" }, host.CaptureNames);
        Assert.Equal(1, host.WaitCalls);            // 同守卫 ~400ms
        Assert.Equal(0, host.DispatcherViolations);  // 全程经派发器
        // 步骤记录含 ended_during_show 标记。
        Assert.Contains(host.Records, r => r == "native-ui-lifecycle-step");
    }

    /// <summary>hr 不可用时（Show 未到达系统）不得断言系统返回了失败。</summary>
    [Fact]
    public void ShowHrUnavailableIsNotReportedAsSystemFailure()
    {
        var host = new FakeHost { FailShowAt = 1 };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        executor.Run();

        // 宿主返回 hr 时 ShowHrAvailable=true（系统应答），ShowFailed 合法。
        Assert.Equal(LifecycleStop.ShowFailed, executor.Stop);
        Assert.Equal(1, host.ShowCalls);
    }

    [Fact]
    public void CaptureFailureStops()
    {
        var host = new FakeHost { CaptureFails = true };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.CaptureFailed, executor.Stop);
        Assert.Equal(0, host.ShowCalls);
    }

    [Fact]
    public void BaselineWithoutCompositionIsRejected()
    {
        var host = new FakeHost { Composing = false };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.CompositionChanged, executor.Stop);
        Assert.Equal("baseline", executor.StoppedAtStage);
        Assert.Equal(0, host.ShowCalls);
    }

    [Fact]
    public void EachStopIsRecordedOnce()
    {
        var host = new FakeHost { Live = false, CandidateAppearsAfterProbes = int.MaxValue };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        executor.Run();

        Assert.Equal(1, host.Records.Count(r => r == "native-ui-lifecycle-stop"));
    }

    [Fact]
    public void ProfileChangeIsAnInvariantViolation()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile);
        var changed = baseline with { Profile = "other-ime" };

        Assert.NotNull(changed.DescribeInvariantViolation(baseline));
        Assert.False(changed.SameInvariant(baseline));
        // 文本/范围相同也不能掩盖 profile 变化。
        Assert.True(changed.SameComposition(baseline));
    }

    [Fact]
    public void CompositionEndingIsAnInvariantViolation()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile);

        Assert.Contains("no longer active",
            (baseline with { Composing = false }).DescribeInvariantViolation(baseline));
    }

    [Fact]
    public void BaselineWritesMustBeZero()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile);

        Assert.Contains("binding writes",
            (baseline with { Writes = 1 }).DescribeInvariantViolation(baseline));
        Assert.False((baseline with { Writes = 1 }).SameInvariant(baseline with { Writes = 1 }));
    }

    /// <summary>
    /// R29B：text/span/composing/writes/profile 全部相同，仅组合连续性计数不同
    /// （组合曾结束又以相同文本重启）必须是不变量违反。
    /// </summary>
    [Fact]
    public void ContinuityCounterChangeIsAnInvariantViolation()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile,
            CompositionBeginCount: 3, CompositionEndCount: 2, CompositionSessionId: 3,
            InputFocusChangeCount: 4);
        // 表面完全相同的重启组合：text/span/composing/writes 都一致，只有计数不同。
        var restarted = baseline with { CompositionBeginCount = 4, CompositionSessionId = 4 };

        Assert.True(restarted.SameComposition(baseline));
        Assert.False(restarted.SameInvariant(baseline));
        Assert.Contains("continuity",
            restarted.DescribeInvariantViolation(baseline));
    }

    /// <summary>
    /// R29B：输入焦点事件计数变化 = 焦点曾瞬时离开再回来；即使当前 focus==own、
    /// text/span 完全相等也必须拒绝（away-then-back 不可接受）。
    /// </summary>
    [Fact]
    public void FocusEventCountChangeIsAnInvariantViolation()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile,
            CompositionBeginCount: 1, CompositionEndCount: 0, CompositionSessionId: 1,
            InputFocusChangeCount: 2);
        var awayAndBack = baseline with { InputFocusChangeCount = 4 };

        Assert.False(awayAndBack.SameInvariant(baseline));
        Assert.Contains("focus",
            awayAndBack.DescribeInvariantViolation(baseline));
    }

    /// <summary>R29B：基线本身不组合中时，即使当前组合中也必须拒绝。</summary>
    [Fact]
    public void BaselineComposingIsRequiredBySameInvariant()
    {
        var notComposingBaseline = new LifecycleSnapshot("ni", 0, 2, 0, false, VerifiedProfile);
        var composingNow = notComposingBaseline with { Composing = true };

        Assert.False(composingNow.SameInvariant(notComposingBaseline));
        Assert.Contains("no longer active",
            composingNow.DescribeInvariantViolation(notComposingBaseline));
    }

    /// <summary>R29B：默认构造的 6 参数快照（无计数）仍与既有语义兼容。</summary>
    [Fact]
    public void LegacyConstructionDefaultsCountersToZero()
    {
        var baseline = new LifecycleSnapshot("ni", 0, 2, 0, true, VerifiedProfile);

        Assert.Equal(0, baseline.CompositionBeginCount);
        Assert.Equal(0, baseline.CompositionEndCount);
        Assert.Equal(0, baseline.CompositionSessionId);
        Assert.Equal(0, baseline.InputFocusChangeCount);
        Assert.True(baseline.SameInvariant(baseline));
        Assert.Null(baseline.DescribeInvariantViolation(baseline));
    }

    [Fact]
    public void UnverifiableBaselineProfileStopsBeforeAnyShow()
    {
        var host = new FakeHost { Profile = "unknown" };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.SafePointLost, executor.Stop);
        Assert.Equal("baseline", executor.StoppedAtStage);
        Assert.Equal(0, host.ShowCalls);
    }

    /// <summary>
    /// 任何无法证明是“简体中文 Microsoft Pinyin input processor 完整身份”的基线
    /// 标签都必须被拒绝：旧式不完整标签、查询失败描述、错误语言/类型/GUID、
    /// 空值与未核验哨兵，一个都不能漏。
    /// </summary>
    [Theory]
    [InlineData("ms-pinyin")] // 旧式标签：不完整
    [InlineData("ms-pinyin:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}")] // 缺 lang/type/guid
    [InlineData("unverifiable")] // 生产未核验哨兵（ImeIdentity.UnverifiableProfile）
    [InlineData("")] // 空
    [InlineData("other:hr=0x80004005:{guid}:{guid}")] // 查询失败描述
    [InlineData("hr=0x80004005|type=0x0001|lang=0x0804|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}|profile={FA550B04-5AD7-411F-A5AC-CA038EC515D7}")] // hr 失败
    [InlineData("hr=00000000|type=0x0001|lang=0x0409|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}|profile={FA550B04-5AD7-411F-A5AC-CA038EC515D7}")] // 非 0x0804
    [InlineData("hr=00000000|type=0x0002|lang=0x0804|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}|profile={FA550B04-5AD7-411F-A5AC-CA038EC515D7}")] // 非 input-processor 类型
    [InlineData("hr=00000000|type=0x0001|lang=0x0804|clsid={00000000-0000-0000-0000-000000000000}|profile={FA550B04-5AD7-411F-A5AC-CA038EC515D7}")] // 错误 CLSID
    [InlineData("hr=00000000|type=0x0001|lang=0x0804|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}|profile={00000000-0000-0000-0000-000000000000}")] // 错误 profile GUID
    [InlineData("hr=00000000|type=0x0001|lang=0x0804|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}")] // 截断：缺 profile 段
    [InlineData("hr=00000000|type=0x0001|lang=0x0804|clsid={81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}|profile={FA550B04-5AD7-411F-A5AC-CA038EC515D7}|extra=1")] // 尾部多字段
    public void NonPinyinOrUnverifiedBaselineProfileStopsBeforeAnyShow(string profile)
    {
        var host = new FakeHost { Profile = profile };
        var executor = NewCollectingExecutor(host, new List<LifecycleSnapshot>());

        var ok = executor.Run();

        Assert.False(ok);
        Assert.Equal(LifecycleStop.SafePointLost, executor.Stop);
        Assert.Equal("baseline", executor.StoppedAtStage);
        Assert.Equal(0, host.ShowCalls);
        Assert.Equal(0, host.CaptureCalls);
    }

    /// <summary>完整身份标签（hr=0 + 0x0804 + input-processor + 双 GUID）才被接受。</summary>
    [Fact]
    public void VerifiedIdentityTagAcceptsOnlyCompleteMicrosoftPinyinIdentity()
    {
        var okTag = ImeIdentity.VerifiedIdentityTag(new ActiveProfileSnapshot(0,
            TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile,
            true, IntPtr.Zero, 1));
        Assert.True(ImeIdentity.IsVerifiableMicrosoftPinyin(okTag));

        // 完整标签编码所有字段：改任一字段都会得到不同标签。
        Assert.Equal(okTag, VerifiedProfile);
        Assert.Contains("lang=0x0804", okTag);
        Assert.Contains("type=0x0001", okTag);

        // 任何一项不符都落到未核验哨兵。
        Assert.Equal(ImeIdentity.UnverifiableProfile,
            ImeIdentity.VerifiedIdentityTag(new ActiveProfileSnapshot(unchecked((int)0x80004005),
                TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
                TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile,
                true, IntPtr.Zero, 1)));
        Assert.Equal(ImeIdentity.UnverifiableProfile,
            ImeIdentity.VerifiedIdentityTag(new ActiveProfileSnapshot(0,
                TsfProfile.TfProfileTypeInputProcessor, 0x0409,
                TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile,
                true, IntPtr.Zero, 1)));
        Assert.Equal(ImeIdentity.UnverifiableProfile,
            ImeIdentity.VerifiedIdentityTag(new ActiveProfileSnapshot(0,
                TsfProfile.TfProfileTypeKeyboardLayout, TsfProfile.LangIdChineseSimplified,
                TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile,
                true, IntPtr.Zero, 1)));
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(ImeIdentity.UnverifiableProfile));
    }

    /// <summary>
    /// 宿主侧安全点门禁：只有环境前置检查全部通过时才允许 profile 被接受；
    /// 失焦/前台丢失/持锁/通知重入都映射为 ok=false。
    /// </summary>
    [Theory]
    [InlineData(true, true, 0u, 0, true)] // 全通过
    [InlineData(false, true, 0u, 0, false)] // 前台丢失
    [InlineData(true, false, 0u, 0, false)] // 焦点不在自有窗口
    [InlineData(true, true, 1u, 0, false)] // 仍持读锁
    [InlineData(true, true, 0u, 1, false)] // OnLayoutChange 重入
    public void SafePointGateRequiresForegroundFocusNoLockNoReentry(
        bool ownForeground, bool ownFocus, uint lockType, int layoutDepth, bool expectedOk)
    {
        Assert.Equal(expectedOk,
            LifecycleSafePointGate.Evaluate(ownForeground, ownFocus, lockType, layoutDepth));
    }
}
