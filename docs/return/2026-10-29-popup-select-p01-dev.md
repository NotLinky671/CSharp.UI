# P-01 开发记录（2026-10-29）

任务：[P-01 UiRootLayerView + OverlayManager + PopupSurface](../task/01-overlay-manager.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md)
基线：`249ca96 docs: plan popup and select controls`（工作区起始干净，`git status --porcelain` 为空）
状态：**P-01 实施完成，待主审 + fast 独立复审；未提交、未推送、未勾选验收。**

## 改动文件

新增（internal，无公开 API）：

- `src/CSharp.UI/Runtime/UiRootLayerView.cs`——固定根层描述 `[userRoot 归一化, OverlayHost]`，ZStack 语义 Measure/Arrange（先用户树、后 overlay），`IsHitTestVisible=true`、`HitTestSelf=false`。
- `src/CSharp.UI/Runtime/OverlayHostView.cs`——overlay 宿主；`Children` 恒空（由 manager 安全阶段显式 reconcile），`IsHitTestVisible = 有 entry || 吞并手势期`，`HitTestSelf=false`。
- `src/CSharp.UI/Runtime/OverlayManager.cs`——单活动 entry、open/close pending 安全阶段消费、资格门（mounted/enabled/anchorVisible/popupBounds/窗口激活）、锚可见矩形、popupBounds=视口内缩 margin、内联 bottom+clamp 定位、owner awaiting-edge 骨架、嵌套 Show 拒绝 + runtime 诊断。
- `src/CSharp.UI/Runtime/PopupSurfaceView.cs`——popup 内容实际父；`ResolveEnvironment` 返回锚**当前** Environment；`GetChildClip` 与 `Emit` 按最终 Frame 裁剪。

修改：

- `src/CSharp.UI/Runtime/UiRuntime.cs`——构造时经 `UiRootLayerView` 单次 `MountRoot`；`Root` 返回 userNode（不再置 `Parent=null`）；新增 internal `TraversalRoot`/`Overlay`；布局/绘制/命中/焦点/卸载遍历改 `_layer`；`SetEnvironment`/`SetViewport`/`Start`/异常恢复失效根层；`ProcessPendingWorkCore` 在响应式消费后增加 overlay 安全阶段消费（改变子树 → `progressed` → 再布局到稳定）；窗口失活通知关闭；`OnNodeUnmounting` 钩子；异常路径丢弃未消费 overlay 请求。
- `src/CSharp.UI/Runtime/Reconciler.cs`——新增 `NormalizeRootView`（ForEach/多 descriptor 仍包 `ImplicitRootContainer`，`R08_RootForEach_LaysOutAllChildren` 语义保持）。
- `src/CSharp.UI/Input/InputRouter.cs`——HitTest/Focus 遍历起点改 `TraversalRoot`。
- `src/CSharp.UI/Runtime/ViewNode.cs`——仅 XML 文档：`Parent`/`Depth` 说明根层。
- `docs/task/README.md`、`docs/task/01-overlay-manager.md`——授权状态：用户 2026-10-29 已授权，P-01 进行中（不勾验收）。

测试：

- `tests/CSharp.UI.Tests/OverlayRootTests.cs`（新，14 个用例，覆盖 A1–A12）。
- `tests/CSharp.UI.Integration.Tests/OverlayLayerSmokeTests.cs`（新，真实 Skia 文本 + headless 运行时最小 smoke：挂载/排版/裁剪/关闭/卸载）。

## 与计划的机械性/最小调整（如实记录）

1. **`Show` 内部签名扩展**：`Show(ViewNode anchor, View content, double maxHeight = double.PositiveInfinity, ViewNode? owner = null, double margin = 4)`。
   `owner`/`margin` 是 P-03 `Popup`（`Margin`/`MaxHeight` 在公开视图上）与 P-05 `Select` 所需的 internal 入口；不改变公开 API 范围，也没有公共 Popup/Select 类型落地。
