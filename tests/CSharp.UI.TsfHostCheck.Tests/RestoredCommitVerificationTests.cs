using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R37：恢复后提交证明的<b>共享判定</b>测试。全部用例直接调用生产路径同一份
/// <see cref="RestoredCommitGate"/>（<c>ScenarioRunner</c> 也调用它），事实记录由
/// <see cref="RestoredCommitPreFacts"/>/<see cref="RestoredCommitPostFacts"/> 如实构造——
/// 不是另写一套平行谓词，也不通过任何"诊断属性"放行。
/// </summary>
public sealed class RestoredCommitVerificationTests
{
    private const string Good = "你好";

    private static string VerifiedProfile => ImeIdentity.Describe(0,
        TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    /// <summary>提交前基线事实：组合中、零写入、presenter 有效且页内选中「你好」、overlay 真实可见。</summary>
    private static RestoredCommitPreFacts Pre(
        string? selectedText = Good,
        bool presenterValid = true,
        bool overlayVisible = true,
        bool composing = true,
        int writes = 0,
        int beginCount = 1,
        int endCount = 0,
        int sessionId = 1,
        int focusEvents = 1,
        uint generation = 2,
        int selectionOnPage = 0,
        string? invariantViolation = null,
        bool safePointOk = true)
        => new(
            safePointOk, safePointOk ? "ok" : "pre: own foreground lost",
            invariantViolation,
            "ni'hao", 0, 6, writes, writes, composing, VerifiedProfile,
            beginCount, endCount, sessionId, focusEvents,
            PresenterAvailable: true, presenterValid,
            ElementId: 0, Generation: generation,
            EntryCount: presenterValid ? 9 : 0,
            SelectedText: selectedText,
            SelectionOnPage: selectionOnPage,
            OverlayInspectOk: true,
            OverlayVisible: overlayVisible,
            OverlayElementId: 0,
            OverlayGeneration: generation,
            OverlayFailure: null);

    /// <summary>提交后事实：组合恰好结束一次、文本「你好」、一条 tsf-composition 记录、presenter 清空、overlay 隐藏。</summary>
    private static RestoredCommitPostFacts Post(
        string text = Good,
        int writeDelta = 1,
        string entryText = Good,
        string entrySource = RestoredCommitExpectation.ExpectedSource,
        bool compositionEnded = true,
        bool composing = false,
        int endCount = 1,
        int beginCount = 1,
        int sessionId = 1,
        int focusEvents = 1,
        bool presenterCleared = true,
        bool overlayVisible = false,
        int? writesAtEndOfWait = null,
        bool safePointOk = true,
        int entryCount = 1)
    {
        var entries = Enumerable.Range(0, Math.Max(0, entryCount))
            .Select(i => new BindingWrite(entryText, entrySource, 1, i + 1))
            .ToArray();
        return new RestoredCommitPostFacts(
            safePointOk, safePointOk ? "ok" : "post: own foreground lost",
            text, writeDelta, composing, beginCount, endCount, sessionId, focusEvents,
            VerifiedProfile, writeDelta, entries, compositionEnded, presenterCleared,
            OverlayInspectOk: true, OverlayVisible: overlayVisible,
            WritesAtEndOfWait: writesAtEndOfWait ?? writeDelta);
    }

    // 1) 成功基线：共享门禁对真实提交形状判通过（含可选图门禁）。
    [Fact]
    public void GatePassesOnRealCommittedShape()
    {
        var pre = Pre();
        var post = Post();

        Assert.Null(RestoredCommitGate.EvaluatePre(pre));
        Assert.Null(RestoredCommitGate.EvaluatePost(pre, post));
        Assert.Null(RestoredCommitGate.EvaluateCommittedStatePreserved(post));
    }

    // 2) 文本不对：提交了别的候选/只提交一个字/空/拉丁。
    [Theory]
    [InlineData("你好吗")]
    [InlineData("你")]
    [InlineData("")]
    [InlineData("hello")]
    public void WrongCommittedTextFails(string text)
    {
        var pre = Pre();

        var failure = RestoredCommitGate.EvaluatePost(pre, Post(text: text, entryText: text));

        Assert.NotNull(failure);
        Assert.Contains("document text is not exactly", failure, StringComparison.Ordinal);
    }

    // 3) 两次写入（或一次都没写）：增量与<b>实际记录条目</b>都必须精确为 1。
    [Fact]
    public void WrongWriteCountFails()
    {
        var pre = Pre();

        foreach (var delta in new[] { 0, 2 })
        {
            var failure = RestoredCommitGate.EvaluatePost(pre,
                Post(writeDelta: delta, entryCount: delta));
            Assert.NotNull(failure);
            Assert.Contains("binding write delta must be exactly", failure,
                StringComparison.Ordinal);
        }

        // 计数报 1 但实际记录两条：以实际记录为准，绝不信单一计数。
        var twoEntries = RestoredCommitGate.EvaluatePost(pre, Post(writeDelta: 1, entryCount: 2));
        Assert.NotNull(twoEntries);
        Assert.Contains("recorded binding entries must be exactly", twoEntries,
            StringComparison.Ordinal);

        // 来源不是组合结束的最终文本：同样失败。
        var wrongSource = RestoredCommitGate.EvaluatePost(pre, Post(entrySource: "tsf-direct"));
        Assert.NotNull(wrongSource);
        Assert.Contains("recorded binding entry source", wrongSource, StringComparison.Ordinal);
    }

    // 4) 组合没有结束 / 结束了不止一次 / 中途重启。
    [Fact]
    public void CompositionMustEndExactlyOnce()
    {
        var pre = Pre();

        var notEnded = RestoredCommitGate.EvaluatePost(pre,
            Post(compositionEnded: false, composing: true, endCount: 0, writeDelta: 0,
                entryCount: 0));
        Assert.NotNull(notEnded);
        Assert.Contains("did not end within", notEnded, StringComparison.Ordinal);

        var endedTwice = RestoredCommitGate.EvaluatePost(pre, Post(endCount: 2));
        Assert.NotNull(endedTwice);
        Assert.Contains("end count must increase exactly once", endedTwice,
            StringComparison.Ordinal);

        var restarted = RestoredCommitGate.EvaluatePost(pre, Post(beginCount: 2, sessionId: 2));
        Assert.NotNull(restarted);
        Assert.Contains("begin count changed", restarted, StringComparison.Ordinal);
    }

    // 5) 恢复后的候选已被清掉 / 选中的不是「你好」/ 代数无效 / overlay 不可见或展示旧代数。
    [Fact]
    public void ClearedOrWrongCandidateFailsBeforeKeyIsSent()
    {
        Assert.Contains("no valid published snapshot",
            RestoredCommitGate.EvaluatePre(Pre(presenterValid: false))!,
            StringComparison.Ordinal);
        Assert.Contains("generation is zero",
            RestoredCommitGate.EvaluatePre(Pre(generation: 0))!,
            StringComparison.Ordinal);
        Assert.Contains("selected candidate is not",
            RestoredCommitGate.EvaluatePre(Pre(selectedText: "你"))!,
            StringComparison.Ordinal);
        Assert.Contains("selected candidate is not",
            RestoredCommitGate.EvaluatePre(Pre(selectedText: null))!,
            StringComparison.Ordinal);
        Assert.Contains("not really visible",
            RestoredCommitGate.EvaluatePre(Pre(overlayVisible: false))!,
            StringComparison.Ordinal);

        // overlay 亮着但展示的是旧代数：与既有锚定门禁同一事实，绝不放宽。
        Assert.Contains("not displaying the current presenter snapshot",
            RestoredCommitGate.EvaluatePre(Pre() with { OverlayGeneration = 1 })!,
            StringComparison.Ordinal);

        // 提交前区间必须仍是零写入——否则"这次提交"无法归因。
        Assert.Contains("writes before commit must be 0",
            RestoredCommitGate.EvaluatePre(Pre(writes: 1))!,
            StringComparison.Ordinal);
    }

    // 6) 焦点丢失（瞬时离开又回来）与提交后严格安全点失败。
    [Fact]
    public void FocusLossOrPostSafePointFails()
    {
        var pre = Pre();

        var focusLost = RestoredCommitGate.EvaluatePost(pre, Post(focusEvents: 3));
        Assert.NotNull(focusLost);
        Assert.Contains("input focus events changed", focusLost, StringComparison.Ordinal);

        var safePoint = RestoredCommitGate.EvaluatePost(pre, Post(safePointOk: false));
        Assert.NotNull(safePoint);
        Assert.Contains("post safe point failed", safePoint, StringComparison.Ordinal);
    }

    // 7) 默认 off；解析期只允许 R37 唯一组合，其余组合如实拒绝。
    [Fact]
    public void DefaultOffAndParserRejectsOtherCombinations()
    {
        Assert.True(Program.TryParse(Array.Empty<string>(), out var off, out _));
        Assert.False(off.Options!.VerifyRestoredCommit);
        Assert.False(off.Options.HostDrawnCandidates);

        Assert.True(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                    "--host-drawn-candidates", "--verify-restored-commit" },
            out var on, out var onError), onError);
        Assert.True(on.Options!.VerifyRestoredCommit);
        Assert.True(on.Options.UiElementEnabledOnly);

