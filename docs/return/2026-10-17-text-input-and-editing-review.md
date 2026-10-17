# 文本输入与编辑基础审核记录

审核日期：2026-10-17

设计依据：[文本输入与编辑计划](../plan/2026-10-17-text-input-and-editing-plan.md)。

当前阶段：TI-01实施期间预审；尚未放行，不代表实现已完成。

## TI-01 实施期检查

### [P1] 截图探针必须限定自有测试窗口与候选区域

当前新增 `tools/CSharp.UI.ImeProbe/ScreenCapture.cs` 提供 `CaptureDesktop`，默认抓取整个虚拟桌面；这会把无关应用画面带进证据，与本任务只操作专用测试窗口的范围不符。正式运行前请移除全桌面截图路径，使用自己的窗口区域及已确认归属的系统候选窗口区域。候选弹窗越出客户区时可以单独截图该候选窗口，不保存整个屏幕。不能以“本机屏幕内容”替代窗口范围约束。

### [P2] 事件日志的线程和序号必须能够用于核对真实消息顺序

当前 `ProbeLog.NativeThreadId` 初始化为 `Thread.CurrentThread.ManagedThreadId`，名字与数值含义不符；记录中的 `tid` 也只是托管线程号。请分别记录真实Win32线程ID和托管线程ID，并在最终消息线程核对中使用正确字段。另 `seq` 在日志写入锁之前递增，多线程记录可能使文件顺序与seq乱序；请固定序号分配和写入的顺序，或明示并验证排序恢复规则。TI-01要据此证明消息/回调次序，不能留下不可判读的时间线。

## 已核对基础

基线Debug构建通过，四套完整测试300+67+30+242=639全部通过；现有12条xUnit分析器警告保留。本轮目前未修改生产框架。Codex独立核对缓存GLFW分发包为3.4.0.44，Windows DLL版本串为 `3.4.0 Win32 WGL Null EGL OSMesa VisualC DLL`，SHA256为 `3DCBEAE25F5A38935F00056E14B4A64815A81DD2F14E5A10701BE13CA6A56BC3`。仍需原型记录实际加载模块/运行时版本与对应源码的匹配边界，不能仅凭上游3.4源码即判二进制完全等同。

DSH自测后交Codex独立审核；上述事项处理并核验前不得提交或推送，也不得开始TI-02。

### [P1] 结果读取/计数必须受本条消息GCS_RESULTSTR标志约束

当前 `ImeProbeWindow.OnCompositionUpdate` 每条WM_IME_COMPOSITION均查询GCS_RESULTSTR，只要读到非空就增加commitCount。lParam标志才说明本条消息更新了哪些数据；查询到的可用结果不能自动视为本条新结果，否则探针自身可能把旧结果/分段状态重复计数。请将提交观察/计数限定到GCS_RESULTSTR标志并处理结果+下一段预编辑同消息的顺序，保留原始查询状态（无数据/错误/空串）与实际事件的区别。若为调查故意额外查询其他字段，日志明确只作快照，不参与新结果计数。添加覆盖无result标志但缓存仍有旧结果的确定性测试或可核对序列，不能靠最终字符串相等去重。

### [P2] 原生回调异常路径与卸载成功判据需要闭合

当前OnWndProc的第二个catch在捕获CallWindowProc异常后直接写日志，日志失败仍可能穿回原生回调。UninstallSubclass也未检查SetWindowProc返回值便记录uninstall-ok并清除安装标志。请保证最外层异常边界不依赖日志成功，并以实际恢复返回值/当前过程验证卸载成功；恢复失败时不能释放仍被原生引用的委托或虚报资源已清理。异常注入需包含日志写入失败和恢复失败边界，而不只是HandleMessage主动抛错。

### [P1] 场景的PASS必须匹配实际断言，不能只表示“有事件”

