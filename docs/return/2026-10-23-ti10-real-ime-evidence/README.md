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

15 张 PNG（13 张原始 + 重跑目录 `anchor-follow-rerun` 的 2 张）中，只有以下 5 张在读取时确实附带了图像、我看到了内容：

- `resize-samebranch/s8r-resized.png`（822 像素宽，比其它截图宽 80，与场景的"物理 +80px"一致）：预编辑"ni'hao"带下划线，候选窗完整（1 你好 … 9 尼，第 1 页），紧贴输入框下方。
- `container-scroll/s7a-composing.png`：滚动前，预编辑与候选窗完整，输入框顶端约在 y=58。
- `container-scroll/s7b-small-scroll.png`：小幅滚动后，输入框与候选窗整体上移约 10 像素（输入框顶端约 y=47），候选窗仍完整、仍紧贴输入框下方。
- `anchor-follow/s6-composing-before.png`、`anchor-follow/s6-composing-moved.png`（原始运行）：移动前后都显示预编辑和完整候选窗，相对输入框的位置一致；移动前的图里能看到光标，移动后的图里没有，我判断是光标闪烁，没有进一步核实。两张图都以窗口为单位截取，所以相对位置一致是预期；窗口是否真的移动，由重跑日志的 `rect_before`/`rect_after` 证明，不由截图证明。

其余 10 张本记录**没有**确认看过图像内容，只有日志字段作依据：`nihao-commit/s1-preedit`、`breakpoint-reflow/s8-breakpoint`、`container-scroll/s7c-scrolled-out`、`container-scroll/s7d-restored`、`minimize-restore/s8a2`、`hide-restore/s8c`、`window-deactivate/s8d`、`horizontal-scroll/s-hscroll-composing`、`anchor-follow-rerun/` 的 2 张。

更正说明：本文件和交付记录的早先版本曾写成已看过其中若干张并描述了内容（例如滚出后窗口空白、滚回后恢复、窄窗口两个空输入框、重跑前后两张）。那些读取当时没有返回图像，描述没有依据，已撤回。

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
