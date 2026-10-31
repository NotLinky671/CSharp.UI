# P-03 开发记录：公开 Popup 与 IsOpen 双向纪律（2026-10-29）

任务：[P-03 Popup 公开 view 与 IsOpen 双向纪律](../archive/2026-10-31-popup-select-tasks/03-popup-view.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md) §4
前置：P-02（主审已 read 接受；本工作流复审结论见下）
设计依据：[2026-10-29-popup-select-p03-design.md](2026-10-29-popup-select-p03-design.md)
状态：**实施完成，冻结待主审 read + fast 独立 actual Popup public API probe；未勾选验收、未提交。**
证据目录：`docs/return/2026-10-29-popup-select-p03-evidence/`（原始 stdout/stderr + 真实 exit code，UTF-8）。

## P-02 read 复审结论（本工作流独立读实际源码）

**无契约阻断，接口接受。** 逐项核对：

- `PopupPlacementCalculator.Resolve(Rect anchorRect, Size contentDesired, Rect popupBounds,
  PopupPlacement preference, double maxHeight)`——与实际源码一致；**无 margin 形参**（`popupBounds`
  已内缩，未二次内缩）；非有限输入 → `null`；`w≤0 || h≤0 || 边界空` → `null`；Above/Below 强制方向，
  Auto 才翻转；`h=min(desired,maxHeight,sideAvail,bounds.H)`；左右/上下 clamp 正确。
- `ActiveOverlayEntry` 带 `Preference`，`Show(...)` 末参 `preference`（含枚举校验）。
- `ArrangeEntry`：`_geometryInvalid` 在 Arrange 只排空矩形，关闭留给安全阶段（符合“布局期不 reconcile”）；
  最终尺寸不同于 DesiredSize 时至多一次 `MeasureFresh`。
- `MeasureEntry` 语义未变（锚环境不同则强制重测）。
- 14 个 P-02 用例只断言 `Rect?`/最终 Frame/挂载，无内部字段 pin。

## 本任务实现

### 公开 API

- `src/CSharp.UI/Views/Popup.cs`（新）：
  `public enum PopupPlacement { Auto, Above, Below }` +
  `public sealed class Popup : View`：
  - 构造 `Popup(View anchor, View content, Binding<bool> isOpen, PopupPlacement preference = Auto)`；
  - `Anchor`/`Content`/`IsOpen` 只读属性；`Preference`/`Margin`/`MaxHeight`/`CloseOnOutsidePress`/
    `CloseOnEscape` 为 init 属性（构造与 init 均校验：Margin 有限非负、MaxHeight 正或 +∞、preference 合法枚举，
    非法显式抛 `ArgumentOutOfRangeException`，不静默兜底）；
  - 布局照 `ModifierView` 的透传约定：`Children=[Anchor]`（固定数组）、Measure 透传约束取锚 Desired、
    Arrange 同 bounds、`GetLayoutFeatures` 透传锚、默认 Emit 只画 Children——弹层 content 不参与主布局/绘制；
  - `OnMount` → `Overlay.RegisterOwner(node)`；卸载由运行时既有 `OnNodeUnmounting` 处理。
- **枚举干净迁移**：删除 P-02 的 `internal enum PopupPlacementSide`，全部 callers（calculator、manager、
  P-02 单测）改用公开 `PopupPlacement`；**无 alias、无 shim、无双套枚举**。`PopupPlacementCalculator`
  保持 internal（与公开枚举不同名，无冲突）。

### owner 生命周期（manager 拉取式状态机）

- `src/CSharp.UI/Runtime/OverlayOwner.cs`（新，internal）：
  `IOverlayOwner { TryGetOpenRequest(node, out OverlayOpenRequest), OnOverlayClosed(node, reason) }`、
  `OverlayOpenRequest(Anchor, Content, Margin, MaxHeight, Preference)`、
  `OverlayCloseReason { OwnerRequest, OutsidePress, Escape, AnchorIneligible, WindowDeactivated, Displaced, Unmounted }`。
