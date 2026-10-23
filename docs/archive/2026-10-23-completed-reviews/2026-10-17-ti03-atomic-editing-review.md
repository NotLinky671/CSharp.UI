# TI-03 原子编辑与绑定同步审核

审核日期：2026-10-17

状态：实施与审核中。

依据：[TI-03任务](../../task/03-atomic-editing-and-binding.md)。Microsoft Pinyin按用户要求不测试；本轮先验证平台无关实际TextField编辑路径。

## 实施前独立复现

实际调用现有已编译核心DLL的TextField输入路径，使用无窗口UiRuntime及绑定setter替身，测试代码与输出保存在[证据目录](2026-10-17-ti03-atomic-editing-review-evidence/)。

命令：`dotnet exec artifacts/ime-work/ti03-audit/bin/Debug/net10.0/Audit.dll`；exit1。

- 拒绝插入：绑定保持abc，但caret错误前移到4。
- 随后拒绝退格：绑定仍abc，但caret前移留下的错误进一步影响删除定位。
- setter写前抛错：caret已被修改，异常后不一致。
- 转换回读、纯净化空操作、行首退格基线检查通过；这些通过不表示事务模型已实现。

这些是TI03应修复的已确认目标，最终须重新运行相同实际输入检查。当前不预先放行，不提交实现。

## 第一版代码独立审核：R1 [P1] 生命周期与异常后的编辑状态未同步

当前基础定向7项已通过：拒绝插入/删除和写前抛错不再提前移动caret。以下三个独立实际TextField场景仍失败（见codex-sync-audit-initial.log及对应源码txt）：

1. 绑定实例固定，初始abcdefgh，caret=8，应用外部把值改为x，然后InvalidateLayout+BuildFrame；画面读到x，Editor.Text仍abcdefgh、Caret仍8。没有新描述时OnUpdate不执行；Measure/GetLayout只读绑定却没有同步会话。
2. 初始abcdefgh，setter实际写成x然后抛异常；Editor已回读x，但TextField.ApplyEdit直接退出，不限制真实state.Caret/Anchor也不使布局/渲染失效。后续BuildFrame仍caret8，只靠绘制夹取隐藏了错误。
3. setter写成x后使getter暂时抛错，异常后恢复getter并InvalidateLayout+BuildFrame；RequiresResync和旧Editor.Text/选区没有在下次成功读取时恢复。

要求：所有成功观测实际绑定值的安全生命周期/输入入口都能同步session与选区，保持字素边界并保证caret可见；不能在Build/Measure中写Binding或引入外部副作用。OnUpdate并不是唯一同步点。setter/回读异常路径标记组合/历史失效，确保下次可读会恢复，不能依赖必须先发生另一次成功编辑。恢复不能掩盖原异常或再次读取抛错覆盖原异常；不把候选当事实。

## R2 [P1] 来源/配置变化与重入事务的归属

代码检查：OnUpdate只比较IsOwnPendingWrite(text)就直接return，忽略当前Binding.Identity和MaxLength。setter同步触发重建时若描述换成相同文本的另一来源或改变MaxLength，会被错误当成自身确认；Apply返回后仍将旧binding结果/selection写入新描述。需要用真实同值来源替换、配置改变与setter同步重建的实际输入测试复核，事务结束时必须重新核对挂载状态和来源/配置，不能串写新客户端。普通同来源同配置重建应不误清历史/推进两次版本。

## R3 [P2] 统一事务的字素边界与确认选区

TextEditSession.Apply目前只阻止替换范围拆代理对，不阻止拆组合附加符/ZWJ；已有TI02 TextGraphemeBoundaries可复用。转换后的afterSelection只Math.Clamp，返回EditResult可含字素内部位置，即使TextField随后Snap也已与结果快照不一致。统一执行器应对真实输入文本/替换范围和确认后选区保持字素不变量，为TI04/TI05提供可用事务，而非只有当前TextField辅助函数恰好吸附。

当前为实施中第一版发现，DSH完成时需自查上述项并补实际集成回归；不提交、不提前通过。微软拼音不测试。

R2独立复现已确认：固定oldBinding初始abc，在其setter接收abcX后同步切换组件状态到newBinding（值NEW）并ProcessPendingWork；调用返回后实际新绑定为NEW，但Editor.Text=abcX且Caret=4。输出见codex-source-audit-initial.log及对应源码txt。旧事务不能在来源已被替换后覆盖新会话与选区。需要集成测试验证，不仅检查Sync的helper。

