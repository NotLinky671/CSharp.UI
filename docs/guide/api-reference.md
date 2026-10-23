# 公开 API 参考（首版）

命名空间统一为 `CSharp.UI`；后端实现位于 `CSharp.UI.Skia` 与 `CSharp.UI.OpenTK`（宿主入口仍在 `CSharp.UI` 命名空间下）。

## 命名空间约定

核心（`CSharp.UI` 程序集）的全部公开类型位于命名空间 `CSharp.UI`；文本与图片实现（`CSharp.UI.Text` 程序集）位于命名空间 **`CSharp.UI.Skia`**；宿主（`CSharp.UI.OpenTK` 程序集）位于命名空间 `CSharp.UI.OpenTK`。

> 说明：文本实现**不使用** `CSharp.UI.Text` 作为命名空间。核心已有公开视图类型 `CSharp.UI.Text`，而 C# 不允许命名空间与类型同名共存（引用时会报 CS0434），因此实现命名空间改为 `CSharp.UI.Skia`；程序集名仍为 `CSharp.UI.Text`。

## 应用入口

| 类型 | 说明 |
|---|---|
| `UiApplication.Run(View root, UiApplicationOptions? options = null) → int` | 创建单窗口、初始化 OpenGL 上下文并运行事件循环，返回退出码 |
| `UiApplicationOptions` | 窗口标题/初始逻辑尺寸/可调整大小/VSync/主题/默认字体/文本服务/图片服务/剪贴板服务/缓存容量/退出快捷键 |
| `UiApplication.RequestExit(int exitCode = 0)` / `RequestRedraw()` | 退出或请求重绘 |
| `UiDispatcher.Post(Action)` | 把后台结果投递到 UI 线程 |
| `UiHostContext` | 通过 `UiApplicationOptions.Started/FrameRendered` 观察运行时、调度器、DPI 与帧计数 |

## 视图描述

| 类型 | 说明 |
|---|---|
| `View` | 视图描述基类（不可变、可重复声明） |
| `Component` | 组件基类，`protected abstract View Build()`；`protected State<T> UseState<T>(string name, T initialValue)`、`UseBinding<T>(...)` |
| `VStack` / `HStack` / `ZStack` | 垂直 / 水平 / 层叠布局；`.Spacing(...)`、`.Align(...)` |
| `Flow` | 自动换行布局：`.Spacing(...)`（项间距）、`.LineSpacing(...)`（行间距）、`.Align(Alignment)`（行内对齐） |
| `AdaptiveStack` | 按父级有效逻辑宽度自动切换横/纵布局；`.Spacing(...)`、`.VerticalSpacing(...)`、`.Align(...)`、`.WideAlign(...)` |
| `Spacer` / `Spacer(double fixedSize)` | 吸收剩余空间 / 固定空隙 |
| `ScrollView(View content, ScrollAxis axis)` | 单轴滚动容器，保留滚动位置；`WheelSensitivity`（有限非负，默认 1）缩放滚轮距离 |
| `Text` | 文本；`.WithFont(...)`、`.Wrap(maxLines)`、`.Align(...)`、`.Ellipsis()`、`.WithLineHeight(...)` |
| `Rectangle` | 纯色/圆角/描边矩形 |
| `Image(string source)` | 图片；`.Resize(ImageResizeMode)` |
| `Button(string label, Action? onClick)` | 按钮；`.Primary()`、`.With(label, onClick)` |
| `Toggle(Binding<bool> value, string? label)` | 开关（也可用 `Toggle(bool, Action<bool>?, string?)`） |
| `Slider(Binding<double> value, double min, double max, double step, double? width)` | 数值滑块 |
| `TextField(Binding<string> text, string? placeholder)` | 单行输入框 |
| `TextEditor(Binding<string> text, string? placeholder)` | 多行输入框：硬换行 + 按内容区宽软折行，固定 `MinLines`（默认 3）行视口内纵向滚动（内部 `ScrollY`，不随内容增高）；init `Width`/`Height`/`HorizontalPadding`/`VerticalPadding`/`CornerRadius`/`MaxLength`/`BlinkInterval`/`MinLines` |
| `ForEach<T>(items, item => view, item => key)` | 带稳定键的动态集合 |
| `KeyedView`（`.Key(string)`） | 为同级节点提供稳定身份 |

