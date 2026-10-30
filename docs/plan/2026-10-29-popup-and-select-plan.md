# 2026-10-29 Popup 覆盖层与 Select 控件实施计划

基线：`60ec40c`（含 `c704c20` scrollbar first-drag 修复）。本文件只做设计。**状态：全部 P-01~P-07 主审验收通过、代码与文档收口完成（交付记录见 [2026-10-30-popup-select-delivery](../delivery/2026-10-30-popup-select-delivery.md)；提交信息见 git 历史）。**

## 问题

当前控件族只有 Button/Toggle/Slider/TextField/TextEditor，缺单选下拉（Select）。
Select 依赖此前不存在的通用能力：**真实 view 子树能画出父容器/ScrollView clip 之外**。

现有内部机制不足以承担：

- `UiRuntime.FrameOverlay`（`UiRuntime.cs:148-151`）：帧末追加绘制命令的 IME 候选入口，
  无 ViewNode 生命周期、不参与 Measure/Arrange/HitTest/focus——不能承载真实 view 子树。
- `View.HitTestOverlay`（`View.cs:136`）：owner 自绘命中区（Floating scrollbar 专用），
  只判定归属，不产生子节点。
- `ZStack`：同层叠放但在同一布局坐标系内，受祖先 clip 约束。

## 目标

1. **单窗口根级 overlay 层**：popup 是真实 `ViewNode` 子树（完整 Measure/Arrange/Emit/
   HitTest/`BuildOrder` 焦点遍历/mount-unmount 生命周期），绘制在主内容之上、
   不受任何中间祖先 clip 约束。
2. **公开 `Popup` API**：锚定定位、placement 可配置、`Binding<bool>` 双向开合、
   content 是普通 `View`（非 Select 私有）。
3. **`Select<T>` 控件**：绑定当前值 + 选项 + placeholder + disabled + 项禁用；
   限高内部滚动、外点/Esc/选后关闭、焦点规则明确、完整键盘、主题/DPI。

## 非目标

- 可编辑 ComboBox（键入过滤）、多选、远程搜索、虚拟化、动画/过渡、item 自定义 View 模板。
- modal/focus trap/dialog/context menu/Tooltip/多层 popup 堆栈/focus-visible 样式。
- 公共滚动 API、多窗口/原生 popup、不改现有 public API 签名。

## 外部参考（只借鉴设计，不复制代码；调研日期 2026-10-29，活跃度经 GitHub API 核实）

- **Avalonia `Popup`/`ComboBox`**（https://github.com/AvaloniaUI/Avalonia，
  MIT — https://raw.githubusercontent.com/AvaloniaUI/Avalonia/main/licence.md；
  `pushed_at` 2026-10-28，最新 release `12.1.4` 2026-10-09，
  https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4）：
  popup 走真实布局/输入管线、host 于视觉树顶层、light-dismiss。
  与本设计对齐的可核实行为：`OverlayDismissEventPassThrough=false`（外点吞并）、
  `PointerPressed` 触发 dismiss、`WindowDeactivated` 关闭、锚 `LayoutUpdated`
  重定位/`Detached` 关闭、ComboBox 的 Tab 关闭且不 Handled、disabled 项跳过、
  `MaxDropDownHeight` 上限。不适用：跨视觉树任意锚定、多 popup 嵌套、
  `IPopupHost` 可换原生窗口——本框架单窗口、单 popup、不引 host 抽象。
- **Floating UI**（https://github.com/floating-ui/floating-ui，MIT —
  https://raw.githubusercontent.com/floating-ui/floating-ui/master/LICENSE；
  `pushed_at` 2026-10-27；文档 https://floating-ui.com/docs/flip | /shift | /size）：
  `bottom → flip → shift` 顺序与 `size` middleware 的"可用宽/高 clamp→
  按限后尺寸重 measure"——本计划 §3 的"先限宽→measure→选 side→限高→clamp"
  与其同构；`size` 的 `availableWidth/Height`、`minWidth=reference` 提示
  trigger 宽下限（Select popup 宽≥trigger 宽）。不适用：collision middleware
  泛化链/autoUpdate/arrow——本计划为确定的两向+clamp。
- **NOTICE 决策**：纯设计借鉴、不引第三方代码/依赖——**不新增/不更新 NOTICE**；
  实现中若实际引入任何外部代码须先报主审。

## 设计

