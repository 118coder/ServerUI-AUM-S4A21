# ============================================================
#  DfoGmTool - GM工具 独立启动脚本 (v1.83)
#  写法完全对齐 ServerUI 里 btGm 的启动逻辑:
#    gmp = AUM管理组件\dfogmtool\publish\DfoGmTool.exe
#    sb  = AUM管理组件\ServerS4A12-AUM\dist\win-x64
#    启动前先杀掉旧 DfoGmTool 进程
#    以 sb 作为 --server-bin 参数 + 环境变量 DFO_GM_SERVER_BIN
#    关键: 工作目录 = publish, ASP.NET 才能定位 wwwroot (否则页面 404)
#    3 秒后自动打开 http://localhost:5050
#  本文件为 UTF-8 (带 BOM), 由纯 ASCII 的 GM工具.bat 调用, 规避批处理中文乱码
# ============================================================

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$Base      = $PSScriptRoot
if ((Get-Item $Base).Name -eq 'ps1核心') { $Base = (Get-Item $Base).Parent.FullName }
$GmDir     = Join-Path $Base 'dfogmtool\publish'
$GmExe     = Join-Path $GmDir 'DfoGmTool.exe'
$ServerBin = Join-Path $Base 'ServerS4A12-AUM\dist\win-x64'
$DbPath    = Join-Path $ServerBin 'Data\inventory.db'
$PvfPath   = Join-Path $ServerBin 'Data\Pvf\Script.pvf'

Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  DfoGmTool - GM工具 (独立启动)' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ---------- 检测 1: GM工具目录 / 可执行文件 ----------
Write-Host '[检测 1/2] GM工具目录...' -ForegroundColor Yellow
Write-Host ('  路径: ' + $GmDir) -ForegroundColor Gray
if (-not (Test-Path -LiteralPath $GmDir)) {
    Write-Host '[错误] 未找到 GM工具 publish 目录, GM工具尚未编译。' -ForegroundColor Red
    Write-Host '       请先在 ServerUI 中执行一次增量/全量更新, 或运行 开始更新.bat。' -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}
if (-not (Test-Path -LiteralPath $GmExe)) {
    Write-Host '[错误] 未找到 DfoGmTool.exe, GM工具尚未编译完成。' -ForegroundColor Red
    Write-Host '       请先在 ServerUI 中执行一次增量/全量更新, 或运行 开始更新.bat。' -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}
if (-not (Test-Path -LiteralPath (Join-Path $GmDir 'wwwroot\index.html'))) {
    Write-Host '[警告] publish\wwwroot 缺失, 页面可能返回 404, 建议重新编译 GM工具。' -ForegroundColor Yellow
}
Write-Host '  OK - DfoGmTool.exe 已就绪' -ForegroundColor Green

# ---------- 检测 2: 服务端数据目录 ----------
Write-Host '[检测 2/2] 服务端数据目录...' -ForegroundColor Yellow
Write-Host ('  路径: ' + $ServerBin) -ForegroundColor Gray
if (-not (Test-Path -LiteralPath $ServerBin)) {
    Write-Host '[错误] 未找到服务端 dist 目录, 请先执行一次更新。' -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}
$missing = @()
if (-not (Test-Path -LiteralPath $DbPath))  { $missing += 'Data\inventory.db' }
if (-not (Test-Path -LiteralPath $PvfPath)) { $missing += 'Data\Pvf\Script.pvf' }
if ($missing.Count -gt 0) {
    Write-Host ('[错误] 服务端数据目录不完整, 缺少: ' + ($missing -join ', ')) -ForegroundColor Red
    Write-Host '       请先在 ServerUI 中执行一次增量/全量更新以生成服务端数据。' -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}
Write-Host '  OK - inventory.db / Script.pvf 均存在' -ForegroundColor Green
Write-Host ''

# ---------- 停止旧的 GM工具进程 (覆盖/独占前先杀进程) ----------
Write-Host '[1/2] 停止旧的 GM工具进程...' -ForegroundColor Yellow
Get-Process -Name 'DfoGmTool' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Seconds 1

# ---------- 启动 GM工具 (对齐 ServerUI: 工作目录=publish + --server-bin + 环境变量) ----------
Write-Host '[2/2] 启动 GM工具 (工作目录: dfogmtool\publish)...' -ForegroundColor Yellow
$env:DFO_GM_SERVER_BIN = $ServerBin
try {
    $proc = Start-Process -FilePath $GmExe `
        -ArgumentList ('--server-bin "{0}"' -f $ServerBin) `
        -WorkingDirectory $GmDir -PassThru
    Write-Host ('  已启动 (PID ' + $proc.Id + ')') -ForegroundColor Green
    Write-Host ('  服务端目录: ' + $ServerBin) -ForegroundColor Gray
} catch {
    Write-Host ('[错误] 启动失败: ' + $_.Exception.Message) -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}

Write-Host ''
Write-Host 'GM工具运行中, 浏览器访问: http://localhost:5050' -ForegroundColor Cyan
Write-Host '3秒后自动打开浏览器...' -ForegroundColor DarkGray
Start-Sleep -Seconds 3
try { Start-Process 'http://localhost:5050' } catch {
    Write-Host '浏览器未能自动打开, 请手动访问 http://localhost:5050' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '关闭本窗口不会停止 GM工具 (它已在独立进程运行)。' -ForegroundColor DarkGray
Write-Host '如需停止: 结束 DfoGmTool.exe 进程, 或在 ServerUI 退出时自动清理。' -ForegroundColor DarkGray
Write-Host '========================================' -ForegroundColor Cyan
Read-Host '按回车关闭本窗口'