## 修饰符

`Padding`、`Frame`、`Width`、`Height`、`Size`、`Background`、`Foreground`、`Border`、`CornerRadius`、`Opacity`、`Clip`、`Disabled`、`Theme`、`Font`、`Key`、`Flex`、`FillWidth`。
修饰符返回**新的包装视图**，调用顺序具有语义（见[修饰符顺序](modifier-order.md)）。

## 弹性尺寸

| 类型 | 说明 |
|---|---|
| `.Flex(grow = 1, shrink = 1, basis = null, min = 0, max = 正无穷, axis = FlexAxis.Main)` | 让内容在最近的堆叠容器中按剩余空间增长或压缩；`basis` 省略表示自然尺寸 |
| `.FillWidth()` | 占满可用宽度：横向 Stack 主轴上参与伸缩，纵向 Stack 交叉轴上占满；宽度无界时回退自然尺寸 |
| `FlexView` / `FillWidthView` | 上述修饰符对应的包装视图（一般通过扩展方法使用） |
| `FlexAxis` | `Main`（由最近布局容器主轴解析）/ `Horizontal` / `Vertical` |

- 分配规则：先按约束后的 basis 测量，再分配正剩余空间（按 grow 权重）或不足空间（按 shrink × 约束后 basis 权重）；达到 `min`/`max` 的项目冻结后把剩余量分配给其它项目。零 basis 没有可压缩量。
- 分配结束后按最终主轴长度重新测量全部子项，换行文本的高度随实际宽度变化，并把复测后的最终高度向上传递给普通容器；固定项与 min 之和无法容纳时输出带节点路径的溢出诊断，不产生负尺寸。
- `.Frame(width: 240)` 始终是固定尺寸，不会被 `.Flex()`/`.FillWidth()` 压缩或拉伸；需要“通常 240、可以变窄”时使用 `.Flex(basis: 240, min: 120)`。
- **同轴嵌套外层生效**：外层 `.Flex()`/`.FillWidth()` 完整覆盖内层的弹性声明（grow/shrink、basis、min/max 与 FillWidth 的填充标记都不叠加、不残留内层值），只有 `.Frame(...)` 的硬性固定/最小/最大声明会被保留；不同轴独立处理。
- `.FillWidth()` 覆盖内层 `.Flex()` 的显式 basis：基准回到自然尺寸，因此宽度无界时按契约回退自然宽度，而不是继续使用内层 basis。
- 参数在声明时校验：权重与显式 basis/min 必须有限且非负，`max` 允许正无穷，禁止 NaN，且 `min ≤ max`。
- 固定尺寸与上限：`.Frame(width: 240)` 是固定尺寸；`.Frame(minWidth: 200, maxWidth: 240)` 是有范围的盒子。声明的 `maxWidth`/`maxHeight` 只是上限，子项实际可用尺寸取父级约束与声明的**较小者**，因此窄容器不会按更宽的声明排版文字。范围重载参数较多时可直接使用 `new FrameView(content, maxWidth: 320, alignment: Alignment.Start)`。
- 响应式用法、断点约定与固定尺寸迁移见[响应式布局：使用与迁移](responsive-layout.md)。

## 自动换行 Flow

| 类型 | 说明 |
|---|---|
| `Flow(params View[] children)` / `Flow(IReadOnlyList<View>)` | 按声明顺序从左到右贪心分行；子项顺序与身份不变 |
| `Spacing` / `.Spacing(double)` | 行内项目间距（有限非负，默认 0） |
| `LineSpacing` / `.LineSpacing(double)` | 行与行之间的间距（有限非负，默认 0） |
| `LineAlignment` / `.Align(Alignment)` | 行内交叉轴对齐：`Start`（默认，顶部）/ `Center` / `End`（`Stretch` 会拉伸到行高） |

- 行宽来自父级提供的逻辑约束并保留小数：`width < breakpoint` 与断点无关，本容器直接使用可用宽度；宽度无界时所有项目排成单行、使用自然尺寸。
- 行高由该行最终测量后的最高项目决定；整体高度按行间距累加，可直接作为纵向 `ScrollView` 的内容，宽度变化会更新内容高度与滚动边界。
- 不可压缩的硬固定项目（`.Frame(width:...)`、`.Frame(minWidth:...)`、`Rectangle.FixedSize`）宽于整行时独占一行并输出可定位溢出诊断；可重新测量的普通超宽内容按整行宽度重新测量。空容器、零宽项目、正好放满都有确定行为。
- `.FillWidth()` 作为“占整行”的请求独占一行（不参与逐行权重分配）；无界宽度时回退自然尺寸。
- 第一版不做逐行 Flex 权重分配：直接子项的 `.Flex()` 会输出可定位不支持诊断，请把弹性表单项放入内嵌 `HStack`/`AdaptiveStack`。

