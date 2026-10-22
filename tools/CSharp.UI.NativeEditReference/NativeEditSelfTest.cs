namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 纯托管自检：参数解析、退出码语义、观测不变量、安全门字段。
/// 不创建窗口、不初始化 COM、不发送输入。
/// </summary>
internal static class NativeEditSelfTest
{
    internal static int Run()
    {
        var failures = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? string.Empty : " — " + detail)}");            if (!ok)
            {
                failures++;
            }
        }

        var help = NativeEditArguments.Parse(new[] { "--help" });
        Check("help does not open a window", help.Help && help.Options is null);

        var selfTest = NativeEditArguments.Parse(new[] { "--self-test" });
        Check("self-test does not open a window", selfTest.SelfTest && selfTest.Options is null);

        var defaults = NativeEditArguments.Parse(Array.Empty<string>());
        Check("defaults are valid", defaults.Options is not null && defaults.Options.Scenario is null
            && !defaults.Options.HiddenSmoke, defaults.Error);

        var one = NativeEditArguments.Parse(new[] { "--scenario", "child-move", "--out", "x" });
        Check("explicit scenario parsed", one.Options is { Scenario: "child-move" } && one.Options.OutDir.EndsWith("x"),
            one.Error);

        var unknown = NativeEditArguments.Parse(new[] { "--scenario", "bogus" });
        Check("unknown scenario rejected", unknown.Options is null && unknown.Error?.Contains("bogus") == true);

        var conflict = NativeEditArguments.Parse(new[] { "--hidden-smoke", "--scenario", "child-clip" });
        Check("hidden smoke rejects scenario", conflict.Options is null && conflict.Error is not null);

        var badTimeout = NativeEditArguments.Parse(new[] { "--timeout-ms", "0" });
        Check("non-positive timeout rejected", badTimeout.Options is null);

        Check("all scenarios known",
            NativeEditScenario.All.All(NativeEditScenario.IsKnown)
            && NativeEditScenario.All.Length == 3);

        // R28：child-collapse 是 opt-in——被 IsKnown 接受，但**绝不**进入默认 All。
        Check("opt-in child-collapse known but not default",
            NativeEditScenario.IsKnown("child-collapse")
            && !NativeEditScenario.All.Contains("child-collapse"));

        Check("collapse parse accepted",
            NativeEditArguments.Parse(new[] { "--scenario", "child-collapse" }).Options
                is { Scenario: "child-collapse" });

        Check("only clip/collapse have restore", NativeEditScenario.HasRestore("child-clip")
            && NativeEditScenario.HasRestore("child-collapse")
            && !NativeEditScenario.HasRestore("internal-scroll")
            && !NativeEditScenario.HasRestore("child-move"));

        Check("action kinds are distinct",
            new[] { "internal-scroll", "child-move", "child-clip", "child-collapse" }
                .Select(NativeEditScenario.ActionKind).Distinct().Count() == 4);

        // 退出码：执行成功且有待视觉项 → 3，绝不为 0。
        Check("success with pending visual exits 3",
            NativeEditOutcome.Resolve(true, true, false, false) == NativeEditOutcome.VisualPending);
        Check("never returns 0 with pending visual",
            NativeEditOutcome.Resolve(true, true, false, false) != 0);
        Check("failed checks exit 1", NativeEditOutcome.Resolve(false, true, false, false) == 1);
        Check("aborted exits 1", NativeEditOutcome.Resolve(true, true, false, true) == 1);
        Check("missing precondition exits 2", NativeEditOutcome.Resolve(true, false, true, false) == 2);
        Check("hidden smoke can exit 0 (no visual item)",
            NativeEditOutcome.Resolve(true, false, false, false) == 0);

        Check("composition active requires start without end",
            NativeEditObservation.CompositionActive(1, 0, "ni") && !NativeEditObservation.CompositionActive(1, 1, "ni"));
        Check("composition without compstr is not active",
            !NativeEditObservation.CompositionActive(0, 0, null));
        // R25：空 CompStr 不算活动组合。
        Check("empty compstr is not an active composition",
            !NativeEditObservation.CompositionActive(1, 0, ""));
        Check("committed detection", NativeEditObservation.HasCommittedOrEnded(1, 0)
            && NativeEditObservation.HasCommittedOrEnded(0, 1)
            && !NativeEditObservation.HasCommittedOrEnded(0, 0));
        Check("baseline pollution detected",
            NativeEditObservation.BaselineDocumentIntact(0, "a", "a")
            && !NativeEditObservation.BaselineDocumentIntact(1, "a", "a")
            && !NativeEditObservation.BaselineDocumentIntact(0, "a", "ab"));

        // R25：EM_POSFROMCHAR 打包值解析（低 16 位 x、高 16 位 y，各有符号）。
        var (px, py) = EditPosFromChar.Unpack(0x0037_000A);
        var (_, negativeY) = EditPosFromChar.Unpack(unchecked((long)0xFFE4_0000UL));
        Check("em-posfromchar packing",
            px == 10 && py == 55 && negativeY < 0 && EditPosFromChar.IsFailure(-1)
            && !EditPosFromChar.IsFailure(0),
            $"x={px} y={py} negY={negativeY}");

        // R25：基线目标行末必须落在真实行尾。
        var baselineText = EditBaseline.Text();
        var start = EditBaseline.TargetLineStart();
        var end = EditBaseline.TargetLineEnd();
        Check("baseline targets a real line end",
            baselineText.Substring(start, end - start) == EditBaseline.LineText(EditBaseline.TargetLine)
            && baselineText.Split("\r\n").Length == EditBaseline.LineCount,
            $"lines={EditBaseline.LineCount} target={start}..{end}");

        var rect = new NativeRect(10, 20, 110, 120);
        var overlap = rect.Intersect(new NativeRect(0, 0, 500, 50));
        Check("rect intersection", overlap == new NativeRect(10, 20, 110, 50), overlap.ToString());
        Check("fully clipped detection",
            rect.Intersect(new NativeRect(0, 200, 500, 400)).IsEmpty);

        Console.WriteLine($"self-test: {(failures == 0 ? "PASS" : "FAIL")} ({failures} failed)");
        return failures == 0 ? 0 : 1;
    }
}
