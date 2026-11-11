#Requires -Version 7.0
<#
阶段 0-1 探针证据捕获（需要 PowerShell 7+；脚本使用 Tee-Object -Encoding / 现代重定向语义）。

用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\capture-probe-evidence.ps1

输出目录: evidence/stage0/net48-probe/<commit>/
断言失败时 exit 1，不会静默成功。
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\net48-probe\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
Write-Host "evidence dir: $dir"

# 1. 构建
dotnet build src\RstcUpdater\RstcUpdater.csproj -c Release -nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $dir 'build.log') -Encoding utf8 |
    Select-Object -Last 5
$buildExit = $LASTEXITCODE
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

function Invoke-Case {
    param([string[]]$Argv)
    $out = & $exe @Argv 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; Code = $LASTEXITCODE }
}

# 2. 成功 / 失败 / CLI 边界 / 默认护栏
$probeOk = Invoke-Case @('--probe')
$probeFail = Invoke-Case @('--probe', '--url', 'https://127.0.0.1:9/x')
$urlMissing = Invoke-Case @('--probe', '--url')
$bogus = Invoke-Case @('--probe', '--bogus')
$help = Invoke-Case @('--help')
$noArgs = Invoke-Case @()
$guard = Invoke-Case @('--install', 'E:\其他文件\红石镇客户端\红石镇1.21客户端')

$probeOk.Output + "EXIT=$($probeOk.Code)" | Out-File -FilePath (Join-Path $dir 'probe.ok.txt') -Encoding utf8
$probeFail.Output + "EXIT=$($probeFail.Code)" | Out-File -FilePath (Join-Path $dir 'probe.fail.txt') -Encoding utf8

$boundary = @()
$boundary += '--url-missing: exit=' + $urlMissing.Code
$boundary += $urlMissing.Output.TrimEnd()
$boundary += ''
$boundary += '--bogus: exit=' + $bogus.Code
$boundary += $bogus.Output.TrimEnd()
$boundary += ''
$boundary += '--help: exit=' + $help.Code
$boundary += $help.Output.TrimEnd()
$boundary += ''
$boundary += 'no-args: exit=' + $noArgs.Code
$boundary += $noArgs.Output.TrimEnd()
$boundary += ''
$boundary += '--install <player-instance>: exit=' + $guard.Code
$boundary += $guard.Output.TrimEnd()
$boundary | Out-File -FilePath (Join-Path $dir 'cli-boundary.txt') -Encoding utf8

# 3. 产物 + 哈希
$outDir = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48'
Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name |
    Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize | Out-File -FilePath (Join-Path $dir 'artifacts.txt') -Encoding utf8

Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$hash  $($_.Name)"
} | Out-File -FilePath (Join-Path $dir 'SHA256SUMS') -Encoding ascii

# 4. 身份/环境事实
$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$net = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$facts = @()
$facts += "commit=$commit"
$facts += "build_exit=$buildExit"
$facts += "probe_ok_exit=$($probeOk.Code)"
$facts += "probe_fail_exit=$($probeFail.Code)"
$facts += "cli_url_missing_exit=$($urlMissing.Code)"
$facts += "cli_bogus_exit=$($bogus.Code)"
$facts += "cli_help_exit=$($help.Code)"
$facts += "cli_noargs_exit=$($noArgs.Code)"
$facts += "guard_player_instance_exit=$($guard.Code)"
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'os=' + $os.ProductName + ' ' + $os.DisplayVersion + ' build ' + $os.CurrentBuild + '.' + $os.UBR
$facts += 'dotnet_release=' + $net.Release
$facts += 'dotnet_version=' + $net.Version
$facts += 'dotnet_sdk=' + (dotnet --version)
$facts += 'powershell=' + $PSVersionTable.PSVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8

Write-Host "commit=$commit"
Write-Host "build=$buildExit probe_ok=$($probeOk.Code) probe_fail=$($probeFail.Code)"
Write-Host "boundary: url_missing=$($urlMissing.Code) bogus=$($bogus.Code) help=$($help.Code) noargs=$($noArgs.Code) guard=$($guard.Code)"

# 5. 断言（不通过则 exit 1）
$failures = @()
if ($buildExit -ne 0) { $failures += "build exit $buildExit" }
if ($probeOk.Code -ne 0) { $failures += "probe ok exit $($probeOk.Code)" }
if ($probeFail.Code -ne 5) { $failures += "probe fail exit $($probeFail.Code)" }
if ($urlMissing.Code -ne 2) { $failures += "--url missing exit $($urlMissing.Code)" }
if ($bogus.Code -ne 2) { $failures += "--bogus exit $($bogus.Code)" }
if ($help.Code -ne 0) { $failures += "--help exit $($help.Code)" }
if ($noArgs.Code -ne 2) { $failures += "no-args exit $($noArgs.Code)" }
if ($guard.Code -ne 2) { $failures += "guard exit $($guard.Code)" }
if ($failures.Count -gt 0) {
    Write-Error ("证据断言失败: " + ($failures -join '; '))
    exit 1
}

Write-Host "CAPTURE=PASS"
exit 0