## 自适应堆叠 AdaptiveStack

| 类型 | 说明 |
|---|---|
| `AdaptiveStack(double breakpoint, params View[] children)` | 父级有效逻辑宽度 `< breakpoint` 为纵向，`>= breakpoint` 为横向；`breakpoint` 必须有限且为正 |
| `Spacing` / `.Spacing(double)` | 宽模式（横向、HStack 语义）的项间距 |
| `VerticalSpacing` / `.VerticalSpacing(double)` | 窄模式（纵向、VStack 语义）的项间距 |
| `WideAlignment` / `.WideAlign(Alignment)` | 宽模式交叉轴（垂直）对齐，默认 `Center`（与 `HStack` 一致） |
| `NarrowAlignment` / `.Align(Alignment)` | 窄模式交叉轴（水平）对齐，默认 `Stretch`（与 `VStack` 一致） |

- 容器类型、运行时父节点与子项集合始终不变，只切换测量/排列策略，因此输入框文本、光标、选区、焦点、组件 `UseState` 与滚动状态在宽窄切换时保持。
- 断点依据父级扣除 Padding/Frame 后提供的逻辑宽度，不使用窗口尺寸、物理像素或 DPI；宽度无界时按横向自然尺寸处理。
- 复用统一 Stack 算法：`FlexAxis.Main` 随当前模式的最近 Stack 主轴解释，显式 `FlexAxis.Horizontal` 的宽度 basis 在纵向模式下不会变成高度。
- 窄模式的 Spacer 与普通 VStack 一致（仅在主轴有界时吸收剩余空间）；宽模式与 HStack 一致。

## 任意内容分支 Responsive

| 类型 | 说明 |
|---|---|
| `Responsive(Func<ResponsiveInfo, View> builder)` | 按父容器最终有效逻辑宽度选择内容结构；有界宽度占满所分配宽度、高度自然展开，宽度无界时使用自然尺寸 |
| `ResponsiveInfo` | 只读 `AvailableWidth` / `IsWidthBounded`；构造器拒绝 NaN、负宽度以及“有界标记与有限性不一致”，属性不可 `set`/`init` |

- **只在安全阶段构建**：测量（含探测与自然尺寸测量）只登记最终有效宽度提案，不执行 builder、不协调/卸载节点；
  `UiRuntime` 在重建与布局之间执行 builder 并重新布局到稳定后才绘制。同一节点一轮内多次提案合并为最后一次有效提案，
  短时间连续的状态与宽度变化只构建一次。
- **builder 是纯描述构建**：可以读取已有的 `State<T>`（自动登记为动态内容的依赖，状态变化后只重建该内容），
  但不得写状态（写入相同值同样抛出 `InvalidOperationException`）、不得创建 `UseState`、不得执行 IO 或操作窗口
  （`SetViewport`/`SetEnvironment`/`BuildFrame` 等）。状态所有权仍只属于外层 `Component.Build`。
- **依赖随条件切换清理**：每次成功构建后依赖集合被整体替换，条件分支移除后不再触发构建；builder 抛错时保留上一次成功内容的依赖并恢复读取作用域，
  节点卸载后丢弃过时请求、不再构建。动态内容循环与整体工作预算受限，并给出带节点路径的诊断。
- **内容切换沿用协调规则**：同父级同 Key/类型的节点复用；跨父级或类型变化会真正卸载并清理焦点、指针捕获、选区与
  布局/绘制阶段补登记的绑定依赖。需要跨分支保持输入焦点和本地编辑状态时优先使用 `AdaptiveStack`；
  持久数据放在外层 `State`/`Binding`，框架不承诺跨父级迁移焦点或本地编辑状态。

## 状态与绑定

