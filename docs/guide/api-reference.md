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
| `Spacer` / `Spacer(double fixedSize)` | 吸收剩余空间 / 固定空隙 |
| `ScrollView(View content, ScrollAxis axis)` | 单轴滚动容器，保留滚动位置；`WheelSensitivity`（有限非负，默认 1）缩放滚轮距离 |
| `Text` | 文本；`.WithFont(...)`、`.Wrap(maxLines)`、`.Align(...)`、`.Ellipsis()`、`.WithLineHeight(...)` |
| `Rectangle` | 纯色/圆角/描边矩形 |
| `Image(string source)` | 图片；`.Resize(ImageResizeMode)` |
| `Button(string label, Action? onClick)` | 按钮；`.Primary()`、`.With(label, onClick)` |
| `Toggle(Binding<bool> value, string? label)` | 开关（也可用 `Toggle(bool, Action<bool>?, string?)`） |
| `Slider(Binding<double> value, double min, double max, double step, double? width)` | 数值滑块 |
| `TextField(Binding<string> text, string? placeholder)` | 单行输入框 |
| `ForEach<T>(items, item => view, item => key)` | 带稳定键的动态集合 |
| `KeyedView`（`.Key(string)`） | 为同级节点提供稳定身份 |

## 修饰符

`Padding`、`Frame`、`Width`、`Height`、`Size`、`Background`、`Foreground`、`Border`、`CornerRadius`、`Opacity`、`Clip`、`Disabled`、`Theme`、`Font`、`Key`。
修饰符返回**新的包装视图**，调用顺序具有语义（见[修饰符顺序](modifier-order.md)）。

## 状态与绑定

| 类型 | 说明 |
|---|---|
| `State<T>` | `Value` 读写；读取登记依赖，写入在值变化时通知依赖组件；`PeekValue()` 不登记依赖 |
| `Binding<T>` | `Value` 双向读写；`Binding.From(get, set)`、`Binding.Constant(value)`、`Binding.Select(...)`；`State<T>` 可隐式转换为 `Binding<T>` |

## 输入事件（平台无关）

| 类型 | 说明 |
|---|---|
| `PointerEvent` / `PointerEventKind` / `PointerButton` | 指针按下、移动、释放与取消（窗口失焦） |
| `WheelEvent` | 滚轮，`DeltaY > 0` 表示向上滚动；`Mode`（`WheelDeltaMode.Steps` 默认 / `Pixels`）显式声明增量单位 |
| `WheelDeltaMode` | `Steps`：归一化步数，一格为 1（触摸板可为小数）；`Pixels`：增量已是逻辑像素。单位必须显式标记，不能按数值大小猜测 |
| `WheelScrollSettings` / `IWheelScrollSettingsService` | 系统每格行数/字符数与步数→逻辑距离换算（每行 16、每字符 8 个逻辑像素；`0` 禁用、`uint.MaxValue` 整页） |
| `KeyEvent` / `UiKey` / `KeyModifiers` / `KeyEventKind` | 控制键与修饰键；字符输入不通过键盘事件传递 |
| `TextInputEvent` | 宿主提交的 Unicode 文本；`TextInputEvent.SanitizeSingleLine(...)` 按单行规则过滤 |

## 基础类型

`Point`、`Size`、`Rect`、`Thickness`、`Color`、`Alignment`、`LayoutConstraints`、`FontFamily`、`FontDescription`、`FontWeight`、`TextAlignment`、`TextWrapping`、`TextTrimming`、`FontMetrics`、`Theme`、`EnvironmentValues`。

## 绘制与服务契约

| 类型 | 说明 |
|---|---|
| `DrawCommand`（`RectangleCommand`、`TextureCommand`、`PushClipCommand`、`PopClipCommand`） | 有序绘制命令；由布局/绘制阶段生成，渲染器按顺序消费 |
| `RenderFrame` | 一帧的命令、视口逻辑尺寸、DPI 与清屏色 |
| `ITextService` | 排版、测量、字体度量与 CPU 栅格化（实现：`CSharp.UI.Skia.SkiaTextService`）。`Rasterize(layout)` 返回白色遮罩；`Rasterize(layout, color)` 按实际文字颜色 RGB 栅格化（alpha 由绘制端 tint 统一乘一次），外部实现可只实现旧方法，颜色重载有默认实现 |
| `ITextureService` | 按稳定键缓存 GPU 纹理、有界淘汰（实现：`CSharp.UI.OpenTK` 的 OpenGL 纹理服务）。`BeginFrame()` 由运行时在每帧生成命令前调用，用于保护当前帧与上一帧引用的纹理、在帧边界回收容量 |
| `IImageService` | 图片解码与缓存（实现：`CSharp.UI.Skia.SkiaImageService`） |
| `IClipboardService` | 剪贴板读写（宿主默认使用 GLFW 剪贴板） |
| `IWheelScrollSettingsService` | 系统滚轮设置来源（Windows 宿主只读 `SPI_GETWHEELSCROLLLINES/CHARS`；默认每格 3 行/3 字符） |
| `IUiDispatcher` / `IUiClock` | 调度与时钟 |
| `UiRuntime` / `UiRuntimeServices` / `ViewNode` | 运行时：节点树、布局、输入分发与诊断；无窗口测试可直接驱动 |

## 无窗口测试替身

`CSharp.UI.Testing` 命名空间提供 `HeadlessUiDispatcher`、`StubTextService`、`RecordingTextureService`、`StubImageService`、`StubClipboardService`、`StubWheelScrollSettingsService`、`TestUiClock`。
它们不依赖窗口、字体原生库或 OpenGL 上下文，供核心布局/状态/交互测试使用。
