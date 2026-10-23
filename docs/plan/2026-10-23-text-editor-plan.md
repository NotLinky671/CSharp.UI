# 多行文本编辑器 TextEditor 计划

计划日期：2026-10-23

状态：已实施——TE-01~TE-06 完成（Windows IMM 微软拼音真机验收 2026-10-23 通过）,TE-07 交付记录见 [2026-10-23-text-editor-delivery](../delivery/2026-10-23-text-editor-delivery.md)。任务索引:[docs/archive/2026-10-23-text-editor-tasks/README.md](../archive/2026-10-23-text-editor-tasks/README.md)(TE-01~TE-07)。

前置：TI-01～TI-10 全部收口并归档 [docs/archive/2026-10-23-text-input-editing-tasks/](../archive/2026-10-23-text-input-editing-tasks/README.md)；交付边界见 [P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)、[TI-09/TI-10 交付记录](../delivery/2026-10-23-ti09-ti10-delivery.md)。

## 1. 目标与产品定位

`TextEditor` 是面向任务描述、备注、表单多行内容的多行纯文本编辑控件：

```csharp
var notes = UseState("notes", string.Empty);
new TextEditor(notes.ToBinding(), "补充任务描述…")
    .FillWidth()
    .Frame(minHeight: 72, maxHeight: 200);
```

- 目标规模：正常备注量级（数百至数千 UTF-16 字符）。不做代码编辑器、虚拟化、语法高亮、富文本、双击选词、右键菜单、公开编辑命令 API、Ctrl+Left/Right 单词导航（TextField 未实现，不新增）。
- 输入路径与 `TextField` 同源。Windows 默认 IMM：宿主已子类化且有输入上下文时报告 `TextInputCapabilities.Full`（`WindowsImeBridge.cs:364-385`），`PublishGeometry`（:522-607）实际调用 `SetCandidatePosition`/`SetCandidateExclude`/`SetCompositionWindow`（CFS 系列）并应答 `IMR_QUERYCHARPOSITION`（:889-940）——**这些是源码已证实存在并接线的 IMM API**；`WM_IME_SETCONTEXT` 屏蔽系统组合窗、候选窗由 IMM 系统绘制（:722-729）。内部 opt-in `UseTsfInputBridge`、macOS/Linux committed-text 回退边界不变。**不新增公开 TSF/候选开关，不新增非 Windows 原生 IME**。
- `TextField` 语义零变化。

## 2. 真实代码基础与缺口

### 2.1 可直接复用（源码核实）

| 资产 | 位置 | 内容 |
|---|---|---|
| 原子事务 | `Editing/TextEditSession.cs` | `Apply(identity, maxLength, op, selection, read, write)`：回读→外部同步→候选（容量释放计入+整字素截断）→单写→回读确认→版本推进。不假设单行。 |
| 契约值类型 | `Editing/TextInputContracts.cs` | `TextRange`/`TextSelection`/`TextSnapshot`/`EditOperation`/`EditResult`/`TextGraphemeBoundaries`；`TryCreate(scrubSingleLine:)` 显式参数。 |
| 历史 | `Editing/TextEditHistory.cs` | `TextEditHistory`+`runtime.Services.HistoryCapacity`；合并窗、双上限、`BreakMerge`。 |
| 组合状态机 | `Editing/TextFieldComposition.cs` | 分段结果、替换区锚定、`MapDisplayToDocument`。 |
| 组合客户端 | `Editing/TextFieldCompositionClient.cs:26` | 实现 **5 接口**：`ITextInputClient`/`ICompositionCharGeometrySource`/`ICompositionIdentitySource`/`IGeometryPublishInvalidationSink`/`ITextInputSelectionTarget`（`TrySetCommittedSelection`，TSF 桥会调）。**耦合点**：构造 `(ViewNode, TextFieldState, int)`。 |
| 会话接线 | `Editing/TextInputServiceSession.cs` | epoch 租约、`Start(node, Func<int,…>)`、`EndForOwner`、`PublishGeometry`；形参全为具体 `TextFieldCompositionClient`。 |
| 运行时接缝 | `Runtime/UiRuntime.cs:702,726,760` | 三处 `is TextFieldState`。 |
| View 钩子 | `Views/View.cs:73,85`、`UiRuntime.cs:499-506` | `Prepare`/`PublishTextInputGeometry` 控件无关。 |
| 命令解析 | `TextEditCommandResolver` | Undo/Redo/Cut/Copy/Paste/SelectAll 平台修饰键。 |
| 多行排版 | `TextLayoutBuilder.cs`+`TextContracts.cs` | `Lines{Start,End,Bounds,Baseline}`、`CaretStops`/`CaretXs`、`HitTestIndex`、`GetRangeBounds`（**单 union Rect**，`TextContracts.cs:237-278`）、`SplitParagraphs` 识别三种换行。 |
| 滚轮换算 | `Input/WheelScrolling.cs:39,42` | `StepDistance`：16px/行档、`uint.MaxValue` 整页、0 禁用、视口封顶；`Pixels` 直通。 |

