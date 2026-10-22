using CSharp.UI;
using CSharp.UI.OpenTK;
using CSharp.UI.Testing;

int failures = 0;
Run("duplicate partial confirmation", h =>
{
 var a = h.Adapter(); var token=a.Begin(preedit: "nihao").Token!;
 var operation=token.LastAcceptedOperationId+1;
 a.CommitResult(token,operation,"你", "hao"); a.CommitResult(token,operation,"你", "hao");
 Check(h.Writes == 1, $"same confirmation replay wrote {h.Writes} times; text={h.Text}");
 a.CommitResult(token,operation+1,"你","hao");
 Check(h.Writes==2,"two legal identical results with distinct operation IDs were deduplicated");
});
Run("refresh after external binding change", h =>
{
 var a=h.Adapter(); a.Begin(preedit:"ni"); h.Text="external";
 var snapshot=a.Refresh();
 Check(snapshot.IsComposing==h.Service.ActiveClient!.IsComposing,
  $"mirror={snapshot.IsComposing}, core={h.Service.ActiveClient.IsComposing}, display={snapshot.DisplayText}");
});
Run("cross-region selection", h =>
{
 var a=h.Adapter(); var token=a.Begin(new TextRange(1,3),"ni",2).Token!;
 var result=a.SetNativeSelection(new TextSelection(0,2),token,token.LastAcceptedOperationId+1);
 Check(result.IsRejected,"unsupported cross-region range reported success");
});
Run("snapshot output immutability", h =>
{
 var a=h.Adapter(); var s=a.Begin(preedit:"ni",segments:new[]{new TextRange(0,2)}).Snapshot;
 if(s.Segments is TextRange[] array) array[0]=new TextRange(0,0);
 else if(s.Segments is IList<TextRange> list)
 { try{list[0]=new TextRange(0,0);}catch(NotSupportedException){} }
 Check(s.Segments[0]==new TextRange(0,2),"published snapshot was mutated through array cast");
});
Run("selection getter revokes client", h =>
{
 var client=h.Service.ActiveClient!; var version=client.Document.Version;
 h.OnRead=()=>{h.OnRead=null;h.Runtime.SetFocus(null);};
 bool ok=((ITextInputSelectionTarget)client).TrySetCommittedSelection(client.Session,version,new TextSelection(1,1));
 Check(!ok,"selection accepted after getter revoked focused client");
});
Run("failed document read prevents apply", h =>
{
 var client=new ThrowingClient(); var a=new TextInputCompositionSession(client,_=>true);
 client.Throw=true; var result=a.Begin(preedit:"ni");
 Check(result.IsRejected && client.Applies==0,$"rejected={result.IsRejected}, applies={client.Applies}");
});
Run("refresh checks bridge ownership", h=>
{
 bool current=true; var a=new TextInputCompositionSession(h.Service.ActiveClient!,_=>current);
 a.Begin(preedit:"ni"); current=false; var snapshot=a.Refresh();
 Check(!snapshot.IsComposing && !a.IsOpen,"revoked bridge ownership retained composing mirror");
});
Run("post-commit read failure preserves outcome",h=>
{
 var client=new AfterCommitFailureClient(); var a=new TextInputCompositionSession(client,_=>true);
 var token=a.Begin(preedit:"ni").Token!;
 var result=a.CommitResult(token,token.LastAcceptedOperationId+1,"你");
 Check(client.Writes==1 && result.Handled && result.Committed,
  $"writes={client.Writes}, handled={result.Handled}, committed={result.Committed}");
 Check(!a.IsOpen && token.IsRetired,"failed synchronization did not invalidate session/token");
});
Console.WriteLine($"Independent P2a audit failures={failures}");
return failures==0?0:1;

void Run(string name,Action<Host> action)
{
 try { using var h=new Host(); action(h); Console.WriteLine($"PASS {name}"); }
 catch(Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
static void Check(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}

sealed class Host:IDisposable
{
 public string Text="ABCD";
 public int Writes;
 public Action? OnRead;
 public Service Service=new();
 public UiRuntime Runtime;
 public Host()
 {
  Runtime=new UiRuntime(new TextField(new Binding<string>(()=>{OnRead?.Invoke();return Text;},v=>{Writes++;Text=v;})),
   new UiRuntimeServices{Dispatcher=new HeadlessUiDispatcher(),TextService=new StubTextService(),
    TextureService=new RecordingTextureService(),TextInputService=Service});
  Runtime.SetViewport(new Size(400,120),1);Runtime.Start();Runtime.BuildFrame();
  Runtime.SetFocus(Runtime.Root);Runtime.BuildFrame();
 }
 public TextInputCompositionSession Adapter()=>new(Service.ActiveClient!,c=>ReferenceEquals(c,Service.ActiveClient));
 public void Dispose()=>Runtime.Dispose();
}
sealed class Service:ITextInputService
{
 public TextInputCapabilities Capabilities=>TextInputCapabilities.Full;
 public ITextInputClient? ActiveClient{get;private set;}
 public bool StartSession(ITextInputClient c){ActiveClient=c;return true;}
 public bool EndSession(){ActiveClient=null;return true;}
 public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> e)=>ActiveClient!.ApplyComposition(e);
 public bool RequestCancel()=>false;
 public bool PublishGeometry(TextInputGeometry g)=>true;
}
sealed class ThrowingClient:ITextInputClient
{
 public bool Throw;
 public int Applies;
 private readonly TextSnapshot _snapshot=new("ABCD",new TextSelection(0,0),0);
 public TextSnapshot Document=>Throw?throw new InvalidOperationException("getter failed"):_snapshot;
 public TextInputSession Session=>new(7);
 public bool IsComposing=>false;
 public string Preedit=>"";
 public TextInputState GetState()=>new(_snapshot);
 public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
 {Applies++;return new(TextInputOutcome.Started,_snapshot,true,AppliedEventCount:1);}
 public bool CancelComposition()=>false;
}
sealed class AfterCommitFailureClient:ITextInputClient
{
 private TextSnapshot _snapshot=new("ABCD",new TextSelection(0,0),0);
 private bool _fail;
 public int Writes;
 public TextSnapshot Document=>_fail?throw new InvalidOperationException("post-apply read failed"):_snapshot;
 public TextInputSession Session=>new(11);
 public bool IsComposing{get;private set;}
 public string Preedit{get;private set;}="";
 public TextInputState GetState()=>new(_snapshot,Preedit,composing:IsComposing);
 public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
 {
  var e=events[0];
  if(e.Kind==CompositionEventKind.Start)
  {IsComposing=true;Preedit=e.Preedit;return new(TextInputOutcome.Started,_snapshot,true,AppliedEventCount:1);}
  if(e.Kind==CompositionEventKind.Result)
  {
   Writes++;var end=e.Replacement.Start+e.Preedit.Length;
   _snapshot=new(_snapshot.Text[..e.Replacement.Start]+e.Preedit+_snapshot.Text[e.Replacement.End..],new TextSelection(end,end),_snapshot.Version+1);
   IsComposing=false;Preedit="";_fail=true;
   return new(TextInputOutcome.ResultCommitted,_snapshot,true,AppliedEventCount:1);
  }
  return TextInputApplyResult.NotHandled(_snapshot);
 }
 public bool CancelComposition()=>false;
}
