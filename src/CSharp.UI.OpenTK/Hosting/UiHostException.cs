namespace CSharp.UI.OpenTK;

/// <summary>
/// 宿主初始化失败（无法创建窗口、无法创建 OpenGL 上下文、framebuffer 不完整等）。
/// </summary>
/// <remarks>
/// 异常消息包含平台、GLFW 版本与窗口设置，便于区分“平台/驱动缺失”与“应用配置错误”；
/// <see cref="Diagnostics"/> 保留完整的诊断文本，宿主也会把它写入 <see cref="UiRuntime.Diagnostics"/> 与标准错误。
/// </remarks>
public sealed class UiHostException : Exception
{
    /// <summary>使用消息、诊断文本与内部异常构造。</summary>
    public UiHostException(string message, string diagnostics, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = diagnostics;
    }

    /// <summary>完整诊断文本（平台、GLFW、窗口设置、GL 版本等）。</summary>
    public string Diagnostics { get; }
}
