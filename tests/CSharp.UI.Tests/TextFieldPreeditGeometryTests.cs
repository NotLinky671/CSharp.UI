using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-06：预编辑显示串排版、下划线/活动段/组合 caret、显示→文档命中、长预编辑横向滚动与
/// 最终布局后的候选锚点几何发布（无窗口 CPU 测试，文本排版使用确定性替身）。
/// </summary>
public class TextFieldPreeditGeometryTests
{
    private const double Epsilon = 1e-6;

    [Fact]
    public void 组合期间显示串与文档排版分离并绘制活动段下划线和组合caret()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var session = Client(node).Session.SessionId;
        AssertApplied(
            service.Apply(CompositionEvent.Start(
                session,
                0,
                0,
                0,
                new TextRange(2, 2),
                "ni",
                TextSelection.Caret(2),
                compositionCaret: 1,
                segments: [new TextRange(0, 1), new TextRange(1, 2)])),
            TextInputOutcome.Started);

        app.Runtime.ProcessPendingWork();
        var commands = app.Frame();
        var state = State(node);
        var displayLayout = Layout(app, "abni");

        // 文档排版仍是已提交文本，显示缓存才是“abni”；两者不互相污染。
        Assert.Equal("abni", state.DisplayLayoutCache!.Text);
        Assert.Equal("ab", state.Editor.Text);
        Assert.Equal("ab", binding.Text);
        Assert.Equal(0, binding.Writes);

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.Equal((int)Math.Ceiling(displayLayout.Size.Width * app.Runtime.PixelScale), texture.Texture.PixelWidth);
        Assert.Equal((int)Math.Ceiling(displayLayout.Size.Height * app.Runtime.PixelScale), texture.Texture.PixelHeight);

