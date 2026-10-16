# 平台、字体与原生依赖

## 三平台前提

| 平台 | 架构 | 前提 |
|---|---|---|
| Windows | x64 | 桌面会话 + 支持 OpenGL 3.3 的驱动（本项目在 Windows 上完成真机验证，见下） |
| Linux | x64 | X11 或 Wayland 会话 + Mesa（OpenGL 3.3）；无显示器时用 Xvfb + `LIBGL_ALWAYS_SOFTWARE=1` |
| macOS | x64 / arm64 | 10.15+，创建 Forward Compatible 的 OpenGL 3.3 Core 上下文 |

渲染基线为 OpenGL 3.3 Core / GLSL 330，只使用三平台共有能力；macOS 上以 `ContextFlags.ForwardCompatible` 创建上下文。

## 原生依赖

| 依赖 | 来源 | 用途 |
|---|---|---|
| GLFW | `OpenTK 4.9.4`（`runtimes/<rid>/native/glfw3.dll` / `libglfw.so.3` / `libglfw.3.dylib`） | 窗口、输入、剪贴板、事件循环 |
| libSkiaSharp | `SkiaSharp 3.119.4` + `SkiaSharp.NativeAssets.*` | CPU 文本栅格化、图片解码 |
| libHarfBuzzSharp | `HarfBuzzSharp 8.3.1.5` + `HarfBuzzSharp.NativeAssets.*` | 文本 shaping |

发布检查覆盖 Windows x64、Linux x64、macOS x64/arm64 四种架构（命令与结果见 [../task/14-cross-platform-validation.md](../task/14-cross-platform-validation.md)）。
核心库 `CSharp.UI` **不引用** OpenTK、SkiaSharp 或任何原生库，因此核心单元测试不需要图形上下文。

## DPI 与三个坐标空间

宿主严格区分三个空间，框架内部的尺寸、布局、字体大小、光标与输入事件全部使用**逻辑像素**：

| 空间 | 来源 | 说明 |
|---|---|---|
| GLFW 窗口内容区坐标 | `window.ClientSize` | Windows/X11 与物理像素 1:1；macOS 为点（point） |
| UI 逻辑像素 | `UiHostContext.LogicalSize`、布局与命中测试 | = framebuffer ÷ 内容缩放 D |
| framebuffer 像素 | `window.FramebufferSize` | = 逻辑尺寸 × D（渲染器据此设置 `glViewport`） |

- `UiApplicationOptions.Width/Height` 是**逻辑尺寸**：宿主在创建窗口时启用 GLFW `ScaleToMonitor`，
  再按实际内容缩放反推客户区，因此 Windows 150% 下 400×300 逻辑会得到约 600×450 物理客户区，
  运行时仍是 400×300、`PixelScale` 为 1.5；macOS Retina（窗口坐标 = 点、framebuffer = 2×）不会二次放大。
- 鼠标：逻辑坐标 = 窗口坐标 × R ÷ D（R = framebuffer ÷ 窗口内容区）。Windows 150% 时除以 1.5，
  macOS Retina 时保持原值；滚轮增量是行/格语义，不随 DPI 缩放（见下节）。
- 系统缩放变化或跨屏移动由 GLFW 窗口内容缩放回调处理：更新 D、逻辑视口与输入换算，失效文本/纹理缓存并重绘，
  并把客户区调整到仍覆盖原逻辑尺寸；窗口管理器已按新 DPI 调整过时不会重复放大。不逐帧轮询。
- 逻辑尺寸保留小数（不取整），保证渲染器由逻辑视口推导的 framebuffer 与真实 framebuffer 逐像素一致。
- 原始内容缩放不可用（零/非有限数）时按轴回退，最后才用 framebuffer÷窗口比，不把无效值传给布局与纹理服务。
- 交互调整大小与窗口刷新（Windows 的窗口过程在拖拽边框期间进入 modal loop，不保证 `PollEvents`/`WaitEvents` 及时返回）
  由宿主的 `Resize`/`FramebufferResize`/`Refresh` 回调处理：查询真实 GLFW 窗口与 framebuffer 尺寸，更新逻辑视口、
  DPI 与输入换算，判断是否需要重新布局，然后在回调返回前清屏并提交/交换一帧；静止无事件时仍按需重绘，不忙等。
  回调期间正在构造/未启动、最小化或零尺寸、已请求退出、正在重建/绘制，以及用户 `FrameRendered` 回调内再次触发
  尺寸/刷新事件时都会跳过即时提交并保留后续重绘请求；清屏不受上一帧 scissor 限制。

