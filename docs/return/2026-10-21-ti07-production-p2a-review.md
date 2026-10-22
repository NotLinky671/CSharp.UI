# TI-07 P2a：权威编辑适配审核

审核日期：2026-10-21

状态：R1～R7 返修后，Codex 独立最终复审通过 P2a；放行 P2b 的第一批协议实现。P2a 不是原生 TSF 或 TI-07 整体验收。

依据：[正式接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)。以下针对当前适配器源码，不是已接通原生 TSF 的生产验收。

## R1（P1）：回调没有可校验的组合/操作身份

`TextInputCompositionSession.Begin/UpdatePreedit/CommitResult/Cancel/End/SetNativeSelection` 的写入接口没有调用者携带的组合身份或期望修订号。适配器每次自行生成新的核心 sequence，会把重复/迟到原生回调包装成新事件。

特别是 `CommitResult("你", "hao")` 保持组合活动；同一原生确认被再次交付时，同一次调用可再次写绑定。旧组合结束后 Begin 新组合，再到达旧结果同样会被套上新组合身份。不能用字符串或时间去重。

返修：为原生回调提供明确捕获的组合与操作 token/修订校验，或等价的不可复用组合对象与单调事件票据；旧组合、重复已处理确认和乱序操作必须在核心写入前拒绝。普通的两次合法相同结果仍可分别提交，不能按内容猜测。测试覆盖分段确认重放及同 epoch 新组合中的旧结果/取消。

## R2（P1）：Refresh 将旧预编辑拼入新权威文档

`Refresh` 无条件 ReadDocument，再更新 `_expectedVersion`，却继续使用本地 `_composing/_preedit/_replacement` 建快照。外部绑定变化会由核心同步取消组合并撤销 epoch；适配器仍可能返回 `IsComposing=true` 和旧预编辑，且修订号不推进。这样供未来 TSF 读取的文档与真实 TextField 不一致。

返修：Refresh 必须检查闭合/归属/版本和核心组合状态，不能通过刷新消除活动段的版本冲突。失效后撤销预编辑镜像并按实际变化推进修订；关闭后不得再读取客户端。覆盖外部绑定变化、失焦、核心本地取消、只改选区、Close 后查询。

## R3（P2）：不支持的原生选区被静默折叠

`SetNativeSelection` 只要任一端在预编辑里，就只用 active 端更新 caret 并报告 Applied，丢弃 anchor；跨越已提交文本/预编辑的范围也会被夹取成组合内 caret。

返修：保留原生选区真实范围/方向并与核心能力正确映射；若本批只能支持预编辑内折叠 caret，对非折叠/跨区选区显式拒绝并保持快照不变，报告支持边界。不能把未执行的选区设置称为成功。

## R4（P2）：不可变快照暴露了可写数组

`TextInputAuthoritativeSnapshot.Segments` 返回 `_segments` 数组作为 IReadOnlyList，消费者可以向下转型为 TextRange[] 并修改已发布快照。防御性复制输入不能防止输出突变。

返修：不可变集合或只读包装，覆盖修改输入列表、尝试修改输出和旧快照在后续更新后的稳定性。

## R5（P1）：选区更新在绑定回读后没有重新校验归属

核心 `TextFieldCompositionClient.TrySetCommittedSelection` 仅在回读前检查 IsReleased，CurrentDocument 会调用用户 Binding getter。getter 同步切换焦点后，旧客户端已经释放，但后续只检查版本/EpochInvalidationRequested，仍可返回 true 并修改旧节点选区。

Codex 使用真实 TextField、getter 内 `Runtime.SetFocus(null)` 独立复现：旧客户端的选区设置被接受。返修在每次可重入外部回读后、写入前重新校验 IsReleased、当前焦点租约/所有者、epoch、版本及非组合状态；测试 getter 内失焦/换焦点/卸载，不能只测 setter 重入。

## R6（P1）：回读失败后仍可能投递写事件

`ReadDocument` 捕获异常后设置 `_stale=true` 并返回旧快照；调用它的 Begin/Update/Commit 等路径随后只比较版本。若旧快照版本与 `_expectedVersion` 相同，仍会进入 ApplyEvent，未在回读后拒绝失效状态。

返修：每次可重入读回完成后必须再次检查读回是否成功、当前归属/epoch/关闭及组合状态；失败不能继续用最后快照确认写入。使用构造后 Document getter 开始抛错的客户端替身，断言各写操作零 ApplyComposition 调用、返回显式拒绝；真实 getter 内撤销归属由 R5 覆盖。

