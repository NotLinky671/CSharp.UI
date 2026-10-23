using CSharp.UI.TsfHostCheck;

internal static class ElementReentrancyAudit
{
    internal static void Run(Action<string,bool,object?> check)
    {
        var queryThrow=new Element {ThrowPostQuery=true};
        using(var info=new ElementInfo(101,queryThrow)) {
            var result=info.TryShow(true);
            check("post-query exception does not replace actual Show S_OK",result.HResult==0,result.HResult);
            check("Show called despite diagnostic result",queryThrow.ShowCalls==1,queryThrow.ShowCalls);
        }
        var ended=new Element();
        var endedInfo=new ElementInfo(102,ended);
        ended.OnShow=endedInfo.Dispose;
        var endResult=endedInfo.TryShow(true);
        check("synchronous End preserves actual Show S_OK",endResult.HResult==0,endResult.HResult);
        check("synchronous End skips post IsShown",ended.QueryCalls==1,ended.QueryCalls);
        var readsBefore=ended.AllCalls;
        endedInfo.Refresh();
        endedInfo.TryShow(false);
        endedInfo.Dispose();
        endedInfo.Dispose();
        check("ended element never called again",ended.AllCalls==readsBefore,$"{readsBefore}->{ended.AllCalls}");
        var ordinary=new Element();
        using(var info=new ElementInfo(103,ordinary)) {
            var a=info.TryShow(true);
            var b=info.TryShow(false);
            check("ordinary show/hide retains results",a.HResult==0&&b.HResult==0&&a.ShownAfter==true&&b.ShownAfter==false,$"{a.ShownAfter}/{b.ShownAfter}");
        }
        var refreshEnded=new Element();
        using(var info=new ElementInfo(104,refreshEnded)) {
            refreshEnded.OnDescription=info.Dispose;
            var calls=refreshEnded.AllCalls;
            info.Refresh();
            check("Refresh synchronous End stops later COM calls",refreshEnded.AllCalls==calls+1,$"{calls}->{refreshEnded.AllCalls}");
        }
    }
    private sealed class Element:ITfUIElement
    {
        internal int ShowCalls,QueryCalls,AllCalls;
        internal bool ThrowPostQuery;
        internal Action? OnShow;
        internal Action? OnDescription;
        private bool shown;
        public int GetDescription(out string value){AllCalls++;value="audit";OnDescription?.Invoke();return 0;}
        public int GetGUID(out Guid value){AllCalls++;value=Guid.Empty;return 0;}
        public int Show(int value){AllCalls++;ShowCalls++;shown=value!=0;OnShow?.Invoke();return 0;}
        public int IsShown(out int value){AllCalls++;QueryCalls++;value=shown?1:0;if(ThrowPostQuery&&ShowCalls>0)throw new InvalidOperationException("post-query failure");return 0;}
    }
}
