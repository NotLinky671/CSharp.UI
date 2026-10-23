# TI-07 路线复核与有界诊断准备结果（DSH）

日期：2026-10-20。执行：DeepSeek Harness，精确模型 `commandcode-plan/deepseek/deepseek-v4-flash`。规划/审核/真实验收：Codex。

状态：**路线复核 + 有界诊断准备完成；未运行可见 GUI、未注入按键、未提交推送、未开 TI-08。** 本轮只做两件事：(1) 对真实 IMM 与隔离 TSF 两条已复现失败路线做证据级复核并给出确定缺口/假设分级；(2) 用现有 CLI 准备至多 3 项优先实验。附带一处最小诊断留痕改动（仅 `tools/` 与 `tests/`）。

设计依据：[候选布局路线修订)](../../../../../docs/plan/2026-10-19-windows-ime-candidate-layout-route-amendment.md)、[TI-07)](../../../../../docs/archive/2026-10-23-text-input-editing-tasks/07-windows-ime-and-host-routing.md)、[TSF 原型审核](../../2026-10-20-ti07-tsf-prototype-review.md)。

---

## 勘误（2026-10-20，R17 修复后补录；Codex 审核意见已并入）

Codex 审核（审核文件“路线诊断独立复测与报告审核”）不接受本报告原先的“剩余解释只有三类”“三项实验全失败即无应用侧手段”“自绘是唯一保证路线”。已按以下方式修正：

- 结论摘要第 3、4 条改为**候选解释（不声称穷尽）**与**当前环境/实现下尚未取得跟随**，不再推断机制不可能或应用侧无任何手段。
- §3 的 STA 能力依据原先写成“`--apartment sta` → `Engage` 内 `CoInitializeEx(APARTMENTTHREADED)`”，该依据**错误**：R17 实测进程入口线程在 `Main` 之前已被固定为 MTA（`apartment_at_main_entry=type=1`），窗口创建后才请求 STA 只能拿到 `RPC_E_CHANGED_MODE`（exit 2）。已改为引用 R17 修复后**实际 RunHost 隐藏路径**的 MTA/STA 结果（见下）。
- §5 自绘候选结论改为“在本轮已知资料下的成本评估与候选方向”，不再是“唯一保证路线”。

R17 修复与实测（细节见 [ti07-dsh-sta-startup-fix-result.md](dsh-sta-startup-fix-result.md)）：
`--hidden-host` 复用可见运行同一条 `RunHost`（套间准备 → 窗口 → TSF → 消息循环 → 清理），不 ShowWindow、不请求前台、不发键、不跑场景。
隐藏路径实测：MTA `exit=0`（入口线程，`apartment=type=1`）；STA `exit=0`（宿主自选线程，`apartment=type=3`，`runs_on_entry_thread=false`）。
证据：`artifacts/ime-work/ti07-dsh-sta-startup-fix/hidden-mta{,-console}.txt`、`hidden-sta{,-console}.txt`、`hidden-{mta,sta}/tsf-host-check.jsonl`。
本报告 §6 的测试计数（Debug/Release 各 115、自检 52）是 R17 修复**之前**的数字；R17 修复后 Debug 目标测试 139、自检 59（Release 与完整独立复测由 Codex 执行，见新报告）。

## 0. 结论摘要

1. **候选不跟随不是“我们的几何/通知没送出去”。** 两条路线都有原始日志证明 TIP/IME 在纯布局变化后**读到了新坐标**，却仍未移动/隐藏候选：
   - TSF：`scroll-mta-round2` 中滚动的 11 物理像素，在 `ITextStoreACPSink::OnLayoutChange` 的**同步回调内** TIP 重新取得读锁并调用 `GetTextExt(0,6)` 读到新矩形 `(107,128)-(195,167)`，候选未动。
   - IMM：`query-scroll-round2` 中滚动后 IME 重新请求 `IMR_QUERYCHARPOSITION` 并拿到新字符位置 `pt=170,221→211`、anchor `caret=83,28→83,18`；完全裁出后宿主如实撤销（`anchor-revoked ok=True`），候选仍可见。
2. 因此“继续增加布局刷新/通知堆叠”缺乏机制依据，本轮**不再安排任何刷新堆叠变体**（已失败的 `IMC_SETCANDIDATEPOS`、重复 `OnLayoutChange`、pbShow 生命周期顺序等）。
3. 本轮把可检验的解释归为三类**候选**（不声称穷尽 TSF/TIP 行为）：**位移量阈值/量化**、**TIP 只在组合/选区更新时重定位**、**TIP 完全不消费布局变化几何**。第 4 节的 3 项优先实验用于区分这些候选；就本轮已知 CLI 能力而言它们可直接执行（无需新参数）。
4. 若 3 项实验仍全部未能观察到跟随，只能得出“**当前环境 + 当前原型实现 + 当前 TIP 版本下这三项对照未通过**”，不能据此推断应用侧再无任何手段、或机制上不可能；UILess 自绘候选只是**已知的一种**应用侧方向（第 5 节成本评估）。本轮**不实现自绘候选、不作生产迁移决定**。
5. 与路线复核同批的工具改动只有**诊断留痕**（几何查询的拒绝路径 + `OnLayoutChange` 重入深度），不改变任何通知语义、公开 API、退出码或生产代码。（R17 的宿主套间/线程选择修复是随后单独一轮，见勘误与 [STA 启动修复报告](dsh-sta-startup-fix-result.md)。）

