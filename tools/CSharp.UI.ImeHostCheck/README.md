# CSharp.UI.ImeHostCheck — TI-07 真实微软拼音验收工具

生产 `UiApplication` 宿主 + 自有 HWND 子类化桥的真实 IME 验收。全部输入经 `SendInput`
扫描码/物理键注入到**已确认前台的自有窗口**；绝不伪造 `WM_IME_*` 消息冒充验收。

## 运行

```powershell
dotnet run --project tools/CSharp.UI.ImeHostCheck -- --out artifacts/ime-work/ti07/hostcheck [--tsf] [--profile mspinyin] [--scenario nihao-commit] [--timeout-ms 300000] [--foreground-wait-ms 180000] [--manual-english] [--manual-wait-ms 90000]
```

参数：

- `--out` — 证据输出目录（JSONL 日志 + 截图），默认 `artifacts/ime-work/ti07/hostcheck`
- `--profile mspinyin` — 按已审核探针路线 FORPROCESS 选择微软拼音（只选已启用 profile，结束时恢复；不启用/禁用）
- `--tsf` — 显式切换正式宿主到生产 TSF 桥（工具主线程为 `[STAThread]`；不迁移窗口、不绕过 GLFW 检查）。默认不传时仍走 IMM。工具在真实 TextField 聚焦后读取正式 `Session`、只读候选快照和组合身份；`nihao-commit` 要求预编辑 setter 增量 0、真实候选包含“你好”，空格后精确追加“你好”且 setter 恰好 +1。TSF 模式使用逻辑高度 640 的窗口及足够滚动内容；`container-scroll` 小滚/完全裁出/恢复后检查几何和同一组合/候选代次，并最终唯一提交。微软拼音 profile 选择仍只作用本进程且结束恢复。TSF 模式下组合/几何判据读 `TsfBridge.CompositionIdentity`/`Session.GeometryProvider` 与桥自身计数；IMM `ActiveClient`/`LastAnchorClient`/`GeometryRevokeCount` 只在非 `--tsf` 路径使用，绝不混读。`input-backend` 环境记录反映惰性建立前状态；真实后端以会话建立后的 `session` 记录（`active_backend`/`backend_reason`/`tsf_session_present`）为准。
- `--english-keyboard` — `ascii-plain` 改用“既有已启用英文键盘布局”：枚举 `GetKeyboardLayoutList` 选 en-US 主 LangId 0x0409 的实际 HKL，`ActivateKeyboardLayout` 切换本脚本线程布局后，再以 `ITfInputProcessorProfileMgr` `GetProfile(TF_PROFILETYPE_KEYBOARDLAYOUT,0x0409,HKL)` 核对 `TF_IPP_FLAG_ENABLED` 并 `ActivateProfile(FORPROCESS)`，`GetActiveProfile` 回读 Type/LangId/HKL 全对才发 `abc`；场景末恢复原线程布局+原活动 profile。非微软拼音内部英文模式，日志如实标注；任一前置不成立即如实失败不发键。
- `--scenario <name>` — 只跑指定场景（环境/会话建立与关闭清理仍照常）；**未知名字立即报错退出 1，不允许 0 场景记为通过**
- `--timeout-ms` — 总超时，默认 300s；看门狗 Timer 在宿主运行期间真实触发退出，不是事后 Join
- `--foreground-wait-ms` — 等待用户点击自有窗口取得前台的时长，默认 180s；超时未得 → 退出码 2。最小化/隐藏恢复等前台丢失后，每个场景前置再给一次 15s 的有限重点击窗口（不自动抢前台）。
- `--manual-english` — 只跑 `ascii-plain`，中英文切换交给**真实用户**完成（R16：微软拼音对 IMM 转换位写入 set_ok=true 但回读恒 0x0401、对注入扫描码 Shift 亦不响应；程序不写任何 IME 模式位）。窗口标题栏与控制台提示切换；确认键 F10 必须在本窗口前台**由松到按并松开**才算确认（松开后再复核前台；他窗前台的 F10、起始按住、按住中丢前台均不算）。**未获确认立即记失败且不发任何键**。确认后验证 `abc` 精确写入 +3 且无组合；模式由用户自行恢复。
- `--manual-wait-ms` — 人工确认等待时长，默认 90s。
- `--selftest` — 无 GUI 自检（确认守卫的取消/超时/前台语义等纯逻辑检查，注入式探针，不启动宿主、不发送输入），全部通过退出 0。

