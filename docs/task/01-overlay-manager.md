# P-01 UiRootLayerView + OverlayManager + PopupSurface：挂载、资格门、锚环境现值、关闭规则

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §1/§2/§4/§5/§9。
前置：无。状态：待授权实施。

## 目标

`UiRuntime` 内部固定包根层 `UiRootLayerView`（[userRoot, OverlayHost] 两子）；
`OverlayManager` 管理单活动 entry 的 popup 子树挂载/资格门/锚环境现值/关闭
规则；popup content 经 `PopupSurfaceView` 拿锚局部环境+按最终 Frame 裁剪。

## 改动面

- `src/CSharp.UI/Runtime/UiRootLayerView.cs`（新，internal）：
  - `Children = [userRoot 描述, overlayHost 描述]` 固定两子；
  - **挂载顺序（不 double-mount）**：以 `UiRootLayerView` 调
    `Reconciler.MountRoot` → `_layer` 节点 + 两子节点；
    `UiRuntime.Root`（public）返回其中 **userNode**（`Require<ScrollView>`
    等断言不变）；runtime 内部 traversal（`RunLayout`/`BuildFrame`/HitTest/
    `BuildOrder`/Unmount walk/`FocusManager.Collect`）起点改 `_layer`；
    `SetRoot`/用户根替换走 `_layer` 的 child reconcile（新根描述在
    `userNode` 槽 reconcile/unmount——不再 MountRoot）。
  - `GetChildClip → null`；Measure/Arrange/Emit 按 ZStack 语义（两子同 frame
    叠放，overlay 在上）；`Environment = Runtime.Environment`；
  - `IsHitTestVisible = true`（恒）且**显式 `HitTestSelf(...) => false`**——
    `InputRouter` 沿 `IsHitTestVisible` 遍历：父 false 剪整棵子树，故
    RootLayer 恒 true（OverlayHost 按下文 `entry!=null||swallow` 条件化）；
    自身命中靠显式 `HitTestSelf=false`（默认 `Frame.Contains` 会命中——
    不能省）；空白区行为与原单 Root 一致。
- `src/CSharp.UI/Runtime/OverlayManager.cs`（新，internal）：
  - `ActiveEntry` 单槽（internal record：`AnchorNode: ViewNode`（锚的**实际
    view 子树根节点**——`Popup` 的 Anchor 子节点、Select 的 trigger 节点；
    placement 矩形与锚豁免共用该 Frame）；`PopupNode: ViewNode`；
    `SavedFocus: ViewNode?`；`MaxHeight: double`）——不建 `IReadOnlyList`。
  - `Show(ViewNode anchor, View content, double maxHeight = +∞)`：
    **资格门**（`anchor.IsMounted && !anchor.IsEffectivelyDisabled &&
    anchorVisible 非空 && popupBounds 非空 && 窗口激活`）通过 → `CloseAll()` →
    挂载 `PopupSurface(content)` 为 OverlayHost 子节点（经 reconcile）；
    不通过 → 拒绝、不产 entry、不写任何状态。`Show` 不改焦；
    记录 `SavedFocus=FocusedNode`。
  - **嵌套 `Show` 请求**（entry 存在期间 popup content 内再 `Show`）→
    **拒绝、输出 runtime 诊断，外层保持开且不改外层/选择值**——
    nested `Show` 返回 false（不开、不抛）；非本轮目标边界。
  - `Close()`/`CloseAll()`（单槽同义）：正常 unmount popup 子树——
    `FocusManager.OnNodeUnmounting`/capture 清理走既有路径。
  - **关闭规则**（每轮布局后校验；pending 关闭走下条安全阶段）：
    1. 锚 `!IsMounted` → 关；2. `IsOpen` 变 false（P-03 侧管 binding）；
    3. **`anchorVisible` = `anchor.Frame ∩ viewport ∩ 各祖先 GetChildClip`**
       ——只用于锚可见资格判定：`anchorVisible.IsEmpty` → 关（部分非空→保持；
       `popupBounds` 是 `viewport.Inset(Margin)` 独立矩形，不与锚祖先 clip 交）；
    4. 锚 `IsEffectivelyDisabled` → 关；
    5. 窗口失活钩子（`NotifyWindowActivation(false)`/`HandleFocusChanged(false)`
       runtime 通知点）→ `CloseAll()`；
    6. 另一 `Show` → 旧 entry 关。
  - **owner open-request 状态**（防自激，P-03 细节）：manager 对每 owner
    记录"已观察 open-request"——内部关闭后记"待 false→true 边沿"，
    binding 恒 true 不再 `Show`；owner unmount/替换清状态；binding 实例换
    不重置。本任务只建骨架，完整纪律 P-03。
  - **有界安全阶段（fast #4）**：Measure/Arrange/Emit 内**不得** reconcile/
    unmount/写 binding（`ProcessPendingWork` 不可重入）——open/close 请求登记
    pending，在**本轮布局之后**的安全阶段消费：消费 → InvalidateLayout →
    下一轮 `RunLayout` 应用，**`ProcessPendingWork` 返回前收敛稳定**
    （有界：关闭请求本身不再产新请求——一轮消费完即停；不开 async/thread/
    无界 drain）；binding 写回调重入由 manager request-generation + 既有
    guard 覆盖（复用纪律不直接引私有 focus 字段）。
  - `OverlayHost`（internal `View` 子类）：`Children` = entry 的 popup 描述
    （至多 1，由 manager 提供）；`GetChildClip → null`；
    **`IsHitTestVisible = (entry != null || swallowGestureActive)`** 且
    **`HitTestSelf(...) => false`（恒，显式覆写）**——无 entry/非吞并期
    host 不可见、整支被跳、下方原样接收 wheel/hover；有 entry → 遍历子但
    自身无命中，popup 子节点（PopupSurface→content）正常可点；
    P-04 dismiss-target 是唯一有 self-hit 的临时节点。
  - Arrange：读锚 `Frame` 现值 → **`popupBounds` = `viewport.Inset(Margin)`**
    （不与锚祖先 clip 交）→ placement 计算 → `context.Arrange` popup 于最终
    Frame（placement 算法细节 P-02，本任务先内联 bottom+clamp 实现）。
