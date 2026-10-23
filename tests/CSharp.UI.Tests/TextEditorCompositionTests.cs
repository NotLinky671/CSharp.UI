using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TE-04：真实 <see cref="UiRuntime"/> + <see cref="TextEditor"/> 的组合生命周期 ——
/// 共享 <see cref="TextCompositionClient"/> 的 start/update/result/cancel/stale 拒绝、
/// 跨行 replacement 与换行结果、组合期间按键归属、焦点/卸载撤销。
/// </summary>
/// <remarks>
/// 与 <see cref="TextFieldCompositionTests"/> 同型：注入假 <see cref="ITextInputService"/>，
/// 用真实焦点/按键分发驱动真实客户端；断言绑定写入、版本、选区、显示串与拒绝结果。
/// </remarks>
public class TextEditorCompositionTests
{
    // ---------------------------------------------------------------- 生命周期

    [Fact]
    public void FocusStart_ComposesAcrossLines_ResultWritesSingleTransaction()
    {
        var binding = new ScriptedTextBinding("hello\nworld");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        var client = Client(node);
        Assert.Equal(node.Runtime.TextInputSession, State(node).InputSession);
        Assert.True(service.StartCount >= 1);

        // 在第二行行尾（显示 offset 11）插入预编辑；replacement 为空范围。
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(11, 11), "!", TextSelection.Caret(11))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Update(
            client.Session.SessionId, 0, Editor(node).Version, 1,
            new TextRange(11, 11), "!!", null, [new TextRange(0, 2)])), TextInputOutcome.Updated);

        // 预编辑不写绑定、不进历史、显示串带预编辑。
        Assert.Equal("hello\nworld", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("hello\nworld!!", client.DisplayString);
        Assert.True(client.IsComposing);

        AssertApplied(service.Apply(CompositionEvent.Result(
            client.Session.SessionId, 0, Editor(node).Version, 2, "!!")), TextInputOutcome.ResultCommitted);
        Assert.Equal("hello\nworld!!", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.False(client.IsComposing);
    }

    [Fact]
    public void ResultContainingNewline_CommitsAcrossLines()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(1, 1), "x", TextSelection.Caret(1))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(
            client.Session.SessionId, 0, Editor(node).Version, 1, "x\ny")), TextInputOutcome.ResultCommitted);

        Assert.Equal("ax\nyb", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        // 换行结果只经一次事务提交；历史记录一条 CompositionResult。
        Assert.Equal(1, State(node).History.UndoCount);
    }

    [Fact]
    public void ReplacementSpansLines_NormalizesOnlyReplacement()
    {
        var binding = new ScriptedTextBinding("one\ntwo\nthree");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        // 替换 "two\nthree"（跨行 range），结果文本自身带 \n。
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(4, 13), "x", TextSelection.Caret(4))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(
            client.Session.SessionId, 0, Editor(node).Version, 1, "2\r\n3")), TextInputOutcome.ResultCommitted);

        // 只 normalize replacement 文本（\r\n → \n），文档其余部分不动。
        Assert.Equal("one\n2\n3", binding.Text);
    }

    [Fact]
    public void Cancel_RestoresCommittedSelectionAndKeepsDocument()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.End);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        Assert.Equal(4, state.Caret);
        Assert.Equal(6, state.Anchor);

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(4, 6), "ZZ", new TextSelection(6, 4))), TextInputOutcome.Started);
        Assert.True(client.IsComposing);

        Assert.True(service.RequestCancel());
        Assert.False(client.IsComposing);
        Assert.Equal("abcdef", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(4, state.Caret);
        Assert.Equal(6, state.Anchor);
    }

    // ---------------------------------------------------------------- 按键归属

    [Fact]
    public void ComposingSwallowsNonCommandKeys()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;
        var state = State(node);
        app.Key(UiKey.End);
        Assert.Equal(4, state.Caret);

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(4, 4), "n", TextSelection.Caret(4))), TextInputOutcome.Started);

        // 组合期间非命令键归输入法：caret 不动、不取消组合、事件被消费。
        app.Key(UiKey.Left);
        Assert.Equal(4, state.Caret);
        Assert.True(client.IsComposing);
        app.Key(UiKey.Backspace);
        Assert.Equal("abcd", binding.Text);
        Assert.True(client.IsComposing);
        app.Key(UiKey.Enter);
        Assert.True(client.IsComposing);
    }

    [Fact]
    public void CommandKeyDuringComposing_CancelsThenExecutes()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(0, 0), "n", TextSelection.Caret(0))), TextInputOutcome.Started);

        // Ctrl+A：先取消组合再全选已提交文档。
        app.Key(UiKey.A, KeyModifiers.Control);
        Assert.False(client.IsComposing);
        Assert.Equal(0, State(node).Anchor);
        Assert.Equal(6, State(node).Caret);
    }

    // ---------------------------------------------------------------- stale / 失效

    [Fact]
    public void StaleSessionId_IsRejected()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var wrong = client.Session.SessionId + 999;
        var result = service.Apply(CompositionEvent.Start(wrong, 0, 0, 0, default, "n"));
        Assert.True(result.HasRejection);
        Assert.False(client.IsComposing);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void ExternalWriteDuringComposing_InvalidatesEpochAndRejects()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(4, 4), "n", TextSelection.Caret(4))), TextInputOutcome.Started);
        Assert.True(client.IsComposing);

        // 真实绑定外部写入：下一事件前同步发现文档版本不匹配，组合失效。
        binding.Text = "abCD_qr";
        var result = service.Apply(CompositionEvent.Update(
            client.Session.SessionId, 0, 0, Editor(node).Version + 100, new TextRange(4, 4), "nn"));
        Assert.True(result.HasRejection || client.EpochInvalidationRequested);
    }

    [Fact]
    public void BlurAndRefocus_NewEpoch_OldClientRejected()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding);
        using var _app = app;

        var oldClient = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(
            oldClient.Session.SessionId, 0, 0, Editor(node).Version,
            new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();

        var newClient = Client(node);
        Assert.NotSame(oldClient, newClient);
        Assert.True(oldClient.IsReleased);
        Assert.NotEqual(oldClient.Session.SessionId, newClient.Session.SessionId);

        // 旧 epoch 的组合事件被拒绝。
        var result = service.Apply(CompositionEvent.Update(
            oldClient.Session.SessionId, 0, 0, Editor(node).Version, new TextRange(2, 2), "nn"));
        Assert.True(result.HasRejection);
    }

    // ---------------------------------------------------------------- 几何

    [Fact]
    public void PublishGeometry_AfterScroll_RefreshesSnapshotScrollY()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line-{i:00}"));
        var binding = new ScriptedTextBinding(lines);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding, viewport: new Size(400, 120));
        using var _app = app;
        var state = State(node);

        // 到文档末尾并滚动。
        app.Key(UiKey.End, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(state.ScrollY > 0);

        var client = Client(node);
        var source = (ICompositionCharGeometrySource)client;
        var snapshot = source.CompositionCharGeometry;
        Assert.NotNull(snapshot);
        Assert.Equal(state.ScrollY, snapshot!.ScrollY);
        Assert.Equal(state.CaretAffinity, snapshot.Affinity);

        // ScrollY 变化（向上滚）使旧快照立即失效。
        app.Runtime.DispatchWheel(new WheelEvent(node.Frame.Center, 0, 3));
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(state.ScrollY < snapshot.ScrollY);
        var fresh = source.CompositionCharGeometry;
        Assert.True(fresh is null || fresh.ScrollY == state.ScrollY);
    }

    [Fact]
    public void PublishGeometry_CaretAnchorMatchesEmitTopAlignedOrigin()
    {
        // 回归：内容短于视口时发布原点必须与 Emit 相同（textBounds 左上、顶部对齐），
        // 不能走 TextPainter.ResolveOrigin 的垂直居中——否则 caret 锚点比绘制位置低半高差。
        var binding = new ScriptedTextBinding("ab\ncd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateEditor(service, binding.Binding, viewport: new Size(400, 300));
        using var _app = app;

        app.Runtime.ProcessPendingWork();
        var geometry = service.LastPublished;
        Assert.True(geometry.HasCaret, "焦点编辑器的 caret 几何应已发布");

        var state = State(node);
        var inner = InnerRect(node);
        var layout = app.Runtime.Services.TextService.Layout(TextPainter.CreateRequest(
            "ab\ncd", node.Environment.Font ?? FontDescription.Default, inner.Width,
            node.Runtime.PixelScale, wrapping: TextWrapping.Wrap, maxLines: int.MaxValue,
            lineHeight: 0) with { Editing = true });

        // caret 0 在第一行行首；Emit 顶部对齐原点 = SnapToPhysicalPixel(inner.X, inner.Y - ScrollY)，
        // caret 屏幕位置 = 原点 + 排版 caret 偏移。
        var caretBounds = layout.GetCaretBounds(0, state.CaretAffinity);
        var scale = layout.PixelScale <= 0 ? 1.0 : layout.PixelScale;
        var originX = Math.Round(inner.X * scale, MidpointRounding.AwayFromZero) / scale;
        var originY = Math.Round((inner.Y - state.ScrollY) * scale, MidpointRounding.AwayFromZero) / scale;
        Assert.Equal(originY + caretBounds.Y, geometry.CaretBounds.Y, 3);
        Assert.Equal(originX + caretBounds.X, geometry.CaretBounds.X, 3);
    }

    private static Rect InnerRect(ViewNode node)
    {
        var editor = Assert.IsType<TextEditor>(node.View);
        return node.Frame.Deflate(new Thickness(editor.HorizontalPadding, editor.VerticalPadding));
    }

    // ---------------------------------------------------------------- helpers

    private static (TestApp App, object Host, ViewNode Node) CreateEditor(
        FakeTextInputService service,
        Binding<string> binding,
        Size? viewport = null)
    {
        var editor = new TextEditor(binding, null) { Width = 200, MinLines = 3 };
        var app = new TestApp(new VStack(editor), viewport ?? new Size(400, 300), textInputService: service);
        app.Frame();
        var node = app.Runtime.Root.FindDescendants<TextEditor>().First();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return (app, new object(), node);
    }

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端（会话未建立）。");

    private static TextEditorState State(ViewNode node) => Assert.IsType<TextEditorState>(node.InteractionState);

    private static TextEditSession Editor(ViewNode node) => State(node).Editor;

    private static void AssertApplied(TextInputApplyResult result, TextInputOutcome expected)
    {
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(expected), $"期望 {expected}，实际 {result.Outcome}（{result.RejectionReason}）。");
    }

    private sealed class ScriptedTextBinding
    {
        public ScriptedTextBinding(string initial) => Text = initial;
        public string Text { get; set; }
        public int SetterCalls;
        public Binding<string> Binding => new(() => Text, v => { Text = v; SetterCalls++; });
    }

    /// <summary>最小假服务：把事件投递给当前活动客户端，与 TextFieldCompositionTests 同型。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities { get; set; } = TextInputCapabilities.Full;
        public ITextInputClient? ActiveClient { get; private set; }
        public int StartCount { get; private set; }

        public bool StartSession(ITextInputClient client)
        {
            StartCount++;
            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
               ?? throw new InvalidOperationException("no active client");

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public TextInputGeometry LastPublished { get; private set; }
        public int PublishCount { get; private set; }

        public bool PublishGeometry(TextInputGeometry geometry)
        {
            LastPublished = geometry;
            PublishCount++;
            return true;
        }

        public TextInputApplyResult Apply(params CompositionEvent[] events)
            => ActiveClient?.ApplyComposition(events)
               ?? throw new InvalidOperationException("no active client");
    }
}
