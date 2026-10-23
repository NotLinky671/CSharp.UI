# FS-04：输入/调度回归（hit 优先 / hidden 透明 / 嵌套 / deadline / IME）

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../return/2026-10-23-floating-scrollbar-review.md)。

阶段：C

设计依据：[Floating 滚动条计划](../plan/2026-10-23-floating-scrollbar-plan.md) §决策三/四/七/八/十一/十二/十三。

[返回任务索引](README.md)

前置依赖：FS-02、FS-03。

## 文件范围

- `tests/CSharp.UI.Tests/` 新增 `FloatingScrollbarTests`（或并入既有滚动条测试文件，按命名惯例）；必要时增补 `InputRouter`/`UiRuntime` 回归用例。
- 产品代码仅在复审发现缺陷时按 `docs/return/` 返修，不在本任务新扩行为。

## 实现契约（回归覆盖）

### 命中协议

- 可见 floating bar 区域点击 → owner bar 分支（thumb drag / track page），不落到下层内容；同点 hidden → 正常落内容/子控件（Button 可点、TextEditor caret 落点）。
- 子内容优先仅在 bar 区外成立：bar 条内可见时 owner 优先（parent bar 盖 child 与 paint 一致）；嵌套 ScrollView（外层 floating + 内层）不交叠各归各，交叠区归外层。
- 祖先 clip（外层 ScrollView `GetChildClip`/`Clip`）外不命中 overlay；`IsHitTestVisible`/disabled 节点 overlay 不命中。
- hidden（未滚动/已隐藏/无 overflow/disabled）→ `HitTestOverlay` null：不制造死区。

### 调度

-  armed 期间 `OnClockTick` 不触发额外 `InvalidateRender`（focused 判据分离；用 `NeedsDraw`/帧数或计数对比验证——consumer-observable 边界内取证）。
- deadline 到期 → `OnTimedWake` 一次、隐藏、`InvalidateRender`；二次 tick 不重复 due。
- hover/drag 抑制期无 armed 槽：时钟推进远超 1000ms 不隐藏；无唤醒（`GetNextWakeup` 不为 floating 供值）。
- unmount（树重建移除节点）→ 无唤醒残留、无异常；disabled → 立即隐藏+无 due。
- 同 tick due 回调内再 `RequestTimedWake`（理论上 release-rearm 交错）不二次触发、不抛迭代异常。

### 显示源矩阵

- wheel 真滚/thumb drag/track page/键盘 caret ensure/编辑 ensure/拖选自动滚动 → 显示+重新计时。
- 边界 wheel（无 offset 变化）、同值写、被动 clamp（内容 shrink 至仍溢出/不溢出）、resize、parent translate、DPI/布局重排 → 不显示不延期。
- own offset 写回不 cancel gesture（drag 中 wheel/同 frame 几何）。

### TextEditor IME 回归

- 组合期用户 wheel/bar 浏览 → caret 裁出 → `Unavailable` → 组合保活不取消；真实推进（新 preedit/caret）→ 再锚定；同值 PPW/时钟不回弹。
- bar 命中不 `SetFocus`、不 cancel composition、不改 caret/selection/history/binding。
- `TextField` 零改动回归。

## 验收

- 新回归用例全绿；既有四套件（core/text/integration/OpenTK）Debug 全绿。
- 无 wiring/mockecho/源码字符串测试；不 pin 内部字段名/常量字面（用 `TestUiClock` 推进与命中/绘制命令断言转换）。
- 旧 incidental 假设（gutter 恒 12 之类）如需调整按主审结论更新，不 re-pin。
