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

$script:checks = 0
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
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
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# ---------- 场景 1：安装 v1 -> 加玩家文件 ----------
Remove-Item -LiteralPath (Join-Path $game 'rstc-backup') -Recurse -Force -ErrorAction SilentlyContinue
Reset-Instance 'RSTC-0-5'
$i1 = Join-Path $game 'versions\RSTC-0-5'
$c1 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5', '--tag', 'v20260712-103024')
Check '安装 pack-min' ($c1.Code -eq 0 -and $c1.Output -match 'PACK_APPLY=PASS') "exit=$($c1.Code)"
Check 'RSTC_version.txt 写 Tag' ((Get-Content -LiteralPath (Join-Path $i1 'RSTC_version.txt') -Raw -Encoding utf8).Trim() -eq 'v20260712-103024')
$c1.Output | Out-File -FilePath (Join-Path $dir 'case1.install.txt') -Encoding utf8

New-Item -ItemType Directory -Force (Join-Path $i1 'saves') | Out-Null
[byte[]](1..32) | Set-Content -LiteralPath (Join-Path $i1 'saves\test.dat') -AsByteStream
[byte[]](33..64) | Set-Content -LiteralPath (Join-Path $i1 'mods\mytest.jar') -AsByteStream
'player local options' | Set-Content -LiteralPath (Join-Path $i1 'options.txt') -Encoding utf8

# ---------- 场景 2：D2 删除预览（dry-run，不得写盘） ----------
$optsHashBefore = Hash (Join-Path $i1 'options.txt')
$dry = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5', '--update', '--dry-run')
Check 'dry-run exit 0' ($dry.Code -eq 0) "exit=$($dry.Code)"
Check 'dry-run 预览 config/a.txt' ($dry.Output -match 'DELETE\s+config/a\.txt')
Check 'dry-run 预览 mods/mytest.jar' ($dry.Output -match 'DELETE\s+mods/mytest\.jar')
Check 'dry-run 不写盘（a.txt 仍在）' (Test-Path -LiteralPath (Join-Path $i1 'config\a.txt'))
Check 'dry-run 不写盘（b.txt 未创建）' (-not (Test-Path -LiteralPath (Join-Path $i1 'config\b.txt')))
Check 'dry-run 不写盘（options 未被改）' ((Hash (Join-Path $i1 'options.txt')) -eq $optsHashBefore)
$dry.Output | Out-File -FilePath (Join-Path $dir 'case2.dryrun.txt') -Encoding utf8

# ---------- 场景 3：真实更新 v2（默认策略） ----------
$c2 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5', '--update')
Check '更新 pack-min-v2' ($c2.Code -eq 0 -and $c2.Output -match 'PACK_APPLY=PASS') "exit=$($c2.Code)"
$c2.Output | Out-File -FilePath (Join-Path $dir 'case3.update.txt') -Encoding utf8
Check '删除 config/a.txt' (-not (Test-Path -LiteralPath (Join-Path $i1 'config\a.txt')))
Check '新增 config/b.txt' (Test-Path -LiteralPath (Join-Path $i1 'config\b.txt'))
Check '删除 mods/min.jar' (-not (Test-Path -LiteralPath (Join-Path $i1 'mods\min.jar')))
Check '新增 mods/min2.jar' (Test-Path -LiteralPath (Join-Path $i1 'mods\min2.jar'))
Check '玩家自加 mod 默认删除' (-not (Test-Path -LiteralPath (Join-Path $i1 'mods\mytest.jar')))
Check 'saves/test.dat 保留' (Test-Path -LiteralPath (Join-Path $i1 'saves\test.dat'))
Check 'options.txt 保留本地（冲突）' ((Get-Content -LiteralPath (Join-Path $i1 'options.txt') -Raw -Encoding utf8) -match 'player local options')
$journal = Get-Content -LiteralPath (Join-Path $i1 '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check 'journal status=done' ($journal.status -eq 'done')
Check 'journal 记录 backupPath' ([bool]$journal.backupPath)
$latest = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory | Sort-Object Name)[-1]
Check '备份含被删 config/a.txt' (Test-Path -LiteralPath (Join-Path $latest.FullName 'files\config\a.txt'))
Check '备份含被删 mods/min.jar' (Test-Path -LiteralPath (Join-Path $latest.FullName 'files\mods\min.jar'))
Check '备份含被删 mytest.jar' (Test-Path -LiteralPath (Join-Path $latest.FullName 'files\mods\mytest.jar'))

