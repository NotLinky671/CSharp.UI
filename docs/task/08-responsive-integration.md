# RL-08：输入、DPI 与图形集成回归

创建日期：2026-10-16

状态：待Codex复审

阶段：F

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

用真实排版、输入路由和GPU输出确认响应式布局可交互，并与R16/R17/R18修复共存。

## 前置依赖

[RL-07](07-responsive-gallery-and-guides.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 覆盖四档DPI下相同逻辑尺寸的相同布局选择，实际子控件坐标、文本纹理尺寸和嵌套裁剪正确。
- [x] 在实际示例与独立嵌套组件中派发点击、文本、Tab、Slider拖动和滚轮；跨断点后的旧区域不可点击，滚动偏移按新内容范围收敛。
- [x] GPU验证最终RGB与裁剪、物理像素映射；保留R17按实际RGB栅格化，不能靠缩放旧文字纹理适配。
- [x] 真实宿主连续跨断点resize/refresh能取得稳定布局，重建期间不重入，不出现部分树或黑边；区分程序化回调与原生模态循环。
- [x] 记录构建/布局次数与缓存上限，确认静止不重建/重绘；修复发现的问题并同步任务/指南。

## 测试与验收

- [x] 真实Skia与GL、头部/表单/滚动区域实际行为通过，不只断言非空或非黑帧。
- [x] 状态/选区/焦点验证覆盖实际控件，物理和逻辑坐标不混用；极窄/无界/嵌套情况有明确诊断。
- [x] R18生命周期、回调异常、原生循环测试结果如实记录；新增图形测试隔离FBO和上下文状态。

## 实施记录

实现日期：2026-10-16。执行模型：`opencode-go/deepseek-v4.1-flash`（Codex 复审通过 RL-07 后才开始）。

### 公开/内部接口

本轮**未改动生产代码**：真实示例、运行时、文本/OpenTK 层均保持 RL-07 已冻结的公开与内部接口（`FrameView` 上限、`ResponsiveInfo`、`AdaptiveStack`、`Flex`/`FillWidth`、`ITextService.Rasterize(layout, color)`、`OpenTkHost` 即时回调等）。新增内容全部位于测试工程 `tests/CSharp.UI.OpenTK.Tests`：

| 新文件 | 覆盖 |
|---|---|
| `GalleryIntegrationHost.cs` | 真实示例 + 真实 Skia 的无窗口共用宿主与逐节点布局快照 |
| `GalleryDpiLayoutTests.cs`（14） | 320/480/768/1024 × DPI 1/1.25/1.5/2：逐节点逻辑布局与断点模式一致、文字纹理按 DPI 栅格化、内层裁剪矩形一致 |
| `GalleryBreakpointInputTests.cs`（12） | 跨断点按钮/Toggle/主题点击、Slider 拖动（Down/Move 不同坐标与切换中捕获）、Tab 顺序、中文输入与选区、真实光标原点+caret、双层滚轮与滚动收敛、Nested 外层 State 与稳定输入 |
| `GalleryGpuIntegrationTests.cs`（6） | 真实 Gallery 文字最终 RGB 对直接绘制参考；内层视口 GPU 裁剪；**同一运行时** 1024→320 与 DPI 1→1.25→1.5→2 的真实重排/重新上传 |
| `GalleryHostResizeTests.cs`（1） | 真实 OpenTkHost + GL，程序化连续跨断点 resize/refresh，与同逻辑尺寸无窗口布局逐节点对照 |
| `GalleryIdleAndBoundsTests.cs`（5） | 静止不构建/测量/重绘、连续宽度变化的测量量与纹理缓存契约、极窄/无界/嵌套的诊断与确定回退 |

### 验证命令与退出码

```bash
dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false     # exit 0，0 错误
dotnet run --project tests/CSharp.UI.Tests --no-build              # exit 0
dotnet run --project tests/CSharp.UI.Text.Tests --no-build         # exit 0
dotnet run --project tests/CSharp.UI.Integration.Tests --no-build  # exit 0
dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build       # exit 0
```

| 测试工程 | 通过 | 失败 | 跳过 | 退出码 |
|---|---:|---:|---:|---:|
| CSharp.UI.Tests | 300 | 0 | 0 | 0 |
| CSharp.UI.Text.Tests | 67 | 0 | 0 | 0 |
| CSharp.UI.Integration.Tests | 30 | 0 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 242（RL-08 新增 38） | 0 | 0 | 0 |
| 合计 | **639** | **0** | **0** | 0 |

未弱化任何旧断言；R16 坐标契约、R17 颜色遮罩与 R18 窗口回调/生命周期回归保持通过。

### 实测数据（本机 Windows / 真实 Skia / OpenGL 3.3 Intel）

- DPI 1/1.25/1.5/2 下同一逻辑宽度逐节点布局与断点模式完全一致，文字纹理 `PixelWidth == ceil(逻辑宽 × DPI)` 且原点吸附整数物理像素。
- 同一运行时 1024→320：换行说明排版 358.477×37.242（2 行）→ 252×55.863（3 行）；320 宽纹理随 DPI 为 252×56 / 315×70 / 378×84 / 504×112；最终 RGB 与直接颜色绘制 mean 0.0585–0.0992、max 2（阈值 0.75/4），实际墨迹 2905–10588 像素。
- 内层滚动跨边界行在视口下边缘外越界像素 0，边界内墨迹 222/282 像素。
- 宿主连续 7 档跨断点 resize：每步回调返回前提交新帧、`pending=0`、渲染器/上下文/GLFW framebuffer 一致、右上角为清屏色，且与同逻辑尺寸无窗口布局逐节点一致。
- 连续 41 档宽度：单轮最大 Measure 175、每步 ≤2 轮布局、组件 Build 增量 0、存活纹理峰值 256（容量 256）。容量是长期目标：本帧/上一帧引用受保护时可临时超出，后续帧边界回收，本轮峰值不作为全局硬上限。

### 适用平台与未验证边界

- 适用：Windows 10/11 桌面，.NET 10，真实 Segoe UI / Microsoft YaHei UI 回退，OpenGL 3.3（Intel RaptorLake-S）。
- 未验证：macOS/Linux 真机；LCD/ClearType；真实人工鼠标拖拽；原生 SC_SIZE 模态循环（旧独立探针 `artifacts/resize-native-review` 在简单 Text 场景下 `expandedFrames=22, expandedPixels=1,506,600, pixelErrors=0`，但**不覆盖真实 Gallery 跨断点布局**，且该脚本未强制非零采样，需结合计数判读）；Release 构建与启动冒烟由 RL-09 负责。
- 提交 SHA 与完整日志见 `artifacts/responsive-work/rl08-result.txt`、`rl08-progress.txt`。

实现完成，标注**待Codex复审**；复审通过前不宣称最终验收完成。
