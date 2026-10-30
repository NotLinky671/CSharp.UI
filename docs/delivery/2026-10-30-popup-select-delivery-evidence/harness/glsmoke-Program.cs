using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;

// P06 offscreen-GL acceptance for public Popup/Select. PREPARED ONLY (not built/run before gate).
var evidence = Environment.GetEnvironmentVariable("P06_EV")
    ?? throw new InvalidOperationException("P06_EV evidence directory is required");
if (evidence.StartsWith("/c/", StringComparison.Ordinal)) evidence = "C:/" + evidence[3..];
Directory.CreateDirectory(evidence);
var prefix = Environment.GetEnvironmentVariable("P06_PREFIX") ?? "";
Console.WriteLine($"EVIDENCE {evidence}");

GlOffscreenSurface.AllowNonMainThreadCreation();
var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " :: " + detail)}");
    if (!ok) failures++;
}

void SavePng(string path, PixelBuffer pixels)
{
    using var bmp = new SKBitmap(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    Marshal.Copy(pixels.Pixels, 0, bmp.GetPixels(), pixels.Pixels.Length);
    using var image = SKImage.FromBitmap(bmp);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    Console.WriteLine($"PNG {path} {pixels.Width}x{pixels.Height}");
}

Ctx NewRun(string tag, View root, double width, double height, Theme theme, double scale)
{
    var text = new SkiaTextService();
    var surface = new GlOffscreenSurface(
        (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), "P06 " + tag);
    var textures = new GlTextureService(128);
    var renderer = new GlRenderer(textures);
    var runtime = new UiRuntime(root, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(),
        TextService = text,
        RichTextService = text as IRichTextService,
        TextureService = textures,
        ImageService = new SkiaImageService(),
        Clipboard = new StubClipboardService(),
        Clock = new TestUiClock(),
        WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
    });
    runtime.SetEnvironment(new EnvironmentValues { Theme = theme });
    runtime.SetViewport(new Size(width, height), scale);
    runtime.Start();
    runtime.ProcessPendingWork();
    return new Ctx(tag, runtime, surface, renderer, textures, scale, width, height);
}

Point Center(ViewNode node) => new(node.Frame.X + node.Frame.Width / 2, node.Frame.Y + node.Frame.Height / 2);

void Click(Ctx c, Point point)
{
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
    c.Runtime.ProcessPendingWork();
}

void Down(Ctx c, UiKey key)
{
    c.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, key));
    c.Runtime.ProcessPendingWork();
}

void Snap(Ctx c, string name)
{
    c.Surface.Render(c.Renderer, c.Runtime.BuildFrame());
    SavePng(Path.Combine(evidence, $"{prefix}{c.Tag}-{name}.png"), c.Surface.ReadPixels());
}

ViewNode? Label(Ctx c, string label)
    => c.Runtime.Root.FindDescendants<Button>().FirstOrDefault(n => n.View is Button b && b.Label == label);

void ScenarioSelectMatrix()
{
    foreach (var width in new double[] { 320, 1024 })
    foreach (var theme in new[] { Theme.Light, Theme.Dark })
    foreach (var scale in new double[] { 1, 1.5, 2 })
    {
        var fixture = new SelectFixture
        {
            Items = [new("A"), new("B", Disabled: true), new("C")],
        };
        using var c = NewRun($"select-{width}-{(theme.IsDark ? "dark" : "light")}-{scale}", fixture, width, 600, theme, scale);
        var select = c.Runtime.Root.FindDescendant<Select<string?>>();
        Check($"{c.Tag}.select-found", select is not null);
        if (select is null) continue;

        Click(c, Center(select));
        var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
        Check($"{c.Tag}.open", popup is not null);
        if (popup is null) continue;

        var bounds = new Rect(4, 4, width - 8, 600 - 8);
        Check($"{c.Tag}.popup-inside-viewport",
            bounds.Contains(popup.Frame), $"popup={popup.Frame} bounds={bounds}");
        Snap(c, "open");

        Down(c, UiKey.Down);
        Down(c, UiKey.Enter);
        Check($"{c.Tag}.commit-payload",
            fixture.Writes.Count == 1 && fixture.Writes[0] == "C",
            $"writes=[{string.Join(",", fixture.Writes)}]");
        Snap(c, "committed");
    }
}

