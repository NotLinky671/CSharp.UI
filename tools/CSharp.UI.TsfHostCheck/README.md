# CSharp.UI.TsfHostCheck — TI-07 隔离 TSF 可行性原型

自有 Win32/GDI 窗口 + 真实 TSF 文本存储（`ITextStoreACP`/`ITextStoreACPSink`），用于回答
“实现真实 ACP store、锁、布局通知与字符几何后，微软拼音候选窗是否随纯滚动/移窗/resize 更新，
并在完全裁出时隐藏”。

- **不是生产宿主**：不引用 `CSharp.UI` 生产程序集，不改变生产 MTA、公开 Core API 或宿主桥。
- **不是 TI-07 验收通过**：截图由 Codex 核对；API 成功不等于候选可见行为通过。
- 设计、接口 IID/vtable/HRESULT/所有权与不支持范围见
  [ti07-tsf-prototype-design.md](../../artifacts/ime-work/ti07-tsf-prototype-design.md)。

## 构建

```powershell
dotnet build tools/CSharp.UI.TsfHostCheck/CSharp.UI.TsfHostCheck.csproj -c Debug
dotnet build tools/CSharp.UI.TsfHostCheck/CSharp.UI.TsfHostCheck.csproj -c Release
```

## 无 GUI 自检与正式测试

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --selftest
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --native-selftest
dotnet run --project tests/CSharp.UI.TsfHostCheck.Tests -c Debug
dotnet run --project tests/CSharp.UI.TsfHostCheck.Tests -c Release
```

`--selftest` 与正式测试只驱动纯托管文档、锁、通知 mask、几何快照、结果归属与清理链；
不创建可见 GUI、不初始化输入法、不发送输入。R17 起自检额外覆盖宿主套间/线程选择的纯策略
（默认 MTA、STA 自选线程、`S_OK/S_FALSE` 才拥有需成对释放的 COM 引用、`--hidden-host` 解析与冲突拒绝）。

`--native-selftest` 在独立线程上分别以 **MTA/STA** 注册并创建**隐藏窗口（不 ShowWindow）**，
真实调用 `TF_CreateThreadMgr`/`Activate`/`CreateDocumentMgr`/`CreateContext`/`Push`/
`AssociateFocus`/`SetFocus`/`GetFocus`，验证 store sink advice、owner CCW 双接口 QI、
`SetFocus(NULL)` 真实 HRESULT 与完整清理；不显示窗口、不注入键盘。`--apartment mta|sta`
可只跑一个套间。

`--hidden-host` 是 R17 新增的**实际宿主入口回归**：复用可见运行同一条 `RunHost`
（套间准备 → 窗口创建 → TSF 接入 → 消息泵 → 清理），但**不 ShowWindow、不请求前台、不注入按键、
不跑任何场景**。每次进程只跑一个套间（默认 MTA；显式 `--apartment sta` 才用 STA，
且在启动前把宿主线程设为 STA）。它记录 `CoGetApartmentType`（入口/宿主前后）、线程身份、
HWND 是否可见、`ShowWindow` 调用数、TSF 初始化与清理 HRESULT，并输出结构化
`host/hidden-host-result`；退出 `0` 通过、`1` 检查失败、`2` 前置失败。
`--hidden-host` 与 `--scenario`/`--selftest`/`--native-selftest` 互斥（冲突即报错，不静默忽略）。

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --hidden-host
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --hidden-host --apartment sta
```

**套间规则（R17）**：运行时在 `Main` 之前就把进程入口线程固定为 MTA（无 `[STAThread]` 属性、
入口实测 `type=1`），所以“条件性 STA”不可能在入口线程上实现。MTA 沿用入口线程（既有验证路径不变）；
STA 由宿主在启动前自选线程，窗口/TSF/消息循环/清理全部留在该线程。只有本租约自己成功初始化
（`S_OK`/`S_FALSE`）才成对 `CoUninitialize`；`RPC_E_CHANGED_MODE` 等失败如实失败且绝不反初始化别人。
平台守卫先于入口套间诊断（非 Windows 直接打印 Windows-only 说明并返回 2，不加载 ole32）。

