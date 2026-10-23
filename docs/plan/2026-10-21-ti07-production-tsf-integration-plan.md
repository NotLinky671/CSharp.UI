# TI-07：正式 OpenTK 宿主 TSF 接入

计划日期：2026-10-21

状态：**已完成（2026-10-22，主代理最终批准）**。隔离原型、P1、P2、P3b、P4（取消安全/失活零写批）全部经真实验收与独立审核通过，见[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)。TI-07 在记录的平台/单 DPI/输入模式边界内完成；TI-08～TI-10 未开始；默认 IMM 不变。P2 最终独立结果见[owner 审核](../archive/2026-10-23-completed-reviews/2026-10-21-ti07-production-p2b-owner-review.md)；P3b 见[交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md)。

依据：[原编辑计划](2026-10-17-text-input-and-editing-plan.md)、[候选呈现决策](2026-10-20-ti07-candidate-presentation-decision.md)、[隔离原型交付](../delivery/2026-10-21-ti07-candidate-prototype-delivery.md)。执行归属见 [TI-07](../archive/2026-10-23-text-input-editing-tasks/07-windows-ime-and-host-routing.md)。本补充计划继续该任务，不重建 TI-01～TI-10 任务集。

## 交付目标

真实 TextField 使用微软拼音预编辑并只提交一次；候选由框架文本与 OpenGL 呈现，随有效文字几何移动，输入区域完全裁出时隐藏，恢复后保持同一组合并能唯一提交。原型的 GDI、合成文档和 BindingRecorder 不进入产品。

本轮接续由主代理负责规划、独立审核与真实输入验收；实现通过 workflow 并行委派，provider=`magpie`，model=`devin/deepseek-v4-1-flash-1017`（用户最新确认且连接测试成功，取代本轮先前的 commandcode-plan 模型）。每批审核失败写入 docs/return；整体通过后英文提交、推送，不提前提交混杂的现有工作区改动。此前 DSH ACP 的模型与思考等级记录作为历史保留。

## 接入约束

- 核心保持平台无关；COM、HWND 和 OpenTK 类型只在宿主程序集。生产程序集不引用 tools 或测试程序集。
- NativeWindow、GL 上下文、运行时和 TSF 文档在同一 UI 线程。TSF 要求 STA；先支持显式 STA 入口，保留 Run 同步返回、异常传播和 Dispatcher 行为。未经独立验证不把窗口迁移到后台线程，不全局关闭 GLFW 主线程检查。MTA 的明确降级和非 Windows 路径必须可测、能力报告必须准确。
- TSF 与 IMM 每个窗口只能有一个活动的组合写入来源。关闭重复 WM_CHAR/GLFW 回声必须依赖消息归属，不能按内容或时间去重。
- 已有 TextField 编辑事务是提交权威。TSF store 的预编辑镜像不得逐键写 Binding；提交后按真实回读版本同步，保留撤销、MaxLength、净化、setter 拒绝和外部绑定变更规则。
- 锁与延迟通知遵守已验证协议；原生回调不能排版、重建、生成帧或抛出托管异常。失焦、换 epoch、卸载及释放撤销旧事件归属，处理回调重入。
- UIElement 的 id=0 合法。按页面有界读取候选，不能因总候选数大于 256 就拒绝合法列表；页、条目和文本长度仍有安全上限。
- 候选在内容之后通过框架 DrawContext/绘制命令生成；遵守帧纹理保护、主题字体、DPI 和客户区边界。有效输入几何来自最终布局与祖先裁剪交集，隐藏/恢复不结束组合、不偷换焦点、不写绑定。候选自身按客户区约束，不盲目继承 TextField 的文字裁剪矩形。
- 输入法继续管理候选选择及语言逻辑。键盘选词是必验；若提供候选点击，必须调用有效且支持的 TSF 行为接口，不能直接把显示文本写入 Binding。不支持鼠标选择时明确报告该边界。
- 不安装输入法、不修改系统默认；不启动 TI-08 及以后任务。

## 按依赖实施

### P1：宿主边界与 STA 生命周期基础

抽取内部 Windows 输入桥契约，先由现有 IMM 桥实现，宿主和输入适配器通过契约依赖，保留验收工具的 IMM 观测入口。增加只管理当前线程 COM STA 引用的租约，成功 S_OK/S_FALSE 成对释放、失败不释放、禁止跨线程释放。此批不启用 TSF，不改 UiApplication.Run 的线程行为。验证适配器 token/消费语义和旧宿主图形回归。

### P2：生产 TSF 文档适配器

将必要 COM ABI、锁管理与 store 协议放到宿主模块，不引用原型程序集。建立当前编辑客户端、预编辑镜像、ACP 选区与版本映射；Start/Update/Result/Cancel/End 对接现有组合状态机。处理分段结果、回读拒绝、外部变更、焦点切换、锁内延迟通知及释放。先用无窗口/隐藏窗口测试通过，不切换默认生产路径。

