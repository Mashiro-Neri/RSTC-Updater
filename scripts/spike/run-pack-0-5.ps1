#Requires -Version 7.0
<#
阶段 0-5：pack-min / pack-min-v2 更新链路验收（A6/A7/D1/D2/D3 的离线等价场景）。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-pack-0-5.ps1 -GameDir <测试 .minecraft>
断言失败 exit 1；证据写到 evidence/stage0/pack-0-5/<commit>/。
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$game = [System.IO.Path]::GetFullPath($GameDir)
$packs = Join-Path $RepoRoot 'evidence\stage0\fixtures'
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\pack-0-5\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "[OK]   $name $detail" -ForegroundColor Green }
    else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $failures.Add("$name $detail") }
}
function Run-Case([string[]]$argv) {
    $out = & $exe @argv 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; Code = $LASTEXITCODE }
}
function Reset-Instance([string]$name) {
    $p = Join-Path $game "versions\$name"
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

# ---------- 场景 1：安装 v1 -> 加玩家文件 -> 更新 v2（默认策略） ----------
Reset-Instance 'RSTC-0-5'
$i1 = Join-Path $game 'versions\RSTC-0-5'
$c1 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5')
Check '安装 pack-min' ($c1.Code -eq 0 -and $c1.Output -match 'PACK_APPLY=PASS') "exit=$($c1.Code)"
$c1.Output | Out-File -FilePath (Join-Path $dir 'case1.install.txt') -Encoding utf8

New-Item -ItemType Directory -Force (Join-Path $i1 'saves') | Out-Null
[byte[]](1..32) | Set-Content -LiteralPath (Join-Path $i1 'saves\test.dat') -AsByteStream
[byte[]](33..64) | Set-Content -LiteralPath (Join-Path $i1 'mods\mytest.jar') -AsByteStream
'player local options' | Set-Content -LiteralPath (Join-Path $i1 'options.txt') -Encoding utf8

$c2 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5', '--update')
Check '更新 pack-min-v2' ($c2.Code -eq 0 -and $c2.Output -match 'PACK_APPLY=PASS') "exit=$($c2.Code)"
$c2.Output | Out-File -FilePath (Join-Path $dir 'case2.update.txt') -Encoding utf8

Check '删除 config/a.txt' (-not (Test-Path -LiteralPath (Join-Path $i1 'config\a.txt')))
Check '新增 config/b.txt' (Test-Path -LiteralPath (Join-Path $i1 'config\b.txt'))
Check '删除 mods/min.jar' (-not (Test-Path -LiteralPath (Join-Path $i1 'mods\min.jar')))
Check '新增 mods/min2.jar' (Test-Path -LiteralPath (Join-Path $i1 'mods\min2.jar'))
Check '玩家自加 mod 默认删除' (-not (Test-Path -LiteralPath (Join-Path $i1 'mods\mytest.jar')))
Check 'saves/test.dat 保留' (Test-Path -LiteralPath (Join-Path $i1 'saves\test.dat'))
Check 'options.txt 保留本地（冲突）' ((Get-Content -LiteralPath (Join-Path $i1 'options.txt') -Raw -Encoding utf8) -match 'player local options')
Check '版本状态文件' ((Get-Content -LiteralPath (Join-Path $i1 'RSTC_version.txt') -Raw -Encoding utf8).Trim() -eq '6.0-min2')
$journal = Get-Content -LiteralPath (Join-Path $i1 '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check 'journal status=done' ($journal.status -eq 'done')

$backups = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory | Sort-Object Name)
$latest = $backups[-1]
Check '备份含被删 config/a.txt' (Test-Path -LiteralPath (Join-Path $latest 'files\config\a.txt'))
Check '备份含被删 mods/min.jar' (Test-Path -LiteralPath (Join-Path $latest 'files\mods\min.jar'))
Check '备份含被删 mytest.jar' (Test-Path -LiteralPath (Join-Path $latest 'files\mods\mytest.jar'))

# ---------- 场景 2：--keep 保留玩家文件 ----------
Reset-Instance 'RSTC-0-5-keep'
$i2 = Join-Path $game 'versions\RSTC-0-5-keep'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-keep')
[byte[]](99..120) | Set-Content -LiteralPath (Join-Path $i2 'mods\mytest.jar') -AsByteStream
$c3 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-keep', '--update', '--keep', 'mods/mytest.jar')
Check '更新 + --keep' ($c3.Code -eq 0) "exit=$($c3.Code)"
Check '--keep 保留 mods/mytest.jar' (Test-Path -LiteralPath (Join-Path $i2 'mods\mytest.jar'))
$c3.Output | Out-File -FilePath (Join-Path $dir 'case3.keep.txt') -Encoding utf8

# ---------- 场景 3：--options pack 覆盖本地 ----------
Reset-Instance 'RSTC-0-5-optpack'
$i3 = Join-Path $game 'versions\RSTC-0-5-optpack'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-optpack')
'player local options' | Set-Content -LiteralPath (Join-Path $i3 'options.txt') -Encoding utf8
$c4 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-optpack', '--update', '--options', 'pack')
Check '更新 + --options pack' ($c4.Code -eq 0) "exit=$($c4.Code)"
Check 'options 采用包内版本' ((Get-Content -LiteralPath (Join-Path $i3 'options.txt') -Raw -Encoding utf8) -match 'options v2 changed')
$c4.Output | Out-File -FilePath (Join-Path $dir 'case4.optpack.txt') -Encoding utf8

# ---------- 场景 4：备份保留 3 份 ----------
for ($i = 0; $i -lt 3; $i++) {
    $null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5', '--update')
}
$count = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory).Count
Check '备份保留 3 份' ($count -eq 3) "count=$count"

# ---------- 证据 ----------
$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'powershell=' + $PSVersionTable.PSVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts += "failures=$($failures.Count)"
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8
Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notlike 'SHA256SUMS*' } |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } |
    Out-File -FilePath (Join-Path $dir 'SHA256SUMS.evidence.txt') -Encoding ascii

Write-Host "evidence: $dir"
if ($failures.Count -gt 0) {
    Write-Host ("FAILED: " + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host 'PACK_0_5=PASS' -ForegroundColor Green
exit 0
