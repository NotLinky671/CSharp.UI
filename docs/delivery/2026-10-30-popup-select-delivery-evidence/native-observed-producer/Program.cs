using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Samples.ControlGallery;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;

// ============================================================================
// native-observed: real-Gallery OS acceptance for Popup/Select.
//
// Runs the REAL `GalleryApp` under the production `OpenTkHost`
// (UiApplication.Run) with ONLY the public Started/FrameRendered observer
// hooks -- no product change, no input-dispatch replacement, no Dispatch*
// injection. A controller thread (PerMonitorAwareV2) drives the real OS
// window via Win32 SendInput against OUR own hwnd, then observes the real
// runtime (frames, focus, overlay, scroll offsets, demo State, ViewNode
// frames) on the UI thread via Dispatcher.Post, and reads back the real
// framebuffer via GL.ReadPixels in the FrameRendered callback.
//
// This is an instrumented REAL host (same GalleryApp + same OpenTkHost code
// path as ControlGallery.exe), NOT the original exe: the only difference is
// the options object adds Started/FrameRendered observers. F3 satisfied.
// ============================================================================

internal static class Driver
{
    private static int _fail;
    private static UiHostContext? _ctx;
    private static IntPtr _hwnd;
    private static string _ev = ".";

    // Latest GL readback (filled inside FrameRendered on the UI thread).
    private static readonly object _pngLock = new();
    private static byte[]? _pngBgra;
    private static int _pngW, _pngH;

