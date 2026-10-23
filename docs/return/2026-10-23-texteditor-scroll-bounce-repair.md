# TextEditor 滚动回跳修复记录（2026-10-23）

## 用户报告

「当行数超过 TextEditor 的 height 时，滚动到最上面之后会突然回到最后一行。」

## 根因（源码级，已用临时桩验证调用链）

`TextEditor.Arrange`（src/CSharp.UI/Views/TextEditor.cs:199-234）此前对**每次** Arrange 在 `node.IsFocused` 时无条件 `EnsureCaretVisible`。外层 `ScrollView.OnWheel`（ScrollView.cs:173-174）在编辑器滚到顶且继续滚轮时消费冒泡事件 → `ScrollOffsetY` 变化 → `InvalidateLayout` → 父级重排把编辑器节点平移（纯位置变化，几何不变）→ `Arrange` 重跑 → ensure 把 `ScrollY` 拉回 caret 所在末行（文档 40 行 ≈ maxScroll 647.5）。

临时栈打印证实：真实用户路径中 `EnsureCaretVisible` 由 `VStack.Arrange → LayoutContext.Arrange → TextEditor.Arrange` 触发；修复前测试实测 `ScrollY` 0→647.5（与用户的"回到最后一行"一致）。

## 修复（最小、不改公开 API、不扩 scroll 系统）

`Arrange` 仅在 caret 可见性几何键真实变化时重判：`EnsureGeometryKey(InnerSize, Font, PixelScale, HorizontalPadding, VerticalPadding)`（值类型 record struct，比较零分配）。内容区宽高、字体、DPI、padding 覆盖 caret 行位置与可见带的全部决定量；绝对位置不入键——外层滚动平移、同 bounds 重建重排不再 ensure。

- 首次记录（prev==null）不 ensure：焦点获得已由 `OnFocusChanged` 收敛。
- 键盘导航/编辑/指针/焦点/ApplyEdit/Undo/`OnFocusChanged`/`SetCaret` 的显式 ensure 全部保留；组合期间 `PublishTextInputGeometry` 的 ensure 保留。
- 宽度 reflow/窗口缩放/DPI 或字体变化仍 ensure（键不同）。

## 回归测试（fails-before 已记录）

`tests/CSharp.UI.Tests/TextEditorTests.cs` 新增 `OuterScroll_BubbledWheel_DoesNotBounceScrollYBackToCaret`：

- `TextEditor` 嵌外层 `ScrollView`（上下各 400px Spacer 真实滚动余量），caret 经 Ctrl+End 到末行；
- 滚轮向上把内部 `ScrollY` 滚到 0（编辑器逐轮消费，到顶后冒泡）；
- 继续向上滚轮 → 外层 `ScrollOffsetY` 真实下降（断言覆盖冒泡重排路径）；
- 断言：`ScrollY` 保持 0、caret/选区不变；重复滚轮、时钟推进、重绘、同 bounds `InvalidateLayout` 重排均不回拉；
- 末尾 `Ctrl+End` 断言显式键盘导航仍 ensure 回 caret 可见位置。

**Before**（无条件 ensure 恢复版）：`Assert.Equal(0, state.ScrollY)` FAIL，Expected 0 / Actual 647.5。
**After**：1/1 通过。

## 自测

- `dotnet build CSharp.UI.slnx`：0 警 0 错。
- `CSharp.UI.Tests` 594/594（+1 新测试）、`CSharp.UI.Text.Tests` 85/85、`CSharp.UI.Integration.Tests` 53/53、`CSharp.UI.OpenTK.Tests` 678/678，均 exit 0。

## 边界

不改变"用户滚允许 caret 离视口"契约；不引入通用 scroll 管理/telemetry/重试；`TextField` 无垂直滚动不受影响；真实窗口滚轮路径由 fast 独立验收。

## 最终 gate(2026-10-23)

fast 独立真实窗口验收通过(8/8 CHECK,exit=0;inner 到顶后外层滚动不回弹、Ctrl+End ensure 969.266、宽度 resize 再 ensure);Debug/Release 回归 594/85/53 + OTK 目标 192 全绿;复审 `docs/return/2026-10-23-text-editor-scroll-bounce-review.md`、证据 `2026-10-23-text-editor-scroll-bounce-evidence/`。主审已批准并授权提交。
