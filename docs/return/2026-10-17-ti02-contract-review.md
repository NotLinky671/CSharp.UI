# TI-02 编辑与输入契约首轮审核

审核日期：2026-10-17

状态：发现问题，待DSH返修；不允许冻结接口或提交实现。

依据：[计划](../plan/2026-10-17-text-input-and-editing-plan.md)、[TI-02](../task/02-editing-and-input-contracts.md)。用户已免除微软拼音测试，不安装、启用或测试它。以下均为无窗口CPU问题。

独立复现：使用本轮已编译核心DLL的外部console调用真实契约；输出见[text-input-and-editing-review-evidence/codex-ti02-contract-audit-initial.log](2026-10-17-text-input-and-editing-review-evidence/codex-ti02-contract-audit-initial.log)。最终定位须以返修后的源码行号为准。

## R1 [P1] 跨消息更新被拒绝，组合顺序不能跨批次跟踪

位置：`src/CSharp.UI/Editing/TextInputContracts.cs`，`CompositionBatchValidator.Validate`。

复现：第一次Validate传Start(session12, composition0, v0, seq0, "n")；第二次传activeCompositionId=0、上次report的open/ended集合及Update(session12, composition0, v0, seq1, "ni")，结果为Invalid。实现每次把lastSequence清为-1，拒绝批次首Update/End，忽略已有活动组合；跨批次重复/递减序号也没有可靠游标。真实组合通常分属多条消息，不能要求每条消息带Start。

返修：保持精简；可以将状态性批次判定移交TI-04，仅在TI-02冻结正确契约，或引入明确、不可变且有界的会话游标。不要要求调用方累积整个组合历史，也不要永久保留不断增长的ended ID集合。补真实分批Start→Update→Update→Result→End及重复/乱序回归；无Start结果仍合法，已确认分段与下一段预编辑顺序明确。

## R2 [P1] 非匹配版本与越界载荷被接受

位置：同上及`CompositionEvent`工厂/验证。

复现：文档v0，Result事件v10，被判Accepted；空文档上Start replacement=[20,30)、preedit="n"、caret=99，同样Accepted。现判定仅拒绝小于当前的版本，且只校验身份，不校验范围/caret/segments/枚举。版本必须锚定明确快照，不存在的未来版本不能用于写入；同消息结果后的新段要有明确重基规则，不能靠放过所有未来版本解决。

返修：定义有效载荷验证边界（无异常失败、不可拆代理/字素的规则），消费者可明确验证replacement、选区、caret、分段范围和未知事件类型。默认/with构造也不能绕过有效性。补未来版本、越界和未知枚举以及正常结果后新段的测试。

## R3 [P1] 回退报告未执行的动作且无焦点时仍要求重投

位置：`src/CSharp.UI/Editing/TextInputFallback.cs`，`NoTextInputService.ApplyComposition`、`TextInputForwarder.ApplyComposition`。

复现：无组合能力服务收到Result，返回Outcome含ResultCommitted，同时Handled=false。实现把请求阶段映射成已执行动作。相同问题波及Started/Updated等。调用方依动作位选择字符所有权，会误以为已经写入。另一个独立风险是有组合服务但ActiveClient=null时RetryAsCommittedText=true，可能将已失去归属的结果重投到新焦点。

返修：请求阶段与实际动作必须区分。未执行只报告拒绝/未处理；仅在有明确当前归属、确有已提交结果且无组合能力时允许字符回退。过时、重复、无客户端的事件不可简单重投到当前焦点。用两个客户端验证旧结果在切焦点/取消回调期间不串写。

## R4 [P2] 组合载荷并非不可变

位置：`CompositionEvent.Segments`及其他公开init属性、`CompositionBatchReport`集合。

复现：Start工厂虽复制输入segments，但返回对象可把Segments转回TextRange[]并修改；复现将[0,1)改成[30,40)，事件内容随之改变。公开init也能直接赋外部可变list/null；入队后可能改变数据，破坏当场复制与序号验证。

返修：封装并真正冻结集合，清理不必要的公开init，保持小型公开面。补源数组、取出集合和with路径的快照不变性测试。

## R5 [P2] 默认值与快照/结果不变量未统一

位置：`TextSnapshot`、`EditOperation`、`EditResult`。

复现：default(TextSnapshot).Length抛NullReferenceException。当前readonly struct不可避免存在default；其他含string默认值也有相同风险。EditResult重复保存DocumentVersion却未约束等于After.Version；Accepted未确保版本和真实变更相符，可能制造冲突快照。

返修：明确default的安全空值或显式无效值语义；公共查询和失败返回不发生空引用，统一版本和快照不变量。将用户原文从默认ToString诊断中去掉，遵守计划不把用户文本写日志的边界。补默认/无效结果/矛盾版本回归。

## 后续审核

修复后运行核心完整测试、解决方案构建和必要兼容回归，记录实际命令/退出码/数量。Binding来源、setter回读、MaxLength及历史配置还须有明确契约记录。DSH不得修改此Codex审核文档，不提交/推送，等待Codex复审。

独立首轮核心回归：dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll，exit 0；363项通过、0失败/跳过。输出见配套证据codex-ti02-core-initial.log。该通过结果与上述6条独立复现输出同时成立，说明现有测试尚未覆盖所列边界。

## R6 [P2] Binding来源策略仍未冻结，记录含错误推论

