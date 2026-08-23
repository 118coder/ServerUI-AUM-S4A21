# v1.917: AUM 管理器自更新脚本
$ErrorActionPreference = "Continue"
$ScriptRoot = $PSScriptRoot
if ((Get-Item $ScriptRoot).Name -eq 'ps1核心') { $ScriptRoot = (Get-Item $ScriptRoot).Parent.FullName }

Write-Host ""
Write-Host "========================================"
Write-Host "  AUM 管理器自更新 v1.917"
Write-Host "========================================"
Write-Host ""

Write-Host "[1/5] 查找 .NET 10 SDK..."
$dn = $null
try { $v = & dotnet --version 2>&1; if ($LASTEXITCODE -eq 0 -and $v -match '^\d+\.' -and [int]$Matches[1] -ge 10) { $dn = "dotnet" } } catch {}
if (-not $dn) { $pf = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"; if (Test-Path $pf) { $dn = $pf } }
if (-not $dn) { $l = Join-Path $ScriptRoot "dotnet-sdk\dotnet.exe"; if (Test-Path $l) { $dn = $l } }
if (-not $dn) { Write-Host "ERROR: 未找到 .NET 10 SDK!"; Read-Host "按任意键退出"; exit 1 }
$ver = & $dn --version 2>&1
Write-Host "  SDK: v$($ver.Trim())"

Write-Host ""
Write-Host "[2/5] 检测 GitHub 最新版本..."
$remoteVer = $null
$localVerFile = Join-Path $ScriptRoot "AUM-version.txt"
$localVer = if (Test-Path $localVerFile) { (Get-Content $localVerFile -Encoding UTF8).Trim() } else { "0" }
Write-Host "  本地版本: v$localVer"

try {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $resp = Invoke-WebRequest -Uri "https://raw.githubusercontent.com/118coder/ServerUI-AUM-S4A21/main/AUM-version.txt?t=$ts" -UseBasicParsing -TimeoutSec 10 -Headers @{ "User-Agent" = "ServerUI-AUM"; "Cache-Control" = "no-cache" }
    $remoteVer = $resp.Content.Trim() -replace '\s+', ''
    Write-Host "  远程版本: v$remoteVer (Raw)"
} catch {
    try {
        $apiUrl = "https://api.github.com/repos/118coder/ServerUI-AUM-S4A21/contents/AUM-version.txt?ref=main&t=$ts"
        $resp = Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec 10 -Headers @{ "User-Agent" = "ServerUI-AUM" }
        $json = $resp.Content | ConvertFrom-Json
        if ($json.content) {
            $bytes = [Convert]::FromBase64String(($json.content -replace '\s', ''))
            $remoteVer = [System.Text.Encoding]::UTF8.GetString($bytes).Trim()
            Write-Host "  远程版本: v$remoteVer (API)"
        }
    } catch {
        Write-Host "  ERROR: 无法连接 GitHub，请检查网络。"
        Read-Host "按任意键退出"
        exit 1
    }
}

if (-not $remoteVer) { Write-Host "ERROR: 获取远程版本失败。"; Read-Host "按任意键退出"; exit 1 }

function Compare-Version($a, $b) {
    $pa = @($a -split '[.\-_]' | ForEach-Object { $_ -replace '[^0-9]','' } | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
    $pb = @($b -split '[.\-_]' | ForEach-Object { $_ -replace '[^0-9]','' } | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
    $len = [Math]::Max($pa.Count, $pb.Count)
    for ($i = 0; $i -lt $len; $i++) {
        $va = if ($i -lt $pa.Count) { $pa[$i] } else { 0 }
        $vb = if ($i -lt $pb.Count) { $pb[$i] } else { 0 }
        if ($va -ne $vb) { return $va.CompareTo($vb) }
    }
    return 0
}

$cmp = Compare-Version $remoteVer $localVer
if ($cmp -le 0) {
    Write-Host ""
    if ($cmp -lt 0) { Write-Host "当前版本 v$localVer 高于远程 v$remoteVer (开发版)，无需更新。" }
    else { Write-Host "已是最新版本 v$localVer。" }
    Write-Host ""
    $choice = Read-Host "输入 1 强制重新下载编译，或直接回车退出"
    if ($choice -ne "1") { Read-Host "按任意键退出"; exit 0 }
    Write-Host "强制重新编译模式..."
}

if ($cmp -gt 0) {
    Write-Host ""
    Write-Host "发现新版本 v$remoteVer！当前 v$localVer"
    $confirm = Read-Host "是否进行更新? (Y/N)"
    if ($confirm -ne "Y" -and $confirm -ne "y") { Write-Host "已取消。"; Read-Host "按任意键退出"; exit 0 }
}

Write-Host ""
Write-Host "[3/5] 下载最新源码..."
$tmpDir = Join-Path $env:TEMP "AUM-selfupdate"
if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir }
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
$tmpZip = Join-Path $tmpDir "source.zip"

$zipUrl = "https://github.com/118coder/ServerUI-AUM-S4A21/archive/refs/heads/main.zip"
$ok = $false
for ($a = 1; $a -le 3; $a++) {
    try {
        Write-Host "  下载中 (第${a}次)..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -UseBasicParsing -TimeoutSec 60 -Headers @{ "User-Agent" = "ServerUI-AUM" }
        if ((Test-Path $tmpZip) -and (Get-Item $tmpZip).Length -gt 10240) { $ok = $true; break }
    } catch { }
    if ($a -lt 3) { Start-Sleep -Seconds 3 }
}
if (-not $ok) { Write-Host "ERROR: 下载失败。"; Read-Host "按任意键退出"; exit 1 }
Write-Host "  下载完成: $([math]::Round((Get-Item $tmpZip).Length/1KB)) KB"

Write-Host ""
Write-Host "[4/5] 编译新版本..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmpExtract = Join-Path $tmpDir "extract"
if (Test-Path $tmpExtract) { Remove-Item -Recurse -Force $tmpExtract }
[System.IO.Compression.ZipFile]::ExtractToDirectory($tmpZip, $tmpExtract)

$rootDir = $tmpExtract
$subDir = Get-ChildItem $tmpExtract -Directory | Where-Object { $_.Name -match "ServerUI|S4A12" } | Select-Object -First 1
if ($subDir) { $rootDir = $subDir.FullName }
$srcDir = Join-Path $rootDir "ServerUI"
if (-not (Test-Path $srcDir)) {
    $alt = Get-ChildItem $rootDir -Directory -Recurse -Filter "ServerUI" | Select-Object -First 1
    if ($alt) { $srcDir = $alt.FullName }
}
if (-not (Test-Path (Join-Path $srcDir "ServerUI.csproj"))) {
    Write-Host "ERROR: 源码结构异常。"
    Read-Host "按任意键退出"
    exit 1
}

foreach ($sub in @("Services", "Models")) {
    $subPath = Join-Path $srcDir $sub
    if (-not (Test-Path $subPath)) { continue }
    Get-ChildItem $subPath -File -Filter "*.cs" | ForEach-Object {
        $rootPath = Join-Path $srcDir $_.Name
        if (Test-Path $rootPath) { Remove-Item $rootPath -Force }
    }
}

$pubDir = Join-Path $tmpDir "publish"
New-Item -ItemType Directory -Path $pubDir -Force | Out-Null
Write-Host "  编译框架依赖版..."
$proj = Join-Path $srcDir "ServerUI.csproj"
& $dn restore $proj --ignore-failed-sources 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: restore 失败。"; Read-Host "按任意键退出"; exit 1 }
& $dn publish $proj -c Release -r win-x64 --no-self-contained -o $pubDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    & $dn publish $proj -c Release -r win-x64 --no-self-contained -o $pubDir 2>&1 | Out-Null
}
$newExe = Join-Path $pubDir "ServerUI.exe"
if (-not (Test-Path $newExe) -or (Get-Item $newExe).Length -lt 10240) {
    Write-Host "ERROR: 编译失败。"
    Read-Host "按任意键退出"
    exit 1
}
Write-Host "  框架依赖版: $([math]::Round((Get-Item $newExe).Length/1KB)) KB"

$pubScDir = Join-Path $tmpDir "publish-sc"
New-Item -ItemType Directory -Path $pubScDir -Force | Out-Null
Write-Host "  编译自包含版..."
& $dn publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o $pubScDir 2>&1 | Out-Null
$newScExe = Join-Path $pubScDir "ServerUI.exe"

Write-Host ""
Write-Host "[5/5] 替换文件..."
Get-Process -Name "ServerUI" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$parentDir = (Get-Item $ScriptRoot).Parent.FullName

if (Test-Path $newScExe) {
    Copy-Item $newScExe (Join-Path $parentDir "开始游戏-ServerUI.exe") -Force
    Write-Host "  开始游戏-ServerUI.exe -> 上层目录: OK"
}
Copy-Item $newExe (Join-Path $parentDir "ServerUI-无依赖版.exe") -Force
Write-Host "  ServerUI-无依赖版.exe -> 上层目录: OK"

$localSrc = Join-Path $ScriptRoot "ServerUI"
if (-not (Test-Path $localSrc)) { New-Item -ItemType Directory -Path $localSrc -Force | Out-Null }
Get-ChildItem $srcDir -File -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($srcDir.Length).TrimStart('\')
    if ($rel -match '(^|\\)(bin|obj)(\\|$)') { return }
    $dst = Join-Path $localSrc $rel
    $dstDir = Split-Path $dst -Parent
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    Copy-Item $_.FullName $dst -Force
}
Write-Host "  源码已同步"

$ps1CoreDir = Join-Path $ScriptRoot "ps1核心"
New-Item -ItemType Directory -Path $ps1CoreDir -Force | Out-Null
foreach ($f in Get-ChildItem $rootDir -File -Filter "*.ps1") {
    $name = $f.Name
    if ($name -match "GameLog|运行日志") { continue }
    Copy-Item $f.FullName (Join-Path $ps1CoreDir $name) -Force
}
foreach ($f in Get-ChildItem $rootDir -File -Filter "*.bat") {
    $name = $f.Name
    Copy-Item $f.FullName (Join-Path $ScriptRoot $name) -Force
}
foreach ($f in Get-ChildItem $rootDir -File -Filter "*.txt", "*.md") {
    $name = $f.Name
    if ($name -match "GameLog|运行日志") { continue }
    Copy-Item $f.FullName (Join-Path $ScriptRoot $name) -Force
}

Set-Content -Path $localVerFile -Value $remoteVer -Encoding UTF8
Write-Host "  AUM-version.txt -> v$remoteVer"

if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue }

Write-Host ""
if ($cmp -gt 0) { Write-Host "  更新完成! v$localVer -> v$remoteVer" }
else { Write-Host "  强制重编译完成! v$localVer (源码已刷新)" }
Write-Host "========================================"
Write-Host ""
Read-Host "按任意键退出"