- `OverlayManager`：
  - owner 注册表 `List<ViewNode>`（注册序 = 主树声明序）+ `Dictionary` 状态（按引用）+ `HashSet` 幂等注册；
    只在 mount/unmount 增删，**拉取循环内不增删**（索引遍历 + 每步 `IsMounted`/状态复核）。
  - `ConsumePending`：`PollOwners()` → 预算检查 → `ApplyOwnerRefresh()` → `_geometryInvalid` → 挂起关闭
    （原因随请求）→ 资格校验 → 挂起打开（挤占）。
  - 状态机：`Requested` + `AwaitingOpenEdge`；rising edge = 观察到 false→true；内部关闭后 binding/flag 恒 true
    **不重开、不重复写**；资格门不满足不写、不设 await（恢复后首次即开一次）；同阶段多个 owner 请求后者胜，
    前一个 owner 收到 `Displaced`（写 false 一次 + await）。
  - `Close(OverlayCloseReason reason = OwnerRequest)`：P-04 的 Esc/外点将来只调用同一入口；
    `OnWindowDeactivated()` → `Close(WindowDeactivated)`（写回一次、不改主树焦点）。
  - 关闭通知：`OwnerRequest` 不回写；`OutsidePress`/`Escape`/`AnchorIneligible`/`WindowDeactivated`/
    `Displaced`/`Unmounted` 每个 entry **恰好通知一次**；`OwnerRequest` 的关闭在资格校验之前消费，
    避免“owner 已写 false + 再写一次”的双计数。
  - **content/参数更新**：拉取时只有引用/值真的不同才登记刷新；消费阶段用
    `Reconciler.UpdateNode(surfaceNode, new ChildDescriptor(new PopupSurfaceView(anchor, content), ...))`
    替换 **surface 节点自身**描述（节点身份保持、content 子树按 key/类型复用）；Margin/MaxHeight/
    Preference 变化只做一次 record `with` + 宿主重排。**未变化的一轮不产生任何安全阶段工作、不置 layout dirty。**
  - 嵌套拒绝：owner 请求在拉取时即判嵌套（锚已挂接或 `_mounting` 期未挂接）→ 诊断一次 + 置 await 抑制
    每轮重复请求；消费阶段保留兜底复检。
- `Popup` 读取：`IsOpen.Read(node)`（登记来源 `State` 依赖），`TryGetOpenRequest` 只读当前描述，不建视图。

## 实施中定位并修复的真实缺陷（非 fixture/断言问题）

首次实现把内容刷新写成 `Reconciler.ReconcileDescriptors(surfaceNode, ...)`——该 API 协调的是**入参节点的子项**，
于是每次刷新都在 surface 之下又建一层 `PopupSurfaceView` 子节点、surface 自身 `View` 从未替换 →
`NeedsRefresh` 每轮为 true → 安全阶段反复消费（被 P-01 的 16 次预算拦住并抛 `InvalidOperationException`，
即预算按设计生效）。改为 `Reconciler.UpdateNode(surfaceNode, ...)` 后 C7 通过，且**断言未放宽**：
`Assert.Same(popupNode)` + `Assert.Same(contentTextNode)` + `IsMounted` + 文本 `popup 0→popup 1`
（组件节点状态保留，不 unmount/remount）。调试用的临时诊断已全部移除（`grep DBG` 为空）。

## 测试（`tests/CSharp.UI.Tests/PopupViewTests.cs` 新增 12 个用例）

