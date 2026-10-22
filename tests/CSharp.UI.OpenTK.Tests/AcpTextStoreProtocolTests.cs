using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P2b：生产 ACP 协议测试（<see cref="AcpTextStore"/> / <see cref="TextStoreLockManager"/> /
/// <see cref="AcpStagingBuffer"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 这些测试直接引用<b>生产</b>类型，不 source-link tools 程序集。sink 用协议替身记录调用，
/// 不创建可见窗口、不注入键盘、不改全局输入法配置。
/// </para>
/// <para>全部断言真实 HRESULT 与输出参数初始化，不用“看起来成功”的宽松判据。</para>
/// </remarks>
public sealed class AcpTextStoreProtocolTests
{
    // ---------------------------------------------------------------- 替身

    private sealed class RecordingSink : ITextStoreACPSink
    {
        public List<(uint Flags, TsTextChange Change)> TextChanges { get; } = [];
        public int SelectionChanges { get; private set; }
        public List<uint> LockGrants { get; } = [];
        public int LayoutChanges { get; private set; }
        public bool ThrowOnGrant { get; set; }

        /// <summary>授予期间回调（模拟 TSF 在 OnLockGranted 内调用 store）。</summary>
        public Action<uint>? OnGranted { get; set; }

        /// <summary>文本通知回调（模拟 sink 在 OnTextChange 内重入 store）。</summary>
        public Action? OnTextChanged { get; set; }

        public int OnTextChange(uint dwFlags, ref TsTextChange pChange)
        {
            TextChanges.Add((dwFlags, pChange));
            OnTextChanged?.Invoke();
            return 0;
        }

        public int OnSelectionChange()
        {
            SelectionChanges++;
            return 0;
        }

        public int OnLayoutChange(int lcode, uint vcView)
        {
            LayoutChanges++;
            return 0;
        }

        public int OnStatusChange(uint dwFlags) => 0;

        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs) => 0;

        public int OnLockGranted(uint dwLockFlags)
        {
            LockGrants.Add(dwLockFlags);
            OnGranted?.Invoke(dwLockFlags);
            if (ThrowOnGrant)
            {
                throw new InvalidOperationException("sink grant failure");
            }

            return 0;
        }

        public int OnStartEditTransaction() => 0;

