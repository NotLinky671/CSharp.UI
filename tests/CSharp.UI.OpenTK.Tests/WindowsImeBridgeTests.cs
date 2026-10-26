using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using GlfwKeyModifiers = OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 IME 桥测试：消息→组合事件翻译、结果回声归属、按键 token、候选锚点坐标与资源状态，
/// 全部在真实 <see cref="UiRuntime"/> + <see cref="TextField"/> 上进行（原生 HWND 由假互操作替身代替）。
/// </summary>
public sealed class WindowsImeBridgeTests
{
    // ------------------------------------------------------------ 假互操作

    private sealed class FakeIme : IImeInterop
    {
        private static int _nextHandle = 0x1000;
        public IntPtr Context = new(Interlocked.Increment(ref _nextHandle));
        public string? Composition;
        public string? Result;
        public int Cursor = -1;
        public List<int> Clauses = [];
        public bool CancelResult = true;
        public int EnsureCalls;
        public int ReleaseCount;
        public int CancelCalls;
        public List<(int X, int Y)> CandidatePositions = [];
        public List<(int L, int T, int R, int B)> CandidateExcludes = [];
        public List<(int X, int Y, int L, int T, int R, int B)> CompositionWindows = [];

        public IntPtr GetContext() => Context;

        public void ReleaseContext(IntPtr himc) => ReleaseCount++;

        public IntPtr EnsureContext()
        {
            EnsureCalls++;
            return Context;
        }

        public ImmCompositionSnapshot ReadComposition(IntPtr himc, long flags)
            => new(flags, Composition, Result, Cursor, Clauses);

        public bool SetCandidatePosition(IntPtr himc, int x, int y)
        {
            CandidatePositions.Add((x, y));
            return true;
        }

        public bool SetCandidateExclude(IntPtr himc, int left, int top, int right, int bottom)
        {
            CandidateExcludes.Add((left, top, right, bottom));
            return true;
        }

        public bool SetCompositionWindow(IntPtr himc, int x, int y, int left, int top, int right, int bottom)
        {
            CompositionWindows.Add((x, y, left, top, right, bottom));
            return true;
        }

        public bool GetCandidateWindow(IntPtr himc, out int x, out int y, out uint style)
        {
            x = y = 0;
            style = 0;
            return false;
        }

        public bool NotifyCancelComposition(IntPtr himc)
        {
            CancelCalls++;
            return CancelResult;
        }

        public bool Open = true;
        public uint Conversion = Win32Ime.IME_CMODE_NATIVE;
        public uint Sentence;
        public int SetStateCalls;

        public bool GetNativeInputState(IntPtr himc, out bool open, out uint conversion, out uint sentence)
        {
            open = Open;
            conversion = Conversion;
            sentence = Sentence;
            return himc != IntPtr.Zero;
        }

        public bool SetNativeInputState(IntPtr himc, bool open, uint conversion, uint sentence)
        {
            if (himc == IntPtr.Zero)
            {
                return false;
            }

            SetStateCalls++;
            Open = open;
            Conversion = conversion;
            Sentence = sentence;
            return true;
        }
    }

    private sealed class Host : IDisposable
    {
        public string Value = string.Empty;
        public int Writes;
        public FakeIme Ime = new();
        public WindowsImeBridge Bridge;
        public UiRuntime Runtime;

        /// <summary>桥的窗口句柄；默认 <see cref="IntPtr.Zero"/>（现有 Host 用例的受控假句柄）。</summary>
        public IntPtr Hwnd;

        public Host(IntPtr hwnd = default)
        {
            Hwnd = hwnd;
            Bridge = new WindowsImeBridge(
                hwnd, Ime, new OpenTkUiDispatcher(() => { }), () => (800, 600));
            Bridge.SimulateInstalled();
            Runtime = new UiRuntime(
                new TextField(new Binding<string>(() => Value, v => { Value = v; Writes++; })),
                new UiRuntimeServices
                {
                    Dispatcher = new HeadlessUiDispatcher(),
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    TextureService = new RecordingTextureService(),
                    TextInputService = Bridge,
                });
            Bridge.AttachRuntime(Runtime);
            Runtime.SetViewport(new Size(400, 100), 1.0);
            Runtime.Start();
            Runtime.BuildFrame();
            Runtime.SetFocus(Runtime.Root);
            Runtime.BuildFrame();
        }

        public ITextInputClient Client => Bridge.ActiveClient!;

        /// <summary>把一条窗口消息喂给桥（与真实 WndProc 相同的处理路径，但没有链式默认过程）。</summary>
        public IntPtr? Send(uint msg, long wParam = 0, long lParam = 0)
            => Bridge.HandleMessage(Hwnd, msg, new IntPtr(wParam), new IntPtr(lParam));

        public void Start() => Send(Win32Ime.WM_IME_STARTCOMPOSITION);

        public void Composition(string? comp = null, string? result = null, int cursor = -1, int[]? clauses = null)
        {
            Ime.Composition = comp;
            Ime.Result = result;
            Ime.Cursor = cursor;
            Ime.Clauses = clauses is null ? [] : [.. clauses];
            var flags = 0L;
            if (comp is not null || cursor >= 0 || clauses is not null)
            {
                flags |= Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS;
                if (clauses is not null)
                {
                    flags |= Win32Ime.GCS_COMPCLAUSE;
                }
            }

            if (result is not null)
            {
                flags |= Win32Ime.GCS_RESULTSTR;
            }

            Send(Win32Ime.WM_IME_COMPOSITION, 0, flags);
        }

        public void End() => Send(Win32Ime.WM_IME_ENDCOMPOSITION);

        public void Dispose()
        {
            Runtime.Dispose();

            // 受控假句柄只验证租约身份：用例结束时归还本桥残留的租约（不触碰其它窗口的真实持有者）。
            if (Hwnd != IntPtr.Zero && HostWriterRegistry.CurrentLease(Hwnd) is { } leftover)
            {
                HostWriterRegistry.Release(leftover);
            }
        }
    }

    // ------------------------------------------------------------ 会话与组合流

    [Fact]
    public void 聚焦建立会话并在启动组合后进入预编辑()
    {
        using var host = new Host();
        Assert.NotNull(host.Bridge.ActiveClient);
        Assert.True(host.Bridge.Capabilities.Composition);

        host.Start();
        host.Composition(comp: "nihao", cursor: 5);

        Assert.True(host.Client.IsComposing);
        Assert.Equal("nihao", host.Client.Preedit);
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Writes);

