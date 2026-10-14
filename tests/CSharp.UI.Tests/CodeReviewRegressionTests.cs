using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// 代码审核返修（docs/return/2026-10-14-code-review.md）的回归测试。
/// 每个测试对应一个编号问题，命名包含编号以便溯源。
/// </summary>
public class CodeReviewRegressionTests
{
    // ---------------------------------------------------------------- R01

    [Fact]
    public void R01_ManyComponents_UpdateInOnePass_WithoutFalseLoopDiagnostic()
    {
        var host = new BulkHost(120);
        using var app = new TestApp(host, new Size(400, 300));
        app.Frame();

        Assert.Equal(120, app.Runtime.All<BulkItemComponent>().Count());

        // 同时更新 120 个组件的状态：这是正常批量刷新，不能被误判为更新循环。
        foreach (var item in host.Items)
        {
            item.State!.Value = 1;
        }

        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.All(app.Runtime.All<BulkItemComponent>(), node =>
            Assert.Equal(1, node.GetStateValue<int>("value")));

        // 一轮内多次修改仍然合并为一次重建。
        var owner = Assert.IsType<ViewNode>(host.Items[0].State!.Owner);
        var builds = owner.Runtime.BuildCount;
        foreach (var item in host.Items)
        {
            item.State!.Value = 2;
            item.State.Value = 3;
        }

        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(3, host.Items[0].State!.Value);
        Assert.Equal(120, host.Items.Count(i => i.State!.Value == 3));
        var ownerBuilds = host.Items[0].State!.Owner?.Runtime.BuildCount ?? 0;
        Assert.True(ownerBuilds >= builds);
    }

