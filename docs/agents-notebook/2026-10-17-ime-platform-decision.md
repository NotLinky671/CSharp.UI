# TI-01 平台接入决策：Windows IME / 跨平台能力矩阵

决策日期：2026-10-17

任务：[TI-01 平台可行性与 Windows 原型](../archive/2026-10-23-text-input-editing-tasks/01-platform-feasibility.md)
计划：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md) §3 / §7 / §11

状态：**2026-10-17 范围修订后，Codex批准TI-01接入路线，允许TI-02实施**。

用户已移除微软拼音并明确不要求测试，不再安装、启用或验证它。现有WeType真实预编辑与单次中文结果、86项独立探针测试及平台矩阵支持以自有HWND子类化+IMM32宽接口继续。系统候选实际定位、取消和生产宿主完整联调尚未通过，移交TI-07/TI-10使用当前可用输入法验收。

下文是范围修订前的调查历史，其中“微软拼音强制门槛”“不放行TI-02”及恢复/启用建议均已被本修订取代，不作为当前执行指令。历史观测不能证明微软拼音现仍安装，也不证明兼容全部TSF输入法。

## 1. 实际依赖与二进制身份（OBSERVED）

| 项 | 值 | 来源 |
|---|---|---|
| OpenTK | 4.9.4.0（`product_version 4.9.4+8d1d4628…`） | 探针 `environment.loaded_modules` |
| GLFW 托管包 | `OpenTK.redist.glfw` 3.4.0.44 | `binary-evidence.txt` / Codex 独立核对 |
| `glfw3.dll` | 文件/产品版本 `3.4.0`，`glfwGetVersionString` = `3.4.0 Win32 WGL Null EGL OSMesa VisualC DLL`，SHA256 `3DCBEAE25F5A38935F00056E14B4A64815A81DD2F14E5A10701BE13CA6A56BC3` | 探针 + 上游源码对照 |
| `imm32.dll` / `msctf.dll` | 10.0.26100.9278 / 10.0.26100.8875 | 探针 `system_dlls` |
| OS | Windows 10.0.26200，X64，会话 5，交互式 | 探针 `environment` |

边界：GLFW 3.4 的 Windows 后端（`win32_window.c`/`win32_init.c`）**不 import IMM32，也不处理
`WM_IME_COMPOSITION`**；`glfw3.dll` 原始字节扫描同样为 0 命中。上游 3.4 tag 源码与 redist 二进制行为
交叉核对一致，但**不构成 redist 与 tag 逐字节相同的证明**。因此 IME 组合消息必须由宿主自己接入。

## 2. Windows 接入路线（决策）

**TI-01 原型采用（候选路线，非最终冻结）：自有 HWND 子类化 + IMM32 宽字符接口作为最小接入路径。**
当前只有 WeType 通过该路径取得真实预编辑与结果，因此 IMM 只是**候选路线**；在真实微软拼音门槛通过前，
不得把 Windows 最终路线写成已冻结。

- 探针只子类化自己创建的窗口（`SetWindowProc(GWLP_WNDPROC)`），在 `WM_NCDESTROY` 恢复，恢复失败会
  报告 `uninstall-failed` 并钉住委托，绝不虚报 `uninstall-ok`（`fault-restore` 场景 PASS）。
- 原生回调内的托管异常（含日志写入失败）都在边界内吞掉并计数，不穿回原生栈（`fault` 场景 PASS）。
- `WM_IME_COMPOSITION` 的结果计数**只由本条消息的 `GCS_RESULTSTR` 标志 + 本条查询到的非空结果串决定**；
  无标志但有缓存数据只作快照，不参与提交计数（`CompositionObservation` + 单元测试，最终测试工程共86项，Codex独立执行通过）。
