using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// 代码复审返修（docs/return/2026-10-14-code-review-follow-up.md）的回归测试：R10、R11。
/// </summary>
public class CodeReviewFollowUpTests
{
    // ---------------------------------------------------------------- R10

    [Fact]
    public void R10_KeyedCollection_SingleToManyAndBack_KeepsItemNodeAndState()
    {
        var host = new KeyedCollectionHost([1]);
        using var app = new TestApp(host, new Size(300, 200));
        app.Frame();

        var item1 = Item<KeyedItemComponent>(app, 1);
        item1.FindState<int>("value")!.Value = 42;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(42, item1.GetStateValue<int>("value"));

        // 单项 → 多项：项目 1 的键与视图类型没变，必须复用同一节点并保留状态。
        host.Items.Add(2);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(item1, Item<KeyedItemComponent>(app, 1));
        Assert.Equal(42, item1.GetStateValue<int>("value"));
        Assert.True(item1.IsMounted);

        // 多项 → 单项（再把项目 2 删掉）：仍然复用同一节点并保留更新后的状态。
        item1.FindState<int>("value")!.Value = 77;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        host.Items.Remove(2);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(item1, Item<KeyedItemComponent>(app, 1));
        Assert.Equal(77, item1.GetStateValue<int>("value"));
        Assert.True(item1.IsMounted);
    }

    [Fact]
    public void R10_KeyedCollection_RemoveAddAndReorder_KeepPerItemIdentity()
    {
        var host = new KeyedCollectionHost([1, 2, 3]);
        using var app = new TestApp(host, new Size(300, 200));
        app.Frame();

        var nodes = new Dictionary<int, ViewNode>
        {
            [1] = Item<KeyedItemComponent>(app, 1),
            [2] = Item<KeyedItemComponent>(app, 2),
            [3] = Item<KeyedItemComponent>(app, 3),
        };
        foreach (var (key, node) in nodes)
        {
            node.FindState<int>("value")!.Value = key * 10;
        }

        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 删掉中间项。
        host.Items.Remove(2);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(nodes[1], Item<KeyedItemComponent>(app, 1));
        Assert.Same(nodes[3], Item<KeyedItemComponent>(app, 3));
        Assert.DoesNotContain(app.Runtime.Root.FindDescendants<KeyedItemComponent>(), n => Equals(n.IdentityKey, 2));
        Assert.False(nodes[2].IsMounted);
        Assert.Equal(10, nodes[1].GetStateValue<int>("value"));
        Assert.Equal(30, nodes[3].GetStateValue<int>("value"));

        // 插入新项 + 重排：既有项目的身份与状态跟随键。
        host.Items.Clear();
        host.Items.AddRange([5, 3, 1]);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal([5, 3, 1], app.Runtime.Root.FindDescendants<KeyedItemComponent>().Select(n => (int)n.IdentityKey!));
        Assert.Same(nodes[1], Item<KeyedItemComponent>(app, 1));
        Assert.Same(nodes[3], Item<KeyedItemComponent>(app, 3));
        Assert.Equal(10, nodes[1].GetStateValue<int>("value"));
        Assert.Equal(30, nodes[3].GetStateValue<int>("value"));
        Assert.All(app.Runtime.Root.FindDescendants<KeyedItemComponent>(), n => Assert.True(n.Frame.Height > 0));
    }

    [Fact]
    public void R10_KeyedCollection_FocusAndEditState_SurviveAddRemove()
    {
        var host = new KeyedFieldCollectionHost([1]);
        using var app = new TestApp(host, new Size(400, 200));
        app.Frame();

        var field1 = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field1);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        app.Runtime.DispatchTextInput(new TextInputEvent("abc"));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var field1Node = Item<KeyedFieldItemComponent>(app, 1);
        Assert.Same(field1, field1Node.FindDescendants<TextField>().Single());
        Assert.Same(field1, app.Runtime.FocusedNode);
        Assert.Equal("abc", host.Values[1].PeekValue());

        // 增加一个项目：焦点与编辑状态必须保留在项目 1 上。
        host.Items.Add(2);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var field1After = Item<KeyedFieldItemComponent>(app, 1).FindDescendants<TextField>().Single();
        Assert.Same(field1, field1After);
        Assert.Same(field1, app.Runtime.FocusedNode);
        Assert.Equal("abc", host.Values[1].PeekValue());
        Assert.NotNull(app.Runtime.GetNextWakeup());

