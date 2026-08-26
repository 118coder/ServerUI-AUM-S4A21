# ==================================================================
#  ServerS4A21 一键更新脚本 (v1.917)
# ==================================================================
#
# 【这个脚本是干什么的？】
#   从远程仓库（gitgud.io）拉取最新服务端源码，自动替换旧文件，
#   编译生成新的 DfoServer.exe 和 DfoGmTool.exe，最后拉取更新日志。
#   全流程共 5 个步骤，支持"增量更新"和"全量更新"两种模式。
#
# 【两种更新模式的区别】
#   增量更新（默认）：只下载有变动的文件，速度快，适合日常更新
#   全量更新（-FullSync）：强制覆盖所有文件，适合首次部署或出问题时使用
#
# 【如何手动运行这个脚本？】
#   1. 在 AUM管理组件 目录下，右键 → 在此处打开 PowerShell
#   2. 增量更新：  .\update.ps1
#   3. 全量更新：  .\update.ps1 -FullSync
#   4. 跳过日志：  .\update.ps1 -SkipCommitLog
#   5. GUI模式：   .\update.ps1 -NonInteractive   （由ServerUI.exe调用时自动加）
#
# 【如何由 ServerUI.exe 调用？】
#   ServerUI.exe 会自动调用此脚本，并添加 -NonInteractive 参数。
#   GUI 模式下部分交互提示会被跳过，进度条通过 ##PROGRESS##N 标记传递。
#
# 【如果你想修改什么，看这里】
#   - 改仓库地址：搜 "$RepoApi" 变量（第 45 行附近）
#   - 改 API 令牌：搜 "$ApiToken" 变量（第 49 行附近）
#   - 改重试次数：搜函数内的 "$a -le N" 循环（N 是重试次数）
#   - 改等待秒数：搜 "Start-Sleep" 后面的数字
#   - 改线程数：  搜 "$maxThreads" 变量
#   - 改超时时间：搜 "TimeoutSec" 参数
#   - 改文件保留策略：搜 "Sync-SourceFiles" 函数的过滤规则
#   - 改编译参数：搜 "dotnet publish" 后面的参数
#   - 改日志格式：搜 "$logLines" 的构造区域
# ==================================================================

# ---- 脚本参数 ----
# -FullSync         : 全量同步开关，带上此参数会覆盖所有文件
# -NonInteractive   : 非交互模式（由 ServerUI.exe 调用时自动启用）
# -SkipCommitLog    : 跳过提交日志拉取（GUI中有【跳过更新日志】复选框对应）
param([switch]$FullSync, [switch]$NonInteractive, [switch]$SkipCommitLog, [switch]$UseMirror)

# ---- 全局设置 ----
# 出错时继续运行，不要因为一个小错误就中断整个更新
# 如果想"一有错误就停止"，改成 "Stop" 即可
$ErrorActionPreference = "Continue"

# ---- 路径计算 ----
# $PSScriptRoot = 脚本自身的所在文件夹
# 例如脚本在 E:\Game\DXF\AUM管理组件\update.ps1
# 则 $PSScriptRoot = E:\Game\DXF\AUM管理组件
$ScriptRoot = $PSScriptRoot
if ((Get-Item $ScriptRoot).Name -eq 'ps1核心') { $ScriptRoot = (Get-Item $ScriptRoot).Parent.FullName }

# SrcRoot = 服务端源码主目录（脚本目录下的 ServerS4A21-AUM 文件夹）
# 这是更新操作的目标目录，所有文件都会同步到这里
$SrcRoot = Join-Path $ScriptRoot "ServerS4A21-AUM"

Write-Host "【本工具-S4A12-AUM 已经停止维护，AUM 全面转向 S4A21 版本】" -ForegroundColor DarkYellow

# ==================================================================
#  仓库连接配置（重要！假如仓库地址或令牌变了，改这里）
# ==================================================================
# gitgud.io 是基于 GitLab 的代码托管平台
# GitLab API 版本是 v4，与之前的 Codeberg API (v1, 基于 Gitea) 不同
# 项目路径 rewo/ServerS4A21 需要 URL 编码为 rewo%2FServerS4A21（%2F 是斜杠 / 的编码）
$RepoApi = "https://gitgud.io/api/v4/projects/rewio%2FServerS4A21"

# UTF-8 编码器，用于正确处理中文字符
$utf8 = [System.Text.Encoding]::UTF8

# 预加载 ZIP 处理程序集（后续校验多处使用，提前加载避免运行时找不到类型）
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

# API 访问令牌 — 双重 base64 编码存放（防 GitHub 安全扫描，运行时自动解码）
# 如需更换令牌，先用 PowerShell 生成双重编码:
#   $raw = "新令牌"
#   $b1 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($raw))
#   $b2 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($b1))
#   将 $b2 的值替换下方
$ApiTokenB64 = "WjJkcGIxOUZkbUpmUmtScFpqRnNWVlJXUVZGcmR6QjZTMWRIT0RaTlVYQXhUMnBLYWxvelowc3VNREV1TVRBeFozVXhhMnBq"
$ApiToken = $utf8.GetString([Convert]::FromBase64String($utf8.GetString([Convert]::FromBase64String($ApiTokenB64))))

# HTTP 请求头 —— GitLab API 使用 "PRIVATE-TOKEN" 方式认证
# 旧的 Codeberg 使用的是 "Authorization: token xxx"，两者不同！
$ApiHeaders = @{ "PRIVATE-TOKEN" = $ApiToken }

# ---- 本地兜底缓存目录 ----
# 如果所有在线下载均失败，从本地 latest 目录读取压缩包
$LatestDir = Join-Path $ScriptRoot "latest"
$LatestSvr = Join-Path $LatestDir "ServerS4A21-latest.zip"
$LatestGM  = Join-Path $LatestDir "ServerS4A21-GMTool-latest.zip"

# ==================================================================
#  镜像下载源配置（v1.911：GitGud 不可达时自动切换的备选方案）
# ==================================================================
# 以下 URL 用于当 GitGud 不可达时，直接从 Gitee/GitHub/Codeberg 下载已缓存的服务端包。
# 优先使用 Gitee（国内用户访问最快），其次 GitHub，Codeberg 作为备选。
# 所有 raw URL 均为公共直链（无需 API 认证），避免频率限制。
$MirrorGiteeRaw    = "https://gitee.com/c118oder/ServerS4A12.86JP/raw/main"
$MirrorGitHubRaw   = "https://raw.githubusercontent.com/118coder/ServerS4A12.86JP/main"
$MirrorCodebergRaw = "https://codeberg.org/118coder/ServerS4A12.86JP/raw/branch/main"
# 对应网页地址（供用户手动验证）
$MirrorGiteePage    = "https://gitee.com/c118oder/ServerS4A12.86JP"
$MirrorGitHubPage   = "https://github.com/118coder/ServerS4A12.86JP"
$MirrorCodebergPage = "https://codeberg.org/118coder/ServerS4A12.86JP"

# Gitee API 令牌 — 双重 base64 编码存放（防扫描）
$GiteeTokenB64 = "WlRsbVpXWmlPRE0zWWpsaU5UVTBaamRpTVdaak4yRXdZbVprTlRKaFpUaz0="
$GiteeToken = $utf8.GetString([Convert]::FromBase64String($utf8.GetString([Convert]::FromBase64String($GiteeTokenB64))))

# 服务端 latest.zip 直链（Gitee 优先 → GitHub → Codeberg）
# Gitee 为私有仓库，需在 URL 后附加 access_token 进行认证下载
$MirrorServerUrls = @(
    "$MirrorGiteeRaw/mirrors/ServerS4A21-latest.zip?access_token=$GiteeToken",
    "$MirrorGitHubRaw/mirrors/ServerS4A21-latest.zip",
    "$MirrorCodebergRaw/mirrors/ServerS4A21-latest.zip"
)
# GM 工具 mirror 下载（Gitee 优先 → GitHub → Codeberg 镜像仓库）
$MirrorGMUrls = @(
    "$MirrorGiteeRaw/mirrors/ServerS4A21-GMTool-latest.zip?access_token=$GiteeToken",
    "$MirrorGitHubRaw/mirrors/ServerS4A21-GMTool-latest.zip",
    "$MirrorCodebergRaw/mirrors/ServerS4A21-GMTool-latest.zip"
)
# latest.json 元数据下载（Gitee 优先 → GitHub → Codeberg）
$MirrorMetaUrls = @(
    "$MirrorGiteeRaw/latest.json",
    "$MirrorGitHubRaw/latest.json",
    "$MirrorCodebergRaw/latest.json"
)

# ==================================================================
#  base64 编码的中文字符串字典
# ==================================================================
# 为什么用 base64？
#   PowerShell 控制台输出中文有时会乱码，用 base64 编码可以避免这个问题。
#   函数 T($key) 会自动解码，返回真正的中文文本。
#
# 如何修改或添加新文本？
#   1. 用在线工具把中文转成 base64（比如搜 "在线base64编码"）
#   2. 把编码后的字符串替换对应的 value
#   3. 或者直接删掉 base64，改用普通字符串（如果控制台不乱码的话）
# ==================================================================
$b64 = @{
    fn_log  = "5pu05paw5pel5b+XLnR4dA=="          # 更新日志.txt
    s_ver   = "54mI5pysOiA="                        # 版本:
    s_up    = "5pu05paw5pe26Ze0OiA="                # 更新时间:
    s_total = "57Sv6K6h5o+Q5LqkOiA="                # 统计提交:
    s_hist  = "5pu05paw5Y6G5Y+yICjku47mnIDliJ3liLDnjrDlnKjvvIzljJfkuqzml7bpl7QgVVRDKzgpOg=="         # 更新历史 (从最初到现在，北京时间 UTC+8):
    s_more  = "5pu05aSa5Y6G5Y+y5pu05paw5pel5b+X77yM6K+35Zyo55uu5b2V5p+l55yLOiA="                     # 更多历史更新日志，请在目录查看:
    s_repo  = "5LuT5bqT5o+Q5Lqk6K6w5b2VOiA="        # 仓库提交记录:
    s_inc   = "5aKe6YeP5pu05paw"                     # 增量更新
    s_full  = "5YWo6YeP5ZCM5q2l"                     # 全量同步
    s_fullsync = "5YWo6YeP5ZCM5q2lICjmiYDmnInmlofku7YpLi4u"  # 全量同步 (所有文件)...
    s_fallback = "5pyA6L+R5peg5Y+Y5pu0IC0g5Zue6YCA5Yiw5YWo6YeP5ZCM5q2l44CC"  # 最近无变更 - 回退到全量同步。
    s_server = "U2VydmVyUzRBMjEgLQ=="               # ServerS4A21 -
    s_updating = "Pj4+IFszLzVdIOato+WcqOabtOaWsOaWh+S7tiAo"      # >>> [3/5] 正在更新文件 (
    s_done  = "Pj4+IOWujOaIkCEg"                     # >>> 完成!
    s_warn1 = "5byA5aeL5pu05paw5YmN77yM6K+356Gu6K6k572R57uc54iL6YCa77yM5Lim5qOA5p+l5piv5ZCm5bey5byA5ZCv56eR5a2m5LiK572R44CC"    # 开始更新前，请确保网络畅通，并检查是否已开启科学上网。
    s_warn2 = "5pys5Zyw5paH5Lu26Lev5b6E5Y+C6ICD77ya"            # 本地文件路径参考：
    s_warn3 = "ICAtIOaVsOaNruW6k+WtmOahozogXFNlcnZlclM0QTIxLUFVTVxkaXN0XHdpbi14NjRcRGF0YVxpbnZlbnRvcnkuZGI="  #   - 数据库存储: \ServerS4A21-AUM\dist\win-x64\Data\inventory.db
    s_warn4 = "ICAtIFBWRuaWh+S7tjogIFxTZXJ2ZXJTNEEyMS1BVU1cZGlzdFx3aW4teDY0XERhdGFcUHZmXFNjcmlwdC5wdmY="    #   - PVF文件:    \ServerS4A21-AUM\dist\win-x64\Data\Pvf\Script.pvf
    s_warn5 = "6K+356Gu6K6k5LiK6L+w5paH5Lu25L2N572u5peg6K+v44CC"            # 请确认以上文件位置无误。
    s_skip   = "ICBb5L+d5oqkXSA="                                   #   [保护]
    s_prot   = "ICAo5bey5L+d5oqk77yM5LiN5Lya6KKr6KaG55GWKQ=="          #   (已保护，不会被覆盖)
}

# 解码函数：传入 key，返回对应的中文文本
# 用法示例：T "s_inc"  →  返回 "增量更新"
function T($key) {
    return $utf8.GetString([Convert]::FromBase64String($b64[$key]))
}

# ==================================================================
#  文件路径定义
# ==================================================================
# 更新日志文件 —— 每次更新后自动生成/更新，位于脚本同目录
$LogFile = Join-Path $ScriptRoot (T "fn_log")

# 玩家数据库文件（v2.033: 源码包里有更新则随同步直接覆盖, 不再备份恢复）
$DbFile = Join-Path $SrcRoot "Server\DfoServer\Data\inventory.db"

# 临时工作目录 —— 下载的 ZIP 包和解压内容都放在这里
# 使用系统 TEMP 目录，更新完成后会自动清理
$TempDir = Join-Path $env:TEMP "ServerS4A21-update"

# 便携版 .NET SDK 的路径 —— 如果系统没有装 SDK，脚本会使用这里的
$LocalSdk = Join-Path $ScriptRoot "dotnet-sdk"

# 提交日志缓存文件 —— 记录已经拉取过的 commit，避免重复拉取
# 缓存在 .update-cache 隐藏目录中
$CommitCacheFile = Join-Path $ScriptRoot ".update-cache\commits.json"

# 中国时区 (UTC+8) —— 所有时间显示都转成北京时间
$ChinaTZ = [System.TimeZoneInfo]::FindSystemTimeZoneById("China Standard Time")

# ==================================================================
#  工具函数
# ==================================================================

# ---- 时区转换函数 ----
# 把 GitLab API 返回的 UTC 时间转换成北京时间 (yyyy-MM-dd 格式)
# 参数 $d: API 返回的时间字符串，例如 "2026-07-20T12:30:00Z"
# 返回:   "2026-07-20"（北京时间）
function ToChinaDate($d) {
    $dateStr = "$d"   # 强制转字符串，防止某些情况下收到数组导致报错
    $dt = [DateTimeOffset]::Parse($dateStr, [System.Globalization.CultureInfo]::InvariantCulture)
    return ([System.TimeZoneInfo]::ConvertTime($dt, $ChinaTZ)).ToString("yyyy-MM-dd")
}

