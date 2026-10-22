# TI-07 P4 真实验收执行记录（进行中）

日期：2026-10-22 · 状态：执行中（不声称整体通过）

工具：`tools/CSharp.UI.ImeHostCheck`（本批最小适配：TSF 判据读 `TsfBridge.CompositionIdentity`/`Session.GeometryProvider`/桥计数；IMM `ActiveClient`/`LastAnchorClient` 仅 IMM 路径。新 `--english-keyboard` 路径：HKL 枚举+切换 + TSF `ActivateProfile(KEYBOARDLAYOUT,FORPROCESS)` + `GetActiveProfile` 精确核验。`session` 记录补真实 `ActiveBackend`/`BackendReason`/`tsf_session_present`——`input-backend` 为惰性建立前值，不作 TSF 证据。minimize/hide 增加分阶段 setter/CompositionIdentity/候选快照见证。anchor-follow TSF 判据加强为同一组合身份+移动零写+唯一你好提交。）

所有运行：`dotnet run --project tools/CSharp.UI.ImeHostCheck --no-build -- --out <dir> --tsf --profile mspinyin [--english-keyboard] --scenario <name> --timeout-ms 120000 --foreground-wait-ms 120000`，原始 stdout/stderr/exit/JSONL 落各目录。

## 已执行矩阵（按场景逐一）

| 场景 | 结果 | 关键判据 |
|---|---|---|
| focus-switch-cancel | PASS (exit 0) | 组合切字段本地取消；field1/field2 文本与 setter 均未变 |
| external-update-cancel | PASS (exit 0) | 外部绑定写入取消组合；setter 0 额外写入 |
| breakpoint-reflow | PASS (exit 0) | wide→narrow 真实布局切换；旧会话结算；新挂载字段会话重建 |
| anchor-follow | **FAIL** (exit 1) | 真实移动 +60,+60 后 geometry→unavailable、publish delta=0 无重发（交给 good 定位）|
| anchor-follow-fixed | PASS (exit 0) | good 去重重置补丁后重发新 screenorigin；同 CompositionId/SessionId/Epoch=1/UIElement id+generation、preedit ni'hao；移动期零写；最终空格唯一 `你好`+1；`active_backend=Tsf`/`tsf_session_present=true`；前后 PNG s6-composing-before/moved |
| minimize-restore | **FAIL** (exit 1) | 最小化正确撤销几何；恢复后 `session-or-anchor-not-ready`——TsfBridge/GeometryProvider 5s 未重建；final_text=`nihao` writes=1（残留组合被 IME 提交原始拼音的来源待 good 按失活 contract 判定） |
| hide-restore | PASS (exit 0) | 隐藏期间几何撤销、组合取消合法；恢复后新 Epoch 几何有效重建（强化见证已加：分阶段 setter/快照） |
| unicode-direct | PASS (exit 0) | 中😀 各一标量提交 setter +2（合成通道，非 IME 证据）|
| undo-redo | PASS (exit 0) | 组合提交你好→Ctrl+Z 回退原文→Ctrl+Y 恢复你好 |
| close-window | PASS (exit 0) | 组合中 WM_CLOSE；窗口真销毁；subclass_restored，callback_exceptions=0 |
| ascii-plain (--english-keyboard) | **FAIL** (exit 1) | en-US HKL 0x4090409 + TSF 活动 profile 验证为 KEYBOARDLAYOUT/0x0409（GetProfile flags 0x2 + ActivateProfile S_OK + GetActiveProfile 全对）；`abc` 扫描码仍触发真实组合（ime_took_keys=true, writes=0）——已交 good 判定；en-US 键盘 TIP 本机 IME-backed 需按 contract 区分环境边界 vs 产品缺陷 |

## 未覆盖/环境边界

- DPI 变化：环境单 DPI 屏（GetDpiForWindow=144）；不改系统 DPI，如实记环境边界。
- 横向文本 ScrollX、禁用/卸载、窗口级真实失活（alt-tab 等价）：现有工具无对应场景，需新场景实现——不因"工具未写场景"而冒称环境不可达。
- 英文普通输入：`--english-keyboard` 前置链已工作；`abc` 组合吃键待 good 按失活/TIP contract 判定；manual-english F10 守卫未动。

## 构建

`dotnet build tools/CSharp.UI.ImeHostCheck -c Debug`：exit 0（`build-tool/` 原始 stdout/stderr/exit）；只工具变更，产品未改。

## 追加执行（cancel-safety EndCore 补丁后）

