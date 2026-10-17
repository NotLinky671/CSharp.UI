# CSharp.UI.ImeProbe (TI-01 Windows IME feasibility probe)

独立的最小探针，用于回答 TI-01 的问题：当前宿主（OpenTK 4.9.4 + GLFW 3.4）能否安全接入真实输入法的
预编辑、结果提交、取消与系统候选窗锚点。它**不引用生产框架**，只子类化自己创建的窗口，不修改
`src/` 下任何生产代码。

## 安全边界（必须保持）

- 只操作探针自己创建的 GLFW 窗口；`SendInput` 前确认前台窗口就是本窗口，失去前台立即中止。
- 不合成任何 `WM_IME_*` 消息；合成项只在日志里显式标注 `synthetic`。
- 截图只抓自有可见窗口矩形，不做任意 margin 扩张，也从不抓整个桌面；窗口外候选需要证据时必须先确证其归属与边界。
- 不读用户日常输入、不操作 DSH/其他应用。
- 临时启用已安装输入法只在显式 `--temporary-enable-mspinyin on` 下发生：先快照原活动 profile 与目标
  profile 的启用状态（状态位 `0x2 = TF_IPP_FLAG_ENABLED`，与 `ActivateProfile` 的参数位不是同一枚举），
  再以 `FORPROCESS|ENABLEPROFILE` 激活（**不用 FORSESSION、不改默认输入法**）。恢复义务在任何原生变更
  尝试之前建立：无论激活失败、部分成功还是日志抛异常，都会在 `finally`/`Dispose` 里
  `DeactivateProfile(FORPROCESS|DISABLEPROFILE)` 并恢复原活动 profile，最后用
  `GetProfile`/`GetActiveProfile` 回读校验（日志 `temporary-profile-restored` 的 `RESTORED` 只在回读匹配时为 true）。

## 构建（串行，勿与其他工程并行构建）

```powershell
dotnet build tools\CSharp.UI.ImeProbe\CSharp.UI.ImeProbe.csproj -c Debug -m:1 -nodeReuse:false
dotnet build tests\CSharp.UI.ImeProbe.Tests\CSharp.UI.ImeProbe.Tests.csproj -c Debug -m:1 -nodeReuse:false
dotnet run --project tests\CSharp.UI.ImeProbe.Tests -c Debug --no-build
```

`tests/CSharp.UI.ImeProbe.Tests` 覆盖 GCS 标志过滤、组合提交判据（`CompositionObservation`）、提交内容判定
（`CommitOutcome`：纯 ASCII 回吐不算中文选词；固定串核对明确预期内容；拼音只从本次组合预编辑归因）、
候选锚点判定（`CandidateAnchorOutcome`：API 往返最高只到 OBSERVED）、身份目标匹配（`ProbeTargetProfile`：
mspinyin / wetype / current 各自语义）、`--warmup ctrl-space` 必须被拒绝、候选列表解析、INPUT 结构布局、
PNG 编码、日志顺序，以及受控临时 profile 的状态位语义与恢复义务；
最终86项经Codex独立执行通过，0失败、0跳过。包含逐结果预编辑快照、冻结后历史重置、同消息后续预编辑隔离，以及场景结果切片的回归。
复跑命令（不使用 `dotnet test`，它在本沙箱下依赖命名管道）：

```powershell
dotnet exec tests\CSharp.UI.ImeProbe.Tests\bin\Debug\net10.0\CSharp.UI.ImeProbe.Tests.dll
```
其中 ProbeLog 的线程字段断言按平台区分：Windows 上记录真实 Win32 线程 ID，非 Windows 上显式写 0
且不调用 kernel32（因此该测试在非 Windows 也只是纯托管 CPU 测试）。profile 管理测试全部使用注入的
假后端，不触碰真实输入法配置。

## 运行

```powershell
$exe = '.\tools\CSharp.UI.ImeProbe\bin\Debug\net10.0\imeprobe.exe'

# 环境/身份快照（不注入按键）
& $exe --scenario info --capture off --out artifacts\ime-work\probe-info

# 完整场景（含组合/取消/焦点/resize/普通英文/非 BMP/故障注入/关闭）
& $exe --scenario all --input-mode batch --capture on `
       --duration 300 --out artifacts\ime-work\probe-all

# 只验证一次真实提交（nihao -> 空格）
& $exe --scenario ime-commit --input-mode batch --capture off --out artifacts\ime-work\probe-commit

