using CSharp.UI;
using CSharp.UI.Samples;

namespace CSharp.UI.Samples.TodoList;

/// <summary>待办事项。</summary>
/// <param name="Id">稳定标识，用作 <c>ForEach</c> 的键。</param>
/// <param name="Title">标题。</param>
/// <param name="Done">是否完成。</param>
public sealed record TodoItem(Guid Id, string Title, bool Done);

/// <summary>
/// 待办列表示例：TextField + Binding + 带键 ForEach + 增删重排 + ScrollView，
/// 用于验证状态身份在列表变化后仍属于原项目。
/// </summary>
public sealed class TodoApp : Component
{
    protected override View Build()
    {
        var items = UseState("items", Seed());
        var draft = UseState("draft", string.Empty);
        var theme = UseState("theme", Theme.Light);

        var current = items.Value;
        var remaining = current.Count(i => !i.Done);

        return new VStack(
            new HStack(
                new Text("待办清单").WithFont(FontDescription.Default.WithSize(22)),
                new Spacer(),
                new Button(theme.Value.IsDark ? "切换到明亮主题" : "切换到暗色主题", () => theme.Value = theme.Value.IsDark ? Theme.Light : Theme.Dark)),
            new HStack(
                new TextField(draft.ToBinding(), "输入待办内容后回车或点击添加"),
                new Button("添加", () => items.Value = Add(current, draft.Value, draft)),
                new Button("清空已完成", () => items.Value = [.. current.Where(i => !i.Done)])),
            new Text($"剩余 {remaining} 项 / 共 {current.Count} 项").WithFont(FontDescription.Default.WithSize(12)),
            new ScrollView(
                new VStack(
                    new ForEach<TodoItem>(
                        current,
                        (item, _) => new TodoRow(
                            item,
                            onToggle: () => items.Value = [.. current.Select(i => i.Id == item.Id ? i with { Done = !i.Done } : i)],
                            onRemove: () => items.Value = [.. current.Where(i => i.Id != item.Id)],
                            onMoveUp: () => items.Value = Move(current, item.Id, -1),
                            onMoveDown: () => items.Value = Move(current, item.Id, 1)),
                        (item, _) => item.Id))
                    .Spacing(6))
                .Frame(height: 260),
            new Text("列表项使用稳定键；重排或删除后，每行的状态仍属于原来的项目。")
                .WithFont(FontDescription.Default.WithSize(12)))
        .Spacing(12)
        .Padding(20)
        .Background(theme.Value.Background)
        .Foreground(theme.Value.Foreground)
        .Theme(theme.Value);
    }

    private static List<TodoItem> Seed() =>
    [
        new TodoItem(Guid.NewGuid(), "阅读 docs/guide/getting-started.md", false),
        new TodoItem(Guid.NewGuid(), "运行控件展示示例中英文混排", true),
        new TodoItem(Guid.NewGuid(), "尝试键盘 Tab 与 Enter/Space 激活控件", false),
    ];

    private static List<TodoItem> Add(List<TodoItem> items, string title, State<string> draft)
    {
        var text = title.Trim();
        if (text.Length == 0)
        {
            return items;
        }

        draft.Value = string.Empty;
        return [.. items, new TodoItem(Guid.NewGuid(), text, false)];
    }

    private static List<TodoItem> Move(List<TodoItem> items, Guid id, int delta)
    {
        var index = items.FindIndex(i => i.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count)
        {
            return items;
        }

        var copy = new List<TodoItem>(items);
        (copy[index], copy[target]) = (copy[target], copy[index]);
        return copy;
    }
}

/// <summary>单行待办：完成的切换、标题与上移/下移/删除操作。</summary>
public sealed class TodoRow : Component
{
    private readonly TodoItem _item;
    private readonly Action _onToggle;
    private readonly Action _onRemove;
    private readonly Action _onMoveUp;
    private readonly Action _onMoveDown;

    public TodoRow(TodoItem item, Action onToggle, Action onRemove, Action onMoveUp, Action onMoveDown)
    {
        _item = item;
        _onToggle = onToggle;
        _onRemove = onRemove;
        _onMoveUp = onMoveUp;
        _onMoveDown = onMoveDown;
    }

    protected override View Build()
        => new HStack(
                new Toggle(new Binding<bool>(() => _item.Done, value => { if (value != _item.Done) { _onToggle(); } }), _item.Title),
                new Spacer(),
                new Button("上移", _onMoveUp),
                new Button("下移", _onMoveDown),
                new Button("删除", _onRemove))
            .Spacing(8);
}

internal static class Program
{
    private static int Main()
    {
        return UiApplication.Run(
            new TodoApp(),
            new UiApplicationOptions
            {
                Title = "CSharp.UI — 待办列表",
                Width = 760,
                Height = 480,
                FrameRendered = SmokeTestHook.CreateFrameHook("TodoList"),
            });
    }
}
