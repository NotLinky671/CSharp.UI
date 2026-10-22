using System.Runtime.InteropServices;
using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3b：独立 UIElement sink 生命周期测试（不接宿主、不激活 TSF）。
/// </summary>
public sealed class TsfUiElementSinkTests
{
    private sealed class FakeSource : ITfSource
    {
        public int AdviseHr { get; set; }
        public uint Cookie { get; set; } = 7;
        public int UnadviseHr { get; set; }
        public int AdviseCount { get; private set; }
        public int UnadviseCount { get; private set; }
        public object? LastSink { get; private set; }
        public Guid LastIid { get; private set; }
        public uint LastCookie { get; private set; }

        public int AdviseSink(Guid riid, object punk, out uint pdwCookie)
        {
            AdviseCount++;
            LastIid = riid;
            LastSink = punk;
            pdwCookie = Cookie;
            return AdviseHr;
        }

        public int UnadviseSink(uint dwCookie)
        {
            UnadviseCount++;
            LastCookie = dwCookie;
            return UnadviseHr;
        }
    }

    private sealed class FakeElement : ITfUIElement
    {
        public int GetDescription(out string pbstrDescription)
        {
            pbstrDescription = "fake";
            return 0;
        }

        public int GetGUID(out Guid pguid)
        {
            pguid = Guid.Empty;
            return 0;
        }

        public int Show(int bShow) => 0;

        public int IsShown(out int pbShow)
        {
            pbShow = 0;
            return 0;
        }
    }

    private sealed class FakeCandidate : ITfUIElement, ITfCandidateListUIElement
    {
        public uint Count { get; set; } = 3;
        public int CountHr { get; set; }
        public uint Selection { get; set; } = 1;
        public int SelectionHr { get; set; }
        public uint CurrentPage { get; set; }
        public int CurrentPageHr { get; set; }
        public uint[] Pages { get; set; } = [0];
        public int PageCountHr { get; set; }
        public int PageTableHr { get; set; }
        public uint? FilledOverride { get; set; }
        public Func<uint, int> StringHr { get; set; } = _ => 0;
        public Action<uint>? OnGetString { get; set; }

        public int GetCountCalls { get; private set; }
        public int GetSelectionCalls { get; private set; }
        public int GetPageIndexCalls { get; private set; }
        public int GetStringCalls { get; private set; }

        public int GetDescription(out string pbstrDescription)
        {
            pbstrDescription = "candidate";
            return 0;
        }

        public int GetGUID(out Guid pguid)
        {
            pguid = Guid.Empty;
            return 0;
        }

        public int Show(int bShow) => 0;

        public int IsShown(out int pbShow)
        {
            pbShow = 1;
            return 0;
        }

        public int GetUpdatedFlags(out uint pdwFlags)
        {
            pdwFlags = 0;
            return 0;
        }

        public int GetDocumentMgr(out ITfDocumentMgr ppdim)
        {
            ppdim = null!;
            return 0;
        }

        public int GetCount(out uint puCount)
        {
            GetCountCalls++;
            puCount = Count;
            return CountHr;
        }

        public int GetSelection(out uint puIndex)
        {
            GetSelectionCalls++;
            puIndex = Selection;
            return SelectionHr;
        }

        public int GetString(uint uIndex, out string pstr)
        {
            GetStringCalls++;
            pstr = $"c{uIndex}";
            OnGetString?.Invoke(uIndex);
            return StringHr(uIndex);
        }

        public int GetPageIndex(IntPtr pIndex, uint uSize, out uint puPageCnt)
        {
            GetPageIndexCalls++;
            if (uSize == 0)
            {
                puPageCnt = (uint)Pages.Length;
                return PageCountHr;
            }

            puPageCnt = FilledOverride ?? (uint)Pages.Length;
            for (var i = 0; i < Pages.Length && i < uSize; i++)
            {
                Marshal.WriteInt32(pIndex, i * sizeof(uint), (int)Pages[i]);
            }

            return PageTableHr;
        }

        public int SetPageIndex(IntPtr pIndex, uint uPageCnt) => 0;

        public int GetCurrentPage(out uint puPage)
        {
            puPage = CurrentPage;
            return CurrentPageHr;
        }
    }

    private sealed class FakeManager : ITfUIElementMgr
    {
        public Dictionary<uint, ITfUIElement?> Elements { get; } = [];
        public int GetElementHr { get; set; }
        public Action<uint>? OnGetUIElement { get; set; }
        public int GetElementCalls { get; private set; }

