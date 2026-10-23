using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-05：真实 <see cref="UiRuntime"/> + <see cref="TextField"/> 上的有界撤销/重做历史、合并边界、
/// 预算淘汰与统一编辑命令派发。
/// </summary>
/// <remarks>
/// 这些测试通过真实输入分发（文本事件 / 按键 / 指针 / 组合服务）驱动稳定焦点节点，
/// 断言绑定实际文本、写入次数、文档版本单调性与历史计数，而不是复制实现公式做辅助函数自证。
/// 只有预算/溢出这类纯记账场景直接使用内部历史类型。
/// </remarks>
public class TextFieldUndoRedoTests
{
    private enum FieldLayout
    {
        Plain,
        Flow,
        Adaptive,
    }

    // ================================================================ 基本撤销/重做与选区

    [Fact]
    public void UndoRedo_RestoresTextDirectionalSelectionAndMonotonicVersion()
    {
        var binding = new ScriptedTextBinding("abc");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;
        var editor = Editor(node);
        var state = State(node);

        // 反向选择（anchor=3、caret=0）后键入，替换整个选区。
        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        Assert.True(state.HasSelection);
        Assert.Equal(3, state.Anchor);
        Assert.Equal(0, state.Caret);

        Send(app, "X");
        Assert.Equal("X", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        Assert.Equal(1, editor.Version);
        AssertHistory(node, undo: 1, redo: 0);

        // 撤销：恢复文本与原来<b>有方向</b>的选区，版本继续单调递增。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("abc", binding.Text);
        Assert.Equal(2, binding.SetterCalls);
        Assert.Equal(2, editor.Version);
        Assert.Equal(3, state.Anchor);
        Assert.Equal(0, state.Caret);
        Assert.True(state.HasSelection);
        AssertHistory(node, undo: 0, redo: 1);

        // 重做：恢复确认后的折叠光标。
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("X", binding.Text);
        Assert.Equal(3, binding.SetterCalls);
        Assert.Equal(3, editor.Version);
        Assert.Equal(1, state.Caret);
        Assert.Equal(1, state.Anchor);
        AssertHistory(node, undo: 1, redo: 0);
    }

    [Fact]
    public void EmptyStackUndoRedo_CallsNoSetterAndDoesNotAdvanceVersion()
    {
        var binding = new ScriptedTextBinding("abc");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        app.Key(UiKey.Z, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.Equal("abc", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(0, Editor(node).Version);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void TypingMerge_WindowIsInclusiveAtFiveHundredMilliseconds()
    {
        AssertMerge(advanceMilliseconds: 499, merged: true);
        AssertMerge(advanceMilliseconds: 500, merged: true);
        AssertMerge(advanceMilliseconds: 501, merged: false);
    }

    [Fact]
    public void TypingMerge_RequiresAdjacentCollapsedCaret()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        Send(app, "b", advanceMilliseconds: 100);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        // 光标左移（即使随后右移回原处）打断合并：之后的键入独立成项。
        app.Key(UiKey.Left);
        app.Key(UiKey.Right);
        Send(app, "c", advanceMilliseconds: 100);
        Assert.Equal("abc", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        // 撤销只回退最后一项。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void SameDirectionGraphemeDeletion_Merges_ButSelectioReplacementDoesNot()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "abcd");
        AssertHistory(node, undo: 1, redo: 0);

        // 同方向连续退格：合并为一项完整删除事务。
        Advance(app, 1000);
        app.Key(UiKey.Backspace);
        Advance(app, 100);
        app.Key(UiKey.Backspace);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("abcd", binding.Text);
        AssertHistory(node, undo: 1, redo: 1);

        // 撤销后键入新字符（不同类别，独立成项）。
        Send(app, "X", advanceMilliseconds: 1000);
        Assert.Equal("abcdX", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        // 选区替换独立成项，不与相邻键入/删除合并。
        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        app.Key(UiKey.Backspace);
        Assert.Equal("bcdX", binding.Text);
        AssertHistory(node, undo: 3, redo: 0);
    }

    [Fact]
    public void OppositeDirectionDeletes_DoNotMerge()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "abcd", advanceMilliseconds: 0);
        app.Key(UiKey.Home);
        app.Key(UiKey.Right);
        Advance(app, 100);

        app.Key(UiKey.Delete);   // 向前删除 b
        Advance(app, 100);
        app.Key(UiKey.Backspace); // 向后删除 a
        Assert.Equal("cd", binding.Text);

        // 键入组 + 两种方向删除各自成项。
        AssertHistory(node, undo: 3, redo: 0);
    }

    [Fact]
    public void UnicodeSurrogateCombiningAndZwj_AreUndoneAsWholeGraphemes()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a\U0001F600e\u0301" + family);
        Assert.Equal("a\U0001F600e\u0301" + family, binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        Advance(app, 1000);
        app.Key(UiKey.Backspace); // 删除整个 ZWJ 家庭序列
        Assert.Equal("a\U0001F600e\u0301", binding.Text);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a\U0001F600e\u0301" + family, binding.Text);
        AssertWellFormed(binding.Text);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("a\U0001F600e\u0301", binding.Text);
        AssertWellFormed(binding.Text);
    }

    [Fact]
    public void PointerSelection_EvenWhenItMovesBack_BreaksMerge()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "ab");
        AssertHistory(node, undo: 1, redo: 0);

        var click = ClickPoint(node);
        app.Click(click);
        var caretAfterFirstClick = State(node).Caret;
        app.Click(click); // 连续点击同一位置：选区没有净移动，但仍是分组边界
        Assert.Equal(caretAfterFirstClick, State(node).Caret);

        Send(app, "X", advanceMilliseconds: 100);
        AssertHistory(node, undo: 2, redo: 0);
    }

    [Fact]
    public void ClipboardCommands_AreSingleTransactions_AndCopyOnlyBreaksMerge()
    {
        var binding = new ScriptedTextBinding("hello");
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.A, KeyModifiers.Control);
        app.Key(UiKey.C, KeyModifiers.Control);
        Assert.Equal("hello", app.Clipboard.GetText());
        Assert.Equal(0, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);

        app.Key(UiKey.X, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("hello", binding.Text);

        app.Clipboard.SetText("世界");
        app.Key(UiKey.V, KeyModifiers.Control);
        Assert.Equal("世界", binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        // Copy 不写历史，但它仍是分组边界。
        Advance(app, 1000);
        Send(app, "a");
        app.Key(UiKey.C, KeyModifiers.Control);
        Send(app, "b", advanceMilliseconds: 100);
        AssertHistory(node, undo: 3, redo: 0);
    }

    [Fact]
    public void FocusAndWindowActivationChanges_BreakMergeWithoutClearingHistory()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Send(app, "b", advanceMilliseconds: 100);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        Advance(app, 1000);
        Send(app, "c");
        AssertHistory(node, undo: 3, redo: 0);

        // 窗口失活是分组边界，但不清空历史。
        app.Runtime.NotifyWindowActivation(false);
        Send(app, "d", advanceMilliseconds: 100);
        app.Runtime.NotifyWindowActivation(true);
        Assert.Equal("abcd", binding.Text);
        AssertHistory(node, undo: 4, redo: 0);
    }

    [Fact]
    public void CompositionPhases_BreakMerge()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        AssertHistory(node, undo: 1, redo: 0);

        Advance(app, 100);
        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 1, 0, new TextRange(1, 1), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Update(session, 0, 1, 1, new TextRange(1, 1), "ni")), TextInputOutcome.Updated);
        AssertApplied(service.Apply(CompositionEvent.Cancel(session, 0, 1, 2)), TextInputOutcome.Cancelled);
        AssertHistory(node, undo: 1, redo: 0);

        Send(app, "b", advanceMilliseconds: 100);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
    }

    [Fact]
    public void UndoRedo_BreaksMerge()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        // 重放本身打断合并：随后 100ms 内的键入不得并入被恢复的旧事务。
        Send(app, "b", advanceMilliseconds: 100);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
    }

    // ================================================================ redo 分叉 / no-op / 容量

    [Fact]
    public void RedoFork_ClearsRedoOnNewAcceptedEdit()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        Send(app, "b", advanceMilliseconds: 1000);
        AssertHistory(node, undo: 2, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        AssertHistory(node, undo: 1, redo: 1);

        Send(app, "c", advanceMilliseconds: 1000);
        Assert.Equal("ac", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("ac", binding.Text);
        Assert.Equal(setterCalls, binding.SetterCalls);
    }

    [Fact]
    public void NoOpAndCapacityRejection_DoNotAddEntryOrClearRedo()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, maxLength: 2);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        Send(app, "b", advanceMilliseconds: 1000);
        Assert.Equal("ab", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        AssertHistory(node, undo: 1, redo: 1);

        // 净化后为空的输入不是事务：不写绑定、不新增历史、不清 redo。
        var setterCalls = binding.SetterCalls;
        app.Runtime.DispatchTextInput(new TextInputEvent("\n\t"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal(setterCalls, binding.SetterCalls);
        AssertHistory(node, undo: 1, redo: 1);

        // 容量整体拒绝（剩余 1 个码元放不下 2 码元代理对）：仍不写绑定、不清 redo。
        Send(app, "\U0001F600", advanceMilliseconds: 100);
        Assert.Equal(setterCalls, binding.SetterCalls);
        AssertHistory(node, undo: 1, redo: 1);

        // redo 仍然可用。
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
    }

    [Fact]
    public void ZeroCapacity_DisablesRecordingButNotEditing()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, capacity: TextHistoryCapacity.Disabled);
        using var _app = app;

        Send(app, "abc");
        Assert.Equal("abc", binding.Text);
        Assert.Equal(1, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);

        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("abc", binding.Text);
        Assert.Equal(setterCalls, binding.SetterCalls);
    }

    [Fact]
    public void SingleOversizeEdit_ExecutesButClearsBothStacks()
    {
        // 文本预算 8：先有可撤销项，再粘贴 20 个码元（单次事务超预算）。
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, capacity: new TextHistoryCapacity(100, 8));
        using var _app = app;

        Send(app, "ab");
        AssertHistory(node, undo: 1, redo: 0);

        app.Clipboard.SetText(new string('x', 20));
        app.Key(UiKey.V, KeyModifiers.Control);
        Assert.Equal("ab" + new string('x', 20), binding.Text);
        AssertHistory(node, undo: 0, redo: 0);

        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(setterCalls, binding.SetterCalls);
    }

    [Fact]
    public void UndoRestoresCaretVisibilityAfterLongTypingGroup()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, viewport: new Size(400, 160));
        using var _app = app;

        Send(app, new string('a', 100));
        Assert.Equal(100, State(node).Caret);
        Assert.True(State(node).ScrollX > 0, "末端光标应把横向滚动推离原点。");

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(0, State(node).Caret);
        Assert.Equal(0.0, State(node).ScrollX);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(100, State(node).Caret);
        Assert.True(State(node).ScrollX > 0);
    }

    [Fact]
    public void HistoryEntriesStoreOnlyDeltaFragments_NotFullDocumentSnapshots()
    {
        // 2000 码元文档上的一次单字符键入只应保留 1 个码元的历史片段。
        var binding = new ScriptedTextBinding(new string('x', 2000));
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "Y");
        Assert.Equal(2001, binding.Text.Length);
        AssertHistory(node, undo: 1, redo: 0);
        Assert.Equal(1, State(node).History.RetainedTextLength);
    }

    [Fact]
    public void CompositionResult_RecordsHistoryWithoutRunningTextLayout()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(2, 2), "n")), TextInputOutcome.Started);

        // 真实组合回调内记录历史只允许核心内存操作：不得触发文本排版。
        var layoutMisses = app.Text.LayoutMisses;
        var result = service.Apply(CompositionEvent.Result(session, 0, 0, 1, "你"));
        Assert.Equal(layoutMisses, app.Text.LayoutMisses);
        AssertApplied(result, TextInputOutcome.ResultCommitted);
        AssertHistory(node, undo: 1, redo: 0);
    }

    [Fact]
    public void ExternallyObservedChange_InvalidatesBeforeUndoAction()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        // 外部直接改写绑定（没有经过描述重建）：Undo 动作开始前必须先观测并清理历史。
        binding.Text = "zz";
        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);

        Assert.Equal("zz", binding.Text);
        Assert.Equal(setterCalls, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void MaxLengthTruncatedEdit_RecordsOnlyTheActuallyAcceptedWholeGraphemes()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, maxLength: 3);
        using var _app = app;

        // 容量 3：代理对占 2 码元，"a" + 😀 恰好 3，尾部 "z" 被整字素截断。
        Send(app, "a\U0001F600z");
        Assert.Equal("a\U0001F600", binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        // 历史片段必须记录真实接受的 3 个码元与真实删除片段，而不是原始请求的 4 个。
        Assert.Equal(3, State(node).History.RetainedTextLength);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        AssertWellFormed(binding.Text);
    }

    [Fact]
    public void MacClipboardCommands_RequireCommand_NotControl()
    {
        var binding = new ScriptedTextBinding("hello");
        var (app, _, node) = CreateField(binding.Binding, platform: TextEditPlatformKind.MacOS);
        using var _app = app;

        app.Key(UiKey.A, KeyModifiers.Meta);
        Assert.True(State(node).HasSelection);

        // macOS 的 Control+C 不是复制命令。
        app.Key(UiKey.C, KeyModifiers.Control);
        Assert.Equal(string.Empty, app.Clipboard.GetText());
        Assert.Equal(0, binding.SetterCalls);

        app.Key(UiKey.C, KeyModifiers.Meta);
        Assert.Equal("hello", app.Clipboard.GetText());

        app.Key(UiKey.X, KeyModifiers.Control);
        Assert.Equal("hello", binding.Text);
        app.Key(UiKey.X, KeyModifiers.Meta);
        Assert.Equal(string.Empty, binding.Text);
        AssertHistory(node, undo: 1, redo: 0);
    }

    // ================================================================ 复审回归（2026-10-19 R1–R3）

    [Theory]
    [InlineData(1000)]
    [InlineData(100)]
    public void UndoRedo_CrossTransactionCombiningMark_UsesGraphemeSafeDeltas(int gapMilliseconds)
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "e");
        Send(app, "\u0301", advanceMilliseconds: gapMilliseconds);

        // 两个事务合成一个完整字素 e+U+0301（同一窗口内也不合并跨字素事务）。
        Assert.Equal("e\u0301", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
        Assert.Equal(2, Editor(node).Version);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("e", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("e", binding.Text);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("e\u0301", binding.Text);
        AssertWellFormed(binding.Text);
    }

    [Fact]
    public void UndoRedo_CrossTransactionZwjSequence_RestoresEachStep()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "\U0001F469"); // 👩
        Send(app, "\u200D", advanceMilliseconds: 1000);
        Send(app, "\U0001F4BB", advanceMilliseconds: 1000);
        Assert.Equal("\U0001F469\u200D\U0001F4BB", binding.Text);
        AssertHistory(node, undo: 3, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("\U0001F469\u200D", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("\U0001F469", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);

        app.Key(UiKey.Y, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("\U0001F469\u200D\U0001F4BB", binding.Text);
        AssertWellFormed(binding.Text);
    }

    [Fact]
    public void UndoSetterRejection_ClearsBothStacks_AndSubsequentReplayDoesNotWrite()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.RejectWrites = true;
        var writes = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        Assert.Equal(writes + 1, binding.SetterCalls); // 确实尝试过一次写入
        AssertHistory(node, undo: 0, redo: 0);

        binding.RejectWrites = false;
        writes = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        Assert.Equal(writes, binding.SetterCalls);
    }

    [Fact]
    public void RedoSetterRejection_ClearsBothStacks_AndSubsequentReplayDoesNotWrite()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        AssertHistory(node, undo: 0, redo: 1);

        binding.RejectWrites = true;
        var writes = binding.SetterCalls;
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(writes + 1, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);

        binding.RejectWrites = false;
        writes = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(writes, binding.SetterCalls);
    }

    [Fact]
    public void GetterFailureBeforeHistoryCommand_ClearsUntrustedHistory()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.ThrowOnRead = true;
        Assert.Throws<InvalidOperationException>(() =>
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Z, KeyModifiers.Control)));
        binding.ThrowOnRead = false;

        var writes = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        Assert.Equal(writes, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void CompositionClientReadFailure_AlsoInvalidatesHistory()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        var client = Client(node);
        binding.ThrowOnRead = true;
        _ = client.Document; // 客户端安全返回缓存，但必须让历史失效
        binding.ThrowOnRead = false;

        var writes = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        Assert.Equal(writes, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void UndoRedo_RegionalIndicatorInsertionAtPrefix_RepairsPairingInBothDocuments()
    {
        // 🇦🇧 是一个 RI 字素；在最前面插入 🇽 后变成 🇽🇦 + 🇧，映射回原文的端点落在 🇦🇧 内部。
        const string before = "\U0001F1E6\U0001F1E7";
        const string after = "\U0001F1FD" + before;

        var binding = new ScriptedTextBinding(before);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home); // caret 0
        var version = Editor(node).Version;
        Send(app, "\U0001F1FD");

        Assert.Equal(after, binding.Text);
        Assert.Equal(version + 1, Editor(node).Version);
        AssertHistory(node, undo: 1, redo: 0);

        // 受影响的局部片段：删除原文 4 个码元 + 插入结果 6 个码元。
        Assert.Equal(10, State(node).History.RetainedTextLength);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(before, binding.Text);
        Assert.Equal(0, State(node).Caret);
        Assert.True(Editor(node).Version > version + 1);
        AssertHistory(node, undo: 0, redo: 1);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(after, binding.Text);
        // TI-03 确认后的 caret 必须落在字素边界：插入端点 2 在 🇽🇦 内部，向后吸附到 4。
        Assert.Equal(4, State(node).Caret);
        Assert.True(Editor(node).Version > version + 2);
        AssertHistory(node, undo: 1, redo: 0);
    }

    [Fact]
    public void UndoRedo_RegionalIndicatorInsertionInMiddle_RepairsPairingInBothDocuments()
    {
        // 🇦🇧🇨（边界 0,4,6）中间插入 🇽 后变成 🇦🇧🇽🇨（边界 0,4,8）。
        const string before = "\U0001F1E6\U0001F1E7\U0001F1E8";
        const string after = "\U0001F1E6\U0001F1E7\U0001F1FD\U0001F1E8";

        var binding = new ScriptedTextBinding(before);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right); // caret 4（第二个 RI 对之后）
        Assert.Equal(4, State(node).Caret);
        var version = Editor(node).Version;
        Send(app, "\U0001F1FD");

        Assert.Equal(after, binding.Text);
        Assert.Equal(version + 1, Editor(node).Version);
        AssertHistory(node, undo: 1, redo: 0);
        Assert.Equal(6, State(node).History.RetainedTextLength); // 删除 🇨(2) + 插入 🇽🇨(4)

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(before, binding.Text);
        Assert.Equal(4, State(node).Caret);
        AssertHistory(node, undo: 0, redo: 1);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(after, binding.Text);
        Assert.Equal(8, State(node).Caret); // 插入端点 6 在 🇽🇨 内部，向后吸附到 8
        AssertHistory(node, undo: 1, redo: 0);
    }

    [Fact]
    public void UndoRedo_DeletionCausingRegionalIndicatorRepairing_RestoresExactTextAndSelection()
    {
        // 删除分隔符会让两侧 RI 重新配成 🇦🇧：前后文档的边界集合完全不同。
        const string before = "\U0001F1E6X\U0001F1E7"; // 🇦 X 🇧
        const string after = "\U0001F1E6\U0001F1E7";   // 🇦🇧

        var binding = new ScriptedTextBinding(before);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        app.Key(UiKey.Home);
        app.Key(UiKey.Right); // caret 2（🇦 之后）
        app.Key(UiKey.Right); // caret 3（X 之后）
        Assert.Equal(3, State(node).Caret);

        var version = Editor(node).Version;
        app.Key(UiKey.Backspace); // 删除 X，两侧 RI 重排成 🇦🇧
        Assert.Equal(after, binding.Text);
        Assert.Equal(version + 1, Editor(node).Version);
        AssertHistory(node, undo: 1, redo: 0);
        Assert.Equal(9, State(node).History.RetainedTextLength); // 删除 🇦X🇧(5) + 插入 🇦🇧(4)

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(before, binding.Text);
        Assert.Equal(3, State(node).Caret); // 恢复删除前的有方向选区（折叠 caret 3）
        AssertHistory(node, undo: 0, redo: 1);

        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal(after, binding.Text);
        Assert.Equal(4, State(node).Caret); // 删除端点 2 在 🇦🇧 内部，向后吸附到 4
        AssertHistory(node, undo: 1, redo: 0);
    }

    // ================================================================ 组合（IME）历史

    [Fact]
    public void CompositionPreedit_NeverEntersHistory_EachSegmentedResultIsOneEntry()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(0, 0), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Update(session, 0, 0, 1, new TextRange(0, 0), "ni")), TextInputOutcome.Updated);
        AssertHistory(node, undo: 0, redo: 0);
        Assert.Equal(0, binding.SetterCalls);

        // 第一段结果：一项历史。
        AssertApplied(service.Apply(CompositionEvent.Result(session, 0, 0, 2, "你")), TextInputOutcome.ResultCommitted);
        Assert.Equal("你", binding.Text);
        AssertHistory(node, undo: 1, redo: 0);

        // 同一组合的下一段预编辑（已提交前缀保留）与第二段结果：各自独立一项。
        AssertApplied(service.Apply(CompositionEvent.Start(session, 1, 1, 0, new TextRange(1, 1), "h")), TextInputOutcome.Started);
        AssertHistory(node, undo: 1, redo: 0);
        AssertApplied(service.Apply(CompositionEvent.Result(session, 1, 1, 1, "好")), TextInputOutcome.ResultCommitted);
        Assert.Equal("你好", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("你", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
    }

    [Fact]
    public void CompositionCancel_AddsNoHistory_AndOnlyDiscardsUncommittedSegment()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(2, 2), "n")), TextInputOutcome.Started);
        AssertApplied(service.Apply(CompositionEvent.Cancel(session, 0, 0, 1)), TextInputOutcome.Cancelled);
        AssertHistory(node, undo: 0, redo: 0);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
    }

    [Fact]
    public void UndoWithEmptyStack_StillCancelsPreedit()
    {
        var binding = new ScriptedTextBinding("ab");
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        var client = Client(node);
        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(2, 2), "n")), TextInputOutcome.Started);
        Assert.True(client.IsComposing);

        var version = Editor(node).Version;
        app.Key(UiKey.Z, KeyModifiers.Control);

        Assert.False(client.IsComposing);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.SetterCalls);
        Assert.Equal(version, Editor(node).Version);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void UnownedUndoDuringPreedit_CancelsThenUndoes_AndCopyDoesNotCancel()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        // 组合开始前先建立已提交选区：组合期间的 Copy 只复制这段已提交文本。
        app.Key(UiKey.End);
        app.Key(UiKey.Home, KeyModifiers.Shift);
        Assert.True(State(node).HasSelection);

        Advance(app, 1000);
        var session = Session(node);
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 1, 0, new TextRange(0, 1), "n")), TextInputOutcome.Started);
        Assert.True(Client(node).IsComposing);

        // Copy 不取消组合、不写历史，只复制已提交选区。
        app.Key(UiKey.C, KeyModifiers.Control);
        Assert.True(Client(node).IsComposing);
        Assert.Equal("a", app.Clipboard.GetText());
        AssertHistory(node, undo: 1, redo: 0);

        // 未被输入法声明消费的 Undo：先取消预编辑，再执行撤销。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.False(Client(node).IsComposing);
        Assert.Equal(string.Empty, binding.Text);
        Assert.Equal(2, binding.SetterCalls);
        AssertHistory(node, undo: 0, redo: 1);
    }

    [Fact]
    public void ExplicitImeConsumedKeyToken_WinsEvenForCommandShortcutAndEndBeforeKeyDown()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var service = new FakeTextInputService();
        var (app, _, node) = CreateField(binding.Binding, service: service);
        using var _app = app;

        Send(app, "a");
        var session = Session(node);
        AssertApplied(
            service.Apply(CompositionEvent.Result(session, 0, 1, 0, "b", new TextRange(1, 1))),
            TextInputOutcome.ResultCommitted);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);

        // 组合在同一原生按键内结束并声明已消费：同一 KeyDown 的 Ctrl+Z 必须让给输入法。
        var token = new object();
        Assert.True(app.Runtime.NotifyCompositionKeyOrigin(token, consumedByInputMethod: true));
        var setterCalls = binding.SetterCalls;
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Z, KeyModifiers.Control), token);

        Assert.Equal("ab", binding.Text);
        Assert.Equal(setterCalls, binding.SetterCalls);
        AssertHistory(node, undo: 2, redo: 0);

        // 归属是一次性的：随后同一组合快捷键正常执行撤销。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        AssertHistory(node, undo: 1, redo: 1);
    }

    // ================================================================ 不可信历史与外部变化

    [Fact]
    public void SetterExplicitRejection_ClearsUntrustedHistory_WithoutRecording()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.RejectWrites = true;
        Send(app, "b", advanceMilliseconds: 1000);

        Assert.Equal("a", binding.Text);
        AssertHistory(node, undo: 0, redo: 0);

        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(setterCalls, binding.SetterCalls);
        Assert.Equal("a", binding.Text);
    }

    [Fact]
    public void SetterNormalization_ClearsHistory_AndDoesNotRecordInverseOperation()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.Normalize = value => value.ToUpperInvariant();
        Send(app, "b", advanceMilliseconds: 1000);

        // 请求 "ab"，实际回读 "AB"：接受事务，但不得记录基于请求的逆操作。
        Assert.Equal("AB", binding.Text);
        Assert.Equal(2, Editor(node).Version);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void SetterThrowAfterMutation_ClearsHistory_WithoutRecording()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.ThrowAfterMutation = true;
        Assert.Throws<InvalidOperationException>(() =>
            app.Runtime.DispatchTextInput(new TextInputEvent("b")));
        binding.ThrowAfterMutation = false;

        Assert.Equal("ab", binding.Text); // 外部副作用不回滚
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void GetterThrowAfterWrite_ClearsHistory_WithoutFakeSuccess()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        binding.FailReadsAfterWrite = true;
        Assert.Throws<InvalidOperationException>(() =>
            app.Runtime.DispatchTextInput(new TextInputEvent("b")));
        binding.ResetReadFailure();

        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 0, redo: 0);
    }

    [Fact]
    public void ExternalTextChange_ClearsBothStacks_ButSameValueRebuildDoesNot()
    {
        var host = new StateFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 120));
        app.Frame();
        var node = Focus(app, 0);

        Send(app, "d");
        AssertHistory(node, undo: 1, redo: 0);

        host.Text!.Value = "xy";
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal("xy", host.Text.PeekValue());
        AssertHistory(node, undo: 0, redo: 0);

        // 同值重建不重复清空历史：先制造一项，再触发同值外部重建。
        Send(app, "z", advanceMilliseconds: 1000);
        AssertHistory(node, undo: 1, redo: 0);
        host.Tick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        AssertHistory(node, undo: 1, redo: 0);
    }

    [Fact]
    public void SourceAndMaxLengthChanges_ClearHistory()
    {
        var first = new ScriptedTextBinding("abc");
        var second = new ScriptedTextBinding("zz");
        var host = new SwitchableFieldHost { Current = first.Binding };
        using var app = new TestApp(host, new Size(400, 120));
        app.Frame();
        var node = Focus(app, 0);

        Send(app, "d");
        AssertHistory(node, undo: 1, redo: 0);

        host.Current = second.Binding;
        host.Tick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal("zz", ((TextField)node.View).Text.Value);
        AssertHistory(node, undo: 0, redo: 0);

        // MaxLength 变化同样清空历史（即使是同一来源）。
        var dynamicBinding = new ScriptedTextBinding("abcdef");
        var dynamicHost = new DynamicMaxFieldHost(dynamicBinding.Binding, maxLength: 10);
        using var app2 = new TestApp(dynamicHost, new Size(400, 120));
        app2.Frame();
        var node2 = Focus(app2, 0);
        Send(app2, "X");
        AssertHistory(node2, undo: 1, redo: 0);

        dynamicHost.Max!.Value = 3;
        app2.Runtime.ProcessPendingWork();
        app2.Frame();
        AssertHistory(node2, undo: 0, redo: 0);
    }

    [Fact]
    public void SynchronousUnmountDuringSetter_DiscardsThatNodesHistory()
    {
        var binding = new ScriptedTextBinding("abc");
        var host = new SwitchableFieldHost { Current = binding.Binding };
        TestApp? app = null;

        binding.AfterStore = () =>
        {
            host.ShowField = false;
            host.Tick!.Value++;
            app!.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 120));
        app.Frame();
        var node = Focus(app, 0);

        Send(app, "X");

        Assert.Null(app.Runtime.Root.FindDescendant<TextField>());
        Assert.False(node.IsMounted);
        Assert.Null(node.InteractionState);
    }

    [Fact]
    public void SynchronousFocusSwitchDuringSetter_DoesNotCorruptTheOtherNodesHistory()
    {
        var first = new ScriptedTextBinding("aaa");
        var second = new ScriptedTextBinding("bbb");
        var host = new TwoFieldHost { First = first.Binding, Second = second.Binding };
        TestApp? app = null;
        ViewNode? secondNode = null;

        first.AfterStore = () =>
        {
            app!.Runtime.SetFocus(secondNode);
            app.Runtime.ProcessPendingWork();
        };

        app = new TestApp(host, new Size(400, 160));
        app.Frame();
        var firstNode = Focus(app, 0);
        secondNode = app.Runtime.Root.FindDescendants<TextField>().ElementAt(1);

        Send(app, "X");

        // 焦点被 setter 同步切走：新节点历史必须保持为空，旧节点历史不被串写。
        Assert.Same(secondNode, app.Runtime.FocusedNode);
        AssertHistory(secondNode, undo: 0, redo: 0);
        Assert.Equal("bbb", second.Text);

        first.AfterStore = null;
        app.Runtime.SetFocus(secondNode);
        Send(app, "Z");
        AssertHistory(secondNode, undo: 1, redo: 0);
        // 旧节点的事务仍落在自己的来源上，历史不被新节点污染。
        Assert.Equal("Xaaa", first.Text);
    }

    // ================================================================ 布局/响应式生命周期

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlowAndAdaptiveLayoutChanges_PreserveHistory(bool adaptive)
    {
        var layout = adaptive ? FieldLayout.Adaptive : FieldLayout.Flow;
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, layout: layout, viewport: new Size(400, 200));
        using var _app = app;

        Send(app, "abc");
        AssertHistory(node, undo: 1, redo: 0);

        app.Runtime.SetViewport(new Size(900, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(node, app.Runtime.Root.Require<TextField>());
        AssertHistory(node, undo: 1, redo: 0);

        app.Runtime.SetViewport(new Size(250, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Same(node, app.Runtime.Root.Require<TextField>());
        AssertHistory(node, undo: 1, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(string.Empty, binding.Text);
    }

    [Fact]
    public void ResponsiveTrueUnmount_DoesNotMigrateHistory()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var host = new ResponsiveFieldHost(binding.Binding, breakpoint: 500);
        using var app = new TestApp(host, new Size(400, 160));
        app.Frame();
        var node = Focus(app, 0);

        Send(app, "abc");
        AssertHistory(node, undo: 1, redo: 0);

        // 变宽：Responsive 切到另一个分支，TextField 真正卸载。
        app.Runtime.SetViewport(new Size(800, 160), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Null(app.Runtime.Root.FindDescendant<TextField>());
        Assert.False(node.IsMounted);

        // 变窄：重新挂载一个新的 TextField（同一绑定、全新节点状态）。
        app.Runtime.SetViewport(new Size(400, 160), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var remounted = Focus(app, 0);
        Assert.NotSame(node, remounted);
        Assert.Equal("abc", binding.Text);
        AssertHistory(remounted, undo: 0, redo: 0);
    }

    // ================================================================ 平台快捷键与 AltGr

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void WindowsAndLinux_UseControlShortcuts(string platformName)
    {
        var platform = platformName == "windows" ? TextEditPlatformKind.Windows : TextEditPlatformKind.Linux;
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, platform: platform);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        Send(app, "b", advanceMilliseconds: 1000);
        AssertHistory(node, undo: 2, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("a", binding.Text);
        app.Key(UiKey.Y, KeyModifiers.Control);
        Assert.Equal("ab", binding.Text);

        app.Key(UiKey.Z, KeyModifiers.Control);
        app.Key(UiKey.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
    }

    [Fact]
    public void MacOS_UsesCommandOnly_AndCommandYIsNotRedo()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding, platform: TextEditPlatformKind.MacOS);
        using var _app = app;

        Send(app, "a", advanceMilliseconds: 0);
        Send(app, "b", advanceMilliseconds: 1000);

        // macOS 的 Control+Z 不触发撤销。
        var setterCalls = binding.SetterCalls;
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(setterCalls, binding.SetterCalls);

        // Command+Z 撤销，Command+Shift+Z 重做；Command+Y 不是重做。
        app.Key(UiKey.Z, KeyModifiers.Meta);
        Assert.Equal("a", binding.Text);
        app.Key(UiKey.Y, KeyModifiers.Meta);
        Assert.Equal("a", binding.Text);
        app.Key(UiKey.Z, KeyModifiers.Meta | KeyModifiers.Shift);
        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: 2, redo: 0);
    }

    [Fact]
    public void WrongPlatformModifierAltGrAndUnsupportedExtras_DoNotExecuteCommands()
    {
        var windowsBinding = new ScriptedTextBinding(string.Empty);
        var (windowsApp, _, windowsNode) = CreateField(windowsBinding.Binding, platform: TextEditPlatformKind.Windows);
        using var _windows = windowsApp;

        Send(windowsApp, "a", advanceMilliseconds: 0);
        Send(windowsApp, "b", advanceMilliseconds: 1000);

        var setterCalls = windowsBinding.SetterCalls;
        windowsApp.Key(UiKey.Z, KeyModifiers.Meta);                          // 错误平台主修饰键
        windowsApp.Key(UiKey.Z, KeyModifiers.Control | KeyModifiers.Alt);    // AltGr
        windowsApp.Key(UiKey.Z, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        windowsApp.Key(UiKey.X, KeyModifiers.Control | KeyModifiers.Shift);  // 不支持的额外组合
        windowsApp.Key(UiKey.C, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.Equal("ab", windowsBinding.Text);
        Assert.Equal(setterCalls, windowsBinding.SetterCalls);
        AssertHistory(windowsNode, undo: 2, redo: 0);

        var macBinding = new ScriptedTextBinding(string.Empty);
        var (macApp, _, macNode) = CreateField(macBinding.Binding, platform: TextEditPlatformKind.MacOS);
        using var _mac = macApp;

        Send(macApp, "a", advanceMilliseconds: 0);
        Send(macApp, "b", advanceMilliseconds: 1000);
        var macSetters = macBinding.SetterCalls;
        macApp.Key(UiKey.Z, KeyModifiers.Control);                       // macOS 的 Control 不是命令键
        macApp.Key(UiKey.Z, KeyModifiers.Meta | KeyModifiers.Alt);       // Command+Option

        Assert.Equal("ab", macBinding.Text);
        Assert.Equal(macSetters, macBinding.SetterCalls);
        AssertHistory(macNode, undo: 2, redo: 0);
    }

    [Fact]
    public void CapsLock_DoesNotChangeShortcutMeaning()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        AssertHistory(node, undo: 1, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control | KeyModifiers.CapsLock);
        Assert.Equal(string.Empty, binding.Text);
        AssertHistory(node, undo: 0, redo: 1);
    }

    // ================================================================ 预算/记账（直接内部类型）

    [Fact]
    public void HistoryCountBudget_EvictsOldestWholeTransactions()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        for (var i = 0; i < 105; i++)
        {
            history.Record(Insert("x", start: i, kind: TextEditKind.Paste), TimeSpan.FromMilliseconds(i));
        }

        Assert.Equal(100, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(100, history.RetainedTextLength);
        Assert.Equal(104, history.UndoPeek!.ReplacementStart);

        // 撤销全部保留项后，重做栈最底端应是最早保留下来的第 6 次记录（最老 5 项被整事务淘汰）。
        for (var i = 0; i < 100; i++)
        {
            history.CommitUndo();
        }

        Assert.False(history.CanUndo);
        Assert.Equal(5, history.RedoPeek!.ReplacementStart);
        Assert.Equal(100, history.RetainedTextLength);
    }

    [Fact]
    public void HistoryTextBudget_EvictsOldestWholeTransactions_AndCountsBothStacks()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 10));
        history.Record(Insert("abcd", start: 0, kind: TextEditKind.Paste), TimeSpan.Zero);
        history.Record(Insert("efgh", start: 4, kind: TextEditKind.Paste), TimeSpan.FromSeconds(1));
        Assert.Equal(8, history.RetainedTextLength);

        // 再记 4 个字符 → 12 超过预算 10：淘汰最老整事务后剩 8。
        history.Record(Insert("ijkl", start: 8, kind: TextEditKind.Paste), TimeSpan.FromSeconds(2));
        Assert.Equal(2, history.UndoCount);
        Assert.Equal(8, history.RetainedTextLength);

        // 栈间移动同一实例不重复计数。
        history.CommitUndo();
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(1, history.RedoCount);
        Assert.Equal(8, history.RetainedTextLength);
    }

    [Fact]
    public void SingleOversizeRecord_ClearsBothStacks_AndIsNotSaved()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 5));
        history.Record(Insert("ab", start: 0, kind: TextEditKind.Paste), TimeSpan.Zero);
        Assert.Equal(1, history.UndoCount);

        history.Record(Insert("abcdef", start: 2, kind: TextEditKind.Paste), TimeSpan.FromSeconds(1));

        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(0, history.RetainedTextLength);
    }

    [Fact]
    public void MergeThatWouldExceedTextBudget_StopsMerging_AndKeepsWholeTransactions()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 5));
        history.Record(Typing("abc", start: 0), TimeSpan.Zero);
        history.Record(Typing("def", start: 3), TimeSpan.FromMilliseconds(100));

        // 合并后 6 > 5：不合并，按完整事务入栈后整事务淘汰最老项，不留下部分可撤销字符。
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(3, history.RetainedTextLength);
        Assert.Equal("def", history.UndoPeek!.Inserted);
        Assert.Equal(3, history.UndoPeek.ReplacementStart);
    }

    [Fact]
    public void MergeThatFillsTheBudget_EvictsOldestWholeTransactions_AndKeepsMergedEntryIntact()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 10));

        // A 是另一种合并类别（退格删除），不会与后续键入合并。
        history.Record(Delete(TextEditMergeKind.BackspaceDelete, deleted: "aaaa", start: 0), TimeSpan.Zero);
        history.Record(Typing("bbbb", start: 0), TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, history.UndoCount);
        Assert.Equal(8, history.RetainedTextLength);

        // B+C 合并为 8（本身仍在预算内），但 A+B+C=12 超过预算：必须淘汰最老整事务 A。
        history.Record(Typing("cccc", start: 4), TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(8, history.RetainedTextLength);
        Assert.Equal(0, history.UndoPeek!.ReplacementStart);
        Assert.Equal("bbbbcccc", history.UndoPeek.Inserted);
    }

    [Fact]
    public void MergeWindow_IsInclusive_AndNegativeIntervalsDoNotMerge()
    {
        var at499 = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        at499.Record(Typing("a", start: 0), TimeSpan.FromMilliseconds(1000));
        at499.Record(Typing("b", start: 1), TimeSpan.FromMilliseconds(1499));
        Assert.Equal(1, at499.UndoCount);

        var at500 = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        at500.Record(Typing("a", start: 0), TimeSpan.FromMilliseconds(1000));
        at500.Record(Typing("b", start: 1), TimeSpan.FromMilliseconds(1500));
        Assert.Equal(1, at500.UndoCount);

        var at501 = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        at501.Record(Typing("a", start: 0), TimeSpan.FromMilliseconds(1000));
        at501.Record(Typing("b", start: 1), TimeSpan.FromMilliseconds(1501));
        Assert.Equal(2, at501.UndoCount);

        var backwards = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        backwards.Record(Typing("a", start: 0), TimeSpan.FromMilliseconds(1000));
        backwards.Record(Typing("b", start: 1), TimeSpan.FromMilliseconds(900));
        Assert.Equal(2, backwards.UndoCount);
    }

    [Fact]
    public void NonAdjacentOrDifferentKindEdits_NeverMerge()
    {
        var history = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        history.Record(Typing("a", start: 0), TimeSpan.Zero);
        history.Record(Typing("b", start: 5), TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, history.UndoCount);

        var mixed = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        mixed.Record(Typing("a", start: 0), TimeSpan.Zero);
        mixed.Record(Delete(TextEditMergeKind.BackspaceDelete, deleted: "x", start: 0), TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, mixed.UndoCount);

        var broken = new TextEditHistory(new TextHistoryCapacity(100, 1_048_576));
        broken.Record(Typing("a", start: 0), TimeSpan.Zero);
        broken.BreakMerge();
        broken.Record(Typing("b", start: 1), TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, broken.UndoCount);
    }

    [Fact]
    public void IndependentHistoryConsumption_PreservesCompositionInvalidationFlag()
    {
        var session = new TextEditSession();
        session.Sync(new object(), "a", int.MaxValue);

        // 制造一次外部变化：两个接缝位都应置位。
        session.Sync(new object(), "b", int.MaxValue);
        Assert.True(session.CompositionInvalidationRequested);
        Assert.True(session.HistoryInvalidationRequested);

        // 历史消费者独立消费，不得清掉组合消费者的标志。
        Assert.True(session.ConsumeHistoryInvalidation());
        Assert.False(session.HistoryInvalidationRequested);
        Assert.True(session.CompositionInvalidationRequested);

        // 组合消费者再独立消费。
        Assert.True(session.ConsumeCompositionInvalidation());
        Assert.False(session.CompositionInvalidationRequested);
    }

    [Fact]
    public void StructuredTransactionOutcome_DistinguishesCapacityRejectionFromSetterRefusal()
    {
        var capacityRejected = new ScriptedTextBinding("ab");
        var (capacityApp, _, capacityNode) = CreateField(capacityRejected.Binding, maxLength: 2);
        using var _capacity = capacityApp;
        Send(capacityApp, "X");
        Assert.Equal(TextEditTransactionOutcome.Rejected, Editor(capacityNode).LastTransaction);
        var refused = new ScriptedTextBinding("ab") { RejectWrites = true };
        var (refusedApp, _, refusedNode) = CreateField(refused.Binding);
        using var _refused = refusedApp;
        Send(refusedApp, "X");
        Assert.Equal(TextEditTransactionOutcome.SetterRefused, Editor(refusedNode).LastTransaction);

        var unchanged = new ScriptedTextBinding("a");
        var (unchangedApp, _, unchangedNode) = CreateField(unchanged.Binding, maxLength: 1);
        using var _unchanged = unchangedApp;
        unchangedApp.Key(UiKey.A, KeyModifiers.Control);
        Send(unchangedApp, "a");
        Assert.Equal(TextEditTransactionOutcome.NoChange, Editor(unchangedNode).LastTransaction);
    }

    // ================================================================ 辅助

    private static void AssertMerge(int advanceMilliseconds, bool merged)
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateField(binding.Binding);
        using var _app = app;

        Send(app, "a");
        Send(app, "b", advanceMilliseconds);

        Assert.Equal("ab", binding.Text);
        AssertHistory(node, undo: merged ? 1 : 2, redo: 0);

        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal(merged ? string.Empty : "a", binding.Text);
    }

    private static (TestApp App, FieldHost Host, ViewNode Node) CreateField(
        Binding<string> binding,
        int maxLength = int.MaxValue,
        FieldLayout layout = FieldLayout.Plain,
        TextHistoryCapacity? capacity = null,
        TextEditPlatformKind? platform = null,
        ITextInputService? service = null,
        Size? viewport = null)
    {
        var host = new FieldHost(binding, maxLength, layout);
        var app = new TestApp(
            host,
            viewport ?? new Size(400, 160),
            textInputService: service,
            historyCapacity: capacity,
            editPlatform: platform);
        app.Frame();
        var node = Focus(app, 0);
        return (app, host, node);
    }

    private static ViewNode Focus(TestApp app, int index)
    {
        var node = app.Runtime.Root.FindDescendants<TextField>().ElementAt(index);
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return node;
    }

    private static Point ClickPoint(ViewNode node) => new(node.Frame.X + 12, node.Frame.Y + (node.Frame.Height / 2));

    private static void Advance(TestApp app, int milliseconds)
        => app.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));

    private static void Send(TestApp app, string text, int advanceMilliseconds = 0)
    {
        if (advanceMilliseconds > 0)
        {
            Advance(app, advanceMilliseconds);
        }

        app.Runtime.DispatchTextInput(new TextInputEvent(text));
        app.Runtime.ProcessPendingWork();
        app.Frame();
    }

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    private static TextEditSession Editor(ViewNode node) => State(node).Editor;

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端（会话未建立）。");

    private static int Session(ViewNode node) => Client(node).Session.SessionId;

    private static void AssertHistory(ViewNode node, int undo, int redo)
    {
        var history = State(node).History;
        Assert.Equal(undo, history.UndoCount);
        Assert.Equal(redo, history.RedoCount);
    }

    private static void AssertApplied(TextInputApplyResult result, TextInputOutcome expected)
    {
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(expected), $"期望 {expected}，实际 {result.Outcome}（{result.RejectionReason}）。");
    }

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

    private static TextHistoryEntry Insert(string inserted, int start, TextEditKind kind)
        => new(start, string.Empty, inserted, TextSelection.Caret(start), TextSelection.Caret(start + inserted.Length), kind, TextEditMergeKind.None);

    private static TextHistoryEntry Typing(string inserted, int start)
        => new(start, string.Empty, inserted, TextSelection.Caret(start), TextSelection.Caret(start + inserted.Length), TextEditKind.Typing, TextEditMergeKind.TypingAppend);

    private static TextHistoryEntry Delete(TextEditMergeKind mergeKind, string deleted, int start)
        => new(start, deleted, string.Empty, TextSelection.Caret(start + deleted.Length), TextSelection.Caret(start), TextEditKind.Backspace, mergeKind);

    /// <summary>可编排拒绝/规范化/抛错的字符串绑定；身份稳定，便于跨重建验证。</summary>
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

        public Func<string, string>? Normalize { get; set; }

        public bool RejectWrites { get; set; }

        public bool ThrowBeforeMutation { get; set; }

        public bool ThrowAfterMutation { get; set; }

        public bool FailReadsAfterWrite { get; set; }

        public bool ThrowOnRead { get; set; }

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

    /// <summary>单输入框宿主：可选 Flow/Adaptive 包装与动态 MaxLength。</summary>
    private sealed class FieldHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly int _maxLength;
        private readonly FieldLayout _layout;

        public FieldHost(Binding<string> binding, int maxLength, FieldLayout layout)
        {
            _binding = binding;
            _maxLength = maxLength;
            _layout = layout;
        }

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;

            View field = new TextField(_binding) { MaxLength = _maxLength, Width = 200 };
            field = _layout switch
            {
                FieldLayout.Flow => new Flow(field),
                FieldLayout.Adaptive => new AdaptiveStack(500, field),
                _ => field,
            };

            return new VStack(field);
        }
    }

    /// <summary>状态绑定宿主：用于外部写入与同值重建。</summary>
    private sealed class StateFieldHost : Component
    {
        public StateFieldHost(string initial) => Initial = initial;

        public string Initial { get; }

        public State<string>? Text { get; private set; }

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Text = UseState("text", Initial);
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new VStack(new TextField(Text) { Width = 200 });
        }
    }

    /// <summary>可切换绑定来源并隐藏输入框的宿主。</summary>
    private sealed class SwitchableFieldHost : Component
    {
        public Binding<string> Current { get; set; } = Binding<string>.Constant(string.Empty);

        public bool ShowField { get; set; } = true;

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            var content = ShowField ? (View)new TextField(Current) { Width = 200 } : new Text("no field");
            return new VStack(content);
        }
    }

    /// <summary>动态 MaxLength 宿主。</summary>
    private sealed class DynamicMaxFieldHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly int _maxLength;

        public DynamicMaxFieldHost(Binding<string> binding, int maxLength)
        {
            _binding = binding;
            _maxLength = maxLength;
        }

        public State<int>? Max { get; private set; }

        protected override View Build()
        {
            Max = UseState("max", _maxLength);
            return new VStack(new TextField(_binding) { MaxLength = Max.Value, Width = 200 });
        }
    }

    /// <summary>两个独立输入框的宿主：用于验证 setter 内同步切焦点不串写历史。</summary>
    private sealed class TwoFieldHost : Component
    {
        public Binding<string> First { get; set; } = Binding<string>.Constant(string.Empty);

        public Binding<string> Second { get; set; } = Binding<string>.Constant(string.Empty);

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new VStack(
                new TextField(First) { Width = 200 },
                new TextField(Second) { Width = 200 });
        }
    }

    /// <summary>Responsive 分支宿主：窄宽显示输入框，宽宽显示文本（真正卸载输入框）。</summary>
    private sealed class ResponsiveFieldHost : Component
    {
        private readonly Binding<string> _binding;
        private readonly double _breakpoint;

        public ResponsiveFieldHost(Binding<string> binding, double breakpoint)
        {
            _binding = binding;
            _breakpoint = breakpoint;
        }

        protected override View Build()
            => new Responsive(info => info.AvailableWidth < _breakpoint
                ? (View)new TextField(_binding) { Width = 200 }
                : new Text("wide branch"));
    }

    /// <summary>记录调用并可编排同步回调的文本输入服务替身。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities { get; set; } = TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public int StartCount { get; private set; }

        public int EndCallCount { get; private set; }

        public bool StartSession(ITextInputClient client)
        {
            ArgumentNullException.ThrowIfNull(client);
            StartCount++;
            ActiveClient = client;
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
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.NotHandled(TextSnapshot.Empty);

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;

        public TextInputApplyResult Apply(params CompositionEvent[] events)
            => (ActiveClient ?? throw new InvalidOperationException("fake service has no active client"))
                .ApplyComposition(events);
    }
}
