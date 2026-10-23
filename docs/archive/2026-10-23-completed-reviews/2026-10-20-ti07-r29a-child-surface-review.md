# TI-07 R29A 子窗口宿主审核

审核日期：2026-10-20。状态：不通过，等待 DSH 完成首轮并返修。

本次审核针对正在实现的隔离宿主，不代表最终源码。Codex 已对当前可编译二进制执行独立真实 Win32 几何审核，25/28 通过。未创建组合、未发送输入；自有测试根窗口使用 SW_SHOWNOACTIVATE 显示，外部前台和焦点保持不变，全部自有 HWND 已销毁。

证据：[独立原始输出](2026-10-20-ti07-tsf-prototype-review-evidence/r29a-interim-native-audit/audit.log)。同目录保留审核源码、原生坐标 oracle、当时宿主源码与被测二进制哈希。

## 已复现的问题

1. `DisplaySurface.FromChildIntersection` 只考虑直接 viewport，遗漏根窗口裁剪。把自有 viewport 移到根客户区底部后，真实可见区域为 `(104,482)-(816,492)`，查询却返回 `(104,482)-(816,537)`。实际输入客户区必须与所有裁剪祖先客户区求交，不能只比较直接父窗口。
2. 完全裁出时 `GetScreenExt` 返回 `(104,111)-(816,71)`，并把这个倒置矩形发布为当前布局 clip。契约需要全零矩形 `{0,0,0,0}`。共享求交函数需归一化空区域，真实原生查询和布局均使用它。

## 源码审核发现，最终版本需复核

- `InitializeInputFocus` 在 Invoke 外读取/检查前台和 GetFocus，再排队 SetFocus；存在工作线程焦点读取错误及排队期间失焦仍设置焦点的竞态。必须在同一次 UI 线程调度内检查真实窗口存在、当前进程/UI 线程归属、前台，再决定设置焦点。重复调用也不能在 worker 上读 GetFocus。
- WndProc 的 `isInput` 只在 opt-in 成立，默认 off 时 WM_SETFOCUS/KILLFOCUS 不再调用原有 session.OnWindowFocus；这是默认路径回归。opt-in 根窗口也不应继续路由输入 child 的 KEY/CHAR。
- opt-in 根窗口仍调用完整 Paint 绘制相同文字，容易出现根与 child 重复文本；viewport 的 WM_PAINT 未执行 BeginPaint/EndPaint，可能保留未验证更新区域。根/viewport只绘制各自背景，文字仅 input 绘制，更新应失效实际输入表面。
- 实时 surface 查询失败时 BuildAndPublishLayout 重用旧 `_clientScreen` 并发布当前 generation，可能使失败几何伪装为有效新布局。失败应保留失效/错误，不发布旧坐标成功。
- 根尺寸和 DPI 变化后，需同步真实 viewport/input 宽高、单行字体行高和布局；不能仅初始化时计算。
- 新增 `IntersectSurface` 若仅用于测试而真实路径另行实现，测试无法覆盖接线缺陷。测试应使用实际共用实现。

实际 scroll/restore 已由原生 oracle 确认 input 和 row 精确上移 11 物理像素，没有二次扣减；这只证明几何，尚未证明微软拼音候选跟随或隐藏。真实输入验收保持未放行，TI-07 未完成。

## DSH 首轮结束后的最终源码复核

DSH runner83458 正常 end_turn/exit0。Codex 从当前源码独立重建 Debug 并执行正式测试，304/304 通过，但真实原生审核为 **28/32**：遗漏根裁剪、完全裁出 surface 非全零、布局 clip 非全零、viewport WM_PAINT 未验证更新区域，四项失败。证据：[重建后原始输出](2026-10-20-ti07-tsf-prototype-review-evidence/r29a-rebuilt-baseline/audit.log)。其余源码发现仍未修正，尤其默认焦点路由丢失及初始焦点检查不在同一次 UI 调度内。

补充：首轮结束后第一次扩展审核使用的 Debug 工具 DLL 早于最终 resize helper，26/32 中两项 resize 失败属于旧二进制结果，不能指控最终源码。Codex 重新构建后 resize 两项通过；最终按 28/32 记录，其余四项仍复现。DSH 报告的“默认行为逐字节不变”“无交集返回 zero-RECT”“初始焦点在 UI 线程内”与实际源码不符，需修正实现及报告。

下一轮精确返修任务：`artifacts/ime-work/ti07-dsh-r29a-review-fix.md`，仍只修改隔离宿主及对应测试；真实候选输入测试继续不放行。

## 最终焦点修复及回归复核

runner24791 已正常 end_turn/exit0。当前源码的 InitializeInputFocus 在同一次 UI Invoke 内读取原生事实、执行受条件约束的 SetFocus、确认结果并发布初始化状态，消除了此前 worker 后置赋值的竞态。

Codex 独立完整回归输出已生成于 `2026-10-20-ti07-tsf-prototype-review-evidence/codex-r29a-focus-final/`：Debug/Release 各 318/318 正式测试、82/82 自检，四次默认隐藏宿主运行，以及独立生命周期 38/38 托管检查、9/9 隐藏原生接线检查通过。另行逐项复核两份 baseline：保护文件及生产源码 SHA256 均未变化。未取得原脚本最终进程退出状态，以上结论依据各步骤实际日志及独立 hash 复核。

上一轮相同几何实现的 PMv2 原生审核为 34/34、实际 DPI144，证据 `r29a-pmv2-native-audit/`；最终状态发布修改后的重跑尚未执行。请求执行最终隐藏几何审核时，自动审批连接中断（tls: bad record MAC），工具明确报告命令未执行。这是审批服务失败，不是测试失败，也不是动作被判定不安全。不得绕过审批。

R29B 场景接线仍未派发；真实微软拼音候选跟随及全裁剪隐藏尚未验收。TI-07 继续未完成，不提交或推送。

## 审批恢复后的最终审核

最终隐藏几何审核已重新执行，退出 0，实际 PerMonitorV2/DPI144 下 **34/34 通过**。证据见 `2026-10-20-ti07-tsf-prototype-review-evidence/r29a-final-native-audit/`。因此放行 R29B 场景接线实现，任务见 `docs/plan/2026-10-20-ti07-child-input-surface-scenarios-task.md`。候选行为仍未验证，不能把宿主几何通过视为 TI-07 通过。

## 首轮返修结果：几何通过，初始焦点仍需返修

runner96894 已 end_turn；实现报告 309 项测试、82 自检。Codex 复核最终源码与已更新二进制后，真实 Win32 独立审核 **33/33** 通过，含根最小化、全祖先裁剪、全零矩形、viewport 更新区域验证、精确 11px 移动、恢复和 resize。证据：[原始输出](2026-10-20-ti07-tsf-prototype-review-evidence/r29a-after-review-fix/audit.log)。所有窗口销毁，外部前台/焦点保持，无输入。

仍不能放行：`InitializeInputFocus` 把检查放进第一个 Invoke，再通过第二个 Invoke 无条件 SetFocus，中间仍能失去前台；重复调用分支仍在调用线程直接 GetFocus，也未验证 PID/TID 必须等于当前进程/实际 UI 线程。DSH 的 D3 报告同时声称“一次 Invoke”和“第二个 Invoke”，与要求及实际安全性不符。下一轮只修这一处真实控制流及对应测试，任务 `artifacts/ime-work/ti07-dsh-r29a-focus-final-followup-draft.md`（虽文件名保留 draft，内容已确认可派发）。真实候选跟随/隐藏均尚未重新验收，TI-07 未完成。
