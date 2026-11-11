<#
捕获阶段 0-1 探针证据，并按 commit 绑定。

用法（项目根）:
  powershell -ExecutionPolicy Bypass -File scripts\spike\capture-probe-evidence.ps1
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$commit = (git rev-parse HEAD).Trim()
$dir = Join-Path $RepoRoot "evidence\stage0\net48-probe\$commit"
New-Item -ItemType Directory -Force $dir | Out-Null
Write-Host "evidence dir: $dir"

# 1. 构建
dotnet build src\RstcUpdater\RstcUpdater.csproj -c Release -nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $dir 'build.log') -Encoding utf8 |
    Select-Object -Last 5
$buildExit = $LASTEXITCODE

$exe = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48\RstcUpdater.exe'

# 2. 成功路径
& $exe --probe 2>&1 | Tee-Object -FilePath (Join-Path $dir 'probe.ok.txt') -Encoding utf8
$okExit = $LASTEXITCODE
"EXIT=$okExit" | Tee-Object -Append -FilePath (Join-Path $dir 'probe.ok.txt') -Encoding utf8

# 3. 失败路径
& $exe --probe --url 'https://127.0.0.1:9/x' 2>&1 | Tee-Object -FilePath (Join-Path $dir 'probe.fail.txt') -Encoding utf8
$failExit = $LASTEXITCODE
"EXIT=$failExit" | Tee-Object -Append -FilePath (Join-Path $dir 'probe.fail.txt') -Encoding utf8

# 4. 参数边界
$boundary = @()
$boundary += "--url-missing: " + $( & $exe --probe --url 2>&1 | Out-String; $LASTEXITCODE )
$boundary += "--bogus: " + $( & $exe --probe --bogus 2>&1 | Out-String; $LASTEXITCODE )
$boundary += "--help: " + $( & $exe --help 2>&1 | Out-String; $LASTEXITCODE )
$boundary | Out-File -FilePath (Join-Path $dir 'cli-boundary.txt') -Encoding utf8

# 5. 产物 + 哈希
$outDir = Join-Path $RepoRoot 'src\RstcUpdater\bin\Release\net48'
Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name |
    Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize | Out-File -FilePath (Join-Path $dir 'artifacts.txt') -Encoding utf8

Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$hash  $($_.Name)"
} | Out-File -FilePath (Join-Path $dir 'SHA256SUMS') -Encoding ascii

# 6. 身份/环境事实
$facts = @()
$facts += "commit=$(git rev-parse HEAD)"
$facts += "build_exit=$buildExit"
$facts += "probe_ok_exit=$okExit"
$facts += "probe_fail_exit=$failExit"
$facts += "product_version=" + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
$facts += "os=" + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName) +
          ' ' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion) +
          ' build ' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild) +
          '.' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)
$facts += 'dotnet_release=' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release)
$facts += 'dotnet_version=' + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Version)
$facts += 'dotnet_sdk=' + (dotnet --version)
$facts += 'time=' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
$facts | Out-File -FilePath (Join-Path $dir 'facts.txt') -Encoding utf8

Write-Host "commit=$commit"
Write-Host "build=$buildExit probe_ok=$okExit probe_fail=$failExit"
Write-Host "done: $dir"

if ($buildExit -ne 0 -or $okExit -ne 0 -or $failExit -ne 5) { exit 1 }
