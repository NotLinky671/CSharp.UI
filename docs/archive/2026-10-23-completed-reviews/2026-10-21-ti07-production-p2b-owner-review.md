# TI-07 P2b 原生组合 owner 审核

审核日期：2026-10-21

状态：返修后通过 P2b owner 验收（2026-10-21）。以下保留首批未通过及增量返修的审核历史；最终结论与独立证据见文末。P2 已通过，不代表 P3、P4 或 TI-07 整体完成。

## R1（P1）：End 分类仍按非空文字推断提交

`NativeCompositionPolicy.Classify` 的 End 分支直接检查 `hasActiveComposition && compositionText.Length > 0` 并返回 Commit。这与文档注释声称“不复制非空结束即提交”矛盾，也正是任务明确禁止的原型规则。`EditCompleted` 目前只 Ignore，End 仍立即 Commit，未利用完整原生编辑事务边界区分终止后的删除、恢复或部分结果。

必须以实际完成事务、活动范围与原生最终文档变化建立分类，显式取消须有真实来源，不能让测试自己先声明 Cancel 以绕过真实 OnEnd。测试至少覆盖：End 时范围仍非空但随后同一事务恢复/删除预编辑；选区替换后取消；先 Insert 再 Start；真实 OnEndEdit 到来前零 Binding 写入。OnEndComposition 没有取消/确认标志，不能用注释补出不存在的信号。

## R2（P1）：直接将原生预编辑范围当作已提交替换区

`ApplyPreedit` 在 Begin 时用 `new TextRange(fact.Anchor, fact.End)` 作为 P2a replacement。该范围属于原生显示文档，已包含新插入预编辑长度；已提交文档替换区应来自捕获的原始选区和映射。例如已提交 `ABCD` caret=2，先插入 `nihao` 得到原生 `[2,7)`，真实 replacement 应是 `[2,2)`，而非 `[2,7)`。有后缀、替换选区或部分确认时都会错位。Begin 前需保留权威基线、原生编辑事实和明确坐标转换；不夹取来掩盖错误。

## R3（P1）：溢出与身份缓存未有界处理

首版 `_pending` 虽有 64 项上限，但被删除事实仍留在 `_factOperationIds`；`_operationIds`、`_appliedActions` 也可永久增长。更严重的是 `_needsResync` 为真时 Drain 仍可继续分类剩余事实，违背历史不完整时不能继续推断结果的要求。Staging 自身的溢出同样要纳入可验证边界。需有界身份水位/回放策略，失去完整历史必须停止不可信提交并安全同步，不能只暴露诊断标志。

增量实现将操作身份限制为 256 项，但只在 Dispose 清空，达到上限即永久 `_needsResync=true`。真实 sink 为每个回调生成一个新 key，因此普通连续输入也会很快触顶；这不能作为正常输入的有界缓存设计。已完成操作应由可证明的单调水位拒绝重放并退休身份，或提供安全可恢复的重同步流程，不能要求用户反复失焦/新建 owner 才继续输入。增加超过该上限的连续合法事务测试，证明零重复、零丢失且内存有界。

## R4（P1）：未完成真实事务整合与回读修正

首版没有消费 AcpStagingBuffer，未实现部分确认与剩余预编辑，未将 EditCompleted 形成完整事务事实；`SynchronizeToNative` 只是独立可选调用，成功推进 store generation 后 owner 仍保留旧 observed generation，下一次 Capture 会将自己的同步视为外部失效。最终实现须把一次完成事务的分类、真实 P2a 回读与原生修正纳入同一安全阶段流程，明确自己同步和外部失效的区别。用真实 TextField 集成用例证明连续两轮组合、部分确认、净化/拒绝回读、取消及无组合原生提交可用。

增量注意：无活动组合的完整原生写入也是本批要求，不是要求“store 直接写 Binding”。应经编辑完成分类后走权威编辑事务。现有 `表行6直接非组合原生写入不写绑定且需要resync` 测试固定了相反行为，须修订；独立审计新增该要求的用例，`ABCD` 中间由 TSF 完成插入 `X` 应经事务成为 `ABXCD`，而非永久 resync/丢弃输入。

## R5（P1）：原生 smoke 未执行所声称的组合路径

`TsfCompositionOwnerSmokeTests` 的说明声称验证 GetSelection/GetRange/GetExtent 和真实组合 API，实际只调用 GetActiveView 及对测试 RecordingEditSink 做 Advise/Unadvise；没有 Start/Update/End、没有调用 GetExtent，也没有产品 owner 接收 OnEndEdit。失败的 QI/Advise 还只记 note 后成功退出。产品 NativeCompositionOwner 此时本身也未实现原生 sink，产品中无真实组合/编辑完成回调接线。这不能满足“原生 owner 实现及真实隐藏窗口组合接线”的本批要求。