当前RunImeCommit只要求结果数增加（>0），却备注“once”；RunImeSegmented只要求至少有一个新结果；RunImeCancel的PASS不使用noResult/window_alive；RunImeFocus只要compositionStarted便PASS，即使取消失败；RunImeResize只数请求锚点次数，不验证系统成功返回/实际候选位置。因此汇总PASS不足以证明TI-01要求。请分别使用明确断言（单次/分段结果、无提交取消、存活及结束状态等）或标为OBSERVED/待独立验收，不可把API调用次数当候选窗口定位通过。before快照应在Prepare清理之后，避免清理前次组合污染本场景计数。消息链原样观察有价值，但必须与真实消费/去重策略是否已验证分开报告。

另PumpOnce无条件吞掉GLFW.PollEvents/RenderFrame所有异常会掩盖探针失败。请记录实际异常，只有明确处于已请求销毁且预期的情况可忽略；原型显示/候选验收不能在渲染失败时仍判通过。

### TI-01 原生复测诊断：原型COM单元未满足激活条件，不能据此否定IMM可行性

Codex独立读取 `artifacts/ime-work/probe-mspinyin/events.jsonl`：CoInitializeEx(STA)返回0x80010106（RPC_E_CHANGED_MODE），随后ActivateProfile返回0x80004005，组合更新0，仅有nihao空格字符流。当前Program.Main未声明STAThread。请先在探针创建窗口/触发COM前建立可核对的正确apartment，并记录实际线程与激活成功/当前active profile，再判断IMM或TSF路线。此轮激活未成功，不能归因于“微软拼音不提供预编辑”。若需要STA，决策必须说明与现有UiApplication.Run/示例入口的兼容策略，不要求未经审核替换生产事件循环。

补充实测证据：`probe-all-1/summary.txt`里ime-resize在composition_updates=0时PASS，ime-close在was_composing=false/started=0时仍写“close while composing”并PASS；latin的key_repeats=0却备注auto-repeat。请把“未开始组合时API/关闭边界可用”的基础检查与“组合中移动/关闭/真实重复”分开，不允许这些PASS顶替未发生的场景。当前结果的UNVERIFIED项是有效诚实记录，须保留并在最终状态中反映。

### Codex 独立STA对照已取得真实组合结果（身份仍待核实）

不修改DSH源码，Codex在 `artifacts/ime-work/sta-review` 用独立STA线程反射调用已编译探针；按仓库GlOffscreenSurface同样的Windows-only方式关闭GLFW主线程检测，capture=off、专用窗口。`codex-sta-launcher.log` 与 `codex-sta-probe/events.jsonl` 证实STA下CoInitializeEx返回S_FALSE（已正确初始化），6次真实WM_IME_COMPOSITION更新（n→ni→ni'h…）后只有1个GCS_RESULTSTR“你好”；链式默认处理随后产生WM_IME_CHAR两次及GLFW字符回调“你”“好”。这支持IMM获取预编辑/结果可行，并证明新结果通道与原字符通道不能同时应用。

关键限制：本次ActivateProfile(mspinyin)仍返回0x80004005，故只能认定当前已启用输入法产生真实组合，不能说已确定是微软拼音。请读取实际激活的TSF profile CLSID/GUID、语言与状态，不把请求的profile参数当作实际身份。只读注册表可见HKLM该微软拼音配置Enable=0（HKCU可能覆盖），仅作环境线索，不自行改全局设置。本次非原型主线程运行只作独立隔离定位；正式最小原型应给出正确STA主入口与未来宿主兼容策略。

独立身份核对已完成：Codex按本机SDK msctf.idl声明，以只读ITfInputProcessorProfileMgr.GetActiveProfile（vtable槽10）在STA探针前后查询。两次均S_OK，lang=0804，CLSID=86598fb9-66a2-463e-b9c2-aeb906d477ad、profile=607fdf85-fcc8-4dbd-a365-41296f980c9c、flags=3，实际为WeType/微信输入法而非微软拼音。记录 `artifacts/ime-work/codex-sta-identity.log`。该输入法确实通过IMM消息提供真实预编辑及“你好”结果；不能据此满足计划的微软拼音强制门槛。请正式原型增加同类真实profile记录，不再把--profile=mspinyin或ImmGetDescription空串当身份依据。

