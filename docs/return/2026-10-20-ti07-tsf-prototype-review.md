# TI-07 隔离 TSF 原型审核

审核日期：2026-10-20。审核：Codex。实现：Devin CLI，用户现指定 DeepSeek V4.1 Flash High（`deepseek-v4-1-flash-1017-high`）。

状态：首轮实现中静态审核，未放行真实输入测试或生产接入，不提交推送。设计依据：[路线修订](../plan/2026-10-19-windows-ime-candidate-layout-route-amendment.md)。以下发现需在首轮实现结束后复核，若仍存在须返修；不能用按错误契约编写的自测通过替代修复。

## R1：不要在 TSF 自身持锁修改时回送文本和选区通知

`AcpTextStore.ApplyChangeLocked` 对包括 `AcpEditOrigin.Tsf` 在内的全部写入同步调用 `NotifyTextChangeLocked`；`SetSelection` 同步调用 `NotifySelectionChangeLocked`。真实 TSF 通过 `RequestLock`→`OnLockGranted` 调用这些方法时，这会在其锁内回送更改通知。官方明确禁止针对 `SetText`、`InsertTextAtSelection`、`SetSelection` 反向调用对应 sink 通知，并说明持锁时通知可返回 `TS_E_NOLOCK`。这可能造成通知拒绝、重入或错误的变更跟踪。

修复：区分 TIP 发起的修改与应用外部修改；前者不回声通知，后者在协议允许、可授锁的安全点按 mask 发通知。测试必须记录回调期间是否持锁及 TSF-origin 通知数量，不能只断言通知次数增加。

依据：[OnTextChange](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacpsink-ontextchange)、[OnSelectionChange](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacpsink-onselectionchange)。

## R2：完全裁出不应返回 E_FAIL

设计 §7.3 和 `GetTextExt` 将已布局但完全不可见的范围返回 `E_FAIL`。官方明确要求窗口最小化或指定文本当前不可见时，返回 `S_OK` 与全零 `RECT`。这与未计算/已失效布局的 `TS_E_NOLAYOUT` 不同。错误拒绝会使候选行为实验建立在不合规的文本存储上。

修复实现、设计和正式测试，区分失效、部分裁剪、完全不可见及最小化；记录 `pfClipped`。实际隐藏仍需截图验证，不能从零矩形或 HRESULT 推断。

依据：[GetTextExt](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettextext)。

## R3：相同长度替换错误收缩组合范围

`AdjustCompositionSpan` 把 `change.NewEnd == change.OldEnd` 当成删除，并减去整个旧区间长度。实际这是等长替换。例如已有组合 `[0,1)` 的 `a` 被 `中` 替换后，组合范围错误变为 `[0,0)`，`EndComposition` 读到空串，漏记提交。

修复基于真实区间替换和边界重力映射组合范围，分别覆盖等长、缩短、变长、删除、前缀变更；不能依赖之后一定存在的 `OnUpdateComposition` 修正。`OnUpdateComposition` 还需遵守 `pRangeNew` 的新范围语义，不只是读取旧 view 后记录新指针。

## R4：GetText 仅请求 run 信息时无法推进

`GetText` 将 run 的字符数与 `pacpNext` 都设为按 plain-buffer 容量截出的数量。合法调用 `pchPlain=NULL,cchPlainReq=0,cRunInfoReq=1` 只请求 run 信息时，非空文档得到零字符 run、`pacpNext` 停在原处，调用方无法扫描文档。

修复独立处理 plain-only、run-only、两者都请求、到达末尾与部分缓冲区。空指针与非零容量组合必须明确拒绝，不能报告已写入不存在的缓冲区。

依据：[GetText](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettext)。

## 后续复审重点

- 锁请求应有界合并，异步升级不丢失；不要把 1024 次排空上限之后的队列永远悬置。非法 lock flags 不得获得“只含写位而无读位”的锁。
- `SetText` 选择和替换后的真实选区语义、普通英文字符插入点推进、补充平面字符、Backspace/Delete/Enter/Esc，不只验证初始空文档。
- 组合结束不等于“非空就提交”；需要验证取消后原文恢复、替换现有选区、分段提交与最终写入时序。对外提交应反映最终文档事务，不能把片段追加计数当作真实 Binding。
- `SetFocus(NULL)` 文档不承诺可用，失焦/关闭须验证实际焦点撤销结果，不忽略失败。
- 资源按成功阶段跟踪，恢复原焦点关联、释放每个 out 指针，避免对同一 RCW 的多个 QI cast 重复释放。所有通知与异常计数必须如实记录。

独立复测程序位于 `artifacts/ime-work/ti07-tsf-codex-audit`，不由 Devin 修改；可运行后将原始结果持久保存到本文件同前缀的证据目录。

## 首次可运行版本独立复测

结果：11项，5通过、6失败，原始日志 `artifacts/ime-work/ti07-tsf-audit-round1.txt`。R1（文本与选区两项）、R2、R4已实际失败；R3在Devin首次构建前自行修正，等长范围/结果两项已通过，保留上文作为历史发现，不再当作当前失败。以下两项为新增确定缺陷。

## R5：CreateContext 的 owner 对象没有暴露组合 sink

`CreateContext(..., _store, ...)` 传入的 `AcpTextStore` 只实现 `ITextStoreACP`。随后创建另一个 `TsfCompositionObserver` 并尝试在 context 的 `ITfSource` 上 Advise，不能替代官方注册方式。官方说明 TSF 在 `CreateContext` 时 QueryInterface 所传入对象的 `ITfContextOwnerCompositionSink` 并安装 sink。因此当前关键预编辑/提交归属可能没有真实回调，且注册失败也只记录后返回 engaged 成功。

修复：传入的同一 owner COM identity 同时暴露 store 与 composition sink（可以委托内部 observer，或使用真实双接口 owner wrapper）；创建前即接好，不能伪造成功。用真实隐藏原型窗口/TSF context 验证 QI 和 sink 安装，不以手动调用 observer 的模拟测试证明接线完成。关键注册/关联失败应使初始化如实失败并清理。

依据：[ITfContextOwnerCompositionSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfcontextownercompositionsink)、[CreateContext](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-createcontext)。

## R6：普通字符插入不推进 caret，连续输入倒序

`ApplyAppText` 依赖 `TryReplace` 的选区映射；空文档 caret=0 时插入 a 后，映射仍保持caret=0，再插入 b 得到 ba。独立复测真实 RequestLock→ApplyAppText 两次未得到 ab/caret2。这会使英文/Unicode回退和后续选区替换不可信。

修复：应用插入应折叠caret到新文本末尾，SetText/InsertTextAtSelection各自遵守协议返回的选区与范围约定，明确测试初始空串和已有文档中部插入，退格/替换/补充平面字符。不要仅修改测试预期为倒序。

## R7：RegisterClassW 的结构字符串仍按 ANSI 编组

`WndClassW` 只有 Sequential，没有 `CharSet.Unicode`；其中 `ClassName/MenuName` 默认为 ANSI LPStr，而传给 `RegisterClassW` 需要 UTF-16。后续 `CreateWindowExW` 用真实 UTF-16 类名，可能找不到已注册的乱码类，甚至读取错误的字符串缓冲区。这种声明错误不能通过托管自测证明原生窗口可创建。

修复结构字符串编组并核对全部原生声明，例如 `SetFocus` 原生返回 HWND，不能声明为 BOOL 并以旧焦点是否非空来判断成功。补真实隐藏窗口注册/创建/销毁/注销的 smoke，失败初始化后也须销毁窗口与注销类，`Dispose` 不能只释放字体。不得因测试需要而显示窗口或注入按键，实际GUI由Codex接手。

首轮实现收尾中的追加复测：新增RegisterClassW Unicode ABI检查，共12项6通过6失败。普通应用输入倒序（R6）已被Devin在收尾自行修正，该独立检查现通过；R1文本/选区两项、R2、R4、R5、R7仍失败。原始结果codex-audit-round1-final.txt。历史发现不因修复删除，但下轮不再重复报告已通过项目为失败。

## 第二轮追加独立检查：12通过、2失败

Devin在首轮会话结束前自行读取审核记录并修复：R1的TSF-origin回声通知、R2、R4、R5的接口暴露、R7的Unicode结构，已有12项独立检查通过。新增的两项协议检查仍失败：

1. **R1尚未完整关闭**：ApplyAppText在ITfEditSession/OnLockGranted写锁期间仍同步NotifyTextChangeLocked/NotifySelectionChangeLocked。单纯区分App/Tsf来源不够，必须在锁释放且允许重新grant锁的安全阶段再发应用通知；正式测试应记录sink回调时LockType==0，并在回调内重入RequestLock验证可用。
2. **R8非法锁标志被授予**：RequestLock(0x4)只有写位、没有读位，当前(flags & 0x6)!=0的判据仍grant一个既不读也不完整写的锁。仅接受READ(0x2)或READWRITE(0x6)，SYNC(0x1)可选，并明确拒绝其它位或组合。补真实grant计数零/文档不可改/错误session HRESULT测试。pending请求还需有界合并与最终grant，不能依靠1024循环后悬置队列。

