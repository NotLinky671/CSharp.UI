using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 有界的 TSF 接入实验：只为 TI-01 决策记录“IMM32 兼容路径是否足够 / 是否必须走 TSF”。
/// </summary>
/// <remarks>
/// <para>
/// 接口顺序来自本机 SDK 10.0.26100.0 的 <c>msctf.idl</c>（ITfThreadMgr，第 535-561 行）：
/// IUnknown(3) 之后依次是 Activate(槽 3)、Deactivate(4)、CreateDocumentMgr(5)、EnumDocumentMgrs(6)、
/// GetFocus(7)、SetFocus(8)、AssociateFocus(9)、IsThreadFocus(10)、GetFunctionProvider(11)、
/// EnumFunctionProviders(12)、GetGlobalCompartment(13)。
/// </para>
/// <para>
/// 该实验只激活本进程的 TSF 线程管理器并把本探针自己的 HWND 关联到它创建的文档管理器；
/// 不注册任何应用侧文本编辑/组合接收器（那属于 TI-07 范围），因此即使输入法加载成功，
/// 也只用于判断“输入法是否真的接入了本进程”，而不是宣称已完成 TSF 集成。
/// </para>
/// </remarks>
internal static class TsfSession
{
    private const int S_OK = 0;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(IntPtr self, out uint clientId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDocumentMgrDelegate(IntPtr self, out IntPtr documentMgr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFocusDelegate(IntPtr self, IntPtr documentMgr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AssociateFocusDelegate(IntPtr self, IntPtr hwnd, IntPtr documentMgr, out IntPtr previousDocumentMgr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsThreadFocusDelegate(IntPtr self, out int isFocus);

    [DllImport("msctf.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int TF_CreateThreadMgr(out IntPtr threadMgr);

    private static IntPtr _threadMgr;
    private static IntPtr _documentMgr;

    internal static bool IsActive => _threadMgr != IntPtr.Zero;

    /// <summary>激活 TSF 线程管理器、创建文档管理器并把探针窗口关联进去；返回是否全部成功。</summary>
    internal static bool Engage(ProbeLog log, IntPtr hwnd, string label)
    {
        if (_threadMgr != IntPtr.Zero)
        {
            log.Record("tsf", "msctf-api", "already-engaged", new Dictionary<string, object?>
            {
                ["label"] = label,
            });
            return true;
        }

        var createHr = TF_CreateThreadMgr(out var threadMgr);
        log.Record("tsf", "msctf-api", "create-thread-mgr", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["hresult"] = $"0x{createHr:X8}",
            ["succeeded"] = createHr >= 0,
            ["thread_mgr"] = threadMgr == IntPtr.Zero ? "(null)" : $"0x{threadMgr.ToInt64():X}",
            ["iid"] = "AA80E801-2021-11D2-93E0-0060B067B86E (ITfThreadMgr, msctf.idl line 532)",
        });
        if (createHr < 0 || threadMgr == IntPtr.Zero)
        {
            return false;
        }

        var vtable = Marshal.ReadIntPtr(threadMgr);
        var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
        var activateHr = activate(threadMgr, out var clientId);
        log.Record("tsf", "msctf-api", "activate", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["hresult"] = $"0x{activateHr:X8}",
            ["succeeded"] = activateHr >= 0,
            ["client_id"] = clientId,
        });
        if (activateHr < 0)
        {
            return false;
        }

        _threadMgr = threadMgr;

        var createDocument = Marshal.GetDelegateForFunctionPointer<CreateDocumentMgrDelegate>(Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size));
        var documentHr = createDocument(threadMgr, out var documentMgr);
        log.Record("tsf", "msctf-api", "create-document-mgr", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["hresult"] = $"0x{documentHr:X8}",
            ["succeeded"] = documentHr >= 0,
            ["document_mgr"] = documentMgr == IntPtr.Zero ? "(null)" : $"0x{documentMgr.ToInt64():X}",
        });
        if (documentHr < 0 || documentMgr == IntPtr.Zero)
        {
            return false;
        }

        _documentMgr = documentMgr;

        var associateFocus = Marshal.GetDelegateForFunctionPointer<AssociateFocusDelegate>(Marshal.ReadIntPtr(vtable, 9 * IntPtr.Size));
        var associateHr = associateFocus(threadMgr, hwnd, documentMgr, out var previous);
        log.Record("tsf", "msctf-api", "associate-focus", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["hresult"] = $"0x{associateHr:X8}",
            ["succeeded"] = associateHr >= 0,
            ["hwnd"] = $"0x{hwnd.ToInt64():X}",
            ["previous_document_mgr"] = previous == IntPtr.Zero ? "(null)" : $"0x{previous.ToInt64():X}",
        });

        var setFocus = Marshal.GetDelegateForFunctionPointer<SetFocusDelegate>(Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size));
        var focusHr = setFocus(threadMgr, documentMgr);
        var isThreadFocus = Marshal.GetDelegateForFunctionPointer<IsThreadFocusDelegate>(Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size));
        isThreadFocus(threadMgr, out var focused);
        log.Record("tsf", "msctf-api", "set-focus", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["hresult"] = $"0x{focusHr:X8}",
            ["thread_focus"] = focused != 0,
        });

        LogTextServiceModules(log, label);
        return associateHr >= 0;
    }

    /// <summary>记录本进程是否真正加载了输入法 TIP 模块（判断 TSF 是否把输入法接进来）。</summary>
    internal static void LogTextServiceModules(ProbeLog log, string label)
    {
        var loaded = new List<string>();
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var name = module.ModuleName;
                if (name.Contains("tip", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("pinyin", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ime", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("wetype", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("chs", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Add($"{name} ({module.FileVersionInfo.FileVersion})");
                }
            }
        }
        catch (Exception ex)
        {
            loaded.Add($"error: {ex.Message}");
        }

        log.Record("tsf", "msctf-api", "text-service-modules", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["count"] = loaded.Count,
            ["modules"] = loaded.Count == 0 ? "(none loaded in this process)" : string.Join(" | ", loaded),
        });
    }

    internal static int Release()
    {
        var threadMgr = _threadMgr;
        _threadMgr = IntPtr.Zero;
        _documentMgr = IntPtr.Zero;
        if (threadMgr == IntPtr.Zero)
        {
            return S_OK;
        }

        try
        {
            var vtable = Marshal.ReadIntPtr(threadMgr);
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));
            return release(threadMgr);
        }
        catch (Exception)
        {
            return S_OK;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr self);
}
