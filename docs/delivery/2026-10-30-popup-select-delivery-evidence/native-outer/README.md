# native-outer —— 打开态外层滚动跟随 + 裁出关闭（P06 E1/E4 补证）

补齐 P-06 E1「外层滚动时跟随」与 E4「打开后滚外层」在**真实 GalleryApp + 原生窗口**的缺失证据（此前 47-case 只证了 inner，未证 outer）。单 case 驱动（`Program.cs` + `native-outer.csproj`，AssemblyName `CSharp.UI.OpenTK.Tests` 友元名），900×700 逻辑 @1.5 → fb 1350×1050，owned-HWND `SendInput`。

## 证据（`driver.stdout.txt` + 4 PNG）

打开普通 `Select`（`select-open-anchored` PASS、`popup-node` PASS），wheel 点在 **外层 page.Right−20 / H/2**（在 Gallery padding 内、popup/inner 之外）：

- **跟随（小负 delta −30）**：`outer off 2112→2124`（+12）、`anchorΔY=-12`、`popupΔY=-12`（相等非零）、`popup-still-open`、`binding-unchanged` → **E1/E4「外层滚动时跟随」通过**。图 `native-03-outer-follow.png`。
- **裁出关闭（继续 −120 直至锚完全离屏）**：`outer off 2124→2460` 递增、`anchor top=-45.1` 与根视口 `[0,H]` 不相交（`anchor-fully-clipped`）、`overlay-closed-on-clipout`、`binding-still-unchanged`、`host-alive` → **E4「滚出即关闭」通过**。图 `native-04-outer-clipped-closed.png`。

`UiApplication.Run exit=0`（其后由 harness 自身 `RequestExit(0)`，非被消费 Esc 误退）。无 `SetScrollOffset`/`Dispatch`/`State` 伪写。

`Program.cs` SHA256 `f7bf869c8822a7d3152df9361c54d95c3ad1e2c494db7f3d6ebd3cdaae2a8a50`。原工程在 `Temp/popup-select-acceptance/native-outer/`；可复现：同 4 ProjectReference + `dotnet build -c Release` + 复制 `shared/Assets/logo.png`，`P06_NATIVE_EV=<out>`。
