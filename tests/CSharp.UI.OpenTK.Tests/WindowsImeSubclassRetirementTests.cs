using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 退休（Dispose）子类化语义的<b>真实隐藏 HWND</b> 回归：
/// 本桥不再是顶层窗口过程时绝不覆盖外部过程，只有回到顶层才恢复原过程并释放租约。
/// </summary>
/// <remarks>
/// <para>
/// <b>覆盖的缺陷：</b>恢复路径若无条件 <c>SetWindowLongPtrW(GWLP_WNDPROC)</c>，会把后来安装的外部过程
/// 直接覆盖掉；且覆盖后仍可能归还租约，让第二个 writer 与仍在链上的回调并存。
/// </para>
/// <para>
/// <b>真实原生对象：</b>用 STATIC 内置类创建隐藏窗口（不注册/不反注册自定义类，避免影响进程其它窗口），
/// 真实 <c>SetWindowLongPtrW</c>/<c>GetWindowLongPtrW</c> 安装与外部过程后装；全部调用只作用于本测试自己的 HWND。
/// </para>
/// <para>
/// <b>不做的事：</b>不显示窗口、不投真实键盘、不改全局输入法配置、不调用 <c>HostWriterRegistry.ResetForTesting</c>
/// （那会清掉其它并行测试的真实持有者）；租约断言只收紧不放宽。
/// </para>
/// </remarks>
public sealed class WindowsImeSubclassRetirementTests
{
    private const int WS_OVERLAPPED = 0x00000000;
    private const int GWLP_WNDPROC = -4;

