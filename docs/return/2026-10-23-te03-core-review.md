# TE-03 TextEditor 控件核心审核记录（独立验收，非交付声明）

日期：2026-10-23

## 结论

TE-03 通过：实现（`src/CSharp.UI/Views/TextEditor.cs`，~1930 行 + `TextEditorState`）符合计划 §3.4–3.7；`CSharp.UI.Tests` 581/581 全绿（含新增 `TextEditorTests` 20 项与共享 `FromTransaction` cutover 的既有回归）；真实 GLFW 窗口 smoke 全场景 PASS（exit=0）。

## 真实窗口验收证据

证据文件：`2026-10-23-te03-core-review-evidence/realui-smoke-log.txt`（完整 PASS/diag 日志）、`cap-s03-selection.png`、`cap-s08-selection.png`（含 `.bmp` 原图；cap-s08 已目视核验为真实像素：多行文本 + 蓝底选区 + 焦点环）。

运行方式（throwaway harness，`~/.omp/tmp/te03-realui/`；`AssemblyName=CSharp.UI.OpenTK.Tests` 走 `InternalsVisibleTo` 到达 `UiApplication.CurrentHost.Window` 与 `WindowsImeBridge.GetWindowHandle`）：

```
dotnet build realui.csproj -c Release
dotnet bin/Release/net10.0-windows/CSharp.UI.OpenTK.Tests.dll   # stdout 直读；exit=0
```

驱动线程 `SetCursorPos`/`SendInput` 注入（scancode + `KEYEVENTF_EXTENDEDKEY`，真实点击先取前台），状态经 `_ctx.Dispatcher.Post` 在 UI 线程直读 `TextEditorState`/`TextEditorMeasureCache`。原始关键日志摘录：

```
PASS boot / hwnd / fg2；S00 点击聚焦；S00b 'a'+Left+'b'→'ba'（caret 真实在插入点）
S01 doc='hello\nworld'（Enter 原子换行，无重复 CR/字符、无 IME 消费）
S02a Home→6 / S02b End→11；S03 拖选 3→7；S03a/b Left→折叠 6,6
S04a/b Up/Down preferredX 5↔11；S04c 20×203 行长行外部绑定→60 行
S05a 窗口缩 45%：editorFrame 776→321.3、innerW 756→301.3、60→120 行 soft-wrap 重排
S06a wheel 0→96 消费（结合 UserWheelScroll_IsNotBouncedBackByNextFrame 单测证明不回弹）
S07a 悬停越界 tick=80ms：166.47→240.95→296.82 连续推进；S07b/c release 后 IsDragging=False 且 500ms 不再滚
```

## 复审项裁决

- **F1（已实现修正）**：初版 `Emit` 每帧无条件 `EnsureCaretVisible`，与计划"用户滚允许 caret 离视口"冲突；已改为只在键盘/编辑/pointer/Arrange/焦点入口 ensure，并新增回归测试 `UserWheelScroll_IsNotBouncedBackByNextFrame`。
- **F3（已修正）**：`Measure` 在 `IsWidthTight` 分支曾双扣 `HorizontalPadding`；现换行宽唯一来源 `frameWidth − 2×HorizontalPadding`。
- **R8（已修）**：`TextEditorMeasureCache.Key`/`GetLayout` 热路径每次 `new` record 两次堆分配 → `TextEditorLayoutKey` 改 `readonly record struct` 零分配。
- **R9（判定非 bug，记录澄清）**：`MoveCaretHorizontal` 无"折叠到选区边缘"分支——与 `TextField.MoveCaret`(:1567-1586) 契约一致：非 Shift 方向键按字素移一停点并在 `SetCaret` 折叠 anchor；真实窗口 S03a 验证折叠 + caret 合法。不改变既有语义。

## Driver 侧纠错（非产品 bug，过程记录）

- GLFW 用 `HIWORD(lParam)` scancode 映键；VK-only SendInput 丢方向/Home/End → 补真实 scancode + `EXTENDEDKEY` 恢复。
- `SetForegroundWindow` 单独调用被 Windows 前台限制 → 真实点击自窗先行。
- S05/S07 初轮失败为测试输入设计（短行不 wrap、内容不足到 maxScroll），加长行/补充内容后通过。

## 边界（如实）

- 跨 OS 窗口边缘的拖选：`HandleMouseLeave→PointerCancel` 清 `IsDragging`（计划"捕获丢失即停"）；拖出窗口的自滚不在范围。
- IME 组合路径未验（`Composing` 恒 null，TE-04 接 client）；Unicode 已提交通道在 caret 处插入正确。
- 非 Windows 平台本轮无真机。