2. **`Show`/`Close` 一律登记 pending，安全阶段消费**（不提供同步挂载捷径）：A11 断言的“布局内调用不当轮生效、`ProcessPendingWork` 返回后稳定”因此是唯一语义。
3. **overlay 宿主每轮布局强制重测**（`UiRootLayerView.Measure` 对宿主用 `MeasureFresh`）：宿主不是用户树失效的祖先，缓存复用会跳过 popup 测量阶段、锚环境变化无法传播（实测 A4 失败后修正）。代价是每轮多一次宿主测量（子树内容仍走自身缓存）。
4. **P-01 内联定位基线**：`ArrangeEntry` 为贴锚底部 + 视口 clamp（计划 §3 的 P-02 算法替换点，不是 static delegate/假实现）。P-02 直接替换该实现。
5. **P-02 命名冲突提醒（主审 8D9E）**：计划伪码 `internal static class PopupPlacement` 与 P-03 `public enum PopupPlacement` 同命名空间冲突；P-02 实施用 internal `PopupPlacementCalculator`（或 Resolver），公开枚举名保留。B5 泛写 `desiredH>BoundsH→h=BoundsH` 与 sideAvail 限制矛盾，按计划算法 `min(desired,maxHeight,sideAvail,boundsH)` 优先。
6. **owner open-request 状态骨架**：manager 提供 `IsOwnerAwaitingOpenEdge(owner)` 与位移关闭时置位；完整“false→true 边沿”状态机属 P-03，本任务不实现 Popup binding。
7. 未触碰 `FrameOverlay`（IME 候选）与 `HitTestOverlay`。

## 可观察变化（如实记录，兼容风险）

- `UiRuntime.Root.Parent` 不再为 null（现为内部根层）、`Root.Depth == 1`；公开文档已更新。任务内核对：现有测试无 `Parent == null`/`Depth == 0` 断言；`Require<T>`/`FindDescendants<T>` 全部通过（728 Core 用例）。
- `ViewNode.DebugPath` 现以 `RootLayer / ...` 开头（诊断文本变化）；无测试 pin 该前缀。
- 未命中空白区的行为不变：根层/宿主自身显式 `HitTestSelf=false`；无 entry 时宿主整支被跳过（A9）。

## 验收映射（headless，`OverlayRootTests`）

| 验收 | 测试 | 观察结果 |
|---|---|---|
| A1 | `A1_RootIsUserNode_WithInternalLayerParent`、`A1_ForEachRoot_KeepsAllItemsUnderUserNode` | 通过 |
| A2 | `A2_ShowMountsPopupAboveMainTree_AndParticipatesInInputAndFocus` | 通过（命中在 popup 子树、FocusOrder 在主树后、push clip 序在主导航纹理后） |
| A3 | `A3_AnchorScrolledOutCloses_AndPopupEscapesAnchorScrollClip` | 通过（popup 越过 ScrollView clip；部分可见跟随；滚出后卸载） |
| A4 | `A4_PopupUsesAnchorCurrentEnvironment_NotSnapshot` | 通过（Theme Light→Dark 下轮 popup 随动，非快照） |
| A5 | `A5_DisabledAnchorRejectsShow_AndClosesOpenPopup` | 通过 |
| A6 | `A6_AnchorUnmountClosesPopup_AndReleasesFocus` | 通过 |
| A7 | `A7_WindowDeactivationClosesPopup` | 通过 |
| A8 | `A8_SecondShowDisplacesFirst_AndFirstOwnerWaitsForEdge` | 通过（旧 entry 卸载、单槽、旧 owner awaiting edge） |
| A9 | `A9_NoEntry_HostIsSkippedByHitTestAndFocusTraversal` | 通过 |
| A10 | `A10_PopupContentIsClippedToFinalFrame` | 通过（PushClip=最终 Frame、内容溢出、溢出区不命中） |
| A11 | `A11_ShowInsideLayout_IsDeferredAndConverges`、`A11_CloseInsideLayout_IsDeferredAndConverges` | 通过 |
| A12 | `A12_NestedShowIsRejected_OuterPopupStaysOpen` | 通过（返回 false、外层保持、单 entry、诊断含“嵌套”） |

## 命令与结果（原始日志在 evidence 目录）

工作目录：`C:/Users/nothh/code/C#/CSharp.UI`

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx -c Debug` | 0 警告 0 错误，exit 0 → `build-debug.log` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total 728, Failed 0，exit 0 → `core-tests.log` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total 57, Failed 0，exit 0 → `integration-tests.log`（含新真实 Skia smoke） |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total 50, Failed 0，exit 0 → `markdown-tests.log` |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests -c Debug` | Total 684, Failed 1，exit 1 → `opentk-tests.log` |

**OpenTK 唯一失败为基线既有问题，非本轮引入**：`GalleryIdleAndBoundsTests.极窄视口有确定几何与溢出诊断`
（Markdown 代码块 Copy 按钮在 80px 视口右边界 83.88 越界）。已用 `git worktree add C:/tmp/csui-baseline 249ca96`
在基线提交上运行同一套件复现（Total 684, Failed 1，同一用例），worktree 已移除。本轮未修改该行为。

## Freeze 声明

P-01 代码、测试与文档已冻结：**停止 build/edit**，等待主审 read/review 与 fast 的独立 probe（probe 只需适配真实
internal `Show(anchor, content, maxHeight, owner, margin)` 签名）。主审通过后再进入 P-02；未通过则在 `docs/return/`
新增返修记录。
