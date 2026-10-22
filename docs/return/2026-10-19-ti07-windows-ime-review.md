# TI-07 Windows IME桥审核

审核日期：2026-10-19。

状态：实施中复审，尚未通过。首批问题与返修复测按章节保留；R13已通过，最新阻塞为R15真实矩阵与验证脚本。25项独立检查通过不代表整个TI-07真实验收通过。

依据：[TI-07](../task/07-windows-ime-and-host-routing.md)、TI-02冻结契约、TI-04生命周期与TI-06几何。微软拼音已按用户最新授权恢复主要验收，路线对照见[微软拼音探针记录](../agents-notebook/2026-10-19-ti07-mspinyin-route-check.md)。Codex负责本记录，Devin实现返修后待独立复测，不提前提交。

## R1 [P1] 按结果字符匹配的回声队列没有真正消息归属，拒绝/转换结果也可能再次写入

WindowsImeBridge.ApplyBatch只在ResultCommitted后把结果逐字符加入_echoUnits；TryConsumeEcho只比较下一个字符值，任意后来的WM_CHAR/WM_UNICHAR都能消耗，没有原生消息/链式默认处理范围归属。这仍是按文本推测回声；序号和位置只是队列索引，不是原生输入身份。结果被setter拒绝/转换或已消费但未产生实际文档变化时，不会登记回声，默认WM_IME_CHAR→WM_CHAR→GLFW路径仍能再次调用Binding或落到其他焦点。

必须以实际已消费的原生结果消息/来源链确定唯一提交通道，不能仅以ResultCommitted或字符相等判断。测试需要：setter拒绝/转换/异常下不再次写入；结束后延迟回声；没有回声时随后相同的普通字符不能被误吞；失焦/换epoch后旧结果不能写入新客户端。日志/Trace失败不能让已经处理的结果落回默认路径重试。禁止改成固定时间窗或全局字符串去重。

## R2 [P1] 本地已取消后RequestCancel直接返回，平台实际未取消

核心按冻结顺序先取消预编辑、再在安全阶段调用服务RequestCancel。此时ActiveClient.IsComposing已经是false。桥的RequestCancel以“_activeClient is not { IsComposing: true }”直接返回，导致ImmNotifyIME(CPS_CANCEL)根本未执行。EndSession也只清本地字段，未结束仍存在的原生组合。将候选排除区设为整个客户区不是撤销/取消，系统可能仅把候选移到窗口外。

需要独立跟踪原生待取消段/所属epoch，在正确安全阶段按所有者通知原生，保留先撤销旧客户端、再允许平台同步回调的TI-04边界。覆盖鼠标/Cut/Paste/Undo、外部绑定、失焦/失活/隐藏/卸载/关闭；验证真实微软拼音取消及候选消失，而非只断言API调用或本地IsComposing=false。

## R3 [P1] 无GCS标志的WM_IME_COMPOSITION取消被忽略

ImeEventTranslator.OnComposition在!HasPreeditData且无非空结果时返回空事件。Windows明确规定无GCS_*标志的WM_IME_COMPOSITION表示当前组合取消，自绘应用应删除预编辑。此路径不能依赖未来必定出现END才能清理。

按已有段身份产生正确的取消事件、恢复最近正式文档与该段前选区；补单独flags=0且没有随后End的测试，并保证重复取消幂等、分段已提交结果不回滚。

参考：[Microsoft WM_IME_COMPOSITION](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-composition)。

## R4 [P2] 改变公开DispatchKey返回类型破坏冻结接口

UiRuntime.DispatchKey两个公开重载从void改为bool。直接调用后忽略返回值仍能编译，不代表接口兼容；已有Action<KeyEvent> handler = runtime.DispatchKey等用法和已编译调用方会受影响。未批准这种公开契约变更。

Codex决定：保留原公开void重载；新增最小internal返回处理结果的分发入口供OpenTK使用。核心已经向CSharp.UI.OpenTK开放InternalsVisibleTo，不需要扩大公开API。InputRouter为internal，可直接返回bool。补公开方法组兼容和宿主实际消费结果测试。

## R5 [P1] 组合提交回调换焦点后，旧批次会覆盖新会话桥状态

ApplyBatch调用client.ApplyComposition后，无条件对共享_translator.NoteApplied和_echoUnits写回。Binding setter可同步切换焦点、卸载或换源，导致桥StartSession已为新客户端重置translator；旧批次返回后仍会把旧文档版本/替换区和回声写入新会话。核心已隔离旧租约，不代表桥自己的状态也自动隔离。

