using System.Reflection;
using CSharp.UI.TsfHostCheck;

internal static class LifecycleAudit
{
    internal static void Run(Action<string, bool, object?> check)
    {
        foreach (var sample in new[] {
            (Name:"flags failure after successful activation", Activation:0, FlagsHr:unchecked((int)0x80004005), Flags:0u, Expected:1),
            (Name:"missing flag after successful activation", Activation:0, FlagsHr:0, Flags:0u, Expected:1),
            (Name:"successful activation", Activation:0, FlagsHr:0, Flags:4u, Expected:1),
            (Name:"failed activation", Activation:unchecked((int)0x80004005), FlagsHr:0, Flags:4u, Expected:0) })
        {
            var fake = new Manager(sample.Activation, sample.FlagsHr, sample.Flags);
            var session = new TsfSessionHost(new AcpTextStore(), 0, null, (_, _, _) => {}, false, () => {}, uiElementEnabledOnly:true);
            typeof(TsfSessionHost).GetField("_threadMgr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, fake);
            var result = session.ActivateForTest(fake);
            check(sample.Name + " result", sample.Name == "successful activation" ? result == 0 : result < 0, result);
            session.Cleanup();
            session.Cleanup();
            check(sample.Name + " exactly-once Deactivate", fake.Deactivations == sample.Expected, fake.Deactivations);
            check(sample.Name + " ownership cleared", !session.ResourceState.Activated && !session.ResourceState.HasThreadManager && session.CleanupFailures == 0, session.ResourceState);
        }
    }
    private sealed class Manager(int activationHr, int flagsHr, uint flags) : ITfThreadMgrEx
    {
        internal int Deactivations;
        public int Activate(out uint id) { id=0; throw new InvalidOperationException("Default Activate must not run in this branch"); }
        public int ActivateEx(out uint id,uint requested) { id=42; if(requested!=4)throw new InvalidOperationException("Wrong flags"); return activationHr; }
        public int GetActiveFlags(out uint actual) { actual=flags; return flagsHr; }
        public int Deactivate() { Deactivations++; return 0; }
        public int CreateDocumentMgr(out ITfDocumentMgr mgr) { mgr=null!; return unchecked((int)0x80004001); }
        public int EnumDocumentMgrs(out nint value) { value=0; return unchecked((int)0x80004001); }
        public int GetFocus(out ITfDocumentMgr mgr) { mgr=null!; return 1; }
        public int SetFocus(ITfDocumentMgr? mgr) => 0;
        public int AssociateFocus(nint hwnd,ITfDocumentMgr? mgr,out ITfDocumentMgr? previous) { previous=null; return 0; }
        public int IsThreadFocus(out int focused) { focused=0; return 0; }
        public int GetFunctionProvider(Guid clsid,out nint provider) {provider=0;return unchecked((int)0x80004001);}
        public int EnumFunctionProviders(out nint value) {value=0;return unchecked((int)0x80004001);}
        public int GetGlobalCompartment(out nint value) {value=0;return unchecked((int)0x80004001);}
    }
}
