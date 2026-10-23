# 测试指南

## 测试分层

| 工程 | 内容 | 是否需要图形上下文 |
|---|---|---|
| `tests/CSharp.UI.Tests` | 核心：基础类型、契约替身、协调更新、状态、布局与修饰符、输入/焦点、控件、滚动/图片、TextField、主题环境 | 否 |
| `tests/CSharp.UI.Text.Tests` | 文本：字体解析与回退、排版/换行/对齐/省略、字素与代理对映射、缓存上限、图片解码 | 否（需要原生 Skia/HarfBuzz） |
| `tests/CSharp.UI.Integration.Tests` | 跨模块：真实 SkiaSharp/HarfBuzz 排版驱动核心布局与 TextField，文本纹理上传与复用（不注入 GL 上下文，使用记录型纹理服务） | 否（需要原生 Skia/HarfBuzz） |
| `tests/CSharp.UI.OpenTK.Tests` | 图形集成：离屏 framebuffer 渲染、颜色/圆角/边框、层叠顺序、透明混合、裁剪与 DPI、GPU 资源计数 | **是**（OpenGL 3.3） |

核心测试全部基于 `CSharp.UI.Testing` 提供的无窗口替身（调度器、文本、纹理、图片、剪贴板、时钟），因此不需要窗口、字体原生库或 GL 上下文。

## 运行方式

测试使用 **xUnit v3 + Microsoft.Testing.Platform 进程内运行器**（不使用 vstest 宿主，便于在受限环境下运行）：

```bash
# 核心单元测试
dotnet run --project tests/CSharp.UI.Tests

# 文本测试
dotnet run --project tests/CSharp.UI.Text.Tests

# 跨模块集成测试（文本 + 运行时/控件）
dotnet run --project tests/CSharp.UI.Integration.Tests

# 图形集成测试（需要 OpenGL 上下文）
dotnet run --project tests/CSharp.UI.OpenTK.Tests

# 只跑图形类测试（按 Trait 过滤）
dotnet run --project tests/CSharp.UI.OpenTK.Tests -- -trait "Category=Graphics"
```

也可以使用 `dotnet test CSharp.UI.slnx`；若需要按名称筛选，可用运行器的 `--help` 查看当前支持的过滤器（当前为单破折号的 `-class`、`-method`、`-namespace`、`-trait` 及对应的 `-class-`/`-method-` 排除形式）。

测试需要写文件时（字体/图片解码用例）使用 `TextTestHelpers.UniqueTempPath`，产物位于测试输出目录下的 `scratch/` 子目录，按进程隔离、用例自行删除文件；不要依赖系统 Temp——受限策略下测试宿主可能无权写入，会出现 UnauthorizedAccess。

## 无图形环境

- 核心与文本测试不依赖图形环境，可直接在 CI 容器中运行。
- 图形测试需要 OpenGL 3.3 上下文（Linux 上可用 Mesa + Xvfb）。**图形测试失败不会被标记为跳过**：缺少上下文时会如实失败并输出诊断，避免把“没跑”当成“通过”。
- Windows 与 macOS 的首版真机输入/DPI 验证结果记录在[首版跨平台验证记录](../archive/2026-10-14-framework-tasks/14-cross-platform-validation.md)。

## 真实窗口冒烟测试

除离屏图形测试外，三个示例都支持无人值守的真实窗口验证：

```bash
CSHARPUI_SMOKE_FRAMES=5 dotnet run --project samples/CSharp.UI.Samples.Counter --no-build
```

渲染指定帧数后打印逻辑视口、framebuffer、DPI、最小化状态与 GL 上下文描述并自动退出（退出码 0）。
未设置该环境变量时行为与普通运行一致。发布产物同样支持：

```bash
CSHARPUI_SMOKE_FRAMES=5 ./artifacts/publish/win-x64/ControlGallery.exe
```

## Windows IME 验收

- TSF 路径的宿主 smoke 与真实输入验收必须在 `[STAThread]` 入口运行；MTA 只用于验证“明确失败并回退 IMM”。
- 真实 IME 场景使用实际微软拼音：预编辑、键盘选词/翻页、取消、唯一提交与候选随几何移动/裁出隐藏。合成按键、fake owner 或受控元素事实不能替代该验收。
- 候选鼠标选择当前不支持，不得用“点击候选文本”绕过输入法写入绑定。

## 断言与验证要点

状态与协调更新：

- 相同身份节点保持同一 `ViewNode` 实例；带键列表插入/删除/重排后状态跟随原项目。
- 相同值赋值不触发重建；一轮内多次变化只合并为一次构建。
- 不再读取的状态不再触发该组件；卸载后依赖清空、资源释放一次。

布局：

- 嵌套 Stack、间距、对齐、`Spacer`、`Frame` 的数值结果；零尺寸视口不产生无效坐标。
- 修饰符顺序（`Padding` 与 `Background`）产生不同的背景范围。
- 纯颜色变化不重新布局（`UiRuntime.LayoutPassCount` 不变），文本变化触发重新布局。

交互：

- 重叠节点命中顶层；裁剪外、禁用内容不可点击；拖动越界仍有捕获并限制数值。
- Tab/Shift+Tab 顺序稳定；节点复用保持焦点；节点删除或禁用后焦点有效。
- 已提交文本与控制键区分，不把键名当输入字符。
- 滚轮默认单位是归一化步数（`WheelEvent.Mode` 为 `Steps`）：一格按系统设置换算为逻辑像素（当前设置 3 行 → 48）。
  直接用像素距离的测试必须显式传 `WheelDeltaMode.Pixels`，不能靠增量大小猜测；真实适配器的普通一格 `±1` 另用
  `OpenTkInputAdapter` 路径覆盖。

