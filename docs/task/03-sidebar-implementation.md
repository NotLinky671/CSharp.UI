# PS-03 Sidebar\<T\> 实现

依据：[plan](../plan/2026-10-31-panel-sidebar-plan.md) §5/§7；契约清单见
[PS-01](01-contract-and-layout-rules.md)。
前置：PS-02（主审通过）。状态：**主审通过**——`Sidebar.cs`（tag 4EE4）与 runtime
接缝（tag AAFF）经主审 read 接受；强化后永久测试 **27 绿**；GL 原生 smoke
**16/16 PASS**（见 [复核记录](../return/2026-10-31-sidebar-review.md) 与
[证据目录](../return/2026-10-31-sidebar-review-evidence/)）。

## 目标

新增公开 `Sidebar<T>` + `SidebarItem<T>`（`src/CSharp.UI/Views/Sidebar.cs` 新文件）：
纵向选择导航，单选提交纪律与 `Select<T>` 同源，但为主树常驻控件（无 popup/overlay）。

## API（plan 冻结）

```csharp
public sealed class Sidebar<T> : View
{
    public Sidebar(Binding<T> value, IReadOnlyList<SidebarItem<T>> items,
                   string? header = null);
    public Binding<T> Value { get; }
    public IReadOnlyList<SidebarItem<T>> Items { get; }
    public string? Header { get; }
    public double Width { get; init; } = 200;   // 自然偏好，非硬宽
}
public sealed record SidebarItem<T>(T Value, string? Label = null, bool Disabled = false);
```

## 实现要求

- **数据**：`Items` 构造快照；`Value` 唯一（`EqualityComparer<T>.Default`，重复
  `ArgumentException`）；`Label==null` → `Value?.ToString() ?? ""`；`T` 可空合法。
- **布局**：Desired.Width = min(`Width`, 约束宽)（自然偏好，可 `Frame`/`Flex`/
  `FillWidth` 覆盖）；行高 `max(32, 文本行高)`；label 单行 ellipsis；
  header 为文本头非选中行。
- **状态**：owner `IsFocusable=true`；行非 focusable 不入 Tab 序；
  `InteractionState` 内部态持 active index（stable-by-value）。
- **提交**：pointer Up 同行 / `Enter`/`Space` → 写 `Value` 一次；同值重选仍写一次；
  重复 `Enter`/`Space` 忽略。
- **导航**：`Up`/`Down` 跳 disabled 两端停不环绕；`Home`/`End` 首/末非 disabled；
  只 move 不写；`Tab`/`Shift+Tab` 不消费；`Ctrl`/`Meta`/`Alt` 组合不导航/确认；
  重复方向键可持续移动。
- **不写**：items 空/全 disabled/Sidebar disabled → 不提交；未知当前值 → 无高亮
  不纠正；导航/焦点/外部绑定变化不写。
- **反应性**：`Value` 读取走 `Binding.Read(node)` 登记依赖；items 替换按 `Value`
  保 active；`Binding` 实例更换写当前；外部 `State`/Theme 现值驱动。
- **行身份（null/重复标签边界）**：items 含 `null` Value（如 `Sidebar<int?>` 的
  `[null, 0]`）时，`null` 的 key 回退**不得**与 `Value=0` 的默认 index 回退碰撞——
  实现需在 Sidebar 局部用非 null 的 null-key sentinel（**不改全局 `ForEach`/`Select`**
  语义），且**不可用 `ToString` 作 key**（不同值同 ToString 不冲突要求）；active
  移动后重排/删除/禁用/替换时身份按 `Value` 保持，`Up` 不提交别的项。
- **滚动**：不内置滚动容器；active **键盘**移动（`Up`/`Down`/`Home`/`End`）触发
  ensure-visible——仅键盘路径，用户滚轮/纯平移/外部绑定变化不重新 ensure
  （避免回弹）；作用于最近祖先 `ScrollView` 内目标行（已完全可见则不动），
  无祖先/空视口安全 no-op；单次有界兑现，不忙等/不新增 poll/timer/公共滚动 API。
  **具体挂点由实现者用既有 runtime 安全边界证明**（measure/arrange 代际与
  dirty 清理时序），不预写"Arrange 内改祖先 ScrollOffset"之类未证方案；
  无安全挂点如实报主审，不作 stub/不留"未来补"。
- **视觉**：hover/active/selected/disabled/focused 走 `ResolvedTheme` token
  （与 Button/Select 同族）；遵守父 clip 与显式 `.Clip()`。

## 验收（永久测试，消费者可观察）

- C1 数据：重复 `Value` 构造抛 `ArgumentException`；`Label=null` 回退 `ToString`；
  `Sidebar<string?>` 与 `null` Value 合法。
- C2 提交：pointer Up 同行写一次（事件计数恰好 1）；同值重选再写一次；
  `Enter`/`Space` 提交 active 写一次；重复 `Enter`/`Space` 不重复写。
- C3 导航：`Up`/`Down`/`Home`/`End` 只移动 active 不写值、跳过 disabled、
  两端停不环绕；`Tab`/`Shift+Tab` 不消费；`Ctrl/Alt/Meta`+方向/Enter 不导航不提交。
- C4 边界：items 空/全 disabled/Sidebar disabled 不写；未知值无高亮不纠正；
  disabled 翻转即时生效。
- C5 身份/反应性：items 重排/替换按 `Value` 保 active；`Binding` 实例更换后写新
  绑定当前值；外部 `State` 写入驱动 UI；Theme 现值。
- C6 焦点：owner 可聚焦、行不入 `GetFocusOrder`/Tab 序；focus 视觉只在 owner 级。
- C7 布局：自然宽 = `Width` 偏好、受父约束收缩；行高 ≥32 且随字号增大；
  label 长文本 ellipsis（排版请求验证）；header 不命中不导航。
- C8 裁剪/滚动：在调用方 `ScrollView` 内 active **键盘**移动触发 ensure-visible
  （目标行进入可视区，经可观察滚动偏移断言）；纯滚轮/平移/外部写值**不**触发
  ensure（无回弹）；无 `ScrollView` 祖先时键盘导航安全 no-op。
- C9 真身份边界（永久测试）：`Sidebar<int?>` items `[null, 0]`——`null` 行与
  `Value=0` 行各自独立命中/提交（null-key sentinel 不碰撞 index 回退）；两行
  同 `ToString` 的不同值不冲突。**指针手势跨 items 变更**：对某行
  `PointerEventKind.Down` 按下后，items 发生重排/删除/禁用/替换，再
  `PointerEventKind.Up` 释放——**禁止写回另一 Value**（命中行身份随 `Value`
  跟踪，错项不提交）。键盘 active（`Up`/`Down`/`Home`/`End` 移动）按 `Value`
  保持，另行断言其可观察选中高亮与写回值，不 pin 内部 VNode 实例。
- C10：不 pin 内部字段名/常量字面/源码字符串。
- 主审独立复审通过后开 PS-04。
