using System.Runtime.InteropServices;
using CSharp.UI;
using CSharp.UI.Testing;
using CSharp.UI.OpenTK;
class Audit {
static int pass,fail;
static void Check(bool ok,string label){Console.WriteLine($"{(ok?"PASS":"FAIL")} {label}"); if(ok)pass++;else fail++;}
static void Main(){
Func<int,int,int,int,string?,TextRange,string?,CompositionEvent> legacyResult=CompositionEvent.Result;
Check(legacyResult is not null,"original public Result factory method group remains compatible");
using(var h=new Host()){
h.Start(); h.Composition(comp:"x",cursor:1);h.Composition(result:"a");
Check(h.Send(Win32Ime.WM_CHAR,'a') is null,"ordinary WM_CHAR equal to result must not be swallowed by text matching");
h.End();
}
using(var h=new Host()){
h.Start();h.Composition(comp:"abc",cursor:1);
h.Ime.Composition="abc";h.Send(Win32Ime.WM_IME_COMPOSITION,0,Win32Ime.GCS_RESULTCLAUSE); // GCS_RESULTCLAUSE only
Check(h.Client.IsComposing,"result-clause-only update is not flagless cancellation");
}
using(var h=new Host()){
h.Start();h.Composition(comp:"abc",cursor:2);
h.Composition(comp:"xyz",result:"A",cursor:1,clauses:[0,1,3]);

Check(h.Client.IsComposing&&h.Client.Preedit=="xyz","combined result retains next preedit");
var c=(TextFieldCompositionClient)h.Client; Check(c.PreeditCaret==1&&c.Segments.SequenceEqual(new[]{new TextRange(0,1),new TextRange(1,3)}),"combined result preserves next cursor and clauses");

}
using(var h=new Host()){
h.Bridge.PublishGeometry(new TextInputGeometry(new Rect(30,10,2,20)));
var count=h.Ime.CandidatePositions.Count;
h.Bridge.UpdatePointerTransform(2d/3,2d/3);
Check(h.Ime.CandidatePositions.Count>count&&h.Ime.CandidatePositions[^1]==(45,45),"scale change republishes unchanged logical anchor");
}
using(var h=new Host()){
h.Send(Win32Ime.WM_IME_SETCONTEXT,1,0x8000000F);
Check(h.Bridge.MaskedSetContextLParam==0xF,"SDK composition high bit cleared and all candidate bits retained");
}
using(var h=new Host()){
Action<KeyEvent> legacy=h.Runtime.DispatchKey;
Action<KeyEvent,object?> legacyOrigin=h.Runtime.DispatchKey;
Check(legacy is not null&&legacyOrigin is not null,"both public void DispatchKey method groups remain compatible");
h.Start();h.Composition(comp:"abc",cursor:1);h.Composition(result:"A");h.Composition(comp:"xyz",cursor:1);
h.Ime.Composition=null;h.Ime.Result=null;h.Send(Win32Ime.WM_IME_COMPOSITION,0,0);
Check(!h.Client.IsComposing&&h.Value=="A"&&h.Writes==1,"flagless cancel preserves earlier segmented result");
h.Send(Win32Ime.WM_IME_COMPOSITION,0,0);
Check(h.Value=="A"&&h.Writes==1,"duplicate native cancel is idempotent");
}
using(var h=new Host()){
h.Runtime.SetFocus(null);h.Start();
h.Runtime.SetFocus(h.Runtime.Root);h.Runtime.BuildFrame();
h.Composition(result:"OLD");
Check(h.Value==""&&h.Writes==0,"native composition started without client cannot transfer to newly focused client");
}
using(var h=new Host()){
h.Ime.Context=IntPtr.Zero;
Check(!h.Bridge.Capabilities.Composition,"missing HIMC reports committed-only fallback");
Check(h.Send(Win32Ime.WM_IME_CHAR,'中') is null,"committed-only fallback must retain native IME character channel");
}
{
var parts=new List<TextRange>{new(0,1),new(1,3)};
var e=CompositionEvent.ResultWithNextPreeditAttributes(0,0,0,0,"A",default,"xyz",1,parts);
parts.Clear();
Check(e.NextPreeditSegments?.Count==2,"next-preedit event freezes caller segment collection");
}
using(var h=new Host()){
h.Composition(comp:"old",cursor:3);
Check(h.Client.IsComposing,"implicit native preedit establishes composition without START");
h.Runtime.SetFocus(null);
Check(h.Ime.CancelCalls>0,"implicit native preedit participates in platform cancellation");
h.Runtime.SetFocus(h.Runtime.Root);h.Runtime.BuildFrame();
h.Composition(result:"OLD");
Check(h.Value==""&&h.Writes==0,"implicit native composition cannot leak delayed result across focus epoch");
}
using(var h=new Host()){
var input=new OpenTkInputAdapter(h.Runtime,()=>{});
input.HandleTextInput(new global::OpenTK.Windowing.Common.TextInputEventArgs(0x4E2D));
input.HandleTextInput(new global::OpenTK.Windowing.Common.TextInputEventArgs(0x1F600));
Check(h.Value=="中😀"&&h.Writes==2,"BMP plus supplementary scalar makes two binding transactions");
}
if(Environment.GetCommandLineArgs().Contains("--native")) NativeAudit();
if(Environment.GetCommandLineArgs().Contains("--tool")) ToolAudit();
Console.WriteLine($"Audit: passed={pass} failed={fail}");Environment.ExitCode=fail==0?0:1;
}static void ToolAudit(){
var dir=Path.Combine(Environment.CurrentDirectory,"tools/CSharp.UI.ImeHostCheck/bin/Debug/net10.0");
var probeAssembly=System.Reflection.Assembly.LoadFrom(Path.Combine(dir,"imeprobe.dll"));
var assembly=System.Reflection.Assembly.LoadFrom(Path.Combine(dir,"CSharp.UI.ImeHostCheck.dll"));
var program=assembly.GetType("CSharp.UI.ImeHostCheck.Program",true)!;
var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
using var h=new Host();
var context=new UiHostContext(h.Runtime,new OpenTkUiDispatcher(()=>{}),null,"audit",_=>{});
using var cancel=new CancellationTokenSource(); cancel.Cancel();
bool interrupted=false;var time=System.Diagnostics.Stopwatch.StartNew();
try{program.GetMethod("Query",flags)!.MakeGenericMethod(typeof(bool)).Invoke(null,[context,(Func<bool>)(()=>true),cancel.Token]);}
catch(System.Reflection.TargetInvocationException e)when(e.InnerException is OperationCanceledException){interrupted=true;}
Check(interrupted&&time.ElapsedMilliseconds<1500,"tool Query cancels without UI dispatcher pumping");
var logType=probeAssembly.GetType("CSharp.UI.ImeProbe.ProbeLog",true)!;
using var log=(IDisposable)Activator.CreateInstance(logType,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,[Path.Combine(Environment.CurrentDirectory,"artifacts/ime-work/ti07-codex-tool-cancellation.jsonl"),false],null)!;
interrupted=false;
try{program.GetMethod("Scenario",flags)!.Invoke(null,[log,"cancel-test",new List<string>(),(Func<bool>)(()=>throw new OperationCanceledException())]);}
catch(System.Reflection.TargetInvocationException e)when(e.InnerException is OperationCanceledException){interrupted=true;}
Check(interrupted,"tool Scenario propagates cancellation instead of continuing");
}
[DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
[DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
[DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
static void NativeAudit(){
var hwnd=CreateWindowExW(0,"STATIC","Codex TI07 invisible audit",0,0,0,100,100,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
if(hwnd==IntPtr.Zero)throw new Exception("CreateWindow failed");
try{
var original=Win32Ime.GetWindowProc(hwnd);
using(var b=new WindowsImeBridge(hwnd,new FakeIme{ThrowEnsure=true},new OpenTkUiDispatcher(()=>{}),()=>(100,100))){
b.InstallSubclass(); GC.Collect();GC.WaitForPendingFinalizers();
Check(b.SubclassRestored&&!b.SubclassInstalled&&Win32Ime.GetWindowProc(hwnd)==original,"native install-context exception restores actual HWND procedure");
SendMessageW(hwnd,0x400,IntPtr.Zero,IntPtr.Zero);
Check(b.RestoreFailureCount==0&&b.InstallError is not null,"native failed install reports failure with zero restore failures");
}
int compositionChains=0,charChains=0;
Win32Ime.WndProc observer=(h,m,w,l)=>{if(m==Win32Ime.WM_IME_COMPOSITION)compositionChains++;if(m==Win32Ime.WM_CHAR)charChains++;return Win32Ime.CallWindowProc(original,h,m,w,l);};
var ptr=Marshal.GetFunctionPointerForDelegate(observer);
Win32Ime.SetWindowProc(hwnd,ptr);
using(var h=new Host(hwnd,true)){
h.Bridge.Trace=_=>throw new Exception("injected log failure");
SendMessageW(hwnd,Win32Ime.WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);
h.Ime.Composition="ni";h.Ime.Cursor=2;
SendMessageW(hwnd,Win32Ime.WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(Win32Ime.GCS_COMPSTR|Win32Ime.GCS_CURSORPOS));
h.Ime.Composition=null;h.Ime.Result="你好";
SendMessageW(hwnd,Win32Ime.WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(Win32Ime.GCS_RESULTSTR));
Check(h.Value=="你好"&&h.Writes==1&&compositionChains==0,"actual HWND result consumed once without entering original composition chain");
SendMessageW(hwnd,Win32Ime.WM_CHAR,new IntPtr('你'),IntPtr.Zero);
Check(charChains==1&&h.Bridge.CallbackExceptionCount==0,"actual HWND ordinary char reaches original procedure despite throwing trace");
h.Bridge.Dispose();
Check(h.Bridge.SubclassRestored&&Win32Ime.GetWindowProc(hwnd)==ptr,"actual HWND normal disposal restores predecessor pointer");
}
Win32Ime.SetWindowProc(hwnd,original);GC.KeepAlive(observer);
}finally{DestroyWindow(hwnd);}
}
    private sealed class FakeIme : IImeInterop
    {
        private static int _nextHandle = 0x1000;
        public IntPtr Context = new(Interlocked.Increment(ref _nextHandle));
        public string? Composition;
        public string? Result;
        public int Cursor = -1;
        public List<int> Clauses = [];
        public bool CancelResult = true;
        public bool ThrowEnsure;
        public int EnsureCalls;
        public int ReleaseCount;
        public int CancelCalls;
        public List<(int X, int Y)> CandidatePositions = [];
        public List<(int L, int T, int R, int B)> CandidateExcludes = [];
        public List<(int X, int Y, int L, int T, int R, int B)> CompositionWindows = [];

        public bool GetNativeInputState(IntPtr himc,out bool open,out uint conversion,out uint sentence){open=true;conversion=1;sentence=0;return true;}
public bool SetNativeInputState(IntPtr himc,bool open,uint conversion,uint sentence)=>true;
public IntPtr GetContext() => Context;

        public void ReleaseContext(IntPtr himc) => ReleaseCount++;

        public IntPtr EnsureContext()
        {
            EnsureCalls++; if(ThrowEnsure) throw new InvalidOperationException("injected context failure");
            return Context;
        }

        public ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags)
            => new(flags, Composition, Result, Cursor, Clauses);

        public bool SetCandidatePosition(IntPtr himc, int x, int y)
        {
            CandidatePositions.Add((x, y));
            return true;
        }

        public bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom)
        {
            CandidateExcludes.Add((left, top, right, bottom));
            return true;
        }

        public bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom)
        {
            CompositionWindows.Add((x, y, left, top, right, bottom));
            return true;
        }

        public bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style)
        {
            x = y = 0;
            style = 0;
            return false;
        }

        public bool NotifyCancelComposition(IntPtr himc)
        {
            CancelCalls++;
            return CancelResult;
        }
    }

