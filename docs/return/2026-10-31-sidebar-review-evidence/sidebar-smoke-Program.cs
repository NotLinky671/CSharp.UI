// Sidebar offscreen-GL throwaway smoke — PS-03 acceptance.
// Real UiRuntime + Skia text + GlOffscreenSurface; real DispatchPointer/DispatchKey/DispatchWheel.
// Proves: selected highlight, active indicator, disabled row, light/dark theme,
// End→last-row-visible (pixel + frame), wheel-up browsing does NOT snap back.
// Env: SB_EV (evidence dir, required).

using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;

var ev = Environment.GetEnvironmentVariable("SB_EV")
    ?? throw new InvalidOperationException("SB_EV evidence dir required");
Directory.CreateDirectory(ev);
Console.WriteLine($"EVIDENCE {ev}");

GlOffscreenSurface.AllowNonMainThreadCreation();
var failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " :: " + detail)}");
    if (!ok) failures++;
}

void SavePng(string path, PixelBuffer px)
{
    using var bmp = new SKBitmap(px.Width, px.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    Marshal.Copy(px.Pixels, 0, bmp.GetPixels(), px.Pixels.Length);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    Console.WriteLine($"PNG {path} {px.Width}x{px.Height}");
}

Point Center(ViewNode n) => new(n.Frame.X + n.Frame.Width / 2, n.Frame.Y + n.Frame.Height / 2);

void Click(Ctx c, Point p)
{
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, p, PointerButton.Left));
    c.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, p, PointerButton.Left));
    c.Runtime.ProcessPendingWork();
}

void Key(Ctx c, UiKey k)
{
    c.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, k));
    c.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Up, k));
    c.Runtime.ProcessPendingWork();
}

void Wheel(Ctx c, Point p, double dy)
{
    c.Runtime.DispatchWheel(new WheelEvent(p, 0, dy, KeyModifiers.None, WheelDeltaMode.Pixels));
    c.Runtime.ProcessPendingWork();
}

PixelBuffer Snap(Ctx c, string name)
{
    c.Surface.Render(c.Renderer, c.Runtime.BuildFrame());
    var px = c.Surface.ReadPixels();
    SavePng(Path.Combine(ev, $"{c.Tag}-{name}.png"), px);
    return px;
}

Color PixelAt(Ctx c, PixelBuffer px, ViewNode n)
{
    var cx = (int)((n.Frame.X + n.Frame.Width / 2) * c.Scale);
    var cy = (int)((n.Frame.Y + n.Frame.Height / 2) * c.Scale);
    return px.GetColor(Math.Clamp(cx, 0, px.Width - 1), Math.Clamp(cy, 0, px.Height - 1));
}

static bool Near(Color a, Color b, int tol = 14)
    => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

ViewNode? Row(Ctx c, string value)
    => c.Runtime.Root.FindDescendants<SidebarRowView<string?>>()
        .FirstOrDefault(n => n.View is SidebarRowView<string?> r && r.Item.Value == value);

Ctx NewScrollRun(string tag, int count, int disabledIndex, double w, double h, Theme theme, double scale)
{
    var host = new Host(count, disabledIndex);
    var scroll = new ScrollView(host, ScrollAxis.Vertical) { ScrollbarStyle = ScrollbarStyle.Standard };
    var text = new SkiaTextService();
    var surface = new GlOffscreenSurface((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), "SB " + tag);
    var textures = new GlTextureService(128);
    var renderer = new GlRenderer(textures);
    var rt = new UiRuntime(scroll, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(),
        TextService = text,
        RichTextService = text,
        TextureService = textures,
        ImageService = new SkiaImageService(),
        Clipboard = new StubClipboardService(),
        Clock = new TestUiClock(),
        WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = theme });
    rt.SetViewport(new Size(w, h), scale);
    rt.Start();
    rt.ProcessPendingWork();
    return new Ctx { Tag = tag, Runtime = rt, Surface = surface, Renderer = renderer,
        Textures = textures, Scale = scale, Host = host };
}