### 独立微软拼音受控测试：身份已确认，但当前驱动仍未取得组合结果

用户要求自行处理已授权请求后，Codex通过执行审批完成受控验证：快照原实际WeType profile与微软拼音flags=0；仅用FORPROCESS|ENABLEPROFILE临时启用已安装微软拼音（不使用FORSESSION、不改默认）。前后GetActiveProfile均确认为微软拼音CLSID/profile、flags=3，原型初始化为STA；运行当前ime-commit驱动后仍无结果，不能宣称通过。finally中DeactivateProfile(FORPROCESS|DISABLEPROFILE)和恢复原活动profile均S_OK，核对微软拼音flags恢复0、原活动WeType恢复，RESTORED=True。日志 `codex-microsoft-pinyin-controlled.log`。

这次已排除输入法身份与COM模式不确定性，尚需检查自动驱动在UI线程发送/休眠不泵消息与微软拼音实际键盘处理是否相容；可对比保持消息循环的逐键真实输入。受控变更已恢复，不把新输入法长期加入用户配置。今后的正式工具若需同类临时测试，必须先快照并finally恢复验证且明确记录，不能静默永久启用。

TI-01接续审核补充（13:04）：CaptureWindowRegion目前使用任意CandidateCaptureMargin扩张自有窗口，未确认扩张区域属于候选窗口；这仍不满足P1限定范围。当前锚点在窗口内部时请优先margin=0，仅采集自己的窗口矩形，并确认前台/可见；需要窗口外候选证据时再按真实候选归属与边界单独采集，不以任意背景边距代替。旧整屏试验图片留本地不分发即可，正式证据重采。

纯托管/跨平台边界也请准确：ProbeLog已直接调用kernel32 GetCurrentThreadId，因此其单元测试不是平台无关纯CPU测试。可将Win32专用日志检查明确归Windows，或在非Windows使用空native_tid并单独断言；不要在Linux测试加载时P/Invoke kernel32。另当前未赋值的ManagedThreadId属性应删除或正确初始化，避免再次误导字段含义。

接续API返回值检查：现有TsfProfile.ActivateForProcess把HRESULT>=0写succeeded=true，异常catch还返回S_FALSE。微软官方ActivateProfile文档明确S_FALSE表示“language profile is not enabled”，不是激活成功。请只在S_OK且GetActiveProfile身份匹配时确认激活；异常返回实际失败状态，不能把异常变成正值HRESULT。参考：https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile 。此规则与CoInitializeEx的S_FALSE“已初始化”含义不同，不能通用套用同一判断。

### [P1] 新增profile管理把ActivateProfile参数位误用成状态flags位，必须在运行前修正

`TsfProfileManagement.InputProcessorProfile.IsEnabled` 当前用 `(Flags & TfIppmEnableProfile) != 0`（0x1）。这两个枚举不是同一个语义：TF_INPUTPROCESSORPROFILE.Flags中0x1是TF_IPP_FLAG_ACTIVE，0x2才是TF_IPP_FLAG_ENABLED；ActivateProfile的TF_IPPMF_ENABLEPROFILE参数恰为0x1但不能复用。当前读取日志还把0x2标成flags_disabled_profile，含义相反。

后果是“原本已启用但未活动”的profile（flags=2）会被当作禁用，临时scope在恢复时可能错误禁用原本已启用的输入法。请定义独立TF_IPP_FLAG_*常量，修正IsEnabled/日志/快照与恢复验证，补flags=0/1/2/3的确定性单元测试后再运行设置变更路径。Codex独立辅助是按状态位0x2检查启用，可参考但不复制错误枚举。SDK msctf.idl的TF_INPUTPROCESSORPROFILE及TF_IPP_FLAG_*定义可直接核对。

