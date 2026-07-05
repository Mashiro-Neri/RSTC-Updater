# ============================================================
#  RSTC Updater 发布脚本
#  用法:
#    .\publish.ps1 3.2          指定版本号发布
#    .\publish.ps1 -Minor       次版本 +1 (3.0 -> 3.1)
#    .\publish.ps1 -Major       主版本 +1 (3.0 -> 4.0)
#    .\publish.ps1 -Patch       修订版 +1 (3.0 -> 3.0.1)
# ============================================================
param(
    [string]$Version,
    [string]$Message,
    [switch]$Major,
    [switch]$Minor,
    [switch]$Patch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
$ps1File = Join-Path $root "update_modpack.ps1"

# ==================== 1. 检查工作区 ====================
Push-Location -LiteralPath $root
try {
    $gitStatus = git status --porcelain 2>&1
    if ($gitStatus -match '\S') {
        Write-Host "ERROR: 工作区不干净，请先提交或暂存所有更改:" -ForegroundColor Red
        Write-Host $gitStatus
        exit 1
    }
} finally { Pop-Location }

# ==================== 2. 读取当前版本 ====================
$ps1Content = Get-Content -LiteralPath $ps1File -Raw -Encoding UTF8
$dollar = '$'
$matchPattern = $dollar + 'Script:UpdaterVersion\s*=\s*"([^"]+)"'
$currentMatch = [regex]::Match($ps1Content, $matchPattern)
if (-not $currentMatch.Success) {
    Write-Host "ERROR: 无法在 update_modpack.ps1 中找到 UpdaterVersion" -ForegroundColor Red
    exit 1
}
$currentVer = $currentMatch.Groups[1].Value
$parts = $currentVer.Split('.')
$majorVer = [int]$parts[0]
$minorVer = if ($parts.Length -ge 2) { [int]$parts[1] } else { 0 }
$patchVer = if ($parts.Length -ge 3) { [int]$parts[2] } else { 0 }

# ==================== 3. 计算新版本 ====================
if ($Major) {
    $newVer = "$($majorVer + 1).0"
} elseif ($Minor) {
    $newVer = "$majorVer.$($minorVer + 1)"
} elseif ($Patch) {
    $newVer = "$majorVer.$minorVer.$($patchVer + 1)"
} elseif ($Version) {
    $newVer = $Version.TrimStart('v').Trim()
} else {
    Write-Host "用法: .\publish.ps1 <版本号> [-message ""说明""]" -ForegroundColor Yellow
    Write-Host "      .\publish.ps1 -Major|-Minor|-Patch [-message ""说明""]" -ForegroundColor Yellow
    Write-Host "当前版本: $currentVer" -ForegroundColor Gray
    exit 1
}

if ($newVer -match '[^\d.]') {
    Write-Host "ERROR: 版本号格式无效: $newVer" -ForegroundColor Red
    exit 1
}

# ==================== 4. 确认 ====================
Write-Host ""
Write-Host "  RSTC Updater 发布脚本" -ForegroundColor Cyan
Write-Host "  ────────────────────"
Write-Host "  当前版本: $currentVer" -ForegroundColor Gray
Write-Host "  新版本:   $newVer" -ForegroundColor Green
if ($Message) { Write-Host "  说明:     $Message" -ForegroundColor Gray }
Write-Host ""

$confirm = Read-Host "确认发布? [Y/n]"
if ($confirm -and $confirm -notmatch '^(y|Y|yes|YES)$') {
    Write-Host "已取消"
    exit 0
}

# ==================== 5. 替换版本号 ====================
$newAssign = $dollar + ('Script:UpdaterVersion = "' + $newVer + '"')
$findPattern = $dollar + 'Script:UpdaterVersion\s*=\s*"[^"]*"'
$ps1Content = $ps1Content -replace $findPattern, $newAssign
$headerMatch = [regex]::Match($ps1Content, '(红石镇客户端更新器\s+)v[\d.]+')
if ($headerMatch.Success) {
    $ps1Content = $ps1Content -replace '(红石镇客户端更新器\s+)v[\d.]+', ('${1}v' + $newVer)
}

Set-Content -LiteralPath $ps1File -Value $ps1Content -Encoding UTF8 -NoNewline

# ==================== 6. 语法校验 ====================
Write-Host "  语法检查中..." -NoNewline
$tokens = $null; $errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($ps1File, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) {
    Write-Host " 检测到语法错误!" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  - $($_.Message)" -ForegroundColor Red }
    Write-Host "  已回滚版本号，请修改语法错误后重试" -ForegroundColor Yellow
    git checkout -- "update_modpack.ps1" 2>&1 | Out-Null
    exit 1
}
Write-Host "OK" -ForegroundColor Green

# ==================== 7. Git 操作 ====================
$tag = "v$newVer"
$commitMsg = if ($Message) { "${tag}: $Message" } else { "Release $tag" }

Write-Host "  提交: $commitMsg" -ForegroundColor Cyan
git add "update_modpack.ps1" 2>&1 | Out-Null
git commit -m $commitMsg 2>&1 | Out-Null
git tag $tag 2>&1 | Out-Null

Write-Host "  推送中..." -ForegroundColor Cyan
git push origin HEAD 2>&1
git push origin $tag 2>&1

# ==================== 8. 完成 ====================
Write-Host ""
Write-Host "  ==========================================" -ForegroundColor Green
Write-Host "   发布成功! $tag" -ForegroundColor Green
Write-Host "  ==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Release 地址: https://github.com/Mashiro-Neri/RSTC-Updater/releases/tag/$tag" -ForegroundColor Gray
Write-Host "  Actions 构建: https://github.com/Mashiro-Neri/RSTC-Updater/actions" -ForegroundColor Gray
Write-Host ""
Write-Host "  等待 Actions 完成后，用户在启动器菜单选择" -ForegroundColor Yellow
Write-Host "  "更新启动器" 即可自动更新。" -ForegroundColor Yellow
Write-Host ""
