# TI-07 P3a 正式宿主输入路由审核

审核日期：2026-10-21

状态：实施中，尚未验收。以下为对当前增量的明确问题，最终交付后须逐项复核；P2已通过的结论不变。

## 当前执行摘要（2026-10-21）

下文保留发现时的源码与失败历史，不代表所有问题仍处于初版状态。当前按小批次逐项关闭，未列为独立通过的项目继续开放。

| 项目 | 当前证据与状态 | 下一验收条件 |
| --- | --- | --- |
| R1：GetForeground ABI | 已改为 out Guid；最终回归时复核 | 正确签名及原生调用保持 |
| R2：真实消费与字符回声 | DSH 已改产品 MSG，报告 A 一次/C 零次及负向对照；原失败证据保留 | Codex 对产品钩子独立复测 |
| R3：初始快照 | 已使用真实快照；非零反向选区/version>1 尚未覆盖 | 构造真实状态并逐项核对 |
| R4/R6/R7/R8：资源、按键参数、服务归属和单写入者 | 已有实现返修，最终稳定路径尚未完整独立验收 | 两字段生命周期、失败清理和回退实测 |
| R9：默认输入与完整负载 | 默认输入审计 DSH 重跑通过，事件负载已实现；事实/普通输入交错仍开放 | 完整交错的文字、写入次数与归属断言 |
| **R10：无后续输入时自动重放** | **Codex 独立复现失败：writes0/pending1；当前唯一主要实现门槛** | 同一审计无需下一次输入即 writes1/pending0 |
| 工具项目引用 | Codex 已还原两个实际工具，退出0 | 使用正常 ProjectReference 构建和运行 |

当前返修任务：`artifacts/ime-work/ti07-production-p3a-auto-replay-repair-task.md`。R10 通过后再处理交错顺序，不以重复完整回归替代定向门槛。

## R1（P1）：新增 ITfKeystrokeMgr.GetForeground ABI 错误

当前 TsfInterop.cs 声明 `int GetForeground([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, out IntPtr ppunk)`，HostRoutingSpike 随后用该签名调用，并把返回值当 COM 指针。这与官方 `[out] CLSID *pclsid` 不一致；它不接受输入 CLSID，也不返回 IUnknown。应保留正确槽位且使用 `GetForeground(out Guid)`。不能以额外参数在x64上恰好未崩溃当 ABI 正确或对所谓返回指针调用 Marshal.Release。

官方依据（Codex已读取完整页）：https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-getforeground 。原生工具应核对真实返回的 CLSID/HRESULT，而非“ptr非null”。已用 GetCurrentThreadId 替换托管线程号的修订方向正确，继续保留。

## R2（P1）：自定义消息循环不能代替实际 GLFW 路径证明

当前 HostRoutingSpike 虽创建真实 OpenTKHost 窗口，PumpFor 却自行 PeekMessage/TranslateMessage/DispatchMessage。它证明的是这段自定义循环可以配合WH_GETMESSAGE，不证明实际产品 GLFW.PollEvents/WaitEvents 的顺序与消费效果。请保留显式 PM_NOREMOVE 检查，同时补使用实际 GLFW pump 驱动自有HWND的消费/未消费/多键排队/拆卸证据；最终使用产品钩子及协调器的正式隐藏入口更须经过真实宿主循环。不能用工具钩子复制品的成功替代产品钩子已接入。不要替换产品消息泵来让探针通过。

## 最终交付仍须覆盖

internal opt-in 正式输入桥、一个HWND一个writer、STA/MTA/failure清理、原生前置按键路由、真实TextField归属/同pump确认→字符→另一输入框及unsafe延后、resize/frame守卫、默认IMM未变、P2所有审计及正式回归。不把纯函数判据/Queue单元测试通过当作这些接线已经完成。新增工具最终应使用正常ProjectReference及传递/原生依赖；restore若受沙箱阻止，请报告给Codex提升执行，避免长期依赖手工复制测试输出与禁用deps文件。

