using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

internal static class ComNative
{
    internal const uint CoInitMultithreaded = 0x0;
    internal const uint CoInitApartmentThreaded = 0x2;

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();
}

/// <summary>应用侧编辑会话：唯一写入入口，必须在 msctf 授予的写锁内执行。</summary>
internal sealed class AppEditSession : ITfEditSession
{
    private readonly AcpTextStore _store;
    private readonly Func<AcpTextStore, int> _body;
    private readonly TsfTrace _trace;

    internal AppEditSession(AcpTextStore store, Func<AcpTextStore, int> body, TsfTrace trace)
    {
        _store = store;
        _body = body;
        _trace = trace;
    }

    public int DoEditSession(uint ec)
    {
        try
        {
            var hr = _body(_store);
            _trace("edit-session", "do-edit-session", new Dictionary<string, object?>
            {
                ["ec"] = ec,
                ["hresult"] = TsfHResult.Describe(hr),
                ["write_lock"] = _store.Locks.IsWriteLocked,
            });
            return hr;
        }
        catch (Exception ex)
        {
            _trace("edit-session", "exception", new Dictionary<string, object?>
            {
                ["ec"] = ec,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return TsfHResult.E_FAIL;
        }
    }
}

/// <summary>跟踪 TIP 驱动的组合：开始/更新/结束，并把最终文本交给 store 做一次性归属。</summary>
internal sealed class TsfCompositionObserver : ITfContextOwnerCompositionSink
{
    private readonly AcpTextStore _store;
    private readonly TsfTrace _trace;
    private readonly Action _requestRepaint;

    internal TsfCompositionObserver(AcpTextStore store, TsfTrace trace, Action requestRepaint)
    {
        _store = store;
        _trace = trace;
        _requestRepaint = requestRepaint;
    }

    public int OnStartComposition(ITfCompositionView pComposition, out int pfOk)
    {
        pfOk = 1;
        try
        {
            if (TryGetExtent(pComposition, out var anchor, out var length, out var detail))
            {
                _store.BeginComposition(anchor, length);
            }
            else
            {
                _store.BeginComposition(_store.Document.Selection.Start, 0);
            }

            _trace("composition", "on-start", new Dictionary<string, object?>
            {
                ["anchor"] = anchor,
                ["length"] = length,
                ["extent"] = detail,
            });
            _requestRepaint();
        }
        catch (Exception ex)
        {
            pfOk = 0;
            _trace("composition", "on-start-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    public int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew)
    {
        try
        {
            var got = false;
            var anchor = 0;
            var length = 0;
            var detail = "(none)";

            // pRangeNew 是新组合范围的权威来源；为空时才回退到 composition view 的当前范围。
            if (pRangeNew != IntPtr.Zero)
            {
                got = TryGetRangeAcpExtent(pRangeNew, out anchor, out length, out detail);
            }

            if (!got)
            {
                got = TryGetExtent(pComposition, out anchor, out length, out detail);
            }

            if (got)
            {
                _store.UpdateComposition(anchor, length);
            }

            _trace("composition", "on-update", new Dictionary<string, object?>
            {
                ["anchor"] = anchor,
                ["length"] = length,
                ["extent"] = detail,
                ["range_new"] = pRangeNew == IntPtr.Zero ? "(null)" : $"0x{pRangeNew.ToInt64():X}",
                ["used_range_new"] = got && pRangeNew != IntPtr.Zero,
            });
            _requestRepaint();
        }
        catch (Exception ex)
        {
            _trace("composition", "on-update-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    public int OnEndComposition(ITfCompositionView pComposition)
    {
        try
        {
            var committed = _store.EndComposition();
            _trace("composition", "on-end", new Dictionary<string, object?>
            {
                ["committed_utf16_units"] = committed.Length,
                ["committed"] = ImeFormat.EscapeUtf16(committed),
            });
            _requestRepaint();
        }
        catch (Exception ex)
        {
            _trace("composition", "on-end-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    private static bool TryGetExtent(ITfCompositionView view, out int anchor, out int length, out string detail)
    {
        anchor = 0;
        length = 0;
        detail = "(none)";
        IntPtr range = IntPtr.Zero;
        try
        {
            var hr = view.GetRange(out range);
            if (hr < 0 || range == IntPtr.Zero)
            {
                detail = $"GetRange hr={TsfHResult.Describe(hr)}";
                return false;
            }

            return TryGetRangeAcpExtent(range, out anchor, out length, out detail);
        }
        catch (Exception ex)
        {
            detail = $"exception {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (range != IntPtr.Zero)
            {
                Marshal.Release(range);
            }
        }
    }

    private static bool TryGetRangeAcpExtent(IntPtr range, out int anchor, out int length, out string detail)
    {
        anchor = 0;
        length = 0;
        detail = "(none)";
        IntPtr acpRange = IntPtr.Zero;
        try
        {
            var iid = TsfIids.RangeAcp;
            var qi = Marshal.QueryInterface(range, in iid, out acpRange);
            if (qi < 0 || acpRange == IntPtr.Zero)
            {
                detail = $"QI(ITfRangeACP) hr={TsfHResult.Describe(qi)}";
                return false;
            }

            var extentHr = TsfNative.GetRangeAcpExtent(acpRange, out anchor, out length);
            detail = $"GetExtent hr={TsfHResult.Describe(extentHr)} anchor={anchor} cch={length}";
            return extentHr >= 0;
        }
        catch (Exception ex)
        {
            detail = $"exception {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (acpRange != IntPtr.Zero)
            {
                Marshal.Release(acpRange);
            }
        }
    }
}

/// <summary>
/// 观察 TIP 的 UIElement（候选列表）生命周期。默认不干预显示；显式实验可拒绝显示或调用 Show(FALSE)。
/// API 成功不代表候选真的隐藏，必须由截图核对。
/// </summary>
internal sealed class TsfUiElementObserver : ITfUIElementSink, IDisposable
{
    private readonly ITfUIElementMgr? _manager;
    private readonly TsfTrace _trace;
    private readonly object _gate = new();
    private readonly Dictionary<uint, ElementInfo> _elements = new();

    internal TsfUiElementObserver(ITfUIElementMgr? manager, TsfTrace trace)
    {
        _manager = manager;
        _trace = trace;
    }

    internal bool SuppressCandidates { get; set; }

    /// <summary>R34 opt-in：宿主自绘候选呈现器（默认 null = 不接呈现逻辑，
    /// observer 行为与 R27 完全一致）。只接受非 null 一次注入；注入后
    /// Begin/Update/End 回调把元素视图同步给同一呈现器。</summary>
    internal HostDrawnCandidatePresenter? HostDrawnPresenter { get; set; }

    internal int BeginCount { get; private set; }

    internal int UpdateCount { get; private set; }

    internal int EndCount { get; private set; }

    internal int CandidateCount
    {
        get
        {
            lock (_gate)
            {
                return _elements.Count;
            }
        }
    }

    public int BeginUIElement(uint dwUIElementId, ref int pbShow)
    {
        try
        {
            BeginCount++;
            var incoming = pbShow;
            var info = Describe(dwUIElementId);
            ElementInfo? displaced = null;
            lock (_gate)
            {
                // 同 id 重复 Begin：先让被替换的旧实例走 End（幂等、借用安全），
                // 不得直接覆盖引用而泄漏/孤立旧 RCW。
                if (_elements.TryGetValue(dwUIElementId, out var old))
                {
                    displaced = old;
                }

                _elements[dwUIElementId] = info;
            }

            displaced?.MarkEnded();

            // 官方 in/out：应用未自绘候选时必须明确决定。默认 TRUE；只有显式抑制且确属候选元素才 FALSE。
            var suppress = SuppressCandidates && info.IsCandidateList;
            pbShow = suppress ? 0 : 1;

            // R34：仅候选元素接入呈现器；类型判定看 QI（候选内容无需就绪），
            // 代数由呈现器分配并回写到本 ElementInfo 供 Update/End 复核。非候选为 0、绝不注册。
            if (HostDrawnPresenter is { } presenter)
            {
                var generation = presenter.BeginElement(
                    new HostDrawnCandidatePresenter.ElementView(
                        dwUIElementId, info.SnapshotSource));
                info.HostDrawnGeneration = generation;
                _trace("uielement", "host-drawn-begin-view", new Dictionary<string, object?>
                {
                    ["id"] = dwUIElementId,
                    ["generation"] = generation,
                    ["is_candidate_list"] = info.IsCandidateList,
                });
            }

            _trace("uielement", "begin", new Dictionary<string, object?>
            {
                ["id"] = dwUIElementId,
                ["show_incoming"] = incoming,
                ["show_outgoing"] = pbShow,
                ["suppressed"] = suppress,
                ["description"] = info.Description,
                ["guid"] = info.Guid,
                ["is_candidate_list"] = info.IsCandidateList,
                ["candidate_count"] = info.CandidateCount,
                ["selection"] = info.Selection,
                ["first_candidates"] = info.FirstCandidates,
            });
        }
        catch (Exception ex)
        {
            _trace("uielement", "begin-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    public int UpdateUIElement(uint dwUIElementId)
    {
        try
        {
            UpdateCount++;
            ElementInfo? info;
            lock (_gate)
            {
                _elements.TryGetValue(dwUIElementId, out info);
            }

            info?.Refresh();
            _trace("uielement", "update", new Dictionary<string, object?>
            {
                ["id"] = dwUIElementId,
                ["description"] = info?.Description,
                ["candidate_count"] = info?.CandidateCount,
                ["selection"] = info?.Selection,
            });

            // R34：对存活候选按注入源读当前页并发布到 overlay；未追踪/非候选元素不发布。
            // 整个查询在 ElementInfo 借用内执行——End 在借用期间只置死亡标记，
            // 读取核心逐次 COM 调用复核该标记；RCW 释放推迟到借用退出的 finally。
            if (HostDrawnPresenter is { } presenter && info is { IsCandidateList: true })
            {
                info.ExecuteRead(() =>
                {
                    var generation = info.HostDrawnGeneration;
                    var source = presenter.SourceOf(dwUIElementId, generation);
                    if (source is not null)
                    {
                        presenter.PublishUpdate(dwUIElementId, generation, source,
                            () => info.Ended);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _trace("uielement", "update-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    public int EndUIElement(uint dwUIElementId)
    {
        try
        {
            EndCount++;
            ElementInfo? info = null;
            lock (_gate)
            {
                if (_elements.Remove(dwUIElementId, out var removed))
                {
                    info = removed;
                }
            }

            // 若该元素的 RCW 正被一次 Show 借用，MarkEnded 只置死亡标记并把
            // 释放推迟到借用退出的 finally；绝不就地 ReleaseComObject 在用 RCW。
            info?.MarkEnded();
            _trace("uielement", "end", new Dictionary<string, object?>
            {
                ["id"] = dwUIElementId,
                ["description"] = info?.Description,
            });

            // R34：把被 End 的代数传给呈现器——移除视图 + 立即清屏（若它正是当前来源）。
            if (HostDrawnPresenter is { } presenter && info is not null)
            {
                presenter.EndElement(dwUIElementId, info.HostDrawnGeneration, "end-ui-element");
            }
        }
        catch (Exception ex)
        {
            _trace("uielement", "end-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        return TsfHResult.S_OK;
    }

    /// <summary>
    /// 对当前存活的**候选**元素调用 <c>ITfUIElement::Show</c>；返回每个元素的前后 IsShown 与 HRESULT。
    /// <para>
    /// R27：必须过滤 <see cref="ElementInfo.IsCandidateList"/>——不得对语言栏等非候选元素
    /// 调用 Show，否则会干预与候选无关的 TIP UI。
    /// </para>
    /// </summary>
    internal IReadOnlyList<UiElementShowResult> TryShowCandidates(bool show)
    {
        var results = new List<UiElementShowResult>();
        List<ElementInfo> snapshot;
        lock (_gate)
        {
            // 只保留候选元素；非候选（语言栏、阅读窗口等）不参与。
            snapshot = _elements.Values.Where(e => e.IsCandidateList).ToList();
        }

        foreach (var info in snapshot)
        {
            var result = info.TryShow(show);
            results.Add(result);
            _trace("uielement", "show-probe", new Dictionary<string, object?>
            {
                ["id"] = result.Id,
                ["requested_show"] = show,
                ["is_candidate_list"] = result.IsCandidateList,
                ["shown_before"] = result.ShownBefore,
                ["shown_after"] = result.ShownAfter,
                ["hresult"] = TsfHResult.Describe(result.HResult),
                ["hr_available"] = result.ShowHrAvailable,
                ["ended_during_show"] = result.EndedDuringShow,
                ["shown_before_hr"] = result.IsShownBeforeHr,
                ["shown_after_hr"] = result.IsShownAfterHr,
                ["exception_type"] = result.ShowExceptionType,
                ["exception_stage"] = result.ShowExceptionStage,
            });
        }

        return results;
    }

    /// <summary>
    /// R27：当前存活的候选元素快照（只含 <see cref="ElementInfo.IsCandidateList"/>）。
    /// 返回的是 observer 持有的同一 <see cref="ElementInfo"/>；调用方**不得**释放它，
    /// 元素一旦 End 即被 observer 移除并释放，因此调用前应先用 <see cref="IsLive"/> 复核。
    /// </summary>
    internal IReadOnlyList<ElementInfo> LiveCandidates()
    {
        lock (_gate)
        {
            return _elements.Values.Where(e => e.IsCandidateList).ToList();
        }
    }

    /// <summary>该元素是否仍存活（未被 End 移除）。End 之后的元素不得再使用。</summary>
    internal bool IsLive(ElementInfo info)
    {
        lock (_gate)
        {
            return _elements.TryGetValue(info.Id, out var current) && ReferenceEquals(current, info);
        }
    }

    /// <summary>当前存活元素总数（含非候选），用于日志区分“Begin 累计”与“真实存活”。</summary>
    internal int LiveElementCount
    {
        get
        {
            lock (_gate)
            {
                return _elements.Count;
            }
        }
    }

    private ElementInfo Describe(uint id)
    {
        if (_manager is null)
        {
            return new ElementInfo(id, null);
        }

        var hr = _manager.GetUIElement(id, out var element);
        if (hr < 0 || element is null)
        {
            _trace("uielement", "get-element-failed", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["hresult"] = TsfHResult.Describe(hr),
            });
            return new ElementInfo(id, null);
        }

        return new ElementInfo(id, element);
    }

    public void Dispose()
    {
        List<ElementInfo> snapshot;
        lock (_gate)
        {
            snapshot = _elements.Values.ToList();
            _elements.Clear();
        }

        foreach (var info in snapshot)
        {
            info.Dispose();
        }

        // R34：会话释放即清屏——overlay 不得保留任何旧候选。
        HostDrawnPresenter?.OnFocusLost("observer-disposed");
    }
}

/// <summary>
/// 一次 Show 探测的结构化结果。<see cref="HResult"/> 是原生 <c>ITfUIElement::Show</c>
/// **实际返回**的 HRESULT；仅当 <see cref="ShowHrAvailable"/> 为 true 时它才代表系统应答。
/// 为 false 表示无可用的原生返回 HRESULT——元素在调用前已 End，或调用在返回一个可用
/// HRESULT 之前抛出；**不能据此推断系统从未执行 Show**。此时
/// <see cref="ShowExceptionType"/> 记录异常详情，不得把该值解释为“系统返回失败”。
/// </summary>
internal readonly record struct UiElementShowResult(
    uint Id, bool IsCandidateList, int HResult, bool? ShownBefore, bool? ShownAfter)
{
    /// <summary><see cref="HResult"/> 是否为 Show 的真实返回值（false = 原生 HRESULT 不可用）。</summary>
    internal bool ShowHrAvailable { get; init; }

    /// <summary>整个调用期间捕获的异常类型名（无异常为 null）。</summary>
    internal string? ShowExceptionType { get; init; }

    /// <summary>异常消息（无异常为 null）。</summary>
    internal string? ShowExceptionMessage { get; init; }

    /// <summary>异常发生的阶段（"shown-before"|"show"|"shown-after"；无异常为 null）。</summary>
    internal string? ShowExceptionStage { get; init; }

    /// <summary>IsShown(before) 的 HRESULT；未执行/抛异常为 null。</summary>
    internal int? IsShownBeforeHr { get; init; }

    /// <summary>IsShown(after) 的 HRESULT；被跳过（End）或未执行为 null。</summary>
    internal int? IsShownAfterHr { get; init; }

    /// <summary>本次 Show 调用期间是否发生了同步 End（借用中的 RCW 延迟释放）。</summary>
    internal bool EndedDuringShow { get; init; }
}

internal sealed class ElementInfo : IDisposable
{
    private readonly ITfUIElement? _element;
    private ITfCandidateListUIElement? _candidate;

    /// <summary>EndUIElement（或 Dispose）之后为 true：此后禁止任何 Show/IsShown/Refresh。</summary>
    private bool _ended;

    /// <summary>借用计数：原生 Show 调用进行中。End 若发生在借用期间，只标记死亡、延迟释放。</summary>
    private int _inFlight;

    /// <summary>RCW 是否已释放；保证同一实例只释放一次，且不释放其他别名引用。</summary>
    private bool _released;

    internal ElementInfo(uint id, ITfUIElement? element)
    {
        Id = id;
        _element = element;
        _snapshotSource = new BorrowedCandidateSource(this);
        Refresh();
    }

    internal uint Id { get; }

    internal string Description { get; private set; } = "(none)";

    internal string Guid { get; private set; } = "(none)";

    internal bool IsCandidateList { get; private set; }

    internal uint CandidateCount { get; private set; }

    internal uint Selection { get; private set; }

    internal string FirstCandidates { get; private set; } = "(none)";

    /// <summary>元素是否已被 End（同步或异步）。Ended 元素不再允许任何 COM 调用。</summary>
    internal bool Ended => _ended;

    /// <summary>R34：presenter 为本元素分配的代数（Begin 接入时写入；未接入为 0）。</summary>
    internal uint HostDrawnGeneration { get; set; }

    /// <summary>R34：true 表示一次 Update 快照借用正在进行中；借用内的同步 End
    /// 只置死亡标记，本标志供呈现器在借用退出前判断“查询期间元素已被 End”——
    /// 防止把 still-in-flight 的快照发布到 overlay。</summary>
    internal bool ExecutingPublish => _inFlight > 0;

    /// <summary>
    /// R34B：在<b>借用</b>内执行一段原生快照读取。<paramref name="body"/> 运行期间
    /// 任何同步 End 只置死亡标记、把 RCW 释放推迟到本借用退出；借用外该元素
    /// 不得再被调用（见 <see cref="TryShow"/>/<see cref="Refresh"/> 同一约定）。
    /// </summary>
    internal void ExecuteRead(Action body)
    {
        _inFlight++;
        try
        {
            body();
        }
        finally
        {
            _inFlight--;
            // 借用退出：若借用期间同步 End，在此恰好释放一次；此后所有 COM 调用停止。
            ReleaseIfNeeded();
        }
    }

    /// <summary>R34B：由呈现器在 Begin 注册的<b>借用感知</b>快照源。每次
    /// <see cref="ICandidateSnapshotSource.ReadPage"/> 调用都把 End 判定透传到
    /// <see cref="CandidatePageReader"/>——原生查询逐个发生在借用内，End 即中断。</summary>
    internal ICandidateSnapshotSource SnapshotSource => _snapshotSource;

    private readonly BorrowedCandidateSource _snapshotSource;

    /// <summary>
    /// 借用 RCW 刷新快照。与 <see cref="TryShow"/> 同一借用生命周期：
    /// 借用期间若同步 End 到达，<see cref="MarkEnded"/> 只置死亡标记、释放延迟到
    /// 本方法退出；此后**不再发起任何新 COM 调用**（含 GetGUID/候选查询）。
    /// 已取得的快照字段如实保留，但绝不拿旧 candidate/RCW 继续调用。
    /// </summary>
    internal void Refresh()
    {
        if (_element is null || _ended)
        {
            return;
        }

        _inFlight++;
        try
        {
            if (_element.GetDescription(out var description) >= 0)
            {
                Description = string.IsNullOrEmpty(description) ? "(empty)" : description;
            }

            if (_ended)
            {
                return;
            }

            if (_element.GetGUID(out var guid) >= 0)
            {
                Guid = guid.ToString();
            }

            if (_ended)
            {
                return;
            }

            _candidate ??= _element as ITfCandidateListUIElement;
            IsCandidateList = _candidate is not null;
            if (_candidate is null)
            {
                return;
            }

            if (_candidate.GetCount(out var count) >= 0)
            {
                CandidateCount = count;
            }

            if (_ended)
            {
                return;
            }

            if (_candidate.GetSelection(out var selection) >= 0)
            {
                Selection = selection;
            }

            if (_ended)
            {
                return;
            }

            var items = new List<string>();
            var limit = Math.Min(CandidateCount, 5u);
            for (var i = 0u; i < limit; i++)
            {
                if (_ended)
                {
                    break;
                }

                if (_candidate.GetString(i, out var item) >= 0 && !string.IsNullOrEmpty(item))
                {
                    items.Add(item);
                }
            }

            FirstCandidates = items.Count == 0 ? "(none)" : string.Join(" | ", items);
        }
        catch (Exception)
        {
            // 元素可能已失效；保持已有快照。
        }
        finally
        {
            _inFlight--;
            // 借用退出：若借用期间同步 End，在此恰好释放一次。
            ReleaseIfNeeded();
        }
    }

    /// <summary>
    /// 借用 RCW 执行一次 Show 探测。借用期间若同步 End 到达，<see cref="MarkEnded"/>
    /// 只置死亡标记；真正的 ReleaseComObject 在本借用退出的 finally 中**恰好一次**完成，
    /// 绝不在回调里就地释放仍在使用的 RCW。
    /// </summary>
    internal UiElementShowResult TryShow(bool show)
    {
        bool? before = null;
        bool? after = null;
        int? beforeHr = null;
        int? afterHr = null;
        var hr = TsfHResult.E_FAIL;
        var hrAvailable = false;
        string? exType = null;
        string? exMessage = null;
        string? exStage = null;
        var wasEnded = _ended;

        _inFlight++;
        try
        {
            if (_element is not null && !_ended)
            {
                try
                {
                    var hrBefore = _element.IsShown(out var shownBefore);
                    beforeHr = hrBefore;
                    if (hrBefore >= 0)
                    {
                        before = shownBefore != 0;
                    }
                }
                catch (Exception ex)
                {
                    exType ??= ex.GetType().Name;
                    exMessage ??= ex.Message;
                    exStage ??= "shown-before";
                }

                // IsShown 的失败不掩盖随后 Show 的真实 HRESULT。
                if (!_ended)
                {
                    try
                    {
                        hr = _element.Show(show ? 1 : 0);
                        hrAvailable = true;
                    }
                    catch (Exception ex)
                    {
                        exType ??= ex.GetType().Name;
                        exMessage ??= ex.Message;
                        exStage ??= "show";
                    }
                }

                // Show 返回后若元素已 End（同步回调），不得再调用 IsShown：
                // 该 RCW 已失效，调用只会抛出并把 hr 污染成 E_FAIL。
                if (!_ended)
                {
                    try
                    {
                        var hrAfter = _element.IsShown(out var shownAfter);
                        afterHr = hrAfter;
                        if (hrAfter >= 0)
                        {
                            after = shownAfter != 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        exType ??= ex.GetType().Name;
                        exMessage ??= ex.Message;
                        exStage ??= "shown-after";
                    }
                }
            }
        }
        finally
        {
            _inFlight--;
            // 借用退出：若元素已 End 且尚未释放，在此恰好释放一次。
            ReleaseIfNeeded();
        }

        return new UiElementShowResult(Id, IsCandidateList, hr, before, after)
        {
            ShowHrAvailable = hrAvailable,
            ShowExceptionType = exType,
            ShowExceptionMessage = exMessage,
            ShowExceptionStage = exStage,
            IsShownBeforeHr = beforeHr,
            IsShownAfterHr = afterHr,
            EndedDuringShow = _ended && !wasEnded,
        };
    }

    /// <summary>
    /// 标记元素已 End。若当前有借用中的 Show，释放延迟到借用退出时完成；
    /// 否则立即释放一次。幂等：重复调用不再释放。
    /// </summary>
    internal void MarkEnded()
    {
        _ended = true;
        ReleaseIfNeeded();
    }

    /// <summary>幂等：等价于 <see cref="MarkEnded"/>。</summary>
    public void Dispose() => MarkEnded();

    /// <summary>
    /// 仅当无人借用且尚未释放时，对本实例持有的 RCW 执行恰好一次
    /// ReleaseComObject；绝不触碰其他别名引用，也不做 AddRef 猜测。
    /// </summary>
    private void ReleaseIfNeeded()
    {
        if (_ended && _inFlight == 0 && !_released && _element is not null
            && Marshal.IsComObject(_element))
        {
            _released = true;
            try
            {
                Marshal.ReleaseComObject(_element);
            }
            catch (Exception)
            {
                // 释放失败不掩盖业务结果；进程退出时系统回收。
            }
        }
    }

    /// <summary>
    /// R34B：<see cref="ElementInfo"/> 持有的快照源。每次 ReadPage 都被外层
    /// <see cref="ExecuteRead"/> 借用包住（observer.UpdateUIElement 路径）；每次
    /// 原生调用前后复核本元素的 End 标记，与 <paramref name="sourceEnded"/>（同
    /// 一实例视角的 ended 探针）合并——任何一个为真都立即停止。
    /// </summary>
    private sealed class BorrowedCandidateSource : ICandidateSnapshotSource
    {
        private readonly ElementInfo _owner;

        internal BorrowedCandidateSource(ElementInfo owner) => _owner = owner;

        public bool IsCandidateList => _owner.IsCandidateList;

        public CandidatePageRead ReadPage(Func<bool> sourceEnded)
        {
            // 读取起点复核：未在借用内（_inFlight==0 时仍允许）或元素已 End 皆中止。
            // 逐次查询的 ended 判定由 CandidatePageReader 调用本委托完成。
            Func<bool> ended = () => _owner._ended || sourceEnded();
            var candidate = _owner._candidate
                ?? (_owner._element as ITfCandidateListUIElement);
            if (candidate is null || _owner._element is null)
            {
                return CandidatePageRead.Failed("element is not a candidate list");
            }

            try
            {
                return CandidatePageReader.Run(
                    new ComCandidateListOperations(candidate), ended);
            }
            catch (Exception ex)
            {
                return CandidatePageRead.Failed($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// TSF 接入与清理：线程管理器、文档管理器、上下文、sink advice、按键路由、应用编辑会话。
/// 所有资源成对释放；每一步失败都记录并继续后续清理。
/// </summary>
internal sealed class TsfSessionHost
{
    private readonly AcpTextStore _store;
    private readonly IntPtr _hwnd;
    private readonly ProbeLog? _log;
    private readonly TsfTrace _trace;
    private readonly bool _sta;

    /// <summary>R26：是否用 ActivateEx(UIELEMENTENABLEDONLY) 激活（默认 false）。</summary>
    private readonly bool _uiElementEnabledOnly;
    private readonly Action _requestRepaint;
    private readonly HostApartment? _hostApartment;

    private ITfThreadMgr? _threadMgr;
    private ITfDocumentMgr? _documentMgr;
    private ITfContext? _context;
    private ITfKeystrokeMgr? _keystrokeMgr;
    private ITfUIElementMgr? _uiElementMgr;
    private ITfSource? _uiElementSource;
    private TsfCompositionObserver? _compositionObserver;
    private TsfUiElementObserver? _uiElementObserver;
    private ITfDocumentMgr? _previousDocumentMgr;
    private uint _tid;
    private uint _editCookie;
    private uint _uiCookie;
    private bool _comInitialized;
    private bool _activated;
    private bool _contextPushed;
    private bool _associated;
    private bool _focusSet;
    private bool _uiAdvised;
    private bool _cleaned;
    private bool _lastKeyEaten;
    private ushort? _pendingHighSurrogate;

    internal TsfSessionHost(AcpTextStore store, IntPtr hwnd, ProbeLog? log, TsfTrace trace,
        bool sta, Action requestRepaint, HostApartment? hostApartment = null,
        bool uiElementEnabledOnly = false)
    {
        _store = store;
        _hwnd = hwnd;
        _log = log;
        _trace = trace;
        _sta = sta;
        _requestRepaint = requestRepaint;
        _hostApartment = hostApartment;
        _uiElementEnabledOnly = uiElementEnabledOnly;
    }

    /// <summary>
    /// R26：是否改用 <c>ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)</c> 激活。
    /// 默认 false 时完全保留既有 <c>ITfThreadMgr::Activate</c> 行为。
    /// </summary>
    internal bool UiElementEnabledOnly => _uiElementEnabledOnly;

    /// <summary>真实生效的激活标志（<c>GetActiveFlags</c> 的原始值）；未查询时为 null。</summary>
    internal uint? ActiveActivationFlags { get; private set; }

    /// <summary>本次实际使用的激活方式：<c>activate</c> / <c>activate-ex</c> / <c>(failed)</c>。</summary>
    internal string ActivationMode { get; private set; } = "(not-activated)";

    internal bool Engaged => _threadMgr is not null && _context is not null;

    /// <summary>
    /// 是否已取得 TSF 激活所有权（清理链据此决定是否执行唯一一次 Deactivate）。
    /// R26：<c>ActivateEx</c> 原生成功即置真——即使随后 flags 校验失败，也必须由清理链释放。
    /// </summary>
    internal bool ActivationOwned => _activated;

    internal ITfThreadMgr? ThreadManager => _threadMgr;

    internal ITfDocumentMgr? DocumentManager => _documentMgr;

    internal readonly record struct SessionResourceState(
        bool ComInitialized,
        bool Activated,
        bool ContextPushed,
        bool Associated,
        bool FocusSet,
        bool UiAdvised,
        bool HasThreadManager,
        bool HasDocumentManager,
        bool HasContext);

    internal SessionResourceState ResourceState => new(
        _comInitialized, _activated, _contextPushed, _associated, _focusSet, _uiAdvised,
        _threadMgr is not null, _documentMgr is not null, _context is not null);

    internal uint ClientId => _tid;

    internal uint EditCookie => _editCookie;

    /// <summary>已推入的 TSF context；只供只读诊断（context/view 几何对照）使用，调用方不得释放。</summary>
    internal ITfContext? Context => _context;

    /// <summary>
    /// R29B 只读接线诊断：<see cref="Engage"/> 时真正交给
    /// <c>ITfThreadMgr::AssociateFocus</c> 的 hwnd —— opt-in 为 input child，默认 off 为 root。
    /// 只读：不改变激活标志、不重新关联、不影响 context 生命周期；
    /// 隐藏宿主检查据此证明 TSF 关联指向真实输入 surface 而非 root。
    /// </summary>
    internal IntPtr AssociatedHwnd => _associated ? _hwnd : IntPtr.Zero;

    internal int CleanupFailures { get; private set; }

    internal IReadOnlyList<CleanupStepResult> LastCleanupSteps { get; private set; }
        = Array.Empty<CleanupStepResult>();

    internal string Apartment { get; private set; } = "(unknown)";

    internal TsfUiElementObserver? UiElements => _uiElementObserver;

    /// <summary>
    /// 仅供测试：直接驱动激活分支（不创建 document/context）。
    /// 生产路径只从 <see cref="Engage"/> 调用同一实现。
    /// </summary>
    internal int ActivateForTest(ITfThreadMgr threadMgr)
        => ActivateUiElementEnabledOnly(threadMgr);

    /// <summary>
    /// R26：用 <c>ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)</c> 激活。
    /// <para>
    /// 严格不混双激活：只在确实拿到 <see cref="ITfThreadMgrEx"/> 时才调用 ActivateEx；
    /// QI 失败、ActivateEx 失败或真实 flags 缺失标志时都**明确失败**，绝不回退到默认
    /// <c>Activate</c>（否则会掩盖真实结果）。
    /// </para>
    /// <para>
    /// COM 所有权：这里的 <c>ITfThreadMgrEx</c> 是通过**托管强制转换**从同一个
    /// <see cref="ITfThreadMgr"/> RCW 得到的接口视图。RCW 由 CLR 按引用计数管理，
    /// 强制转换**不产生**需要手工释放的额外 COM 引用，因此这里**不**调用
    /// <c>Marshal.ReleaseComObject</c>（那会错误地释放共享 RCW，使既有 ThreadMgr 悬空）。
    /// 只有改用原始 <c>Marshal.QueryInterface</c> 取得裸指针时，才需要成对 <c>Marshal.Release</c>。
    /// </para>
    /// <para>
    /// 激活所有权：一旦 <c>ActivateEx</c> 原生返回成功，本会话就**已经**处于激活状态，
    /// 因此立即置 <see cref="_activated"/>；之后若 flags 校验失败，仍由清理链执行**唯一一次**
    /// <c>Deactivate</c>，不会泄漏激活引用。
    /// </para>
    /// </summary>
    private int ActivateUiElementEnabledOnly(ITfThreadMgr threadMgr)
    {
        const uint requestedFlags = TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY;

        // 精确接口转换：必须拿到 ITfThreadMgrEx（不是 IUnknown/object 身份）。
        if (threadMgr is not ITfThreadMgrEx threadMgrEx)
        {
            ActivationMode = "(failed)";
            _trace("tsf", "activate-ex", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(TsfHResult.E_NOINTERFACE),
                ["requested_mode"] = "activate-ex",
                ["requested_flags"] = $"0x{requestedFlags:X}",
                ["stage"] = "query-interface",
                ["iid"] = TsfIids.ThreadMgrEx.ToString(),
                ["supported"] = false,
            });
            return TsfHResult.E_NOINTERFACE;
        }

        _trace("tsf", "activate-ex-query", new Dictionary<string, object?>
        {
            ["requested_mode"] = "activate-ex",
            ["iid"] = TsfIids.ThreadMgrEx.ToString(),
            ["supported"] = true,
        });

        // 初始化时的真实 active flags（ActivateEx 之前）。
        var preFlagsHr = threadMgrEx.GetActiveFlags(out var preFlags);
        _trace("tsf", "active-flags", new Dictionary<string, object?>
        {
            ["stage"] = "before-activate-ex",
            ["hresult"] = TsfHResult.Describe(preFlagsHr),
            ["flags"] = preFlagsHr >= 0 ? $"0x{preFlags:X}" : null,
            ["requested_mode"] = "activate-ex",
        });

        var exHr = threadMgrEx.ActivateEx(out _tid, requestedFlags);
        ActivationMode = "activate-ex";
        // 所有权：ActivateEx 只要原生成功，TSF 线程就已处于激活状态。
        // 立即记下激活所有权，确保后续 flags 校验失败时清理链仍执行唯一一次 Deactivate。
        if (exHr >= 0)
        {
            _activated = true;
        }

        _trace("tsf", "activate-ex", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(exHr),
            ["client_id"] = _tid,
            ["requested_mode"] = "activate-ex",
            ["requested_flags"] = $"0x{requestedFlags:X}",
            ["stage"] = "activate-ex",
            ["activation_owned"] = _activated,
        });
        if (exHr < 0)
        {
            ActivationMode = "(failed)";
            return exHr;
        }

        // 真实生效标志：ActivateEx 返回后立即读取一次真实 active flags。
        // 正确 ABI 下此阶段 GetActiveFlags 返回 S_OK 且包含 UIElement-only 位；
        // 若读取失败或位缺失，则如实失败——即使 ActivateEx 自己返回了 S_OK。
        var flagsHr = threadMgrEx.GetActiveFlags(out var activeFlags);
        ActiveActivationFlags = flagsHr >= 0 ? activeFlags : null;
        var hasActivated = flagsHr >= 0 && (activeFlags & TsfFlags.TF_TMF_ACTIVATED) != 0;
        var hasUiElementOnly = flagsHr >= 0
            && (activeFlags & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0;
        _trace("tsf", "active-flags", new Dictionary<string, object?>
        {
            ["stage"] = "after-activate-ex",
            ["hresult"] = TsfHResult.Describe(flagsHr),
            ["flags"] = flagsHr >= 0 ? $"0x{activeFlags:X}" : null,
            ["requested_flags"] = $"0x{requestedFlags:X}",
            ["has_activated_bit"] = hasActivated,
            ["has_uielement_only"] = hasUiElementOnly,
            ["requested_mode"] = "activate-ex",
        });

        if (flagsHr < 0)
        {
            ActivationMode = "(failed)";
            return flagsHr;
        }

        if (!hasUiElementOnly)
        {
            // ActivateEx 返回 S_OK 但真实 flags 未体现该模式：如实失败，不伪 S_OK、不回退。
            ActivationMode = "(failed)";
            _trace("tsf", "activate-ex-flags-missing", new Dictionary<string, object?>
            {
                ["flags"] = $"0x{activeFlags:X}",
                ["expected"] = $"0x{requestedFlags:X}",
                ["activate_ex_returned"] = "S_OK",
                ["note"] = "system accepted ActivateEx but reported no UIElement-only bit",
            });
            return TsfHResult.E_FAIL;
        }

        return TsfHResult.S_OK;
    }

    /// <summary>
    /// 再次读取真实 active flags（只读，用于日志与显式模式的复核）。
    /// <para>
    /// 读取失败时返回 <c>null</c> 并**保留** <see cref="ActiveActivationFlags"/> 里上一次的
    /// 历史值不变——绝不把旧值当作本次新结果返回，调用方据 <c>null</c> 判定“本次不可用”。
    /// 默认路径不会调用本方法。
    /// </para>
    /// </summary>
    internal uint? ReadActiveActivationFlags(string stage)
    {
        if (_threadMgr is not ITfThreadMgrEx threadMgrEx)
        {
            _trace("tsf", "active-flags", new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["hresult"] = TsfHResult.Describe(TsfHResult.E_NOINTERFACE),
                ["flags"] = null,
                ["requested_mode"] = ActivationMode,
                ["note"] = "ITfThreadMgrEx unavailable",
            });
            return null;
        }

        var hr = threadMgrEx.GetActiveFlags(out var flags);
        if (hr >= 0)
        {
            ActiveActivationFlags = flags;
        }

        _trace("tsf", "active-flags", new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["hresult"] = TsfHResult.Describe(hr),
            ["flags"] = hr >= 0 ? $"0x{flags:X}" : null,
            ["requested_mode"] = ActivationMode,
            ["fresh"] = hr >= 0,
        });
        return hr >= 0 ? flags : null;
    }

    internal int Engage()
    {
        if (_store.NotificationDispatcher is null)
        {
            // 生产原型必须在 Engage 前安装自有 UI dispatcher；不能由 store 跨线程猜安全阶段。
            _trace("env", "missing-notification-dispatcher", null);
            return TsfHResult.E_FAIL;
        }

        if (_hostApartment is { } lease)
        {
            // R17：套间已由宿主入口在创建 HWND 之前、在同一个真实宿主线程上确定。
            // 这里绝不二次初始化，也不替宿主反初始化（CoUninitialize 由 HostApartment 租约负责）。
            if (lease.HResult < 0)
            {
                _trace("env", "host-apartment-unavailable", new Dictionary<string, object?>
                {
                    ["hresult"] = TsfHResult.Describe(lease.HResult),
                    ["requested"] = HostApartment.DescribeRequested(lease.RequestedSta),
                    ["apartment_before"] = lease.BeforeDescription,
                    ["apartment"] = lease.Description,
                    ["win32_thread_id"] = lease.Win32ThreadId,
                });
                return lease.HResult;
            }

            _comInitialized = false;
            Apartment = lease.Description;
            _trace("env", "apartment", new Dictionary<string, object?>
            {
                ["apartment"] = Apartment,
                ["source"] = "host-apartment",
                ["requested"] = HostApartment.DescribeRequested(lease.RequestedSta),
                ["matches_requested"] = lease.ReachedRequestedApartment,
                ["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId(),
                ["managed_thread_id"] = Environment.CurrentManagedThreadId,
            });

            if (!lease.ReachedRequestedApartment)
            {
                // 请求 STA 却落在 MTA（或反之）时必须如实失败，不能用错误套间产出“对照”证据。
                _trace("env", "apartment-mismatch", new Dictionary<string, object?>
                {
                    ["requested"] = HostApartment.DescribeRequested(lease.RequestedSta),
                    ["apartment"] = Apartment,
                    ["apartment_type"] = lease.ApartmentType,
                });
                return TsfHResult.E_FAIL;
            }
        }
        else
        {
            var coHr = ComNative.CoInitializeEx(IntPtr.Zero,
                _sta ? ComNative.CoInitApartmentThreaded : ComNative.CoInitMultithreaded);
            _comInitialized = coHr >= 0;
            _trace("env", "coinitialize", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(coHr),
                ["requested"] = _sta ? "STA" : "MTA",
                ["succeeded"] = coHr >= 0,
            });
            if (coHr < 0)
            {
                return coHr;
            }

            var apartmentHr = NativeMethods.CoGetApartmentType(out var apartmentType, out var apartmentQualifier);
            Apartment = apartmentHr >= 0
                ? $"type={apartmentType} qualifier={apartmentQualifier}"
                : $"CoGetApartmentType failed {TsfHResult.Describe(apartmentHr)}";
            _trace("env", "apartment", new Dictionary<string, object?>
            {
                ["apartment"] = Apartment,
                ["source"] = "session",
                ["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId(),
                ["managed_thread_id"] = Environment.CurrentManagedThreadId,
            });
        }

        var tmHr = TsfNative.TF_CreateThreadMgr(out var threadMgr);
        _trace("tsf", "create-thread-mgr", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(tmHr),
            ["iid"] = TsfIids.ThreadMgr.ToString(),
        });
        if (tmHr < 0 || threadMgr is null)
        {
            Cleanup();
            return tmHr < 0 ? tmHr : TsfHResult.E_FAIL;
        }

        _threadMgr = threadMgr;

        // R26：默认路径保持不变；仅显式开启时才改用 ITfThreadMgrEx::ActivateEx。
        // 两种模式互斥，失败不回退到另一种激活（避免混合激活掩盖真实结果）。
        if (_uiElementEnabledOnly)
        {
            var exHr = ActivateUiElementEnabledOnly(threadMgr);
            if (exHr < 0)
            {
                Cleanup();
                return exHr;
            }
        }
        else
        {
            var activateHr = threadMgr.Activate(out _tid);
            ActivationMode = "activate";
            _trace("tsf", "activate", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(activateHr),
                ["client_id"] = _tid,
                ["requested_mode"] = "activate",
            });
            if (activateHr < 0)
            {
                ActivationMode = "(failed)";
                Cleanup();
                return activateHr;
            }
        }

        _activated = true;

        var docHr = threadMgr.CreateDocumentMgr(out var documentMgr);
        _trace("tsf", "create-document-mgr", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(docHr),
        });
        if (docHr < 0 || documentMgr is null)
        {
            Cleanup();
            return docHr < 0 ? docHr : TsfHResult.E_FAIL;
        }

        _documentMgr = documentMgr;

        // 官方注册方式：CreateContext 的 owner 对象必须同时暴露 ITfContextOwnerCompositionSink，
        // TSF 会在创建上下文时对该对象 QI 并安装组合 sink。必须在 CreateContext 之前接好。
        _compositionObserver = new TsfCompositionObserver(_store, _trace, _requestRepaint);
        _store.CompositionObserver = _compositionObserver;

        var contextHr = documentMgr.CreateContext(_tid, 0, _store, out var context, out _editCookie);
        _trace("tsf", "create-context", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(contextHr),
            ["edit_cookie"] = _editCookie,
            ["store"] = _store.GetType().Name,
            ["owner_exposes_composition_sink"] = _store is ITfContextOwnerCompositionSink,
        });
        if (contextHr < 0 || context is null)
        {
            Cleanup();
            return contextHr < 0 ? contextHr : TsfHResult.E_FAIL;
        }

        _context = context;

        var pushHr = documentMgr.Push(context);
        _trace("tsf", "push-context", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(pushHr),
        });
        if (pushHr < 0)
        {
            Cleanup();
            return pushHr;
        }

        _contextPushed = true;

        var associateHr = threadMgr.AssociateFocus(_hwnd, documentMgr, out _previousDocumentMgr);
        _trace("tsf", "associate-focus", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(associateHr),
            ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
            ["previous"] = _previousDocumentMgr is null ? "(null)" : "document-mgr",
        });
        if (associateHr < 0)
        {
            // 关键关联失败不能假装 engaged。
            Cleanup();
            return associateHr;
        }

        _associated = true;

        var focusHr = threadMgr.SetFocus(documentMgr);
        _trace("tsf", "set-focus", new Dictionary<string, object?>
        {
            ["hresult"] = TsfHResult.Describe(focusHr),
        });
        if (focusHr < 0)
        {
            Cleanup();
            return focusHr;
        }

        _focusSet = true;

        _keystrokeMgr = threadMgr as ITfKeystrokeMgr;
        _trace("tsf", "keystroke-mgr", new Dictionary<string, object?>
        {
            ["available"] = _keystrokeMgr is not null,
        });

        _uiElementMgr = threadMgr as ITfUIElementMgr;
        _uiElementObserver = new TsfUiElementObserver(_uiElementMgr, _trace);
        _uiElementSource = _uiElementMgr as ITfSource;
        if (_uiElementSource is not null)
        {
            // ITfUIElementMgr 本身没有 AdviseSink；sink 通过同一对象 QI 出的 ITfSource 注册。
            var uiHr = _uiElementSource.AdviseSink(
                TsfIids.UiElementSink, _uiElementObserver, out _uiCookie);
            _uiAdvised = uiHr >= 0;
            _trace("tsf", "advise-uielement-sink", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(uiHr),
                ["cookie"] = _uiCookie,
                ["iid"] = TsfIids.UiElementSink.ToString(),
            });
        }
        else
        {
            _trace("tsf", "uielement-source-missing", new Dictionary<string, object?>
            {
                ["manager_available"] = _uiElementMgr is not null,
            });
        }

        try
        {
            if (_log is not null)
            {
                TsfSession.LogTextServiceModules(_log, "tsf-prototype");
            }
        }
        catch (Exception)
        {
            // 只读诊断失败不影响接入。
        }

        _trace("tsf", "engaged", new Dictionary<string, object?>
        {
            ["thread_focus"] = QueryThreadFocus(),
            ["store_sink_advised"] = _store.HasSink,
            ["store_sink_mask"] = $"0x{_store.SinkMask:X}",
            ["activation_mode"] = ActivationMode,
            ["active_flags"] = ActiveActivationFlags is { } flags ? $"0x{flags:X}" : null,
        });

        // R26：默认路径**不**触碰扩展接口（ITfThreadMgrEx QI / GetActiveFlags），
        // 保持旧行为不受“系统不支持该扩展”影响。只有显式开启新模式时才做 engaged 后的复核。
        // after-activate-ex 与 after-engaged 两个阶段读取的都是**真实** active flags；
        // 任一阶段读取失败或缺少 UIElement-only 位都如实失败。
        if (_uiElementEnabledOnly)
        {
            var engagedFlags = ReadActiveActivationFlags("after-engaged");
            var uiElementOnly = engagedFlags is { } value
                && (value & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0;
            if (engagedFlags is null || !uiElementOnly)
            {
                // engaged 后仍无法确认该模式：明确失败（清理链会执行唯一一次 Deactivate）。
                _trace("tsf", "activate-ex-engaged-flags-failed", new Dictionary<string, object?>
                {
                    ["flags"] = engagedFlags is { } f ? $"0x{f:X}" : "(unavailable)",
                    ["requested_mode"] = ActivationMode,
                });
                ActivationMode = "(failed)";
                return TsfHResult.E_FAIL;
            }
        }

        return TsfHResult.S_OK;
    }

    internal void OnWindowFocus(bool focused)
    {
        var threadMgr = _threadMgr;
        if (threadMgr is null)
        {
            return;
        }

        try
        {
            var hr = focused ? threadMgr.SetFocus(_documentMgr) : threadMgr.SetFocus(null);
            if (focused)
            {
                _focusSet = hr >= 0;
            }
            else if (hr >= 0)
            {
                _focusSet = false;
            }

            _trace("focus", focused ? "set-focus" : "clear-focus", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(hr),
                ["composing"] = _store.IsComposing,
                ["thread_focus"] = QueryThreadFocus(),
            });
        }
        catch (Exception ex)
        {
            _trace("focus", "exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }
    }

    internal bool RouteKeyDown(IntPtr wParam, IntPtr lParam)
    {
        _lastKeyEaten = false;
        var manager = _keystrokeMgr;
        if (manager is null)
        {
            return false;
        }

        try
        {
            var testHr = manager.TestKeyDown(wParam, lParam, out var eaten);
            if (testHr < 0)
            {
                _trace("key", "test-key-down-failed", new Dictionary<string, object?>
                {
                    ["hresult"] = TsfHResult.Describe(testHr),
                    ["wparam"] = wParam.ToInt64(),
                });
                return false;
            }

            if (eaten == 0)
            {
                return false;
            }

            var keyHr = manager.KeyDown(wParam, lParam, out _);
            _lastKeyEaten = true;
            _trace("key", "key-down-eaten", new Dictionary<string, object?>
            {
                ["test_hresult"] = TsfHResult.Describe(testHr),
                ["key_hresult"] = TsfHResult.Describe(keyHr),
                ["wparam"] = wParam.ToInt64(),
                ["lparam"] = lParam.ToInt64(),
            });
            return true;
        }
        catch (Exception ex)
        {
            _trace("key", "key-down-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return false;
        }
    }

    internal bool RouteKeyUp(IntPtr wParam, IntPtr lParam)
    {
        var manager = _keystrokeMgr;
        if (manager is null)
        {
            return false;
        }

        try
        {
            var testHr = manager.TestKeyUp(wParam, lParam, out var eaten);
            if (testHr < 0 || eaten == 0)
            {
                return false;
            }

            var keyHr = manager.KeyUp(wParam, lParam, out _);
            _trace("key", "key-up-eaten", new Dictionary<string, object?>
            {
                ["test_hresult"] = TsfHResult.Describe(testHr),
                ["key_hresult"] = TsfHResult.Describe(keyHr),
                ["wparam"] = wParam.ToInt64(),
            });
            return true;
        }
        catch (Exception ex)
        {
            _trace("key", "key-up-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return false;
        }
    }

    /// <summary>WM_CHAR：被 TIP 吃掉的键不重复写；组合中一律不写；否则走应用编辑会话。</summary>
    internal bool HandleChar(IntPtr wParam)
    {
        var keyEaten = _lastKeyEaten;
        _lastKeyEaten = false;
        if (keyEaten)
        {
            _trace("key", "char-suppressed-key-eaten", new Dictionary<string, object?>
            {
                ["wparam"] = wParam.ToInt64(),
            });
            return true;
        }

        if (_store.IsComposing)
        {
            _trace("key", "char-suppressed-composing", new Dictionary<string, object?>
            {
                ["wparam"] = wParam.ToInt64(),
            });
            return true;
        }

        var codeUnit = (ushort)(long)wParam;

        // 代理对：暂存高代理，等低代理到达后作为一次原子编辑写入（与生产适配器按标量计数一致）。
        if (char.IsHighSurrogate((char)codeUnit))
        {
            FlushPendingSurrogate();
            _pendingHighSurrogate = codeUnit;
            _trace("key", "char-high-surrogate-buffered", new Dictionary<string, object?>
            {
                ["code_unit"] = $"0x{codeUnit:X4}",
            });
            return true;
        }

        if (char.IsLowSurrogate((char)codeUnit) && _pendingHighSurrogate is { } high)
        {
            _pendingHighSurrogate = null;
            var pair = new string(new[] { (char)high, (char)codeUnit });
            var pairHr = RequestAppEdit(store => store.ApplyAppText(pair));
            _trace("key", "char-surrogate-pair-applied", new Dictionary<string, object?>
            {
                ["high"] = $"0x{high:X4}",
                ["low"] = $"0x{codeUnit:X4}",
                ["hresult"] = TsfHResult.Describe(pairHr),
            });
            return true;
        }

        FlushPendingSurrogate();
        var hr = RequestAppEdit(store => store.ApplyAppText(((char)codeUnit).ToString()));
        _trace("key", "char-applied", new Dictionary<string, object?>
        {
            ["code_unit"] = $"0x{codeUnit:X4}",
            ["hresult"] = TsfHResult.Describe(hr),
        });
        return true;
    }

    private void FlushPendingSurrogate()
    {
        if (_pendingHighSurrogate is not { } high)
        {
            return;
        }

        _pendingHighSurrogate = null;
        var hr = RequestAppEdit(store => store.ApplyAppText(((char)high).ToString()));
        _trace("key", "char-unpaired-surrogate-applied", new Dictionary<string, object?>
        {
            ["code_unit"] = $"0x{high:X4}",
            ["hresult"] = TsfHResult.Describe(hr),
        });
    }

    /// <summary>
    /// R29B opt-in：真实**只读**编辑会话（TF_ES_SYNC|TF_ES_READ）。在持锁 DoEditSession
    /// 内执行 <paramref name="body"/>，返回其 HRESULT；会话未执行/请求失败如实回报。
    /// 用于 clip-out 下在真读锁内验证 GetTextExt 零/clipped 矩形——与 TIP 查询同前提。
    /// </summary>
    internal int RequestAppRead(Func<AcpTextStore, int> body)
    {
        var context = _context;
        if (context is null)
        {
            return TsfHResult.E_FAIL;
        }

        try
        {
            var session = new AppEditSession(_store, body, _trace);
            var hr = context.RequestEditSession(_tid, session,
                ContextViewProbe.ReadOnlySessionFlags, out var sessionHr);
            return hr >= 0 ? sessionHr : hr;
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    internal int RequestAppEdit(Func<AcpTextStore, int> body)
    {
        var context = _context;
        if (context is null)
        {
            return TsfHResult.E_FAIL;
        }

        try
        {
            var session = new AppEditSession(_store, body, _trace);
            var hr = context.RequestEditSession(_tid, session, TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE,
                out var sessionHr);
            _trace("edit-session", "request", new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(hr),
                ["session_hresult"] = TsfHResult.Describe(sessionHr),
                ["write_lock"] = _store.Locks.IsWriteLocked,
            });
            return hr >= 0 ? sessionHr : hr;
        }
        catch (Exception ex)
        {
            _trace("edit-session", "request-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return TsfHResult.E_FAIL;
        }
    }

    internal void Cleanup()
    {
        if (_cleaned)
        {
            return;
        }

        _cleaned = true;
        var composing = _store.IsComposing;

        _trace("cleanup", "begin", new Dictionary<string, object?>
        {
            ["composing"] = composing,
            ["composition_span"] = $"{_store.CompositionSpan.Start}..{_store.CompositionSpan.End}",
            ["thread_focus"] = QueryThreadFocus(),
        });

        var chain = new CleanupChain();
        chain.Add("set-focus-null", () =>
        {
            if (_threadMgr is null)
            {
                return TsfHResult.S_OK;
            }

            var hr = _threadMgr.SetFocus(null);
            var wasFocused = _focusSet;
            _focusSet = false;
            // SetFocus(NULL) 的真实 HRESULT 以日志为准；只有此前确实设置过焦点才把失败计入清理失败。
            return hr < 0 && wasFocused ? hr : TsfHResult.S_OK;
        });
        chain.Add("notify-layout-destroy", () =>
        {
            // 必须在 context/sink 仍有效、Pop/Release/Deactivate 之前发送 DESTROY。
            _store.NotifyLayoutDestroyed();
            return TsfHResult.S_OK;
        });
        chain.Add("unadvise-uielement", () =>
        {
            if (!_uiAdvised || _uiElementSource is null || _uiCookie == 0)
            {
                return TsfHResult.S_OK;
            }

            var hr = _uiElementSource.UnadviseSink(_uiCookie);
            _uiCookie = 0;
            _uiAdvised = false;
            return hr;
        });
        chain.Add("pop-context", () =>
        {
            if (!_contextPushed || _documentMgr is null)
            {
                return TsfHResult.S_OK;
            }

            var hr = _documentMgr.Pop(TsfFlags.TF_POPF_ALL);
            _contextPushed = false;
            return hr;
        });
        chain.Add("restore-association", () =>
        {
            if (!_associated || _threadMgr is null || _hwnd == IntPtr.Zero)
            {
                return TsfHResult.S_OK;
            }

            // 恢复先前关联的文档管理器；返回的“被替换”指针是新的 out 引用，成对释放一次。
            var hr = _threadMgr.AssociateFocus(_hwnd, _previousDocumentMgr, out var replaced);
            _associated = false;
            if (replaced is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(replaced);
                }
                catch (Exception)
                {
                    return TsfHResult.E_FAIL;
                }
            }

            return hr;
        });
        chain.Add("release-previous-document-mgr", () => ReleaseCom(ref _previousDocumentMgr));
        chain.Add("release-context", () => ReleaseCom(ref _context));
        chain.Add("release-document-mgr", () => ReleaseCom(ref _documentMgr));
        chain.Add("deactivate", () =>
        {
            if (!_activated || _threadMgr is null)
            {
                return TsfHResult.S_OK;
            }

            var hr = _threadMgr.Deactivate();
            _activated = false;
            return hr;
        });
        chain.Add("release-thread-mgr", () => ReleaseCom(ref _threadMgr));
        chain.Add("drop-interface-aliases", () =>
        {
            // _keystrokeMgr/_uiElementMgr/_uiElementSource 都是 _threadMgr 同一 RCW 的 QI 别名；
            // RCW 只持有一个 COM 引用，不能对每个 cast 各 ReleaseComObject 一次。
            _keystrokeMgr = null;
            _uiElementMgr = null;
            _uiElementSource = null;
            return TsfHResult.S_OK;
        });
        chain.Add("dispose-uielement-observer", () =>
        {
            _uiElementObserver?.Dispose();
            _uiElementObserver = null;
            _compositionObserver = null;
            _store.CompositionObserver = null;
            _store.CancelPendingNotifications();
            _store.CancelCompositionTracking();
            return TsfHResult.S_OK;
        });
        chain.Add("couninitialize", () =>
        {
            if (!_comInitialized)
            {
                // 宿主租约（HostApartment）持有 COM 引用时，由宿主入口成对释放；
                // 会话没有引用，绝不代替别人（或宿主）反初始化。
                _trace("cleanup", "couninitialize-delegated", new Dictionary<string, object?>
                {
                    ["owner"] = _hostApartment is null ? "(none)" : "host-apartment",
                });
                return TsfHResult.S_OK;
            }

            ComNative.CoUninitialize();
            _comInitialized = false;
            return TsfHResult.S_OK;
        });

        var report = chain.Run();
        CleanupFailures = report.Failures;
        LastCleanupSteps = report.Steps;
        foreach (var step in report.Steps)
        {
            _trace("cleanup", step.Name, new Dictionary<string, object?>
            {
                ["hresult"] = TsfHResult.Describe(step.HResult),
                ["exception"] = step.Exception?.GetType().Name,
                ["message"] = step.Exception?.Message,
            });
        }

        _trace("cleanup", "end", new Dictionary<string, object?>
        {
            ["failures"] = CleanupFailures,
            ["composing_after"] = _store.IsComposing,
        });
    }

    private int QueryThreadFocus()
    {
        var threadMgr = _threadMgr;
        if (threadMgr is null)
        {
            return -1;
        }

        try
        {
            if (threadMgr.IsThreadFocus(out var focused) >= 0)
            {
                return focused;
            }
        }
        catch (Exception)
        {
            // 查询失败返回 -1 供日志区分。
        }

        return -1;
    }

    private int ReleaseCom<T>(ref T? reference) where T : class
    {
        var local = reference;
        reference = null;
        if (local is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            if (Marshal.IsComObject(local))
            {
                Marshal.ReleaseComObject(local);
            }

            return TsfHResult.S_OK;
        }
        catch (Exception ex)
        {
            _trace("cleanup", "release-exception", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            return TsfHResult.E_FAIL;
        }
    }
}
