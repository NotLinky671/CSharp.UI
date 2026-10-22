using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>COM identity 比较的三态结果：只有 <see cref="Same"/> 才算同一对象。</summary>
internal enum ComIdentity
{
    /// <summary>两个接口指针指向同一 COM 身份。</summary>
    Same,

    /// <summary>查询成功，但两者是不同对象。</summary>
    Different,

    /// <summary>查询本身失败（异常/空指针）：不能据此断言“不同”。</summary>
    Error,
}

/// <summary>一次身份比较的结果，保留失败原因供日志逐项复核。</summary>
internal readonly record struct ComIdentityResult(ComIdentity Identity, string? Detail)
{
    internal bool Same => Identity == ComIdentity.Same;

    internal bool Errored => Identity == ComIdentity.Error;
}

/// <summary>
/// 焦点/关联只读快照（默认关闭，仅 `--trace-layout-chain`）。
/// <para>
/// 只在宿主已处于**安全阶段**（发布布局前后、场景的 UI 线程 Invoke 内）调用：
/// 绝不在 sink / 锁 / 几何回调内部递归查询 COM，也不调用 SetFocus/AssociateFocus 改变行为。
/// </para>
/// <para>
/// 快照本身非原子：Win32 前台、Win32 焦点与 TSF 焦点是三次独立查询，
/// 期间状态可能被外部改变；日志据此只描述“采集时点”，不能当作时刻一致的证明。
/// </para>
/// <para>
/// COM identity 一律用 <c>Marshal.GetIUnknownForObject</c> 取得的 IUnknown 指针比较，
/// 并在 finally 中成对 Release；绝不用 RCW 引用相等证明身份（两个 RCW 可指向同一对象，
/// 同一 RCW 也可能是不同接口的别名）。查询失败与“不同”严格区分（R23）。
/// </para>
/// <para>
/// 所有释放异常都写进本次诊断记录（`release_errors`），不静默吞掉后声称成功；
/// 日志委托自身抛异常也不得穿透宿主流程。
/// </para>
/// </summary>
internal static class FocusSnapshot
{
    /// <summary>身份比较结果的文本形式。</summary>
    internal const string SameDocumentManager = "same-document-mgr";
    internal const string OtherDocumentManager = "other-document-mgr";
    internal const string NoFocus = "no-focus";
    internal const string Unavailable = "unavailable";
    internal const string IdentityError = "identity-error";

    /// <summary>
    /// 采集一次只读快照并写入 `diag/layout-chain` 日志。
    /// 未启用诊断时立即返回，不执行任何原生或 COM 查询。
    /// 日志失败不改变宿主控制流（与其他 trace 一致）。
    /// </summary>
    internal static void Capture(LayoutChainTrace chain, TsfTrace trace, string label,
        IntPtr ownHwnd, ITfThreadMgr? threadMgr, ITfDocumentMgr? ownDocumentMgr, ITfContext? ownContext)
    {
        if (!chain.Enabled)
        {
            return;
        }

        var fields = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["own_hwnd"] = $"0x{ownHwnd.ToInt64():X}",
            // 非原子：以下各项是依次采集，不能当作同一时刻的联合状态。
            ["atomic"] = false,
        };

        try
        {
            fields["win32_thread_id"] = NativeMethods.CurrentWin32ThreadId();
        }
        catch (Exception ex)
        {
            fields["win32_thread_id_error"] = Describe(ex);
        }

        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            fields["win32_foreground"] = $"0x{foreground.ToInt64():X}";
            fields["win32_foreground_is_own"] = foreground == ownHwnd && ownHwnd != IntPtr.Zero;

