using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P2b 第二批：生产原生组合 owner 观察器 + P2a 会话 + <b>真实</b> <see cref="TextField"/>。
/// </summary>
/// <remarks>
/// <para>
/// 每条用例都走真实链路：<see cref="NativeCompositionOwner"/> → <see cref="TextInputCompositionSession"/>
/// → 真实 <see cref="TextCompositionClient"/> → 真实 TI-03 编辑事务与 Binding setter。
/// 不使用 BindingRecorder，也不只断言 mock dispatcher。
/// </para>
/// <para>
/// 断言绑定写入次数、撤销历史条目、真实文档版本/选区、显示镜像与显式拒绝结果。
/// </para>
/// </remarks>
public sealed class NativeCompositionOwnerTests
{
    // ---------------------------------------------------------------- 真实宿主

    private sealed class ScriptedBinding
    {
        public ScriptedBinding(string initial)
        {
            Text = initial;
            Binding = CSharp.UI.Binding<string>.From(() => Text, Write);
        }

        public CSharp.UI.Binding<string> Binding { get; }

        public string Text { get; set; }

        public List<string> SetterValues { get; } = [];

        public Func<string, string>? Normalize { get; set; }

        public bool Refuse { get; set; }

        public int SetterCalls => SetterValues.Count;

        private void Write(string value)
        {
            SetterValues.Add(value);
            if (Refuse)
            {
                return;
            }

            Text = Normalize?.Invoke(value) ?? value;
        }
    }

    private sealed class OwnerHost : IDisposable
    {
        public ScriptedBinding Store { get; }
        public ForwardingService Service { get; } = new();
        public UiRuntime Runtime { get; }
        public ViewNode Node { get; }
        public TextInputCompositionSession Session { get; }
        public NativeCompositionOwner Owner { get; }

        public OwnerHost(string initial, int maxLength = int.MaxValue)
        {
            Store = new ScriptedBinding(initial);
            Runtime = new UiRuntime(
                new TextField(Store.Binding) { MaxLength = maxLength },
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
                    TextureService = new RecordingTextureService(),
                    TextInputService = Service,
                });
            Runtime.SetViewport(new Size(400, 120), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Node = Runtime.Root;
            Runtime.SetFocus(Node);
            Runtime.BuildFrame();

            var client = State.Composing ?? throw new InvalidOperationException("会话未建立。");
            Session = new TextInputCompositionSession(
                client,
                c => ReferenceEquals(c, Runtime.TextInputSession.ActiveClient));
            Owner = new NativeCompositionOwner(Store_(), Session, client.Epoch);
        }

        private AcpTextStore Store_() => _store ??= CreateStore();

        private AcpTextStore? _store;

        private AcpTextStore CreateStore()
        {
            var store = new AcpTextStore();
            store.ResetAuthoritativeText(Store.Text);
            return store;
        }

        public AcpTextStore Acp => Store_();

        public TextCompositionClient Client
            => State.Composing ?? throw new InvalidOperationException("会话未建立。");

        public TextFieldState State => Assert.IsType<TextFieldState>(Node.InteractionState);

        public int Version => State.Editor.Version;

        public int HistoryCount => State.History.UndoCount;

        /// <summary>设置已提交选区（走真实 epoch+版本校验的生产接缝）。</summary>
        public void SelectRange(int start, int end)
        {
            var target = (ITextInputSelectionTarget)Client;
            Assert.True(target.TrySetCommittedSelection(Client.Session, Version, new TextSelection(start, end)));
            Runtime.ProcessPendingWork();
        }

        /// <summary>
        /// 在原生文档中写入文本（模拟 TSF 写会话的真实原生编辑；进入 store 镜像，不写 Binding）。
        /// </summary>
        /// <remarks>
        /// 走 store 的公开 internal 文档模型，与 TSF 在写锁内 <c>SetText</c> 的效果等价
        /// （更新原生 ACP 视图与 staging），<b>不</b>触碰 Binding。
        /// </remarks>
        public void NativeInsert(int acpStart, string value)
            => Acp.Document.TryReplace(acpStart, acpStart, value, AcpEditOrigin.Tsf, out _);

        /// <summary>
        /// 在<b>真实 ACP 写锁</b>内整体替换原生文档文本（与 TSF 写会话 <c>SetText</c> 等价）。
        /// </summary>
        /// <remarks>
        /// 走 <see cref="AcpTextStore.RequestLock"/> + <c>SetText</c>，不使用文档内部 API 直接改写，
        /// 也不触碰 Binding；用于构造“同一批里连续多个原生事务”的排队场景。
        /// </remarks>
        public void ReplaceNativeText(string text)
        {
            var sink = new SelectionWriteSink();
            Assert.Equal(0, Acp.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3));

            var value = Marshal.StringToHGlobalUni(text);
            try
            {
                sink.Write = () => Acp.SetText(0, 0, Acp.Document.Length, value, (uint)text.Length, out _);
                var hr = Acp.RequestLock(TsfFlags.TS_LF_READWRITE, out var sessionHr);
                Assert.True(hr >= 0 && sessionHr >= 0 && sink.WriteResult >= 0,
                    $"原生文本写入失败 hr=0x{hr:X8} session=0x{sessionHr:X8} write=0x{sink.WriteResult:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(value);
                Acp.UnadviseSink(sink);
            }
        }

        /// <summary>
        /// 在<b>真实 ACP 写锁</b>内设置原生选区（与输入法真实调用 <c>ITextStoreACP::SetSelection</c> 一致）。
        /// </summary>
        /// <remarks>
        /// 走 <see cref="AcpTextStore.RequestLock"/> + <c>SetSelection</c>，保留 <c>TS_AE_START</c> 方向，
        /// 不使用文档内部 API 直接改选区，避免绕过真实路径。
        /// </remarks>
        public void SetNativeSelection(TextSelection selection)
        {
            var sink = new SelectionWriteSink();
            Assert.Equal(0, Acp.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3));

            var native = new TsSelectionAcp
            {
                AcpStart = Math.Min(selection.Anchor, selection.Active),
                AcpEnd = Math.Max(selection.Anchor, selection.Active),
                Style = new TsSelectionStyle
                {
                    ActiveSelEnd = selection.Anchor == selection.Active
                        ? TsfFlags.TS_AE_NONE
                        : selection.Active < selection.Anchor
                            ? TsfFlags.TS_AE_START
                            : TsfFlags.TS_AE_END,
                },
            };

            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TsSelectionAcp>());
            try
            {
                Marshal.StructureToPtr(native, pointer, false);
                sink.Write = () => Acp.SetSelection(1, pointer);
                var hr = Acp.RequestLock(TsfFlags.TS_LF_READWRITE, out var sessionHr);
                Assert.True(hr >= 0 && sessionHr >= 0 && sink.WriteResult >= 0,
                    $"原生选区写入失败 hr=0x{hr:X8} session=0x{sessionHr:X8} write=0x{sink.WriteResult:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
                Acp.UnadviseSink(sink);
            }
        }

