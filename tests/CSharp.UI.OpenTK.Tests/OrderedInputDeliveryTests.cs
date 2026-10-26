using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.GraphicsLibraryFramework;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;
using GlfwKeyModifiers = global::OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3a R9：有序交付闸门的<b>真实</b>行为（真实 <see cref="UiRuntime"/> / <see cref="TextField"/> /
/// <see cref="OpenTkInputAdapter"/>，不使用手工出队或队列计数代替交付）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须用真实对象：</b>上一版测试只断言 <c>DeferredCount</c> 增加，
/// 而实际输入被丢弃（默认 IMM 路径 <c>writes=0</c>）。这里断言真实绑定写入与真实字段内容。
/// </para>
/// <para>不依赖 Windows/COM：全部在托管层完成，可在任何平台确定性运行。</para>
/// </remarks>
public sealed class OrderedInputDeliveryTests
{
    /// <summary>真实宿主替身：真实运行时 + 真实 TextField + 真实适配器。</summary>
    private sealed class Harness : IDisposable
    {
        public string TextA = "AAAA";
        public string TextB = "BBBB";
        public int WritesA;
        public int WritesB;

        /// <summary>setter 嵌套次数（&gt;0 表示发生了不允许的重入交付）。</summary>
        public int NestedSetterCount;

        /// <summary>在交付期守卫内发生的绑定写入次数（应立即/延后路径都等于写入总数）。</summary>
        public int WritesWhileDelivering;

        private readonly Action<OpenTkInputAdapter, string>? _reenterOnWrite;
        private bool _inSetter;
        public UiRuntime Runtime { get; }
        public OpenTkInputAdapter Adapter { get; }
        public TextField FieldA { get; }
        public TextField FieldB { get; }

        public Harness(IWindowsInputBridge? bridge = null, Action? requestExit = null, Action<OpenTkInputAdapter, string>? reenterOnWrite = null)
        {
            // 真实 setter：可选在写入期间重入适配器（用于真实重入回归，而不是只强制 unsafe）。
        FieldA = new TextField(new Binding<string>(() => TextA, v =>
        {
            if (_inSetter)
            {
                NestedSetterCount++;
            }

            _inSetter = true;
            try
            {
                WritesA++;

                // 立即路径与延后路径都必须持有交付期守卫：写入期间 IsDelivering 必须为 true。
                if (Adapter?.IsDelivering == true)
                {
                    WritesWhileDelivering++;
                }

                TextA = v;
                _reenterOnWrite?.Invoke(Adapter!, v);
            }
            finally
            {
                _inSetter = false;
            }
        }));
            _reenterOnWrite = reenterOnWrite;

            FieldB = new TextField(new Binding<string>(() => TextB, v => { WritesB++; TextB = v; }));

            Runtime = new UiRuntime(new VStack(FieldA, FieldB), new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                TextureService = new RecordingTextureService(),
                TextInputService = bridge ?? (ITextInputService)new NullInputService(),
            });

            Runtime.SetViewport(new Size(400, 200), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();

            // 聚焦真实 TextField（VStack 根本身不可聚焦），否则文本没有接收者。
            Runtime.MoveFocus();
            Runtime.BuildFrame();

            Adapter = new OpenTkInputAdapter(Runtime, requestExit ?? (() => { }), ime: bridge);
        }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>最小文本输入服务（真实运行时的服务槽位）。</summary>
    private sealed class NullInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.CommittedTextOnly;

        public ITextInputClient? ActiveClient => null;

        public bool StartSession(ITextInputClient client) => false;

        public bool EndSession() => true;

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => TextInputApplyResult.Rejected(TextSnapshot.Empty, "no client");

        public bool RequestCancel() => false;

        public bool PublishGeometry(TextInputGeometry geometry) => false;
    }

    // ---------------------------------------------------------------- 默认路径必须直通

    [Fact]
    public void 默认路径普通文本立即交付且不进入延后队列()
    {
        using var h = new Harness();

        h.Adapter.HandleTextInput(new TextInputEventArgs('x'));

        // 关键回归：默认路径不得把输入压进队列后丢弃。
        // 没有 TSF 就没有待应用事实，因此**不延后**（Deferred=0）且立即交付。
        Assert.Equal(0, h.Adapter.OrderedDeferredCount);
        Assert.Equal(1, h.Adapter.OrderedDeliveredCount);
        Assert.Contains('x', h.TextA);
        Assert.Equal(1, h.WritesA);
    }

