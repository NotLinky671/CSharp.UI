# TI-07 P2b 协议首批审核

审核日期：2026-10-21

状态：协议首批返修已通过 Codex 独立复审；以下保留初审问题和复现过程。本批不启用正式 TSF 输入，不代表 P2 或 TI-07 完成。

依据：[正式接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)。原型中已有的代码也需按生产契约复核，不能因复用而视为正确。

## R1（P2）：文字几何供应器的坐标契约错误

`WindowsTsf/AcpGeometryProvider.cs` 将 TryGetTextRect 的输出写为客户区矩形，AcpTextStore.GetTextExt 却原样返回，无客户区到屏幕转换。按此接口接入窗口后会漏掉窗口屏幕原点，候选定位错误。

统一为屏幕物理像素（或明确在 store 中完整换算，但不得混用单位）；提供带非零/负窗口原点的测试，区分逻辑、客户区和屏幕单位。P3 可继续负责实际几何供应器，本批须冻结正确协议。[GetTextExt 官方契约](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettextext)

本项没有要求修改零长度 caret 范围的兼容行为。官方页面虽然列出 start==end 的限制，但已验收原型支持 caret 查询；如拟改变，须另外核实 SDK/原生调用与 caret 定位的替代路径，记录兼容影响，不能仅凭此项顺带拒绝真实定位请求。

## R2（P1）：没有执行所属 UI 线程保护

AcpTextStore 的 Guard 只有 disposed 检查与 try/catch，没有记录/校验创建线程。RequestLock 和通知入口可从其他线程直接调用 sink；锁住状态对象不能替代 STA 线程归属。内部 Flush/Continue/ApplyAuthoritativeUpdate/Reset/Dispose 也需相应保护。

捕获 owner 线程，COM 入口跨线程返回明确 HRESULT、零 sink 调用；托管同步/释放 API 给出明确失败或拒绝且保留原线程清理机会。通过注入错误 dispatcher 及直接跨线程调用测试证明没有跨线程 COM 调用。

## R3（P1）：重复注册同一 sink 必须更新 mask

AdviseSink 看到已有 sink 一律返回 E_INVALIDARG；官方要求同一 COM identity 的再次注册更新 dwMask 并成功，不额外持有引用。不同 sink 应拒绝，不能与同 identity 的掩码更新混淆。UnadviseSink 的无连接/错误 identity 返回值也应核对规定，而非统一 E_INVALIDARG。

增加同对象改 mask、IUnknown identity 别名改 mask、不同对象、无连接移除等测试；通知使用新 mask，延迟任务不能误发给替换后的 sink。[AdviseSink](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-advisesink)、[UnadviseSink](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-unadvisesink)

## R4（P1）：权威快照同步丢弃活动预编辑

ApplyAuthoritativeUpdate 只取 snapshot.Committed.Text/Selection，忽略 DisplayText、替换区和 PreeditCaret。P2a 活动快照中已提交文档与原生显示文档不同；同步后会删除原生预编辑，caret 也回到已提交选区位置。部分提交后的截断/净化回读同样不能正确同步。

按完整快照重建原生 ACP 视图：活动时使用真实显示镜像与组合内 caret 映射，非活动时使用已提交文本和选区方向。或明确拆成只允许无组合调用的 API，并补齐等价的完整活动快照同步能力，不允许静默丢预编辑。覆盖前后缀、替换选区、分段确认、实际回读缩短及反向选区。此同步仍不写 Binding。

## R5（P1）：应用更新会在读锁内修改文档，且不报告更改

ApplyAuthoritativeUpdate/ResetAuthoritativeText 不检查当前 TSF 锁，可在 OnLockGranted(READ) 中重置原生文档；同一读锁中前后 GetText 会看到不同文本。ApplyAuthoritativeUpdate 同时未自动产生对应文本/选区通知，调用方得到 true 但 TSF 不知道更改。

将应用更新限制或排队到 RequestLock 真正返回后的所属 UI 安全阶段；持锁调用不得直接修改，须明确拒绝或保存有界、代数校验的待处理更新。有效更新与正确 mask 的通知作为同一同步操作，零变化不重复通知，延迟通知不跨 sink/会话代数。测试锁内/通知内更新、过期排队更新、通知回调重入及释放。

官方明确禁止应用在 RequestLock 内修改文档或发送 OnTextChange；这区别于 TSF 持写锁调用 SetText 更新原生暂存。[RequestLock 官方说明](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-requestlock)

## 后续验收

