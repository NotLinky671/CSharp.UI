using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-03：真实 TextField 运行时/输入路径上的统一原子编辑事务与 Binding 同步。
/// </summary>
/// <remarks>
/// 这些测试驱动稳定焦点节点上的真实 <see cref="TextField"/> 输入分发（文本事件 / 按键 / 剪贴板），
/// 断言绑定实际写入次数、文档版本、选区与滚动状态，而不是复制实现公式做辅助函数自证。
/// </remarks>
public class TextFieldAtomicEditTests
{
    private enum FieldLayout
    {
        Plain,
        Flow,
        Adaptive,
    }

    // ---------------------------------------------------------------- 单次事务与写入次数

    [Fact]
    public void Typing_CommitsOneTransactionPerEvent_AndSelectionMovesDoNotWrite()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Type(app, "a");
        Type(app, "b");
        Type(app, "c");

        Assert.Equal("abc", binding.Text);
        Assert.Equal(3, binding.SetterCalls);
        var editor = Editor(node);
        Assert.Equal(3, editor.Version);
        Assert.False(editor.HistoryInvalidationRequested);
        Assert.False(editor.CompositionInvalidationRequested);

        // 选区/光标移动不是文本事务：不写绑定、不推进版本。
        app.Key(UiKey.Left);
        app.Key(UiKey.Right);
        app.Key(UiKey.Home);
        app.Key(UiKey.End);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        app.Key(UiKey.A, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(3, binding.SetterCalls);
        Assert.Equal(3, editor.Version);
        Assert.Equal("abc", binding.Text);
    }

    [Fact]
    public void CutAndPaste_AreSingleTransactionsUsingTheClipboard()
    {
        var binding = new ScriptedTextBinding("hello");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.A, KeyModifiers.Control);
        app.Key(UiKey.C, KeyModifiers.Control);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("hello", app.Clipboard.GetText());

        app.Key(UiKey.X, KeyModifiers.Control);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(string.Empty, binding.Text);

