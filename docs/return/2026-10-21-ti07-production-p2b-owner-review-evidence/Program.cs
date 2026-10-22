using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;

int failures = 0;
Run("End alone cannot confirm nonempty composition before completed edit", h =>
{
    var (session, owner) = h.CreateOwner(4, 4);
    using (owner)
    {
        owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true);
        owner.DrainAtSafePhase();
        owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", true);
        owner.DrainAtSafePhase();
        Check(h.Writes == 0, $"End committed prematurely: writes={h.Writes}");
    }
});
Run("insert-before-start range cannot consume committed suffix", h =>
{
    var (session, owner) = h.CreateOwner(2, 2);
    using (owner)
    {
        owner.Capture(NativeCompositionFactKind.Start, 2, 5, "nihao", true);
        owner.DrainAtSafePhase();
        Check(session.Snapshot.DisplayText == "ABnihaoCD", $"mirror={session.Snapshot.DisplayText}");
        Check(h.Writes == 0, "preedit wrote Binding");
    }
});
Run("native selected replacement retains correct committed suffix", h =>
{
    var (session, owner) = h.CreateOwner(1, 3);
    using (owner)
    {
        owner.Capture(NativeCompositionFactKind.Start, 1, 5, "nihao", true);
        owner.DrainAtSafePhase();
        Check(session.Snapshot.DisplayText == "AnihaoD", $"mirror={session.Snapshot.DisplayText}");
    }
});
Run("incomplete history cannot continue committing facts", h =>
{
    var (_, owner) = h.CreateOwner(4, 4);
    using (owner)
    {
        owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true);
        owner.DrainAtSafePhase();
        owner.MarkNeedsResync();
        owner.Capture(NativeCompositionFactKind.End, 4, 2, "ni", true);
        owner.DrainAtSafePhase();
        Check(h.Writes == 0, $"resync-required owner committed: writes={h.Writes}");
    }
});
Run("own native synchronization does not invalidate next composition update", h =>
{
    var (session, owner) = h.CreateOwner(4, 4);
    using (owner)
    {
        owner.Capture(NativeCompositionFactKind.Start, 4, 2, "ni", true);
        owner.DrainAtSafePhase();
        Check(owner.SynchronizeToNative(), "sync failed");
        owner.Capture(NativeCompositionFactKind.Update, 4, 3, "nih", true);
        owner.DrainAtSafePhase();
        Check(!owner.NeedsResync && session.Snapshot.Preedit == "nih", "own synchronization treated as external invalidation");
    }
});
Console.WriteLine($"Owner audit failures: {failures}");
return failures == 0 ? 0 : 1;

void Run(string name, Action<Host> action)
{
    try { using var h = new Host(); action(h); Console.WriteLine($"[PASS] {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"[FAIL] {name}: {e.Message}"); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Host : IDisposable
{
    public string Text = "ABCD";
    public int Writes;
    public Service Service = new();
    public UiRuntime Runtime;
    public AcpTextStore Store = new();
    public Host()
    {
        Runtime = new UiRuntime(new TextField(new Binding<string>(() => Text, value => { Writes++; Text = value; })),
            new UiRuntimeServices { Dispatcher = new HeadlessUiDispatcher(), TextService = new StubTextService(),
                TextureService = new RecordingTextureService(), TextInputService = Service });
        Runtime.SetViewport(new Size(400, 120), 1);
        Runtime.Start(); Runtime.BuildFrame(); Runtime.SetFocus(Runtime.Root); Runtime.BuildFrame();
        Store.ResetAuthoritativeText(Text);
    }
    public (TextInputCompositionSession, NativeCompositionOwner) CreateOwner(int anchor, int active)
    {
        var client = Service.ActiveClient!;
        if (!((ITextInputSelectionTarget)client).TrySetCommittedSelection(client.Session, client.Document.Version,
                new TextSelection(anchor, active))) throw new InvalidOperationException("selection failed");
        var session = new TextInputCompositionSession(client, c => ReferenceEquals(c, Service.ActiveClient));
        return (session, new NativeCompositionOwner(Store, session, client.Session.SessionId));
    }
    public void Dispose() { Store.Dispose(); Runtime.Dispose(); }
}
sealed class Service : ITextInputService
{
    public TextInputCapabilities Capabilities => TextInputCapabilities.Full;
    public ITextInputClient? ActiveClient { get; private set; }
    public bool StartSession(ITextInputClient client) { ActiveClient = client; return true; }
    public bool EndSession() { ActiveClient = null; return true; }
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events) => ActiveClient!.ApplyComposition(events);
    public bool RequestCancel() => false;
    public bool PublishGeometry(TextInputGeometry geometry) => true;
}
