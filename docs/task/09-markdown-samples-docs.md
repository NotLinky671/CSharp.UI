# MD-05：示例、文档与 BSD notice

创建日期：2026-10-25

状态:**已完成并验收**(2026-10-26;真实 `UiRuntime` harness 点击 URL→echo 通过;dark/900/doc-bottom PNG 有 URL+logo+alt+表格;`Guide/API/NOTICE/pack` 主审已读接受)

阶段：D

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §范围三档/验收要点。

[返回任务索引](README.md)

前置依赖：MD-03、MD-04（G2）。

## 文件范围

- `samples/CSharp.UI.Samples.ControlGallery/`：Markdown 演示页（中文样例：标题/嵌套强调/列表/引用/hr/围栏代码/链接回调/本地图/表格横滚；链接回调用页内状态回显 URL，不打开浏览器）。
- `samples/` 示例图：`tools/CSharp.UI.AssetGenerator` 生成或复用现有。
- `docs/guide/`：`markdown.md`（新，用法/策略/不支持清单/边界）+ `api-reference.md`/`platform-notes.md` 更新；`README.md`/`README.zh-CN.md` 功能现状更新（写已交付行为）。
- `NOTICE`/`docs/guide/platform-notes.md`：Markdig BSD-2-Clause 归属（版权行+许可摘要）。

## 实现契约

- Gallery 演示遵守现有样例模式；`MarkdownBlockView` 外包 `ScrollView` 展示长文。
- 文档写明：三档范围、`UiRuntimeServices.RichTextService` 注入要求与 `UiApplication` 默认装配（Skia 双接口自动复用）、不支持内容策略表、BSD notice、无 hover 键盘激活/行内嵌图等边界。
- README 功能现状只写已验收交付行为。
- 交付记录与验收证据归 MD-06。

## 边界

- 只 samples/docs/NOTICE；不动产品代码。
- 范围外功能零实现零示例。

## 验收

- Gallery Markdown 页真实运行：中文全要素、链接回调、图片、表格横滚（证据归 MD-06 采集）。
- `platform-notes.md`/`NOTICE` 含 Markdig BSD-2-Clause；`markdown.md` 清单与实现一致无超前宣称。
- 文档内相对链接全部可解析。
