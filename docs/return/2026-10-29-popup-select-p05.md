# P-05 开发记录：Select\<T\> 控件（2026-10-29）

任务：[P-05 Select\<T\> 控件](../archive/2026-10-31-popup-select-tasks/05-select-control.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md) §6
前置：P-04（主审通过：独立 probe D1..D10 PASS + 组合期回归批次）
状态：**实施完成，冻结待主审 read + fast 独立 probe；未勾选验收、未提交。**
证据目录：`docs/return/2026-10-29-popup-select-p05-evidence/`（原始 stdout/stderr + 真实 exit code，UTF-8）。

## 公开 API（按 plan 冻结）

`src/CSharp.UI/Views/Select.cs`（新文件，不继续膨胀 `Controls.cs`）：

```csharp
public sealed record SelectItem<T>(T Value, string? Label = null, bool Disabled = false);
public sealed class Select<T> : View
{
    public Select(Binding<T> value, IReadOnlyList<SelectItem<T>> items, string? placeholder = null);
    public Binding<T> Value { get; }  public IReadOnlyList<SelectItem<T>> Items { get; }  public string? Placeholder { get; }
    public double MaxPopupHeight { get; init; } = 240;   // 有限正数，非法显式抛
}
```

- 构造：`Value` 按 `EqualityComparer<T>.Default` 唯一，重复 → `ArgumentException`（不静默 first-wins）；
  `SelectItem` 为 null → `ArgumentNullException`；items 复制为数组快照（调用方之后改原列表不影响控件）。
- 显示：`Label ?? (Value is null ? "" : Value.ToString() ?? "")`；值不在 items → placeholder（灰化）；
  值在但该项 `Disabled` → 仍显示该项 Label。
- `T` 不做 `Nullable<T>` 包装（`SelectItem<string?>(null)` 也合法，键走 ForEach 的 null-key 规则）。

## 机制（复用 P-03/P-04 的同一 overlay 生命周期）

- **owner 拉取**：`Select` 实现 internal `IOverlayOwner`，`OnMount` 注册；`TryGetOpenRequest` 每轮安全阶段：
  `Value.Read(node)` 登记来源依赖 → `SyncValue`（值变化时让 trigger 重排一次，标签回显）→ open flag 为真且
  可开时给出 `OverlayOpenRequest(node, 缓存内容, margin 4, MaxPopupHeight, Auto)`。
  **不新立 popup 系统**：打开/关闭/外点/Esc/失活/锚失效/挤占/卸载全部沿用 manager 已有路径与 `CloseReason`。
- **open flag**（`node.InteractionState` 的 `SelectState<T>`）：pointer Up 同点 / Enter·Space·Up·Down 打开；
  再点 trigger、选后、Esc（manager 路径）、Tab 都置 false → 拉取阶段 `RequestClose(OwnerRequest)`（不回写值）。
  `OnOverlayClosed` 同步清 flag + pressed（不留 stale open）。
- **提交**：行 Up（同一次按下、同一项、指针仍在行内）或 Enter/Space 提交 active 值：
  `ownerNode.View` 取**当前** `Select<T>` 的最新 `Value` 绑定写**恰好一次**，然后关弹层、`SetFocus(trigger)`。
  行事件不捕获旧描述/旧绑定；父组件重建换 setter 后写新绑定（S13 验证 old 0 / new 1）。
- **active/pressed 按值记录**（不是下标）：items 重建后按值重定位（移除/禁用 → 首个可用项）；
  按下后 items 重排/移除/禁用，Up 不会提交"现在位于该位置"的另一项（S12）。
- **焦点恒在 trigger**：行 `IsFocusable=false`（不进 Tab），行 Down/Up 不改焦点；点 disabled 行或列表滚动条槽时
  由 `SelectPopupContent`/ScrollView 消费 Down，避免落到 InputRouter 的"空白左键清焦"（S8）。
  这是 Select 内容自身的容器消费，**没有全局阻止 popup 空白清焦**（通用 `Popup` 仍按原规则）。