### 1. Root 层结构（确定方案，含可观察变化如实记录）

**`UiRuntime` 内部固定包一层 `UiRootLayerView`**，构造顺序（不 double-mount）：

- 以 internal `UiRootLayerView`（描述含 `[userRoot 描述, overlayHost 描述]` 两子）
  调 `Reconciler.MountRoot`——得到 `_layer` 节点 + 两个子节点
  （`userNode`、`overlayHostNode`）；
- `UiRuntime.Root`（public）返回 **`userNode`**（不是 `_layer`）——
  `Root.Require<ScrollView>` 等现有断言/语义不变；
- runtime 内部 traversal 起点（`RunLayout`/`BuildFrame`/HitTest/`BuildOrder`/
  Unmount walk/`FocusManager.Collect`）用 `_layer`；
- 内部用户根替换辅助路径（如有——`UiRuntime` 内部换根入口）经 `_layer` 的
  child reconcile（`userNode` 子树按 key/类型正常更新/unmount）——不把新根
  再 MountRoot 一次（会丢 state/绕 lifecycle）；**不新增 public `SetRoot`**。

**可观察变化（如实记录，非"consumer 不可见"）**：

- `userNode.Parent` 现为 `_layer` 而非 `null`（`ViewNode.Parent` 是 public getter），
  `userNode.Depth` 相应 +1——**兼容风险**：`Depth`/Parent 链依赖的测试或用户代码
  行为变化；`IsSelfOrDescendant` 等祖先判定对 Root 多一层祖先（`_layer` 非 focusable、
  无 hit、不可观察于 `BuildOrder`——`Popup` 语义不受其影响）。任务内核对
  依赖 `Parent==null`/`Depth==0` 的测试/断言并更新；docs/guide 在 P-07 记录
  "Root.Parent 现为 internal 层节点"。
- `UiRootLayerView`：`Children = [userDesc, overlayDesc]` 固定两子（`OverlayHost`
  的 `Children` 由 `OverlayManager` 提供）；`GetChildClip → null`；
  Measure/Arrange/Emit 按 ZStack 语义（两子同 frame 叠放，后者绘制/命中在上）；
  自身 `Environment = Runtime.Environment`；
  **`IsHitTestVisible = true`（恒）且显式 `HitTestSelf(...) => false`**——
  `InputRouter` 沿 `IsHitTestVisible` 遍历：父为 false 会剪掉整棵子树，故
  RootLayer 恒 true（Host 按下文 `entry!=null||swallow` 条件化）；自身命中
  靠显式 `HitTestSelf=false` 拒绝（不能省略——默认实现 `Frame.Contains`
  会命中）；空白区无命中、原行为保留。
- `OverlayManager`（internal，`Runtime/`）：**单活动 entry**
  `ActiveEntry { AnchorNode: ViewNode; PopupNode: ViewNode; SavedFocus: ViewNode? }`
  ——`AnchorNode` 是锚的**实际 view 子树根节点**（`Popup` 的 Anchor 子节点、
  `Select` 的 trigger 节点——锚子树整体豁免与定位矩形都用该节点 Frame）。
- `OverlayHost`（internal `View` 子类）：`Children` = entry 的 popup 描述列表
  （至多 1）；`GetChildClip → null`（popup 不被 clip——裁剪由 popup 自身
  `PopupSurface` 的 `GetChildClip`/Emit clip 到其最终 Frame 负责，见 §3）；
  **`IsHitTestVisible = (entry != null || swallowGestureActive)`** 且
  **`HitTestSelf(...) => false`**（恒，显式覆写——默认 Frame.Contains 不能留）：
  无 entry/非吞并期 → host 不可见、整支被跳、下方用户树原样接收 wheel/hover；
  有 entry → host 遍历但自身仍无命中——popup 子节点（`PopupSurface`→content）
  正常可点；吞并期 dismiss-target 是唯一有 self-hit 的临时节点（全屏、
  `Handled` owner 标、Up/Cancel 后释放）。

### 2. `PopupSurface`（internal，portal content 实际父）

`LayoutContext` 的环境规则 `node.Parent?.Environment ?? Runtime.Environment` 是
固定读 `Parent`——overlay 子树挂 `_layer`/`OverlayHost` 下默认拿根环境。
**确定方案**：popup content 的实际父是 internal `PopupSurfaceView`（每个 entry
一个，挂在 OverlayHost 下）：