# 逐键都泵消息的对照（TSF 输入法键处理可能依赖消息循环）
& $exe --scenario ime-commit --input-mode pumped --capture off --out artifacts\ime-work\probe-commit-pumped

# 真实扫描码对照（仍是真实键盘事件；只用于后续微软拼音驱动对比，不合成 WM_IME）
& $exe --scenario ime-commit --input-mode scancode --capture off --out artifacts\ime-work\probe-commit-scancode

# 受控临时启用微软拼音（会先快照、结束恢复并回读校验）
& $exe --scenario ime-commit --temporary-enable-mspinyin on --input-mode batch `
       --capture off --out artifacts\ime-work\probe-mspinyin

# 恢复失败边界（单独进程；会在 WM_NCDESTROY 报告恢复失败且不虚报成功）
& $exe --scenario fault-restore --capture off --out artifacts\ime-work\probe-fault-restore

# 候选锚点：自有窗口内部两个锚点各截一张“同一窗口矩形”的图（人工核对候选是否可见/移动）
& $exe --scenario ime-candidate --profile mspinyin --temporary-enable-mspinyin on `
       --input-mode scancode --capture on --out artifacts\ime-work\probe-candidate

# 前置条件：后台环境里窗口不会自动获得前台。这里显式等待用户把窗口点到前台（只等待，不争抢）
& $exe --scenario ime-commit --profile mspinyin --input-mode scancode --capture off `
       --foreground-wait 120000 --duration 300 --out artifacts\ime-work\probe-commit-attended
```

关键选项：`--scenario`（含新增 `ime-candidate`）、`--input-mode batch|pumped|scancode`、
`--temporary-enable-mspinyin on|off`、`--profile current|mspinyin|wetype`（决定身份核对的目标）、
`--capture on|off`、`--warmup none|shift`、`--fault-inject none|message|log`、`--keys`、`--second-keys`、
`--duration`、`--settle`、`--foreground-wait <ms>`（默认 8000；只等待、不争抢前台，超时即跳过该场景）。

`--warmup ctrl-space` **被明确拒绝**（解析阶段即报错）：组合键在第二次 keydown 之后若失去前台，
`SendRaw` 依保护规则会拒绝发送 key-up，“失败也恢复键态”无法保证；探针不为这个可选预热新建可注入后端。

## 结果语义（严格区分，不可互相顶替）

| 状态 | 含义 |
|---|---|
| `PASS` | 本场景的**明确断言**成立（例如恰好一次 `GCS_RESULTSTR` 提交**且提交内容等于明确预期内容并可由本次组合预编辑归因**、非 BMP 合成边界产生单个 2 码元 TextInput）。 |
| `OBSERVED` | 观察到了消息链/API 行为，但端到端验收语义未成立（例如锚点 API 往返成功但系统候选窗实际位置未独立观测；关闭发生在未组合时；重复通知来自合成消息）。 |
| `UNVERIFIED` | 缺少输入条件，未伪造（例如真实输入法没有产生组合）。 |
| `SKIP` | 前置条件缺失（前台/输入法/布局）。 |
| `FAIL` | 探针基础设施故障或断言被违反。 |

探针 `exit 0` 只表示没有 `FAIL`，**不代表 TI-01 的微软拼音门槛通过**；真实 IME 的预编辑/单次结果/
取消/候选锚点必须由相应场景达到 `PASS` 或由人工证据补齐。

### 提交内容必须与“段数正确”分开判定

真实证据里出现过 `GCS_RESULTSTR="niha"`（纯 ASCII，失焦时把未转换的预编辑串回吐）这种形状：
一次带 `GCS_RESULTSTR` 标志的提交，但**不是**空格选词得到的“你好”。因此 `ime-commit`/`ime-segmented`
除段数外还核对内容（`CommitOutcome`），判据是三条：

1. **固定测试串用明确预期内容**：`nihao` 要求结果等于 `你好`（`ni`→`你`、`ma`→`吗`）。不用“含非 ASCII”
   一概代替中文——`é`、全角标点都是非 ASCII，不能算正确的中文选词。
2. **必须由本次组合会话归因**：注入的拼音（规范化后忽略 `'` 等音节分隔符）要出现在**本会话**的预编辑历史里；
   否则结果可能来自旧会话，判为不可归因。
