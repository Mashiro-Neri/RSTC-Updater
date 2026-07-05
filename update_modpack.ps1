# ============================================================
#  红石镇客户端更新器 v4.1
#  基于 GitHub Releases, 支持检查更新 / 首次下载 / 版本管理 / 自更新
# ============================================================

# ==================== 配置区 ====================
$Script:RepoUrl  = "https://github.com/Mashiro-Neri/Redstone-Town-Client_update_modpack.git"
$Script:VersionFile = "RSTC_version.txt"
$Script:UpdaterVersion = "4.1"
$Script:UpdaterRepo   = "Mashiro-Neri/RSTC-Updater"

# 同步目录
$Script:SyncFolders = @("mods", "config", "resourcepacks", "shaderpacks", "PCL")
# 带本地配置的目录
$Script:ConfigFolders = @("config")
# 始终保留的本地文件
$Script:ProtectedFiles = @("options.txt", "servers.dat")
# 备份目录名
$Script:BackupDir = "_modpack_backup"

# 下载镜像
$Script:DownloadMirrors = @(
    @{ Name = "GitHub 直连 (可能需要梯子)"; Url = "" },
    @{ Name = "gh-proxy.com"; Url = "https://gh-proxy.com/" },
    @{ Name = "hk.gh-proxy.com"; Url = "https://hk.gh-proxy.com/" },
    @{ Name = "edgeone.gh-proxy.com"; Url = "https://edgeone.gh-proxy.com/" },
    @{ Name = "gh.llkk.cc"; Url = "https://gh.llkk.cc/" }
)
$Script:DownloadRetries = 3
$Script:DownloadBufferSize = 65536
# ============================================================

$Script:Timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$Script:LogFile = $null
$Script:IsFreshInstall = $true
$Script:PreserveConfig = $false
$Script:SelectedMirror = 0
$Script:McRoot = $null
$Script:TempRepo = $null

# ==================== TUI 工具函数 ====================
$Script:TuiWidth = if ($Host.UI.RawUI -and $Host.UI.RawUI.WindowSize) { [Math]::Min($Host.UI.RawUI.WindowSize.Width - 4, 78) } else { 50 }

function Write-Box {
    param([string[]]$Lines, [string]$Color = "Cyan")
    $w = 52
    Write-Host ("  " + [string]::new('═', $w)) -ForegroundColor $Color
    foreach ($line in $Lines) {
        $pad = [Math]::Max(0, [int](($w - $line.Length) / 2))
        Write-Host ("  " + (' ' * $pad) + $line) -ForegroundColor $Color
    }
    Write-Host ("  " + [string]::new('═', $w)) -ForegroundColor $Color
    Write-Host ""
}

function Write-Step {
    param([int]$Step, [int]$Total, [string]$Title)
    Write-Host ""
    Write-Host "  [ $Step/$Total ] $Title" -ForegroundColor Cyan
    Write-Host ("  " + [string]::new('─', 50))
}

function Show-Menu {
    param([string]$Title, [string[]]$Items, [int]$Default = 0, [bool]$HasBack = $false)
    $selected = $Default; $count = $Items.Length; $firstLine = [Console]::CursorTop
    while ($true) {
        [Console]::SetCursorPosition(0, $firstLine)
        if ($Title) { Write-Host "  $Title" -ForegroundColor DarkGray }
        for ($i = 0; $i -lt $count; $i++) {
            if ($i -eq $selected) { Write-Host ("  ▶ " + $Items[$i]) -ForegroundColor White -BackgroundColor DarkCyan }
            else { Write-Host ("    " + $Items[$i]) -ForegroundColor Gray }
        }
        $hintLine = if ($HasBack) { "  ↑↓ 移动  Enter 确认   B 返回" } else { "  ↑↓ 移动  Enter 确认" }
        Write-Host $hintLine -ForegroundColor DarkGray
        $totalLines = $count + 1; if ($Title) { $totalLines++ }
        $key = [Console]::ReadKey($true); $keyStr = $key.Key.ToString()
        if ($keyStr -eq 'B' -or $keyStr -eq 'Escape') { $selected = -1; break }
        if ($keyStr -eq 'UpArrow') { if ($selected -gt 0) { $selected-- } }
        elseif ($keyStr -eq 'DownArrow') { if ($selected -lt $count - 1) { $selected++ } }
        elseif ($keyStr -match '^D(\d)$' -or $keyStr -match '^NumPad(\d)$') { $num = [int]$Matches[1]; if ($num -lt $count) { $selected = $num; break } }
        if ($key.Key -eq 'Enter' -or $keyStr -match '^(D\d|NumPad\d)$') { break }
        [Console]::SetCursorPosition(0, $firstLine)
        $blankW = if ($Host.UI.RawUI -and $Host.UI.RawUI.WindowSize) { $Host.UI.RawUI.WindowSize.Width } else { 80 }
        $blank = [string]::new(' ', $blankW)
        for ($j = 0; $j -lt $totalLines; $j++) { Write-Host $blank }
    }
    return $selected
}

function Show-Input {
    param([string]$Prompt, [string]$Default = "")
    $usrInput = Read-Host "  $Prompt"
    if ($null -eq $usrInput) { return "" }
    $usrInput = $usrInput.Trim('"').Trim()
    if ([string]::IsNullOrWhiteSpace($usrInput) -and $Default) { $usrInput = $Default }
    return $usrInput
}

function Show-Progress {
    param([double]$Percent, [string]$Status)
    $barWidth = 40; $filled = [Math]::Max(0, [Math]::Min($barWidth, [Math]::Round($Percent / 100 * $barWidth)))
    $bar = "  [" + [string]::new('#', $filled) + [string]::new('-', $barWidth - $filled) + "]"
    Write-Host ("`r$bar $("{0,3}" -f [Math]::Round($Percent))%  $Status" + ' ' * 10) -NoNewline
}

function Write-Success { Write-Host "  ✓ $args" -ForegroundColor Green }
function Write-Warn    { Write-Host "  ⚠ $args" -ForegroundColor Yellow }
function Write-ErrorT  { Write-Host "  ✗ $args" -ForegroundColor Red }

