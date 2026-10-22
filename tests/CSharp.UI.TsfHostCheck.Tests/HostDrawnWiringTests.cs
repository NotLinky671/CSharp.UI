using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R34D：真实模式接线与呈现生命周期的聚焦测试。共享同一
/// <see cref="HostDrawnCandidatePresenter"/> 控制流（observer Begin/Update/End 注入
/// 与生产相同），用同一 <see cref="ICandidateOverlay"/> 替身驱动——不是平行实现。
/// 覆盖：默认关不创建 overlay、flag 解析校验、End+clip+restore 不复活、裁剪中
/// Update 保持隐藏、overlay 工厂异常/缺层如实判失败（绝不成功空列表）。
/// </summary>
public sealed class HostDrawnWiringTests
{
    private static readonly string[] NoArgs = [];

    // ---------- flag 解析：默认关 + 无效组合如实拒绝 ----------

    [Fact]
    public void HostDrawnCandidatesDefaultsToOff()
    {
        Assert.True(Program.TryParse(NoArgs, out var parsed, out _));
        Assert.False(parsed.Options.HostDrawnCandidates);
        // 默认 off 下 --uielement-enabled-only 由本模式内部自管；解析结果应为 false。
        Assert.False(parsed.Options.UiElementEnabledOnly);
    }

