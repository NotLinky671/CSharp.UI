using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 只读 DPI awareness 查询；PerMonitorV2 由原型 exe 的 manifest 声明，本类不修改系统/进程设置。
/// </summary>
internal static class DpiNative
{
    internal static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiContext, IntPtr comparisonContext);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr hprocess, out int value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    internal static bool IsPerMonitorV2()
    {
        try
        {
            return AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string DescribeThreadAwareness()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            var awareness = GetAwarenessFromDpiAwarenessContext(context);
            var v2 = AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2);
            return $"context=0x{context.ToInt64():X} awareness={awareness} per_monitor_v2={v2}";
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.GetType().Name}";
        }
    }

    internal static string DescribeProcessAwareness()
    {
        try
        {
            var hr = GetProcessDpiAwareness(GetCurrentProcess(), out var awareness);
            return hr >= 0 ? $"awareness={awareness}" : $"GetProcessDpiAwareness hr=0x{hr:X8}";
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.GetType().Name}";
        }
    }
}
