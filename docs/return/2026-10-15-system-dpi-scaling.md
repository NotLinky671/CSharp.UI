# R13：系统 150% 缩放没有应用到 UI

记录日期：2026-10-15  
审核人：Codex  
状态：已实机复现，待 DeepSeek Harness 修复与 Codex 复审。  
执行模型：`opencode-go/deepseek-flash`。

## 问题与证据

用户的 Windows 笔记本使用 150% 系统缩放，但界面仍按 100% 显示。

当前 [OpenTkHost.cs](../../src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs) 将 `ClientSize` 当作 UI 逻辑尺寸，用 `FramebufferSize / ClientSize` 推导 `PixelScale`。Windows/GLFW 的窗口内容区坐标和 framebuffer 通常都是物理像素，因此该比值不能表示用户设置的 DPI 缩放。

Codex 使用隐藏窗口在本机实际读取（未更改系统设置）：

```text
rawClient=(400, 300)
framebuffer=(400, 300)
monitorScale=1.5/1.5
runtimeLogical=400×300
runtimeScale=1
```

复现程序：本地忽略目录 `artifacts/dpi-review/Probe.csproj`，程序集名为 `CSharp.UI.OpenTK.Tests`，仅用于读取宿主内部窗口状态。正常桌面环境构建运行即可；Codex 默认沙箱的 NuGet 缓存/网络不同，首次还原曾失败，使用已有用户缓存后成功。不要把沙箱还原失败归因为框架缺陷。

## 修复目标与坐标约定

- 默认自动跟随系统缩放，不要求示例或应用手动设置 1.5，也不修改系统显示设置。
- 框架尺寸、布局、字体大小、光标和输入事件使用逻辑像素；初始 `UiApplicationOptions.Width/Height` 继续表示逻辑尺寸。
- 以窗口当前内容缩放作为 UI 缩放来源，优先使用 GLFW 的 window content scale；不能仅以 framebuffer/window 比值推断 DPI，也不能只读取一次主屏缩放。
- 正确区分三个空间：GLFW 窗口内容区坐标、UI 逻辑坐标、framebuffer 像素。设 UI 缩放为 D、framebuffer/window 比为 R，则逻辑视口为 framebuffer/D，鼠标窗口坐标转逻辑坐标为窗口坐标×R/D。macOS Retina 的 R=2、D=2 时鼠标坐标保持原值；Windows 150% 的 R=1、D=1.5 时鼠标坐标除以 1.5。不可把 Retina 再放大一遍。
- 在本机 150% 下，配置 400×300 逻辑窗口应得到约 600×450 的 Windows 客户区/framebuffer，运行时仍为约 400×300、PixelScale=1.5；100 逻辑像素控件占约 150 物理像素。
- 原始窗口内容缩放不可用/为零/非有限数时，采用明确、可测试的有效回退，不能将无效值传到布局和纹理服务。

## 必须接入的行为

1. **窗口创建**：处理初始系统缩放与后续监视器变化。可使用 GLFW `ScaleToMonitor` 提示处理 Windows/X11 的初始与跨 DPI 缩放，但必须确认 OpenTK 4.9.4 的实际调用顺序，不要叠加两次尺寸缩放或污染离屏测试窗口的全局 hint。
2. **动态 DPI**：系统缩放变化或跨不同 DPI 显示器移动时更新内容比例、逻辑视口、输入变换，失效文本/纹理缓存并重绘。可用 GLFW content-scale callback；委托须保持存活、正确注销，不能覆盖已有必要回调。移动/恢复等实际事件也需正确处理；不通过持续逐帧轮询或忙等规避事件接入。
3. **输入**：指针移动、按下/抬起、拖选、Slider 拖动、滚轮事件的位置都使用一致的逻辑坐标；滚轮增量不作为鼠标位置缩放。DPI 改变后已缓存的指针位置也必须保持一致。
4. **渲染**：文字按正确 DPI 栅格化，矩形/边框/裁剪正常缩放；framebuffer 视口使用实际像素范围，处理小数比例与尺寸取整，不能靠系统位图拉伸产生模糊 UI。
5. **回归**：保留按需事件循环、最小化恢复、R01—R12 修复及跨平台设计。已有窗口测试若把原始 `ClientSize` 错当逻辑尺寸，应修正其断言语义，不降低测试要求。
6. **示例**：三个 `samples` 的 Debug 和 Release 都重编译、验证产物包含新 DLL，执行启动冒烟。不要重复出现仅更新 Debug、用户仍运行旧 Release 的情况。

## 测试与验收

- [ ] 纯计算测试覆盖 Windows 100% / 125% / 150% / 200%（R=1），Retina（R=2、D=2）及 GLFW 屏幕坐标与 framebuffer 不同的情形。
- [ ] 150% 初始逻辑尺寸和 framebuffer 像素一致，400×300 逻辑尺寸约对应 Windows 600×450 像素，不漂移或重复放大。
- [ ] 缩放后的 Button 命中、TextField 光标/选区定位、Slider 拖动及滚轮位置正确；至少用事件适配器或宿主路径验证，不只测试矩阵公式。
- [ ] DPI 从 1→1.5→2→1 变化时，布局、输入转换和缓存失效一致；零尺寸/最小化状态不会产生无效值或死循环。
- [ ] 当前本机 150% 隐藏/可见宿主能读到 PixelScale=1.5，三个示例冒烟输出相应的逻辑视口与物理 framebuffer。
- [ ] 四个测试工程运行并记录真实结果；不跳过或弱化图形、输入、剪贴板断言。
- [ ] macOS/Linux 如无真机条件，区分纯计算覆盖与实际验证，不宣称实机已通过。

