using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class CandidateOverlayRenderingTests
{
    private readonly GlContextFixture _fixture;

    public CandidateOverlayRenderingTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void CandidateOverlay_RendersChineseAfterContent_AndConstrainsAndHighlightsSelection()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService);
        var baseline = runtime.BuildFrame();
        var overlay = new CandidateOverlay();
        runtime.FrameOverlay = overlay.Draw;
        overlay.SetSnapshot(Snapshot(["你好", "候选文字"], selected: 1));
        overlay.SetGeometry(new Rect(30, 25, 196, 190), new Rect(225, 88, 20, 20));

        var frame = runtime.BuildFrame();
        Assert.True(frame.Commands.Count > baseline.Commands.Count);
        var overlayBackgroundIndex = Array.FindIndex(frame.Commands.ToArray(), command =>
            command is RectangleCommand rectangle
            && rectangle.Fill == Theme.Light.Surface
            && rectangle.Stroke == Theme.Light.Border);
        Assert.True(overlayBackgroundIndex > 0, "Overlay background must be appended after content.");

        var overlayBackground = Assert.IsType<RectangleCommand>(frame.Commands[overlayBackgroundIndex]);
        Assert.Contains(frame.Commands, command => command is TextureCommand);
        var selection = Assert.Single(frame.Commands.OfType<RectangleCommand>(), command =>
            command.Fill == Theme.Light.SelectionBackground && command.Stroke is null);
        var client = new Rect(30, 25, 196, 190);
        Assert.True(Contains(client, overlayBackground.Bounds),
            $"overlay {overlayBackground.Bounds} must remain inside client {client}");
        Assert.True(overlayBackground.Bounds.Left >= client.Left);
        Assert.True(overlayBackground.Bounds.Right <= client.Right);
        Assert.True(overlayBackground.Bounds.Top >= client.Top);
        Assert.True(overlayBackground.Bounds.Bottom <= client.Bottom);

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();
        Assert.Equal(Color.Red, Pixel(pixels, 5, 5));
        Assert.True(HasTextInk(pixels, frame.Commands.OfType<TextureCommand>()),
            "Real Skia-rasterized Chinese candidate text must produce visible framebuffer pixels.");
        var selectedSample = Pixel(scope.Surface.ReadPixels(),
            selection.Bounds.Right - 4, selection.Bounds.Y + (selection.Bounds.Height / 2));
        AssertColor.Equal(Theme.Light.SelectionBackground, selectedSample);
        var firstRow = frame.Commands.OfType<TextureCommand>().First().Bounds;
        var unselectedSample = Pixel(scope.Surface.ReadPixels(),
            firstRow.Right + CandidateOverlay.OuterPadding + 8,
            firstRow.Y + (firstRow.Height / 2));
        Assert.NotEqual(Theme.Light.SelectionBackground, unselectedSample);

        SaveEvidencePng(pixels);
    }

    [Fact]
    public void CandidateOverlay_InvalidEmptyOrMissingGeometryAddsNoCommands()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService);
        var overlay = new CandidateOverlay();
        runtime.FrameOverlay = overlay.Draw;
        var baseline = runtime.BuildFrame();
        var client = new Rect(0, 0, 240, 220);

        overlay.SetSnapshot(Snapshot(["你好"], selected: -1, valid: false));
        overlay.SetGeometry(client, new Rect(40, 40, 20, 20));
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
        overlay.SetSnapshot(Snapshot([], selected: -1));
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
        overlay.SetSnapshot(Snapshot(["你好"], selected: -1));
        overlay.SetGeometry(client, null);
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
        overlay.Clear();
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
    }

    [Fact]
    public void CandidateOverlay_DoesNotDrawWhenNoVerticalPlacementOrClientTooNarrow()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService);
        var overlay = new CandidateOverlay();
        runtime.FrameOverlay = overlay.Draw;
        var baseline = runtime.BuildFrame();

        overlay.SetSnapshot(Snapshot(["你好", "世界"], selected: 0));
        overlay.SetGeometry(new Rect(20, 20, 210, 100), new Rect(80, 58, 30, 20));
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);

        overlay.SetSnapshot(Snapshot(["你好"], selected: 0));
        overlay.SetGeometry(new Rect(20, 20, 100, 200), new Rect(40, 80, 20, 20));
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
    }

    [Fact]
    public void CandidateOverlay_ContentAndGeometryClearIndependently()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService);
        var overlay = new CandidateOverlay();
        runtime.FrameOverlay = overlay.Draw;
        var baseline = runtime.BuildFrame();
        var client = new Rect(10, 10, 240, 200);
        var row = new Rect(50, 60, 20, 20);

        overlay.SetSnapshot(Snapshot(["你好"], selected: 0));
        overlay.SetGeometry(client, row);
        Assert.True(runtime.BuildFrame().Commands.Count > baseline.Commands.Count);

        // End：只清候选，几何保留；同一输入框再次出候选时直接复用几何。
        overlay.ClearSnapshot();
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
        overlay.SetSnapshot(Snapshot(["世界"], selected: 0));
        Assert.True(runtime.BuildFrame().Commands.Count > baseline.Commands.Count);

        // 几何缺失：候选仍在缓存，但零命令；恢复几何后无需重设快照。
        overlay.SetGeometry(client, null);
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
        overlay.SetGeometry(client, row);
        Assert.True(runtime.BuildFrame().Commands.Count > baseline.Commands.Count);

        // 失焦/卸载：两者都清，恢复几何也不会重新显示旧候选。
        overlay.Clear();
        overlay.SetGeometry(client, row);
        Assert.Equal(baseline.Commands, runtime.BuildFrame().Commands);
    }

    private static UiRuntime CreateRuntime(GlTestScope scope, SkiaTextService textService)
    {
        var runtime = new UiRuntime(new Rectangle(Color.Red), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textService,
            RichTextService = textService as IRichTextService,
            TextureService = scope.Textures,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light, Font = FontDescription.Default });
        runtime.SetViewport(new Size(256, 256), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    private static CandidateSnapshot Snapshot(IReadOnlyList<string> entries, int selected, bool valid = true)
    {
        var snapshotEntries = entries.Select((text, index) => new CandidateSnapshotEntry((uint)index, text)).ToArray();
        var selectionIndex = selected >= 0 ? (uint)selected : uint.MaxValue;
        return new CandidateSnapshot(0, 1, snapshotEntries, selected, selectionIndex,
            (uint)entries.Count, 0, 0, valid, valid ? null : "invalid");
    }

    private static bool Contains(Rect outer, Rect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top
            && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static Color Pixel(PixelBuffer pixels, double x, double y)
        => pixels.GetColor(Math.Clamp((int)Math.Floor(x), 0, pixels.Width - 1),
            Math.Clamp((int)Math.Floor(y), 0, pixels.Height - 1));

    private static bool HasTextInk(PixelBuffer pixels, IEnumerable<TextureCommand> commands)
    {
        foreach (var command in commands)
        {
            var left = Math.Clamp((int)Math.Floor(command.Bounds.Left), 0, pixels.Width - 1);
            var top = Math.Clamp((int)Math.Floor(command.Bounds.Top), 0, pixels.Height - 1);
            var right = Math.Clamp((int)Math.Ceiling(command.Bounds.Right), 0, pixels.Width - 1);
            var bottom = Math.Clamp((int)Math.Ceiling(command.Bounds.Bottom), 0, pixels.Height - 1);
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var color = pixels.GetColor(x, y);
                    if (color.R < 150 && color.G < 150 && color.B < 150)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void SaveEvidencePng(PixelBuffer pixels)
    {
        var path = Environment.GetEnvironmentVariable("TI07_P3B_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        Marshal.Copy(pixels.Pixels, 0, bitmap.GetPixels(), pixels.Pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        png.SaveTo(stream);
    }
}