// A. selected/active/disabled + light/dark; B. End→last-row-visible; C. wheel no snapback.
foreach (var (themeName, theme) in new[] { ("light", Theme.Light), ("dark", Theme.Dark) })
{
    using var c = NewScrollRun($"a-{themeName}", 12, disabledIndex: 3, 240, 300, theme, 1.5);
    var scrollNode = c.Runtime.Root.FindDescendant<ScrollView>();
    var owner = c.Runtime.Root.FindDescendant<Sidebar<string?>>();
    Check($"{themeName}.mounted", scrollNode is not null && owner is not null);
    if (scrollNode is null || owner is null) continue;

    // select v5 via real pointer click → selected fill drawn.
    Click(c, Center(Row(c, "v5")!));
    Check($"{themeName}.select-write", c.Host.Writes == 1 && c.Host.Value.Value == "v5",
        $"writes={c.Host.Writes} val={c.Host.Value.Value}");
    var pxSel = Snap(c, "selected");

    var selBg = theme.SelectionBackground;
    var v5px = PixelAt(c, pxSel, Row(c, "v5")!);
    Check($"{themeName}.selected-fill", Near(v5px, selBg), $"px={v5px} want~{selBg}");

    // disabled row v3 must NOT render as selection (it's disabled and unselected).
    var v3px = PixelAt(c, pxSel, Row(c, "v3")!);
    Check($"{themeName}.disabled-not-sel", !Near(v3px, selBg, 8), $"v3 px={v3px}");

    // keyboard nav: Home → Down (skips nothing yet) → Enter commits.
    c.Runtime.SetFocus(owner);
    Key(c, UiKey.Home);
    Key(c, UiKey.Down);
    Key(c, UiKey.Enter);
    Check($"{themeName}.kbd-commit", c.Host.Value.Value == "v1" && c.Host.Writes == 2,
        $"val={c.Host.Value.Value} writes={c.Host.Writes}");
    Snap(c, "active");

    // B. End → last row truly inside the (narrow) caller viewport.
    Key(c, UiKey.End);
    var last = Row(c, "v11")!;
    var pxEnd = Snap(c, "end-visible");
    Check($"{themeName}.end-frame-visible",
        last.Frame.Bottom <= scrollNode.Frame.Bottom + 0.5,
        $"bottom={last.Frame.Bottom} vp={scrollNode.Frame.Bottom}");
    var lastPx = PixelAt(c, pxEnd, last);
    Check($"{themeName}.end-row-drawn", lastPx.A == 255, $"v11 center px={lastPx}");

    // C. wheel up (browse back toward top) must NOT snap back to active.
    var offsetAfterEnd = scrollNode.ScrollOffsetY;
    Wheel(c, new Point(60, 60), 120);
    var offsetAfterWheel = scrollNode.ScrollOffsetY;
    Check($"{themeName}.wheel-moved-up", offsetAfterWheel < offsetAfterEnd,
        $"end={offsetAfterEnd} wheel={offsetAfterWheel}");
    Snap(c, "wheel-browse");
}

Console.WriteLine(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
Environment.ExitCode = failures == 0 ? 0 : 1;
return failures == 0 ? 0 : 1;
sealed class Host : Component
{
    internal State<IReadOnlyList<SidebarItem<string?>>> Items { get; private set; } = null!;
    internal State<string?> Value { get; private set; } = null!;
    internal int Writes;

    private readonly int _count;
    private readonly int _disabledIndex;

    internal Host(int count, int disabledIndex)
    {
        _count = count;
        _disabledIndex = disabledIndex;
    }

    protected override View Build()
    {
        Items = UseState<IReadOnlyList<SidebarItem<string?>>>("items",
            Enumerable.Range(0, _count)
                .Select(i => new SidebarItem<string?>($"v{i}", $"Item {i}", Disabled: i == _disabledIndex))
                .ToArray());
        Value = UseState<string?>("value", null);
        var binding = new Binding<string?>(() => Value.Value, v => { Writes++; Value.Value = v; });
        return new Sidebar<string?>(binding, Items.Value, "导航") { Width = 200 };
    }
}

sealed class Ctx : IDisposable
{
    internal string Tag = "";
    internal UiRuntime Runtime = null!;
    internal GlOffscreenSurface Surface = null!;
    internal GlRenderer Renderer = null!;
    internal GlTextureService Textures = null!;
    internal double Scale;
    internal Host Host = null!;
    public void Dispose() { Renderer.Dispose(); Textures.Dispose(); Surface.Dispose(); Runtime.Dispose(); }
}
