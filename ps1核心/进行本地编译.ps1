# ==================================================================
#  本地编译 ServerS4A12 — 兜底策略
#  当 ServerUI.exe 不可用时, 双击 进行本地编译.bat 调用此脚本
# ==================================================================
$ErrorActionPreference = "Continue"
$ScriptRoot = Split-Path -Parent (Get-Command $PSCommandPath | Select-Object -ExpandProperty Source)
if ((Get-Item $ScriptRoot).Name -eq 'ps1核心') { $ScriptRoot = (Get-Item $ScriptRoot).Parent.FullName }
$LatestDir = Join-Path $ScriptRoot "latest"
$SvrZip = Join-Path $LatestDir "ServerS4A12-latest.zip"
$GmZip = Join-Path $LatestDir "DfoGmTool-latest.zip"

Write-Host ""
Write-Host "========================================"
Write-Host "  ServerS4A12 本地编译 (兜底策略)"
Write-Host "========================================"
Write-Host ""

Write-Host "[1/5] 检查本地缓存..."
if (-not (Test-Path $SvrZip)) { Write-Host "ERROR: 缺少 $SvrZip"; Read-Host "按任意键退出"; exit 1 }
$SvrSize = [math]::Round((Get-Item $SvrZip).Length / 1KB)
Write-Host "  ServerS4A12-latest.zip = $SvrSize KB"
if (Test-Path $GmZip) { 
    $GmSize = [math]::Round((Get-Item $GmZip).Length / 1KB)
    Write-Host "  DfoGmTool-latest.zip    = $GmSize KB" 
} else { 
    Write-Host "  DfoGmTool-latest.zip: 缺失 (跳过GM编译)" 
}

