#Requires -Version 7.0
<#
A1b 证据采集：检查 PCL2/HMCL 是否已启动 RSTC-0-2-A2/A1/B，并把日志/启动器配置收进 evidence/acceptance。
用法:
  pwsh -File scripts\accept\collect-a1b-logs.ps1 -GameDir <测试 .minecraft> [-OutDir <目录>]
说明：只读取，不写玩家的 .minecraft；输出目录默认 evidence\acceptance\<时间戳>\a1b。
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$OutDir,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$game = [System.IO.Path]::GetFullPath($GameDir)
if (-not (Test-Path -LiteralPath $game)) {
    Write-Host "[FAIL] 游戏目录不存在: $game" -ForegroundColor Red
    exit 2
}

if (-not $OutDir) {
    $OutDir = Join-Path $RepoRoot ("evidence\acceptance\{0}\a1b" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

$instances = @('RSTC-0-2-A2', 'RSTC-0-2-A1', 'RSTC-0-2-B')
$rows = @()
$missing = @()
foreach ($instance in $instances) {
    $dir = Join-Path $game "versions\$instance"
    $log = Join-Path $dir 'logs\latest.log'
    $logLen = 0
    $modsLoaded = $null
    $crash = $false
    if (Test-Path -LiteralPath $log) {
        Copy-Item -LiteralPath $log -Destination (Join-Path $OutDir "$instance.latest.log") -Force
        $logLen = (Get-Item -LiteralPath $log).Length
        $text = Get-Content -LiteralPath $log -Raw -Encoding utf8 -ErrorAction SilentlyContinue
        if ($text) {
            $m = [regex]::Match($text, '(?i)(?:Loading|Loaded)\s+(\d+)\s+mods')
            if ($m.Success) { $modsLoaded = [int]$m.Groups[1].Value }
            $crash = $text -match '(?i)Exception|crash report'
        }
    }
    else {
        $missing += $instance
    }

    foreach ($cfg in @('PCL\Setup.ini', 'hmclversion.cfg')) {
        $src = Join-Path $dir $cfg
        if (Test-Path -LiteralPath $src) {
            $name = $instance + '.' + ($cfg -replace '[\\/]', '_')
            Copy-Item -LiteralPath $src -Destination (Join-Path $OutDir $name) -Force
        }
    }

    $rows += [pscustomobject]@{
        Instance = $instance
        LogExists = (Test-Path -LiteralPath $log)
        LogBytes = $logLen
        ModsLoaded = $modsLoaded
        CrashLike = $crash
    }
}

$rows | Format-Table -AutoSize | Out-String | Write-Host
$rows | Export-Csv -LiteralPath (Join-Path $OutDir 'a1b-summary.csv') -NoTypeInformation -Encoding utf8

$launchers = @(
    'PCL2: E:\其他文件\红石镇客户端\红石镇1.20.1定制客户端\Plain Craft Launcher 2.exe（游戏目录指向本测试目录）',
    "HMCL: $RepoRoot\evidence\stage0\tools\HMCL-3.16.4.jar（游戏目录指向本测试目录）"
)
$launchers | Set-Content -LiteralPath (Join-Path $OutDir 'how-to-launch.txt') -Encoding utf8

Write-Host "evidence: $OutDir"
if ($missing.Count -gt 0) {
    Write-Host ("[WAIT] 尚未启动过: " + ($missing -join ', ')) -ForegroundColor Yellow
    Write-Host "请按 how-to-launch.txt 用 PCL2/HMCL 各启动一次，然后重跑本脚本。" -ForegroundColor Yellow
    exit 1
}

$crashes = @($rows | Where-Object { $_.CrashLike })
if ($crashes.Count -gt 0) {
    Write-Host ("[WARN] 日志含异常/崩溃关键字: " + (($crashes | ForEach-Object { $_.Instance }) -join ', ')) -ForegroundColor Yellow
}

Write-Host 'A1B_LOGS=PASS' -ForegroundColor Green
exit 0