分两批审核：P2a 先实现纯托管的权威编辑会话适配，并用真实 TextField 验证预编辑零写入、唯一提交、拒绝/净化回读、版本/epoch 失效以及替换选区映射；P2b 再接 COM store、锁与真实组合范围回调。P2a 的平台内部 API 使用核心值类型，不声明已完成原生协议。若需要更新非组合的选区，新增核心内部可选客户端能力，校验 epoch、文档版本及字素边界，不扩张公开 ITextInputClient，不通过合成按键间接修改选区。

P2b 内部继续分开检查协议与事务语义：先交付经过 SDK/原型核对的 COM ABI、ACP 锁/通知、临时原生编辑缓冲及只读几何供应器；再连接组合 owner 回调，按真实范围变化和结束原因驱动 P2a。前一批的 SetText 只改变临时缓冲，不能自动认定结果或写 Binding；后一批必须处理部分确认、取消、范围重锚及实际回读修正。不得把“store 能创建、协议测试通过”视为正式输入完成。

STA 入口优先支持应用以 `[STAThread]` 声明的现有主线程；租约不能把 MTA 转成 STA。专用 STA 线程仅是待验证的另一选项，不是强制架构，也不据此解除 GLFW 主线程约束。

### P3：宿主接线与候选 OpenGL 呈现

STA 上初始化 TSF，键盘预处理与消息转发接入自有 HWND；初始化失败彻底清理再走明确降级，不能并行启用 IMM 插入。连接 UIElement 候选快照、有界页面读取、最终几何及帧生成。候选变化唤醒按需渲染；失效时即时撤销旧快照，下一安全布局恢复。补充真实宿主测试入口与 STA 示例入口说明。

