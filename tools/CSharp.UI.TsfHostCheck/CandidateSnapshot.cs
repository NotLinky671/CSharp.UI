using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R34：一次宿主自绘候选的<b>有界、不可变</b>显示快照。元素 id 与
/// <see cref="HostDrawnCandidatePresenter"/> 分配的单调代数、当前页的候选
/// 字符串（真实序号）、选中项（页内索引 + 真实序号）、页元数据与有效/失败
/// 状态全部在构建时冻结——发布之后任何后续 COM 变化都不会改写已展示内容。
/// </summary>
internal sealed class CandidateSnapshot
{
    internal CandidateSnapshot(uint elementId, uint generation, IReadOnlyList<CandidateSnapshotEntry> entries,
        int selectionOnPage, uint selectionIndex, uint totalCount, uint pageStart,
        uint currentPage, bool valid, string? failure)
    {
        ElementId = elementId;
        Generation = generation;
        // 防御性复制 + 只读封装：调用方持有的 List 若随后被改写，绝不能污染已发布快照。
        Entries = new ReadOnlyCollection<CandidateSnapshotEntry>(
            (entries ?? Array.Empty<CandidateSnapshotEntry>()).ToArray());
        SelectionOnPage = selectionOnPage;
        SelectionIndex = selectionIndex;
        TotalCount = totalCount;
        PageStart = pageStart;
        CurrentPage = currentPage;
        Valid = valid;
        Failure = failure;
    }

    /// <summary>TSF UIElement id。</summary>
    internal uint ElementId { get; }

    /// <summary>呈现器分配的单调代数（元素注册序）；恢复时据此判定“同一个元素”。</summary>
    internal uint Generation { get; }

    /// <summary>当前页的候选条目（真实序号 + 文本），只读且防御复制，已按上界截断。</summary>
    internal IReadOnlyList<CandidateSnapshotEntry> Entries { get; }

    /// <summary>选中项在 <see cref="Entries"/> 内的页内索引；无选中为 -1。</summary>
    internal int SelectionOnPage { get; }

    /// <summary>选中项的真实候选序号（<c>GetSelection</c> 原值）。</summary>
    internal uint SelectionIndex { get; }

    /// <summary>候选总数（<c>GetCount</c> 原值）。</summary>
    internal uint TotalCount { get; }

    /// <summary>当前页第一条候选的真实序号。</summary>
    internal uint PageStart { get; }

    /// <summary><c>GetCurrentPage</c> 原值（不可用为 <see cref="uint.MaxValue"/>）。</summary>
    internal uint CurrentPage { get; }

    /// <summary>本快照是否为成功查询的结果；false 时 <see cref="Entries"/> 为空。</summary>
    internal bool Valid { get; }

    /// <summary>失败快照的具体原因（Valid 为 true 时为 null）。</summary>
    internal string? Failure { get; }

    /// <summary>该快照是否真实含候选（log/gate 据此拒绝“空快照冒充有候选”）。</summary>
    internal bool HasCandidates => Valid && Entries.Count > 0;

    /// <summary>候选数量与页/选中是否越界的<b>一致性</b>判定（托管可测）。</summary>
    internal static bool SelectionWithinCount(uint selection, uint count)
        => selection < count;

    /// <summary>把真实选中序号映射到当前页内的可见索引；不在本页返回 -1。</summary>
    internal static int PageLocalSelection(uint selectionIndex, uint pageStart, int entryCount)
        => selectionIndex >= pageStart && selectionIndex < pageStart + (uint)entryCount
            ? (int)(selectionIndex - pageStart)
            : -1;

    internal string Describe()
        => $"id={ElementId} gen={Generation} valid={Valid} count={TotalCount} "
            + $"page={CurrentPage} start={PageStart} sel={SelectionIndex} "
            + $"sel_on_page={SelectionOnPage} entries={Entries.Count}"
            + (Valid ? string.Empty : $" failure={Failure ?? "(unknown)"}");

    internal string DescribeContent(int maxItems = 12)
    {
        var shown = Entries.Take(Math.Max(0, maxItems))
            .Select(e => $"{e.Index}:{e.Text}");
        var suffix = Entries.Count > maxItems ? $" …(+{Entries.Count - maxItems})" : string.Empty;
        return string.Join(" | ", shown) + suffix;
    }
}

/// <summary>快照内一条候选：真实 TSF 序号 + 原始文本（绝不改编号语义）。</summary>
internal readonly record struct CandidateSnapshotEntry(uint Index, string Text);

