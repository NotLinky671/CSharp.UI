// Blank-click focus-clear acceptance harness — ROUND 1 (prepared, NOT RUN before freeze).
//
// User-authorized contract: clicking truly blank space clears keyboard focus
// (focus ring disappears, FocusedNode == null, text caret/composition go through
// the existing blur path). Clicking a real control still transfers focus
// normally; scrollbar drags / wheel / pointer-move do NOT clear focus; and
// non-primary buttons do not clear.
//
// Fixture is the REAL ControlGallery (samples assembly, not a toy view tree):
//   - real Button with focus ring, real TextField with caret + fake input
//     service for runtime composition sessions (NOT an OS IME claim), real
//     nested ScrollViews with scrollbars, real RichText link hit areas.
//   - headless UiRuntime + Skia text services + GlOffscreenSurface for actual
//     rendered-pixel evidence of the focus ring (light + dark themes).
//
// "Blank" definition (verifying, not deciding): a primary-left Down whose
// unconsumed hit chain contains no focusable or interactive ancestor. This is
// whatever rule the good implementation lands; the harness treats a click as
// blank only when no ancestor of the hit node is focusable AND the click is
// not on a scrollbar track/thumb/link.
//
// Env: BCF_EV (evidence dir, required), BCF_RUN (run id).

using System.Reflection;
using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;
using SysPoint = CSharp.UI.Point;

var ev = Environment.GetEnvironmentVariable("BCF_EV")
    ?? throw new InvalidOperationException("BCF_EV not set");
Directory.CreateDirectory(ev);
T.Check("evidence-dir-real-repo", ev.Contains(@"C#\CSharp.UI"), ev);
var run = Environment.GetEnvironmentVariable("BCF_RUN") ?? "run1";

foreach (var asmName in new[] { "CSharp.UI", "CSharp.UI.OpenTK", "CSharp.UI.Text" })
{
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName)
              ?? Assembly.Load(asmName);
    var loc = asm.Location;
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loc)));
    Console.WriteLine($"ASM {asmName} {loc} sha256={sha[..16]}");
}

GlOffscreenSurface.AllowNonMainThreadCreation();
using var surface = new GlOffscreenSurface(1024, 768);
Console.WriteLine($"HOST {surface.ContextDescription}");

// ---------------------------------------------------------------- helpers --

// A "click" = move + down + up at the same logical position with a given button.
static void Click(UiRuntime rt, SysPoint p, PointerButton button = PointerButton.Left)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Down, p, button));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Up, p, button));
    rt.ProcessPendingWork();
}
static void Press(UiRuntime rt, SysPoint p, PointerButton button = PointerButton.Left)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Down, p, button));
    rt.ProcessPendingWork();
}
static void Drag(UiRuntime rt, SysPoint from, SysPoint to)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, to));
    rt.ProcessPendingWork();
    _ = from;
}
static void Release(UiRuntime rt, SysPoint p, PointerButton button = PointerButton.Left)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Up, p, button));
    rt.ProcessPendingWork();
}
static void Wheel(UiRuntime rt, SysPoint p, double dy = -1)
{
    rt.DispatchWheel(new WheelEvent(p, 0, dy));
    rt.ProcessPendingWork();
}
static void MoveOnly(UiRuntime rt, SysPoint p)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    rt.ProcessPendingWork();
}
static void Key(UiRuntime rt, UiKey key, KeyModifiers mod = KeyModifiers.None)
{
    rt.DispatchKey(new KeyEvent(KeyEventKind.Down, key, mod));
    rt.DispatchKey(new KeyEvent(KeyEventKind.Up, key, mod));
    rt.ProcessPendingWork();
}

// Focus-ring evidence: count of pixels matching the node's EFFECTIVE theme
// FocusRing (node.Environment.ResolvedTheme, not the harness-intended theme)
// within the node's frame bbox. Exact-ish match (tol 16) — no wide blue hue
// heuristic that could mask a missing ring.
static int FocusRingPixels(PixelBuffer px, Rect frame, double scale, Theme theme)
{
    var target = theme.FocusRing;
    int count = 0;
    int x0 = Math.Clamp((int)(frame.X * scale), 0, px.Width - 1);
    int x1 = Math.Clamp((int)(frame.Right * scale), 0, px.Width - 1);
    int y0 = Math.Clamp((int)(frame.Y * scale), 0, px.Height - 1);
    int y1 = Math.Clamp((int)(frame.Bottom * scale), 0, px.Height - 1);
    for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            if (ColorsNear(px.GetColor(x, y), target)) count++;
    return count;
}
static bool ColorsNear(Color a, Color b, int tol = 16)
    => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

