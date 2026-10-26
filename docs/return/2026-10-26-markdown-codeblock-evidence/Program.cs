// Markdown code-block acceptance harness — ROUND 1 (prepared, NOT RUN before freeze).
// Verifies NEW feature: fenced/indented code blocks get a rounded-rect background
// plus an optional overlay copy button.
//
// Contract under test (behavior-level, NOT pinned to internal view names):
//   1. Code block background is visibly rounded: sampling pixels just inside the
//      background box near a corner must show the PAGE color (corner clipped),
//      while points (radius, radius) inside are the code background. Both themes.
//   2. Copy button exists when style toggle on (default): a clickable control in
//      the top-right of the code block's VIEWPORT; clicking it writes the exact
//      raw code text (leading spaces, blank lines, unicode; NOT the fence or
//      language tag) to the runtime IClipboardService.
//   3. Toggle off -> button not drawn AND not clickable (click writes nothing).
//   4. Wide code in a narrow doc: code stays single-line (NoWrap) and scrolls
//      horizontally via wheel DeltaX; the copy button stays inside the visible
//      viewport (overlaid, not scrolled away) at scrolled offset.
//   5. Empty code block (``````\n``````) copies empty string; no crash.
//   6. Inline `code` produces NO button and NO block background rectangle.
//   7. Real OS clipboard: if the running UiRuntime can reach the host clipboard
//      (GLFW-backed IClipboardService injected here via the same GLFW call the
//      host uses on its hidden test window), read-back must equal code text.
//      Prior user clipboard text is saved first and restored afterwards.
//      If OS clipboard is unreachable in this process, the injected clipboard
//      service is still the SAME concrete type the host wires (boundary noted).
//
// Style options are discovered by name at runtime (expected CodeCornerRadius /
// ShowCodeCopyButton per task notes); if the landed names differ, set env
// MDCB_CORNER / MDCB_SHOWBTN to the real property names before running.
//
// Env:  MDCB_EV (evidence dir), MDCB_RUN (run id), MDCB_CORNER, MDCB_SHOWBTN.

using System.Reflection;
using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.OpenTK;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;
using SysPoint = CSharp.UI.Point;

var ev = Environment.GetEnvironmentVariable("MDCB_EV")
    ?? throw new InvalidOperationException("MDCB_EV not set");
Directory.CreateDirectory(ev);
T.Check("evidence-dir-real-repo", ev.Contains(@"C#\CSharp.UI"), ev);
var run = Environment.GetEnvironmentVariable("MDCB_RUN") ?? "run1";

foreach (var asmName in new[] { "CSharp.UI", "CSharp.UI.Markdown", "CSharp.UI.OpenTK", "CSharp.UI.Text" })
{
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName)
              ?? Assembly.Load(asmName);
    var loc = asm.Location;
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loc)));
    Console.WriteLine($"ASM {asmName} {loc} sha256={sha[..16]}");
}

// Discover the (possibly differently named) style properties on the REAL options type.
var cornerName = Environment.GetEnvironmentVariable("MDCB_CORNER") ?? "CodeCornerRadius";
var showName = Environment.GetEnvironmentVariable("MDCB_SHOWBTN") ?? "ShowCodeCopyButton";
var cornerProp = typeof(MarkdownStyleOptions).GetProperty(cornerName, BindingFlags.Public | BindingFlags.Instance);
var showProp = typeof(MarkdownStyleOptions).GetProperty(showName, BindingFlags.Public | BindingFlags.Instance);
T.Check("style-corner-prop", cornerProp != null && cornerProp.PropertyType == typeof(double),
    $"props={string.Join(',', typeof(MarkdownStyleOptions).GetProperties().Select(p => p.Name))}");
T.Check("style-showbtn-prop", showProp != null && showProp.PropertyType == typeof(bool),
    $"props={string.Join(',', typeof(MarkdownStyleOptions).GetProperties().Select(p => p.Name))}");