## 滚轮单位与系统滚动设置

滚轮增量是**归一化步数**（GLFW 在 Windows 上把原生 `WM_MOUSEWHEEL` 的 120 除以 `WHEEL_DELTA`，普通一格为 ±1），
不是逻辑像素，也不随 DPI 缩放。`WheelEvent.Mode` 显式声明单位，默认 `WheelDeltaMode.Steps`；
只有明确知道增量是逻辑像素时才用 `WheelDeltaMode.Pixels`，命令式不按增量大小猜测单位。

- `ScrollView` 把归一化步数换算为逻辑像素：每行 16、每字符 8 个逻辑像素，单个滚轮步的距离按对应视口大小封顶，
  再乘实际（可含小数的）步数与 `ScrollView.WheelSensitivity`（有限非负，默认 1），最后夹紧内容边界。
  距离是逻辑像素，因此 DPI 100%/150%/200% 下同一格滚动的逻辑距离一致。
- 步长来自平台无关服务 `IWheelScrollSettingsService`：Windows 宿主只读查询 `SPI_GETWHEELSCROLLLINES` /
  `SPI_GETWHEELSCROLLCHARS`，**不修改任何系统设置**；设置值 0 表示该方向禁用滚轮，`uint.MaxValue` 表示每格一页，
  查询失败或非 Windows 回退到每格 3 行/3 字符。核心不引用任何 Windows 或窗口类型。

## 字体与回退

字体解析顺序：

1. `FontFamily.FromFile(path)`：应用指定的字体文件（文件不存在或无效时给出带路径的诊断并回退）；
2. `FontFamily.FromName(name)`：系统字体家族名；
3. `FontFamily.Default`：平台默认字体；
4. 缺字回退：`SKFontManager.MatchCharacter`，保证中文可显示。

`FontFamily.WithFallbacks(...)` 可按顺序追加备选家族。

> **字体许可**：本仓库**不重新分发第三方字体二进制文件**。示例使用系统字体与其回退（Windows: Microsoft YaHei / Segoe UI；macOS: PingFang SC / Helvetica；Linux: Noto Sans CJK）。
> 需要跨机器可复现的截图时，请自行把许可允许分发的字体放到应用资源目录，并用 `FontFamily.FromFile` 指定。

## 示例资源

`samples/Shared/Assets/logo.png` 由仓库自带的工具绘制生成（不使用任何第三方素材，可随仓库分发）：

```bash
dotnet run --project tools/CSharp.UI.AssetGenerator
```

## 文本输入的实际边界

首版支持：

- 宿主提交的 Unicode 文本（例如中文输入法上屏后的结果）与剪贴板粘贴（含中文）；
- 按字素边界的光标移动、删除、选择、剪切/复制/粘贴与横向滚动；
- 光标闪烁（仅在获得焦点时定时重绘）。

首版**不包含**：

- 完整的 IME 组合文本、候选窗定位与预编辑高亮（输入法组合期间的中间状态不保证）；
- 多行编辑、富文本、彩色 Emoji 字形图集、完整双向文本排版；
- 系统无障碍桥接、动画系统、列表虚拟化、移动端与浏览器目标。

因此“支持中文显示与粘贴”不等于“完整 IME 支持”。

## 已知限制

- 单窗口：同一进程只允许一个 `UiApplication.Run`。
- `Opacity` 是逐图元相乘，不做离屏分组合成；子项重叠时与整体透明度的视觉效果略有差异。
- `ScrollView` 首版不绘制滚动条，也不做虚拟化，内容全部布局。
- `Rectangle` 圆角为矩形圆角，不支持每角独立半径。
- 图形验证受运行环境影响：缺少 OpenGL 上下文的机器上，图形集成测试会真实失败而不是被跳过。
