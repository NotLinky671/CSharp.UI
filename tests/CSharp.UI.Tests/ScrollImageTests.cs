using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 11：单轴滚动容器与图片。</summary>
public class ScrollImageTests
{
    private static VStack LongContent(int count) => new([.. Enumerable.Range(0, count).Select(i => (View)new Text($"line {i}"))]);

    [Fact]
    public void ShortContent_DoesNotScroll()
    {
        using var app = new TestApp(new VStack(new ScrollView(new Text("short"), ScrollAxis.Vertical)), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, -120, WheelDeltaMode.Pixels);

        Assert.Equal(0, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void LongContent_ScrollsToEndAndClamps()
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60), ScrollAxis.Vertical)), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        var content = scroll.Children[0];
        var maxOffset = content.DesiredSize.Height - scroll.Frame.Height;
        Assert.True(maxOffset > 0);

        app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var first = scroll.ScrollOffsetY;
        Assert.True(first > 0);

        // 一直滚动到末端后不再越界。
        for (var i = 0; i < 50; i++)
        {
            app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.Equal(maxOffset, scroll.ScrollOffsetY, 3);

        // 向上滚回起点。
        for (var i = 0; i < 80; i++)
        {
            app.Wheel(scroll.Frame.Center, 100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.Equal(0, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void OffsetStaysValid_WhenContentShrinksOrViewportGrows()
    {
        var component = new ScrollHost { ItemCount = 60 };
        using var app = new TestApp(component, new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        for (var i = 0; i < 10; i++)
        {
            app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        var before = scroll.ScrollOffsetY;
        Assert.True(before > 0);

        // 内容缩短：偏移必须重新限制到合法范围。
        component.ItemCount = 1;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(0, scroll.ScrollOffsetY, 3);

        // 视口变大：同样重新限制。
        component.ItemCount = 60;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        for (var i = 0; i < 10; i++)
        {
            app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.True(scroll.ScrollOffsetY > 0);

        app.Runtime.SetViewport(new Size(200, 5000), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(0, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void Rebuild_PreservesScrollPositionAndNodeIdentity()
    {
        var component = new ScrollHost { ItemCount = 60 };
        using var app = new TestApp(component, new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        for (var i = 0; i < 5; i++)
        {
            app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        var offset = scroll.ScrollOffsetY;
        Assert.True(offset > 0);

        component.Title = "changed";
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(scroll, app.Runtime.Root.Require<ScrollView>());
        Assert.Equal(offset, scroll.ScrollOffsetY, 3);
    }

    [Fact]
    public void HitTest_FollowsScrolledContent()
    {
        var log = new List<string>();
        var content = new VStack([.. Enumerable.Range(0, 40).Select(i => (View)new Text($"line {i}"))]);
        var target = new Button("target", () => log.Add("target"));
        var view = new VStack(new ScrollView(new VStack(content, target), ScrollAxis.Vertical));

        using var app = new TestApp(view, new Size(300, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        var button = app.Runtime.Root.Require<Button>();

        // 滚动前按钮在视口外，点击不到。
        Assert.True(button.Frame.Y > scroll.Frame.Bottom);
        app.Click(new Point(150, 100));
        Assert.Empty(log);

        // 滚动到按钮可见后可以点击，命中位置与视觉位置一致。
        for (var i = 0; i < 30 && button.Frame.Bottom > scroll.Frame.Bottom; i++)
        {
            app.Wheel(scroll.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
            app.Frame();
        }

        Assert.True(button.Frame.Y < scroll.Frame.Bottom);
        Assert.InRange(button.Frame.Y, scroll.Frame.Y - button.Frame.Height, scroll.Frame.Bottom);
        app.Click(new Point(button.Frame.Center.X, Math.Max(button.Frame.Center.Y, scroll.Frame.Y + 1)));
        Assert.Equal(["target"], log);
    }

    [Fact]
    public void NestedScrollViews_InnerConsumesUntilItsEdge()
    {
        var inner = new ScrollView(LongContent(40), ScrollAxis.Vertical);
        var outer = new ScrollView(new VStack(new Text("header"), new FrameView(inner, height: 100), LongContent(40)), ScrollAxis.Vertical);

        using var app = new TestApp(new VStack(outer), new Size(200, 200));
        app.Frame();

        var outerNode = app.Runtime.Root.Require<ScrollView>();
        var innerNode = app.Runtime.All<ScrollView>().Last();

        // 在内部容器上滚动：内部先消费。
        // 真实适配器的普通一格是 ±1 步，这里沿用像素式的辅助调用，必须显式声明像素单位。
        app.Wheel(innerNode.Frame.Center, -100, WheelDeltaMode.Pixels);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(innerNode.ScrollOffsetY > 0);

        // 内部滚到末端后，继续滚动由外层消费。
        for (var i = 0; i < 60; i++)
        {
            app.Wheel(innerNode.Frame.Center, -100, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.True(outerNode.ScrollOffsetY > 0);
    }

    [Fact]
    public void Image_UsesNaturalSizeAndReusesTexture()
    {
        using var app = new TestApp(
            new VStack(new Image("test.png")),
            new Size(400, 300),
            configureImages: images => images.Register("test.png", 120, 60));
        var frame = app.Runtime.BuildFrame();

        var image = app.Runtime.Root.Require<Image>();
        Assert.Equal(new Size(120, 60), image.DesiredSize);

        var texture = frame.Commands.OfType<TextureCommand>().Single();
        Assert.Equal(
            new Rect(image.Frame.X + ((image.Frame.Width - 120) / 2), image.Frame.Y, 120, 60),
            texture.Bounds);

        // 重复构建/绘制复用同一纹理，不重复上传。
        var uploads = app.Textures.FactoryInvocations;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(uploads, app.Textures.FactoryInvocations);
        Assert.Equal(1, app.Textures.LiveTextureCount);
    }

    [Fact]
    public void Image_ResizeModesPreserveAspect()
    {
        using var fit = new TestApp(
            new VStack(new Image("wide.png").Resize(ImageResizeMode.Fit).Frame(width: 100, height: 100, alignment: Alignment.Start)),
            new Size(400, 300),
            configureImages: images => images.Register("wide.png", 200, 100));
        var fitFrame = fit.Runtime.BuildFrame();

        var fitCommand = fitFrame.Commands.OfType<TextureCommand>().Single();
        Assert.Equal(100, fitCommand.Bounds.Width, 3);
        Assert.Equal(50, fitCommand.Bounds.Height, 3);

        using var fill = new TestApp(
            new VStack(new Image("wide.png").Resize(ImageResizeMode.Fill).Frame(width: 100, height: 100, alignment: Alignment.Start)),
            new Size(400, 300),
            configureImages: images => images.Register("wide.png", 200, 100));
        var fillCommand = fill.Runtime.BuildFrame().Commands.OfType<TextureCommand>().Single();
        Assert.Equal(200, fillCommand.Bounds.Width, 3);
        Assert.Equal(100, fillCommand.Bounds.Height, 3);
    }

    [Fact]
    public void Image_LoadFailureIsDiagnosed_AndNoCommandsAreEmitted()
    {
        using var app = new TestApp(new VStack(new Image("missing.png")), new Size(400, 300));
        var frame = app.Frame();

        Assert.DoesNotContain(frame, c => c is TextureCommand);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("missing.png"));
        Assert.Equal(0, app.Textures.LiveTextureCount);
    }

    [Fact]
    public void Image_ReplaceAndUnmount_DoNotGrowResourcesUnboundedly()
    {
        var component = new ImageHost();
        using var app = new TestApp(
            component,
            new Size(400, 300),
            configureImages: images =>
            {
                images.Register("a.png", 40, 40);
                images.Register("b.png", 40, 40);
                images.Capacity = 1;
            });
        app.Textures.Capacity = 2;

        for (var i = 0; i < 6; i++)
        {
            component.Source = i % 2 == 0 ? "a.png" : "b.png";
            component.Trigger!.Value++;
            app.Runtime.ProcessPendingWork();
            app.Frame();
        }

        Assert.True(app.Textures.LiveTextureCount <= 2);
        Assert.True(app.Images.CachedImageCount <= 1);

        app.Runtime.Dispose();
        Assert.Equal(0, app.Textures.LiveTextureCount);
        Assert.Equal(0, app.Images.CachedImageCount);
    }
}

/// <summary>可改变项目数量与标题的滚动组件。</summary>
internal sealed class ScrollHost : Component
{
    public int ItemCount { get; set; } = 10;

    public string Title { get; set; } = "list";

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return new VStack(
            new Text(Title),
            new ScrollView(
                new VStack([.. Enumerable.Range(0, ItemCount).Select(i => (View)new Text($"line {i}"))]),
                ScrollAxis.Vertical));
    }
}

/// <summary>图片来源可替换的组件。</summary>
internal sealed class ImageHost : Component
{
    public string Source { get; set; } = "a.png";

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return new VStack(new Image(Source));
    }
}
