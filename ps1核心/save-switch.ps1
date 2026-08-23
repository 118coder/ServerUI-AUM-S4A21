# v1.918: 存档管理脚本 — 列出切换库中所有存档文件夹、切换/保存操作（含杂DB），操作时自动停服/重启
$ErrorActionPreference = "Continue"
$ScriptRoot = $PSScriptRoot
if ((Get-Item $ScriptRoot).Name -eq 'ps1核心') { $ScriptRoot = (Get-Item $ScriptRoot).Parent.FullName }
$SwitchDir = Join-Path $ScriptRoot "存档管理\切换库"
$DataDir = Join-Path $ScriptRoot "ServerS4A21-AUM\dist\win-x64\Data"
$DbTarget = Join-Path $DataDir "inventory.db"
$BackupDir = Join-Path $ScriptRoot "存档管理\备份存档"

if (-not (Test-Path $SwitchDir)) { New-Item -ItemType Directory -Path $SwitchDir -Force | Out-Null }
if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }

Write-Host "========================================"
Write-Host "  存档管理 (v1.918)"
Write-Host "========================================"
Write-Host ""
Write-Host "切换库路径: $SwitchDir"
Write-Host "目标位置:   $DataDir"
Write-Host ""

# v1.918: 列出存档文件夹（不再直接读取 .db 文件）
$dirs = @(Get-ChildItem $SwitchDir -Directory -ErrorAction SilentlyContinue)
Write-Host "--- 现有存档 ---"
if ($dirs.Count -eq 0) {
    Write-Host "  (暂无存档)"
} else {
    for ($i = 0; $i -lt $dirs.Count; $i++) {
        $files = @(Get-ChildItem $dirs[$i].FullName -Filter "inventory*" -ErrorAction SilentlyContinue)
        $totalSize = ($files | Measure-Object -Property Length -Sum).Sum
        $sizeKB = [math]::Round($totalSize / 1KB, 1)
        $hasDb = (Test-Path (Join-Path $dirs[$i].FullName "inventory.db"))
        $flag = if ($hasDb) { "" } else { " (无inventory.db)" }
        Write-Host ("  " + ($i+1) + ". " + $dirs[$i].Name + "  (" + $sizeKB + " KB)" + $flag)
    }
}

Write-Host ""
Write-Host "--- 当前游戏存档 ---"
if (Test-Path $DataDir) {
    $invFiles = @(Get-ChildItem $DataDir -Filter "inventory*" -ErrorAction SilentlyContinue)
    $totalSize = ($invFiles | Measure-Object -Property Length -Sum).Sum
    $sizeKB = [math]::Round($totalSize / 1KB, 1)
    $count = $invFiles.Count
    Write-Host "  inventory* 共 $count 个文件 ($sizeKB KB)"
} else {
    Write-Host "  (未找到)"
}

Write-Host ""
Write-Host "========================================"
Write-Host "  操作选择"
Write-Host "========================================"
Write-Host ""
Write-Host "  输入编号 = 切换到对应存档（含杂DB）"
Write-Host "  输入 S   = 保存当前存档（含杂DB）到切换库"
Write-Host "  输入 0   = 取消退出"
Write-Host ""

$sel = (Read-Host "请选择").Trim()

if ($sel -eq "0" -or $sel -eq "") {
    Write-Host "已取消。"
    pause
    exit
}

# v1.916: 检测并停止服务端
function StopServer { & "$ScriptRoot\停止服务.bat" 2>$null }
$wasRunning = (Get-Process -Name "DfoServer" -ErrorAction SilentlyContinue).Count -gt 0
if ($wasRunning) {
    Write-Host "检测到服务端运行中，正在自动停止..."
    StopServer
    Start-Sleep -Seconds 2
}

if ($sel -eq "S" -or $sel -eq "s") {
    if (-not (Test-Path $DataDir)) {
        Write-Host "错误: 当前没有 Data 目录，无法保存。"
        pause
        exit
    }
    $saveName = (Read-Host "请输入存档名称").Trim()
    if ($saveName -eq "") {
        Write-Host "已取消。"
        pause
        exit
    }
    $saveFolder = Join-Path $SwitchDir $saveName
    if (Test-Path $saveFolder) {
        $overwrite = (Read-Host "该名称已存在，是否覆盖? (Y/N)").Trim()
        if ($overwrite -ne "Y" -and $overwrite -ne "y") {
            Write-Host "已取消。"
            pause
            exit
        }
    } else {
        New-Item -ItemType Directory -Path $saveFolder -Force | Out-Null
    }
    # v1.918: 保存所有 inventory* 文件（主DB + 杂DB）
    Get-ChildItem $DataDir -Filter "inventory*" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $saveFolder $_.Name) -Force
    }
    $savedCount = @(Get-ChildItem $saveFolder -Filter "inventory*").Count
    Write-Host "当前存档已保存到: $saveFolder ($savedCount 个文件)"

    if ($wasRunning) {
        Write-Host "正在重启服务端..."
        Start-Process -FilePath (Join-Path $ScriptRoot "ServerS4A21-AUM\start-server.bat") -WindowStyle Minimized
    }
    pause
    exit
}

try { $idx = [int]$sel - 1 } catch {
    Write-Host "无效输入: $sel"
    pause
    exit
}

if ($idx -lt 0 -or $idx -ge $dirs.Count) {
    Write-Host "无效编号: $sel (有效范围: 1-$($dirs.Count))"
    pause
    exit
}

$srcFolder = $dirs[$idx].FullName
Write-Host ""
Write-Host "已选择: $($dirs[$idx].Name)"

# 检查目标存档文件夹是否存在 inventory.db
if (-not (Test-Path (Join-Path $srcFolder "inventory.db"))) {
    Write-Host "错误: 目标存档文件夹内不存在 inventory.db，无法切换。"
    Write-Host "请删除该文件夹或选择其他存档。"
    pause
    exit
}

$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$bakFolder = Join-Path $BackupDir "backup_${ts}"
New-Item -ItemType Directory -Path $bakFolder -Force | Out-Null

# v1.918: 备份所有 inventory* 文件（主DB + 杂DB）
if (Test-Path $DataDir) {
    Write-Host "正在备份当前存档（含杂DB）..."
    Get-ChildItem $DataDir -Filter "inventory*" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $bakFolder $_.Name) -Force
    }
    Write-Host "备份完成: $bakFolder"
}

# v1.918: 恢复所有 inventory* 文件（主DB + 杂DB）
Write-Host "正在切换..."
Get-ChildItem $srcFolder -Filter "inventory*" | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $DataDir $_.Name) -Force
}
Write-Host "切换完成!"

Write-Host ""
Write-Host "========================================"
Write-Host "  已成功切换存档。如果无法登录服务端或"
Write-Host "  网络连接中断，请开启 ServerUI 后勾选"
Write-Host "  [清理冗余DB] 复选框再操作一次。"
Write-Host ""
Write-Host "  存档目录位于:"
Write-Host "  $DataDir"
Write-Host "========================================"

if ($wasRunning) {
    Write-Host ""
    Write-Host "正在重启服务端..."
    Start-Process -FilePath (Join-Path $ScriptRoot "ServerS4A21-AUM\start-server.bat") -WindowStyle Minimized
}
pause