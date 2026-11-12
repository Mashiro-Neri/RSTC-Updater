#Requires -Version 7.0
<#
V1 端到端冒烟：安装 → 更新（dry-run + 实际 + 损坏包）→ 迁移（拒绝覆盖/备份/通配符）→ 自更新检查 → 状态。
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
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 独立配置目录（避免用户 %APPDATA% 配置影响断言；同时测试 RSTC_CONFIG_DIR 覆盖）
$cfgDir = Join-Path $dir 'config'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
@{ lastGameDir = $game; lastInstance = 'RSTC-SMOKE'; backupRetention = 3 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Encoding utf8
$env:RSTC_CONFIG_DIR = $cfgDir

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
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

$instance = 'RSTC-SMOKE'
$instanceDir = Join-Path $game "versions\$instance"
if (Test-Path -LiteralPath $instanceDir) { Remove-Item -LiteralPath $instanceDir -Recurse -Force }
$src = Join-Path $game 'versions\RSTC-SMOKE-SRC'
if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }

# ---------- 1) 安装 ----------
$c1 = Run-Case @('--install-v1', $game, '--instance', $instance, '--zip', $PackV1)
Check '安装（26.3 夹具包）' ($c1.Code -eq 0 -and $c1.Output -match 'PACK_APPLY=PASS') "exit=$($c1.Code)"
Check '实例 json 存在' (Test-Path -LiteralPath (Join-Path $instanceDir "$instance.json"))
Check '实例 jar 存在（A2）' (Test-Path -LiteralPath (Join-Path $instanceDir "$instance.jar"))
$smokeJson = Get-Content -LiteralPath (Join-Path $instanceDir "$instance.json") -Raw -Encoding utf8 | ConvertFrom-Json
Check '实例 json clientVersion=26.3' ($smokeJson.clientVersion -eq '26.3') "clientVersion=$($smokeJson.clientVersion)"
Check '实例有 saves 目录' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves'))
Check '启动器配置存在' ((Test-Path -LiteralPath (Join-Path $instanceDir 'PCL\Setup.ini')) -and (Test-Path -LiteralPath (Join-Path $instanceDir 'hmclversion.cfg')))
$c1.Output | Out-File -FilePath (Join-Path $dir '1-install.txt') -Encoding utf8

# ---------- 2) 更新 dry-run / 实际 ----------
$v1 = (Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()
$c2 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2, '--dry-run')
Check '更新 dry-run' ($c2.Code -eq 0) "exit=$($c2.Code)"
Check 'dry-run 不改版本' (((Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()) -eq $v1)
$c2.Output | Out-File -FilePath (Join-Path $dir '2-update-dryrun.txt') -Encoding utf8

$c3 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2)
Check '更新实际' ($c3.Code -eq 0 -and $c3.Output -match 'PACK_APPLY=PASS') "exit=$($c3.Code)"
$v2 = (Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim()
Check '版本已变化' ($v2 -ne $v1) "$v1 -> $v2"
Check '删除 config/a.txt' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'config\a.txt')))
Check '新增 config/b.txt' (Test-Path -LiteralPath (Join-Path $instanceDir 'config\b.txt'))
$c3.Output | Out-File -FilePath (Join-Path $dir '3-update.txt') -Encoding utf8

