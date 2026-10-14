# 任务 06：OpenTK 桌面宿主与按需事件循环

- 任务 ID：06
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)

## 目标与范围

提供单窗口应用入口，统一处理图形上下文、主线程调度、尺寸/DPI 变化和按需重绘。

交付宿主及后端服务接入；本任务可用清屏或测试渲染器验证，完整绘制由任务 07 实现。

## 前置依赖

- 前置任务：[任务 02：平台无关基础类型与服务契约](02-core-contracts.md)、[任务 04：状态、双向绑定与更新调度](04-state-and-binding.md)。
- 接入依据：使用任务 02 的平台服务契约、任务 04 的 UI 调度队列与重建请求。

## 实施清单

- [x] 使用 OpenTK 4.9.4 NativeWindow，实现 UiApplication.Run(root, options) 及窗口标题、初始逻辑尺寸等必要配置。
- [x] 创建 OpenGL 3.3 Core 上下文，macOS 使用 Forward Compatible；初始化失败输出清楚的窗口/上下文诊断并清理已创建资源。
- [x] 窗口事件、组件更新和 GL 操作在主线程执行；把 Dispatcher 队列、重绘请求与系统事件接入同一循环。
- [x] 空闲时等待事件；后台 Post 可唤醒等待；需要光标闪烁时支持定时唤醒，最小化时跳过绘制。
- [x] 区分窗口逻辑尺寸、framebuffer 尺寸和 DPI；窗口大小、DPI 或系统重绘事件触发布局/缓存失效及视口更新。
- [x] 实现可替换的渲染/文本服务组装、剪贴板服务和关闭顺序；销毁上下文前释放需 GL 的资源，再释放窗口。

## 公开 API 与交接

交付 UiApplication.Run(root, options) 和窗口配置类型；宿主实现核心平台服务，不让应用直接控制 NativeWindow 生命周期。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 单窗口可创建、调整大小和关闭，启动与退出期间 GL 操作均有正确上下文。
- [x] 界面空闲时等待事件，不持续重建或绘制；后台 Post 能唤醒并在主线程执行。
- [x] 最小化期间不渲染，恢复后正确重绘；逻辑尺寸与 framebuffer 尺寸不混用。
- [x] 初始化失败和正常关闭都能完成清理；DPI 改变可通知布局、文本缓存和渲染器。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成**（2026-10-14）。
- 交付（`src/CSharp.UI.OpenTK`，命名空间 `CSharp.UI` 与 `CSharp.UI.OpenTK`）：
  - `UiApplication` / `UiApplicationOptions` / `UiHostContext`：`Run(View root, UiApplicationOptions?)`、窗口标题与初始逻辑尺寸、主题、默认字体、可替换的文本/图片/剪贴板服务、纹理缓存容量、退出快捷键与 `Started`/`FrameRendered` 观测钩子。
  - `Hosting/OpenTkHost.cs`：窗口与上下文生命周期、按需事件循环、DPI/最小化处理、服务组装与关闭顺序。
  - `Hosting/GlWindowFactory.cs`：OpenGL 3.3 Core（macOS 使用 Forward Compatible）与失败诊断（`UiHostException` 内含平台/GLFW/驱动信息）。
  - `Hosting/OpenTkUiDispatcher.cs`：跨线程 `Post` + `GLFW.PostEmptyEvent` 唤醒、重绘请求、最早定时唤醒。
  - `Hosting/OpenTkInputAdapter.cs` / `OpenTkInputMapping.cs`：指针/滚轮/键盘/已提交文本 → 核心事件，键码与修饰键映射（macOS Command → Meta）。
  - `Hosting/GlfwClipboardService.cs`：GLFW 实现的 `IClipboardService`。
- 关键行为：
  - 空闲时阻塞在 `GLFW.WaitEvents()`；后台 `Post` 用 `GLFW.PostEmptyEvent` 打断等待；定时唤醒用 `GLFW.WaitEventsTimeout`，超时由 `UiRuntime.GetNextWakeup()`（光标闪烁）与调度器请求共同决定。
  - 每轮循环调用 `UiRuntime.OnClockTick()`：仅在焦点视图声明需要定时重绘时请求重绘，静止界面不持续绘制。
  - 逻辑尺寸取 `ClientSize`、framebuffer 取 `FramebufferSize`，两者之比作为 DPI；变化时 `SetViewport` 触发布局失效，DPI 变化同时清空文本与纹理缓存；最小化时跳过渲染，恢复后强制重算视口。
  - 关闭顺序：渲染器 → 运行时 → 纹理服务 → 宿主自行创建的 Skia 文本/图片服务 → 窗口；应用注入的服务由应用自行释放（有测试覆盖）。
- 实际执行：
  - `dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build` → **91 通过 / 0 失败**（其中图形 44 项）。
  - 示例窗口冒烟（真实可见窗口 + GL 上下文）：
    - `CSHARPUI_SMOKE_FRAMES=30 dotnet run --project samples/CSharp.UI.Samples.Counter --no-build` → exit 0，逻辑视口 480×320、framebuffer 480×320、DPI 1、GL `3.3.0 - Build 32.0.101.6790 / Intel(R) RaptorLake-S Mobile Graphics Controller`。
    - `TodoList` → 760×480、30 帧、exit 0；`ControlGallery` → 900×700、30 帧、exit 0；发布产物 `artifacts/publish/win-x64/ControlGallery.exe` → 20 帧、exit 0。
  - 空闲不重绘：宿主测试中隐藏窗口静置 300ms 帧计数不变；聚焦输入框后 1.7s 内帧数增长（光标闪烁驱动）。
- 环境适配（已写入 `docs/agents-notebook/README.md`）：GLFW 可能报 `Win32: Failed to create interface`，宿主安装“记录不抛出”的 GLFW 错误回调；Intel 驱动不接受 GLSL 源码中的非 ASCII 字符，着色器注释只用 ASCII。
- 未验证：真实驱动级鼠标/键盘/滚轮事件注入、真实屏幕缩放（DPI≠1）下的窗口、真实最小化/恢复操作；macOS/Linux 未运行（见任务 14）。
