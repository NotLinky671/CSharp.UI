using Xunit;

namespace CSharp.UI.NativeEditReference.Tests;

/// <summary>
/// 原生 EDIT 对照工具的纯托管契约测试：命令行、退出码语义、观测不变量、矩形裁剪。
/// 不创建窗口、不初始化 COM、不发送任何输入。视觉结论一律由人工截图判定。
/// </summary>
public sealed class NativeEditContractTests
{
    [Fact]
    public void HelpAndSelfTestNeverProduceARunOptions()
    {
        var help = NativeEditArguments.Parse(new[] { "--help" });
        Assert.True(help.Help);
        Assert.Null(help.Options);

        var selfTest = NativeEditArguments.Parse(new[] { "--self-test" });
        Assert.True(selfTest.SelfTest);
        Assert.Null(selfTest.Options);
    }

    [Theory]
    [InlineData("internal-scroll")]
    [InlineData("child-move")]
    [InlineData("child-clip")]
    public void EveryScenarioIsAccepted(string scenario)
    {
        var parsed = NativeEditArguments.Parse(new[] { "--scenario", scenario });

        Assert.Null(parsed.Error);
        Assert.Equal(scenario, parsed.Options!.Scenario);
    }

    [Fact]
    public void UnknownScenarioAndIllegalCombinationsAreRejected()
    {
        Assert.NotNull(NativeEditArguments.Parse(new[] { "--scenario", "nope" }).Error);
        Assert.NotNull(NativeEditArguments.Parse(new[] { "--hidden-smoke", "--scenario", "child-clip" }).Error);
        Assert.NotNull(NativeEditArguments.Parse(new[] { "--timeout-ms", "0" }).Error);
        Assert.NotNull(NativeEditArguments.Parse(new[] { "--foreground-wait-ms", "-1" }).Error);
        Assert.NotNull(NativeEditArguments.Parse(new[] { "--not-a-switch" }).Error);
    }

    [Fact]
    public void VisualPendingIsAlwaysExit3AndNever0()
    {
        Assert.Equal(3, NativeEditOutcome.Resolve(checksPass: true, visualPending: true,
            preconditionMissing: false, aborted: false));
        Assert.NotEqual(0, NativeEditOutcome.Resolve(true, true, false, false));
    }

    [Fact]
    public void FailureAndPreconditionCodesAreDistinct()
    {
        Assert.Equal(1, NativeEditOutcome.Resolve(false, true, false, false));
        Assert.Equal(1, NativeEditOutcome.Resolve(true, true, false, aborted: true));
        Assert.Equal(2, NativeEditOutcome.Resolve(true, false, preconditionMissing: true, aborted: false));
        // 只有隐藏 smoke（无待视觉项）可以 0。
        Assert.Equal(0, NativeEditOutcome.Resolve(true, visualPending: false,
            preconditionMissing: false, aborted: false));
    }

    [Fact]
    public void ClipAndCollapseAreTheOnlyScenariosWithRestore()
    {
        Assert.True(NativeEditScenario.HasRestore("child-clip"));
        Assert.True(NativeEditScenario.HasRestore("child-collapse"));
        Assert.False(NativeEditScenario.HasRestore("internal-scroll"));
        Assert.False(NativeEditScenario.HasRestore("child-move"));
    }

    [Fact]
    public void ChildCollapseIsOptInOnlyAndNeverInDefaultMatrix()
    {
        // opt-in：被解析/动作识别接受，但不属于默认 All，也不会改变默认三场景集合。
        Assert.True(NativeEditScenario.IsKnown("child-collapse"));
        Assert.DoesNotContain("child-collapse", NativeEditScenario.All);
        Assert.Equal(3, NativeEditScenario.All.Length);

        var parsed = NativeEditArguments.Parse(new[] { "--scenario", "child-collapse" });
        Assert.Null(parsed.Error);
        Assert.Equal("child-collapse", parsed.Options!.Scenario);
    }