| 目录 | 场景 | exit | 结果 |
|---|---|---|---|
| english-uithread/ | ascii-plain --english-keyboard | 0 | PASS：UI 线程快照原 profile(INPUTPROCESSOR mspinyin)+HKL0x8040804 → GetProfile(KL,0409,hkl)flags=0x3 → ActivateProfile(FORPROCESS)+GetActiveProfile 全对；abc landed_exact writes_delta=3 ime_took_keys=false；finally 恢复原 profile+HKL 全回读核对 |
| hide-restore-cancel-fixed/ | hide-restore --tsf --profile mspinyin | 0 | 工具判 PASS 但日志证真实缺陷：隐藏中 writes 0→1 text="nihao"（pending cancel 把预编辑当提交写入绑定）。state_while_hidden=null（会话已拆✓）restore epoch2/session2。已交 good |
| minimize-restore-cancel-fixed/ | minimize-restore --tsf --profile mspinyin | 0 | 同上：最小化中 writes 0→1 text="nihao" 泄漏 |
| window-deactivate-v2..v4/ | window-deactivate | 1 | v4: SetActiveWindow 于脚本线程→不能跨队列取前台（工具缺陷非环境）；已重写为 UI 线程 AuxActivate + 六路失活观察（IsWindowActive/TsfSession.IsFocused/GetFocus/GetActiveWindow/GLFW IsFocused/foreground），待复跑 |

工具判据已加严：hide/minimize/deactivate 失活阶段零写计入 verdict（再跑将如实失败而非 PASS）。
新场景已实现：window-deactivate(UI 线程自有顶层辅助窗)、horizontal-scroll(真实 ScrollX>0 经 TSF 几何快照同源见证)、disabled-unmount(HarnessComponent State 驱动 .Disabled/条件卸载两阶段)。待 good 取消补丁稳定后统一真实复验。

## 追加执行 2

| 目录 | 场景 | exit | 结果 |
|---|---|---|---|
| horizontal-scroll-v2/ | horizontal-scroll --tsf --profile mspinyin | 0 | **PASS**：13 次 nihao+Space 填充使真实 ScrollX=63.98>0，横向滚动下新组合锚点有效、Space 唯一提交你好（writes 精确） |
| window-deactivate-v5/ | window-deactivate --tsf --profile mspinyin | 1 | 失活机制全通：同 UI 队列真前台转移（aux 0x7022E），IsWindowActive=false、geometry_revoked、销毁后真实回落 reestablished；唯一失败=失活期间 nihao 1写泄漏（与 minimize/hide 同根，good 已接） |
| horizontal-scroll-v1/ | （首次） | 1 | 填充量不足（8 次未超宽）—工具判据正确拒绝，已修 32 轮 |

工具新增：setter 时序 trace（每次真实 setter 记 field/seq/text/phase）+ 全场景 phase 标记 + host/aux WM_* tap（SetWindowSubclass 只读见证 WM_ACTIVATE/KILLFOCUS/SETFOCUS/SHOWWINDOW/NCACTIVATE 等）；新场景 resize-samebranch（480→560 同 wide-hstack 分支保持同 Session/CompositionId/预编辑/0写+唯一提交）、disabled-unmount（两阶段真实 State 驱动）。判据加严：minimize/hide/deactivate 失活阶段零写计入 verdict；deactivate tsf_focus_lost 改 session-present 语义。

## 追加执行 3

| 目录 | 场景 | exit | 结果 |
|---|---|---|---|
| disabled-unmount-v1/ | disabled-unmount --tsf --profile mspinyin | 0 | **PASS**：disable+unmount 两阶段均 composition_ended+geometry_revoked+**zero_writes=true**+reestablished——State 驱动真实节点路径下失活取消零写（与窗口级路径泄漏成对照） |
| resize-samebranch-v1/ | resize-samebranch --tsf --profile mspinyin | 1 | 工具判据缺陷：size 为物理像素 720（150%），目标误按 480 物理计算→越过 460 断点→mode 翻转→组合合法取消，判据如实失败。已修为物理 +80 且 mode_during==wide-hstack 入 verdict |
| build-tool4/ | 工具 Debug 构建 | 0 | DiagnosticProbe 接线完成（写同一 ProbeLog tick 对齐 wnd-msg/phase/binding-write；RunScenarios 末尾置 null） |

DiagnosticProbe（good 新增 NativeCompositionOwner static Action<string>?）已接线。等 good 交还 dotnet 后跑 window-deactivate-trace 或 minimize-trace 新目录取证。

