# TI-08 设计与差距分析：跨平台能力查询与兼容回退

日期：2026-10-22（阶段一：只读研究 + 最小方案，未经主审批准不动产品码）
任务：[`docs/archive/2026-10-23-text-input-editing-tasks/08-platform-capabilities-and-fallback.md`](../archive/2026-10-23-text-input-editing-tasks/08-platform-capabilities-and-fallback.md)
前置基线：HEAD `30b15cb`（TI-07 P4 已交付；默认 IMM、`UseTsfInputBridge` 内部开关不变）

---

## 0. 结论一句话

核心契约层（`TextInputCapabilities` / `ITextInputService` / `NoTextInputService` / `TextInputForwarder`）和 Windows 桥（IMM 默认 + 惰性 TSF）在 TI-02–TI-07 已交付且基本满足 TI-08；**真实差距集中在三处**：

1. **非 Windows 宿主**目前把 `TextInputService` 置 `null`（`OpenTkHost.cs:166`），能力报告只能落到 `NoTextInputService.CommittedTextOnly`——没有平台化实例、没有 X11/Wayland 分级、没有"平台服务存在 vs 暂无焦点"可观察信号。
2. **`TextInputCapabilities` 是分项 bool 位**（committed / composition / anchor），TI-08 要求的"分项能力查询"语义已具备；缺的是**非 Windows 的分级 Reason**（X11 字符可达但无组合 vs Wayland 普通 xkb 字符可达但无 text-input 协议），以及"不支持组合/候选锚点"与"暂时失焦"的显式区分证据。
3. **诊断/离屏可观察**：`UiHostContext` 公开面没有 `TextInputBackendReason`（`OpenTkHost.TextInputBackendReason` 是 internal 字段且**从未被 TSF 回退路径更新**，恒为 `"默认 IMM 输入桥。"`——TSF 实际原因存在 `router.BackendReason` 里）；`UiRuntime.Diagnostics` 已是有界公共 API 可复用。

## 1. 现状清单（已核对的源代码证据）