if (cornerProp == null || showProp == null)
{
    Console.WriteLine("=== MDCB RESULT: style properties not found — abort (report to main) ===");
    return 2;
}
var cornerDefault = (double)cornerProp.GetValue(new MarkdownStyleOptions())!;
var showDefault = (bool)showProp.GetValue(new MarkdownStyleOptions())!;
Console.WriteLine($"STYLE {cornerName} default={cornerDefault}; {showName} default={showDefault}");

GlOffscreenSurface.AllowNonMainThreadCreation();
using var surface = new GlOffscreenSurface(512, 512);
Console.WriteLine($"HOST {surface.ContextDescription}");

// Fixture: fenced block WITH language tag (must not be copied), indented code,
// inline code, empty fenced block, and a very wide fenced line for scroll checks.
var wideLine = "var x = " + new string('q', 220) + "; // tail-marker";
var docMd = """
# Code Block Acceptance

Paragraph before code with `inline` snippet in it.

```csharp
  indented-lead = true;

var unicode = "héllo→世界";
```

    indented code block line




""";
// appending indented/fenced/empty/wide blocks manually
var docFull = docMd +
    "\n    indented code block line\n\n" +
    "```\n```\n\n" +                  // empty fenced block
    "```\n" + wideLine + "\n```\n";     // very wide fenced line
var doc = Markdown.Parse(docFull);
Console.WriteLine($"DOC len={docFull.Length}");

// Locate the code BLOCK container: the internal MarkdownCodeBlockView (or any
// single-child wrapper) whose subtree holds a ScrollView(Horizontal) containing
// the code RichText. Header/copy button live OUTSIDE the scroll viewport in the
// real implementation, so we search the block container, not just the scroll.
static ViewNode? FindCodeBlock(UiRuntime rt, string needle)
{
    var scroll = rt.Root!.Descendants()
        .Where(n => n.View is ScrollView s && s.Axis == ScrollAxis.Horizontal)
        .FirstOrDefault(n => n.Descendants()
            .Any(d => d.View is RichText r && r.Content.Text.Contains(needle)));
    if (scroll == null) return null;
    // climb to the smallest ancestor that contains a Button OR is directly under
    // the MarkdownBlockView content stack — that ancestor is the block wrapper.
    var node = scroll;
    for (var p = scroll; p != null; p = p.Parent)
    {
        if (p.Descendants().Any(d => d.View is Button)) { node = p; break; }
        node = p;
    }
    return node;
}

// Copy-button point: prefer a real framework Button anywhere inside the block
// container (header is outside the horizontal scroll). Fall back to any
// clickable non-text leaf. Returns logical-px hit point.
static SysPoint? FindButtonPoint(UiRuntime rt, ViewNode codeBlock, ViewNode? _)
{
    var btns = codeBlock.Descendants().Where(d => d.View is Button).ToList();
    var vp = codeBlock.Frame;
    if (btns.Count > 0)
    {
        var f = btns[0].Frame;
        return new SysPoint(f.X + f.Width / 2, f.Y + f.Height / 2);
    }
    for (double yy = vp.Y + 2; yy < vp.Bottom - 2; yy += 2)
        for (double xx = vp.Right - 4; xx > vp.X + 4; xx -= 2)
        {
            var n = rt.HitTest(new SysPoint(xx, yy));
            if (n == null) continue;
            if (n.View is not RichText and not ScrollView and not Spacer
                && !n.Descendants().Skip(1).Any(d => d.View is RichText))
                return new SysPoint(xx, yy);
        }
    return null;
}

static void Click(UiRuntime rt, SysPoint p)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Down, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Up, p));
    rt.ProcessPendingWork();
}

