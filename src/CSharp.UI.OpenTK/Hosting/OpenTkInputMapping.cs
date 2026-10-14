using global::OpenTK.Windowing.GraphicsLibraryFramework;
using GlfwKeyModifiers = global::OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace CSharp.UI.OpenTK;

/// <summary>
/// OpenTK/GLFW 输入到框架无关输入的映射。
/// </summary>
/// <remarks>
/// 纯函数，不依赖窗口与 GL 上下文，便于在无显示环境下验证映射表：
/// 平台修饰键按“macOS Command（Super）→ <see cref="KeyModifiers.Meta"/>，Windows/Linux Ctrl →
/// <see cref="KeyModifiers.Control"/>”的约定直译。
/// </remarks>
internal static class OpenTkInputMapping
{
    /// <summary>把 GLFW 按键映射为框架按键；控制/导航以外的按键返回 <see cref="UiKey.Unknown"/>。</summary>
    internal static UiKey ToUiKey(Keys key) => key switch
    {
        Keys.Tab => UiKey.Tab,
        Keys.Enter or Keys.KeyPadEnter => UiKey.Enter,
        Keys.Space => UiKey.Space,
        Keys.Escape => UiKey.Escape,
        Keys.Backspace => UiKey.Backspace,
        Keys.Delete => UiKey.Delete,
        Keys.Insert => UiKey.Insert,
        Keys.Left => UiKey.Left,
        Keys.Right => UiKey.Right,
        Keys.Up => UiKey.Up,
        Keys.Down => UiKey.Down,
        Keys.Home => UiKey.Home,
        Keys.End => UiKey.End,
        Keys.PageUp => UiKey.PageUp,
        Keys.PageDown => UiKey.PageDown,
        Keys.A => UiKey.A,
        Keys.B => UiKey.B,
        Keys.C => UiKey.C,
        Keys.D => UiKey.D,
        Keys.E => UiKey.E,
        Keys.F => UiKey.F,
        Keys.G => UiKey.G,
        Keys.H => UiKey.H,
        Keys.I => UiKey.I,
        Keys.J => UiKey.J,
        Keys.K => UiKey.K,
        Keys.L => UiKey.L,
        Keys.M => UiKey.M,
        Keys.N => UiKey.N,
        Keys.O => UiKey.O,
        Keys.P => UiKey.P,
        Keys.Q => UiKey.Q,
        Keys.R => UiKey.R,
        Keys.S => UiKey.S,
        Keys.T => UiKey.T,
        Keys.U => UiKey.U,
        Keys.V => UiKey.V,
        Keys.W => UiKey.W,
        Keys.X => UiKey.X,
        Keys.Y => UiKey.Y,
        Keys.Z => UiKey.Z,
        _ => UiKey.Unknown,
    };

    /// <summary>把 GLFW 修饰键映射为框架修饰键。</summary>
    internal static KeyModifiers ToKeyModifiers(GlfwKeyModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(GlfwKeyModifiers.Shift))
        {
            result |= KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(GlfwKeyModifiers.Control))
        {
            result |= KeyModifiers.Control;
        }

        if (modifiers.HasFlag(GlfwKeyModifiers.Alt))
        {
            result |= KeyModifiers.Alt;
        }

        if (modifiers.HasFlag(GlfwKeyModifiers.Super))
        {
            // macOS 的 Command 键在 GLFW 中是 Super；Windows/Linux 为 Windows/Super 键。
            result |= KeyModifiers.Meta;
        }

        if (modifiers.HasFlag(GlfwKeyModifiers.CapsLock))
        {
            result |= KeyModifiers.CapsLock;
        }

        return result;
    }

    /// <summary>把 GLFW 鼠标按钮映射为框架指针按钮。</summary>
    internal static PointerButton ToPointerButton(MouseButton button) => button switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Right => PointerButton.Right,
        MouseButton.Middle => PointerButton.Middle,
        _ => PointerButton.None,
    };
}
