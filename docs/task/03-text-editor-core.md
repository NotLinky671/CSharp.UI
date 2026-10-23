# TE-03：TextEditor 控件核心

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.4、§3.5、§3.6、§3.7。

[返回任务索引](README.md)

## 目标

实现 `TextEditor` 多行编辑控件的全部非 IME 行为：Measure/Arrange/Emit、文档/显示排版缓存、键盘导航（含 affinity）、Enter/净化、指针命中+拖选+边缘滚动、ScrollY、二维 caret 可见性、占位符、禁用/卸载/焦点接缝、历史/原子事务。**不接 IME 会话**（TE-04）。

## 文件范围

- `src/CSharp.UI/Views/TextEditor.cs`（新）：视图 + `TextEditorState`。
- `tests/CSharp.UI.Tests/`：核心控件测试（StubTextService/Recording 替身，无窗口）。
- `src/CSharp.UI/Editing/TextFieldCompositionClient.cs`：若 TE-02 接口面缺项，仅补接口成员不改行为。

## 契约

1. **API（冻结）**：`TextEditor(Binding<string> text, string? placeholder = null)`、`TextEditor(State<string>, ...)`、`.With(Binding<string>)`；init 属性 `Width`(`double?`)、`Height`(`double?`)、`HorizontalPadding=10`、`VerticalPadding=6`、`CornerRadius=6`、`MaxLength=int.MaxValue`、`BlinkInterval=530ms`、`MinLines=3`。`DebugName`/`IsFocusable=true`。无 `MaxVisibleLines`/`WrapText`/`ScrollX`。
2. **Measure**：有界宽 `Width ??` 沿用 140 下限模式（`max(min(140,available),约束)`）；wrap 宽=内容区宽（`Frame` 内宽 −2×HorizontalPadding）。**高度统一**：`preferredHeight = Height ?? (MinLines*lineHeight + 2*VerticalPadding)`，有界/无界高都先按 `preferredHeight` 再由父级 `LayoutConstraints` 夹取；`Frame(height:)`/`Flex` 强制分配最终 viewport，**不按全文增高**——ScrollView/VStack 无界高下仍取 `MinLines` 视窗，超高内容内部滚动。参数校验：`MinLines` 正整数、`Height`/`Width`/`HorizontalPadding`/`VerticalPadding` 有限非负（与 `TextField` init 校验风格一致），非法值构造期显式拒绝。无界宽→`Width ?? 140`（固定退化 fallback，**不加诊断**）；两轴皆无界→`Width ?? 140` × `preferredHeight`（不加诊断）。`innerHeight<=0` 或 `innerWidth<=0`→不绘文本/caret、几何 `Unavailable`、命中返回 caret 0。
3. **状态**：`TextEditorState`（`InteractionState`）字段：`Editor`、`History`、`InputSession`、`Composing`、`Caret`、`Anchor`、`ScrollY`、`PreferredX`(`double?`)、`CaretAffinity`、`IsDragging`、`BlinkEpoch`、`NativeKeyToken`、文档/显示排版缓存（键=`(text,font,pixelScale,innerWidth,lineHeight,editing:true)`）。
4. **导航**：
   - Left/Right：字素跨行；Shift 扩选；清 `PreferredX`、按来源方向设 `CaretAffinity`（左来→`LineEnd`，右来→`LineStart`）。
   - Up/Down：`line=LineIndexForIndex(caret,CaretAffinity)`；`targetX = PreferredX ??= GetCaretX(caret,CaretAffinity)`；`HitTestLine(line±1,targetX,out aff)`→caret+affinity；越界→文档首/尾；`PreferredX` 保留至非上下 caret 变化。
   - Home/End：`LineStartIndex`/`LineEndIndex`（End 停 terminator 前）。
   - Ctrl/Cmd+Home/End：文档首/尾。PageUp/Down：`max(1,floor(innerHeight/lineHeight))` 行位移+滚动。
   - Enter→`\n`（`TextEditKind.Typing`），**插入前后显式 `History.BreakMerge`**（换行是新逻辑行起点，不归并前序字符）；Tab 焦点遍历不消费。
   - Backspace/Delete：选区或字素删除（`\r\n` 单字素）。
   - 编辑命令：Undo/Redo/Cut/Copy/Paste/SelectAll 经 `TextEditCommandResolver`+`TextEditHistory`+`TextEditSession` 同一通道。
