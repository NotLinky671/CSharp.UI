using SkiaSharp;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>字体解析、缺字回退与字体文件诊断测试。</summary>
public class FontResolutionTests
{
    [Fact]
    public void 中文文本_通过缺字回退可排版并栅格化()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("中文测试"));

        Assert.True(layout.Size.Width > 0);
        var cjkAvailable = SKFontManager.Default.MatchCharacter(0x4E2D) is not null;
        if (cjkAvailable)
        {
            // 系统有中文字体时必须通过缺字回退覆盖，而不是绘制缺字方框。
            Assert.DoesNotContain(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingGlyph);
        }
        else
        {
            Assert.Contains(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingGlyph);
        }

        var raster = service.Rasterize(layout);
        Assert.True(raster.Pixels.Any(value => value > 0), "中文应产生非空覆盖率遮罩");
    }

    [Fact]
    public void 字体文件不存在_默认记录诊断并回退到平台默认字体()
    {
        using var service = new SkiaTextService();
        var path = TextTestHelpers.UniqueTempPath(".ttf");
        var font = new FontDescription(FontFamily.FromFile(path), 16);

        var layout = service.Layout(TextTestHelpers.Request("Hello", font: font));

        Assert.True(layout.Size.Width > 0, "字体文件缺失时仍应回退排版");
        Assert.NotEmpty(service.Diagnostics);
        var diagnostic = service.Diagnostics.First(d => d.Kind == TextDiagnosticKind.MissingFontFile);
        Assert.Contains(path, diagnostic.Message);
        Assert.Equal(path, diagnostic.Source);
        Assert.NotNull(service.ResolvePrimaryFamilyName(font));
    }

    [Theory]
    [InlineData(@"C:\definitely-missing-font.ttf")]
    [InlineData(@"Z:\definitely\missing\font.ttf")]
    public void 字体文件不存在_可配置为抛出带路径的诊断(string path)
    {
        using var service = new SkiaTextService { ThrowOnMissingFontFile = true };
        var font = new FontDescription(FontFamily.FromFile(path), 16);

        var exception = Assert.Throws<InvalidOperationException>(
            () => service.Layout(TextTestHelpers.Request("Hello", font: font)));
        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void 字体文件内容无效_记录诊断并回退()
    {
        var path = TextTestHelpers.UniqueTempPath(".ttf");
        File.WriteAllText(path, "这不是字体文件内容");
        try
        {
            using var service = new SkiaTextService();
            var layout = service.Layout(TextTestHelpers.Request("Hello", font: new FontDescription(FontFamily.FromFile(path), 16)));

            Assert.True(layout.Size.Width > 0);
            Assert.Contains(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingFontFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 字体文件有效_按文件解析字体族()
    {
        var path = TextTestHelpers.UniqueTempPath(".ttf");
        using (var stream = SKTypeface.Default.OpenStream())
        {
            Assert.NotNull(stream);
            using var memory = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, chunk.Length)) > 0)
            {
                memory.Write(chunk, 0, read);
            }

            Assert.True(memory.Length > 0, "系统默认字体应能导出为字体文件");
            File.WriteAllBytes(path, memory.ToArray());
        }

        try
        {
            using var service = new SkiaTextService();
            var font = new FontDescription(FontFamily.FromFile(path), 16);
            var layout = service.Layout(TextTestHelpers.Request("Hello", font: font));

            Assert.True(layout.Size.Width > 0);
            Assert.DoesNotContain(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingFontFile);
            Assert.Equal(SKTypeface.Default.FamilyName, service.ResolvePrimaryFamilyName(font));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 家族名不存在_记录诊断并回退()
    {
        using var service = new SkiaTextService();
        var font = FontDescription.FromName("CSharpUI-No-Such-Family-42", 16);

        var layout = service.Layout(TextTestHelpers.Request("Hello", font: font));

        Assert.True(layout.Size.Width > 0);
        Assert.Contains(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingFontFamily);
        Assert.False(string.IsNullOrEmpty(service.ResolvePrimaryFamilyName(font)));
    }

    [Fact]
    public void 回退家族_按声明顺序在首选家族缺失时生效()
    {
        // 用系统真实存在的家族名做回退，避免依赖具体平台的字体。
        var existing = SKFontManager.Default.FontFamilies.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        Assert.False(string.IsNullOrEmpty(existing));

        using var service = new SkiaTextService();
        var font = FontFamily.FromName("CSharpUI-No-Such-Family-42").WithFallbacks(existing!);
        var description = new FontDescription(font, 16);

        Assert.Equal(existing, service.ResolvePrimaryFamilyName(description));
        Assert.Contains(service.Diagnostics, d => d.Kind == TextDiagnosticKind.MissingFontFamily);
        Assert.True(service.Measure(TextTestHelpers.Request("Hello", font: description)).Width > 0);
    }

    [Fact]
    public void 缺字回退_诊断按字体族去重且数量有界()
    {
        using var service = new SkiaTextService();
        for (var i = 0; i < 50; i++)
        {
            service.Layout(TextTestHelpers.Request($"第{i}行中文"));
        }

        Assert.True(service.Diagnostics.Count <= 64, $"诊断数量应有界：{service.Diagnostics.Count}");
        var fallbackDiagnostics = service.Diagnostics.Where(d => d.Kind == TextDiagnosticKind.CharacterFallback).ToList();
        Assert.True(fallbackDiagnostics.Count <= 8, $"同一回退字体只应记录一次：{fallbackDiagnostics.Count}");
    }
}
