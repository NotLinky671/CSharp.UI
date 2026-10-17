namespace CSharp.UI.ImeProbe;

/// <summary>命令行选项。</summary>
internal sealed class ProbeOptions
{
    internal string Scenario { get; private set; } = "all";

    internal string Profile { get; private set; } = "current";

    /// <summary>探针开始前是否把本窗口 IME 上下文切到中文模式（仅本窗口上下文，不改全局默认）。</summary>
    internal string ImeMode { get; private set; } = "native";

    /// <summary>是否为本窗口显式创建并关联独立 IME 上下文。</summary>
    internal bool AssociateOwnContext { get; private set; }

    /// <summary>是否在打字前做一次有界 TSF 线程管理器接入实验。</summary>
    internal bool EngageTsf { get; private set; }

    /// <summary>
    /// 显式受控开关：临时启用已安装微软拼音 profile（仅 FORPROCESS，快照并在退出前恢复校验）。
    /// 默认关闭；打开时探针会先记录快照，绝不改默认输入法或使用 FORSESSION。
    /// </summary>
    internal bool TemporaryEnableMsPinyin { get; private set; }

    /// <summary>
    /// 按键驱动方式：<c>batch</c> = 逐个 SendInput + Sleep，整段注入后统一泵消息（旧行为）；
    /// <c>pumped</c> = 每个按键之间都泵消息，用于对照 TSF 输入法的键处理是否依赖消息循环；
    /// <c>scancode</c> = 与 pumped 同样逐键泵消息，但用真实扫描码（KEYEVENTF_SCANCODE）驱动，
    /// 用于对照虚拟键（wScan=0）与真实扫描码在输入法早期键处理上的差异，不合成任何 WM_IME 消息。
    /// </summary>
    internal string InputMode { get; private set; } = "batch";

    /// <summary>是否用真实扫描码（而非 wScan=0 的虚拟键）驱动按键。</summary>
    internal bool UseScanCodes => InputMode == "scancode";

    /// <summary>
    /// 打字前的输入法模式预热按键：<c>none</c> / <c>shift</c>（模拟用户切换中英模式）。
    /// <c>ctrl-space</c> 已被明确拒绝：组合键在失焦时无法保证 keyup，探针不声称做不到的键态恢复。
    /// </summary>
    internal string Warmup { get; private set; } = "none";

    /// <summary>解析阶段就把不支持的预热直接拒绝，避免运行到一半才失败。</summary>
    private static string ValidateWarmup(string value)
        => value switch
        {
            "none" or "shift" => value,
            "ctrl-space" => throw new ArgumentException(
                "--warmup ctrl-space is intentionally rejected: a modifier chord cannot guarantee its key-up when the "
                + "foreground is lost mid-chord. Use --warmup none or shift."),
            _ => throw new ArgumentException($"unknown warmup {value}"),
        };

    /// <summary>合成边界故障注入：none / message / log / restore。</summary>
    internal string FaultInject { get; private set; } = "none";

    internal string Keys { get; private set; } = "nihao";

    internal string SecondKeys { get; private set; } = "ma";

    internal string OutDirectory { get; private set; } =
        Path.Combine("artifacts", "ime-work", $"probe-{DateTime.Now:yyyyMMdd-HHmmss}");

    internal bool Capture { get; private set; } = true;

    internal double DurationSeconds { get; private set; } = 180;

    internal int InterKeyDelayMs { get; private set; } = 120;

    internal int SettleMs { get; private set; } = 450;

    internal int Width { get; private set; } = 900;

    internal int Height { get; private set; } = 520;

    internal int ManualSeconds { get; private set; } = 180;

    /// <summary>
    /// 每个按键注入场景开始前，等待本窗口成为前台的最长时间（毫秒）。
    /// 默认 8 秒；人工把窗口点到前台时可以用更大的值（例如 60000），探针只等待、不争抢前台。
    /// </summary>
    internal int ForegroundWaitMs { get; private set; } = 8000;

