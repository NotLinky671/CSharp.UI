using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

/// <summary>
/// AST→runtime 真实管线 smoke：ThemedBar 主题色解析 + 实际尺寸。
/// </summary>
public class MarkdownRuntimeSmokeTests
{
    [Fact]
    public void ThemedBar_主题切换时Fill与尺寸正确()
    {
        var doc = Markdown.Parse("---");
        var view = new MarkdownBlockView { Document = doc };

        // Light theme
        using var runtime = new UiRuntime(view, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService(),
        });
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        runtime.SetViewport(new Size(400, 100), 1.0);
        runtime.ProcessPendingWork();
        var lightRect = runtime.BuildFrame().Commands.OfType<RectangleCommand>().First();
        Assert.Equal(Theme.Light.Border, lightRect.Fill);
        Assert.True(lightRect.Bounds.Height > 0, "hr 应有非零高度");

        // Dark theme — 同一 doc/view，切 environment 重 BuildFrame
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
        runtime.ProcessPendingWork();
        var darkRect = runtime.BuildFrame().Commands.OfType<RectangleCommand>().First();
        Assert.Equal(Theme.Dark.Border, darkRect.Fill);
        Assert.NotEqual(lightRect.Fill, darkRect.Fill); // 主题切换真实生效
    }

    private static ViewNode? FindNode(ViewNode node, Func<View, bool> predicate)
    {
        if (predicate(node.View))
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }
}
