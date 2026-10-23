using System.Runtime.InteropServices;
using CSharp.UI.TsfHostCheck;

var passed = 0;
var failed = 0;
void Check(bool ok, string label)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}");
    if (ok) passed++; else failed++;
}
var store = new AcpTextStore();
var sink = new AuditSink();
Check(store.AdviseSink(TsfIids.TextStoreAcpSink, sink, uint.MaxValue) == 0, "sink accepts protocol IID");
int Under(uint flags, Func<int> body)
{
    var result = int.MinValue;
    sink.Grant = _ => result = body();
    var request = store.RequestLock(flags | TsfFlags.TS_LF_SYNC, out var session);
    return request < 0 ? request : result;
}
int Replace(int start, int end, string text)
{
    var mem = Marshal.StringToHGlobalUni(text);
    try { return store.SetText(0, start, end, mem, (uint)text.Length, out _); }
    finally { Marshal.FreeHGlobal(mem); }
}
store.ResetDocument("abc");
Check(Under(TsfFlags.TS_LF_READWRITE, () => Replace(0, 3, "xyz")) == 0, "TSF replacement succeeds with write lock");
Check(sink.TextChanges == 0, "TSF SetText must not echo OnTextChange into its own locked sink");
var selectionBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<TsSelectionAcp>());
try
{
    Marshal.StructureToPtr(new TsSelectionAcp { AcpStart = 0, AcpEnd = 1 }, selectionBuffer, false);
    Under(TsfFlags.TS_LF_READWRITE, () => store.SetSelection(1, selectionBuffer));
    Check(sink.SelectionChanges == 0, "TSF SetSelection must not echo OnSelectionChange");
}
finally { Marshal.FreeHGlobal(selectionBuffer); }

var runBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<TsRunInfo>());
try
{
    Under(TsfFlags.TS_LF_READ, () =>
    {
        var hr = store.GetText(0, -1, IntPtr.Zero, 0, out var chars, runBuffer, 1, out var runs, out var next);
        var info = Marshal.PtrToStructure<TsRunInfo>(runBuffer);
        Check(hr == 0 && chars == 0 && runs == 1 && info.Count == 3 && next == 3,
            "run-only GetText scans document and advances ACP without plain-text buffer");
        return hr;
    });
}
finally { Marshal.FreeHGlobal(runBuffer); }

store.PublishLayout(AcpLayoutSnapshot.Build(store.LayoutGeneration, store.Document.Version,
    store.Document.Text, [10, 10, 10], 20, TsfRect.FromLtrb(0, 0, 200, 100),
    10, -50, 0, 0), false);
Under(TsfFlags.TS_LF_READ, () =>
{
    var hr = store.GetTextExt(1, 0, 3, out var rect, out var clipped);
    Check(hr == 0 && rect.Left == 0 && rect.Top == 0 && rect.Right == 0 && rect.Bottom == 0 && clipped != 0,
        "laid-out fully invisible text returns S_OK and zero rectangle");
    return hr;
});
store.InvalidateLayout("audit");
Under(TsfFlags.TS_LF_READ, () =>
{
    var hr = store.GetTextExt(1, 0, 3, out _, out _);
    Check(hr == TsfHResult.TS_E_NOLAYOUT, "invalidated layout remains distinct from clipped text");
    return hr;
});

store.ResetDocument("a");
store.BeginComposition(0, 1);
Under(TsfFlags.TS_LF_READWRITE, () => Replace(0, 1, "中"));
Check(store.CompositionSpan == (0, 1), "same-length preedit replacement preserves composition extent");
Check(store.EndComposition() == "中", "same-length final composition yields actual result");

store.ResetDocument("");
Under(TsfFlags.TS_LF_READWRITE, () => store.ApplyAppText("a"));
Under(TsfFlags.TS_LF_READWRITE, () => store.ApplyAppText("b"));
Check(store.Document.Text == "ab" && store.Document.Selection == (2, 2),
    "ordinary app text advances insertion caret in original input order");
Check(typeof(ITfContextOwnerCompositionSink).IsAssignableFrom(typeof(AcpTextStore)),
    "CreateContext owner object exposes composition sink through same COM identity");
Check(typeof(WndClassW).StructLayoutAttribute?.CharSet == CharSet.Unicode,
    "RegisterClassW window-class strings marshal as UTF-16");

Console.WriteLine($"Audit: passed={passed} failed={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class AuditSink : ITextStoreACPSink
{
    internal Func<uint, int>? Grant;
    internal int TextChanges;
    internal int SelectionChanges;
    public int OnTextChange(uint flags, ref TsTextChange change) { TextChanges++; return 0; }
    public int OnSelectionChange() { SelectionChanges++; return 0; }
    public int OnLayoutChange(int code, uint view) => 0;
    public int OnStatusChange(uint flags) => 0;
    public int OnAttrsChange(int start, int end, uint count, IntPtr attrs) => 0;
    public int OnLockGranted(uint flags) => Grant?.Invoke(flags) ?? 0;
    public int OnStartEditTransaction() => 0;
    public int OnEndEditTransaction() => 0;
}
