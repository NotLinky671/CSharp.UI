namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R30：一次自有 input child 可见性操作的结果（hide/show）。两个布尔字段都是
/// 操作<b>之后</b>在原生侧读回的真实状态：查询失败/归属丢失如实为 false + Reason，
/// 绝不伪造"已隐藏/已显示"。
/// </summary>
internal readonly record struct ChildSurfaceVisibilityResult(
    bool Ok, bool WasVisible, bool VisibleAfter, string Reason);

/// <summary>
/// R30 opt-in 实验（<c>--child-hide-when-clipped</c>）的<b>共享可见性操作</b>：
/// 真实路径（<see cref="TsfHostWindow.SetInputSurfaceVisibility"/>）与托管测试替身
/// 走<b>同一个</b>本函数——操作前后在原生侧各读一次真实可见性，归属校验失败、
/// 原生调用返回 false 或抛出都如实成失败结果，绝不把 Win32 调用成功等同于
/// "候选已隐藏"（候选隐藏与否只能由真实截图人工判定）。
/// <para>
/// 本操作只触碰调用方已验证归属的自有 HWND；不做 SetFocus、不动前台、不发
/// WM_IME_*、不碰任何候选窗口。show=true 时仅当 <paramref name="wasOriginallyVisible"/>
/// 为 true 才提交 SWP_SHOWWINDOW——恢复路径绝不把本就不可见的 child 伪造成可见。
/// </para>
/// </summary>
internal static class ChildSurfaceVisibility
{
    /// <summary>
    /// 执行一次可见性变更。
    /// </summary>
    /// <param name="show">true = SWP_SHOWWINDOW，false = SWP_HIDEWINDOW（都带 NOACTIVATE|NOMOVE|NOSIZE|NOZORDER）。</param>
    /// <param name="ownsWindow">归属门禁：HWND 必须存活且属本进程本 UI 线程；返回 false 即拒绝操作。</param>
    /// <param name="isVisible">读当前真实可见性（操作前后各调一次）。</param>
    /// <param name="setVisible">实际执行 SetWindowPos/ShowWindow 的可注入原生动作；参数为 show 标志。</param>
    /// <param name="showOnlyIfWasVisible">show=true 且本参数为 false 时只读回状态、
    /// 不提交任何原生写（恢复路径不伪造一次显示）；hide 与常规 show 不受影响。</param>
    internal static ChildSurfaceVisibilityResult RunSurfaceVisibilityOperation(
        bool show,
        Func<bool> ownsWindow,
        Func<bool> isVisible,
        Func<bool, bool> setVisible,
        bool showOnlyIfWasVisible)
    {
        // 归属校验失败或抛出：拒绝操作，绝不在归属未验证时触碰任何 HWND。
        var owned = false;
        try
        {
            owned = ownsWindow();
        }
        catch (Exception ex)
        {
            return new ChildSurfaceVisibilityResult(false, false, false,
                $"ownership probe threw {ex.GetType().Name}: {ex.Message}");
        }

        if (!owned)
        {
            return new ChildSurfaceVisibilityResult(false, false, false,
                "own input surface ownership check failed");
        }

        // 操作前读回真实可见性：hide 需要保存原始状态供恢复判断；show 需要确认当前未显示。
        bool wasVisible;
        try
        {
            wasVisible = isVisible();
        }
        catch (Exception ex)
        {
            return new ChildSurfaceVisibilityResult(false, false, false,
                $"visibility read before threw {ex.GetType().Name}: {ex.Message}");
        }

        // show=true 且原本就不可见（showOnlyIfWasVisible=false）：恢复语义——
        // 只读回、不写，绝不把本就隐藏的 child 伪造成显示。
        var applied = false;
        if (!show || showOnlyIfWasVisible)
        {
            try
            {
                if (!setVisible(show))
                {
                    var afterOnFail = false;
                    try
                    {
                        afterOnFail = isVisible();
                    }
                    catch
                    {
                        // 读回失败不覆盖首个失败原因；VisibleAfter 保持 false。
                    }

                    return new ChildSurfaceVisibilityResult(false, wasVisible, afterOnFail,
                        $"set-window-pos {(show ? "show" : "hide")} returned false");
                }

                applied = true;
            }
            catch (Exception ex)
            {
                var afterOnThrow = false;
                try
                {
                    afterOnThrow = isVisible();
                }
                catch
                {
                    // 同上：读回失败不掩盖原生动作异常。
                }

                return new ChildSurfaceVisibilityResult(false, wasVisible, afterOnThrow,
                    $"set-window-pos {(show ? "show" : "hide")} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 操作后读回真实可见性——绝不把调用成功当成已显示/已隐藏。
        bool visibleAfter;
        try
        {
            visibleAfter = isVisible();
        }
        catch (Exception ex)
        {
            return new ChildSurfaceVisibilityResult(false, wasVisible, false,
                $"visibility read after threw {ex.GetType().Name}: {ex.Message}");
        }

        if (applied)
        {
            var expectVisible = show;
            if (visibleAfter != expectVisible)
            {
                return new ChildSurfaceVisibilityResult(false, wasVisible, visibleAfter,
                    $"visibility mismatch after {(show ? "show" : "hide")} "
                        + $"(expected_visible={expectVisible} actual={visibleAfter})");
            }
        }

        return new ChildSurfaceVisibilityResult(true, wasVisible, visibleAfter,
            applied ? "ok" : "not-applied-originally-hidden");
    }
}
