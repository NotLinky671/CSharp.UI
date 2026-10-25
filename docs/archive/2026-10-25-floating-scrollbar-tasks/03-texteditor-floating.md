> **历史归档**（2026-10-25 移自 `docs/task/`）：本任务已完成并验收通过，仅供历史追溯；当前任务索引见 [docs/task/README.md](../../task/README.md)。

# FS-03：TextEditor Floating 接线

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../../return/2026-10-23-floating-scrollbar-review.md)。

阶段：B

设计依据：[Floating 滚动条计划](../../plan/2026-10-23-floating-scrollbar-plan.md) §决策二/三/五/六/七/八/九/十/十二。

[返回任务索引](README.md)

前置依赖：FS-01。

## 文件范围

- `src/CSharp.UI/Views/TextEditor.cs`：`ScrollbarStyle` 属性 + `With` 复制；Measure/`InnerRect`/`TrackRect`/`EmitScrollbar`/`HitTestOverlay`/`OnTimedWake`/`OnPointerEnter`/`OnPointerMove`/`OnPointerLeave`/`OnPointerDown`/`OnUpdate`/`Arrange`；5 处 ScrollY 写点。
- 对应单元测试（consumer-observable）。

## 实现契约

### 布局（Floating）

- `gutter = style==Floating ? 0 : min(Thickness, max(0,padded.Width))`：`InnerRect`（Floating 下 = `frame − padding`）、`TrackRect`（padded 右缘 `g` 宽条）、Measure 的 `+ min(Thickness, …)` 宽度项按 style 取 0。
- 排版 wrap 宽 = `InnerRect(...).Width`——Floating 下 wrap 用全 padded 宽（显隐不再改 wrap，天然无 reflow 环）。
- `GetChildClip`/IME 几何 `EffectiveClip` 链路不变。

### 显示源（5 处真实写点）

`NotifyScrolled(node)` 在写回成功后调用（Standard 下 no-op）：

- `OnWheel`（:1649）——边界 wheel 不显示；
- bar thumb-drag（:560，`next` 变化时）与 track page（:510）；
- `EnsureCaretVisible`（:1590/:1594/:1597）——集中覆盖键盘导航/编辑/caret 推进/组合 ensure/Arrange ensure 键变；内部比较旧值，无变化不通知；
- `OnClockTick` 拖选自动滚动（:1739）。

`PublishTextInputGeometry` clamp（:1936）与被动 clamp/translate **不**通知。

### 命中与可见门

- `HitTestOverlay`：`ScrollbarStyle==Floating && state.Bar.IsFloatingVisible && ShowScrollbar(V,C,T)` → `TrackRect(node.Frame)`；否则 null。
- `OnPointerDown` bar 门（:490）换 `Visible` 门——hidden 时不消费、落文本路径（caret/selection 正常）。
- bar 命中仍最先分支、短路返回：不 `SetFocus`、不 `CancelCompositionForHostAction`、不改 caret/selection/history/Binding（§决策十二不变量）。

### hover 抑制（决策七）

- 实现契约（R3/R5 后，TextEditor 同 owner target 特性）：owner 实现 `View.OnHoverDeepestChanged(node, Point position, bool isDeepest)`——`over = isDeepest && IsFloatingVisible && track.Contains(position)`；`over ? SuppressFloatingTimer : (suppressed && PressKind==None → RearmFloatingTimer(now))`。TextEditor content 与 bar 同属一个 owner deepest——`UpdateHover` 对同 target 早退时 `RefreshHover` 用 `forceDeepestNotify=true` 按 router 权威 `e.Position` 重估；普通 Move 不付此成本。
- 同 FS-02：`ShowFloating` 后 `runtime.RefreshHover()` 静止续命；`ClearHover` 清空前 `isDeepest=false` 通知为权威身份（不重查）。

### due / cleanup

- `OnTimedWake` override → `state.Bar.OnTimedWakeDue(node, now)`；`OnClockTick`（:1693 拖选）保持 focused 路径原语义，due 不复用它。
- `OnUpdate`（:1869）：`ScrollbarStyle` 变化 → `state.Bar.ResetFloating`。
- `Arrange`（:216-220 区）：`SyncGeometry` 保持；`ShowScrollbar` 翻 false → `ResetFloating`；disabled → `ResetFloating`。
- `ReleaseGesture`（Up/Cancel/geometry-cancel）后 Floating 且可见 → `RearmFloatingTimer`。

## 边界

- `RequiresCaretReveal` 协议、`PublishTextInputGeometry`/`Unavailable`/组合保活/重锚链路逐点不变；`NotifyScrolled` 不碰 reveal 标志。
- `GetNextWakeup`（blink/drag-scroll）不改公式；deadline 归 runtime `TimedWakes`。
- `TextField` 零改动；Standard 行为逐点等价。

## 验收

- 溢出编辑内容 wheel/drag/track/键盘 caret ensure（Ctrl+End 等真实滚动）→ 可见；~1000ms 隐藏；hover/drag 续命。
- hidden 期点击 bar 区 = 文本点击（caret 落点、可选区），无死区。
- 被动 clamp（外部文本变短）/parent translate 不显示。
- 组合期用户浏览→caret Unavailable→真实推进再锚定，全链不变（既有 IME 回归绿）。
- disabled/unmount/style 切换 cleanup 同 FS-02。
- Standard 回归全绿（`TextEditorScrollbarTests`/`TextEditorTests`/`TextEditorCompositionTests` 等）。
