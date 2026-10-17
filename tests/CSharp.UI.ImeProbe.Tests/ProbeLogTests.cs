using System.Text.Json;
using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 日志顺序与字段语义测试：文件中的行序必须严格等于 seq 顺序，且托管线程号与 Win32 线程号分开记录。
/// </summary>
public sealed class ProbeLogTests
{
    [Fact]
    public void FileOrderMatchesSequenceNumbers_UnderConcurrentWriters()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imeprobe-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var log = new ProbeLog(path, echo: false))
            {
                Parallel.For(0, 200, i => log.Record("test", "unit", "record", new Dictionary<string, object?>
                {
                    ["i"] = i,
                }));
            }

            var lines = File.ReadAllLines(path);
            Assert.NotEmpty(lines);
            Assert.Contains("\"kind\":\"log-open\"", lines[0]);

            var sequences = lines
                .Where(line => line.Contains("\"kind\":\"record\"", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64())
                .ToList();

            Assert.Equal(200, sequences.Count);
            for (var i = 1; i < sequences.Count; i++)
            {
                Assert.True(sequences[i] > sequences[i - 1],
                    $"seq must increase in file order: {sequences[i - 1]} then {sequences[i]}");
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void RecordsCarryManagedAndWin32ThreadIdsSeparately()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imeprobe-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var log = new ProbeLog(path, echo: false))
            {
                log.Record("test", "unit", "record");
            }

            var record = File.ReadAllLines(path)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .First(element => element.GetProperty("kind").GetString() == "record");

            Assert.True(record.TryGetProperty("managed_tid", out _));
            Assert.True(record.TryGetProperty("win32_tid", out var win32));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(win32.GetInt64() > 0);
            }
            else
            {
                // 非 Windows 上 ProbeLog 不调用 kernel32：native tid 是显式的空值，并单独断言。
                Assert.Equal(0, win32.GetInt64());
            }

            Assert.False(record.TryGetProperty("tid", out _), "the ambiguous 'tid' field must be gone");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FaultInjectedWriteFailure_IsCountedAndDoesNotCorruptTheLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imeprobe-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var log = new ProbeLog(path, echo: false))
            {
                log.ThrowOnNextWrite = true;
                Assert.Throws<IOException>(() => log.Record("test", "unit", "injected"));
                log.Record("test", "unit", "after-failure");

                Assert.Equal(1, log.FailedWriteCount);
            }

            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, line => line.Contains("\"kind\":\"after-failure\"", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("\"kind\":\"injected\"", StringComparison.Ordinal));

            var sequences = lines
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64())
                .ToList();
            for (var i = 1; i < sequences.Count; i++)
            {
                Assert.True(sequences[i] > sequences[i - 1]);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
