# R16：文字位图二次重采样造成模糊，字体定位策略需要复核

记录日期：2026-10-15

审核人：Codex

审核版本：`029c46a`

优先级：P2

状态：DSH 已完成修复并提交 `fdd91fa`，Codex 于 2026-10-16 独立验收及提交范围复核通过。下文历史记录（含会话中断与失败记录）与字形外伸裁剪已知限制均保留。

## 用户表现

用户在笔记本 150% 系统缩放下反馈字体模糊、边缘锯齿明显。本次检查文字排版、栅格化、纹理上传、绘制尺寸和物理像素定位；没有取得用户所见画面的截图，不把所有主观锯齿表现归为同一个已证实原因。

## 已确认问题一：整数位图被压回小数物理尺寸

- [SkiaTextService.BuildRaster](../../../src/CSharp.UI.Text/SkiaTextService.cs) 使用 `ceil(layout.Size * PixelScale)` 分配 Alpha8 字形遮罩，按实际 DPI 栅格化，这一步不是低分辨率位图直接放大。
- [Text.Emit](../../../src/CSharp.UI/Views/Primitives.cs)、[TextPainter.Paint](../../../src/CSharp.UI/Views/Controls.cs) 和 [TextField.Emit](../../../src/CSharp.UI/Views/TextField.cs) 却以 `layout.Size` 作为目标逻辑尺寸，将整个整数像素纹理映射到它。
- [GlRenderer.DrawTexture](../../../src/CSharp.UI.OpenTK/Rendering/GlRenderer.cs) 只乘 DPI，目标物理宽高仍是小数；UV 覆盖整个纹理。
- [GlTextureService.Upload](../../../src/CSharp.UI.OpenTK/Rendering/GlTextureService.cs) 使用 `Linear` min/mag 过滤，因此对已经栅格抗锯齿的笔画再次插值。线性过滤本身合法，但当前文字纹理与目标像素不能一对一对应。

本机 14 逻辑像素字体、文本 `中文字体边缘 English 123`，150% 下：

```text
逻辑排版：158.210938 × 18.621094
文字位图：238 × 28
实际绘制：237.316406 × 27.931641 物理像素
```

取整误差虽小于 1 像素，但它是整段纹理的缩放，采样相位随位置变化，能改变整段笔画覆盖率，不能因总体缩放百分比很小就忽略。

## 已确认问题二：位图绘制起点没有对齐物理像素

`Text.Emit` / `TextPainter.Paint` 只把逻辑起点保留三位小数，`GlCoordinateMath.ToFramebufferRect` 直接乘 DPI；这不等于物理像素对齐。居中布局、字体度量和滚动偏移容易生成小数物理坐标。

同一张文字遮罩即使保持原始尺寸，只要原点平移半个物理像素，线性采样仍会混合相邻像素。表现为不同位置的文字清晰度、笔画粗细和边缘灰度不一致，滚动过程中也可能变化。

实际 `GalleryApp`、900×700 逻辑视口、DPI 1.5，使用真实文本服务统计初始可见绘制命令：

```text
可见文字纹理命令：29
纹理尺寸与目标物理尺寸不一致：29
起点含小数物理坐标：28
```

## 独立 OpenGL 对照实验

用真实 `SkiaTextService` 生成遮罩，使用项目 `GlTextureService` / `GlRenderer` / `GlOffscreenSurface` 渲染到离屏 framebuffer，读回 alpha 与原始遮罩逐像素比较。所有组都使用相同的灰度抗锯齿位图和线性过滤，没有更换字体、没有切换成 Nearest。

| DPI | 当前尺寸、整数起点：alpha 平均绝对差 | 原位图尺寸、整数起点 | 原位图尺寸、半像素起点 |
|---|---:|---:|---:|
| 1 | 22.6044 | 0 | 36.3128 |
| 1.5 | 11.4976 | 0 | 28.1073 |
| 2 | 15.1498 | 0 | 21.1529 |

差值范围为 0—255，分母为整张文字遮罩的像素数，包含透明像素；它证明采样改变了覆盖率，不能解释为主观清晰度百分比。150% 当前尺寸组有 2235 个像素相对原始遮罩的差超过 1；一对一组为 0。

复现代码：本地忽略目录 `artifacts/font-review/Verify.csproj`、`Program.cs`。

150% 原尺寸证据图：`artifacts/font-review/current-size.png`、`one-to-one.png`、`half-pixel.png`。本次已构建、运行并查看前两张图；未修改生产代码。

## 抗锯齿与字体配置：已确认现状及未确认影响

