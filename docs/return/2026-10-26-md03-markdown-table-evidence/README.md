# MD-03 独立验证：Markdown 表格布局 — 中间快照 `a79f60c3`(run1~3)→ 修复后快照 `dd776ba4`(run4 最终)

日期：2026-10-26 · 独立运行 `temp-verify-md04-real`（外部 runner 引 ProjectReference 真实程序集，不编译产品源、无 shim、无 mock）。frozen dll SHA-256：

- `CSharp.UI.Markdown.dll`:`a79f60c3adaa5bde999ea66ca421092659b11013a32dda030ea7a76e2b0eefcc` (run1~3 中间,含 bug)→ `dd776ba4c96212b38c32a0b85999f9d3ff1faa04a7f395d2701280bf8f2b34c7` (run4 修复后)
- `CSharp.UI.dll` `00ad025c8d6bc8a50f527b0b03404552150f2b3fde9b77d3bcaa122d7acf9214`
- `CSharp.UI.Text.dll` `ac83efbaf6eea3c141707b6ac7db85ddc235a7f63c0b3c3b84d8f44dfb2cf0c0`
- `CSharp.UI.OpenTK.dll` `7c7418b62d6e71248814b844d1e88876b65944f7f4066a725b5f8ae30a973d15`

## 结果：run2 `md03-run2-stdout.txt` — 20 PASS / 0 FAIL, exit 0

### 三档列宽（真实 fixture 经同一 Skia 服务预量后定 W）

- **min > W overflow**：`x` 80 字 token → pref=min=672; W=320 → desiredW=577.43, ScrollView clip=320，**横滚必需**。
- **min < W < pref 压缩恰 W**：col A `the quick brown fox…` 实测 pref≈419/min≈63(总长 63 字 434 含 padding), col B `k` (15)；W=256.18 实测 table desired=256.18（恰 W, 非 pref）。
- **Σpref ≤ W**：`| a | b |` desired=47.78 < 1024 viewport,**不撑满**。

### Resize / Responsive

- 320 → 1024 同 doc:desired 恒 154.23（=pref,不随 viewport 变）,非 viewport 放大。

### 同列跨行同宽(非跨列等宽)

- `colone` header/r1x/r2x cell 同 X=8、同 W=41.5352,误差 <0.5。

### header 样式

- `plain *italic* tail` header 全段 bold 覆盖 17/17 UTF-16 单元;italic 样式区间仍独立保留。

### 空 header + 缺 cell

- `|  | b |` + 数据行缺第 2 列：`x` 渲染于 col0 (X=8=padding),`b` header 仍渲染;无 [NodeType] 占位。

### mixed style

- 内联 \`mono\` 在 cell 内显示 Cascadia Mono family range。

### CJK

- `你好世界啊` (6 字素可断) 200 视口下 desired=109.55 fit,不强制 overflow。

### Wheel 横滚

- 长 token overflow 后,DeltaX=-1 → offset 0→24;DeltaX=+1 → 回 0。dispatch 路由 + ScrollView 真实滚动。

### GL 像素(1100x400 FBO, step=1 采样 cell glyph rect)

| 场景 | cell glyph rect | dark px |
|---|---|---|
| overflow-320 | (8,19.6,514×18.6) | 39 |
| natural-1024 | (8,0,7.5×18.6) | 16 |
| compress-320 | (8,0,281×37.2) | 697 |
| mixed-cjk | (8,0,87.1×18.7) | 240 |
| hdr-mixed | (8,0,85×18.6) | 166 |

PNG:`md03-gl-*.png`。所有断言均以 `cell.Frame`(=glyph 目标区)内实际暗像素计。

## 备注

- run1 (md03-run1-*) 是同一 harness 的早前快照,当时 9 项 FAIL 中大部分是 harness 自身 fixture 误读(`table.Frame` vs `DesiredSize`;Latin 34 字 <320 自然 fit;`fr.Commands.Count>=0` 恒真;不同文本列的等宽对比错误)。本轮 fixture 由 Skia 实测校准,撤回 run1 的相关失败假设——`u-wheel-scrolls` 在 run1 中因 hover 未先建立 + DeltaX 方向读反而误报,run2 双向探测后证明 ScrollView 横滚正常工作。
- harness 文件:`temp-verify-md04-real/Md03.cs` + `md04real.cs`(主程序),AssemblyName=`CSharp.UI.Markdown.Tests` (仅复用既有 InternalsVisibleTo,不改产品装配)。

## run3（同快照 `a79f60c3`，新增跨档 resize 与 overflow→1024 还原）

`md03-run3-{stdout,stderr,exit=0}`。**20 PASS / 3 FAIL**:

- `md3-resize-1024-pref`/`md3-resize-rowheight-diff`/`md3-resize-back-320` — 真实产品 bug:
  `MarkdownTable` 未重写 `LayoutPropertiesEquivalent`，新 descriptor 的 `_availableWidth` 变化
  时协调器只 InvalidateRender 不重新 Measure,DesiredSize 滞后一拍。
- `md3-resize-320-compress`、`md3-resize-pref-stable` 等 PASS;overflow 320 scroll +24 → 1024
  归 0(clamp) PASS。

## run4（good 修复后新快照 `dd776ba4`）— 27 PASS / 0 FAIL, exit 0

`md03-run4-{stdout,stderr,exit}.txt`。

`MarkdownTable.LayoutPropertiesEquivalent` 已比 `_availableWidth`（`MarkdownTable.cs` L184-186)。
跨档 resize 结果：

| viewport | desired W | desired H |
|---|---|---|
| 320 | 320 (压缩到 W) | 56.86（wrap 两行）|
| 1024 | 434.04（回 pref,不再黏住 320）| 38.24（单行）|
| 320 | 320 (再压缩） | — |

**`--force-fail` 受控故障探针验证 harness 退出码**：inject 1 FAIL 时进程 exit=1;
run1~3 的 exit=0 是历史 harness `Md03.Run` 硬编码 `return 0` 的 bug（run1/2 不
含 Md03 末段 — 当时函数体仍 `return 0`）；run3 起修复。run4 exit=0 反映真实 27 PASS / 0 FAIL。