## 追加执行 4（取证+新场景）

| 目录 | 场景 | exit | 结果 |
|---|---|---|---|
| window-deactivate-trace/ | window-deactivate --tsf --profile mspinyin（DiagnosticProbe+wnd-msg+phase 全序） | 1 | **根因已定**：同 tick(698593296) 同一 DrainSafePhase 迭代内 native WM_KILLFOCUS → drain 应用已 capture Update(t12)/End(t14) → WRITE nihao → EditCompleted Commit(t15) → 然后才 deliver Focus(false) → session 由 End fact 自身退役。**cancel-intent 从未生成**——失活交付排在同 iteration 已排队 End/Result 之后；按 plan:118 失活应先 cancel0写，缺陷=交付排序。raw seq 全留存 |
| resize-samebranch-fixed/ | resize-samebranch --tsf --profile mspinyin | 0 | **PASS**：720→800 物理仍 wide-hstack，同 CompositionId/预编辑/0写，geometry publish+3/revoke+2，唯一提交你好 |

resize-samebranch-v1（失败）保留未覆盖。good 接 ordering 返修；取证后我按指令清理 DiagnosticProbe 接线+wnd-tap 临时源码，保留场景与判据。

## 临时诊断清理计划（good ordering 补丁稳定后执行）

工具侧（本次取证引入）：
1. `NativeCompositionOwner.DiagnosticProbe` 接线（RunScenarios 头挂/尾置 null）——产品侧属 good，由 good 移除；我移除工具挂接。
2. host/aux `wnd-msg` SetWindowSubclass tap（InstallHostTap/RemoveHostTap/TapMsg/AuxWindowProc 打点）——取证据专用，移除。
3. `setPhase`/`binding-write`/`owner-diag` trace——`binding-write`/`phase` 对长期判据有用（零写对位），**保留**；仅移除 owner-diag 挂接与 wnd-msg 类与调用。

产品侧：good 负责移除 DiagnosticProbe 接缝与其调用点。

保留（非临时）：新场景 window-deactivate（UI 线程 aux 失活路径）/horizontal-scroll/resize-samebranch/disabled-unmount、HarnessComponent、失活阶段零写判据、anchor_after 移动时捕获、English UI 线程作用域、post-lazy backend 记录。原始证据目录全部保留不覆盖。

## 误删事故与恢复（独立核对记录）

- **事故**：good 自报 REM 误删未跟踪文件 `src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionOwner.cs`（无 git 回收站）。
- **恢复来源**：`src/CSharp.UI.OpenTK/bin/Release/net10.0/CSharp.UI.OpenTK.dll`(316,928B, mtime 21:29, md5 `2a7f70529796245bb5a645a124a07581`)+PDB(102,232B, md5 `90ba38e3daaa5bd6c2eb80f2b1aeebd9`)，经 ilspy 反编译。
- **恢复产物**：`artifacts/ime-work/p4-cancel-safety/NativeCompositionOwner.decompiled.cs`；恢复初始时 src 源文件与之 md5 相同（36,616B, md5 `e1c726064733f2986c890faa6551635c`）；随后 good 补回 nested 类型+init-only 修正至 38,881B/1382 行，当前 src 已与 decompiled 快照不同。
- **保全备份**：`artifacts/ime-work/p4-cancel-safety/backup-2129/`（DLL+PDB，21:37 复制）与 `recovery-backup/`（DLL Debug+Release+PDB+decompiled）均已独立复制，md5 与源一致。
- **功能对位（我侧核对）**：当前源含 DLL 后全部已验证特性——`CancelIntent.DeactivationFence`/`_cancelSuppressionTicket`/`ExecutePendingCancelsUpTo`/`drain suppressed …(cancelled-segment residue)`/`DiagnosticProbe`/`RequestCancel`。失活 hook（WM_ACTIVATE+WM_KILLFOCUS→deactivation-hook intent-registered）在 `TsfHostSession.cs`（未受影响，21:25 mtime）。
- **不可恢复差异**：全部 XML 文档注释丢失（0 个 `///`，原文件有大量中文 summary/remarks）；字段/参数命名可能被反编译器重命名；原始格式/分部结构不保证一致。**反编译能编译+测试绿≠源码等价**——待 good 逐项声明。
- **状态**：GUI 验收暂停至 good 给出恢复清单+事故记录；我侧不跑、不覆盖。

### 恢复清单（good 自报，已核）