| 类型 | 说明 |
|---|---|
| `State<T>` | `Value` 读写；读取登记依赖（组件 `Build` 与 `Responsive` builder 各自独立作用域），写入时把变化派发到对应更新目标；`PeekValue()` 不登记依赖 |
| `Binding<T>` | `Value` 双向读写；`Binding.From(get, set)`、`Binding.Constant(value)`、`Binding.Select(...)`；`State<T>` 可隐式转换为 `Binding<T>`；`Read(node)` 在构建之外补登记长期依赖，读取它的节点卸载或被替换描述时释放 |

## 输入事件（平台无关）

| 类型 | 说明 |
|---|---|
| `PointerEvent` / `PointerEventKind` / `PointerButton` | 指针按下、移动、释放与取消（窗口失焦） |
| `WheelEvent` | 滚轮，`DeltaY > 0` 表示向上滚动；`Mode`（`WheelDeltaMode.Steps` 默认 / `Pixels`）显式声明增量单位 |
| `WheelDeltaMode` | `Steps`：归一化步数，一格为 1（触摸板可为小数）；`Pixels`：增量已是逻辑像素。单位必须显式标记，不能按数值大小猜测 |
| `WheelScrollSettings` / `IWheelScrollSettingsService` | 系统每格行数/字符数与步数→逻辑距离换算（每行 16、每字符 8 个逻辑像素；`0` 禁用、`uint.MaxValue` 整页） |
| `KeyEvent` / `UiKey` / `KeyModifiers` / `KeyEventKind` | 控制键与修饰键；字符输入不通过键盘事件传递 |
| `TextInputEvent` | 宿主提交的 Unicode 文本；`TextInputEvent.SanitizeSingleLine(...)` 按单行规则过滤、`SanitizeMultiLine(...)` 归一化多行换行（`\r\n`/孤 `\r`→`\n`、去 `\t`） |

## 文本编辑快捷键

`TextField`/`TextEditor` 的内置编辑命令只通过平台快捷键触发，没有公开的方法入口：

| 命令 | Windows / Linux | macOS |
|---|---|---|
| 撤销 | Ctrl+Z | Command+Z |
| 重做 | Ctrl+Y 或 Ctrl+Shift+Z | Command+Shift+Z（没有 Command+Y） |
| 剪切 / 复制 / 粘贴 | Ctrl+X / Ctrl+C / Ctrl+V | Command+X / Command+C / Command+V |
| 全选 | Ctrl+A | Command+A |

- 剪切、复制、粘贴、全选带 Shift 时不触发命令。
- Alt 或 AltGr（常表现为 Control+Alt）参与的组合、错误平台的主修饰键（例如 macOS 的 Control、Windows 的 Win 键）以及其它额外修饰键都不触发命令；CapsLock 不改变含义。
- 未带命令修饰键的字符通过 `TextInputEvent` 传入，不走键盘快捷键。
- 当前平台的输入能力用 `TextInputForwarder.EffectiveService(context.Runtime.Services.TextInputService).Capabilities` 查询；各平台能力与验证级别见[平台、字体与原生依赖](platform-notes.md)。
- 真实输入法的手动验收步骤见[输入法手动验收清单](ime-manual-acceptance.md)。

## 多行编辑行为（TextEditor）