- **提交内容必须与段数分开判定**：真实证据里出现过一次带 `GCS_RESULTSTR` 标志、内容却是纯 ASCII
  `"niha"`（失焦回吐未转换预编辑串）的提交。因此 `ime-commit`/`ime-segmented` 除“恰好一段”外还核对
  固定输入的明确预期结果（如nihao→你好）与该结果所属组合的预编辑历史；纯 ASCII 回吐、结果不符、缺少本段预编辑归因均不能通过。每个结果冻结独立快照并重置当前段历史，同消息后续预编辑不混入前段；场景只读取起点之后新增的结果。

**不采用（本轮）：TSF 文本存储接入。** 说明：`ITfTextInputProcessor` 是**输入法服务端**接口，
不是应用文本存储的一部分；应用侧接入实现的是 `ITextStoreACP` 加上 TSF 线程/文档管理器。
本轮不实现该组合，但若后续证明微软拼音在 IMM32 路径下无法提供必需数据，**最小 TSF 应用客户端
（`ITextStoreACP` + 线程/文档管理器）的可行性仍必须在 TI-01 内先验证，才允许 TI-02 冻结路线**；
不能把“首次验证 TSF 客户端可行性”推迟到依赖后续契约的 TI-07。届时须先在 `docs/agents-notebook`
提决策交 Codex 复审，受影响任务不得凭猜测开始。

**保留能力：** 探针提供只读的 `ITfInputProcessorProfileMgr::GetActiveProfile`（vtable 槽 10）与
`GetProfile`（槽 5），用于记录**实际**活动 profile 身份，而不是把 `--profile` 参数或（对 TSF 输入法常为
空的）`ImmGetDescription` 当身份。受控临时启用只在显式开关下进行，见 §4。

## 3. 真实观测：消息时间线与来源（OBSERVED）

### 3.1 正确 STA 与真实 WeType 组合（Codex 独立 STA 对照，历史证据）

Codex 用独立 STA 包装器反射调用当时已编译的探针，得到：

- `CoInitializeEx(STA)` 返回 `S_FALSE`（已正确初始化 STA）。
- 6 次真实 `WM_IME_COMPOSITION` 更新：`n → ni → ni'h…`，随后 1 个 `GCS_RESULTSTR` 结果“你好”。
- 链式默认处理后还出现 `WM_IME_CHAR` 两次与 GLFW 字符回调“你”“好”。
- 只读 `GetActiveProfile`：`lang=0804`，`CLSID=86598fb9-66a2-463e-b9c2-aeb906d477ad`，
  `profile=607fdf85-fcc8-4dbd-a365-41296f980c9c`，`flags=3` —— **实际是微信输入法（WeType），不是微软拼音**。

结论：真实 TSF 输入法确实能通过 IMM32 兼容路径（CUAS）向子类化窗口提供真实预编辑与“你好”结果；
新结果通道与原字符通道不能同时应用（否则重复）。证据：`codex-sta-launcher.log`、`codex-sta-identity.log`
（保留在 `artifacts/ime-work`，属可清理的中间证据）。

### 3.2 本轮复跑（DSH，2026-10-17 13:14–13:21，最终证据）

- 入口 `[STAThread]`：`CoInitializeEx` = `S_FALSE`，`CoGetApartmentType` = `APTTYPE_MAINSTA`，实际
  `win32_tid` 与消息线程一致（见 `events.jsonl` 的 `managed_tid`/`win32_tid`/`native_tid`）。
- 焦点后只读 `GetActiveProfile`：实际活动 profile 仍为 WeType（`clsid=86598fb9…`、`flags=3`），
  当前 `HKL=0x08040804`，`ImmIsIME=true`。
- 但本轮 `nihao` 未触发任何组合：按键以 `WM_KEYDOWN N/I/H/A/O` + `WM_CHAR` 直达窗口，
  `composition_updates=0`、`result_flag_commits=0`。`ime-commit`/`ime-segmented`/`ime-cancel`/`ime-focus`
  全部 `UNVERIFIED`。