退出码：`0` = 全部场景通过；`2` = 前置条件缺失（环境不可用——无前台/无桥/无窗口）；`1` = 有失败场景或宿主异常。进程退出码与 `host_exit_code` 同源（worker `RequestExit` 用同一判定），不再出现脚本通过但宿主 exit 2 的假象。判定按场景自身要求规范化：单场景失败优先；仅跑 `ascii-plain`/`unicode-direct` 等无组合场景时，缺少中文组合不构成否决（它们本就不要求组合），可 exit 0；需要组合的中文场景无组合必然自身 fail 计入 `1`。过滤后 0 场景也记 `1`，不会假通过。

## 场景前置（所有发键场景共享）

每个场景独立建立前置，不复用其他场景的节点/焦点/滚动残留：

1. 确认本工具窗口持有前台（丢失时有限等待用户重新点击；绝不 SetForegroundWindow/AttachThreadInput/模拟鼠标）。
2. 清理上一场景可能残留的组合（Esc）。
3. `DispatchWheel` 正 delta 滚回可见顶部。
4. **从当前 `Runtime.Root` 重新查找已挂载 `TextField`**（绝不缓存跨 Responsive 分支的 `ViewNode`）并 `SetFocus`。
5. 等待 `ActiveClient` 非空 + `LastAnchorClient` 有效才进入场景体；前置失败即该场景失败。

## 判据（每个场景的真实通过条件）

