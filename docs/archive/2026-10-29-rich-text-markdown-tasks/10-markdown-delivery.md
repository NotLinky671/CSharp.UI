# MD-06：交付验收——构建、套件、真实 smoke、NOTICE 与记录

创建日期：2026-10-25

状态:**已完成并验收**(2026-10-26;final-* 双 config 全套件 1592/config 0 fail;3 sample×2 cfg fresh build+真窗 smoke 全绿;NOTICE 入包 byte-identical;manifest+sha256 落盘)

阶段：D

设计依据：[计划](../../plan/2026-10-25-rich-text-and-markdown-plan.md) §验收要点/阶段门槛。

[返回任务索引](README.md)

前置依赖：MD-05（G3）。

## 文件范围

- `docs/delivery/2026-XX-XX-rich-text-markdown-delivery.md`（实际日期）+ `…-evidence/`（raw logs、manifest、PNG、SHA256SUMS、SOURCE-SHA256）。
- 不新建产品代码；只采集与记录。

## 验收项（逐项执行，原始输出留证据）

1. **构建**：`dotnet build CSharp.UI.slnx` Debug+Release（fresh/`--no-incremental`），0 警告 0 错误。
2. **测试套**：`CSharp.UI.Tests`/`CSharp.UI.Text.Tests`/`CSharp.UI.Integration.Tests`/`CSharp.UI.OpenTK.Tests`/`CSharp.UI.Markdown.Tests` 全量，双 config 全绿（计数记录）。
3. **样例**：3 samples × 2 config fresh build + 真实窗口 smoke（`CSHARPUI_SMOKE_FRAMES`）exit 0。
4. **真实 GL 像素证据**：离屏/真窗渲染，light/dark × 多 pixelScale——RichText 多样式、装饰顺序、链接命中（行内矩形）、表格 320 窄宽横滚与 1024 自然宽；PNG+断言 log 留存。
5. **依赖与许可**：`src/CSharp.UI` Core 无 Markdig 依赖——**交付包依赖图核验**（人工检查 csproj/`dotnet list package --include-transitive` 输出入 evidence，非永久 wiring/源字符串测试）；`CSharp.UI.Markdown` 包元数据正确；`NOTICE`/platform-notes 含 Markdig BSD-2-Clause 归属——**以打包产物实际内容核验**（nuget/pkg 输出展开检查 NOTICE 文件随包分发，非文档文案 echo）。
6. **manifest**：每步 stdout/stderr log + sha256；`SOURCE-SHA256` 与最终源一致。
7. **零 stub 核对**：范围内功能无 `TODO`/占位/假实现；范围外清单逐项未实现（grep+review 断言）。

8. **边界声明**：平台覆盖（未在相应 OS 运行/构建即明确未验证，不虚构跨平台编译；Windows 等实际执行平台按交付证据记录）、输入注入级别、未验项如实写明。

## 边界

- 不修补发现的缺陷——缺陷返 `docs/return/` 记录并回炉对应任务。
- 不宣称未执行的验证；macOS/Linux 只承诺源码/编译层。

## 验收（本任务自身）

- 交付记录引用真实命令输出与证据目录；manifest 全步 exit 0；评审可凭证据复现每项声明。
