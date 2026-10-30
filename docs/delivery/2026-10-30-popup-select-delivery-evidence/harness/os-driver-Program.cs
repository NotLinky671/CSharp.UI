using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// P06 real-Gallery OS driver (SendInput + PrintWindow against our own PID only).
// Usage: driver <ControlGallery.exe> <evidence-dir>
// Layout coordinates come from the coordprobe run on the real GalleryApp at
// 900x700 logical @ scale 1.5 (DPI 144); the driver measures the real client
// rect and scales logical -> client pixels with dpiScale = clientH / 700.
public static class Program
{
    private const double LogicalW = 900, LogicalH = 700;

    // ---- logical coords (probe, after scrolling the demo section into view) ----
    // select-0 普通"选择模式"  center (476,271); select-1 disabled (476,339);
    // select-2 longlist (520.5,380.8); select-3 inner-scroll anchor (438,449);
    // settings button (138,600); theme-toggle initially (826,37) at offset 0.
    private static readonly (double x, double y) SelectMode = (476, 271);
    private static readonly (double x, double y) SelectDisabled = (476, 339);
    private static readonly (double x, double y) SelectLong = (520.5, 380.8);
    private static readonly (double x, double y) SelectInner = (438, 449);
    private static readonly (double x, double y) SettingsBtn = (138, 600);
    private static readonly (double x, double y) ThemeToggle = (826, 37);
    // scroll position for the demo section (probe scrolled offsetY 2160)
    private static readonly (double x, double y) ScrollHover = (450, 350);
    private static readonly (double x, double y) OutsidePoint = (450, 680); // page area below section