function Write-Banner {
    Write-Host ""
    $w = 52; $inner = 50
    Write-Host ("  ╔" + [string]::new('═', $inner) + "╗") -ForegroundColor Red
    Write-Host ("  ║" + (' ' * $inner) + "║") -ForegroundColor Red
    $art = @(
        "  ██████╗ ███████╗████████╗                     ",
        "  ██╔══██╗██╔════╝╚══██╔══╝                     ",
        "  ██████╔╝███████╗   ██║                        ",
        "  ██╔══██╗╚════██║   ██║                        ",
        "  ██║  ██║███████║   ██║                        ",
        "  ╚═╝  ╚═╝╚══════╝   ╚═╝                        "
    )
    foreach ($l in $art) {
        $p = [int](($inner - $l.Length)/2); if ($p -gt 0) { Write-Host ("  ║" + (' ' * $p) + $l + (' ' * ($inner - $l.Length - $p)) + "║") -ForegroundColor Red }
        else { Write-Host ("  ║ " + $l + " ║") -ForegroundColor Red }
    }
    Write-Host ("  ║" + (' ' * $inner) + "║") -ForegroundColor Red
    $title = "红石镇客户端更新器 v$($Script:UpdaterVersion)"
    $tw = (($title -split '' | Where-Object { $_ -match '[^\u0000-\u00ff]' }).Count * 2) + ($title.Length - ($title -split '' | Where-Object { $_ -match '[^\u0000-\u00ff]' }).Count)
    $padL = [Math]::Max(0, [int](($inner - $tw)/2)); $padR = $inner - $tw - $padL
    Write-Host ("  ║" + (' ' * $padL) + $title + (' ' * $padR) + "║") -ForegroundColor Red
    Write-Host ("  ║" + (' ' * $inner) + "║") -ForegroundColor Red
    Write-Host ("  ╚" + [string]::new('═', $inner) + "╝") -ForegroundColor Red
    Write-Host ""
}

# ==================== 核心函数 ====================

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $line = "[$($Script:Timestamp)] [$Level] $Message"
    Write-Host $line
    if ($Script:LogFile) { Add-Content -LiteralPath $Script:LogFile -Value $line -Encoding UTF8 }
}

function Get-LatestRelease {
    $ownerRepo = ($Script:RepoUrl -replace 'https://github\.com/', '' -replace '\.git$', '')
    $apiUrl = "https://api.github.com/repos/$ownerRepo/releases/latest"
    Write-Log "获取最新 Release 信息..."
    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Method Get -TimeoutSec 15 -ErrorAction Stop
        $asset = $release.assets | Select-Object -First 1
        if (-not $asset) {
            Write-Log "Release 没有附件文件" "ERROR"
            return $null
        }
        return @{
            Tag     = $release.tag_name
            Name    = $release.name
            Body    = if ($release.body) { ($release.body -split "`n" | Select-Object -First 15) -join "`n" } else { "" }
            Url     = $asset.browser_download_url
            Size    = $asset.size
            Asset   = $asset.name
        }
    } catch {
        Write-Log "获取 Release 信息失败，请检查网络连接或尝试使用梯子" "WARN"
        return $null
    }
}

function Get-LocalVersion {
    param([string]$McRoot)
    $vf = Join-Path $McRoot $Script:VersionFile
    if (Test-Path -LiteralPath $vf) {
        return (Get-Content -LiteralPath $vf -TotalCount 1 -ErrorAction SilentlyContinue).Trim()
    }
    return $null
}

function Set-LocalVersion {
    param([string]$McRoot, [string]$Tag)
    $vf = Join-Path $McRoot $Script:VersionFile
    try { Set-Content -LiteralPath $vf -Value $Tag -Encoding UTF8 -ErrorAction SilentlyContinue }
    catch { Write-Log "写入版本文件失败: $vf" "WARN" }
}

function Test-MirrorSpeed {
    param([string]$MirrorUrl, [string]$TestUrl, [int]$TestBytes = 196608)
    $url = if ($MirrorUrl) { $MirrorUrl + $TestUrl } else { $TestUrl }
    try {
        $req = [System.Net.HttpWebRequest]::Create($url); $req.Timeout = 10000; $req.ReadWriteTimeout = 10000
        $sw = [System.Diagnostics.Stopwatch]::StartNew(); $resp = $req.GetResponse(); $stream = $resp.GetResponseStream()
        $buf = [byte[]]::new(4096); $total = 0
        while (($r = $stream.Read($buf, 0, $buf.Length)) -gt 0) { $total += $r; if ($total -ge $TestBytes) { break } }
        $stream.Close(); $resp.Close(); $sw.Stop()
        if ($total -gt 0) { return @{ Speed = [math]::Round($total / 1024.0 / $sw.Elapsed.TotalSeconds); Status = 'ok' } }
    } catch {
        $msg = "$_"
        if ($msg -match 'forcibly closed|refused|aborted') { return @{ Speed = 0; Status = 'blocked' } }
        if ($msg -match 'timed out|timeout') { return @{ Speed = 0; Status = 'timeout' } }
        if ($msg -match 'resolve|not found|no such host') { return @{ Speed = 0; Status = 'dns' } }
    }
    return @{ Speed = 0; Status = 'blocked' }
}