/// <summary>
/// 候选来源的一次查询结果：默认实现为真失败。真实路径由
/// <see cref="ElementInfo"/> 包装 <see cref="ITfCandidateListUIElement"/>；
/// 测试用同一接口注入假数据——呈现器控制流对两者完全一致（无平行实现）。
/// </summary>
internal readonly record struct CandidatePageRead(
    bool Ok, uint Count, uint Selection, uint PageStart, uint CurrentPage,
    IReadOnlyList<string> Entries, string? Failure)
{
    internal static CandidatePageRead Failed(string reason)
        => new(false, 0, 0, 0, uint.MaxValue, Array.Empty<string>(), reason);
}

/// <summary>
/// R34：从“存活的候选元素”读取一页候选的<b>可注入来源</b>。呈现器只依赖本接口，
/// 因此 COM 细节（vtable、异常、已释放 RCW）全部留在实现内；任何一步失败都
/// 返回 <see cref="CandidatePageRead.Failed"/>——绝不把部分结果冒充成功。
/// </summary>
internal interface ICandidateSnapshotSource
{
    /// <summary>元素是否仍是候选列表类型（QI 判定，不依赖候选内容已就绪）。</summary>
    bool IsCandidateList { get; }

    /// <summary>
    /// 读取当前页候选；失败如实返回 Failed(reason)。
    /// <paramref name="sourceEnded"/> 在<b>每次</b>原生查询前后复核——返回 true 时立即
    /// 停止，绝不对已 End/已替换的元素再发任何 COM 调用。
    /// </summary>
    CandidatePageRead ReadPage(Func<bool> sourceEnded);
}

/// <summary>
/// 原生候选查询的<b>逐操作适配层</b>：生产实现逐方法转发
/// <see cref="ITfCandidateListUIElement"/>；测试注入脚本化操作，驱动与生产完全
/// 相同的 <see cref="CandidatePageReader.Run"/> 控制流（含逐调用 End 判定）。
/// 每次调用返回 raw HRESULT——适配器绝不把正 HRESULT 解释成成功。
/// </summary>
internal interface ICandidateListOperations
{
    int GetCount(out uint count);

    int GetSelection(out uint index);

    int GetString(uint index, out string? text);

    int GetPageIndex(IntPtr indices, uint size, out uint pageCount);

    int GetCurrentPage(out uint page);
}

/// <summary>生产适配器：把每个操作直接转发给真实 <see cref="ITfCandidateListUIElement"/>。</summary>
internal sealed class ComCandidateListOperations : ICandidateListOperations
{
    private readonly ITfCandidateListUIElement _candidate;

    internal ComCandidateListOperations(ITfCandidateListUIElement candidate) => _candidate = candidate;

    public int GetCount(out uint count) => _candidate.GetCount(out count);

    public int GetSelection(out uint index) => _candidate.GetSelection(out index);

    public int GetString(uint index, out string? text) => _candidate.GetString(index, out text);

    public int GetPageIndex(IntPtr indices, uint size, out uint pageCount)
        => _candidate.GetPageIndex(indices, size, out pageCount);

    public int GetCurrentPage(out uint page) => _candidate.GetCurrentPage(out page);
}

/// <summary>
/// R34B：整页读取的<b>唯一控制流</b>——生产与测试共用。约束：
/// <list type="bullet">
/// <item>每次原生查询<b>前后</b>都复核 <paramref name="sourceEnded"/>；一旦为 true
/// 立即返回“查询中断”结果并带上已发查询计数——绝不对已 End 元素继续调用。</item>
/// <item>除 <c>GetSelection</c> 的 S_FALSE（无选择）与 <c>GetCount</c>==0 的合约语义外，
/// 任何 <c>hr != S_OK</c> 都是失败；S_FALSE 之外的正 HRESULT 不得伪造数据。</item>
/// <item>页边界严格：当前页 = <c>[pageStart[currentPage], pageStart[currentPage+1])</c>，
/// 末页以 totalCount 为终点；页表必须 [0,count) 内严格单调递增且 page[0]==0，
/// 元数据失败即整体 Failed——绝不回退“猜第一页”。</item>
/// <item>原生 buffer 用 try/finally 释放；条目数双重上界（页区间 ∩ MaxPageEntries）。</item>
/// </list>
/// </summary>
internal static class CandidatePageReader
{
    /// <summary>
    /// 执行一次完整页查询。<paramref name="ended"/> 在每次原生调用前后复核。
    /// </summary>
    internal static CandidatePageRead Run(ICandidateListOperations ops, Func<bool> ended)
        => Run(ops, ended, Marshal.AllocHGlobal, Marshal.FreeHGlobal);

