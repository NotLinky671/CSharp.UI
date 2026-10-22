using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R27：`native-ui-lifecycle` 依赖的 observer 契约。
/// <list type="bullet">
/// <item>`TryShowCandidates` 必须**只**作用于候选元素，绝不触碰语言栏等非候选；</item>
/// <item>`LiveCandidates`/`LiveElementCount` 反映**存活**元素，End 后即移除；</item>
/// <item>`IsLive` 在 End 之后为 false（End 后的 RCW 不得再使用）；</item>
/// <item>Show 结果保留 HRESULT 与 IsShown 前后值；</item>
/// <item>Dispose 幂等，且不会对同一元素重复释放。</item>
/// </list>
/// 全部纯托管，不启动 COM/TSF。
/// </summary>
public sealed class NativeUiLifecycleObserverTests
{
    private static (TsfUiElementObserver Observer, FakeUiElementManager Manager, List<Entry> Entries)
        NewObserver(params (uint Id, ITfUIElement Element)[] elements)
    {
        var entries = new List<Entry>();
        var map = elements.ToDictionary(e => e.Id, e => e.Element);
        var manager = new FakeUiElementManager(map);
        var observer = new TsfUiElementObserver(manager, (stage, kind, fields) =>
            entries.Add(new Entry(stage, kind,
                fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields))));
        return (observer, manager, entries);
    }

    private sealed record Entry(string Stage, string Kind, Dictionary<string, object?> Fields);

    [Fact]
    public void TryShowCandidatesSkipsNonCandidateElements()
    {
        var candidate = new FakeCandidateElement();
        var languageBar = new FakeElement();
        var (observer, _, entries) = NewObserver((1, candidate), (2, languageBar));

        var show1 = 1;
        observer.BeginUIElement(1, ref show1);
        var show2 = 1;
        observer.BeginUIElement(2, ref show2);

        var results = observer.TryShowCandidates(show: true);

        // 只对候选元素调用；语言栏完全不被触碰。
        Assert.Single(results);
        Assert.Equal(1u, results[0].Id);
        Assert.True(results[0].IsCandidateList);
        Assert.Equal(0, languageBar.ShowCalls);
        Assert.Equal(1, candidate.ShowCalls);
        Assert.Contains(entries, e => e.Kind == "show-probe"
            && (uint)e.Fields["id"]! == 1u);
        Assert.DoesNotContain(entries, e => e.Kind == "show-probe"
            && (uint)e.Fields["id"]! == 2u);
    }

    [Fact]
    public void LiveCandidatesOnlyCountsSurvivingCandidates()
    {
        var candidate = new FakeCandidateElement();
        var other = new FakeCandidateElement();
        var (observer, _, _) = NewObserver((1, candidate), (2, other));

        Assert.Empty(observer.LiveCandidates());
        Assert.Equal(0, observer.LiveElementCount);

        var s1 = 1;
        observer.BeginUIElement(1, ref s1);
        var s2 = 1;
        observer.BeginUIElement(2, ref s2);
        Assert.Equal(2, observer.LiveCandidates().Count);
        Assert.Equal(2, observer.LiveElementCount);

        // End 之后从存活集合移除（Begin 累计计数仍保留）。
        observer.EndUIElement(1);
        Assert.Single(observer.LiveCandidates());
        // 被移除的是 id=1，存活的是 id=2。
        Assert.Equal(2u, observer.LiveCandidates()[0].Id);
        Assert.Equal(1, observer.LiveElementCount);
        Assert.Equal(2, observer.BeginCount);
        Assert.Equal(1, observer.EndCount);
    }

    [Fact]
    public void IsLiveIsFalseAfterEnd()
    {
        var candidate = new FakeCandidateElement();
        var (observer, _, _) = NewObserver((7, candidate));

        var show = 1;
        observer.BeginUIElement(7, ref show);
        var live = Assert.Single(observer.LiveCandidates());
        Assert.True(observer.IsLive(live));

        observer.EndUIElement(7);

        // End 后的元素不得再被当作存活；调用方据此停止，不调用 Show。
        Assert.False(observer.IsLive(live));
        Assert.Empty(observer.LiveCandidates());
        Assert.Equal(0, candidate.ShowCalls);
    }

    [Fact]
    public void ShowResultCarriesHResultAndShownTransitions()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK };
        var (observer, _, _) = NewObserver((3, candidate));

        var show = 1;
        observer.BeginUIElement(3, ref show);
        var results = observer.TryShowCandidates(show: false);

        var result = Assert.Single(results);
        Assert.Equal(TsfHResult.S_OK, result.HResult);
        Assert.False(result.ShownBefore);
        Assert.False(result.ShownAfter);
        Assert.Equal(1, candidate.ShowCalls);
    }

    [Fact]
    public void FailedShowIsReportedAsFailure()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.E_FAIL };
        var (observer, _, _) = NewObserver((4, candidate));

        var show = 1;
        observer.BeginUIElement(4, ref show);
        var result = Assert.Single(observer.TryShowCandidates(show: true));

        Assert.Equal(TsfHResult.E_FAIL, result.HResult);
        // 失败不得被包装成成功。
        Assert.True(result.HResult < 0);
    }

    [Fact]
    public void DisposeIsIdempotentAndDoesNotDoubleRelease()
    {
        var candidate = new FakeCandidateElement();
        var (observer, _, _) = NewObserver((5, candidate));

        var show = 1;
        observer.BeginUIElement(5, ref show);
        observer.Dispose();
        observer.Dispose();

        Assert.Empty(observer.LiveCandidates());
        Assert.Equal(0, observer.LiveElementCount);
    }

    [Fact]
    public void SuppressOnlyAffectsCandidatesAndCanBeRestored()
    {
        var candidate = new FakeCandidateElement();
        var other = new FakeElement();
        var (observer, _, _) = NewObserver((1, candidate), (2, other));

        // 原值 false → 设为 true → 恢复 false，验证场景 finally 的语义。
        var previous = observer.SuppressCandidates;
        Assert.False(previous);
        observer.SuppressCandidates = true;

        var showCandidate = 1;
        observer.BeginUIElement(1, ref showCandidate);
        var showOther = 1;
        observer.BeginUIElement(2, ref showOther);

        Assert.Equal(0, showCandidate); // 候选被抑制
        Assert.Equal(1, showOther);     // 非候选仍显示

        observer.SuppressCandidates = previous;
        Assert.False(observer.SuppressCandidates);
    }

    // ---- R27 同步重入：End 在原生 Show 内部回调（同一 UI 线程）----

    /// <summary>同步 End 期间借用的 RCW：IsShown-after 必须跳过（显式 null）、hr 保留。</summary>
    [Fact]
    public void SynchronousEndDuringShowSkipsAfterIsShownAndPreservesHr()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK };
        var (observer, _, _) = NewObserver((11, candidate));
        var show = 1;
        observer.BeginUIElement(11, ref show);

        // Show 内部同步回调 End（重入同一 UI 线程，模拟 msctf）。
        candidate.OnShow = () => observer.EndUIElement(11);

        var result = Assert.Single(observer.TryShowCandidates(show: true));

        Assert.Equal(TsfHResult.S_OK, result.HResult);
        Assert.True(result.ShowHrAvailable);
        Assert.True(result.EndedDuringShow);
        Assert.False(result.ShownBefore);
        Assert.Null(result.ShownAfter);              // End 后不得再调用 IsShown
        Assert.Equal(TsfHResult.S_OK, result.IsShownBeforeHr);
        Assert.Null(result.IsShownAfterHr);          // 显式 null，非假 true/false
        Assert.Equal(1, candidate.IsShownCalls);     // 只调用了 before 那次
        Assert.Empty(observer.LiveCandidates());     // 已从存活集合移除
    }

    /// <summary>End 之后禁止新的 Show/IsShown/Refresh：TryShow 立即返回且不再触 COM。</summary>
    [Fact]
    public void TryShowAfterEndDoesNotTouchCom()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK };
        var (observer, _, _) = NewObserver((12, candidate));
        var show = 1;
        observer.BeginUIElement(12, ref show);
        var live = Assert.Single(observer.LiveCandidates());

        observer.EndUIElement(12);

        var showCallsBefore = candidate.ShowCalls;
        var isShownCallsBefore = candidate.IsShownCalls;
        var result = live.TryShow(true);

        Assert.False(result.ShowHrAvailable);       // 原生 HRESULT 不可用（已 End）
        Assert.False(result.EndedDuringShow);        // 本次调用前已 End，不算 during
        Assert.Equal(showCallsBefore, candidate.ShowCalls);
        Assert.Equal(isShownCallsBefore, candidate.IsShownCalls);
        Assert.True(live.Ended);
    }

    /// <summary>同步 End + 借用：释放延迟到借用退出 finally，且恰好一次（不就地重复释放）。</summary>
    [Fact]
    public void SynchronousEndDuringShowDefersSingleRelease()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK };
        var (observer, _, _) = NewObserver((13, candidate));
        var show = 1;
        observer.BeginUIElement(13, ref show);
        var live = Assert.Single(observer.LiveCandidates());

        candidate.OnShow = () => observer.EndUIElement(13);
        var result = Assert.Single(observer.TryShowCandidates(show: true));

        Assert.True(result.EndedDuringShow);
        Assert.True(live.Ended);

        // Dispose/MarkEnded 幂等：不再重复释放（managed fake 无 RCW，验证不抛、状态不变）。
        observer.Dispose();
        observer.Dispose();
        live.Dispose();
        Assert.True(live.Ended);
    }

    /// <summary>
    /// Refresh 同步 End：GetDescription 期间 End 后，不再发起 GetGUID/候选调用；
    /// 借用期间不就地释放、退出时恰好一次释放；重复 Dispose 不再触 COM。
    /// </summary>
    [Fact]
    public void SynchronousEndDuringRefreshStopsSubsequentComCalls()
    {
        var candidate = new FakeCandidateElement();
        var (observer, _, _) = NewObserver((18, candidate));
        var show = 1;
        observer.BeginUIElement(18, ref show);
        var live = Assert.Single(observer.LiveCandidates());

        // 正常构造已 Refresh 一次：GetDescription=1, GetGUID=1（无中途 End）。
        Assert.Equal(1, candidate.GetDescriptionCalls);
        Assert.Equal(1, candidate.GetGUIDCalls);
        Assert.Equal(1, candidate.GetCountCalls);

        // 第二次 Refresh：GetDescription 内部同步 End → 应立即停止后续 COM。
        candidate.OnGetDescription = () => live.MarkEnded();
        live.Refresh();

        Assert.Equal(2, candidate.GetDescriptionCalls);
        Assert.Equal(1, candidate.GetGUIDCalls);      // End 后未再调用
        Assert.Equal(1, candidate.GetCountCalls);     // 候选查询也未再调用
        Assert.True(live.Ended);

        // 再次 Refresh/Dispose：幂等，不再发起任何新 COM 调用。
        live.Refresh();
        live.Dispose();
        live.Dispose();
        observer.Dispose();
        Assert.Equal(2, candidate.GetDescriptionCalls);
        Assert.Equal(1, candidate.GetGUIDCalls);
        Assert.Equal(1, candidate.GetCountCalls);
    }

    /// <summary>Show 抛异常：记录 hrUnavailable + 异常详情；不得伪造系统 HRESULT。</summary>
    [Fact]
    public void ShowExceptionRecordedNotFaked()
    {
        var candidate = new FakeCandidateElement { ThrowOnShow = new InvalidOperationException("boom") };
        var (observer, _, _) = NewObserver((14, candidate));
        var show = 1;
        observer.BeginUIElement(14, ref show);

        var result = Assert.Single(observer.TryShowCandidates(show: true));

        Assert.False(result.ShowHrAvailable);       // 无可用原生 HRESULT，不得声称系统返回 E_FAIL
        Assert.Equal("InvalidOperationException", result.ShowExceptionType);
        Assert.Equal("show", result.ShowExceptionStage);
        Assert.Equal("boom", result.ShowExceptionMessage);
    }

    /// <summary>IsShown-after 抛异常不得污染已取得的 Show HRESULT。</summary>
    [Fact]
    public void IsShownAfterExceptionDoesNotPolluteShowHr()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK, ThrowOnIsShownAfterShow = true };
        var (observer, _, _) = NewObserver((15, candidate));
        var show = 1;
        observer.BeginUIElement(15, ref show);

        var result = Assert.Single(observer.TryShowCandidates(show: true));

        Assert.Equal(TsfHResult.S_OK, result.HResult);
        Assert.True(result.ShowHrAvailable);         // Show 真实返回被保留
        Assert.False(result.ShownBefore);
        Assert.Null(result.ShownAfter);
        Assert.Null(result.IsShownAfterHr);
        Assert.Equal("shown-after", result.ShowExceptionStage);
    }

    /// <summary>同 id 重复 Begin：旧实例先 End（幂等），新引用接管存活集合。</summary>
    [Fact]
    public void DuplicateBeginSameIdEndsDisplacedElement()
    {
        var first = new FakeCandidateElement();
        var second = new FakeCandidateElement();
        var (observer, manager, _) = NewObserver((16, first));
        manager.Elements[16] = second;               // Begin 时 manager 返回 second

        var show = 1;
        observer.BeginUIElement(16, ref show);       // 绑定 first
        var firstInfo = Assert.Single(observer.LiveCandidates());

        // 同 id 再次 Begin：manager 现在给 second；first 的 ElementInfo 应被 End。
        observer.BeginUIElement(16, ref show);
        var secondInfo = Assert.Single(observer.LiveCandidates());

        Assert.True(firstInfo.Ended);
        Assert.NotSame(firstInfo, secondInfo);
        Assert.False(secondInfo.Ended);
        Assert.True(observer.IsLive(secondInfo));
        Assert.False(observer.IsLive(firstInfo));
    }

    /// <summary>正常 show/hide 顺序：无 End 时 before/after 都记录，hr 保留。</summary>
    [Fact]
    public void NormalShowHideSequenceRecordsBothShownValues()
    {
        var candidate = new FakeCandidateElement { ShowHResult = TsfHResult.S_OK };
        var (observer, _, _) = NewObserver((17, candidate));
        var show = 1;
        observer.BeginUIElement(17, ref show);

        var shown = Assert.Single(observer.TryShowCandidates(show: true));
        var hidden = Assert.Single(observer.TryShowCandidates(show: false));

        Assert.False(shown.ShownBefore);
        Assert.True(shown.ShownAfter);
        Assert.True(hidden.ShownBefore);
        Assert.False(hidden.ShownAfter);
        Assert.Equal(TsfHResult.S_OK, shown.HResult);
        Assert.Equal(4, candidate.IsShownCalls); // 每次 TryShow 调 before+after 两次 IsShown
    }

    // ---- 纯托管假实现 ----

    private class FakeElement : ITfUIElement
    {
        internal int ShowCalls { get; private set; }

        internal int IsShownCalls { get; private set; }

        internal int ShowHResult { get; init; } = TsfHResult.S_OK;

        /// <summary>Show 内部的同步回调（重入 EndUIElement 等）。</summary>
        internal Action? OnShow { get; set; }

        /// <summary>Show 时抛出的异常（非 null 则抛）。</summary>
        internal Exception? ThrowOnShow { get; init; }

        /// <summary>Show 之后的 IsShown 抛异常。</summary>
        internal bool ThrowOnIsShownAfterShow { get; init; }

        /// <summary>GetDescription 内部的同步回调（重入 EndUIElement 等）。</summary>
        internal Action? OnGetDescription { get; set; }

        internal int GetDescriptionCalls { get; private set; }

        internal int GetGUIDCalls { get; private set; }

        internal bool Shown { get; private set; }

        public int GetDescription(out string pbstrDescription)
        {
            GetDescriptionCalls++;
            pbstrDescription = "fake";
            // 模拟原生 GetDescription 内同步派发 EndUIElement（同一 UI 线程重入）。
            OnGetDescription?.Invoke();
            return TsfHResult.S_OK;
        }

        public int GetGUID(out Guid pguid)
        {
            GetGUIDCalls++;
            pguid = Guid.Empty;
            return TsfHResult.S_OK;
        }

        public int Show(int bShow)
        {
            ShowCalls++;
            if (ThrowOnShow is not null)
            {
                throw ThrowOnShow;
            }

            if (ShowHResult >= 0)
            {
                Shown = bShow != 0;
            }

            // 模拟原生 Show 内同步派发 EndUIElement（同一 UI 线程重入）。
            OnShow?.Invoke();
            return ShowHResult;
        }

        public int IsShown(out int pbShow)
        {
            IsShownCalls++;
            // Show 之后再次调用 IsShown 才允许抛（before 那次已成功）。
            if (ThrowOnIsShownAfterShow && IsShownCalls > 1)
            {
                throw new InvalidOperationException("element gone after show");
            }

            pbShow = Shown ? 1 : 0;
            return TsfHResult.S_OK;
        }
    }

    private sealed class FakeCandidateElement : FakeElement, ITfCandidateListUIElement
    {
        internal int GetCountCalls { get; private set; }

        public int GetCount(out uint puCount)
        {
            GetCountCalls++;
            puCount = 2;
            return TsfHResult.S_OK;
        }

        public int GetSelection(out uint puIndex)
        {
            puIndex = 0;
            return TsfHResult.S_OK;
        }

        public int GetString(uint uIndex, out string pstr)
        {
            pstr = $"cand{uIndex}";
            return TsfHResult.S_OK;
        }

        public int GetPageIndex(IntPtr pIndex, uint uSize, out uint puPageCnt)
        {
            puPageCnt = 0;
            return TsfHResult.S_OK;
        }

        public int SetPageIndex(IntPtr pIndex, uint uPageCnt) => TsfHResult.S_OK;

        public int GetCurrentPage(out uint puPage)
        {
            puPage = 0;
            return TsfHResult.S_OK;
        }

        public int GetUpdatedFlags(out uint pdwFlags)
        {
            pdwFlags = 0;
            return TsfHResult.S_OK;
        }

        public int GetDocumentMgr(out ITfDocumentMgr ppdim)
        {
            ppdim = null!;
            return TsfHResult.E_NOTIMPL;
        }
    }

    private sealed class FakeUiElementManager : ITfUIElementMgr
    {
        internal Dictionary<uint, ITfUIElement> Elements { get; }

        internal FakeUiElementManager(Dictionary<uint, ITfUIElement> elements) => Elements = elements;

        public int BeginUIElement(ITfUIElement pElement, ref int pbShow, out uint pdwUIElementId)
        {
            pdwUIElementId = 0;
            return TsfHResult.E_NOTIMPL;
        }

        public int UpdateUIElement(uint dwUIElementId) => TsfHResult.S_OK;

        public int EndUIElement(uint dwUIElementId) => TsfHResult.S_OK;

        public int GetUIElement(uint dwUIElementId, out ITfUIElement ppElement)
        {
            if (Elements.TryGetValue(dwUIElementId, out var element))
            {
                ppElement = element;
                return TsfHResult.S_OK;
            }

            ppElement = null!;
            return TsfHResult.E_FAIL;
        }

        public int EnumUIElements(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }
    }
}
