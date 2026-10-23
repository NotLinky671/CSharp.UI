# TE-03：TextEditor 控件核心

创建日期：2026-10-23

状态：已完成（2026-10-23 主审批准；真实窗口验收证据见 [TE-03 核心审核记录](../return/2026-10-23-te03-core-review.md)）。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.4、§3.5、§3.6、§3.7。

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

## 实施记录（2026-10-23）

### 文件

- `src/CSharp.UI/Views/TextEditor.cs`（新，~1990 行）：`public sealed class TextEditor : View` + `internal sealed class TextEditorState : ITextEditSessionOwner`。
- `src/CSharp.UI/Editing/TextEditHistory.cs`：新增 `internal static TextHistoryEntry FromTransaction(EditResult, EditOperation, TextSelection)`——把 TextField 原有的“替换区按前后两份文档字素边界扩边 + HashSet/substring 逆操作切片 + MergeKind 判定”收敛为唯一共享实现；`TextField` 两处调用点（普通编辑 :587、组合提交 :1375）全部 cutover，私有 `BuildHistoryEntry`/`MergeKindFor` 已删，不再有两套 history 规则。
- `src/CSharp.UI/Views/View.cs`：新增 `internal virtual void OnClockTick(ViewNode, TimeSpan)`（默认无操作）。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：`OnClockTick()` 在推进后调用 `focused.View.OnClockTick(focused, Clock.Now)` 再做唤醒失效判定——拖选边缘滚动这类有界内部推进在安全阶段执行。
- `tests/CSharp.UI.Tests/TextEditorTests.cs`（新，20 项）：核心行为集，见下“自测”。

### 签名

```csharp
public TextEditor(Binding<string> text, string? placeholder = null)
public TextEditor(State<string> state, string? placeholder = null)
public TextEditor With(Binding<string> text)
// init：Width(double?)/Height(double?)/HorizontalPadding=10/VerticalPadding=6/
//       CornerRadius=6/MaxLength=int.MaxValue/BlinkInterval=530ms/MinLines=3
public override string DebugName => $"TextEditor({Placeholder ?? "text"})";
public override bool IsFocusable => true;
```

init 校验：`Width`/`Height`/`HorizontalPadding`/`VerticalPadding`/`CornerRadius` 必须有限非负，`MinLines > 0`，非法值 init 期 `ArgumentOutOfRangeException`。

### 关键结构决策

- **排版键 = `readonly record struct TextEditorLayoutKey(Text, Font, PixelScale, InnerWidth)`**：热查询路径 `GetLayout` 比较两个值类型，零堆分配（复审指出的 `sealed record` + `cache.Key` 每次 new 两次堆分配已消）。`TextEditorMeasureCache`/`TextEditorDisplayCache` 保留为 per-layout 的引用类型缓存槽。
- **全部三处排版请求（Measure / GetLayout / GetDisplayLayout）`Editing = true`**：硬换行分段、尾随空段、`caretsByLine` 归属都由同一请求开关供给；文档/显示串缓存键各自含 `(text, font, pixelScale, innerWidth)`。
- **换行宽唯一来源**：`wrapWidth = frameWidth − 2×HorizontalPadding`，`frameWidth = Width ?? (IsWidthTight ? MaxWidth : min(140, available))`——`Measure` 不再 tight 分支扣一次、`wrapWidth` 又扣一次的双减。
- **文本原点顶部对齐**：`DisplayTextBounds = inner 高度取 max(inner, layout)` 时 `ResolveOrigin` 的垂直居中会把偏短文本下推；统一改为 `TextOrigin = SnapToPhysicalPixel(textBounds 左上角)`，绘制与 `HitTestIndex` 命中共用同一原点；`Paint` 的 bounds 高 = `layout.Size.Height`，其内部居中偏移恒 0。
- **`Emit` 不做 caret ensure**：用户滚轮把 caret 滚出视口是合法状态；每帧 ensure 会立刻回弹 ScrollY。ensure 只在键盘/编辑/pointer/`Arrange`（重排后重新收敛）/`OnFocusChanged` 入口执行（复审 F1，已由无窗口 UiRuntime 复现脚本验证：wheel→ScrollY=144→下一帧仍为 144）。
- **拖选边缘滚动 due-gate**：`GetNextWakeup` 拖选越界返回 `DragScrollTick=80ms`；宿主每轮 UI 循环都调 `OnClockTick`，故 `state.LastDragScrollTick`（`TimeSpan?`）自限 `now - last < 80ms` 才跳过——首次拖选 `null` 立即滚；`IsDragging=false`（释放/取消/失焦/卸载/禁用）即停，不再返回 tick。
- **caret 移动不含“折叠到选区边缘”分支**：与 `TextField.MoveCaret`(:1567-1576) 契约一致——非 Shift 方向键把 caret 按字素移一停点并在 `SetCaret` 折叠 anchor 到新 caret，不是跳到 SelectionStart/End；复审建议按既有 consumer 逻辑核对后判定 `TextEditor.MoveCaretHorizontal` 与 TextField 同语义，未改。
- **`OnUnmount`**：取消组合 → `EndInputSession` → `ClearComposing` → 复位 `IsDragging`/`DragPointer`；`IsLayoutEquivalent(View other) => false`（与 TextField 相同，交互状态由节点持有）。
- **`TextEditorState` 全委托 `ITextEditSessionOwner`**：`SyncCompositionDocument(node)`/`ApplyCompositionCommit(node,range,result)`/`MaxLength(node)`/`TextIdentity(node)`/`IsTextEditNode`/`IsEditableNode` 经 `node.View is TextEditor field` 转发到 `TextEditor` 同名内部方法；`Composing` 接缝保留（本任务恒 null，TE-04 接 client 生效）。

