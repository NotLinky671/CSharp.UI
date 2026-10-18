# TI-04 组合状态机审核

审核日期：2026-10-18

状态：2026-10-18最终独立复审通过，R1–R6及追加返修项已解决；允许DSH按工作流提交并推送本次实现。下文保留首版发现与返修过程，最终结论见文末。

依据：[TI-04任务及2026-10-18语义补充](../task/04-composition-state-machine.md)。本轮微软拼音仍免测，核心测试不改变系统输入法。

## R1 [P1] 分段重基范围会覆盖已提交结果

首版`TextFieldComposition.AdvanceAfterResult`把下一段替换区保留在`replacement.Start`，并按原选区上界夹取end。Start选择[0,2)后提交“你好”并带NextPreedit“shi”时，下一段应从真实提交后caret2继续，范围通常[2,2)，不能仍为[0,2)。当前下一结果会覆盖先前确认的“你好”，显示串也会把它隐藏。按真实After.Selection/文档版本重基，Cancel只撤销未提交段，明确保持已提交段及未选中后缀。用非空原选区、前后缀、多段不同长度、MaxLength部分接受的实际TextField测试。

## R2 [P1] 无Start结果忽略版本和显式替换范围

首版CommitResult无活动段时没有检查e.DocumentVersion与当前版本一致，且使用document.Selection.Range替代e.Replacement。TI02工厂不带selectionBefore不表示可以忽略版本；过时/未来的结果不能按最新caret猜测写入。必须验证当前epoch、精确版本与事件显式replacement，字素/范围合法才按该锚点提交。补当前caret与replacement不同、未来/过时版本的实际两字段测试。

## R3 [P1] Cancel/End必须校验身份与次序，拒绝事件不能消耗有效ID

首版TryApply对Cancel直接CancelActive而不校验compositionId/sequence/version；旧组合取消会影响当前新段。End也需精确epoch/版本语义，重复取消/结束幂等不意味着可以取消另一个活动段。TryAdmitNewCompositionId在payload/版本验证前已经更新最高ID，导致无效Start消耗ID并拒绝后续合法重试；不要在验证前提交状态。单调ID允许跳号（只需严格递增，跳号不占额外内存），不要把有界内存误解为仅能+1。使用单调水位确保超过最近16个结束ID后旧重放仍拒绝；移除不必要的近期ID数组，保持精简。

## R4 [P1] 结果拒绝/无变化的消费与报告不正确

首版空结果空范围返回ResultCommitted但没有真实事务变化，违背已冻结契约。拒绝的活动结果只更新seq并保留旧段，无Start拒绝路径也可能没更新seq，不能确保同事件不再次调用setter。按真实事务结果报告实际动作，空/净化后空、无变化和拒绝不谎报ResultCommitted；仍须退休该结果序号/段并按NextPreedit明确继续（正确保留前选区，不删原选区）。已处理前缀后后续失败时不能Handled=false宣称整批没处理，否则会引起宿主默认字符重复；返回真实前缀动作/处理数并定义Handled语义，附测试。

## 后续复审

上述为正在实施的首版代码问题，DSH完成前须逐项核对与补真实客户端集成回归。不在本记录宣称尚未实现的核心/原生能力通过；Code/测试由DSH实现，Codex独立复查并持久保存证据后才放行提交。

## R5 [P1] 会话生命周期与取消必须保留同节点状态并隔离重入

首版TextField.OnUpdate在注释“同节点同绑定保留”后却无条件CancelCompositionForHostAction，会让每次组件重建取消组合。Arrange/Emit也只能在真实外部变化时取消，不能布局即取消。Cancel需恢复活动段开始前有方向的selection；先撤销epoch/旧客户端归属再通知平台，当前未调用service.RequestCancel也无法通知原生取消（需延迟到安全阶段，不在Measure/Emit里调用外部服务）。

首版运行时_textInputLeases List每次焦点切换Add，到Dispose才清理，焦点切换会无界增长。只保留当前租约/单调epoch即可。TextFieldState.RequireComposing复用已IsReleased客户端，则重新聚焦后仍无法输入；新epoch要新客户端/清空旧身份，不能让旧引用Session动态指向新租约。旧节点OnUnmount不得调用共享session.End而结束当前其他节点会话，应按客户端/lease所有权条件结束。

StartSession/EndSession可同步调用SetFocus/Dispose/抛错。首版Start返回后无条件ServiceSessionStarted=true且catch清空共享owners/Lease，可能覆盖重入中创建的新会话；End通知返回后也需校验状态。用runtime真实双字段、Start/End中重入切焦点、外部TextField卸载时另一个字段已获得焦点、同节点重新聚焦等测试。服务可变集合遍历回调要快照或单当前客户端，避免重入修改集合异常。

