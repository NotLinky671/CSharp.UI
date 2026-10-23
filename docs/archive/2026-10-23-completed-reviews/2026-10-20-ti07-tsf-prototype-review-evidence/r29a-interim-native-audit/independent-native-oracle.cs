using System.Runtime.InteropServices;

internal static class NativeOracle
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    internal static Rect Client(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r)) throw new Exception("GetClientRect failed");
        var p = new Point();
        if (!ClientToScreen(hwnd, ref p)) throw new Exception("ClientToScreen failed");
        return new Rect { Left=p.X,Top=p.Y,Right=p.X+r.Width,Bottom=p.Y+r.Height };
    }
    internal static Rect Window(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new Exception("GetWindowRect failed");
        return r;
    }
    internal static Rect VisibleIntersection(params nint[] windows)
    {
        var r=Client(windows[0]);
        foreach(var w in windows.Skip(1)) {
            var next=Client(w);
            r=new Rect {Left=Math.Max(r.Left,next.Left),Top=Math.Max(r.Top,next.Top),Right=Math.Min(r.Right,next.Right),Bottom=Math.Min(r.Bottom,next.Bottom)};
        }
        return r.Width<=0||r.Height<=0?default:r;
    }
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint process);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd,ref Point point);
}