### 3.3 运行上下文与结果对照（OBSERVED；环境归因未证实）

同一台机器上不同入口/参数/时刻的结果并不一致，必须并列记录，不能互相顶替：

| 时刻 | 入口 / 参数 | 实际结果 |
|---|---|---|
| 12:10 / 12:27 | Codex 独立 STA launcher（反射调用已编译探针） | 真实组合与 `ActivateProfile` 成功；12:27 日志 `Temporary Microsoft activation flags=10000001 result=0x00000000` |
| 13:14–13:21 | DSH 的 `--scenario all --input-mode batch --capture on`（`--profile current`） | `ActivateProfile(FORPROCESS|ENABLEPROFILE)` 返回 `0x80004005 (E_FAIL)`；`nihao` 未触发组合，`ime-commit` 等 `UNVERIFIED` |
| **13:16** | **Codex 直接运行当前编译的 `imeprobe.dll` 正式入口** `--scenario ime-commit --profile wetype --input-mode pumped --capture off` | **`ime-commit` PASS：APTTYPE_MAINSTA + 实际 WeType 身份下取得一次标志结果与重复字符通道**（`codex-native-entry-current.log`、`codex-native-entry-current/events.jsonl`） |

因此“13:1x 无法组合”只适用于 13:14–13:21 那条 DSH 复跑，**与 13:16 正式入口 + 显式 `--profile wetype`
+ `--input-mode pumped` 取得真实结果并不矛盾**；显式在获焦后激活当前已启用 profile 与逐键泵消息可能是关键差异，
尚需同条件对照才能定性（先对比这条最小命令，不扩大到 TSF 文本存储）。

关于 CTF/CUAS 宿主：DSH 自身曾记录 11:51 启动 `ctfmon.exe`（来自 `ti01-progress`，**不是 Codex 的记录**）；
本会话也确实观察到 `ctfmon.exe` 未运行、手动启动后立即退出、无 `TabletInputService`、注册表无 CTF 禁用策略。
但这些只是**未验证假设**的相关线索：把“该宿主不可用”写成已证实的“根因、不是探针缺陷”，或声称缺它
“必然返回 E_FAIL”，都缺少同条件因果对照。当前只能记为：真实 TSF 输入法的键拦截与 `ENABLEPROFILE`
**可能**依赖该系统宿主；本会话该条件**可能**缺失，但这不是已证实的根因，也不能据此否定 IMM 可行性。

## 4. 微软拼音受控验证（FAIL / UNVERIFIED）

计划 §11 要求以真实微软拼音完成“预编辑、选词提交一次、取消、候选可定位”。本轮：

- 探针新增 `--temporary-enable-mspinyin on`：先 `GetActiveProfile` + `GetProfile` 快照原活动 profile 与
  微软拼音状态（状态位 `0x2 = TF_IPP_FLAG_ENABLED` 才是“已启用”；`0x1 = TF_IPP_FLAG_ACTIVE` 是活动位，
  与 `ActivateProfile` 参数位 `TF_IPPMF_ENABLEPROFILE=0x1` 不是同一枚举），再以
  `ActivateProfile(FORPROCESS|ENABLEPROFILE)`（`flags=0x10000001`）激活并 `GetActiveProfile` 核对实际身份。
- **恢复义务在任何原生变更尝试之前建立**：无论激活返回失败（含“返回失败但已部分改动启用位”）、日志写入
  抛异常还是后端抛异常，都会执行恢复步骤（必要时 `DeactivateProfile(FORPROCESS|DISABLEPROFILE)`、
  再恢复原活动 profile），并在最后用 `GetProfile`/`GetActiveProfile` **回读校验**；各恢复步骤独立执行，
  日志失败不打断恢复。`RESTORED` 只在回读匹配时为 true，`TryCreate` 返回空时也会带出实际的
  `configuration_touched`/`restore_readback_verified`，不无条件声称“所有更改已恢复”。
