#!/usr/bin/env bash
# CSharp.UI 首版验证脚本（Linux / macOS 与任意 dotnet CLI 环境）。
#
# 任务 14 的验证入口：
#   1. 构建解决方案；
#   2. 运行核心、文本测试；图形测试需要 OpenGL 上下文（Linux 下可用 Mesa + Xvfb）；
#   3. 为四种目标架构执行 dotnet publish 并检查原生资产；
#   4. 报告写入 artifacts/verification。
#
# 用法：
#   bash build/verify.sh                 # 完整验证
#   SKIP_GRAPHICS=1 bash build/verify.sh # 跳过图形测试（结果记录为未运行）
#
# Linux 无显示器（X11 + 软件渲染）示例：
#   sudo apt-get install -y xvfb libgl1-mesa-dri libglx-mesa0 libx11-6
#   xvfb-run -s "-screen 0 1280x800x24" env LIBGL_ALWAYS_SOFTWARE=1 bash build/verify.sh
#
# 注意：Xvfb 验证的是 X11 路径，不能作为 Wayland 的验证结果。

set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

configuration="${CONFIGURATION:-Release}"
report_dir="$repo_root/artifacts/verification"
mkdir -p "$report_dir"
report="$report_dir/$(uname -s | tr '[:upper:]' '[:lower:]')-$(date +%Y%m%d-%H%M%S).md"

failures=()

log() { echo "$1" | tee -a "$report"; }

log "# CSharp.UI 验证报告（$(uname -s)）"
log ""
log "- 时间：$(date -Iseconds)"
log "- 主机：$(uname -srm)"
log "- 运行时：$(dotnet --version)"
log "- 配置：$configuration"
if [ -n "${DISPLAY:-}" ]; then log "- 显示：DISPLAY=$DISPLAY（X11）"; else log "- 显示：未设置 DISPLAY"; fi
if [ -n "${WAYLAND_DISPLAY:-}" ]; then log "- Wayland：WAYLAND_DISPLAY=$WAYLAND_DISPLAY（本轮未单独验证）"; fi
log ""

step() {
  local name="$1"; shift
  log "### $name"
  log '```'
  if "$@" >>"$report" 2>&1; then
    log '```'
    log "- 结果：通过"
  else
    log '```'
    log "- 结果：失败（退出码 $?）"
    failures+=("$name")
  fi
  log ""
}

step "构建（dotnet build CSharp.UI.slnx）" dotnet build CSharp.UI.slnx -c "$configuration"
step "核心单元测试（无图形上下文）" dotnet run --project tests/CSharp.UI.Tests -c "$configuration" --no-build
step "文本测试（SkiaSharp/HarfBuzz）" dotnet run --project tests/CSharp.UI.Text.Tests -c "$configuration" --no-build
step "跨模块集成测试（真实排版 + 运行时/控件）" dotnet run --project tests/CSharp.UI.Integration.Tests -c "$configuration" --no-build

if [ "${SKIP_GRAPHICS:-0}" = "1" ]; then
  log "### 图形集成测试"
  log "- 结果：未运行（SKIP_GRAPHICS=1；不作为通过记录）"
  log ""
  failures+=("图形集成测试（未运行）")
else
  step "图形集成测试（需要 OpenGL 3.3 上下文）" dotnet run --project tests/CSharp.UI.OpenTK.Tests -c "$configuration" --no-build
fi

for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
  out="$repo_root/artifacts/publish/$rid"
  step "发布 $rid" dotnet publish samples/CSharp.UI.Samples.ControlGallery -c "$configuration" -r "$rid" --self-contained false -o "$out"

  log "### 原生资产检查 $rid"
  native=$(find "$out" -type f \( -iname '*glfw*' -o -iname '*skia*' -o -iname '*harfbuzz*' \) 2>/dev/null | xargs -r -n1 basename | sort -u | tr '\n' ' ')
  if [ -z "$native" ]; then
    log "- 结果：未找到原生资产（仅打包验证；该架构未在本机运行）"
    failures+=("原生资产 $rid")
  else
    log "- 结果：$native"
  fi
  log ""
done

log "## 汇总"
if [ ${#failures[@]} -eq 0 ]; then
  log "- 全部步骤通过。"
else
  log "- 需要注意的步骤：${failures[*]}"
fi

echo ""
echo "报告已写入：$report"
[ ${#failures[@]} -eq 0 ]