## 实现依据

- [GLFW 3.4 窗口内容缩放及 ScaleToMonitor/ScaleFramebuffer](https://www.glfw.org/docs/3.4/window_guide.html#window_scale)。
- [OpenTK 4.9.4 NativeWindow 源码](https://github.com/opentk/opentk/blob/4.9.4/src/OpenTK.Windowing.Desktop/NativeWindow.cs)。该版本没有现成的 NativeWindow content-scale 事件包装，应核对原生回调和已安装 API。

## 交付

遵循 AGENTS.md：DeepSeek Harness 实现、单元测试和必要的本地英文提交，Codex 复审。把实际测试、150% 实机观察、未验证项和提交情况追加本文。

只提交本次 DPI 修复所需文件与本文；不要暂存、还原或提交既有 ACP 辅助工具、`docs/agents-notebook/README.md`、上一份 R12 记录的 Codex 复审改动。不要 `git add .`、不要推送、不改其他项目或系统显示配置。

## 中途复审补充：初始化回退路径

Codex 检查进行中的修改时发现：构造函数在 `_context`、`_runtime` 和 `_input` 完成创建之前调用 `EnsureInitialLogicalSize()`，它又复用 `ApplyLogicalClientSize()`。如果初始客户区需要实际调整，该方法会立即调用 `UpdateViewport()`，从而访问尚未初始化的服务。窗口已经按 DPI 正确创建时会提前返回，不能用这一路径的成功代替对回退分支的验证。

请在提交前确保初始调整仅设置窗口尺寸，完整服务创建后才刷新运行时/上下文/输入换算；动态 DPI 调整仍需立即同步。补充一个会实际进入初始尺寸调整分支的回归用例，避免平台忽略 hint 或创建期间 DPI 变化时出现空引用异常。

---

## R13 实施与验证记录（DeepSeek Harness 追加）

执行模型：`opencode-go/deepseek-flash`，模型已按要求复核。以下均为本机实际执行过的命令与观察结果。

### 实现要点

- 新增 `src/CSharp.UI.OpenTK/Hosting/ViewportMetrics.cs`（纯计算）。设 D = `glfwGetWindowContentScale`（窗口当前内容缩放）、
  R = `FramebufferSize / ClientSize`，则：
  - 逻辑视口 = framebuffer ÷ D；指针换算 = R ÷ D（窗口坐标 → 逻辑坐标）；
  - `WindowSizeForLogicalSize`：逻辑尺寸 × D ÷ R，向上取整，用于反推客户区。
  - 逻辑尺寸**保留小数不取整**，并保证 `Ceiling(逻辑 × D) == 真实 framebuffer`（浮点误差时按最小步长向下修正），
    因此渲染器 `glViewport` 与真实 framebuffer 逐像素一致；535×401 与 D=1.5 这类不可整除尺寸不会再多出 1 像素。
  - 回退顺序：有效的内容缩放轴（X 优先、其次 Y）→ framebuffer÷窗口比；零/负数/NaN/∞ 不进入布局与纹理服务。
- `GlWindowFactory`：桌面宿主窗口创建时启用 GLFW `ScaleToMonitor`（创建后立即恢复 `false`），离屏/测试窗口显式 `false`，不泄漏全局 hint；
  新增 `GetWindowContentScale`。OpenTK 4.9.4 的 `NativeWindowSettings` 无该属性，也不调用 `DefaultWindowHints`，实测该 hint 在创建时即生效。
- `OpenTkHost`：`UpdateViewport` 改由 `ViewportMetrics` 计算逻辑尺寸/DPI/指针换算；窗口创建后读取实际 D/R 反推客户区
  （已正确时不改动，避免初始二次放大）；挂接 GLFW 窗口内容缩放回调（保留先前回调并链式调用、退出时注销）；
  DPI 变化时刷新视口、失效文本/纹理缓存、重绘，并把客户区调整到仍覆盖原逻辑尺寸（窗口管理器已调整过则不重复放大）；不逐帧轮询。
- `OpenTkInputAdapter`：只缓存原始窗口坐标，逻辑坐标在使用时按 R/D 换算；DPI 变化后缓存位置按新比例重新解释；滚轮增量不缩放。
- 指南：`docs/guide/platform-notes.md` 新增“DPI 与三个坐标空间”。

### 对中途复审（初始化回退路径）的处理

构造顺序已调整为：`_runtime`、`_renderer`、`_input`、`_context` 全部创建完成后才调用 `EnsureInitialLogicalSize()`（→ `ApplyLogicalClientSize`），
因此初始尺寸真正需要调整时，随后的 `UpdateViewport` 不会再访问未初始化的服务；动态 DPI 路径仍立即同步。
新增回归用例 `初始尺寸回退分支会调整客户区并刷新视口`：在 `Started` 中把客户区强改成 200×100（模拟平台忽略 hint 或被窗口管理器钳制），
再走与构造函数相同的初始调整分支，断言确实进入调整分支、逻辑尺寸回到 400×300、`framebuffer == Ceiling(逻辑 × D)` 且未抛异常。

### 本机 150% 实机观察

隐藏宿主探针（`artifacts/dpi-review`，本地忽略目录，未提交）：

```text
scaleToMonitor=false: client=(400, 300) framebuffer=(400, 300) contentScale=(1.5, 1.5)
scaleToMonitor=true : client=(600, 450) framebuffer=(600, 450) contentScale=(1.5, 1.5)
host: rawClient=(600, 450) framebuffer=(600, 450) contentScale=1.5/1.5 monitorScale=1.5/1.5 runtimeLogical=400×300 runtimeScale=1.5 rendererFramebuffer=600×450
simulated200: logical=400×300 client=800×600 scale=2 (startLogical=400×300, startClient=600×450)
simulated200(repeat): client=800×600 logical=400×300
restored: client=600×450 logical=400×300 scale=1.5
```

即：400×300 逻辑窗口得到 600×450 客户区/framebuffer、`PixelScale=1.5`、逻辑视口 400×300、渲染器 framebuffer 600×450（逐像素一致）；
`ScaleToMonitor` 在创建时即生效（无初始二次放大）；动态 DPI 变更后逻辑尺寸保持不变，重复通知不重复放大，恢复后回到初始状态。

### 测试与构建

- `dotnet build CSharp.UI.slnx -m:1 -nodeReuse:false --no-restore`：成功，0 错误；仅有离线 `NU1900` 环境警告，无代码警告。
- 四个测试工程（`dotnet run --project tests/<工程> --no-build`，Debug，串行，全部通过）：

| 测试工程 | Total | Failed | Skipped |
|---|---|---|---|
| CSharp.UI.Tests | 138 | 0 | 0 |
| CSharp.UI.Text.Tests | 58 | 0 | 0 |
| CSharp.UI.Integration.Tests | 13 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 137 | 0 | 0 |

  `CSharp.UI.OpenTK.Tests` 含图形（离屏 framebuffer）、宿主窗口、输入与 GLFW 剪贴板往返测试；本机剪贴板可访问，往返断言通过（未跳过）。
- 新增/调整的 DPI 测试覆盖：100/125/150/200%（R=1）、Retina（R=2、D=2）、R=2 且 D=1、
  535×401 / 533×400 / 601×334 / 701×497 / 999×667 等不可整除尺寸的“逻辑视口 → framebuffer 逐像素一致”与输入换算一致性、
  无效缩放按轴回退（含 X 无效 Y 有效）、零窗口/零 framebuffer 不产生无效值；150% 下按钮命中、Slider 拖动、TextField 光标与选区、
  滚轮位置与增量、DPI 变化后缓存指针重新解释；宿主初始逻辑尺寸、真实内容缩放来源、动态 DPI 保持逻辑尺寸且不重复放大、
  初始化回退分支。
- 三个示例 Debug 与 Release 各重新构建成功（0 错误）；六个输出目录的 `CSharp.UI.OpenTK.dll` SHA-256 与对应配置的框架构建产物完全一致：
  Debug `232A55242587976672ED3F9D77C3AFDCFFD61C92369C4FDD623B846948949F51`、
  Release `E82AFC773AD2460E8D4B1698EE27AABA453CD3A6BF9517CE18749004A7769C0B`（确认 Release 也在跑新 DLL）。
- 三个示例 Release 以 `CSHARPUI_SMOKE_FRAMES=5` 启动冒烟（退出码 0）：

| 示例 | 逻辑视口 | framebuffer | DPI | 退出码 |
|---|---|---|---|---|
| Counter | 480×320 | 720×480 | 1.5 | 0 |
| TodoList | 760×480 | 1140×720 | 1.5 | 0 |
| ControlGallery | 900×700 | 1350×1050 | 1.5 | 0 |

### 未验证项

- macOS / Linux 无真机：Retina 与 Wayland/X11 仅由纯计算与代码路径覆盖，不宣称实机通过。
- Windows 125% / 200% 与真实跨显示器 DPI 变更未在本机触发（不修改系统显示设置）：动态路径以与 GLFW 回调完全相同的刷新路径模拟验证；
  真实系统设置变化由 Codex 独立复核（`artifacts/dpi-verify`）。
- 150% 下鼠标交互通过事件适配器与命中测试验证，非人工实际拖动。

### 提交

本次 R13 修复、测试、指南与本文合并为一次本地英文提交，未推送；未暂存、未修改 Codex 拥有的
`docs/agents-notebook/README.md`、`docs/agents-notebook/acp/` 与 `docs/return/2026-10-15-textfield-caret-alignment.md`。
提交 SHA 见 DeepSeek Harness 的交付报告与 `git log -1 --stat`。

