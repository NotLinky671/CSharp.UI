# CSharp.UI

**English** | [简体中文](README.zh-CN.md)

A cross-platform declarative desktop UI framework for C#, built on **OpenTK + OpenGL**: immutable view descriptions plus persistent runtime nodes, with state-driven updates, component composition, and chainable modifiers.

- Target framework: **.NET 10 / C# 14** (`net10.0`)
- Rendering baseline: **OpenGL 3.3 Core / GLSL 330** — every final draw goes through OpenTK/OpenGL
- Text: **SkiaSharp 3.119.4 + HarfBuzz** handle CPU shaping and rasterization; OpenGL uploads the textures
- Platforms: Windows / macOS / Linux with a unified custom-drawn appearance; single window in the first release

```csharp
public sealed class Counter : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);

        return new VStack(
            new Text($"Count: {count.Value}"),
            new Button("Increment", () => count.Value++)
        )
        .Spacing(12)
        .Padding(24);
    }
}

UiApplication.Run(new Counter());
```

## Repository layout

| Path | Contents |
|---|---|
| `src/CSharp.UI` | Core: views, components, state, reconciliation, layout, controls, and platform-agnostic draw commands (no OpenTK / SkiaSharp dependency) |
| `src/CSharp.UI.Text` | Font parsing, fallback, text shaping and rasterization, image decoding (SkiaSharp + HarfBuzz) |
| `src/CSharp.UI.OpenTK` | Desktop host: window, on-demand event loop, input adaptation, OpenGL rendering and GPU resources, service composition |
| `samples/` | Three samples — counter, todo list, and control gallery (one codebase for all three platforms); the gallery and `NestedResponsiveExample` use the generic responsive APIs and stay usable at 320/480/768/1024 logical widths |
| `tests/` | Core unit tests, text tests, graphics integration tests (xUnit v3) |
| `tools/CSharp.UI.AssetGenerator` | Generates the sample image assets so they stay reproducible |
| `docs/guide` | Getting started, API, state, modifiers, keys, components, platform, and testing guides |
| `docs/task` | Current TextEditor planning and implementation tasks; completed task sets live in `docs/archive/` |

Historical IME tools and their three test projects are preserved at tag `archive/tools-before-removal`. Restore them in a separate checkout using `git worktree add --detach ../CSharp.UI-ime-archive archive/tools-before-removal`; existing acceptance evidence remains under `docs/`.

## Quick start

```bash
dotnet build CSharp.UI.slnx
dotnet run --project samples/CSharp.UI.Samples.Counter
dotnet run --project tests/CSharp.UI.Tests
```

See the [getting started guide](docs/guide/getting-started.md) for details.

## Documentation

- [Getting started](docs/guide/getting-started.md)
- [Public API reference](docs/guide/api-reference.md)
- [Responsive layout: usage and migration](docs/guide/responsive-layout.md)
- [Text input and editing plan](docs/plan/2026-10-17-text-input-and-editing-plan.md) (TI-01–TI-10 closed; TI-07 Windows TSF production acceptance completed 2026-10-22 — see [P4 delivery](docs/delivery/2026-10-22-ti07-p4-delivery.md); TI-08 non-Windows platforms report committed-text-only capability, verified at source level and by CPU simulation without real-device IME evidence; TI-09–TI-10 closed on 2026-10-23 by user acceptance without an independent review record, with known boundaries in the [delivery record](docs/delivery/2026-10-23-ti09-ti10-delivery.md); task records archived at [docs/archive/2026-10-23-text-input-editing-tasks/](docs/archive/2026-10-23-text-input-editing-tasks/README.md))
- [Text editor plan](docs/plan/2026-10-23-text-editor-plan.md) (multi-line plain-text `TextEditor` implemented: editing core, IME hookup, samples/docs; Windows real-IME acceptance on mspinyin/IMM completed 2026-10-23 — see [delivery record](docs/delivery/2026-10-23-text-editor-delivery.md); non-Windows remains committed-text-only; no public TSF toggle)
- [State and lifecycle](docs/guide/state-lifecycle.md)
- [Modifier order](docs/guide/modifier-order.md)
- [Keys and dynamic lists](docs/guide/keys-and-lists.md)
- [Writing custom components](docs/guide/custom-components.md)
- [Platform, fonts, and native dependencies](docs/guide/platform-notes.md)
- [Testing guide](docs/guide/testing.md)
- Design and tasks: [first-release plan](docs/plan/2026-10-14-declarative-ui-framework-plan.md), [responsive-layout plan](docs/plan/2026-10-16-responsive-layout-plan.md), [current tasks](docs/task/README.md), [first-release acceptance record](docs/archive/2026-10-14-framework-tasks/README.md), [responsive-layout delivery record](docs/delivery/2026-10-16-responsive-layout-delivery.md)

