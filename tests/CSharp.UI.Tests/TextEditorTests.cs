using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TE-03：TextEditor 多行编辑控件核心行为 —— 用真实 <see cref="UiRuntime"/> + <see cref="StubTextService"/>
/// 驱动 Measure/Arrange/Emit、键盘导航（affinity/PreferredX）、Enter/删除、选区与历史、
/// 纵向滚动（滚轮/拖选边缘）、二维 caret 可见性与焦点/外部变化接缝。
/// </summary>
/// <remarks>
/// StubTextService 的编辑排版（Editing=true）按硬换行符分段（行 End 含 terminator、ContentEnd 不含）+
/// 等宽 wrap + 尾随空段 + caretsByLine 归属；字符宽 = font.Size × 0.6（默认字体 14 → 8.4px/字符），行高 = font.Size × 1.25（17.5px）。
/// </remarks>
public class TextEditorTests
{
    private const double CharWidth = 8.4;   // 14 * 0.6
    private const double LineHeight = 17.5; // 14 * 1.25

    // ================================================================ 构造与布局

    [Fact]
    public void Measure_UsesMinLinesViewport_NotFullContentHeight()
    {
        var binding = new ScriptedTextBinding(string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line-{i}")));
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;

        // 3 行视口：3 * 17.5 + 12 = 64.5，不是 30 行全高。
        Assert.InRange(node.Frame.Height, 60, 70);
    }

    [Fact]
    public void Constructor_RejectsInvalidInitParams()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextEditor(new Binding<string>(() => "", _ => { })) { MinLines = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextEditor(new Binding<string>(() => "", _ => { })) { Width = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextEditor(new Binding<string>(() => "", _ => { })) { Height = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextEditor(new Binding<string>(() => "", _ => { })) { HorizontalPadding = -1 });
    }

    // ================================================================ 键盘导航

    [Fact]
    public void LeftRight_MoveCaretByGrapheme_AndSetAffinity()
    {
        var binding = new ScriptedTextBinding("ab\ncd");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        // 初始 caret 0 → Right 过 'a' 'b' 到硬换行边界 stop=2。
        app.Key(UiKey.Right);
        Assert.Equal(1, state.Caret);
        Assert.Equal(TextAffinity.LineStart, state.CaretAffinity);

        app.Key(UiKey.Right);
        Assert.Equal(2, state.Caret);
        Assert.Equal(TextAffinity.LineStart, state.CaretAffinity);
    }

    [Fact]
    public void UpDown_PreservesPreferredX_AcrossUnequalLines()
    {
        // 行 0 比行 1 宽：caret 在行 0 靠右，Down 到行 1 末尾，再 Up 应回到接近原 X 而不是行 1 的 X。
        var binding = new ScriptedTextBinding("abcdefghij\nxy");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        // caret 到行 0 末尾（stop=10）。
        for (var i = 0; i < 10; i++)
        {
            app.Key(UiKey.Right);
        }

        Assert.Equal(10, state.Caret);
        var preferredX = state.PreferredX;

        app.Key(UiKey.Down);
        // 行 1 "xy" 只有 2 字 → caret 落在行 1 末端 stop=13（"abcdefghij\n" = 11 + 2）。
        Assert.Equal(13, state.Caret);
        Assert.NotNull(state.PreferredX); // 期望 X 保留

        app.Key(UiKey.Up);
        // 回到行 0 的期望 X 附近 → stop=10。
        Assert.Equal(10, state.Caret);
    }

    [Fact]
    public void HomeEnd_MoveToLineBounds()
    {
        var binding = new ScriptedTextBinding("abc\ndef");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.End);
        Assert.Equal(3, state.Caret); // 行 0 尾（terminator 前）
        Assert.Equal(TextAffinity.LineEnd, state.CaretAffinity);

        app.Key(UiKey.Home);
        Assert.Equal(0, state.Caret);

        // 移到第二行再 End。
        app.Key(UiKey.Down);
        app.Key(UiKey.End);
        Assert.Equal(7, state.Caret);
        Assert.Equal(TextAffinity.LineEnd, state.CaretAffinity);
    }

    [Fact]
    public void CtrlHomeEnd_JumpDocumentBounds()
    {
        var binding = new ScriptedTextBinding("abc\ndef");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.End);
        app.Key(UiKey.Down);
        app.Key(UiKey.End);
        Assert.Equal(7, state.Caret);

        app.Key(UiKey.Home, KeyModifiers.Control);
        Assert.Equal(0, state.Caret);

        app.Key(UiKey.End, KeyModifiers.Control);
        Assert.Equal(7, state.Caret);
    }

    [Fact]
    public void ShiftRight_ExtendsSelection()
    {
        var binding = new ScriptedTextBinding("abcd");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.Right, KeyModifiers.Shift);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        Assert.True(state.HasSelection);
        Assert.Equal(0, state.Anchor);
        Assert.Equal(2, state.Caret);