## R6 [P2] 完整客户端读取与历史失效接缝不可丢失

首版ReadDocument仅返回Editor缓存，没有同步外部Binding；实时组合回调需在验证之前读实际值，外部内容/来源/MaxLength变化须取消并换epoch，不能先执行结果后再发现失效。同步不得在原生组合回调中GetLayout/Frame/递归处理待办（核心StringInfo足够限制选区，几何后续安全布局）。ConsumeEditorInvalidation调用ConsumeInvalidation连同History位一起清除，会使未来TI05看不到需清空历史的事实，按独立消费者位处理，不提前实现历史。

## 首次实际运行时复现

外部Console引用当前已编译核心DLL，注入ITextInputService替身，操作真实TextField客户端（不是独立helper）：Start选择ab→Result你好+NextPreedit shi→Result世，最终绑定变为“世”，而正确值应“你好世”。两次setter均执行，确认R1会覆盖已提交结果。首段预编辑正确保持Binding=ab且writes=0，初步基础路径已生效。输出和源码见codex-segment-initial.log及对应txt。

该程序第二次重新聚焦后未来版本结果未写入，但此时重获焦点的客户端已释放，不能据此证明R2版本检查已修复；需独立新运行时验证。

R2/R3/R5独立运行时确认（各自全新UiRuntime，避免失效客户端干扰）：无Start的v999结果实际写入BADab；显式replacement[1,2)应得到aX，实际按caret0写成Xab；旧组合0的Cancel取消了当前组合1；同节点重新聚焦复用已释放客户端，Start被Rejected。见codex-identity-initial.log与源码。均为真实TextField+注入服务，非helper判据。

## 返修中代码复查补充（仍须完成后实测）

- `FocusManager.SetFocus`在通知旧服务之后无条件继续设置外层目标。EndSession内同步SetFocus其他节点时，外层会重新标记旧目标IsFocused=true并启动其会话；服务层generation不足以保护焦点管理器。焦点切换和OnNodeUnmounting均需保证重入后只保留最终真实焦点。
- 状态机`Apply`当前只在循环前调用一次readDocument，注释却声明每个事件之前同步；setter里同步换来源/卸载后同批次后续事件必须重新验证归属与真实文档，不能用旧CompositionDocument继续。
- `ContinueAfterResult`未提交且带NextPreedit时直接`Active!.Value`；无Start的空/拒绝Result没有Active。应建立正确的下一段锚点，不能抛异常。空非空替换区分支也须退休无Start身份，防止重复结果再写。
- `RequestNativeCancel`在客户端先本地Cancel后才检查_owner.IsComposing，因此正常取消不会排队。延期通知必须绑定旧归属/代际，不能后来取消新的段；RearmFor在End回调之后需重新检查当前焦点和新owner，不能用回调前resumable结果覆盖重入的新会话。
- 客户端catch里再次CurrentDocument会重复调用抛错getter；错误路径应使用最后可靠快照，避免托管异常越过服务回调。同时不能丢失已执行批次前缀的动作。
- `ApplyCompositionCommit`成功路径仍调用EnsureCaretVisible，内部GetLayout/ClampIndex依赖文本布局；异常恢复也调用ClampSelectionToText。组合回调必须只以核心字素规则限制选区并标记待布局，不能因结果提交直接进行文本排版。仅改SyncCompositionDocument不足以满足此边界。
- `NotifyCompositionKeyOrigin(token, true)`记录调用方token，但OnKeyDown实际用state.NativeKeyToken常量判定，而DispatchKey/KeyEvent没有携带该token的路径；公开接缝不能真正阻止结束组合后的同一按键继续移动光标或Tab。应让真实输入分发传递/消费同一次按键的显式身份，测试必须通过公开UiRuntime分发，不只直接测试helper。
- `EndSegment`在成功结果后把_lastSelection设回Active.SelectionBeforeComposition（无Start则0），导致TextInputApplyResult.Document的selection不等于实际Editor提交后的caret；同样SyncDocument未组合时不更新_lastSelection。返回快照必须包含真实文本、版本和当前选区，取消恢复则走明确的恢复分支，不能混用。

### 返修第一轮独立实测（21通过、7失败）

使用DSH首次返修构建出的核心DLL执行真实运行时外部审计，日志`codex-extended-r1.log`。原先分段覆盖、未来Result、显式范围、旧Cancel身份隔离、跳号、重新聚焦等已通过。仍失败：活动段未来版本Cancel直接结束组合；无Start空Result带NextPreedit不能继续且下一结果锚点丢失；getter持续抛错时异常越过ApplyComposition；EndSession同步切回前字段后外层焦点处理污染IsFocused和实际写入目标。日志End未来版本检查接在Cancel失败之后，仅证明前段已丢失，独立End版本用例将在后续复测时分开。