### 2.2 缺口（逐条落实）

1. **显示排版 ≠ 编辑排版**：
   - `SplitParagraphs`（`TextLayoutBuilder.cs:147-179`）`end>=text.Length` break：`"a\n"` 无末尾空段。
   - `CaretXs`（:116-130）单值且按 `TrimEnd` 夹紧；`LineIndexForStop`（:370-380）共享 offset 归上行——**单 X 数组不能表达“上行末 vs 下行首”两个真实坐标**，需行内 caret 表。
   - `HitTestIndex` 无行定向/affinity；`GetRangeBounds` 是单 union 而非逐行矩形。
2. `TextInputEvent.SanitizeSingleLine`（`InputEvents.cs:201`）单行净化固定。
3. IME 三处硬耦合（2.1）；`ITextInputSelectionTarget` 必须进泛化接口面。
4. `TextFieldState` 仅 `ScrollX`，无 `ScrollY`/`PreferredX`/拖选边缘滚动。
5. `ScrollView` 单轴，嵌套滚轮需消费/冒泡规则。
6. `TextField.Measure` 无限宽排版+固定高 32，与 TextEditor 语义不同。
7. **IMM 桥单行假设待审计**：契约面无显式单行断言（`ImeEventTranslator` 只产 Start/Update/Result/Cancel 事件、`WindowsImeBridge.PublishGeometry` 只消费 `TextInputGeometry` 矩形、`WindowsTextInputRouter` 只转发 `ITextInputClient`）；TE-02 内完成 selection/snapshot/几何应答源码核对 + TextField 回归实测，结论落任务记录，不预先宣称“自动获得”。

## 3. 可实施契约

### 3.1 `TextLayoutRequest.Editing`（默认 false，public bool）

`Editing=false` 路径逐字节保留现有行为。`Editing=true`：

1. `SplitParagraphs` 末尾换行生成**尾随空段**：`"a\n"`→`[0,2)`（含 `\n`）+`[2,2)` 零宽 `TextLineInfo`；空文本仍一行。
2. caret X 按未裁剪内容宽度（行尾空白推进）；`TrimEnd` 仅用于 `ContentWidth`。
3. 调用方保证 `Trimming=None`、`MaxLines=int.MaxValue`；builder 对 `Editing` 不省略。

### 3.2 `TextLayoutResult` 扩展（可编译访问性）

新增 **`public readonly record struct CaretPosition(int Stop, double X, int LineIndex)`** 与 **`public IReadOnlyList<IReadOnlyList<CaretPosition>> CaretsByLine`**：每行一条 caret 子表。**共享 offset 仅在软换行折行点产生两个真实坐标**（上行末 `X=行内容宽`、下行首 `X=OffsetX`）；硬换行符后 offset 只在下行产生 `LineStart` 坐标（上行 `LineEnd` 停在 terminator 前，无共享）。两个 X 都由行内真实排版得出，非单一 `CaretXs` 旁挂枚举。`CaretStops`/`CaretXs`/`GetCaretX`/`HitTestIndex(Point)`/`GetCaretBounds(index,width)`/`GetRangeBounds` 旧成员全部保留、语义不变（`HitTestIndex(Point)` 内部委托新 API 只投影 offset）。

**`public enum TextAffinity { Unspecified=0, LineEnd=1, LineStart=2 }`**（public：`TextLayoutResult` 是 `CSharp.UI` 核心程序集 public 类型，affinity/行定向 API 是外部自定义 `ITextService` 实现者产出/消费编辑排版的必要契约，不缩公开服务面）。