    private static double _s = 1.0; // client-px per logical unit
    private static IntPtr _hwnd;
    private static int _w, _h;
    private static string _ev = ".";
    private static int _fail;

    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: driver <exe> <evidence-dir>"); return 2; }
        var exe = Path.GetFullPath(args[0]);
        _ev = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(_ev);

        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start gallery");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"GALLERY-OUT {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"GALLERY-ERR {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            _hwnd = WaitWindow(process.Id, TimeSpan.FromSeconds(30));
            Console.WriteLine($"WINDOW pid={process.Id} hwnd=0x{_hwnd:X}");
            if (_hwnd == IntPtr.Zero) return 3;

            W32.GetClientRect(_hwnd, out var r);
            _w = r.R - r.L; _h = r.B - r.T;
            _s = _h / LogicalH;
            Console.WriteLine($"CLIENT {_w}x{_h} scale={_s:0.###} (logical {LogicalW}x{LogicalH})");
            W32.SetForegroundWindow(_hwnd);
            Thread.Sleep(800); // first frames
            Shot("00-initial-light");

            // ---- scroll until demo section visible: wheel down at page center ----
            for (var i = 0; i < 14; i++) { Wheel(ScrollHover, -120 * 5); Thread.Sleep(120); }
            Shot("01-scrolled-to-section");

            // ---- S1: normal Select open, ArrowDown+Enter commit, echo ----
            Click(SelectMode); Thread.Sleep(300); Shot("02-select-open");
            Key(0x28 /*VK_DOWN*/); Key(0x0D /*VK_RETURN*/); Thread.Sleep(300); Shot("03-select-committed");

            // ---- S2: disabled Select click must not open ----
            Click(SelectDisabled); Thread.Sleep(250); Shot("04-disabled-click");

            // ---- S3: long list, End key, Enter last ----
            Click(SelectLong); Thread.Sleep(300); Shot("05-longlist-open");
            Key(0x23 /*VK_END*/); Thread.Sleep(250); Shot("06-longlist-end");
            Key(0x0D); Thread.Sleep(300); Shot("07-longlist-committed");

            // ---- S4: generic Popup: open, type into TextField, close ----
            Click(SettingsBtn); Thread.Sleep(350); Shot("08-settings-open");
            // TextField sits just under "设置内容" inside popup below the button
            Click((SettingsBtn.x + 60, SettingsBtn.y + 70)); Thread.Sleep(200);
            Type("hi"); Thread.Sleep(200); Shot("09-settings-typed");
            Key(0x1B /*VK_ESCAPE*/); Thread.Sleep(250); Shot("10-settings-esc");
            // also click 关闭 button path on reopen
            Click(SettingsBtn); Thread.Sleep(300);
            Click((SettingsBtn.x + 40, SettingsBtn.y + 150)); Thread.Sleep(250); Shot("11-settings-closebtn");

            // ---- S5: outside press closes, lower layer not activated ----
            Click(SelectMode); Thread.Sleep(300); Shot("12-reopen-before-outside");
            Click(OutsidePoint); Thread.Sleep(300); Shot("13-outside-closed");

            // ---- S6: Esc on open popup closes; app stays alive ----
            Click(SelectMode); Thread.Sleep(300);
            Key(0x1B); Thread.Sleep(300); Shot("14-esc-closed");

            // ---- S7: theme toggle to dark, verify bg pixel darkened ----
            // scroll back to top first
            for (var i = 0; i < 14; i++) { Wheel(ScrollHover, 120 * 5); Thread.Sleep(100); }
            Thread.Sleep(300);
            var lightPx = Pixel(450, 350);
            Click(ThemeToggle); Thread.Sleep(500); Shot("15-dark-theme");
            var darkPx = Pixel(450, 350);
            Console.WriteLine($"PIXEL light={lightPx:X8} dark={darkPx:X8}");
            Check("theme-darkened", darkPx != lightPx && (darkPx & 0xFF) < 0x60, $"light={lightPx:X8} dark={darkPx:X8}");

            Check("app-alive", !process.HasExited, "gallery exited unexpectedly");
            Console.WriteLine(_fail == 0 ? "RESULT PASS" : $"RESULT FAIL ({_fail})");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            Console.WriteLine($"GALLERY-EXIT code={(process.HasExited ? process.ExitCode : -1)}");
        }
    }

    private static void Check(string name, bool ok, string detail = "")
    { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " :: " + detail)}"); if (!ok) _fail++; }

    private static (int x, int y) P((double x, double y) l)
    {
        W32.GetClientRect(_hwnd, out var r); _w = r.R - r.L; _h = r.B - r.T; _s = _h / LogicalH;
        W32.ClientToScreen(_hwnd, ref r); return ((int)(l.x * _s) + r.L, (int)(l.y * _s) + r.T);
    }

    private static void Click((double x, double y) l)
    {
        var (x, y) = P(l);
        W32.SetCursorPos(x, y); Thread.Sleep(60);
        W32.SendInput(1, new[] { new W32.INPUT { type = 0, mi = new W32.MI { dx = x, dy = y, dwFlags = 0x0002 } } }, Marshal.SizeOf<W32.INPUT>());
        Thread.Sleep(60);
        W32.SendInput(1, new[] { new W32.INPUT { type = 0, mi = new W32.MI { dx = x, dy = y, dwFlags = 0x0004 } } }, Marshal.SizeOf<W32.INPUT>());
    }

    private static void Wheel((double x, double y) l, int delta)
    {
        var (x, y) = P(l); W32.SetCursorPos(x, y); Thread.Sleep(40);
        W32.SendInput(1, new[] { new W32.INPUT { type = 0, mi = new W32.MI { dx = x, dy = y, mouseData = (uint)delta, dwFlags = 0x0800 } } }, Marshal.SizeOf<W32.INPUT>());
    }

    private static void Key(int vk)
    {
        W32.SendInput(1, new[] { new W32.INPUT { type = 1, ki = new W32.KI { wVk = (ushort)vk, dwFlags = 0 } } }, Marshal.SizeOf<W32.INPUT>());
        Thread.Sleep(50);
        W32.SendInput(1, new[] { new W32.INPUT { type = 1, ki = new W32.KI { wVk = (ushort)vk, dwFlags = 2 } } }, Marshal.SizeOf<W32.INPUT>());
    }

    private static void Type(string s)
    {
        foreach (var ch in s)
        {
            W32.SendInput(1, new[] { new W32.INPUT { type = 1, ki = new W32.KI { wScan = ch, dwFlags = 4 } } }, Marshal.SizeOf<W32.INPUT>());
            W32.SendInput(1, new[] { new W32.INPUT { type = 1, ki = new W32.KI { wScan = ch, dwFlags = 4 | 2 } } }, Marshal.SizeOf<W32.INPUT>());
        }
    }

    private static uint Pixel(int lx, int ly)
    {
        W32.GetClientRect(_hwnd, out var r); var w = r.R - r.L; var h = r.B - r.T;
        var buf = Capture(_hwnd, w, h);
        var x = (int)(lx * _s); var y = (int)(ly * _s);
        var i = (y * w + x) * 4;
        return (uint)(buf[i] | buf[i + 1] << 8 | buf[i + 2] << 16 | buf[i + 3] << 24);
    }

    private static void Shot(string name)
    {
        W32.GetClientRect(_hwnd, out var r); var w = r.R - r.L; var h = r.B - r.T;
        SaveBmp(Path.Combine(_ev, name + ".bmp"), Capture(_hwnd, w, h), w, h);
    }

    private static IntPtr WaitWindow(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = IntPtr.Zero;
            W32.EnumWindows((h, _) =>
            {
                W32.GetWindowThreadProcessId(h, out var owner);
                if (owner != pid || !W32.IsWindowVisible(h)) return true;
                var title = new StringBuilder(256);
                W32.GetWindowText(h, title, title.Capacity);
                if (!title.ToString().Contains("控件展示", StringComparison.Ordinal)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }

    private static byte[] Capture(IntPtr hwnd, int w, int h)
    {
        var winDc = W32.GetDC(hwnd);
        var memDc = W32.CreateCompatibleDC(winDc);
        var bmp = W32.CreateCompatibleBitmap(winDc, w, h);
        var old = W32.SelectObject(memDc, bmp);
        W32.BitBlt(memDc, 0, 0, w, h, winDc, 0, 0, 0x00CC0020); // test: screen capture instead of PrintWindow
        W32.SelectObject(memDc, old);
        var info = new W32.BITMAPINFO { Colors = new uint[3] };
        info.H.Size = (uint)Marshal.SizeOf<W32.BITMAPINFOHEADER>();
        info.H.W = w; info.H.H = -h; info.H.Planes = 1; info.H.BitCount = 32;
        var buffer = new byte[w * h * 4];
        W32.GetDIBits(memDc, bmp, 0, h, buffer, ref info, 0);
        W32.DeleteObject(bmp); W32.DeleteDC(memDc); W32.ReleaseDC(hwnd, winDc);
        return buffer;
    }

    private static void SaveBmp(string path, byte[] bgra, int w, int h)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(54 + bgra.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(bgra.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(bgra);
        Console.WriteLine($"BMP {path} {w}x{h}");
    }

    private static class W32
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref RECT r);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, int start, int lines, byte[] buf, ref BITMAPINFO info, uint usage);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MI mi; public KI ki; }
        [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER
        { public uint Size; public int W, H; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPel, YPel; public uint ClrUsed, ClrImportant; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO
        { public BITMAPINFOHEADER H; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] Colors; }
    }
}
