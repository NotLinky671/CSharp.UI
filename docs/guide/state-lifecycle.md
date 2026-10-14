# 状态与生命周期

## 状态由组件节点持有

`UseState<T>(name, initialValue)` 的状态保存在**运行时组件节点**上，而不是组件实例上。
组件重建时按名称取回同一个 `State<T>`，因此下面的写法不会丢状态：

```csharp
protected override View Build()
{
    var count = UseState("count", 0);   // 重建后仍是同一个 State<int>
    return new Text($"计数：{count.Value}");
}
```

规则：

- 名称在组件内唯一；同名状态重复声明为不同类型会抛出带节点路径的诊断。
- 新身份节点（不同类型、不同 Key、不同位置）使用 `initialValue`；旧节点卸载后其状态与订阅被释放。
- `UseState` 只能在 `Build()` 执行期间调用。

## 依赖跟踪

- 构建期间读取 `State<T>.Value` 会把当前组件登记为该状态的依赖。
- 构建结束后，依赖集合被替换为本次实际读取的集合：**不再读取的状态不会继续触发该组件**。
- 写入时按 `EqualityComparer<T>.Default` 判断，相同值不触发更新。
- 状态变化只把相关组件放入待重建队列，同一轮事件中的多次变化合并为一次重建；`Build()` 期间不会发生递归通知。
- 控件绑定（`Toggle`、`Slider`、`TextField`）在测量/绘制阶段通过 `Binding<T>.Read(node)` 读取来源状态，因此即使绑定保存在字段中也能正确登记依赖。

## 更新与绘制

```csharp
count.Value++;      // 只是入队 + 请求重绘
// 宿主（或测试）随后执行：
runtime.ProcessPendingWork();   // 重建组件 → 重新布局（如需要）
runtime.BuildFrame();           // 生成有序绘制命令
```

- 组件重建后，框架按描述结构比较新旧视图：只有布局相关属性变化时才重新测量；纯颜色变化只重绘。
- 子项增删或顺序变化会使父级的测量缓存失效（新增子项不经过属性更新路径，必须显式失效，否则新子项会停留在 0×0）。
- 应用不需要手动请求重绘，`UiApplication.Run` 的事件循环负责“按需唤醒、静止不重绘”。

## 更新循环诊断

- 循环检测按“同一组件在单轮调度内被重复重建的次数”计数（上限 100 次）。
- 一次性更新任意数量的组件都是正常场景，不受上限影响。
- 只有在 `Build()` 期间修改状态这类真正的循环下才会中止，并抛出/记录包含节点路径的诊断：请在事件处理中修改状态，而不是在 `Build()` 内。

## 禁用与焦点

- `Disabled` 通过环境传播，在布局阶段解析；因此更新流程在布局完成后校验交互状态。
- 控件被禁用（或节点卸载）时：焦点被清除、`GetNextWakeup()` 不再返回光标唤醒，悬停/按下/指针捕获被释放，被捕获的控件会收到 `OnPointerCancel` 以中止拖动。

## 线程约束

- 状态读写、布局、绘制命令生成都必须在 UI 线程执行。
- 在后台线程直接写状态会抛出 `UiThreadViolationException`，消息提示使用 `UiDispatcher.Post`。
- 正确的后台模式：

```csharp
Task.Run(() =>
{
    var result = Compute();
    UiDispatcher.Post(() => count.Value = result);   // 回到 UI 线程执行
});
```

- `IUiDispatcher.Post` 会唤醒空闲的事件循环；`RequestRedraw()` 只请求重绘；`RequestWakeup(delay)` 供光标闪烁等定时重绘使用。

## 卸载与清理

节点卸载（条件分支切换、列表项删除、窗口关闭）时：

1. 先卸载子树，再调用视图的卸载钩子；
2. 释放节点通过 `node.AddResource(...)` 注册的资源（释放失败会记录到 `UiRuntime.Diagnostics` 而不中断卸载）；
3. 释放状态订阅，清空交互状态（悬停、按下、指针捕获、焦点）与编辑状态；
4. 删除节点不会留下存活的事件处理器或资源引用。

组件的挂载/更新/卸载可通过覆写 `OnMount`、`OnUpdate`、`OnUnmount`（`View` 上的内部虚方法，测试程序集可见）观察。

## 运行时诊断

- `UiRuntime.Diagnostics`：资源释放失败、图片加载失败、组件重建异常等。
- `UiRuntime.LayoutPassCount` / `LastMeasureCount` / `BuildCount`：验证“纯视觉变化不重新布局”“更新次数被合并”。
- 组件更新在 100 轮内未收敛会抛出“更新循环”诊断，通常意味着在 `Build()` 内修改状态。
