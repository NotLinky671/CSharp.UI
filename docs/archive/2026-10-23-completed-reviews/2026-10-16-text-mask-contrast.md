# R17：白色文字遮罩复用于深色文字，边缘覆盖率未匹配实际文字颜色

记录日期：2026-10-16

审核人：Codex

审核版本：`588ac04`（字体采样修复为 `fdd91fa`）

优先级：P2

状态：DeepSeek Harness 已修复并提交 `03b7a07`，Codex 于 2026-10-16 独立复审通过。历史诊断与实现期间问题保留，最终结论见文末。

## 用户现象与本轮范围

用户提供 Hello 窗口截图，反馈文字仍模糊、笔画局部粗细不均、边缘抗锯齿观感不好。R16 已消除文字纹理二次缩放与小数物理原点的额外插值，但不能据此认定 CPU 栅格化质量也达标。

本轮只在本地忽略目录 `artifacts/font-quality-review/` 创建独立诊断程序，没有修改框架、测试或示例源码。工作区原有 `AGENTS.md` 修改不属于本轮。

## 已确认问题：白字遮罩不是与颜色无关的几何覆盖率

- [SkiaTextService.BuildRaster](../../../src/CSharp.UI.Text/SkiaTextService.cs) 始终以 `SKColors.White` 绘制 Alpha8 位图，不接收实际文字颜色。
- [TextureFragment](../../../src/CSharp.UI.OpenTK/Rendering/GlShaderSources.cs) 把遮罩红通道直接作为 coverage，乘实际 tint 后以预乘 alpha 混合。
- 在本机 Skia/Windows 路径上，白字绘制得到的边缘值包含与文字亮度相关的栅格处理。其结果不能通过简单染色无差异地复用于深字浅底。Skia 的设计文档明确说明 glyph mask 的 gamma/contrast 处理可依赖绘制颜色，不能把所有遮罩都视为未经调整的面积覆盖率。