- **键盘表**（trigger `OnKeyDown`）：关 Enter/Space(Down)/Up/Down 打开并初定位（选中项或首/末可用项）——
  不满足打开条件不消费；开 Up/Down 跳 disabled、两端停；Home/End 首末；Enter/Space 提交；
  Esc 不自处理（交给 P-04 的统一 Esc：先 focused 路由，再 manager 关闭并恢复 SavedFocus）；
  Tab 关弹层但不消费（主树焦点继续移动）。`Repeat` 的 Enter/Space 一律消费但不动作（按住不会开完立即提交），
  Up/Down 允许重复导航，KeyUp 不提交。
- **布局**：trigger 继承 Theme/Font/Disabled/Opacity（`ResolvedTheme` token），单行 `TextTrimming.Ellipsis`
  且绘制裁剪到标签区；弹层内容 `ScrollView(Standard)` + 行容器（`VStack`），宽度 ≥ trigger 实宽
  （`Arrange` 记录 trigger Frame 宽，变化时 `InvalidateOpenPopupMeasure` 让弹层重测一次），最终由 placement
  按 `popupBounds` clamp；高度经 `MaxPopupHeight`/side 可用空间限制，内部真正 ScrollView 滚动。
- **ensure-visible**：键盘移动 active 后，安全阶段按 active 行的 Frame 与滚动视口调整 `ScrollOffsetY`
  （无新公共滚动 API；只在需要时发生）。
- **收敛信号**：manager 的 `ConsumePending` 用 `LayoutSerial` 变化判断 owner 在安全阶段做过的必要失效
  （标签宽度/ensure-visible/最小宽度跟随），使这些调整在同一轮 `ProcessPendingWork` 内收敛，而不是拖到下一帧。

## 实施中定位并修复的真实缺陷

1. `Select` 初版没有 `OnMount → RegisterOwner`，owner 永不被拉取、弹层打不开（13 个用例全红暴露）；
   补上注册（状态仍在 `InteractionState`，与 P-03 `Popup` 一致）。
2. 内容根写成 `ScrollView(ForEach<...>)`：`ForEach` 会展开为多个同级子项，而 `ScrollView` 只测量/排列第一个子项
   → 行全部重叠、点任何位置都命中首行。改为 `ScrollView(VStack(ForEach(...)))`（行仍按值 key 复用）。
3. 行点击后提交若只读构造期捕获的 `Select`/`Binding`，父组件重建后会调用旧 setter；
   改为提交时 `ownerNode.View` 取当前描述（S13 覆盖）。

## 测试（`tests/CSharp.UI.Tests/SelectTests.cs`，16 个消费者用例）