独立正式入口对照（13:16）：Codex直接运行当前编译的imeprobe.dll（无反射包装）`--scenario ime-commit --profile wetype --input-mode pumped --capture off`，在APTTYPE_MAINSTA和实际WeType身份下取得一次标志结果与重复字符通道，ime-commit PASS。日志codex-native-entry-current.log，事件codex-native-entry-current/events.jsonl。与DSH刚跑的--profile current未产生组合不同，显式在获焦后激活当前已启用profile可能是关键差异；先对比这条最小命令，不必立即扩大TSF文本存储范围。若DSH相同命令仍无组合，需比较其执行环境/进程权限与Codex执行上下文，不重复把失败归为平台不支持。

执行上下文排查已增加只读对照：`dotnet exec artifacts/ime-work/sta-review/bin/Debug/net10.0/ImeStaReview.dll --identity-only` 不创建窗口、不改设置。Codex当前结果为WinSta0 / Default，session=5、activeConsole=5、interactive=True，记录codex-execution-context.txt。请DSH在自己的执行工具里运行同一命令并另存输出，比较desktop/station/会话；仅看到ctfmon缺失或同一DLL失败不能直接断言是session-level英文模式。可以再记录自身进程token完整性/受限状态，不读取他人进程输入或认证数据。必须用可复现实测区分执行环境与平台能力。

当前仍有P1未关闭：新增TsfProfileManagement的IsEnabled误用参数位0x1，状态正确启用位为0x2（SDK已核对）。请在任何最终验收和设置变更操作前关闭此项并补四状态测试；不要只重复四套旧框架测试来替代这项修复。

### [P1] 临时profile变更的恢复必须独立于日志和激活返回值

当前TemporaryProfileScope.ActivateAndVerify先调用原生激活，再写日志，之后才设_configurationTouched=true。若激活已改变启用状态但日志抛错，TryCreate的catch只Release(manager)，没有恢复；即使原生调用失败也不能预设绝无部分改动。Dispose中disable后的日志抛错也会跳过恢复原活动profile。请在尝试变更前设置恢复义务、让scope所有异常出口进入finally恢复，恢复过程用不抛出的日志且一个步骤失败仍尝试其余步骤；只在实际回读原状态匹配后报告RESTORED。TryCreate返回null时也不可无条件声称“所有更改已恢复”，必须传达或抛出恢复验证失败状态。

请补注入日志失败/激活失败/恢复一步失败的测试，证明清理仍执行，且profile已启用但不活动（flags=2）不会被错误禁用。此项与前述状态位0x2/S_FALSE处理一起关闭；仅对当前flags=0成功路径测一遍不足以验收可复用设置变更工具。

微软拼音最小驱动补测建议（未验证假设，不作根因结论）：现有InputScript.BuildKeyboardInput对普通虚拟键使用wScan=0，原生WM_KEYDOWN日志常见lParam=0x1（扫描码为0）。GLFW源码会为这种合成键补MapVirtualKey，但输入法在更早阶段处理按键。请增加一次带真实扫描码的SendInput/KEYEVENTF_SCANCODE对照（固定nihao/空格，只在自有前台窗口，逐键维持消息循环，不合成WM_IME），核对原生lParam确实有扫描码，再判断微软拼音路线是否需要TSF文本存储。虚拟键驱动和扫描码驱动结果须分开记录，不能把该假设直接写成已证实原因。若仍无法驱动真实MS输入则保持未验证，决策应列出尚未排除的输入驱动/环境条件。

另一次独立获焦后+pumped复测已确认微软拼音真实CLSID/profile、STA、open_status=true和conversion=0x11，但没有WM_IME组合；原始codex-microsoft-pinyin-controlled.log及codex-sta-probe记录已更新，恢复原状态再次RESTORED=True。不据此单独宣布MS不支持IMM，须结合上述更完整输入条件。

### [P2] 决策文档与README须纠正尚未证实的环境归因及记录错误

