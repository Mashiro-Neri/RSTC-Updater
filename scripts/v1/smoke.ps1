#Requires -Version 7.0
<#
V1 端到端冒烟：安装 → 更新（dry-run + 实际）→ 迁移 → 自更新检查 → 状态。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\v1\smoke.ps1 -GameDir <测试 .minecraft>
前提：该游戏目录已有完整 26.3（Fabric/原版会复用）。
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$PackV1,
    [string]$PackV2,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$game = [System.IO.Path]::GetFullPath($GameDir)
if (-not $PackV1) { $PackV1 = Join-Path $RepoRoot 'evidence\stage0\fixtures\pack-min.zip' }
if (-not $PackV2) { $PackV2 = Join-Path $RepoRoot 'evidence\stage0\fixtures\pack-min-v2.zip' }
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\v1\smoke\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$script:checks = 0
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:checks++
    if ($ok) { Write-Host "[OK]   $name $detail" -ForegroundColor Green }
    else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $failures.Add("$name $detail") }
}
function Run-Case([string[]]$argv) {
    $out = & $exe @argv 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; Code = $LASTEXITCODE }
}

$instance = 'RSTC-SMOKE'
$instanceDir = Join-Path $game "versions\$instance"
if (Test-Path -LiteralPath $instanceDir) { Remove-Item -LiteralPath $instanceDir -Recurse -Force }

# 1) 安装
$c1 = Run-Case @('--install-v1', $game, '--instance', $instance, '--zip', $PackV1)
Check '安装（26.3 夹具包）' ($c1.Code -eq 0 -and $c1.Output -match 'PACK_APPLY=PASS') "exit=$($c1.Code)"
Check '实例 json 存在' (Test-Path -LiteralPath (Join-Path $instanceDir "$instance.json"))
Check '实例 jar 存在（A2）' (Test-Path -LiteralPath (Join-Path $instanceDir "$instance.jar"))
Check '启动器配置存在' ((Test-Path -LiteralPath (Join-Path $instanceDir 'PCL\Setup.ini')) -and (Test-Path -LiteralPath (Join-Path $instanceDir 'hmclversion.cfg')))
$c1.Output | Out-File -FilePath (Join-Path $dir '1-install.txt') -Encoding utf8

# 2) 更新 dry-run
$v1 = (Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()
$c2 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2, '--dry-run')
Check '更新 dry-run' ($c2.Code -eq 0) "exit=$($c2.Code)"
Check 'dry-run 不改版本' (((Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()) -eq $v1)
$c2.Output | Out-File -FilePath (Join-Path $dir '2-update-dryrun.txt') -Encoding utf8

# 3) 更新实际
$c3 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2)
Check '更新实际' ($c3.Code -eq 0 -and $c3.Output -match 'PACK_APPLY=PASS') "exit=$($c3.Code)"
$v2 = (Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()
Check '版本已变化' ($v2 -ne $v1) "$v1 -> $v2"
Check '删除 config/a.txt' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'config\a.txt')))
Check '新增 config/b.txt' (Test-Path -LiteralPath (Join-Path $instanceDir 'config\b.txt'))
$c3.Output | Out-File -FilePath (Join-Path $dir '3-update.txt') -Encoding utf8

# 4) 迁移（构造一个旧实例做源）
$src = Join-Path $game 'versions\RSTC-SMOKE-SRC'
if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $src 'saves'), (Join-Path $src 'screenshots'), (Join-Path $src 'xaero') | Out-Null
[byte[]](1..16) | Set-Content -LiteralPath (Join-Path $src 'saves\world.dat') -AsByteStream
[byte[]](17..32) | Set-Content -LiteralPath (Join-Path $src 'screenshots\shot.png') -AsByteStream
'minimap' | Set-Content -LiteralPath (Join-Path $src 'xaero\minimap.txt') -Encoding utf8
'old options' | Set-Content -LiteralPath (Join-Path $src 'options.txt') -Encoding utf8
$srcHash = (Get-FileHash -LiteralPath (Join-Path $src 'saves\world.dat') -Algorithm SHA256).Hash
$c4 = Run-Case @('--migrate-v1', $game, '--from', 'RSTC-SMOKE-SRC', '--to', $instance, '--categories', 'saves,screenshots,maps')
Check '迁移 exit 0' ($c4.Code -eq 0) "exit=$($c4.Code)"
Check '迁移 saves' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves\world.dat'))
Check '迁移内容一致' ((Get-FileHash -LiteralPath (Join-Path $instanceDir 'saves\world.dat') -Algorithm SHA256).Hash -eq $srcHash)
Check '源未改动' ((Get-FileHash -LiteralPath (Join-Path $src 'saves\world.dat') -Algorithm SHA256).Hash -eq $srcHash)
$c4.Output | Out-File -FilePath (Join-Path $dir '4-migrate.txt') -Encoding utf8

# 5) 自更新检查
$c5 = Run-Case @('--check-self')
Check '自更新检查' ($c5.Code -eq 0) "exit=$($c5.Code)"
$c5.Output | Out-File -FilePath (Join-Path $dir '5-check-self.txt') -Encoding utf8

# 6) 状态 + 关键文件检查
$c6 = Run-Case @('--status', $game)
Check '状态/关键文件' ($c6.Code -eq 0) "exit=$($c6.Code)"
Check '状态输出含实例' ($c6.Output -match [regex]::Escape($instance))
$c6.Output | Out-File -FilePath (Join-Path $dir '6-status.txt') -Encoding utf8

# 证据
$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += "instance=$instance"
$facts += "version_before=$v1"
$facts += "version_after=$v2"
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'powershell=' + $PSVersionTable.PSVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts += "checks=$($script:checks)"
$facts += "failures=$($failures.Count)"
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8
Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notlike 'SHA256SUMS*' } |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } |
    Out-File -FilePath (Join-Path $dir 'SHA256SUMS.evidence.txt') -Encoding ascii

Write-Host "checks=$($script:checks) failures=$($failures.Count)"
Write-Host "evidence: $dir"
if ($failures.Count -gt 0) {
    Write-Host ("FAILED: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host 'V1_SMOKE=PASS' -ForegroundColor Green
exit 0