## R3（P1）：初始同步没有使用真实 TextField 快照

当前 TsfHostSession.TryCreate 在已构造 TextInputCompositionSession 后，仍手工 Create(initialText, selection0/0, replacement0/0, epoch1,version1) 发送给 store。聚焦已有内容中间/反向选区的输入框时，原生选区会错误回到0，epoch/version亦不是权威回读。初始同步须使用 compositionSession.Snapshot 中的真实文本、方向选区、版本和epoch，检查ApplyAuthoritativeUpdate实际结果；不能忽略失败后返回Success。补真实TextField非零caret、反向selection、非1版本用例，不用FakeClient固定0掩盖。

## R4（P1）：Focus/Blur 重入与原生资源成对性

当前每次 Focus 都 Push(_context)，Blur 仅 SetFocus(null) 不Pop；重复Focus或Blur→Focus将重复推入已在栈上的同一context。须显式跟踪push状态/幂等，保证正常失活恢复以及SetFocus失败重试不会累计/遗留栈。失败清理当前只要threadMgr非null就Deactivate，即使Activate未成功；须只配对实际成功的Activate，避免动到并未持有的激活引用。独立验证连续Focus两次、Blur→Focus多轮、失败后的可重试及最终清理，并记录真实HRESULT。

## R5（P1）：正式隐藏入口不能手动解除仍活动的 IMM 所有权来模拟接入

当前 TsfHostBridgeCheck 使用 Rectangle 生产根，再另建 HeadlessUiDispatcher/StubTextService/RecordingTextureService 的 UiRuntime/TextField；它不是该生产宿主自己的TextField。工具还直接HostWriterRegistry.Release(hwnd, immOwner)后创建TSF，并在结束后重新占用。释放注册表不等于解除IMM子类化/停止IMM输入，两者可能同时活动；这不能证明单writer或正式桥接线。

请通过内部opt-in让真实生产宿主以TextField根组装TSF服务/原生钩子，在宿主自身runtime聚焦真实客户端，并由产品负责切换/卸载IMM，而非工具私自修改registry。registry只能由真实持有者在生命周期中释放，测试不能绕过冲突来制造成功。单独native session hidden smoke仍可保留为底层测试，但标签、计数和验收结论必须准确区分。默认IMM、初始化失败真实fallback和后续同pump输入顺序须从实际宿主行为证明。

R4 官方补充依据（Codex已读取完整页）：https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-push 。context stack最多两个，Push须配对Pop；重复聚焦不能反复Push占用栈。


### R2 增量（P1）：WH_GETMESSAGE 返回1不消费消息，当前“无回声”断言还把VK当字符

新增的真实GLFW场景仍未证明消费：PumpHookCallback 与原HookCallback均在“消费”分支仅 `return (IntPtr)1`，没有修改lParam指向的MSG。这不是低级键盘hook；WH_GETMESSAGE的返回值不负责阻止后续TranslateMessage。官方明确“GetMessage/PeekMessage随后将消息连同修改返回给调用方”。应在PM_REMOVE/自有HWND/本次确实被TSF消费后，将实际MSG改为WM_NULL等明确无翻译的形式，并正确链式传递；PM_NOREMOVE不处理。

官方原文（Codex已完整读取）：https://learn.microsoft.com/en-us/windows/win32/winmsg/getmsgproc 。不得把return1当作已吞键，也不得靠工具自己的pump跳过消息掩盖产品行为。

此外WM_CHAR的wParam是Unicode字符，不是VK。当前 `_pumpSwallowedChar` 只比较 `wParam==0x43`（VK_C），普通未按Shift的C会生成小写c(0x63)，将被计入“普通字符”。日志 keydown1/char4/swallowedDown1/swallowedChar0 与漏掉小写字符检查相容，不能作为无回声证据。请记录本工具合成消息的实际字符codepoint（不采集用户文本），验证c/C或使用已明确映射的确定性测试字符，并做负向验证：故意取消MSG改写时该无回声检查必须失败。只数counter++不是消费。实际GLFW场景必须证明A对应的字符存在、C对应字符不存在，不能将任意旧/无关字符计数当作A的回声。该修复也须用于最终产品hook，继续保持hook链及无关HWND消息原样。