补齐产品回调适配、连接 cookie/引用生命周期，用真实 TSF 编辑会话调用实际组合 API，断言 Start/Update/End 和 OnEndEdit 确实到达产品实现，并验证真实 ACP 范围/边界；必需调用失败应测试失败。不能只注册一个测试 sink，再用托管 Capture 测试或日志文字替代原生调用。

## R6（P1）：完成编辑时将纯预编辑变化提交为结果

增量返修的首五项独立审计已转绿，但新增完成编辑用例复现：初始已提交 `ABCD`，预编辑 `ni` 同步到原生后，TSF 只把预编辑更新为 `nih`，随后 OnEndEdit。当前 CompleteEdit 对先前含预编辑的 native baseline 做字符串差量，得到新增 `h`，又因它比剩余预编辑短而把 `h` 当作确认结果；真实 Binding 被写成 `ABCDh`。这仍是未完成中文输入时提前提交。

完成事务应依据权威已提交基线、真实活动范围和确认区域分类，不能把两个预编辑快照之间的差量当结果。纯预编辑更新应零 setter、保持同一原生组合对应的票据；不能 End/Begin 偷换活动组合。独立审计第六项可复现，原生修改通过真实产品 SetText 的写锁暂存路径完成。

同一问题还要求在 OnEndEdit 捕获不可变的完成事务文档、选区和活动范围；不能只入队几个布尔标志，在稍后 Drain 时读取已经被后续原生事务改过的全局文档/范围。需覆盖两个完成事务在同一 UI 安全阶段前已排队、连续两个合法相同结果和部分确认后再更新预编辑。

## R7（P1）：原生 sink 注册与释放缺少生命周期守卫

新增 NativeCompositionOwnerSink 的 AdviseEditSink 不校验已释放、所属线程或已有连接；再次注册会覆盖 `_source`/cookie，旧连接未撤销，Dispose 后仍可重新注册而后续 Dispose 直接返回，导致连接泄漏。OnStart/Update/End/OnEndEdit 也未先校验 `_disposed` 和所属线程，就可能调用借用 COM 范围/记录。

为所有实际调用 COM 的注册、撤销、回调和释放入口增加一致的所有权/线程/释放守卫。重复注册要明确拒绝或幂等，不覆盖尚未清理的 cookie；Dispose 后不得新建连接或访问借用范围，错误线程释放须保留 owner 线程清理机会。增加重复注册、撤销后重连、释放后注册、跨线程调用和回调重入清理测试，真实连接资源成对回收。

## R8（P1）：预编辑文字未变化而结束组合时丢失合法结果

独立完成事务审计 `artifacts/ime-work/p2b-owner-terminal-audit/` 已复现：初始 `ABCD` caret=2，原生写成 `ABniCD`，Start + OnEndEdit 后预编辑零 Binding 写入；下一真实事务仅结束组合，原生文字保持 `ABniCD`，再 OnEndEdit。当前实现把“本次事务 delta 为空且无剩余活动范围”认定为取消，最终 Binding 仍为 `ABCD`、组合结束，合法结果 `ni` 丢失。

确认时原生文字不一定再次变化，例如接受未转换的字母或结束已完成转换的范围。分类需同时保留组合前的权威替换基线和逐事务快照，结合终止后最终文档与原替换区的映射，不可仅以最后一个事务是否修改文字区分确认和取消。保持 End 回调本身零提交，完成边界到来后再决策，不能回退为“End 非空即提交”。同一附加审计中的选区替换取消场景当前通过，需保持取消零写入和原选区恢复。证据见配套 `terminal-debug.log` 与 `Program-terminal.cs.txt`。

## R9（P1）：完成事务丢弃原生选区与预编辑内光标变化

独立选区审计 `artifacts/ime-work/p2b-owner-selection-audit/` 经真实产品 ACP 写锁调用 SetSelection，然后 CaptureCompletedEdit(selectionChanged=true,textChanged=false)，复现三项失败：已提交 caret 从4移到1仍保持4；反向选区 anchor3/active1仍保持4/4；`ABnihaoCD` 的预编辑范围[2,7)内原生 caret=3，显示预编辑 caret 应为1，实际始终在5。三项 Binding 写入都是0，但操作被静默丢弃，文字输入位置/光标会与输入法不同步。

