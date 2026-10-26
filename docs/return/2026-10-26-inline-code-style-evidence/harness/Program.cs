// Inline-code style acceptance harness — ROUND 1 (prepared, NOT RUN before freeze).
// Verifies NEW feature: inline `code` gets a rounded background rect per line
// fragment and its text is painted with the node's effective Theme.Accent.
//
// Behavior-level checks (no private reflection, no hand-drawn verification):
//   * code ink = node.Environment.ResolvedTheme.Accent (tol) — custom accent
//     themes (#00C853, #E040FB) prove live-theme consumption, not hardcoded blue.
//   * per-fragment background rect is corner-clipped (corner pixel = page,
//     inner pixel = bg); a plain TextStyle.Background run stays square.
//   * wrapped code token -> per-line background fragments, no bleed into
//     trailing text; multiple inline codes same paragraph stay accent.
//   * CodeBackground=null -> no bg rect but accent ink still present.
//   * bold+link code: link still activates with correct payload.
//   * fenced block unchanged: wrapper rounded rect + copy button writes exact
//     code text; inline code has no copy affordance (click sweep = 0 writes).
//   * dynamic theme swap Light->Dark->custom accent on the SAME runtime/doc.
//   * external .Foreground() recolors plain text but code keeps accent.
//
// Segment geometry comes from the REAL committed layout: a RecordingRichText-
// Service decorator over SkiaTextService captures the RichTextLayoutResult
// handles that the view itself laid out (public IRichTextService surface).
//
// Env: ICS_EV (evidence dir), ICS_RUN (run id).

using System.Reflection;
using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.Markdown;
using CSharp.UI.OpenTK;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using SkiaSharp;
using SysPoint = CSharp.UI.Point;

var ev = Environment.GetEnvironmentVariable("ICS_EV")
    ?? throw new InvalidOperationException("ICS_EV not set");
Directory.CreateDirectory(ev);
T.Check("evidence-dir-real-repo", ev.Contains(@"C#\CSharp.UI"), ev);
var run = Environment.GetEnvironmentVariable("ICS_RUN") ?? "run1";

foreach (var asmName in new[] { "CSharp.UI", "CSharp.UI.Markdown", "CSharp.UI.OpenTK", "CSharp.UI.Text" })
{
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName)
              ?? Assembly.Load(asmName);
    var loc = asm.Location;
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loc)));
    Console.WriteLine($"ASM {asmName} {loc} sha256={sha[..16]}");
}

Console.WriteLine($"STYLE-PROPS {string.Join(',', typeof(MarkdownStyleOptions).GetProperties().Select(p => p.Name))}");

GlOffscreenSurface.AllowNonMainThreadCreation();
using var surface = new GlOffscreenSurface(512, 512);
Console.WriteLine($"HOST {surface.ContextDescription}");

// ---------- Fixture ----------
var linkUrl = "https://example.test/x";
var docMd = """
# Inline Code Acceptance

Start `alpha` mid `beta` end `héllo世界` tail.

Wrap: `WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW` after.

Mix **`boldcode`** and [`linkcode`](https://example.test/x) plain.

```csharp
  block-lead = true;
```

End `trailing`.
""";
var doc = Markdown.Parse(docMd);

// ---------- Runtime plumbing ----------

static Ctx NewRuntime(View view, Theme theme, double width, double scale)
{
    var skia = new SkiaTextService();
    var inner = new SkiaTextService();
    var rich = new RecordingRich(inner);
    var img = new SkiaImageService();
    var textures = new GlTextureService(128);
    var renderer = new GlRenderer(textures);
    var clip = new StubClipboardService();
    var rt = new UiRuntime(view, new UiRuntimeServices
    {
        Dispatcher = new HeadlessUiDispatcher(),
        TextService = skia,
        RichTextService = rich,
        TextureService = textures,
        ImageService = img,
        Clipboard = clip,
    });
    rt.SetEnvironment(new EnvironmentValues { Theme = theme });
    rt.SetViewport(new Size(width, 700), scale);
    rt.Start();
    rt.ProcessPendingWork();
    return new Ctx(rt, renderer, rich, clip);
}

PixelBuffer Snap(Ctx c, string tag)
{
    surface.Render(c.Renderer, c.Rt.BuildFrame());
    var px = surface.ReadPixels();
    SavePng(Path.Combine(ev, $"{run}-{tag}.png"), px);
    return px;
}

static ViewNode? FindRich(UiRuntime rt, string needle)
    => rt.Root!.Descendants()
        .FirstOrDefault(n => n.View is RichText r && r.Content.Text.Contains(needle));