## 第二轮独立复测

原R1三个独立同步场景已通过，日志codex-sync-audit-round2.log。真实source replacement的Unicode字素变体通过。原R2/R3仍须完整核心回归与代码复核后关闭。

R1残留 [P2]：实际键盘读取没有SyncEditor。初始固定委托绑定abc，外部改值x，DispatchKey(End)+BuildFrame（只触发render，没有重新布局）：Editor.Text仍abc，实际绑定和画面为x。OnKeyDown只读Text.Read，不同步会话；纯重绘的Emit/GetLayout也没有同步。证据codex-input-sync-audit-initial.log及源码txt。请在实际键盘/文本输入及安全重绘观测入口补齐同步，避免版本在后续组合/历史中滞后；同步不应递归GetLayout或持续使布局失效。

额外验证：setter内部Dispose运行时未抛二次异常、未复活已清空InteractionState，当前样例通过；不因没有独立测例就凭猜测阻塞这一项。

R1纯重绘场景补充 [P2]：固定绑定200个a，视口180宽，End后ScrollX=1520。外部缩短成x，只InvalidateRender+BuildFrame，ScrollX仍1520，新文字完全移出可见区域。最新SyncEditor的knownLayout路径虽同步了Caret，但未限制ScrollX/确保caret可见；Emit不会再Arrange。请复用本次layout/inner rect计算横向滚动，不递归重排、不每帧持续失效。不以绘制caret夹取掩盖错误，见codex-render-scroll-audit-initial.log和源码txt；加实际runtime回归。原任务要求文字caret和横向滚动保持，此项属R1同一安全读取同步问题。

## 最终独立复审：TI-03通过

审核日期：2026-10-17。R1、R2、R3及第二轮键盘/纯重绘/横向滚动补充均已关闭。

结论：TextField的普通输入、退格/删除、选区替换、剪切/粘贴已统一为原子替换通道；一次写绑定、回读确认后提交选区与版本。拒绝、无变化、异常、规范化、外部文本/来源/MaxLength变化有明确结果；真实字素范围校验与确认后选区保持一致。正常组件重建和Flow/Adaptive保留状态。Render-only同步复用既有layout，调整真实ScrollX，不递归重排或持续使布局失效。

独立验证（来自相应轮次；不声称全部一次执行）：

- 最终解决方案Debug缓存构建：exit0，0错误/警告，`codex-build-final.log`。缓存0警告不表示全量重编译没有既有告警；DSH记录TI02既有5条nullable告警未在本任务修改。
- 最终核心：405通过、0失败/跳过，exit0，`codex-core-final.log`。
- Text67、Integration30：exit0，`codex-text.log`、`codex-integration.log`；均在最终滚动小改动前独立复跑，最终解决方案已重建。
- 最终OpenTK242：exit0、0失败/跳过，`codex-opentk-final.log`。包含真实隐藏窗口/OpenGL与剪贴板回归，不能统称无窗口；没有输入法安装/启用/测试。
- 独立实际运行时定向检查：Basic7、Sync3、Source1、InputSync2、Render1，合计14项PASS。`codex-final-*.log`及对应源码txt持久保存；每组使用引用当前已编译核心DLL的外部console单独Rebuild后执行，避免切换源码时增量缓存漏编译。尤其Render输出ScrollX 1520→0，来源替换输出新binding与Editor同为NEW。

四套回归计数合计744（405+67+30+242），较TI02的708新增36项。保留所有原失败证据与旧断言；没有删除或放宽基线测试。

本任务仅交付原子编辑与Binding同步，组合状态机/预编辑/撤销重做/原生IME桥仍归TI04～TI10；微软拼音按用户要求不测试。允许DSH对本任务已审核代码、测试、任务索引及审核证据英文提交并推送；不得顺带提交artifacts、bin/obj或无关改动。

## 提交与推送

DSH按授权创建英文提交`bc38eb8`（Unify TextField edits with atomic binding transactions），工作区干净。DSH沙箱Git凭据助手因命名管道权限失败而无法推送；Codex通过正常授权环境执行`git push origin main`成功，远程由`9efd2d0`推进到`bc38eb8`。没有更改凭据配置、force/reset/rebase，未在审核后修改实现。TI03里程碑已落地，TI04～TI10仍待实施。
