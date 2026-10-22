namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 只读几何提供方（TI-07 P2b）：向 ACP store 提供缓存好的不可变几何与显示表面。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标契约（冻结）：</b>两类查询都返回<b>屏幕物理像素</b>，与官方 <c>ITextStoreACP::GetTextExt</c>
/// （"bounding box, in screen coordinates"）和 <c>GetScreenExt</c> 一致。提供方负责把客户区/逻辑像素
/// 换算成屏幕物理像素（含窗口屏幕原点、DPI 与客户区原点），store <b>原样返回</b>、不再二次换算，
/// 因此不存在单位混用。
/// </para>
/// <para>
/// <b>只读契约：</b>本接口只<b>返回</b>已缓存数据；实现<b>不得</b>在 <c>GetTextExt</c>/<c>GetScreenExt</c>
/// 调用栈内排版、测量、重建或发通知。真实排版发生在安全阶段，由 P3 在布局后推入新快照。
/// </para>
/// <para>
/// <b>两类几何严格分开：</b>
/// <list type="bullet">
/// <item><description>
/// <b>文本几何</b>（<see cref="TryGetTextRect"/>）对应 <c>GetTextExt</c>：需要有效读锁与当前布局，
/// 未绑定/失效时返回 false，由 store 映射为 <c>TS_E_NOLAYOUT</c>。
/// </description></item>
/// <item><description>
/// <b>显示表面</b>（<see cref="TryGetScreenRect"/>）对应 <c>GetScreenExt</c>：与文本布局版本解耦，
/// 只随真实客户区几何变化，<b>不要求</b>读锁。
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>未注入提供方时：</b>默认实现 <see cref="UnboundGeometryProvider"/> 对两类查询都返回 false，
/// store 因此给出显式 <c>TS_E_NOLAYOUT</c> / 不可用结果，绝不伪造成功几何。生产宿主在正式 TSF
/// 会话中注入 <see cref="HostAcpGeometryProvider"/>（只读不可变缓存）。
/// </para>
/// </remarks>
internal interface IAcpGeometryProvider
{
    /// <summary>
    /// 查询文本范围在<b>屏幕物理像素</b>中的矩形（<c>GetTextExt</c>）。
    /// </summary>
    /// <param name="acpStart">起点（UTF-16 码元偏移）。</param>
    /// <param name="acpEnd">终点（不含）。</param>
    /// <param name="identity">store 在同一读锁内读取的原生代数/文档版本/完整文本身份。</param>
    /// <param name="rect">屏幕物理像素矩形（必须已含窗口屏幕原点）。</param>
    /// <param name="clipped">是否被裁剪。</param>
    /// <param name="knownEmpty">
    /// 是否“已布局但当前完全不可见”。为 true 时 store 返回 <c>S_OK</c> + 全零矩形 + clipped=1，
    /// 而不是失败。
    /// </param>
    /// <returns>是否有可用布局；false 时 store 返回 <c>TS_E_NOLAYOUT</c>。</returns>
    /// <remarks>
    /// <b>零长度范围（caret 查询）：</b><paramref name="acpStart"/> == <paramref name="acpEnd"/> 是
    /// <b>合法</b>请求——TSF 用它定位插入点与候选锚点。实现应返回该位置的 caret 矩形
    /// （通常零宽、行高非零），<b>不得</b>把它当成无效请求，也不得伪造全零矩形。
    /// </remarks>
    bool TryGetTextRect(int acpStart, int acpEnd, in AcpGeometryQueryIdentity identity,
        out TsfRect rect, out bool clipped, out bool knownEmpty);

    /// <summary>
    /// 查询真实客户端显示表面的<b>屏幕物理像素</b>矩形（<c>GetScreenExt</c>）。
    /// </summary>
    /// <param name="rect">屏幕物理像素矩形；不可见/最小化时为全零。</param>
    /// <param name="hresult">
    /// 成功返回 <c>S_OK</c>（含“不可见 → 零矩形”）；原生查询失败时返回负 HRESULT。
    /// </param>
    /// <returns>是否有可用显示表面信息（含“不可见”）。false 时 store 返回 <c>TS_E_NOLAYOUT</c>。</returns>
    bool TryGetScreenRect(out TsfRect rect, out int hresult);
}