# ---------- 2b) M2：损坏/越界 zip 不应崩溃（含合法 index，需 --force 才进入应用） ----------
$evilZip = Join-Path $game '.rstc-tmp\smoke-evil.zip'
New-Item -ItemType Directory -Force (Split-Path $evilZip) | Out-Null
if (Test-Path -LiteralPath $evilZip) { Remove-Item -LiteralPath $evilZip -Force }
$za = [System.IO.Compression.ZipFile]::Open($evilZip, 'Create')
$idx = $za.CreateEntry('modrinth.index.json')
$w = New-Object System.IO.StreamWriter($idx.Open()); $w.Write('{"versionId":"evil","dependencies":{"minecraft":"26.3"}}'); $w.Dispose()
$e = $za.CreateEntry('overrides/../../evil-outside.txt')
$w = New-Object System.IO.StreamWriter($e.Open()); $w.Write('evil'); $w.Dispose()
$za.Dispose()
$outside = Join-Path $game 'evil-outside.txt'
if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath $outside -Force }
$instanceHashBefore = (Get-ChildItem -LiteralPath $instanceDir -Recurse -File | ForEach-Object { "$($_.FullName)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }) -join "`n"
$c3b = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $evilZip, '--force')
Check '越界 zip 被拒绝且不崩溃' ($c3b.Code -eq 7) "exit=$($c3b.Code)"
Check '越界 zip 无越界写盘' (-not (Test-Path -LiteralPath $outside))
$instanceHashAfter = (Get-ChildItem -LiteralPath $instanceDir -Recurse -File | ForEach-Object { "$($_.FullName)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }) -join "`n"
Check '越界 zip 未改动实例' ($instanceHashBefore -eq $instanceHashAfter)
$c3b.Output | Out-File -FilePath (Join-Path $dir '3b-update-badzip.txt') -Encoding utf8

# ---------- 3) M1：隔离检测按实际值 ----------
$setupPath = Join-Path $instanceDir 'PCL\Setup.ini'
$setupBackup = Get-Content -LiteralPath $setupPath -Raw -Encoding utf8
$hmclPath = Join-Path $instanceDir 'hmclversion.cfg'
$hmclBackup = Get-Content -LiteralPath $hmclPath -Raw -Encoding utf8
($setupBackup -replace 'VersionArgumentIndieV2:True', 'VersionArgumentIndieV2:False') | Set-Content -LiteralPath $setupPath -Encoding utf8
'{"usesGlobal":false,"gameDirType":0}' | Set-Content -LiteralPath $hmclPath -Encoding utf8
$c3c = Run-Case @('--status', $game)
Check '隔离关闭时状态显示未启用' ($c3c.Output -match '未启用隔离') ''
$setupBackup | Set-Content -LiteralPath $setupPath -Encoding utf8 -NoNewline
$hmclBackup | Set-Content -LiteralPath $hmclPath -Encoding utf8 -NoNewline
$c3d = Run-Case @('--status', $game)
Check '隔离恢复后显示已启用' ($c3d.Output -match '已启用隔离') ''
$c3c.Output | Out-File -FilePath (Join-Path $dir '3c-status-isolation-off.txt') -Encoding utf8

# ---------- 4) 迁移：拒绝覆盖 + 备份 + XaeroWaypoints* 全部 ----------
New-Item -ItemType Directory -Force (Join-Path $src 'saves'), (Join-Path $src 'screenshots'), (Join-Path $src 'xaero'), (Join-Path $src 'XaeroWaypoints_A'), (Join-Path $src 'XaeroWaypoints_B') | Out-Null
[byte[]](1..16) | Set-Content -LiteralPath (Join-Path $src 'saves\world.dat') -AsByteStream
[byte[]](17..32) | Set-Content -LiteralPath (Join-Path $src 'screenshots\shot.png') -AsByteStream
'minimap' | Set-Content -LiteralPath (Join-Path $src 'xaero\minimap.txt') -Encoding utf8
'wp-a' | Set-Content -LiteralPath (Join-Path $src 'XaeroWaypoints_A\a.txt') -Encoding utf8
'wp-b' | Set-Content -LiteralPath (Join-Path $src 'XaeroWaypoints_B\b.txt') -Encoding utf8
'source options' | Set-Content -LiteralPath (Join-Path $src 'options.txt') -Encoding utf8
$srcHash = Hash (Join-Path $src 'saves\world.dat')
$targetOptionsBefore = Hash (Join-Path $instanceDir 'options.txt')
$backupBefore = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory -Filter 'migrate_*' -ErrorAction SilentlyContinue).Count

$c4a = Run-Case @('--migrate-v1', $game, '--from', 'RSTC-SMOKE-SRC', '--to', $instance)
Check '迁移遇同名文件默认拒绝' ($c4a.Code -eq 2) "exit=$($c4a.Code)"
Check '拒绝时目标 options 未变' ((Hash (Join-Path $instanceDir 'options.txt')) -eq $targetOptionsBefore)
$c4a.Output | Out-File -FilePath (Join-Path $dir '4a-migrate-refused.txt') -Encoding utf8

$c4 = Run-Case @('--migrate-v1', $game, '--from', 'RSTC-SMOKE-SRC', '--to', $instance, '--overwrite')
Check '迁移（显式覆盖）exit 0' ($c4.Code -eq 0) "exit=$($c4.Code)"
Check '迁移 saves' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves\world.dat'))
Check '迁移内容一致' ((Hash (Join-Path $instanceDir 'saves\world.dat')) -eq $srcHash)
Check '源未改动' ((Hash (Join-Path $src 'saves\world.dat')) -eq $srcHash)
Check 'XaeroWaypoints_A 已迁移' (Test-Path -LiteralPath (Join-Path $instanceDir 'XaeroWaypoints_A\a.txt'))
Check 'XaeroWaypoints_B 已迁移' (Test-Path -LiteralPath (Join-Path $instanceDir 'XaeroWaypoints_B\b.txt'))
$migrateBackup = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory -Filter 'migrate_*' -ErrorAction SilentlyContinue)
Check '覆盖前有迁移备份' ($migrateBackup.Count -gt $backupBefore) "before=$backupBefore after=$($migrateBackup.Count)"
$c4.Output | Out-File -FilePath (Join-Path $dir '4-migrate.txt') -Encoding utf8

# ---------- 4b) R2-M1：目标只读 → 失败回滚、不崩溃 ----------
'source2 options' | Set-Content -LiteralPath (Join-Path $src 'options.txt') -Encoding utf8
$targetOptionsBefore2 = Hash (Join-Path $instanceDir 'options.txt')
Set-ItemProperty -LiteralPath (Join-Path $instanceDir 'options.txt') -Name IsReadOnly -Value $true
$c4b = Run-Case @('--migrate-v1', $game, '--from', 'RSTC-SMOKE-SRC', '--to', $instance, '--categories', 'settings', '--overwrite')
Check '只读目标迁移失败 exit 2（不崩溃）' ($c4b.Code -eq 2) "exit=$($c4b.Code)"
Check '失败后目标已回滚' ((Hash (Join-Path $instanceDir 'options.txt')) -eq $targetOptionsBefore2)
Check '失败摘要含已回滚' ($c4b.Output -match '已回滚')
Set-ItemProperty -LiteralPath (Join-Path $instanceDir 'options.txt') -Name IsReadOnly -Value $false
$c4b.Output | Out-File -FilePath (Join-Path $dir '4b-migrate-readonly.txt') -Encoding utf8

# ---------- 5) 自更新检查 ----------
$c5 = Run-Case @('--check-self')
Check '自更新检查' ($c5.Code -eq 0) "exit=$($c5.Code)"
$c5.Output | Out-File -FilePath (Join-Path $dir '5-check-self.txt') -Encoding utf8

# ---------- 6) 状态 + 关键文件检查 ----------
$c6 = Run-Case @('--status', $game)
Check '状态/关键文件' ($c6.Code -eq 0) "exit=$($c6.Code)"
Check '状态输出含实例' ($c6.Output -match [regex]::Escape($instance))
$c6.Output | Out-File -FilePath (Join-Path $dir '6-status.txt') -Encoding utf8

# ---------- 证据 ----------
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