// Renders the frame, saves a PNG, and RETURNS the pixel buffer so callers can
// analyze the exact same readback the screenshot was written from.
static PixelBuffer Snap(GlOffscreenSurface surf, GlRenderer renderer, UiRuntime rt, string path)
{
    surf.Render(renderer, rt.BuildFrame());
    var px = surf.ReadPixels();
    SavePng(path, px);
    return px;
}

// Find a node of a given view type; returns the first in DFS order.
// (deepest); returns the first in DFS order.
static ViewNode? Find<TView>(UiRuntime rt, Func<ViewNode, bool>? extra = null) where TView : View
    => rt.Root!.Descendants().Where(n => n.View is TView && (extra?.Invoke(n) ?? true)).FirstOrDefault();

static SysPoint CenterOf(ViewNode n) => new(n.Frame.X + n.Frame.Width / 2, n.Frame.Y + n.Frame.Height / 2);

// ------------------------------------------------------- fixture (gallery) --

// The real gallery is a single outer ScrollView containing Sections. We build
// one runtime per theme so screenshots are per-theme evidence.
static UiRuntime CreateGallery(SkiaTextService text, SkiaTextService rich,
    StubClipboardService clip, FakeTextInputService input, Theme theme, Size viewport, double scale,
    GlTextureService textures)
{
    var rt = new UiRuntime(new GalleryApp(), new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(),
        TextService = text,
        RichTextService = rich,
        ImageService = new SkiaImageService(),
        TextureService = textures,
        Clipboard = clip,
        TextInputService = input,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = theme });
    rt.SetViewport(viewport, scale);
    rt.Start();
    rt.ProcessPendingWork();
    return rt;
}

// Theme instances matching environment for pixel assertions.
var lightTheme = Theme.Light;
var darkTheme = Theme.Dark;