当前完成事实只有 SelectionChanged 布尔值，没有冻结原生选区端点和方向，ApplyPreedit 也总把 caret 放在 preedit.Length。需捕获该事务的完整有向原生选区（含 IsSelectionBackward），在安全阶段经已验收 P2a SetNativeSelection 映射到真实 TextField；预编辑内折叠 caret 使用同一票据更新且零 Binding 写入。已有 P2a 明确不支持的跨预编辑区域/非折叠形态应显式拒绝，不能丢弃 anchor 后假装成功。不得读取 Drain 时的较新全局选区替代捕获值。证据 `selection-debug.log` 和 `Program-selection.cs.txt`；该日志另含旧 queued-transactions 回归，与三项选区断言分别记录。

## 验证要求

### 12:30 增量独立复核

当前已编译 Debug 产品的原有十项 owner 审计全部通过，证明排队事务和部分确认票据的两项返修已覆盖。但 R7 仍未解决完整：独立附加审计 `artifacts/ime-work/p2b-owner-lifecycle-audit/` 实际复现两项失败（未改动固定十场景程序）。跨线程调用 `OnStartComposition` 仍读取借用 view 一次、增加回调计数并入队一个事实；跨线程 Dispose 仍设 `_disposed=true`，此后所属线程尝试注册返回 CO_E_OBJNOTCONNECTED，失去继续使用/清理连接的机会。注册/撤销入口的线程校验不能替代回调与 Dispose 自身的校验。

新增的“重复注册”单元测试两次传 null，两次均未建立连接，无法证明真实已持有 cookie 时的幂等、撤销后重连或回调重入释放。请用实际隐藏 context 或受控且真实实现 ITfSource 的连接替身验证这些路径，断言注册与撤销次数、cookie 和释放时机。

13:23 增量源码已将 Unadvise 的 source/cookie 在 COM 调用前摘除并成对释放，方向正确；但新增名为“撤销后重新注册到另一context使用新source与cookie且成对回收”的测试仍两次调用 NoContext(null)，未创建任何真实连接，不能据此关闭上述验证要求。请补真实 contextA→contextB 的连接/撤销成功断言（现有隐藏原生 smoke 可创建第二 context），或受控连接 seam 的实际 cookie/回调计数。不要仅靠测试名称声称已经覆盖。

另有静态可确认的跨平台测试回归：该测试类使用普通无平台 guard 的 Fact，断言释放后注册为 CO_E_OBJNOTCONNECTED、跨线程注册为 RPC_E_WRONG_THREAD、NoContext 为 E_INVALIDARG；但 AdviseEditSink 在全部托管守卫之前即于非 Windows 返回 E_NOINTERFACE，Linux/macOS 上这些断言必然不成立。请在真正的 COM 操作前保留统一托管生命周期/参数检查，或明确区分原生与可移植用例，不让新增 Windows 实现破坏跨平台测试。此项为条件分支静态复核，未声称已在 Linux 实机运行。

R3 新退休表仍有静态缺口：`_completedOperationWatermark` 只有赋值没有读取，退休字典满后整体 Clear。被清除的旧 key 再次投递将重新分配 operation id，不能声称有单调水位拒绝任意旧操作重放。需要来源明确的单调序号/epoch 或等价可证明的有界回放策略；增加身份表多次轮转后重放最早已完成操作的检查。

该问题亦已独立复现：`artifacts/ime-work/p2b-owner-replay-audit/` 先完成一轮组合，再完成 768 次合法独立原生写入，随后重放最早 Start 的同一 `nativeOperationKey` 和范围/文字。当前已编译产品重新开启预编辑（IsComposing=true），而非拒绝退休操作。见 `replay-debug.log`/`Program-replay.cs.txt`。水位必须使用最初捕获的来源身份；仅在 Drain 读取一个水位，但在 Resolve 中仍给已移除旧 key 分配新编号，同样绕过保护。

12:58 契约复核：有界精确内存无法无限保留任意 opaque string key 的身份，因此允许内部迁移为来源分配、重放保持不变的单调 ticket；Codex 已把 replay 审计新增场景迁移为 `sourceTicket`（1/2/3，随后 4+，最终重放 Start 的 ticket1），保留全部行为断言。当前增量源码虽增加可选 `sourceTicket` 参数，但 Capture/CaptureCompletedEdit 仍未将其传入 Resolve；参数存在不等于身份保护生效。还须统一原生与显式取消的 ticket 域，避免两个计数器都从1开始。sink 的 null-range Update fallback 使用 StartCallbacks 生成 key，会在同一组合多个真实 Update 间重用 `update-1`；新身份模型不能把合法后续更新当重放。以上需由实际调用链和有意义的重放 Start/多次 Update 测试验证，不能仅重放一个本来就无文字差量的 CompletedEdit 来证明去重。

