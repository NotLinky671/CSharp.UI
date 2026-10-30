// Native width smoke for popup/select at narrow window widths (320 / 1024).
// Sibling of native-observed: reuses its verified owned-HWND + SendInput +
// WaitNext/Settle + GL.Front PNG helpers verbatim, but runs ONLY the slim
// consumer path required for width acceptance:
//   per width  -> real GalleryApp, default production services, public
//                 Started/FrameRendered observers only
//   per theme  -> real Header theme button click (top-of-page), not a state write
//   asserts    -> popup anchored at the Select, popup.Frame inside [4, W-4],
//                 framebuffer width == logical width * PixelScale,
//                 keyboard commit of a DIFFERENT option than the initial value,
//                 disabled select ignores a native click
//   output     -> Light/Dark PNG per width, logs state/frame bounds/exit code
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Samples.ControlGallery;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;

internal static class Driver
{
    private static int _fail;
    private static UiHostContext? _ctx;
    private static IntPtr _hwnd;
    private static string _ev = ".";

    private static readonly object _pngLock = new();
    private static byte[]? _pngBgra;
    private static int _pngW, _pngH;

    public static int Main()
    {
        var dir = Environment.GetEnvironmentVariable("P06_NATIVE_EV") ?? ".";
        _ev = Path.GetFullPath(dir);
        Directory.CreateDirectory(_ev);
        var width = int.TryParse(Environment.GetEnvironmentVariable("P06_WIDTH"), out var w) ? w : 320;
        var theme = (Environment.GetEnvironmentVariable("P06_WIDTH_THEME") ?? "dark").Trim().ToLowerInvariant();
        Log($"EV={_ev} WIDTH={width} THEME={theme}");

        var uiReady = new ManualResetEventSlim();
        var uiExited = new ManualResetEventSlim();
        Exception? uiError = null;

        var controller = new Thread(() => RunController(uiReady, uiExited, theme));
        controller.SetApartmentState(ApartmentState.STA);
        controller.Start();

        int code = 1;
        try
        {
            var options = new UiApplicationOptions
            {
                Title = "CSharp.UI — 控件展示",
                Width = width,
                Height = 700,
                Started = ctx => { _ctx = ctx; uiReady.Set(); },
                FrameRendered = OnFrameRendered,
            };
            code = UiApplication.Run(new GalleryApp(), options);
            Log($"UiApplication.Run exit={code}");
        }
        catch (Exception ex)
        {
            uiError = ex;
            Log($"UI-THREAD EXCEPTION {ex}");
        }
        finally { uiExited.Set(); }

        controller.Join(TimeSpan.FromSeconds(30));
        if (uiError is not null) Check("ui-thread", false, uiError.Message);
        if (_fail > 0) code = 1;
        Log($"RESULT {(_fail == 0 ? "PASS" : $"FAIL ({_fail})")}");
        return code == 0 && _fail == 0 ? 0 : 1;
    }

    // ---------------- frame / screenshot (verbatim from native-observed) ----

    private static unsafe IntPtr GlfwWin32(OpenTkHost host) => (IntPtr)host.Window.WindowPtr;

    private static void OnFrameRendered(UiHostContext ctx)
    {
        var w = (int)ctx.FramebufferSize.Width;
        var h = (int)ctx.FramebufferSize.Height;
        if (w <= 0 || h <= 0) return;
        var bgra = new byte[w * h * 4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Front);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        unsafe
        {
            fixed (byte* p = bgra)
            {
                GL.ReadPixels(0, 0, w, h, global::OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, (IntPtr)p);
            }
        }
        GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
        lock (_pngLock) { _pngBgra = bgra; _pngW = w; _pngH = h; }
    }

    private static void Shot(string name)
    {
        byte[]? bgra; int w, h;
        lock (_pngLock) { bgra = _pngBgra; w = _pngW; h = _pngH; }
        if (bgra is null) { Log($"SHOT {name} :: no frame captured"); return; }
        var path = Path.Combine(_ev, $"native-{name}.png");
        WritePng(path, bgra, w, h);
        Log($"PNG {path} {w}x{h}");
    }

