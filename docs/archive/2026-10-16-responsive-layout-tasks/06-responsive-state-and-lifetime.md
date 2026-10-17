# RL-06：Responsive 状态依赖与生命周期

创建日期：2026-10-16

状态：已完成（Codex 复审通过）

阶段：D

设计依据：[响应式布局计划](../../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

让响应式 builder 读取状态后自动更新，并正确管理分支身份、卸载、焦点、捕获和资源。

## 前置依赖

[RL-05](05-responsive-transactions.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 推广内部依赖跟踪目标，Responsive builder 有独立读取作用域；状态通知可区分普通 Component 与响应式内容更新，不能因 View 不是 Component 而丢弃失效。
- [x] 使用栈式/可恢复的追踪作用域；条件依赖替换、异常、嵌套组件挂载与卸载后都清理订阅，保持既有 UseState 所有权。
- [x] 禁止 builder 内创建 UseState、同步写状态或借窗口操作触发布局副作用；能够检测的状态写入给出明确异常，IO/外部副作用在公开契约中明确禁止。
- [x] 只挂载活跃分支；跨父级/类型切换按现有规则卸载，不许隐形保留全部候选树；同父级同Key/类型可复用。
- [x] 清理移除控件的焦点、捕获、绑定额外依赖和计时；外层State/Binding数据保留，跨父级编辑态迁移不承诺。

## 测试与验收

- [x] builder 内读取的 State 变化能更新内容，条件读取停止后无残留订阅，卸载节点不再构建。
- [x] 同父级Key复用保持状态，真正移除分支会卸载并清除交互；外层绑定数据跨分支切换保留。
- [x] 错误builder、嵌套Responsive与组件、后台Post、短时间多次状态/宽度变化正确合并，缓存/依赖有界。
- [x] UseState既有全部回归与R18重入/退出回归保留；API文档说明与AdaptiveStack的身份差别。

## 实施记录

### 公开 API

- 无新增公开类型；行为契约写入 `docs/guide/api-reference.md` 的“任意内容分支 Responsive”与“状态与绑定”表：
  builder 是纯描述构建（不得写状态/创建 `UseState`/IO/窗口操作），读取 `State<T>` 自动登记动态内容依赖并自动更新。
- 可检测的副作用在运行时给出明确异常（`InvalidOperationException`）：builder 内写状态（含写入相同值，先于相等性短路）与调用 `UseState`。
  IO/窗口操作（`SetViewport`/`SetEnvironment`/`BuildFrame` 等）不可普遍检测，因此在公开文档中明确禁止。

### 内部契约与边界

- `IStateReadScope`（`Views/Component.cs`）：状态读取作用域的公共接口；`ComponentState` 与 `ResponsiveContentState` 各自实现，
  分别代表组件构建与动态内容构建。`BuildTracker` 改为**栈式**：`Push(scope)` 返回恢复令牌，`using` 释放时还原上一个作用域；
  `BuildTracker.IsDynamicContentBuild` 供副作用守卫查询；`ClearIfCurrent` 处理卸载防御。
- `View.OnStateDependencyChanged(node)`：状态依赖失效的派发入口。`Component` 覆写为重建 `Build`；
  `Responsive` 覆写为 `UiRuntime.InvalidateResponsiveContent`（复用 RL-05 的事务与最终提案机制）。
  `StateBase.NotifyDependents` 不再直接调用 `InvalidateComponent`，非 Component 的依赖节点不会再被静默丢弃。
- `ResponsiveContentState`（`Runtime/ResponsiveTransactions.cs`）承担 builder 的读取作用域：
  `BeginReads()` 清空并压栈，`CommitReads()` 在成功返回后整体替换依赖集合（移除不再读取、补登记新增），
  builder 抛错时不提交（保留上一次成功内容的依赖）；`ReleaseDependencies()` 在节点卸载时释放。
  作用域与 `CommitReads` 之间用 `using` 配对，异常路径同样恢复 tracker。
- `UiRuntime.ExecuteResponsiveContent` 在独立读取作用域内执行 builder；空返回与抛错都给出带节点路径与可用宽度的诊断。
- 依赖集合按节点**引用计数**（`StateBase._dependents` + `_dependentRefs`）：同一节点可同时有构建期读取与绑定补登记两种来源，
  释放其一时不会误删另一来源；`ComponentState.EndBuild` 只对新增/移除的状态登记与释放，重复构建不累积引用。
- `Binding<T>.Read(node)` 的长期依赖现在记录在读取它的节点上（`ViewNode.BindingDependencyRegistrations`）：
  节点卸载或描述被替换（`Reconciler.UpdateNode`，例如复用输入框换用新绑定）时释放；
  `ComponentState.RemoveExtraDependency` 与引用计数配合，不会误删仍然有效的构建依赖。控件被动态内容移除后不再重建已经无关的组件。
- 同步移除 `Responsive` 与 `UiRuntime.InvalidateResponsiveContent` 上“本阶段不提供状态依赖能力/不会自动订阅”的过时 XML 注释，源码提示与已实现能力一致。

### 验证命令与退出码（顺序执行，`--no-restore -m:1 -nodeReuse:false`）

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx` | exit 0，0 错误（仅既有 NU1900 网络警告） |
| `CSharp.UI.Tests` 程序集 | exit 0，299 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Text.Tests` 程序集 | exit 0，67 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Integration.Tests` 程序集 | exit 0，30 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.OpenTK.Tests` 程序集 | exit 0，189 Total / 0 Failed / 0 Skipped（本机桌面权限） |
| 三个示例 Debug 构建（Counter/TodoList/ControlGallery） | 各自 exit 0 |
| Codex 独立探针 `artifacts/responsive-work/responsive-state-invariants` 构建 | exit 0，0 警告 / 0 错误 |
| 探针运行 | exit 0，21/21 PASS（含纯构建副作用与恢复检查） |
| `git diff --check` | exit 0（仅 LF→CRLF 提示） |

四套合计 585，失败/错误/跳过均为 0；新增 15（核心 14：`ResponsiveStateTests.cs`；集成 1：真实 Skia 文本的状态驱动重新排版与高度汇总）。
另外在既有 `PerNodeOscillation_FailsControlledWithPathAndInputThenRecovers` 中补充循环路径 tracker 归位断言（不改变测试数）。
完整命令、日志路径与限制见 `artifacts/responsive-work/rl06-progress.txt`。

### 适用平台与未验证边界

- Windows 本机 .NET 10、DPI 1.0（核心/文本/集成无窗口），OpenTK 使用本机 OpenGL 3.3 与隐藏窗口。
- 未验证：macOS/Linux 真机、三平台人工视觉验收、DPI 1/1.25/2 下响应式布局人工对比、Release 启动检查、真实鼠标拖拽 resize（R18 程序化 resize 回归保留并通过）。
- 已知边界：builder 异常后下一次 `ProcessPendingWork` 会重试该描述（与 RL-05 失败描述恢复一致）；
  跨父级/类型切换不迁移焦点或本地编辑状态；描述被替换时释放该节点在布局/绘制阶段补登记的绑定依赖，随后在下一次测量/绘制重新登记。
- 提交：本地英文提交（精确路径），不 amend、不推送；SHA 见 `git log`。

### Codex 复审

实现 `964a1e9`；独立状态探针21/21通过，Codex独立复跑核心299及集成30项通过，并核对四套585项完整日志、三示例Debug构建及最终变更。直接与附加依赖并集、绑定来源替换、纯构建异常及恢复问题均关闭，详见[审核记录](../../return/2026-10-16-responsive-layout-review.md)。
