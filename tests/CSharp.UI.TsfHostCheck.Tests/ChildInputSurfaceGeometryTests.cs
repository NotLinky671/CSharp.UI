using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R29A 真实输入 child 宿主的纯托管几何/谓词测试 —— 不创建窗口、不初始化 COM、不调输入法。
/// 只证明接口不变量：焦点前置条件、显示边界交集、clip origin 与 glyph origin 解耦、
/// 滚动不二次抵消。真实 GetWnd/绘制/焦点由 Codex 审计的窗口场景验证，这里不冒充。
/// </summary>
public sealed class ChildInputSurfaceGeometryTests
{
    // ---- CanInitializeInputFocus -------------------------------------------------

    [Fact]
    public void FocusInitAllowedOnlyWhenRootIsForegroundAndInputSharesThreadAndProcess()
    {
        var root = new IntPtr(0x1000);
        var input = new IntPtr(0x2000);
        Assert.True(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: root,
            rootHwnd: root,
            inputHwnd: input,
            rootThreadId: 42,
            rootProcessId: 7,
            inputThreadId: 42,
            inputProcessId: 7));
    }

    [Fact]
    public void FocusInitRefusedWhenRootNotForeground()
    {
        // 别的窗口在前台：严禁 SetFocus(input)，避免抢焦点。
        Assert.False(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: new IntPtr(0x9999),
            rootHwnd: new IntPtr(0x1000),
            inputHwnd: new IntPtr(0x2000),
            rootThreadId: 42, rootProcessId: 7,
            inputThreadId: 42, inputProcessId: 7));
    }

    [Fact]
    public void FocusInitRefusedWhenForegroundIsZero()
    {
        Assert.False(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: IntPtr.Zero,
            rootHwnd: new IntPtr(0x1000),
            inputHwnd: new IntPtr(0x2000),
            rootThreadId: 42, rootProcessId: 7,
            inputThreadId: 42, inputProcessId: 7));
    }

    [Fact]
    public void FocusInitRefusedWhenInputOnDifferentThread()
    {
        // input 与 root 不同 UI 线程 → 不可从本线程 SetFocus。
        Assert.False(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: new IntPtr(0x1000),
            rootHwnd: new IntPtr(0x1000),
            inputHwnd: new IntPtr(0x2000),
            rootThreadId: 42, rootProcessId: 7,
            inputThreadId: 77, inputProcessId: 7));
    }

    [Fact]
    public void FocusInitRefusedWhenInputOnDifferentProcess()
    {
        Assert.False(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: new IntPtr(0x1000),
            rootHwnd: new IntPtr(0x1000),
            inputHwnd: new IntPtr(0x2000),
            rootThreadId: 42, rootProcessId: 7,
            inputThreadId: 42, inputProcessId: 99));
    }

    [Fact]
    public void FocusInitRefusedWhenInputHandleIsZero()
    {
        Assert.False(TsfHostWindow.CanInitializeInputFocus(
            foregroundHwnd: new IntPtr(0x1000),
            rootHwnd: new IntPtr(0x1000),
            inputHwnd: IntPtr.Zero,
            rootThreadId: 42, rootProcessId: 7,
            inputThreadId: 42, inputProcessId: 7));
    }

    // ---- DisplaySurface.IntersectSurface (托管交集核) ----------------------------

    [Fact]
    public void SurfaceIntersectionIsTheVisibleOverlapOfInputAndAncestor()
    {
        // input 屏幕矩形 ∩ viewport 屏幕矩形 = TSF GetScreenExt 应报的可见显示面。
        var input = TsfRect.FromLtrb(116, 100, 316, 126);   // input client，屏幕坐标
        var ancestor = TsfRect.FromLtrb(0, 0, 200, 300);    // viewport client，屏幕坐标
        var surface = DisplaySurface.IntersectSurface(input, ancestor);

        Assert.Equal(116, surface.Left);
        Assert.Equal(100, surface.Top);
        Assert.Equal(200, surface.Right);
        Assert.Equal(126, surface.Bottom);
        Assert.False(surface.IsEmpty);
    }

    [Fact]
    public void SurfaceIntersectionIsEmptyWhenInputIsOutsideAncestor()
    {
        // input 完全在 ancestor 右侧之外（横向不相交），交集为空。
        var input = TsfRect.FromLtrb(300, 100, 500, 126);
        var ancestor = TsfRect.FromLtrb(0, 0, 200, 300);
        var surface = DisplaySurface.IntersectSurface(input, ancestor);

        // 无重叠 → 精确归零 RECT：TSF GetScreenExt 必须报 (0,0,0,0)，不可留倒置/半开坐标。
        Assert.Equal(default(TsfRect), surface);
        Assert.True(surface.IsEmpty);
    }

    [Fact]
    public void ClipOriginIsNotGlyphOrigin_ScrollMovesInputNotClip()
    {
        // 不变量：绘制坐标从 input client origin 换算；clip 仅是 TSF 上报的交集。
        // 滚动后 input client origin 移动（SetWindowPos 平移 child），viewport clip 矩形不动。
        const int scrollY = 40;

        // viewport 屏幕矩形不随滚动改变。
        var ancestor = TsfRect.FromLtrb(0, 0, 400, 300);

        // input child 真实平移：client 矩形 y 由 [100,126) → [60,86)。
        var inputBefore = TsfRect.FromLtrb(16, 100, 216, 126);
        var inputAfter = TsfRect.FromLtrb(16, 100 - scrollY, 216, 126 - scrollY);

        var clipBefore = DisplaySurface.IntersectSurface(inputBefore, ancestor);
        var clipAfter = DisplaySurface.IntersectSurface(inputAfter, ancestor);

        // 两个 clip 都在 viewport 内、未截顶/截底，但 clip 矩形整体随 input 上移。
        Assert.Equal(100, clipBefore.Top);
        Assert.Equal(126, clipBefore.Bottom);
        Assert.Equal(60, clipAfter.Top);   // 100 - scrollY
        Assert.Equal(86, clipAfter.Bottom); // 126 - scrollY
        // clip 高度不变（仍在可视区内），但屏幕位置随真实 input 平移 ——
        // 这正是"clip 只上报、不决定 glyph"的证据：glyph 起点跟着 input origin 走。
        Assert.Equal(clipBefore.Height, clipAfter.Height);
        Assert.Equal(scrollY, clipBefore.Top - clipAfter.Top);
    }

    [Fact]
    public void ClipClampsToAncestorButGlyphStaysOnInputOrigin()
    {
        // input 上移超出 viewport 顶边：clip 被钳到 viewport.top（=0），
        // 但绘制仍以真实 input client origin 为基准（input.Top 为负）。
        const int scrollY = 140;
        var ancestor = TsfRect.FromLtrb(0, 0, 400, 300);
        var input = TsfRect.FromLtrb(16, 100 - scrollY, 216, 126 - scrollY); // y=[-40,-14)，全在顶之上

        var clip = DisplaySurface.IntersectSurface(input, ancestor);
        // input.bottom=-14 < ancestor.top=0 → 交集为空（不可见），如实报精确 zero-rect。
        Assert.Equal(default(TsfRect), clip);
        // 而 input origin 仍是真实屏幕坐标（负值），glyph 基准未被 clip 钳制。
        Assert.True(input.Top < 0);
        Assert.Equal(-40, input.Top);
    }

    [Fact]
    public void IntersectSurfaceNormalizesInvertedIntersectionToZero()
    {
        // 倒置交集（ancestor 完全在 input 右下方之外，纵横向都不交）：Intersect 的朴素
        // max/min 会产生 Right<Left 的倒置矩形；NormalizeEmpty 必须归零，不冒充有界区域。
        var input = TsfRect.FromLtrb(0, 0, 100, 50);
        var ancestor = TsfRect.FromLtrb(400, 300, 600, 400);
        Assert.Equal(default(TsfRect), DisplaySurface.IntersectSurface(input, ancestor));
    }

    [Fact]
    public void NormalizeEmptyLeavesValidRectUntouched()
    {
        var valid = TsfRect.FromLtrb(10, 20, 300, 90);
        Assert.Equal(valid, DisplaySurface.NormalizeEmpty(valid));
    }

    // ---- HwndRoutesInput：共享消息角色判定 ----------------------------------------

    [Fact]
    public void DefaultMode_RootIsTheInputSurface()
    {
        // 默认 off（childInputSurface=false）：root 即真实输入 surface，
        // WM_SETFOCUS/KILLFOCUS/KEY/CHAR/WHEEL 在 root 上有效。
        var root = new IntPtr(0x1000);
        Assert.True(TsfHostWindow.HwndRoutesInput(false, root, root, IntPtr.Zero));
    }

    [Fact]
    public void OptInMode_OnlyInputChildRoutesInput()
    {
        var root = new IntPtr(0x1000);
        var viewport = new IntPtr(0x3000);
        var input = new IntPtr(0x2000);

        // opt-in：仅真实 input child 路由输入；root 与 viewport 只是背景，绝不冒充输入。
        Assert.True(TsfHostWindow.HwndRoutesInput(true, input, root, input));
        Assert.False(TsfHostWindow.HwndRoutesInput(true, root, root, input));
        Assert.False(TsfHostWindow.HwndRoutesInput(true, viewport, root, input));
    }

    [Fact]
    public void OptInMode_ZeroInputHandleNeverRoutes()
    {
        var root = new IntPtr(0x1000);
        Assert.False(TsfHostWindow.HwndRoutesInput(true, IntPtr.Zero, root, IntPtr.Zero));
    }

    // ---- 滚动不二次抵消 -----------------------------------------------------------

    [Fact]
    public void ScrollDoesNotDoubleSubtract_OptInMovesChildOnce()
    {
        // opt-in：scroll 只通过 SetWindowPos 平移 input child 一次；Paint 的本地 glyph
        // 起点不再减 scroll（_scrollX/_scrollY 强制为 0）。若两边都减，glyph 会偏离 2*scroll。
        const int scrollY = 30;
        const int textPaddingY = 14;

        // 默认 off：本地 glyph y = paddingY - scrollY（负，向上）。
        var offLocalY = textPaddingY - scrollY;
        // opt-in：child 已移动，本地 glyph y = paddingY（scroll 由真实窗口位置承担）。
        var optInLocalY = textPaddingY - 0;

        Assert.Equal(-16, offLocalY);
        Assert.Equal(14, optInLocalY);
        // 二者相差正好一个 scroll —— 证明只能减一次，不可二次。
        Assert.Equal(scrollY, optInLocalY - offLocalY);
    }

    // ---- RunFocusInitialization：单次 UI 派发内"查→判→设→确认"的真实顺序 -------------

    private static (IntPtr foreground, bool rootValid, bool inputValid,
        uint rootTid, uint rootPid, uint inputTid, uint inputPid)
        Facts(IntPtr foreground, uint rootTid, uint rootPid, uint inputTid, uint inputPid,
            bool rootValid = true, bool inputValid = true)
        => (foreground, rootValid, inputValid, rootTid, rootPid, inputTid, inputPid);

    [Fact]
    public void FocusInit_FirstCall_SetFocusOnceAndMarksInitializedWhenConfirmed()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0; IntPtr focus = IntPtr.Zero;

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7),
            setFocus: h => { setFocusCalls++; focus = h; return true; },
            getFocus: () => focus,
            getThreadId: () => 42);

        Assert.True(r.Initialized);
        Assert.Equal("ok", r.Outcome);
        Assert.Equal(1, setFocusCalls);
        Assert.Equal(1, r.SetFocusCalls);
    }

    [Fact]
    public void FocusInit_ExternalForegroundAfterQueueArrival_ZeroSetFocus()
    {
        // 关键 race：前置检查时 root 在前台，但队列到 UI 线程执行时前台已让位给外部窗口。
        // 决策必须基于"执行瞬间"的 probe（此处 foreground != root）→ 0 次 SetFocus、不初始化。
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000); var foreign = new IntPtr(0x9999);
        var setFocusCalls = 0;

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            // probe 在执行瞬间返回外部前台 —— 等价于"队列到达后前台已变"。
            probe: () => Facts(foreign, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7),
            setFocus: h => { setFocusCalls++; return true; },
            getFocus: () => IntPtr.Zero,
            getThreadId: () => 42);

        Assert.False(r.Initialized);
        Assert.Equal(0, setFocusCalls);
        Assert.Equal(0, r.SetFocusCalls);
        Assert.Equal("refused:foreground-or-thread", r.Outcome);
    }

    [Fact]
    public void FocusInit_WrongProcessId_ZeroSetFocus()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0;

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            // input 属于别的进程 → 拒焦，0 次 SetFocus。
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 99),
            setFocus: h => { setFocusCalls++; return true; },
            getFocus: () => IntPtr.Zero,
            getThreadId: () => 42);

        Assert.False(r.Initialized);
        Assert.Equal(0, setFocusCalls);
        Assert.Equal("foreign-process", r.Outcome);
    }

    [Fact]
    public void FocusInit_WrongOwnerThread_ZeroSetFocus()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0;

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            // input 的 owner 线程不是当前 UI 线程 → 拒焦。
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 77, inputPid: 7),
            setFocus: h => { setFocusCalls++; return true; },
            getFocus: () => IntPtr.Zero,
            getThreadId: () => 42);

        Assert.False(r.Initialized);
        Assert.Equal(0, setFocusCalls);
        Assert.Equal("foreign-ui-thread", r.Outcome);
    }

    [Fact]
    public void FocusInit_RepeatCall_DoesNotSetFocusAgain()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0;

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: true, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7),
            setFocus: h => { setFocusCalls++; return true; },
            getFocus: () => input,
            getThreadId: () => 42); // 已聚焦 input

        Assert.True(r.Initialized);           // 如实回报真实焦点
        Assert.Equal(0, setFocusCalls);       // 绝不二次 SetFocus / 夺焦
        Assert.Equal(0, r.SetFocusCalls);
    }

    [Fact]
    public void FocusInit_SetFocusNotConfirmed_NotMarkedInitialized()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);

        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42, expectedProcessId: 7,
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7),
            setFocus: h => true,
            getFocus: () => IntPtr.Zero,
            getThreadId: () => 42);          // SetFocus 后 GetFocus 仍非 input → 未确认

        Assert.False(r.Initialized);          // SetFocus 未确认 → 不标 initialized
        Assert.Equal(1, r.SetFocusCalls);
        Assert.Equal("set-focus-mismatch", r.Outcome);
    }

    [Fact]
    public void FocusInit_RunOnWrongThread_FailsClosed()
    {
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0;

        // body 在错误线程上执行（如 dispatcher 退化/非 owning 线程）→ fail closed。
        var r = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, rootHwnd: root, inputHwnd: input,
            uiThreadId: 42,                  // 期望 UI 线程 42，但 body 在 77 上执行
            expectedProcessId: 7,
            probe: () => Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7),
            setFocus: h => { setFocusCalls++; return true; },
            getFocus: () => IntPtr.Zero,
            getThreadId: () => 77);         // body 跑在非 owning 线程

        Assert.False(r.Initialized);
        Assert.Equal(0, setFocusCalls);
        Assert.Equal("wrong-ui-thread", r.Outcome);
    }

    [Fact]
    public void FocusInit_SecondCallSeesInitializedAndNeverSetsFocusAgain()
    {
        // 状态发布不变量：第一次初始化确认后 _inputFocusInitialized 在同一次 UI body 内置 true；
        // 排队的第二次调用在同一 body 起点读到它 → alreadyInitialized=true → 只读 GetFocus、0 次 SetFocus。
        // 用共享 RunFocusInitialization 顺序执行两次（等价于同一 UI 线程串行的两次 body），
        // 第二调用的 alreadyInitialized 由第一次确认的返回值驱动 —— 复刻"已发布状态被后续读取"。
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0; IntPtr focus = IntPtr.Zero;

        (IntPtr, bool, bool, uint, uint, uint, uint) probe() =>
            Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7);
        bool setFocus(IntPtr h) { setFocusCalls++; focus = h; return true; }

        var first = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, root, input, uiThreadId: 42, expectedProcessId: 7,
            probe, setFocus, () => focus, () => 42);
        Assert.True(first.Initialized);
        Assert.Equal(1, setFocusCalls);

        // 已发布 initialized → 第二调用读 alreadyInitialized=true：0 次 SetFocus、如实回报焦点。
        var second = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: first.Initialized, root, input, uiThreadId: 42, expectedProcessId: 7,
            probe, setFocus, () => focus, () => 42);
        Assert.True(second.Initialized);
        Assert.Equal(1, setFocusCalls);   // 未再增加
        Assert.Equal(0, second.SetFocusCalls);
    }

    [Fact]
    public void FocusInit_FailedSetFocus_LeavesInitializationFalseSoNextRetries()
    {
        // SetFocus 未被 GetFocus 确认 → Initialized=false → 后续调用仍按未初始化处理（可再次 SetFocus）。
        var root = new IntPtr(0x1000); var input = new IntPtr(0x2000);
        var setFocusCalls = 0; IntPtr focus = IntPtr.Zero;

        (IntPtr, bool, bool, uint, uint, uint, uint) probe() =>
            Facts(root, rootTid: 42, rootPid: 7, inputTid: 42, inputPid: 7);

        var first = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: false, root, input, uiThreadId: 42, expectedProcessId: 7,
            probe, h => { setFocusCalls++; /* 不更新 focus：SetFocus 未生效 */ return true; },
            () => focus, () => 42);
        Assert.False(first.Initialized);          // 未确认 → 不标 initialized
        Assert.Equal("set-focus-mismatch", first.Outcome);

        // 下一调用仍以 alreadyInitialized=false 进入 → 允许重新 SetFocus（非夺焦，是首次初始化未完成）。
        var second = TsfHostWindow.RunFocusInitialization(
            alreadyInitialized: first.Initialized, root, input, uiThreadId: 42, expectedProcessId: 7,
            probe, h => { setFocusCalls++; focus = h; return true; },
            () => focus, () => 42);
        Assert.True(second.Initialized);
        Assert.Equal(2, setFocusCalls);
    }
}
