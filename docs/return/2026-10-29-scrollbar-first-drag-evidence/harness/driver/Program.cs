using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Real-window verification driver for the Gallery first-drag fix.
// Launches its OWN fresh ControlGallery process, waits for the first frames, then:
//   1) cold first gesture: Down on thumb center -> 3 x Move (pump between each) -> Up, screenshots each step;
//   2) second gesture at the CURRENT thumb center (recomputed) -> 3 x Move -> Up.
// No mouse input is sent before the first Down. Only the launched pid is closed.
public static class Driver
{
    static class W32
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dcBlt, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr ddc, int x, int y, int w, int h, IntPtr sdc, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, int start, int lines, byte[] buf, ref BITMAPINFO info, uint usage);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MI mi; }
        [StructLayout(LayoutKind.Sequential)] public struct MI
        {
            public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra;
        }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER
        {
            public uint Size; public int W, H; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPel, YPel; public uint ClrUsed, ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO
        {
            public BITMAPINFOHEADER H;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] Colors;
        }

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint WM_CLOSE = 0x0010;
    }

    private static readonly StringBuilder Log = new();

    private static void Say(string line)
    {
        Console.WriteLine(line);
        Log.AppendLine(line);
    }

    private static IntPtr FindWindowByPid(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        W32.EnumWindows((h, _) =>
        {
            if (!W32.IsWindowVisible(h)) return true;
            W32.GetWindowThreadProcessId(h, out var p);
            if (p != pid) return true;
            var sb = new StringBuilder(256);
            W32.GetWindowText(h, sb, 256);
            if (sb.ToString().Contains("控件展示")) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static byte[] CaptureClient(IntPtr hwnd, int w, int h)
    {
        var winDc = W32.GetDC(hwnd);
        var memDc = W32.CreateCompatibleDC(winDc);
        var bmp = W32.CreateCompatibleBitmap(winDc, w, h);
        var old = W32.SelectObject(memDc, bmp);
        if (!W32.PrintWindow(hwnd, memDc, 1 | 2))
            W32.BitBlt(memDc, 0, 0, w, h, winDc, 0, 0, 0x00CC0020);
        W32.SelectObject(memDc, old);
        var info = new W32.BITMAPINFO { Colors = new uint[3] };
        info.H.Size = (uint)Marshal.SizeOf<W32.BITMAPINFOHEADER>();
        info.H.W = w; info.H.H = -h; info.H.Planes = 1; info.H.BitCount = 32;
        var buf = new byte[w * h * 4];
        W32.GetDIBits(memDc, bmp, 0, h, buf, ref info, 0);
        W32.DeleteObject(bmp); W32.DeleteDC(memDc); W32.ReleaseDC(hwnd, winDc);
        return buf;
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
    }

    private static int DiffCount(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
        return n;
    }

    private static void Send(uint flags)
    {
        var inp = new W32.INPUT[1];
        inp[0].type = 0;
        inp[0].mi.dwFlags = flags;
        var sent = W32.SendInput(1, inp, Marshal.SizeOf<W32.INPUT>());
        if (sent != 1) Say($"WARN SendInput flags=0x{flags:X} returned {sent}");
        Thread.Sleep(350);
    }

    private static void MoveTo(int x, int y)
    {
        W32.SetCursorPos(x, y);
        Thread.Sleep(350);
    }

    private static string Fg(IntPtr hwnd)
        => W32.GetForegroundWindow() == hwnd ? "foreground=yes" : $"foreground=no:{W32.GetForegroundWindow():X}";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: galldriver2 <outDir> <ControlGallery.exe> [contentLogicalHeight]");
            return 64;
        }
        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        var exe = Path.GetFullPath(args[1]);
        var contentLogical = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 2884.0859375;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        Say($"launched pid={proc.Id} exe={exe} cwd={psi.WorkingDirectory}");

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(200); hwnd = FindWindowByPid((uint)proc.Id); }
        if (hwnd == IntPtr.Zero)
        {
            Say("ERROR: window not found");
            try { proc.Kill(); } catch { }
            return 2;
        }
        // Let the very first frames settle; do NOT inject any pointer input yet.
        Thread.Sleep(2500);
        W32.SetForegroundWindow(hwnd);
        Thread.Sleep(500);

        W32.GetClientRect(hwnd, out var rc);
        var origin = new W32.POINT { X = 0, Y = 0 };
        W32.ClientToScreen(hwnd, ref origin);
        int cw = rc.R - rc.L, ch = rc.B - rc.T;
        var dpi = W32.GetDpiForWindow(hwnd);
        double dpiScale = dpi / 96.0;
        // Empirically this app receives pointer coordinates in physical client pixels (== layout
        // logical coordinates) while rendering at dpiScale; track geometry is therefore
        // expressed in client/input units, not client/dpiScale.
        double scale = 1.0;
        var logicalW = cw;
        var logicalH = ch;
        Say($"hwnd={hwnd:X} client={cw}x{ch} origin=({origin.X},{origin.Y}) dpi={dpi} dpiScale={dpiScale:0.###} input/logical={logicalW:0.##}x{logicalH:0.##} {Fg(hwnd)}");

        // Standard persistent-gutter geometry (12 logical px track, 2 px inset).
        var t = logicalH - 4;
        var thumbLen = Math.Clamp(t * logicalH / contentLogical, Math.Min(t, 24), t);
        var maxOffset = contentLogical - logicalH;
        var travel = t - thumbLen;
        var centerLogical = 2 + thumbLen / 2;
        Say($"standard bar: contentLogical={contentLogical:0.###} thumbLen={thumbLen:0.###} maxOffset={maxOffset:0.###} travel={travel:0.###} coldThumbCenterY={centerLogical:0.###}");

        int x0 = origin.X + (int)Math.Round((logicalW - 6) * scale);
        int y0 = origin.Y + (int)Math.Round(centerLogical * scale);
        Say($"cold gesture screen start=({x0},{y0})");

        var shots = new List<(string Tag, byte[] Pixels)>();
        void Capture(string tag)
        {
            var px = CaptureClient(hwnd, cw, ch);
            SaveBmp(Path.Combine(outDir, tag + ".bmp"), px, cw, ch);
            if (shots.Count > 0)
                Say($"capture {tag}.bmp diff-vs-prev={DiffCount(shots[^1].Pixels, px)}");
            else
                Say($"capture {tag}.bmp");
            shots.Add((tag, px));
        }

        Capture("01-before-cold");

        // Cold first gesture: cursor is placed, then the FIRST input is LEFT DOWN.
        MoveTo(x0, y0);
        if (W32.GetForegroundWindow() != hwnd)
        {
            Say($"ABORT: gallery not foreground before first Down ({Fg(hwnd)}); no input sent");
            proc.Kill();
            return 3;
        }
        Say($"cursor=(x0,y0) {Fg(hwnd)}; sending LEFTDOWN as first mouse input");
        Send(W32.MOUSEEVENTF_LEFTDOWN);
        Capture("02-cold-down");
        int[,] deltas = { { 1, 55 }, { 2, 55 }, { 3, 55 } };
        for (int i = 0; i < 3; i++)
        {
            int stepY = origin.Y + (int)Math.Round((centerLogical + deltas[i, 1] * deltas[i, 0]) * scale);
            MoveTo(x0, stepY);
            Capture($"0{3 + i}-cold-move{i + 1}");
            Say($"move{i + 1} cursor=({x0},{stepY}) {Fg(hwnd)}");
        }
        Send(W32.MOUSEEVENTF_LEFTUP);
        Capture("06-cold-up");

        // Second gesture: same action, but Down lands on the CURRENT thumb center.
        // The thumb followed the pointer 1:1 during the drag, so the resting center is
        // coldCenter + total move distance; recompute thumb geometry from that offset.
        var totalLogical = 165.0;
        var offsetAfterFirst = Math.Clamp(totalLogical / travel * maxOffset, 0, maxOffset);
        var center2Logical = 2 + (offsetAfterFirst / maxOffset) * travel + thumbLen / 2;
        Say($"second gesture: offsetAfterFirst={offsetAfterFirst:0.###} recomputed thumbCenterY={center2Logical:0.###}");
        int y2 = origin.Y + (int)Math.Round(center2Logical * scale);
        MoveTo(x0, y2);
        if (W32.GetForegroundWindow() != hwnd)
        {
            Say($"ABORT: gallery not foreground before second Down ({Fg(hwnd)}); no input sent");
            proc.Kill();
            return 3;
        }
        Say($"cursor=(x0,{y2}) {Fg(hwnd)}; sending LEFTDOWN");
        Send(W32.MOUSEEVENTF_LEFTDOWN);
        Capture("07-second-down");
        for (int i = 1; i <= 3; i++)
        {
            int stepY = origin.Y + (int)Math.Round((center2Logical + 40 * i) * scale);
            MoveTo(x0, stepY);
            Capture($"0{7 + i}-second-move{i}");
            Say($"second move{i} cursor=({x0},{stepY}) {Fg(hwnd)}");
        }
        Send(W32.MOUSEEVENTF_LEFTUP);
        Capture("11-second-up");

        Say($"post-run alive={!proc.HasExited}");
        // Close only the process we launched: WM_CLOSE first, kill only this pid as fallback.
        if (!proc.HasExited)
        {
            W32.PostMessage(hwnd, W32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!proc.WaitForExit(5000))
            {
                Say("graceful close timed out; killing own pid");
                proc.Kill();
            }
        }
        proc.WaitForExit(5000);
        File.WriteAllText(Path.Combine(outDir, "gallery.stdout.txt"), stdout.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "gallery.stderr.txt"), stderr.ToString(), new UTF8Encoding(false));
        Say($"gallery exit code={(proc.HasExited ? proc.ExitCode : -999)}");
        File.WriteAllText(Path.Combine(outDir, "driver.log.txt"), Log.ToString(), new UTF8Encoding(false));
        return 0;
    }
}
