using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 12：单行 TextField 与 Unicode 编辑。</summary>
public class TextFieldTests
{
    private static (TestApp App, FieldHost Host, ViewNode Node) CreateField(string initial = "")
    {
        var host = new FieldHost(initial);
        var app = new TestApp(host, new Size(400, 60));
        app.Frame();
        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return (app, host, node);
    }

    private static void Type(TestApp app, string text)
    {
        app.Runtime.DispatchTextInput(new TextInputEvent(text));
        app.Runtime.ProcessPendingWork();
        app.Frame();
    }

    [Fact]
    public void CommittedTextAndChinesePaste_UpdateBinding()
    {
        var (app, host, _) = CreateField();
        using var _app = app;

        Type(app, "abc");
        Assert.Equal("abc", host.Text!.PeekValue());

        // 提交的中文文本与粘贴。
        Type(app, "中文");
        Assert.Equal("abc中文", host.Text.PeekValue());

        app.Clipboard.SetText("粘贴的内容");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("abc中文粘贴的内容", host.Text.PeekValue());

        // 单行规则过滤换行与制表符。
        Type(app, "x\ny\tz");
        Assert.Equal("abc中文粘贴的内容xyz", host.Text.PeekValue());
    }

    [Fact]
    public void ExternalBindingChange_UpdatesDisplay()
    {
        var (app, host, _) = CreateField();
        using var _app = app;

        host.Text!.Value = "外部设置";
        app.Runtime.ProcessPendingWork();
        var frame = app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        Assert.NotNull(field);
        Assert.Equal("外部设置", host.Text.PeekValue());
        Assert.Contains(frame, c => c is TextureCommand);
    }

