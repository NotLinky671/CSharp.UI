namespace CSharp.UI;

/// <summary>
/// 文本编辑控件的文本输入客户端（TI-04，TE-02 泛化）：把组合事件应用到节点的编辑会话与绑定上。
/// </summary>
/// <remarks>
/// <para>
/// 客户端只在 UI 线程被调用，由焦点/生命周期接线（编辑控件与运行时）创建、绑定到
/// <see cref="ITextInputService"/> 并在失焦、禁用、卸载或窗口失活时撤销。epoch 在创建时固化：撤销后的客户端
/// 永远报告自己的旧 epoch，不会动态指向新会话；所有写入路径在撤销后只返回显式拒绝。
/// </para>
/// <para>
/// <b>预编辑与 Binding/历史分离：</b>预编辑只存在于 <see cref="TextFieldComposition"/> 的小状态里，
/// 不写绑定也不进入历史；只有 <see cref="CompositionEventKind.Result"/> 才通过节点既有的 TI-03 原子事务
/// 写入一次绑定，且只报告真实事务结果（setter 拒绝 / 净化后无变化 / MaxLength 全拒绝都不算提交）。
/// </para>
/// <para>
/// <b>事件前同步：</b>每个组合事件在验证之前都从真实绑定回读并同步会话；外部文本/来源/MaxLength 变化会先取消
/// 未提交段并请求换 epoch，绝不会先执行结果再发现失效。
/// </para>
/// <para>
/// <b>不重入布局：</b>所有路径只调用节点私有的真实事务入口与内部状态同步；不执行布局、不生成帧、
/// 不产生平台副作用，也不在组合回调里递归处理待办工作。
/// </para>
/// <para>
/// <b>控件解耦（TE-02）：</b>控件相关状态与事务入口经内部窄接口 <see cref="ITextEditSessionOwner"/>
/// 访问；客户端不依赖具体控件类型（TextField/TextEditor 同一客户端）。
/// </para>
/// </remarks>
internal sealed class TextCompositionClient : ITextInputClient, ICompositionCharGeometrySource, ICompositionIdentitySource, IGeometryPublishInvalidationSink, ITextInputSelectionTarget
{
    private readonly ViewNode _node;
    private readonly ITextEditSessionOwner _state;
    private readonly TextFieldComposition _composition = new();
    private readonly TextFieldKeyOwnership _keyOwnership = new();
    private readonly int _epoch;

    /// <summary>正在应用的批次深度；服务在提交回调里同步重入时直接拒绝，避免递归写入。</summary>
    private int _applying;

    /// <summary>是否成功发布过几何（用于“隐藏时撤销锚点”与同值去重）。</summary>
    private bool _hasPublishedGeometry;

    /// <summary>最近一次请求发布的几何；同值不重复调用平台服务。</summary>
    private TextInputGeometry _lastPublishedGeometry;

    /// <summary>当前有效几何：与最近一次发布给服务/实际显示一致；释放/失效时查询返回不可用。</summary>
    private TextInputGeometry _currentGeometry = TextInputGeometry.Unavailable;

    /// <summary>是否已有有效几何（首次发布前查询为不可用）。</summary>
    private bool _hasCurrentGeometry;

    /// <summary>最后一次成功回读的可靠文档；绑定 getter/setter 抛错时用它兜底，绝不在回调里再次调用抛错 getter。</summary>
    private CompositionDocument? _lastReliableDocument;

    /// <summary>
    /// 最终布局捕获的组合字符几何快照（R17）：只在安全阶段由视图重建/清空，
    /// 平台逐字符位置查询只读它，绝不在回调里重新排版。
    /// </summary>
    private CompositionCharGeometrySnapshot? _charGeometry;

    internal TextCompositionClient(ViewNode node, ITextEditSessionOwner state, int epoch)
    {
        _node = node;
        _state = state;
        _epoch = epoch;
    }

    /// <summary>组合状态机（测试与 TI-06 读取显示串映射）。</summary>
    internal TextFieldComposition Composition => _composition;

    /// <summary>宿主撤销焦点归属后为 true；此后旧回调都只返回拒绝，不再写入。</summary>
    internal bool IsReleased { get; set; }

    /// <summary>该客户端创建时固化的焦点 epoch；撤销后不会改变。</summary>
    internal int Epoch => _epoch;

