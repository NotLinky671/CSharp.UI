# SB-02：ScrollView 接线（persistent gutter + owner 绘/拖）

创建日期：2026-10-23

状态：已完成（fast 独立 5 executable checks + core 609 全绿 | 主审批准 | 含 5 条修正：Emit PopClip frameClip、Slider 调色板、SyncGeometry 共享、capture 不重复字段、HitTestSelf 不被 childClip 阻塞）

阶段：B

设计依据：[Scrollbar 计划](../plan/2026-10-23-scrollbar-plan.md) §决策一/二/三/四。

[返回任务索引](README.md)

前置依赖：SB-01。

- `src/CSharp.UI/Views/ScrollView.cs`：Measure/Arrange/`GetChildClip`/`Emit`/`OnPointerDown`/`OnPointerMove`/`OnPointerUp`/`OnPointerCancel`/`OnPointerEnter`；`ScrollViewState`（`IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?`）。

`ScrollView`（纵/横）接线共享 `ScrollbarMetrics`：persistent gutter 布局、owner `Emit` 画轨道、`OnPointer*` 处理 thumb 拖拽与 track 翻页，`ScrollOffsetX/Y` 经 owner 写回、wheel 路径不变。

## 文件范围

- `src/CSharp.UI/Views/ScrollView.cs`：Measure/Arrange/`GetChildClip`/`Emit`/`OnPointerDown`/`OnPointerMove`/`OnPointerUp`/`OnPointerCancel`；`ScrollViewState`（`IsDraggingBar`/`BarDragAnchor`/`BarTrackPressed`）。
- 对应集成测试。

## 实现契约

### 布局

- `gutter` = 沿滚动轴 cross 方向恒占（纵滚扣 width、横滚扣 height），宽 `min(ScrollbarThickness, max(0, crossExtent))`——极窄 frame 不产负 `contentRect`。
- Measure：子约束在 cross 方向 `min/max` 均减 `gutter`（`clamp>=0`），滚动轴仍无限；`DesiredSize = constraints.Constrain(contentDesired + gutter)`（滚动轴取 content、cross 轴取 content+槽）。
- Arrange：`contentRect = frame − gutter`（沿滚动轴 cross 方向缩）、`trackRect = frame 沿滚动轴的 gutter 区`；`GetChildClip` 返回 `contentRect`（内容不进 gutter）。
- `T = max(0, trackRect[axis] − 2×inset)`；`V<=0 || T<=0` → `ShowScrollbar=false`（不画不交互，槽仍占）。
- 非有限 `C`（`double.PositiveInfinity`）：`ShowScrollbar=false`（无有限 viewport 不画）。

### Emit

- `PushClip(frame)` → `PushClip(contentRect)` → `EmitChild` → `PopClip`（恢复 frame clip）→ **在 `frame` clip 内**画 track/thumb（`RectangleCommand`+圆角+主题色 `ControlBackground`/`Hover`/`Pressed`/`Disabled`）→ `PopClip`；`ShowScrollbar==false` 只留空槽不画。

### 指针

- `OnPointerDown`：**最先**查 `trackRect` 命中：
  - `ShowScrollbar==false` → 空槽仍属 owner 区域：消费（`return true`）不 `SetFocus`、不进文本路径、不 track-page；pointer 不落 `trackRect`（内容区）→ 走既有逻辑（return false 冒泡）。
  - `Thumb` → `IsDraggingBar=true`、`BarDragAnchor = p[axis] − thumbLeading`（含 `s`）、`BarPressKind=Thumb`；`context.MarkHandled(); return true`（不 `SetFocus`——不夺焦）。
  - `Before/After` → `BarPressKind=Track`、`offset[axis] -= V`/`+= V` + `InvalidateLayout`；`return true`。
- `OnPointerMove`：`IsDraggingBar` → `offset[axis] = clamp(((p[axis] − s − BarDragAnchor)/travel)*maxOffset, 0, maxOffset)` + `InvalidateLayout`；`return true`。
- `OnPointerUp`/`OnPointerCancel`：清 `IsDraggingBar`/`BarDragAnchor`/`BarPressKind`；**不**清 `LastPointerPoint`（pointer 观测，释放后仍悬 thumb 需保持）；`Up` 也缓存 `e.Position`；cancel 不滚回；`return true`（Up）/false（Cancel）。
- `Emit` 内先 `ReferenceEquals(context.Runtime.HoveredNode, node)` 再用缓存 `LastPointerPoint`（`OnPointerEnter`/`Move`/`Down` 存 `e.Position`）+ 当下 `trackRect`/`thumbLeading` 重算 hover——`HoveredNode!=owner` 立即 `None`（解决子消费 move/stale），`ShowScrollbar` 翻转/几何变化用新 metrics 同 point 重算；hover kind 为当次绘制值非持久状态。**不**用 `owner.IsHovered`；不新增全局 hover router。
- Wheel 落 gutter → 走 `OnWheel`（`maxOffset<=0` 不消费，继续冒泡）；track 点击仅单次 Down 翻页，无 auto-repeat。

### 拖拽手势生命周期（决策五，选定）

- **几何/range/axis 变化（resize/content shrink/overflow 消失/`Axis` 属性变）→ 取消当前 bar-gesture**（`IsDraggingBar=false`+清 `BarDragAnchor`/`BarPressKind`；**不**清 `LastPointerPoint`——pointer 观测不属于 gesture），`ScrollOffset` clamp 到 `[0,maxOffset]`；**自身 offset 更新保留 gesture**（正常 drag 不 cancel 自身）。`Axis` 虽 `init` 但 immutable View 重建可换值——node 上 axis 变化必须 cancel gesture。
- 取消后剩余捕获 move/up 不落入文本拖选（`IsDraggingBar` 已清，pointer 不在 `trackRect`→无 bar 分支→return false）。
- `IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?` 存 `ScrollViewState`（`InteractionState`）；`ResetInteractionState` 随卸载清。
- `IsEffectivelyDisabled` → `OnPointerDown` 短路 false。
- `ValidateInteractionState` 自动 `OnPointerCancel`（InputRouter:378，disabled/unmounted 捕获）→ 清 `IsDraggingBar`。

## 边界

- 不改 wheel 语义、不改 `FlexGrow`、不改公开 API；persistent gutter 是新默认行为（文档写明"无溢出时留槽"）。
- 不引入 child View 节点、不复制 ScrollView/Editor 两套状态机。

## 验收

- 集成：overflow→thumb、no-overflow→槽占无 thumb、drag→offset 连续、track 点击 ±V、边界 wheel 冒泡外层、disabled 不响应、unmount 安全、thumb 最小尺寸/夹取、`GetChildClip` 排除 gutter、`DesiredSize` 含槽。
- 既有基线绿。
