#Requires -Version 7.0
<#
发布侧校验：检查整合包 zip / 更新器 zip 是否满足 v5 契约。
- pack：根 modrinth.index.json、versionId、dependencies.minecraft、overrides/、**不得含 PCL/**、mods 数量
- updater：含 RstcUpdater.exe、不得含 PCL/、DLL/脚本清单
用法:
  pwsh -File scripts\release\validate-pack.ps1 -ZipPath <zip> [-ExpectedMinecraft 26.3] [-Kind pack|updater]
#>
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ExpectedMinecraft = '26.3',
    [ValidateSet('pack', 'updater')][string]$Kind = 'pack'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $ZipPath)) {
    Write-Host "[FAIL] zip 不存在: $ZipPath" -ForegroundColor Red
    exit 2
}

$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }
    $checks = 0
    $failures = New-Object System.Collections.Generic.List[string]
    function Check([string]$name, [bool]$ok, [string]$detail = '') {
        $script:checks++
        if ($ok) { Write-Host "[OK]   $name $detail" -ForegroundColor Green }
        else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $failures.Add("$name $detail") }
    }

    $pclEntries = @($entries | Where-Object { $_ -match '^PCL/' -or $_ -match '^overrides/PCL/' })
    Check '不包含 PCL/（用户硬要求）' ($pclEntries.Count -eq 0) "found=$($pclEntries.Count)"

    if ($Kind -eq 'pack') {
        $indexEntry = $zip.Entries | Where-Object { $_.FullName -replace '\\', '/' -eq 'modrinth.index.json' } | Select-Object -First 1
        Check '根目录有 modrinth.index.json' ($null -ne $indexEntry)
        if ($indexEntry) {
            $reader = New-Object System.IO.StreamReader($indexEntry.Open())
            try { $index = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            Check 'versionId 非空' ([bool]$index.versionId) "versionId=$($index.versionId)"
            Check "dependencies.minecraft = $ExpectedMinecraft" ($index.dependencies.minecraft -eq $ExpectedMinecraft) "actual=$($index.dependencies.minecraft)"
        }

        $overrides = @($entries | Where-Object { $_ -match '^overrides/' })
        Check '有 overrides/' ($overrides.Count -gt 0) "count=$($overrides.Count)"
        $mods = @($overrides | Where-Object { $_ -match '^overrides/mods/.*\.jar$' })
        Check '包含 mods/*.jar' ($mods.Count -gt 0) "mods=$($mods.Count)"
        $top = $overrides | ForEach-Object { ($_ -split '/')[1] } | Sort-Object -Unique
        Write-Host "[INFO] overrides 顶层: $($top -join ', ')"
    }
    else {
        $exe = @($entries | Where-Object { $_ -match '^RstcUpdater\.exe$' })
        Check '含 RstcUpdater.exe（zip 根）' ($exe.Count -eq 1)
        $dlls = @($entries | Where-Object { $_ -match '\.dll$' })
        Check '含依赖 DLL' ($dlls.Count -gt 0) "dll=$($dlls.Count)"
        $scripts = @($entries | Where-Object { $_ -match '\.(bat|ps1|psm1)$' })
        Write-Host "[INFO] 脚本/批处理: $($scripts.Count) 个"
    }

    $info = Get-Item -LiteralPath $ZipPath
    $sha = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    Write-Host ""
    Write-Host "zip: $($info.Name)  size=$($info.Length)  sha256=$sha"
    Write-Host "kind=$Kind checks=$($checks) failures=$($failures.Count)"
    if ($failures.Count -gt 0) {
        Write-Host ("FAILED: " + ($failures -join '; ')) -ForegroundColor Red
        exit 1
    }

    Write-Host 'PACK_VALIDATE=PASS' -ForegroundColor Green
    exit 0
}
finally {
    $zip.Dispose()
}