# ---------- 场景 4：--keep 保留 ----------
Reset-Instance 'RSTC-0-5-keep'
$i2 = Join-Path $game 'versions\RSTC-0-5-keep'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-keep')
[byte[]](99..120) | Set-Content -LiteralPath (Join-Path $i2 'mods\mytest.jar') -AsByteStream
$c3 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-keep', '--update', '--keep', 'mods/mytest.jar')
Check '更新 + --keep' ($c3.Code -eq 0) "exit=$($c3.Code)"
Check '--keep 保留 mods/mytest.jar' (Test-Path -LiteralPath (Join-Path $i2 'mods\mytest.jar'))
$c3.Output | Out-File -FilePath (Join-Path $dir 'case4.keep.txt') -Encoding utf8

# ---------- 场景 5：--options pack ----------
Reset-Instance 'RSTC-0-5-optpack'
$i3 = Join-Path $game 'versions\RSTC-0-5-optpack'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-optpack')
'player local options' | Set-Content -LiteralPath (Join-Path $i3 'options.txt') -Encoding utf8
$c4 = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-optpack', '--update', '--options', 'pack')
Check '更新 + --options pack' ($c4.Code -eq 0) "exit=$($c4.Code)"
Check 'options 采用包内版本' ((Get-Content -LiteralPath (Join-Path $i3 'options.txt') -Raw -Encoding utf8) -match 'options v2 changed')
$c4.Output | Out-File -FilePath (Join-Path $dir 'case5.optpack.txt') -Encoding utf8