## Responsive layout

`Flex`/`FillWidth` provide flexible lengths, `Flow` wraps automatically, `AdaptiveStack` switches between
horizontal and vertical arrangement while preserving child identity, and `Responsive` picks an arbitrary content
structure from the parent's final effective logical width. Every capability is based on the logical width the parent
allocates — window size, resolution, and DPI are never read:

```csharp
static View Header(State<Theme> theme, Theme current) =>
    new AdaptiveStack(
            560,
            new Text("CSharp.UI Control Gallery"),
            new Button(
                current.IsDark ? "Light theme" : "Dark theme",
                () => theme.Value = current.IsDark ? Theme.Light : Theme.Dark))
        .Spacing(12);

static View Actions(Action save, Action cancel) =>
    new Flow(new Button("Save", save), new Button("Cancel", cancel)).Spacing(8);

static View Search(State<string> query) =>
    new TextField(query.ToBinding()).FillWidth();
```

See [responsive layout: usage and migration](docs/guide/responsive-layout.md) for details.

## First-release scope

Included: `VStack`/`HStack`/`ZStack`/`Spacer`/`ScrollView`, `Text`/`Image`/`Rectangle`,
`Button`/`Toggle`/`Slider`/`TextField`/`TextEditor` (multi-line: hard newlines plus width-driven soft wrap, a fixed `MinLines` viewport with internal `ScrollY`, pointer drag-selection with edge auto-scroll, undo/redo and clipboard commands),
`Padding`/`Frame`/`Background`/`Foreground`/`Border`/`CornerRadius`/`Opacity`/`Clip`/`Disabled`/`Key`,
`ForEach`, light and dark themes, font/color environment inheritance, mouse/keyboard/wheel/focus/pointer capture,
and Chinese text display with committed text input on the default IMM path.

Text editing commands (undo/redo, cut/copy/paste, select all) are keyboard shortcuts only; there is no public command API. See the [API reference](docs/guide/api-reference.md) and the [manual IME acceptance checklist](docs/guide/ime-manual-acceptance.md).

Pre-release Windows TSF path (internal opt-in `UseTsfInputBridge`, not a public option): composition/preedit, a UI-less framework-drawn candidate panel that follows the final text geometry, and accurate return to IMM on failure. It requires an `[STAThread]` entry point. Production acceptance (P4) completed 2026-10-22 on a single-DPI (150%) mspinyin environment — see [P4 delivery record](docs/delivery/2026-10-22-ti07-p4-delivery.md); multi-DPI evidence is an environment boundary. Candidate mouse selection is not supported; keyboard candidate selection is handled by the IME itself.

Not included in the default release: real-IME composition for `TextEditor` on non-Windows platforms (Windows mspinyin/IMM real acceptance completed 2026-10-23 — see [delivery record](docs/delivery/2026-10-23-text-editor-delivery.md); macOS/Linux remain committed-text-only), an animation system, list virtualization, full
bidirectional text, color emoji glyph atlases, mobile/browser/host embedding, and system accessibility bridges.

## Package status

The framework packages stay **pre-release** (`0.1.0-preview.1`); this repository does not publish to NuGet automatically.

## License and assets

The code is released under the MIT license. The repository does not redistribute third-party font binaries; the
sample images are drawn and generated by `tools/CSharp.UI.AssetGenerator`. See
[platform, fonts, and native dependencies](docs/guide/platform-notes.md) for details.