13:19 调用契约同步：新源码提供 `AllocateOperationTicket()` 后，Codex 将 replay 审计的新增场景进一步改为通过实际 owner 分配 ticket，并保存 earliestStartTicket 在 768 次合法输入后原样重放。原有十场景和所有行为断言均未改变。这样验证实际生产身份来源，不依赖审计自行模拟编号分配。最终运行须重建审计以复制新的产品 DLL。

R5 本次已移除手动完成 fallback，真实 OnEndEdit 与一次提交是进步；仍需真实 Update 回调和部分确认/剩余预编辑的范围链，不能只用 Start、SetText、End 的最终结果替代。最终日志应列出必需回调计数及范围断言。

原生回调适配预审注意：既有 `ITfCompositionView.GetRange` 的输出是 `IntPtr`，若对该输出调用 `Marshal.GetIUnknownForObject(range)`，取得的是装箱 IntPtr 的 CCW，不是原生范围对象；应直接对返回的原生指针 QI，并成对释放 GetRange 取得的引用。实际 smoke 应能发现此问题。另外 OnUpdateComposition 的 pRangeNew 为 null 可表示范围未变，仍应从 composition view 取得现有范围和本次文字；不能把合法的原范围更新一律标记无效。捕获阶段仍须正确取得文本及选区，不能永远向 owner 传 null 预编辑。

null pRangeNew 并非假想兼容需求：既有真实输入证据 `2026-10-20-ti07-tsf-prototype-review-evidence/activation-scroll-real/tsf-host-check.jsonl` 的 seq99、135，以及多个真实运行日志，都记录了 pRangeNew="(null)"、GetExtent 成功并持续更新预编辑。请保留这个已观察到的目标输入法路径。

连接方式的官方依据已经核实：[ITfContextOwnerCompositionSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfcontextownercompositionsink) 明确写道，TSF 在 CreateContext 时查询传入对象，若该对象支持该接口则安装 sink。因此本项目 CreateContext 传 AcpTextStore 时，该对象必须通过 QI 暴露 ITfContextOwnerCompositionSink 并转发到产品 owner（或提供经验证的等价组合对象）。仅在 CreateContext 后对 context AdviseSink 失败，不能据此推导为隐藏窗口缺少活动文档、需要额外前台激活或 ITfContextOwner。先满足文档明确的 QI 接线，再测回调。参考 notes 同日记录。

原生 smoke 还须检查每次 EndComposition 的 HRESULT，使用 DoEditSession 提供的有效写 cookie，不能在锁外硬编码 `EndComposition(1u)` 后只打印结果。必需的 OnEndEdit 未到达不能 note-and-pass；应在有效写会话后断言产品收到回调、更新/终止范围正确，最终真实 TextField 只提交一次。[StartComposition](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontextcomposition-startcomposition) 的 pSink 是可为 NULL 的 **ITfCompositionSink**，与 context-owner sink 不是同一接口；不能把任意 IUnknown/owner-sink 对象当成该接口传入，以免方法槽位错误。

建议原生 smoke 在有效写编辑会话中经真实 ITfRange.SetText/组合范围 API 驱动文本和范围变更，让 TSF 真实维护范围与编辑记录；不要只在测试中直接调用 store.SetText，随后假定 TSF 已看到相同的范围变化。至少覆盖一次实际 preedit 更新（含合法 null pRangeNew 路径）、真实更新范围/OnEndEdit，以及后续写会话中的结果替换和 End，最终断言真实 Binding 恰好一次及前后缀保留。直接写 store 的托管协议测试仍可保留，但其证据不能替代上述原生路径。

当前增量 smoke 在 `sink.EndEditCallbacks == 0` 时手动 `owner.CaptureCompletedEdit(...)`，随后让测试通过；这正是本项禁止的合成回调替代。必须删除该 fallback，要求真实 OnEndEdit 到达以及真实 Update 回调/范围变化通过。不能一边报告未收到完成事件，一边用手动完成宣称原生唯一提交已验证。

先修正数据模型和真实事件语义，再完成正式测试和原生隐藏窗口接线。不要用手动 Capture 的预分类结果代替真实 COM 回调路径，不引入文本/时间去重。P2b 协议首批已验收的锁/通知独立审计需保持通过；P2a 票据和已确认结果契约也需保持。

