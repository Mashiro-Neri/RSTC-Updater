#Requires -Version 7.0
<#
V1 菜单（TUI）脚本化验收：用 --menu-script 驱动真实菜单动作。
覆盖：安装到新目录 → 检查关键文件 → 从旧实例迁移 → 检查更新器更新 → 退出。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\v1\menu-smoke.ps1 -GameDir <已有 26.3 的临时 .minecraft>
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$PackV1,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$game = [System.IO.Path]::GetFullPath($GameDir)
if (-not $PackV1) { $PackV1 = Join-Path $RepoRoot 'evidence\stage0\fixtures\pack-min.zip' }
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\v1\menu-smoke\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$script:checks = 0
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $script:checks++
    if ($ok) { Write-Host "[OK]   $name $detail" -ForegroundColor Green }
    else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $failures.Add("$name $detail") }
}

$instance = 'RSTC-MENU'
$source = 'RSTC-MENU-SRC'
foreach ($name in @($instance, $source)) {
    $p = Join-Path $game "versions\$name"
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

# 清理测试目录里其他测试残留的未完成 journal（否则菜单启动会先弹回滚提示，吃掉脚本输入）
$versionsRoot = Join-Path $game 'versions'
if (Test-Path -LiteralPath $versionsRoot) {
    foreach ($journal in Get-ChildItem -LiteralPath $versionsRoot -Recurse -Filter '.rstc-journal.json' -File -ErrorAction SilentlyContinue) {
        try {
            $status = (Get-Content -LiteralPath $journal.FullName -Raw -Encoding utf8 | ConvertFrom-Json).status
        }
        catch { $status = '' }
        if ($status -in @('writing', 'failed')) {
            # journal 位于 versions\<实例>\.rstc-journal.json，其目录就是实例目录
            $leftoverDir = $journal.DirectoryName
            Remove-Item -LiteralPath $leftoverDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "[cleanup] 移除测试残留实例 $(Split-Path -Leaf $leftoverDir)（journal=$status）"
        }
    }
}

# 迁移源：1 个存档
$srcDir = Join-Path $game "versions\$source"
New-Item -ItemType Directory -Force (Join-Path $srcDir 'saves') | Out-Null
[byte[]](1..32) | Set-Content -LiteralPath (Join-Path $srcDir 'saves\world.dat') -AsByteStream
'src options' | Set-Content -LiteralPath (Join-Path $srcDir 'options.txt') -Encoding utf8

# 独立配置目录（LastGameDir 指向测试目录）
$cfgDir = Join-Path $dir 'config'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
@{ lastGameDir = $game; lastInstance = ''; backupRetention = 3 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Encoding utf8
$env:RSTC_CONFIG_DIR = $cfgDir

# 菜单脚本输入（按菜单动作实际提问顺序）
$menuScript = Join-Path $dir 'menu-input.txt'
@(
    '安装到新目录',
    $instance,
    $PackV1,
    'y',
    '检查关键文件',
    '从旧实例迁移',
    $source,
    '存档',
    'y',
    '检查更新器更新',
    '退出'
) | Set-Content -LiteralPath $menuScript -Encoding utf8

$out = & $exe --menu-script $menuScript 2>&1 | Out-String
$code = $LASTEXITCODE
$out | Out-File -FilePath (Join-Path $dir 'menu-session.txt') -Encoding utf8

$instanceDir = Join-Path $game "versions\$instance"
Check '菜单进程 exit 0' ($code -eq 0) "exit=$code"
Check '安装完成（PACK_APPLY=PASS）' ($out -match 'PACK_APPLY=PASS')
Check '实例已创建' (Test-Path -LiteralPath $instanceDir)
Check '关键文件检查执行' ($out -match '关键文件检查')
Check '迁移完成' ($out -match '迁移完成')
Check '迁移复制 1 个存档' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves\world.dat'))
Check '自更新检查执行（表已渲染）' ($out -match '更新器自更新' -and $out -match '当前版本')
Check '正常退出（无脚本用尽错误）' ($out -notmatch '菜单脚本输入用尽')

# ---------- 负向：脚本缺失/为空/中途耗尽都应 exit 2，且不能是 CLR 异常负码 ----------
$missing = & $exe --menu-script (Join-Path $dir 'no-such-script.txt') 2>&1 | Out-String
Check '缺脚本文件 exit 2' ($LASTEXITCODE -eq 2) "exit=$LASTEXITCODE"
$emptyScript = Join-Path $dir 'menu-empty.txt'
Set-Content -LiteralPath $emptyScript -Value '' -Encoding utf8
$emptyRun = & $exe --menu-script $emptyScript 2>&1 | Out-String
Check '空脚本 exit 2' ($LASTEXITCODE -eq 2) "exit=$LASTEXITCODE"
$shortScript = Join-Path $dir 'menu-short.txt'
Set-Content -LiteralPath $shortScript -Value '检查关键文件' -Encoding utf8
$shortRun = & $exe --menu-script $shortScript 2>&1 | Out-String
Check '脚本中途耗尽 exit 2' ($LASTEXITCODE -eq 2) "exit=$LASTEXITCODE"
Check '耗尽错误信息可读' ($shortRun -match '菜单脚本输入用尽')
($missing + "`n" + $emptyRun + "`n" + $shortRun) | Out-File -FilePath (Join-Path $dir 'menu-negative.txt') -Encoding utf8

$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += "checks=$($script:checks)"
$facts += "failures=$($failures.Count)"
$facts += "time=" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
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
Write-Host 'V1_MENU_SMOKE=PASS' -ForegroundColor Green
exit 0
