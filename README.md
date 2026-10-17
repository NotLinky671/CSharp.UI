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
| `docs/task` | Current IME and text-editing tasks; completed task sets live in `docs/archive/` |

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
- [Text input and editing plan](docs/plan/2026-10-17-text-input-and-editing-plan.md) (planned; not implemented)
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
`Button`/`Toggle`/`Slider`/`TextField`, `Padding`/`Frame`/`Background`/`Foreground`/`Border`/`CornerRadius`/`Opacity`/`Clip`/`Disabled`/`Key`,
`ForEach`, light and dark themes, font/color environment inheritance, mouse/keyboard/wheel/focus/pointer capture,
and Chinese text display with committed text input.

Not included: full IME composition text and candidate windows, multi-line editing, an animation system, list
virtualization, full bidirectional text, color emoji glyph atlases, mobile/browser/host embedding, and system
accessibility bridges.

## Package status

The framework packages stay **pre-release** (`0.1.0-preview.1`); this repository does not publish to NuGet automatically.

## License and assets

The code is released under the MIT license. The repository does not redistribute third-party font binaries; the
sample images are drawn and generated by `tools/CSharp.UI.AssetGenerator`. See
[platform, fonts, and native dependencies](docs/guide/platform-notes.md) for details.
