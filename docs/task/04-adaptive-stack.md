# RL-04：AdaptiveStack 稳定布局切换

创建日期：2026-10-16

状态：待 Codex 复审

阶段：C

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

根据父级有效逻辑宽度切换横向/纵向布局，避免更换容器类型导致控件重新挂载。

## 前置依赖

[RL-02](02-flex-and-fill-width.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 实现 AdaptiveStack(breakpoint, params View[] children)，正有限断点，width<断点为纵向、width>=断点为横向；无界宽度为横向自然尺寸。
- [x] 复用统一 Stack 测量/排列算法，支持两种模式分别配置间距和交叉轴对齐；默认语义与对应 HStack/VStack 一致。
- [x] 父节点类型与同级子项集合稳定，只改变布局策略，不通过 HStack/VStack 树替换实现。
- [x] 正确解释 FlexAxis.Main 与显式水平/垂直参数，说明 Spacer 在有限/无限主轴的行为；更新模式变化的测量缓存。

## 测试与验收

- [x] 断点精确值、两侧小量、小数宽度、嵌套约束及 DPI1/1.25/1.5/2 下模式正确。
- [x] 输入中文、选择文本、设置焦点后宽→窄→宽，TextField/Component/滚动节点身份及交互状态保持。
- [x] 同模式连续宽度变化仅重新布局，不卸载子项；无界与硬尺寸溢出有明确结果。

## 实施记录

公开接口：`AdaptiveStack(double breakpoint, params View[])` / `AdaptiveStack(double, IReadOnlyList<View>)`、`Breakpoint`、`Spacing`、`VerticalSpacing`、`WideAlignment`、`NarrowAlignment`，以及 `WithSpacing/WithVerticalSpacing/Align/WideAlign` 和扩展方法 `.Spacing(...)`、`.VerticalSpacing(...)`。内部接口：复用 `StackLayout`，新增 `ViewNode.AdaptiveIsHorizontal`（纳入探测快照），共享 `ResponsiveValidation` 参数校验。

验证命令与退出码（Windows，.NET 10，`--no-restore -m:1 -nodeReuse:false`）：

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | exit 0，0 错误 |
| `dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll` | exit 0，259 Total / 0 Failed |
| `dotnet exec tests/CSharp.UI.Integration.Tests/bin/Debug/net10.0/CSharp.UI.Integration.Tests.dll` | exit 0，27 Total / 0 Failed |

RL-04 新增核心测试 12 项（`AdaptiveStackTests`：断点精确值/两侧/小数、无界横向自然尺寸、两模式间距与交叉轴对齐、Main 随模式、显式 Horizontal 不变高度、DPI 1/1.25/1.5/2、同模式连续缩放不卸载、宽窄切换身份/Key/组件 State/TextField 焦点光标选区/Tab 顺序、旧坐标不命中、硬尺寸溢出诊断、探测模式恢复、参数校验），集成测试 6 项（`AdaptiveStackIntegrationTests`：真实 Skia 下宽窄切换保持中文数据与真实光标可见、真实换行高度随模式变化、DPI 1/1.25/1.5/2 模式选择一致）。全部在 Windows 本机执行；未验证 macOS/Linux 原生与三平台人工视觉验收。提交 SHA 见仓库本地提交历史。
