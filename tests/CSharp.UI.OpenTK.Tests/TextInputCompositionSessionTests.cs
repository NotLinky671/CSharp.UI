using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P2a：TSF 侧会话适配器与<b>真实</b> <see cref="UiRuntime"/> + <see cref="TextField"/> + Binding 会话。
/// </summary>
/// <remarks>
/// <para>
/// 除显式标注的边界用例（重入/恶意客户端，真实 UI 无法产生）外，全部使用真实编辑事务：
/// 断言绑定 setter 调用次数、撤销历史条目、真实文档版本/选区回读、显示镜像与拒绝结果。
/// </para>
/// <para>不使用窗口、不启用 TSF、不做 UI/输入自动化；文本服务使用无排版副作用的测试替身。</para>
/// </remarks>
public sealed class TextInputCompositionSessionTests
{
    // ---------------------------------------------------------------- 真实宿主

    /// <summary>可编排规范化/拒绝/抛错/重入的绑定。</summary>
    private sealed class ScriptedBinding
    {
        public ScriptedBinding(string initial)
        {
            Text = initial;
            Binding = CSharp.UI.Binding<string>.From(
                () =>
                {
                    GetterHook?.Invoke();
                    return Text;
                },
                Write);
        }

        public CSharp.UI.Binding<string> Binding { get; }

        public string Text { get; set; }

        /// <summary>setter 收到的原始值（未规范化），用于验证“预编辑不写绑定”。</summary>
        public List<string> SetterValues { get; } = [];

        /// <summary>getter 内回调（R5：模拟用户 getter 同步切换焦点/卸载）。</summary>
        public Action? GetterHook { get; set; }

        public Func<string, string>? Normalize { get; set; }

        public bool Refuse { get; set; }

        public bool ThrowAfterMutation { get; set; }

        public Action? AfterStore { get; set; }

        public int SetterCalls => SetterValues.Count;

        private void Write(string value)
        {
            SetterValues.Add(value);
            if (Refuse)
            {
                // setter 明确拒绝：不落地。
                return;
            }

            Text = Normalize?.Invoke(value) ?? value;
            AfterStore?.Invoke();
            if (ThrowAfterMutation)
            {
                throw new InvalidOperationException("scripted setter failure after mutation");
            }
        }
    }

    /// <summary>真实运行时 + 真实 TextField 的宿主；服务替身只转发到真实客户端。</summary>
    private sealed class FieldHost : IDisposable
    {
        public ScriptedBinding Store { get; }
        public RecordingService Service { get; } = new();
        public UiRuntime Runtime { get; }
        public ViewNode Node { get; private set; }

        public FieldHost(string initial, int maxLength = int.MaxValue, View? root = null)
        {
            Store = new ScriptedBinding(initial);
            Runtime = new UiRuntime(
                root ?? new TextField(Store.Binding) { MaxLength = maxLength },
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = Service,
                });
            Runtime.SetViewport(new Size(400, 120), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Node = root is null
                ? Runtime.Root
                : Runtime.Root.Children[0];
            Runtime.SetFocus(Node);
            Runtime.BuildFrame();
        }

        public TextCompositionClient Client
            => State.Composing ?? throw new InvalidOperationException("会话未建立。");

        public TextFieldState State => Assert.IsType<TextFieldState>(Node.InteractionState);

        public int Version => State.Editor.Version;

        public int HistoryCount => State.History.UndoCount;

        public TextInputCompositionSession Adapter(Func<ITextInputClient, bool>? owner = null)
            => new(Client, owner);

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>最小 ITextInputService：把调用转发到真实客户端（不实现任何原生行为）。</summary>
    private sealed class RecordingService : ITextInputService
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

    /// <summary>
    /// 单调操作编号生成器（模拟原生回调序列）：每次投递取一个新编号，用于票据水位校验。
    /// </summary>
    private static long _operationCounter;

    private static long Next() => Interlocked.Increment(ref _operationCounter);

    /// <summary>携带当前票据与一个新操作编号更新预编辑。</summary>
    private static TextInputAdapterResult Update(
        TextInputCompositionSession session,
        string? preedit,
        int? caret = null,
        IReadOnlyList<TextRange>? segments = null)
        => session.UpdatePreedit(session.Token, Next(), preedit, caret, segments);

    /// <summary>携带当前票据与一个新操作编号提交结果。</summary>
    private static TextInputAdapterResult Commit(
        TextInputCompositionSession session,
        string? result,
        string? nextPreedit = null,
        int? nextPreeditCaret = null,
        IReadOnlyList<TextRange>? nextSegments = null)
        => session.CommitResult(session.Token, Next(), result, nextPreedit, nextPreeditCaret, nextSegments);

    /// <summary>
    /// 用真实选区接缝设置已提交选区（不伪造按键/指针事件）。
    /// </summary>
    /// <remarks>
    /// 断言用例关心的是“选中范围上的组合”，不是按键序列算术；直接经 epoch+版本校验的接缝设置，
    /// 走的是与平台桥完全相同的生产路径。
    /// </remarks>
    private static void SelectRange(FieldHost host, int start, int end)
    {
        var client = host.Client;
        Assert.IsAssignableFrom<ITextInputSelectionTarget>(client);
        var target = (ITextInputSelectionTarget)client;
        var applied = target.TrySetCommittedSelection(client.Session, host.Version, new TextSelection(start, end));
        Assert.True(applied, "真实选区接缝应接受当前 epoch/版本下的选区。");
        host.Runtime.ProcessPendingWork();
        Assert.Equal(start, host.State.SelectionStart);
        Assert.Equal(end, host.State.SelectionEnd);
    }

    [Fact]
    public void 普通输入后显式采纳基线可继续原生结果()
    {
        using var host = new FieldHost("");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni").IsRejected);
        Assert.False(Commit(session, "你").IsRejected);
        host.Runtime.DispatchTextInput(new TextInputEvent("x"));
        Assert.True(session.TryAdoptCommittedBaseline(out var snapshot));
        Assert.Equal("你x", snapshot.Committed.Text);
        Assert.Equal(host.Version, snapshot.Committed.Version);

        // 采纳后下一次原生结果必须走真实 Begin → Commit 事务（不再被版本失配拒绝）。
        Assert.False(session.Begin(preedit: "hao").IsRejected);
        Assert.False(Commit(session, "好").IsRejected);
        Assert.Equal("你x好", host.Store.Text);
        Assert.Equal(3, host.Store.SetterCalls);
    }