独立审计已在首版 Debug 程序集复现 R2–R5 共四项失败，见配套 `*-evidence/baseline-independent-audit.log`；独立程序源码保存在同目录。返修需先重建生产 DLL，再重建审计程序，避免旧副本导致误判。

增量返修复核：首四项已转绿，真实 CCW QI/原生 GetStatus vtable 调用也通过。扩展审计另复现 R5 通知回调内仍允许 ApplyAuthoritativeUpdate 递归修改，以及反向选区的零变化更新仍推进代数/发通知。后者是 `_document.Selection` 已规范化为 start/end，却直接与 anchor/active 比较导致；需同时比较规范化范围与方向。详细复现位于 `artifacts/ime-work/p2b-audit/Program.cs`，请补入生产回归测试；所有 COM 入口和内部 AttachWindow 等入口也须完成线程/释放保护，不只经 Guard 的入口。

安全阶段增量复核：通知重入、反向选区 no-op、内联通知保护均转绿，但普通入队 dispatcher 在 RequestLock 后没有收到待处理通知的调度，因为 ScheduleFlush 直接拒绝请求栈内的投递（应拒绝的是不安全执行，而非正常异步排队）。此外，内联 dispatcher 下嵌套异步锁请求可使 ContinuePendingLocks 与 ScheduleLockContinuation 相互递归；独立审计进程实际 Stack overflow 退出。后续审计给 dispatcher 加八次递归上限用于安全复现，不能将这个测试限额当产品修复。两项均纳入 R5/有界续排验收。

连接更换边界：ApplyAuthoritativeUpdate 的文本通知回调若撤销旧 sink 并注册新 sink，随后选区通知不得继续发送给新连接。初始返修代码在两次通知之间没有校验连接代数；独立审计第十二项覆盖该边界（与返修任务已要求的 sink replacement 相同范围）。

原生接线测试还需消除预先安装 NoOpSink 对证据的污染：目前测试在 CreateContext 之前手动 AdviseSink，随后用 HasSink 证明原生连接建立；此断言只能证明测试自己的 sink 仍在。请分离托管协议测试与原生接线测试，原生路径从无 sink 的 store 开始，记录实际 TSF advice/所需激活阶段，且独立验证 CCW 的 QI 和 vtable 调用。若 CreateContext/Push 本身不触发 advice，应准确报告边界并用有效的原生生命周期操作验证，不能借测试 sink 代替真实连接。

Codex 将独立复现并审核返修结果；本批还需实际 CCW/原生 CreateContext/Push 证据、Debug/Release 测试、原生资源与延迟回调清理。通过不等于组合分类、P3 或 TI-07 通过。

## 最终复审：协议首批通过（2026-10-21）

DSH 两轮返修结束后，Codex 在稳定源码上重新构建完整解决方案，再分别重建独立审计程序并运行正式回归。R1～R5 和原生证据问题均已解决，增量复核发现的线程通知、内联续排递归、反向选区 no-op、连接更换通知泄漏均已关闭。

| 独立验证 | Debug | Release |
| --- | --- | --- |
| 完整解决方案构建 | 0 错误 | 0 错误 |
| 核心测试 | 545/545，0 跳过 | 545/545，0 跳过 |
| OpenTK 宿主测试（含原生接线） | 453/453，0 跳过 | 453/453，0 跳过 |
| 独立协议/重入审计 | 12/12 | 12/12 |

每个配置正式回归共 998 项；独立审计另外运行。完整构建各有 3 条既有 xUnit2031 告警，来自未改动的集成测试。本批生产文件无新增构建告警。P2a 已验收的五份源码/测试文件与原验收 SHA256 完全一致。

真实 Windows 原生测试从没有 sink 的 store 开始：CreateContext 后仍无 sink，Push 后由 TSF 建立连接，mask=0x1F。独立程序另经 IUnknown.QueryInterface 获取产品 ITextStoreACP，直接调用原生 vtable GetStatus 成功。这些证据证明 COM 协议接线，不代替真实输入法组合行为。

证据位于 [审核证据目录](2026-10-21-ti07-production-p2b-protocol-review-evidence/)：`build-*.log`、正式测试日志/XML、`independent-*.log`、独立程序源码及 `source-hashes.json`。验证脚本为 `artifacts/ime-work/codex-p2b-final-check.ps1`；执行时允许桌面剪贴板回归并恢复原内容，未注入真实输入。

批准进入 P2b 下一批：原生组合 owner/编辑完成观察与真实 TextField 事务接入。实际布局几何供应器、原生确认/取消分类、按键路由、OpenGL 候选呈现及真实微软拼音生产验收仍待完成。TI-07 不勾选，不提前提交推送实现。
