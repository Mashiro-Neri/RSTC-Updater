#Requires -Version 7.0
<#
阶段 0-4：真实 RSTC-5.5 包完整链路：下载（更新器自身下载器）→ sha256 校验 → 解压/写入 → 自检 → 幂等/玩家数据。
Release 元数据用 gh api 取（避免未认证 REST 限流），下载与校验由 RstcUpdater --fetch-pack 完成。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-pack-0-4.ps1 -GameDir <测试 .minecraft>
断言失败 exit 1；证据写 evidence/stage0/pack-0-4/<commit>/。
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$game = [System.IO.Path]::GetFullPath($GameDir)
$repo = 'Mashiro-Neri/Redstone-Town-Client_update_modpack'
$tag = 'v20260922-001655'
$instanceName = 'RSTC-5.5'
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\pack-0-4\$commit"
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

# 1. 取 Release 元数据
$releaseJson = Join-Path $dir 'release.json'
gh api "repos/$repo/releases/tags/$tag" | Out-File -FilePath $releaseJson -Encoding utf8
Check 'Release 元数据已取' (Test-Path -LiteralPath $releaseJson)

# 2. 清理实例与旧 zip
$instanceDir = Join-Path $game "versions\$instanceName"
if (Test-Path -LiteralPath $instanceDir) { Remove-Item -LiteralPath $instanceDir -Recurse -Force }
$tmpDir = Join-Path $game '.rstc-tmp'
if (Test-Path -LiteralPath $tmpDir) { Remove-Item -LiteralPath $tmpDir -Recurse -Force }

# 3. fetch-pack：下载 → sha256 校验 → 应用
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$c1 = Run-Case @('--fetch-pack', $game, '--release-json', $releaseJson, '--instance', $instanceName, '--keep-zip')
$sw.Stop()
Check 'fetch-pack exit 0' ($c1.Code -eq 0) "exit=$($c1.Code) elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Check '下载后 sha256 校验通过' ($c1.Output -match 'sha256 一致')
Check '应用 PASS' ($c1.Output -match 'PACK_APPLY=PASS')
$c1.Output | Out-File -FilePath (Join-Path $dir 'fetch-install.txt') -Encoding utf8

$zipPath = Join-Path $tmpDir 'RSTC-5.5.zip'
Check 'zip 已保留（--keep-zip）' (Test-Path -LiteralPath $zipPath)
$zipInfo = Get-Item -LiteralPath $zipPath
Check 'zip 大小 = 609,837,724' ($zipInfo.Length -eq 609837724) "size=$($zipInfo.Length)"
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Check 'zip sha256 与 Release digest 一致' ($zipSha -eq 'CDA10E2DC9A4B61A9A96FD18E5CCCBAB8D1705C76CE837F746E76CDA4341085F') "sha=$zipSha"

# 4. 结构与版本状态
Check 'RSTC_version.txt = Tag' ((Get-Content -LiteralPath (Join-Path $instanceDir 'RSTC_version.txt') -Raw -Encoding utf8).Trim() -eq $tag)
Check 'PCL/ 未被安装' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'PCL')))
$installed = @(Get-ChildItem -LiteralPath $instanceDir -Recurse -File |
    Where-Object { $_.Name -notin @('modrinth.index.json', 'RSTC_version.txt', '.rstc-journal.json') })
Check '安装文件数 = 422' ($installed.Count -eq 422) "installed=$($installed.Count)"
$mods = @(Get-ChildItem -LiteralPath (Join-Path $instanceDir 'mods') -File -Filter *.jar)
Check 'mods 数量 = 127' ($mods.Count -eq 127) "mods=$($mods.Count)"
$journal = Get-Content -LiteralPath (Join-Path $instanceDir '.rstc-journal.json') -Raw -Encoding utf8 | ConvertFrom-Json
Check 'journal status=done' ($journal.status -eq 'done')

# 5. 内容校验：逐个 overrides 条目对比 zip 内 SHA256 与实例文件 SHA256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$mismatch = 0
$checked = 0
$sha = [System.Security.Cryptography.SHA256]::Create()
foreach ($entry in $archive.Entries) {
    $full = $entry.FullName.Replace('\\', '/')
    if (-not $full.StartsWith('overrides/') -or $full.EndsWith('/')) { continue }
    $rel = $full.Substring('overrides/'.Length)
    if ($rel.StartsWith('PCL/')) { continue }
    $dest = Join-Path $instanceDir ($rel.Replace('/', '\\'))
    if (-not (Test-Path -LiteralPath $dest)) { $mismatch++; continue }
    $s1 = $entry.Open()
    $h1 = $sha.ComputeHash($s1)
    $s1.Dispose()
    $s2 = [System.IO.File]::OpenRead($dest)
    $h2 = $sha.ComputeHash($s2)
    $s2.Dispose()
    if ([System.BitConverter]::ToString($h1) -ne [System.BitConverter]::ToString($h2)) { $mismatch++ }
    $checked++
}
$archive.Dispose()
$sha.Dispose()
Check '逐文件内容 SHA256 一致' ($mismatch -eq 0) "checked=$checked mismatch=$mismatch"

# 6. 幂等更新（复用）
$c2 = Run-Case @('--pack', $game, '--zip', $zipPath, '--instance', $instanceName, '--update')
Check '幂等更新 exit 0' ($c2.Code -eq 0) "exit=$($c2.Code)"
Check '幂等更新复用 422' ($c2.Output -match '复用（内容一致不重写）[\s\S]{0,80}?422')
$c2.Output | Out-File -FilePath (Join-Path $dir 'update-idempotent.txt') -Encoding utf8

# 7. 玩家数据保护
New-Item -ItemType Directory -Force (Join-Path $instanceDir 'saves') | Out-Null
[byte[]](1..32) | Set-Content -LiteralPath (Join-Path $instanceDir 'saves\test.dat') -AsByteStream
[byte[]](33..64) | Set-Content -LiteralPath (Join-Path $instanceDir 'mods\player-added.jar') -AsByteStream
$c3 = Run-Case @('--pack', $game, '--zip', $zipPath, '--instance', $instanceName, '--update')
Check '带玩家数据更新 exit 0' ($c3.Code -eq 0) "exit=$($c3.Code)"
Check 'saves/test.dat 保留' (Test-Path -LiteralPath (Join-Path $instanceDir 'saves\test.dat'))
Check '玩家自加 mod 默认删除' (-not (Test-Path -LiteralPath (Join-Path $instanceDir 'mods\player-added.jar')))
$c3.Output | Out-File -FilePath (Join-Path $dir 'update-with-player-data.txt') -Encoding utf8

# 8. 体积与峰值空间
$instSize = (Get-ChildItem -LiteralPath $instanceDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$peakExtra = $zipInfo.Length + $instSize
$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$game"
$facts += "release_tag=$tag"
$facts += "zip_bytes=$($zipInfo.Length)"
$facts += "zip_sha256=$zipSha"
$facts += "installed_files=$($installed.Count)"
$facts += "mods=$($mods.Count)"
$facts += "content_checked=$checked"
$facts += "content_mismatch=$mismatch"
$facts += "instance_bytes=$instSize"
$facts += "peak_estimate_bytes=$peakExtra"
$facts += "fetch_and_apply_seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))"
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
Write-Host 'PACK_0_4=PASS' -ForegroundColor Green
exit 0
