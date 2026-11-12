#Requires -Version 7.0
<#
语言兼容回归（PCL2 语言 quirk）：
  1) 新装实例 json 必须含 clientVersion=26.3 且存在 saves/
  2) 删掉 clientVersion/saves 后强制更新，更新器应自动补回（备份 .rstc-bak 不含 clientVersion）
  3) 二次更新幂等：不重复修补、json 哈希不变
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\v1\language-smoke.ps1 -GameDir <已有 26.3 的临时 .minecraft>
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
$dir = Join-Path $RepoRoot "evidence\v1\language-smoke\$commit"
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

$instance = 'RSTC-LANG-TEST'
$instanceDir = Join-Path $game "versions\$instance"
if (Test-Path -LiteralPath $instanceDir) { Remove-Item -LiteralPath $instanceDir -Recurse -Force }

# 1) 新装：必须带 clientVersion + saves
$c1 = Run-Case @('--install-v1', $game, '--instance', $instance, '--zip', $PackV1)
Check '新装 exit 0' ($c1.Code -eq 0) "exit=$($c1.Code)"
$jsonPath = Join-Path $instanceDir "$instance.json"
$j1 = Get-Content -LiteralPath $jsonPath -Raw -Encoding utf8 | ConvertFrom-Json
Check '新装 json 含 clientVersion=26.3' ($j1.clientVersion -eq '26.3') "clientVersion=$($j1.clientVersion)"
Check '新装存在 saves/' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves'))
$c1.Output | Out-File -FilePath (Join-Path $dir '1-install.txt') -Encoding utf8

# 2) 模拟旧实例：删掉 clientVersion 与 saves，强制更新补回
$node = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($jsonPath))
$obj = [System.Text.Json.Nodes.JsonObject]$node
$obj.Remove('clientVersion')
[System.IO.File]::WriteAllText($jsonPath, $obj.ToJsonString(), (New-Object System.Text.UTF8Encoding($false)))
if (Test-Path -LiteralPath (Join-Path $instanceDir 'saves')) { Remove-Item -LiteralPath (Join-Path $instanceDir 'saves') -Recurse -Force }
$c2 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2, '--force')
Check '迁移更新 exit 0' ($c2.Code -eq 0) "exit=$($c2.Code)"
$j2 = Get-Content -LiteralPath $jsonPath -Raw -Encoding utf8 | ConvertFrom-Json
Check '更新后补回 clientVersion=26.3' ($j2.clientVersion -eq '26.3') "clientVersion=$($j2.clientVersion)"
Check '更新后 saves/ 恢复' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves'))
Check '更新时输出补丁提示' ($c2.Output -match '补 clientVersion')
$bakPath = $jsonPath + '.rstc-bak'
Check '备份文件存在' (Test-Path -LiteralPath $bakPath)
if (Test-Path -LiteralPath $bakPath) {
    $bak = Get-Content -LiteralPath $bakPath -Raw -Encoding utf8
    Check '备份内容不含 clientVersion' (-not ($bak -match 'clientVersion'))
}
$c2.Output | Out-File -FilePath (Join-Path $dir '2-migrate.txt') -Encoding utf8

# 3) 幂等：_force_ 再更新一次，不应重复修补
$hashBefore = (Get-FileHash -LiteralPath $jsonPath -Algorithm SHA256).Hash
$c3 = Run-Case @('--update-v1', $game, '--instance', $instance, '--zip', $PackV2, '--force')
Check '二次更新 exit 0' ($c3.Code -eq 0) "exit=$($c3.Code)"
Check '二次更新不重复补丁' (-not ($c3.Output -match '补 clientVersion'))
Check '二次更新 json 不变' ((Get-FileHash -LiteralPath $jsonPath -Algorithm SHA256).Hash -eq $hashBefore)
$c3.Output | Out-File -FilePath (Join-Path $dir '3-idempotent.txt') -Encoding utf8

$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += "instance=$instance"
$facts += "checks=$($script:checks)"
$facts += "failures=$($failures.Count)"
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
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
Write-Host 'V1_LANGUAGE_SMOKE=PASS' -ForegroundColor Green
exit 0