/// <summary>
/// 默认未绑定实现（TI-07 P2b）：两类几何都如实报告不可用。
/// </summary>
/// <remarks>
/// 无活动 TSF 会话时 store 不返回任何几何：<c>GetTextExt</c> → <c>TS_E_NOLAYOUT</c>，
/// <c>GetScreenExt</c> → 不可用。绝不返回零矩形冒充成功。
/// </remarks>
internal sealed class UnboundGeometryProvider : IAcpGeometryProvider
{
    /// <summary>共享实例（无状态）。</summary>
    internal static UnboundGeometryProvider Instance { get; } = new();

    private UnboundGeometryProvider()
    {
    }

    /// <inheritdoc />
    public bool TryGetTextRect(int acpStart, int acpEnd, in AcpGeometryQueryIdentity identity,
        out TsfRect rect, out bool clipped, out bool knownEmpty)
    {
        rect = default;
        clipped = false;
        knownEmpty = false;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetScreenRect(out TsfRect rect, out int hresult)
    {
        rect = default;
        hresult = TsfHResult.TS_E_NOLAYOUT;
        return false;
    }
}

/// <summary>
/// <c>GetTextExt</c> 查询时由 store 在<b>同一读锁内</b>构造的只读身份。
/// </summary>
/// <remarks>
/// provider 只比较本值与不可变缓存，<b>不</b>在查询栈内访问活动客户端、排版或发通知。
/// <see cref="Text"/> 是 store 当前视图的完整文本，用于完整串身份核对（不是长度比较）。
/// </remarks>
internal readonly record struct AcpGeometryQueryIdentity(
    long NativeGeneration, long DocumentVersion, string Text);

/// <summary>
/// 安全阶段发布的一次不可变几何缓存（逻辑布局 + 精确身份 + 屏幕换算参数）。
/// </summary>
/// <remarks>所有字段在发布时冻结；provider 查询只读它，绝不重新排版或访问客户端。</remarks>
internal sealed record HostGeometryCache(
    TextInputGeometry Logical,
    CompositionCharGeometrySnapshot Char,
    string DisplayText,
    string Preedit,
    int Epoch,
    int CoreDocumentVersion,
    long StoreDocumentVersion,
    long NativeGeneration,
    long MirrorRevision,
    long NativeRevision,
    int LayoutSerial,
    double ScrollX,
    TsfRect ClientScreen,
    double PixelScale);

/// <summary>
/// 生产几何提供方：只发布通过完整身份校验的不可变缓存；查询只做只读比较与矩形换算。
/// </summary>
/// <remarks>
/// <para>
/// <b>身份：</b>原生代数、store 文档版本（long）与完整显示串必须与查询时的 store 视图一致；
/// 客户端 epoch/核心版本/预编辑/显示串由 bridge 在发布前核对。任何不一致都由 store 映射为
/// <c>TS_E_NOLAYOUT</c>，绝不返回近似或旧坐标。
/// </para>
/// <para><b>线程：</b>只在 UI 线程写；读写通过 <see cref="Volatile"/> 交换不可变引用，不加锁。</para>
/// </remarks>
internal sealed class HostAcpGeometryProvider : IAcpGeometryProvider
{
    /// <summary>显示表面与文本布局<b>独立</b>：文本几何失效时客户区/屏幕矩形仍然有效。</summary>
    private sealed record SurfaceCache(TsfRect Rect, int HResult);

    private HostGeometryCache? _cache;
    private SurfaceCache? _surface;

    internal HostGeometryCache? Current => Volatile.Read(ref _cache);

    /// <summary>发布文本几何缓存（<b>不</b>改变显示表面）。</summary>
    internal void Publish(HostGeometryCache cache) => Volatile.Write(ref _cache, cache);

    /// <summary>清除文本几何缓存；显示表面保持（GetScreenExt 与文本布局版本解耦）。</summary>
    internal void Clear() => Volatile.Write(ref _cache, null);

    /// <summary>发布可用显示表面（屏幕物理像素）。</summary>
    internal void PublishSurface(TsfRect rect)
        => Volatile.Write(ref _surface, new SurfaceCache(rect, TsfHResult.S_OK));

