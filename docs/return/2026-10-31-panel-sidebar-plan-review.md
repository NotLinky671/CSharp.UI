# Panel/Sidebar 计划主审复核记录（2026-10-31）

日期：2026-10-31。对象：[docs/plan/2026-10-31-panel-sidebar-plan.md](../plan/2026-10-31-panel-sidebar-plan.md)（tag C0F8→FA9B 系列修订）与 [docs/task/](../task/README.md) PS-01~PS-05。

## 结论

整体 API 与任务拆分**已接受**，不阻塞 Panel（PS-02）编码先行。以下为复核中提出并已落文档的修正与边界。

## 已落文档的修正

1. **PS-03 前置措辞**：原文"PS-02（主审通过）"在尚未实现时属于假绿表述，已改为
   "须其经主审通过后才实施本任务"，并显式标注"产品尚未实现"（`docs/task/03-sidebar-implementation.md:5`）。
2. **行身份真边界（新永久测试 C9 / plan §5 数据纪律）**：`Sidebar<int?>` items
   `[null, 0]` 场景——`null` 的 ForEach key 回退会与 `Value=0` 的 index 回退
   碰撞。契约要求：Sidebar 局部用非 null 的 null-key sentinel（**不改**全局
   `ForEach`/`Select` 语义）；**不可用 `ToString` 作 key**（不同值同 ToString
   不冲突）。**指针手势跨 items 变更**：某行 `PointerEventKind.Down` 按下后
   items 重排/删除/禁用/替换，再 `PointerEventKind.Up` 释放——禁止写回另一
   `Value`（命中行身份随 `Value` 跟踪；若写成键盘 Down/Up 语义则本就无提交、
   测试成空断言，已澄清为指针事件）。键盘 active（`Up`/`Down`/`Home`/`End`）
   按 `Value` 保持另行断言。stable-by-value 定义为**可观察语义**（高亮与写回
   值跟随同一 `Value`），不强 pin 内部 VNode 实例。
3. **ensure-visible 机制边界**：行为契约维持（仅键盘 active 触发、wheel/平移/
   外部写不 ensure、最近祖先 `ScrollView`、无祖先/空视口 no-op、有界兑现无
   poll/timer/公共 API）。**已核实事实**（非"仍核查"）：`UiRuntime` key
   dispatch（`UiRuntime.cs` 约 :870-880）路径上**没有** `ProcessPendingWork`；
   `InputRouter.cs` 约 :365-394 已读——键盘事件后不自动跑布局。**允许**最小
   内部单目标 bring-into-view 请求在既有 `ProcessPendingWork` 布局后安全消费、
   按 dirty 有界再布局（不强制全局队列）；**禁止** Arrange 内对祖先布局失效
   （`RunLayout` 清理时序吃掉）；具体实现仍由 good 证明并报主审，不作 stub。

## 接受的合同要点（冻结）

- `Panel(View content)` 单子装饰容器；`ContentPadding`/`BorderThickness` 参与
  布局，`CornerRadius`/`BackgroundColor`/`BorderColor` paint-only；`GetChildClip`
  不变、不做 rounded mask；无 `ShowBorder`/header/footer。
- `Sidebar<T>` 数据/提交/导航/焦点/反应性纪律同 plan §5；`Width` 为自然偏好。
- 范围外：drawer/modal/Router/Shell/折叠/图标/动画/多子 Panel/多选/层级树/公共
  滚动 API——不实现不 stub。
- 开源借鉴（Avalonia/FluentAvalonia/wpfui，均 MIT）仅设计借鉴，不复制代码、
  不新增依赖、不更新 NOTICE。

## 实施工作流分工

- **panel-sidebar-implementation（good）**：src/tests/samples 产品面；先
  Panel + 真实 renderer smoke，并行验证 Sidebar 键盘安全边界；Panel 主审通过后
  才开 Sidebar 编码。
- **本代理（panel-sidebar-plan）**：docs-only——plan/task/return/delivery/README。
- **主审（main）**：每任务独立 read 复审，门槛不过不开下一任务。

## 未知机制（非 stub，如实记录）

ensure-visible 的具体挂点**未冻结**——已核实 `DispatchKeyHandled`/`ProcessPendingWork`
时序事实（见上 §3），允许最小内部单目标 bring-into-view 请求由 good 在实施前
用既有 runtime 安全边界证明后报主审；无安全挂点则如实上报，不作 stub、
不留"未来补"。