    [Fact]
    public void 活动组合拒绝采纳普通基线且预编辑不写绑定()
    {
        using var host = new FieldHost("abc");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "nihao").IsRejected);
        Assert.False(session.TryAdoptCommittedBaseline(out _));
        Assert.True(session.Snapshot.IsComposing);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 版本失效后显式采纳也不复活旧会话()
    {
        using var host = new FieldHost("");
        var session = host.Adapter();
        host.Runtime.DispatchTextInput(new TextInputEvent("x"));
        session.Refresh();
        Assert.False(session.IsOpen);
        Assert.False(session.TryAdoptCommittedBaseline(out _));

        // 失效是粘性的：显式采纳不能复活会话，后续真实 Begin/Commit 事务都被拒绝且零写入。
        // （setter 计数已含第 229 行普通输入那一次真实写入，这里比较的是增量。）
        var writesAfterOrdinaryInput = host.Store.SetterCalls;
        Assert.Equal(1, writesAfterOrdinaryInput);
        Assert.True(session.Begin(preedit: "huai").IsRejected);
        Assert.True(Commit(session, "坏").IsRejected);
        Assert.Equal("x", host.Store.Text);
        Assert.Equal(writesAfterOrdinaryInput, host.Store.SetterCalls);
    }

    [Fact]
    public void 归属撤销时拒绝采纳普通基线()
    {
        using var host = new FieldHost("abc");
        var current = true;
        var session = host.Adapter(_ => current);
        current = false;
        Assert.False(session.TryAdoptCommittedBaseline(out _));
        Assert.Equal(0, host.Store.SetterCalls);
    }

    // ---------------------------------------------------------------- 选区组合（前缀/后缀）

    [Fact]
    public void 选中范围组合保留前缀后缀且预编辑整体替换选区()
    {
        using var host = new FieldHost("abcd");
        // 选中 "bc"（[1,3)）：直接用真实选区接缝设置，避免依赖按键次数的算术。
        SelectRange(host, 1, 3);

        var session = host.Adapter();
        var begin = session.Begin(preedit: "XY");

        Assert.False(begin.IsRejected, begin.Reason);
        Assert.True(session.Snapshot.IsComposing);
        // 显示镜像 = 前缀 + 预编辑 + 后缀，不是追加。
        Assert.Equal("aXYd", session.Snapshot.DisplayText);
        Assert.Equal("abcd", session.Snapshot.Committed.Text);
        Assert.Equal(new TextRange(1, 3), session.Snapshot.Replacement);

        // 预编辑不写绑定、不推进版本、不进历史。
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
        Assert.Equal(0, host.HistoryCount);
        Assert.Equal("abcd", host.Store.Text);
    }