文本编辑：

- 中文粘贴、代理对与组合字符删除、选择替换、剪贴板与光标可见性。

渲染与资源：

- 离屏图验证背景/内容/边框/圆角与 ZStack 顺序、透明混合与嵌套裁剪。
- 重复提交不持续创建 GPU 资源；关闭后资源计数回落；缓存保持有界。

文字颜色遮罩（R17）：

- 文本遮罩按实际文字颜色栅格化：`Rasterize(layout, color)` 的覆盖率与同一排版、同一字形定位的
  直接颜色绘制参考 alpha 通道比较，最大差不超过 1/255。GPU 合成后的最终 RGB 单独检查，
  阈值为平均绝对差 ≤ 0.75/255、最大差 ≤ 4/255。
- 图形侧回归 `TextColorMaskTests` 走真实 CPU→GL 最终 RGB，覆盖深字浅底/浅字深底、12/14/16/28 号、
  中英混排、重复细竖线、DPI 1/1.25/1.5/2；并用旧白字遮罩在同一 GPU 路径上做负对照。
- 前景色/主题变化后 GPU 纹理缓存必须分离、排版（测量）缓存不得失效；半透明与 `Opacity` 只乘一次。

响应式布局：

- 弹性、换行、自适应堆叠与 `Responsive` 分支都基于父容器分配的逻辑宽度：同一逻辑宽度在 DPI 1/1.25/1.5/2 下选择相同；跨断点后文字按新宽度重新排版并重新栅格化（1 纹素 = 1 物理像素），不缩放旧文字纹理。
- 真实示例回归位于 `GalleryDpiLayoutTests`、`GalleryBreakpointInputTests`、`GalleryGpuIntegrationTests`、`GalleryHostResizeTests`、`GalleryIdleAndBoundsTests`；`NestedResponsiveExample` 覆盖同一窗口内不同父宽度选择不同内容分支。
- 独立探针 `artifacts/responsive-work/*-invariants` 覆盖真实 Skia 布局、连续缩放、输入命中与最终 RGB；Windows 原生 `SC_SIZE` 模态循环与 150% 原尺寸截图证据见[响应式布局交付记录](../delivery/2026-10-16-responsive-layout-delivery.md)。
- 物理鼠标人工拖拽、macOS/Linux 真机与系统 DPI 修改不在自动化结论内。

## 多行编辑（TextEditor）验收要点

- 尾随换行停在空行末；软换行共享 offset 的归属按 `TextAffinity`（`LineEnd`/`LineStart`）；选区逐行矩形（`GetRangeRects`，行末零宽 marker 涂成可见窄块）。
- `MinLines` 固定视口：超高内容内部 `ScrollY` 滚动不增高控件；嵌在 `ScrollView` 里滚轮先由编辑器消费、到边界冒泡外层。
- `Enter` 前后 `BreakMerge`：撤销按"换行"事务粒度恢复；粘贴 `\r\n`→`\n`、`\r\n` 单字素删除。
- 拖选越界：`GetNextWakeup` 返回 ~80ms tick，静止越界指针连续滚动；释放/取消/失焦即停。**注意**：指针拖出 OS 窗口边缘时宿主按"捕获丢失即停"取消拖动（`PointerCancel`），跨窗口的自滚不在验收范围。
- 真实 IME（组合/预编辑/候选锚点）的多行验收已完成（Windows 微软拼音/IMM,2026-10-23——见[交付记录](../delivery/2026-10-23-text-editor-delivery.md)）；不以单测或合成消息替代,后续平台 IME 变更仍需真机复验。

## 滚动条（ScrollView/TextEditor）验收要点

- **显隐（Standard 默认）**：内容溢出才显示轨道+滑块；persistent 12 逻辑像素槽恒占、无溢出留空槽（显隐不引起布局回流）。
- **显隐（Floating）**：不占布局槽（内容贴边）；实际滚动时显示（ScrollView：滚轮/拖拽/轨道翻页；TextEditor 另含键盘 caret 收敛与编辑滚动），静止约 1 秒隐藏；指针悬停条上或拖拽期间不隐藏（离开/释放后重新计时）；无 overflow 永不显示；被动 clamp、视口尺寸/父级平移不产生显示。
- **命中优先**：Standard 槽与 Floating 可见条命中 owner（bar），不进内容 caret/拖选；Floating 可见条优先于重叠子内容；隐藏后同一位置完全落到内容（底层按钮可点）。Standard 无溢出时空槽同样不透到内容。
- **交互**：滑块可拖拽（最小滑块长 24）；轨道点击按视口翻页一次、无自动重复；几何/range 变化、禁用、卸载取消进行中的 bar 手势（不回弹）；Floating 禁用时立即隐藏。
- **IME 锚点**（TextEditor）：组合期用户滚动为持久意图——caret 裁出视口 → `Unavailable`（候选锚点撤除、组合保活）；真实新预编辑/caret 推进后重新锚定；同值 PPW/时钟/平移不弹回。

## 资源计数的读取方式

| 计数 | 位置 |
|---|---|
| 纹理创建/释放/存活 | `ITextureService.LiveTextureCount`；OpenGL 实现还提供创建与释放计数（帧内引用受保护，容量在帧边界回收） |
| 排版/栅格缓存 | `ITextService.CachedLayoutCount`、`CachedRasterCount` |
| 图片缓存 | `IImageService.CachedImageCount` |
| 布局轮数 / 构建次数 | `UiRuntime.LayoutPassCount`、`BuildCount` |
| 诊断信息 | `UiRuntime.Diagnostics` |