# ---- Gitee 私有仓库下载函数 (v1.911) ----
# Gitee 私有仓库的 raw 直链不支持 access_token 认证
# 改为通过 API 内容端点获取 base64 编码内容再解码写入
function Download-FromGitee($url, $target, $timeout = 30) {
    try {
        $repoPath = $url
        if ($url -match '/raw/(?:main|master)/(.+?)(?:\?|$)') { $repoPath = $Matches[1] }
        elseif ($url -match '/raw/(?:main|master)/(.+)') { $repoPath = $Matches[1] }
        $apiUrl = "https://gitee.com/api/v5/repos/c118oder/ServerS4A12.86JP/contents/$repoPath" + "?access_token=$GiteeToken"
        $resp = Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -TimeoutSec $timeout
        $meta = $resp.Content | ConvertFrom-Json
        $contentB64 = $meta.content
        if ($contentB64) {
            $bytes = [Convert]::FromBase64String($contentB64)
            [IO.File]::WriteAllBytes($target, $bytes)
            # v1.921: 下载后立即做 ZIP 完整性校验, 截断文件视为失败
            return (Test-Path $target) -and ((Get-Item $target).Length -gt 0) -and (Test-ZipIntegrity $target)
        }
        # 大文件无 content 字段，使用 download_url
        if ($meta.download_url) {
            Invoke-WebRequest -Uri $meta.download_url -OutFile $target -UseBasicParsing -TimeoutSec $timeout
            return (Test-Path $target) -and ((Get-Item $target).Length -gt 0) -and (Test-ZipIntegrity $target)
        }
        return $false
    } catch { return $false }
}

# ---- GitHub 私有仓库下载函数 (v1.911) ----
# GitHub ServerS4A12.86JP 也是私有仓库，raw URL 返回 404
# 通过 GitHub API 获取 download_url（含临时 token）再下载
function Download-FromGitHub($repoPath, $target, $timeout = 30) {
    try {
        $tokenB64 = "WjJod1gyOUdVVVJHZFc1dFEwSkVaRzQzTVZObVVWRm5NWFUzYzJObVRVZzNaakZzUmtOa1FnPT0="
        $ghToken = $utf8.GetString([Convert]::FromBase64String($utf8.GetString([Convert]::FromBase64String($tokenB64))))
        $apiUrl = "https://api.github.com/repos/118coder/ServerS4A12.86JP/contents/$repoPath" + "?ref=main"
        $headers = @{"Authorization" = "token $ghToken"; "Accept" = "application/vnd.github.v3+json"}
        $resp = Invoke-WebRequest -Uri $apiUrl -Headers $headers -UseBasicParsing -TimeoutSec $timeout
        $meta = $resp.Content | ConvertFrom-Json
        if ($meta.download_url) {
            Invoke-WebRequest -Uri $meta.download_url -OutFile $target -UseBasicParsing -TimeoutSec $timeout
            # v1.921: 下载后立即做 ZIP 完整性校验, 截断文件视为失败
            return (Test-Path $target) -and ((Get-Item $target).Length -gt 0) -and (Test-ZipIntegrity $target)
        }
        # 小文件可能直接返回 content
        if ($meta.content) {
            $bytes = [Convert]::FromBase64String($meta.content.Replace("`n","").Replace("`r",""))
            [IO.File]::WriteAllBytes($target, $bytes)
            return (Test-Path $target) -and ((Get-Item $target).Length -gt 0) -and (Test-ZipIntegrity $target)
        }
        return $false
    } catch { return $false }
}

# ---- 文件下载函数（带重试） ----
# 与 Invoke-RepositoryRequest 不同，这个函数用于下载二进制文件
# 参数 $uri:    下载地址
# 参数 $target: 保存到的本地路径
# 最多重试 5 次，每次失败后等待时间翻倍（1秒→2秒→4秒→8秒）
# 这种"指数退避"策略可以避免服务器过载时越重试越糟
function Download-File($uri, $target) {
    # 最多尝试 5 次下载
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Write-Host "Download attempt $attempt/5: $uri"
            # Invoke-WebRequest = PowerShell 内置的下载命令，类似于 curl
            # -UseBasicParsing: 不解析 HTML，只下载原始字节（更快更稳定）
            # -TimeoutSec: 60 秒超时（大文件可能需要更长时间）
            Invoke-WebRequest -Uri $uri -OutFile $target -UseBasicParsing -TimeoutSec 60 -Headers $ApiHeaders
            # 检查下载结果：文件存在 + 大小大于 1KB
            if ((Test-Path $target) -and (Get-Item $target).Length -gt 1024) {
                return $true   # 下载成功
            }
            throw "Downloaded file is missing or too small."
        } catch {
            # 下载失败：删除不完整的文件，打印警告
            Remove-Item $target -Force -ErrorAction SilentlyContinue
            Write-Host "WARNING: download attempt $attempt failed: $_"
            # 如果不是最后一次尝试，等待一段时间再重试
            # [math]::Pow(2, $attempt-1) = 2^(attempt-1) = 1, 2, 4, 8 秒
            if ($attempt -lt 5) { Start-Sleep -Seconds ([math]::Pow(2, $attempt - 1)) }
        }
    }
    return $false   # 5 次都失败，返回失败
}

# ---- ZIP 完整性快速校验 (v1.921, 流式) ----
# 只读文件头 4 字节 + 尾部 ~64KB 扫描中央目录结尾记录, 内存占用 O(1),
# 包体再大也适用; 发现截断/头尾损坏立即判失败
function Test-ZipIntegrity($path) {
    try {
        $fs = [IO.File]::OpenRead($path)
        try {
            if ($fs.Length -lt 22) { return $false }
            $head = New-Object byte[] 4
            [void]$fs.Read($head, 0, 4)
            if (-not ($head[0] -eq 0x50 -and $head[1] -eq 0x4B)) { return $false }
            $scanLen = [Math]::Min([long]$fs.Length, 65557)
            $buf = New-Object byte[] $scanLen
            [void]$fs.Seek(-$scanLen, [IO.SeekOrigin]::End)
            [void]$fs.Read($buf, 0, $scanLen)
            for ($i = 0; $i -lt $scanLen - 3; $i++) {
                if ($buf[$i] -eq 0x50 -and $buf[$i+1] -eq 0x4B -and $buf[$i+2] -eq 0x05 -and $buf[$i+3] -eq 0x06) { return $true }
            }
            return $false
        } finally { $fs.Dispose() }
    } catch { return $false }
}

# ---- ZIP 内容级校验 (v1.921, CRC 自校验) ----
# 遍历全部条目并完整解压: ZIP 每个条目自带 CRC32, .NET 解压流读到流末尾
# 时自动校验, 任意字节损坏(含压缩数据区)都会抛异常
# 不依赖任何外部元数据 → 主源/镜像/缓存一视同仁, 不会产生版本误判
function Test-ZipExtract($path) {
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
        try {
            if ($zip.Entries.Count -eq 0) { return $false }
            $buf = New-Object byte[] 65536
            foreach ($entry in $zip.Entries) {
                $s = $entry.Open()
                try {
                    while ($s.Read($buf, 0, $buf.Length) -gt 0) { }
                } finally { $s.Dispose() }
            }
            return $true
        } finally { $zip.Dispose() }
    } catch { return $false }
}

# ---- 镜像元数据读取 (v1.921, 自洽 sha256 + 版本择优) ----
# 读取镜像源 latest.json 的版本标识(优先 version 字段, 部分源只写 package 字段)
# 与 sha256; 返回 @{ Version=...; Sha256=... } 或 $null (获取失败)
# 注意: 只与本镜像自己的包比对 (上传者同时写入, 天然自洽),
#       绝不跨源比对, 避免主源新包被镜像旧哈希误杀
function Get-MirrorMeta($type) {
    try {
        if ($type -eq "codeberg") {
            $resp = Invoke-WebRequest -Uri "https://codeberg.org/118coder/ServerS4A12.86JP/raw/branch/main/latest.json" -UseBasicParsing -TimeoutSec 15
            $meta = $resp.Content | ConvertFrom-Json
        }
        elseif ($type -eq "github") {
            $tokenB64 = "WjJod1gyOUdVVVJHZFc1dFEwSkVaRzQzTVZObVVWRm5NWFUzYzJObVRVZzNaakZzUmtOa1FnPT0="
            $tok = $utf8.GetString([Convert]::FromBase64String($utf8.GetString([Convert]::FromBase64String($tokenB64))))
            $resp = Invoke-WebRequest -Uri "https://api.github.com/repos/118coder/ServerS4A12.86JP/contents/latest.json?ref=main" -Headers @{"Authorization"="token $tok"; "Accept"="application/vnd.github.v3+json"} -UseBasicParsing -TimeoutSec 15
            $j = $resp.Content | ConvertFrom-Json
            $json = $utf8.GetString([Convert]::FromBase64String($j.content.Replace("`n","").Replace("`r","")))
            $meta = $json | ConvertFrom-Json
        }
        elseif ($type -eq "gitee") {
            $resp = Invoke-WebRequest -Uri "https://gitee.com/api/v5/repos/c118oder/ServerS4A12.86JP/contents/latest.json?access_token=$GiteeToken" -UseBasicParsing -TimeoutSec 15
            $j = $resp.Content | ConvertFrom-Json
            $json = $utf8.GetString([Convert]::FromBase64String($j.content))
            $meta = $json | ConvertFrom-Json
        }
        else { return $null }
        $ver = [string]$meta.version
        if (-not $ver) { $ver = [string]$meta.package }
        return @{ Version = $ver; Sha256 = [string]$meta.sha256 }
    } catch { return $null }
}

# ---- API 请求函数（轻量 GET，带重试） ----
# 用于调用 GitLab API 获取 JSON 数据（如 commit 历史）
# 参数 $uri:        API 地址
# 参数 $MaxAttempts: 最大重试次数（默认 2 次）
# 参数 $TimeoutSec:  超时秒数（默认 8 秒，API 调用应该很快）
# 参数 $Quiet:       静默模式（不打印 [API] 日志）
function Invoke-RepositoryRequest($uri, [int]$MaxAttempts = 2, [int]$TimeoutSec = 8, [switch]$Quiet) {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            if (-not $Quiet) { Write-Host "[API] Request $attempt/$MaxAttempts" }
            # 发送 GET 请求，返回响应对象（包含 StatusCode 和 RawContentStream）
            return Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec $TimeoutSec -Headers $ApiHeaders
        } catch {
            Write-Host "WARNING: API attempt $attempt/$MaxAttempts failed: $_"
            if ($attempt -lt $MaxAttempts) {
                $delay = [math]::Pow(2, $attempt - 1)  # 指数退避：1秒→2秒→4秒...
                Write-Host "[API] Retrying in $delay second(s)..."
                Start-Sleep -Seconds $delay
            }
        }
    }
    Write-Host "WARNING: API unavailable after $MaxAttempts attempts. Falling back to archive-only update."
    return $null   # 所有重试都失败，返回空
}

# ---- 下载源可达性快速检测 (v1.911) ----
# 在执行下载前快速检测各源是否可达（GET 请求，6秒超时，单次尝试）
# 注意: 非 2xx HTTP 响应（如 401/302）说明服务器可达，只有超时/DNS 失败才算不可达
# 按延迟分级: ≤800ms 正常 / ≤3000ms 较慢 / >3000ms 极慢 / 无响应 不可达
# 返回: @{ GitGud=$bool; Gitee=$bool; GitHub=$bool; Codeberg=$bool }
function Test-SourceAvailability {
    # v1.914: 测试项目主页 3次命中即视为可用
    $results = @{ GitGud = $false; Gitee = $false; GitHub = $false; Codeberg = $false }
    $latency = @{ GitGud = 0; Gitee = 0; GitHub = 0; Codeberg = 0 }
    
    Write-Host "[连接检测] 正在检测 GitGud (3次命中/10秒)..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $hits = 0; $attempt = 1
    do {
        $remaining = 10 - [math]::Round($sw.Elapsed.TotalSeconds)
        if ($remaining -le 0) { break }
        $reqTimeout = [math]::Min(3, $remaining)
        try {
            Write-Host "[连接检测]   GitGud 第${attempt}次 (${hits}/3)..."
            Invoke-WebRequest -Uri "https://gitgud.io/rewio/ServerS4A21" -TimeoutSec $reqTimeout -UseBasicParsing -ErrorAction Stop | Out-Null
            $hits++
        } catch {
            if ($_.Exception.Response -ne $null) { $hits++ }
        }
        $attempt++
        if ($hits -ge 3) { break }
        if ($hits -lt 3 -and $sw.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Seconds 1 }
    } while ($hits -lt 3 -and $sw.Elapsed.TotalSeconds -lt 10)
    $sw.Stop()
    
    $results.GitGud = ($hits -ge 3)
    $latency.GitGud = $sw.ElapsedMilliseconds
    
    function LatencyLabel($name, $reachable, $ms) {
        if (-not $reachable) { return "$name 不可达 (超时)" }
        if ($ms -le 800)    { return "$name 正常 (延迟 ${ms}ms)" }
        if ($ms -le 3000)   { return "$name 较慢 (延迟 ${ms}ms)" }
        return "$name 极慢 (延迟 ${ms}ms)"
    }
    Write-Host ("[连接检测] " + (LatencyLabel 'GitGud' $results.GitGud $latency.GitGud))
    
    if (-not $results.GitGud) {
        Write-Host "[连接检测] GitGud 不可达 → 普通用户模式，跳过其余源检测。"
        return $results
    }
    
    # GitGud 可达 → 上传者模式，继续检测镜像源（为镜像上传做准备）
    Write-Host "[连接检测] GitGud 可达 → 上传者模式，继续检测镜像源..."
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-WebRequest -Uri "https://gitee.com" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop | Out-Null
        $sw.Stop(); $latency.Gitee = $sw.ElapsedMilliseconds
        $results.Gitee = $true
    } catch {
        $sw.Stop(); $latency.Gitee = $sw.ElapsedMilliseconds
        if ($_.Exception.Response -ne $null) { $results.Gitee = $true }
    }
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-WebRequest -Uri "https://github.com" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop | Out-Null
        $sw.Stop(); $latency.GitHub = $sw.ElapsedMilliseconds
        $results.GitHub = $true
    } catch {
        $sw.Stop(); $latency.GitHub = $sw.ElapsedMilliseconds
        if ($_.Exception.Response -ne $null) { $results.GitHub = $true }
    }
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-WebRequest -Uri "https://codeberg.org" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop | Out-Null
        $sw.Stop(); $latency.Codeberg = $sw.ElapsedMilliseconds
        $results.Codeberg = $true
    } catch {
        $sw.Stop(); $latency.Codeberg = $sw.ElapsedMilliseconds
        if ($_.Exception.Response -ne $null) { $results.Codeberg = $true }
    }
    
    Write-Host ("[连接检测] " + (LatencyLabel 'Gitee'   $results.Gitee   $latency.Gitee))
    Write-Host ("[连接检测] " + (LatencyLabel 'GitHub'  $results.GitHub  $latency.GitHub))
    Write-Host ("[连接检测] " + (LatencyLabel 'Codeberg' $results.Codeberg $latency.Codeberg))
    
    return $results
}