新 API（`public`，签名冻结）：
- `Rect GetCaretBounds(int index, TextAffinity affinity, double width = 1)`
- `int HitTestIndex(Point position, out TextAffinity affinity)`
- `int LineIndexForIndex(int index, TextAffinity affinity)`
- `int LineStartIndex(int lineIndex)` / `int LineEndIndex(int lineIndex)`：`LineEndIndex` 返回该行**换行符前** caret stop（硬换行行 terminator 不在行内可停留位置；terminator 后 offset 即下行 `LineStart`）
- `int HitTestLine(int lineIndex, double x, out TextAffinity affinity)`（返回 caret stop 的 UTF-16 offset，affinity 经 out）
- `IReadOnlyList<Rect> GetRangeRects(int start, int end)`：跨行选区逐行矩形；行间无中间空白填充；行末换行 marker 的几何矩形按该行真实行 Bounds 返回（宽度 0，绘制层用 `SelectionLineEndMarkerWidth=2` 常量涂成可见窄块——几何 API 返回的是测量宽度，不是可见宽度）；空行选中返回该行全行高零宽 rect（同样由绘制层给 2px marker）。
- `double GetCaretX(int index, TextAffinity affinity)`（affinity 消歧的 X）

`Editing` 进排版/纹理缓存键：TextEditor 自有缓存键 `(text,font,pixelScale,innerWidth,lineHeight,editing:true)`；`TextTextureKey` 不改（TextEditor 用自有键类型）。

### 3.3 IME 最小 cutover（接口面冻结，不写伪代码 scaffold）

新增 `internal interface ITextEditSessionOwner`（核心程序集 `Editing/`）：暴露 `Editor`/`History`/`InputSession`/`NativeKeyToken` + 组合客户端所需的控件回调（原子事务、组合提交、选区恢复、失效/重绘通知、几何快照刷新、`TrySetCommittedSelection` 对应的“设置已提交选区”）。成员集在 TE-02 按 `TextFieldCompositionClient` 现有调用点逐一列出并冻结——**只冻结方法与属性签名清单，不写内部 scaffold**。

- `TextFieldState`/`TextEditorState` 各自实现；`TextFieldCompositionClient` 构造改 `(ViewNode, ITextEditSessionOwner, int)`；5 接口实现经窄接口对两控件均可用。
- `TextInputServiceSession` 形参整体换为客户端类型（是否改名 `TextCompositionClient` 由 TE-02 定，internal 自由）；`UiRuntime` 三处 `is TextFieldState`→`is ITextEditSessionOwner`，成员经接口调用。
- `CompositionCharGeometrySnapshot` 增 `ScrollY` 键（TextField 恒 0）；`ScrollX` 保留（TextEditor 恒 0，不删除既有字段）。
- `ITextInputClient`/`CompositionEvent`/`TextInputGeometry`/`ITextInputService`/IMM/TSF 桥契约**零改动**；多行 IME 成立的前提是 cutover 完成（客户端构造面泛化 + TextEditor 发布含 ScrollY/affinity 的二维几何），不是“桥无需工作即自动获得”。TE-02 完成 §2.2-7 审计并把结论写入任务记录。

### 3.4 文档/选区/导航（确定性）

- `TextEditorState`：`Editor`/`History`/`InputSession`/`Composing`/`Caret`/`Anchor`/`ScrollY`/`PreferredX`(`double?`)/`CaretAffinity`(`TextAffinity`)/`IsDragging`/`BlinkEpoch`/`NativeKeyToken`/文档与显示排版缓存。**无 `ScrollX`**。
- `CaretAffinity` 持久规则：点击/上下/resize 后保留；编辑事务/版本变化/左右/Home/End/Page/新点击后按新位置重算（歧义点按来源方向：左来→`LineEnd`，右来→`LineStart`）。
- 上下：`line=LineIndexForIndex(caret,CaretAffinity)`；`targetX = PreferredX ??= GetCaretX(caret,CaretAffinity)`（**当前 affinity 的真实 X**，`GetCaretX(int,TextAffinity)` 新重载）；`HitTestLine(line±1,targetX,out aff)`；越界→文档首/尾。非上下 caret 变化清 `PreferredX`。
- Home/End：`LineStartIndex`/`LineEndIndex`。Ctrl/Cmd+Home/End 文档首/尾。PageUp/Down：`max(1,floor(innerHeight/lineHeight))` 行位移并滚动。Shift 扩选。
- Enter→`\n`（`TextEditKind.Typing`，不新增 kind）；Tab 焦点遍历不插入。

### 3.5 换行/净化/Binding

- **文档**：绑定值为真实值，**框架永不自动回写**；外部 `\r\n` 原文保留，删除/排版/复制按字素正确处理。
- **输入归一化**：`TextInputEvent.SanitizeMultiLine`：`\r\n`→`\n`、孤 `\r`→`\n`、去 `\t`；`SanitizeSingleLine` 不动。已提交文本+粘贴统一执行；`EditOperation` 归一化入口 TE-01 落签名（`TryCreate` 参数或显式预处理）。
- **原生 range→候选映射**：IME Result 的 `Range` 用**真实文档快照 offset**（含 `\r\n` 原长），归一化只作用于 `Replacement` 插入串；**不用归一化长度回裁原 range**。setter 转换的真实值以 `TextEditSession` 回读为准。
- **历史/版本**：分段 Result 经同一原子通道，精确落地才记历史、版本按 `TextEditSession` 实际推进；不承诺无条件 +1。**Enter 插入前后显式 `History.BreakMerge`**（换行是新逻辑行的起点，不归并到前序连续字符）；粘贴/IME 沿用原有事务分组边界（命令入口已 BreakMerge，不改）。

