namespace CSharp.UI;

/// <summary>
/// 焦点管理：维护当前焦点节点、Tab/Shift+Tab 遍历顺序，并在节点卸载或被禁用时清理焦点。
/// </summary>
internal sealed class FocusManager
{
    private readonly UiRuntime _runtime;

    /// <summary>焦点变更代际：服务回调重入 <see cref="SetFocus"/> 时用于让外层调用放弃收尾。</summary>
    private int _focusGeneration;

    public FocusManager(UiRuntime runtime) => _runtime = runtime;

    /// <summary>当前焦点节点。</summary>
    public ViewNode? Focused { get; private set; }

    /// <summary>按深度优先顺序收集可参与遍历的焦点节点。</summary>
    public IReadOnlyList<ViewNode> BuildOrder()
    {
        var order = new List<ViewNode>();
        Collect(_runtime.TraversalRoot, order);
        return order;
    }

    private static void Collect(ViewNode node, List<ViewNode> order)
    {
        if (!node.IsMounted)
        {
            return;
        }

        if (node.View.IsFocusable && !node.IsEffectivelyDisabled)
        {
            order.Add(node);
        }

        foreach (var child in node.ChildList)
        {
            Collect(child, order);
        }
    }

    /// <summary>设置焦点；节点不可用（未挂载、被禁用、不可聚焦）时清除焦点。</summary>
    /// <remarks>
    /// 通知旧服务时服务可能同步重入（<c>EndSession</c> 里再次 <see cref="SetFocus"/>）：外层调用必须在
    /// 重入后放弃收尾，不能把已经被重入覆盖的目标重新标记为焦点，也不能替换重入建立的服务归属。
    /// </remarks>
    public void SetFocus(ViewNode? node)
    {
        if (node is not null && (!node.IsMounted || node.IsEffectivelyDisabled || !node.View.IsFocusable))
        {
            node = null;
        }

        if (ReferenceEquals(node, Focused))
        {
            return;
        }

        var generation = ++_focusGeneration;
        var previous = Focused;
        Focused = node;
        if (previous is not null)
        {
            previous.IsFocused = false;

            // 旧焦点客户端先撤销文本输入归属（先失效），再触发 OnFocusChanged；这样即使服务同步回调
            // 也不会写入正在获得焦点的新客户端。
            _runtime.ReleaseTextInputOwnershipFor(previous);

            // 撤销归属会同步通知服务：服务可能在 EndSession 内重入并把焦点切回旧节点。
            // 此时外层必须立即放弃，不能再对 previous 发出失焦通知（那会结束重入刚建立的新会话）。
            if (_focusGeneration != generation || !ReferenceEquals(Focused, node))
            {
                _runtime.InvalidateRender();
                return;
            }

            previous.View.OnFocusChanged(new InputContext(_runtime), previous, false);
        }

        // 服务在回调里重入并切换了焦点：外层不得再改动焦点标志或服务归属。
        if (_focusGeneration != generation || !ReferenceEquals(Focused, node))
        {
            _runtime.InvalidateRender();
            return;
        }

        if (node is not null)
        {
            node.IsFocused = true;
            node.View.OnFocusChanged(new InputContext(_runtime), node, true);
        }

        _runtime.InvalidateRender();
    }

    /// <summary>窗口失活等接缝：先撤销当前焦点客户端的文本输入归属，再收敛取消。</summary>
    public void CancelTextInput()
    {
        if (Focused is { } focused)
        {
            _runtime.ReleaseTextInputOwnershipFor(focused);
        }
    }

    /// <summary>窗口重新激活：焦点仍在可编辑控件上时用新的 epoch 恢复文本输入会话。</summary>
    public void ResumeTextInput()
    {
        if (Focused is { } focused
            && focused.IsMounted
            && !focused.IsEffectivelyDisabled
            && focused.InteractionState is ITextEditSessionOwner owner
            && owner.IsEditableNode(focused))
        {
            focused.View.OnFocusChanged(new InputContext(_runtime), focused, true);
        }
    }

    /// <summary>按遍历顺序移动焦点；没有可聚焦节点时清除焦点。</summary>
    public void Move(bool backward)
    {
        var order = BuildOrder();
        if (order.Count == 0)
        {
            SetFocus(null);
            return;
        }

        var current = Focused is null ? -1 : IndexOf(order, Focused);
        var next = current < 0
            ? (backward ? order.Count - 1 : 0)
            : ((current + (backward ? -1 : 1)) % order.Count + order.Count) % order.Count;
        SetFocus(order[next]);
    }

