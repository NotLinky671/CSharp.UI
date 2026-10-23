# TI-07 P3b 独立审核（布局通知代次 / 真实候选生命周期 / 几何失效边界）

日期：2026-10-22
状态：P3b 范围内验收完成（Debug/Release 指定回归、真实可见基线、真实滚动/裁出/恢复均通过）；**TI-07 整体与 P4 未完成**。Release 原始日志与工具 selftest 已落盘；P3b 交付记录见[2026-10-22-ti07-p3b-delivery.md](../../delivery/2026-10-22-ti07-p3b-delivery.md)。
**本记录确认 P3b 范围验收完成，不声明 TI-07/P4 通过**；其余真实微软拼音场景（移动/DPI、失活、英文/Unicode、Enter 候选就绪重跑等）仍由 real-ime 执行。

依据：[P3b 设计](../../plan/2026-10-21-ti07-p3b-host-integration-design.md)、[宿主接线记录](2026-10-21-ti07-p3b-host-integration.md)、[真实首跑证据](2026-10-21-ti07-p3b-real-ime-evidence/nihao-second/ime-host-check.jsonl)、[可见基线证据](2026-10-22-ti07-p3b-real-ime-evidence/nihao-visible-baseline/ime-host-check.jsonl)。

## 1. 结论先行：布局通知的 sink generation

**问题**：`AcpTextStore.NotifyLayoutChange` 的延迟项是否可能把旧连接的布局通知送给替换后的 sink（A → Unadvise → B → flush）。

**结论：不可达，不构成确认缺陷；但原“按 sink generation”表述与实现不符，已在代码注释与设计/任务文档统一为准确机制。**

行号（`src/CSharp.UI.OpenTK/WindowsTsf/AcpTextStore.cs`）：

- `DeferredNotification(Kind, Flags, Change)` 不含代次：121-122；layout 入队只写 kind：1411。
- 延迟发送 `SendLayoutChangeLocked` 在发送时读取**当前** `_sink`/`_sinkMask`：1421-1443；flush 分派：1542-1553。
- `UnadviseSink` 在移除连接时 `_sink=null`（642）、`_sinkGeneration++`（645）、**`_deferred.Clear()`（647）**；`Dispose` 同样清空（1991）。
- `AdviseSink` 在已有连接时直接返回 `CONNECT_E_ADVISELIMIT`（609），新连接只能在旧连接撤销、队列已清空之后建立（615）。

因此队列非空 ⇒ 连接自入队以来未变；flush 只可能发送给当前连接，A 的排队项既不回给 A，也不会给 B。同一 identity 的 mask 更新不换代（610-616），发送仍按新 mask 门控。既有 text/selection 通知路径未改动，未扩大范围。

**协议审计测试**（真实 `AcpTextStore` 协议对象 + 记录 sink，非 mock 几何）：

- `tests/CSharp.UI.OpenTK.Tests/AcpTextStoreProtocolTests.cs:830-855`：持锁布局入队 → 锁外 flush 恰好 1 次，重复 flush 不重发（证明队列项真实存在）。
- 同文件 `:856-888`：持锁入队 → Unadvise A → Advise B → flush：A=0、B=0，随后 B 的新通知仍为 1（证明不泄漏、不是永久禁发）。
- 同文件 `:889-902`：mask 不含 `TS_AS_LAYOUT_CHANGE` 时返回 false 且不排队。

执行：real-ime 于 2026-10-22 Debug 独立运行 `AcpTextStoreProtocolTests` = 75/75（原 72 + 新增 3）、`TsfLayoutNotificationRegressionTests` = 1/1，均 Failed 0 / exit 0。原始 stdout 摘要待其持久化（会话转录，非原始流），本记录不伪造原文。结论与静态审计一致：A→B 不泄漏。

消费者侧：`TsfInputBridge.NotifyLayoutChangedIfPending`（`TsfInputBridge.cs:794-816`）在 store 拒绝时也清 pending（806-807），避免“无 mask 时 pending 永真 → 唤醒自激”。真实 TSF 的 store mask 实测为 `0x1F`（含 layout；`docs/return/2026-10-21-ti07-production-p2b-owner-review-evidence/CSharp.UI.OpenTK.Tests-Debug.log:33`，Release 同值），本路径可发送。