    /// <summary>
    /// 同上，但原生 buffer 的分配/释放可注入（测试用计数实现验证配平）——
    /// 控制流与生产完全一致，绝不复制一份“测试专用读页逻辑”。
    /// </summary>
    internal static CandidatePageRead Run(ICandidateListOperations ops, Func<bool> ended,
        Func<int, IntPtr> alloc, Action<IntPtr> free)
    {
        // 查询入口前先判源有效；调用方已持有借用/视图注册。
        if (Ended(ended))
        {
            return CandidatePageRead.Failed("source ended before read");
        }

        var hr = ops.GetCount(out var count);
        if (hr != TsfHResult.S_OK)
        {
            return CandidatePageRead.Failed($"GetCount {TsfHResult.Describe(hr)}");
        }

        if (Ended(ended))
        {
            return CandidatePageRead.Failed("source ended during read");
        }

        // count==0 是合约允许的空列表：不再发任何选择/页/串查询，如实返回空成功页。
        if (count == 0)
        {
            return new CandidatePageRead(true, 0, uint.MaxValue, 0, 0,
                Array.Empty<string>(), null);
        }

        // 上界之外的 count 视为元数据损坏——绝不按“截断的 count”伪造一个貌似合理的页。
        if (count > HostDrawnCandidatePresenter.MaxCandidateCount)
        {
            return CandidatePageRead.Failed(
                $"GetCount out of range (count={count} max={HostDrawnCandidatePresenter.MaxCandidateCount})");
        }

        var hasSelection = true;
        uint selection = 0;
        hr = ops.GetSelection(out selection);
        if (hr < 0)
        {
            return CandidatePageRead.Failed($"GetSelection {TsfHResult.Describe(hr)}");
        }

        if (hr == TsfHResult.S_FALSE)
        {
            // 合约语义：无选择——不伪造选中项。
            hasSelection = false;
            selection = uint.MaxValue;
        }
        else if (hr != TsfHResult.S_OK)
        {
            // S_OK/S_FALSE 之外的正 HRESULT：不得把 out 值当成真实选中。
            return CandidatePageRead.Failed($"GetSelection {TsfHResult.Describe(hr)}");
        }
        else if (!CandidateSnapshot.SelectionWithinCount(selection, count))
        {
            return CandidatePageRead.Failed(
                $"GetSelection out of range (sel={selection} count={count})");
        }

        if (Ended(ended))
        {
            return CandidatePageRead.Failed("source ended during read");
        }

        hr = ops.GetCurrentPage(out var currentPage);
        if (hr != TsfHResult.S_OK)
        {
            // 页元数据读取失败 = 明确失败；绝不回退第一页当真实页。
            return CandidatePageRead.Failed($"GetCurrentPage {TsfHResult.Describe(hr)}");
        }

        if (Ended(ended))
        {
            return CandidatePageRead.Failed("source ended during read");
        }

        // 两趟 GetPageIndex：先计数再取表。任一趟失败都是元数据失败。
        hr = ops.GetPageIndex(IntPtr.Zero, 0, out var pageCount);
        if (hr != TsfHResult.S_OK)
        {
            return CandidatePageRead.Failed($"GetPageIndex(count) {TsfHResult.Describe(hr)}");
        }

        if (Ended(ended))
        {
            return CandidatePageRead.Failed("source ended during read");
        }

        var range = ReadPageRange(ops, count, pageCount, currentPage, ended,
            alloc, free, out var pageStart, out var failure);
        if (failure is not null)
        {
            return CandidatePageRead.Failed(failure);
        }

        var pageEnd = range;
        var pageCapacity = (int)Math.Min(pageEnd - pageStart,
            (uint)HostDrawnCandidatePresenter.MaxPageEntries);
        var entries = new List<string>(pageCapacity);
        for (var index = pageStart; index < pageEnd
            && entries.Count < HostDrawnCandidatePresenter.MaxPageEntries; index++)
        {
            var sHr = ops.GetString(index, out var text);
            if (sHr != TsfHResult.S_OK)
            {
                return CandidatePageRead.Failed(
                    $"GetString({index}) {TsfHResult.Describe(sHr)}");
            }

            entries.Add(text ?? string.Empty);

            if (Ended(ended))
            {
                return CandidatePageRead.Failed("source ended during read");
            }
        }

        return new CandidatePageRead(true, count,
            hasSelection ? selection : uint.MaxValue, pageStart, currentPage,
            entries, null);
    }

