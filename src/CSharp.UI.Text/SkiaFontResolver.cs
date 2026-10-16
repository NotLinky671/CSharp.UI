using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CSharp.UI.Skia;

/// <summary>
/// 一次排版使用的字体实例：<see cref="SKFont"/>（字号）+ <see cref="SKShaper"/>（HarfBuzz shaping）。
/// 由 <see cref="SkiaFontResolver"/> 拥有并缓存，调用方不得释放。
/// </summary>
internal sealed class FontInstance : IDisposable
{
    private bool _disposed;

    public FontInstance(SKTypeface typeface, float size)
    {
        Typeface = typeface;
        // Subpixel=false：字形原点吸附到整数设备像素，这是本框架的“整数定位”策略，
        // 目的是让 Alpha8 灰度遮罩里的字形落在整数物理像素上（与绘制端的原点吸附契约一致）。
        // 注意：Subpixel 控制的是小数像素定位，与是否使用 LCD 子像素抗锯齿无关；
        // 是否 LCD 由 Edging 决定，这里保持 SKFontEdging.Antialias，即灰度抗锯齿，不会出现彩边。
        Font = new SKFont(typeface, size)
        {
            Subpixel = false,
            Edging = SKFontEdging.Antialias,
        };
        Shaper = new SKShaper(typeface);
    }

    /// <summary>字体族。</summary>
    public SKTypeface Typeface { get; }

    /// <summary>指定字号的字体（用于度量与栅格化）。</summary>
    public SKFont Font { get; }

    /// <summary>HarfBuzz shaping 器。</summary>
    public SKShaper Shaper { get; }

    /// <summary>释放 SKFont 与 SKShaper；<see cref="Typeface"/> 由解析器统一管理引用计数。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shaper.Dispose();
        Font.Dispose();
    }
}

/// <summary>
/// 字体解析：把 <see cref="FontDescription"/> 解析为 SKTypeface，做缺字回退，并缓存 SKTypeface、SKFont 与 SKShaper。
/// </summary>
/// <remarks>
/// 缓存与释放约定：
/// <list type="bullet">
/// <item>首选字体按“文件路径/家族名 + 粗细 + 斜体 + 回退名列表”缓存，容量 <see cref="PrimaryCapacity"/>。</item>
/// <item>缺字回退按“码点 + 粗细 + 斜体”缓存，容量 <see cref="FallbackCapacity"/>，解析不到时缓存 null 以避免重复查询系统字体库。</item>
/// <item>SKFont/SKShaper 按“字体族 + 字号”缓存，容量 <see cref="FontInstanceCapacity"/>。</item>
/// <item>所有 SKTypeface 在解析器内部按引用计数持有，缓存淘汰或 <see cref="Clear"/> 时释放。</item>
/// </list>
/// </remarks>
internal sealed class SkiaFontResolver : IDisposable
{
    private const int PrimaryCapacity = 32;
    private const int FallbackCapacity = 256;
    private const int FontInstanceCapacity = 64;

    private readonly LruCache<TypefaceKey, SKTypeface> _primaries = new(PrimaryCapacity);
    private readonly LruCache<FallbackKey, SKTypeface?> _fallbacks = new(FallbackCapacity);
    private readonly LruCache<FontInstanceKey, FontInstance> _instances = new(FontInstanceCapacity);
    private readonly Dictionary<SKTypeface, int> _references = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _reportedFallbacks = new(StringComparer.Ordinal);
    private readonly Action<TextDiagnostic> _report;
    private bool _disposed;

    public SkiaFontResolver(Action<TextDiagnostic> report)
    {
        _report = report;
        _primaries.OnEvicted = (_, typeface) => Release(typeface);
        _fallbacks.OnEvicted = (_, typeface) =>
        {
            if (typeface is not null)
            {
                Release(typeface);
            }
        };
        _instances.OnEvicted = (key, instance) =>
        {
            instance.Dispose();
            Release(key.Typeface);
        };
    }