- `PopupSurfaceView.ResolveEnvironment(node, parentEnv)` 返回**锚节点当前
  `Environment`**（每次布局读 `anchor.Environment` 现值）——锚局部
  Theme/Disabled/Font 作用域生效；锚环境变化下一轮布局自然生效；锚变
  `IsEffectivelyDisabled` → §4 资格门关闭。
- `PopupSurfaceView.GetChildClip/Emit`：`PushClip(finalFrame)`——popup 内容
  裁剪到其最终 Frame（超限内容不穿 anchor/不出 viewport——Select 内套
  `ScrollView` 提供滚动；通用 `Popup` 超限时裁剪）。
- `PopupSurfaceView` 的 `Children` = `[entry.Content 描述]`；popup content
  内 `UseState`/focusable 等全部正常（真实 ViewNode 生命周期）。

### 3. Placement 与 Measure 时序（确定版，两阶段有界）

**两个独立矩形（确定，不混用）**：

- **`popupBounds`** = `viewport.Inset(Margin)`——popup 的**可用边界**：
  placement 的宽/高上限、clamp 范围。锚祖先 clip **不参与**——否则 popup
  逃不出 ScrollView clip（违背 overlay 核心目标）；
  `popupBounds` 宽或高 ≤ 0 → `null` → caller 走关闭路径
  （绝不产 NaN/负/零尺寸 Frame）。
- **`anchorVisible`** = `anchor.Frame.Intersect(viewport)`，再与锚 Parent 链
  每个祖先 `GetChildClip` 矩形取交——**只用于锚可见资格判定**：
  `anchorVisible.IsEmpty`（锚完全滚出）→ 关闭；部分相交 → 保持。

**时序（确定）**：

1. 主树 Measure+Arrange 先完成（锚 Frame/Environment 已定）；
2. overlay 层 Measure：`OverlayHost` 给 `PopupSurface` 约束 =
   `(0..popupBounds.W, 0..popupBounds.H)` → `DesiredSize` 按宽约束得真实高；
3. placement 纯计算（**不调 Measure**，输入 `popupBounds`）：
   - `w = clamp(desiredW, 0, popupBounds.W)`；
   - `spaceBelow = max(0, popupBounds.Bottom - anchor.Bottom)`、
     `spaceAbove = max(0, anchor.Y - popupBounds.Top)`；
   - `side = Auto ? (desiredH ≤ spaceBelow || spaceBelow ≥ spaceAbove ? Below : Above)
     : preference`（`Above`/`Below` **强制**方向——不足按该侧限高、不翻转；
     `Auto` 才两向 flip）；
   - `sideAvail = side==Below ? spaceBelow : spaceAbove`；
   - `h = min(desiredH, entry.MaxHeight, sideAvail, popupBounds.Height)`；
   - `h ≤ 0 || w ≤ 0` → null（关闭）；
   - `top = side==Below ? anchor.Bottom : anchor.Y - h`，
     clamp `[popupBounds.Top, popupBounds.Bottom-h]`；
   - `left = anchor.X`，clamp `[popupBounds.Left, popupBounds.Right-w]`；
4. **第二阶段 measure（有界一次）**：最终 `(w,h)` 与阶段 2 的 `DesiredSize` 不同
   → 用约束上限 `(w,h)` 对该 popup 再 Measure 一次（至多一次；仍超限属内容
   自裁——Arrange 按 `(w,h)`、内容经 `PopupSurface` clip）；
   然后 `context.Arrange` popup 于 `Rect(left, top, w, h)`。
   Select 场景：限高后 `ScrollView` 在第二次 measure 拿到最终有限高 →
   thumb/可视区正确。
- **定位参考**：锚 `Frame`（矩形，非可见交）——**确定规则**；锚部分 clip 时
  popup 贴 Frame 边（可能与 clip 边重叠显示，随滚动下一轮修正）。
- **锚滚出判定**：`anchorVisible.IsEmpty` → 关（`anchorVisible` 部分非空→保持；
  判定只读 `anchorVisible`，与 `popupBounds` 独立）。
- **Select `MaxPopupHeight`** = 传给 `Show` 的 `entry.MaxHeight`；
  `Popup.MaxHeight` 默认 `+∞`（构造校验：正或+∞；`Margin` 有限 ≥0）。
