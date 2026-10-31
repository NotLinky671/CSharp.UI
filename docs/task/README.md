# Panel 容器与 Sidebar 导航任务索引

创建日期：2026-10-31

设计依据：[Panel 与 Sidebar 实施计划](../plan/2026-10-31-panel-sidebar-plan.md)。

状态：**PS-01~PS-05 全部主审验收通过，代码与文档收口完成**（复核：[计划](../return/2026-10-31-panel-sidebar-plan-review.md)、[Panel](../return/2026-10-31-panel-review.md)、[Sidebar](../return/2026-10-31-sidebar-review.md)、[Gallery](../return/2026-10-31-gallery-review.md)；交付：[2026-10-31-panel-sidebar-delivery](../delivery/2026-10-31-panel-sidebar-delivery.md)；提交信息见 git 历史）。前一任务组（P-01~P-07 Popup/Select）已完成并归档至
[docs/archive/2026-10-31-popup-select-tasks/](../archive/2026-10-31-popup-select-tasks/README.md)。

## 核心决策（计划已冻结，以下为摘要——细节以 plan 为准）

- **`Panel(View content)`**：单子装饰容器；init `ContentPadding`(默认 `Thickness(12)`)、
  `BackgroundColor`/`BorderColor`（nullable → 当前 `Theme.Surface`/`Theme.Border`）、
  `BorderThickness`（默认 1）、`CornerRadius`（默认 8）。无边框用 `BorderThickness=0`；
  无 header/footer（调用方 `VStack`）；**无新增圆角内容裁剪**（装饰圆角同 `Border` 语义，
  遵守父 clip 与显式 `.Clip()`）。绘制：背景 → 子 → 边框。
- **`Sidebar<T>(Binding<T>, IReadOnlyList<SidebarItem<T>>, string? header = null)`**：
  `SidebarItem<T>(T Value, string? Label = null, bool Disabled = false)` 与 `Select` 同
  快照/唯一值/可空纪律；`Width` init 默认 200 为**自然偏好**非硬宽；label 单行 ellipsis，
  行高 min 32（字体大可增）；单 focus owner、行非 focusable；pointer Up 同行 /
  `Enter`/`Space` 提交一次（同值重选一次）；`Up`/`Down`/`Home`/`End` 跳 disabled 只
  move active；`Tab` 不消费；`Ctrl`/`Meta`/`Alt` 不当导航/确认；重复 `Enter`/`Space`
  不重复提交；未知值无高亮不自动纠正；items 替换按 `Value` 保身份、`Binding` 更换写当前。
- **滚动**：超高交调用方 `ScrollView`；仅键盘 active 移动触发 ensure-visible
  （实现挂点由 good 用既有 runtime 安全边界证明，不写死未证方案）；
  无公共滚动 API。
- **不做**：drawer/modal/Router/Shell/页面栈/内置折叠断点/图标/动画/多子 Panel/
  分组 header 列表/多选/层级树——不实现不 stub 不假渲染。
- **模块所有权**：good 负责 src/tests/samples 产品面；docs 规划由本索引维护者负责；
  main 每任务独立 read 复审。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | PS-01 | [容器/导航契约与布局准则](01-contract-and-layout-rules.md) | 无 | A |
| ☑ | PS-02 | [Panel 实现 + 消费者边界](02-panel-implementation.md) | PS-01 | B |
| ☑ | PS-03 | [Sidebar\<T\> 实现](03-sidebar-implementation.md) | PS-02 | B |
| ☑ | PS-04 | [Gallery 真实集成与原生实证](04-gallery-integration.md) | PS-02、PS-03 | C |
| ☑ | PS-05 | [文档收口与交付验收](05-docs-delivery.md) | PS-04 | C |

依赖链：PS-01 → PS-02 → PS-03 → PS-04 → PS-05（串行）。

## 阶段门槛

- A：契约清单经主审确认无歧义，PS-02/PS-03 可直接照契约实现。
- B：`Panel`/`Sidebar` API 与行为契约闭合；永久测试断言消费者可观察行为全绿。
- C：Gallery 集成 + 原生实证 raw；双 config 0w0e；五套件 fresh 全绿；文档更新；
  交付记录；主审门 → 单次英文 commit/push。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件。
- 永久测试只承诺消费者可观察行为与既有计数器，不 pin 内部字段名/常量字面/源码字符串；
  不引入 wiring/mockecho 测试。
- 范围外：drawer/modal/Router/Shell/折叠断点/图标/动画/多子 Panel/分组/多选/层级树、
  公共滚动 API——不实现、不 stub、不假渲染。
- 原生实证用 own PID/own window 并记录；禁止按名称 kill；截图走既有
  ZLib/`GL.ReadPixels(FRONT)`，不用 GDI 未知通道。
