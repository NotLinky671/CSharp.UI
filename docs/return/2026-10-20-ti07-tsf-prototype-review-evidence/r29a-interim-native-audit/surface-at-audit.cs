using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>显示表面查询结果：区分“不可见/最小化（零矩形 S_OK）”与“查询失败”。</summary>
internal readonly record struct DisplaySurfaceResult(
    int HResult, TsfRect Rect, bool WindowAvailable, string? Failure)
{
    internal bool Ok => HResult >= 0;
}

/// <summary>
/// 显示表面（`ITextStoreACP::GetScreenExt`）的数据源。
/// <para>
/// 契约依据：微软官方 Windows-classic-samples `tsfapp/textstor.cpp` 的
/// `CTSFEditWnd::GetScreenExt` 明确注明 “no lock is necessary for this method.”，
/// 直接 `GetClientRect` + `MapWindowPoints` 得到客户区屏幕矩形，非法 view 返回 `E_INVALIDARG`。
/// 因此显示表面**独立于文档读锁与文本布局版本**，只随真实窗口的客户区几何变化。
/// </para>
/// <para>
/// 查询只读：不排版、不发通知、不 `SetFocus`、不申请文档锁。
/// </para>
/// </summary>
internal static class DisplaySurface
{
    private const int SW_SHOWMINIMIZED = 2;

    /// <summary>
    /// 读取当前真实 HWND 的客户区屏幕矩形。
    /// 未创建/未显示/最小化 → `S_OK` + 全零矩形（与官方“最小化返回零矩形”一致）。
    /// 原生查询失败 → 负 HRESULT，由调用方如实上报，绝不用旧坐标伪装成功。
    /// </summary>
    internal static DisplaySurfaceResult FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return new DisplaySurfaceResult(TsfHResult.S_OK, default, false, "no-window");
        }

        try
        {
            // 最小化或不可见：官方语义为零矩形，而不是沿用上一次的坐标。
            if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
            {
                return new DisplaySurfaceResult(TsfHResult.S_OK, default, true, "hidden-or-minimized");
            }

            if (!NativeMethods.GetClientRect(hwnd, out var client))
            {
                return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true, "get-client-rect-failed");
            }

            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin))
            {
                return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true, "client-to-screen-failed");
            }

            var rect = TsfRect.FromLtrb(origin.X, origin.Y,
                origin.X + client.Width, origin.Y + client.Height);
            return new DisplaySurfaceResult(TsfHResult.S_OK, rect, true, null);
        }
        catch (Exception ex)
        {
            // 异常不穿过 COM 边界；由调用方输出零矩形并返回失败 HRESULT。
            return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// R29A opt-in：真实单行输入 child 与其显示祖先（viewport 或 root）客户区的屏幕交集。
    /// <para>
    /// 与 <see cref="FromWindow"/> 同样无锁、独立于文本 generation：只读真实 HWND 几何，
    /// 不排版、不发通知、不取文档锁。隐藏/最小化/无真实交集 → <c>S_OK</c> + 全零矩形；
    /// 任一原生查询失败 → 负 HRESULT 并如实上报，绝不合成旧坐标。
    /// </para>
    /// </summary>
    /// <param name="inputHwnd">真实输入 child（非零）。</param>
    /// <param name="clipAncestorHwnd">显示边界祖先：viewport child（opt-in）或 root（默认）。</param>
    internal static DisplaySurfaceResult FromChildIntersection(IntPtr inputHwnd, IntPtr clipAncestorHwnd)
    {
        if (inputHwnd == IntPtr.Zero || clipAncestorHwnd == IntPtr.Zero
            || !IsWindow(inputHwnd) || !IsWindow(clipAncestorHwnd))
        {
            return new DisplaySurfaceResult(TsfHResult.S_OK, default, false, "no-window");
        }

        try
        {
            if (IsIconic(clipAncestorHwnd) || !IsWindowVisible(clipAncestorHwnd)
                || IsIconic(inputHwnd) || !IsWindowVisible(inputHwnd))
            {
                return new DisplaySurfaceResult(TsfHResult.S_OK, default, true, "hidden-or-minimized");
            }

            if (!NativeMethods.GetWindowRect(inputHwnd, out var inputWindow))
            {
                return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true, "get-window-rect-failed");
            }

            if (!NativeMethods.GetClientRect(clipAncestorHwnd, out var ancestorClient))
            {
                return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true, "get-client-rect-failed");
            }

            var ancestorOrigin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(clipAncestorHwnd, ref ancestorOrigin))
            {
                return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true, "client-to-screen-failed");
            }

            var inputScreen = TsfRect.FromLtrb(inputWindow.Left, inputWindow.Top,
                inputWindow.Right, inputWindow.Bottom);
            var ancestorScreen = TsfRect.FromLtrb(ancestorOrigin.X, ancestorOrigin.Y,
                ancestorOrigin.X + ancestorClient.Width, ancestorOrigin.Y + ancestorClient.Height);
            var intersection = TsfRect.Intersect(inputScreen, ancestorScreen);
            return new DisplaySurfaceResult(TsfHResult.S_OK, intersection, true, null);
        }
        catch (Exception ex)
        {
            return new DisplaySurfaceResult(TsfHResult.E_FAIL, default, true,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 无锁交集的托管核：给定真实输入屏幕矩形与真实祖先屏幕矩形，返回可见显示矩形。
    /// 仅供托管测试验证“交集 origin 不得作 glyph origin、无交集为零 RECT”的几何不变量；
    /// 真实查询仍走 <see cref="FromChildIntersection"/> 的原生路径。
    /// </summary>
    internal static TsfRect IntersectSurface(TsfRect inputScreen, TsfRect ancestorScreen)
        => TsfRect.Intersect(inputScreen, ancestorScreen);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    internal static int MinimizedShowState => SW_SHOWMINIMIZED;
}