    [Fact]
    public void ChildCollapseHasDistinctResizeActionKind()
    {
        var kind = NativeEditScenario.ActionKind("child-collapse");

        Assert.Equal("setwindowpos-child-collapse", kind);
        Assert.DoesNotContain(kind,
            NativeEditScenario.All.Select(NativeEditScenario.ActionKind));
    }

    [Theory]
    [InlineData(1, 0, "ni", true)]
    [InlineData(1, 1, "ni", false)]
    [InlineData(0, 0, null, false)]
    // 空 CompStr 不算活动组合（R25 审核：start>0 && end==0 不足以证明非空组合）。
    [InlineData(1, 0, "", false)]
    [InlineData(0, 0, "ni", false)]
    public void CompositionActiveNeedsStartedWithoutEndAndNonEmptyCompStr(int starts, int ends,
        string? uncommitted, bool expected)
    {
        Assert.Equal(expected, NativeEditObservation.CompositionActive(starts, ends, uncommitted));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void CommitOrEndIsVisible(int results, int ends, bool expected)
    {
        Assert.Equal(expected, NativeEditObservation.HasCommittedOrEnded(results, ends));
    }

    [Fact]
    public void BaselineMustBeCleanBeforeInput()
    {
        Assert.True(NativeEditObservation.BaselineDocumentIntact(0, "line0", "line0"));
        Assert.False(NativeEditObservation.BaselineDocumentIntact(1, "line0", "line0"));
        Assert.False(NativeEditObservation.BaselineDocumentIntact(0, "line0", "line0x"));
    }

    [Fact]
    public void IntersectionDetectsFullyClippedEdit()
    {
        var edit = new NativeRect(0, 0, 560, 320);
        var viewport = new NativeRect(0, 0, 560, 180);

        Assert.Equal(new NativeRect(0, 0, 560, 180), edit.Intersect(viewport));
        Assert.False(edit.Intersect(viewport).IsEmpty);

        // 移到 viewport 上方（完全裁出）。
        var clipped = new NativeRect(0, -336, 560, -16);
        Assert.True(clipped.Intersect(viewport).IsEmpty);
    }

    [Fact]
    public void ActionKindsDistinguishScenarios()
    {
        var kinds = NativeEditScenario.All.Select(NativeEditScenario.ActionKind).ToArray();

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
        Assert.DoesNotContain("none", kinds);
    }

    [Theory]
    [InlineData(0L, 0, 0)]
    [InlineData(0x00370000L, 0, 55)]
    [InlineData(0x0037000AL, 10, 55)]
    [InlineData(0xFFFF0000L, 0, -1)]
    [InlineData(unchecked((long)0xFFFFFFFF_FFFF0000), 0, -1)]
    public void PosFromCharUnpacksSignedLowAndHighWords(long packed, int expectedX, int expectedY)
    {
        var (x, y) = EditPosFromChar.Unpack(packed);

        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void PosFromCharNegativeCoordinatesSurviveUnpacking()
    {
        // 组合行滚到视口上方时 y 为负：必须解析为负值而不是 655xx。
        var (x, y) = EditPosFromChar.Unpack(unchecked((long)0xFFE40000UL));

        Assert.Equal(0, x);
        Assert.True(y < 0, $"expected negative y, got {y}");
    }

    [Fact]
    public void PosFromCharFailureIsDetected()
    {
        Assert.True(EditPosFromChar.IsFailure(-1));
        Assert.False(EditPosFromChar.IsFailure(0));
    }

    // ---- R28 collapse/restore：对“决定 resize 目标与判定”的同一套纯逻辑做单元测试 ----
    // 不是重复一遍枚举注册，而是直接断言动作会把高度压成 0、恢复会还原原宽高原点。

    [Theory]
    [InlineData(560, 420, 560, 0)]
    [InlineData(1, 1, 1, 0)]
    public void CollapseTargetKeepsWidthZeroesHeight(int w, int h, int expectW, int expectH)
    {
        var target = CollapseResizePlan.CollapseTarget(w, h);

        Assert.NotNull(target);
        Assert.Equal((expectW, expectH), target!.Value);
    }

    [Theory]
    [InlineData(0, 420)]
    [InlineData(560, 0)]
    [InlineData(-1, 420)]
    public void CollapseTargetRejectsNonPositiveGeometry(int w, int h)
    {
        Assert.Null(CollapseResizePlan.CollapseTarget(w, h));
    }

    [Fact]
    public void RestoreTargetIsTheOriginalRect()
    {
        Assert.Equal((560, 420), CollapseResizePlan.RestoreTarget(560, 420));
        Assert.Equal((3, 7), CollapseResizePlan.RestoreTarget(3, 7));
    }

    [Fact]
    public void CollapseFlagsMoveOnlySizeNeverFocusOrZOrder()
    {
        var flags = CollapseResizePlan.Flags;

        Assert.True((flags & CSharp.UI.ImeProbe.NativeMethods.SWP_NOMOVE) != 0);
        Assert.True((flags & CSharp.UI.ImeProbe.NativeMethods.SWP_NOZORDER) != 0);
        Assert.True((flags & CSharp.UI.ImeProbe.NativeMethods.SWP_NOACTIVATE) != 0);
        const uint swpHideWindow = 0x0080; // SWP_HIDEWINDOW（禁用，本工具只声明了安全子集）
        Assert.Equal(0u, flags & swpHideWindow);
        Assert.Equal(0u, flags & CSharp.UI.ImeProbe.NativeMethods.SWP_SHOWWINDOW);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 5, false)]   // 窗口高度 0 但 client 仍非空 → 不算真正 collapse
    [InlineData(1, 0, false)]
    public void CollapsedToZeroHeightNeedsWindowAndClientZero(int wh, int ch, bool expected)
    {
        Assert.Equal(expected, CollapseResizePlan.CollapsedToZeroHeight(wh, ch));
    }

    [Fact]
    public void GeometryPreservedAfterCollapseRequiresEverything()
    {
        // 全部满足
        Assert.True(CollapseResizePlan.GeometryPreservedAfterCollapse(
            0, 0, 96, 176, 560, 96, 176, 560, parentUnchanged: true, viewportUnchanged: true));
        // 高度非 0
        Assert.False(CollapseResizePlan.GeometryPreservedAfterCollapse(
            8, 8, 96, 176, 560, 96, 176, 560, true, true));
        // 原点移动
        Assert.False(CollapseResizePlan.GeometryPreservedAfterCollapse(
            0, 0, 96, 176, 560, 97, 176, 560, true, true));
        // 宽度变化
        Assert.False(CollapseResizePlan.GeometryPreservedAfterCollapse(
            0, 0, 96, 176, 560, 96, 176, 559, true, true));
        // 父窗/viewport 变化
        Assert.False(CollapseResizePlan.GeometryPreservedAfterCollapse(
            0, 0, 96, 176, 560, 96, 176, 560, false, true));
        Assert.False(CollapseResizePlan.GeometryPreservedAfterCollapse(
            0, 0, 96, 176, 560, 96, 176, 560, true, false));
    }

    [Fact]
    public void RestoredToOriginalNeedsSameOriginAndSize()
    {
        Assert.True(CollapseResizePlan.RestoredToOriginal(96, 176, 560, 420, 96, 176, 560, 420));
        Assert.False(CollapseResizePlan.RestoredToOriginal(96, 176, 560, 420, 96, 177, 560, 420));
        Assert.False(CollapseResizePlan.RestoredToOriginal(96, 176, 560, 420, 96, 176, 560, 0));
        Assert.False(CollapseResizePlan.RestoredToOriginal(96, 176, 560, 420, 96, 176, 500, 420));
    }

    [Fact]
    public void BaselineTargetsARealLineEnd()
    {
        var text = EditBaseline.Text();
        var start = EditBaseline.TargetLineStart();
        var end = EditBaseline.TargetLineEnd();

        Assert.Equal(EditBaseline.LineCount, text.Split("\r\n").Length);
        // 目标行末确实落在目标行文本末尾，而不是固定偏移。
        Assert.Equal(EditBaseline.LineText(EditBaseline.TargetLine),
            text.Substring(start, end - start));
        Assert.Equal(EditBaseline.LineLength(EditBaseline.TargetLine), end - start);
    }
}
