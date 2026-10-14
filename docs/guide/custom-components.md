# 编写自定义组件

组件是普通 C# 类：继承 `Component` 并实现 `Build()`。首版不引入 XAML 与源生成器。

## 最小组件

```csharp
public sealed class Badge : Component
{
    private readonly string _text;

    public Badge(string text) => _text = text;

    protected override View Build()
        => new Text(_text)
            .Padding(6, 2)
            .Background(Color.Parse("#2563EB"))
            .Foreground(Color.White)
            .CornerRadius(4);
}
```

组件可以像内建视图一样参与组合与修饰：

```csharp
new HStack(new Badge("新建"), new Text("标题")).Spacing(8);
```

## 组件参数与更新

- 组件实例在每次父级 `Build()` 时重新创建；**节点身份由类型、位置或 Key 决定**，因此组件内的状态不会丢失。
- 通过构造函数传入的参数在重建时自然更新；`OnUpdate` 可用于根据新参数调整状态（例如把选择限制到新的数据范围）。
- 组件返回的描述树必须每次构建都重新创建，不要缓存并复用同一个描述实例来表达不同数据。

## 状态、绑定与事件的配合

```csharp
public sealed class SearchBox : Component
{
    private readonly State<string> _query;

    public SearchBox(State<string> query) => _query = query;

    protected override View Build()
        => new HStack(
            new TextField(_query.ToBinding(), "搜索"),
            new Button("清空", () => _query.Value = string.Empty))
        .Spacing(8);
}
```

- `UseState` 属于**声明它的组件**；父组件可以把 `State<T>` 或 `Binding<T>` 传给子组件复用。
- 控件绑定在测量/绘制阶段读取来源状态，因此绑定保存在字段中时依赖也能正确登记。
- 事件处理器在重建时被替换，旧回调不会被调用。

## 组合与派生内容

```csharp
public sealed class ListPanel : Component
{
    private readonly IReadOnlyList<string> _items;

    public ListPanel(IReadOnlyList<string> items) => _items = items;

    protected override View Build()
        => new ScrollView(
                new VStack(
                    new ForEach<string>(
                        _items,
                        (item, index) => new Text($"{index + 1}. {item}"),
                        (item, index) => item))
                    .Spacing(4))
            .Frame(height: 200);
}
```

## 后台工作

```csharp
public sealed class Loader : Component
{
    protected override View Build()
    {
        var status = UseState("status", "未开始");

        return new VStack(
            new Text(status.Value),
            new Button("加载", () => Task.Run(() =>
            {
                var data = Fetch();
                UiDispatcher.Post(() => status.Value = data);   // 回到 UI 线程
            }))).Spacing(8);
    }
}
```

不要在后台线程直接写状态；这会抛出 `UiThreadViolationException` 并给出使用 `UiDispatcher.Post` 的提示。

## 什么时候用 Key

- 列表项、可重排的集合、会插入/删除的分组：使用稳定 Key。
- 位置固定的静态内容：不需要 Key。

详见 [Key 与动态列表](keys-and-lists.md)。

## 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 状态在列表重排后错位 | 使用序号键 | 为项目提供稳定键 |
| 抛出“更新循环”诊断 | 在 `Build()` 内修改状态 | 把修改移到事件处理或 `UiDispatcher.Post` |
| 组件不更新 | 构建期没有读取该状态 | 在 `Build()` 中读取，或使用控件绑定 |
| 列表项状态被清空 | 节点类型或键变化导致替换 | 检查键的一致性并使用相同类型 |
