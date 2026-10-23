using System.Reflection;
using CSharp.UI.ImeProbe;
using CSharp.UI.TsfHostCheck;

internal static class Program
{
    private static int _total, _failed;
    private static void Check(string name,bool ok,object? detail=null) {
        _total++; if(!ok)_failed++;
        Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}: {detail}");
    }
    private static bool Same(TsfRect actual,NativeOracle.Rect expected)
        => actual.Left==expected.Left&&actual.Top==expected.Top&&actual.Right==expected.Right&&actual.Bottom==expected.Bottom;
    private static nint Handle(object host,string name)
        => (nint)(host.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(host)
            ?? throw new Exception($"Missing audit wiring property {name}"));
    private static bool Create(TsfHostWindow window)
    {
        var method=typeof(TsfHostWindow).GetMethod("Create",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var parameters=method.GetParameters();
        if(!parameters.Any(p=>p.ParameterType==typeof(bool)&&p.Name=="childInputSurface"))
            throw new Exception("Audit factory requires connection to actual childInputSurface option; inspect implementation before adapting factory.");
        return (bool)method.Invoke(window,parameters.Select(p=>p.Name switch {
            "width" => (object)760,
            "height" => 420,
            "title" => "CSharp.UI R29 geometry review (no input)",
            "show" => false,
            "childInputSurface" => true,
            _ when p.HasDefaultValue => p.DefaultValue,
            _ => throw new Exception("Unknown Create parameter "+p.Name)
        }).ToArray())!;
    }
    private static int Main()
    {
        var initialForeground=NativeOracle.GetForegroundWindow();
        Directory.CreateDirectory("artifacts/ime-work/ti07-child-surface-codex-audit/output");
        using var log=new ProbeLog("artifacts/ime-work/ti07-child-surface-codex-audit/output/native.jsonl",echo:false);
        TsfTrace trace=(_,_,_)=>{};
        var store=new AcpTextStore(trace);
        var recorder=new BindingRecorder(store,trace);
        using var window=new TsfHostWindow(store,log,trace);
        Exception? error=null;
        nint input=0,viewport=0,root=0;
        var thread=new Thread(()=> {
            try {
                if(!Create(window))throw new Exception("Hidden Create failed");
                window.RunMessageLoop();
            } catch(Exception ex) {error=ex;window.LoopStarted.Set();}
        }){IsBackground=true};
        thread.Start();
        if(!window.LoopStarted.Wait(TimeSpan.FromSeconds(12)))throw new Exception("UI loop timeout");
        try {
            if(error is not null)throw error;
            window.Invoke(()=> {
                root=window.Hwnd; input=Handle(window,"InputHwnd");viewport=Handle(window,"ViewportHwnd");
                Check("own process manifest uses PerMonitorV2",DpiNative.IsPerMonitorV2(),window.CurrentDpi);
                Check("three genuine different windows",root!=0&&input!=0&&viewport!=0&&input!=root&&viewport!=root&&input!=viewport);
                Check("actual parent chain",NativeOracle.GetParent(input)==viewport&&NativeOracle.GetParent(viewport)==root);
                var tid=NativeOracle.GetWindowThreadProcessId(input,out var pid);
                Check("input process/thread ownership",pid==Environment.ProcessId&&tid==window.UiThreadId);
                Check("created hidden without ShowWindow",!NativeOracle.IsWindowVisible(root)&&window.ShowWindowCalls==0);
                var hr=store.GetWnd(1,out var storeWindow);
                Check("actual GetWnd input",hr==0&&storeWindow==input,$"0x{storeWindow:X}");
                hr=store.GetScreenExt(1,out var hiddenRect);
                Check("hidden surface query no lock",hr==0&&hiddenRect.Equals(default(TsfRect)),hiddenRect);
                NativeOracle.ShowWindow(root,4); // SW_SHOWNOACTIVATE: own tool only, no focus request.
                NativeOracle.ShowWindow(root,7); // SW_SHOWMINNOACTIVE on the own test window.
                hr=store.GetScreenExt(1,out var minimizedRect);
                Check("minimized root hides child surface without lock",hr==0&&minimizedRect.Equals(default(TsfRect)),minimizedRect);
                NativeOracle.ShowWindow(root,4);
                NativeOracle.InvalidateRect(viewport,0,false);
                Check("own viewport update region established",NativeOracle.GetUpdateRect(viewport,out _,false));
                NativeOracle.SendMessage(viewport,0x000F,0,0);
                Check("viewport WM_PAINT validates actual update region",!NativeOracle.GetUpdateRect(viewport,out _,false));
                store.ResetDocument("sample");window.SetScroll(0,0);
            });
            window.Invoke(()=>{});
            window.Invoke(()=> {
                var originalFocus=NativeOracle.GetFocus();
                var rootBefore=NativeOracle.Window(root);var viewBefore=NativeOracle.Window(viewport);
                var inputBefore=NativeOracle.Client(input);var rowBefore=window.LastLayout!.TextRowScreen;
                var expected=NativeOracle.VisibleIntersection(input,viewport,root);
                var hr=store.GetScreenExt(1,out var before);
                Check("visible actual ancestor intersection",hr==0&&Same(before,expected)&&expected.Height>0,$"{before} expected{expected}");
                var viewClientBefore=NativeOracle.Client(viewport);var rootClientBefore=NativeOracle.Client(root);
                try {
                    Check("review own viewport movement succeeds",NativeOracle.SetWindowPos(viewport,0,viewClientBefore.Left-rootClientBefore.Left,rootClientBefore.Height-10,0,0,0x15));
                    var ancestorExpected=NativeOracle.VisibleIntersection(input,viewport,root);
                    var ancestorHr=store.GetScreenExt(1,out var ancestorSurface);
                    Check("surface intersects root as well as immediate viewport",ancestorHr==0&&Same(ancestorSurface,ancestorExpected),$"{ancestorSurface} expected{ancestorExpected}");
                } finally {
                    NativeOracle.SetWindowPos(viewport,0,viewClientBefore.Left-rootClientBefore.Left,viewClientBefore.Top-rootClientBefore.Top,0,0,0x15);
                    window.SetScroll(0,0);
                }
                store.InvalidateLayout("review-text-invalid");
                hr=store.GetScreenExt(1,out var independentSurface);
                Check("surface survives text invalidation without lock",hr==0&&Same(independentSurface,expected));
                Check("GetTextExt still refuses no read lock",store.GetTextExt(1,0,1,out _,out _)==TsfHResult.TS_E_NOLOCK);
                window.ScrollBy(11);
                var inputAfter=NativeOracle.Client(input);var rowAfter=window.LastLayout!.TextRowScreen;
                Check("input moved exactly11",inputAfter.Left==inputBefore.Left&&inputAfter.Top==inputBefore.Top-11&&inputAfter.Width==inputBefore.Width&&inputAfter.Height==inputBefore.Height,$"{inputBefore}->{inputAfter}");
                Check("row moved exactly11 without double subtraction",rowAfter.Left==rowBefore.Left&&rowAfter.Top==rowBefore.Top-11,$"{rowBefore}->{rowAfter}");
                Check("local text padding stable",rowAfter.Top-inputAfter.Top==rowBefore.Top-inputBefore.Top);
                Check("root/viewport stationary",rootBefore.Equals(NativeOracle.Window(root))&&viewBefore.Equals(NativeOracle.Window(viewport)));
                expected=NativeOracle.VisibleIntersection(input,viewport,root);
                hr=store.GetScreenExt(1,out var partial);
                Check("partial actual surface clipping",hr==0&&Same(partial,expected),$"{partial} expected{expected}");
                window.SetScroll(0,0);
                var fullDelta=NativeOracle.Client(input).Bottom-NativeOracle.Client(viewport).Top+40;
                window.ScrollBy(fullDelta);
                expected=NativeOracle.VisibleIntersection(input,viewport,root);
                hr=store.GetScreenExt(1,out var clipped);
                Check("real focused surface entirely outside viewport",expected.Equals(default(NativeOracle.Rect))&&NativeOracle.Client(input).Bottom<NativeOracle.Client(viewport).Top);
                Check("fully clipped surface zero no lock",hr==0&&clipped.Equals(default(TsfRect)),clipped);
                Check("layout clip actual ancestor intersection",window.LastLayout!.ClientScreen.Equals(default(TsfRect)),window.LastLayout.ClientScreen);
                window.SetScroll(0,0);
                Check("actual input geometry restored",inputBefore.Equals(NativeOracle.Client(input)));
                Check("row geometry restored",window.LastLayout!.TextRowScreen.Equals(rowBefore));
                hr=store.GetScreenExt(1,out var restored);
                Check("display surface restored",hr==0&&Same(restored,NativeOracle.VisibleIntersection(input,viewport,root)));
                Check("no focus changed by geometry",NativeOracle.GetFocus()==originalFocus);
                Check("external foreground preserved",NativeOracle.GetForegroundWindow()==initialForeground);
                Check("no binding write/composition",recorder.Writes==0&&!store.IsComposing);
                try {
                    window.ResizeWindow(rootBefore.Width+80,rootBefore.Height+50);
                    var resizedRoot=NativeOracle.Client(root);var resizedViewport=NativeOracle.Client(viewport);var resizedInput=NativeOracle.Client(input);
                    Check("viewport follows actual root client resize",resizedViewport.Width==resizedRoot.Width&&resizedViewport.Height==resizedRoot.Height,$"root{resizedRoot} viewport{resizedViewport}");
                    Check("input width follows root resize",resizedInput.Width-inputBefore.Width==resizedRoot.Width-rootClientBefore.Width,$"before{inputBefore} after{resizedInput}");
                } finally {window.ResizeWindow(rootBefore.Width,rootBefore.Height);window.SetScroll(0,0);}
            });
        } catch(Exception ex) {Check("audit execution",false,ex);}
        finally {
            window.RequestClose();
            Check("UI exits",thread.Join(TimeSpan.FromSeconds(10)));
        }
        Check("all actual HWND destroyed",!NativeOracle.IsWindow(root)&&!NativeOracle.IsWindow(input)&&!NativeOracle.IsWindow(viewport));
        Check("foreground retained on teardown",NativeOracle.GetForegroundWindow()==initialForeground);
        Console.WriteLine($"RESULT {_total-_failed}/{_total}");
        return _failed==0?0:1;
    }
}
