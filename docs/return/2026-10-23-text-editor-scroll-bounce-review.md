# TextEditor 滚动回弹(嵌套 ScrollView 滚顶后继续滚→跳回 caret 行)独立复审

日期:2026-10-23。复审依据 HEAD `0855c68` 源 + good 本轮工作树差异(仅 `src/CSharp.UI/Views/TextEditor.cs`,+19/−3)。

## 复现路径(源码级证实,与用户报告一致)

1. 焦点在 TextEditor、caret 在末行,内层 `ScrollY > 0`。
2. 用户向上滚:`TextEditor.OnWheel`(`TextEditor.cs:1477-1526`)逐帧 `state.ScrollY` 钳至 `[0,maxScroll]`,**不** ensure(行 1521 注释"用户滚只改滚动");到顶后 `next == state.ScrollY`,返回 `false`(行 1516-1519)。
3. 滚轮未消费 → `InputRouter.Route`(`InputRouter.cs:510-`)沿 `node.Parent` 冒泡 → 外层 `ScrollView.OnWheel`(`ScrollView.cs:123-176`)消费、`ScrollOffsetY` 变化、`InvalidateLayout`。
4. 下一布局轮:`ScrollView.Arrange` 以 `bounds.Y - ScrollOffsetY` 重排子节点(行 89-93)→ `TextEditor.Arrange` 收到**只平移、未变尺寸**的 `bounds`。
5. 修复前 `Arrange` 无条件 `if (node.IsFocused) EnsureCaretVisible(node, state)`(旧行 215-218)→ `state.ScrollY` 被拉回 caret(末行)→ **回弹**。

对称问题:任何祖先滚动导致聚焦 editor 平移重排时,内部 `ScrollY` 都会被拉回 caret。

## 修复评估(good,TextEditor.cs diff,终版)

`Arrange` 现以 `state.ArrangedEnsureKey`(`TextEditor.EnsureGeometryKey` record struct,`TextEditor.cs:1949`)门控:

- key = 值类型 `(InnerSize, Font, PixelScale, HorizontalPadding, VerticalPadding)`——**不含绝对位置**:祖先滚动只改 bounds 位置时 key 不变 → 不 ensure → 内部 `ScrollY` 保持 → 修复本 bug。
- **旧 key 非 null 且与新 key 不等才 ensure**;首次记录(prev==null)只存 key 不 ensure——聚焦时 caret 收敛已由 `OnFocusChanged` 完成(行 551),无 blur-reset 字段。
- innerSize/font/DPI/padding 变化(reflow、resize、断点、字体/DPI 切换)→ key 变 → ensure,caret 仍可见,resize 语义保持。

边界核对:键盘/编辑/指针入口仍各自调用 `EnsureCaretVisible`,不受门控影响;`PublishTextInputGeometry` 只在组合期间 ensure,组合 caret 锚定不变。

## 单测确认(good 新增 `TextEditorTests.OuterScroll_BubbledWheel_DoesNotBounceScrollYBackToCaret`,79 行)

我已独立阅读该测试(主审已先读):真实外层 `ScrollOffsetY 350→更低`(冒泡 wheel,`Assert.True(outer < before)`)、`ScrollY` 在 wheel/clock advance/同-bounds `InvalidateLayout` 重建后保持 0、caret 不变、Ctrl+End 键盘 ensure 恢复 `scrollAtCaret`——断言点与用户 bug 路径及门控语义一一对应。随套件运行通过(Debug/Release 594/594)。

构建原始输出(控制台尾部,verbatim;未单独存文件、未重跑):

```
Debug   --no-incremental: 已成功生成。0 个警告 0 个错误 (00:00:06.90)
Release --artifacts-path ~/.omp/tmp/scroll-bounce-release-artifacts:
        已成功生成。0 个警告 0 个错误 (00:00:06.92)
```

（此前 Release 常规路径曾 MSB3021:用户运行中的 ControlGallery PID 61228 锁 `bin/Release` DLL,未杀进程,按指示用 `--artifacts-path` 全新输出根完成真实 Release 编译。）

## 备注

## 修复后真实窗口验证(2026-10-23,证据 `2026-10-23-text-editor-scroll-bounce-evidence/`)

临时 harness `~/.omp/tmp/te07-scroll-bounce/`(不入库):真实 640×480 窗口,外层 ScrollView + 280px header + `TextEditor(60 行,Height=160)` + 400px footer;真实 SendInput 点击聚焦、Ctrl+End(真实 scancode + EXTENDED)、`PostMessage WM_MOUSEWHEEL`(消息级注入,非 HID;OS 物理滚轮投递已知不送时使用,lParam=屏幕坐标,光标随 editor 实时 Track),HWND 由宿主 `WindowsImeBridge.GetWindowHandle` 反射取得。

结果(exit=0,run.log 8/8 PASS):

- `focus` 真实点击聚焦、hovered=TextEditor;
- `caret-end` Ctrl+End → caret=470(文档尾)、scrollY=969.266(ensure 生效);
- `outer-precondition` 向下滚 → 外层 offset 0→360(editor 平移向上,inner 保持 969.266);
- `inner-top` 向上滚 → inner 回到 0,外层仍 360;
- `outer-moved`/`no-bounce` 继续向上滚 → 外层 360→0(editor 纯平移重排),**inner 保持 0 不回弹**(旧代码会 snap 到 ~969);
- `keyboard-ensure` Ctrl+End → inner 恢复 969.266(键盘 ensure 仍工作,截图 line 53-57);
- `resize-ensure` SetWindowPos 宽 ×1.15 → EnsureGeometryKey 变化 → 重排 ensure → inner 回到 969.266(宽度 reflow 语义保持,截图 line ~末行)。

截图:`cap-inner-top-outer-scrolled.png`(inner=0 显示 line 1-5)、`cap-after-ctrlend-ensure.png`(line 53-57)、`cap-after-resize-ensure.png`(宽窗 line ~末行)。

回归(冻结源,新编译非旧 DLL):Debug 全套 CSharp.UI.Tests 594/85/53 全绿 + OTK 目标 192 项(TextEditor/Gallery/Composition/IME 相关类)全绿;Release 经 `--artifacts-path` 全新输出目录构建 0 警 0 错(用户 ControlGallery PID 61228 运行中锁旧 Release DLL,未杀,新产物路径绕开),同套 594/85/53 + OTK 目标 192 全绿。

边界:滚轮为消息级 `WM_MOUSEWHEEL` 注入(SendInput 物理滚轮在本机投递不到该 GLFW 窗口,SendInput 键鼠按键/点击可投递并已用于点击与 Ctrl+End);外层滚动重复帧与同尺寸 Arrange 均已覆盖。

未重跑已确认的失败(源码链已证);不复活归档工具;不改 good 核心、测试与计划;未触碰 IME 默认/TSF 开关。
