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
var notifications = new Queue<Action>();
store.NotificationDispatcher = notifications.Enqueue;
var requestDepth = 0;
sink.InsideRequest = () => requestDepth > 0;
Check(store.AdviseSink(TsfIids.TextStoreAcpSink, sink, uint.MaxValue) == 0, "sink accepts protocol IID");
int Under(uint flags, Func<int> body)
{
    var result = int.MinValue;
    sink.Grant = _ => result = body();
    requestDepth++;
    try
    {
        var request = store.RequestLock(flags | TsfFlags.TS_LF_SYNC, out var session);
        return request < 0 ? request : result;
    }
    finally { requestDepth--; }
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
sink.HoldsLock = () => store.Locks.LockType != 0;
Under(TsfFlags.TS_LF_READWRITE, () => store.ApplyAppText("c"));
Check(!sink.NotifiedWhileLocked,
    "application text/selection notifications occur only after TSF lock is released");
Check(!sink.NotifiedInsideRequest,
    "application notifications are deferred until RequestLock itself returns");
var invalidGranted = false;
sink.Grant = _ => { invalidGranted = true; return 0; };
var invalidHr = store.RequestLock(0x4, out var invalidSession);
Check(!invalidGranted && (invalidHr < 0 || invalidSession < 0),
    "write-bit without required read-bit is rejected as invalid lock flags");
invalidGranted = false;
invalidHr = store.RequestLock(TsfFlags.TS_LF_READ | 0x100, out invalidSession);
Check(!invalidGranted && (invalidHr < 0 || invalidSession < 0),
    "unknown lock flags must not silently grant a valid-looking read lock");
Check(sink.TextChanges == 0 && sink.SelectionChanges == 0 && notifications.Count == 1,
    "un-pumped application notifications remain pending behind one coalesced dispatch");
var reentered = false;
sink.Reenter = () =>
{
    var hr = store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out var session);
    reentered |= hr == 0 && session == 0;
};
var pumpCount = 0;
while (notifications.TryDequeue(out var action))
{
    if (++pumpCount > 16) throw new Exception("Notification pump failed to converge");
    action();
}
Check(sink.TextChanges > 0 && reentered && !sink.NotifiedWhileLocked && !sink.NotifiedInsideRequest,
    "same-thread safe pump delivers deferred notifications and permits reentrant read locks");
sink.Reenter = null;
var textNotificationsBeforeUnadvise = sink.TextChanges;
Under(TsfFlags.TS_LF_READWRITE, () => store.ApplyAppText("d"));
store.UnadviseSink(sink);
while (notifications.TryDequeue(out var action)) action();
Check(sink.TextChanges == textNotificationsBeforeUnadvise,
    "pending dispatch never notifies a sink after Unadvise");
if (OperatingSystem.IsWindows())
{
    Check(Marshal.GetEndComSlot(typeof(ITfCandidateListUIElement)) == 14,
        "candidate-list COM interface preserves inherited UIElement slots through slot 14");
    var ownerUnknown = Marshal.GetIUnknownForObject(store);
    try
    {
        foreach (var interfaceId in new[] { TsfIids.TextStoreAcp, TsfIids.ContextOwnerCompositionSink })
        {
            var iid = interfaceId;
            var hr = Marshal.QueryInterface(ownerUnknown, ref iid, out var iface);
            Check(hr == 0 && iface != IntPtr.Zero, $"real owner CCW exposes {interfaceId}");
            if (iface != IntPtr.Zero) Marshal.Release(iface);
        }
    }
    finally { Marshal.Release(ownerUnknown); }
}

if (Environment.GetCommandLineArgs().Contains("--invoke"))
{
    // Enable only after the recursive baseline has been repaired and rebuilt: StackOverflow is process-fatal.
    using var window = new TsfHostWindow(new AcpTextStore(), null!, (_, _, _) => { });
    var callingThread = Environment.CurrentManagedThreadId;
    Check(window.Invoke(() => 42) == 42, "generic dispatcher returns computed value without recursively selecting itself");
    Check(window.Invoke(() => Environment.CurrentManagedThreadId) == callingThread,
        "uncreated-window generic dispatch uses the same synchronous Action path");
    var propagated = false;
    try { window.Invoke<int>(() => throw new InvalidOperationException("audit-marker")); }
    catch (InvalidOperationException error) { propagated = error.Message == "audit-marker"; }
    Check(propagated, "generic dispatcher preserves callback failure");
    Check(!ImeIdentity.IsMicrosoftPinyin(1, 1,
        new Guid("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E"),
        new Guid("FA550B04-5AD7-411F-A5AC-CA038EC515D7")),
        "S_FALSE with matching profile fields is not confirmed Microsoft Pinyin identity");
    using var uiObserver = new TsfUiElementObserver(null, (_, _, _) => { });
    var showUi = 0;
    uiObserver.BeginUIElement(1, ref showUi);
    Check(showUi == 1, "default UI observer explicitly allows native UI even when input flag is FALSE");
    showUi = 1;
    uiObserver.BeginUIElement(2, ref showUi);
    Check(showUi == 1, "default UI observer preserves permission to show native UI");
}
Console.WriteLine($"Audit: passed={passed} failed={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class AuditSink : ITextStoreACPSink
{
    internal Func<uint, int>? Grant;
    internal int TextChanges;
    internal int SelectionChanges;
    internal Func<bool>? HoldsLock;
    internal bool NotifiedWhileLocked;
    internal Func<bool>? InsideRequest;
    internal bool NotifiedInsideRequest;
    internal Action? Reenter;
    public int OnTextChange(uint flags, ref TsTextChange change) { TextChanges++; NotifiedWhileLocked |= HoldsLock?.Invoke() ?? false; NotifiedInsideRequest |= InsideRequest?.Invoke() ?? false; Reenter?.Invoke(); return 0; }
    public int OnSelectionChange() { SelectionChanges++; NotifiedWhileLocked |= HoldsLock?.Invoke() ?? false; NotifiedInsideRequest |= InsideRequest?.Invoke() ?? false; return 0; }
    public int OnLayoutChange(int code, uint view) => 0;
    public int OnStatusChange(uint flags) => 0;
    public int OnAttrsChange(int start, int end, uint count, IntPtr attrs) => 0;
    public int OnLockGranted(uint flags) => Grant?.Invoke(flags) ?? 0;
    public int OnStartEditTransaction() => 0;
    public int OnEndEditTransaction() => 0;
}
