# 点击非交互空白清除键盘焦点 —— 交付

日期：2026-10-26
计划：[docs/plan/2026-10-26-blank-press-clear-focus-plan.md](../plan/2026-10-26-blank-press-clear-focus-plan.md)
评审记录：[docs/return/2026-10-26-blank-press-clear-focus-review.md](../return/2026-10-26-blank-press-clear-focus-review.md)
证据：[docs/return/2026-10-26-blank-press-clear-focus-evidence/](../return/2026-10-26-blank-press-clear-focus-evidence/)

## 用户语义

**左键**点在窗口内、未被任何节点消费的非聚焦区（空白容器、普通 `Text`、根/stack
padding 等）→ 清除当前键盘焦点：按钮蓝色 focus ring 消失，输入框失焦结束文本输入
会话（IME 组合按既有取消契约结束）。点交互区（按钮/输入框/滚动条槽/链接命中区，
或 deepest 链上存在 `IsFocusable` 祖先的修饰符/内部 Text）→ 正常接管或转移焦点，
不先清后转。右键/中键按下、滚轮、悬停、拖拽不触发清焦。

## 实现

`InputRouter.DispatchPointer` 的 `Down` 分支在路由 `OnPointerDown` 之后新增清焦条件
（仅 `PointerButton.Left`、本次按下未被任何节点消费 `context.Handled==false`、
命中链上无可聚焦祖先）→ `_focus.SetFocus(null)`——复用既有焦点撤销契约
（`EndSession`/`OnFocusChanged(false)`/`InvalidateRender`），无 focus-visible、
无新事件协议、无控件白名单。

## 验证（实测）

- 构建：Debug + Release solution，0 警告 0 错误。
- 测试：`CSharp.UI.Tests` **704 / 0**（+6 新增：blank 清 Button 焦/字段会话结束/
  祖先链保留/非左键不清/滚动条消费不清/转焦单次 EndSession）；
  `Integration.Tests` 53/0；`Markdown.Tests` 50/0。
- 独立 GL 验收（fast，run6）：**62 PASS / 0 FAIL**，exit 0。
  主审实际看 `light-1-btn-ring`/`light-2-blank-noring`/`dark-2-blank-noring`
  截图确认蓝框 291/292 px → 0。

## 边界与限制

- GL 截图为离屏 render-to-texture 管线，**非物理窗口 + 非 OS 鼠标注入**；结论覆盖
  框架层输入路由与绘制结果。
- **OS 输入法未实测**：验收通过注入 `ITextInputService` 完成 Start→IsComposing→
  blank→EndSession 契约；物理 IMM/TSF 不在本轮。
- macOS/Linux 未实跑；验证平台 Windows x64。
- 右键/中键按下不清焦是既定取舍。