function Invoke-AssetDownload {
    param([string]$AssetUrl, [string]$TargetPath)
    $mirror = $Script:DownloadMirrors[$Script:SelectedMirror]
    $dlUrl = if ($mirror.Url) { $mirror.Url + $AssetUrl } else { $AssetUrl }
    Write-Log "下载镜像: $($mirror.Name)"

    $totalSize = 0
    Write-Host "  正在连接服务器..." -NoNewline
    try {
        $sizeReq = [System.Net.HttpWebRequest]::Create($dlUrl); $sizeReq.Method = "HEAD"
        $sizeReq.AllowAutoRedirect = $true; $sizeReq.Timeout = 8000
        $sizeResp = $sizeReq.GetResponse(); $totalSize = [int64]$sizeResp.ContentLength; $sizeResp.Close()
    } catch { $totalSize = 0 }

    if ($totalSize -gt 0) {
        $sizeMB = [math]::Round($totalSize / 1MB, 1)
        Write-Host ("`r  文件大小: ${sizeMB}MB" + ' ' * 30) -ForegroundColor Green
        Write-Log "文件大小: ${sizeMB}MB"
    } else { Write-Host ("`r  文件大小暂未知" + ' ' * 30) -ForegroundColor Yellow; Write-Log "文件大小: 未知" }

    $downloaded = $false; $retry = 0; $zipFile = Join-Path $TargetPath "release.zip"
    while (-not $downloaded -and $retry -lt $Script:DownloadRetries) {
        try {
            if ($retry -gt 0) { Write-Log "重试下载 ($retry/$($Script:DownloadRetries))..."; Start-Sleep -Seconds 2 }
            Write-Log "正在下载..."
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $request = [System.Net.HttpWebRequest]::Create($dlUrl); $request.Timeout = 60000; $request.ReadWriteTimeout = 120000; $request.AllowAutoRedirect = $true
            $response = $request.GetResponse()
            if ($totalSize -le 0 -and $response.ContentLength -gt 0) {
                $totalSize = [int64]$response.ContentLength; $sizeMB = [math]::Round($totalSize / 1MB, 1)
                Write-Log "文件大小: ${sizeMB}MB"
            }
            $stream = $response.GetResponseStream(); $fs = [System.IO.File]::OpenWrite($zipFile)
            $buffer = [byte[]]::new($Script:DownloadBufferSize); $totalRead = 0L; $nextReport = 1048576L
            try {
                $read = 0
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $fs.Write($buffer, 0, $read); $totalRead += $read
                    if ($totalRead -ge $nextReport) {
                        $pct = if ($totalSize -gt 0) { [math]::Round($totalRead / $totalSize * 100) } else { 0 }
                        $mbDone = [math]::Round($totalRead / 1MB, 1); $mbTotal = if ($totalSize -gt 0) { [math]::Round($totalSize / 1MB, 1) } else { "?" }
                        $elapsed = $stopwatch.Elapsed.TotalSeconds
                        $speedMB = if ($elapsed -gt 0) { ($totalRead / 1MB) / $elapsed } else { 0 }
                        $speedStr = if ($speedMB -ge 1) { "$([math]::Round($speedMB, 1))MB/s" } else { "$([math]::Round($speedMB * 1024))KB/s" }
                        $remain = if ($speedMB -gt 0 -and $totalSize -gt 0) { [math]::Round(($totalSize - $totalRead) / 1MB / $speedMB) } else { -1 }
                        $remainStr = if ($remain -ge 0) { "剩余 ${remain}秒" } else { "" }
                        Show-Progress -Percent $pct -Status "$mbDone/$mbTotal MB | ${speedStr} | ${remainStr}"
                        $nextReport = $totalRead + 1048576L
                    }
                }
            } finally { $fs.Close(); $fs.Dispose(); $stream.Close(); $stream.Dispose(); $response.Close(); $response.Dispose() }
            $stopwatch.Stop()
            if ($totalSize -gt 0) {
                $bar = "  [" + [string]::new('#', 40) + "]"
                Write-Host ("`r$bar 100%  下载完成" + ' ' * 10)
            } else { Write-Host "" }
            $downloadedSize = [math]::Round((Get-Item -LiteralPath $zipFile).Length / 1MB, 1)
            Write-Log "下载完成: ${downloadedSize}MB, 耗时 $([math]::Round($stopwatch.Elapsed.TotalSeconds,1))秒"
            $actualSize = (Get-Item -LiteralPath $zipFile).Length
            if ($totalSize -gt 0) {
                $ratio = [math]::Round($actualSize / $totalSize * 100, 1)
                if ($ratio -lt 90) { Remove-Item -LiteralPath $zipFile -Force -ErrorAction SilentlyContinue; throw "下载不完整 ($ratio%)，请换镜像或手动下载" }
            }
            $downloaded = $true
        } catch {
            $retry++
            Write-Log "下载出错: $_" "WARN"
            if (Test-Path -LiteralPath $zipFile) { Remove-Item -LiteralPath $zipFile -Force -ErrorAction SilentlyContinue }
            if ($retry -ge $Script:DownloadRetries) { throw "下载失败 (已重试 $($Script:DownloadRetries) 次): $_" }
        }
    }

    Write-Log "正在解压..."
    Expand-ToTarget -ZipFile $zipFile -TargetPath $TargetPath
    Write-Log "解压完成"
}

function Expand-ToTarget {
    param([string]$ZipFile, [string]$TargetPath)
    $extractDir = Join-Path $TargetPath "repo_extracted"
    if (Test-Path -LiteralPath $extractDir) { try { [System.IO.Directory]::Delete($extractDir, $true) } catch {} }
    Write-Host "  正在解压... (请耐心等待, 大文件可能需数分钟)" -ForegroundColor Cyan
    $prevProgressPref = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipFile, $extractDir) }
    catch {
        try { Expand-Archive -LiteralPath $ZipFile -DestinationPath $extractDir -Force -ErrorAction Stop }
        catch { throw "解压失败: $_" }
    }
    $ProgressPreference = $prevProgressPref
    $entryCount = (Get-ChildItem -LiteralPath $extractDir -Recurse -File).Count
    Write-Host ("  解压完成 ($entryCount 个文件)") -ForegroundColor Green
    Remove-Item -LiteralPath $ZipFile -Force -ErrorAction SilentlyContinue

    $innerDir = Get-ChildItem -LiteralPath $extractDir -Directory | Where-Object { $_.Name -notlike '__MACOSX*' } | Select-Object -First 1
    if (-not $innerDir) {
        $allItems = Get-ChildItem -LiteralPath $extractDir
        if ($allItems) { $innerDir = $extractDir }
        else { try { [System.IO.Directory]::Delete($extractDir, $true) } catch {}; throw "解压后未找到内容" }
    }
    $innerPath = if ($innerDir -is [string]) { $innerDir } else { $innerDir.FullName }
    Copy-Item -Path (Join-Path $innerPath "*") -Destination $TargetPath -Recurse -Force -ErrorAction Stop
    try { [System.IO.Directory]::Delete($extractDir, $true) } catch {}
}

function Sync-Folders {
    param([string]$McRoot, [string]$RepoRoot)
    foreach ($folder in $Script:SyncFolders) {
        $src = Join-Path $RepoRoot $folder; $dst = Join-Path $McRoot $folder
        if (Test-Path -LiteralPath $src) {
            Write-Log "同步文件: $folder/"
            if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
            $roboLog = Join-Path ([System.IO.Path]::GetTempPath()) "robo_sync.log"
            cmd /c "robocopy `"$src`" `"$dst`" /MIR /NP /NFL /NDL /NJH /NJS /R:3 /W:2 /XD _modpack_backup >`"$roboLog`" 2>&1"
            $exitCode = $LASTEXITCODE
            Remove-Item $roboLog -Force -ErrorAction SilentlyContinue
            if ($exitCode -ge 8) { Write-Log "同步 $folder 时出现错误 (退出码: $exitCode)" "WARN" }
            elseif ($exitCode -ge 4) { Write-Log "同步 $folder 部分完成 (退出码: $exitCode)" "WARN" }
            else { Write-Log "  -> 完成" }
        } else { Write-Log "仓库中不存在 $folder/ 目录，跳过同步" "WARN" }
    }
    $repoItems = Get-ChildItem -LiteralPath $RepoRoot -File
    foreach ($item in $repoItems) {
        if ($item.Name -match '^\.git') { continue }
        if ($item.Name -match '^(readme|changelog|license)\.md' -or $item.Name -eq '.gitattributes') { continue }
        if ($item.Name -eq 'update_modpack.ps1' -or $item.Name -eq 'update_modpack.bat' -or $item.Name -eq $Script:VersionFile -or $item.Name -eq $Script:BackupDir -or $item.Name -like 'update_log_*.txt') { continue }
        Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $McRoot $item.Name) -Force
        Write-Log "同步根文件: $($item.Name)"
    }
}

