using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 13：主题环境继承、示例写法与文档一致性（无窗口部分）。</summary>
public class ThemeTests
{
    [Fact]
    public void Theme_IsInheritedBySubtree_AndLocalOverrideWins()
    {
        var log = new List<string>();
        using var app = new TestApp(
            new VStack(
                new Text("outer"),
                new VStack(new Text("inner")).Theme(Theme.Dark),
                new Text("explicit").Foreground(Color.Red)),
            new Size(400, 200),
            theme: Theme.Light);
        var frame = app.Frame();

        var outer = app.Runtime.Root.FindDescendants<Text>().First();
        var inner = app.Runtime.Root.FindDescendants<Text>().ToArray()[1];
        var explicitText = app.Runtime.Root.FindDescendants<Text>().Last();

        Assert.Same(Theme.Light, outer.Environment.ResolvedTheme);
        Assert.Same(Theme.Dark, inner.Environment.ResolvedTheme);
        Assert.Equal(Theme.Light.Foreground, outer.Environment.ResolvedForeground);
        Assert.Equal(Theme.Dark.Foreground, inner.Environment.ResolvedForeground);

        // 子级显式设置的前景色不被父级默认值覆盖。
        Assert.Equal(Color.Red, explicitText.Environment.ResolvedForeground);
        Assert.Equal(Theme.Light, explicitText.Environment.ResolvedTheme);
        _ = log;
    }

    [Fact]
    public void FontInheritance_LocalOverrideOnlyAffectsSubtree()
    {
        var small = FontDescription.Default.WithSize(12);
        var large = FontDescription.Default.WithSize(30);

        using var app = new TestApp(
            new VStack(
                new Text("inherits small"),
                new VStack(new Text("large")).Font(large),
                new Text("explicit").WithFont(large)),
            new Size(400, 200));
        app.Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light, Font = small });
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var texts = app.Runtime.Root.FindDescendants<Text>().ToArray();
        Assert.Equal(12, texts[0].Environment.Font!.Value.Size, 3);
        Assert.Equal(30, texts[1].Environment.Font!.Value.Size, 3);
        Assert.Equal(30, ((Text)texts[2].View).Font!.Value.Size, 3);
    }

    [Fact]
    public void ThemeSwitch_UpdatesAllControlVisuals()
    {
        var component = new ThemedControlsHost();
        using var app = new TestApp(component, new Size(300, 200));
        var lightFrame = app.Frame();

        component.SelectedTheme!.Value = Theme.Dark;
        app.Runtime.ProcessPendingWork();
        var darkFrame = app.Frame();

        Assert.NotEmpty(lightFrame);

        var lightRects = lightFrame.OfType<RectangleCommand>().Select(c => c.Fill).ToArray();
        var darkRects = darkFrame.OfType<RectangleCommand>().Select(c => c.Fill).ToArray();
        Assert.NotEqual(lightRects, darkRects);

        // 控件在两种主题下都有可辨识的填充色（不是全透明）。
        Assert.All(lightRects, color => Assert.True(color.A > 0));
        Assert.All(darkRects, color => Assert.True(color.A > 0));
    }

    [Fact]
    public void DocumentedPlanCounterSyntax_CompilesAndRuns()
    {
        // 与设计文档目标写法一致（见 docs/guide/getting-started.md）。
        var component = new PlanStyleCounter();
        using var app = new TestApp(component, new Size(300, 200));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal("计数：0", ((Text)text.View).Content);

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var updated = app.Runtime.Root.Require<Text>();
        Assert.Equal("计数：1", ((Text)updated.View).Content);
    }

    [Fact]
    public void EnvironmentValues_DefaultToLightTheme()
    {
        var environment = new EnvironmentValues();
        Assert.Same(Theme.Light, environment.ResolvedTheme);
        Assert.Equal(Theme.Light.Foreground, environment.ResolvedForeground);
        Assert.Equal(1.0, environment.Opacity, 3);
        Assert.False(environment.Disabled);
    }
}

/// <summary>主题可切换的控件宿主。</summary>
internal sealed class ThemedControlsHost : Component
{
    public State<Theme>? SelectedTheme { get; private set; }

    protected override View Build()
    {
        SelectedTheme = UseState("theme", Theme.Light);
        return new VStack(
                new Button("button"),
                new Toggle(false, _ => { }, "toggle"),
                new Slider(Binding<double>.Constant(0.3)),
                new TextField(Binding<string>.Constant("text")) { Width = 160 })
            .Spacing(8)
            .Padding(12)
            .Background(SelectedTheme.Value.Background)
            .Foreground(SelectedTheme.Value.Foreground)
            .Theme(SelectedTheme.Value);
    }
}

/// <summary>设计文档中的计数器写法。</summary>
internal sealed class PlanStyleCounter : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);

        return new VStack(
            new Text($"计数：{count.Value}"),
            new Button("增加", () => count.Value++))
        .Spacing(12)
        .Padding(24);
    }
}
