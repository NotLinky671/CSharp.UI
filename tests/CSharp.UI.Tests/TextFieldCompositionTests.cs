using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-04：真实 TextField 的 ITextInputClient、组合状态机与焦点/生命周期接线。
/// </summary>
/// <remarks>
/// <para>
/// 通过注入的假 <see cref="ITextInputService"/> 与真实 UI 输入分发（焦点、指针、按键、已提交文本、窗口激活）
/// 驱动节点上的真实客户端，断言绑定写入次数、文档版本、选区、显示串与拒绝结果；不使用原生 IME。
/// </para>
/// <para>
/// 组合事件用确定性的表驱动序列构造；断言的是文档/选区/写入次数等真实不变量，而不是实现公式。
/// </para>
/// </remarks>
public class TextFieldCompositionTests
{
    private const int Session = 1;

    // ---------------------------------------------------------------- 预编辑与显示映射

    [Fact]
    public void LongPreeditWritesNothingAndKeepsFixedReplacement()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        var state = State(node);
        Assert.Equal(2, state.Caret);
        Assert.Equal(4, state.Anchor);

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 4), "n", new TextSelection(4, 2))), TextInputOutcome.Started);
        for (var i = 1; i <= 40; i++)
        {
            var update = CompositionEvent.Update(Session, 0, 0, i, new TextRange(0, 0), new string('n', i + 1), null, [new TextRange(0, 1)]);
            AssertApplied(service.Apply(update), TextInputOutcome.Updated);
        }

        // 长预编辑不写绑定、不推进版本、不改已提交选区。
        Assert.Equal("abcd", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal(2, state.Caret);
        Assert.Equal(4, state.Anchor);

        var client = Client(node);
        Assert.True(client.IsComposing);
        Assert.Equal(41, client.Preedit.Length);
        Assert.Equal("ab" + client.Preedit, client.DisplayString);
        Assert.Single(client.Segments);
        Assert.Equal(0, client.Segments[0].Start);
    }

    [Fact]
    public void DisplayMappingRestoresCommittedDocumentOffsets()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        // 替换区 [1,3) 覆盖 "bc"，显示串应为 a + XY + d。
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(1, 3), "XY")), TextInputOutcome.Started);
        var client = Client(node);
        Assert.Equal("aXYd", client.DisplayString);

        var prefix = client.MapDisplayToDocument(0);
        Assert.Equal(TextCompositionDisplayRegion.Committed, prefix.Region);
        Assert.Equal(0, prefix.ToDocumentOffset());

        var preedit = client.MapDisplayToDocument(2);
        Assert.Equal(TextCompositionDisplayRegion.Preedit, preedit.Region);
        Assert.Equal(1, preedit.ToDocumentOffset());
        Assert.Equal(1, preedit.ToPreeditOffset());

        // 显示偏移 3（"d"）映射回替换区之后的真实文档偏移 3。
        var suffix = client.MapDisplayToDocument(3);
        Assert.Equal(TextCompositionDisplayRegion.Committed, suffix.Region);
        Assert.Equal(3, suffix.ToDocumentOffset());
    }

    [Fact]
    public void UpdateDoesNotRewriteFixedReplacementFromEvent()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 2), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Update(Session, 0, 0, 1, new TextRange(3, 3), "ni")), TextInputOutcome.Updated);

        Assert.Equal("nicd", Client(node).DisplayString);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 2, "ni")), TextInputOutcome.ResultCommitted);
        Assert.Equal("nicd", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
    }

    [Fact]
    public void SegmentsSnapshotIsImmutableAfterAcceptance()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var segments = new List<TextRange> { new(0, 1) };
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "ni", null, null, segments)), TextInputOutcome.Started);
        segments[0] = new TextRange(1, 2);
        segments.Add(new TextRange(3, 4));

        Assert.Single(Client(node).Segments);
        Assert.Equal(new TextRange(0, 1), Client(node).Segments[0]);
    }

    // ---------------------------------------------------------------- R1：分段重基

    [Fact]
    public void SegmentedResultsPreserveCommittedPrefixAndSuffix()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        // 选择 "bc"（替换区 [1,3)），第一段提交 "你好" 并带下一段预编辑。
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(1, 3), "nihao", new TextSelection(3, 1))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你好", default, "shi")), TextInputOutcome.ResultCommitted);

        // 已确认段保留，前缀 "a" 与后缀 "d" 都保留；下一段从真实提交后的 caret 3 继续。
        Assert.Equal("a你好d", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(1, Editor(node).Version);
        var client = Client(node);
        Assert.True(client.IsComposing);
        Assert.Equal("a你好shid", client.DisplayString);
        Assert.Equal(new TextRange(3, 3), client.Composition.Active!.Value.Replacement);

        // 同一组合 ID 的下一段继续，序号递增，版本锚定真实回读值。
        AssertApplied(service.Apply(CompositionEvent.Update(Session, 0, 1, 2, default, "sh")), TextInputOutcome.Updated);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 1, 3, "世")), TextInputOutcome.ResultCommitted);

        Assert.Equal("a你好世d", binding.Text);
        Assert.Equal(2, binding.SetterCalls);
        Assert.Equal(2, Editor(node).Version);
        Assert.False(client.IsComposing);
    }

    [Fact]
    public void ExactSegmentedSequenceFromReviewKeepsCommittedText()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(client.ApplyComposition([CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(0, 2), "nihao")]), TextInputOutcome.Started);
        Assert.Equal(0, binding.SetterCalls);

        AssertApplied(client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 1, "你好", new TextRange(0, 2), "shi")]), TextInputOutcome.ResultCommitted);
        Assert.Equal("你好", binding.Text);
        Assert.Equal(1, binding.SetterCalls);

        // 下一段使用真实回读版本与真实 caret；结果只提交一次，已确认的 "你好" 不被覆盖。
        AssertApplied(client.ApplyComposition([CompositionEvent.Result(epoch, 0, client.Document.Version, 2, "世", new TextRange(2, 2))]), TextInputOutcome.ResultCommitted);
        Assert.Equal("你好世", binding.Text);
        Assert.Equal(2, binding.SetterCalls);
        Assert.Equal(2, Editor(node).Version);
    }

    [Fact]
    public void DelayedOldClientCallbacksDoNotAffectNewFieldsComposition()
    {
        var first = new ScriptedTextBinding("ab");
        var second = new ScriptedTextBinding("cd");
        var service = new FakeTextInputService();
        var host = new TwoFieldHost(first.Binding, second.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.GetFocusOrder()[0];
        var secondNode = app.Runtime.GetFocusOrder()[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        var oldClient = Client(firstNode);
        var oldEpoch = oldClient.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(oldEpoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 切到第二个字段并开始新组合。
        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();
        var newClient = Client(secondNode);
        AssertApplied(service.Apply(CompositionEvent.Start(newClient.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "m", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 延迟到达的旧取消/旧结果不得影响新字段的活动组合。
        Assert.False(oldClient.CancelComposition());
        Assert.True(oldClient.ApplyComposition([CompositionEvent.Cancel(oldEpoch, 0, 0, 1)]).HasRejection);
        Assert.True(oldClient.ApplyComposition([CompositionEvent.Result(oldEpoch, 0, 0, 1, "X")]).HasRejection);

        Assert.True(newClient.IsComposing);
        Assert.Equal("m", newClient.Preedit);
        Assert.Equal(0, first.SetterCalls);
        Assert.Equal(0, second.SetterCalls);
        Assert.Same(newClient, service.ActiveClient);
    }

    [Fact]
    public void SourceSwapDuringResultDoesNotWriteStaleTransactionIntoNewSource()
    {
        var oldBinding = new ScriptedTextBinding("ab");
        const string NewValue = "NEW";
        var newBinding = Binding<string>.From(() => NewValue, _ => { });
        var host = new SwitchableSourceHost(oldBinding.Binding);
        TestApp? app = null;

        oldBinding.AfterStore = () =>
        {
            host.Current = newBinding;
            host.Tick!.Value++;
            app!.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 120), textInputService: new FakeTextInputService());
        app.Frame();
        var node = app.Runtime.GetFocusOrder()[0];
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        var service = (FakeTextInputService)app.Runtime.Services.TextInputService!;
        var client = Client(node);

        AssertApplied(service.Apply(CompositionEvent.Start(client.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        var result = service.Apply(CompositionEvent.Result(client.Session.SessionId, 0, 0, 1, "Z"));
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));

        // 事务期间的来源替换：旧事务不得把结果写进新来源；会话已按新来源重同步。
        Assert.Equal(NewValue, newBinding.Value);
        Assert.Equal("NEW", Editor(node).Text);
        Assert.Equal("NEW", ((TextField)node.View).Text.Value);
    }

    [Fact]
    public void NextSegmentUsesActualAfterSelectionAfterNormalization()
    {
        var binding = new ScriptedTextBinding(string.Empty) { NormalizeOnce = value => value.Replace("你好", "你") };
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你好", default, "h")), TextInputOutcome.ResultCommitted);

        // setter 把 "你好" 规范化为 "你"：下一段必须锚定真实回读后的 caret 1，而不是请求长度 2。
        Assert.Equal("你", binding.Text);
        Assert.Equal(new TextRange(1, 1), Client(node).Composition.Active!.Value.Replacement);
        Assert.Equal("你h", Client(node).DisplayString);

        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 1, 2, "好")), TextInputOutcome.ResultCommitted);
        Assert.Equal("你好", binding.Text);
        Assert.Equal(2, binding.SetterCalls);
    }

    [Fact]
    public void CancelDropsOnlyUncommittedSegmentAndRestoresSelection()
    {
        var binding = new ScriptedTextBinding("xy");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        // 组合前选区覆盖整个 "xy"，方向为 2→0。
        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        Assert.Equal(0, State(node).Caret);
        Assert.Equal(2, State(node).Anchor);

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 2), "n", new TextSelection(2, 0))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你", default, "h")), TextInputOutcome.ResultCommitted);
        Assert.Equal("你", binding.Text);
        Assert.Equal(1, Editor(node).Version);

        AssertApplied(service.Apply(CompositionEvent.Cancel(Session, 0, 1, 2)), TextInputOutcome.Cancelled);

        // 只取消未提交段：已确认结果保留，版本不新增，组合前选区（含方向）被恢复。
        Assert.Equal("你", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(1, Editor(node).Version);
        Assert.False(Client(node).IsComposing);
        Assert.Equal(1, State(node).Caret);
        Assert.Equal(1, State(node).Anchor);
    }

    // ---------------------------------------------------------------- R2：无 Start 的结果

    [Fact]
    public void NoStartFutureOrStaleVersionIsRejectedWithoutWrite()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        Assert.True(service.Apply(CompositionEvent.Result(Session, 0, 99, 0, "BAD", new TextRange(0, 1))).HasRejection);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);

        // 先制造一个真实版本推进，再发送已过时的 v0 结果。
        app.Key(UiKey.End);
        app.Runtime.DispatchTextInput(new TextInputEvent("Z"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal("abZ", binding.Text);
        Assert.Equal(1, Editor(node).Version);
        Assert.True(service.Apply(CompositionEvent.Result(Session, 0, 0, 0, "BAD", new TextRange(0, 1))).HasRejection);
        Assert.Equal("abZ", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
    }

    [Fact]
    public void NoStartResultWithNextPreeditContinuesOnSameComposition()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        AssertApplied(
            client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 0, "X", new TextRange(0, 1), "h")]),
            TextInputOutcome.ResultCommitted);

        Assert.Equal("Xb", binding.Text);
        Assert.True(client.IsComposing);
        Assert.Equal("h", client.Preedit);
        Assert.Equal("Xhb", client.DisplayString);
        Assert.Equal(new TextRange(1, 1), client.Composition.Active!.Value.Replacement);

        AssertApplied(client.ApplyComposition([CompositionEvent.Result(epoch, 0, client.Document.Version, 1, "i")]), TextInputOutcome.ResultCommitted);
        Assert.Equal("Xib", binding.Text);
        Assert.Equal(2, binding.SetterCalls);
    }

    [Fact]
    public void NoStartResultUsesExplicitReplacementNotCurrentCaret()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        // 显式替换区 [1,2)（"b" → "X"），当前 caret 在 0：必须按显式范围得到 "aX"。
        AssertApplied(client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 0, "X", new TextRange(1, 2))]), TextInputOutcome.ResultCommitted);
        Assert.Equal("aX", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
    }

    [Fact]
    public void NoStartFutureVersionIsRejectedAcrossTwoFields()
    {
        var first = new ScriptedTextBinding("ab");
        var second = new ScriptedTextBinding("cd");
        var service = new FakeTextInputService();
        var host = new TwoFieldHost(first.Binding, second.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.GetFocusOrder()[0];
        var secondNode = app.Runtime.GetFocusOrder()[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        var firstClient = Client(firstNode);

        Assert.True(firstClient.ApplyComposition([CompositionEvent.Result(firstClient.Session.SessionId, 0, 42, 0, "BAD", new TextRange(0, 1))]).HasRejection);
        Assert.Equal("ab", first.Text);
        Assert.Equal(0, first.SetterCalls);

        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();
        var secondClient = Client(secondNode);
        Assert.NotEqual(firstClient.Session.SessionId, secondClient.Session.SessionId);

        Assert.True(firstClient.ApplyComposition([CompositionEvent.Result(firstClient.Session.SessionId, 0, 42, 0, "BAD")]).HasRejection);
        Assert.Equal("ab", first.Text);
        Assert.Equal("cd", second.Text);
        Assert.Equal(0, second.SetterCalls);
    }

    // ---------------------------------------------------------------- R3：身份/次序

    [Fact]
    public void CancelValidatesCompositionIdentityAndCannotCancelNewerSegment()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "old")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Cancel(Session, 0, 0, 1)), TextInputOutcome.Cancelled);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 1, 0, 0, new TextRange(0, 0), "new")), TextInputOutcome.Started);

        // 旧组合的取消不得影响新的活动段。
        var stale = service.Apply(CompositionEvent.Cancel(Session, 0, 0, 2));
        Assert.False(stale.HasRejection); // 同一已结束组合的重复取消：幂等消费
        Assert.Equal("new", Client(node).Preedit);
        Assert.True(Client(node).IsComposing);

        // 错误 ID 的取消被拒绝，也不影响新组合。
        Assert.True(service.Apply(CompositionEvent.Cancel(Session, 7, 0, 2)).HasRejection);
        Assert.Equal("new", Client(node).Preedit);

        // 活动组合的取消必须校验序号：未递增被拒绝。
        Assert.True(service.Apply(CompositionEvent.Cancel(Session, 1, 0, 0)).HasRejection);
        Assert.True(Client(node).IsComposing);
    }

    [Fact]
    public void WrongEpochEventIsRejectedEvenOnAValidClient()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        // 旧/错误 epoch：即使客户端本身仍是有效归属，也不得写入。
        Assert.True(client.ApplyComposition([CompositionEvent.Start(epoch + 7, 0, 0, 0, new TextRange(0, 0), "n")]).HasRejection);
        Assert.False(client.IsComposing);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void InvalidStartDoesNotConsumeCompositionId()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 5, 0, new TextRange(0, 0), "n")).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 9), "n")).HasRejection);
        Assert.False(Client(node).IsComposing);

        // 无效 Start 不得消耗 ID 0：随后合法的 ID 0 必须被接受。
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "n")), TextInputOutcome.Started);
        Assert.Equal("n", Client(node).Preedit);
    }

    [Fact]
    public void CompositionIdsMayJumpButReplaysStayRejected()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        // 允许跳号（严格递增即可），并制造远多于 32 个已结束组合。
        var committed = 0;
        for (var i = 2; i < 100; i += 3)
        {
            AssertApplied(service.Apply(CompositionEvent.Start(Session, i, committed, 0, new TextRange(committed, committed), "x")), TextInputOutcome.Started);
            AssertApplied(service.Apply(CompositionEvent.Result(Session, i, committed, 1, "x")), TextInputOutcome.ResultCommitted);
            committed++;
        }

        Assert.Equal(33, binding.SetterCalls);
        Assert.Equal(33, Editor(node).Version);
        Assert.Equal(33, binding.Text.Length);

        // 任何低于已见水位的旧 ID 都拒绝（不依赖“最近 16 个”集合）。
        Assert.True(service.Apply(CompositionEvent.Start(Session, 2, 33, 0, new TextRange(0, 0), "y")).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 96, 33, 0, new TextRange(0, 0), "y")).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Result(Session, 5, 33, 0, "y")).HasRejection);
        Assert.Equal(33, binding.SetterCalls);
    }

    [Fact]
    public void EndValidatesIdentitySequenceAndIsIdempotentOnlyForTheEndedSegment()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "n")), TextInputOutcome.Started);

        Assert.True(service.Apply(CompositionEvent.End(Session, 1, 0, 1)).HasRejection);
        Assert.True(service.Apply(CompositionEvent.End(Session, 0, 0, 0)).HasRejection);
        Assert.True(Client(node).IsComposing);

        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你")), TextInputOutcome.ResultCommitted);

        // 结果已经结束该段：后续 End/Cancel 幂等消费，不写入、不撤销已确认结果。
        AssertApplied(service.Apply(CompositionEvent.End(Session, 0, 1, 2)), TextInputOutcome.None);
        AssertApplied(service.Apply(CompositionEvent.Cancel(Session, 0, 1, 3)), TextInputOutcome.None);
        Assert.Equal("你", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(1, Editor(node).Version);

        // 其他已结束 ID 既不是活动段也不是最近结束段：拒绝。
        Assert.True(service.Apply(CompositionEvent.End(Session, 5, 1, 0)).HasRejection);
    }

    // ---------------------------------------------------------------- 结果事务

    [Fact]
    public void ResultCommitsExactlyOnceThroughRealTransaction()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "ni")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Update(Session, 0, 0, 1, default, "nih")), TextInputOutcome.Updated);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 2, "你")), TextInputOutcome.ResultCommitted);

        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal("你", binding.Text);
        Assert.Equal(1, Editor(node).Version);
        Assert.Equal(1, Client(node).CommitCount);
        Assert.False(Client(node).IsComposing);
    }

    [Fact]
    public void EmptyOrScrubbedResultCannotDeleteSelectionAndIsConsumedOnce()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        Assert.True(State(node).HasSelection);

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 1), "n", new TextSelection(1, 0))), TextInputOutcome.Started);

        var empty = service.Apply(CompositionEvent.Result(Session, 0, 0, 1, string.Empty));
        Assert.False(empty.HasRejection);
        Assert.False(empty.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal(1, empty.AppliedEventCount);
        Assert.Equal("abcd", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.True(State(node).HasSelection, "空结果不得删除原选区");

        // 净化后为空：同样不能删除原选区。
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 1, 0, 0, new TextRange(0, 1), "n", new TextSelection(1, 0))), TextInputOutcome.Started);
        var scrubbed = service.Apply(CompositionEvent.Result(Session, 1, 0, 1, "\n"));
        Assert.False(scrubbed.HasRejection);
        Assert.False(scrubbed.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("abcd", binding.Text);
        Assert.True(State(node).HasSelection);

        // 同一事件重放不得再次调用 setter。
        Assert.True(service.Apply(CompositionEvent.Result(Session, 1, 0, 1, "\n")).HasRejection);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void FullyRejectedResultKeepsSegmentAndNeverClaimsCommit()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding, maxLength: 2);
        using var _app = app;

        app.Key(UiKey.End);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        var rejected = service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "X", default, "h"));
        Assert.False(rejected.HasRejection);
        Assert.False(rejected.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, Editor(node).Version);
        Assert.True(Client(node).IsComposing);
        Assert.Equal("h", Client(node).Preedit);

        // 序号已退休：同一事件不会再次尝试写入；后续更大序号仍可更新预编辑。
        Assert.True(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "X")).HasRejection);
        AssertApplied(service.Apply(CompositionEvent.Update(Session, 0, 0, 2, default, "ha")), TextInputOutcome.Updated);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void MaxLengthPartiallyAcceptsWholeGraphemesInOneWrite()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding, maxLength: 3);
        using var _app = app;

        // 文档已用满 3 个码元中的 2 个：文档 "ab"，MaxLength 3，空替换区只剩 1 个码元容量，
        // 结果 "你好" 按完整字素只接受 "你"。
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你好")), TextInputOutcome.ResultCommitted);

        Assert.Equal("ab你", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(1, Editor(node).Version);
    }

    [Fact]
    public void NoOpEmptyResultAtEmptyRangeDoesNotClaimCommit()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0))), TextInputOutcome.Started);
        var result = service.Apply(CompositionEvent.Result(Session, 0, 0, 1, string.Empty));

        Assert.False(result.HasRejection);
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
    }

    [Fact]
    public void PartialBatchReportsAcceptedPrefixAndHandled()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        AssertApplied(client.ApplyComposition([CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "n")]), TextInputOutcome.Started);

        var batch = new[]
        {
            CompositionEvent.Result(Session, 0, 0, 1, "你", default, "h"),
            CompositionEvent.Update(Session, 0, 99, 2, default, "ha"),
        };

        var result = client.ApplyComposition(batch);

        // 已处理前缀：真实动作 + 处理数；Handled 必须为 true，否则宿主会重复插入字符。
        Assert.True(result.HasRejection);
        Assert.True(result.Handled);
        Assert.Equal(1, result.AppliedEventCount);
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal("你", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal("h", Client(node).Preedit);
    }

    [Fact]
    public void RejectedFirstEventMeansNothingHandled()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var result = service.Apply(
            CompositionEvent.Update(Session, 0, 0, 0, default, "n"),
            CompositionEvent.Start(Session, 0, 0, 1, new TextRange(0, 0), "n"));

        Assert.True(result.HasRejection);
        Assert.False(result.Handled);
        Assert.Equal(0, result.AppliedEventCount);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void SetterExceptionIsContainedAndActualDocumentIsRestored()
    {
        var binding = new ScriptedTextBinding("ab") { ThrowAfterMutation = true, Normalize = _ => "abZ" };
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        var result = service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "Z"));

        // 原生回调路径不抛异常；真实回读已更新会话，但不谎报 ResultCommitted。
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal("abZ", binding.Text);
        Assert.Equal("abZ", Editor(node).Text);
        Assert.True(Editor(node).Version >= 1);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("组合事件应用失败", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 载荷/容量

    [Fact]
    public void PayloadValidationRejectsBrokenSurrogatesSegmentsAndRanges()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "\uD83D")).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(1, 5), "n")).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "ni", null, null, [new TextRange(0, 3)])).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "ni", null, null, [new TextRange(1, 2), new TextRange(1, 2)])).HasRejection);
        Assert.True(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "ni", null, 5)).HasRejection);

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);

        var emojiBinding = new ScriptedTextBinding("a\U0001F600b");
        var (app2, _, node2) = CreateField(new FakeTextInputService(), emojiBinding.Binding);
        using var _app2 = app2;
        Assert.True(Client(node2).ApplyComposition([CompositionEvent.Start(Session, 0, 0, 0, new TextRange(1, 2), "x")]).HasRejection);
        Assert.Equal(0, emojiBinding.SetterCalls);
    }

    [Fact]
    public void ZeroCapacityResultIsRejectedWithoutLosingSelection()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding, maxLength: 2);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        Assert.True(State(node).HasSelection);

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 1), "n", new TextSelection(1, 0))), TextInputOutcome.Started);
        var result = service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "\U0001F600"));

        Assert.False(result.HasRejection);
        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, Editor(node).Version);
        Assert.True(State(node).HasSelection);
    }

    // ---------------------------------------------------------------- R5：焦点/生命周期

    [Fact]
    public void FocusSwitchReleasesOldClientAndRefocusCreatesFreshClient()
    {
        var first = new ScriptedTextBinding("ab");
        var second = new ScriptedTextBinding("cd");
        var service = new FakeTextInputService();
        var host = new TwoFieldHost(first.Binding, second.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.GetFocusOrder()[0];
        var secondNode = app.Runtime.GetFocusOrder()[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        var firstClient = Client(firstNode);
        var firstEpoch = firstClient.Session.SessionId;

        AssertApplied(service.Apply(CompositionEvent.Start(firstEpoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        var delivered = new List<TextInputApplyResult>();
        service.OnEndSession = () =>
        {
            // 旧客户端在服务结束回调里同步投递结果：必须被拒绝，不能写入新客户端。
            delivered.Add(firstClient.ApplyComposition([CompositionEvent.Result(firstEpoch, 0, 0, 1, "X")]));
        };

        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();

        Assert.False(firstClient.IsComposing);
        Assert.True(firstClient.IsReleased);
        Assert.Equal(0, first.SetterCalls);
        Assert.Single(delivered);
        Assert.True(delivered[0].HasRejection);

        // 旧客户端被平台保留时只报告自己的旧 epoch，不会动态指向新会话。
        Assert.Equal(firstEpoch, firstClient.Session.SessionId);

        // 重新聚焦第一个字段：必须创建新客户端/新 epoch，并能正常组合。
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        var refocused = Client(firstNode);
        Assert.NotSame(firstClient, refocused);
        Assert.NotEqual(firstEpoch, refocused.Session.SessionId);
        Assert.False(refocused.IsReleased);

        AssertApplied(service.Apply(CompositionEvent.Start(refocused.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(refocused.Session.SessionId, 0, 0, 1, "Z")), TextInputOutcome.ResultCommitted);
        Assert.Equal("abZ", first.Text);
    }

    [Fact]
    public void SameNodeSameBindingRebuildAndAdaptiveRelayoutPreserveComposition()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var host = new RebuildHost(binding.Binding, FieldLayout.Adaptive);
        using var app = new TestApp(host, new Size(400, 220), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 同节点、同绑定的描述重建 + 布局重排：保留组合与客户端。
        host.Tick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Runtime.SetViewport(new Size(200, 220), 1.0);
        app.Runtime.ProcessPendingWork();

        Assert.Same(node, app.Runtime.Require<TextField>());
        Assert.Same(client, Client(node));
        Assert.True(client.IsComposing);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void ResponsiveUnmountClearsCompositionButAdaptiveRelayoutKeepsIt()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var host = new ResponsiveFieldHost(binding.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        app.Runtime.SetViewport(new Size(200, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        Assert.True(Client(node).IsComposing);

        // 真正卸载：Responsive 换成静态文本。
        host.ShowField!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.Null(app.Runtime.Root.FindDescendant<TextField>());
        Assert.False(client.IsComposing);
        Assert.True(client.IsReleased);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Null(service.ActiveClient);
    }

    [Fact]
    public void UnmountOfOldNodeDoesNotEndNewFieldsSession()
    {
        var first = new ScriptedTextBinding("ab");
        var second = new ScriptedTextBinding("cd");
        var service = new FakeTextInputService();
        var host = new TwoFieldHost(first.Binding, second.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.GetFocusOrder()[0];
        var secondNode = app.Runtime.GetFocusOrder()[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();

        var secondClient = Client(secondNode);
        Assert.Same(secondClient, service.ActiveClient);

        // 旧节点（第一个字段）现在才被卸载：不得结束第二个字段的会话。
        host.HideFirst!.Value = true;
        app.Runtime.ProcessPendingWork();

        Assert.False(firstNode.IsMounted);
        Assert.Same(secondClient, service.ActiveClient);
        Assert.False(secondClient.IsReleased);
        AssertApplied(service.Apply(CompositionEvent.Start(secondClient.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
    }

    [Fact]
    public void ServiceReentrantSessionCallbacksDoNotClobberNewerSession()
    {
        var first = new ScriptedTextBinding("ab");
        var second = new ScriptedTextBinding("cd");
        var service = new FakeTextInputService();
        var host = new TwoFieldHost(first.Binding, second.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.GetFocusOrder()[0];
        var secondNode = app.Runtime.GetFocusOrder()[1];
        var reentered = false;

        // 服务在 StartSession 内同步把焦点切到第二个字段（等于结束外层会话并新建一个）。
        service.OnStartSession = _ =>
        {
            if (!reentered)
            {
                reentered = true;
                app.Runtime.SetFocus(secondNode);
            }
        };

        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();

        Assert.True(reentered);
        Assert.Same(Client(secondNode), service.ActiveClient);
        Assert.False(Client(secondNode).IsReleased);

        AssertApplied(service.Apply(CompositionEvent.Start(Client(secondNode).Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        Assert.Equal("cd", second.Text);
    }

    [Fact]
    public void ServiceStartFailureIsContainedWithCommittedFallback()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService { ThrowOnStartSession = true };
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        Assert.True(State(node).Composing is null || State(node).Composing!.IsReleased);
        Assert.Null(service.ActiveClient);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("StartSession", StringComparison.Ordinal));

        // 已提交文本回退路径不受影响。
        app.Key(UiKey.End);
        app.Runtime.DispatchTextInput(new TextInputEvent("X"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal("abX", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
    }

    [Fact]
    public void DisableCancelsCompositionAndEndsSession()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var host = new DisableableFieldHost(binding.Binding);
        using var app = new TestApp(host, new Size(400, 120), textInputService: service);
        app.Frame();
        var node = app.Runtime.GetFocusOrder()[0];
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        host.Disabled!.Value = true;
        app.Runtime.ProcessPendingWork();

        Assert.False(client.IsComposing);
        Assert.True(client.IsReleased);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
        Assert.Null(service.ActiveClient);
    }

    [Fact]
    public void UnmountAndDisposeCancelCompositionCleanly()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var host = new DisableableFieldHost(binding.Binding);
        var app = new TestApp(host, new Size(400, 120), textInputService: service);
        app.Frame();
        var node = app.Runtime.GetFocusOrder()[0];
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        host.ShowField!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.False(node.IsMounted);
        Assert.False(client.IsComposing);
        Assert.True(client.IsReleased);
        Assert.Null(node.InteractionState);
        Assert.Equal(0, binding.SetterCalls);
        Assert.True(service.EndCallCount >= 1);

        app.Dispose();
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void WindowDeactivationCancelsAndReactivationResumesWithFreshEpoch()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        app.Runtime.NotifyWindowActivation(false);

        Assert.False(client.IsComposing);
        Assert.True(client.IsReleased);
        Assert.Equal(0, binding.SetterCalls);
        Assert.True(client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 1, "X")]).HasRejection);

        app.Runtime.NotifyWindowActivation(true);
        app.Runtime.ProcessPendingWork();

        // 重新激活后焦点仍在：用新 epoch 恢复会话，旧引用仍是旧 epoch。
        var resumed = Client(node);
        Assert.NotSame(client, resumed);
        Assert.NotEqual(epoch, resumed.Session.SessionId);
        Assert.Equal(epoch, client.Session.SessionId);

        AssertApplied(service.Apply(CompositionEvent.Start(resumed.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(resumed.Session.SessionId, 0, 0, 1, "Z")), TextInputOutcome.ResultCommitted);
        Assert.Equal("abZ", binding.Text);
    }

    // ---------------------------------------------------------------- R6：事件前同步

    [Fact]
    public void ExternalBindingChangeBeforeResultCancelsAndReplacesEpoch()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 外部写入发生在结果事件之前：组合必须先失效，结果绝不按旧锚点写入。
        binding.Text = "EXT";
        var result = client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 1, "X")]);

        Assert.False(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.Equal("EXT", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.False(client.IsComposing);

        // 安全阶段换新 epoch。
        app.Runtime.ProcessPendingWork();
        var refreshed = Client(node);
        Assert.NotSame(client, refreshed);
        Assert.NotEqual(epoch, refreshed.Session.SessionId);
        Assert.False(refreshed.IsReleased);
    }

    [Fact]
    public void CompositionCommitPreservesHistoryInvalidation()
    {
        var binding = new ScriptedTextBinding(string.Empty) { Normalize = _ => "你X" };
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 0), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你")), TextInputOutcome.ResultCommitted);

        // 规范化提交会标记历史失效；组合路径不得把该事实一起清掉。
        Assert.True(Editor(node).HistoryInvalidationRequested);
    }

    // ---------------------------------------------------------------- 取消接缝

    [Fact]
    public void ComposingTextInputIsSwallowedWithoutCommittedWrite()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        app.Runtime.DispatchTextInput(new TextInputEvent("x"));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
        Assert.False(Client(node).IsComposing);
    }

    [Fact]
    public void MouseReselectionCancelsComposition()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 2), "n", new TextSelection(2, 0))), TextInputOutcome.Started);

        app.Click(new Point(node.Frame.X + 5, node.Frame.Y + 5));
        app.Runtime.ProcessPendingWork();

        Assert.False(Client(node).IsComposing);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("abcd", binding.Text);

        // 宿主取消会延迟到安全阶段通知平台撤销旧预编辑。
        Assert.True(service.CancelCount >= 1);
    }

    [Fact]
    public void KeyboardDuringCompositionIsOwnedByInputMethodAndEndKeyIsConsumedOnce()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding, withSecondField: true);
        using var _app = app;

        app.Key(UiKey.End);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        var caretBefore = State(node).Caret;

        foreach (var key in new[] { UiKey.Left, UiKey.Backspace, UiKey.Delete, UiKey.Space, UiKey.Enter, UiKey.Escape, UiKey.Tab })
        {
            app.Key(key);
        }

        app.Runtime.ProcessPendingWork();
        Assert.Equal(caretBefore, State(node).Caret);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.True(Client(node).IsComposing);
        Assert.Same(node, app.Runtime.FocusedNode);

        // 输入法在同一原生按键内结束组合并声明已消费该 Tab：KeyDown 不得触发焦点遍历。
        var token = State(node).NativeKeyToken;
        AssertApplied(service.Apply(CompositionEvent.Result(Session, 0, 0, 1, "你")), TextInputOutcome.ResultCommitted);
        Assert.True(app.Runtime.NotifyCompositionKeyOrigin(token, consumedByInputMethod: true));

        var focusedBefore = app.Runtime.FocusedNode;
        app.Key(UiKey.Tab);
        app.Runtime.ProcessPendingWork();
        Assert.Same(focusedBefore, app.Runtime.FocusedNode);

        // 归属是一次性的：随后同 token 的 Tab 正常遍历。
        app.Key(UiKey.Tab);
        app.Runtime.ProcessPendingWork();
        Assert.NotSame(focusedBefore, app.Runtime.FocusedNode);
    }

    [Fact]
    public void CopyReadsCommittedSelectionOnlyWhileComposing()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(0, 4), "XY", new TextSelection(4, 0))), TextInputOutcome.Started);

        app.Key(UiKey.C, KeyModifiers.Control);

        Assert.Equal("abcd", app.Clipboard.GetText());
        Assert.True(Client(node).IsComposing);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void EditCommandsCancelCompositionBeforeApplying()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        app.Clipboard.SetText("Z");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.False(Client(node).IsComposing);
        Assert.Equal("abZ", binding.Text);
        Assert.Equal(1, binding.SetterCalls);

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 1, 1, 0, new TextRange(3, 3), "n", TextSelection.Caret(3))), TextInputOutcome.Started);
        app.Key(UiKey.A, KeyModifiers.Control);
        Assert.False(Client(node).IsComposing);
        Assert.True(State(node).HasSelection);

        app.Key(UiKey.X, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(2, binding.SetterCalls);
    }

    [Fact]
    public void MaxLengthChangeDuringCompositionCancelsWithoutWrite()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, host, node) = CreateField(service, binding.Binding, maxLength: 5, dynamicMaxLength: true);
        using var _app = app;

        AssertApplied(service.Apply(CompositionEvent.Start(Session, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);
        host.Max!.Value = 1;
        app.Runtime.ProcessPendingWork();

        Assert.False(Client(node).IsComposing);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
    }

    // ---------------------------------------------------------------- 独立运行时复审（实际 TextField + 注入服务）

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureVersionCancelOrEndCannotClearValidPreedit(bool end)
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, default, "n")), TextInputOutcome.Started);

        // 未来版本不能结束有效组合：状态、预编辑与绑定都保持不变。
        Assert.True(service.Apply(end
            ? CompositionEvent.End(epoch, 0, 999, 1)
            : CompositionEvent.Cancel(epoch, 0, 999, 1)).HasRejection);
        Assert.True(client.IsComposing);
        Assert.Equal("n", client.Preedit);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);

        // 正确版本仍可结束，且已结束身份之后保持幂等。
        AssertApplied(service.Apply(end
            ? CompositionEvent.End(epoch, 0, 0, 1)
            : CompositionEvent.Cancel(epoch, 0, 0, 1)), end ? TextInputOutcome.Ended : TextInputOutcome.Cancelled);
        Assert.False(client.IsComposing);
        AssertApplied(service.Apply(end
            ? CompositionEvent.End(epoch, 0, 0, 2)
            : CompositionEvent.Cancel(epoch, 0, 0, 2)), TextInputOutcome.None);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void NoStartEmptyResultWithNextPreeditContinuesAndPreservesExplicitRange()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        // 无 Start 的空结果 + 显式 replacement[0,2) + NextPreedit：建立有效下一段，不写绑定。
        AssertApplied(
            client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 0, string.Empty, new TextRange(0, 2), "n")]),
            TextInputOutcome.Updated);
        Assert.True(client.IsComposing);
        Assert.Equal("n", client.Preedit);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(new TextRange(0, 2), client.Composition.Active!.Value.Replacement);

        // 同一组合 ID 的下一结果替换保留的原始范围，而不是按最新 caret 猜测。
        AssertApplied(
            client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 1, "X")]),
            TextInputOutcome.ResultCommitted);
        Assert.Equal("X", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.False(client.IsComposing);

        // 已退休的身份不能再次写入。
        Assert.True(client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 2, "BAD", new TextRange(0, 1))]).HasRejection);
        Assert.Equal("X", binding.Text);
    }

    [Fact]
    public void ThrowingBindingGetterReturnsRejectionWithoutEscaping()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        binding.ThrowGet = true;
        try
        {
            // 绑定 getter 持续抛错：不得从 catch 里再次读取，必须返回显式拒绝。
            var result = client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 0, "X", new TextRange(0, 1))]);
            Assert.True(result.HasRejection);
            Assert.Equal(0, binding.SetterCalls);
        }
        finally
        {
            binding.ThrowGet = false;
        }

        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void PartialPrefixSurvivesLaterSetterFailureWithRealDocument()
    {
        var binding = new ScriptedTextBinding("ab") { ThrowOnSetterCall = 2 };
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        var result = client.ApplyComposition(
        [
            CompositionEvent.Result(epoch, 0, 0, 1, "X", default, "h"),
            CompositionEvent.Result(epoch, 0, 1, 2, "Y"),
        ]);

        // 第一段真实提交，第二段 setter 抛错：前缀动作/处理数如实保留，返回文档是真实文档。
        Assert.True(result.Handled);
        Assert.Equal(1, result.AppliedEventCount);
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.Rejected));
        Assert.Equal(binding.Text, result.Document.Text);
        Assert.Equal("abXY", binding.Text);
    }

    [Fact]
    public void ExternalBindingObservationRevokesOldEpochImmediately()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        // 外部写入后先读取一次 Document（宿主可以拿到“最新”版本），旧 epoch 仍必须立即失效。
        binding.Text = "EXT";
        var observedVersion = client.Document.Version;

        var result = client.ApplyComposition(
            [CompositionEvent.Result(epoch, 10, observedVersion, 0, "BAD", new TextRange(0, 1))]);
        Assert.True(result.HasRejection);
        Assert.Equal("EXT", binding.Text);
        Assert.Equal(0, binding.SetterCalls);

        // 安全阶段换新 epoch；旧客户端仍是旧身份。
        app.Runtime.ProcessPendingWork();
        var refreshed = Client(node);
        Assert.NotSame(client, refreshed);
        Assert.NotEqual(epoch, refreshed.Session.SessionId);
    }

    [Fact]
    public void EndSessionSynchronousRefocusKeepsReentrantFocusAndServiceOwner()
    {
        var first = new ScriptedTextBinding("a");
        var second = new ScriptedTextBinding("b");
        var service = new FakeTextInputService();
        using var app = new TestApp(
            new VStack(new TextField(first.Binding) { Width = 120 }, new TextField(second.Binding) { Width = 120 }),
            new Size(400, 120),
            textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.Root.Children[0];
        var secondNode = app.Runtime.Root.Children[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();

        // 切到第二字段时，服务在 EndSession 内同步把焦点切回第一字段。
        service.OnEndSession = () =>
        {
            service.OnEndSession = null;
            app.Runtime.SetFocus(firstNode);
        };
        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();

        Assert.True(firstNode.IsFocused);
        Assert.False(secondNode.IsFocused);

        var current = service.ActiveClient;
        Assert.NotNull(current);
        var applied = current!.ApplyComposition(
            [CompositionEvent.Result(current.Session.SessionId, 0, current.Document.Version, 0, "X", new TextRange(0, 1))]);
        Assert.False(applied.HasRejection, applied.RejectionReason);
        Assert.Equal("X", first.Text);
        Assert.Equal("b", second.Text);
    }

    [Fact]
    public void StartSessionSynchronousRefocusKeepsReentrantFocusAndServiceOwner()
    {
        var first = new ScriptedTextBinding("a");
        var second = new ScriptedTextBinding("b");
        var service = new FakeTextInputService();
        using var app = new TestApp(
            new VStack(new TextField(first.Binding) { Width = 120 }, new TextField(second.Binding) { Width = 120 }),
            new Size(400, 120),
            textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.Root.Children[0];
        var secondNode = app.Runtime.Root.Children[1];
        service.OnStartSession = _ =>
        {
            service.OnStartSession = null;
            app.Runtime.SetFocus(secondNode);
        };
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();

        Assert.True(secondNode.IsFocused);
        Assert.False(firstNode.IsFocused);
        var current = service.ActiveClient;
        Assert.NotNull(current);
        var applied = current!.ApplyComposition(
            [CompositionEvent.Result(current.Session.SessionId, 0, current.Document.Version, 0, "X", new TextRange(0, 1))]);
        Assert.False(applied.HasRejection, applied.RejectionReason);
        Assert.Equal("a", first.Text);
        Assert.Equal("X", second.Text);
    }

    // -------------------------------------------- 取消→结束紧邻序列的同步重入（TI-07 P4 修复回归）

    [Fact]
    public void EndDeliversPendingNativeCancelAfterOwnershipSevered()
    {
        // 窗口失活/失焦紧邻序列：本地已取消 → 会话结束必须把登记的取消真正交给服务（否则 TIP 从未收到
        // TerminateComposition，Deactivate 后自行决定提交/放弃）；且必须在归属切断之后调用（plan:116）。
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 调用次序见证：RequestCancel 到达时归属必须已失效——替身在取消回调里同步投递旧 Result，
        // 该事件只对已撤销的旧客户端显式拒绝，不得写绑定。
        var deliveredLate = new List<TextInputApplyResult>();
        service.OnRequestCancel = () =>
        {
            deliveredLate.Add(service.ActiveClient!.ApplyComposition(
                [CompositionEvent.Result(epoch, 0, 0, 1, "X", new TextRange(2, 2))]));
        };

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(1, service.CancelCount);
        Assert.Single(deliveredLate);
        Assert.True(deliveredLate[0].HasRejection);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void RequestCancelSynchronousRefocusKeepsReentrantSessionAndServiceOwner()
    {
        // 服务在 RequestCancel 内同步把焦点切到第二字段（重入建立新会话）：
        // 外层结束不得再发旧会话的 EndSession/几何撤销覆盖新归属，新会话必须可写。
        var first = new ScriptedTextBinding("a");
        var second = new ScriptedTextBinding("b");
        var service = new FakeTextInputService();
        using var app = new TestApp(
            new VStack(new TextField(first.Binding) { Width = 120 }, new TextField(second.Binding) { Width = 120 }),
            new Size(400, 120),
            textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.Root.Children[0];
        var secondNode = app.Runtime.Root.Children[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();

        var firstClient = Client(firstNode);
        AssertApplied(service.Apply(CompositionEvent.Start(firstClient.Session.SessionId, 0, 0, 0, new TextRange(1, 1), "n", TextSelection.Caret(1))), TextInputOutcome.Started);

        // 失焦触发本地取消→EndCore→RequestCancel；服务在取消内同步重入切焦点到第二字段。
        service.OnRequestCancel = () =>
        {
            service.OnRequestCancel = null;
            app.Runtime.SetFocus(secondNode);
        };

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        Assert.True(secondNode.IsFocused);
        var current = service.ActiveClient;
        Assert.NotNull(current);
        Assert.Same(Client(secondNode), current);
        Assert.False(Client(secondNode).IsReleased);

        var applied = current.ApplyComposition(
            [CompositionEvent.Result(current.Session.SessionId, 0, current.Document.Version, 0, "X", new TextRange(0, 1))]);
        Assert.False(applied.HasRejection, applied.RejectionReason);
        Assert.Equal("a", first.Text);
        Assert.Equal("X", second.Text);
    }

    [Fact]
    public void RequestCancelSynchronousEndDoesNotRecurseOrClobber()
    {
        // 服务在 RequestCancel 内同步重入会话结束（等同失活→失焦紧邻序列里的重入 End）：
        // 归属已切断，重入 End 必须是幂等无会话（不递归、不抛错）；回调结果记录到外部断言，
        // 不在回调里 Assert（异常会被 ServiceFailure 收敛吞掉而失去失败信号）。
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        AssertApplied(service.Apply(CompositionEvent.Start(client.Session.SessionId, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        var reenteredEndResult = new List<bool>();
        service.OnRequestCancel = () =>
        {
            service.OnRequestCancel = null;
            // 归属已切断后重入按所有者结束：必须是幂等 false（owner 已释放，不递归也不误终）。
            reenteredEndResult.Add(app.Runtime.TextInputSession.EndForOwner(client));
        };

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        Assert.Single(reenteredEndResult);
        Assert.False(reenteredEndResult[0]);
        Assert.Equal(1, service.CancelCount);
        Assert.True(client.IsReleased);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void CancelThenEndStillRejectsLateResultAndKeepsCommittedText()
    {
        // 本地取消后归属结束：迟到的旧 Result 不得写绑定；已取消段零写，文本保持已提交版本。
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        var late = client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 1, "nihao", new TextRange(2, 2))]);
        Assert.True(late.HasRejection);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void ResultSnapshotMatchesActualDocumentSelectionAndVersion()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;

        // 无 Start、显式 replacement[1,2)：真实提交后 caret 在 2，返回快照必须与真实文档一致。
        var result = client.ApplyComposition([CompositionEvent.Result(epoch, 0, 0, 0, "X", new TextRange(1, 2))]);
        AssertApplied(result, TextInputOutcome.ResultCommitted);
        Assert.Equal("aX", binding.Text);

        var actual = client.Document;
        Assert.Equal(actual.Selection, result.Document.Selection);
        Assert.Equal(actual.Version, result.Document.Version);
        Assert.Equal(2, result.Document.Selection.Active);
        Assert.Equal(2, State(node).Caret);
    }

    [Fact]
    public void HostCancelSnapshotReflectsRestoredSelection()
    {
        var binding = new ScriptedTextBinding("abcd");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(0, 4), "n", new TextSelection(4, 0))), TextInputOutcome.Started);

        // 宿主取消：恢复组合前选区（含方向），返回快照与真实文档一致。
        Assert.True(client.CancelComposition());
        Assert.False(client.IsComposing);
        Assert.Equal(4, State(node).Anchor);
        Assert.Equal(0, State(node).Caret);
        Assert.Equal(new TextSelection(4, 0), client.Composition.LastDocument.Selection);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void PublicDispatchKeyCarriesExplicitOriginTokenOnce()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding, withSecondField: true);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 组合仍在进行时，宿主用自己的显式 token 声明该原生按键由输入法消费；随后同一按键内结束组合。
        var hostToken = new object();
        Assert.True(app.Runtime.NotifyCompositionKeyOrigin(hostToken, consumedByInputMethod: true));
        AssertApplied(service.Apply(CompositionEvent.Result(epoch, 0, 0, 1, "X")), TextInputOutcome.ResultCommitted);

        var focusedBefore = app.Runtime.FocusedNode;
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Tab, KeyModifiers.None), hostToken);
        Assert.Same(focusedBefore, app.Runtime.FocusedNode);

        // 归属一次性：随后一个不同的显式 token（或无 token 的基线分发）正常遍历焦点。
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Tab, KeyModifiers.None), new object());
        app.Runtime.ProcessPendingWork();
        Assert.NotSame(focusedBefore, app.Runtime.FocusedNode);
    }

    [Fact]
    public void InactiveWindowProgrammaticRefocusDoesNotStartSession()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(2, 2), "n", TextSelection.Caret(2))), TextInputOutcome.Started);

        // 窗口失活：本地组合取消、会话撤销。
        app.Runtime.NotifyWindowActivation(false);
        Assert.False(client.IsComposing);
        Assert.Null(service.ActiveClient);

        // 失活期间允许逻辑焦点更新，但不得启动文本输入会话。
        app.Runtime.SetFocus(null);
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        Assert.Same(node, app.Runtime.FocusedNode);
        Assert.Null(service.ActiveClient);

        // 重新激活后才建立新 epoch。
        app.Runtime.NotifyWindowActivation(true);
        app.Runtime.ProcessPendingWork();
        var resumed = Client(node);
        Assert.NotSame(client, resumed);
        Assert.NotEqual(epoch, resumed.Session.SessionId);
        Assert.Same(resumed, service.ActiveClient);
        AssertApplied(service.Apply(CompositionEvent.Start(resumed.Session.SessionId, 0, 0, 0, default, "n")), TextInputOutcome.Started);
    }

    [Fact]
    public void CompositionSuccessAndErrorPathsDoNotPerformTextLayout()
    {
        var guarded = new LayoutGuardedTextService();
        var service = new FakeTextInputService();
        var value = "ab";
        using var runtime = new UiRuntime(
            new TextField(new Binding<string>(() => value, v => value = v)),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = guarded,
                TextInputService = service,
            });
        runtime.SetViewport(new Size(400, 60), 1);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        runtime.SetFocus(runtime.Root);

        var client = (TextFieldCompositionClient)service.ActiveClient!;
        guarded.InCallback = true;
        var result = client.ApplyComposition(
            [CompositionEvent.Result(client.Session.SessionId, 0, 0, 0, "LONG", new TextRange(0, 2))]);
        guarded.InCallback = false;

        AssertApplied(result, TextInputOutcome.ResultCommitted);
        Assert.Equal("LONG", value);
        Assert.Equal(0, guarded.UnsafeLayouts);
    }

    [Fact]
    public void CompositionErrorRecoveryDoesNotPerformTextLayout()
    {
        var guarded = new LayoutGuardedTextService();
        var service = new FakeTextInputService();
        var value = "ab";
        var writes = 0;
        using var runtime = new UiRuntime(
            new TextField(new Binding<string>(() => value, v =>
            {
                value = v;
                if (++writes == 1)
                {
                    throw new InvalidOperationException("scripted setter failure after mutation");
                }
            })),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = guarded,
                TextInputService = service,
            });
        runtime.SetViewport(new Size(400, 60), 1);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        runtime.SetFocus(runtime.Root);

        var client = (TextFieldCompositionClient)service.ActiveClient!;
        guarded.InCallback = true;
        var result = client.ApplyComposition(
            [CompositionEvent.Result(client.Session.SessionId, 0, 0, 0, "LONG", new TextRange(0, 2))]);
        guarded.InCallback = false;

        Assert.True(result.HasRejection);
        Assert.Equal("LONG", value);
        Assert.Equal(value, result.Document.Text);
        Assert.Equal(0, guarded.UnsafeLayouts);
    }

    [Fact]
    public void BatchRereadsLiveVersionBeforeEachEventAndKeepsCommittedPrefix()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(service, binding.Binding);
        using var _app = app;

        var client = Client(node);
        var epoch = client.Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(epoch, 0, 0, 0, new TextRange(0, 2), "n")), TextInputOutcome.Started);

        // 第一事件提交后版本推进；第二事件仍锚定旧版本 v0，必须在执行前被重新校验拒绝。
        var result = client.ApplyComposition(
        [
            CompositionEvent.Result(epoch, 0, 0, 1, "X"),
            CompositionEvent.Start(epoch, 1, 0, 0, default, "bad"),
        ]);

        Assert.True(result.Handled);
        Assert.Equal(1, result.AppliedEventCount);
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted));
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.Rejected));
        Assert.Equal("X", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.False(Client(node).IsComposing);
    }

    [Fact]
    public void PendingNativeCancelIsNotDeliveredToNewOwnerComposition()
    {
        var first = new ScriptedTextBinding("a");
        var second = new ScriptedTextBinding("b");
        var service = new FakeTextInputService();
        using var app = new TestApp(
            new VStack(new TextField(first.Binding) { Width = 120 }, new TextField(second.Binding) { Width = 120 }),
            new Size(400, 120),
            textInputService: service);
        app.Frame();

        var firstNode = app.Runtime.Root.Children[0];
        var secondNode = app.Runtime.Root.Children[1];
        app.Runtime.SetFocus(firstNode);
        app.Runtime.ProcessPendingWork();
        var firstClient = Client(firstNode);
        AssertApplied(service.Apply(CompositionEvent.Start(firstClient.Session.SessionId, 0, 0, 0, default, "n")), TextInputOutcome.Started);

        // 本地取消排入延迟平台通知，随后焦点切到新字段：旧归属的取消在结束旧会话时
        // 必须真正交给服务（plan：失焦先取消未提交段）；不得延迟到新段、不得取消新组合。
        Assert.True(firstClient.CancelComposition());
        app.Runtime.SetFocus(secondNode);
        app.Runtime.ProcessPendingWork();
        var secondClient = Client(secondNode);
        AssertApplied(service.Apply(CompositionEvent.Start(secondClient.Session.SessionId, 0, 0, 0, default, "m")), TextInputOutcome.Started);

        app.Runtime.ProcessPendingWork();
        Assert.Equal(1, service.CancelCount);
        Assert.True(secondClient.IsComposing);
        Assert.Equal("m", secondClient.Preedit);
    }

    // ---------------------------------------------------------------- 辅助

    private enum FieldLayout
    {
        Plain,
        Flow,
        Adaptive,
    }

    private static (TestApp App, FieldHost Host, ViewNode Node) CreateField(
        FakeTextInputService service,
        Binding<string> binding,
        int maxLength = int.MaxValue,
        bool dynamicMaxLength = false,
        bool withSecondField = false)
    {
        var host = new FieldHost(binding, maxLength, dynamicMaxLength, withSecondField);
        var app = new TestApp(host, new Size(400, 160), textInputService: service);
        app.Frame();
        var node = app.Runtime.GetFocusOrder()[0];
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return (app, host, node);
    }

    private static TextFieldCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端（会话未建立）。");

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    private static TextEditSession Editor(ViewNode node) => State(node).Editor;

    private static void AssertApplied(TextInputApplyResult result, TextInputOutcome expected)
    {
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(expected), $"期望 {expected}，实际 {result.Outcome}（{result.RejectionReason}）。");
    }

    /// <summary>记录调用并可编排同步/延迟回调的文本输入服务替身。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities { get; set; } = TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public List<ITextInputClient> SeenClients { get; } = [];

        public int StartCount { get; private set; }

        public int EndCallCount { get; private set; }

        public int CancelCount { get; private set; }

        public bool ThrowOnStartSession { get; set; }

        public Action<ITextInputClient>? OnStartSession { get; set; }

        public Action? OnEndSession { get; set; }

        /// <summary>服务在 <see cref="RequestCancel"/> 内的同步回调（模拟原生终止同步触发焦点/生命周期动作）。</summary>
        public Action? OnRequestCancel { get; set; }
        public bool StartSession(ITextInputClient client)
        {
            ArgumentNullException.ThrowIfNull(client);
            StartCount++;
            SeenClients.Add(client);
            if (ThrowOnStartSession)
            {
                throw new InvalidOperationException("fake service start failure");
            }

            ActiveClient = client;
            OnStartSession?.Invoke(client);
            return true;
        }

        public bool EndSession()
        {
            EndCallCount++;
            if (ActiveClient is null)
            {
                return false;
            }

            ActiveClient = null;
            OnEndSession?.Invoke();
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        {
            var client = ActiveClient ?? throw new InvalidOperationException("fake service has no active client");
            return client.ApplyComposition(events);
        }

        public bool RequestCancel()
        {
            CancelCount++;
            OnRequestCancel?.Invoke();
            return ActiveClient?.CancelComposition() ?? false;
        }

        public bool PublishGeometry(TextInputGeometry geometry) => false;

        /// <summary>把一批组合事件投递给当前活动客户端。</summary>
        public TextInputApplyResult Apply(params CompositionEvent[] events)
        {
            var client = ActiveClient ?? throw new InvalidOperationException("fake service has no active client");
            return client.ApplyComposition(events);
        }
    }

    /// <summary>可编排规范化/抛错的字符串绑定；身份稳定，便于跨重建验证。</summary>
    private sealed class ScriptedTextBinding
    {
        public ScriptedTextBinding(string initial)
        {
            Text = initial;
            Binding = CSharp.UI.Binding<string>.From(
                () => ThrowGet ? throw new InvalidOperationException("scripted getter failure") : Text,
                Write);
        }

        public Binding<string> Binding { get; }

        public string Text { get; set; }

        public Func<string, string>? Normalize { get; set; }

        /// <summary>只在第一次写入时应用的规范化（模拟 setter 对首段结果的转换）。</summary>
        public Func<string, string>? NormalizeOnce { get; set; }

        /// <summary>存储成功后、事务回读前的回调；模拟 setter 内部同步完成的状态驱动重建。</summary>
        public Action? AfterStore { get; set; }

        public bool ThrowAfterMutation { get; set; }

        /// <summary>getter 持续抛错（模拟无法回读的绑定）；用于验证原生回调不越界。</summary>
        public bool ThrowGet { get; set; }

        /// <summary>第 N 次 setter 调用在写入后抛错（1 起算；-1 表示不按次数抛错）。</summary>
        public int ThrowOnSetterCall { get; set; } = -1;

        public int SetterCalls { get; private set; }

        private void Write(string value)
        {
            SetterCalls++;
            var normalizer = SetterCalls == 1 ? NormalizeOnce ?? Normalize : Normalize;
            Text = normalizer?.Invoke(value) ?? value;
            AfterStore?.Invoke();
            if (ThrowAfterMutation || SetterCalls == ThrowOnSetterCall)
            {
                throw new InvalidOperationException("scripted setter failure after mutation");
            }
        }
    }

    /// <summary>组合回调期间统计文本排版的守护服务：回调内任何 Layout 都计为不安全。</summary>
    private sealed class LayoutGuardedTextService : ITextService
    {
        private readonly StubTextService _inner = new();

        public bool InCallback { get; set; }

        public int UnsafeLayouts { get; private set; }

        public TextLayoutResult Layout(TextLayoutRequest request)
        {
            if (InCallback)
            {
                UnsafeLayouts++;
            }

            return _inner.Layout(request);
        }

        public Size Measure(TextLayoutRequest request) => Layout(request).Size;

        public FontMetrics GetFontMetrics(FontDescription font) => _inner.GetFontMetrics(font);

        public PixelBuffer Rasterize(TextLayoutResult layout) => _inner.Rasterize(layout);

        public int CachedLayoutCount => _inner.CachedLayoutCount;

        public int CachedRasterCount => _inner.CachedRasterCount;

        public int LayoutCacheCapacity
        {
            get => _inner.LayoutCacheCapacity;
            set => _inner.LayoutCacheCapacity = value;
        }

        public void ClearCaches() => _inner.ClearCaches();
    }

    /// <summary>承载一个或两个输入框的宿主；同节点重建与动态 MaxLength 都由状态驱动。</summary>
    private sealed class FieldHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly int _maxLength;
        private readonly bool _dynamicMaxLength;
        private readonly bool _second;

        public FieldHost(Binding<string> binding, int maxLength, bool dynamicMaxLength, bool second)
        {
            _binding = binding;
            _maxLength = maxLength;
            _dynamicMaxLength = dynamicMaxLength;
            _second = second;
        }

        public State<int>? Max { get; private set; }

        protected override View Build()
        {
            Max = UseState("max", _maxLength);
            var maxLength = _dynamicMaxLength ? Max.Value : _maxLength;
            var field = new TextField(_binding) { MaxLength = maxLength, Width = 120 };
            View content = _second
                ? new VStack(field, new TextField(Binding<string>.Constant(string.Empty)) { Width = 120 })
                : field;
            return new VStack(content);
        }
    }

    /// <summary>同节点、同绑定但强制描述重建的宿主。</summary>
    private sealed class RebuildHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly FieldLayout _layout;

        public RebuildHost(Binding<string> binding, FieldLayout layout)
        {
            _binding = binding;
            _layout = layout;
        }

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            View field = new TextField(_binding) { Width = 120 };
            field = _layout switch
            {
                FieldLayout.Flow => new Flow(field),
                FieldLayout.Adaptive => new AdaptiveStack(300, field),
                _ => field,
            };
            return new VStack(field);
        }
    }

    /// <summary>两个输入框的宿主（焦点切换/旧节点卸载验证）。</summary>
    private sealed class TwoFieldHost : Component
    {
        private readonly Binding<string> _first;
        private readonly Binding<string> _second;

        public TwoFieldHost(Binding<string> first, Binding<string> second)
        {
            _first = first;
            _second = second;
        }

        public State<bool>? HideFirst { get; private set; }

        protected override View Build()
        {
            HideFirst = UseState("hideFirst", false);
            return new VStack(
                HideFirst.Value ? new Text("first hidden") : new TextField(_first) { Width = 120 },
                new TextField(_second) { Width = 120 });
        }
    }

    /// <summary>可禁用/卸载的输入框宿主。</summary>
    private sealed class DisableableFieldHost : Component
    {
        private readonly Binding<string> _binding;

        public DisableableFieldHost(Binding<string> binding) => _binding = binding;

        public State<bool>? Disabled { get; private set; }

        public State<bool>? ShowField { get; private set; }

        protected override View Build()
        {
            Disabled = UseState("disabled", false);
            ShowField = UseState("show", true);
            View field = new TextField(_binding) { Width = 120 };
            if (Disabled.Value)
            {
                field = new DisabledView(field, true);
            }

            return new VStack(ShowField.Value ? field : new Text("no field"));
        }
    }

    /// <summary>窄屏重排用 AdaptiveStack，真正卸载时换静态文本的宿主。</summary>
    private sealed class ResponsiveFieldHost : Component
    {
        private readonly Binding<string> _binding;

        public ResponsiveFieldHost(Binding<string> binding) => _binding = binding;

        public State<bool>? ShowField { get; private set; }

        protected override View Build()
        {
            ShowField = UseState("show", true);
            if (!ShowField.Value)
            {
                return new Text("no field");
            }

            return new AdaptiveStack(300, new TextField(_binding) { Width = 120 });
        }
    }

    /// <summary>可在 setter 回调内同步切换绑定来源并强制重建的宿主。</summary>
    private sealed class SwitchableSourceHost : Component
    {
        public SwitchableSourceHost(Binding<string> binding) => Current = binding;

        public Binding<string> Current { get; set; }

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new VStack(new TextField(Current) { Width = 120 });
        }
    }
}
