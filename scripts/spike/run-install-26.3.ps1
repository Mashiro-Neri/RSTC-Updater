#Requires -Version 7.0
<#
阶段 0-1：在独立临时目录安装原版 26.3（L2，用户已确认）。
用法（项目根，需要 PowerShell 7+）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-install-26.3.ps1 -GameDir "E:\...\evidence\stage0\test-minecraft-<ts>"
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$full = [System.IO.Path]::GetFullPath($GameDir)
$commit = (git rev-parse HEAD).Trim()
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$dir = Join-Path $RepoRoot "evidence\stage0\install-26.3\$commit\$stamp"
New-Item -ItemType Directory -Force $dir | Out-Null

if (-not $env:RSTC_FORBIDDEN_TARGETS) {
    $env:RSTC_FORBIDDEN_TARGETS = 'E:\其他文件\红石镇客户端'
}

$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $exe --install $full 2>&1 | Tee-Object -FilePath (Join-Path $dir 'install.cold.txt') -Encoding utf8
$installExit = $LASTEXITCODE
$sw.Stop()

& (Join-Path $RepoRoot 'scripts\spike\verify-install-26.3.ps1') -GameDir $full 6>&1 |
    Tee-Object -FilePath (Join-Path $dir 'verify.cold.txt') -Encoding utf8
$verifyExit = $LASTEXITCODE

# warm 复跑：同目录再装一次（--reuse），应跳过已存在文件
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
& $exe --install $full --reuse 2>&1 | Tee-Object -FilePath (Join-Path $dir 'install.warm.txt') -Encoding utf8
$warmExit = $LASTEXITCODE
$sw2.Stop()

& (Join-Path $RepoRoot 'scripts\spike\verify-install-26.3.ps1') -GameDir $full 6>&1 |
    Tee-Object -FilePath (Join-Path $dir 'verify.warm.txt') -Encoding utf8
$verifyWarmExit = $LASTEXITCODE

$facts = @()
$facts += "commit=$(git rev-parse HEAD)"
$facts += "game_dir=$full"
$facts += "install_cold_exit=$installExit"
$facts += "install_cold_seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))"
$facts += "verify_cold_exit=$verifyExit"
$facts += "install_warm_exit=$warmExit"
$facts += "install_warm_seconds=$([math]::Round($sw2.Elapsed.TotalSeconds, 1))"
$facts += "verify_warm_exit=$verifyWarmExit"
$facts += 'os=' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName) +
          ' build ' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild)
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8

Get-ChildItem -LiteralPath $full -Recurse -File | Measure-Object -Property Length -Sum |
    ForEach-Object { "files=$($_.Count) bytes=$($_.Sum)" } |
    Out-File -FilePath (Join-Path $dir 'installed-size.txt') -Encoding utf8

Write-Host "install=$installExit verify=$verifyExit warm=$warmExit"
Write-Host "evidence: $dir"

if ($installExit -ne 0 -or $verifyExit -ne 0 -or $warmExit -ne 0 -or $verifyWarmExit -ne 0) { exit 1 }
exit 0