    // 组合身份只读接缝（诊断/验收）：不扩张公开 ITextInputClient 契约。
    int ICompositionIdentitySource.Epoch => _epoch;

    int ICompositionIdentitySource.CompositionId => _composition.CompositionId;

    bool ICompositionIdentitySource.IsComposing => _composition.IsComposing;

    /// <summary>检测到来源/绑定变化、需要换 epoch 时置位；由生命周期接线在安全阶段消费。</summary>
    internal bool EpochInvalidationRequested { get; private set; }

    /// <inheritdoc />
    public TextSnapshot Document => CurrentDocument().Snapshot;

    /// <inheritdoc />
    /// <remarks>epoch 在创建时固化；释放后仍返回旧 epoch（旧引用不能伪装成新会话）。</remarks>
    public TextInputSession Session => new(_epoch);

    /// <inheritdoc />
    public bool IsComposing => _composition.IsComposing;

    /// <inheritdoc />
    public string Preedit => _composition.Preedit;

    /// <summary>当前显示串（已提交前缀 + 预编辑 + 已提交后缀）；TI-06 用于绘制与命中。</summary>
    public string DisplayString => _composition.IsComposing ? _composition.DisplayString : _state.Editor.Text;

    /// <summary>把显示串偏移映射为文档位置（TI-06 消费）。</summary>
    public TextCompositionDisplayMap MapDisplayToDocument(int displayOffset) => _composition.MapDisplayToDocument(displayOffset);

    /// <summary>
    /// 在安全阶段（最终布局后）重建/清空逐字符几何快照；与锚点发布同帧同源（R17）。
    /// 传入 null 表示当前没有可用的字符几何（裁出/不可用）。
    /// </summary>
    internal void UpdateCompositionCharGeometry(CompositionCharGeometrySnapshot? snapshot)
        => _charGeometry = snapshot;

    /// <inheritdoc />
    /// <remarks>
    /// 快照全部身份键匹配才有效：epoch（释放/换 epoch 后旧快照隔离）、当前预编辑串
    /// （组合回调更新后、下一次安全阶段重建前的旧快照不得应答）、已提交文档版本（外部写入失效）、
    /// 布局失效序号与横向滚动（字体/环境/DPI/祖先滚动/显式失效到重建之间的旧快照立即拒绝，
    /// 绝不用旧排版应答）；仅按核心状态核对，不触发任何排版/布局。
    /// </remarks>
    CompositionCharGeometrySnapshot? ICompositionCharGeometrySource.CompositionCharGeometry
        => _charGeometry is { } snapshot
            && !IsReleased
            && !EpochInvalidationRequested
            && snapshot.Epoch == _epoch
            && snapshot.DocumentVersion == _state.Editor.Version
            && snapshot.LayoutSerial == _node.Runtime.LayoutSerial
            && snapshot.ScrollX == _state.ScrollX
            && snapshot.ScrollY == _state.ScrollY
            && snapshot.Affinity == _state.CaretAffinity
            && string.Equals(snapshot.Preedit, _composition.Preedit, StringComparison.Ordinal)
            && (snapshot.Preedit.Length > 0
                || snapshot.CaretIndex == snapshot.Layout.SnapToCaretStop(
                    Math.Clamp(_state.Caret, 0, _state.Editor.Text.Length)))
            ? snapshot
            : null;

    /// <summary>当前预编辑 caret（相对预编辑串的 UTF-16 偏移）。</summary>
    public int PreeditCaret => _composition.PreeditCaret;

    /// <summary>当前预编辑分段（不可变快照）。</summary>
    public IReadOnlyList<TextRange> Segments => _composition.Segments;

    /// <summary>成功提交的真实事务次数（一次结果事件只提交一次）。</summary>
    internal int CommitCount => _composition.CommitCount;

    /// <inheritdoc />
    public TextInputState GetState()
    {
        var document = CurrentDocument();
        // 客户端查询与发布给服务的几何必须同源：不在此处排版，也不返回旧锚点。
        // 被释放/外部失效的客户端一律报告不可用，避免 TI-07 用陈旧 caret 定位候选窗。
        var geometry = IsReleased || EpochInvalidationRequested || !_hasCurrentGeometry
            ? TextInputGeometry.Unavailable
            : _currentGeometry;
        return new TextInputState(document.Snapshot, _composition.Preedit, geometry, _composition.IsComposing);
    }

