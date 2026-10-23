# TI-05 撤销重做审核

审核日期：2026-10-19

状态：2026-10-19最终独立复审通过；R1–R3及追加字素边界问题已解决，允许DSH按工作流提交和推送。下文保留早期问题与返修证据，最终结论见文末。

依据：[TI-05及2026-10-19语义补充](../2026-10-23-text-input-editing-tasks/05-undo-redo-and-edit-commands.md)。只检查核心真实运行时，微软拼音继续免测，不改系统输入法配置。

## 首轮实测

外部Console直接引用DSH本轮已编译核心DLL，驱动真实UiRuntime、TextField、注入时钟/输入服务/脚本Binding，经公开键盘和文本分发执行45项检查：38通过、7失败，退出码1。源码、完整输出及DLL哈希在同名-evidence目录的codex-initial-runtime.cs.txt、codex-initial-runtime.log、codex-initial-core-sha256.txt。原有合并时间边界、反向选区、redo分叉、容量拒绝、分段IME、命令token、AltGr及100条淘汰已通过所测场景；不能据此宣称整个任务完成。

## R1 [P1] 跨事务形成字素后逆操作范围失效

TextField.BuildHistoryEntry直接保存operation.Range和插入片段。先输入e，超过500ms后输入组合音标U+0301，文档成为一个完整字素é，而第二项逆操作范围[1,2)落在新字素内部，TextEditSession.Apply拒绝该范围。Undo后仍为é，原有e也无法继续撤销。emoji分别输入“👩”、ZWJ、“💻”同样无法逐项恢复。

必须构造在编辑前后文档都合法的字素边界差量，必要时扩展保存受影响片段并计入预算；不能禁用核心字素校验、拆字写入、整份快照隐藏保留，或静默丢失可撤销操作。2026-10-19补充文档提交ca7b77c已明确此边界。增加超时及合并窗口内的组合字符/ZWJ回归，重做也需准确。

## R2 [P1] setter拒绝Undo/Redo后未清理不可信历史

ReplayHistory在!result.Changed分支直接返回，未处理LastTransaction=SetterRefused。真实输入a后让setter拒绝Undo，文档a未变；重新允许setter，再次Undo/Redo仍会使用旧记录并调用setter。任务已明确setter拒绝/转换/异常应清理不可信历史，不能把容量拒绝/空操作与setter拒绝混为一谈。

按结构化事务结论区分，拒绝的Undo/Redo不能谎报成功、移动到另一栈或恢复错误选区；清理两栈后再次执行应无写入。普通编辑容量全拒绝/真正空操作仍须保留redo。补Undo与Redo双向拒绝测试。

## R3 [P1] 历史命令前getter失败后旧历史仍可重放

OnKeyDown先SyncEditor，此处getter抛错发生在ReplayHistory的try/catch之前，旧历史未清理。真实输入a后使getter持续抛错，执行Undo并捕获异常，恢复getter后再次Undo/Redo仍会写Binding，a被旧记录撤销，违背异常后清理不可信历史的约定。

需覆盖所有真实读取入口（普通命令前同步、组合客户端Document/GetState回读、事务异常回读等）的历史失效传播，不能只在ReplayHistory内部读取失败时Clear。历史消费与组合失效独立，不能清除组合消费者标志；仅提供未使用的ConsumeHistoryInvalidation或依赖LastTransaction不够覆盖事务之外的读错。普通UI入口是否重抛沿用既有契约，但异常后两栈必须实际失效，原生组合回调不能异常越界。增加真实客户端、真实命令分发回归。

第二次实测另加入ITextInputClient.Document读取失败：输入a后让getter抛错，调用实际client.Document（客户端安全返回缓存），恢复getter后执行Undo/Redo仍重放旧历史，确认事务之外的客户端读取异常也漏清理。当前46项中38通过、8失败；其余失败与首轮一致，见codex-runtime-r2.log及同名cs.txt。不能仅修OnKeyDown的catch而遗漏此入口。

## 返修要求