当前决策§3.3把ctfmon缺失写成已证实“根因、不是探针缺陷”，并说13:1x同一Codex入口已无法组合。但Codex在13:16通过正式入口+显式--profile wetype+pumped取得真实结果（return已有具体日志），这与该绝对叙述矛盾。请并列列出实际运行上下文/参数/时刻与结果，把ctfmon影响保留为未验证假设，未有同条件因果对照前不能在README声称缺ctfmon必然返回E_FAIL。11:51启动ctfmon的记录来自DSH自身ti01-progress，不是Codex；更正归属，或只记实测不写作者推断。

TI-01未满足项请保留“真实微软拼音”的原门槛，不追加“或等价TSF输入法”来替代。只取得WeType数据支持IMM作为候选路线，不能把Windows最终路线写成已冻结；若确需TSF，最小应用客户端可行性仍须TI-01先验证，再允许TI-02冻结，不能推迟到依赖后续契约的TI-07才首次验证。应用接入实现的是ITextStoreACP/线程与文档管理器，ITfTextInputProcessor是输入法服务端接口，不应当作应用文本存储的一部分。

工具README PowerShell示例的 `$exe = tools\...\imeprobe.exe` 会执行右侧命令而不是保存路径，须改为带引号路径（如 `$exe = '.\tools\...\imeprobe.exe'`）。请修复后核对可复跑参数。文档仍称新15项全为“纯托管辅助”也不准确：日志测试直接P/Invoke kernel32，需明确Windows测试范围或提供非Windows分支。

临时profile的状态位/异常恢复两个P1仍见上文，当前源码尚未关闭；即使最终状态为待验证，也必须先保证所交付验证工具不会错误改变用户配置，才可进入审核。

### Codex 复审接续（2026-10-17）

已通过ACP下发聚焦返修任务（session `2b3be748-a7c9-46c5-9898-0190b0bcdcb8`，模型 `opencode-go/deepseek-v4.1-flash`），要求优先关闭状态位/异常恢复两个P1及文档事实问题，完成前不运行该工具的临时配置变更，不提交推送、不进入TI-02。

本次只读独立检查 `ImeStaReview.dll --identity-only` exit 0：WinSta0/Default，session=5、activeConsole=5、interactive=True；实际WeType flags=3；微软拼音GetProfile S_OK、flags=0。之前受控试验后的当前状态与原快照一致。此检查没有激活或变更输入法。

### 返修中间复审：TemporaryProfileAttempt 的 RestoreVerified 语义仍须修正

当前重构的成功路径 `return new TemporaryProfileAttempt(created, true, true, true, string.Empty)` 在scope仍存活、尚未Dispose时就把RestoreVerified设为true；这不是真实回读恢复结果。建议该字段改为可空状态（尚未恢复=null），或成功路径false并明确“恢复未执行”，调用方仅在Dispose后读取Scope.Restored；不可把“已激活且稍后会恢复”写成“恢复已验证”。创建/快照失败且ConfigurationTouched=false时区分无需恢复与恢复已回读，不混用通过日志。另正式Dispose最好用try/finally保证RestoreCore异常也Release；核心恢复步骤已有独立catch，这是正确方向。请在完成汇报前复读此追加项。

返修中间复审补充：`RestoreCore.activeRestored` 目前只比较CLSID/ProfileGuid；原活动项若为TF_PROFILETYPE_KEYBOARDLAYOUT，两GUID常为空，不同语言/HKL也可能被误判恢复成功。请将ProfileType、LangId纳入身份；keyboard-layout类型还须比较Hkl（必要时SubstituteHkl），输入处理器则核对CLSID/ProfileGuid。增加原活动为键盘布局但恢复到另一HKL的确定性失败测试。当前实际WeType路径不受此特定问题影响，但可复用恢复工具不能漏掉键盘布局分支。

Codex独立工具回归：本轮当前编译产物 `dotnet exec tests/CSharp.UI.ImeProbe.Tests/bin/Debug/net10.0/CSharp.UI.ImeProbe.Tests.dll` exit 0，28项通过、0失败、0跳过。此结果仅证明已纳入的工具单测通过，不关闭上面两项新追加的恢复状态/键盘布局身份复审项，也不替代真实微软拼音门槛。请DSH结束前复读return末尾并补对应断言。

