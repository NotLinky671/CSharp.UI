<#
.SYNOPSIS
    CSharp.UI 首版验证脚本（Windows / 任意平台上的 dotnet CLI）。

.DESCRIPTION
    任务 14 的验证入口：
      1. 还原 + 构建解决方案；
      2. 分层次运行测试（核心单元测试、文本测试、图形集成测试），图形测试不被默认跳过；
      3. 为四种目标架构执行 dotnet publish，并检查 GLFW / SkiaSharp / HarfBuzz 原生资产；
      4. 输出可复核的报告到 artifacts/verification。

.PARAMETER SkipGraphics
    跳过需要 OpenGL 上下文的图形集成测试（结果会明确标注为“未运行”，不会当作通过）。

.PARAMETER SkipPublish
    跳过四架构发布与原生资产检查。

.EXAMPLE
    pwsh -File build/verify.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipGraphics,
    [switch]$SkipPublish,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$reportDir = Join-Path $repoRoot 'artifacts/verification'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $reportDir "windows-$stamp.md"

$lines = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Report([string]$text) {
    Write-Host $text
    $lines.Add($text)
}

function Invoke-Step([string]$name, [scriptblock]$action, [switch]$Optional) {
    Add-Report "### $name"
    $output = & $action 2>&1 | Out-String
    $code = $LASTEXITCODE
    $lines.Add('```')
    $lines.Add(($output.Trim() -split "`r?`n" | Select-Object -Last 25) -join "`n")
    $lines.Add('```')
    if ($code -ne 0) {
        $status = if ($Optional) { '未通过（可选步骤）' } else { '失败' }
        Add-Report "- 结果：$status（退出码 $code）"
        if (-not $Optional) { $failures.Add($name) }
        else { $failures.Add("$name（可选）") }
    }
    else {
        Add-Report '- 结果：通过'
    }
    Add-Report ''
}

Add-Report "# CSharp.UI 验证报告（Windows）"
Add-Report ''
Add-Report "- 时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
Add-Report "- 主机：$([System.Environment]::OSVersion.VersionString) / $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
Add-Report "- 运行时：$(dotnet --version)"
Add-Report "- 配置：$Configuration"
Add-Report ''

Invoke-Step '还原与构建（dotnet build CSharp.UI.slnx）' {
    dotnet build CSharp.UI.slnx -c $Configuration
}

Invoke-Step '核心单元测试（无图形上下文）' {
    dotnet run --project tests/CSharp.UI.Tests -c $Configuration --no-build
}

Invoke-Step '文本测试（SkiaSharp/HarfBuzz，无图形上下文）' {
    dotnet run --project tests/CSharp.UI.Text.Tests -c $Configuration --no-build
}

Invoke-Step '跨模块集成测试（真实排版 + 运行时/控件，无图形上下文）' {
    dotnet run --project tests/CSharp.UI.Integration.Tests -c $Configuration --no-build
}

if ($SkipGraphics) {
    Add-Report '### 图形集成测试'
    Add-Report '- 结果：未运行（按参数跳过；不作为通过记录）'
    Add-Report ''
    $failures.Add('图形集成测试（未运行）')
}
else {
    Invoke-Step '图形集成测试（需要 OpenGL 3.3 上下文）' {
        dotnet run --project tests/CSharp.UI.OpenTK.Tests -c $Configuration --no-build
    }
}

if (-not $SkipPublish) {
    $rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
    foreach ($rid in $rids) {
        $outDir = Join-Path $repoRoot "artifacts/publish/$rid"
        Invoke-Step "发布 $rid" {
            dotnet publish samples/CSharp.UI.Samples.ControlGallery -c $Configuration -r $rid --self-contained false -o $outDir
        } -Optional

        Add-Report "### 原生资产检查 $rid"
        $native = Get-ChildItem -Path $outDir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'glfw|libSkiaSharp|SkiaSharp\.dll|libHarfBuzzSharp|HarfBuzzSharp\.dll' } |
            ForEach-Object { $_.Name } | Sort-Object -Unique
        if ($native.Count -eq 0) {
            Add-Report '- 结果：未找到原生资产（仅打包验证；该架构未在本机运行）'
            $failures.Add("原生资产 $rid")
        }
        else {
            Add-Report "- 结果：找到 $($native.Count) 个原生文件：$($native -join ', ')"
        }
        Add-Report ''
    }
}

Add-Report '## 汇总'
if ($failures.Count -eq 0) {
    Add-Report '- 全部步骤通过。'
}
else {
    Add-Report "- 需要注意的步骤：$($failures -join '；')"
}

$lines -join "`n" | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ''
Write-Host "报告已写入：$reportPath"

if ($failures.Count -gt 0) { exit 1 }
exit 0