- **trigger 宽 > viewport**：`w = clamp(..., popupBounds.W)`——popup 宽可 < 锚宽，
  视口安全优先。
- **DPI**：全部逻辑像素；`PixelScale` 只影响光栅化。

### 4. `Popup` 公开 API 与开合纪律

```csharp
public sealed class Popup : View
{
    public Popup(View anchor, View content, Binding<bool> isOpen,
                 PopupPlacement preference = PopupPlacement.Auto);
    public View Anchor { get; }
    public View Content { get; }
    public Binding<bool> IsOpen { get; }
    public PopupPlacement Preference { get; init; } = PopupPlacement.Auto;
    public double Margin { get; init; } = 4;                        // 有限、非负
    public double MaxHeight { get; init; } = double.PositiveInfinity; // 正或+∞
    public bool CloseOnOutsidePress { get; init; } = true;
    public bool CloseOnEscape { get; init; } = true;
}
public enum PopupPlacement { Auto, Above, Below }
```

- `Popup` 是主树正常节点：**布局只透传 Anchor**（Measure 取 anchor Desired、
  Arrange 给 anchor 同 Frame、`GetLayoutFeatures` 透传 anchor features）——
  popup content 不影响主布局；`Popup.Children = [Anchor]`，`Content` 不经
  `Children` 挂主树。
- **`Popup.Emit` 只 emit Anchor**（popup content 由 overlay 层自己画，
  不从 `Popup` 的 Emit 路径重复）。

#### IsOpen 双向纪律（确定）

- **资格门**（每轮 `ProcessPendingWork` 校验）：`锚 IsMounted &&
  !IsEffectivelyDisabled && anchorVisible 非空 && popupBounds 非空 &&
  窗口激活`——**门不满足绝不挂载**，无论 binding 值。
- **open-request 状态机（防自激，确定）**——manager 对每个 `Popup` owner
  记录其**已观察到的 open-request 状态**（owner 自己的 request generation，
  非全局 FocusManager 字段）：
  - owner 从未开且 `IsOpen==true` → 资格门通过 → `Show`（记录"已打开"）；
  - owner 开着 → 任何**内部关闭**（外点/Esc/锚失效/disabled/失活/挤占）→
    写 `IsOpen=false` 一次 + 把该 owner 记为"**已内部关闭（待 false→true
    边沿）**"——之后每轮即使 binding 仍 true **也不再 `Show`**；
    binding setter 是 controlled 语义：manager 必须真的观察到 `false` 落进
    owner 状态（外部 State 真变 false→true 新边沿）才允许重开——
    getter 恒 true → 保持关、不反复写 false、**无自激**；
  - owner 未实际打开过、仅资格门不满足（恒 true + 资格缺失）→ 保持关、
    不写、资格恢复后**首次**评估即开（只一次，无循环）；
  - owner `OnUnmount`/节点替换 → 该 owner 状态清（新 owner 独立 generation）；
  - `IsOpen` binding **实例**换（`State.ToBinding()` 每 Build 新实例）→
    按新 binding 现值收敛，**不重置 owner 的已观察 open-request 状态**
    （换实例不算新边沿）。
- **`IsOpen` 变 false（owner 主动写）** → 关（若开着），并把 owner 记为
  "owner-driven close"——下次 `false→true` 边沿才可重开。
- **关闭顺序**（写回仅一次/边沿；写 false 走 owner 状态机不重复写）：
  1. `Popup`/锚节点卸载 → 关（owner 状态清）；
  2. `IsOpen` 变 false → 关；
  3. 外点（§5）→ 写 `IsOpen=false` → 关（记"待边沿"）；
  4. Esc（§5）→ 写 `IsOpen=false` → 关 + 焦点恢复（§5）；
  5. `anchorVisible.IsEmpty` → 写 `IsOpen=false` → 关；
  6. 锚 `IsEffectivelyDisabled` → 写 `IsOpen=false` → 关；
  7. 窗口失活 → 写 `IsOpen=false` → 关；
  8. **另一 `Show` 挤占** → 旧 entry 关（owner 记"待边沿"——其 binding 恒
     true 也不再开，**无每帧竞争**；两个 `Popup` 初始同时 true：按主树声明
     顺序后者 `Show` 胜，前者被挤占记"待边沿"不再扫描重开）。