P3b 实施前补核：正式自绘路径需复用原型已验证的 `ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)`，核对真实 `GetActiveFlags`，且只配对一次激活/停用，不在普通 Activate 后叠加第二次激活。QI/激活或 flags 校验失败须逆序释放再明确回退，不伪称UI-less成功。`BeginUIElement`只按接口识别与可呈现能力决定pbShow，不因内容未ready错误放行系统候选；首次Update才读取快照；id0合法。候选文字命令在UiRuntime同一DrawContext、同一TextureService.BeginFrame之后、内容之后生成，不额外BeginFrame。依据[微软UILess总览](https://learn.microsoft.com/en-us/windows/win32/tsf/uiless-mode-overview)。当前只读准备不等于P3b已实施。

P3b 进展（2026-10-21）：基础契约批已实施并冻结——生产 `CandidateSnapshot`/`CandidatePageReader`（总候选不设256硬拒，按页有界；页/文本上限与严格页表校验）、UIElement ABI（`ITfThreadMgrEx`/sink/manager/candidate list）、`UiRuntime.FrameOverlay` 帧末入口与最小 `CandidateOverlay`（独立逻辑客户区/锚点、框架 TextPainter/DrawContext、无效/缺几何隐藏）。Debug 构建 0 错误，新契约类测试 38/38；真实 ActivateEx/GetActiveFlags/sink 行为、宿主接线与图形 smoke 尚待独立验证，P3 与 TI-07 仍未通过。正式接线方案已冻结为[P3b 集成设计](2026-10-21-ti07-p3b-host-integration-design.md)（ActivateEx/flags 单激活、sink/RCW/重入、几何与安全阶段、同帧 overlay、失败回退、文件范围）；基础 GPU 验收前不修改 session/bridge/host。独立 `TsfUiElementSink` 与生命周期测试已写入但未接宿主、未构建；cookie 以 `TF_INVALID_COOKIE`+显式 advised 标志判定，Begin 先退休旧 Entry 再 GetUIElement，并以 Begin 代/结束墓碑拒绝重入复活。

P3b 验收（2026-10-22）：Debug 147 / 最终 Release 166 项（ACP75、reader38、sink18、geometry5、GPU4、取消4、生命周期1、通知回归1、正式宿主1、RouterFallback7、WindowsInputBridgeContract12）Failed 0 / exit 0；Release 原始日志与工具 selftest 已落盘。真实微软拼音 640 高窗 9 项候选可见、预编辑零写、Space 精确“你好”/唯一写入；真实滚动 0→7→324→0、完全裁出隐藏且组合保留、恢复同一组合身份后唯一提交；Escape/数字/分段通过；Enter 候选 ready 后提交原始拼音（本机行为，不要求汉字）。见[P3b 交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md)。**P4 与 TI-07 整体仍未通过**，未覆盖矩阵见交付记录 §5。

### P4：生产验收与交付

P4 完成（2026-10-22，主代理最终批准）：取消安全/失活零写批通过——失活归因判据为 capture 时冻结 `GetFocus!=hwnd||GetForegroundWindow!=hwnd`（minimize 走 focus 侧、hide 走 fg 侧；单顶层宿主契约）；drain 在首个 deactivated fact 前执行 deactivation fence，独立结果恰好一次提交不变。postclean2 三场景（minimize/hide/窗口失活）exit0：失活段 0 旧段写 + 恢复新 epoch 真实 nihao→Space 唯一提交你好。Release 全量 2053/0 失败（同一最终源，`test-sp-*`）；TsfCancellationTests 10/10。NativeCompositionOwner.cs 误删经 Release DLL 反编译恢复（非逐字等价，边界见交付记录 §4）。详见[P4 交付记录](../delivery/2026-10-22-ti07-p4-delivery.md)。

原验收矩阵：真实微软拼音在正式 OpenTK TextField 上验证：nihao 预编辑、空格/数字/Enter 选词、Escape 取消、连续分段、普通英文和 Unicode；每个结果只提交一次，按键不泄漏为退出/表单动作。150% 下内部滚动、完全裁出及恢复后提交、窗口移动/resize、响应式切换、可用显示器 DPI 变化、横向文本滚动、失焦/失活、绑定外改、禁用/卸载/关闭。记录实际可执行场景与未覆盖平台边界，不用模拟事件替代真实证据。

#### P4 执行范围（2026-10-22 本轮，产品审核侧）

分工：p4-product-review 独占产品 `src/`、产品回归 `tests/`、`docs/plan|task|guide|delivery` 与独立审核记录；p4-real-validation 独占 `tools/CSharp.UI.ImeHostCheck` 与真实执行证据、dotnet/可见运行。只读检查完成的可达判据：

- 移动/resize/DPI/最小化失效：`OpenTkHost.cs:586-916` 回调 → `IWindowsInputBridge.NotifyGeometryInvalidated` → `TsfInputBridge.cs:491-505`（清文本几何 + `RefreshSurface` 重发独立显示表面 + pending）；最小化 → `NotifySurfaceUnavailable`（`:508-520`，表面零矩形 + S_OK）。判据：invalidate 后下一安全布局真实重发，不残留旧屏幕坐标。
- 失焦/失活：`OpenTkInputAdapter.HandleFocusChanged` → `DeferredInputAction.ForFocus` → `UiRuntime.NotifyWindowActivation` → `InputRouter.CancelTextInput` → `EndSession` → `RetireTsfLocked` → `TsfInputBridge.Dispose`（`ClearPresentation` → hook/coordinator → `TsfHostSession.Dispose`：BlurCore → UIElement sink unadvise+Dispose → edit-sink unadvise → owner/store → Deactivate → `Pop(TF_POPF_ALL)` → COM/租约）。重新激活 `ResumeTextInput` 按新 epoch 重建。判据：无悬空 UIElement/组合事实落到新会话，激活后 IME 输入可恢复。
- 外改/禁用/卸载：`TextField` 取消接缝 → `CancelCompositionForHostAction` → `RequestCancel`（`TsfInputBridge.cs:600-652`）→ owner 意图在安全阶段 `TerminateComposition`（真实原生终止优先，可重试失败保留意图）。判据：组合真实取消、无迟到 End/完成编辑写回绑定、setter 无额外写。
- 待办唤醒：`HasPendingSafeWork = _layoutChangePending || Owner.HasPendingSafeWork`（`TsfInputBridge.cs:180-181`）；宿主 `RequestDeferredInputRecoveryWake`（`OpenTkHost.cs:508-519`）仅在真实待办时单次唤醒。

已确认待真实观察的产品风险（未定为缺陷）：

1. `DrawCandidates` 帧内因身份失效置 `_layoutChangePending` 后，帧末无额外 `RequestDeferredInputRecoveryWake`；空闲宿主可能等到下一事件才 drain（独立审核 §2.1 边界，P4 场景观察）。
2. 窗口级失活（TextField 仍持逻辑焦点）无单独 `UIElementSink.Clear` 路径，依赖 `CancelTextInput → EndSession → bridge.Dispose` 完成 UIElement 撤销。
3. 工具默认矩阵无真实窗口级失活（alt-tab 类）、DPI 变化、横向 `ScrollX`、禁用/卸载场景；这些项不可虚构通过，须以工具证据或明确环境边界收口。
4. 英文普通输入（本轮执行判据，沿用既有“自有线程选择已启用 profile 并恢复”范围；非用户原话或新授权）：不重跑已知无效的微软拼音 IMM 模式位/注入 Shift 路径。先以本轮 GetProfiles/GetActiveProfile 确证存在已启用英文键盘 profile（如 en-US `HKL=0x04090409`/`TF_PROFILETYPE_KEYBOARDLAYOUT`），再 FORPROCESS 临时选择并恢复后以真实扫描码 `abc` 验证 TSF 路径普通输入，记录为“既有英文 profile 普通输入”，不声称微软拼音内部英文模式；无已启用英文 profile 时如实记为环境边界，不虚构。

## 检查和退出条件

每批先构建再直接运行测试程序集，避免已知 dotnet test 挂起；保留命令、退出码、计数及失败原文。Debug/Release 的目标测试与相关核心/宿主回归必须通过。真实候选证据保留窗口裁剪截图与事件日志，证明组合身份、零预编辑写入及唯一提交；整屏私人内容不放交付证据。

TI-07 只有 P1～P4 全部完成且 Codex 独立审核通过才勾选。最终交付另写带日期记录和同名前缀 evidence 目录；隔离原型通过不代替生产通过。有限实验失败即写明原因、返修具体缺陷，不重复无依据刷新变体。