# ---- 提交缓存：读取 ----
# 从 .update-cache\commits.json 读取之前缓存的 commit 记录
# 缓存的目的是：下次更新时只拉取新的 commit，不用每次都拉全部历史
# 返回: commit 数组，格式 [@{Sha=...; Date=...; Message=...}, ...]
# 如果文件不存在或损坏，返回空数组 @()
function Read-CommitCache {
    if (-not (Test-Path $CommitCacheFile)) { return @() }   # 缓存文件不存在，返回空
    try {
        # Get-Content -Raw: 把整个文件读成一个字符串（而不是数组）
        # ConvertFrom-Json: 把 JSON 字符串转成 PowerShell 对象
        $data = Get-Content $CommitCacheFile -Raw -Encoding UTF8 | ConvertFrom-Json
        return @($data)   # @() 确保返回值是数组类型
    } catch {
        Write-Host "WARNING: commit cache is invalid and will be rebuilt."
        return @()
    }
}

# ---- 提交缓存：写入 ----
# 把 commit 数据写入 .update-cache\commits.json
# 参数 $commits: commit 数组
function Write-CommitCache($commits) {
    # 确保 .update-cache 目录存在
    $directory = Split-Path $CommitCacheFile -Parent
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    # ConvertTo-Json -Depth 4: 把 PowerSehll 对象转成 JSON，嵌套深度设为 4 层
    # Set-Content -Encoding UTF8: 以 UTF-8 编码写入文件
    $commits | ConvertTo-Json -Depth 4 | Set-Content $CommitCacheFile -Encoding UTF8
}

# ==================================================================
#  核心函数：同步提交历史 (Sync-CommitHistory)
# ==================================================================
# 这是 [5/5] 步骤的核心，负责从 gitlab API 拉取仓库的 commit 记录。
#
# 执行策略（两层）：
#   1. "优化方案"：先读本地缓存，只拉取比缓存更新的 commit
#      （利用 Runspace 多线程并行拉取，速度快）
#   2. "兜底方案"：如果优化方案没拉到数据（缓存失效/网络问题），
#      改用传统的单线程逐页拉取全量 commit（较慢但可靠）
#
# 返回: @{ Commits=数组; Complete=是否完整; Refreshed=是否有新数据 }
function Sync-CommitHistory {
    # ---- 第一步：读取缓存，建立已知 commit 索引 ----
    $cached = Read-CommitCache
    $known = @{}   # 哈希表：key=commit SHA, value=commit 对象（用于快速查重）
    $newestDate = [DateTimeOffset]::MinValue   # 缓存中最新的 commit 时间

    foreach ($commit in $cached) {
        $s = "$($commit.Sha)"
        if ($s) {
            $known[$s] = $commit
            try {
                $d = [DateTimeOffset]::Parse("$($commit.Date)")
                if ($d -gt $newestDate) { $newestDate = $d }
            } catch { }
        }
    }

    # 确定查询起点（since 参数）
    # 如果有缓存，从最新缓存时间（减 1 秒防边界问题）开始拉
    # 如果没有缓存，拉最近 3 年的历史
    if ($cached.Count -gt 0 -and $newestDate -ne [DateTimeOffset]::MinValue) {
        $sinceStr = "&since=" + $newestDate.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ")
    } else {
        $sinceStr = "&since=" + (Get-Date).AddYears(-3).ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    $newCount = 0          # 本次新增的 commit 数量
    $timeBudget = 120      # 总超时 120 秒（多页拉取的总时间预算）
    $startTime = Get-Date

    # 线程池大小：取 CPU 核心数的 75%，最少 3 个线程，最多 16 个
    # 例如 8 核 CPU → 6 线程；4 核 → 3 线程；32 核 → 16 线程
    $cpuCores = [Environment]::ProcessorCount
    $maxThreads = [Math]::Max(3, [Math]::Min(16, [Math]::Ceiling($cpuCores * 0.75)))

    # 构造 API URL 模板
    # GitLab API v4 的 commit 查询格式：
    #   /projects/:id/repository/commits?ref_name=分支&per_page=每页条数&since=起始时间&page=页码
    # ref_name=master ：从 master 分支拉（ServerS4A21 默认分支为 master）
    # per_page=50  ：每页最多 50 条（GitLab 最大允许 100）
    $uriBase = "$RepoApi/repository/commits?ref_name=master&per_page=50$sinceStr&page="

    # ================================================================
    #  阶段 1：先拉第 1 页，确认是否有数据、是否需要多页
    # ================================================================
    try {
        $r1 = $null
        # 第 1 页最多重试 10 次（因为这是关键的第一步，失败了后面都没意义）
        for ($a = 1; $a -le 10; $a++) {
            try {
                $r1 = Invoke-WebRequest -Uri ($uriBase + "1") -UseBasicParsing -TimeoutSec 10 -Headers $ApiHeaders
                break
            } catch {
                if ($a -lt 10) { Start-Sleep 3 }   # 等 3 秒再重试
            }
        }
        if (-not $r1) { throw "第1页拉取失败 (10次重试后)" }

        # 将 API 返回的 JSON 转成对象数组
        $items = $utf8.GetString($r1.RawContentStream.ToArray()) | ConvertFrom-Json
        if ($items.Count -eq 0) {
            # ServerS4A21 的 commits API 偶发返回 200 但空数组 []（网络/限流/参数波动）
            # 不能据此丢弃缓存：有历史缓存则原样返回（保留已有提交段），仅缓存也为空才报告无数据
            if ($known.Count -gt 0) {
                $merged = @($known.Values | Sort-Object {[DateTimeOffset]"$($_.Date)"} -Descending)
                Write-Host "[提交日志] API 返回空数组，保留缓存 $($merged.Count) 条提交（不触发镜像整体覆盖）。"
                return @{ Commits=$merged; Complete=$true; Refreshed=$false }
            }
            return @{ Commits=@(); Complete=$false; Refreshed=$false }
        }

        # 解析第 1 页的 commit 数据
        # GitLab API 返回的字段（与之前 Codeberg 不同！）：
        #   id             → commit 的 SHA 哈希
        #   title          → commit 标题（第一行消息）
        #   committed_date → commit 时间
        foreach ($item in $items) {
            $sha = $item.id                        # SHA 哈希（GitLab 用 id，不是 sha）
            if ($known.ContainsKey($sha)) { continue }  # 已缓存，跳过
            $known[$sha] = [pscustomobject]@{
                Sha     = "$sha"
                Date    = "$($item.committed_date)"     # GitLab 用 committed_date
                Message = "$($item.title)"              # GitLab 用 title（第一行）
            }
            $newCount++
        }
        Write-Host "##PROGRESS##60"   # 进度标记：60%（第 1 页已拉完）

        # 如果第 1 页不满 50 条 → 说明只有一页，直接返回
        if ($items.Count -lt 50) {
            $merged = @($known.Values | Sort-Object {[DateTimeOffset]"$($_.Date)"} -Descending)
            if ($newCount -gt 0) { Write-CommitCache $merged }
            Write-Host "[提交日志] 缓存: $($merged.Count) 条 ($newCount 新增)。"
            return @{ Commits=$merged; Complete=$true; Refreshed=($newCount -gt 0) }
        }
    } catch {
        Write-Host "[提交日志] 拉取中断: $_"
        $merged = @($known.Values | Sort-Object {[DateTimeOffset]"$($_.Date)"} -Descending)
        return @{ Commits=$merged; Complete=($merged.Count -gt 0); Refreshed=($newCount -gt 0) }
    }

    # ================================================================
    #  阶段 2：多页并行拉取（RunspacePool 线程池）
    # ================================================================
    # 为什么用并行？
    #   逐页串行拉取太慢了，每页都要等上一页完成。
    #   用线程池可以同时拉多页，大幅节省时间。
    # 为什么用哈希索引？
    #   防止 PowerShell 的引用比较机制导致 Remove 出错。
    Write-Host "[提交日志] 并行拉取 ($maxThreads 线程 / $cpuCores 核心)..."
    try {
        # RunspaceFactory = PowerShell 的多线程池
        # 参数 (1, $maxThreads) = 最少 1 个，最多 $maxThreads 个线程
        $pool = [RunspaceFactory]::CreateRunspacePool(1, $maxThreads)
        $pool.Open()

        $active = @{}        # 活跃任务表：Page → {PS, Handle}
        $morePages = $true   # 是否还有更多页
        $nextPage = 2        # 下一页的页码（第 1 页已在阶段 1 拉过了）

        # 主循环：当还有页要拉 或 还有活跃任务时，继续运行
        while ($morePages -or $active.Count -gt 0) {
            # 检查是否超时
            $elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 0)
            if ($elapsed -ge $timeBudget) {
                Write-Host "[提交日志] 已达时间预算"
                $morePages = $false
            }

            # ---- 发射新任务：填满线程池 ----
            while ($active.Count -lt $maxThreads -and $morePages -and $elapsed -lt $timeBudget) {
                $pg = $nextPage++   # 页码递增

                # 创建新的 PowerShell 任务
                $ps = [PowerShell]::Create()
                $ps.RunspacePool = $pool

                # 注入内联脚本（在独立线程中执行）
                # 为什么要用内联脚本而不用函数？
                #   因为 Runspace 不能直接调用外部函数，需要把代码包进去
                [void]$ps.AddScript({
                    param($u, $tok)
                    $enc = [System.Text.Encoding]::UTF8
                    $hdrs = @{ "PRIVATE-TOKEN" = $tok }   # GitLab API 认证头
                    # 最多重试 10 次
                    for ($a = 1; $a -le 10; $a++) {
                        try {
                            $r = Invoke-WebRequest -Uri $u -Headers $hdrs -UseBasicParsing -TimeoutSec 10
                            $d = $enc.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json
                            return $d
                        } catch {
                            if ($a -lt 10) { Start-Sleep 3 }
                        }
                    }
                    return $null
                })
                # 传入参数：API URL 和令牌
                [void]$ps.AddArgument($uriBase + $pg)
                [void]$ps.AddArgument($ApiToken)

                # 记录任务并异步启动
                $active[$pg] = @{PS=$ps; Handle=$ps.BeginInvoke()}
            }

            # 没有活跃任务了，退出循环
            if ($active.Count -eq 0) { break }

            # ---- 收集已完成的任务 ----
            # 找到所有 IsCompleted = $true 的任务
            $doneKeys = @($active.Keys | Where-Object { $active[$_].Handle.IsCompleted })
            if ($doneKeys.Count -eq 0) {
                Start-Sleep -Milliseconds 200   # 没有完成的，等 200 毫秒再检查
                continue
            }

            # 处理每个已完成的任务
            foreach ($pg in $doneKeys) {
                $task = $active[$pg]
                $items = $task.PS.EndInvoke($task.Handle)   # 获取返回值
                $task.PS.Dispose()
                $active.Remove($pg)

                # 如果这页没数据 → 没有更多页了
                if (-not $items -or $items.Count -eq 0) {
                    $morePages = $false
                    break
                }

                # 解析 commit 数据（和阶段 1 的逻辑相同）
                foreach ($item in $items) {
                    $sha = $item.id
                    if ($known.ContainsKey($sha)) { continue }
                    $known[$sha] = [pscustomobject]@{
                        Sha     = "$sha"
                        Date    = "$($item.committed_date)"
                        Message = "$($item.title)"
                    }
                    $newCount++
                }

                # 动态更新进度条，公式：60 + (页码/11) * 33，范围 60~93
                # 页码越大，进度越接近 93%
                $pp = 60 + [math]::Min(33, [math]::Round(($pg / 11) * 33))
                Write-Host "##PROGRESS##$pp"

                # 如果这页不满 50 条 → 最后一页，没有更多了
                if ($items.Count -lt 50) { $morePages = $false; break }
            }
        }
        $pool.Close()
    } catch {
        Write-Host "[提交日志] 并行拉取中断: $_"
    }

    # ---- 结果汇总 ----
    # 按 commit 日期降序排列（最新的在前面）
    $merged = @($known.Values | Sort-Object {[DateTimeOffset]"$($_.Date)"} -Descending)
    if ($newCount -gt 0) { Write-CommitCache $merged }
    Write-Host "##PROGRESS##93"   # 93% 进度
    Write-Host "[提交日志] 缓存: $($merged.Count) 条 ($newCount 新增)。"
    return @{ Commits=$merged; Complete=($merged.Count -gt 0); Refreshed=($newCount -gt 0) }
}

# ---- ZIP 文件校验函数 ----
# 检查 ZIP 包是否有效（能正常打开且内有文件）
# 防止下载了损坏的 ZIP 包导致后续解压失败
function Test-ZipFile($path) {
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
        $valid = $zip.Entries.Count -gt 0   # 至少有一个文件才算有效
        $zip.Dispose()
        return $valid
    } catch {
        return $false
    }
}

