using CSharp.UI.TsfHostCheck;
using CSharp.UI.ImeProbe;

internal static class NativeWiringAudit
{
    internal static void Run(Action<string,bool,object?> check)
    {
        var evidence=Path.GetFullPath("artifacts/ime-work/ti07-r27-native-wiring-audit");
        Directory.CreateDirectory(evidence);
        using var log=new ProbeLog(Path.Combine(evidence,"native.jsonl"),echo:false);
        var entries=new List<(string Kind,int Thread,IDictionary<string,object?>? Fields)>();
        TsfTrace trace=(_,kind,fields)=> { lock(entries) entries.Add((kind,Environment.CurrentManagedThreadId,fields)); };
        var store=new AcpTextStore(trace);
        var recorder=new BindingRecorder(store,trace);
        using var window=new TsfHostWindow(store,log,trace);
        Exception? error=null;
        var uiThread=0;
        var thread=new Thread(()=> {
            try {
                uiThread=Environment.CurrentManagedThreadId;
                if(!window.Create(240,120,"CSharp.UI R27 hidden wiring review",show:false))throw new Exception("hidden create failed");
                window.RunMessageLoop();
            } catch(Exception ex) {error=ex;window.LoopStarted.Set();}
        }) {IsBackground=true};
        thread.Start();
        if(!window.LoopStarted.Wait(TimeSpan.FromSeconds(10)))throw new Exception("hidden loop did not start");
        if(error is not null)throw error;
        try {
            var session=new TsfSessionHost(store,window.Hwnd,log,trace,false,()=>{});
            var runner=new ScenarioRunner(window,store,recorder,session,log,trace,new ScenarioOptions{OutDir=evidence});
            using var observer=new TsfUiElementObserver(null,trace);
            var host=new ScenarioLifecycleHost(runner,observer,0);
            check("worker differs from actual UI",Environment.CurrentManagedThreadId!=uiThread,uiThread);
            var probe=host.Probe("review-hidden-probe");
            check("hidden probe rejects without active foreground",!probe.Ok,probe.Detail);
            var captured=runner.CaptureLifecycle("review-hidden-capture");
            check("hidden capture rejected",!captured,captured);
            lock(entries) {
                var probes=entries.Where(x=>x.Kind=="native-ui-lifecycle-probe").ToArray();
                check("both real methods reached probe",probes.Length==2,probes.Length);
                check("real Probe/Capture bodies on UI thread",probes.All(x=>x.Thread==uiThread),string.Join(",",probes.Select(x=>x.Thread)));
                check("hidden precondition prevents profile queries",probes.All(x=>x.Fields is not null&&x.Fields.TryGetValue("profile_queried",out var q)&&Equals(q,false)),null);
                var captures=entries.Where(x=>x.Kind=="review-hidden-capture").ToArray();
                check("invalid capture never marked valid",captures.Length==1&&captures[0].Fields is not null&&captures[0].Fields!.TryGetValue("valid_evidence",out var v)&&Equals(v,false),captures.Length);
            }
            check("no screenshot file produced",!File.Exists(Path.Combine(evidence,"review-hidden-capture.png")),null);
            check("no document write",recorder.Writes==0&&!store.IsComposing,recorder.Writes);
        } finally {
            window.RequestClose();
            if(!thread.Join(TimeSpan.FromSeconds(10)))throw new Exception("hidden loop did not close");
        }
    }
}
