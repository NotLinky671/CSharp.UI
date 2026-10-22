using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// TI-07 P3b 共享契约：候选快照与有界读取的安全上限。
/// </summary>
/// <remarks>
/// <para>
/// 本文件是 A（候选快照/读取/ABI）与 B（候选 overlay 布局/绘制）之间的<b>唯一</b>共享契约：
/// B 只消费 <see cref="CandidateSnapshot"/>，绝不直接调用 COM、绝不读取
/// <c>ITfUIElement</c>/<c>ITfCandidateListUIElement</c>。几何输入（客户区/锚点/字体/颜色/
/// pixelScale）属于 overlay 自身入口，不进入候选快照；屏幕物理像素到逻辑坐标的转换由后续
/// bridge/host 集成完成。
/// </para>
/// <para>
/// <b>总候选数没有 256 硬上限：</b><see cref="CandidatePageReader"/> 只为<b>当前页</b>分配条目，
/// 读取量由页区间与 <see cref="MaxPageEntries"/> 共同约束；只需页起点表在
/// <see cref="MaxPageCount"/> 之内。因此 <c>GetCount</c> 大于 256 的合法列表仍可读取。
/// </para>
/// </remarks>
internal static class CandidateSnapshotLimits
{
    /// <summary>单页最多读取/绘制的条目数（一页真的画不下更多；超出部分按页边界截断）。</summary>
    internal const int MaxPageEntries = 32;

    /// <summary>页起点表最大项数（两趟 <c>GetPageIndex</c> 的 buffer 上界，不是候选总数上界）。</summary>
    internal const uint MaxPageCount = 1024;

    /// <summary>单条候选文本的最大 UTF-16 码元数（截断时绝不拆开代理对）。</summary>
    internal const int MaxCandidateTextLength = 128;
}

/// <summary>快照内一条候选：真实 TSF 序号 + 原始文本（绝不改编号语义）。</summary>
internal readonly record struct CandidateSnapshotEntry(uint Index, string Text);

/// <summary>
/// 一次宿主自绘候选的<b>有界、不可变</b>显示快照。
/// </summary>
/// <remarks>
/// <para>
/// 元素 id（<c>0</c> 合法）、单调代数、当前页的候选（真实序号 + 文本）、选中项
/// （页内索引 + 真实序号）、页元数据与有效/失败状态全部在构建时冻结。发布之后任何后续
/// COM 变化都不会改写已展示内容。
/// </para>
/// <para>
/// <b>失效语义：</b><see cref="Valid"/> 为 false 或 <see cref="Entries"/> 为空时必须隐藏候选；
/// 元素 End/同 id 新 Begin/焦点或 epoch 变化由呈现生命周期清空并递增代数，
/// 旧代数快照不得再被 overlay 接受。
/// </para>
/// </remarks>
internal sealed class CandidateSnapshot
{
    internal CandidateSnapshot(uint elementId, uint generation,
        IReadOnlyList<CandidateSnapshotEntry> entries,
        int selectionOnPage, uint selectionIndex, uint totalCount, uint pageStart,
        uint currentPage, bool valid, string? failure)
        : this(elementId, generation, entries, selectionOnPage, selectionIndex,
            totalCount, pageStart, currentPage, valid, failure,
            takeEntriesOwnership: false)
    {
    }

    /// <param name="takeEntriesOwnership">
    /// 为 true 时要求 <paramref name="entries"/> 是构造方<b>不再共享</b>的私有数组，直接只读封装而不复制；
    /// 内部读取路径用它避免“构建数组 + 快照再复制一次”。外部调用保持默认的防御性复制。
    /// </param>
    internal CandidateSnapshot(uint elementId, uint generation,
        IReadOnlyList<CandidateSnapshotEntry> entries,
        int selectionOnPage, uint selectionIndex, uint totalCount, uint pageStart,
        uint currentPage, bool valid, string? failure,
        bool takeEntriesOwnership)
    {
        ElementId = elementId;
        Generation = generation;
        // 防御性复制 + 只读封装：调用方持有的 List 若随后被改写，绝不能污染已发布快照。
        var source = entries ?? Array.Empty<CandidateSnapshotEntry>();
        var owned = takeEntriesOwnership && source is CandidateSnapshotEntry[]
            ? (CandidateSnapshotEntry[])source
            : source.ToArray();
        Entries = new ReadOnlyCollection<CandidateSnapshotEntry>(owned);
        SelectionOnPage = selectionOnPage;
        SelectionIndex = selectionIndex;
        TotalCount = totalCount;
        PageStart = pageStart;
        CurrentPage = currentPage;
        Valid = valid;
        Failure = failure;
    }

    /// <summary>TSF UIElement id；<c>0</c> 是合法 id。</summary>
    internal uint ElementId { get; }

    /// <summary>呈现生命周期分配的单调代数（元素注册序）；恢复时据此判定“同一个元素”。</summary>
    internal uint Generation { get; }

    /// <summary>当前页的候选条目（真实序号 + 文本），只读防御复制，已按上界截断。</summary>
    internal IReadOnlyList<CandidateSnapshotEntry> Entries { get; }