独立复现程序：`artifacts/ime-work/p2b-owner-audit/`。结果保存在 [基线日志](2026-10-21-ti07-production-p2b-owner-review-evidence/baseline-debug.log)，源码副本同目录。五项结果分别是：End 尚无完成事务便写入 Binding 一次；中间插入得到 `ABnihao` 丢失 `CD`；替换选区得到 `Anihao` 丢失 `D`；resync-required 状态仍提交一次；自身原生同步导致下一更新被误认为外部失效。

### R9 增量：排队的部分确认＋光标移动吞掉后续最终确认（独立复现失败）

Codex 新增第14个 selection 审计场景，原13个断言保持不变。初始 ABCD、选中[1,3)，进入 nihao 预编辑并安全 Drain；之后捕获部分结果 A你haoD、剩余预编辑 hao 和原生 caret3 的 CompletedEdit；尚未 Drain 就继续捕获最终结果 A你好D、End 和 CompletedEdit。预期 Binding 写入2次、最终 A你好D、组合结束；实际仅写入1次、提交 A你D、仍在组合，RejectedCount=1。证据为 selection-queued-build.log / selection-queued-debug.log，Program-selection-queued.cs.txt。

根因是 CompleteEdit 在 Drain 时为第二个 selection 子动作调用 AllocateOperationTicket，再把该新高编号传入仍有效的 P2a token。后续事实已在捕获阶段拿到较低编号，因此随后合法最终结果被 P2a 当重放拒绝。捕获来源身份与应用动作身份不能混用，尤其不能 Drain 中插入高来源编号使 token 水位超越已排队事件。

请保持来源/native/control ticket 统一域及有界重放保护；可以用原子 CommitResult(nextPreeditCaret) 完成部分结果和光标，也可另外建立按实际安全应用顺序分配的 P2a action 序号。若选择后者，来源事实必须先通过来源重放检查，重复来源不得分配/应用新 core action，core action 序号不得混入来源退休表。保持已验收 P2a 文件不变和全部既有终止/生命周期/原生 smoke 检查。

## P2b owner 最终独立验收：通过（2026-10-21）

DSH queued-selection-repair 交回并正常关闭 ACP 后，Codex 对稳定源码重新构建完整解决方案 Debug/Release，均退出0、零警告零错误；随后重建并执行七组独立审计，再直接执行核心及宿主正式测试程序集。未使用 dotnet test，未降低断言。

| 独立验证 | Debug | Release |
| --- | --- | --- |
| 核心正式回归 | 545/545 | 545/545 |
| OpenTK 正式回归 | 504/504 | 504/504 |
| P2a 权威会话审计 | 8/8 | 8/8 |
| P2b 协议审计 | 12/12 | 12/12 |
| owner 基线审计 | 10/10 | 10/10 |
| lifecycle 包装审计 | 12/12 | 12/12 |
| terminal 包装审计 | 12/12 | 12/12 |
| replay 包装审计 | 11/11 | 11/11 |
| selection 包装审计 | 14/14 | 14/14 |

包装程序重复执行基线十项，因此独立场景按去重后计算：owner19 + P2a8 + protocol12 = 39，不能将所有包装计数相加当作唯一场景数。此次确认排队“部分结果＋caret＋最终结果”两次提交、最终结束，原先只提交一个字的失败已消除。来源 ticket 仅作捕获身份及重放退休；P2a 动作序号按安全应用顺序分配，子动作不再提前消耗未来来源序号。

R1～R9 所列行为已结合静态审查、独立场景与正式测试关闭。真实隐藏 COM smoke 收到 Start1/Update2/End1/OnEndEdit2、rangeFailures0，原生范围(2,5)，预编辑写入0，最终写入1；没有手工 CaptureCompletedEdit fallback。实际 contextA→contextB 均 Advise/Unadvise 成功，重复注册 cookie 不变，撤销后 source/cookie 清空。跨线程/释放守卫与合法 null pRangeNew 路径保留。此前接受的 P2a 五个源文件 SHA256 复核一致。

证据在 [配套目录](2026-10-21-ti07-production-p2b-owner-review-evidence/)：build-Debug.log / build-Release.log、七组审计各配置日志及源码副本、正式测试 XML/日志、source-hashes.json、p2a-hash-check.json。执行脚本 artifacts/ime-work/codex-p2b-owner-final-check.ps1；退出0，最后输出 Independent P2b owner verification complete。

本结论为 Windows 隐藏原生协议及事务验收；不声称 Linux/macOS 实机验证，不替代正式窗口微软拼音验收。P2 可以关闭，接续 P3a（显式 STA、单来源输入路由、真实消息顺序，内部测试开关）、P3b 候选 OpenGL 呈现，最后 P4。默认生产宿主仍为 IMM，TI-07 仍未整体完成；本轮不提交混合工作区，不启动 TI-08。