## 2026-10-17 本轮复审结论

工具修复已核对：状态启用位改为0x2；变更前承担恢复义务；日志失败不阻断恢复；S_FALSE不算激活成功；RestoreVerified改可空三态；恢复核对type/langid/HKL或profile身份；Dispose finally释放后端。确定性测试覆盖flags 0/1/2/3、日志失败、部分激活失败、S_FALSE、后端异常、错误布局/语言及恢复失败。Codex独立执行最终编译产物：35项通过、0失败、0跳过，exit 0。上述具体工具问题可关闭；这不是TI-01整体验收通过。

独立扫描码对照两次：真实微软拼音激活与身份校验成功，WM_KEYDOWN记录出现扫描码（如N的lParam=0x310001）。两次均在固定串输入阶段失去前台，保护检查停止注入，ime-commit均SKIP，不能证明微软拼音提交成功，也不能据此否定IMM。两次恢复回读均RESTORED=true；第一次原配置为英文键盘布局（HKL=0x04090409），第二次为WeType，均恢复到各自快照，目标微软拼音启用位恢复false。未继续争抢前台或绕过保护。

**仍未通过的阶段门槛**：真实微软拼音预编辑、单次提交、取消及实际候选位置。下一步先在前台稳定的专用窗口完成扫描码对照；若仍失败，提出并审核最小TSF客户端可行性实验。TI-02继续待开始，生产框架未改；没有批准提交/推送实现。

独立证据已保存到 [配套证据目录](2026-10-17-text-input-and-editing-review-evidence/README.md)，不再仅依赖可清理的artifacts。

## 2026-10-17 接续验证：真实预编辑观察更正及候选定位补充审核

逐条检查 `codex-mspinyin-scancode-review/events.jsonl`（14:47重试）可见seq76/98/110/122真实GCS_COMPSTR为n、ni、ni'h、ni'ha；seq140有带标志结果niha，之后失焦/注入中止。此前仅按SKIP汇总写“仍无预编辑”不够准确。该轮after-focus为MS身份，但组合时未再次读profile，故这部分只能作为进一步IMM验证线索，不能直接据此认定MS提交验收通过。新ACP任务 `564fdc8f-fadb-4993-96c2-038a9ee70bec` 正在改进就绪顺序及事件时刻身份核对。

### [P2] 候选样式不要合并两个互斥取值；IMR_QUERYCHARPOSITION不是仅重转换查询

`ImeProbeWindow.PublishCandidateAnchor` 当前先设置CFS_CANDIDATEPOS，再设 `CFS_CANDIDATEPOS | CFS_EXCLUDE`。官方CANDIDATEFORM.dwStyle明确为二者之一，而非任意位组合；请改成有效的单一值（需要排除区域时用CFS_EXCLUDE），保留真实返回值，不用无效组合探针作为候选能力结论。来源：https://learn.microsoft.com/en-us/windows/win32/api/imm/ns-imm-candidateform 。

`OnImeRequest` 把IMR_QUERYCHARPOSITION注释为“重转换字符位置查询，本轮不实现”。实际它请求的是组合串字符坐标；自绘组合串的应用应填IMECHARPOSITION，默认组合UI则可转默认过程。请纠正注释/能力判断：当前只观察默认UI可转发，但不能用未实现几何回调否定系统候选锚点。若候选定位验证需要，应以最小固定测试串几何实现该请求并核对dwSize、dwCharPos、屏幕坐标、行高和文档矩形，不能伪造通用字符几何。来源：https://learn.microsoft.com/en-us/windows/win32/intl/imr-querycharposition 。

本次codex-ms-commit-current与codex-ms-commit-shift为前台稳定但纯英文字流，恢复均通过；仍须比较激活后异步模式/上下文，不能以此断言平台不支持。

