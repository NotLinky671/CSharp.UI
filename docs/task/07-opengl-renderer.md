# 任务 07：OpenGL 命令渲染与 GPU 资源管理

- 任务 ID：07
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)

## 目标与范围

通过 OpenTK/OpenGL 完成所有最终 UI 绘制，正确处理层叠、透明混合、裁剪和纹理资源。

实现通用图形与纹理渲染；文字栅格化由任务 08、图片加载由任务 11 提供。

## 前置依赖

- 前置任务：[任务 05：布局、修饰符与绘制命令生成](05-layout-and-modifiers.md)、[任务 06：OpenTK 桌面宿主与按需事件循环](06-opentk-host.md)。
- 接入依据：消费任务 05 生成的有序绘制命令，使用任务 06 的上下文、framebuffer 尺寸与生命周期。

## 实施清单

- [x] 使用 OpenTK 的 OpenGL 绑定、GLSL 330 着色器及 VAO/VBO/索引缓冲，实现矩形、圆角和边框。
- [x] 实现 RGBA 纹理上传、纹理矩形绘制和透明度叠加，统一 CPU 位图格式、纹理采样与混合约定，避免透明边缘色晕。
- [x] 按绘制顺序消费命令，只合并相邻且纹理/裁剪/管线状态兼容的命令，不为批处理跨越层叠顺序。
- [x] 实现矩形裁剪栈，把逻辑坐标换算为 framebuffer scissor 坐标，处理 DPI 与 OpenGL 原点差异。
- [x] 实现纹理、缓冲、着色器和 framebuffer 的创建与释放；提供可检查的资源计数，在有效上下文上完成销毁。
- [x] 实现离屏 framebuffer 测试入口与像素读回，用固定场景验证绘制结果；主渲染路径不使用 Skia GPU surface。

## 公开 API 与交接

应用侧沿用框架 View 与颜色类型；渲染器通过核心命令及纹理句柄工作，GL 对象不进入控件公开 API。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 离屏图验证背景、内容、边框、圆角及 ZStack 顺序，透明内容按正确顺序混合。
- [x] 嵌套裁剪、空裁剪、不同 DPI 和视口高度下 scissor 位置正确。
- [x] 纹理像素格式和透明度约定一致；图片与后续文字可共用纹理绘制流程。
- [x] 重复提交场景不会持续创建资源；退出时 GL 资源计数回落，着色器失败能报告诊断并释放部分资源。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成**（2026-10-14）。
- 交付（`src/CSharp.UI.OpenTK/Rendering`）：
  - `GlRenderer`：GLSL 330 着色器 + VAO/VBO/索引缓冲，圆角矩形填充与描边（SDF）、纹理与 `Alpha8` 遮罩绘制、透明度叠加、相邻命令批处理（纹理/裁剪/管线状态变化即切批，不跨层叠顺序）。
  - `GlTextureService`：`ITextureService` 实现（按 `Equals` 键缓存、LRU 淘汰并 `glDeleteTextures`、`LiveTextureCount`/创建/释放/字节计数、`Clear`）。
  - `GlShaderProgram` / `GlShaderSources` / `GlShaderException`：编译与链接诊断，失败路径释放部分资源。
  - `GlCoordinateMath`：逻辑坐标 → framebuffer 的 scissor/NDC 换算（含 DPI 与 Y 轴翻转）。
  - `GlResourceCounters`：进程级 GL 资源创建/释放/存活计数。
  - `Testing/GlOffscreenSurface`：隐藏窗口 + FBO + `glReadPixels` 的离屏验证入口。
- 像素与混合约定：CPU 位图预乘 alpha（`Rgba8888Premultiplied`/`Bgra8888Premultiplied`），文本为 `Alpha8` 覆盖率遮罩；GPU 统一 `glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)`，避免透明边缘色晕。
- 实际执行：`dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build` → **91 通过 / 0 失败**；图形子集 `-- -trait "Category=Graphics"` → **44 通过 / 0 失败**（真实光栅化 + `glReadPixels` 断言），覆盖：
  - 背景 / 内容 / 边框 / 圆角 / ZStack 层叠顺序、预乘透明混合；
  - 嵌套裁剪、空裁剪、不同 DPI 与视口高度下的 scissor 与内容位置；
  - `Alpha8` 遮罩与无色晕、非 4 字节对齐行距的 Alpha8、无效句柄跳过；
  - 纹理 LRU 淘汰、重复提交不增长资源、释放后计数回落；
  - 着色器失败诊断与部分资源回收；GLFW 剪贴板往返。
- 已知限制：图形测试在 xunit worker 线程运行，夹具调用 `GlOffscreenSurface.AllowNonMainThreadCreation()` 放开 GLFW 主线程检查；**macOS 仍要求 GLFW 在主线程**，在 macOS 上运行图形测试需要主线程测试宿主。
