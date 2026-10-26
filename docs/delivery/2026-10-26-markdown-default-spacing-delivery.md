# Markdown 默认排版间距交付

日期：2026-10-26
计划：[docs/plan/2026-10-26-markdown-default-spacing-plan.md](../plan/2026-10-26-markdown-default-spacing-plan.md)
审核记录：[docs/return/2026-10-26-markdown-default-spacing.md](../return/2026-10-26-markdown-default-spacing.md)
独立验证证据：[docs/return/2026-10-26-markdown-spacing-evidence/](../return/2026-10-26-markdown-spacing-evidence/)

## 交付内容

修复"Markdown 渲染太紧凑"：默认块级排版从全零间距改为有层次的阅读间距，
现有 API 保留；仅新增可选样式属性。

### 新增 `MarkdownStyleOptions` 字段与实测默认效果

| 字段 | 默认 | 实测（真实 GL 几何） |
|---|---|---|
| `BlockSpacing` | 12 | 段落↔段落 gap = 12 |
| `HeadingSpacingBefore` | 20 | 正文→标题 gap = 20（节间分隔） |
| `HeadingSpacingAfter` | 8 | 标题→正文 gap = 8（标题贴近其正文） |
| `ListItemSpacing` | 4 | 列表项之间间距（紧凑成组） |
| `CellVerticalPadding` | 6 | 表格行带高 +12（2×vp）；列宽不变 |

间距模型 = margin-collapse：相邻块 `gap = max(前块 after, 后块 before)`，
文档/引用/列表项/表格 cell 内首块不产生前空白（首块标题无顶部空白）。
间距可按需配置；实测自定义 `Style` 后标题→正文 gap 8→16 生效。

### 表格垂直 padding

`MarkdownTable` 新增 `cellVerticalPadding`（clamp ≥0）：计入行高与 cell y 偏移，
不进列宽 min/pref；`LayoutPropertiesEquivalent` 纳入比较——同 doc 换 `Style` 立即重排版。
分隔条仍整宽位于行间；320→1024→320 跨档 resize 与 Σmin 超宽横滚语义不变。

## 验证范围（实测）

- `dotnet build CSharp.UI.slnx`：Debug + Release 均 0 警告 0 错误。
- `CSharp.UI.Markdown.Tests`：**49 / 0 失败，Debug+Release 两 cfg**；
  `CSharp.UI.Tests` 698/0、`CSharp.UI.Integration.Tests` 53/0（Debug）。
- 独立真实 OpenGL smoke：**76 项 fixture PASS / 0 FAIL**；12 组合截图矩阵
  （light/dark × 320/1024 × PixelScale 1/1.5/2）已生成，主审抽查亮/暗 1024 顶部、
  320 表格及 400 默认完整块 fixture 视觉接受；真实 `UseState` Style swap、
  链接点击 echo 均成立。
- `dotnet test` MSBuild IPC 本机命名管道 Access denied（环境限制）——测试通过
  项目 runner（`dotnet run --no-build`）或生成的测试 exe 运行；历史 harness 外跑
  run1–4 失败属采集/harness 问题非产品缺陷，stdout collector caveat 详见证据目录
  （direct1 返回码未独立记录；76/0 有 raw 输出，run5 实际 exit 0）。

- **行高**：本轮保留字体自然行高（0=行内最大字高语义不动）；本轮修复的是
  块间距与表格留白，不引入行高配置。

- 未改 Core/ScrollView/Responsive/Flow；未建通用 spacing 引擎；未加新 Markdown 语法。
- macOS/Linux 未实跑（沿用既有平台边界）；验证平台 Windows x64。
- 无未完项。