void ScenarioBottomFlip()
{
    foreach (var theme in new[] { Theme.Light, Theme.Dark })
    {
        var fixture = new SelectFixture { Items = [new("A"), new("B")], TopSpace = 190 };
        using var c = NewRun($"bottom-{(theme.IsDark ? "dark" : "light")}", fixture, 320, 260, theme, 1.5);
        var select = c.Runtime.Root.FindDescendant<Select<string?>>();
        if (select is null) { Check($"{c.Tag}.select", false); continue; }
        var anchor = select.Frame;
        Click(c, Center(select));
        var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
        Check($"{c.Tag}.open", popup is not null);
        if (popup is null) continue;

        Check($"{c.Tag}.flips-above", popup.Frame.Top < anchor.Top,
            $"popup={popup.Frame} anchor={anchor}");
        Check($"{c.Tag}.inside-viewport", popup.Frame.Bottom <= 260 + 0.01, popup.Frame.ToString());
        Snap(c, "above");
    }
}

void ScenarioScrollClip()
{
    foreach (var width in new double[] { 320, 1024 })
    {
        var fixture = new ScrollAnchorFixture();
        using var c = NewRun($"scrollclip-{width}", fixture, width, 320, Theme.Light, 1.5);
        var select = c.Runtime.Root.FindDescendant<Select<string?>>();
        var scroll = c.Runtime.Root.FindDescendant<ScrollView>();
        Check($"{c.Tag}.nodes", select is not null && scroll is not null);
        if (select is null || scroll is null) continue;

        Click(c, Center(select));
        var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
        Check($"{c.Tag}.open", popup is not null);
        if (popup is null) continue;

        Check($"{c.Tag}.escapes-scroll-clip", popup.Frame.Bottom > scroll.Frame.Bottom,
            $"popup={popup.Frame} scroll={scroll.Frame}");
        var point = new Point(popup.Frame.X + popup.Frame.Width / 2,
            Math.Min(popup.Frame.Bottom - 4, scroll.Frame.Bottom + 30));
        var hit = c.Runtime.HitTest(point);
        Check($"{c.Tag}.popup-hit-outside-scroll",
            hit is not null && IsUnder(popup, hit), $"hit={hit?.View.DebugName ?? "null"}");
        Snap(c, "outside-scroll");
        Down(c, UiKey.Enter);
    }
}

void ScenarioLongList()
{
    var items = Enumerable.Range(0, 30).Select(i => new SelectItem<string?>($"item-{i}")).ToArray();
    foreach (var width in new double[] { 320, 1024 })
    {
        var fixture = new SelectFixture { Items = [.. items], MaxPopupHeight = 120 };
        using var c = NewRun($"longlist-{width}", fixture, width, 400, Theme.Light, 1.5);
        var select = c.Runtime.Root.FindDescendant<Select<string?>>();
        if (select is null) { Check($"{c.Tag}.select", false); continue; }
        Click(c, Center(select));
        var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
        Check($"{c.Tag}.open", popup is not null);
        if (popup is null) continue;

        Down(c, UiKey.End);
        var listScroll = popup.FindDescendant<ScrollView>();
        Check($"{c.Tag}.popup-scroll", listScroll is not null);
        if (listScroll is not null)
        {
            Console.WriteLine($"INFO {c.Tag}.offset :: offset={listScroll.ScrollOffsetY:0.##}");
            Check($"{c.Tag}.scrolled-open", listScroll.ScrollOffsetY > 0.5,
                $"offset={listScroll.ScrollOffsetY}");
        }

        Snap(c, "end-scrolled");
        Down(c, UiKey.Enter);
        Check($"{c.Tag}.end-payload",
            fixture.Writes.Count == 1 && fixture.Writes[0] == "item-29",
            $"writes=[{string.Join(",", fixture.Writes)}]");
        Snap(c, "committed");
    }
}

void ScenarioPopupTextField()
{
    var fixture = new PopupFixture();
    using var c = NewRun("popup-textfield", fixture, 500, 340, Theme.Light, 1.5);
    var anchor = Label(c, "popup-anchor");
    Check($"{c.Tag}.anchor", anchor is not null);
    if (anchor is null) return;

    Click(c, Center(anchor));
    Check($"{c.Tag}.open", c.Runtime.Overlay.HasActiveEntry);
    var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
    if (popup is null) return;

    var input = popup.FindDescendant<TextField>();
    var close = popup.FindDescendants<Button>().FirstOrDefault(n => n.View is Button b && b.Label == "popup-close");
    Check($"{c.Tag}.content", input is not null && close is not null);
    if (input is null || close is null) return;

    Click(c, Center(input));
    Check($"{c.Tag}.field-focused", ReferenceEquals(c.Runtime.FocusedNode, input));
    c.Runtime.DispatchTextInput(new TextInputEvent("abc"));
    c.Runtime.ProcessPendingWork();
    Check($"{c.Tag}.text-state", fixture.Text.Value == "abc", $"text={fixture.Text.Value}");
    Snap(c, "typing");

    Click(c, Center(close));
    Check($"{c.Tag}.closed", !c.Runtime.Overlay.HasActiveEntry);
    Snap(c, "closed");
}