- **嵌套 `Show` 请求**（popup content 内再 `Popup`/`Select` 请求开第二 popup）→
  **拒绝、输出 runtime 诊断，外层保持开且不改外层/选择值**——nested
  `Show` 返回 false（不开、不抛）；非本轮目标边界，不再设计交互；
  写 guide "popup 内嵌 popup 不支持"边界。
- **binding 追踪**：`Popup` 对 `IsOpen` 的读取必须走 `Binding.Read(node)`
  （或 component `Build` 内读 `State`）登记依赖——`.Value` getter 直接读
  不登记、`State` 变化不会驱动重建。
- **描述更新**：`Popup` 重建（同 key/类型）→ 节点+entry 保持；
  `Content` 描述变化且开着 → popup 子树 reconcile（不 unmount/remount）；
  `Popup` `OnUnmount` 且开着 → 内部关闭路径写 `IsOpen=false`
  （owner 状态清，setter 重入由 manager request-generation/既有
  guard 覆盖——复用纪律不直接引私有 focus 字段）。

### 5. 输入与焦点（确定语义）

- **挂起纪律**：Measure/Arrange/Emit 内**不得** reconcile/unmount/写 binding——
  `ProcessPendingWork` 不可重入；`OverlayManager` 的关闭/打开请求登记为
  pending，在**本轮布局后有界安全阶段**消费（与现有
  `ProcessPendingWorkCore` 的"重建→布局→响应式→再布局到稳定"循环同阶）：
  消费 → InvalidateLayout → 下一轮 RunLayout 应用（mount/unmount/reposition），
  **返回前收敛到稳定**（有界：关闭请求本身不再产新请求——一轮消费完即停；
  不做 async/thread/无界 drain）；binding 写回调重入由 **manager 自己的
  owner/request-generation 检查**防循环——复用 `FocusManager` 的代际防重入
  **纪律**（同思路），不使用其私有状态。验收断言以 `ProcessPendingWork`
  返回后状态为准（不断言 Arrange 内即时 mount）。
- **外点吞并**（`CloseOnOutsidePress`）：Down 命中点在 popup 子树外且不在
  锚子树内（Parent 链不含 `entry.PopupNode` 也不含 `entry.AnchorNode`）：
  1. 写 `IsOpen=false` → `CloseAll()`；
  2. **该 gesture Down/Move/Up/Cancel/capture 全部归 internal dismiss-target**——
     保持 `IsMounted=true` 至 gesture 结束（全屏命中、`Handled` owner 标）；
     Up/Cancel 后释放、清除。**dismiss-target 只在吞并 gesture 期间存在**——
     `OverlayHost.IsHitTestVisible = (entry != null || swallow active)`：
     非吞并期且无 entry → false（不拦 wheel/hover）；entry 存在 → true
     但 `HitTestSelf=false`（popup 子节点正常可点）。
- **锚子树豁免**：Down 命中点沿 Parent 链含 `entry.AnchorNode` → 正常分发
  不判定外点（锚子树整体豁免；Select toggle 与通用 `Popup` 锚内 trigger 自行
  处理；**锚内非 trigger 普通视图 Down 也不关**——写 guide）。
  落**另一个 popup 锚**上仍是外点：只关旧、吞并；第二次按下才作用新锚。
- **Esc**（`CloseOnEscape`）：**复用 `DispatchKey` 现有顺序**（`InputRouter.cs:353-366`）
  ——`Route` 已先把 KeyDown 发 focused 节点（TextField/TextEditor
  composition-cancel、popup 内自处理 Esc 的控件、Select trigger 的
  `OnKeyDown` 先消费）；**仅未 handled && `Kind==Down && Key==Esc` &&
  entry 存在** → 写 `IsOpen=false` → 关 + 焦点恢复 → `handled=true`；
  然后才是既有 Tab fallback。**不新增 IME 特判/接口**。
  验收：真实 composition 期 Esc 被编辑控件消费 → popup 保持开；下一 Esc 关。
- **焦点恢复**：打开时 `SavedFocus = FocusedNode`（Select 恒=trigger）：
  - Esc/选后关闭 → `SetFocus(SavedFocus)`（仍 mounted/enabled/focusable）→
    否则锚子树首个 focusable（DFS）→ 否则 null；
  - 外点关闭 → **不主动设焦**（当前焦保留；焦在卸载 content 内 →
    `OnNodeUnmounting` 清 null——不自动回锚）；
  - `IsOpen` 外写 false/替换/失活/挤占关闭 → 同外点（不主动设焦）；
  - `Show` 本身不改焦。
