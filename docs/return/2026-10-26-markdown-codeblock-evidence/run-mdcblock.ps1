# Markdown code-block verification runner — run ONLY after freeze notice.
# Reliable subprocess capture: System.Diagnostics.Process with async
# ReadToEndAsync stdout/stderr + explicit ExitCode (no Start-Process redirect
# stdout loss, no `| tail` exit swallow).
$ErrorActionPreference = 'Continue'
$Repo = 'C:\Users\nothh\code\C#\CSharp.UI'
$Ev = Join-Path $Repo 'docs\return\2026-10-26-markdown-codeblock-evidence'
New-Item -ItemType Directory -Force -Path $Ev | Out-Null

$run = 'run1'
while (Test-Path (Join-Path $Ev "$run-offscreen.exit.txt")) {
    $run = 'run' + ([int]($run -replace '\D','') + 1)
}
"run=$run $(Get-Date -Format o)" | Out-File (Join-Path $Ev 'MANIFEST.txt') -Append -Encoding utf8

# --- stage external lib (sample bin + fresh src framework assemblies) ---
$Lib = 'C:\Users\nothh\AppData\Local\Temp\md-codeblock\lib'
if (Test-Path $Lib) { Remove-Item $Lib -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Lib | Out-Null
$SampleBin = Join-Path $Repo 'samples\CSharp.UI.Samples.ControlGallery\bin\Debug\net10.0'
Copy-Item (Join-Path $SampleBin '*') $Lib -Recurse -Force

$srcMap = @{
    'CSharp.UI.dll'          = 'src\CSharp.UI\bin\Debug\net10.0\CSharp.UI.dll'
    'CSharp.UI.Text.dll'     = 'src\CSharp.UI.Text\bin\Debug\net10.0\CSharp.UI.Text.dll'
    'CSharp.UI.Markdown.dll' = 'src\CSharp.UI.Markdown\bin\Debug\net10.0\CSharp.UI.Markdown.dll'
    'CSharp.UI.OpenTK.dll'   = 'src\CSharp.UI.OpenTK\bin\Debug\net10.0\CSharp.UI.OpenTK.dll'
}
foreach ($k in $srcMap.Keys) {
    $src = Join-Path $Repo $srcMap[$k]
    if (-not (Test-Path $src)) { "MISSING $src" | Out-File (Join-Path $Ev 'MANIFEST.txt') -Append; exit 2 }
    Copy-Item $src (Join-Path $Lib $k) -Force
}
$dllReport = Join-Path $Ev "$run-dlls.txt"
foreach ($dll in Get-ChildItem $Lib -Filter *.dll) {
    $h = (Get-FileHash $dll.FullName -Algorithm SHA256).Hash.ToLower()
    "$($dll.Name) sha256=$h" | Out-File $dllReport -Append -Encoding utf8
}

$app = 'C:\Users\nothh\AppData\Local\Temp\md-codeblock\app'
$env:MDCB_EV = $Ev
$env:MDCB_RUN = $run

# Reliable capture: Process + async readers, UTF-8, explicit ExitCode.
function Invoke-Captured([string]$exe, [string[]]$cmdArgs, [string]$cwd, [string]$outFile, [string]$errFile, [string]$exitFile) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = (($cmdArgs | ForEach-Object { '"' + ($_ -replace '"','\"') + '"' }) -join ' ')
    $psi.WorkingDirectory = $cwd
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [System.IO.File]::WriteAllText($outFile, $outTask.Result, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($errFile, $errTask.Result, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($exitFile, "code=$($p.ExitCode)", [System.Text.Encoding]::ASCII)
    return $p.ExitCode
}

$buildOut = Join-Path $Ev "$run-build.stdout.txt"
$buildErr = Join-Path $Ev "$run-build.stderr.txt"
$buildEx  = Join-Path $Ev "$run-build.exit.txt"
$code = Invoke-Captured 'dotnet' @('build', "$app\mdcblock.csproj", '-c', 'Release') $app $buildOut $buildErr $buildEx
"build exit=$code" | Out-File (Join-Path $Ev 'MANIFEST.txt') -Append -Encoding utf8
if ($code -eq 0) {
    $exe = "$app\bin\Release\net10.0\win-x64\MdCodeblockOffscreen.exe"
    $env:PATH = "$app\bin\Release\net10.0\win-x64\runtimes\win-x64\native;$env:PATH"
    $offOut = Join-Path $Ev "$run-offscreen.stdout.txt"
    $offErr = Join-Path $Ev "$run-offscreen.stderr.txt"
    $offEx  = Join-Path $Ev "$run-offscreen.exit.txt"
    $code = Invoke-Captured $exe @() (Split-Path $exe) $offOut $offErr $offEx
    "offscreen exit=$code" | Out-File (Join-Path $Ev 'MANIFEST.txt') -Append -Encoding utf8
}
