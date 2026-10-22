namespace CSharp.UI;

/// <summary>
/// 组合字符几何快照（TI-07 R17）：最终布局完成后的安全阶段一次性捕获的只读排版视图，
/// 供平台“组合串中某个字符在屏幕上的位置”查询（Windows <c>IMR_QUERYCHARPOSITION</c>）使用。
/// </summary>
/// <remarks>
/// <para>
/// 快照只在最终布局后由视图重建：捕获的是<b>显示串</b>（已提交前缀 + 预编辑替换区 + 已提交后缀）的
/// 真实排版、已吸附物理像素的文本原点与有效可编辑裁剪；原生回调只读它，绝不触发
/// Measure/Arrange/Build 或重新排版，也不按平均字宽推算。
/// </para>
/// <para>
/// <b>失效语义：</b>快照携带预编辑文本、文档版本与会话 epoch 三个身份键；任一变化、归属被撤销、
/// 几何被完全裁出或发布 <see cref="TextInputGeometry.Unavailable"/> 时，提供方
/// （<see cref="ICompositionCharGeometrySource"/>）立即不再提供它，绝不返回旧几何。
/// 快照只持有不可变引用（排版结果与字符串），没有逐索引缓存与无界内存。
/// </para>
/// </remarks>
internal sealed class CompositionCharGeometrySnapshot
{
    /// <summary>显示串的真实排版结果（不可变；与绘制共用同一份）。</summary>
    internal required TextLayoutResult Layout { get; init; }

    /// <summary>快照建立时的预编辑串（原生组合串）；身份核对用，不匹配即整份失效。</summary>
    internal required string Preedit { get; init; }

    /// <summary>预编辑在显示串中的起始 UTF-16 偏移（前缀/替换区之后的下标）。</summary>
    internal required int PreeditStart { get; init; }

    /// <summary>有效 caret 的显示串 UTF-16 索引：组合期间为组合 caret，否则为已提交文本 caret。</summary>
    internal required int CaretIndex { get; init; }

    /// <summary>显示串排版原点（逻辑像素，已按同一规则吸附物理像素；含滚动与内边距）。</summary>
    internal required Point Origin { get; init; }

    /// <summary>有效可编辑可见区域（逻辑像素）：内容区 ∩ 祖先裁剪 ∩ 视口；用于应答文档矩形。</summary>
    internal required Rect EditableClip { get; init; }

    /// <summary>快照归属的焦点会话 epoch；换 epoch 的旧快照立即失效。</summary>
    internal required int Epoch { get; init; }

    /// <summary>快照建立时的已提交文档版本；外部写入推进版本后旧快照立即失效。</summary>
    internal required int DocumentVersion { get; init; }

    /// <summary>
    /// 快照建立时 <see cref="UiRuntime.LayoutSerial"/> 的值：任何布局失效请求（InvalidateLayout、
    /// SetEnvironment 字体/环境、SetViewport/DPI、祖先滚动、卸载重建等）都会推进序号——
    /// 失效到下一次安全阶段重建之间的旧快照立即拒绝，绝不用旧排版应答（R17）。
    /// </summary>
    internal required int LayoutSerial { get; init; }

    /// <summary>快照建立时 TextField 的横向滚动偏移；ScrollX 变化后旧快照立即失效。</summary>
    internal required double ScrollX { get; init; }