文档统一：

- `AcpTextStore.cs:1386-1391`（NotifyLayoutChange remarks）、`TsfInputBridge.cs:640-643`。
- `docs/plan/2026-10-21-ti07-p3b-host-integration-design.md:245,271`。
- `docs/task/07-windows-ime-and-host-routing.md:12`。

## 2. 本轮新改动的读码结论

### 2.1 Unavailable 仅“可见→不可见”排 pending（3 处）

`TsfInputBridge.cs:677,700,787` 均使用 `markLayoutPending: _session.GeometryProvider.Current is not null`。`ClearGeometry` 先清 provider（817-826），因此同一 Unavailable 状态的后续安全阶段不会再置 pending；可见→不可见、窗口失效（`NotifyGeometryInvalidated` 469-483）仍各排一次。自激路径被消除，且未吞掉真实转移。

边界（非缺陷，未改）：`DrawCandidates` 在**帧内**因身份失效置 pending 时，帧后没有额外的 `RequestDeferredInputRecoveryWake`；宿主在无新事件时会等到下一次事件才 drain。该分支是“发布后到绘制前又失效”的兜底，正常发布已在 `ProcessPendingWork` 末尾触发一次唤醒（`OpenTkHost.cs:508-519`）。空闲唤醒的真实覆盖由 P4 场景补，不由本测试声称。

### 2.2 几何身份与 stale

- 发布：`TsfInputBridge.cs:677-739`；epoch/核心版本/char 文档版本、预编辑、**完整显示串**、store 文本、LayoutSerial、ScrollX、PixelScale 全部核对，失败即清缓存。
- 帧内复核：`:744-790`；再比对当前 char 快照、LayoutSerial/ScrollX、store 版本/代次、权威预编辑/显示串，任一失效立即清几何（无旧坐标回退）。
- ACP 查询：`AcpGeometryProvider.cs:184-247`（`TryGetTextRect` 只读比较 native 代次/store 版本/完整文本）；文本与显示表面独立缓存（154-180、251-266）；`GetTextExt` 在 store 同一读锁内构造身份（`AcpTextStore.cs:1180-1186`）。
- 未发现新的伪造原点/钳制/旧几何路径。

### 2.3 UIElement sink 生命周期

`TsfUiElementSink.cs`：

- Begin：`_pendingBegins` 按引用登记（171），先退休同 id 旧 Entry（174-179），`GetUIElement` 成功即 `ownedElement` 由 finally 恰好释放一次（193、222-246）；提交点复核 attempt 身份与 `_lifeSerial`（203-212），id 0 无特殊分支。
- Update：全局单调 `_nextUpdateSerial`，旧外层读取不得覆盖新提交（252、270-283、278-281）；`InFlight` 借用期间 End 只标记，借出退出释放（288-296）。
- End/Clear/Dispose：End 只按 id 移除（305-333）；`Clear` 防嵌套且清期间禁止注册（335-368）；`Dispose` 先置 disposed 再 Clear（370-380）。
- 残余风险（COM 语义限制，不可在本层修复）：End 只带 id；若系统在旧 id 的 End 之前就复用同 id 发起新 Begin，迟到 End 会移除新 Entry。真实微软拼音首跑中 id 0 跨组合复用、代次递增，但未观察到乱序；归入 P4 真实场景观察，不作为已确认缺陷。

### 2.4 同帧纹理保护

`UiRuntime.cs:567-577`：`BeginFrame → Emit → FrameOverlay → ToArray`；`OpenTkHost.cs:921-948` 的 `_frameInProgress` 覆盖 BuildFrame/Submit/Swap。候选不额外 BeginFrame。与设计一致。

### 2.5 默认 IMM 回退

`WindowsTextInputRouter.cs:364-411`：`TsfEnabled=false` 直接 `StartImmSession`，不触碰 TSF；TSF 建桥失败/epoch 替换走 `RetireTsfLocked`（650）+ `EnsureImm` 新实例（676），不并行两个写入来源。未发现回退路径的新缺陷。

