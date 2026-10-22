using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R26：<c>ITfThreadMgrEx</c> 的 COM ABI 回归。
/// <para>
/// 依据本机 SDK `msctf.idl:566-573`：`interface ITfThreadMgrEx : ITfThreadMgr`，
/// 只新增 `ActivateEx` 与 `GetActiveFlags`。vtable 布局为
/// IUnknown 0–2、ITfThreadMgr 11 个方法占 3–13、**ActivateEx=14、GetActiveFlags=15**。
/// </para>
/// <para>
/// **关键**：`[ComImport]` 接口的 C# 基接口列表**不影响** CLR 生成的 vtable
/// （官方 qualify-net-types-for-interoperation）。若只声明 2 个扩展方法，它们会落在
/// slot 3/4，把 `Activate`/`Deactivate` 顶替掉。因此本接口必须**显式重声明**
/// 全部 11 个基方法，共 13 个声明方法。
/// </para>
/// <para>
/// 这些断言只核验 CLR 侧形状（`Marshal.GetEndComSlot`、声明顺序、签名、PreserveSig），
/// 不调用任何原生 COM；真实 flags 由 Codex 原生运行记录。
/// </para>
/// </summary>
public sealed class ThreadMgrExAbiTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>SDK 顺序的 11 个 ITfThreadMgr 方法，slot 3..13。</summary>
    private static readonly string[] BaseMethodOrder =
    {
        "Activate", "Deactivate", "CreateDocumentMgr", "EnumDocumentMgrs", "GetFocus",
        "SetFocus", "AssociateFocus", "IsThreadFocus", "GetFunctionProvider",
        "EnumFunctionProviders", "GetGlobalCompartment",
    };

    [Fact]
    public void IidMatchesSdkThreadMgrEx()
    {
        var guid = typeof(ITfThreadMgrEx).GetCustomAttribute<GuidAttribute>();

        Assert.NotNull(guid);
        Assert.Equal("3e90ade3-7594-4cb0-bb58-69628f5f458c", guid!.Value.ToLowerInvariant());
        Assert.Equal(new Guid("3e90ade3-7594-4cb0-bb58-69628f5f458c"), TsfIids.ThreadMgrEx);
    }

    [Fact]
    public void EndComSlotIsFifteen()
    {
        // ComImport 基接口列表不影响 vtable；必须是展平后的 15。
        Assert.Equal(15, Marshal.GetEndComSlot(typeof(ITfThreadMgrEx)));
    }

    [Fact]
    public void DeclaresThirteenFlattenedMethods()
    {
        var declared = typeof(ITfThreadMgrEx).GetMethods(Declared);

        Assert.Equal(13, declared.Length);
    }

    [Fact]
    public void BaseMethodsAreRedeclaredInSdkOrder()
    {
        // 前 11 个声明方法必须逐字对应 ITfThreadMgr，否则扩展方法会落错槽。
        var declared = typeof(ITfThreadMgrEx).GetMethods(Declared);
        var names = declared.Take(11).Select(m => m.Name).ToArray();

        Assert.Equal(BaseMethodOrder, names);
    }

    [Fact]
    public void ActivateExIsSlotFourteenAndGetActiveFlagsIsFifteen()
    {
        var declared = typeof(ITfThreadMgrEx).GetMethods(Declared);

        // 声明索引 11 → slot 3+11 = 14；索引 12 → slot 15。
        // （Marshal.GetComSlotForMethodInfo 在 .NET Core 不可用，故用声明顺序 + GetEndComSlot 交叉验证。）
        Assert.Equal("ActivateEx", declared[11].Name);
        Assert.Equal("GetActiveFlags", declared[12].Name);
        Assert.Equal(14, 3 + Array.FindIndex(declared, m => m.Name == "ActivateEx"));
        Assert.Equal(15, 3 + Array.FindIndex(declared, m => m.Name == "GetActiveFlags"));
        // 末槽必须等于最后一个声明方法的槽，证明没有额外的 CLR 附加方法。
        Assert.Equal(Marshal.GetEndComSlot(typeof(ITfThreadMgrEx)), 3 + declared.Length - 1);
    }

    [Fact]
    public void RedeclaredBaseSignaturesMatchThreadMgr()
    {
        // 重声明必须与基接口签名完全一致（含参数类型），否则同样会错位/错封送。
        var baseMethods = typeof(ITfThreadMgr).GetMethods(Declared);
        var declared = typeof(ITfThreadMgrEx).GetMethods(Declared);

        Assert.Equal(11, baseMethods.Length);
        for (var i = 0; i < baseMethods.Length; i++)
        {
            Assert.Equal(baseMethods[i].Name, declared[i].Name);
            Assert.Equal(baseMethods[i].ReturnType, declared[i].ReturnType);
            Assert.Equal(
                baseMethods[i].GetParameters().Select(p => p.ParameterType),
                declared[i].GetParameters().Select(p => p.ParameterType));
        }
    }

    [Fact]
    public void ActivateExSignatureMatchesSdk()
    {
        var method = typeof(ITfThreadMgrEx).GetMethod("ActivateEx", Declared);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(uint).MakeByRefType(), parameters[0].ParameterType);
        Assert.True(parameters[0].IsOut);
        Assert.Equal(typeof(uint), parameters[1].ParameterType);
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.NotNull(method.GetCustomAttribute<PreserveSigAttribute>());
    }

    [Fact]
    public void GetActiveFlagsSignatureMatchesSdk()
    {
        var method = typeof(ITfThreadMgrEx).GetMethod("GetActiveFlags", Declared);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(uint).MakeByRefType(), parameters[0].ParameterType);
        Assert.True(parameters[0].IsOut);
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.NotNull(method.GetCustomAttribute<PreserveSigAttribute>());
    }

    [Fact]
    public void EveryMethodPreservesSig()
    {
        var declared = typeof(ITfThreadMgrEx).GetMethods(Declared);

        Assert.All(declared, m =>
        {
            Assert.Equal(typeof(int), m.ReturnType);
            Assert.NotNull(m.GetCustomAttribute<PreserveSigAttribute>());
        });
    }

    [Fact]
    public void UiElementEnabledOnlyFlagIsFour()
    {
        Assert.Equal(0x4u, TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY);
        Assert.Equal(0x1u, TsfFlags.TF_TMAE_NOACTIVATETIP);
        Assert.Equal(0x2u, TsfFlags.TF_TMAE_SECUREMODE);
        Assert.Equal(0x8u, TsfFlags.TF_TMAE_COMLESS);
        Assert.Equal(0x10u, TsfFlags.TF_TMAE_WOW16);
        Assert.Equal(0x80000000u, TsfFlags.TF_TMF_ACTIVATED);
    }
}