    /// <summary>
    /// <see cref="FontFamily.FromFile"/> 指向的文件不存在或无法解析时：
    /// false（默认）记录诊断并回退到平台默认字体，true 直接抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public bool ThrowOnMissingFontFile { get; set; }

    /// <summary>解析首选字体族；结果由内部缓存持有，调用方不要释放。</summary>
    public SKTypeface ResolvePrimary(FontDescription font)
    {
        var family = font.Family;
        var key = new TypefaceKey(
            family.FilePath,
            family.FamilyName,
            string.Join('\n', family.Fallbacks),
            (int)font.Weight,
            font.Italic);
        return _primaries.GetOrAdd(key, CreatePrimary, out _);
    }

    /// <summary>取回（必要时创建）指定字体族与字号的字体实例；由内部缓存持有，调用方不要释放。</summary>
    public FontInstance GetFontInstance(SKTypeface typeface, double size)
    {
        var key = new FontInstanceKey(typeface, (float)Math.Max(0.1, size));
        return _instances.GetOrAdd(key, k =>
        {
            Retain(k.Typeface);
            return new FontInstance(k.Typeface, k.Size);
        }, out _);
    }

    /// <summary>解析 <paramref name="font"/> 的首选字体实例。</summary>
    public FontInstance GetFontInstance(FontDescription font) => GetFontInstance(ResolvePrimary(font), font.Size);

    /// <summary>
    /// 为单个码点选择字体实例：首选字体覆盖该码点时用它，否则按回退家族、系统缺字回退的顺序选择。
    /// </summary>
    public FontInstance GetFontInstanceForCharacter(SKTypeface primary, FontDescription font, int codepoint)
    {
        if (codepoint < 0 || primary.ContainsGlyph(codepoint))
        {
            return GetFontInstance(primary, font.Size);
        }

        var fallback = ResolveFallback(primary, font, codepoint);
        return fallback is null ? GetFontInstance(primary, font.Size) : GetFontInstance(fallback, font.Size);
    }