### 2.6 新增宿主回归测试的审阅

`tests/CSharp.UI.OpenTK.Tests/TsfLayoutNotificationRegressionTests.cs`：

- 用真实 `UiApplication.Run` + 真实 TSF 桥 + 真实 TextField，观察 `LayoutNotificationCount` 与 `GeometryProvider.Current`，不读私有布尔，不 pin 帧号；120 帧上限、5 帧平台期，超时带 phase/frames/count/sinkMask 原文。
- 说明：`baseline` 在 `Started` 内、首次几何发布之后取得，因此 phase 0 的 `+1` 实际是“首次可见发布与 1×1 撤销被 coalesce 的同一次通知”，phase 0 可能在首帧即满足；真正捕捉“每帧重发/漂移”的是 1×1 与恢复后的两个 5 帧平台期。测试逻辑成立，但**不能**观察宿主完全空闲时的唤醒（每帧显式 `RequestRedraw` 自驱动）。空闲唤醒由真实 P4 场景承担。
- 首跑（2026-10-22 Debug）1/1 通过、Failed 0 / exit 0：说明 1×1 撤销与恢复各只产生一次通知，两个 5 帧平台期无漂移，退出时无待处理安全阶段工作。原始 stdout 待 real-ime 持久化。
- 若后续失败（含 Release），优先看错误行中的 `phase/frames/count/baseline/sinkMask/cache`；本文件不修改计数断言、不改回源文本。

### 2.7 真实 scroll 验收的只读组合身份接缝（2026-10-22 追加）

真实 `container-scroll` 需要证明“裁出/恢复前后仍是同一组合”，但原可读面不足：
`TextInputAuthoritativeSnapshot` 没有 epoch，也没有核心 `CompositionId`；几何缓存 `HostGeometryCache.Epoch` 在完全裁出时按设计为 null。

为此新增最小 internal 只读接缝（不扩张公开 `ITextInputClient`，不改变任何写入/布局/通知行为）：

- `src/CSharp.UI/Editing/CompositionCharGeometry.cs:139`：`internal interface ICompositionIdentitySource { int Epoch; int CompositionId; bool IsComposing; }`。
- `src/CSharp.UI/Editing/TextFieldCompositionClient.cs:26,75-79`：由真实客户端实现；`CompositionId` 来自核心 `TextFieldComposition.CompositionId`。
- `src/CSharp.UI.OpenTK/WindowsTsf/TsfInputBridge.cs:216-222`：`internal HostCompositionIdentity? CompositionIdentity`，结构为 `(SessionId, CompositionId, IsComposing)`；`SessionId` 与公开 `ActiveClient.Session.SessionId` 同值，且**不依赖几何缓存**，hidden 时可读。

验收映射（real-ime 工具侧使用，不由本审核改工具）：`small/hidden/restored` 三次读取都必须 `IsComposing==true` 且 `SessionId/CompositionId/CurrentElementId/CurrentGeneration/Preedit` 与 `initial` 相同、`Preedit=="ni'hao"`；`CompositionId` 在 `IsComposing==false` 时可能是最近结束的组合，不能单独当活动身份；提交仍要求 `writes` 恢复前不变、Space 后 `before+"你好"` 且写入恰好 +1。

工具 helper 静态核对（未由本审核编译）：`TsfCandidateDiagnostic` 已含 `CompositionSessionId/CompositionId/SetterWrites/CurrentElementId/CurrentGeneration/Preedit/UiElementAdvised/CandidatePresentationAvailable`，`ToLog` 与构造实参顺序一致，无 `OwnerEpoch` 残留、无占位/NotImplemented；身份取自 `TsfInputBridge.CompositionIdentity`，不再引用不可见的 `TextFieldCompositionClient`；截图前要求 `RenderedFrameCount` 前进并主动 `RequestRedraw`。real-ime 已修首个编译错误（`Query` 方法组改 lambda），构建/运行由其在 dotnet 独占下执行。