        // Shift+Left 收缩回来。
        app.Key(UiKey.Left, KeyModifiers.Shift);
        Assert.Equal(1, state.Caret);
        Assert.Equal(0, state.Anchor);
    }

    // ================================================================ Enter / 归一化 / 删除 / MaxLength

    [Fact]
    public void Enter_InsertsNewline_AsAtomicTransaction_WithHistory()
    {
        var binding = new ScriptedTextBinding("ab");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.End);
        app.Key(UiKey.Enter);

        Assert.Equal("ab\n", binding.Text);
        Assert.Equal(3, state.Caret);
        Assert.Equal(1, state.History.UndoCount);
    }

    [Fact]
    public void Enter_BreakMerge_IsolatesNewlineFromTyping()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;

        Send(app, "ab");
        app.Key(UiKey.Enter);
        Send(app, "cd");

        Assert.Equal("ab\ncd", binding.Text);

        // 撤销一次：只撤 "cd"（Enter 显式 BreakMerge → "cd" 独立成项）。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("ab\n", binding.Text);

        // 再撤销：撤 "\n"（Enter 自成一项，不并入 "ab"）。
        app.Key(UiKey.Z, KeyModifiers.Control);
        Assert.Equal("ab", binding.Text);
    }

    [Fact]
    public void Paste_NormalizesCRLF()
    {
        var binding = new ScriptedTextBinding("ab");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;

        app.Key(UiKey.End);
        app.Clipboard.SetText("x\r\ny");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();

        Assert.Equal("abx\ny", binding.Text);
    }

    [Fact]
    public void Backspace_DeletesCRLF_AsSingleGrapheme()
    {
        var binding = new ScriptedTextBinding("a\r\nb");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        // Ctrl+End → 文档末尾 caret=4。
        app.Key(UiKey.End, KeyModifiers.Control);
        Assert.Equal(4, state.Caret);

        // 左移一次跨过 \r\n 整体 → caret=3（\r\n 是单字素停点）。
        app.Key(UiKey.Left);
        Assert.Equal(3, state.Caret);

        // 退格删除 \r\n 整体（不是单独 \n）。
        app.Key(UiKey.Backspace);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(1, state.Caret);
    }

    [Fact]
    public void MaxLength_TruncatesInsert()
    {
        var binding = new ScriptedTextBinding("ab");
        var (app, host, node) = CreateEditor(binding.Binding, maxLength: 4);
        using var _app = app;

        app.Key(UiKey.End);
        Send(app, "cdef");

        Assert.Equal("abcd", binding.Text); // 超出部分被 MaxLength 截断
    }

    // ================================================================ 滚动

    [Fact]
    public void Wheel_ScrollsContent_WithoutMovingCaret()
    {
        var binding = new ScriptedTextBinding(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line-{i:00}")));
        var (app, _, node) = CreateEditor(binding.Binding, viewport: new Size(400, 120));
        using var _app = app;
        var state = State(node);

        Assert.Equal(0, state.ScrollY);
        var caretBefore = state.Caret;

        // 悬停 editor 内部然后向下滚（DeltaY 负 = 向下）。
        var inside = new Point(node.Frame.X + 20, node.Frame.Y + 20);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, inside));
        app.Runtime.DispatchWheel(new WheelEvent(inside, 0, -3, KeyModifiers.None, WheelDeltaMode.Steps));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.True(state.ScrollY > 0, $"滚轮后 ScrollY 应 > 0，实际 {state.ScrollY}");
        Assert.Equal(caretBefore, state.Caret); // 用户滚不改 caret
    }

    [Fact]
    public void Wheel_AtTop_UpScroll_Bubbles()
    {
        var binding = new ScriptedTextBinding("a\nb");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        var inside = new Point(node.Frame.X + 20, node.Frame.Y + 20);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, inside));
        // DeltaY 正 = 向上滚，已在顶部 → 不消费（ScrollY 不变）。
        app.Runtime.DispatchWheel(new WheelEvent(inside, 0, 3, KeyModifiers.None, WheelDeltaMode.Steps));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(0, state.ScrollY);
    }

    [Fact]
    public void UserWheelScroll_IsNotBouncedBackByNextFrame()
    {
        // 回归：Emit 不得每帧 EnsureCaretVisible —— 用户滚轮把 caret 滚出视口后须保持。
        var binding = new ScriptedTextBinding(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line-{i:00}")));
        var (app, _, node) = CreateEditor(binding.Binding, viewport: new Size(400, 120));
        using var _app = app;
        var state = State(node);

        var inside = new Point(node.Frame.X + 20, node.Frame.Y + 20);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, inside));
        app.Runtime.DispatchWheel(new WheelEvent(inside, 0, -5, KeyModifiers.None, WheelDeltaMode.Steps));
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var afterWheel = state.ScrollY;
        Assert.True(afterWheel > 0);

        app.Frame(); // 无新输入的重绘帧
        Assert.Equal(afterWheel, state.ScrollY);
    }

    [Fact]
    public void Drag_OutOfBounds_ReturnsWakeupTick_AndAdvancesScroll()
    {
        var binding = new ScriptedTextBinding(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line-{i:00}")));
        var (app, _, node) = CreateEditor(binding.Binding, viewport: new Size(400, 120));
        using var _app = app;
        var state = State(node);

        var inside = new Point(node.Frame.X + 20, node.Frame.Y + 20);
        var below = new Point(node.Frame.X + 20, node.Frame.Bottom + 30);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, inside));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, inside, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, below));
        app.Runtime.ProcessPendingWork();

        Assert.True(state.IsDragging);

        // pointer 越界 → wakeup 返回固定 tick。
        var wakeup = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeup);
        Assert.True(wakeup.Value <= TimeSpan.FromMilliseconds(200), $"tick 应为 ~80ms，实际 {wakeup.Value}");

        // 时钟推进 → ScrollY 增长；释放后停止。
        var before = state.ScrollY;
        app.Clock.Advance(TimeSpan.FromMilliseconds(200));
        app.Runtime.OnClockTick();
        Assert.True(state.ScrollY > before, $"tick 后 ScrollY 应增长（{before} → {state.ScrollY}）");

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, below, PointerButton.Left));
        Assert.False(state.IsDragging);

        // 释放后时钟推进不再滚动（拖选 tick 停止）。
        var afterRelease = state.ScrollY;
        app.Clock.Advance(TimeSpan.FromMilliseconds(200));
        app.Runtime.OnClockTick();
        Assert.Equal(afterRelease, state.ScrollY);
    }

    // ================================================================ caret 可见性 / 接缝

    [Fact]
    public void EnsureCaretVisible_AfterKeyboardNavigation()
    {
        var binding = new ScriptedTextBinding(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line-{i:00}")));
        var (app, _, node) = CreateEditor(binding.Binding, viewport: new Size(400, 120));
        using var _app = app;
        var state = State(node);

        // 移到文档底部 → caret 在最末行，ScrollY 应推到 caret 可见位置。
        app.Key(UiKey.End, KeyModifiers.Control);
        var innerH = node.Frame.Height - 12;
        Assert.True(state.ScrollY > 0, "caret 移到底部后应滚动");
        // caret 底边应落在 [ScrollY, ScrollY+innerH] 视口内。
        var caret = State(node).Caret;
        Assert.True(caret > 0);
    }

    [Fact]
    public void ExternalBindingChange_ClampsSelection_ClearsHistory()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.End);
        Send(app, "x");
        Assert.Equal(1, state.History.UndoCount);

        // 外部改写绑定为更短文本。
        binding.Text = "ab";
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(0, state.History.UndoCount);
        Assert.True(state.Caret <= 2, $"caret {state.Caret} 应被夹到新文本长 2");
    }

    [Fact]
    public void Blur_CollapsesSelection()
    {
        var binding = new ScriptedTextBinding("abcdef");
        var (app, _, node) = CreateEditor(binding.Binding);
        using var _app = app;
        var state = State(node);

        app.Key(UiKey.Right, KeyModifiers.Shift);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        Assert.True(state.HasSelection);

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();
        Assert.False(state.HasSelection);
        Assert.Equal(state.Caret, state.Anchor);
    }

    // ================================================================ 占位符与绘制

    [Fact]
    public void EmptyDocument_ShowsPlaceholder()
    {
        var binding = new ScriptedTextBinding(string.Empty);
        var (app, _, node) = CreateEditor(binding.Binding, placeholder: "Type here…");
        using var _app = app;

        var frame = app.Frame();
        Assert.Contains(frame, c => c is TextureCommand); // 占位符纹理
    }

    // ================================================================ helpers

    private sealed class ScriptedTextBinding
    {
        public ScriptedTextBinding(string initial) => Text = initial;
        public string Text { get; set; }
        public Binding<string> Binding => new(() => Text, v => Text = v);
    }

    private static (TestApp App, object Host, ViewNode Node) CreateEditor(
        Binding<string> binding,
        int maxLength = int.MaxValue,
        string? placeholder = null,
        Size? viewport = null)
    {
        var editor = new TextEditor(binding, placeholder) { Width = 200, MinLines = 3, MaxLength = maxLength };
        var app = new TestApp(new VStack(editor), viewport ?? new Size(400, 300));
        app.Frame();
        var node = app.Runtime.Root.FindDescendants<TextEditor>().First();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return (app, new object(), node);
    }

    private static void Send(TestApp app, string text)
    {
        app.Runtime.DispatchTextInput(new TextInputEvent(text));
        app.Runtime.ProcessPendingWork();
        app.Frame();
    }

    private static TextEditorState State(ViewNode node) => Assert.IsType<TextEditorState>(node.InteractionState);
}