    /// <summary>清空全部字体缓存并释放底层原生对象。</summary>
    public void Clear()
    {
        _instances.Clear();
        _primaries.Clear();
        _fallbacks.Clear();

        // 兜底：释放仍被引用但已不在缓存中的字体族（正常情况下不会发生）。
        foreach (var typeface in _references.Keys.ToArray())
        {
            typeface.Dispose();
        }

        _references.Clear();
        _reportedFallbacks.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    private SKTypeface CreatePrimary(TypefaceKey key)
    {
        var style = CreateStyle(key.Weight, key.Italic);
        if (key.FilePath is { Length: > 0 } path)
        {
            if (File.Exists(path))
            {
                SKTypeface? fromFile = null;
                try
                {
                    fromFile = SKTypeface.FromFile(path, 0);
                }
                catch (Exception exception)
                {
                    _report(new TextDiagnostic(TextDiagnosticKind.MissingFontFile, $"字体文件 \"{path}\" 无法解析：{exception.Message}", path));
                }

                if (fromFile is not null)
                {
                    return Retain(fromFile);
                }

                _report(new TextDiagnostic(TextDiagnosticKind.MissingFontFile, $"字体文件 \"{path}\" 不是有效的字体，已回退到回退家族或平台默认字体。", path));
            }
            else
            {
                var message = $"字体文件不存在：\"{path}\"，已回退到回退家族或平台默认字体。";
                if (ThrowOnMissingFontFile)
                {
                    throw new InvalidOperationException(message);
                }

                _report(new TextDiagnostic(TextDiagnosticKind.MissingFontFile, message, path));
            }
        }
        else if (key.FamilyName is { Length: > 0 } familyName)
        {
            var matched = SKFontManager.Default.MatchFamily(familyName, style);
            if (matched is not null)
            {
                return Retain(matched);
            }

            _report(new TextDiagnostic(TextDiagnosticKind.MissingFontFamily, $"未找到字体家族 \"{familyName}\"，已回退到回退家族或平台默认字体。", familyName));
        }

        foreach (var fallback in FallbackNames(key))
        {
            if (string.IsNullOrWhiteSpace(fallback))
            {
                continue;
            }

            var matched = SKFontManager.Default.MatchFamily(fallback, style);
            if (matched is not null)
            {
                return Retain(matched);
            }
        }

        return Retain(SKTypeface.Default);
    }

    private SKTypeface? ResolveFallback(SKTypeface primary, FontDescription font, int codepoint)
    {
        var key = new FallbackKey(codepoint, (int)font.Weight, font.Italic);
        if (_fallbacks.TryGet(key, out var cached))
        {
            return cached;
        }

        var resolved = CreateFallback(primary, font, codepoint);
        _fallbacks.GetOrAdd(key, _ => resolved, out _);
        return resolved;
    }

    private SKTypeface? CreateFallback(SKTypeface primary, FontDescription font, int codepoint)
    {
        var style = CreateStyle((int)font.Weight, font.Italic);

        // 1) 按声明顺序尝试回退家族。
        foreach (var name in font.Family.Fallbacks)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var candidate = SKFontManager.Default.MatchFamily(name, style);
            if (candidate is null)
            {
                continue;
            }

            if (candidate.ContainsGlyph(codepoint))
            {
                return Retain(candidate);
            }

            candidate.Dispose();
        }

        // 2) 系统缺字回退：先以首选家族为提示，再做全局回退（例如 Windows 的中文回退到 Microsoft YaHei UI）。
        var familyName = primary.FamilyName;
        var matched = string.IsNullOrEmpty(familyName)
            ? null
            : SKFontManager.Default.MatchCharacter(familyName, style, null!, codepoint);
        matched ??= SKFontManager.Default.MatchCharacter(null!, style, null!, codepoint);
        if (matched is not null)
        {
            if (matched.ContainsGlyph(codepoint))
            {
                // 同一对（首选字体, 回退字体）只记录一次诊断，避免中文等大量缺字场景刷屏。
                var pairKey = string.Concat(font.Family.DisplayName, "\u0000", matched.FamilyName);
                if (_reportedFallbacks.Add(pairKey))
                {
                    _report(new TextDiagnostic(
                        TextDiagnosticKind.CharacterFallback,
                        $"字体 \"{font.Family.DisplayName}\" 无法覆盖部分字符（例如 U+{codepoint:X4}），已回退到 \"{matched.FamilyName}\"。",
                        matched.FamilyName));
                }

                return Retain(matched);
            }

            matched.Dispose();
        }

        _report(new TextDiagnostic(
            TextDiagnosticKind.MissingGlyph,
            $"字体 \"{font.Family.DisplayName}\" 及其回退都无法覆盖字符 U+{codepoint:X4}，将绘制缺字方框。",
            $"U+{codepoint:X4}"));
        return null;
    }

    /// <summary>回退家族名列表；以换行符拼接后参与缓存键相等性比较。</summary>
    private static string[] FallbackNames(TypefaceKey key)
        => key.Fallbacks.Length == 0 ? [] : key.Fallbacks.Split('\n');

    private SKTypeface Retain(SKTypeface typeface)
    {
        _references[typeface] = _references.TryGetValue(typeface, out var count) ? count + 1 : 1;
        return typeface;
    }

    private void Release(SKTypeface typeface)
    {
        if (!_references.TryGetValue(typeface, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _references.Remove(typeface);
            typeface.Dispose();
        }
        else
        {
            _references[typeface] = count - 1;
        }
    }

    private static SKFontStyle CreateStyle(int weight, bool italic)
        => new(Math.Clamp(weight, 1, 1000), (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private readonly record struct TypefaceKey(string? FilePath, string? FamilyName, string Fallbacks, int Weight, bool Italic);

    private readonly record struct FallbackKey(int Codepoint, int Weight, bool Italic);

    private readonly record struct FontInstanceKey(SKTypeface Typeface, float Size);
}

