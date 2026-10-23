# Scrollbar（滚动条）实施计划

计划日期：2026-10-23

状态：实施与验收完成；提交推送关联本次实现提交（见 Git 历史）。任务 SB-01~SB-07 已归档至 [docs/archive/2026-10-23-scrollbar-tasks/](../archive/2026-10-23-scrollbar-tasks/README.md)；后续 Floating 样式见 [Floating 滚动条计划](2026-10-23-floating-scrollbar-plan.md)。

关联：[文本编辑器计划](2026-10-23-text-editor-plan.md)、[滚动回跳修复计划](2026-10-23-text-editor-scroll-bounce-fix.md)、[修复记录](../return/2026-10-23-texteditor-scroll-bounce-repair.md)。

## 目标与范围

为单轴 `ScrollView`（纵/横）与 wrap-only `TextEditor`（纵）提供共享滚动条：内容溢出才显示、滑块按比例、可拖拽滑块、点击轨道按视口翻页。复用既有指针捕获/绘制/裁剪通道，不新建滚动框架。

**不实现**：横向 TextEditor 滚动条、双轴 ScrollView、公开滚动命令、minimap、动画/惯性、触摸、虚拟化、无障碍桥。

## 核心决策（待审）

### 决策一：persistent gutter + owner 自绘（非 child View）

- 轨道槽恒占 `ScrollbarThickness` 宽（cross 方向），`overflow` 只决定画不画 track/thumb——槽位固定免宽度回流环。
- **理由**：`ScrollView.GetChildClip` 对所有 child 生效（child scrollbar 会被同一裁剪裁出/命中排除）；immutable `View.Children` 不能由 Measure/Arrange 动态产出 runtime 子节点；`InputRouter` Down 前置捕获 `_hovered`（命中即捕获，无新增 API）。
- owner 在 `Emit` 内 content clip pop 后、`frame` clip 内画轨道；`OnPointerDown` 最先查 `trackRect`+`ShowScrollbar` 进 bar 路径。
- 无 overflow 时槽仍属 owner 区域（不落到文本选择/underlying child；wheel 仍 owner/ancestor 路由）。

### 决策二：persistent gutter 换布局确定性

- `overflow` 与否不改 gutter 宽——cross 宽从布局初始固定，无"出现→宽变→高变→overflow 翻转"环。
- 代价：无 overflow 留 `ScrollbarThickness` 空白槽（文档写明）。
- ScrollView Measure：子约束 cross `min/max` 减 `gutter`（`clamp>=0`），`DesiredSize` 加回 `gutter` 再 `constrain`；TextEditor `inner` 恒扣。
- 收敛：宽度与 overflow 解耦，`M=max(0,C−V)` 只影响绘不画，一次 Measure/Arrange 收敛，无二遍测量。

### 决策三：公式冻结（绝对坐标，`s`=inset 后起点）

令 `s = trackRect[axis].Start + inset`（**inset 后**实际轨道起点，非 trackRect 原点）、`T = max(0, trackRect[axis].Length − 2×inset)`、`V`=视口、`C`=内容、`M=max(0,C−V)`：

- `ShowScrollbar = M>0 && V>0 && T>0 && double.IsFinite(C) && double.IsFinite(V)`
- `thumbLength = clamp(T*V/C, min(T,MinThumbLength), T)`
- `travel = T−thumbLength`；`travel<=0`（`C>V` 但 `T<=MinThumbLength` 全满）→ 显示满 thumb（`thumbLeading=s`）、drag 无行程、track 无区（**非** `ShowScrollbar=false`）
- `thumbLeading = s + (clamp(offset,0,M)/M)*travel`
- 命中（`p`=pointer 沿滚动轴坐标，绝对）：`p < thumbLeading`→`Before`、`p > thumbLeading+thumbLength`→`After`、之间→`Thumb`；`travel<=0` 全轨 `Thumb`
- drag：`grab = pDown − thumbLeading`（**含 `s`**）；`offset = clamp(((p−s−grab)/travel),0,1)*M`（`travel<=0` 不变）
- track 点击：`Before`→`−V`、`After`→`+V`，单次无 auto-repeat

常量：`ScrollbarThickness=12`、`MinThumbLength=24`、`inset=2`；gutter 实际宽 `min(12, max(0,crossExtent))`。

### 决策四：按压/hover 状态（单一枚举，不新增全局事件）

- **按压**：`BarPressKind { None, Thumb, Track }`（单枚举存 owner state，取代 bool 冗余）；`Thumb` Down→`Thumb`，track `Before/After` Down→`Track`（按压视觉），`Up`/`Cancel`/geometry-cancel/disable/unmount→`None`。单 Down 只翻一页，无 auto-repeat。
- **不**用 `owner.IsPressed` 涂 bar——`InputRouter` Down 把整条 ancestor chain `IsPressed=true`（内容 Button 按下也会点亮 owner），bar 按压必须按 `BarPressKind` 判定。
- **hover**：owner 缓存 `OnPointerEnter`/`OnPointerMove`/`OnPointerDown`/`OnPointerUp` 的 `e.Position`（`LastPointerPoint?`，nullable——无观测时 hover `None`）；`Emit` 先 `ReferenceEquals(context.Runtime.HoveredNode, node)` 再用该 point + 当下 `trackRect`/`thumbLeading` 重算 hover——`HoveredNode!=owner` 立即 `None`（解决子消费 move/stale），`ShowScrollbar` 翻转/几何变化用新 metrics 同 point 重算。**不**用 `owner.IsHovered`（ancestor chain 误亮）；不新增全局 hover router；不维护独立 `BarHoverKind` 持久状态——hover kind 为当次 `Emit` 的绘制值，单 `BarPressKind`+`BarDragAnchor`+`LastPointerPoint?` 足够。**`LastPointerPoint` 是 pointer 观测不是 gesture anchor**：`Up`/`Cancel`/geometry-cancel/press 清理**不**清它（释放后 pointer 静止仍悬 thumb 需保持）；仅 `ResetInteractionState`（卸载）清；window-outside 由 `HoveredNode!=owner` 归 `None`，不依赖 point 清空。
- 状态字段：`IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?`（`TextEditor`+`ScrollView` 各一份，`InteractionState`）。