    [Fact]
    public void HostDrawnCandidatesParsesValidCombo()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta", "--host-drawn-candidates" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options.HostDrawnCandidates);
        Assert.True(parsed.Options.ChildInputSurface);
        // 内部自管激活：UiElementEnabledOnly 由本模式开启。
        Assert.True(parsed.Options.UiElementEnabledOnly);
    }

    [Theory]
    [InlineData("--host-drawn-candidates")]
    public void HostDrawnCandidatesRejectsWithoutChildInputSurface(string flag)
    {
        var ok = Program.TryParse(new[] { flag, "--scenario", "clip-out", "--apartment", "sta" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-input-surface", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HostDrawnCandidatesRejectsHiddenHost()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--hidden-host", "--apartment", "sta",
                    "--host-drawn-candidates" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("scroll-follow")]
    [InlineData("clip-out")]
    public void HostDrawnCandidatesAcceptsSupportedScenarios(string scenario)
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario,
                    "--apartment", "sta", "--host-drawn-candidates" },
            out _, out var error);

        Assert.True(ok, error);
    }

    [Theory]
    [InlineData("nihao-commit")]
    [InlineData("move-window")]
    [InlineData("resize")]
    public void HostDrawnCandidatesRejectsUnsupportedScenarios(string scenario)
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario,
                    "--apartment", "sta", "--host-drawn-candidates" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void HostDrawnCandidatesRequiresSta()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--host-drawn-candidates" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("sta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HostDrawnCandidatesRejectsConflictingExperiments()
    {
        foreach (var extra in new[]
        {
            "--child-hide-when-clipped", "--child-suspend-tsf-focus-when-clipped",
            "--suppress-candidate-ui", "--uielement-enabled-only", "--probe-context-view",
        })
        {
            var ok = Program.TryParse(
                new[] { "--child-input-surface", "--scenario", "clip-out",
                        "--apartment", "sta", "--host-drawn-candidates", extra },
                out _, out var error);

            Assert.False(ok, $"expected rejection for {extra}");
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    // ---------- presenter 生命周期：End+clip+restore 不复活 / 裁剪中 Update 隐藏 ----------

    /// <summary>替身 overlay：真实 ICandidateOverlay 接口的同一份调用记录。</summary>
    private sealed class FakeOverlay : ICandidateOverlay
    {
        internal readonly List<string> Calls = new();
        internal CandidateSnapshot? LastApply;

        public void Apply(CandidateSnapshot snapshot)
        {
            Calls.Add($"apply:{snapshot.ElementId}:{snapshot.Generation}:{snapshot.Valid}");
            LastApply = snapshot;
        }

        public void Hide(uint id, uint generation, string reason)
            => Calls.Add($"hide:{id}:{generation}:{reason}");

        public void Invalidate() => Calls.Add("invalidate");
    }

    private sealed class FakeSource : ICandidateSnapshotSource
    {
        internal CandidatePageRead Read =
            new(true, 1, 0, 0, 0, new[] { "你" }, null);

        public bool IsCandidateList => true;
        public CandidatePageRead ReadPage(Func<bool> ended) => Read;
    }

    private sealed class ScriptedOverlayFactory
    {
        internal ICandidateOverlay? Next = new FakeOverlay();
        internal Exception? Throw;
        internal int Calls;

        internal ICandidateOverlay Create()
        {
            Calls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Next!;
        }
    }

    private static HostDrawnCandidatePresenter NewPresenter(ScriptedOverlayFactory factory)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "host-drawn-wiring-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var log = new ProbeLog(path, echo: false);
        return new HostDrawnCandidatePresenter(factory.Create, log,
            (_, _, _) => { });
    }

    [Fact]
    public void PublishUpdateAppliesLiveSnapshotToOverlay()
    {
        var factory = new ScriptedOverlayFactory();
        var overlay = (FakeOverlay)factory.Next!;
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(7, source));

        var outcome = presenter.PublishUpdate(7, generation, source, () => false);

        Assert.True(outcome.Executed);
        Assert.True(outcome.Snapshot?.Valid);
        Assert.Contains("apply:7", overlay.Calls[0]);
        Assert.True(presenter.HasLiveCandidates);
    }

    [Fact]
    public void EndThenPublishNeverWritesSnapshotNorOverlay()
    {
        var factory = new ScriptedOverlayFactory();
        var overlay = (FakeOverlay)factory.Next!;
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(9, source));

        _ = presenter.PublishUpdate(9, generation, source, () => false);
        Assert.Contains(overlay.Calls, c => c.StartsWith("apply:", StringComparison.Ordinal));
        var applied = overlay.LastApply;
        Assert.NotNull(applied);

        // End 同源：展示已清屏；同 id 同代的再次 Update 不得写快照字段、不得动 overlay。
        presenter.EndElement(9, generation, "end-ui-element");
        Assert.Contains(overlay.Calls, c => c.StartsWith("hide:", StringComparison.Ordinal));
        Assert.Null(presenter.Current);
        var callsAfterEnd = overlay.Calls.Count;

        // 结束后仍到达的 Update：view 已移除——早期锁内门禁直接拦下（连 COM 读都
        // 不发起），不写 _published、不触 overlay、绝不复活已清屏快照。
        var after = presenter.PublishUpdate(9, generation, source, () => false);
        Assert.False(after.Executed);
        Assert.Equal("view-not-current-generation", after.Reason);
        Assert.Equal(callsAfterEnd, overlay.Calls.Count);
        Assert.Null(presenter.Current);
        Assert.False(presenter.HasLiveCandidates);
        Assert.Same(applied, overlay.LastApply);
    }

    [Fact]
    public void PublishWhileHiddenByClipUpdatesSnapshotButNeverShows()
    {
        var factory = new ScriptedOverlayFactory();
        var overlay = (FakeOverlay)factory.Next!;
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(11, source));

        _ = presenter.PublishUpdate(11, generation, source, () => false);
        Assert.True(presenter.HasLiveCandidates);

        // 裁剪中仍到达的 Update：view 存活 → 提交点放行，apply 写 overlay——
        // "隐藏时更新保持隐藏"由窗口侧 _overlayHiddenByClip 把关，presenter 不越权。
        var outcome = presenter.PublishUpdate(11, generation, source, () => false);
        Assert.True(outcome.Executed);
        Assert.True(presenter.HasLiveCandidates);
        var lastApplied = overlay.LastApply;
        Assert.NotNull(lastApplied);
        Assert.Equal(11u, lastApplied.ElementId);
        Assert.Equal(generation, lastApplied.Generation);
        Assert.True(lastApplied.Valid);

        // 元素 End 后（裁剪中元素仍被系统 End）Update 绝不能写 overlay 或快照——
        // view 已移除 → 早期门禁直接拦下；overlay 与 _published 都保持 End 后清空状态。
        presenter.EndElement(11, generation, "end-ui-element");
        var callsAfterEnd = overlay.Calls.Count;
        var stale = presenter.PublishUpdate(11, generation, source, () => false);
        Assert.False(stale.Executed);
        Assert.Equal("view-not-current-generation", stale.Reason);
        Assert.Equal(callsAfterEnd, overlay.Calls.Count);
        Assert.Null(presenter.Current);
        Assert.False(presenter.HasLiveCandidates);
    }

    [Fact]
    public void SameIdNewGenerationDisplacesOldViewAndCannotRepublish()
    {
        var factory = new ScriptedOverlayFactory();
        var overlay = (FakeOverlay)factory.Next!;
        var presenter = NewPresenter(factory);
        var source = new FakeSource();

        var gen1 = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(5, source));
        _ = presenter.PublishUpdate(5, gen1, source, () => false);
        Assert.True(presenter.HasLiveCandidates);

        // 同 id 重复 Begin：旧视图被替换，旧代 Update 不得发布；End 旧代只记 trace。
        var gen2 = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(5, source));
        Assert.NotEqual(gen1, gen2);
        var stale = presenter.PublishUpdate(5, gen1, source, () => false);
        Assert.False(stale.Executed);
        Assert.Contains("view-not-current-generation", stale.Reason);

        // 新代正常发布——真实 overlay 只看到新快照。
        var fresh = presenter.PublishUpdate(5, gen2, source, () => false);
        Assert.True(fresh.Executed);
        Assert.True(fresh.Snapshot?.Valid);
    }

    // ---------- overlay 工厂异常/null：如实判失败，绝不成功空列表 ----------

    [Fact]
    public void OverlayFactoryNullProducesFailedPublish()
    {
        var factory = new ScriptedOverlayFactory { Next = null };
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(3, source));

        var outcome = presenter.PublishUpdate(3, generation, source, () => false);

        // 缺真实显示层不能当成功：Executed 但快照 invalid、HasCandidates=false。
        Assert.True(outcome.Executed);
        Assert.False(outcome.Snapshot?.Valid);
        Assert.False(presenter.HasLiveCandidates);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public void OverlayFactoryThrowProducesFailedPublish()
    {
        var factory = new ScriptedOverlayFactory { Throw = new InvalidOperationException("factory-down") };
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(4, source));

        var outcome = presenter.PublishUpdate(4, generation, source, () => false);

        Assert.True(outcome.Executed);
        Assert.False(outcome.Snapshot?.Valid);
        Assert.False(presenter.HasLiveCandidates);
    }

    [Fact]
    public void FocusLostClearsOverlayAndPublishedSnapshot()
    {
        var factory = new ScriptedOverlayFactory();
        var overlay = (FakeOverlay)factory.Next!;
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(13, source));
        _ = presenter.PublishUpdate(13, generation, source, () => false);
        Assert.True(presenter.HasLiveCandidates);

        presenter.OnFocusLost("window-focus-lost");

        Assert.Contains(overlay.Calls, c => c.StartsWith("hide:", StringComparison.Ordinal));
        Assert.Null(presenter.Current);
        Assert.False(presenter.HasLiveCandidates);
    }

    [Fact]
    public void DisposeClearsAllAndBlocksFurtherPublish()
    {
        var factory = new ScriptedOverlayFactory();
        var presenter = NewPresenter(factory);
        var source = new FakeSource();
        var generation = presenter.BeginElement(new HostDrawnCandidatePresenter.ElementView(15, source));
        _ = presenter.PublishUpdate(15, generation, source, () => false);

        presenter.Dispose();

        var outcome = presenter.PublishUpdate(15, generation, source, () => false);
        Assert.False(outcome.Executed);
        Assert.Equal(0, presenter.LiveViewCount);
        Assert.False(presenter.HasLiveCandidates);
    }
}