    /// <summary>选中项在 <see cref="Entries"/> 内的页内索引；无选中或不在本页为 -1。</summary>
    internal int SelectionOnPage { get; }

    /// <summary>选中项的真实候选序号；无选中为 <see cref="uint.MaxValue"/>。</summary>
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

    /// <summary>该快照是否真实含候选（overlay 据此拒绝“空快照冒充有候选”）。</summary>
    internal bool HasCandidates => Valid && Entries.Count > 0;

    /// <summary>选中序号是否落在候选总数内（托管可测的一致性判定）。</summary>
    internal static bool SelectionWithinCount(uint selection, uint count)
        => selection < count;

    /// <summary>把真实选中序号映射到当前页内的可见索引；不在本页返回 -1。</summary>
    internal static int PageLocalSelection(uint selectionIndex, uint pageStart, int entryCount)
        => entryCount > 0
            && selectionIndex >= pageStart
            && selectionIndex - pageStart < (uint)entryCount
            ? (int)(selectionIndex - pageStart)
            : -1;

    internal string Describe()
        => $"id={ElementId} gen={Generation} valid={Valid} count={TotalCount} "
            + $"page={CurrentPage} start={PageStart} sel={SelectionIndex} "
            + $"sel_on_page={SelectionOnPage} entries={Entries.Count}"
            + (Valid ? string.Empty : $" failure={Failure ?? "(unknown)"}");
}

/// <summary>
/// 候选来源的一次查询结果；失败时 <see cref="Failure"/> 必须给出原因，
/// <see cref="Entries"/> 为空——绝不把部分结果冒充成功。
/// </summary>
internal readonly record struct CandidatePageRead(
    bool Ok, uint Count, uint Selection, uint PageStart, uint CurrentPage,
    IReadOnlyList<string> Entries, string? Failure)
{
    internal static CandidatePageRead Failed(string reason)
        => new(false, 0, 0, 0, uint.MaxValue, Array.Empty<string>(), reason);
}

/// <summary>
/// 原生候选查询的<b>逐操作适配层</b>：生产实现逐方法转发
/// <c>ITfCandidateListUIElement</c>；测试注入脚本化操作，驱动与生产完全相同的
/// <see cref="CandidatePageReader.Run"/> 控制流（含逐调用 End 判定）。
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

/// <summary>生产适配器：把每个操作直接转发给真实 <c>ITfCandidateListUIElement</c>。</summary>
internal sealed class ComCandidateListOperations : ICandidateListOperations
{
    private readonly ITfCandidateListUIElement _candidate;

    internal ComCandidateListOperations(ITfCandidateListUIElement candidate) => _candidate = candidate;

    public int GetCount(out uint count) => _candidate.GetCount(out count);

    public int GetSelection(out uint index) => _candidate.GetSelection(out index);

    public int GetString(uint index, out string? text) => _candidate.GetString(index, out text);

    public int GetPageIndex(IntPtr indices, uint size, out uint pageCount)
        => _candidate.GetPageIndex(indices, size, out pageCount);

    public int GetCurrentPage(out uint page)
        => _candidate.GetCurrentPage(out page);
}

/// <summary>
/// 整页读取的<b>唯一控制流</b>——生产与测试共用。约束：
/// <list type="bullet">
/// <item>每次原生查询<b>前后</b>都复核 <c>ended</c>；一旦为 true 立即中断，
/// 绝不对已 End 元素继续调用。</item>
/// <item>除 <c>GetSelection</c> 的 <c>S_FALSE</c>（无选择）与 <c>GetCount</c>==0 的合约语义外，
/// 任何 <c>hr != S_OK</c> 都是失败；S_FALSE 之外的正 HRESULT 不得伪造数据。</item>
/// <item>页边界严格：当前页 = <c>[pageStart[currentPage], pageStart[currentPage+1])</c>，
/// 末页以 totalCount 为终点；页表必须在 [0,count) 内严格单调递增且 page[0]==0，
/// 元数据失败即整体 Failed——绝不回退“猜第一页”。</item>
/// <item>总候选数不设 256 硬上限；只为当前页分配条目，条目数受页区间 ∩
/// <see cref="CandidateSnapshotLimits.MaxPageEntries"/> 双重上界。</item>
/// <item>原生 buffer 用 try/finally 释放；任何出口恰好释放一次。</item>
/// </list>
/// </summary>
internal static class CandidatePageReader
{
    /// <summary>执行一次完整页查询；<paramref name="ended"/> 在每次原生调用前后复核。</summary>
    internal static CandidatePageRead Run(ICandidateListOperations ops, Func<bool> ended)
        => Run(ops, ended, Marshal.AllocHGlobal, Marshal.FreeHGlobal);