### 决策五：焦点与组合不夺焦、不取消

- ScrollView bar 命中不 `SetFocus`；TextEditor bar 命中走最先 `trackRect` 分支——**不** `SetFocus`、**不** `CancelCompositionForHostAction`、**不** `SetCaretFromPointer`/`IsDragging`，`return true` 短路。
- 焦点保持当前 owner（已 focus 则 composition 保活；未 focus 的 gutter 点击不触发 focus）。

### 决策六：拖拽手势生命周期（选定，唯一）

- **几何/range/axis 变化（resize/content shrink/overflow 消失/`Axis` 属性变）→ 取消当前 bar-gesture**（`IsDraggingBar=false`+清 `BarDragAnchor`/`BarPressKind`；**不**清 `LastPointerPoint`——pointer 观测不属于 gesture），offset clamp 到 `[0, maxOffset]`；**自身 offset 更新保留 gesture**（正常 drag 不 cancel 自身）。`Axis` 虽 `init` 但 immutable View 重建可换值——node 上 axis 变化必须 cancel gesture。
- 取消后剩余捕获 move/up 不落入文本拖选（`IsDraggingBar` 已清，pointer 不命中 trackRect→owner `OnPointerMove` 无 bar 分支→return false；`OnPointerUp` 同理）。
- `IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?` 存 owner `InteractionState`（`ResetInteractionState` 随卸载清）。
- `ValidateInteractionState`（InputRouter:378）对 disabled/unmounted `_captured` 直接 `OnPointerCancel`→owner 清 bar；`Pressed`/`IsPointerCaptured` 由 `ClearPressState` 清。

### 决策七：wheel 落 gutter 走 owner wheel

- gutter 区 wheel 仍走 owner `OnWheel`（ScrollView `maxOffset<=0` 不消费冒泡；TextEditor 写 `ScrollY`+`InvalidateRender`）；scrollbar 自身不消费 wheel。

### 决策八：组合期用户滚动——持久浏览意图（非单帧）

- `TextEditorState` 新增 `RequiresCaretReveal`（bool）：**显式操作**（键盘/编辑/pointer 文本/caret 推进/undo）或**组合内容真实推进**（preedit 变化/caret 位置变化）→ set；用户 scroll（wheel/bar/track）→ clear；`PublishTextInputGeometry` 的 `IsComposing` ensure 仅当 `RequiresCaretReveal==true` 时执行（执行后 clear）。
- **不因 `IsComposing` 本身每帧 set**：纯重绘/PPW/同值 preedit 重复通知/parent-translate 不能重新 ensure——用户拖到 caret 完全裁出→release→多帧/clock/同值 PPW 仍不回弹；真实新输入/caret 推进→`RequiresCaretReveal` set→下一次 publish 正确 reveal。
- caret 滚出视口 → `ComputeInputGeometry` 返 `Unavailable`（`inner` 裁出）→ 快照 null → `WindowsImeBridge.PublishGeometry(HasCaret=false)` → 候选窗 exclusion（`_anchorRevoked`），**不** `NotifyNativeCancel`（仅 `EndSession`/`RequestCancel` 调）→ **组合保活、不取消、不弹回**；caret 回视口重锚。
- 依据：`WindowsImeBridge.PublishGeometry` :540-559 源码验证；`RequestCancel`/`EndSession` 为唯一 native cancel 路径。

## 公开 API 面

零新增：无 `ScrollbarVisibility` 枚举、无公开滚动命令；行为变化 = 溢出显示 scrollbar + persistent gutter（文档写明）。

## 证据锚点（符号级）

- Capture：`InputRouter` `_captured`/`_hovered` 前置捕获；`ValidateInteractionState` 对 disabled/unmounted `_captured` 直接 `OnPointerCancel`+`ClearPressState`（:374-379）。
- Hit-test：`HitTest` 子节点 `GetChildClip` 裁剪+逆序+退回 `HitTestSelf`（`frame.Contains`）。
- ScrollView：`GetChildClip=>node.Frame`、`Emit` `PushClip`→child→`PopClip`、`OnWheel` `maxOffset<=0` 不消费、`Arrange` offset clamp。
- TextEditor：`InnerRect`（13 处统一）、`origin.Y−ScrollY`、`EnsureCaretVisible` 只在 Arrange 键变+显式、`OnWheel` `InvalidateRender`、`PublishTextInputGeometry` `ScrollY` clamp+`IsComposing` ensure、`ComputeInputGeometry` `inner` 裁出→`Unavailable`。
- WindowsImeBridge：`PublishGeometry(HasCaret=false)` → `SetCandidateExclude` 全窗 exclusion+`_anchorRevoked`（:540-559），无 `NotifyNativeCancel`；native cancel 仅 `EndSession`(:438)/`RequestCancel`(:483)。
- Theme：`ControlBackground`/`Hover`/`Pressed`/`Disabled` 经 `ResolvedTheme`。
- 绘制：`RectangleCommand`、PushClip/PopClip。
- 状态：`ViewNode.InteractionState`/`IsPointerCaptured`/`IsMounted`/`IsEffectivelyDisabled`/`ResetInteractionState`。