| 用例 | 覆盖 |
|---|---|
| `C1_IsOpenBindingMountsAndUnmountsRealPopupSubtree` | true→挂载（命中在 popup 子树、锚布局/命中不变）；false→卸载 |
| `C2_InternalCloseWritesFalseOnce_AndOnlyANewEdgeReopens` | 内部关闭（`Close(Escape)` 入口）写回 false 恰好一次；之后 3 轮不重开不重写；真边沿才重开 |
| `C3_ConstantTrueBindingStaysClosedAfterInternalClose` | getter 恒 true：内部关闭后写一次即抑制，无每帧自激 |
| `C6_DisabledGateRejectsOpenWithoutWritingBack_AndOpensOnceEligible` | 资格门不满足不回写、恢复后首次即开 |
| `C6_PopupContentUsesAnchorLocalEnvironment` | popup content 环境=锚局部环境（Theme 作用域），主树同级不受影响 |
| `C7_ContentDescriptionUpdateReconcilesWithoutRemount` | content 描述更新：surface/content 节点复用、state 保留 |
| `C8_PreferenceForcesPlacementSide` | Above/Below 生效（Frame 断言） |
| `C9_SecondOwnerWins_FirstStaysClosedWithoutPerFrameCompetition` | 两个初始 true：后者胜、稳定单 entry、无每帧竞争 |
| `C10_NestedPopupInsideContentIsRejected_OuterStaysOpen` | 公开 Popup 嵌套：拒绝、外层保持、诊断只报一次 |
| `C12_OwnerUnmountClosesPopupAndWritesFalseOnce` | owner 节点卸载：关闭 + 写回一次 |
| `C13_ForEachAnchorStacksAllRows_AnchorsToWholeSubtree_AndRowsStayClickable` | ForEach 锚：两行都参与布局且不重叠、popup 以整棵锚子树底边定位、第二行仍正常点击回调 |
| `C0_StableOpenPopupDoesNotProduceExtraLayoutWork` | 打开态稳定/关闭后均不产生额外 layout pass（空闲不空转） |

P-01 既有回归（A1–A16，含 OnMount 嵌套/OnMount 关闭/OnUnmount 打开/自激预算）全部保持通过。

## 验证（原始 stdout/stderr 与真实 exit code；UTF-8）

| 命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug` | 0 警告 0 错误，exit 0 | `build-debug.stdout/stderr/exit.txt` |
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0（真 fresh） | `build-debug-fresh.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **758**, Failed 0，exit 0（746 + 12） | `CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `CSharp.UI.Integration.Tests.*` |
| 同上（含 throwaway 公开 API smoke） | Total **57**, Failed 0，exit 0 | `smoke-public-popup.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `CSharp.UI.Markdown.Tests.*` |

- **消费者 smoke**（throwaway，运行后删除、`git status` 无残留）：真实 Skia 文本 + headless runtime +
  公开 `new Popup(anchor, content, state.ToBinding(), PopupPlacement.Below) { Margin = 6 }`：
  `State` true → 挂载、真实文本宽 >0、`popup.Frame.Y == anchor.Frame.Bottom`、PushClip/Texture 落在 popup 区域；
  `Overlay.Close(Escape)` → 写回 false 一次并卸载；`State` true（组件重建的新 binding 实例）→ 重新挂载。
- **OpenTK 套件本轮不跑**：已知 `GalleryIdleAndBoundsTests.极窄视口…`（Markdown Copy 按钮 80px）为基线
  preexisting，与本任务无关；P07 统一处理（主审已给最小一般性修复方向，本任务不混改）。
- 未做任何 `git add`/commit/push；未改写 P-01/P-02 的 return 与 evidence。

## 返修批次：ForEach 锚（M14）与测试卫生（2026-10-29 post-anchor）

**fast 独立 probe 结论**：13 个主场景 PASS，M14（ForEach 锚）真实 FAIL——两行都落在
`(8, 8, 344×17.5)` 重叠，popup 贴的是首行底（25.5）而不是整棵锚子树底（43）。原始失败日志保留在
`2026-10-29-popup-select-p03-evidence/P03-independent-run*.stdout.txt`（本轮未改写）。

**根因（源码确认）**：`Popup._children = [Anchor]`，协调器把 `ForEach` 展开为多个同级节点 →
`Popup.Measure/Arrange` 给每个子项同一 bounds（互相重叠），`TryGetOpenRequest` 取 `ChildList[0]`
（只按首项定位）。注释“多条目锚不支持”不构成合法的 scope 收缩（`Anchor` 是公开的任意 `View`）。

