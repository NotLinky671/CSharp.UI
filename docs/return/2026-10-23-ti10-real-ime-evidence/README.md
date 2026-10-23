# TI-10 真实输入法证据（2026-10-23）

## 运行方式

`dotnet run --project tools/CSharp.UI.ImeHostCheck --no-build -c Release -- --out <dir> --tsf --profile mspinyin --scenario <name> --timeout-ms 120000 --foreground-wait-ms 90000`

每个场景一次独立进程；工具用 `SendInput` 扫描码驱动真实微软拼音（TSF opt-in，`tsf_opt_in:true`）。运行前已请求操作者不要操作键盘鼠标，但运行期间是否有人为输入无法由日志证明；结果全部 pass 只能说明没有造成可检测的失败。

## 结果（18 个场景，退出码均为 0，`exitcodes.txt`）

| 场景 | 工具判定 | 关键字段 |
|---|---|---|
| nihao-commit | pass | 最终文本"你好"，绑定写入 1 |
| escape-cancel | pass | 宿主存活，文本不变，写入增量 0 |
| enter-select | pass | Enter 确认的是原始拼音"nihao"（`enter_selected_candidate:false`，本机微软拼音行为），写入 1，无换行或提交（`no_newline_or_submit:true`） |
| segmented | pass | 最终文本"你好世界"，写入 2 |
| digit-select | pass | 最终文本"你好"，写入 1 |
| undo-redo | pass | 最终文本"你好" |
| minimize-restore / hide-restore | pass | `zero_writes_while_hidden`、`restore_commit_ok`、`reestablished` 均为 true |
| window-deactivate | pass | `zero_writes_while_away`、`restore_commit_ok`、`reestablished` 均为 true |
| focus-switch-cancel / external-update-cancel / close-window | pass | 绑定写入 0（external-update-cancel 的最终文本是外部写入内容） |
| breakpoint-reflow / container-scroll / horizontal-scroll / resize-samebranch / disabled-unmount | pass | 结尾 `failures` 为空，回调异常 0，恢复失败 0 |
| anchor-follow | pass | 窗口移动后锚点缓存清空并重新发布（`republished:true`、`publish_count_delta:1`），同一组合（`identity_same_composition:true`），移动期间写入不变，恰好提交一次 |

## 已人工查看的截图

15 张 PNG（13 张原始 + 重跑目录 `anchor-follow-rerun` 的 2 张）中，以下 6 张已确认亲眼看过图像内容：

- `nihao-commit/s1-preedit.png`：预编辑"ni'hao"带下划线，下方系统微软拼音候选窗可见（1 你好 … 9 尼，第 1 页）。
- `breakpoint-reflow/s8-breakpoint.png`：窄窗口纵向排列，两个输入框均为空，没有预编辑和候选窗；与日志一致（`wide-hstack` 切到 `narrow-vstack`，旧会话已取消，重排后重新建立）。
- `container-scroll/s7c-scrolled-out.png`：输入框滚出可视区后窗口为空白，没有残留的候选窗。
- `container-scroll/s7d-restored.png`：滚回后预编辑与候选窗恢复显示。
- `anchor-follow-rerun/s6-composing-before.png`、`anchor-follow-rerun/s6-composing-moved.png`：移动前后都显示预编辑"ni'hao"和完整的候选窗（1 你好 … 9 尼，第 1 页），候选窗相对输入框的位置一致。两张图都是以窗口为单位截取，所以相对位置一致是预期；窗口真的移动过由日志的 `rect_before`/`rect_after` 证明，不是由截图证明。

另有一张 822 像素宽的预编辑加候选窗截图，我无法确定它对应 `window-deactivate`、`horizontal-scroll`、`resize-samebranch` 中的哪一个，因此不作为这三个场景的视觉证据。

其余 9 张（`minimize-restore`、`hide-restore`、`window-deactivate`、`horizontal-scroll`、`resize-samebranch` 各 1 张，原始 `anchor-follow` 2 张，`container-scroll/s7a`、`s7b`）：本记录**没有**确认查看过图像，只有日志字段作为依据。

## anchor-follow 重跑（补位移量证据）

原始 18 个场景运行时，`anchor-follow` 只记录了移动前的窗口矩形。之后给 `ImeHostCheck` 增加了移动后矩形与 `window_moved_by_60_60` 两个只读记录字段（不改通过判据），重跑一次，输出在 `anchor-follow-rerun/`：

- `rect_before` = `342,414,742x1016`，`rect_after` = `402,474,742x1016`，`window_moved_by_60_60` = true：窗口位置确实移动了 (+60, +60)，大小不变。
- `republished`、`identity_same_composition`、`writes_unchanged_during_move`、`commit_exactly_once` 均为 true，工具判定 pass，退出码 0。
- 重跑使用的是修改后的工具；原始 18 个场景（含原始 `anchor-follow`）由修改前的工具生成，不重写。

## 不能由本证据得出的结论

- 原始 `anchor-follow` 日志没有 `rect_after`；位移量只由重跑的日志证明。
- 输入法身份依赖工具自己的 `--profile mspinyin` 校验；本记录没有单独核对 CLSID。
- 环境：本机单显示器，DPI 缩放 150%（截图与冒烟日志为 1.5）；其它 DPI、其它输入法、macOS、Linux 没有证据。
- 日志中含本机路径 `C:\Users\nothh\...`（与仓库内既有证据文件一致，未改写，以保持哈希可复核）。
- `SHA256SUMS.txt` 为本目录 JSONL 与 PNG 的哈希。
