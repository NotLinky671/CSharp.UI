using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TextEditor persistent-gutter + bar-gesture 验收（SB-03）：真实 UiRuntime，
/// 覆盖 thumb drag/track press/cancel、wheel/bar 清 RequiresCaretReveal、geometry cancel。
/// </summary>
public class TextEditorScrollbarTests
{
    // TextEditor 默认 padding（产品常量；若改默认请同步更新此处）。
    private const double HorizontalPad = 10;
    private const double VerticalPad = 6;

    private static (TestApp App, ViewNode Editor, Binding<string> Binding) MakeEditor(
        double viewportHeight = 100)
    {
        var lines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}"));
        var binding = Binding<string>.From(() => lines, _ => { });
        var app = new TestApp(new TextEditor(binding), new Size(200, viewportHeight));
        app.Frame();
        return (app, app.Runtime.Root.Require<TextEditor>(), binding);
    }

    /// <summary>带 IME 假服务的生成（用于组合+scroll 场景）。</summary>
    private static (TestApp App, ViewNode Editor) MakeEditor(
        FakeTextInputService service,
        double viewportHeight = 100)
    {
        var lines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}"));
        var binding = Binding<string>.From(() => lines, _ => { });
        var app = new TestApp(new TextEditor(binding), new Size(200, viewportHeight),
            textInputService: service);
        app.Frame();
        return (app, app.Runtime.Root.Require<TextEditor>());
    }

    /// <summary>轨道槽 = frame − padding 的右侧 gutter（纵滚）。</summary>
    private static Rect TrackRect(ViewNode editor)
    {
        var f = editor.Frame;
        return new Rect(
            f.Right - HorizontalPad - ScrollbarMetrics.Thickness,
            f.Y + VerticalPad,
            ScrollbarMetrics.Thickness,
            f.Height - 2 * VerticalPad);
    }

    /// <summary>内容区 = frame − padding − right gutter。</summary>
    private static Rect InnerRect(ViewNode editor)
    {
        var f = editor.Frame;
        return new Rect(
            f.X + HorizontalPad,
            f.Y + VerticalPad,
            f.Width - 2 * HorizontalPad - ScrollbarMetrics.Thickness,
            f.Height - 2 * VerticalPad);
    }

    private static TextEditorState GetState(ViewNode editor)
        => (TextEditorState)editor.InteractionState!;

    // ------------------------------------------------------------ 布局 / 几何

    [Fact]
    public void InnerRect_SubtractsRightGutter_TrackOccupiesRightEdge()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;

        var frame = editor.Frame;
        var track = TrackRect(editor);
        var inner = InnerRect(editor);

        Assert.Equal(ScrollbarMetrics.Thickness, track.Width, 3);
        Assert.Equal(frame.Right - HorizontalPad, track.Right, 3);
        Assert.Equal(inner.Right + ScrollbarMetrics.Thickness, track.Right, 3);
    }

    // ------------------------------------------------------------ thumb drag / track press / cancel

    [Fact]
    public void ThumbDrag_WritesScrollY_ThenReleaseStops()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;
        var state = GetState(editor);

        var track = TrackRect(editor);
        var thumb = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: InnerRect(editor).Height,
            contentExtent: 600,
            offset: state.ScrollY);

        var grab = thumb.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 30)));

        Assert.True(state.Bar.IsDraggingBar, "thumb 拖拽应进入 bar gesture。");
        Assert.True(state.ScrollY > 0, "thumb drag 应推进 ScrollY。");
        Assert.False(state.RequiresCaretReveal, "bar-drag 应清 RequiresCaretReveal。");

        var after = state.ScrollY;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(grab.X, grab.Y + 30), PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 80)));
        Assert.Equal(after, state.ScrollY, 3);
    }

    [Fact]
    public void TrackClick_PagesOnce_AndClearsRequiresCaretReveal()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;
        var state = GetState(editor);

        var track = TrackRect(editor);
        var thumb = ScrollbarMetrics.ThumbRect(track, false, InnerRect(editor).Height, 600, state.ScrollY);
        var belowThumb = new Point(track.Center.X, thumb.Bottom + 5);
        app.Click(belowThumb);

        Assert.True(state.ScrollY > 0, "track After 应翻页推进 ScrollY。");
        Assert.False(state.RequiresCaretReveal);
    }

    [Fact]
    public void BarClick_DoesNotSetFocusOrMoveCaret()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;

        var track = TrackRect(editor);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, track.Center, PointerButton.Left));

        Assert.False(editor.IsFocused, "bar 点击不应夺焦。");
        var state = GetState(editor);
        Assert.False(state.IsDragging, "bar 点击不应进入文本拖选。");
        Assert.Null(state.DragPointer);
    }

    [Fact]
    public void BarCancel_ReleasesCaptureAndStopsDrag()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;
        var state = GetState(editor);

        var track = TrackRect(editor);
        var thumb = ScrollbarMetrics.ThumbRect(track, false, InnerRect(editor).Height, 600, 0);
        var grab = thumb.Center;

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 20)));
        var before = state.ScrollY;

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Cancel, grab));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 90)));

        Assert.Equal(before, state.ScrollY, 3);
        Assert.False(state.Bar.IsDraggingBar);
    }

    [Fact]
    public void Wheel_DuringComposition_ScrollsAndKeepsCompositionAlive()
    {
        var service = new FakeTextInputService();
        var (app, editor) = MakeEditor(service);
        using var _ = app;

        // 焦点 + 启组合。
        app.Runtime.SetFocus(editor);
        app.Runtime.ProcessPendingWork();
        var client = GetClient(editor);
        service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, GetDocVersion(editor),
            new TextRange(0, 0), "n", TextSelection.Caret(0)));
        app.Runtime.ProcessPendingWork();

        var state = GetState(editor);
        // 用户滚 → caret/preedit 离视口，组合必须保活。
        app.Runtime.DispatchWheel(new WheelEvent(editor.Frame.Center, 0, -5, KeyModifiers.None, WheelDeltaMode.Steps));
        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();

        Assert.True(state.ScrollY > 0, "wheel 应推进 ScrollY。");
        Assert.True(client.IsComposing, "wheel 不应取消组合。");
        Assert.False(state.IsDragging);
    }

    [Fact]
    public void SameLengthPreeditAdvance_RevealsCaretAfterUserScroll()
    {
        var service = new FakeTextInputService();
        var (app, editor) = MakeEditor(service);
        using var _ = app;

        app.Runtime.SetFocus(editor);
        app.Runtime.ProcessPendingWork();
        var client = GetClient(editor);

        var version = GetDocVersion(editor);
        service.Apply(CompositionEvent.Start(
            client.Session.SessionId, 0, 0, version,
            new TextRange(0, 0), "ni", TextSelection.Caret(0)));
        app.Runtime.ProcessPendingWork();

        var state = GetState(editor);
        // 用户滚开（caret 离视口）。
        app.Runtime.DispatchWheel(new WheelEvent(editor.Frame.Center, 0, -3, KeyModifiers.None, WheelDeltaMode.Steps));
        app.Runtime.ProcessPendingWork();
        var scrolledY = state.ScrollY;
        Assert.True(scrolledY > 0, "wheel 应已滚开 caret。");

        // 同长度 preedit 推进（"ni"→"ha"）：真实串变化 → publish 应重新 ensure caret。
        service.Apply(CompositionEvent.Update(
            client.Session.SessionId, 0, version, 1,
            new TextRange(0, 0), "ha", null, [new TextRange(0, 2)]));
        app.Runtime.ProcessPendingWork();

        Assert.True(client.IsComposing, "推进后组合仍活动。");
        Assert.True(state.ScrollY < scrolledY || state.ScrollY == 0,
            "preedit 推进应把 caret 重新 reveal（ScrollY 回落到 caret 可见带）。");
    }

    private static TextCompositionClient GetClient(ViewNode editor)
        => GetState(editor).Composing
           ?? throw new InvalidOperationException("会话未建立。");

    private static int GetDocVersion(ViewNode editor)
        => GetState(editor).Editor.Version;

    [Fact]
    public void GeometryChangeDuringBarDrag_CancelsGesture()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;
        var state = GetState(editor);

        var track = TrackRect(editor);
        var thumb = ScrollbarMetrics.ThumbRect(track, false, InnerRect(editor).Height, 600, 0);
        var grab = thumb.Center;

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        Assert.True(state.Bar.IsDraggingBar);

        // 视口变化 → Arrange 调 SyncGeometry 发现 track/extent 变化 → gesture 取消
        app.Runtime.SetViewport(new Size(250, 150), 1.0);
        app.Runtime.ProcessPendingWork();

        Assert.False(state.Bar.IsDraggingBar, "geometry 变化应取消 bar gesture。");
        var before = state.ScrollY;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 80)));
        Assert.Equal(before, state.ScrollY, 3);
    }

    [Fact]
    public void BarDrag_DoesNotEnterTextDragSelect()
    {
        var (app, editor, _) = MakeEditor();
        using var _ = app;
        var state = GetState(editor);

        var track = TrackRect(editor);
        var thumb = ScrollbarMetrics.ThumbRect(track, false, InnerRect(editor).Height, 600, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, thumb.Center, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(thumb.Center.X - 50, thumb.Center.Y + 20)));

        Assert.True(state.Bar.IsDraggingBar);
        Assert.False(state.IsDragging, "bar-drag 不应进入文本拖选。");
        Assert.Null(state.DragPointer);
    }
}

/// <summary>最小假服务（同 TextEditorCompositionTests 内 FakeTextInputService）。</summary>
internal sealed class FakeTextInputService : ITextInputService
{
    public TextInputCapabilities Capabilities { get; set; } = TextInputCapabilities.Full;
    public ITextInputClient? ActiveClient { get; private set; }
    public int StartCount { get; private set; }
    public TextInputGeometry LastPublished { get; private set; }
    public int PublishCount { get; private set; }

    public bool StartSession(ITextInputClient client)
    {
        StartCount++;
        ActiveClient = client;
        return true;
    }

    public bool EndSession() { ActiveClient = null; return true; }
    public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;
    public bool PublishGeometry(TextInputGeometry geometry)
    {
        LastPublished = geometry;
        PublishCount++;
        return true;
    }

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => ActiveClient?.ApplyComposition(events)
           ?? throw new InvalidOperationException("no active client");

    public TextInputApplyResult Apply(params CompositionEvent[] events)
        => ActiveClient?.ApplyComposition(events)
           ?? throw new InvalidOperationException("no active client");
}