TI-02实施记录将Source==null标作“来源未识别”并把后果留TI-03决定，与本任务要求冻结来源识别相冲突。明确委托绑定复用同实例与每次重建创建新实例的默认语义，例如内部身份用Source??binding自身；无法证明同来源的新委托实例按来源更换处理，文档说明需要State或缓存绑定以保持身份，不用字符串相等猜来源。Select的身份策略也必须明确，不要求为此新增庞大公开API。

同值setter短路不代表正常自身编辑不会重建：从旧文本写成新文本会NotifyDependents；稳定来源和事务确认才是保留历史的依据。请修正实施记录。MaxLength/历史的测试若只是新写一份公式的参考模型，不能当实际编辑实现验收；移至TI03/TI05真实消费方接入后验证，当前只记录契约。

### R1/R2 复核后的范围决定

当前返修的CompositionCursor/BatchValidator仍会预判每个Result使版本+1，而TI03绑定拒绝、净化后无变化、MaxLength全拒绝并不递增；保留最近若干ended ID也会放过被淘汰旧ID重放。这再次表明状态判定必须和真实事务结果共同实现。Codex决定：TI02移除过早公开的Cursor/BatchValidator/Report，将实际状态判定归TI04。本任务保留事件值、不可变载荷校验、会话与同步结果契约，并明确TI04责任；不继续在契约任务堆叠状态机或伪称其完成。

## 第二轮独立复核：仍需关闭的边界

独立构建exit0，核心363、Text67、Integration30通过。OpenTK沙箱242中1个既有剪贴板往返失败，需非受限环境重跑，不伪报通过。第二轮复现见配套证据codex-ti02-contract-audit-round2.log。

- R2：`new TextSnapshot("😀", caret0)`上Result replacement=[1,1)仍TryValidatePayload(document)=true，会把一个代理对从中间拆开。完整快照重载应验证replacement及组合前选区端点为字素边界；长度重载只能检查长度，文档明确其不能保证字素安全。组合内caret/segments允许UTF16内部位置，后续排版映射负责，不混淆两种坐标。
- R5：`EditOperation.Delete(default,(TextEditKind)999)`绕过TryCreate校验；factory应与构造规则一致，不让未知Kind/Reason通过；公开init的无效with也需要明确验证入口供TI03消费，不仅factory看起来有效。
- R5：Rejected结果允许before="a",after="b",二者v0，和当前“拒绝不改变文档”的承诺矛盾；应拒绝这个构造。Binding转换后的重同步不应伪造Rejected同版本不同文档，留TI03明确同步/结果语义。
- R5：default(EditResult).AcceptedText仍为null，非nullable公开属性应安全返回空串；default Status=None明确表示无效/未执行，补测试。

附文档澄清：default(CompositionEvent)等价空Start(0)是当前已文档化且测试的合法空载荷，Codex接受这个形状语义；不能再写成默认Kind未定义或一定拒绝。TryValidatePayload负documentLength属于调用者参数错误可抛，避免笼统声称所有错误都不抛。

清理未使用或错误的公开`TextInputState.DisplayText`拼接（现在总是document+preedit，并非替换区显示串）；显示构造归TI04/TI06，此阶段不提供误导性快捷属性。修正Sequence说明为同一组合跨消息递增。

OpenTK独立非受限重跑exit0：242通过、0失败/跳过，日志codex-ti02-opentk-unrestricted.log。原沙箱剪贴板失败仍保留证据；该轮四套合计702通过。最后边界返修后需重新构建核心并运行对应回归。

## 最终复审结论：TI-02通过

R1～R6及第二轮边界均关闭。删除提前实现的CompositionBatchValidator/Report/Cursor，实际跨消息状态机、序号去重和事务后重基明确由TI04实现；这不是该功能已完成的证据。平台无关契约、会话/能力/同步结果、绑定来源身份、事件不可变载荷、字素范围检查与旧API兼容通过本任务审核。

独立验证（各自轮次，非声称全部一次运行）：

- 最终解决方案Debug cached build：exit0，0错误/警告；`codex-ti02-build-final.log`。
- 最终核心：369通过、0失败/跳过，exit0；`codex-ti02-core-final.log`。
- Text67、Integration30：exit0；`codex-ti02-CSharp.UI.Text.Tests.log`、`codex-ti02-CSharp.UI.Integration.Tests.log`。
- OpenTK242：非受限环境exit0、0失败/跳过；`codex-ti02-opentk-unrestricted.log`。沙箱剪贴板失败日志保留，不掩盖首次失败。
- 最终独立定向调用14项全部PASS，exit0；`codex-ti02-contract-audit-final.log`与同名前缀源码txt。初始复现代码/日志保留作对照。

计数合计708（369+67+30+242），较原639增加69项；没有删除/放宽旧断言。最终边界修改影响核心，其他三套是该轮之前复跑，最终解决方案已重建。关键证据位于同日期text-input-and-editing-review-evidence目录。

不宣称完整IME、预编辑绘制、实际编辑事务、撤销重做或候选窗已完成。微软拼音按用户指示未再启用或测试；当前输入法真实宿主验收仍归TI07/TI10。允许DSH只对已审核TI01工具/证据、TI02代码/测试及相应文档英文提交并推送，禁止顺带提交artifacts或无关改动。提交完成后方可交付该里程碑。
