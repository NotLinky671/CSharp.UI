# Key 与动态列表

## 身份匹配规则

协调器把新的视图描述映射到已有的运行时节点：

1. 指定了 `Key`（或 `ForEach` 键）的同级条目按 **(键, 视图类型)** 匹配；
2. 未指定键的条目按 **视图类型 + 结构位置** 顺序匹配；
3. 匹配成功的节点复用同一实例，只更新属性、子项与事件处理器；
4. 键相同但类型不同的条目被视为替换：旧节点卸载、新节点创建。

## 为什么需要稳定的键

不稳定（按序号）的键在插入或删除后会把状态“错位”到相邻项目：

```csharp
// 反例：使用序号作为键
new ForEach<Todo>(items, item => new TodoRow(item))

// 正例：使用项目自身的稳定标识
new ForEach<Todo>(items, item => new TodoRow(item).Key(item.Id), item => item.Id)
```

第二种写法保证：重排、插入、删除后，每个 `TodoRow` 节点的输入框内容、焦点、滚动位置等状态仍属于原来的项目。

## ForEach 与 Key 的优先级

- `new ForEach<T>(items, builder, keySelector)`：键来自 `keySelector`。
- 构建函数内部再次调用 `.Key(...)` 时，以内部的 `Key` 为准（便于对派生视图分组）。
- 外层 `.Key(...)` 包裹 `ForEach` 时，外层键作为前缀与项目键组合，避免不同分组的同键冲突。

## 重复键诊断

同一父节点下出现相同 (键, 类型) 时会抛出 `DuplicateKeyException`，消息包含键、类型与父节点路径：

```
同级节点出现重复键 "same"（类型 LifecycleComponent，位于 Root / VStack / LifecycleComponent[same]）。
重复键会让协调器无法区分两个条目，请为每个项目提供唯一键……
```

静默串用状态通常都是重复键导致的，因此框架选择显式失败而不是猜一个匹配。

## 动态列表的写法

```csharp
var items = UseState("items", new List<TodoItem>());

// 增：赋一个新的列表实例，值比较（引用相等）判定为变化 → 触发重建
items.Value = [.. items.Value, new TodoItem(Guid.NewGuid(), draft.Value, false)];

// 删
items.Value = [.. items.Value.Where(i => i.Id != id)];

// 重排
var copy = new List<TodoItem>(items.Value);
(copy[a], copy[b]) = (copy[b], copy[a]);
items.Value = copy;
```

`State<List<T>>` 使用默认相等比较（引用相等），因此需要**替换整个列表实例**；这与不可变描述树的思路一致，也让“变化”判定简单可靠。

## 空列表与条件内容

`ForEach` 在集合为空时不产生任何节点；条件内容直接用普通 C# 条件表达式：

```csharp
new VStack(
    items.Value.Count == 0 ? new Text("还没有待办事项") : new ForEach<TodoItem>(...),
    showDetails ? new Text("详细信息") : new Spacer())
```

条件分支切换会卸载不再出现的节点（触发一次清理），并在需要时创建新节点。