### 3.6 滚动/尺寸/事件

- **wrap 恒开**：排版宽=内容区宽；无 `ScrollX` 状态。超宽单字素按 `BuildLines` 既有硬断；caret 所在行内字素段内 X 精确，**不引入横向滚动配置**。
- `ScrollY ∈ [0, maxScrollY]`，`maxScrollY = max(0, layoutHeight - innerHeight)`（内容底减可视高，不再加 caretHeight，避免多滚一行）。
- **caret 可见性**：键盘/编辑事务后 `EnsureCaretVisible` 二维（caret 宽+像素吸附余量，照搬 TextField :1790 模式）；**用户滚轮滚动允许 caret 离视口**（不改 caret，下次 caret 操作时 ensure）。
- **滚轮**：`IWheelScrollSettingsService.StepDistance` 换算（16px/行档、整页档、0=禁用不消费冒泡、视口封顶；`Pixels` 直通）；可滚方向消费、边界冒泡外层。
- **拖选边缘滚动**：拖动期间指针越出内容区时持续滚动——`OnPointerMove` 推进一次到 pointer 所在行；同时 `GetNextWakeup` 在 `IsDragging` 且 pointer 越界时返回固定 `DragScrollTick=TimeSpan.FromMilliseconds(80)`（一行/tick 有界推进，静止越界指针连续自动滚动；该值是调度步进常量，不是性能承诺）；`OnPointerUp`/`OnPointerCancel`/失焦/卸载/捕获丢失/禁用即停（`IsDragging=false` 后不再返回 wakeup）。无额外全局 timer，复用既有按需唤醒。
- **Measure**：有界宽 `Width ??` 沿用 140 下限模式（`max(min(140,available),约束)`）；wrap 宽=内容区宽。**高度统一**：`preferredHeight = Height ?? (MinLines*lineHeight + 2*VerticalPadding)`，有界/无界高都先按 `preferredHeight` 再由父级 `LayoutConstraints` 夹取；`Frame(height:)`/`Flex` 强制分配最终 viewport，**不按全文增高**——ScrollView/VStack 无界高下仍取 `MinLines` 视窗，超高内容内部滚动。参数校验：`MinLines` 为正整数、`Height`/`Width` 为有限非负、`HorizontalPadding`/`VerticalPadding` 为有限非负（与 `TextField` 现有 init 校验风格一致），非法值构造期显式拒绝。无界宽→`Width ?? 140`（固定退化 fallback，**不加诊断**）；两轴皆无界→自然尺寸（`Width ?? 140` × `preferredHeight`，不加诊断）。
- **退化**：`innerHeight<=0` 或 `innerWidth<=0`→不绘文本/caret，`EnsureCaretVisible` 无操作，几何 `Unavailable`，命中返回 caret 0；超宽单字素的真实 caret 若完全裁出→`Unavailable`（保持文档 offset，不伪造原生位置、不 clamp 显示）。
- **占位符**：空文档无选区时绘制（不换行，超宽裁剪）。

### 3.7 取消接缝（同 TextField 协议）

切焦/失活/卸载/禁用/外部文本来源 MaxLength 变化/编辑命令/鼠标改选区→先取消；复制不取消；失焦折叠选区；同节点同绑定更新/纯重布局不取消。

## 4. 任务拆分（TE-01～TE-07）