- 历史实测（旧构建）：`ActivateProfile` 返回 `0x80004005`；旧的 `temporary-profile-restored` 记录
  `RESTORED=true`、`configuration_touched=false`。新构建下同样的 E_FAIL 会改为
  `configuration_touched=true` 并走完整恢复+回读路径；本次修复只补测试，未再运行真实设置变更。
- 因此微软拼音的真实预编辑/单次结果/取消/候选锚点证据**未取得**，`profile-temporary-enable` 判 `FAIL`。

Windows 门槛判定：**未通过**。TI-01 保持“待验证”，不放行 TI-02。

### 4.1 本轮（DSH，15:13–15:25）初始化顺序对照与未满足条件

本轮把初始化顺序改为 **获焦并泵 → 激活临时 profile → 再泵 → 在真实目标 HIMC 上设 native/open → 再泵回读**，
并要求设置后必须回读匹配才算落地（新场景 `native-mode-readback`）。同时新增只读身份快照：场景起点/结束、
首次真实组合、每次 `result` 时刻各记录一次 `GetActiveProfile`（CLSID/ProfileGuid/类型/语言/flags）并附同一时刻
IMM 的 open/conversion；回调内只登记标签，查询在消息过程退出后执行（无递归泵、无在回调内新建窗口）。

实际结果（4 次真实运行，证据 `artifacts/ime-work/ti01-r2-*-console.log`、`ti01-r2-evidence/`）：

| 项 | 结果 |
|---|---|
| `native-mode-readback` | **PASS**（4/4）：`ImmSetOpenStatus(true)` 与 `CMODE_NATIVE\|CMODE_ROMAN` 都在真实 HIMC 上回读成功 |
| `profile-temporary-enable` | **FAIL**：`ActivateProfile(FORPROCESS\|ENABLEPROFILE)` 返回 `0x80004005`，与旧结论一致 |
| 恢复回读 | **RESTORED=true**（每次都恢复到快照中的原活动 WeType） |
| 真实按键场景 | **SKIP**：探针窗口始终不是前台窗口，未注入任何按键 |

本轮执行上下文中的观察（不作为全机能力或根因结论）：

1. **微软拼音在本机未启用**。独立只读入口 `ImeStaReview.dll --identity-only` 连跑三次稳定给出：
   `WinSta0/Default`、`session=5`、`interactive=True`；实际活动 profile = WeType（`flags=3`）；
   **微软拼音 `GetProfile flags=0`（既未活动也未启用）**（`caps=0x5003002E`）。
   DSH 的激活尝试返回 E_FAIL；但 Codex 经权限审批的受控运行曾从同样 flags=0 成功临时启用，
   取得真实微软拼音身份并恢复。因此不能将原禁用状态直接当成 E_FAIL 的原因，也不能要求永久启用来代替排查执行差异。
2. **部分运行前台不可得**。`SendInput` 前必须确认探针窗口是前台窗口；DSH 这几轮前台是其他应用，
   探针按保护规则 SKIP，没有把按键发给其他窗口。真实验证需在短时保持探针前台的条件下执行
   （`--foreground-wait` 只等待、不争抢）。

关于“把 `SetNativeImeMode` 移到 profile 激活之后”的效果：本轮确实观察到 native/open 在真实 HIMC 上
**回读成功**，但**没有**取得任何真实组合，因此**不能**说“旧顺序被异步覆盖”已获证实——顺序改动仍只是假设，
缺少“同一条件下组合是否出现”的对照数据。

关于只产生英文字流的假设：本轮**没有取得真实打字阶段**（前置条件缺失即 SKIP），因此无法比较
“MS 真实 TSF 中英 compartment 状态”与“IMM 状态”的差异。可复跑的最小下一步已具备：`identity-snapshot`
已同时记录 TSF 实际活动 profile 与同一时刻 IMM 的 open/conversion，等目标输入法可用且前台可得时即可直接比对。