function Backup-ProtectedFiles {
    param([string]$McRoot, [string]$BackupBase)
    $backupPath = Join-Path $BackupBase "protected_files"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    foreach ($file in $Script:ProtectedFiles) {
        $src = Join-Path $McRoot $file; $dst = Join-Path $backupPath $file
        if (Test-Path -LiteralPath $src) {
            $dstDir = Split-Path $dst -Parent
            if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
            Copy-Item -LiteralPath $src -Destination $dst -Force
            Write-Log "已备份: $file"
        }
    }
}

function Restore-ProtectedFiles {
    param([string]$McRoot, [string]$BackupBase)
    $backupPath = Join-Path $BackupBase "protected_files"
    if (-not (Test-Path -LiteralPath $backupPath)) { return }
    Get-ChildItem -LiteralPath $backupPath -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $McRoot $_.Name) -Force
        Write-Log "已恢复: $($_.Name)"
    }
}

function Restore-CustomConfigs {
    param([string]$McRoot, [string]$BackupBase)
    $backupPath = Join-Path $BackupBase "custom_configs"
    if (-not (Test-Path -LiteralPath $backupPath)) { return }
    Get-ChildItem -LiteralPath $backupPath -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($backupPath.Length).TrimStart('\', '/')
        $dst = Join-Path $McRoot $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
    }
}

