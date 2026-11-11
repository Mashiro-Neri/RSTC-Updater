#Requires -Version 7.0
<#
阶段 0-4：用真实 RSTC-5.5.zip（约 580 MiB）跑 pack 安装链路。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-pack-0-4.ps1 -GameDir <测试 .minecraft> -ZipPath <RSTC-5.5.zip>
断言失败 exit 1；证据写到 evidence/stage0/pack-0-4/<commit>/。
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$game = [System.IO.Path]::GetFullPath($GameDir)
$zip = [System.IO.Path]::GetFullPath($ZipPath)
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\pack-0-4\$commit"
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

Check 'zip 存在' (Test-Path -LiteralPath $zip) $zip
$zipInfo = Get-Item -LiteralPath $zip
Write-Host ("zip size = {0:N0} bytes" -f $zipInfo.Length)

# 从 zip 读 overrides 统计（与包一致性的基准）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
$overrideFiles = @($archive.Entries | Where-Object { $_.FullName -like 'overrides/*' -and $_.Name -ne '' })
$pclEntries = @($overrideFiles | Where-Object { $_.FullName -like 'overrides/PCL/*' })
$expectedInstalled = $overrideFiles.Count - $pclEntries.Count
$indexEntry = $archive.Entries | Where-Object { $_.FullName -eq 'modrinth.index.json' } | Select-Object -First 1
$topLevels = @($overrideFiles | ForEach-Object { ($_.FullName.Substring('overrides/'.Length) -split '/')[0] } | Sort-Object -Unique)
Write-Host "overrides entries = $($overrideFiles.Count); PCL/ entries = $($pclEntries.Count); top-level = $($topLevels -join ', ')"
$archive.Dispose()

$instanceName = 'RSTC-5.5'
$instanceDir = Join-Path $game "versions\$instanceName"
if (Test-Path -LiteralPath $instanceDir) { Remove-Item -LiteralPath $instanceDir -Recurse -Force }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$c1 = Run-Case @('--pack', $game, '--zip', $zip, '--instance', $instanceName)
$sw.Stop()
Check '安装 5.5' ($c1.Code -eq 0 -and $c1.Output -match 'PACK_APPLY=PASS') "exit=$($c1.Code) elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"
$c1.Output | Out-File -FilePath (Join-Path $dir 'install.txt') -Encoding utf8

# 结构断言
$installed = @(Get-ChildItem -LiteralPath $instanceDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin @('modrinth.index.json', 'RSTC_version.txt', '.rstc-journal.json') })
Check '实例文件数 = overrides - PCL/' ($installed.Count -eq $expectedInstalled) "installed=$($installed.Count) expect=$expectedInstalled"
Check 'PCL/ 未被安装（用户要求跳过）' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'PCL')))
Check 'index 版本 5.5' ((Get-Content -LiteralPath (Join-Path $instanceDir 'modrinth.index.json') -Raw -Encoding utf8 | ConvertFrom-Json).versionId -eq '5.5')
Check 'RSTC_version.txt = 5.5' ((Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim() -eq '5.5')
Check 'options.txt 存在' (Test-Path -LiteralPath (Join-Path $instanceDir 'options.txt'))
Check 'servers.dat 存在' (Test-Path -LiteralPath (Join-Path $instanceDir 'servers.dat'))
$mods = @(Get-ChildItem -LiteralPath (Join-Path $instanceDir 'mods') -File -Filter *.jar -ErrorAction SilentlyContinue)
Check 'mods 数量 > 0' ($mods.Count -gt 0) "mods=$($mods.Count)"
$journal = Get-Content -LiteralPath (Join-Path $instanceDir '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check 'journal status=done' ($journal.status -eq 'done')

# 幂等：同包再更新一次
$c2 = Run-Case @('--pack', $game, '--zip', $zip, '--instance', $instanceName, '--update')
Check '同包幂等更新' ($c2.Code -eq 0 -and $c2.Output -match 'PACK_APPLY=PASS') "exit=$($c2.Code)"
Check '幂等更新复用全部文件（不重写）' ($c2.Output -match '复用（内容一致不重写）[\s\S]{0,80}?422') 'expect reused=422'
$c2.Output | Out-File -FilePath (Join-Path $dir 'update-idempotent.txt') -Encoding utf8

# 玩家数据保护：加 saves 与玩家 mod 后再更新
New-Item -ItemType Directory -Force (Join-Path $instanceDir 'saves') | Out-Null
[byte[]](1..32) | Set-Content -LiteralPath (Join-Path $instanceDir 'saves\test.dat') -AsByteStream
[byte[]](33..64) | Set-Content -LiteralPath (Join-Path $instanceDir 'mods\player-added.jar') -AsByteStream
$c3 = Run-Case @('--pack', $game, '--zip', $zip, '--instance', $instanceName, '--update')
Check '带玩家数据更新' ($c3.Code -eq 0) "exit=$($c3.Code)"
$c3.Output | Out-File -FilePath (Join-Path $dir 'update-with-player-data.txt') -Encoding utf8
Check 'saves/test.dat 保留' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves\test.dat'))
Check '玩家自加 mod 被删（默认策略）' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'mods\player-added.jar')))

# 体积与耗时
$instSize = (Get-ChildItem -LiteralPath $instanceDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += "zip=$zip"
$facts += "zip_bytes=$($zipInfo.Length)"
$facts += "override_entries=$($overrideFiles.Count)"
$facts += "pcl_entries_skipped=$($pclEntries.Count)"
$facts += "expected_installed=$expectedInstalled"
$facts += "top_levels=$($topLevels -join ',')"
$facts += "installed_files=$($installed.Count)"
$facts += "mods=$($mods.Count)"
$facts += "instance_bytes=$instSize"
$facts += "install_seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))"
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
Write-Host 'PACK_0_4=PASS' -ForegroundColor Green
exit 0