    /// <summary>测试用入口：直接走与命令行相同的解析与校验逻辑（不启动窗口、不做任何原生调用）。</summary>
    internal static ProbeOptions ParseForTests(string[] args) => Parse(args);

    internal static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string? Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"option {name} requires a value");
                }

                return args[++i];
            }

            switch (name)
            {
                case "--scenario":
                    options.Scenario = Next()!;
                    break;
                case "--profile":
                    options.Profile = Next()!;
                    break;
                case "--ime-mode":
                    options.ImeMode = Next()!;
                    break;
                case "--associate-own-context":
                    options.AssociateOwnContext = ParseBool(Next()!);
                    break;
                case "--tsf":
                    options.EngageTsf = ParseBool(Next()!);
                    break;
                case "--temporary-enable-mspinyin":
                    options.TemporaryEnableMsPinyin = ParseBool(Next()!);
                    break;
                case "--input-mode":
                    options.InputMode = Next()!;
                    if (options.InputMode is not ("batch" or "pumped" or "scancode"))
                    {
                        throw new ArgumentException("--input-mode must be batch, pumped or scancode");
                    }

                    break;
                case "--warmup":
                    options.Warmup = ValidateWarmup(Next()!);
                    break;
                case "--fault-inject":
                    options.FaultInject = Next()!;
                    break;
                case "--keys":
                    options.Keys = Next()!;
                    break;
                case "--second-keys":
                    options.SecondKeys = Next()!;
                    break;
                case "--out":
                    options.OutDirectory = Next()!;
                    break;
                case "--capture":
                    options.Capture = ParseBool(Next()!);
                    break;
                case "--duration":
                    options.DurationSeconds = double.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--inter-key-delay":
                    options.InterKeyDelayMs = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--settle":
                    options.SettleMs = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--width":
                    options.Width = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--height":
                    options.Height = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--manual-seconds":
                    options.ManualSeconds = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--foreground-wait":
                    options.ForegroundWaitMs = int.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture);
                    if (options.ForegroundWaitMs < 0)
                    {
                        throw new ArgumentException("--foreground-wait must not be negative");
                    }

                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"unknown option {name}");
            }
        }

        return options;
    }

    private static bool ParseBool(string value)
        => value.ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => throw new ArgumentException($"invalid boolean {value}"),
        };

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            CSharp.UI.ImeProbe - TI-01 Windows IME feasibility probe

              --scenario <all|info|ime-commit|ime-segmented|ime-cancel|ime-focus|ime-resize|ime-candidate|latin|repeat|nonbmp|fault|fault-restore|ime-close|manual>
              --profile  <current|mspinyin|wetype>   optional process-local TSF profile activation (FORPROCESS only)
              --temporary-enable-mspinyin <on|off>   snapshot + FORPROCESS|ENABLEPROFILE + finally restore/verify
              --input-mode <batch|pumped|scancode>    batch = SendInput+Sleep then pump; pumped = pump between keys;
                                                      scancode = pumped + real scan codes (KEYEVENTF_SCANCODE)
              --keys     <ascii pinyin>              default nihao
              --second-keys <ascii pinyin>           default ma (segmented scenario)
              --out      <directory>                 default artifacts/ime-work/probe-<timestamp>
              --capture  <on|off>                    own-window PNG evidence, default on
              --duration <seconds>                   wall-clock guard, default 180
              --inter-key-delay <ms>                 default 120
              --settle   <ms>                        default 450
              --width/--height <px>                  default 900x520
              --manual-seconds <n>                   default 180

            The probe only sends keys to its own dedicated window and aborts if that window is not
            the foreground window. It never synthesizes WM_IME_* messages, and screenshots never
            capture the whole desktop. Reproducible commands are in tools/CSharp.UI.ImeProbe/README.md.
            """);
    }
}
