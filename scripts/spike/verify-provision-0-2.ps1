#Requires -Version 7.0
<#
阶段 0-2：校验 Fabric profile、Fabric 库与 A2/A1/B 三份实例的结构。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\verify-provision-0-2.ps1 -GameDir <临时 .minecraft>
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir
)

$ErrorActionPreference = 'Stop'
function Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red; exit 1 }
function Ok([string]$msg) { Write-Host "[OK]   $msg" -ForegroundColor Green }

$game = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host "game dir: $game"

# 1. Fabric profile
$fabricDir = Join-Path $game 'versions\fabric-loader-0.20.1-26.3'
$fabricJson = Join-Path $fabricDir 'fabric-loader-0.20.1-26.3.json'
if (-not (Test-Path -LiteralPath $fabricJson)) { Fail "缺少 Fabric profile: $fabricJson" }
$fp = Get-Content -LiteralPath $fabricJson -Raw -Encoding utf8 | ConvertFrom-Json
if ($fp.inheritsFrom -ne '26.3') { Fail "Fabric profile inheritsFrom=$($fp.inheritsFrom)" }
Ok "Fabric profile（inheritsFrom=26.3, $($fp.libraries.Count) 库）"

# 2. Fabric 库（逐个核对 profile 里的 maven 坐标）+ intermediary
$missing = @()
foreach ($l in $fp.libraries) {
    $parts = $l.name -split ':'
    $group = $parts[0] -replace '\.', '/'
    $artifact = $parts[1]
    $ver = $parts[2]
    $classifier = if ($parts.Length -gt 3) { '-' + $parts[3] } else { '' }
    $rel = "$group/$artifact/$ver/$artifact-$ver$classifier.jar"
    $p = Join-Path $game ('libraries\' + $rel.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $p)) { $missing += $l.name }
}
if ($missing.Count -gt 0) { Fail ("缺少 Fabric 库: " + ($missing -join ', ')) }
Ok "Fabric profile $($fp.libraries.Count)/$($fp.libraries.Count) 库文件存在"
$intermediary = Join-Path $game 'libraries\net\fabricmc\intermediary\0.0.0\intermediary-0.0.0.jar'
if (-not (Test-Path -LiteralPath $intermediary)) { Fail '缺少 intermediary 占位 jar' }
Ok 'intermediary 占位 jar 存在'

# 3. 三份实例
$expect = @{
    A2 = @{ Jar = 'RSTC-0-2-A2'; Inherits = $null; Libs = 122; HasJar = $true }
    A1 = @{ Jar = '26.3'; Inherits = $null; Libs = 122; HasJar = $false }
    B  = @{ Jar = $null; Inherits = '26.3'; Libs = 8; HasJar = $false }
}
foreach ($v in @('A2', 'A1', 'B')) {
    $name = "RSTC-0-2-$v"
    $dir = Join-Path $game "versions\$name"
    $json = Join-Path $dir "$name.json"
    if (-not (Test-Path -LiteralPath $json)) { Fail "缺少实例 json: $json" }
    $d = Get-Content -LiteralPath $json -Raw -Encoding utf8 | ConvertFrom-Json
    if ($d.id -ne $name) { Fail "$v id=$($d.id)" }
    if ($d.mainClass -ne 'net.fabricmc.loader.impl.launch.knot.KnotClient') { Fail "$v mainClass=$($d.mainClass)" }
    if ($expect[$v].Jar -and $d.jar -ne $expect[$v].Jar) { Fail "$v jar=$($d.jar) 期望 $($expect[$v].Jar)" }
    if ($expect[$v].Inherits -and $d.inheritsFrom -ne $expect[$v].Inherits) { Fail "$v inheritsFrom=$($d.inheritsFrom)" }
    if (-not $expect[$v].Inherits -and $d.inheritsFrom) { Fail "$v 不应有 inheritsFrom" }
    if ($d.libraries.Count -ne $expect[$v].Libs) { Fail "$v libraries=$($d.libraries.Count) 期望 $($expect[$v].Libs)" }
    if (-not ($d.arguments.jvm -match 'FabricMcEmu')) { Fail "$v arguments.jvm 缺 FabricMcEmu" }
    $jarExists = Test-Path -LiteralPath (Join-Path $dir "$name.jar")
    if ($expect[$v].HasJar -ne $jarExists) { Fail "$v jar 存在性=$jarExists 期望 $($expect[$v].HasJar)" }
    $mods = @(Get-ChildItem -LiteralPath (Join-Path $dir 'mods') -File -Filter *.jar -ErrorAction SilentlyContinue)
    if ($mods.Count -lt 1) { Fail "$v 没有样例 mod" }
    $setup = Join-Path $dir 'PCL\Setup.ini'
    if (-not (Test-Path -LiteralPath $setup)) { Fail "$v 缺 PCL/Setup.ini" }
    if (-not (Select-String -LiteralPath $setup -Pattern 'VersionArgumentIndieV2:True' -Quiet)) { Fail "$v PCL 隔离键缺失" }
    $hmcl = Join-Path $dir 'hmclversion.cfg'
    if (-not (Test-Path -LiteralPath $hmcl)) { Fail "$v 缺 hmclversion.cfg" }
    $hc = Get-Content -LiteralPath $hmcl -Raw -Encoding utf8 | ConvertFrom-Json
    if ($hc.gameDirType -ne 1) { Fail "$v hmcl gameDirType=$($hc.gameDirType)" }
    Ok "$v（$($d.libraries.Count) 库, jar=$($d.jar), inheritsFrom=$($d.inheritsFrom), mods=$($mods.Count)）"
}

Write-Host 'VERIFY=PASS' -ForegroundColor Green
exit 0