- 事故原话：`edit` 锚定失败时误发文件级 `REM` 删除 NativeCompositionOwner.cs（本意修 drain 重复 `_pending.Clear()`）。
- SHA256：dll `b7141d2501da416a4e1916c985ee92348054b28491fd57fd1433b9ab1623a5ee`；pdb `949eb3539f4910f1eb438231f69ad76612bad765c2b78e8fb8bedc1207c65c30`。
- 反编译后补回：nested 类型（NativeCompositionDrainResult/NativeCommittedBaseline/NativeCompositionContext/NativeDocumentDelta）+ init-only 初始化修正。现 38,881B/1382 行，Release 编译 0 错、TsfCancellationTests 8/8。
- DLL 未含的未 build 编辑（drain-start onlyDeactivationFence/suppressionBound/<=bound）按已否决语义**不恢复**。当前方向（主代理批准、待实证）：**更早窗口消息边界 + 既有 ticket 分界**——失活栅栏沿既有 hook/捕获模式在事实应用前执行，失活段归因同段 facts；不新增组合 generation 抽象，不得吞全部 pending。
- 我侧 md5/sha 已复核一致；`deactivation-hook` 等字符串在 TsfHostSession.cs（未删）。

## 场景证据 Manifest（截至当前，实际日志为据）

| 目录 | 场景 | exit | pass/fail | 说明 |
|---|---|---|---|---|
| ascii-plain-english-keyboard/ | ascii-plain | 1 | fail | 早期失败；后 english-uithread PASS 取代 |
| english-uithread/ | ascii-plain | 0 | pass | UI 线程作用域切换英文，profile/HKL 恢复，abc 3写 |
| unicode-direct/ | unicode-direct | 0 | pass | 合成通道（非 IME 证据） |
| focus-switch-cancel/ | focus-switch-cancel | 0 | pass | TSF 两字段切换取消 |
| container-scroll/ | (P3b 目录外) | - | - | P3b 已验，见 p3b evidence |
| breakpoint-reflow/ | breakpoint-reflow | 0 | pass | 跨分支 resize 允许取消 |
| anchor-follow/ | anchor-follow | 1 | fail | 早期 API 缺陷（zeroWrites 误算） |
| anchor-follow-fixed/ | anchor-follow | 0 | pass | 修复后 PASS（同 CompositionId，移动中 0写→你好1写，主代理亲看 PNG） |
| resize-samebranch-v1/ | resize-samebranch | 1 | fail | 误判 DPI：480 逻辑≠720 物理，误跨断点（已修+80 物理判据） |
| resize-samebranch-fixed/ | resize-samebranch | 0 | pass | 720→800 物理 wide-hstack，同 identity/preedit/0写，唯一提交 |
| minimize-restore/ | minimize-restore | 1 | fail | 早期版本 nihao 泄漏 |
| minimize-restore-cancel-fixed/ | minimize-restore | 0 | **工具 exit0；严格失活零写 FAIL（不采纳）** | 日志证：最小化中 writes 0→1 text="nihao" 旧段泄漏，判据当时未覆盖（见上文 §追加执行2） |
| minimize-trace/ | minimize-restore | 1 | fail | 取证：KILLFOCUS 先 capture、WM_ACTIVATE 迟到 |
| minimize-trace2/ | minimize-restore | 1 | fail | 取证：KILLFOCUS hook 已注册 intent 但 ticket 分界未执行 |
| hide-restore/ | hide-restore | 0 | **工具 exit0；严格失活零写 FAIL（不采纳）** | 早期弱判据 PASS；后续取证证隐藏中 nihao 泄漏（同型缺陷） |
| hide-restore-cancel-fixed/ | hide-restore | 0 | **工具 exit0；严格失活零写 FAIL（不采纳）** | 日志证：隐藏中 writes 0→1 text="nihao"（pending cancel 把预编辑当提交写入），state_while_hidden=null 会话已拆；见上文 §追加执行2 |
| disabled-unmount-v1/ | disabled-unmount | 0 | pass | 真实 State 禁用+卸载两阶段零写 |
| external-update-cancel/ | external-update-cancel | 0 | pass | 外部绑定写入取消 |
| undo-redo/ | undo-redo | 0 | pass | Ctrl+Z/Y 真实恢复 |
| close-window/ | close-window | 0 | pass | 组合中关闭释放 |
| horizontal-scroll-v1/ | horizontal-scroll | 1 | fail | v1 判据用 IMM 计数（已修 TSF 几何 ScrollX） |
| horizontal-scroll-v2/ | horizontal-scroll | 0 | pass | ScrollX=63.98，锚点有效，唯一提交 |
| window-deactivate-v2..v5/ | window-deactivate | 1 | fail×4 | 早期各版本失活路径迭代失败原始证据 |
| window-deactivate-trace/ | window-deactivate | 1 | fail | 取证 v1：deliver Focus 排在 Result 后 |
| window-deactivate-trace2/ | window-deactivate | 1 | fail | 取证 v2：Terminate 成功但已 capture Commit 仍应用 |
| window-deactivate-trace3/ | window-deactivate | 0 | pass | 取证 v3：cancel-intent→Terminate→drain suppressed×4→零写 |
| build-tool[1-4]/ | - | 0 | - | 工具构建原始输出 |

