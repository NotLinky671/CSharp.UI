using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 结构化事件日志（JSON Lines）。每条记录都带顺序号、单调时间、墙钟时间、托管/原生线程号、
/// 会话号、来源与单位，便于把原生消息、IMM32 查询、GLFW 回调和注入脚本放进同一条时间线。
/// </summary>
/// <remarks>
/// 只记录专用测试窗口与固定测试文本；不读取用户日常输入。
/// </remarks>
internal sealed class ProbeLog : IDisposable, IProbeEventLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly bool _echo;
    private long _sequence;
    private bool _disposed;

    internal ProbeLog(string path, bool echo = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full) ?? ".");
        _writer = new StreamWriter(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        _echo = echo;
        LogPath = full;
        ProcessId = Environment.ProcessId;
        SessionId = Process.GetCurrentProcess().SessionId;
        ManagedThreadId = Environment.CurrentManagedThreadId;
        NativeThreadId = NativeMethods.CurrentWin32ThreadId();
        Record("probe", "ProbeLog", "log-open", new Dictionary<string, object?>
        {
            ["path"] = full,
            ["pid"] = ProcessId,
            ["session"] = SessionId,
            ["managed_thread_id"] = ManagedThreadId,
            ["win32_thread_id"] = NativeThreadId,
            ["unit"] = "json-lines",
        });
    }

    internal string LogPath { get; }

    internal int ProcessId { get; }

    internal int SessionId { get; }

    /// <summary>写日志的托管线程号（构造时记录，不再是未赋值的默认 0）。</summary>
    internal int ManagedThreadId { get; }

    /// <summary>写日志的原生 Win32 线程 ID；非 Windows 上为 0（不调用 kernel32）。</summary>
    internal uint NativeThreadId { get; }

    internal long LastSequence => Interlocked.Read(ref _sequence);

    /// <summary>故障注入：下一次写入抛出异常（验证“日志写入失败不穿回原生回调”）。</summary>
    internal bool ThrowOnNextWrite { get; set; }

    /// <summary>因故障注入而失败的写入次数。</summary>
    internal int FailedWriteCount { get; private set; }


    /// <summary>
    /// 写入一条记录。序号分配、序列化与写入在同一把锁内完成，因此文件中的行序严格等于 seq 顺序。
    /// </summary>
    /// <param name="stage">生命周期阶段：probe / wndproc / glfw / imm32 / script / tsf / capture / scenario。</param>
    /// <param name="source">来源：hwnd-subclass / glfw-char-callback / glfw-key-callback / imm32-api / sendinput / ...</param>
    /// <param name="kind">记录种类。</param>
    /// <param name="fields">附加字段；字符串值按原文写入，需要转义的 UTF-16 用 <see cref="EscapeUtf16"/>。</param>
    internal void Record(string stage, string source, string kind, IDictionary<string, object?>? fields = null)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (ThrowOnNextWrite)
            {
                // 故障注入：模拟日志写入失败，调用方必须把它挡在原生回调边界内。
                ThrowOnNextWrite = false;
                FailedWriteCount++;
                throw new IOException("fault injection: log write failure");
            }

            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["seq"] = ++_sequence,
                ["t_ms"] = Math.Round(_clock.Elapsed.TotalMilliseconds, 4),
                ["wall"] = DateTimeOffset.Now.ToString("O"),
                ["managed_tid"] = Environment.CurrentManagedThreadId,
                ["win32_tid"] = NativeMethods.CurrentWin32ThreadId(),
                ["pid"] = ProcessId,
                ["session"] = SessionId,
                ["stage"] = stage,
                ["source"] = source,
                ["kind"] = kind,
            };

            if (fields is not null)
            {
                foreach (var pair in fields)
                {
                    record[pair.Key] = pair.Value;
                }
            }

            var json = JsonSerializer.Serialize(record, SerializerOptions);
            _writer.WriteLine(json);
            if (_echo)
            {
                Console.WriteLine(json);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    /// <summary>结构化日志接口的显式实现，供只依赖 <see cref="IProbeEventLog"/> 的恢复路径使用。</summary>
    void IProbeEventLog.Record(string stage, string source, string kind, IDictionary<string, object?>? fields)
        => Record(stage, source, kind, fields);
}

/// <summary>UTF-16 / 原生单位换算与转义辅助（便于单元测试，不依赖 Windows）。</summary>
internal static class ImeFormat
{
    /// <summary>把字符串转成 <c>\uXXXX</c> 序列，明确每个 UTF-16 码元，便于核对非 BMP 数据。</summary>
    internal static string EscapeUtf16(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        var builder = new StringBuilder(value.Length * 6);
        foreach (var unit in value)
        {
            builder.Append("\\u").Append(((int)unit).ToString("X4"));
        }

        return builder.ToString();
    }

    /// <summary>UTF-16 码元数。</summary>
    internal static int CodeUnitCount(string? value) => value?.Length ?? 0;

    /// <summary>Unicode 码点数（代理对算一个）。</summary>
    internal static int CodePointCount(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            count++;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
        }

        return count;
    }

    /// <summary>把字节数格式化为日志字段值（IMM32 的返回量以字节计）。</summary>
    internal static int BytesForUtf16(string? value) => (value?.Length ?? 0) * 2;

    /// <summary>十六进制输出字节数组（用于 GCS_COMPATTR）。</summary>
    internal static string ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>把 DWORD 数组（GCS_COMPCLAUSE）输出为十六进制列表。</summary>
    internal static string ToHex(uint[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(",", values.Select(v => v.ToString("X8")));
    }
}

/// <summary>CANDIDATELIST 解析（纯托管，可用合成数据测试）。</summary>
internal static class CandidateListParser
{
    internal sealed record Candidate(uint Index, string Text, bool IsSelected);

    /// <summary>解析 <see cref="NativeMethods.ImmGetCandidateListW"/> 返回的原始缓冲区。</summary>
    /// <remarks>
    /// 结构（winuser.h CANDIDATELIST）：dwSize、dwStyle、dwCount、dwSelection、dwPageStart、dwPageSize，
    /// 随后是 dwCount 个相对本结构起点的字符串偏移（DWORD），字符串为 NUL 结尾的 WCHAR。
    /// 偏移单位是字节；字符串长度单位是 UTF-16 码元。
    /// </remarks>
    internal static List<Candidate> Parse(byte[]? raw, int maxCandidates = 16)
    {
        var result = new List<Candidate>();
        if (raw is null || raw.Length < 24)
        {
            return result;
        }

        var size = BitConverter.ToUInt32(raw, 0);
        if (size == 0 || size > raw.Length)
        {
            return result;
        }

        var count = BitConverter.ToUInt32(raw, 8);
        var selection = BitConverter.ToUInt32(raw, 12);
        for (var i = 0u; i < count && result.Count < maxCandidates; i++)
        {
            var offsetField = 24 + (int)i * 4;
            if (offsetField + 4 > size)
            {
                break;
            }

            var offset = BitConverter.ToUInt32(raw, offsetField);
            if (offset >= size || offset >= raw.Length)
            {
                continue;
            }

            var end = (int)offset;
            while (end + 1 < raw.Length && !(raw[end] == 0 && raw[end + 1] == 0))
            {
                end += 2;
            }

            var text = Encoding.Unicode.GetString(raw, (int)offset, end - (int)offset);
            result.Add(new Candidate(i, text, i == selection));
        }

        return result;
    }
}