对原生快照、批次应用和收尾副作用加入所属客户端/会话代际校验，回调后重新验证，不覆盖或取消新会话；不要用清空所有全局状态破坏新客户端。补真实UiRuntime+脚本Binding在Result期间切换焦点的回归，验证新会话的下一段版本、范围、字符来源以及旧延迟消息不串写。

## 后续门槛

以上为静态首审，待实际运行验证；还须复审宽接口错误/奇数字节/非法UTF16、结果+下一段的caret/分段属性、原生按键生命周期、坐标和同值去重、实际子类化恢复/异常边界，以及真实微软拼音生产宿主完整矩阵。不能用探针路线成功或合成消息替代本项真实验收。

## Codex 独立运行补审（2026-10-19，第二轮实施中）

独立控制台 `artifacts/ime-work/ti07-codex-audit` 引用已编译生产 DLL，不重新构建生产工程；真实 UiRuntime/TextField 加 IImeInterop 替身。输出见 `artifacts/ime-work/ti07-codex-audit-result.txt`。扩充后的六项检查一项通过、五项失败，仍不通过：

- R1 仍未解决：把 Queue<char> 改为 List<string> 并在 START/END 清理，依旧仅按字符相等吞 WM_CHAR/WM_UNICHAR。组合窗口范围不是字符来源身份。提交 `a` 后、END 前没有默认回声，普通 WM_CHAR `a` 仍被吞掉。生产桥不得用这种匹配猜测。应明确处理过的结果是否进入默认链，并证明普通字符通道不受影响；测试不得直接把任意同字符标成“回声”来证明正确。
- R6 [P1]：仅携带 GCS_RESULTCLAUSE（0x1000）的消息被当作取消。`!HasPreeditData && !HasResultFlag` 不等于没有任何 GCS_* 标志。实际独立检查中已存在的 `abc` 预编辑因此被清掉。应针对完整原生标志区分取消与其他数据更新，覆盖 RESULTREADSTR/RESULTCLAUSE 等只有结果元数据的消息。
- R7 [P2]：逻辑锚点不变时 UpdatePointerTransform 只更新比例，没有重新发布已缓存几何。先发布 (30,10,2,20)，再由1倍改为150%，候选调用次数未变、仍停旧像素位置。核心同值几何去重不能负责宿主坐标变换失效；需宿主重新发布有效锚点，并覆盖窗口移动/DPI/尺寸与可见性变化。

同消息结果+下一段预编辑保留字符串的检查通过，但仍须另查其 cursor 和 clauses，不能用字符串存在代替属性正确。宽接口当前仍使用默认替换式 UTF-16 解码，未配对代理项会被改成 U+FFFD 后作为合法结果提交；应保留拒绝语义，并验证负数/奇数字节/回读超界/长度上限。

## R8 [P1] 系统组合窗屏蔽常量写成候选窗位

本机 Windows SDK `C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/imm.h` 定义 `ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000`，`ISC_SHOWUICANDIDATEWINDOW = 0x00000001`。生产 Win32Ime 却把前者写成1，WM_IME_SETCONTEXT 因而屏蔽第一候选窗、保留系统组合窗，与自绘预编辑要求相反。现有测试也直接使用1和2，因此只重复了错误定义而没有验证系统语义。修正常量，使用真实掩码0x8000000F测试：只清高位，保留所有候选位；实际微软拼音必须可见且没有第二套系统预编辑。同步核对全部原生常量与结构布局。

## R9 [P2] 结果+下一段快照丢失原生 caret 和分段

独立检查：活动预编辑后，同消息提供结果 `A`、下一段 `xyz`、cursor=1、clauses=[0,1,3]；下一段字符串保留，但 TextFieldCompositionClient.PreeditCaret 与 Segments 不匹配原生属性。翻译器仅设置 Result.NextPreedit 就返回，结果工厂不含下一段属性，核心默认 caret=末尾、segments=[]。应按同一消息真实快照在结果处理及真实文档重锚后更新下一段属性，不猜版本、不额外写 Binding；覆盖结果拒绝/净化/换焦点时不污染后续段。

## 真实宿主工具门槛

