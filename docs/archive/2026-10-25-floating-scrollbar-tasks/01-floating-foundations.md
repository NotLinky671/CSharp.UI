> **历史归档**（2026-10-25 移自 `docs/task/`）：本任务已完成并验收通过，仅供历史追溯；当前任务索引见 [docs/task/README.md](../../task/README.md)。

# FS-01：基础件（ScrollbarStyle + TimedWakes + HitTestOverlay + ScrollbarState/Painter 扩展）

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../../return/2026-10-23-floating-scrollbar-review.md)。

阶段：A

设计依据：[Floating 滚动条计划](../../plan/2026-10-23-floating-scrollbar-plan.md) §决策一/三/四/五/九。

[返回任务索引](README.md)

前置依赖：无。

## 文件范围

- `src/CSharp.UI/Views/Scrollbar.cs`：`FloatingHideDelay`(1000ms)/`FloatingThumbThickness`(4) 常量；`ScrollbarState` 新增 `IsFloatingVisible`/`FloatingHideDeadline`/`FloatingSuppressed` 与 `ShowFloating`/`OnTimedWakeDue`/`SuppressFloatingTimer`/`RearmFloatingTimer`/`ResetFloating`/`NotifyScrolled`（armed 态由 `TimedWakes` 统一持，state 内无 `FloatingTimerArmed` 镜像）；`ScrollbarPainter.Draw` 加 style/floating 分支。
- `src/CSharp.UI/Views/View.cs`：`internal virtual Rect? HitTestOverlay(ViewNode node) => null`；`internal virtual void OnTimedWake(ViewNode node, TimeSpan now)` 默认 no-op。
- `src/CSharp.UI/Input/InputRouter.cs`：`HitTest` children 逆序循环前 overlay 检查（`insideAncestorClip && !IsEffectivelyDisabled && Frame.Contains(position) && overlay.Contains(position) → node`）。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：`TimedWake` **值类型**（`private struct`，非 class）+ `_timedWakes` + `RequestTimedWake`/`CancelTimedWake`/`NextTimedWakeDelay` + `DispatchDueTimedWakes`（`try/finally` + `_timedWakeDispatching`）；`OnNodeUnmounting` 摘除；`GetNextWakeup` min 合并；`OnClockTick` 逆序 due 遍历 + focused 判据分离。
- `src/CSharp.UI/Views/ScrollView.cs` / `src/CSharp.UI/Views/TextEditor.cs`：仅 `public ScrollbarStyle ScrollbarStyle { get; init; } = Standard` 属性与 `TextEditor.With` 复制（其余接线属 FS-02/03）。
- 对应单元测试。

## 实现契约

### 公开枚举

```csharp
public enum ScrollbarStyle { Standard, Floating }
```

放 `Scrollbar.cs` 顶部（`ScrollAxis` 同族），XML 文档写明两值语义；默认 `Standard` = 现 persistent gutter 行为。

### HitTestOverlay 路由

- 位置：`InputRouter.HitTest` 递归内、`insideAncestorClip` 之后、children 循环**之前**；guard 为 `insideAncestorClip && !node.IsEffectivelyDisabled && node.Frame.Contains(position)`（禁用/frame 外/clip 外永不命中 overlay）。
- `node.View.HitTestOverlay(node)` 返回 null → 完全透传（hidden/Standard/无 overflow 不制造死区）；非空且 `overlay.Contains(position)` → return node。
- 返回 node 后走既有 `OnPointerDown` bar 分支；不新增输入通道、不硬编码 owner 类型；除此之外维持原 child-first 次序。

### TimedWakes（UiRuntime 内部）

- `TimedWake { ViewNode Node; TimeSpan Deadline; bool Armed; }`，`List<TimedWake>` 有界；节点级单槽位（重挂即覆盖）。
- `OnClockTick`：逆序索引遍历——失效槽（`!IsMounted || IsEffectivelyDisabled || !Armed`）`RemoveAt` 清理；`Armed && now>=Deadline` → 先 `Armed=false`+`RemoveAt` 再 `Node.View.OnTimedWake(node, now)`；之后按原顺序 tick focused。
- `GetNextWakeup()`：`min(focused?.View.GetNextWakeup, NextTimedWakeDelay(now))`，遍历惰性剔除失效槽。
- `OnClockTick` 尾部重绘判据只看 `focused.View.GetNextWakeup(...)`（原语义）；future deadline 不触发每 tick 重绘。
- 回调期结构修改仅经 armed 标记+槽位增删；逆序索引+`RemoveAt`，无 foreach/`ToArray`/枚举器；单节点单 tick 至多一次 due。
- `OnNodeUnmounting` 内 `CancelTimedWake(node)` 兜底。

### ScrollbarState / Painter 扩展

- `ShowFloating(node,now)`/`OnTimedWakeDue(node,now)`/`SuppressFloatingTimer`/`RearmFloatingTimer`/`ResetFloating`/`NotifyScrolled` 语义按计划 §决策五；suppress 中 `ShowFloating` 只置可见不动计时。
- `ScrollbarPainter.Draw`：Floating+`!IsFloatingVisible` → return；Floating 不画 track、thumb cross 宽 `FloatingThumbThickness`(4) 居中于 track、圆角 `min(W,H)/2`、中性灰三态混色不变；Standard 现状不变。

## 边界

- 不改 `OpenTkHost`/dispatcher/`IUiClock`；不新增公开 API（`TimedWakes`/`OnTimedWake`/`HitTestOverlay` 全 internal）。
- Standard 路径逐点等价：绘制/布局/命中/唤醒不变。
- 不引入 `HashSet`/`System.Threading.Timer`/每帧轮询/`DateTime`。
- ScrollView/TextEditor 本任务只加 `ScrollbarStyle` 属性——不接 floating 行为（避免半成品接线）。

## 验收

- `ScrollbarStyle` 默认 Standard 可 init Floating（两 owner）。
- TimedWakes：armed 到期回调一次且 `InvalidateRender`；cancel/unmount/disabled 无残留唤醒；同 tick 重注册不二次触发；future deadline 存在时 `OnClockTick` 不额外重绘（对比 focused 判据）。
- HitTestOverlay：非空 rect 优先于 children；null 完全透传；祖先 clip 外不命中。
- Standard 行为回归全绿（既有 scrollbar/scrollview/texteditor 测试）。
