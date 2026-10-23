# TE-02 IME 客户端泛化与桥单行假设审计——独立复审记录

日期:2026-10-23。本记录补录独立复审结论,供 `docs/task/02-ime-client-cutover.md` 引用(原审核输出存于会话工件,不入库;此处只记录当时实际执行过的验证与结果,不虚构原始 stdout)。

## 复审范围

- `TextFieldCompositionClient` → `TextCompositionClient` 泛化改名与接口忠实性(TextField/TextEditor 共用同一组合客户端契约)。
- `WindowsImeBridge`/`WindowsTextInputRouter`/`TsfHost` 全部行级文本假设审计:不得残留单行 caret 几何、单行 measure/anchor 假设。
- `ITextEditSessionOwner` 接缝 owner 契约。

## 实际验证(复审时执行)

| 验证 | 结果 |
|---|---|
| 独立 runtime smoke(TextEditor + TextField 组合行为场景) | 22/22 PASS |
| 定向回归 CSharp.UI.Tests(组合/编辑/几何相关类过滤) | 234 项通过 |
| 定向回归 CSharp.UI.Text.Tests(排版/字素/代理对几何) | 179 项通过 |

原始进程 stdout 未落盘保存;最终复验以交付证据日志为准([text-editor-delivery-evidence](../delivery/2026-10-23-text-editor-delivery-evidence/) — 该目录 SHA-256 manifest 保护原始 bytes),其中 `CSharp.UI.Tests` 592、`CSharp.UI.Text.Tests` 85、`CSharp.UI.Integration.Tests` 53、`CSharp.UI.OpenTK.Tests` 678、双配置全绿覆盖了上述子集。

## 结论

TE-02 泛化 cutover 忠实;IMM/TSF 桥无单行假设残留;复审通过,批准进入 TE-03。
