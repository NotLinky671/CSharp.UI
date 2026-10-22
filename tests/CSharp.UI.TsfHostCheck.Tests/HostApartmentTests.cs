using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R17 套间策略的纯托管覆盖：默认 MTA、STA 仅在显式请求时使用；
/// 只有 S_OK/S_FALSE 拥有需要成对释放的 COM 引用；真实套间必须匹配请求。
/// 这些测试不初始化 COM（真实初始化在宿主线程上由 HostApartment 完成，见 --hidden-host）。
/// </summary>
public sealed class HostApartmentTests
{
    [Fact]
    public void MtaIsDefaultAndStaIsOptIn()
    {
        Assert.Equal(ComNative.CoInitMultithreaded, HostApartment.CoInitFlag(false));
        Assert.Equal(ComNative.CoInitApartmentThreaded, HostApartment.CoInitFlag(true));
        Assert.Equal(0x0u, HostApartment.CoInitFlag(false));
        Assert.Equal(0x2u, HostApartment.CoInitFlag(true));
        Assert.Equal("MTA", HostApartment.DescribeRequested(false));
        Assert.Equal("STA", HostApartment.DescribeRequested(true));
    }

    [Theory]
    [InlineData(TsfHResult.S_OK, true)]
    [InlineData(TsfHResult.S_FALSE, true)]
    [InlineData(TsfHResult.RPC_E_CHANGED_MODE, false)]
    [InlineData(TsfHResult.CO_E_NOTINITIALIZED, false)]
    [InlineData(TsfHResult.E_FAIL, false)]
    public void OnlySuccessHResultsOwnAComReference(int hresult, bool expected)
    {
        // RPC_E_CHANGED_MODE 表示线程已被别人以不同模型占用：没有引用，必须如实失败且不得反初始化。
        Assert.Equal(expected, HostApartment.OwnsReferenceFor(hresult));
    }

    [Theory]
    [InlineData(false, HostApartment.AptTypeMta, true)]
    [InlineData(false, HostApartment.AptTypeSta, false)]
    [InlineData(false, HostApartment.AptTypeMainSta, false)]
    [InlineData(false, HostApartment.AptTypeNa, false)]
    [InlineData(true, HostApartment.AptTypeSta, true)]
    [InlineData(true, HostApartment.AptTypeMainSta, true)]
    [InlineData(true, HostApartment.AptTypeMta, false)]
    [InlineData(true, HostApartment.AptTypeNa, false)]
    public void RequestedApartmentMustMatchRealApartment(bool sta, int apartmentType, bool expected)
    {
        Assert.Equal(expected, HostApartment.MatchesExpectedApartment(sta, apartmentType));
    }

    [Fact]
    public void MtaKeepsEntryThreadAndStaSelfSelectsDedicatedThread()
    {
        // 运行时在 Main 之前把入口线程固定为 MTA（实测 type=1），
        // 因此只有 STA 需要自选线程，且该线程在启动前设置套间。
        Assert.Equal(HostApartment.HostThreadSelection.ProcessEntry, HostApartment.SelectHostThread(false));
        Assert.Equal(HostApartment.HostThreadSelection.DedicatedSta,
            HostApartment.SelectHostThread(true));
    }

    [Fact]
    public void ApartmentDescriptionIsReadable()
    {
        Assert.Equal("type=1 qualifier=0", HostApartment.DescribeApartment(1, 0));
        Assert.Equal("type=3 qualifier=0", HostApartment.DescribeApartment(3, 0));
    }

    [Fact]
    public void ComApartmentHResultsAreReadable()
    {
        Assert.Equal("RPC_E_CHANGED_MODE", TsfHResult.Describe(TsfHResult.RPC_E_CHANGED_MODE));
        Assert.Equal("CO_E_NOTINITIALIZED", TsfHResult.Describe(TsfHResult.CO_E_NOTINITIALIZED));
    }
}
