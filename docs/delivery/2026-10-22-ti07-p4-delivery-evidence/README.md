# TI-07 P4 交付证据（持久化）

日期：2026-10-22 · 配套交付记录：[2026-10-22-ti07-p4-delivery.md](../2026-10-22-ti07-p4-delivery.md)

本目录持久保存从 gitignored `artifacts/ime-work/p4-cancel-safety/` 精选的原始日志。真实 GUI 场景 JSONL/PNG 已持久位于 `docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p4-real-validation/`（postclean2-* 等目录），此处只索引不复制。

## 最终同源验证（`NativeCompositionOwner.cs` = `58626b17…`，post-stackalloc）

### Release 全量 7 套件（`test-sp-*`，均 Failed 0 — 2053/0 依据）

| 文件 | 程序集 | Total |
|---|---|---|
| `test-sp-ui.stdout.txt` | CSharp.UI.Tests | 549 |
| `test-sp-otk.stdout.txt` | CSharp.UI.OpenTK.Tests | 666 |
| `test-sp-tsfc.stdout.txt` | CSharp.UI.TsfHostCheck.Tests | 581 |
| `test-sp-int.stdout.txt` | CSharp.UI.Integration.Tests | 53 |
| `test-sp-ner.stdout.txt` | CSharp.UI.NativeEditReference.Tests | 41 |
| `test-sp-text.stdout.txt` | CSharp.UI.Text.Tests | 67 |
| `test-sp-ip.stdout.txt` | CSharp.UI.ImeProbe.Tests | 96 |

合计 **2053 / Failed 0**。

### Debug 核心套件（同一最终源）

| 文件 | Total |
|---|---|
| `test-final-dbg-ui.stdout.txt` | 549 |
| `test-final-dbg-otk.stdout.txt` | 666 |
| `test-final-dbg-tsfc.stdout.txt` | 581 |
| `test-final-dbg-comp.stdout.txt`（TextFieldComposition） | 66 |
| `test-final-dbg-cancel.stdout.txt`（TsfCancellationTests） | 10 |

### 最终 slnx 构建（同一最终源，0 错误）

- `build-sp-rel.stdout.txt`：Release 全 slnx（src/samples/tests/tools 全项目），**0 警告 0 错误**。
- `build-sp-dbg.stdout.txt`：Debug 全 slnx，10 既有警告 0 错误。

### %TEMP% UnauthorizedAccess 前后对照（同一最终源轮）

- `test-CSharp.UI.Text.Tests.stdout.txt`：修复前 Failed 8 / 67
- `test-imeprobe.stdout.txt`：修复前 Failed 15 / 96
- `test-text-pt.stdout.txt`：进程级 TEMP/TMP 重定向后 67/67 Failed 0
- `test-imeprobe-pt.stdout.txt`：同上 96/96 Failed 0

## 真实 GUI 证据索引（未复制，见 real-validation 目录）

- `docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p4-real-validation/postclean2-minimize-restore/`、`postclean2-hide-restore/`、`postclean2-window-deactivate/`：exit 0、`zero_writes_while_*=true`、`restore_commit_ok=true`、`composition_session_id` 1→2、JSONL 无 owner-diag/wnd-msg 残留。
- 工具自身构建：`docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p4-real-validation/build-tool[1-4]/`（`dotnet build tools/CSharp.UI.ImeHostCheck`，各 exit 0）。
- 全矩阵 manifest：`docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p4-real-validation/README.md`。

## 历史阶段日志（保留备查，不计入最终结论）

以下为较早阶段（stackalloc 前 owner = `30e8879f…` 或中间补丁）的运行，与最终哈希不关联：

- `test-final-ui.stdout.txt`（549）、`test-final-otk.stdout.txt`（666）——pre-stackalloc Release 两套件。
- `build-final-rel.stdout.txt`/`.stderr.txt`、`build-final-dbg.stdout.txt`/`.stderr.txt`——较早阶段 slnx 构建。
- `test-allocfree.stdout.txt`（8/8）、`test-tsfcancel-fence.stdout.txt`（8/8）——中间补丁定向批（当时的取消测试数非最终 10）。

## 源码哈希清单（source-manifest，交付时点实测 sha256）

| 路径 | sha256 |
|---|---|
| `src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionOwner.cs` | `58626b17fd50974a7f0b70c3bcafc9d753ba8d0958cb9634d1d19e44ab95631b` |
| `src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionFact.cs` | `bee697ac8d0df789148558f86f4debc9783462769657836edfc338cff5c75167` |
| `src/CSharp.UI.OpenTK/WindowsTsf/TsfHostSession.cs` | `bfbd342c12f130315599db928c19823e45c157bb8ad18cf22071994af29138e1` |
| `src/CSharp.UI/Editing/TextInputServiceSession.cs` | `17a083f359ea9d86d8d94460c7b13a5aa622ff55a122f7e52a3c3b8de95dc359` |
| `tests/CSharp.UI.OpenTK.Tests/TsfCancellationTests.cs` | `069f2f3234f2aa6a3d834ed647c1373274a62ad9a277cac2fbf67ebc846bae2a` |
| `tools/CSharp.UI.ImeHostCheck/Program.cs` | `f9e323f637064074267aede42b7b84e3cea26be0ca61c8ba92e9c30ee7f1f7b8` |
| `tools/CSharp.UI.HostRoutingSpike/Program.cs` | `534eb27e2842f30f0dcd934a549e1ae0494327a257b40ec576464f8e16588826` |
