#requires -Version 5.1
<#
.SYNOPSIS
    生成 AUM 发行包（latest.json + 3 个 zip），供【更新AUM】的「发行包直替」通道使用。

.DESCRIPTION
    长期可用的发版脚本。步骤：
      1. 校验/写入 AUM-version.txt（与 -Version 一致）
      2. dotnet publish 三个变体（有依赖单文件 / 无依赖单文件 / 兼容模式，若存在）
      3. 打包成 zip（内含 exe，兼容模式额外带 .config）
      4. 计算 SHA256 与字节数
      5. 生成 latest.json（schema/version/channel/assets/minSupportedVersion/notesUrl）
      6. 输出 release-files/ 清单

.PARAMETER Version
    目标版本，形如 2.21 或 2.21.1。

.PARAMETER OutDir
    输出目录，默认 <repo>/release-files。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -Version 2.21

.NOTES
    只写 OutDir 与 AUM-version.txt，不提交、不推送、不创建 Release。
    发布由 .github/workflows/release.yml 在打 tag 时完成（或手工上传 OutDir 内的 4 个文件）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$scriptRoot = $PSScriptRoot
$repoRoot = (Get-Item $scriptRoot).Parent.FullName
$webProj = Join-Path $repoRoot 'ServerUI.Web\ServerUI.Web.csproj'
$verFile = Join-Path $repoRoot 'AUM-version.txt'

if (-not (Test-Path $webProj)) { throw "找不到 $webProj（请在仓库根运行本脚本）" }
if ($Version -notmatch '^\d+\.\d+(\.\d+)?$') { throw "版本号格式应为 x.y 或 x.y.z，实际：$Version" }

if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'release-files' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Write-Step($t) { Write-Host ""; Write-Host "==> $t" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. 版本文件
Write-Step "同步 AUM-version.txt → $Version"
$current = if (Test-Path $verFile) { (Get-Content $verFile -Raw -Encoding UTF8).Trim() } else { '' }
if ($current -ne $Version) {
    Set-Content -Path $verFile -Value $Version -Encoding UTF8 -NoNewline
    Write-Host "    已更新（原值：$current）"
} else {
    Write-Host "    已一致"
}

# ---------------------------------------------------------------- 2. 发布
$dotnet = 'dotnet'
$ver = & $dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or -not $ver -or [int]($ver.Split('.')[0]) -lt 10) {
    throw "需要 .NET 10 SDK（当前：$ver）"
}
Write-Host "    SDK: $ver"

$publishRoot = Join-Path $OutDir '.publish'
$fdDir = Join-Path $publishRoot 'fd'
$scDir = Join-Path $publishRoot 'sc'

Write-Step '发布 有依赖版（框架依赖单文件）'
& $dotnet publish $webProj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -o $fdDir
if ($LASTEXITCODE -ne 0) { throw '有依赖版发布失败' }
$fdExe = Join-Path $fdDir 'ServerUI.exe'
if (-not (Test-Path $fdExe)) { throw '有依赖版产物缺失' }

Write-Step '发布 无依赖版（自包含单文件）'
& $dotnet publish $webProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -o $scDir
if ($LASTEXITCODE -ne 0) { throw '无依赖版发布失败' }
$scExe = Join-Path $scDir 'ServerUI.exe'
if (-not (Test-Path $scExe)) { throw '无依赖版产物缺失' }

# ---------------------------------------------------------------- 3. 组装 zip
$stage = Join-Path $OutDir '.stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function New-Zip([string]$zipPath, [hashtable]$files) {
    $work = Join-Path $stage ([IO.Path]::GetFileNameWithoutExtension($zipPath))
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    foreach ($dst in $files.Keys) {
        Copy-Item -LiteralPath $files[$dst] -Destination (Join-Path $work $dst) -Force
    }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item $work -Recurse -Force
}

Write-Step '打包'
$fdName = 'ServerUI-有依赖版.exe'
$scName = 'ServerUI-无依赖版.exe'
New-Zip (Join-Path $OutDir 'ServerUI-fd.zip') @{ $fdName = $fdExe }
New-Zip (Join-Path $OutDir 'ServerUI-sc.zip') @{ $scName = $scExe }

# 兼容模式（可选）：仓库里有 net48 工程就一起发
$compatProj = Join-Path $repoRoot 'ServerUI\ServerUI-Win7.csproj'
$assets = @()
$assets += [ordered]@{ kind = 'fd'; name = 'ServerUI-fd.zip'; inner = $fdName }
$assets += [ordered]@{ kind = 'sc'; name = 'ServerUI-sc.zip'; inner = $scName }

if (Test-Path $compatProj) {
    Write-Step '发布 兼容模式（net48）'
    $compatDir = Join-Path $publishRoot 'compat'
    & $dotnet publish $compatProj -c Release -o $compatDir
    if ($LASTEXITCODE -eq 0) {
        $cExe = Join-Path $compatDir 'ServerUI-Win7.exe'
        $cCfg = "$cExe.config"
        if (Test-Path $cExe) {
            $files = @{ 'ServerUI-兼容模式.exe' = $cExe }
            if (Test-Path $cCfg) { $files['ServerUI-兼容模式.exe.config'] = $cCfg }
            New-Zip (Join-Path $OutDir 'ServerUI-compat.zip') $files
            $assets += [ordered]@{ kind = 'compat'; name = 'ServerUI-compat.zip'; inner = 'ServerUI-兼容模式.exe' }
        } else { Write-Host '    兼容模式产物缺失，跳过' -ForegroundColor Yellow }
    } else {
        Write-Host '    兼容模式编译失败，跳过（不影响主版本）' -ForegroundColor Yellow
    }
} else {
    Write-Host '    仓库无 ServerUI-Win7.csproj，跳过兼容模式' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 4. 哈希 + 清单
Write-Step '计算 SHA256 并生成 latest.json'
foreach ($a in $assets) {
    $p = Join-Path $OutDir $a.name
    $a.sha256 = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    $a.size = (Get-Item -LiteralPath $p).Length
}

$notesUrl = "https://github.com/118coder/ServerUI-AUM-S4A21/releases/tag/v$Version"
$manifest = [ordered]@{
    schema              = 1
    version             = $Version
    channel             = 'stable'
    minSupportedVersion = '2.0'
    notesUrl            = $notesUrl
    assets              = @($assets | ForEach-Object {
        [ordered]@{ kind = $_.kind; name = $_.name; sha256 = $_.sha256; size = $_.size; inner = $_.inner }
    })
}
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $OutDir 'latest.json'), $json, (New-Object Text.UTF8Encoding($false)))

Remove-Item $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 5. 清单输出
Write-Step '完成'
Write-Host ""
Write-Host "输出目录：$OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -File | Sort-Object Name | ForEach-Object {
    "{0,-28} {1,12:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  1) 提交 AUM-version.txt 与本脚本生成物（release-files/ 默认不提交）"
Write-Host "  2) 打 tag： git tag v$Version && git push origin v$Version"
Write-Host "  3) GitHub Actions 会创建 Release 并上传 latest.json 与 3 个 zip"
Write-Host "  4) 客户端【更新AUM】即可走发行包直替（或源码编译，视环境而定）"