    private static int IndexOf(IReadOnlyList<ViewNode> nodes, ViewNode target)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>节点卸载前清理焦点与文本输入归属。</summary>
    /// <remarks>
    /// 撤销归属会同步通知服务，服务可能在回调里把焦点重新设到别的节点；此时不得把重入建立的新焦点清空。
    /// </remarks>
    public void OnNodeUnmounting(ViewNode node)
    {
        var target = Focused;
        if (target is null || !IsSelfOrDescendant(node, target))
        {
            return;
        }

        var generation = ++_focusGeneration;
        _runtime.ReleaseTextInputOwnershipFor(target);

        // 服务回调重入并建立了新的焦点：保留重入结果，不覆盖。
        if (_focusGeneration != generation || !ReferenceEquals(Focused, target))
        {
            return;
        }

        Focused = null;
    }

    /// <summary>重建后校验焦点仍然有效（节点被删除或被 Disabled 包裹时清除焦点）。</summary>
    public void Validate()
    {
        var focused = Focused;
        if (focused is null)
        {
            return;
        }

        if (!focused.IsMounted || focused.IsEffectivelyDisabled || !focused.View.IsFocusable)
        {
            SetFocus(null);
        }
    }

    private static bool IsSelfOrDescendant(ViewNode ancestor, ViewNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 输入分发：命中测试、悬停/按下状态、指针捕获、滚轮与键盘路由。
/// </summary>
internal sealed class InputRouter
{
    private readonly UiRuntime _runtime;
    private readonly FocusManager _focus;
    private readonly List<ViewNode> _hoverChain = [];
    private readonly List<ViewNode> _pressedChain = [];
    private ViewNode? _hovered;
    private ViewNode? _pressed;
    private ViewNode? _captured;
    private PointerEvent? _lastPointerEvent;

    public InputRouter(UiRuntime runtime, FocusManager focus)
    {
        _runtime = runtime;
        _focus = focus;
    }

    /// <summary>当前悬停的最深节点。</summary>
    public ViewNode? Hovered => _hovered;

    /// <summary>当前持有指针捕获的节点。</summary>
    public ViewNode? Captured => _captured;

    /// <summary>分发指针事件。</summary>
    public void DispatchPointer(PointerEvent e)
    {
        var context = new InputContext(_runtime);
        _lastPointerEvent = e;
        switch (e.Kind)
        {
            case PointerEventKind.Down:
                // 外点吞并：命中判定必须早于 hover/press/capture。判定为外点时 popup 关闭请求入队，
                // 同时立即挂载全屏 dismiss-target —— 本次 Down 及后续 Move/Up/Cancel 只落在它身上，
                // 下层用户树（Button 激活、ScrollView 拖动）不会收到任何事件。
                _runtime.Overlay.TryBeginOutsidePress(HitTest(e.Position));
                UpdateHover(e, context);
                _pressed = _hovered;
                _captured = _hovered;
                _pressedChain.Clear();
                for (var node = _pressed; node is not null; node = node.Parent)
                {
                    _pressedChain.Add(node);
                    node.IsPressed = true;
                }

                if (_captured is not null)
                {
                    // 视图可拒捕（如 RichText 未命中链接区间 / 无激活回调）：路由回退到
                    // _pressed/_hovered 的既有冒泡路径——父链仍能正常消费 Up/后续事件。
                    if (_captured.View.WantsPointerCapture(_captured, e))
                    {
                        _captured.IsPointerCaptured = true;
                    }
                    else
                    {
                        _captured = null;
                    }
                }
                if (_hovered is not null)
                {
                    Route<PointerEvent>(_hovered, context, static (node, ctx, pointer) => node.View.OnPointerDown(ctx, node, pointer), e);
                }

                // 左键点"非交互空白"清焦：本次按下未被任何节点消费（含 deepest==null）且
                // 命中链上没有 mounted+enabled+IsFocusable 祖先接管——消费节点（Button/TextField
                // 自 SetFocus、ScrollView 吞 bar、RichText 按住、slider 拖拽）与控件内 Text 修饰符
                // 区不清焦；右键/中键不 blur（上下文/辅助输入非 primary）。消费方自己 SetFocus(null)
                // 的罕见重入由 Handled 保留——消费优先，不覆盖其决定。
                if (!context.Handled
                    && e.Button == PointerButton.Left
                    && !ChainHasFocusable(_hovered))
                {
                    _focus.SetFocus(null);
                }

                break;

            case PointerEventKind.Move:
                UpdateHover(e, context);
                var moveTarget = _captured ?? _hovered;
                if (moveTarget is not null)
                {
                    Route<PointerEvent>(moveTarget, context, static (node, ctx, pointer) => node.View.OnPointerMove(ctx, node, pointer), e);
                }

                break;

            case PointerEventKind.Up:
            {
                var upTarget = _captured ?? _pressed;
                if (upTarget is not null)
                {
                    Route<PointerEvent>(upTarget, context, static (node, ctx, pointer) => node.View.OnPointerUp(ctx, node, pointer), e);
                }

                ClearPressState(context);

                // 吞并手势在 Up 之后结束：先让 Up 只落在 dismiss-target 上，再卸载它并重算 hover。
                _runtime.Overlay.EndSwallowGesture();
                UpdateHover(e, context);
                break;
            }

            case PointerEventKind.Cancel:
            {
                var cancelTarget = _captured ?? _pressed ?? _hovered;
                if (cancelTarget is not null)
                {
                    Route<PointerEvent>(cancelTarget, context, static (node, ctx, _) => node.View.OnPointerCancel(ctx, node), default);
                }

                ClearPressState(context);
                ClearHover(context);
                _runtime.Overlay.EndSwallowGesture();
                break;
            }
        }

        _runtime.InvalidateRender();
    }

    /// <summary>分发滚轮事件；从命中节点向上冒泡，直到某个容器消费（可滚动）。</summary>
    public void DispatchWheel(WheelEvent e)
    {
        var context = new InputContext(_runtime);
        var pointerEvent = new PointerEvent(PointerEventKind.Move, e.Position, PointerButton.None, e.Modifiers);
        _lastPointerEvent = pointerEvent;
        UpdateHover(pointerEvent, context);
        var target = _captured ?? _hovered;

        // 滚轮不参与外点吞并：吞并期的 dismiss-target 会让真实 hover 链停在它身上，
        // 这里跳过它重新按同一位置在真实树里命中（弹层外滚轮照常滚内容、不关不吞）。
        if (target is not null && _runtime.Overlay.IsDismissTarget(target))
        {
            target = HitTest(e.Position, _runtime.Overlay.DismissTargetNode);
        }

        if (target is not null)
        {
            Route<WheelEvent>(target, context, static (node, ctx, wheel) => node.View.OnWheel(ctx, node, wheel), e);
        }
    }

    /// <summary>
    /// 按最近一次观测到的指针位置重算 hover（Floating scrollbar 可见性翻转等 owner 自发的命中结构变化）：
    /// 静止指针悬在新出现的 overlay 上时 deepest hover 应更新为 owner；无指针观测则 no-op。
    /// </summary>
    internal void RefreshHover()
    {
        if (_lastPointerEvent is { } e)
        {
            // 显隐/几何刷新：同一 deepest 也要按真实位置重估抑制（force=true）。
            UpdateHover(e, new InputContext(_runtime), forceDeepestNotify: true);
        }
    }

    /// <summary>分发键盘事件；Tab / Shift+Tab 在无人处理时执行焦点遍历。</summary>
    /// <returns>事件是否被焦点控件消费或经焦点遍历消费；宿主据此决定自身快捷键（如退出）是否继续生效。</returns>
    public bool DispatchKey(KeyEvent e)
    {
        var context = new InputContext(_runtime);
        var handled = false;
        if (_focus.Focused is not null)
        {
            handled = Route<KeyEvent>(_focus.Focused, context, (node, ctx, key) => e.Kind == KeyEventKind.Down ? node.View.OnKeyDown(ctx, node, key) : node.View.OnKeyUp(ctx, node, key), e);
        }

        // Esc：焦点控件先路由（组合期取消/自处理 Esc 的控件已消费 → 这里不动作）。
        // 未消费且存在可关闭 entry、owner 允许 Esc 时关闭并恢复焦点，然后才走 Tab fallback。
        if (!handled
            && e.Kind == KeyEventKind.Down
            && e.Key == UiKey.Escape
            && _runtime.Overlay.TryRequestEscapeClose(out var restoreFocus))
        {
            _focus.SetFocus(restoreFocus);
            _runtime.InvalidateRender();
            handled = true;
        }

        if (!handled && e.Kind == KeyEventKind.Down && e.Key == UiKey.Tab)
        {
            _focus.Move(e.Modifiers.HasFlag(KeyModifiers.Shift));
            _runtime.InvalidateRender();
            handled = true;
        }

        return handled;
    }

    /// <summary>分发已提交文本输入；只发送给当前焦点节点。</summary>
    public void DispatchTextInput(TextInputEvent e)
    {
        if (e.Text.Length == 0)
        {
            return;
        }

        var context = new InputContext(_runtime);
        if (_focus.Focused is not null)
        {
            Route<TextInputEvent>(_focus.Focused, context, static (node, ctx, text) => node.View.OnTextInput(ctx, node, text), e);
        }
    }

    /// <summary>节点卸载前清理与之相关的悬停、按下与捕获状态。</summary>
    public void OnNodeUnmounting(ViewNode node)
    {
        if (_captured is not null && IsSelfOrDescendant(node, _captured))
        {
            _captured = null;
        }

        if (_pressed is not null && IsSelfOrDescendant(node, _pressed))
        {
            _pressed = null;
        }

        if (_hovered is not null && IsSelfOrDescendant(node, _hovered))
        {
            _hovered = null;
        }

        _hoverChain.RemoveAll(n => IsSelfOrDescendant(node, n));
        _pressedChain.RemoveAll(n => IsSelfOrDescendant(node, n));
    }

    /// <summary>
    /// 协调更新与布局之后校验交互状态：被禁用或已卸载的节点不再保留悬停、按下与指针捕获。
    /// </summary>
    /// <remarks>禁用变化通过环境传播，只有布局完成后才能判定，因此由运行时在更新流程末尾调用。</remarks>
    public void ValidateInteractionState()
    {
        if (_captured is { } captured && (!captured.IsMounted || captured.IsEffectivelyDisabled))
        {
            // 通知控件中止拖动/按下状态，避免禁用后仍继续响应移动或释放。
            var context = new InputContext(_runtime);
            captured.View.OnPointerCancel(context, captured);
            ClearPressState(context);
        }

        if (_hovered is { } hovered && (!hovered.IsMounted || hovered.IsEffectivelyDisabled))
        {
            ClearHover(new InputContext(_runtime));
        }

        _hoverChain.RemoveAll(n => !n.IsMounted);
        _pressedChain.RemoveAll(n => !n.IsMounted);
    }

    /// <summary>命中测试：按逆绘制顺序查找最上层可见、未被裁剪、未被禁用的目标。</summary>
    public ViewNode? HitTest(Point position) => HitTest(position, null);

    /// <summary>命中测试，跳过指定子树（滚轮不得被吞并期的 dismiss-target 截获）。</summary>
    private ViewNode? HitTest(Point position, ViewNode? skip)
    {
        var clip = new Rect(0, 0, _runtime.ViewportSize.Width, _runtime.ViewportSize.Height);
        return HitTest(_runtime.TraversalRoot, position, clip, skip);
    }

    private static ViewNode? HitTest(ViewNode node, Point position, Rect clip, ViewNode? skip)
    {
        if (!node.IsMounted || ReferenceEquals(node, skip) || !node.View.IsHitTestVisible(node))
        {
            return null;
        }

        var insideAncestorClip = clip.ContainsInclusive(position);

        // owner overlay（Floating scrollbar 等自绘覆盖区）：子节点之前优先归属 owner；
        // guard 完整——祖先 clip / 非禁用 / frame 内是 router 侧契约，不依赖 owner 自觉隐藏。
        if (insideAncestorClip
            && !node.IsEffectivelyDisabled
            && node.Frame.Contains(position)
            && node.View.HitTestOverlay(node) is { } overlay
            && overlay.Contains(position))
        {
            return node;
        }

        var childClip = node.View.GetChildClip(node);
        var effectiveClip = childClip.HasValue ? clip.Intersect(childClip.Value) : clip;
        var insideEffectiveClip = effectiveClip.ContainsInclusive(position);

        if (insideEffectiveClip)
        {
            // 逆序：后绘制的子项在上层，优先命中。
            for (var i = node.ChildList.Count - 1; i >= 0; i--)
            {
                var hit = HitTest(node.ChildList[i], position, effectiveClip, skip);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        if (node.IsEffectivelyDisabled)
        {
            return null;
        }

        // 自身命中按祖先 clip + HitTestSelf(frame)；childClip 只约束子项，不限制 owner
        // （否则 ScrollView gutter 区永远无法被 owner 命中）。
        return insideAncestorClip && node.View.HitTestSelf(node, position) ? node : null;
    }

    private void UpdateHover(PointerEvent e, InputContext context)
        => UpdateHover(e, context, forceDeepestNotify: false);

    /// <summary>
    /// <paramref name="forceDeepestNotify"/>=true 时即使 target 与 _hovered 相同也按真实
    /// <see cref="PointerEvent.Position"/> 通知 <see cref="View.OnHoverDeepestChanged"/>：
    /// RefreshHover/几何刷新用于让"content 与 bar 同属一个 owner deepest"的视图（TextEditor、
    /// owner 自命中区）在 track 移离/移入指针后重判抑制——普通 Move 早退不付此成本。
    /// </summary>
    private void UpdateHover(PointerEvent e, InputContext context, bool forceDeepestNotify)
    {
        var target = HitTest(e.Position);
        if (ReferenceEquals(target, _hovered))
        {
            if (forceDeepestNotify && target is not null)
            {
                // 链未变但几何/显隐已变：同一 deepest 也要重估（over→suppress / !over→rearm）。
                target.View.OnHoverDeepestChanged(target, e.Position, isDeepest: true);
            }
            return;
        }

        var chain = new List<ViewNode>();
        for (var node = target; node is not null; node = node.Parent)
        {
            chain.Insert(0, node);
        }

        foreach (var node in _hoverChain)
        {
            if (!chain.Contains(node) && node.IsMounted)
            {
                node.IsHovered = false;
                node.View.OnPointerLeave(context, node, e);
            }
        }

        foreach (var node in chain)
        {
            if (!_hoverChain.Contains(node))
            {
                node.IsHovered = true;
                node.View.OnPointerEnter(context, node, e);
            }
        }

        _hoverChain.Clear();
        _hoverChain.AddRange(chain);
        _hovered = target;

        // deepest 提交后按真实 e.Position 通知 deepest 身份变化（HoverNode 已指向 target）：
        // 新 deepest 重估抑制入口（overlay 翻可见/静止悬 bar→suppress）；让位成员重估出口。
        if (target is not null)
        {
            target.View.OnHoverDeepestChanged(target, e.Position, isDeepest: true);
        }

        for (var i = chain.Count - 2; i >= 0; i--)
        {
            chain[i].View.OnHoverDeepestChanged(chain[i], e.Position, isDeepest: false);
        }
    }

    private void ClearHover(InputContext context)
    {
        var position = _lastPointerEvent?.Position ?? default;
        foreach (var node in _hoverChain)
        {
            if (node.IsMounted)
            {
                node.IsHovered = false;
                node.View.OnHoverDeepestChanged(node, position, isDeepest: false);
            }
        }

        _hoverChain.Clear();
        _hovered = null;

        // 释放按下状态。
        foreach (var node in _pressedChain)
        {
            if (node.IsMounted)
            {
                node.IsPressed = false;
            }
        }

        _pressedChain.Clear();
        _pressed = null;
    }

    private void ClearPressState(InputContext context)
    {
        foreach (var node in _pressedChain)
        {
            if (node.IsPointerCaptured)
            {
                node.IsPointerCaptured = false;
            }

            node.IsPressed = false;
        }

        _pressedChain.Clear();
        _pressed = null;
        _captured = null;
    }

    private bool Route<TEvent>(ViewNode from, InputContext context, Func<ViewNode, InputContext, TEvent, bool> handler, TEvent e)
    {
        for (var node = from; node is not null; node = node.Parent)
        {
            if (!node.IsMounted || node.IsEffectivelyDisabled)
            {
                continue;
            }

            if (handler(node, context, e))
            {
                context.MarkHandled();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 命中链上是否存在仍可作为键盘焦点目标的节点（mounted + 未 disabled + <see cref="View.IsFocusable"/>）。
    /// deepest 自身不可聚焦但祖先可聚焦（控件内 Text、包裹修饰符）→ 返回 true，按下不清焦。
    /// </summary>
    private static bool ChainHasFocusable(ViewNode? deepest)
    {
        for (var node = deepest; node is not null; node = node.Parent)
        {
            if (node.IsMounted && !node.IsEffectivelyDisabled && node.View.IsFocusable)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelfOrDescendant(ViewNode ancestor, ViewNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