HostCheck 当前仍调用 TemporaryProfileAttempt；其旧 Scope 可能改 profile 启用状态。运行前必须改成验证已启用身份并只选择当前进程/自有窗口，禁止启用/禁用 profile。Enter 场景 `writes_delta <= 1` 会把零提交当通过，且未见证 OnSubmit 是否泄漏；普通 ascii 在中文模式下不切换、不验证独立写入次数；缺数字选词、Unicode、滚动/断点/焦点场景。完善后再跑完整矩阵。候选 API 调用计数只算发布证据，实际候选窗截图/位置仍须独立核验。

## R10 [P1] 安装后初始化失败会释放仍被原生引用的委托

InstallSubclass 在成功 SetWindowLongPtr 后设置 installed，再调用 EnsureContext/ReleaseContext。后两者若抛异常，catch 无条件 `_wndProcDelegate = null`，却没有恢复原过程，原生 HWND 仍保留托管回调函数指针。必须区分安装前与安装后失败，已安装则保留委托并验证恢复；恢复失败继续保活。补失败注入或实际 HWND 故障路径测试；不得仅在 Dispose 中 GC.KeepAlive 一个已经置空的字段。

## R11 [P1] 无焦点开始的原生组合不能转归后来聚焦的客户端

扩展独立检查：SetFocus(null) → WM_IME_STARTCOMPOSITION → SetFocus(TextField) → WM_IME_COMPOSITION(GCS_RESULTSTR="OLD")。当前桥使用 `_compositionOwnerClient ?? _activeClient`，把原本没有归属客户端的旧组合当作新客户端自己的组合，导致 `OLD` 写入新输入框。必须区分“没有原生组合”和“原生组合存在但owner=null/已撤销”；后者不可回退到新active。补无owner、owner释放、显式新START恢复以及合法无START结果的分离测试。

扩展后独立审计共10项，4通过、6失败（仍基于返修前Debug DLL）。两个公开void DispatchKey方法组兼容、重复无标志取消及分段取消保留既有结果通过。首轮GUI只取得MTA/DPI144、前台不可用、卸载恢复成功证据；没有文本输入验收结果，未通过。真实GUI验收前去除未经审核的鼠标点击fallback并修复工具提前失败不退出的问题。

## 返修中 R9 接口约束

下一段属性可以采用最小 internal 桥接，但不得把原公开 `CompositionEvent.Result` 七参签名直接改成九参可选参数。即便两个属性标 internal，public 工厂新增参数仍改变公开方法签名和方法组/二进制兼容性。保留原七参入口；额外 internal 工厂或内部初始化由桥使用，并冻结分段列表。扩展检查需包含原七参 Func 方法组编译与运行，及列表外部修改不影响事件快照。

## R12 [P1] 去重返修不能破坏仅提交文本降级路径

当前返修把 WM_IME_CHAR 按 `_subclassInstalled` 全部吞掉，然而 Capabilities 在 HIMC=0 时返回 CommittedTextOnly，而 OnCompositionMessage 在同一情形返回 false、把结果交给默认链。之后默认 IME_CHAR 被桥吞掉，唯一提交来源也消失。安装子类化不等于当前实际拥有完整输入通道。保留明确的通道归属/本条处理判定；HIMC不可用等声明降级路径须可继续普通已提交输入。独立审计新增 HIMC=0 的能力与字符放行检查，及原Result工厂方法组兼容检查，后续统计以新运行输出为准。

## 第二轮返修独立复测（2026-10-19）

Codex亲自运行独立审计：托管检查14/14通过；扩展到真实不可见Win32 STATIC窗口后19/19通过，退出码0。记录 `artifacts/ime-work/ti07-codex-native-final.txt`。真实HWND检查覆盖：安装后EnsureContext异常时恢复原过程、失败诊断；生产WndProc结果消息不进前任默认链且只写一次；Trace抛异常仍能让普通WM_CHAR到达前任过程；Dispose恢复前任函数指针。这是Windows消息路径与故障注入验证，仍不等于真实微软拼音端到端验收。

R1/R3～R6/R8～R12上述已覆盖行为通过当前独立检查；R2真实取消/候选消失、R7真实移动缩放/滚动跟随，以及真实微软拼音全部场景仍待验收，不提前放行提交。验证工具还需保证真正运行中的总超时（不能只在UiApplication.Run返回后Join）、明确的中文/英文输入模式和准确提交文本、焦点/失活/隐藏/滚动/断点场景，才能据此作完成判断。

## R13 [P1] 桥接受隐式预编辑，却没有建立原生归属和取消状态

