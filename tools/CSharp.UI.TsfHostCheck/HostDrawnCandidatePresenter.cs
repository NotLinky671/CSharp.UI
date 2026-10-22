using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R34 opt-in：宿主自绘候选的<b>呈现生命周期核心</b>。真实路径与测试替身共用同一
/// 实例——observer 的 Begin/Update/End 回调把“元素视图”注入同一组状态迁移：
/// <list type="bullet">
/// <item>Begin：仅对候选元素注册视图并分配单调代数；非候选完全不接入，
/// 候选类型判定只看 QI，不要求候选内容已就绪。</item>
/// <item>Update：对存活候选借源查询生成有界快照并发布（display=apply+invalidation）。</item>
/// <item>End/dispose/焦点丢失：失效并<b>立即</b>清空 overlay；回收视图防止
/// “End 后又 Update”或“同 id 重复 Begin”把旧快照挂回去。</item>
/// </list>
/// 绝不再调用 <c>ITfUIElement.Show</c>、绝不恢复为 retype/commit/cancel、
/// 绝不把 <see cref="ElementInfo.FirstCandidates"/> 诊断串当 UI 数据。
/// </summary>
internal sealed class HostDrawnCandidatePresenter : IDisposable
{
    /// <summary>单次快照读取的候选总数硬上界（查询页起点表/条目都受此约束）。</summary>
    internal const uint MaxCandidateCount = 256;

    /// <summary>单页最多读取的条目数（一页真的画不下更多）。</summary>
    internal const int MaxPageEntries = 32;

    /// <summary>页起点表最大项数（GetPageIndex 两趟读取的 buffer 上界）。</summary>
    internal const uint MaxPageCount = 64;

    /// <summary>单条候选文本的最大长度（异常长串截断并保留原 index 语义）。</summary>
    internal const int MaxCandidateTextLength = 128;

    /// <summary>发布失败的脱敏原因前缀（真实失败详情进 trace，不进 Result）。</summary>
    internal const string PublishFailedReason = "publish-failed";

    private readonly Func<ICandidateOverlay> _overlayFactory;
    private readonly ProbeLog _log;
    private readonly TsfTrace _trace;
    private readonly object _gate = new();
    private readonly Dictionary<uint, View> _views = new();
    private uint _nextGeneration = 1;
    private CandidateSnapshot? _published;
    private bool _disposed;

    /// <summary>
    /// <paramref name="overlayFactory"/>：当前 UI 线程上取“真实 overlay 通道”
    /// （本实现为 <see cref="ICandidateOverlay"/>；窗口侧接线在 child host 模式注入）。
    /// 延迟取值：dispose 后 factory 仍被调用则返回 null overlay 时本类不发任何写。
    /// </summary>
    internal HostDrawnCandidatePresenter(Func<ICandidateOverlay> overlayFactory,
        ProbeLog log, TsfTrace trace)
    {
        _overlayFactory = overlayFactory;
        _log = log;
        _trace = trace;
    }

    /// <summary>当前已发布的快照（可能为 null / invalid 快照）。只读，UI 线程外安全。</summary>
    internal CandidateSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    /// <summary>上次发布是否含真实候选（executor 基线据此要求非空）。</summary>
    internal bool HasLiveCandidates => Current?.HasCandidates == true;

    /// <summary>当前注册的元素视图数（真实 + 测试路径共用）。</summary>
    internal int LiveViewCount
    {
        get
        {
            lock (_gate)
            {
                return _views.Count;
            }
        }
    }

    /// <summary>
    /// Begin 视角的元素快照：呈现器只关心<b>候选与否</b>与一个稳定的
    /// <see cref="ICandidateSnapshotSource"/>；元素内容是否已就绪不影响接入。
    /// </summary>
    internal readonly record struct ElementView(
        uint Id, ICandidateSnapshotSource Source);

