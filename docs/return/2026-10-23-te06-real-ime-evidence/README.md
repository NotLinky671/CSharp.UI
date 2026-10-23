# TE-06 真实输入法验收证据(Windows IMM + 微软拼音)

日期:2026-10-23(第三轮修正跑;此前两版断言/驱动不合格已按主审意见修正)。环境:Windows 10.0.26200,DPI 144(150%),默认 IMM 桥(`UseTsfInputBridge` 未启用)。宿主 `ExitOnEscape=false`,Esc 不会把宿主关掉(此前导致 S8 前段静默退出)。本目录原始文件(events.jsonl + PNG)的 SHA-256 原始字节散列见 `SHA256SUMS.txt`,目录级 `.gitattributes` `* -text` 保护原始 bytes。

## 输入法身份(非按参数猜)

`ITfInputProcessorProfileMgr.GetActiveProfile` 实际回读(`S0-profile-identity`):

- CLSID `81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E`(微软拼音 TSF TIP)
- Profile `FA550B04-5AD7-411F-A5AC-CA038EC515D7`

驱动仅 `ActivateProfile(FORPROCESS|ENABLE)` 限定本进程;`finally` 中 `DeactivateProfile`+恢复原 profile;HIMC 转换句+句子模式保存/恢复。不改系统默认输入法。

## 场景与结论(全部 check 通过,outcome=PASS,app-exit=0)

| 场景 | 真实驱动 | 断言(固定预期,不按 actual 塑 expected) | 结果 |
|---|---|---|---|
| S0 | GetActiveProfile 回读 | CLSID+GUID == mspinyin | PASS |
| S1 | 点击第二行 → `nihao` | 预编辑 `'ni'hao'` 上屏;0 绑定写;锚点 (101,83) | PASS |
| S2 | `nihao` 后 Space | `AA\nB你好B\nCC` byte-exact;setter delta=1 | PASS |
| S3 | `shijie` 后 Esc | 组合活动→Esc 取消;0 写;宿主存活 | PASS |
| S4 | 真实拖拽选 `[1,5)` `'A\nBB'`(跨行)→ `nihao`+Space | 选区 `[1,5)` 实读;`selected='A\nBB'` 含换行;最终 `A你好\nCC` byte-exact;setter delta=1 | PASS |
| S5 | 组合中外部 CRLF 写 + `zaijian` | 外部写取消组合;新 epoch 3≠2;`A你好再见\nCC\r\n外部` 单次写 | PASS |
| S6 | 组合中 deactivate/activate + `huifu` | deactivation 0 写;`A恢复你好再见\nCC\r\n外部` 单次写 | PASS |
| S7 | 组合中 SetWindowPos +120× | publishCount 59→61;锚点逻辑值不变(caret 未动);组合保持 | PASS |
| S8 | 组合中 2× WM_MOUSEWHEEL(消息级注入,同 wndproc→GLFW→adapter→DispatchWheel 真实路径) | 见下方快照 | PASS |

### S8 快照(三次 UI 线程原子读取:A 轮前 / B 轮+ProcessPendingWork / C 再一帧)

| 快照 | scrollY | caret | caretDoc Y | caretScreenY | anchor(client) | publish | wheelDelivered | composing |
|---|---|---|---|---|---|---|---|---|
| A | 0 | 43 | 111.7 | 129.7 | (132, 223) | 70 | 0 | true |
| B | 96 | 43 | 111.7 | 33.7 | (132, 79) | 71 | 2 | true |
| C | 96 | 43 | 111.7 | 33.7 | (132, 79) | 71 | 2 | true |

结论:

- **滚轮真实送达**:`WheelEventCount` 0→2(此前 `SendInput MOUSEEVENTF_WHEEL` 连续两轮到不了 adapter,改用 `PostMessage(WM_MOUSEWHEEL)` 消息级注入后确定送达——同一窗口过程→GLFW→`HandleWheel`→`DispatchWheel` 路径)。
- **ScrollY 持久变化 0→96**,非 EnsureCaretVisible 回弹(caret 屏幕 y=33.7 仍在 innerH=149 内,B→C 不回弹)。
- **锚点几何已按 `TextOrigin−ScrollY` 重发**:publish 70→71,anchor (132,223)→(132,79),Δy=−144 px = −96 逻辑 × DPI 1.5,与滚动完全同步。组合保持、0 次绑定写。

### 修正记录(相对前两版)

- **S4**:弃 Shift+Right(mspinyin 把 Shift 当中英切换吃掉,选区从未建立,此前 `[6,6)` 是假绿),改真实拖拽 `GetCaretBounds(1)→(5)`,断言固定 `[1,5)` 与固定期望 `A你好\nCC`。方向/Home/End 用 `KEYEVENTF_EXTENDEDKEY`。
- **S2**:由弱断言改为固定完整文本 `AA\nB你好B\nCC` byte-exact。
- **S7**:由"锚点值变化"改为"publish 计数增加"(caret 未动时锚点逻辑值可不变)。
- **S8**:`SendInput` 滚轮实际投递为 0(adapter 计数证明),改用 `PostMessage(WM_MOUSEWHEEL)`;caret 置于视口中部(caretScreenY 129→33 不越界)保证 ScrollY 变化不被 EnsureCaretVisible 抵消;三快照(轮前/轮+ProcessPendingWork/再一帧)同时读取 scrollY、caret、GetCaretBounds、innerH、anchor、publish、wheelDelivered。
- **宿主**:`ExitOnEscape=false`(此前 S8 开头收束 Esc 触发默认退出);`Started` 回调修复。

## 边界

- 单 DPI(150%)、单窗口、单 profile 会话;多 DPI/多显示器/macOS/Linux 真机 IME 不在本证据范围。
- 系统候选窗枚举已记录;候选跟随屏幕位置属平台窗口管理。
- 分段 Result 的原生多段消息未构造;分段历史由 `CompositionContracts`/`TextEditHistory` 单元测试覆盖。
- `PostMessage` 属于消息级注入(非原始 HID 输入),但消息是真实 WM_MOUSEWHEEL,经宿主真实 wndproc/GLFW/DeferredWheel 管线;`SendInput` 滚轮在此环境反复不可投递(adapter 计数 0),已记录。