| 用例 | 覆盖 |
|---|---|
| `S1_PointerUpOpensAndRowClickCommitsExactlyOnce` | Down 不开、Up 开、行点击写一次、关闭、trigger 回显、焦点留 trigger |
| `S2_KeyboardNavigationSkipsDisabledAndCommitsActive` | Space 打开、Down 跳 disabled、Enter 提交、Home/End 定位 |
| `S3_KeyRepeatDoesNotOpenThenImmediatelyCommit` | 按住 Enter 的重复 Down 不提交、KeyUp 不提交、新按键才提交 |
| `S4_EscapeClosesWithoutWritingAndKeepsFocusOnTrigger` | Esc 关、零写回、焦点在 trigger |
| `S5_TabClosesWithoutConsumingAndPopupAddsNoFocusTargets` | Tab 关闭不消费、打开前后焦点序一致、焦点移到主树下一项 |
| `S6_MissingValueShowsPlaceholder_AllDisabledShowsLabelButDoesNotOpen` | 值缺失 placeholder 不写回、全 disabled 显示 Label 但不开、空列表不开、重复值抛异常 |
| `S7_SameValueReselectionStillWritesOnce_NullValueIsLegal` | 同值重选仍写一次、null 值可选并回显 |
| `S8_DisabledItemIgnoresPointerAndKeyboard_DisabledSelectDoesNotOpen` | disabled 行/滚动条槽点击不夺焦不提交；随后键盘直接提交（无需重新聚焦）；整体 disabled 不开、Tab 跳过 |
| `S9_LongListScrollsWithinMaxHeightAndEnsureVisibleFollowsActive` | 限高内滚、弹层宽 ≥ trigger 宽、End 后 ensure-visible 产生滚动、提交末项 |
| `S10_NarrowViewportClampsPopupWithinUsableBounds` | 长标签 + 窄视口：弹层不越界 |
| `S11_ExternalValueChangeUpdatesDisplayWithoutLosingOpenState` | 外部 State 变化（`Binding.Read` 依赖）→ 回显更新、open 不丢、重建后仍可提交 |
| `S12_ItemsReorderWhilePressedCommitsThePressedValueNotTheNewIndex` | 按下后 items 重排 → Up 不提交新索引项、零写回、弹层保持开 |
| `S13_RowCommitAfterReplacementUsesCurrentBindingInstance` | 打开后替换 binding setter：旧 setter 0 次、新 setter 1 次 |
| `S14_FirstOpenWithSelectedLastItemScrollsAndRowIsClickable` | 初值=末项 + 限高：**首次打开单次 Process** 即滚到末项可见，直接点弹层底部提交（无需 End 预热） |
| `S15_ReorderMovingActiveToLastScrollsItIntoViewWithoutChangingSelection` | 打开后把 active 重排到末项：单次 Process 内重新 ensure-visible、选择不变、零写回 |
| `S16_OwnerInvalidatingEveryPollIsBoundedAndRecovers` | 每轮换值的 getter（owner 每轮请求失效）：被单轮预算拦截（诊断 + 异常），getter 稳定后运行时恢复、弹层状态不丢 |

断言只走 Binding 写计数/挂载状态/Frame/FocusedNode/ScrollOffset/trigger 排版文本（绘制输入），不 pin 内部行类型、
私有字段或默认常量。Core 770 → **786**（+16）。

## 验证（原始 stdout/stderr 与真实 exit code；UTF-8）