R2 已独立复现（当前稳定交回后）：Codex复制探针到 artifacts/ime-work/p3a-echo-audit，只修正C/c字符判定并记录本工具合成字符codepoint，保留原消息pump/hook行为。真实GLFW返回 U+0061 两次、U+0063 两次，swallowedDown=1 但 swallowedChar=2，最终exit1/RESULT FAIL count2。见配套evidence/echo-audit-debug.log及Program-echo-audit.cs.txt；构建exit0。返回1确实没有消费C键，原swallowedChar0是错误大小写比较造成的假通过。R2仍开放，不能沿用报告“已满足”结论。普通A也出现两次，最终产品端到端测试须断言一次普通字符只写入一次；不要靠只看char>0掩盖重复。

R2 审计接线说明：p3a-echo-audit 的现有 Program 是当时探针的源码快照，内部仍含旧return1的hook复制品；仅重新构建产品不会自动修复该复制品。它的作用是保留已证实的失败。最终应由Codex把审计接线迁移到真实产品TsfMessageHook，保留真实GLFW、A/a与C/c字符断言及负向验证；DSH不得修改Codex审计或宣称旧复制品重新链接产品即会通过。请在报告说明新产品hook API和实际工具使用路径，Codex随后独立迁移。

## R6（P1）：产品hook需保留完整原生按键参数与委托生命周期

当前新增TsfMessageHook消费回调是 `Func<IntPtr,uint,uint,bool>`，只传hwnd/message/wParam，丢弃MSG.lParam。正式TestKeyDown/KeyDown及KeyUp须获得本条真实lParam（scan code、repeat、extended/transition信息）；不能在桥里填0或另猜。请传递完整不可变消息值或原始wParam/lParam，证明repeat/up参数端到端保留。

同类Uninstall目前无论UnhookWindowsHookEx是否成功，都清空_hook/_installed/_proc。若原生解绑失败，原生仍可能回调该委托；清掉最后托管引用会产生悬空回调。须按明确失败结果保留/安全托管仍被引用的委托和句柄，让所属线程可重试清理；不得返回失败却宣称已完全卸载。保持幂等与所属线程清理语义，补受控失败验证。此项针对当前增量，最终稳定交付时重查。

## R7（P1）：桥必须在 runtime 构造时成为真实服务，而非构造期找尚未产生的客户端

当前TrySwitchToTsfBridge在UiRuntime构造后、Start和真实输入焦点建立前调用，并立即要求ActiveClient非null。这时常规真实宿主还没有活动客户端，会永远回退IMM；更换_inputBridge字段也不会改变已传入UiRuntimeServices.TextInputService的原IMM实例。运行时Service仍从Services.TextInputService读取，后续客户端Start/End/Geometry可能继续发给已Dispose的旧桥。

应在runtime创建前组装稳定的输入服务/桥或内部转发层，由其StartSession时在真实客户端归属下创建TSF会话，AttachRuntime后保持同一个runtime服务实例，失败回退通过该实例有序切换；不能靠先造一个未聚焦runtime再在构造期间找client完成接线。验证Started之前没有focus也可创建宿主，Started内聚焦TextField后真正建立TSF，随后另一个TextField的Start/End/Geometry到达同一正确服务；断言ReferenceEquals或等价实际调用链。默认路径保持不变。