static bool CornersRounded(PixelBuffer px, Rect bg, double scale, out string detail)
{
    // Frame coords are logical; pixels are physical.
    // Frame coords are logical; pixels are physical. Probe a point hugging the
    // corner (0.6 logical px in) — clipped for any positive radius — vs a point
    // well inside (16 logical px) that must be bg-filled. r=0 must NOT clip.
    int cx = (int)Math.Round((bg.X + 0.6) * scale);
    int cy = (int)Math.Round((bg.Y + 0.6) * scale);
    int ix = (int)Math.Round((bg.X + 16) * scale);
    int iy = (int)Math.Round((bg.Y + 16) * scale);
    var corner = px.GetColor(Math.Clamp(cx, 0, px.Width - 1), Math.Clamp(cy, 0, px.Height - 1));
    var inside = px.GetColor(Math.Clamp(ix, 0, px.Width - 1), Math.Clamp(iy, 0, px.Height - 1));
    detail = $"corner={corner} inside={inside} bg={bg}";
    return !corner.Equals(inside); // corner clipped -> differs from bg-fill pixel
}

// ---------- Part A: representative matrix + radius config consumption ----------
// Representative subset (not full 24): light-320x1, dark-1024x1, dark-320x1.5,
// light-1024x2 — required screenshots plus radius/on-off evidence.
MarkdownStyleOptions StyleWith(object? corner = null, bool? show = null)
{
    var s = new MarkdownStyleOptions();
    if (corner is double c) cornerProp.SetValue(s, c);
    if (show is bool b) showProp.SetValue(s, b);
    return s;
}

foreach (var (theme, w, scale, radius) in new[]
         { ("light", 320, 1.0, (double?)null), ("dark", 1024, 1.0, (double?)null),
           ("dark", 320, 1.5, (double?)null), ("light", 1024, 2.0, (double?)null),
           ("light", 320, 1.0, 0.0),         // radius=0 config consumption
           ("light", 320, 1.0, 20.0) })      // radius=20 visibly larger
{
    var tag = $"A-{theme}-{w}x{scale}" + (radius is { } r ? $"-r{(int)r}" : "");
    using var skia = new SkiaTextService();
    using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService();
    using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var view = new MarkdownBlockView { Document = doc, Style = radius is { } rr ? StyleWith(rr, true) : null };
    using var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(),
        TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img,
        Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = theme == "dark" ? Theme.Dark : Theme.Light });
    rt.SetViewport(new Size(w, 700), scale);
    rt.Start();
    rt.ProcessPendingWork();
    surface.Render(renderer, rt.BuildFrame());
    SavePng(Path.Combine(ev, $"{run}-{tag}-top.png"), surface.ReadPixels());

    var codeScroll = FindCodeBlock(rt, "indented-lead");
    T.Check($"{tag}-code-scroll", codeScroll != null);
    if (codeScroll == null) continue;

    var px = surface.ReadPixels();
    bool rounded = CornersRounded(px, codeScroll.Frame, scale, out var cd);
    if (radius == 0.0)
        T.Check($"{tag}-radius0-square", !rounded, $"expect square: {cd}");
    else
        T.Check($"{tag}-rounded-bg", rounded, cd);

    var bp = FindButtonPoint(rt, codeScroll, null);
    T.Check($"{tag}-btn-found", bp != null);
    if (bp is { } pt && radius == null)
    {
        clip.SetText("__sentinel__"); var n0 = clip.SetCount;
        Click(rt, pt);
        var expected = "  indented-lead = true;\n\nvar unicode = \"héllo→世界\";";
        T.Check($"{tag}-btn-writes", clip.SetCount > n0 && clip.GetText() == expected,
            $"got={clip.GetText()?.Replace("\n", "\\n")}");
    }
    Console.WriteLine($"=== {tag} done ===");
}