        // 删掉新增的项目：项目 1 依旧聚焦且内容不变。
        host.Items.Remove(2);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(field1, Item<KeyedFieldItemComponent>(app, 1).FindDescendants<TextField>().Single());
        Assert.Same(field1, app.Runtime.FocusedNode);
        Assert.Equal("abc", host.Values[1].PeekValue());
    }

    [Fact]
    public void R10_KeyedCollection_EmptySingleAndMany_LayoutCorrectly()
    {
        var host = new KeyedCollectionHost([]);
        using var app = new TestApp(host, new Size(300, 200));
        app.Frame();
        Assert.Empty(app.Runtime.Root.FindDescendants<KeyedItemComponent>());

        host.Items.Add(1);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var single = Item<KeyedItemComponent>(app, 1);
        Assert.True(single.Frame.Height > 0);

        host.Items.AddRange([2, 3]);
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(single, Item<KeyedItemComponent>(app, 1));
        var all = app.Runtime.Root.FindDescendants<KeyedItemComponent>().ToArray();
        Assert.Equal(3, all.Length);
        Assert.All(all, n => Assert.True(n.Frame.Height > 0));
        Assert.True(Item<KeyedItemComponent>(app, 2).Frame.Y >= single.Frame.Bottom);
        Assert.True(Item<KeyedItemComponent>(app, 3).Frame.Y >= Item<KeyedItemComponent>(app, 2).Frame.Bottom);
    }

    [Fact]
    public void R10_PlainSingleViewComponent_HasNoExtraContainerLayer()
    {
        var host = new PlainHost();
        using var app = new TestApp(host, new Size(200, 100));
        app.Frame();

        var hostNode = app.Runtime.Root.Require<PlainHost>();
        Assert.Single(hostNode.Children);
        Assert.IsType<VStack>(hostNode.Children[0].View);
    }

    // ---------------------------------------------------------------- R11

    [Fact]
    public void R11_RejectedInsertionWithSelection_KeepsTextSelectionAndCaret_OnInput()
    {
        var (app, host, node) = CreateField("ab", maxLength: 2);
        using var _ = app;

        SelectFirstGrapheme(app);
        AssertSelection(node, 0, 1);

        // 剩余容量 1，😀 需要 2 个码元：整体拒绝，文本与选区原样保留。
        app.Runtime.DispatchTextInput(new TextInputEvent("\U0001F600"));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("ab", host.Text!.PeekValue());
        AssertSelection(node, 0, 1);
    }

    [Fact]
    public void R11_RejectedInsertionWithSelection_KeepsTextSelectionAndCaret_OnPaste()
    {
        var (app, host, node) = CreateField("ab", maxLength: 2);
        using var _ = app;

        SelectFirstGrapheme(app);
        app.Clipboard.SetText("\U0001F600");
        app.Key(UiKey.V, KeyModifiers.Control);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("ab", host.Text!.PeekValue());
        AssertSelection(node, 0, 1);
    }

    [Fact]
    public void R11_RejectedCombiningCharacterWithSelection_KeepsText()
    {
        var (app, host, node) = CreateField("ab", maxLength: 2);
        using var _ = app;

        SelectFirstGrapheme(app);

        // 组合字符（e + U+0301）需要 2 个码元，同样整体拒绝。
        app.Runtime.DispatchTextInput(new TextInputEvent("e\u0301"));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("ab", host.Text!.PeekValue());
        AssertSelection(node, 0, 1);
    }

    [Theory]
    [InlineData(0, 1)]   // 选区在开头
    [InlineData(1, 2)]   // 选区在中间
    [InlineData(2, 3)]   // 选区在末尾
    public void R11_RejectionAtStartMiddleAndEnd_KeepsEverything(int selectionStart, int selectionEnd)
    {
        var (app, host, node) = CreateField("abc", maxLength: 3);
        using var _ = app;

        SelectRange(app, node, selectionStart, selectionEnd);
        AssertSelection(node, selectionStart, selectionEnd);

        // 每个位置的可插入容量都是 1，😀 放不下 → 拒绝且不删除选区。
        app.Runtime.DispatchTextInput(new TextInputEvent("\U0001F600"));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal("abc", host.Text!.PeekValue());
        AssertSelection(node, selectionStart, selectionEnd);
    }

    [Fact]
    public void R11_PartiallyFittingInsertion_StillAcceptedAndExplicitDeleteStillWorks()
    {
        // 部分可放入：容量 1 → 只插入 "X"，保留 "bcd"。
        var (app, host, node) = CreateField("abcd", maxLength: 4);
        using var _app = app;
        SelectFirstGrapheme(app);
        app.Runtime.DispatchTextInput(new TextInputEvent("XYZ"));
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal("Xbcd", host.Text!.PeekValue());

        // 显式删除仍按原语义执行（拒绝路径不能影响删除）。
        var (app2, host2, node2) = CreateField("ab", maxLength: 2);
        using var _app2 = app2;
        SelectFirstGrapheme(app2);
        app2.Key(UiKey.Backspace);
        app2.Runtime.ProcessPendingWork();
        app2.Frame();
        Assert.Equal("b", host2.Text!.PeekValue());
        AssertSelection(node2, 0, 0);

        // 显式剪切同样删除选区。
        var (app3, host3, _) = CreateField("ab", maxLength: 2);
        using var _app3 = app3;
        SelectFirstGrapheme(app3);
        app3.Key(UiKey.X, KeyModifiers.Control);
        app3.Runtime.ProcessPendingWork();
        app3.Frame();
        Assert.Equal("b", host3.Text!.PeekValue());
        Assert.Equal("a", app3.Clipboard.GetText());
    }

    // ---------------------------------------------------------------- helpers

    private static (TestApp App, MaxLengthHost Host, ViewNode Node) CreateField(string initial, int maxLength)
    {
        var host = new MaxLengthHost(maxLength, initial);
        var app = new TestApp(host, new Size(400, 60));
        app.Frame();
        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return (app, host, node);
    }

    /// <summary>Home + Shift+Right：选中第一个字素。</summary>
    private static void SelectFirstGrapheme(TestApp app)
    {
        app.Key(UiKey.Home);
        app.Key(UiKey.Right, KeyModifiers.Shift);
    }

    private static void SelectRange(TestApp app, ViewNode node, int start, int end)
    {
        app.Key(UiKey.Home);
        for (var i = 0; i < start; i++)
        {
            app.Key(UiKey.Right);
        }

        for (var i = start; i < end; i++)
        {
            app.Key(UiKey.Right, KeyModifiers.Shift);
        }
    }

    private static void AssertSelection(ViewNode node, int start, int end)
    {
        var state = Assert.IsType<TextFieldState>(node.InteractionState);
        Assert.Equal(start, state.SelectionStart);
        Assert.Equal(end, state.SelectionEnd);
    }

    private static ViewNode Item<TView>(TestApp app, int key)
        where TView : View
        => app.Runtime.Root.FindDescendants<TView>().Single(n => Equals(n.IdentityKey, key));
}

