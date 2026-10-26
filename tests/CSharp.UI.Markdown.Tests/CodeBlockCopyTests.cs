using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Markdown.Tests;

/// <summary>
/// 代码块复制按钮的消费者可观察行为：真实 <see cref="UiRuntime"/> +
/// <see cref="StubClipboardService"/> 指针点击 → 剪贴板收到原始代码文本。
/// 断言精确内容/写次数/开关行为，不断言内部视图结构或默认常量。
/// </summary>
public class CodeBlockCopyTests
{
    // fenced csharp + 前导 2 空格 + 空行 + Unicode；Markdig Lines.ToString()
    // 保行尾换行——期望串手写固定，不调用同一实现产期望。
    private const string CodeMarkdown =
        "```csharp\n" +
        "var x = \"héllo\";\n" +
        "  return x;\n" +
        "\n" +
        "```\n";

    // 预期：fence 行与语言标识不进复制文本；行间/空行换行保留；末行无尾部换行（Markdig Lines.ToString 语义）。
    private const string ExpectedCopy = "var x = \"héllo\";\n  return x;\n";

    private static (UiRuntime Runtime, StubClipboardService Clipboard) Host(
        string markdown, MarkdownStyleOptions? style = null, int width = 400)
    {
        var doc = Markdown.Parse(markdown);
        var view = new MarkdownBlockView { Document = doc, Style = style };
        var clipboard = new StubClipboardService();
        var runtime = new UiRuntime(view, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService(),
            Clipboard = clipboard,
        });
        runtime.SetViewport(new Size(width, 300), 1.0);
        runtime.ProcessPendingWork();
        return (runtime, clipboard);
    }

    private static ViewNode? FindCopyButton(ViewNode node)
    {
        if (node.View is Button { Label: "Copy" })
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            if (FindCopyButton(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void Click(UiRuntime runtime, Point position)
    {
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, position));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, position, PointerButton.Left));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, position, PointerButton.Left));
    }

    [Fact]
    public void 复制按钮_点击_剪贴板收到精确代码原文()
    {
        var (runtime, clipboard) = Host(CodeMarkdown);
        using var _ = runtime;

        var button = FindCopyButton(runtime.Root!);
        Assert.NotNull(button); // 默认开
        Click(runtime, button!.Frame.Center);

        Assert.Equal(ExpectedCopy, clipboard.GetText());
        Assert.Equal(1, clipboard.SetCount);
    }

    [Fact]
    public void ShowCodeCopyButton_关闭_同位置点击不写剪贴板()
    {
        var (runtime, clipboard) = Host(
            CodeMarkdown, new MarkdownStyleOptions { ShowCodeCopyButton = false });
        using var _ = runtime;

        Assert.Null(FindCopyButton(runtime.Root!));
        // 代码块右上区域点击不写剪贴板。
        Click(runtime, new Point(runtime.ViewportSize.Width - 10, 10));
        Assert.Equal(0, clipboard.SetCount);
    }

    /// <summary>
    /// 组件用新 <see cref="MarkdownDocument"/> 重 Build：原按钮位置现在复制**新**文本——
    /// 验证捕获文本随描述更新，不留旧快照。
    /// </summary>
    private sealed class SwapDoc : Component
    {
        private readonly MarkdownDocument _a;
        private readonly MarkdownDocument _b;
        public State<bool>? Flag;

        public SwapDoc(MarkdownDocument a, MarkdownDocument b)
        {
            _a = a;
            _b = b;
        }

        protected override View Build()
        {
            Flag ??= UseState("swap", false);
            return new MarkdownBlockView { Document = Flag.Value ? _b : _a };
        }
    }

    [Fact]
    public void 文档更新_同位置复制_收到新文本()
    {
        var docA = Markdown.Parse("```\nAAA\n```");
        var docB = Markdown.Parse("```\nBBB\n```");
        var clipboard = new StubClipboardService();
        var component = new SwapDoc(docA, docB);
        var runtime = new UiRuntime(component, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService(),
            Clipboard = clipboard,
        });
        using var _ = runtime;
        runtime.SetViewport(new Size(400, 300), 1.0);
        runtime.ProcessPendingWork();

        var button = FindCopyButton(runtime.Root!);
        Assert.NotNull(button);
        var center = button!.Frame.Center;
        Click(runtime, center);
        Assert.Equal("AAA", clipboard.GetText());

        // 换 doc：同位置（按钮仍在右上）复制应给新文本。
        component.Flag!.Value = true;
        runtime.ProcessPendingWork();
        Click(runtime, center);
        Assert.Equal("BBB", clipboard.GetText());
        Assert.Equal(2, clipboard.SetCount);
    }
}