function Backup-CustomConfigs {
    param([string]$McRoot, [string]$RepoRoot, [string]$BackupBase)
    $backupPath = Join-Path $BackupBase "custom_configs"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    foreach ($folder in $Script:ConfigFolders) {
        $localCfg = Join-Path $McRoot $folder; $repoCfg = Join-Path $RepoRoot $folder
        if (-not (Test-Path -LiteralPath $localCfg)) { continue }
        if (Test-Path -LiteralPath $repoCfg) {
            $repoFiles = Get-ChildItem -LiteralPath $repoCfg -Recurse -File | ForEach-Object { $_.FullName.Substring($repoCfg.Length).TrimStart('\', '/') }
            Get-ChildItem -LiteralPath $localCfg -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($localCfg.Length).TrimStart('\', '/')
                if ($rel -notin $repoFiles) {
                    $dst = Join-Path $backupPath $rel; $dDir = Split-Path $dst -Parent
                    if (-not (Test-Path -LiteralPath $dDir)) { New-Item -ItemType Directory -Path $dDir -Force | Out-Null }
                    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
                }
            }
        } else { Copy-Item -LiteralPath $localCfg -Destination (Join-Path $backupPath $folder) -Recurse -Force }
    }
}

function Cleanup-Temp {
    if ($Script:TempRepo -and (Test-Path -LiteralPath $Script:TempRepo)) {
        try { [System.IO.Directory]::Delete($Script:TempRepo, $true) } catch {}
        Write-Log "已清理临时目录"
    }
}

function Find-MinecraftRoot {
    foreach ($c in @(
        [Environment]::GetFolderPath("ApplicationData") + "\.minecraft",
        "$home\.minecraft", "$home\AppData\Roaming\.minecraft",
        "D:\.minecraft", "E:\.minecraft", "C:\.minecraft"
    )) {
        if (Test-Path -LiteralPath $c) {
            if ((Test-Path -LiteralPath (Join-Path $c "launcher_profiles.json")) -or (Test-Path -LiteralPath (Join-Path $c "options.txt"))) { return $c }
        }
    }
    return $null
}

function Get-UserMcRoot {
    $found = Find-MinecraftRoot
    if ($found) {
        Write-Host "自动检测到MC根目录: $found"
        $answer = Read-Host "是否使用此目录? [Y/n]"
        if ($answer -and $answer -ne '' -and $answer -notmatch '^(y|Y|yes|YES)$') { $found = $null }
    }
    if (-not $found) {
        while ($true) {
            $p = Read-Host "请将版本文件夹拖入窗口或自行输入 (启动器点击版本文件夹后打开的路径)"; if (-not $p) { exit 1 }
            $p = $p.Trim('"').Trim()
            if (Test-Path -LiteralPath (Join-Path $p "options.txt")) { $found = $p; break }
            if (Test-Path -LiteralPath (Join-Path $p ".minecraft\options.txt")) { $found = Join-Path $p ".minecraft"; break }
            Write-Host "[错误] 该路径下未找到options.txt" -ForegroundColor Red
        }
    }
    return $found
}

function Show-Usage {
    Write-Host "用法:" -ForegroundColor Yellow
    Write-Host "  .\update_modpack.ps1 [选项]"; Write-Host ""
    Write-Host "选项:" -ForegroundColor Yellow
    Write-Host "  --preserve-config    保留用户配置文件"
    Write-Host "  --fresh              强制首次下载 (跳过版本检查)"
    Write-Host "  --mirror <n>         下载镜像编号 (0-4)"
    Write-Host "  --mirror-url <url>   自定义镜像地址"
    Write-Host "  --repo-url <url>     指定仓库地址"
    Write-Host "  --mc-root <path>     指定MC根目录路径"
    Write-Host "  --non-interactive    跳过TUI交互"
    Write-Host "  --help               显示此帮助"; Write-Host ""
}

# ==================== 自更新功能 ====================

function Get-UpdaterRelease {
    $apiUrl = "https://api.github.com/repos/$($Script:UpdaterRepo)/releases/latest"
    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Method Get -TimeoutSec 15 -ErrorAction Stop
        $ps1Asset = $release.assets | Where-Object { $_.name -eq 'update_modpack.ps1' } | Select-Object -First 1
        $batAsset = $release.assets | Where-Object { $_.name -eq 'update_modpack.bat' } | Select-Object -First 1
        if (-not $ps1Asset -or -not $batAsset) { return $null }
        return @{
            Tag       = $release.tag_name
            Name      = $release.name
            Body      = if ($release.body) { ($release.body -split "`n" | Select-Object -First 15) -join "`n" } else { "" }
            Ps1Url    = $ps1Asset.browser_download_url
            BatUrl    = $batAsset.browser_download_url
        }
    } catch { return $null }
}

function Invoke-UpdaterUpdate {
    Write-Host ""
    Write-Step -Step 1 -Total 4 -Title "检查启动器更新"
    Write-Host "  当前版本: v$($Script:UpdaterVersion)" -ForegroundColor Gray
    Write-Host "  正在获取最新版本信息..." -ForegroundColor Cyan

    $updaterRelease = Get-UpdaterRelease
    if (-not $updaterRelease) {
        Write-ErrorT "无法获取更新器版本信息，请检查网络连接"
        if ($Script:IsInteractive) { Write-Host "`n  按任意键返回..."; [Console]::ReadKey($true) | Out-Null }
        return
    }

    if ($updaterRelease.Tag -eq "v$($Script:UpdaterVersion)" -or $updaterRelease.Tag -eq $Script:UpdaterVersion) {
        Write-Success "启动器已是最新版本! (v$($Script:UpdaterVersion))"
        if ($Script:IsInteractive) { Write-Host "`n  按任意键返回..."; [Console]::ReadKey($true) | Out-Null }
        return
    }

    Write-Step -Step 2 -Total 4 -Title "发现新版本!"
    Write-Host "  当前: v$($Script:UpdaterVersion)  →  $($updaterRelease.Tag)" -ForegroundColor Yellow
    Write-Host ""
    Write-Box -Lines @("$($updaterRelease.Name)", "版本: $($updaterRelease.Tag)") -Color Cyan
    if ($updaterRelease.Body) {
        Write-Host "  更新内容:" -ForegroundColor Cyan
        $updaterRelease.Body -split "`n" | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
    }
    Write-Host ""

    $confirm = Show-Menu -Title "是否更新启动器?" -Items @("是 - 立即更新", "否 - 取消") -Default 0
    if ($confirm -ne 0) { return }

    Write-Step -Step 3 -Total 4 -Title "选择下载镜像"
    $mirrorItems = @("自动测速 - 选最快的", "手动选择 - 自己挑镜像")
    $mc = Show-Menu -Title "请选择:" -Items $mirrorItems -Default 0 -HasBack $true
    if ($mc -eq -1) { return }

    if ($mc -eq 0) {
        Write-Host "`n  正在测试镜像速度..." -ForegroundColor Cyan
        $results = @()
        foreach ($i in 0..($Script:DownloadMirrors.Length - 1)) {
            $m = $Script:DownloadMirrors[$i]; Write-Host "    测试 $($m.Name)..." -NoNewline
            $res = Test-MirrorSpeed -MirrorUrl $m.Url -TestUrl $updaterRelease.Ps1Url
            $results += @{ Index = $i; Name = $m.Name; Speed = $res.Speed }
            if ($res.Status -eq 'ok') { Write-Host "`r  ▶ $($m.Name): $(if ($res.Speed -ge 1024){"$([math]::Round($res.Speed/1024,1))MB/s"}else{"$($res.Speed)KB/s"})" -ForegroundColor Green }
            else { Write-Host "`r  ✗ $($m.Name): $(switch($res.Status){'blocked'{'无法连接'};'timeout'{'超时'};'dns'{'DNS失败'};default{'不可用'}})" -ForegroundColor Red }
        }
        $best = $results | Where-Object { $_.Speed -gt 0 } | Sort-Object Speed -Descending | Select-Object -First 1
        if ($best) { Write-Host ""; Write-Success "最快: $($best.Name)"; $Script:SelectedMirror = $best.Index }
        else { Write-Warn "全部不可用，使用默认直连" }
    } else {
        $items = @(); foreach ($m in $Script:DownloadMirrors) { $items += $m.Name }; $items += "自定义"
        $idx = Show-Menu -Title "请选择:" -Items $items -Default 0 -HasBack $true
        if ($idx -eq -1) { return }
        if ($idx -ge 0 -and $idx -lt $Script:DownloadMirrors.Length) { $Script:SelectedMirror = $idx }
        elseif ($idx -eq $items.Length - 1) {
            $cu = Show-Input "请输入镜像URL" "https://gh-proxy.com/"
            if ($cu) { $cu = $cu.Trim('/') + '/'; $Script:DownloadMirrors += @{Name="自定义 ($cu)"; Url=$cu}; $Script:SelectedMirror = $Script:DownloadMirrors.Length - 1 }
        }
    }

    $mirror = $Script:DownloadMirrors[$Script:SelectedMirror]
    $ps1Url = if ($mirror.Url) { $mirror.Url + $updaterRelease.Ps1Url } else { $updaterRelease.Ps1Url }
    $batUrl = if ($mirror.Url) { $mirror.Url + $updaterRelease.BatUrl } else { $updaterRelease.BatUrl }

    Write-Step -Step 4 -Total 4 -Title "下载更新"
    Write-Host "  镜像: $($mirror.Name)" -ForegroundColor Gray
    Write-Host "  正在下载 ..." -ForegroundColor Cyan

    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "rstc_updater_update"
    if (Test-Path -LiteralPath $tempDir) { try { [System.IO.Directory]::Delete($tempDir, $true) } catch {} }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    $newPs1 = Join-Path $tempDir "update_modpack.ps1"
    $newBat = Join-Path $tempDir "update_modpack.bat"

    try {
        $client = [System.Net.WebClient]::new()
        $client.DownloadFile($ps1Url, $newPs1)
        $client.DownloadFile($batUrl, $newBat)
        $client.Dispose()
    } catch {
        Write-ErrorT "下载失败: $_"
        try { [System.IO.Directory]::Delete($tempDir, $true) } catch {}
        if ($Script:IsInteractive) { Write-Host "`n  按任意键返回..."; [Console]::ReadKey($true) | Out-Null }
        return
    }

    Write-Success "下载完成!"

    $newPs1Path = Join-Path $tempDir "update_modpack.ps1"
    $newBatPath = Join-Path $tempDir "update_modpack.bat"
    $destPs1 = Join-Path $scriptDir "update_modpack.ps1"
    $destBat = Join-Path $scriptDir "update_modpack.bat"

    Write-Host "  正在替换文件..." -ForegroundColor Cyan
    try {
        Copy-Item -LiteralPath $newPs1Path -Destination $destPs1 -Force -ErrorAction Stop
        Copy-Item -LiteralPath $newBatPath -Destination $destBat -Force -ErrorAction Stop
    } catch {
        Write-ErrorT "替换失败: $_"
        Write-Host "  新文件位于: $tempDir" -ForegroundColor Yellow
        if ($Script:IsInteractive) { Write-Host "`n  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
        return
    }

    try { [System.IO.Directory]::Delete($tempDir, $true) } catch {}
    Write-Success "文件替换完成"

    Write-Host ""
    Write-Host ("  " + [string]::new('=', 52)) -ForegroundColor Green
    Write-Host "   更新完成! 请重新运行 update_modpack.bat" -ForegroundColor Green
    Write-Host ("  " + [string]::new('=', 52)) -ForegroundColor Green
    Write-Host ""
    if ($Script:IsInteractive) { Write-Host "  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
    exit 0
}

# ==================== 主流程 ====================

function Main {
    param([string[]]$ScriptArgs)

    if ("--help" -in $ScriptArgs) { Write-Banner; Show-Usage; return }

    $Script:IsInteractive = ($ScriptArgs.Length -eq 0)
    if ("--non-interactive" -in $ScriptArgs) { $Script:IsInteractive = $false }

    for ($i = 0; $i -lt $ScriptArgs.Length; $i++) {
        switch ($ScriptArgs[$i]) {
            "--preserve-config" { $Script:PreserveConfig = $true }
            "--fresh" { $Script:IsFreshInstall = $true }
            "--repo-url" { $Script:RepoUrl = $ScriptArgs[++$i] }
            "--mc-root" { $Script:McRoot = ($ScriptArgs[++$i]).Trim('"').Trim() }
            "--mirror" { $v = 0; if ([int]::TryParse($ScriptArgs[++$i], [ref]$v)) { $Script:SelectedMirror = $v } }
            "--mirror-url" { $u = ($ScriptArgs[++$i]).Trim('/'); $Script:DownloadMirrors += @{Name="自定义 ($u/)"; Url="$u/"}; $Script:SelectedMirror = $Script:DownloadMirrors.Length - 1 }
            "--non-interactive" { }
            "--help" { break }
            default { Write-Host "未知参数: $($ScriptArgs[$i])" -ForegroundColor Yellow }
        }
    }

    if ($Script:IsInteractive) {
        Write-Banner
        $mode = Show-Menu -Title "请选择:" -Items @("检查整合包更新", "没有整合包 (首次下载)", "更新启动器") -Default 0
        if ($mode -eq 2) { Invoke-UpdaterUpdate; return }
        $Script:IsFreshInstall = ($mode -eq 1)
    }

    # 获取 Release 信息 (两个模式都需要)
    Write-Host "`n  正在获取最新版本信息..." -ForegroundColor Cyan
    $release = Get-LatestRelease
    if (-not $release) { Write-ErrorT "无法获取 Release 信息，请检查网络或使用梯子"; exit 1 }

    # === 首次下载: 下载到桌面 ===
    if ($Script:IsFreshInstall) {
        $step = 1
        while ($step -le 2) {
            switch ($step) {
                1 {
                    Write-Step -Step 1 -Total 2 -Title "版本信息"
                    Write-Box -Lines @("$($release.Name)", "版本: $($release.Tag)", "大小: $([math]::Round($release.Size/1MB,1))MB") -Color Cyan
                    Write-Host "  更新日志:" -ForegroundColor Cyan
                    $release.Body -split "`n" | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
                    Write-Host ""
                    $confirm = Show-Menu -Title "是否开始下载?" -Items @("是 - 开始下载", "否 - 取消") -Default 0
                    if ($confirm -ne 0) { Write-Host "  已取消"; return }
                    $step++
                }
                2 {
                    Write-Step -Step 2 -Total 2 -Title "选择下载方式"
                    $mc = Show-Menu -Title "请选择:" -Items @("自动测速 - 选最快的", "手动选择 - 自己挑镜像") -Default 0 -HasBack $true
                    if ($mc -eq -1) { $step--; continue }
                    if ($mc -eq 0) {
                        Write-Host "`n  正在测试镜像速度..." -ForegroundColor Cyan
                        $results = @()
                        foreach ($i in 0..($Script:DownloadMirrors.Length - 1)) {
                            $m = $Script:DownloadMirrors[$i]; Write-Host "    测试 $($m.Name)..." -NoNewline
                            $res = Test-MirrorSpeed -MirrorUrl $m.Url -TestUrl $release.Url
                            $results += @{ Index = $i; Name = $m.Name; Speed = $res.Speed }
                            if ($res.Status -eq 'ok') { Write-Host "`r  ▶ $($m.Name): $(if ($res.Speed -ge 1024){"$([math]::Round($res.Speed/1024,1))MB/s"}else{"$($res.Speed)KB/s"})" -ForegroundColor Green }
                            else { Write-Host "`r  ✗ $($m.Name): $(switch($res.Status){'blocked'{'无法连接'};'timeout'{'超时'};'dns'{'DNS失败'};default{'不可用'}})" -ForegroundColor Red }
                        }
                        $best = $results | Where-Object { $_.Speed -gt 0 } | Sort-Object Speed -Descending | Select-Object -First 1
                        if ($best) { Write-Success "最快: $($best.Name)"; $Script:SelectedMirror = $best.Index }
                        else { Write-Warn "全部不可用，使用默认" }
                    } else {
                        $items = @(); foreach ($m in $Script:DownloadMirrors) { $items += $m.Name }; $items += "自定义"
                        $idx = Show-Menu -Title "请选择:" -Items $items -Default 0 -HasBack $true
                        if ($idx -eq -1) { $step--; continue }
                        if ($idx -ge 0 -and $idx -lt $Script:DownloadMirrors.Length) { $Script:SelectedMirror = $idx }
                        elseif ($idx -eq $items.Length - 1) {
                            $cu = Show-Input "请输入镜像URL" "https://gh-proxy.com/"
                            if ($cu) { $cu = $cu.Trim('/') + '/'; $Script:DownloadMirrors += @{Name="自定义 ($cu)"; Url=$cu}; $Script:SelectedMirror = $Script:DownloadMirrors.Length - 1 }
                        }
                    }
                    $step++
                }
            }
        }

        $desktop = [Environment]::GetFolderPath("Desktop")
        $savePath = Join-Path $desktop $release.Asset
        Write-Log "首次下载: 目标 = $savePath"

        $mirror = $Script:DownloadMirrors[$Script:SelectedMirror]
        $dlUrl = if ($mirror.Url) { $mirror.Url + $release.Url } else { $release.Url }
        Write-Host "`n  正在下载 $($release.Name) ($([math]::Round($release.Size/1MB,1))MB)..." -ForegroundColor Cyan
        Write-Host "  保存位置: $savePath" -ForegroundColor Gray
        Write-Host ""

        $totalSize = $release.Size
        $retry = 0; $downloaded = $false
        while (-not $downloaded -and $retry -lt $Script:DownloadRetries) {
            try {
                if ($retry -gt 0) { Write-Log "重试下载 ($retry/$($Script:DownloadRetries))..."; Start-Sleep -Seconds 2 }
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $request = [System.Net.HttpWebRequest]::Create($dlUrl); $request.Timeout = 60000; $request.ReadWriteTimeout = 120000; $request.AllowAutoRedirect = $true
                $response = $request.GetResponse()
                $stream = $response.GetResponseStream(); $fs = [System.IO.File]::OpenWrite($savePath)
                $buffer = [byte[]]::new($Script:DownloadBufferSize); $totalRead = 0L; $nextReport = 1048576L
                try {
                    $read = 0
                    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $fs.Write($buffer, 0, $read); $totalRead += $read
                        if ($totalRead -ge $nextReport) {
                            $pct = if ($totalSize -gt 0) { [math]::Round($totalRead / $totalSize * 100) } else { 0 }
                            $mbDone = [math]::Round($totalRead / 1MB, 1); $mbTotal = [math]::Round($totalSize / 1MB, 1)
                            $elapsed = $stopwatch.Elapsed.TotalSeconds
                            $speedMB = if ($elapsed -gt 0) { ($totalRead / 1MB) / $elapsed } else { 0 }
                            $speedStr = if ($speedMB -ge 1) { "$([math]::Round($speedMB, 1))MB/s" } else { "$([math]::Round($speedMB * 1024))KB/s" }
                            $remain = if ($speedMB -gt 0) { [math]::Round(($totalSize - $totalRead) / 1MB / $speedMB) } else { -1 }
                            $remainStr = if ($remain -ge 0) { "剩余 ${remain}秒" } else { "" }
                            Show-Progress -Percent $pct -Status "$mbDone/$mbTotal MB | ${speedStr} | ${remainStr}"
                            $nextReport = $totalRead + 1048576L
                        }
                    }
                } finally { $fs.Close(); $fs.Dispose(); $stream.Close(); $stream.Dispose(); $response.Close(); $response.Dispose() }
                $stopwatch.Stop()
                if ($totalSize -gt 0) {
                    $bar = "  [" + [string]::new('#', 40) + "]"
                    Write-Host ("`r$bar 100%  下载完成" + ' ' * 10)
                } else { Write-Host "" }
                if ((Get-Item $savePath).Length -lt $totalSize * 0.9) { Remove-Item $savePath -Force -ErrorAction SilentlyContinue; throw "文件不完整 ($([math]::Round((Get-Item $savePath).Length/$totalSize*100))%), 重试" }
                $downloaded = $true
            } catch {
                $retry++
                Write-Log "下载出错: $_" "WARN"
                if ($retry -ge $Script:DownloadRetries) { Write-ErrorT "下载失败, 已重试 $($Script:DownloadRetries) 次"; if (Test-Path $savePath) { Remove-Item $savePath -Force -ErrorAction SilentlyContinue }; exit 1 }
            }
        }
        Write-Success "下载完成! $([math]::Round((Get-Item -LiteralPath $savePath).Length/1MB,1))MB, 耗时 $([math]::Round($stopwatch.Elapsed.TotalSeconds,1))秒"
        Write-Host ""
        Write-Box -Lines @("下载完成!", "文件已保存到桌面:", $release.Asset, "", "将 ZIP 文件拖入启动器即可安装") -Color Green
        if ($Script:IsInteractive) { Write-Host "`n  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
        return
    }

    # === 检查更新: 步进式交互流程 ===
    $step = 1; $confirmed = $false
    while (-not $confirmed -and $step -le 4) {
        switch ($step) {
            1 {
                if ($Script:McRoot) { $step++; continue }

                # 检查当前目录是否就是版本文件夹
                $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
                if (-not $Script:McRoot -and (Test-Path -LiteralPath (Join-Path $scriptDir "options.txt"))) {
                    Write-Host "  检测到当前运行目录就是版本文件夹" -ForegroundColor Green
                    if ($Script:IsInteractive) {
                        $useCur = Show-Menu -Title "当前目录: $scriptDir" -Items @("是，直接使用当前目录", "否，手动选择") -Default 0
                        if ($useCur -eq 0) { $Script:McRoot = $scriptDir }
                    } else { $Script:McRoot = $scriptDir }
                }

                if (-not $Script:McRoot) {
                    Write-Step -Step 1 -Total 3 -Title "MC 根目录"
                    $found = Find-MinecraftRoot
                    if ($found) {
                        $uf = Show-Menu -Title "自动检测到: $found" -Items @("是，使用此目录", "否，手动输入") -Default 0
                        if ($uf -eq -1) { if ($step -gt 1) { $step-- }; continue }
                        if ($uf -eq 0) { $Script:McRoot = $found }
                    }
                    if (-not $Script:McRoot) {
                        $mi = Show-Input "请将版本文件夹拖入窗口或自行输入 (启动器点击版本文件夹后打开的路径)"
                        if (-not $mi) { exit 1 }
                        if ($mi -eq 'q' -or $mi -eq 'Q') { $Script:McRoot = $null; continue }
                        if (Test-Path -LiteralPath (Join-Path $mi "options.txt")) { $Script:McRoot = $mi }
                        elseif (Test-Path -LiteralPath (Join-Path $mi ".minecraft\options.txt")) { $Script:McRoot = Join-Path $mi ".minecraft" }
                        else { Write-ErrorT "该路径下未找到 options.txt"; continue }
                    }
                }
                Write-Log "MC根目录: $Script:McRoot"
                $Script:LogFile = Join-Path $Script:McRoot "update_log_$($Script:Timestamp).txt"
                Write-Log "==================== 更新开始 ===================="
                $localVer = Get-LocalVersion $Script:McRoot
                if ($localVer -eq $release.Tag) {
                    Write-Box -Lines @("已是最新版本!", "当前: $($release.Tag)") -Color Green
                    if ($Script:IsInteractive) { Write-Host "  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
                    return
                }
                $step++
            }
            2 {
                Write-Step -Step 2 -Total 3 -Title "发现新版本!"
                if ($localVer) { Write-Host "  本地: $localVer  →  $($release.Tag)" -ForegroundColor Gray }
                Write-Host ""
                Write-Box -Lines @("$($release.Name)", "大小: $([math]::Round($release.Size/1MB,1))MB") -Color Cyan
                Write-Host "  更新日志:" -ForegroundColor Cyan
                $release.Body -split "`n" | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
                Write-Host ""
                $confirm = Show-Menu -Title "是否更新?" -Items @("是 - 开始下载更新", "否 - 取消") -Default 0 -HasBack $true
                if ($confirm -eq -1) { $step--; continue }
                if ($confirm -ne 0) { Write-Host "  已取消"; return }
                $step++
            }
            3 {
                Write-Step -Step 3 -Total 3 -Title "选择下载镜像"
                $mc = Show-Menu -Title "请选择:" -Items @("自动测速 - 选最快的", "手动选择 - 自己挑镜像") -Default 0 -HasBack $true
                if ($mc -eq -1) { $step--; continue }
                if ($mc -eq 0) {
                    Write-Host "`n  正在测试镜像速度..." -ForegroundColor Cyan
                    $results = @()
                    foreach ($i in 0..($Script:DownloadMirrors.Length - 1)) {
                        $m = $Script:DownloadMirrors[$i]; Write-Host "    测试 $($m.Name)..." -NoNewline
                        $res = Test-MirrorSpeed -MirrorUrl $m.Url -TestUrl $release.Url
                        $results += @{ Index = $i; Name = $m.Name; Speed = $res.Speed }
                        if ($res.Status -eq 'ok') { Write-Host "`r  ▶ $($m.Name): $(if ($res.Speed -ge 1024){"$([math]::Round($res.Speed/1024,1))MB/s"}else{"$($res.Speed)KB/s"})" -ForegroundColor Green }
                        else { Write-Host "`r  ✗ $($m.Name): $(switch($res.Status){'blocked'{'无法连接'};'timeout'{'超时'};'dns'{'DNS失败'};default{'不可用'}})" -ForegroundColor Red }
                    }
                    $best = $results | Where-Object { $_.Speed -gt 0 } | Sort-Object Speed -Descending | Select-Object -First 1
                    if ($best) { Write-Host ""; Write-Success "最快: $($best.Name)"; $Script:SelectedMirror = $best.Index }
                    else { Write-Warn "全部不可用，使用默认" }
                } else {
                    $items = @(); foreach ($m in $Script:DownloadMirrors) { $items += $m.Name }; $items += "自定义"
                    $idx = Show-Menu -Title "请选择:" -Items $items -Default 0 -HasBack $true
                    if ($idx -eq -1) { $step--; continue }
                    if ($idx -ge 0 -and $idx -lt $Script:DownloadMirrors.Length) { $Script:SelectedMirror = $idx }
                    elseif ($idx -eq $items.Length - 1) {
                        $cu = Show-Input "请输入镜像URL" "https://gh-proxy.com/"
                        if ($cu) { $cu = $cu.Trim('/') + '/'; $Script:DownloadMirrors += @{Name="自定义 ($cu)"; Url=$cu}; $Script:SelectedMirror = $Script:DownloadMirrors.Length - 1 }
                    }
                }
                $step++
            }
            4 {
                Write-Step -Step 3 -Total 3 -Title "用户配置文件"
                $cfgChoice = Show-Menu -Title "是否保留本地配置?" -Items @("是 - 保留 options.txt, servers.dat 及自定义config", "否 - 使用发布版文件覆盖") -Default 1 -HasBack $true
                if ($cfgChoice -eq -1) { $step--; continue }
                $Script:PreserveConfig = ($cfgChoice -eq 0)
                Write-Log "保留配置: $($Script:PreserveConfig)"
                $confirmed = $true
            }
        }
    }

    # 非交互模式下没有 step 循环, 手动处理 MC 根目录 + 日志
    if (-not $Script:IsInteractive) {
        if (-not $Script:McRoot) { $Script:McRoot = Get-UserMcRoot }
        Write-Log "MC根目录: $Script:McRoot"
        $Script:LogFile = Join-Path $Script:McRoot "update_log_$($Script:Timestamp).txt"
        Write-Log "==================== 更新开始 ===================="
        $localVer = Get-LocalVersion $Script:McRoot
        if ($localVer -eq $release.Tag) { Write-Log "已是最新版本"; exit 0 }
    }

    # 备份 + 临时目录
    $backupBase = Join-Path $Script:McRoot $Script:BackupDir
    $backupSession = Join-Path $backupBase $Script:Timestamp
    New-Item -ItemType Directory -Path $backupSession -Force | Out-Null
    Write-Log "备份目录: $backupSession"
    $Script:TempRepo = Join-Path ([System.IO.Path]::GetTempPath()) "mc_modpack_update_$($Script:Timestamp)"
    New-Item -ItemType Directory -Path $Script:TempRepo -Force | Out-Null
    Write-Log "临时目录: $Script:TempRepo"

    try {
        # 下载 Release
        Write-Host ""
        Write-Host "  正在下载 $($release.Name) ($([math]::Round($release.Size/1MB,1))MB)..." -ForegroundColor Cyan
        Invoke-AssetDownload -AssetUrl $release.Url -TargetPath $Script:TempRepo

        # 备份
        Backup-ProtectedFiles -McRoot $Script:McRoot -BackupBase $backupSession
        if ($Script:PreserveConfig) { Backup-CustomConfigs -McRoot $Script:McRoot -RepoRoot $Script:TempRepo -BackupBase $backupSession }

        # 同步前验证 (仅警告, 不阻塞)
        foreach ($f in $Script:SyncFolders) {
            $chk = Join-Path $Script:TempRepo $f
            if (Test-Path $chk) {
                $sz = (Get-ChildItem $chk -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB
                if ($sz -lt 0.1) { Write-Log "$f/ 目录较小 ($([math]::Round($sz,2))MB), 将跳过" "WARN" }
            }
        }

        # 同步
        Sync-Folders -McRoot $Script:McRoot -RepoRoot $Script:TempRepo

        # 恢复
        Restore-ProtectedFiles -McRoot $Script:McRoot -BackupBase $backupSession
        if ($Script:PreserveConfig) { Restore-CustomConfigs -McRoot $Script:McRoot -BackupBase $backupSession }

        # 写入版本
        Set-LocalVersion $Script:McRoot $release.Tag
        Write-Log "版本已更新: $($release.Tag)"

        # 清理临时目录 (在显示完成信息之前)
        Cleanup-Temp

        Write-Log "==================== 更新完成 ===================="
        Write-Host ""
        Write-Success "更新成功! $($release.Name)"
        Write-Host "  MC根目录: $($Script:McRoot)" -ForegroundColor Gray
        Write-Host "  备份目录: $backupSession" -ForegroundColor Gray
        Write-Host "  日志文件: $($Script:LogFile)" -ForegroundColor Gray
        if ($Script:IsInteractive) { Write-Host "`n  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
    }
    catch {
        # 清理临时目录 + 恢复备份
        Cleanup-Temp
        Restore-ProtectedFiles -McRoot $Script:McRoot -BackupBase $backupSession
        if ($Script:PreserveConfig) { Restore-CustomConfigs -McRoot $Script:McRoot -BackupBase $backupSession }
        Write-Log "已尝试恢复备份文件"
        Write-Log "发生错误: $_" "ERROR"
        Write-Host ""
        Write-ErrorT "更新过程中出现错误，请检查日志: $($Script:LogFile)"
        if ($Script:IsInteractive) { Write-Host "`n  按任意键退出..."; [Console]::ReadKey($true) | Out-Null }
        exit 1
    }
}

# 入口
Main -ScriptArgs $args