// Segment-space rects (logical, window coords) covering `token` in `rich`.
// Layout = the recording service's latest handle for the view's own request.
static List<Rect> TokenRects(Ctx ctx, ViewNode rich, string token, int occurrence = 0)
{
    var r = (RichText)rich.View;
    var text = r.Content.Text;
    var idx = -1;
    for (var k = 0; k <= occurrence; k++)
    {
        idx = text.IndexOf(token, idx + 1, StringComparison.Ordinal);
        if (idx < 0) return [];
    }
    var start = idx;
    var end = start + token.Length;
    var layout = ctx.Rich.LastFor(r.Content);
    if (layout == null) return [];
    var frame = rich.Frame;
    // Segments are relative to the TEXT BLOCK origin: EmitRich places the
    // block at ResolveOrigin = vertical-centered, physical-pixel-snapped —
    // NOT frame.Y. Same public math reproduced here (TextPainter is
    // internal): y = frame.Y + (frame.Height - layout.Size.Height)/2, then
    // rounded to the pixel grid at layout.PixelScale.
    var scale = layout.PixelScale;
    var oy = Math.Round(
        (frame.Y + ((frame.Height - layout.Size.Height) / 2)) * scale,
        MidpointRounding.AwayFromZero) / scale;
    var ox = r.Alignment switch
    {
        TextAlignment.Center => frame.X + ((frame.Width - layout.Size.Width) / 2),
        TextAlignment.Right => frame.Right - layout.Size.Width,
        _ => frame.X,
    };
    ox = Math.Round(ox * scale, MidpointRounding.AwayFromZero) / scale;
    var origin = new Point(ox, oy);
    var rects = new List<Rect>();
    foreach (var seg in layout.Segments)
    {
        if (seg.Start < end && seg.End > start)
        {
            var line = layout.Lines[seg.LineIndex];
            rects.Add(new Rect(
                origin.X + seg.X,
                origin.Y + line.Bounds.Y,
                seg.Width,
                line.Bounds.Height));
        }
    }
    return rects;
}

static int MatchPixels(PixelBuffer px, Rect rect, double scale, Color target, int tol, int inset = 0)
{
    var count = 0;
    int x0 = Math.Clamp((int)Math.Floor((rect.X + inset) * scale), 0, px.Width - 1);
    int x1 = Math.Clamp((int)Math.Ceiling((rect.Right - inset) * scale), 0, px.Width - 1);
    int y0 = Math.Clamp((int)Math.Floor((rect.Y + inset) * scale), 0, px.Height - 1);
    int y1 = Math.Clamp((int)Math.Ceiling((rect.Bottom - inset) * scale), 0, px.Height - 1);
    for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var c = px.GetColor(x, y);
            if (Math.Abs(c.R - target.R) <= tol && Math.Abs(c.G - target.G) <= tol
                && Math.Abs(c.B - target.B) <= tol && c.A > 200)
                count++;
        }
    return count;
}

// Corner-clip probe: corner-hugging point must differ from deep-inside point.
static (bool rounded, Color corner, Color inside) CornerProbe(
    PixelBuffer px, Rect rect, double scale, double cornerIn = 0.6, double deep = 12)
{
    int cx = Math.Clamp((int)Math.Round((rect.X + cornerIn) * scale), 0, px.Width - 1);
    int cy = Math.Clamp((int)Math.Round((rect.Y + cornerIn) * scale), 0, px.Height - 1);
    int ix = Math.Clamp((int)Math.Round((rect.X + Math.Min(deep, rect.Width / 2)) * scale), 0, px.Width - 1);
    int iy = Math.Clamp((int)Math.Round((rect.Y + Math.Min(deep, rect.Height / 2)) * scale), 0, px.Height - 1);
    var corner = px.GetColor(cx, cy);
    var inside = px.GetColor(ix, iy);
    return (!corner.Equals(inside), corner, inside);
}

// Count pixels in `rect` matching ANY target in `targets` within tol.
static int MatchAny(PixelBuffer px, Rect rect, double scale, int tol, params Color[] targets)
{
    var count = 0;
    int x0 = Math.Clamp((int)Math.Floor(rect.X * scale), 0, px.Width - 1);
    int x1 = Math.Clamp((int)Math.Ceiling(rect.Right * scale), 0, px.Width - 1);
    int y0 = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, px.Height - 1);
    int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), 0, px.Height - 1);
    for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var c = px.GetColor(x, y);
            foreach (var t in targets)
                if (Math.Abs(c.R - t.R) <= tol && Math.Abs(c.G - t.G) <= tol
                    && Math.Abs(c.B - t.B) <= tol && c.A > 200) { count++; break; }
        }
    return count;
}