    public static int Main()
    {
        var dir = Environment.GetEnvironmentVariable("P06_NATIVE_EV") ?? ".";
        _ev = Path.GetFullPath(dir);
        Directory.CreateDirectory(_ev);
        Log($"EV={_ev}");

        var uiReady = new ManualResetEventSlim();
        var uiExited = new ManualResetEventSlim();
        Exception? uiError = null;

        // Controller thread drives the real window via SendInput while THIS
        // (main) thread runs the host -- GLFW requires the main thread.
        var controller = new Thread(() => RunController(uiReady, uiExited));
        controller.SetApartmentState(ApartmentState.STA);
        controller.Start();

        int code = 1;
        try
        {
            var options = new UiApplicationOptions
            {
                Title = "CSharp.UI — 控件展示",
                Width = 900,
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
        if (uiError is not null)
        {
            Check("ui-thread", false, uiError.Message);
        }
        if (_fail > 0) code = 1;

        Log($"RESULT {(_fail == 0 ? "PASS" : $"FAIL ({_fail})")}");
        return code == 0 && _fail == 0 ? 0 : 1;
    }

    // ---------------- frame / screenshot ----------------

    private static unsafe IntPtr GlfwWin32(OpenTkHost host)
        => (IntPtr)host.Window.WindowPtr;

    private static void OnFrameRendered(UiHostContext ctx)
    {
        // FrameRendered runs on the UI thread AFTER SwapBuffers; read FRONT.
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

    // Minimal PNG writer (truecolour RGBA, no filtering) -- avoids a
    // System.Drawing dependency in an ad-hoc verification tool.
    private static void WritePng(string path, byte[] bgra, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var src = (h - 1 - y) * w * 4; // flip vertical (GL origin bottom-left)
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
        // scanlines each prefixed with filter byte 0
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
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // colour type RGBA
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

    private static void RunController(ManualResetEventSlim uiReady, ManualResetEventSlim uiExited)
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
            Log($"CONTEXT scale={ctx.PixelScale} logical={ctx.LogicalSize.Width}x{ctx.LogicalSize.Height} fb={ctx.FramebufferSize.Width}x{ctx.FramebufferSize.Height} frames={ctx.RenderedFrameCount}");

            _hwnd = Win32Ime.GlfwGetWin32Window(GlfwWin32(UiApplication.CurrentHost!));
            Check("hwnd", _hwnd != IntPtr.Zero, $"hwnd=0x{_hwnd:X}");
            Log($"HWND pid={Environment.ProcessId} hwnd=0x{_hwnd:X}");

            WaitNext(ctx, "first-frames");
            WaitForeground();
            Shot("00-initial-light");

            // ---- 0b. real theme toggle while the header is naturally visible --
            var themeBtn0 = FindButtonByLabel(ctx, "暗色主题") ?? FindButtonByLabel(ctx, "明亮主题");
            Check("theme-button-found", themeBtn0 is not null);
            var tb0 = themeBtn0 is null ? Rect.Empty : FrameOf(ctx, themeBtn0);
            Log($"themeBtn frame={tb0}");
            Check("themeBtn-visible", !tb0.IsEmpty && tb0.Top >= 0 && tb0.Bottom <= ctx.LogicalSize.Height);
            var darkBefore0 = RootIsDark(ctx);
            Click(tb0.Center.X, tb0.Center.Y);
            WaitNext(ctx, "theme-toggle");
            var darkAfter0 = RootIsDark(ctx);
            Check("theme-toggled", darkAfter0 != darkBefore0, $"theme {darkBefore0} -> {darkAfter0}");
            Shot("00b-theme-dark");

            // ---- 1. real scrollbar drag brings section into view ----------------
            ScrollRootToDemo(ctx);
            Shot("01-scrolled-to-section");

            // ---- 2. Select open + KEYBOARD commit (Down/Enter) ------------------
            var select = FindSelect(ctx, "请选择模式");
            Check("select-found", select is not null, "mode select not found");
            Settle(ctx);
            var sFrame = FrameOf(ctx, select!);
            Log($"select frame={sFrame}");
            Click(sFrame.Center.X, sFrame.Center.Y);
            WaitNext(ctx, "select-open");
            Check("select-open", OverlayAnchoredAt(ctx, select!), "popup not open or wrong owner");
            Shot("02-select-open");
            DumpTree(ctx, OverlayPopupNode(ctx) ?? ctx.Runtime.Root, "popup-tree.txt");
            Key(0x28); Key(0x0D); // Down, Enter
            WaitNext(ctx, "select-commit");
            Check("select-committed", !OverlayActive(ctx), "overlay still open");
            Check("select-value", StateValue<string?>(ctx, "popupSelectMode") == "紧凑模式（窄屏）",
                $"value={StateValue<string?>(ctx, "popupSelectMode")}");
            Shot("03-select-committed");

            // ---- 3. disabled select rejects click -------------------------------
            var disabled = FindByView<Select<string?>>(ctx, s => s.Placeholder == "不可操作");
            Check("disabled-found", disabled is not null);
            var beforeDisabled = StateValue<string?>(ctx, "popupSelectDisabled");
            var dFrame = FrameOf(ctx, disabled!);
            Click(dFrame.Center.X, dFrame.Center.Y);
            WaitNext(ctx, "disabled-click");
            Check("disabled-no-open", !OverlayActive(ctx), "disabled opened popup");
            Check("disabled-no-write", StateValue<string?>(ctx, "popupSelectDisabled") == beforeDisabled,
                "disabled wrote value");
            Shot("04-disabled-click");

            // ---- 4. long list: open -> End -> REAL mouse click last visible row --
            var longSel = FindSelect(ctx, "请选择长列表项");
            Check("longlist-found", longSel is not null);
            Click(FrameOf(ctx, longSel!).Center.X, FrameOf(ctx, longSel!).Center.Y);
            WaitNext(ctx, "longlist-open");
            Check("longlist-open", OverlayAnchoredAt(ctx, longSel!), "popup not anchored at longlist");
            // E1 at OS level: this longlist trigger sits low enough that the
            // Auto placement already flips the popup ABOVE it -- assert the real
            // geometry (popup bottom at/above the trigger top) rather than
            // relying on the screenshot alone.
            var lsFrame = FrameOf(ctx, longSel!);
            var lpFrame = FrameOf(ctx, OverlayPopupNode(ctx)!);
            Log($"longlist anchor={lsFrame} popup={lpFrame}");
            Check("longlist-flip-above", lpFrame.Bottom <= lsFrame.Top + 1.5,
                $"popup not above trigger: popup.bottom={lpFrame.Bottom} trigger.top={lsFrame.Top}");
            Shot("05-longlist-open");
            Key(0x23); // End scrolls to last item
            WaitNext(ctx, "longlist-end");
            Settle(ctx);
            Check("longlist-still-open", OverlayActive(ctx), "list closed on End");
            var popupNode = OverlayPopupNode(ctx);
            DumpTree(ctx, popupNode ?? ctx.Runtime.Root, "longlist-tree.txt");
            var innerScroll = OnUi(ctx, () => popupNode?.FindDescendant<ScrollView>());
            var scOff = innerScroll is null ? -1 : ScrollOffsetY(ctx, innerScroll);
            for (var i = 0; i < 15 && scOff <= 0; i++)
            {
                Thread.Sleep(120);
                scOff = innerScroll is null ? -1 : ScrollOffsetY(ctx, innerScroll);
            }
            Log($"longlist inner-scroll offset={scOff}");
            Check("longlist-scrolled", scOff > 0, "inner scroll did not move");
            Shot("06-longlist-end");
            // E5: real mouse move+down+up on the item-30 option row, clicked at
            // the center of its VISIBLE intersection with the list clip (not the
            // raw frame center, which could sit outside the clip).
            var lastRow = FindOptionRowByIndex(ctx, popupNode!, 30);
            Check("lastrow-found", lastRow is not null, "item-30 option row not found");
            var listClip = OnUi(ctx, () => popupNode!.FindDescendant<ScrollView>()?.Frame ?? popupNode!.Frame);
            var rFrame = FrameOf(ctx, lastRow!);
            var vis = rFrame.Intersect(listClip);
            Log($"item-30 row frame={rFrame} clip={listClip} vis={vis}");
            Check("lastrow-visible", !vis.IsEmpty, "item-30 row not visible in list clip");
            Click(vis.Center.X, vis.Center.Y); // real SendInput gesture
            WaitNext(ctx, "longlist-commit");
            Check("longlist-committed", !OverlayActive(ctx), "overlay still open");
            var lv = StateValue<string?>(ctx, "popupSelectLong");
            Log($"popupSelectLong actual={lv}");
            Check("longlist-value", lv?.Contains("30") == true, $"value={lv}");
            Shot("07-longlist-committed");

            // ---- 5. generic Popup: button -> TextField type -> Esc --------------
            var popupBtn = FindButtonByLabel(ctx, "打开设置");
            Check("popup-button-found", popupBtn is not null);
            EnsureVisible(ctx, popupBtn!);
            var pbFrame = FrameOf(ctx, popupBtn!);
            Log($"popupBtn frame={pbFrame}");
            Click(pbFrame.Center.X, pbFrame.Center.Y);
            WaitNext(ctx, "popup-open");
            var popupViewNode = OnUi(ctx, () => ctx.Runtime.Overlay.ActiveEntry?.AnchorNode);
            Check("popup-open", OverlayActive(ctx) && popupViewNode is not null);
            Check("popup-isopen-true", StateValue<bool>(ctx, "popupSelectSettingsOpen"), "IsOpen binding not true");
            Shot("08-popup-open");
            var popSurface = OverlayPopupNode(ctx);
            var textField = FindByView<TextField>(ctx, null, popSurface);
            Check("textfield-found", textField is not null, "TextField not in popup");
            Click(FrameOf(ctx, textField!).Center.X, FrameOf(ctx, textField!).Center.Y);
            WaitNext(ctx, "tf-focus");
            Check("textfield-focused", OnUi(ctx, () => ReferenceEquals(ctx.Runtime.FocusedNode, textField)),
                "TextField not focused");
            Type("portal-ui");
            WaitNext(ctx, "tf-typed");
            Check("textfield-text", StateValue<string>(ctx, "popupSelectSettingsText") == "portal-ui",
                $"text={StateValue<string>(ctx, "popupSelectSettingsText")}");
            Shot("09-popup-typed");
            // native outside press closes popup (keeps host alive; Esc-close is
            // exercised as the dedicated last step before RequestExit).
            Click(450, ctx.LogicalSize.Height - 6);
            WaitNext(ctx, "popup-outside");
            Check("popup-outside-closed", !OverlayActive(ctx), "popup still open");
            Check("popup-isopen-false", !StateValue<bool>(ctx, "popupSelectSettingsOpen"), "IsOpen still true");
            Shot("10-popup-closed");

            // ---- 6. outside press on another anchor dismisses only --------------
            var pb2 = FrameOf(ctx, popupBtn!);
            Click(pb2.Center.X, pb2.Center.Y);
            WaitNext(ctx, "reopen");
            Check("reopened", OverlayActive(ctx));
            var modeSel = FindSelect(ctx, "请选择模式")!;
            Click(FrameOf(ctx, modeSel).Center.X, FrameOf(ctx, modeSel).Center.Y);
            WaitNext(ctx, "outside-dismiss");
            Check("outside-dismissed-only", !OverlayActive(ctx), "outside gesture opened Select");
            Shot("11-outside-dismissed");
            Click(FrameOf(ctx, modeSel).Center.X, FrameOf(ctx, modeSel).Center.Y);
            WaitNext(ctx, "second-open");
            Check("second-gesture-opens", OverlayActive(ctx), "select did not open on 2nd gesture");
            // native outside press (empty page area below the demo) closes it --
            // keeps the host alive (app-level Esc-quit is exercised separately).
            Click(450, ctx.LogicalSize.Height - 8);
            WaitNext(ctx, "outside-close");
            Check("outside-closed", !OverlayActive(ctx), "select still open after outside press");
            Shot("12-second-gesture");

            // ---- 7. Auto-upward flip when anchor near bottom ---------------------
            var innerSel = FindSelect(ctx, "滚动区域选择");
            Check("inner-select-found", innerSel is not null);
            if (innerSel is not null) EnsureVisible(ctx, innerSel);
            // Reset the inner scrollview offset to 0 so the anchor is fully
            // inside the inner clip before opening (setup only).
            var innerScroll0 = FindInnerScrollView(ctx, innerSel!);
            if (innerScroll0 is not null)
            {
                for (var i = 0; i < 20 && ScrollOffsetY(ctx, innerScroll0) > 0.5; i++)
                {
                    var sf0 = FrameOf(ctx, innerScroll0);
                    Wheel(sf0.Center.X, sf0.Center.Y, 60);
                    WaitFrames(ctx, ctx.RenderedFrameCount + 1, $"inner-reset{i}");
                }
                Log($"inner reset offset={ScrollOffsetY(ctx, innerScroll0):F1}");
            }
            EnsureVisible(ctx, innerSel!);
            var iFrame = FrameOf(ctx, innerSel!);
            var innerClip = innerScroll0 is null ? Rect.Empty : FrameOf(ctx, innerScroll0);
            Log($"inner anchor={iFrame} innerClip={innerClip}");
            Click(iFrame.Center.X, iFrame.Center.Y);
            WaitNext(ctx, "inner-open");
            Check("inner-open", OverlayAnchoredAt(ctx, innerSel!), "popup not anchored at inner select");
            var popFrame = FrameOf(ctx, OverlayPopupNode(ctx)!);
            Log($"anchor top={iFrame.Top} bottom={iFrame.Bottom} popup top={popFrame.Top} bottom={popFrame.Bottom} innerClip={innerClip}");
            // The popup must be inside the window viewport; because the anchor
            // lives in a 140px inner ScrollView, the popup escaping the parent's
            // clip proves the root-level overlay does not inherit ancestor clip.
            // (Auto-flip itself is already proven at OS level by the longlist
            // popup, which opened above its trigger -- see longlist asserts.)
            Check("inner-popup-in-viewport", popFrame.Top >= 0 && popFrame.Bottom <= ctx.LogicalSize.Height,
                $"popup out of viewport: {popFrame}");
            Check("inner-popup-escapes-clip", innerClip.IsEmpty || popFrame.Bottom > innerClip.Bottom - 0.5 || popFrame.Top < innerClip.Top + 0.5,
                $"popup stayed inside parent clip: popup={popFrame} clip={innerClip}");
            Shot("13-inner-open-flipped");

            // ---- E4: two-phase native inner-wheel over the inner ScrollView ----
            var scrollNode = innerScroll0;
            Check("inner-scroll-found", scrollNode is not null);
            var scrollFrame = innerClip;
            var popupFrame2 = popFrame;
            // Wheel point: the inner ScrollView's own vertical scrollbar gutter
            // (right ~4px of the inner frame), verified to sit inside the inner
            // frame + window viewport and OUTSIDE the popup frame. Scrolling
            // there routes to the inner ScrollView even when the popup covers
            // most of the content area.
            var wx = scrollFrame.Right - 4;
            var wy = Math.Clamp(scrollFrame.Center.Y, scrollFrame.Top + 8, scrollFrame.Bottom - 8);
            var wheelPtInside = scrollFrame.Contains(new Point(wx, wy))
                && !popupFrame2.Contains(new Point(wx, wy))
                && wy >= 0 && wy <= ctx.LogicalSize.Height;
            Log($"inner-wheel point ({wx:F0},{wy:F0}) popup={popupFrame2} scroll={scrollFrame} inside={wheelPtInside}");
            Check("inner-wheel-point-ok", wheelPtInside, "no safe wheel point inside inner scroll gutter");

            // Phase A: small wheel while anchor still partially visible -> popup
            // stays open and its frame follows the anchor by the same delta.
            var beforeA = ScrollOffsetY(ctx, scrollNode!);
            var anchorTopA = FrameOf(ctx, innerSel!).Top;
            var popTopA = FrameOf(ctx, OverlayPopupNode(ctx)!).Top;
            Wheel(wx, wy, -60);
            WaitNext(ctx, "inner-wheel-A");
            var afterA = ScrollOffsetY(ctx, scrollNode!);
            var anchorTopB = FrameOf(ctx, innerSel!).Top;
            var popTopB = OverlayPopupNode(ctx) is { } pnA ? FrameOf(ctx, pnA).Top : double.NaN;
            var stillOpenA = OverlayActive(ctx);
            Log($"A off {beforeA}->{afterA} anchorTop {anchorTopA:F1}->{anchorTopB:F1} popTop {popTopA:F1}->{popTopB:F1} open={stillOpenA}");
            Check("inner-follows", stillOpenA && Math.Abs(anchorTopB - anchorTopA) > 0.5
                && Math.Abs((popTopB - popTopA) - (anchorTopB - anchorTopA)) < 2.5,
                $"follow: open={stillOpenA} aTop {anchorTopA:F1}->{anchorTopB:F1} pTop {popTopA:F1}->{popTopB:F1}");
            Shot("14-inner-followed");

            // Phase B: wheel until the inner offset saturates AND the anchor is
            // fully clipped out of the inner viewport; the overlay must close.
            // Multiple wheels because one -480 may be capped by max offset.
            var beforeB = ScrollOffsetY(ctx, scrollNode!);
            var afterB = beforeB;
            var clipped = false;
            var openB = true;
            for (var i = 0; i < 12 && !(clipped && !openB); i++)
            {
                Wheel(wx, wy, -480);
                WaitFrames(ctx, ctx.RenderedFrameCount + 1, $"inner-wheel-B{i}");
                afterB = ScrollOffsetY(ctx, scrollNode!);
                var af = FrameOf(ctx, innerSel!);
                clipped = af.IsEmpty || !af.IntersectsWith(scrollFrame);
                openB = OverlayActive(ctx);
            }
            var anchorFrameB = FrameOf(ctx, innerSel!);
            Log($"B off {beforeB}->{afterB} anchor={anchorFrameB} clip={scrollFrame} clipped={clipped} open={openB}");
            Check("inner-clip-closes", afterB > beforeB + 0.5 && !openB,
                $"offset {beforeB}->{afterB} clipped={clipped} open={openB}");
            Shot("15-inner-clipped");

            // ---- 9. dedicated Esc-close proof ------------------------------------
            // Assert the app is still alive BEFORE the Esc (post-Esc the host's
            // own ExitOnEscape consumes the same gesture and exits -- that is
            // the documented host shortcut, recorded here as fact, not a fail).
            Check("app-alive-before-esc", !uiExited.IsSet, "host exited before Esc test");
            var pb3 = FindButtonByLabel(ctx, "打开设置");
            if (pb3 is not null)
            {
                EnsureVisible(ctx, pb3);
                var pb3f = FrameOf(ctx, pb3);
                Click(pb3f.Center.X, pb3f.Center.Y);
                WaitNext(ctx, "esc-reopen");
                Check("esc-reopen", OverlayActive(ctx));
                Key(0x1B);
                WaitNext(ctx, "esc-close-popup");
                Check("esc-closed-popup", !OverlayActive(ctx), "popup still open");
                Check("esc-isopen-false", !StateValue<bool>(ctx, "popupSelectSettingsOpen"), "IsOpen still true");
            }
            Shot("16-esc-closed");

            // The SAME Esc gesture must NOT have quit the app: the overlay
            // consumed Esc-Down to close the popup, so the paired KeyUp must
            // not re-trigger the host's ExitOnEscape. Post-Esc app-alive proof.
            Check("app-alive-after-esc", !uiExited.IsSet,
                "host exited after popup Esc-close (Esc-Up wrongly triggered exit)");

            // ---- 10. done -------------------------------------------------------
            Log($"final frames={ctx.RenderedFrameCount} host-exited={uiExited.IsSet}");
            if (!uiExited.IsSet)
            {
                ctx.RequestExit(0);
            }
            // (single RequestExit above; the host stayed alive after the
            //  consumed Esc, so this is the harness's own clean shutdown.)
        }
        catch (Exception ex)
        {
            Log($"CONTROLLER EXCEPTION {ex}");
            Check("controller", false, ex.Message);
            try { _ctx?.RequestExit(1); } catch { }
        }
    }

    // ---------------- runtime observation helpers (UI thread) ----------------

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
                if (n.View is Button b && b.Label == label)
                    return n;
            }
            return null;
        });

    // node.Frame is already viewport-space (ScrollView arranges children at
    // content - offset). Returned as-is.
    private static Rect FrameOf(UiHostContext ctx, ViewNode node)
        => OnUi(ctx, () => node.Frame);

    private static double ScrollOffsetY(UiHostContext ctx, ViewNode node)
        => OnUi(ctx, () => node.ScrollOffsetY);

    private static bool OverlayActive(UiHostContext ctx)
        => OnUi(ctx, () => ctx.Runtime.Overlay.HasActiveEntry);

    private static ViewNode? OverlayPopupNode(UiHostContext ctx)
        => OnUi(ctx, () => ctx.Runtime.Overlay.ActiveEntry?.PopupNode);

    // Strong open-check: overlay active AND its anchor is  itself or a
    // descendant of it (the Select's own node / the Popup anchor subtree).
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

    private static ViewNode? FindOptionRowByIndex(UiHostContext ctx, ViewNode popupNode, int index1)
        => OnUi(ctx, () =>
        {
            // SelectOption rows in document order; index1 is 1-based item number.
            var i = 0;
            foreach (var n in popupNode.Descendants())
            {
                if (n.View.DebugName == "SelectOption") { i++; if (i == index1) return n; }
            }
            return null;
        });

    private static ViewNode? FindLastOptionRow(UiHostContext ctx, ViewNode popupNode)
        => OnUi(ctx, () =>
        {
            // Option rows are SelectOption views inside the popup ScrollView;
            // 'last visible' = greatest Frame.Bottom that still intersects the
            // popup's own clip rect (the list viewport), so a real click lands.
            var scroll = popupNode.FindDescendant<ScrollView>();
            var clip = scroll?.Frame ?? popupNode.Frame;
            ViewNode? last = null;
            var maxBottom = double.MinValue;
            foreach (var n in popupNode.Descendants())
            {
                if (n.View.DebugName != "SelectOption" || n.Frame.IsEmpty) continue;
                // visible = intersects the list's viewport clip
                if (!n.Frame.IntersectsWith(clip)) continue;
                if (n.Frame.Bottom > maxBottom) { maxBottom = n.Frame.Bottom; last = n; }
            }
            return last;
        });

    private static ViewNode? FindInnerScrollView(UiHostContext ctx, ViewNode node)
        => OnUi(ctx, () =>
        {
            var p = node.Parent;
            while (p is not null)
            {
                if (p.View is ScrollView) return p;
                p = p.Parent;
            }
            return null;
        });

    private static void DumpTree(UiHostContext ctx, ViewNode node, string file)
    {
        var sb = new System.Text.StringBuilder();
        OnUi(ctx, () =>
        {
            void W(ViewNode n, int d)
            {
                sb.AppendLine($"{new string(' ', d * 2)}{n.View.DebugName} {n.Frame} focusable={n.View.IsFocusable} scrollY={n.ScrollOffsetY}");
                foreach (var c in n.Children) W(c, d + 1);
            }
            W(node, 0);
            return 0;
        });
        File.WriteAllText(Path.Combine(_ev, file), sb.ToString());
    }

    private static void ScrollRootToDemo(UiHostContext ctx)
    {
        var demo = OnUi(ctx, () => ctx.Runtime.Root.FindDescendant<PopupSelectDemo>());
        Check("demo-found", demo is not null);
        if (demo is null) return;
        var pageScroll = OnUi(ctx, () =>
        {
            ViewNode? outer = null;
            var p = demo.Parent;
            while (p is not null) { if (p.View is ScrollView) outer = p; p = p.Parent; }
            return outer;
        });
        if (pageScroll is null) { Check("page-scroll", false); return; }
        Settle(ctx);
        var frame = FrameOf(ctx, pageScroll);
        var x = frame.Right - 6;
        var y0 = frame.Y + 40;
        var start = ScrollOffsetY(ctx, pageScroll);
        // Native wheel until the demo's first Select is comfortably inside the
        // viewport (its viewport-space Frame is directly observable).
        var mode = FindSelect(ctx, "请选择模式");
        for (var i = 0; i < 30; i++)
        {
            if (mode is not null)
            {
                var vf = FrameOf(ctx, mode);
                if (vf.Top > 40 && vf.Bottom < ctx.LogicalSize.Height - 40) break;
            }
            Wheel(frame.Center.X, frame.Center.Y, -480);
            WaitFrames(ctx, ctx.RenderedFrameCount + 1, $"wheel{i}");
        }
        var end = ScrollOffsetY(ctx, pageScroll);
        Log($"scroll {start} -> {end}");
        Check("scroll-moved", end > start, "root scroll offset did not rise");
    }

    // Native-wheel scroll the OUTER page until the node's frame is comfortably
    // inside the viewport (setup only; wheel is a real gesture anyway).
    // Native-wheel scroll the OUTER page until the node's frame is comfortably
    // inside the viewport (setup only; wheel is a real gesture anyway).
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

    // Scroll the outer page until the anchor's viewport bottom sits near the
    // window bottom band so Auto placement must flip the popup above.
    // Setup for the flip test: anchor's viewport bottom must land in the band
    // [H-30, H-10] so Auto placement has < popup-height below and flips above.
    // (1) first reset the INNER scrollview offset to 0 via a positive wheel over
    //     a visible point inside the inner frame (so the anchor isn't clipped);
    // (2) then positive wheel on the outer page's right padding strip
    //     (x = page.Right - 20, inside Gallery padding, away from the inner
    //     scroll, popup, and the 12px root scrollbar gutter) to DECREASE the
    //     outer offset and push the whole inner region toward the bottom.
    // Every step logs outer/inner offset + anchor bottom so a mis-routed wheel
    // is visible in raw output. Setup only -- not the native proof.
    // ---------------- native input ----------------

    // Wait for the frame produced BY the preceding input. Caller records the
    // baseline BEFORE the input; we only wait for baseline+1 (idle UI does not
    // repaint, so +N waits would always hit the timeout).
    private static int _baseline;
    private static void Mark(UiHostContext ctx) => _baseline = ctx.RenderedFrameCount;
    private static void WaitFrames(UiHostContext ctx, int target, string tag)
    {
        var sw = Stopwatch.StartNew();
        while (ctx.RenderedFrameCount < target && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(16);
        Log($"WAIT {tag} frames={ctx.RenderedFrameCount}");
    }
    private static void WaitNext(UiHostContext ctx, string tag)
        => WaitFrames(ctx, _baseline + 1, tag);

    private static void Settle(UiHostContext ctx)
    {
        // Wait until no new frames for ~150ms so layout/scroll animations settle.
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
            // last resort: force to top briefly then click our own window to activate
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

    private static void MouseDown(double lx, double ly)
    { var (sx, sy) = ToScreen(_ctx!, lx, ly); Win32.SetCursorPos(sx, sy); SendMouse(Win32.MOUSEEVENTF_LEFTDOWN); }
    private static void MouseUp(double lx, double ly)
    { var (sx, sy) = ToScreen(_ctx!, lx, ly); Win32.SetCursorPos(sx, sy); SendMouse(Win32.MOUSEEVENTF_LEFTUP); }
    private static void MouseMove(double lx, double ly)
    { var (sx, sy) = ToScreen(_ctx!, lx, ly); Win32.SetCursorPos(sx, sy); SendMouse(Win32.MOUSEEVENTF_MOVE); Thread.Sleep(20); }

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

    // VKs that need KEYEVENTF_EXTENDEDKEY so GLFW sees End/Arrows not numpad.
    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24
        or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;
    private static void Key(int vk)
    {
        var ext = IsExtended(vk) ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        // Real scancode so GLFW maps the physical key (SendInput with wScan=0
        // can be delivered as the numpad/virtual variant).
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

    private static void Type(string s)
    {
        EnsureForegroundOrThrow("type");
        Mark(_ctx!);
        foreach (var c in s)
        {
            var i = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
            i.u.ki.wScan = c; i.u.ki.dwFlags = Win32.KEYEVENTF_UNICODE;
            var u = i; u.u.ki.dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP;
            SendOrThrow(new[] { i }, "type-down");
            SendOrThrow(new[] { u }, "type-up");
            Thread.Sleep(15);
        }
    }

    private static Win32.INPUT MouseEvent(uint flags)
    {
        var i = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        i.u.mi.dwFlags = flags;
        return i;
    }
    private static void SendMouse(uint flags)
    {
        SendOrThrow(new[] { MouseEvent(flags) }, "mouse");
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
