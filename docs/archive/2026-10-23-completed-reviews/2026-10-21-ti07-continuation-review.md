# TI-07 接续执行独立审核

## 最新结论：P3a 定向门槛通过，可进入 P3b

主代理在最终冻结源码上串行独立运行 Debug/Release：各 **339 项**（16 个指定测试类）全通过，0错误/0失败/0跳过；各 **8 个审计工程、9 次隐藏宿主运行**全退出0（two-field含safe/unsafe两次）。最终执行任务 `pwsh-cdee5647bb1942c6` 退出0。此结论覆盖下文历史P3a失败；不代表P3b/P4或整个TI-07已通过，不提交推送。

证据冻结于[最终P3a证据](2026-10-21-ti07-continuation-review-evidence/p3a-final/source-hashes.json)，配套stdout/stderr/exit按配置保存。AcpTextStore SHA256仍为已接受P2b的 `7AFC23C64495C5601C79EC43C1B30FCBB13F68EEAAC281EDCE4950BAFC5343EE`，核心ACP协议未为迁就测试而放宽。

关键最终事实：普通/原生连续得到“你x好”且写3次；真实原生取消 Terminate=S_OK、End回调1次、取消写0/store恢复ABCD，下一段精确得到ABCD好且总写1次。必须保留精确文本/次数断言，不能以4项绿灯替代它们：中间一次薄弱测试虽通过，但日志结果是ABCDnihao好nihao、写2次，已被主代理拦下并补回推后修正。

ABI崩溃最终修复不仅替换对象，还将 StartComposition/TakeOwnership 的第三参改为按真正 ITfCompositionSink 接口封送；仅传NullCompositionSink实例但保留IUnknown object仍会崩溃。原生终止同步回调诊断在调用进入前建立归因作用域。取消产生的终结事实经正常批次排空后才兑现待回推，不递归Drain/不清合法pending；成功取消退休自身ticket，负HRESULT不借核心取消结果冒充成功。

最后回退修复：没有可用后端时不虚报StartSession成功并清本次失败归属；默认IMM能力完整透传，不能把尚未完成的TSF候选限制施加给既有IMM；仅第一次惰性TSF启动前保留组合握手，实际失败后回到真实降级能力。

只读复审已撤回缺乏代码依据的“双writer”“无限自激”“必然全局FIFO倒序”推断；真实有界重试依靠自然安全边界，不直接丢取消意图。下一阶段实施UIElement有界读取、UI-less真实激活与同帧OpenGL候选，随后真实微软拼音P4。下文保留全部返修过程。

日期：2026-10-21。用户授权继续完成 TI-07；仅在整体通过后提交推送。沿用既有任务集，TI-08 不启动。

执行模型：workflow 子代理，provider=`magpie`，model=`devin/deepseek-v4-1-flash-1017`（用户最新确认、已验证连接）。初期 commandcode-plan 模型及旧 ACP 状态中的runner/“NO subagents”只作历史，不是当前执行限制。

## 当前定向门槛

- A：OpenTkInputAdapter 的 Submit 立即交付必须持有与重放相同的交付守卫；DeliverPending 必须有单轮预算并向正常宿主 loop 让出。真实 setter 重入一次的测试须有 injected 标记；持续追加源另验证公平性。
- B：宿主必须接到实际 TsfInputBridge 协调器/store/host work 的安全恢复通知，而非只订阅 router 的另一协调器或测试 Raise。真实 HostWorkGuard 必须参与安全判定；TSF 成功建立后必须真正 Focus 上下文，失败正确回退。
- C：TsfHostSession 构造函数未保存 writerLease 和成功 Activate 的持有状态；Dispose 先设 disposed 使 Blur 提前返回。须修复资源转移和真实失焦、Pop/Deactivate/租约释放，并验证同 HWND 后续会话可建立。
- D：TsfMessageHook 解绑失败必须有实例外强根保活；退休后迟到回调仅链式传递；Dispose 可由所属线程重试，不再访问已释放 session。

## 已准备的验证

