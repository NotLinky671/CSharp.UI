# 2026-10-26 inline-code-padding evidence — FINAL (run4 172/0)

真实结果：GL offscreen harness **172 pass / 0 fail**，exit=0（run4）。
产品两个真实 defect 由本 harness 发现、修复并复验关闭；前序 run1/run3
的失败 raw 全部保留、不覆盖。

## Run history (each run keeps its own raw stdout/stderr/exit/png)

| run | 结果 | 说明 |
|-----|------|------|
| run1 | 64 fail / 101 pass | **harness probe 误测**：旧 HorizontalPadding 像素探针把
 seg.X(glyphX)+seg.Width(含右pad) 当纯 advance 盒解读，bg 边缘走查在
 AA/接缝处误停。产品当时实际已真 padding（手工量图 6–7 物理 px）。 |
| run3 | 167 pass / 1 fail | **真实产品 defect #1**：`RichText.HitLink`
 命中矩形 = `[seg.X, seg.X+Width)` —— 右侧 pad 命中（Width 已含）但
 左侧 pad 带 `[X−InsetLeft, X)` 不命中，与 Emit 绘制的 pill 盒
 `[X−InsetLeft, X+Width)` 不一致。证据：`D-linkpad-leftpad-activates`
 FAIL，点击点 135.33 位于已绘制 pill 内（run3-D-linkpad.png 像素为
 灰底 #E3E3E5），右带点击可激活。 |
| run3 (审图) | — | **真实产品 defect #2**（主审读图发现）：`中Subagent文`
 内部 CJK→Latin→CJK 三个 fallback canonical segment 因 Emit 合并键
 比较 InsetLeft/InsetRight（5/0、0/0、0/5）被拆成三段圆角 pill，接缝
 处露出页色。修复后 Emit 按逻辑 TextStyleRange+line+color+radius 合并。 |
| run4 | **172 pass / 0 fail** | 两 defect 修复后同脚本（未放宽任何检查）
 全绿：`D-linkpad-leftpad-activates` 同一坐标 135.33 → 激活正确
 payload；新增 E3-seam 实测：3 个 fallback segment → **恰好 1 个**
 `RectangleCommand [31.8,135.5)` 覆盖逻辑 pill 盒，脚本边界 0 页色
 缝隙；相邻独立 code 仍各自独立 pill。 |

Harness 自身迭代与 product defect 分开披露如上，不笼统称
"harness fail" 或 "product fixed"。

## Suite results (final, Release build 0 warnings / 0 errors)

- `logs/run5-build-release.{stdout,stderr,exit}.txt` — `dotnet build
  CSharp.UI.slnx -c Release` → code=0，**0 个警告 0 个错误**（本轮
  xUnit2031 警告已由实现在新 RichTextViewTests 中修正，非旧代码遗留）。
- `logs/run5-test-Tests.{stdout,stderr,exit}.txt` — **711/711** pass, code=0
- `logs/run5-test-Text...` — **112/112** pass, code=0
- `logs/run5-test-Markdown...` — **50/50** pass, code=0
- `logs/run5-test-Integration...` — **56/56** pass, code=0

注：`dotnet test`（MSBuild testing-platform named-pipe）在本机报
`UnauthorizedAccessException`（run1/run2 logs 保留），属基础设施限制非
产品问题；套件均经测试 .exe 直跑取得真实 count。

## Harness（仓库外，不进 solution）

- 目录：`%LOCALAPPDATA%\Temp\inline-code-padding-smoke\`
  （`app/Program.cs`、`app/inlinecodepadsmoke.csproj`、`run-padding.ps1`）
- 机制：真实 `GlOffscreenSurface`（Intel GL 3.3）+ `SkiaTextService` +
  `RecordingRich`（IRichTextService 公开面 decorator，记录视图真实排版
  handle）+ `StubClipboardService`（框架测试替身，剪贴板只到服务边界，
  **OS 剪贴板全程未触碰**）；`run-padding.ps1` 以
  `System.Diagnostics.Process` 异步读满 UTF-8 stdout/stderr + 显式
  ExitCode，lib 由 sample Debug bin + 各 src Debug dll 拼装，repo bin 不改。
- 局限：offscreen FBO 非物理鼠标/窗口路径；Windows-only 验证；点击经
  `UiRuntime.DispatchPointer` 注入逻辑 PointerEvent。

## 覆盖（Part 标签）

- A：light/dark/custom-accent(#00C853) × ps 1.0/1.5/2.0，`行内代码`+
  `Subagent`：segment `InsetL=InsetR=5`（几何面，draw-box-only 实现会
  在此失败）+ 像素带内灰底填充 + 垂直留白(top~6-7/bottom~2-4 物理px) +
  圆角 + 与相邻普通字同 baseline。
- B：相邻普通字 glyph 核心区不被 pill 灰底覆盖。
- C：wrap —— 每个 line fragment InsetL=InsetR=5（续行也是完整 pill），
  fragment 不超 layout 宽，pad=6 vs pad=0 同文宽度差证实 padding 进 advance。
- D：link-code 左/右 pad 带点击激活正确 payload；同段 gap 词点击不激活；
  第二 link 不误命中。
- E：bold/CJK/混合/`中Subagent文` 单 token 首末段各一份 inset，无按
  fallback run 双加。
- E2：真实 Skia `IRichTextService.Layout` 微分 —— pad5−pad0 总宽
  +10.00、首段 X +5、span +5（per-token 非 per-run）、行高 +10.00
  （vPad 进 lineheight）、LayoutIdentity 变。
- E3：fallback 接缝 —— pill 内逐列无页色缝隙 + 逻辑 range 每行恰好
  一个背景 `RectangleCommand`（RenderFrame.Commands 公开面）。
- F：文末 code 右 pad 完整 + RasterBounds 覆盖段右缘。
- G：缓存 —— padding 变 → 新 LayoutIdentity+宽变（geometry 签名缺
  padding 会在此现形）；radius-only → 同 identity + 0 重栅格。
- H：围栏块 copy 按钮写 `  block-lead = true;`；行内 code 无 copy。
