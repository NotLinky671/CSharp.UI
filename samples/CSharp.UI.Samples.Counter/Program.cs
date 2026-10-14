using CSharp.UI;
using CSharp.UI.Samples;

namespace CSharp.UI.Samples.Counter;

/// <summary>
/// 计数器示例：与设计文档中的目标写法保持一致，验证 Component.Build / UseState / VStack / Text / Button 链路。
/// </summary>
public sealed class Counter : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);

        return new VStack(
            new Text($"计数：{count.Value}").WithFont(FontDescription.Default.WithSize(28)),
            new Text($"已点击 {count.Value} 次").WithFont(FontDescription.Default.WithSize(13)),
            new HStack(
                new Button("增加", () => count.Value++),
                new Button("减少", () => count.Value--),
                new Button("重置", () => count.Value = 0)),
            new Text("点击按钮或按 Tab 聚焦后用 Enter/Space 激活。")
                .WithFont(FontDescription.Default.WithSize(12)))
        .Spacing(12)
        .Padding(24);
    }
}

internal static class Program
{
    private static int Main()
    {
        return UiApplication.Run(
            new Counter(),
            new UiApplicationOptions
            {
                Title = "CSharp.UI — 计数器",
                Width = 480,
                Height = 320,
                Theme = Theme.Light,
                FrameRendered = SmokeTestHook.CreateFrameHook("Counter"),
            });
    }
}