        // 预编辑 [2,4)：第一段为非活动（1px、次要色），第二段为活动（2px、强调色），caret 位于段边界取后一段。
        var inactive = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.MutedForeground && c.Bounds.Height == 1);
        var active = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height == 2);
        var compositionCaret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);

        var first = displayLayout.GetRangeBounds(2, 3);
        var second = displayLayout.GetRangeBounds(3, 4);
        AssertRect(new Rect(texture.Bounds.X + first.X, texture.Bounds.Y + first.Bottom - 1, first.Width, 1), inactive.Bounds);
        AssertRect(new Rect(texture.Bounds.X + second.X, texture.Bounds.Y + second.Bottom - 2, second.Width, 2), active.Bounds);

        // 组合 caret 与文字纹理共享同一原点与行高；所有下划线都落在文本行底部。
        var caret = displayLayout.GetCaretBounds(3, 1.5);
        AssertRect(new Rect(texture.Bounds.X + caret.X, texture.Bounds.Y + caret.Y, caret.Width, caret.Height), compositionCaret.Bounds);
        var lineBottom = texture.Bounds.Y + displayLayout.Lines[0].Bounds.Bottom;
        Assert.Equal(lineBottom, compositionCaret.Bounds.Bottom, 6);
        Assert.Equal(lineBottom, active.Bounds.Bottom, 6);
        Assert.Equal(lineBottom, inactive.Bounds.Bottom, 6);
        Assert.Equal(0, state.Caret);
        Assert.DoesNotContain(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.SelectionBackground);
    }

    [Fact]
    public void 组合显示替换原选区且不绘制文档选区也不写绑定()
    {
        var binding = new MutableText("abef");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var state = State(node);
        state.Anchor = 2;
        state.Caret = 4;

        var session = Client(node).Session.SessionId;
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(2, 4), "ni", new TextSelection(4, 2), 2)),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        var commands = app.Frame();

        Assert.Equal("abni", State(node).DisplayLayoutCache!.Text);
        Assert.Equal("abef", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.Equal("ni", Client(node).Preedit);
        Assert.DoesNotContain(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.SelectionBackground);

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var displayLayout = Layout(app, "abni");
        Assert.Equal((int)Math.Ceiling(displayLayout.Size.Width * app.Runtime.PixelScale), texture.Texture.PixelWidth);
    }

    [Fact]
    public void 组合中鼠标按下先取消再按正式文档命中并与替换显示一致()
    {
        var binding = new MutableText("abcdef");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var session = Client(node).Session.SessionId;
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(0, 0), "XYZ", TextSelection.Caret(0), 3)),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(Client(node).IsComposing);

        // 点击位置按“正式文档”的排版计算：若错误地用显示串命中，会落到别的字素边界。
        var clip = Assert.Single(app.Frame().OfType<PushClipCommand>()).Bounds;
        var documentLayout = Layout(app, "abcdef");
        var click = new Point(clip.X + documentLayout.GetCaretX(2) + 0.1, node.Frame.Center.Y);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, click));

        Assert.False(Client(node).IsComposing);
        Assert.Equal("abcdef", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.Equal(2, State(node).Caret);

        // 取消后重新绘制的是正式文档（组合显示缓存可以保留旧键，但当前显示串已回到文档）。
        var commands = app.Frame();
        Assert.Equal("abcdef", Client(node).DisplayString);
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.Equal((int)Math.Ceiling(documentLayout.Size.Width * app.Runtime.PixelScale), texture.Texture.PixelWidth);
    }

    [Fact]
    public void 长预编辑横向滚动保证组合caret可见()
    {
        var binding = new MutableText(string.Empty);
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 100 }).Align(Alignment.Start), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var preedit = new string('n', 40);
        var session = Client(node).Session.SessionId;
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(0, 0), preedit, TextSelection.Caret(0), preedit.Length)),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        var commands = app.Frame();
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        var displayLayout = Layout(app, preedit);
        var state = State(node);

        Assert.True(displayLayout.Size.Width > clip.Width);
        Assert.True(state.ScrollX > displayLayout.Size.Width - clip.Width, "末尾 caret 需要额外的宽度/吸附余量。");

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.True(texture.Bounds.X < clip.X, "长预编辑应已横向滚动。");
        var caretX = displayLayout.GetCaretX(preedit.Length);
        Assert.True(texture.Bounds.X + caretX >= clip.X - Epsilon, "真实排版 caret 左边缘应可见。");
        Assert.True(texture.Bounds.X + caretX + 1.5 <= clip.Right + Epsilon, "真实排版 caret 右边缘应可见。");
        var compositionCaret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
        Assert.InRange(compositionCaret.Bounds.X, clip.X, clip.Right - compositionCaret.Bounds.Width + Epsilon);
        Assert.True(compositionCaret.Bounds.Right <= clip.Right + Epsilon);
        Assert.Equal(0, binding.Writes);
    }

    [Fact]
    public void 最终布局后发布几何且同值不重复发布()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 第一次发布的是已提交 caret；与同一帧绘制的 caret 完全同位置。
        Assert.Single(service.Geometries);
        var commandCaret = Caret(app.Frame());
        AssertRect(commandCaret.Bounds, service.LastGeometry.CaretBounds);
        Assert.False(service.LastGeometry.HasComposition);

        var published = service.Geometries.Count;
        var redraws = app.Dispatcher.RedrawRequestCount;
        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();
        Assert.Equal(published, service.Geometries.Count);
        Assert.Equal(redraws, app.Dispatcher.RedrawRequestCount);

        // 组合开始：几何在同一安全阶段更新，caret 是组合 caret，组合矩形覆盖预编辑范围。
        var session = Client(node).Session.SessionId;
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(2, 2), "ni", TextSelection.Caret(2), 2, [new TextRange(0, 2)])),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(published + 1, service.Geometries.Count);
        var composed = service.LastGeometry;
        Assert.True(composed.HasComposition);

        var commands = app.Frame();
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var displayLayout = Layout(app, "abni");
        var range = displayLayout.GetRangeBounds(2, 4);
        var expectedComposition = new Rect(texture.Bounds.X + range.X, texture.Bounds.Y + range.Y, range.Width, range.Height);
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        AssertRect(expectedComposition.Intersect(clip), composed.CompositionBounds);

        var compositionCaret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
        AssertRect(compositionCaret.Bounds, composed.CaretBounds);
        AssertRect(composed.CaretBounds.Union(composed.CompositionBounds), composed.ExclusionBounds);

        // 再次安全阶段/绘制：同值不重复发布，也不额外请求重绘。
        published = service.Geometries.Count;
        redraws = app.Dispatcher.RedrawRequestCount;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(published, service.Geometries.Count);
        Assert.Equal(redraws, app.Dispatcher.RedrawRequestCount);

        // 更新预编辑与取消组合都会重新发布几何。
        AssertApplied(service.Apply(CompositionEvent.Update(session, 0, 0, 1, new TextRange(2, 2), "nihao", 5)), TextInputOutcome.Updated);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(published + 1, service.Geometries.Count);
        Assert.True(service.LastGeometry.CompositionBounds.Width > composed.CompositionBounds.Width);

        AssertApplied(service.Apply(CompositionEvent.Cancel(session, 0, 0, 2)), TextInputOutcome.Cancelled);
        app.Runtime.ProcessPendingWork();
        Assert.False(service.LastGeometry.HasComposition);
        Assert.False(Client(node).IsComposing);
        Assert.Equal(service.LastGeometry, Client(node).GetState().Geometry);
        Assert.True(Client(node).GetState().Geometry.HasCaret);
    }

    [Fact]
    public void 失焦撤销已发布锚点且不重复唤醒()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(service.LastGeometry.HasCaret);
        var published = service.Geometries.Count;

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        Assert.False(service.LastGeometry.HasCaret);
        Assert.Equal(published + 1, service.Geometries.Count);

        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();
        Assert.Equal(published + 1, service.Geometries.Count);
    }

    [Fact]
    public void 字体变化后重新发布几何且保留内容区约束()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var initial = service.LastGeometry;
        var published = service.Geometries.Count;
        Assert.True(initial.HasCaret);

        app.Runtime.SetEnvironment(new EnvironmentValues
        {
            Theme = Theme.Light,
            Font = FontDescription.Default.WithSize(24),
        });
        app.Runtime.ProcessPendingWork();

        Assert.Equal(published + 1, service.Geometries.Count);
        var updated = service.LastGeometry;
        Assert.True(updated.HasCaret);
        Assert.True(updated.CaretBounds.Height > initial.CaretBounds.Height);
        Assert.True(updated.CaretBounds.Bottom <= app.Runtime.Root.Frame.Bottom + Epsilon);
    }

    [Fact]
    public void 撤销锚点的同步回调不能写入已失焦客户端()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var oldClient = Assert.IsAssignableFrom<ITextInputClient>(service.ActiveClient);

        service.OnPublishGeometry = geometry =>
        {
            if (!geometry.HasCaret)
            {
                // 平台在“撤销锚点”回调里同步投递旧客户端的结果：归属已撤销，不得写入。
                var result = oldClient.ApplyComposition(
                [
                    CompositionEvent.Result(oldClient.Session.SessionId, 0, 0, 0, "BAD", new TextRange(0, 1)),
                ]);
                Assert.True(result.HasRejection);
            }
        };

        app.Runtime.SetFocus(null);
        app.Runtime.ProcessPendingWork();

        Assert.Equal("ABCD", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.False(service.LastGeometry.HasCaret);
        Assert.Equal(TextInputGeometry.Unavailable, oldClient.GetState().Geometry);
        Assert.True(service.EndCallCount > 0);
    }

    [Fact]
    public void 纯重绘发现外部绑定变化时先撤销组合再发布几何()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 240 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var session = Client(node).Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(1, 3), "WW", new TextSelection(3, 1), 2)), TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(service.LastGeometry.HasComposition);
        var oldClient = Client(node);

        // 纯重绘路径：外部直接改写绑定，不重建描述、不失效布局。
        binding.Text = "X";
        app.Runtime.InvalidateRender();
        app.Runtime.ProcessPendingWork();

        Assert.False(service.LastGeometry.HasComposition);
        Assert.Equal("X", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.False(oldClient.IsComposing);
        Assert.True(oldClient.IsReleased);

        var active = Assert.IsAssignableFrom<TextCompositionClient>(service.ActiveClient);
        Assert.NotSame(oldClient, active);
        Assert.Equal(service.LastGeometry, active.GetState().Geometry);
        Assert.Equal("X", active.GetState().Document.Text);

        var commands = app.Frame();
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var layout = Layout(app, "X");
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * app.Runtime.PixelScale), texture.Texture.PixelWidth);
    }

    [Fact]
    public void 取消长预编辑后鼠标按下先收敛正式文档滚动再命中()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var session = Client(node).Session.SessionId;
        var preedit = new string('W', 80);
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(1, 3), preedit, TextSelection.Caret(0), preedit.Length)),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        var commands = app.Frame();
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        Assert.True(State(node).ScrollX > 100, "长预编辑应产生较大的横向滚动。");

        // 只发 PointerDown：必须立即按正式文档 + 恢复后的 caret 0 命中，而不是使用显示串旧滚动。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(clip.X + 1, node.Frame.Center.Y)));

        Assert.False(Client(node).IsComposing);
        Assert.Equal(0, State(node).Caret);
        Assert.True(State(node).ScrollX < 20, $"取消后滚动应收敛到正式文档附近，实际 {State(node).ScrollX}。");
    }

    [Fact]
    public void 窗口失活撤销锚点且同步回调不写旧客户端()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var oldClient = Assert.IsAssignableFrom<ITextInputClient>(service.ActiveClient);

        service.OnPublishGeometry = geometry =>
        {
            if (!geometry.HasCaret)
            {
                oldClient.ApplyComposition([CompositionEvent.Result(oldClient.Session.SessionId, 0, 0, 0, "BAD", new TextRange(0, 1))]);
            }
        };

        app.Runtime.NotifyWindowActivation(false);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("ABCD", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.Null(service.ActiveClient);
        Assert.Equal(TextInputGeometry.Unavailable, oldClient.GetState().Geometry);

        app.Runtime.NotifyWindowActivation(true);
        app.Runtime.ProcessPendingWork();
        Assert.NotNull(service.ActiveClient);
        Assert.NotSame(oldClient, service.ActiveClient);
        Assert.True(service.LastGeometry.HasCaret);
        Assert.Equal(service.LastGeometry, service.ActiveClient!.GetState().Geometry);
    }


    [Fact]
    public void 节点卸载撤销归属并让旧客户端几何失效()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        var host = new HideHost(binding.Binding);
        using var app = new TestApp(host, new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var oldClient = Assert.IsAssignableFrom<ITextInputClient>(service.ActiveClient);

        var session = Client(node).Session.SessionId;
        AssertApplied(service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(0, 2), "WW", new TextSelection(2, 0), 2)), TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();

        host.Show!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.Equal("ABCD", binding.Text);
        Assert.Equal(0, binding.Writes);
        Assert.Null(service.ActiveClient);
        Assert.Equal(TextInputGeometry.Unavailable, oldClient.GetState().Geometry);
    }

    [Fact]
    public void 长预编辑滚动后外部缩短文本在安全发布前收敛滚动与锚点()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var session = Client(node).Session.SessionId;
        var preedit = new string('W', 80);
        AssertApplied(
            service.Apply(CompositionEvent.Start(session, 0, 0, 0, new TextRange(1, 3), preedit, TextSelection.Caret(0), preedit.Length)),
            TextInputOutcome.Started);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(State(node).ScrollX > 100);

        // 纯重绘：长预编辑滚动状态下的外部短文本。发布前必须同步、取消组合并收敛 ScrollX；
        // 不能等 Emit 才修正（否则服务收到的是旧滚动算出的不可用锚点）。
        binding.Text = "X";
        app.Runtime.InvalidateRender();
        app.Runtime.ProcessPendingWork();

        var active = Assert.IsAssignableFrom<TextCompositionClient>(service.ActiveClient);
        Assert.False(active.IsComposing);
        Assert.Equal("X", active.GetState().Document.Text);
        Assert.Equal(0, State(node).ScrollX);
        Assert.True(service.LastGeometry.HasCaret);
        Assert.Equal(service.LastGeometry, active.GetState().Geometry);
        Assert.Equal(0, binding.Writes);

        var commands = app.Frame();
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Height > 2);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
    }

    [Fact]
    public void 普通长文档滚动后外部缩短文本在安全发布前收敛滚动与锚点()
    {
        var binding = new MutableText(new string('W', 80));
        var service = new FakeTextInputService();
        using var app = new TestApp(new VStack(new TextField(binding.Binding) { Width = 200 }), new Size(400, 200), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(State(node).ScrollX > 100);

        binding.Text = "X";
        app.Runtime.InvalidateRender();
        app.Runtime.ProcessPendingWork();

        var active = Assert.IsAssignableFrom<TextCompositionClient>(service.ActiveClient);
        Assert.Equal(0, State(node).ScrollX);
        Assert.True(service.LastGeometry.HasCaret);
        Assert.Equal(service.LastGeometry, active.GetState().Geometry);

        var commands = app.Frame();
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Height > 2);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
    }

    [Fact]
    public void 祖先滚动裁剪参与几何且完全裁出时撤销锚点()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        var scroll = new ScrollView(new VStack(new TextField(binding.Binding) { Width = 200 }, new Spacer(200)));
        using var app = new TestApp(scroll, new Size(300, 80), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        var scrollNode = app.Runtime.Require<ScrollView>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Single(service.Geometries);
        var visible = service.LastGeometry;
        Assert.True(visible.HasCaret);

        // 部分裁出：caret 的顶边在视口上方，发布几何必须夹到祖先滚动视口的裁剪边界。
        scrollNode.ScrollOffsetY = 20;
        scrollNode.InvalidateLayout();
        app.Runtime.ProcessPendingWork();
        var partial = service.LastGeometry;
        Assert.True(partial.HasCaret);
        Assert.Equal(scrollNode.Frame.Top, partial.CaretBounds.Top, 6);
        Assert.True(partial.CaretBounds.Bottom <= scrollNode.Frame.Bottom + Epsilon);
        Assert.Equal(scrollNode.Frame.Top, partial.ExclusionBounds.Top, 6);

        // 完全滚出：撤销锚点（空几何），重复安全阶段不再发布同值。
        scrollNode.ScrollOffsetY = 120;
        scrollNode.InvalidateLayout();
        app.Runtime.ProcessPendingWork();
        var hidden = service.LastGeometry;
        Assert.False(hidden.HasCaret);
        Assert.False(hidden.HasComposition);
        Assert.Equal(hidden, Client(node).GetState().Geometry);
        Assert.False(Client(node).GetState().Geometry.HasCaret);

        var published = service.Geometries.Count;
        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();
        Assert.Equal(published, service.Geometries.Count);
    }

    [Fact]
    public void 嵌套裁剪与内边距共同决定发布几何()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        var scroll = new ScrollView(new VStack(field, new Spacer(200)), ScrollAxis.Vertical);
        var root = new VStack(new ClipView(scroll).Frame(height: 50));
        using var app = new TestApp(root, new Size(300, 120), textInputService: service);
        app.Frame();
        var node = app.Runtime.Require<TextField>();
        var scrollNode = app.Runtime.Require<ScrollView>();
        var clipNode = app.Runtime.Require<ClipView>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 未滚动：caret 完全在 Clip 视口内，几何原点来自最终节点矩形。
        var visible = service.LastGeometry;
        Assert.True(visible.HasCaret);
        Assert.True(visible.CaretBounds.Bottom <= clipNode.Frame.Bottom + Epsilon);

        // 滚动 10：caret 顶部被裁剪，发布几何的顶边必须等于嵌套 Clip 的上边界。
        scrollNode.ScrollOffsetY = 10;
        scrollNode.InvalidateLayout();
        app.Runtime.ProcessPendingWork();
        var clipped = service.LastGeometry;
        Assert.True(clipped.HasCaret);
        Assert.Equal(clipNode.Frame.Top, clipped.CaretBounds.Top, 6);
        Assert.True(clipped.CaretBounds.Bottom <= clipNode.Frame.Bottom + Epsilon);

        // 继续滚出：caret 完全不可见 → 撤销锚点。
        scrollNode.ScrollOffsetY = 60;
        scrollNode.InvalidateLayout();
        app.Runtime.ProcessPendingWork();
        Assert.False(service.LastGeometry.HasCaret);
    }

    private static RectangleCommand Caret(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Width > 0 && c.Bounds.Height > 2);

    private static TextLayoutResult Layout(TestApp app, string text)
        => app.Text.Layout(TextPainter.CreateRequest(text, FontDescription.Default, double.PositiveInfinity, app.Runtime.PixelScale));

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端（会话未建立）。");

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    private static void AssertApplied(TextInputApplyResult result, TextInputOutcome expected)
    {
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(expected), $"期望 {expected}，实际 {result.Outcome}（{result.RejectionReason}）。");
    }

    /// <summary>可卸载输入框的宿主：用于验证真正卸载时撤销归属与几何。</summary>
    private sealed class HideHost : Component
    {
        private readonly Binding<string> _binding;

        public HideHost(Binding<string> binding) => _binding = binding;

        public State<bool>? Show { get; private set; }

        protected override View Build()
        {
            Show = UseState("show", true);
            return new VStack(Show.Value ? new TextField(_binding) { Width = 200 } : new Text("hidden"));
        }
    }

    /// <summary>可写文本绑定：身份稳定，记录真实 setter 调用次数。</summary>
    private sealed class MutableText
    {
        public MutableText(string initial)
        {
            Text = initial;
            Binding = new Binding<string>(
                () => Text,
                value =>
                {
                    Text = value;
                    Writes++;
                });
        }

        public string Text { get; set; }

        public int Writes { get; private set; }

        public Binding<string> Binding { get; }
    }

    /// <summary>记录几何发布并转发组合事件的文本输入服务替身。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public List<TextInputGeometry> Geometries { get; } = [];

        public List<ITextInputClient> SeenClients { get; } = [];

        public int EndCallCount { get; private set; }

        /// <summary>在平台收到 PublishGeometry 时同步执行的回调（模拟原生桥同步重入）。</summary>
        public Action<TextInputGeometry>? OnPublishGeometry { get; set; }

        public TextInputGeometry LastGeometry => Geometries.Count == 0 ? TextInputGeometry.Unavailable : Geometries[^1];

        public bool StartSession(ITextInputClient client)
        {
            ActiveClient = client;
            SeenClients.Add(client);
            return true;
        }

        public bool EndSession()
        {
            EndCallCount++;
            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public bool PublishGeometry(TextInputGeometry geometry)
        {
            Geometries.Add(geometry);
            OnPublishGeometry?.Invoke(geometry);
            return true;
        }

        public TextInputApplyResult Apply(params CompositionEvent[] events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");
    }
}