| ID | 任务 | 文件范围 | 前置 | 阶段 |
|---|---|---|---|---|
| TE-01 | 编辑排版：`TextLayoutRequest.Editing`、`CaretPosition`/`CaretsByLine`/`TextAffinity`/行内 caret 表、`GetRangeRects`、`HitTestIndex(out)`/`HitTestLine`/`Line*Index` API、`GetCaretBounds(affinity)`/`GetCaretX(affinity)`、`SanitizeMultiLine`、`EditOperation` 归一化入口；文本层单测 | `TextContracts.cs`、`TextLayoutBuilder.cs`、`InputEvents.cs`、`TextInputContracts.cs`、Text.Tests、核心契约测试 | 无 | A |
| TE-02 | IME cutover：`ITextEditSessionOwner`（成员集按调用点冻结）、`TextFieldCompositionClient`/`TextInputServiceSession`/`UiRuntime` 泛化、`Snapshot.ScrollY`；IMM 桥单行假设审计落记录；门槛=TextField IME/编辑回归全绿 | `Editing/*.cs`、`UiRuntime.cs`、`TextField.cs`（接口实现）、`Hosting/*.cs`（审计，仅修实际 bug） | TE-01 | A |
| TE-03 | TextEditor 核心：`TextEditor.cs`+`TextEditorState`（Measure/Arrange/Emit/双缓存/键盘导航+affinity/Enter/净化/指针+拖选+边缘滚动/ScrollY/EnsureCaretVisible 二维/占位符/接缝/历史）；不接 IME | `Views/TextEditor.cs`(新)、核心测试 | TE-02 | B |
| TE-04 | TextEditor IME：客户端适配、几何发布（ScrollY/affinity/快照）、跨行预编辑装饰；TextField IME 回归全绿 | `TextEditor.cs`、`TextFieldCompositionClient.cs` | TE-03 | B |
| TE-05 | 示例文档：ControlGallery 新增多行编辑区；`api-reference`/`platform-notes`/`testing`/README 更新；TodoList 仅说明不改 | samples、docs/guide、README.md | TE-03 | C |
| TE-06 | 图形集成+Windows 真实验收：DPI 排版/选区/装饰离屏；IMM mspinyin 真机（多行组合、跨行 Replacement、锚点、Esc、唯一提交、滚动/resize/焦点/外部绑定）；TSF 内部路径现状回归通过（不新增开关、非交付前置）；docs/return 循环 | OpenTK 测试、验收记录、docs/return | TE-04、TE-05 | D |
| TE-07 | 交付：构建、四套测试两配置、smoke、`docs/delivery/2026-10-23-text-editor-delivery.md`+evidence | docs/delivery | TE-06 | D |

TE-03/TE-04 串行（同文件）；TE-05 可与 TE-04 并行（边界不重叠）。返修 `docs/return/`；TE-06 真机为硬门槛；非 Windows 如实记 committed-only。

## 5. 验收标准

1. 换行中英混排输入；Enter→`\n`；粘贴 `\r\n`/`\r`→`\n`、Tab 移除；MaxLength 字素截断。
2. 左右跨行字素；上下 `PreferredX`（`"a\n"` 空行停放、`a \n` 空白列）；Home/End 行首尾（End 停 terminator 前）；Ctrl+Home/End 文档首尾；PageUp/Down；Shift 扩选。
3. 行尾空白点击归 `LineEnd`；跨行拖选逐行矩形+行末窄 marker、行间不涂空；边缘拖滚；`a\n` 空行可点停。
4. ScrollView 嵌套：可滚消费/边界冒泡；resize/断点切换后 caret/affinity/锚点保持。
5. IMM mspinyin 真机：多行组合、跨行 Replacement、候选锚点（`PublishGeometry`→`SetCandidatePosition`/`SetCandidateExclude`/`SetCompositionWindow`）、Esc 取消、唯一提交、resize/滚动/焦点/外部绑定（含 `\r\n`）。
6. 原生 range 基于真实快照 offset，不被归一化长度错裁；分段 Result 逐项历史；取消不写绑定不推版本。
7. 非 Windows committed-text 可用，声明 committed-only。
8. TextField 回归全绿；四套测试 Debug+Release；三示例 smoke。

## 6. 风险与控制

| 风险 | 控制 |
|---|---|
| `Editing` 破坏 `Text`/`TextField` | 默认 false 隔离；TE-01 文本+集成测试门槛 |
| IME 泛化波及已验路径 | 只换形参；TE-02 门槛 TextField IME 回归全绿；审计落记录 |
| 共享 offset caret 归属错 | 行内 caret 表双坐标+affinity 持久；钉死 wrap 边界/行尾空白/`a\n` |
| caret 可见性 vs 滚浏览 | ensure 仅键盘/编辑后；用户滚不 ensure；`innerHeight<=0` 退化定义 |
| 归一化 vs 原快照 range | Range 真实 offset、Replacement 归一化、回读确认 |
| 滚轮嵌套 | 边界冒泡+`StepDistance` 档（禁用/整页）+真实验收 |
| 伪精确承诺 | 只用可观察行为与既有计数器 |

## 7. 执行与审核

沿用 TI 流程：主审规划审核、实现者自测→`docs/return/` 返修→通过后英文提交推送。计划批准前不动 `src/`/测试。迁移已完成（历史 review 链接修正经主审授权，正文未改）。
