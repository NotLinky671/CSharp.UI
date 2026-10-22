using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>真实隐藏窗口 smoke：注册/创建/销毁/注销，不显示窗口、不注入输入。</summary>
public sealed class WindowSmokeTests
{
    [Fact]
    public void UnicodeClassRegistersCreatesDestroysAndUnregisters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var className = $"CSharp.UI.TsfHostCheck.Smoke.测试.{Environment.ProcessId}";
        WindowNative.WndProcDelegate proc = (hWnd, msg, wParam, lParam) =>
            WindowNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        var instance = WindowNative.GetModuleHandleW(null);
        var wc = new WndClassW
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(proc),
            Instance = instance,
            ClassName = className,
        };

        var atom = WindowNative.RegisterClassW(ref wc);
        Assert.NotEqual(0, atom);

        var hwnd = WindowNative.CreateWindowExW(
            0, className, "CSharp.UI TSF prototype hidden smoke", 0,
            0, 0, 120, 80, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Assert.False(NativeMethods.IsWindowVisible(hwnd));

        Assert.True(WindowNative.DestroyWindow(hwnd));
        Assert.True(WindowNative.UnregisterClassW(className, instance));
        GC.KeepAlive(proc);
    }
}
