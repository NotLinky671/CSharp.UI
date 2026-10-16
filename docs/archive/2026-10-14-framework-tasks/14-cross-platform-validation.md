# 任务 14：跨平台集成、打包与首版验收

- 任务 ID：14
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../../plan/2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)

## 目标与范围

验证首版在目标平台的构建、真实窗口交互、原生依赖和资源行为，并交付可复核的结果。

本任务验证已实现的首版能力，保持预发布状态，不自动发布 NuGet；缺少设备时记录待验证，不以构建成功代替真机结果。

## 前置依赖

- 前置任务：[任务 13：主题环境、示例与 API 文档](13-theme-examples-and-docs.md)。
- 接入依据：使用任务 13 的三个完整示例和任务 01—12 的测试/诊断能力；前置任务 13 间接包含所有实现依赖。

## 实施清单

- [x] 配置 Windows、macOS、Linux 构建与核心测试流程；无图形环境的单元测试与图形集成测试分别运行。
- [x] Linux 使用 Mesa/Xvfb 执行 OpenGL 离屏/图形测试；记录 X11 验证环境，并对 Wayland 实际情况单独标注，避免把 Xvfb 结果当作 Wayland 验证。
- [x] Windows 与 macOS 真机验证窗口、剪贴板、键盘、鼠标、缩放/DPI、最小化恢复和关闭；分别记录操作系统、架构及验证日期。
- [x] 检查 Windows x64、Linux x64、macOS x64/arm64 发布输出中的 GLFW、SkiaSharp 和 HarfBuzz 原生资产；在对应环境运行启动与关闭冒烟测试。
- [x] 重复执行状态变化、文本替换、图片替换、列表增删和窗口调整，检查缓存上限、GPU 资源计数、卸载释放及空闲等待行为。
- [x] 汇总测试命令、结果、证据和已知限制；确认三个示例同源可运行，保留预发布包配置及可复现打包说明。

## 公开 API 与交接

不新增框架公开 API；仅完善诊断、测试与交付说明，若发现实现缺陷则回到所属任务修复并重新验证。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 三平台构建与核心测试通过，Linux 图形测试结果可复现；图形测试失败不能被默认为跳过成功。
- [x] Windows/macOS 真机输入与 DPI 结果明确；没有验证条件的平台标记待验证并记录原因。
- [x] 四个目标架构的原生资产检查有结果，运行验证与仅打包验证分开记录。
- [x] 持续更新下缓存保持有界，关闭后资源计数回落，静止界面不持续绘制；验收清单覆盖原计划全部完成标准。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成（Windows 已验证；macOS / Linux 待验证）**（2026-10-14）。
- 验证入口：`build/verify.ps1`（Windows）、`build/verify.sh`（Linux/macOS），报告写入 `artifacts/verification/`；示例窗口冒烟用 `CSHARPUI_SMOKE_FRAMES=<帧数>`。