// Count "ink-like" pixels: darker/saturated vs the page color, i.e. any glyph.
static int InkPixels(PixelBuffer px, Rect rect, double scale, Color page)
{
    var count = 0;
    int x0 = Math.Clamp((int)Math.Floor(rect.X * scale), 0, px.Width - 1);
    int x1 = Math.Clamp((int)Math.Ceiling(rect.Right * scale), 0, px.Width - 1);
    int y0 = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, px.Height - 1);
    int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), 0, px.Height - 1);
    for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var c = px.GetColor(x, y);
            // glyph ink: noticeably darker or more saturated than the page
            var dist = Math.Abs(c.R - page.R) + Math.Abs(c.G - page.G) + Math.Abs(c.B - page.B);
            if (c.A > 200 && dist > 90)
                count++;
        }
    return count;
}

// Longest horizontal run (in physical px) of pixels matching `target` within
// tol on the given logical scan line `y` inside [x0,x1] logical.
static (int longestRun, int totalMatch) BgRun(PixelBuffer px, double y, double x0, double x1, double scale, Color target, int tol)
{
    int iy = Math.Clamp((int)Math.Round(y * scale), 0, px.Height - 1);
    int ix0 = Math.Clamp((int)Math.Floor(x0 * scale), 0, px.Width - 1);
    int ix1 = Math.Clamp((int)Math.Ceiling(x1 * scale), 0, px.Width - 1);
    var run = 0; var longest = 0; var total = 0;
    for (var x = ix0; x <= ix1; x++)
    {
        var c = px.GetColor(x, iy);
        if (Math.Abs(c.R - target.R) <= tol && Math.Abs(c.G - target.G) <= tol
            && Math.Abs(c.B - target.B) <= tol && c.A > 200)
        { run++; total++; longest = Math.Max(longest, run); }
        else run = 0;
    }
    return (longest, total);
}

// Most frequent opaque color inside `rect` (the bg fill dominates ink).
static Color ModalColor(PixelBuffer px, Rect rect, double scale)
{
    var hist = new Dictionary<(byte r, byte g, byte b), int>();
    int x0 = Math.Clamp((int)Math.Floor(rect.X * scale), 0, px.Width - 1);
    int x1 = Math.Clamp((int)Math.Ceiling(rect.Right * scale), 0, px.Width - 1);
    int y0 = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, px.Height - 1);
    int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), 0, px.Height - 1);
    for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var c = px.GetColor(x, y);
            if (c.A <= 200) continue;
            var key = (c.R, c.G, c.B);
            hist[key] = hist.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    var best = hist.OrderByDescending(kv => kv.Value).FirstOrDefault();
    return new Color(best.Key.r, best.Key.g, best.Key.b, 255);
}

static void Click(UiRuntime rt, SysPoint p)
{
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Move, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Down, p));
    rt.DispatchPointer(new PointerEvent(PointerEventKind.Up, p));
    rt.ProcessPendingWork();
}

// ---------- Part A: representative matrix light/dark x 320/1024 x ps 1.5/2.0 ----------
var customAccentA = Color.Parse("#00C853");
var customTheme = Theme.Light with { Accent = customAccentA };