扩大独立检查后，新增三项一通过两失败，托管总计15通过、2失败（`artifacts/ime-work/ti07-codex-implicit-audit.txt`）。序列：没有START，直接COMPOSITION(comp="old") → 预编辑成功；SetFocus(null)却没有NotifyCancel调用；重新聚焦同一TextField建立新epoch后，旧COMPOSITION(result="OLD")写入新会话。翻译器显式支持无START的隐式段，但桥只在START设置_nativeComposing/_compositionBound/_compositionOwnerClient，导致后续失焦取消与来源隔离失效。桥接受隐式段时必须同步建立所属原生段（并在提交/取消/END时按所属代际维护），而不是仅让核心进入组合。修复后保留合法无START结果的支持；覆盖隐式预编辑、隐式结果+下一段、旧结果不能跨焦点epoch。

R13返修后Codex亲自运行托管+真实隐藏HWND检查共22/22通过；原生结果链与上下文失败恢复仍通过。需把隐式段取消/跨焦点回归同步纳入正式测试工程。

## R14 [P2] 真实验收工具的Unicode写入计数错误，Query仍无界等待

HostCheck把 `中😀` 的预期setter增量写成3。生产适配器按Unicode标量回调：`中`一次、完整代理对组成的😀一次，应为两次原子编辑，不能让孤立代理项单独写入。Codex用真实OpenTkInputAdapter喂两个TextInputEventArgs标量验证，最终文本`中😀`、Writes=2，独立检查18/18通过（另有5项原生窗口检查此前通过）；输出 `artifacts/ime-work/ti07-codex-unicode-audit.txt`。修工具与README判据并补正式回归。

Query仍使用无超时的 `tcs.Task.GetAwaiter().GetResult()`。宿主关闭/看门狗触发后若投递动作无法执行，worker可能永久等待，最终仅靠主线程15秒后放弃并退出。应给Query明确取消/有界等待，传播看门狗和hostExited，避免继续场景或写已关闭日志；OperationCanceledException应中断场景循环，不能被Scenario的catch吞掉后继续发键。独立验证总超时和无前台退出，不假报通过。

## 真实微软拼音首轮结果与R15（2026-10-19）

Codex启动生产宿主、用户点击窗口取得前台。`artifacts/ime-work/ti07-codex-host-nihao-retry` 记录：实际UI线程MTA、DPI144，微软拼音CLSID/GUID与启用/活动标志均核实；真实预编辑`ni'hao`、系统候选首项“你好”、空格后精确提交“你好”且setter+1。Enter原拼音`nihao`也只提交一次、无换行；原过程恢复true、回调异常0。截图已亲自查看，没有第二套系统预编辑。此路线在实际生产MTA条件下可用，不需要改STA。

完整矩阵 `artifacts/ime-work/ti07-codex-host-matrix` 退出1。中文选词/Esc/Enter/分段/数字/Unicode/焦点切换取得真实正向证据；ascii、anchor-follow、external-update-cancel、undo-redo失败。部分其他“pass”判据不足，不能接受为完成：

- 英文模式切换在脚本线程调用IMM，日志set_ok=false且readback仍0x0401；`abc`被IME预编辑吸收（setter+0）。先改为自有UI线程操作并回读；若微软拼音确实忽略IMM转换位，可只在自有前台窗口用已确认的Shift中英文切换并回读、恢复原模式，不能据此宣称框架普通输入失败。
- container-scroll只检查offset从0到144，publish_delta=0、anchor_after=null，截图整个输入框已被滚出；没有活动预编辑，不能验证组合候选跟随。
- breakpoint-reflow只检查布局mode变化，publish_delta=0；随后仍使用旧的fieldNode/旧焦点和滚动状态，anchor-follow、外部绑定与Undo前置条件不成立。每场景必须重新查询当前已挂载字段，滚动回可见区、建立当前活动会话；断点导致卸载时应验证旧会话取消/撤销，再重建当前会话，不能继续使用旧节点。
- minimize-restore在before/while/after锚点全部null时也判pass；close-window的composing_at_close=false仍判pass。必须要求有效前置状态及真实组合，并验证结束/恢复、候选几何与释放。没有覆盖的场景不能因一个WM_CLOSE成功便放行。
- `--scenario nihao-commit`仍额外跑了Enter，所有场景都应遵守过滤器。单场景Undo应自己建立可撤销历史。

