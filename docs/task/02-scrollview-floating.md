# FS-02：ScrollView Floating 接线

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../return/2026-10-23-floating-scrollbar-review.md)。

阶段：B

设计依据：[Floating 滚动条计划](../plan/2026-10-23-floating-scrollbar-plan.md) §决策二/三/五/六/七/八/九/十。

[返回任务索引](README.md)

前置依赖：FS-01。

## 文件范围

- `src/CSharp.UI/Views/ScrollView.cs`：Measure/`ContentRect`/`TrackRect`/`EmitScrollbar`/`HitTestOverlay`/`OnTimedWake`/`OnPointerEnter`/`OnPointerMove`/`OnPointerLeave`/`OnPointerDown`/`OnUpdate`/`Arrange`。
- 对应单元测试（consumer-observable）。

## 实现契约

### 布局（Floating）

- `gutter = style==Floating ? 0 : min(Thickness, max(0,crossExtent))`：Measure 子约束/`DesiredSize`、`ContentRect`、`TrackRect` 全按此取。
- Floating `TrackRect` = frame 沿滚动轴边缘内 `min(Thickness, crossExtent)` 宽条（纵滚右缘、横滚底缘）；`ContentRect` = 全 frame。
- `GetChildClip` 不变（返回 `ContentRect`）；显隐不改 clip。

### 显示源（仅真实 offset 变化）

三处写点写回成功后 `state.NotifyScrolled(node)`（state=`ScrollbarStateOrCreate`；Standard 下 no-op）：

- `OnWheel` 消费且 `next != offset`（:191-206）——边界 wheel（`next==offset` → return false）不显示；
- `OnPointerMove` thumb-drag `next` 变化时（:303-317）；
- `OnPointerDown` track 翻页写 offset（:371-380）——`next==offset` 时不通知（加同值早退比较）。

Arrange clamp（:106-107）不通知。

### 命中与可见门

- `HitTestOverlay`：`ScrollbarStyle==Floating && state.IsFloatingVisible && ShowScrollbar(V,C,T)` → `TrackRect(node.Frame)`；否则 null。
- `OnPointerDown` 的 `ShowScrollbar` 门（:344）换成 `Visible` 门（Floating 下含 `IsFloatingVisible`；Standard 下即 `ShowScrollbar`）——hidden 时 bar 分支不进、pointer 正常落内容/子项。
- `EmitScrollbar` → `ScrollbarPainter.Draw` 传 style+state（painter 内部 Floating+!visible return）。

### hover 抑制（决策七）

- 实现契约（R3/R5 后）：owner 实现 `View.OnHoverDeepestChanged(node, Point position, bool isDeepest)`——`over = isDeepest && IsFloatingVisible && track.Contains(position)`；`over ? SuppressFloatingTimer : (suppressed && PressKind==None → RearmFloatingTimer(now))`。position 为 router 权威 `e.Position`（非 owner 缓存 `LastPointerPoint`）；`RefreshHover(forceDeepestNotify)` 让同 target 下也按真实位置重估（覆盖 child 消费 Move 后 wheel@bar 的静止悬停）。
- `ShowFloating` 置可见后 `runtime.RefreshHover()`（router 用私有 `_lastPointerEvent` 重跑 `UpdateHover`），静止指针续命；出口经 `UpdateHover` 对曾 suppress 的 hover-chain 节点补发 `isDeepest=false` 通知 + `ClearHover` 清空前通知（权威 bool 不重查）。
- hover 判定的 overlay 几何用当下 `TrackRect`+`ShowScrollbar`+`IsFloatingVisible`，不持久存 hover-kind。

### due / cleanup

- `OnTimedWake` override → `state.OnTimedWakeDue(node, now)`。
- `OnUpdate`：`ScrollbarStyle` 变化（immutable 重建换值）→ `ResetFloating`；`Axis` 变化沿用 `CancelBarGesture` + `ResetFloating`。
- `Arrange`：`SyncGeometry` 保持；`ShowScrollbar` 翻 false → `ResetFloating`；disabled → `ResetFloating`（`IsEffectivelyDisabled` 时）。
- gesture `ReleaseGesture`（Up/Cancel/geometry-cancel）后：Floating 且 `IsFloatingVisible` → `RearmFloatingTimer`（drag/track press 期间 suppress）。

## 边界

- Standard 行为逐点等价（gutter/常显/track 绘制/空槽消费语义不变）。
- 无 fade/惯性/新 timer/每帧轮询；hidden idle 零唤醒。
- 不改 wheel 语义/`FlexGrow`/公开滚动 API。

## 验收

- wheel 真实滚动 → bar 可见；~1000ms（TestUiClock 推进）无活动 → 隐藏；隐藏期 `HitTestOverlay`=null、bar 区点击落底层内容。
- 边界 wheel（已到顶/底再滚）不显示；resize/parent translate/被动 clamp 不显示。
- hover 可见条期间不隐藏（deadline 推进超过 1000ms 仍可见）；移出条 → 重新计时后隐藏。
- thumb drag/track page 期间保持可见；release 后重新计时。
- 无 overflow 永不显示、永不命中。
- style 切 Standard→Floating→Standard 状态收敛（不残留唤醒/死区）；axis 变化 cancel gesture+floating。
- disabled → 立即隐藏+无 due；unmount → 无唤醒残留。
- Standard 回归全绿。
