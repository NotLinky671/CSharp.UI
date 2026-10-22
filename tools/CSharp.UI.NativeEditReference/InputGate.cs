using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 输入安全门：每次按键前都重新核对前台、焦点与线程归属。
/// 只做检查，**不做**夺取：不 TOPMOST、不 AttachThreadInput、不 SetFocus。
/// </summary>
internal static class InputGate
{
    internal static bool Check(IntPtr parent, IntPtr edit, out string detail)
    {
        detail = Describe(parent, edit, out var ok);
        return ok;
    }

    internal static string Describe(IntPtr parent, IntPtr edit, out bool ok)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var focus = NativeEditNative.GetFocus();
        var parentThread = NativeMethods.GetWindowThreadProcessId(parent, out var parentPid);
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        var editThread = NativeMethods.GetWindowThreadProcessId(edit, out var editPid);
        var processId = Environment.ProcessId;

        ok = foreground == parent
            && focus == edit
            && parentPid == processId
            && editPid == processId
            && parentThread == editThread
            && foregroundThread == parentThread
            && foregroundPid == processId;

        return $"foreground=0x{foreground.ToInt64():X} parent=0x{parent.ToInt64():X} "
            + $"focus=0x{focus.ToInt64():X} edit=0x{edit.ToInt64():X} "
            + $"pid(own)={processId} parent_pid={parentPid} edit_pid={editPid} fg_pid={foregroundPid} "
            + $"threads parent={parentThread} edit={editThread} fg={foregroundThread}";
    }
}

/// <summary>场景几何快照：窗口/客户区、交集、真实组合起点、原生 caret、EM_POSFROMCHAR 坐标。</summary>
internal sealed class GeometrySnapshot
{
    internal required string Phase { get; init; }

    internal NativeRect ParentWindow { get; init; }

    internal NativeRect ParentClient { get; init; }

    internal NativeRect ViewportWindow { get; init; }

    internal NativeRect ViewportClient { get; init; }

    internal NativeRect EditWindow { get; init; }

    internal NativeRect EditClient { get; init; }

    internal NativeRect EditViewportIntersection { get; init; }

    internal int FirstVisibleLine { get; init; }

    internal int LineCount { get; init; }

    internal int SelectionStart { get; init; }

    internal int SelectionEnd { get; init; }

    internal string Text { get; init; } = string.Empty;

    /// <summary>输入前记录的真实组合起点 ACP（不是当前选区）。</summary>
    internal int CompositionStartAcp { get; init; }

    internal bool CompositionPointKnown { get; init; }

    internal int CompositionPointScreenX { get; init; }

    internal int CompositionPointScreenY { get; init; }

    internal bool NativeCaretKnown { get; init; }

    internal int NativeCaretScreenX { get; init; }

    internal int NativeCaretScreenY { get; init; }

    internal bool EmCaretKnown { get; init; }

    internal int EmCaretScreenX { get; init; }

    internal int EmCaretScreenY { get; init; }

    internal string? NativeCaretFailure { get; init; }

    internal string? EmCaretFailure { get; init; }

    internal Dictionary<string, object?> ToFields() => new()
    {
        ["phase"] = Phase,
        ["parent_window"] = ParentWindow.ToString(),
        ["parent_client"] = ParentClient.ToString(),
        ["viewport_window"] = ViewportWindow.ToString(),
        ["viewport_client"] = ViewportClient.ToString(),
        ["edit_window"] = EditWindow.ToString(),
        ["edit_client"] = EditClient.ToString(),
        ["edit_viewport_intersection"] = EditViewportIntersection.ToString(),
        ["edit_fully_clipped"] = EditViewportIntersection.IsEmpty,
        ["first_visible_line"] = FirstVisibleLine,
        ["line_count"] = LineCount,
        ["selection_start"] = SelectionStart,
        ["selection_end"] = SelectionEnd,
        ["text"] = ImeFormat.EscapeUtf16(Text),
        ["composition_start_acp"] = CompositionStartAcp,
        ["composition_point_known"] = CompositionPointKnown,
        ["composition_point_screen"] = CompositionPointKnown
            ? $"({CompositionPointScreenX},{CompositionPointScreenY})"
            : "(unknown)",
        ["native_caret_known"] = NativeCaretKnown,
        ["native_caret_screen"] = NativeCaretKnown
            ? $"({NativeCaretScreenX},{NativeCaretScreenY})"
            : "(unknown)",
        ["native_caret_failure"] = NativeCaretFailure,
        ["em_caret_known"] = EmCaretKnown,
        ["em_caret_screen"] = EmCaretKnown ? $"({EmCaretScreenX},{EmCaretScreenY})" : "(unknown)",
        ["em_caret_failure"] = EmCaretFailure,
    };
}