## 5. 候选窗锚点（OBSERVED，不等于候选定位）

- `ime-resize` 在 resize / move 前后发布锚点，`ImmSetCandidateWindow` / `ImmGetCandidateWindow`
  往返成功（`candidate_set_ok=3`、`candidate_get_ok=3`）。
- 但**系统候选窗的实际屏幕位置没有独立观测**（无候选 UI 出现、无法归属候选窗口），
  因此该场景判 `OBSERVED` 而不是 `PASS`；`system_candidate_actual_position_confirmed=false`。
- 截图只保存自有**可见**窗口矩形，没有任意 margin 扩张（本轮 PNG 为 922×576，桌面为 2560×1600）；
  窗口外候选需要证据时，必须先用系统 API 确证候选窗口归属与边界后按其矩形单独采集，
  旧的全桌面截图不作为随仓库证据。

本轮新增 `ime-candidate` 场景作为更严格的做法：在自有窗口**内部**取两个锚点各发布一次，
要求两次截图的窗口矩形完全一致（截图日志新增 `window_rect_screen`），并把判定固化为：API 往返 +
同矩形 + 组合中最多只到 **OBSERVED**，只有人工核对两张 PNG 后才能谈“候选可见且随锚点移动”。
该场景本轮因前台不可得未运行，候选定位仍 `UNVERIFIED`。

## 6. 跨平台能力矩阵（源码级 OBSERVED；真机均 UNVERIFIED）

| 平台 | 当前依赖的接入点 | 已提交文本 | 预编辑/组合 | 候选锚点 | 结论 |
|---|---|---|---|---|---|
| Windows | 宿主子类化 + IMM32（GLFW 自身不处理 IME） | 是（`WM_CHAR`/GLFW char，需去重） | 是（13:16 WeType + `pumped` 取得真实结果）；微软拼音尚未取得 | IMM32 API 可发；实际候选未观测 | IMM 为候选路线；**微软拼音门槛未过** |
| macOS | GLFW 3.4 `GLFWContentView` 已实现 `NSTextInputClient`（`setMarkedText`/`insertText`/`firstRectForCharacterRange`/`interpretKeyEvents`） | 是（GLFW char 回调） | **未暴露**：marked text 在 GLFW 视图内部消费，公共 API 无预编辑 | **未暴露** | 需独立真机调查；不得覆盖 delegate / swizzle |
| Linux/X11 | GLFW 3.4 使用 XIM（`XOpenIM`/`XCreateIC`/`XFilterEvent`/`Xutf8LookupString`） | 是（UTF-8 → char 回调） | **未暴露**：XIM 预编辑在 GLFW 内部处理 | **未暴露** | 需独立真机调查 |
| Linux/Wayland | GLFW 3.4 Wayland 后端**无** `zwp_text_input_v3` / text-input 协议（源码与平台头均无） | 无（无 text-input 协议） | 无 | 无 | 当前依赖不支持；如需须另接协议 |

单位：Windows 组合串以 UTF-16 码元计（`ImmGetCompositionStringW` 的缓冲长度仍以字节计）；X11 XIM
提交为 UTF-8；Wayland text-input-v3 为 UTF-8。映射到核心 UTF-16 时须显式转换。

## 7. 对后续任务的影响与需 Codex 决定的事项

1. TI-01 未通过 → 不启动 TI-02；契约保持草案。**真实微软拼音的标准保持不变**，不追加“或等价 TSF 输入法”
   来替代；WeType 数据只支持“IMM 是候选路线”，不能把 Windows 最终路线写成已冻结。
2. 需要 Codex 决定：是否在有可用 CTF/CUAS 宿主的环境复跑，以完成微软拼音真实验收。宿主缺失目前只是
   **未验证假设**（见 §3.3），不是唯一已证实阻塞项；应先按相同入口/参数/时刻做同条件对照，再决定。