R6 已在消费调用链确认：TsfInputBridge.IsKeyConsumedByTsf 接到丢失lParam的三参数回调后，手工构造 `(1 | ((int)wParam << 16))` 传Test/KeyDown/Up。这把虚拟键码冒充scan code、丢掉keyup/previous/extended/repeat信息，不可进入正式路径。必须一路传真实MSG.lParam，且测试不同scan/repeat/keyup位逐项到达原生调用适配层。

R7 当前TsfInputBridge.StartSession只更换_activeClient，底层readonly_session仍绑定首次client；即使绕过构造期问题，切到第二TextField后的TSF事实仍会对旧client执行/被拒绝。桥的稳定服务身份必须支持每个真实focus epoch重建/撤销实际owner/store/context，不只是改ActiveClient字段。EndSession/取消还须经owner/core action序列而非直接CancelComposition导致session镜像失步。初审要求同pump两输入框端到端测试正是为了发现这种问题。

R4 增量：当前Blur为避免重复Push改成每次Pop(0)，但忽略其HRESULT直接_pushed=false。官方 https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-pop 明确：仅一个primary context时Pop(0)返回E_FAIL，primary只能在文档uninitialize使用TF_POPF_ALL移除，不能每次失活都ALL。应让底层主context保持push直至真正会话卸载，Blur仅撤焦点，Focus恢复不再push；最终卸载用正确ALL并检查HRESULT。失败SetFocus后也不能用失败Pop(0)谎称回滚。保留真实状态/明示失败，连续Focus/Blur验证GetTop及栈深语义。当前单纯try/catch捕不到PreserveSig的负HRESULT，不能以没抛异常当成功。

## R8（P1）：登记表把两个不同TSF实例当“同来源幂等”，且IMM未登记

HostWriterRegistry.TryAcquire同kind允许任意重复调用引用计数。两个不同TsfHostSession对同HWND均能成功Acquire(Tsf)并各自建立store/owner，违背“一窗口一个实际writer”；同kind不等于同owner。应给每个持有者唯一租约身份，只允许同一个租约实例的幂等操作（或直接拒绝第二实例），释放校验真实持有者，不允许仅凭kind释放别人。当前WindowsImeBridge无HostWriterRegistry使用，因此默认IMM实际活动时CurrentOwner可为null，也不能证明TSF/IMM冲突保护。若不用登记IMM而由固定桥生命周期保证，应移除虚假的全局保护声明并在实际工厂禁止第二writer；最终测试须创建两个真实TSF实例/一个实际IMM+TSF，确认拒绝且旧对象仍可正常使用/清理。重入同一实例与两个实例是不同场景。

R2/R5 工具轮次增量：TsfHostBridgeCheck.PollKeyRouting从dispatcher动作内再次Post自身，OpenTkUiDispatcher.ProcessPostedActions是while TryDequeue直到队列为空，因此40次“轮询”实际可能全部发生在同一次dispatcher排空中，尚未回到下一次GLFW.PollEvents。observed0/stillQueued不能据此推断GLFW没有处理自有HWND，更不能在第20次改用显式Peek/Translate/Dispatch绕过。请移除显式诊断pump，通过已证实的宿主loop阶段通知/定时异步下一轮投递等方式让控制权真正返回事件循环（并用pumpiteration trace证明）。禁止同步dispatcher自投递忙循环，禁止递归pump。验证必须保证真实pump确实发生之后执行，不以多Post次数冒充多轮。

R7 验收不能只观察host.InputBridge类型或hookcount：当前runtime.Services.TextInputService仍指向旧IMM、OpenTkInputAdapter._ime也是构造时readonly旧IMM，switch只改host字段；真正StartSession/Geometry/Cancel与keytoken不走新桥。必须用实际服务调用及两TextField/ordinarytext后续组合证明。这是静态确定的断链，不因合成C键被产品hook吞掉而关闭。

R2 新产品钩子修订后，原p3a-echo-audit内部旧复制品当然仍失败。请报告API，Codex独立迁移到产品hook并使用正确keyup1/transition（旧snapshot工具key-up错误不修改已保存原始失败证据）。“C无回声”与“runtime实际桥服务”是两个不同门槛。