原始日志codex-audit-round2.txt，源码codex-audit-round2.cs。此轮通过不等于真实COM接线、资源清理和候选行为通过。

补充真实CCW验证：Codex取得实际owner的IUnknown，分别QueryInterface ITextStoreACP与ITfContextOwnerCompositionSink均S_OK且非空指针，释放配对。独立审核现16项14通过2失败（锁内应用通知/非法锁标志），见codex-audit-round2-ccw.txt；不再仅以托管IsAssignableFrom证明接口暴露。

## 第三轮契约补验：16通过、2失败

18项独立检查中16通过、2失败。先前“锁未释放”已修复，但尚不能关闭R1：RequestLock在内部_locks返回后直接FlushDeferredNotifications，因此仍在ITextStoreACP::RequestLock方法返回之前发应用OnTextChange，官方明确禁止。独立审核以调用深度标记而非仅LockType检测，已复现。需要投递到窗口安全阶段，方法真正返回后再flush，测试同时证明通知最终送达且sink回调可重新取得锁，不可简单丢弃通知。

R8只修了0x4，READ|0x100等含未知位请求仍grant。须精确白名单验证flags，不能只检查读位存在。队列有界合并与清理阶段计数继续列为审核门槛。

证据codex-audit-round3.txt及源码；不以Devin自行运行某一版audit的“16/16”作为最终审核结论。

## R9：候选列表 COM 派生接口槽位被截断

Codex补做CLR实际COM槽位检查：`Marshal.GetEndComSlot(typeof(ITfCandidateListUIElement))` 未达到SDK要求的slot14。当前C#接口只用`: ITfUIElement`继承，未在导入的派生接口中重新声明基接口4个方法；原生接口却要求IUnknown0–2、UIElement3–6、CandidateList7–14。仅凭C#继承声明不代表导入vtable正确。GetCount/GetString等可能调用到错误的原生函数，真实候选场景前必须修复。

要求按正确顺序在COM派生接口显式展平或new重新声明GetDescription/GetGUID/Show/IsShown，然后候选8方法，核对CLR start/end slot及实际GetCount(9)/GetString(11)/GetCurrentPage(14)。不要只修改槽位测试预期。独立19项当前16pass3fail，见codex-audit-round3-abi.txt；新增失败是ABI，前两项仍是RequestLock未返回就通知、未知flags授锁。

## R10：延迟通知不能用线程池计时器代替自有窗口线程

返修中的ScheduleFlush在NotificationDispatcher为空时创建20ms Timer回调FlushDeferredNotifications。ThreadPool既不是原来的TSF线程，也没有保证原来的STA/COM上下文，时间延迟也不保证RequestLock一定已经返回。无窗口托管测试必须使用显式可控队列/手动泵调度，原生宿主必须要求自有UI dispatcher；不能让缺调度器的store自动向COM sink跨线程通知。请去掉该隐式Timer回退，缺dispatcher保留pending并如实说明/拒绝直到调用方安全flush，或者初始化前必须注入dispatcher。

补测试：发出多次App修改但不泵队列，通知数0；退出RequestLock后在原线程泵队列，通知最终到达且可重入grant锁；销毁/Unadvise后排队回调不调用旧sink。不要把Thread.Sleep等20ms当作协议正确性证明。锁队列合并后16次排空上限仍应有后续真实调度或明确拒绝策略，不能永久留pending而无唤醒。

## R10 返修后 Codex 独立验证（2026-10-20）

Code review：线程池Timer回退已移除，无宿主dispatcher时保留pending由调用方原线程显式pump；自有窗口PostAlways调度，RequestLock不inline发送；锁白名单验证与bounded pending continuation已补。候选列表COM接口显式展平至slot14。

Codex独立audit扩展22项，22/22通过：包括同线程显式queue未pump零通知、pump后真实发送且sink可重入授读锁、Unadvise后不通知旧sink、真实CCW两个IID、CLR候选COM末槽14。Debug与Release目标工程独立构建成功，各79项正式测试+48项selftest通过，零失败/跳过。首次沙箱构建无诊断失败，受限环境外重跑通过；TEMP/TMP只改测试进程至workspace。

Codex另独立执行--native-selftest：隐藏窗口、MTA(type1)/STA(type0)两种套间均成功CreateContext/Push/ownerQI/store sinkmask0x1F、GetFocus为自有document、SetFocus(NULL)本环境S_OK、清理0失败，窗口销毁/类注销成功。源码中对NULL焦点的疑问已由该环境原生结果核对，不继续当作确定失败。此测试未输入真实拼音，不证明TIP预编辑或候选可见行为。

证据codex-target-validation目录、codex-audit-round4.txt及同名cs。当前仅放行接下来真实隔离原型验收，不放行生产迁移、TI07完成或提交。可见测试前补只读active-profile核对，防止把其它TIP结果归为微软拼音。

## R11：真实窗口在身份查询前发生调度递归与栈溢出

2026-10-20 09:00 HKT，Codex真实启动MTA nihao-commit。TSF初始化成功，但TsfHostWindow.Invoke<T>内部`Invoke(() => result = func(), timeoutMs)`因赋值表达式lambda仍返回T，重载解析再次选择Invoke<T>，无条件递归。真实进程exit -1073741571（stack overflow），控制台大量相同Invoke<ActiveProfileSnapshot>帧。尚未发送nihao，不存在真实输入通过证据，也未正常执行受控清理。

修复使用明确Action/不返回值的块lambda进入非泛型派发；补泛型返回值/异常/跨UI调用测试，确保结果和线程身份。不能在同一xunit进程运行已知会栈溢出的基线而杀死全部测试；可在修复后测试或隔离子进程复现。证据nihao-mta-round1原始jsonl与console。

## R12：隔离窗口未感知系统150% DPI

同一真实日志seq5窗口dpi=96，而此前生产OpenTK窗口为144。原型无显式DPI manifest或启动前设置，Win32/GDI路径可能处于DPI-unaware坐标虚拟化，不能作为150%物理候选坐标验收。用户系统设置不变。

修复仅原型进程的DPI-awareness（优先PerMonitorV2 manifest，或在任何HWND创建前合法API并核对结果），记录线程/窗口awareness与真实GetDpiForWindow；不能硬编码144或事后给虚拟坐标乘1.5假充。WM_DPICHANGED更新自有字体/布局与建议窗口rect；滚动参数明确逻辑/物理单位。原型与生产显示路径差异继续说明。原型应从当前150%显示器实际取得144；不改系统DPI或生产宿主。

依据：[设置进程DPI感知](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)、[GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)。

## 身份判据补充

ImeIdentity.IsMicrosoftPinyin/SameIdentity当前使用HRESULT>=0，应按本轮已冻结“GetActiveProfile返回S_OK”要求使用==S_OK；S_FALSE不能作为已确认身份。匹配ID但S_FALSE的合成快照亦应拒绝，读日志实际HRESULT不得被改写。

## 真实MTA、DPI144、微软拼音唯一提交已见证

nihao-mta-round2已由Codex真实运行与查看两张截图：预编辑ni'hao，Space后文档精确“你好”，绑定writes1；前后GetActiveProfile S_OK且CLSID/ProfileGuid均微软拼音，自有前台；实际MTA(type1)、windowDPI144。Codex正常关闭自有窗口后host exit0、cleanup failures0。此轮仅支持真实预编辑与唯一提交，不代表候选可见行为已通过。

## R13：默认观察器没有明确允许原生候选UI显示

本轮s1-composing中没有候选浮窗。日志seq73/76的BeginUIElement show_requested=false但suppressed=false，说明观察模式沿用了进入时的FALSE。代码默认不设置pbShow，仅SuppressCandidates=true时写0；官方要求应用决定是否由TIP绘制：本应用未自绘候选，默认应明确写TRUE，不能假定入参已经true。否则原型主动让系统候选保持隐藏，无法用来测试动态定位。

修复默认明确pbShow=TRUE；仅显式抑制且归属候选元素时FALSE，日志区分输入/输出。补初值0与1、unknown元素/候选元素、显式抑制模式的测试。真实验证候选出现后再测滚动/裁出，不能把“始终隐藏”当裁出通过。

依据：[BeginUIElement](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfuielementsink-beginuielement)。

## R14：单场景完成后没有结束窗口事件循环

本轮seq231场景pass后，ScenarioRunner.Run正常返回0，但Program仍阻塞RunMessageLoop，没有主动关闭。Codex核对PID/标题后手动调用CloseMainWindow发正常关闭才结束，原始证据已保留。工具应在runner完成（含失败/前置超时）后投递自有UI关闭，不等看门狗。看门狗触发须明确非成功退出，不能把超时关闭视为正常完成。补单场景自动收尾与超时/提前关闭结果测试；窗口销毁后不得继续投递或输入。


## R13/R14 修复后继续真实滚动验收