    /// <summary>
    /// 读页起点表并求当前页区间 [pageStart, pageEnd)。严格校验：页数在界内、
    /// currentPage 有效、起点落在 [0,count)、严格递增且首页为 0。失败写入
    /// <paramref name="failure"/> 并返回 0；绝不返回“貌似合理”的截断页。
    /// </summary>
    private static uint ReadPageRange(ICandidateListOperations ops, uint count,
        uint pageCount, uint currentPage, Func<bool> ended,
        Func<int, IntPtr> alloc, Action<IntPtr> free,
        out uint pageStart, out string? failure)
    {
        pageStart = 0;
        failure = null;

        if (pageCount == 0 || pageCount > HostDrawnCandidatePresenter.MaxPageCount)
        {
            failure = $"GetPageIndex count out of range (pages={pageCount} "
                + $"max={HostDrawnCandidatePresenter.MaxPageCount})";
            return 0;
        }

        if (currentPage >= pageCount)
        {
            failure = $"GetCurrentPage out of range (page={currentPage} pages={pageCount})";
            return 0;
        }

        var size = (int)pageCount;
        var buffer = alloc(size * sizeof(uint));
        uint filled;
        uint[] table;
        try
        {
            // 一个覆盖查询与拷贝的 try/finally：无论 GetPageIndex 失败、
            // 抛异常还是拷贝中途出错，buffer 在任何出口都恰好释放一次。
            var hr = ops.GetPageIndex(buffer, (uint)size, out filled);
            if (hr != TsfHResult.S_OK)
            {
                failure = $"GetPageIndex(entries) {TsfHResult.Describe(hr)}";
                return 0;
            }

            table = new uint[size];
            for (var i = 0; i < size; i++)
            {
                table[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            }
        }
        finally
        {
            free(buffer);
        }

        if (Ended(ended))
        {
            failure = "source ended during read";
            return 0;
        }

        if (filled != pageCount)
        {
            failure = $"GetPageIndex count mismatch (declared={pageCount} filled={filled})";
            return 0;
        }

        // 页表单调性 + 范围校验：首页必须为 0、每项 < count、严格递增。
        for (var i = 0; i < size; i++)
        {
            var start = table[i];
            if (start >= count)
            {
                failure = $"page[{i}] start out of range (start={start} count={count})";
                return 0;
            }

            if (i == 0 && start != 0)
            {
                failure = $"page[0] start not zero (start={start})";
                return 0;
            }

            if (i > 0 && start <= table[i - 1])
            {
                failure = $"page[{i}] not strictly increasing (prev={table[i - 1]} start={start})";
                return 0;
            }
        }

        pageStart = table[(int)currentPage];
        // 末页终点为 count；其后无“下一页起点”。
        return currentPage + 1 < pageCount ? table[(int)currentPage + 1] : count;
    }

    private static bool Ended(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            // 判定失败按“已结束”处理——fail closed。
            return true;
        }
    }
}

/// <summary>真实的 <see cref="ICandidateSnapshotSource"/>：调用方保证本元素仍存活。</summary>
internal sealed class ComCandidateSnapshotSource : ICandidateSnapshotSource
{
    private readonly ITfCandidateListUIElement? _candidate;

    internal ComCandidateSnapshotSource(ITfUIElement? element)
        => _candidate = element as ITfCandidateListUIElement;

    /// <summary>测试注入：直接给定脚本化操作集（仍走同一 <see cref="CandidatePageReader"/>）。</summary>
    private readonly ICandidateListOperations? _opsOverride;

    internal ComCandidateSnapshotSource(ICandidateListOperations ops) => _opsOverride = ops;

    public bool IsCandidateList => _candidate is not null || _opsOverride is not null;

    public CandidatePageRead ReadPage(Func<bool> sourceEnded)
    {
        var ops = _opsOverride
            ?? (_candidate is { } c ? new ComCandidateListOperations(c) : null);
        if (ops is null)
        {
            return CandidatePageRead.Failed("element is not a candidate list");
        }

        try
        {
            return CandidatePageReader.Run(ops, sourceEnded);
        }
        catch (Exception ex)
        {
            // 已释放/失效的 RCW 或任何回调内异常：如实判失败，绝不把异常逃出 sink 回调。
            return CandidatePageRead.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// 呈现器一次发布尝试的真实结果：Executed 表示真的发起了原生快照查询
/// （发布成功与否看 Snapshot.Valid）；Skipped 表示在发布<b>前</b>被拦下
/// （元素已结束/来源不可用/代数被替换），此时绝不查询也绝不改动已展示快照。
/// </summary>
internal readonly record struct CandidatePublishOutcome(
    bool Executed, CandidateSnapshot? Snapshot, string? Reason)
{
    internal static CandidatePublishOutcome ExecutedResult(CandidateSnapshot snapshot)
        => new(true, snapshot, null);

    internal static CandidatePublishOutcome Skipped(string reason)
        => new(false, null, reason);
}
