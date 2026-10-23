# Floating 滚动条任务索引

创建日期：2026-10-23

设计依据：[Floating 滚动条实施计划](../plan/2026-10-23-floating-scrollbar-plan.md)。

状态：**FS-01~FS-07 全部完成并主审验收通过**（最终验收 2026-10-24）。交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../return/2026-10-23-floating-scrollbar-review.md)。用户已授权实施。前置共享滚动条任务组 SB-01~SB-07 已完成并归档至 [docs/archive/2026-10-23-scrollbar-tasks/](../archive/2026-10-23-scrollbar-tasks/README.md)。

## 核心决策（已冻结，见计划全文）
- **公开 API**：`public enum ScrollbarStyle { Standard, Floating }`；`ScrollView.ScrollbarStyle`/`TextEditor.ScrollbarStyle` 可 `init`，默认 `Standard`（persistent 12px gutter 现状不变）；无第二可见性枚举、无公开滚动命令。
- **Floating**：滚动条覆盖内容边缘不占布局槽、显隐不改内容宽；overflow+实际滚动显示、约 1000ms 无活动隐藏；hover/drag 续命；hidden 完全不拦命中；可见 bar 经 `View.HitTestOverlay` 在子内容前命中（祖先 clip 内）；不画 track、thumb 更细中性灰；无 fade/惯性/触摸/新动画。
- **显示源仅限真实 offset 变化**：wheel/bar/track/键盘-caret ensure/edit 引起者；被动 range clamp/parent translate/边界 wheel 不显示不延期。
- **唤醒**：`UiRuntime` 内部 `TimedWakes`（单槽位 deadline + armed 标记 + 逆序遍历），due 回调走独立 `View.OnTimedWake`（不混 focused `OnClockTick`）；`OnClockTick` 尾部重绘判据仍只看 focused——future deadline 不导致每事件重绘，hidden idle 零唤醒。
- **复用**：`ScrollbarMetrics`/`ScrollbarState`/`ScrollbarPainter` 单套扩展，不复制状态机；TextEditor IME/caret/anchor/history/binding 语义不变；TextField 零改动。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | FS-01 | [基础件：ScrollbarStyle 枚举 + TimedWakes + HitTestOverlay 钩子 + ScrollbarState/Painter 扩展](01-floating-foundations.md) | 无 | A |
| ☑ | FS-02 | [ScrollView Floating 接线](02-scrollview-floating.md) | FS-01 | B |
| ☑ | FS-03 | [TextEditor Floating 接线](03-texteditor-floating.md) | FS-01 | B |
| ☑ | FS-04 | [输入/调度回归（hit 优先/hidden 透明/嵌套/deadline/IME）](04-input-schedule-regression.md) | FS-02、FS-03 | C |
| ☑ | FS-05 | [示例（ControlGallery Floating 演示）](05-samples.md) | FS-02、FS-03、FS-04 | C |
| ☑ | FS-06 | [文档更新（guide/api/README）](06-docs.md) | FS-02~FS-05 | C |
| [OK] | FS-07 | [交付（双 config 构建+四套件+真实 smoke+记录）](07-delivery.md) | FS-06 | D |

## 阶段门槛

- A：`ScrollbarStyle` 公开枚举与默认值冻结；`TimedWakes` 到期/注销/unmount 语义落地；`HitTestOverlay` 路由不破坏 child-first；`ScrollbarState`/`Painter` 扩展后 Standard 行为逐点等价。
- B：两 owner Floating 全路径接线（几何/显示源/hover 抑制/cleanup 矩阵），各自 consumer-observable 测试绿。
- C：hit 优先/hidden 透明/嵌套/deadline 唤醒与无 busy-loop 回归 + Gallery 演示 + 文档一致。
- D：Windows 真实窗口 smoke 证据与交付记录；macOS/Linux 仅源码/CPU 编译。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件。
- FS-02 与 FS-03 文件不同可并行；FS-05 仅碰 samples 可与 FS-04 并行。
- 所有任务只承诺消费者可观察行为与既有计数器，不承诺伪精确性能数字；不引入 wiring/mockecho/源码字符串测试；永久测试不 pin 内部字段名/常量字面（1000ms 经 `TestUiClock` 推进验证）。
