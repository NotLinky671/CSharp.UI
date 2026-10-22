using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R34C：用脚本化 <see cref="ICandidateListOperations"/> 假对象驱动真实的
/// <see cref="CandidatePageReader.Run"/>（与生产完全同一控制流），验证
/// <list type="bullet">
/// <item>多页候选只读当前页区间 [pageStart, pageEnd) 的 GetString；</item>
/// <item>GetCurrentPage / GetPageIndex 失败如实 Failed，绝不回退猜页；</item>
/// <item>ended 在每次原生调用后复核为 true 时立即停止后续调用；</item>
/// <item>GetPageIndex(fill) 失败或抛异常时原生 buffer 仍被释放（分配/释放配平）；</item>
/// <item>GetSelection S_FALSE 如实“无选择”，不伪造选中项；</item>
/// <item><see cref="CandidateSnapshot"/> 对条目列表做防御性复制。</item>
/// </list>
/// </summary>
public sealed class CandidatePageReaderTests
{
    /// <summary>脚本化假操作集：逐方法配置返回值/out 值/异常，并记录调用次数。</summary>
    private sealed class FakeCandidateListOperations : ICandidateListOperations
    {
        internal int CountHr = TsfHResult.S_OK;
        internal uint Count;
        internal int SelectionHr = TsfHResult.S_OK;
        internal uint Selection;
        internal int CurrentPageHr = TsfHResult.S_OK;
        internal uint CurrentPage;
        internal int PageIndexCountHr = TsfHResult.S_OK;
        internal uint PageCount;
        internal int PageIndexFillHr = TsfHResult.S_OK;
        internal uint[]? PageStarts;
        internal uint PageIndexFilled;
        internal Exception? PageIndexFillThrow;
        internal int StringHr = TsfHResult.S_OK;
        internal Func<uint, string?>? StringFor;

        internal int GetCountCalls;
        internal int GetSelectionCalls;
        internal int GetCurrentPageCalls;
        internal int GetPageIndexCalls;
        internal int GetStringCalls;
        internal readonly List<uint> StringIndexes = new();

        public int GetCount(out uint count)
        {
            GetCountCalls++;
            count = Count;
            return CountHr;
        }

        public int GetSelection(out uint index)
        {
            GetSelectionCalls++;
            index = Selection;
            return SelectionHr;
        }

        public int GetString(uint index, out string? text)
        {
            GetStringCalls++;
            StringIndexes.Add(index);
            text = StringFor?.Invoke(index) ?? $"cand-{index}";
            return StringHr;
        }

        public int GetPageIndex(IntPtr indices, uint size, out uint pageCount)
        {
            GetPageIndexCalls++;
            if (indices == IntPtr.Zero || size == 0)
            {
                pageCount = PageCount;
                return PageIndexCountHr;
            }

            if (PageIndexFillThrow is not null)
            {
                throw PageIndexFillThrow;
            }

            pageCount = PageIndexFilled;
            if (PageIndexFillHr == TsfHResult.S_OK && PageStarts is not null)
            {
                for (var i = 0; i < PageStarts.Length && i < (int)size; i++)
                {
                    Marshal.WriteInt32(indices, i * sizeof(uint), unchecked((int)PageStarts[i]));
                }
            }

            return PageIndexFillHr;
        }

        public int GetCurrentPage(out uint page)
        {
            GetCurrentPageCalls++;
            page = CurrentPage;
            return CurrentPageHr;
        }
    }

    /// <summary>计数分配器：走与生产相同的 Marshal.AllocHGlobal，仅记录配平。</summary>
    private sealed class CountingAllocator
    {
        internal int AllocCalls;
        internal int FreeCalls;

        internal IntPtr Alloc(int bytes)
        {
            AllocCalls++;
            return Marshal.AllocHGlobal(bytes);
        }

        internal void Free(IntPtr ptr)
        {
            FreeCalls++;
            Marshal.FreeHGlobal(ptr);
        }

        internal int Outstanding => AllocCalls - FreeCalls;
    }

    /// <summary>ended 在第 N 次原生调用后置为 true 的探针（按假对象调用计数推进）。</summary>
    private sealed class EndAfterProbe
    {
        private readonly Func<int> _calls;
        private readonly int _threshold;
        private bool _ended;