- **换行归一化**：`OnTextInput` 与粘贴的**插入串**经 `TextInputEvent.SanitizeMultiLine`（`\r\n`/孤 `\r`→`\n`、去 `\t`）；外部写入绑定的原文与 setter 回读值不会被自动重写。
- **原子事务与历史**：每次编辑走 `TextEditSession` 原子事务；`Enter` 前后显式 `BreakMerge`（换行不并入前序连续键入），Ctrl+Z/Y 按事务粒度恢复文本与选区；连续同类键入、Backspace/Delete 按既有合并规则归并。
- **视口与滚动**：`MinLines` 决定可视行数；`Height`/`Frame(height:)`/`Flex` 可强制视口；超高内容在内部 `ScrollY` 滚动，滚轮可滚消费、到边界冒泡给外层 `ScrollView`；用户滚轮不改 caret、不自动回滚；键盘/编辑/指针操作后 caret 二维收敛到视口。
- **导航**：Left/Right 按字素跨行（非 Shift 与 `TextField` 同语义：移一停点并折叠选区）；Up/Down 用 `PreferredX` 跨不等宽行、按 `TextAffinity` 消软换行共享 offset 歧义；Home/End 行首/行尾；Ctrl(Cmd)+Home/End 文档首/尾；PageUp/Down 按视口行数位移；Shift 扩选；Tab 不消费（焦点遍历）。
- **指针**：点击按 `HitTestIndex` 落 caret（软换行边界按 `LineEnd`/`LineStart` 归属）；拖动跨行选区；指针在内容区上/下边界外悬停时按固定步进自动滚动，释放/取消/失焦/禁用即停。
- **外部绑定变化**：保持文本原文，夹取 caret/选区到字素边界并清空撤销历史（与 `TextField` 同接缝协议）；禁用/卸载/失焦行为同 `TextField`。
- **排版扩展点**：编辑相关排版经 `TextLayoutRequest.Editing=true`（尾随空段、行尾空白 caret、逐行 caret 表）；软换行共享 offset 的归属用 `CaretPosition`/`TextAffinity`；逐行选区矩形用 `TextLayoutResult.GetRangeRects`。自定义 `ITextService` 需实现同一请求开关语义（见[绘制与服务契约](#绘制与服务契约)）。

## 基础类型


## 绘制与服务契约

| 类型 | 说明 |
|---|---|
| `DrawCommand`（`RectangleCommand`、`TextureCommand`、`PushClipCommand`、`PopClipCommand`） | 有序绘制命令；由布局/绘制阶段生成，渲染器按顺序消费 |
| `RenderFrame` | 一帧的命令、视口逻辑尺寸、DPI 与清屏色 |
| `ITextService` | 排版、测量、字体度量与 CPU 栅格化（实现：`CSharp.UI.Skia.SkiaTextService`）。`Rasterize(layout)` 返回白色遮罩；`Rasterize(layout, color)` 按实际文字颜色 RGB 栅格化（alpha 由绘制端 tint 统一乘一次），外部实现可只实现旧方法，颜色重载有默认实现 |
| `TextLayoutRequest` / `TextLayoutResult` | 排版请求（文本、字体、宽度、换行、省略、对齐、行高、DPI、字距、`Editing` 编辑排版模式）与结果（尺寸、行、字素 caret 表）。编辑控件用 `Editing=true`：尾随换行产空段、行尾空白 caret 未裁剪、`CaretsByLine` 行内 caret 表（软换行共享 offset 在上下行各产真实坐标） |
| `CaretPosition` / `TextAffinity` | 编辑排版：行内 caret 的文档 offset/X/视觉行；`TextAffinity` 消共享 offset 歧义（`LineEnd`=上行末、`LineStart`=下行首）。`TextLayoutResult` 提供 `GetCaretX`/`GetCaretBounds`/`LineIndexForIndex`/`HitTestLine`/`HitTestIndex`/`LineStartIndex`/`LineEndIndex`/`GetRangeRects`（逐行选区矩形，含硬换行 marker/空行 marker；caret 定位查询不分配临时集合，范围矩形结果按需生成） |
| `ITextureService` | 按稳定键缓存 GPU 纹理、有界淘汰（实现：`CSharp.UI.OpenTK` 的 OpenGL 纹理服务）。`BeginFrame()` 由运行时在每帧生成命令前调用，用于保护当前帧与上一帧引用的纹理、在帧边界回收容量 |
| `IImageService` | 图片解码与缓存（实现：`CSharp.UI.Skia.SkiaImageService`） |
| `IClipboardService` | 剪贴板读写（宿主默认使用 GLFW 剪贴板） |
| `IWheelScrollSettingsService` | 系统滚轮设置来源（Windows 宿主只读 `SPI_GETWHEELSCROLLLINES/CHARS`；默认每格 3 行/3 字符） |
| `IUiDispatcher` / `IUiClock` | 调度与时钟 |
| `UiRuntime` / `UiRuntimeServices` / `ViewNode` | 运行时：节点树、布局、输入分发与诊断；无窗口测试可直接驱动 |

## 无窗口测试替身

`CSharp.UI.Testing` 命名空间提供 `HeadlessUiDispatcher`、`StubTextService`、`RecordingTextureService`、`StubImageService`、`StubClipboardService`、`StubWheelScrollSettingsService`、`TestUiClock`。
它们不依赖窗口、字体原生库或 OpenGL 上下文，供核心布局/状态/交互测试使用。
