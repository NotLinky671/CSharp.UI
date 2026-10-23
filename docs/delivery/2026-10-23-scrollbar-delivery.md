# Scrollbar（滚动条）交付记录

日期：2026-10-23

状态：SB-01~SB-07 已实现并经双配置构建/四套件测试/六示例冒烟/真实窗口 IME 与 GPU 像素级验收；交付记录即本文档。提交推送关联本次实现提交，见 Git 历史。

## 范围

为单轴 `ScrollView`（纵/横）与 `TextEditor`（纵）实现共享滚动条：内容溢出才显示、persistent 12 逻辑像素轨道槽（无溢出留槽）、滑块拖拽、轨道点击按视口翻页一次（无自动重复）、几何/范围变化取消手势、滚动条交互不夺焦点/不改 caret·选区·撤销历史·绑定；IME 组合期用户滚动为持久浏览意图（caret 裁出视口时输入几何 `Unavailable` 撤锚、组合保活，真实推进后重锚）。公开 API 零新增。

设计与任务：[滚动条计划](../plan/2026-10-23-scrollbar-plan.md)、[任务索引](../task/README.md)（SB-01~SB-07）、[审查记录](../return/2026-10-23-scrollbar-review.md)。

## 最终验证（双配置 fresh build，原始输出见 [evidence](2026-10-23-scrollbar-delivery-evidence/)）

构建命令：`dotnet build CSharp.UI.slnx -c <Debug|Release> --no-incremental --nologo -v minimal -m:1 -nodeReuse:false`
测试命令：`dotnet run --project tests/<工程> --no-build -c <Debug|Release>`（xUnit 进程内 runner）
示例命令：`dotnet build samples/<示例> -c <cfg> --no-incremental …`（fresh 标准 bin）+ `CSHARPUI_SMOKE_FRAMES=5 dotnet run --project samples/<示例> --no-build -c <cfg>`

| 验证项 | Debug | Release |
|---|---|---|
| `CSharp.UI.slnx` 非增量全量构建 | exit 0，0 警告 0 错误 | exit 0，0 警告 0 错误 |
| `CSharp.UI.Tests` | 618/618 exit 0 | 618/618 exit 0 |
| `CSharp.UI.Text.Tests` | 85/85 exit 0 | 85/85 exit 0 |
| `CSharp.UI.Integration.Tests` | 53/53 exit 0 | 53/53 exit 0 |
| `CSharp.UI.OpenTK.Tests`（真实 GL） | 678/678 exit 0 | 678/678 exit 0 |
| **四套件合计** | **1434/1434** | **1434/1434** |

六个示例构建均 exit 0（Counter/TodoList/ControlGallery × Debug/Release，`--no-incremental` 标准 `bin/<cfg>/net10.0/` 产物），六次冒烟均真实建窗渲染 5 帧自动退出（exit 0，DPI 1.5，GL 3.3.0 / Intel RaptorLake-S）：

| 示例 | 逻辑视口 | framebuffer |
|---|---|---|
| Counter | 480×320 | 720×480 |
| TodoList | 760×480 | 1140×720 |
| ControlGallery | 900×700 | 1350×1050 |

逐次退出码/argv/时长与全部原始 stdout/stderr：[manifest.json](2026-10-23-scrollbar-delivery-evidence/manifest.json)（22 runs，0 failures，per-run 记录）。证据目录逐文件原始字节 SHA256 清单：[SHA256SUMS.txt](2026-10-23-scrollbar-delivery-evidence/SHA256SUMS.txt)（47 文件 = .gitattributes + 44 raw 日志 + manifest + source-sha256，清单自身除外；manifest 内 evidence_files 字段为分阶段快照，最终以 SHA256SUMS 为准）。源码快照（src/samples/tests 228 文件，排除 bin/obj）：[source-sha256.json](2026-10-23-scrollbar-delivery-evidence/source-sha256.json)——标识本轮被验源码内容（raw bytes，已核对与当前源一致），非 diff 证明。

## 真实 Windows IME 验收（Windows 默认 IMM、单 DPI 150%）

独立验收驱动实机记录见 [审查记录证据](../return/2026-10-23-scrollbar-review-evidence/)。最终轮 24/24 PASS（[actual-ime-exact-2-final24.log](../return/2026-10-23-scrollbar-review-evidence/actual-ime-exact-2-final24.log)）：mspinyin profile 身份确认（clsid `81d4e9c9…`、guid `fa550b04…`，仅进程内激活后还原）、`nihao` 预编辑 + 锚点 live、滚动条 thumb-drag 使 caret 裁出视口 → `anchorLive=False`（几何 `Unavailable`，组合保活不取消）、release 后 ScrollY 保持 2126.5 无回弹、从 2126.5 回拖至 53.2（接近原锚定偏移 60.3）后重新锚定、Space 提交字节级精确（`Replacement=(84,84)` 精确比对通过）、Esc 取消 0 写、两次滚动条浏览期间 caret 保持 84、撤销历史 0 不变（随后的 Space 提交按事务正常入史）。