---

## 1. 复核证据清单

| 证据 | 路径 | 关键内容 |
|---|---|---|
| TSF 第二轮真实滚动 | `docs/archive/2026-10-23-completed-reviews/2026-10-20-ti07-tsf-prototype-review-evidence/scroll-mta-round2/tsf-host-check.jsonl`、`s4a-composing.png`、`s4b-small-scroll.png` | seq213–222 滚动+通知；seq259 结构化结果；seq280 exit_code=3 |
| TSF 首轮真实滚动 | 同目录上一级 `scroll-mta-round1/` | 11 像素迁移与候选未动（历史） |
| TSF 真实提交/身份 | `nihao-mta-round2/` | 微软拼音 S_OK、MTA、dpi144、唯一提交 |
| IMM 滚动查询 | `docs/archive/2026-10-23-completed-reviews/2026-10-19-ti07-windows-ime-review-evidence/query-scroll-round2/ime-host-check.jsonl` | `ime-request id=0x0006`、`ime-char-pos`、`anchor`、`anchor-revoked` |
| IMM 其它轮次 | 同前缀 `query-scroll-round1/`、`ime-control-final/`、`caret-notify-probe/`、`native-caret-early-final/` | 请求号分布一致性（仅 0x0006/0x0007） |
| 本机 SDK 契约 | `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\{msctf,TextStor}.{idl,h}` | vtable/枚举/接口语义 |
| 官方文档 | 见正文链接 | 通知归属、UILess、GetTextExt 语义 |

代码引用行为修改后的当前行号；被本轮改动的区域另标注“修改前”。

---

## 2. 真实代码 / COM 契约 / 通知 / 窗口消息复核

### 2.1 契约选择正确（已排除的两条“也许缺接口”假设）

