# RT-01 StyledText 模型实施记录

日期：2026-10-25。任务：[`docs/task/01-styled-text-model.md`](../task/01-styled-text-model.md)。

## 变更文件

- `src/CSharp.UI/Text/StyledText.cs`（新）：`StyledText`、`TextStyleRange`、`TextInteractionRange`、`TextStyle`、`TextPaintStyle`、`StyledTextBuilder`；internal `StyledTextFontSignature`、`StyledTextSegment`。
- `src/CSharp.UI/Primitives/FontDescription.cs`：新增 internal `FontFamily.FrozenSnapshot()`（把 `Fallbacks` 冻结为独立 `ReadOnlyCollection<string>`；公开 API 语义不变）。
- `tests/CSharp.UI.Tests/StyledTextTests.cs`（新）：21 个用例。
- `docs/task/01-styled-text-model.md`、`docs/task/README.md`、`docs/plan/2026-10-25-rich-text-and-markdown-plan.md`：状态更新。
- 原始日志归档：`docs/return/2026-10-25-rt01-styled-text-evidence/`。

## 公开 API

- `StyledText`：`Text` / `Ranges` / `Interactions` / `HasRichContent` / `Create(string)` / `WithRanges` / `WithInteractions` / 值相等 + `==`/`!=`。
- internal（供 RT-02 消费，测试程序集经 IVT 可见）：`SegmentBoundaries`、`Segments`、`TopologyEquals`、`TopologyHashCode`、`StyledTextFontSignature`、`StyledTextSegment`。
- `StyledTextBuilder`：`AddStyle` / `AddInteraction` / `Build`。

## 设计决策

- `TextStyleRange.Style` 声明为 `TextStyle?`（任务规格写 `TextStyle`，但需表达"全继承"）。**注意**：null 与 `new TextStyle()`（全 null 字段）在解析语义上等价，但参与值相等时**不相等**——不做归一化，调用方固定一种写法。
- 乱序输入按任务约定处理：复制后排序，再验证重叠。
- `Size` 仅验证有限正值；不对 `FontWeight` 枚举做额外校验（计划未要求）。
- `Payload` 为 `object?` 参与值相等；调用方契约要求不可变且 Equals/GetHashCode 稳定（模型不做深冻结/克隆）。同 URL 两个链接须给不同 `Id`。
- 分段拓扑 = {0,Length} ∪ 两表端点；每段几何签名仅 Family/Size/Weight/Italic，颜色/payload/装饰不可见。`TopologyEquals`/`TopologyHashCode` 为 RT-02 GeometryKey 的模型层基础，不含 ParagraphFont/排版参数（属 RT-02 请求侧）。
- `FontFamily.Fallbacks` 快照：新增 internal `FrozenSnapshot()`（对象初始化器在类内可写 private init），不改动 `FontFamily` 公开语义。
- `WithRanges`/`WithInteractions` 是**替换**整表（不是追加）；追加请用 `StyledTextBuilder` 重新构建。

## 复审修复（主审轮次）

- `BuildSegments` 样式查找从「起点恰好等于 range.Start」改为「range 包含 segment 起点」（`ranges[i].Start <= start`，`End > start` 由推进循环保证）——交互端点把一条长样式区间切成多段时，后段不再丢失字体签名。回归用例：`InteractionEndpointInsideStyleRange_DoesNotDropStyleSignature`（同拓扑 {0,2,4,6}，整段 Bold vs 首段 Bold 拓扑签名不等）。
- `StyledTextFontSignature`/`StyledTextSegment` 降为 `internal`（拓扑只供 RT-02 消费）。
- `ValidateGraphemeBoundaries` 改为 `StringInfo.ParseCombiningCharacters` 一次 int[] + `Array.BinarySearch`——不再逐字素枚举 `(string)enumerator.Current` 分配子串，也不用 HashSet。

## 验证（日志见 evidence/ 目录）

- `dotnet build src/CSharp.UI`（Debug）：0 警告 0 错误（`build-Debug.stdout.txt`）。
- `tests/CSharp.UI.Tests` 全套（exe 直跑 `-automated sync`，因 `dotnet test` IPC named pipe 在本环境被拒）：**661/661** 通过（`suite-CSharp.UI.Tests-Debug.stdout.txt`，exit 0 见 `suite-exit.txt`）；其中 `StyledTextTests` 21/21（`suite-StyledTextTests-Debug.stdout.txt`）。
- 独立复审 smoke（fast，最终快照）：31/31 PASS、exit 0——覆盖 public API `Dictionary` 键消费、原输入列表与 `FontFamily` 底层 mutation 隔离、同拓扑换色与换 payload 值几何签名不变、交互端点切段后字体签名保持、Unicode（ZWJ/组合字符/旗帜 emoji）边界、非法输入拒绝。原始 runner stdout 存于 [2026-10-25-rt01-styled-text-evidence/](2026-10-25-rt01-styled-text-evidence/)（含 SHA 清单）。

## 验收

- 主审通过，无返修缺陷。
- 契约限定复述：`Payload` 须由调用方提供**不可变且 Equals/GetHashCode 稳定**的值（模型不深冻结/克隆）；`TextStyleRange.Style` 为 `TextStyle?`，`null` 与全 null 字段解析等价但**值不相等**（不归一化）；plain 文本经 `Create` 走零区间快路径。shaping/排版/Markdown 均未实施（RT-02 起）。

## 未做（范围外）

- RT-02 resolved font/shaping/lease/cache；`RichText` 视图、Markdown、GL/IME/UI 验证。
