# CSharp.UI：基于 OpenTK 的 C# 声明式 UI 框架

计划日期：2026-10-14

## 目标与技术基线

从空仓库构建一个可用于小型桌面应用的最小框架，支持 Windows、macOS、Linux，提供统一自绘外观、状态驱动更新、组件组合和链式修饰。

- 使用 **.NET 10 / C# 14**；本机已安装对应 SDK，.NET 10 为 LTS。[支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- 固定使用 **OpenTK 4.9.4**，通过 `NativeWindow` 管理窗口、输入和图形上下文。[版本说明](https://github.com/opentk/opentk/releases/tag/4.9.4)
- 渲染基线为 **OpenGL 3.3 Core、GLSL 330**；macOS 创建 Forward Compatible 上下文，仅使用三平台共有能力。[GLFW 平台兼容说明](https://www.glfw.org/docs/latest/compat_guide.html)
- 文本使用 **SkiaSharp 3.119.4 + SkiaSharp.HarfBuzz** 做 CPU 排版与栅格化；纹理上传、图形绘制和最终合成全部使用 OpenTK/OpenGL。[SkiaSharp 版本说明](https://mono.github.io/SkiaSharp/docs/releases/3.119.4.html)

## 架构与公开 API

### 声明式组件与状态

采用“不可变视图描述树 + 持久运行时节点树”。应用重新声明 UI，框架负责节点复用、布局、交互和资源生命周期。

公开 API 包含：

- `View`：视图描述基类。
- `Component`：可组合组件，通过 `Build()` 返回视图。
- `State<T>`：可观察状态，通过 `Value` 读写。
- `Binding<T>`：双向绑定，连接状态与输入控件。
- `UiApplication.Run(root, options)`：启动单窗口应用。
- `UiDispatcher.Post(action)`：将后台任务结果投递到 UI 线程。

目标写法：

```csharp
public sealed class Counter : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);

        return new VStack(
            new Text($"计数：{count.Value}"),
            new Button("增加", () => count.Value++)
        )
        .Spacing(12)
        .Padding(24);
    }
}

UiApplication.Run(new Counter());
```

状态由运行时组件节点持有，`UseState` 通过组件内的名称定位，避免重新构建描述时丢失状态。

更新规则：

- 构建期间读取状态，自动登记组件依赖；值实际变化才触发更新。
- 同一轮事件中的多次变化合并处理，避免递归构建。
- 节点默认按类型和结构位置匹配；动态集合使用显式 `Key`。
- 节点复用时更新属性和事件处理器，保留焦点、选择与滚动位置；卸载时释放订阅和资源。
- UI 状态、事件处理和 OpenGL 操作统一在主线程执行。

### 项目分层

| 模块 | 职责 |
|---|---|
| `CSharp.UI` | 视图、组件、状态、协调更新、布局、控件与平台无关绘制命令 |
| `CSharp.UI.Text` | 字体解析、fallback、文本排版、测量与栅格化 |
| `CSharp.UI.OpenTK` | 桌面宿主、事件循环、输入适配、OpenGL 渲染与资源管理 |
| 示例与测试 | 控件展示、实际应用示例、核心单元测试及图形集成验证 |

核心模块不依赖 OpenTK 或 SkiaSharp 类型；公开 API 使用框架自己的颜色、尺寸、矩形和字体描述类型。

### 布局、控件与修饰符

首版实现：

- 布局：`VStack`、`HStack`、`ZStack`、`Spacer`、单轴 `ScrollView`。
- 内容与控件：`Text`、`Image`、`Rectangle`、`Button`、`Toggle`、`Slider`、单行 `TextField`。
- 动态内容：普通 C# 条件表达式、带稳定键的 `ForEach`。
- 修饰符：`Padding`、`Frame`、`Background`、`Foreground`、`Border`、`CornerRadius`、`Opacity`、矩形 `Clip`、`Disabled`、`Key`。
- 样式：统一的明暗主题，字体和颜色通过父级环境继承；首版手动切换主题。

布局采用约束下的 `Measure / Arrange` 两阶段计算，统一使用逻辑像素。Stack 支持间距、对齐和 Spacer 分配剩余空间；ScrollView 在滚动方向测量内容尺寸，并裁剪至视口。

修饰符返回新的包装视图，调用顺序具有语义。例如先 `Padding` 再 `Background`，背景覆盖内边距。

### 渲染、文本与输入

- 渲染器消费按绘制顺序排列的命令，支持矩形、圆角、边框、图片、文本、透明度和嵌套矩形裁剪。
- 使用着色器、顶点缓冲和相邻命令批处理；保持透明内容与层叠顺序正确。
- 首版缓存排版结果和文本纹理，按内容、字体、宽度与 DPI 区分缓存，并设置容量上限；暂不实现字形图集。
- 字体支持应用指定文件及系统 fallback；示例携带许可允许分发的中英文字体，保证示例与截图可复现。
- `Text` 支持中英文混排、换行、对齐和省略；首版不承诺完整双向文本排版及彩色 Emoji。
- 输入包含命中测试、悬停、按下、指针捕获、滚轮、Tab 焦点和按钮键盘激活。
- `TextField` 支持已提交的 Unicode 文本、光标、鼠标与键盘选择、删除、剪贴板和横向滚动；编辑按字素边界处理。
- 完整 IME 组合文本与候选窗定位后续实现；首版支持中文显示、粘贴及宿主能够提供的提交文本。

事件循环按需唤醒：输入、状态变化、窗口重绘和光标闪烁触发绘制；静止界面等待事件。尺寸和 DPI 变化重新计算布局、视口及文本缓存，最小化时跳过绘制。

## 实施顺序

1. **工程与渲染底座**  
   建立解决方案、模块、依赖版本管理和测试工程；完成窗口、OpenGL 初始化、基础图形、裁剪、DPI 和资源释放。

2. **声明式运行时与布局**  
   实现视图描述、组件、状态依赖、节点协调更新、Key 和布局系统；完成可交互计数器示例。

3. **文本与基础交互**  
   接入字体和排版、文本纹理缓存、焦点及指针系统，完成 Button、Toggle、Slider、ScrollView 和 Image。

4. **输入控件与交付**  
   完成 TextField、主题、控件展示和待办列表示例；补齐 API 文档、平台运行说明、测试及打包验证。框架包保持预发布状态，不在此阶段自动发布 NuGet。

## 测试与验收

- **状态与协调更新**：验证变化合并、依赖移除、组件卸载，以及带键列表增删重排后状态仍属于原项目。
- **布局**：验证嵌套 Stack、Spacer、Frame、修饰符顺序、文本换行、滚动边界及不同 DPI。
- **交互**：验证裁剪外内容无法点击、拖动捕获、禁用控件、焦点遍历、键盘激活和焦点节点移除。
- **文本编辑**：验证中文粘贴、代理对与组合字符删除、选择替换、剪贴板和光标位置。
- **渲染与资源**：使用离屏 framebuffer 验证透明混合、裁剪、层叠顺序；重复更新和关闭窗口后资源计数回落，缓存保持有界。
- **跨平台**：三平台执行构建和核心测试；Linux 使用 Mesa/Xvfb 运行图形测试；Windows 与 macOS 真机验证窗口、输入和 DPI。打包检查覆盖 Windows x64、Linux x64、macOS x64/arm64 的原生依赖。
- **完成标准**：同一份计数器、待办列表和控件展示代码可在三平台运行；更新保留交互状态，静止界面不持续重绘，无持续资源增长。未实际验证的平台明确记录为待验证。

## 已确定的边界与默认值

- 面向常规桌面工具，首版单窗口，采用普通 C#，不引入 XAML 或源生成器。
- 首版允许字体与排版原生依赖，所有最终渲染通过 OpenTK。
- 暂缓移动端、浏览器、宿主嵌入、动画系统、列表虚拟化、多行编辑、完整 IME 和系统无障碍桥接。
- 默认名称与命名空间为 `CSharp.UI`；首版以正确性、组件易用性和跨平台一致性为优先。