    /// <summary>
    /// 把组合串的 UTF-16 字符偏移（<c>IMECHARPOSITION.dwCharPos</c>）解析为该字符插入点的
    /// 逻辑像素位置与行高；所有索引都映射到真实排版 caret，绝不回落到当前 caret 或平均宽度。
    /// </summary>
    /// <remarks>
    /// 按官方语义：<paramref name="charPos"/> 是预编辑串内的字符偏移；偏移等于串长时返回末尾插入点。
    /// 偏移落在代理对/组合字素内部时向下吸附到字素边界（取该字素的前导 caret 位置）。
    /// 无预编辑时只有偏移 0 有明确语义：应答当前文档 caret；其余偏移一律拒绝。
    /// </remarks>
    /// <returns>偏移越界或排版数据缺失时返回 false，调用方应答平台失败。</returns>
    internal bool TryResolveCharPosition(uint charPos, out double x, out double y, out double lineHeight)
    {
        x = 0;
        y = 0;
        lineHeight = 0;

        int displayIndex;
        if (Preedit.Length > 0)
        {
            // 有效区间 [0, Preedit.Length]：末端偏移应答预编辑末尾的插入点。
            if (charPos > (uint)Preedit.Length)
            {
                return false;
            }

            displayIndex = PreeditStart + unchecked((int)charPos);
        }
        else
        {
            // 无组合串时偏移 0 指当前文档 caret；其他偏移没有定义。
            if (charPos != 0)
            {
                return false;
            }

            displayIndex = CaretIndex;
        }

        if (displayIndex < 0 || displayIndex > Layout.Text.Length)
        {
            return false;
        }

        var snapped = Layout.SnapToCaretStop(displayIndex);
        var line = Layout.GetLineForIndex(snapped);
        if (line.Bounds.Height <= 0)
        {
            return false;
        }

        x = Origin.X + Layout.GetCaretX(snapped);
        y = Origin.Y + line.Bounds.Y;
        lineHeight = line.Bounds.Height;
        return true;
    }
}

/// <summary>
/// 组合字符几何提供方（TI-07 R17 的最小 internal 接缝）：把最终布局捕获的只读快照交给平台桥。
/// 公开 <see cref="ITextInputClient"/> 契约不变；只有实现本接缝的客户端才应答逐字符几何查询。
/// </summary>
internal interface ICompositionCharGeometrySource
{
    /// <summary>
    /// 当前有效快照；客户端已撤销、epoch 失效、预编辑/文档版本已变化或几何被裁出时为 null。
    /// </summary>
    CompositionCharGeometrySnapshot? CompositionCharGeometry { get; }
}

/// <summary>
/// 平台几何失效接缝（TI-07 P4）：窗口 Move/Resize/DPI/epoch 使<b>平台侧</b>已发布几何失效时，
/// 由平台桥通知客户端重置“已发布”去重状态；公开 <see cref="ITextInputClient"/> 契约不变。
/// </summary>
/// <remarks>
/// <para>
/// 客户端按<b>逻辑</b> <see cref="TextInputGeometry"/> 同值去重；纯窗口移动不改变逻辑几何，
/// 但屏幕原点已变，平台必须收到一次真实重发。平台桥在自身缓存清空后调用本接缝，
/// 使下一次安全阶段同值发布不被去重吞掉（R7 锚点跟随的真实前提）。
/// </para>
/// <para>
/// <b>只重置去重：</b>不清除客户端“当前有效几何”——逻辑锚点在移动后仍然有效，
/// 失效的只是平台侧缓存；不可用几何发布由常规 <c>PublishGeometry(Unavailable)</c> 路径负责。
/// </para>
/// </remarks>
internal interface IGeometryPublishInvalidationSink
{
    /// <summary>平台几何缓存已失效：重置同值去重，保证下一次安全阶段确实重发。</summary>
    void InvalidatePublishedGeometry();
}

/// <summary>
/// 组合身份只读接缝（TI-07 P4 诊断/验收）：把<b>真实</b>焦点 epoch 与核心组合 ID 交给平台桥，
/// 供真实场景证明“滚动/裁出/恢复前后仍是同一组合”。公开 <see cref="ITextInputClient"/> 契约不变。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不在 <see cref="ITextInputClient"/>：</b>组合 ID 是核心事务内部状态，公开接口不应为诊断扩张。
/// </para>
/// <para>
/// <b>语义：</b><see cref="Epoch"/> 与 <see cref="ITextInputClient.Session"/>.SessionId 同值；
/// <see cref="CompositionId"/> 是核心状态机当前（或最近结束）的组合 ID，必须配合
/// <see cref="IsComposing"/> 判断是否活动组合。不要在未组合时把它当作活动身份。
/// </para>
/// </remarks>
internal interface ICompositionIdentitySource
{
    /// <summary>焦点会话 epoch（与公开 Session.SessionId 同值）。</summary>
    int Epoch { get; }

    /// <summary>核心组合 ID（同一 epoch 内单调）；是否活动由 <see cref="IsComposing"/> 决定。</summary>
    int CompositionId { get; }

    /// <summary>核心状态机当前是否处于未提交组合。</summary>
    bool IsComposing { get; }
}