| 项 | 位置 | 状态 |
|---|---|---|
| 分项能力记录 | `src/CSharp.UI/Editing/TextInputServiceContracts.cs:248-301` | ✅ `TextInputCapabilities{CommittedText,Composition,CandidateAnchor,Reason}`，`None/CommittedTextOnly/Full` 三个静态实例；构造期强制 composition∨anchor ⇒ committed |
| 空实现服务 | `src/CSharp.UI/Editing/TextInputFallback.cs:18-54` | ✅ `NoTextInputService`：CommittedTextOnly，StartSession→false，ApplyComposition→Rejected(Handled=false) 不重投 |
| 转发/回退 | `src/CSharp.UI/Editing/TextInputFallback.cs:76-139` | ⚠️ `TextInputForwarder`：null→NoTextInputService、无 composition→Rejected 不重投、`DispatchCommittedText`→既有通道均正确；**`ApplyComposition` 内 `service.Capabilities` 读点（`:123`）无 catch（§5b）** |
| 会话接线 | `src/CSharp.UI/Editing/TextInputServiceSession.cs` | ⚠️ epoch 租约/先失效后通知已交付；StartSession/EndSession/RequestCancel/PublishGeometry 异常收敛有界——**但 `Capabilities` getter 读取（`:124`）无 catch，穿透 SetFocus 零诊断（§5b 已证实）** |
| Windows 能力 | `src/CSharp.UI.OpenTK/Hosting/WindowsImeBridge.cs:364-385`、`WindowsTsf/WindowsTextInputRouter.cs:284-328` | ✅ 如实分级：未子类化→CommittedTextOnly；有 HIMC→Full；TSF 惰性握手保留 composition 位 |
| 已提交文本通道 | `src/CSharp.UI.OpenTK/Hosting/OpenTkInputAdapter.cs:222-232,550-557` | ✅ GLFW `TextInput`→`DispatchTextInput`，控制字符已过滤，所有平台共用 |
| 快捷键平台分流 | `src/CSharp.UI/Editing/TextEditHistory.cs:55-109` | ✅ `TextEditCommandResolver`：macOS=Meta(Command)，Win/Linux=Ctrl；foreign primary 拒绝；`Alt` 位拒绝（AltGr=Ctrl+Alt 不误触发）；已有 `TextFieldUndoRedoTests.cs:1209-1291` 三平台注入矩阵 |
| Esc/关闭快捷键 | `src/CSharp.UI.OpenTK/Hosting/OpenTkInputAdapter.cs:565-575` | ✅ **已裁定（主审）**：TI-08 只保留/验证平台编辑快捷键与 AltGr 字符；宿主退出键 `IsCommandPressed=Control∥Meta`（`InputEvents.cs:193`，macOS 上 Ctrl+Q 与 Cmd+Q 均可退出）如实记录为现状约定，**不改**、不计阻塞 |
| 非 Windows 服务 | `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs:164-167` | ❌ `else { _inputBridge = _ime; }` = null；`UiRuntimeServices.TextInputService=null` → 运行时 `EffectiveService` 回退 `NoTextInputService` |
| 原生库隔离 | `OpenTkHost.cs:114-167` 全部分支 | ✅ Windows 桥/router/overlay 只在 `OperatingSystem.IsWindows()` 内构造；`WindowsImeBridge.GetWindowHandle:175` 自带平台早退；`WindowsWheelScrollSettingsService.Read:33` 非 Windows 短路不 P/Invoke |
| X11/Wayland 判定 | — | ❌ 源码中无 `GLFW.GetPlatform()` 调用；GLFW 3.4 managed API `GLFW.GetPlatform()/PlatformSupported` 可用（`OpenTK.Windowing.GraphicsLibraryFramework.xml:2387+`），Wayland 需 `OPENTK_4_USE_WAYLAND=1` |
| 可观察诊断 | `UiRuntime.Diagnostics`（public，`UiRuntime.cs:162,779-787`，有界 512 条）；`router.BackendReason`（internal） | ⚠️ `UiHostContext` 无公开文本输入诊断面；`OpenTkHost._textInputBackendReason` 字段（`OpenTkHost.cs:55`）**写死初始值、无 setter 消费**（见 §3） |
| 既有测试 | `tests/CSharp.UI.Tests/TextInputServiceContractTests.cs`（13+ cases）、`tests/CSharp.UI.OpenTK.Tests/RouterFallbackTests.cs`、`TextFieldUndoRedoTests.cs:1209+` | ✅ 能力一致性、无能力拒绝不重投、null→CommittedTextOnly、committed 文本到达、三平台快捷键矩阵均已覆盖 |

## 2. TI-08 检查项逐项差距

