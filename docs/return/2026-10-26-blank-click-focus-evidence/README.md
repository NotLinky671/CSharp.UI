# Blank-click focus-clear acceptance evidence — 2026-10-26

User-authorized contract: a primary-left `PointerDown` that bubbles **unconsumed**
with **no focusable ancestor** in the hit chain clears keyboard focus
(`SetFocus(null)` → existing blur path: `ReleaseTextInputOwnershipFor` →
`EndSession` + composition cancel → `OnFocusChanged(false)` → caret/ring gone).
Consumed clicks (controls, scrollbar track/thumb, link hits) and non-left
buttons never clear. Implementation: `InputRouter.DispatchPointer` Down tail
`!context.Handled && e.Button == Left && !ChainHasFocusable(_hovered)`.

## Runner (what produced the run6 evidence)

- `harness/` — text-only snapshot of the external harness for reproduction:
  `Program.cs` (the checks), `blankfocus.csproj`, `run-blankfocus.ps1`
  (stages `samples/CSharp.UI.Samples.ControlGallery/bin/Debug` + fresh
  `src/*/bin/Debug` assemblies into `%TEMP%/blank-focus-smoke/lib`, SHA256
  manifest, reliable `Process` stdout/stderr UTF-8 capture, auto-incrementing
  `runN` prefixes — never overwrites older runs). No binaries/natives copied.
- Live source was `C:\Users\nothh\AppData\Local\Temp\blank-focus-smoke\`
  (public API only; assembly `BlankFocusOffscreen` has no InternalsVisibleTo).

## Final run — `run6` (GL offscreen, exit code 0)

`BlankFocusOffscreen.exe`, GL context `3.3.0 - Build 32.0.101.6790 / Intel(R)
RaptorLake-S Mobile Graphics Controller`, real `ControlGallery` fixture via
headless `UiRuntime` + `SkiaTextService` + `GlOffscreenSurface`, 1024×768 @1.0.
Result: **62 PASS / 0 FAIL** (`run6-offscreen.stdout.txt`,
`run6-offscreen.exit.txt` = `code=0`).

Loaded assemblies actually exercised (from `run6-offscreen.stdout.txt` /
`run6-dlls.txt`):

| Assembly | SHA-256 (prefix) |
|---|---|
| `CSharp.UI.dll` | `8334219979B3B933` |
| `CSharp.UI.OpenTK.dll` | `C9C3FF1F81695261` |
| `CSharp.UI.Text.dll` | `ACE0330419F3C187` |

`run6-dlls.txt` additionally lists full SHA-256 for all staged DLLs
(`ControlGallery.dll ac43344d…`, OpenTK/SkiaSharp/Markdig/HarfBuzz deps).

### What run6 proves

- **Click Button → focus ring draws; click blank → ring gone, `FocusedNode` null.**
  Light: ring `#2563EB` px 291→0 in the button's frame. Dark: gallery's own
  theme-toggle button clicked first (`dark-really-dark resolved=True`), ring
  `#4C8DFF` px 292→0. Pixel target = `node.Environment.ResolvedTheme.FocusRing`
  inside the node's frame (tol 16) — not a global blue-pixel heuristic.
  Screenshots: `run6-light-1-btn-ring.png`, `run6-light-2-blank-noring.png`,
  `run6-dark-1-btn-ring.png`, `run6-dark-2-blank-noring.png`.
- **TextField**: focus → session started → typed text → blank click →
  `FocusedNode` null + `EndSession` called (caret shots
  `run6-*-3-tf-caret.png` / `run6-*-4-tf-nocaret.png`).
- **TextField → Button transfer** fires blur exactly once (EndSession delta = 1).
- **Wheel and Move never clear focus.**
- **Scrollbar** track click / thumb press + drag + release never clear focus
  (consumed `Down`, focus stays on the Button).
- **Right-click on blank does not clear** (primary-button-only rule).
- **Plain `Text` click clears** (unconsumed, no focusable ancestor).
- **Nested fixture**: `.Padding(8).Background()`-wrapped `Button` — clicking the
  button transfers focus; clicking the 4-logical-px padding gap clears
  (`run6-nested-gap.png`).
- **Composition blur (runtime-session level)**: injected `ITextInputService`
  delivers `CompositionEvent.Start` → `IsComposing`; blank click cancels the
  composition and ends the session. This is the real runtime/session code path
  the host wires — **not** an OS-level IME test; no real IME was engaged and
  no system IME/clipboard/keyboard state was touched.

### Boundaries

- Focusable-parent-containing-unhandled-child is not reproducible from public
  API (all focusable views are leaves; `View.Children`/`OnPointerDown` are
  internal) — covered by the new core unit test with an internal fixture
  (`FocusablePassThrough` + `Text`).
- Dark pass required clicking the gallery's own theme toggle: `GalleryApp`
  owns theme via `UseState`; the runtime root environment does not override it.

## Test suites (Release, actual exit codes in `*.exit.txt`)

| Step | Command | Result |
|---|---|---|
| Solution build | `dotnet build CSharp.UI.slnx -c Release` | exit 0, 0 warnings, 0 errors |
| Core | `dotnet run --project tests/CSharp.UI.Tests -c Release` | 704 total / 0 failed / 0 skipped, exit 0 |
| Integration | `dotnet run --project tests/CSharp.UI.Integration.Tests -c Release` | 53/0, exit 0 |
| Markdown | `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Release` | 50/0, exit 0 |

(Text/OpenTK suites not run for this change — focus/input routing lives in
Core + Integration; nothing in those packages was touched.)

## Earlier harness iterations (raw preserved, not overwritten)

`run1`–`run5` keep their original stdout/stderr/exit/PNG: they are harness
debugging artifacts, not product failures. Highlights:

- `run1` (first): build error then exit `-532462766` — harness crashed after
  scrollbar interactions because stale scroll geometry made later clicks land
  on blank space (correct product behavior) and the composition section hit a
  null active client.
- `run2`: 57/3 — composition `Start` rejected on a stale `documentVersion`;
  scrollbar-section scroll restore was missing mid-section.
- `run3`: 59/1 — "dark" run was actually rendered in light theme (the gallery
  ignores the root environment theme; the toggle click was added in run6).
  Earlier "dark" PNGs are mislabeled-light and kept for the record.
- `run4`/`run5`: same dark-theme mislabel with added diagnostics confirming
  `FocusedNode` was correct throughout.
