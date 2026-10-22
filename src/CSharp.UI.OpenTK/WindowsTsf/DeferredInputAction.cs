namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 一条被延后的输入<b>完整不可变捕获</b>（TI-07 P3a R9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须带负载：</b>上一版只保存 <c>(kind, sequence, 0)</c>，因此延后项<b>无法重放</b>，
/// 输入被静默丢弃。这里保存重放所需的全部不可变快照：文本、按键（含重复/修饰键/归属 token）、
/// 指针（含坐标/按键/点击计数）、滚轮、焦点激活值。
/// </para>
/// <para>
/// <b>绝不读取可变当前状态：</b>重放时不得再读 <c>_modifiers</c>/<c>LogicalPointerPosition</c>/
/// <c>CurrentKeyToken</c> 等可变字段——那些值在延后期间可能已改变。所有值在<b>捕获时刻</b>冻结。
/// </para>
/// </remarks>
internal sealed class DeferredInputAction
{
    private DeferredInputAction(
        HostDeferredInputKind kind,
        long sequence,
        string? text,
        DeferredKeyInput? key,
        DeferredPointerInput? pointer,
        DeferredWheelInput? wheel,
        bool? focusActivated,
        Point? focusPointerPosition = null,
        KeyModifiers? focusModifiers = null)
    {
        Kind = kind;
        Sequence = sequence;
        Text = text;
        Key = key;
        Pointer = pointer;
        Wheel = wheel;
        FocusActivated = focusActivated;
        FocusPointerPosition = focusPointerPosition;
        FocusModifiers = focusModifiers;
    }

    /// <summary>输入类别。</summary>
    internal HostDeferredInputKind Kind { get; }

    /// <summary>原生到达序号（同一宿主内单调，用于证明顺序）。</summary>
    internal long Sequence { get; }

    /// <summary>已提交文本（<see cref="HostDeferredInputKind.Character"/>）。</summary>
    internal string? Text { get; }

    /// <summary>按键快照（<see cref="HostDeferredInputKind.Key"/>）。</summary>
    internal DeferredKeyInput? Key { get; }

    /// <summary>指针快照（<see cref="HostDeferredInputKind.Pointer"/>）。</summary>
    internal DeferredPointerInput? Pointer { get; }

    /// <summary>滚轮快照（<see cref="HostDeferredInputKind.Wheel"/>）。</summary>
    internal DeferredWheelInput? Wheel { get; }

    /// <summary>焦点激活值（<see cref="HostDeferredInputKind.Focus"/>）。</summary>
    internal bool? FocusActivated { get; }

    /// <summary>失活清理时使用的捕获指针坐标（焦点事件，可空）。</summary>
    internal Point? FocusPointerPosition { get; }

    /// <summary>失活清理时使用的捕获修饰键（焦点事件，可空）。</summary>
    internal KeyModifiers? FocusModifiers { get; }

    /// <summary>捕获一条已提交文本。</summary>
    internal static DeferredInputAction ForText(long sequence, string text)
        => new(HostDeferredInputKind.Character, sequence, text, null, null, null, null);

    /// <summary>捕获一次按键。</summary>
    internal static DeferredInputAction ForKey(long sequence, DeferredKeyInput key)
        => new(HostDeferredInputKind.Key, sequence, null, key, null, null, null);

    /// <summary>捕获一次指针事件。</summary>
    internal static DeferredInputAction ForPointer(long sequence, DeferredPointerInput pointer)
        => new(HostDeferredInputKind.Pointer, sequence, null, null, pointer, null, null);

    /// <summary>捕获一次滚轮事件。</summary>
    internal static DeferredInputAction ForWheel(long sequence, DeferredWheelInput wheel)
        => new(HostDeferredInputKind.Wheel, sequence, null, null, null, wheel, null);

    /// <summary>捕获一次焦点变化。</summary>
    internal static DeferredInputAction ForFocus(long sequence, bool activated)
        => new(HostDeferredInputKind.Focus, sequence, null, null, null, null, activated);

    /// <summary>捕获一次焦点变化（含失活清理所需的坐标与修饰键快照）。</summary>
    internal static DeferredInputAction ForFocus(
        long sequence, bool activated, Point pointerPosition, KeyModifiers modifiers)
        => new(HostDeferredInputKind.Focus, sequence, null, null, null, null, activated, pointerPosition, modifiers);

    /// <summary>只输出类别与序号，不含用户内容。</summary>
    public override string ToString() => $"{Kind}#{Sequence}";
}

/// <summary>被延后的按键快照（不可变）。</summary>
/// <param name="Kind">按下/抬起。</param>
/// <param name="Key">映射后的 UI 键。</param>
/// <param name="Modifiers">捕获时的修饰键。</param>
/// <param name="IsRepeat">是否自动重复。</param>
/// <param name="KeyToken">该原生按键链的归属 token（捕获时冻结）。</param>
/// <param name="ConsumedByIme">捕获时输入法是否已消费。</param>
internal readonly record struct DeferredKeyInput(
    KeyEventKind Kind,
    UiKey Key,
    KeyModifiers Modifiers,
    bool IsRepeat,
    object? KeyToken,
    bool ConsumedByIme);

/// <summary>被延后的指针快照（不可变，坐标已按捕获时系数换算为逻辑坐标）。</summary>
/// <param name="Kind">事件类别。</param>
/// <param name="Position">捕获时的逻辑坐标。</param>
/// <param name="Button">按键。</param>
/// <param name="Modifiers">捕获时的修饰键。</param>
/// <param name="ClickCount">点击计数。</param>
internal readonly record struct DeferredPointerInput(
    PointerEventKind Kind,
    Point Position,
    PointerButton Button,
    KeyModifiers Modifiers,
    int ClickCount);

/// <summary>被延后的滚轮快照（不可变）。</summary>
/// <param name="Position">捕获时的逻辑坐标。</param>
/// <param name="OffsetX">水平增量。</param>
/// <param name="OffsetY">垂直增量。</param>
/// <param name="Modifiers">捕获时的修饰键。</param>
internal readonly record struct DeferredWheelInput(
    Point Position,
    double OffsetX,
    double OffsetY,
    KeyModifiers Modifiers);