        internal EndAfterProbe(Func<int> calls, int threshold)
        {
            _calls = calls;
            _threshold = threshold;
        }

        internal bool Probe()
        {
            if (_calls() >= _threshold)
            {
                _ended = true;
            }

            return _ended;
        }
    }

    private static int TotalCalls(FakeCandidateListOperations f)
        => f.GetCountCalls + f.GetSelectionCalls + f.GetCurrentPageCalls
            + f.GetPageIndexCalls + f.GetStringCalls;

    /// <summary>标准两页脚本：count=6、starts=[0,3]、current=1 → 本页应读 3,4,5。</summary>
    private static FakeCandidateListOperations TwoPageFake()
        => new()
        {
            Count = 6,
            Selection = 4,
            CurrentPage = 1,
            PageCount = 2,
            PageStarts = new uint[] { 0, 3 },
            PageIndexFilled = 2,
        };

    [Fact]
    public void ReadsOnlyCurrentPageEntries()
    {
        var fake = TwoPageFake();
        var read = CandidatePageReader.Run(fake, () => false);

        Assert.True(read.Ok);
        Assert.Equal(6u, read.Count);
        Assert.Equal(1u, read.CurrentPage);
        Assert.Equal(3u, read.PageStart);
        Assert.Equal(4u, read.Selection);
        // 本页 [3,6)：只读 3、4、5 三条，绝不多发 GetString。
        Assert.Equal(new uint[] { 3, 4, 5 }, fake.StringIndexes);
        Assert.Equal(3, read.Entries.Count);
        Assert.Equal(new[] { "cand-3", "cand-4", "cand-5" }, read.Entries);
        // GetPageIndex 恰好两次：先计数再填表。
        Assert.Equal(2, fake.GetPageIndexCalls);
    }

    [Fact]
    public void GetCurrentPageFailureProducesFailed()
    {
        var fake = TwoPageFake();
        fake.CurrentPageHr = TsfHResult.E_FAIL;

        var read = CandidatePageReader.Run(fake, () => false);

        Assert.False(read.Ok);
        Assert.Contains("GetCurrentPage", read.Failure);
        Assert.Empty(read.Entries);
        // 页元数据失败即整体失败——绝不进入 GetPageIndex/GetString。
        Assert.Equal(0, fake.GetPageIndexCalls);
        Assert.Equal(0, fake.GetStringCalls);
    }

    [Fact]
    public void GetPageIndexCountFailureProducesFailed()
    {
        var fake = TwoPageFake();
        fake.PageIndexCountHr = TsfHResult.E_FAIL;

        var read = CandidatePageReader.Run(fake, () => false);

        Assert.False(read.Ok);
        Assert.Contains("GetPageIndex", read.Failure);
        Assert.Equal(1, fake.GetPageIndexCalls);
        Assert.Equal(0, fake.GetStringCalls);
    }

    [Fact]
    public void GetPageIndexFillFailureProducesFailedAndFrees()
    {
        var fake = TwoPageFake();
        fake.PageIndexFillHr = TsfHResult.E_FAIL;
        var alloc = new CountingAllocator();

        var read = CandidatePageReader.Run(fake, () => false, alloc.Alloc, alloc.Free);

        Assert.False(read.Ok);
        Assert.Contains("GetPageIndex", read.Failure);
        Assert.Equal(2, fake.GetPageIndexCalls);
        Assert.Equal(1, alloc.AllocCalls);
        // 关键回归：fill 失败也必须释放已分配 buffer。
        Assert.Equal(0, alloc.Outstanding);
        Assert.Equal(0, fake.GetStringCalls);
    }

    [Fact]
    public void GetPageIndexFillThrowFreesAndSourceWrapsAsFailed()
    {
        var fake = TwoPageFake();
        fake.PageIndexFillThrow = new InvalidOperationException("com exploded");
        var alloc = new CountingAllocator();

        // 异常出口同样配平：finally 在异常逃逸前释放 buffer。
        Assert.Throws<InvalidOperationException>(
            () => CandidatePageReader.Run(fake, () => false, alloc.Alloc, alloc.Free));
        Assert.Equal(1, alloc.AllocCalls);
        Assert.Equal(0, alloc.Outstanding);

        // 经 ComCandidateSnapshotSource（真实发布路径）时异常如实转 Failed。
        var fake2 = TwoPageFake();
        fake2.PageIndexFillThrow = new InvalidOperationException("com exploded");
        var wrapped = new ComCandidateSnapshotSource(fake2).ReadPage(() => false);
        Assert.False(wrapped.Ok);
        Assert.Contains("InvalidOperationException", wrapped.Failure);
    }

