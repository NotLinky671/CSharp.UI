# 任务 01：工程初始化与版本管理

- 任务 ID：01
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../../plan/2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)

## 目标与范围

建立能够持续构建和测试的工程骨架，为核心、文本、OpenTK 后端和示例提供明确的模块边界。

本任务建立工程与测试入口，不实现窗口、控件或渲染逻辑。

## 前置依赖

- 前置任务：无。
- 接入依据：当前目录仅有设计文档；技术基线采用原计划中的固定版本。

## 实施清单

- [x] 建立 .NET 10 / C# 14 解决方案，按 src、samples、tests 分组；创建 CSharp.UI、CSharp.UI.Text、CSharp.UI.OpenTK 三个库和示例、测试工程。
- [x] 引用方向保持单向：Text 引用 Core；OpenTK 引用 Core 并在宿主中组装 Text 服务；示例引用 OpenTK。测试按模块引用目标库，避免循环引用。
- [x] 使用集中式包版本管理固定 OpenTK 4.9.4、SkiaSharp 3.119.4 和同版本 SkiaSharp.HarfBuzz；后续任务补齐相应平台原生资产包。
- [x] 配置 SDK 基线、Nullable、统一构建设置和忽略构建产物的规则；测试采用 xUnit，单元测试与需要图形上下文的集成测试分开。
- [x] 提供统一的 dotnet build / dotnet test 入口，记录环境要求；仅生成本地工程，不发布包。

## 公开 API 与交接

确定程序集和命名空间 CSharp.UI、CSharp.UI.Text、CSharp.UI.OpenTK；公开控件与基础类型归核心，宿主启动入口归 OpenTK 后端。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 从干净工作目录恢复依赖后，解决方案能执行 dotnet build 和 dotnet test；不依赖 IDE 专用配置。
- [x] 三个库目标框架均为 net10.0，编译器语言版本为 C# 14，依赖版本集中且无预览 OpenTK 5 引用。
- [x] 核心工程没有 OpenTK、SkiaSharp 及平台原生库引用；程序集引用图无循环。
- [x] 无图形环境时核心测试仍可运行；测试入口可以单独选择图形集成测试。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成**（2026-10-14）。
- 交付：`CSharp.UI.slnx`、`Directory.Build.props`、`Directory.Packages.props`、`.gitignore`，以及 src/samples/tests/tools 下共 10 个工程。
- 实际执行：
  - `dotnet restore CSharp.UI.slnx` → 成功（OpenTK 4.9.4、SkiaSharp 3.119.4、SkiaSharp.HarfBuzz 3.119.4、HarfBuzzSharp 8.3.1.5 均已固定）。
  - `dotnet build CSharp.UI.slnx` → 成功，0 警告 0 错误。
  - `dotnet run --project tests/CSharp.UI.Tests` → 90 通过 / 0 失败。
  - `dotnet run --project tests/CSharp.UI.Text.Tests` → 55 通过 / 0 失败。
  - `dotnet run --project tests/CSharp.UI.OpenTK.Tests` → 见任务 06/07 记录。
- 命名空间偏差（已记录）：文本实现程序集名保持 `CSharp.UI.Text`，命名空间为 `CSharp.UI.Skia`（核心已有公开视图类型 `CSharp.UI.Text`，命名空间不能与同名公开类型共存，否则宿主与示例无法引用文本服务；详见任务 08 记录）。
- 偏差：
  - 测试运行器由 vstest 改为 **xunit.v3 + Microsoft.Testing.Platform 进程内运行器**（`dotnet run --project tests/<项目>`），原因是本机 vstest 测试宿主在受限进程句柄下崩溃；测试框架仍为 xUnit。
  - `CSharp.UI.OpenTK` 额外引用 `CSharp.UI.Text`，用于在宿主内组装文本/图片服务（任务 06 要求“在宿主中组装 Text 服务”）；核心工程无 OpenTK/SkiaSharp/原生库引用，引用图无循环。
- 完整记录见 [完成记录汇总](completion-records.md)。