R6 失败路径增量：仅在Uninstall失败时保留对象字段还不够，如果Dispose已置disposed且上层router随后丢弃bridge/hook实例，_proc字段仍会随对象被GC。请明确失败期间的强根持有者（例如受控待清理注册表/lease），让原生callback引用的托管委托在真正Unhook成功前始终有GC根，且owner线程可重试。Dispose不能先永久标记disposed然后后续Dispose直接return；callback在退休状态应只链式传递不再访问已释放session。受控Unhook失败测试需覆盖GC/重试/迟到callback，而非只检查字段非null。

## R9（P1）：新顺序闸门丢失实际输入内容，且默认IMM路径会停止交付

当前OpenTkInputAdapter.TryEnterOrderedDelivery在不能Drain时只Defer(kind,seq,0)，没有保存字符、KeyEvent、PointerEvent、focus值、位置、修饰键、token或可执行动作，故根本无法重放实际输入。源码也无调用TryDequeue真正重放的路径。注释声称FIFO不等于队列有输入负载。更严重：router.DrainSafePhase在没有TSF桥时返回false，普通默认IMM/未聚焦/TSF失败fallback下每个输入都会进入这个丢弃队列。默认路径必须直接安全通行，不能因没有TSF而全拦。

请改为保存完整不可变输入/执行动作及归属/事件顺序barrier的协调方案，在安全阶段真正FIFO交付，且不先drain未来所有原生事实越过较早普通输入。不要仅补text字段遗漏focus/wheel/key等。HandleFocusChanged目前调用NotifyWindowActivation之前仍没闸门，HandleWheel也直接Dispatch；所有触碰runtime的入口都须纳入一致顺序。hostWorking不能固定false，重入在真实apply期间发生也不能绕过。

必须真实证明默认IMM能打字/点击、无focus能点输入框、MTA失败fallback仍可输入、TSFunsafe时字符/点击不丢、随后安全阶段按原始顺序到达正确输入框，且队列回空。不要只断言DeferredCount增加或调用纯协调器的Dequeue。

R8/R7 增量：WindowsImeBridge当前构造期TryAcquire失败仍继续安装/工作（注释明确“失败不阻断IMM”），这直接绕过冲突保护；_writerLease在Dispose中亦无Release调用，导致占用泄漏。应只在真正安装成功后持有活动租约，拒绝冲突时不安装/不写，安装失败/正常撤销成对释放。headless/fake HWND既有测试不能泄漏全局登记。真实卸载失败仍须如实保留实际owner。

WindowsTextInputRouter当前先TryCreateTSF，成功后才RetireImm；IMM一旦正确登记，这一顺序必定WriterConflict，永远无法接管。先安全撤销实际IMM，再建立TSF；失败后用工厂创建一个新的可用IMM实例，而不是返回已Dispose的readonly旧_immSubBridge。恢复后真实服务需收到当前client.StartSession且可处理组合/几何，能力及reason与实际一致。TSF从未建立、成功后下一client建立失败、MTA失败等分支均验证一个实际writer和可用fallback。

R3/R9 工具覆盖不能降级：当前所谓“反向非零选区＋version>1”块只读初始sel0/0/version1并比对；这不能证明原先hardcode缺陷不会回归。可以通过真实键盘/Shift选择或单元真实TextField接缝在建立新session前造出非零反向选区和版本>1，再比对初始store的有向端点/实际版本；不应仅删除version断言。

当前RunSamePumpOrdering只PostKey(A/C)，没有TSF确认事实、第二输入框点击/焦点迁移、后续字符、unsafe锁。该函数名称/注释不是覆盖证据；A1/C0仍只是消费路径测试。按计划真实模拟交错的完整行为断言不可删去。两个TextField根存在但B从未获得焦点/输入，也不能算clientA→B验收。