        public int GetUIElement(uint dwUIElementId, out ITfUIElement ppElement)
        {
            GetElementCalls++;
            OnGetUIElement?.Invoke(dwUIElementId);
            Elements.TryGetValue(dwUIElementId, out var element);
            ppElement = element!;
            return GetElementHr;
        }

        public int BeginUIElement(ITfUIElement pElement, ref int pbShow, out uint pdwUIElementId)
        {
            pdwUIElementId = 0;
            return 0;
        }

        public int UpdateUIElement(uint dwUIElementId) => 0;

        public int EndUIElement(uint dwUIElementId) => 0;

        public int EnumUIElements(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return 0;
        }
    }

    private sealed class Harness
    {
        internal FakeManager Manager { get; } = new();
        internal FakeSource Source { get; } = new();
        internal List<CandidateSnapshot?> Published { get; } = [];
        internal int Redraws { get; private set; }
        internal int Releases { get; private set; }
        internal bool CurrentOwner { get; set; } = true;
        internal TsfUiElementSink Sink { get; }

        internal Harness(Action<object>? onRelease = null)
        {
            Sink = new TsfUiElementSink(
                Manager, Source, epoch: 3,
                () => CurrentOwner,
                snapshot => Published.Add(snapshot),
                () => Redraws++,
                element =>
                {
                    Releases++;
                    onRelease?.Invoke(element);
                });
        }
    }

    private static Harness Started(out FakeCandidate candidate)
    {
        var harness = new Harness();
        candidate = new FakeCandidate();
        harness.Manager.Elements[0] = candidate;
        Assert.Equal(0, harness.Sink.Advise());
        return harness;
    }

    private static void Begin(TsfUiElementSink sink, uint id = 0)
    {
        var show = 1;
        Assert.Equal(0, sink.BeginUIElement(id, ref show));
        Assert.Equal(0, show);
    }

    [Fact]
    public void Advise_CookieZero_IsValid()
    {
        var harness = new Harness();
        harness.Source.Cookie = 0;

        Assert.Equal(0, harness.Sink.Advise());
        Assert.True(harness.Sink.IsAdvised);
        Assert.Equal(0u, harness.Sink.Cookie);
        Assert.Equal(0, harness.Sink.Unadvise());
        Assert.False(harness.Sink.IsAdvised);
        Assert.Equal(0u, harness.Source.LastCookie);
    }

    [Fact]
    public void Advise_InvalidCookie_FailsWithoutUnadvise()
    {
        var harness = new Harness();
        harness.Source.Cookie = TsfFlags.TF_INVALID_COOKIE;

        Assert.True(harness.Sink.Advise() < 0);
        Assert.False(harness.Sink.IsAdvised);
        Assert.Equal(0, harness.Sink.Unadvise());
        Assert.Equal(0, harness.Source.UnadviseCount);
    }

    [Fact]
    public void Unadvise_InvalidArgument_ClearsAdvisedFlag()
    {
        var harness = new Harness();
        harness.Source.UnadviseHr = TsfHResult.E_INVALIDARG;
        Assert.Equal(0, harness.Sink.Advise());

        Assert.Equal(0, harness.Sink.Unadvise());
        Assert.False(harness.Sink.IsAdvised);
        Assert.Equal(1, harness.Source.UnadviseCount);
    }

    [Fact]
    public void Begin_Candidate_SuppressesWithoutReadingContent()
    {
        var harness = Started(out var candidate);

        Begin(harness.Sink);

        Assert.True(harness.Sink.IsAdvised);
        Assert.Equal(1, harness.Sink.LiveEntryCount);
        Assert.Equal(0u, harness.Sink.CurrentElementId);
        Assert.Equal(0, candidate.GetCountCalls);
        Assert.Equal(0, candidate.GetSelectionCalls);
        Assert.Equal(0, candidate.GetPageIndexCalls);
        Assert.Equal(0, candidate.GetStringCalls);
        Assert.Empty(harness.Published);
    }

    [Fact]
    public void Begin_NonCandidate_LeavesShowAndReleases()
    {
        var harness = new Harness();
        var element = new FakeElement();
        harness.Manager.Elements[5] = element;
        Assert.Equal(0, harness.Sink.Advise());

        var show = 1;
        harness.Sink.BeginUIElement(5, ref show);

        Assert.Equal(1, show);
        Assert.Equal(0, harness.Sink.LiveEntryCount);
        Assert.Equal(1, harness.Releases);
    }

