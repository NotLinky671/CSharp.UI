# MD-01 独立验证（Parse 层，run1）：13 PASS / 0 FAIL, exit 0

日期：2026-10-26
验证代理：rich-markdown-verification（独立验证；非实现方）
被测快照：`lib-sha-md01.txt` —— `CSharp.UI.Markdown.dll` `55bfc328`（MD-01 freeze）、
`Markdig.dll` `4dab6e49`（1.5.0.0 官方 nupkg 提取）、`CSharp.UI.dll` `00ad025c`、
其余依赖见 `lib-sha-md01.txt` 全 15 项。
命令：`dotnet rt03verify.dll md01`（外置 harness，%TEMP%\rt03-verify\app，
`-p:Md01Enabled=true` 条件编译 `Md01.cs`；产品无改动、repo 未构建）。
raw：`md01-run1-stdout.txt` / `md01-run1-stderr.txt`(0B) / `md01-run1-exit.txt`=0。

## 断言（全部经公共 API，无内部断言）

| 检查 | 结果 |
|---|---|
| MD01-null-source-ane | `Parse(null)` → `ArgumentNullException`（非 NRE、非空文档） |
| MD01-source-verbatim | 中文源 `# 一级标题\n\n正文 **粗体**…😀跨行…` 48 字符逐字保留于 `doc.Source` |
| MD01-options-default | 默认 `MarkdownPipelineOptions{PipeTables=T, EmphasisExtras=T, TaskLists=F, AutoLinks=T, Html=Literal}` 记录于 `doc.Options` |
| MD01-doc-equals-self | 值相等自反 |
| MD01-cache-same-instance | 同源 + 值相等 options（两实例）→ `ReferenceEquals` 同一份 `MarkdownDocument`（LRU 缓存） |
| MD01-diff-option-diff-instance | 仅 `TaskLists` 翻转 → 不同实例且不等值 |
| MD01-diff-source-diff-instance | 仅源不同 → 不同实例 |
| MD01-option-{PipeTables,EmphasisExtras,TaskLists,AutoLinks,Html}-differs | 五项 settable 选项各自翻转都产生不同 document（`!ref-eq && !Equals`） |
| MD01-lru-consistent | 64 容量溢出后重解析同 key 仍内部一致（首次实例被 evict 属预期 LRU，NOTE 记录 `first!=again`） |

诊断（非契约，reflect 确认真实解析非空壳）：`MarkdownDocument` 内部持有
`Markdig.Syntax.MarkdownDocument` AST 根，子节点计数=1（`- [ ] t` 源）；AST 未公开——
与"内部管线"定位一致。

## 诚实边界

- 仅 Parse 层。视图适配/渲染在 MD-02+，本目录不含视图断言。
- `ClearParseCache` 在 run1 快照为 public API 可调；收紧后 internal——run2 起
  用唯一 source 前缀隔离（断言面不变）。
- Html enum 探测用 `Strip`（非默认 Literal）验证分支差异；每个枚举值未穷举。

## run2（good 收紧后快照 `6b7c4c64`）：13 PASS / 0 FAIL, exit 0

`ClearParseCache`/`ParseCacheCount` 已 internal——harness 不再可调（改用唯一 source
前缀隔离，断言面不变）。新快照 `lib-sha-md01-r2.txt`：`CSharp.UI.Markdown.dll`
`6b7c4c64`，其余 dll 与 run1 相同。raw：`md01-run2-stdout/stderr/exit`。
Parse 公共语义与 run1 完全一致（13 项同名断言全过）。