以上优先返修验证脚本的状态隔离与判据，再独立重跑。不能把日志中的“pass”逐行直接转成整个TI-07已完成；真实候选移动/滚动/裁剪、隐藏和关闭仍有未验证项。

## 第二轮真实矩阵复核（2026-10-19）及R16

`artifacts/ime-work/ti07-codex-host-matrix-round2` 退出1，16项中15个脚本判据通过，ascii-plain失败。英文IMM设置返回true但回读仍0x0401，Shift后备也未切换，abc被预编辑吸收，不能据此判断普通输入框故障。其余中文、焦点、断点、最小化、窗口隐藏、外改、撤销重做、组合中关闭取得正向证据；最终原过程恢复true，callback/restore errors均0。

R16 [P1] 锚点撤销API成功不代表真实候选撤销。Codex亲自查看`s7c-scrolled-out.png`：输入框完全裁出后，微软拼音候选仍浮在旧位置；`s7b-small-scroll.png`显示小幅滚动后的候选。当前PublishGeometry(Unavailable)只SetCandidateExclude整个客户区；实际输入法未因此移除旧浮窗。不能把revoke计数增加当可见候选撤销验收通过。请分析IMM约束，提供不破坏TI-06“仅裁出保留组合”的最小策略与可验证判据；如果系统UI不可控，应明确路线限制与需要的语义选择，不能偷偷取消组合或虚报通过。

工具还需修正：总超时默认60s短于前台等待180s；窗口IsVisible=false导致失活取消，与视图Opacity隐藏保留组合的语义不同，README/注释须准确区分。以上均未关闭，继续禁止提交推送。

补充回归：Codex运行 `dotnet run --project tests/CSharp.UI.ImeProbe.Tests --no-restore`，86项通过、失败/跳过均0；有既存NuGet漏洞元数据源不可达警告，不影响测试执行。输出保存于同名前缀证据目录`imeprobe-regression.txt`。

R16路线核对：Microsoft的CANDIDATEFORM文档将CFS_EXCLUDE定义为定位排除区，不是关闭候选。ImmNotifyIME另有NI_CLOSECANDIDATE/NI_OPENCANDIDATE，文档说关闭/打开成功时会发IMN通知，仍需真实微软拼音验证其支持及预编辑保留。来源：https://learn.microsoft.com/windows/win32/api/imm/nf-imm-immnotifyime 与 https://learn.microsoft.com/windows/win32/api/imm/ns-imm-candidateform 。不可据现有排除区调用返回成功宣称系统浮窗已撤销。

截图补充：对比`s7a-composing.png`与`s7b-small-scroll.png`，输入框上移约10物理像素，候选列表的顶部却几乎保持不变。小幅滚动的锚点重发计数也不能证明可见候选跟随。R16同时覆盖动态跟随与完全裁出残留，不能只解决关闭后标整体通过。

工具round3复审：Devin添加手动英文模式、默认总超时300s、窗口隐藏说明修正，Debug/Release工具构建通过。Codex重跑独立检查25/25通过（包括真实隐藏HWND和工具取消），证据`native-audit-round3.txt`。新增手动确认路径仍发现超时返回false后无条件SendAsciiText、最终pass未要求确认；已要求后续返修只在自有前台完整按下/松开确认后发键。候选关闭/重开诊断正在准备，未批准生产语义变更。Devin“IMM仅剩取消可消除候选”的分析尚未经过NI_CLOSECANDIDATE实测，不能作为路线定论。

手动确认守卫返修已复审：只有自有前台完整F10按下/松开才确认，未确认直接返回且不发abc；Codex运行工具--selftest 8/8通过，证据`tool-confirmation-selftest.txt`。Devin工具Debug/Release构建通过，新增仅显式运行的candidate-visibility-probe；该诊断pass只代表实验流程执行，不代表真实候选关闭成功，仍须截图核对。接下来恢复用户协助的真实窗口实验，候选与英文尚未标通过。

## R16关闭候选实验（2026-10-19，Codex真实复核）

用户点击取得前台后运行candidate-visibility-probe，生产MTA/DPI144/当前微软拼音，NI_CLOSECANDIDATE与NI_OPENCANDIDATE均返回false。Codex亲自查看s9a/s9b/s9c，候选框始终可见且位置相同。预编辑`ni'hao`和原生GCS_COMPSTR全程保持，绑定写入0，原过程恢复true、异常0。流程诊断显示pass不表示候选关闭成功；最终host_exit_code=2是诊断场景未累计any_composition_observed的工具问题（日志中实际组合true），不作为产品通过证据。完整证据保存candidate-close-probe子目录。