    [Fact]
    public void EndAfterGetCountStopsSubsequentCalls()
    {
        var fake = TwoPageFake();
        // 第一次原生调用（GetCount）后 ended=true → 不得再发任何调用。
        var probe = new EndAfterProbe(() => TotalCalls(fake), 1);

        var read = CandidatePageReader.Run(fake, probe.Probe);

        Assert.False(read.Ok);
        Assert.Contains("ended", read.Failure);
        Assert.Equal(1, fake.GetCountCalls);
        Assert.Equal(0, fake.GetSelectionCalls);
        Assert.Equal(0, fake.GetCurrentPageCalls);
        Assert.Equal(0, fake.GetPageIndexCalls);
        Assert.Equal(0, fake.GetStringCalls);
    }

    [Fact]
    public void EndAfterGetPageIndexFillStopsGetString()
    {
        var fake = TwoPageFake();
        var alloc = new CountingAllocator();
        // 第 4 次原生调用（count+sel+page+pageindex(count)=4 之前 fill 是第 5 次）
        // GetCount=1 GetSelection=2 GetCurrentPage=3 GetPageIndex(count)=4 GetPageIndex(fill)=5
        var probe = new EndAfterProbe(() => TotalCalls(fake), 5);

        var read = CandidatePageReader.Run(fake, probe.Probe, alloc.Alloc, alloc.Free);

        Assert.False(read.Ok);
        Assert.Contains("ended", read.Failure);
        Assert.Equal(2, fake.GetPageIndexCalls);
        // 页区间已算出但 ended 中断：绝不再发 GetString。
        Assert.Equal(0, fake.GetStringCalls);
        Assert.Equal(0, alloc.Outstanding);
    }

    [Fact]
    public void SFalseSelectionDoesNotFabricateSelection()
    {
        var fake = TwoPageFake();
        fake.SelectionHr = TsfHResult.S_FALSE;

        var read = CandidatePageReader.Run(fake, () => false);

        Assert.True(read.Ok);
        // 无选择如实为 MaxValue——绝不伪造一个貌似合理的选中项。
        Assert.Equal(uint.MaxValue, read.Selection);
        Assert.Equal(3, read.Entries.Count);
    }

    [Fact]
    public void PositiveNonFalseSelectionHresultIsFailure()
    {
        var fake = TwoPageFake();
        // 正 HRESULT 且非 S_FALSE：不得把 out 值当成真实选中。
        fake.SelectionHr = 2;

        var read = CandidatePageReader.Run(fake, () => false);

        Assert.False(read.Ok);
        Assert.Contains("GetSelection", read.Failure);
    }

    [Fact]
    public void SnapshotCopiesEntryList()
    {
        var mutable = new List<CandidateSnapshotEntry>
        {
            new(0, "a"),
            new(1, "b"),
        };
        var snapshot = new CandidateSnapshot(1, 1, mutable, 0, 0, 2, 0, 0, true, null);

        // 快照发布后改写源 List：已发布内容绝不被污染。
        mutable.Add(new CandidateSnapshotEntry(2, "c"));
        mutable[0] = new CandidateSnapshotEntry(9, "mutated");

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal(0u, snapshot.Entries[0].Index);
        Assert.Equal("a", snapshot.Entries[0].Text);
    }

    [Fact]
    public void CountZeroReturnsEmptySuccess()
    {
        var fake = new FakeCandidateListOperations { Count = 0 };

        var read = CandidatePageReader.Run(fake, () => false);

        Assert.True(read.Ok);
        Assert.Equal(0u, read.Count);
        Assert.Empty(read.Entries);
        // 合约语义：空列表不再发选择/页/串查询。
        Assert.Equal(0, fake.GetSelectionCalls);
        Assert.Equal(0, fake.GetPageIndexCalls);
        Assert.Equal(0, fake.GetStringCalls);
    }
}
