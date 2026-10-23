using CSharp.UI.Samples.ControlGallery;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08：在真实 <c>GalleryApp</c> 与 <c>NestedResponsiveExample</c> 上跨断点派发真实输入
/// （按钮 / Toggle 点击、Slider 拖动、Tab 焦点、TextField 中文输入与选区、双层滚轮），
/// 验证旧矩形与裁剪区外不再命中、光标真实位置可见、滚动边界随重排收敛、同一父节点状态不丢。
/// </summary>
/// <remarks>
/// 布局与控件都是示例真实实现，只替换平台服务（真实 Skia 排版）；输入经公开运行时入口派发。
/// </remarks>
public sealed class GalleryBreakpointInputTests
{
    private const double Epsilon = 0.01;

    private static readonly Size Wide = new(1024, 1600);

    private static readonly Size Narrow = new(320, 1600);

    [Fact]
    public void 主题按钮跨断点后旧矩形不再命中新矩形可点击()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var dark = FindButton(host, "暗色主题");
        var oldCenter = dark.Frame.Center;
        Assert.True(dark.Frame.Right > 600, $"宽模式下主题按钮应位于页头右侧，实际 {dark.Frame}。");
        host.Click(dark);
        Assert.NotNull(FindButtonOrDefault(host, "明亮主题"));

        host.Resize(Narrow, 1.0);

        var light = FindButton(host, "明亮主题");
        var newCenter = light.Frame.Center;
        Assert.True(
            !light.Frame.Contains(oldCenter),
            $"窄模式下主题按钮应移动，旧中心 {oldCenter} 不应仍在其新矩形 {light.Frame} 内。");
        Assert.NotSame(light, host.Runtime.HitTest(oldCenter));
        Assert.Same(light, host.Runtime.HitTest(newCenter));