3. 纯 ASCII 回吐判 `UNVERIFIED` 并写明原因，不会被写成 `PASS`。没有固定预期内容的输入退化为
   “非 ASCII + 本会话归因”。美国键盘布局路径（`latin`/`nonbmp`/`fault`）不要求中文，避免误判。

### 初始化顺序与现场身份佐证（假设，非根因结论）

初始化顺序是：**获焦并泵 -> 激活临时 profile -> 再泵 -> 在真实目标 HIMC 上设 native/open -> 再泵并回读**。
上一版把 `SetNativeImeMode` 放在 profile 激活之前；本轮把它后移，并在设置后立即回读 open/conversion
（`native-mode-readback` 场景，只认回读值）。这只是被显式标注的**假设**（可能被异步 profile/上下文切换覆盖），
必须靠前后对照数据判断，日志里不写“根因已证实”。

同时在场景起点/结束、首次真实组合、以及每次 `result` 时刻记录只读身份快照
（`kind=identity-snapshot`：`GetActiveProfile` 的 CLSID/ProfileGuid/类型/语言/flags，外加同一时刻 IMM 的
open/conversion）。窗口过程回调里**只登记标签**（连同当时的 `seq` 与 `compositionId`），真正的 COM 查询在
消息返回、回调退出后由事件泵执行（无递归泵、无在回调里新建窗口）；日志把
`registered_at_event_seq`/`registered_composition_id` 与 `queried_at_event_seq` 分开写，**不把晚查到的值
写成“事件瞬间的身份”**，两者之间的间隔可能发生 profile 切换，审核时可据此判断。

身份核对的**目标**由启动参数决定（`ProbeTargetProfile`）：`--temporary-enable-mspinyin on` 或
`--profile mspinyin` → 微软拼音；`--profile wetype` → 微信输入法；`--profile current` → 本进程启动时
实际活动的那一个（含键盘布局，按 CLSID/ProfileGuid 比对）。不再永远把“是否微软拼音”当成唯一成功判据。

候选锚点：`PublishCandidateAnchor` 不再把 `CFS_CANDIDATEPOS | CFS_EXCLUDE` 按位或——
`CANDIDATEFORM.dwStyle` 是互斥取值，需要排除区域时单独发一次 `CFS_EXCLUDE`。
`IMR_QUERYCHARPOSITION` 是**组合串字符坐标**查询（不是重转换查询）：当前使用系统默认组合 UI，由默认 UI 处理，
探针未实现并转给默认过程，但这**不构成**“系统候选锚点不可用”的证据；本轮不实现更大的字符几何。

### 前台是硬前置条件，探针不争抢

`SendInput` 前必须确认探针窗口就是前台窗口。后台环境（例如另一个全屏应用占着前台）下 Windows 前台锁定
不允许探针自行夺取前台：探针只做**一次**有界尝试，然后最多每约 2 秒试一次，直到 `--foreground-wait` 超时，
就按 `SKIP` 跳过该场景，绝不把按键发给任何其他窗口。真实按键场景因此需要用户把探针窗口点到前台。

## 已知环境依赖（Windows）

真实 TSF 输入法（微软拼音、微信输入法等）在 IMM32 应用里工作依赖系统的 CTF/CUAS 宿主
（`ctfmon.exe`）只是一个**未验证假设**：13:16 由正式入口
`--scenario ime-commit --profile wetype --input-mode pumped` 取得过真实结果，因此不能把
“`ctfmon` 缺失必然导致 `ActivateProfile(FORPROCESS|ENABLEPROFILE)` 返回 `0x80004005` 且输入法不拦键”
写成已证实的因果关系。探针只如实记录 `E_FAIL`/`S_FALSE` 与 `UNVERIFIED`，不伪造通过；
需要判定环境条件时，请用相同入口/参数/时刻做对照复跑，而不是把单次失败归因于平台。
证据与判断见 `docs/agents-notebook/2026-10-17-ime-platform-decision.md`。

DSH 本轮独立只读入口连跑三次均观察到活动 WeType（`flags=3`）、微软拼音 `GetProfile flags=0`。
其激活尝试返回 `0x80004005`，探针记录 `FAIL` 并完成恢复回读（`RESTORED=true`）。
这不能证明禁用状态导致激活失败：Codex 经系统权限审批的受控运行曾从微软拼音 flags=0 成功临时启用，
核对实际微软拼音 flags=3 后再恢复。需要比较执行限制和输入上下文，不要求用户永久启用或修改默认输入法。
