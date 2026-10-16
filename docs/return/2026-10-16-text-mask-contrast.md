# R17：白色文字遮罩复用于深色文字，边缘覆盖率未匹配实际文字颜色

记录日期：2026-10-16

审核人：Codex

审核版本：`588ac04`（字体采样修复为 `fdd91fa`）

优先级：P2

状态：已独立复现，待 DeepSeek Harness 实现修复及 Codex 复审。本文记录诊断与验收要求，不代表已修复。

## 用户现象与本轮范围

用户提供 Hello 窗口截图，反馈文字仍模糊、笔画局部粗细不均、边缘抗锯齿观感不好。R16 已消除文字纹理二次缩放与小数物理原点的额外插值，但不能据此认定 CPU 栅格化质量也达标。

本轮只在本地忽略目录 `artifacts/font-quality-review/` 创建独立诊断程序，没有修改框架、测试或示例源码。工作区原有 `AGENTS.md` 修改不属于本轮。

## 已确认问题：白字遮罩不是与颜色无关的几何覆盖率

- [SkiaTextService.BuildRaster](../../src/CSharp.UI.Text/SkiaTextService.cs) 始终以 `SKColors.White` 绘制 Alpha8 位图，不接收实际文字颜色。
- [TextureFragment](../../src/CSharp.UI.OpenTK/Rendering/GlShaderSources.cs) 把遮罩红通道直接作为 coverage，乘实际 tint 后以预乘 alpha 混合。
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