Devin返修默认BeginUIElement明确允许原生UI、仅显式抑制候选时拒绝；runner结束自动投递自有UI关闭，看门狗记非成功。Codex重新构建独立审核，包含泛型Invoke/异常/S_FALSE拒绝、incomingFALSE仍允许UI等28项，28/28通过。此前R11/R12独立正式测试90/90与真实隐藏PMV2/DPI144验证输出已持久保存。真实滚动可见效果和单场景自动收尾仍待新一轮窗口验证。
## R15：真实 TSF 纯滚动仍未带动原生候选框（2026-10-20）

Codex 已查看 scroll-mta-round1 两张原始截图：预编辑文字上移 11 物理像素，候选框仍停留在原位置，视觉验收失败。窗口 DPI144、MTA，前后只读身份均为微软拼音 S_OK。seq196–205 表明布局 generation10→11，row `(107,139)-(195,178)`→`(107,128)-(195,167)`；OnLayoutChange 内 TIP 重新授读锁，GetTextExt 已读取更新后的矩形，通知返回 S_OK，但这不证明浮窗移动。

脚本 scenario pass=true 只检查了文档行移动、composition 保留、零 Binding 写入与 generation，未验证候选浮窗，应明确输出视觉待验，不能作为完整 scroll-follow 通过。记录 detail 中 composing=False 是 CleanupComposition 之后读取，亦应区分清理前后的状态。此次 host 自动 exit0、cleanup failures0，R14 真实收尾已验证；R13 原生候选已可见。

证据：[滚动前](2026-10-20-ti07-tsf-prototype-review-evidence/scroll-mta-round1/s4a-composing.png)、[滚动后](2026-10-20-ti07-tsf-prototype-review-evidence/scroll-mta-round1/s4b-small-scroll.png)，同目录保留完整 JSONL 与控制台。不批准生产迁移或提交。继续限定隔离原型诊断，不能通过改文档/选区、取消重启 composition、生成 Binding 写入或操纵外部候选窗口来冒充纯布局更新。

## R16：新通知生命周期与结构化验收结果仍需修正

独立 Debug/Release 各 99 正式测试、51 自测及 28 项审核检查通过，但代码复核发现：新增 NotifyLayoutDestroyed 放在 cleanup 的 dispose-uielement-observer，位于 pop-context、release-context、Deactivate 之后；TSF 很可能已 Unadvise 文本存储 sink，该通知无法送达。应在 context/sink 尚有效时发送，并以真实隐藏上下文日志验证先 DESTROY 后 Unadvise/Pop。单独手工调用 store 的测试不能覆盖宿主清理接线。

R15 的 detail 虽已标注 pending-not-assessed，结构化 result 仍 pass=true 且 runner exit0，自动化消费者仍会误读。应提供机器可读的 checks_pass、candidate_visual、assessment 等字段，完整 pass 在视觉未评估时不可为 true；使用独立非成功的待人工验收退出码（如3），不与断言失败和前台缺失混淆。对应场景包括 scroll/move/resize/clip-out/UIElement 显示诊断。保持普通提交场景的成功语义。此修正不等于视觉问题已解决。


## R16 返修独立复核

Codex 独立 Debug/Release 各109正式测试、51自测通过；重新构建独立audit后28/28通过。真实隐藏窗口MTA/STA均通过，destroy=23、Unadvise=24、Pop=28，清理0失败。结果结构化区分checks_pass与candidate_visual，待视觉返回3，不再误报完整成功。证据codex-round10。用户三个保护文件SHA256保持不变。接下来真实MTA纯滚动第二轮，仍未批准TI07完成/生产迁移/提交。


## 第二轮真实滚动：补齐生命周期后仍未通过视觉验收

2026-10-20 09:30 HKT，Codex 运行 scroll-mta-round2 并亲自对比截图：文字行139→128（11物理像素），微软拼音原生候选框仍停在原处。前后身份微软拼音S_OK，MTA/DPI144，composition过程保持True，Binding writes0；清理后False且cleanup failures0。脚本现如实给出checks_pass=true、candidate_visual=pending、pass=false，exit3；Codex人工结论为视觉失败，不能把pending当通过。CREATE/CHANGE/DESTROY生命周期变更没有解决本场景。

证据scroll-mta-round2目录的两张原图、JSONL及console。R15继续开放；R13/R14/R16已修复验证。隔离原型已证明真实ACP预编辑/提交与正确布局查询可工作，但未证明原生候选随纯滚动更新。不批准生产TSF迁移、TI07完成或提交。未继续把尚未验证的clip-out/move/resize宣称通过；也不能仅凭这一场景断言所有TSF实现或所有Windows/TIP版本均不支持。下一步需要另行评估候选UI呈现策略，不能继续以接口S_OK代替可见结果。

## R17：可见 STA 对照无法启动

Codex 运行 `--scenario scroll-follow --apartment sta`，真实进程在预编辑前即 exit2。seq17 CoInitializeEx 请求 STA 返回 0x80010106（RPC_E_CHANGED_MODE），seq18 engage-failed，清理0失败。当前 Program 在默认运行线程创建窗口后才按参数初始化 COM；仅给 Engage 传 sta=true 不能改变已初始化套间。隐藏 NativeSmoke 使用专门设置套间的线程可以通过，不能据此推断可见 CLI 路线已可用。

修复须仅在隔离工具中，于创建 HWND/COM 之前确定运行线程套间，让窗口、TSF、消息循环与清理始终同线程。MTA默认保持；STA启动需有真实隐藏宿主路径回归及实际CoGetApartmentType验证。禁止强制CoUninitialize别人的初始化或更改生产宿主。证据 artifacts/ime-work/ti07-tsf-codex-scroll-sta-round1-console.txt，尚未输入任何测试按键，无候选行为结论。

## R18：新增几何诊断测试暂未编译通过

路线复核进行中，Codex 独立执行 `dotnet build tests/CSharp.UI.TsfHostCheck.Tests/CSharp.UI.TsfHostCheck.Tests.csproj -c Debug --no-restore -v quiet`，发现 `GeometryTraceTests.cs` 第148、171行各两处 CS1503。`sink.Grant = _ =>` 将 `_` 声明为 uint 参数，使内部 `out _` 不再是 discard，无法传给 `out TsfRect` 与 `out int`。须更名 lambda 参数并保持诊断断言；修复后重新独立构建和测试。受限环境此前“0错误但构建失败”不能当作通过。此时 DSH 仍在工作，该条为中途发现，最终以返修后复测结果为准。

## 路线诊断独立复测与报告审核

DSH 修正了 R18（将查询抽为无参 lambda，避免 `_` 名称绑定）；Codex 独立 Debug/Release 各115正式测试、52自测均通过，无跳过，独立 audit 28/28通过。原始构建与测试输出保存于同前缀证据目录 `codex-route-diagnostics/`。本轮只增加几何查询拒绝与通知重入深度日志，不代表候选视觉通过。

新增诊断之后，Codex 还独立运行原有 `--native-selftest`，真实隐藏窗口 MTA/STA 2/2通过，日志位于 `codex-route-diagnostics/native/`。该入口仍是独立 NativeSmoke 路线，只证明底层初始化未被诊断破坏；R17 要求实际 RunHost 路线另行修复并复测，不能据此关闭。

R17 返修中途检查：Codex 构建工具后运行新增 `--hidden-host --apartment sta`，仍exit2；在任何 HWND 创建之前 `apartment_before=type=1 qualifier=0`，请求STA返回 `RPC_E_CHANGED_MODE`。这直接证明只把 CoInitializeEx 提到窗口前不足以修复：入口线程本来已处于MTA，须在宿主线程启动前选择其套间。当前仍未放行可见输入测试。中途原始日志位于 `artifacts/ime-work/ti07-tsf-codex-sta-startup-interim/`，最终按后续版本复测。

DSH 已生成 `artifacts/ime-work/ti07-dsh-route-reassessment-result.md`，但 ACP prompt 在收尾时达到20分钟超时；进程已退出、session已关闭。不能将该RPC记为成功，也不能因此忽略已落盘成果，以上独立复测有效。

报告中的“剩余解释只有三类”“三项实验全失败即无应用侧手段”“自绘是唯一保证路线”不予接受：当前有限实验和原型实现不足以穷尽 TSF/TIP 行为及应用侧方案。这里只能得出当前环境和实现尚未通过的结论。已要求 DSH 修正文案并先修 R17，再验证 STA 对照；本轮不批准生产迁移、自绘候选实现、TI07完成或提交推送。

## R19：入口诊断提前绕过了 Windows 平台守卫

STA返修中的 `Program.Main` 为记录入口套间，在 `OperatingSystem.IsWindows()` 判断之前新增了 `NativeMethods.CoGetApartmentType`。该调用位于ole32.dll；在Linux/macOS上会先抛出加载异常，无法执行原有的Windows-only说明并返回2。应先保留平台判断，再在任何日志/窗口/COM对象创建前记录套间。该调整不影响Windows套间诊断，不需要真实非Windows机器即可从调用顺序确认；请修复后重新构建。此条为进行中审核，后续按最终版本复核。

## R17 / R19 最终独立复测