### 自测

`dotnet build CSharp.UI.slnx` 0 警告 0 错误；`CSharp.UI.Tests` **581/581 全绿**（既有 561 + `TextEditorTests` 新增 20）。

新增 20 项（真实 `TestApp`/`UiRuntime` + `StubTextService` 编辑排版 `Editing=true`）：

| 测试 | 覆盖 |
|---|---|
| `Measure_UsesMinLinesViewport_NotFullContentHeight` | `MinLines*lineHeight + 2×VerticalPadding` 视口高（30 行内容仍 64.5px） |
| `Constructor_RejectsInvalidInitParams` | `MinLines=0`/`Width=-1`/`Height=NaN`/`HorizontalPadding=-1` init 期拒绝 |
| `LeftRight_MoveCaretByGrapheme_AndSetAffinity` | 字素停点 + 右来 `LineStart` affinity |
| `UpDown_PreservesPreferredX_AcrossUnequalLines` | 不等宽行间 `PreferredX` 保留与恢复 |
| `HomeEnd_MoveToLineBounds` | `LineStartIndex`/`LineEndIndex` + `LineStart`/`LineEnd` affinity |
| `CtrlHomeEnd_JumpDocumentBounds` | `IsCommandPressed` → 文档首/尾 |
| `ShiftRight_ExtendsSelection` / `Shift+Left` 收缩 | 方向选区 |
| `Enter_InsertsNewline_AsAtomicTransaction_WithHistory` | `\n` 走 `TextEditSession` + 历史项 |
| `Enter_BreakMerge_IsolatesNewlineFromTyping` | 前后显式 `BreakMerge` → `ab`/`"\n"`/`cd` 独立三项 |
| `Paste_NormalizesCRLF` | `\r\n`→`\n` |
| `Backspace_DeletesCRLF_AsSingleGrapheme` | `\r\n` 单字素删除 |
| `MaxLength_TruncatesInsert` | 截断到 MaxLength |
| `Wheel_ScrollsContent_WithoutMovingCaret` | 用户滚不改 caret（§3.7） |
| `Wheel_AtTop_UpScroll_Bubbles` | 边界返回未消费 |
| `UserWheelScroll_IsNotBouncedBackByNextFrame` | **F1 回归**：滚出视口后重绘不回弹 |
| `Drag_OutOfBounds_ReturnsWakeupTick_AndAdvancesScroll` | 越界 wakeup≈80ms + `OnClockTick` 推进 + 释放停 |
| `EnsureCaretVisible_AfterKeyboardNavigation` | `Ctrl+End` 后 `ScrollY>0` caret 入视口 |
| `ExternalBindingChange_ClampsSelection_ClearsHistory` | 外部变化 seam |
| `Blur_CollapsesSelection` | 失焦折叠选区 |
| `EmptyDocument_ShowsPlaceholder` | 占位符纹理命令 |

真实窗口/视觉面 smoke 由 fast 独立 harness 完成（本轮 build 未与 fast 并发）。`TextField`/`TextFieldCompositionTests`/`TextFieldUndoRedoTests` 等既有套件在共享 `FromTransaction` cutover 后全绿，consumer 回归保持。

### 边界

- `Composing` 接缝全部就位但本任务不创建组合客户端（恒 null）；IME 会话归 TE-04。
- `OnClockTick` 的 due-gate 保证 ≤1 行/80ms，不承诺实际帧率；`DragScrollTick` 是调度步进常量。
- 非 Shift 方向键移动语义与 `TextField` 相同（字素移一停点 + 折叠），不提供“折叠到选区边缘”模式。
- 视觉面/真实 DPI/真实字体的最终验收依赖 fast 的真实窗口 smoke（本轮仅无窗口 `StubTextService` 层）。
