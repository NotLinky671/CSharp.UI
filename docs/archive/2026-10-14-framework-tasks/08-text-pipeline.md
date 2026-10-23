# 任务 08：字体、文本排版与纹理缓存

- 任务 ID：08
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../../plan/2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)
- 返修记录：[2026-10-14 代码审核返修](../2026-10-23-completed-reviews/2026-10-14-code-review.md) 中的 R09（字距只改变测量结果，未改变实际绘制） 已修复并补充回归测试（详见该文档的修复记录与《返修后的验证结果》）。

## 目标与范围

提供中英文文本显示与测量，并为 TextField 提供一致的文本位置映射。

SkiaSharp/HarfBuzz 仅用于 CPU 字体处理和栅格化；不实现完整双向文本、彩色 Emoji、字形图集或 IME。

## 前置依赖

- 前置任务：[任务 05：布局、修饰符与绘制命令生成](05-layout-and-modifiers.md)、[任务 07：OpenGL 命令渲染与 GPU 资源管理](07-opengl-renderer.md)。
- 接入依据：实现任务 02 的文本契约，接入任务 05 的布局测量及任务 07 的纹理上传。

## 实施清单

- [x] 接入 SkiaSharp 3.119.4 与 SkiaSharp.HarfBuzz，补齐支持平台的原生资产，保持依赖只位于文本实现与宿主组装层。
- [x] 支持指定字体文件与系统 fallback；示例选择许可允许分发的中英文字体并保留许可，测试使用固定字体。
- [x] 实现 shaping、文本宽度/行高测量、中英文混排、换行、对齐和省略，Text 的测量与实际绘制使用同一排版结果。
- [x] 保存字素边界、文本范围及布局位置映射，为任务 12 的命中定位、光标和选择提供数据。
- [x] 将排版结果 CPU 栅格化为位图并通过 OpenTK 上传纹理；文本变更、字体变更和 DPI 变更时正确失效。
- [x] 缓存排版与文本纹理，键包含内容、字体、宽度、DPI 及影响结果的样式；采用有界淘汰，释放字体、位图和被淘汰的 GPU 纹理。

## 公开 API 与交接

交付 Text 及框架字体、文本对齐/换行/省略配置；文本服务返回框架排版结果，不暴露 SKTypeface 或 SKShaper。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 固定字体下中英文混排、换行、对齐与省略结果稳定，布局测量与可见文本一致。
- [x] 字体缺失时 fallback 可用；不能覆盖的字符有可解释行为，字体文件无效时报告诊断。
- [x] 文本映射覆盖代理对、组合字符与字素边界，可用于点击定位和光标计算。
- [x] 相同文本复用排版和纹理；字体、宽度或 DPI 变化正确失效；高频内容变化下缓存有界，淘汰资源可释放。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成**（2026-10-14）。
- 交付（`src/CSharp.UI.Text`，程序集 `CSharp.UI.Text`，**命名空间 `CSharp.UI.Skia`**）：
  - `SkiaTextService`：`ITextService` 实现（排版/测量/字体度量/`Alpha8` 栅格化/有界缓存/诊断）
  - `TextLayoutBuilder`、`TextShaping`：HarfBuzz shaping、`StringInfo` 字素边界、断行、对齐、省略、行与光标映射
  - `SkiaFontResolver`：字体文件/家族名/平台默认解析、家族级与缺字回退、SKTypeface 引用计数与有界缓存
  - `SkiaImageService`：`IImageService` 实现（预乘 RGBA8888 解码与有界缓存）
  - `TextDiagnostics`、`README.md`（字体来源与回退、缓存键与容量、像素格式、第三方字体说明、已知限制）
- 命名空间偏差（已记录）：实现命名空间使用 `CSharp.UI.Skia` 而不是 `CSharp.UI.Text`。核心已有公开视图类型 `CSharp.UI.Text`，C# 不允许命名空间与同名公开类型共存——任何 `using CSharp.UI;` 的编译单元都无法引用 `CSharp.UI.Text.X`（CS0434），宿主与示例因而无法组装文本服务。程序集名仍为 `CSharp.UI.Text`。
- 实际执行：
  - `dotnet build src/CSharp.UI.Text/CSharp.UI.Text.csproj` → 成功，0 警告 0 错误。
  - `dotnet run --project tests/CSharp.UI.Text.Tests --no-build` → **55 通过 / 0 失败**（父代理独立复跑确认）。
  - `dotnet run --project tests/CSharp.UI.Integration.Tests --no-build` → **3 通过 / 0 失败**（真实排版驱动核心布局、TextField 中文输入与代理对删除、文本纹理复用）。
- 关键实现事实：
  - 字素边界用 `System.Globalization.StringInfo`：`a😀b` → CaretStops `[0,1,3,4]`（不含代理对中间索引），`e+U+0301` → `[0,2]`，`👨‍👩‍👧` → `[0,8]`。
  - 中文缺字回退实测到 Microsoft YaHei UI、Emoji 回退到 Segoe UI Emoji；无法覆盖的字符记录 `MissingGlyph` 诊断并按缺字方框绘制。
  - 排版缓存键为 `TextLayoutRequest`（值相等，含字体/宽度/DPI/样式），栅格缓存键为排版结果实例；字体、缺字回退与字体实例缓存同样有界（128 / 64 / 32 / 256 / 64）。
  - 文本栅格化为 `Alpha8` 覆盖率遮罩（绘制端按 tint 着色）；图片统一预乘 `Rgba8888Premultiplied`，避免透明边缘色晕。
  - 行高与纵向度量取首选字体；`LineGap` 为字体真实 leading（可为 0，不做合成）。
- 已知限制：不支持完整双向文本、彩色 Emoji 图层、IME 组合文本与候选窗、字形图集；断行不含 UAX#14 完整规则与避头尾；`Rasterize` 会重新 shaping 一次（结果已缓存）。
