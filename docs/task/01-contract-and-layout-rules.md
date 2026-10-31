# PS-01 容器/导航契约与布局准则

依据：[plan](../plan/2026-10-31-panel-sidebar-plan.md) §4/§5/§7。
前置：无。状态：**主审通过**（复核记录见 [2026-10-31-panel-sidebar-plan-review](../return/2026-10-31-panel-sidebar-plan-review.md)；key-dispatch/`ProcessPendingWork` 已知事实已写入 plan §5）。

## 目标

把 plan §4/§5 的冻结 API 细化为实现级契约清单：明确 `Panel`/`Sidebar<T>` 的
measure/arrange/emit/输入/绑定/环境纪律与复用的既有机制清单，使 PS-02/PS-03
可直接照单实现，主审可按单验收。本任务为**文档任务**，不改产品代码。

## 交付物

更新本文件（或经主审认可的计划修订）：逐条列出以下契约，每条注明复用的既有实现
（文件/类型），不留"待设计"项。

### Panel 契约清单

1. 构造/属性：`Panel(View content)`、`Content` 只读；init 五属性及默认值
   （`ContentPadding=Thickness(12)`、`BackgroundColor/BorderColor=null`、
   `BorderThickness=1`、`CornerRadius=8`）；null 色 → 当前 `Theme.Surface`/
   `Theme.Border` 按轮读现值。
2. Measure：child 测量约束 = 父约束 deflate(`ContentPadding` + `BorderThickness`
   双侧)；Desired = child.Desired + 同项 inflate；与 `PaddingView`
   （`Views/Modifiers.cs:44`）的 `GetLayoutFeatures` 模式一致，明确本类
   features 申报。**仅 `ContentPadding`/`BorderThickness` 参与布局**——
   `CornerRadius`/`BackgroundColor`/`BorderColor` 为 paint-only（变更只重绘，
   不 re-layout），写明该纪律的实现落点。
3. Arrange：child Frame = 自身内容矩形 deflate 同上；自身 Frame 由父分配。
4. Emit：背景（含 CornerRadius 圆角矩形，同 `BorderView`/`CornerRadiusView` 现有
   draw 命令语义）→ 子内容 → 边框；不写新 DrawCommand 类型（复用现有图元）。
5. 裁剪纪律：`GetChildClip` 不变、**不做 rounded mask**、不新增内容裁剪；写明
   "圆角为装饰、`ContentPadding=0` 时子内容可能覆盖圆角像素"为已知取舍
   （不虚称防覆盖）；父 clip/显式 `ClipView` 正常生效。
6. 环境/禁用/透明度：走既有 `Environment` 继承，无特殊行为。

### Sidebar 契约清单

1. 构造/校验：`Sidebar<T>(Binding<T>, IReadOnlyList<SidebarItem<T>>, string? header)`；
   `Value`/`Items`/`Header` 只读；`SidebarItem<T>` record 签名；
   `Items` 快照 + `Value` 唯一 → `ArgumentException`（`EqualityComparer<T>.Default`）；
   `Label==null` → `Value?.ToString() ?? ""`。
2. 尺寸：`Width` init 默认 200 = 自然偏好（measure Desired.Width=min(Width,约束)），
   可 `Frame`/`Flex`/`FillWidth` 覆盖；自然高 = header 高 + Σ行高；行高
   `max(32, 文本行高)`；label 单行 ellipsis（明确复用的文本排版请求参数）。
3. 结构：行视图内部组成（`InteractionState` active/hover 内部态；行非 focusable——
   写明与 `Select` 选项行同一纪律及实现落点）；owner `IsFocusable=true`。
4. 提交：pointer Up 同行 / `Enter`/`Space` → `Value` 写一次（同值重选仍写一次）；
   重复 `Enter`/`Space` 忽略。
5. 导航：`Up`/`Down` 移 active 跳 disabled 两端停；`Home`/`End` 首/末非 disabled；
   只 move 不写；`Tab`/`Shift+Tab` 不消费；`Ctrl`/`Meta`/`Alt` 组合不导航/确认。
6. 不写路径汇总：导航/焦点进出/失焦/外部绑定变化/items 替换/disabled 翻转。
7. ensure-visible 行为契约（**不把实现方案写死**）：仅键盘 active 移动
   （`Up`/`Down`/`Home`/`End`）触发；用户滚轮/纯内容平移/外部绑定变化不重新
   ensure（避免回弹）；作用对象为最近祖先 `ScrollView` 内目标行可见性
   （行已完全可见则不动）；无 `ScrollView` 祖先或空视口 → 安全 no-op；
   单次有界兑现，不忙等/不新增 poll/timer/不暴露公共滚动 API。
   **批准机制（main 2026-10-31）**：Runtime 单 slot 按需 bring-into-view 请求——
   仅键盘 active 挂目标；在既有 `ProcessPendingWork` 稳定 while 内、仅当
   `!progressed` 时消费一次：清 slot、target 合法（mounted/enabled/focused）
   且 delta 非 0 → 写最近祖先 `ScrollView` offset 并 `progressed=true`，沿既有
   `_layoutDirty` 收敛链；focus/validate/hover/text-geometry 在最终 frame 运行。
   pending 可按 owner+描述/active `Value` 解析最新 row；失焦/卸载自然 drop；
   二次布局不得再请求。已知事实：key dispatch 路径无 `ProcessPendingWork`
   （`UiRuntime.cs` 约 :870-880、`InputRouter.cs` 约 :365-394 已核实）；**禁止**
   Arrange 内祖先 invalidate 与 `_focus.Validate` 后手工 `RunLayout` 绕过
   responsive/overlay 收敛链。方法名/字段由 good 证明后报主审，不 pin 私有名。
8. items 替换/重排：按 `Value` 保身份与 active；`Binding` 实例更换写当前绑定；
   `Binding.Read(node)` 登记依赖；外部 `State` 变化驱动重绘；`Theme` 按轮现值。
9. 命中/裁剪：遵守父 clip 与显式 `.Clip()`；行命中区与视觉一致。

## 验收

- A1：清单逐条给出既有机制落点（类型/文件），无"待设计"、无与 plan 冲突项。
- A2：主审 read 确认无歧义后才允许开 PS-02。
