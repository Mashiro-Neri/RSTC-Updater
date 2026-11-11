# 校验 CmlLib 安装 26.3 的结果（阶段 0-1，L2 安装的配套校验）
# 用法: .\verify-install-26.3.ps1 -GameDir <临时 .minecraft 路径>
param(
    [Parameter(Mandatory = $true)][string]$GameDir
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red; exit 1 }
function Ok([string]$msg) { Write-Host "[OK]   $msg" -ForegroundColor Green }

if (-not (Test-Path -LiteralPath $GameDir)) { Fail "game dir not found: $GameDir" }
Write-Host "game dir: $((Resolve-Path -LiteralPath $GameDir).Path)"

$vj = Join-Path $GameDir 'versions\26.3\26.3.json'
if (-not (Test-Path -LiteralPath $vj)) { Fail "missing version json: $vj" }
Ok 'version json exists'
$vjson = Get-Content -LiteralPath $vj -Raw -Encoding utf8 | ConvertFrom-Json
if ($vjson.id -ne '26.3') { Fail "version json id = $($vjson.id)" }
Ok "version json id = 26.3"

$jar = Join-Path $GameDir 'versions\26.3\26.3.jar'
if (-not (Test-Path -LiteralPath $jar)) { Fail "missing client jar: $jar" }
$jarSize = (Get-Item -LiteralPath $jar).Length
if ($jarSize -ne 41483720) { Fail "client jar size $jarSize != 41483720" }
Ok 'client jar size = 41,483,720 B'

$ai = Join-Path $GameDir 'assets\indexes\34.json'
if (-not (Test-Path -LiteralPath $ai)) { Fail "missing asset index: $ai" }
$idx = Get-Content -LiteralPath $ai -Raw -Encoding utf8 | ConvertFrom-Json
$count = ($idx.objects.PSObject.Properties | Measure-Object).Count
if ($count -ne 5149) { Fail "asset objects $count != 5149" }
Ok "asset index 34 with $count objects"

$assetDir = Join-Path $GameDir 'assets\objects'
$assetFiles = @(Get-ChildItem -LiteralPath $assetDir -Recurse -File -ErrorAction SilentlyContinue)
if ($assetFiles.Count -eq 0) { Fail "assets/objects is empty" }
Ok "assets/objects: $($assetFiles.Count) files"

# 26.3 按 Windows 客户端规则过滤后是 74 个 artifact（2026-11-11 实测），不是版本 json 的 114 个总数
$libs = @(Get-ChildItem -LiteralPath (Join-Path $GameDir 'libraries') -Recurse -File -Filter *.jar -ErrorAction SilentlyContinue)
if ($libs.Count -lt 74) { Fail "libraries jar count $($libs.Count) < 74" }
Ok "libraries: $($libs.Count) jars"

$total = (Get-ChildItem -LiteralPath $GameDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Ok ("total size: {0:N1} MB" -f ($total / 1MB))

Write-Host 'VERIFY=PASS' -ForegroundColor Green