    private static void WritePng(string path, byte[] bgra, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var src = (h - 1 - y) * w * 4;
            var dst = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                rgba[dst + 0] = bgra[src + 2];
                rgba[dst + 1] = bgra[src + 1];
                rgba[dst + 2] = bgra[src + 0];
                rgba[dst + 3] = bgra[src + 3];
                src += 4; dst += 4;
            }
        }
        var raw = new byte[h * (1 + w * 4)];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(rgba, y * w * 4, raw, y * (1 + w * 4) + 1, w * 4);
        }
        using var ms = new MemoryStream();
        using var ds = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
        ds.Write(raw, 0, raw.Length);
        ds.Flush(); ds.Close();
        var idat = ms.ToArray();

        static void Chunk(Stream s, string name, byte[] data)
        {
            Span<byte> lb = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lb, data.Length);
            s.Write(lb);
            var nb = System.Text.Encoding.ASCII.GetBytes(name);
            s.Write(nb);
            s.Write(data);
            var crc = Crc32(nb, data);
            Span<byte> cb = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cb, crc);
            s.Write(cb);
        }

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", idat);
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var table = _crcTable ??= BuildCrcTable();
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    private static uint[]? _crcTable;
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    // ---------------- controller ----------------

    private static void RunController(ManualResetEventSlim uiReady, ManualResetEventSlim uiExited, string theme)
    {
        try
        {
            Win32.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (!uiReady.Wait(TimeSpan.FromSeconds(60)))
            {
                Check("ui-started", false, "host never Started");
                _ctx?.RequestExit(1);
                return;
            }
            var ctx = _ctx!;
            var requestedWidth = int.TryParse(Environment.GetEnvironmentVariable("P06_WIDTH"), out var rw) ? rw : 320;
            var lw = ctx.LogicalSize.Width;
            Log($"CONTEXT scale={ctx.PixelScale} logical={ctx.LogicalSize.Width}x{ctx.LogicalSize.Height} fb={ctx.FramebufferSize.Width}x{ctx.FramebufferSize.Height} frames={ctx.RenderedFrameCount} requestedW={requestedWidth}");

            // Width evidence: actual logical width must match the request (not
            // a self-derived value), PixelScale must be the reported 1.5, and
            // the framebuffer width must be requested * 1.5.
            Check("logical-width", Math.Abs(lw - requestedWidth) < 1.5,
                $"logical={lw} expected requested={requestedWidth}");
            Check("pixel-scale-1.5", Math.Abs(ctx.PixelScale - 1.5) < 0.01,
                $"scale={ctx.PixelScale} expected 1.5");
            var expectFb = (int)Math.Round(requestedWidth * 1.5);
            Check("framebuffer-width", ctx.FramebufferSize.Width == expectFb,
                $"fb={ctx.FramebufferSize.Width} expected={expectFb} (requested {requestedWidth} x 1.5)");

            _hwnd = Win32Ime.GlfwGetWin32Window(GlfwWin32(UiApplication.CurrentHost!));
            Check("hwnd", _hwnd != IntPtr.Zero, $"hwnd=0x{_hwnd:X}");
            WaitNext(ctx, "first-frames");
            WaitForeground();
            Shot("00-initial");

            // ---- theme via the real Header theme button (top-of-page) --------
            // We NEVER write the dark/light State directly: the click is a real
            // SendInput gesture on the visible Header button.
            var darkWanted = theme == "dark";
            var darkNow = RootIsDark(ctx);
            if (darkWanted != darkNow)
            {
                var tbtn = FindButtonByLabel(ctx, darkWanted ? "暗色主题" : "明亮主题");
                Check("theme-button-found", tbtn is not null);
                if (tbtn is not null)
                {
                    var tb = FrameOf(ctx, tbtn);
                    Log($"themeBtn frame={tb}");
                    Click(tb.Center.X, tb.Center.Y);
                    WaitNext(ctx, "theme-toggle");
                    Check("theme-toggled", RootIsDark(ctx) == darkWanted,
                        $"theme {darkNow} -> {RootIsDark(ctx)} wanted {darkWanted}");
                }
            }
            else
            {
                Log($"theme already {theme} (no toggle needed)");
            }

            // ---- scroll the outer page so the demo Select is visible ---------
            ScrollToSelect(ctx, "请选择模式");
            Shot("01-scrolled");

            // ---- real Select open + anchor + viewport-band geometry ----------
            var select = FindSelect(ctx, "请选择模式");
            Check("select-found", select is not null, "mode select not found");
            Settle(ctx);
            var sFrame = FrameOf(ctx, select!);
            Log($"select frame={sFrame} viewport W={lw}");
            Click(sFrame.Center.X, sFrame.Center.Y);
            WaitNext(ctx, "select-open");
            Check("select-open-anchored", OverlayAnchoredAt(ctx, select!), "popup not anchored at select");
            var pop = OverlayPopupNode(ctx);
            Check("popup-node", pop is not null, "no popup node");
            var pFrame = pop is null ? Rect.Empty : FrameOf(ctx, pop);
            Log($"popup frame={pFrame} viewport=({lw}x{ctx.LogicalSize.Height})");
            // Popup must sit inside the usable width band [4, W-4].
            Check("popup-in-width-band",
                pFrame.Left >= 4 && pFrame.Right <= lw - 4 && pFrame.Width > 0,
                $"popup outside [4,{lw - 4}]: {pFrame}");
            Check("popup-in-viewport-y",
                pFrame.Top >= 0 && pFrame.Bottom <= ctx.LogicalSize.Height,
                $"popup outside viewport: {pFrame}");
            Shot("02-select-open");

            // ---- keyboard commit a DIFFERENT option than the initial ---------
            var before = StateValue<string?>(ctx, "popupSelectMode");
            Key(0x28); Key(0x0D); // Down, Enter
            WaitNext(ctx, "select-commit");
            var after = StateValue<string?>(ctx, "popupSelectMode");
            Log($"select value {before} -> {after}");
            Check("select-committed", !OverlayActive(ctx), "overlay still open");
            Check("select-value", after == "紧凑模式（窄屏）",
                $"value={after} expected 紧凑模式（窄屏） (initial {before})");
            Shot("03-select-committed");

            // ---- disabled select ignores a native click ----------------------
            var disabled = FindByView<Select<string?>>(ctx, s => s.Placeholder == "不可操作");
            Check("disabled-found", disabled is not null,
                "disabled select missing at this width (consumer gap)");
            if (disabled is not null)
            {
                EnsureVisible(ctx, disabled);
                var dv = StateValue<string?>(ctx, "popupSelectDisabled");
                var df = FrameOf(ctx, disabled);
                Click(df.Center.X, df.Center.Y);
                WaitNext(ctx, "disabled-click");
                Check("disabled-no-open", !OverlayActive(ctx), "disabled opened popup");
                Check("disabled-no-write", StateValue<string?>(ctx, "popupSelectDisabled") == dv, "disabled wrote value");
            }
            Shot($"04-{theme}");

            Log($"final frames={ctx.RenderedFrameCount} host-exited={uiExited.IsSet}");
            if (!uiExited.IsSet) ctx.RequestExit(0);
        }
        catch (Exception ex)
        {
            Log($"CONTROLLER EXCEPTION {ex}");
            Check("controller", false, ex.Message);
            try { _ctx?.RequestExit(1); } catch { }
        }
    }

    // Scroll the outer page until the named Select's frame is in view.
    private static void ScrollToSelect(UiHostContext ctx, string placeholder)
    {
        for (var i = 0; i < 30; i++)
        {
            var sel = FindSelect(ctx, placeholder);
            if (sel is not null)
            {
                var vf = FrameOf(ctx, sel);
                if (vf.Top > 30 && vf.Bottom < ctx.LogicalSize.Height - 30) return;
            }
            var page = OnUi(ctx, () =>
            {
                ViewNode? outer = null;
                var n = ctx.Runtime.Root;
                foreach (var d in SelfAnd(n)) { if (d.View is ScrollView) { outer = d; break; } }
                return outer;
            });
            if (page is null) return;
            var pf = FrameOf(ctx, page);
            Wheel(pf.Center.X, pf.Center.Y, -480);
            WaitFrames(ctx, ctx.RenderedFrameCount + 1, $"scroll{i}");
        }
    }

    private static void EnsureVisible(UiHostContext ctx, ViewNode node)
    {
        for (var i = 0; i < 25; i++)
        {
            var f = FrameOf(ctx, node);
            if (f.Top > 30 && f.Bottom < ctx.LogicalSize.Height - 30) return;
            var page = OnUi(ctx, () =>
            {
                ViewNode? outer = null;
                var p = node.Parent;
                while (p is not null) { if (p.View is ScrollView) outer = p; p = p.Parent; }
                return outer;
            });
            if (page is null) return;
            var pf = FrameOf(ctx, page);
            Wheel(pf.Center.X, pf.Center.Y, f.Top < 30 ? 480 : -480);
            WaitFrames(ctx, ctx.RenderedFrameCount + 1, $"ensure{i}");
        }
    }

    // ---------------- runtime observation helpers ----------------

    private static T OnUi<T>(UiHostContext ctx, Func<T> f)
    {
        var done = new ManualResetEventSlim();
        T result = default!;
        Exception? err = null;
        ctx.Dispatcher.Post(() =>
        {
            try { result = f(); }
            catch (Exception ex) { err = ex; }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        if (err is not null) throw err;
        return result;
    }

    private static IEnumerable<ViewNode> SelfAnd(ViewNode root)
    {
        yield return root;
        foreach (var n in root.Descendants()) yield return n;
    }

    private static ViewNode? FindByView<T>(UiHostContext ctx, Func<T, bool>? pred = null, ViewNode? scope = null)
        where T : View
        => OnUi(ctx, () =>
        {
            var root = scope ?? ctx.Runtime.Root;
            foreach (var n in SelfAnd(root))
            {
                if (n.View is T v && (pred is null || pred(v))) return n;
            }
            return null;
        });

    private static ViewNode? FindSelect(UiHostContext ctx, string placeholder)
        => FindByView<Select<string?>>(ctx, s => s.Placeholder == placeholder);

    private static ViewNode? FindButtonByLabel(UiHostContext ctx, string label)
        => OnUi(ctx, () =>
        {
            foreach (var n in SelfAnd(ctx.Runtime.Root))
            {
                if (n.View is Button b && b.Label == label) return n;
            }
            return null;
        });

    private static Rect FrameOf(UiHostContext ctx, ViewNode node) => OnUi(ctx, () => node.Frame);
    private static double ScrollOffsetY(UiHostContext ctx, ViewNode node) => OnUi(ctx, () => node.ScrollOffsetY);
    private static bool OverlayActive(UiHostContext ctx) => OnUi(ctx, () => ctx.Runtime.Overlay.HasActiveEntry);
    private static ViewNode? OverlayPopupNode(UiHostContext ctx) => OnUi(ctx, () => ctx.Runtime.Overlay.ActiveEntry?.PopupNode);

    private static bool OverlayAnchoredAt(UiHostContext ctx, ViewNode node)
        => OnUi(ctx, () =>
        {
            var a = ctx.Runtime.Overlay.ActiveEntry?.AnchorNode;
            if (a is null) return false;
            for (var p = a; p is not null; p = p.Parent)
            {
                if (ReferenceEquals(p, node)) return true;
            }
            return false;
        });

    private static T? StateValue<T>(UiHostContext ctx, string name)
        => OnUi(ctx, () =>
        {
            var demo = ctx.Runtime.Root.FindDescendant<PopupSelectDemo>();
            if (demo is null) return default;
            var st = demo.FindState<T>(name);
            return st is null ? default : st.Value;
        });

    private static bool RootIsDark(UiHostContext ctx)
        => OnUi(ctx, () => ctx.Runtime.Root.FindDescendant<PopupSelectDemo>()
            ?.Environment.ResolvedTheme.IsDark == true);

    // ---------------- native input (verbatim from native-observed) ----------

    private static int _baseline;
    private static void Mark(UiHostContext ctx) => _baseline = ctx.RenderedFrameCount;
    private static void WaitFrames(UiHostContext ctx, int target, string tag)
    {
        var sw = Stopwatch.StartNew();
        while (ctx.RenderedFrameCount < target && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(16);
        Log($"WAIT {tag} frames={ctx.RenderedFrameCount}");
    }
    private static void WaitNext(UiHostContext ctx, string tag) => WaitFrames(ctx, _baseline + 1, tag);

    private static void Settle(UiHostContext ctx)
    {
        var last = ctx.RenderedFrameCount;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(120);
            if (ctx.RenderedFrameCount == last) return;
            last = ctx.RenderedFrameCount;
        }
    }

    private static void WaitForeground()
    {
        var sw = Stopwatch.StartNew();
        while (Win32.GetForegroundWindow() != _hwnd && sw.Elapsed < TimeSpan.FromSeconds(5))
        { Win32.SetForegroundWindow(_hwnd); Thread.Sleep(50); }
    }

    private static (int sx, int sy) ToScreen(UiHostContext ctx, double lx, double ly)
    {
        var client = Win32.GetClientRectSize(_hwnd);
        var cx = (int)Math.Round(lx * client.W / ctx.LogicalSize.Width);
        var cy = (int)Math.Round(ly * client.H / ctx.LogicalSize.Height);
        Win32.ClientToScreen(_hwnd, ref cx, ref cy);
        return (cx, cy);
    }

    private static void Click(double lx, double ly)
    {
        Settle(_ctx!);
        var ctxv = _ctx!;
        if (ly < 0 || lx < 0 || ly > ctxv.LogicalSize.Height || lx > ctxv.LogicalSize.Width)
        {
            throw new InvalidOperationException($"click logical ({lx:F1},{ly:F1}) outside viewport {ctxv.LogicalSize}");
        }
        var (sx, sy) = ToScreen(ctxv, lx, ly);
        EnsureForegroundOrThrow("click");
        if (!PointIsOurs(sx, sy))
        {
            throw new InvalidOperationException($"click target ({sx},{sy}) not owned by pid {_ownPid}");
        }
        Mark(ctxv);
        Log($"CLICK logical=({lx:F1},{ly:F1}) screen=({sx},{sy})");
        Win32.SetCursorPos(sx, sy);
        Thread.Sleep(30);
        SendOrThrow(new[] { MouseEvent(Win32.MOUSEEVENTF_LEFTDOWN), MouseEvent(Win32.MOUSEEVENTF_LEFTUP) }, "click");
        Thread.Sleep(120);
    }

    private static int _ownPid = Environment.ProcessId;
    private static bool OwnsForeground() => Win32.GetForegroundWindow() == _hwnd;
    private static bool PointIsOurs(int sx, int sy)
    {
        var h = Win32.WindowFromPoint(new Win32.POINT { X = sx, Y = sy });
        if (h == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(h, out var pid);
        return pid == _ownPid;
    }
    private static void EnsureForegroundOrThrow(string op)
    {
        if (OwnsForeground()) return;
        Win32.SetForegroundWindow(_hwnd);
        Thread.Sleep(60);
        if (!OwnsForeground())
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
            Thread.Sleep(40);
            Win32.SetWindowPos(_hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
            Win32.SetForegroundWindow(_hwnd);
            Thread.Sleep(60);
        }
        if (!OwnsForeground())
        {
            throw new InvalidOperationException($"{op}: cannot activate own window (foreground=0x{Win32.GetForegroundWindow():X})");
        }
    }
    private static uint SendOrThrow(Win32.INPUT[] inputs, string op)
    {
        var n = Win32.SendInput((uint)inputs.Length, inputs, Win32.INPUT.Size);
        if (n != inputs.Length)
        {
            throw new InvalidOperationException($"{op}: SendInput injected {n}/{inputs.Length}");
        }
        return n;
    }

    private static void Wheel(double lx, double ly, int delta)
    {
        var (sx, sy) = ToScreen(_ctx!, lx, ly);
        EnsureForegroundOrThrow("wheel");
        if (!PointIsOurs(sx, sy))
        {
            throw new InvalidOperationException($"wheel target ({sx},{sy}) not ours");
        }
        Win32.SetCursorPos(sx, sy);
        Mark(_ctx!);
        var input = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        input.u.mi.mouseData = delta;
        input.u.mi.dwFlags = Win32.MOUSEEVENTF_WHEEL;
        SendOrThrow(new[] { input }, "wheel");
        Thread.Sleep(120);
    }

    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24
        or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;
    private static void Key(int vk)
    {
        var ext = IsExtended(vk) ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        var sc = Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_VSC);
        var d = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        d.u.ki.wVk = (ushort)vk; d.u.ki.wScan = (ushort)sc; d.u.ki.dwFlags = ext;
        var u = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        u.u.ki.wVk = (ushort)vk; u.u.ki.wScan = (ushort)sc; u.u.ki.dwFlags = Win32.KEYEVENTF_KEYUP | ext;
        EnsureForegroundOrThrow($"key 0x{vk:X}");
        Mark(_ctx!);
        var sent = SendOrThrow(new[] { d, u }, $"key 0x{vk:X}");
        Log($"KEY vk=0x{vk:X} sc=0x{sc:X} ext={ext} sent={sent}");
        Thread.Sleep(60);
    }

    private static Win32.INPUT MouseEvent(uint flags)
    {
        var i = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        i.u.mi.dwFlags = flags;
        return i;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        Log($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " :: " + detail)}");
        if (!ok) _fail++;
    }
    private static void Log(string m) => Console.WriteLine(m);
}

internal static class Win32
{
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002,
        MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_WHEEL = 0x0800;
    public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; public static readonly int Size = Marshal.SizeOf<INPUT>(); }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint map);
    public const uint MAPVK_VK_TO_VSC = 0;
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;

    public static (int W, int H) GetClientRectSize(IntPtr h)
    { GetClientRect(h, out var r); return (r.Right - r.Left, r.Bottom - r.Top); }
    public static void ClientToScreen(IntPtr h, ref int x, ref int y)
    { var p = new POINT { X = x, Y = y }; ClientToScreen(h, ref p); x = p.X; y = p.Y; }
}
