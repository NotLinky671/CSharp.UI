using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3b：候选页快照/有界读取契约测试；直接引用生产类型，不创建窗口或激活 TSF。
/// </summary>
public sealed class CandidateSnapshotContractTests
{
    // ---------------------------------------------------------------- 脚本化候选来源

    private sealed class ScriptedOps : ICandidateListOperations
    {
        public uint Count { get; set; }
        public int CountHr { get; set; }
        public uint Selection { get; set; }
        public int SelectionHr { get; set; }
        public uint CurrentPage { get; set; }
        public int CurrentPageHr { get; set; }
        public uint[] Pages { get; set; } = [0];
        public uint? DeclaredPageCount { get; set; }
        public int PageCountHr { get; set; }
        public int PageTableHr { get; set; }
        public uint? FilledOverride { get; set; }
        public Func<uint, int> StringHr { get; set; } = _ => 0;
        public Func<uint, string?> StringText { get; set; } = index => $"c{index}";
        public bool ThrowOnCount { get; set; }
        public bool ThrowOnTable { get; set; }

        public int CountCalls { get; private set; }
        public int SelectionCalls { get; private set; }
        public int CurrentPageCalls { get; private set; }
        public int PageCountCalls { get; private set; }
        public int PageTableCalls { get; private set; }
        public List<uint> StringCalls { get; } = [];

        public int GetCount(out uint count)
        {
            CountCalls++;
            if (ThrowOnCount)
            {
                throw new InvalidOperationException("scripted count failure");
            }

            count = Count;
            return CountHr;
        }

        public int GetSelection(out uint index)
        {
            SelectionCalls++;
            index = Selection;
            return SelectionHr;
        }

        public int GetString(uint index, out string? text)
        {
            StringCalls.Add(index);
            text = StringText(index);
            return StringHr(index);
        }

        public int GetPageIndex(IntPtr indices, uint size, out uint pageCount)
        {
            if (size == 0)
            {
                PageCountCalls++;
                pageCount = DeclaredPageCount ?? (uint)Pages.Length;
                return PageCountHr;
            }

            PageTableCalls++;
            if (ThrowOnTable)
            {
                throw new InvalidOperationException("scripted table failure");
            }

            pageCount = FilledOverride ?? (uint)Pages.Length;
            for (var i = 0; i < Pages.Length && i < size; i++)
            {
                Marshal.WriteInt32(indices, i * sizeof(uint), (int)Pages[i]);
            }

            return PageTableHr;
        }

        public int GetCurrentPage(out uint page)
        {
            CurrentPageCalls++;
            page = CurrentPage;
            return CurrentPageHr;
        }
    }

    private static CandidatePageRead Read(ScriptedOps ops, Func<bool>? ended = null)
        => CandidatePageReader.Run(ops, ended ?? (() => false));

    private static void AssertFailed(CandidatePageRead read, string fragment)
    {
        Assert.False(read.Ok);
        Assert.Empty(read.Entries);
        Assert.NotNull(read.Failure);
        Assert.Contains(fragment, read.Failure);
    }

    // ---------------------------------------------------------------- 成功读取

    [Fact]
    public void LargeTotalCount_IsReadPerPage_NotRejectedAt256()
    {
        var ops = new ScriptedOps
        {
            Count = 1000,
            Selection = 999,
            SelectionHr = TsfHResult.S_OK,
            CurrentPage = 2,
            Pages = [0, 500, 900],
        };

        var read = Read(ops);

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(1000u, read.Count);
        Assert.Equal(999u, read.Selection);
        Assert.Equal(900u, read.PageStart);
        Assert.Equal(2u, read.CurrentPage);
        // 只读当前页且受 MaxPageEntries 截断：100 条候选的页只取 32 条。
        Assert.Equal(CandidateSnapshotLimits.MaxPageEntries, read.Entries.Count);
        Assert.Equal(900u, ops.StringCalls[0]);
        Assert.Equal(931u, ops.StringCalls[^1]);
        Assert.Equal(CandidateSnapshotLimits.MaxPageEntries, ops.StringCalls.Count);
        Assert.Equal("c900", read.Entries[0]);
        Assert.Equal("c931", read.Entries[^1]);
    }

