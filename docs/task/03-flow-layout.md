# RL-03：Flow 自动换行容器

创建日期：2026-10-16

状态：待 Codex 复审

阶段：B

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

提供确定性的横向自动分行，维持同一批子节点、绘制顺序和交互身份。

## 前置依赖

[RL-02](02-flex-and-fill-width.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 实现不可变 Flow、Spacing/LineSpacing 与行内顶部/Center/End 对齐，采用从左到右的贪心分行；行高由最终测量后的最高项目决定。
- [x] 空项、正好放满、无界宽度、单项超宽都有明确行为；可重新测量的项限制到整行宽度，不可压缩固定项独占一行并诊断。
- [x] 不用每行额外运行时容器重挂载子项；只保存有界布局缓存并更新 Frame，Key 与声明顺序保持。
- [x] 首版不做逐行 Flex 权重分配，直接子项的 Flex grow/shrink 给出不支持诊断；FillWidth 作为整行宽度请求处理，不能按逐行权重分配。
- [x] 垂直 ScrollView 中自然高度正确；宽度收缩/展开使内容高度和滚动边界更新。

## 测试与验收

- [x] 分行的几何、换行阈值、行高、间距和多行文字用真实布局断言，包含 Padding/Border。
- [x] 换行前后 Key 节点实例、组件状态、TextField 焦点/选择与 Tab 顺序保持；旧坐标不能命中。
- [x] 窗口宽度相同但嵌套容器宽度不同会产生不同分行；无界时为单行，极窄时无死循环。

## 实施记录

公开接口：`Flow(params View[])` / `Flow(IReadOnlyList<View>)`、`Spacing`、`LineSpacing`、`LineAlignment`、`WithSpacing/WithLineSpacing/Align`，以及扩展方法 `.Spacing(...)`、`.LineSpacing(...)`。内部接口：`FlowLayout`、`FlowLayoutCache`、`FlowItemLayout`、`ViewNode.FlowCache`（纳入探测快照）、`LayoutDiagnosticKind.Unsupported` 与 `LayoutContext.ReportUnsupported`。

验证命令与退出码（Windows，.NET 10，`--no-restore -m:1 -nodeReuse:false`）：

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | exit 0，0 错误 |
| `dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll` | exit 0，247 Total / 0 Failed |
| `dotnet exec tests/CSharp.UI.Integration.Tests/bin/Debug/net10.0/CSharp.UI.Integration.Tests.dll` | exit 0，21 Total / 0 Failed |

RL-03 新增核心测试 20 项（`FlowLayoutTests`：贪心分行/精确阈值/空/零宽/无界/超宽重测量/硬固定溢出/FillWidth 整行/Flex 诊断/行内对齐/Padding/嵌套宽度差异/宽度变化/DPI 1、1.25、1.5、2/探测缓存恢复/参数校验），集成测试 3 项（`FlowIntegrationTests`：真实 Skia 换行高度、ScrollView 内容高度与滚动边界、缩窄后真实光标可见）。全部在 Windows 本机执行；未验证 macOS/Linux 原生与三平台人工视觉验收。提交 SHA 见仓库本地提交历史。
