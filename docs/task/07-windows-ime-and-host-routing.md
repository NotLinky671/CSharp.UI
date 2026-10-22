# TI-07：Windows IME 桥与宿主输入联调

创建日期：2026-10-17

状态：**TI-07 已于 2026-10-22 完成**（P1～P4 全部验收，主代理最终批准；在记录的平台/单 DPI/输入模式边界内）。隔离原型、正式 OpenTK 宿主 P1/P2/P3b、P4（取消安全/失活零写/恢复与工具收口）均已验收；见[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)。TI-08～TI-10 未开始；默认 IMM 不变。此前阶段限制与进展作为历史记录。

## 2026-10-22 P4 取消安全批通过，TI-07 完成

- minimize/hide/窗口失活三路径失活段旧组合结果零写 + 恢复新 epoch 唯一提交你好（setter +1），postclean2 三场景 exit0；归因判据为 capture 时冻结 `GetFocus!=hwnd||GetForegroundWindow!=hwnd`（单顶层宿主契约）。
- Release 全量 2053/0 失败（同一最终源）；TsfCancellationTests 10/10。记录：[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)、[恢复后核心实现审核](../return/2026-10-22-ti07-p4-restored-core-review.md)。
- NativeCompositionOwner.cs 误删恢复：Release DLL 反编译+补齐，非逐字等价（边界如实声明，见交付记录 §4）。
- **TI-07 完成**（边界内）；提交推送由主代理安排。

## 2026-10-22 P3b 验收完成，P4/TI-07 未完成（历史）


- Debug 147 / 最终 Release 166 项（ACP75、reader38、sink18、geometry5、GPU4、取消4、生命周期1、通知回归1、正式宿主1、RouterFallback7、WindowsInputBridgeContract12）Failed 0 / exit 0；Release 原始日志与工具 selftest 已落盘。
- 真实微软拼音：640 高窗 9 项候选可见、预编辑零写、Space 精确“你好”/唯一写入；滚动 0→7→324→0，小滚跟随、完全裁出隐藏且组合保留、恢复同一组合身份（session/composition/element generation/preedit）后唯一提交；Escape/数字/分段通过；Enter 候选 ready 后提交原始拼音（本机行为，不要求汉字）。
- 记录：[P3b 交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md)、[独立审核](../return/2026-10-22-ti07-p3b-independent-review.md)。
- （历史）P4 未覆盖矩阵见交付记录 §5；彼时整体 TI-07 不勾选、不提交推送。

## 2026-10-22 P4 产品边界审核（历史分工记录，已收口）

（历史分工记录）分工：p4-product-review 独占产品 `src/`、相关产品回归 `tests/`、`docs/plan|task|guide|delivery` 与独立审核记录；p4-real-validation 独占 `tools/CSharp.UI.ImeHostCheck` 与真实执行、dotnet/可见运行。P4 可达判据与待观察产品风险已写入[正式接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md) P4 节；产品侧只读核对记录见[2026-10-22 P4 产品边界审核](../return/2026-10-22-ti07-p4-product-boundary-review.md)。首个真实失败交产品审核侧从原始日志/截图/源码定位根因，只修确证缺陷。（当时约束：P4 未全部通过前不勾选、不提交推送、不启动 TI-08——现已按批准结论收口。）

## 2026-10-21 P3b 宿主接线实现完成，待独立验证（已由 2026-10-22 交付记录关闭）