未跑/待复验（good 当前补丁后新目录）：window-deactivate、minimize-restore、hide-restore 三场景最新补丁版零写+恢复唯一提交。清理 wndtap/owner-diag 接线后须实际 smoke。

## 追加执行 5（栅栏二轮：更早消息边界 + fence 收紧）

| 目录 | 场景 | exit | 严格零写 | 结论 |
|---|---|---|---|---|
| minimize-trace3/ | minimize-restore | 1 | FAIL | **更早消息边界对真实 minimize 无效**：TIP 经自身 TSF focus 通道在任一窗口消息前 capture（seq164 capture End t14 先于 seq165 WM_KILLFOCUS）；ShowWindow(SW_MINIMIZE) 真实序=KILLFOCUS→SIZE_MINIMIZED→ACTIVATE(0x200000)→ACTIVATEAPP，无 SC_MINIMIZE/SHOWWINDOW。pendingCancel=1 登记但分界未执行→WRITE nihao。须走失活段归因/retire 前置 |
| hide-trace/ | hide-restore | 0 | **PASS 零写** | WM_ACTIVATE@155 先于 TIP capture → cancel-apply t12 terminated hr=0 → drain suppressed Update/EditCompleted/End/EditCompleted t13-16 → 零写 → Retire→Dispose；恢复 Focus→新 epoch |
| deactivate-trace4/ | window-deactivate | 0 | **PASS 零写** | 同 trace3 链（WM_ACTIVATE 边界有效） |

**现状**：hide+deactivate 两路径经更早 WM_ACTIVATE 边界已达成真实零写；minimize 路径 TIP 失焦感知在消息泵外，无消息边界可用，待 good 归因/retire 前置修复。

## 追加执行 6（FocusProbe 取证：capture 时刻窗口状态对照）

| 目录 | 场景 | exit | 结论 |
|---|---|---|---|
| minimize-probe/ | minimize-restore | 1 | 取证：失活段 capture t12-15 `focus=0x0 focusIsHwnd=False`（capture 时刻 GetFocus 已离自有 hwnd）；fgIsHwnd=True iconic=False（前台未切、未 iconic）。正常段 t1-11 focusIsHwnd=True |
| anchor-probe/ | anchor-follow | 0 | 对照：正常提交 capture t12-14 `focusIsHwnd=True fgIsHwnd=True`，WRITE 你好 1次 |

**归因判据候选**：capture 时刻 `GetFocus==自有hwnd`（focusIsHwnd）真实二分物理失活 vs 正常独立提交；GetForegroundWindow/IsIconic 不区分（失活瞬间前台仍 hwnd、未 iconic）。已转 good 作归因实现依据。

## 追加执行 7（focusIsHwnd 归因版）

| 目录 | 场景 | exit | 结论 |
|---|---|---|---|
| minimize-restore-fixed/ | minimize-restore | 0 | **PASS 零写**：capture t12-15 `deactivated=True`（focus=0x0）→cancel-apply t16 terminated→drain suppressed×4 + 后续 t17/18 deactivated=True 亦 suppressed→零写→Retire→恢复 WM_ACTIVATE+SETFOCUS+deliver Focus 真实回落 |
| hide-restore-fixed/ | hide-restore | 1 | **FAIL（回归）**：hide 路径 capture t12-15 `deactivated=False`——capture 时刻 KILLFOCUS 未分派 GetFocus 仍 hwnd（focusIsHwnd=True 假阴）；但 fg=0x0 fgIsHwnd=False 已为失活信号。intent t16>facts 分界不执行→WRITE nihao。判据候选 `!focusIsHwnd \|\| !fgIsHwnd` 已回 good |

**现状**：minimize+deactivate 经 focusIsHwnd 归因已零写；hide 路径需合并 fg 信号。

