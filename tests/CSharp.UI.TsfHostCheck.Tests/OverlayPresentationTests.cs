using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R34E：overlay 展示决策/状态核 <see cref="OverlayPresentation"/> 的聚焦测试——
/// 与真实窗口路径（TsfHostWindow 的 Apply/UpdateGeometry/Restore/End）共用同一实例
/// 同一控制流，不是玩具替身。覆盖：hide→update→visible 恢复<b>最新同代</b>内容、
/// 裁剪中可见重锚保持隐藏、End→恢复不得复活、presenter 失配→清空隐藏、
/// 无效快照清空、无可展示内容。
/// </summary>
public sealed class OverlayPresentationTests
{
    private static CandidateSnapshot Snap(uint id, uint generation,
        bool valid = true, int entries = 2, string? failure = null)
        => new(id, generation,
            valid ? Enumerable.Range(0, entries)
                    .Select(i => new CandidateSnapshotEntry((uint)i, $"c{i}")).ToArray()
                : Array.Empty<CandidateSnapshotEntry>(),
            0, 0, (uint)entries, 0, 0, valid, failure);

    [Fact]
    public void ApplyVisibleShowsLatestSnapshot()
    {
        var p = new OverlayPresentation();
        var snap = Snap(7, 1);
        Assert.Same(snap, p.Apply(snap, clipped: false));
        Assert.False(p.HiddenByClip);
        Assert.Equal(7u, p.ElementId);
        Assert.Equal(1u, p.Generation);
    }

    [Fact]
    public void ClipThenUpdateThenRestoreShowsLatestSameGeneration()
    {
        var p = new OverlayPresentation();
        var gen1 = Snap(11, 1, entries: 1);
        var gen2 = Snap(11, 1, entries: 3);

        Assert.NotNull(p.Apply(gen1, clipped: false));
        Assert.Null(p.Apply(null, clipped: true));          // 全裁剪：只隐藏
        Assert.True(p.HiddenByClip);

        // 裁剪中到达的新 Update 覆写唯一 live（同元素同代数）。
        Assert.Null(p.Apply(gen2, clipped: true));
        Assert.Same(gen2, p.Live);

        // 几何恢复可见：presenter 当前同元素同代数 → 展示的是最新快照而非裁剪前帧。
        var shown = p.Restore(gen2);
        Assert.Same(gen2, shown);
        Assert.False(p.HiddenByClip);
        Assert.Equal(3, shown!.Entries.Count);
    }

    [Fact]
    public void ReanchorWhileClippedKeepsHiddenButFollowsLive()
    {
        var p = new OverlayPresentation();
        var snap = Snap(5, 1);
        Assert.NotNull(p.Apply(snap, clipped: false));
        Assert.Null(p.Apply(null, clipped: true));

        // 裁剪中的几何重锚（null incoming）：仍隐藏，展示目标仍是 live。
        Assert.Null(p.Apply(null, clipped: true));
        Assert.True(p.HiddenByClip);
        Assert.Same(snap, p.Live);
    }

    [Fact]
    public void EndThenRestoreStaysHiddenAndClearsAll()
    {
        var p = new OverlayPresentation();
        Assert.NotNull(p.Apply(Snap(9, 1), clipped: false));
        Assert.Null(p.Apply(null, clipped: true));          // 裁剪隐藏

        p.Clear();                                           // End：清空全部
        Assert.Null(p.Live);
        Assert.False(p.HiddenByClip);
        Assert.Equal(0u, p.ElementId);
        Assert.Equal(0u, p.Generation);

        // End 后 presenter.Current 为 null：恢复不得复活任何内容。
        Assert.Null(p.Restore(null));
        Assert.Null(p.Apply(null, clipped: false));
        Assert.False(p.HiddenByClip);
    }

    [Fact]
    public void RestoreWithStaleOrMissingPresenterClearsAndHides()
    {
        var p = new OverlayPresentation();
        Assert.NotNull(p.Apply(Snap(3, 2), clipped: false));
        Assert.Null(p.Apply(null, clipped: true));

        // presenter 当前换代（同 id 新元素接管）：旧 live 不得复活。
        Assert.Null(p.Restore(Snap(3, 3)));
        Assert.Null(p.Live);
        Assert.Equal(0u, p.ElementId);

        // 重新建立 live 后 presenter 无有效当前 → 同样清空隐藏。
        Assert.NotNull(p.Apply(Snap(4, 1), clipped: false));
        Assert.Null(p.Apply(null, clipped: true));
        Assert.Null(p.Restore(null));
        Assert.Null(p.Live);
        Assert.False(p.HiddenByClip);
    }

    [Fact]
    public void InvalidSnapshotClearsAllAndHides()
    {
        var p = new OverlayPresentation();
        Assert.NotNull(p.Apply(Snap(6, 1), clipped: false));

        var bad = Snap(6, 1, valid: false, entries: 0, failure: "read-failed");
        Assert.Null(p.Apply(bad, clipped: false));
        Assert.Null(p.Live);
        Assert.Equal(0u, p.ElementId);
        Assert.Equal(0u, p.Generation);

        // 无效发布后再恢复：presenter 失配 → 仍清空隐藏。
        Assert.Null(p.Restore(null));
    }

    [Fact]
    public void ElementIdZeroIsLegalAndRestores()
    {
        // R34E 复审：真实微软拼音下发 id=0 的候选元素（R29B seq71/74 实测）——
        // 恢复判定只看 live 存在 + id/generation 匹配，0 不是无效哨兵。
        var p = new OverlayPresentation();
        var snap = Snap(0, 3);
        Assert.Same(snap, p.Apply(snap, clipped: false));
        Assert.Equal(0u, p.ElementId);
        Assert.Equal(3u, p.Generation);

        Assert.Null(p.Apply(null, clipped: true));          // 全裁剪隐藏
        Assert.True(p.HiddenByClip);

        // presenter 当前仍是同 id=0 同代数的有效快照 → 恢复展示。
        var shown = p.Restore(Snap(0, 3));
        Assert.NotNull(shown);
        Assert.False(p.HiddenByClip);
    }

    [Fact]
    public void EndClearedElementIdZeroCannotRestore()
    {
        // id=0 元素 End 后清空；presenter 再拿同 id=0 的旧快照来恢复时，
        // 无 live 身份 → 不得复活展示。
        var p = new OverlayPresentation();
        Assert.NotNull(p.Apply(Snap(0, 2), clipped: false));
        Assert.Null(p.Apply(null, clipped: true));

        p.Clear();                                           // End：清空全部
        Assert.Null(p.Live);
        Assert.Equal(0u, p.ElementId);
        Assert.Equal(0u, p.Generation);

        // 同 id=0 的快照在 Clear 后不代表“同元素”——Live 已空，拒绝恢复。
        Assert.Null(p.Restore(Snap(0, 2)));
        Assert.Null(p.Live);
        Assert.False(p.HiddenByClip);
    }

    [Fact]
    public void EmptyPresentationProducesNothingToShow()
    {
        var p = new OverlayPresentation();
        Assert.Null(p.Apply(null, clipped: false));         // 无 live：重锚为空操作
        Assert.Null(p.Apply(null, clipped: true));
        Assert.Null(p.Restore(Snap(1, 1)));                  // 无 live 身份不匹配 → 不建立
        Assert.False(p.HiddenByClip);
    }
}