    /// <summary>
    /// 元素被 End/dispose 的标记（observer 在 End/重复 Begin/释放时调用）。
    /// <paramref name="generation"/> 必须是 Begin 时注册的同一令牌，否则忽略——
    /// 绝不允许旧 id 的新元素误删新视图。
    /// </summary>
    internal void EndElement(uint id, uint generation, string reason)
    {
        List<View> removed;
        lock (_gate)
        {
            removed = new List<View>();
            if (_views.TryGetValue(id, out var view) && view.Generation == generation)
            {
                _views.Remove(id);
                removed.Add(view);
            }
        }

        if (removed.Count > 0)
        {
            _trace("uielement", "host-drawn-end", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["generation"] = generation,
                ["reason"] = reason,
                ["published_id"] = _published?.ElementId,
            });
            // 若被 End 的元素正是当前展示来源：立即失效并清屏——绝不继续画旧快照。
            ClearIfSourceEnded(id, generation, reason);
        }
    }

    /// <summary>
    /// Update 视角的发布：同一真实控制流被 observer.UpdateUIElement 与测试共享。
    /// <paramref name="sourceEnded"/>：查询<b>前</b>先判源有效，并透传给读取核心做
    /// 逐次原生调用复核——若查询期间元素被同步 End（借用内 reentrancy），读取立即
    /// 中断且本快照不得发布；End 的清理路径已清屏。
    /// </summary>
    internal CandidatePublishOutcome PublishUpdate(uint id, uint generation,
        ICandidateSnapshotSource source, Func<bool> sourceEnded)
    {
        // 源有效性与代数在同一锁内复核——同 id 新 Begin 已替换视图时，绝不让
        // 旧代查询继续往下走；元素已 End 时在发起任何 COM 调用前直接拦下。
        lock (_gate)
        {
            if (_disposed)
            {
                return CandidatePublishOutcome.Skipped("presenter-disposed");
            }

            if (!_views.TryGetValue(id, out var view) || view.Generation != generation
                || !ReferenceEquals(view.Source, source))
            {
                return CandidatePublishOutcome.Skipped("view-not-current-generation");
            }
        }

        if (SafeEnded(sourceEnded))
        {
            return CandidatePublishOutcome.Skipped("source-ended-before-read");
        }

        CandidatePageRead read;
        // 真正发起原生查询（update 的“执行”阶段）；Ended 判定逐次下传到读取核心。
        try
        {
            read = source.ReadPage(sourceEnded);
        }
        catch (Exception ex)
        {
            read = CandidatePageRead.Failed($"{ex.GetType().Name}: {ex.Message}");
        }

        var endedDuringRead = SafeEnded(sourceEnded);

        // 查询期间被同步 End：本快照不得发布——End 的清理路径已清屏。
        if (endedDuringRead)
        {
            _trace("uielement", "host-drawn-publish-skipped-ended", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["generation"] = generation,
                ["reason"] = "element ended during snapshot read",
            });
            return CandidatePublishOutcome.ExecutedResult(InvalidSnapshot(id, generation,
                read, "element-ended-during-read"));
        }

        if (!read.Ok)
        {
            var failed = InvalidSnapshot(id, generation, read,
                $"{PublishFailedReason}: {read.Failure ?? "unknown"}");
            // 发布失败 = 失败：写 invalid 快照 + 清空 overlay（绝非成功空/旧列表）。
            var failedOutcome = ApplyPublish(id, generation, source, sourceEnded,
                failed, hidden: false, failureNote: read.Failure);
            return failedOutcome ?? CandidatePublishOutcome.Skipped("publish-superseded");
        }

        var entries = new List<CandidateSnapshotEntry>(read.Entries.Count);
        for (var i = 0; i < read.Entries.Count; i++)
        {
            var raw = read.Entries[i] ?? string.Empty;
            var text = raw.Length > MaxCandidateTextLength
                ? raw[..MaxCandidateTextLength]
                : raw;
            entries.Add(new CandidateSnapshotEntry(read.PageStart + (uint)i, text));
        }

        var selectionOnPage = CandidateSnapshot.PageLocalSelection(
            read.Selection, read.PageStart, entries.Count);
        var snapshot = new CandidateSnapshot(id, generation, entries,
            selectionOnPage, read.Selection, read.Count, read.PageStart,
            read.CurrentPage, true, null);

        var outcome = ApplyPublish(id, generation, source, sourceEnded,
            snapshot, hidden: false, failureNote: null);
        return outcome ?? CandidatePublishOutcome.Skipped("publish-superseded");
    }

    private static CandidateSnapshot InvalidSnapshot(uint id, uint generation,
        CandidatePageRead read, string failure)
        => new(id, generation, Array.Empty<CandidateSnapshotEntry>(), -1, 0,
            read.Count, read.PageStart, read.CurrentPage, false, failure);

    /// <summary>
    /// 把一次查询结果<b>落到</b>真实 overlay：<b>提交点</b>在同一锁内复核
    /// sourceEnded/视图代数/disposed 后才写快照字段 → 应用/隐藏/无效化。
    /// overlay 抛异常不吞成成功：已发布槽改写为 invalid 快照并如实返回失败结果。
    /// </summary>
    private CandidatePublishOutcome? ApplyPublish(uint id, uint generation,
        ICandidateSnapshotSource source, Func<bool> sourceEnded,
        CandidateSnapshot snapshot, bool hidden, string? failureNote)
    {
        CandidateSnapshot? previous;
        lock (_gate)
        {
            // 提交点复核：读取结束到发布之间元素被 End/dispose、或同 id 新 Begin
            // 接管了视图时，本次提交作废——旧快照绝不能覆盖新元素状态。
            if (_disposed
                || !_views.TryGetValue(id, out var view)
                || view.Generation != generation
                || !ReferenceEquals(view.Source, source)
                || SafeEnded(sourceEnded))
            {
                _trace("uielement", "host-drawn-publish-aborted", new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["generation"] = generation,
                    ["reason"] = "source ended or superseded before publish commit",
                });
                return null;
            }

            previous = _published;
            _published = snapshot;
        }

        ICandidateOverlay? overlay = null;
        string? overlayFailure = null;
        try
        {
            overlay = _overlayFactory();
        }
        catch (Exception ex)
        {
            overlayFailure = $"factory {ex.GetType().Name}: {ex.Message}";
        }

        if (overlay is null)
        {
            // 真实显示层缺失/创建失败不能当成功发布：落成 invalid 快照并如实失败。
            var reason = overlayFailure ?? "overlay-unavailable";
            _trace("uielement", "host-drawn-no-overlay", new Dictionary<string, object?>
            {
                ["id"] = snapshot.ElementId,
                ["generation"] = snapshot.Generation,
                ["reason"] = reason,
            });
            var failed = new CandidateSnapshot(id, generation,
                Array.Empty<CandidateSnapshotEntry>(), -1, 0, snapshot.TotalCount,
                snapshot.PageStart, snapshot.CurrentPage, false,
                $"{PublishFailedReason}: {reason}");
            lock (_gate)
            {
                if (ReferenceEquals(_published, snapshot))
                {
                    _published = failed;
                }
            }

            return CandidatePublishOutcome.ExecutedResult(failed);
        }

        try
        {
            if (hidden || !snapshot.Valid)
            {
                overlay.Hide(snapshot.ElementId, snapshot.Generation,
                    hidden ? "hidden" : failureNote ?? PublishFailedReason);
            }
            else
            {
                overlay.Apply(snapshot);
            }

            overlay.Invalidate();
        }
        catch (Exception ex)
        {
            _trace("uielement", "host-drawn-overlay-failed", new Dictionary<string, object?>
            {
                ["id"] = snapshot.ElementId,
                ["generation"] = snapshot.Generation,
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
            // overlay 写失败不是成功：把已发布槽落成 invalid 快照（entries 已清），
            // 调用方据此把本次 update 判为失败——绝不让 log-only 异常冒出成功结果。
            var failed = new CandidateSnapshot(id, generation,
                Array.Empty<CandidateSnapshotEntry>(), -1, 0, snapshot.TotalCount,
                snapshot.PageStart, snapshot.CurrentPage, false,
                $"{PublishFailedReason}: overlay {ex.GetType().Name}");
            lock (_gate)
            {
                // 仅当槽里仍是刚才那份快照时改写——不得覆盖期间更新的发布。
                if (ReferenceEquals(_published, snapshot))
                {
                    _published = failed;
                }
            }

            return CandidatePublishOutcome.ExecutedResult(failed);
        }

        _trace("uielement", "host-drawn-publish", new Dictionary<string, object?>
        {
            ["id"] = snapshot.ElementId,
            ["generation"] = snapshot.Generation,
            ["valid"] = snapshot.Valid,
            ["count"] = snapshot.TotalCount,
            ["page"] = snapshot.CurrentPage,
            ["sel"] = snapshot.SelectionIndex,
            ["sel_on_page"] = snapshot.SelectionOnPage,
            ["entries"] = snapshot.Entries.Count,
            ["content"] = snapshot.DescribeContent(),
            ["previous_id"] = previous?.ElementId,
            ["previous_gen"] = previous?.Generation,
            ["failure"] = failureNote,
        });
        return CandidatePublishOutcome.ExecutedResult(snapshot);
    }

    /// <summary>当前展示来源被 End/dispose：失效 + 清空 overlay（立即隐藏，不留残影）。
    /// _published 在同锁内一并清空——已结束元素的快照绝不能留作"当前展示"。</summary>
    private void ClearIfSourceEnded(uint id, uint generation, string reason)
    {
        bool shouldClear;
        lock (_gate)
        {
            shouldClear = _published is { } p
                && p.ElementId == id && p.Generation == generation;
            if (shouldClear)
            {
                _published = null;
            }
        }

        if (!shouldClear)
        {
            return;
        }

        _trace("uielement", "host-drawn-clear", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["generation"] = generation,
            ["reason"] = reason,
        });

        ICandidateOverlay? overlay = null;
        try
        {
            overlay = _overlayFactory();
        }
        catch (Exception ex)
        {
            _trace("uielement", "host-drawn-clear-failed", new Dictionary<string, object?>
            {
                ["stage"] = "factory",
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        try
        {
            overlay?.Hide(id, generation, reason);
            overlay?.Invalidate();
        }
        catch (Exception ex)
        {
            _trace("uielement", "host-drawn-clear-failed", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }
    }

    /// <summary>
    /// 焦点丢失/会话结束：清掉全部视图 + 已发布快照 + overlay（立即清空显示）。
    /// </summary>
    internal void OnFocusLost(string reason)
    {
        List<uint> ids;
        lock (_gate)
        {
            ids = _views.Keys.ToList();
            _views.Clear();
            _published = null;
        }

        _trace("uielement", "host-drawn-focus-lost", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["cleared_views"] = ids.Count,
        });

        ICandidateOverlay? overlay = null;
        try
        {
            overlay = _overlayFactory();
        }
        catch (Exception ex)
        {
            _trace("uielement", "host-drawn-focus-lost-failed", new Dictionary<string, object?>
            {
                ["stage"] = "factory",
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }

        try
        {
            overlay?.Hide(0, 0, reason);
            overlay?.Invalidate();
        }
        catch (Exception ex)
        {
            _trace("uielement", "host-drawn-focus-lost-failed", new Dictionary<string, object?>
            {
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            });
        }
    }

    /// <summary>
    /// 注册一个 Begin 的元素视图；非候选源不接入。同 id 重复 Begin 由 observer 先
    /// EndElement 再 BeginElement，绝不让旧代数覆写新视图。返回分配的代数（非候选为 0）。
    /// </summary>
    internal uint BeginElement(ElementView view)
    {
        if (!view.Source.IsCandidateList)
        {
            _trace("uielement", "host-drawn-begin-noncandidate", new Dictionary<string, object?>
            {
                ["id"] = view.Id,
            });
            return 0;
        }

        uint generation;
        uint? supersededGeneration = null;
        lock (_gate)
        {
            generation = _nextGeneration++;
            if (_views.TryGetValue(view.Id, out var old))
            {
                // 同 id 重复 Begin：先记旧代数；锁外把"新元素接管"如实清一次屏——
                // 绝不把旧元素仍挂着的快照继续当新元素的展示。
                supersededGeneration = old.Generation;
            }

            _views[view.Id] = new View(view.Id, generation, view.Source);
        }

        if (supersededGeneration is { } oldGen)
        {
            _trace("uielement", "host-drawn-begin-supersede", new Dictionary<string, object?>
            {
                ["id"] = view.Id,
                ["old_generation"] = oldGen,
                ["new_generation"] = generation,
            });
            ClearIfSourceEnded(view.Id, oldGen, "superseded-by-duplicate-begin");
        }

        _trace("uielement", "host-drawn-begin", new Dictionary<string, object?>
        {
            ["id"] = view.Id,
            ["generation"] = generation,
            ["live_views"] = _views.Count,
        });
        return generation;
    }

    /// <summary>某 id 的当前代数（未注册为 null）。</summary>
    internal uint? GenerationOf(uint id)
    {
        lock (_gate)
        {
            return _views.TryGetValue(id, out var view) ? view.Generation : null;
        }
    }

    /// <summary>本元素是否仍被追踪且代数一致（Update 前复核；不一致 → 拒绝发布）。</summary>
    internal bool IsTracked(uint id, uint generation)
    {
        lock (_gate)
        {
            return _views.TryGetValue(id, out var view) && view.Generation == generation;
        }
    }

    /// <summary>取元素的注入源（Update 用）；未追踪返回 null。</summary>
    internal ICandidateSnapshotSource? SourceOf(uint id, uint generation)
    {
        lock (_gate)
        {
            return _views.TryGetValue(id, out var view) && view.Generation == generation
                ? view.Source
                : null;
        }
    }

    /// <summary>dispose：移除全部视图并清屏；此后任何 Begin/Update 都不再发布。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _views.Clear();
        }

        OnFocusLost("presenter-disposed");
    }

    private static bool SafeEnded(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            // 判定失败按“已结束”处理——fail closed，宁可不发布。
            return true;
        }
    }

    private sealed record View(uint Id, uint Generation, ICandidateSnapshotSource Source);
}
