# TI-07 微软拼音路线对照

日期：2026-10-19（Asia/Hong_Kong）。执行者：Codex。范围：独立TI-01探针，不是生产框架验收。

用户已恢复微软拼音并授权测试，旧免测不再适用。以当前已安装profile运行既有探针，没有启用/禁用profile或修改全局默认输入法；探针会为自己的HIMC打开中文模式。本次不安装输入法。

命令（仓库根）：

```powershell
dotnet tools/CSharp.UI.ImeProbe/bin/Debug/net10.0/imeprobe.dll --scenario ime-commit --profile current --input-mode scancode --capture on --duration 25 --foreground-wait 1500 --out artifacts/ime-work/ti07-codex-mspinyin-route
```

exit 0。只向确认前台的自有窗口输入专用串nihao；截图为自有窗口矩形。原始消息及截图在[证据目录](2026-10-19-ti07-mspinyin-route-evidence/)。

实际观测：

- GetActiveProfile报告微软拼音，CLSID `81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e`、profile `fa550b04-5ad7-411f-a5ac-ca038ec515d7`，状态active/enabled；不是按命令参数猜身份。
- 线程MAINSTA，窗口DPI144（150%）。
- 6条组合更新，预编辑历史含n、ni、ni'h、ni'ha、ni'hao；截图可见首选“你好”。
- seq163是唯一GCS_RESULTSTR结果，result_str为“你好”，UTF16长度2、宽接口字节量4，后续结束组合。
- 同一内容还到达2次WM_IME_CHAR、2次WM_CHAR及2次GLFW TextInput，生产桥必须明确来源归属并只提交一次。
- 窗口结束恢复原过程，uninstall-ok、verified=true，callback_exceptions=0。

注意：--profile current使旧探针的commit_requires_chinese=false，因此不能仅拿summary里的PASS证明中文验收；上述中文内容、微软拼音身份和所属预编辑历史由Codex另外核对原始事件及截图。candidate_requests/API成功也不证明候选跟随不同锚点。此次只支持IMM32路线在当前微软拼音环境可取得真实组合/结果；TI-07仍需生产宿主预编辑、单次绑定提交、按键隔离、移动/DPI/滚动后的系统候选跟随及生命周期验证。

官方边界参考：[WM_IME_COMPOSITION](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-composition)、[WM_IME_CHAR](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-char)、[ImmSetCandidateWindow](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immsetcandidatewindow)。API声明不能替代本机真实窗口证据。
