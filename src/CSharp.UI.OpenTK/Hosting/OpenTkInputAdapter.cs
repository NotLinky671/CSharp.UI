using System.Diagnostics;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 把 OpenTK 窗口事件转换为框架输入事件并分发到 <see cref="UiRuntime"/>。
/// </summary>
/// <remarks>
/// <para>坐标使用逻辑像素（GLFW 光标坐标即内容区左上原点的屏幕坐标，与逻辑像素一致）。</para>
/// <para>
/// 修饰键在每个键盘事件上刷新；鼠标事件沿用最近一次键盘事件的修饰键状态（GLFW 的鼠标移动事件不携带修饰键）。
/// 窗口失焦时派发 <see cref="PointerEventKind.Cancel"/> 清理指针捕获与按下状态。
/// </para>
/// </remarks>
internal sealed class OpenTkInputAdapter
{
    private const int DoubleClickMilliseconds = 400;
    private const double DoubleClickDistance = 4;

    private readonly UiRuntime _runtime;
    private readonly Action _requestExit;
    private readonly bool _exitOnEscape;
    private readonly bool _exitOnCloseShortcut;

    private KeyModifiers _modifiers;
    private PointerButton _pressedButton;
    private PointerButton _lastDownButton;
    private Point _pointerPosition;
    private Point _lastDownPosition;
    private long _lastDownTimestamp;
    private int _clickCount = 1;