5. **指针**：`OnPointerDown` 焦点+取消组合+`HitTestIndex(local,out aff)`→caret+affinity+anchor；`OnPointerMove` 拖动 caret；`OnPointerUp/Cancel` 结束拖动。`IndexFromPosition` 用 `HitTestIndex`+文档排版（同 TextField :1635 模式）。
6. **拖选边缘滚动**：`OnPointerMove` 时 pointer.Y 出内容区→推进到 pointer 所在行；同时 `GetNextWakeup` 在 `IsDragging` 且 pointer 越界时返回固定 `DragScrollTick=TimeSpan.FromMilliseconds(80)`（一行/tick 有界推进，静止越界指针连续自动滚动；该值是调度步进常量，不是性能承诺）；`OnPointerUp`/`OnPointerCancel`/失焦/卸载/捕获丢失/禁用即停（`IsDragging=false` 后不再返回 wakeup）。无额外全局 timer，复用按需唤醒。
7. **滚动**：`ScrollY∈[0,maxScrollY]`，`maxScrollY=max(0, layoutHeight-innerHeight)`（内容底减可视高，不多加 caretHeight）；`OnWheel` 按 `StepDistance`（16px/行档、整页档、0 禁用不消费冒泡、视口封顶、`Pixels` 直通）；可滚方向消费、边界冒泡。用户滚不改 caret、不 ensure。
8. **caret 可见性**：键盘/编辑事务后 `EnsureCaretVisible` 二维（caret 宽+像素吸附余量，照搬 TextField :1790 模式）；用户滚不 ensure。
9. **绘制**：背景/边框/焦点环/禁用色/占位符（空文档无选区时，不换行超宽裁剪）同 TextField 主题族；文本 `TextPainter` 同原点；选区用 `GetRangeRects` 逐行矩形（几何返回测量宽 0，绘制层用 `SelectionLineEndMarkerWidth=2` 常量涂成可见窄块）；caret 用 `GetCaretBounds(index,affinity)`；内容区 `PushClip`。
10. **接缝**：切焦/失活/卸载/禁用/外部变化/编辑命令/鼠标改选区→先取消组合（本任务 `Composing` 恒 null，但接缝代码保留供 TE-04）；复制不取消；失焦折叠选区；同节点同绑定/纯重布局不取消。
11. **归一化**：`OnTextInput`/粘贴经 `SanitizeMultiLine`；IME Result 的 `Range` 用真实文档 offset、`Replacement` 归一化（本任务 IME 未接，仅粘贴/已提交文本路径生效）。

## 验收（核心测试，无窗口）

- `StubTextService` 构造多行 `TextLayoutResult`：`"a\n"` caret 可停末尾空行；wrap 边界 offset 点击归 `LineEnd`/`LineStart` 正确；`a \n` 行尾空白点击 caret 在行尾。
- 上下 `PreferredX` 跨不等宽行；Home/End/PageUp/Down/Ctrl+Home/End 断言 offset 精确；Shift 变体选区方向正确。
- Enter 插入 `\n` 走原子事务+历史且**前后显式 BreakMerge**（Enter 前后 undo 精确恢复文本与选区，不并入前序连续键入）；粘贴 `\r\n`→`\n`；`\r\n` 单字素删除；MaxLength 字素截断。
- 拖选跨行+边缘滚动：pointer 越界后 `GetNextWakeup` 返回固定 tick、ScrollY 持续推进；pointer 静止越界期间连续滚动；释放/取消/失焦后停。滚轮可滚消费/边界返回未消费。
- caret 键盘操作后可见；用户滚后 caret 可离视口且不自动回滚。
- 外部绑定更新取消（保留接缝）+夹选区+清历史；禁用/卸载/失焦行为同 TextField 协议。
- `TextField` 全部既有测试全绿（TE-02 门槛延续）。