void ScenarioLocalTheme()
{
    var fixture = new LocalThemeFixture();
    using var c = NewRun("local-theme", fixture, 420, 320, Theme.Light, 1.5);
    var anchor = Label(c, "theme-anchor");
    if (anchor is null) { Check($"{c.Tag}.anchor", false); return; }
    Click(c, Center(anchor));
    var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
    Check($"{c.Tag}.open", popup is not null);
    if (popup is null) return;
    Check($"{c.Tag}.owner-env-dark", popup.Environment.Theme == Theme.Dark,
        $"popupTheme={popup.Environment.Theme?.IsDark}");
    Snap(c, "dark");
}


void ScenarioSinglePanelOpaque()
{
    var fixture = new SinglePanelFixture();
    using var c = NewRun("single-panel", fixture, 320, 240, Theme.Light, 1.0);
    var select = c.Runtime.Root.FindDescendant<Select<string?>>();
    Check("panel.select", select is not null);
    if (select is null) return;
    Click(c, Center(select));
    var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
    Check("panel.open", popup is not null);
    if (popup is null) return;

    c.Surface.Render(c.Renderer, c.Runtime.BuildFrame());
    var pixels = c.Surface.ReadPixels();
    SavePng(Path.Combine(evidence, $"{prefix}{c.Tag}-open-panel.png"), pixels);
    var x = popup.Frame.Right - 6;
    var y = popup.Frame.Bottom - 8;
    var px = pixels.GetColor((int)Math.Round(x * c.Scale), (int)Math.Round(y * c.Scale));
    var surface = Theme.Light.Surface;
    Console.WriteLine($"INFO panel.pixel :: {px} surface={surface} at=({x},{y})");
    Check("panel.opaque-surface", Near(px, surface), $"pixel={px} surface={surface}");
}

void ScenarioSingleOpacityZero()
{
    var fixture = new SinglePanelFixture { OpacityZero = true };
    using var c = NewRun("single-opacity0", fixture, 320, 240, Theme.Light, 1.0);
    var select = c.Runtime.Root.FindDescendant<Select<string?>>();
    Check("opacity0.select", select is not null);
    if (select is null) return;
    Click(c, Center(select));
    var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
    Check("opacity0.open", popup is not null);
    if (popup is null) return;

    c.Surface.Render(c.Renderer, c.Runtime.BuildFrame());
    var pixels = c.Surface.ReadPixels();
    SavePng(Path.Combine(evidence, $"{prefix}{c.Tag}-opacity0.png"), pixels);
    var x = popup.Frame.Right - 6;
    var y = popup.Frame.Bottom - 8;
    var px = pixels.GetColor((int)Math.Round(x * c.Scale), (int)Math.Round(y * c.Scale));
    var page = Theme.Light.Background;
    Console.WriteLine($"INFO opacity0.pixel :: {px} page={page} at=({x},{y})");
    Check("opacity0.no-panel-ink", Near(px, page), $"pixel={px} page={page}");
}

void ScenarioSingleOpenBackground()
{
    var fixture = new SelectFixture { Items = [new("A"), new("B")] };
    using var c = NewRun("single-open", fixture, 320, 240, Theme.Light, 1.0);
    var select = c.Runtime.Root.FindDescendant<Select<string?>>();
    Check("single.select", select is not null);
    if (select is null) return;
    Click(c, Center(select));
    var popup = c.Runtime.Overlay.ActiveEntry?.PopupNode;
    Check("single.open", popup is not null);
    if (popup is null) return;
    Console.WriteLine($"INFO single.popupFrame :: {popup.Frame}");
    Snap(c, "open-background");
}