- **通用 `Popup` 的 Tab**：content 内 focusable 视图经点击正常获焦；
  `BuildOrder` DFS 含 overlay 子树（序在主树后）；非 modal、不 trap、
  Tab 自由进出；popup 关闭时焦在卸载 content 内 → 清 null。
- **capture**：
  - **打开 = Up(同点)**：Down 只标 pressed，Up 同点才开——opening gesture
    完整落 trigger、不误选；Down-activation 不实现。
  - binding 直写在 gesture 中途变 true → popup 于本轮 `ProcessPendingWork`
    收敛开；**当前 gesture 后续 Up/Move 仍按既有 captured/pressed 原目标
    路由**（capture 不因 popup 挂载改变——原目标继续收 gesture），
    **只有下一个 pointer Down 才对新 hit 结构判定**（可命中 popup）。
    弹层内 Down→正常 hit→press→capture；弹层关时未结束 gesture 的 capture
    由 `OnNodeUnmounting`/InputRouter 清理。
- **wheel**：弹层外 wheel 不关不吞并（真实 hover 分发；锚出可视区由关闭
  规则兜底）；弹层内 wheel 内容消费、到边冒泡。
- **IME**：popup 内 TextField 聚焦时 IME 候选走 `FrameOverlay` 独立通道（正交）。

### 6. `Select<T>` 控件（唯一机制）

```csharp
public sealed class Select<T> : View
{
    public Select(Binding<T> value, IReadOnlyList<SelectItem<T>> items,
                  string? placeholder = null);
    public double MaxPopupHeight { get; init; } = 240;   // 有限、正
}
public sealed record SelectItem<T>(T Value, string? Label = null, bool Disabled = false)
```

- **构造校验**：`items` 内 `Value` 按 `EqualityComparer<T>.Default` **唯一**——
  重复 → `ArgumentException`（显式抛，不静默 first-wins；`T` 可空时 `null`
  合法 Value）。`Label==null` → `Value?.ToString() ?? ""`。
- **打开机制（唯一）**：Select `InteractionState` 持 `open` flag + active index；
  打开/关闭统一调 `OverlayManager.Show/Close`——**与公开 `Popup` 共用同一
  overlay 生命周期/关闭路径**（外点/Esc/失活/锚规则共用同一 manager 入口）；
  `AnchorNode` = trigger 节点本身（同一 rect 用于 placement 与锚豁免）；
  SavedFocus = trigger；内部 flag 驱动（不走 `Binding<bool>`——无 owner
  open-request 状态机）；
  **内部关闭路径（manager 侧关闭 → `Close`）→ Select 同步清 open flag**
  （单次回调同一处——状态随 entry 一致，不留 stale open）。
- **trigger**：`IsFocusable`；显示当前 item Label 或 placeholder（值缺失 →
  placeholder 灰化；值在但项 Disabled → **仍显示该项 Label**——Disabled 只
  影响"能否再选/active 跳过"，不影响回显）；下拉箭头；
  常态/hover/pressed/focused/disabled 视觉走 `ResolvedTheme`。
- **不开条件**（pointer Up-activation 与键盘 activate 统一判）：
  items 空 / 全 Disabled / `Select` 整体 disabled → 不打开。
- **active**：开时=当前选中项（若非 disabled）→ 否则第一个非 disabled；
  键移跳 disabled、两端停不环绕；items 替换按 equality 重定位
  （找不到→第一个非 disabled）；active 移出可视区 → internal ensure-visible
  （按行 Frame 调 ScrollOffset，无新公共 API）。
- **键盘**（确定表）：

  | 状态 | 键 | 行为 |
  |---|---|---|
  | 关 | Enter/Space(Down)、Up/Down | 满足打开条件 → 开并初定位；不满足 → 不消费 |
  | 关 | Tab/Shift+Tab | 正常遍历（不消费） |
  | 开 | Up/Down | active 前后移（跳 disabled、两端停） |
  | 开 | Home/End | 首/末非 disabled 项 |
  | 开 | Enter/Space | 提交→写一次→关→焦留 trigger |
  | 开 | Esc | 关不提交、焦留 trigger（先经 DispatchKey Route 段——trigger
    `OnKeyDown` 自己处理 Esc 即消费，manager 无二次） |
  | 开 | Tab | 关→不消费→主树 Move；Shift+Tab 反向 |
  | 开 | Alt+Down | 不实现 |