    [Fact]
    public void SurrogatePairsAndCombiningCharacters_AreDeletedAsWholeGraphemes()
    {
        var (app, host, _) = CreateField();
        using var _app = app;

        // 😀 是代理对，末尾的组合字符 é 由 e + U+0301 组成。
        Type(app, "a\U0001F600e\u0301");
        var text = host.Text!.PeekValue();
        Assert.Equal("a\U0001F600e\u0301", text);

        // 退格删除组合字符整体。
        app.Key(UiKey.Backspace);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a\U0001F600", host.Text.PeekValue());

        // 再退格删除代理对整体，不会留下半个代理项。
        app.Key(UiKey.Backspace);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a", host.Text.PeekValue());

        // 行首 Backspace 无副作用；Delete 删除光标后的字素。
        app.Key(UiKey.Home);
        app.Key(UiKey.Backspace);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a", host.Text.PeekValue());

        app.Key(UiKey.Delete);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host.Text.PeekValue());
    }

    [Fact]
    public void EmptyStringAndCaretBounds_DoNotCorruptText()
    {
        var (app, host, _) = CreateField();
        using var _app = app;

        app.Key(UiKey.Backspace);
        app.Key(UiKey.Delete);
        app.Key(UiKey.Left);
        app.Key(UiKey.Right);
        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host.Text!.PeekValue());

        Type(app, "x");
        app.Key(UiKey.End);
        app.Key(UiKey.Backspace);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host.Text.PeekValue());
    }

    [Fact]
    public void ShiftSelection_SelectAllAndReplacementBehaveCorrectly()
    {
        var (app, host, _) = CreateField();
        using var _app = app;

        Type(app, "hello");
        app.Key(UiKey.End);

        // Shift+Left 选择最后一个字符。
        app.Key(UiKey.Left, KeyModifiers.Shift);
        var frame = app.Frame();
        Assert.Contains(frame, c => c is RectangleCommand { Fill: var fill } && fill == Theme.Light.SelectionBackground);

        // 输入替换选择。
        Type(app, "y");
        Assert.Equal("helly", host.Text!.PeekValue());

        // 全选后替换。
        app.Key(UiKey.A, KeyModifiers.Control);
        Type(app, "全部替换");
        Assert.Equal("全部替换", host.Text.PeekValue());
    }

    [Fact]
    public void CopyCutPaste_UseHostClipboard()
    {
        var (app, host, _) = CreateField("hello");
        using var _app = app;

        app.Key(UiKey.A, KeyModifiers.Control);
        app.Key(UiKey.C, KeyModifiers.Control);
        Assert.Equal("hello", app.Clipboard.GetText());

        app.Key(UiKey.X, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host.Text!.PeekValue());

        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("hello", host.Text.PeekValue());
    }

    [Fact]
    public void MouseClickAndDrag_SelectText()
    {
        var (app, host, node) = CreateField("abcdef");
        using var _app = app;

        // 点击定位光标：点击行首附近后输入，字符应插入到对应位置。
        var inner = new Rect(node.Frame.X + 10, node.Frame.Y + 2, node.Frame.Width - 20, node.Frame.Height - 4);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(inner.X + 1, inner.Center.Y)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(inner.X + 1, inner.Center.Y)));
        Type(app, "X");
        Assert.StartsWith("X", host.Text!.PeekValue());

        // 拖选：从行首拖到行尾后替换。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(inner.X, inner.Center.Y)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(inner.Right, inner.Center.Y)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(inner.Right, inner.Center.Y)));
        Type(app, "Z");
        Assert.Equal("Z", host.Text.PeekValue());
    }

    [Fact]
    public void LongText_KeepsCaretVisible_ByScrollingHorizontally()
    {
        var host = new FieldHost(new string('w', 200));
        using var app = new TestApp(host, new Size(200, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 光标移到末尾：必须出现横向滚动，且光标位于可视范围内。
        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();
        var frame = app.Runtime.BuildFrame();

        var pushes = frame.Commands.OfType<PushClipCommand>().ToArray();
        Assert.NotEmpty(pushes);

        var caret = frame.Commands
            .OfType<RectangleCommand>()
            .Where(c => c.Bounds.Width <= 2 && c.Bounds.Height > 4)
            .ToArray();
        Assert.NotEmpty(caret);
        Assert.InRange(caret[^1].Bounds.X, pushes[^1].Bounds.X, pushes[^1].Bounds.Right);
    }

    [Fact]
    public void Blink_IsRequestedOnlyWhileFocused()
    {
        var (app, _, node) = CreateField();
        using var _app = app;

        var wakeup = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeup);
        Assert.True(wakeup!.Value > TimeSpan.Zero);

        // 时钟推进后光标可见性翻转，说明闪烁状态按时间变化。
        app.Clock.Advance(TimeSpan.FromMilliseconds(600));
        app.Runtime.OnClockTick();
        Assert.True(app.Runtime.NeedsDraw);

        app.Runtime.SetFocus(null);
        Assert.Null(app.Runtime.GetNextWakeup());

        // 失焦后不再接受输入。
        var before = app.Runtime.Root.Require<TextField>();
        Assert.NotNull(before);
        Type(app, "ignored");
        Assert.Equal(string.Empty, ((FieldHost)app.Runtime.Root.View).Text!.PeekValue());
    }

    [Fact]
    public void Unmount_StopsInputAndBlink()
    {
        var host = new FieldHost();
        var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        Assert.NotNull(app.Runtime.GetNextWakeup());

        app.Runtime.Dispose();
        Assert.Null(app.Runtime.GetNextWakeup());

        app.Runtime.DispatchTextInput(new TextInputEvent("x"));
        Assert.Equal(string.Empty, host.Text!.PeekValue());
    }

    [Fact]
    public void Rebuild_KeepsCaretAndSelectionOnSameNode()
    {
        var host = new FieldHost("abcdef");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Key(UiKey.End);
        app.Key(UiKey.Left, KeyModifiers.Shift);

        host.ExternalTick!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(node, app.Runtime.Root.Require<TextField>());

        // 选择仍然有效：输入替换选中的字符。
        Type(app, "Z");
        Assert.Equal("abcdeZ", host.Text!.PeekValue());
    }
}