    [Fact]
    public void 预编辑多次更新零次setter调用且镜像修订单调递增()
    {
        using var host = new FieldHost("abc");
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        host.Runtime.ProcessPendingWork();

        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "n").IsRejected);
        var revisionAfterBegin = session.Snapshot.MirrorRevision;
        var nativeAfterBegin = session.Snapshot.NativeRevision;

        var texts = new[] { "ni", "nih", "niha", "nihao" };
        for (var i = 0; i < texts.Length; i++)
        {
            var update = Update(session, texts[i], texts[i].Length);
            Assert.False(update.IsRejected, update.Reason);
            Assert.True(session.Snapshot.MirrorRevision > revisionAfterBegin, "每次真实镜像变化都必须推进镜像修订号。");
            Assert.True(session.Snapshot.NativeRevision >= nativeAfterBegin);
        }

        // 20 多次按键级更新：绑定一次都没被调用，版本/历史不变。
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
        Assert.Equal(0, host.HistoryCount);
        Assert.Equal("abc", host.Store.Text);
        Assert.Equal("abcnihao", session.Snapshot.DisplayText);
        Assert.Equal("nihao", session.Snapshot.Preedit);
    }

    [Fact]
    public void 同值预编辑更新不推进镜像修订号()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni").IsRejected);
        var revision = session.Snapshot.MirrorRevision;

        var same = Update(session, "ni", 2);
        Assert.False(same.IsRejected, same.Reason);
        Assert.Equal(revision, session.Snapshot.MirrorRevision);
    }

    // ---------------------------------------------------------------- 提交

    [Fact]
    public void 单次结果只写一次绑定与一项历史且重复结果被拒绝()
    {
        using var host = new FieldHost("abcd");
        SelectRange(host, 1, 3);

        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni").IsRejected);
        Assert.False(Update(session, "nihao", 5).IsRejected);

        var commit = Commit(session, "你好");
        Assert.False(commit.IsRejected, commit.Reason);
        Assert.True(commit.Committed);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal(1, host.HistoryCount);
        Assert.Equal("a你好d", host.Store.Text);
        Assert.Equal("a你好d", session.Snapshot.Committed.Text);
        Assert.False(session.Snapshot.IsComposing);
        Assert.Equal(string.Empty, session.Snapshot.Preedit);
        Assert.Equal("a你好d", session.Snapshot.DisplayText);

        // 重复提交（R1）：票据已退休，同一确认被再次投递时在核心写入前显式拒绝，绝不二次写绑定。
        var duplicate = Commit(session, "你好");
        Assert.True(duplicate.IsRejected);
        Assert.Contains("退休", duplicate.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal(1, host.HistoryCount);
        Assert.Equal("a你好d", host.Store.Text);
    }

    [Fact]
    public void 重复End不二次写入且取消后End幂等()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        var end = session.End(session.Token, Next());
        Assert.False(end.IsRejected, end.Reason);
        Assert.Equal(0, host.Store.SetterCalls);

        // R1：组合已结束，票据退休；重复 End 在核心写入前显式拒绝。
        var duplicate = session.End(session.Token, Next());
        Assert.True(duplicate.IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
    }

    [Fact]
    public void 取消恢复组合前选区且不写绑定()
    {
        using var host = new FieldHost("abcd");
        SelectRange(host, 1, 3);

        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "XY").IsRejected);
        Assert.Equal("aXYd", session.Snapshot.DisplayText);

        var cancel = session.Cancel(session.Token, Next());
        Assert.False(cancel.IsRejected, cancel.Reason);
        Assert.False(session.Snapshot.IsComposing);
        Assert.Equal("abcd", session.Snapshot.Committed.Text);
        Assert.Equal("abcd", session.Snapshot.DisplayText);

        // 取消恢复组合前选区（[1,3)）。
        Assert.Equal(1, host.State.SelectionStart);
        Assert.Equal(3, host.State.SelectionEnd);

        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
        Assert.Equal(0, host.HistoryCount);
    }

    [Fact]
    public void 部分结果加剩余预编辑按真实回读重新锚定而非按结果长度()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();

        Assert.False(session.Begin(preedit: "nihao").IsRejected);
        Assert.Equal("nihao", session.Snapshot.DisplayText);

        // 第一段只接受 "你好"（预编辑里剩 "ma"）：真实回读的 caret 才是下一段锚点。
        var first = Commit(session, "你好", nextPreedit: "ma", nextPreeditCaret: 2);
        Assert.False(first.IsRejected, first.Reason);
        Assert.True(first.Committed);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("你好", host.Store.Text);
        Assert.True(session.Snapshot.IsComposing);
        Assert.Equal("ma", session.Snapshot.Preedit);
        // 显示镜像 = 已提交 "你好" + 剩余预编辑 "ma"（不是把 "ma" 追加到请求结果长度处）。
        Assert.Equal("你好ma", session.Snapshot.DisplayText);
        Assert.Equal(new TextRange(2, 2), session.Snapshot.Replacement);

        var second = Commit(session, "吗");
        Assert.False(second.IsRejected, second.Reason);
        Assert.True(second.Committed);
        Assert.Equal(2, host.Store.SetterCalls);
        Assert.Equal(2, host.HistoryCount);
        Assert.Equal("你好吗", host.Store.Text);
        Assert.False(session.Snapshot.IsComposing);
    }

    [Fact]
    public void 结果不假设版本加一而是采用真实回读版本()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        var before = session.ExpectedVersion;

        Assert.False(session.Begin(preedit: "x").IsRejected);
        var commit = Commit(session, "X");
        Assert.False(commit.IsRejected, commit.Reason);

        // 真实版本来自核心事务回读，不是适配器自己 +1。
        Assert.Equal(host.Version, session.ExpectedVersion);
        Assert.Equal(host.Version, session.Snapshot.Committed.Version);
        Assert.True(session.ExpectedVersion > before);
    }

    // ---------------------------------------------------------------- 真实回读的拒绝路径

    [Fact]
    public void setter拒绝不谎报提交且不写绑定()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        host.Store.Refuse = true;
        var commit = Commit(session, "X");

        Assert.False(commit.IsRejected, commit.Reason);
        Assert.False(commit.Committed);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("ab", host.Store.Text);
        Assert.Equal("ab", session.Snapshot.Committed.Text);
        Assert.Equal(0, host.Version);
        Assert.Equal(0, host.HistoryCount);
    }

    [Fact]
    public void 净化后内容不同时以真实回读为准()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        // setter 把换行净化掉：真实落地文本与请求不同，快照必须报告真实值。
        host.Store.Normalize = value => value.Replace("\n", string.Empty, StringComparison.Ordinal);
        var commit = Commit(session, "X\nY");

        Assert.False(commit.IsRejected, commit.Reason);
        Assert.True(commit.Committed);
        Assert.Equal("abXY", host.Store.Text);
        Assert.Equal("abXY", session.Snapshot.Committed.Text);
        Assert.Equal("abXY", session.Snapshot.DisplayText);
    }

    [Fact]
    public void MaxLength截断后快照等于真实回读()
    {
        using var host = new FieldHost("ab", maxLength: 4);
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        var commit = Commit(session, "XYZ");

        Assert.False(commit.IsRejected, commit.Reason);
        // 真实容量规则：只接受能放下的部分（ab + 2 个字符 = 4）。
        Assert.Equal("abXY", host.Store.Text);
        Assert.Equal("abXY", session.Snapshot.Committed.Text);
        Assert.Equal("abXY", session.Snapshot.DisplayText);
        Assert.Equal(host.Version, session.ExpectedVersion);
    }

    [Fact]
    public void setter抛错后不泄露用户原文且适配器不抛异常()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "secret").IsRejected);

        host.Store.ThrowAfterMutation = true;
        var commit = Commit(session, "机密内容");

        // 适配器自身不抛；结果按真实状态报告。
        Assert.NotNull(commit.Reason);
        Assert.DoesNotContain("机密内容", commit.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", commit.Reason!, StringComparison.Ordinal);

        // 核心诊断同样不得包含用户原文。
        foreach (var line in host.Runtime.Diagnostics)
        {
            Assert.DoesNotContain("机密内容", line, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 未配对代理项结果被拒绝且不写绑定()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        var commit = Commit(session, "a\uD800");

        Assert.True(commit.IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ab", host.Store.Text);
        Assert.Equal(0, host.Version);
    }

    // ---------------------------------------------------------------- 外部变化 / 归属

    [Fact]
    public void 外部绑定变化后旧锚点显式拒绝且不写入()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        // 外部直接改绑定（模拟其它代码写入）。
        host.Store.Text = "外部内容";
        host.Runtime.ProcessPendingWork();

        var update = Update(session, "xy", 2);
        Assert.True(update.IsRejected);
        Assert.Equal("外部内容", host.Store.Text);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 焦点切换后旧捕获不写入新会话()
    {
        var first = new TextField(new ScriptedBinding("aa").Binding);
        var secondStore = new ScriptedBinding("bb");
        var second = new TextField(secondStore.Binding);
        using var host = new FieldHost(string.Empty, root: new VStack(first, second));

        var firstNode = host.Runtime.Root.Children[0];
        host.Runtime.SetFocus(firstNode);
        host.Runtime.BuildFrame();

        var firstClient = Assert.IsType<TextFieldState>(firstNode.InteractionState).Composing!;
        var staleSession = new TextInputCompositionSession(firstClient);
        Assert.False(staleSession.Begin(preedit: "x").IsRejected);

        // 焦点切到第二个字段：旧捕获不再是当前归属。
        var secondNode = host.Runtime.Root.Children[1];
        host.Runtime.SetFocus(secondNode);
        host.Runtime.BuildFrame();

        var stale = staleSession.CommitResult(staleSession.Token, Next(), "X");
        Assert.True(stale.IsRejected);
        Assert.Equal("aa", first.Text.Read(firstNode));
        Assert.Equal("bb", secondStore.Text);
        Assert.Equal(0, secondStore.SetterCalls);
    }

    [Fact]
    public void 归属判定为假时立即拒绝且不调用客户端()
    {
        using var host = new FieldHost("ab");
        var session = new TextInputCompositionSession(host.Client, _ => false);

        var begin = session.Begin(preedit: "x");

        Assert.True(begin.IsRejected);
        Assert.False(session.IsOpen);
        Assert.Equal(0, host.Store.SetterCalls);

        // 之后的调用同样拒绝（失效是粘性的）。
        Assert.True(Update(session, "xy", 2).IsRejected);
        Assert.True(session.End(session.Token, Next()).IsRejected);
    }

    [Fact]
    public void 关闭会话后所有操作幂等拒绝()
    {
        using var host = new FieldHost("ab");
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        session.Close();

        Assert.True(Update(session, "xy", 2).IsRejected);
        Assert.True(Commit(session, "X").IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal("ab", host.Store.Text);
    }

    // ---------------------------------------------------------------- 选区映射

    [Fact]
    public void 预编辑内选区改为更新组合内caret而不动已提交选区()
    {
        using var host = new FieldHost("abcd");
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        host.Runtime.ProcessPendingWork();

        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "XY").IsRejected);
        var committedBefore = host.State.Caret;

        // 显示镜像 "abcdXY"，偏移 5 落在预编辑内部（第 2 个预编辑字符处）。
        var result = session.SetNativeSelection(TextSelection.Caret(5), session.Token, Next());

        Assert.False(result.IsRejected, result.Reason);
        Assert.Equal(1, session.Snapshot.PreeditCaret);
        // 已提交选区没有被当作“预编辑已提交”而移动。
        Assert.Equal(committedBefore, host.State.Caret);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 已提交选区经epoch与版本双校验设置()
    {
        using var host = new FieldHost("abcd");
        var session = host.Adapter();
        host.Runtime.ProcessPendingWork();

        // 显示镜像 == 已提交文档（无组合）：偏移 2 → 已提交偏移 2。
        var result = session.SetNativeSelection(new TextSelection(1, 3));

        Assert.False(result.IsRejected, result.Reason);
        Assert.Equal(1, host.State.SelectionStart);
        Assert.Equal(3, host.State.SelectionEnd);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
    }

    [Fact]
    public void 已提交选区设置拒绝过期版本()
    {
        using var host = new FieldHost("abcd");
        var session = host.Adapter();
        host.Runtime.ProcessPendingWork();

        // 外部改绑定让期望版本失效。
        host.Store.Text = "changed";
        host.Runtime.ProcessPendingWork();

        var result = session.SetNativeSelection(TextSelection.Caret(1));

        Assert.True(result.IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void 原生与已提交坐标在有前缀后缀时按替换区正确互转()
    {
        using var host = new FieldHost("abcd");
        SelectRange(host, 1, 3);

        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "XY").IsRejected);
        // 已提交 "abcd"，替换区 [1,3)，显示 "aXYd"。

        // 已提交偏移 0/1 映射到显示 0/1；替换区内部折叠到预编辑起点；后缀按长度差平移。
        Assert.Equal(0, session.CommittedToNative(0));
        Assert.Equal(1, session.CommittedToNative(1));
        Assert.Equal(1, session.CommittedToNative(2));
        Assert.Equal(3, session.CommittedToNative(3));
        Assert.Equal(4, session.CommittedToNative(4));

        // 反向：显示 3（"d"）→ 已提交 3；显示 2（预编辑内）→ 已提交 1。
        Assert.Equal(3, session.NativeToCommitted(3));
        Assert.Equal(1, session.NativeToCommitted(2));

        // 往返：替换区之外的偏移必须原样回到自身（预编辑内折叠点除外）。
        foreach (var offset in new[] { 0, 1, 3, 4 })
        {
            Assert.Equal(offset, session.NativeToCommitted(session.CommittedToNative(offset)));
        }

        // 显示串长度与镜像一致。
        Assert.Equal(session.Snapshot.DisplayText.Length, session.Snapshot.DisplayLength);
        Assert.Equal(4, session.Snapshot.DisplayLength); // "aXYd"
    }

    [Fact]
    public void 镜像修订号与文档版本互相独立()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "n").IsRejected);

        // 预编辑推进镜像修订，但文档版本不动。
        var mirrorBefore = session.Snapshot.MirrorRevision;
        var versionBefore = session.Snapshot.Committed.Version;
        Assert.False(Update(session, "ni", 2).IsRejected);
        Assert.True(session.Snapshot.MirrorRevision > mirrorBefore);
        Assert.Equal(versionBefore, session.Snapshot.Committed.Version);

        // 提交推进文档版本，镜像修订也推进；两者不相等（不是同一个计数器）。
        var commit = Commit(session, "你");
        Assert.False(commit.IsRejected, commit.Reason);
        Assert.True(session.Snapshot.Committed.Version > versionBefore);
        Assert.True(session.Snapshot.MirrorRevision > mirrorBefore);
        Assert.NotEqual((long)session.Snapshot.Committed.Version, session.Snapshot.MirrorRevision);
    }

    [Fact]
    public void 快照分段是不可变副本()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();

        var segments = new List<TextRange> { new(0, 1) };
        Assert.False(session.Begin(preedit: "ni", preeditCaret: 2, segments: segments).IsRejected);

        // 调用方事后改写原列表不影响已接受的分段。
        segments[0] = new TextRange(1, 2);
        segments.Add(new TextRange(2, 3));

        Assert.Single(session.Snapshot.Segments);
        Assert.Equal(new TextRange(0, 1), session.Snapshot.Segments[0]);
    }

    [Fact]
    public void 字素边界上不拆代理对()
    {
        using var host = new FieldHost("a\uD83D\uDE00b"); // a + 😀 + b
        var session = host.Adapter();
        host.Runtime.ProcessPendingWork();

        // 代理对内部（偏移 2）必须被吸附到边界，不能落在半个码元上。
        var result = session.SetNativeSelection(TextSelection.Caret(2));

        Assert.False(result.IsRejected, result.Reason);
        var caret = host.State.Caret;
        Assert.True(caret is 1 or 3, $"caret 必须吸附到字素边界，实际 {caret}。");
    }

    // ---------------------------------------------------------------- 重入 / 恶意边界（假客户端）

    [Fact]
    public void 回调内重入被显式拒绝且不递归写入()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        TextInputAdapterResult? reentrant = null;
        host.Store.AfterStore = () => reentrant = Update(session, "xy", 2);

        var commit = Commit(session, "X");

        Assert.False(commit.IsRejected, commit.Reason);
        Assert.NotNull(reentrant);
        Assert.True(reentrant!.Value.IsRejected);
        // 拒绝原因必须是“重入”（_applying 守卫），而不是票据水位：证明重入在核心写入前被挡住。
        Assert.Contains("重入", reentrant.Value.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("abX", host.Store.Text);
    }

    // ---------------------------------------------------------------- R1：组合身份票据

    [Fact]
    public void R1分段确认重放被拒绝且不二次写入()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();
        var begin = session.Begin(preedit: "nihao");
        Assert.False(begin.IsRejected, begin.Reason);
        var token = begin.Token;
        Assert.NotNull(token);

        // 第一段确认（结果 + 剩余预编辑）：组合继续，票据仍是同一个。
        var operationId = Next();
        var first = session.CommitResult(token, operationId, "你好", nextPreedit: "ma", nextPreeditCaret: 2);
        Assert.False(first.IsRejected, first.Reason);
        Assert.True(first.Committed);
        Assert.Same(token, first.Token);
        Assert.Equal(1, host.Store.SetterCalls);

        // 同一原生确认被重放（同一 operationId）：核心写入前拒绝，绝不二次写绑定。
        var replay = session.CommitResult(token, operationId, "你好", nextPreedit: "ma", nextPreeditCaret: 2);
        Assert.True(replay.IsRejected);
        Assert.Contains("水位", replay.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, host.Store.SetterCalls);
        Assert.Equal("你好", host.Store.Text);
    }

    [Fact]
    public void R1旧组合结果迟到不套上新组合身份()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();

        // 第一段组合：结束它（票据退休）。
        var firstBegin = session.Begin(preedit: "aa");
        Assert.False(firstBegin.IsRejected, firstBegin.Reason);
        var oldToken = firstBegin.Token!;
        Assert.False(session.End(oldToken, Next()).IsRejected);

        // 同一 epoch 内开新组合：新票据与旧票据不是同一实例。
        var secondBegin = session.Begin(preedit: "bb");
        Assert.False(secondBegin.IsRejected, secondBegin.Reason);
        var newToken = secondBegin.Token!;
        Assert.NotSame(oldToken, newToken);

        // 旧组合的结果迟到：携带旧票据 → 在核心写入前拒绝。
        var late = session.CommitResult(oldToken, Next(), "AA");
        Assert.True(late.IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(string.Empty, host.Store.Text);

        // 旧组合的取消迟到：同样拒绝，新组合不受影响。
        var lateCancel = session.Cancel(oldToken, Next());
        Assert.True(lateCancel.IsRejected);
        Assert.True(session.Snapshot.IsComposing);
        Assert.Equal("bb", session.Snapshot.Preedit);
    }

    [Fact]
    public void R1两次合法相同结果各有独立编号均可提交()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();

        // 第一段：提交 "X"。
        var begin1 = session.Begin(preedit: "x");
        Assert.False(begin1.IsRejected, begin1.Reason);
        var first = session.CommitResult(begin1.Token, Next(), "X");
        Assert.False(first.IsRejected, first.Reason);
        Assert.True(first.Committed);

        // 第二段：内容完全相同，但它是<b>另一次</b>合法提交（不同组合、不同编号）。
        var begin2 = session.Begin(preedit: "x");
        Assert.False(begin2.IsRejected, begin2.Reason);
        var second = session.CommitResult(begin2.Token, Next(), "X");
        Assert.False(second.IsRejected, second.Reason);
        Assert.True(second.Committed);

        // 不能按内容去重：两次都真实提交。
        Assert.Equal(2, host.Store.SetterCalls);
        Assert.Equal("XX", host.Store.Text);
        Assert.Equal(2, host.HistoryCount);
    }

    [Fact]
    public void R1缺少票据或票据不符时拒绝()
    {
        using var host = new FieldHost(string.Empty);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "x").IsRejected);

        // 缺少票据。
        var missing = session.CommitResult(null, Next(), "X");
        Assert.True(missing.IsRejected);
        Assert.Contains("票据", missing.Reason!, StringComparison.Ordinal);

        // 伪造的票据实例（引用不等）。
        var forged = new TextInputCompositionToken(0, 0, 0);
        var mismatch = session.CommitResult(forged, Next(), "X");
        Assert.True(mismatch.IsRejected);

        // 非正操作编号。
        var badId = session.CommitResult(session.Token, 0, "X");
        Assert.True(badId.IsRejected);

        Assert.Equal(0, host.Store.SetterCalls);
    }

    // ---------------------------------------------------------------- R2：Refresh 不得复活旧预编辑

    [Fact]
    public void R2外部绑定变化后Refresh撤销预编辑镜像()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni").IsRejected);
        Assert.True(session.Snapshot.IsComposing);

        // 外部直接改绑定：核心同步取消组合并撤销 epoch。
        host.Store.Text = "external";
        host.Runtime.ProcessPendingWork();

        var refreshed = session.Refresh();

        // 不得返回 IsComposing=true 加旧预编辑。
        Assert.False(refreshed.IsComposing);
        Assert.Equal(string.Empty, refreshed.Preedit);
        Assert.Equal("external", refreshed.Committed.Text);
        Assert.Equal("external", refreshed.DisplayText);
    }

    [Fact]
    public void R2核心本地取消后Refresh撤销镜像并推进修订()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni").IsRejected);
        var mirrorBefore = session.Snapshot.MirrorRevision;

        // 核心本地取消（例如宿主 RequestCancel 路径），不经适配器。
        host.Client.CancelComposition();
        host.Runtime.ProcessPendingWork();

        var refreshed = session.Refresh();

        Assert.False(refreshed.IsComposing);
        Assert.Equal(string.Empty, refreshed.Preedit);
        Assert.True(refreshed.MirrorRevision > mirrorBefore, "镜像撤销必须推进镜像修订号。");
        Assert.Equal("ab", refreshed.Committed.Text);
    }

    [Fact]
    public void R2Close后Refresh不再读取客户端()
    {
        var client = new CountingDocumentClient();
        var session = new TextInputCompositionSession(client);
        session.Close();

        var readsBefore = client.DocumentReads;
        var snapshot = session.Refresh();

        // 关闭后不再回读客户端，只返回最后一次快照。
        Assert.Equal(readsBefore, client.DocumentReads);
        Assert.False(snapshot.IsComposing);
    }

    [Fact]
    public void R2只改选区时镜像内容不变且修订不误增()
    {
        using var host = new FieldHost("abcd");
        var session = host.Adapter();
        host.Runtime.ProcessPendingWork();

        var before = session.Snapshot;
        Assert.False(session.SetNativeSelection(TextSelection.Caret(2)).IsRejected);
        host.Runtime.ProcessPendingWork();

        var after = session.Refresh();
        // 文本与预编辑都没变：镜像修订不推进（选区变化由原生修订反映）。
        Assert.Equal(before.DisplayText, after.DisplayText);
        Assert.Equal(before.MirrorRevision, after.MirrorRevision);
    }

    // ---------------------------------------------------------------- R3：不支持的原生选区

    [Fact]
    public void R3预编辑内非折叠选区显式拒绝且快照不变()
    {
        using var host = new FieldHost("abcd");
        SelectRange(host, 4, 4);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "XY").IsRejected);

        var before = session.Snapshot;
        // 显示 "abcdXY"：范围 [4,6) 是非折叠的预编辑内选区。
        var result = session.SetNativeSelection(new TextSelection(4, 6), session.Token, Next());

        Assert.True(result.IsRejected);
        Assert.Contains("非折叠", result.Reason!, StringComparison.Ordinal);
        Assert.Equal(before.PreeditCaret, session.Snapshot.PreeditCaret);
        Assert.Equal(before.DisplayText, session.Snapshot.DisplayText);
        Assert.Equal(0, host.Store.SetterCalls);
    }

    [Fact]
    public void R3跨已提交与预编辑边界的选区显式拒绝()
    {
        using var host = new FieldHost("abcd");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "XY").IsRejected);

        // 显示 "abXYcd"：范围 [1,3) 跨越已提交文本与预编辑边界。
        var result = session.SetNativeSelection(new TextSelection(1, 3), session.Token, Next());

        Assert.True(result.IsRejected);
        Assert.Equal(0, host.Store.SetterCalls);
        Assert.Equal(0, host.Version);
    }

    [Fact]
    public void R3已提交选区保留真实方向()
    {
        using var host = new FieldHost("abcd");
        var session = host.Adapter();
        host.Runtime.ProcessPendingWork();

        // 反向选区（anchor 3、active 1）：接缝必须保留方向，而不是规范化成 [1,3) 的折叠形态。
        Assert.False(session.SetNativeSelection(new TextSelection(3, 1)).IsRejected);

        Assert.Equal(3, host.State.Anchor);
        Assert.Equal(1, host.State.Caret);
        Assert.Equal(TextSelectionDirection.Backward, new TextSelection(host.State.Anchor, host.State.Caret).Direction);
    }

    // ---------------------------------------------------------------- R4：快照真正不可变

    [Fact]
    public void R4快照分段不可向下转型修改()
    {
        using var host = new FieldHost("ab");
        SelectRange(host, 2, 2);
        var session = host.Adapter();
        Assert.False(session.Begin(preedit: "ni", preeditCaret: 2, segments: [new TextRange(0, 1)]).IsRejected);

        var published = session.Snapshot;
        var segments = published.Segments;

        // 不能向下转型为可写数组。
        Assert.False(segments is TextRange[]);
        Assert.IsNotType<TextRange[]>(segments);

        // 后续更新后旧快照保持稳定。
        Assert.False(Update(session, "nihao", 5, [new TextRange(0, 2)]).IsRejected);
        Assert.Single(published.Segments);
        Assert.Equal(new TextRange(0, 1), published.Segments[0]);
        Assert.Equal(new TextRange(0, 2), session.Snapshot.Segments[0]);
    }

    // ---------------------------------------------------------------- R5：getter 内撤销归属

    [Fact]
    public void R5getter内失焦后选区设置被拒绝()
    {
        using var host = new FieldHost("abcd");

        // 取真实客户端与当前版本（与 Codex 复现步骤一致）。
        var client = host.Client;
        var target = Assert.IsAssignableFrom<ITextInputSelectionTarget>(client);
        var capturedSession = client.Session;
        var capturedVersion = host.Version;

        // 让绑定 getter 在下一次读取时同步清除焦点：这会撤销旧客户端的归属。
        var armed = false;
        host.Store.GetterHook = () =>
        {
            if (armed)
            {
                armed = false;
                host.Runtime.SetFocus(null);
                host.Runtime.ProcessPendingWork();
            }
        };

        armed = true;
        var applied = target.TrySetCommittedSelection(capturedSession, capturedVersion, TextSelection.Caret(1));

        // R5：getter 内已失焦，旧客户端不再是所有者 → 必须拒绝，且不得修改旧节点选区。
        Assert.False(applied, "getter 内撤销归属后，旧客户端的选区设置必须被拒绝。");
        Assert.Null(host.Runtime.FocusedNode);
        Assert.Equal(0, host.State.Caret);
    }

    [Fact]
    public void R5getter内切换焦点后旧客户端选区设置被拒绝()
    {
        var firstStore = new ScriptedBinding("aaaa");
        var secondStore = new ScriptedBinding("bbbb");
        using var host = new FieldHost(
            string.Empty,
            root: new VStack(new TextField(firstStore.Binding), new TextField(secondStore.Binding)));

        var firstNode = host.Runtime.Root.Children[0];
        var secondNode = host.Runtime.Root.Children[1];
        host.Runtime.SetFocus(firstNode);
        host.Runtime.BuildFrame();

        var firstState = Assert.IsType<TextFieldState>(firstNode.InteractionState);
        var oldClient = firstState.Composing!;
        var target = Assert.IsAssignableFrom<ITextInputSelectionTarget>(oldClient);
        var capturedSession = oldClient.Session;
        var capturedVersion = firstState.Editor.Version;

        var armed = false;
        firstStore.GetterHook = () =>
        {
            if (armed)
            {
                armed = false;
                host.Runtime.SetFocus(secondNode);
                host.Runtime.ProcessPendingWork();
            }
        };

        armed = true;
        var applied = target.TrySetCommittedSelection(capturedSession, capturedVersion, TextSelection.Caret(3));

        Assert.False(applied, "getter 内切换焦点后，旧客户端的选区设置必须被拒绝。");
        Assert.Same(secondNode, host.Runtime.FocusedNode);
        // 旧节点选区未被改动。
        Assert.Equal(0, firstState.Caret);
    }

    // ---------------------------------------------------------------- R6：回读失败不得写事件

    [Fact]
    public void R6回读失败后所有写操作零ApplyComposition调用()
    {
        var client = new ThrowingAfterConstructionClient();
        var session = new TextInputCompositionSession(client);

        // 构造时回读成功；之后 Document 开始抛错。
        client.BreakDocument();

        var begin = session.Begin(preedit: "x");
        var update = session.UpdatePreedit(null, 1, "xy", 2);
        var commit = session.CommitResult(null, 2, "X");
        var cancel = session.Cancel(null, 3);
        var end = session.End(null, 4);
        var selection = session.SetNativeSelection(TextSelection.Caret(1));

        Assert.True(begin.IsRejected);
        Assert.True(update.IsRejected);
        Assert.True(commit.IsRejected);
        Assert.True(cancel.IsRejected);
        Assert.True(end.IsRejected);
        Assert.True(selection.IsRejected);

        // 关键：回读失败后绝不投递写事件。
        Assert.Equal(0, client.ApplyCalls);
    }

    [Fact]
    public void R6Refresh在回读失败后撤销镜像且不乐观成功()
    {
        var client = new ThrowingAfterConstructionClient();
        var session = new TextInputCompositionSession(client);
        client.BreakDocument();

        var refreshed = session.Refresh();

        Assert.False(refreshed.IsComposing);
        Assert.Equal(string.Empty, refreshed.Preedit);
        Assert.Equal(0, client.ApplyCalls);
    }

    // ---------------------------------------------------------------- R7：已确认结果不得被回读失败抹掉

    [Fact]
    public void R7提交后回读失败仍保留Handled与Committed()
    {
        // 客户端：ApplyComposition 返回真实成功结果，之后 Document getter 开始抛错。
        var client = new CommittedThenThrowingClient();
        var session = new TextInputCompositionSession(client);

        var begin = session.Begin(preedit: "nihao");
        Assert.False(begin.IsRejected, begin.Reason);
        var token = begin.Token!;

        var operationId = Next();
        var commit = session.CommitResult(token, operationId, "你好");

        // 关键：核心确实写入了一次，适配器必须如实报告，而不是 Handled=false / Committed=false。
        // ApplyCalls=2：Begin 投递了 Start，Commit 投递了 Result；真实提交只有 1 次。
        Assert.Equal(2, client.ApplyCalls);
        Assert.Equal(1, client.CommitCalls);
        Assert.True(commit.Handled, "已确认的 Handled 不得因为后置回读失败而丢失。");
        Assert.True(commit.Committed, "已确认的 ResultCommitted 不得因为后置回读失败而丢失。");
        Assert.False(commit.IsRejected, "已执行动作的结论不能被降级为“没有任何动作”。");
        Assert.True(commit.IsAppliedButUnsynchronized);
        Assert.NotNull(commit.Reason);
        Assert.Contains("回读失败", commit.Reason!, StringComparison.Ordinal);

        // 使用核心确认的权威 Document（不是适配器猜测的版本递增）。
        Assert.Equal(client.ConfirmedDocument.Text, commit.Snapshot.Committed.Text);
        Assert.Equal(client.ConfirmedDocument.Version, commit.Snapshot.Committed.Version);
        Assert.Equal(1, commit.ApplyResult.AppliedEventCount);

        // 会话失效、预编辑镜像撤销、票据退休。
        Assert.False(session.IsOpen);
        Assert.False(commit.Snapshot.IsComposing);
        Assert.Equal(string.Empty, commit.Snapshot.Preedit);
        Assert.True(token.IsRetired);

        // 后续操作在核心写入前被拒绝：不会二次写入。
        var appliesBefore = client.ApplyCalls;
        var subsequent = session.CommitResult(token, Next(), "你好");
        Assert.True(subsequent.IsRejected);
        Assert.Equal(appliesBefore, client.ApplyCalls);
        Assert.Equal(1, client.CommitCalls);
    }

    [Fact]
    public void R7setter内换焦点保留已确认提交且不二次写入()
    {
        // 真实 TextField：setter 内同步切换焦点（真实 UI 可产生的归属变化）。
        var firstStore = new ScriptedBinding("ab");
        var secondStore = new ScriptedBinding("cd");
        using var host = new FieldHost(
            string.Empty,
            root: new VStack(new TextField(firstStore.Binding), new TextField(secondStore.Binding)));

        var firstNode = host.Runtime.Root.Children[0];
        var secondNode = host.Runtime.Root.Children[1];
        host.Runtime.SetFocus(firstNode);
        host.Runtime.BuildFrame();

        var firstState = Assert.IsType<TextFieldState>(firstNode.InteractionState);
        var client = firstState.Composing!;
        var session = new TextInputCompositionSession(client, c => ReferenceEquals(c, host.Runtime.TextInputSession.ActiveClient));

        // caret 放到 "ab" 末尾，使结果追加而不是插到开头。
        SelectRange(host, 2, 2);

        var begin = session.Begin(preedit: "ni");
        Assert.False(begin.IsRejected, begin.Reason);
        var token = begin.Token!;

        // setter 内换焦点：事务已真实提交，但归属随后被撤销。
        firstStore.AfterStore = () =>
        {
            host.Runtime.SetFocus(secondNode);
            host.Runtime.ProcessPendingWork();
        };

        var commit = session.CommitResult(token, Next(), "你");

        // 真实写入恰好一次（第一个字段），结果如实保留。
        Assert.Equal(1, firstStore.SetterCalls);
        Assert.Equal(0, secondStore.SetterCalls);
        Assert.True(commit.Handled);
        Assert.True(commit.Committed);
        Assert.False(commit.IsRejected);

        // 焦点已在第二个字段；第一个字段的绑定只被写了一次。
        Assert.Same(secondNode, host.Runtime.FocusedNode);
        Assert.Equal("ab你", firstStore.Text);

        // 票据退休后旧捕获不再写入。
        Assert.True(token.IsRetired);
        var appliesBefore = firstStore.SetterCalls;
        var subsequent = session.CommitResult(token, Next(), "你");
        Assert.True(subsequent.IsRejected);
        Assert.Equal(appliesBefore, firstStore.SetterCalls);
    }

    [Fact]
    public void R7读前失败仍是零投递且显式拒绝()
    {
        // R6 保持：ApplyComposition 之前的回读失败 → 零投递、显式拒绝、无 Handled。
        var client = new ThrowingAfterConstructionClient();
        var session = new TextInputCompositionSession(client);
        client.BreakDocument();

        var begin = session.Begin(preedit: "x");

        Assert.True(begin.IsRejected);
        Assert.False(begin.Handled);
        Assert.False(begin.Committed);
        Assert.False(begin.IsAppliedButUnsynchronized);
        Assert.Equal(0, client.ApplyCalls);
    }

    /// <summary>
    /// ApplyComposition 返回真实成功结果后，Document getter 开始抛错的客户端（R7 边界）。
    /// </summary>
    /// <remarks>
    /// 模拟“核心已确认提交，但适配器随后无法回读”——真实 UI 无法稳定产生该序列，故用替身覆盖。
    /// </remarks>
    private sealed class CommittedThenThrowingClient : ITextInputClient
    {
        private readonly TextSnapshot _confirmed = new("你好", TextSelection.Caret(2), version: 1);

        private bool _breakAfterCommit;

        public int ApplyCalls { get; private set; }

        public int CommitCalls { get; private set; }

        /// <summary>核心确认的权威文档（用于断言适配器没有伪造版本）。</summary>
        public TextSnapshot ConfirmedDocument => _confirmed;

        public TextSnapshot Document
            => _breakAfterCommit
                ? throw new InvalidOperationException("readback failure after confirmed commit")
                : new TextSnapshot("ab", TextSelection.Caret(2));

        public TextInputSession Session => new(1);

        public bool IsComposing => false;

        public string Preedit => string.Empty;

        public TextInputState GetState() => new(Document);

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        {
            ApplyCalls++;
            var kind = events.Count > 0 ? events[0].Kind : CompositionEventKind.Start;

            // Begin（Start）与 Update 都成功消费，但不产生提交。
            if (kind != CompositionEventKind.Result)
            {
                return new TextInputApplyResult(
                    kind == CompositionEventKind.Start ? TextInputOutcome.Started : TextInputOutcome.Updated,
                    new TextSnapshot("ab", TextSelection.Caret(2)),
                    Handled: true,
                    RejectionReason: null,
                    AppliedEventCount: 1);
            }

            // 真实提交一次，随后让回读开始失败（R7 的确切时序）。
            CommitCalls++;
            _breakAfterCommit = true;
            return new TextInputApplyResult(
                TextInputOutcome.ResultCommitted,
                _confirmed,
                Handled: true,
                RejectionReason: null,
                AppliedEventCount: 1);
        }

        public bool CancelComposition() => false;
    }

    /// <summary>
    /// 构造时回读成功、之后 Document 抛错的客户端（R6 边界：真实 UI 无法产生）。
    /// </summary>
    private sealed class ThrowingAfterConstructionClient : ITextInputClient
    {
        private bool _broken;

        public int ApplyCalls { get; private set; }

        public void BreakDocument() => _broken = true;

        public TextSnapshot Document
            => _broken
                ? throw new InvalidOperationException("readback failure after construction")
                : new TextSnapshot("ab", TextSelection.Caret(2));

        public TextInputSession Session => new(1);

        public bool IsComposing => false;

        public string Preedit => string.Empty;

        public TextInputState GetState() => new(new TextSnapshot("ab", TextSelection.Caret(2)));

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        {
            ApplyCalls++;
            return TextInputApplyResult.NotHandled(new TextSnapshot("ab", TextSelection.Caret(2)));
        }

        public bool CancelComposition() => false;
    }

    /// <summary>统计回读次数的客户端（覆盖“Close 后不再读取”）。</summary>
    private sealed class CountingDocumentClient : ITextInputClient
    {
        public int DocumentReads { get; private set; }

        public TextSnapshot Document
        {
            get
            {
                DocumentReads++;
                return new TextSnapshot("ab", TextSelection.Caret(2));
            }
        }

        public TextInputSession Session => new(1);

        public bool IsComposing => false;

        public string Preedit => string.Empty;

        public TextInputState GetState() => new(Document);

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => TextInputApplyResult.NotHandled(Document);

        public bool CancelComposition() => false;
    }

    [Fact]
    public void 客户端文档回读抛错时适配器不抛异常()
    {
        // 真实 UI 无法产生“回读永久抛错”的客户端，用最小假客户端覆盖该边界。
        var client = new ThrowingDocumentClient();
        var session = new TextInputCompositionSession(client);

        var begin = session.Begin(preedit: "x");

        Assert.True(begin.IsRejected);
        Assert.Equal(0, client.ApplyCalls);
    }

    /// <summary>只用于覆盖“回读抛错”边界的最小客户端。</summary>
    private sealed class ThrowingDocumentClient : ITextInputClient
    {
        public int ApplyCalls { get; private set; }

        public TextSnapshot Document => throw new InvalidOperationException("readback failure");

        public TextInputSession Session => new(1);

        public bool IsComposing => false;

        public string Preedit => string.Empty;

        public TextInputState GetState() => new(TextSnapshot.Empty);

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        {
            ApplyCalls++;
            return TextInputApplyResult.NotHandled(TextSnapshot.Empty);
        }

        public bool CancelComposition() => false;
    }
}