        host.End();
        Assert.False(host.Client.IsComposing);
    }

    [Fact]
    public void 结果只提交一次且结果消息不进默认链()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        // 携带结果的 COMPOSITION 消息被本桥完全接管（返回 Zero=已处理，不链式默认处理），
        // 因此 DefWindowProc 不会按结果再生成 WM_IME_CHAR 回声（R1 源头拦截）。
        Assert.Equal(IntPtr.Zero, host.Send(Win32Ime.WM_IME_COMPOSITION, 0,
            Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS | Win32Ime.GCS_RESULTSTR));
    }

    [Fact]
    public void 结果提交恰好一次且普通字符消息永不被吞()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Composition(comp: string.Empty, result: "你好");

        Assert.Equal("你好", host.Value);
        Assert.Equal(1, host.Writes);

        // WM_IME_CHAR 按消息类别就是 IME 结果回声通道：桥接管后按类吞掉，不看字符值。
        Assert.Equal(IntPtr.Zero, host.Send(Win32Ime.WM_IME_CHAR, '你'));
        Assert.Equal(IntPtr.Zero, host.Send(Win32Ime.WM_IME_CHAR, '好'));
        Assert.Equal("你好", host.Value);
        Assert.Equal(1, host.Writes);

        // 普通字符通道不属于本桥：与结果相同的 WM_CHAR 照常放行，绝不按文本相等吞字符。
        Assert.Null(host.Send(Win32Ime.WM_CHAR, '你'));
        host.End();
    }

    [Fact]
    public void WM_UNICHAR探测与标量都不属于本桥全部放行()
    {
        using var host = new Host();
        host.Start();
        host.Composition(result: "😀");

        // UNICODE_NOCHAR 探测与真实 UTF-32 标量都不是本桥的消息类别：一律链式放行。
        Assert.Null(host.Send(Win32Ime.WM_UNICHAR, Win32Ime.UNICODE_NOCHAR));
        Assert.Null(host.Send(Win32Ime.WM_UNICHAR, char.ConvertToUtf32("😀", 0)));
        host.End();
        Assert.Equal("😀", host.Value);
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public void 结果与同消息下一段预编辑按先结果后预编辑处理()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        // 一次消息同时携带结果与下一段预编辑（真实 IME 存在此序列）。
        host.Composition(comp: "shijie", result: "你好", cursor: 6);

        Assert.Equal("你好", host.Value);
        Assert.Equal(1, host.Writes);
        Assert.True(host.Client.IsComposing);
        Assert.Equal("shijie", host.Client.Preedit);

        host.Composition(result: "世界");
        host.End();
        Assert.Equal("你好世界", host.Value);
        Assert.Equal(2, host.Writes);
        Assert.False(host.Client.IsComposing);
    }

    [Fact]
    public void 分段结果跨消息各提交一次()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Composition(result: "你好");
        // 同一组合内继续出现新预编辑（无新的 Start 消息）：隐式新段。
        host.Composition(comp: "shijie", cursor: 6);
        Assert.Equal("shijie", host.Client.Preedit);
        host.Composition(result: "世界");
        host.End();

        Assert.Equal("你好世界", host.Value);
        Assert.Equal(2, host.Writes);
    }

    [Fact]
    public void 无结果标志但上下文留有旧结果串时不重复提交()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Composition(result: "你好");
        host.End();
        Assert.Equal(1, host.Writes);

        // 下一条消息只有光标标志、但上下文仍查询得到旧结果：快照而已，不能再次提交。
        host.Ime.Result = "你好";
        host.Ime.Composition = null;
        host.Ime.Cursor = 0;
        host.Send(Win32Ime.WM_IME_COMPOSITION, 0, Win32Ime.GCS_CURSORPOS);
        Assert.Equal("你好", host.Value);
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public void 未配对代理项的结果被拒绝且不写绑定()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "x", cursor: 1);
        host.Composition(result: "a\uD800");
        host.End();

        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public void 取消恢复组合前状态且不写绑定()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Send(Win32Ime.WM_IME_ENDCOMPOSITION); // IME 取消路径：无结果直接结束

        Assert.False(host.Client.IsComposing);
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Writes);

        // 重复的 ENDCOMPOSITION 幂等，不再产生动作。
        host.End();
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public void 失焦后服务会话切断且后续消息不写入()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);

        host.Runtime.SetFocus(null);
        Assert.Null(host.Bridge.ActiveClient);

        host.Start();
        host.Composition(result: "你好");
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Writes);
    }

    // ------------------------------------------------------------ 按键归属与宿主退出

    [Fact]
    public void IME消费的Escape不触发宿主退出()
    {
        var exits = 0;
        using var host = new Host();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        // 输入法消费的按键：wParam=VK_PROCESSKEY，GLFW 侧键值为 Unknown。
        host.Bridge.BeginKeyDispatch(new IntPtr(Win32Ime.VK_PROCESSKEY));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Unknown, 0, GlfwNoModifiers.Value, false));
        host.Bridge.EndKeyDispatch();

        Assert.Equal(0, exits);
    }

    [Fact]
    public void 组合期间Escape被控件消费不退出且取消后下一次Esc退出()
    {
        var exits = 0;
        using var host = new Host();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        host.Start();
        host.Composition(comp: "ni", cursor: 2);

        // 组合期间 Esc（按未被 IME 直接消费的路径到达）：控件消费，不退出。
        host.Bridge.BeginKeyDispatch(new IntPtr(0x1B));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Escape, 0, GlfwNoModifiers.Value, false));
        host.Bridge.EndKeyDispatch();
        Assert.Equal(0, exits);

        // 组合结束后普通 Esc 仍按既有行为退出（此时文本控件不消费 Escape）。
        host.End();
        host.Bridge.BeginKeyDispatch(new IntPtr(0x1B));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Escape, 0, GlfwNoModifiers.Value, false));
        host.Bridge.EndKeyDispatch();
        Assert.Equal(1, exits);
    }

    [Fact]
    public void 无IME桥时Esc保持原有退出行为()
    {
        var exits = 0;
        using var host = new Host();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true);

        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Escape, 0, GlfwNoModifiers.Value, false));
        Assert.Equal(1, exits);
    }

    [Fact]
    public void 组合结束同一原生按键的KeyDown被token吞掉()
    {
        var exits = 0;
        using var host = new Host();
        var adapter = new OpenTkInputAdapter(host.Runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true, host.Bridge);

        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        // IME 在这次按键内结束组合并提交结果；同一按键随后仍进入 KeyDown。
        host.Composition(result: "你好");

        host.Bridge.BeginKeyDispatch(new IntPtr(Win32Ime.VK_PROCESSKEY));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Unknown, 0, GlfwNoModifiers.Value, false));
        host.Bridge.EndKeyDispatch();

        Assert.Equal("你好", host.Value);
        Assert.Equal(0, exits);
        Assert.False(host.Client.IsComposing);
    }

    // ------------------------------------------------------------ 候选锚点

    [Fact]
    public void 候选锚点按逻辑到客户区换算并在一百五十缩放正确()
    {
        using var host = new Host();
        // Windows 150%：PointerScale = R/D = 1/1.5，逻辑坐标 ÷ (2/3) = ×1.5。
        host.Bridge.UpdatePointerTransform(2.0 / 3.0, 2.0 / 3.0);

        var geometry = new TextInputGeometry(
            new Rect(30, 10, 2, 20),
            new Rect(30, 10, 60, 20),
            new Rect(28, 8, 64, 24));
        Assert.True(host.Bridge.PublishGeometry(geometry));

        // 会话建立时已自动发布过一次真实 caret 几何：断言最后一次发布。
        var pos = host.Ime.CandidatePositions[^1];
        Assert.Equal((45, 45), pos); // caret.Left*1.5=45, caret.Bottom*1.5=45

        var exclude = host.Ime.CandidateExcludes[^1];
        Assert.Equal((42, 12, 138, 48), exclude); // 排除区 ×1.5

        var comp = host.Ime.CompositionWindows[^1];
        Assert.Equal(45, comp.X);
        Assert.Equal(135, comp.R); // composition.Right ×1.5 = 90×1.5
    }

    [Fact]
    public void 几何不可用时撤销锚点为整客户区排除而不是窗口原点()
    {
        using var host = new Host();
        host.Bridge.UpdatePointerTransform(1, 1);
        Assert.True(host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(10, 10, 2, 20), Rect.Empty, Rect.Empty)));

        host.Bridge.PublishGeometry(TextInputGeometry.Unavailable);

        Assert.Null(host.Bridge.LastAnchorClient);
        var last = host.Ime.CandidateExcludes[^1];
        Assert.Equal((0, 0, 800, 600), last);
        Assert.True(host.Bridge.GeometryRevokeCount > 0);
        // 撤销路径不得把候选定位到 (0,0)：最后一次位置调用仍来自之前的有效发布。
        Assert.Equal((10, 30), host.Ime.CandidatePositions[^1]);
    }

    [Fact]
    public void 宿主取消请求转发ImmNotifyIME()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);

        Assert.True(host.Bridge.RequestCancel());
        Assert.Equal(1, host.Ime.CancelCalls);

        host.End();
        Assert.False(host.Bridge.RequestCancel());
    }

    [Fact]
    public void SetContext屏蔽系统组合窗但保留全部候选窗位()
    {
        using var host = new Host();
        // 真实掩码：0x8000000F = 组合窗高位 + 全部4个候选窗位 + 指南位低段（R8）。
        Assert.Null(host.Send(Win32Ime.WM_IME_SETCONTEXT, 1, 0x8000000F));
        Assert.Equal((long?)0x0000000F, host.Bridge.MaskedSetContextLParam);
        // 只清 0x80000000 高位：候选位 0xF 与指南位 0x40000000 之外的位不被动。
        Assert.Null(host.Send(Win32Ime.WM_IME_SETCONTEXT, 1, unchecked((long)0xC000000F)));
        Assert.Equal((long?)0x4000000F, host.Bridge.MaskedSetContextLParam);
    }

    [Fact]
    public void 候选窗请求按已发布锚点应答()
    {
        using var host = new Host();
        host.Bridge.UpdatePointerTransform(1, 1);
        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(10, 10, 2, 20), Rect.Empty, Rect.Empty));

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32Ime.CANDIDATEFORM>());
        try
        {
            var result = host.Bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_REQUEST, new IntPtr(Win32Ime.IMR_CANDIDATEWINDOW), ptr);
            Assert.Equal(new IntPtr(1), result);
            var form = Marshal.PtrToStructure<Win32Ime.CANDIDATEFORM>(ptr);
            Assert.Equal(Win32Ime.CFS_CANDIDATEPOS, form.dwStyle);
            Assert.Equal(10, form.ptCurrentPos.X);
            Assert.Equal(30, form.ptCurrentPos.Y);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void 非IME消息与异常路径不吞且计入回调异常()
    {
        using var host = new Host();
        Assert.Null(host.Send(0x0400 /*WM_USER*/));
        Assert.Equal(0, host.Bridge.CallbackExceptionCount);
        Assert.Equal(0, host.Bridge.RestoreFailureCount);
    }

    // ------------------------------------------------------------ R1/R2/R3/R5 复审项

    [Fact]
    public void 绑定拒绝的结果回声仍被吞掉不再经字符通道写入()
    {
        // setter 拒绝（异常）：结果未落地，但 IME 仍会按该结果逐字符回声——必须照样吞掉（R1）。
        var value = string.Empty;
        var writes = 0;
        var ime = new FakeIme();
        var bridge = new WindowsImeBridge(IntPtr.Zero, ime, new OpenTkUiDispatcher(() => { }), () => (800, 600));
        bridge.SimulateInstalled();
        using var runtime = new UiRuntime(
            new TextField(new Binding<string>(() => value, _ => { writes++; throw new InvalidOperationException("setter rejected"); })),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                TextureService = new RecordingTextureService(),
                TextInputService = bridge,
            });
        bridge.AttachRuntime(runtime);
        runtime.SetViewport(new Size(400, 100), 1.0);
        runtime.Start();
        runtime.BuildFrame();
        runtime.SetFocus(runtime.Root);
        runtime.BuildFrame();

        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_STARTCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
        ime.Composition = "ni";
        ime.Cursor = 2;
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero,
            new IntPtr(Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS));
        ime.Composition = null;
        ime.Cursor = -1;
        ime.Result = "你好";
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero, new IntPtr(Win32Ime.GCS_RESULTSTR));

        // 结果被 setter 拒绝：文本未变，但回声字符消息仍按序吞掉，不会经默认字符通道再次写入。
        Assert.Equal(IntPtr.Zero, bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_CHAR, new IntPtr('你'), IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_CHAR, new IntPtr('好'), IntPtr.Zero));
        Assert.Equal(string.Empty, value);
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_ENDCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
    }

    [Fact]
    public void 无GCS标志的组合消息取消活动段且不依赖后续End()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        Assert.True(host.Client.IsComposing);

        // MSDN：lParam 不含任何 GCS_* 标志的 WM_IME_COMPOSITION 表示组合被取消（R3）。
        host.Ime.Composition = null;
        host.Ime.Result = null;
        host.Ime.Cursor = -1;
        host.Ime.Clauses = [];
        host.Send(Win32Ime.WM_IME_COMPOSITION, 0, 0);

        Assert.False(host.Client.IsComposing);
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public void 本地取消后RequestCancel仍通知平台且EndSession取消原生组合()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);

        // 本地已取消（核心顺序：先本地取消再到安全阶段 RequestCancel）：客户端已不组合，
        // 但桥仍须把取消通知发给平台（R2）。组合期间按键归输入法所有，
        // 本地取消走 CancelComposition 接缝而不是普通按键。
        Assert.True(host.Client.IsComposing);
        Assert.True(host.Client.CancelComposition());
        Assert.False(host.Client.IsComposing);

        Assert.True(host.Bridge.RequestCancel());
        Assert.True(host.Ime.CancelCalls >= 1);
        host.End();

        // EndSession 时仍有原生组合 → 同样通知平台取消。
        host.Start();
        host.Composition(comp: "n", cursor: 1);
        host.Runtime.SetFocus(null);
        Assert.True(host.Ime.CancelCalls >= 2);
    }

    [Fact]
    public void 提交回调换焦点后旧批次不覆盖新会话桥状态()
    {
        // setter 在提交时同步把焦点切回第一个字段（R5）：旧批次的版本/锚点不能污染新会话。
        string a = "a", b = "b";
        var switchOnWrite = false;
        var ime = new FakeIme();
        var bridge = new WindowsImeBridge(IntPtr.Zero, ime, new OpenTkUiDispatcher(() => { }), () => (800, 600));
        bridge.SimulateInstalled();
        UiRuntime? runtime = null;
        runtime = new UiRuntime(
            new VStack(
                new TextField(new Binding<string>(() => a, v => a = v)),
                new TextField(new Binding<string>(() => b, v =>
                {
                    b = v;
                    if (switchOnWrite)
                    {
                        runtime!.SetFocus(runtime.Root.Children[0]);
                    }
                }))),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                TextureService = new RecordingTextureService(),
                TextInputService = bridge,
            });
        bridge.AttachRuntime(runtime);
        runtime.SetViewport(new Size(400, 200), 1.0);
        runtime.Start();
        runtime.BuildFrame();
        var second = runtime.Root.Children[1];
        runtime.SetFocus(second);
        runtime.BuildFrame();
        Assert.NotNull(bridge.ActiveClient);

        switchOnWrite = true;
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_STARTCOMPOSITION, IntPtr.Zero, IntPtr.Zero);
        ime.Composition = "ni";
        ime.Cursor = 2;
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero,
            new IntPtr(Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS));
        ime.Composition = null;
        ime.Cursor = -1;
        ime.Result = "好";
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero, new IntPtr(Win32Ime.GCS_RESULTSTR));

        // 结果在 caret 处插入（替换预编辑区，不清空既有文本）：旧字段收到自己的提交。
        Assert.Equal("好b", b);
        ime.Composition = "x";
        ime.Cursor = 1;
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero,
            new IntPtr(Win32Ime.GCS_COMPSTR | Win32Ime.GCS_CURSORPOS));
        ime.Composition = null;
        ime.Cursor = -1;
        ime.Result = "坏";
        bridge.HandleMessage(IntPtr.Zero, Win32Ime.WM_IME_COMPOSITION, IntPtr.Zero, new IntPtr(Win32Ime.GCS_RESULTSTR));
        Assert.Equal("a", a); // 新客户端会话身份不同：旧组合消息不写入
        runtime.Dispose();
    }

    // ------------------------------------------------------------ R6~R10 复审项

    [Fact]
    public void 仅结果元数据标志不是取消且保留预编辑()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "abc", cursor: 1);
        Assert.True(host.Client.IsComposing);

        // 只带 GCS_RESULTCLAUSE（0x1000）的消息仍是数据更新，不是平台取消（R6）。
        host.Ime.Composition = "abc";
        Assert.Equal(IntPtr.Zero, host.Send(Win32Ime.WM_IME_COMPOSITION, 0, Win32Ime.GCS_RESULTCLAUSE));
        Assert.True(host.Client.IsComposing);
        Assert.Equal("abc", host.Client.Preedit);

        // 同理 RESULTREADSTR/RESULTREADCLAUSE 单独出现也不是取消。
        Assert.Equal(IntPtr.Zero, host.Send(Win32Ime.WM_IME_COMPOSITION, 0,
            Win32Ime.GCS_RESULTREADSTR | Win32Ime.GCS_RESULTREADCLAUSE));
        Assert.True(host.Client.IsComposing);
        host.End();
    }

    [Fact]
    public void 结果加下一段保留原生光标与分段()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "abc", cursor: 2);
        host.Composition(comp: "xyz", result: "A", cursor: 1, clauses: [0, 1, 3]);

        Assert.True(host.Client.IsComposing);
        Assert.Equal("xyz", host.Client.Preedit);
        var client = Assert.IsType<TextCompositionClient>(host.Client);
        Assert.Equal(1, client.PreeditCaret);
        Assert.Equal([new TextRange(0, 1), new TextRange(1, 3)], client.Segments);
        Assert.Equal("A", host.Value);
        host.End();
    }

    [Fact]
    public void 换算系数变化后按缓存逻辑几何重发锚点且同值不重复调用()
    {
        using var host = new Host();
        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(30, 10, 2, 20), Rect.Empty, Rect.Empty));
        var count = host.Ime.CandidatePositions.Count;
        var publishes = host.Bridge.GeometryPublishCount;

        // 指针换算从 1.0 变为 150%（scale=2/3）：已缓存的逻辑锚点必须按新系数重发（R7）。
        host.Bridge.UpdatePointerTransform(2.0 / 3.0, 2.0 / 3.0);
        Assert.True(host.Ime.CandidatePositions.Count > count);
        Assert.Equal((45, 45), host.Ime.CandidatePositions[^1]);
        Assert.True(host.Bridge.GeometryPublishCount > publishes);

        // 相同换算系数下重复发布同一份逻辑几何：客户区值逐位相等 → 不再调用平台。
        var beforeSame = host.Ime.CandidatePositions.Count + host.Ime.CandidateExcludes.Count
            + host.Ime.CompositionWindows.Count;
        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(30, 10, 2, 20), Rect.Empty, Rect.Empty));
        var afterSame = host.Ime.CandidatePositions.Count + host.Ime.CandidateExcludes.Count
            + host.Ime.CompositionWindows.Count;
        Assert.Equal(beforeSame, afterSame);
    }

    [Fact]
    public void 锚点撤销幂等且恢复后正常重发()
    {
        using var host = new Host();
        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(10, 10, 2, 20), Rect.Empty, Rect.Empty));
        host.Bridge.PublishGeometry(TextInputGeometry.Unavailable);
        var excludes = host.Ime.CandidateExcludes.Count;

        // 已撤销状态下再次撤销不再调用平台（不连续同值唤醒）。
        host.Bridge.PublishGeometry(TextInputGeometry.Unavailable);
        Assert.Equal(excludes, host.Ime.CandidateExcludes.Count);
        Assert.Null(host.Bridge.LastAnchorClient);

        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(20, 20, 2, 20), Rect.Empty, Rect.Empty));
        Assert.Equal((20, 40), host.Ime.CandidatePositions[^1]);
    }

    [Fact]
    public void 几何失效通知清平台缓存并强制重发同值锚点()
    {
        using var host = new Host();
        host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(30, 10, 2, 20), Rect.Empty, Rect.Empty));
        var callsBefore = host.Ime.CandidatePositions.Count + host.Ime.CandidateExcludes.Count
            + host.Ime.CompositionWindows.Count;
        Assert.True(callsBefore > 0);

        // 窗口移动/尺寸变化使平台侧状态失效：即使客户区值不变，也必须真正重发到 IMM（R7/R13）。
        host.Bridge.NotifyGeometryInvalidated();
        var callsAfter = host.Ime.CandidatePositions.Count + host.Ime.CandidateExcludes.Count
            + host.Ime.CompositionWindows.Count;
        Assert.True(callsAfter > callsBefore);
        Assert.Equal((30, 30), host.Ime.CandidatePositions[^1]);
    }

    [Fact]
    public void 自有上下文输入模式可读可写且只作用于本窗口()
    {
        using var host = new Host();
        Assert.True(host.Bridge.TryGetNativeInputState(out var open, out var conv, out var sent));
        Assert.True(open);
        Assert.Equal(Win32Ime.IME_CMODE_NATIVE, conv);

        // 切英文（清 NATIVE 位）→ 恢复中文（置位）→ 恢复原值：往返完整。
        Assert.True(host.Bridge.TrySetNativeInputState(true, conv & ~Win32Ime.IME_CMODE_NATIVE, sent));
        Assert.Equal(0u, host.Ime.Conversion & Win32Ime.IME_CMODE_NATIVE);
        Assert.True(host.Bridge.TrySetNativeInputState(open, conv, sent));
        Assert.Equal(Win32Ime.IME_CMODE_NATIVE, host.Ime.Conversion & Win32Ime.IME_CMODE_NATIVE);
        Assert.True(host.Ime.SetStateCalls >= 2);

        // 无上下文时两项都安全返回 false，不创建上下文。
        host.Ime.Context = IntPtr.Zero;
        Assert.False(host.Bridge.TryGetNativeInputState(out _, out _, out _));
        Assert.False(host.Bridge.TrySetNativeInputState(true, 0, 0));
    }

    [Fact]
    public void 无START预编辑建立组合且失焦时通知平台取消()
    {
        // 真实 IME 存在跳过 WM_IME_STARTCOMPOSITION 直接投预编辑的序列（R13）：
        // 首个带 GCS_* 数据的消息把归属绑到当前客户端并标记原生组合进行中，
        // 因此失焦（EndSession）时同样能向平台发 ImmNotifyIME(CPS_CANCEL)。
        using var host = new Host();
        var cancelBefore = host.Ime.CancelCalls;

        host.Composition(comp: "old", cursor: 3); // 注意：不调用 Start()，无 WM_IME_STARTCOMPOSITION。
        Assert.True(host.Client.IsComposing);
        Assert.Equal("old", host.Client.Preedit);

        host.Runtime.SetFocus(null);
        Assert.True(host.Ime.CancelCalls > cancelBefore);
        Assert.Null(host.Bridge.ActiveClient);
    }

    [Fact]
    public void 无START组合的延迟结果不跨焦点epoch泄漏()
    {
        // 隐式组合的归属已在首个数据消息时绑定；换焦点后旧段的延迟结果仍发给原归属
        // （已释放 → 会话归属校验丢弃），绝不写入新客户端（R13/R11 隐式路径）。
        using var host = new Host();
        host.Composition(comp: "old", cursor: 3);
        Assert.True(host.Client.IsComposing);

        host.Runtime.SetFocus(null);
        host.Runtime.SetFocus(host.Runtime.Root);
        host.Runtime.BuildFrame();

        var before = host.Writes;
        host.Composition(result: "OLD");
        Assert.Equal(before, host.Writes);
        Assert.Equal(string.Empty, host.Value);
    }

    [Fact]
    public void 两个标量文本输入恰好两次绑定写入()
    {
        // 文本事件按标量投递：“中”（BMP 单码元）一次、“😀”（完整代理对）一次——
        // 各产生一次绑定写入，不按 UTF-16 码元数计（R14）。
        using var host = new Host();
        var before = host.Writes;

        host.Runtime.DispatchTextInput(new TextInputEvent("中"));
        host.Runtime.DispatchTextInput(new TextInputEvent("😀"));

        Assert.Equal(before + 2, host.Writes);
        Assert.Equal("中😀", host.Value);
    }

    [Fact]
    public void 宽接口UTF16解码严格拒绝畸形与越界()
    {
        // 奇数长度/负长度/越界/未配对代理项全部拒绝（不产出 U+FFFD）。
        var zhong = BitConverter.GetBytes('中');            // UTF-16LE：0x2D 0x4E
        var emoji = MemoryMarshal.AsBytes("😀".AsSpan()).ToArray(); // 代理对 4 字节
        Assert.Null(Imm32Interop.DecodeUtf16(zhong, -2));
        Assert.Null(Imm32Interop.DecodeUtf16(zhong, 100));           // 声明长度越界
        Assert.Null(Imm32Interop.DecodeUtf16([0x61], 1));            // 奇数字节
        Assert.Null(Imm32Interop.DecodeUtf16([0x3D, 0xD8], 2));      // 孤立高代理
        Assert.Null(Imm32Interop.DecodeUtf16([0x00, 0xDE], 2));      // 孤立低代理
        Assert.Null(Imm32Interop.DecodeUtf16([0x3D, 0xD8, 0x61, 0x00], 4)); // 高代理后接非低代理
        // 合法序列照常解码。
        Assert.Equal("中", Imm32Interop.DecodeUtf16(zhong, 2));
        Assert.Equal("😀", Imm32Interop.DecodeUtf16(emoji, 4));
        Assert.Equal(string.Empty, Imm32Interop.DecodeUtf16([], 0));
    }

    // ------------------------------------------------------------ R17 逐字符几何查询

    private static readonly int ImeCharPositionSize = Marshal.SizeOf<Win32Ime.IMECHARPOSITION>();

    /// <summary>发送 IMR_QUERYCHARPOSITION：返回是否被有效应答以及读回的结构内容。</summary>
    private static (bool Ok, Win32Ime.IMECHARPOSITION Value) QueryCharPosition(
        Host host, uint offset, int? declaredSize = null)
    {
        var memory = Marshal.AllocHGlobal(ImeCharPositionSize);
        try
        {
            Marshal.StructureToPtr(new Win32Ime.IMECHARPOSITION
            {
                dwSize = (uint)(declaredSize ?? ImeCharPositionSize),
                dwCharPos = offset,
            }, memory, fDeleteOld: false);
            var answer = host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, memory.ToInt64());
            return (answer is { } result && result != IntPtr.Zero,
                Marshal.PtrToStructure<Win32Ime.IMECHARPOSITION>(memory));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void 字符位置结构为官方三十六字节布局()
    {
        // imm.h IMECHARPOSITION：dwSize/dwCharPos/POINT/cLineHeight/RECT = 4+4+8+4+16。
        Assert.Equal(36, ImeCharPositionSize);
        Assert.Equal(0, Marshal.OffsetOf<Win32Ime.IMECHARPOSITION>(nameof(Win32Ime.IMECHARPOSITION.dwSize)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<Win32Ime.IMECHARPOSITION>(nameof(Win32Ime.IMECHARPOSITION.dwCharPos)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Win32Ime.IMECHARPOSITION>(nameof(Win32Ime.IMECHARPOSITION.pt)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<Win32Ime.IMECHARPOSITION>(nameof(Win32Ime.IMECHARPOSITION.cLineHeight)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<Win32Ime.IMECHARPOSITION>(nameof(Win32Ime.IMECHARPOSITION.rcDocument)).ToInt32());
    }

    [Fact]
    public void 组合串偏移零中末解析为递增的真实排版坐标()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x - 400, y - 300); // 负屏幕原点：多显示器坐标原样保留
        host.Start();
        host.Composition(comp: "Wiii", cursor: 4);
        host.Runtime.BuildFrame();

        var first = QueryCharPosition(host, 0);
        var middle = QueryCharPosition(host, 2);
        var end = QueryCharPosition(host, 4);

        Assert.True(first.Ok && middle.Ok && end.Ok);
        Assert.True(first.Value.pt.X < middle.Value.pt.X && middle.Value.pt.X < end.Value.pt.X);
        Assert.True(first.Value.pt.X < 0); // 负屏幕坐标不夹取
        Assert.True(first.Value.cLineHeight > 0);
        Assert.True(first.Value.rcDocument.Right > first.Value.rcDocument.Left);
        Assert.True(first.Value.rcDocument.Bottom > first.Value.rcDocument.Top);
        // rcDocument 是可编辑可见区域而不是整个客户区（客户区宽 800，字段内容区明显更窄）。
        Assert.True(first.Value.rcDocument.Right - first.Value.rcDocument.Left < 800);
        host.End();
    }

    [Fact]
    public void 字符位置查询拒绝越界偏移与过小结构()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "Wiii", cursor: 4);
        host.Runtime.BuildFrame();

        // 长 4 的组合串合法偏移是 0..4（末端 = 插入点）：越界一律如实拒绝，不回落当前 caret。
        Assert.False(QueryCharPosition(host, 5).Ok);
        Assert.False(QueryCharPosition(host, 100).Ok);
        // 调用方声明的缓冲区小于 36 字节结构时不写入。
        Assert.False(QueryCharPosition(host, 0, declaredSize: 4).Ok);
        Assert.False(QueryCharPosition(host, 0, declaredSize: 8).Ok);
        host.End();
    }

    [Fact]
    public void 字符位置查询在锚点撤销与失焦后拒绝并在恢复后重答()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);

        // 显式撤销几何：旧的字符几何同样不得应答。
        host.Bridge.PublishGeometry(TextInputGeometry.Unavailable);
        Assert.False(QueryCharPosition(host, 0).Ok);
        host.End();

        // 失焦再聚焦建立新会话：旧快照不跨 epoch 泄漏，新组合重新应答。
        host.Runtime.SetFocus(null);
        Assert.False(QueryCharPosition(host, 0).Ok);
        host.Runtime.SetFocus(host.Runtime.Root);
        host.Runtime.BuildFrame();
        host.Start();
        host.Composition(comp: "ha", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
        Assert.True(QueryCharPosition(host, 2).Ok);
        host.End();
    }

    [Fact]
    public void 无预编辑时偏移零应答文档caret其余拒绝()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);

        // 焦点会话已建立但未组合：按官方语义只有偏移 0（当前文档 caret）有意义。
        var caret = QueryCharPosition(host, 0);
        Assert.True(caret.Ok);
        Assert.True(caret.Value.cLineHeight > 0);
        Assert.False(QueryCharPosition(host, 1).Ok);
        Assert.False(QueryCharPosition(host, 2).Ok);
    }

    [Fact]
    public void 预编辑更新后旧快照立即失效不回落旧位置()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);

        // 组合串已更新但尚未最终布局：按过期排版应答任何偏移都是错误几何——全部拒绝（R17）。
        host.Composition(comp: "niha", cursor: 4);
        Assert.False(QueryCharPosition(host, 0).Ok);
        Assert.False(QueryCharPosition(host, 4).Ok);

        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
        Assert.True(QueryCharPosition(host, 4).Ok);
        Assert.False(QueryCharPosition(host, 5).Ok);
        host.End();
    }

    [Fact]
    public void 代理对内偏移吸附字素边界且越界拒绝()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "😀x", cursor: 3); // 😀 为代理对（2 码元），字素边界在 0/2/3
        host.Runtime.BuildFrame();

        var lead = QueryCharPosition(host, 0);
        var insideSurrogate = QueryCharPosition(host, 1); // 代理对内：吸附到该字素的前导 caret
        var after = QueryCharPosition(host, 2);
        var end = QueryCharPosition(host, 3);

        Assert.True(lead.Ok && insideSurrogate.Ok && after.Ok && end.Ok);
        Assert.Equal(lead.Value.pt.X, insideSurrogate.Value.pt.X);
        Assert.True(after.Value.pt.X > lead.Value.pt.X);
        Assert.True(end.Value.pt.X > after.Value.pt.X);
        Assert.False(QueryCharPosition(host, 4).Ok);
        host.End();
    }

    [Fact]
    public void 前缀替换区把预编辑偏移映射到显示串索引()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Value = "中";
        var state = (TextFieldState)host.Runtime.Root.InteractionState!;
        state.Caret = 1;
        state.Anchor = 1;
        host.Runtime.BuildFrame();

        // 无预编辑时偏移 0 = 文档 caret（索引 1 处的插入点）。
        var caretQuery = QueryCharPosition(host, 0);
        Assert.True(caretQuery.Ok);

        // 在 caret=1 处开始组合：显示串为“中ni”，预编辑起点映射到显示索引 1，而非 0。
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();

        var start = QueryCharPosition(host, 0);
        var end = QueryCharPosition(host, 2);
        Assert.True(start.Ok && end.Ok);
        Assert.Equal(caretQuery.Value.pt.X, start.Value.pt.X);
        Assert.True(end.Value.pt.X > start.Value.pt.X);
        host.End();
    }

    [Fact]
    public void 滚动后同一偏移解析到平移后的真实显示位置()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Value = new string('中', 60);
        var state = (TextFieldState)host.Runtime.Root.InteractionState!;
        state.Caret = 60;
        state.Anchor = 60;
        host.Runtime.BuildFrame();

        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        var scrolled = QueryCharPosition(host, 0);
        Assert.True(scrolled.Ok);
        Assert.True(state.ScrollX > 0); // 末尾 caret 已由可见性收敛滚入视野

        // 同一偏移 0 回到文档开头组合：解析到完全不同的显示位置（随真实排版与滚动平移）。
        host.End();
        state.Caret = 0;
        state.Anchor = 0;
        host.Runtime.BuildFrame();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        var unScrolled = QueryCharPosition(host, 0);
        Assert.True(unScrolled.Ok);
        Assert.True(scrolled.Value.pt.X > unScrolled.Value.pt.X);
        host.End();
    }

    [Fact]
    public void 一百五十缩放下坐标按客户区系数换算且负屏幕坐标保留()
    {
        using var host = new Host();
        host.Bridge.UpdatePointerTransform(2.0 / 3.0, 2.0 / 3.0);
        host.Bridge.ClientToScreen = (x, y) => (x - 500, y - 400);
        host.Start();
        host.Composition(comp: "Wiii", cursor: 4);
        host.Runtime.BuildFrame();

        var first = QueryCharPosition(host, 0);
        var end = QueryCharPosition(host, 4);
        Assert.True(first.Ok && end.Ok);
        Assert.True(first.Value.pt.X < end.Value.pt.X);
        Assert.True(first.Value.pt.X < 0);
        Assert.True(first.Value.cLineHeight > 0);
        Assert.True(first.Value.rcDocument.Right > first.Value.rcDocument.Left);
        host.End();
    }

    [Fact]
    public void 布局失效后字符位置查询拒绝并在重建后恢复()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);

        // InvalidateLayout 到下一次安全阶段之间：旧排版快照不得应答（R17 布局失效键）。
        host.Runtime.InvalidateLayout(host.Runtime.Root);
        Assert.False(QueryCharPosition(host, 0).Ok);
        Assert.False(QueryCharPosition(host, 2).Ok);

        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
        Assert.True(QueryCharPosition(host, 2).Ok);
        host.End();
    }

    [Fact]
    public void 环境字体变化后字符位置查询拒绝并在重建后恢复()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);

        // SetEnvironment(Font) 使全部排版失效：重建前不得用旧字体度量应答。
        host.Runtime.SetEnvironment(new EnvironmentValues
        {
            Font = FontDescription.Default with { Size = FontDescription.Default.Size + 4 },
        });
        Assert.False(QueryCharPosition(host, 0).Ok);

        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
        host.End();
    }

    [Fact]
    public void 横向滚动未重建前旧快照拒绝()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Value = new string('中', 60);
        var state = (TextFieldState)host.Runtime.Root.InteractionState!;
        state.Caret = 60;
        state.Anchor = 60;
        host.Runtime.BuildFrame();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);

        // ScrollX 改变但尚未安全阶段重建：旧位置不得泄漏。
        state.ScrollX += 10;
        Assert.False(QueryCharPosition(host, 0).Ok);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
        host.End();
    }

    [Fact]
    public void 非组合caret移动未重建前旧快照拒绝()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Value = "中中中中";
        var state = (TextFieldState)host.Runtime.Root.InteractionState!;
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok); // 无预编辑：偏移 0 = 文档 caret

        // caret 已移动但快照未重建：不得按旧 caret 应答。
        state.Caret = 3;
        state.Anchor = 3;
        Assert.False(QueryCharPosition(host, 0).Ok);
        host.Runtime.BuildFrame();
        Assert.True(QueryCharPosition(host, 0).Ok);
    }

    [Fact]
    public void 已识别字符查询的无效情形返回零而其他请求仍走默认链()
    {
        using var host = new Host();
        host.Bridge.ClientToScreen = (x, y) => (x, y);
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        host.Runtime.BuildFrame();

        var ptr = Marshal.AllocHGlobal(ImeCharPositionSize);
        try
        {
            Marshal.StructureToPtr(
                new Win32Ime.IMECHARPOSITION { dwSize = (uint)ImeCharPositionSize }, ptr, fDeleteOld: false);
            Assert.Equal(new IntPtr(1),
                host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, ptr.ToInt64()));

            // 已识别查询：一切无效输入都返回明确 FALSE（IntPtr.Zero = 本消息已结束），
            // 绝不返回 null 交给默认过程补出窗口原点/旧位置坐标。
            Assert.Equal(IntPtr.Zero,
                host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, 0));

            host.Bridge.PublishGeometry(TextInputGeometry.Unavailable);
            Assert.Equal(IntPtr.Zero,
                host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, ptr.ToInt64()));

            host.Runtime.SetFocus(null);
            Assert.Equal(IntPtr.Zero,
                host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, ptr.ToInt64()));

            // 不属本桥的请求仍照常链式默认转发（Send 返回 null = 会进入默认过程）。
            Assert.Null(host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_CANDIDATEWINDOW, ptr.ToInt64()));
            Assert.Null(host.Send(Win32Ime.WM_IME_REQUEST, 0x0099, ptr.ToInt64()));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ------------------------------------------------------------ 退休（Dispose）生命周期

    [Fact]
    public void 真实运行时退休后迟到消息零写入且销毁清理仍可用()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        Assert.True(host.Client.IsComposing);
        var writesBefore = host.Writes;

        // owner UI 线程正常退休：先标退休，再撤销会话/组合归属，最后恢复原过程。
        host.Bridge.Dispose();
        Assert.Null(host.Bridge.ActiveClient);
        Assert.False(host.Bridge.SubclassInstalled);
        Assert.True(host.Bridge.SubclassRestored);
        Assert.Equal(0, host.Bridge.RestoreFailureCount);

        // 迟到到达的组合/开始/结果/按键/结果回声/几何查询/上下文消息：不改状态、不写核心。
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Composition(result: "你好");
        host.End();
        Assert.Null(host.Send(Win32Ime.WM_KEYDOWN, 0x41));
        Assert.Null(host.Send(Win32Ime.WM_IME_CHAR, '你'));
        Assert.Null(host.Send(Win32Ime.WM_IME_SETCONTEXT, 1, 0x8000000F));
        Assert.Null(host.Send(Win32Ime.WM_IME_REQUEST, Win32Ime.IMR_QUERYCHARPOSITION, 0));

        Assert.Equal(writesBefore, host.Writes);
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Bridge.CallbackExceptionCount);
        Assert.Null(host.Bridge.MaskedSetContextLParam);
        // 退休后迟到按键也不得登记归属或通知运行时（零状态变更）。
        Assert.Null(host.Bridge.CurrentKeyToken);
        Assert.False(host.Bridge.CurrentKeyConsumedByIme);

        // 退休不破坏 WM_NCDESTROY 清理路径：仍幂等通过且不误报恢复失败。
        Assert.Null(host.Send(Win32Ime.WM_NCDESTROY));
        Assert.Equal(0, host.Bridge.RestoreFailureCount);
    }

    [Fact]
    public void 退休后所有输入契约入口零写入且迟到销毁消息仍可清理()
    {
        // 真实运行时 + 受控假句柄：覆盖“退休标志”在消息路径之外的写入入口（契约方法/按键归属）。
        var hwnd = new IntPtr(0x5712);
        using var host = new Host(hwnd);
        var client = host.Client;
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        Assert.True(client.IsComposing);
        var writes = host.Writes;
        var ensures = host.Ime.EnsureCalls;
        var cancels = host.Ime.CancelCalls;
        var publishes = host.Bridge.GeometryPublishCount;
        var revokes = host.Bridge.GeometryRevokeCount;
        var releases = host.Ime.ReleaseCount;

        host.Bridge.Dispose();

        // 契约层直接入口：建立会话、应用组合、发布几何、取消请求全部拒绝，且不触碰原生。
        Assert.False(host.Bridge.StartSession(client));
        Assert.Equal(ensures, host.Ime.EnsureCalls);
        Assert.False(host.Bridge.PublishGeometry(new TextInputGeometry(new Rect(1, 2, 3, 4), Rect.Empty, Rect.Empty)));
        Assert.False(host.Bridge.PublishGeometry(TextInputGeometry.Unavailable));
        Assert.Equal(publishes, host.Bridge.GeometryPublishCount);
        Assert.Equal(revokes, host.Bridge.GeometryRevokeCount);
        Assert.False(host.Bridge.RequestCancel());
        Assert.Equal(cancels, host.Ime.CancelCalls);
        Assert.False(host.Bridge.EndSession());
        var rejected = host.Bridge.ApplyComposition(
            [CompositionEvent.Update(0, 0, 0, 0, new TextRange(0, 0), "ni")]);
        Assert.False(rejected.Handled);
        Assert.True(rejected.HasRejection);
        Assert.Equal(0, rejected.AppliedEventCount);
        Assert.Equal(writes, host.Writes);

        // 迟到按键登记：不产生 token、不通知运行时（零状态变更）。
        host.Bridge.BeginKeyDispatch(new IntPtr(Win32Ime.VK_PROCESSKEY));
        Assert.Null(host.Bridge.CurrentKeyToken);
        Assert.False(host.Bridge.CurrentKeyConsumedByIme);

        // 迟到消息仍零动作：几何请求即使带有效锚点缓冲也不应答、不写入。
        host.Bridge.Dispose();
        host.Start();
        host.Composition(comp: "nihao", cursor: 5);
        host.Composition(result: "你好");
        host.End();
        Assert.Null(host.Send(Win32Ime.WM_IME_CHAR, '你'));
        Assert.Equal(writes, host.Writes);
        Assert.Equal(string.Empty, host.Value);
        Assert.Equal(0, host.Bridge.CallbackExceptionCount);
        Assert.Null(host.Bridge.ActiveClient);
        Assert.Null(host.Bridge.LastAnchorClient);

        // 晚到 WM_NCDESTROY：只做清理（本受控句柄不是真实窗口 → 按窗口已销毁解除安装），不写输入。
        Assert.Null(host.Send(Win32Ime.WM_NCDESTROY));
        Assert.False(host.Bridge.SubclassInstalled);
        Assert.True(host.Bridge.SubclassRestored);
        Assert.Equal(writes, host.Writes);
        Assert.Equal(0, host.Bridge.RestoreFailureCount);

        // 窗口已销毁 → 租约确实归还，后续来源可以接管（不虚占）。
        Assert.False(host.Bridge.HoldsWriterLease);
        Assert.Null(HostWriterRegistry.CurrentLease(hwnd));
        Assert.True(releases > 0);
    }

    [Fact]
    public void 恢复失败保留租约与钉住委托且owner可重复Dispose重试()
    {
        // 受控假句柄：不是真实窗口 → 恢复按“窗口已销毁”确定成功；另用未安装状态核对重试幂等。
        var hwnd = new IntPtr(0x5713);
        var bridge = new WindowsImeBridge(hwnd, new FakeIme(), new OpenTkUiDispatcher(() => { }), () => (800, 600));
        try
        {
            Assert.NotNull(HostWriterRegistry.CurrentLease(hwnd));
            Assert.True(bridge.HoldsWriterLease);

            // 从未安装子类化：没有原生回调可到达，Dispose 归还租约（不得虚占）。
            bridge.Dispose();
            Assert.True(bridge.HoldsWriterLease == false);
            Assert.Null(HostWriterRegistry.CurrentLease(hwnd));

            // 重复 Dispose（owner 线程重试入口）：不抛、不改已确认状态、不重复归还。
            bridge.Dispose();
            bridge.Dispose();
            Assert.False(bridge.HoldsWriterLease);
            Assert.False(bridge.SubclassInstalled);
            Assert.Equal(0, bridge.RestoreFailureCount);
            Assert.Null(HostWriterRegistry.CurrentLease(hwnd));
        }
        finally
        {
            if (HostWriterRegistry.CurrentLease(hwnd) is { } leftover)
            {
                HostWriterRegistry.Release(leftover);
            }
        }
    }

    [Fact]
    public void 未安装子类化的Host退休仍清理且不残留租约()
    {
        // 现有 Host 形态（IntPtr.Zero + SimulateInstalled）：租约从未取得，Dispose 必须仍能清理。
        using var host = new Host();
        Assert.True(host.Bridge.SubclassInstalled);
        host.Bridge.Dispose();
        Assert.False(host.Bridge.SubclassInstalled);
        Assert.True(host.Bridge.SubclassRestored);
        Assert.Equal(0, host.Bridge.RestoreFailureCount);
        Assert.False(host.Bridge.HoldsWriterLease);
    }

    [Fact]
    public void 外线程Dispose在已退休桥上仍被拒绝且不触碰恢复状态()
    {
        // 已退休（恢复失败可重试的入口）的桥上再来外线程 Dispose：同样零副作用，恢复重试只能由 owner 执行。
        using var host = new Host();
        host.Bridge.Dispose();
        var failures = host.Bridge.RestoreFailureCount;
        var restored = host.Bridge.SubclassRestored;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                host.Bridge.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Equal(failures, host.Bridge.RestoreFailureCount);
        Assert.Equal(restored, host.Bridge.SubclassRestored);
    }

    [Fact]
    public void 正常Dispose可重复调用且幂等()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);

        host.Bridge.Dispose();
        var publishes = host.Bridge.GeometryPublishCount;
        var revokes = host.Bridge.GeometryRevokeCount;

        // 重复 Dispose（owner 线程重试入口）：不抛、不重复恢复、不改变已退休状态。
        host.Bridge.Dispose();
        host.Bridge.Dispose();

        Assert.Null(host.Bridge.ActiveClient);
        Assert.False(host.Bridge.SubclassInstalled);
        Assert.True(host.Bridge.SubclassRestored);
        Assert.Equal(0, host.Bridge.RestoreFailureCount);
        Assert.Equal(0, host.Bridge.CallbackExceptionCount);
        Assert.Equal(publishes, host.Bridge.GeometryPublishCount);
        Assert.Equal(revokes, host.Bridge.GeometryRevokeCount);
    }

    [Fact]
    public void 外线程Dispose被拒绝且不改变活动状态()
    {
        using var host = new Host();
        host.Start();
        host.Composition(comp: "ni", cursor: 2);
        var client = host.Bridge.ActiveClient;
        Assert.NotNull(client);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                host.Bridge.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        // 跨线程释放无副作用：会话、组合归属与子类化状态原样保留，活动桥继续按原样工作。
        Assert.Same(client, host.Bridge.ActiveClient);
        Assert.True(host.Bridge.SubclassInstalled);
        Assert.False(host.Bridge.SubclassRestored);

        host.Composition(result: "你好");
        Assert.Equal("你好", host.Value);
        Assert.Equal(1, host.Writes);
    }

    [Fact]
    public void 桥持有租约期间第二写入来源被拒绝且确认退役后归还()
    {
        // 受控假句柄：不安装真实子类化，只验证租约身份、不抢占与归还时机。
        // 不使用 HostWriterRegistry.ResetForTesting（那会清掉其它测试的真实持有者）。
        var hwnd = new IntPtr(0x5711);
        var bridge = new WindowsImeBridge(hwnd, new FakeIme(), new OpenTkUiDispatcher(() => { }), () => (800, 600));
        try
        {
            var lease = HostWriterRegistry.CurrentLease(hwnd);
            Assert.NotNull(lease);
            Assert.Equal(HostWriterKind.Imm, lease!.Kind);
            Assert.Null(bridge.InstallError);

            // 第二个独立实例（另一来源、另一租约）不得抢占本桥的租约。
            Assert.False(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var second, out var reason));
            Assert.Null(second);
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Same(lease, HostWriterRegistry.CurrentLease(hwnd));
        }
        finally
        {
            bridge.Dispose();
            if (HostWriterRegistry.CurrentLease(hwnd) is { } leftover)
            {
                HostWriterRegistry.Release(leftover);
            }
        }

        // 从未安装子类化 → Dispose 确认无原生引用后归还租约，后续来源可以正常接管。
        Assert.Null(HostWriterRegistry.CurrentLease(hwnd));
        Assert.True(HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var next, out _));
        Assert.True(HostWriterRegistry.Release(next));
        Assert.Null(HostWriterRegistry.CurrentLease(hwnd));
    }
}