        public int OnEndEditTransaction() => 0;
    }

    /// <summary>可编排的几何提供方（只读，不测量）。</summary>
    private sealed class ScriptedGeometry : IAcpGeometryProvider
    {
        public bool TextBound { get; set; }
        public TsfRect TextRect { get; set; }
        public bool TextClipped { get; set; }
        public bool TextKnownEmpty { get; set; }

        public bool ScreenBound { get; set; }
        public TsfRect ScreenRect { get; set; }
        public int ScreenHResult { get; set; }

        public int TextQueries { get; private set; }
        public int ScreenQueries { get; private set; }

        public AcpGeometryQueryIdentity? LastIdentity { get; private set; }

        public bool TryGetTextRect(int acpStart, int acpEnd, in AcpGeometryQueryIdentity identity,
            out TsfRect rect, out bool clipped, out bool knownEmpty)
        {
            LastIdentity = identity;
            TextQueries++;
            rect = TextRect;
            clipped = TextClipped;
            knownEmpty = TextKnownEmpty;
            return TextBound;
        }

        public bool TryGetScreenRect(out TsfRect rect, out int hresult)
        {
            ScreenQueries++;
            rect = ScreenRect;
            hresult = ScreenHResult;
            return ScreenBound;
        }
    }

    private static (AcpTextStore Store, RecordingSink Sink) CreateStore(
        string text = "abcd", uint mask = TsfFlags.TS_AS_ALL_SINKS)
    {
        var store = new AcpTextStore();
        store.ResetAuthoritativeText(text);
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, mask));
        return (store, sink);
    }

    /// <summary>在真实写锁会话内执行动作（同步授予）。</summary>
    private static void InWriteLock(AcpTextStore store, uint flags, Action body)
    {
        var sink = new RecordingSink { OnGranted = granted => body() };
        // 用一次性 sink 承载本次授予，避免影响既有 sink 记录。
        var previous = store.HasSink;
        if (!previous)
        {
            Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        }
    }

    // ---------------------------------------------------------------- 未持锁时的拒绝

    [Fact]
    public void 未持锁时读方法返回TS_E_NOLOCK且输出已初始化()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;

        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetEndACP(out var end));
        Assert.Equal(0, end);

        Assert.Equal(TsfHResult.TS_E_NOLOCK,
            store.GetText(0, -1, IntPtr.Zero, 0, out var plain, IntPtr.Zero, 0, out var runs, out var next));
        Assert.Equal(0u, plain);
        Assert.Equal(0u, runs);
        Assert.Equal(0, next);

        Assert.Equal(TsfHResult.TS_E_NOLOCK,
            store.GetSelection(0, 1, IntPtr.Zero, out var fetched));
        Assert.Equal(0u, fetched);

        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.QueryInsert(0, 0, 1, out var qs, out var qe));
        Assert.Equal(0, qs);
        Assert.Equal(0, qe);
    }

    [Fact]
    public void 未持锁时写方法返回TS_E_NOLOCK且不修改文档()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;

        Assert.Equal(TsfHResult.TS_E_NOLOCK,
            store.SetText(0, 0, 0, IntPtr.Zero, 0, out var change));
        Assert.Equal(0, change.AcpStart);

        Assert.Equal(TsfHResult.TS_E_NOLOCK,
            store.InsertTextAtSelection(0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.SetSelection(0, IntPtr.Zero));

        // 文档未被修改。
        Assert.Equal("abcd", store.Document.Text);
        Assert.Equal(0, store.Staging.PendingCount);
    }

    // ---------------------------------------------------------------- 锁协议

    [Fact]
    public void 同步读锁授予并按授予类型回调()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var hr = store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out var session);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(TsfHResult.S_OK, session);
        // 授予位是 READ，绝不是请求 flags（0x3）。
        Assert.Equal([TsfFlags.TS_LF_READ], sink.LockGrants);
        // 授予结束后锁已释放。
        Assert.Equal(0u, store.Locks.LockType);
    }

    [Fact]
    public void 同步重入返回TS_E_SYNCHRONOUS且不清除已排队升级()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var nested = 0;
        var upgradeSession = 0;
        sink.OnGranted = flags =>
        {
            if (nested++ > 0)
            {
                return;
            }

            // 首次授予内：同步请求 → 拒绝重入；异步写升级 → 排队。
            Assert.Equal(TsfHResult.S_OK,
                store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out var syncSession));
            Assert.Equal(TsfHResult.TS_E_SYNCHRONOUS, syncSession);

            Assert.Equal(TsfHResult.S_OK,
                store.RequestLock(TsfFlags.TS_LF_READWRITE, out upgradeSession));
            Assert.Equal(TsfHResult.TS_S_ASYNC, upgradeSession);
        };

        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 授予序列：READ，然后排空时授予 READWRITE（写包含读）。
        Assert.Equal([TsfFlags.TS_LF_READ, TsfFlags.TS_LF_READWRITE], sink.LockGrants);
        Assert.Equal(TsfHResult.TS_S_ASYNC, upgradeSession);
    }

    [Fact]
    public void 非法锁标志被拒绝且不授予()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        // 只有写位（缺 READ）。
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_WRITE, out var onlyWrite));
        Assert.Equal(TsfHResult.E_INVALIDARG, onlyWrite);

        // 未知高位。
        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_READ | 0x100, out var unknown));
        Assert.Equal(TsfHResult.E_INVALIDARG, unknown);

        Assert.Empty(sink.LockGrants);
        Assert.Equal(0u, store.Locks.LockType);
    }

    [Fact]
    public void 有界排空上限内完成且pending有界()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var depth = 0;
        sink.OnGranted = granted =>
        {
            if (depth++ < 4)
            {
                store.RequestLock(TsfFlags.TS_LF_READ, out _);
            }
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // pending 至多一个读 + 一个写。
        Assert.InRange(store.Locks.PendingCount, 0, 2);
    }

    [Fact]
    public void 写升级合并掉pending读()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;

        var grants = new List<uint>();
        var once = 0;
        ((RecordingSink)GetSink(store)!).OnGranted = granted =>
        {
            grants.Add(granted);
            if (once++ > 0)
            {
                return;
            }

            // 先排一个读，再用写升级覆盖它。
            store.RequestLock(TsfFlags.TS_LF_READ, out _);
            store.RequestLock(TsfFlags.TS_LF_READWRITE, out _);
        };

        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 写包含读：最终只授予 READWRITE（合并），不重复授予 READ。
        Assert.Equal(TsfFlags.TS_LF_READWRITE, grants[^1]);
    }

    [Fact]
    public void 无sink时不授予锁且如实失败()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("ab");

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ, out var session));
        Assert.Equal(TsfHResult.E_FAIL, session);
        Assert.Equal(0u, store.Locks.LockType);
    }

    [Fact]
    public void sink授予抛异常被收敛且锁状态已释放()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;
        sink.ThrowOnGrant = true;

        Assert.Equal(TsfHResult.S_OK,
            store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 异常不穿过 COM 边界，锁仍被正确释放。
        Assert.Equal(0u, store.Locks.LockType);
    }

    private static object? GetSink(AcpTextStore store)
    {
        // 通过反射读取私有 sink 字段（测试观察协议行为，不改变生产可见性）。
        var field = typeof(AcpTextStore).GetField("_sink",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(store);
    }

    // ---------------------------------------------------------------- Advise/Unadvise

    [Fact]
    public void AdviseSink错误IID返回TS_E_NOINTERFACE且不安装()
    {
        using var store = new AcpTextStore();
        var sink = new RecordingSink();

        Assert.Equal(TsfHResult.TS_E_NOINTERFACE, store.AdviseSink(Guid.NewGuid(), sink, TsfFlags.TS_AS_ALL_SINKS));
        Assert.False(store.HasSink);
    }

    [Fact]
    public void R3同一identity重复注册更新mask并成功()
    {
        var (store, sink) = CreateStore("abcd", TsfFlags.TS_AS_TEXT_CHANGE);
        using var _s = store;

        // 同一对象再次注册：官方要求更新 dwMask 并返回 S_OK（不额外持有引用）。
        Assert.Equal(TsfHResult.S_OK,
            store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_SEL_CHANGE));
        Assert.Equal(TsfFlags.TS_AS_SEL_CHANGE, store.SinkMask);

        // 新 mask 立即生效：文本通知被抑制，选区通知被允许。
        store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        Assert.Empty(sink.TextChanges);
        store.NotifySelectionChange();
        Assert.Equal(1, sink.SelectionChanges);
    }

    [Fact]
    public void R3不同sink对象在旧sink移除前被拒绝()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;

        Assert.Equal(TsfHResult.CONNECT_E_ADVISELIMIT,
            store.AdviseSink(TsfIids.TextStoreAcpSink, new RecordingSink(), TsfFlags.TS_AS_ALL_SINKS));
    }

    [Fact]
    public void R3UnadviseSink无连接返回CONNECT_E_NOCONNECTION()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("abcd");

        // 从未注册：官方 CONNECT_E_NOCONNECTION（与 TS_E_INVALIDPOS 同值，靠方法区分）。
        Assert.Equal(TsfHResult.CONNECT_E_NOCONNECTION, store.UnadviseSink(new RecordingSink()));
    }

    [Fact]
    public void R3UnadviseSink身份不匹配返回E_INVALIDARG()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;

        Assert.Equal(TsfHResult.E_INVALIDARG, store.UnadviseSink(new RecordingSink()));
        Assert.True(store.HasSink);
    }

    [Fact]
    public void UnadviseSink后不再回调且丢弃排队通知()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));
        Assert.False(store.HasSink);
        Assert.Equal(0u, store.SinkMask);

        // 卸载后应用侧通知不再发送。
        store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        store.FlushDeferredNotifications();
        Assert.Empty(sink.TextChanges);
    }

    // ---------------------------------------------------------------- GetText

    [Fact]
    public void 持读锁GetText按UTF16码元读取并推进pacpNext()
    {
        var (store, sink) = CreateStore("a\uD83D\uDE00b"); // a + 😀 + b = 4 码元
        using var _s = store;

        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            sink.OnGranted = granted =>
            {
                var hr = store.GetText(0, -1, buffer, 4, out var plain, IntPtr.Zero, 0, out var runs, out var next);
                Assert.Equal(TsfHResult.S_OK, hr);
                Assert.Equal(4u, plain);
                Assert.Equal(4, next);
                Assert.Equal(0u, runs);

                // 码元原样（代理对未被拆解或替换）。
                var text = Marshal.PtrToStringUni(buffer, 4);
                Assert.Equal("a\uD83D\uDE00b", text);

                // 子范围：代理对中间（码元 1..3）也能读，按码元不按字素。
                Assert.Equal(TsfHResult.S_OK,
                    store.GetText(1, 3, buffer, 2, out var mid, IntPtr.Zero, 0, out _, out _));
                Assert.Equal(2u, mid);
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void 查询模式容量为零时只推进位置不写缓冲()
    {
        var (store, sink) = CreateStore("abcdef");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            // run-only 调用：pchPlain=NULL, cchPlainReq=0。
            Assert.Equal(TsfHResult.S_OK,
                store.GetText(0, -1, IntPtr.Zero, 0, out var plain, IntPtr.Zero, 0, out var runs, out var next));
            Assert.Equal(0u, plain);
            Assert.Equal(0u, runs);
            Assert.Equal(6, next);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void 非零容量配空缓冲区返回E_INVALIDARG()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.E_INVALIDARG,
                store.GetText(0, -1, IntPtr.Zero, 4, out _, IntPtr.Zero, 0, out _, out _));
            Assert.Equal(TsfHResult.E_INVALIDARG,
                store.GetText(0, -1, IntPtr.Zero, 0, out _, IntPtr.Zero, 2, out _, out _));
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void 越界范围返回TS_E_INVALIDPOS()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.TS_E_INVALIDPOS,
                store.GetText(0, 99, IntPtr.Zero, 0, out _, IntPtr.Zero, 0, out _, out _));
            Assert.Equal(TsfHResult.TS_E_INVALIDPOS,
                store.GetText(-1, 2, IntPtr.Zero, 0, out _, IntPtr.Zero, 0, out _, out _));
            Assert.Equal(TsfHResult.TS_E_INVALIDPOS,
                store.QueryInsert(0, 99, 1, out _, out _));
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void GetEndACP与GetSelection返回真实值()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var selectionBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<TsSelectionAcp>());
        try
        {
            sink.OnGranted = granted =>
            {
                Assert.Equal(TsfHResult.S_OK, store.GetEndACP(out var end));
                Assert.Equal(4, end);

                Assert.Equal(TsfHResult.S_OK,
                    store.GetSelection(0, 1, selectionBuffer, out var fetched));
                Assert.Equal(1u, fetched);
                var selection = Marshal.PtrToStructure<TsSelectionAcp>(selectionBuffer);
                Assert.Equal(0, selection.AcpStart);
                Assert.Equal(0, selection.AcpEnd);
                Assert.Equal(TsfFlags.TS_AE_NONE, selection.Style.ActiveSelEnd);

                // 只有索引 0 / TS_DEFAULT_SELECTION 有效。
                Assert.Equal(TsfHResult.S_OK, store.GetSelection(5, 1, selectionBuffer, out var none));
                Assert.Equal(0u, none);
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(selectionBuffer);
        }
    }

    // ---------------------------------------------------------------- 暂存编辑（不写 Binding）

    [Fact]
    public void TSF写入只进入暂存且不回送自身通知()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var textBuffer = Marshal.StringToHGlobalUni("XY");
        try
        {
            sink.OnGranted = granted =>
            {
                Assert.Equal(TsfHResult.S_OK,
                    store.SetText(0, 0, 0, textBuffer, 2, out var change));
                Assert.Equal(0, change.AcpStart);
                Assert.Equal(2, change.AcpNewEnd);
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(textBuffer);
        }

        // 文本确实更新（原生视图）。
        Assert.Equal("XYab", store.Document.Text);

        // 但绝不回送 OnTextChange（TSF 已知该变更）。
        Assert.Empty(sink.TextChanges);

        // 只进入暂存批次，且批次不是权威快照。
        var staged = store.Staging.DrainForGeneration(out var generation);
        Assert.Single(staged);
        Assert.Equal(AcpStagedEditKind.Insert, staged[0].Kind);
        Assert.Equal("XY", staged[0].Inserted);
        Assert.Equal(generation, staged[0].NativeGeneration);
    }

    [Fact]
    public void 暂存编辑记录删除类别()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.S_OK,
                store.SetText(0, 1, 3, IntPtr.Zero, 0, out _));
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));

        Assert.Equal("ad", store.Document.Text);
        var staged = store.Staging.DrainForGeneration(out _);
        Assert.Single(staged);
        Assert.Equal(AcpStagedEditKind.Delete, staged[0].Kind);
        Assert.Equal(string.Empty, staged[0].Inserted);
    }

    [Fact]
    public void 暂存有界且超容量丢弃最旧并计数()
    {
        var buffer = new AcpStagingBuffer(capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            buffer.Record(AcpStagedEditKind.Insert, new AcpTextChange(i, i, i + 1, i + 1), "x", false);
        }

        Assert.Equal(3, buffer.PendingCount);
        Assert.Equal(2, buffer.DroppedCount);

        var drained = buffer.DrainForGeneration(out _);
        Assert.Equal(3, drained.Count);
        // 最旧的被丢弃：保留最后三条（start 2,3,4）。
        Assert.Equal(2, drained[0].Change.Start);
    }

    [Fact]
    public void 推进代数后陈旧暂存被丢弃()
    {
        var buffer = new AcpStagingBuffer();
        buffer.Record(AcpStagedEditKind.Insert, new AcpTextChange(0, 0, 1, 1), "a", false);
        var oldGeneration = buffer.Generation;

        buffer.AdvanceGeneration();

        var drained = buffer.DrainForGeneration(out var generation);
        Assert.Empty(drained);
        Assert.NotEqual(oldGeneration, generation);
    }

    [Fact]
    public void InsertTextAtSelection查询模式不修改文本()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var startPtr = Marshal.AllocHGlobal(4);
        var endPtr = Marshal.AllocHGlobal(4);
        try
        {
            sink.OnGranted = granted =>
            {
                Assert.Equal(TsfHResult.S_OK,
                    store.InsertTextAtSelection(TsfFlags.TS_IAS_QUERYONLY, IntPtr.Zero, 0,
                        startPtr, endPtr, IntPtr.Zero));

                Assert.Equal(0, Marshal.ReadInt32(startPtr));
                Assert.Equal(0, Marshal.ReadInt32(endPtr));
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(startPtr);
            Marshal.FreeHGlobal(endPtr);
        }

        Assert.Equal("abcd", store.Document.Text);
        Assert.Equal(0, store.Staging.PendingCount);
    }

    [Fact]
    public void InsertTextAtSelection写入后选区移到插入末尾()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var textBuffer = Marshal.StringToHGlobalUni("XY");
        try
        {
            sink.OnGranted = granted =>
            {
                Assert.Equal(TsfHResult.S_OK,
                    store.InsertTextAtSelection(0, textBuffer, 2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(textBuffer);
        }

        Assert.Equal("XYab", store.Document.Text);
        var (start, end) = store.Document.Selection;
        Assert.Equal(2, start);
        Assert.Equal(2, end);
        // 响应 InsertTextAtSelection 的内部选区调整不回送 OnSelectionChange。
        Assert.Equal(0, sink.SelectionChanges);
    }

    // ---------------------------------------------------------------- 权威更新（防陈旧代数）

    [Fact]
    public void 权威更新推进代数并使旧暂存失效()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var capturedGeneration = store.NativeGeneration;

        sink.OnGranted = granted =>
        {
            store.SetText(0, 0, 0, IntPtr.Zero, 0, out _);
        };
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("authoritative", TextSelection.Caret(3), version: 7),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, capturedGeneration));
        Assert.Equal("authoritative", store.Document.Text);
        Assert.NotEqual(capturedGeneration, store.NativeGeneration);

        // 旧代数再次更新被拒绝。
        Assert.False(store.ApplyAuthoritativeUpdate(snapshot, capturedGeneration));
    }

    [Fact]
    public void 权威更新不写绑定也不推断组合结局()
    {
        var (store, _) = CreateStore("ab");
        using var _s = store;
        var generation = store.NativeGeneration;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("cd", TextSelection.Caret(1), version: 3),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        // 只是原生视图同步：暂存为空（没有产生“原生编辑”）。
        Assert.Equal(0, store.Staging.PendingCount);
        var (text, start, end, _) = store.ReadAuthoritativeDocument();
        Assert.Equal("cd", text);
        Assert.Equal(1, start);
        Assert.Equal(1, end);
    }

    // ---------------------------------------------------------------- 通知 mask 与延迟

    [Fact]
    public void 应用侧通知按mask门控()
    {
        var (store, sink) = CreateStore("ab", TsfFlags.TS_AS_TEXT_CHANGE);
        using var _s = store;

        store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        Assert.Single(sink.TextChanges);

        // 未请求选区 mask：不发送。
        store.NotifySelectionChange();
        Assert.Equal(0, sink.SelectionChanges);
    }

    [Fact]
    public void 持锁期间通知延迟到锁外安全阶段()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            // 持锁时通知必须延迟：此刻不发送。
            store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
            Assert.Empty(sink.TextChanges);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 锁已释放，但尚未 flush：仍未发送。
        Assert.Empty(sink.TextChanges);

        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void 无dispatcher时保留pending由调用方显式flush()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        // 未注入 UiDispatcher：不跨线程回调。
        store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        Assert.Single(sink.TextChanges);

        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void 注入dispatcher时延迟通知经同一线程调度()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var queued = new Queue<Action>();
        store.UiDispatcher = action => queued.Enqueue(action);

        sink.OnGranted = granted => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 安全阶段规则：RequestLock 栈内不调度（因此此时队列可能为空，pending 保留在 store 内）。
        Assert.Empty(sink.TextChanges);

        // 真正的安全阶段：显式 Flush 发送（若之前已排队则执行队列）。
        while (queued.Count > 0)
        {
            queued.Dequeue()();
        }

        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void 布局通知在持锁期间延迟且连接不变时只发送一次()
    {
        // 对照项：证明“延迟入队”确实持有待发送项（不是从未入队）。
        var (store, sink) = CreateStore("ab", TsfFlags.TS_AS_LAYOUT_CHANGE);
        using var _s = store;

        sink.OnGranted = granted =>
        {
            Assert.True(store.NotifyLayoutChange(), "持锁期间布局通知必须入队并返回 true。");
            Assert.Equal(0, sink.LayoutChanges);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 锁已释放、尚未 flush：仍不发送。
        Assert.Equal(0, sink.LayoutChanges);

        store.FlushDeferredNotifications();
        Assert.Equal(1, sink.LayoutChanges);

        // 队列已清空：再次 flush 不重复发送。
        store.FlushDeferredNotifications();
        Assert.Equal(1, sink.LayoutChanges);
    }

    [Fact]
    public void 布局通知在sink撤销后丢弃且不泄漏给新连接()
    {
        // 审计：持锁布局入队 → Unadvise A → Advise B → flush。
        // 延迟项属于 A 的连接；撤销 A 会清空延迟队列，B 不得收到旧连接的迟到通知。
        var (store, sinkA) = CreateStore("ab", TsfFlags.TS_AS_LAYOUT_CHANGE);
        using var _s = store;

        sinkA.OnGranted = granted =>
        {
            Assert.True(store.NotifyLayoutChange());
            Assert.Equal(0, sinkA.LayoutChanges);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        Assert.Equal(0, sinkA.LayoutChanges);

        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sinkA));
        var sinkB = new RecordingSink();
        Assert.Equal(TsfHResult.S_OK,
            store.AdviseSink(TsfIids.TextStoreAcpSink, sinkB, TsfFlags.TS_AS_LAYOUT_CHANGE));

        store.FlushDeferredNotifications();

        // 旧连接未收到；新连接也不接收旧连接排队的通知。
        Assert.Equal(0, sinkA.LayoutChanges);
        Assert.Equal(0, sinkB.LayoutChanges);

        // 新连接之后的真实通知仍然可用（不是永久禁发）。
        Assert.True(store.NotifyLayoutChange());
        Assert.Equal(1, sinkB.LayoutChanges);
    }

    [Fact]
    public void 布局通知未订阅layout掩码时返回false且不排队()
    {
        var (store, sink) = CreateStore("ab", TsfFlags.TS_AS_TEXT_CHANGE);
        using var _s = store;

        Assert.False(store.NotifyLayoutChange());

        store.FlushDeferredNotifications();
        Assert.Equal(0, sink.LayoutChanges);
    }

    // ---------------------------------------------------------------- 几何

    [Fact]
    public void GetTextExt未绑定返回TS_E_NOLAYOUT且不伪造零矩形()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.TS_E_NOLAYOUT,
                store.GetTextExt(store.ViewCookie, 0, 2, out var rect, out var clipped));
            Assert.True(rect.IsEmpty);
            Assert.Equal(0, clipped);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void GetTextExt要求读锁而GetScreenExt不要求()
    {
        var geometry = new ScriptedGeometry
        {
            TextBound = true,
            TextRect = TsfRect.FromLtrb(1, 2, 3, 4),
            ScreenBound = true,
            ScreenRect = TsfRect.FromLtrb(10, 20, 110, 220),
            ScreenHResult = TsfHResult.S_OK,
        };
        using var store = new AcpTextStore(geometry);
        store.ResetAuthoritativeText("abcd");
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        // 未持锁：GetTextExt 拒绝。
        Assert.Equal(TsfHResult.TS_E_NOLOCK,
            store.GetTextExt(store.ViewCookie, 0, 2, out _, out _));

        // 未持锁：GetScreenExt 仍可用（独立于读锁与文本布局）。
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(store.ViewCookie, out var screen));
        Assert.Equal(10, screen.Left);
        Assert.Equal(1, geometry.ScreenQueries);
        Assert.Equal(0, geometry.TextQueries);
    }

    [Fact]
    public void GetTextExt已布局但完全不可见返回S_OK零矩形与clipped()
    {
        var geometry = new ScriptedGeometry { TextBound = true, TextKnownEmpty = true };
        using var store = new AcpTextStore(geometry);
        store.ResetAuthoritativeText("abcd");
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.S_OK,
                store.GetTextExt(store.ViewCookie, 0, 2, out var rect, out var clipped));
            Assert.True(rect.IsEmpty);
            Assert.Equal(1, clipped);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void GetScreenExt非法view返回E_INVALIDARG()
    {
        var geometry = new ScriptedGeometry
        {
            ScreenBound = true,
            ScreenHResult = TsfHResult.S_OK,
        };
        using var store = new AcpTextStore(geometry);
        store.ResetAuthoritativeText("ab");

        Assert.Equal(TsfHResult.E_INVALIDARG, store.GetScreenExt(999, out var rect));
        Assert.True(rect.IsEmpty);
        Assert.Equal(0, geometry.ScreenQueries);
    }

    [Fact]
    public void GetScreenExt未绑定返回显式不可用()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("ab");

        var hr = store.GetScreenExt(store.ViewCookie, out var rect);
        Assert.True(hr < 0, $"未绑定几何必须显式失败，实际 {TsfHResult.Describe(hr)}。");
        Assert.True(rect.IsEmpty);
    }

    [Fact]
    public void GetWnd返回绑定句柄且非法view被拒绝()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("ab");
        var hwnd = new IntPtr(0x1234);
        store.AttachWindow(hwnd);

        Assert.Equal(TsfHResult.S_OK, store.GetWnd(store.ViewCookie, out var actual));
        Assert.Equal(hwnd, actual);

        Assert.Equal(TsfHResult.TF_E_INVALIDVIEW, store.GetWnd(999, out var none));
        Assert.Equal(IntPtr.Zero, none);
    }

    // ---------------------------------------------------------------- 不支持的操作

    [Fact]
    public void 不支持的操作返回明确HRESULT且输出已初始化()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("ab");

        Assert.Equal(TsfHResult.E_NOTIMPL, store.GetFormattedText(0, 1, out var dataObject));
        Assert.Equal(IntPtr.Zero, dataObject);

        Assert.Equal(TsfHResult.TS_E_NOOBJECT, store.GetEmbedded(0, IntPtr.Zero, IntPtr.Zero, out var punk));
        Assert.Equal(IntPtr.Zero, punk);

        Assert.Equal(TsfHResult.S_OK, store.QueryInsertEmbedded(IntPtr.Zero, IntPtr.Zero, out var insertable));
        Assert.Equal(0, insertable);

        Assert.Equal(TsfHResult.E_NOTIMPL, store.RequestSupportedAttrs(0, 0, IntPtr.Zero));
        Assert.Equal(TsfHResult.E_NOTIMPL, store.RequestAttrsAtPosition(0, 0, IntPtr.Zero, 0));
        Assert.Equal(TsfHResult.E_NOTIMPL, store.RequestAttrsTransitioningAtPosition(0, 0, IntPtr.Zero, 0));

        Assert.Equal(TsfHResult.E_NOTIMPL,
            store.FindNextAttrTransition(0, 1, 0, IntPtr.Zero, 0, out var next, out var found, out var offset));
        Assert.Equal(0, next);
        Assert.Equal(0, found);
        Assert.Equal(0, offset);

        Assert.Equal(TsfHResult.S_OK, store.RetrieveRequestedAttrs(0, IntPtr.Zero, out var fetched));
        Assert.Equal(0u, fetched);

        // 未持读锁时锁检查优先（TS_E_NOLOCK），不会越到几何判定。
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetACPFromPoint(store.ViewCookie, IntPtr.Zero, 0, out var acp));
        Assert.Equal(0, acp);
    }

    [Fact]
    public void GetACPFromPoint持锁时按非法view与无布局分别拒绝()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted =>
        {
            // 非法 view：TF_E_INVALIDVIEW。
            Assert.Equal(TsfHResult.TF_E_INVALIDVIEW,
                store.GetACPFromPoint(999, IntPtr.Zero, 0, out _));

            // 合法 view 但空指针：TS_E_INVALIDPOINT。
            Assert.Equal(TsfHResult.TS_E_INVALIDPOINT,
                store.GetACPFromPoint(store.ViewCookie, IntPtr.Zero, 0, out _));

            // 合法指针但本批不实现命中测试：显式无布局，绝不猜位置。
            var point = Marshal.AllocHGlobal(Marshal.SizeOf<TsfPoint>());
            try
            {
                Marshal.StructureToPtr(new TsfPoint { X = 5, Y = 5 }, point, false);
                Assert.Equal(TsfHResult.TS_E_NOLAYOUT,
                    store.GetACPFromPoint(store.ViewCookie, point, 0, out _));
            }
            finally
            {
                Marshal.FreeHGlobal(point);
            }
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    // ---------------------------------------------------------------- R1：几何坐标契约

    [Fact]
    public void R1GetTextExt返回屏幕物理像素原样不二次换算()
    {
        // 提供方给出已含窗口屏幕原点的屏幕矩形（非零、可含负值）。
        var geometry = new ScriptedGeometry
        {
            TextBound = true,
            TextRect = TsfRect.FromLtrb(-120, 340, -60, 365),
        };
        using var store = new AcpTextStore(geometry);
        store.ResetAuthoritativeText("abcd");
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.S_OK,
                store.GetTextExt(store.ViewCookie, 0, 2, out var rect, out var clipped));
            // 原样返回：store 不做客户区→屏幕换算，也不改变符号。
            Assert.Equal(-120, rect.Left);
            Assert.Equal(340, rect.Top);
            Assert.Equal(-60, rect.Right);
            Assert.Equal(365, rect.Bottom);
            Assert.Equal(0, clipped);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void R1零长度caret查询返回真实caret矩形而不是拒绝()
    {
        // 零长度范围是合法的 caret 查询（TSF 用它定位插入点/候选锚点）。
        // 已验收原型支持该路径，本批保留；提供方返回零宽、行高非零的 caret 矩形。
        var geometry = new ScriptedGeometry
        {
            TextBound = true,
            TextRect = TsfRect.FromLtrb(40, 12, 40, 32),
        };
        using var store = new AcpTextStore(geometry);
        store.ResetAuthoritativeText("abcd");
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        sink.OnGranted = granted =>
        {
            Assert.Equal(TsfHResult.S_OK,
                store.GetTextExt(store.ViewCookie, 2, 2, out var rect, out var clipped));
            // 真实 caret 矩形：零宽但有行高，且不被当成零矩形成功。
            Assert.Equal(40, rect.Left);
            Assert.Equal(40, rect.Right);
            Assert.Equal(12, rect.Top);
            Assert.Equal(32, rect.Bottom);
            Assert.Equal(0, clipped);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void R1零长度caret查询在未绑定时仍显式无布局()
    {
        // 未绑定几何：即便 caret 查询合法，也必须如实报告无布局（不伪造成功）。
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("abcd");
        var sink = new RecordingSink();
        Assert.Equal(0, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        sink.OnGranted = granted =>
            Assert.Equal(TsfHResult.TS_E_NOLAYOUT,
                store.GetTextExt(store.ViewCookie, 2, 2, out _, out _));

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    // ---------------------------------------------------------------- R2：所属 UI 线程保护

    [Fact]
    public void R2跨线程COM调用被拒绝且零sink调用()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        var ownerThread = store.OwnerThreadId;
        var results = new List<int>();
        var exception = (Exception?)null;

        var thread = new Thread(() =>
        {
            try
            {
                results.Add(store.GetEndACP(out _));
                results.Add(store.RequestLock(TsfFlags.TS_LF_READ, out _));
                results.Add(store.AdviseSink(TsfIids.TextStoreAcpSink, new RecordingSink(), TsfFlags.TS_AS_ALL_SINKS));
                results.Add(store.SetText(0, 0, 0, IntPtr.Zero, 0, out _));
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(exception);
        // 全部显式失败。
        Assert.All(results, hr => Assert.Equal(TsfHResult.E_FAIL, hr));
        Assert.Equal(4, store.CrossThreadRejectedCount);

        // 关键：跨线程从未授予锁、从未回调 sink。
        Assert.Empty(sink.LockGrants);
        Assert.Equal(0u, store.Locks.LockType);

        // 所属线程仍正常。
        Assert.Equal(ownerThread, store.OwnerThreadId);
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
    }

    [Fact]
    public void R2跨线程权威更新与重置被拒绝且文档不变()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("abcd");
        var generation = store.NativeGeneration;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("zzz", TextSelection.Caret(1), version: 9),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        var applied = true;
        var reset = true;
        var thread = new Thread(() =>
        {
            applied = store.ApplyAuthoritativeUpdate(snapshot, generation);
            reset = store.ResetAuthoritativeText("hacked");
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.False(applied);
        Assert.False(reset);
        // 文档未被跨线程修改。
        Assert.Equal("abcd", store.Document.Text);
        Assert.Equal(generation, store.NativeGeneration);
    }

    [Fact]
    public void R2错误dispatcher不导致跨线程COM调用()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        // 注入“错误”dispatcher：立即在另一个线程执行排队动作。
        var dispatcherThreads = new List<int>();
        store.UiDispatcher = action =>
        {
            var t = new Thread(() =>
            {
                lock (dispatcherThreads)
                {
                    dispatcherThreads.Add(Environment.CurrentManagedThreadId);
                }

                action();
            });
            t.Start();
            t.Join(TimeSpan.FromSeconds(5));
        };

        sink.OnGranted = granted => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 通知在 RequestLock 栈内不调度（安全阶段规则），因此不跨线程回调。
        Assert.Empty(sink.TextChanges);

        // 在所属线程显式 flush 仍能正确发送。
        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    // ---------------------------------------------------------------- R4：完整快照（不丢预编辑）

    [Fact]
    public void R4组合中同步使用显示镜像与组合内caret()
    {
        var (store, _) = CreateStore("abcd");
        using var _s = store;
        var generation = store.NativeGeneration;

        // 组合中：已提交 "abcd"，替换区 [1,3)，预编辑 "XY"，caret 在预编辑第 1 位。
        // 显示镜像 = "a" + "XY" + "d" = "aXYd"；原生 caret = 1 + 1 = 2。
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("abcd", TextSelection.Caret(4), version: 5),
            new TextRange(1, 3),
            preedit: "XY",
            preeditCaret: 1,
            segments: null,
            mirrorRevision: 3,
            nativeRevision: 3,
            composing: true);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        var (text, start, end, _) = store.ReadAuthoritativeDocument();
        // 关键：预编辑没有被丢弃。
        Assert.Equal("aXYd", text);
        Assert.Equal(2, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void R4非组合同步使用已提交文本并保留选区方向()
    {
        var (store, _) = CreateStore("ab");
        using var _s = store;
        var generation = store.NativeGeneration;

        // 反向选区（anchor 3、active 1）。
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("abcd", new TextSelection(3, 1), version: 6),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 4,
            nativeRevision: 4,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        var (text, start, end, _) = store.ReadAuthoritativeDocument();
        Assert.Equal("abcd", text);
        // 方向保留：anchor 在前（AcpStart=1），active 在后（AcpEnd=3）。
        Assert.Equal(1, start);
        Assert.Equal(3, end);
    }

    [Fact]
    public void R4零变化不重置不推进代数不重复通知()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;
        var generation = store.NativeGeneration;

        // 与当前原生视图完全一致（文本 "abcd"，选区 0,0）。
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("abcd", TextSelection.Caret(0), version: 1),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        // 零变化：代数不变、无通知。
        Assert.Equal(generation, store.NativeGeneration);
        Assert.Empty(sink.TextChanges);
        Assert.Equal(0, sink.SelectionChanges);
    }

    // ---------------------------------------------------------------- R5：应用更新受锁限制并通知

    [Fact]
    public void R5持读锁时权威更新被拒绝且文档不变()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;
        var generation = store.NativeGeneration;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("zzz", TextSelection.Caret(0), version: 9),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        sink.OnGranted = granted =>
        {
            // 持读锁期间：官方禁止应用改文档 → 明确拒绝。
            Assert.False(store.ApplyAuthoritativeUpdate(snapshot, generation));
            // 同一读锁内前后读取一致。
            Assert.Equal("abcd", store.Document.Text);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 锁外重试成功。
        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));
        Assert.Equal("zzz", store.Document.Text);
    }

    [Fact]
    public void R5持读锁时重置被拒绝()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        sink.OnGranted = granted => Assert.False(store.ResetAuthoritativeText("hacked"));

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        Assert.Equal("abcd", store.Document.Text);

        // 锁外成功。
        Assert.True(store.ResetAuthoritativeText("ok"));
        Assert.Equal("ok", store.Document.Text);
    }

    [Fact]
    public void R5权威更新产生按mask的文本与选区通知()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;
        var generation = store.NativeGeneration;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("xy", TextSelection.Caret(2), version: 3),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        // 文本与选区都变了：两条通知都发出（锁外）。
        Assert.Single(sink.TextChanges);
        Assert.Equal(1, sink.SelectionChanges);
        Assert.Equal(4, sink.TextChanges[0].Change.AcpOldEnd);
        Assert.Equal(2, sink.TextChanges[0].Change.AcpNewEnd);
    }

    [Fact]
    public void R5仅选区变化只发选区通知()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;
        var generation = store.NativeGeneration;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("abcd", TextSelection.Caret(2), version: 2),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        Assert.Empty(sink.TextChanges);
        Assert.Equal(1, sink.SelectionChanges);
    }

    // ---------------------------------------------------------------- 审计复现：通知内重入

    [Fact]
    public void Audit通知回调内权威更新被拒绝且不递归修改()
    {
        // 复现 artifacts/ime-work/p2b-audit：OnTextChange 内再次 ApplyAuthoritativeUpdate。
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        bool? nested = null;
        var nestedText = string.Empty;
        sink.OnTextChanged = () =>
        {
            // 只重入一次，避免测试自身无限递归。
            sink.OnTextChanged = null;
            var snapshot = TextInputAuthoritativeSnapshot.Create(
                new TextSnapshot("nested", TextSelection.Caret(0), version: 1),
                new TextRange(0, 0),
                preedit: null,
                preeditCaret: 0,
                segments: null,
                mirrorRevision: 1,
                nativeRevision: 1,
                composing: false);
            nested = store.ApplyAuthoritativeUpdate(snapshot, store.NativeGeneration);
            nestedText = store.Document.Text;
        };

        var outer = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("outer", TextSelection.Caret(2), version: 1),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(outer, store.NativeGeneration));

        // 通知内重入必须被拒绝，且文档仍是外层结果。
        Assert.False(nested, "通知回调内的权威更新必须被拒绝。");
        Assert.Equal("outer", store.Document.Text);
        Assert.Equal("outer", nestedText);
    }

    [Fact]
    public void Audit通知回调内重置被拒绝()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        bool? nestedReset = null;
        sink.OnTextChanged = () =>
        {
            sink.OnTextChanged = null;
            nestedReset = store.ResetAuthoritativeText("hacked");
        };

        var outer = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("outer", TextSelection.Caret(0), version: 1),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(outer, store.NativeGeneration));

        Assert.False(nestedReset, "通知回调内的重置必须被拒绝。");
        Assert.Equal("outer", store.Document.Text);
    }

    [Fact]
    public void Audit通知期间sink卸载不导致后续回调或陈旧选区通知()
    {
        var (store, sink) = CreateStore("abcd");
        using var _s = store;

        // 文本通知内卸载 sink：后续排队的选区通知不得再发往旧 sink。
        sink.OnTextChanged = () =>
        {
            sink.OnTextChanged = null;
            store.UnadviseSink(sink);
        };

        var generation = store.NativeGeneration;
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("xy", TextSelection.Caret(2), version: 3),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        // 文本通知已发出（1 次），但卸载后的选区通知被丢弃。
        Assert.Single(sink.TextChanges);
        Assert.Equal(0, sink.SelectionChanges);
        Assert.False(store.HasSink);

        // 后续 flush 也不得回调旧 sink。
        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void Audit通知期间sink替换后旧连接的尾部选区通知不泄漏给新sink()
    {
        // 复现 p2b-audit 第 12 项：文本通知内卸载旧 sink 并安装新 sink，
        // 后续的选区通知不得发给新连接。
        var (store, oldSink) = CreateStore("abcd");
        using var _s = store;

        var newSink = new RecordingSink();
        oldSink.OnTextChanged = () =>
        {
            oldSink.OnTextChanged = null;
            store.UnadviseSink(oldSink);
            store.AdviseSink(TsfIids.TextStoreAcpSink, newSink, TsfFlags.TS_AS_ALL_SINKS);
        };

        var generation = store.NativeGeneration;
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("new text", TextSelection.Caret(2), version: 3),
            new TextRange(2, 2),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        // 旧 sink 收到文本通知；新 sink 一条都不该收到（含尾部选区通知）。
        Assert.Single(oldSink.TextChanges);
        Assert.Empty(newSink.TextChanges);
        Assert.Equal(0, newSink.SelectionChanges);
    }

    // ---------------------------------------------------------------- 审计复现：反向选区零变化

    [Fact]
    public void Audit反向选区零变化不推进代数也不通知()
    {
        // 复现 p2b-audit：反向选区重复设置被误判为变化。
        var (store, sink) = CreateStore("hello");
        using var _s = store;

        // 反向选区：anchor 4、active 1 → 规范化 [1,4)，方向 backward。
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("hello", new TextSelection(4, 1), version: 1),
            new TextRange(1, 4),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, store.NativeGeneration));
        var generation = store.NativeGeneration;
        var textChanges = sink.TextChanges.Count;
        var selectionChanges = sink.SelectionChanges;

        // 同一反向快照再次应用：零变化。
        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));

        Assert.Equal(generation, store.NativeGeneration);
        Assert.Equal(textChanges, sink.TextChanges.Count);
        Assert.Equal(selectionChanges, sink.SelectionChanges);

        // 方向被保留。
        var (start, end) = store.Document.Selection;
        Assert.Equal(1, start);
        Assert.Equal(4, end);
        Assert.True(store.Document.IsSelectionBackward);
    }

    [Fact]
    public void Audit重复反向零变化多次应用均不推进()
    {
        var (store, _) = CreateStore("hello");
        using var _s = store;

        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("hello", new TextSelection(4, 1), version: 1),
            new TextRange(1, 4),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, store.NativeGeneration));
        var generation = store.NativeGeneration;

        for (var i = 0; i < 3; i++)
        {
            Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));
            Assert.Equal(generation, store.NativeGeneration);
        }

        Assert.True(store.Document.IsSelectionBackward);
    }

    [Fact]
    public void Audit正反方向切换被识别为真实变化()
    {
        var (store, sink) = CreateStore("hello");
        using var _s = store;

        // 正向 [1,4)。
        var forward = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("hello", new TextSelection(1, 4), version: 1),
            new TextRange(1, 4),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);
        Assert.True(store.ApplyAuthoritativeUpdate(forward, store.NativeGeneration));
        Assert.False(store.Document.IsSelectionBackward);
        var generation = store.NativeGeneration;
        var selectionChanges = sink.SelectionChanges;

        // 反向 [1,4)：范围相同但方向不同 → 必须视为真实变化。
        var backward = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("hello", new TextSelection(4, 1), version: 1),
            new TextRange(1, 4),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 2,
            nativeRevision: 2,
            composing: false);
        Assert.True(store.ApplyAuthoritativeUpdate(backward, generation));

        Assert.NotEqual(generation, store.NativeGeneration);
        Assert.True(sink.SelectionChanges > selectionChanges);
        Assert.True(store.Document.IsSelectionBackward);
    }

    [Fact]
    public void AuditACPGetSelection反向选区返回start小于等于end且TS_AE_START()
    {
        var (store, sink) = CreateStore("hello");
        using var _s = store;

        var backward = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("hello", new TextSelection(4, 1), version: 1),
            new TextRange(1, 4),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);
        Assert.True(store.ApplyAuthoritativeUpdate(backward, store.NativeGeneration));

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TsSelectionAcp>());
        try
        {
            sink.OnGranted = granted =>
            {
                Assert.Equal(TsfHResult.S_OK, store.GetSelection(0, 1, buffer, out var fetched));
                Assert.Equal(1u, fetched);
                var selection = Marshal.PtrToStructure<TsSelectionAcp>(buffer);

                // ACP 契约：AcpStart <= AcpEnd，方向由 style 表达。
                Assert.True(selection.AcpStart <= selection.AcpEnd);
                Assert.Equal(1, selection.AcpStart);
                Assert.Equal(4, selection.AcpEnd);
                Assert.Equal(TsfFlags.TS_AE_START, selection.Style.ActiveSelEnd);
            };

            Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------- 审计复现：内联 dispatcher

    [Fact]
    public void Audit内联dispatcher不在RequestLock栈内发送通知()
    {
        // 复现 p2b-audit：UiDispatcher = action => action() 会同步内联执行。
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        store.UiDispatcher = action => action();

        var requestActive = true;
        var notifiedInRequest = false;
        sink.OnTextChanged = () => notifiedInRequest |= requestActive;
        sink.OnGranted = granted => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));
        requestActive = false;

        // 关键：内联调度发生在 RequestLock 栈内，必须被拒绝。
        Assert.False(notifiedInRequest, "通知不得在 RequestLock 栈内发出。");
        Assert.Empty(sink.TextChanges);

        // 安全阶段显式 Flush 仍能发出（工作未丢）。
        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void Audit内联dispatcher不无限递归且保留pending()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var dispatchCalls = 0;
        store.UiDispatcher = action =>
        {
            // 防御：若实现错误地递归调度，这里会快速失败而不是挂死。
            if (++dispatchCalls > 100)
            {
                throw new InvalidOperationException("dispatcher recursion detected");
            }

            action();
        };

        sink.OnGranted = granted => store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 没有递归爆炸，通知也未发出，pending 保留。
        Assert.True(dispatchCalls <= 100);
        Assert.Empty(sink.TextChanges);

        store.FlushDeferredNotifications();
        Assert.Single(sink.TextChanges);
    }

    [Fact]
    public void Audit内联continuation中的权威更新不绕过保护()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        // 内联 dispatcher：continuation 也会同步执行。
        store.UiDispatcher = action => action();

        bool? appliedInGrant = null;
        var generation = store.NativeGeneration;
        var snapshot = TextInputAuthoritativeSnapshot.Create(
            new TextSnapshot("zzz", TextSelection.Caret(0), version: 1),
            new TextRange(0, 0),
            preedit: null,
            preeditCaret: 0,
            segments: null,
            mirrorRevision: 1,
            nativeRevision: 1,
            composing: false);

        sink.OnGranted = granted =>
        {
            // 授予内（RequestLock 栈内）直接更新：必须被拒绝。
            appliedInGrant = store.ApplyAuthoritativeUpdate(snapshot, generation);
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        Assert.False(appliedInGrant, "RequestLock 栈内的权威更新必须被拒绝。");
        Assert.Equal("ab", store.Document.Text);

        // 安全阶段重试成功。
        Assert.True(store.ApplyAuthoritativeUpdate(snapshot, generation));
        Assert.Equal("zzz", store.Document.Text);
    }

    [Fact]
    public void Audit安全阶段后显式Flush与Continue均可工作()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        store.UiDispatcher = action => action();

        var depth = 0;
        sink.OnGranted = granted =>
        {
            store.NotifyTextChange(0, new AcpTextChange(0, 0, 1, 1));
            if (depth++ < 2)
            {
                store.RequestLock(TsfFlags.TS_LF_READ, out _);
            }
        };

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _));

        // 安全阶段：显式 Flush 与 Continue 都能推进。
        store.FlushDeferredNotifications();
        store.ContinuePendingLocks();

        Assert.True(sink.TextChanges.Count >= 1);
        Assert.Equal(0, store.RequestDepth);
    }

    // ---------------------------------------------------------------- 审计复现：绕过 Guard 的入口

    [Fact]
    public void Audit绕过Guard的入口也有线程保护且输出已初始化()
    {
        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("abcd");
        var hwnd = new IntPtr(0x7777);
        Assert.True(store.AttachWindow(hwnd));

        var results = new List<int>();
        var statuses = new List<TsStatus>();
        var views = new List<uint>();
        var windows = new List<IntPtr>();
        var attach = true;

        var thread = new Thread(() =>
        {
            results.Add(store.GetStatus(out var status));
            statuses.Add(status);
            results.Add(store.GetActiveView(out var view));
            views.Add(view);
            results.Add(store.GetWnd(store.ViewCookie, out var wnd));
            windows.Add(wnd);
            attach = store.AttachWindow(new IntPtr(0x8888));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        // 全部显式失败。
        Assert.All(results, hr => Assert.Equal(TsfHResult.E_FAIL, hr));
        Assert.False(attach);

        // 输出已初始化。
        Assert.All(statuses, s => Assert.Equal(0u, s.DynamicFlags));
        Assert.All(views, v => Assert.Equal(0u, v));
        Assert.All(windows, w => Assert.Equal(IntPtr.Zero, w));

        // 原线程的句柄未被跨线程覆盖。
        Assert.Equal(TsfHResult.S_OK, store.GetWnd(store.ViewCookie, out var actual));
        Assert.Equal(hwnd, actual);
    }

    [Fact]
    public void Audit错误线程Dispose后拥有线程仍能正确释放()
    {
        var store = new AcpTextStore();
        store.ResetAuthoritativeText("ab");

        // 跨线程 Dispose：不得破坏拥有线程的释放能力。
        var thread = new Thread(store.Dispose);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        // 拥有线程仍可正常释放（幂等且不抛）。
        store.Dispose();
        Assert.True(store.IsDisposed);
    }

    // ---------------------------------------------------------------- 释放

    [Fact]
    public void 释放后所有入口显式失败且不再回调()
    {
        var (store, sink) = CreateStore("ab");
        store.Dispose();

        Assert.True(store.IsDisposed);
        Assert.False(store.HasSink);
        Assert.Equal(TsfHResult.E_FAIL, store.GetStatus(out _));
        Assert.Equal(TsfHResult.E_FAIL, store.GetEndACP(out _));
        Assert.Equal(TsfHResult.E_FAIL, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));

        // 重复释放幂等。
        store.Dispose();
    }

    [Fact]
    public void 释放后陈旧暂存失效()
    {
        var (store, sink) = CreateStore("ab");
        sink.OnGranted = granted => store.SetText(0, 0, 0, IntPtr.Zero, 0, out _);
        Assert.Equal(TsfHResult.S_OK, store.RequestLock(TsfFlags.TS_LF_READWRITE | TsfFlags.TS_LF_SYNC, out _));

        store.Dispose();

        Assert.Empty(store.Staging.DrainForGeneration(out _));
    }

    // ---------------------------------------------------------------- 真实 CCW / vtable

    [Fact]
    public void 真实CCW的ITextStoreACP查询接口与vtable可解析()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows：没有 COM，无 CCW 可测。
        }

        using var store = new AcpTextStore();
        store.ResetAuthoritativeText("abcd");

        // 真实 CCW：CLR 为托管对象生成 COM 可调用包装。
        var unknown = Marshal.GetIUnknownForObject(store);
        try
        {
            Assert.NotEqual(IntPtr.Zero, unknown);

            // QI 到 ITextStoreACP：必须成功并给出可解析的接口指针。
            var iid = TsfIids.TextStoreAcp;
            var hr = Marshal.QueryInterface(unknown, in iid, out var textStore);
            try
            {
                Assert.Equal(0, hr);
                Assert.NotEqual(IntPtr.Zero, textStore);

                // vtable：IUnknown 占 0-2，ITextStoreACP 方法从 slot 3 起。
                // 逐个确认前若干槽位都是非空可执行地址（不调用，只验证布局可解析）。
                var vtable = Marshal.ReadIntPtr(textStore);
                Assert.NotEqual(IntPtr.Zero, vtable);
                for (var slot = 0; slot < 3 + 24; slot++)
                {
                    var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
                    Assert.NotEqual(IntPtr.Zero, fn);
                }
            }
            finally
            {
                if (textStore != IntPtr.Zero)
                {
                    Marshal.Release(textStore);
                }
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void 真实CCW未实现ITextStoreACPSink时QI失败()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = new AcpTextStore();
        var unknown = Marshal.GetIUnknownForObject(store);
        try
        {
            // store 不是 sink：QI 到 sink IID 必须失败（E_NOINTERFACE）。
            var sinkIid = TsfIids.TextStoreAcpSink;
            var hr = Marshal.QueryInterface(unknown, in sinkIid, out var sinkPtr);
            Assert.NotEqual(0, hr);
            Assert.Equal(IntPtr.Zero, sinkPtr);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void 真实COM身份比较区分不同sink实例()
    {
        var (store, sink) = CreateStore("ab");
        using var _s = store;

        var other = new RecordingSink();

        // 身份不匹配：不卸载。
        Assert.Equal(TsfHResult.E_INVALIDARG, store.UnadviseSink(other));
        Assert.True(store.HasSink);

        // 身份匹配：正常卸载。
        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));
        Assert.False(store.HasSink);
    }
}
