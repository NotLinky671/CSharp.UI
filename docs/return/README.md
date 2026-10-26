# 返修记录目录

本目录仅存放**当前未解决、需要返修**的审核问题记录。

## 写入规则

- 审核发现问题时，由审核方创建带日期的 `.md` 文件描述问题与验收条件；实现方返修并由审核方复审通过后才可英文提交并推送实现（见 `AGENTS.md` 工作流）。
- 记录按 `YYYY-MM-DD-<主题>.md` 命名；大型证据放同名 `-evidence`/配套子目录。
- 问题关闭后不在此堆积：按任务归档到 `docs/archive/` 下对应归档目录；本次历史记录见[2026-10-23 已完成审核归档](../archive/2026-10-23-completed-reviews/README.md)。

## 当前状态

**暂无待返修事项。** TE-01~TE-05 已经复审/主审批准关闭(链路见任务索引),TE-06 真实 IMM 验收三轮返修后于 2026-10-23 全部通过(证据 [2026-10-23-te06-real-ime-evidence](2026-10-23-te06-real-ime-evidence/),交付 [text-editor-delivery](../delivery/2026-10-23-text-editor-delivery.md))。全部历史审核(框架 R01–R09、R12–R18、响应式 RL-01～09、文本输入 TI-01～TI-10)均已收口并归档。

### 富文本与 Markdown 任务组(2026-10-26 已收口)

| 任务 | 记录 | 证据 | 状态 |
|---|---|---|---|
| RT-01 | [StyledText 契约](2026-10-25-rt01-styled-text.md) | [evidence](2026-10-25-rt01-styled-text-evidence/) | 已验收 |
| RT-02 | [IRichTextService/Skia 排版](2026-10-25-rt02-rich-text-service.md) | [evidence](2026-10-25-rt02-rich-text-service-evidence/) | 已验收 |
| RT-03 | [RichText 视图/paint 解算](2026-10-26-rt03-rich-text-view.md) | [evidence](2026-10-26-rt03-rich-text-view-evidence/) | 已验收 |
| RT-04 | [链接 hover/激活/capture](2026-10-26-rt04-rich-text-link-hit.md) | [evidence](2026-10-26-rt04-rich-text-link-hit-evidence/) | 已验收 |
| MD-01 | [Markdig 管道+缓存](2026-10-26-md01-markdown-module-pipeline.md) | [evidence](2026-10-26-md01-markdown-module-pipeline-evidence/) | 已验收 |
| MD-02 | [块/内联 adapter](2026-10-26-md02-markdown-block-adapter.md) | [evidence](2026-10-26-md02-markdown-block-adapter-evidence/) | 已验收 |
| MD-03 | [MarkdownTable 二段列宽+真实排版](2026-10-26-md03-markdown-table.md) | [evidence](2026-10-26-md03-markdown-table-evidence/) | 已验收 |
| MD-04 | [图片/链接策略+降级](2026-10-26-md04-markdown-assets-links.md) | [evidence](2026-10-26-md04-markdown-assets-links-evidence/) | 已验收 |
| MD-05 | [Gallery MarkdownDemo+文档+NOTICE](2026-10-26-md05-docs-notice-verification.md) | 真实 `UiRuntime` harness 点击 URL→echo 通过;50 checks(2 themes×3 scales×2 widths)实际 render;320×2 URL/quote/list/code+1024 dark table 完整 | 已验收 |
| MD-06 | 交付验收(双 config 套件/三 sample 真窗/NOTICE 打包/manifest) | `final-*` 全套件 1592/config 0 fail;3 sample×2 cfg fresh+smoke 全绿;NOTICE 入包 byte-identical sha256 `58905395…`;offscreen 12-matrix 50/0 | 已验收 |

交付:[rich-text-markdown-delivery](../delivery/2026-10-26-rich-text-markdown-delivery.md)。
