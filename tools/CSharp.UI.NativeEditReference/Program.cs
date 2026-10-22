using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

internal static class Program
{
    private static int Main(string[] args)
    {
        // 先做平台判断：非 Windows 不加载任何 Win32/imm32 入口。
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("CSharp.UI.NativeEditReference only runs on Windows (Win32/EDIT/IME).");
            return 2;
        }

        var parsed = NativeEditArguments.Parse(args);
        if (parsed.Options is null && !parsed.Help && !parsed.SelfTest)
        {
            Console.Error.WriteLine(parsed.Error);
            Console.WriteLine(NativeEditArguments.Usage);
            return 1;
        }

        if (parsed.Help)
        {
            Console.WriteLine(NativeEditArguments.Usage);
            return 0;
        }

        if (parsed.SelfTest)
        {
            return NativeEditSelfTest.Run();
        }

        return RunHost(parsed.Options!);
    }

    /// <summary>
    /// 在自选线程上运行全部窗口/输入/清理，并显式选择 STA。
    /// 选择 STA 是因为本工具按“单线程消息泵 + 原生控件”模型运行（所有 HWND/IMM/profile 调用都在这一条线程上），
    /// 而不是断言 Windows 强制要求 STA。
    /// </summary>
    private static int RunHost(NativeEditOptions options)
    {
        var exitCode = 2;
        Exception? failure = null;
        var hostThread = new Thread(() =>
        {
            try
            {
                exitCode = RunHostCore(options);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = false,
            Name = "native-edit-host",
        };

        hostThread.SetApartmentState(ApartmentState.STA);
        hostThread.Start();
        hostThread.Join();

        if (failure is not null)
        {
            Console.Error.WriteLine($"host thread failed: {failure.GetType().Name}: {failure.Message}");
            return 1;
        }

        return exitCode;
    }

    private static int RunHostCore(NativeEditOptions options)
    {
        Directory.CreateDirectory(options.OutDir);
        using var log = new ProbeLog(Path.Combine(options.OutDir, "native-edit.jsonl"));
        var observations = new NativeEditObservationLog(log);
        var runner = new NativeEditRunner(options, log, observations);

        // 不使用并发 Timer：超时由 runner 在每个 wait/pump/input/action 中检查同一 deadline，
        // 因此绝不会在 ProbeLog 已释放后写日志，也不会让 UI 线程继续对无效 HWND 操作。
        var exit = NativeEditOutcome.Failed;
        try
        {
            exit = runner.Run();
        }
        catch (Exception ex)
        {
            log.Record("run", "native-edit", "host-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            exit = NativeEditOutcome.Failed;
        }

        var timedOutFlag = runner.TimedOut;
        var finalExit = timedOutFlag && exit == NativeEditOutcome.Pass
            ? NativeEditOutcome.Failed
            : exit;

        log.Record("run", "native-edit", "finish", new Dictionary<string, object?>
        {
            ["mode"] = options.HiddenSmoke ? "hidden-smoke" : "scenario",
            ["exit_code"] = finalExit,
            ["timed_out"] = timedOutFlag,
            ["window_closed"] = runner.WindowClosed,
            ["aborted"] = runner.Aborted,
            ["failed_checks"] = runner.Failures.Count == 0 ? null : string.Join(",", runner.Failures),
            ["accepted"] = false,
            ["candidate_visual"] = options.HiddenSmoke
                ? "not-required"
                : timedOutFlag ? "not-assessed" : "pending-human-review",
            ["note"] = "exit code reflects execution/data integrity only; R15/TI-07 is NOT claimed",
        });

        return finalExit;
    }
}