foreach (var (themeName, theme, scale) in new[] { ("light", lightTheme, 1.0), ("dark", darkTheme, 1.0) })
{
    var tag = themeName;
    using var text = new SkiaTextService();
    using var rich = new SkiaTextService();
    var clip = new StubClipboardService();
    var input = new FakeTextInputService();
    var tex = new GlTextureService(128);
    using var rt = CreateGallery(text, rich, clip, input, theme, new Size(1024, 768), scale, tex);
    using var renderer = new GlRenderer(tex);

    // The gallery owns its theme through UseState("theme") — the runtime root
    // environment does NOT switch it (verified run1-4: dark screenshots were
    // actually light). For the "dark" pass, click the gallery's real
    // theme-toggle button so the resolved theme is genuinely dark.
    if (theme.IsDark)
    {
        var toggle = rt.Root!.Descendants()
            .FirstOrDefault(n => n.View is Button b && b.Label.Contains("暗色主题"));
        T.Check($"{tag}-theme-toggle-found", toggle != null);
        if (toggle is { } tg)
        {
            Click(rt, CenterOf(tg));
            rt.ProcessPendingWork();
            // Assert the resolved theme under the click target is now dark;
            // if the gallery didn't switch, the run is mislabeled — fail hard.
            var sample = rt.Root!.Descendants().FirstOrDefault(n => n.View is Button);
            var isDarkNow = sample?.Environment.ResolvedTheme.IsDark == true;
            T.Check($"{tag}-really-dark", isDarkNow,
                $"resolved={sample?.Environment.ResolvedTheme.IsDark}");
            if (!isDarkNow) continue;
        }
        else continue;
    }

    // ---------- 1. click Button -> focus ring visible; click blank -> gone --
    var button = Find<Button>(rt, n => ((Button)n.View).Label.Contains("点击计数"));
    T.Check($"{tag}-gallery-button", button != null);
    if (button is null) continue;
    var blankPt = new SysPoint(4, 4); // padding corner of outer scroll — no control
    Click(rt, CenterOf(button));
    T.Check($"{tag}-btn-focused", rt.FocusedNode == button,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"} btnFrame={button.Frame}");
    var effTheme = button.Environment.ResolvedTheme;
    var pxOn = Snap(surface, renderer, rt, Path.Combine(ev, $"{run}-{tag}-1-btn-ring.png"));
    var ringOn = FocusRingPixels(pxOn, button.Frame, scale, effTheme);
    T.Check($"{tag}-btn-ring-pixels", ringOn > 40,
        $"focusring px={ringOn} frame={button.Frame} ringColor={effTheme.FocusRing} dark={effTheme.IsDark}");

    Click(rt, blankPt);
    T.Check($"{tag}-blank-clears-focus", rt.FocusedNode is null,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"}");
    var pxOff = Snap(surface, renderer, rt, Path.Combine(ev, $"{run}-{tag}-2-blank-noring.png"));
    var ringOff = FocusRingPixels(pxOff, button.Frame, scale, effTheme);
    T.Check($"{tag}-blank-noring-pixels", ringOff < Math.Max(10, ringOn / 4),
        $"ring px on={ringOn} off={ringOff}");

    // ---------- 2. Tab regains focus; Enter activates the button ------------
    var clicksBefore = ClicksFromLabel(((Button)button.View).Label);
    Key(rt, UiKey.Tab);
    T.Check($"{tag}-tab-refocuses", rt.FocusedNode is not null);
    var focusedNow = rt.FocusedNode!;
    var labelBefore = focusedNow.View is Button fb ? fb.Label : "";
    if (focusedNow.View is Button)
        Key(rt, UiKey.Enter);
    rt.ProcessPendingWork();
    // Gallery button callback increments clicks which changes its label text.
    var focusedAfter = rt.FocusedNode?.View is Button fb2 ? fb2.Label : "";
    T.Check($"{tag}-enter-activates", focusedNow.View is Button && labelBefore != focusedAfter,
        $"label '{labelBefore}' -> '{focusedAfter}'");

    // ---------- 3. click TextField -> type -> click blank -> caret/blur ------
    var field = Find<TextField>(rt, n => n.View is TextField);
    T.Check($"{tag}-textfield-found", field != null);
    if (field is { } tf)
    {
        Click(rt, CenterOf(tf));
        T.Check($"{tag}-tf-focused", rt.FocusedNode == tf);
        T.Check($"{tag}-tf-session-started", input.ActiveClient is not null,
            "text-input session started on focus");
        rt.DispatchTextInput(new TextInputEvent("Xy"));
        rt.ProcessPendingWork();
        // caret should draw while focused (frame contains a thin caret rect)
        Snap(surface, renderer, rt, Path.Combine(ev, $"{run}-{tag}-3-tf-caret.png"));
        Click(rt, blankPt);
        T.Check($"{tag}-tf-blur-on-blank", rt.FocusedNode is null,
            $"focused={rt.FocusedNode?.View.DebugName ?? "null"}");
        T.Check($"{tag}-tf-session-ended", input.ActiveClient is null,
            "text-input session ended on blur");
        Snap(surface, renderer, rt, Path.Combine(ev, $"{run}-{tag}-4-tf-nocaret.png"));
    }

    // ---------- 4. textfield -> button transfer; blur fired exactly once ----
    if (field is { } tf2)
    {
        Click(rt, CenterOf(tf2));
        T.Check($"{tag}-tf2-focused", rt.FocusedNode == tf2);
        var endBefore = input.EndCallCount;
        Click(rt, CenterOf(button));
        T.Check($"{tag}-transfer-to-button", rt.FocusedNode == button);
        T.Check($"{tag}-blur-once", input.EndCallCount - endBefore == 1,
            $"endcalls delta={input.EndCallCount - endBefore} (must be exactly 1)");
    }

    // ---------- 5. wheel + move do not clear focus ---------------------------
    if (field is { } tf3)
    {
        Click(rt, CenterOf(tf3));
        Wheel(rt, new SysPoint(20, 20), -1);
        T.Check($"{tag}-wheel-keeps-focus", rt.FocusedNode == tf3);
        MoveOnly(rt, new SysPoint(6, 6));
        T.Check($"{tag}-move-keeps-focus", rt.FocusedNode == tf3);
    }

    // ---------- 6. scrollbar interaction does NOT clear focus ----------------
    var scroll = rt.Root!.Descendants().First(n => n.View is ScrollView);
    // find the scrollbar track point: right edge gutter of the outer scroll.
    var sframe = scroll.Frame;
    // ScrollbarMetrics.Thickness is internal; the persistent gutter is 12 logical px.
    var barPt = new SysPoint(sframe.Right - 6, sframe.Y + sframe.Height / 2);
    Click(rt, CenterOf(button));          // refocus a control first
    T.Check($"{tag}-pre-scrollbar-focus", rt.FocusedNode == button);
    Click(rt, barPt);                      // click the track (or press thumb)
    T.Check($"{tag}-scrollbar-keeps-focus", rt.FocusedNode == button,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"} after scrollbar click");

    // Track-click may have paged; return to top before the drag case.
    for (var i = 0; i < 20; i++) Wheel(rt, new SysPoint(60, 60), +6);
    rt.ProcessPendingWork();
    Click(rt, CenterOf(button));
    Press(rt, barPt);
    Drag(rt, barPt, new SysPoint(barPt.X, barPt.Y + 60));
    T.Check($"{tag}-scrollbar-drag-keeps-focus", rt.FocusedNode == button);
    Release(rt, new SysPoint(barPt.X, barPt.Y + 60));
    T.Check($"{tag}-scrollbar-release-keeps-focus", rt.FocusedNode == button);

    // The scrollbar clicks may have paged the outer scroll; return to top so
    // all later geometry lookups (button/field centers) land on real controls.
    for (var i = 0; i < 20; i++) Wheel(rt, new SysPoint(60, 60), +6);
    rt.ProcessPendingWork();

    // ---------- 7. right-click on blank does NOT clear (primary-only) --------
    Click(rt, CenterOf(button));
    T.Check($"{tag}-pre-rclick-focus", rt.FocusedNode == button);
    Click(rt, blankPt, PointerButton.Right);
    T.Check($"{tag}-rightclick-blank-keeps", rt.FocusedNode == button,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"} after right click");

    // ---------- 8. plain Text (unconsumed, no focusable ancestor) blurs ------
    // The gallery has plain label Text nodes; clicking one is a "blank" click
    // (deepest hit has no focusable ancestor) and must clear focus.
    Click(rt, CenterOf(button));
    T.Check($"{tag}-pre-text-focus", rt.FocusedNode == button);
    var plainText = rt.Root!.Descendants().FirstOrDefault(n => n.View is Text);
    if (plainText is { } pt)
    {
        Click(rt, CenterOf(pt));
        T.Check($"{tag}-plaintext-clears", rt.FocusedNode is null,
            $"focused={rt.FocusedNode?.View.DebugName ?? "null"} after Text click");
    }
    else
    {
        T.Check($"{tag}-plaintext-clears", false, "no Text node found in gallery");
    }

    // ---------- 9. runtime composition blur (NOT OS-IME claim) ---------------
    // Uses the same real runtime session path (ITextInputService) the host
    // wires; the fake service only DELIVERS composition events. This proves
    // blank-click goes through EndSession + composition cancel, not that a
    // real OS IME was involved.
    if (field is { } tf4)
    {
        Click(rt, CenterOf(tf4));
        var client = input.ActiveClient;
        T.Check($"{tag}-comp-session", client is not null);
        if (client is null)
        {
            T.Check($"{tag}-comp-started", false, "no active client — session never started");
            T.Check($"{tag}-comp-blur-cancels", false, "skipped: no session");
        }
        else
        {
            var session = client.Session.SessionId;
            // Live document version — earlier sections committed text, so it is
            // > 0; a stale version would reject Start.
            var start = input.Apply(CompositionEvent.Start(session, 0, client.Document.Version, 0, new TextRange(0, 0), "ni"));
            T.Check($"{tag}-comp-started", !start.HasRejection && client.IsComposing,
                $"outcome={start.Outcome} reject={start.HasRejection}");
            Click(rt, blankPt);
            T.Check($"{tag}-comp-blur-cancels", !client.IsComposing && input.ActiveClient is null,
                $"composing={client.IsComposing} active={input.ActiveClient != null}");
            rt.ProcessPendingWork();
        }
    }

    Console.WriteLine($"=== {tag} done ===");
}

