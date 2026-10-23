# SB-03：TextEditor 接线（persistent gutter + bar 优先 + 组合期持久浏览意图）

创建日期：2026-10-23

状态：已完成（fast 独立真实窗口 IME/拖拽验收通过；core 618 + Integration 53 + OpenTK 678 Debug 全绿，Debug/Release 双 config 构建 0 警告 0 错误；审核期返修 Measure/GetLayout 排版宽已并入——`InnerRect` 同源取 key、非 tight `DesiredSize.Width` 含 `min(12, padded)`）。证据见 [交付记录](../../delivery/2026-10-23-scrollbar-delivery.md) 与 [审查记录](../../return/2026-10-23-scrollbar-review.md)。

阶段：B

设计依据：[Scrollbar 计划](../../plan/2026-10-23-scrollbar-plan.md) §决策一~七。

[返回任务索引](README.md)

前置依赖：SB-01。

## 目标

- `src/CSharp.UI/Views/TextEditor.cs`：`InnerRect`（恒扣）、`Emit`（content clip pop 后画轨道）、`OnPointerDown`（bar-hit 最先）、`OnPointerMove`/`OnPointerUp`/`OnPointerCancel`/`OnPointerEnter`/`OnClockTick`（`IsDraggingBar` 分支）、`PublishTextInputGeometry`（`RequiresCaretReveal` 语义）、`TextEditorState.IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?`/`RequiresCaretReveal`。

## 文件范围

- `src/CSharp.UI/Views/TextEditor.cs`：`InnerRect`（恒扣）、`Emit`（content clip pop 后画轨道）、`OnPointerDown`（bar-hit 最先）、`OnPointerMove`/`OnPointerUp`/`OnPointerCancel`/`OnClockTick`（`IsDraggingBar` 分支）、`PublishTextInputGeometry`（`RequiresCaretReveal` 语义）、`TextEditorState.IsDraggingBar`/`BarDragAnchor`/`BarTrackPressed`/`RequiresCaretReveal`。
- 对应集成测试。

## 实现契约

### 布局

- `gutter` = 右侧 `ScrollbarThickness` 恒占；`inner = frame − padding − gutter`（**唯一内容区**，13 处使用点统一）；`trackRect = frame − padding` 的 gutter 区（`inner` 右外、`frame` 内）。
- wrap 宽 = `inner.Width`（含 gutter）；overflow 只决定画不画，不改 inner。
- `s = trackRect.Y + inset`（**inset 后**轨道起点）、`T = max(0, trackRect.Height − 2×inset)`；`gutter` 宽 `min(ScrollbarThickness, max(0, frame.Width − 2×padding))`；`V<=0 || T<=0` → `ShowScrollbar=false`（槽仍占）。
- 非有限 `C`（`double.PositiveInfinity`）→ `ShowScrollbar=false`（`inf/inf` 不产生 NaN）。

### Emit

- `PushClip(frame)` → `PushClip(inner)` 画内容 → `PopClip`（恢复 frame clip）→ **在 `frame` clip 内**（`inner` 外、`frame` 内的 gutter 区）画 track/thumb（`RectangleCommand`+主题色）→ `PopClip`；`ShowScrollbar==false` 只留空槽不画。

### 指针（bar 最先，无 focus/caret/composition 副作用）

- `OnPointerDown`：**最先**查 `trackRect` 命中：
  - `ShowScrollbar==false` → 空槽仍属 owner 区域：消费（`return true`）不 `SetFocus`、不 `CancelCompositionForHostAction`、不 `SetCaretFromPointer`/`IsDragging`、不 track-page——pointer 不落 `trackRect`（内容区）→ 走既有文本路径（`SetFocus`→cancel 组合→caret/drag-select）。
  - `Thumb` → `IsDraggingBar=true`、`BarDragAnchor = p.Y − thumbLeading`（含 `s`）、`BarPressKind=Thumb`；**不** `SetFocus`、**不** `CancelCompositionForHostAction`、**不** `SetCaretFromPointer`/`IsDragging`；`context.MarkHandled(); return true`。
  - `Before/After` → `BarPressKind=Track`、`ScrollY -= V`/`+= V` + `InvalidateRender` + `RequiresCaretReveal=false`；`return true`。
