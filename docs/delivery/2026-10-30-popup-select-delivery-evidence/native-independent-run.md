# Native independent run (E5) — frozen harness, post-Esc-fix

Command (cwd = `C:/Users/nothh/AppData/Local/Temp/popup-select-acceptance/native-observed`):
`P06_NATIVE_EV=<delivery-evidence>/native-independent bin/Release/net10.0/win-x64/CSharp.UI.OpenTK.Tests.exe`
→ observed shell exit **0**, stderr empty, ~106s wall time.

## Frozen producer hashes (verified before run, all match prior freeze)
- `Program.cs` sha256 `2f60f55b6869ab8ebbad9ac1efebea596ee5e3815088928803ffa6dbff5716fc`
- `native-observed.csproj` sha256 `030a9e6e949d0f136ab37eebbed07cbee757e1453bd5a7db58329eb9fe84b861`
- AppHost `CSharp.UI.OpenTK.Tests.exe` sha256 `d3608b5a2386c2c9ab896c6695a670a869546f623954f144f30574e377a59471` (162304 B)

## Managed dependency hashes (host-fixed build, run against these)
- `CSharp.UI.OpenTK.Tests.dll` `7457f351fa4dc4b6f4ade0251246f96398086f203da60bb578715398682a8592`
- `CSharp.UI.OpenTK.dll` `352ac448ac9213fe6878141312ba0a104c1df15088524de42aed593425a9ba47`
- `CSharp.UI.dll` `14426738212daadee82bb4571a38fed5411feeeb34381dde88d281b61518c547`
- `ControlGallery.dll` `6dfd79df2fc3226be59ee0eadd77451d0c2f651cebd89fc6c4809a9d4f111ce5`

## Results (raw: native-independent.stdout.txt)
- `grep -c '^PASS' = 47`, `grep -c '^FAIL' = 0`, `RESULT PASS`
- `CONTEXT scale=1.5 logical=900x700 fb=1350x1050` (real DPI 144)
- Key raw lines:
  - `popupSelectLong actual=选项 30 · 长列表项标签` + `PASS longlist-value` (exact item-30)
  - `PASS longlist-flip-above` (E1 native flip proven)
  - `PASS textfield-text` (popup TextField `portal-ui` + bound echo)
  - `B off 24->216 ... clipped=True open=False` + `PASS inner-clip-closes` (E4)
  - `PASS inner-popup-escapes-clip`, `PASS inner-follows` (anchor-follow)
  - `PASS app-alive-before-esc` → Esc → `PASS esc-closed-popup` + `esc-isopen-false` → **`PASS app-alive-after-esc`** (fix verified: popup Esc no longer exits app)
  - `final frames=476 host-exited=False`; `UiApplication.Run exit=0` emitted **after** own `RequestExit(0)` — not the Esc bug.
- 18 PNG + 2 tree dumps in `native-independent/`; every PNG decoded (zlib ok, 1350×1050).

## Process hygiene
- Only own PID window driven; SendInput per-step logs present (`CLICK logical=... screen=...`, `KEY vk=... ext=... sent=2`).
- Post-run check: zero lingering `CSharp.UI.OpenTK.Tests`/`ControlGallery` processes; nothing killed by name.
- No system IME/clipboard/DPI changes.