DSH 最后一次 ACP 收尾成功结束，指定模型 `commandcode-plan/deepseek/deepseek-v4-flash` 经路由确认。R17 改为默认 MTA 沿用入口线程，显式 STA 在 `Thread.Start` 前设置独立线程套间；两者共用 RunHostCore 的窗口、TSF初始化和清理。R19 已将 Windows 守卫恢复至所有 ole32 调用之前。路线报告已撤回过度推论。

Codex 使用 `artifacts/ime-work/ti07-codex-sta-final.ps1` 独立构建并执行：Debug/Release 各139正式测试（零错误/失败/跳过）、59自测通过；实际隐藏宿主默认MTA及显式STA共4组均exit0。原始JSONL独立核对套间、同一原生线程完成初始化/清理、COM准备早于HWND、窗口隐藏、不运行输入场景、写入0、清理失败0及COM引用最终释放。STA实际为type3，独立线程且不同于入口线程；默认MTA为type1。

另独立重新构建audit并28/28通过，原有真实隐藏NativeSmoke MTA/STA 2/2通过。证据位于同前缀 `codex-sta-startup-final/`。非Windows行为仅静态核对平台守卫顺序，未声称已在Linux/macOS执行。R17、R18、R19已关闭；R15候选视觉跟随仍开放。下一项为真实可见STA滚动对照，尚无视觉结论，仍不提交推送、不批准生产迁移。

## 真实 STA 小滚动对照：仍视觉失败

修复后 Codex 执行 `--scenario scroll-follow --apartment sta`（scroll-sta-round2），自有窗口自动正常取得前台，真实微软拼音前后身份守卫通过，实际STA type3、DPI144。本人查看 `s4a-composing.png` 和 `s4b-small-scroll.png`：文本行139→128，候选框仍在原位置。组合期间写入0、组合保持；结构化checks_pass=true、candidate_visual=pending、pass=false、exit3，清理失败0。人工视觉判定为失败，不能因脚本几何检查通过而修改总结果。

本次带新日志的GetTextExt成功查询包含read-lock=0x2、layout_notify_depth=1及滚动后的矩形；因此修复STA入口未解决当前原型的候选跟随。证据位于同前缀 `scroll-sta-round2/`。该结论仅针对本环境及原型实现，不能推广为所有应用侧机制无解。下一项开始MTA负向大幅滚动、保持文本完全可见的对照；候选视觉仍是验收门槛。

## 有界真实对照结果：大幅滚动 / 完全裁出 / 移窗

用户点击配合后，Codex 完成大幅滚动，并继续执行完全裁出及移窗对照；所有进程均已正常退出，无待点击窗口。三组均为真实微软拼音前后身份确认、MTA、DPI144，组合期间写入0、组合保持，结束清理失败0。脚本退出3表示视觉待审核，本节为独立人工视觉结果。

| 对照 | 几何与截图结果 | 审核结论 |
|---|---|---|
| `large-down`，逻辑滚动-100 | 文字行139→289，下移150物理像素且完全可见；GetTextExt成功返回`(107,289)-(195,328)`，读锁0x2、通知重入深度1；候选仍停原屏幕位置 | 当前大幅位移样本仍不跟随，不能把小位移作为唯一解释 |
| `clipout-suppress`，先自然裁出，再尝试Show(FALSE) | 文字行bottom85低于client top125，GetTextExt返回完全不可见零矩形；截图中文字消失但候选仍可见；恢复后组合仍在 | 自然裁出隐藏失败。显式隐藏**未实际执行**，observer返回`no live ui elements`，不能声称Show(FALSE)已调用但无效 |
| `move`，窗口上移24 | 捕获原点y80→56，文字行139→115。两图候选蓝色选择标记局部y113..136相同，因此其屏幕y193..216→169..192，确实上移24 | 候选随整窗移动，当前问题集中在窗口内部布局移动。机制可能涉及窗口关系，不能仅据此证明TIP消费了布局通知 |

Codex逐张查看了上述2/4/2张原图；对移窗还读取原图像素核对蓝色选择标记位置，并按capture日志转换为屏幕坐标，避免把相同的局部图坐标误判为不移动。截图区域均为自有窗口矩形：前两组`(80,80,760,420)`，移窗后`(80,56,760,420)`；只能判断当前可见候选部分，不据此推断截图外内容。

三组从第一张到最后一张截图期间的geometry/rejected均为0。组合输入及取消期间另有TS_E_NOLAYOUT日志（深度0），不能把这些无关时段的拒绝归到滚动通知。clip-out中UIElement在输入初期Begin后即End，候选显示期间没有活动元素，因此进一步UIElement控制尚缺可操作对象。

证据已持久保存于同前缀`large-down/`、`clipout-suppress/`、`move/`，每组含原图、原始JSONL、控制台与`codex-selected-events.json`。R15仍开放。停止堆叠刷新和位移变体；下一轮如继续，仅核对TSF context/view层实际几何是否与store一致，不改生产，不直接转向自绘候选，不提交推送。

## R20：context/view 探针中途审核——范围与总体判定必须可靠

初版`ContextViewProbe`中，`GetRangeAcpExtent`失败后继续查询，成功时也未检查actual ACP/length与请求范围完全相同；`Classify`只用RectMatch判定ok，忽略已记录的ScreenMatch/WndMatch。这样可能把不同范围或窗口/屏幕不一致的对照标为ok。须将GetExtent失败或范围偏差明确输出并停止几何比较，不能用于证明同一范围坐标一致；总体判定覆盖全部要求的几何项，并区分会话实际未执行与真实坐标不一致。当前DSH仍在实现，以上为中途发现，最终以实现结束后的复核为准。SDK已独立核对ITfRangeACP继承22个ITfRange方法，GetExtent槽25、SetExtent槽26，不能使用IUnknown后的槽3/4。

## R21：只读探针真实隐藏入口触发原生崩溃

Codex 独立构建工具并执行 `--hidden-host --probe-context-view`（MTA，无窗口显示、无输入），进程返回`-1073741819`，控制台出现`Fatal error / Internal CLR error (0x80131506)`。最后日志为RequestEditSession引发RequestLock→OnLockGranted（flags0x3，read lock0x2），尚未写出context-view/probe结果。此轮不能作为探针成功，不放行带探针的真实输入。

确定需修正的ABI声明：SDK msctf.idl中RequestEditSession第二参为`ITfEditSession *`，当前ITfContext声明却为`[MarshalAs(UnmanagedType.IUnknown)] object pes`，传入的是IUnknown身份，不能保证是原生按DoEditSession槽调用的ITfEditSession接口指针。应按SDK用精确接口封送或显式QI后的正确接口指针，不凭对象实现了接口推断IUnknown传参安全。修复后复测真实隐藏MTA/STA；若仍崩溃，继续定位并如实保留。证据artifacts/ime-work/ti07-codex-view-interim/，原生崩溃的确切根因仍须由修复前后对照验证，不能只凭此声明就宣布全部解决。

Codex已在自有独立audit新增一项RequestEditSession接口参数ABI回归检查（原28项变为29项）；这次audit源码变更由Codex执行，DSH仍不得修改。用户三个保护文件要求不变。

R21独立复测：接口改为精确`ITfEditSession`后，Codex重建独立audit并29/29通过；真实隐藏MTA、STA各执行一次`--hidden-host --probe-context-view`均exit0，request/session均S_OK，session_ran=true，rect/screen/HWND三项一致，未显示窗口、无输入。原生崩溃不再出现，证据`codex-context-view-r21/`。R20的实际范围验证/总体状态仍需返修，暂不放行带探针的真实输入。

本轮DSH首个探针实现任务15分钟RPC超时，但实现、报告与日志已落盘；随后同会话恢复返回-32603内部错误，无成功返修。Codex已用当前工作区新DSH ACP会话继续R20，模型仍为`commandcode-plan/deepseek/deepseek-v4-flash`；不得把这些RPC失败记为测试通过或自行切换模型。

R20后续复核：新ACP会话返修已正常结束，范围与三项几何检查已经加入。但实际范围不一致分支返回E_FAIL，最终Classify优先把sessionHr失败分类为session-failed，因此真实路径无法产生承诺的range-mismatch。虽然不会误报ok，仍会丢失具体诊断原因；已要求最小返修并覆盖实际分支到分类的回归。暂不运行带探针的真实输入。

## R20 / R21 最终独立复核与真实组合对照（2026-10-20）

DSH同一新会话完成最小后续返修，ACP正常结束。范围不一致分支现在返回S_OK表示检查完成，保留range_match=false并停止几何查询；extent失败仍传播原始HRESULT。新增从Run→编辑会话回调→最终分类的实际分支测试，覆盖范围不同、extent失败及成功对照。Codex审核此控制流后接受R20诊断修复，R21真实入口也继续通过；它们不代表R15通过。

Codex独立运行结果（证据`codex-context-view-final/`）：

