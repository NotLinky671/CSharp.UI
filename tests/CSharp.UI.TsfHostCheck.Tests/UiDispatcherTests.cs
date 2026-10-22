using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class UiDispatcherTests
{
    [Fact]
    public void GenericInvokeReturnsValueOnUiThreadAndPropagatesExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logPath = Path.Combine(Path.GetTempPath(), $"tsf-invoke-{Guid.NewGuid():N}.jsonl");
        using var log = new ProbeLog(logPath, echo: false);
        var traceSink = new ProbeTsfTrace(log);
        var store = new AcpTextStore(traceSink.Write);
        using var window = new TsfHostWindow(store, log, traceSink.Write);

        var uiManagedThreadId = 0;
        Exception? createError = null;
        var loopThread = new Thread(() =>
        {
            try
            {
                if (!window.Create(240, 120, "CSharp.UI TSF hidden Invoke test", show: false))
                {
                    throw new InvalidOperationException("hidden window creation failed");
                }

                uiManagedThreadId = Environment.CurrentManagedThreadId;
                window.RunMessageLoop();
            }
            catch (Exception ex)
            {
                createError = ex;
                window.LoopStarted.Set();
            }
        })
        {
            IsBackground = true,
            Name = "tsf-invoke-test-ui",
        };
        loopThread.Start();

        Assert.True(window.LoopStarted.Wait(TimeSpan.FromSeconds(5)), "message loop did not start");
        if (createError is not null)
        {
            throw createError;
        }

        var value = window.Invoke(() => 42);
        var invokedThreadId = window.Invoke(() => Environment.CurrentManagedThreadId);

        Assert.Equal(42, value);
        Assert.Equal(uiManagedThreadId, invokedThreadId);

        var exception = Assert.Throws<AggregateException>(
            () => window.Invoke<int>(() => throw new InvalidOperationException("injected")));
        Assert.IsType<InvalidOperationException>(exception.InnerException);

        window.RequestClose();
        window.RequestClose();
        Assert.Equal(1, window.CloseRequestCount);
        Assert.True(loopThread.Join(TimeSpan.FromSeconds(5)), "message loop did not exit");
    }
}