**真实首跑（2026-10-22 Debug，已通过）**：`docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p3b-real-ime-evidence/container-scroll/`（jsonl + s7a–s7d + stdout/stderr/exit，exit=0）。`tsf-container-scroll-result`：`scroll_before=0 → small=7 → out=324 → restored=0`；`initial/small/hidden/restored` 均为 `composition_session_id=1, composition_id=0, element_id=0, generation=3, preedit=ni'hao, is_composing=true`，`setter_writes` 全程 0 直到提交；hidden 时 `geometry=unavailable` 且 `composition_preserved_hidden=true`；恢复后几何重新发布；`final_text=你好`、`writes_delta=1`、`commit_exactly_once=true`。
几何/通知计数（同一 log）：`layoutNotifications 6→7→8→9`，只在可见→不可见、恢复等真实转移各 +1；hidden 期间 `cleared=16→24` 但通知不再增长——真实宿主验证了“3 处 Unavailable 守卫”消除了自激。`publish=8, rejected=1`，恢复后 `layout=17`。

## 3. 证据一致性（复核）

- `docs/archive/2026-10-23-completed-reviews/2026-10-21-ti07-p3b-host-integration-evidence/raw-host-smoke.txt`：正式宿主 1/1、exit 0；`raw-release-summary.txt`：Release 101/101 与最终增量 28/28、exit 0。均为执行时 summary 转录（README 已标注原始 stdout 未持久），未把转录当原始流。
- 真实首跑 `nihao-second/ime-host-check.jsonl`：`seq 27` 的 `tsf_candidate` 是真实 TSF 证据（mspinyin profile、flags `0x80000004`、id 0 generation 3、9 个真实候选含“你好”、预编辑 `ni'hao`、写入增量 0、几何 available/scale 1.5/scroll 0）；`seq 30` 空格提交精确“你好”、`writes_delta=1`；`seq 37` exit 0、binding_writes=1。该窗口仅 140 逻辑高（截图 742×266），9 行候选 + 页脚 + 内边距必然超过可用高度，`CandidateOverlay.Draw` 的 `height > maxHeight`（`CandidateOverlay.cs:145-149`）按设计零命令；这不是身份失效，已由下一条高窗复验取代。
- **可见基线（2026-10-22，已证明）** `docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p3b-real-ime-evidence/nihao-visible-baseline/`：640 逻辑高窗口（截图 742×1016，客户区 480×640 @1.5）。`ime-host-check.jsonl` `seq 27` 预编辑 `ni'hao`、`writes_delta=0`、真实 9 候选（你好…）、id 0 / generation 3、flags `0x80000004`、几何 `caret=(55.816,18.667,1.5×18.621) composition=(18,18.667,37.816×18.621); client=(125,231)-(845,1191); scale=1.5; scroll=0; epoch=1; layout=14; storeVersion=6; nativeGeneration=1; viewport=480×640`；`seq 30` 空格提交精确“你好”、`writes_delta=1`；`seq 37` exit 0、binding_writes=1。主代理亲读截图：9 项真实候选、蓝底选中“你好”、第 1 页 → **候选真实可见**，几何身份字段齐全。
- **真实滚动/裁出/恢复（2026-10-22，已通过）** `docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p3b-real-ime-evidence/container-scroll/`：`ime-host-check.jsonl` + `s7a-composing.png`/`s7b-small-scroll.png`/`s7c-scrolled-out.png`/`s7d-restored.png`（742×1016）+ `stdout.txt`/`stderr.txt`（空）/`exit.txt`（0）。`seq29` 全部判据 true；`seq30` pass；`seq36` host_exit 0、final_text 你好、binding_writes 1、failures 空。四次身份读取见 §2.7；hidden 时 `geometry=unavailable`、`Current null`，但组合仍为 `ni'hao`；`layoutNotifications` 只在真实转移递增（6→7→8→9）。
- **真实 Escape/Enter/数字/分段（2026-10-22 Debug，四项均 exit 0；原始 stdout/stderr/exit 已落盘）**：
  - `escape-cancel/`：`escape-cancel-result` text_unchanged=true、writes_delta=0、host_alive=true → 取消不写绑定。
  - `enter-select/`：无候选前置首跑 delta1/raw `nihao`/无泄漏/组合结束。**候选 ready final** `enter-select-candidate-ready-final/`：`candidate_ready_before_enter=true`、raw `nihao`、delta1、no_newline_or_submit=true、composition_ended=true、host_alive=true、`safety_result=pass`、exit 0；`enter_selected_candidate=false`、`enter_outcome=Enter确认原始拼音（本机微软拼音行为）`，按 README 判据不要求 CJK。先前 10s 调度超时与立即读 `IsComposing` 的时序问题均已修复（bounded Query + 有界等待组合结束），失败/重试构建日志保留。
  - `digit-select/`：committed_chunk=`你好`、writes_delta=1、digit_not_written=true、committed_is_cjk=true。
  - `segmented/`：text=`你好世界`、expected 相同、writes_delta=2、two_commits_exact=true（两段各选词一次）。