    /// <summary>创建输入适配器。</summary>
    /// <param name="runtime">目标运行时。</param>
    /// <param name="requestExit">请求退出事件循环。</param>
    /// <param name="exitOnEscape">按 Esc 是否退出。</param>
    /// <param name="exitOnCloseShortcut">按 Ctrl+Q / Cmd+Q 是否退出。</param>
    internal OpenTkInputAdapter(UiRuntime runtime, Action requestExit, bool exitOnEscape = true, bool exitOnCloseShortcut = true)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(requestExit);
        _runtime = runtime;
        _requestExit = requestExit;
        _exitOnEscape = exitOnEscape;
        _exitOnCloseShortcut = exitOnCloseShortcut;
    }

    /// <summary>最近一次分发到运行时的指针事件。</summary>
    internal PointerEvent? LastPointerEvent { get; private set; }

    /// <summary>最近一次分发到运行时的滚轮事件。</summary>
    internal WheelEvent? LastWheelEvent { get; private set; }

    /// <summary>最近一次分发到运行时的键盘事件。</summary>
    internal KeyEvent? LastKeyEvent { get; private set; }

    /// <summary>最近一次分发到运行时的文本输入事件。</summary>
    internal TextInputEvent? LastTextEvent { get; private set; }

    /// <summary>累计指针事件数量。</summary>
    internal int PointerEventCount { get; private set; }

    /// <summary>累计滚轮事件数量。</summary>
    internal int WheelEventCount { get; private set; }

    /// <summary>累计键盘事件数量。</summary>
    internal int KeyEventCount { get; private set; }

    /// <summary>累计文本输入事件数量。</summary>
    internal int TextInputEventCount { get; private set; }

    /// <summary>累计退出请求次数（Esc / Ctrl+Q）。</summary>
    internal int ExitRequestCount { get; private set; }

    /// <summary>当前修饰键状态。</summary>
    internal KeyModifiers Modifiers => _modifiers;

    /// <summary>处理鼠标移动。</summary>
    internal void HandleMouseMove(MouseMoveEventArgs e)
    {
        _pointerPosition = new Point(e.X, e.Y);
        DispatchPointer(new PointerEvent(PointerEventKind.Move, _pointerPosition, _pressedButton, _modifiers, _clickCount));
    }

    /// <summary>处理鼠标按下。</summary>
    internal void HandleMouseDown(MouseButtonEventArgs e)
    {
        var button = OpenTkInputMapping.ToPointerButton(e.Button);
        var now = Stopwatch.GetTimestamp();
        var isDoubleClick = button != PointerButton.None
            && button == _lastDownButton
            && (now - _lastDownTimestamp) <= DoubleClickMilliseconds * (Stopwatch.Frequency / 1000)
            && _pointerPosition.DistanceTo(_lastDownPosition) <= DoubleClickDistance;

        _clickCount = isDoubleClick ? Math.Min(_clickCount + 1, 3) : 1;
        _lastDownButton = button;
        _lastDownTimestamp = now;
        _lastDownPosition = _pointerPosition;
        _pressedButton = button;
        DispatchPointer(new PointerEvent(PointerEventKind.Down, _pointerPosition, button, _modifiers, _clickCount));
    }

    /// <summary>处理鼠标释放。</summary>
    internal void HandleMouseUp(MouseButtonEventArgs e)
    {
        var button = OpenTkInputMapping.ToPointerButton(e.Button);
        DispatchPointer(new PointerEvent(PointerEventKind.Up, _pointerPosition, button, _modifiers, _clickCount));
        if (button == _pressedButton)
        {
            _pressedButton = PointerButton.None;
        }
    }

    /// <summary>处理指针离开窗口。</summary>
    internal void HandleMouseLeave()
    {
        DispatchPointer(new PointerEvent(PointerEventKind.Cancel, _pointerPosition, PointerButton.None, _modifiers));
    }

    /// <summary>处理窗口焦点变化；失去焦点时取消指针捕获与按下状态。</summary>
    internal void HandleFocusChanged(bool focused)
    {
        if (focused)
        {
            _runtime.InvalidateRender();
            return;
        }

        DispatchPointer(new PointerEvent(
            PointerEventKind.Cancel,
            _pointerPosition,
            _pressedButton == PointerButton.None ? PointerButton.Left : _pressedButton,
            _modifiers));
        _pressedButton = PointerButton.None;
        _modifiers = KeyModifiers.None;
        _clickCount = 1;
    }

    /// <summary>处理滚轮；位置使用最近一次鼠标移动的位置（GLFW 滚轮回调不提供坐标）。</summary>
    internal void HandleWheel(MouseWheelEventArgs e)
    {
        var wheel = new WheelEvent(_pointerPosition, e.OffsetX, e.OffsetY, _modifiers);
        LastWheelEvent = wheel;
        WheelEventCount++;
        _runtime.DispatchWheel(wheel);
        _runtime.InvalidateRender();
    }

    /// <summary>处理按键按下。</summary>
    internal void HandleKeyDown(KeyboardKeyEventArgs e)
    {
        _modifiers = OpenTkInputMapping.ToKeyModifiers(e.Modifiers);
        var key = OpenTkInputMapping.ToUiKey(e.Key);
        var keyEvent = new KeyEvent(KeyEventKind.Down, key, _modifiers, e.IsRepeat);
        LastKeyEvent = keyEvent;
        KeyEventCount++;
        _runtime.DispatchKey(keyEvent);

        if ((key == UiKey.Escape && _exitOnEscape)
            || (key == UiKey.Q && _exitOnCloseShortcut && keyEvent.IsCommandPressed))
        {
            RequestExit();
            return;
        }

        _runtime.InvalidateRender();
    }

    /// <summary>处理按键释放。</summary>
    internal void HandleKeyUp(KeyboardKeyEventArgs e)
    {
        _modifiers = OpenTkInputMapping.ToKeyModifiers(e.Modifiers);
        var keyEvent = new KeyEvent(KeyEventKind.Up, OpenTkInputMapping.ToUiKey(e.Key), _modifiers, e.IsRepeat);
        LastKeyEvent = keyEvent;
        KeyEventCount++;
        _runtime.DispatchKey(keyEvent);
        _runtime.InvalidateRender();
    }

    /// <summary>处理已提交的字符输入；控制字符（Enter/Tab/Backspace 等）由键盘事件处理，这里忽略。</summary>
    internal void HandleTextInput(TextInputEventArgs e)
    {
        var text = e.AsString;
        if (string.IsNullOrEmpty(text) || (text.Length == 1 && char.IsControl(text[0])))
        {
            return;
        }

        var textEvent = new TextInputEvent(text);
        LastTextEvent = textEvent;
        TextInputEventCount++;
        _runtime.DispatchTextInput(textEvent);
        _runtime.InvalidateRender();
    }

    private void DispatchPointer(PointerEvent pointerEvent)
    {
        LastPointerEvent = pointerEvent;
        PointerEventCount++;
        _runtime.DispatchPointer(pointerEvent);
    }

    private void RequestExit()
    {
        ExitRequestCount++;
        _requestExit();
    }
}