R9 默认路径已独立复现失败：Codex以正常ProjectReference创建 artifacts/ime-work/p3a-default-input-audit，真实STA隐藏UiApplication.Run、UseTsfInputBridge=false、真实TextField聚焦、宿主自己的OpenTkInputAdapter.HandleTextInput(x)。预期写入1且队列0，实际writes0/hasX=false/deferred1/delivered0，exit1。证据default-input-{restore,build,debug}.log及Program-default-input.cs.txt。原530自有测试通过未覆盖此实际默认宿主路径，P3绝不可验收。

环境复核：Codex正常权限对新ProjectReference探针与该审计restore均exit0（project-reference-restore.log/default-input-restore.log），无需DLL复制。DSH报告的artifacts/tmp-prcheck/T.csproj已被它清理，不存在；第一次提升尝试因此MSB1009，不是NuGet错误。后续请把两个工具改正常ProjectReference并保留，若DSH自身仍restore受限，Codex可在其停止修改后直接restore实际工具。

R9 重放增量待最终验证：当前TryEnterOrderedDelivery先router.DrainSafePhase（全部facts），然后才ReplayDeferred；ReplayDeferred每项前又全Drain。这仍把较晚捕获的factB提前到更早延后charX前，注释“顺序屏障”不能改变实际调用。需要捕获时的来源边界/统一协调队列，按事实与普通输入原始顺序消费。另ReplayDeferred仅在下一条输入到来时触发，若锁内捕获x后安全返回且用户不再输入，宿主loop没有实际重放入口会一直滞留；必须在安全返回后的唤醒/loop主动重放到队空。

完整重放行为还须与立即交付一致：延后Escape/关闭快捷键仍需匹配原处理语义；Focus失活除了NotifyWindowActivation还需取消指针捕获、清理_pressedButton/modifiers/clickcount；不能通过只重放半个handler留下状态。退出/清理等副作用应同一交付函数处理，以避免立即/延后两套switch逐渐分歧。实际事件的KeyConsumed/所属epoch/token必须冻住且失效处理明确，不能后到事件复用上次hook缓存token。

R9 自有新测试核对：不安全文本用例靠下一条r触发，不能证明无后续输入的自动重放；事实A字符X事实B点击B字符Y保持字面顺序只对coordinator塞X/pointer/Y再手动Dequeue，没有factA/factB、真实adapter/TextField或clickB，仍不满足该命名声称的范围。应准确命名基础FIFO用例，同时补真正交错事务的端到端检查，不能用纯Queue测试关闭R9。


## R10（P1）：安全恢复后没有后续输入，已排队字符永久滞留

Codex在DSH交回稳定源码后独立执行 artifacts/ime-work/p3a-replay-audit。真实STA隐藏UiApplication/TextField/产品Tsf桥，受控进入实际产品coordinator的inside-apply守卫，adapter捕获x后退出守卫，等待150ms再通过dispatcher观察，期间无新用户输入。实际writes0/hasXFalse/pending1/replayed0，退出1。预期writes1、hasXTrue、pending0。此为合成重入状态，不冒充真实微软拼音。

证据 replay-build.log/replay-debug.log；审计程序位于artifacts/ime-work/p3a-replay-audit/Program.cs。当前ReplayDeferred只被下一条输入调用，没有宿主安全阶段主动重放入口。须让所属UI线程安全恢复后主动按序交付，不能靠下一次字符触发，不递归pump、不忙等。立即和延后事件必须共用交付语义，避免重放忽略快捷键退出或失活清理。

Codex同时已正常还原HostRoutingSpike、TsfHostBridgeCheck两个实际工具的ProjectReference，均退出0；无需继续手工复制依赖。