- `OnPointerMove`：`IsDraggingBar` → `ScrollY = clamp(((p.Y−s−BarDragAnchor)/travel)*maxScroll, 0, maxScroll)` + `InvalidateRender` + `RequiresCaretReveal=false`；`return true`。
- `OnPointerUp`/`OnPointerCancel`：清 `IsDraggingBar`/`BarDragAnchor`/`BarPressKind`；**不**清 `LastPointerPoint`（pointer 观测，释放后仍悬 thumb 需保持）；`Up` 也缓存 `e.Position`；cancel 不滚回；`return true`（Up）/false（Cancel）。
- `Emit` 内先 `ReferenceEquals(context.Runtime.HoveredNode, node)` 再用缓存 `LastPointerPoint`（`OnPointerEnter`/`Move`/`Down` 存 `e.Position`）+ 当下 `trackRect`/`thumbLeading` 重算 hover——`HoveredNode!=owner` 立即 `None`（解决子消费 move/stale），`ShowScrollbar` 翻转/几何变化用新 metrics 同 point 重算；hover kind 为当次绘制值非持久状态。**不**用 `owner.IsHovered`；不新增全局 hover router。
- `OnClockTick`：`IsDraggingBar` 不进 drag-select 边滚（`DragPointer` 为 null 已天然防）。

### 拖拽手势生命周期（决策五，选定）

- **几何/range 变化（resize/content shrink/overflow 消失）→ 取消当前 bar-gesture**（`IsDraggingBar=false`+清 `BarDragAnchor`/`BarPressKind`；**不**清 `LastPointerPoint`——pointer 观测不属于 gesture），`ScrollY` clamp 到 `[0,maxScroll]`；**自身 offset 更新保留 gesture**（正常 drag 不 cancel 自身）。
- 取消后剩余捕获 move/up 不落入文本拖选（`IsDraggingBar` 已清，pointer 不在 `trackRect`→无 bar 分支→return false）。
- `IsDraggingBar`/`BarDragAnchor`/`BarPressKind`/`LastPointerPoint?` 存 `TextEditorState`（`ResetInteractionState` 随卸载清）。
- `IsEffectivelyDisabled` → `OnPointerDown` 短路 false。
- `ValidateInteractionState` 自动 `OnPointerCancel`（InputRouter:378，disabled/unmounted 捕获）→ 清 `IsDraggingBar`。
- Wheel 落 gutter → `OnWheel`（写 `ScrollY`+`InvalidateRender`+`RequiresCaretReveal=false`）；track 点击仅单次 Down，无 auto-repeat。

### 组合期持久浏览意图（决策七，冻结）

- `TextEditorState.RequiresCaretReveal`（bool）：**显式操作**（键盘/编辑/pointer 文本/caret 推进/undo）或**组合内容真实推进**（`preedit` 变化/`caret` 变化）→ `true`；用户 scroll（wheel/bar/track）→ `false`；**不**因 `IsComposing` 本身每帧置位。
- `PublishTextInputGeometry`：`IsComposing && RequiresCaretReveal` → `EnsureCaretVisible` 一次后 `RequiresCaretReveal=false`；否则不 ensure。
- 验收：拖到 caret 完全裁出→release→多帧/clock/同值 PPW 仍不回弹；真实新输入/caret 推进→`RequiresCaretReveal` set→下一次 publish 正确 reveal。
- caret 滚出视口 → `ComputeInputGeometry` 返 `Unavailable` → `WindowsImeBridge.PublishGeometry(HasCaret=false)` → `SetCandidateExclude` 全窗+`_anchorRevoked`（**不** `NotifyNativeCancel`）→ 组合保活；caret 回视口重锚。
- 非组合路径不变。

## 边界

- `IsDraggingBar` 与 `IsDragging`（文本拖选）互斥。
- 不改 `EnsureCaretVisible`/`EnsureGeometryKey`/`MaxScrollY`/undo/`Binding`/`Caret`/`Anchor`/选区；`TextField` 零改动。
- persistent gutter 使 `inner.Width` 恒窄——**visibility 变化不改 `contentRect`/`EnsureGeometryKey`**；真实尺寸/字体/DPI 变化才经键触发 reflow/ensure（`overflow` 出现/消失本身不改变键，属预期非回归）。

## 验收

- 集成：overflow→thumb、drag/track→`ScrollY`、caret/选区/undo/`Binding` 不变、bar 点击不移 caret、组合期 drag→`ScrollY` 变且不弹回（`Unavailable` 期间组合保活、回视口重锚）、IME anchor 随 `ScrollY` 更新、disabled 不响应、unmount 安全、scroll-bounce 回归绿。
- 既有基线绿。