foreach (var (themeName, theme, w, scale) in new (string, Theme, int, double)[]
{
    ("light", Theme.Light, 320, 1.5),
    ("dark", Theme.Dark, 1024, 2.0),
    ("light", Theme.Light, 1024, 1.5),
    ("dark", Theme.Dark, 320, 2.0),
    ("custom", customTheme, 320, 1.5),
})
{
    var tag = $"A-{themeName}-{w}x{scale}";
    var ctx = NewRuntime(new MarkdownBlockView { Document = doc }, theme, w, scale);
    var px = Snap(ctx, $"{tag}-top");

    var rich = FindRich(ctx.Rt, "alpha");
    T.Check($"{tag}-rich-alpha", rich != null);
    if (rich == null) continue;
    var effAccent = rich.Environment.ResolvedTheme.Accent;
    T.Check($"{tag}-eff-accent", effAccent == theme.Accent, $"eff={effAccent} theme={theme.Accent}");

    var rects = TokenRects(ctx, rich, "alpha");
    T.Check($"{tag}-alpha-rects", rects.Count >= 1, $"n={rects.Count}");
    for (var i = 0; i < rects.Count; i++)
    {
        var acc = MatchPixels(px, rects[i], scale, effAccent, 48);
        var fg = MatchPixels(px, rects[i], scale, theme.Foreground, 24);
        T.Check($"{tag}-alpha-ink-{i}", acc > fg,
            $"accent={acc} fg={fg} rect={rects[i]}");
    }
    if (rects.Count > 0)
    {
        var (rounded, corner, inside) = CornerProbe(px, rects[0], scale);
        T.Check($"{tag}-alpha-bg-rounded", rounded, $"corner={corner} inside={inside} rect={rects[0]}");
    }

    // second code same paragraph accent too; plain word between stays fg & square.
    var beta = TokenRects(ctx, rich, "beta");
    if (beta.Count > 0)
        T.Check($"{tag}-beta-ink", MatchPixels(px, beta[0], scale, effAccent, 48) > 0,
            $"n={MatchPixels(px, beta[0], scale, effAccent, 48)}");
    var mid = TokenRects(ctx, rich, "mid");
    if (mid.Count > 0)
    {
        var ink = InkPixels(px, mid[0], scale, theme.Background);
        var accMid = MatchPixels(px, mid[0], scale, effAccent, 30);
        T.Check($"{tag}-mid-not-accent", ink > 0 && accMid <= ink / 2,
            $"ink={ink} accent={accMid} rect={mid[0]}");
        var (midRound, mc, mi) = CornerProbe(px, mid[0], scale);
        T.Check($"{tag}-mid-square", !midRound, $"corner={mc} inside={mi}");
    }
    else
    {
        T.Check($"{tag}-mid-rect", false, "no rect for token 'mid'");
    }
    Console.WriteLine($"=== {tag} done ===");
}

// ---------- Part B: wrapped code -> per-line fragments ----------
{
    var tag = "B-wrap";
    var ctx = NewRuntime(new MarkdownBlockView { Document = doc }, Theme.Light, 320, 1.5);
    var px = Snap(ctx, tag);
    var rich = FindRich(ctx.Rt, "WWWW");
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var token = "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW";
        var rects = TokenRects(ctx, rich, token);
        T.Check($"{tag}-fragments>=2", rects.Count >= 2, $"n={rects.Count}");
        var lineYs = rects.Select(r => Math.Round(r.Y, 1)).Distinct().Count();
        T.Check($"{tag}-multiline", lineYs >= 2, $"lines={lineYs}");
        var shown = 0;
        foreach (var r in rects)
        {
            if (shown++ >= 3) break;
            var (rounded, c, ins) = CornerProbe(px, r, 1.5);
            T.Check($"{tag}-frag-rounded", rounded, $"corner={c} inside={ins} rect={r}");
        }
        // Correct geometric invariant for "single continuous bg per line":
        // the fragment band must contain ZERO page-colored pixels in its
        // interior (inset by ~radius for the rounded caps) — bg fills the
        // whole band, glyphs sit on top. bgTarget = modal color of the band.
        // Two fragments on different lines must not vertically overlap, and
        // the band must not extend past the fragment edge into plain text.
        var bgTarget = ModalColor(px, rects[0], 1.5);
        // (BgTarget used only for the band-fill and tail checks below.)
        for (var li = 0; li < rects.Count; li++)
        {
            var r = rects[li];
            var interior = new Rect(r.X + 6, r.Y + 2, Math.Max(2, r.Width - 12), r.Height - 4);
            var pageInBand = MatchPixels(px, interior, 1.5, Theme.Light.Background, 12);
            var bgInBand = MatchPixels(px, interior, 1.5, bgTarget, 16);
            T.Check($"{tag}-line{li}-band-filled",
                pageInBand == 0 && bgInBand > 0,
                $"page={pageInBand} bg={bgInBand} bgTarget={bgTarget} interior={interior}");
        }
        for (var li = 1; li < rects.Count; li++)
        {
            var gap = rects[li].Y - rects[li - 1].Bottom;
            T.Check($"{tag}-lines-no-overlap-{li}", gap > -0.5,
                $"gap={gap:F2} prev={rects[li - 1]} cur={rects[li]}");
        }

        // Tail after the last fragment: must be page color, NOT the code bg
        // blend (tight tol 8 — page #F5F6F8 vs bg #E3E3E5 differ by ~18-21).
        var last = rects[^1];
        var tail = new Rect(last.Right + 2, last.Y + 2, 12, Math.Max(4, last.Height - 4));
        // <=1 bg-tol px tolerated: trailing-text AA edge can land in the bg hue.
        var pagePx = MatchPixels(px, tail, 1.5, Theme.Light.Background, 8);
        var bgPx = MatchPixels(px, tail, 1.5, bgTarget, 8);
        T.Check($"{tag}-no-bg-bleed-tail", bgPx <= 1 && pagePx > 0,
            $"bg={bgPx} page={pagePx} bgTarget={bgTarget} tail={tail} last={last}");
    }
}