| 检查 | 结果 |
|---|---|
| Debug / Release正式测试 | 各168/168，无失败、跳过 |
| Debug / Release自检 | 各67/67 |
| 默认关闭探针的实际隐藏宿主 | Debug/Release × MTA/STA，4/4 |
| 启用探针的实际隐藏宿主 | Debug/Release × MTA/STA，4/4；真实会话执行，范围精确相同，rect/screen/HWND全一致，释放成功，无显示/输入/写入，清理0失败 |
| 重建后的独立audit | 29/29 |
| 原生隐藏context smoke | MTA/STA，2/2 |

沙箱内初次构建失败且没有编译错误，已获准在沙箱外重跑，上表仅采用重跑的真实结果。三个用户保护文件SHA256均与基线一致。

随后真实MTA `scroll-follow --probe-context-view`实际取得自有窗口前台并确认微软拼音身份，无强制焦点操作。进程exit1：输入第一个n后组合结束并写入一次；后续组合范围1..5，截图候选为英文ihao。因此**本次不作为有效中文组合验收**，不把它写成全通过。此异常发生在首个探针之前，不能归因于探针；当前中文/英文输入模式需用户确认。

有界观察：两张原图均已由Codex查看，文字y139→128而候选位置不变；两个只读会话均S_OK，同一真实组合范围1..5对应actual_acp=1/actual_cch=4，view/store分别返回`(125,139)-(187,178)`与`(125,128)-(187,167)`，screen/HWND一致，释放全成功。无崩溃，结束清理0失败。只证明该次英文候选样本的几何接线及可见现象，不外推有效中文样本结论，也不说明所有原生路线不可行。

完整失败日志、两张原图及独立视觉判定保存在`context-view-scroll-final/`。当前无活动GUI；下一步由用户确认微软拼音为中文模式后，重新打开自有窗口完成有效中文组合的context/view对照。R15仍开放，不迁移生产TSF、不实现自绘候选、不提交推送。

## 有效中文组合 context/view 对照完成（2026-10-20 11:26–11:27 HKT）

用户确认中文模式后，Codex依次执行scroll-follow、clip-out、move-window，均启用默认关闭的`--probe-context-view`。三组都在自有前台发送输入，前后实际profile的CLSID/GUID精确匹配微软拼音；原图显示ni'hao及“你好”等中文候选。各组组合期间Binding写入0、组合保持，结束取消成功、清理0失败，进程退出3（脚本检查通过但视觉待人工审核）。原型已全部退出，无待点击窗口。

每组两个真实只读会话均成功：请求范围0..6，actual_acp=0/actual_cch=6精确一致；context/view与store的文本矩形、clipped、屏幕矩形、HWND逐项一致，范围引用及view释放成功，无异常。Codex逐张查看全部7张原始PNG后判定：

| 场景 | 几何 | 人工视觉结果 |
|---|---|---|
| 内部小滚动 | 两侧文本RECT均从`(107,139)-(195,178)`变为`(107,128)-(195,167)` | 文字上移11物理像素，候选不动；跟随失败 |
| 完全裁出 | 两侧均返回`(0,0)-(0,0)`、clipped=1；文字行bottom85、client top125 | 文字消失，候选仍显示；隐藏失败。恢复后组合仍在。本轮未请求UIElement显式隐藏 |
| 整窗上移 | 两侧文本RECT均变为`(107,115)-(195,154)`；捕获原点从(80,80)变为(80,56) | 候选局部位置不变，屏幕位置随窗口上移24物理像素；仅整窗移动通过 |

移窗结论同时核对capture原点，避免把相同局部图坐标误判成屏幕位置不动；读取两张原图蓝色区域的像素也确认局部位置相同。这里只判断原图内可见的候选部分，不推断截图外候选内容。

有界结论：本次已验证真实中文组合范围在context/view查询路径上获得了更新后的几何，范围错误、view层仍返回旧坐标均不能解释当前样本；但只读探针查询本身并不证明微软拼音在原通知时主动消费了这些坐标，也不能据此确定内部根因或证明所有原生路线无解。探针增加了只读会话，因此结果单独保存；先前默认关闭探针的同类失败证据继续保留。

完整原始JSONL、控制台、7张PNG、精选事件与独立视觉判定分别保存在`context-view-scroll-chinese-round2/`、`context-view-clip-chinese-round2/`、`context-view-move-chinese-round2/`。R20/R21诊断修复通过，R15仍开放，TI-07未通过；不以API/测试通过替代可见行为，不批准生产TSF迁移或自绘候选，不提交推送。此轮有界诊断已完成；后续应先根据现有证据修订研究路线，明确下一项协议假设和停止条件，避免继续堆叠无依据刷新调用。

## R22：通知回调归因过度与调用链诊断缺口

用户授权继续后，Codex先核对官方OnLayoutChange、GetTextExt、RequestLock、AssociateFocus、SetFocus。既有中文滚动日志seq257失效、seq258发布布局，之后depth1成功查询发生在通知内、应用探针前。因此“没有收到任何回调”并不符合现有证据。源码注释和README将depth1直接称作“TIP响应”，把depth0称作“无关查询”，同样超出证据：IUnknown sink所代表的是TSF层，调用嵌套不能识别具体TIP，也不能证明候选消费返回值；depth0也可能是延迟响应。

本轮有界任务是增加默认关闭的配对通知ID、锁/几何/探针来源与UI线程安全点只读焦点关联记录，先分清应用主动探针、TSF同步回调及归因未知的通知外查询。不得为日志修改文本、锁或通知原行为，亦不得在store回调内递归查询COM焦点。实施由DSH ACP指定模型承担，Codex再独立审核及不启用context/view主动探针的真实验收。现有基线精选时序保存在`layout-chain-baseline/`。R22为诊断准确性问题，并非已经确定的候选不动根因。

官方依据：

- [OnLayoutChange](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacpsink-onlayoutchange)：布局变化通知，NOLAYOUT后布局可用需通知；没有承诺候选窗口必然移动。
- [GetTextExt](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettextext)：屏幕坐标、需要读锁，已布局但不可见为S_OK零矩形。
- [RequestLock](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-requestlock)：方法与session HRESULT有区别，通知和锁重入必须遵守契约。
- [AssociateFocus](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-associatefocus) / [SetFocus](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-setfocus)：关联窗口和document manager后TSF可自动处理焦点；不能凭猜测在滚动时重复设置焦点。

## R23：调用链诊断初版中途审核——嵌套/线程隔离与失败如实记录

Codex自有独立检查`artifacts/ime-work/ti07-layout-chain-codex-audit/`直接编译当前LayoutChainTrace，6项中3项失败：内层通知Exit把ID清零而不恢复外层；重入锁请求同样丢失外层ID；同一store在另一线程DescribeScope读到主线程的应用probe标签。现有锁仅串行访问字段，不能实现线程局部归因；这违反本轮嵌套恢复与跨线程不串标签要求，会污染核心证据。当前为中途状态，最终以实现完成后的复测为准。

另需最终检查FocusSnapshot：Capture末尾直接调用trace，日志委托异常会穿透场景；SameComIdentity捕获查询异常后返回false，会把“身份查询失败”伪装成“其他document manager”；多处ReleaseQuietly吞掉释放异常且注释声称有另行记录，实际没有。诊断必须不改变宿主流程，但失败/释放错误仍应可见，不能用静默吞掉代替可靠结果。尚未放行新版GUI，不将其归为候选不动根因。


### R23 独立复跑与末轮说明修正（2026-10-20）

Codex独立运行`ti07-codex-layout-chain-final.ps1`：Debug/Release各203正式测试、74自检通过；29项独立协议检查、6项作用域检查通过；默认关闭4入口、context/view探针4入口、trace-only/trace+probe共8入口均通过；MTA/STA原生smoke通过。证据见`codex-layout-chain-final/`。沙箱内构建返回0错误但失败，按要求在沙箱外重跑成功，没有改动断言以放行。

末轮仍发现两处说明/边界错误，已交回DSH最小修正：ThreadLocal<T>不随ExecutionContext流动，报告不能把Task同线程内联当作跨线程流动证据；Scope按名称弹栈不能保证同名嵌套重复Dispose安全，应实现唯一租约或明确仅支持严格LIFO一次释放。该作用域仅为隔离工具诊断，不能据此宣称生产候选问题修复。待完成后复核，无新增刷新或焦点设置。

### R23 收尾复核及真实调用链结论（2026-10-20）

末轮采用严格LIFO、同线程且每租约释放一次的诊断作用域契约；Codex核对ContextViewProbe所有PushScope均为using路径。ThreadLocal/AsyncLocal说明已勘误。末轮Codex独立复跑Debug/Release各204正式测试、74自检及6项scope audit通过，三份保护文件SHA256仍与基线一致。诊断可靠性R22/R23在本轮有界范围内通过。

随后Codex运行不带`--probe-context-view`的真实中文scroll-follow和clip-out，只带`--trace-layout-chain`。两次均MTA、DPI144，GetActiveProfile前后确认为微软拼音CLSID/profile，截图确有ni'hao及“你好”候选；均writes=0、cleanup_failures=0，退出3待视觉判定。Codex亲自查看全部5张原图，独立视觉均失败，不能把脚本checks_pass当成TI07通过。