`artifacts/ime-work/ti07-workflow-p3a-check.ps1` 在各实现稳定交回后统一串行执行，包含定向输入测试、default/replay/reentrant/immediate/safe-signal/two-field safe+unsafe 审计。每个实际进程有超时，构建失败不运行旧 DLL。首次定向失败即停止并读取证据，不反复运行全量。

本轮尚未构建测试；源码静态问题及旧独立失败记录不是新运行结果。两字段审计使用受控原生快照，不能代表真实微软拼音或实际 GLFW 交错消息验收。

## 后续未关闭边界

P3a 仍须核对事实/普通输入完整交错顺序、非零反向初始选区及 version>1、MTA/初始化失败回退、默认输入、窗口 resize/frame 不安全守卫、取消经 owner 与原生组合终止而非仅直接取消核心客户端。P3b 的 UIElement 分页读取和 OpenGL 呈现、P4 真实输入验收均未通过。

## 第一轮静态复核（尚未构建）

- A 新增单轮预算后，先排空旧队列再处理当前 immediate，可能让旧队列 setter 新追加的事件越过已经捕获的 immediate。返修要求：有旧 pending 时先将当前 action 按序加入同一队列再有界处理；真实 x/z/y 场景最终须 xzy。
- D 首版外线程 Dispose 只置退休标志虽然未解绑，却使活动回调停止消费，仍改变活动状态；要求外线程调用无状态变更地拒绝。新增的测试分配辅助曾越界改入策略文件，要求移回专属测试文件。另需 owner 线程静态重试入口，以供宿主正常安全阶段清理待退休根。
- 尚待下一轮核对的既有问题：WindowsImeBridge.Dispose 在 RestoreSubclass 之前释放 writer，恢复失败时可能虚报窗口已空闲；取消目前仍直接调用核心 client；普通输入后再原生提交的版本基线需独立验证。未将这些静态发现宣称为新运行结果。

## 首次独立运行结果

Debug 宿主测试工程构建成功；OrderedInputDeliveryTests 23/23、SafeBoundarySignalTests 7/7（以日志实际总数为准）通过。真实隐藏宿主审计 default、replay、reentrant-replay、immediate-reentrancy、safe-signal、two-field safe/unsafe 均退出0。安全通知日志实际 TSF guard 退出 wake delta=1、随后 writes1/pending0；两字段 unsafe a=你x/b=y、writes2/1、created2/retired1/pending0。

新初始选区审计准备阶段曾错误假设初始核心版本为1，并用 EndSession 的返回值短路 Start；已修正为两次真实输入产生 version2、反向6->4，并分别调用End/Start。ACP文档内部修订不是核心版本，现保存并核对实际传入的 InitialSnapshot 核心版本，原生文本选区另外核对。修正后退出0，未更改验收门槛。

**新确认失败**：`p3a-native-after-ordinary-audit` 通过真实宿主、实际 adapter/owner：第一次受控原生结果“你”后普通x，text=你x/writes2；下一次受控原生结果“你x好”被拒，text仍你x/writes2/rejected1，退出1。说明普通核心输入后 TSF 权威版本/基线未同步，P3a 继续开放。该场景不是实际微软拼音。

Hook退休首轮9项6失败，包含测试使用hook句柄而非HWND、managed线程id传给原生、错误累计计数及外线程断言在owner执行；已交子代理最小修正，需重建重跑，不据测试失败虚构原生故障。真实生命周期测试尚待运行。

证据保存在 `artifacts/ime-work/ti07-workflow-p3a-evidence`；最终阶段通过前须复制必要证据到持久审核目录。本轮尚未整体通过，未提交推送。

## Devin 模型续修与独立复验

用户最新确认 provider=`magpie`、model=`devin/deepseek-v4-1-flash-1017`，连接测试成功；目标、任务与计划已同步，旧模型记录仅保留为历史。

