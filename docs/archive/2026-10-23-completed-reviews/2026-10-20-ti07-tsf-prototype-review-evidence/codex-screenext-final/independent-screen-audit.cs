using CSharp.UI.TsfHostCheck;
var failed = 0;
void Check(bool ok, string label) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}"); if (!ok) failed++; }
var store = new AcpTextStore();
Check(store.GetScreenExt(99, out var invalid) == TsfHResult.E_INVALIDARG && invalid.Equals(default(TsfRect)), "invalid view yields E_INVALIDARG and zero output without lock");
store.ResetDocument("abc");
var expected = TsfRect.FromLtrb(30, 40, 330, 240);
store.PublishLayout(AcpLayoutSnapshot.Build(store.LayoutGeneration, store.Document.Version, "abc", [10,10,10], 20, expected, 50, 60, 0, 0), false);
Check(store.GetScreenExt(1, out var visible) == 0 && visible.Equals(expected), "valid surface can be queried without document lock");
store.InvalidateLayout("audit-text-layout-only");
Check(store.GetScreenExt(1, out var afterInvalidation) == 0 && afterInvalidation.Equals(expected), "text-layout invalidation does not invalidate unchanged display surface");
Check(store.GetTextExt(1, 0, 3, out _, out _) == TsfHResult.TS_E_NOLOCK, "GetTextExt still requires document read lock");
if (args.Contains("--native"))
{
    Check(DpiNative.IsPerMonitorV2(), "native audit runs with PerMonitorV2 awareness");
    using var log = new CSharp.UI.ImeProbe.ProbeLog("artifacts/ime-work/ti07-screenext-native-audit.jsonl", echo: false);
    var nativeStore = new AcpTextStore();
    using var window = new TsfHostWindow(nativeStore, log, (_,_,_) => { });
    Check(window.Create(400, 240, "CSharp.UI GetScreenExt contract audit", show: false), "actual own HWND created hidden");
    var foregroundBefore = ScreenAuditNative.GetForegroundWindow();
    Check(nativeStore.GetScreenExt(1, out var hidden) == 0 && hidden.Equals(default(TsfRect)), "actual hidden window returns S_OK zero rect");
    ScreenAuditNative.ShowWindow(window.Hwnd, 4); // SW_SHOWNOACTIVATE: no foreground request.
    var expectedNative = window.ClientScreenRect;
    Check(nativeStore.GetScreenExt(1, out var shown) == 0 && shown.Equals(expectedNative) && !shown.Equals(default(TsfRect)), "actual shown window returns physical client screen rect without lock");
    nativeStore.InvalidateLayout("audit-no-reflow");
    Check(nativeStore.GetScreenExt(1, out var unchanged) == 0 && unchanged.Equals(expectedNative), "actual surface survives text-layout-only invalidation");
    Check(ScreenAuditNative.SetWindowPos(window.Hwnd, IntPtr.Zero, 120, 135, 470, 290, 0x0014), "actual own window move/resize without activation");
    expectedNative = window.ClientScreenRect;
    Check(nativeStore.GetScreenExt(1, out var moved) == 0 && moved.Equals(expectedNative) && !moved.Equals(shown), "actual move and resize return current surface");
    ScreenAuditNative.ShowWindow(window.Hwnd, 7); // SW_SHOWMINNOACTIVE.
    Check(nativeStore.GetScreenExt(1, out var minimized) == 0 && minimized.Equals(default(TsfRect)), "actual minimized window returns zero rect");
    ScreenAuditNative.ShowWindow(window.Hwnd, 4);
    Check(nativeStore.GetScreenExt(1, out var restored) == 0 && restored.Equals(window.ClientScreenRect) && !restored.Equals(default(TsfRect)), "actual restored window returns current surface");
    ScreenAuditNative.ShowWindow(window.Hwnd, 0);
    Check(nativeStore.GetScreenExt(1, out var hiddenAgain) == 0 && hiddenAgain.Equals(default(TsfRect)), "actual rehidden window returns zero rect");
    Check(ScreenAuditNative.GetForegroundWindow() == foregroundBefore, "native surface audit never changes foreground");
}

Console.WriteLine($"Audit failures={failed}");
return failed == 0 ? 0 : 1;

internal static class ScreenAuditNative
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
}