下一步批准仅工具层的隐藏Win32 caret定位对照：自有前台、UI线程CreateCaret/SetCaretPos，绝不ShowCaret，退出/失活DestroyCaret；比较真实滚动前后候选截图。暂不接生产，不改变裁出保留组合契约。同时独立手动英文路径待真实补验。

## 英文普通输入人工补验（2026-10-19）

用户在自有窗口切到英文后完成F10确认，日志ascii-plain-result：landed_exact=true，text_delta=abc，writes_delta=3，confirm_observed=true，ime_took_keys=false。普通英文输入行为通过；最终helloabc包含用户确认前手动输入的hello，判据基于确认后的基线增量，未把用户输入计入脚本的3次事务。子类恢复true、异常0。证据manual-english目录。

最终host_exit_code仍2：工具把any_composition_observed=false当成所有过滤场景的环境缺失，英文单项本应没有组合，必须按场景需求判断。需要纯逻辑修正及自检，无需仅为退出码重做用户已完成的实际英文动作；保留原始exit2证据，不重写原输出。R16动态候选定位/裁出残留尚未通过。

隐藏caret诊断首稿未放行GUI：创建前GUIThreadInfo失败被当作“没有caret”，且只查GetFocus未同时查前台；DestroyCaret未再检查当前归属，可能在对象替换后销毁别人的队列caret。要求返修：GUI查询成功+自有前台和焦点+无线程caret才创建；各阶段失去前台即停止；仅曾创建且当前hwndCaret仍属自有窗口才销毁。新增纯逻辑守卫自检，与英文/Unicode退出码修正一并完成后再真实运行。

工具收尾复审：退出码不再以“无中文组合”否决英文/Unicode成功场景；实际执行场景数为0、场景失败、宿主异常仍失败。隐藏caret创建要求GUI查询成功+自有焦点和前台+无已有caret，各阶段前台丢失停止，释放前复核当前caret所属HWND。Devin工具Debug/Release构建通过，Codex独立--selftest 16/16通过，证据tool-final-selftest.txt。没有仅为统计码再要求用户重复英文测试；原始exit2证据继续保留。下一步运行native-caret-probe，只作定位来源对照，不直接宣称R16已修复。

## 隐藏系统caret对照结果（2026-10-19）

Codex启动native-caret-probe，本次普通Focus成功取得自有前台，生产MTA/DPI144/微软拼音。CreateCaret/SetCaretPos成功，原锚点与原生caret(83,56)，小滚动后桥锚点(83,46)，仅桥更新时原生caret保持(83,56)，同步后原生caret确实(83,46)。Codex亲自对比四张截图：候选顶部始终基本相同，未随滚动移动；创建隐藏caret及同步位置都未解决R16。GCS_COMPSTR与框架预编辑ni'hao保留、writes0，DestroyCaret成功、过程恢复true、异常0，host/process exit0（诊断流程成功，不代表R16通过）。证据native-caret-probe目录。

下一步仅工具对照：在组合开始前创建隐藏caret、同步后发布真实位置变化WinEvent；核对现有IMR_QUERYCHARPOSITION缺口及真实请求，不直接扩大TSF文本存储实现。

## R17 [P1] 真实微软拼音请求字符几何，宿主没有应答（2026-10-19）

新诊断trace在真实生产宿主捕获大量WM_IME_REQUEST wParam=0x0006（IMR_QUERYCHARPOSITION），包括初始组合与小滚动之后。现OnImeRequest只响应0x0001/0x0002，未响应0x0006；默认处理无法得知OpenGL自绘预编辑排版。NotifyWinEvent已发、原生caret成功更新，候选截图仍留旧Y，不能靠它们替代正确应答。证据artifacts/ime-work/ti07-codex-caret-notify-probe（待持久化）。

官方要求自绘组合串应用填充IMECHARPOSITION：dwCharPos为组合串UTF16偏移，pt为该字符屏幕坐标、cLineHeight为物理行高、rcDocument为可编辑区域屏幕矩形。来源https://learn.microsoft.com/windows/win32/intl/imr-querycharposition 与 https://learn.microsoft.com/windows/win32/api/imm/ns-imm-imecharposition 。必须使用已有最终布局的精确字符位置，不能所有索引都返回当前caret，不在原生回调触发Measure/Arrange，也不把客户区当屏幕坐标。