    [Fact]
    public void 默认路径点击输入框可聚焦并接收输入()
    {
        using var h = new Harness();

        // 无 TSF 时必须能通过指针点击聚焦第二个字段。
        h.Adapter.HandleMouseMove(new MouseMoveEventArgs(10, 190, 0, 0));
        h.Adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, 0));
        h.Adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, 0));

        // 指针事件必须真实交付（不被队列吞掉）。
        Assert.True(h.Adapter.PointerEventCount >= 2, $"pointer events={h.Adapter.PointerEventCount}");
        Assert.Equal(0, h.Adapter.OrderedOverflowCount);
    }

    [Fact]
    public void 默认路径滚轮与焦点也经过同一闸门并交付()
    {
        using var h = new Harness();

        h.Adapter.HandleWheel(new MouseWheelEventArgs(0, -1f));
        h.Adapter.HandleFocusChanged(true);

        Assert.Equal(1, h.Adapter.WheelEventCount);
        Assert.Equal(0, h.Adapter.OrderedOverflowCount);
    }

    // ---------------------------------------------------------------- 完整负载捕获

    [Fact]
    public void 延后项保存完整不可变负载而不是空占位()
    {
        var coordinator = new HostInputCoordinator();

        // 文本负载
        Assert.True(coordinator.Defer(DeferredInputAction.ForText(1, "abc")));
        // 指针负载（含坐标/按键/修饰键/点击计数）
        Assert.True(coordinator.Defer(DeferredInputAction.ForPointer(2, new DeferredPointerInput(
            PointerEventKind.Down, new Point(12, 34), PointerButton.Left, KeyModifiers.Shift, 2))));
        // 按键负载（含重复/token/消费标志）
        Assert.True(coordinator.Defer(DeferredInputAction.ForKey(3, new DeferredKeyInput(
            KeyEventKind.Down, UiKey.A, KeyModifiers.Control, IsRepeat: true, KeyToken: "tok", ConsumedByIme: true))));
        // 焦点负载
        Assert.True(coordinator.Defer(DeferredInputAction.ForFocus(4, activated: false)));

        Assert.True(coordinator.TryDequeue(out var text));
        Assert.Equal("abc", text!.Text);

        Assert.True(coordinator.TryDequeue(out var pointer));
        Assert.Equal(new Point(12, 34), pointer!.Pointer!.Value.Position);
        Assert.Equal(PointerButton.Left, pointer.Pointer!.Value.Button);
        Assert.Equal(2, pointer.Pointer!.Value.ClickCount);
        Assert.Equal(KeyModifiers.Shift, pointer.Pointer!.Value.Modifiers);

        Assert.True(coordinator.TryDequeue(out var key));
        Assert.Equal(UiKey.A, key!.Key!.Value.Key);
        Assert.True(key.Key!.Value.IsRepeat);
        Assert.Equal("tok", key.Key!.Value.KeyToken);
        Assert.True(key.Key!.Value.ConsumedByIme);

        Assert.True(coordinator.TryDequeue(out var focus));
        Assert.False(focus!.FocusActivated);
    }

    [Fact]
    public void 队列溢出显式失败而不是静默丢弃()
    {
        var coordinator = new HostInputCoordinator();
        for (var i = 0; i < HostInputCoordinator.MaxDeferredInputs; i++)
        {
            Assert.True(coordinator.Defer(DeferredInputAction.ForText(i, "x")));
        }

        Assert.False(coordinator.Defer(DeferredInputAction.ForText(9999, "overflow")));
        Assert.True(coordinator.Overflowed);
        Assert.Equal(1, coordinator.OverflowCount);
    }

    // ---------------------------------------------------------------- 不安全 → 延后 → 重放

    [Fact]
    public void 不安全时文本进入队列并在恢复安全后按序重放()
    {
        // 真实路由器（无 TSF），用测试接缝强制“不安全”。
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        router.ForceUnsafeForTesting = true;

        // 不安全期间：文本必须入队，且**不得**触碰运行时。
        h.Adapter.HandleTextInput(new TextInputEventArgs('q'));
        Assert.Equal(1, h.Adapter.OrderedDeferredCount);
        Assert.Equal(0, h.WritesA);
        Assert.DoesNotContain('q', h.TextA);
        Assert.Equal(1, router.Coordinator.DeferredCount);

        // 队列里必须有**真实负载**（不是空占位）。
        Assert.True(router.Coordinator.TryDequeue(out var captured));
        Assert.Equal("q", captured!.Text);
        router.Coordinator.RequeueFront(captured!);

        // 恢复安全：下一次输入按到达序号统一交付（q 先于 r），队列回空且内容真正落地。
        router.ForceUnsafeForTesting = false;
        h.Adapter.HandleTextInput(new TextInputEventArgs('r'));

        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.Contains('q', h.TextA);
        Assert.Contains('r', h.TextA);
        Assert.True(h.TextA.IndexOf('q') < h.TextA.IndexOf('r'), $"order violated in '{h.TextA}'");

        // r 的到达序号晚于已排队的 q，因此它排在队尾并**经队列**交付：两条都算按序重放。
        Assert.Equal(2, h.Adapter.OrderedReplayedCount);
        Assert.Equal(2, h.WritesA);
    }

    [Fact]
    public void 不安全时指针与焦点也入队并在恢复后重放()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        router.ForceUnsafeForTesting = true;

        h.Adapter.HandleMouseMove(new MouseMoveEventArgs(10, 20, 0, 0));
        h.Adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, 0));
        h.Adapter.HandleFocusChanged(true);

        Assert.True(router.Coordinator.DeferredCount >= 3, $"deferred={router.Coordinator.DeferredCount}");
        Assert.Equal(0, h.Adapter.PointerEventCount);
        Assert.Equal(0, h.Adapter.WheelEventCount);

        // 恢复安全：重放后指针事件真正到达运行时。
        router.ForceUnsafeForTesting = false;
        h.Adapter.HandleTextInput(new TextInputEventArgs('z'));

        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.True(h.Adapter.PointerEventCount >= 2, $"pointer={h.Adapter.PointerEventCount}");
        Assert.True(h.Adapter.OrderedReplayedCount >= 3, $"replayed={h.Adapter.OrderedReplayedCount}");
    }

    [Fact]
    public void 事实A字符X事实B点击B字符Y保持字面顺序()
    {
        // 顺序屏障：延后项之间不得被重排；后来的输入不得越过更早的延后项。
        var coordinator = new HostInputCoordinator();
        coordinator.Defer(DeferredInputAction.ForText(1, "X"));
        coordinator.Defer(DeferredInputAction.ForPointer(2, new DeferredPointerInput(
            PointerEventKind.Down, new Point(50, 150), PointerButton.Left, KeyModifiers.None, 1)));
        coordinator.Defer(DeferredInputAction.ForText(3, "Y"));

        var order = new List<string>();
        while (coordinator.TryDequeue(out var item))
        {
            order.Add(item!.Kind switch
            {
                HostDeferredInputKind.Character => $"char:{item.Text}",
                HostDeferredInputKind.Pointer => "pointer",
                _ => item.Kind.ToString(),
            });
        }

        Assert.Equal(["char:X", "pointer", "char:Y"], order);
    }
    // ---------------------------------------------------------------- R10 安全恢复后自动交付

    [Fact]
    public void 安全恢复后无后续输入也主动交付延后文本()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        // 不安全期间捕获真实文本（不触碰运行时）。
        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('k'));
        Assert.Equal(1, router.Coordinator.DeferredCount);
        Assert.Equal(0, h.WritesA);

        // 恢复安全，**不再有任何用户输入**，由宿主安全阶段主动交付。
        router.ForceUnsafeForTesting = false;
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();

        // 精确断言：真正写入、队列清空、无后续输入依赖。
        Assert.Equal(1, delivered);
        Assert.Equal(1, h.WritesA);
        Assert.Contains('k', h.TextA);
        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.Equal(1, h.Adapter.OrderedReplayedCount);
    }

    [Fact]
    public void 主动交付按FIFO处理多条且队列回空()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('1'));
        h.Adapter.HandleTextInput(new TextInputEventArgs('2'));
        h.Adapter.HandleTextInput(new TextInputEventArgs('3'));
        Assert.Equal(3, router.Coordinator.DeferredCount);
        Assert.Equal(0, h.WritesA);

        router.ForceUnsafeForTesting = false;
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();

        Assert.Equal(3, delivered);
        Assert.Equal(0, router.Coordinator.DeferredCount);

        // 顺序必须与到达顺序一致（1→2→3），且都真正写入。
        var idx1 = h.TextA.IndexOf('1');
        var idx2 = h.TextA.IndexOf('2');
        var idx3 = h.TextA.IndexOf('3');
        Assert.True(idx1 >= 0 && idx2 > idx1 && idx3 > idx2,
            $"order violated in '{h.TextA}'");
    }

    [Fact]
    public void 主动交付在不安全时保留队列不丢输入()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('z'));
        Assert.Equal(1, router.Coordinator.DeferredCount);

        // 仍不安全：主动交付必须什么都不做并保留队列。
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();
        Assert.Equal(0, delivered);
        Assert.Equal(1, router.Coordinator.DeferredCount);
        Assert.Equal(0, h.WritesA);

        // 恢复后仍能交付（不因一次失败而丢失）。
        router.ForceUnsafeForTesting = false;
        Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Contains('z', h.TextA);
    }

    [Fact]
    public void 不安全期间新输入只入队并等待安全阶段交付()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('a'));

        // 恢复安全：这一段没有后续输入，由主动交付把 a 落地。
        router.ForceUnsafeForTesting = false;
        Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Contains('a', h.TextA);
        Assert.Equal(1, h.WritesA);

        // 再次不安全：新输入只入队，不会立即交付。
        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('b'));
        Assert.Equal(1, router.Coordinator.DeferredCount);
        Assert.Equal(1, h.WritesA);

        router.ForceUnsafeForTesting = false;
        Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Contains('b', h.TextA);
    }
    [Fact]
    public void 延后交付与立即交付共用退出语义()
    {
        // 立即路径：无焦点时 Esc 触发退出。
        var immediateExits = 0;
        var routerA = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(routerA, () => immediateExits++))
        {
            h.Adapter.HandleKeyDown(new KeyboardKeyEventArgs(
                Keys.Escape, 0, GlfwNoModifiers.Value, false));
        }

        // 延后路径：同样的 Esc 经重放交付，必须产生**相同**退出副作用。
        var deferredExits = 0;
        var routerB = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(routerB, () => deferredExits++))
        {
            routerB.ForceUnsafeForTesting = true;
            h.Adapter.HandleKeyDown(new KeyboardKeyEventArgs(
                Keys.Escape, 0, GlfwNoModifiers.Value, false));
            Assert.Equal(0, deferredExits);

            routerB.ForceUnsafeForTesting = false;
            Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        }

        // 两条路径语义一致（都触发一次退出）。
        Assert.Equal(1, immediateExits);
        Assert.Equal(1, deferredExits);
    }
    // ---------------------------------------------------------------- 真实重入（setter 内再输入）

    [Fact]
    public void 重放期间setter重入的新输入只入队且同一轮按FIFO交付()
    {
        // 真实 runtime + 真实 TextField + 真实适配器；在 Binding setter 内调用 HandleTextInput。
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);

        // **一次性注入标记**：旧版用 value.Contains('x') 会在随后的 xy / xyy 值上持续满足而无限追加，
        // 使定向测试进程不返回（R10 挂起根因）。这里只注入一次，持续来源另有专门用例。
        var injected = false;
        using var h = new Harness(router, reenterOnWrite: (adapter, _) =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            adapter.HandleTextInput(new TextInputEventArgs('y'));
        });

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('x'));
        Assert.Equal(1, router.Coordinator.DeferredCount);

        router.ForceUnsafeForTesting = false;
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();

        // x 与 y 都必须真正落地；y 只能经队列在 x 的 setter 返回后交付，不得出现 setter 嵌套。
        Assert.Equal(2, delivered);
        Assert.Contains('x', h.TextA);
        Assert.Contains('y', h.TextA);
        Assert.True(h.TextA.IndexOf('x') < h.TextA.IndexOf('y'), $"order violated in '{h.TextA}'");
        Assert.Equal(2, h.WritesA);
        Assert.Equal(2, h.WritesWhileDelivering);
        Assert.Equal(0, h.NestedSetterCount);
        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.Equal(2, h.Adapter.OrderedReplayedCount);
    }

    [Fact]
    public void 立即交付期间setter重入的新输入只入队且同守卫不嵌套()
    {
        // R10 独立复现：x 为**立即**交付时，旧版 DeliverCaptured 不在 _delivering 内，
        // setter 内调用 adapter 会嵌套写入（reentered=true）。立即与延后必须同守卫。
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        var injected = false;
        using var h = new Harness(router, reenterOnWrite: (adapter, _) =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            adapter.HandleTextInput(new TextInputEventArgs('y'));
        });

        // 安全路径：默认立即交付（不进入延后队列）。
        h.Adapter.HandleTextInput(new TextInputEventArgs('x'));

        Assert.Equal(2, h.WritesA);
        Assert.Equal(2, h.WritesWhileDelivering);
        Assert.Equal(0, h.NestedSetterCount);
        Assert.Contains('x', h.TextA);
        Assert.Contains('y', h.TextA);
        Assert.True(h.TextA.IndexOf('x') < h.TextA.IndexOf('y'), $"order violated in '{h.TextA}'");
        Assert.Equal(0, router.Coordinator.DeferredCount);
    }

    [Fact]
    public void 旧排队项setter追加的新项不得越过当前输入()
    {
        // 到达序号：x（已排队）< z（当前输入）< y（x 交付期间由 setter 追加）。
        // 若先把旧队列整体 drain 再交付 z，y 就会先于 z 落地——真实到达顺序被破坏。
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        var injected = false;
        using var h = new Harness(router, reenterOnWrite: (adapter, value) =>
        {
            // 一次性注入：只在交付 x 的那次写入里追加 y。
            if (!injected && value.Contains('x'))
            {
                injected = true;
                adapter.HandleTextInput(new TextInputEventArgs('y'));
            }
        });

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('x'));
        Assert.Equal(1, router.Coordinator.DeferredCount);

        // 恢复安全后，当前输入 z 在旧项 x 仍排队时到达。
        router.ForceUnsafeForTesting = false;
        h.Adapter.HandleTextInput(new TextInputEventArgs('z'));

        // 真实字段顺序必须是 x → z → y（z 不得被 x 的 setter 追加项越过）。
        Assert.Equal(0, router.Coordinator.DeferredCount);
        Assert.Equal(3, h.WritesA);
        Assert.Equal(3, h.WritesWhileDelivering);
        Assert.Equal(0, h.NestedSetterCount);
        var ix = h.TextA.IndexOf('x');
        var iz = h.TextA.IndexOf('z');
        var iy = h.TextA.IndexOf('y');
        Assert.True(ix >= 0 && ix < iz && iz < iy, $"order violated in '{h.TextA}'");
    }

    // ---------------------------------------------------------------- 单轮预算（持续来源也必须返回）

    [Fact]
    public void 持续追加来源在单轮预算内返回且FIFO不被新来源越过()
    {
        // 真正持续来源：setter 每写一次就追加一条新输入（**没有**一次性标志）。
        // 没有单轮预算时本轮排空永不返回（R10 实测挂起）；有预算则必须返回，
        // 且更早排队项先交付、剩余项按 FIFO 留给下一轮。
        static char Injected(int index) => (char)(0x4E00 + index);

        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        var injected = 0;
        using var h = new Harness(router, reenterOnWrite: (adapter, _) =>
        {
            adapter.HandleTextInput(new TextInputEventArgs(Injected(injected++)));
        });

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('p'));
        h.Adapter.HandleTextInput(new TextInputEventArgs('q'));
        Assert.Equal(2, router.Coordinator.DeferredCount);

        // 订阅放在入队之后：只统计本轮“预算耗尽 → 让出”的信号。
        var wakes = 0;
        h.Adapter.RequestWake = () => wakes++;
        h.Adapter.DeferredInputQueued += () => wakes++;

        router.ForceUnsafeForTesting = false;
        var budget = OpenTkInputAdapter.MaxOrderedDeliveriesPerTurn;
        var delivered = h.Adapter.DeliverDeferredAtSafePhase();

        // 单轮**返回**且工作量恰好被预算截断：不是无界排空。
        Assert.Equal(budget, delivered);
        Assert.Equal(budget, h.WritesA);
        Assert.Equal(0, h.NestedSetterCount);
        Assert.Equal(h.WritesA, h.WritesWhileDelivering);

        // 更早排队项 p、q 先交付；持续来源的追加项不得越过它们。
        Assert.True(h.TextA.IndexOf('p') >= 0 && h.TextA.IndexOf('p') < h.TextA.IndexOf('q'),
            $"queued order violated in '{h.TextA}'");
        Assert.True(h.TextA.IndexOf('q') < h.TextA.IndexOf(Injected(0)),
            $"new source overtook queued items in '{h.TextA}'");

        // p 注入 0、q 注入 1，之后每次交付注入下一个：本轮交付注入项 0..budget-3，
        // 顺序必须与注入顺序一致；未交付的队尾项按 FIFO 保留。
        for (var i = 1; i <= budget - 3; i++)
        {
            var previous = h.TextA.IndexOf(Injected(i - 1));
            var current = h.TextA.IndexOf(Injected(i));
            Assert.True(previous >= 0 && current > previous, $"order violated at {i} in '{h.TextA}'");
        }

        Assert.True(h.TextA.Contains(Injected(budget - 3)));
        Assert.False(h.TextA.Contains(Injected(budget - 2)));
        Assert.Equal(2, router.Coordinator.DeferredCount);

        // 预算耗尽 → 只让出**一次**唤醒给正常 loop（不是周期轮询）。
        Assert.Equal(1, wakes);
    }

    [Fact]
    public void 跨轮预算交付保持FIFO且每轮只让出一次唤醒()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        const int Total = 200; // 超过单轮预算，强制跨轮
        router.ForceUnsafeForTesting = true;
        for (var i = 0; i < Total; i++)
        {
            h.Adapter.HandleTextInput(new TextInputEventArgs((char)(0x4E00 + i)));
        }

        Assert.Equal(Total, router.Coordinator.DeferredCount);
        Assert.Equal(0, h.WritesA);

        var wakes = 0;
        h.Adapter.RequestWake = () => wakes++;
        h.Adapter.DeferredInputQueued += () => wakes++;

        router.ForceUnsafeForTesting = false;

        var budget = OpenTkInputAdapter.MaxOrderedDeliveriesPerTurn;
        var rounds = 0;
        while (router.Coordinator.DeferredCount > 0)
        {
            var wakesBefore = wakes;
            var delivered = h.Adapter.DeliverDeferredAtSafePhase();

            Assert.True(delivered > 0 && delivered <= budget, $"delivered={delivered}");
            Assert.True(wakes - wakesBefore <= 1, $"每轮让出次数超预算：{wakes - wakesBefore}");
            rounds++;
            Assert.True(rounds <= 8, "轮次失控：单轮预算没有真正限制工作量。");
        }

        Assert.Equal((Total + budget - 1) / budget, rounds);
        Assert.Equal(Total, h.WritesA);

        // 全部项按提交顺序交付（交付顺序即字符串顺序，与初始 caret 无关）。
        for (var i = 1; i < Total; i++)
        {
            var previous = h.TextA.IndexOf((char)(0x4E00 + i - 1));
            var current = h.TextA.IndexOf((char)(0x4E00 + i));
            Assert.True(previous >= 0 && current > previous, $"order violated at {i}");
        }
    }

    // ---------------------------------------------------------------- 快捷键 / 失活共享语义

    [Fact]
    public void 延后交付与立即交付共用关闭快捷键语义()
    {
        // 立即路径：Ctrl+Q 触发退出。
        var immediateExits = 0;
        var immediateRouter = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(immediateRouter, () => immediateExits++))
        {
            h.Adapter.HandleKeyDown(new KeyboardKeyEventArgs(
                Keys.Q, 0, GlfwKeyModifiers.Control, false));
        }

        // 延后路径：同一按键经 FIFO 重放交付，必须产生**相同**退出副作用。
        var deferredExits = 0;
        var deferredRouter = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(deferredRouter, () => deferredExits++))
        {
            deferredRouter.ForceUnsafeForTesting = true;
            h.Adapter.HandleKeyDown(new KeyboardKeyEventArgs(
                Keys.Q, 0, GlfwKeyModifiers.Control, false));
            Assert.Equal(0, deferredExits);

            deferredRouter.ForceUnsafeForTesting = false;
            Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        }

        Assert.Equal(1, immediateExits);
        Assert.Equal(1, deferredExits);
    }

    [Fact]
    public void 延后失活清理与立即失活清理交付相同指针取消()
    {
        // 立即路径：失活时取消指针捕获并派发 Cancel。
        var immediateRouter = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(immediateRouter))
        {
            h.Adapter.HandleMouseMove(new MouseMoveEventArgs(10, 20, 0, 0));
            h.Adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, 0));
            h.Adapter.HandleFocusChanged(false);

            Assert.Equal(PointerEventKind.Cancel, h.Adapter.LastPointerEvent!.Value.Kind);
            Assert.Equal(3, h.Adapter.PointerEventCount);
        }

        // 延后路径：同样的输入经队列重放，副作用必须一致（不因重放只做半个 handler 而留下状态）。
        var deferredRouter = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using (var h = new Harness(deferredRouter))
        {
            deferredRouter.ForceUnsafeForTesting = true;
            h.Adapter.HandleMouseMove(new MouseMoveEventArgs(10, 20, 0, 0));
            h.Adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, 0));
            h.Adapter.HandleFocusChanged(false);

            Assert.Equal(3, deferredRouter.Coordinator.DeferredCount);
            Assert.Equal(0, h.Adapter.PointerEventCount);

            deferredRouter.ForceUnsafeForTesting = false;
            Assert.Equal(3, h.Adapter.DeliverDeferredAtSafePhase());

            Assert.Equal(PointerEventKind.Cancel, h.Adapter.LastPointerEvent!.Value.Kind);
            Assert.Equal(3, h.Adapter.PointerEventCount);
        }
    }

    // ---------------------------------------------------------------- 有界唤醒（非周期轮询）

    [Fact]
    public void 持续不安全时不周期轮询且恢复安全后单次唤醒交付()
    {
        var router = new WindowsTextInputRouter(immSubBridge: null, enableTsf: false);
        using var h = new Harness(router);

        // 按宿主接线方式订阅：队列转移 + 真实安全边界退出，各请求一次唤醒。
        var wakes = 0;
        h.Adapter.RequestWake = () => wakes++;
        h.Adapter.DeferredInputQueued += () => wakes++;
        router.Coordinator.SafePhaseExited += () => wakes++;

        router.ForceUnsafeForTesting = true;
        h.Adapter.HandleTextInput(new TextInputEventArgs('w'));

        // 入队一次 → 队列转移通知 + 单次唤醒请求。
        var wakesAfterQueue = wakes;
        Assert.True(wakesAfterQueue >= 1, $"wakes={wakesAfterQueue}");

        // 持续不安全：反复尝试交付都不应产生额外唤醒（不是周期轮询）。
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(0, h.Adapter.DeliverDeferredAtSafePhase());
        }

        // 持续不安全期间唤醒次数**不再增长**（证明不是周期轮询）。
        Assert.Equal(wakesAfterQueue, wakes);
        Assert.Equal(1, router.Coordinator.DeferredCount);

        // 真实安全边界退出 → 再通知一次，交付成功。
        router.ForceUnsafeForTesting = false;
        router.Coordinator.RaiseSafePhaseExitedForTesting();
        Assert.Equal(wakesAfterQueue + 1, wakes);

        Assert.Equal(1, h.Adapter.DeliverDeferredAtSafePhase());
        Assert.Contains('w', h.TextA);
        Assert.Equal(0, router.Coordinator.DeferredCount);
    }
    // ---------------------------------------------------------------- 顺序屏障

    [Fact]
    public void 延后项按字面到达顺序重放()
    {
        var coordinator = new HostInputCoordinator();
        coordinator.Defer(DeferredInputAction.ForText(1, "first"));
        coordinator.Defer(DeferredInputAction.ForPointer(2, new DeferredPointerInput(
            PointerEventKind.Down, new Point(1, 2), PointerButton.Left, KeyModifiers.None, 1)));
        coordinator.Defer(DeferredInputAction.ForText(3, "third"));

        Assert.True(coordinator.TryDequeue(out var a));
        Assert.True(coordinator.TryDequeue(out var b));
        Assert.True(coordinator.TryDequeue(out var c));

        // 字面顺序：文本 → 指针 → 文本，不被重排。
        Assert.Equal(1L, a!.Sequence);
        Assert.Equal(2L, b!.Sequence);
        Assert.Equal(3L, c!.Sequence);
        Assert.Equal("first", a.Text);
        Assert.Equal("third", c.Text);
    }

    [Fact]
    public void 重放期间再次不安全时该项回到队首保持顺序()
    {
        var coordinator = new HostInputCoordinator();
        coordinator.Defer(DeferredInputAction.ForText(1, "keep"));

        Assert.True(coordinator.TryDequeue(out var taken));
        coordinator.RequeueFront(taken!);

        Assert.True(coordinator.TryDequeue(out var again));
        Assert.Equal("keep", again!.Text);
        Assert.Equal(1L, again.Sequence);
    }
}
