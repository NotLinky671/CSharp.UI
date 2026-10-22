using System.Runtime.InteropServices;
using CSharp.UI.OpenTK;

internal static class Program
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoGetApartmentType(out int type, out int qualifier);

    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) return 2;
        int failures = 0;
        foreach (var mode in new[] { ApartmentState.STA, ApartmentState.MTA })
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var beforeHr = CoGetApartmentType(out var before, out _);
                    using var outer = StaComApartmentLease.Acquire();
                    Console.WriteLine($"{mode}: before={beforeHr:X8}/{before}, lease={outer.Status}, hr={outer.HResult:X8}");
                    if (mode == ApartmentState.STA)
                    {
                        Check(outer.Succeeded && outer.OwnsReference, "real STA initialization");
                        using (var inner = StaComApartmentLease.Acquire())
                        {
                            Check(inner.Status == StaComApartmentStatus.AlreadyInitialized && inner.OwnsReference, "nested STA reference");
                        }
                        Check(CoGetApartmentType(out var afterInner, out _) >= 0 && afterInner is 0 or 3, "STA survives nested disposal");
                        outer.Dispose();
                        Check(outer.UninitializeCount == 1 && !outer.OwnsReference, "outer balanced release");
                        Check(CoGetApartmentType(out var afterOuter, out _) >= 0 && afterOuter is 0 or 3, "CLR STA reference preserved");
                    }
                    else
                    {
                        Check(outer.Status == StaComApartmentStatus.ChangedMode && !outer.OwnsReference, "MTA rejected explicitly");
                        outer.Dispose();
                        Check(outer.UninitializeCount == 0, "MTA never uninitialized by failed lease");
                        Check(CoGetApartmentType(out var after, out _) >= 0 && after == 1, "MTA reference preserved");
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(mode);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) { Console.WriteLine($"FAIL {mode}: timeout"); failures++; }
            else if (error is not null) { Console.WriteLine($"FAIL {mode}: {error.Message}"); failures++; }
        }
        Console.WriteLine($"Native audit failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
}
