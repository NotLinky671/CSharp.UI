using System.Runtime.InteropServices;
using System.Text;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 窗口与 OpenGL 上下文创建：宿主窗口与离屏测试窗口共用同一套设置与诊断逻辑。
/// </summary>
/// <remarks>
/// 基线为 OpenGL 3.3 Core、GLSL 330；macOS 需要 Forward Compatible 上下文。
/// 创建失败时抛出带完整诊断的 <see cref="UiHostException"/>。
/// </remarks>
internal static class GlWindowFactory
{
    /// <summary>请求的 OpenGL 主版本。</summary>
    internal const int MajorVersion = 3;

    /// <summary>请求的 OpenGL 次版本。</summary>
    internal const int MinorVersion = 3;

    private static readonly Lock ErrorGate = new();
    private static string? _lastError;
    private static bool _errorCallbackInstalled;

    /// <summary>最近一次 GLFW 平台错误文本；没有错误时为 null。</summary>
    internal static string? LastGlfwError
    {
        get
        {
            lock (ErrorGate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>清除记录的 GLFW 错误。</summary>
    internal static void ClearGlfwError()
    {
        lock (ErrorGate)
        {
            _lastError = null;
        }
    }

    /// <summary>
    /// 安装“记录而不抛出”的 GLFW 错误回调（幂等，必须在创建任何窗口之前调用）。
    /// </summary>
    /// <remarks>
    /// OpenTK 默认把任何 GLFW 平台错误转换成异常，这会让受限环境里的良性错误（例如无法初始化
    /// DirectInput 手柄支持）中断窗口创建。宿主记录错误文本并写入诊断，窗口/上下文创建失败仍会
    /// 抛出 <see cref="UiHostException"/>，因此不会掩盖真正的失败。
    /// </remarks>
    internal static void EnsureErrorCallback()
    {
        lock (ErrorGate)
        {
            if (_errorCallbackInstalled)
            {
                return;
            }

            _errorCallbackInstalled = true;
        }

        try
        {
            GLFWProvider.SetErrorCallback(OnGlfwError);
        }
        catch (Exception ex)
        {
            lock (ErrorGate)
            {
                _lastError = $"安装 GLFW 错误回调失败（{ex.GetType().Name}：{ex.Message}）";
            }
        }
    }

    private static void OnGlfwError(global::OpenTK.Windowing.GraphicsLibraryFramework.ErrorCode errorCode, string description)
    {
        lock (ErrorGate)
        {
            _lastError = $"{errorCode}：{description}";
        }
    }

    /// <summary>创建窗口设置。</summary>
    internal static NativeWindowSettings CreateSettings(
        string title,
        int clientWidth,
        int clientHeight,
        bool resizable,
        bool vsync,
        bool visible,
        bool isEventDriven = true)
    {
        var flags = ContextFlags.Default;
        if (OperatingSystem.IsMacOS())
        {
            // macOS 只提供 Core Profile 的 3.2+ 上下文，且必须请求 Forward Compatible。
            flags |= ContextFlags.ForwardCompatible;
        }

        return new NativeWindowSettings
        {
            Title = title,
            ClientSize = new global::OpenTK.Mathematics.Vector2i(Math.Max(1, clientWidth), Math.Max(1, clientHeight)),
            WindowBorder = resizable ? WindowBorder.Resizable : WindowBorder.Fixed,
            APIVersion = new Version(MajorVersion, MinorVersion),
            Profile = ContextProfile.Core,
            Flags = flags,
            Vsync = vsync ? VSyncMode.On : VSyncMode.Off,
            IsEventDriven = isEventDriven,
            StartVisible = visible,
            StartFocused = visible,
            NumberOfSamples = 0,
            DepthBits = 0,
            StencilBits = 0,
            RedBits = 8,
            GreenBits = 8,
            BlueBits = 8,
            AlphaBits = 8,
            SrgbCapable = false,
        };
    }

    /// <summary>创建窗口；失败时抛出带平台与异常信息的 <see cref="UiHostException"/>。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="clientWidth">客户区宽度（逻辑像素；<paramref name="scaleToMonitor"/> 为 true 时按当前显示器缩放放大）。</param>
    /// <param name="clientHeight">客户区高度（逻辑像素）。</param>
    /// <param name="resizable">是否可调整大小。</param>
    /// <param name="vsync">是否启用垂直同步。</param>
    /// <param name="visible">是否可见（测试使用隐藏窗口）。</param>
    /// <param name="purpose">用途描述，写入诊断信息。</param>
    /// <param name="scaleToMonitor">
    /// 是否启用 GLFW <c>ScaleToMonitor</c> 提示：Windows/X11 上把请求尺寸按显示器内容缩放放大，
    /// 使客户区的物理像素覆盖请求的逻辑像素；macOS 本身按点创建，该提示不起作用。
    /// 只有桌面宿主窗口应传 true；离屏/测试窗口必须保持 false。
    /// </param>
    /// <remarks>
    /// OpenTK 4.9.4 的 <c>NativeWindowSettings</c> 没有该属性，也不会调用 <c>DefaultWindowHints</c>，
    /// 因此这里在 <c>NativeWindow</c> 构造前直接设置 GLFW 提示，并在创建后立刻恢复 false，
    /// 避免全局 hint 泄漏到随后创建的离屏窗口。
    /// </remarks>
    internal static NativeWindow CreateWindow(
        string title,
        int clientWidth,
        int clientHeight,
        bool resizable,
        bool vsync,
        bool visible,
        string purpose,
        bool scaleToMonitor = false)
    {
        NativeWindowSettings? settings = null;
        try
        {
            // 必须先于 NativeWindowSettings 构造（它会触发 glfwInit）。
            EnsureErrorCallback();
            ClearGlfwError();
            settings = CreateSettings(title, clientWidth, clientHeight, resizable, vsync, visible);

            // GLFW 提示是全局状态：只影响这一次窗口创建，创建后立即恢复默认。
            GLFW.WindowHint(WindowHintBool.ScaleToMonitor, scaleToMonitor);
            NativeWindow window;
            try
            {
                window = new NativeWindow(settings);
            }
            finally
            {
                GLFW.WindowHint(WindowHintBool.ScaleToMonitor, false);
            }

            // 上下文可用性校验：能读到 GL 版本才认为创建成功。
            var version = GL.GetString(StringName.Version);
            if (string.IsNullOrEmpty(version))
            {
                window.Dispose();
                var reason = LastGlfwError ?? "无法读取 OpenGL 版本字符串";
                var diagnostics = DescribeEnvironment(settings, purpose);
                throw new UiHostException(
                    $"窗口已创建但 OpenGL 上下文不可用（{purpose}）：{reason}{Environment.NewLine}{diagnostics}",
                    diagnostics);
            }

            return window;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var diagnostics = DescribeEnvironment(settings, purpose);
            var message = new StringBuilder()
                .Append("无法创建 OpenTK/GLFW 窗口或 OpenGL 上下文（").Append(purpose).Append("）。")
                .AppendLine()
                .Append(diagnostics)
                .AppendLine()
                .Append("原因：").Append(ex.GetType().Name).Append("：").Append(ex.Message)
                .ToString();
            throw new UiHostException(message, diagnostics, ex);
        }
    }

    /// <summary>返回平台、GLFW 与当前 GL 上下文信息，用于诊断输出。</summary>
    internal static string DescribeEnvironment(NativeWindowSettings? settings = null, string? purpose = null)
    {
        var builder = new StringBuilder();
        builder.Append("平台：").Append(RuntimeInformation.OSDescription)
            .Append("（").Append(RuntimeInformation.OSArchitecture).Append("，")
            .Append(RuntimeInformation.ProcessArchitecture).Append(" 进程，.NET ")
            .Append(Environment.Version).Append('）').AppendLine();
        builder.Append("GLFW：").Append(SafeGlfwVersion()).AppendLine();
        if (LastGlfwError is { } glfwError)
        {
            builder.Append("GLFW 最近错误：").Append(glfwError).AppendLine();
        }
        builder.Append("OpenGL 请求：").Append(MajorVersion).Append('.').Append(MinorVersion).Append(" Core");
        if (OperatingSystem.IsMacOS())
        {
            builder.Append("（macOS Forward Compatible）");
        }

        builder.AppendLine();
        if (settings is not null)
        {
            builder.Append("窗口设置：标题=\"").Append(settings.Title)
                .Append("\"，客户区=").Append(settings.ClientSize.X).Append('×').Append(settings.ClientSize.Y)
                .Append("，边框=").Append(settings.WindowBorder)
                .Append("，VSync=").Append(settings.Vsync)
                .Append("，可见=").Append(settings.StartVisible)
                .Append("，Flags=").Append(settings.Flags)
                .AppendLine();
        }

        if (purpose is not null)
        {
            builder.Append("用途：").Append(purpose).AppendLine();
        }

        builder.Append("OpenGL 实际：").Append(DescribeContext());
        return builder.ToString();
    }

    /// <summary>返回当前 GL 上下文的版本/厂商/渲染器；没有上下文时返回说明文本。</summary>
    internal static string DescribeContext()
    {
        try
        {
            unsafe
            {
                if (GLFW.GetCurrentContext() == null)
                {
                    return "当前线程没有 GL 上下文";
                }
            }

            return string.Join(
                " / ",
                GL.GetString(StringName.Version) ?? "未知版本",
                GL.GetString(StringName.Renderer) ?? "未知渲染器",
                GL.GetString(StringName.Vendor) ?? "未知厂商");
        }
        catch (Exception ex)
        {
            return $"无法读取 GL 信息（{ex.GetType().Name}：{ex.Message}）";
        }
    }

    /// <summary>
    /// 读取窗口当前内容缩放（<c>glfwGetWindowContentScale</c>）：Windows/X11 为系统 DPI 缩放；
    /// macOS 在 Retina 上通常为 2，接普通显示器时同样可能是 1，不能假定固定值。
    /// </summary>
    /// <remarks>
    /// 不可用（窗口已销毁、GLFW 未初始化、平台未提供）时返回零，由 <see cref="ViewportMetrics"/> 走确定性回退；
    /// 不使用“只读一次主屏缩放”的做法，窗口移动到其他显示器后重新读取即可得到新值。
    /// </remarks>
    internal static unsafe (float X, float Y) GetWindowContentScale(NativeWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            GLFW.GetWindowContentScale(window.WindowPtr, out var xScale, out var yScale);
            return (xScale, yScale);
        }
        catch (Exception)
        {
            return (0f, 0f);
        }
    }

    private static string SafeGlfwVersion()
    {
        try
        {
            return GLFW.GetVersionString();
        }
        catch (Exception ex)
        {
            return $"未初始化或不可用（{ex.GetType().Name}：{ex.Message}）";
        }
    }
}