    [Fact]
    public void R01_RealUpdateLoop_IsStillDetectedWithNodeDiagnostic()
    {
        var component = new LoopingComponent();
        using var app = new TestApp(component);

        component.External!.Value = 1;
        var exception = Assert.Throws<InvalidOperationException>(() => app.Runtime.ProcessPendingWork());

        Assert.Contains("更新循环", exception.Message);
        Assert.Contains("LoopingComponent", exception.Message);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("更新循环") || d.Contains("重复重建"));
    }

    // ---------------------------------------------------------------- R02

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(400)]
    public void R02_TextFieldWithoutWidth_HandlesNarrowLayouts(double available)
    {
        using var app = new TestApp(
            new VStack(new TextField(Binding<string>.Constant("文本"))),
            new Size(available, 120));
        var frame = app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        Assert.True(field.Frame.Width <= available + 0.001);
        Assert.True(field.Frame.Width >= 0);
        Assert.All(frame, command => Assert.True(double.IsFinite(command.GetBounds().Width)));
    }

    [Fact]
    public void R02_TextFieldWithPadding_RespectsParentConstraint()
    {
        using var app = new TestApp(
            new VStack(new TextField(Binding<string>.Constant("x")).Padding(20)),
            new Size(100, 120));
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        Assert.Equal(60, field.Frame.Width, 3);
    }

    [Fact]
    public void R02_TextFieldExplicitWidthAndTightConstraints_StillWork()
    {
        using var app = new TestApp(
            new VStack(new TextField(Binding<string>.Constant("x")) { Width = 300 }),
            new Size(150, 120));
        app.Frame();

        // 显式宽度受父级上限收缩，但不抛异常。
        var field = app.Runtime.Root.Require<TextField>();
        Assert.True(field.Frame.Width <= 150.001);

        using var tight = new TestApp(
            new VStack(new TextField(Binding<string>.Constant("tight"))),
            new Size(500, 120));
        tight.Frame();
        Assert.Equal(500, tight.Runtime.Root.Require<TextField>().Frame.Width, 3);
    }

    // ---------------------------------------------------------------- R03

    [Fact]
    public void R03_TexturesReferencedByCurrentFrame_SurviveEviction()
    {
        var component = new TwoTextHost();
        using var app = new TestApp(component, new Size(400, 200));
        app.Textures.Capacity = 1;

        var frame = app.Frame();
        var textures = frame.OfType<TextureCommand>().ToArray();

        // 同一帧内两个文本都引用了各自纹理：容量 1 也不能淘汰本帧尚未提交的纹理。
        Assert.Equal(2, textures.Length);
        Assert.All(textures, t => Assert.True(t.Texture.IsValid));
        Assert.Equal(2, app.Textures.LiveTextureCount);

        // 帧边界处回落容量：文本停止使用后，经过一个帧边界回收，存活数量回到容量上限。
        // （上一帧引用的纹理会保护到下一个帧边界，避免刚用过的纹理被重复上传。）
        component.Second = false;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        var next = app.Frame();

        Assert.Single(next.OfType<TextureCommand>());
        Assert.Equal(2, app.Textures.LiveTextureCount);

        app.Frame();
        Assert.Equal(1, app.Textures.LiveTextureCount);
    }

    // ---------------------------------------------------------------- R04

    [Fact]
    public void R04_UnkeyedNodes_MatchByStructuralPosition()
    {
        var host = new PositionSwapHost();
        using var app = new TestApp(host, new Size(300, 200));
        app.Frame();

        var cellBefore = app.Runtime.Root.Require<CellComponent>();
        Assert.Equal(1, cellBefore.GetStateValue<int>("value"));

        // [Cell(1), Text] → [Text, Cell(99)]：Cell 换了结构位置，必须使用新节点与初始值。
        host.Swapped = true;
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var cellAfter = app.Runtime.Root.Require<CellComponent>();
        Assert.NotSame(cellBefore, cellAfter);
        Assert.Equal(99, cellAfter.GetStateValue<int>("value"));
        Assert.False(cellBefore.IsMounted);
    }

    [Fact]
    public void R04_SameTypeSamePosition_KeepsState()
    {
        var host = new TwoCellHost();
        using var app = new TestApp(host, new Size(300, 200));
        app.Frame();

        var cells = app.Runtime.Root.FindDescendants<CellComponent>().ToArray();
        Assert.Equal(2, cells.Length);
        cells[0].FindState<int>("value")!.Value = 42;

        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var after = app.Runtime.Root.FindDescendants<CellComponent>().ToArray();
        Assert.Same(cells[0], after[0]);
        Assert.Same(cells[1], after[1]);
        Assert.Equal(42, after[0].GetStateValue<int>("value"));
    }

    // ---------------------------------------------------------------- R05

    [Fact]
    public void R05_MaxLength_DoesNotSplitGraphemes()
    {
        var host = new MaxLengthHost(maxLength: 1);
        using var app = new TestApp(host, new Size(300, 80));
        app.Frame();
        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);

        // 😀 需要 2 个 UTF-16 码元，容量 1 时整体拒绝，绝不写入半个代理项。
        app.Runtime.DispatchTextInput(new TextInputEvent("\U0001F600"));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(string.Empty, host.Text!.PeekValue());
        Assert.DoesNotContain('\uFFFD', host.Text.PeekValue());

        // 组合字符（e + U+0301）需要 2 个码元，同样整体拒绝。
        app.Runtime.DispatchTextInput(new TextInputEvent("e\u0301"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host.Text.PeekValue());
    }

    [Fact]
    public void R05_MaxLength_FitsWholeGraphemesOnly()
    {
        var host = new MaxLengthHost(maxLength: 4);
        using var app = new TestApp(host, new Size(300, 80));
        app.Frame();
        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);

        // 容量 4 恰好容纳 "a😀z"（1+2+1 个 UTF-16 码元）。
        app.Runtime.DispatchTextInput(new TextInputEvent("a\U0001F600z"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a\U0001F600z", host.Text!.PeekValue());

        // 容量 3：只能放下完整字素 "a😀"，末尾 z 被拒绝（不会写入半个字符）。
        var host3 = new MaxLengthHost(maxLength: 3);
        using var app3 = new TestApp(host3, new Size(300, 80));
        app3.Frame();
        app3.Runtime.SetFocus(app3.Runtime.Root.Require<TextField>());
        app3.Runtime.DispatchTextInput(new TextInputEvent("a\U0001F600z"));
        app3.Runtime.ProcessPendingWork();
        Assert.Equal("a\U0001F600", host3.Text!.PeekValue());

        // 容量 1：连一个 2 码元字素都放不下，整体拒绝。
        var host1 = new MaxLengthHost(maxLength: 1);
        using var app1 = new TestApp(host1, new Size(300, 80));
        app1.Frame();
        app1.Runtime.SetFocus(app1.Runtime.Root.Require<TextField>());
        app1.Runtime.DispatchTextInput(new TextInputEvent("\U0001F600"));
        app1.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, host1.Text!.PeekValue());
    }

    [Fact]
    public void R05_MaxLength_ReplacingSelectionKeepsUnselectedText()
    {
        var host = new MaxLengthHost(maxLength: 4, initial: "abcd");
        using var app = new TestApp(host, new Size(300, 80));
        app.Frame();
        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);

        // 选中 "a"（Home + Shift+Right），提交 3 个字符：容量 = 4 - (4-1) = 1 → 只插入 "X"，保留 "bcd"。
        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
        app.Runtime.DispatchTextInput(new TextInputEvent("XYZ"));
        app.Runtime.ProcessPendingWork();

        Assert.Equal("Xbcd", host.Text!.PeekValue());
    }

    [Fact]
    public void R05_MaxLength_PasteAndZeroCapacity_AreConsistent()
    {
        var host = new MaxLengthHost(maxLength: 4, initial: "ab");
        using var app = new TestApp(host, new Size(300, 80));
        app.Frame();
        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);

        // 光标移到末尾后粘贴：剩余容量 2，只插入 "XY"，保留原有 "ab"。
        app.Key(UiKey.End);
        app.Clipboard.SetText("XYZW");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("abXY", host.Text!.PeekValue());

        // 容量用尽：再无插入，也不破坏现有文本。
        app.Clipboard.SetText("ZZ");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("abXY", host.Text.PeekValue());

        // 剩余容量 1 时粘贴代理对会被整体拒绝。
        var host2 = new MaxLengthHost(maxLength: 3, initial: "ab");
        using var app2 = new TestApp(host2, new Size(300, 80));
        app2.Frame();
        app2.Runtime.SetFocus(app2.Runtime.Root.Require<TextField>());
        app2.Clipboard.SetText("\U0001F600");
        app2.Key(UiKey.V, KeyModifiers.Control);
        app2.Runtime.ProcessPendingWork();
        Assert.Equal("ab", host2.Text!.PeekValue());
    }

    // ---------------------------------------------------------------- R06

    [Fact]
    public void R06_DisablingFocusedControl_ClearsFocusAndBlink()
    {
        var host = new DisableHost();
        using var app = new TestApp(host, new Size(400, 120));
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        Assert.Same(field, app.Runtime.FocusedNode);
        Assert.NotNull(app.Runtime.GetNextWakeup());

        // 通过祖先 Disabled 修饰符禁用：重建 + 布局后焦点与光标唤醒都必须清理。
        host.Disabled!.Value = true;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.True(field.IsEffectivelyDisabled);
        Assert.Null(app.Runtime.FocusedNode);
        Assert.Null(app.Runtime.GetNextWakeup());
    }

    [Fact]
    public void R06_DisablingCapturedControl_ReleasesPointerCaptureAndPress()
    {
        var host = new DisableSliderHost();
        using var app = new TestApp(host, new Size(300, 80));
        app.Frame();

        var slider = app.Runtime.Root.Require<Slider>();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, slider.Frame.Center));
        Assert.True(slider.IsPointerCaptured);
        Assert.True(slider.IsPressed);

        host.Disabled!.Value = true;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.False(slider.IsPointerCaptured);
        Assert.False(slider.IsPressed);
    }

    // ---------------------------------------------------------------- R07

    [Fact]
    public void R07_BlinkWakeupCoversBothToggleBoundaries()
    {
        var host = new FieldHost();
        using var app = new TestApp(host, new Size(400, 80));
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.ProcessPendingWork();

        var interval = field.View is TextField textField ? textField.BlinkInterval : TimeSpan.FromMilliseconds(530);
        Assert.Equal(TimeSpan.FromMilliseconds(530), interval);

        // 第一次唤醒必须是半个周期（亮 → 灭），而不是整个周期。
        var first = app.Runtime.GetNextWakeup();
        Assert.NotNull(first);
        Assert.Equal(interval.TotalMilliseconds, first!.Value.TotalMilliseconds, 1);
        Assert.True(CaretVisible(app));

        // 推进到灭灯阶段：唤醒点仍为半个周期，且光标不可见。
        app.Clock.Advance(interval);
        app.Runtime.OnClockTick();
        var second = app.Runtime.GetNextWakeup();
        Assert.NotNull(second);
        Assert.Equal(interval.TotalMilliseconds, second!.Value.TotalMilliseconds, 1);
        Assert.False(CaretVisible(app));

        // 再推进半个周期：回到可见阶段。
        app.Clock.Advance(interval);
        app.Runtime.OnClockTick();
        Assert.True(CaretVisible(app));
    }

    // ---------------------------------------------------------------- R08

    [Fact]
    public void R08_RootForEach_LaysOutAllChildren()
    {
        View root = new ForEach<int>([1, 2, 3], (i, _) => new Text($"第 {i} 行"), (i, _) => i);
        using var app = new TestApp(root, new Size(200, 100));
        var frame = app.Frame();

        var texts = app.Runtime.Root.FindDescendants<Text>().ToArray();
        Assert.Equal(3, texts.Length);
        Assert.All(texts, node =>
        {
            Assert.True(node.Frame.Width > 0);
            Assert.True(node.Frame.Height > 0);
        });

        // 垂直堆叠：第二个子项在第一个下方。
        Assert.True(texts[1].Frame.Y >= texts[0].Frame.Bottom);
        Assert.True(texts[2].Frame.Y >= texts[1].Frame.Bottom);

        // 显式键不因隐式包装丢失。
        Assert.Equal([1, 2, 3], texts.Select(t => (int)t.IdentityKey!));

        // 可见性：确实生成了文本纹理命令。
        Assert.Equal(3, frame.OfType<TextureCommand>().Count());
    }

    [Fact]
    public void R08_RootForEach_HandlesEmptySingleAndReorder()
    {
        var host = new RootForEachHost();
        using var app = new TestApp(host, new Size(200, 100));
        app.Frame();

        // 单项。
        host.Items.Clear();
        host.Items.Add(7);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var single = app.Runtime.Root.FindDescendants<Text>().ToArray();
        Assert.Single(single);
        Assert.True(single[0].Frame.Width > 0);

        // 空集合不产生子项也不崩溃。
        host.Items.Clear();
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Empty(app.Runtime.Root.FindDescendants<Text>());

        // 重新添加并乱序：键身份跟随项目。
        host.Items.AddRange([3, 1, 2]);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var nodes = app.Runtime.Root.FindDescendants<Text>().ToArray();
        Assert.Equal([3, 1, 2], nodes.Select(n => (int)n.IdentityKey!));
        Assert.All(nodes, n => Assert.True(n.Frame.Height > 0));
    }

    private static bool CaretVisible(TestApp app)
    {
        var frame = app.Runtime.BuildFrame();
        var clip = frame.Commands.OfType<PushClipCommand>().LastOrDefault();
        return frame.Commands
            .OfType<RectangleCommand>()
            .Any(c => c.Bounds.Width <= 2 && c.Bounds.Height > 4 && (clip is null || clip.Bounds.IntersectsWith(c.Bounds)));
    }
}

