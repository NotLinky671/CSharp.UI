using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R13：DPI 缩放下的输入换算与交互命中（不创建窗口，直接驱动 <see cref="OpenTkInputAdapter"/>）。
/// </summary>
/// <remarks>
/// 宿主把“窗口坐标 → 逻辑坐标”的换算系数（R/D）交给适配器：Windows 150% 为 1/1.5，macOS Retina 为 1。
/// 这里用窗口坐标派发事件，验证按钮命中、Slider 拖动、TextField 光标与滚轮位置都落在同一套逻辑坐标上。
/// </remarks>
public sealed class DpiInputAdapterTests
{
    /// <summary>Windows 150%：窗口坐标是物理像素，内容缩放 1.5，R=1。</summary>
    private const double Windows150WindowToLogical = 1 / 1.5;

    [Fact]
    public void 一百五十百分比下鼠标窗口坐标换算为逻辑坐标()
    {
        var (adapter, _) = CreateAdapter(new Rectangle(Color.Red), new Size(300, 200), 1.5, Windows150WindowToLogical);

        adapter.HandleMouseMove(new MouseMoveEventArgs(150, 90, 0, 0));

        var move = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(new Point(100, 60), move.Position);
        // 原始窗口坐标被保留，便于 DPI 变化后重新解释。
        Assert.Equal(new Point(150, 90), adapter.PointerWindowPosition);
        Assert.Equal(new Point(100, 60), adapter.PointerPosition);
    }

    [Fact]
    public void Retina下鼠标坐标保持不变()
    {
        var (adapter, _) = CreateAdapter(new Rectangle(Color.Red), new Size(300, 200), 2.0, windowToLogical: 1.0);

        adapter.HandleMouseMove(new MouseMoveEventArgs(150, 90, 0, 0));

        Assert.Equal(new Point(150, 90), adapter.LastPointerEvent!.Value.Position);
    }

    [Fact]
    public void 一百五十百分比下按钮命中使用换算后的逻辑坐标()
    {
        var clicks = new List<string>();
        var root = new HStack(
            new Spacer(),
            new Button("hit", () => clicks.Add("hit")).Frame(width: 80, height: 40));
        var (adapter, runtime) = CreateAdapter(root, new Size(300, 200), 1.5, Windows150WindowToLogical);

        var button = runtime.Root.FindDescendant<Button>()!;
        Assert.True(button.Frame.X > 100, $"按钮应位于容器右侧，实际 {button.Frame}");
        var center = button.Frame.Center;

        DispatchClick(adapter, ToWindow(center));

        Assert.Equal(["hit"], clicks);
        // 换算后的逻辑坐标确实落在按钮矩形内。
        Assert.Same(button, runtime.HitTest(adapter.LastPointerEvent!.Value.Position));
    }

    [Fact]
    public void 按钮只按换算后的逻辑坐标命中()
    {
        var clicks = new List<string>();
        var root = new HStack(
            new Spacer(),
            new Button("hit", () => clicks.Add("hit")).Frame(width: 80, height: 40));
        var (adapter, runtime) = CreateAdapter(root, new Size(300, 200), 1.5, Windows150WindowToLogical);

        var button = runtime.Root.FindDescendant<Button>()!;
        var center = button.Frame.Center;

        // 修复前的行为：直接把原始窗口坐标当逻辑坐标使用（150% 下坐标偏移 1.5 倍），落不到按钮上。
        Assert.NotSame(button, runtime.HitTest(new Point(center.X * 1.5, center.Y * 1.5)));

        DispatchClick(adapter, ToWindow(center));
        Assert.Equal(["hit"], clicks);
    }