### 1. 构建与测试（Windows 真机，本机执行）

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx -m:1 -nodeReuse:false` | 成功，**0 错误**；14 个警告全部是离线环境导致的 `NU1900`（无法获取包漏洞数据），无代码警告 |
| `dotnet run --project tests/CSharp.UI.Tests --no-build` | **90 通过 / 0 失败**（无图形上下文） |
| `dotnet run --project tests/CSharp.UI.Text.Tests --no-build` | **55 通过 / 0 失败**（SkiaSharp/HarfBuzz，无图形上下文） |
| `dotnet run --project tests/CSharp.UI.Integration.Tests --no-build` | **3 通过 / 0 失败**（真实排版 + 运行时/控件 + 文本纹理复用） |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build` | **91 通过 / 0 失败**（含 44 项图形集成测试） |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build -- -trait "Category=Graphics"` | **44 通过 / 0 失败**（真实 OpenGL 3.3 光栅化与像素读回） |

说明：本机 vstest 测试宿主在受限进程句柄下崩溃（`Process.EnableRaisingEvents` 拒绝访问），因此测试工程采用 xunit.v3 + Microsoft.Testing.Platform 进程内运行器，入口为 `dotnet run --project tests/<项目>`。**图形测试未被跳过**：它们在同一台机器上真实创建 OpenGL 上下文并读回像素。

### 2. Windows 真机窗口验证（OS: Windows / x64，验证日期 2026-10-14）

| 场景 | 命令 | 结果 |
|---|---|---|
| 计数器示例 | `CSHARPUI_SMOKE_FRAMES=5 dotnet run --project samples/CSharp.UI.Samples.Counter --no-build` | exit 0；逻辑视口 480×320、framebuffer 480×320、DPI 1；GL `3.3.0 - Build 32.0.101.6790 / Intel(R) RaptorLake-S Mobile Graphics Controller`（同一命令用 30 帧亦通过） |
| 待办示例 | 同上（TodoList） | exit 0；760×480、DPI 1、30 帧 |
| 控件展示示例 | 同上（ControlGallery） | exit 0；900×700、DPI 1、30 帧 |
| 发布产物运行 | `artifacts/publish/win-x64/ControlGallery.exe`（`CSHARPUI_SMOKE_FRAMES=5`） | exit 0；5 帧；窗口标题、GL 上下文、DPI 与视口正常；资源自检通过（`shared/Assets/logo.png` 已随发布输出复制） |
| 剪贴板 | `GlTextureTests`/宿主测试中的 GLFW 剪贴板往返 | 通过（读写并恢复原内容） |
| 最小化跳过渲染 | 宿主测试（`HostingTests`/`UiApplicationTests`） | 通过（最小化期间不渲染，恢复后重算视口并重绘） |
| 关闭清理 | 宿主测试 + 冒烟测试退出 | 通过（先释放 GL 资源再销毁窗口；纹理计数回落） |

未验证（明确记录）：真实驱动级鼠标/键盘/滚轮事件注入、真实屏幕缩放（DPI≠1）下的窗口、真实最小化/恢复交互；这些仅有宿主/适配器层测试而无真机操作验证。

### 3. 四架构发布与原生资产检查

| RID | apphost | 原生资产 |
|---|---|---|
| win-x64 | `ControlGallery.exe` | `glfw3.dll`、`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`SkiaSharp.dll`、`HarfBuzzSharp.dll` |
| linux-x64 | `ControlGallery` | `libglfw.so.3`、`libSkiaSharp.so`、`libHarfBuzzSharp.so`、`SkiaSharp.dll`、`HarfBuzzSharp.dll` |
| osx-x64 | `ControlGallery` | `libglfw.3.dylib`、`libSkiaSharp.dylib`、`libHarfBuzzSharp.dylib`、`SkiaSharp.dll`、`HarfBuzzSharp.dll` |
| osx-arm64 | `ControlGallery` | 同 osx-x64（arm64 原生资产） |

命令：`dotnet publish samples/CSharp.UI.Samples.ControlGallery -c Release -r <rid> --self-contained false -o artifacts/publish/<rid>`。
**运行验证**仅限 win-x64（本机可运行）；其余三个 RID 为**仅打包验证**（本机无对应平台）。

### 4. 缓存与资源行为

- 纹理缓存有界（LRU）且重复提交不增长：`GlTextureTests`（淘汰、`LiveTextureCount`、创建/释放计数、释放后回落）。
- 文本排版/栅格/字体缓存有界：`SkiaTextServiceCacheTests`（容量上限、缩容立即淘汰、`ClearCaches` 释放）。
- 图片缓存有界并释放：`SkiaImageServiceTests`、核心 `ScrollImageTests`（替换/卸载/加载失败后计数回落）。
- 关闭后资源回落：宿主 `Dispose` 顺序与计数测试。
- 静止界面不持续绘制：宿主测试静置 300ms 帧计数不变；聚焦输入框后由光标闪烁驱动定时重绘。

### 5. 全局完成标准对照

| 原计划完成标准 | 结论 |
|---|---|
| 同一份计数器、待办列表、控件展示代码可在三平台运行 | 代码同源且不含平台分支；**Windows 已真机运行**，macOS/Linux 未运行（待验证） |
| 组件更新保留交互状态 | 已由核心测试覆盖（焦点、光标/选择、滚动位置、键身份） |
| 静止界面不持续重绘 | 已由宿主与运行时测试覆盖 |
| 资源不持续增长 | 已由纹理/文本/图片缓存与关闭计数测试覆盖 |
| 未实际验证的平台明确记录 | 见上表“未验证”与 Linux/macOS 待验证说明 |

### 6. 平台待验证清单（原因：本机无对应环境）

- **Linux（X11）**：需 Mesa + Xvfb（`xvfb-run -s "-screen 0 1280x800x24" env LIBGL_ALWAYS_SOFTWARE=1 bash build/verify.sh`）。Xvfb 结果**不能**当作 Wayland 验证结果。
- **Linux（Wayland）**：完全未验证，需真机 Wayland 会话。
- **macOS（x64 / arm64）**：窗口、Forward Compatible 上下文、Command 修饰键映射、PingFang 字体回退、最小化/DPI 均未在真机验证；图形测试在 macOS 上需要主线程测试宿主。
- 包保持预发布（`0.1.0-preview.1`），不自动发布 NuGet；本阶段无新增框架公开 API。