/// <summary>批量组件宿主：每个子项是独立组件并持有自己的命名状态。</summary>
internal sealed class BulkItem
{
    public int Id { get; init; }

    public State<int>? State { get; set; }
}

internal sealed class BulkHost : Component
{
    public BulkHost(int count)
    {
        Items = [.. Enumerable.Range(0, count).Select(i => new BulkItem { Id = i })];
    }

    public List<BulkItem> Items { get; }

    protected override View Build()
    {
        var tick = UseState("tick", 0);
        _ = tick.Value;
        return new VStack(
            new ForEach<BulkItem>(
                Items,
                (item, _) => new BulkItemComponent(item),
                (item, _) => item.Id));
    }
}

internal sealed class BulkItemComponent : Component
{
    private readonly BulkItem _item;

    public BulkItemComponent(BulkItem item) => _item = item;

    protected override View Build()
    {
        var value = UseState("value", 0);
        _item.State = value;
        return new Text($"{_item.Id}:{value.Value}");
    }
}

/// <summary>未带键的位置交换宿主：<c>[Cell(1), Text]</c> ↔ <c>[Text, Cell(99)]</c>。</summary>
internal sealed class PositionSwapHost : Component
{
    public bool Swapped { get; set; }

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return Swapped
            ? new VStack(new Text("second"), new CellComponent(99))
            : new VStack(new CellComponent(1), new Text("second"));
    }
}

