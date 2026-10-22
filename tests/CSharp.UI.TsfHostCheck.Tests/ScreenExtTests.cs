using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R24：`GetScreenExt` 是**显示表面**查询，依据微软官方 `tsfapp/textstor.cpp`
/// （`CTSFEditWnd::GetScreenExt` 注明 “no lock is necessary for this method.”）：
/// 独立于文档读锁与文本布局版本，非法 view 为 <c>E_INVALIDARG</c>，最小化/未显示返回零矩形。
/// <para>
/// 这些测试只验证隔离工具的 store 契约，不声称证明 TIP 行为或候选窗移动；
/// 真实 HWND 目前由 <see cref="DisplaySurface.FromWindow"/> 查询，
/// 无窗口路径由注入 provider / 独立表面快照覆盖。
/// </para>
/// </summary>
public sealed class ScreenExtTests
{
    [Fact]
    public void ScreenExtDoesNotRequireReadLockAndIsConsistentInsideLock()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(11, 22, 411, 222), true, null));

        // 无锁：必须成功（官方：no lock is necessary）。
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var noLock));
        Assert.Equal(TsfRect.FromLtrb(11, 22, 411, 222), noLock);

        // 读锁内：同一表面结果，不受锁状态影响。
        var insideLock = default(TsfRect);
        var insideHr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            insideHr = store.GetScreenExt(1, out insideLock);
            return insideHr;
        });
        Assert.Equal(TsfHResult.S_OK, insideHr);
        Assert.Equal(noLock, insideLock);
    }

    [Fact]
    public void InvalidViewReturnsEInvalidArgWithZeroOutputWithoutLock()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(1, 2, 3, 4), true, null));

        var hr = store.GetScreenExt(99, out var rect);

        Assert.Equal(TsfHResult.E_INVALIDARG, hr);
        Assert.Equal(default(TsfRect), rect);
    }

    [Fact]
    public void TextLayoutInvalidationKeepsSurfaceValid()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));
        var expected = TsfRect.FromLtrb(30, 40, 330, 240);
        StoreTestHelpers.Publish(store, new[] { 10, 10, 10 }, rowHeight: 20,
            client: expected, originX: 50, originY: 60, notify: false);

        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var before));
        Assert.Equal(expected, before);

        // 仅文本布局失效：显示表面未变化，必须仍可查且返回同一矩形。
        store.InvalidateLayout("text-layout-only");
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var afterInvalidation));
        Assert.Equal(expected, afterInvalidation);

        // 对照：GetTextExt 因布局失效而不可用（保持原语义，证明两者确实解耦）。
        Assert.Equal(TsfHResult.TS_E_NOLAYOUT,
            StoreTestHelpers.TextExt(store, sink, 0, 1).Hr);
    }

    [Fact]
    public void EditingDoesNotChangeSurface()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var expected = TsfRect.FromLtrb(5, 6, 405, 206);
        StoreTestHelpers.Publish(store, Array.Empty<int>(), client: expected, notify: false);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "hello"));

        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var afterEdit));
        Assert.Equal(expected, afterEdit);
    }

    [Fact]
    public void SurfaceFollowsCurrentWindowGeometryInsteadOfStaleSnapshot()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(0, 0, 100, 100), true, null));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var initial));
        Assert.Equal(TsfRect.FromLtrb(0, 0, 100, 100), initial);

        // 窗口移动：表面必须反映新位置，不能沿用旧坐标。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(0, -24, 100, 76), true, null));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var moved));
        Assert.Equal(TsfRect.FromLtrb(0, -24, 100, 76), moved);
        Assert.NotEqual(initial, moved);

        // resize：尺寸变化同样即时反映。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(0, -24, 200, 176), true, null));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var resized));
        Assert.Equal(200, resized.Width);
        Assert.Equal(200, resized.Height);
    }

    [Fact]
    public void MinimizedOrHiddenSurfaceIsZeroRectButStillOk()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        // 最小化/隐藏：官方语义是 S_OK + 零矩形，不是错误，也不能返回旧坐标。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, default, true, "hidden-or-minimized"));

        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var hidden));
        Assert.Equal(default(TsfRect), hidden);
        Assert.True(hidden.IsEmpty);
    }

    [Fact]
    public void RestoredSurfaceRecoversAfterMinimize()
    {
        var (store, _) = StoreTestHelpers.NewStore();
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, default, true, "hidden-or-minimized"));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var minimized));
        Assert.Equal(default(TsfRect), minimized);

        // 恢复显示：表面回到真实几何。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(80, 80, 840, 500), true, null));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var restored));
        Assert.Equal(TsfRect.FromLtrb(80, 80, 840, 500), restored);
    }

    [Fact]
    public void NativeQueryFailureIsDistinguishedFromInvisible()
    {
        var (store, _) = StoreTestHelpers.NewStore();

        // 原生失败：负 HRESULT + 零输出，绝不用旧坐标伪装成功。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.E_FAIL, TsfRect.FromLtrb(1, 2, 3, 4), true, "client-to-screen-failed"));
        var failureHr = store.GetScreenExt(1, out var failed);
        Assert.True(failureHr < 0);
        Assert.Equal(default(TsfRect), failed);

        // 不可见：S_OK + 零矩形（与失败严格区分）。
        store.UpdateDisplaySurface(new DisplaySurfaceResult(
            TsfHResult.S_OK, default, true, "hidden-or-minimized"));
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var invisible));
        Assert.Equal(default(TsfRect), invisible);
    }

    [Fact]
    public void ThrowingProviderDoesNotEscapeComBoundary()
    {
        var store = new AcpTextStore();
        store.DisplaySurfaceProvider = () => throw new InvalidOperationException("provider boom");

        // 异常不得穿过 COM 边界：输出零矩形并返回失败 HRESULT。
        var hr = store.GetScreenExt(1, out var rect);
        Assert.True(hr < 0);
        Assert.Equal(default(TsfRect), rect);
    }

    [Fact]
    public void InjectedProviderIsUsedAndItsFailureZeroesOutput()
    {
        var store = new AcpTextStore();
        store.DisplaySurfaceProvider = () => new DisplaySurfaceResult(
            TsfHResult.S_OK, TsfRect.FromLtrb(7, 8, 107, 108), true, null);
        Assert.Equal(TsfHResult.S_OK, store.GetScreenExt(1, out var injected));
        Assert.Equal(TsfRect.FromLtrb(7, 8, 107, 108), injected);

        // provider 报失败时输出必须清零。
        store.DisplaySurfaceProvider = () => new DisplaySurfaceResult(
            TsfHResult.E_FAIL, TsfRect.FromLtrb(9, 9, 9, 9), true, "boom");
        Assert.True(store.GetScreenExt(1, out var failed) < 0);
        Assert.Equal(default(TsfRect), failed);
    }

    [Fact]
    public void TextExtWithoutReadLockIsStillRejected()
    {
        // 对照：本轮只放开 GetScreenExt，GetTextExt 的读锁语义保持不变。
        var (store, _) = StoreTestHelpers.NewStore();
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetTextExt(1, 0, 0, out _, out _));
        Assert.Equal(TsfHResult.TS_E_NOLOCK, store.GetACPFromPoint(1, IntPtr.Zero, 0, out _));
    }

    [Fact]
    public void GetWndContractIsUnchanged()
    {
        // 明确不扩大到 GetWnd：它仍按 view 校验返回，且不做锁检查（保持既有行为）。
        var store = new AcpTextStore();
        Assert.Equal(TsfHResult.TF_E_INVALIDVIEW, store.GetWnd(99, out _));
        Assert.Equal(TsfHResult.S_OK, store.GetWnd(1, out var hwnd));
        Assert.Equal(IntPtr.Zero, hwnd);
    }
}
