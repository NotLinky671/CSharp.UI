# MD-06 delivery evidence — final run

日期:2026-10-26 · 状态:**完成**(最终 freeze 后 fresh 重跑通过)

## 最终步骤(`final-` 前缀,真实 exit + stdout/stderr 全部落盘)

| 步骤 | exit | 结果 |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 | 0 警告 0 错误,~8s |
| `dotnet build CSharp.UI.slnx -c Release --no-incremental` | 0 | 0 警告 0 错误,~8s |
| `CSharp.UI.Tests` Debug / Release | 0 / 0 | **698 / 698 PASS** |
| `CSharp.UI.Text.Tests` Debug / Release | 0 / 0 | **109 / 109 PASS** |
| `CSharp.UI.Integration.Tests` Debug / Release | 0 / 0 | **53 / 53 PASS** |
| `CSharp.UI.OpenTK.Tests` Debug / Release | 0 / 0 | **684 / 684 PASS**(此前 66 失败已修,host 注入 RichTextService) |
| `CSharp.UI.Markdown.Tests` Debug / Release | 0 / 0 | **48 / 48 PASS** |
| **测试合计** | | **1592 PASS / config** |
| 3 samples × Debug/Release fresh build | 0 ×6 | 0 警告 0 错误 |
| 3 samples × Debug/Release 真窗 smoke `CSHARPUI_SMOKE_FRAMES=5` | 0 ×6 | 5 帧自停,exit 0 |
| `dotnet list src/CSharp.UI package --include-transitive` | 0 | 无包引用 |
| `dotnet list src/CSharp.UI.Markdown package` | 0 | Markdig 1.5.0 only |
| `dotnet pack` CSharp.UI Release | 0 | nupkg 成员:`nuspec`、`lib/net10.0/CSharp.UI.dll`(不打包 NOTICE,正确:core 无第三方) |
| `dotnet pack` CSharp.UI.Markdown Release | 0 | nupkg 含 `NOTICE` 1701B,sha256 `58905395...` 与仓库 NOTICE byte-identical |
| md06 offscreen 12-matrix on final DLLs | 0 | 50 PASS / 0 FAIL(同 24 PNG 重写) |
| md06 API quick-example on final DLLs | 0 | 6 RichText + 1 MarkdownTable + 1 link node + 9 draw cmds |

## 历史(保留,不洗绿)

前一轮 `suite-opentk-tests-{Debug,Release}.stdout.txt` 952/957KB 包含 `66 FAIL — RichTextService 未注入 fixture` 的完整原始记录;`suite-opentk-tests-{Debug,Release}.exit.txt` 保留 `1`。`final-` 前缀下的 `final-suite-opentk-tests-*` 为修复后真实 exit=0。

## 元数据

- `SOURCE-SHA256.txt`:270 files FINAL(src 129 / tests 122 / samples 10 / tools 2 / 根 7 — Directory.Build.props、Directory.Packages.props、NOTICE、CSharp.UI.slnx、AGENTS.md、README.md、README.zh-CN.md),排除 `bin/obj/.git/artifacts/TestResults/node_modules/.vs/packages` 任意层。doc-freeze 后已 regen。
- `SHA256SUMS.txt`:224 entries covering MANIFEST、所有 stdout/stderr/exit、24 PNG、nupkg 提取物、nuspec、NOTICE.bin、SOURCE-*、NOTES.md、verification-summary.json、final-pack-audit.{json,txt}、`.gitattributes`(`* -text` byte-preservation marker)— 完整目录除自身外全部 payload,0 drift 0 missing。
- `NOTICE-sha256.txt`:仓库根 NOTICE sha256 `58905395...`。
- `final-pack-audit.{json,txt}`:nupkg members/deps/NOTICE byte 比对(pkg NOTICE = 仓库 NOTICE byte-identical)。
- `verification-summary.json`:全部 final 事实 + `outstanding=[]` + `final_state=complete`,不参与自身 SHA256SUMS 循环。
- `.gitattributes`(每个新证据目录):`* -text` — 防 autocrlf 修改 raw bytes 使 SHA 失效;`git check-attr` 验证 `text: unset`。

## 验证环境

Windows x64 · .NET 10.0.11 · OpenGL 3.3 Core · Intel RaptorLake-S · 监视器 DPI 1.5;offscreen `UiRuntime.SetViewport` 覆盖 ps=1/1.5/2 的真实物理像素渲染,非虚报 monitor DPI。