// ---------- Part C: CodeBackground=null -> no bg, accent ink stays ----------
{
    var tag = "C-nobg";
    var style = new MarkdownStyleOptions { CodeBackground = null };
    var ctx = NewRuntime(new MarkdownBlockView { Document = doc, Style = style }, Theme.Light, 320, 1.5);
    var px = Snap(ctx, tag);
    var rich = FindRich(ctx.Rt, "alpha");
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var rects = TokenRects(ctx, rich, "alpha");
        T.Check($"{tag}-rects", rects.Count > 0);
        if (rects.Count > 0)
        {
            T.Check($"{tag}-accent-ink",
                MatchPixels(px, rects[0], 1.5, Theme.Light.Accent, 48) > 0);
            // Precise: ZERO bg-blend pixels inside the code run (CodeBg
            // (128,128,128,40) over page #F5F6F8 -> ~#E3E3E5), and the strip
            // just left of the run must be page color.
            var bgBlend = new Color(227, 227, 229, 255);
            var bgPx = MatchPixels(px, rects[0], 1.5, bgBlend, 10);
            var leftEdge = new Rect(rects[0].X - 3, rects[0].Y + 2, 2, Math.Max(2, rects[0].Height - 4));
            var pagePx = MatchPixels(px, leftEdge, 1.5, Theme.Light.Background, 12);
            T.Check($"{tag}-no-bg-rect", bgPx == 0 && pagePx > 0,
                $"bgPx={bgPx} pageEdge={pagePx} leftEdge={leftEdge} rect={rects[0]}");
        }
    }
}

// ---------- Part D: same-runtime theme swap Light -> Dark -> custom ----------
{
    var tag = "D-theme-swap";
    var ctx = NewRuntime(new MarkdownBlockView { Document = doc }, Theme.Light, 640, 1.5);
    var rich = FindRich(ctx.Rt, "alpha");
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var rects = TokenRects(ctx, rich, "alpha");
        T.Check($"{tag}-rects", rects.Count > 0);
        if (rects.Count > 0)
        {
            var pxL = Snap(ctx, $"{tag}-light");
            T.Check($"{tag}-light-ink",
                MatchPixels(pxL, rects[0], 1.5, Theme.Light.Accent, 48) > 0);
            // Poison case FIRST (directly adjacent to initial Light):
            // Light -> Light-with-different-Accent keeps ResolvedForeground
            // IDENTICAL, so a paint cache keyed only on paragraphForeground
            // would keep stale light-accent ink. Must be adjacent — running it
            // after a Dark hop would change foreground too and prove nothing.
            var sameFg = Theme.Light with { Accent = Color.Parse("#FF6D00") };
            ctx.Rt.SetEnvironment(new EnvironmentValues { Theme = sameFg });
            ctx.Rt.ProcessPendingWork();
            var pxS = Snap(ctx, $"{tag}-samefg-accent");
            var newAcc = MatchPixels(pxS, rects[0], 1.5, sameFg.Accent, 48);
            var staleAcc = MatchPixels(pxS, rects[0], 1.5, Theme.Light.Accent, 24);
            T.Check($"{tag}-samefg-accent-ink", newAcc > 0, $"px={newAcc} accent={sameFg.Accent}");
            T.Check($"{tag}-samefg-not-stale", newAcc >= staleAcc,
                $"new={newAcc} staleLight={staleAcc}");

            ctx.Rt.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
            ctx.Rt.ProcessPendingWork();
            var pxD = Snap(ctx, $"{tag}-dark");
            var dark = MatchPixels(pxD, rects[0], 1.5, Theme.Dark.Accent, 48);
            var stale = MatchPixels(pxD, rects[0], 1.5, Theme.Light.Accent, 24);
            T.Check($"{tag}-dark-ink", dark > 0, $"dark={dark}");
            T.Check($"{tag}-dark-not-stale", dark >= stale, $"dark={dark} staleLight={stale}");

            var custom = Theme.Dark with { Accent = Color.Parse("#E040FB") };
            ctx.Rt.SetEnvironment(new EnvironmentValues { Theme = custom });
            ctx.Rt.ProcessPendingWork();
            var pxC = Snap(ctx, $"{tag}-custom");
            T.Check($"{tag}-custom-ink",
                MatchPixels(pxC, rects[0], 1.5, custom.Accent, 48) > 0,
                $"px={MatchPixels(pxC, rects[0], 1.5, custom.Accent, 48)} accent={custom.Accent}");
        }
    }
}