- 证据阅读注意：`seq 3`（`backend:Imm`、`session_present:false`）与 `seq 15`（`session` 能力 `composition=False anchor=False`）都是 TSF 惰性建立**之前**的快照，不是最终能力；`seq 27` 的 `candidate_presentation_available=true` 与截图才是本轮结论依据。
- 布局通知计数：`container-scroll` 的 `geometry_counters` 已含 `layoutNotifications=6→7→8→9`（真实转移各一次，hidden 重复帧不增长），且 `TsfLayoutNotificationRegressionTests` 已 Debug 1/1（§5）；无需再由旧日志推断。
- `finish` 行的 `geometry_publishes/geometry_revokes` 是工具持有的 **IMM** 桥计数（`tools/CSharp.UI.ImeHostCheck/Program.cs:282-283`），TSF 路径下为 0 属预期，不能据此判断 TSF 未发布；TSF 发布事实以 `seq 27` 的几何字段与截图为准。

## 4. 本轮改动（文档/测试 + 只读诊断接缝）

- 测试：`AcpTextStoreProtocolTests.cs` 新增上述 3 项，Debug 已跑 75/75（72→75），Failed 0 / exit 0（§5）。
- 只读诊断接缝（供真实 scroll 验收，不改行为）：`CompositionCharGeometry.cs:139`、`TextFieldCompositionClient.cs:26,75-79`、`TsfInputBridge.cs:216-222`（见 2.7）。
- 测试跨平台守卫（仅测试）：`TsfHostIntegrationTests.cs:25-29`、`TsfLayoutNotificationRegressionTests.cs:35-39` 在非 Windows 直接 `return`，沿用 `TsfSessionLifetimeTests` 既有模式；Windows 行为不变，最终构建后两类各 1/1 通过。Linux/macOS 未执行，不声称通过。
- 文档/注释：`AcpTextStore.cs:1386-1391,1139-1140`、`TsfInputBridge.cs:5-9,640-643`、`AcpGeometryProvider.cs:30-34,73-76`、`TsfHostSession.cs:7,29-33,931-934`、`WindowsTextInputRouter.cs:4,30-33,322`、`docs/plan/2026-10-21-ti07-p3b-host-integration-design.md:245,271`、`docs/task/07-windows-ime-and-host-routing.md:12`、`docs/guide/platform-notes.md:95`。
- 已移除的过时表述：`AcpGeometryProvider` 的“本批不绑定 / P3 接线前”、`TsfHostSession`/`TsfInputBridge`/`WindowsTextInputRouter` 的“P3a / 候选属 P3b / 不声明 Full”概述、Dispose 顺序自相矛盾的行、`GetACPFromPoint` 的“本批不实现”。
- `platform-notes` 的候选移动/裁出隐藏改为“已实现并接入、真实场景待 P4 正式验收”，未提前宣称通过。
- 清理：删除上一轮 validation 的临时工程 `artifacts/ime-work/p3b-activation-smoke/`（含 bin/obj）；其激活 smoke 的真实日志已保存在 `docs/archive/2026-10-23-completed-reviews/2026-10-21-ti07-p3b-evidence/activate-ex-real-sta.log`（ActivateEx 0x80000004、恰好一次 Deactivate、之后 0x0），未删除任何用户原有 artifact 或证据。

