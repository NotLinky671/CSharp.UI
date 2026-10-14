using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 基于 GLFW 剪贴板 API 的剪贴板服务（Windows / macOS / Linux 共有能力）。
/// </summary>
/// <remarks>剪贴板读写失败（窗口已销毁、平台无剪贴板）时不抛出，只返回 null 或忽略写入。</remarks>
internal sealed unsafe class GlfwClipboardService : IClipboardService
{
    private readonly NativeWindow _window;

    /// <summary>由宿主窗口创建。</summary>
    internal GlfwClipboardService(NativeWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <inheritdoc />
    public string? GetText()
    {
        try
        {
            var text = GLFW.GetClipboardString(_window.WindowPtr);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            GLFW.SetClipboardString(_window.WindowPtr, text);
        }
        catch (Exception)
        {
            // 剪贴板写入失败不影响 UI；调用方无需处理。
        }
    }
}