/// <summary>带键集合宿主：<c>Build()</c> 直接返回 <c>ForEach</c>（按项目 ID 作为稳定键）。</summary>
internal sealed class KeyedCollectionHost : Component
{
    public KeyedCollectionHost(IEnumerable<int> items) => Items = [.. items];

    public List<int> Items { get; }

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return new ForEach<int>(Items, (item, _) => new KeyedItemComponent(item), (item, _) => item);
    }
}

internal sealed class KeyedItemComponent : Component
{
    private readonly int _id;

    public KeyedItemComponent(int id) => _id = id;

    protected override View Build()
    {
        var value = UseState("value", 0);
        return new Text($"{_id}:{value.Value}");
    }
}

/// <summary>带键集合宿主：每个项目内含一个输入框（用于验证焦点与编辑状态）。</summary>
internal sealed class KeyedFieldCollectionHost : Component
{
    public KeyedFieldCollectionHost(IEnumerable<int> items) => Items = [.. items];

    public List<int> Items { get; } = [];

    public Dictionary<int, State<string>> Values { get; } = [];

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        Values.Clear();
        return new ForEach<int>(
            Items,
            (item, _) => new KeyedFieldItemComponent(item, this),
            (item, _) => item);
    }
}

internal sealed class KeyedFieldItemComponent : Component
{
    private readonly int _id;
    private readonly KeyedFieldCollectionHost _host;

    public KeyedFieldItemComponent(int id, KeyedFieldCollectionHost host)
    {
        _id = id;
        _host = host;
    }

    protected override View Build()
    {
        var text = UseState("text", string.Empty);
        _host.Values[_id] = text;
        return new VStack(new Text($"项目 {_id}"), new TextField(text.ToBinding()) { Width = 160 });
    }
}

/// <summary>返回单个普通视图的组件（用于确认没有引入额外容器层级）。</summary>
internal sealed class PlainHost : Component
{
    protected override View Build() => new VStack(new Text("a"), new Text("b"));
}