- `SKPaint.IsAntialias = true` 且 `SKFont.Edging = Antialias`，并非完全关闭抗锯齿。150% 原始遮罩有 911 个非 0/255 的半透明像素，灰度覆盖率确实存在。
- 文本使用 Alpha8 灰度遮罩，未采用 Windows LCD/ClearType 的 RGB 子像素抗锯齿；与系统原生文字的视觉效果可能不同。这是当前实现策略，不能直接当作“应打开 MSAA”或“没有 AA”。
- [SkiaFontResolver.FontInstance](../../../src/CSharp.UI.Text/SkiaFontResolver.cs) 设置 `Subpixel = false`，旁边的注释把它解释为避免彩边。Skia 的 `Subpixel` 控制字形是否尊重小数像素定位，`Edging` 才区分灰度与 LCD 抗锯齿，两者不是同一件事。关闭定位可影响分数坐标字形的落点，但其对当前主观锯齿的贡献尚未单独量化；不应未经对照直接切换为 LCD 模式。
- 本机默认西文字体实际为 `Segoe UI`，中文正常回退到 `Microsoft YaHei UI`，诊断没有显示缺字方框。当前没有证据认定为加载了错误字体。

依据：[Skia SKFont 文档](https://api.skia.org/classSkFont.html) 中 `setSubpixel` / `setEdging` 的不同语义，以及 [Khronos 纹理过滤说明](https://wikis.khronos.org/opengl/GLAPI/glTexParameter)。

## 后续修复建议与验收要求

实现期间补充独立字形外伸验证：将服务的同一个 `DrawLine` 绘制到四周额外 32 物理像素的大画布，比较原位图边界。Windows Segoe UI、28 逻辑像素、DPI 1.5 时，单独 `j`（常规/斜体）的左边界外均有 14 个非透明像素，单独斜体 `f` 右边界外有 3 个。这是已有位图按 advance 尺寸截断字形外伸的问题；不能把采样修复等同于此问题已经解决。复现位于本地忽略目录 `artifacts/font-ink-review/`。返修如解决它，需要明确保存栅格原点/余量并保持布局 advance 与实际 ink 范围分离；如未解决，应保留为明确的已知限制。

1. 优先修正文字遮罩的物理像素映射：保持栅格尺寸与目标像素一对一，处理 ceil 产生的边缘余量；同时明确文字物理原点对齐策略。不要全局把布局尺寸取整，也不要把所有图片纹理强制 Nearest。
2. 统一 `Text`、控件标签与 `TextField` 的文字绘制路径；如果调整显示原点，光标、选区、点击定位与横向滚动必须使用一致变换，避免重新引入 R12 的对齐问题。
3. 保留布局、换行、对齐、裁剪语义，并检查位图边缘余量及斜体字形外伸是否被截断。不能靠放大字号或任意加粗掩盖采样错误。
4. 将字形小数定位与 LCD 渲染分开评估；修正误导性注释。对比固定字体、中英文、12/14/16/28 字号、明暗背景和实际 GPU 输出后，再决定 hinting/定位策略。
5. 在 DPI 1、1.25、1.5、2 和不同小数布局原点下验证，加入从真实文字遮罩上传到 framebuffer 的质量回归；断言覆盖率或明确的像素映射不变量，不能仅检查非空纹理、成功上传或整帧非黑。
6. 验收静态文字与滚动后文字；重新构建相关示例 Debug/Release。真实触摸板、不同显示器/操作系统的主观观感需要单独标注，不能把本机离屏结果扩大为全平台人工验证。

## 实现期间独立复查补充

初版实现的文字物理映射已通过独立 48 组实际控件 GPU 读回对照（四档 DPI × 四字号 × Text/Button/TextField），最大覆盖率差为 0；实际 Gallery 四档 DPI 滚动前后也没有纹理重缩放或小数物理起点。最终提交后仍需核对产物。

输入框末尾的光标夹紧需要准确描述：当前实现先共用文字原点，再将光标矩形夹回可见区；其横向偏移包含光标自身宽度，不能声称“与文字的偏差不超过一个吸附量”。独立长文本样例 DPI 1 下为约 1.119 逻辑像素，超过 0.5 物理像素的原点吸附量。可接受的验收应分别检查：纵向原点一致；内部光标 X 与实际纹理原点加排版光标 X 精确一致；仅在裁剪边缘夹紧以保持完整可见，位移有界（光标宽度加半个物理像素）；点击仍使用实际文字原点。也可以用统一的滚动余量策略解决，但不能只移动光标后宣称所有坐标始终完全相同。

## 2026-10-15 最终验收检查：尚未完成

Codex 在当前未提交代码上重新执行，不能把此前局部通过等同于整体验收通过。

- 整个解决方案构建失败：[GalleryTextSamplingTests.cs](../../../tests/CSharp.UI.OpenTK.Tests/GalleryTextSamplingTests.cs) 第 32 行使用 `initial.Count` 比较和插值，但返回值是数组，应使用数组长度；产生 CS0019、CS1503。因此 OpenTK 测试项目尚未完成最终构建与全套运行。
- 核心测试 172/172 通过；集成测试 14/14 通过。
- 文本测试 68 项中 65 通过、3 失败，均来自本轮新增 `TextRasterMarginTests.cs`（后续收尾已移除）的“默认字体墨迹不触及边界”断言。墨迹触及位图边界不能直接证明发生裁剪，该测试还依赖系统字体。应删除或用同一排版路径在额外余量画布上的对照替代；不能靠放宽断言宣称外伸裁剪已经解决。
- 三个示例 Counter、TodoList、ControlGallery 均已重新构建 Debug 和 Release，六次构建全部成功。三个 Release 示例均在本机 DPI 1.5 下完成各 5 帧启动检查。
- 独立验证程序已在本次最终构建后重新运行：48 组实际控件纹理与 GPU 读回覆盖率最大差为 0；Gallery 四档 DPI 滚动前后均无位图缩放和小数物理起点；TextField 四档 DPI 的点击、选区、内部光标、末尾可见性与边缘夹紧检查全部通过。
- 尚无本轮修复提交，HEAD 仍为 `029c46a`。已知字形外伸裁剪及灰度抗锯齿与 ClearType 的观感差异仍保留。

原 DSH ACP 会话已中断，恢复接口返回错误 `-32603`；确认原 ACP 后台进程已经停止后，尝试交接收尾。当前配置不再提供用户指定的 `opencode-go/deepseek-flash`，因此未切换模型执行。可用列表包含 `opencode-go/deepseek-v4.1-flash`，模型选择待用户确认；不能把新建但未提交提示词的连接视为修复任务已启动。

## 2026-10-16 状态复核

用户要求确认额度耗尽后的完成状态。Codex 重新检查 Git 并执行解决方案构建与文本测试：HEAD 仍为 `029c46a`，本轮代码仍未提交；解决方案仍因 `GalleryTextSamplingTests.cs:32` 的两个编译错误失败；文本测试仍为 68 项、失败 3 项，均来自上述边界断言。没有证据表明此前中断的任务已完成收尾。本次日志保存在本地忽略目录 `artifacts/font-review/2026-10-16-build.log` 和 `2026-10-16-text-tests.log`。此前独立 GPU 验证与示例检查结果保留，本次没有重复运行这些已通过且代码未变的检查。

## 2026-10-16 Codex 收尾验证（进行中）

DSH 本轮已修正 Gallery 数组长度编译错误，并移除无效的 TextRasterMarginTests。Codex 使用 `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false -v quiet` 完整构建成功，0 错误（现有 NU1900 漏洞信息源访问警告保留）。无需重新还原或修改 NuGet 配置。

Codex 直接运行本次构建产物的四套完整测试：核心 172、文本 58、集成 14、OpenTK 164，共 408 项全部通过，0 错误、0 失败、0 跳过。日志：`artifacts/font-review/2026-10-16-final-build.log` 与 `2026-10-16-*-verified.log`。DSH 继续完成本地提交；此前未完成状态属于历史记录，以本节及后续最终复审为准。

Codex 已完成三个示例 Debug/Release 六次重新构建，三个 Release 示例均通过 DPI 1.5 下各 5 帧启动检查。剩余工作仅为 DSH 英文提交与 Codex 提交范围复核。DSH 子进程的 MSBuild 环境单独失败不代表本机独立构建失败，不应继续修改 SDK 或 NuGet 配置来修复；本次不需要这些环境变更。

## 2026-10-16 最终复审通过

- DSH 使用 `opencode-go/deepseek-v4.1-flash` 完成收尾，并创建本地英文提交 `fdd91fa00e4b951410f4acb701dbf6b3c65ddff5`：`Fix text raster sampling at fractional DPI scales`。
- Codex 核对提交只包含本轮 4 个源码文件、5 个测试文件及本报告，共 10 个文件；此前其他文档改动均未混入。提交差异格式检查通过，生产代码与通过验收时一致。
- 完整解决方案构建与 408 项测试通过。三个示例 Debug/Release 均已更新，且 12 份核心/文本 DLL 副本哈希与对应配置的库构建结果一致；三个 Release 示例的 5 帧启动检查全部通过。
- 本轮解决文字遮罩二次缩放与小数物理原点导致的额外模糊，保持现有灰度抗锯齿。字形外伸裁剪仍为已知限制；没有把本机 Windows 验证扩大为三平台人工观感验证。
- 本节为 Codex 提交后的独立复审记录，随其他审核文档单独提交，不修改 DSH 的原提交。