if (Environment.GetEnvironmentVariable("P06_SINGLE") == "1")
{
    ScenarioSingleOpenBackground();
    ScenarioSinglePanelOpaque();
    ScenarioSingleOpacityZero();
    Console.WriteLine(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}

ScenarioSelectMatrix();
ScenarioBottomFlip();
ScenarioScrollClip();
ScenarioLongList();
ScenarioPopupTextField();
ScenarioLocalTheme();

Console.WriteLine(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
Environment.ExitCode = failures == 0 ? 0 : 1;

static bool IsUnder(ViewNode ancestor, ViewNode? node)
{
    for (var current = node; current is not null; current = current.Parent)
    {
        if (ReferenceEquals(current, ancestor)) return true;
    }

    return false;
}

static bool Near(Color a, Color b, int tolerance = 6)
    => Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance &&
       Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;

internal sealed class SelectFixture : Component
{
    internal State<string?> Value { get; private set; } = null!;
    internal List<SelectItem<string?>> Items { get; init; } = [];
    internal List<string?> Writes { get; } = [];
    internal double MaxPopupHeight { get; init; } = 240;
    internal double TopSpace { get; init; }

    protected override View Build()
    {
        Value = UseState<string?>("value", null);
        _ = Value.Value;
        var binding = new Binding<string?>(() => Value.Value, value =>
        {
            Writes.Add(value);
            Value.Value = value;
        });
        var select = new Select<string?>(binding, Items, "choose") { MaxPopupHeight = MaxPopupHeight };
        return new VStack(new Spacer(TopSpace), select, new Spacer(60)).Spacing(8).Padding(12);
    }
}

internal sealed class PopupFixture : Component
{
    internal State<bool> Open { get; private set; } = null!;
    internal State<string> Text { get; private set; } = null!;

    protected override View Build()
    {
        Open = UseState("open", false);
        Text = UseState("text", string.Empty);
        _ = Open.Value;
        _ = Text.Value;
        var anchor = new Button("popup-anchor", () => Open.Value = !Open.Value);
        var content = new VStack(
            new TextField(Text.ToBinding(), "popup-input").FillWidth(),
            new Button("popup-close", () => Open.Value = false))
            .Spacing(8).Padding(10);
        return new VStack(new Popup(anchor, content, Open.ToBinding()), new Spacer(120)).Spacing(8).Padding(12);
    }
}

internal sealed class ScrollAnchorFixture : Component
{
    internal State<string?> Value { get; private set; } = null!;
    internal List<string?> Writes { get; } = [];

    protected override View Build()
    {
        Value = UseState<string?>("value", null);
        _ = Value.Value;
        var binding = new Binding<string?>(() => Value.Value, value =>
        {
            Writes.Add(value);
            Value.Value = value;
        });
        var select = new Select<string?>(binding, [new("A"), new("B"), new("C")], "scroll-select");
        // 锚贴在滚动视口底部附近：弹层高度超过视口内剩余空间，
        // 只有逃出父级 clip 才能完整展开（断言 popup.Frame.Bottom > scroll.Frame.Bottom）。
        return new ScrollView(new VStack(new Spacer(90), select, new Spacer(500)).Spacing(8), ScrollAxis.Vertical)
            .Frame(height: 140);
    }
}

internal sealed class LocalThemeFixture : Component
{
    internal State<bool> Open { get; private set; } = null!;

    protected override View Build()
    {
        Open = UseState("open", false);
        _ = Open.Value;
        var anchor = new Button("theme-anchor", () => Open.Value = !Open.Value);
        var popup = new Popup(anchor, new Button("theme-content", null), Open.ToBinding()).Theme(Theme.Dark);
        return new VStack(popup, new Spacer(200)).Spacing(8).Padding(12);
    }
}

internal sealed class Ctx : IDisposable
{
    private bool _disposed;

    internal Ctx(string tag, UiRuntime runtime, GlOffscreenSurface surface, GlRenderer renderer,
        GlTextureService textures, double scale, double width, double height)
    {
        Tag = tag;
        Runtime = runtime;
        Surface = surface;
        Renderer = renderer;
        Textures = textures;
        Scale = scale;
        Width = width;
        Height = height;
    }

    internal string Tag { get; }
    internal UiRuntime Runtime { get; }
    internal GlOffscreenSurface Surface { get; }
    internal GlRenderer Renderer { get; }
    internal GlTextureService Textures { get; }
    internal double Scale { get; }
    internal double Width { get; }
    internal double Height { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Renderer.Dispose();
        Textures.Dispose();
        Surface.Dispose();
        Runtime.Dispose();
    }
}

internal sealed class SinglePanelFixture : Component
{
    internal State<string?> Value { get; private set; } = null!;
    internal bool OpacityZero { get; init; }

    protected override View Build()
    {
        Value = UseState<string?>("value", null);
        _ = Value.Value;
        var binding = new Binding<string?>(() => Value.Value, value => Value.Value = value);
        View select = new Select<string?>(binding, [new("A"), new("B")], "choose").FillWidth();
        if (OpacityZero) select = select.Opacity(0);
        var underlay = new Text("UNDERLAY-UNDERLAY-UNDERLAY")
            .Wrap()
            .Background(Color.Parse("#FF00AA"))
            .Foreground(Color.White)
            .Frame(height: 120);
        return new VStack(select, underlay).Spacing(0).Padding(12);
    }
}