## 5. 已执行（Debug / Release）

2026-10-22 real-ime 独立 Debug 运行（同一构建）：

| 类 | 结果 |
|---|---|
| `AcpTextStoreProtocolTests` | 75/75（原 72 + 新增 3），Failed 0，exit 0 |
| `TsfLayoutNotificationRegressionTests` | 1/1，Failed 0，exit 0 |
| `TsfHostIntegrationTests` | 1/1，Failed 0，exit 0 |
| `TsfUiElementSinkTests` | 18/18，Failed 0，exit 0 |
| `HostAcpGeometryProviderTests` | 5/5，Failed 0，exit 0 |
| `CandidateOverlayRenderingTests` | 4/4（真实 GPU），Failed 0，exit 0 |
| `TsfCancellationTests` | 4/4，Failed 0，exit 0 |
| `TsfSessionLifetimeTests` | 1/1，Failed 0，exit 0 |
| `CandidateSnapshotContractTests` | 38/38，Failed 0，exit 0 |

合计 147 项、Failed 0、各 exit 0。**证据出处**：Debug 轮为 real-ime 会话回报（原始 stdout 未持久，不伪造原文）；Release 轮已按类保存原始 `stdout.txt`/`stderr.txt`/`exit.txt`，见下。

真实工具场景（Debug，原始文件已落盘）：`container-scroll`、`escape-cancel`、`digit-select`、`segmented`、`enter-select`（候选 ready final）各 1/1、exit 0；`default-imm-unicode-direct` 1/1、exit 0（提交 `中😀`、2 次写入；合成字符通道，非 IME 证据）。证据在各同名目录（jsonl/stdout/stderr/exit + scroll 四张 PNG）。`enter-select` 在有候选时仍提交原始拼音 `nihao`，按 README 判据通过并记录本机行为，不要求 CJK。

**Release（最终构建，原始日志，均 exit 0、stderr 空）**：`release-build-final` 0；`release-final-AcpTextStoreProtocolTests` 75/75、`release-final-CandidateSnapshotContractTests` 38/38、`release-final-TsfUiElementSinkTests` 18/18、`release-final-HostAcpGeometryProviderTests` 5/5、`release-final-CandidateOverlayRenderingTests` 4/4、`release-final-TsfCancellationTests` 4/4、`release-final-TsfSessionLifetimeTests` 1/1、`release-final-TsfLayoutNotificationRegressionTests` 1/1、`release-final-TsfHostIntegrationTests` 1/1、`release-final-RouterFallbackTests` 7/7、`release-final-WindowsInputBridgeContractTests` 12/12；合计 **166/166**、Failed 0。加非 Windows 守卫后两个 TSF 测试另以 `release-TsfHostIntegrationTests-final`/`release-TsfLayoutNotificationRegressionTests-final` 复跑 1/1、1/1。`tool-selftest` all pass、exit 0。构建失败/重试日志保留。

Release 启动形式（已逐类执行，原始输出在 §4 所列 `release-*` 目录）：

```
dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj -c Release
tests/CSharp.UI.OpenTK.Tests/bin/Release/net10.0/CSharp.UI.OpenTK.Tests.exe -class <FQCN>
```

运行类：`AcpTextStoreProtocolTests`、`CandidateSnapshotContractTests`、`TsfUiElementSinkTests`、`HostAcpGeometryProviderTests`、`CandidateOverlayRenderingTests`、`TsfCancellationTests`、`TsfSessionLifetimeTests`、`TsfLayoutNotificationRegressionTests`、`TsfHostIntegrationTests`。首次构建失败日志与重试成功日志均保留。

任何失败保留 `phase/frames/count/baseline/sinkMask` 或协议断言原文，由本审核同会话定位；不弱化计数、不改回源文本。

## 6. 未覆盖 / 明确边界

