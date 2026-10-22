using CSharp.UI.TsfHostCheck;

var passed=0; var total=0;
void Check(string name, bool ok, object? detail=null) { total++; if(ok)passed++; Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}: {detail}"); }
foreach(var sample in new[]{"normal","before-text","before-span","before-writes","after-profile","after-composing","invalid-baseline","focus-wait","initial-writes","persistent-writes","initial-lang","initial-type","initial-clsid","initial-profile","initial-truncated"})
{
    var host=new Host(sample);
    var executor=new NativeUiLifecycleExecutor(host,_=>{});
    var result=executor.Run();
    Check(sample+" result",sample=="normal"?result:!result,$"result={result}, stop={executor.Stop}, shows={host.Shows}");
    if(sample.StartsWith("before-")||sample=="invalid-baseline"||sample=="focus-wait"||sample.StartsWith("initial-")||sample=="persistent-writes")
        Check(sample+" no Show",host.Shows==0,host.Shows);
    if(sample=="focus-wait") Check("focus reason preserved",executor.Stop==LifecycleStop.SafePointLost,executor.Stop);
    if(sample.StartsWith("after-")) Check(sample+" no later stages",host.Shows==1,host.Shows);
}
var teardown=NativeUiLifecycleTeardown.Run(false,_=>{},()=>true,()=>true,()=>{},(_,_)=>{});
Check("teardown still composing not Cleaned",teardown.Outcome!=NativeUiLifecycleTeardown.Outcome.Cleaned,teardown.Outcome);
if(args.Contains("--native-wiring"))NativeWiringAudit.Run(Check);
Console.WriteLine($"Independent lifecycle audit {passed}/{total}; {(args.Contains("--native-wiring")?"includes hidden native wiring, no input":"managed only")}");
return passed==total?0:1;

sealed class Host(string scenario): ILifecycleHost
{
    internal int Shows;
    public bool InvokeOnUiThread(Func<bool> action)=>action();
    public SafePointProbe Probe(string stage)
    {
        var s=Snapshot(stage);
        return new(scenario!="focus-wait",scenario=="focus-wait"?"foreground lost":"ok",s.Text,s.SpanStart,s.SpanEnd,s.Writes,s.Composing,s.Profile);
    }
    private LifecycleSnapshot Snapshot(string stage)
    {
        // The host now requires a full verified identity; supply the real fixed
        // Microsoft Pinyin identifiers rather than the previous placeholder.
        var identity=ImeIdentity.Describe(0,1,0x0804,
            Guid.Parse("81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e"),
            Guid.Parse("fa550b04-5ad7-411f-a5ac-ca038ec515d7"));
        var s=new LifecycleSnapshot("ni'hao",0,6,0,true,identity);
        if(scenario=="initial-lang")return s with {Profile=identity.Replace("lang=0x0804","lang=0x0409")};
        if(scenario=="initial-type")return s with {Profile=identity.Replace("type=0x0001","type=0x0002")};
        if(scenario=="initial-clsid")return s with {Profile=identity.Replace("81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e",Guid.Empty.ToString())};
        if(scenario=="initial-profile")return s with {Profile=identity.Replace("fa550b04-5ad7-411f-a5ac-ca038ec515d7",Guid.Empty.ToString())};
        if(scenario=="initial-truncated")return s with {Profile="hr=00000000|"};
        if(scenario=="initial-writes")return s with {Writes=1};
        if(scenario=="persistent-writes"&&stage!="baseline"&&!stage.StartsWith("wait-live:"))return s with {Writes=1};
        if(scenario=="invalid-baseline")return s with {Composing=false};
        if(stage=="native-shown:before") return scenario switch {
            "before-text"=>s with {Text="changed"},
            "before-span"=>s with {SpanEnd=5},
            "before-writes"=>s with {Writes=1},
            _=>s};
        if(stage=="native-shown:after") return scenario switch {
            "after-profile"=>s with {Profile="different-profile"},
            "after-composing"=>s with {Composing=false},
            _=>s};
        return s;
    }
    public void Record(string kind,IDictionary<string,object?> fields){}
    public bool Capture(string name)=>true;
    public void Wait(int ms){}
    public uint? LiveCandidateId()=>7;
    public UiElementShowResult? ShowOnCandidate(uint id,bool show) {Shows++;return new(id,true,0,!show,show);}
    public bool IsLive(uint id)=>true;
}