**修复**：构造期 `_children = [Reconciler.NormalizeRootView(anchor)]`（与 P-01 用户根同一归一化模式）——
公开 `Anchor` 仍是调用方原描述；单视图归一化后仍是同一描述实例（不引入额外层级），`ForEach`/多条目
归一化为一棵锚子树，Popup 因此始终只有一个布局子项，Measure/Arrange/特征透传不变，定位与锚豁免
以该子树根 Frame 为准。空 `ForEach` → 锚 Frame 为空 → 既有资格门拒绝打开（不产零尺寸 popup）。

**同批测试/文档卫生**：

- 删除 `C4_C5_CloseFlagsAndPlacementAreExposedOnPopup`（只钉 init 属性默认值与构造异常 = wiring，
  无行为；P-04 用真实外点/Esc 覆盖，P-02 已有几何）。
- 新增唯一永久回归 `C13_ForEachAnchorStacksAllRows_AnchorsToWholeSubtree_AndRowsStayClickable`：
  两行不重叠、popup 以整棵锚子树底边定位、点第二行仍触发其回调（不 pin 子节点数量/归一化类型名）。
- `Popup` XML：去掉指向不存在/内部类型 `OverlayHost` 的 cref；`Anchor` 与 `TryGetOpenRequest`
  关于“唯一子项/多条目不支持”的注释改为归一化后的真实语义。
- `OverlayOwnerState` 的“骨架/由后续任务消费”过期措辞改为已实现的状态机说明。
- Core 计数 758（−1 默认值用例 +1 ForEach 回归，不为凑数新增）。

| 命令 | 结果 | 证据（目录 `2026-10-29-popup-select-p03-post-anchor-evidence/`） |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0 | `p03-post-anchor-build.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **758**, Failed 0，exit 0 | `p03-post-anchor-CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `p03-post-anchor-CSharp.UI.Integration.Tests.*` |
| 同上（含 throwaway ForEach 锚真实 Skia smoke） | Total **57**, Failed 0，exit 0 | `p03-post-anchor-smoke.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `p03-post-anchor-CSharp.UI.Markdown.Tests.*` |

- throwaway smoke（真实 Skia + headless runtime，`pixelScale=1.5`）：`ForEach<int>` 两行真实文本按钮
  不重叠、popup 贴整棵锚子树底、popup 内容真实排版、第二行 Down/Up 触发 `row 2` 回调；运行后已删除
  （`git status` 无残留），仅保留日志作为 evidence。
- 上一轮证据目录（`2026-10-29-popup-select-p03-evidence/`）与 fast 的独立 probe 日志均未改写。


## 未实现（按计划留给后续任务，不声称通过）

- 外点吞并/锚豁免/Esc/失活接管/capture 与 Up-activation（P-04）。本任务只暴露
  `CloseOnOutsidePress`/`CloseOnEscape` 与 `Close(reason)` 入口，**不模拟输入**。
- 焦点恢复（P-04）：用**发起关闭时活动 entry** 的 `SavedFocus`/`AnchorNode` 快照，不引入
  `LastClosedEntry` 之类会被后来挂载污染的状态。
- `Select`（P-05）将实现同一个 `IOverlayOwner`（内部 flag 作为 request、`OnOverlayClosed` 清 flag），
  无需新接口。

## Freeze

P-03 源码/测试/文档已冻结（含 post-anchor 返修批次）：**停止 build/edit**。等待 fast 独立 probe 复跑
（公开 `Popup`/`PopupPlacement`：Binding 开合、内部关闭写回一次、恒 true 抑制、真边沿重开、content 更新
保 state、两个初始 true 后者胜、嵌套拒绝诊断一次；**M14 ForEach 锚应转为两行不重叠 + 按整棵锚子树定位**），
主审 read 后 gate P-04。不通过则在 `docs/return/` 继续新增返修记录。