    [Fact]
    public void RepeatedBegin_FailingGetElement_StillClearsOldSnapshotFirst()
    {
        var harness = Started(out _);
        Begin(harness.Sink);
        harness.Sink.UpdateUIElement(0);
        Assert.NotNull(harness.Sink.LastPublished);
        var updatesBefore = harness.Sink.UpdateCount;

        harness.Manager.GetElementHr = TsfHResult.E_FAIL;
        var show = 1;
        harness.Sink.BeginUIElement(0, ref show);

        Assert.Equal(0, harness.Sink.LiveEntryCount);
        Assert.Null(harness.Sink.LastPublished);
        Assert.Equal(updatesBefore, harness.Sink.UpdateCount);
        Assert.Equal(1, harness.Releases); // 旧元素已退休释放
    }

    [Fact]
    public void Begin_ReentrantEnd_DoesNotRegisterOrRevive()
    {
        var harness = Started(out _);
        var sink = harness.Sink;
        harness.Manager.OnGetUIElement = id =>
        {
            harness.Manager.OnGetUIElement = null;
            sink.EndUIElement(id);
        };

        var show = 1;
        sink.BeginUIElement(0, ref show);

        Assert.Equal(0, sink.LiveEntryCount);
        Assert.Null(sink.LastPublished);
        Assert.Equal(1, harness.Releases);
    }

    [Fact]
    public void Begin_ReentrantSameId_OuterAttemptDoesNotOverwrite()
    {
        var harness = Started(out _);
        var sink = harness.Sink;
        var nested = false;
        harness.Manager.OnGetUIElement = id =>
        {
            if (nested)
            {
                return;
            }

            nested = true;
            harness.Manager.OnGetUIElement = null;
            var show = 1;
            sink.BeginUIElement(id, ref show);
        };

        var outerShow = 1;
        sink.BeginUIElement(0, ref outerShow);

        Assert.Equal(1, sink.LiveEntryCount); // 内层 Begin 的 Entry
        Assert.Equal(1, harness.Releases);    // 外层取得的 RCW 被释放
    }

    [Fact]
    public void Update_PublishesBoundedSnapshot_AndDeduplicates()
    {
        var harness = Started(out _);
        Begin(harness.Sink);

        harness.Sink.UpdateUIElement(0);
        var first = Assert.Single(harness.Published);
        Assert.NotNull(first);
        Assert.True(first!.Valid);
        Assert.Equal(3, first.Entries.Count);
        Assert.Equal("c0", first.Entries[0].Text);
        Assert.Equal(1, first.SelectionOnPage);
        Assert.Equal(0u, harness.Sink.CurrentElementId);
        Assert.Equal(1, harness.Redraws);

        harness.Sink.UpdateUIElement(0);
        Assert.Single(harness.Published);
        Assert.Equal(1, harness.Redraws);
    }

    [Fact]
    public void Update_NestedNewerReadWins_OuterStaleReadDoesNotOverwrite()
    {
        var harness = Started(out var candidate);
        var sink = harness.Sink;
        var nested = false;
        candidate.Selection = 1;
        candidate.OnGetString = _ =>
        {
            if (nested)
            {
                return;
            }

            nested = true;
            candidate.Selection = 2;
            sink.UpdateUIElement(0); // 外层仍持有 selection=1 的读取结果
        };
        Begin(sink);

        sink.UpdateUIElement(0);

        var snapshot = Assert.Single(harness.Published);
        Assert.NotNull(snapshot);
        Assert.Equal(2u, snapshot!.SelectionIndex); // 较新的内层结果保留
        Assert.Equal(2, snapshot.SelectionOnPage);
    }

    [Fact]
    public void Clear_ReentrantBeginDuringRelease_StaysBlockedUntilOuterClearFinishes()
    {
        TsfUiElementSink? sink = null;
        var reentered = false;
        var harness = new Harness(_ =>
        {
            if (reentered || sink is null)
            {
                return;
            }

            reentered = true;
            var show = 1;
            sink.BeginUIElement(0, ref show); // 外层 Clear 的 RCW 释放期间尝试注册
            Assert.Equal(1, show);            // 不得抑制系统候选
            sink.Clear();                     // 嵌套 Clear 不得复位外层 _clearing
        });
        harness.Manager.Elements[0] = new FakeCandidate();
        sink = harness.Sink;
        Assert.Equal(0, sink.Advise());
        Begin(sink);

        sink.Clear();

        Assert.True(reentered);
        Assert.Equal(0, sink.LiveEntryCount);
        Assert.Null(sink.LastPublished);
    }

