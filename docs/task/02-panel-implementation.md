# PS-02 Panel 实现 + 消费者边界

依据：[plan](../plan/2026-10-31-panel-sidebar-plan.md) §4/§7；契约清单见
[PS-01](01-contract-and-layout-rules.md)。
前置：PS-01（主审通过）。状态：**主审通过**——源码 `src/CSharp.UI/Views/Panel.cs`
（tag AC80）经主审 read 接受；GL 实图（light/dark/DPI 1.5）已实读归档；修订后
永久测试 **8/8 绿**（去 incidental 后，见 [首轮复核](../return/2026-10-31-panel-review.md)）。

## 目标

新增公开 `Panel` 控件（`src/CSharp.UI/Views/` 新文件，避免 `Controls.cs` 继续膨胀），
实现 PS-01 冻结的全部契约；低影响控件不堆测试，但必须有消费者边界验证 +
布局/渲染 throwaway smoke。

## 改动

- `src/CSharp.UI/Views/Panel.cs`（新文件）：

```csharp
public sealed class Panel : View
{
    public Panel(View content);
    public View Content { get; }
    public Thickness ContentPadding { get; init; } = new(12);
    public Color? BackgroundColor { get; init; }
    public Color? BorderColor { get; init; }
    public double BorderThickness { get; init; } = 1;
    public double CornerRadius { get; init; } = 8;
}
```

- measure：child 约束 deflate `ContentPadding + BorderThickness`（双侧）；
  Desired = child.Desired inflate 同项。
- arrange：child Frame = 内容矩形同 deflate。
- emit：背景（当前 `Theme.Surface` 当 null）→ 子 → 边框（`Theme.Border` 当 null，
  `BorderThickness=0` 时不绘）；圆角走既有圆角矩形 draw 语义。
- `GetLayoutFeatures` 复用 `PaddingView` 模式。
- `IsFocusable=false`（容器不抢焦点）；disabled/opacity 走环境继承。

## 验收

- B1：永久测试（`tests/CSharp.UI.Tests/`，消费者可观察断言）：
  - Desired 尺寸 = child.Desired + padding + border（双侧）；
  - child Frame 内缩量正确；
  - `BorderThickness=0` 时不绘边框、`ContentPadding=0` 时 child 贴内容边；
  - null `BackgroundColor`/`BorderColor` 取当前 `Theme.Surface`/`Theme.Border`，
    换 Theme 后按现值变化；
  - 不 pin 内部字段/常量字面。
- B2：throwaway 布局/渲染 smoke（非永久断言）：真实 draw 命令输出或原生渲染一张
  PNG 证明显示背景/边框/圆角/内容次序正确，raw 存入证据目录，不转永久截图断言。
- B3：无 `ShowBorder`/header/footer/多子叠放/圆角内容裁剪等范围外产物。
- B4：主审独立复审通过后开 PS-03。