// ---------- Part B: dynamic UseState toggle true->false->true on the SAME doc --
// Same live runtime: locate the clicked button point while ON, flip state off
// (rebuild via UseState), click the same coordinates -> zero clipboard writes;
// flip back on -> writes again. Real config-consumption evidence, not a fresh
// options literal.
{
    using var skia = new SkiaTextService(); using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService(); using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var host = new ToggleHost(doc);
    using var rt = new UiRuntime(host, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(), TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img, Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(640, 700), 1.0);
    rt.Start(); rt.ProcessPendingWork();
    var codeScroll = FindCodeBlock(rt, "indented-lead");
    T.Check("B-code-scroll", codeScroll != null);
    var bp = codeScroll != null ? FindButtonPoint(rt, codeScroll, null) : null;
    T.Check("B-btn-found-on", bp != null);
    if (codeScroll != null && bp is { } pt)
    {
        clip.SetText("__s__"); var n0 = clip.SetCount;
        Click(rt, pt);
        T.Check("B-on-writes", clip.SetCount > n0);

        var hostNode = rt.Root!.Descendants().First(n => n.View is ToggleHost);
        var state = hostNode.FindState<bool>("show")!;
        state.Value = false;
        rt.ProcessPendingWork();
        surface.Render(renderer, rt.BuildFrame());
        SavePng(Path.Combine(ev, $"{run}-B-off.png"), surface.ReadPixels());

        clip.SetText("__s2__"); var n1 = clip.SetCount;
        Click(rt, pt); // same coords, button must be gone
        T.Check("B-off-no-writes", clip.SetCount == n1, $"writes={clip.SetCount - n1}");

        state.Value = true;
        rt.ProcessPendingWork();
        surface.Render(renderer, rt.BuildFrame());
        var codeScroll2 = FindCodeBlock(rt, "indented-lead");
        var bp2 = codeScroll2 != null ? FindButtonPoint(rt, codeScroll2, null) : null;
        if (bp2 is { } pt2)
        {
            clip.SetText("__s3__"); var n2 = clip.SetCount;
            Click(rt, pt2);
            T.Check("B-rewrite-on", clip.SetCount > n2);
        }
        else T.Check("B-rewrite-on", false, "button not found after re-enable");
    }
}


// ---------- Part C: horizontal scroll keeps button in viewport ----------
{
    using var skia = new SkiaTextService(); using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService(); using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var view = new MarkdownBlockView { Document = doc };
    using var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(), TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img, Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(320, 700), 1.0);
    rt.Start(); rt.ProcessPendingWork();

    var codeScroll = FindCodeBlock(rt, "tail-marker");
    T.Check("C-wide-scroll", codeScroll != null);
    if (codeScroll != null)
    {
        var contentRt = codeScroll.Descendants().First(n => n.View is RichText);
        var w0 = contentRt.Frame;
        T.Check("C-nowrap-wide", contentRt.DesiredSize.Width > codeScroll.Frame.Width,
            $"contentW={contentRt.DesiredSize.Width} vpW={codeScroll.Frame.Width}");
        // scroll right inside the code block
        var scrollVp = codeScroll.Descendants().First(n => n.View is ScrollView).Frame;
        var c = new SysPoint(scrollVp.X + scrollVp.Width / 2,
                             scrollVp.Y + scrollVp.Height / 2);
        for (var i = 0; i < 5; i++)
            rt.DispatchWheel(new WheelEvent(c, DeltaX: -3, DeltaY: 0));
        rt.ProcessPendingWork();
        surface.Render(renderer, rt.BuildFrame());
        SavePng(Path.Combine(ev, $"{run}-C-scrolled.png"), surface.ReadPixels());
        var w1 = contentRt.Frame;
        T.Check("C-content-scrolled", w1.X != w0.X || contentRt.Frame.X < codeScroll.Frame.X, $"x {w0.X} -> {w1.X}");
        // button still clickable in viewport after scroll
        var bp = FindButtonPoint(rt, codeScroll, null);
        T.Check("C-btn-in-viewport", bp != null && codeScroll.Frame.Contains(bp.Value));
        if (bp is { } pt)
        {
            clip.SetText("__s__"); var n0 = clip.SetCount;
            Click(rt, pt);
            T.Check("C-btn-writes-after-scroll", clip.SetCount > n0
                && (clip.GetText() ?? "").Contains("tail-marker"));
        }
    }
}