| 场景 | 实际回调与关联 | 视觉结果 |
| --- | --- | --- |
| 内部小滚动 | seq310发布generation11，文字RECT由(107,139)-(195,178)变为(107,128)-(195,167)。seq311开始通知ID9，seq312请求锁32，seq313授予READ(0x2)，seq315的text-ext取得新RECT，seq317释放、318方法/session均S_OK、320通知S_OK退出。seq308/323安全点Win32前台和焦点均自有窗口，TSF document manager及top context身份均相同，释放无错。没有应用context/view主动探针事件。 | 文字向上11物理像素，候选原位：失败。 |
| 完全裁出 | seq313发布row=(107,46)-(195,85)，clienttop125；通知ID9/锁32内seq318为text-ext-fully-invisible-zero-rect，随后锁与通知成功返回。没有额外候选UI抑制，也没有应用主动探针。该场景未接入焦点快照，不扩展滚动的关联结论。 | 预编辑文字消失，候选仍显示；恢复后组合可见：失败。 |

证据已复制到[滚动](2026-10-20-ti07-tsf-prototype-review-evidence/layout-chain-scroll-final/)与[裁出](2026-10-20-ti07-tsf-prototype-review-evidence/layout-chain-clip-final/)，含完整JSONL、控制台、原图、精选事件及Codex独立visual-verdict。

本轮能排除的解释：在该滚动样本的同步通知路径上，并非漏发OnLayoutChange、没有TSF回调、读锁未获授、返回旧几何或采集时焦点关联到其他document/top context。仍不能确定具体TIP调用者或TIP内部是否/如何消费返回值；焦点快照非原子，仅描述采集时点。按计划停止继续添加刷新变体，R15/TI07保持开放；不批准生产TSF迁移、自绘候选或提交推送。

## R24：下一项有界协议复核——GetScreenExt（待验证，非已确定根因）

Codex发现当前GetScreenExt先RequireRead()，非法view返回TF_E_INVALIDVIEW，且依赖有效文本布局。官方[GetScreenExt](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-getscreenext)定义的是显示表面的屏幕RECT，列明S_OK/E_INVALIDARG及最小化时零RECT；没有像GetTextExt那样明确要求读锁。现有TextStoreLockTests反而把GetScreenExt无锁返回TS_E_NOLOCK固定为预期。非法view返回码与官方文档不一致；读锁及布局耦合需要以官方样例/SDK及独立契约检查进一步复核，不能仅据文档省略一句话宣称全部要求已确定。

下一步先核对官方实现依据，独立复现无锁/非法view/文本布局失效但表面仍有效/最小化场景；有充分依据才委托DSH最小修订隔离工具并更新错误契约测试，保持GetTextExt读锁语义。此项是协议合规调查，不保证解决候选跟随：本轮关键通知里的GetScreenExt已有READ锁且成功，单纯去锁不能解释该次已成功查询后仍不动的结果。修订后仅做单变量真实对照；若视觉仍失败，记录排除结果，不继续叠加刷新或擅自迁移生产。

### R24 官方样例确认与独立复现（2026-10-20）

微软官方[Windows-classic-samples tsfapp/textstor.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/winui/tsf/tsfapp/textstor.cpp)的CTSFEditWnd::GetScreenExt明确注明`no lock is necessary for this method`，直接查询客户区并转屏幕坐标，非法view返回E_INVALIDARG。该依据已解决上节“只凭文档省略不可断言”的不确定性。独立audit `artifacts/ime-work/ti07-screenext-codex-audit`复现3失败/1对照通过：无锁非法view、无锁有效surface、文本布局失效不应使未变化surface不可查，全部失败；GetTextExt仍需读锁通过。现允许最小修订隔离工具，范围与停止条件已追加计划；不把合规偏差直接认定为候选不动根因。

### R24 协议修订独立审核通过，真实中文对照待完成（2026-10-20）

DSH ACP session65e1139b-2971-4808-8ed7-e9f84769ed7d按精确模型commandcode-plan/deepseek/deepseek-v4-flash完成实现，报告artifacts/ime-work/ti07-dsh-screenext-result.md。GetScreenExt移除文档读锁要求、非法view E_INVALIDARG、显示表面与文本布局版本解耦；真实AttachWindow路径每次只读查询当前客户区屏幕坐标，隐藏/最小化零RECT，错误不沿用旧坐标。GetTextExt读锁语义保持。变更仅隔离工具及其测试/说明。

Codex独立运行ti07-codex-screenext-final.ps1：Debug/Release各217正式测试+75自检，29项既有协议audit、6项作用域audit、16个默认/探针/trace隐藏宿主入口、MTA/STA原生smoke通过。新增独立screenext audit15检查全部通过，包括PerMonitorV2真实窗口隐藏、无锁显示、仅文本布局失效、移动/resize、最小化、恢复、再次隐藏，所有窗口状态操作不请求前台，实际前台未改变。原先3项失败全部修正。三保护文件SHA256与基线一致。沙箱内构建被进程限制阻止，按要求提升权限重跑成功。

证据：[codex-screenext-final](2026-10-20-ti07-tsf-prototype-review-evidence/codex-screenext-final/)，含Debug/Release输出、原生检查、独立audit结果及官方样例摘录。R24协议合规部分通过；不等于R15/TI07视觉通过。已打开真实scroll-follow --trace-layout-chain（无context/view主动探针）准备单变量对照，当前等待用户点击取得前台，不强制焦点、不模拟点击，不重复打开窗口。后续以真实结果追加结论。

### R24 修订后真实对照完成：协议通过，候选行为仍失败（2026-10-20）

真实scroll窗口先等待用户手动点击取得前台，之后自动检查并输入；未模拟点击或强制focus。scroll和clip两次均为实际微软拼音，前后GetActiveProfile的CLSID/profile一致且active，截图包含ni'hao及“你好”候选；MTA/DPI144。均不启用context/view主动探针，只记录trace-layout-chain。两次writes=0、cleanup_failures=0、exit3；Codex亲自查看全部5张原图，视觉结论均失败。

- **scroll**：行Y139→128，候选未动。通知ID9期间READ锁32有效，seq310/312 GetScreenExt实时返回client(91,125)-(829,489)，surface_only=true、failure=null；seq311 GetTextExt返回新generation11 RECT(107,128)-(195,167)，锁与通知均S_OK。seq304/319安全点前台/Win32焦点为自有窗口、document/top context相同、释放无错。
- **clip**：文字行bottom85 < clienttop125，文字完全不可见而候选仍显示；恢复文字后组合继续。通知ID9/READ锁31中seq306返回fully-invisible-zero-rect，seq305/307 GetScreenExt仍正确返回整个可见客户区，锁与通知S_OK。没有申请UIElement suppression；该场景未增加focus snapshot。

已保存完整原始日志、控制台、5PNG、精选事件及Codex独立visual-verdict：[R24滚动](2026-10-20-ti07-tsf-prototype-review-evidence/screenext-scroll-final/)、[R24裁出](2026-10-20-ti07-tsf-prototype-review-evidence/screenext-clip-final/)。独立原生surface审核源码和JSONL也随`codex-screenext-final/`归档。真实对照结果与修订前一致。

**结论与停止条件**：R24确为协议错误且修订已通过独立审核，但该单变量修订没有修复R15。停止在GetScreenExt上继续尝试刷新/锁变体，不把“候选不跟随”归咎于已经被排除的表面查询限制。R15/TI07继续开放，TI08未开始；不得用217/75或API S_OK替代视觉验收。尚未定位TSF已成功查询新几何后候选仍沿用原位置的内部原因。后续应先选择有正面对照的宿主差异实验，再单独限定实施范围；本轮不擅自迁移生产TSF、不增加自绘候选、不改变组合/提交语义，不提交推送。当前无活动DSH或GUI。

## R25 首审：原生 EDIT 对照工具（2026-10-20，未通过）

本轮建立原生 EDIT 对照，不代表生产修复或 TI-07 完成。Codex 独立隐藏原生 audit 13 项中 8 通过、5 失败：选区结尾读取未初始化内存；标准 EDIT 字符坐标错误地按 RichEdit 缓冲协议读取（两个字符样本失败）；负坐标错误；子窗口移动宣称上移11px，实际从(96,96)到(112,101)。父/viewport稳定、隐藏创建销毁与前台不变等对照通过。审核工具未显示窗口、未发送输入。

此外静态审查发现：场景六行内容不足以滚动；UI等待未泵消息；逐键焦点门、真实Pinyin身份、超时退出、组合前后不变量与EN_CHANGE观测缺失；快照混淆字符位置、组合起点和原生caret。全部修复前不运行真实输入，避免产生无效“通过”证据。

具体返修清单位于 `../../artifacts/ime-work/ti07-dsh-native-reference-review-fix.md`（本地实施材料）；独立审核源位于 `../../artifacts/ime-work/ti07-native-reference-codex-audit/`。继续DSH实现/Codex审核，不提交推送，不降低R15真实验收标准。


