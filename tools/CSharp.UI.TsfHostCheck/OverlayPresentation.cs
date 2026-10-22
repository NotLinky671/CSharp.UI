namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R34E：overlay 窗口的<b>展示决策/状态核</b>——真实窗口代码（TsfHostWindow 的
/// Apply/UpdateGeometry/Restore/End 路径）与单元测试驱动同一个实例、同一控制流，
/// 不是平行实现。只承载托管状态：唯一 live 快照、clip 隐藏标记、当前元素 id/代数。
/// 原生动作（SetWindowPos/InvalidateRect）由窗口按返回值执行。
/// </summary>
/// <remarks>
/// 不变量：
/// <list type="bullet">
/// <item><see cref="Live"/> 是<b>唯一</b>有效快照槽——裁剪中的新 Update 直接覆写它，
/// 绝不存在“裁剪前旧帧”兜底；恢复展示的就是最近一次发布的同代内容。</item>
/// <item>元素 End/被替换/发布失败 → <see cref="Clear"/> 清空全部状态；此后任何
/// 几何重算/恢复都不得复活展示。</item>
/// <item>恢复展示必须经 <see cref="Restore"/> 用 presenter 的<b>当前</b>快照核对
/// 同元素同代数——不接受陈旧快照顶替。</item>
/// </list>
/// 每个方法返回“应展示的内容”（null = 隐藏窗口）；调用方据此只执行 Show/Hide。
/// </remarks>
internal sealed class OverlayPresentation
{
    /// <summary>当前 live 快照（恒为 Valid；无效/已清为 null）。裁剪中也被最新发布覆写。</summary>
    internal CandidateSnapshot? Live { get; private set; }

    /// <summary>当前因 input 全裁剪而隐藏展示（live 快照仍保留）。</summary>
    internal bool HiddenByClip { get; private set; }

    /// <summary>当前承载快照的元素 id（<b>0 是合法 id</b>，真实微软拼音就下发
    /// id=0 的候选元素——R29B seq71/74 实测）；无快照时由 <see cref="Live"/> 判空，
    /// 本字段不复位语义仅作对齐信息保留 0。</summary>
    internal uint ElementId { get; private set; }

    /// <summary>与 <see cref="ElementId"/> 同组的 live 代数。</summary>
    internal uint Generation { get; private set; }

    /// <summary>
    /// 发布/几何重估的统一入口。<paramref name="incoming"/> 为 null 表示纯几何重锚
    /// （resize/DPI/scroll 后）；<paramref name="clipped"/> 是调用方实测的
    /// “input∩裁剪祖先为空”的真实事实。返回应展示内容，null = 隐藏。
    /// </summary>
    internal CandidateSnapshot? Apply(CandidateSnapshot? incoming, bool clipped)
    {
        if (incoming is { Valid: true } valid)
        {
            // 最新有效发布即唯一 live；可见时展示它，裁剪中仍记录但保持隐藏。
            Live = valid;
            ElementId = valid.ElementId;
            Generation = valid.Generation;
            HiddenByClip = clipped;
            return clipped ? null : valid;
        }

        if (incoming is null)
        {
            // 纯几何重估：无 live 则无内容可展示。
            if (Live is null)
            {
                HiddenByClip = clipped;
                return null;
            }

            HiddenByClip = clipped;
            return clipped ? null : Live;
        }

        // 无效发布（查询失败/来源失效）：如实清空全部展示状态并隐藏，
        // 绝不继续画失败快照冒充有候选。
        Clear();
        return null;
    }

    /// <summary>
    /// 裁剪恢复：input 可见交集重新非空时调用。<paramref name="presenterCurrent"/>
    /// 是 presenter 的<b>当前</b>发布（不经 COM/重读）。存在 live 且同元素同代数的
    /// 有效当前 → 同步为 live 并返回展示；null/invalid/无 live/代数失配 → 清空
    /// 全部状态并隐藏（绝不复活已 End/被替换元素的旧内容）。元素 id=0 合法，
    /// 可恢复性只看 live 存在 + id/generation 匹配，不把 0 当无效哨兵。
    /// </summary>
    internal CandidateSnapshot? Restore(CandidateSnapshot? presenterCurrent)
    {
        HiddenByClip = false;
        if (presenterCurrent is { Valid: true } current
            && Live is not null
            && current.ElementId == ElementId
            && current.Generation == Generation)
        {
            Live = current;
            return current;
        }

        Clear();
        return null;
    }

    /// <summary>清空全部展示状态（End/焦点丢失/来源替换/销毁）。</summary>
    internal void Clear()
    {
        Live = null;
        HiddenByClip = false;
        ElementId = 0;
        Generation = 0;
    }
}