- 基线最小接线后，连续事务审计 `next native: text=你x好 writes=3 pending=0 rejected=0`，退出0（非真实微软拼音）。新增显式采纳仅限当前归属、未失效、非组合、无待应用原生事实；不放宽 Refresh/ReconcileVersion，不清 stale/needsResync/pending。
- 主代理独立串行 Debug build 成功，0错误（有18条既有/本轮警告，尚需最终清理）。定向运行：Hook退休 **9/9**、IMM桥 **57/57**、真实TSF生命周期 **1/1**、会话适配 **49/49**，均0失败/0跳过。
- 真实 Blur 日志已从旧 Own 变为 **OwnParking**，验证空文档管理器接管焦点，且同HWND第二会话与租约归还仍通过。
- 受控取消终止审计退出0、写入0，但仅证明核心取消后迟到事实不写；**不能据此证明原生组合已终止**，原生 TerminateComposition 接线仍在返修。
- 主代理审核额外收紧：移除同步前吸收未知 NativeGeneration 的逻辑；IMM恢复不覆盖后装外部窗口过程，失败保留根与租约，真正恢复后移除失败保活根。对应真实隐藏HWND叠加子类测试已新增，尚待重建运行；本段额外收紧不冒充上述已通过版本。

P3a仍开放：取消原生终止/取消后下一段输入、跨来源捕获顺序、失败回退与最后回归需完成。P3b只读落点核对不等于启动实现；P4尚未进行。未提交推送。

## 原生取消首轮返修（仍未通过）

原生取消接线落地后先遇两个编译准备错误：ResolveOperationIdLocked 命名参数不匹配；测试读取不存在的 TsfHostSession.Snapshot。主代理修为已有接口、owner内部只读真实Snapshot，构建成功后才运行测试。

独立运行 TsfCancellationTests 失败并以 **-1073741819** 退出：前三用例直接在runner线程创建真实TSF，得到 MTA/RPC_E_CHANGED_MODE；另有隐藏窗口创建失败。真实STA原生终止用例在 `TerminateComposition → RequestLock → ITextStoreACPSink.OnLockGranted` 触发CLR fatal `0x80131506`。失败stdout/stderr已保存为审核证据 `cancel-first-failure.*.log`，不能算取消验收通过。

静态复核另确认：bridge忙碌分支调owner.RequestCancel仍无条件执行；负HRESULT也清意图并可能以coreCancelled报告成功；CancelIntent SourceTicket尚未真正参与事实顺序，取消同步可能因终止回调pending而未回推store。已退同一Devin代理最小修复并授权独占构建，须同owner STA完整建立使用释放、保活窗口委托、真实负HRESULT保留可重试意图、终止后core/owner/store一致。不得跳过原生断言、复活stale或用永久resync掩盖。

跨来源顺序只读审核的初始“常态必然倒序”判断经主代理反驳、子代理复核已撤回：正常 GLFW char 在 TSF KeyDown 内部 RequestLock 完成后；同一事务的终结事实优先于嵌套输入符合原子性。当前无独立事务真实可达倒序证据，保留观察风险，不据此重构全局共享序列。

主代理继续定位到高置信 ABI 错配线索：新取消测试及既有 smoke 的 StartCompositionEditSession 把 NativeCompositionOwnerSink（ITfContextOwnerCompositionSink）当第三参传给 StartComposition，但官方要求 ITfCompositionSink；MarshalAs(IUnknown) object 声明掩盖了类型错误。Terminate 会调用 TIP 的 OnCompositionTerminated（cookie+composition），若拿到 owner sink 的 vtable 将按错误签名进入托管回调。Learn StartComposition 明确第三参可为 NULL；已要求子代理测试第三参改 NULL，owner通知仍由 store 独立安装的 observer 正常接收。该根因须以隔离原生测试不再崩溃验证，当前不提前宣称已修复。

P3b只读准备已交回，不等于开始实现；P3a仍开放且未提交推送。

## 进展

主代理只读检查普通进程列表时未看到 dotnet/devin/codex/node 执行进程，原实现当前没有可见的活动构建测试；保留所有工作区改动。WMI 命令行查询被拒绝后未提权，未据此推断所有受保护进程状态。
