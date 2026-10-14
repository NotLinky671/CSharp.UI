# 任务完成记录（任务 01—13）

本文件汇总各任务的实施与验收记录。每个任务文档末尾也附有各自的记录；索引中的复选框与状态已同步更新。

记录格式：状态 / 交付物 / 实际执行的命令与结果 / 偏差与未验证项。

---

## 任务 01：工程初始化与版本管理 —— 已完成

交付物：

- `CSharp.UI.slnx`（src / samples / tests / tools 四个分组）
- `Directory.Build.props`（net10.0、C# 14、Nullable、隐式 using、预发布版本 0.1.0-preview.1、`IsPackable` 默认关闭）
- `Directory.Packages.props`（集中式包版本：OpenTK 4.9.4、SkiaSharp 3.119.4、SkiaSharp.HarfBuzz 3.119.4、HarfBuzzSharp 8.3.1.5 及原生资产、xunit.v3 3.2.2）
- `.gitignore`
- 工程：`src/CSharp.UI`、`src/CSharp.UI.Text`、`src/CSharp.UI.OpenTK`、三个示例、三个测试工程、`tools/CSharp.UI.AssetGenerator`

实际执行与结果：

| 命令 | 结果 |
|---|---|
| `dotnet restore CSharp.UI.slnx` | 成功（首次需要联网下载固定版本包） |
| `dotnet build CSharp.UI.slnx` | 成功（0 警告 0 错误，Release/Debug 均可） |
| `dotnet run --project tests/CSharp.UI.Tests` | 90 通过 / 0 失败 |
| `dotnet run --project tests/CSharp.UI.Text.Tests` | 通过（见任务 08 记录） |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests` | 见任务 06/07 记录 |

引用方向：`CSharp.UI.Text` → `CSharp.UI`；`CSharp.UI.OpenTK` → `CSharp.UI` + `CSharp.UI.Text`（宿主组装 Text 服务，见任务 06）；示例 → OpenTK（+ Text，用于原生资产与可选服务）；测试按模块引用。无循环引用。

偏差与说明：

- `CSharp.UI.OpenTK` 额外引用 `CSharp.UI.Text`：任务 01 的原文是“OpenTK 引用 Core 并在宿主中组装 Text 服务”，若不在宿主内引用则 `UiApplication.Run(root)` 无法获得排版能力。核心工程仍然不引用 OpenTK/SkiaSharp。
- **命名空间偏差（已记录）**：文本实现程序集名保持 `CSharp.UI.Text`，但其**命名空间**为 `CSharp.UI.Skia`。原因是核心已有公开视图类型 `CSharp.UI.Text`，而 C# 不允许命名空间与同名的公开类型共存：任何 `using CSharp.UI;` 的编译单元都无法引用 `CSharp.UI.Text.X` 或 `using X = CSharp.UI.Text;`（CS0434），宿主与示例也就无法组装文本服务。改用 `CSharp.UI.Skia` 后，任务 13 要求的目标写法 `new Text(...)` 与文本服务引用可以同时工作。
- 测试运行方式：本机 vstest 测试宿主在受限进程句柄下崩溃（`Process.EnableRaisingEvents` 拒绝访问），因此测试工程改用 **xunit.v3 + Microsoft.Testing.Platform 进程内运行器**，测试入口为 `dotnet run --project tests/<项目>`（`dotnet test` 同样可用）。这满足“无图形环境时核心测试仍可运行”与“可以单独选择图形集成测试”。

---

## 任务 02：平台无关基础类型与服务契约 —— 已完成

交付物：`src/CSharp.UI/Primitives/Geometry.cs`（Point/Size/Rect/Thickness/Alignment）、`Color.cs`、`FontDescription.cs`、`LayoutConstraints.cs`、`Rendering/DrawCommands.cs`（PixelBuffer/TextureHandle/DrawCommand/ITextureService/RenderFrame）、`Text/TextContracts.cs`（TextLayoutRequest/Result/ITextService）、`Services/PlatformServices.cs`（IImageService/ImageResource/IClipboardService/IUiDispatcher/IUiClock/UiDispatcher）、`Input/InputEvents.cs`、`Testing/TestDoubles.cs`。

验收对应：

- 基础类型转换与矩形相交在不同 DPI 下正确；空裁剪区与零尺寸可表示（`PrimitiveTests`）。
- 契约由纯托管替身实现，无需窗口、字体原生库或 GL 上下文（`CSharp.UI.Testing` + `TextContractTests`）。
- 文本契约包含字素边界与光标/选择映射（`CaretStops`/`CaretXs`/`HitTestIndex`/`GetCaretX`/`GetRangeBounds`），核心事件不含平台键码（`UiKey`）。
- 绘制命令可有序表达，含裁剪入栈/出栈与纹理句柄；所有权约定写在 XML 文档注释中（服务创建并释放）。

实际执行：`dotnet run --project tests/CSharp.UI.Tests` → 90 通过 / 0 失败（含 `PrimitiveTests`、`TextContractTests`、`DrawCommandTests`）。

---

## 任务 03：视图描述、组件与节点协调更新 —— 已完成

交付物：`Views/View.cs`、`Views/Component.cs`、`Runtime/ViewNode.cs`、`Runtime/Reconciler.cs`、`Runtime/Contexts.cs`。

验收对应（`ReconciliationTests`）：

- 同类型同身份节点保持同一 `ViewNode` 实例；属性与事件回调被替换，旧回调不再被调用。
- 带键列表插入、删除、重排后节点附加状态跟随原项目；相同键不同类型时节点被替换。
- 条件分支替换与组件卸载触发一次清理，资源释放一次；删除后无存活引用。
- 描述树可重复使用且未被协调更新修改；重复键抛出带键、类型与父路径的 `DuplicateKeyException`。

---

## 任务 04：状态、双向绑定与更新调度 —— 已完成

交付物：`State/State.cs`（State<T>/Binding<T>/StateBase）、`Views/Component.cs`（ComponentState/BuildTracker）、`Runtime/UiRuntime.cs`（InvalidateComponent/ProcessPendingWork/RegisterBindingDependency）。

验收对应（`StateTests`）：

- 组件重建后命名状态保持原值；新身份节点使用初始值；卸载后依赖清空。
- 相同值赋值不触发更新；一轮中多次赋值合并为一次重建；`Build()` 内修改状态被诊断为更新循环。
- 依赖状态变化只更新读取它的组件；不再读取的状态不再触发。
- `Binding<T>` 双向一致；后台直接写状态抛出 `UiThreadViolationException`（提示 `UiDispatcher.Post`），`Post` 动作在 UI 线程队列中执行。

---

## 任务 05：布局、修饰符与绘制命令生成 —— 已完成

交付物：`Views/Stacks.cs`（VStack/HStack/ZStack/Spacer + `StackLayout`）、`Views/Modifiers.cs`、`Views/Primitives.cs`（Text/Rectangle/Spacer）、`Runtime/Contexts.cs`（LayoutContext/DrawContext）。

验收对应（`LayoutTests`）：

- 嵌套 Stack、间距、对齐、Spacer、Frame 的布局结果符合约束；零尺寸视口不产生无效坐标。
- `Padding` 与 `Background` 的顺序产生不同背景范围；`Border`/`Opacity` 的命令顺序与透明度倍数正确。
- `ZStack` 按描述顺序层叠且顶层优先命中；嵌套 `Clip` 取交集，空裁剪区不输出可见内容。
- 不同 DPI 下逻辑布局一致；文本度量变化触发重新测量（`LayoutPassCount` 增加），纯颜色变化不重新布局（`LayoutPassCount` 不变）。

---

## 任务 06：OpenTK 桌面宿主与按需事件循环 —— 已完成

交付：`src/CSharp.UI.OpenTK/UiApplication.cs`（`UiApplication`/`UiApplicationOptions`/`UiHostContext`）、`Hosting/**`（宿主、调度器、输入适配、GLFW 剪贴板、窗口工厂）。

实际执行：`dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build` → 91 通过 / 0 失败；三个示例真机窗口冒烟各 30 帧、exit 0（GL 3.3.0 / Intel RaptorLake-S，DPI 1）。详见任务 06 文档。

---

## 任务 07：OpenGL 命令渲染与 GPU 资源管理 —— 已完成

交付：`src/CSharp.UI.OpenTK/Rendering/**`（`GlRenderer`、`GlTextureService`、`GlShaderProgram`/`GlShaderSources`、`GlCoordinateMath`、`GlResourceCounters`）、`Testing/GlOffscreenSurface.cs`。

实际执行：图形子集 `-- -trait "Category=Graphics"` → 44 通过 / 0 失败（真实光栅化 + `glReadPixels`：层叠顺序、透明混合、嵌套/空裁剪、DPI、Alpha8 遮罩、LRU 淘汰、资源计数回落、着色器失败诊断）。详见任务 07 文档。

---

## 任务 14：跨平台集成、打包与首版验收 —— 已完成（Windows 已验证；macOS / Linux 待验证）

- Windows：`dotnet build CSharp.UI.slnx` 成功（0 错误）；239 项测试全部通过（核心 90 + 文本 55 + 集成 3 + 宿主/图形 91，其中图形 44）；三个示例真机窗口冒烟 exit 0；发布产物 `win-x64` 运行 20 帧 exit 0。
- 四架构发布与原生资产：win-x64 / linux-x64 / osx-x64 / osx-arm64 均包含 GLFW + SkiaSharp + HarfBuzz 原生资产（仅 win-x64 做了运行验证，其余为仅打包验证）。
- 待验证：Linux（X11 需 Mesa + Xvfb；Wayland 完全未验证）、macOS x64/arm64 真机、真实 DPI≠1 缩放、驱动级输入注入、真实最小化/恢复操作。详见任务 14 文档与 `build/verify.ps1` / `build/verify.sh`。

---

## 任务 08：字体、文本排版与纹理缓存 —— 已完成

见 `src/CSharp.UI.Text/**` 与 `tests/CSharp.UI.Text.Tests`；记录见任务 08 文档。

---

## 任务 09：输入分发、指针捕获与焦点 —— 已完成

交付物：`Input/InputRouter.cs`（InputRouter + FocusManager）、`Runtime/UiRuntime.cs`（DispatchPointer/Wheel/Key/TextInput、SetFocus、MoveFocus、GetFocusOrder、HitTest）。

验收对应（`InputFocusTests`）：

- 重叠节点命中顶层；裁剪之外的内容不可点击；禁用子树不可点击也不参与 Tab 遍历。
- 指针捕获在拖出控件后继续接收移动事件，数值仍被限制到范围内；释放、取消与卸载后无残留捕获。
- Tab/Shift+Tab 顺序稳定并循环；节点复用保持焦点；节点删除或禁用后焦点被清除或跳过。
- 不同 DPI 下点击位置正确；已提交文本与控制键区分，不把键名当输入字符。

---

## 任务 10：Button、Toggle 与 Slider —— 已完成

交付物：`Views/Controls.cs`（Button/Toggle/Slider/TextPainter）。

验收对应（`ControlTests`）：

- 鼠标点击与键盘（Enter/Space）各触发一次回调；移出释放与取消不误触发；长按重复不重复激活。
- Toggle/Slider 双向一致；拖动越界仍有捕获且数值被限制在范围内，键盘步进同样受限。
- 禁用控件不触发行为、不参与焦点遍历，并使用禁用配色。
- 更新标签/回调/绑定后使用新值并保持焦点；计数器示例无需手动重绘即可刷新文本。

---

## 任务 11：单轴滚动容器与图片 —— 已完成

交付物：`Views/ScrollView.cs`（ScrollView/Image/ImageResizeMode）。

验收对应（`ScrollImageTests`）：

- 短内容不产生越界滚动；长内容可滚动到末端并夹紧；内容缩短或视口变化后偏移重新限制到合法范围。
- 重复构建保持滚动位置与节点身份。
- 滚动后命中位置与可见位置一致；嵌套滚动容器内层先消费，到边界后由外层消费；裁剪内容不可点击。
- 图片按自然尺寸与缩放方式显示，同一资源复用纹理；替换、卸载与加载失败后缓存有界且资源计数回落；加载失败写入诊断。

---

## 任务 12：单行 TextField 与 Unicode 编辑 —— 已完成

交付物：`Views/TextField.cs`。

验收对应（`TextFieldTests`）：

- 已提交中英文文本与中文剪贴板粘贴更新绑定；外部绑定变化同步显示；换行与制表符按单行规则过滤。
- 代理对与组合字符整体删除，空字符串、首尾光标移动与删除不损坏文本。
- 鼠标拖选、Shift 选择、全选、剪切/复制/粘贴与选择替换结果正确。
- 长文本保持光标可见（横向滚动）；重建保持光标与选择；失焦与卸载后停止闪烁且不接受输入。

---

## 任务 13：主题环境、示例与 API 文档 —— 已完成

交付物：

- 环境继承：`Environment/EnvironmentValues.cs`、`Views/Modifiers.cs`（`Theme`/`Font`/`Foreground`/`CornerRadius`/`Opacity`/`Disabled` 的环境传播）。
- 示例：`samples/CSharp.UI.Samples.Counter`、`samples/CSharp.UI.Samples.TodoList`、`samples/CSharp.UI.Samples.ControlGallery`（同一份代码适用于三平台）。
- 文档：`README.md`、`docs/guide/{getting-started,api-reference,state-lifecycle,modifier-order,keys-and-lists,custom-components,platform-notes,testing}.md`。
- 资源：`samples/Shared/Assets/logo.png` 由 `tools/CSharp.UI.AssetGenerator` 生成（无第三方素材）。

验收对应（`ThemeTests` 等）：

- 主题切换更新全部控件；字体/颜色环境继承与局部覆盖正确；两种主题下控件填充色都可辨识。
- 设计文档中的计数器写法（`.Spacing(12).Padding(24)`）可编译运行；三个示例使用同一份跨平台代码。
- 待办列表示例使用 TextField + Binding + 带键 ForEach + 增删重排 + ScrollView；控件展示覆盖首版清单。
- 文档与实现一致；未分发第三方字体并在文档中说明；未验证平台与未实现能力没有被宣称为已支持。
- 已记录偏差：原计划要求示例携带许可允许分发的中英文字体以保证截图可复现。本次选择**不分发字体二进制文件**（离线环境无法获取并附带完整许可文本的 CJK 字体），改用系统字体 + 缺字回退，并在 `docs/guide/platform-notes.md` 说明如何用 `FontFamily.FromFile` 指定自带字体；因此“跨机器截图完全可复现”不成立，中英文显示与排版能力本身已由测试与示例验证。