## R25 最终复核与真实原生 EDIT 对照（2026-10-20）

独立 Debug/Release 各 27 项正式测试、25 项自检、隐藏生命周期及扩展原生 audit 15/15 均通过。新增两项直接验证真实 runner 的 clip 接线，发现并修正了第二处父坐标混用。普通自有前台窗口允许一次初始子控件聚焦，未使用 TOPMOST/AttachThreadInput/合成点击；隐藏入口不调用 Show/SetFocus/前台请求。三份用户保护文件哈希不变。构建存在缓存的 NU1900 漏洞元数据访问警告，不影响编译/测试结果，原始输出保留。

真实运行 `native-reference-real-round2` exit3，Codex亲看7张原图。每场景实际微软拼音 active，DPI144，实时 `ni'hao` 前后一致；动作及恢复均无提交/结束通知，未观察到 EN_KILLFOCUS；Esc 清理另计。

| 对照 | 文字/宿主实际变化 | 原生候选可见行为 | 结论 |
| --- | --- | --- | --- |
| EDIT 内部滚动 | 首可见行0→1，文字行Y278→259，所有窗口不动 | 候选保持原位 | FAIL |
| 自有 EDIT 子窗口移动 | 上移11物理像素，父/viewport不变 | 候选同步上移11像素 | 正面对照PASS，不是框架修复 |
| 组合行完全裁出 | 行Y267→194，bottom213 < viewport top221；仍在父截图范围 | 候选上移但继续显示，原生IME预编辑浮层也在viewport外可见 | 隐藏FAIL |
| 裁出后恢复 | 子窗口恢复(0,0)，组合保留 | 候选重新位于恢复后的锚点 | 只说明恢复，无隐藏通过 |

证据：`2026-10-20-ti07-tsf-prototype-review-evidence/native-reference-real-round2/`（7PNG、完整JSONL、console、Codex verdict）；最终独立复核证据 `codex-native-reference-initial-focus-final/`。原生候选右侧可能超出父截图，但其左侧和中文候选始终清晰可见，足以否定隐藏，不据裁图推断其他窗口状态。

R25研究完成，R15/TI07仍未通过。已继续R26默认关闭的 `ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)` 激活对照，原生 `pbShow=TRUE`，不自绘候选/生产迁移/系统窗口操作。官方 UIless Mode Overview 明确允许保留TIP原生UI；是否有效以真实运行判定，不能凭flag或测试数量宣称完成。

## R26 首审：COM ABI 与激活所有权（2026-10-20，未通过）

Codex独立无原生调用的ABI审核4/9通过、5失败：`ITfThreadMgrEx`只声明两个扩展方法，CLR实际末槽为4，ActivateEx/GetActiveFlags落slot3/4，SDK要求slot14/15。C# `[ComImport]`基接口继承不影响虚表布局，必须显式重声明11个基方法；微软官方说明已读取： https://learn.microsoft.com/en-us/dotnet/standard/native-interop/qualify-net-types-for-interoperation 。不要与GeneratedComInterface的继承规则混淆。

因此DSH初步原生探测中“ActivateEx成功、flags为0”的结果不可解释为系统能力或时序问题；它调用的是错误槽。默认Engage末尾也无条件调用了错误扩展接口，可能错误调用Deactivate；必须先修ABI再运行。另有激活成功但后续flags验证失败时未记激活所有权、after-engaged失败未传递、默认路径受新QI影响等缺陷。

返修清单：`../../artifacts/ime-work/ti07-dsh-uielement-activation-review-fix.md`；独立审核源与baseline.log在`../../artifacts/ime-work/ti07-activation-codex-audit/`。不得修改审核源来通过，不作生产迁移或提交推送。

## R26（2026-10-20）：ActivateEx 对照通过协议审核，真实跟随仍失败

首版 ITfThreadMgrEx ComImport 继承声明错误，实际 ActivateEx/GetActiveFlags 落在槽3/4。Codex静态审核复现4/9，已要求DSH显式重声明11个基方法；旧版所谓系统GetActiveFlags时序失败结论作废。修正后独立ABI及激活失败/清理audit21/21，Debug/Release各234正式测试+80自检，8隐藏宿主矩阵、4原生自检命令全部通过。默认路径不查询扩展接口；ActivateEx模式初始化及engaged两处真实flags含0x4，唯一Deactivate，三保护文件hash不变。证据：`2026-10-20-ti07-tsf-prototype-review-evidence/codex-activation-final/`。

真实scroll使用已审核二进制、UIElement-only实际flags0x8000000D、当前微软拼音profile前后均匹配、DPI144。Codex亲看两张PNG：文字屏幕Y139→128，候选框保持原位，视觉FAIL；组合期间绑定零写入、清理零失败。证据：`2026-10-20-ti07-tsf-prototype-review-evidence/activation-scroll-real/`。pbShow TRUE之后Begin立即End，新激活方式未改变此行为，停止激活参数假设。

后续hide-probe第一次发生前台丢失并导致组合提交，第二次等待前台超时，均为无效验收，不能用于隐藏行为结论。未强夺焦点、未改系统配置、未提交推送。R15/TI07保持未通过。

下一项R27仅隔离原生UIElement生命周期：按微软UILess协议先Begin返回FALSE保留候选元素，再用ITfUIElement.Show(TRUE/FALSE/TRUE)请求其原生UI并检查组合不变、零写入。无自绘候选、无系统候选HWND操作，无生产迁移。若元素不存活、Show不支持或视觉失败，则结束该有界假设；初始化即抑制不能当作裁出隐藏验收通过。DSH指定模型实现，Codex独立审核后真实截图验收。

## R27 首审（2026-10-20）：暂不允许真实输入

首版native-ui-lifecycle存在控制流缺陷：动作前仅worker线程检查前台，UI排队后未复核；等待后无条件截图，失焦/组合变化不阻止后续Show；GetFocus在worker读取错误线程；实时文本/组合范围只日志未比较；异常和早退finally未执行本场景组合teardown。现有observer测试不能验证这些执行器路径。

返修要求：实际执行器在自有UI线程核对焦点、原子组合/文本/span/writes/profile快照，每阶段及等待后检查；状态变化立即停止；所有退出恢复抑制且安全teardown，失焦不发送Esc到其他应用；用同一实际执行器的注入测试覆盖失败、异常和成功顺序。详见`artifacts/ime-work/ti07-dsh-r27-review-fix.md`。仅隔离工具，不改生产范围；修正并独立复审后才做真实候选实验。

### R27 续审与模型切换（2026-10-20）
用户要求模型改为workbuddy-ai/deepseek-v4.1-flash，旧ACP子进程已停止，既有修改保留。续审仍发现动作前未比较完整基线、profile来自缓存、截图有效性未检查前后台及组合状态等缺陷，已准备具体返修文档artifacts/ime-work/ti07-dsh-r27-model-switch.md。新模型启动因自动审批拒绝尚未执行；TI07仍未通过。


### R27 独立失败复现（2026-10-20）

Codex在当前源码构建后，现有258项托管测试全部通过，但共用执行器的独立audit仅7/21通过。证据保存在同目录证据文件夹`codex-r27-lifecycle-baseline/`，包括独立源码、原始输出和现有测试输出。未运行GUI或注入输入，三保护文件hash不变。

- 动作前text/span已不同于baseline时，仍执行完整3次Show且返回成功；动作前writes改变时至少错误执行1次Show才停止。
- profile改变或Composing=false但text/span未变时，仍执行3次Show且返回成功。
- 初始writes非零、阶段间writes增加后保持稳定时，仍执行3次Show且返回成功。
- 清理回调返回但组合仍存活时，teardown报告Cleaned。现有测试使用不清空组合的回调并期望Cleaned，无法证明清理成功。

此外实际宿主profile字段仍来自缓存_scenarioIdentity，截图valid_evidence仅由文件创建成功决定；源码必须修正，不能只修改audit使之通过。完整返修范围见`artifacts/ime-work/ti07-dsh-r27-model-switch.md`，另需确保teardown检查清理后组合状态，未清掉则报告失败或明确交给session清理。

指定新模型workbuddy-ai/deepseek-v4.1-flash的ACP启动仍被自动审批阻止；没有改用旧模型或绕过审批，尚无新模型实现。真实候选跟随和裁出隐藏仍未通过。

### R27 新模型首轮复审（2026-10-20）

用户明确授权后，新模型workbuddy-ai/deepseek-v4.1-flash经ACP确认并完成首轮返修。Codex自行重建并运行独立生命周期audit，7/21提升为21/21，未修改审查程序。完整执行器不变量和teardown剩余组合误报已修。

真实接线仍未放行：ScenarioLifecycleHost.Capture直接调用worker上的CaptureLifecycle，后者直接GetFocus/读取store并查询active profile，未经过UI dispatcher；ProbeLifecycleSafePoint计算focus/isMicrosoftPinyin却没有将不匹配判为失败，且profile查询在锁/焦点有效性检查前。初始非拼音或查询失败可能被接纳为基线。该缺陷无法由单独Fake ILifecycleHost的21项检查覆盖。