    /// <inheritdoc />
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => _state.InputSession.ApplyFromClient(this, events);

    /// <inheritdoc />
    public bool CancelComposition() => CancelComposition(restoreSelection: true);

    /// <summary>取消未提交段；<paramref name="restoreSelection"/> 为 false 时只按新文档夹取（外部变化路径）。</summary>
    internal bool CancelComposition(bool restoreSelection)
    {
        if (IsReleased)
        {
            return false;
        }

        var document = CurrentDocument();
        if (!_composition.Cancel(document, restoreSelection, out var restored))
        {
            return false;
        }

        ApplyRestoredSelection(restored);

        // 宿主取消恢复选区后，返回/诊断快照必须等于真实文档（含恢复后的选区），不能停留在组合前锚点。
        _composition.RecordDocument(CurrentCompositionDocument());
        _keyOwnership.Reset();
        _state.InputSession.RequestNativeCancel();
        _node.Runtime.InvalidateLayout(_node);
        return true;
    }

    /// <summary>外部绑定/来源变化：取消未提交段（不恢复旧选区）并请求换新 epoch。</summary>
    internal void InvalidateForExternalChange()
    {
        if (IsReleased)
        {
            return;
        }

        EpochInvalidationRequested = true;
        if (_composition.IsComposing)
        {
            _composition.Cancel(CurrentCompositionDocument(), restoreSelection: false, out _);
        }

        _keyOwnership.Reset();
    }

