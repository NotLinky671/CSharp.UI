using CSharp.UI;
using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Skia;
using CSharp.UI.Testing;

// P06 OS-driver coordinate probe: real GalleryApp layout, headless (no window).
// Emits logical frames of the click targets. Viewport scale mirrors the real
// workstation (DPI 144 => 1.5); sizes here are LOGICAL units.
var logicalW = args.Length > 0 ? double.Parse(args[0]) : 900;
var logicalH = args.Length > 1 ? double.Parse(args[1]) : 700;

var runtime = new UiRuntime(new GalleryApp(), new UiRuntimeServices
{
    Dispatcher = new HeadlessUiDispatcher(),
    TextService = new SkiaTextService(),
    RichTextService = new SkiaTextService() as IRichTextService,
    TextureService = new RecordingTextureService(),
    ImageService = new StubImageService(),
    Clipboard = new StubClipboardService(),
    Clock = new TestUiClock(),
    WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
});
runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
runtime.SetViewport(new Size(logicalW, logicalH), 1.5);
runtime.Start();
runtime.ProcessPendingWork();

void Dump(ViewNode? n, string tag)
{
    if (n is null) { Console.WriteLine($"MISS {tag}"); return; }
    var f = n.Frame;
    Console.WriteLine($"NODE {tag} view={n.View.DebugName} frame={f.X:0.#},{f.Y:0.#},{f.Width:0.#}x{f.Height:0.#} center={f.X + f.Width / 2:0.#},{f.Y + f.Height / 2:0.#}");
}

var root = runtime.Root;
var scroll = root.FindDescendant<ScrollView>();
Dump(scroll, "root-scroll");
Console.WriteLine($"INFO offsetY={scroll?.ScrollOffsetY:0.#}");

var demo = root.FindDescendant<PopupSelectDemo>();
Console.WriteLine($"INFO selects={root.FindDescendants<Select<string?>>().Count()}");

void DumpAll(string stage)
{
    Console.WriteLine($"--- {stage} offsetY={scroll?.ScrollOffsetY:0.#}");
    Dump(root.FindDescendants<Button>().FirstOrDefault(n => n.View is Button b && b.Label is "暗色主题" or "明亮主题"), "theme-toggle");
    var selects = root.FindDescendants<Select<string?>>().ToArray();
    for (var i = 0; i < selects.Length; i++) Dump(selects[i], $"select-{i}");
    Dump(root.FindDescendants<Button>().FirstOrDefault(n => n.View is Button b && b.Label == "打开设置"), "settings-button");
    Dump(demo?.FindDescendant<ScrollView>(), "demo-inner-scroll");
    Dump(root.FindDescendants<Text>().FirstOrDefault(n => n.View is Text t && t.Content == "设置输入回显："), "settings-echo");
}

DumpAll("initial");

// Wheel-scroll the root ScrollView until the demo section is comfortably visible.
if (scroll is not null && demo is not null)
{
    for (var step = 0; step < 40 && (demo.Frame.Y < 0 || demo.Frame.Bottom > scroll.Frame.Bottom - 20); step++)
    {
        var p = new Point(scroll.Frame.X + scroll.Frame.Width / 2, scroll.Frame.Y + scroll.Frame.Height / 2);
        runtime.DispatchWheel(new WheelEvent(p, 0, -3, Mode: WheelDeltaMode.Steps));
        runtime.ProcessPendingWork();
    }
    Console.WriteLine($"INFO scrolled offsetY={scroll.ScrollOffsetY:0.#} demoY={demo.Frame.Y:0.#} demoBottom={demo.Frame.Bottom:0.#} scrollBottom={scroll.Frame.Bottom:0.#}");
    DumpAll("scrolled");
}
runtime.Dispose();