第二轮返修要求已准备于artifacts/ime-work/ti07-dsh-r27-wiring-review.md：实际UI线程接线、初始焦点/profile/无锁条件拒绝、完整身份、截图前后有效性及真实共用实现测试。当前不允许真实GUI验收，不以21/21代替候选视觉通过。生产范围未变化。

### R27 中断后工作区复核（2026-10-20）

Codex在DSH旧请求确认终止后，独立对当前源码执行 `dotnet build tests/CSharp.UI.TsfHostCheck.Tests/CSharp.UI.TsfHostCheck.Tests.csproj --no-restore -m:1 -nr:false -v quiet`，退出1：`NativeUiLifecycleExecutorTests.cs:348` 的 `new FakeHost { Profile = "unknown" }` 引用未定义的Profile属性（CS0117）。这是旧模型RPC中断留下的未完成测试修改，必须由实现方补齐真实FakeHost profile行为及安全点拒绝语义，不能删除失败断言或伪装通过。构建失败后未运行旧测试二进制充当当前源码验证。输出：artifacts/ime-work/ti07-codex-r27-current-build.log。

源码接线问题仍存在：ScenarioLifecycleHost.cs:60 的Capture直接调用worker线程上的CaptureLifecycle；ScenarioRunner.cs:881的安全点分支仅验证foreground，未拒绝focus/profile/读锁不符。当前禁止真实输入。

DSH会话模型devin/swe-2已通过无任务/无工具的配置专用ACP确认。派发代码返修仍被自动审批阻止，等待用户明确授权该新模型源码访问、隔离代码写入和测试；未回退模型，无生产迁移、自绘或提交推送。

## R27 独立复核及真实Show结果（2026-10-20）

DSH devin/swe-2 完成隔离工具接线返修。Codex独立Debug/Release各283正式+82自检、31项生命周期及完整身份audit、9项真实隐藏窗口线程接线、4隐藏MTA/STA宿主通过。生产源码哈希和三保护文件未变。证据codex-r27-final。

真实native-ui-lifecycle有效运行：UIElement-only flags含0x4，当前微软拼音完整身份为0804/inputprocessor/固定CLSID与profile，ni'hao组合0..6，前台及自有焦点、锁0、布局通知深度0全部有效。初始pbShow=FALSE保留了live候选元素并持续Update；Codex亲看r27a截图，组合文字可见、候选不可见。此初始化抑制本身不算裁出隐藏验收。

请求Show(TRUE)时同步EndUIElement(id0)：日志seq220合法before，seq221 End，seq222工具报告E_FAIL/after=null，随后失败停止、Esc只在teardown，writes0/cleanup0。原始图与JSONL已存r27-native-lifecycle-real。

Codex发现观测可靠性缺陷：ElementInfo.TryShow调用原生Show期间End可同步Dispose/ReleaseComObject，调用返回后继续IsShown可能抛异常，catch会把已返回的HRESULT覆盖为合成E_FAIL。当前结果不能据此断言系统Show返回E_FAIL。下一最小修订为重入End标记与在途调用借用/延迟唯一释放、真实ShowHRESULT与后置查询异常分离；S_OK后End可在完整baseline/焦点守卫下仅补诊断图，不对已End元素继续Show，不把整体失败变通过。任务ti07-dsh-r27-reentrant-show-task.md。当前无GUI活动、未提交或迁移生产，TI07仍未通过。

R27同步End问题另有Codex纯托管独立复现：6个检查中3个失败，合计34/37。Show实际返回S_OK后，后置IsShown异常被覆盖为E_FAIL；Dispose同步发生后仍进行后置IsShown；Dispose后Refresh/TryShow仍触发原对象调用。原始输出与源码已附r27-native-lifecycle-real/reentrant-baseline*，无GUI/input。


## 2026-10-20 R27 结论与 R28 对照

R27工具生命周期观测缺陷修正后，Codex独立Debug/Release293正式+82自检、38项纯托管/重入audit、9项真实隐藏接线和4隐藏宿主均通过，生产及保护文件hash不变，证据codex-r27-reentrant-final。

真实微软拼音原生Show(TRUE)实际返回S_OK，同步EndUIElement；400ms后同一ni'hao/0..6组合、零写入、自有focus/foreground、完整profile继续有效，但原生候选未在预期窗口截图显示，且元素已结束无法再合法执行hide/restore。Codex亲看初始抑制与native-shown-ended两图，原始证据已保存r27-native-lifecycle-reentrant-final。此前E_FAIL是工具观测污染，已纠正；本结论不是“系统返回E_FAIL”，也不是初始化抑制已经通过动态裁出隐藏。R27假设停止，TI07仍开放。

R28下一有界对照在NativeEditReference里增加仅显式启用的child-collapse：只把自有EDIT高度变0并恢复原尺寸，NOACTIVATE|NOMOVE|NOZORDER，不隐藏窗口style、不SetFocus、不改输入法、不操作系统候选HWND。依据R25自有child移动已能驱动候选的正面对照，探究输入表面完全收缩是否可隐藏候选且保留实时组合和焦点。微软SetWindowPos文档仅说明几何API，不保证本假设的IME行为：https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos 。

先DSH devin/swe-2实现与托管测试，Codex审核/隐藏几何接线后真实单变量collapse/restore，核对live GCS_COMPSTR、end/result/change及focus事件、前后profile和截图。旧默认三场景不变。若组合提交/结束、焦点丢失、候选不隐藏或不能恢复，则立即停止此假设；不以重新输入/取消补救或降低验收口径。若通过也只是宿主对照，后续仍须隔离TSF自有child输入表面和内部scroll实测，不能直接宣称框架修复。无生产迁移或自绘候选授权，无提交推送。

补充只读路线核对：2026-10-20读取Chromium当前官方镜像的tsf_text_store.cc和input_method_win_tsf.cc；其布局通知和范围几何也通过TSF store/context路径，没有从本次阅读取得可解释当前失败的新实测依据，因此不据此重启盲目刷新变体。来源：https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/tsf_text_store.cc ；https://raw.githubusercontent.com/chromium/chromium/main/ui/base/ime/win/input_method_win_tsf.cc 。当前继续R28单变量原生子表面对照。


## R28 初审与真实隐藏复现（2026-10-20）
初版ResizeEditChild将合法零面积矩形当失败，导致无法恢复原尺寸；RunScenario缺少finally，action之后先截图再检查profile/实时组合，Check失败也继续执行，未满足新场景fail-fast要求。Codex真实隐藏audit25/27：实际自有EDIT缩为高度0且clientheight0、位置宽度/style/祖先/focus保持，但恢复失败、实际高度仍0。无显示、前台请求或输入，窗口已全部销毁。证据r28-collapse-baseline。返修要求artifacts/ime-work/ti07-dsh-r28-review-fix.md。真实IME实验尚未放行，TI07仍未通过。


## R28 最终结论与 R29（2026-10-20）
Codex独立Debug/Release各41正式测试+27自检、2隐藏smoke、27/27真实隐藏几何audit通过；合法零高度恢复和守卫修复已复核，生产/TSF/三保护文件hash未变。证据codex-r28-collapse-final。

真实child-collapse运行exit3仅表示待视觉审核。Codex亲看3PNG：EDIT高度420→0→420，候选始终可见，collapse时移到父窗口上方区域，restore后回原位。实时ni'hao组合不变，测量期end/result/change/focuschange均0，当前微软拼音0804有效。隐藏FAIL，R28假设停止。原始图/日志/visual verdict已存r28-collapse-real。native_caret_screen字段实为未转换的client rcCaret，不能用于屏幕几何推理；本结论用真实PNG与已映射的EM/组合坐标，不受此错误影响。

R29拟在隔离TsfHostCheck显式--child-input-surface下，让真实单行输入child承担TSF关联/GetWnd、焦点、键盘和绘制，固定viewport内容器scroll移动该child；真实祖先交集用于GetScreenExt/GetTextExt。验证宿主表面差异，不再重复height0或UIElement Show刷新。范围与不变量详见artifacts/ime-work/ti07-dsh-r29-child-input-surface.md。DSH ACP devin/swe-2实现，Codex独立审核后真实验证；默认路径不变，不迁移生产或自绘候选。TI07未通过，无提交推送。

R29完整派发runner63123已在30分钟prompt超时后正常退出；ownedSessionClosed=true，process exited，未强制终止。独立hash比较原有隔离源码0变化，无新源码，无GUI/input。观察到126次工具读取及压缩后重读，不能作为实现成果。
已拆R29A仅TsfHostWindow/DisplaySurface/目标托管测试，暂不改Program/ScenarioRunner/InputScript。新ACP runner83458，session861e8d88-fbcd-4e9f-9fad-3a6670465b0a，devin/swe-2 confirmed=true。任务ti07-dsh-r29a-host-only.md。等待完成后Codex接新Create(...childInputSurface:true)独立audit，成功后另派R29B场景/每键守卫。无活动GUI，TI07未通过。不要并行派重复任务。