    /// <summary>
    /// 同上，但原生 buffer 的分配/释放可注入（测试用计数实现验证配平）——
    /// 控制流与生产完全一致。
    /// </summary>
    internal static CandidatePageRead Run(ICandidateListOperations ops, Func<bool> ended,
        Func<int, IntPtr> alloc, Action<IntPtr> free)
    {
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

        var pageEnd = ReadPageRange(ops, count, pageCount, currentPage, ended,
            alloc, free, out var pageStart, out var failure);
        if (failure is not null)
        {
            return CandidatePageRead.Failed(failure);
        }

        var entryCount = (int)Math.Min(pageEnd - pageStart,
            (uint)CandidateSnapshotLimits.MaxPageEntries);
        var entries = new string[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            var index = pageStart + (uint)i;
            var sHr = ops.GetString(index, out var text);
            if (sHr != TsfHResult.S_OK)
            {
                return CandidatePageRead.Failed(
                    $"GetString({index}) {TsfHResult.Describe(sHr)}");
            }

            entries[i] = TruncateCandidateText(text);

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
    /// 读页起点表并求当前页区间 [pageStart, pageEnd)。页数、currentPage、
    /// 页表（首页为 0、落在 [0,count)、严格递增）任一不合法即整体失败；
    /// 绝不回退“猜第一页”。
    /// </summary>
    private static uint ReadPageRange(ICandidateListOperations ops, uint count,
        uint pageCount, uint currentPage, Func<bool> ended,
        Func<int, IntPtr> alloc, Action<IntPtr> free,
        out uint pageStart, out string? failure)
    {
        pageStart = 0;
        failure = null;

        if (pageCount == 0 || pageCount > CandidateSnapshotLimits.MaxPageCount)
        {
            failure = $"GetPageIndex count out of range (pages={pageCount} "
                + $"max={CandidateSnapshotLimits.MaxPageCount})";
            return 0;
        }

        if (currentPage >= pageCount)
        {
            failure = $"GetCurrentPage out of range (page={currentPage} pages={pageCount})";
            return 0;
        }

        var size = (int)pageCount;
        var buffer = alloc(size * sizeof(uint));
        try
        {
            var hr = ops.GetPageIndex(buffer, (uint)size, out var filled);
            if (hr != TsfHResult.S_OK)
            {
                failure = $"GetPageIndex(entries) {TsfHResult.Describe(hr)}";
                return 0;
            }

            // 先验证实际填充数，绝不读取未填充的内存。
            if (filled != pageCount)
            {
                failure = $"GetPageIndex count mismatch (declared={pageCount} filled={filled})";
                return 0;
            }

            // 直接校验原生缓冲，不为中间页表再做一份托管数组。
            var currentStart = 0u;
            var nextStart = 0u;
            var previous = 0u;
            for (var i = 0; i < size; i++)
            {
                var start = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
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

                if (i > 0 && start <= previous)
                {
                    failure = $"page[{i}] not strictly increasing (prev={previous} start={start})";
                    return 0;
                }

                previous = start;
                if ((uint)i == currentPage)
                {
                    currentStart = start;
                }
                else if ((uint)i == currentPage + 1)
                {
                    nextStart = start;
                }
            }

            if (Ended(ended))
            {
                failure = "source ended during read";
                return 0;
            }

            pageStart = currentStart;
            // 末页终点为 count；其后无“下一页起点”。
            return currentPage + 1 < pageCount ? nextStart : count;
        }
        finally
        {
            free(buffer);
        }
    }

    /// <summary>
    /// 截断到 <see cref="CandidateSnapshotLimits.MaxCandidateTextLength"/> 个 UTF-16 码元；
    /// 截断点落在代理对中间时回退一个码元，绝不产生半个代理。
    /// </summary>
    internal static string TruncateCandidateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= CandidateSnapshotLimits.MaxCandidateTextLength)
        {
            return text;
        }

        var length = CandidateSnapshotLimits.MaxCandidateTextLength;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return text[..length];
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

/// <summary>
/// 从“存活的候选元素”读取一页候选的<b>可注入来源</b>。呈现器只依赖本接口，
/// COM 细节（vtable、异常、已释放 RCW）全部留在实现内；任何一步失败都返回
/// <see cref="CandidatePageRead.Failed"/>。
/// </summary>
internal interface ICandidateSnapshotSource
{
    /// <summary>元素是否仍是候选列表类型（QI 判定，不依赖候选内容已就绪）。</summary>
    bool IsCandidateList { get; }

    /// <summary>
    /// 读取当前页候选；<paramref name="sourceEnded"/> 在每次原生查询前后复核——
    /// 返回 true 时立即停止，绝不对已 End/已替换的元素再发任何 COM 调用。
    /// </summary>
    CandidatePageRead ReadPage(Func<bool> sourceEnded);
}

/// <summary>真实的 <see cref="ICandidateSnapshotSource"/>：调用方保证本元素仍存活。</summary>
internal sealed class ComCandidateSnapshotSource : ICandidateSnapshotSource
{
    private readonly ITfCandidateListUIElement? _candidate;
    private readonly ICandidateListOperations? _opsOverride;

    internal ComCandidateSnapshotSource(ITfUIElement? element)
        => _candidate = element as ITfCandidateListUIElement;

    /// <summary>测试注入：直接给定脚本化操作集（仍走同一 <see cref="CandidatePageReader"/>）。</summary>
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
            // 已释放/失效的 RCW 或任何回调内异常：如实判失败，
            // 绝不把异常逃出 sink 回调。
            return CandidatePageRead.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