// ---------- Part E: bold+link code; link still activates ----------
{
    var tag = "E-mix";
    object? activated = null;
    var view = new MarkdownBlockView { Document = doc, OnLinkActivate = p => activated = p };
    var ctx = NewRuntime(view, Theme.Light, 640, 1.5);
    var px = Snap(ctx, tag);

    var boldRich = FindRich(ctx.Rt, "boldcode");
    T.Check($"{tag}-bold-rich", boldRich != null);
    if (boldRich != null)
    {
        var r = TokenRects(ctx, boldRich, "boldcode");
        if (r.Count > 0)
            T.Check($"{tag}-bold-ink",
                MatchPixels(px, r[0], 1.5, Theme.Light.Accent, 48) > 0);
    }

    var linkRich = FindRich(ctx.Rt, "linkcode");
    T.Check($"{tag}-link-rich", linkRich != null);
    if (linkRich != null)
    {
        var r = TokenRects(ctx, linkRich, "linkcode");
        T.Check($"{tag}-link-rects", r.Count > 0);
        if (r.Count > 0)
        {
            var center = new SysPoint(r[0].X + r[0].Width / 2, r[0].Y + r[0].Height / 2);
            Click(ctx.Rt, center);
            var payload = activated as MarkdownLinkPayload;
            T.Check($"{tag}-link-activated",
                payload != null && payload.Url == linkUrl, $"activated={activated}");
        }
    }
}

// ---------- Part F: plain TextStyle.Background run stays square ----------
{
    var tag = "F-plainbg";
    var text = "plainBG";
    var styled = new StyledText($"x {text} y",
        [new TextStyleRange(2, 2 + text.Length,
            new TextStyle { Background = new Color(128, 128, 128, 160) })],
        null);
    var ctx = NewRuntime(new RichText(styled), Theme.Light, 320, 1.5);
    var px = Snap(ctx, tag);
    var rich = FindRich(ctx.Rt, text);
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var rects = TokenRects(ctx, rich, text);
        T.Check($"{tag}-rects", rects.Count > 0);
        if (rects.Count > 0)
        {
            var r = rects[0];
            // Assert the run's bg fill actually covers this rect (bg is
            // (128,128,128,160) over page -> solid gray, NOT page color), then
            // that the corner is NOT clipped (square bg).
            var interior = new Rect(r.X + 1, r.Y + 1, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));
            var grayPx = MatchPixels(px, interior, 1.5, new Color(155, 156, 158, 255), 30);
            var pagePx = MatchPixels(px, interior, 1.5, Theme.Light.Background, 12);
            T.Check($"{tag}-bg-present", grayPx > pagePx,
                $"gray={grayPx} page={pagePx} interior={interior}");
            // Square vs round: at a SQUARE corner the corner-hugging pixel is
            // bg gray; a rounded corner would clip it to page. Probe only the
            // corner px (inside may be glyph ink).
            var cx = Math.Clamp((int)Math.Round((r.X + 0.6) * 1.5), 0, px.Width - 1);
            var cy = Math.Clamp((int)Math.Round((r.Y + 0.6) * 1.5), 0, px.Height - 1);
            var cornerC = px.GetColor(cx, cy);
            var isGray = Math.Abs(cornerC.R - 155) < 40 && Math.Abs(cornerC.G - 156) < 40
                && Math.Abs(cornerC.B - 158) < 40;
            T.Check($"{tag}-square", isGray, $"corner={cornerC} at=({cx},{cy}) rect={r}");
        }
    }
}

// ---------- Part G: external .Foreground does not recolor code ----------
{
    var tag = "G-ext-fg";
    var fg = Color.Parse("#B7410E");
    var ctx = NewRuntime(
        new MarkdownBlockView { Document = doc }.Foreground(fg),
        Theme.Light, 640, 1.5);
    var px = Snap(ctx, tag);
    var rich = FindRich(ctx.Rt, "alpha");
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var code = TokenRects(ctx, rich, "alpha");
        var plain = TokenRects(ctx, rich, "mid");
        if (code.Count > 0)
        {
            var acc = MatchPixels(px, code[0], 1.5, Theme.Light.Accent, 48);
            var fgPx = MatchPixels(px, code[0], 1.5, fg, 30);
            T.Check($"{tag}-code-accent", acc > fgPx, $"acc={acc} fg={fgPx}");
        }
        if (plain.Count > 0)
        {
            // Rust-fg pixels in the plain-word rect; also assert ink exists.
            var rust = MatchPixels(px, plain[0], 1.5, fg, 40);
            var ink = InkPixels(px, plain[0], 1.5, Theme.Light.Background);
            T.Check($"{tag}-plain-follows-fg", rust > 0 && rust >= ink / 3,
                $"rust={rust} ink={ink} rect={plain[0]}");
        }
        else
        {
            T.Check($"{tag}-plain-rect", false, "no rect for 'mid'");
        }
    }
}

