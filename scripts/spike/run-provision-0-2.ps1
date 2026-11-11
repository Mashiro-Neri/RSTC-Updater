#Requires -Version 7.0
<#
阶段 0-2：运行 provision 并采集证据（需要 PowerShell 7+）。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-provision-0-2.ps1 -GameDir <临时 .minecraft> [-ModSource <mods 目录>]
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$ModSource,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$full = [System.IO.Path]::GetFullPath($GameDir)
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\provision-0-2\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$argv = @('--provision', $full, '--reuse')
if ($ModSource) { $argv += @('--mod-src', $ModSource) }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $exe @argv 2>&1 | Tee-Object -FilePath (Join-Path $dir 'provision.txt') -Encoding utf8
$provisionExit = $LASTEXITCODE
$sw.Stop()

& (Join-Path $RepoRoot 'scripts\spike\verify-provision-0-2.ps1') -GameDir $full 6>&1 |
    Tee-Object -FilePath (Join-Path $dir 'verify.txt') -Encoding utf8
$verifyExit = $LASTEXITCODE

$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$full"
$facts += "mod_source=$ModSource"
$facts += "provision_exit=$provisionExit"
$facts += "provision_seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))"
$facts += "verify_exit=$verifyExit"
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'os=' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName) +
          ' build ' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild)
$facts += 'powershell=' + $PSVersionTable.PSVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8

# 实例 json 哈希
$hashes = @()
foreach ($v in @('A2', 'A1', 'B')) {
    $name = "RSTC-0-2-$v"
    $json = Join-Path $full "versions\$name\$name.json"
    $hashes += "$((Get-FileHash -LiteralPath $json -Algorithm SHA256).Hash)  $name.json"
}
$hashes | Out-File -FilePath (Join-Path $dir 'SHA256SUMS.instances.txt') -Encoding ascii

Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notlike 'SHA256SUMS*' } |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } |
    Out-File -FilePath (Join-Path $dir 'SHA256SUMS.evidence.txt') -Encoding ascii

Write-Host "provision=$provisionExit verify=$verifyExit"
Write-Host "evidence: $dir"
if ($provisionExit -ne 0 -or $verifyExit -ne 0) { exit 1 }
exit 0