    [Fact]
    public void 外部后装过程不被覆盖且回到顶层后才恢复并释放租约()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：没有 SetWindowLongPtrW/GWLP_WNDPROC，真实子类化不适用。
            return;
        }

        var className = "STATIC";
        var instance = GetModuleHandleW(null);

        // 隐藏窗口：只作为 HWND 与子类化载体，不调用 ShowWindow。
        var hwnd = CreateWindowExW(0, className, "CSharp.UI IME retirement (hidden)", WS_OVERLAPPED,
            0, 0, 200, 80, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            // 无法创建真实窗口时如实跳过：本用例的全部结论都依赖真实 HWND。
            return;
        }

        var ime = new RecordingIme();
        var dispatcher = new OpenTkUiDispatcher(() => { });
        var text = string.Empty;
        var writes = 0;
        WindowsImeBridge? bridge = null;
        UiRuntime? runtime = null;

        // 外部过程：链式交给默认过程，模拟“后来安装的第二个子类化”。
        WndProcDelegate externalProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
        var externalPtr = Marshal.GetFunctionPointerForDelegate(externalProc);

        // 外部过程的委托根由本测试持有到用例结束：GC 后仍可回收本桥（本桥不再被原生引用）。
        try
        {
            // 类过程（STATIC 内置 DefWindowProcW）：安装前的真实顶层过程，也是本桥要恢复的目标。
            var classProc = GetWindowProcPtr(hwnd);

            bridge = new WindowsImeBridge(hwnd, ime, dispatcher, () => (200, 80));
            bridge.InstallSubclass();
            Assert.True(bridge.SubclassInstalled, "真实 HWND 上子类化必须安装成功。");
            Assert.Null(bridge.InstallError);
            Assert.NotNull(HostWriterRegistry.CurrentLease(hwnd));
            Assert.True(bridge.HoldsWriterLease);

            // 本桥安装后顶层过程已不是类过程：记下它作为“移除外部后回到的顶层”。
            var bridgeProc = GetWindowProcPtr(hwnd);
            Assert.NotEqual(classProc, bridgeProc);

            // 真实 TextField 权威客户端：退休后迟到组合消息必须零写入。
            runtime = new UiRuntime(
                new TextField(new Binding<string>(() => text, v => { text = v; writes++; })),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = bridge,
                });
            bridge.AttachRuntime(runtime);
            runtime.SetViewport(new Size(200, 80), 1.0);
            runtime.Start();
            runtime.BuildFrame();
            runtime.SetFocus(runtime.Root);
            runtime.BuildFrame();
            Assert.NotNull(bridge.ActiveClient);

            bridge.HandleMessage(hwnd, Win32Ime.WM_IME_STARTCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
            ime.Composition = "ni";
            ime.Cursor = 2;
            bridge.HandleMessage(hwnd, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero,
                new IntPtr(Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS));
            var writesBeforeRetirement = writes;

            // 后装外部过程：本桥不再位于顶层，原生回调仍可经外部过程链式到达。
            Assert.Equal(bridgeProc, SetWindowLongPtrW(hwnd, GWLP_WNDPROC, externalPtr));
            Assert.Equal(externalPtr, GetWindowProcPtr(hwnd));

            // 退休：绝不覆盖外部过程，保留租约与安装状态（本桥仍是实际 writer 的一部分）。
            bridge.Dispose();
            Assert.Equal(externalPtr, GetWindowProcPtr(hwnd));
            Assert.True(bridge.SubclassInstalled, "外部过程仍在顶层时不得虚报已恢复。");
            Assert.False(bridge.SubclassRestored);
            Assert.True(bridge.RestoreFailureCount >= 1);
            Assert.True(bridge.HoldsWriterLease, "外部过程仍在顶层时必须保留租约，禁止第二个 writer 接管。");
            Assert.Equal(HostWriterKind.Imm, HostWriterRegistry.CurrentLease(hwnd)!.Kind);

            // 保留租约期间第二个独立 writer 必须被拒绝（不放宽租约断言）。
            Assert.False(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var blocked, out var blockedReason));
            Assert.Null(blocked);
            Assert.False(string.IsNullOrEmpty(blockedReason));

            // 退休后迟到组合/结果消息：零写入、不响应答。
            bridge.HandleMessage(hwnd, Win32Ime.WM_IME_STARTCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
            ime.Composition = null;
            ime.Cursor = -1;
            ime.Result = "你好";
            bridge.HandleMessage(hwnd, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero, new IntPtr(Win32Ime.GCS_RESULTSTR));
            bridge.HandleMessage(hwnd, Win32Ime.WM_IME_ENDCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(writesBeforeRetirement, writes);
            Assert.Equal(string.Empty, text);
            Assert.Null(bridge.ActiveClient);
            Assert.Equal(0, bridge.CallbackExceptionCount);

            // 移除外部过程：本桥重回顶层（原过程仍是本桥安装时的原过程）。
            Assert.Equal(externalPtr, SetWindowLongPtrW(hwnd, GWLP_WNDPROC, bridgeProc));
            Assert.Equal(bridgeProc, GetWindowProcPtr(hwnd));

            // 再次 Dispose（owner 线程重试入口）：恢复原过程、释放租约，不再钉住委托根。
            bridge.Dispose();
            Assert.False(bridge.SubclassInstalled);
            Assert.True(bridge.SubclassRestored);
            Assert.Equal(classProc, GetWindowProcPtr(hwnd));
            Assert.False(bridge.HoldsWriterLease);
            Assert.Null(HostWriterRegistry.CurrentLease(hwnd));
            Assert.True(bridge.RestoreFailureCount >= 1); // 失败次数是历史事实，不因重试成功而清零。

            // 后续来源可以正常接管本窗口（租约确实归还）。
            Assert.True(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var next, out _));
            Assert.True(HostWriterRegistry.Release(next));
            Assert.Null(HostWriterRegistry.CurrentLease(hwnd));

            // 重复 Dispose 幂等：不重复归还、不改变已恢复状态。
            bridge.Dispose();
            Assert.False(bridge.HoldsWriterLease);
            Assert.False(bridge.SubclassInstalled);
            Assert.True(bridge.SubclassRestored);
        }
        finally
        {
            runtime?.Dispose();
            try
            {
                bridge?.Dispose();
            }
            catch (Exception)
            {
            }

            if (bridge is not null && HostWriterRegistry.CurrentLease(hwnd) is { } leftover)
            {
                HostWriterRegistry.Release(leftover);
            }

            // 外部过程委托根在用例内保持存活；销毁窗口后原生侧不再持有任何指针。
            GC.KeepAlive(externalProc);
            DestroyWindow(hwnd);
        }
    }

    private static IntPtr GetWindowProcPtr(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GWLP_WNDPROC);

    /// <summary>只记录组合数据的最小 IMM 互操作替身（不触碰真实输入上下文）。</summary>
    private sealed class RecordingIme : IImeInterop
    {
        private static int _nextHandle = 0x3000;
        private readonly IntPtr _context = new(Interlocked.Increment(ref _nextHandle));

        public string? Composition;
        public string? Result;
        public int Cursor = -1;

        public IntPtr GetContext() => _context;

        public void ReleaseContext(IntPtr himc)
        {
        }

        public IntPtr EnsureContext() => _context;

        public ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags)
            => new(flags, Composition, Result, Cursor, []);

        public bool SetCandidatePosition(IntPtr himc, int x, int y) => true;

        public bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom) => true;

        public bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom) => true;

        public bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style)
        {
            x = y = 0;
            style = 0;
            return false;
        }

        public bool NotifyCancelComposition(IntPtr himc) => true;

        public bool GetNativeInputState(IntPtr himc, out bool open, out uint conversion, out uint sentence)
        {
            open = true;
            conversion = Win32Ime.IME_CMODE_NATIVE;
            sentence = 0;
            return himc != IntPtr.Zero;
        }

        public bool SetNativeInputState(IntPtr himc, bool open, uint conversion, uint sentence)
            => himc != IntPtr.Zero;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
}