// ---------- Part H: fenced block unchanged; inline has no copy ----------
{
    var tag = "H-block-copy";
    var ctx = NewRuntime(new MarkdownBlockView { Document = doc }, Theme.Light, 640, 1.5);
    var px = Snap(ctx, tag);

    var scroll = ctx.Rt.Root!.Descendants()
        .Where(n => n.View is ScrollView s && s.Axis == ScrollAxis.Horizontal)
        .FirstOrDefault(n => n.Descendants()
            .Any(d => d.View is RichText r && r.Content.Text.Contains("block-lead")));
    T.Check($"{tag}-block-found", scroll != null);
    ViewNode? block = scroll;
    for (var p = scroll; p != null; p = p.Parent)
    {
        if (p.Descendants().Any(d => d.View is Button)) { block = p; break; }
        block = p;
    }
    if (block != null)
    {
        var (rounded, corner, inside) = CornerProbe(px, block.Frame, 1.5);
        T.Check($"{tag}-block-rounded", rounded, $"corner={corner} inside={inside}");
        var btn = block.Descendants().FirstOrDefault(d => d.View is Button);
        T.Check($"{tag}-btn", btn != null);
        if (btn != null)
        {
            var f = btn.Frame;
            ctx.Clip.SetText("__sentinel__"); var n0 = ctx.Clip.SetCount;
            Click(ctx.Rt, new SysPoint(f.X + f.Width / 2, f.Y + f.Height / 2));
            T.Check($"{tag}-copy-payload",
                ctx.Clip.SetCount > n0 && ctx.Clip.GetText() == "  block-lead = true;",
                $"got={ctx.Clip.GetText()}");
        }
    }

    var rich = FindRich(ctx.Rt, "alpha");
    if (rich != null)
    {
        var n0 = ctx.Clip.SetCount;
        var f = rich.Frame;
        for (double y = f.Y + 1; y < f.Bottom - 1; y += 6)
            for (double x = f.X + 1; x < f.Right - 1; x += 6)
                Click(ctx.Rt, new SysPoint(x, y));
        T.Check($"{tag}-inline-no-copy", ctx.Clip.SetCount == n0,
            $"writes={ctx.Clip.SetCount - n0}");
    }
}

