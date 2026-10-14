namespace CSharp.UI;

/// <summary>
/// 焦点管理：维护当前焦点节点、Tab/Shift+Tab 遍历顺序，并在节点卸载或被禁用时清理焦点。
/// </summary>
internal sealed class FocusManager
{
    private readonly UiRuntime _runtime;

    public FocusManager(UiRuntime runtime) => _runtime = runtime;

    /// <summary>当前焦点节点。</summary>
    public ViewNode? Focused { get; private set; }

    /// <summary>按深度优先顺序收集可参与遍历的焦点节点。</summary>
    public IReadOnlyList<ViewNode> BuildOrder()
    {
        var order = new List<ViewNode>();
        Collect(_runtime.Root, order);
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

        var previous = Focused;
        Focused = node;
        if (previous is not null)
        {
            previous.IsFocused = false;
            previous.View.OnFocusChanged(new InputContext(_runtime), previous, false);
        }

        if (node is not null)
        {
            node.IsFocused = true;
            node.View.OnFocusChanged(new InputContext(_runtime), node, true);
        }

        _runtime.InvalidateRender();
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

    /// <summary>节点卸载前清理焦点。</summary>
    public void OnNodeUnmounting(ViewNode node)
    {
        if (Focused is not null && IsSelfOrDescendant(node, Focused))
        {
            Focused = null;
        }
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
        switch (e.Kind)
        {
            case PointerEventKind.Down:
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
                    _captured.IsPointerCaptured = true;
                }

                if (_hovered is not null)
                {
                    Route<PointerEvent>(_hovered, context, static (node, ctx, pointer) => node.View.OnPointerDown(ctx, node, pointer), e);
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
                break;
            }
        }

        _runtime.InvalidateRender();
    }

    /// <summary>分发滚轮事件；从命中节点向上冒泡，直到某个容器消费（可滚动）。</summary>
    public void DispatchWheel(WheelEvent e)
    {
        var context = new InputContext(_runtime);
        UpdateHover(new PointerEvent(PointerEventKind.Move, e.Position, PointerButton.None, e.Modifiers), context);
        var target = _captured ?? _hovered;
        if (target is not null)
        {
            Route<WheelEvent>(target, context, static (node, ctx, wheel) => node.View.OnWheel(ctx, node, wheel), e);
        }
    }

    /// <summary>分发键盘事件；Tab / Shift+Tab 在无人处理时执行焦点遍历。</summary>
    public void DispatchKey(KeyEvent e)
    {
        var context = new InputContext(_runtime);
        var handled = false;
        if (_focus.Focused is not null)
        {
            handled = Route<KeyEvent>(_focus.Focused, context, (node, ctx, key) => e.Kind == KeyEventKind.Down ? node.View.OnKeyDown(ctx, node, key) : node.View.OnKeyUp(ctx, node, key), e);
        }

        if (!handled && e.Kind == KeyEventKind.Down && e.Key == UiKey.Tab)
        {
            _focus.Move(e.Modifiers.HasFlag(KeyModifiers.Shift));
            _runtime.InvalidateRender();
        }
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
    public ViewNode? HitTest(Point position)
    {
        var clip = new Rect(0, 0, _runtime.ViewportSize.Width, _runtime.ViewportSize.Height);
        return HitTest(_runtime.Root, position, clip);
    }

    private static ViewNode? HitTest(ViewNode node, Point position, Rect clip)
    {
        if (!node.IsMounted || !node.View.IsHitTestVisible(node))
        {
            return null;
        }

        var childClip = node.View.GetChildClip(node);
        var effectiveClip = childClip.HasValue ? clip.Intersect(childClip.Value) : clip;
        var insideClip = effectiveClip.ContainsInclusive(position);

        if (insideClip)
        {
            // 逆序：后绘制的子项在上层，优先命中。
            for (var i = node.ChildList.Count - 1; i >= 0; i--)
            {
                var hit = HitTest(node.ChildList[i], position, effectiveClip);
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

        return insideClip && node.View.HitTestSelf(node, position) ? node : null;
    }

    private void UpdateHover(PointerEvent e, InputContext context)
    {
        var target = HitTest(e.Position);
        if (ReferenceEquals(target, _hovered))
        {
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
    }

    private void ClearHover(InputContext context)
    {
        foreach (var node in _hoverChain)
        {
            if (node.IsMounted)
            {
                node.IsHovered = false;
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