    private sealed class Host : IDisposable
    {
        public string Value = string.Empty;
        public int Writes;
        public FakeIme Ime = new();
        public WindowsImeBridge Bridge;
        public UiRuntime Runtime;

        public Host(IntPtr hwnd = default, bool native = false)
        {
            Bridge = new WindowsImeBridge(
                hwnd, Ime, new OpenTkUiDispatcher(() => { }), () => (800, 600));
            if(native) Bridge.InstallSubclass(); else Bridge.SimulateInstalled();
            Runtime = new UiRuntime(
                new TextField(new Binding<string>(() => Value, v => { Value = v; Writes++; })),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
                    TextureService = new RecordingTextureService(),
                    TextInputService = Bridge,
                });
            Bridge.AttachRuntime(Runtime);
            Runtime.SetViewport(new Size(400, 100), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.SetFocus(Runtime.Root);
            Runtime.BuildFrame();
        }

        public ITextInputClient Client => Bridge.ActiveClient!;

        /// <summary>把一条窗口消息喂给桥（与真实 WndProc 相同的处理路径，但没有链式默认过程）。</summary>
        public IntPtr? Send(uint msg, long wParam = 0, long lParam = 0)
            => Bridge.HandleMessage(IntPtr.Zero, msg, new IntPtr(wParam), new IntPtr(lParam));

        public void Start() => Send(Win32Ime.WM_IME_STARTCOMPOSITION);

        public void Composition(string? comp = null, string? result = null, int cursor = -1, int[]? clauses = null)
        {
            Ime.Composition = comp;
            Ime.Result = result;
            Ime.Cursor = cursor;
            Ime.Clauses = clauses is null ? [] : [.. clauses];
            var flags = 0L;
            if (comp is not null || cursor >= 0 || clauses is not null)
            {
                flags |= Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS;
                if (clauses is not null)
                {
                    flags |= Win32Ime.GCS_COMPCLAUSE;
                }
            }

            if (result is not null)
            {
                flags |= Win32Ime.GCS_RESULTSTR;
            }

            Send(Win32Ime.WM_IME_COMPOSITION, 0, flags);
        }

        public void End() => Send(Win32Ime.WM_IME_ENDCOMPOSITION);

        public void Dispose() => Runtime.Dispose();
    }


}