# MD-04 记录：图片/链接策略落地 — 实现自测 + 待独立验证

日期：2026-10-26 · 范围：`src/CSharp.UI.Markdown/MarkdownImageView.cs`（新增）、`tests/CSharp.UI.Markdown.Tests/MarkdownImageTests.cs`（新增永久回归）

## 范围

- 块级唯一 inline image → `MarkdownImageView`（internal，静态 `[Image, RichText]` 子节点），由 good 的 `BlockAdapter` `AdaptParagraph` 首行 `TryCreate` 接入。
- remote 默认拒绝（不调 `Resolve`）、`AllowRemote`+resolver、Resolve null→alt、`TryLoad` false→alt。
- 尺寸契约：不放大、宽度等比缩、宽无界自然。
- 混排（同段含其它 inline）→ `TryCreate` 返回 false，走既有 `[alt]` literal。
- `OnUpdate` 按 source/alt 语义变化清 `ViewCache`；`Measure` 不再对 inactive 子节点 `Arrange`（修复 probe 污染）。

## 证据结构

- **实现自测（source-shim harness，`temp-verify-md04/`）**：早期快照 29/29 PASS + GL 6/6 PASS。
  raw：`md04-run1-{stdout,stderr,exit}.txt`、`md04-gl-run1-{stdout,stderr,exit}.txt`、PNG `md04-{real,local,remote-alt,fail-alt,wide,plain-richtext}.png`。
- **真实程序集集成 probe（`temp-verify-md04-real/` 引项目 ref，无源码/shim）**：18/18 PASS + GL 3/3 PASS。
  raw：`md04-real-run1-{stdout,stderr,exit}.txt`、PNG `md04-real-{local,remote-alt,fail-alt}.png`。
- **永久回归**：`tests/CSharp.UI.Markdown.Tests/MarkdownImageTests.cs` —— 6/6 PASS（tiny 不放大、超宽等比缩、TryLoad fail→alt、remote 默认 resolver 0、state 切 source A→B aspect 更新、fail→success）。

## 备注

- `BlockAdapter.cs`/`InlineAdapter.cs`/`MarkdownTable.cs`/`AdapterTests.cs` 由 good 持有，本任务未编辑。
- 早期 harness `temp-verify-md04/` 因 `MarkdownTable.cs` 实现过程中语法断裂而排除该文件并加同名 shim；仅作诊断用，不作为最终验收依据。最终结论以 `temp-verify-md04-real/` 真实 dll 结果 + 永久测试为准。
- 状态：实现+自测完成，独立验证待主审。

## run2(最终冻结快照 `a79f60c3`)

外部 runner `temp-verify-md04-real` + 真实 `CSharp.UI.Markdown.dll` + Skia/OpenTK 实栈:
18/18 unit PASS + GL 3/3 PASS,exit 0。raw:`md04-real-run2-{stdout,stderr,exit}.txt`;
run1 PNG 仍有效(GL 代码路径未变)。