- **E1｜不需要 `ITfContextOwnerServices::OnLayoutChange`。** 官方说明 `ITfContextOwnerServices` 面向 **“不实现 `ITextStoreACP`/`ITextStoreAnchor` 的 context owner”**（[接口页](https://learn.microsoft.com/windows/win32/api/msctf/nn-msctf-itfcontextownerservices)、[OnLayoutChange](https://learn.microsoft.com/windows/win32/api/msctf/nf-msctf-itfcontextownerservices-onlayoutchange)）。本原型的 `CreateContext` owner（`AcpTextStore`）实现 ACP store，因此正确路径就是 `ITextStoreACPSink::OnLayoutChange`——正是原型使用的路径（`AcpTextStore.cs:265`）。
- **E2｜`ITfTextLayoutSink::OnLayoutChange` 只能收到 `TF_LC_CHANGE`。** 官方明确“TSF 不支持多视图，本方法永不收到其它 `TfLayoutCode` 值”（[OnLayoutChange](https://learn.microsoft.com/windows/win32/api/msctf/nf-msctf-itftextlayoutsink-onlayoutchange)）。所以我们发 `TS_LC_CREATE` 再发 `TS_LC_CHANGE`（`AcpTextStore.cs:241-251`）都会被折叠为同一个 TIP 通知，**不存在“CREATE/CHANGE 用错所以 TIP 不认”的空间**。
- **E3｜`GetTextExt` 要求调用方持读锁是官方规定**（[ITextStoreACP::GetTextExt](https://learn.microsoft.com/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettextext)：“The caller must have a read-only lock”）。`RequireRead()`（`AcpTextStore.cs:1593`）与 `IsLocked` 的位掩码实现（`TextStoreLockManager.cs:73-79`，读写锁 0x6 满足 READ 0x2）一致。
- **E4｜`show=TRUE` 时 TIP 不必调用 `UpdateUIElement`。** 官方 UILess 文档：「After the TIP receives TRUE in \*pbShow … the TIP does not have to call UpdateUIElement … TIP must call UpdateUIElement() after BeginUIElement() returns FALSE」（[UILess Mode Overview](https://learn.microsoft.com/windows/win32/tsf/uiless-mode-overview)）。round2 只有 2 次 `uielement/begin`+2 次 `uielement/end`、0 次 update，**不是接线缺失**。

### 2.2 通知确实送达且 TIP 在同步回调内重读几何

`scroll-mta-round2` seq213–222（原始 JSONL）逐条：

| seq | t(ms) | 事件 | 字段要点 |
|---|---|---|---|
| 213 | 24240.2 | `layout/invalidated` | reason=`scroll-y-logical`, generation=11 |
| 214 | 24240.3 | `layout/published` | row `(107,139)-(195,178)` → `(107,128)-(195,167)`, scroll_y=11 |
| 215 | 24240.3 | `lock/lock-granted` | flags=0x2 |
| 216,218 | 24240.4 | `geometry/screen-ext` | client 矩形 |
| 217 | 24240.4 | `geometry/text-ext` | start=0 end=6 **rect=(107,128)-(195,167)** generation=11 |
| 219 | 24240.5 | `lock/lock-released` | — |
| 220 | 24240.5 | `notify/layout` | hresult=S_OK, layout_code=1, view=1 |
| 221 | 24240.5 | `window/layout-published` | reason=`scroll-y-logical` |
| 223 | 24757.3 | `capture/s4b-small-scroll` | 滚动后 **+517ms** |

关键结构事实：seq215–219 全部发生在 seq220 的 `notify/layout` **之前**；而 `notify/layout` 是 `NotifyLayoutChange` 在 `sink.OnLayoutChange(...)` **返回之后**才写的（修改前 `AcpTextStore.cs:264-302`）。所以那次 `GetTextExt` 是**在 TSF 处理我们布局通知的同步调用栈里**发生的——要么是 TIP 的 `ITfTextLayoutSink` 处理函数，要么是 msctf 自己，但无论如何几何是**新**的、查询是**成功**的。

seq259 结构化结果如实：`checks_pass=true candidate_visual=pending assessment=pending-visual pass=false`；seq280 `exit_code=3 cleanup_failures=0`。**未视觉通过**，与审核记录一致。

### 2.3 确定缺陷 / 确定缺口（含文件行）

**D1｜几何查询的拒绝路径完全没有留痕（确定证据缺陷；本轮已修）**
修改前 `AcpTextStore.GetTextExt`（原 1436-1491）与 `GetScreenExt`（原 1493-1525）在以下分支**直接 return，不写任何 trace**：`RequireRead()` 失败（`TS_E_NOLOCK`）、`vcView != _viewCookie`（`TF_E_INVALIDVIEW`）、`IsValidRange` 失败（`TS_E_INVALIDPOS`）、`TryGetCurrentLayout` 失败（`TS_E_NOLAYOUT`）；`GetACPFromPoint`（原 1389-1434）同样。后果：**真实 JSONL 无法区分“TIP 读到新矩形”与“TIP 的查询被拒后沿用旧候选位置”**——而后者正是当前全部结论的替代解释。已修为成功/拒绝双留痕，并加 `layout_notify_depth` 与 `lock_type`（见 §6）。
证据反查：round2 全部 14 条 `geometry/text-ext` 都是成功路径（`clipped=False`），拒绝路径一条都没有记录，说明当时的日志**不具备**排除能力。

**D2｜锁会话号与日志会话号同名互相覆盖（确定缺陷，低危，本轮不修）**
`TextStoreLockManager.GrantLocked`（`TextStoreLockManager.cs:227-233`）写 trace 字段名 `session`，与 `ProbeLog` 顶层 `session`（日志会话号）同名。实测：JSONL seq1 `session:9`（日志会话），seq34 `session:1`（锁会话），同一键两种语义，且锁行丢失日志会话号。**不修**：字段名会进入 Codex 独立 audit 与既有证据的解析约定，改名的收益低于风险；在此如实登记。

**D3｜`ITfContextView` 声明后从未被调用（确定缺口，本轮以更便宜手段部分替代）**
`TsfInterop.cs:514-530` 声明了 `ITfContextView`（含 `GetTextExt`），设计稿 §3.2 把它列为“仅诊断对照”，但全工具**没有任何调用点**（`grep ITfContextView` 只命中声明）。缺口价值：应用自己向 TSF view 查询同一范围，可排除“TSF 侧缓存/归一化导致 TIP 看到的矩形与我们返回的不同”。成本：要构造 `ITfRange`（22 个 vtable 槽）+ 编辑 cookie + 读锁，收益/风险比不佳。本轮用 `layout_notify_depth` + `lock_type` 明示“查询发生在我们的 OnLayoutChange 同步回调内、持读锁且成功”作为等价强度更低的替代；完整 view 对照列为**条件性后续**（仅在 P1–P3 全失败后需要封口时再做）。

**L1｜截图只覆盖自有窗口矩形（确定限制）**
`TsfHostWindow.Capture`（`TsfHostWindow.cs:553-554`）→ `ScreenCapture.CaptureWindowRegion`（`ScreenCapture.cs:119-136`，内部 `GetWindowRect`）只抓自有 top-level 窗口矩形；该文件注释明确要求“需要窗口外候选证据时，调用方必须先用系统 API 确认候选窗口归属与边界，再用 `CaptureRegion`”，而各场景目前不做。当前布局（窗口 rect `(80,80)-(840,500)`）覆盖了候选窗；但 **resize/move 之后必须在实验记录里写明证据矩形**，越界即视为证据不足而不是“候选不存在”。

**L2｜文本原点固定 +14px，限制“上行且完全可见”的位移上限（确定限制，决定实验参数）**
`TsfHostWindow.cs:155`（`TextPaddingY = 14`）与 `801-804`（`origin.Y + TextPaddingY - scrollY`）。dpi144 下 `--scroll-delta` 按逻辑单位换算（`DpiUnits.LogicalToPhysical`，`DpiUnits.cs:11-24`，负值向远离零取整）。因此**纯上行滚动且文本行完全可见的上限≈14 物理像素**；更大的上行位移一定部分裁出（`TextRowScreen` 是未裁剪矩形，`AcpLayout.cs:203`），到 38px 时文本行只剩边缘。要制造**更大的可见纯布局位移**只能取负向（文本下移，下方空间约 300px）。

**L3｜BitBlt 屏幕区域会连带捕获其它顶层窗口（确定限制）**
`CaptureRegion` 用屏幕 DC 抓取矩形（`ScreenCapture.cs:165`），证据归属由 Codex 判断；这不是缺陷，但实验记录需写明它不是窗口私有无干扰采样。

### 2.4 已用现有证据排除 / 未排除的假设

**已排除**
- **E5｜IMM `IMR_CANDIDATEWINDOW` 路线未被触发。** 6 个 IMM 轮次的 `bridge` trace 里 `WM_IME_REQUEST` 只出现 `id=0x0006`（`IMR_QUERYCHARPOSITION`）与 `id=0x0007`（`IMR_DOCUMENTFEED`）；`0x0001`（`IMR_COMPOSITIONWINDOW`）、`0x0002`（`IMR_CANDIDATEWINDOW`）**在全部记录中从未出现**。结论：微软拼音的候选位置不由这两个请求驱动，`WindowsImeBridge.OnImeRequest` 的候选窗应答分支（`WindowsImeBridge.cs:809-844`）对该 TIP 是死路；这从证据上关闭了“再补 IMR_CANDIDATEWINDOW 应答”的方向。
- **E6｜IMM 的字符位置应答确实更新。** 同一 JSONL：滚动前 `ime-char-pos offset=6 pt=170,221` / `anchor caret=83,28,86,56`；滚动后 `pt=170,211` / `caret=83,18,86,46`；`script/container-scroll` detail：`anchor_before=(83,56) anchor_after_small=(83,46) publish_delta_small=1 revoked_when_scrolled_out=True still_composing_after=True`。所以 IMM 侧“IME 拿到了新点”成立。

**仍开放（按证据强度排序）**
- **H1（强）** 微软拼音候选窗只在**组合/选区/文本更新**时重定位；纯布局通知不触发重定位。
  支持：2.2 的 TSF 同步回调内读到新矩形仍未动；2.4 的 IMM 滚动后读到新字符位置仍未动；“继续真实输入一个字母后候选跳到新位置”（审核记录 R15/修订文档）。
  边界：样本位移只有 11/10 物理像素；未做大幅位移、移窗、完全裁出对照 → 不能排除 H2/H3。
- **H2（中）** 位移小于 TIP 的重定位阈值或被量化忽略。区分实验：P1（大位移）、P3（移窗 24px）。
- **H3（中）** 候选位置来自“组合开始时”的缓存，仅组合内容变化才失效；布局变化即使被读到也不写回显示位置。区分实验：P2（完全不可见时返回 `S_OK`+全零 RECT）——若候选**隐藏**，说明几何被消费、只是不用于**重定位**；若候选**仍可见**，说明几何根本没用于该候选 UI。
- **H4（很弱，不单列实验）** 重定位是异步且晚于 500ms 截图。反驳：TIP 的重定位决策发生在 `OnLayoutChange` 的同步回调内（2.2），若要走窗口在该回调返回前早已完成；round2 在 +517ms 截图仍未见移动。故**延时截图不进入优先实验**（现状 500ms 已足够，且新增参数属非必要改动）。
- **H5（无正面证据）** TIP 依赖应用未实现的某个几何/通知面。目前无任何日志显示 TIP 查询失败或查询了未实现接口（`store/unsupported` 只出现属性类方法 `RequestSupportedAttrs`）。P1/P2 的结果会进一步削弱或加强它。

---

## 3. 现有 CLI 能力核对（优先复用，未新增参数）

真实运行入口（README）：`dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- …`。退出码语义：`0` 全通过；`1` 断言失败/未知场景；`2` 前置缺失（无前台/TSF 接入失败）；`3` 脚本 checks 通过但候选视觉待人工核对。

| 需要的诊断 | 现有开关 | 可用 | 说明 |
|---|---|---|---|
| STA 小滚动（对照套间） | `--apartment sta --scenario scroll-follow` | ⚠️ 需 R17 修复后才可用 | **原依据错误**：R17 实测入口线程在 `Main` 前已是 MTA（`apartment_at_main_entry=type=1`），窗口创建后请求 STA 只会得到 `RPC_E_CHANGED_MODE`（exit 2）。R17 修复后由 `RunHost` 在启动前自选 STA 宿主线程并复用同一条窗口/TSF/消息循环/清理路径；隐藏路径实测 STA `exit=0`、`apartment=type=3`（证据 `artifacts/ime-work/ti07-dsh-sta-startup-fix/hidden-sta*`）。**可见 STA 滚动尚未由 Codex 运行**，本报告不宣称其已通过 |
| MTA 较大但仍可见滚动 | `--scenario scroll-follow --scroll-delta <n>` | ✅ | `Program.cs:226-232` 只做 int 解析、无正负/上限限制；负值经 `DpiUnits` 正常换算。受 L2 约束（上行完全可见≤14px；下行可见≈300px） |
| 延时截图 | — | ❌ | 现状固定 `Wait(500)` 后 `Capture`（`ScenarioRunner.cs:280-281`）；按 H4 判定**不需要**，见 §4 备注 |
| 移窗 | `--scenario move-window` | ✅ | 固定 `(0,-24)`（`ScenarioRunner.cs:301`，`TsfHostWindow.MoveWindowBy`），行屏幕 Y 应 −24 |
| 完全裁出 | `--scenario clip-out` | ✅ | `delta = row.Bottom - client.Top + 40`（`ScenarioRunner.cs:347`）→ 文本行完全在客户区外 → `TryGetRangeRect` 返回 false → `GetTextExt` 走 `S_OK`+全零 RECT+`pfClipped=1`（`AcpTextStore.cs` `text-ext-fully-invisible-zero-rect`） |
| 裁出时抑制候选 UI | `--scenario clip-out --suppress-candidate-ui` | ✅ | `TsfUiElementObserver.TryShowCandidates`（`ScenarioRunner.cs:357-364`） |
| UIElement Show 诊断 | `--scenario uielement-hide-probe` | ✅ | `Show(FALSE)`→`Show(TRUE)` 前后 `IsShown`+截图（`ScenarioRunner.cs:388-417`） |
| 7 逻辑像素基线 | `--scroll-delta` 默认 7 | ✅ | 默认值 7（`Program.cs:176`）＝ dpi144 下 11 物理像素；**基线保持不变**，新实验只做对照 |

**结论：第 4 节的 3 项优先实验无需任何新 CLI 参数或新的场景代码。** 本轮没有新增开关、没有改退出码/结构化字段语义、没有改生产 `src/` 与公开 API。

---

## 4. 至多 3 项优先实验（由 Codex 真实运行并核对截图）

统一前置与不变量（每个实验都成立）：
- 真实微软拼音为当前活动 profile，场景前后 `GetActiveProfile` 均 `S_OK` 且 CLSID/GUID 匹配（工具已内置身份守卫）；不切换/不启用/不安装 profile。
- `MTA`（除非该实验显式用 `--apartment sta`；**STA 需要 R17 修复后的宿主线程选择**，见勘误；未修复前该组合会以 `RPC_E_CHANGED_MODE` exit 2，不能作为套间对照）；dpi 由 `GetDpiForWindow` 实测（本机 150% → 144），不硬编码。
- 组合在变化期间保持；`Binding` 写入必须为 0；不得用 Esc/提交/重输打开新组合来制造位移；不得移动/隐藏系统候选 HWND、不得合成 `WM_IME_*`。
- **真实截图是唯一门槛**：`candidate_visual=pending`、`pass=false`、exit 3 都表示“脚本检查通过但视觉未判定”；不得把 `S_OK`/`checks_pass=true` 当视觉通过。
- 证据目录互不覆盖；JSONL 必须含 `geometry/*` 与 `notify/layout` 记录。

### P1（最高优先）｜MTA 大幅纯布局位移：区分“阈值/量化”与“不消费布局”

```powershell
# P1a 文本下移 150 物理像素（--scroll-delta -100 逻辑 → 144dpi 下 -150 物理），文本行仍完全可见
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --scenario scroll-follow --scroll-delta -100 `
  --out artifacts/ime-work/ti07-tsf-codex-large-down --timeout-ms 300000 --foreground-wait-ms 180000

# P1b 文本上移 38 物理像素（--scroll-delta 25 逻辑），文本行部分裁出但仍可见
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --scenario scroll-follow --scroll-delta 25 `
  --out artifacts/ime-work/ti07-tsf-codex-partial-up --timeout-ms 300000 --foreground-wait-ms 180000
```

预期证据：
- JSONL 出现 `layout/invalidated`(reason=`scroll-y-logical`) → `layout/published`，行矩形按 §2.2 同样的结构变化（P1a 139→289；P1b 139→101），`generation+1`。
- `geometry/text-ext` 与 `notify/layout` 的先后关系同 §2.2，且**新增字段** `layout_notify_depth=1`、`lock_type="0x2"`；**滚动通知的同步回调内（`layout_notify_depth=1`）不得出现 `geometry/rejected`**（场景重置等安全阶段可能有无关拒绝，需逐条对应；若滚动时查询被拒，先解释再谈视觉）。
- P1b 的 `geometry/text-ext` 必须 `clipped=True`。
- `scenario/result`：`checks_pass=true`、`writes_delta=0`、`pass=false`、`candidate_visual=pending`；进程 exit 3；`cleanup_failures=0`。
- 两张原图 `s4a-composing.png` / `s4b-small-scroll.png` 由 Codex 逐像素比较候选框位置。

区分能力：P1a 位移是既有样本的 ~14 倍（150 vs 11 物理像素）。**候选若随 150px 移动而 11px 不移动** → H2 成立（阈值/量化），路线仍有救；**若 150px 纯布局变化也不移动** → 基本排除 H2，H1/H3 成为主解释。
停止条件：P1a 与 P1b 都出现“文本行按预期移动、身份 S_OK、writes 0、`rejected` 0 条”，而候选框在两图中相对文本行位置不变 → 记录为“纯布局位移量不是原因”，不再尝试任何位移量变体。
备注：`--scroll-delta -100` 使截图文件名仍为 `s4b-small-scroll.png`（历史命名），不影响证据。

### P2｜MTA 完全裁出 ± 候选抑制：区分“几何被消费但不用于重定位”与“几何完全未被消费”

```powershell
# P2a 完全裁出，不抑制：观察 TIP 是否因“全零 RECT + pfClipped=1”隐藏原生候选
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --scenario clip-out `
  --out artifacts/ime-work/ti07-tsf-codex-clipout --timeout-ms 300000 --foreground-wait-ms 180000

# P2b 完全裁出 + 显式抑制控制消息：区分“应用主动抑制”与“几何驱动的自然隐藏”
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --scenario clip-out --suppress-candidate-ui `
  --out artifacts/ime-work/ti07-tsf-codex-clipout-suppress --timeout-ms 300000 --foreground-wait-ms 180000
```

预期证据：
- `geometry/text-ext-fully-invisible-zero-rect`（`S_OK`、全零 RECT、`pfClipped=1`）出现；对应的 `notify/layout` 为 `S_OK`。
- `detail` 中 `fully_clipped=True`、`composing_clipped=True`、`composing_restored=True`、`writes=0`。
- P2b 额外记录 `clip-out-restore-ui` 的 `Show` 结果（`id / candidate / hr / ShownBefore→ShownAfter`）。
- 截图 `s7a-composing` / `s7b-clipped-out` /（P2b）`s7c-candidate-show-false` / `s7d-restored`；**证据矩形 = 自有窗口 rect，须在记录中写明**（L1）。

区分能力：这是“TIP 是否消费我们的 GetTextExt”的最锐利判据——全零 RECT 是官方规定的“当前不可见”语义（[ITextStoreACP::GetTextExt](https://learn.microsoft.com/windows/win32/api/textstor/nf-textstor-itextstoreacp-gettextext)）。候选隐藏 → 几何被消费，H1/H3 成立（只是不用于重定位）；候选仍可见且 P2b 的 `Show(FALSE)` 也无效 → 应用侧对该原生候选既不能重定位也不能隐藏。
停止条件：在 `fully_clipped=True` 且 `rejected` 为 0 条的前提下，P2a 候选仍可见、P2b `Show(FALSE)` 后 `IsShown` 或截图仍显示候选 → 记录“应用侧无法让该原生候选在裁出时隐藏”，停止对 `Show/pbShow` 变体的继续尝试。

### P3｜MTA 移窗 24px：区分“滚动特有”与“任何布局变化”

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --scenario move-window `
  --out artifacts/ime-work/ti07-tsf-codex-move --timeout-ms 300000 --foreground-wait-ms 180000
```

预期证据：行屏幕 Y `-24`、`writes_delta=0`、`checks_pass=true`、`pass=false`、exit 3；同样的 `layout_notify_depth=1` 成功几何；`s5a-composing.png` / `s5b-moved.png` 比较候选是否整体随窗下/上移。
区分能力：移窗改变的是**屏幕坐标**而不是客户区相对布局。若“移窗跟随但滚动不跟随”，说明 TIP 会处理 `OnLayoutChange`，问题收窄到“滚动后的 `GetTextExt` 结果如何被解释”（此时才值得做 §2.3 D3 的 view 对照）；若“移窗也不跟随”，则 H1/H3 基本坐实。
停止条件：24px 纯移窗（组合保持、writes 0、无 `rejected`）候选不跟随 → 与 P1/P2 合并判定，不再做 resize/滚动组合变体。

**低优先补充（不占用 3 项名额，仅在 Codex 有余量时）**：`--apartment sta --scenario scroll-follow` 的套间对照（R17 修复后可用；先确认隐藏 STA 入口已通过，再用可见滚动验证失败与 MTA/STA 无关）；`--scenario resize` 的视觉对照。二者都无需新参数。

---

## 5. UILess 自绘候选成本评估（本轮不实现、不迁移）

官方机制是 **UILess Mode**（[UILess Mode Overview](https://learn.microsoft.com/windows/win32/tsf/uiless-mode-overview)）：应用改为自己绘制候选列表，TIP 停止绘制。它与现有“观察模式”（我们返回 `pbShow=TRUE`、TIP 自绘）互斥。

### 5.1 激活与协商
- 必须用 `ITfThreadMgrEx::ActivateEx(..., ITF_AE_UIELEMENTENABLEDONLY)` 建立 UI-less 线程；该标志下**只有能控制自身 UIElement 的 TIP 才会被激活**（[ActivateEx](https://learn.microsoft.com/windows/win32/api/msctf/nf-msctf-itfthreadmgrex-activateex)）。→ 兼容性成本：其它 TIP（不实现 UIElement 的）在该线程上不被激活，必须保留“回落到原生 UI”的路径与开关。
- `ITfUIElementSink::BeginUIElement` 对**候选元素**返回 `FALSE`（应用自绘），对其余 UIElement 返回 `TRUE`；`pbShow` 是 in/out（[BeginUIElement](https://learn.microsoft.com/windows/win32/api/msctf/nf-msctf-itfuielementsink-beginuielement)）。
- 返回 `FALSE` 后 TIP **必须**调用 `UpdateUIElement`，应用从此时开始读取内容（首个 update 的 updated flags 为全位）。

### 5.2 选词 / 分页 / 键盘
- 内容接口：`ITfCandidateListUIElement` 的 `GetCount`、`GetString(index)`、`GetSelection`、`GetPageIndex`、`SetPageIndex`、`GetCurrentPage`、`GetUpdatedFlags`（原型已在 `TsfInterop.cs:650-694` 按 slot 3–14 显式展平，ABI 已由 Codex 复核 28/28，可直接复用）。
- 选中/结束：`ITfCandidateListUIElementBehavior::SetSelection(nIndex)`、`Finalize()`、`Abort()`（[Behavior 接口](https://learn.microsoft.com/windows/win32/api/msctf/nn-msctf-itfcandidatelistuielementbehavior)）。→ **键盘语义要接管**：数字键选词、空格/Enter 提交、Esc 取消、↑↓/翻页键，都要由应用转成 `SetSelection/Finalize/Abort`；误处理会直接变成功能缺失（当前 TIP 自绘时这些键由 TIP 处理）。
- 分页契约：官方要求“候选列表应表现为分页而不是滚动”，页码在 `TF_CLUIE_STRING` 变化时由应用重算每页条数（[候选列表 UIElement](https://learn.microsoft.com/windows/win32/tsf/uiless-mode-overview)）；`GetSelection` 在无选中时返回 `S_FALSE` 且首参无效。→ 需要按 TIP 的页码语义做兼容矩阵（`GetPageIndex` 只声明接口不够，要与真实 TIP 的页划分对齐）。
- 可选增强：`ITfIntegratableCandidateListUIElement`（Win8+，`ctffunc.h`）用于更细的 UI-less 键盘/快捷键集成（[接口](https://learn.microsoft.com/windows/win32/api/ctffunc/nn-ctffunc-itfintegratablecandidatelistuielement)）；不实现也能工作，但体验与自绘控件一致性需要它。

### 5.3 可访问性与呈现
- TSF 的候选 UIElement **不提供** UIA/UIAccessibility 桥（它只提供 `ITfCandidateListUIElement` 的字符串/页码数据）。自绘候选要满足可访问性就得**另做 UIA provider**（并成为独立验收面）；`README.md` 已把“system accessibility bridges”列为未交付范围。
- 自绘需要自己的候选窗口/绘制：定位必须用自有几何（这正是当前失败的根源所在，也是自绘能解决跟随的原因），但要额外处理 DPI、裁剪、多显示器、主题、候选窗与预编辑下划线的层级、点击命中、滚动手势，以及“GL/Skia 生产宿主”和“GDI 隔离原型”两条路径的一致性。

### 5.4 成本结论（供 Codex 决策，不由本轮决定）
| 维度 | 成本 | 说明 |
|---|---|---|
| 激活/线程 | 中 | 需 `ITfThreadMgrEx` + UIElement-enabled 激活 + 回退开关 |
| 内容/分页 | 中 | 接口已有，但页码/updated flags 需与真实 TIP 逐项对齐 |
| 键盘/选词 | 高 | 接管数字/空格/Enter/Esc/翻页，直接影响提交语义（TI-07 冻结口径） |
| 可访问性 | 高 | TSF 不代劳，需独立 UIA provider |
| 渲染/DPI/主题 | 中高 | 自有窗口/绘制 + 与滚动/裁剪/移动同步 |
| TIP 兼容 | 中高 | 非 UIElement TIP 必须回落；不同 TIP 语义差异需矩阵验证 |
| 验收口径 | 高 | 组合/取消/提交语义与现有冻结契约的交互需重新审核 |

**评估结论**：UILess 自绘候选是本轮资料下**已知的一种**能让应用完全掌握候选位置/裁剪语义的方向（因为位置由应用自己计算），但在激活方式、键盘/选词语义、分页契约、可访问性与 TIP 兼容上都带来独立且不小的成本；它并非“唯一保证路线”，也不能从本报告的有限实验推出该结论。是否采用须由 Codex 另立任务明确批准；本轮不实现、不迁移、不改变验收口径。

---

## 6. 本轮工具改动（最小、可逆、仅供诊断）

| 文件 | 改动 | 理由 |
|---|---|---|
| `tools/CSharp.UI.TsfHostCheck/AcpTextStore.cs` | 新增 `_layoutNotifyDepth`（:23）；`NotifyLayoutChange` 在 `sink.OnLayoutChange` 前后增减深度并写入 `layout_notify_depth`（:265–310）；`GetTextExt`/`GetScreenExt`/`GetACPFromPoint` 的**全部拒绝分支**调用新增 `TraceGeometryRejected`（:1565–1580）留痕，成功路径追加 `lock_type` 与 `layout_notify_depth`（:1446、:1511、:1551） | 关闭 D1 的证据盲区；让“TIP 是否在我们布局通知的同步回调内成功读到新几何”从时序推断变成显式字段 |
| `tests/CSharp.UI.TsfHostCheck.Tests/GeometryTraceTests.cs`（新增） | 6 项测试：无锁拒绝留痕、过期布局拒绝留痕（含锁类型/代数/版本）、非法 view 留痕（含两个 view cookie）、命中失败留痕、成功路径记录 `lock_type` 且深度 0、`OnLayoutChange` 内查询深度 1 且返回后归零 | 针对性覆盖新行为，含“回调内重入读锁”的真实形态 |
| `tools/CSharp.UI.TsfHostCheck/SelfTest.cs` | `RunGeometryChecks` 追加 1 项“拒绝查询带 HRESULT 留痕”检查 | `--selftest` 同步覆盖 |
| `tools/CSharp.UI.TsfHostCheck/README.md` | “证据”节补充 `geometry/rejected` 与 `layout_notify_depth` 字段说明 | 证据字段可读性 |

**语义不变**：不新增/修改 CLI 开关，不改变退出码、`checks_pass`/`candidate_visual`/`assessment` 字段、通知 mask、锁协议、清理顺序；不触碰生产 `src/`、公开 API、全局输入法配置、外部候选 HWND。

验证（无 GUI、无按键；证据在 `artifacts/ime-work/ti07-dsh-route-reassessment/`）：

| 项目 | 命令 | 结果 |
|---|---|---|
| Debug 目标测试 | `tests/CSharp.UI.TsfHostCheck.Tests`（Debug） | **115 通过 / 0 失败**（原 109） → `Debug-tests.txt` |
| Release 目标测试 | 同上（Release） | **115 通过 / 0 失败** → `Release-tests.txt` |
| Debug 自检 | `--selftest` | **52 通过 / 0 失败**（原 51） → `Debug-selftest.txt` |
| Release 自检 | `--selftest` | **52 通过 / 0 失败** → `Release-selftest.txt` |

构建环境说明（与本轮改动无关，供 Codex 复现）：`dotnet build tests/…Tests.csproj` 在**多节点 + 节点复用**下会因 `_GetProjectReferenceTargetFrameworkProperties` → 嵌套 `GetTargetFrameworks` 评估 `CSharp.UI.ImeProbe.csproj` 时抛出 `MSB4276`（本机所有 SDK 都缺少 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator\Sdk` 目录；顶层评估由 workload resolver 兜住，嵌套评估未兜住）而“0 errors 但生成失败”。绕开方式：`-m:1 -nodeReuse:false`（仅构建参数，不改源码）。详见 `build-environment-note.txt`。该现象对未改动的 `tests/CSharp.UI.OpenTK.Tests` 同样成立，属环境性。

**本机确认无法联网安装/还原 NuGet 之外的依赖**（构建期 `NU1900`：无法访问 `api.nuget.org`）；上述测试全部使用本机已缓存包与离线构建。官方接口结论来自已安装 Windows SDK 10.0.26100.0 的 `msctf.idl`/`TextStor.idl` 与 Microsoft Learn 官方文档页（正文已逐条给出链接），**未凭猜测引用任何接口签名**。

---

## 7. 未做与边界

- 未运行可见 GUI、未注入任何按键、未运行 `--native-selftest`（真实 TSF 隐藏窗口通路），**真实验收与截图核对由 Codex 执行**。
- 未开 TI-08；未提交/推送；保留 dirty 工作树与全部既有证据（未删除、未改写）。
- 未改生产 `src/`、公开 API、全局输入法配置/默认 profile、外部候选 HWND；未改 AGENTS.md、`docs/agents-notebook/acp/2026-10-19-devin-acp-check.md`、`check-devin.mjs`；未改 `artifacts/ime-work/ti07-tsf-codex-audit/`。
- 未取消/重启组合、未改文档/选区冒充布局移动、未以自动测试或 `S_OK` 替代候选视觉。
- 不宣称“所有 TSF 实现或所有 Windows/TIP 版本都不支持候选跟随”——本结论仅限本机微软拼音版本 + 该原型窗口/线程/布局实现（见设计稿“与生产宿主的明确差异”）。

### 保护文件当前 SHA256（供 Codex 独立比对；本轮未修改）
```
4605FBE73D44FADE23E6A6BAC8EFC1D54AA87226455A8C06F8A81365FC630B40  AGENTS.md
81A85B5A8B6681EB8A2E80AEB42C81621245EF982EDCC0B44585F1E6DB3350D8  docs/agents-notebook/acp/2026-10-19-devin-acp-check.md
2DCC755EEDF47674E4B16EB84FEA19588924A88555A41572E2BB2933892EBC7C  docs/agents-notebook/acp/check-devin.mjs
BB9140FEA161004F8F6CC5F6B0B03F20D84A0408037D190C00BFAB33904C6FE5  artifacts/ime-work/ti07-tsf-codex-audit/Program.cs
```
（`AGENTS.md` 在本轮开始前即处于已修改状态，本轮未写入；未回滚任何他人变更。）

---

## 8. 待 Codex 审核

1. §2.3 的 D1/D2/D3、L1/L2/L3 与 §2.4 的假设分级是否成立；D2 是否同意“不改名以免影响既有 audit 解析”。
2. §6 的诊断留痕改动是否接受（若认为越界，可只保留报告、把留痕作为下一轮单独提审）。
3. §4 的 P1/P2/P3 是否批准由 Codex 真实运行；参数与停止条件是否需调整。
4. §5 的自绘候选成本评估是否作为后续独立任务的输入；本轮不迁移、不改验收口径。
5. 在视觉结论出来之前，TI-07 保持“未通过”，不提交、不推送。
