# Markdown spacing verification evidence — 2026-10-26

Independent acceptance of the new `MarkdownStyleOptions` spacing fields
(`BlockSpacing=12`, `HeadingSpacingBefore=20`, `HeadingSpacingAfter=8`,
`ListItemSpacing=4`, `CellVerticalPadding=6`) implemented via `AdaptSequence` +
`Spacer` between blocks. Harness: out-of-repo `Temp/md-spacing` —
real `GlOffscreenSurface` + `UiRuntime` + real `MarkdownDemo`/`MarkdownBlockView`,
`SkiaTextService`/`SkiaImageService`/`GlTextureService`/`GlRenderer`,
staging `lib/` = full sample bin copy with the 4 src-project Debug DLLs
overwritten by the freshly built product. Repo `bin/` never written.

## Commands actually executed

| command | exit | raw |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental -m:1 -nodeReuse:false` | 0 (tool result) | `slnx-build-Debug.stdout.txt` (verbatim tool output copy; stderr empty) |
| `dotnet build CSharp.UI.slnx -c Release --no-incremental -m:1 -nodeReuse:false` | 0 (tool result) | `slnx-build-Release.stdout.txt` (verbatim tool output copy; stderr empty) |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug --no-build` | 0 (tool result) | `markdown-tests-Debug.stdout.txt` — 49/0 fail |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Release --no-build` | 0 (tool result) | `markdown-tests-Release.stdout.txt` — 49/0 fail |
| `MdSpacingOffscreen.exe` (win-x64, natives dir on PATH, MDSPACE_EV/MDSPACE_RUN=direct1, MDSPACE_STYLE=`BlockSpacing=24;HeadingSpacingBefore=36;HeadingSpacingAfter=16;ListItemSpacing=10;CellVerticalPadding=12`) | 未独立记录；完成并输出 `76 PASS / 0 FAIL` | `direct1-full.txt` (original UTF-16LE-BOM capture, 120.8 KB) + `direct1-full.utf8.txt` (faithful transcoded readable copy — same content, no filtering) |

No `direct1.exit.txt` exists: the run was invoked through a PowerShell
`*>` redirect and `$LASTEXITCODE` printed empty in the captured tail —
因此不声明 direct1 进程退出码；程序逻辑 `return T.Fail == 0 ? 0 : 1;` 仅为源码事实。
run5 的 `run5-offscreen.exit.txt`（`code=0`）则是 Start-Process 独立写出的真实返回码。

## Authoritative artifacts

- Geometry/PASS-FAIL truth: **`direct1-full.utf8.txt`** (readable copy of
  `direct1-full.txt`). Contains per-block frame/body/chrome rectangles,
  spacer heights, `gap[i->j] frame/body/ink`, table row bands, link echo,
  style-swap and resize results.
- Visual truth reviewed by main: `run5-A-light-1024x1-top.png`,
  `run5-A-dark-1024x1-top.png`, `run5-A-light-320x1-table.png` (36 PNGs total
  under `run5-A-*`); fixture renders `direct1-B-{default,custom}-400x{1,2}.png`;
  resize `direct1-D-swap-resize-{320,1024}.png`. PNG prefix `run5` vs
  `direct1` only reflects which invocation produced them; both used identical
  DLLs (see `run5-dlls.txt` + ASM sha256 lines in `direct1-full.utf8.txt`).

## Known collector issues (honest record)

- **run1–run4** exit `-532462766`, stderr `Unable to load DLL 'glfw'` —
  harness staging defect (loose-copied natives not probed), NOT product
  failures. Fixed via `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` +
  natives dir on `PATH`.
- **run5 offscreen stdout/stderr never written** — `Start-Process
  -RedirectStandardOutput` produced no file on the successful runs in this
  environment (crashed runs 1–4 did write them). PNGs + exit file exist;
  `MANIFEST.txt` records `offscreen exit=0`. The run-id allocator keyed on the
  missing stdout filename, so run5 was reused — run5 PNGs were overwritten by
  the two successful run5 invocations before this was noticed.
  `direct1-full.txt` is the complete console record of a successful run.
- `smoke-A-light-320x1-top.png` is a one-config early smoke screenshot;
  left in place as raw evidence (not deleted).
- No claim of "all raw complete" — see caveats above.

## What was verified

- 12-matrix real `MarkdownDemo` (light|dark × ps 1|1.5|2 × w 320|1024):
  doc scroll located, `minBodyGap=8` (HeadingSpacingAfter) between adjacent
  block bodies in all configs; spacer heights logged per doc
  (`8,12,20,8,12,12,12,12` on fixture); doc stack width 308 ≤ 320 viewport
  (table hscroll still inside its own ScrollView); github link hit-test +
  click → echo at all 12 configs; table node reached at doc bottom with
  separator bands and row heights logged (e.g. 67.86/49.24 at 320 wrap,
  30.62 uniform at 1024).
- Fixture doc (h→p→p→h, tight/nested/loose list, quote w/ 2 paras + nested
  code, fenced code, hr, 2-row table w/ multiline cell) at w=400 ps=1|2:
  default style minBodyGap=8, custom style minBodyGap=16, table rowBand[0]
  49.24→61.24 under CellVerticalPadding 6→12, table width unchanged.
- State swap: `UseState<MarkdownStyleOptions>` mutated → real relayout,
  minBodyGap 8→16 (spacing fields covered by layout invalidation).
- Viewport 320→1024→320 round-trip: block count stable at 9.
- **Not measured**: table cell height under same-doc state swap (swap test
  used spacing fields; CellVerticalPadding was in the swap spec but the
  assertion only compared minBodyGap — do not over-claim).

## Provenance

`run5-dlls.txt` (staged lib sha256 manifest) + `ASM` lines inside
`direct1-full.utf8.txt`: CSharp.UI `9EAFF221FCBB6BE8`, CSharp.UI.Markdown
`3CE8C91621D82AC8`, CSharp.UI.OpenTK `EDD408CE589BE69C`, CSharp.UI.Text
`C15D013D51C7FB5B` — all loaded from the external stage dir, matching the
freshly built `src/*/bin/Debug` product.