- **提交纪律**：绑定写仅选后路径恰好一次；同值重选仍写一次；disabled 项不可提交。
- **popup 尺寸**：宽 = max(trigger 宽, 内容自然宽) → clamp `popupBounds`；
  高 = min(内容, MaxPopupHeight, side 可用)；内套 `ScrollView` 垂直、Standard。
- **主题/DPI**：全 `ResolvedTheme` token；逻辑像素域。

### 7. 使用示例（Component.Build 内，可编译形态）

```csharp
public sealed class SettingsPanel : Component
{
    protected override View Build()
    {
        var theme = UseState("theme", "Light");
        var popupOpen = UseState("popupOpen", false);

        return new VStack(
            // Select: State.ToBinding() 每次 Build 新实例——节点稳定不因实例换卸载。
            new Select<string>(theme.ToBinding(), [
                new SelectItem<string>("Light"),
                new SelectItem<string>("Dark"),
                new SelectItem<string>("System", "跟随系统"),
            ], placeholder: "选择主题"),
            // 通用 Popup: anchor 是 Button，content 是普通 view 树。
            new Popup(
                anchor: new Button("帮助", () => popupOpen.Value = !popupOpen.Value),
                content: new VStack(
                    new Text("按 Tab 可进入弹层控件").Padding(8),
                    new Button("关闭", () => popupOpen.Value = false).Padding(8))
                    .Background(Theme.Light.Surface).Border(Theme.Light.Border, 1, 6),
                isOpen: popupOpen.ToBinding()))
            .Spacing(8).Padding(16);
    }
}
```

### 8. Gallery 集成

`Program.cs` 基础控件 Section：`Select<string>` + disabled `Select` +
内层 `ScrollView` 内 `Select`。不改 csproj/资产。

### 9. 性能与纪律

- popup 关闭时 overlay 层无 popup 子树；`OverlayManager` 常驻固定开销
  （单 entry 槽 + dismiss-target 仅在吞并期存在），不轮询、无每帧分配；
  开关=一次 reconcile。
- placement 每轮 Arrange：clip 收集 O(锚深度) + 几何 O(1)；不订阅事件、有界二次
  measure 至多一次；不做无界 drain/async。
- `FrameOverlay`（IME 候选）不动，正交。

## 任务与依赖

| ID | 任务 | 依赖 |
|---|---|---|
| P-01 | `UiRootLayerView` + `OverlayManager` + `PopupSurface`（挂载、资格门、锚环境现值、关闭规则、失活钩子、有界安全阶段） | — |
| P-02 | `PopupPlacement` 纯算法 + 两阶段 measure 契约 + 有效可视区收集 | P-01 |
| P-03 | `Popup` 公开 view + `PopupPlacement` 枚举 + IsOpen 双向纪律 | P-02 |
| P-04 | 输入：外点吞并+锚豁免、dismiss-target 挂载纪律、Esc（现有 Route 顺序后）、焦点恢复、Up-activation | P-03 |
| P-05 | `Select<T>` 控件 | P-04 |
| P-06 | Gallery 集成 | P-05 |
| P-07 | 文档与交付验收 | P-06 |

依赖链：P-01 → P-02 → P-03 → P-04 → P-05 → P-06 → P-07（串行）。

## 边界与已知余项

- 外点吞并取舍：弹层外第一手势被消费（含另一 popup 锚、scrollbar track）；
  锚子树豁免使 anchor 区域内点击不关弹层（除非 trigger 自身 toggle 逻辑关）——写 guide。
- popup 内 focusable 内容的 Tab 顺序在主树 DFS 之后——非 modal、不 trap，写 guide。
- `Popup` 内嵌 `Popup`/`Select`：**嵌套 `Show` 请求拒绝**（诊断——外层保持开、
  内层不开；非本轮目标，写 guide 边界）。
- 锚部分 clip 时 popup 贴 `Frame` 边可能与 clip 边重叠显示——确定规则。
- `Root.Parent`/`Depth` 变化：`userNode.Parent = _layer`（非 null）、`Depth` +1——
  兼容风险如实记录（任务内核对依赖断言并更新；guide P-07 写明）。
- binding 恒 true + 资格持续不满足 → 保持关不写（无循环）。