| 检查项（任务文件 25-34 行） | 差距 |
|---|---|
| 分项能力查询 + 默认服务 + 诊断 | 分项位已存在；**非 Windows 无平台化服务实例**；能力 Reason 缺分级（X11 vs Wayland）；`NoTextInputService.CommittedTextOnly` 的 Reason 是通用文案不分平台 |
| 保留 macOS/Linux 原字符/选区/剪贴板/平台快捷键 | GLFW char 通道与 `GlfwClipboardService` 平台无关，现状保留；快捷键分流已有；**需补文档级声明 + 测试证据**而非新代码 |
| 不支持组合/候选锚点 vs 暂时失焦区分 | 语义已隐式存在（Capabilities 静态位 vs `ActiveClient==null`）；**缺一个"committed-only 但服务真实存在"的非 Windows 服务实例**来让该区分可观察——目前非 Windows 直接是 null→NoTextInputService，无法区分"宿主根本没有服务"与"平台服务存在但只有已提交文本" |
| 失败可控不反复唤醒 | ✅ 现有：`ReportServiceFailure` 收敛异常为诊断；`FlushPendingNotifications` 有界；`OrderedDeliveries` 单轮预算 64 |
| 非 Windows 不触发 Windows 原生库 | ✅ 现有；**建议补一个测试断言**（构造 `OpenTkHost` 或至少验证非 Windows 路径不触碰 `WindowsImeBridge`/`WindowsTextInputRouter` 类型）——**但真机 macOS/Linux 不可验证，只能 CPU 级模拟** |
| X11/Wayland 分级诚实记录 | ❌ 无运行时分级；TI-01 矩阵（`docs/agents-notebook/2026-10-17-ime-platform-decision.md:170-180`）有源码级结论：X11 有 XIM（GLFW 内部消费）committed=yes，Wayland 无 text-input 协议 committed 按 GLFW char 回调存在性而定——**主审已指出不能把 Wayland 写成完全无字符**：GLFW Wayland 后端有 xkb compose→char 回调通道，committed 文本可达；缺的是 IME 协议级组合/候选 |
| macOS Command vs Ctrl、AltGr、普通已提交文本不丢 | ✅ 编辑命令路径已有；**AltGr 需在 Wayland/X11 字符通道再确认**（GLFW char 回调传 Unicode 码点，AltGr 产生的字符不走 KeyEvent 修饰键路径，与普通字符同通道，不丢） |
| 编译/模拟/离屏/真机分开标注 | ❌ 文档任务，本轮交付记录需分级标注：macOS/Linux 只能"编译/源码级推断/CPU 模拟"，真机 IME 行为 UNVERIFIED |

## 3. 发现的额外问题（需主审定夺）

**`_textInputBackendReason` 死字段**：`OpenTkHost.cs:55` 初始化后无赋值点，`TextInputBackendReason` 属性（internal）永远返回 `"默认 IMM 输入桥。"`；而 `UiApplicationOptions.UseTsfInputBridge` 的 XML 注释（`UiApplication.cs:160`）承诺"原因记录在 `UiHostContext.TextInputBackendReason`"——该属性在 `UiHostContext` 上**并不存在**（注释指向不存在的 API）。TSF 真实原因只存在于 `router.BackendReason`（internal）。TI-08 范围内最小修正是让宿主 `TextInputBackendReason` 从 `router?.BackendReason`（TSF 启用时）或 IMM 桥状态读取，或移除死字段并修正注释——属"可观察诊断"检查项的直接覆盖。

## 4. 最小方案（提案）

### 4.1 非 Windows 平台服务：复用 `NoTextInputService` + 最小不可变能力配置

**定稿（按主审两轮意见收敛）**：不新建服务类型、不新建平行生命周期、不新增公开 API。

- 主审驳回点 1：`ITextInputService.StartSession` 契约（`TextInputServiceContracts.cs:396-398`）是“开始**并绑定**客户端”；返回 true 必须真实接受/绑定，不允许“返回 true 但 ActiveClient 恒 null”的虚报成功，也不能以“框架内置 caller（`TextInputServiceSession.Start:123-127` 先查 `Capabilities.Composition`）实际不调用”作为理由。
- 主审驳回点 2：同理，`StartSession→false` 的正确依据是“本服务**没有会话绑定机制**”（`NoTextInputService` 既有语义：`StartSession:34-38` → false、`ActiveClient` 恒 null、`ApplyComposition` → Rejected 不重投），而不是“框架不会调用它”。`CommittedText` 能力指的是独立的 GLFW 字符通道，不要求组合服务接受会话。
- 因此：直接复用已审核的 `NoTextInputService`，只让 `Capabilities` 可按平台注入——定稿为 `internal NoTextInputService(string reason)`（构造期 `new TextInputCapabilities(committedText:true, reason:reason)`，`ArgumentException.ThrowIfNullOrWhiteSpace` 校验；committed 恒 true/composition、anchor 恒 false——只注入 Reason，不透传任意能力组合），`Instance` 保持 `CommittedTextOnly` 共享单例不变，全部方法零改动。

平台分级在**宿主侧**构造 `TextInputCapabilities`（committed 恒 true；composition/anchor 恒 false；Reason 分级文案）：