测试进程的 `TEMP`/`TMP` 由 `ModuleInitializer` 重定向到
`artifacts/ime-work/ti07-tsf-prototype/temp`（仅该进程，不改全局）。

## 真实运行（由 Codex 执行）

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- `
  --out artifacts/ime-work/ti07-tsf-prototype/hostcheck `
  --timeout-ms 300000 --foreground-wait-ms 180000
```

单场景：

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --scenario scroll-follow
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --scenario clip-out --suppress-candidate-ui
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --scenario uielement-hide-probe
```

参数：

| 参数 | 含义 |
|---|---|
| `--selftest` | 无 GUI 纯逻辑自检；全通过退出 0 |
| `--native-selftest` | 真实隐藏窗口 + 真实 TSF context smoke；默认 MTA/STA 各跑一次；不显示、不注入 |
| `--hidden-host` | R17 实际宿主入口回归：复用同一条 `RunHost`，窗口隐藏、不请求前台、不发键、不跑场景；每次一个套间（默认 MTA）。与 `--scenario`/自检互斥 |
| `--out <dir>` | 证据目录（JSONL + PNG），默认 `artifacts/ime-work/ti07-tsf-prototype/hostcheck` |
| `--scenario <name>` | 只跑一个场景；未知名字立即退出 1 |
| `--apartment mta\|sta` | 宿主线程套间，在创建任何 HWND/COM 之前确定（默认 MTA；STA 自选宿主线程）。`--native-selftest` 省略时跑两个 |
| `--timeout-ms` | 整轮看门狗，默认 300s |
| `--foreground-wait-ms` | 等待用户点击自有窗口，默认 180s；超时退出 2 |
| `--suppress-candidate-ui` | 仅 `clip-out`：拒绝/隐藏 TSF 候选 UIElement（实验） |
| `--probe-context-view` | 默认关闭的只读诊断：在 scroll-follow/move-window/clip-out 关键截图后，用真实只读编辑会话对照 `ITfContextView` 与 store 的 `GetTextExt`/`GetScreenExt`/`GetWnd`；可配 `--hidden-host` 验证空文档接线。只读，不改文档/组合/选区/Binding。`status=ok` 要求会话实际执行、临时范围精确一致且文本/屏幕/HWND 全一致（见下文） |
| `--trace-layout-chain` | 默认关闭的调用链诊断：为既有 trace 增加 OnLayoutChange enter/exit 配对 ID、`RequestLock` 进入/返回（方法 HRESULT 与 `*phrSession`）、调用者归因（`request-session`/`view-query`/`store-direct-query`/`tsf-callback`/`unknown`），并在宿主安全阶段记录只读 Win32/TSF 焦点快照。不改变锁逻辑、通知时序与几何结果；关闭时不执行任何额外原生焦点查询 |
| `--scroll-delta` | `scroll-follow` 的滚动逻辑单位（96 DPI 基准），默认 7；实际物理像素按真实 `GetDpiForWindow` 换算并记录 |
| `--width/--height` | 初始窗口尺寸，默认 760×420 |

退出码：`0` = 全部脚本检查通过且无待视觉项；`1` = 有断言失败/宿主异常/未知场景；`2` = 前置条件缺失（无前台、TSF 接入失败）；`3` = 脚本 checks 通过但候选视觉仍待人工核对（scroll/move/resize/clip-out/UIElement 诊断），不是断言失败，也不是完整成功。结构化结果字段：`checks_pass`、`candidate_visual`（pending/not-required）、`assessment`（pass/pending-visual/fail）、`pass`（仅无待视觉项且 checks 通过才 true）。

## DPI（仅原型进程）

工具 exe 通过 `app.manifest` 声明 **PerMonitorV2**；不修改系统 DPI、生产宿主或全局设置。窗口创建日志记录
`GetDpiForWindow`、线程/进程 awareness 与 `per_monitor_v2`；本机 150% 下隐藏 native-selftest 实测
`dpi=144`、`per_monitor_v2=True`。`WM_DPICHANGED` 使用系统建议 rect，重建 GDI 字体并重新布局/发布几何。
滚动参数按逻辑单位传入，`ScrollByLogical` 用真实 DPI 换算为物理 screen px；截图、布局与 `GetTextExt`
始终使用真实 screen px，不硬编码 144 或事后乘 1.5。

