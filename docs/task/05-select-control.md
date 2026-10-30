# P-05 Select\<T\> 控件

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §6。
前置：P-04（已主审通过）。状态：**已实现并经主审独立复核通过**（记录见 [P-05 审核](../return/2026-10-29-popup-select-p05.md)，原始输出在 `2026-10-29-popup-select-p05-evidence/`）。

## 目标

公开 `Select<T>`（core `CSharp.UI`，新文件 `Views/Select.cs`——避免 `Controls.cs`
继续膨胀）+ `SelectItem<T>`：绑定当前值、选项、placeholder、disabled、限高内部滚动、
完整键盘、选后关闭、焦点恒在 trigger。

## API（plan 冻结）

```csharp
public sealed class Select<T> : View
{
    public Select(Binding<T> value, IReadOnlyList<SelectItem<T>> items,
                  string? placeholder = null);
    public double MaxPopupHeight { get; init; } = 240;
}
public sealed record SelectItem<T>(T Value, string? Label = null, bool Disabled = false);
```

- 无 `Binding<T?>` 包装——`T?` 对值类型无 `Nullable<T>` 语义；可清空由调用方
  `T` 取本身可空类型（`SelectItem<T>(null)` 合法）。
- **构造校验**：`items` 内 `Value` 按 `EqualityComparer<T>.Default` **唯一**——
  重复 → `ArgumentException`（数据错误显式抛，不静默 first-wins 错选；
  `T` 可空时 `null` 也合法 Value）。`Label==null` → `Value?.ToString() ?? ""`。
- **不开条件**（触发无论 pointer Up-activation 或键盘 activate 统一判）：
  items 空 → 不打开；items 全 Disabled → 不打开；`Select` 整体 disabled →
  不打开不可点。
- **当前值显示**：值不在 items → placeholder 灰化（不写回绑定）；
  值在但该项 Disabled → **仍显示该项 Label**（已选显示合法——Disabled 只影响
  "打开后能否再选该项/active 跳过"，不影响回显；非"未选中"态）。
- **同值重选** → 仍写绑定一次（不 diff）。
- **items 替换**：active 按 equality 重定位同值（stable-by-value，不留 stale index；
  新列表找不到→第一个非 disabled）；disabled 翻转即时生效。

## 行为契约

- **trigger**：普通控件命中区（`IsFocusable=true`），显示当前 Label 或 placeholder
  （灰化）；右侧下拉箭头；常态/hover/pressed/focused/disabled 视觉全走
  `ResolvedTheme`（同 Button/Toggle 族 token 沿用）。
- **打开 = Up(同点)**（P-04 Up-activation）；关时 Space/Enter(Down)/Up/Down 方向键
  也可直接打开（键盘表下行）——pointer 路径统一 Up-activation。
- **popup**：`VStack` 行项；超 `MaxPopupHeight` 内套 `ScrollView`（垂直，Standard
  scrollbar）；宽 = max(trigger 宽, 内容自然宽) 后由 placement clamp；`Disabled` 行
  灰化、不可 hover/激活、键选跳过；active 行高亮（`InteractionState` 内部态，
  非 focusable 子节点）。
- **键盘**（plan §6 冻结表，trigger `OnKeyDown` 处理，弹层项不入 Tab）：
  关：Enter/Space(Down)/Up/Down 打开并初定位 active（当前选中或首/末非 disabled）；
  Tab/Shift+Tab 正常遍历。
  开：Up/Down 移 active（跳 disabled、两端停不环绕）；Home/End 首/末非 disabled；
  Enter/Space 提交→写→关→焦留 trigger；Esc 关不提交（P-04 路径）；Tab 关弹层
  →事件不消费→主树 Move 前移（Shift+Tab 反向）；Alt+Down 不实现。
- **active ensure-visible**：active 行移出 popup `ScrollView` 可视区时内部计算
  ScrollOffset 使其可见（internal 逻辑，不新增公共滚动 API）。
- **提交纪律**：绑定写仅在选后路径恰好一次（pointer Up 同 item / Enter）；
  打开/关闭/Esc/Tab/外点/失活不写。
- **关闭汇总**：选后、Esc、外点、锚卸载、`anchorVisible` 空、锚 disabled、
  窗口失活——**唯一机制**：Select `InteractionState` 持 `open` flag + active
  index；打开/关闭统一调 `OverlayManager.Show/Close`（与公开 `Popup` 共用
  同一 overlay 生命周期/关闭路径）；`AnchorNode` = trigger 节点；
  SavedFocus = trigger；内部 flag 驱动（不走 `Binding<bool>`）；
  **内部关闭路径（manager 侧 `Close`）→ Select 同步清 open flag**
  （单处回调——状态随 entry 一致，不留 stale open）。
- **binding 追踪**：`Select` 对 `Value` 的读取走 `Binding.Read(node)`
  （登记依赖——外部 `State` 变化驱动重建回显新值；`State.ToBinding()`
  每 Build 新实例按现值收敛、不影响 open flag/active）。
- **主题/DPI/窄宽**：全 `ResolvedTheme` token；逻辑像素域；320 宽
  `popupBounds` clamp 验证。

## 不做

可编辑键入、多选、分组头、item 自定义 View、虚拟化、动画、Alt+Down。

## 验收（headless runtime）

- E1：pointer Down+Up 同 trigger → 弹层开（Up-activation，Down 本身不开）；
  Up 同 item → 绑定写一次、关、trigger 回显新值。
- E2：键盘 Space(Down) 打开→Down×N（跳 disabled 行）→Enter→绑定写 active 值、
  关、焦留 trigger；Home/End 首/末项定位。
- E3：打开→Esc→绑定不变、关、焦在 trigger。
- E4：打开→Tab→关、焦点到主树下一 focusable（popup 项不参与——Tab 序列无 item）。
- E5：当前值不在 items → placeholder、不写回；写值后回显正常。
- E6：`Disabled` 项 pointer 不可点/键选跳过；disabled `Select` 不可点不开、Tab 跳过。
- E7：items 空 → trigger 显示 placeholder；全 disabled → 当前匹配项 Label
  仍显示（值缺失才 placeholder）；pointer Up/键盘 activate 均不开。
- E8：超 MaxPopupHeight → 限高内滚；active 键移 ensure-visible（移到底部外→
  ScrollOffset 自动使其可见，断言 offset 变化非 pin 值）。
- E9：同值重选 → 仍写一次；items 替换后 active stable-by-value；构造 duplicate
  `Value` → `ArgumentException`。
- E10：320 宽弹层 clamp 不出有效区；锚滚出 → 关；外点吞并/Esc/失活沿 P-04 语义。
- E11：回调次数——setter 仅选后恰好一次；开/关/Esc/Tab/外点零调用。
- E12：锚 Frame 移动 → popup 跟随；锚卸载/出有效可视区/disabled → 关；
  Esc 关闭后 `FocusedNode` 回 trigger（SavedFocus）。
- E13：外部 `State` 变化（不改 items/触发）→ `Select` 经 `Binding.Read`
  登记的依赖驱动重建、trigger 回显新值（不断言 `Value` getter 直读）。
- 断言经 `UiRuntime`+`DispatchPointer`/`DispatchKey`/`FocusedNode`/`Binding` 探测/
  `ScrollOffset*`/`HitTest`，不 pin 内部字段名。
