using CSharp.UI.TsfHostCheck;

internal static class Program
{
    private static int total, failed;
    static void Check(string name, bool pass, object? details = null)
    {
        total++; if (!pass) failed++;
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}: {details}");
    }

    static int Main()
    {
        foreach (var scenario in Enum.GetValues<ChildSurfaceScenario>())
        {
            var host = new Host();
            var executor = new ChildSurfaceGeometryExecutor(host, _ => { });
            var result = executor.Run(scenario, 7);
            Check($"{scenario} ordinary success pending visual", result && executor.Stop == ChildSurfaceStop.None);
            Check($"{scenario} original zero X geometry restored", host.X == 0 && host.Y == 9 && host.RootY == 100,
                $"x={host.X} y={host.Y} rootY={host.RootY}");

            host = new Host { X = 5 };
            executor = new ChildSurfaceGeometryExecutor(host, _ => { });
            executor.Run(scenario, 7);
            Check($"{scenario} exact original nonzero X/Y geometry restored", host.X == 5 && host.Y == 9 && host.RootY == 100,
                $"x={host.X} y={host.Y} rootY={host.RootY}");

            host = new Host { FailAfterOnce = true };
            executor = new ChildSurfaceGeometryExecutor(host, _ => { });
            result = executor.Run(scenario, 7);
            Check($"{scenario} transient failed safe point cannot recover into success", !result);
            Check($"{scenario} no measurement capture after failed safe point", !host.CapturedAfterFailedProbe,
                string.Join(",", host.Captures));

            host = new Host { FailRestoreCapture = true };
            executor = new ChildSurfaceGeometryExecutor(host, _ => { });
            result = executor.Run(scenario, 7);
            Check($"{scenario} restore capture failure invalidates experiment", !result);

            host = new Host { LoseCompositionAfterBaselineCapture = true };
            executor = new ChildSurfaceGeometryExecutor(host, _ => { });
            result = executor.Run(scenario, 7);
            Check($"{scenario} pre-action loss prevents measured mutation", !result && host.MeasuredActions == 0,
                $"actions={host.MeasuredActions}");

            foreach (var fault in new[] { "measurement-capture", "wait", "restore-capture" })
            {
                host = new Host
                {
                    ThrowMeasurementCapture = fault == "measurement-capture",
                    ThrowFirstWait = fault == "wait",
                    ThrowRestoreCapture = fault == "restore-capture",
                };
                executor = new ChildSurfaceGeometryExecutor(host, _ => { });
                var escaped = false;
                try { result = executor.Run(scenario, 7); }
                catch { result = false; escaped = true; }
                Check($"{scenario} {fault} exception returns failure and restores geometry",
                    !escaped && !result && host.X == 0 && host.Y == 9 && host.RootY == 100,
                    $"escaped={escaped} x={host.X} y={host.Y} rootY={host.RootY}");
            }
        }
        var clipped = new Host { NonzeroEmptyScreenRect = true };
        var clipExecutor = new ChildSurfaceGeometryExecutor(clipped, _ => { });
        Check("clipped GetScreenExt must be exact zero RECT", !clipExecutor.Run(ChildSurfaceScenario.ClipOut, 7));
        Console.WriteLine($"RESULT {total-failed}/{total}");
        return failed == 0 ? 0 : 1;
    }

    private sealed class Host : IChildSurfaceGeometryHost
    {
        public int X = 0, Y = 9, RootX = 50, RootY = 100, MeasuredActions;
        public bool FailAfterOnce, FailRestoreCapture, LoseCompositionAfterBaselineCapture, NonzeroEmptyScreenRect;
        public bool ThrowMeasurementCapture, ThrowFirstWait, ThrowRestoreCapture;
        private int waitCalls;
        public bool CapturedAfterFailedProbe;
        private bool failedProbe, lostComposition;
        public List<string> Captures = [];
        private readonly string profile = ImeIdentity.ExpectedMicrosoftPinyinTag;

        public bool InvokeOnUiThread(Func<bool> action) => action();
        public SafePointProbe Probe(string stage)
        {
            var fail = FailAfterOnce && !failedProbe && MeasuredActions > 0
                && (stage.EndsWith("-after") || stage.EndsWith("-post"));
            if (fail) failedProbe = true;
            return new SafePointProbe(!fail, fail ? "injected transient safe point failure" : "ok",
                "ni'hao", 0, 6, 0, !lostComposition, profile, 1, 0, 1, 1);
        }
        public void Record(string kind, IDictionary<string, object?> fields) { }
        public bool Capture(string name)
        {
            Captures.Add(name);
            if ((ThrowMeasurementCapture && !name.Contains("baseline") && !name.Contains("restored"))
                || (ThrowRestoreCapture && name.Contains("restored")))
                throw new IOException("injected evidence write failure");
            if (failedProbe && !name.Contains("restored")) CapturedAfterFailedProbe = true;
            if (name.Contains("baseline") && LoseCompositionAfterBaselineCapture) lostComposition = true;
            return !(FailRestoreCapture && name.Contains("restored"));
        }
        public void Wait(int milliseconds)
        {
            if (++waitCalls == 1 && ThrowFirstWait)
                throw new InvalidOperationException("injected wait failure after action");
        }
        public int GetScrollY() => Y;
        public int GetScrollX() => X;
        public void SetScrollOffset(int x, int y) { X = x; Y = y; }
        public int ScrollByLogical(int logicalDelta) { MeasuredActions++; Y += 11; return 11; }
        public void ScrollByPhysical(int deltaY) { MeasuredActions++; Y += deltaY; }
        public void MoveRootBy(int dx, int dy) { MeasuredActions++; RootX += dx; RootY += dy; }
        public void SetRootWindowOrigin(int x, int y) { RootX = x; RootY = y; }
        public TsfRect GetTextRowScreenRect() => TsfRect.FromLtrb(RootX+60-X, RootY+60-Y, RootX+250-X, RootY+85-Y);
        public TsfRect GetInputClientScreenRect() => TsfRect.FromLtrb(RootX+50-X, RootY+50-Y, RootX+450-X, RootY+110-Y);
        public TsfRect GetViewportClientScreenRect() => TsfRect.FromLtrb(RootX+50, RootY, RootX+550, RootY+300);
        public TsfRect GetRootWindowScreenRect() => TsfRect.FromLtrb(RootX, RootY, RootX+600, RootY+400);
        public int GetScreenExt(out TsfRect rect)
        {
            var input = GetInputClientScreenRect(); var viewport = GetViewportClientScreenRect();
            rect = input.Bottom <= viewport.Top ? default : input;
            if (NonzeroEmptyScreenRect && input.Bottom <= viewport.Top) rect = TsfRect.FromLtrb(7, 8, 7, 8);
            return 0;
        }
        public int GetTextExt(int acpStart, int acpEnd, out TsfRect rect, out int clipped)
        {
            clipped = GetInputClientScreenRect().Bottom <= GetViewportClientScreenRect().Top ? 1 : 0;
            rect = clipped != 0 ? default : GetTextRowScreenRect(); return 0;
        }
    }
}