### [P1] 本轮 CommitOutcome 把拼音子串要求施加到中文结果，导致正确“你好”永远不能通过

当前新增 `CommitOutcome.Evaluate` 在requireChinese=true时，先要求result非ASCII，再要求 `ContainsExpectedPinyinSubstring(result, expectedPinyin)`；因此正常nihao→你好必然返回false（“你好”不含nihao）。拼音匹配应针对同一composition会话的预编辑历史（规范化n/ni/ni'hao），而不是中文结果。固定nihao验证可直接要求结果为预期“你好”，并另行检查本会话记录过nihao预编辑；其他拼音显式配置预期结果或保持内容OBSERVED，不用非ASCII一概代替中文。

请在结束前修正并补语义测试：nihao预编辑+你好结果成功；niha/nihao原样回吐失败；没有对应composition/来自旧会话的结果不能归因于本场景。不要把“你好不含nihao”的错误判据固化成测试。`ContainsNonAscii`也并不等于CJK（é/全角标点均非ASCII）；固定测试用明确预期内容即可，不需实现复杂通用语言检测。

新IdentityProbe也不能把 MatchesConfiguredProfile 永远赋值 matchesMicrosoftPinyin，用--profile wetype/current时语义错误；应接受实际目标配置并核对hr成功、type/lang/CLSID/GUID。回调仅登记的标签应包含原事件seq/compositionId和请求时刻，回调后查询标注查询时刻（不能把晚查值写成“事件瞬间身份”），以便审核是否有间隔中的切换。

### [P2] 新组合键finally没有实现其注释声称的键态恢复

当前PressCombination在second-down后Pause抛错/失焦时，不保证second-up执行；finally只发first-up，而SendRaw在Aborted或前台不匹配时会拒绝发送，所以“无论中途哪步失败都恢复”不成立。请不要在这条路径未闭合前实跑Ctrl+Space。该选项不是TI-01必要能力，可以直接移除/拒绝ctrl-space并改README；若保留则需可注入的键盘后端与失败路径验证，明确已注入keydown的键如何释放、不能把跳过keyup报告为已恢复。不为可选预热无限扩大工具；当前none/shift真实场景可继续。

接续执行环境对照：DSH本轮 `ti01-r2-commit` 再次ActivateProfile E_FAIL，而Codex通过require_escalated执行的 `codex-ms-commit-current` / `codex-ms-commit-shift` 相同目标激活为S_OK并确认实际MS身份。这仍未证明根因，但应比较DSH执行子进程限制/自身token完整性与Codex显式审批路径，不要继续把profile激活失败归因输入法能力。DSH可完成代码与单测后返回，由Codex在明确权限审批下做原生验证；不绕过自身工具限制，不反复触发失败的配置写入。

另现有“Get-Process imeprobe”并不能覆盖 `dotnet exec imeprobe.dll`（进程名可能为dotnet），原生复测前应确认没有其他相同探针实例；不并行共享窗口/配置试验。正式工具可用单实例命名mutex拒绝重复运行，但本轮无需为此扩大重写。

收尾核对（固定结果归因）：当前CommitOutcome.Evaluate在有expectedResult分支返回Satisfied=contentMatched，忽略pinyinSeenInPreedit。请改为contentMatched && pinyinSeenInPreedit（或在场景调用方显式同时要求两者），Reason不能在false时仍声称预编辑已观察。补“结果你好但本次预编辑为空/只有上一场景历史→不通过”断言；不能只用结果相等将旧/外来结果归因当前输入。其他拼音未配置明确结果时应在报告说明内容验证边界，不把非ASCII等同固定选词验收。

### [P1] 分段场景必须分别保存每次结果所属组合的预编辑快照

