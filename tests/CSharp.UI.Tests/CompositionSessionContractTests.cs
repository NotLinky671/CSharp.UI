using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-02：组合事件值与会话身份契约。只覆盖事件形状、不可变载荷、版本锚点与 default 安全；
/// 跨消息顺序、重复/过时抑制由 TI-04 的组合状态机实现，不在本任务测试。
/// </summary>
public class CompositionEventContractTests
{
    private static readonly TextSnapshot Document = new("你好", TextSelection.Caret(2), version: 4);

    [Fact]
    public void TextInputSession_AdvancesEpochAndCompositionIdMonotonically()
    {
        var session = new TextInputSession(3);
        Assert.Equal(3, session.SessionId);
        Assert.Equal(0, session.CompositionId);

        var next = session.NextSession();
        Assert.Equal(4, next.SessionId);
        Assert.Equal(0, next.CompositionId);

        var composition = session.NextComposition();
        Assert.Equal(3, composition.SessionId);
        Assert.Equal(1, composition.CompositionId);

        Assert.Equal(8, session.WithComposition(7).CompositionId);   // 推进到指定 ID 的后一个
        Assert.Equal(5, new TextInputSession(0, 5).WithComposition(3).CompositionId); // 不倒退

        Assert.Throws<ArgumentOutOfRangeException>(() => new TextInputSession(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextInputSession(0, -1));
    }

    [Fact]
    public void CompositionEventFactories_RejectNegativeIdentityAndCaret()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionEvent.Start(-1, 0, 0, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionEvent.Start(0, -1, 0, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionEvent.Start(0, 0, -1, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionEvent.Start(0, 0, 0, -1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionEvent.Update(0, 0, 0, 0, default, compositionCaret: -1));
    }

    [Fact]
    public void ResultCarriesReplacementAndNextPreeditShape()
    {
        var result = CompositionEvent.Result(1, 2, 4, 3, "你好", new TextRange(0, 2), nextPreedit: "shi");

        Assert.True(result.CommitsResult);
        Assert.True(result.EndsComposition);
        Assert.Equal("你好", result.Preedit);
        Assert.Equal("shi", result.NextPreedit);
        Assert.Equal(new TextRange(0, 2), result.Replacement);
        Assert.True(result.IsAnchoredTo(Document));

        Assert.False(CompositionEvent.Update(1, 2, 4, 3, default, "n").CommitsResult);
        Assert.True(CompositionEvent.Cancel(1, 2, 4).EndsComposition);
        Assert.True(CompositionEvent.End(1, 2, 4).EndsComposition);
    }

    // R2：载荷校验边界（不抛异常、可显式判定）。
    [Fact]
    public void PayloadValidation_AcceptsWellFormedEventAnchoredToDocument()
    {
        var start = CompositionEvent.Start(
            0,
            0,
            Document.Version,
            0,
            new TextRange(2, 2),
            "ni",
            TextSelection.Caret(2),
            compositionCaret: 2,
            segments: [new TextRange(0, 2)]);

        Assert.True(start.TryValidatePayload(Document, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void PayloadValidation_RejectsReplacementBeyondDocument()
    {
        var start = CompositionEvent.Start(0, 0, Document.Version, 0, new TextRange(20, 30), "n", compositionCaret: 1);

        Assert.False(start.TryValidatePayload(Document, out var error));
        Assert.Contains("替换区", error!);
    }

    [Fact]
    public void PayloadValidation_RejectsSelectionBeyondDocument()
    {
        var start = CompositionEvent.Start(
            0,
            0,
            Document.Version,
            0,
            new TextRange(0, 0),
            "n",
            selectionBeforeComposition: new TextSelection(0, 99));

        Assert.False(start.TryValidatePayload(Document, out var error));
        Assert.Contains("组合前选区", error!);
    }

    // R2 第二轮：完整快照重载必须拒绝落在字素内部的范围/选区；长度重载明确较弱。
    [Fact]
    public void SnapshotOverload_RejectsReplacementSplittingSurrogatePair()
    {
        // "😀" 是 1 个字素、2 个 UTF-16 code unit；[1,1) 位于代理对中间。
        var surrogateDocument = new TextSnapshot("\U0001F600", TextSelection.Caret(0), version: 0);
        var inside = CompositionEvent.Result(0, 0, 0, 0, "x", new TextRange(1, 1));

        Assert.False(inside.TryValidatePayload(surrogateDocument, out var error));
        Assert.Contains("字素边界", error!);

        // 端点在字素边界上则通过。
        var atBoundary = CompositionEvent.Result(0, 0, 0, 0, "x", new TextRange(2, 2));
        Assert.True(atBoundary.TryValidatePayload(surrogateDocument, out _));

        // 长度重载只能检查长度，因此 [1,1) 会通过——文档已明确它不能保证字素安全。
        Assert.True(inside.TryValidatePayload(surrogateDocument.Length, out _));
    }

    [Fact]
    public void SnapshotOverload_RejectsReplacementSplittingCombiningCharacter()
    {
        // "e" + U+0301 是 1 个字素、2 个 code unit；边界只有 0 与 2。
        var combiningDocument = new TextSnapshot("e\u0301", TextSelection.Caret(0), version: 0);
        var inside = CompositionEvent.Result(0, 0, 0, 0, "x", new TextRange(1, 1));

        Assert.False(inside.TryValidatePayload(combiningDocument, out var error));
        Assert.Contains("字素边界", error!);
        Assert.True(inside.TryValidatePayload(combiningDocument.Length, out _));
    }

    [Fact]
    public void SnapshotOverload_RejectsReplacementSplittingZwjSequence()
    {
        // 家庭 emoji：👨 ZWJ 👩 ZWJ 👧 是 1 个字素、8 个 code unit。
        var zwjDocument = new TextSnapshot("\U0001F468\u200D\U0001F469\u200D\U0001F467", TextSelection.Caret(0), version: 0);
        var inside = CompositionEvent.Result(0, 0, 0, 0, "x", new TextRange(2, 2));

        Assert.False(inside.TryValidatePayload(zwjDocument, out var error));
        Assert.Contains("字素边界", error!);

        var atEnd = CompositionEvent.Result(0, 0, 0, 0, "x", new TextRange(8, 8));
        Assert.True(atEnd.TryValidatePayload(zwjDocument, out _));
    }

    [Fact]
    public void SnapshotOverload_RejectsSelectionSplittingGrapheme()
    {
        var surrogateDocument = new TextSnapshot("\U0001F600", TextSelection.Caret(0), version: 0);
        var start = CompositionEvent.Start(
            0,
            0,
            0,
            0,
            new TextRange(0, 0),
            "n",
            selectionBeforeComposition: new TextSelection(0, 1));

        Assert.False(start.TryValidatePayload(surrogateDocument, out var error));
        Assert.Contains("字素边界", error!);
    }

    [Fact]
    public void LengthOverload_RejectsNegativeDocumentLength_AsCallerError()
    {
        var e = CompositionEvent.Result(0, 0, 0, 0, "x");
        Assert.Throws<ArgumentOutOfRangeException>(() => e.TryValidatePayload(-1, out _));
    }

    [Fact]
    public void PayloadValidation_RejectsCaretAndSegmentsBeyondPreedit()
    {
        var caretOut = CompositionEvent.Start(0, 0, Document.Version, 0, new TextRange(0, 0), "n", compositionCaret: 99);
        Assert.False(caretOut.TryValidatePayload(Document, out var caretError));
        Assert.Contains("caret", caretError!);

        var segmentOut = CompositionEvent.Start(
            0,
            0,
            Document.Version,
            0,
            new TextRange(0, 0),
            "n",
            segments: [TextRange.FromLength(0, 5)]);
        Assert.False(segmentOut.TryValidatePayload(Document, out var segmentError));
        Assert.Contains("分段", segmentError!);

        var overlapping = CompositionEvent.Start(
            0,
            0,
            Document.Version,
            0,
            new TextRange(0, 0),
            "nim",
            segments: [new TextRange(0, 2), new TextRange(1, 3)]);
        Assert.False(overlapping.TryValidatePayload(Document, out var overlapError));
        Assert.Contains("重叠", overlapError!);
    }

    [Fact]
    public void PayloadValidation_RejectsUnpairedSurrogates()
    {
        var badPreedit = CompositionEvent.Start(0, 0, Document.Version, 0, new TextRange(0, 0), "a\uD83D");
        Assert.False(badPreedit.TryValidatePayload(Document, out var preeditError));
        Assert.Contains("代理项", preeditError!);

        var badNext = CompositionEvent.Result(0, 0, Document.Version, 0, "ok", new TextRange(0, 0), nextPreedit: "\uDC00");
        Assert.False(badNext.TryValidatePayload(Document, out var nextError));
        Assert.Contains("代理项", nextError!);

        // 合法代理对不误报。
        var good = CompositionEvent.Start(0, 0, Document.Version, 0, new TextRange(0, 0), "\U0001F600");
        Assert.True(good.TryValidatePayload(Document, out _));
    }

    // R2：未知/默认事件类型必须被拒绝（with 构造也不能绕过）。
    [Fact]
    public void PayloadValidation_RejectsUndefinedEventKind()
    {
        var undefined = CompositionEvent.Start(0, 0, Document.Version, 0, new TextRange(0, 0), "n")
            with { Kind = (CompositionEventKind)999 };

        Assert.False(undefined.TryValidatePayload(Document, out var error));
        Assert.Contains("未定义", error!);

        var negative = CompositionEvent.End(0, 0, Document.Version) with { Kind = (CompositionEventKind)(-1) };
        Assert.False(negative.TryValidatePayload(Document, out _));
    }

    // R2：版本必须锚定明确快照；未来版本与过时版本都不可应用。
    [Fact]
    public void PayloadValidation_RejectsStaleAndFutureVersionAnchors()
    {
        var stale = CompositionEvent.Result(0, 0, Document.Version - 1, 0, "你好");
        Assert.False(stale.TryValidatePayload(Document, out var staleError));
        Assert.Contains("版本", staleError!);

        var future = CompositionEvent.Result(0, 0, Document.Version + 10, 0, "你好");
        Assert.False(future.TryValidatePayload(Document, out var futureError));
        Assert.Contains("版本", futureError!);

        var current = CompositionEvent.Result(0, 0, Document.Version, 0, "你好");
        Assert.True(current.TryValidatePayload(Document, out _));
    }

    // R4：载荷不可变（源集合、取出集合、with 路径）。
    [Fact]
    public void Segments_AreFrozenAndSourceMutationsDoNotLeak()
    {
        var source = new List<TextRange> { new(0, 1) };
        var e = CompositionEvent.Start(0, 0, 0, 0, default, "ni", segments: source);

        Assert.Single(e.Segments);
        Assert.Equal(new TextRange(0, 1), e.Segments[0]);

        source[0] = new TextRange(30, 40);
        Assert.Equal(new TextRange(0, 1), e.Segments[0]);

        // 取出后没有可写入口，也无法转回可写数组。
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)e.Segments).Add(new TextRange(1, 2)));
        Assert.IsNotType<TextRange[]>(e.Segments);

        // with 生成新事件，原事件不变。
        var changed = e with { Preedit = "h" };
        Assert.Equal("ni", e.Preedit);
        Assert.Equal("h", changed.Preedit);
        Assert.Equal(e.Segments, changed.Segments);
    }

    // R5：default 查询不得空引用异常。
    [Fact]
    public void DefaultCompositionEventQueries_AreNullSafe()
    {
        var e = default(CompositionEvent);

        Assert.Equal(string.Empty, e.Preedit);
        Assert.Empty(e.Segments);
        Assert.Null(e.NextPreedit);
        Assert.Null(e.SelectionBeforeComposition);
        Assert.Null(e.CompositionCaret);
        Assert.Equal(new TextRange(0, 0), e.Replacement);
        Assert.False(e.CommitsResult);
        Assert.False(e.EndsComposition);

        // default(CompositionEvent) 等价于空 Start(0)：Codex 已接受这是合法空载荷形状；
        // 是否可应用仍取决于会话/版本/顺序（TI-04），这里只验证形状与 default 安全。
        Assert.Equal(CompositionEventKind.Start, e.Kind);
        Assert.Equal(0, e.CompositionSessionId);
        Assert.Equal(0, e.DocumentVersion);
        Assert.True(e.TryValidatePayload(0, out _));
        Assert.True(e.TryValidatePayload(TextSnapshot.Empty, out _));
        Assert.DoesNotContain("ni", e.ToString());
    }
}

/// <summary>
/// TI-02：契约的不可变性与 default 安全（R4/R5）。快照/操作/结果的值语义必须自洽且脱敏。
/// </summary>
public class ContractImmutabilityTests
{
    [Fact]
    public void DefaultStructs_AreNullSafeAndSelfConsistent()
    {
        var snapshot = default(TextSnapshot);
        Assert.Equal(string.Empty, snapshot.Text);
        Assert.Equal(0, snapshot.Length);
        Assert.Equal(0, snapshot.Version);
        Assert.True(snapshot.IsConsistent);
        Assert.Equal(TextSelection.Caret(0), snapshot.Selection);

        var operation = default(EditOperation);
        Assert.Equal(string.Empty, operation.Replacement);
        Assert.Equal(0, operation.Range.Length);
        Assert.True(operation.IsNoOp);
        Assert.False(operation.IsDeletion);
        Assert.Equal(0, operation.LengthDelta);
        Assert.Equal(TextEditKind.None, operation.Kind);
        Assert.NotNull(operation.ToString());

        var state = default(TextInputState);
        Assert.Equal(string.Empty, state.Preedit);
        Assert.Equal(string.Empty, state.Document.Text);
        Assert.NotNull(state.ToString());
    }

    [Fact]
    public void EditOperation_RejectsUndefinedKindAndReason_ConsistentlyAcrossEntryPoints()
    {
        // 构造函数。
        Assert.Throws<ArgumentException>(() => new EditOperation(new TextRange(0, 0), "x", kind: (TextEditKind)999));
        Assert.Throws<ArgumentException>(() => new EditOperation(new TextRange(0, 0), "x", reason: (TextEditReason)999));

        // TryCreate。
        Assert.False(EditOperation.TryCreate(
            new TextRange(0, 0),
            "x",
            (TextEditKind)999,
            TextEditReason.UserInput,
            scrubSingleLine: true,
            out _,
            out var kindError));
        Assert.Contains("未定义", kindError!);

        Assert.False(EditOperation.TryCreate(
            new TextRange(0, 0),
            "x",
            TextEditKind.Typing,
            (TextEditReason)999,
            scrubSingleLine: true,
            out _,
            out var reasonError));
        Assert.Contains("未定义", reasonError!);

        // Delete 工厂不得绕过校验（R5 第二轮）。
        Assert.Throws<ArgumentException>(() => EditOperation.Delete(default, (TextEditKind)999));
        Assert.Throws<ArgumentException>(() => EditOperation.Delete(default, TextEditKind.Delete, (TextEditReason)999));
        Assert.Throws<ArgumentException>(() => EditOperation.Delete(default, TextEditKind.None));

        // with 改写后的最小验证入口供 TI-03 消费。
        var valid = EditOperation.Delete(new TextRange(0, 1));
        Assert.True(valid.TryValidate(out var validError));
        Assert.Null(validError);

        var invalidWith = valid with { Kind = (TextEditKind)999 };
        Assert.False(invalidWith.TryValidate(out var withError));
        Assert.Contains("未定义", withError!);

        var invalidReasonWith = valid with { Reason = (TextEditReason)999 };
        Assert.False(invalidReasonWith.TryValidate(out _));
    }

    [Fact]
    public void EditResult_RejectsUndefinedStatus()
    {
        Assert.Throws<ArgumentException>(() =>
            new EditResult((TextEditStatus)999, TextSnapshot.Empty, TextSnapshot.Empty, string.Empty, 0));
    }

    [Fact]
    public void DefaultEditResult_IsExplicitlyInvalidAndNullSafe()
    {
        var result = default(EditResult);

        Assert.Equal(TextEditStatus.None, result.Status);   // 明确表示无效/未执行
        Assert.Equal(string.Empty, result.AcceptedText);    // 非 nullable 属性返回空串
        Assert.Equal(0, result.DocumentVersion);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Before.Version);
        Assert.Equal(0, result.After.Version);
        Assert.NotNull(result.ToString());
    }

    [Fact]
    public void EditResult_RequiresVersionAndTextConsistency()
    {
        var before = new TextSnapshot("a", TextSelection.Caret(1), 2);
        var after = before.Next("ab", TextSelection.Caret(2));

        // 版本必须等于 after.Version。
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Accepted, before, after, "ab", documentVersion: 2));

        // Accepted 必须真的改变文本。
        var sameText = new TextSnapshot("a", TextSelection.Caret(1), 3);
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Accepted, before, sameText, "a", documentVersion: 3));

        // Accepted 必须推进版本（R5：After.Version 严格大于 Before.Version）。
        var noVersionAdvance = new TextSnapshot("ab", TextSelection.Caret(2), 2);
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Accepted, before, noVersionAdvance, "ab", documentVersion: 2));

        // Accepted 不得携带原因。
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Accepted, before, after, "ab", after.Version, "reason"));

        var accepted = new EditResult(TextEditStatus.Accepted, before, after, "ab", after.Version);
        Assert.True(accepted.Changed);
        Assert.True(accepted.After.Version > accepted.Before.Version);
        Assert.Equal(after.Version, accepted.DocumentVersion);

        // Rejected 不得推进版本。
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Rejected, before, after, "ab", after.Version, "rejected"));

        // Rejected 也不得改变文档文本（R5 第二轮：不伪造“同版本不同文档”）。
        var sameVersionDifferentText = new TextSnapshot("b", TextSelection.Caret(1), 2);
        Assert.Throws<ArgumentException>(() => new EditResult(TextEditStatus.Rejected, before, sameVersionDifferentText, "b", 2, "rejected"));

        var rejected = new EditResult(TextEditStatus.Rejected, before, before, "a", before.Version, "setter 拒绝写入");
        Assert.False(rejected.Changed);
        Assert.Equal(before.Version, rejected.DocumentVersion);
        Assert.Equal(rejected.Before.Text, rejected.After.Text);

        // 绑定转换后的真实重同步：文本确实变化就必须报告 Accepted（不得用 Rejected 掩盖），
        // 具体同步语义由 TI-03 明确。
        var transformed = new TextSnapshot("ab ", TextSelection.Caret(2), 3);
        var acceptedTransform = new EditResult(TextEditStatus.Accepted, before, transformed, "ab ", transformed.Version);
        Assert.True(acceptedTransform.Changed);
    }

    [Fact]
    public void Diagnostics_DoNotContainUserText()
    {
        var snapshot = new TextSnapshot("SECRET_USER_TEXT", TextSelection.Caret(2), 7, isComposing: true);
        Assert.DoesNotContain("SECRET_USER_TEXT", snapshot.ToString());
        Assert.Contains("len=16", snapshot.ToString());

        var operation = new EditOperation(new TextRange(0, 0), "SECRET_USER_TEXT");
        Assert.DoesNotContain("SECRET_USER_TEXT", operation.ToString());
        Assert.Contains("len=16", operation.ToString());

        var before = new TextSnapshot("SECRET_OLD", TextSelection.Caret(0), 0);
        var after = before.Next("SECRET_NEW", TextSelection.Caret(0));
        var result = new EditResult(TextEditStatus.Accepted, before, after, "SECRET_NEW", after.Version);
        Assert.DoesNotContain("SECRET", result.ToString());

        var composition = CompositionEvent.Start(0, 0, 0, 0, default, "SECRET_USER_TEXT");
        Assert.DoesNotContain("SECRET_USER_TEXT", composition.ToString());
        Assert.Contains("preeditLen=16", composition.ToString());

        var state = new TextInputState(snapshot, "SECRET_PREEDIT", composing: true);
        Assert.DoesNotContain("SECRET", state.ToString());
        Assert.Contains("preeditLen=14", state.ToString());
    }
}
