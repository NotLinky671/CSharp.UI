using System.Runtime.CompilerServices;

// 宿主内部类型（事件循环、输入适配、调度器）保持 internal，仅对图形集成测试可见，
// 以便在不扩大公开 API 的前提下验证输入映射与调度行为。
[assembly: InternalsVisibleTo("CSharp.UI.OpenTK.Tests")]
// TI-07 真实输入法验收工具：读取桥的消息追踪/锚点计数/子类化恢复状态作为证据，不扩大公开 API。
[assembly: InternalsVisibleTo("CSharp.UI.ImeHostCheck")]
// TI-07 P3a 正式宿主接线验收工具：读取原生消息顺序、TSF 会话/连接/清理状态作为证据，不扩大公开 API。
[assembly: InternalsVisibleTo("CSharp.UI.TsfHostBridgeCheck")]
[assembly: InternalsVisibleTo("CSharp.UI.HostRoutingSpike")]