- `TsfHostSession` 改为单次 `ActivateEx(UIELEMENTENABLEDONLY)` + `GetActiveFlags` 校验，UIElement sink 成对注册/撤销；失败逆序释放并回退 IMM。
- `TsfUiElementSink`：Begin 无永久墓碑、RCW 单次释放（含 QI 异常）、嵌套 Update 新序优先、Clear 防嵌套与注册阻断、Dispose 先置失效。
- `CandidateOverlay` clean cutover 为 SetSnapshot/SetGeometry/ClearSnapshot/Clear；测量缓存、有限值/客户区/可见交集/上下放置与 try/finally 裁剪。
- `HostAcpGeometryProvider`：文本几何与显示表面**独立**缓存；`GetTextExt` 身份签名迁移全部实现（无 shim）；布局通知延迟到真实安全阶段、发送时按当前 sink 与 mask 门控（连接撤销/释放清空延迟队列，旧连接排队的项不会送给新连接）。
- 正式宿主 `UiApplication.Run` + 隐藏 GL + 真实 TextField 焦点 smoke 已实际通过（TSF 后端、真实 flags、sink advised、候选能力、退出释放）；Debug 计数与命令见[执行记录](../return/2026-10-21-ti07-p3b-host-integration.md)。2026-10-22：真实微软拼音 640 高窗可见候选基线已通过（9 项真实候选可见、预编辑零写、空格唯一精确提交“你好”、exit 0；[证据](../return/2026-10-22-ti07-p3b-real-ime-evidence/nihao-visible-baseline/ime-host-check.jsonl)）。**上述为 2026-10-21 节点结论；滚动/完整裁出与恢复、取消/数字/分段等已由 2026-10-22 实测覆盖（详见[P3b 交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md)），移动/DPI、失焦/失活等 P4 场景与 TI-07 整体仍未通过；历史失败证据不修改。**

## 2026-10-21 P3b-A 共享契约/读取器/ABI/overlay 实施中

按用户授权，P3b 先分离基础模块与宿主接线。A（本轮）已完成并冻结共享契约：
- `src/CSharp.UI.OpenTK/WindowsTsf/CandidateSnapshot.cs`：不可变 `CandidateSnapshot`/`CandidateSnapshotEntry`、`CandidateSnapshotLimits`（页32、页表1024、文本128；总候选数不设256硬拒）、唯一有界读取控制流 `CandidatePageReader`（逐次结束复核、页表严格校验、buffer 精确释放、异常 fail-closed）、`ICandidateSnapshotSource`/`ComCandidateSnapshotSource`。
- `src/CSharp.UI.OpenTK/WindowsTsf/TsfUiElementInterop.cs` + `TsfInterop.cs`：`ITfThreadMgrEx`（显式重声明11基槽 + ActivateEx/GetActiveFlags）、`ITfUIElementSink`、`ITfUIElementMgr`、`ITfUIElement`、`ITfCandidateListUIElement`、UIElement IID 与激活标志。仅 ABI，未激活 TSF。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：内部 `FrameOverlay` 帧末追加入口（`Root.View.Emit` 后、`context.ToArray` 前）；核心不依赖候选类型。
- `src/CSharp.UI.OpenTK/WindowsTsf/CandidateOverlay.cs`：消费快照与独立逻辑 `clientBounds`/`anchorRow`，仅用框架 `TextPainter`/`DrawContext`，不读 COM、不继承 TextField 裁剪；无效快照/缺几何/上下放不下零命令，同帧内容之后绘制。`TextPainter` 增加无 `ViewNode` 的排版/绘制重载。
- 测试 `tests/CSharp.UI.OpenTK.Tests/CandidateSnapshotContractTests.cs`：38 项，覆盖大总数分页、页边界/严格页表校验、错误 HRESULT、结束重入、buffer 配平、文本截断（不拆代理对）与快照防御复制。

Debug 构建通过（0错误），上述类过滤测试 38/38 通过。ABI 的真实 `ActivateEx`/`GetActiveFlags`/sink 需由后续 UI-less 宿主接线与独立验证证明；本批不声称 P3 或 TI-07 通过。正式宿主接线设计（ActivateEx/flags、sink 生命周期/RCW、几何与安全阶段、同帧 overlay、失败回退及文件范围）见[P3b 集成设计](../plan/2026-10-21-ti07-p3b-host-integration-design.md)；基础 GPU 验收前不改 session/bridge/host。

独立 `TsfUiElementSink` 与生命周期测试已按修正后的 cookie/Begin 重入/结束墓碑语义写入（`src/CSharp.UI.OpenTK/WindowsTsf/TsfUiElementSink.cs`、`tests/CSharp.UI.OpenTK.Tests/TsfUiElementSinkTests.cs`），尚未接入宿主、尚未构建（按调度等待 validation GPU 与主代理统一构建）；设计中的几何身份校验与 Store 只读查询身份已同步修正。

## 2026-10-21 P3a通过，进入P3b

