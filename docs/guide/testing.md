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

也可以使用 `dotnet test CSharp.UI.slnx`；若需要按名称筛选，可用运行器的 `--help` 查看当前支持的过滤器（`--filter-class`、`--filter-method` 等）。

## 无图形环境

- 核心与文本测试不依赖图形环境，可直接在 CI 容器中运行。
- 图形测试需要 OpenGL 3.3 上下文（Linux 上可用 Mesa + Xvfb）。**图形测试失败不会被标记为跳过**：缺少上下文时会如实失败并输出诊断，避免把“没跑”当成“通过”。
- Windows 与 macOS 的真机输入/DPI 验证结果记录在 [../task/14-cross-platform-validation.md](../task/14-cross-platform-validation.md)。

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

## 资源计数的读取方式

| 计数 | 位置 |
|---|---|
| 纹理创建/释放/存活 | `ITextureService.LiveTextureCount`；OpenGL 实现还提供创建与释放计数（帧内引用受保护，容量在帧边界回收） |
| 排版/栅格缓存 | `ITextService.CachedLayoutCount`、`CachedRasterCount` |
| 图片缓存 | `IImageService.CachedImageCount` |
| 布局轮数 / 构建次数 | `UiRuntime.LayoutPassCount`、`BuildCount` |
| 诊断信息 | `UiRuntime.Diagnostics` |