// ---------- 10. nested fixture: modifier-wrapped focusable + blank gap -------
// A dedicated small tree (not the gallery) proves ancestor-chain semantics:
//   Button wrapped in .Padding(8).Background() — a click on the Button still
//   has a focusable ancestor/self and must NOT clear; a click on the padding
//   gap (outside the Button frame) hits a non-focusable wrapper -> clears.
{
    var input = new FakeTextInputService();
    using var t1 = new SkiaTextService(); using var t2 = new SkiaTextService();
    var tex = new GlTextureService(128);
    var wrapped = new Button("嵌套按钮", () => { }).Padding(8).Background(Theme.Light.Surface);
    using var rt = new UiRuntime(
        new VStack(new Text("plain"), wrapped, new TextField(Binding<string>.Constant("x")) { Width = 160 })
            .Spacing(12).Padding(20),
        new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = t1, RichTextService = t2,
            TextureService = tex, ImageService = new SkiaImageService(),
            Clipboard = new StubClipboardService(), TextInputService = input,
        });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(400, 300), 1.0);
    rt.Start(); rt.ProcessPendingWork();
    using var renderer = new GlRenderer(tex);

    var btn = rt.Root!.Descendants().First(n => n.View is Button);
    var innerField = rt.Root!.Descendants().First(n => n.View is TextField);
    // Focus the text field, then click the nested button -> transfer, no false blank.
    Click(rt, CenterOf(innerField));
    T.Check("nest-field-focused", rt.FocusedNode == innerField);
    Click(rt, CenterOf(btn));
    T.Check("nest-button-focused", rt.FocusedNode == btn,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"}");
    // Click the padding gap: 2 logical px inside the wrapper but outside the
    // button frame (padding=8). Deepest hit = wrapper chain (no focusable) -> clear.
    var gap = new SysPoint(btn.Frame.X - 4, btn.Frame.Y - 4);
    Click(rt, gap);
    T.Check("nest-gap-clears", rt.FocusedNode is null,
        $"focused={rt.FocusedNode?.View.DebugName ?? "null"} gap={gap} btnFrame={btn.Frame}");
    Snap(surface, renderer, rt, Path.Combine(ev, $"{run}-nested-gap.png"));

    // Boundary: the "focusable PARENT containing an unhandled Text child" case
    // cannot be reproduced from a public-API harness — every focusable view in
    // the framework is a leaf and View.Children/OnPointerDown are internal.
    // Good-side core tests cover it (custom focusable container + Text child).
}