/// <summary>两个同类型同位置子项。</summary>
internal sealed class TwoCellHost : Component
{
    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return new VStack(new CellComponent(1), new CellComponent(2));
    }
}

internal sealed class CellComponent : Component
{
    private readonly int _initial;

    public CellComponent(int initial) => _initial = initial;

    protected override View Build()
    {
        var value = UseState("value", _initial);
        return new Text($"{value.Value}");
    }
}

/// <summary>带 MaxLength 的输入框宿主。</summary>
internal sealed class MaxLengthHost : Component
{
    private readonly int _maxLength;
    private readonly string _initial;

    public MaxLengthHost(int maxLength, string initial = "")
    {
        _maxLength = maxLength;
        _initial = initial;
    }

    public State<string>? Text { get; private set; }

    protected override View Build()
    {
        Text = UseState("text", _initial);
        return new VStack(new TextField(Text.ToBinding()) { MaxLength = _maxLength, Width = 200 });
    }
}

/// <summary>可切换禁用的输入框宿主。</summary>
internal sealed class DisableHost : Component
{
    public State<bool>? Disabled { get; private set; }

    protected override View Build()
    {
        Disabled = UseState("disabled", false);
        return new VStack(new TextField(Binding<string>.Constant("abc")) { Width = 200 })
            .Disabled(Disabled.Value);
    }
}

/// <summary>可切换禁用的滑块宿主。</summary>
internal sealed class DisableSliderHost : Component
{
    public State<bool>? Disabled { get; private set; }

    protected override View Build()
    {
        Disabled = UseState("disabled", false);
        return new VStack(new Slider(Binding<double>.Constant(0.4), 0, 1, 0.01, 200))
            .Disabled(Disabled.Value);
    }
}

/// <summary>根 ForEach 宿主（可改变项目集合）。</summary>
internal sealed class RootForEachHost : Component
{
    public List<int> Items { get; } = [1, 2, 3];

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return new ForEach<int>(Items, (item, _) => new Text($"第 {item} 行"), (item, _) => item);
    }
}

/// <summary>两个文本节点（用于验证帧内纹理保护）。</summary>
internal sealed class TwoTextHost : Component
{
    public bool Second { get; set; } = true;

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return Second
            ? new VStack(new Text("第一个文本"), new Text("第二个文本"))
            : new VStack(new Text("第一个文本"));
    }
}
