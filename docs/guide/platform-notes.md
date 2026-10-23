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

发布检查覆盖 Windows x64、Linux x64、macOS x64/arm64 四种架构（命令与结果见 [../task/14-cross-platform-validation.md](../archive/2026-10-14-framework-tasks/14-cross-platform-validation.md)）。
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

默认发布（Windows 默认仍走 IMM）支持：

- 宿主提交的 Unicode 文本（例如中文输入法上屏后的结果）与剪贴板粘贴（含中文）；
- 按字素边界的光标移动、删除、选择、剪切/复制/粘贴与横向滚动；
- 光标闪烁（仅在获得焦点时定时重绘）。

Windows TSF 预发布路径（**内部开关** `UseTsfInputBridge`，不是公开 API/默认路径；TI-07 已于 2026-10-22 完成 P1–P4 验收并经主代理批准，边界见下）：

- 入口必须声明 `[STAThread]`；TSF 会话只在该线程建立，MTA 会明确失败并回退 IMM；
- 支持组合/预编辑与 UI-less 自绘候选通路；候选由框架在内容之后、同一 GL 帧内绘制；真实微软拼音下几何跟随/完全裁出隐藏与恢复、横向滚动、失焦/失活/最小化/隐藏取消零写及恢复后唯一提交已经 P4 真实验收（150% 单 DPI 环境，见[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)）；DPI 变化与多显示器矩阵未覆盖，属环境边界；
- 键盘选词/翻页由输入法自身处理；**不支持鼠标点选候选**；
- 激活、flags 或 UIElement sink 任一不可用时整个 TSF 会话失败并回退 IMM，不并行启用两个写入来源；
- 本路径仍为预发布内部开关，不宣称“完整 IME 支持”。

### 能力查询与平台分级（TI-08）

应用可经公开路径查询当前文本输入能力：

```csharp
var capabilities = TextInputForwarder.EffectiveService(
    context.Runtime.Services.TextInputService).Capabilities;
```

`TextInputCapabilities` 分三项声明：已提交文本（`CommittedText`）、组合/预编辑（`Composition`）、候选锚点（`CandidateAnchor`）；`Reason` 给出当前平台/后端的实际限制文案。服务为 `null` 时归一化为 `NoTextInputService`（仅已提交文本）。能力不随焦点变化：组合/锚点不支持与“暂时无焦点”（`ActiveClient==null`）是两种不同状态，前者按平台静态声明，后者不影响 `Capabilities`。

| 平台 | 已提交文本 | 组合/预编辑 | 候选锚点 | 验证级别 |
|---|---|---|---|---|
| Windows（默认 IMM） | ✅ 真机 | ⚠️ 历史真机：TI-01 WeType 真实预编辑/单次结果（`docs/agents-notebook/2026-10-17-ime-platform-decision.md` §3）；本轮微软拼音正式验收走 TSF opt-in（下行），IMM 组合未在本轮按 mspinyin 复验 | ⚠️ `ImmSetCandidateWindow`/`ImmGetCandidateWindow` API 往返已验；系统候选窗实际屏幕位置未观测 | 真机（含历史证据标注） |
| Windows（`UseTsfInputBridge` 内部开关） | ✅ 真机 | ✅ 真机（微软拼音 P4 验收，[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)） | ✅ UI-less 自绘候选（P4 真实验收，150% 单 DPI） | 真机 |
| macOS | ✅ GLFW char 回调（源码级判读：GLFW `NSTextInputClient` committed 字符通道；本仓库 shared managed code 编译覆盖，osx-x64/arm64 运行时未验） | ❌ 预编辑在 GLFW 视图内部消费，公共 API 无暴露；禁止 delegate 覆盖/method swizzle | ❌ 未接入 | 源码级；macOS 真机未验证 |
| Linux/X11 | ✅ GLFW XIM → char 回调（UTF-8；源码级判读 + 二进制佐证） | ❌ XIM 预编辑在 GLFW 内部处理，未暴露 | ❌ 未接入 | 源码/二进制级 + WSL Linux 程序集加载 smoke（非 IME 路径，无 GLFW 窗口）；真机未验证 |
| Linux/Wayland | ✅ GLFW `wl_keyboard` → libxkbcommon/xkb compose → char 回调（源码级判读 + 二进制佐证：`libglfw.so.3` 含 xkb 动态加载符号） | ❌ GLFW 3.4 Wayland 后端无 `zwp_text_input_v3`/text-input 协议（源码 + 二进制 0 命中） | ❌ 未接入 | 源码/二进制级 + WSL Linux 程序集加载 smoke（非 IME 路径，无 GLFW 窗口）；真机未验证 |

说明：Wayland 行“无 text-input 协议”只否定 **IME 协议级**组合与候选；普通键盘字符经 xkb 键映射/compose 仍可达 char 回调，因此 `CommittedText` 为 true。二进制符号存在/缺失是佐证，不单独证明运行通路。

非 Windows 宿主注入的平台服务是 `NoTextInputService` 的分级实例：能力为 committed-only（`Composition`/`CandidateAnchor` 恒 false），`Reason` 按 `GLFW.GetPlatform()` 真实值区分 macOS / X11 / Wayland / 未知。隔离的真实边界是“不加载/调用 Windows 原生库”：Windows 原生桥（`WindowsImeBridge`、`WindowsTextInputRouter`、TSF interop 类型）只在 `OperatingSystem.IsWindows()` 分支内实例化；WindowsTsf 命名空间中的纯托管类型（如输入交付协调器）仍可能被引用，不能从命名空间推出隔离，隔离判据为 OS 防护分支 + 真实运行未触发 Windows P/Invoke（当前为源码级判读，非 Windows 真机加载路径 UNVERIFIED）。

默认发布**不包含**：

- 把自绘候选或 TSF 路径作为公开/默认选项；
- 鼠标候选选择、富文本、彩色 Emoji 字形图集、完整双向文本排版；
- 系统无障碍桥接、动画系统、列表虚拟化、移动端与浏览器目标。

### 多行编辑（TextEditor）边界

- `TextEditor` 已实现多行编辑核心并完成 Windows 微软拼音/IMM 真机验收（2026-10-23,组合/取消/跨行替换/锚点随 `TextOrigin−ScrollY` 重发——见[交付记录](../delivery/2026-10-23-text-editor-delivery.md)）。IMM 默认系统候选路径,`ImmSetCandidateWindow` 锚点已验。
- 非 Windows 平台维持 committed-only：组合/预编辑/候选锚点声明 false，`TextEditor` 同样只收已提交 Unicode 文本。
- `UseTsfInputBridge` 仍是内部开关（见上节 TSF 段落）；`TextEditor` 组合接入不改变其公开状态。
- 指针拖选越过 **OS 窗口边缘**时宿主按"捕获丢失即停"发 `PointerCancel`（`HandleMouseLeave`），拖选立即结束而非继续自滚；在窗口内悬停于编辑器内容区外的边缘自滚仍工作。

因此“支持中文显示与粘贴”不等于“完整 IME 支持”。

## 已知限制

- 单窗口：同一进程只允许一个 `UiApplication.Run`。
- `Opacity` 是逐图元相乘，不做离屏分组合成；子项重叠时与整体透明度的视觉效果略有差异。
- `ScrollView` 首版不绘制滚动条，也不做虚拟化，内容全部布局。
- `Rectangle` 圆角为矩形圆角，不支持每角独立半径。
- 图形验证受运行环境影响：缺少 OpenGL 上下文的机器上，图形集成测试会真实失败而不是被跳过。
