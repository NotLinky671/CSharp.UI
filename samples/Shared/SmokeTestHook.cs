namespace CSharp.UI.Samples;

/// <summary>
/// 示例共用的窗口冒烟测试钩子：设置环境变量 <c>CSHARPUI_SMOKE_FRAMES</c> 后，
/// 渲染指定帧数即打印窗口/DPI/GL 上下文信息并自动退出，用于无人值守验证真实窗口。
/// </summary>
/// <remarks>
/// 用法：<c>CSHARPUI_SMOKE_FRAMES=30 dotnet run --project samples/CSharp.UI.Samples.Counter</c>
/// 未设置该变量时行为与普通运行完全一致（不自动退出）。
/// </remarks>
public static class SmokeTestHook
{
    /// <summary>环境变量名。</summary>
    public const string FrameCountVariable = "CSHARPUI_SMOKE_FRAMES";

    /// <summary>读取请求的帧数；未设置或非法时返回 0。</summary>
    public static int RequestedFrames()
    {
        var value = Environment.GetEnvironmentVariable(FrameCountVariable);
        return int.TryParse(value, out var frames) && frames > 0 ? frames : 0;
    }

    /// <summary>创建 <c>FrameRendered</c> 回调；不需要冒烟测试时返回 null。</summary>
    public static Action<UiHostContext>? CreateFrameHook(string sampleName)
    {
        var target = RequestedFrames();
        if (target == 0)
        {
            return null;
        }

        var rendered = 0;
        return context =>
        {
            rendered++;
            if (rendered < target)
            {
                // 主动请求下一帧，使冒烟测试不依赖窗口系统事件（否则静止窗口可能一直停在第一帧）。
                context.RequestRedraw();
                return;
            }

            Console.WriteLine(
                $"冒烟测试[{sampleName}]：已渲染 {rendered} 帧；" +
                $"逻辑视口 {context.LogicalSize}；framebuffer {context.FramebufferSize}；DPI {context.PixelScale:0.###}；" +
                $"最小化 {context.IsMinimized}；GL 上下文 {context.GlContextDescription}");
            context.RequestExit(0);
        };
    }
}