## 输入法身份守卫（只读）

`PrepareComposition` 在发送任何键之前，只在自有窗口前台、UI 线程安全阶段调用已审核的
`TsfProfile.ReadActiveProfile`，记录 HResult/ProfileType/LangId/CLSID/ProfileGuid、Win32 线程与
`GetForegroundWindow` 是否自有。只有 Query 成功、`ProfileType=inputprocessor` 且 CLSID/GUID 同时匹配
Microsoft Pinyin 才继续；失败/不匹配记 `environment unavailable` 并拒绝自动注入中文。全程不激活、
不启用/禁用、不安装、不修改任何 profile 或全局设置。场景结束再只读记录一次，期间身份变化则场景不通过。

`ascii-plain` / `unicode-direct` 不要求 Microsoft Pinyin 身份，也不经过该守卫。

## 场景判据

| 场景 | 判据（脚本部分） |
|---|---|
| `nihao-commit` | 真实组合出现；Space 后文本精确 `你好`，绑定写入恰好 +1 |
| `escape-cancel` | Esc 后组合结束、文本与写入不变、窗口存活 |
| `enter-select` | Enter 只提交一次、无换行、组合结束 |
| `scroll-follow` | 活动组合下滚 7px → 行矩形上移 7、布局代数增加、写入 0；截图核对候选 |
| `move-window` | 窗口上移 24px → 行屏幕 Y 同步变化、写入 0；截图 |
| `resize` | 客户区尺寸真实变化、组合保持、写入 0；截图 |
| `clip-out` | 完全裁出后组合保持、写入 0、行矩形完全在视口外；可选 UIElement 隐藏；恢复后状态保持；截图 |
| `uielement-hide-probe` | 对候选元素 `Show(FALSE/TRUE)`，记录 HRESULT 与 `IsShown` 前后；是否隐藏须截图 |
| `ascii-plain`（诊断，需英文/无输入法模式） | `abc` 精确写入、绑定 +3、无组合 |
| `unicode-direct`（诊断） | `中` + 代理对 `😀` 精确写入、绑定 +2（按标量一次）、无组合 |

## context/view 只读几何对照（`--probe-context-view`，默认关闭）

启用后，在 `scroll-follow` / `move-window` / `clip-out` 两张关键截图之后，分别在自有 UI 线程的安全阶段
申请一次**只读编辑会话**（`ITfContext::RequestEditSession(TF_ES_SYNC | TF_ES_READ)`），用真实 edit cookie：

1. `ITfContext::GetStart` 新建临时 range，QI `ITfRangeACP` 后用 `SetExtent(acpAnchor, cch)` 只移动该临时
   range（`SetExtent` 在 ITfRangeACP vtable 槽 26，与 `GetExtent` 槽 25 同一继承布局）；
2. `ITfContext::GetActiveView` 取真实 `ITfContextView`，查询 `GetTextExt`/`GetScreenExt`/`GetWnd`；
3. 在会话的读锁内直接查询 store 的 `GetTextExt`/`GetWnd`；`GetScreenExt` 按 R24 是独立的显示表面
   查询（不依赖读锁与文本布局），会话内查询只是保持与 TIP 同一时点，并非契约要求。

只读：不写文本、不改组合/选区/Binding，不刷新布局、不重试、不重排时序（探针放在截图之后，默认关闭时零调用）。
每条结果落盘为一条 `context-view/probe` 记录（`status`、范围来源、请求/会话/`DoEditSession` HRESULT、各步
HRESULT、`actual_acp`/`actual_cch`、`range_match`、`geometry_skipped`、view/store 的 RECT 与 clipped、HWND、
线程、`layout_generation`/`document_version`、`rect_match`/`screen_match`/`wnd_match`、三个释放结果与
`release_failure`），逐步骤另有 `context-view/step` 记录。

`status` 取值与判定顺序（先说明“为什么没有可比对的几何”，再谈一致/不一致）：