- macOS → “macOS：GLFW 的 NSTextInputClient 实现把 marked text 消费在视图内部，公共 API 无组合/候选暴露；已提交字符经 char 回调可用。”
- Linux/X11 → “Linux/X11：GLFW 内部使用 XIM（预编辑在 GLFW 内消费）；已提交字符经 char 回调可用。”
- Linux/Wayland → “Linux/Wayland：GLFW 3.4 Wayland 后端无 text-input 协议；普通字符经 xkb/char 回调可达，IME 协议级组合/候选缺失。”
- 其他/查询失败 → 通用 committed-only Reason（只声明已提交字符通道，不外推）。

**平台判定（定稿，按主审收敛）**：Linux 走真实 GLFW API `GLFW.GetPlatform()`（OpenTK 4.9.4 已封装 `Platform.X11/Wayland/Null`），**不**读 `WAYLAND_DISPLAY`/`XDG_SESSION_TYPE` 猜测——env 与 GLFW 实际选择的后端可能不一致。<b>不注入 `Func`</b>：分两级——`internal static string DescribeCommittedOnlyReason(Platform)` 纯映射（Cocoa/X11/Wayland/unknown 四档，throwaway/测试可经 InternalsVisibleTo 传任意档位驱动同一真实分支）；`internal static string DescribeCurrentPlatformReason()`（macOS→Cocoa、Linux→真实 `GLFW.GetPlatform()`、其余/异常→unknown，`catch` 带 `when (ex is not OutOfMemoryException)`）。生产只调后者；调用时机在窗口创建后 GLFW 已初始化。

### 4.2 `OpenTkHost` 接线（最小 diff）

```csharp
// else 分支（原 _inputBridge = _ime = null）
else
{
    _platformTextInput = new NoTextInputService(DescribeCurrentPlatformReason());
    // _inputBridge 保持 null（IWindowsInputBridge 是 Windows 专属契约，不混用）
}
// UiRuntimeServices.TextInputService：
TextInputService = (ITextInputService?)_inputBridge ?? _platformTextInput,
```

- 新字段 `_platformTextInput`（`ITextInputService?`）；`AttachRuntime`/`Dispose`/`NotifyGeometryInvalidated`/`UpdatePointerTransform` 只在 `IWindowsInputBridge` 上存在，非 Windows 服务无这些方法，现有调用点不动。
- 能力断言走既有公共入口：测试经 `UiRuntime.Services.TextInputService` 或 `TextInputForwarder.EffectiveService` 读取 `Capabilities`/`Reason`（与 §4.5 应用查询链同一路径）；平台 Reason 分支本身可由 `internal static DescribeCommittedOnlyReason(Platform)` 直接驱动（smoke 经 InternalsVisibleTo），<b>不</b>为断言新增 internal getter。
- `TextInputBackendReason` 死字段修正：**已删 `_textInputBackendReason` 死字段**；getter 改为 `_router?.BackendReason ?? _platformTextInput?.Capabilities.Reason ?? "默认 IMM 输入桥。"`（Windows 走 router 动态原因；非 Windows 走真实平台服务 Reason；无服务时兜底默认文案）。修 `UiApplication.cs:160` 注释中不存在的 `UiHostContext.TextInputBackendReason` 引用。

### 4.3 不改动项（明确边界）

- 不新增公开 API 类型；`NoTextInputService` 只加 internal ctor。
- 不改 `TextInputCapabilities` 记录结构；不改 `IsCommandPressed`（主审已裁定：退出键现状记录，见 §5.1）。
- 不实现任何 macOS/Linux 原生 IME、不碰 GLFW delegate/method swizzle。
- 不改 TI-01 矩阵历史文档；X11/Wayland 分级只写入本计划与后续实施记录。
- 不新增重试/telemetry/抽象层。

### 4.4 测试（全部 CPU 级，无窗口）

