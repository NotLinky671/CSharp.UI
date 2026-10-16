namespace CSharp.UI;

/// <summary>一个待处理的 <see cref="Responsive"/> 内容请求：最终有效输入签名。</summary>
/// <param name="Info">测量阶段登记的最终有效宽度提案。</param>
/// <param name="Revision">登记时的描述/内容版本；版本在消费前变化则请求过时。</param>
internal readonly record struct ResponsiveRequest(ResponsiveInfo Info, int Revision);

/// <summary>
/// <see cref="Responsive"/> 节点的内容事务状态：描述版本、已稳定内容签名与单轮重建预算计数。
/// </summary>
/// <remarks>
/// 描述版本使“同宽的新 builder 描述”仍重建一次；已稳定签名用于在返回旧提案时撤销过时请求。
/// 每轮计数在两个 <c>ProcessPendingWork</c> 调用之间重置，因此持续宽度变化不会被累计误判为循环。
/// </remarks>
internal sealed class ResponsiveContentState
{
    /// <summary>描述/内容版本；协调器替换描述或显式失效内容时递增。</summary>
    public int Revision;

    /// <summary>是否已成功构建过内容。</summary>
    public bool HasContent;

    /// <summary>当前已构建内容对应的最终有效宽度提案。</summary>
    public ResponsiveInfo StableInfo;

    /// <summary>当前已构建内容对应的描述版本。</summary>
    public int StableRevision = -1;

    /// <summary>是否已从有效测量得到过提案。</summary>
    public bool HasLastInfo;

    /// <summary>最近一次有效测量登记的宽度提案（显式内容失效直接排队时复用）。</summary>
    public ResponsiveInfo LastInfo;

    /// <summary>最近一次单轮重建预算所属的轮次标识。</summary>
    public long BudgetPass = -1;

    /// <summary>该轮次内的重建次数（按节点计，不与其它节点共用全局 8 轮）。</summary>
    public int RebuildsInPass;
}

/// <summary>
/// 响应式内容请求队列与安全阶段消费者。
/// </summary>
/// <remarks>
/// <para>
/// 测量只调用 <see cref="Register"/> 记录每个节点的最终有效提案；同一节点一轮内多次登记以最后一次为准。
/// <see cref="Consume"/> 在运行时安全阶段执行 builder 并协调内容，不做测量。
/// </para>
/// <para>
/// 预算分两层：<see cref="MaxRebuildsPerNodePerPass"/> 按节点计，正常深层与批量节点不会互相牵连；
/// <see cref="MaxTransactionsPerPass"/> 是本轮所有节点构建总数的固定上限，与现存节点数量无关，
/// 覆盖“每轮新建大量下一层 Responsive”或反复更换身份的广度扩张描述；
/// “每轮只新增一层”的深度扩张由 <see cref="LayoutContext.MaxMeasureDepth"/> 受控拦截。
/// </para>
/// </remarks>
internal sealed class ResponsiveTransactionManager
{
    /// <summary>单个节点在一轮 <c>ProcessPendingWork</c> 内的最大内容重建次数。</summary>
    internal const int MaxRebuildsPerNodePerPass = 8;

    /// <summary>一轮 <c>ProcessPendingWork</c> 内所有节点内容重建总数的上限（与现存节点数量无关）。</summary>
    internal const int MaxTransactionsPerPass = 4096;

    private readonly Dictionary<ViewNode, ResponsiveRequest> _pending = new(ReferenceEqualityComparer.Instance);

    /// <summary>待消费的请求数量（诊断用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>是否存在待消费请求。</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// 登记一个最终有效提案。该提案与已稳定签名一致且描述版本未变时撤销挂起请求（回到稳定提案），
    /// 否则以最后一次提案覆盖同轮较早的请求。
    /// </summary>
    public void Register(ViewNode node, ResponsiveInfo info)
    {
        if (!node.IsMounted)
        {
            return;
        }

        var state = node.ResponsiveContent ??= new ResponsiveContentState();
        state.HasLastInfo = true;
        state.LastInfo = info;

        if (!state.HasContent || state.StableRevision != state.Revision || !state.StableInfo.Equals(info))
        {
            _pending[node] = new ResponsiveRequest(info, state.Revision);
        }
        else
        {
            // 回到已稳定提案：撤销同轮较早登记、尚未消费的过时请求。
            _pending.Remove(node);
        }
    }

