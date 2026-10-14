// 框架的调度器与状态跟踪是“单 UI 线程 + 单活动运行时”的设计，
// 因此测试串行执行，避免并行类之间互相影响静态的 UiDispatcher 与构建跟踪。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