主代理最终独立验收：Debug/Release各339项指定测试、8个审计工程/9次隐藏宿主运行全部通过，证据及修复轨迹见[接续审核](../return/2026-10-21-ti07-continuation-review.md)。本结论仅关闭P3a，整体未交付，默认IMM不变。

P3b先分离基础模块与宿主接线：
- A：生产CandidatePage快照/有界读取与UIElement ABI接口及纯托管负向测试；不改session/host/runtime。
- B：生产CandidateOverlay布局/绘制（仅不可变快照）与框架同帧追加入口及图形验证；不读COM，不改session/bridge。
- A/B先固定共享快照契约，互不重写；交回后由主代理串行构建/独立验证，再接UIElement sink/ActivateEx/几何与正式宿主，不在未审核基础上大批迁移工具。
- 正式UI-less要真实ActivateEx与GetActiveFlags证明，不双激活；Begin不读未ready内容，id0合法，按页有界而非总候选256硬拒。候选文字使用框架TextPainter/DrawContext，禁止引入原型GDI/工具程序集依赖。
- 几何使用真实ICompositionCharGeometrySource不可变快照和最终布局裁剪，匹配epoch/version/native map；缺失返回不可布局，不能伪造原点。PublishGeometry在布局/帧中只缓存，原生布局通知延至真实安全阶段。候选绘制必须在同一TextureService.BeginFrame保护内、内容之后、提交之前，不额外BeginFrame。
- 独立验收保留精确文本与setter次数（防此前取消测试假阳性）；P4必须实际微软拼音，不以受控owner事实或旧GDI原型代替。全部通过前不提交推送。

## 2026-10-21 接续执行：P3a 定向收尾（历史过程）

用户授权主代理继续完成 TI-07。保留已有任务集和证据；以下执行安排优先于历史工具与模型说明。实现使用 workflow，provider=`magpie`，model=`devin/deepseek-v4-1-flash-1017`（用户最新确认并已连接验证）。

1. 并行返修 A：仅输入适配器与 OrderedInputDeliveryTests，统一立即/重放的交付守卫，FIFO 单轮预算、一次性重入回归及持续追加来源的公平性。
2. 并行返修 B：仅宿主、WindowsTextInputRouter、TsfInputBridge/HostInputCoordinator 的真实安全边界通知，接通当前真实 TSF 协调器并成对解绑；避免周期轮询、递归消息泵及持续重绘。不得和 A 修改同一文件。
3. 两项稳定交回后由主代理串行构建、独立执行 default/replay/reentrant/immediate/safe-signal 审计；子代理不得并发构建共享输出，不修改独立审计断言，不提前提交。
4. 随后补两字段事实/字符/点击交错、非零反向初始选区、MTA/失败回退、释放及 resize/frame 守卫证据；P3a 通过才实施 P3b 和 P4。
5. P3b 正式 OpenGL 候选和 P4 真实微软拼音仍必须按现有生产计划验收。整体通过前不启用 TI-08，不宣称交付。

最新独立复核（2026-10-20）：隔离context/view探针R20/R21修复通过；Debug/Release各168正式测试、67自检，隐藏宿主与只读探针MTA/STA均通过，audit29/29、native smoke2/2。用户确认中文模式后，有效中文组合三组对照完成：范围0..6精确一致，view/store返回更新后几何；内部滚动候选仍不跟随、完全裁出仍不隐藏，整窗上移24物理像素候选跟随。三组组合期间零写入、清理无失败，7张原图已人工审核。R15仍开放，TI-07未通过；下一步先修订研究路线，不直接迁移生产TSF或实现自绘候选。详见[审核记录](../return/2026-10-20-ti07-tsf-prototype-review.md)。

当前有界验证：按[路线修订的2026-10-20追加范围](../plan/2026-10-19-windows-ime-candidate-layout-route-amendment.md)，仅在隔离工具增加默认关闭的布局通知调用链诊断与安全点只读焦点快照。明确区分应用探针、TSF回调及归因未知的延迟查询；诊断需先通过嵌套、线程隔离与异常处理审核，再运行不带主动context/view探针的真实中文场景。不得把TSF层回调直接称为具体TIP已消费新坐标，不新增无依据刷新调用。

