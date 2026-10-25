# RT-01：StyledText 模型——展示文本、样式区间、交互区间、分段拓扑

创建日期：2026-10-25

状态：**待实施**（规划完成，待用户授权）

阶段：A

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策一/二。

[返回任务索引](README.md)

前置依赖：无。

## 文件范围

- `src/CSharp.UI/Text/StyledText.cs`（新）：`StyledText`、`TextStyleRange`、`TextInteractionRange`、`TextStyle`、Builder/`WithRange` 便利 API。
- `src/CSharp.UI/Text/`：Core 侧字素边界判定（`StringInfo` 直用；不引用 `CSharp.UI.Skia` 的 `Graphemes`）。
- `tests/CSharp.UI.Tests/` 对应单测。

## 实现契约

### 公开类型（唯一文本模型）

```csharp
public sealed class StyledText
{
    public string Text { get; }                               // 展示文本（已解码）
    public IReadOnlyList<TextStyleRange> Ranges { get; }      // 样式区间快照（无 payload）
    public IReadOnlyList<TextInteractionRange> Interactions { get; }  // 交互区间快照
    public bool HasRichContent { get; }                       // 任一区间非空
    public static StyledText Create(string text);             // plain fast path
    // 结构相等 + 构造时预计算：全量哈希 + 分段拓扑 + 每段几何样式签名
    // （分段拓扑 = {0,Length} ∪ Ranges端点 ∪ Interactions端点，去重排序；
    //   几何样式签名 = 每 segment 的 Family/Size/Weight/Italic —— 颜色/payload 不可见）
}

public readonly record struct TextStyleRange(int Start, int End, TextStyle Style);
public readonly record struct TextInteractionRange(int Start, int End, int Id, object Payload);
//   Id：同一逻辑链接的稳定身份（跨样式片段同 Id；同 URL 两个不同链接不同 Id）。
public sealed record TextStyle { /* Family?/Size?(绝对px)/Weight?/Italic?/Foreground?/Background?/Underline?/Strikethrough? */ }
```

### 验证（违反抛 `ArgumentException`，不静默 snap/截断）

- 两表各自：`Start` 升序、半开 `[Start,End)`、内部两两不重叠、非空、`End ≤ Text.Length`。
- `Ranges` 与 `Interactions` **之间允许重叠**（样式与交互正交）。
- 所有端点必须是 `StringInfo` 字素簇边界（切代理对/组合序列/ZWJ → 抛）。
- 输入集合复制为快照并排序验证；不持调用方集合引用。
- `Id`/`Payload` 参与 `TextInteractionRange` 值相等；`Payload` 应为值相等 record。

### 分段拓扑（RT-02 消费）

- 构造时算好端点集合（internal 只读暴露）；颜色/payload **值**不进拓扑——仅边界位置。
- 同端点集合仅颜色不同的两实例：几何签名相等（RT-02 的 GeometryKey 可命中）。

### 禁止

- 不建第二套公开 run 列表模型。
- 不改 `Text`/`TextLayoutRequest`/`ITextService`。
- 不含排版/渲染实现。

## 验收

- 重叠/越界/非字素边界/空区间/乱序输入全部按契约拒绝或归一；surrogate、组合字符、ZWJ emoji 边界正确。
- 两表正交重叠构造合法。
- `Equals`/`GetHashCode` 对内容相同分别构造实例成立；仅颜色不同实例几何签名相等；改端点（含 interaction 范围）→ 拓扑不同。
- 调用方事后修改输入集合不影响实例；`StyledText.Create("x").HasRichContent == false`。
