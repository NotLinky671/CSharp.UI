# Inline-code style — independent GL acceptance (run5, authoritative)

Baseline `8e0b002`, product frozen at DCEA (test-only changes after freeze do
not affect product dlls; GL used the same Debug-built product assemblies).
Out-of-repo harness `Temp/inline-code-smoke` — snapshot in `harness/` —
real `GlOffscreenSurface` + `SkiaTextService` + `GlRenderer` + real
`MarkdownBlockView`. **87/87 checks PASS** (`run5-offscreen.stdout.txt`,
exit `code=0`).

## Verified (run5)

- **Accent ink**: pixel count matching `node.Environment.ResolvedTheme.Accent`
  inside each code run's real `RichTextLayoutResult` segment rect exceeds
  foreground count — light-320×1.5, dark-1024×2.0, light-1024×1.5,
  dark-320×2.0, and `Theme.Light with { Accent = #00C853 }` (custom accent
  proves live-theme consumption, not hardcoded blue).
- **Rounded bg**: corner-hugging pixel of each fragment band = page color;
  band interior = code bg blend (`#E3E3E5` light). Bands filled edge-to-edge.
- **Cross-line geometry**: 48-char code token wraps to 2 fragments; each
  line's band fully bg-filled (zero page px inside), no vertical overlap
  between fragments, 12-px strip after last fragment = page (≤1 AA-edge px
  tolerated — trailing text anti-alias, not bg bleed).
- **`CodeBackground=null`**: zero bg-blend px inside the run, page color at
  the run's left edge, accent ink still present.
- **Same-foreground accent swap (poison)**: `SetEnvironment`
  Light → `Theme.Light with { Accent = #FF6D00 }` (identical
  `ResolvedForeground`), directly adjacent — new accent ink present,
  0 stale light-accent px. Then Dark and custom `#E040FB` swaps follow.
- **Bold/link code**: `**boldcode**` accent ink; real pointer click on
  `linkcode` dispatches `OnLinkActivate` with `MarkdownLinkPayload{Url}`.
- **Plain `TextStyle.Background` stays square** (raw `RichText`, bg
  `(128,128,128,160)`): interior 1540 gray px / 0 page px; corner-hugging
  pixel = gray `#ABACAC` (unclipped → square).
- **External `.Foreground(#B7410E)`**: plain word shows rust ink (81 px),
  code keeps accent (225 px accent, 0 rust).
- **Block regression**: fenced block rounded wrapper + copy writes exact
  `  block-lead = true;` to stub; inline paragraph click-sweep writes 0.
- **Cache invariants** (`RecordingRich`, public `IRichTextService`
  decorator over SkiaTextService): accent-only swap keeps
  `RichLayoutIdentity(57)` (no reshape) + rasters=5 (re-ink);
  `InlineCodeCornerRadius` 0→10 keeps identity(57) + rasters=0
  (bg RectangleCommand only — radius not in the glyph raster key).

## Probe bug fixed between run4 and run5

`TokenRects` previously used `frame.Y + line.Bounds.Y` for segment rects,
missing the vertical text-block origin (`frame.Y + (frame.Height −
layout.Height)/2`, pixel-snapped — same math `EmitRich` uses via
`TextPainter.ResolveOrigin`). In tightly-fitted paragraph frames the offset
was ~0 so Parts A–I results were unaffected; the bare-`RichText` Part F root
(700-tall viewport) was off by ~340 px and its run4 "square/bg-present"
checks probed empty page — invalid, not a product defect. run5 reproduces
the origin math exactly and F now asserts against the real rect
(`y≈340.7`, matching the run4 PNG where the gray band visibly sits mid-view).

## Boundaries

- OS clipboard untouched; copy verified only through injected
  `IClipboardService` stub (same contract the host wires).
- No product/source changes made; findings are harness-probe fixes only.
- run1–run4 raw kept for the probe-fix audit trail; run5 is authoritative.

## Suites (Release, frozen product)

- `dotnet build CSharp.UI.slnx -c Release` — 0 warnings, 0 errors
  (`run5-release-build.stdout.txt`, exit `code=0`).
- Real process exits (`run5-tests-*.exit.txt`, all `code=0`):
  CSharp.UI.Tests 707/707, Text 109/109, Markdown 50/50, Integration 53/53.

## Artifacts

`run5-*` pngs + `run5-{build,offscreen}.{stdout,stderr,exit}.txt` +
`run5-dlls.txt` (SHA256 of the 4 product assemblies) + `run5-tests-*` +
`run5-release-build.*` + `harness/` (Program.cs, csproj, runner script).
`.gitattributes` marks `* -text` so raw logs keep original bytes.
