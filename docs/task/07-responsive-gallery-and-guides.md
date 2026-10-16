# RL-07：示例改造与使用指南

创建日期：2026-10-16

状态：已完成（Codex复审通过）

阶段：E

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

使用通用 API 让 Gallery 在320/480/768/1024逻辑宽度可用，提供独立嵌套容器示例与迁移指南。

## 前置依赖

[RL-03](03-flow-layout.md)、[RL-04](04-adaptive-stack.md)、[RL-06](06-responsive-state-and-lifetime.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 页头/表单行用AdaptiveStack，按钮组用Flow，输入框和滑块使用横向弹性长度；文本Wrap/Ellipsis以实际可用宽度计算，可保留演示maxWidth。
- [x] 图片说明、状态文本、修饰符演示都能换行/增高；保留首版全部控件及真实内层ScrollView，不能删除内容来规避窄窗口。
- [x] 新增可独立实例化的NestedResponsiveExample组件（可在现有示例工程内），在同一窗口宽度下展示宽/窄两种容器响应；独立测试直接实例化它，不复制Gallery树。
- [x] 稳定身份优先AdaptiveStack，任意Responsive分支示例把持久状态放外层，明确输入焦点边界。
- [x] 更新根README、API、弹性/Frame顺序、响应式状态身份与迁移指南；新旧两份设计计划及历史任务入口清楚。

## 测试与验收

- [x] 320/480/768/1024逻辑宽度下主题按钮与主要操作可访问，文本按需换行/省略，没有非预期横向裁切。
- [x] 相同全局窗口中不同父宽度正确响应；宽窄切换后计数、绑定、内层滚动与稳定输入焦点保持。
- [x] 示例覆盖所有新增API及首版能力，指南代码可以编译；不向产品界面泄漏诊断/实施细节。

## 实施记录

### 公开 / 内部接口

- **无新增框架公开 API**。示例新增公开组件 `NestedResponsiveExample`（命名空间 `CSharp.UI.Samples.ControlGallery`，`samples/CSharp.UI.Samples.ControlGallery/NestedResponsiveExample.cs`），可直接 `new NestedResponsiveExample()` 实例化；同一窗口内通过 `Flow` 分配出 640 / 320 两个父宽度的面板，各自选择不同的 `Responsive` 分支与 `AdaptiveStack` 排列。
- 示例改造（均只使用已冻结的通用 API，不读取窗口/全局尺寸）：
  - `GalleryApp`：页头与“标签 + 输入/滑块”行使用 `AdaptiveStack`（560），按钮组与修饰符标签使用 `Flow`，输入框/滑块使用 `.FillWidth()`；“布局”区域用 `AdaptiveStack`（840）切换同一批子节点；长文本 `.Wrap()`、省略演示与换行演示用 `new FrameView(content, maxWidth: 360/320, alignment: Start)`。
  - `Counter`：按钮组改为 `Flow`，文本 `.Wrap()`。
  - `TodoList`：页头与“输入 + 添加/清空”表单行改为 `AdaptiveStack`，行内操作按钮改为 `Flow`，文本 `.Wrap()`。
  - 首版全部控件、主题、真实内层 `ScrollView`（`.Frame(height: 120)`，内容仍为 1 行说明 + 40 行）与两层滚轮路由全部保留，未通过删除内容适配。
- **框架内部修正**：`FrameView.Measure` 声明 `maxWidth`/`maxHeight` 时，子项约束改为 `Math.Min(父级上限, 声明值)`（`src/CSharp.UI/Views/Modifiers.cs`）。此前 `WithMaxWidth` 会把声明值直接替换父级上限，导致窄容器里的文字按更宽宽度排版、再被更窄的 `Frame` 截断。公开签名不变。
- 指南：新增 `docs/guide/responsive-layout.md`（使用 + 从固定尺寸迁移 + 状态身份/焦点边界 + 已知边界）；更新 `README.md`、`docs/guide/api-reference.md`、`docs/guide/modifier-order.md`、`docs/guide/state-lifecycle.md`、`docs/guide/getting-started.md`。

### 验证命令与退出码（顺序执行，`--no-restore -m:1 -nodeReuse:false`）

| 命令 | 结果 |
|---|---|
| 三个示例 Debug 构建（Counter/TodoList/ControlGallery） | 各自 exit 0，0 警告 / 0 错误 |
| `CSharp.UI.Tests` 程序集 | exit 0，300 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Text.Tests` 程序集 | exit 0，67 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.Integration.Tests` 程序集 | exit 0，30 Total / 0 Failed / 0 Skipped |
| `CSharp.UI.OpenTK.Tests` 程序集 | exit 0，204 Total / 0 Failed / 0 Skipped（本机桌面权限） |
| Codex 独立探针 `artifacts/responsive-work/gallery-invariants` 构建 | exit 0，0 警告 / 0 错误 |
| 探针运行 | exit 0，67/67 PASS（含 320/480/768/1024 × DPI 1/1.25/1.5/2 的卡片/控件边界、真实 Skia 文本排版宽度不超框、纵向滚动到底、窄化后输入焦点/选区，以及直接实例化 `NestedResponsiveExample` 的分支与边界检查） |
| `git diff --check` | exit 0 |

四套合计 **601**，失败/错误/跳过均为 0。相对 RL-06 基线 585 新增 16 项：核心 +1（`LayoutProbeTests.FrameMaxWidth_ClampsChildMeasurementToParentAvailableWidth`），图形/示例 +15（`tests/CSharp.UI.OpenTK.Tests/GalleryResponsiveTests.cs`，覆盖四个宽度下的溢界、真实换行/省略、主题、计数/开关/滑块/中文输入，以及嵌套示例的同窗分支、稳定焦点/选区/滚动）。
完整命令、日志路径与限制见 `artifacts/responsive-work/rl07-progress.txt`。

### 适用平台与未验证边界

- Windows 本机 .NET 10：核心/文本/集成无窗口；OpenTK 使用本机 OpenGL 3.3；示例边界与文本用真实 SkiaSharp 排版，DPI 1/1.25/1.5/2 为程序化验证。
- 未验证：macOS/Linux 真机与三平台人工视觉验收、真实窗口拖拽缩放时的人工观察、Release 构建与启动检查、跨窗口/多窗口场景（RL-08/RL-09 负责）。
- 已知边界：宽度无界时 `FillWidth` 回退自然尺寸、`AdaptiveStack` 使用横向自然尺寸、`Flow` 单行、`Responsive` 使用自然尺寸分支；`Responsive` 跨父级/类型切换不迁移焦点或本地编辑状态；未声明弹性/换行的硬固定内容仍可能溢出并给出诊断。
- 提交：本地英文提交（精确路径），不 amend、不推送；SHA 见 `git log`。未修改 `AGENTS.md` 用户内容与 Codex 的 `docs/return` 记录，未改动 SDK/NuGet/系统配置，未清理 `artifacts`。