- `src/CSharp.UI/Runtime/PopupSurfaceView.cs`（新，internal）：
  - popup content 的**实际父**（挂 OverlayHost 下）——
    `ResolveEnvironment` 返回**锚当前 `Environment`**（每轮布局读
    `anchor.Environment` 现值——锚局部 Theme/Disabled/Font 作用域生效；
    锚环境变化下轮生效；无 snapshot）。
  - `GetChildClip`/`Emit`：`PushClip(finalFrame)`——popup 内容裁剪到最终
    Frame（`Popup`/`Select` 超限时裁剪、不外溢到 anchor/viewport 外）。
  - `Children = [entry.Content 描述]`；content 内 `UseState`/focusable 正常。
- **可观察变化（如实记录，不写"不可见"）**：
  `userNode.Parent` 现为 `_layer`（非 null）、`Depth` +1——**兼容风险**：
  任务内 grep `Parent == null`/`Depth`/`IsSelfOrDescendant` 依赖并更新受影响
  测试/断言；guide 在 P-07 写"Root.Parent 现为 internal 层节点"。
  `_layer` 非 focusable、无命中、不入 `BuildOrder` 语义。
- **`Show` 挤占**：另一**非嵌套** owner 的 `Show` → 旧 entry 关、新 entry 活；
  **嵌套 `Show`**（popup content 内再请求开 popup/Select——anchor 位于当前
  `entry.PopupNode` 子树内）→ **拒绝、输出 runtime 诊断，外层保持开**
  （`Show` 返回 false、不开、不抛）；非本轮目标边界。

## 不做

外点/Esc/capture/焦点恢复（P-04）；`Popup`/`Select` 公开面（P-03/P-05）；
placement 算法抽出（P-02）；`HitTestOverlay`/`FrameOverlay` 不动；
不做 async 后台线程/无界 drain。

## 验收（headless，tests/CSharp.UI.Tests 新文件按现场约定）

- A1：`UiRuntime.Root` 返回 userNode（`Require<ScrollView>` 等现有断言无回归）；
  `Root.Parent != null && Root.Depth == 1`（如实断言层结构——任务内核对
  依赖 `Parent==null`/`Depth==0` 的既有测试并更新）。
- A2：internal `Show` → popup `IsMounted`、主内容之上命中（HitTest——
  popup content 可点：RootLayer/Host 恒 `IsHitTestVisible` 但 `HitTestSelf`
  返回 false、自身无命中，命中落 popup 子节点）、`BuildOrder` 含其
  focusable 子节点、绘制序在主内容后（DrawCommand 序）。
- A3：锚在 `ScrollView` 内滚出（`anchorVisible` 完全空）→ 关；
  部分可见 → 保持且跟随；popup 不受 ScrollView clip 限（`popupBounds`
  独立——popup 可画出 anchor 所在 ScrollView clip 之外）。
- A4：锚在 `ThemeView`/`FontView` 内 → popup 子节点 `Environment` = 锚局部
  环境；`SetEnvironment`/Theme 值变 → 下轮 popup 环境随动（非 snapshot）。
- A5：锚 `IsEffectivelyDisabled` → 关；`Show` 时锚已 disabled → 拒绝打开。
- A6：锚/`Popup` 节点卸载 → popup 子树 unmount、focus/capture 走既有清理。
- A7：窗口失活钩子 → `CloseAll()`（runtime 内部通知点直接调用断言）。
- A8：`Show` 第二个（主树声明序后者）→ 旧 entry 关、新 entry 活；
  旧 owner 记"待 false→true 边沿"——其 binding 恒 true 也**不再重开**
  （无每帧竞争；`ProcessPendingWork` 返回后稳定单 entry）。
- A9：popup 关 → OverlayHost `IsHitTestVisible=false` 整支被跳
  （HitTest 在用户树命中行为不变、不拦 wheel/hover）；`BuildOrder` 零额外
  遍历；无轮询。
- A10：popup content `PushClip(finalFrame)`——超限内容不画到最终 Frame 外
  （绘制断言或 pixel/DrawCommand 区域断言，不 pin 字段名）。
- A11：布局内调用 `Show`/`Close` → 不当轮生效；`ProcessPendingWork` 返回后
  稳定（popup 挂载/卸载完成、无悬挂 pending）——断言"返回后状态"非
  "Arrange 内即时"。
- A12：嵌套 `Show`（popup content 内再请求开 popup/Select）→ **拒绝**
  （诊断/异常断言）、外层 popup 保持开、无第二 entry。
- 断言走 `HitTest`/`FocusedNode`/`IsMounted`/`DrawCommand` 序/`Environment`/
  reconcile 计数等可观察面，不 pin `OverlayManager`/`UiRootLayerView`/
  `PopupSurfaceView` 字段名。