    /// <summary>
    /// 标记描述被替换：即使宽度不变也需要按新 builder 重建一次内容。
    /// 这里只递增版本、不直接排队：描述更新必须等本轮布局给出最终有效宽度后再构建，
    /// 否则会用过时宽度先构建一次，既重复构建，也可能因过时分支卸载本应复用的子项（含焦点/选区）。
    /// </summary>
    public void MarkDescriptionChanged(ViewNode node)
    {
        var state = node.ResponsiveContent ??= new ResponsiveContentState();
        state.Revision++;
    }

    /// <summary>
    /// 请求内容重建的通用入口（供后续状态依赖接入）；递增版本使同宽内容也重建一次。
    /// 已测量过的节点会直接排队（复用最近的最终有效提案）；运行时仅在测量递归深度受限、无法完成布局时
    /// 先消费它替换过深旧内容，再重试布局。正常布局仍以本轮测量得到的最终有效提案为准。
    /// 本阶段不改变任何状态语义，仅提供清晰接入点。
    /// </summary>
    public void InvalidateContent(ViewNode node)
    {
        var state = node.ResponsiveContent ??= new ResponsiveContentState();
        state.Revision++;
        QueueIfMeasured(node, state);
    }

    private void QueueIfMeasured(ViewNode node, ResponsiveContentState state)
    {
        if (state.HasLastInfo && node.IsMounted)
        {
            _pending[node] = new ResponsiveRequest(state.LastInfo, state.Revision);
        }
    }

    /// <summary>卸载或不再需要时移除挂起请求。</summary>
    public void Remove(ViewNode node) => _pending.Remove(node);

    /// <summary>丢弃全部挂起请求（异常清理）。</summary>
    public void Clear() => _pending.Clear();

    /// <summary>
    /// 消费一批请求：执行 builder、协调内容并更新稳定签名；返回实际构建数量。
    /// 过时（节点已卸载、视图已不是 Responsive、版本变化或已回到稳定签名）的请求被安全丢弃。
    /// </summary>
    public int Consume(UiRuntime runtime, long passId, ref int budget)
    {
        if (_pending.Count == 0)
        {
            return 0;
        }

        var batch = _pending.ToArray();
        _pending.Clear();

        var consumed = 0;
        foreach (var pair in batch)
        {
            var node = pair.Key;
            var request = pair.Value;
            if (!node.IsMounted || node.View is not Responsive responsive)
            {
                continue;
            }

            var state = node.ResponsiveContent;
            if (state is null || request.Revision != state.Revision)
            {
                // 描述在消费前又变化：本次请求已过时，最新版本会在下一次测量重新登记。
                continue;
            }

            if (state.HasContent && state.StableRevision == request.Revision && state.StableInfo.Equals(request.Info))
            {
                continue;
            }

            if (state.BudgetPass != passId)
            {
                state.BudgetPass = passId;
                state.RebuildsInPass = 0;
            }

            state.RebuildsInPass++;
            if (state.RebuildsInPass > MaxRebuildsPerNodePerPass)
            {
                var message =
                    $"检测到响应式内容循环：节点 {node.ShortDebugPath()} 在单轮更新中重复重建 {state.RebuildsInPass} 次" +
                    $"（上限 {MaxRebuildsPerNodePerPass}），最后一次输入为可用宽度 {Describe(request.Info)}。" +
                    "builder 不应根据内容自身尺寸持续改变结构；宽度/内容反馈必须收敛。";
                runtime.ReportDiagnostic(message);
                throw new InvalidOperationException(message);
            }

            if (budget <= 0)
            {
                var message =
                    $"响应式内容构建超出单轮整体工作预算：本轮总构建次数达到 {MaxTransactionsPerPass}，" +
                    $"节点 {node.ShortDebugPath()} 仍请求重建（可用宽度 {Describe(request.Info)}）。" +
                    "builder 每轮产生新的 Responsive 节点或反复更换身份会无限扩张，请让内容结构收敛。";
                runtime.ReportDiagnostic(message);
                throw new InvalidOperationException(message);
            }

            budget--;
            runtime.ExecuteResponsiveContent(node, responsive, request);
            consumed++;
        }

        return consumed;
    }

    private static string Describe(ResponsiveInfo info)
        => info.IsWidthBounded ? info.AvailableWidth.ToString("0.###") : "无界";
}
