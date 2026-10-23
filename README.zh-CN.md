# CSharp.UI

[English](README.md) | **简体中文**

基于 **OpenTK + OpenGL** 的跨平台 C# 声明式桌面 UI 框架：不可变视图描述 + 持久运行时节点，状态驱动更新、组件组合与链式修饰。

- 目标框架：**.NET 10 / C# 14**（`net10.0`）
- 渲染基线：**OpenGL 3.3 Core / GLSL 330**，全部最终绘制通过 OpenTK/OpenGL
- 文本：**SkiaSharp 3.119.4 + HarfBuzz** 负责 CPU 排版与栅格化，纹理由 OpenGL 上传
- 平台：Windows / macOS / Linux 统一自绘外观；首版单窗口

```csharp
public sealed class Counter : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);

        return new VStack(
            new Text($"计数：{count.Value}"),
            new Button("增加", () => count.Value++)
        )
        .Spacing(12)
        .Padding(24);
    }
}

UiApplication.Run(new Counter());
```

## 仓库结构

| 路径 | 内容 |
|---|---|
| `src/CSharp.UI` | 核心：视图、组件、状态、协调更新、布局、控件与平台无关绘制命令（不依赖 OpenTK / SkiaSharp） |
| `src/CSharp.UI.Text` | 字体解析、fallback、文本排版与栅格化、图片解码（SkiaSharp + HarfBuzz） |
| `src/CSharp.UI.OpenTK` | 桌面宿主：窗口、按需事件循环、输入适配、OpenGL 渲染与 GPU 资源、服务组装 |
| `samples/` | 计数器、待办列表、控件展示三个示例（同一份代码适用于三平台）；控件展示页与 `NestedResponsiveExample` 使用通用响应式 API，在 320/480/768/1024 逻辑宽度下可用 |
| `tests/` | 核心单元测试、文本测试、图形集成测试（xUnit v3） |
| `tools/CSharp.UI.AssetGenerator` | 生成示例图片资源，保证资源可复现 |
| `docs/guide` | 入门、API、状态、修饰符、Key、组件、平台与测试说明 |
| `docs/task` | 当前 Scrollbar 规划与实施任务；已完成任务归档于 `docs/archive/` |

历史输入法工具及其三个测试工程保存在标签 `archive/tools-before-removal`。需要复跑时，用 `git worktree add --detach ../CSharp.UI-ime-archive archive/tools-before-removal` 创建独立检出目录；验收日志和截图仍保留在 `docs/`。

## 快速开始

```bash
dotnet build CSharp.UI.slnx
dotnet run --project samples/CSharp.UI.Samples.Counter
dotnet run --project tests/CSharp.UI.Tests
```

详见 [入门指南](docs/guide/getting-started.md)。

## 文档

- [入门](docs/guide/getting-started.md)
- [公开 API 参考](docs/guide/api-reference.md)
- [响应式布局：使用与迁移](docs/guide/responsive-layout.md)
- [文本输入与编辑计划](docs/plan/2026-10-17-text-input-and-editing-plan.md)（TI-01～TI-10 已收口；TI-07 Windows TSF 生产验收 2026-10-22 完成——见 [P4 交付记录](docs/delivery/2026-10-22-ti07-p4-delivery.md)；TI-08 非 Windows 平台如实报告仅已提交文本能力，仅源码级与 CPU 模拟验证、无真机 IME 证据；TI-09～TI-10 于 2026-10-23 由用户验收收口，没有独立复审记录，已知边界见[交付记录](docs/delivery/2026-10-23-ti09-ti10-delivery.md)；任务记录归档于 [docs/archive/2026-10-23-text-input-editing-tasks/](docs/archive/2026-10-23-text-input-editing-tasks/README.md)）
- [多行文本编辑器计划](docs/plan/2026-10-23-text-editor-plan.md)（多行纯文本 `TextEditor` 已实现：编辑核心、IME 接入、示例与文档；Windows 微软拼音/IMM 真机验收 2026-10-23 完成——见[交付记录](docs/delivery/2026-10-23-text-editor-delivery.md)；非 Windows 维持仅已提交文本,不公开 TSF 开关）
- [状态与生命周期](docs/guide/state-lifecycle.md)
- [修饰符顺序](docs/guide/modifier-order.md)
- [Key 与动态列表](docs/guide/keys-and-lists.md)
- [编写自定义组件](docs/guide/custom-components.md)
- [平台、字体与原生依赖](docs/guide/platform-notes.md)
- [测试指南](docs/guide/testing.md)
- 设计与任务：[首版计划](docs/plan/2026-10-14-declarative-ui-framework-plan.md)、[响应式布局计划](docs/plan/2026-10-16-responsive-layout-plan.md)、[文本编辑器计划](docs/plan/2026-10-23-text-editor-plan.md)、[滚动条计划(已规划,未实现)](docs/plan/2026-10-23-scrollbar-plan.md)、[当前任务](docs/task/README.md)、[任务历史归档](docs/archive/)、[首版验收记录](docs/archive/2026-10-14-framework-tasks/README.md)、[响应式布局交付记录](docs/delivery/2026-10-16-responsive-layout-delivery.md)

