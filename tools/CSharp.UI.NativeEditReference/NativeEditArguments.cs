namespace CSharp.UI.NativeEditReference;

internal sealed class NativeEditOptions
{
    internal required string OutDir { get; init; }

    internal string? Scenario { get; init; }

    internal int ForegroundWaitMs { get; init; } = 180_000;

    internal int TimeoutMs { get; init; } = 300_000;

    /// <summary>隐藏生命周期 smoke：不显示窗口、不请求前台、不输入；不是视觉验收。</summary>
    internal bool HiddenSmoke { get; init; }
}

internal static class NativeEditArguments
{
    internal readonly record struct Parsed(
        bool Help, bool SelfTest, NativeEditOptions? Options, string? Error);

    internal static Parsed Parse(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            return new Parsed(true, false, null, null);
        }

        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            return new Parsed(false, true, null, null);
        }

        var outDir = Path.Combine("artifacts", "ime-work", "ti07-native-edit-reference", "run");
        string? scenario = null;
        var foregroundWaitMs = 180_000;
        var timeoutMs = 300_000;
        var hiddenSmoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--scenario":
                    if (!TakeValue(args, ref i, out var scenarioValue, out var scenarioError))
                    {
                        return new Parsed(false, false, null, scenarioError);
                    }

                    scenario = scenarioValue;
                    break;
                case "--out":
                    if (!TakeValue(args, ref i, out var outValue, out var outError))
                    {
                        return new Parsed(false, false, null, outError);
                    }

                    outDir = outValue;
                    break;
                case "--foreground-wait-ms":
                    if (!TakeInt(args, ref i, out foregroundWaitMs, out var fgError) || foregroundWaitMs < 0)
                    {
                        return new Parsed(false, false, null,
                            fgError.Length > 0 ? fgError : "--foreground-wait-ms must be >= 0");
                    }

                    break;
                case "--timeout-ms":
                    if (!TakeInt(args, ref i, out timeoutMs, out var toError) || timeoutMs <= 0)
                    {
                        return new Parsed(false, false, null,
                            toError.Length > 0 ? toError : "--timeout-ms must be positive");
                    }

                    break;
                case "--hidden-smoke":
                    hiddenSmoke = true;
                    break;
                default:
                    return new Parsed(false, false, null, $"Unknown argument '{arg}'.");
            }
        }

        if (scenario is not null && !NativeEditScenario.IsKnown(scenario))
        {
            return new Parsed(false, false, null,
                $"Unknown scenario '{scenario}'. Known: {string.Join(", ", NativeEditScenario.All)}.");
        }

        if (hiddenSmoke && scenario is not null)
        {
            // 隐藏 smoke 只验证生命周期，不执行任何场景动作；两者同时指定是用法冲突。
            return new Parsed(false, false, null,
                "--hidden-smoke does not accept --scenario; run them separately.");
        }

        return new Parsed(false, false, new NativeEditOptions
        {
            OutDir = Path.GetFullPath(outDir),
            Scenario = scenario,
            ForegroundWaitMs = foregroundWaitMs,
            TimeoutMs = timeoutMs,
            HiddenSmoke = hiddenSmoke,
        }, null);
    }

    private static bool TakeValue(string[] args, ref int index, out string value, out string error)
    {
        error = string.Empty;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            error = $"Missing value for '{args[index]}'.";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TakeInt(string[] args, ref int index, out int value, out string error)
    {
        value = 0;
        if (!TakeValue(args, ref index, out var text, out error))
        {
            return false;
        }

        if (!int.TryParse(text, out value))
        {
            error = $"Invalid integer '{text}' for '{args[index - 1]}'.";
            return false;
        }

        return true;
    }

    internal static string Usage => """
        CSharp.UI.NativeEditReference — native EDIT control comparison (R25)

        Purpose: determine whether a plain Win32 multi-line EDIT control, using the
        system's own default input handling and Microsoft Pinyin, can follow/hide its
        candidate window purely from geometry. This tool never creates a TSF store and
        never touches the system candidate window.

        Usage:
          dotnet run --project tools/CSharp.UI.NativeEditReference -- [options]

        Options:
          --scenario <name>          Run one scenario: internal-scroll | child-move | child-clip.
                                     Omit to run all three in order. Opt-in (never run
                                     by default): child-collapse.
          --out <dir>                Evidence directory (JSONL + PNG).
                                     Default: artifacts/ime-work/ti07-native-edit-reference/run
          --foreground-wait-ms <n>   Wait for the user to click this window. Default 180000.
          --timeout-ms <n>           Whole-run watchdog. Default 300000.
          --hidden-smoke             Hidden lifecycle smoke: create windows, verify the
                                     EDIT/IME wiring, tear down. Never shows a window, never
                                     requests foreground, never sends input. Not a visual run.
          --self-test                Pure managed checks; no window, no COM, no input.
          --help, -h                 Show this text.

        Scenarios (each does exactly one geometry action):
          internal-scroll   EM_LINESCROLL by one line; EDIT and parent do not move.
          child-move        SetWindowPos the own EDIT child up by 11 physical px;
                            parent and viewport do not move. This is a child-window move
                            comparison, not an internal-scroll fix.
          child-clip        Move the own EDIT above the viewport so the composition line is
                            fully clipped (parent viewport stays visible), then restore.
          child-collapse    [opt-in; never in the default set] Resize the own EDIT child to
                            height=0 with SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE (width/origin
                            unchanged), keep the live composition + focus, then restore the
                            original size. No hide/show, no style change, no focus change,
                            no re-typing. System candidate HWND is never touched.

        Safety:
          * One ordinary SetForegroundWindow at start.
          * Startup only: if (and only if) the foreground is already our own parent
            window, one ordinary SetFocus to our own EDIT is allowed as normal initial
            child focus. If the foreground request is refused, or the foreground is not
            ours, no SetFocus happens and the tool keeps waiting for a real user click.
            Focus never changes once a scenario starts.
          * Every key event re-checks foreground == own parent AND focus == own EDIT AND
            the window thread belongs to this process. Losing foreground stops the run.
          * No TOPMOST, no AttachThreadInput, no simulated mouse, no SetFocus to any
            external window.
          * The system candidate HWND is never moved, hidden or manipulated.
          * Global IME / keyboard-layout settings are never changed.

        Exit codes (execution/data integrity only — never a visual verdict):
          0 = all checks passed and no pending visual item
          1 = assertion failure, aborted input, or host exception
          2 = precondition missing (foreground/focus/identity not established)
          3 = checks passed but candidate visual still needs human review (normal success)

        This tool cannot and does not claim that R15/TI-07 is complete.
        """;
}