OnCompositionStart现在清空 `_preeditStrings` 是正确的隔离方向，但RunImeSegmented在两次提交完成后，把同一个 `after.PreeditStrings` 同时传给firstVerdict和secondVerdict。第二次WM_IME_STARTCOMPOSITION会清掉第一段nihao历史，最终只剩ma；因此补强归因后第一段“你好”会永远失败。请在每次带标志的新结果入队时，同时复制该结果对应的compositionId与预编辑历史，并让commit/segmented按每个新结果的快照判定（有界集合，结果+预编辑同消息要先快照旧组合再处理后续预编辑）。不要改成全局累计历史来规避，那会重新混入旧会话。

最小确定性验证应覆盖两段独立会话：nihao→你好、ma→吗，第一段历史不可被第二次start清除；本场景结果切片不能读前场景历史。另场景assertion字符串仍写“中文结果contains injected pinyin”，请同步纠正，避免新实现与日志断言矛盾。

逐结果快照实现接续核对：当前isNewResult已先复制_preeditStrings，这是正确方向；但复制后没有清空当前历史，若同消息结果+下一段compStr、并且后续没有新的WM_IME_STARTCOMPOSITION，下一条结果快照仍含上一段预编辑。请在冻结结果后重置当前段历史，再收集后续compStr；快照必须独立保留。最小测试需调用实际“冻结并清空历史”辅助（若抽取一个小方法），而不只是手工构造两个理想CommitSnapshot证明slicer工作，避免测试没覆盖生产记录路径。测试nihao结果后同消息ma→下一结果历史只含ma，不再含nihao。

## 接续收尾复审（2026-10-17，86项工具测试）

本轮新增审核项已逐项核对：

- 初始化顺序改为获焦/泵、激活、泵、设置目标HIMC native/open、泵及回读；效果仍待真实输入对照，不把假设写成根因。
- 身份记录可配置MS/WeType/current，包含登记事件seq/compositionId与查询seq，明确查询在回调退出之后；不冒充事件瞬间身份。
- 候选样式使用单独CFS_EXCLUDE，IMR_QUERYCHARPOSITION注释已纠正；候选双锚点截图仍须实际看图验收。
- 移除不保证keyup的PressCombination路径；ctrl-space预热在参数解析阶段明确拒绝。
- 固定nihao结果必须等于“你好”并能由本段预编辑归因；结果相等但缺历史、ASCII回吐或不符内容均不通过。
- 每个结果冻结compositionId与预编辑副本，并立即清空当前段历史；同消息后续预编辑再进入下一段。生产路径调用FreezeResultAndResetHistory，测试直接覆盖该方法，而不是只构造理想数据。
- commit/segmented按场景起点之后的逐结果快照分别判定，不复用最终那一份当前历史。

Codex独立执行最终工具测试：**86项，0错误、0失败、0跳过，exit 0**。最终只读profile复查：活动WeType flags=3，微软拼音GetProfile flags=0，exit 0。证据见配套目录的codex-probe-tests-final.log及codex-final-profile-readback.log。

上述具体工具缺陷关闭，但不代表TI-01通过。DSH本轮原生场景在其执行上下文激活E_FAIL，或因前台不属于探针而SKIP；Codex早先受审批运行激活成功，但组合输入仍缺稳定前台下的完整证据。前台不可得不是授权使用后台注入/合成WM_IME的理由，未绕过保护。下一步需要一小段前台保持时间，由Codex从已审批执行路径运行真实commit/cancel/segmented/candidate并查看候选截图。

生产src未修改，TI-02仍待开始，未批准实现提交或推送。不要求用户永久启用微软拼音/修改默认输入法；受控验证继续临时启用后快照恢复并回读。


## 2026-10-17 用户范围修订与 TI-01 路线放行

用户确认微软拼音已删除且不要求测试。取消该输入法的安装、启用及真实验收门槛，取代上述历史阻塞结论。Codex依据现有WeType真实预编辑/单次中文结果、最终独立86项探针测试及平台矩阵，通过TI-01路线审核，允许TI-02实施。此结论不表示Windows完整IME验收完成：候选窗实际定位、取消及生产宿主联调仍由TI-07/TI-10使用当前可用输入法补齐。保留历史日志，不继续改变系统输入法配置。