    /// <summary>发布“不可见但查询成功”：全零矩形 + S_OK（最小化/隐藏）。</summary>
    internal void PublishSurfaceHidden() => PublishSurface(default);

    /// <summary>原生客户区/窗口查询失败：如实返回负 HRESULT，绝不用零矩形冒充成功。</summary>
    internal void PublishSurfaceFailure(int hresult)
    {
        if (hresult >= 0)
        {
            hresult = TsfHResult.E_FAIL;
        }

        Volatile.Write(ref _surface, new SurfaceCache(default, hresult));
    }

    /// <summary>窗口表面本身失效（销毁/卸载）：清除后由 GetScreenExt 返回 NOLAYOUT。</summary>
    internal void ClearSurface() => Volatile.Write(ref _surface, null);

    /// <inheritdoc />
    public bool TryGetTextRect(int acpStart, int acpEnd, in AcpGeometryQueryIdentity identity,
        out TsfRect rect, out bool clipped, out bool knownEmpty)
    {
        rect = default;
        clipped = false;
        knownEmpty = false;

        var cache = Current;
        if (cache is null
            || identity.NativeGeneration != cache.NativeGeneration
            || identity.DocumentVersion != cache.StoreDocumentVersion
            || !string.Equals(identity.Text ?? string.Empty, cache.DisplayText, StringComparison.Ordinal))
        {
            return false;
        }

        var text = cache.Char.Layout.Text;
        if (acpStart < 0 || acpEnd < acpStart || acpEnd > text.Length)
        {
            return false;
        }

        if (!double.IsFinite(cache.PixelScale) || cache.PixelScale <= 0)
        {
            return false;
        }

        var isCaret = acpStart == acpEnd;
        Rect logical;
        if (isCaret)
        {
            var caret = cache.Char.Layout.SnapToCaretStop(acpStart);
            logical = cache.Char.Layout.GetCaretBounds(caret, 1.5)
                .Translate(cache.Char.Origin.X, cache.Char.Origin.Y);
        }
        else
        {
            logical = cache.Char.Layout.GetRangeBounds(acpStart, acpEnd)
                .Translate(cache.Char.Origin.X, cache.Char.Origin.Y);
        }

        if (!IsFinite(logical))
        {
            return false;
        }

        var visible = logical.Intersect(cache.Char.EditableClip);
        if (visible.IsEmpty)
        {
            if (isCaret)
            {
                // caret 已完全裁出：真正不可布局，绝不返回旧/伪造坐标。
                return false;
            }

            // 已布局但当前完全不可见：S_OK + 零矩形 + clipped=1。
            knownEmpty = true;
            clipped = true;
            return true;
        }

        rect = ToScreen(visible, cache);
        clipped = !cache.Char.EditableClip.Contains(logical);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetScreenRect(out TsfRect rect, out int hresult)
    {
        // 只依赖窗口表面缓存，不读文本布局缓存：文本失效/无预编辑时仍如实返回客户区。
        var surface = Volatile.Read(ref _surface);
        if (surface is null)
        {
            rect = default;
            hresult = TsfHResult.TS_E_NOLAYOUT;
            return false;
        }

        rect = surface.Rect;
        hresult = surface.HResult;
        return surface.HResult >= 0;
    }

    private static TsfRect ToScreen(Rect logical, HostGeometryCache cache)
    {
        var origin = cache.ClientScreen;
        return TsfRect.FromLtrb(
            origin.Left + ToPixels(logical.Left, cache.PixelScale),
            origin.Top + ToPixels(logical.Top, cache.PixelScale),
            origin.Left + ToPixels(logical.Right, cache.PixelScale),
            origin.Top + ToPixels(logical.Bottom, cache.PixelScale));
    }

    private static int ToPixels(double logical, double scale)
    {
        var value = Math.Round(logical * scale, MidpointRounding.AwayFromZero);
        return value >= int.MaxValue ? int.MaxValue
            : value <= int.MinValue ? int.MinValue
            : (int)value;
    }

    private static bool IsFinite(Rect rect)
        => double.IsFinite(rect.X)
            && double.IsFinite(rect.Y)
            && double.IsFinite(rect.Width)
            && double.IsFinite(rect.Height);
}
