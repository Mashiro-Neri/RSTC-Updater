#Requires -Version 7.0
<#
从「原始客户端」组装可发布 mrpack：只读取客户端目录，绝不写入；输出到指定 zip。
默认包含：mods/config/resourcepacks/emotes/fancymenu_data/data/defaultconfigs + options.txt/servers.dat
默认排除：PCL、存档/截图/日志/小地图/DH 缓存/回放/缓存/natives/版本 json/jar/启动器配置等。
用法:
  pwsh -File scripts\release\build-pack.ps1 `
    -ClientVersionDir <...\.minecraft\versions\26.3-Fabric> `
    -OutZip <项目内路径>\evidence\release\RSTC-6.0.zip
#>
param(
    [Parameter(Mandatory = $true)][string]$ClientVersionDir,
    [Parameter(Mandatory = $true)][string]$OutZip,
    [string]$VersionId = '6.0',
    [string]$Minecraft = '26.3',
    [string]$FabricLoader = '0.20.1',
    [string]$Name = '红石镇客户端 6.0',
    [string]$Summary = '正式版 26.3, Fabric 0.20.1'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$src = [System.IO.Path]::GetFullPath($ClientVersionDir)
if (-not (Test-Path -LiteralPath $src)) {
    Write-Host "[FAIL] 客户端目录不存在: $src" -ForegroundColor Red
    exit 2
}

$includeDirs = @('mods', 'config', 'resourcepacks', 'emotes', 'fancymenu_data', 'data', 'defaultconfigs', 'schematics')
$includeFiles = @('options.txt', 'servers.dat')

$staging = Join-Path $env:TEMP ("rstc-pack-{0}-{1}" -f $VersionId, (Get-Date -Format 'yyyyMMdd_HHmmss'))
$overrides = Join-Path $staging 'overrides'
New-Item -ItemType Directory -Force $overrides | Out-Null

try {
    $copiedFiles = 0
    $copiedBytes = 0
    $report = @()
    foreach ($dir in $includeDirs) {
        $from = Join-Path $src $dir
        if (-not (Test-Path -LiteralPath $from)) {
            $report += "skip  $dir（客户端中不存在）"
            continue
        }

        $to = Join-Path $overrides $dir
        Copy-Item -LiteralPath $from -Destination $to -Recurse -Force
        $files = Get-ChildItem -LiteralPath $to -Recurse -File
        $bytes = ($files | Measure-Object Length -Sum).Sum
        $copiedFiles += $files.Count
        $copiedBytes += $bytes
        $report += ("add   {0,-15} {1,5} files {2,8:F1} MiB" -f $dir, $files.Count, ($bytes / 1MB))
    }

    foreach ($file in $includeFiles) {
        $from = Join-Path $src $file
        if (-not (Test-Path -LiteralPath $from)) {
            $report += "skip  $file（客户端中不存在）"
            continue
        }

        Copy-Item -LiteralPath $from -Destination (Join-Path $overrides $file) -Force
        $len = (Get-Item -LiteralPath $from).Length
        $copiedFiles++
        $copiedBytes += $len
        $report += ("add   {0,-15} {1,5} files {2,8:F1} KiB" -f $file, 1, ($len / 1KB))
    }

    $index = [ordered]@{
        game          = 'minecraft'
        formatVersion = 1
        versionId     = $VersionId
        name          = $Name
        summary       = $Summary
        files         = @()
        dependencies  = [ordered]@{
            minecraft      = $Minecraft
            'fabric-loader' = $FabricLoader
        }
    }
    $indexJson = $index | ConvertTo-Json -Depth 5
    $indexJson | Set-Content -LiteralPath (Join-Path $staging 'modrinth.index.json') -Encoding utf8

    $outFull = [System.IO.Path]::GetFullPath($OutZip)
    $outDir = Split-Path -Parent $outFull
    if ($outDir) { New-Item -ItemType Directory -Force $outDir | Out-Null }
    if (Test-Path -LiteralPath $outFull) { Remove-Item -LiteralPath $outFull -Force }

    Write-Host "[1/3] 组装 staging 完成: $copiedFiles files / $([math]::Round($copiedBytes/1MB,1)) MiB"
    $report | ForEach-Object { Write-Host "      $_" }

    Write-Host "[2/3] 压缩为 $outFull …"
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $outFull,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "[3/3] 校验产物 …"
    $zip = [System.IO.Compression.ZipFile]::OpenRead($outFull)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
        $pcl = @($names | Where-Object { $_ -match '^PCL/' -or $_ -match '^overrides/PCL/' })
        $idxEntry = $zip.Entries | Where-Object { $_.FullName -replace '\\', '/' -eq 'modrinth.index.json' } | Select-Object -First 1
        $reader = New-Object System.IO.StreamReader($idxEntry.Open())
        try { $check = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $mods = @($names | Where-Object { $_ -match '^overrides/mods/.*\.jar$' })
        $info = Get-Item -LiteralPath $outFull
        $sha = (Get-FileHash -LiteralPath $outFull -Algorithm SHA256).Hash

        Write-Host ""
        Write-Host "zip      : $($info.Name)"
        Write-Host "size     : $($info.Length) bytes ($([math]::Round($info.Length/1MB,1)) MiB)"
        Write-Host "sha256   : $sha"
        Write-Host "entries  : $($zip.Entries.Count)（mods jars=$($mods.Count)）"
        Write-Host "versionId: $($check.versionId) / MC $($check.dependencies.minecraft) / loader $($check.dependencies.'fabric-loader')"
        $bad = @()
        if ($pcl.Count -gt 0) { $bad += "包含 PCL/（$($pcl.Count) 项）" }
        if ($check.dependencies.minecraft -ne $Minecraft) { $bad += "MC 依赖不符" }
        if ($mods.Count -eq 0) { $bad += "没有 mods" }
        if ($bad.Count -gt 0) {
            Write-Host ("[FAIL] " + ($bad -join '; ')) -ForegroundColor Red
            exit 1
        }

        $sha | Set-Content -LiteralPath ($outFull + '.sha256.txt') -Encoding ascii
        Write-Host "PACK_BUILD=PASS" -ForegroundColor Green
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
