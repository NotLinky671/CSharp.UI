# Markdown Code-Block Acceptance Evidence — 2026-10-26

## Result

**Final run (run2): 40 PASS / 0 FAIL**, harness process `exit=0`. Feature accepted at runtime level: rounded code-block background + optional top-right "Copy" button writing exact code text to the runtime `IClipboardService`.

## Verified behavior (run2-offscreen.stdout.txt)

- `CodeCornerRadius=8` default, `ShowCodeCopyButton=true` default, `CodeCopyLabel="Copy"` on `MarkdownStyleOptions`.
- Rounded background confirmed by **pixel sampling**: corner-hugging pixel (0.6 logical px inside the block frame) shows the page color (clipped), a point 16 logical px in shows the code background — light `#F5F6F8` vs `#E3E3E5`, dark `#15171C` vs `#26272C`; holds at 1.0×, 1.5×, 2.0× scale. `CodeCornerRadius=0` produces identical corner/inside pixels (square — config is genuinely consumed), `r=20` visibly rounded.
- Representative configs (not the full 12-cell matrix): light-320×1.0, dark-1024×1.0, dark-320×1.5, light-1024×2.0, plus light-320×1.0 with r=0 / r=20.
- Real pointer clicks (Move/Down/Up dispatch) on the copy button write to `StubClipboardService` the exact code text `  indented-lead = true;\n\nvar unicode = "héllo→世界";` — leading indent, blank line, Unicode preserved; **no language tag (`csharp`), no closing fence, no trailing newline** (hand-written expectation; `MarkdigLines` output confirmed to exclude the closing-fence line terminator).
- Dynamic `ShowCodeCopyButton` toggle via `UseState` on the same live runtime/doc: ON writes → OFF writes nothing at the same coords → re-ON writes again.
- Wide line (220 chars, `contentW=2001.56` vs `vpW=320`) stays single-line NoWrap, wheel DeltaX scrolls content `x 8 → -352`, and the button remains inside the block viewport and clickable afterwards (copied text contains `tail-marker`).
- Empty fenced block ```` ``` ```` copies `''` (F). Inline `` `code` `` produces no button and no writes over a full-viewport click sweep (E). `CodeBackground=null` suppresses the background rect while the button still copies (G).

## OS clipboard — NOT verified this run (policy skip)

`EnumClipboardFormats` returned `[49161, 2, 49484, 49756, 49753, 49171, 8, 17]` — includes `CF_BITMAP` (2) and custom/non-text formats. Per the user-data-protection gate, the harness **skipped all OS clipboard reads/writes** (no overwrite risk, nothing to restore). Clipboard behavior is verified only at the framework `IClipboardService` boundary via `StubClipboardService`; the real OS clipboard roundtrip remains unverified.

## Commands and exits (run2)

- `dotnet build mdcblock.csproj -c Release` (harness) → `run2-build.exit.txt` = `code=0`
- `MdCodeblockOffscreen.exe` (Release, offscreen GL harness) → `run2-offscreen.exit.txt` = `code=0`, stdout = 40 PASS / 0 FAIL
- `dotnet build CSharp.UI.slnx -c Release` (repo solution) → `run2-sln-release-build.exit.txt` = `code=0`, 0 warnings 0 errors
- Test suites run as the test-host exes directly (not `dotnet test` — the Microsoft.Testing.Platform named-pipe IPC to the MSBuild node fails in this environment with `UnauthorizedAccessException`; infrastructure limitation, not a product failure):
  - `tests/CSharp.UI.Markdown.Tests/bin/Release/net10.0/CSharp.UI.Markdown.Tests.exe` → `run2-test-Markdown.exit.txt` = `code=0`, **50/50**
  - `tests/CSharp.UI.Tests/bin/Release/net10.0/CSharp.UI.Tests.exe` → `run2-test-Tests.exit.txt` = `code=0`, **698/698**
  - `tests/CSharp.UI.Integration.Tests/bin/Release/net10.0/CSharp.UI.Integration.Tests.exe` → `run2-test-Integration.exit.txt` = `code=0`, **53/53**
  - The earlier `dotnet test` attempts failed identically (`code=1`, MTP named-pipe → MSBuild-node `UnauthorizedAccessException`); only the Core-suite attempt survives on disk as `run2-test-Core.*` — the Markdown and Integration failed-attempt files were overwritten by the direct-exe runs sharing their names.

## Fixture

Markdown doc (harness Program.cs): a `csharp`-tagged fenced block containing two leading-space lines, a blank line, and `var unicode = "héllo→世界";`; an indented code block; an empty fenced block; a 220-char fenced line; plus inline code and a paragraph. GL offscreen host: `GlOffscreenSurface` + `UiRuntime` + `MarkdownBlockView`, SkiaSharp text, GL 3.3.0 / Intel RaptorLake-S.

## Assemblies under test

`run2-dlls.txt` lists SHA-256 of the staged `Temp\md-codeblock\lib` (sample ControlGallery bin overlaid with the four src Debug assemblies); `run2-offscreen.stdout.txt` `ASM` lines record actually-loaded locations + SHA-256 prefixes (CSharp.UI `7E066D10…`, Markdown `D9B0265F…`, OpenTK `C3A55345…`, Text `038A44F5…` loaded from the harness `bin/Release/net10.0/win-x64`).

## Harness source snapshots (for independent reproduction)

`Program.cs`, `mdcblock.csproj`, `run-mdcblock.ps1` are the final external-harness sources copied verbatim from `%TEMP%\md-codeblock`. The script stages `lib` from `samples\CSharp.UI.Samples.ControlGallery\bin\Debug\net10.0` overlaid with src Debug assemblies, builds the app, and captures stdout/stderr/exit via `System.Diagnostics.Process` UTF-8 async readers. Some intermediate manual `dotnet build` probes during debugging were not captured into this directory; only the recorded `runN-*` files are complete raw artifacts.

## run1 — failed diagnostic run (kept, not overwritten)

`run1-*` preserves the first attempt: harness compile fixes were still in flight and the offscreen run exited `code=1` with 34 PASS / 7 FAIL. All 7 were harness defects, fixed before run2 (no product changes): unterminated `"""` raw string + `ToggleHost` ordering/`Program.showProp` reference (compile), missing `unsafe` on the GLFW clipboard wrapper (compile), probe depth 2.4 px landing inside the radius-8 arc at 2× scale (false negative on `rounded-bg`), expected copy text wrongly including a trailing `\n` (4× `btn-writes`), stale `ViewNode` after UseState re-enable (`B-rewrite-on`), and the OS-clipboard safety gate counted as FAIL when a skip is the required outcome. The runner script itself was fixed too: its `Invoke-Captured` declared a parameter named `$args` which collides with the automatic `$args` variable (always empty → `dotnet` invoked with no arguments, exit `-2147450751`), and PS 5.1 `ProcessStartInfo` lacks `ArgumentList`.