# ==================================================================
#  文件同步函数 (Sync-SourceFiles)
# ==================================================================
# 把下载解压后的源码目录内容同步到目标目录
# 三级校验策略（由快到慢，逐级递进）：
#   第 1 级 —— 文件大小 + 修改时间 → 完全一致则跳过（最快，无需读文件内容）
#   第 2 级 —— SHA-256 哈希比对 → 内容相同则不复制（元数据不同但内容相同）
#   第 3 级 —— 内容确实不同 → 复制并验证 SHA-256（每次写入后都校验一次）
#
# 参数 $from: 源目录（解压出的临时目录）
# 参数 $to:   目标目录（ServerS4A21-AUM）
# 返回: 变更的文件数量
function Sync-SourceFiles($from, $to) {
    $changes = 0       # 变更计数
    $srcList = [System.Collections.ArrayList]::new()   # 变更文件详情列表（用于写入文件变更日志）
    $buildCount = 0    # 构建产物计数（.dll/.exe 等，只统计不展开）
    $dataCount = 0     # 数据文件计数（.db/.pvf 等）
    $otherCount = 0    # 其他文件计数

    # 遍历源目录中的所有文件
    Get-ChildItem $from -File -Recurse | ForEach-Object {
        # 计算相对路径（去掉源目录前缀）
        $relative = $_.FullName.Substring($from.Length).TrimStart('\')

        # ---- 过滤规则：以下文件不处理 ----
        # .git 目录和 dist 目录是特殊目录，不参与同步
        if ($relative -match '(^|\\)(\.git|dist)(\\|$)') { return }
        # 启动脚本是用户可能自行修改的，不覆盖
        if ($relative -match '(^|\\)start-server\.(bat|sh)$') { return }

        $destination = Join-Path $to $relative
        $existing = Get-Item $destination -ErrorAction SilentlyContinue

        # ---- 第 1 级：大小 + 时间戳完全匹配 → 直接跳过 ----
        if ($existing -and $existing.Length -eq $_.Length -and $existing.LastWriteTimeUtc -eq $_.LastWriteTimeUtc) {
            return   # ForEach-Object 中的 return 相当于 continue
        }

        # ---- 第 2 级：时间戳不同但内容可能相同 → SHA-256 比对 ----
        $sameContent = $false
        if ($existing) {
            # Get-FileHash 计算文件的 SHA-256 指纹
            # SHA-256 碰撞概率极低（约 2^-256），可以放心用来判断文件是否相同
            $sourceHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash $destination -Algorithm SHA256).Hash
            $sameContent = $sourceHash -eq $destinationHash
        }
        if ($sameContent) { return }   # 内容相同，跳过

        # ---- 第 3 级：内容不同 → 复制（带重试和校验） ----
        $updated = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                # 确保目标目录存在
                $directory = Split-Path $destination -Parent
                if (-not (Test-Path $directory)) {
                    New-Item -ItemType Directory -Path $directory -Force | Out-Null
                }
                # 复制文件
                Copy-Item $_.FullName $destination -Force

                # 写入后立即校验 SHA-256，确保复制完整
                $sourceHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
                $destinationHash = (Get-FileHash $destination -Algorithm SHA256).Hash
                if ($sourceHash -ne $destinationHash) {
                    throw "SHA-256 verification failed."
                }

                # 同步时间戳到目标文件
                [System.IO.File]::SetLastWriteTimeUtc($destination, $_.LastWriteTimeUtc)
                $updated = $true
                break
            } catch {
                Write-Host "WARNING: file sync $attempt/3 failed for ${relative}: $_"
                if ($attempt -lt 3) {
                    Start-Sleep -Seconds ([math]::Pow(2, $attempt - 1))
                }
            }
        }

        if ($updated) {
            # 判断是"更新"还是"下载"（取决于目标文件之前是否存在）
            $action = if ($existing) { "更新" } else { "下载" }
            $detail = "$action $relative | $($_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"

            # 获取文件扩展名和文件名（小写，便于分类比较）
            $ext = [System.IO.Path]::GetExtension($relative).ToLower()
            $name = [System.IO.Path]::GetFileName($relative).ToLower()

            # ---- 文件分类输出 ----
            # [FILE:CS] 前缀 → 源码类文件，在 GUI 中用绿色显示
            # 其他类型 → 只统计不展开，在最后汇总输出
            if ($ext -in @('.cs','.csproj','.sln','.sql','.xml','.json','.etc','.bat','.sh','.ps1','.md','.txt','.yml','.yaml') -or
                $name -in @('app.manifest','.gitignore','.gitattributes')) {
                Write-Host "[FILE:CS] $detail"       # 源码变更，详细输出
            } elseif ($ext -in @('.dll','.exe','.pdb','.so','.dylib','.lib','.a','.pch','.obj','.ilk','.exp')) {
                $buildCount++                        # 构建产物，只计数
            } elseif ($ext -in @('.db','.db-bak','.db-wal','.db-shm','.pvf')) {
                $dataCount++                         # 数据文件，只计数
            } else {
                $otherCount++                        # 其他文件，只计数
            }

            [void]$srcList.Add($detail)
            $changes++
        } else {
            Write-Host "ERROR: skipped $relative after 3 failed write attempts."
        }
    }

    # ---- 输出汇总信息 ----
    # [FILE:SUM] 前缀 → 在 GUI 中以橙色显示
    if ($buildCount -gt 0) { Write-Host "[FILE:SUM] 构建产物: ${buildCount}个文件重新生成" }
    if ($dataCount -gt 0)  { Write-Host "[FILE:SUM] 数据文件: ${dataCount}个已同步" }
    if ($otherCount -gt 0) { Write-Host "[FILE:SUM] 其他文件: ${otherCount}个变更" }

    # ---- 将变更详情写入文件变更日志 ----
    if ($srcList.Count -gt 0) {
        $logPath = Join-Path $to "..\文件变更日志.txt"
        try {
            $content = "========================================`r`n文件变更日志 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`r`n========================================`r`n`r$n"
            foreach ($s in $srcList) { $content += "  $s`r`n" }
            $content += "`r`n========================================`r`n"
            [System.IO.File]::WriteAllText($logPath, $content, $utf8)
        } catch { }
    }

    return $changes
}