| 场景 | 精确判据 |
|---|---|
| `nihao-commit` | 文本末尾精确追加 `你好`，setter 恰好 +1 |
| `escape-cancel` | Esc 后组合结束、文本与 setter 不变、宿主存活 |
| `enter-select` | TSF 模式先等待真实候选快照有效且含“你好”再发 Enter；判据要求恰好一次写入、无换行/OnSubmit 泄漏、组合结束、宿主存活。若提交精确为原始 `nihao`，记录为“Enter 确认原始拼音（本机微软拼音行为）”且通过安全判据；不声称选择汉字或强求 CJK |
| `segmented` | 两段各一次提交，文本精确追加 `你好世界`，setter +2 |
| `digit-select` | 数字 `1` 不写成文本；提交恰一次且结果含 CJK 字符 |
| `ascii-plain` | 默认先切自有 HIMC 英文模式：IMM 读写全部在 UI 线程并回读；微软拼音忽略 IMM 位时在已确认自有前台、无活动组合下按一次物理 Shift 切换并回读确认（round2 实测：IMM set_ok=true 但回读恒 0x0401、注入 Shift 不切换——该路径在本机不可控，如实记 `effective=false`）。`--manual-english` 下改为窗口标题+控制台提示用户手动切英文、F10 确认。判据统一：`abc` 精确追加且 setter +3、无 IME 吸收；不据 IMM 回读位假报成功 |
| `unicode-direct` | `中`（BMP）+ `😀`（代理对）精确追加，setter +2（按标量：中一次、完整代理对一次） |
| `focus-switch-cancel` | 组合中换焦点（当前挂载节点）→ 本地取消，旧段不写入任一字段 |
| `container-scroll` | IMM 沿用原有锚点/几何撤销判据；TSF 模式真实组合期间小滚 7 逻辑像素、完全滚出隐藏、恢复后检查同一 CompositionSessionId/CompositionId/UIElement id+generation 与预编辑、setter 保持 0，之后空格精确提交 `你好` 且 setter +1。保存各阶段自有窗口截图与 JSONL；候选不需要操作系统窗口/API 模拟。 |
| `breakpoint-reflow` | 先有真实组合+有效锚点；窗口宽度越过 460 断点 → 布局模式真实切换，且旧会话取消/锚点撤销；恢复原宽度后重新查询挂载字段重建会话与几何 |
| `anchor-follow` | 组合+有效锚点下真实移动窗口（SetWindowPos）→ 平台锚点缓存清空并重发；移动前后各留 400ms 给系统候选窗重排再截图对比 |
| `minimize-restore` | 起始必须有真实组合+有效锚点；最小化期间见证几何撤销且 setter 零写（失活取消不得把预编辑提交进绑定），恢复后重建会话并恢复有效几何（before/after 全 null 不算通过） |
| `hide-restore` | 起始必须有真实组合+有效锚点；整个窗口 `IsVisible=false` 期间见证几何撤销且 setter 零写（**窗口级隐藏走失活路径，组合被取消合法**——与 TI-06“视图级隐藏保留组合”语义不同，不得混淆），恢复后重建会话并恢复有效几何 |
| `window-deactivate` | 组合+有效锚点下经 UI 线程 `CreateWindowEx`+`ShowWindow`+`SetActiveWindow` 激活本进程自有顶层辅助窗（绝不 `SetForegroundWindow`/`AttachThreadInput`/模拟鼠标）；失活证据取四路任一真实离开：`Runtime.IsWindowActive`→false / `TsfHostSession.IsFocused`→false / `GetFocus`或`GetActiveWindow` 转到辅助窗 / `GetForegroundWindow` 离开宿主——均未到则记前置不可达不算通过；失活后几何撤销+零写；销毁辅助窗等待 `IsWindowActive`/前台真实回落再 EnsureFieldReady。aux 记录含双窗 tid/pid/style/exstyle/owner 与 foreground/active/focus 前后值 |
| `horizontal-scroll` | 多轮真实 nihao+Space 提交把 TextField 填超宽（360px）致内部 `ScrollX`>0——经 TSF 几何快照同源读取；横向滚动下新组合锚点有效且 Space 唯一提交 `你好`；各阶段 setter 计数精确核对 |
| `resize-samebranch` | 组合+有效锚点下 `ClientSize` 物理 +80px 且保持同 Responsive 分支（>460 逻辑 wide-hstack 不过断点）：同 CompositionSessionId/CompositionId/预编辑/setter 0写须保持，新客户区 surface 生效（真实 ClientSize 回读=目标且至少一帧渲染），几何重发后 Space 唯一提交 `你好`；mode_during 必须仍 wide-hstack（若环境把尺寸判到断点另一侧即如实失败不放宽） |
| `disabled-unmount` | 组合中经真实 `State.Value` 切换 `.Disabled(true)` 或条件卸载（真实节点移除路径，HarnessComponent Build 依赖）：两阶段都须组合结束+几何撤销+setter 零写；恢复后 EnsureFieldReady 重建会话 |
| `external-update-cancel` | 组合中外部绑定写入取消组合，setter 无额外写入 |
| `undo-redo` | 场景自行以真实组合+Space 提交一段中文建立历史，再 Ctrl+Z 撤销回提交前文本、Ctrl+Y 重做恢复已提交文本 |
| `close-window` | **必须先见证 IsComposing=true** 再投递 WM_CLOSE，并确认窗口真正销毁/宿主退出；收尾 finish 日志核对子类化恢复与回调异常计数 |
| `candidate-visibility-probe`（诊断，不进默认矩阵） | 真实组合+有效锚点 → 截图 s9a；UI 线程自有 HIMC 上 `ImmNotifyIME(NI_CLOSECANDIDATE)` → 500ms 后快照 GCS_COMPSTR/预编辑/文本写入并截图 s9b；组合仍在才 `NI_OPENCANDIDATE` 重开 → 截图 s9c；Esc 清理。**API 返回值不代表可见性，候选是否消失须 Codex 核对截图；本结果不是 R16 修复验收** |
| `native-caret-probe`（诊断，不进默认矩阵） | 真实组合+有效锚点 → s10a；UI 线程原子守卫 `GetGUIThreadInfo` 查询成功且 `hwndCaret==0`、`GetFocus==hwnd`、前台复核通过才 `CreateCaret(hwnd,NULL,1,1)`+`SetCaretPos` 到当前锚点（**绝不 ShowCaret**；查询失败/已有 caret/非自有焦点或前台均跳过不破坏）→ s10b；小滚动 7px 不同步 caret → s10c（对照）；前台复核后 `SetCaretPos` 到新锚点 → s10d；再以 `NotifyWinEvent(EVENT_OBJECT_LOCATIONCHANGE, hwnd, OBJID_CARET, CHILDID_SELF)` 报告真实 caret 位置（void，非 IME 消息）→ s10e。每步记录 API 值/实际 GetCaretPos/桥锚点/原生串/写入数；**候选是否跟随须 Codex 核对截图，非验收**。等待为有界只读前台轮询，失焦即停止；finally 销毁前复核 `hwndCaret` 仍属本实验 HWND（否则记 `lost_ownership` 不销毁新所有者），再 Esc 清理、滚回顶部 |
| `native-caret-early-probe`（诊断，不进默认矩阵） | 对照“组合开始**之前** caret 已存在”：同一套守卫 `CreateCaret`+`SetCaretPos` 到当前锚点+`NotifyWinEvent` 真实通知 → 再发 nihao 建立组合 → s11a；小滚动 7px → `SetCaretPos`+`NotifyWinEvent` → s11b。守卫/清理与 native-caret-probe 完全同源（仅本实验创建且仍归属的 caret 才销毁）；**候选是否按既有 caret 定位须 Codex 核对截图，非验收** |