`tests/CSharp.UI.OpenTK.Tests`（新文件或并入现有）+ `tests/CSharp.UI.Tests`：

1. `new NoTextInputService(capabilities)`：Capabilities 位与 Reason 透传；StartSession→false、ApplyComposition→Rejected(Handled=false 不重投)、ActiveClient 恒 null（与 `Instance` 同语义）。
2. 平台判定注入：`Func<Platform>` 返回 X11/Wayland/Null + macOS/Windows 分支 → Reason 文本分别含对应平台关键词，committed 恒 true。
3. 非 Windows 加载隔离：`CommittedTextOnly` 平台服务构造路径不引用 `WindowsTsf`/`WindowsImeBridge` 类型（源码级断言 + 现有 `CommittedTextFallback_StillReachesTheFocusedTextField` 已覆盖文本到达）。
4. `TextInputBackendReason` 转发断言（无 router → 默认文案）。
5. <b>§5b 回归（消费者行为，非源码形状）</b>：注入 `Capabilities` getter 抛错的服务 → `SetFocus` 不穿透异常、committed 字符仍写绑定、`Runtime.Diagnostics` 有界增长（≤上限且非每帧重复）；`TextInputForwarder.ApplyComposition` 对抛错 getter 返回 `Rejected`（不泄漏异常、不重投）；`WindowsTextInputRouter.Capabilities` 子桥抛错时如实回退到下一档而非穿透。

### 4.5 应用可见能力查询（复用现有公共入口，不新增 API）

主审审核点核对结果——公共查询链已存在，缺口只在非 Windows 注入了 null：

- `UiHostContext.Runtime`（public）→ `UiRuntime.Services`（public `UiRuntimeServices` 记录）→ `TextInputService`（public `ITextInputService?`，`UiRuntime.cs:45`）→ `Capabilities`（public `TextInputCapabilities`，含 CommittedText/Composition/CandidateAnchor/Reason）。真实应用今天就能写：
  `TextInputForwarder.EffectiveService(context.Runtime.Services.TextInputService).Capabilities`
  （`TextInputForwarder.EffectiveService` 是 public 静态，`TextInputFallback.cs:79`，把 null 归一化为 `NoTextInputService.Instance`；`tools/CSharp.UI.TsfHostBridgeCheck/Program.cs:158` 已按此路径读取）。
- **真实缺口**：非 Windows 宿主注入 null → 应用查询只得通用 `CommittedTextOnly` Reason，看不到 X11/Wayland/macOS 分级。§4.2 接线 `_platformTextInput` 后，同一公共路径返回平台化 Reason——能力查询不需要也不新增任何 getter（internal 或 public）。
- **能力不随焦点变化**：`NoTextInputService`（含新 ctor 实例）的 Capabilities 为构造期常量；Windows 桥 Capabilities 取决于子类化/HIMC 而非焦点——“暂时失焦”只反映为 `ActiveClient==null`/会话缺席，不分级降级，满足区分要求。
- **失败状态 Reason 可观测且不反复唤醒**：非 Windows 的 Reason 在构造期一次求值（含 GLFW.GetPlatform 失败→unknown 文案）；Windows 桥失败态（未子类化/无 HIMC/TSF 退役）经 `Capabilities` getter 每次真实回读。读 `Capabilities` 是纯属性访问，不发诊断、不唤醒；服务操作异常由既有 `TextInputServiceSession.ReportServiceFailure` 写入有界 `UiRuntime.Diagnostics`（512 条上限），不每帧重复。
- `docs/guide/platform-notes.md` 更新时如实写明该查询入口与“null → EffectiveService”约定，不虚构新 API。

## 5. 已裁定的开放问题（主审结论记录）