- 已证明（2026-10-22）：真实微软拼音 640 高窗口下 9 项候选可见、预编辑零写、空格唯一精确提交“你好”、exit 0（§3）。
- 已证明（2026-10-22）：真实 `container-scroll` 小滚→完全裁出→恢复：同一 `composition_session_id/composition_id` + 元素 id/generation + `ni'hao`，hidden 时组合保留、几何清空，恢复后重发；setter 0 直到提交；Space 精确“你好”且 +1；exit 0（§2.7/§3）。
- 已证明（2026-10-22 Debug，原始证据已落盘）：Escape 取消零写；数字选词精确“你好”一次；连续分段精确“你好世界”两次；Enter 在候选 ready 下提交原始拼音 `nihao`、delta1、无泄漏、组合结束、host 存活（本机行为，不要求 CJK）。
- 已证明（2026-10-22 Debug）：默认 IMM Unicode 合成字符通道提交 `中😀`、2 次标量写入（`default-imm-unicode-direct`，非 IME 证据）。
- 仍未验证：窗口移动/resize、可用显示器 DPI 变化、横向文本滚动、失焦/失活、普通英文、IME Unicode 路径、断点重排/锚点跟随/最小化恢复/外部更新取消/撤销重做/关闭窗口等其余矩阵场景；这些属 P4，未通过。
- 窗口级失活（TextField 仍持有焦点）不会主动 `TsfUiElementSink.Clear`；现有清理来自 TextField 失焦/epoch 替换/会话释放触发的 bridge 释放。真实 alt-tab 场景下候选是否由 IME End/组合终止收敛，列入 P4 观察，不据此声称已通过。
- 候选鼠标选择未实现；不把 fake/合成候选当作真实输入证据。

## 7. P3b 收口清单（未全部满足前不写交付记录、不宣称通过）

1. **真实 scroll/crop/restore**：`container-scroll` 场景在 `initial/small/hidden/restored` 四次读取中 `IsComposing==true` 且 `CompositionSessionId/CompositionId/CurrentElementId/CurrentGeneration/Preedit` 完全相同（`Preedit=="ni'hao"`）；几何在裁出时清空、恢复后重新发布；滚动期间 setter 写入为 0；Space 后文本恰为 `before+"你好"` 且写入恰好 +1；s7a–s7d 截图与 jsonl 原文保留、exit 0。**✅ 已满足（2026-10-22 Debug，见 §2.7/§3）**。
2. **真实输入其余 P4 矩阵**：Escape 取消、数字/Enter 选词、连续分段、英文/Unicode、失焦/失活、窗口移动/resize、DPI 变化、横向文本滚动等按实际运行结果记录；未覆盖项必须明确列出，不得用实现推断代替。**部分已满足（2026-10-22 Debug）**：escape-cancel、digit-select、segmented、enter-select（候选 ready 后原始拼音、1 写、无泄漏、组合结束，按 README 通过）已实测；Unicode 合成字符通道通过（非 IME）；其余矩阵项仍待。
3. **Release 证据**：real-ime 保存**原始日志**（非会话转录）并给出命令、exit code、各类计数；ACP/通知回归/host/sink/geometry/GPU/取消/生命周期/reader/RouterFallback/WindowsInputBridgeContract 全绿。**✅ 已满足（2026-10-22 最终 Release 166/166、Failed 0、exit 0、stderr 空；`release-final-*` 原始目录；平台守卫后两测试 1/1、1/1）**。
4. **证据索引**：2026-10-22 的 evidence 目录包含原始 jsonl、截图、命令与 exit；不复制、不伪造缺失 stdout。**✅ 已满足（交付记录 §4 索引）**。
5. **文档状态**：满足以上后，才把既有 task/plan 的 P3b 表述更新为“P3b 验收完成”，同时明确 **TI-07/P4 整体仍未通过**（按实际未覆盖矩阵）。
6. **交付记录**：仅写 2026-10-22 的 **P3b** 交付记录与证据索引；禁止创建/暗示 TI-07 整体交付记录。
7. **提交推送**：继续遵守“整体 TI-07 通过前不提交、不推送”的现行规则。

当前状态：第 1、3、4、5、6 项已满足，P3b 范围收口完成（交付记录已写）；第 2 项部分满足，剩余 P4 矩阵明确未覆盖（含 Enter 候选就绪重跑；原始拼音可通过该场景，不要求 CJK）。TI-07/P4 整体仍未通过，不提交推送。