    [Fact]
    public void End_UnknownId_IsNoOp()
    {
        var harness = Started(out _);

        Assert.Equal(0, harness.Sink.EndUIElement(42));
        Assert.Equal(0, harness.Sink.LiveEntryCount);
        Assert.Equal(0, harness.Releases);
    }

    [Fact]
    public void Update_ReadFailure_PublishesInvalidAndHides()
    {
        var harness = Started(out var candidate);
        candidate.CountHr = TsfHResult.E_FAIL;
        Begin(harness.Sink);

        harness.Sink.UpdateUIElement(0);

        var snapshot = Assert.Single(harness.Published);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Valid);
        Assert.Empty(snapshot.Entries);
        Assert.NotNull(snapshot.Failure);
    }

    [Fact]
    public void Update_SynchronousEndDuringRead_AbortsAndReleasesOnce()
    {
        var harness = Started(out var candidate);
        var sink = harness.Sink;
        candidate.OnGetString = _ =>
        {
            candidate.OnGetString = null;
            sink.EndUIElement(0);
        };
        Begin(sink);

        sink.UpdateUIElement(0);

        Assert.Empty(harness.Published); // 尚未发布，End 后也不得发布
        Assert.Equal(0, sink.LiveEntryCount);
        Assert.Equal(1, harness.Releases);
    }

    [Fact]
    public void End_CurrentClears_NonCurrentDoesNot()
    {
        var harness = Started(out var candidate);
        var second = new FakeCandidate();
        harness.Manager.Elements[1] = candidate;
        harness.Manager.Elements[2] = second;
        var sink = harness.Sink;

        Begin(sink, 1);
        sink.UpdateUIElement(1);
        Assert.Equal(1u, sink.CurrentElementId);
        Assert.NotNull(sink.LastPublished);

        Begin(sink, 2);
        sink.UpdateUIElement(2);
        var secondSnapshot = sink.LastPublished;
        Assert.NotNull(secondSnapshot);
        Assert.Equal(2u, sink.CurrentElementId);

        sink.EndUIElement(1);
        Assert.Same(secondSnapshot, sink.LastPublished);
        Assert.Equal(2u, sink.CurrentElementId);

        sink.EndUIElement(2);
        Assert.Null(sink.LastPublished);
        Assert.Equal(0u, sink.CurrentElementId);
    }

    [Fact]
    public void Clear_HidesAndReleasesAll()
    {
        var harness = Started(out _);
        Begin(harness.Sink);
        harness.Sink.UpdateUIElement(0);

        harness.Sink.Clear();

        Assert.Null(harness.Sink.LastPublished);
        Assert.Equal(0, harness.Sink.LiveEntryCount);
        Assert.Equal(1, harness.Releases);
    }

    [Fact]
    public void EpochOrOwnerInvalid_BlocksComAndClearHides()
    {
        var harness = Started(out _);
        Begin(harness.Sink);
        harness.Sink.UpdateUIElement(0);
        var getCalls = harness.Manager.GetElementCalls;
        var updateCalls = harness.Manager.Elements[0] is FakeCandidate candidate
            ? candidate.GetCountCalls : 0;

        harness.CurrentOwner = false;
        harness.Sink.UpdateUIElement(0);

        Assert.Equal(getCalls, harness.Manager.GetElementCalls);
        Assert.Equal(updateCalls,
            ((FakeCandidate)harness.Manager.Elements[0]!).GetCountCalls);
        Assert.NotNull(harness.Sink.LastPublished); // 尚未 Clear；由会话触发

        harness.Sink.Clear();
        Assert.Null(harness.Sink.LastPublished);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndReleasesOnce()
    {
        var harness = Started(out _);
        Begin(harness.Sink);

        harness.Sink.Dispose();
        harness.Sink.Dispose();

        Assert.Equal(0, harness.Sink.LiveEntryCount);
        Assert.Equal(1, harness.Releases);
        Assert.Null(harness.Sink.LastPublished);
    }
}
