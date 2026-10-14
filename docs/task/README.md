# CSharp.UI 任务索引

创建日期：2026-10-14

设计依据：[CSharp.UI：基于 OpenTK 的 C# 声明式 UI 框架](../2026-10-14-declarative-ui-framework-plan.md)。

本目录把原计划拆分为 14 个本地 Markdown 任务。文档已建立；全部 14 个任务的实现、测试与验收记录已完成：任务 01—13 已实现并通过测试，任务 14 在 Windows 上完成构建/测试/窗口冒烟/四架构原生资产检查，macOS 与 Linux 明确标记为待验证。

完成记录汇总见[任务完成记录](completion-records.md)；原计划保留为设计依据，未修改其技术版本与首版范围。

## 使用方式

- 按下表前置依赖执行，编号提供一种可行顺序；前置任务验收后再开始依赖它的任务。
- 每个任务包含目标、边界、依赖、实施清单、公开 API 与交接、测试与验收。
- 实现与相应测试同步完成；记录结果后更新任务状态及下方完成复选框。完成文档编写不表示框架任务已完成。
- 阶段用于追踪交付进度，不能取代依赖顺序。例如渲染底座 07 依赖阶段二的布局 05。

## 任务列表与依赖

| 完成 | ID | 任务文档 | 主要交付物 | 前置任务 |
|---|---|---|---|---|
| ☑ | 01 | [工程初始化与版本管理](01-project-setup.md) | 建立能够持续构建和测试的工程骨架，为核心、文本、OpenTK 后端和示例提供明确的模块边界。 | 无 |
| ☑ | 02 | [平台无关基础类型与服务契约](02-core-contracts.md) | 在具体实现前定义跨模块契约，让布局、文本、输入和渲染通过核心类型协作。 | [01](01-project-setup.md) |
| ☑ | 03 | [视图描述、组件与节点协调更新](03-view-reconciliation.md) | 建立不可变视图描述树与持久运行时节点树，允许重复声明 UI 而稳定复用对应节点。 | [02](02-core-contracts.md) |
| ☑ | 04 | [状态、双向绑定与更新调度](04-state-and-binding.md) | 让组件读取状态自动建立依赖，状态变化后合并重建相关组件，并提供输入控件的双向绑定。 | [03](03-view-reconciliation.md) |
| ☑ | 05 | [布局、修饰符与绘制命令生成](05-layout-and-modifiers.md) | 将声明式描述转换为正确的逻辑像素布局和有序绘制命令，支持组合式布局与链式修饰。 | [03](03-view-reconciliation.md)、[04](04-state-and-binding.md) |
| ☑ | 06 | [OpenTK 桌面宿主与按需事件循环](06-opentk-host.md) | 提供单窗口应用入口，统一处理图形上下文、主线程调度、尺寸/DPI 变化和按需重绘。 | [02](02-core-contracts.md)、[04](04-state-and-binding.md) |
| ☑ | 07 | [OpenGL 命令渲染与 GPU 资源管理](07-opengl-renderer.md) | 通过 OpenTK/OpenGL 完成所有最终 UI 绘制，正确处理层叠、透明混合、裁剪和纹理资源。 | [05](05-layout-and-modifiers.md)、[06](06-opentk-host.md) |
| ☑ | 08 | [字体、文本排版与纹理缓存](08-text-pipeline.md) | 提供中英文文本显示与测量，并为 TextField 提供一致的文本位置映射。 | [05](05-layout-and-modifiers.md)、[07](07-opengl-renderer.md) |
| ☑ | 09 | [输入分发、指针捕获与焦点](09-input-and-focus.md) | 把平台输入转换成稳定的视图交互，支持鼠标、键盘、滚轮及组件更新后的交互状态保持。 | [05](05-layout-and-modifiers.md)、[06](06-opentk-host.md) |
| ☑ | 10 | [Button、Toggle 与 Slider](10-basic-controls.md) | 提供首版基础交互控件，验证状态、文本、输入和渲染之间的完整数据流。 | [04](04-state-and-binding.md)、[07](07-opengl-renderer.md)、[08](08-text-pipeline.md)、[09](09-input-and-focus.md) |
| ☑ | 11 | [单轴滚动容器与图片](11-scroll-and-image.md) | 提供保留滚动位置的容器与可显示图片的内容视图，覆盖常见桌面布局需求。 | [07](07-opengl-renderer.md)、[09](09-input-and-focus.md) |
| ☑ | 12 | [单行 TextField 与 Unicode 编辑](12-text-field.md) | 提供可绑定字符串的单行输入框，正确处理字素边界、选择、剪贴板与光标可见性。 | [08](08-text-pipeline.md)、[09](09-input-and-focus.md)、[10](10-basic-controls.md) |
| ☑ | 13 | [主题环境、示例与 API 文档](13-theme-examples-and-docs.md) | 形成可阅读、可运行的框架首版，用统一主题和示例说明组件、状态、布局与输入能力。 | [10](10-basic-controls.md)、[11](11-scroll-and-image.md)、[12](12-text-field.md) |
| ☑ | 14 | [跨平台集成、打包与首版验收](14-cross-platform-validation.md) | 验证首版在目标平台的构建、真实窗口交互、原生依赖和资源行为，并交付可复核的结果。 | [13](13-theme-examples-and-docs.md) |

## 四阶段完成清单

### 阶段 1：工程与渲染底座

- [x] [01：工程初始化与版本管理](01-project-setup.md)
- [x] [02：平台无关基础类型与服务契约](02-core-contracts.md)
- [x] [06：OpenTK 桌面宿主与按需事件循环](06-opentk-host.md)
- [x] [07：OpenGL 命令渲染与 GPU 资源管理](07-opengl-renderer.md)

### 阶段 2：声明式运行时与布局

- [x] [03：视图描述、组件与节点协调更新](03-view-reconciliation.md)
- [x] [04：状态、双向绑定与更新调度](04-state-and-binding.md)
- [x] [05：布局、修饰符与绘制命令生成](05-layout-and-modifiers.md)

### 阶段 3：文本与基础交互

- [x] [08：字体、文本排版与纹理缓存](08-text-pipeline.md)
- [x] [09：输入分发、指针捕获与焦点](09-input-and-focus.md)
- [x] [10：Button、Toggle 与 Slider](10-basic-controls.md)
- [x] [11：单轴滚动容器与图片](11-scroll-and-image.md)

### 阶段 4：输入控件与交付

- [x] [12：单行 TextField 与 Unicode 编辑](12-text-field.md)
- [x] [13：主题环境、示例与 API 文档](13-theme-examples-and-docs.md)
- [x] [14：跨平台集成、打包与首版验收](14-cross-platform-validation.md)

## 原计划覆盖关系

| 原计划能力 | 对应任务 |
|---|---|
| .NET 10 / C# 14、OpenTK 4.9.4、SkiaSharp 3.119.4、三库分层及测试入口 | 01、02、08 |
| View、Component.Build、不可变描述、持久节点、条件内容、Key、ForEach、属性和事件更新、卸载 | 03 |
| UseState、State<T>.Value、Binding<T>、依赖跟踪、变化合并、UiDispatcher.Post、UI 线程约束 | 04、06 |
| Measure / Arrange、VStack、HStack、ZStack、Spacer、Frame、Padding、修饰符顺序 | 05 |
| Rectangle、Background、Foreground、Border、CornerRadius、Opacity、矩形 Clip | 05、07 |
| NativeWindow、UiApplication.Run、按需事件循环、DPI、重绘、最小化与关闭 | 06 |
| OpenGL 3.3 Core / GLSL 330、macOS Forward Compatible、图形/纹理、相邻批处理、透明混合、裁剪、GPU 生命周期 | 06、07 |
| Text、字体文件/fallback、可分发字体、中英文排版、换行/对齐/省略、排版与文本纹理有界缓存 | 08 |
| 命中、悬停、按下、指针捕获、滚轮、Tab 焦点、状态保持、交互清理 | 03、09 |
| Button、Toggle、Slider、Disabled、键盘激活及双向绑定 | 09、10 |
| 单轴 ScrollView、滚动位置与边界、Image、纹理资源释放 | 11 |
| 单行 TextField、已提交 Unicode、字素边界、光标/选择、删除、剪贴板、横向滚动及光标闪烁 | 12 |
| 明暗主题、字体/颜色环境继承、计数器、待办列表、控件展示、API 与运行说明 | 10、13 |
| 状态/布局/交互/文本测试、离屏 framebuffer、资源计数与缓存上限 | 各任务同步实现测试，07、14 汇总图形和资源验证 |
| 三平台构建、Mesa/Xvfb、Windows/macOS 真机输入与 DPI、四种架构原生依赖、未验证项记录 | 14 |

## 共用边界与完成定义

- 目标是 Windows、macOS、Linux 的统一自绘桌面 UI；首版单窗口，使用普通 C# 对象组合与链式修饰，不引入 XAML 或源生成器。
- SkiaSharp/HarfBuzz 处理 CPU 文本与位图；最终图形、文字纹理及图片合成全部通过 OpenTK/OpenGL。
- 完整 IME、移动端、浏览器、宿主嵌入、动画、列表虚拟化、多行编辑、完整双向文本、彩色 Emoji 和系统无障碍桥接不在首版交付中。
- 同一份三个示例代码可在目标平台运行；组件更新保留交互状态，静止界面不持续重绘，资源不持续增长。
- 框架包保持预发布状态，不自动发布 NuGet。状态为“已完成”必须有实施清单、测试结果与交付物支持；无设备或环境的验证明确写为“待验证”。