Codex批准最小internal只读几何查询接缝与缓存、Windows正确结构应答及正式回归；公开ITextInputClient/既有API保持兼容。查询无效/过期/裁出须明确失败，不返回旧几何；后续真实滚动验证后再定R16可见候选限制。

Codex补充独立几何检查（真实不可见STATIC HWND，屏幕负X，正式TextField运行时）：偏移0/2/4应答、不同字符不同坐标、负屏幕坐标及有效文档矩形三项失败；越界/结构过小/撤销/失焦拒绝三项通过。合并既有检查共28通过、3失败，原生过程及工具取消原25项仍通过。返修前基线query-geometry-baseline.txt及独立源码query-geometry-audit.cs已保存。后续用同一独立程序复测，不用实现镜像测试代替此回归。

## R17首轮独立复测与追加返修（2026-10-19）

首版实现已可正确应答偏移0/2/4、不同字符不同位置、负屏幕坐标/有效文档矩形，原25项来源/原生/取消检查仍通过。扩展独立检查共30通过、4失败，证据query-geometry-round1.txt：

- [P1] 布局或字体被Invalidate后、下一次安全阶段重建前，客户端只检查epoch/文档版本/预编辑文本，仍提供旧排版几何；必须额外检查布局有效性及几何关联状态，不能回调重排版。新增InvalidateLayout和SetEnvironment(Font)立即查询的正式回归。
- [P1] OnImeRequest在识别0x0006前，遇到无anchor/空lParam便return null，导致原生WndProc交给默认过程。自绘字符查询没有可用几何时必须返回明确0并结束本消息，不能让默认链补出陈旧/窗口原点坐标。识别QUERYCHARPOSITION优先；在其内处理null/无有效锚点/失焦等。其余未处理IME请求仍正常默认链。独立撤销及失焦两项检查因此失败。

以上返修前不批准提交。独立审核源码已扩展到34项，不能只看旧28/3或先前25项结果。下一次重复构建audit以复制最新Debug DLL，再运行--query-geometry --native --tool。

### R17 真实微软拼音滚动复验（2026-10-19，首轮查询实现）

用户手动激活生产 ImeHostCheck 窗口后，container-scroll 正常完成，host exit0、callback_exceptions0、restore_failures0、原窗口过程恢复。脚本 pass 仅代表锚点发布/撤销和组合保持，不代表候选视觉通过。

Codex逐张查看 s7a/b/c：小幅滚动时预编辑文本上移约10物理像素，候选窗仍在原位；输入框完全裁出后，候选窗仍可见。因此 R16 仍未通过。与此前不同，本轮 IMR_QUERYCHARPOSITION 确实得到成功应答：seq147 offset6=(398,449)，seq156/158/162/165～171 同offset=(398,439)，即真实原生查询已读到更新后的字符屏幕位置。补齐请求协议有必要，但该实现单独不足以解决当前系统候选窗动态位置。

关键原始日志和三张截图保存在 [query-scroll-round1](2026-10-19-ti07-windows-ime-review-evidence/query-scroll-round1/ime-host-check.jsonl)。后续须先完成R17快照失效/显式FALSE返修，再据真实窗口复验决定最小路线修订，禁止仅凭锚点或查询坐标正确宣布候选可见行为通过。当前无提交放行。

### R17 第二轮 Codex 独立复审（2026-10-19）

返修加入布局失效序号、横向滚动及无预编辑caret身份核对；查询分支先识别IMR_QUERYCHARPOSITION，空指针/无锚点/失焦等均明确FALSE，不落入默认链。

Codex独立完整构建Debug与Release均成功，四组正式测试每个配置545+67+53+300=965项，全部通过，零失败/跳过。构建首次沙箱内无诊断失败，外部复跑成功。测试TEMP/TMP仅针对测试进程指向专用workspace目录，不改全局设置。额外ImeProbe86/86、Text67/67已独立复跑通过，保留前轮CLI权限失败记录。独立真实隐藏HWND审计扩展到38项，全部通过，包括viewport/DPI失效、空缓冲区明确FALSE与未知请求默认转发。证据见r17-final-regression、temp-recheck、query-geometry-round2.txt及query-geometry-audit-final.cs。