        public void Dispose()
        {
            Owner.Dispose();
            Acp.Dispose();
            Runtime.Dispose();
        }
    }

    private sealed class ForwardingService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public bool StartSession(ITextInputClient client)
        {
            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            if (ActiveClient is null)
            {
                return false;
            }

            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;
    }

    // ---------------------------------------------------------------- 表行 1：StartComposition 之前的初始插入

    [Fact]
    public void 基线选区在Begin前被真实刷新且用于替换区()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        // 真实权威选区必须被基线捕获（不是构造时的陈旧 0,0）。
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 5, "nihao", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        Assert.Equal(2, host.Owner.Baseline.SelectionRange.Start);
        Assert.Equal(2, host.Owner.Baseline.SelectionRange.End);

        // 中间插入：镜像保留前后缀，不丢 CD。
        Assert.Equal("ABnihaoCD", host.Session.Snapshot.DisplayText);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 表行1StartComposition之前的初始插入只驱动预编辑不写绑定()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(4, 4);

        // 事实：TSF 先写入 staging，然后才发 StartComposition（真实顺序）。
        host.Acp.Staging.Record(
            AcpStagedEditKind.Insert,
            new AcpTextChange(4, 4, 6, 1),
            "ni",
            correction: false);

        // 原生文档也真实插入（TSF 写会话的效果）。
        host.NativeInsert(4, "ni");

        host.Owner.Capture(
            NativeCompositionFactKind.Start,
            anchor: 4,
            length: 2,
            preedit: "ni",
            hasRange: true,
            nativeOperationKey: "start-1");


        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.Applied);
        Assert.False(drain.AnyCommitted, "Start 不得提交。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.HistoryCount);
        Assert.Equal("abcd", host.Store.Text);

        // 显示镜像 = 前缀 + 预编辑。
        Assert.Equal("abcdni", host.Session.Snapshot.DisplayText);
        Assert.True(host.Session.Snapshot.IsComposing);
    }

    // ---------------------------------------------------------------- 表行 2：更新（含同长度替换）

    [Fact]
    public void 表行2同长度替换更新不写绑定且镜像跟随()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(4, 4);

        host.Owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        // 同长度替换：ni → nihb 的原生文本变化。
        host.NativeInsert(6, "hb");
        host.Owner.Capture(NativeCompositionFactKind.Update, 4, 4, "nihb", true, nativeOperationKey: "u1");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.Applied);
        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("abcdnihb", host.Session.Snapshot.DisplayText);
        Assert.Equal("nihb", host.Session.Snapshot.Preedit);
    }

    // ---------------------------------------------------------------- 表行 3：范围收缩/部分确认 + 剩余预编辑

    [Fact]
    public void 表行3部分确认后剩余预编辑保持组合且只提交一次()
    {
        using var host = new OwnerHost(string.Empty);

        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 5, "nihao", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        Assert.Equal("nihao", host.Session.Snapshot.DisplayText);

        // 部分确认：原生文档替换为 "你好"（真实差量），End 终止后由完成编辑分类。
        host.NativeInsert(0, "你好");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 2, "你好", true, nativeOperationKey: "e1");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit1");
        var commitDrain = host.Owner.DrainAtSafePhase();

        Assert.True(commitDrain.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("你好", host.Store.Text);

        // 剩余预编辑：新组合在提交文本之后。
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ma", true, nativeOperationKey: "s2");
        var residual = host.Owner.DrainAtSafePhase();

        Assert.True(residual.Applied);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("你好ma", host.Session.Snapshot.DisplayText);
        Assert.True(host.Session.Snapshot.IsComposing);
    }

    // ---------------------------------------------------------------- 表行 4：结果之后终止

    [Fact]
    public void 表行4结果之后End不二次写入()
    {
        using var host = new OwnerHost(string.Empty);

        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        host.NativeInsert(0, "你");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 2, "你", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-e");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);

        // 结果之后的终止（无活动组合、无差量）：不得二次写入。
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 0, string.Empty, true, nativeOperationKey: "e2");
        host.Owner.CaptureCompletedEdit(textChanged: false, nativeOperationKey: "edit-e2");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("你", host.Store.Text);
    }

    // ---------------------------------------------------------------- 表行 5：取消（Escape/删除/恢复）

    [Fact]
    public void 表行5取消恢复组合前文本且不写绑定()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(2, 4); // 选中 "cd"

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "XY", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        Assert.Equal("abXY", host.Session.Snapshot.DisplayText);

        // 取消：原生文本恢复到基线（删除预编辑），由完成编辑判定为取消。
        host.Acp.Document.TryReplace(2, 7, string.Empty, AcpEditOrigin.Tsf, out _);
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "XY", true, nativeOperationKey: "e-cancel");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-cancel");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.Applied);
        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("abcd", host.Store.Text);
        // 选区恢复到组合前 [2,4)。
        Assert.Equal(2, host.State.SelectionStart);
        Assert.Equal(4, host.State.SelectionEnd);
    }

    // ---------------------------------------------------------------- 表行 6：直接非组合原生写入

    [Fact]
    public void 表行6直接非组合原生写入经权威事务提交且不丢输入()
    {
        using var host = new OwnerHost("abcd");

        // 非组合原生写入：staging 有记录，但没有组合事实。
        host.Acp.Staging.Record(
            AcpStagedEditKind.Insert,
            new AcpTextChange(0, 0, 1, 1),
            "X",
            correction: false);

        // 真实原生文档中间插入 X：abcd → abXcd（TSF 写会话的效果）。
        host.NativeInsert(2, "X");

        // 编辑完成事实（已验证的编辑完成边界）。
        host.Owner.Capture(
            NativeCompositionFactKind.EditCompleted,
            anchor: 0,
            length: 0,
            preedit: null,
            hasRange: false,
            textChanged: true,
            nativeOperationKey: "edit-1");

        var drain = host.Owner.DrainAtSafePhase();

        // R4：无活动组合的完整原生写入也经权威编辑事务应用，不得永久 resync/丢弃输入。
        Assert.True(drain.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("abXcd", host.Store.Text);
        Assert.False(host.Owner.NeedsResync);

        // 暂存批次仍可被后续批次消费（未丢失）。
        var staged = host.Acp.Staging.DrainForGeneration(out _);
        Assert.Single(staged);
        Assert.Equal("X", staged[0].Inserted);
    }

    // ---------------------------------------------------------------- 表行 7：选中替换

    [Fact]
    public void 表行7选中范围替换提交只写一次且保留前后缀()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(1, 3); // 选中 "bc"

        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 2, "XY", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        Assert.Equal("aXYd", host.Session.Snapshot.DisplayText);

        host.Acp.Document.TryReplace(1, 3, "ZZ", AcpEditOrigin.Tsf, out _);
        host.Owner.Capture(NativeCompositionFactKind.End, 1, 2, "ZZ", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit7");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("aZZd", host.Store.Text);
        Assert.Equal(1, host.HistoryCount);
    }

    // ---------------------------------------------------------------- 表行 8：外部权威修正

    [Fact]
    public void 表行8外部权威修正后旧事实被拒绝且不写绑定()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(4, 4);

        host.Owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true, nativeOperationKey: "s");

        // 外部权威修正：应用侧重置权威文本（推进代数）。
        host.Acp.ResetAuthoritativeText("external");
        host.Store.Text = "external";
        host.Runtime.ProcessPendingWork();

        // 基线已与权威文档不一致 → 显式拒绝，不写入、不夹取。
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("external", host.Store.Text);
    }

    // ---------------------------------------------------------------- UTF-16 暂存

    [Fact]
    public void UTF16代理对预编辑按码元处理不拆字()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        // 预编辑含代理对（😀 = 2 码元）。
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "😀", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        Assert.Equal("ab😀", host.Session.Snapshot.DisplayText);
        Assert.Equal(2, host.Session.Snapshot.Preedit.Length);
        Assert.Equal(0, host.Store.SetterCalls);

        // 提交完整代理对。
        host.NativeInsert(2, "😀");
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "😀", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-emoji");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal("ab😀", host.Store.Text);
    }

    // ---------------------------------------------------------------- 反向选区

    [Fact]
    public void 反向选区提交保留方向且ACP返回规范化范围()
    {
        using var host = new OwnerHost("abcd");
        host.SelectRange(3, 1); // 反向：anchor 3、active 1

        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 2, "XY", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        Assert.Equal("aXYd", host.Session.Snapshot.DisplayText);

        host.Acp.Document.TryReplace(1, 3, "ZZ", AcpEditOrigin.Tsf, out _);
        host.Owner.Capture(NativeCompositionFactKind.End, 1, 2, "ZZ", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-rev");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal("aZZd", host.Store.Text);
    }

    // ---------------------------------------------------------------- 迟到 update（End 之后 / 新 Begin）

    [Fact]
    public void 迟到update在End之后被拒绝且不写绑定()
    {
        using var host = new OwnerHost(string.Empty);

        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        host.NativeInsert(0, "你");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 2, "你", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-late");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        var setterCalls = host.Store.SetterCalls;

        // 迟到的 Update（组合已结束）：票据退休 → 拒绝。
        host.Owner.Capture(NativeCompositionFactKind.Update, 0, 3, "nih", true, nativeOperationKey: "late");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(setterCalls, host.Store.SetterCalls);
        Assert.Equal("你", host.Store.Text);
    }

    // ---------------------------------------------------------------- 重复原生事实 vs 两次合法相同结果

    [Fact]
    public void 重复原生事实复用operationId被拒绝而两次合法相同结果都提交()
    {
        using var host = new OwnerHost(string.Empty);

        // 第一次组合：提交 "X"。
        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 1, "x", true, nativeOperationKey: "s1");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(0, "X");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 1, "X", true, nativeOperationKey: "e1");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit1");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);

        // 同一原生操作重复投递（同一 key）：复用 operationId → 被 P2a 水位拒绝。
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 1, "X", true, nativeOperationKey: "e1");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit1");
        var replay = host.Owner.DrainAtSafePhase();
        Assert.False(replay.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);

        // 第二次合法的相同内容（不同 key）：真实提交。
        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 1, "x", true, nativeOperationKey: "s2");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(1, "X");
        host.Owner.Capture(NativeCompositionFactKind.End, 1, 1, "X", true, nativeOperationKey: "e2");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit2");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(2, host.Store.SetterCalls);
        Assert.Equal("XX", host.Store.Text);
    }

    // ---------------------------------------------------------------- 失败路径

    [Fact]
    public void 无有效原生范围显式拒绝不伪造caret()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 0, "ni", hasRange: false, nativeOperationKey: "s");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.RejectedCount > 0);
        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.False(host.Session.Snapshot.IsComposing);
    }

    [Fact]
    public void setter拒绝不谎报提交()
    {
        using var host = new OwnerHost("ab");

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        host.NativeInsert(2, "你");
        host.Store.Refuse = true;
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "你", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-refuse");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("ab", host.Store.Text);
    }

    [Fact]
    public void MaxLength截断后快照等于真实回读()
    {
        using var host = new OwnerHost("ab", maxLength: 4);
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        host.NativeInsert(2, "你好");
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "你好", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-max");
        host.Owner.DrainAtSafePhase();

        // 真实容量规则：只接受能放下的部分。
        Assert.Equal("ab你好", host.Store.Text);
        Assert.Equal(host.Store.Text, host.Session.Snapshot.Committed.Text);
    }

    [Fact]
    public void 净化后快照以真实回读为准()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();

        host.Store.Normalize = v => v.Replace("\n", string.Empty, StringComparison.Ordinal);
        host.NativeInsert(2, "X\nY");
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "X\nY", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-norm");
        host.Owner.DrainAtSafePhase();

        Assert.Equal("abXY", host.Store.Text);
        Assert.Equal("abXY", host.Session.Snapshot.Committed.Text);
    }

    // ---------------------------------------------------------------- 安全阶段/线程/重入

    [Fact]
    public void 持锁时安全阶段消费被延后且不写文档()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");

        // 模拟持锁（通过真实 RequestLock 的授予回调）。
        var appliedInsideLock = true;
        var sink = new NoOpSink(() =>
        {
            var drain = host.Owner.DrainAtSafePhase();
            appliedInsideLock = drain.Applied;
        });
        Assert.Equal(0, host.Acp.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        Assert.Equal(0, host.Acp.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        Assert.False(appliedInsideLock, "持锁期间不得应用组合事实。");
        Assert.Equal(0, host.Store.SetterCalls);

        // 安全阶段：正常应用。
        var drain = host.Owner.DrainAtSafePhase();
        Assert.True(drain.Applied);
        Assert.Equal("abni", host.Session.Snapshot.DisplayText);
    }

    [Fact]
    public void 跨线程安全阶段消费被拒绝且不写文档()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");

        NativeCompositionDrainResult? result = null;
        var thread = new Thread(() => result = host.Owner.DrainAtSafePhase());
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(result);
        Assert.False(result!.Value.Applied);
        Assert.Equal(0, host.Store.SetterCalls);
        // pending 保留给所属线程。
        Assert.Equal(1, host.Owner.PendingCount);
    }

    // ---------------------------------------------------------------- 代数变化 / 溢出 / 释放

    [Fact]
    public void 外部权威修正后基线失效显式拒绝不写绑定()
    {
        using var host = new OwnerHost("ab");

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");

        // 应用侧权威修正（外部失效）：重置权威文本。
        host.Acp.ResetAuthoritativeText("changed");
        host.Store.Text = "changed";
        host.Runtime.ProcessPendingWork();

        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("changed", host.Store.Text);
    }

    [Fact]
    public void 溢出后标记resync且不把不完整历史当完整()
    {
        using var host = new OwnerHost("ab");

        // 超过队列上限的事实：溢出必须显式标记，不静默丢。
        for (var i = 0; i < NativeCompositionOwner.MaxPendingFacts + 5; i++)
        {
            host.Owner.Capture(
                NativeCompositionFactKind.Update,
                anchor: 0,
                length: 1,
                preedit: "x",
                hasRange: true,
                nativeOperationKey: $"u{i}");
        }

        Assert.True(host.Owner.NeedsResync, "溢出必须显式标记需要重新同步。");
        Assert.True(host.Owner.DroppedFactCount > 0);
        Assert.Equal(NativeCompositionOwner.MaxPendingFacts, host.Owner.PendingCount);
    }

    [Fact]
    public void 释放后不再应用事实()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.Dispose();

        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.Applied);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    // ---------------------------------------------------------------- 纯预编辑更新

    [Fact]
    public void 纯预编辑更新加完成编辑不得提交也不退休票据()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(4, 4);

        host.Owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        Assert.True(host.Owner.SynchronizeToNative());

        var token = host.Session.Token;
        Assert.NotNull(token);

        // 用户继续打字：预编辑 ni → nih（原生文档随之变化）。
        host.NativeInsert(6, "h");
        host.Owner.Capture(NativeCompositionFactKind.Update, 4, 3, "nih", true, nativeOperationKey: "u");

        // 完成编辑到达，但组合**仍在进行**（没有 End）→ 纯预编辑更新。
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "ed");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted, "纯预编辑更新不得提交。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABCD", host.Store.Text);

        // 票据不得被退休（组合仍在进行）。
        Assert.Same(token, host.Session.Token);
    }

    // ---------------------------------------------------------------- R3 有界性

    [Fact]
    public void 溢出后停止不可信提交而不是仅设标志()
    {
        using var host = new OwnerHost(string.Empty);

        // 制造溢出（超过队列上限）。
        for (var i = 0; i < NativeCompositionOwner.MaxPendingFacts + 5; i++)
        {
            host.Owner.Capture(
                NativeCompositionFactKind.Update, 0, 1, "x", true, nativeOperationKey: $"u{i}");
        }

        Assert.True(host.Owner.NeedsResync);

        // 关键：溢出后即使有真实提交机会，也不得写 Binding。
        host.NativeInsert(0, "X");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "edit-after-overflow");
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 溢出事实的身份条目被清理不留永久残留()
    {
        using var host = new OwnerHost("ab");

        for (var i = 0; i < NativeCompositionOwner.MaxPendingFacts + 10; i++)
        {
            host.Owner.Capture(
                NativeCompositionFactKind.Update, 0, 1, "x", true, nativeOperationKey: $"k{i}");
        }

        // 队列有界。
        Assert.Equal(NativeCompositionOwner.MaxPendingFacts, host.Owner.PendingCount);

        // 操作身份表不得无限增长（被丢弃事实的身份已移除）。
        Assert.True(
            host.Owner.OperationIdentityCount <= NativeCompositionOwner.MaxPendingFacts + 10,
            $"身份表无界增长：{host.Owner.OperationIdentityCount}");
    }

    [Fact]
    public void 操作身份表达到上限后退休而不是永久dead_end()
    {
        using var host = new OwnerHost(string.Empty);

        // 填满并超过身份表上限（每次消费掉，不触发队列溢出）。
        for (var i = 0; i < NativeCompositionOwner.MaxOperationIdentities + 2; i++)
        {
            host.Owner.Capture(NativeCompositionFactKind.Update, 0, 0, null, false, nativeOperationKey: $"id{i}");
            host.Owner.DrainAtSafePhase();
        }

        // R3：普通连续事件不得永久 dead-end；身份表必须有界。
        Assert.False(host.Owner.NeedsResync, "身份表满应退休已完成身份，而不是永久 resync。");
        Assert.True(host.Owner.OperationIdentityCount <= NativeCompositionOwner.MaxOperationIdentities);
    }

    [Fact]
    public void 已应用动作诊断历史有界()
    {
        using var host = new OwnerHost("ab");
        host.SelectRange(2, 2);

        for (var i = 0; i < NativeCompositionOwner.MaxAppliedActionHistory + 20; i++)
        {
            host.Owner.Capture(
                NativeCompositionFactKind.Update, 2, 1, "x", true, nativeOperationKey: $"a{i}");
            host.Owner.DrainAtSafePhase();
        }

        Assert.True(
            host.Owner.AppliedActions.Count <= NativeCompositionOwner.MaxAppliedActionHistory,
            $"诊断历史无界：{host.Owner.AppliedActions.Count}");
    }

    // ---------------------------------------------------------------- 连续两轮组合

    [Fact]
    public void 连续两轮组合各自唯一提交且第二轮基于真实回读()
    {
        using var host = new OwnerHost(string.Empty);

        // 第一轮。
        host.SelectRange(0, 0);
        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 1, "a", true, nativeOperationKey: "s1");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(0, "甲");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 1, "甲", true, nativeOperationKey: "e1");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "ed1");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("甲", host.Store.Text);

        // 第二轮：基线必须反映第一轮的真实回读。
        host.SelectRange(1, 1);
        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 1, "b", true, nativeOperationKey: "s2");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(1, "乙");
        host.Owner.Capture(NativeCompositionFactKind.End, 1, 1, "乙", true, nativeOperationKey: "e2");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "ed2");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);

        Assert.Equal(2, host.Store.SetterCalls);
        Assert.Equal("甲乙", host.Store.Text);
    }

    // ---------------------------------------------------------------- R3 连续正常输入

    [Fact]
    public void 连续正常事务超过身份上限仍零重复零丢失且有界()
    {
        using var host = new OwnerHost(string.Empty);

        // 远超身份表上限的连续合法事务（模拟普通连续输入）。
        // 使用**生产身份路径**：来源（sink）分配的单调 ticket，O(1) 内存。
        var transactions = NativeCompositionOwner.MaxOperationIdentities + 40;
        var expected = string.Empty;
        long ticket = 0;
        for (var i = 0; i < transactions; i++)
        {
            host.SelectRange(expected.Length, expected.Length);
            host.Owner.Capture(
                NativeCompositionFactKind.Start,
                expected.Length,
                1,
                "x",
                true,
                sourceTicket: ++ticket);
            var startDrain = host.Owner.DrainAtSafePhase();
            

            host.NativeInsert(expected.Length, "甲");
            expected += "甲";
            host.Owner.Capture(
                NativeCompositionFactKind.End,
                expected.Length - 1,
                1,
                "甲",
                true,
                sourceTicket: ++ticket);
            host.Owner.DrainAtSafePhase();
            host.Owner.CaptureCompletedEdit(textChanged: true, sourceTicket: ++ticket);
            host.Owner.DrainAtSafePhase();

            // 普通连续输入不得永久 dead-end。
            Assert.False(host.Owner.NeedsResync, $"第 {i} 个事务后进入了 resync（普通输入被 dead-end）。");
        }

        // 零丢失：每个事务各写一次。
        Assert.Equal(transactions, host.Store.SetterCalls);
        Assert.Equal(expected, host.Store.Text);

        // 内存有界。
        Assert.True(
            host.Owner.OperationIdentityCount <= NativeCompositionOwner.MaxOperationIdentities,
            $"身份表无界：{host.Owner.OperationIdentityCount}");
    }

    [Fact]
    public void 已完成操作的重复投递仍被拒绝不二次提交()
    {
        using var host = new OwnerHost(string.Empty);

        host.SelectRange(0, 0);
        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 1, "a", true, nativeOperationKey: "s");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(0, "甲");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 1, "甲", true, nativeOperationKey: "e");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "ed");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);

        // 同一事务（同一键）重复投递：不得二次提交。
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "ed");
        var replay = host.Owner.DrainAtSafePhase();

        Assert.False(replay.AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("甲", host.Store.Text);
    }

    // ---------------------------------------------------------------- R7 生命周期

    /// <summary>
    /// 无 context 时重复注册都不建立连接，且返回一致的 HRESULT。
    /// </summary>
    /// <remarks>
    /// 真实“已持有 cookie 时重复 advise 复用同一 cookie”由原生连接探针断言
    /// （<c>TsfCompositionOwnerSmokeTests.真实隐藏context验证重复advise与换context重连的成对回收</c>）。
    /// </remarks>
    [Fact]
    public void 无context时重复注册都不建立连接()
    {
        using var host = new OwnerHost("ab");
        using var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);

        // 无真实 context 时 QueryForSource 会失败；这里只验证**守卫**语义：
        // 首次未连接 → 失败；再次调用仍失败且不产生任何连接状态。
        var first = sink.AdviseEditSink(NoContext());
        var second = sink.AdviseEditSink(NoContext());

        Assert.False(sink.EditSinkAdvised);
        Assert.Equal(0u, sink.EditSinkCookie);
        Assert.Equal(first, second);
    }

    [Fact]
    public void 释放后注册被显式拒绝且不新建连接()
    {
        using var host = new OwnerHost("ab");
        var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);
        sink.Dispose();

        var hr = sink.AdviseEditSink(NoContext());

        Assert.Equal(TsfHResult.CO_E_OBJNOTCONNECTED, hr);
        Assert.False(sink.EditSinkAdvised);
    }

    [Fact]
    public void 跨线程注册与撤销被拒绝且保留owner清理机会()
    {
        using var host = new OwnerHost("ab");
        using var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);

        int? adviseHr = null;
        int? unadviseHr = null;
        var thread = new Thread(() =>
        {
            adviseHr = sink.AdviseEditSink(NoContext());
            unadviseHr = sink.UnadviseEditSink();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(TsfHResult.RPC_E_WRONG_THREAD, adviseHr);
        Assert.Equal(TsfHResult.RPC_E_WRONG_THREAD, unadviseHr);

        // owner 线程仍能正常清理。
        Assert.Equal(TsfHResult.S_OK, sink.UnadviseEditSink());
    }

    [Fact]
    public void 释放后回调不触碰借用视图也不入队()
    {
        using var host = new OwnerHost("ab");
        var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);
        sink.Dispose();

        var view = new CountingCompositionView();
        var hr = sink.OnStartComposition(view, out var accepted);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(0, accepted);
        Assert.Equal(0, view.RangeReads);
        Assert.Equal(0, host.Owner.PendingCount);
    }

    /// <summary>无 context 的替身（null；AdviseEditSink 会先做 disposed/线程守卫再触碰）。</summary>
    private static ITfContext NoContext() => null!;

    /// <summary>计数用组合视图替身（不触碰真实 COM）。</summary>
    private sealed class CountingCompositionView : ITfCompositionView
    {
        public int RangeReads { get; private set; }

        public int GetOwnerClsid(out Guid pclsid)
        {
            pclsid = Guid.Empty;
            return 0;
        }

        public int GetRange(out IntPtr ppRange)
        {
            RangeReads++;
            ppRange = IntPtr.Zero;
            return 0;
        }
    }

    // ---------------------------------------------------------------- R3 多次轮转后重放最早操作

    [Fact]
    public void 身份表多次轮转后重放最早已完成操作不二次提交()
    {
        using var host = new OwnerHost(string.Empty);

        // 第一个事务：记录其键，稍后重放。
        host.SelectRange(0, 0);
        long ticket = 0;
        var earliestStartTicket = ++ticket;
        host.Owner.Capture(NativeCompositionFactKind.Start, 0, 1, "a", true, sourceTicket: earliestStartTicket);
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(0, "甲");
        host.Owner.Capture(NativeCompositionFactKind.End, 0, 1, "甲", true, sourceTicket: ++ticket);
        host.Owner.DrainAtSafePhase();
        var earliestEditTicket = ++ticket;
        host.Owner.CaptureCompletedEdit(textChanged: true, sourceTicket: earliestEditTicket);
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(1, host.Store.SetterCalls);

        // 制造多次身份表轮转（远超上限的合法事务）。
        var rotations = (NativeCompositionOwner.MaxOperationIdentities * 3) + 20;
        var expected = "甲";
        for (var i = 0; i < rotations; i++)
        {
            host.SelectRange(expected.Length, expected.Length);
            host.Owner.Capture(
                NativeCompositionFactKind.Start, expected.Length, 1, "x", true,
                nativeOperationKey: $"rot-s{i}");
            host.Owner.DrainAtSafePhase();
            host.NativeInsert(expected.Length, "乙");
            expected += "乙";
            host.Owner.Capture(
                NativeCompositionFactKind.End, expected.Length - 1, 1, "乙", true,
                nativeOperationKey: $"rot-e{i}");
            host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: $"rot-ed{i}");
            host.Owner.DrainAtSafePhase();
        }

        var writesBeforeReplay = host.Store.SetterCalls;
        Assert.Equal(rotations + 1, writesBeforeReplay);
        Assert.False(host.Owner.NeedsResync, "连续合法输入不得进入 resync。");

        // 重放**最早**的已完成操作：必须被水位拒绝，不二次提交。
        host.Owner.CaptureCompletedEdit(textChanged: true, sourceTicket: earliestEditTicket);
        var replay = host.Owner.DrainAtSafePhase();

        Assert.False(replay.AnyCommitted, "最早已完成操作被重放并二次提交。");
        Assert.Equal(writesBeforeReplay, host.Store.SetterCalls);
        Assert.Equal(expected, host.Store.Text);

        // 重放后连续合法输入仍然工作。
        host.SelectRange(expected.Length, expected.Length);
        host.Owner.Capture(NativeCompositionFactKind.Start, expected.Length, 1, "x", true,
            nativeOperationKey: "post-replay-start");
        host.Owner.DrainAtSafePhase();
        host.NativeInsert(expected.Length, "丙");
        host.Owner.Capture(NativeCompositionFactKind.End, expected.Length, 1, "丙", true,
            nativeOperationKey: "post-replay-end");
        host.Owner.CaptureCompletedEdit(textChanged: true, nativeOperationKey: "post-replay-edit");
        Assert.True(host.Owner.DrainAtSafePhase().AnyCommitted);
        Assert.Equal(writesBeforeReplay + 1, host.Store.SetterCalls);
    }

    // ---------------------------------------------------------------- null pRangeNew 合法路径

    [Fact]
    public void null范围更新从组合视图回退读取当前范围而不是判无效()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);
        using var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);

        // pRangeNew 为 null 是合法形态：产品应尝试从组合视图读取，而不是直接判无效。
        // 该替身无法提供真实原生指针，因此两个来源都不可用 → 必须**显式失败**（不伪造范围），
        // 但回调本身仍成功返回且事实入队（不崩溃、不抛异常）。
        var view = new FixedRangeCompositionView(anchor: 2, length: 0);
        var hr = sink.OnUpdateComposition(view, IntPtr.Zero);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(1, sink.UpdateCallbacks);
        Assert.Equal(1, sink.RangeFailureCount);
        Assert.Equal(1, host.Owner.PendingCount);
    }

    // ---------------------------------------------------------------- R8 未变化的终止结果

    [Fact]
    public void 预编辑未变化而终止组合时确认该预编辑且只写一次()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        // 原生写成 ABniCD，Start + 完成编辑：纯预编辑，零 Binding 写入。
        host.NativeInsert(2, "ni");
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, sourceTicket: 1);
        host.Owner.CaptureCompletedEdit(sourceTicket: 2);
        var preedit = host.Owner.DrainAtSafePhase();

        Assert.True(preedit.Applied);
        Assert.False(preedit.AnyCommitted, "预编辑更新不得提交。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABniCD", host.Session.Snapshot.DisplayText);

        // R8：下一真实事务只结束组合，原生文字保持 ABniCD（delta 为空），再完成编辑。
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 2, "ni", true, sourceTicket: 3);
        host.Owner.CaptureCompletedEdit(sourceTicket: 4);
        var committed = host.Owner.DrainAtSafePhase();

        // 合法结果 ni 必须成为 ABniCD，且只提交一次、组合结束。
        Assert.True(committed.AnyCommitted, "未变化的终止结果被当成取消而丢失。");
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("ABniCD", host.Store.Text);
        Assert.False(host.Session.Snapshot.IsComposing);
    }

    [Fact]
    public void 终止后原生恢复权威文本仍是取消且零写入并恢复选区()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(1, 3);

        // 选中替换预编辑，先完成一次纯预编辑事务。
        host.NativeInsert(1, "nihao");
        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 5, "nihao", true, sourceTicket: 1);
        host.Owner.CaptureCompletedEdit(sourceTicket: 2);
        host.Owner.DrainAtSafePhase();
        Assert.Equal(0, host.Store.SetterCalls);

        // 真实取消：原生删除预编辑，回到权威已提交文本。
        host.Acp.Document.TryReplace(1, 6, string.Empty, AcpEditOrigin.Tsf, out _);
        host.Owner.Capture(NativeCompositionFactKind.End, 1, 5, "nihao", true, sourceTicket: 3);
        host.Owner.CaptureCompletedEdit(sourceTicket: 4);
        var drain = host.Owner.DrainAtSafePhase();

        Assert.False(drain.AnyCommitted, "取消被当成确认。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABCD", host.Store.Text);
        Assert.Equal(1, host.State.SelectionStart);
        Assert.Equal(3, host.State.SelectionEnd);
    }

    [Fact]
    public void 同一组合两次预编辑更新各自应用且保持同一组合零写入()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        host.NativeInsert(2, "ni");
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, sourceTicket: 1);
        host.Owner.DrainAtSafePhase();
        var token = host.Session.Token;

        // 两次连续更新（真实输入法在同一组合内多次刷新预编辑），快照不同、各自独立身份。
        // 关键缺陷：若按“组合开始计数”生成键（如 update-1），第二次合法更新会被当重放丢弃。
        host.NativeInsert(4, "h");
        host.Owner.Capture(NativeCompositionFactKind.Update, 2, 3, "nih", true, sourceTicket: 2);
        host.Owner.DrainAtSafePhase();
        Assert.Equal("ABnihCD", host.Session.Snapshot.DisplayText);

        host.NativeInsert(5, "a");
        host.Owner.Capture(NativeCompositionFactKind.Update, 2, 4, "niha", true, sourceTicket: 3);
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.Applied, "第二次合法更新被丢弃。");
        Assert.Equal("ABnihaCD", host.Session.Snapshot.DisplayText);
        Assert.True(host.Session.Snapshot.IsComposing, "更新不得结束组合。");
        Assert.True(ReferenceEquals(token, host.Session.Token), "合法后续更新换掉了组合票据。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.False(host.Owner.NeedsResync);
    }

    [Fact]
    public void sink每次真实回调分配独立ticket且显式取消与原生回调同域()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);
        using var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);

        // 每次真实回调各取一个 ticket（不按内容重建），因此同一组合内多次更新不会撞号。
        var before = host.Owner.AllocatedTicketCount;
        sink.OnUpdateComposition(new FixedRangeCompositionView(2, 2), IntPtr.Zero);
        sink.OnUpdateComposition(new FixedRangeCompositionView(2, 3), IntPtr.Zero);

        Assert.Equal(2, sink.UpdateCallbacks);
        Assert.Equal(before + 2, host.Owner.AllocatedTicketCount);

        // 原生回调与宿主显式取消共享同一个 ticket 域：不会出现两个计数器都从 1 开始的撞号。
        var nativeTicket = host.Owner.AllocateOperationTicket();
        var cancelTicket = host.Owner.AllocateOperationTicket();
        Assert.True(cancelTicket > nativeTicket, "ticket 域不单调，取消可能与原生事件撞号。");
    }

    /// <summary>
    /// 空 context 注册被拒绝且不留下任何连接状态（纯托管守卫；<b>不</b>证明换 context 重连）。
    /// </summary>
    /// <remarks>
    /// 真实 contextA→contextB 的 advise/重复 advise/unadvise 由原生连接探针覆盖
    /// （<c>TsfCompositionOwnerSmokeTests.真实隐藏context验证重复advise与换context重连的成对回收</c>），
    /// 那里断言实际 cookie、实际 HRESULT 与 source 成对回收。
    /// </remarks>
    [Fact]
    public void 空context注册被拒绝且不留下连接状态()
    {
        using var host = new OwnerHost("ab");
        using var sink = new NativeCompositionOwnerSink(host.Owner, host.Acp);

        // 无真实 context 时无法建立连接：注册/撤销都不得留下连接状态。
        Assert.Equal(TsfHResult.E_INVALIDARG, sink.AdviseEditSink(NoContext()));
        Assert.False(sink.EditSinkAdvised);
        Assert.False(sink.HoldsSource);
        Assert.Equal(TsfHResult.S_OK, sink.UnadviseEditSink());

        // 撤销后可以再次尝试（状态已完整摘除，不会与旧 cookie/source 混用）。
        Assert.Equal(TsfHResult.E_INVALIDARG, sink.AdviseEditSink(NoContext()));
        Assert.False(sink.EditSinkAdvised);
        Assert.False(sink.HoldsSource);
    }

    // ---------------------------------------------------------------- R9 原生选区集成

    [Fact]
    public void 完成事务的原生caret只移动选区且零绑定写入()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(4, 4);

        // 真实 ACP 写锁内设置原生选区（caret 4 → 1），随后完成编辑报告选区变化、文字未变。
        host.SetNativeSelection(new TextSelection(1, 1));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false);
        host.Owner.DrainAtSafePhase();

        var selection = host.Session.Snapshot.Committed.Selection;
        Assert.Equal(1, selection.Anchor);
        Assert.Equal(1, selection.Active);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABCD", host.Store.Text);
    }

    [Fact]
    public void 完成事务的反向原生选区保留方向()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(4, 4);

        // 反向选中 3→1：规范化范围 [1,3) 无法区分方向，必须保留 anchor/active。
        host.SetNativeSelection(new TextSelection(3, 1));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false);
        host.Owner.DrainAtSafePhase();

        var selection = host.Session.Snapshot.Committed.Selection;
        Assert.Equal(3, selection.Anchor);
        Assert.Equal(1, selection.Active);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 预编辑内折叠caret更新组合caret且零绑定写入()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        host.NativeInsert(2, "nihao");
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 5, "nihao", true, sourceTicket: 1);
        host.Owner.DrainAtSafePhase();
        var token = host.Session.Token;
        Assert.Equal(5, host.Session.Snapshot.PreeditCaret);

        // 原生 caret=3 落在预编辑范围 [2,7) 内 → 显示预编辑 caret 应为 1（不是末尾 5）。
        host.SetNativeSelection(new TextSelection(3, 3));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 2);
        host.Owner.DrainAtSafePhase();

        Assert.Equal(1, host.Session.Snapshot.PreeditCaret);
        Assert.True(host.Session.Snapshot.IsComposing, "caret 更新不得结束组合。");
        Assert.True(ReferenceEquals(token, host.Session.Token), "caret 更新换掉了组合票据。");
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABCD", host.Store.Text);
    }

    [Fact]
    public void 同一组合两次预编辑更新带不同caret各自应用且零绑定写入()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        host.NativeInsert(2, "ni");
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 2, "ni", true, sourceTicket: 1);
        host.Owner.DrainAtSafePhase();
        var token = host.Session.Token;

        // 第一次 null-new-range 更新 + caret 落到预编辑内偏移 1。
        host.NativeInsert(4, "h");
        host.SetNativeSelection(new TextSelection(3, 3));
        host.Owner.Capture(NativeCompositionFactKind.Update, 2, 3, "nih", true, sourceTicket: 2);
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 3);
        host.Owner.DrainAtSafePhase();
        Assert.Equal("ABnihCD", host.Session.Snapshot.DisplayText);
        Assert.Equal(1, host.Session.Snapshot.PreeditCaret);

        // 第二次更新：预编辑再次增长，caret 移到偏移 3。
        host.NativeInsert(5, "a");
        host.SetNativeSelection(new TextSelection(5, 5));
        host.Owner.Capture(NativeCompositionFactKind.Update, 2, 4, "niha", true, sourceTicket: 4);
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 5);
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.Applied, "第二次合法 caret 更新被丢弃。");
        Assert.Equal("ABnihaCD", host.Session.Snapshot.DisplayText);
        Assert.Equal(3, host.Session.Snapshot.PreeditCaret);
        Assert.True(host.Session.Snapshot.IsComposing);
        Assert.True(ReferenceEquals(token, host.Session.Token));
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.False(host.Owner.NeedsResync);
    }

    [Fact]
    public void 排队选区之后的结果各自应用且顺序不被打乱()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(4, 4);

        // 事务 1：只改选区（caret → 1），不写文字。
        host.SetNativeSelection(new TextSelection(1, 1));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 1);

        // 事务 2：真实文字写入 ABXCD，随后选区落到 2。
        host.NativeInsert(2, "X");
        host.SetNativeSelection(new TextSelection(2, 2));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: true, sourceTicket: 2);

        // 两个事务都在 drain 之前排队：必须按顺序各自应用，不能只保留最后一个选区。
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.AnyCommitted, "排队的文字结果丢失。");
        Assert.Equal("ABXCD", host.Store.Text);
        var selection = host.Session.Snapshot.Committed.Selection;
        Assert.Equal(2, selection.Anchor);
        Assert.Equal(2, selection.Active);
        Assert.Equal(1, host.Store.SetterCalls);
    }

    [Fact]
    public void 跨预编辑区域的非折叠原生选区被显式拒绝而不假装成功()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(2, 2);

        host.NativeInsert(2, "nihao");
        host.Owner.Capture(NativeCompositionFactKind.Start, 2, 5, "nihao", true, sourceTicket: 1);
        host.Owner.DrainAtSafePhase();
        var caretBefore = host.Session.Snapshot.PreeditCaret;

        // 选区从已提交文本跨进预编辑（anchor=1, active=4）：本批不支持，必须显式拒绝。
        host.SetNativeSelection(new TextSelection(1, 4));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 2);
        var drain = host.Owner.DrainAtSafePhase();

        Assert.True(drain.RejectedCount > 0, "跨区域选区未被显式拒绝。");
        Assert.Equal(caretBefore, host.Session.Snapshot.PreeditCaret);
        Assert.True(host.Session.Snapshot.IsComposing);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ABCD", host.Store.Text);
    }

    /// <summary>
    /// 排队：部分确认＋同一完成事实里的原生 caret，随后才来的最终确认不得被吞掉。
    /// </summary>
    /// <remarks>
    /// <b>缺陷复现（R9 排队）：</b>若在 Drain 期间为第二个动作（选区）新取一个<b>高</b>来源编号，
    /// 并把它当 P2a 操作编号传进仍然有效的票据，票据水位就会超越那些已在捕获阶段拿到<b>较低</b>编号的
    /// 后续事实，使排队的最终结果被当作重放拒绝（writes=1、仍在组合、rejected=1）。
    /// 修复后：来源 ticket 只管重放/退休，P2a 动作编号按<b>应用顺序</b>单独分配。
    /// </remarks>
    [Fact]
    public void 排队的部分确认加caret不吞掉后续最终确认()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(1, 3);

        // 进入预编辑并安全 Drain（初始预编辑不写绑定）。
        host.NativeInsert(1, "nihao");
        host.Owner.Capture(NativeCompositionFactKind.Start, 1, 5, "nihao", true, sourceTicket: 1);
        host.Owner.CaptureCompletedEdit(sourceTicket: 2);
        host.Owner.DrainAtSafePhase();
        Assert.Equal(0, host.Store.SetterCalls);

        // 部分结果 A你haoD + 原生 caret3：同一个完成事实同时带文本与选区动作。
        host.ReplaceNativeText("A你haoD");
        host.SetNativeSelection(new TextSelection(3, 3));
        host.Owner.Capture(NativeCompositionFactKind.Update, 2, 3, "hao", true, sourceTicket: 3);
        host.Owner.CaptureCompletedEdit(selectionChanged: true, sourceTicket: 4);

        // 尚未 Drain 就继续捕获最终结果 A你好D + End + 完成事实（更低来源编号已在捕获阶段发出）。
        host.ReplaceNativeText("A你好D");
        host.Owner.Capture(NativeCompositionFactKind.End, 2, 1, "好", true, sourceTicket: 5);
        host.Owner.CaptureCompletedEdit(sourceTicket: 6);

        host.Owner.DrainAtSafePhase();

        // 两次提交都必须真实发生，且最终结果与结束状态正确。
        Assert.Equal(2, host.Store.SetterCalls);
        Assert.Equal("A你好D", host.Store.Text);
        Assert.False(host.Session.Snapshot.IsComposing, "最终确认后组合应已结束。");
    }

    /// <summary>
    /// 变体：一个事实带两个动作，后一个排队事实的选区按应用顺序仍然生效。
    /// </summary>
    [Fact]
    public void 排队的事实各自的动作按应用顺序互不吞掉()
    {
        using var host = new OwnerHost("ABCD");
        host.SelectRange(4, 4);

        // 事务 1：真实文字写入 ABXCD + caret 落到 2（同一事实两个动作）。
        host.NativeInsert(2, "X");
        host.SetNativeSelection(new TextSelection(2, 2));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: true, sourceTicket: 1);

        // 事务 2：纯选区完成，caret 落到 4（在事务 1 之后排队）。
        host.SetNativeSelection(new TextSelection(4, 4));
        host.Owner.CaptureCompletedEdit(selectionChanged: true, textChanged: false, sourceTicket: 2);

        var result = host.Owner.DrainAtSafePhase();

        Assert.True(result.AnyCommitted, "排队的文字结果被吞掉。");
        Assert.Equal("ABXCD", host.Store.Text);
        Assert.Equal(1, host.Store.SetterCalls);

        // 第二个事务的选区必须生效（应用顺序晚于事务 1）。
        var selection = host.Session.Snapshot.Committed.Selection;
        Assert.Equal(4, selection.Anchor);
        Assert.Equal(4, selection.Active);
    }

    /// <summary>报告固定 ACP 范围的组合视图替身（受控、无真实 COM）。</summary>
    private sealed class FixedRangeCompositionView : ITfCompositionView
    {
        private readonly int _anchor;
        private readonly int _length;

        public FixedRangeCompositionView(int anchor, int length)
        {
            _anchor = anchor;
            _length = length;
        }

        public int GetOwnerClsid(out Guid pclsid)
        {
            pclsid = Guid.Empty;
            return 0;
        }

        public int GetRange(out IntPtr ppRange)
        {
            ppRange = IntPtr.Zero;
            return TsfHResult.E_FAIL;
        }

        /// <summary>该替身不产生真实指针；测试只验证“不走失败分支”的语义。</summary>
        public int Anchor => _anchor;

        public int Length => _length;
    }

    /// <summary>最小 sink 替身（授予回调内触发安全检查）。</summary>
    private sealed class NoOpSink : ITextStoreACPSink
    {
        private readonly Action? _onGranted;

        public NoOpSink(Action? onGranted = null) => _onGranted = onGranted;

        public int OnTextChange(uint dwFlags, ref TsTextChange pChange) => 0;

        public int OnSelectionChange() => 0;

        public int OnLayoutChange(int lcode, uint vcView) => 0;

        public int OnStatusChange(uint dwFlags) => 0;

        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => 0;

        public int OnLockGranted(uint dwLockFlags)
        {
            _onGranted?.Invoke();
            return 0;
        }

        public int OnStartEditTransaction() => 0;

        public int OnEndEditTransaction() => 0;
    }

    /// <summary>
    /// 在授予的写锁内执行一次真实原生写入（选区/文本）并记录其结果。
    /// </summary>
    private sealed class SelectionWriteSink : ITextStoreACPSink
    {
        public Func<int>? Write { get; set; }

        public int WriteResult { get; private set; }

        public int OnTextChange(uint dwFlags, ref TsTextChange pChange) => 0;

        public int OnSelectionChange() => 0;

        public int OnLayoutChange(int lcode, uint vcView) => 0;

        public int OnStatusChange(uint dwFlags) => 0;

        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => 0;

        public int OnLockGranted(uint dwLockFlags)
        {
            WriteResult = Write?.Invoke() ?? 0;
            return WriteResult;
        }

        public int OnStartEditTransaction() => 0;

        public int OnEndEditTransaction() => 0;
    }
}