            var focus = NativeMethods.GetFocus();
            fields["win32_focus"] = $"0x{focus.ToInt64():X}";
            fields["win32_focus_is_own"] = focus == ownHwnd && ownHwnd != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            fields["win32_error"] = Describe(ex);
        }

        var releaseErrors = new List<string>();
        try
        {
            fields["tsf_focus"] = DescribeTsfFocus(threadMgr, ownDocumentMgr, ownContext, releaseErrors);
        }
        catch (Exception ex)
        {
            // 只读查询失败如实记录，不影响宿主流程。
            fields["tsf_focus_error"] = Describe(ex);
            fields["tsf_focus"] = Unavailable;
        }

        if (releaseErrors.Count > 0)
        {
            // 释放异常必须可见：绝不静默吞掉后声称“全部成功”。
            fields["release_errors"] = string.Join("; ", releaseErrors);
            fields["all_releases_succeeded"] = false;
        }
        else
        {
            fields["release_errors"] = null;
            fields["all_releases_succeeded"] = true;
        }

        // 日志委托异常不能穿透场景：与其他 trace 一样在此兜住。
        try
        {
            trace("diag", "layout-chain", fields);
        }
        catch (Exception)
        {
            // 日志失败不影响宿主；快照本身不改变任何状态。
        }
    }

    /// <summary>
    /// 只读比较 TSF 焦点 document manager 与自有 document manager 的 COM identity。
    /// 查询失败返回 <see cref="Unavailable"/>/<see cref="IdentityError"/>，绝不把
    /// “查询失败”当成 <see cref="OtherDocumentManager"/>。释放异常累积到
    /// <paramref name="releaseErrors"/>。
    /// </summary>
    internal static string DescribeTsfFocus(ITfThreadMgr? threadMgr, ITfDocumentMgr? ownDocumentMgr,
        ITfContext? ownContext, List<string>? releaseErrors = null)
    {
        if (threadMgr is null)
        {
            return Unavailable;
        }

        var focusHr = threadMgr.GetFocus(out var focusDocumentMgr);
        if (focusHr < 0)
        {
            return $"{Unavailable}(hr={TsfHResult.Describe(focusHr)})";
        }

        if (focusDocumentMgr is null)
        {
            return NoFocus;
        }

        try
        {
            if (ownDocumentMgr is null)
            {
                return OtherDocumentManager;
            }

            var comparison = CompareComIdentity(focusDocumentMgr, ownDocumentMgr, releaseErrors);
            if (comparison.Errored)
            {
                // 查询失败 ≠ 不同对象：保留原始异常/HRESULT。
                return $"{IdentityError}({comparison.Detail})";
            }

            if (!comparison.Same)
            {
                return OtherDocumentManager;
            }

            // 必要时（且接口已定义）再读 top context，仅用于描述，不参与判定。
            var topHr = focusDocumentMgr.GetTop(out var topContext);
            if (topHr >= 0 && topContext is not null)
            {
                try
                {
                    if (ownContext is null)
                    {
                        return $"{SameDocumentManager}(top-context-unknown)";
                    }

                    var topComparison = CompareComIdentity(topContext, ownContext, releaseErrors);
                    if (topComparison.Errored)
                    {
                        return $"{SameDocumentManager}({IdentityError}({topComparison.Detail}))";
                    }

                    return topComparison.Same
                        ? SameDocumentManager
                        : $"{SameDocumentManager}(top-context-differs)";
                }
                finally
                {
                    ReleaseComObject(topContext, "top-context", releaseErrors);
                }
            }

            return $"{SameDocumentManager}(top-hr={TsfHResult.Describe(topHr)})";
        }
        finally
        {
            ReleaseComObject(focusDocumentMgr, "focus-document-mgr", releaseErrors);
        }
    }

    /// <summary>
    /// 用 IUnknown 指针比较两个 COM 对象的身份，并在 finally 中成对释放两个引用。
    /// 不用 RCW 引用相等：RCW 与原生身份不是一一对应。
    /// 查询异常返回 <see cref="ComIdentity.Error"/> 并保留原因，绝不伪装成“不同”。
    /// </summary>
    internal static bool SameComIdentity(object left, object right)
        => CompareComIdentity(left, right, null).Same;

    /// <summary>同上，返回三态结果；释放异常累积到 <paramref name="releaseErrors"/>。</summary>
    internal static ComIdentityResult CompareComIdentity(object left, object right,
        List<string>? releaseErrors = null)
    {
        IntPtr leftPtr = IntPtr.Zero;
        IntPtr rightPtr = IntPtr.Zero;
        try
        {
            leftPtr = Marshal.GetIUnknownForObject(left);
            rightPtr = Marshal.GetIUnknownForObject(right);

            if (leftPtr == IntPtr.Zero || rightPtr == IntPtr.Zero)
            {
                return new ComIdentityResult(ComIdentity.Error,
                    $"null-iunknown(left=0x{leftPtr.ToInt64():X},right=0x{rightPtr.ToInt64():X})");
            }

            return new ComIdentityResult(
                leftPtr == rightPtr ? ComIdentity.Same : ComIdentity.Different, null);
        }
        catch (Exception ex)
        {
            return new ComIdentityResult(ComIdentity.Error, Describe(ex));
        }
        finally
        {
            ReleasePointer(leftPtr, "left-iunknown", releaseErrors);
            ReleasePointer(rightPtr, "right-iunknown", releaseErrors);
        }
    }

    private static void ReleasePointer(IntPtr pointer, string target, List<string>? releaseErrors)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Marshal.Release(pointer);
        }
        catch (Exception ex)
        {
            releaseErrors?.Add($"{target}={Describe(ex)}");
        }
    }

    private static void ReleaseComObject(object? target, string name, List<string>? releaseErrors)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(target);
        }
        catch (Exception ex)
        {
            // 释放异常不改变宿主控制流，但必须出现在诊断记录里。
            releaseErrors?.Add($"{name}={Describe(ex)}");
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