**失败轮透明记录**（同 binaries 不删不修饰，见审查记录 §验收证据）：
- `actual-run-first22-containsonly.log`：22/22 但 commit 断言仅 `Contains`——历史轮，已加强为精确比对。
- `actual-final-run-t3t4-grabmiss.log`：T3 跨行选区/T4 bar-drag 未命中（Down 未实际按中 thumb/文本区），失败机理未定位，不笼统归因。
- `actual-ime-exact-foreground-notready.log`：启动时序窗口前台竞态——首批指针输入前未获前台；IME 段 `SetForegroundWindow` 后正常。
- CPU 层 9 项全 PASS（`cpu-run.log`/`sb34-cpu-evidence.log`）：轴翻转/禁用门/capture 取消/几何取消/嵌套 wheel/拖拽中卸载/内容收缩 1050→52.5 手势取消+clamp/headless bar-drag 真实命中 Thumb。Measure 修复核实：`sb34-measure-probe-after.log`——修后运行 `MaxWidth=108.0` 与真实 `innerRect.W=108.0` 一致（frame140 − 2×HP10 − gutter12），运行时 Measure 换行宽与 Arrange/GetLayout 同源、单请求 miss 收敛；原测量实现漏扣槽位的定位与过程见审查记录 §SB-03。

**ControlGallery 演示区实际验收**：primary 经 offscreen `UiRuntime.DispatchPointer`/`DispatchWheel` 驱动真实示例程序集（非 OS SendInput）：逻辑宽 320/1024 两档，长列表 40 行（内容高 900.8）显示滚动条、切 4 行（86.5）滚动条隐藏且槽恒占；视口高 120→180 加高尺寸切换正常。见 [gallery-proof-run.log](../return/2026-10-23-scrollbar-review-evidence/gallery-proof-run.log) 与代表图 [gallery-proof-sb-gallery-w320-long.png](../return/2026-10-23-scrollbar-review-evidence/gallery-proof-sb-gallery-w320-long.png)。说明：w320 截图中 logo 区域显示占位是 throwaway offscreen 二进制未复制资源所致，非标准 sample 缺陷；六次标准 smoke 已正常渲染不受影响（不宣称截图覆盖图片资源）。

## 边界（如实）

- **GPU 像素级验收已通过（offscreen）**：primary 独立 proof——6 配置（pixelScale 1.0/1.5/2.0 × Light/Dark）真实 framebuffer 渲染（面元 360×240~720×480 物理像素），78 项像素断言覆盖 normal/hover/pressed/release-hover/move-off/disabled/no-overflow/内容越槽裁剪场景，见 [gpu-proof-run.log](../return/2026-10-23-scrollbar-review-evidence/gpu-proof-run.log) 与代表图 [gpu-proof-sb-gpu-s1.5_light-normal.png](../return/2026-10-23-scrollbar-review-evidence/gpu-proof-sb-gpu-s1.5_light-normal.png)（36 张全量在证据目录）。注意：pixelScale 为 offscreen 参数化，非切换 OS DPI。
- IME 验收仅 Windows 默认 IMM mspinyin 单 DPI（150%）单环境：文字与指针输入经 OS SendInput、滚轮经 `WM_MOUSEWHEEL` 消息级注入（非 HID 设备级），多 DPI/多 IME/其它 Windows 环境属未验边界。
- 非 Windows 平台 committed-text-only 不变（源码级 + CPU 模拟，无真机 IME 证据）；`UseTsfInputBridge` 内部 TSF 路径本轮未对 `TextEditor` 滚动条场景复验。
- 滚动条无惯性/动画、无触摸、无横向 TextEditor、无双轴 ScrollView、无公开滚动命令（计划明确不实现项）。
- 固定轨道槽占据的像素属产品行为变更（非清屏色），已迁移相关测试断言为语义断言。

## 关联文档

- 计划与决策：[2026-10-23-scrollbar-plan.md](../plan/2026-10-23-scrollbar-plan.md)
- 审查与修复记录：[../return/2026-10-23-scrollbar-review.md](../return/2026-10-23-scrollbar-review.md)（SB-01~SB-04 修复项、消费方迁移、失败轮明细）
- 指南更新：`docs/guide/platform-notes.md`（已知限制新行为）、`api-reference.md`（ScrollView/TextEditor 条目+行为）、`testing.md`（滚动条验收要点）
- 示例：`samples/CSharp.UI.Samples.ControlGallery/Program.cs` 新增"滚动条"演示区（状态驱动溢出显隐、横向滚动、多行编辑器）