    [Fact]
    public void 一百五十百分比下滚轮使用换算后位置且增量不缩放()
    {
        var (adapter, _) = CreateAdapter(new Rectangle(Color.Red), new Size(300, 200), 1.5, Windows150WindowToLogical);

        adapter.HandleMouseMove(new MouseMoveEventArgs(150, 90, 0, 0));
        adapter.HandleWheel(new MouseWheelEventArgs(0.5f, -1f));

        var wheel = Assert.IsType<WheelEvent>(adapter.LastWheelEvent);
        Assert.Equal(new Point(100, 60), wheel.Position);
        // 滚轮增量是行/格语义，不随 DPI 缩放。
        Assert.Equal(0.5, wheel.DeltaX, 6);
        Assert.Equal(-1.0, wheel.DeltaY, 6);
    }

    [Fact]
    public void DPI变化后缓存指针按新比例重新解释()
    {
        var (adapter, _) = CreateAdapter(new Rectangle(Color.Red), new Size(400, 300), 1.5, Windows150WindowToLogical);

        adapter.HandleMouseMove(new MouseMoveEventArgs(150, 90, 0, 0));
        Assert.Equal(new Point(100, 60), adapter.PointerPosition);

        // 系统缩放从 150% 变为 200%：同一窗口坐标必须按新比例解释，且滚轮使用最新位置。
        adapter.UpdatePointerTransform(1 / 2.0, 1 / 2.0);
        Assert.Equal(new Point(75, 45), adapter.PointerPosition);

        adapter.HandleWheel(new MouseWheelEventArgs(0, -1f));
        Assert.Equal(new Point(75, 45), adapter.LastWheelEvent!.Value.Position);
    }