阶段：C

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 2026-10-19 当前执行修订

用户已重新安装并正在使用微软拼音，明确授权开始TI-07；从本项起由Devin CLI承担代码实现与单元测试，Codex继续规划、独立审核和验收。先前2026-10-17的微软拼音免测仅为历史范围，不再限制本项和后续最终验收。

Windows真实验收以当前已安装的微软拼音为主要对象；已有其他输入法可作兼容补充。复用现有配置，不安装输入法、不修改全局默认或启用状态；必要时只在自有测试窗口/线程选择已有profile并恢复。验证真实预编辑、选词仅提交一次、Esc取消不退出、Enter不泄漏、150%候选锚点，以及移动/resize/滚动/焦点/外部绑定/关闭释放。不得用合成消息或普通文字截图替代真实输入法证据。

仍沿用TI-01已审核的自有HWND子类化+IMM32宽接口路线。若微软拼音实测表明路线不足，先保存真实消息与身份证据，提出最小路线修订交Codex审核，不擅自扩大TSF文本存储范围。Devin自测后等待Codex复审；问题写docs/return返修，只有明确通过后才英文提交和推送。

## 目标与范围

按已验证路线接入真实Windows输入法，保证单次提交、候选定位及按键不泄漏。

边界：Windows宿主桥与路由处理结果；TI-01路线变更先复审。

## 前置依赖

[TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md)、[TI-05：有界撤销重做与编辑命令](05-undo-redo-and-edit-commands.md)、[TI-06：预编辑绘制、命中与候选几何](06-preedit-rendering-and-geometry.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [ ] 实现TI-01确定的IMM或经批准TSF路径，固定委托生命周期、消息转发与原生资源成对释放。
- [ ] 规范化宽接口字节量/UTF-16单位与回调序列；每个结果只有一个提交来源，不用字符串/时间全局去重。
- [ ] InputRouter处理结果回到OpenTkInputAdapter，Enter/Escape/候选导航与结束同键后续回调不触发表单、控件或退出。
- [ ] 把最终逻辑caret变换为平台要求的客户区/屏幕坐标，DPI/移动/滚动/失焦/关闭按规则更新或撤销候选锚点。

## 测试与验收

- [ ] 当前已安装的微软拼音（主要验收对象）的真实预编辑、空格/数字/Enter选词、Esc取消、连续分段及英文/Unicode普通输入仅一次；记录完整事件序列。
- [ ] 150%真实候选窗跟随caret，窗口移动/resize、滚动、跨断点、失焦/失活、绑定外改和关闭释放。
- [ ] 合成平台事件覆盖错误/乱序/重复与资源计数；原生输入证据不可仅用DispatchComposition代替。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。Devin CLI自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，Devin CLI返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

## 2026-10-17 范围修订

用户已移除微软拼音且明确无需测试，不安装、启用或测试它。TI-01仅放行接入路线；本任务仍须补齐可用输入法的真实取消、候选定位与生产宿主验收，不能用探针API调用或合成消息标为通过。

## Codex 实施中路线对照（2026-10-19）

独立探针已在当前微软拼音、MAINSTA、150%窗口下取得nihao预编辑、候选“你好”和唯一GCS_RESULTSTR“你好”，也观察到重复字符通道。见[路线对照与证据](../agents-notebook/2026-10-19-ti07-mspinyin-route-check.md)。这不是生产宿主验收，不标本任务通过；不得仅引用旧探针--profile current的泛化PASS作为中文判据。

## Codex 实施中首审门槛（2026-10-19）

首批代码静态审核发现R1～R5：结果来源/回声归属、原生取消、本地无标志取消、公开DispatchKey兼容及提交回调换会话后的收尾隔离。见[TI-07审核与返修要求](../return/2026-10-19-ti07-windows-ime-review.md)。Devin自测后必须读取、返修并交Codex复审，不提前提交或标完成。公开void DispatchKey保留，允许新增internal处理结果入口；核心已有对OpenTK的友元访问。

## 2026-10-19 复审进展

用户指定Devin CLI SWE-2（实际`swe-2-high`）。Codex独立Debug/Release构建与正式回归各950项通过，独立审核25项通过（包含真实不可见Win32窗口过程及工具取消）。生产MTA、150%窗口的当前微软拼音已取得真实预编辑、候选“你好”、唯一中文提交和Enter提交证据；完整矩阵首轮仍有失败及脚本弱判据，最新R15返修尚未放行。证据与结论持续维护于上述审核记录及其同名前缀`-evidence`目录，不据此提前标记本任务完成。

## 真实英文补验进展（2026-10-19）

用户手动切英文并确认后，ascii-plain日志精确追加abc、setter+3、无组合且无重复；英文行为已通过。原始日志及统计退出码问题见审核文件文末与manual-english证据目录。当前仍待R16：真实候选动态跟随及完全裁出后的可见撤销；NI_CLOSECANDIDATE/NI_OPENCANDIDATE实测不支持，工具层隐藏caret对照正在准备。不得把这次英文通过等同于整个TI-07验收完成。

## 最新独立复审（2026-10-19）

R17字符几何应答及失效/默认链隔离已复审：Debug、Release各965项正式测试通过，独立真实隐藏HWND与工具审计42项通过。英文真实补验通过。真实微软拼音滚动第二轮仍存在候选不移动和完全裁出仍可见，故R16未关闭、TI07整体未通过、不提交。截图与原始日志已持久保存至审核证据目录query-scroll-round2。下一步仅补最小滚动后内容变化诊断并审核路线，不以API返回成功代替可见行为验收，不自动取消组合或降低已冻结语义。

## 候选布局路线修订（2026-10-19）

追加内容能使候选跳到新位置，单独几何更新及官方候选控制消息仍未通过可见跟随。Codex已批准[候选布局路线修订](../plan/2026-10-19-windows-ime-candidate-layout-route-amendment.md)中的隔离TSF可行性原型；尚未批准生产迁移或更改宿主套间。原型、锁协议和真实候选证据复审通过后再决定生产接入。任务仍进行中，不开启TI-08及以后任务。

## 2026-10-20 模型调整

用户指定Devin CLI改用DeepSeek V4.1 Flash，当前实际模型ID为`deepseek-v4-1-flash-1017-high`；替代此前SWE-2。Codex继续承担规划、独立审核与真实验收。隔离TSF原型已开始实施，审核问题持续记录于[2026-10-20原型审核](../return/2026-10-20-ti07-tsf-prototype-review.md)，尚未放行生产迁移或提交。

## 当前执行工具与后续诊断

用户随后指定切回 DeepSeek Harness，精确模型 `commandcode-plan/deepseek/deepseek-v4-flash`，替代 Devin CLI。ACP 已确认模型并完成无工具回复验证。Codex 仍负责审核。

隔离 TSF 的第二轮真实小滚动仍未通过：TIP 已读取新矩形，候选未移动；不能以布局通知 S_OK 或自动测试通过交付。下一步限定为路线复核及可逆诊断：检查现有协议实现、对照套间与较大但仍可见的布局变化，并评估 UIElement 候选呈现的能力和限制。先提供证据和有界实验方案，再决定后续实现；不批准生产迁移、自绘候选产品接入、改变组合语义或降低验收标准。不启动 TI-08，不提交推送。
<!-- 2026-10-20 bounded isolated diagnostic update -->

### 2026-10-20：隔离 TSF context/view 几何对照

本轮真实对照发现：候选随整窗移动，但不随窗口内文字滚动；大幅150物理像素位移仍失败，完全裁出仍可见。显式隐藏因无活动UIElement未执行，不算API无效。详见[审核记录](../return/2026-10-20-ti07-tsf-prototype-review.md)末尾。R15仍开放，不提交推送。

后续仅在隔离工具中增加默认关闭的TSF context/view只读几何探针，对比store与context view的范围矩形、屏幕矩形及HWND；不修改文本/组合/真实选区，不增加刷新变体，不做生产迁移或自绘候选。实现仍使用DSH ACP，精确模型`commandcode-plan/deepseek/deepseek-v4-flash`；Codex独立审核并负责真实输入与截图。无输入隐藏回归只验证接线，不代替中文候选视觉验收。

### 2026-10-20 调用链验证结果与停止条件

默认关闭的trace诊断已由DSH实现、Codex独立审核：Debug/Release各204正式+74自检，独立协议29项/作用域6项、MTA/STA原生及16隐藏入口通过。trace-only真实中文滚动与裁出仍视觉失败；通知内TSF回调已在有效READ锁下取得新几何/不可见零矩形，滚动前后安全点文档/top context身份正确。完整证据见[审核末尾](../return/2026-10-20-ti07-tsf-prototype-review.md)。不得把回调嵌套写成已识别TIP消费者。

本轮按停止条件结束刷新变体实验。R15/TI07未完成，TI08不启动。下一项先核对GetScreenExt显示表面契约，特别是无锁调用、非法view错误码、与文本布局失效的关系；明确依据后才能做最小协议修订与单变量对照。它不是已经确定的候选问题根因，不以协议测试替代真实视觉验收。

### 2026-10-20 R24 执行

微软官方样例已明确GetScreenExt不需要文档锁，独立复现3失败。按[路线补充计划末尾](../plan/2026-10-19-windows-ime-candidate-layout-route-amendment.md)修正隔离原型显示表面契约：无锁可查、非法view E_INVALIDARG、与文本布局版本解耦、真实窗口客户区和最小化/未显示状态正确。GetTextExt读锁语义保留。DSH实现和目标测试，Codex独立审核及真实中文对照；视觉失败仍保持R15/TI07开放，不提交推送。

### 2026-10-20 R24 完成状态

GetScreenExt协议修订由DSH完成，Codex独立通过Debug/Release各217正式+75自检、29协议+6scope+15显示表面audit、16隐藏入口及MTA/STA原生smoke。真实中文单变量滚动与裁出仍失败，完整原图与日志已保存[审核记录末尾](../return/2026-10-20-ti07-tsf-prototype-review.md)。R24协议缺陷关闭，R15/TI07保持开放，不提交推送，不启动TI08；停止GetScreenExt刷新变体，不把修正无效果包装为候选已修复。

### 2026-10-20 当前模型与授权更新

用户明确指定并授权 `workbuddy-ai/deepseek-v4.1-flash` 通过 DSH ACP 读取、修改本项目隔离原型及测试，并运行测试；替代此前模型。ACP `session/set_config_option` 已返回精确模型且 `confirmed=true`。当前执行 R27 原生候选 UIElement 生命周期实验的审核返修：完整组合基线不变量、实际 profile、安全截图与清理结果。Codex 独立审核与真实中文验收职责不变，未通过不提交推送、不迁移生产、不自绘候选。

## R28 最终结论与 R29（2026-10-20）
Codex独立Debug/Release各41正式测试+27自检、2隐藏smoke、27/27真实隐藏几何audit通过；合法零高度恢复和守卫修复已复核，生产/TSF/三保护文件hash未变。证据codex-r28-collapse-final。

真实child-collapse运行exit3仅表示待视觉审核。Codex亲看3PNG：EDIT高度420→0→420，候选始终可见，collapse时移到父窗口上方区域，restore后回原位。实时ni'hao组合不变，测量期end/result/change/focuschange均0，当前微软拼音0804有效。隐藏FAIL，R28假设停止。原始图/日志/visual verdict已存r28-collapse-real。native_caret_screen字段实为未转换的client rcCaret，不能用于屏幕几何推理；本结论用真实PNG与已映射的EM/组合坐标，不受此错误影响。

R29拟在隔离TsfHostCheck显式--child-input-surface下，让真实单行输入child承担TSF关联/GetWnd、焦点、键盘和绘制，固定viewport内容器scroll移动该child；真实祖先交集用于GetScreenExt/GetTextExt。验证宿主表面差异，不再重复height0或UIElement Show刷新。范围与不变量详见artifacts/ime-work/ti07-dsh-r29-child-input-surface.md。DSH ACP devin/swe-2实现，Codex独立审核后真实验证；默认路径不变，不迁移生产或自绘候选。TI07未通过，无提交推送。

R29完整规划见[真实子窗口输入表面实验](../plan/2026-10-20-ti07-child-input-surface-experiment.md)，本轮仍是隔离原型验收，不代表TI07交付。

## 2026-10-20 R29B～R33 当前验收状态

当前 DSH ACP 精确模型为 `devin/swe-2`，由 Codex 独立审核。

- R29B 真实子窗口输入表面：内部滚动候选跟随已通过真实微软拼音视觉验收；完全裁出后候选仍显示，隐藏门槛未通过。详见[真实候选审核](../return/2026-10-20-ti07-r29b-real-candidate-review.md)。
- R30 隐藏自有输入子窗口不能隐藏候选，停止该路线。
- R31 暂停 TSF 文档焦点会同步提交组合并写入绑定，停止该路线。最终托管及原生接线回归通过不等于视觉门槛通过，见[真实焦点实验审核](../return/2026-10-20-ti07-r31-real-focus-review.md)。
- R33 对成熟宿主源码的独立核对没有取得新的原生候选隐藏方法；研究结论不能证明平台不可能，也不能用于降低验收。见[源码审核](../return/2026-10-20-ti07-r33-established-host-review.md)。

自绘候选未获授权；候选呈现待决方案不改变原验收条件。TI-07 保持未完成，不迁移生产、不提交推送、不启动 TI-08。

## 2026-10-21 隔离自绘候选路线视觉通过
用户在路线说明后允许继续方案A。DSH ACP现用精确模型devin/deepseek-v4-1-flash-1017、reasoning_effort=high，两项均由ACP回执确认。隔离原型R36在真实微软拼音/ReleaseSTA/DPI144下，滚动跟随及完全裁出隐藏/恢复均经Codex逐张核对12张窗口/整屏图通过，保持同组合、零绑定写入。详见[真实视觉审核](../return/2026-10-21-ti07-hostdraw-real-visual-review.md)。恢复后唯一中文提交另行补验。此结论不表示系统原生候选隐藏通过，也不表示TI-07整体验收或生产迁移完成；任务复选框保持未完成。

2026-10-21补验：R37同一组合恢复后，真实Space提交你好恰好一次、绑定source=tsf-composition，候选关闭，begin1/end1/session1/focus1；测量期仍零写入。Codex独立581/581通过。隔离原型阶段验收见[交付记录](../delivery/2026-10-21-ti07-candidate-prototype-delivery.md)，不等同TI-07整体完成。

## 2026-10-21 正式宿主接入（当前执行依据）

用户已授权继续生产接入。按[正式 TSF 接入补充计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)依次实施 P1～P4：宿主边界与 STA 租约、权威编辑文档适配、TSF 与 OpenGL 候选接线、真实生产验收。此修订取代上文历史阶段的“不迁移生产”限制。

- [x] P1：宿主输入桥抽象与 STA 生命周期基础；保留现有输入行为并通过回归。Codex 独立 Debug/Release 各 1000/1000、真实 STA/MTA 审计 8/8 通过，见[审核记录](../return/2026-10-21-ti07-production-p1-review.md)。
- [x] P2：生产 TSF store 与 TextField 编辑会话接入；协议及事务测试通过。owner 返修通过 Codex 独立 Debug/Release 各 1049/1049 核心及宿主回归，以及 39 个独立审计场景，见[最终审核](../return/2026-10-21-ti07-production-p2b-owner-review.md)。
- [x] P3：正式 STA 宿主、单来源输入路由与 OpenGL 候选呈现（P3a/P3b 已验收，见[P3b 交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md)）。
- [x] P4：正式 TextField 真实微软拼音矩阵、独立审核、交付（取消安全/失活零写批通过，见[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)）；实现提交推送由主代理安排。

（历史）DSH ACP 使用 `devin/deepseek-v4-1-flash-1017`，`reasoning_effort=high`。

P2 进展（2026-10-21）：P2a 权威编辑会话适配已通过 Codex 独立复审，Debug/Release 各 925/925 正式回归、八场景审计各 8/8 通过，见[审核记录](../return/2026-10-21-ti07-production-p2a-review.md)。P2b 协议首批也已通过：COM store、锁、通知、安全阶段与暂存缓冲完成，Codex 独立 Debug/Release 各 998/998 正式回归、12/12 审计及真实原生接线通过，见[协议审核](../return/2026-10-21-ti07-production-p2b-protocol-review.md)。下一批接入原生组合 owner、编辑完成边界和真实 TextField；P2 与整个 TI-07 仍未完成。