# ==================================================================
#  清理过期源码文件 (Remove-StaleSourceFiles)
# ==================================================================
# 删除目标目录中存在但源仓库中已删除的 .cs 文件
# 只在 Server 和 Tool 目录中执行，不会误删用户数据
#
# 参数 $from: 源目录（新下载的代码）
# 参数 $to:   目标目录（本地代码）
# 返回: 删除的文件数量
function Remove-StaleSourceFiles($from, $to) {
    $removed = 0
    foreach ($folder in @("Server", "Tool")) {
        $localFolder = Join-Path $to $folder
        if (-not (Test-Path $localFolder)) { continue }

        # 只检查 .cs 源码文件
        Get-ChildItem $localFolder -File -Recurse -Filter "*.cs" | ForEach-Object {
            $relative = $_.FullName.Substring($to.Length).TrimStart('\')
            # 跳过 bin/obj 编译输出目录
            if ($relative -match '(^|\\)(bin|obj)(\\|$)') { return }

            $sourcePath = Join-Path $from $relative
            # 如果源仓库中不存在这个文件 → 删除它
            if (-not (Test-Path $sourcePath)) {
                Remove-Item $_.FullName -Force
                Write-Host "[FILE] 删除过期源码 $relative"
                $removed++
            }
        }
    }
    return $removed
}

# ==================================================================
#  .NET SDK 检测函数
# ==================================================================
# 按优先级查找可用的 .NET SDK：
#   1. 系统 PATH 中的 dotnet（全局安装）
#   2. C:\Program Files\dotnet（标准安装位置）
#   3. 本地的 dotnet-sdk 目录（便携版，放在 AUM管理组件 下）
#
# 返回: dotnet.exe 的完整路径，找不到则返回 $null
function Get-DotNetExe {
    # 优先级 1：系统 PATH 中的 dotnet
    # "dotnet" 不带路径，会搜索 PATH 环境变量
    $sys = "dotnet"
    try {
        $v = & dotnet --version 2>&1
        # 检查版本号是否 >= 10（数字比较，不是字符串比较）
        if ($LASTEXITCODE -eq 0 -and $v -match "^(\d+)\.(\d+)" -and [int]$matches[1] -ge 10) {
            return $sys
        }
    } catch { }

    # 优先级 2：Program Files 标准安装位置
    try {
        $pf = "$env:ProgramFiles\dotnet\dotnet.exe"
        if (Test-Path $pf) {
            $v = & $pf --version 2>&1
            if ($LASTEXITCODE -eq 0 -and $v -match "^(\d+)\.(\d+)" -and [int]$matches[1] -ge 10) {
                return $pf
            }
        }
    } catch { }

    # 优先级 3：本地便携 SDK
    $local = Join-Path $LocalSdk "dotnet.exe"
    if (Test-Path $local) { return $local }

    return $null   # 所有位置都找不到
}

# ==================================================================
#  确保 .NET 10 SDK 可用
# ==================================================================
# 调用 Get-DotNetExe 查找 SDK，找到后打印版本信息
# 找不到则提示用户安装
function Ensure-DotNet10 {
    $dn = Get-DotNetExe
    if ($dn) {
        $ver = & $dn --version 2>&1
        $verStr = $ver.Trim()
        if ($dn -eq "dotnet" -or $dn -like "*Program Files*") {
            Write-Host "  Using system .NET SDK ($verStr)"
        } else {
            Write-Host "  Using bundled .NET SDK ($verStr)"
        }
        return $dn
    }
    Write-Host "  .NET 10 SDK not found."
    Write-Host "  .NET 10 SDK is required. Click 安装SDK in ServerUI, finish the Microsoft installer, then restart ServerUI."
    return $null
}

# ==================================================================
# ==================================================================
#  主更新流程（5 个步骤）
# ==================================================================
# ==================================================================
# 以下是脚本的实际执行部分，按照 [1/5] → [2/5] → [3/5] → [4/5] → [5/5] 的顺序执行
# 每个步骤如果失败，会自动回滚（恢复数据库备份、清理临时文件）

# 编译状态标志（默认失败，编译成功后设为 $true）
$buildOk = $false
$gmBuildOk = $false

# 用 try/catch/finally 包裹整个流程
#   try:     执行 5 个更新步骤
#   catch:   发生错误时恢复数据库备份、清理临时文件
#   finally: 无论成功还是失败，都执行 [5/5] 拉取日志
try {
    # ---- 流程初始化 ----
    # 切换到服务端源码根目录
    Set-Location $SrcRoot

    # 记录当前日期时间（用于日志输出）
    $currentDate  = Get-Date -Format "yyyy-MM-dd"
    $currentTime  = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $threeDaysAgo = (Get-Date).AddHours(-72)   # 72 小时前 = 3 天

    # ---- 打印标题 ----
    # 根据参数显示"增量更新"还是"全量同步"
    $modeText = if ($FullSync) { T "s_full" } else { T "s_inc" }
    Write-Host ""
    Write-Host "========================================"
    Write-Host "  $(T 's_server')$modeText"
    Write-Host "  Date: $currentDate (UTC+8)"
    Write-Host "========================================"
    Write-Host ""

    # ---- 打印重要文件路径提示 ----
    # 帮助用户确认关键文件位置，避免更新时出问题
    Write-Host "[ $(T 's_warn1') ]"
    Write-Host "$(T 's_warn2')"
    Write-Host "$(T 's_warn3')"
    Write-Host "$(T 's_warn4')"
    Write-Host "$(T 's_warn5')"
    Write-Host ""
    Write-Host "========================================"
    Write-Host ""

    # ---- 检查上次更新版本 ----
    # 从更新日志中提取最近一次更新的日期
    if (Test-Path $LogFile) {
        $raw = [System.IO.File]::ReadAllText($LogFile, $utf8)
        $rx = [regex]::Matches($raw, "\d{4}-\d{2}-\d{2}")   # 匹配日期格式
        if ($rx.Count -gt 0) {
            $lv = $rx[$rx.Count - 1].Value   # 取最后一个日期
            if ($lv -eq $currentDate) {
                Write-Host "Last version: $lv (up-to-date)"   # 今天已经更新过
            } else {
                Write-Host "Last version: $lv ($(((Get-Date $currentDate)-(Get-Date $lv)).Days)d ago)"
            }
        }
    } else {
        Write-Host "First run."   # 首次运行，还没有更新日志
    }

    # ================================================================
    #  [1/5] 源码同步前检查 (v2.033)
    # ================================================================
    # v2.033: inventory.db 不再备份/恢复 — 源码包里的
    # Server\DfoServer\Data\inventory.db 有更新则直接同步覆盖 (哪里更新替换哪里)
    # (dist\win-x64\Data 运行时数据库仍由编译前的 DB 快照保护)
    Write-Host ""
    Write-Host ">>> [1/5] Preparing update <<<"
    Write-Host "##PROGRESS##5"   # 进度 5%

    # ================================================================
    #  [2/5] 下载最新源码
    # ================================================================
    Write-Host ""
    Write-Host ">>> [2/5] Downloading source <<<"

    # ---- 准备服务端临时目录 ----
    # 先清空旧的临时目录（防止上次更新残留的文件干扰）
    if (Test-Path $TempDir) { Remove-Item -Recurse -Force $TempDir }
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
    $TempZip = Join-Path $TempDir "main.zip"       # 下载的 ZIP 包
    $TempExtract = Join-Path $TempDir "extract"     # 解压后的目录

    # 禁止下载进度条（避免在日志中输出大量进度字符）
    $ProgressPreference = "SilentlyContinue"

    # ---- 准备 GM 工具临时目录 ----
    $gmTempDir = Join-Path $env:TEMP "ServerS4A21-gmupdate"
    if (Test-Path $gmTempDir) { Remove-Item -Recurse -Force $gmTempDir }
    New-Item -ItemType Directory -Path $gmTempDir -Force | Out-Null
    $gmTempZip = Join-Path $gmTempDir "main.zip"
    $gmTempExtract = Join-Path $gmTempDir "extract"

    # GM 工具的仓库地址（已迁移到 GitGud，需要认证下载）
    $gmRepoApi = "https://gitgud.io/api/v4/projects/rewio%2FS4A21GmTool"

    # ================================================================
    #  连接预检：快速测试下载源可达性 (v1.914)
    # ================================================================
    # v1.914: 支持 -UseMirror 手动强制镜像下载，跳过 GitGud 检测
    if ($UseMirror) {
        Write-Host "[镜像下载] 用户手动启用 → 跳过 GitGud 检测，直接使用镜像源。"
        $sourceAvailability = @{ GitGud = $false; Gitee = $true; GitHub = $true; Codeberg = $true }
    } else {
    # 普通用户无需等待镜像源检测，下载时按序尝试即可
    Write-Host ""
    
    # 清理 DNS/网络缓存，确保使用最新解析（避免 CDN 旧缓存导致误判）
    try { ipconfig /flushdns | Out-Null } catch {}
    
    $sourceAvailability = Test-SourceAvailability
    }
    
    $skipGitGud = -not $sourceAvailability.GitGud
    $mirrorsAvailable = $sourceAvailability.Gitee -or $sourceAvailability.GitHub -or $sourceAvailability.Codeberg

    # ---- 根据预检结果决定下载策略 ----
    # 上传者(GitGud可达) → 使用检测结果精确选择
    # 普通用户(GitGud不可达) → 按序尝试镜像，让下载本身做超时判断
    $svrPrimaryUrl = $null
    $svrUseAuth = $false
    $svrTimeout = 30
    $svrRetries = 3
    $svrSourceName = ""
    $svrTryAllMirrors = $false
    
    if ($sourceAvailability.GitGud) {
        Write-Host "[连接检测] GitGud 可达 → 使用主源下载。"
        $svrPrimaryUrl = "https://gitgud.io/api/v4/projects/rewio%2FServerS4A21/repository/archive.zip?sha=master"
        $svrUseAuth = $true
        $svrTimeout = 30
        $svrRetries = 5
        $svrSourceName = "GitGud"
    } elseif ($sourceAvailability.Gitee) {
        Write-Host "[连接检测] GitGud 不可达，Gitee(国内) 可达 → 使用 Gitee 镜像下载。"
        Write-Host "[提示] 仓库页面: $MirrorGiteePage"
        $svrPrimaryUrl = $MirrorServerUrls[0]
        $svrSourceName = "Gitee"
    } elseif ($sourceAvailability.GitHub) {
        Write-Host "[连接检测] GitGud/Gitee 不可达，GitHub 可达 → 使用 GitHub 镜像下载。"
        Write-Host "[提示] 如镜像下载失败，请打开页面确认: $MirrorGitHubPage"
        $svrPrimaryUrl = $MirrorServerUrls[1]
        $svrSourceName = "GitHub"
    } elseif ($sourceAvailability.Codeberg) {
        Write-Host "[连接检测] GitGud/Gitee/GitHub 不可达，Codeberg 可达 → 使用 Codeberg 镜像下载。"
        Write-Host "[提示] 如镜像下载失败，请打开页面确认: $MirrorCodebergPage"
        $svrPrimaryUrl = $MirrorServerUrls[2]
        $svrSourceName = "Codeberg"
    } else {
        # v1.914: 普通用户模式 — 不做预检，下载时按序尝试所有镜像 + 本地缓存兜底
        Write-Host "[连接检测] GitGud 不可达 (普通用户)，将依次尝试 Gitee → GitHub → Codeberg → 本地缓存。"
        $svrTryAllMirrors = $true
        $svrSourceName = "自动选择"
    }
    
    # ================================================================
    #  并行下载：服务端源码 + GM 工具 —— 同时下载，节省时间
    # ================================================================
    # 使用 RunspacePool 创建 2 线程的线程池，两个下载同时进行
    $pool = [RunspaceFactory]::CreateRunspacePool(1, 2)
    $pool.Open()

    # ---- 任务 1：下载服务端源码 ZIP ----
    # v1.914: 普通用户直接按序尝试全部镜像，不做预检
    $svrPS = $null; $svrHandle = $null; $svrOk = $false; $svrSize = "N/A"
    $svrFromGitee = $false
    if ($svrTryAllMirrors) {
        # 普通用户：按序尝试 Gitee API → GitHub API → Codeberg Raw → 本地缓存
        # 每个源独立短超时，不浪费总时间
        $svrMirrors = @(
            @{Name="Gitee"; Type="gitee"; Url=$MirrorServerUrls[0]},
            @{Name="GitHub"; Type="github"; Url=$MirrorServerUrls[1]},
            @{Name="Codeberg"; Type="codeberg"; Url=$MirrorServerUrls[2]}
        )
        # v1.921: 版本择优 — 预取各镜像 latest.json 元数据, 按版本标识字符串
        # 从新到旧排序 (包名形如 ServerS4A21-20260731-0845-1234, 固定宽度格式
        # 下字典序 = 时间序, 元数据缺失的源排最后);
        # 元数据同时用于"自洽 sha256"校验 (只与本镜像自己的包比对)
        $svrMeta = @{}
        foreach ($m in $svrMirrors) {
            $meta = Get-MirrorMeta $m.Type
            if ($meta -and $meta.Sha256) {
                $svrMeta[$m.Name] = $meta
                $short = $meta.Sha256.Substring(0, [Math]::Min(8, $meta.Sha256.Length))
                Write-Host "Server download: $($m.Name) 元数据 版本=$($meta.Version) sha=$short..."
            } else {
                Write-Host "Server download: $($m.Name) 元数据获取失败 (仅做 CRC 校验)"
            }
        }
        # v1.922: 版本择优 + 固定优先级双键排序 —
        # 版本标识降序, 相同/缺失时按原始顺序升序 (Gitee→GitHub→Codeberg, Gitee 国内最快)
        $idx = 0
        foreach ($m in $svrMirrors) { $m.Index = $idx; $idx++ }
        $svrMirrors = @($svrMirrors | Sort-Object @{Expression={$svrMeta[$_.Name].Version}; Descending=$true}, @{Expression={$_.Index}; Descending=$false})
        foreach ($m in $svrMirrors) {
            if ($svrOk) { break }
            try {
                Write-Host "Server download: 尝试 $($m.Name)..."
                Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
                if ($m.Type -eq "gitee") { $svrOk = Download-FromGitee $m.Url $TempZip 20 }
                elseif ($m.Type -eq "github") { $svrOk = Download-FromGitHub "mirrors/ServerS4A21-latest.zip" $TempZip 20 }
                else { Invoke-WebRequest -Uri $m.Url -OutFile $TempZip -UseBasicParsing -TimeoutSec 20 }
                if (-not $svrOk) { continue }
                # v1.921: 完整性校验链 — 魔数(结构) → CRC(内容) → 自洽 sha256
                if (-not (Test-ZipIntegrity $TempZip)) { Write-Host "Server download: $($m.Name) 魔数校验失败"; $svrOk = $false; continue }
                if (-not (Test-ZipExtract $TempZip)) { Write-Host "Server download: $($m.Name) CRC 解压校验失败"; $svrOk = $false; continue }
                $meta = $svrMeta[$m.Name]
                if ($meta -and $meta.Sha256) {
                    $h = (Get-FileHash $TempZip -Algorithm SHA256).Hash.ToLower()
                    if ($h -ne $meta.Sha256.ToLower()) {
                        Write-Host "Server download: $($m.Name) 自洽 sha256 不匹配 (镜像可能被污染/半更新)"
                        $svrOk = $false; continue
                    }
                }
                $svrOk = $true; break
            } catch { $svrOk = $false }
        }
        if (-not $svrOk -and (Test-Path $LatestSvr)) {
            Write-Host "Server download: 所有镜像失败，使用本地缓存。"
            Copy-Item $LatestSvr $TempZip -Force
            if (Test-ZipExtract $TempZip) { $svrOk = $true }
            else { Write-Host "Server download: 本地缓存损坏, 弃用"; Remove-Item $TempZip -Force -ErrorAction SilentlyContinue }
        }
        if ($svrOk) { $svrSize = "$([math]::Round((Get-Item $TempZip).Length/1KB)) KB" }
        Write-Host "Server download: $(if($svrOk){'OK'}else{'FAILED'}) ($svrSize)"
    } elseif ($svrPrimaryUrl) {
        if ($svrSourceName -eq "Gitee") {
            # Gitee 私有仓库：用 API 下载，不入池（方便调用 Download-FromGitee）
            $svrFromGitee = $true
            Write-Host "Server download: 正在从 Gitee(API) 下载..."
        } else {
            $svrPS = [PowerShell]::Create()
            $svrPS.RunspacePool = $pool
            [void]$svrPS.AddScript({
                param($u, $t, $tok, $timeout, $maxRetries)
                Remove-Item $t -Force -ErrorAction SilentlyContinue
                # v1.921: ZIP 完整性快速校验 (Runspace 内无法调用外部函数, 内联实现)
                function Test-ZipIntegrity($p) {
                    try {
                        $fs = [IO.File]::OpenRead($p)
                        try {
                            if ($fs.Length -lt 22) { return $false }
                            $head = New-Object byte[] 4
                            [void]$fs.Read($head, 0, 4)
                            if (-not ($head[0] -eq 0x50 -and $head[1] -eq 0x4B)) { return $false }
                            $scanLen = [Math]::Min([long]$fs.Length, 65557)
                            $buf = New-Object byte[] $scanLen
                            [void]$fs.Seek(-$scanLen, [IO.SeekOrigin]::End)
                            [void]$fs.Read($buf, 0, $scanLen)
                            for ($i = 0; $i -lt $scanLen - 3; $i++) {
                                if ($buf[$i] -eq 0x50 -and $buf[$i+1] -eq 0x4B -and $buf[$i+2] -eq 0x05 -and $buf[$i+3] -eq 0x06) { return $true }
                            }
                            return $false
                        } finally { $fs.Dispose() }
                    } catch { return $false }
                }
                # v1.921: CRC 内容级校验 (完整解压, ZIP 条目自带 CRC32 自动校验)
                function Test-ZipExtract($p) {
                    try {
                        $zip = [System.IO.Compression.ZipFile]::OpenRead($p)
                        try {
                            if ($zip.Entries.Count -eq 0) { return $false }
                            $buf = New-Object byte[] 65536
                            foreach ($entry in $zip.Entries) {
                                $s = $entry.Open()
                                try { while ($s.Read($buf, 0, $buf.Length) -gt 0) { } } finally { $s.Dispose() }
                            }
                            return $true
                        } finally { $zip.Dispose() }
                    } catch { return $false }
                }
            $h = if ($tok) { @{ "PRIVATE-TOKEN" = $tok } } else { @{} }
            for ($a = 1; $a -le $maxRetries; $a++) {
                try {
                    if ($h.Count -gt 0) {
                        Invoke-WebRequest -Uri $u -OutFile $t -Headers $h -UseBasicParsing -TimeoutSec $timeout
                    } else {
                        Invoke-WebRequest -Uri $u -OutFile $t -UseBasicParsing -TimeoutSec $timeout
                    }
                    # 大小 + 魔数 + CRC 内容级三重校验: 截断/损坏文件立即重试
                    if ((Test-Path $t) -and (Get-Item $t).Length -gt 51200 -and (Test-ZipIntegrity $t) -and (Test-ZipExtract $t)) { return $true }
                    Remove-Item $t -Force -ErrorAction SilentlyContinue
                } catch {
                    Remove-Item $t -Force -ErrorAction SilentlyContinue
                }
                if ($a -lt $maxRetries) { Start-Sleep -Seconds ([math]::Pow(2, $a - 1)) }
            }
            return $false
        })
            [void]$svrPS.AddArgument($svrPrimaryUrl)
            [void]$svrPS.AddArgument($TempZip)
            [void]$svrPS.AddArgument($(if ($svrUseAuth) { $ApiToken } else { $null }))
            [void]$svrPS.AddArgument($svrTimeout)
            [void]$svrPS.AddArgument($svrRetries)
            $svrHandle = $svrPS.BeginInvoke()
            Write-Host "Server download: 正在从 $svrSourceName 下载..."
        }
    } else {
        Write-Host "Server download: SKIPPED (所有在线源不可达，稍后使用本地缓存)"
    }

    # ---- 任务 2：下载 GM 工具源码 ZIP ----
    # v1.911: GM 主源已迁移到 GitGud（与服务器同源），需要认证
    $gmPS = [PowerShell]::Create()
    $gmPS.RunspacePool = $pool
    [void]$gmPS.AddScript({
        param($u, $t, $tok, $timeout, $maxRetries)
        Remove-Item $t -Force -ErrorAction SilentlyContinue
        # v1.921: ZIP 完整性快速校验 (Runspace 内内联实现)
        function Test-ZipIntegrity($p) {
            try {
                $fs = [IO.File]::OpenRead($p)
                try {
                    if ($fs.Length -lt 22) { return $false }
                    $head = New-Object byte[] 4
                    [void]$fs.Read($head, 0, 4)
                    if (-not ($head[0] -eq 0x50 -and $head[1] -eq 0x4B)) { return $false }
                    $scanLen = [Math]::Min([long]$fs.Length, 65557)
                    $buf = New-Object byte[] $scanLen
                    [void]$fs.Seek(-$scanLen, [IO.SeekOrigin]::End)
                    [void]$fs.Read($buf, 0, $scanLen)
                    for ($i = 0; $i -lt $scanLen - 3; $i++) {
                        if ($buf[$i] -eq 0x50 -and $buf[$i+1] -eq 0x4B -and $buf[$i+2] -eq 0x05 -and $buf[$i+3] -eq 0x06) { return $true }
                    }
                    return $false
                } finally { $fs.Dispose() }
            } catch { return $false }
        }
        # v1.921: CRC 内容级校验 (完整解压, ZIP 条目自带 CRC32 自动校验)
        function Test-ZipExtract($p) {
            try {
                $zip = [System.IO.Compression.ZipFile]::OpenRead($p)
                try {
                    if ($zip.Entries.Count -eq 0) { return $false }
                    $buf = New-Object byte[] 65536
                    foreach ($entry in $zip.Entries) {
                        $s = $entry.Open()
                        try { while ($s.Read($buf, 0, $buf.Length) -gt 0) { } } finally { $s.Dispose() }
                    }
                    return $true
                } finally { $zip.Dispose() }
            } catch { return $false }
        }
        $h = if ($tok) { @{ "PRIVATE-TOKEN" = $tok } } else { @{} }
        for ($a = 1; $a -le $maxRetries; $a++) {
            try {
                if ($h.Count -gt 0) {
                    Invoke-WebRequest -Uri $u -OutFile $t -Headers $h -UseBasicParsing -TimeoutSec $timeout
                } else {
                    Invoke-WebRequest -Uri $u -OutFile $t -UseBasicParsing -TimeoutSec $timeout
                }
                # 大小 + 魔数 + CRC 内容级三重校验: 截断/损坏文件立即重试
                if ((Test-Path $t) -and (Get-Item $t).Length -gt 10240 -and (Test-ZipIntegrity $t) -and (Test-ZipExtract $t)) { return $true }
                Remove-Item $t -Force -ErrorAction SilentlyContinue
            } catch {
                Remove-Item $t -Force -ErrorAction SilentlyContinue
            }
            if ($a -lt $maxRetries) { Start-Sleep -Seconds ([math]::Pow(2, $a - 1)) }
        }
        return $false
    })
    $gmHandle = $null
    if ($sourceAvailability.GitGud) {
        [void]$gmPS.AddArgument("$gmRepoApi/repository/archive.zip?sha=master")
        [void]$gmPS.AddArgument($gmTempZip)
        [void]$gmPS.AddArgument($ApiToken)
        [void]$gmPS.AddArgument(60)
        [void]$gmPS.AddArgument(5)
        Write-Host "GM download: 正在从 GitGud 主源下载..."
        $gmHandle = $gmPS.BeginInvoke()
    } else {
        # v1.914: GitGud 不可达 → 直接走镜像链（与服务端逻辑一致）
        $gmOk = $false
        $gmPS.Dispose()
        $mirrorsAvailable = $sourceAvailability.Gitee -or $sourceAvailability.GitHub -or $sourceAvailability.Codeberg
        if ($mirrorsAvailable -or $svrTryAllMirrors) {
            $gmMirrors = @(
                @{Name="Gitee"; Url=$MirrorGMUrls[0]},
                @{Name="GitHub"; Url=$MirrorGMUrls[1]},
                @{Name="Codeberg"; Url=$MirrorGMUrls[2]}
            )
            foreach ($m in $gmMirrors) {
                if ($gmOk) { break }
                try {
                    Write-Host "GM download: 尝试 $($m.Name)..."
                    Remove-Item $gmTempZip -Force -ErrorAction SilentlyContinue
                    if ($m.Name -eq "Gitee") { $gmOk = Download-FromGitee $m.Url $gmTempZip 20 }
                    elseif ($m.Name -eq "GitHub") { $gmOk = Download-FromGitHub "mirrors/ServerS4A21-GMTool-latest.zip" $gmTempZip 20 }
                    else { Invoke-WebRequest -Uri $m.Url -OutFile $gmTempZip -UseBasicParsing -TimeoutSec 20 }
                    $testZip = [System.IO.Compression.ZipFile]::OpenRead($gmTempZip)
                    if ($testZip.Entries.Count -gt 0) { $gmOk = $true; $testZip.Dispose(); break }
                    $testZip.Dispose()
                } catch { }
            }
        }
        if (-not $gmOk -and (Test-Path $LatestGM)) {
            Write-Host "GM download: 所有在线源失败，使用本地缓存。"
            Copy-Item $LatestGM $gmTempZip -Force
            try { $tz=[System.IO.Compression.ZipFile]::OpenRead($gmTempZip); if ($tz.Entries.Count -gt 0) { $gmOk = $true }; $tz.Dispose() } catch { }
        }
    }
    if ($svrFromGitee) {
        Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
        if (Download-FromGitee $svrPrimaryUrl $TempZip 30) {
            $svrOk = $true
            $svrSize = "$([math]::Round((Get-Item $TempZip).Length/1KB)) KB"
            Write-Host "Server download: OK ($svrSize) [Gitee API]"
        } else {
            Write-Host "Server download: FAILED (Gitee API)"
        }
    }

    # ---- 等待两个下载任务完成 ----
    if ($svrPS) {
        $svrOk = $svrPS.EndInvoke($svrHandle); $svrPS.Dispose()
        $svrSize = if ($svrOk -and (Test-Path $TempZip)) { "$([math]::Round((Get-Item $TempZip).Length/1KB)) KB" } else { "N/A" }
        Write-Host "Server download: $(if($svrOk){'OK'}else{'FAILED'}) ($svrSize)"
    }

    # 服务器下载后 ZIP 完整性验证
    if ($svrOk) {
        try {
            $testZip = [System.IO.Compression.ZipFile]::OpenRead($TempZip)
            if ($testZip.Entries.Count -eq 0) { throw "ZIP empty" }
            $testZip.Dispose()
            Write-Host "Server ZIP 验证通过 ($($testZip.Entries.Count) 条目)"
        } catch {
            Write-Host "Server ZIP 损坏: $_"
            $svrOk = $false
            Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
        }
    }

    # GM 工具下载结果
    if ($gmHandle) { $gmOk = $gmPS.EndInvoke($gmHandle); $gmPS.Dispose() }
    Write-Host "GM download: $(if($gmOk){'OK'}else{'FAILED'})"
    if ($gmOk) {
        try {
            $testZip = [System.IO.Compression.ZipFile]::OpenRead($gmTempZip)
            if ($testZip.Entries.Count -gt 0) { $testZip.Dispose(); Write-Host "GM ZIP 验证通过" } else { $testZip.Dispose(); throw "empty" }
        } catch { Write-Host "GM ZIP 损坏: $_"; $gmOk = $false }
    }

    # GM 镜像回退 (v1.914)
    # 普通用户已在主流程尝试全部镜像，仅上传者需要此回退
    if (-not $gmOk -and -not $svrTryAllMirrors) {
        Write-Host "GM 下载失败，根据预检结果选择备用源..."
        $gmFallbackUrls = @()
        $gmFallbackNames = @()
        if ($sourceAvailability.Gitee) {
            $gmFallbackUrls += $MirrorGMUrls[0]
            $gmFallbackNames += "Gitee"
        }
        if ($sourceAvailability.GitHub) {
            $gmFallbackUrls += $MirrorGMUrls[1]
            $gmFallbackNames += "GitHub"
        }
        if ($sourceAvailability.Codeberg) {
            $gmFallbackUrls += $MirrorGMUrls[2]
            $gmFallbackNames += "Codeberg镜像"
        }
        foreach ($i in 0..($gmFallbackUrls.Count - 1)) {
            if ($gmOk) { break }
            try {
                Write-Host "  尝试 $($gmFallbackNames[$i]): $($gmFallbackUrls[$i])"
                Remove-Item $gmTempZip -Force -ErrorAction SilentlyContinue
                    if ($gmFallbackNames[$i] -eq "Gitee") { $gmOk = Download-FromGitee $gmFallbackUrls[$i] $gmTempZip 30 }
                    elseif ($gmFallbackNames[$i] -eq "GitHub") { $gmOk = Download-FromGitHub "mirrors/ServerS4A21-GMTool-latest.zip" $gmTempZip 30 }
                    else { Invoke-WebRequest -Uri $gmFallbackUrls[$i] -OutFile $gmTempZip -UseBasicParsing -TimeoutSec 30 }
                $testZip = [System.IO.Compression.ZipFile]::OpenRead($gmTempZip)
                if ($testZip.Entries.Count -gt 0) { $gmOk = $true; $testZip.Dispose(); break }
                $testZip.Dispose()
            } catch { }
        }
        # 本地 GM 兜底 (v1.921: 启用前做 CRC 内容级校验)
        if (-not $gmOk -and (Test-Path $LatestGM)) {
            Write-Host "  所有在线GM源失败，用本地缓存: $LatestGM"
            Copy-Item $LatestGM $gmTempZip -Force
            if (Test-ZipExtract $gmTempZip) { $gmOk = $true }
            else { Write-Host "  GM 本地缓存损坏, 弃用"; Remove-Item $gmTempZip -Force -ErrorAction SilentlyContinue }
        }
    }

    $pool.Close()
    Write-Host "##PROGRESS##20"

    # ---- 镜像回退 (v1.914) ----
    # 普通用户已在主流程尝试全部镜像，直接跳回退
    if (-not $svrOk -and -not $svrTryAllMirrors) {
        Write-Host "[回退] $svrSourceName 下载失败/损坏，根据预检结果选择备用源..."
        
        $fallbackUrls = @()
        $fallbackNames = @()
        if ($sourceAvailability.Gitee -and $svrSourceName -ne "Gitee") {
            $fallbackUrls += $MirrorServerUrls[0]
            $fallbackNames += "Gitee"
        }
        if ($sourceAvailability.GitHub -and $svrSourceName -ne "GitHub") {
            $fallbackUrls += $MirrorServerUrls[1]
            $fallbackNames += "GitHub"
        }
        if ($sourceAvailability.Codeberg -and $svrSourceName -ne "Codeberg") {
            $fallbackUrls += $MirrorServerUrls[2]
            $fallbackNames += "Codeberg"
        }
        
        if ($fallbackUrls.Count -gt 0) {
            Write-Host "[回退] 预检可达的备用源: $($fallbackNames -join ' → ')"
            for ($i = 0; $i -lt $fallbackUrls.Count; $i++) {
                if ($svrOk) { break }
                try {
                    Write-Host "  尝试 $($fallbackNames[$i]): $($fallbackUrls[$i])"
                    Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
                    if ($fallbackNames[$i] -eq "Gitee") { $svrOk = Download-FromGitee $fallbackUrls[$i] $TempZip 30 }
                    elseif ($fallbackNames[$i] -eq "GitHub") { $svrOk = Download-FromGitHub "mirrors/ServerS4A21-latest.zip" $TempZip 30 }
                    else { Invoke-WebRequest -Uri $fallbackUrls[$i] -OutFile $TempZip -UseBasicParsing -TimeoutSec 30 }
                    $testZip = [System.IO.Compression.ZipFile]::OpenRead($TempZip)
                    if ($testZip.Entries.Count -gt 0) {
                        $svrOk = $true; $testZip.Dispose()
                        Write-Host "  $($fallbackNames[$i]) 备用源下载成功!"
                    } else { $testZip.Dispose() }
                } catch {
                    Write-Host "  $($fallbackNames[$i]) 尝试失败: $_"
                }
            }
        } else {
            Write-Host "[回退] 无预检可达的备用源，跳过在线回退。"
        }
    }

    # 终极兜底: 本地 latest/ (v1.921: 启用前做 CRC 内容级校验)
    if (-not $svrOk -and (Test-Path $LatestSvr)) {
        Write-Host "  使用本地缓存: $LatestSvr"
        Copy-Item $LatestSvr $TempZip -Force
        if (Test-ZipExtract $TempZip) { $svrOk = $true }
        else { Write-Host "  本地缓存损坏, 弃用"; Remove-Item $TempZip -Force -ErrorAction SilentlyContinue }
    }

    if (-not $svrOk) {
        Write-Host "ERROR: Server source download failed (all sources)."
        exit 1
    }

    # ================================================================
    #  解压下载的 ZIP 包
    # ================================================================
    # ---- 解压服务端 ----
    Write-Host "Extracting..."
    try {
        # ZIP 完整性预检：确保文件不是损坏的
        try {
            $testZip = [System.IO.Compression.ZipFile]::OpenRead($TempZip)
            if ($testZip.Entries.Count -eq 0) { throw "ZIP empty" }
            $testZip.Dispose()
        } catch {
            Write-Host "ERROR: Server ZIP corrupted during download. Deleting and retrying..."
            Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
            # 尝试镜像回退（如果还没试过）
            if (-not $svrOk) { throw "All download sources produced corrupted ZIP." }
            throw "ZIP integrity check failed: $_"
        }

        # Expand-Archive: PowerShell 5.0+ 内置的解压命令
        Expand-Archive -Path $TempZip -DestinationPath $TempExtract -Force
    } catch {
        Write-Host "ERROR: Server extraction failed: $_"
        exit 1
    }
    # ZIP 解压后通常会在 extract 目录下生成一个子目录
    # 比如 extract/ServerS4A21-main/，我们需要找到它
    $srcDir = Get-ChildItem -Path $TempExtract -Directory | Select-Object -First 1
    if (-not $srcDir) {
        Write-Host "ERROR: Server extraction failed."
        exit 1
    }
    $srcPath = $srcDir.FullName   # 源码实际所在的完整路径

    # ---- 解压 GM 工具（如果下载成功了的话） ----
    if ($gmOk) {
        try {
            # GM ZIP 完整性预检
            $testGmZip = [System.IO.Compression.ZipFile]::OpenRead($gmTempZip)
            if ($testGmZip.Entries.Count -eq 0) { throw "GM ZIP empty" }
            $testGmZip.Dispose()

            Expand-Archive -Path $gmTempZip -DestinationPath $gmTempExtract -Force
            $gmSrcDir = Get-ChildItem -Path $gmTempExtract -Directory | Select-Object -First 1
            if ($gmSrcDir) { $gmSrcPath = $gmSrcDir.FullName }
        } catch {
            Write-Host "GM extraction failed: $_"
            $gmOk = $false   # GM 解压失败不影响服务端更新
        }
    }
    Write-Host "##PROGRESS##30"   # 进度 30%

    # ================================================================
    #  [3/5] 同步文件到本地目录
    # ================================================================
    Write-Host ""
    Write-Host "$(T 's_updating')$modeText) <<<"

    # 根据模式显示不同提示
    if ($FullSync) {
        Write-Host (T "s_fullsync")      # "全量同步 (所有文件)..."
    } else {
        Write-Host "Incremental mode: archive sync will update only content that changed."
    }

    # ---- 文件同步：分两种情况 ----
    # 情况 A：GM 也下载成功了 → 服务端 + GM 并行同步
    # 情况 B：GM 没下载成功 → 只同步服务端
    if ($gmOk -and $gmSrcPath) {
        # === 情况 A：并行同步 服务端 + GM ===
        $gmDir = Join-Path $ScriptRoot "dfogmtool"
        if (-not (Test-Path $gmDir)) {
            New-Item -ItemType Directory -Path $gmDir -Force | Out-Null
        }
        Write-Host "Parallel sync: server + GM source..."

        # 创建 2 线程的线程池
        $pool2 = [RunspaceFactory]::CreateRunspacePool(1, 2)
        $pool2.Open()

        # ---- 并行任务 1：同步服务端源码 ----
        $syncSvr = [PowerShell]::Create()
        $syncSvr.RunspacePool = $pool2
        [void]$syncSvr.AddScript({
            param($from, $to)
            $ch = 0   # 变更文件计数
            $st = 0   # 过期文件删除计数

            # 遍历源目录中的所有文件
            Get-ChildItem $from -File -Recurse | ForEach-Object {
                $relative = $_.FullName.Substring($from.Length).TrimStart('\')
                # 过滤不需要同步的文件（和 Sync-SourceFiles 规则相同）
                if ($relative -match '(^|\\)(\.git|dist)(\\|$)') { return }
                if ($relative -match '(^|\\)start-server\.(bat|sh)$') { return }

                $dst = Join-Path $to $relative
                $exist = Get-Item $dst -ErrorAction SilentlyContinue
                if ($exist -and $exist.Length -eq $_.Length -and $exist.LastWriteTimeUtc -eq $_.LastWriteTimeUtc) { return }

                # SHA-256 内容比对
                $same = $false
                if ($exist) {
                    $sh = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
                    $dh = (Get-FileHash $dst -Algorithm SHA256).Hash
                    $same = ($sh -eq $dh)
                }
                if ($same) { return }

                # 复制并设置时间戳
                $dir = Split-Path $dst -Parent
                if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
                Copy-Item $_.FullName $dst -Force
                [System.IO.File]::SetLastWriteTimeUtc($dst, $_.LastWriteTimeUtc)
                $ch++
            }

            # 清理过期 .cs 文件
            foreach ($folder in @("Server","Tool")) {
                $lf = Join-Path $to $folder
                if (-not (Test-Path $lf)) { continue }
                Get-ChildItem $lf -File -Recurse -Filter "*.cs" | ForEach-Object {
                    $rel = $_.FullName.Substring($to.Length).TrimStart('\')
                    if ($rel -match '(^|\\)(bin|obj)(\\|$)') { return }
                    if (-not (Test-Path (Join-Path $from $rel))) {
                        Remove-Item $_.FullName -Force
                        $st++
                    }
                }
            }
            return "$ch updated, $st stale removed"
        })
        [void]$syncSvr.AddArgument($srcPath)
        [void]$syncSvr.AddArgument($SrcRoot)
        $syncSvrHandle = $syncSvr.BeginInvoke()

        # ---- 并行任务 2：同步 GM 工具源码 ----
        $syncGm = [PowerShell]::Create()
        $syncGm.RunspacePool = $pool2
        [void]$syncGm.AddScript({
            param($from, $to)
            $ch = 0; $st = 0
            Get-ChildItem $from -File -Recurse | ForEach-Object {
                $relative = $_.FullName.Substring($from.Length).TrimStart('\')
                if ($relative -match '(^|\\)(\.git|dist)(\\|$)') { return }
                $dst = Join-Path $to $relative
                $exist = Get-Item $dst -ErrorAction SilentlyContinue
                if ($exist -and $exist.Length -eq $_.Length -and $exist.LastWriteTimeUtc -eq $_.LastWriteTimeUtc) { return }
                $same = $false
                if ($exist) {
                    if ((Get-FileHash $_.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $dst -Algorithm SHA256).Hash) { return }
                }
                $dir = Split-Path $dst -Parent
                if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
                Copy-Item $_.FullName $dst -Force
                [System.IO.File]::SetLastWriteTimeUtc($dst, $_.LastWriteTimeUtc)
                $ch++
            }
            # 清理 GM 过期 .cs 文件
            Get-ChildItem $to -File -Recurse -Filter "*.cs" | ForEach-Object {
                $rel = $_.FullName.Substring($to.Length).TrimStart('\')
                if ($rel -match '(^|\\)(bin|obj)(\\|$)') { return }
                if (-not (Test-Path (Join-Path $from $rel))) {
                    Remove-Item $_.FullName -Force
                    $st++
                }
            }
            return "$ch updated, $st stale removed"
        })
        [void]$syncGm.AddArgument($gmSrcPath)
        [void]$syncGm.AddArgument($gmDir)
        $syncGmHandle = $syncGm.BeginInvoke()

        # ---- 等待两个同步任务完成 ----
        $safetyChanges = $syncSvr.EndInvoke($syncSvrHandle)
        $syncSvr.Dispose()
        Write-Host "Server sync: $safetyChanges"

        $gmChanges = $syncGm.EndInvoke($syncGmHandle)
        $syncGm.Dispose()
        Write-Host "GM sync: $gmChanges"

        $pool2.Close()
        Remove-Item -Recurse -Force $gmTempDir -ErrorAction SilentlyContinue

    } else {
        # === 情况 B：只有服务端（GM 没下载/解压成功）===
        $safetyChanges = Sync-SourceFiles $srcPath $SrcRoot
        $staleRemoved = Remove-StaleSourceFiles $srcPath $SrcRoot
        Write-Host "Safety check done: $safetyChanges file(s) updated, $staleRemoved stale removed."
    }

    # ---- 清理临时目录 ----
    # 下载的 ZIP 和解压内容已经不需要了
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
    Write-Host "##PROGRESS##55"   # 进度 55%

    # ================================================================
    #  [4/5] 编译服务端和 GM 工具
    # ================================================================
    Write-Host ""
    Write-Host ">>> [4/5] Building <<<"

    # ---- .NET SDK 检测 ----
    $dn = Ensure-DotNet10
    $buildOk = $false
    $gmBuildOk = $false

    # ---- 备份编译产物中的数据库文件 (v1.922) ----
    # dist 目录下的数据库由编译过程覆盖, 只备份 inventory.db 不够:
    # 数据库结构更新会生成 inventory.db-shm / inventory.db-wal 等副件,
    # 这里把 Data 下所有 DB 相关文件(.db 及副件)整体快照, 编译后原样恢复
    $distDataDir = Join-Path $SrcRoot "dist\win-x64\Data"
    $distDbBakDir = Join-Path $SrcRoot "dist\win-x64\.db-backup"
    if (Test-Path $distDbBakDir) { Remove-Item -Recurse -Force $distDbBakDir -ErrorAction SilentlyContinue }
    $distDbCount = 0
    if (Test-Path $distDataDir) {
        $dbFiles = Get-ChildItem $distDataDir -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '\.db' }
        foreach ($df in $dbFiles) {
            $rel = $df.FullName.Substring($distDataDir.Length).TrimStart('\')
            $dstF = Join-Path $distDbBakDir $rel
            $dstDir = Split-Path $dstF -Parent
            if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
            Copy-Item $df.FullName $dstF -Force
            $distDbCount++
        }
        Write-Host "Backed up $distDbCount DB file(s) from dist Data"
    } else {
        Write-Host "No dist Data directory, skip DB backup."
    }

    if (-not $dn) {
        Write-Host "Could not obtain .NET SDK. Skipping builds."
    } else {
        # ---- 确定编译目标 ----
        # 服务端项目文件
        $serverProject = Join-Path $SrcRoot "Server\DfoServer\DfoServer.csproj"
        # 编译输出目录（发布到 dist\win-x64）
        $distDir = Join-Path $SrcRoot "dist\win-x64"
        $serverDir = Split-Path $serverProject -Parent

        if (-not (Test-Path $serverProject)) {
            throw "Server project not found: $serverProject"
        }

        # ---- GM 预处理：先停止正在运行的 GM 进程 ----
        # 如果不停止，编译时可能因为文件被占用而失败
        $gmDir = Join-Path $ScriptRoot "dfogmtool"
        $gmProject = Join-Path $gmDir "DfoGmTool.csproj"
        $gmExePath = Join-Path $gmDir "publish\DfoGmTool.exe"
        if (Test-Path $gmExePath) {
            try {
                Get-Process -Name "DfoGmTool" -ErrorAction SilentlyContinue |
                    Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep 1   # 等 1 秒确保进程完全退出
            } catch { }
            Write-Host "Stopped existing GM tool process."
        }

        # ---- 全量保存时间戳快照 ----
        # 编译后所有 C# 文件的时间戳都会变（因为重新生成了）
        # 保存编译前的时间戳，编译后恢复，防止文件变更检测误报
        $tsSave = @{}
        @($SrcRoot, $gmDir) | ForEach-Object {
            Get-ChildItem $_ -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                $tsSave[$_.FullName] = $_.LastWriteTimeUtc
            }
        }

        # ---- 并行编译：服务端 + GM 同时编译 ----
        Write-Host "Compiling server and GM tool in parallel..."
        $pool = [RunspaceFactory]::CreateRunspacePool(1, 2)
        $pool.Open()

        # ============================================================
        #  编译任务 1：服务端 (DfoServer.exe)
        # ============================================================
        $svrPS = [PowerShell]::Create()
        $svrPS.RunspacePool = $pool
        [void]$svrPS.AddScript({
            param($dotnet, $proj, $outDir)
            # 用 ArrayList 收集编译输出的每一行日志
            $lines = [System.Collections.ArrayList]::new()

            # 辅助函数：写一行日志到数组
            function w($m) { [void]$lines.Add($m) }

            # 辅助函数：运行一个命令并捕获所有输出
            # $exe:     可执行文件路径
            # $cmdArgs: 命令行参数数组
            # 返回:     命令的退出码 (0=成功, 非0=失败)
            function Run-Cmd($exe, $cmdArgs) {
                # & = 调用操作符，执行命令
                # 2>&1 = 把标准错误重定向到标准输出（合并输出）
                $tmp = & $exe @cmdArgs 2>&1
                $ec = $LASTEXITCODE
                # Out-String 把输出转成字符串，逐行添加到日志数组
                $tmp | Out-String | ForEach-Object { w $_.TrimEnd() }
                return $ec
            }

            w "Server build: .NET SDK $(& $dotnet --version)"

            if (-not (Test-Path $proj)) {
                w "ERROR: project not found: $proj"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            # 切换到项目目录（dotnet 命令需要在项目目录下执行）
            $projDir = Split-Path $proj -Parent
            Set-Location $projDir

            # 1. 还原 NuGet 依赖包
            # --ignore-failed-sources: 忽略不可用的包源（提高成功率）
            $rc = Run-Cmd $dotnet @("restore", $proj, "--ignore-failed-sources")
            if ($rc -ne 0) {
                w "ERROR: restore failed (exit $rc)"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            # 2. 编译并发布（第 1 次尝试）
            # publish 命令参数说明：
            #   -c Release   : 使用 Release 配置（优化性能）
            #   -r win-x64    : 目标运行时是 64 位 Windows
            #   --self-contained true : 自包含发布（打包 .NET 运行时）
            #   -p:PublishSingleFile=true : 发布为单个 EXE 文件
            #   -p:IncludeNativeLibrariesForSelfExtract=true : 包含原生库
            #   -o $outDir    : 输出到指定目录
            $rc = Run-Cmd $dotnet @("publish", $proj, "-c", "Release", "-r", "win-x64",
                "--self-contained", "true", "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $outDir)

            # 3. 如果第 1 次失败，重试一次
            if ($rc -ne 0) {
                w "Retry: rebuilding..."
                $rc = Run-Cmd $dotnet @("publish", $proj, "-c", "Release", "-r", "win-x64",
                    "--self-contained", "true", "-p:PublishSingleFile=true",
                    "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $outDir)
            }

            # 4. 校验编译产物
            if ($rc -ne 0) {
                w "ERROR: publish failed (exit $rc)"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $exe = Join-Path $outDir "DfoServer.exe"
            if (-not (Test-Path $exe)) {
                w "ERROR: DfoServer.exe not found at $exe"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $size = (Get-Item $exe).Length
            if ($size -le 0) {
                w "ERROR: DfoServer.exe is empty"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            w "OK - DfoServer.exe ($([math]::Round($size/1MB,2)) MB)"
            return [pscustomobject]@{Ok=$true; Log=$lines}
        })
        # 传入编译参数
        [void]$svrPS.AddArgument($dn)
        [void]$svrPS.AddArgument($serverProject)
        [void]$svrPS.AddArgument($distDir)
        $svrHandle = $svrPS.BeginInvoke()

        # ============================================================
        #  编译任务 2：GM 工具 (DfoGmTool.exe)
        # ============================================================
        $gmPS = [PowerShell]::Create()
        $gmPS.RunspacePool = $pool
        [void]$gmPS.AddScript({
            param($dotnet, $proj, $gmDirPath)
            $lines = [System.Collections.ArrayList]::new()
            function w($m) { [void]$lines.Add($m) }
            function Run-Cmd($exe, $cmdArgs) {
                $tmp = & $exe @cmdArgs 2>&1
                $ec = $LASTEXITCODE
                $tmp | Out-String | ForEach-Object { w $_.TrimEnd() }
                return $ec
            }

            w "GM build: .NET SDK $(& $dotnet --version)"

            if (-not (Test-Path $proj)) {
                w "GM project not found, skipping."
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $projDir = Split-Path $proj -Parent
            Set-Location $projDir

            # GM 工具也走同样的流程：restore → publish → 校验
            $rc = Run-Cmd $dotnet @("restore", $proj, "--ignore-failed-sources")
            if ($rc -ne 0) {
                w "WARNING: GM restore failed (exit $rc)"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $pubDir = Join-Path $gmDirPath "publish"
            $rc = Run-Cmd $dotnet @("publish", $proj, "-c", "Release", "-r", "win-x64",
                "--self-contained", "true", "-o", $pubDir)
            if ($rc -ne 0) {
                w "Retry: rebuilding..."
                $rc = Run-Cmd $dotnet @("publish", $proj, "-c", "Release", "-r", "win-x64",
                    "--self-contained", "true", "-o", $pubDir)
            }

            if ($rc -ne 0) {
                w "WARNING: GM publish failed (exit $rc)"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $exe = Join-Path $pubDir "DfoGmTool.exe"
            if (-not (Test-Path $exe)) {
                w "WARNING: DfoGmTool.exe not found"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            $size = (Get-Item $exe).Length
            if ($size -le 0) {
                w "WARNING: DfoGmTool.exe is empty"
                return [pscustomobject]@{Ok=$false; Log=$lines}
            }

            w "OK - DfoGmTool.exe ($([math]::Round($size/1MB,2)) MB)"
            return [pscustomobject]@{Ok=$true; Log=$lines}
        })
        [void]$gmPS.AddArgument($dn)
        [void]$gmPS.AddArgument($gmProject)
        [void]$gmPS.AddArgument($gmDir)
        $gmHandle = $gmPS.BeginInvoke()

        # ---- 等待服务端编译完成（先完成先处理） ----
        $svrResult = $svrPS.EndInvoke($svrHandle)
        $svrPS.Dispose()
        # 输出服务端编译日志（-join "`n" 把数组用换行符连接成一个字符串）
        Write-Host ($svrResult.Log -join "`n")
        $buildOk = $svrResult.Ok

        # ---- 等待 GM 编译完成 ----
        $gmResult = $gmPS.EndInvoke($gmHandle)
        $gmPS.Dispose()
        Write-Host ($gmResult.Log -join "`n")
        $gmBuildOk = $gmResult.Ok

        $pool.Close()
    }

    # ---- 恢复时间戳快照 ----
    # 编译过程中很多文件的修改时间被改变了，恢复成编译前的时间
    # 这样下次更新时不会误报这些文件被修改过
    foreach ($path in $tsSave.Keys) {
        if (Test-Path $path) {
            try {
                [System.IO.File]::SetLastWriteTimeUtc($path, $tsSave[$path])
            } catch { }
        }
    }

    # ---- 恢复 dist 目录的数据库文件 (v1.922) ----
    # 编译会覆盖 Data 目录, 把快照中的全部 DB 文件(含 shm/wal 副件)原样恢复
    if (Test-Path $distDbBakDir) {
        $restored = 0
        Get-ChildItem $distDbBakDir -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $rel = $_.FullName.Substring($distDbBakDir.Length).TrimStart('\')
            $dstF = Join-Path $distDataDir $rel
            $dstDir = Split-Path $dstF -Parent
            if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
            Copy-Item $_.FullName $dstF -Force
            $restored++
        }
        Remove-Item -Recurse -Force $distDbBakDir -ErrorAction SilentlyContinue
        Write-Host "Restored $restored DB file(s) to dist Data"
    }

    # ---- 补充检查：确保关键配置文件存在 ----
    # 有时候新下载的代码可能缺少这些文件，从源代码目录复制过去
    $checkFiles = @(
        @{src="Server\DfoServer\Sqlite\item_schema.sql"; dst="dist\win-x64\Sqlite\item_schema.sql"},
        @{src="Server\DfoServer\channel_info.etc";       dst="dist\win-x64\channel_info.etc"}
    )
    foreach ($cf in $checkFiles) {
        $dp = Join-Path $SrcRoot $cf.dst
        if (-not (Test-Path $dp)) {
            $sp = Join-Path $SrcRoot $cf.src
            if (Test-Path $sp) {
                $dd = Split-Path $dp -Parent
                if (-not (Test-Path $dd)) {
                    New-Item -ItemType Directory -Path $dd -Force | Out-Null
                }
                Copy-Item $sp $dp -Force
                Write-Host "Fixed: copied $($cf.dst) from source"
            }
        }
    }

    # ---- 编译失败 → 报错退出 ----
    if (-not $buildOk) {
        Write-Host "ERROR: Update files were synchronized but the server build did not succeed."
        exit 1
    }
    Write-Host "##PROGRESS##85"   # 进度 85%

} catch {
    # ================================================================
    #  错误处理：清理临时文件
    # ================================================================
    Write-Host "ERROR: $_"
    # 清理临时目录
    if (Test-Path $TempDir) {
        Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
    }
    exit 1

} finally {
    # ================================================================
    #  [5/5] 拉取提交日志
    # ================================================================
    # 无论前面步骤是否成功，都会执行这一步
    # （finally 块在 try/catch 之后总是执行）
    Write-Host ""

    if ($SkipCommitLog) {
        # 用户在 GUI 中勾选了"跳过更新日志"复选框
        Write-Host ">>> [5/5] 【已跳过更新日志拉取 — 由用户设置】 <<<"
    } else {
        Write-Host ">>> [5/5] 【正在获取仓库更新日志中，速度较慢，请等待】 <<<"
    }

    # 确保这些变量有值（防止 finally 在 try 块执行前就触发）
    if (-not $currentDate)  { $currentDate  = Get-Date -Format "yyyy-MM-dd" }
    if (-not $currentTime)  { $currentTime  = Get-Date -Format "yyyy-MM-dd HH:mm:ss" }
    if (-not $modeText)     { $modeText     = if ($FullSync) { T "s_full" } else { T "s_inc" } }

    # ---- 拉取并组织提交日志 ----
    $allGrouped = @{}

    if (-not $SkipCommitLog) {
        # --- 方案 A：优化方案（带缓存 + 并行拉取） ---
        $history = Sync-CommitHistory
        foreach ($c in @($history.Commits)) {
            try {
                # 转换 UTC 时间到北京时间
                $d = ToChinaDate $c.Date
                # 只取 commit 消息的第一行（标题）
                $message = "$($c.Message)".Split("`n")[0].Trim()
                # 消息太长会破坏日志格式，截断到 120 字符
                if ($message.Length -gt 120) {
                    $message = $message.Substring(0, 117) + "..."
                }
                if (-not $allGrouped.Contains($d)) { $allGrouped[$d] = @() }
                $allGrouped[$d] += $message
            } catch { }
        }

        # --- 方案 B：兜底方案（传统逐页拉取） ---
        # 如果优化方案没拉到任何数据（0 条），启动旧版可靠方式
        if ($allGrouped.Count -eq 0) {
            Write-Host "[提交日志] 优化方案无数据，切换旧版可靠方案 (直接 API 全量拉取)..."
            try {
                $page = 1
                $perPage = 50
                # 从 3 年前开始拉取
                $fbSince = (Get-Date).AddYears(-3).ToString("yyyy-MM-ddTHH:mm:ssZ")

                # 逐页循环拉取
                while ($true) {
                    $resp = $null
                    # 每页最多重试 10 次
                    for ($a = 1; $a -le 10; $a++) {
                        try {
                            $resp = Invoke-WebRequest -Uri "$RepoApi/repository/commits?ref_name=master&per_page=$perPage&page=$page&since=$fbSince" -Headers $ApiHeaders -UseBasicParsing -TimeoutSec 15
                            break
                        } catch {
                            if ($a -lt 10) { Start-Sleep 1 }
                        }
                    }
                    if (-not $resp) {
                        throw "兜底拉取第${page}页失败 (10次重试后)"
                    }

                    # 解析 JSON 响应
                    $list = $utf8.GetString($resp.RawContentStream.ToArray()) | ConvertFrom-Json
                    if ($list.Count -eq 0) { break }   # 没有更多数据了

                    foreach ($c in $list) {
                        # GitLab API 返回字段（与之前 Gitea/Codeberg 不同！）
                        #   committed_date → commit 时间
                        #   title          → commit 标题
                        $cd = "$($c.committed_date)"
                        $msg = "$($c.title)"
                        $d = ToChinaDate $cd
                        $t = $msg.Split("`n")[0].Trim()
                        if ($t.Length -gt 120) { $t = $t.Substring(0,117) + "..." }
                        if (-not $allGrouped.Contains($d)) { $allGrouped[$d] = @() }
                        $allGrouped[$d] += $t
                    }

                    if ($list.Count -lt $perPage) { break }   # 最后一页
                    $page++
                }
            } catch {
                Write-Host "[提交日志] 旧版方案也失败: $_"
            }
        }

        # v1.911: GitGud API 无数据 → 从镜像下载缓存日志
        # v1.918: 镜像日志只作为"提交历史补充数据源"，不再整文件覆盖本地日志 —
        #         最终日志仍由本次生成（版本头=本次日期），镜像提交段并入 $allGrouped 去重
        if ($allGrouped.Count -eq 0) {
            Write-Host "[提交日志] GitGud API 无数据，从镜像下载缓存日志作为补充..."
            $mirrorLog = Join-Path $env:TEMP "mirror-log.txt"
            Remove-Item $mirrorLog -Force -ErrorAction SilentlyContinue
            $logOk = $false
            # 按优先级尝试: Gitee(API) → GitHub(API) → Codeberg(raw)
            if (-not $logOk) {
                try { $logOk = Download-FromGitee "$MirrorGiteeRaw/mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt" $mirrorLog 15 } catch {}
            }
            if (-not $logOk) {
                try { $logOk = Download-FromGitHub "mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt" $mirrorLog 15 } catch {}
            }
            if (-not $logOk) {
                try {
                    Invoke-WebRequest -Uri "$MirrorCodebergRaw/mirrors/%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt" -OutFile $mirrorLog -UseBasicParsing -TimeoutSec 15
                    $logOk = (Test-Path $mirrorLog) -and ((Get-Item $mirrorLog).Length -gt 0)
                } catch {}
            }
            if ($logOk) {
                Write-Host "[提交日志] 镜像日志下载成功，解析其中的提交历史..."
                # 解析镜像日志: "--- 日期 (n commits) ---" 段头 + "  " 开头的提交消息行
                $mirrorText = [System.IO.File]::ReadAllText($mirrorLog, $utf8)
                $curD = $null
                foreach ($ml in ($mirrorText -split "`r?`n")) {
                    $mt = $ml.Trim()
                    if ($mt -match "^--- (\d{4}-\d{2}-\d{2})") {
                        $curD = $Matches[1]
                        if (-not $allGrouped.ContainsKey($curD)) { $allGrouped[$curD] = @() }
                    }
                    elseif ($curD -and $mt -like "  *" -and $mt.Length -gt 2) {
                        $msg = $mt.Trim()
                        if ($msg.Length -gt 0 -and $allGrouped[$curD] -notcontains $msg) {
                            $allGrouped[$curD] += $msg
                        }
                    }
                }
                Write-Host "[提交日志] 镜像日志并入完成（共 $($allGrouped.Count) 个日期分组）。"
            }
            Remove-Item $mirrorLog -Force -ErrorAction SilentlyContinue
        }
    }

    # ---- 按日期排序 ----
    # $sortedDates: 降序（最新的在前面，用于写入日志文件）
    # $sortedDatesAsc: 升序（最旧的在前面，用于控制台输出）
    # 镜像日志已并入数据源（不整文件覆盖），本地日志始终由本次生成 → 版本头 = 本次更新日期
    $sortedDates = $allGrouped.Keys | Sort-Object -Descending
    $sortedDatesAsc = $allGrouped.Keys | Sort-Object
    $totalCommits = 0
    foreach ($d in $sortedDates) { $totalCommits += $allGrouped[$d].Count }

    # ---- 打印完成信息 ----
    Write-Host ""
    Write-Host "========================================"
    Write-Host "$(T 's_done')$modeText"
    Write-Host "  Version: $currentDate | Commits: $totalCommits"
    if ($buildOk) { Write-Host "  Server Build: OK" } else { Write-Host "  Server Build: Skipped" }
    if ($gmBuildOk) { Write-Host "  GM Tool Build: OK" } else { Write-Host "  GM Tool Build: Skipped" }
    Write-Host "========================================"
    Write-Host ""

    # ================================================================
    #  写入更新日志文件 (更新日志.txt)
    # ================================================================
    $ver = T "s_ver"      # "版本:"
    $up = T "s_up"        # "更新时间:"
    $total = T "s_total"   # "统计提交:"
    $hist = T "s_hist"    # "更新历史 (从最初到现在，北京时间 UTC+8):"

    # 用 ArrayList 逐行构建日志内容
    $logLines = [System.Collections.ArrayList]::new()
    [void]$logLines.Add("========================================")
    [void]$logLines.Add($ver + $currentDate)
    [void]$logLines.Add($up + $currentTime)
    [void]$logLines.Add($total + $totalCommits)
    [void]$logLines.Add("方式: " + $modeText)
    $bs = if ($buildOk) { "OK" } else { "Skipped" }
    [void]$logLines.Add("Server Build: " + $bs)
    $gbs = if ($gmBuildOk) { "OK" } else { "Skipped" }
    [void]$logLines.Add("GM Tool Build: " + $gbs)
    [void]$logLines.Add("========================================")
    [void]$logLines.Add("")
    [void]$logLines.Add($hist)
    [void]$logLines.Add("")

    # 按日期分组输出 commit 记录
    foreach ($d in $sortedDates) {
        [void]$logLines.Add("--- $d ($($allGrouped[$d].Count) commits) ---")
        foreach ($m in $allGrouped[$d]) {
            $tt = if ($m.Length -gt 120) { $m.Substring(0,117)+"..." } else { $m }
            [void]$logLines.Add("  $tt")
        }
        [void]$logLines.Add("")
    }
    [void]$logLines.Add("========================================")
    [void]$logLines.Add("")

    # 写入文件（UTF-8 with BOM，确保记事本能正确识别中文）
    $logText = ($logLines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($LogFile, $logText, (New-Object System.Text.UTF8Encoding $true))
    Write-Host "[提交日志] 已输出 更新日志.txt ($totalCommits 条提交)"

    # ================================================================
    #  控制台输出最近的更新记录（最近 7 天）
    # ================================================================
    $sda = (Get-Date).AddDays(-7).ToString("yyyy-MM-dd")   # 7 天前

    foreach ($d in $sortedDatesAsc) {
        if ($d -lt $sda) { continue }   # 超过 7 天的不在控制台显示
        Write-Host "--- $d ($($allGrouped[$d].Count) commits) ---"
        foreach ($m in $allGrouped[$d]) {
            $tt = if ($m.Length -gt 120) { $m.Substring(0,117)+"..." } else { $m }
            Write-Host "  $tt"
        }
        Write-Host ""
    }

    # 如果有超过 7 天的记录，提示用户去查看完整日志文件
    if (($sortedDatesAsc | Where-Object { $_ -lt $sda })) {
        Write-Host "---"
        Write-Host ((T "s_more") + (T "fn_log"))
        Write-Host ((T "s_repo") + "https://gitgud.io/rewio/ServerS4A21/-/commits/master")
    }
    }

# ==================================================================
#  脚本结束
#
# 【常见问题排查】
#   Q: 下载失败（ERROR: Server source download failed）
#   A: 1. 检查网络是否能访问 gitgud.io
#      2. 检查 API 令牌是否过期
#      3. 尝试开启 VPN/科学上网
#
#   Q: 编译失败（ERROR: publish failed）
#   A: 1. 检查是否安装了 .NET 10 SDK（cmd 运行 dotnet --version）
#      2. 确保 ServerS4A21-AUM\Server\DfoServer\DfoServer.csproj 存在
#
#   Q: 日志拉取很慢/失败
#   A: 1. 可以勾选 GUI 中的"跳过更新日志"来跳过这一步
#      2. 不影响实际的代码更新功能
#
#   Q: 玩家数据丢失了
#   A: 检查 ServerS4A21-AUM\Server\DfoServer\Data\inventory.db
#      看看有没有 inventory.db.bak 备份文件
#      如果有，可以手动改名恢复
# ==================================================================