3. 若在同条件复跑后仍无法通过 IMM32 取得微软拼音数据，则须提“扩大 TSF 范围”的决策。届时应说明
   `ITfTextInputProcessor` 是输入法服务端接口，应用侧接入是 `ITextStoreACP` + TSF 线程/文档管理器；
   **该最小应用客户端可行性必须先在 TI-01 验证**，不得直接开始 TI-07。
4. **最小下一步（有证据、可复跑、范围可控）**：本机微软拼音 `GetProfile flags=0`（未启用），
   所以“激活后立即测试 MS 组合”在当前状态下不可能成立。要推进 TI-01 的微软拼音门槛，需要先让微软拼音
   处于**已启用**状态，而这属于用户级输入法设置变更（本探针刻意不做，也不会为验证擅自开启）。
   建议由开发者/Codex 决定其一：
   (a) 在系统设置里启用微软拼音后，按 README 的 attended 命令重跑（需要用户把探针窗口点到前台）；
   (b) 明确授权一次“可回滚的临时启用”验证，并在验证后回读恢复。
   在 (a)/(b) 未决前，不应扩大 TSF 实现范围，也不应把阻塞归因于“环境缺失”而跳过修复。
5. 输入驱动方式已提供 `--input-mode batch|pumped` 对照，并新增 `--input-mode scancode`
   （真实扫描码 `KEYEVENTF_SCANCODE`，逐键泵消息，仍只发真实键盘事件）。13:16 的 `pumped` 已取得结果，
   但本轮未用 `scancode` 做同条件微软拼音对照，因此“虚拟键（wScan=0）与真实扫描码是否影响输入法早期
   键处理”仍是未验证假设，不写成已证实原因。

## 8. 证据索引

| 文件 | 内容 |
|---|---|
| `ti01-2026-10-17-final-batch-summary.txt` | 本轮最终 `all`（batch）结果：UNVERIFIED/OBSERVED/PASS 逐项 |
| `ti01-2026-10-17-final-batch-summary.json` | 同上，机器可读 |
| `ti01-2026-10-17-active-profile.jsonl` | 焦点后实际活动 profile、STA、输入语言、临时 profile 事件提取 |
| `ti01-2026-10-17-mspinyin-temporary-enable-fail.jsonl` | 微软拼音快照 + `ActivateProfile` E_FAIL + 恢复校验提取 |
| `ti01-2026-10-17-screenshot-scope.txt` | 截图范围核对（922×576 vs 桌面 2560×1600） |
| `ti01-2026-10-17-own-window-screenshot.png` | 自有窗口截图示例（无候选、无整屏） |
| `binary-evidence.txt` / `binary-evidence.json` | 依赖二进制身份（历史，仍然有效） |
| `glfw-3.4-source-scan.txt` | GLFW 3.4 平台后端接入点扫描 |
| `probe-all-2-*`、`mspinyin-activation-extract.txt`、`tsf-engagement-extract.txt` | **历史**观察（旧事件字段/旧结论），仅作对照，不代表当前状态 |

本轮新增证据（中间证据保留在 `artifacts/ime-work` 下，属可清理目录）：

| 文件 | 内容 |
|---|---|
| `ti01-r2-evidence/identity-and-native-mode.jsonl` | 初始化关键时刻只读身份 + 同一时刻 IMM open/conversion + native 回读 |
| `ti01-r2-evidence/sta-review-identity-and-mspinyin-flags.txt`、`-after-runs-1/2.txt` | 独立只读入口三次一致结果：活动 WeType `flags=3`、微软拼音 `flags=0` |
| `ti01-r2-evidence/run-summaries.txt` | 本轮 4 次探针运行的场景结论摘录 |
| `ti01-r2-*-console.log` | 各次运行控制台/事件日志（含 `foreground-wait`、`scenario-skipped` 的原始记录） |