# ---------- 场景 6：D1 中断（删除阶段后）+ journal 恢复 + 回滚（可证伪） ----------
Reset-Instance 'RSTC-0-5-fail'
$ifail = Join-Path $game 'versions\RSTC-0-5-fail'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-fail')
[byte[]](9..40) | Set-Content -LiteralPath (Join-Path $ifail 'mods\mytest.jar') -AsByteStream
$preA = Hash (Join-Path $ifail 'config\a.txt')
$preMin = Hash (Join-Path $ifail 'mods\min.jar')
$preMy = Hash (Join-Path $ifail 'mods\mytest.jar')
$env:RSTC_STAGE0_TEST = '1'
$env:RSTC_STAGE0_FAIL_AFTER_DELETES = '1'
$interrupted = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-fail', '--update')
$env:RSTC_STAGE0_TEST = $null
$env:RSTC_STAGE0_FAIL_AFTER_DELETES = $null
Check '注入中断后 exit 7' ($interrupted.Code -eq 7) "exit=$($interrupted.Code)"
Check '中断时确实执行了删除（a.txt 已不在）' (-not (Test-Path -LiteralPath (Join-Path $ifail 'config\a.txt')))
Check '中断时新文件 b.txt 已写入' (Test-Path -LiteralPath (Join-Path $ifail 'config\b.txt'))
$jfail = Get-Content -LiteralPath (Join-Path $ifail '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check '中断后 journal status=writing' ($jfail.status -eq 'writing') "status=$($jfail.status)"
$interrupted.Output | Out-File -FilePath (Join-Path $dir 'case6.interrupted.txt') -Encoding utf8

$rb = Run-Case @('--rollback', $game, '--instance', 'RSTC-0-5-fail')
Check '回滚 exit 0' ($rb.Code -eq 0) "exit=$($rb.Code)"
Check '回滚恢复 config/a.txt 且内容一致' ((Test-Path -LiteralPath (Join-Path $ifail 'config\a.txt')) -and ((Hash (Join-Path $ifail 'config\a.txt')) -eq $preA))
Check '回滚恢复 mods/min.jar 且内容一致' ((Test-Path -LiteralPath (Join-Path $ifail 'mods\min.jar')) -and ((Hash (Join-Path $ifail 'mods\min.jar')) -eq $preMin))
Check '回滚恢复 mods/mytest.jar 且内容一致' ((Test-Path -LiteralPath (Join-Path $ifail 'mods\mytest.jar')) -and ((Hash (Join-Path $ifail 'mods\mytest.jar')) -eq $preMy))
Check '回滚后 config/b.txt 删除' (-not (Test-Path -LiteralPath (Join-Path $ifail 'config\b.txt')))
Check '回滚后 mods/min2.jar 删除' (-not (Test-Path -LiteralPath (Join-Path $ifail 'mods\min2.jar')))
$jrb = Get-Content -LiteralPath (Join-Path $ifail '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check '回滚后 journal status=rolledback' ($jrb.status -eq 'rolledback') "status=$($jrb.status)"
$rb.Output | Out-File -FilePath (Join-Path $dir 'case6.rollback.txt') -Encoding utf8

$recover = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-fail', '--update')
Check '回滚后重跑更新成功' ($recover.Code -eq 0 -and $recover.Output -match 'PACK_APPLY=PASS') "exit=$($recover.Code)"

# ---------- 场景 7：D3 备份保留 3 份（干净目录，4 次产生备份的更新） ----------
Remove-Item -LiteralPath (Join-Path $game 'rstc-backup') -Recurse -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 4; $i++) {
    $zip = if ($i % 2 -eq 0) { 'pack-min.zip' } else { 'pack-min-v2.zip' }
    $r = Run-Case @('--pack', $game, '--zip', (Join-Path $packs $zip), '--instance', 'RSTC-0-5', '--update')
    if ($r.Code -ne 0) { Check "第 $($i+1) 次更新" $false "exit=$($r.Code)" }
}
$count = @(Get-ChildItem -LiteralPath (Join-Path $game 'rstc-backup') -Directory).Count
Check '备份保留 3 份（4 次真实覆盖后）' ($count -eq 3) "count=$count"

# ---------- 场景 8：H5 备份缺失/失败 journal 的回滚与 Trim 保护 ----------
Reset-Instance 'RSTC-0-5-missing'
$imiss = Join-Path $game 'versions\RSTC-0-5-missing'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min.zip'), '--instance', 'RSTC-0-5-missing')
$env:RSTC_STAGE0_TEST = '1'
$env:RSTC_STAGE0_FAIL_AFTER_DELETES = '1'
$null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs 'pack-min-v2.zip'), '--instance', 'RSTC-0-5-missing', '--update')
$env:RSTC_STAGE0_TEST = $null
$env:RSTC_STAGE0_FAIL_AFTER_DELETES = $null
$jmiss = Get-Content -LiteralPath (Join-Path $imiss '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
$failedBackup = $jmiss.backupPath
Check '失败 journal 记录了 backupPath' ([bool]$failedBackup)
# 删掉一个备份文件，模拟备份丢失
$backupVictim = Join-Path $failedBackup 'files\config\a.txt'
if (Test-Path -LiteralPath $backupVictim) { Remove-Item -LiteralPath $backupVictim -Force }
$rbMiss = Run-Case @('--rollback', $game, '--instance', 'RSTC-0-5-missing')
Check '备份缺失时回滚拒绝（exit 7）' ($rbMiss.Code -eq 7) "exit=$($rbMiss.Code)"
Check '备份缺失时输出“拒绝回滚”' ($rbMiss.Output -match '备份缺失，拒绝回滚')
$jmiss2 = Get-Content -LiteralPath (Join-Path $imiss '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check '备份缺失时 journal 保持 writing（未标 rolledback）' ($jmiss2.status -eq 'writing') "status=$($jmiss2.status)"
$rbMiss.Output | Out-File -FilePath (Join-Path $dir 'case8.rollback-missing.txt') -Encoding utf8

# Trim 保护：在其它实例上再产生 4 个备份，失败 journal 的备份不得被删除
for ($i = 0; $i -lt 4; $i++) {
    $zip = if ($i % 2 -eq 0) { 'pack-min.zip' } else { 'pack-min-v2.zip' }
    $null = Run-Case @('--pack', $game, '--zip', (Join-Path $packs $zip), '--instance', 'RSTC-0-5-missing', '--update')
}
Check '失败 journal 的备份未被 Trim 删除' (Test-Path -LiteralPath $failedBackup)

# 非法实例名回滚必须被拒绝
$badName = Run-Case @('--rollback', $game, '--instance', '..\..\evil')
Check '非法实例名回滚被拒（exit 2）' ($badName.Code -eq 2) "exit=$($badName.Code)"

# ---------- 证据 ----------
$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
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
Write-Host 'PACK_0_5=PASS' -ForegroundColor Green
exit 0
