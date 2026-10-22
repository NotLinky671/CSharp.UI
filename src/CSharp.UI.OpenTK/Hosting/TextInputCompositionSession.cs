using System.Collections.Immutable;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 未来 TSF store 与真实 <see cref="ITextInputClient"/> 之间的纯托管会话适配器（TI-07 P2a）。
/// </summary>
/// <remarks>
/// <para>
/// <b>唯一权威：</b>已提交文档只由核心编辑事务写入。本适配器从不写绑定、不持有自己的文档副本，
/// 每次操作后都用真实回读（<see cref="ITextInputClient.Document"/>）重建快照；预编辑只存在于镜像里。
/// </para>
/// <para>
/// <b>归属捕获：</b>构造时固化一个客户端 + epoch，并持有一个“该客户端是否仍是当前归属”的判定委托
/// （由宿主/桥提供）。旧捕获即使被迟到调用，判定失败就显式拒绝，绝不写入新会话。
/// </para>
/// <para>
/// <b>不做协议判定：</b>这里只提供<b>显式确认的结果</b>与<b>取消</b>操作；原生消息如何分类为“结果/取消/更新”
/// 属于 P2b 的 store 观察器职责。适配器不猜测组合结局。
/// </para>
/// <para>
/// <b>不排版/不生成帧/不调原生：</b>所有方法只调用 <see cref="ITextInputClient.ApplyComposition"/> 与
/// <see cref="ITextInputClient.Document"/>，以及可选的选择接缝 <see cref="ITextInputSelectionTarget"/>。
/// 布局失效由核心自己标记，适配器不触发任何排版。
/// </para>
/// <para>
/// <b>版本不假设：</b>结果提交后版本一律取真实回读值，绝不用“+1”；下一段预编辑的替换区取真实回读选区，
/// 绝不用“请求的结果长度”。
/// </para>
/// <para>
/// <b>不抛异常：</b>重入、归属失效、负载非法、客户端抛错都收敛为显式拒绝结果；拒绝原因不含用户原文。
/// </para>
/// </remarks>
internal sealed class TextInputCompositionSession
{
    private readonly ITextInputClient _client;
    private readonly Func<ITextInputClient, bool> _isCurrentOwner;
    private readonly int _epoch;

    /// <summary>正在投递事件的深度：重入（回调内再次调用）直接拒绝，避免递归写入。</summary>
    private int _applying;

    /// <summary>本会话是否已被显式结束/取消/判定失效；结束后所有操作都是幂等拒绝。</summary>
    private bool _closed;

    /// <summary>是否已经观察到归属失效（旧捕获不得再写入）。</summary>
    private bool _stale;

    private int _nextCompositionId;
    private int _nextSequence;
    private int _expectedVersion;

    /// <summary>单调操作编号：每次被接受的适配器操作各取一个新编号，作为组合票据的不可复用水位。</summary>
    private long _operationId;

    /// <summary>当前活动组合的票据；没有活动组合时为 null。</summary>
    private TextInputCompositionToken? _token;

    private TextRange _replacement;
    private string _preedit = string.Empty;
    private int _preeditCaret;
    private ImmutableArray<TextRange> _segments = [];
    private bool _composing;

    private long _mirrorRevision;
    private long _nativeRevision;

    private TextInputAuthoritativeSnapshot _snapshot;

    /// <summary>创建会话：捕获客户端、epoch 与归属判定。</summary>
    /// <param name="client">真实文本输入客户端（唯一写入权威）。</param>
    /// <param name="isCurrentOwner">
    /// 该客户端是否仍是当前有效归属；每个操作前后都会重新求值。为 null 时视为恒为当前归属（无桥场景）。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    internal TextInputCompositionSession(ITextInputClient client, Func<ITextInputClient, bool>? isCurrentOwner = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _epoch = client.Session.SessionId;
        _isCurrentOwner = isCurrentOwner ?? (_ => true);

