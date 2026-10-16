# RL-09：完整验收与交付记录

创建日期：2026-10-16

状态：已完成（Codex复审通过）

阶段：F

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

交付记录：[通用响应式布局交付记录（RL-09）](../delivery/2026-10-16-responsive-layout-delivery.md)

[返回任务索引](README.md)

## 目标与范围

完成全套回归、三个示例的最新构建、原尺寸证据与平台范围说明，并交Codex独立复审。

## 前置依赖

[RL-08](08-responsive-integration.md) 完成并通过复审（实现 `fc5abeb`，批准 `1b6f91f`）；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 顺序构建并执行四套完整测试，保留441项基线；Debug/Release 解决方案构建均退出码 0，四套 300+67+30+242=639 全部通过，失败/错误/跳过均为 0，未改弱旧断言。
- [x] 三个现有示例Debug/Release全部更新；核对18份核心/文本/OpenTK DLL哈希与对应库构建一致（0 不一致）；Release 三个示例各 5 帧启动 smoke 均退出码 0，无窗口/进程遗留。
- [x] 提供原尺寸宽/窄Gallery图（逻辑 1024/320 @150% → 物理 1536×1050 / 480×1050）、独立嵌套示例结果（逻辑 1100/700/500），以及 Windows 150% 宿主/输入/缩放证据；其他平台区分编译、CPU、离屏与真机，全部如实标为未验证。
- [x] 检查API示例、日期、链接、任务状态、布局诊断与已知限制；闭环所有Codex审核问题。最终 `audit-docs.mjs` 检查51份Markdown、285条相对链接、9任务/10依赖边，errors为空；`validate-docs.mjs` 通过。
- [x] 实现者通过后英文提交本轮文件（本地、未 amend/推送），Codex 完成独立复审并另存记录；不纳入用户既有 `AGENTS.md` 修改与 Codex `docs/return`。

## 测试与验收

- [x] 全部必需行为完成且完整测试通过；无法实际验证的平台范围明确（macOS/Linux 无真机：编译、CPU、离屏、原生四项均未验证，不以估计结果代替）。
- [x] 原计划五项能力均可运行（`Flex`/`FillWidth`、`Flow`、`AdaptiveStack`、`Responsive` 均有通用实现与独立回归），宽窄示例可交互，旧API默认行为保持；没有仅完成示例而缺少通用能力的交付。
- [x] 代码、测试、指南与任务状态一致，所有实施提交和最终审核可追溯。

## 实施记录

实现日期：2026-10-16。执行模型：`opencode-go/deepseek-v4.1-flash`（Codex 复审通过 RL-08 后开始）。

### 公开/内部接口

RL-09 **未改动生产代码**：五项响应式能力的公开与内部接口保持 RL-08 冻结状态。新增内容只有验证程序（均在 `.gitignore` 覆盖的 `artifacts/` 下）与文档：

| 新增/更新 | 内容 |
|---|---|
| `artifacts/responsive-work/rl09-native-scsize/` | 真实 `GalleryApp`/`NestedResponsiveExample` 的 Windows 原生 `SC_SIZE` 模态循环探针：强制 front buffer 可读、逐宽度非零像素采样、framebuffer 契约与无窗口真实布局逐节点对照 |
| `artifacts/responsive-work/rl09-capture/` | 真实窗口 front buffer 原尺寸 PNG 截图（宽/窄 Gallery、嵌套示例三档宽度） |
| `docs/delivery/2026-10-16-responsive-layout-delivery.md` | 持久交付记录（构建/测试/DLL/冒烟/原生循环/截图/平台范围） |
| `docs/delivery/2026-10-16-responsive-layout-evidence/` | 五张原尺寸 PNG（宽/窄 Gallery、Nested 三档），随仓库分发 |
| `docs/guide/responsive-layout.md` | §9 更新原生 SC_SIZE 证据与人工鼠标边界 |
| `docs/guide/testing.md` | 修正运行器过滤器写法并补充响应式回归说明 |

### 验证命令与退出码

```bash
dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false                    # exit 0，0 错误
dotnet build CSharp.UI.slnx -c Release --no-restore -m:1 -nodeReuse:false          # exit 0，0 错误
dotnet run --project tests/CSharp.UI.Tests --no-build                             # exit 0
dotnet run --project tests/CSharp.UI.Text.Tests --no-build                        # exit 0
dotnet run --project tests/CSharp.UI.Integration.Tests --no-build                 # exit 0
dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build                      # exit 0
CSHARPUI_SMOKE_FRAMES=5 <Release 示例 exe>                                        # 三个示例 exit 0
dotnet run --project artifacts/responsive-work/rl09-native-scsize -- gallery     # exit 0
dotnet run --project artifacts/responsive-work/rl09-native-scsize -- nested      # exit 0
```

| 测试工程 | 通过 | 失败 | 跳过 | 退出码 |
|---|---:|---:|---:|---:|
| CSharp.UI.Tests | 300 | 0 | 0 | 0 |
| CSharp.UI.Text.Tests | 67 | 0 | 0 | 0 |
| CSharp.UI.Integration.Tests | 30 | 0 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 242 | 0 | 0 | 0 |
| 合计 | **639** | **0** | **0** | 0 |

441 基线（172+67+14+188）全部保留，净增 198；无回归子集（idle 5、纹理淘汰 2、宿主 resize 1、TextColorMask 15、TextRasterSampling 6、WindowResizeRedraw 10，合计 39）单独运行退出码 0。

### 实测数据与证据

- Release 5 帧 smoke：ControlGallery 900×700→1350×1050、Counter 480×320→720×480、TodoList 760×480→1140×720，DPI 均 1.5，退出码 0。
- 原生 SC_SIZE（Gallery）：`enters=1,exits=1,modalFrames=20,geometryErrors=0,frontReadable=True`；7 个逻辑宽度（900/500/1120/420/620/300/1024）均非空采样、背景与内容像素 >0、纯黑 0、右侧边缘非背景 0；自适应模式随 560/840 断点翻转；布局签名与同尺寸无窗口真实布局一致。Nested：`enters=1,exits=1,modalFrames=16`，6 个宽度同上通过。
- 截图：宽 1024@1.5 → 1536×1050（wrap 358.477×37.242），窄 320@1.5 → 480×1050（wrap 252×55.863）；嵌套 1100 面板 616/296 分别 `HStack`/`VStack` 分支，500 时两面板均窄分支；五张原尺寸 PNG 见 `docs/delivery/2026-10-16-responsive-layout-evidence/`。
- Codex 独立探针重跑：`input-invariants` 180/180、`gpu-invariants` 16/16，退出码均 0；Codex 另独立核对 18 份 DLL 与四套 Release 程序集 639 项全部通过。
- Codex 实施期截图探针预审（稳定帧等待依赖偶发刷新）已处理：`rl09-capture` 显式 `RequestRedraw()`，重跑仍退出码 0 且 PNG 哈希不变，未改生产宿主。

### 适用平台与未验证边界

- 适用：Windows 11 桌面、.NET 10、真实 Segoe UI / Microsoft YaHei UI、OpenGL 3.3（Intel RaptorLake-S）、系统缩放 150%（未修改）。
- 未验证：macOS/Linux 真机的编译、CPU 测试、离屏 GL 与原生窗口（本机 RID 交叉编译 restore 离线失败，退出码 1，未声称通过）；LCD/ClearType；真实人工鼠标拖拽；系统 DPI 修改。
- 提交 SHA 与完整日志见 `artifacts/responsive-work/rl09-progress.txt`。

2026-10-16：Codex最终复审通过，九项任务全部完成。验证范围及未验证边界见交付记录。
