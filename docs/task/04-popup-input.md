# P-04 输入语义：外点吞并+锚豁免、Esc、失活、capture/Up-activation

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §5。
前置：P-03。状态：待授权实施。

## 目标

`Popup`/`OverlayManager` 打开态的指针/键盘/失活语义落地：light-dismiss+整手势吞并、
锚子树豁免、Esc 归锚焦点、失活关闭、Up-activation 打开纪律。

## 改动面

- **外点判定**（`InputRouter`/`OverlayManager` 内部钩点，任务内定位置并记录）：
  entry 存在且 `CloseOnOutsidePress` 时，`Down` 分发前判命中点归属：
  - **内**（hit 目标沿 Parent 链含 `entry.PopupNode`）→ 正常分发；
  - **锚豁免**（hit 目标沿 Parent 链含 `entry.Anchor`——锚子树整体豁免）→
    正常分发（不判定外点；Select/通用 trigger 借此 toggle；锚内非 trigger
    的普通视图 Down 也不关——写 guide）；
  - **外**（含另一 Select/Popup 的锚、scrollbar track、空白）：
    1. 写 `IsOpen=false` → `CloseAll()`；
    2. **该 gesture Down/Move/Up/Cancel/capture 全部归 internal
       dismiss-target**（全屏命中、`Handled` owner 标）——`IsMounted=true`
       至 gesture 结束（Up/Cancel）后释放清除；**只在吞并 gesture 期间存在**——
       非吞并期 `OverlayHost.IsHitTestVisible=false`（恒 `HitTestSelf=false`——
       显式覆写，非默认）。
  - 落**另一个 popup 锚**上仍是外点：只关旧、吞并；第二次按下才作用新锚。
    （嵌套 `Show` 在 P-03 已拒——popup content 内不再 `Show`）。
- **Esc**：复用 `DispatchKey` 现有顺序（`InputRouter.cs:353-366`）——`Route` 已先把
  KeyDown 发 focused 节点（composition-cancel/popup 内自处理 Esc 的控件/Select
  trigger 的 `OnKeyDown` 先消费）；**仅未 handled && `Kind==Down && Key==Esc` &&
  entry 存在**时 overlay 拦截：写 `IsOpen=false` → 关 + `SetFocus(SavedFocus)`
  （仍有效否则锚子树首 focusable 否则 null）→ `handled=true`；再落既有 Tab
  fallback。无 IME 特判/新接口。
- **失活**：P-01 钩子——窗口失活（`HandleFocusChanged(false)`/runtime 通知点）→
  `CloseAll()`；本任务只负责挂接点验证。
- **capture/Up-activation**：
  - **打开 = Up(同点)**：trigger/锚内 Down 只标 pressed，Up 同点且
    `IsOpen=false`→true（或 Select 的内部 open 调用）才打开——opening gesture
    完整落 trigger，不侵入弹层 hit，无误选；
    Down-activation 不实现（不需要 release-capture 特例分支）。
  - 弹层内 Down→正常 hit→press→capture，Up/Cancel 走既有释放；
    弹层关闭时未结束 gesture 的 capture 由 `OnNodeUnmounting`/InputRouter
    既有卸载清理。
  - `Popup` 的 `IsOpen` 经外部 binding 在 gesture 中途变 true → popup 于本轮
    `ProcessPendingWork` 收敛开；**当前 gesture 后续 Up/Move 仍按既有
    captured/pressed 原目标路由**（capture 不因 popup 挂载改变——原目标
    继续收 gesture）；**只有下一个 pointer Down 才按新 hit 结构判**
    （可命中 popup）。
- **focus**（焦点恢复规则 plan v3 §5 冻结）：
  - 打开时 `SavedFocus = FocusedNode`（Select 恒=trigger；通用 `Popup` 可为
    null/任意 focusable）；`Show` 本身不改焦；
  - **Esc/选后关闭** → `SetFocus(SavedFocus)`（仍 mounted/enabled/focusable）
    → 否则锚子树首个 focusable（DFS）→ 否则 null；
  - **外点/外写 false/替换/失活/挤占关闭** → **不主动设焦**（当前焦保留；
    焦在已卸载 popup content 内 → `OnNodeUnmounting` 既有路径清 null——
    不自动回锚）；
  - popup content 内 focusable 视图经点击正常获焦（`BuildOrder` DFS 含
    overlay 子树、序在主树后）；**不做 trap**、不拦 Tab——非 modal。
- **wheel**：弹层外 wheel 不关闭不吞并（按真实 hover 分发；锚出有效可视区由
  P-01 关闭规则兜底）；弹层内 wheel 内容消费、到边冒泡。
- **`Popup` 外点/内点判定与 anchor 的关系**：`entry.Anchor` 是 `Popup` 节点
  （非 anchor 子树的某个叶子）——锚子树整体豁免；`entry.PopupNode` 是
  OverlayHost 下的 popup 根——含其全部后代。

## 不做

modal 遮罩绘制、focus trap、Down-activation、点击穿透选项、多 popup 外点优先级。

## 验收（headless runtime）

- D1：popup 开着、HitTest popup 内 → popup 内容正常 hit/press/capture。
- D2：外点 Down → `IsOpen=false` + entry 关；同 gesture Move/Up 不触发下方
  Button `OnClick`（计数不变）；下一新 gesture 正常落下方。
- D3：外点 Down 落**另一** `Popup`/`Select` 锚 → 只关旧弹层（新锚回调=0）；
  第二次按下才作用于新锚。
- D4：外点 Down 落 scrollbar track → 关弹层、不开始拖拽（`ScrollOffset` 不动）。
- D5：Down 落**当前** `Popup` 锚子树内 → 不关、不吞并（trigger 可达 toggle）。
- D6：Esc → `IsOpen=false`、关、`FocusedNode` = SavedFocus（仍有效；SavedFocus
  失效 → 锚子树首 focusable；皆无 → null）。
- D7：失活钩子调用 → `CloseAll()`、popup 卸载、焦/capture 清理。
- D8：Up-activation——trigger Down 时弹层尚未开，同 gesture Up 同点打开；
  该 gesture 的 Up 不会落入新 popup 内容（不误选断言：popup 内 item 无 press）。
- D9：吞并 gesture 期间 dismiss-target `IsMounted=true`（HitTest 归它）；
  Up 后释放，后续 hit 正常。
- D10：弹层外 wheel → 下方 ScrollView 正常滚动（弹层按 P-01 规则保持/关闭按
  锚可视区判定）；弹层内 wheel 由内容消费。
- 断言经 `DispatchPointer`/`DispatchKey`/`HitTest`/`FocusedNode`/`ScrollOffset*`/
  Binding 计数/`IsMounted`，不 pin 内部字段名。
