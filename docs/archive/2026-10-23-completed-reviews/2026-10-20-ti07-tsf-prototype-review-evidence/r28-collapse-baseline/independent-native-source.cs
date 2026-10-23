using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _failed;
    private static int _total;
    private static object? Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Prop<T>(object target, string name)
        => (T)target.GetType().GetProperty(name, Flags)!.GetValue(target)!;
    private static object Make(Type type, params object[] args)
        => Activator.CreateInstance(type, Flags, null, args, null)!;
    private static void Check(string name, bool value, string? detail = null)
    {
        _total++;
        if (!value) _failed++;
        Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {name} {detail}");
    }
    [STAThread]
    private static int Main()
    {
        var originalForeground = GetForegroundWindow();
        var tool = Assembly.Load("CSharp.UI.NativeEditReference");
        var probe = Assembly.Load("imeprobe");
        var scenarioType=tool.GetType("CSharp.UI.NativeEditReference.NativeEditScenario")!;
        const BindingFlags staticFlags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
        var defaults=(string[])scenarioType.GetField("All",staticFlags)!.GetValue(null)!;
        Check("default matrix remains exactly original three",defaults.SequenceEqual(new[]{"internal-scroll","child-move","child-clip"}),string.Join(",",defaults));
        Check("collapse is explicitly registered",(bool)scenarioType.GetMethod("IsKnown",staticFlags)!.Invoke(null,new object[]{"child-collapse"})!);
        Check("collapse requires restoration",(bool)scenarioType.GetMethod("HasRestore",staticFlags)!.Invoke(null,new object[]{"child-collapse"})!);
        var collapseKind=(string)scenarioType.GetMethod("ActionKind",staticFlags)!.Invoke(null,new object[]{"child-collapse"})!;
        Check("collapse action is distinctly identified",!string.IsNullOrWhiteSpace(collapseKind)&&collapseKind!="none"&&collapseKind!="setwindowpos-child",collapseKind);
        var log = Make(probe.GetType("CSharp.UI.ImeProbe.ProbeLog")!, Path.Combine(AppContext.BaseDirectory, "native-audit.jsonl"), false);
        object? host = null;
        nint parent = 0, edit = 0;
        try
        {
            var observations = Make(tool.GetType("CSharp.UI.NativeEditReference.NativeEditObservationLog")!, log);
            host = Make(tool.GetType("CSharp.UI.NativeEditReference.NativeEditHost")!, log, observations);
            Check("hidden create", (bool)Call(host, "Create", 900, 420, false)!);
            parent = Prop<nint>(host, "Parent");
            edit = Prop<nint>(host, "Edit");
            var viewport = Prop<nint>(host, "Viewport");
            Check("no visible surface", !IsWindowVisible(parent));
            var text = string.Join("\r\n", Enumerable.Range(0, 80).Select(i => $"line {i:00} content"));
            Check("baseline text applied", (bool)Call(host, "SetEditText", text)!);
            Call(host, "SetSelection", 2, 7);
            var selectionResult = Call(host, "GetSelection")!;
            (int Start, int End) selection = selectionResult is ValueTuple<int, int> tuple
                ? tuple : ((int Start, int End))Prop<object>(selectionResult, "Value");
            Check("both selection outputs", selection.Start == 2 && selection.End == 7, selection.ToString());
            foreach (var index in new[] { 0, 2, text.IndexOf("line 03", StringComparison.Ordinal) })
            {
                var expected = SendMessageW(edit, 0xD6, index, 0).ToInt64();
                var query = Call(host, "PosFromChar", index)!;
                var point = Prop<object>(query, "Value");
                var x = (int)point.GetType().GetField("X", Flags)!.GetValue(point)!;
                var y = (int)point.GetType().GetField("Y", Flags)!.GetValue(point)!;
                Check($"standard EDIT EM_POSFROMCHAR {index}", Prop<bool>(query, "Ok") && x == (short)(expected & 0xffff) && y == (short)((expected >> 16) & 0xffff), $"got{x},{y} native{(short)expected},{(short)(expected >> 16)}");
            }
            var index3 = text.IndexOf("line 03", StringComparison.Ordinal);
            var pointBefore = SendMessageW(edit, 0xD6, index3, 0).ToInt64();
            var firstBefore = SendMessageW(edit, 0xCE, 0, 0).ToInt64();
            GetWindowRect(edit, out var rectBefore);
            Call(host, "LinesScroll", 0, 1);
            var pointAfter = SendMessageW(edit, 0xD6, index3, 0).ToInt64();
            Check("real one-line scroll", SendMessageW(edit, 0xCE, 0, 0).ToInt64() == firstBefore + 1 && (short)(pointAfter >> 16) < (short)(pointBefore >> 16));
            var negative = Call(host, "PosFromChar", 0)!;
            var negativePoint = Prop<object>(negative, "Value");
            Check("negative row coordinate", (int)negativePoint.GetType().GetField("Y", Flags)!.GetValue(negativePoint)! < 0);
            GetWindowRect(edit, out var editBefore);
            GetWindowRect(parent, out var parentBefore);
            GetWindowRect(viewport, out var viewportBefore);
            Call(host, "MoveEditChildBy", 0, -11);
            GetWindowRect(edit, out var editAfter);
            GetWindowRect(parent, out var parentAfter);
            GetWindowRect(viewport, out var viewportAfter);
            Check("child exactly 11 physical pixels", editAfter.Left == editBefore.Left && editAfter.Top == editBefore.Top - 11 && editAfter.Right == editBefore.Right && editAfter.Bottom == editBefore.Bottom - 11, $"{editBefore} -> {editAfter}");
            Check("parent and viewport stable", parentBefore.Equals(parentAfter) && viewportBefore.Equals(viewportAfter));
            // Exercise the actual scenario wiring, without IME/input or a visible window.
            Call(host, "MoveEditChild", 0, 0);
            Call(host, "SetFirstVisibleLine", 0);
            var options = Make(tool.GetType("CSharp.UI.NativeEditReference.NativeEditOptions")!);
            options.GetType().GetProperty("OutDir", Flags)!.SetValue(options, AppContext.BaseDirectory);
            var runner = Make(tool.GetType("CSharp.UI.NativeEditReference.NativeEditRunner")!, options, log, observations);
            runner.GetType().GetField("_compositionStartAcp", Flags)!.SetValue(runner, index3);
            object[] clipArguments = [host, "child-clip", string.Empty];
            var clipSucceeded = (bool)Call(runner, "ApplyAction", clipArguments)!;
            Check("actual clip action succeeds", clipSucceeded, clipArguments[2]?.ToString());
            var rowQuery = Call(host, "RowRectForChar", index3)!;
            var row = Prop<object>(rowQuery, "Value");
            var rowBottom = Prop<int>(row, "Bottom");
            GetWindowRect(viewport, out var clipViewport);
            GetWindowRect(parent, out var clipParent);
            Check("clip row is above viewport and inside evidence", Prop<bool>(rowQuery, "Ok") && rowBottom < clipViewport.Top && rowBottom > clipParent.Top, $"rowBottom={rowBottom} viewportTop={clipViewport.Top} parentTop={clipParent.Top}");
            Call(host,"MoveEditChild",0,0);
            GetWindowRect(edit,out var beforeCollapse);
            GetWindowRect(parent,out var collapseParent);
            GetWindowRect(viewport,out var collapseViewport);
            var focusBeforeCollapse=GetFocus();
            var originalStyle=GetWindowLongPtrW(edit,-16);
            var collapsed=Call(host,"ResizeEditChild",beforeCollapse.Right-beforeCollapse.Left,0,"codex-hidden-collapse")!;
            GetWindowRect(edit,out var collapsedRect);
            GetClientRect(edit,out var collapsedClient);
            Check("real collapse succeeds",Prop<bool>(collapsed,"Ok"));
            Check("actual child height and client height zero",collapsedRect.Bottom==collapsedRect.Top&&collapsedClient.Bottom==collapsedClient.Top,$"window={collapsedRect} client={collapsedClient}");
            Check("collapse preserves child origin and width",collapsedRect.Left==beforeCollapse.Left&&collapsedRect.Top==beforeCollapse.Top&&collapsedRect.Right==beforeCollapse.Right);
            Check("collapse keeps window style",GetWindowLongPtrW(edit,-16)==originalStyle);
            var restored=Call(host,"ResizeEditChild",beforeCollapse.Right-beforeCollapse.Left,beforeCollapse.Bottom-beforeCollapse.Top,"codex-hidden-restore")!;
            GetWindowRect(edit,out var restoredRect);
            GetWindowRect(parent,out var afterCollapseParent);
            GetWindowRect(viewport,out var afterCollapseViewport);
            Check("restore from empty rectangle succeeds",Prop<bool>(restored,"Ok"));
            Check("original geometry restored exactly",restoredRect.Equals(beforeCollapse),$"{beforeCollapse}->{restoredRect}");
            Check("collapse restore leaves ancestors unchanged",collapseParent.Equals(afterCollapseParent)&&collapseViewport.Equals(afterCollapseViewport));
            Check("collapse restore never changes focus",GetFocus()==focusBeforeCollapse);
            Check("foreground unchanged", GetForegroundWindow() == originalForeground);
            Call(host, "Dispose");
            Call(host, "Dispose");
            Check("all HWND destroyed", !IsWindow(parent) && !IsWindow(edit));
        }
        catch (Exception ex) { Check("no audit exception", false, ex.ToString()); }
        finally
        {
            if (host is not null) Call(host, "Dispose");
            ((IDisposable)log).Dispose();
        }
        Console.WriteLine($"audit {_total - _failed}/{_total}");
        return _failed == 0 ? 0 : 1;
    }
    [StructLayout(LayoutKind.Sequential)] private record struct Rect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageW(nint hwnd, uint msg, nint wp, nint lp);
}