1. **Esc/关闭快捷键的 Command vs Ctrl**：<b>裁定 = 不改。</b>TI-08 验收口径只覆盖平台编辑快捷键（Undo/Redo/Cut/Copy/Paste/SelectAll）与 AltGr 字符；宿主退出键 `IsCommandPressed=Control∥Meta`（macOS 上 Ctrl+Q 与 Cmd+Q 均可退出，`OpenTkInputAdapter.cs:571` / `InputEvents.cs:193`）作为现状约定如实记录，不算本轮新增缺陷，不扩改 `IsCommandPressed`。
2. **GLFW.GetPlatform 可用性**：OpenTK 4.9.4 各 TFM 是否都暴露该 API 需编译验证（XML 文档确认存在）；若不可用，降级为 unknown。
3. **文档交付物**：`docs/guide/platform-notes.md` 文本输入边界节需补 macOS/Linux committed-only 声明 + X11/Wayland 分级 + “未真机验证”标注（当前 `platform-notes.md:84-106` 只声明“默认发布支持已提交文本”未分级）。

### §5.1b AltGr+Q 退出快捷键缺陷（主审第二轮反例，已修）

**反例**：`OpenTkInputAdapter.DeliverCapturedCore` 的 Q 退出分支（`OpenTkInputAdapter.cs:571`）`IsCommandPressed`（`Control∥Meta`）未排除 Alt；常见 AltGr=Ctrl+Alt 布局按下 AltGr+Q 时 key-down 携带 Control 位 → `IsCommandPressed` 为真 → 误 `RequestExit()`，宿主退出但 '@' 字符已由 GLFW char 通道落地。编辑命令的 Ctrl+Alt 拒绝规则（`TextEditCommandResolver`）不覆盖此宿主退出路径（不同代码路径）。

**修复**：Q 退出分支加 `!keyEvent.Modifiers.HasFlag(KeyModifiers.Alt)`——Alt 位存在时不算命令键；Ctrl/Meta 退出与 Esc 退出边界不变。真实生产 adapter+UiRuntime 复现：修复前 AltGr+Q exits=1/text='@' → 修复后 exits=0/text='@'、Ctrl+Q 仍 exits=1（证据 `docs/agents-notebook/ti08-capabilities-repro/altgr-q-exit.txt`）。

**范围裁定**：§5.1 的"不改退出键"裁定只针对 macOS CtrlQ-vs-CmdQ 分流，不豁免 AltGr 误退（AltGr 是普通字符输入通道，属 TI-08 committed 不丢验收）；`IsCommandPressed` 语义本身不改（`Control∥Meta` 保持），仅 Q 退出消费点加 Alt 排除。

## 5b. 已证实缺陷：`Capabilities` getter 无异常边界（最小运行复现，2026-10-22）

**复现**（throwaway `UiRuntime` + 真实 `TextField`，注入 `Capabilities` getter 抛 `InvalidOperationException` 的服务）：

```
SetFocus THREW InvalidOperationException: caps boom
after throw: node1.IsFocused=True FocusedNode-same=True binding='ab'
committed-after-throw: binding='Zab'          ← 已提交文本仍落地
focus-toggle throws=3/6 diagnostics=0          ← 每次聚焦都抛，零诊断
control: binding='Zab' (expect abZ) focused=True
```

**事实边界**：异常确实穿出 `UiRuntime.SetFocus`（`TextInputServiceSession.cs:124` 的 `service.Capabilities.Composition` 无 try/catch；`InputRouter`/`UiRuntime` 全链路无 catch）。焦点已设置（`IsFocused=true` 在 `OnFocusChanged` 之前赋值），**已提交字符不受影响仍正常编辑**；但**每次获取焦点都抛且无任何诊断记录**——真实宿主上经 pointer→`SetFocus` 每次点击产生一条 adapter 级 `ReportDiagnostic`，属“失败可控但不反复唤醒”的边界缺口。

**同类边界（framework 内全部 `Capabilities` 读点）**：