        host.Click(light);
        Assert.NotNull(FindButtonOrDefault(host, "暗色主题"));
    }

    [Fact]
    public void 计数按钮跨断点保持身份与状态且仍可点击()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var counter = FindButton(host, "点击计数：0");
        host.Click(counter);
        Assert.NotNull(FindButtonOrDefault(host, "点击计数：1"));

        host.Resize(Narrow, 1.0);

        var moved = FindButton(host, "点击计数：1");
        Assert.Same(counter, moved);
        Assert.Same(moved, host.Runtime.HitTest(moved.Frame.Center));

        host.Click(moved);
        Assert.NotNull(FindButtonOrDefault(host, "点击计数：2"));
    }

    [Fact]
    public void 开关跨断点保持节点与状态()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var toggle = host.Runtime.Root.FindDescendant<Toggle>()!;
        var oldCenter = toggle.Frame.Center;
        host.Click(toggle);
        Assert.Contains("开关状态：关", StatusText(host));

        host.Resize(Narrow, 1.0);

        var moved = host.Runtime.Root.FindDescendant<Toggle>()!;
        Assert.Same(toggle, moved);
        Assert.Contains("开关状态：关", StatusText(host));
        Assert.Same(moved, host.Runtime.HitTest(moved.Frame.Center));

        host.Click(moved);
        Assert.Contains("开关状态：开", StatusText(host));
    }

    [Fact]
    public void 滑块跨断点拖动按新宽度映射取值()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var slider = host.Runtime.Root.FindDescendant<Slider>()!;
        var oldCenter = slider.Frame.Center;
        Assert.Same(slider, host.Runtime.HitTest(new Point(slider.Frame.Left + 1, slider.Frame.Center.Y)));

        // 从左向右拖动：Down 与 Move/Up 使用不同位置，最终值必须由 Move（指针捕获路径）驱动。
        DragSlider(host, slider, slider.Frame.Left + 1, slider.Frame.Right - 1);
        Assert.Contains("滑块值：1.00", StatusText(host));

        host.Resize(Narrow, 1.0);

        var moved = host.Runtime.Root.FindDescendant<Slider>()!;
        Assert.Same(slider, moved);
        Assert.True(moved.Frame.Width > 100, $"窄宽度下滑块仍应占满容器，实际 {moved.Frame}。");
        Assert.True(!moved.Frame.Contains(oldCenter), $"窄模式下滑块旧中心 {oldCenter} 不应仍在其新矩形 {moved.Frame} 内。");
        Assert.NotSame(moved, host.Runtime.HitTest(oldCenter));

        // 在新的实际几何上从最左拖到中点：取值必须按当前宽度映射，而不是沿用旧宽度。
        DragSlider(host, moved, moved.Frame.Left + 1, moved.Frame.Center.X);
        var value = SliderValue(host);
        Assert.InRange(value, 0.45, 0.55);

        // 拖动进行中跨断点：Down 在窄几何，Move/Up 在切换后的宽几何；
        // 指针捕获仍在滑块上，取值必须按新几何映射，且由 Move 驱动。
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(moved.Frame.Left + 1, moved.Frame.Center.Y)));
        host.Resize(Wide, 1.0);
        var wideSlider = host.Runtime.Root.FindDescendant<Slider>()!;
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(wideSlider.Frame.Right - 1, wideSlider.Frame.Center.Y)));
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(wideSlider.Frame.Right - 1, wideSlider.Frame.Center.Y)));
        host.Runtime.ProcessPendingWork();
        Assert.Same(moved, wideSlider);
        Assert.InRange(SliderValue(host), 0.95, 1.0);
    }

    [Fact]
    public void Tab焦点顺序跨断点保持声明顺序()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var wide = host.Runtime.GetFocusOrder().Select(node => node.DebugPath).ToArray();
        Assert.True(wide.Length >= 6, $"页面应有足够的可聚焦控件，实际 {wide.Length}。");

        host.Resize(Narrow, 1.0);
        var narrow = host.Runtime.GetFocusOrder().Select(node => node.DebugPath).ToArray();
        Assert.Equal(wide, narrow);

        // 用真实 Tab 键遍历：顺序与 GetFocusOrder 一致。
        host.Runtime.SetFocus(null);
        var visited = new List<string>();
        for (var i = 0; i < narrow.Length; i++)
        {
            host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Tab));
            visited.Add(host.Runtime.FocusedNode!.DebugPath);
        }

        Assert.Equal(narrow, visited);
    }

    [Fact]
    public void 输入框中文输入与选区跨断点保持且旧矩形不再命中()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), Wide, 1.0);

        var field = FindTextField(host, "请输入文本");
        var oldCenter = field.Frame.Center;
        host.Runtime.SetFocus(field);
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        host.Runtime.DispatchTextInput(new TextInputEvent("中文"));
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        host.Runtime.ProcessPendingWork();

        var state = Assert.IsType<TextFieldState>(field.InteractionState);
        var caret = state.Caret;
        var anchor = state.Anchor;
        Assert.True(state.HasSelection, "Shift+Left 后应存在选区。");
        Assert.EndsWith("中文", ((TextField)field.View).Text.Value, StringComparison.Ordinal);

        host.Resize(Narrow, 1.0);

        var moved = FindTextField(host, "请输入文本");
        Assert.Same(field, moved);
        Assert.Same(field, host.Runtime.FocusedNode);
        Assert.Same(state, field.InteractionState);
        Assert.Equal(caret, state.Caret);
        Assert.Equal(anchor, state.Anchor);
        Assert.EndsWith("中文", ((TextField)field.View).Text.Value, StringComparison.Ordinal);

        Assert.True(!moved.Frame.Contains(oldCenter), $"窄模式下输入框旧中心 {oldCenter} 不应仍在其新矩形 {moved.Frame} 内。");
        Assert.NotSame(moved, host.Runtime.HitTest(oldCenter));
        Assert.Same(moved, host.Runtime.HitTest(moved.Frame.Center));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 输入框跨断点缩窄后光标真实位置可见(double scale)
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(1024, 1600), scale);

        // 目标输入框在整页 ScrollView 内：先滚入视口再聚焦——视口外节点不产出任何绘制命令。
        var field = FindTextField(host, "试试中文输入法与 Ctrl+V 粘贴");
        var outer = host.Runtime.Root.FindDescendants<ScrollView>().First();
        BringInnerIntoView(host, outer, field);
        host.Runtime.SetFocus(field);
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));

        // 输入一段足以在 320 逻辑宽度下横向滚动的内容。
        host.Runtime.DispatchTextInput(new TextInputEvent(new string('文', 40)));
        host.Runtime.ProcessPendingWork();

        host.Resize(new Size(320, 1600), scale);

        var moved = FindTextField(host, "试试中文输入法与 Ctrl+V 粘贴");
        Assert.Same(field, moved);
        var movedOuter = host.Runtime.Root.FindDescendants<ScrollView>().First();
        Assert.Same(outer, movedOuter);
        BringInnerIntoView(host, movedOuter, moved);
        var state = Assert.IsType<TextFieldState>(moved.InteractionState);
        Assert.True(state.ScrollX > 0, $"{scale}：窄宽度下长文本应产生横向滚动，实际 ScrollX={state.ScrollX}。");

        var layout = LayoutFor(host, moved);
        var inner = InnerRect(moved.Frame);
        var textBounds = new Rect(
            inner.X - state.ScrollX,
            TextPainter.VerticalOrigin(inner, layout.Size),
            layout.Size.Width,
            layout.Size.Height);
        var origin = TextPainter.ResolveOrigin(textBounds, layout.Size, TextAlignment.Left, layout.PixelScale);
        var caretIndex = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, layout.Text.Length));
        var expectedX = origin.X + layout.GetCaretBounds(caretIndex, 1.5).X;

        var caret = CaretCommand(host, moved);
        var caretWidth = caret.Bounds.Width;

        // 真实位置（文字原点 + 排版 caret）必须落在内容区可见范围内……
        Assert.True(
            expectedX >= inner.Left - Epsilon && expectedX <= inner.Right + Epsilon,
            $"{scale}：光标真实位置 {expectedX} 应落在内容区 {inner} 内。");
        // ……且未被裁剪时与实际光标线精确一致；仅在裁剪边缘允许有界的夹取（光标宽度 + 半个物理像素）。
        var clampLimit = caretWidth + (0.5 / scale);
        Assert.True(
            Math.Abs(caret.Bounds.X - expectedX) <= clampLimit + Epsilon,
            $"{scale}：实际光标 {caret.Bounds.X} 与真实位置 {expectedX} 偏差应不超过 {clampLimit}。");
        Assert.True(
            caret.Bounds.Left >= inner.Left - Epsilon && caret.Bounds.Right <= inner.Right + Epsilon,
            $"{scale}：光标线必须完整可见：{caret.Bounds}，内容区 {inner}。");

        // 把光标移到中间的字素边界：真实位置不在裁剪边缘，必须与实际光标线完全一致（非夹取结果）。
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Home));
        for (var i = 0; i < 5; i++)
        {
            host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Right));
        }

        host.Runtime.ProcessPendingWork();
        var layoutMid = LayoutFor(host, moved);
        var originMid = TextPainter.ResolveOrigin(
            new Rect(inner.X - state.ScrollX, TextPainter.VerticalOrigin(inner, layoutMid.Size), layoutMid.Size.Width, layoutMid.Size.Height),
            layoutMid.Size,
            TextAlignment.Left,
            layoutMid.PixelScale);
        var caretIndexMid = layoutMid.SnapToCaretStop(Math.Clamp(state.Caret, 0, layoutMid.Text.Length));
        var expectedMid = originMid.X + layoutMid.GetCaretBounds(caretIndexMid, 1.5).X;
        var caretMid = CaretCommand(host, moved);
        Assert.Equal(expectedMid, caretMid.Bounds.X, 3);
    }

    [Fact]
    public void 双层滚轮跨断点先内层后外层且偏移随内容收敛()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(1024, 700), 1.0);
        var scrollViews = host.Runtime.Root.FindDescendants<ScrollView>().ToArray();
        Assert.Equal(2, scrollViews.Length);
        var outer = scrollViews[0];
        var inner = scrollViews[1];

        BringInnerIntoView(host, outer, inner);
        var outerBefore = outer.ScrollOffsetY;

        // 内层一格 → 48 逻辑像素，外层不动。
        host.Wheel(inner.Frame.Center, -1);
        Assert.Equal(48, inner.ScrollOffsetY, 3);
        Assert.Equal(outerBefore, outer.ScrollOffsetY, 3);

        host.Resize(new Size(320, 700), 1.0);

        var movedInner = host.Runtime.Root.FindDescendants<ScrollView>().ToArray()[1];
        Assert.Same(inner, movedInner);
        BringInnerIntoView(host, outer, movedInner);
        var contentHeight = movedInner.Children[0].DesiredSize.Height;
        var newMax = Math.Max(0, contentHeight - movedInner.Frame.Height);
        Assert.True(newMax > 0, "重排后内层内容仍应高于视口。");
        Assert.InRange(movedInner.ScrollOffsetY, 0, newMax + Epsilon);

        // 内层到底后，同一格的滚轮由整页外层接手；外层偏移不超过新内容范围。
        for (var i = 0; i < 500 && movedInner.ScrollOffsetY < newMax - Epsilon; i++)
        {
            host.Wheel(movedInner.Frame.Center, -1);
        }

        Assert.Equal(newMax, movedInner.ScrollOffsetY, 3);
        var outerOffsetBefore = outer.ScrollOffsetY;
        host.Wheel(movedInner.Frame.Center, -1);
        Assert.True(outer.ScrollOffsetY > outerOffsetBefore, "内层到底后外层必须接手。");

        // 滚到外层底部（窄宽度内容更高），再回到宽宽度：偏移必须收敛到新的（更小的）边界。
        ScrollOuterToBottom(host, outer);
        var narrowMax = Math.Max(0, outer.Children[0].DesiredSize.Height - outer.Frame.Height);
        Assert.Equal(narrowMax, outer.ScrollOffsetY, 3);

        host.Resize(new Size(1024, 700), 1.0);
        var wideMax = Math.Max(0, outer.Children[0].DesiredSize.Height - outer.Frame.Height);
        Assert.True(wideMax < narrowMax, $"宽模式内容应更矮：wide={wideMax} narrow={narrowMax}。");
        Assert.Equal(wideMax, outer.ScrollOffsetY, 3);
    }

    [Fact]
    public void 嵌套响应式示例跨断点保持外层状态与稳定输入节点()
    {
        using var host = new GalleryIntegrationHost(new NestedResponsiveExample(), new Size(1024, 900), 1.0);

        var stableFields = StableFields(host);
        Assert.Equal(2, stableFields.Length);
        var stable = stableFields[0];

        // 宽面板的 Responsive 目前选择横排分支；在其中的输入框里输入，写入外层共享状态。
        var branchField = host.Runtime.Root.FindDescendants<TextField>()
            .Single(node => ((TextField)node.View).Placeholder == "宽分支输入");
        host.Runtime.SetFocus(branchField);
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        host.Runtime.DispatchTextInput(new TextInputEvent("外层State"));
        host.Runtime.ProcessPendingWork();
        var branchRect = branchField.Frame;

        // 稳定输入节点：输入、选区，随后跨断点必须保留同一节点与编辑态。
        host.Runtime.SetFocus(stable);
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        host.Runtime.DispatchTextInput(new TextInputEvent("稳定"));
        host.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        host.Runtime.ProcessPendingWork();
        var stableState = Assert.IsType<TextFieldState>(stable.InteractionState);
        var caret = stableState.Caret;
        var anchor = stableState.Anchor;

        // 缩到断点以下：宽面板的 Responsive 切换到窄分支（结构变化），稳定 AdaptiveStack 只是换排列。
        host.Resize(new Size(400, 900), 1.0);

        Assert.All(
            host.Runtime.Root.FindDescendants<Responsive>(),
            node => Assert.IsType<VStack>(node.Children[0].View));

        // 两个面板与「稳定输入」共享同一个外层 State，因此它同时包含两处输入。
        var state = host.Runtime.Root.GetStateValue<string>("name");
        Assert.Contains("外层State", state, StringComparison.Ordinal);
        Assert.EndsWith("稳定", state, StringComparison.Ordinal);

        // 稳定输入节点身份、焦点、选区与文本全部保留（同一父节点不丢编辑态）。
        var movedStable = StableFields(host)[0];
        Assert.Same(stable, movedStable);
        Assert.Same(stable, host.Runtime.FocusedNode);
        Assert.Same(stableState, stable.InteractionState);
        Assert.Equal(caret, stableState.Caret);
        Assert.Equal(anchor, stableState.Anchor);
        Assert.EndsWith("稳定", ((TextField)stable.View).Text.Value, StringComparison.Ordinal);

        // 旧分支矩形在结构切换后不再命中该分支输入框。
        Assert.NotSame(branchField, host.Runtime.HitTest(branchRect.Center));
        Assert.DoesNotContain(
            host.Runtime.Root.FindDescendants<TextField>(),
            node => ReferenceEquals(node, branchField));

        // 两个面板切换后都选择窄分支，输入框读取同一外层文本。
        var narrowBranchFields = host.Runtime.Root.FindDescendants<TextField>()
            .Where(node => ((TextField)node.View).Placeholder == "窄分支输入")
            .ToArray();
        Assert.Equal(2, narrowBranchFields.Length);
        Assert.All(
            narrowBranchFields,
            node => Assert.EndsWith("稳定", ((TextField)node.View).Text.Value, StringComparison.Ordinal));
    }

    private static void DragSlider(GalleryIntegrationHost host, ViewNode slider, double downX, double moveX)
    {
        var y = slider.Frame.Center.Y;
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(downX, y)));
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(moveX, y)));
        host.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(moveX, y)));
        host.Runtime.ProcessPendingWork();
    }

    private static double SliderValue(GalleryIntegrationHost host)
    {
        var text = StatusText(host);
        var marker = "滑块值：";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOf('，', start);
        return double.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void BringInnerIntoView(GalleryIntegrationHost host, ViewNode outer, ViewNode inner)
    {
        var outerOnly = new Point(outer.Frame.X + 8, outer.Frame.Y + 6);
        for (var i = 0; i < 400 && !FullyInside(inner.Frame, outer.Frame); i++)
        {
            host.Wheel(outerOnly, -1);
        }

        Assert.True(FullyInside(inner.Frame, outer.Frame), $"外层滚动后内层视口应完全可见：{inner.Frame} / {outer.Frame}。");
    }

    private static void ScrollOuterToBottom(GalleryIntegrationHost host, ViewNode outer)
    {
        var outerOnly = new Point(outer.Frame.X + 8, outer.Frame.Y + 6);
        for (var i = 0; i < 400; i++)
        {
            var before = outer.ScrollOffsetY;
            host.Wheel(outerOnly, -1);
            if (Math.Abs(outer.ScrollOffsetY - before) < Epsilon)
            {
                break;
            }
        }
    }

    private static bool FullyInside(Rect target, Rect clip)
        => !target.IsEmpty
            && target.X >= clip.X - Epsilon
            && target.Y >= clip.Y - Epsilon
            && target.Right <= clip.Right + Epsilon
            && target.Bottom <= clip.Bottom + Epsilon;

    private static Rect InnerRect(Rect frame)
        => frame.Deflate(new Thickness(10, 0)).Deflate(new Thickness(0, 2));

    private static TextLayoutResult LayoutFor(GalleryIntegrationHost host, ViewNode field)
    {
        var view = (TextField)field.View;
        var font = field.Environment.Font ?? FontDescription.Default;
        var text = view.Text.Value;
        return host.TextService.Layout(
            TextPainter.CreateRequest(text, font, double.PositiveInfinity, field.Runtime.PixelScale));
    }

    private static RectangleCommand CaretCommand(GalleryIntegrationHost host, ViewNode field)
    {
        var theme = field.Environment.ResolvedTheme;
        var caretColor = field.IsEffectivelyDisabled ? theme.DisabledForeground : theme.Foreground;
        var candidates = host.Runtime.BuildFrame().Commands
            .OfType<RectangleCommand>()
            .Where(command => command.CornerRadius == 0
                && command.Stroke is null
                && command.StrokeWidth == 0
                && command.Fill == caretColor
                && field.Frame.Contains(command.Bounds.Center))
            .ToArray();
        return Assert.Single(candidates);
    }

    private static ViewNode FindButton(GalleryIntegrationHost host, string label)
        => host.Runtime.Root.FindDescendants<Button>().Single(node => ((Button)node.View).Label == label);

    private static ViewNode? FindButtonOrDefault(GalleryIntegrationHost host, string label)
        => host.Runtime.Root.FindDescendants<Button>().SingleOrDefault(node => ((Button)node.View).Label == label);

    private static ViewNode FindTextField(GalleryIntegrationHost host, string placeholder)
        => host.Runtime.Root.FindDescendants<TextField>()
            .Single(node => ((TextField)node.View).Placeholder == placeholder);

    private static ViewNode[] StableFields(GalleryIntegrationHost host)
        => host.Runtime.Root.FindDescendants<TextField>()
            .Where(node => ((TextField)node.View).Placeholder == "稳定输入（切换布局后继续编辑）")
            .ToArray();

    private static string StatusText(GalleryIntegrationHost host)
        => ((Text)host.Runtime.Root.FindDescendants<Text>()
            .Single(node => ((Text)node.View).Content.StartsWith("开关状态", StringComparison.Ordinal)).View).Content;
}