## 响应式布局

`Flex`/`FillWidth` 提供弹性长度，`Flow` 自动换行，`AdaptiveStack` 在保持子节点身份的前提下切换横/纵排列，
`Responsive` 按父容器最终有效逻辑宽度选择任意内容结构。所有能力都基于父容器分配的逻辑宽度，不读取窗口尺寸、分辨率或 DPI：

```csharp
static View Header(State<Theme> theme, Theme current) =>
    new AdaptiveStack(
            560,
            new Text("CSharp.UI 控件展示"),
            new Button(
                current.IsDark ? "明亮主题" : "暗色主题",
                () => theme.Value = current.IsDark ? Theme.Light : Theme.Dark))
        .Spacing(12);

static View Actions(Action save, Action cancel) =>
    new Flow(new Button("保存", save), new Button("取消", cancel)).Spacing(8);

static View Search(State<string> query) =>
    new TextField(query.ToBinding()).FillWidth();
```

详见[响应式布局：使用与迁移](docs/guide/responsive-layout.md)。

## 首版范围

包含：`VStack`/`HStack`/`ZStack`/`Spacer`/`ScrollView`、`Text`/`Image`/`Rectangle`、`Button`/`Toggle`/`Slider`/`TextField`/`TextEditor`（多行：硬换行加按内容区宽软折行、固定 `MinLines` 视口内部 `ScrollY` 滚动、指针拖选边缘自滚、撤销重做与剪贴板命令）、`Padding`/`Frame`/`Background`/`Foreground`/`Border`/`CornerRadius`/`Opacity`/`Clip`/`Disabled`/`Key`、`ForEach`、明暗主题、字体/颜色环境继承、鼠标/键盘/滚轮/焦点/指针捕获、中文显示与**默认 IMM 路径**的已提交文本输入。

文本编辑命令（撤销/重做、剪切/复制/粘贴、全选）只通过键盘快捷键触发，没有公开的命令 API。见 [API 参考](docs/guide/api-reference.md)与[输入法手动验收清单](docs/guide/ime-manual-acceptance.md)。

Windows TSF 预发布路径（内部开关 `UseTsfInputBridge`，不是公开选项/默认路径）：组合/预编辑、随最终文字几何移动的 UI-less 框架自绘候选，以及失败时准确回退 IMM。入口必须声明 `[STAThread]`。生产验收（P4）已于 2026-10-22 在单 DPI（150%）微软拼音环境完成——见 [P4 交付记录](docs/delivery/2026-10-22-ti07-p4-delivery.md)；多 DPI 证据属环境边界。不支持鼠标点选候选；键盘选词由输入法自身处理。

默认发布不包含：`TextEditor` 在非 Windows 平台的真实输入法组合输入（Windows 微软拼音/IMM 真机验收 2026-10-23 完成——见[交付记录](docs/delivery/2026-10-23-text-editor-delivery.md)；macOS/Linux 维持仅已提交文本）、动画系统、列表虚拟化、完整双向文本、彩色 Emoji 字形图集、移动端/浏览器/宿主嵌入、系统无障碍桥接。

## 包状态

框架包保持**预发布**状态（`0.1.0-preview.1`），本仓库不自动发布 NuGet。

## 许可与资源

代码以 MIT 许可发布。仓库不重新分发第三方字体二进制文件；示例图片由 `tools/CSharp.UI.AssetGenerator` 绘制生成。详见[平台、字体与原生依赖](docs/guide/platform-notes.md)。
