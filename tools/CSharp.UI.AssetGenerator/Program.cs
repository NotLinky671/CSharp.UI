using SkiaSharp;

namespace CSharp.UI.AssetGenerator;

/// <summary>
/// 生成示例图片资源：<c>samples/Shared/Assets/logo.png</c>。
/// </summary>
/// <remarks>
/// 资源由本工具绘制生成（不使用任何第三方素材），因此可以随仓库分发；
/// 运行方式：<c>dotnet run --project tools/CSharp.UI.AssetGenerator</c>。
/// </remarks>
internal static class Program
{
    private const int Size = 128;

    private static int Main(string[] args)
    {
        var output = args.Length > 0
            ? args[0]
            : Path.Combine(FindRepositoryRoot(), "samples", "Shared", "Assets", "logo.png");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 圆角渐变底：蓝色强调色。
        var bounds = new SKRect(4, 4, Size - 4, Size - 4);
        using var background = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                bounds.Location,
                new SKPoint(bounds.Right, bounds.Bottom),
                [new SKColor(37, 99, 235), new SKColor(76, 141, 255)],
                null,
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRoundRect(bounds, 24, 24, background);

        // 白色折线（与示例主题一致的简单图形）。
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 7,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = SKColors.White,
        };
        using var path = new SKPath();
        path.MoveTo(36, 84);
        path.LineTo(62, 48);
        path.LineTo(92, 90);
        canvas.DrawPath(path, stroke);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var file = File.Create(output))
        {
            data.SaveTo(file);
        }

        Console.WriteLine($"已生成示例图片：{output}（{new FileInfo(output).Length} 字节）");
        return 0;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSharp.UI.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
