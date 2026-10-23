# RL-05：Responsive 安全内容构建事务

创建日期：2026-10-16

状态：已完成（Codex 复审通过）

阶段：D

设计依据：[响应式布局计划](../../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

在测量和构建之间引入安全的响应式更新阶段，不在 Measure/Arrange 中运行任意用户 builder 或修改节点树。

## 前置依赖

[RL-02](02-flex-and-fill-width.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 实现 ResponsiveInfo(AvailableWidth, IsWidthBounded) 与 Responsive(Func<ResponsiveInfo, View>)；有界宽度占满分配空间，高度自然；无界以 PositiveInfinity/false 表达。
- [x] Responsive 节点先稳定挂载，最终有效 Measure 提案登记到去重队列；Probe 不激活内容，同轮多提案最后有效者覆盖，回到已稳定提案时撤销过时请求。
- [x] UiRuntime.ProcessPendingWork 在安全阶段执行 builder、协调内容并再次布局，稳定后才允许 Emit。首次挂载、父宽度变化和描述更新共用机制，不递归泵事件。
- [x] Reconciler 更新 Responsive 描述时保留原有子树直到事务消费，不能先按空 Children 清空子项；同父级 Key/类型复用规则保持。
- [x] 输入签名包含有效宽度与描述版本；同宽重复 Measure 不重建，而同宽的新 builder 描述仍更新一次，不能误忽略捕获值变化，也不能每次创建闭包都触发布局循环。
- [x] 先用嵌套最小原型证明提案稳定、Probe/有效复测和描述更新，再冻结内部契约；每节点重复构建预算与整体工作上限按计划验证，不把正常深层/批量节点当全局8轮错误。

## 测试与验收

- [x] builder 只在安全阶段执行；观察到的布局/子树完整，Measure 与 Emit 不执行 builder。
- [x] 嵌套超过8层以及批量独立 Responsive 能正常完成；同一节点真正振荡时受控诊断，不忙等或提交半成品。
- [x] 宽度变更、同宽描述替换、首帧、无界探测转有限最终提案、卸载后的过时请求、builder 异常均覆盖。
- [x] R18 回调即时绘制与该稳定阶段共存，不在组件重建期间重入 runtime；本任务尚不标记状态依赖能力已完成，交 RL-06。

## 实施记录

### 公开 API

- `ResponsiveInfo(double availableWidth, bool isWidthBounded)`（`Views/Responsive.cs`）：只读 `AvailableWidth`/`IsWidthBounded`；构造器拒绝 NaN、负宽度以及“有界标记与有限性不一致”，属性没有 `set`/`init`，不能绕过校验。无界以 `PositiveInfinity`/false 表达。
- `Responsive(Func<ResponsiveInfo, View> builder)`：测量只登记最终有效提案；有界宽度占满分配宽度、高度由内容自然决定；无界宽度使用内容自然尺寸。

### 内部契约与边界

- `View.RequiresEffectiveProposal`（`FlexView`/`FillWidthView`/`Responsive`）与 `LayoutContext.SubtreeNeedsEffectiveProposal`：把 RL-02 只识别 `SubtreeHasFlexDeclaration` 的过滤器扩展为通用“最终提案需求”契约，普通 VStack/ScrollView 等无直接弹性的祖先也会把有效测量传到动态内容（含 ZStack/Flow/AdaptiveStack 祖先）。
- `LayoutContext.RequestResponsiveContent`：只有 `IsEffectiveProposal`（非 Probe、非自然测量）才登记；Measure 不执行 builder、不协调/卸载、不递归 `ProcessPendingWork`/`BuildFrame`。
- `ResponsiveTransactionManager`（`Runtime/ResponsiveTransactions.cs`）：按节点合并请求，同轮最后有效提案覆盖；提案与已稳定签名一致且描述版本未变时撤销过时请求；签名 = (AvailableWidth, IsWidthBounded, 描述版本)。
- 描述替换只递增版本、等本轮布局给出最终宽度后构建（避免用过时宽度先构建、重复构建或卸载本应复用的子项）；显式 `InvalidateResponsiveContent` 复用最近有效提案排队，仅在测量递归深度受限（`LayoutDepthLimitException`）时先消费它替换过深旧内容再重试布局，因此“描述/状态/父约束同批变化”只激活最终有效提案。
- `UiRuntime.ProcessPendingWork` 循环：重建组件 → 布局 → 消费本轮登记请求，内容变化后重新布局到稳定才允许 `BuildFrame`；重入 `ProcessPendingWork`/`BuildFrame` 抛出可捕获异常；异常清空队列、整树失效，后续合法更新恢复。
- `Reconciler`：Responsive 描述更新保留既有子树（不按空 Children 清空）；`ReconcileDynamicContent` 复刻组件内容结构规则（单视图不额外加层，多条目/ForEach 包隐式容器）。
- 预算：每节点单轮重建 8；单轮整体构建 4096（与现存节点数量无关）；测量递归深度 256。`InvalidateSubtree`、`Reconciler.Unmount` 改为显式栈遍历，不依赖 StackOverflow。
- `UiRuntime.InvalidateResponsiveContent(viewNode)`：RL-06 状态依赖接入点，当前不改变状态语义。

### 验证命令与退出码（顺序执行，`--no-restore -m:1 -nodeReuse:false`）

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx` | exit 0，0 错误（仅既有 NU1900 与 xUnit 分析器警告） |
| `CSharp.UI.Tests` 程序集 | exit 0，285 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Text.Tests` 程序集 | exit 0，67 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Integration.Tests` 程序集 | exit 0，29 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.OpenTK.Tests` 程序集 | exit 0，189 Total / 0 Failed / 0 Skipped |
| 三个示例 Debug 构建（Counter/TodoList/ControlGallery） | 各自 exit 0 |
| Codex 独立探针 `artifacts/responsive-work/responsive-invariants` 构建 | exit 0 |
| 探针正常模式 | exit 0，24 PASS / 0 FAIL |
| 探针 `--growing` 增长链模式 | exit 0：受控失败（深度 256、builds=256、pending=0）、两次重复处理不再增长、显式失效后恢复；`artifacts/responsive-work/rl05-progress.txt` |

四套合计 570，失败/错误/跳过均为 0；新增 29（核心 26：`ResponsiveTests.cs`；集成 2：`ResponsiveIntegrationTests.cs`；OpenTK 1：`WindowResizeRedrawTests` 响应式即时重绘）。

### 适用平台与未验证边界

- Windows 本机 .NET 10、DPI 1.5；核心/文本/集成无需窗口或 GL，OpenTK 使用本机 OpenGL 3.3 与隐藏窗口（程序化 resize/refresh，不是人工鼠标拖拽）。
- 未实现（RL-06 负责）：builder 内状态读取依赖与自动更新；本轮只留接入点，不宣称状态订阅完成。
- 未验证：macOS/Linux 真机、三平台人工视觉验收、DPI 1/1.25/2 下 Responsive 布局对比。
- 已知边界：无限自嵌套描述不能原地“切回”有限内容，需要 `InvalidateResponsiveContent` 或替换节点；`MaxMeasureDepth` 限制单次布局测量递归深度。
- 提交：RL-05 本地英文提交（实现、注释与回归补充），不 amend、不推送；SHA 见 `git log`。

### Codex 复审

实现 `465d3e0`、注释 `18a2d3b`、父约束回归 `4723e65`。Codex独立执行普通探针24/24、增长链失败/重试/恢复探针退出0；四套测试285+67+29+189=570全部通过，无失败/错误/跳过。已核对三个Debug示例构建记录与变更，实施期栈溢出、公开init绕过、旧宽度瞬时卸载输入框的问题均关闭。详见[审核记录](../2026-10-23-completed-reviews/2026-10-16-responsive-layout-review.md)。
