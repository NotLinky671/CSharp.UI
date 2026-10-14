// 文本服务与字体缓存是共享的静态资源，测试串行执行以保证诊断计数稳定。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
