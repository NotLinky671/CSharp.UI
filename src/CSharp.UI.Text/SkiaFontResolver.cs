using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CSharp.UI.Skia;

/// <summary>
/// 一个已解析字体族 + 字号实例的租约：只要任一租约存活，<see cref="SkiaFontResolver"/> 的
/// 有界缓存即使淘汰该实例也会推迟释放底层 <see cref="SKFont"/>/<see cref="SKShaper"/>/<see cref="SKTypeface"/>，
/// 直到最后一份租约归还。仅供富文本排版后端把已排版字形绑定到具体 face 使用；plain 排版路径不取租约。
/// </summary>
internal sealed class FontInstanceLease : IDisposable
{
    private FontInstance? _instance;

    internal FontInstanceLease(FontInstance instance) => _instance = instance;

    /// <summary>租约仍存活时返回被钉住的实例；已归还返回 null（后端保证此时不再访问）。</summary>
    internal FontInstance? TryInstance => _instance;

    /// <summary>被钉住的实例（仍存活时使用）。</summary>
    internal FontInstance Instance
        => _instance ?? throw new ObjectDisposedException(nameof(FontInstanceLease));

    /// <summary>归还租约；幂等。最后一个租约归还且实例已被缓存淘汰时释放底层原生对象。</summary>
    public void Dispose()
    {
        var instance = Interlocked.Exchange(ref _instance, null);
        instance?.ReleaseLease();
    }
}

/// <summary>
/// 一次排版使用的字体实例：<see cref="SKFont"/>（字号）+ <see cref="SKShaper"/>（HarfBuzz shaping）。
/// 由 <see cref="SkiaFontResolver"/> 拥有并缓存，调用方不得释放。
/// </summary>
internal sealed class FontInstance : IDisposable
{
    private bool _disposed;
    private int _leases;
    private SkiaFontResolver? _owner;

    /// <summary>解析器在该实例上的内部引用；租约归零时经它归还并触发延迟释放检查。</summary>
    internal SkiaFontResolver? Owner
    {
        get => _owner;
        set => _owner = value;
    }

    /// <summary>当前存活租约数（只随 <see cref="SkiaFontResolver.AcquireLease"/> 与归还变化）。</summary>
    internal int LeaseCount => _leases;

    internal void AddLease() => _leases++;

    /// <summary>归还一份租约；归零时通知解析器检查是否可以真正释放。</summary>
    internal void ReleaseLease()
    {
        _leases--;
        _owner?.OnLeaseReleased(this);
    }


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
/// <item>SKFont/SKShaper 按“字体族 + 字号”缓存，容量 <see cref="FontInstanceCapacity"/>；
/// 被 <see cref="FontInstanceLease"/> 钉住的实例淘汰/清理时推迟释放（SKFont/SKShaper 与 SKTypeface 引用一起），
/// 直到最后一份租约归还。</item>
/// <item>所有 SKTypeface 在解析器内部按引用计数持有，缓存淘汰或 <see cref="Clear"/> 时释放；
/// 因租约存活而延迟的释放排在 <see cref="_deferred"/> 队列中，租约归还时补释放。</item>
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
    private readonly List<FontInstance> _deferred = [];
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
        _instances.OnEvicted = (key, instance) => RetireInstance(instance, key.Typeface);
    }

    /// <summary>
    /// 为富文本排版后端钉住一个字体实例：返回的 <see cref="FontInstanceLease"/> 在
    /// <see cref="IDisposable.Dispose"/> 之前保证底层 <see cref="SKFont"/>/<see cref="SKShaper"/>/字体族
    /// 不被缓存淘汰或 <see cref="Clear"/> 释放。实例随后仍可能被淘汰出缓存，但租约持有者不受影响。
    /// </summary>
    public FontInstanceLease AcquireLease(FontInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        instance.AddLease();
        return new FontInstanceLease(instance);
    }

    /// <summary>租约归还回调：实例若已退出缓存（待释放队列中），现在补释放。</summary>
    internal void OnLeaseReleased(FontInstance instance)
    {
        if (instance.LeaseCount > 0 || !_deferred.Remove(instance))
        {
            return;
        }

        var typeface = instance.Typeface;
        instance.Owner = null;
        instance.Dispose();
        Release(typeface);
    }

    /// <summary>
    /// 缓存淘汰/<see cref="Clear"/> 触发的实例退役：有存活租约时排入延迟释放队列（不释放 SKFont/Shaper，
    /// 也暂保留 typeface 引用——等最后一个租约归还时一起补释放），无租约立即释放。
    /// </summary>
    private void RetireInstance(FontInstance instance, SKTypeface typeface)
    {
        if (instance.LeaseCount > 0)
        {
            _deferred.Add(instance);
            return;
        }

        instance.Owner = null;
        instance.Dispose();
        Release(typeface);
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
        var instance = _instances.GetOrAdd(key, k =>
        {
            Retain(k.Typeface);
            return new FontInstance(k.Typeface, k.Size);
        }, out _);
        // Owner 每次写（幂等）：实例被有界缓存淘汰后若因存活租约排入延迟队列，
        // 再次经 GetOrAdd 重新入缓存时不该再被“补释放”——此时该实例已不在 _deferred 中，
        // RetireInstance 只在真实退役路径置 null。
        instance.Owner = this;
        return instance;
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
    /// <summary>清空全部字体缓存并释放底层原生对象；有存活租约的实例按 <see cref="RetireInstance"/> 延迟释放。</summary>
    public void Clear()
    {
        _instances.Clear();
        _primaries.Clear();
        _fallbacks.Clear();

        // 兜底：三个缓存 Clear 之后，_references 里只剩延迟队列实例持有的份额（每个延迟实例一份）。
        // 这些份额必须保留在引用表里——最后一个租约归还时由 OnLeaseReleased→Release 补释放；
        // 若存在既不在缓存也不在延迟队列中的残留引用（理论上不该发生），按旧语义直接 dispose。
        var deferredFaces = new HashSet<SKTypeface>(ReferenceEqualityComparer.Instance);
        foreach (var instance in _deferred)
        {
            deferredFaces.Add(instance.Typeface);
        }

        foreach (var typeface in _references.Keys.ToArray())
        {
            if (deferredFaces.Contains(typeface))
            {
                continue;
            }

            _references.Remove(typeface);
            typeface.Dispose();
        }

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

