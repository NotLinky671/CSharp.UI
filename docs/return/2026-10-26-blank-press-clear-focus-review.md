# 点击非交互空白清除键盘焦点（blur）—— 评审记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-blank-press-clear-focus-plan.md](../plan/2026-10-26-blank-press-clear-focus-plan.md)
基线：b26e0af（Markdown 代码块复制已推送）

## 源码依据（已核实）

- `Button.Emit`（Controls.cs:302-303）：`IsFocused ? theme.FocusRing : theme.Border`——
  用户看到的"蓝框"是键盘焦点环，不是 pressed/hover。
- `Button.OnPointerDown`（Controls.cs:321-330）：`context.Runtime.SetFocus(node)`。
- `FocusManager.SetFocus`（InputRouter.cs:49-88）：焦点只在①新焦点、②`SetFocus(null)`、
  ③焦点节点卸载/禁用时离开；无"点空白清焦"分支。
- `InputRouter.DispatchPointer` Down（旧 :237-266）：冒泡 `OnPointerDown` 后无清理。

## 最终实现

`InputRouter.DispatchPointer` 的 `Down` 分支末尾（路由 `OnPointerDown` 之后）：

```
if (!context.Handled                    // 本次按下未被任何节点消费（含 deepest==null）
    && e.Button == PointerButton.Left   // 仅左键（primary）触发 blur；右键/中键不清
    && !ChainHasFocusable(_hovered))    // deepest→root 链上存在 mounted+enabled+focusable → 不清
    _focus.SetFocus(null);
```

- **消费优先**：Button/TextField 自 `SetFocus`、ScrollView 吞 bar 槽、RichText 按住链接、
  Slider 拖拽都返回 `true` → 不触发清焦，IME/拖拽/按压契约不动。
- **祖先链兜底**：控件内 Text/修饰符 deepest 但父可聚焦 → `ChainHasFocusable` true，
  不清焦（含未消费场景）。
- **一次转移**：field→button 由 Button `SetFocus` 直接转移（旧 client EndSession 一次），
  无"先清后转"双重会话；消费方自 `SetFocus(null)` 的罕见重入由 `Handled` 保留。
- **无新协议/控件白名单/遥测**；不动 `FocusManager`/`HitTest`/IME 服务。

## 测试演进（受审）

- 初版 `BlankLeftPress_KeepsFocus_WhenHitChainHasFocusableAncestor` 实际点在 Button 自身
  （Button.OnPointerDown 消费）——未覆盖"未消费但链上 focusable 祖先"分支，被主审要求
  改为最小 `FocusablePassThrough` 裸容器（`IsFocusable=true`、`OnPointerDown=false`、
  child=Text）：deepest=Text，链上祖先 focusable → 焦点保留。
- 三个 blank 测试的空白点原先 `Frame.Right+60` 可能超出 viewport（覆盖的是 `HitTest=null`
  而非"窗口内空白"）——统一改为小控件 + 大 viewport 内真实空白点。
- `FocusTransferHost` helper 一度 dead（test 直接 inline VStack）——最终保留并改用
  `Binding` 参数形态，无重复 host。

## 实测（Debug）

- `dotnet build CSharp.UI.slnx`：0 警告 0 错误。
- `CSharp.UI.Tests`：**704 / 0**（+6 新增：blank 左键清焦/字段会话结束/祖先链保留/
  非左键不清/滚动条消费不清/转焦单次 EndSession）。
- `Markdown.Tests` 50/0、`Integration.Tests` 53/0（回归不破）。

## 边界与待办

- **真实 IME**：未跑系统输入法——`BlankPressRecordingTextInputService`（Start/EndSession
  计数）覆盖契约层会话终止时机；`runtime` 注入 `ITextInputService` 组合
  `Start→IsComposing→blank→composing=false+EndSession` 已在 fast GL run6 验收
  （合同层，非 OS 物理 IME）。
- **真实 GL 实测**（fast，独立会话）：Gallery run6 **62 PASS / 0 FAIL**，exit 0。
  原始证据 `docs/return/2026-10-26-blank-press-clear-focus-evidence/`（raw 日志 + 截图）。
  主审实际看 `light-1-btn-ring`（按钮蓝环 291px）/`light-2-blank-noring`（空白后 0px）与
  `dark-2-blank-noring`（dark theme 内点击，292px→0px）确认用户蓝框已清；`FocusedNode=null`。
  - 窗内空白/普通 Text/嵌套 padding 左键按下 → 焦点清空。
  - Tab 重新聚焦 + Enter 激活照常。
  - field 焦点（StartSession 已建）→ 空白 → EndSession + `IsFocused=false`；
    field→button 转焦 `EndCallCount` 恰好 +1（无先清后转）。
  - ScrollView bar 点击/thumb 拖/Up、wheel、move、右键按下 → 焦点保留。
- **验证平台**：Windows x64 Debug+Release solution 0w0e、Core.Tests 704/0、
  Integration.Tests 53/0、Markdown.Tests 50/0。macOS/Linux 未实跑（沿用既有平台边界）。
- 右键/中键点空白不清焦是既定取舍（上下文/辅助输入非 primary，桌面惯例）。
- Gallery 截图是离屏 render-to-texture 管线，**非物理窗口 + 非 OS 鼠标注入**；
  结论覆盖框架层输入路由与绘制结果，不宣称物理鼠标端到端实测。