| 位置 | 现状 | 修正 |
|---|---|---|
| `TextInputServiceSession.cs:124` `service.Capabilities.Composition` | ❌ 无 catch，穿透 SetFocus | 包 `catch (Exception ex) when (ex is not OutOfMemoryException)` → `ReportServiceFailure("Capabilities", ex)`，`canCompose=false`，`return false`（不吞 OOM、不造 Full、不重放） |
| `TextInputFallback.cs:123` `service.Capabilities.Composition`（`ApplyComposition` 内） | ❌ 无 catch，穿透到批次调用方 | 同样收敛为 `Rejected` + 脱敏 reason（组合批次不得因 getter 抛错而泄漏异常） |
| `WindowsTextInputRouter.cs:308,311` `tsf.Capabilities` / `imm?.Capabilities` | ⚠️ router 自身是 `Capabilities` 实现；子桥 getter 若抛错会经本 getter 继续穿透 | 包局部 catch → 报告<b>诚实保守能力快照</b>（该子桥档位视为缺失）+ 脱敏 Reason；**不**新建/释放/重试 backend、不假称已切换 IMM、不与仍活动 TSF 形成双 writer（遵守 `:271-281` 既有 doc：禁止读能力建/切后端） |

**修正原则**（与既有 `ReportServiceFailure` 一致：StartSession/EndSession/RequestCancel/PublishGeometry 在 `:155-161,259-267,293-300,477-484` 已同样收敛）：能力查询失败 → `canCompose=false` + 一条有界 `ReportDiagnostic`（512 上限），**不**自动重试、**不**每帧重复（Capabilities 只在会话开始/批次应用时读取，非逐帧轮询；失败态 Reason 由服务自身报告）。已提交文本路径 `DispatchTextInput` 不经 `Capabilities`，天然不受影响——回归只需断言“注入失败服务后 committed 字符仍写绑定、焦点正常、诊断计数有界”。

## 6. 验收命令（提案）

```bash
dotnet build CSharp.UI.slnx -c Debug && dotnet build CSharp.UI.slnx -c Release
dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll        # 核心契约
dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.dll  # 宿主/平台服务
# Release 同上
```

真实 smoke（主审安排）：一个 sample 启动后，经公共路径 `context.Runtime.Services.TextInputService`（或 `TextInputForwarder.EffectiveService`）读 `.Capabilities`（Windows 上 IMM/TSF 分级，非 Windows 上 committed-only 平台分级 Reason）。macOS/Linux 真机验证标 UNVERIFIED。

## 7. 文件改动范围（提案）

| 文件 | 改动 |
|---|---|
| `src/CSharp.UI/Editing/TextInputFallback.cs` | `NoTextInputService` 新增 internal ctor；`TextInputForwarder.ApplyComposition` 的 `Capabilities` 读点加异常收敛（§5b） |
| `src/CSharp.UI/Editing/TextInputServiceSession.cs` | `Start` 的 `Capabilities.Composition` 读点加 `catch→ReportServiceFailure→canCompose=false`（§5b） |
| `src/CSharp.UI.OpenTK/WindowsTsf/WindowsTextInputRouter.cs` | `Capabilities` getter 内 `tsf.Capabilities`/`imm?.Capabilities` 读点局部 catch → 该档如实回退（§5b） |
| `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs` | else 分支实例化分级 `NoTextInputService`、新字段、`TextInputBackendReason` 转发修正、`GLFW.GetPlatform()` 判定 |
| `src/CSharp.UI.OpenTK/UiApplication.cs` | 修正 `UseTsfInputBridge` 注释中不存在的 `UiHostContext.TextInputBackendReason` 引用 |
| `tests/CSharp.UI.OpenTK.Tests/`（新测试文件或并入现有）| 平台分级/拒绝语义/Reason 断言 |
| `docs/guide/platform-notes.md` | 文本输入边界节补分级与未验证标注 |
| `docs/archive/2026-10-23-text-input-editing-tasks/08-platform-capabilities-and-fallback.md` | 追加实施记录段（不删用户已有项） |

非目标：不改 `NoTextInputService` 既有方法语义、不碰 Windows 桥、不碰 TextField/快捷键解析、不碰 `IsCommandPressed`（主审已裁定不改，见 §5.1）。