    /// <summary>真正应用一个批次；只由 <see cref="TextInputServiceSession"/> 在归属有效时调用。</summary>
    /// <remarks>
    /// 不抛出托管异常：格式/顺序/绑定异常都转成显式结果并记录不含用户原文的诊断，原生回调路径不允许异常越界。
    /// 绑定 getter/setter 抛错时使用最后可靠快照，绝不在错误处理里再次调用抛错 getter。
    /// </remarks>
    internal TextInputApplyResult ApplyValidated(IReadOnlyList<CompositionEvent> events)
    {
        if (IsReleased)
        {
            return TextInputApplyResult.Rejected(LastReliableSnapshot, "该文本输入客户端已被焦点/生命周期接线撤销；旧事件不写入。");
        }

        if (EpochInvalidationRequested)
        {
            // 外部绑定/来源变化已经立即撤销旧 epoch（逻辑失效不延迟到安全阶段）；旧事件不写入。
            return TextInputApplyResult.Rejected(LastReliableSnapshot, "外部绑定/来源变化已立即撤销旧 epoch；旧事件不写入。");
        }

        if (_applying > 0)
        {
            return TextInputApplyResult.Rejected(LastReliableSnapshot, "组合回调重入被拒绝：服务不得在提交回调内同步再投组合事件。");
        }

        _applying++;
        try
        {
            // 任何组合阶段（Start/Update/Result/Cancel/End）都是合并分组边界；预编辑更新不入历史，
            // 每个真实 Result 由 ApplyCompositionCommit 各自记录一项。
            _state.History.BreakMerge();

            var result = _composition.Apply(
                events,
                _epoch,
                CommitResult,
                CurrentDocument,
                () => !IsReleased && !EpochInvalidationRequested && _state.InputSession.IsCurrentOwner(this));
            _keyOwnership.IsComposing = _composition.IsComposing;
            if (_composition.LastResultWriteFailed)
            {
                // 绑定 setter/getter 抛错：原生回调路径不越界，只记录不含用户原文的诊断。
                _node.Runtime.ReportDiagnostic($"组合事件应用失败（节点 {_node.ShortDebugPath()}）：绑定写入异常");
            }

            // 预编辑出现、变化或消失都需要一次最终布局：组合回调内只标记失效（不执行排版、不生成帧），
            // 由安全阶段重排后绘制显示串/下划线/组合 caret，并重新保证组合 caret 可见。
            if (_node.IsMounted
                && (result.Outcome & (TextInputOutcome.Started
                    | TextInputOutcome.Updated
                    | TextInputOutcome.Cancelled
                    | TextInputOutcome.Ended
                    | TextInputOutcome.ResultCommitted)) != 0)
            {
                _node.Runtime.InvalidateLayout(_node);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 事务/绑定异常已经由状态机与 ApplyCompositionCommit 恢复真实文档；这里只记录脱敏诊断并返回拒绝。
            // 不再次调用 CurrentDocument（可能重复抛错），使用状态机最后一次可靠快照。
            // 历史上下文已不可信，必须实际失效；不触碰组合消费者自己的失效标志。
            _state.History.Clear();
            _node.Runtime.ReportDiagnostic($"组合事件应用失败（节点 {_node.ShortDebugPath()}）：{ex.GetType().Name}");
            return TextInputApplyResult.Rejected(_composition.LastDocument, "组合事件应用失败；未确认任何写入。");
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>
    /// 发布候选锚点几何（TI-06）：只接受当前有效归属；同值不重复调用平台服务。
    /// </summary>
    /// <remarks>
    /// 调用方必须在最终布局后的安全阶段调用（运行时 <c>ProcessPendingWork</c>），不得在 Build/Measure/Emit
    /// 或原生组合回调内调用。隐藏/完全裁出时由调用方发布 <see cref="TextInputGeometry.Unavailable"/>，
    /// 这里同样按同值去重，避免持续唤醒平台。
    /// </remarks>
    internal bool PublishGeometry(TextInputGeometry geometry)
    {
        if (IsReleased || EpochInvalidationRequested)
        {
            return false;
        }

        _currentGeometry = geometry;
        _hasCurrentGeometry = true;

        // 不可用几何（裁出/隐藏）意味着逐字符快照同样失效：立即清空，绝不保留旧几何（R17）。
        if (!geometry.HasCaret)
        {
            _charGeometry = null;
        }

        if (_hasPublishedGeometry && _lastPublishedGeometry == geometry)
        {
            return false;
        }

        _hasPublishedGeometry = true;
        _lastPublishedGeometry = geometry;
        return _state.InputSession.PublishGeometry(this, geometry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 窗口 Move/Resize/DPI/失活恢复等平台失效不改变逻辑几何，但平台缓存已清空；
    /// 只重置“已发布”去重标记，让下一次安全阶段的同值 <see cref="PublishGeometry"/> 真正调用服务（R7）。
    /// 当前有效几何保留：逻辑锚点仍真实，不能用 Unavailable 冒充。
    /// </remarks>
    void IGeometryPublishInvalidationSink.InvalidatePublishedGeometry()
    {
        _hasPublishedGeometry = false;
        _lastPublishedGeometry = default;
    }

    /// <summary>是否已经向平台发布过几何（撤销锚点只需要在发布过时发生）。</summary>
    internal bool HasPublishedGeometry => _hasPublishedGeometry;

    /// <summary>
    /// 归属被撤销后标记几何失效（不调用平台）：使 <see cref="GetState"/> 立即不返回旧锚点；
    /// 平台撤销通知由会话接线在“先失效”之后、代际保护下发出。
    /// </summary>
    internal void MarkGeometryRevoked()
    {
        _currentGeometry = TextInputGeometry.Unavailable;
        _hasCurrentGeometry = false;
        _charGeometry = null;
    }

    /// <summary>
    /// 组合期间编辑命令/快捷键的显式按键归属：组合进行中（或有未消费的结束键）时返回
    /// <see cref="TextInputKeyDecision.Consume"/>，让平台输入法先处理方向键、Backspace/Delete、空格、Enter/Escape。
    /// </summary>
    public TextInputKeyDecision ShouldOwnInputKey(object nativeKeyToken)
    {
        _keyOwnership.IsComposing = _composition.IsComposing;
        return _keyOwnership.ShouldOwn(nativeKeyToken);
    }

    /// <summary>
    /// 在原生按键进入 <c>KeyDown</c> 之前记录“该次按键由当前输入消息拥有”。
    /// </summary>
    /// <param name="nativeKeyToken">原生按键出现的身份 token（同一次按键的 KeyDown 必须传同一个实例）。</param>
    /// <param name="consumedByInputMethod">输入法是否已经消费该按键（例如选词 Enter / 取消 Escape）。</param>
    /// <returns>控件是否应在本次 KeyDown 中放弃该按键（true = 不再执行编辑命令）。</returns>
    /// <remarks>
    /// 该接缝覆盖“组合在同一原生按键内结束、随后仍进入 KeyDown”的情况：此时
    /// <see cref="IsComposing"/> 已经是 false，不能只检查它。语义由调用方声明，不做字符串/时间窗猜测。
    /// </remarks>
    public bool TrackOriginatingKey(object nativeKeyToken, bool consumedByInputMethod)
    {
        _keyOwnership.IsComposing = _composition.IsComposing;
        return _keyOwnership.Track(nativeKeyToken, consumedByInputMethod);
    }

    /// <summary>
    /// 该 token 是否被<b>显式</b>声明为输入法已消费（一次性）；不把“当前正在组合”算作显式消费。
    /// </summary>
    /// <remarks>
    /// 命令键需要这个更精确的判定：输入法明确消费的同一次原生按键（包括带命令修饰符的按键）必须让给输入法；
    /// 组合期间未被声明的 Undo/Redo/Cut/Paste/SelectAll 仍应先取消预编辑再执行。
    /// </remarks>
    internal bool IsExplicitlyConsumedKey(object nativeKeyToken)
        => _keyOwnership.IsExplicitlyConsumed(nativeKeyToken);

    /// <summary>撤销归属时清空未消费的按键接缝。</summary>
    internal void ResetKeyOwnership() => _keyOwnership.Reset();

    /// <summary>
    /// 按 epoch + 已提交文档版本设置已提交选区（TI-07 P2a 平台桥接缝）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只动选区：</b>不写绑定、不记录历史、不产生编辑事务、不调用平台服务；端点按核心字素规则吸附，
    /// 只标记布局失效（组合回调内不做排版）。
    /// </para>
    /// <para>
    /// <b>拒绝条件（全部无副作用）：</b>epoch 不匹配、客户端已撤销/待换 epoch、组合进行中（预编辑期间的
    /// 选区必须走 <see cref="CompositionEvent.Update"/> 的组合内 caret）、正在应用组合批次（重入）、
    /// 期望版本与真实回读版本不一致（外部写入已经发生）。
    /// </para>
    /// <para>
    /// <b>R5 重入校验：</b><see cref="CurrentDocument"/> 会调用用户 Binding getter，getter 可能同步切换焦点、
    /// 卸载或换源——此时旧客户端已被释放。因此<b>每次</b>可重入回读之后、写入之前都必须重新校验
    /// 释放状态、当前焦点租约/所有者、epoch、版本与非组合状态，不能只比较版本。
    /// </para>
    /// </remarks>
    public bool TrySetCommittedSelection(TextInputSession session, int documentVersion, TextSelection selection)
    {
        if (!IsSelectionTargetValid(session, documentVersion))
        {
            return false;
        }

        // 真实回读：外部绑定变化会让期望版本失效（同步失败时 CurrentDocument 已请求换 epoch）。
        // 这一步会调用用户 getter，可能同步撤销本客户端的归属。
        var document = CurrentDocument();

        // R5：回读之后、写入之前重新校验全部身份条件（getter 内可能已失焦/卸载/换源）。
        if (!IsSelectionTargetValid(session, documentVersion))
        {
            return false;
        }

        if (document.Version != documentVersion)
        {
            return false;
        }

        var text = document.Text;
        var anchor = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(selection.Anchor, 0, text.Length));
        var active = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(selection.Active, 0, text.Length));
        if (_state.Anchor == anchor && _state.Caret == active)
        {
            // 同值不重复标记布局失效。
            return true;
        }

        _state.Anchor = anchor;
        _state.Caret = active;

        // 选区变化影响 caret 可见性与候选锚点几何：只标记失效，由安全阶段重排。
        _node.Runtime.InvalidateLayout(_node);
        return true;
    }

    /// <summary>
    /// 选区接缝的身份校验（R5）：释放状态、epoch、非组合、非重入、版本参数合法，以及
    /// <b>仍是当前焦点租约的所有者</b>（getter 内失焦/换焦点/卸载后必须为 false）。
    /// </summary>
    /// <remarks>该方法本身不读绑定、不排版、不调用平台，可在回读前后重复调用。</remarks>
    private bool IsSelectionTargetValid(TextInputSession session, int documentVersion)
    {
        if (IsReleased || EpochInvalidationRequested || _applying > 0)
        {
            return false;
        }

        // 预编辑期间的选区属于未提交内容：不能当作已提交文本去移动，必须通过 Update 改组合内 caret。
        if (_composition.IsComposing)
        {
            return false;
        }

        if (session.SessionId != _epoch || documentVersion < 0)
        {
            return false;
        }

        // 当前焦点租约/所有者校验：旧客户端在失焦/卸载/换源后不再是所有者，绝不能写入。
        if (!_state.InputSession.IsCurrentOwner(this))
        {
            return false;
        }

        // 节点必须仍挂载且仍可输入；卸载/禁用后的旧引用不得移动选区。
        if (!_state.IsEditableNode(_node))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 真实事务提交入口：把一段结果作为一次原子替换交给节点的 TI-03 会话，并返回真实事务结果。
    /// </summary>
    private EditResult CommitResult(TextRange range, string result)
    {
        if (!_state.IsTextEditNode(_node))
        {
            return default;
        }

        // 事务前的真实同步：外部写入 / 来源替换 / MaxLength 变化都会在这里被发现并标记组合失效。
        _state.SyncCompositionDocument(_node);
        return _state.ApplyCompositionCommit(_node, range, result);
    }

    /// <summary>
    /// 读取当前真实文档视图：<b>先</b>同步真实绑定（外部内容/来源/MaxLength 变化会取消未提交段并立即请求换 epoch），
    /// 再返回同步后的值。不做排版、不递归处理待办、不调用平台服务。
    /// </summary>
    /// <remarks>
    /// 绑定 getter 抛错时返回<b>最后一次可靠快照</b>，绝不让托管异常越过原生回调，也不在错误路径再次调用抛错 getter。
    /// </remarks>
    private CompositionDocument CurrentDocument()
    {
        try
        {
            if (!_state.IsTextEditNode(_node))
            {
                var detached = new CompositionDocument(
                    _state.Editor.Text,
                    _state.Editor.Version,
                    new TextSelection(_state.Anchor, _state.Caret),
                    this,
                    int.MaxValue);
                _lastReliableDocument = detached;
                return detached;
            }

            if (_state.SyncCompositionDocument(_node))
            {
                RequestEpochInvalidation();
            }

            var document = new CompositionDocument(
                _state.Editor.Text,
                _state.Editor.Version,
                new TextSelection(_state.Anchor, _state.Caret),
                _state.TextIdentity(_node),
                _state.MaxLength(_node));
            _lastReliableDocument = document;
            return document;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;

            // 事务之外的客户端读取失败（Document/GetState/组合前同步）同样让历史不可信；
            // 清理历史是核心内存操作，不触碰组合消费者自己的失效标志。
            _state.History.Clear();
            return _lastReliableDocument ?? CurrentCompositionDocument();
        }
    }

    /// <summary>最后一次可靠快照；绑定 getter 抛错或被撤销时用于返回真实文档与拒绝原因。</summary>
    internal TextSnapshot LastReliableSnapshot
        => _lastReliableDocument is { } document
            ? document.Snapshot
            : new TextSnapshot(_state.Editor.Text, new TextSelection(_state.Anchor, _state.Caret), _state.Editor.Version);

    /// <summary>外部变化导致组合失效：取消未提交段（不恢复旧选区，只按新文本夹取）并请求换 epoch。</summary>
    private void RequestEpochInvalidation() => InvalidateForExternalChange();

    private CompositionDocument CurrentCompositionDocument()
        => new(
            _state.Editor.Text,
            _state.Editor.Version,
            new TextSelection(_state.Anchor, _state.Caret),
            _state.IsTextEditNode(_node) ? _state.TextIdentity(_node) : this,
            _state.IsTextEditNode(_node) ? _state.MaxLength(_node) : int.MaxValue);

    private void ApplyRestoredSelection(TextSelection? restored)
    {
        if (restored is not { } selection)
        {
            return;
        }

        var length = _state.Editor.Text.Length;
        _state.Anchor = Math.Clamp(selection.Anchor, 0, length);
        _state.Caret = Math.Clamp(selection.Active, 0, length);
        _node.Runtime.InvalidateLayout(_node);
    }

}
