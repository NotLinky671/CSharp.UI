namespace CSharp.UI;

/// <summary>修饰键状态；与平台无关，由宿主适配。</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>无修饰键。</summary>
    None = 0,

    /// <summary>Shift。</summary>
    Shift = 1 << 0,

    /// <summary>Control（Windows/Linux）。</summary>
    Control = 1 << 1,

    /// <summary>Alt / Option。</summary>
    Alt = 1 << 2,

    /// <summary>Command（macOS）/ Windows 键 / Super。</summary>
    Meta = 1 << 3,

    /// <summary>Caps Lock 开启。</summary>
    CapsLock = 1 << 4,
}

/// <summary>平台无关的按键标识。只表示控制键与导航键；字符输入通过已提交文本事件传递。</summary>
public enum UiKey
{
    /// <summary>未知或未映射的按键。</summary>
    Unknown = 0,

    /// <summary>Tab。</summary>
    Tab,

    /// <summary>Enter / Return。</summary>
    Enter,

    /// <summary>空格。</summary>
    Space,

    /// <summary>Escape。</summary>
    Escape,

    /// <summary>退格。</summary>
    Backspace,

    /// <summary>Delete。</summary>
    Delete,

    /// <summary>Insert。</summary>
    Insert,

    /// <summary>左方向键。</summary>
    Left,

    /// <summary>右方向键。</summary>
    Right,

    /// <summary>上方向键。</summary>
    Up,

    /// <summary>下方向键。</summary>
    Down,

    /// <summary>Home。</summary>
    Home,

    /// <summary>End。</summary>
    End,

    /// <summary>Page Up。</summary>
    PageUp,

    /// <summary>Page Down。</summary>
    PageDown,

    /// <summary>A—Z（按下时 <see cref="KeyEvent.Text"/> 为空，字符输入走文本事件）。</summary>
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
}

/// <summary>指针按钮。</summary>
public enum PointerButton
{
    /// <summary>无按钮（移动事件）。</summary>
    None = 0,

    /// <summary>左键。</summary>
    Left,

    /// <summary>右键。</summary>
    Right,

    /// <summary>中键。</summary>
    Middle,
}

/// <summary>指针事件种类。</summary>
public enum PointerEventKind
{
    /// <summary>按下。</summary>
    Down,

    /// <summary>移动（按下期间也会收到，用于拖动）。</summary>
    Move,

    /// <summary>释放。</summary>
    Up,

    /// <summary>指针离开窗口或被取消（窗口失焦）。</summary>
    Cancel,
}

/// <summary>指针事件；坐标为逻辑像素。</summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Position">指针位置（逻辑像素，相对窗口内容区左上角）。</param>
/// <param name="Button">涉及的按钮。</param>
/// <param name="Modifiers">修饰键。</param>
/// <param name="ClickCount">连击次数，1 为单击。</param>
public readonly record struct PointerEvent(
    PointerEventKind Kind,
    Point Position,
    PointerButton Button = PointerButton.Left,
    KeyModifiers Modifiers = KeyModifiers.None,
    int ClickCount = 1);

/// <summary>滚轮事件；坐标为逻辑像素。<see cref="DeltaY"/> 为正表示向上滚动。</summary>
public readonly record struct WheelEvent(
    Point Position,
    double DeltaX,
    double DeltaY,
    KeyModifiers Modifiers = KeyModifiers.None);

/// <summary>键盘按键事件种类。</summary>
public enum KeyEventKind
{
    /// <summary>按下。</summary>
    Down,

    /// <summary>释放。</summary>
    Up,
}

/// <summary>
/// 键盘事件。字符输入不通过本事件传递，见 <see cref="TextInputEvent"/>。
/// </summary>
/// <param name="Kind">按下或释放。</param>
/// <param name="Key">平台无关按键。</param>
/// <param name="Modifiers">修饰键。</param>
/// <param name="Repeat">是否为按住后的重复触发。</param>
/// <param name="Text">宿主能够提供的已提交字符（可选）；一般情况下为 null，字符输入走文本事件。</param>
public readonly record struct KeyEvent(
    KeyEventKind Kind,
    UiKey Key,
    KeyModifiers Modifiers = KeyModifiers.None,
    bool Repeat = false,
    string? Text = null)
{
    /// <summary>是否处于 Control（或 macOS Command）按下状态，用于编辑快捷键。</summary>
    public bool IsCommandPressed => Modifiers.HasFlag(KeyModifiers.Control) || Modifiers.HasFlag(KeyModifiers.Meta);
}

/// <summary>已提交的 Unicode 文本输入（不含控制键）。</summary>
/// <param name="Text">已提交文本；可能是中文、emoji 或其他多码元文本。</param>
public readonly record struct TextInputEvent(string Text)
{
    /// <summary>按单行规则过滤后的文本：移除 CR/LF 与制表符。</summary>
    public static string SanitizeSingleLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is not ('\r' or '\n' or '\t'))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