Write-Host ""
Write-Host "[2/5] 查找 .NET SDK..."
$dn = $null
try { $v = & dotnet --version 2>&1; if ($LASTEXITCODE -eq 0 -and $v -match "^\d+\." -and [int]$Matches[1] -ge 10) { $dn = "dotnet" } } catch {}
if (-not $dn) { $pf = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"; if (Test-Path $pf) { $dn = $pf } }
if (-not $dn) { $l = Join-Path $ScriptRoot "dotnet-sdk\dotnet.exe"; if (Test-Path $l) { $dn = $l } }
if (-not $dn) { Write-Host "ERROR: 未找到 .NET 10 SDK! 请安装后再运行。"; Read-Host "按任意键退出"; exit 1 }
$ver = & $dn --version 2>&1
Write-Host "  SDK: v$($ver.Trim())"

Write-Host ""
Write-Host "[3/5] 解压与同步..."
$TempDir = Join-Path $env:TEMP "ServerS4A12-localbuild"
if (Test-Path $TempDir) { Remove-Item -Recurse -Force $TempDir }
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
$SvrExtract = Join-Path $TempDir "server-extract"
$GmExtract = Join-Path $TempDir "gm-extract"
New-Item -ItemType Directory -Path $SvrExtract -Force | Out-Null
New-Item -ItemType Directory -Path $GmExtract -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

Write-Host "  解压服务端源码..."
[System.IO.Compression.ZipFile]::ExtractToDirectory($SvrZip, $SvrExtract)
$svrSub = Get-ChildItem $SvrExtract -Directory | Select-Object -First 1
if (-not $svrSub) { Write-Host "ERROR: 服务端ZIP结构异常"; Read-Host "按任意键退出"; exit 1 }
$svrSrc = $svrSub.FullName

$SrcRoot = Join-Path $ScriptRoot "ServerS4A12-AUM"

Write-Host "  同步服务端文件..."
$ch = 0
Get-ChildItem $svrSrc -File -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($svrSrc.Length).TrimStart('\')
    if ($rel -match '(^|\\)(\.git|dist)(\\|$)') { return }
    if ($rel -match '(^|\\)start-server\.(bat|sh)$') { return }
    $dst = Join-Path $SrcRoot $rel
    $dstDir = Split-Path $dst -Parent
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    $exist = Get-Item $dst -ErrorAction SilentlyContinue
    if ($exist -and $exist.Length -eq $_.Length -and $exist.LastWriteTimeUtc -eq $_.LastWriteTimeUtc) { return }
    Copy-Item $_.FullName $dst -Force
    $ch++
}
Write-Host "  服务端: $ch 个文件已同步"

$GmDir = Join-Path $ScriptRoot "dfogmtool"
if (Test-Path $GmZip) {
    Write-Host "  解压GM工具源码..."
    [System.IO.Compression.ZipFile]::ExtractToDirectory($GmZip, $GmExtract)
    $gmSub = Get-ChildItem $GmExtract -Directory | Select-Object -First 1
    if ($gmSub) {
        $gmSrc = $gmSub.FullName
        if (-not (Test-Path $GmDir)) { New-Item -ItemType Directory -Path $GmDir -Force | Out-Null }
        $gch = 0
        Get-ChildItem $gmSrc -File -Recurse | ForEach-Object {
            $rel = $_.FullName.Substring($gmSrc.Length).TrimStart('\')
            if ($rel -match '(^|\\)(\.git|dist)(\\|$)') { return }
            $dst = Join-Path $GmDir $rel
            $dstDir = Split-Path $dst -Parent
            if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
            Copy-Item $_.FullName $dst -Force
            $gch++
        }
        Write-Host "  GM工具: $gch 个文件已同步"
    }
    else {
        Write-Host "  GM工具ZIP结构异常，跳过"
    }
}

Write-Host ""
Write-Host "[4/5] 编译..."
$buildOk = $false
$gmBuildOk = $false

# 编译前停止正在运行的进程，避免文件锁定
Write-Host "  停止旧进程..."
Get-Process -Name "DfoServer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "DfoGmTool" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$distDb = Join-Path $SrcRoot "dist\win-x64\Data\inventory.db"
$distDbBak = Join-Path $SrcRoot "dist\win-x64\Data\inventory.db.tmpbak"
if (Test-Path $distDb) { New-Item -ItemType Directory -Path (Split-Path $distDbBak -Parent) -Force | Out-Null; Copy-Item $distDb $distDbBak -Force }

$serverProj = Join-Path $SrcRoot "Server\DfoServer\DfoServer.csproj"
$distDir = Join-Path $SrcRoot "dist\win-x64"
if (Test-Path $serverProj) {
    Write-Host "  编译 DfoServer.exe (这需要几分钟)..."
    & $dn restore $serverProj --ignore-failed-sources 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $dn publish $serverProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $distDir 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { & $dn publish $serverProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $distDir 2>&1 | Out-Null }
        $exe = Join-Path $distDir "DfoServer.exe"
        if ((Test-Path $exe) -and (Get-Item $exe).Length -gt 0) { 
            $buildOk = $true
            $sz = [math]::Round((Get-Item $exe).Length / 1MB, 2)
            Write-Host "  DfoServer.exe 编译成功 ($sz MB)" 
        } else { 
            Write-Host "  ERROR: DfoServer.exe 编译失败" 
        }
    } else { 
        Write-Host "  ERROR: restore 失败 (exit $LASTEXITCODE)" 
    }
} else { 
    Write-Host "  WARN: 未找到 $serverProj" 
}

$gmProj = Join-Path $GmDir "DfoGmTool.csproj"
if (Test-Path $gmProj) {
    Write-Host "  编译 DfoGmTool.exe ..."
    $pubDir = Join-Path $GmDir "publish"
    # v1.919: 编译前清理 dfogmtool 目录下的过期 .cs 文件，避免与新版源码中的类型冲突 (CS0101)
    if ($gmSrc) {
        Get-ChildItem $GmDir -Recurse -Filter "*.cs" -ErrorAction SilentlyContinue | ForEach-Object {
            $rel = $_.FullName.Substring($GmDir.Length).TrimStart('\')
            if ($rel -match '(^|\\)(bin|obj|publish)(\\|$)') { return }
            $srcPath = Join-Path $gmSrc $rel
            if (-not (Test-Path $srcPath)) { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
        }
    }
    & $dn restore $gmProj --ignore-failed-sources 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $dn publish $gmProj -c Release -r win-x64 --self-contained true -o $pubDir 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { & $dn publish $gmProj -c Release -r win-x64 --self-contained true -o $pubDir 2>&1 | Out-Null }
        $exe = Join-Path $pubDir "DfoGmTool.exe"
        if ((Test-Path $exe) -and (Get-Item $exe).Length -gt 0) { 
            $gmBuildOk = $true
            $sz = [math]::Round((Get-Item $exe).Length / 1MB, 2)
            Write-Host "  DfoGmTool.exe 编译成功 ($sz MB)" 
        } else { 
            Write-Host "  WARN: DfoGmTool.exe 编译失败" 
        }
    } else { 
        Write-Host "  WARN: GM restore 失败 (exit $LASTEXITCODE)" 
    }
} else { 
    Write-Host "  WARN: 未找到 $gmProj" 
}

if (Test-Path $distDbBak) { Copy-Item $distDbBak $distDb -Force; Remove-Item $distDbBak -Force }

Write-Host ""
Write-Host "[5/5] 清理..."
if (Test-Path $TempDir) { Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue; Write-Host "  已清理临时目录" }
$caches = @(
    (Join-Path $SrcRoot "Server\DfoServer\obj"),
    (Join-Path $SrcRoot "Server\DfoServer\bin"),
    (Join-Path $GmDir "obj"),
    (Join-Path $GmDir "bin")
)
foreach ($d in $caches) { if (Test-Path $d) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue } }
Write-Host "  已清理编译缓存"

Write-Host ""
Write-Host "========================================"
if ($buildOk) { Write-Host "  DfoServer.exe : 编译成功" } else { Write-Host "  DfoServer.exe : 编译失败" }
if ($gmBuildOk) { Write-Host "  DfoGmTool.exe : 编译成功" } else { Write-Host "  DfoGmTool.exe : 编译失败/跳过" }
Write-Host "========================================"
Write-Host ""
Read-Host "按 Enter 键退出"