        // 构造期回读失败即失效：之后所有写操作显式拒绝（不能拿空快照继续确认写入）。
        if (TryReadDocument(out var document))
        {
            _expectedVersion = document.Version;
            _replacement = document.Selection.Range;
            _snapshot = BuildSnapshot(document);
        }
        else
        {
            _snapshot = BuildSnapshot(new CompositionDocumentView(TextSnapshot.Empty));
        }
    }

    /// <summary>本会话捕获的客户端（供宿主做身份比较；不用于写入）。</summary>
    internal ITextInputClient Client => _client;

    /// <summary>本会话固化的 epoch。</summary>
    internal int Epoch => _epoch;

    /// <summary>当前期望的已提交文档版本（最近一次真实回读值）。</summary>
    internal int ExpectedVersion => _expectedVersion;

    /// <summary>当前组合 ID（下一次 <see cref="Begin"/> 使用 <see cref="NextCompositionId"/>）。</summary>
    internal int CompositionId => _nextCompositionId;

    /// <summary>下一次 <see cref="Begin"/> 将使用的组合 ID。</summary>
    internal int NextCompositionId => _nextCompositionId;

    /// <summary>本会话是否仍可写入（未结束且归属仍有效）。</summary>
    internal bool IsOpen => !_closed && !_stale;

    /// <summary>当前权威快照（已提交文档 + 显示镜像）。</summary>
    internal TextInputAuthoritativeSnapshot Snapshot => _snapshot;

    /// <summary>当前活动组合的票据；没有活动组合时为 null。</summary>
    internal TextInputCompositionToken? Token => _token;

    // ------------------------------------------------------------------ 组合生命周期

    /// <summary>
    /// 开始一段组合：以<b>当前已提交选区</b>或显式替换区作为预编辑替换范围。
    /// </summary>
    /// <param name="replacement">显式替换区（已提交坐标）；null 使用当前已提交选区范围。</param>
    /// <param name="preedit">初始预编辑串；null 视为空串。</param>
    /// <param name="preeditCaret">预编辑 caret（相对预编辑串）；null 取末尾。</param>
    /// <param name="segments">预编辑分段（相对预编辑串）。</param>
    /// <returns>
    /// 显式结论；被拒绝时快照保持不变。成功时 <see cref="TextInputAdapterResult.Token"/> 非 null，
    /// 调用方必须保存并在后续 Update/Commit/Cancel/End 中原样携带。
    /// </returns>
    internal TextInputAdapterResult Begin(
        TextRange? replacement = null,
        string? preedit = null,
        int? preeditCaret = null,
        IReadOnlyList<TextRange>? segments = null)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        if (!TryReadForWrite(out var document, out var readRejection))
        {
            return readRejection;
        }

        if (!ReconcileVersion(document, out var versionRejection))
        {
            return versionRejection;
        }

        if (_composing)
        {
            return TextInputAdapterResult.Rejected(_snapshot, "该会话已有活动组合；重复 Begin 不写入。");
        }

        var range = replacement ?? document.Selection.Range;
        if (!IsValidReplacement(range, document, out var rangeError))
        {
            return TextInputAdapterResult.Rejected(_snapshot, rangeError!);
        }

        var value = preedit ?? string.Empty;
        if (!IsValidPreedit(value, preeditCaret, segments, out var preeditError))
        {
            return TextInputAdapterResult.Rejected(_snapshot, preeditError!);
        }

        var compositionId = _nextCompositionId;
        var eventResult = ApplyEvent(
            CompositionEvent.Start(
                _epoch,
                compositionId,
                document.Version,
                0,
                range,
                value,
                document.Selection,
                preeditCaret,
                segments),
            document);

        if (eventResult.IsRejected)
        {
            return eventResult;
        }

        // 只有真实被接受后才推进身份水位并发放不可复用票据。
        _nextCompositionId = compositionId + 1;
        _nextSequence = 1;
        _composing = true;
        _replacement = range;
        _preedit = value;
        _preeditCaret = Math.Clamp(preeditCaret ?? value.Length, 0, value.Length);
        _segments = Freeze(segments);

        // 票据水位从 0 开始：Begin 本身不占用后续操作编号，因此调用方的第一个操作编号（>0）即被接受。
        var ticket = new TextInputCompositionToken(_epoch, compositionId, ++_operationId);
        _token = ticket;

        return CompleteAfterApply(TextInputAdapterStatus.Applied, eventResult, token: ticket);
    }

    /// <summary>
    /// 更新预编辑（不写绑定）。
    /// </summary>
    /// <param name="token">Begin 返回的组合票据；必须与当前活动组合是同一个实例。</param>
    /// <param name="operationId">
    /// 该原生回调的单调操作编号；必须严格大于本票据已接受的水位。重复投递的同一确认编号相同，因此被拒绝。
    /// </param>
    /// <param name="preedit">新的预编辑串。</param>
    /// <param name="preeditCaret">预编辑 caret；null 取末尾。</param>
    /// <param name="segments">预编辑分段。</param>
    internal TextInputAdapterResult UpdatePreedit(
        TextInputCompositionToken? token,
        long operationId,
        string? preedit,
        int? preeditCaret = null,
        IReadOnlyList<TextRange>? segments = null)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        if (TryRejectToken(token, operationId, out var tokenRejection))
        {
            return tokenRejection;
        }

        if (!_composing)
        {
            return TextInputAdapterResult.Rejected(_snapshot, "没有活动组合的预编辑更新被拒绝：缺少 Begin 的替换区锚点。");
        }

        var value = preedit ?? string.Empty;
        if (!IsValidPreedit(value, preeditCaret, segments, out var preeditError))
        {
            return TextInputAdapterResult.Rejected(_snapshot, preeditError!);
        }

        if (!TryReadForWrite(out var document, out var readRejection))
        {
            return readRejection;
        }

        if (!ReconcileVersion(document, out var versionRejection))
        {
            return versionRejection;
        }

        var sequence = _nextSequence;
        var eventResult = ApplyEvent(
            CompositionEvent.Update(
                _epoch,
                _nextCompositionId - 1,
                document.Version,
                sequence,
                _replacement,
                value,
                preeditCaret,
                segments),
            document);

        if (eventResult.IsRejected)
        {
            return eventResult;
        }

        _nextSequence = sequence + 1;
        _preedit = value;
        _preeditCaret = Math.Clamp(preeditCaret ?? value.Length, 0, value.Length);
        _segments = Freeze(segments);

        return CompleteAfterApply(TextInputAdapterStatus.Applied, eventResult);
    }

    /// <summary>
    /// 提交<b>已确认</b>的结果（显式结果，不做协议猜测）。
    /// </summary>
    /// <param name="token">Begin 返回的组合票据（或上一次带下一段预编辑的成功提交返回的新票据）。</param>
    /// <param name="operationId">该原生确认的单调操作编号；重复投递同一确认会被拒绝。</param>
    /// <param name="result">本段接受的已提交文本。</param>
    /// <param name="nextPreedit">
    /// 同一原生消息里紧跟结果之后的下一段预编辑；不为 null 时按“先结果、后预编辑”继续同一组合。
    /// </param>
    /// <param name="nextPreeditCaret">下一段预编辑 caret（相对 <paramref name="nextPreedit"/>）。</param>
    /// <param name="nextPreeditSegments">下一段预编辑分段。</param>
    /// <returns>显式结论；真实事务结果由核心回读决定（setter 拒绝/容量拒绝/净化都如实反映）。</returns>
    internal TextInputAdapterResult CommitResult(
        TextInputCompositionToken? token,
        long operationId,
        string? result,
        string? nextPreedit = null,
        int? nextPreeditCaret = null,
        IReadOnlyList<TextRange>? nextPreeditSegments = null)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        // 票据校验先于任何核心写入：旧组合、已退休票据、重复确认都在写入前拒绝。
        if (TryRejectToken(token, operationId, out var tokenRejection))
        {
            return tokenRejection;
        }

        if (!TryReadForWrite(out var document, out var readRejection))
        {
            return readRejection;
        }

        if (!ReconcileVersion(document, out var versionRejection))
        {
            return versionRejection;
        }

        var value = result ?? string.Empty;
        if (!IsValidCommittedText(value, out var textError))
        {
            return TextInputAdapterResult.Rejected(_snapshot, textError!);
        }

        if (nextPreedit is { } next && !IsValidPreedit(next, nextPreeditCaret, nextPreeditSegments, out var nextError))
        {
            return TextInputAdapterResult.Rejected(_snapshot, nextError!);
        }

        if (!_composing)
        {
            // 重复提交（同一段已经结束）：幂等拒绝，绝不二次写绑定。
            return TextInputAdapterResult.NoOp(_snapshot, "没有活动组合；重复结果被忽略（不会二次写入绑定）。");
        }

        var compositionId = _nextCompositionId - 1;
        var sequence = _nextSequence;
        var eventResult = ApplyEvent(
            CompositionEvent.ResultWithNextPreeditAttributes(
                _epoch,
                compositionId,
                document.Version,
                sequence,
                value,
                _replacement,
                nextPreedit,
                nextPreeditCaret,
                nextPreeditSegments),
            document);

        if (eventResult.IsRejected)
        {
            return eventResult;
        }

        _nextSequence = sequence + 1;

        // 真实回读：版本与下一段替换区一律取自实际结果，绝不用“+1”或“请求的结果长度”。
        // R7：此处已在 ApplyComposition 返回之后；回读失败不能抹掉已确认的提交。
        if (!TryReadDocument(out var readback))
        {
            return CompleteWithoutSynchronization(
                _snapshot,
                eventResult.ApplyResult,
                _token,
                "组合结果已由核心处理，但随后的文档回读失败；适配器无法同步，已撤销不可信预编辑。");
        }

        _expectedVersion = readback.Version;

        if (eventResult.Committed)
        {
            // 真实提交成功：下一段从真实回读选区重新锚定（通常是折叠 caret）。
            _replacement = readback.Selection.Range;
        }

        // 未真实提交（setter 拒绝/容量拒绝/净化后无变化）时保留原替换区，仍在同一未提交段。

        if (nextPreedit is { } continued)
        {
            _composing = true;
            _preedit = continued;
            _preeditCaret = Math.Clamp(nextPreeditCaret ?? continued.Length, 0, continued.Length);
            _segments = Freeze(nextPreeditSegments);
        }
        else
        {
            _composing = false;
            _preedit = string.Empty;
            _preeditCaret = 0;
            _segments = [];
            // 组合真正结束：退休票据（保留引用），之后携带它的任何操作都得到“已退休”的显式拒绝。
            _token?.Retire();
        }

        return CompleteAfterApply(TextInputAdapterStatus.Applied, eventResult, token: _token);
    }

    /// <summary>取消当前未提交组合（显式取消，不做协议猜测）；恢复组合前选区由核心负责。</summary>
    /// <param name="token">Begin 返回的组合票据。</param>
    /// <param name="operationId">该原生取消的单调操作编号。</param>
    internal TextInputAdapterResult Cancel(TextInputCompositionToken? token, long operationId)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        if (TryRejectToken(token, operationId, out var cancelTokenRejection))
        {
            return cancelTokenRejection;
        }

        if (!_composing)
        {
            return TextInputAdapterResult.NoOp(_snapshot, "没有活动组合；重复取消被忽略。");
        }

        if (!TryReadForWrite(out var document, out var cancelReadRejection))
        {
            return cancelReadRejection;
        }

        var compositionId = _nextCompositionId - 1;
        var sequence = _nextSequence;
        var eventResult = ApplyEvent(
            CompositionEvent.Cancel(_epoch, compositionId, document.Version, sequence),
            document);

        if (eventResult.IsRejected)
        {
            return eventResult;
        }

        _nextSequence = sequence + 1;
        _composing = false;
        _preedit = string.Empty;
        _preeditCaret = 0;
        _segments = [];
        // 退休但保留引用：重复 End/Cancel 得到“已退休”，而不是“缺少票据”。
        _token?.Retire();

        // R7：已在 ApplyComposition 返回之后；回读失败不能抹掉已确认的结束动作。
        if (!TryReadDocument(out var readback))
        {
            return CompleteWithoutSynchronization(
                _snapshot,
                eventResult.ApplyResult,
                _token,
                "组合结束已由核心处理，但随后的文档回读失败；适配器无法同步，已撤销不可信预编辑。");
        }

        _expectedVersion = readback.Version;
        _replacement = readback.Selection.Range;

        return CompleteAfterApply(TextInputAdapterStatus.Applied, eventResult, token: _token);
    }

    /// <summary>结束组合（幂等：重复 End 不会二次写入）。</summary>
    /// <param name="token">Begin 返回的组合票据。</param>
    /// <param name="operationId">该原生 End 的单调操作编号。</param>
    internal TextInputAdapterResult End(TextInputCompositionToken? token, long operationId)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        if (TryRejectToken(token, operationId, out var endTokenRejection))
        {
            return endTokenRejection;
        }

        if (!_composing)
        {
            // 幂等：已经结束/从未开始。
            return TextInputAdapterResult.NoOp(_snapshot, "没有活动组合；重复 End 被忽略。");
        }

        if (!TryReadForWrite(out var document, out var endReadRejection))
        {
            return endReadRejection;
        }

        var compositionId = _nextCompositionId - 1;
        var sequence = _nextSequence;
        var eventResult = ApplyEvent(
            CompositionEvent.End(_epoch, compositionId, document.Version, sequence),
            document);

        if (eventResult.IsRejected)
        {
            return eventResult;
        }

        _nextSequence = sequence + 1;
        _composing = false;
        _preedit = string.Empty;
        _preeditCaret = 0;
        _segments = [];
        // 退休但保留引用：重复 End/Cancel 得到“已退休”，而不是“缺少票据”。
        _token?.Retire();

        // R7：已在 ApplyComposition 返回之后；回读失败不能抹掉已确认的结束动作。
        if (!TryReadDocument(out var readback))
        {
            return CompleteWithoutSynchronization(
                _snapshot,
                eventResult.ApplyResult,
                _token,
                "组合结束已由核心处理，但随后的文档回读失败；适配器无法同步，已撤销不可信预编辑。");
        }

        _expectedVersion = readback.Version;
        _replacement = readback.Selection.Range;

        return CompleteAfterApply(TextInputAdapterStatus.Applied, eventResult, token: _token);
    }

    // ------------------------------------------------------------------ 原生选区映射

    /// <summary>
    /// 把原生/显示镜像坐标的选区映射为已提交文档选区并设置它。
    /// </summary>
    /// <param name="displaySelection">原生/显示镜像坐标中的选区（保留方向）。</param>
    /// <param name="token">有活动组合时必须携带其票据；无组合时为 null。</param>
    /// <param name="operationId">该原生选区变化的单调操作编号（有组合时用于票据水位）。</param>
    /// <remarks>
    /// <para>
    /// <b>已提交选区：</b>两端都在已提交文本上时走可选接缝 <see cref="ITextInputSelectionTarget"/>
    /// （epoch + 版本双校验），<b>保留真实方向</b>；客户端不支持时显式拒绝，不伪造成功。
    /// </para>
    /// <para>
    /// <b>预编辑内选区（R3）：</b>本批只支持“折叠 caret 落在预编辑内”这一种。非折叠选区或跨越
    /// 已提交文本/预编辑边界的范围<b>显式拒绝</b>并保持快照不变——绝不把 anchor 丢弃后假装成功。
    /// </para>
    /// </remarks>
    internal TextInputAdapterResult SetNativeSelection(
        TextSelection displaySelection,
        TextInputCompositionToken? token = null,
        long operationId = 0)
    {
        if (TryRejectBeforeApply(out var rejection))
        {
            return rejection;
        }

        if (!TryReadForWrite(out var document, out var readRejection))
        {
            return readRejection;
        }

        if (!ReconcileVersion(document, out var versionRejection))
        {
            return versionRejection;
        }

        var displayLength = _snapshot.DisplayLength;
        var anchor = Math.Clamp(displaySelection.Anchor, 0, displayLength);
        var active = Math.Clamp(displaySelection.Active, 0, displayLength);

        if (_composing)
        {
            var anchorInPreedit = IsInsidePreedit(anchor);
            var activeInPreedit = IsInsidePreedit(active);
            if (anchorInPreedit || activeInPreedit)
            {
                // 只有“折叠 caret 落在预编辑内”能映射为组合内 caret；其余形态必须显式拒绝。
                if (anchor != active)
                {
                    return TextInputAdapterResult.Rejected(
                        _snapshot,
                        "本批只支持预编辑内的折叠 caret：非折叠选区（含跨已提交/预编辑边界）被显式拒绝，快照不变。");
                }

                if (!activeInPreedit)
                {
                    return TextInputAdapterResult.Rejected(
                        _snapshot,
                        "选区端点落在预编辑之外（跨区）：本批不支持，显式拒绝，快照不变。");
                }

                // 委托给 UpdatePreedit：票据与操作编号的水位校验只在那一处消费，避免重复消费同一编号。
                var caret = PreeditOffsetOf(active);
                return UpdatePreedit(token, operationId, _preedit, caret, _segments);
            }

            // 已提交文本上的选区变化：仍需组合票据（属于同一原生会话），但不消费预编辑水位。
            if (TryRejectToken(token, operationId, out var tokenRejection))
            {
                return tokenRejection;
            }
        }

        if (_client is not ITextInputSelectionTarget target)
        {
            return TextInputAdapterResult.Rejected(
                _snapshot,
                "该客户端不支持按 epoch/版本设置已提交选区；原生选区变化被显式拒绝。");
        }

        // 保留真实方向：anchor/active 分别映射，不重新排序。
        var committed = new TextSelection(
            _snapshot.DisplayToCommitted(anchor),
            _snapshot.DisplayToCommitted(active));

        bool applied;
        try
        {
            applied = target.TrySetCommittedSelection(new TextInputSession(_epoch), _expectedVersion, committed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return TextInputAdapterResult.Rejected(_snapshot, $"设置已提交选区失败（{ex.GetType().Name}）；未确认任何写入。");
        }

        if (!applied)
        {
            return TextInputAdapterResult.Rejected(
                _snapshot,
                "已提交选区未被接受：epoch/版本不符、组合中或客户端已撤销。");
        }

        // R7：选区已经由客户端接受（返回 true），随后回读失败同样不能假称“未执行”。
        // 这里没有组合事件，因此 ApplyResult 保持 default（Handled 如实为 false），
        // 但状态明确表达“选区已应用 + 同步失败”，并由 Reason 说明。
        if (!TryReadDocument(out var readback))
        {
            return CompleteWithoutSynchronization(
                _snapshot,
                default,
                _token,
                "已提交选区已被客户端接受，但随后的文档回读失败；适配器无法同步。",
                forceAuthoritative: false);
        }

        _expectedVersion = readback.Version;
        return CompleteAfterApply(TextInputAdapterStatus.Applied, default, bumpNativeOnly: true);
    }

    /// <summary>
    /// 把已提交偏移映射到原生/显示镜像偏移（供 store 报告原生坐标）。
    /// </summary>
    internal int CommittedToNative(int committedOffset) => _snapshot.CommittedToDisplay(committedOffset);

    /// <summary>
    /// 把原生/显示镜像偏移映射回已提交偏移。
    /// </summary>
    internal int NativeToCommitted(int nativeOffset) => _snapshot.DisplayToCommitted(nativeOffset);

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 刷新真实回读并重建快照（R2：不得把旧预编辑拼进新权威文档）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>关闭后不再读取客户端</b>：直接返回最后一次快照，避免在宿主释放后触碰已失效的客户端。
    /// </para>
    /// <para>
    /// <b>版本冲突不能被“刷新”消除</b>：若真实版本与期望版本不一致（外部绑定变化、核心已同步取消组合），
    /// 本方法<b>撤销</b>本地预编辑镜像并标记失效，绝不返回 <c>IsComposing=true</c> 加旧预编辑。
    /// </para>
    /// <para>
    /// <b>核心本地取消</b>（用户按 Esc、宿主取消、失焦）：客户端报告 <c>IsComposing=false</c> 时同样撤销镜像，
    /// 即使版本没变。
    /// </para>
    /// <para>真实变化推进对应修订号；没有任何变化时修订号保持不变。</para>
    /// </remarks>
    /// <summary>普通核心输入完成后的窄同步入口；不复活失效会话，不接受活动组合。</summary>
    /// <remarks>
    /// <para>
    /// <b>只读、不写绑定、不发事件：</b>普通按键字符已经由核心事务写进文档；这里只是让 TSF 权威基线
    /// （期望版本 + 替换区 + 快照）追上真实回读，否则下一次原生结果会在 <see cref="ReconcileVersion"/>
    /// 处被当成外部失效而拒绝。
    /// </para>
    /// <para>
    /// <b>只清理本地镜像，不清失效状态：</b><see cref="_stale"/>/<see cref="_closed"/> 是粘性的，
    /// 这里<b>绝不</b>清除；活动组合或客户端仍报告组合时同样不采纳（预编辑必须走 Update）。
    /// </para>
    /// </remarks>
    internal bool TryAdoptCommittedBaseline(out TextInputAuthoritativeSnapshot snapshot)
    {
        snapshot = _snapshot;
        if (_closed || _stale || _composing || _applying != 0 || !SafeIsCurrentOwner()
            || ReadClientComposing() || !TryReadDocument(out var document))
        {
            return false;
        }

        var previous = _snapshot;
        var next = BuildSnapshot(document);
        if (HasObservableMirrorChange(previous, next))
        {
            _mirrorRevision++;
            next = BuildSnapshot(document);
        }

        if (HasObservableNativeChange(previous, next))
        {
            _nativeRevision++;
            next = BuildSnapshot(document);
        }

        _expectedVersion = document.Version;
        _replacement = document.Selection.Range;
        _snapshot = next;
        snapshot = _snapshot;
        return true;
    }

    internal TextInputAuthoritativeSnapshot Refresh()
    {
        // R2/R6：关闭或已失效后不再读取客户端；镜像必须撤销，绝不保留旧预编辑。
        if (_closed)
        {
            return _snapshot;
        }

        if (_stale)
        {
            DiscardMirror();
            _snapshot = BuildSnapshot(new CompositionDocumentView(_snapshot.Committed));
            return _snapshot;
        }

        // R2/R6：归属谓词在回读之前求值；不再归属时不触碰客户端。
        if (!SafeIsCurrentOwner())
        {
            _stale = true;
            DiscardMirror();
            _snapshot = BuildSnapshot(new CompositionDocumentView(_snapshot.Committed));
            return _snapshot;
        }

        var previous = _snapshot;
        if (!TryReadDocument(out var document))
        {
            // 回读失败：显式失效并撤销镜像，不把失败包装成乐观结果。
            _stale = true;
            DiscardMirror();
            _snapshot = BuildSnapshot(new CompositionDocumentView(previous.Committed));
            return _snapshot;
        }

        // 版本冲突（外部绑定变化）与核心本地取消（Esc/宿主取消/失焦）都撤销镜像；
        // “刷新”不得消除活动段的版本冲突。
        var versionConflict = document.Version != _expectedVersion;
        var coreCancelled = _composing && !ReadClientComposing();
        if (versionConflict || coreCancelled)
        {
            _stale = true;
            DiscardMirror();
        }
        else
        {
            _expectedVersion = document.Version;
        }

        // R2：修订号必须反映<b>真实可观察状态</b>变化（文本/选区/预编辑/caret/分段/组合标记），不只是文本。
        var next = BuildSnapshot(document);
        if (HasObservableMirrorChange(previous, next))
        {
            _mirrorRevision++;
        }

        if (HasObservableNativeChange(previous, next))
        {
            _nativeRevision++;
        }

        _snapshot = BuildSnapshot(document);
        return _snapshot;
    }

    /// <summary>镜像可观察变化：显示串、预编辑、caret、分段或组合标记。</summary>
    private static bool HasObservableMirrorChange(
        TextInputAuthoritativeSnapshot previous,
        TextInputAuthoritativeSnapshot next)
        => !string.Equals(previous.DisplayText, next.DisplayText, StringComparison.Ordinal)
            || previous.PreeditCaret != next.PreeditCaret
            || previous.Replacement != next.Replacement
            || previous.IsComposing != next.IsComposing
            || !SegmentsEqual(previous.Segments, next.Segments);

    /// <summary>原生可观察变化：镜像变化之外还包括已提交文本/版本/选区。</summary>
    private static bool HasObservableNativeChange(
        TextInputAuthoritativeSnapshot previous,
        TextInputAuthoritativeSnapshot next)
        => HasObservableMirrorChange(previous, next)
            || previous.Committed.Version != next.Committed.Version
            || previous.Committed.Selection != next.Committed.Selection;

    private static bool SegmentsEqual(IReadOnlyList<TextRange> left, IReadOnlyList<TextRange> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>撤销本地预编辑镜像（不写绑定、不发事件）：组合状态回到“无活动段”。</summary>
    /// <remarks>票据只退休、不清空引用：迟到的旧操作因此得到“已退休”的明确原因。</remarks>
    private void DiscardMirror()
    {
        _composing = false;
        _preedit = string.Empty;
        _preeditCaret = 0;
        _segments = [];
        _token?.Retire();
    }

    /// <summary>客户端当前是否仍处于组合状态；读取失败按“已取消”处理（不保留不可信镜像）。</summary>
    private bool ReadClientComposing()
    {
        try
        {
            return _client.IsComposing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>标记本会话失效（归属被撤销/宿主释放）：之后所有操作显式拒绝，不写入。</summary>
    internal void MarkStale() => _stale = true;

    /// <summary>
    /// 显式关闭会话（宿主释放/窗口关闭）：之后所有操作幂等拒绝，且不再回读客户端。
    /// </summary>
    /// <remarks>
    /// 关闭不写绑定、不发组合事件：未提交预编辑由核心的会话结束路径取消，这里只停止本适配器写入。
    /// </remarks>
    internal void Close()
    {
        _closed = true;
        DiscardMirror();
    }

    /// <summary>
    /// 校验组合票据与操作编号（R1）：必须与当前活动组合是<b>同一个实例</b>、未退休，且编号严格递增。
    /// </summary>
    /// <remarks>
    /// 用引用相等而不是内容比较：旧组合结束、新组合开始后，旧票据不可能等于新票据，因此旧结果/旧取消
    /// 无法被套上新身份。同一原生确认被重复投递时编号相同 → 拒绝。两次合法的相同结果各有独立编号 → 都接受。
    /// </remarks>
    private bool TryRejectToken(
        TextInputCompositionToken? token,
        long operationId,
        out TextInputAdapterResult rejection)
    {
        rejection = default;

        if (token is null)
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "操作缺少组合票据：必须携带 Begin（或上一次成功提交）返回的票据。");
            return true;
        }

        if (!ReferenceEquals(token, _token))
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "组合票据不是当前活动组合：旧组合或已结束组合的操作不写入。");
            return true;
        }

        if (token.IsRetired)
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "组合票据已退休（该组合已结束/取消）：重复或迟到的操作不写入。");
            return true;
        }

        if (operationId <= 0)
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "操作编号非法（必须为正的单调编号）：不能按内容或时间推断归属。");
            return true;
        }

        if (!token.TryAccept(operationId))
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                $"操作编号 {operationId} 未超过该组合已接受的水位；重复投递的同一确认不写入。");
            return true;
        }

        return false;
    }

    private bool TryRejectBeforeApply(out TextInputAdapterResult rejection)
    {
        rejection = default;

        if (_closed || _stale)
        {
            rejection = TextInputAdapterResult.Rejected(_snapshot, "该输入会话已结束或归属已撤销；操作不写入。");
            return true;
        }

        if (_applying > 0)
        {
            rejection = TextInputAdapterResult.Rejected(_snapshot, "组合回调重入被拒绝：不得在回调内同步再投组合事件。");
            return true;
        }

        // 归属判定：旧捕获绝不能写入新会话。
        bool current;
        try
        {
            current = _isCurrentOwner(_client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _stale = true;
            rejection = TextInputAdapterResult.Rejected(_snapshot, $"归属判定失败（{ex.GetType().Name}）；操作不写入。");
            return true;
        }

        if (!current)
        {
            _stale = true;
            rejection = TextInputAdapterResult.Rejected(_snapshot, "该客户端已不是当前焦点归属；旧捕获不写入。");
            return true;
        }

        return false;
    }

    private bool ReconcileVersion(CompositionDocumentView document, out TextInputAdapterResult rejection)
    {
        rejection = default;
        if (document.Version == _expectedVersion)
        {
            return true;
        }

        // 外部绑定/来源变化：期望版本已经失效，旧锚点不得写入。
        _stale = true;
        _snapshot = BuildSnapshot(document);
        rejection = TextInputAdapterResult.Rejected(
            _snapshot,
            $"期望文档版本 v{_expectedVersion} 与真实回读 v{document.Version} 不一致；外部变化后旧锚点不写入。");
        return false;
    }

    /// <summary>投递一个事件批次并返回真实结果；不抛异常。</summary>
    /// <remarks>
    /// 框架返回的 <see cref="TextInputApplyResult"/> 是权威：批次里没有任何事件被消费（<c>Handled=false</c>，
    /// 例如序号未递增、版本过期、重复 End）时这里映射为<b>显式拒绝</b>，绝不把“投递成功”当成“已应用”。
    /// </remarks>
    private TextInputAdapterResult ApplyEvent(CompositionEvent compositionEvent, CompositionDocumentView document)
    {
        _ = document;
        _applying++;
        try
        {
            var applyResult = _client.ApplyComposition([compositionEvent]);
            if (!applyResult.Handled)
            {
                var reason = applyResult.RejectionReason ?? "框架未消费该组合事件（身份/序号/版本不匹配）。";
                return TextInputAdapterResult.Rejected(_snapshot, reason);
            }

            return TextInputAdapterResult.Applied(TextInputAdapterStatus.Applied, _snapshot, applyResult);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 原生回调路径不允许托管异常越界；原因不含用户原文。
            return TextInputAdapterResult.Rejected(
                _snapshot,
                $"组合事件投递失败（{ex.GetType().Name}）；未确认任何写入。");
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>
    /// 投递后重建快照并按真实变化推进镜像/原生修订号。
    /// </summary>
    /// <remarks>
    /// <b>R7：</b>本方法只在 <see cref="ITextInputClient.ApplyComposition"/> 已经返回之后运行。后置回读失败或
    /// 归属变化时<b>绝不</b>把结论降级为 <see cref="TextInputAdapterStatus.Rejected"/>——那会让宿主以为没有发生
    /// 任何写入而重复插入字符。此时保留真实 <see cref="TextInputApplyResult"/>（含 Handled/ResultCommitted/
    /// AppliedEventCount 与权威 Document），标记会话失效、退休票据、撤销不可信预编辑镜像，
    /// 并用 <see cref="TextInputAdapterStatus.AppliedSynchronizationFailed"/> 明确报告“已执行 + 同步失败”。
    /// </remarks>
    private TextInputAdapterResult CompleteAfterApply(
        TextInputAdapterStatus status,
        TextInputAdapterResult applied,
        bool bumpNativeOnly = false,
        TextInputCompositionToken? token = null)
    {
        var previous = _snapshot;
        var confirmed = applied.ApplyResult;

        if (!TryReadDocument(out var document))
        {
            return CompleteWithoutSynchronization(
                previous,
                confirmed,
                token,
                "组合事件已由核心处理，但随后的文档回读失败；适配器无法同步，已撤销不可信预编辑。");
        }

        if (status != TextInputAdapterStatus.Rejected)
        {
            _expectedVersion = document.Version;
        }

        var next = BuildSnapshot(document);

        // R2：修订号只反映真实可观察状态（显示串/预编辑/caret/分段/组合标记），不只是文本。
        // bumpNativeOnly（纯选区设置）不推进镜像修订，但选区变化仍推进原生修订。
        var mirrorChanged = !bumpNativeOnly && HasObservableMirrorChange(previous, next);
        if (mirrorChanged)
        {
            _mirrorRevision++;
        }

        if (mirrorChanged || HasObservableNativeChange(previous, next))
        {
            _nativeRevision++;
        }

        _snapshot = BuildSnapshot(document);

        // 归属可能在回调内被撤销（setter 内换焦点/卸载）：已确认的结果仍如实保留（R7）。
        if (!SafeIsCurrentOwner())
        {
            return CompleteWithoutSynchronization(
                _snapshot,
                confirmed,
                token,
                "组合事件已由核心处理，但处理期间归属已被撤销（焦点切换/卸载）；已确认结果保留，后续同步不可用。");
        }

        return TextInputAdapterResult.Applied(status, _snapshot, confirmed, token);
    }

    /// <summary>
    /// 已确认执行但无法继续同步时的统一收尾（R7）：保留真实结果，标记失效并撤销不可信镜像。
    /// </summary>
    /// <param name="fallback">同步失败时用于构建快照的基准（已确认结果里没有可用 Document 时才使用）。</param>
    /// <param name="confirmed">核心返回的真实结果；其 <c>Document</c> 是权威快照。</param>
    /// <param name="token">当前票据（会被退休）。</param>
    /// <param name="reason">同步失败原因（脱敏，不含用户原文）。</param>
    /// <remarks>
    /// <para>
    /// <b>不伪造版本递增：</b>快照使用 <see cref="TextInputApplyResult.Document"/>（核心真实回读值），
    /// 不是适配器自己 +1。
    /// </para>
    /// <para>
    /// <b>不重放：</b>不重新投递事件、不把结果回退成“未处理”，也不尝试补偿写入。
    /// </para>
    /// </remarks>
    private TextInputAdapterResult CompleteWithoutSynchronization(
        TextInputAuthoritativeSnapshot fallback,
        TextInputApplyResult confirmed,
        TextInputCompositionToken? token,
        string reason,
        bool forceAuthoritative = true)
    {
        // 会话已不可信：后续任何操作都在核心写入前被拒绝。
        _stale = true;

        // 预编辑镜像不可信：撤销（票据退休，迟到操作得到“已退休”）。
        DiscardMirror();

        // 权威快照优先取核心确认的 Document；只有它不可用（或调用方明确要求不使用）时才退回基准，
        // 绝不自行推进版本。
        var useConfirmed = forceAuthoritative && (confirmed.Handled || !confirmed.IsNoOp);
        var authoritative = useConfirmed
            ? new CompositionDocumentView(confirmed.Document)
            : new CompositionDocumentView(fallback.Committed);

        _snapshot = BuildSnapshot(authoritative);
        return TextInputAdapterResult.AppliedSynchronizationFailed(_snapshot, confirmed, reason, token);
    }

    private bool SafeIsCurrentOwner()
    {
        try
        {
            return _isCurrentOwner(_client);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private TextInputAuthoritativeSnapshot BuildSnapshot(CompositionDocumentView document)
    {
        var committed = document.Snapshot;
        var replacement = _composing ? _replacement : new TextRange(0, 0);
        var preedit = _composing ? _preedit : string.Empty;
        var caret = _composing ? _preeditCaret : 0;
        return TextInputAuthoritativeSnapshot.Create(
            committed,
            replacement,
            preedit,
            caret,
            _composing ? _segments : null,
            _mirrorRevision,
            _nativeRevision,
            _composing);
    }

    /// <summary>
    /// 真实回读客户端文档，并<b>显式报告成功与否</b>（R6）。
    /// </summary>
    /// <remarks>
    /// 失败时标记失效并返回 false：调用方<b>不得</b>用最后一次快照继续确认写入，也不能把失败包装成乐观结果。
    /// 成功时返回的文档来自真实回读（可能已由用户 getter 触发焦点/绑定变化，调用方必须再校验归属）。
    /// </remarks>
    private bool TryReadDocument(out CompositionDocumentView document)
    {
        try
        {
            document = new CompositionDocumentView(_client.Document);
            return true;
        }
        catch (Exception)
        {
            _stale = true;
            // 构造期回读失败时 _snapshot 尚未赋值：退回空快照而不是解引用 null。
            document = new CompositionDocumentView(_snapshot?.Committed ?? TextSnapshot.Empty);
            return false;
        }
    }

    /// <summary>
    /// 回读之后、写入之前的统一重新校验（R5/R6）：读回成功、未关闭/未失效、归属仍有效、非重入。
    /// </summary>
    /// <remarks>
    /// 用户 getter 可能同步切换焦点、卸载或改写绑定；每次可重入读回后都必须重新求值这些条件，
    /// 不能只比较版本。
    /// </remarks>
    private bool TryRevalidateAfterRead(out TextInputAdapterResult rejection)
    {
        rejection = default;

        if (_closed || _stale)
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "文档回读失败或会话已失效；不再投递任何写事件。");
            return false;
        }

        if (_applying > 0)
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "组合回调重入被拒绝：不得在回调内同步再投组合事件。");
            return false;
        }

        if (!SafeIsCurrentOwner())
        {
            _stale = true;
            DiscardMirror();
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "文档回读期间归属已被撤销（焦点切换/卸载）；旧捕获不写入。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 写路径的统一入口（R5/R6）：真实回读 → 回读成功校验 → 归属/关闭/重入再校验。
    /// </summary>
    /// <remarks>
    /// 只有两个校验都通过才把文档交给调用方继续；任何失败都返回显式拒绝，绝不进入 <c>ApplyComposition</c>。
    /// </remarks>
    private bool TryReadForWrite(out CompositionDocumentView document, out TextInputAdapterResult rejection)
    {
        if (!TryReadDocument(out document))
        {
            rejection = TextInputAdapterResult.Rejected(
                _snapshot,
                "文档回读失败；不再投递任何写事件。");
            return false;
        }

        return TryRevalidateAfterRead(out rejection);
    }

    private bool IsInsidePreedit(int displayOffset)
        => _composing
            && displayOffset >= _snapshot.DisplayPreeditStart
            && displayOffset < _snapshot.DisplayPreeditEnd;

    private int PreeditOffsetOf(int displayOffset)
        => Math.Clamp(displayOffset - _snapshot.DisplayPreeditStart, 0, _preedit.Length);

    private static bool IsValidReplacement(TextRange range, CompositionDocumentView document, out string? error)
    {
        if (!range.IsWithin(document.Text.Length))
        {
            error = $"替换区 {range} 超出真实文档 [0, {document.Text.Length}]；不写入。";
            return false;
        }

        if (!TextGraphemeBoundaries.AllAreBoundaries(document.Text, [range.Start, range.End], out var invalid))
        {
            error = $"替换区 {range} 的端点 {invalid} 不在已提交文档的字素边界上；不拆字写入。";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsValidPreedit(string? text, int? caret, IReadOnlyList<TextRange>? segments, out string? error)
    {
        var value = text ?? string.Empty;
        if (HasUnpairedSurrogate(value))
        {
            error = "预编辑串包含未配对代理项，不能用于显示或写入。";
            return false;
        }

        if (caret is { } position && (position < 0 || position > value.Length))
        {
            error = $"组合内 caret {position} 超出预编辑串 [0, {value.Length}]。";
            return false;
        }

        if (segments is null)
        {
            error = null;
            return true;
        }

        var previousEnd = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!segment.IsWithin(value.Length) || segment.Start < previousEnd)
            {
                error = $"分段 {segment} 超出预编辑串或与前一段重叠（前一段结束于 {previousEnd}）。";
                return false;
            }

            previousEnd = segment.End;
        }

        error = null;
        return true;
    }

    private static bool IsValidCommittedText(string text, out string? error)
    {
        if (HasUnpairedSurrogate(text))
        {
            error = "结果文本包含未配对代理项，不能写入文档。";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>复制为不可变集合：防御输入列表被事后改写。</summary>
    private static ImmutableArray<TextRange> Freeze(IReadOnlyList<TextRange>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TextRange>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            builder.Add(segments[i]);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>已提交文档视图（只含适配器需要的真实回读字段）。</summary>
    private readonly record struct CompositionDocumentView(TextSnapshot Snapshot)
    {
        public string Text => Snapshot.Text;

        public int Version => Snapshot.Version;

        public TextSelection Selection => Snapshot.Selection;
    }
}
