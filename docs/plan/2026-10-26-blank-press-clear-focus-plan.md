# 点击非交互空白清除键盘焦点（blur on blank press）

日期：2026-10-26
基线：b26e0af（Markdown 代码块复制已推送）。本轮只改 Core 输入路由 + 定向测试，不动控件实现、
不加 focus-visible、不改 IME 服务。

## 用户报告（ground truth）

Gallery 点击按钮后保留蓝色描边、点空白不消失。根因（已核实）：

- `Button.Emit`（Controls.cs:302-303）：`IsFocused ? theme.FocusRing : theme.Border`——
  蓝框 = focus ring，非 pressed/hover。
- `Button.OnPointerDown`（Controls.cs:321-330）：`SetFocus(node)` 拿焦。
- `FocusManager.SetFocus`（InputRouter.cs:49-88）：焦点只在新焦点/卸载/禁用时离开；
  `InputRouter.DispatchPointer`（:237-266）Down 路径无"空白清焦"分支。
- 用户明确要求：点空白即 blur。

## 方案（最小、无新协议）

`InputRouter.DispatchPointer` 的 `Down` 分支：路由 `OnPointerDown` 后（或 _hovered null 时），
若本次按下**没有任何节点消费**（`context.Handled` false）且 **focus 仍未落在命中链上**
（deepest→root 任一 `IsFocusable` 节点），则 `_focus.SetFocus(null)`。

- **判断顺序在路由之后**：消费节点（Button/TextField 自己 SetFocus、ScrollView 吞 bar 点击、
  RichText 链接按住、slider 拖拽）不触发清焦——不扰 IME/拖拽/按压语义。
- **祖先链检查兜底**：即使视图没消费也没自己 SetFocus（deepest 是 Button 内 Text 等修饰符），
  命中链上存在 mounted+enabled+focusable 节点即视为"焦点候选区"，不清焦；
  控件自己也总是 `SetFocus`（幂等短退），此检查只兜未消费场景。
- **仅 `PointerButton.Left`**：右键/中键按下不动焦点（桌面惯例——右键是上下文操作，不 blur）。
- **消费后焦点被外部显式清掉的罕见情况**（handler 里 `SetFocus(null)`）：跳过检查，
  不覆盖消费方决定。
- `_hovered==null`（viewpoint 外）：`IsFocusable` 链为空 → 左键清焦，一致 blur。
- 新焦点 `SetFocus` 发生在旧焦点 client `EndSession`/IME 结束后（既有 SetFocus 时序），
  本改不引入"先清后转"双重会话；`Route` 消费标记保持原义。

## 不做

- 不做 focus-visible/模态；不按控件类型白名单；不加新事件协议/遥测。
- 不改 Move/Up/Cancel/Wheel、不改 HitTest、不改 SetFocus 本身的校验/服务撤销契约。
- 不验证用户系统 IME——Fake/Stub 会话终止已覆盖契约层。

## 验证（消费者可观察，无结构 pin）

`tests/CSharp.UI.Tests/InputFocusTests.cs`：

1. `BlankLeftPress_ClearsButtonFocus`：Button 获焦 → 左键点空白 → FocusedNode=null +
   帧命令中按钮矩形 stroke 回到 Border 色（FocusRing 消失）。
2. `BlankLeftPress_ClearsFieldFocusAndEndsSession`：TextField 焦点+激活 client →
   左键点空白 → FocusedNode=null + `service.ActiveClient` 收 EndSession（契约层 IME 结束）。
3. `BlankLeftPress_KeepsFocus_WhenChainContainsFocusable`：焦点 Button → 点其内部 Text 区
   （Button 未消费时按链检查）→ FocusedNode 仍是 Button；等价场景：deepest 不可聚焦但
   链上有可聚焦祖先。
4. `RightOrMiddlePress_DoesNotClearFocus`：右键/中键点空白 → 焦点保留。
5. `ConsumedPress_DoesNotClearFocus`：ScrollView standard bar 槽点击（消费返回 true）→
   已聚焦 TextField 不清焦。
6. `FocusTransfer_NoInterimClear`：field1 焦点 → 左键点 button → FocusedNode 直接=button，
   IME 只经一次 EndSession（Fake 计数）。