## 追加执行 8（focusIsHwnd||fgIsHwnd 归因版 + 恢复唯一提交判据）

| 目录 | 场景 | exit | 结论 |
|---|---|---|---|
| minimize-restore-fixed2/ | minimize-restore | 0 | **PASS 全判据**：失活段 capture t12-15 deactivated=True→cancel-apply terminated→drain suppressed×4→**0 写**；恢复 WM_ACTIVATE/SETFOCUS 真实回落→新 epoch(session_id=2) nihao→Space 唯一提交你好(setter_writes=1) |
| hide-restore-fixed2/ | hide-restore | 0 | **PASS 全判据**：同链 suppressed×4+零写→恢复后新 epoch 唯一提交你好 |
| window-deactivate-fixed2/ | window-deactivate | 0 | **PASS 全判据**：同链 suppressed×4+零写→恢复后新 epoch 唯一提交你好 |

判据已强化：三场景日志现含 `state_commit`（新 CompositionSessionId/CompositionId/generation）+`restore_commit_ok`（文本尾部恰 你好 且 setter +1）+失活段 `zero_writes_while_*=true`+`reestablished`。工具新增恢复段真实输入（nihao→组合→Space）。

## 追加执行 9（postclean smoke：接缝清理后真实复验）

工具侧已移除 DiagnosticProbe 挂接+wnd-msg tap（_msgLog/TapMsg/InstallHostTap/RemoveHostTap/host_msg_tap/comctl32 pinvoke），产品侧 good 已删 DiagnosticProbe/Trace 及调用点。恢复段补真实输入判据 nihao→候选 PNG→Space→你好+1。

| 目录 | 场景 | exit | 失活段写 | 恢复提交 | 候选PNG |
|---|---|---|---|---|---|
| postclean-minimize-restore/ | minimize-restore | 0 | 0（zero_writes_while_hidden=true） | 你好 setter+1, session_id=2, epoch=2 | s8a2-restored-composing.png（9项候选可读，1.你好高亮，第1页） |
| postclean-hide-restore/ | hide-restore | 0 | 0 | 你好 +1, session_id=2 | s8c-restored-composing.png |
| postclean-window-deactivate/ | window-deactivate | 0 | 0 | 你好 +1, session_id=2 | s8d-restored-composing.png |

JSONL 清理后无 owner-diag/wnd-msg 行（取证通道已移除，场景仍全过）。

命令（每目录同）：
`dotnet run --project tools/CSharp.UI.ImeHostCheck --no-build -- --out <dir> --tsf --profile mspinyin --scenario <scenario> --timeout-ms 120000 --foreground-wait-ms 90000 > <dir>/stdout.txt 2> <dir>/stderr.txt; echo $? > <dir>/exit.txt`

sha256：
- src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionOwner.cs = 30e8879fd59ddad268cd477f50ffa79a46a688e512c2be2783d03bda40582798（good 冻结版，build 0 错，全量测试绿）
- tools/CSharp.UI.ImeHostCheck/Program.cs = f9e323f637064074267aede42b7b84e3cea26be0ca61c8ba92e9c30ee7f1f7b8

## 追加执行 10（postclean 后工具修正）

- HostRoutingSpike（untracked）：`new TsfMessageHook(...)` lambda 3参 vs 产品 4参签名（lParam 新增，product caller 已迁移）。修为 `(hwnd, message, wParam, lParam) => wParam == 0x43`（lParam 不参与判据）。Debug+Release `dotnet build tools/CSharp.UI.HostRoutingSpike` 各 0 错。

## 追加执行 11（post-stackalloc 冻结版 fresh-dir 终验）

NativeCompositionOwner.cs sha256=58626b17fd50974a7f0b70c3bcafc9d753ba8d0958cb9634d1d19e44ab95631b（stackalloc 冻结版）。

| 目录 | 场景 | exit | 失活段写 | 恢复提交 | PNG |
|---|---|---|---|---|---|
| postclean2-minimize-restore/ | minimize-restore | 0 | 0 | 你好 +1, session_id=2 | s8a2-restored-composing.png |
| postclean2-hide-restore/ | hide-restore | 0 | 0 | 你好 +1, session_id=2 | s8c-restored-composing.png |
| postclean2-window-deactivate/ | window-deactivate | 0 | 0 | 你好 +1, session_id=2 | s8d-restored-composing.png |

JSONL 无 owner-diag/wnd-msg 残留。Program.cs sha256=f9e323f637064074267aede42b7b84e3cea26be0ca61c8ba92e9c30ee7f1f7b8。
