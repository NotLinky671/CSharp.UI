using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R16：直接实例化真实控件展示程序集里的 <see cref="GalleryApp"/>，用真实 SkiaSharp 文字服务，
/// 检查页面上全部可见文字命令（含滚动后）都满足“1 纹素 = 1 物理像素、原点是整数物理像素”。
/// </summary>
/// <remarks>
/// 不复制简化视图树；布局与滚动都是示例真实实现，只替换平台纹理/图片/时钟等替身。
/// 滚动通过 <see cref="UiRuntime.DispatchWheel"/> 投递真实滚轮事件（归一化步数）。
/// </remarks>
public sealed class GalleryTextSamplingTests
{
    private const double Epsilon = 0.001;

    private static readonly Size GalleryViewport = new(900, 700);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 控件展示可见文字命令与物理像素一对一且滚动后保持(double pixelScale)
    {
        using var host = new GalleryHost(GalleryViewport, pixelScale);

        var initial = AssertAllTextMapped(host, pixelScale, "初始");
        Assert.True(initial.Length >= 10, $"初始可见文字命令应足够多，实际 {initial.Length}。");
        var initialOrigins = initial.Select(command => command.Bounds.Y).ToArray();

        // 真实滚轮：向下滚动整页，文字位置必须变化（证明真的滚动了）。
        var outerPoint = new Point(host.Outer.Frame.X + 8, host.Outer.Frame.Y + 6);
        for (var i = 0; i < 4; i++)
        {
            host.Wheel(outerPoint, -1);
        }

        Assert.True(host.Outer.ScrollOffsetY > 0, "外层页面应已向下滚动。");
        var scrolled = AssertAllTextMapped(host, pixelScale, "向下滚动后");
        Assert.NotEqual(initialOrigins, scrolled.Select(command => command.Bounds.Y).ToArray());

        // 向上滚回：同样必须保持映射。
        for (var i = 0; i < 4; i++)
        {
            host.Wheel(outerPoint, 1);
        }

        Assert.Equal(0, host.Outer.ScrollOffsetY, 6);
        AssertAllTextMapped(host, pixelScale, "向上滚回后");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 控件展示内层滚动容器滚动后文字仍按物理像素一对一(double pixelScale)
    {
        using var host = new GalleryHost(GalleryViewport, pixelScale);
        BringInnerIntoView(host);

        // 先让内层真正滚动一段，再检查内层可见文字。
        var innerTexts = InnerTextCommands(host);
        Assert.NotEmpty(innerTexts);
        var before = innerTexts.Select(command => command.Bounds.Y).ToArray();

        host.Wheel(host.Inner.Frame.Center, -1);
        Assert.True(host.Inner.ScrollOffsetY > 0, "内层滚动容器应已滚动。");

        var after = InnerTextCommands(host);
        Assert.NotEmpty(after);
        Assert.NotEqual(before, after.Select(command => command.Bounds.Y).ToArray());
        AssertMapped(after, pixelScale, "内层滚动后");
    }

    /// <summary>检查当前帧全部可见文字命令，并返回它们。</summary>
    private static TextureCommand[] AssertAllTextMapped(GalleryHost host, double pixelScale, string phase)
    {
        var texts = host.Runtime.BuildFrame().Commands
            .OfType<TextureCommand>()
            .Where(command => command.Texture.IsAlphaMask)
            .ToArray();
        Assert.NotEmpty(texts);
        AssertMapped(texts, pixelScale, phase);
        return texts;
    }

    private static void AssertMapped(TextureCommand[] texts, double pixelScale, string phase)
    {
        foreach (var text in texts)
        {
            Assert.Equal(0, Math.IEEERemainder(text.Bounds.X * pixelScale, 1), 6);
            Assert.Equal(0, Math.IEEERemainder(text.Bounds.Y * pixelScale, 1), 6);
            Assert.Equal(text.Texture.PixelWidth, text.Bounds.Width * pixelScale, 6);
            Assert.Equal(text.Texture.PixelHeight, text.Bounds.Height * pixelScale, 6);
        }
    }

    /// <summary>取内层滚动视图视口裁剪区内的文字命令。</summary>
    private static TextureCommand[] InnerTextCommands(GalleryHost host)
    {
        var commands = host.Runtime.BuildFrame().Commands;
        var result = new List<TextureCommand>();
        var depth = 0;
        var targetDepth = -1;
        foreach (var command in commands)
        {
            switch (command)
            {
                case PushClipCommand push:
                    depth++;
                    if (targetDepth < 0 && IsSameRect(push.Bounds, host.Inner.Frame))
                    {
                        targetDepth = depth;
                    }

                    break;
                case PopClipCommand:
                    if (targetDepth == depth)
                    {
                        targetDepth = -1;
                    }

                    depth--;
                    break;
                case TextureCommand texture when targetDepth > 0 && texture.Texture.IsAlphaMask:
                    result.Add(texture);
                    break;
            }
        }

        return [.. result];
    }

    private static bool IsSameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < Epsilon
            && Math.Abs(a.Y - b.Y) < Epsilon
            && Math.Abs(a.Width - b.Width) < Epsilon
            && Math.Abs(a.Height - b.Height) < Epsilon;

    /// <summary>让外层整页滚动，直到 120 像素的内层视口完全进入外层可见范围。</summary>
    private static void BringInnerIntoView(GalleryHost host)
    {
        var outerOnly = new Point(host.Outer.Frame.X + 8, host.Outer.Frame.Y + 6);
        for (var i = 0; i < 200 && !FullyInside(host.Inner.Frame, host.Outer.Frame); i++)
        {
            host.Wheel(outerOnly, -1);
        }

        Assert.True(FullyInside(host.Inner.Frame, host.Outer.Frame), "外层滚动后内层视口应完全可见。");
    }

    private static bool FullyInside(Rect target, Rect clip)
        => !target.IsEmpty
            && target.X >= clip.X - Epsilon
            && target.Y >= clip.Y - Epsilon
            && target.Right <= clip.Right + Epsilon
            && target.Bottom <= clip.Bottom + Epsilon;

    /// <summary>真实 <see cref="GalleryApp"/> + 真实 <see cref="SkiaTextService"/>，只替换平台服务。</summary>
    private sealed class GalleryHost : IDisposable
    {
        private readonly RecordingTextureService _textures = new();
        private readonly SkiaTextService _textService = new();
        private readonly StubWheelScrollSettingsService _wheel = new(WheelScrollSettings.Default);

        public GalleryHost(Size viewport, double pixelScale)
        {
            Runtime = new UiRuntime(new GalleryApp(), new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = _textService,
                TextureService = _textures,
                ImageService = CreateImages(),
                Clock = new TestUiClock(),
                WheelScrollSettings = _wheel,
            });

            Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
            Runtime.SetViewport(viewport, pixelScale);
            Runtime.Start();
            Runtime.ProcessPendingWork();

            // 通过示例注入的 Key 定位；新增 demo ScrollView 不影响命中。
            var outerNode = GalleryScrollLookup.RootScroll(Runtime);
            var innerNode = GalleryScrollLookup.NestedScroll(Runtime);
            ScrollViews = [outerNode, innerNode];
        }

        public UiRuntime Runtime { get; }

        public IReadOnlyList<ViewNode> ScrollViews { get; }

        public ViewNode Outer => ScrollViews[0];

        public ViewNode Inner => ScrollViews[1];

        public void Wheel(Point logicalPosition, double notches)
        {
            Runtime.DispatchWheel(new WheelEvent(logicalPosition, 0, notches, KeyModifiers.None, WheelDeltaMode.Steps));
            Runtime.ProcessPendingWork();
        }

        public void Dispose()
        {
            Runtime.Dispose();
            _textService.Dispose();
        }

        private static StubImageService CreateImages()
        {
            var images = new StubImageService();
            images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
            return images;
        }
    }
}