Console.WriteLine($"=== BCF RESULT {T.Pass} PASS / {T.Fail} FAIL ===");
return T.Fail == 0 ? 0 : 1;

// ----------------------------- helpers --------------------------------------

static int ClicksFromLabel(string label)
{
    // "点击计数：N" -> N; returns -1 on parse failure.
    var idx = label.LastIndexOf('：');
    return idx >= 0 && int.TryParse(label[(idx + 1)..], out var n) ? n : -1;
}

static void SavePng(string path, PixelBuffer pixels)
{
    using var bmp = new SKBitmap(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    Marshal.Copy(pixels.Pixels, 0, bmp.GetPixels(), pixels.Pixels.Length);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    Console.WriteLine($"PNG {path} {pixels.Width}x{pixels.Height}");
}

static class T
{
    public static int Pass, Fail;
    public static void Check(string n, bool ok, string note = "")
    {
        if (ok) { Pass++; Console.WriteLine($"PASS {n} {note}"); }
        else { Fail++; Console.WriteLine($"FAIL {n} {note}"); }
    }
}

// Minimal runtime composition service — delivers events to whatever client the
// runtime binds. Used ONLY to prove the blur path runs EndSession and cancels
// an in-flight composition; it is not evidence of a real OS IME.
sealed class FakeTextInputService : ITextInputService
{
    public TextInputCapabilities Capabilities => TextInputCapabilities.Full;
    public ITextInputClient? ActiveClient { get; private set; }
    public int StartCount { get; private set; }
    public int EndCallCount { get; private set; }

    public bool StartSession(ITextInputClient client)
    {
        StartCount++;
        ActiveClient = client;
        return true;
    }
    public bool EndSession()
    {
        EndCallCount++;
        if (ActiveClient is null) return false;
        ActiveClient = null;
        return true;
    }
    public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;
    public bool PublishGeometry(TextInputGeometry geometry) => false;
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => (ActiveClient ?? throw new InvalidOperationException("no active client")).ApplyComposition(events);
    public TextInputApplyResult Apply(params CompositionEvent[] events)
        => ApplyComposition(events);
}
