using System.Runtime.CompilerServices;

// 宿主内部类型（事件循环、输入适配、调度器）保持 internal，仅对图形集成测试可见，
// 以便在不扩大公开 API 的前提下验证输入映射与调度行为。
[assembly: InternalsVisibleTo("CSharp.UI.OpenTK.Tests")]