// ---------- Part I: cache invariants — accent reraster, radius none ----------
// Contract: ForegroundIsAccent resolution is paint-only -> accent change must
// reraster the affected run (new ink) but must NOT reshape (same
// RichLayoutIdentity returned by the next Layout call for the same request).
// InlineCodeCornerRadius change is bg-rect-only -> must NOT reraster and must
// return the same layout identity. Layout() CALLS alone don't prove a
// reshape — a cache-hit Layout still returns a handle; we assert the returned
// handle's LayoutIdentity stays identical, and Rasterize() deltas.
{
    var tag = "I-cache";
    var host = new InlineStyleHost(doc);
    var ctx = NewRuntime(host, Theme.Light, 640, 1.5);
    var rich = FindRich(ctx.Rt, "alpha");
    T.Check($"{tag}-rich", rich != null);
    if (rich != null)
    {
        var before = ctx.Rich.LastFor(((RichText)rich.View).Content);
        T.Check($"{tag}-layout-handle", before != null);
        if (before != null)
        {
            var identity0 = before.LayoutIdentity;
            var rects = TokenRects(ctx, rich, "alpha");
            T.Check($"{tag}-rects", rects.Count > 0);
            if (rects.Count > 0)
            {
                var hostNode = ctx.Rt.Root!.Descendants()
                    .First(n => n.View is InlineStyleHost);

                // Accent-only swap: same layout identity + reraster required.
                ctx.Rich.MarkCounts();
                var custom = Theme.Light with { Accent = Color.Parse("#FF6D00") };
                ctx.Rt.SetEnvironment(new EnvironmentValues { Theme = custom });
                ctx.Rt.ProcessPendingWork();
                var pxA = Snap(ctx, $"{tag}-accent");
                var after = ctx.Rich.LastFor(((RichText)rich.View).Content);
                var d1 = ctx.Rich.DeltaSinceMark();
                T.Check($"{tag}-accent-same-layout",
                    after != null && after.LayoutIdentity.Equals(identity0),
                    $"id0={identity0} id1={after?.LayoutIdentity}");
                T.Check($"{tag}-accent-reraster", d1.rasters >= 1,
                    $"rasters={d1.rasters} layouts={d1.layouts}");
                T.Check($"{tag}-accent-ink",
                    MatchPixels(pxA, rects[0], 1.5, custom.Accent, 48) > 0,
                    $"px={MatchPixels(pxA, rects[0], 1.5, custom.Accent, 48)}");

                // Radius-only swap: same layout identity + no reraster.
                ctx.Rich.MarkCounts();
                var state = hostNode.FindState<double>("radius")!;
                state.Value = state.Value < 1.0 ? 10.0 : 0.0;
                ctx.Rt.ProcessPendingWork();
                var pxR = Snap(ctx, $"{tag}-radius");
                var afterR = ctx.Rich.LastFor(((RichText)rich.View).Content);
                var d2 = ctx.Rich.DeltaSinceMark();
                T.Check($"{tag}-radius-same-layout",
                    afterR != null && afterR.LayoutIdentity.Equals(identity0),
                    $"id0={identity0} idR={afterR?.LayoutIdentity} layouts={d2.layouts}");
                T.Check($"{tag}-radius-no-reraster", d2.rasters == 0,
                    $"rasters={d2.rasters}");
            }
        }
    }
}


Console.WriteLine($"=== RESULT pass={T.Pass} fail={T.Fail} ===");
Environment.Exit(T.Fail == 0 ? 0 : 1);

// ---------- helpers ----------
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

static class T
{
    public static int Pass, Fail;
    public static void Check(string n, bool ok, string note = "")
    {
        if (ok) { Pass++; Console.WriteLine($"PASS {n} {note}"); }
        else { Fail++; Console.WriteLine($"FAIL {n} {note}"); }
    }
}

// Recording IRichTextService decorator: forwards everything to the real
// SkiaTextService and remembers the latest layout handle per StyledText — the
// exact geometry the committed view painted with (same RichLayoutCore).
sealed class RecordingRich : IRichTextService
{
    private readonly IRichTextService _inner;
    private readonly List<(StyledText Text, RichTextLayoutResult Layout)> _last = [];

    public RecordingRich(IRichTextService inner) => _inner = inner;

    public int CachedLayoutCount => _inner.CachedLayoutCount;
    public int CachedRasterCount => _inner.CachedRasterCount;
    public int LayoutCacheCapacity { get => _inner.LayoutCacheCapacity; set => _inner.LayoutCacheCapacity = value; }
    public void ClearCaches() => _inner.ClearCaches();
    public Size Measure(RichTextLayoutRequest request) => _inner.Measure(request);
    public int LayoutCalls;
    public int RasterCalls;
    private int _layoutMark, _rasterMark;
    public void MarkCounts() { _layoutMark = LayoutCalls; _rasterMark = RasterCalls; }
    public (int layouts, int rasters) DeltaSinceMark() => (LayoutCalls - _layoutMark, RasterCalls - _rasterMark);

    public PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint)
    {
        RasterCalls++;
        return _inner.Rasterize(layout, paint);
    }

    public RichTextLayoutResult Layout(RichTextLayoutRequest request)
    {
        LayoutCalls++;
        var handle = _inner.Layout(request);
        lock (_last) { _last.Add((request.Text, handle)); }
        return handle;
    }

    public RichTextLayoutResult? LastFor(StyledText text)
    {
        lock (_last)
        {
            for (var i = _last.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_last[i].Text, text) || _last[i].Text.Text == text.Text)
                    return _last[i].Layout;
        }
        return null;
    }
}
sealed record Ctx(UiRuntime Rt, GlRenderer Renderer, RecordingRich Rich, StubClipboardService Clip);
sealed class InlineStyleHost : Component
{
    private readonly MarkdownDocument _doc;
    public InlineStyleHost(MarkdownDocument doc) => _doc = doc;
    protected override View Build()
    {
        var radius = UseState("radius", 0.0);
        var style = new MarkdownStyleOptions { InlineCodeCornerRadius = radius.Value };
        return new MarkdownBlockView { Document = _doc, Style = style };
    }
}