随后使用最新Debug生产DLL运行真实微软拼音container-scroll第二轮，原过程恢复、回调/恢复错误均0，host exit0。Codex逐张查s7a/b/c：候选窗依旧没有随输入框上移，并在输入框完全裁出时仍可见。证据[query-scroll-round2](2026-10-19-ti07-windows-ime-review-evidence/query-scroll-round2/ime-host-check.jsonl)。R17本轮两类独立缺陷已修复，R16真实可见行为仍未通过，TI07整体不放行、不提交。Devin正在只做最小路线分析，未经Codex审查不实现TSF或扩大范围。

补充：Codex未接受“IntPtr.Zero/null等价于前任过程计数”的替代验证，额外安装真实前任WndProc观察者，发送WM_IME_REQUEST：空缓冲、撤销、失焦均明确FALSE且前任调用增量0；未知请求前任调用增量1。独立审计最终42/42通过，见query-geometry-round2-native.txt，源码同步更新。

### R16 路线分析首审（2026-10-19）

Devin分析提案未放行：CANDIDATEFORM官方只列CFS_CANDIDATEPOS或CFS_EXCLUDE，不能声称CFS_FORCE_POSITION按位或为有效候选样式；COMPOSITIONFORM同样为one-of枚举。不能由API TRUE断言契约必然无误，也不能未经验证断言TSF MTA接入可用、GetScreenExt自然隐藏候选，更不能用残留为系统行为直接降低冻结验收要求。依据：https://learn.microsoft.com/en-us/windows/win32/api/imm/ns-imm-candidateform 和 https://learn.microsoft.com/en-us/windows/win32/api/imm/ns-imm-compositionform 。已要求修正分析，只增加显式工具诊断：滚动后真实追加一个组合字母，检查候选是否在文本变化后跳到新位置；不更改生产几何、不发送伪造位置、不启动TSF改造。

补充已执行此前暂缓的native-caret-early-probe：组合开始前先创建自有隐藏caret，再nihao，滚动后SetCaretPos+NotifyWinEvent。Codex查看s11a/s11b，文字上移而候选仍原位；因此“创建得太晚”在该工具时序中未被证实可解决。host exit0、原过程恢复、回调/恢复错误0，绑定写入0；证据native-caret-early-final。该caret在字段焦点建立后、组合前创建，不外推为“早于焦点创建也已排除”。

### R16 E1内容变化对照与官方控制入口（2026-10-19）

scroll-edit-diagnostic真实运行host exit0、原过程恢复、callback/restore异常0。Codex逐张查看s12a/b/c：纯滚7逻辑像素后候选仍原位；继续真实输入a后ni'hao变成ni'hao'a，候选随之上移约10物理像素到当前文本下方。日志anchor由(83,56)→(83,46)→(99,46)，writes_delta0、text_unchanged=true。证据scroll-edit-final。证明该轮内容更新能使候选采用当前几何，但纯滚动未能触发，仍不宣称系统实现原因已经完全查明。

Codex补查微软官方IMC_SETCANDIDATEPOS文档：该WM_IME_CONTROL命令专门用于应用自绘组合字符、使用IME候选窗的场景，返回0表示成功。https://learn.microsoft.com/en-us/windows/win32/intl/imc-setcandidatepos 。因此不接受“IMM完全没有显式控制入口”的提案断言。下一诊断仅通过ImmGetDefaultIMEWnd(ownHWND)与同线程/同进程验证，向该默认IME窗口发送合法位置控制消息，保持组合/绑定语义；不操控其他候选窗HWND、不扩大TSF、不改生产。


### R16 官方候选位置控制实测（2026-10-19）

ime-control-position-probe验证默认IME窗口与自有窗口同PID72088、同TID64572且有效；WM_IME_CONTROL/IMC_SETCANDIDATEPOS派发成功、命令返回0，ImmGetCandidateWindow读回style0x40和(83,46)。ni'hao保持、组合为true、writes_delta0、text不变，hostexit0、原过程恢复、callback/restore错误0。Codex查s13b/c，两图候选同位，没有随滚动更新。证据ime-control-final。故不把该诊断接入生产，也不以调用成功关闭R16。

经上述独立查询/真实内容变化/原生caret/专用控制等对照，现有路线尚无已证实可通过动态跟随与裁出隐藏的实现。Codex批准的下一步仅为TI07内TSF真实文本存储和布局通知的隔离可行性原型，先验证线程/锁/内容所有权与真实候选行为；未批准直接替换生产IMM桥或调整生产线程模型。详见2026-10-19路线修订计划，原验收要求保持。