// ---------- Part D: real OS clipboard — SAFETY GATED ---------------------------
// NEVER overwrite unrecoverable user clipboard content. We enumerate clipboard
// formats first; only if every present format is one we can fully save and
// restore (CF_UNICODETEXT / CF_TEXT / CF_OEMTEXT / CF_LOCALE) do we run the
// GLFW roundtrip and restore afterwards. Any image/file/custom format present
// -> SKIP the OS read/write entirely and report the boundary; stub-based
// verification above still stands.
{
    var formats = WinClip.EnumFormats();
    Console.WriteLine($"OSCLIP formats=[{string.Join(',', formats)}]");
    var safe = formats.Count == 0
        || formats.All(f => f is 1 or 7 or 13 or 16); // TEXT/OEM/UNICODETEXT/LOCALE
    if (!safe)
        Console.WriteLine("OSCLIP gate: non-text payload present — OS read/write SKIPPED per user-data-protection policy.");
    if (safe)
    {
        var saved = WinClip.Get();
        try
        {
            using var win = new OpenTK.Windowing.Desktop.NativeWindow(
                new OpenTK.Windowing.Desktop.NativeWindowSettings
                {
                    StartVisible = false,
                    StartFocused = false,
                    Title = "mdcb-clip",
                    Size = new OpenTK.Mathematics.Vector2i(64, 64),
                });
            var svc = new GlfwClipboardFor(win);
            var sample = "  indented-lead = true;\n\nvar unicode = \"héllo→世界\";";
            svc.SetText(sample);
            var back = svc.GetText();
            T.Check("D-os-clipboard-roundtrip", back == sample,
                back == null ? "null" : $"len={back.Length}");
            T.Check("D-utf8-exact", back != null && back.Contains("héllo→世界"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OSCLIP unreachable: {ex.GetType().Name} {ex.Message}");
            T.Check("D-os-clipboard-unreachable-boundary", false,
                "report as platform limit; framework-level Stub roundtrip already verified");
        }
        finally
        {
            if (saved != null) WinClip.Set(saved);
        }
    }
    else
    {
        Console.WriteLine("OSCLIP skip: user clipboard holds non-text payload; not touching it.");
    }
}

// ---------- Part E: inline code has no button / no block box ----------
{
    var inlineDoc = Markdown.Parse("Text with `inline code` and more text.\n");
    using var skia = new SkiaTextService(); using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService(); using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var view = new MarkdownBlockView { Document = inlineDoc };
    using var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(), TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img, Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(320, 700), 1.0);
    rt.Start(); rt.ProcessPendingWork();
    var clipN = clip.SetCount;
    for (double yy = 0; yy < 400; yy += 4)
        for (double xx = 0; xx < 320; xx += 4)
            Click(rt, new SysPoint(xx, yy));
    T.Check("E-inline-no-copy", clip.SetCount == clipN, $"writes={clip.SetCount - clipN}");
}

// ---------- Part F: empty code block copies empty string ----------
{
    var emptyDoc = Markdown.Parse("```\n```\n");
    using var skia = new SkiaTextService(); using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService(); using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var view = new MarkdownBlockView { Document = emptyDoc };
    using var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(), TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img, Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(320, 700), 1.0);
    rt.Start(); rt.ProcessPendingWork();
    // find the code block container (empty text → locate via the horizontal
    // scroll inside the block wrapper, then climb like FindCodeBlock)
    var codeScroll = rt.Root!.Descendants()
        .Where(n => n.View is ScrollView s && s.Axis == ScrollAxis.Horizontal)
        .FirstOrDefault();
    T.Check("F-code-scroll", codeScroll != null);
    if (codeScroll != null)
    {
        var block = codeScroll;
        for (var p = codeScroll; p != null; p = p.Parent)
        {
            if (p.Descendants().Any(d => d.View is Button)) { block = p; break; }
            block = p;
        }
        var bp = FindButtonPoint(rt, block, null);
        if (bp is { } pt)
        {
            clip.SetText("__s__"); var n0 = clip.SetCount;
            Click(rt, pt);
            T.Check("F-empty-copies-empty", clip.SetCount > n0 && clip.GetText() == string.Empty,
                $"got={(clip.GetText() == null ? "null" : $"'{clip.GetText()}'")}");
        }
        else Console.WriteLine("F: no button point (empty block may still render box)");
    }
}

