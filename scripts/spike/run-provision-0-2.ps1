#Requires -Version 7.0
<#
阶段 0-2：运行 provision 并采集证据（需要 PowerShell 7+）。
用法（项目根）:
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\spike\run-provision-0-2.ps1 -GameDir <临时 .minecraft> [-ModSource <mods 目录>]
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$ModSource,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$full = [System.IO.Path]::GetFullPath($GameDir)
$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\provision-0-2\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

$argv = @('--provision', $full, '--reuse')
if ($ModSource) { $argv += @('--mod-src', $ModSource) }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $exe @argv 2>&1 | Tee-Object -FilePath (Join-Path $dir 'provision.txt') -Encoding utf8
$provisionExit = $LASTEXITCODE
$sw.Stop()

& (Join-Path $RepoRoot 'scripts\spike\verify-provision-0-2.ps1') -GameDir $full 6>&1 |
    Tee-Object -FilePath (Join-Path $dir 'verify.txt') -Encoding utf8
$verifyExit = $LASTEXITCODE

# ---------- H1：已存在的启动器配置必须保留（只补隔离键） ----------
$instA1 = Join-Path $full 'versions\RSTC-0-2-A1'
New-Item -ItemType Directory -Force (Join-Path $instA1 'PCL') | Out-Null
"CustomKey:keepme`r`nVersionArgumentIndieV2:False`r`n" | Set-Content -LiteralPath (Join-Path $instA1 'PCL\Setup.ini') -Encoding utf8
'{"usesGlobal":false,"gameDirType":0,"javaDir":"./.jre21/bin/java.exe"}' |
    Set-Content -LiteralPath (Join-Path $instA1 'hmclversion.cfg') -Encoding utf8

$c2 = & $exe --provision $full --reuse 2>&1 | Out-String
$preserveExit = $LASTEXITCODE
$setup = Get-Content -LiteralPath (Join-Path $instA1 'PCL\Setup.ini') -Raw -Encoding utf8
$hmcl = Get-Content -LiteralPath (Join-Path $instA1 'hmclversion.cfg') -Raw -Encoding utf8 | ConvertFrom-Json
$factsExtra = @()
$factsExtra += "preserve_exit=$preserveExit"
$factsExtra += "pcl_custom_key_preserved=$([bool]($setup -match 'CustomKey:keepme'))"
$factsExtra += "pcl_isolation_upserted=$([bool]($setup -match 'VersionArgumentIndieV2:True'))"
$factsExtra += "hmcl_untouched=$([bool]($hmcl.gameDirType -eq 0 -and $hmcl.javaDir -eq './.jre21/bin/java.exe'))"
Write-Host ("[PRESERVE] exit=$preserveExit customKey=$($setup -match 'CustomKey:keepme') isolation=$($setup -match 'VersionArgumentIndieV2:True') hmclUntouched=$($hmcl.gameDirType -eq 0)")

# ---------- A2/A1 条件 JVM 参数不应丢失 ----------
$vanilla = Get-Content -LiteralPath (Join-Path $full 'versions\26.3\26.3.json') -Raw -Encoding utf8 | ConvertFrom-Json
$vanillaJvmCount = @($vanilla.arguments.jvm).Count
$vanillaRuleCount = @($vanilla.arguments.jvm | Where-Object { $_ -isnot [string] }).Count
$a2 = Get-Content -LiteralPath (Join-Path $full 'versions\RSTC-0-2-A2\RSTC-0-2-A2.json') -Raw -Encoding utf8 | ConvertFrom-Json
$a2JvmCount = @($a2.arguments.jvm).Count
$a2RuleCount = @($a2.arguments.jvm | Where-Object { $_ -isnot [string] }).Count
$factsExtra += "vanilla_jvm_entries=$vanillaJvmCount"
$factsExtra += "vanilla_jvm_rule_objects=$vanillaRuleCount"
$factsExtra += "a2_jvm_entries=$a2JvmCount"
$factsExtra += "a2_jvm_rule_objects=$a2RuleCount"
Write-Host ("[JVM] vanilla=$vanillaJvmCount (rule=$vanillaRuleCount) A2=$a2JvmCount (rule=$a2RuleCount)")
if ($a2RuleCount -lt $vanillaRuleCount) { Write-Error "A2 丢失条件 JVM 参数（$a2RuleCount < $vanillaRuleCount）"; exit 1 }

# ---------- M9：preserve 测试会污染 A1 配置，复位后必须可再次 verify ----------
Remove-Item -LiteralPath (Join-Path $instA1 'PCL\Setup.ini'), (Join-Path $instA1 'hmclversion.cfg') -Force -ErrorAction SilentlyContinue
$null = & $exe --provision $full --reuse 2>&1 | Out-String
$finalVerifyRaw = & (Join-Path $RepoRoot 'scripts\spike\verify-provision-0-2.ps1') -GameDir $full 6>&1 | Out-String
$finalVerifyExit = $LASTEXITCODE
$factsExtra += "final_verify_exit=$finalVerifyExit"
$finalVerifyRaw | Out-File -FilePath (Join-Path $dir 'verify.after-preserve.txt') -Encoding utf8
Write-Host "[FINAL] verify_exit=$finalVerifyExit"
if ($finalVerifyExit -ne 0) { Write-Error "preserve 测试复位后 verify 失败（脚本不可复跑）"; exit 1 }

$facts = @()
$facts += "commit=$commit"
$facts += "game_dir=$full"
$facts += "mod_source=$ModSource"
$facts += "provision_exit=$provisionExit"
$facts += "provision_seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))"
$facts += "verify_exit=$verifyExit"
$factsExtra | Out-File -FilePath (Join-Path $dir 'preserve.txt') -Encoding utf8
$facts += $factsExtra
$facts += 'product_version=' + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += 'os=' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName) +
          ' build ' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild)
$facts += 'powershell=' + $PSVersionTable.PSVersion
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8

# 实例 json 哈希
$hashes = @()
foreach ($v in @('A2', 'A1', 'B')) {
    $name = "RSTC-0-2-$v"
    $json = Join-Path $full "versions\$name\$name.json"
    $hashes += "$((Get-FileHash -LiteralPath $json -Algorithm SHA256).Hash)  $name.json"
}
$hashes | Out-File -FilePath (Join-Path $dir 'SHA256SUMS.instances.txt') -Encoding ascii

Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -notlike 'SHA256SUMS*' } |
    ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } |
    Out-File -FilePath (Join-Path $dir 'SHA256SUMS.evidence.txt') -Encoding ascii

Write-Host "provision=$provisionExit verify=$verifyExit"
Write-Host "evidence: $dir"
if ($provisionExit -ne 0 -or $verifyExit -ne 0) { exit 1 }
exit 0