## R7（P1）：写入之后回读失败不能抹掉已确认的提交

R6 的拒绝必须区分“事件投递之前读取失败”和“ApplyComposition 已返回 Handled/ResultCommitted 后才读取失败”。当前 CommitResult 与 CompleteAfterApply 的后置读取失败路径调用 `TextInputAdapterResult.Rejected`，把已返回的真实 ApplyResult 替换为 default，使 Handled/Committed 变为 false。未来宿主会据此错误释放字符回声，可能重复插入。

返修：读前失败保持零投递；写后读取失败/归属变化则标记会话失效、撤销不可信镜像，但保留已经确认的 ApplyResult（包括真实 Document、Handled、ResultCommitted 与已执行前缀），明确表达“已执行动作，后续同步失败”，不能假称未执行。测试客户端在返回成功结果之后使 Document getter 抛错，以及真实 setter 内换焦点；断言唯一写入、Handled 不丢失、旧票据退休、后续操作被拒绝。R6 不要求否认已发生的事实。

## 独立复现与后续审核

Codex 的 `artifacts/ime-work/p2a-audit` 已在真实 UiRuntime/TextField 上复现 R1～R5；初始输出和源码保存在[审核证据](2026-10-21-ti07-production-p2a-review-evidence/)。第五项输出为 `selection accepted after getter revoked focused client`，不是仅静态推断。

票据接口返修后的独立中间复测：原先四项均通过；新增检查仍复现 R5、R6 和 R2 的桥归属撤销分支，共 3 失败。R6 使用允许的抛错客户端替身，实际输出 `rejected=False, applies=1`；R2 桥归属谓词改为 false 后 Refresh 仍保留组合镜像。见 `partial-repair.log`。此为返修中结果，尚未最终放行。

DSH 完成本轮、停止变异验证后，Codex 从稳定源码重建生产及独立审计并复测：原先七条断言场景均通过，只有 R7 仍失败，实际 `writes=1, handled=False, committed=False`。证据 `pre-r7-final.log`。此前 `post-commit-check.log` 是并发变异验证期间的中间构建，不作为最终 R6 返修结论。当前最终阻断项是 R7。

Codex 将独立运行针对上述路径的复现与返修验收。通过前不得勾选 P2 或启用 TSF/原生候选接线；仅测试数量全绿不等于契约正确。

## 最终独立复审通过

R1～R7 已关闭。R7 新增明确的 AppliedSynchronizationFailed，保留核心确认的 ApplyResult/Document/Handled/ResultCommitted，撤销不可信预编辑并退休票据；未把后置同步失败伪装成零写入。

Codex 在 DSH 停止所有变异验证后，从稳定源码重新构建并独立验证：

| 检查 | Debug | Release |
|---|---|---|
| CSharp.UI.slnx 构建 | 通过，0 错误 | 通过，0 错误 |
| 独立八场景审计 | 8/8 | 8/8 |
| 核心测试 | 545/545 | 545/545 |
| 宿主/图形/输入测试 | 380/380 | 380/380 |
| 正式回归合计 | 925/925 | 925/925 |

上述测试退出码均为 0，无跳过。构建各有 3 条既有 xUnit2031 告警，位置为未修改的 TextFieldCaretAlignmentTests 两处及 FlexIntegrationTests 一处；不在本批扩大修改。文本与独立集成测试未因本批重复执行，P1 的结果作为未改模块依据。

构建仍使用 `--no-restore -m:1 -nr:false -p:NuGetAudit=false`；正式测试直接执行生成 DLL，参数 `-noLogo -noColor -parallel none -xml <路径>`。八场景审计的项目按 Configuration 引用对应 Debug/Release 的生产 DLL，避免仅更改审计输出目录却仍测 Debug。完整构建/测试日志、XML、审计源码与最终 SHA256 见[证据目录](2026-10-21-ti07-production-p2a-review-evidence/)。

本批冻结边界：内部选择接缝不改公开 ITextInputClient；预编辑内仅支持折叠 caret，非折叠/跨区选区明确拒绝；显示镜像坐标转换不代表已实现原生 store/range 协议。无 COM store、原生组合分类、默认宿主切换或候选 OpenGL 接线。本次只接受 P2a，P2 总框仍未完成，下一批按补充计划先实现协议。