## 安全边界

- 只向 `GetForegroundWindow()==hwnd` 的自有窗口发键；前台丢失即停。
- 截图仅自有窗口区域；系统候选窗在窗口外时不越界抓取。
- IMM 模式切换只写自有 HIMC 的 open/conversion 位并回读，结束时恢复；窗口已销毁属“无须恢复”，窗口仍存活而恢复失败如实记为失败场景。
- Shift 中英文切换兜底：只在已确认自有前台、无活动组合时对自有窗口按一次 Shift，结果一律以 UI 线程回读为准；不改系统默认/启用项、不动用户 MSPinyin 配置。本机微软拼音对该注入不响应（round2 实测）；需要英文判据时使用 `--manual-english` 由用户真实切换。
- `--manual-english`：程序全程不写任何 IME 模式位，模式切换与恢复均由用户完成；确认键 F10 只读检测（自有前台按下沿+松开复核），不注入输入。
- `candidate-visibility-probe`：NI_CLOSECANDIDATE/NI_OPENCANDIDATE 只在生产 UI 线程对自有 HIMC 调用，成对 GetContext/ReleaseContext；目标是关闭/重开系统候选列表，绝不发 CPS_CANCEL、不取消组合、不隐藏他人窗口、不改 TSF/全局设置。
- `native-caret-probe`/`native-caret-early-probe`：创建须同时满足 `GetGUIThreadInfo` 查询成功且无线程 caret、UI 线程 `GetFocus==hwnd`、前台复核属自有 HWND；绝不 `ShowCaret`、不改全局闪烁设置，不同时存在两个 caret。实验期间每次等待/截图/`SetCaretPos` 前复核前台，失焦即停止不修改失活队列 caret。`NotifyWinEvent(EVENT_OBJECT_LOCATIONCHANGE, hwnd, OBJID_CARET, CHILDID_SELF)` 只报告自有 caret 的真实位置变化（合法 user32 可达性 API，void 无返回值可冒充成功），不伪造 IME 消息。销毁前在 UI 线程复核 `hwndCaret` 仍为自有 HWND 且系本实验创建，所有权已转移则记 `lost_ownership` 不销毁新所有者。不隐藏/操作系统候选 HWND、不合成 IME 消息、不取消组合。
- 场景共 25 个名字：默认矩阵 20 个 + 5 个仅显式 `--scenario` 运行的诊断场景（`candidate-visibility-probe`、`native-caret-probe`、`native-caret-early-probe`、`scroll-edit-diagnostic`、`ime-control-position-probe`）。
- 不模拟鼠标点击、不 TOPMOST/AttachThreadInput 抢前台；启动时单次普通 Focus + 等待用户点击。
- UI 线程只做只读核对（活动 profile/COM 套间如实记录）；脚本线程才初始化 COM 做 TSF 查询。

## 证据文件

`ime-host-check.jsonl`（所有事件：环境、会话、IME 模式含 method=imm/shift-key、每条原生消息、绑定写入、锚点计数）
+ `s1-preedit.png` / `s6-composing-before.png` / `s6-composing-moved.png` / `s7a-composing.png` / `s7b-small-scroll.png` / `s7c-scrolled-out.png` / `s8-breakpoint.png`。
API 调用计数只算发布证据；候选窗实际可见/跟随须人工核对截图，不宣称通过。
