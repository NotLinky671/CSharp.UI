using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-02：为 TI-03 冻结 Binding 来源身份语义（R6）。
/// </summary>
/// <remarks>
/// <para>冻结的语义（内部身份 = <c>Source ?? binding 自身</c>）：</para>
/// <list type="number">
/// <item>
/// <description>
/// <see cref="State{T}.ToBinding"/> 每次返回新的 <see cref="Binding{T}"/> 实例，但来源状态是组件节点持有的
/// 同一 <see cref="State{T}"/>，因此身份稳定；不能按绑定实例引用或字符串相等判断来源是否变化。
/// </description>
/// </item>
/// <item><description>被复用的同一个自定义绑定实例身份稳定（用实例本身识别）。</description></item>
/// <item>
/// <description>
/// 每次重建新建的自定义/映射绑定没有可证明的来源，身份是新实例，必须按“来源已更换”保守处理；
/// 需要跨重建保留编辑状态时缓存绑定实例或使用 <see cref="State{T}"/>。
/// </description>
/// </item>
/// </list>
/// <para>
/// 本文件只固定识别语义；来源更换后的清理与重同步行为由 TI-03 实现并测试。
/// </para>
/// </remarks>
public class BindingSourceIdentityContractTests
{
    [Fact]
    public void StateToBinding_CreatesNewInstance_ButIdentityIsTheSourceState()
    {
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var first = host.Text!.ToBinding();
        var second = host.Text.ToBinding();

        Assert.NotSame(first, second);                              // 绑定实例不是身份
        Assert.Same(host.Text, first.Source);
        Assert.Same(first.Source, second.Source);
        Assert.Same(first.Identity, second.Identity);               // 身份 = 来源状态
        Assert.Same(host.Text, first.Identity);
    }

    [Fact]
    public void StateToBinding_IdentityIsStableAcrossComponentRebuilds()
    {
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        var firstBinding = ((TextField)node.View).Text;
        var identity = firstBinding.Identity;

        for (var i = 1; i <= 5; i++)
        {
            host.ExternalTick!.Value = i;
            app.Runtime.ProcessPendingWork();
            app.Frame();

            var current = ((TextField)node.View).Text;
            Assert.NotSame(firstBinding, current);                  // 每轮重建都是新绑定实例
            Assert.Same(identity, current.Identity);                // 但来源身份保持不变
        }
    }

    [Fact]
    public void ReusedCustomBindingInstance_HasStableIdentity()
    {
        var host = new ReusedCustomBindingHost();
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        var identity = ((TextField)node.View).Text.Identity;

        Assert.Same(host.CustomBinding, identity);                  // 身份 = 复用的绑定实例本身

        for (var i = 1; i <= 3; i++)
        {
            host.ExternalTick!.Value = i;
            app.Runtime.ProcessPendingWork();
            app.Frame();
            Assert.Same(identity, ((TextField)node.View).Text.Identity);
        }

        Assert.Equal("custom", host.Value);
    }

    [Fact]
    public void NewlyConstructedCustomBindings_HaveDistinctIdentity()
    {
        var stored = "x";
        var first = Binding<string>.From(() => stored, value => stored = value);
        var second = Binding<string>.From(() => stored, value => stored = value);

        Assert.Null(first.Source);
        Assert.Null(second.Source);
        Assert.NotSame(first.Identity, second.Identity);            // 新建 = 新身份（保守处理）
        Assert.Same(first, first.Identity);
    }

    [Fact]
    public void MappedBindings_LoseSourceIdentity_AndRequireCaching()
    {
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var sourceBinding = ((TextField)app.Runtime.Root.Require<TextField>().View).Text;
        var mappedFirst = sourceBinding.Select<string>(static value => value, static value => value);
        var mappedSecond = sourceBinding.Select<string>(static value => value, static value => value);

        Assert.Null(mappedFirst.Source);                            // 映射不保留来源状态
        Assert.NotSame(sourceBinding.Identity, mappedFirst.Identity);
        Assert.NotSame(mappedFirst.Identity, mappedSecond.Identity); // 每次新建都不同 → 保守重置

        // 缓存同一映射实例时身份稳定。
        var cached = mappedFirst;
        Assert.Same(cached.Identity, mappedFirst.Identity);
    }

    [Fact]
    public void WritingADifferentValueThroughBinding_RebuildsTheComponent()
    {
        // 与“同值短路”区分：把旧文本写成新文本会通知依赖组件重建。
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var builds = host.BuildCount;

        var binding = ((TextField)app.Runtime.Root.Require<TextField>().View).Text;
        binding.Value = "abcd";
        app.Runtime.ProcessPendingWork();

        Assert.True(host.BuildCount > builds);
        Assert.Equal("abcd", host.Text!.PeekValue());
    }

    [Fact]
    public void SameValueAssignment_DoesNotNotifyButIsNotEvidenceAboutRealEdits()
    {
        // 同值写入被 State 短路（不通知依赖）：这只说明“没有变化”，不代表真实编辑不会重建。
        var host = new BuildCountingFieldHost("abc");
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var builds = host.BuildCount;
        host.Text!.Value = "abc";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(builds, host.BuildCount);

        // 真实编辑（旧文本 -> 新文本）必须通知依赖。
        host.Text.Value = "abcd";
        app.Runtime.ProcessPendingWork();
        Assert.True(host.BuildCount > builds);
    }

    [Fact]
    public void StateBackedBinding_ReadRegistersDependencyOnOwningComponent()
    {
        var host = new BuildCountingFieldHost();
        using var app = new TestApp(host, new Size(400, 60));
        app.Frame();

        var node = app.Runtime.Root.Require<TextField>();
        var binding = ((TextField)node.View).Text;
        _ = binding.Read(node);

        // 通过绑定读取建立长期依赖后，外部写入状态会触发组件重建。
        var builds = host.BuildCount;
        host.Text!.Value = "external";
        app.Runtime.ProcessPendingWork();

        Assert.True(host.BuildCount > builds);
    }
}

/// <summary>带构建计数的单行文本框宿主组件（用于验证同值重建与来源身份）。</summary>
internal sealed class BuildCountingFieldHost : Component
{
    private readonly string _initial;

    public BuildCountingFieldHost(string initial = "") => _initial = initial;

    public int BuildCount { get; private set; }

    public State<string>? Text { get; private set; }

    public State<int>? ExternalTick { get; private set; }

    protected override View Build()
    {
        BuildCount++;
        Text = UseState("text", _initial);
        ExternalTick = UseState("tick", 0);
        _ = ExternalTick.Value;
        return new VStack(new TextField(Text.ToBinding()) { Width = 200 });
    }
}

/// <summary>把同一个自定义绑定实例跨重建复用的宿主组件。</summary>
internal sealed class ReusedCustomBindingHost : Component
{
    private string _value;

    public ReusedCustomBindingHost(string initial = "custom")
    {
        _value = initial;
        CustomBinding = Binding<string>.From(() => _value, value => _value = value);
    }

    public Binding<string> CustomBinding { get; }

    public string Value
    {
        get => _value;
        set => _value = value;
    }

    public State<int>? ExternalTick { get; private set; }

    protected override View Build()
    {
        ExternalTick = UseState("tick", 0);
        _ = ExternalTick.Value;
        return new VStack(new TextField(CustomBinding) { Width = 200 });
    }
}
