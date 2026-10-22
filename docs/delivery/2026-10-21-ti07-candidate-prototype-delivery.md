# TI-07 自绘候选隔离原型验收记录

验收日期：2026-10-21

结论：用户批准的隔离原型路线已通过真实微软拼音验收，滚动跟随、完全裁出隐藏、同组合恢复以及恢复后唯一中文提交均已验证。TI-07 整体尚未完成，OpenTK 正式宿主尚未迁移。本记录只交付隔离路线验证结果。

## 路线与实现

微软拼音继续负责预编辑、组词、候选内容与提交；隔离工具通过 TSF UILess 候选接口取得当前页和选中项，以自己的窗口绘制候选。原生候选窗口隐藏路线没有被宣称修复。

实现位置为 `tools/CSharp.UI.TsfHostCheck` 及对应测试。包括候选读取与 COM 生命周期、唯一活动快照、裁剪显示状态、DPI 字体与文字测量、客户区内摆放，以及默认关闭的场景接线。后台测试线程不会强制取得前台；等待用户点击及手动选择微软拼音后，才允许逐键检查前台与焦点并发送测试输入。

最终 DSH ACP 模型为 `devin/deepseek-v4-1-flash-1017`，`reasoning_effort=high`，两项均收到 ACP 确认。Codex 负责独立审核、运行测试和逐张核对真实截图。

## 验收证据

环境：Windows、Release 可执行文件、专用 STA UI 线程、PMv2、DPI144（150%）。实际微软拼音 profile：lang0804，CLSID81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e，profile fa550b04-5ad7-411f-a5ac-ca038ec515d7。

| 检查 | 实际结果 | 证据 |
|---|---|---|
| 内部滚动跟随 | 文字与候选一起上移11物理像素，再回到原位 | [滚动日志](2026-10-21-ti07-candidate-prototype-delivery-evidence/scroll/tsf-host-check.jsonl)，三张窗口图及本地三张整屏图全部核对 |
| 完全裁出隐藏 | 输入可见交集为空时，窗口及整屏均无文字和候选；实际 IsWindowVisible=false | [裁剪及提交日志](2026-10-21-ti07-candidate-prototype-delivery-evidence/clip-and-commit/tsf-host-check.jsonl)，三阶段窗口图及本地整屏图全部核对 |
| 同组合恢复 | 原 ni'hao/span0..6、候选id0/gen2恢复；测量期 begin1/end0/session1/focus_events1、写入0 | 同上，seq236/282/315及提交前seq318/319 |
| 恢复后中文提交 | 真实 Space down/up各一次；最终文本你好，唯一绑定条目你好，source=tsf-composition；begin1/end1/session1，候选关闭 | 同上，seq320/354、337、362/363；[提交后窗口](2026-10-21-ti07-candidate-prototype-delivery-evidence/clip-and-commit/r37-restored-commit.png) |
| 自动测试 | Codex 独立581/581通过，0失败0跳过 | [测试输出](2026-10-21-ti07-candidate-prototype-delivery-evidence/tests.log) |
| 构建 | Debug测试工程及Release工具构建成功 | [构建输出](2026-10-21-ti07-candidate-prototype-delivery-evidence/build.log)、[Release输出](2026-10-21-ti07-candidate-prototype-delivery-evidence/release-build.log) |

测量及提交期间没有通过取消重输、切断文档焦点或重新创建组合来恢复候选。提交后稳定等待400ms仍只有一条绑定记录。全部截图捕获前后均有对应阶段检查；脚本退出3保留“待视觉审核”的含义，由本记录补上人工视觉结论，没有改写退出码为0。

窗口图与原始日志已复制至同名前缀证据目录，[SHA256清单](2026-10-21-ti07-candidate-prototype-delivery-evidence/evidence-hashes.json)可供复核。整屏图含桌面其他应用，仅保留于本地私有 `artifacts/ime-work/r36-hostdraw-scroll-real` 和 `r37-hostdraw-restored-commit-real`。

## 复现命令

先构建工具及测试，然后直接运行 Release EXE，以使用其 DPI manifest：

```powershell
dotnet build tools/CSharp.UI.TsfHostCheck/CSharp.UI.TsfHostCheck.csproj -c Release --no-restore -m:1 -nr:false
dotnet build tests/CSharp.UI.TsfHostCheck.Tests/CSharp.UI.TsfHostCheck.Tests.csproj --no-restore -m:1 -nr:false
dotnet exec tests/CSharp.UI.TsfHostCheck.Tests/bin/Debug/net10.0/CSharp.UI.TsfHostCheck.Tests.dll
& tools/CSharp.UI.TsfHostCheck/bin/Release/net10.0/CSharp.UI.TsfHostCheck.exe --child-input-surface --host-drawn-candidates --scenario scroll-follow --apartment sta --out artifacts/ime-work/manual-hostdraw-scroll
& tools/CSharp.UI.TsfHostCheck/bin/Release/net10.0/CSharp.UI.TsfHostCheck.exe --child-input-surface --host-drawn-candidates --verify-restored-commit --scenario clip-out --apartment sta --out artifacts/ime-work/manual-hostdraw-clip-commit
```

窗口出现后点击自有窗口并手动选择微软拼音中文模式，保持前台直至自动关闭。界面和输入法未就绪时明确退出，不把环境未就绪当通过。

## 尚未交付的能力

生产 `src` 与保护文件的 SHA256 相对实验前基线无漂移。此次不迁移生产、不修改 TI-07 整体完成状态，也不启动依赖其整体交付的后续任务。

原型仍将总候选数限制为256，输入中间字母n时531项列表会暂时被拒绝，完整nihao对应94项时本次正常；生产接入前应改成按当前页控制分配，而非以总候选数作为容量。鼠标选词、任意小窗口布局、完整输入法及平台矩阵均未在本轮验收。本次不能用来宣称这些能力已可用。

R37最终实现请求超时结束，未形成DSH最终报告；本记录采用实际保存代码、Codex独立构建测试及真实输入日志作为依据，不将超时任务称为正常交付。