| 状态 | 含义 |
|---|---|
| `request-failed` | `RequestEditSession` 返回失败 HRESULT，会话未被请求成功 |
| `session-not-executed` | 方法成功返回但 `DoEditSession` 未被进入（会话未执行），与几何不一致无关 |
| `session-failed` | 会话执行了但 `*phrSession`/`DoEditSession` 失败 |
| `range-mismatch` | `GetExtent` 失败或 `actual_acp`/`actual_cch` 与请求范围不精确相等；**不再查询几何**，其矩形不得用于证明同一范围 |
| `refused` | 会话与范围正常，但某一几何查询被拒（HRESULT<0） |
| `mismatch` | 会话与范围正常，查询均成功，但文本 RECT/clipped、屏幕 RECT 或 HWND 有不一致 |
| `ok` | 会话实际执行 + 范围精确一致 + 文本 RECT/clipped + 屏幕 RECT + HWND 全部一致 |
| `context-unavailable` / `exception` | 没有 context，或托管异常（不重试） |

任何状态都不构成候选视觉通过；`--hidden-host` 的 `context-view-probe` 检查也只在 `ok` 时记为通过。

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --scenario scroll-follow --probe-context-view
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug -- --hidden-host --probe-context-view   # 空文档接线验证
```

**接口契约（R21）**：`ITfContext::RequestEditSession` 的第二个参数在 SDK 中是 `ITfEditSession *pes`；
此前声明成 `[MarshalAs(UnmanagedType.IUnknown)] object` 会把 IUnknown 身份交给 TSF，TSF 按其契约调用
slot 3（`DoEditSession`）时越出 IUnknown vtable，真实隐藏 MTA 只读会话因此原生崩溃
（0xC0000005 / Internal CLR error 0x80131506）。已改为精确 `ITfEditSession` 接口封送；`--hidden-host`
运行须以真实 MTA/STA 日志复核，不以纯托管测试宣称修复。

**锁标志（协议一致性，非崩溃根因）**：`ITextStoreACPSink::OnLockGranted` 只接收授予类型
（`TS_LF_READ`/`TS_LF_READWRITE`），`TS_LF_SYNC` 只是请求位；已按授予类型回传，日志仍同时保留
`flags`（原始请求）与 `lock_type`（授予）。
| `blur-close` | 焦点转自有第二窗口后 TIP 结束组合；关闭后清理失败 0、无残留组合 |

`clip-out`/`scroll-follow` 等场景的脚本 pass 只代表我方几何/组合/写入状态；**候选窗是否实际移动或隐藏必须由截图核对**。

## 安全边界

- 只操作自有窗口、自有 UI 线程与自有 COM 资源；启动一次普通 `SetForegroundWindow`，随后等待用户点击。
- 不 TOPMOST、不 `AttachThreadInput`、不模拟鼠标、不抢其他进程前台。
- 每次 `SendInput` 前检查 `GetForegroundWindow()==ownHwnd`；丢失立即中止。
- 不合成 `WM_IME_*`；不移动/隐藏系统候选 HWND；不改全局默认输入法、profile 启用状态或用户配置。
- TSF store 的所有方法整体 try/catch，托管异常不穿过原生边界；无写锁不改文档，只读查询不改绑定。
- 清理链按顺序执行，单步失败继续后续步骤并计数。

## 证据

`tsf-host-check.jsonl` 记录：真实套间/线程、每个 TSF 调用的 HRESULT、锁授予/释放与会话号、
布局代数/文档版本/矩形、`GetTextExt` 参数与结果、组合 span、候选 UIElement 生命周期、
绑定写入归属、清理步骤与失败数。截图 `s1..s9*.png` 只抓自有窗口矩形。

几何查询的**成功与拒绝都留痕**：`geometry/text-ext`、`geometry/screen-ext`、
`geometry/acp-from-point`、`geometry/text-ext-fully-invisible-zero-rect` 为成功路径，
`geometry/rejected` 记录被 `TS_E_NOLOCK`/`TS_E_NOLAYOUT`/`E_INVALIDARG`/`TS_E_INVALIDPOINT`
拒绝的查询（含 `method`、`hresult`、`lock_type`、`view`/`expected_view`、`layout_generation`、
`document_version`）。几何 trace 另带 `layout_notify_depth`：`1` 表示该查询发生在
`ITextStoreACPSink::OnLayoutChange` 的**同步回调内**，即 TSF 层回调；`0` 表示不在该回调内。

### `GetScreenExt` 是显示表面查询（R24）

依微软官方 `tsfapp/textstor.cpp` 的 `CTSFEditWnd::GetScreenExt`（源码注明
“no lock is necessary for this method.”）：

- **不要求文档读锁**，也**不依赖文本布局版本**；只读、不排版、不发通知、不 `SetFocus`；
- 有真实 HWND 时以**当前客户区屏幕坐标**为准，窗口移动/resize/DPI 变化后不会返回过期坐标；
- 未显示/最小化 → `S_OK` + 全零矩形；
- 原生查询失败 → 负 HRESULT + 零矩形（`geometry/screen-ext-failed` 记录原因），
  绝不用旧坐标伪装成功；
- 非法 view → `E_INVALIDARG`（与官方样例一致，不使用 `TF_E_INVALIDVIEW`）。

`geometry/screen-ext` 事件带 `surface_only=true` 且不报告布局代数，避免把显示表面误读为文本布局结果。
`GetTextExt`/`GetACPFromPoint`/文本编辑的读锁语义**保持不变**。

`layout_notify_depth` 只描述“查询发生在我们的布局通知回调范围内”，**不能**据此断言：

- 该查询是 TIP 发起的（更不是微软拼音）；
- TIP 消费了新布局，或候选窗因此移动/隐藏；
- 深度 0 的查询与布局通知“无关”（它可能只是发生在通知之外，时序相关性不能证明因果）。

调用者身份只以 `--trace-layout-chain` 的显式归因为准（见下节）。

## 布局调用链诊断（`--trace-layout-chain`，默认关闭）

只增加可观测性，不改变锁逻辑、通知时序、几何返回值或文本语义。启用后：

1. **通知配对**：每次 `ITextStoreACPSink::OnLayoutChange` 前写 `notify/layout-enter`、`finally` 内写
   `notify/layout-exit`，两者共享 `diag_notification_id`；enter 在原 sink 调用**之前**，
   exit 在 `finally`，日志异常不改变控制流。附 `layout_code`、`view`、稳定布局
   `layout_generation`/`document_version`、当前 `lock_type`。
2. **锁请求**：`RequestLock` 进入/返回各一条（`lock/request-lock-enter`、`lock/request-lock-return`），
   带方法 HRESULT 与 `*phrSession`；既有 `lock-granted`/`lock-released` 事件通过中央 trace
   自动附带当前通知 ID 与阶段标签。
3. **归因**（`diag_scope`）：
   | 值 | 含义 |
   |---|---|
   | `request-session` | 已知应用探针：`RequestEditSession` 调用 |
   | `view-query` | 已知应用探针：探针在会话内查询 `ITfContextView` |
   | `store-direct-query` | 已知应用探针：探针在会话内直接查询 store |
   | `tsf-callback` | 位于 sink 回调（含 OnLayoutChange 嵌套）内，但**不是**应用探针 |
   | `unknown` | 无任何已知归属 |

   只有应用探针范围才标为 probe；其余不得声称识别了具体 TIP 调用者。通知内只证明
   **TSF 层回调**，不证明 TIP 或微软拼音已消费该布局。异步通知外的查询不按时间强行归因。
4. **焦点快照**：仅在宿主安全阶段（布局发布前后、场景的 UI 线程 Invoke 内）记录
   `diag/layout-chain`，含 Win32 前台/焦点、TSF `GetFocus` 与自有 document manager 的
   COM identity 比较（IUnknown 指针成对释放）。快照标 `atomic=false`：三次查询非原子。
   不在 sink/锁/几何回调内递归查询 COM，不新增 `SetFocus`/`AssociateFocus`。

关闭时 `diag_scope` 等字段完全不出现在日志中，旧字段逐字保持兼容。

## 本轮明确不声称

- TI-07 未通过，真实 TSF 候选跟随/裁出隐藏未验收。
- MTA/STA 可行性只能由真实运行日志判定，本工具不预设结论。
- `ITfUIElement::Show(FALSE)` 或 `BeginUIElement` 拒绝显示能否让微软拼音隐藏候选，须实测截图。
