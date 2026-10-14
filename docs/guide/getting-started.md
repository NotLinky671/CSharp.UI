# 入门：构建第一个 CSharp.UI 应用

本指南给出从零到可运行窗口的最短路径，并说明环境要求与常用命令。

## 环境要求

| 项目 | 要求 |
|---|---|
| .NET SDK | **10.0**（本仓库固定 `net10.0` / C# 14） |
| 操作系统 | Windows / macOS / Linux 桌面环境（需要 OpenGL 3.3 Core 上下文） |
| 图形 | 真实桌面会话；Linux 无显示器时可用 Mesa + Xvfb（见 [平台与运行说明](platform-notes.md)） |
| 字体 | 使用系统字体与字体回退；仓库不重新分发第三方字体二进制文件 |

## 获取与构建

```bash
dotnet build CSharp.UI.slnx
```

运行测试（核心单元测试不需要窗口与字体原生库之外的依赖）：

```bash
dotnet run --project tests/CSharp.UI.Tests
dotnet run --project tests/CSharp.UI.Text.Tests
dotnet run --project tests/CSharp.UI.OpenTK.Tests   # 需要 OpenGL 上下文
```

测试使用 **xUnit v3 + Microsoft.Testing.Platform 进程内运行器**，因此用 `dotnet run`（或 `dotnet test`）执行；
筛选方式见 [测试指南](testing.md)。

## 最小应用

```csharp
using CSharp.UI;
using CSharp.UI.Skia;

public sealed class Hello : Component
{
    protected override View Build()
        => new Text("你好，CSharp.UI").Padding(24);
}

internal static class Program
{
    private static int Main()
    {
        using var textService = new SkiaTextService();
        using var imageService = new SkiaImageService();

        return UiApplication.Run(new Hello(), new UiApplicationOptions
        {
            Title = "第一个应用",
            Width = 480,
            Height = 240,
            TextService = textService,
            ImageService = imageService,
        });
    }
}
```

要点：

- `Component.Build()` 每次构建都返回**全新的视图描述**；框架负责复用运行时节点。
- 文字排版/图片解码由 `CSharp.UI.Skia` 提供，宿主（`CSharp.UI.OpenTK`）在 `UiApplicationOptions` 中组装。
- `UiApplication.Run` 必须在主线程调用，并且只支持单窗口。

## 状态与交互

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
```

- 构建期间读取 `count.Value` 会把当前组件登记为依赖；写入新值只重建读取过它的组件。
- 同一轮事件中的多次写入会合并为一次重建，不需要手动请求重绘。
- 状态只能在 UI 线程读写；后台线程请使用 `UiDispatcher.Post(action)`（见[状态生命周期](state-lifecycle.md)）。

## 三个可运行示例

| 示例 | 重点 | 运行 |
|---|---|---|
| `samples/CSharp.UI.Samples.Counter` | 组件、状态、按钮与键盘激活 | `dotnet run --project samples/CSharp.UI.Samples.Counter` |
| `samples/CSharp.UI.Samples.TodoList` | TextField + Binding + 带键 `ForEach` + 增删重排 + ScrollView | `dotnet run --project samples/CSharp.UI.Samples.TodoList` |
| `samples/CSharp.UI.Samples.ControlGallery` | 全部首版控件、布局、修饰符、主题、图片与中英文文本 | `dotnet run --project samples/CSharp.UI.Samples.ControlGallery` |

三份示例代码完全相同地适用于 Windows、macOS 与 Linux；宿主差异（GLFW、字体回退、剪贴板）由框架处理。

## 下一步

- [公开 API 参考](api-reference.md)
- [状态与生命周期](state-lifecycle.md)
- [修饰符顺序](modifier-order.md)
- [Key 与动态列表](keys-and-lists.md)
- [编写自定义组件](custom-components.md)
- [平台、字体与原生依赖](platform-notes.md)
