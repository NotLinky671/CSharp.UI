using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;
using System.Runtime.InteropServices;

int failed = 0;
Run("Windows CCW QI and native GetStatus vtable", () =>
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows audit required");
    using var store = new AcpTextStore();
    var unknown = Marshal.GetIUnknownForObject(store);
    IntPtr acp = IntPtr.Zero;
    try
    {
        var iid = TsfIids.TextStoreAcp;
        Check(Marshal.QueryInterface(unknown, in iid, out acp) == 0 && acp != IntPtr.Zero, "QI failed");
        var vtable = Marshal.ReadIntPtr(acp);
        var fn = Marshal.GetDelegateForFunctionPointer<GetStatusFn>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));
        Check(fn(acp, out var status) == 0, "native GetStatus failed");
        Check(status.DynamicFlags == 0, "unexpected dynamic flags");
        GC.KeepAlive(store);
    }
    finally
    {
        if (acp != IntPtr.Zero) Marshal.Release(acp);
        Marshal.Release(unknown);
    }
});
Run("same sink re-advice updates mask", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    Check(store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 1) == 0, "initial advice");
    Check(store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 2) == 0 && store.SinkMask == 2,
        "same sink mask update rejected");
});
Run("foreign thread cannot grant sink lock", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    int hr = 0;
    var thread = new Thread(() => hr = store.RequestLock(TsfFlags.TS_LF_READ, out _));
    thread.Start();
    Check(thread.Join(TimeSpan.FromSeconds(5)), "worker timed out");
    Check(hr < 0 && sink.Grants == 0, $"hr={hr:X8}, grants={sink.Grants}");
});
Run("foreign thread cannot send sink notifications", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    var thread = new Thread(() =>
    {
        store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        store.NotifySelectionChange();
    });
    thread.Start();
    Check(thread.Join(TimeSpan.FromSeconds(5)), "worker timed out");
    Check(sink.TextChanges == 0 && sink.SelectionChanges == 0, "foreign-thread sink notification emitted");
});
Run("active authoritative snapshot preserves display text and caret", () =>
{
    using var store = new AcpTextStore();
    var snapshot = Snapshot("abcXYZdef", new TextSelection(6, 3), new TextRange(3, 6), "nihao", 2, true);
    Check(store.ApplyAuthoritativeUpdate(snapshot, store.NativeGeneration), "update rejected");
    var actual = store.ReadAuthoritativeDocument();
    Check(actual.Text == "abcnihaodef", $"display text length={actual.Text.Length}");
    Check(actual.SelectionStart == 5 && actual.SelectionEnd == 5, "display caret not mapped");
});
Run("application update cannot mutate inside read grant", () =>
{
    using var store = new AcpTextStore();
    store.ResetAuthoritativeText("original");
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    sink.Granted = () =>
    {
        var before = store.ReadAuthoritativeDocument().Text;
        try
        {
            store.ApplyAuthoritativeUpdate(Snapshot("replacement", new TextSelection(0, 0),
                new TextRange(0, 0), "", 0, false), store.NativeGeneration);
        }
        catch (InvalidOperationException) { }
        Check(store.ReadAuthoritativeDocument().Text == before, "text changed during read grant");
    };
    store.RequestLock(TsfFlags.TS_LF_READ, out var session);
    Check(session == 0, $"grant failed {session:X8}: {sink.Failure}");
});
Run("authoritative update cannot reenter during notification", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    bool? nested = null;
    sink.TextChanged = () =>
    {
        sink.TextChanged = null;
        nested = store.ApplyAuthoritativeUpdate(Snapshot("nested", new TextSelection(0, 0),
            new TextRange(0, 0), "", 0, false), store.NativeGeneration);
    };
    Check(store.ApplyAuthoritativeUpdate(Snapshot("outer", new TextSelection(2, 2),
        new TextRange(2, 2), "", 0, false), store.NativeGeneration), "outer update rejected");
    Check(nested == false && store.ReadAuthoritativeDocument().Text == "outer", "nested update mutated in notification");
});
Run("authoritative no-op does not advance generation or notify", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    var snapshot = Snapshot("hello", new TextSelection(4, 1), new TextRange(1, 4), "", 0, false);
    Check(store.ApplyAuthoritativeUpdate(snapshot, store.NativeGeneration), "first update rejected");
    var generation = store.NativeGeneration;
    var count = sink.TextChanges;
    Check(store.ApplyAuthoritativeUpdate(snapshot, generation), "no-op rejected");
    Check(store.NativeGeneration == generation && sink.TextChanges == count, "no-op changed generation or notified");
    var actual = store.ReadAuthoritativeDocument();
    Check(actual.SelectionStart == 1 && actual.SelectionEnd == 4 && store.Document.IsSelectionBackward,
        "backward selection lost");
});
Run("inline dispatcher cannot notify before RequestLock returns", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    store.UiDispatcher = action => action();
    bool requestActive = true;
    bool notifiedInRequest = false;
    sink.Granted = () => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
    sink.TextChanged = () => notifiedInRequest |= requestActive;
    store.RequestLock(TsfFlags.TS_LF_READ, out _);
    requestActive = false;
    Check(!notifiedInRequest, "notification emitted inside RequestLock stack");
    store.FlushDeferredNotifications();
    Check(sink.TextChanges == 1, "safe-stage notification lost");
});
Run("queued dispatcher delivers pending notifications automatically", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    var work = new Queue<Action>();
    store.UiDispatcher = work.Enqueue;
    sink.Granted = () => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
    store.RequestLock(TsfFlags.TS_LF_READ, out _);
    Check(sink.TextChanges == 0, "premature notification");
    Check(work.Count > 0, "no UI callback scheduled for pending notification");
    for (int n = 0; n < 16 && work.Count > 0; n++) work.Dequeue()();
    Check(work.Count == 0 && sink.TextChanges == 1, "notification did not reach safe-stage sink");
});
Run("inline continuation cannot defeat lock drain bound", () =>
{
    using var store = new AcpTextStore();
    var sink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, sink, 3);
    int dispatches = 0;
    store.UiDispatcher = action =>
    {
        if (++dispatches > 8) throw new InvalidOperationException("audit stops recursive inline dispatch");
        action();
    };
    sink.Granted = () =>
    {
        if (sink.Grants < 40) store.RequestLock(TsfFlags.TS_LF_READWRITE, out _);
    };
    store.RequestLock(TsfFlags.TS_LF_READ, out _);
    Check(dispatches <= 1, $"inline dispatcher recursively entered {dispatches} times");
    Check(sink.Grants <= TextStoreLockManager.MaxDrainIterations + 1,
        $"single request performed {sink.Grants} grants, exceeding drain budget");
});
Run("sink replacement during text notification cannot receive old selection notice", () =>
{
    using var store = new AcpTextStore();
    var oldSink = new Sink();
    var newSink = new Sink();
    store.AdviseSink(TsfIids.TextStoreAcpSink, oldSink, 3);
    oldSink.TextChanged = () =>
    {
        store.UnadviseSink(oldSink);
        store.AdviseSink(TsfIids.TextStoreAcpSink, newSink, 3);
    };
    Check(store.ApplyAuthoritativeUpdate(Snapshot("new text", new TextSelection(2, 2),
        new TextRange(2, 2), "", 0, false), store.NativeGeneration), "update rejected");
    Check(newSink.TextChanges == 0 && newSink.SelectionChanges == 0,
        "replacement sink received trailing notification from previous connection");
});
Console.WriteLine($"Audit failures: {failed}");
return failed == 0 ? 0 : 1;