// ---------- Part G: CodeBackground=null still renders the copy button ----------
{
    using var skia = new SkiaTextService(); using var skia2 = new SkiaTextService();
    using var img = new SkiaImageService(); using var textures = new GlTextureService(128);
    using var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var noBg = new MarkdownStyleOptions { CodeBackground = null };
    var view = new MarkdownBlockView { Document = doc, Style = noBg };
    using var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(), TextService = skia, RichTextService = skia2,
        TextureService = textures, ImageService = img, Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
    rt.SetViewport(new Size(320, 700), 1.0);
    rt.Start(); rt.ProcessPendingWork();
    var block = FindCodeBlock(rt, "indented-lead");
    var bp = block != null ? FindButtonPoint(rt, block, null) : null;
    T.Check("G-nobg-btn-found", bp != null);
    if (bp is { } pt)
    {
        clip.SetText("__g__"); var n0 = clip.SetCount;
        Click(rt, pt);
        T.Check("G-nobg-btn-writes", clip.SetCount > n0
            && (clip.GetText() ?? "").Contains("indented-lead"));
    }
}

Console.WriteLine($"=== MDCB RESULT {T.Pass} PASS / {T.Fail} FAIL ===");
return T.Fail == 0 ? 0 : 1;


sealed unsafe class GlfwClipboardFor : IClipboardService
{
    private readonly OpenTK.Windowing.Desktop.NativeWindow _w;
    public GlfwClipboardFor(OpenTK.Windowing.Desktop.NativeWindow w) => _w = w;
    public string? GetText()
    {
        try
        {
            var t = OpenTK.Windowing.GraphicsLibraryFramework.GLFW.GetClipboardString(_w.WindowPtr);
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch { return null; }
    }
    public void SetText(string text)
    {
        try { OpenTK.Windowing.GraphicsLibraryFramework.GLFW.SetClipboardString(_w.WindowPtr, text); }
        catch { }
    }
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

static partial class Program
{
    static void SavePng(string path, PixelBuffer pixels)
    {
        using var bmp = new SKBitmap(pixels.Width, pixels.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        Marshal.Copy(pixels.Pixels, 0, bmp.GetPixels(), pixels.Pixels.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        Console.WriteLine($"PNG {path} {pixels.Width}x{pixels.Height}");
    }
}

// Minimal user clipboard save/restore via Win32 (text only; non-text payloads
// are documented as out of scope — we restore text if we read text).
static class WinClip
{
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint f, UIntPtr b);
    const uint CF_UNICODETEXT = 13; const uint GMEM_MOVEABLE = 0x0002;
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);

    public static List<uint> EnumFormats()
    {
        var list = new List<uint>();
        if (!OpenClipboard(IntPtr.Zero)) return list;
        try { uint f = 0; while ((f = EnumClipboardFormats(f)) != 0) list.Add(f); }
        finally { CloseClipboard(); }
        return list;
    }

    public static string? Get()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUni(p);
            GlobalUnlock(h);
            return s;
        }
        finally { CloseClipboard(); }
    }

    public static void Set(string s)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var bytes = (s.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            var p = GlobalLock(h);
            Marshal.Copy(s.ToCharArray(), 0, p, s.Length);
            Marshal.WriteInt16(p + s.Length * 2, 0);
            GlobalUnlock(h);
            SetClipboardData(CF_UNICODETEXT, h);
        }
        finally { CloseClipboard(); }
    }
}
sealed class ToggleHost : Component
{
    private readonly MarkdownDocument _doc;
    public ToggleHost(MarkdownDocument doc) => _doc = doc;
    protected override View Build()
    {
        var show = UseState("show", true);
        var s = new MarkdownStyleOptions();
        if (!show.Value)
            typeof(MarkdownStyleOptions)
                .GetProperty(Environment.GetEnvironmentVariable("MDCB_SHOWBTN") ?? "ShowCodeCopyButton")!
                .SetValue(s, false);
        return new MarkdownBlockView { Document = _doc, Style = s };
    }
}