第二轮扩展实测`codex-extended-r2.log`：31项中23通过、8失败。Cancel和End未来版本现已各用全新运行时独立确认均错误清除了预编辑；本地取消通知及无Start空结果退休ID已通过。新增R6确认：先外部修改绑定，再读取client.Document得到新版本，仍持旧epoch的Result使用该新版本可写入BAD，说明仅设置EpochInvalidationRequested没有立即撤销旧归属。必须在检测外部变化时立即使旧客户端失效，不能只等安全阶段换epoch。源码副本同目录codex-extended-r2.cs.txt。

第三轮以DSH build-final构建出的DLL重新独立执行，32项中23通过、9失败（`codex-extended-r3.log`/同名cs.txt）。新增返回快照断言确认：无Start Result明确替换[1,2)把ab变aX，真实Editor caret2，但返回Result.Document.Selection仍caret0。不放行；其余8项失败与第二轮一致。

第四轮以DSH核心451通过后的构建独立复测，新增文本服务计数监测，34项中27通过、7失败（`codex-extended-r4.log`/同名cs.txt）。无Start空结果续段已修复；所测成功组合回调没有调用文本布局，继续保留该边界回归。仍失败的7项：结果快照选区不实、外部观察后旧epoch仍可写、未来Cancel及End清除有效组合、持续getter异常越界、EndSession重入后的焦点标志与写入目标错误。键盘token公开分发问题仍需独立确认/修复。

### 第二轮返修需额外核对的生命周期边界

- 焦点代际检查必须位于每次外部回调之后，包括ReleaseTextInputOwnershipFor返回之后、调用previous.OnFocusChanged(false)之前；否则EndSession内重新聚焦原节点后，外层旧失焦回调会结束原节点刚建立的新客户端。双字段测试需同时断言焦点标志、服务ActiveClient与最终写入目标，不能只断言焦点指针。
- Window失活期间程序调用SetFocus(null)再SetFocus(field)，逻辑焦点可以更新，但不得在窗口重新激活前StartSession；Start/Resume/Rearm都须遵守窗口有效性。此边界将随独立复测验证。

### 第二轮返修首次构建的独立实测：仅余两处失败

`codex-extended-r5.log`及同名cs.txt：实际执行40项，38通过、2失败（End服务客户端为空，后续写入断言因无客户端跳过；全部通过时应执行41项）。上述快照、外部观察后旧epoch、未来Cancel/End、getter异常、成功及错误路径不排版、setter写后抛错返回真实文档，以及公开DispatchKey(e, token)的结束键归属均已通过。

仍未通过：①NotifyWindowActivation(false)后SetFocus(null)再SetFocus(root)，service.ActiveClient非null，失活窗口仍启动会话。②EndSession内同步重新聚焦原字段，焦点标志现在正确，但ActiveClient变null；原因正是上面所述Release返回后外层previous.OnFocusChanged(false)结束了刚重新建立的同节点新客户端。代际检查必须在Release之后、previous.OnFocusChanged(false)之前，且在该回调返回后再次检查。请在完成本轮前修复并补实际双字段服务断言。

## 最终独立复审结论（2026-10-18）

通过。R1–R6与追加问题均已返修；独立真实运行时验收41项全部通过（codex-extended-r6.log、同名cs.txt及二进制哈希），覆盖分段提交、显式范围/版本/ID、外部变更立即失效、无Start续段、异常回读、批次前缀、窗口失活/恢复、同步焦点重入，以及公开DispatchKey(e, token)结束键归属。成功与setter写后抛错恢复路径未在组合回调中执行文本排版。

Codex最终执行：

| 检查 | 结果 |
|---|---|
| 缓存解决方案构建 --no-restore -m:1 -nodeReuse:false | exit 0，0警告/0错误 |
| 核心测试 | 467通过，0失败/跳过（405原有 + 62新增TI-04） |
| 文本测试 | 67通过，0失败/跳过 |
| 集成测试 | 30通过，0失败/跳过 |
| OpenTK测试 | 242通过，0失败/跳过；实际运行Windows窗口、OpenGL及剪贴板测试 |
| 独立运行时验收 | 41通过，0失败 |

合计806项工程测试，另加41项独立检查。完整输出见本文件同名-evidence目录中的codex-final-*.log；构建为codex-final-build.log。早期失败日志作为过程证据保留，不代表最终状态。

边界：本任务仅完成核心组合状态机与模拟服务/宿主接缝。预编辑绘制及几何属TI-06，真实平台消息桥属TI-07；未进行微软拼音或其他真实IME验收，未变更系统输入法配置，也不宣称macOS/Linux实机组合输入通过。整个文本输入计划尚未交付，TI-05～TI-10继续按依赖推进。

提交与推送：由DSH在本结论后执行，实际提交标识及远程状态将在完成后补记。
