using System.Reflection;
using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;

// Independent post-fix runtime verification: real GalleryApp + UiRuntime + Skia text service.
// Reads ScrollbarState only through reflection (diagnostic; no permanent test changes).
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 2)
        {
            var vp = new Size(double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
            using var m = App.Create(vp);
            var g = Measure(m.Root);
            Console.WriteLine($"viewport={vp} frame={m.Root.Frame} contentHeight={m.Root.ChildList[0].DesiredSize.Height:0.######} thumbLen={g.ThumbLen:0.######} maxOffset={g.MaxOffset:0.######} travel={g.Travel:0.######} thumbCenter0={g.ThumbCenterAt(0):0.######}");
            return 0;
        }
        ScenarioColdFirstDragThenSecondAtCurrentThumb();
        ScenarioResizeDuringFirstDragCancelsGesture();
        Console.WriteLine(_failures == 0 ? "RESULT=PASS" : $"RESULT=FAIL failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private sealed class App : IDisposable
    {
        public required UiRuntime Runtime { get; init; }
        public required ViewNode Root { get; init; }
        public required SkiaTextService Text { get; init; }

        public static App Create(Size viewport)
        {
            var text = new SkiaTextService();
            var images = new StubImageService();
            images.Register("placeholder", 64, 64);
            var runtime = new UiRuntime(new CSharp.UI.Samples.ControlGallery.GalleryApp(), new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = text,
                RichTextService = text as IRichTextService,
                TextureService = new RecordingTextureService(),
                ImageService = images,
                Clipboard = new StubClipboardService(),
                Clock = new TestUiClock(),
                WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
            });
            runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
            runtime.SetViewport(viewport, 1.0);
            runtime.Start();
            Pump(runtime);
            var keyed = runtime.Root.Descendants().First(x =>
                x.HasIdentityKey && "gallery-root-scroll".Equals(x.IdentityKey as string, StringComparison.Ordinal));
            var root = keyed.View is ScrollView ? keyed : keyed.FindDescendants<ScrollView>().First();
            return new App { Runtime = runtime, Root = root, Text = text };
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Text.Dispose();
        }
    }

    private static void Pump(UiRuntime runtime)
    {
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
    }

    private static object? State(ViewNode root) => Prop(root, "InteractionState");

    private static object? Prop(object owner, string name)
        => owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner);

    private static string Dump(ViewNode root)
    {
        var st = State(root);
        if (st is null)
        {
            return $"offsetY={root.ScrollOffsetY:0.###} state=null";
        }
        return string.Join(' ',
            $"offsetY={root.ScrollOffsetY:0.###}",
            $"PressKind={Prop(st, "PressKind")}",
            $"IsDraggingBar={Prop(st, "IsDraggingBar")}",
            $"Anchor={Prop(st, "BarDragAnchor")}",
            $"LastGeom={Prop(st, "LastGeometry") ?? "null"}",
            $"LastPt={Prop(st, "LastPointerPoint")}");
    }

    private sealed record Geometry(double CenterY, double ThumbLen, double MaxOffset, double Travel)
    {
        public double ThumbCenterAt(double offset)
            => 2 + (offset / MaxOffset) * Travel + ThumbLen / 2;
    }

    private static Geometry Measure(ViewNode root)
    {
        var frame = root.Frame;
        var content = root.ChildList[0].DesiredSize.Height;
        var viewport = frame.Height;
        var t = frame.Height - 4;
        var thumbLen = Math.Clamp(t * viewport / content, Math.Min(t, 24), t);
        var maxOffset = content - viewport;
        return new Geometry(2 + thumbLen / 2, thumbLen, maxOffset, t - thumbLen);
    }

    private static void ScenarioColdFirstDragThenSecondAtCurrentThumb()
    {
        Console.WriteLine("=== Scenario 1: fresh process cold first drag (Down -> Move x3 with pump) then second gesture at current thumb ===");
        using var app = App.Create(new Size(900, 700));
        var root = app.Root;
        var x = root.Frame.Right - 6;
        Console.WriteLine($"root frame={root.Frame} contentHeight={root.ChildList[0].DesiredSize.Height:0.###}");
        Console.WriteLine($"[initial] {Dump(root)}");

        var g = Measure(root);
        Console.WriteLine($"geometry: thumbCenterFirst={g.ThumbCenterAt(0):0.###} thumbLen={g.ThumbLen:0.###} maxOffset={g.MaxOffset:0.###} travel={g.Travel:0.###}");

        var down = new Point(x, g.ThumbCenterAt(0));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, down, PointerButton.Left, KeyModifiers.None, 1));
        Console.WriteLine($"[cold Down @({down.X:0.##},{down.Y:0.###})] {Dump(root)}");
        var st = State(root);
        Check(st is not null && Prop(st, "LastGeometry") is not null, "cold Down must establish LastGeometry baseline");
        Check(st is not null && $"{Prop(st, "PressKind")}" == "Thumb", "cold Down must enter PressKind=Thumb");

        var offsets = new List<double>();
        for (var i = 1; i <= 3; i++)
        {
            app.Runtime.DispatchPointer(new PointerEvent(
                PointerEventKind.Move, new Point(x, down.Y + i * 55), PointerButton.Left, KeyModifiers.None, 1));
            Pump(app.Runtime);
            Console.WriteLine($"[move{i} +{i * 55} then pump] {Dump(root)}");
            offsets.Add(root.ScrollOffsetY);
            var s2 = State(root);
            Check(s2 is not null && $"{Prop(s2, "PressKind")}" == "Thumb", $"after move{i}+pump gesture must remain Thumb");
            Check(i == 1 || offsets[i - 1] > offsets[i - 2],
                $"offset must advance after move{i}+pump ({string.Join(",", offsets.Select(v => v.ToString("0.###")))})");
        }

        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Up, new Point(x, down.Y + 165), PointerButton.Left, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[Up] {Dump(root)}");
        var afterUp = root.ScrollOffsetY;
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Move, new Point(x, down.Y + 220), PointerButton.None, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[move after Up] {Dump(root)}");
        Check(Math.Abs(root.ScrollOffsetY - afterUp) < 0.001, "offset must stay frozen after Up");

        // Second gesture: recompute the thumb center from the *current* offset.
        var now = Measure(root);
        var center2 = now.ThumbCenterAt(root.ScrollOffsetY);
        Console.WriteLine($"second gesture: current offset={root.ScrollOffsetY:0.###} -> thumbCenter={center2:0.###}");
        var down2 = new Point(x, center2);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, down2, PointerButton.Left, KeyModifiers.None, 1));
        Console.WriteLine($"[down2 @({down2.X:0.##},{down2.Y:0.###})] {Dump(root)}");
        var second = new List<double> { root.ScrollOffsetY };
        for (var i = 1; i <= 3; i++)
        {
            app.Runtime.DispatchPointer(new PointerEvent(
                PointerEventKind.Move, new Point(x, down2.Y + i * 40), PointerButton.Left, KeyModifiers.None, 1));
            Pump(app.Runtime);
            Console.WriteLine($"[move2.{i} +{i * 40} then pump] {Dump(root)}");
            second.Add(root.ScrollOffsetY);
            Check(second[i] > second[i - 1], $"second gesture move{i} must advance offset");
        }
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Up, new Point(x, down2.Y + 120), PointerButton.Left, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[Up2] {Dump(root)}");
    }

    private static void ScenarioResizeDuringFirstDragCancelsGesture()
    {
        Console.WriteLine("=== Scenario 2: first drag progresses, real viewport resize cancels stale gesture, later Move stays frozen ===");
        using var app = App.Create(new Size(900, 700));
        var root = app.Root;
        var g = Measure(root);
        var x = root.Frame.Right - 6;
        var down = new Point(x, g.ThumbCenterAt(0));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, down, PointerButton.Left, KeyModifiers.None, 1));
        Console.WriteLine($"[Down] {Dump(root)}");
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Move, new Point(x, down.Y + 55), PointerButton.Left, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[move1 then pump] {Dump(root)}");
        var worked = root.ScrollOffsetY;
        Check(worked > 0, "first Move must advance offset before resize");

        app.Runtime.SetViewport(new Size(760, 640), 1.0);
        Pump(app.Runtime);
        Console.WriteLine($"[resize 760x640 then pump] {Dump(root)}");
        var st = State(root);
        Check(st is not null && $"{Prop(st, "PressKind")}" == "None", "resize must cancel in-flight gesture");

        var frozen = root.ScrollOffsetY;
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Move, new Point(x, down.Y + 140), PointerButton.Left, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[move after resize] {Dump(root)}");
        Check(Math.Abs(root.ScrollOffsetY - frozen) < 0.001, "stale drag must not scroll after resize");

        // A fresh gesture on the new geometry must work again.
        var g2 = Measure(root);
        var x2 = root.Frame.Right - 6;
        var center2 = g2.ThumbCenterAt(root.ScrollOffsetY);
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Down, new Point(x2, center2), PointerButton.Left, KeyModifiers.None, 1));
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Move, new Point(x2, center2 + 45), PointerButton.Left, KeyModifiers.None, 1));
        Pump(app.Runtime);
        Console.WriteLine($"[fresh gesture after resize] {Dump(root)}");
        Check(root.ScrollOffsetY > frozen, "fresh gesture on resized geometry must scroll");
    }

    private static void Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "CHECK PASS: " : "CHECK FAIL: ") + message);
        if (!ok)
        {
            _failures++;
        }
    }
}