        var rejected = new[]
        {
            // 缺宿主自绘候选 / 缺 child input surface
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--verify-restored-commit" },
            new[] { "--scenario", "clip-out", "--apartment", "sta", "--host-drawn-candidates",
                "--verify-restored-commit" },
            // 非 clip-out 场景
            new[] { "--child-input-surface", "--scenario", "scroll-follow", "--apartment", "sta",
                "--host-drawn-candidates", "--verify-restored-commit" },
            // 缺 STA
            new[] { "--child-input-surface", "--scenario", "clip-out", "--host-drawn-candidates",
                "--verify-restored-commit" },
            // 隐藏宿主
            new[] { "--child-input-surface", "--hidden-host", "--apartment", "sta",
                "--host-drawn-candidates", "--verify-restored-commit" },
            // 与 R30 / R31 / 抑制 / 诊断冲突
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--host-drawn-candidates", "--child-hide-when-clipped",
                "--verify-restored-commit" },
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--host-drawn-candidates", "--child-suspend-tsf-focus-when-clipped",
                "--verify-restored-commit" },
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--host-drawn-candidates", "--suppress-candidate-ui",
                "--verify-restored-commit" },
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--host-drawn-candidates", "--probe-context-view",
                "--verify-restored-commit" },
            new[] { "--child-input-surface", "--scenario", "clip-out", "--apartment", "sta",
                "--host-drawn-candidates", "--trace-layout-chain",
                "--verify-restored-commit" },
        };

        foreach (var args in rejected)
        {
            Assert.False(Program.TryParse(args, out _, out var error),
                $"expected rejection for: {string.Join(' ', args)}");
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    // 8) 任一条真实事实偏离都必须判失败——不存在"整体看起来对"就放行。
    [Fact]
    public void EverySingleDeviationFails()
    {
        var pre = Pre();
        Assert.Null(RestoredCommitGate.EvaluatePost(pre, Post()));

        var deviations = new[]
        {
            Post(text: "你好吗"),
            Post(entryText: "你好吗"),
            Post(entrySource: "app"),
            Post(writeDelta: 2, entryCount: 2),
            Post(writeDelta: 0, entryCount: 0),
            Post(compositionEnded: false, composing: true, endCount: 0),
            Post(endCount: 2),
            Post(beginCount: 2),
            Post(sessionId: 2),
            Post(focusEvents: 2),
            Post(presenterCleared: false),
            Post(overlayVisible: true),
            Post(safePointOk: false),
            // 稳定窗口内才落进的额外写入：增量恰好 1 也不许通过。
            Post(writeDelta: 1, writesAtEndOfWait: 0),
        };

        foreach (var post in deviations)
        {
            Assert.NotNull(RestoredCommitGate.EvaluatePost(pre, post));
        }

        // 可选 post-commit 图门禁只核对"已提交状态保持"，同样逐条拒绝偏离。
        Assert.Null(RestoredCommitGate.EvaluateCommittedStatePreserved(Post()));
        Assert.NotNull(RestoredCommitGate.EvaluateCommittedStatePreserved(
            Post(composing: true, compositionEnded: false)));
        Assert.NotNull(RestoredCommitGate.EvaluateCommittedStatePreserved(Post(text: "你好吗")));
        Assert.NotNull(RestoredCommitGate.EvaluateCommittedStatePreserved(
            Post(presenterCleared: false)));
        Assert.NotNull(RestoredCommitGate.EvaluateCommittedStatePreserved(
            Post(overlayVisible: true)));
        Assert.NotNull(RestoredCommitGate.EvaluateCommittedStatePreserved(
            Post(safePointOk: false)));
    }
}