R10 初步调度复核：每轮HasDeferredInput都RequestWakeup(MinimumWaitSeconds=0.001)仍是持续1ms周期轮询，只是从忙循环变成计时轮询，不满足本轮“无周期忙轮询”条件。输入在当前调用栈内排队时可请求一次owner线程唤醒，宿主下一次安全边界交付；若仍不安全则由实际退出apply/request/notification/host-work的边界安排一次重试，或明确fail-closed原因，不能每轮无上限重设1ms定时器。必须有计数/实际循环证据证明持续不安全时不自旋、不持续重绘，恢复后又能立即获得安全机会。

R10 统一交付复核：新增DeliverCaptured不等于立即路径已共用它；当前HandleText/Key仍在闸门返回true后各自直接Dispatch，TryEnterOrderedDelivery也未检查_replaying。请在最终交回前确认无两套行为、重放setter重入新输入只能队列化。独立无后续输入审计通过仅关闭滞留复现，不自动关闭这些约束。

### R10 独立复测与剩余阻塞（稳定交回后）

Codex重建当前源码后运行replay和default审计均exit0：无后续输入writes1/hasXTrue/pending0/replayed1，默认IMM writes1/hasXTrue/deferred0。这关闭“必须下一次输入才重放”的原始滞留复现，不关闭P3。

独立新增 artifacts/ime-work/p3a-reentrant-replay-audit：在被重放x的真实Binding setter内调用产品adapter捕获y，预期y排队等setter返回、两次顺序写入且无setter嵌套。实际writes2/hasYTrue/reenteredTrue，exit1；说明TryEnterOrderedDelivery未检查_replaying，所谓重入测试没有覆盖真实setter重入。证据reentrant-replay-{build,debug}.log/Program-reentrant-replay.cs.txt。原自有测试名“重放期间新输入只入队”实际只强制unsafe，没有真实重放回调重入。

当前宿主仍每轮队列不空时RequestWakeup(1ms)，需要按上文边界通知要求修正；每次等待1ms不构成总量有界。立即handler仍直接Dispatch，并未与DeliverCaptured共用。以上为R10剩余修订门槛。

R10 当前增量待稳定复核：宿主订阅的是router.Coordinator.SafePhaseExited，但实际TsfInputBridge.DrainSafePhase使用自己另一Coordinator。若未转发，实际TSF守卫退出并不会通知宿主；不能用RaiseSafePhaseExitedForTesting代替真实调用证明接线。Codex已准备p3a-safe-signal-audit，检查退出实际tsf.Coordinator时WakeUpCount变化。

当前Submit直接交付新action时没有包住_delivering；只有DeliverPending的队列重放有守卫。Codex已准备p3a-immediate-reentrancy-audit，与重放测试同一setter内注入y，但x为立即交付，预期一样禁止嵌套。两路径共用函数不等于都持有交付期守卫。

当前DeliverPending while一直处理setter追加项没有预算上限；若setter每次写入追加一个新事件，队列从不超过容量但循环可无限占UI线程。每轮必须有明确预算并把剩余工作交回正常loop调度，不能靠队列容量声称有界。自有新回归的回调value.Contains(x)会在后续xy/xyy中持续满足，应有一次性注入标记（如Codex审计injected），或在有意测试无穷来源时断言单轮预算/可退出。请分别验证正常一次重入与持续追加输入的公平性。

R10 测试挂起已确认：本轮DSH执行的 OrderedInputDeliveryTests 进程PID68152从15:44:07运行至约15:50，CPU累计338秒，无返回。Codex核实该进程命令精确为本项目定向测试后仅停止该测试进程，ACP未停止。请先检查新setter重入用例缺少一次性injected标记，以及DeliverPending无单轮预算的组合；不能重复无超时运行。保留真正持续追加来源测试，但限定执行预算并断言控制权返回。


R10 重复挂起：同一17项定向测试再次超过60秒且CPU63秒，Codex仅停止该第二次测试PID63132。请不要再原样重跑。真正setter测试回调每个含x的新值都再次注入y；先加injected一次性标记，再单独跑该用例，持续源用例需有限预算。每次真实测试进程加有限timeout。

