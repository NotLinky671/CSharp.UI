namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R34：overlay 的<b>真实绘制通道</b>。窗口侧实现是 root 客户区内的
/// no-activate child；测试用同一接口注入托管替身——同一 presenter 控制流
/// 驱动两者（Apply/Hide/Invalidate 的真实调用序）。
/// </summary>
internal interface ICandidateOverlay
{
    /// <summary>把当前快照应用到 overlay 内容（真实 HWND 更新或替身记录）。</summary>
    void Apply(CandidateSnapshot snapshot);

    /// <summary>隐藏 overlay（含清空展示）；<paramref name="id"/>/<paramref name="generation"/>
    /// 是将要/曾经展示的来源标识，供日志对齐。</summary>
    void Hide(uint id, uint generation, string reason);

    /// <summary>请求重绘（真实路径为 InvalidateRect；替身可记录调用）。</summary>
    void Invalidate();
}

/// <summary>
/// R34 overlay 的<b>可见性/几何</b>体检结果（executor 用；纯托管判定可测）。
/// <see cref="VisibleRect"/> 为 overlay 在屏幕上的实际矩形（未展示/全裁剪为空矩形）。
/// </summary>
internal readonly record struct OverlayInspection(
    bool Ok, string? Failure, TsfRect VisibleRect, uint ElementId, uint Generation,
    uint SnapshotId, int EntryCount, bool HiddenByClip, bool Visible = false)
{
    internal static OverlayInspection Unavailable(string reason)
        => new(false, reason, default, 0, 0, 0, 0, false, false);
}

/// <summary>
/// 本工具内部的 overlay 窗口契约：UI 线程上创建/定位/销毁真实 HWND、
/// 在 <see cref="TsfHostWindow.Paint"/> 的同一条绘制路径上描边与文本。
/// </summary>
internal interface IHostCandidateOverlayWindow : ICandidateOverlay
{
    /// <summary>overlay HWND；未创建/已销毁为 0。</summary>
    IntPtr Hwnd { get; }

    /// <summary>当前是否应用了有效快照（即 overlay 有内容可画）。</summary>
    bool HasContent { get; }

    /// <summary>当前是否因 input 全裁剪而隐藏（快照仍保留；只隐藏展示）。</summary>
    bool HiddenByClip { get; }

    /// <summary>当前快照 id/generation（无快照为 0）。</summary>
    uint CurrentElementId { get; }

    uint CurrentGeneration { get; }

    /// <summary>已应用快照的只读副本（UI 线程外可见；可能为 null）。</summary>
    CandidateSnapshot? Snapshot { get; }

    /// <summary>按当前真实几何/UI 线程事实重算 overlay 矩形并检查展示状态。</summary>
    OverlayInspection Inspect(string stage);
}