void Run(string name, Action action)
{
    try { action(); Console.WriteLine($"[PASS] {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"[FAIL] {name}: {e.Message}"); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static TextInputAuthoritativeSnapshot Snapshot(string text, TextSelection selection, TextRange replacement,
    string preedit, int caret, bool composing) => TextInputAuthoritativeSnapshot.Create(
        new TextSnapshot(text, selection), replacement, preedit, caret, [], 1, 1, composing);

sealed class Sink : ITextStoreACPSink
{
    public int Grants;
    public Action? Granted;
    public Action? TextChanged;
    public int TextChanges;
    public int SelectionChanges;
    public string? Failure;
    public int OnTextChange(uint flags, ref TsTextChange change)
    {
        TextChanges++;
        TextChanged?.Invoke();
        return 0;
    }
    public int OnSelectionChange() { SelectionChanges++; return 0; }
    public int OnLayoutChange(int code, uint view) => 0;
    public int OnStatusChange(uint flags) => 0;
    public int OnAttrsChange(int start, int end, uint count, IntPtr attrs) => 0;
    public int OnLockGranted(uint flags)
    {
        Grants++;
        try { Granted?.Invoke(); return 0; }
        catch (Exception e) { Failure = e.Message; return unchecked((int)0x80004005); }
    }
    public int OnStartEditTransaction() => 0;
    public int OnEndEditTransaction() => 0;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int GetStatusFn(IntPtr self, out TsStatus status);