### R1返修复测：双文档边界仍有一个实际失败

第三轮独立检查49项中48通过、1失败（codex-runtime-r3.log及同名cs.txt）。原先组合附加符/ZWJ、setter拒绝、命令前及客户端getter异常已修复。剩余R1：初始文本“🇦🇧”，Home后输入单个区域指示符“🇽”，结果“🇽🇦🇧”成功；Undo仍保持该结果，未恢复“🇦🇧”。这是核心Unicode分组行为，不涉及emoji图形支持。

BuildHistoryEntry当前只把范围扩大到afterText字素边界，再映射beforeText。这个例子中after的首字素是“🇽🇦”，映射出的before终点却落在原“🇦🇧”内部，valid=false后回退到原始范围[0,2)，该逆操作在after仍非法。合法普通编辑即可触发，不能以“正常编辑不应走到这里”回退。应持续扩大到前后文档对应的共同合法边界（必要时跨越多个重组字素），保存真正局部的前后片段并计预算，绝不回退存储已知无效的逆范围。补区域指示符在前/中间插入或删除引起重新分组的实际TextField回归；保留已通过案例。

DSH继续实现及补测试，完成后等待Codex复测；本记录及证据由Codex维护，未经明确审核通过不提交实现。保留TI-04全部基线及其生命周期/epoch/key token/回调不排版边界；不要将首轮已通过的38项当作全部验收。

## 最终独立复审结论（2026-10-19）

通过。R1在前后文档共同字素边界上构建必要局部差量，区域指示符重新配对也可正确撤销；R2拒绝的Undo/Redo实际清理两栈；R3命令和真实组合客户端读取失败后均使旧历史失效。最终外部Console真实运行时验收49项全部通过，见codex-runtime-r4.log及同名cs.txt；TI-04原有独立运行时41项也全部通过，见codex-ti04-compatibility.log及源码副本。DLL哈希见codex-final-audit-binaries.txt。

Codex最终执行结果：

| 检查 | 结果 |
|---|---|
| 缓存解决方案构建 --no-restore -m:1 -nodeReuse:false | exit 0，0警告/0错误 |
| 核心测试 | 529通过，0失败/跳过（原467 + 新增TI-05 62） |
| 文本测试 | 67通过，0失败/跳过 |
| 集成测试 | 30通过，0失败/跳过 |
| OpenTK测试 | 242通过，0失败/跳过，实际运行Windows窗口、OpenGL及剪贴板 |
| TI-05独立运行时检查 | 49通过，0失败 |
| TI-04独立兼容性检查 | 41通过，0失败 |

合计868项工程测试，另加90项独立检查。持久证据在同名-evidence目录的codex-final-*.log、codex-runtime-r4.*及codex-ti04-compatibility.*；历史失败日志保留作为返修过程证据，不能据此判断最终状态。

实现边界与取舍：历史为节点局部差量，undo/redo共用条数与文本双预算；扩展到共同字素边界后的真实片段也计入预算。历史失效通过结构化事务结论、Sync外部变化及真实读取异常清理实现；独立ConsumeHistoryInvalidation接缝保留，不破坏TI-03/TI-04粘性观察标志或清除组合标志。原有公开TextField用法不变，容量与快捷键平台注入为内部策略。

macOS/Linux快捷键仅经过核心模拟映射检查，不宣称真机输入验收。未安装、启用或测试微软拼音，未变更系统输入法配置。原生宿主最终输入路由仍属TI-07，预编辑绘制/候选几何属TI-06；整个文本输入计划尚未完成，不创建计划级交付结论。

提交与推送：DSH按批准范围创建实现提交`e4a766bed253f30e2eeaa0c850e0aa704102ecc7`（Implement bounded text editing history and undo redo commands）。DSH沙箱内Git凭据助手出现Win32 error 5，未改凭据、远程或提交历史；Codex通过宿主执行同一条`git push origin main`成功，远程main已从ca7b77c推进至e4a766b。实现提交后工作树干净；后续文档提交仅补记实际推送，不改代码或验收结论。