| 命令 | 结果 | 证据（`2026-10-29-popup-select-p05-evidence/`） |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0 | `p05-build.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **783**, Failed 0，exit 0 | `p05-CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `p05-CSharp.UI.Integration.Tests.*` |
| 同上（含 throwaway 真实 Skia Select smoke） | Total **57**, Failed 0，exit 0 | `p05-select-smoke.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `p05-CSharp.UI.Markdown.Tests.*` |

- throwaway smoke（真实 Skia 文本 + headless runtime，`pixelScale=1.5`）：真实文本 trigger（placeholder 排版非空）→
  pointer Up 打开 → 行点击提交（写一次、关闭、回显 `Beta`、焦点回 trigger）→ Space 打开 → Esc 关闭且不再写值。
  运行后删除（`git status` 无残留），仅保留日志。
- 未跑 OpenTK 套件（已知 Markdown Copy 80px 基线边界，P07 用已准备的最小 general fix 统一处理）。

## 未实现 / 边界

- Alt+Down、可编辑键入、多选、分组、自定义 item 模板、虚拟化、动画（计划明确不做）。
- 弹层内容为“每次 items 引用变化重建一次”，行按值 key 复用；不提供虚拟化（长列表为真实行节点）。
- Gallery 集成（P06）未做：样例的 Select 宽度不要硬编码 120（主审 3539 提醒：Gallery 窄宽验收会遍历主树，
  Select 应跟随父容器提案）。

## 返修批次：First-open ensure-visible / 不透明面板 / 预算守护（2026-10-29 postensure）

独立 probe 与主审实图确认了两个真实缺陷，另发现一处预算绕过，本批按最小修处理：

1. **首次打开已选末项不滚入可视区**（probe S14：offset 0、行 Y 856 在视口 44..124 之外）：
   `TryOpen` 置 `PendingEnsureVisible` 后，`TryGetOpenRequest` 在 `ScrollNode` 尚为 null 时就清掉了 pending。
   修法：`EnsureActiveVisible()` 返回 bool（只有视口可用、目标行存在且**当前内容代际已真实 Arrange** 才返回 true），
   pending 只在兑现时清除，否则留到后续安全阶段重试；另引入 `ContentGeneration/ScrollReadyGeneration`，
   防止 items 重建后“旧 ScrollNode 仍 mounted、Frame 有效”被误判为已可见；`SyncItems` 在列表变化时重新 rearm。
   收敛走 manager 既有 `LayoutSerial` 进度信号 + 单轮预算，**一次 `ProcessPendingWork` 内完成**（S14/S15）。
2. **弹层无底色**（probe 实图：无 panel/边框，行直接画在 page 上）：
   `SelectPopupContent` 增加不透明面板 `Theme.Surface` + `Theme.Border` + 控件族圆角；内容在真实
   `PanelInset` 内 Measure/Arrange，并通过 `GetChildClip` 裁剪（不是只画背景矩形），滚动条/active 行不会压到边框圆角外。
   **通用 `PopupSurface` 默认仍无底色**（generic popup 内容由用户自定）。
3. **owner 每轮失效绕过单轮预算**：`ConsumePending` 的 `return ownerInvalidated` 早于 `++_roundsInPass`，
   一次都不消耗预算。改为只有“完全没有工作”才提前返回；`ownerInvalidated` 与其它进度一样经过预算
   （诊断 + 异常 + 运行时丢弃请求恢复），稳定 owner 仍 O(1) 且不产生 work（S16）。
4. **`InvalidateOpenPopupMeasure` 时序**（主审 7CEF 要求核实）：`UiRuntime.RunLayout` 在**末尾**才清
   `_layoutDirty`（`UiRuntime.cs:1045`），所以 `Select.Arrange` 内直接 `InvalidateLayout` 会被同一次布局末尾抹掉、
   也不产生进度信号。改为**只登记请求**（`_popupMeasureInvalid`），由 `ConsumePending` 在预算之后消费 →
   `InvalidateLayout(surface)` + 返回进度 → 同一轮 ProcessPendingWork 再布局一次（有界、只一次）。
   本地临时探针（视口 360→900 单次 Process）证明确实跟随，探针跑后已删除。
5. `DrawArrow` 改用 `node.Environment.Opacity`（`.Opacity(0)` 不再残留实心箭头）；`OnKeyDown` 对 Ctrl/Meta 组合
   直接返回 false（不吞全局快捷键；Alt+Down 仍按普通 Down，计划未实现）；`Items` 用 `Array.AsReadOnly` 包一次
   （公开只读列表不能被 cast 回可变数组，也不会每帧复制）。

| 命令 | 结果 | 证据（`2026-10-29-popup-select-p05-evidence/`） |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0 | `p05-postensure2-build.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **786**, Failed 0，exit 0 | `p05-postensure2-CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `p05-postensure2-CSharp.UI.Integration.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `p05-postensure2-CSharp.UI.Markdown.Tests.*` |

（`p05-*` / `p05-final-*` 为较早一轮证据；`p05-select-smoke.*` 为真实 Skia Select smoke：57/0。）

## Freeze

P-05 源码/测试/文档已冻结（含 postensure 返修批次）：**停止 build/edit**。
等待主审 read 与 fast 同一 probe 复跑：**S14（初值末项首次打开一次 Process 内滚入可视区并可点）**、
列表重排 active 换位后同样可见、单次 GL 帧确认不透明面板/边框、`.Opacity(0)` 无一帧残墨，
以及原有 13 项交互回归。通过后进入 P-06；不通过则在 `docs/return/` 继续新增返修记录。