    [Fact]
    public void ReadsFullFirstPage_WithPageBoundaries()
    {
        var ops = new ScriptedOps
        {
            Count = 12,
            Selection = 3,
            CurrentPage = 0,
            Pages = [0, 5],
        };

        var read = Read(ops);

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(5, read.Entries.Count);
        Assert.Equal(0u, read.PageStart);
        Assert.Equal([0u, 1u, 2u, 3u, 4u], ops.StringCalls);
    }

    [Fact]
    public void EmptyCandidateList_IsValidEmpty_AndQueriesNothingElse()
    {
        var ops = new ScriptedOps { Count = 0 };

        var read = Read(ops);

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(0u, read.Count);
        Assert.Empty(read.Entries);
        Assert.Equal(uint.MaxValue, read.Selection);
        Assert.Equal(0, ops.SelectionCalls);
        Assert.Equal(0, ops.CurrentPageCalls);
        Assert.Equal(0, ops.PageCountCalls);
        Assert.Empty(ops.StringCalls);
    }

    [Fact]
    public void GetSelectionSFalse_MeansNoSelection()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            Selection = 0,
            SelectionHr = TsfHResult.S_FALSE,
            Pages = [0],
        };

        var read = Read(ops);

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(uint.MaxValue, read.Selection);
    }

    // ---------------------------------------------------------------- 失败与边界

    [Fact]
    public void GetCountFailure_Fails()
    {
        var ops = new ScriptedOps { Count = 1, CountHr = TsfHResult.E_FAIL };

        AssertFailed(Read(ops), "GetCount");
        Assert.Equal(0, ops.SelectionCalls);
    }

    [Fact]
    public void GetSelectionNegativeFailure_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            SelectionHr = TsfHResult.E_FAIL,
            Pages = [0],
        };

        AssertFailed(Read(ops), "GetSelection");
        Assert.Equal(0, ops.CurrentPageCalls);
    }

    [Fact]
    public void GetSelectionOtherPositiveHResult_Fails_WithoutTrustingOutValue()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            SelectionHr = 2,
            Pages = [0],
        };

        AssertFailed(Read(ops), "GetSelection");
    }

    [Fact]
    public void SelectionOutOfRange_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            Selection = 5,
            Pages = [0],
        };

        AssertFailed(Read(ops), "out of range");
    }

    [Fact]
    public void GetCurrentPageFailure_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            CurrentPageHr = TsfHResult.E_FAIL,
            Pages = [0],
        };

        AssertFailed(Read(ops), "GetCurrentPage");
    }

    [Fact]
    public void ZeroPageCount_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            Pages = [],
        };

        AssertFailed(Read(ops), "count out of range");
    }

    [Fact]
    public void PageCountAboveLimit_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            DeclaredPageCount = CandidateSnapshotLimits.MaxPageCount + 1,
        };

        AssertFailed(Read(ops), "count out of range");
        Assert.Equal(0, ops.PageTableCalls);
    }

    [Fact]
    public void CurrentPageOutsidePageTable_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            CurrentPage = 1,
            Pages = [0],
        };

        AssertFailed(Read(ops), "GetCurrentPage out of range");
    }

    [Fact]
    public void PageCountQueryFailure_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            PageCountHr = TsfHResult.E_FAIL,
        };

        AssertFailed(Read(ops), "GetPageIndex(count)");
    }

    [Fact]
    public void PageTableFailure_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            Pages = [0, 3],
            PageTableHr = TsfHResult.E_FAIL,
        };

        AssertFailed(Read(ops), "GetPageIndex(entries)");
    }

    [Fact]
    public void PageTableFilledCountMismatch_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            Pages = [0, 3],
            FilledOverride = 1,
        };

        AssertFailed(Read(ops), "count mismatch");
    }

    [Theory]
    [InlineData(new uint[] { 1, 3 }, "page[0] start not zero")]
    [InlineData(new uint[] { 0, 3, 3 }, "not strictly increasing")]
    [InlineData(new uint[] { 0, 3, 2 }, "not strictly increasing")]
    [InlineData(new uint[] { 0, 5 }, "start out of range")]
    public void CorruptPageTable_Fails(uint[] pages, string fragment)
    {
        var ops = new ScriptedOps
        {
            Count = 5,
            CurrentPage = 0,
            Pages = pages,
        };

        AssertFailed(Read(ops), fragment);
    }

    [Fact]
    public void GetStringFailure_Fails()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            Pages = [0],
            StringHr = index => index == 1 ? TsfHResult.E_FAIL : TsfHResult.S_OK,
        };

        AssertFailed(Read(ops), "GetString(1)");
        Assert.Equal([0u, 1u], ops.StringCalls);
    }

    [Fact]
    public void CurrentPageResolvesLastPageToTotalCount()
    {
        var ops = new ScriptedOps
        {
            Count = 7,
            CurrentPage = 1,
            Pages = [0, 4],
        };

        var read = Read(ops);

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(4u, read.PageStart);
        Assert.Equal(3, read.Entries.Count);
        Assert.Equal([4u, 5u, 6u], ops.StringCalls);
    }

    // ---------------------------------------------------------------- 结束/重入

    [Fact]
    public void SourceEndedBeforeRead_IssuesNoComCalls()
    {
        var ops = new ScriptedOps { Count = 3, Pages = [0] };

        AssertFailed(Read(ops, () => true), "ended before read");
        Assert.Equal(0, ops.CountCalls);
    }

    [Fact]
    public void SourceEndedAfterCount_StopsBeforeSelection()
    {
        var ops = new ScriptedOps { Count = 3, Pages = [0] };
        var probes = 0;
        Func<bool> ended = () => ++probes > 1;

        AssertFailed(Read(ops, ended), "ended during read");
        Assert.Equal(1, ops.CountCalls);
        Assert.Equal(0, ops.SelectionCalls);
    }

    [Fact]
    public void SourceEndedDuringStringRead_StopsImmediately()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            Pages = [0],
        };
        var probes = 0;
        Func<bool> ended = () => ++probes >= 8;

        AssertFailed(Read(ops, ended), "ended during read");
        Assert.Equal(2, ops.StringCalls.Count);
    }

    [Fact]
    public void EndedProbeThrowing_FailsClosedBeforeCom()
    {
        var ops = new ScriptedOps { Count = 3, Pages = [0] };

        AssertFailed(Read(ops, () => throw new InvalidOperationException("probe")),
            "ended before read");
        Assert.Equal(0, ops.CountCalls);
    }

    // ---------------------------------------------------------------- 原生缓冲配平

    [Fact]
    public void PageTableBuffer_FreedExactlyOnce_OnSuccess()
    {
        var ops = new ScriptedOps { Count = 3, Pages = [0] };
        var allocs = 0;
        var frees = 0;

        var read = CandidatePageReader.Run(ops, () => false,
            size => { allocs++; return Marshal.AllocHGlobal(size); },
            ptr => { frees++; Marshal.FreeHGlobal(ptr); });

        Assert.True(read.Ok, read.Failure);
        Assert.Equal(1, allocs);
        Assert.Equal(1, frees);
    }

    [Fact]
    public void PageTableBuffer_FreedExactlyOnce_OnHResultFailure()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            Pages = [0],
            PageTableHr = TsfHResult.E_FAIL,
        };
        var allocs = 0;
        var frees = 0;

        var read = CandidatePageReader.Run(ops, () => false,
            size => { allocs++; return Marshal.AllocHGlobal(size); },
            ptr => { frees++; Marshal.FreeHGlobal(ptr); });

        Assert.False(read.Ok);
        Assert.Equal(1, allocs);
        Assert.Equal(1, frees);
    }

    [Fact]
    public void PageTableBuffer_FreedExactlyOnce_WhenNativeCallThrows()
    {
        var ops = new ScriptedOps
        {
            Count = 3,
            Pages = [0],
            ThrowOnTable = true,
        };
        var allocs = 0;
        var frees = 0;

        Assert.Throws<InvalidOperationException>(() =>
            CandidatePageReader.Run(ops, () => false,
                size => { allocs++; return Marshal.AllocHGlobal(size); },
                ptr => { frees++; Marshal.FreeHGlobal(ptr); }));

        Assert.Equal(1, allocs);
        Assert.Equal(1, frees);
    }

    [Fact]
    public void SnapshotSource_NonCandidateElement_ReportsFailure()
    {
        var source = new ComCandidateSnapshotSource((ITfUIElement?)null);

        Assert.False(source.IsCandidateList);
        var read = source.ReadPage(() => false);
        AssertFailed(read, "not a candidate list");
    }

    [Fact]
    public void SnapshotSource_ComException_FailsClosed_WithoutEscaping()
    {
        var source = new ComCandidateSnapshotSource(
            new ScriptedOps { ThrowOnCount = true });

        var read = source.ReadPage(() => false);

        AssertFailed(read, "InvalidOperationException");
    }

    // ---------------------------------------------------------------- 文本/快照语义

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("候选", "候选")]
    public void TruncateCandidateText_KeepsShortText(string? input, string expected)
        => Assert.Equal(expected, CandidatePageReader.TruncateCandidateText(input));

    [Fact]
    public void TruncateCandidateText_CapsAt128()
    {
        var input = new string('x', CandidateSnapshotLimits.MaxCandidateTextLength + 1);

        var text = CandidatePageReader.TruncateCandidateText(input);

        Assert.Equal(CandidateSnapshotLimits.MaxCandidateTextLength, text.Length);
    }

    [Fact]
    public void TruncateCandidateText_DoesNotSplitSurrogatePair()
    {
        var input = new string('x', CandidateSnapshotLimits.MaxCandidateTextLength - 1)
            + "😀" + "z";

        var text = CandidatePageReader.TruncateCandidateText(input);

        Assert.Equal(CandidateSnapshotLimits.MaxCandidateTextLength - 1, text.Length);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    [Fact]
    public void Snapshot_DefensivelyCopiesEntries()
    {
        var entries = new List<CandidateSnapshotEntry>
        {
            new(0, "a"),
        };
        var snapshot = new CandidateSnapshot(0, 1, entries, 0, 0, 1, 0, 0, true, null);

        entries.Add(new CandidateSnapshotEntry(1, "b"));
        entries[0] = new CandidateSnapshotEntry(9, "changed");

        Assert.Single(snapshot.Entries);
        Assert.Equal("a", snapshot.Entries[0].Text);
        Assert.Equal(0u, snapshot.Entries[0].Index);
    }

    [Fact]
    public void Snapshot_PageLocalSelection_MapsOnlyCurrentPage()
    {
        Assert.Equal(2, CandidateSnapshot.PageLocalSelection(2, 0, 3));
        Assert.Equal(0, CandidateSnapshot.PageLocalSelection(5, 5, 1));
        Assert.Equal(-1, CandidateSnapshot.PageLocalSelection(0, 5, 3));
        Assert.Equal(-1, CandidateSnapshot.PageLocalSelection(5, 5, 0));
        Assert.Equal(-1, CandidateSnapshot.PageLocalSelection(uint.MaxValue, 0, 3));
    }

    [Fact]
    public void Snapshot_SelectionWithinCount_RejectsUIntMax()
    {
        Assert.True(CandidateSnapshot.SelectionWithinCount(0, 1));
        Assert.True(CandidateSnapshot.SelectionWithinCount(uint.MaxValue - 1, uint.MaxValue));
        Assert.False(CandidateSnapshot.SelectionWithinCount(uint.MaxValue, uint.MaxValue));
        Assert.False(CandidateSnapshot.SelectionWithinCount(5, 5));
    }

}