        app.Key(UiKey.V, KeyModifiers.Control);
        Assert.Equal(2, binding.SetterCalls);
        Assert.Equal("hello", binding.Text);
        Assert.Equal(2, Editor(node).Version);
    }

    // ---------------------------------------------------------------- no-op / 整体拒绝

    [Fact]
    public void SameValueReplacement_IsNoOp_DoesNotCallSetterOrMoveSelection()
    {
        var binding = new ScriptedTextBinding("a");
        var (app, _, node) = CreateField(binding.Binding, maxLength: 1);
        using var _app = app;

        app.Key(UiKey.A, KeyModifiers.Control);
        var state = State(node);
        var caretBefore = state.Caret;
        var anchorBefore = state.Anchor;
        Assert.True(state.HasSelection);

        // 用同样的字符替换同样的选区：候选文本与当前文档一致，必须跳过 setter。
        Type(app, "a");

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal("a", binding.Text);
        Assert.Equal(caretBefore, state.Caret);
        Assert.Equal(anchorBefore, state.Anchor);
    }

    [Fact]
    public void ZeroCapacityTyping_IsRejected_WithoutSetterOrSelectionMove()
    {
        var binding = new ScriptedTextBinding("ab");
        var (app, _, node) = CreateField(binding.Binding, maxLength: 2);
        using var _app = app;

        Type(app, "X");

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, State(node).Caret);
    }

    [Fact]
    public void FullRejectedPaste_PreservesSelectionTextAndVersion()
    {
        var binding = new ScriptedTextBinding("abcd");
        var (app, _, node) = CreateField(binding.Binding, maxLength: 4);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);

        var state = State(node);
        Assert.True(state.HasSelection);

        // 容量 = 4 - (4 - 1) = 1，代理对需要 2 个 UTF-16 码元：整体拒绝。
        app.Clipboard.SetText("\U0001F600");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal("abcd", binding.Text);
        Assert.True(state.HasSelection);
        Assert.Equal(1, state.Caret);
        Assert.Equal(0, state.Anchor);
    }

    [Fact]
    public void EmptyClipboardPaste_DoesNotCallSetter()
    {
        var binding = new ScriptedTextBinding("ab");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Key(UiKey.V, KeyModifiers.Control);

        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal("ab", binding.Text);
    }

    // ---------------------------------------------------------------- Unicode 字素

    [Fact]
    public void SurrogateCombiningAndZwjClusters_AreDeletedAsWholeGraphemes()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        // 😀 代理对；é 组合附加符；👨‍👩‍👧 ZWJ 家庭序列。
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Type(app, "a\U0001F600e\u0301" + family);
        Assert.Equal("a\U0001F600e\u0301" + family, binding.Text);
        AssertWellFormed(binding.Text);

        var expected = new[] { "a\U0001F600e\u0301", "a\U0001F600", "a", string.Empty };
        foreach (var step in expected)
        {
            app.Key(UiKey.Backspace);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(step, binding.Text);
            AssertWellFormed(binding.Text);
        }
    }

    [Fact]
    public void MaxLength_TruncatesOnlyAtWholeGraphemeBoundaries()
    {
        var fits = new ScriptedTextBinding(string.Empty);
        var (appFits, _, _) = CreateField(fits.Binding, maxLength: 4);
        using var _fits = appFits;
        Type(appFits, "a\U0001F600z");
        Assert.Equal("a\U0001F600z", fits.Text);
        AssertWellFormed(fits.Text);

        var partial = new ScriptedTextBinding(string.Empty);
        var (appPartial, _, _) = CreateField(partial.Binding, maxLength: 3);
        using var _partial = appPartial;
        Type(appPartial, "a\U0001F600z");
        Assert.Equal("a\U0001F600", partial.Text);
        AssertWellFormed(partial.Text);

        // 容量 1 放不下 2 码元代理对：整体拒绝，绝不写半个代理项。
        var rejected = new ScriptedTextBinding(string.Empty);
        var (appRejected, _, _) = CreateField(rejected.Binding, maxLength: 1);
        using var _rejected = appRejected;
        Type(appRejected, "\U0001F600");
        Assert.Equal(string.Empty, rejected.Text);
        Assert.Equal(0, rejected.SetterCalls);
    }

    [Fact]
    public void MaxLength_ReplacingSelectionReleasesCapacity()
    {
        var binding = new ScriptedTextBinding("abcd");
        var (app, _, _) = CreateField(binding.Binding, maxLength: 4);
        using var _app = app;

        // 选中 "a"：容量 = 4 - (4 - 1) = 1，提交 "XYZ" 只插入能完整放下的 "X"。
        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        Type(app, "XYZ");

        Assert.Equal("Xbcd", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
    }

    [Fact]
    public void ReversedSelection_ReplacesAndDeletesTheSameRange()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        // 反向选择：从行尾 Shift+Home 得到 anchor=end、caret=0。
        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        var state = State(node);
        Assert.True(state.HasSelection);
        Assert.Equal(0, state.Caret);
        Assert.Equal(6, state.Anchor);

        // 替换整个反向选择。
        Type(app, "Z");
        Assert.Equal("Z", binding.Text);
        Assert.Equal(1, state.Caret);
        Assert.Equal(1, state.Anchor);

        // 再次反向选择后 Backspace：删除的是选区，而不是光标前的字符。
        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        app.Key(UiKey.Backspace);
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(0, state.Caret);
        Assert.Equal(0, state.Anchor);
    }

    // ---------------------------------------------------------------- setter 行为

    [Fact]
    public void SetterRejection_DoesNotMoveSelectionOrAdvanceVersion()
    {
        var binding = new ScriptedTextBinding("abc") { RejectWrites = true };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right);
        var state = State(node);

        Type(app, "X");

        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal("abc", binding.Text);
        Assert.Equal(0, Editor(node).Version);
        Assert.Equal(1, state.Caret);
        Assert.Equal(1, state.Anchor);
    }

    [Fact]
    public void SetterNormalization_IsAcceptedWithActualReadback_AndMarksHistoryInvalidation()
    {
        var binding = new ScriptedTextBinding(string.Empty) { Normalize = value => value.ToUpperInvariant() };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Type(app, "ab");

        // 回读值 "AB" 与旧文本不同：必须报告 Accepted 并推进版本（不能伪造“同版本却换了文档”）。
        Assert.Equal("AB", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        var editor = Editor(node);
        Assert.Equal(1, editor.Version);
        Assert.Equal("AB", editor.Text);

        // 实际同步结果与请求候选不同：不能为“请求的 abc 替换”记录逆操作，标记组合/历史失效。
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);

        // 光标按实际回读文本重新限制。
        Assert.Equal(2, State(node).Caret);
    }

    [Fact]
    public void SetterNormalization_ThatShrinksText_ClampsCaretToReadback()
    {
        var binding = new ScriptedTextBinding(string.Empty) { Normalize = value => value.Length > 1 ? value[..^1] : value };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Type(app, "abc");

        Assert.Equal("ab", binding.Text);
        var editor = Editor(node);
        Assert.Equal(1, editor.Version);
        Assert.Equal(2, State(node).Caret);
        Assert.True(editor.HistoryInvalidationRequested);
    }

    [Fact]
    public void SetterThrowBeforeMutation_LeavesTextCaretAndVersionUntouched_AndRethrows()
    {
        var binding = new ScriptedTextBinding("abc") { ThrowBeforeMutation = true };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        var state = State(node);
        Assert.Equal(3, state.Caret);

        Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));

        Assert.Equal("abc", binding.Text);
        var editor = Editor(node);
        Assert.Equal(0, editor.Version);
        Assert.Equal(3, state.Caret);
        Assert.Equal(3, state.Anchor);
        Assert.False(editor.RequiresResync);

        // setter 抛错的写入结果不可信：保守标记组合/历史失效（不伪造成功编辑）。
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);
    }

    [Fact]
    public void SetterThrowAfterMutation_ResyncsActualText_AndRethrowsTheSetterException()
    {
        var binding = new ScriptedTextBinding("abc") { ThrowAfterMutation = true };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        var state = State(node);
        Assert.Equal(0, state.Caret);

        var error = Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));
        Assert.Contains("after mutation", error.Message, StringComparison.Ordinal);

        // 外部副作用不回滚，但会话必须按真实回读值重同步，而不是假装事务成功。
        Assert.Equal("Xabc", binding.Text);
        var editor = Editor(node);
        Assert.Equal(1, editor.Version);
        Assert.Equal("Xabc", editor.Text);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);

        // 不得留下投机光标。
        Assert.Equal(0, state.Caret);

        // 下一次同值回读不能再清空历史/推进版本。
        editor.ConsumeInvalidation();
        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(1, editor.Version);
        Assert.False(editor.HistoryInvalidationRequested);
    }

    [Fact]
    public void GetterThrowBeforeAnyWrite_LeavesNoFalseSuccessfulState()
    {
        var binding = new ScriptedTextBinding("abc");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;
        binding.ThrowOnRead = true;

        Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));

        var editor = Editor(node);
        Assert.Equal("abc", binding.Text);
        Assert.Equal(0, editor.Version);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, State(node).Caret);
    }

    [Fact]
    public void GetterThrowAfterWrite_MarksResync_WithoutFakeSuccess_AndRecoversOnNextRead()
    {
        var binding = new ScriptedTextBinding("abc") { FailReadsAfterWrite = true };
        var (app, host, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        var state = State(node);

        Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));

        // setter 的外部副作用已发生，但没有可靠回读：不得推进版本，也不得仿造 accepted。
        Assert.Equal("Xabc", binding.Text);
        var editor = Editor(node);
        Assert.Equal(0, editor.Version);
        Assert.True(editor.RequiresResync);
        Assert.Equal(0, state.Caret);

        // 回读失败即写入结果不可信：组合/历史立即标记失效，不依赖后续编辑。
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);

        // 回读恢复后，下一次外部同步按实际文本重同步并推进版本。
        binding.ResetReadFailure();
        host.Tick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("Xabc", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.False(editor.RequiresResync);
        Assert.True(editor.HistoryInvalidationRequested);
    }

    // ---------------------------------------------------------------- 外部变化与自身确认

    [Fact]
    public void OwnStateDrivenRebuild_IsNotMistakenForExternalChange()
    {
        var host = new FieldHost("abc");
        using var app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);

        Type(app, "d");
        Assert.Equal("dabc", host.Text!.PeekValue());

        // 自身写入触发状态重建：不能重复推进版本，也不能标记历史失效。
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var editor = Editor(node);
        Assert.Equal(1, editor.Version);
        Assert.Equal("dabc", editor.Text);
        Assert.False(editor.HistoryInvalidationRequested);
    }

    [Fact]
    public void SynchronousRebuildInsideSetter_WhileTransactionPending_UsesPendingCandidate()
    {
        // 模拟更激进的宿主：setter 内部同步完成“状态写入 + 同节点重建 + OnUpdate”。
        // 事务尚未回读确认，OnUpdate 必须按进行中的候选识别为自己的写入，不能当成外部变化。
        ViewNode? node = null;
        var binding = new ScriptedTextBinding("abc");
        binding.AfterStore = () =>
        {
            var field = (TextField)node!.View;
            Reconciler.UpdateNode(node, new ChildDescriptor(field.With(binding.Binding), null, false));
        };

        var (app, _, created) = CreateField(binding.Binding);
        using var _app = app;
        node = created;
        var editor = Editor(node);

        Type(app, "X");

        Assert.Equal("Xabc", binding.Text);
        Assert.Equal(1, editor.Version);
        Assert.Equal("Xabc", editor.Text);
        Assert.False(editor.HistoryInvalidationRequested);
        Assert.False(editor.CompositionInvalidationRequested);
    }

    [Fact]
    public void ExternalBindingChange_ResyncsAndClampsSelection_ButSameValueRebuildDoesNotClearHistory()
    {
        var host = new FieldHost("abc");
        using var app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);
        var editor = Editor(node);

        Type(app, "d");
        app.Key(UiKey.End);
        Assert.Equal(1, editor.Version);

        // 外部写入：推进版本、标记失效并把选区限制到新文本。
        host.Text!.Value = "xy";
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(2, editor.Version);
        Assert.Equal("xy", editor.Text);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);
        Assert.Equal(2, State(node).Caret);

        // 同值重建不得每帧清空历史或再次推进版本。
        editor.ConsumeInvalidation();
        host.ExternalTick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(2, editor.Version);
        Assert.False(editor.HistoryInvalidationRequested);
        Assert.False(editor.CompositionInvalidationRequested);
    }

    [Fact]
    public void BindingSourceReplacement_IsTreatedConservatively()
    {
        var first = "source A";
        var second = "bb";
        var host = new SwitchableFieldHost { Current = Binding<string>.From(() => first, value => first = value) };
        using var app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);
        var editor = Editor(node);

        Type(app, "!");
        Assert.Equal(1, editor.Version);
        editor.ConsumeInvalidation();

        // 复用同一节点换用新来源：必须按“来源已更换”重同步并标记失效。
        host.Current = Binding<string>.From(() => second, value => second = value);
        host.Tick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(node, app.Runtime.Root.Require<TextField>());
        Assert.Equal("bb", ((TextField)node.View).Text.Value);
        Assert.Equal(2, editor.Version);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);
        Assert.True(State(node).Caret <= 2);
    }

    [Fact]
    public void MaxLengthChange_InvalidatesAndClampsSelection()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var host = new TextEditFieldHost(binding.Binding, maxLength: 10, dynamicMaxLength: true);
        using var app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);
        var editor = Editor(node);

        app.Key(UiKey.End);
        Assert.Equal(6, State(node).Caret);

        host.Max!.Value = 3;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // MaxLength 变化不是文本变化：版本/历史接缝推进，但已有文本与选区只按新文本限制（文本没变）。
        Assert.Equal(1, editor.Version);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.True(editor.CompositionInvalidationRequested);
        Assert.Equal(6, State(node).Caret);

        // 新上限立即生效：容量 3 - 6 <= 0，后续键入整体拒绝。
        Type(app, "X");
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("abcdef", binding.Text);
        Assert.Equal(1, editor.Version);
    }

    // ---------------------------------------------------------------- 布局重建保持状态

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutRebuild_PreservesEditStateAndContinuesTransactions(bool useFlow)
    {
        var layout = useFlow ? FieldLayout.Flow : FieldLayout.Adaptive;
        var binding = new ScriptedTextBinding("hello");
        var host = new TextEditFieldHost(binding.Binding, layout: layout);
        using var app = new TestApp(host, new Size(600, 120));
        app.Frame();
        var node = Focus(app);
        var state = State(node);
        var editor = Editor(node);

        app.Key(UiKey.End);
        app.Key(UiKey.Left, KeyModifiers.Shift);
        var caret = state.Caret;
        var anchor = state.Anchor;
        Assert.True(state.HasSelection);

        // Flow 通过组件重建换描述；Adaptive 通过视口切换换排列模式。两者都必须是同一节点。
        if (useFlow)
        {
            host.Tick!.Value++;
        }
        else
        {
            app.Runtime.SetViewport(new Size(200, 120), 1.0);
        }

        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(node, app.Runtime.Root.Require<TextField>());
        Assert.Same(state, node.InteractionState);
        Assert.Equal(caret, state.Caret);
        Assert.Equal(anchor, state.Anchor);
        Assert.Equal(0, editor.Version);
        Assert.False(editor.HistoryInvalidationRequested);

        // 布局变化后事务仍沿用同一已提交文档。
        Type(app, "X");
        Assert.Equal("hellX", binding.Text);
        Assert.Equal(1, editor.Version);
    }

    [Fact]
    public void LongTyping_KeepsCaretVisibleWithHorizontalScroll()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Type(app, new string('w', 80));

        var state = State(node);
        Assert.Equal(1, Editor(node).Version);
        Assert.Equal(80, state.Caret);
        Assert.True(state.ScrollX > 0, "长文本输入后必须出现横向滚动，使光标保持可见。");
    }

    // ---------------------------------------------------------------- 安全生命周期入口（R1）

    [Fact]
    public void ExternalValueChange_WithoutDescriptionRebuild_SyncsSessionAndSelectionOnLayout()
    {
        var binding = new ScriptedTextBinding("abcdefgh");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        var state = State(node);
        Assert.Equal(8, state.Caret);

        // 自定义绑定没有通知：外部改值不会产生新描述，OnUpdate 不会执行。
        binding.Text = "x";
        app.Runtime.InvalidateLayout(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var editor = Editor(node);
        Assert.Equal("x", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.Equal(1, state.Caret);
        Assert.Equal(1, state.Anchor);
    }

    [Fact]
    public void SetterThrowAfterMutation_ClampsRealSelectionAndInvalidatesLayout()
    {
        // setter 把整个值改写成更短的 "x" 后抛错：真实文本变短，光标必须夹回并重排。
        var binding = new ScriptedTextBinding("abcdefgh") { Normalize = _ => "x", ThrowAfterMutation = true };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        var state = State(node);
        Assert.Equal(8, state.Caret);

        Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));

        // 外部副作用已发生：会话/选区按真实文本恢复，布局必须失效，不能用绘制夹取掩盖错位。
        Assert.Equal("x", binding.Text);
        Assert.Equal(1, Editor(node).Version);
        Assert.Equal(1, state.Caret);
        Assert.Equal(1, state.Anchor);
        Assert.True(app.Runtime.NeedsDraw);
    }

    [Fact]
    public void GetterFailureAfterWrite_RecoversOnNextSafeRead_WithoutAnotherEdit()
    {
        var binding = new ScriptedTextBinding("abcdefgh") { Normalize = _ => "x", FailReadsAfterWrite = true };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        var state = State(node);
        var editor = Editor(node);

        Assert.Throws<InvalidOperationException>(() => app.Runtime.DispatchTextInput(new TextInputEvent("X")));
        Assert.Equal("x", binding.Text);
        Assert.True(editor.RequiresResync);
        Assert.Equal(0, editor.Version);
        Assert.Equal(8, state.Caret);

        // getter 恢复后，仅靠布局（没有任何新编辑）就要重同步。
        binding.ResetReadFailure();
        app.Runtime.InvalidateLayout(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("x", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.False(editor.RequiresResync);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.Equal(1, state.Caret);
    }

    [Fact]
    public void ExternalValueChange_ThenKeyboardInput_SyncsSessionWithoutDescriptorOrLayout()
    {
        var binding = new ScriptedTextBinding("abc");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;
        var editor = Editor(node);

        // 固定委托绑定：外部改值不会产生新描述，也不会请求重新布局。
        binding.Text = "x";
        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("x", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.True(editor.HistoryInvalidationRequested);
        Assert.Equal(1, State(node).Caret);
    }

    [Fact]
    public void ExternalValueChange_ThenRenderOnlyObservation_SyncsSessionWithoutRelayout()
    {
        var binding = new ScriptedTextBinding("abc");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;
        app.Key(UiKey.End);
        var editor = Editor(node);

        binding.Text = "x";
        app.Runtime.InvalidateRender();
        app.Frame();

        // 纯重绘入口（没有描述重建、没有重新布局）也必须观测真实绑定值并限制选区。
        Assert.Equal("x", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.Equal(1, State(node).Caret);
    }

    [Fact]
    public void ExternalShrink_WithRenderOnlyObservation_ClampsHorizontalScrollAndStaysStable()
    {
        var binding = new ScriptedTextBinding(new string('a', 200));
        var (app, _, node) = CreateField(binding.Binding, viewport: new Size(180, 60));
        using var _app = app;

        app.Key(UiKey.End);
        var state = State(node);
        var editor = Editor(node);
        Assert.True(state.ScrollX > 0, "长文本行尾必须已经横向滚动。");

        // 外部把文本缩短成单个字符：只 InvalidateRender + BuildFrame（没有重新布局、没有 Arrange）。
        binding.Text = "x";
        app.Runtime.InvalidateRender();
        app.Frame();

        // 纯重绘观测必须同步会话，并且用同一排版/内容区把横向滚动夹回，让新文字可见。
        Assert.Equal("x", editor.Text);
        Assert.Equal(1, editor.Version);
        Assert.True(state.ScrollX < 10, $"缩短后横向滚动必须夹回可见范围，实际 {state.ScrollX}。");

        // 稳定重复帧不得继续推进版本或反复改动滚动（不持续失效）。
        var version = editor.Version;
        var scroll = state.ScrollX;
        app.Runtime.InvalidateRender();
        app.Frame();
        app.Runtime.InvalidateRender();
        app.Frame();

        Assert.Equal(version, editor.Version);
        Assert.Equal(scroll, state.ScrollX);
    }

    // ---------------------------------------------------------------- 事务期间来源/配置替换（R2）

    [Fact]
    public void SourceReplacementDuringSetter_DoesNotLetOldTransactionOverwriteNewSession()
    {
        var newValue = "NEW";
        var oldBinding = new ScriptedTextBinding("abc");
        var newBinding = Binding<string>.From(() => newValue, value => newValue = value);
        var host = new SwitchableFieldHost { Current = oldBinding.Binding };
        TestApp? app = null;

        // setter 内部同步完成后把来源换成新绑定并完成重建：旧事务结束时必须重新核对挂载来源。
        oldBinding.AfterStore = () =>
        {
            host.Current = newBinding;
            host.Tick!.Value++;
            app!.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);
        var editor = Editor(node);

        Type(app, "X");

        Assert.Equal("NEW", ((TextField)node.View).Text.Value);
        Assert.Equal("NEW", editor.Text);
        Assert.NotEqual("abcX", editor.Text);
        Assert.True(editor.Version >= 2);
        Assert.True(editor.HistoryInvalidationRequested);
    }

    [Fact]
    public void SameTextSourceSwapDuringSetter_TracksNewSourceIdentity()
    {
        var newValue = "abcX";
        var oldBinding = new ScriptedTextBinding("abc");
        var newBinding = Binding<string>.From(() => newValue, value => newValue = value);
        var host = new SwitchableFieldHost { Current = oldBinding.Binding };
        TestApp? app = null;

        oldBinding.AfterStore = () =>
        {
            host.Current = newBinding;
            host.Tick!.Value++;
            app!.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);
        var editor = Editor(node);

        Type(app, "X");

        // 文本相同但来源已换：会话必须归属新来源，否则后续外部写入会被误判。
        Assert.Equal("abcX", ((TextField)node.View).Text.Value);
        Assert.Equal("abcX", editor.Text);
        Assert.True(editor.MatchesContext(newBinding.Identity, int.MaxValue));
        Assert.False(editor.MatchesContext(oldBinding.Binding.Identity, int.MaxValue));
    }

    [Fact]
    public void FieldUnmountedDuringSetter_DoesNotWriteOldTransactionIntoRemovedTree()
    {
        var oldBinding = new ScriptedTextBinding("abc");
        var host = new SwitchableFieldHost { Current = oldBinding.Binding };
        TestApp? app = null;

        // setter 内部隐藏并重建：输入框节点被卸载，旧事务结束时不得复活/串写已移除的树。
        oldBinding.AfterStore = () =>
        {
            host.ShowField = false;
            host.Tick!.Value++;
            app!.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 80));
        app.Frame();
        var node = Focus(app);

        Type(app, "X");

        Assert.Null(app.Runtime.Root.FindDescendant<TextField>());
        Assert.False(node.IsMounted);
        Assert.Null(node.InteractionState);
    }

    // ---------------------------------------------------------------- 事务字素不变量（R3）

    [Fact]
    public void Executor_RejectsReplacementRangeInsideGrapheme()
    {
        var binding = new ScriptedTextBinding("e\u0301Z");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        var editor = Editor(node);
        var operation = new EditOperation(new TextRange(1, 2), "Y", TextEditKind.Paste, TextEditReason.Clipboard);

        var result = editor.Apply(
            binding.Binding.Identity,
            int.MaxValue,
            operation,
            TextSelection.Caret(1),
            () => binding.Text,
            value => binding.Text = value);

        // 偏移 1 在组合附加符内部：整体拒绝，不得拆字写入。
        Assert.Equal(TextEditStatus.Rejected, result.Status);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal("e\u0301Z", binding.Text);
    }

    [Fact]
    public void Normalization_ReportsConfirmedSelectionOnGraphemeBoundary()
    {
        // 请求把 "X" 插到 0 后，绑定规范化为 "e\u0301X"：target caret 1 落在 e 与 U+0301 之间。
        var binding = new ScriptedTextBinding(string.Empty) { Normalize = _ => "e\u0301X" };
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Type(app, "X");

        Assert.Equal("e\u0301X", binding.Text);
        Assert.Equal(1, Editor(node).Version);
        Assert.True(Editor(node).HistoryInvalidationRequested);

        // 确认后的选区必须落在字素边界（0/2/3），不能是 1。
        Assert.Equal(2, State(node).Caret);
    }

    // ---------------------------------------------------------------- 辅助

    private static (TestApp App, TextEditFieldHost Host, ViewNode Node) CreateField(
        Binding<string> binding,
        int maxLength = int.MaxValue,
        FieldLayout layout = FieldLayout.Plain,
        bool dynamicMaxLength = false,
        Size? viewport = null)
    {
        var host = new TextEditFieldHost(binding, maxLength, layout, dynamicMaxLength);
        var app = new TestApp(host, viewport ?? new Size(400, 120));
        app.Frame();
        var node = Focus(app);
        return (app, host, node);
    }

    private static ViewNode Focus(TestApp app)
    {
        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return node;
    }

    private static void Type(TestApp app, string text)
    {
        app.Runtime.DispatchTextInput(new TextInputEvent(text));
        app.Runtime.ProcessPendingWork();
        app.Frame();
    }

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    private static TextEditSession Editor(ViewNode node) => State(node).Editor;

    /// <summary>文本中不得出现落单的代理项。</summary>
    private static void AssertWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "高代理项后必须紧跟低代理项。");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), "不得出现落单的低代理项。");
            }
        }
    }

    /// <summary>可编排拒绝 / 规范化 / 抛错的字符串绑定；身份稳定，便于跨重建验证。</summary>
    private sealed class ScriptedTextBinding
    {
        private bool _failReads;

        public ScriptedTextBinding(string initial)
        {
            Text = initial;
            Binding = CSharp.UI.Binding<string>.From(Read, Write);
        }

        public Binding<string> Binding { get; }

        public string Text { get; set; }

        /// <summary>写入前的转换（模拟绑定把值规范化成别的文本）。</summary>
        public Func<string, string>? Normalize { get; set; }

        /// <summary>setter 静默拒绝写入（回读仍是旧值）。</summary>
        public bool RejectWrites { get; set; }

        /// <summary>setter 在改变存储前抛错。</summary>
        public bool ThrowBeforeMutation { get; set; }

        /// <summary>setter 在改变存储后抛错（外部副作用已发生）。</summary>
        public bool ThrowAfterMutation { get; set; }

        /// <summary>写入成功后让后续 getter 抛错（回读失败）。</summary>
        public bool FailReadsAfterWrite { get; set; }

        /// <summary>getter 始终抛错。</summary>
        public bool ThrowOnRead { get; set; }

        /// <summary>存储成功后、事务回读前的回调；用于模拟 setter 内部同步完成的状态驱动重建。</summary>
        public Action? AfterStore { get; set; }

        public int SetterCalls { get; private set; }

        public void ResetReadFailure() => _failReads = false;

        private string Read()
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("scripted getter failure");
            }

            if (_failReads)
            {
                throw new InvalidOperationException("scripted getter failure after write");
            }

            return Text;
        }

        private void Write(string value)
        {
            SetterCalls++;
            if (ThrowBeforeMutation)
            {
                throw new InvalidOperationException("scripted setter failure before mutation");
            }

            if (!RejectWrites)
            {
                Text = Normalize?.Invoke(value) ?? value;
                if (FailReadsAfterWrite)
                {
                    _failReads = true;
                }

                AfterStore?.Invoke();
            }

            if (ThrowAfterMutation)
            {
                throw new InvalidOperationException("scripted setter failure after mutation");
            }
        }
    }

    /// <summary>复用同一节点、可强制重建并可切换绑定的输入框宿主。</summary>
    private sealed class TextEditFieldHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly int _maxLength;
        private readonly FieldLayout _layout;
        private readonly bool _dynamicMaxLength;

        public TextEditFieldHost(
            Binding<string> binding,
            int maxLength = int.MaxValue,
            FieldLayout layout = FieldLayout.Plain,
            bool dynamicMaxLength = false)
        {
            _binding = binding;
            _maxLength = maxLength;
            _layout = layout;
            _dynamicMaxLength = dynamicMaxLength;
        }

        public State<int>? Tick { get; private set; }

        public State<int>? Max { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            Max = UseState("max", _maxLength);
            var maxLength = _dynamicMaxLength ? Max.Value : _maxLength;

            View field = new TextField(_binding) { MaxLength = maxLength, Width = 200 };
            field = _layout switch
            {
                FieldLayout.Flow => new Flow(field),
                FieldLayout.Adaptive => new AdaptiveStack(300, field),
                _ => field,
            };

            return new VStack(field);
        }
    }

    /// <summary>可在运行时切换绑定来源/隐藏输入框、并强制同节点重建的宿主。</summary>
    private sealed class SwitchableFieldHost : Component
    {
        public Binding<string> Current { get; set; } = Binding<string>.Constant(string.Empty);

        public bool ShowField { get; set; } = true;

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new VStack(ShowField ? new TextField(Current) { Width = 200 } : (View)new Text("no field"));
        }
    }
}
