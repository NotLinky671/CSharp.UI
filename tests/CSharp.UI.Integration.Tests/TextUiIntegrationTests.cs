using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;
using CSharp.UI.Skia;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名，否则无法引用。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// 文本实现与核心运行时/控件的跨模块集成测试：
/// 用真实的 SkiaSharp/HarfBuzz 排版驱动布局与 TextField，验证“中英文已提交文本”的完整链路（无窗口）。
/// </summary>
public class TextUiIntegrationTests
{
    private sealed class FieldHost : Component
    {
        public State<string>? Text { get; private set; }

        public State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Text = UseState("text", string.Empty);
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new VStack(new Text(Text.Value), new TextField(Text.ToBinding()) { Width = 260 });
        }
    }

    private static (UiRuntime Runtime, HeadlessUiDispatcher Dispatcher, SkiaTextService Text, FieldHost Host) Create()
    {
        var host = new FieldHost();
        var dispatcher = new HeadlessUiDispatcher();
        var text = new SkiaTextService();
        var runtime = new UiRuntime(host, new UiRuntimeServices
        {
            Dispatcher = dispatcher,
            TextService = text,
            TextureService = null,
            ImageService = null,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });

        runtime.SetViewport(new Size(400, 200), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        return (runtime, dispatcher, text, host);
    }

    [Fact]
    public void Layout_UsesRealTextMetricsAndHonorsWrapping()
    {
        var (runtime, _, _, _) = Create();
        using var _runtime = runtime;

        var textNode = runtime.Root.Require<Text>();
        var label = (Text)textNode.View;

        // 空文本：零尺寸。
        Assert.True(textNode.DesiredSize.Width <= 0.001);

        // 中文 + 英文混排：宽度大于 0，且换行后在有限宽度内不超过约束。
        label = label.WithFont(FontDescription.Default.WithSize(16));
        var wide = runtime.Services.TextService.Layout(new TextLayoutRequest("中文 English 混排")
        {
            Font = label.Font!.Value,
            MaxWidth = double.PositiveInfinity,
        });
        Assert.True(wide.Size.Width > 20);
        Assert.True(wide.Size.Height > 10);

        var wrapped = runtime.Services.TextService.Layout(new TextLayoutRequest("中文 English 混排自动换行测试内容")
        {
            Font = label.Font!.Value,
            MaxWidth = 80,
            Wrapping = TextWrapping.Wrap,
        });
        Assert.True(wrapped.Lines.Count > 1);
        Assert.True(wrapped.Size.Width <= 80.5);
    }

    [Fact]
    public void TextField_AcceptsCommittedChineseAndMovesCaretByGrapheme()
    {
        var (runtime, _, text, host) = Create();
        using var _runtime = runtime;

        var field = runtime.Root.Require<TextField>();
        runtime.SetFocus(field);
        runtime.ProcessPendingWork();

        // 提交中文文本（模拟输入法上屏结果）。
        runtime.DispatchTextInput(new TextInputEvent("中文"));
        runtime.ProcessPendingWork();
        Assert.Equal("中文", host.Text!.PeekValue());

        // 追加英文与 emoji（代理对）。
        runtime.DispatchTextInput(new TextInputEvent("ab\U0001F600"));
        runtime.ProcessPendingWork();
        var expected = "中文ab\U0001F600";
        Assert.Equal(expected, host.Text.PeekValue());

        // 退格删除代理对整体，不会留下半个代理项。
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Backspace));
        runtime.ProcessPendingWork();
        Assert.Equal("中文ab", host.Text.PeekValue());
        Assert.False(host.Text.PeekValue().Contains('\uFFFD'));

        // 光标映射基于真实排版：点击左端应把光标放在靠近行首的字素边界。
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Home));
        runtime.ProcessPendingWork();
        runtime.DispatchTextInput(new TextInputEvent("首"));
        runtime.ProcessPendingWork();
        Assert.Equal("首中文ab", host.Text.PeekValue());

        // 排版缓存被复用（相同请求不重复排版）。
        var misses = text.LayoutMisses;
        runtime.Services.TextService.Layout(new TextLayoutRequest("首中文ab")
        {
            Font = FontDescription.Default,
        });
        runtime.Services.TextService.Layout(new TextLayoutRequest("首中文ab")
        {
            Font = FontDescription.Default,
        });
        Assert.Equal(misses + 1, text.LayoutMisses);
    }

    [Fact]
    public void TextNode_UsesRealRasterizationWhenTexturesAreAvailable()
    {
        var dispatcher = new HeadlessUiDispatcher();
        var text = new SkiaTextService();
        var textures = new RecordingTextureService();
        using var runtime = new UiRuntime(
            new VStack(new Text("中文 raster").WithFont(FontDescription.Default.WithSize(18))),
            new UiRuntimeServices
            {
                Dispatcher = dispatcher,
                TextService = text,
                TextureService = textures,
                Clock = new TestUiClock(),
            });

        runtime.SetViewport(new Size(300, 100), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();

        var command = Assert.Single(frame.Commands.OfType<TextureCommand>());
        Assert.True(command.Texture.IsValid);
        Assert.True(command.Texture.IsAlphaMask);
        Assert.True(command.Texture.PixelWidth > 0);

        // 重复绘制复用同一纹理，不重复上传。
        var uploads = textures.FactoryInvocations;
        runtime.BuildFrame();
        Assert.Equal(uploads, textures.FactoryInvocations);
    }
}

/// <summary>节点查找辅助。</summary>
internal static class TextUiNodeExtensions
{
    public static ViewNode Require<TView>(this ViewNode node)
        where TView : View
        => node.FindDescendant<TView>() ?? throw new InvalidOperationException($"未找到 {typeof(TView).Name} 节点。");
}
