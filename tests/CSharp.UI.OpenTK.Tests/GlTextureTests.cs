using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 纹理上传/绘制、像素格式约定、缓存淘汰与资源计数测试。
/// </summary>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GlTextureTests
{
    private static readonly Color Background = new(255, 255, 255);

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具。</summary>
    public GlTextureTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void RGBA纹理按像素绘制()
    {
        using var scope = _fixture.CreateScope();
        var buffer = OpaqueRgba(2, 2, Color.Red, Color.Green, Color.Blue, Color.White);
        var handle = scope.Textures.GetOrCreate("rgba", () => buffer);
        Assert.False(handle.IsAlphaMask);
        Assert.Equal(2, handle.PixelWidth);

        var frame = Frame(
            new Size(8, 8),
            new TextureCommand(handle, new Rect(0, 0, 8, 8), new Rect(0, 0, 2, 2), Color.White));

        var stats = scope.Render(frame);

        Assert.Equal(1, stats.TextureCommands);
        Assert.Equal(1, stats.Batches);
        AssertColor.Equal(Color.Red, scope.GetPixel(1, 1));
        AssertColor.Equal(Color.Green, scope.GetPixel(6, 1));
        AssertColor.Equal(Color.Blue, scope.GetPixel(1, 6));
        AssertColor.Equal(Color.White, scope.GetPixel(6, 6));
    }

    [Fact]
    public void BGRA预乘纹理通道顺序正确()
    {
        using var scope = _fixture.CreateScope();
        var buffer = new PixelBuffer(1, 1, PixelFormat.Bgra8888Premultiplied);
        // 预乘不透明红：B=0、G=0、R=255、A=255。
        buffer.Pixels[0] = 0;
        buffer.Pixels[1] = 0;
        buffer.Pixels[2] = 255;
        buffer.Pixels[3] = 255;

        var handle = scope.Textures.GetOrCreate("bgra", () => buffer);
        var frame = Frame(
            new Size(4, 4),
            new TextureCommand(handle, new Rect(0, 0, 4, 4), new Rect(0, 0, 1, 1), Color.White));

        scope.Render(frame);

        AssertColor.Equal(Color.Red, scope.GetPixel(2, 2));
    }

    [Fact]
    public void 预乘半透明纹理按ONE混合()
    {
        using var scope = _fixture.CreateScope();
        // 预乘 50% 红：R=128、A=128。
        var buffer = new PixelBuffer(1, 1, PixelFormat.Rgba8888Premultiplied);
        buffer.Pixels[0] = 128;
        buffer.Pixels[1] = 0;
        buffer.Pixels[2] = 0;
        buffer.Pixels[3] = 128;

        var handle = scope.Textures.GetOrCreate("premul", () => buffer);
        var frame = Frame(
            new Size(4, 4),
            new TextureCommand(handle, new Rect(0, 0, 4, 4), new Rect(0, 0, 1, 1), Color.White));

        scope.Render(frame);

        // 预乘值叠加在白色上：0.502 + 0.498 = 1.0（R），G/B 为 0.498。
        AssertColor.Equal(new Color(255, 127, 127), scope.GetPixel(2, 2));
    }

    [Fact]
    public void Alpha8遮罩按tint着色且边缘无色晕()
    {
        using var scope = _fixture.CreateScope();
        var mask = new PixelBuffer(2, 1, PixelFormat.Alpha8);
        mask.Pixels[0] = 255;
        mask.Pixels[1] = 128;

        var handle = scope.Textures.GetOrCreate("mask", () => mask);
        Assert.True(handle.IsAlphaMask);
        Assert.Equal(2, handle.PixelWidth);

        var frame = Frame(
            new Size(2, 1),
            new TextureCommand(handle, new Rect(0, 0, 2, 1), new Rect(0, 0, 2, 1), Color.Red));

        scope.Render(frame);

        // tint 预乘 × 覆盖率：全遮罩像素为纯红，半遮罩像素与白色背景混合后仍是红色调。
        AssertColor.Equal(Color.Red, scope.GetPixel(0, 0));
        AssertColor.Equal(new Color(255, 127, 127), scope.GetPixel(1, 0));
    }

    [Fact]
    public void 遮罩tint的不透明度参与计算()
    {
        using var scope = _fixture.CreateScope();
        var mask = new PixelBuffer(1, 1, PixelFormat.Alpha8);
        mask.Pixels[0] = 255;
        var handle = scope.Textures.GetOrCreate("mask-opacity", () => mask);

        var frame = Frame(
            new Size(2, 2),
            new TextureCommand(handle, new Rect(0, 0, 2, 2), new Rect(0, 0, 1, 1), new Color(0, 0, 255, 128)));

        scope.Render(frame);

        // 蓝色 50% 不透明 × 全遮罩：0.502 蓝叠加白底。
        AssertColor.Equal(new Color(127, 127, 255), scope.GetPixel(1, 1));
    }

    [Fact]
    public void 非四字节对齐的Alpha8位图行序正确()
    {
        using var scope = _fixture.CreateScope();
        // 3×2 遮罩：每行 3 字节，行首不是 4 字节对齐；上传时必须把 GL_UNPACK_ALIGNMENT 设为 1。
        var mask = new PixelBuffer(3, 2, PixelFormat.Alpha8);
        byte[] rows = [255, 0, 0, 0, 255, 0];
        Array.Copy(rows, mask.Pixels, rows.Length);

        var handle = scope.Textures.GetOrCreate("mask-alignment", () => mask);
        var frame = Frame(
            new Size(3, 2),
            new TextureCommand(handle, new Rect(0, 0, 3, 2), new Rect(0, 0, 3, 2), Color.Red));

        scope.Render(frame);

        AssertColor.Equal(Color.Red, scope.GetPixel(0, 0));
        AssertColor.Equal(Background, scope.GetPixel(1, 0));
        AssertColor.Equal(Background, scope.GetPixel(0, 1));
        AssertColor.Equal(Color.Red, scope.GetPixel(1, 1));
        AssertColor.Equal(Background, scope.GetPixel(2, 1));
    }

    [Fact]
    public void 带行距的Alpha8位图按行距上传()
    {
        using var scope = _fixture.CreateScope();
        // 逻辑宽度 2、行距 6：第二行数据必须从第 6 个字节开始。
        var pixels = new byte[6 * 2];
        pixels[0] = 255;
        pixels[1] = 0;
        pixels[6] = 0;
        pixels[7] = 255;
        var mask = new PixelBuffer(2, 2, PixelFormat.Alpha8, pixels, stride: 6);

        var handle = scope.Textures.GetOrCreate("mask-stride", () => mask);
        var frame = Frame(
            new Size(2, 2),
            new TextureCommand(handle, new Rect(0, 0, 2, 2), new Rect(0, 0, 2, 2), Color.Red));

        scope.Render(frame);

        AssertColor.Equal(Color.Red, scope.GetPixel(0, 0));
        AssertColor.Equal(Background, scope.GetPixel(1, 0));
        AssertColor.Equal(Background, scope.GetPixel(0, 1));
        AssertColor.Equal(Color.Red, scope.GetPixel(1, 1));
    }

    [Fact]
    public void 无效纹理句柄被跳过()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(8, 8),
            new TextureCommand(TextureHandle.Invalid, new Rect(0, 0, 8, 8), Rect.Empty, Color.White));

        var stats = scope.Render(frame);

        Assert.Equal(0, stats.TextureCommands);
        Assert.Equal(1, stats.SkippedCommands);
        Assert.Equal(0, stats.Batches);
        AssertColor.Equal(Background, scope.GetPixel(4, 4));
    }

    [Fact]
    public void 重复请求同一键复用纹理且重复提交不增长资源()
    {
        using var scope = _fixture.CreateScope();
        var factoryCalls = 0;
        var handle1 = scope.Textures.GetOrCreate("key", () =>
        {
            factoryCalls++;
            return OpaqueRgba(1, 1, Color.Red);
        });
        var handle2 = scope.Textures.GetOrCreate("key", () =>
        {
            factoryCalls++;
            return OpaqueRgba(1, 1, Color.Red);
        });

        Assert.Equal(handle1, handle2);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, scope.Textures.CreatedTextureCount);

        var frame = Frame(
            new Size(4, 4),
            new TextureCommand(handle1, new Rect(0, 0, 4, 4), new Rect(0, 0, 1, 1), Color.White));

        for (var i = 0; i < 10; i++)
        {
            scope.Render(frame);
        }

        Assert.Equal(1, scope.Textures.CreatedTextureCount);
        Assert.Equal(0, scope.Textures.ReleasedTextureCount);
        Assert.Equal(1, scope.Textures.LiveTextureCount);
        Assert.Equal(2, scope.Renderer.LiveBufferCount);
        Assert.Equal(2, scope.Renderer.LiveVertexArrayCount);
        Assert.Equal(2, scope.Renderer.LiveProgramCount);
    }

    [Fact]
    public void 超出容量时按最近最少使用淘汰()
    {
        _fixture.Surface.MakeCurrent();
        using var service = new GlTextureService(2);

        // 帧内引用受保护：即使超出容量也不会淘汰当前帧尚未提交的纹理。
        var first = service.GetOrCreate("a", () => OpaqueRgba(1, 1, Color.Red));
        var second = service.GetOrCreate("b", () => OpaqueRgba(1, 1, Color.Green));
        var third = service.GetOrCreate("c", () => OpaqueRgba(1, 1, Color.Blue));

        Assert.Equal(3, service.CreatedTextureCount);
        Assert.Equal(0, service.ReleasedTextureCount);
        Assert.Equal(3, service.LiveTextureCount);
        Assert.True(service.TryGetTextureName(first, out _));
        Assert.True(service.TryGetTextureName(second, out _));
        Assert.True(service.TryGetTextureName(third, out _));

        // 停止引用后的帧边界回收：上一帧引用的条目在下一帧边界按 LRU 淘汰回容量。
        service.BeginFrame();
        Assert.Equal(3, service.LiveTextureCount);

        service.BeginFrame();
        Assert.Equal(1, service.ReleasedTextureCount);
        Assert.Equal(2, service.LiveTextureCount);
        Assert.False(service.TryGetTextureName(first, out _));
        Assert.True(service.TryGetTextureName(second, out _));
        Assert.True(service.TryGetTextureName(third, out _));

        // 淘汰后重新请求同一键会重新上传并得到新句柄；
        // 新建条目受帧保护，但同一帧内不受保护的 b 会被立即回收，容量保持长期有界。
        var again = service.GetOrCreate("a", () => OpaqueRgba(1, 1, Color.Red));
        Assert.NotEqual(first, again);
        Assert.Equal(4, service.CreatedTextureCount);
        Assert.Equal(2, service.ReleasedTextureCount);
        Assert.Equal(2, service.LiveTextureCount);
        Assert.True(service.TryGetTextureName(again, out _));
        Assert.True(service.TryGetTextureName(third, out _));
    }

    [Fact]
    public void 缩小容量立即淘汰多余条目()
    {
        _fixture.Surface.MakeCurrent();
        using var service = new GlTextureService(4);
        service.GetOrCreate("a", () => OpaqueRgba(1, 1, Color.Red));
        service.GetOrCreate("b", () => OpaqueRgba(1, 1, Color.Red));
        service.GetOrCreate("c", () => OpaqueRgba(1, 1, Color.Red));

        service.Capacity = 1;

        Assert.Equal(1, service.LiveTextureCount);
        Assert.Equal(2, service.ReleasedTextureCount);
    }

    [Fact]
    public void Clear与Dispose释放全部纹理()
    {
        _fixture.Surface.MakeCurrent();
        var texturesBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Texture);
        var service = new GlTextureService(8);
        service.GetOrCreate("a", () => OpaqueRgba(2, 2, Color.Red));
        service.GetOrCreate("b", () => OpaqueRgba(2, 2, Color.Green));
        Assert.Equal(2, service.LiveTextureCount);
        Assert.Equal(texturesBefore + 2, GlResourceCounters.GetLiveCount(GlResourceKind.Texture));

        service.Clear();
        Assert.Equal(0, service.LiveTextureCount);
        Assert.Equal(2, service.ReleasedTextureCount);
        Assert.True(service.LiveTextureBytes == 0);
        Assert.Equal(texturesBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Texture));

        service.Dispose();
        Assert.Equal(texturesBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Texture));
        Assert.Throws<ObjectDisposedException>(() => service.GetOrCreate("c", () => OpaqueRgba(1, 1, Color.Red)));
    }

    [Fact]
    public void 释放渲染器后GL对象计数回落到基线()
    {
        var before = Snapshot();
        var scope = _fixture.CreateScope();
        _fixture.Surface.MakeCurrent();
        var handle = scope.Textures.GetOrCreate("dispose", () => OpaqueRgba(2, 2, Color.Red));
        scope.Render(Frame(new Size(4, 4), new TextureCommand(handle, new Rect(0, 0, 4, 4), new Rect(0, 0, 2, 2), Color.White)));

        Assert.True(GlResourceCounters.GetLiveCount(GlResourceKind.Program) > before[GlResourceKind.Program]);
        Assert.True(GlResourceCounters.GetLiveCount(GlResourceKind.Buffer) > before[GlResourceKind.Buffer]);

        scope.Dispose();

        var after = Snapshot();
        foreach (var kind in Enum.GetValues<GlResourceKind>())
        {
            Assert.True(
                after[kind] == before[kind],
                $"{kind} 存活数量应从 {before[kind]} 回落到 {before[kind]}，实际 {after[kind]}（{GlResourceCounters.Describe()}）");
        }
    }

    private RenderFrame Frame(Size viewport, params DrawCommand[] commands)
        => new(commands, viewport, 1.0, Background);

    private static Dictionary<GlResourceKind, long> Snapshot()
        => Enum.GetValues<GlResourceKind>().ToDictionary(kind => kind, GlResourceCounters.GetLiveCount);

    private static PixelBuffer OpaqueRgba(int width, int height, params Color[] colors)
    {
        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        for (var i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            Assert.Equal(255, color.A);
            var offset = i * 4;
            buffer.Pixels[offset] = color.R;
            buffer.Pixels[offset + 1] = color.G;
            buffer.Pixels[offset + 2] = color.B;
            buffer.Pixels[offset + 3] = color.A;
        }

        return buffer;
    }
}