官方依据：[The Raster Tragedy in Skia](https://docs.skia.org/docs/dev/design/raster_tragedy/)，尤其 Gamma Hack、Contrast Hack；字体定位和 edging 的区别见 [SkFont](https://api.skia.org/classSkFont.html)。本机像素实验确认了实际差异，文档用于解释机制；本轮没有给每个边缘差值进一步分离 gamma 和 contrast 的各自贡献。

## 独立复现与截图比对

固定 Segoe UI、14 逻辑像素、DPI 1.5、`Hello, CSharp.UI!`。使用真实 `SkiaTextService.Layout/Rasterize`，并反射调用同一个 `DrawLine` 生成参考图；所有路径的字形、排版、坐标和字体属性一致，参考路径只改变目标表面与绘制颜色。

| 对照 | 结果 |
|---|---|
| 当前 Alpha8 白色遮罩 vs 同一路径的 RGBA 白字黑底红通道 | 平均差 0，最大差 0 |
| 当前遮罩染成深色 vs 同一路径直接 RGBA 深字浅底 | 红通道平均绝对差 10.0712/255；626 个像素相差超过 1 |

第二组前景为 RGB(30,31,35)，背景为 RGB(245,246,249)，分母为整张 156×28 位图，含透明边距；不是主观清晰度改善百分比。直接 RGBA 参考仍使用灰度 AA，没有启用 LCD，证明差异不能全部归因于缺少 ClearType。

用户截图独立匹配结果：当前 156×28 遮罩在截图像素原点 (86,257)，取截图背景 RGB(245,246,248)、不透明笔画 RGB(27,29,33) 后，覆盖整张文字区域的 RGB 平均绝对差为 0.04022/255。该结果强烈支持截图符合当前 CPU 遮罩及一对一合成路径，没有显示额外的大范围纹理缩放。截图不提供可执行文件身份，故不据此断言具体启动程序集的版本。

复现入口：`artifacts/font-quality-review/Verify.csproj`、`Program.cs`。结果：`result.txt`；原尺寸与 3 倍最近邻检查图：`comparison.png`。最近邻放大只用于看清源像素，不用于生产渲染。

## 已排除的简单修复

本机实际默认配置为 `Hinting=Normal`、`Subpixel=false`、`LinearMetrics=false`、`BaselineSnap=true`、`EmbeddedBitmaps=false`、灰度 AA。

- 只将 Hinting 改为 Full，在本次样例及重复 `l` 中未观察到改善，不能宣称“以前没有 hinting”。
- 开启 Subpixel（小数像素字形定位，非 LCD AA）后，12 个相同 `l` 的归一化覆盖率总和由全部 36.392，变为部分 36.392、部分 30.055。这个特定样例反而出现覆盖率差异，不能把开关作为无条件改善。
- 关闭 hinting 会改变轮廓，但没有消除白字遮罩复用于深字的颜色处理问题。
- 当前重复 `l` 在关闭小数定位时覆盖率一致；不能仅凭截图把所有“粗细不均”判定为相同字形真的使用了不同字重。曲线、竖线和半透明边缘的对比度也会影响主观粗细。

## 修复要求与接入边界

1. 明确文字遮罩的契约：选择经过验证的颜色中立覆盖率，再在文字专用合成路径处理对比度；或按实际文字颜色生成适用遮罩。必须以同一路径直接绘制的参考图验证，不能仅添加任意 `pow(alpha, 常量)`、全局 sRGB 开关、假粗体或 Nearest 过滤。
2. 若栅格输出依赖文字颜色或渲染模式，更新文本服务请求/栅格接口及所有相关缓存键，覆盖 Text、Button/Toggle 标签与 TextField。前景环境、明暗主题和文字颜色变化时，不能复用不适用的旧遮罩。共享同一排版结果与字形位置，避免为了着色改变测量、换行和光标几何。
3. 保留 R16 的一纹素对应一物理像素及原点吸附；保持一般图片、透明度、嵌套裁剪与资源生命周期正确。灰度 AA 可继续作为跨平台基线，LCD 路径需要单独设计背景、透明表面及 RGB/BGR 约束，不得仅把 Alpha8 的 Edging 切为 LCD 后宣称支持 ClearType。
4. 添加有意义的端到端颜色验证：真实文字栅格、GPU 合成与参考图比较最终 RGB，而不仅是 GPU alpha 是否等于 CPU 遮罩。R16 的 alpha 一致性测试仍然有效，但它不能发现 CPU 遮罩本身的颜色依赖问题。

## 验收

- 固定字体；覆盖用户原句、相同细竖笔画及中英文，字号 12/14/16/28，DPI 1/1.25/1.5/2，深字浅底与浅字深底。记录参考算法及可接受差异，Windows 系统字体结果与其他平台分开记录。
- 前景色/主题动态改变后的纹理缓存与实际像素正确；半透明文字和控件 Disabled 不发生双重颜色校正或错误预乘。
- TextField 光标、选区、点击定位和横向滚动保持一致；Gallery 内外滚动继续满足 R16 像素映射。
- 完整四套测试，三个示例 Debug/Release 重新构建，并在本机 150% 下完成 Release 启动检查；额外提供与用户原句相同的原尺寸修复前后图，不把放大图当作最终观感验收。
- 字形外伸裁剪仍是 R16 已知限制，未在本次颜色诊断中解决。没有 macOS/Linux 真机结果时，不宣称全平台主观观感通过。

## Codex 实现期间复查（2026-10-16）

发现新增 GPU 测试 `TextColorMaskTests.AssertLegacyWhiteMaskFails` 的颜色相关性判定有误：当前把直接白字 RGB 与直接实际色文字 RGB 比较，前景色本身就会造成差异，不能证明遮罩覆盖率随颜色变化。必须固定最终 tint 来比较，或比较独立 `RenderCoverage(..., Color.White)` 和 `RenderCoverage(..., command.Tint.WithAlpha(255))` 的 alpha。实际本机浅字深底的遮罩与白字相同，不能要求这类负对照也必然失败。正向最终 RGB 验证应继续覆盖深浅主题。

Codex 独立基线位于 `artifacts/font-color-independent/`：384 组（四档 DPI × 四字号 × 两主题 × 三类文本 × 四类控件），旧实现 188 组超出最大 RGB 差 3 的阈值，最大 RGB 差 84，平均每组 MAE 4.91334/255；几何均正确。原尺寸修复前图为 `before.png`。后续使用同一程序复测。

### 实现期间复查补充：透明度参考必须按整段文字分组合成

主修复已经通过 Codex 独立 384 组最终 RGB 检查：0 组超标，最大差 2/255，各组平均 MAE 0.10419/255，原尺寸 `artifacts/font-color-independent/after.png` 已查看，边缘明显不同于旧图。

额外 384 组有色半透明/Opacity 组合检查中，若参考直接把 alpha 传给每次 DrawLine 的 SKPaint，在 DPI 1.25、14 号中英文混排处出现差异。框架的既有契约是把整个不透明文字遮罩合成为纹理，再统一应用 tint alpha × opacity；逐字形/字体 run source-over 的透明度不能作为该契约的通用参考。Codex 改用独立 Skia SaveLayer 在整段不透明颜色绘制后统一应用 alpha，384 组全部通过，最大差 3/255，各组平均 MAE 0.11035/255（`extended-group-after.txt`）。这不是生产路径仍有 8 项失败；它是参考图分组合成语义不一致，初次结果保留在 `extended-after.txt`。

建议 DSH 将测试参考 `TextDirectReference.RenderOnBackground` 的透明颜色分支改成“按实际不透明 RGB 直接绘制到层，Restore 时统一乘 alpha”，并加入至少一项 `中文边缘 English 123`、14 号、DPI 1.25、前景 alpha 128 与 Opacity 0.63 的组合回归，防止未来用逐 glyph 透明度参考误判本框架纹理整体透明度。保持正向最终 RGB 阈值与原有源码行为，不放宽阈值。

## 实现记录（DeepSeek Harness，后续已由 Codex 复审）

### 策略：按实际文字颜色栅格化同一份遮罩（未采用 pow / 全局 sRGB / 假粗体 / Nearest）

先在本机用独立探针（`artifacts/font-quality-review/exp`）验证报告建议的假设，再改生产代码：

- Alpha8 遮罩确实随 `SKPaint` 颜色变化：A8(白) 与 A8(深色 30,31,35) 平均差 12.03、最大 99、626 像素相差超过 1。
- A8(color) 与“同一 `DrawLine` 路径直接绘制到 BGRA 透明表面”的 alpha 通道**逐像素完全一致**（mean 0、max 0）。
- A8 遮罩会被 paint alpha 预先乘一次（a=128 vs 255：mean 15.36、max 127）。因此遮罩只处理 RGB，
  固定按 alpha=255 绘制，透明度只在 GPU tint 乘一次。

字体 hinting/subpixel 策略、R16 的 1 纹素=1 物理像素与原点吸附、LCD 与字形外伸裁剪范围均未改动。

### API 与缓存变化

- `ITextService.Rasterize(TextLayoutResult, Color)`：新增颜色重载，只用 RGB。为兼容外部实现，
  接口提供默认实现 `=> Rasterize(layout)`；旧 `Rasterize(layout)` 保留且等价 `Color.White`（本机输出与修复前逐像素一致）。
- `SkiaTextService`：`BuildRaster` 用 `new SKColor(R,G,B,255)` 绘制 Alpha8；栅格缓存键为 `(排版结果引用, RGB)`，
  颜色条目仍受 `RasterCacheCapacity` 约束；排版（测量）缓存键不变，颜色不触发重新排版。
- `TextPainter.TextPaintKey`、`Text.Emit` 的 `TextTextureKey` 增加归一化 RGB 并调用 `Rasterize(layout, maskColor)`；
  Button/Toggle 标签与 TextField 共用 `TextPainter`，自动生效。`StubTextService` 同步为颜色感知键。

### 测试与实测（四套 432 全绿：核心 172、文本 67、集成 14、图形 179；baseline 408，新增 24）

`tests/CSharp.UI.Text.Tests/TextMaskColorTests.cs`（9 例，CPU 契约）：遮罩覆盖率与同色直接绘制逐像素一致（≤1）、
白字遮罩复用于深色文字确实超阈值、缓存按 RGB 复用且忽略 alpha、颜色变化不触发重新排版、
颜色条目受容量约束、尺寸/几何不受颜色影响。

`tests/CSharp.UI.OpenTK.Tests/TextColorMaskTests.cs`（15 例，真实 CPU→GL 最终 RGB）。
参考由共享 `tests/Shared/TextDirectReference.cs` 生成：复用生产同款 `DrawLine` 定位，但直接绘制到颜色表面，
不调用 `Rasterize`。阈值 mean ≤ 0.75、max ≤ 4（0—255）：

| 场景 | 颜色感知遮罩 | 旧白字遮罩（同一 GPU 路径负对照） |
|---|---|---|
| 原句 14px DPI1.5 深字浅底 | mean 0.1015 / max 2 | mean 10.1257 / max 84 |
| 原句 12px DPI1.0 深字浅底 | mean 0.1201 / max 2 | mean 10.0368 / max 84 |
| 原句 28px DPI2.0 深字浅底 | mean 0.0339 / max 2 | mean 3.4801 / max 84 |
| 中英混排 14px DPI1.5 深字浅底 | mean 0.1141 / max 2 | mean 10.3365 / max 84 |
| 重复细竖线 14px DPI1.5 深字浅底 | mean 0.2581 / max 2 | mean 17.6221 / max 82 |
| 浅字深底（原句/中英/细竖线） | mean ≤ 0.19 / max 2 | 参考遮罩与白字遮罩逐像素一致（max 0），不构成错误场景 |

对 Codex 复查（颜色相关性判定）的处理：负对照改为比较**独立参考覆盖率本身**
（`RenderCoverage(Color.White)` vs `RenderCoverage(Tint.WithAlpha(255))`），不再用前景色不同的两幅 RGB 比较；
浅字深底遮罩与白字一致时按独立参考自动跳过，不再要求其必然失败。

对 Codex 复查（透明度参考语义）的处理：`TextDirectReference.RenderOnBackground` 在 `color.A < 255` 时
改为把整段文字按不透明 RGB 画进图层、Restore 时统一乘 alpha；并新增
`中文边缘 English 123`、14 号、DPI 1.25、前景 alpha 128 与 `Opacity 0.63` 的组合回归。
该组实测 mean 0.1016 / max 2；原先逐 run 传 alpha 的写法在该组会偏大（mean 0.27 / max 3），已不再使用。

半透明与禁用：`alpha=128` mean 0.22 / max 2、`Opacity 0.5` mean 0.23 / max 2、两者叠加 mean 0.16 / max 2，
均与整体合成参考一致，未出现双重 alpha 混合；禁用按钮标签（`DisabledForeground`）mean 0.057 / max 2。
主题前景色动态切换：新色产生新纹理、切回复用旧纹理、`LayoutMisses` 不变。
R16 的 `TextRasterSamplingTests` 改为用命令实际 tint 取遮罩（`Rasterize(layout, command.Tint)`），继续通过。

### 验收证据

- 用户原句原尺寸（156×28 物理像素，Segoe UI 14，DPI1.5，笔画 `#1B1D21`，背景 `#F5F6F8`）：
  `artifacts/font-quality-review/r17-before.png`、`r17-after.png`、`r17-before-after-original.png`（未放大）。
  相对同一直接参考的红通道绝对差：修复前 mean 10.1671 / max 84，修复后 mean 0.1037 / max 1。
- 三个示例 Debug/Release 全部重新构建、退出码 0；Release 在真机 DPI 1.5 下用 `CSHARPUI_SMOKE_FRAMES=5`
  冒烟，三条均渲染 5 帧后退出码 0（GL 3.3 Intel RaptorLake-S）。

### 未解决限制

- 本机是 Windows/Segoe UI。颜色相关性来自 Skia 的字形遮罩处理；若某平台遮罩与颜色无关，
  颜色感知契约仍成立（结果与白字遮罩相同），负对照按独立参考自动跳过；没有 macOS/Linux 真机结论。
- 只在 Skia 灰度 AA 下验证；LCD/ClearType 仍需单独设计背景、透明表面与 RGB/BGR 约束。
- 字形外伸裁剪仍是 R16 已知限制，本次未处理。
- 颜色进入 GPU 纹理键后，同一文字的不同前景色各占一个纹理条目，容量语义由既有 LRU 保证；
  未做“同一遮罩多色复用”，因为那正是本次要消除的错误来源。

## Codex 最终独立复审（2026-10-16）

复审提交：`03b7a07`，`Rasterize text masks with the actual text color`。结论：本轮修复通过；实现期间发现的负对照判定与透明度参考问题均已修正，未发现阻碍本轮验收的代码问题。

- 独立完整构建解决方案成功，0 错误；现有 NU1900 漏洞信息服务访问警告保留。随后直接运行本次构建的四套测试程序集：核心 172、文本 67、集成 14、OpenTK 179，共 **432 项全部通过，0 失败、0 跳过**。日志位于 `artifacts/font-color-independent/solution-build.txt` 与 `*-final.txt`。
- 独立比较真实控件的最终 GPU RGB 与 Skia 直接绘制参考：四档 DPI、四字号、两主题、三类文本、Text/Button/Toggle/TextField，共 384 组；修复前 188 组超标、最大差 84/255，修复后 0 组超标、最大差 2/255，各组平均 MAE 0.10419/255。额外有色半透明与整体 Opacity 的 384 组也全部达标，最大差 3/255、平均 MAE 0.11035/255。两组均保持 R16 的像素尺寸与原点映射。这里的误差描述像素差异，不表示主观清晰度百分比。
- 使用同一实际运行时对四类控件执行浅色→深色→浅色切换：新颜色使用新纹理，切回原颜色复用原纹理，排版未重新执行。另编译并运行只实现旧栅格方法的外部文本服务替身，确认新增默认接口方法可用。证据为 `final-after.txt`、`final-extended-after.txt`；两份程序最终均退出 0。
- 三个示例 Debug/Release 六次构建均成功，12 份核心/文本 DLL 副本的 SHA-256 均匹配对应配置的最新库构建。核对三个 Release 示例各 5 帧启动日志，均退出 0，本机真实 DPI 为 1.5。生产源码在这些检查后未再变更。
- 独立生成并查看 `artifacts/font-color-independent/before.png`、`after.png`，均为用户原句的原尺寸实际 GPU 输出。原来的白字遮罩复用造成的深字边缘加重已消除；仍保留灰度 AA 与已记录的字形外伸裁剪限制，不宣称已实现 LCD/ClearType 或完成三平台人工观感验证。
- 核对修复提交仅含本轮 15 个代码、测试与文档文件，差异格式检查通过。用户原有 `AGENTS.md` 修改未被提交。本节以单独的英文审核记录提交保存，不改写 DSH 的修复提交。