    [Fact]
    public void DPI变化后按下与释放使用同一套逻辑坐标()
    {
        var component = new SliderHost();
        var (adapter, runtime) = CreateAdapter(component, new Size(200, 60), 1.5, Windows150WindowToLogical);
        var slider = runtime.Root.FindDescendant<Slider>()!;

        var start = new Point(slider.Frame.X + 10, slider.Frame.Center.Y);
        DispatchDown(adapter, ToWindow(start));
        Assert.True(slider.IsPointerCaptured);

        // 拖动过程中缩放变化（例如窗口被拖到另一块显示器）：后续事件按新比例解释。
        adapter.UpdatePointerTransform(1.0, 1.0);
        DispatchMove(adapter, new Point(slider.Frame.Right + 500, start.Y));
        Assert.Equal(1.0, component.Value!.PeekValue(), 3);

        adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, GlfwNoModifiers.Value));
        Assert.False(slider.IsPointerCaptured);
    }

    [Fact]
    public void 一百五十百分比下Slider起点拖动到终点()
    {
        var component = new SliderHost();
        var (adapter, runtime) = CreateAdapter(component, new Size(200, 60), 1.5, Windows150WindowToLogical);
        var slider = runtime.Root.FindDescendant<Slider>()!;

        DispatchDown(adapter, ToWindow(new Point(slider.Frame.X + 2, slider.Frame.Center.Y)));
        // 拖动到控件左侧之外 → 0；再拖到右侧之外 → 1，全部用窗口坐标表达。
        DispatchMove(adapter, ToWindow(new Point(slider.Frame.X - 200, slider.Frame.Center.Y)));
        Assert.Equal(0.0, component.Value!.PeekValue(), 3);

        DispatchMove(adapter, ToWindow(new Point(slider.Frame.Right + 200, slider.Frame.Center.Y)));
        Assert.Equal(1.0, component.Value!.PeekValue(), 3);
    }

    [Fact]
    public void 一百五十百分比下TextField光标定位与百分之百一致()
    {
        var scaled = CreateTextFieldCaret(1.5, Windows150WindowToLogical);
        var unscaled = CreateTextFieldCaret(1.0, windowToLogical: 1.0);

        Assert.True(scaled.Caret > 0, "在文本框中段点击后光标应移动到文字内部。");
        Assert.Equal(unscaled.Caret, scaled.Caret);
    }

    [Fact]
    public void 一百五十百分比下TextField选区拖动与百分之百一致()
    {
        var scaled = CreateTextFieldSelection(1.5, Windows150WindowToLogical);
        var unscaled = CreateTextFieldSelection(1.0, windowToLogical: 1.0);

        Assert.True(scaled.SelectionStart < scaled.SelectionEnd, "从文本框左侧拖到右侧应产生选区。");
        Assert.Equal(unscaled.SelectionStart, scaled.SelectionStart);
        Assert.Equal(unscaled.SelectionEnd, scaled.SelectionEnd);
    }

    private static (int Caret, int SelectionStart, int SelectionEnd) CreateTextFieldCaret(double pixelScale, double windowToLogical)
    {
        var (adapter, runtime) = CreateAdapter(
            new TextField(Binding<string>.Constant("abcdef")),
            new Size(300, 40),
            pixelScale,
            windowToLogical);
        var node = runtime.Root.FindDescendant<TextField>()!;
        var target = new Point(node.Frame.X + (node.Frame.Width * 0.6), node.Frame.Center.Y);

        DispatchClick(adapter, ToWindow(target, windowToLogical));

        var state = Assert.IsType<TextFieldState>(node.InteractionState);
        return (state.Caret, state.SelectionStart, state.SelectionEnd);
    }

    private static (int Caret, int SelectionStart, int SelectionEnd) CreateTextFieldSelection(double pixelScale, double windowToLogical)
    {
        var (adapter, runtime) = CreateAdapter(
            new TextField(Binding<string>.Constant("abcdef")),
            new Size(300, 40),
            pixelScale,
            windowToLogical);
        var node = runtime.Root.FindDescendant<TextField>()!;
        var y = node.Frame.Center.Y;
        var end = ToWindow(new Point(node.Frame.Right - 2, y), windowToLogical);

        DispatchDown(adapter, ToWindow(new Point(node.Frame.X + 2, y), windowToLogical));
        DispatchMove(adapter, end);
        adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, GlfwNoModifiers.Value));

        var state = Assert.IsType<TextFieldState>(node.InteractionState);
        return (state.Caret, state.SelectionStart, state.SelectionEnd);
    }

    private static (OpenTkInputAdapter Adapter, UiRuntime Runtime) CreateAdapter(
        View root,
        Size viewport,
        double pixelScale,
        double windowToLogical)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
        });
        runtime.SetViewport(viewport, pixelScale);
        runtime.ProcessPendingWork();
        var adapter = new OpenTkInputAdapter(runtime, () => { });
        adapter.UpdatePointerTransform(windowToLogical, windowToLogical);
        return (adapter, runtime);
    }

    /// <summary>把逻辑坐标换算为 150% 下的窗口坐标（R=1、D=1.5）。</summary>
    private static Point ToWindow(Point logical) => new(logical.X * 1.5, logical.Y * 1.5);

    /// <summary>按给定换算系数把逻辑坐标换算为窗口坐标。</summary>
    private static Point ToWindow(Point logical, double windowToLogical) => new(logical.X / windowToLogical, logical.Y / windowToLogical);

    private static void DispatchClick(OpenTkInputAdapter adapter, Point windowPosition)
    {
        DispatchDown(adapter, windowPosition);
        adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, GlfwNoModifiers.Value));
    }

    private static void DispatchMove(OpenTkInputAdapter adapter, Point windowPosition)
        => adapter.HandleMouseMove(new MouseMoveEventArgs((float)windowPosition.X, (float)windowPosition.Y, 0, 0));

    private static void DispatchDown(OpenTkInputAdapter adapter, Point windowPosition)
    {
        DispatchMove(adapter, windowPosition);
        adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, GlfwNoModifiers.Value));
    }
}

/// <summary>带滑块与状态绑定的组件，用于验证 DPI 下的拖动数值。</summary>
internal sealed class SliderHost : Component
{
    public State<double>? Value { get; private set; }

    protected override View Build()
    {
        Value = UseState("value", 0.5);
        return new VStack(new Slider(Value.ToBinding(), 0, 1, 0.01, 200));
    }
}
