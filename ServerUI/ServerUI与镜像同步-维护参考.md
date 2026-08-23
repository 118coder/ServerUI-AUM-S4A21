# ServerUI / 镜像同步 维护参考文档

> 适用版本：S4A21 · AUM 管理器（ServerUI）v2.034
> 用途：作为**下一次更新 / 排障 / 换令牌**时的唯一参考。
> 最后核验：2026-08-24（下载地址已多次实测）

---

## 一、仓库地址与分支约定（**务必遵守**）

| 用途 | 地址 | 分支 |
|---|---|---|
| S4A21 服务端主源 | `https://gitgud.io/rewio/ServerS4A21` | **`master`** |
| S4A21 GM 主源 | `https://gitgud.io/rewio/S4A21GmTool` | **`master`** |
| S4A12 服务端主源（旧，仅开发者脚本保留） | `https://gitgud.io/rewio/86JP` | `main` |
| S4A12 GM 主源（旧，仅开发者脚本保留） | `https://gitgud.io/rewio/86JPGMTool` | `main` |
| AUM 自更新仓库（GitHub） | `https://github.com/118coder/ServerUI-AUM-S4A21` | `main`（GitHub 默认） |
| 镜像仓库（**保留 S4A12 名，不删**） | GitHub / Codeberg：`118coder/ServerS4A12.86JP`<br>Gitee：`c118oder/ServerS4A12.86JP` | `main` |

**关键警告（已踩坑）**：
- S4A21 的 gitgud 仓库默认分支是 `master`，不是 `main`。
- 对 `ServerS4A21` 用 `ref_name=main` 提交记录会返回 **200 但只有 2 字节（空数组 `[]`）**，更新日志会"看起来正常但拉不到内容"；archive `?sha=main` 则直接 404。
- 因此 S4A21 相关 URL 一律用 **`master`**：`repository/archive.zip?sha=master`、`repository/commits?ref_name=master`、提交页 `/-/commits/master`。
- S4A12（86JP）保持 `main`；GitHub 的 AUM 自更新仓库（GitHub 默认 `main`）保持 `main`。

---

## 二、镜像上传 / 下载体系

### 2.1 镜像仓库（不变）
- 三个镜像平台写**同一个仓库名** `ServerS4A12.86JP`（Gitee 前缀为 `c118oder`）。
- 仓库地址**永不改名**，只改**上传文件的前缀**。

### 2.2 上传文件名（S4A21 前缀）
| 项目 | 文件名 | 说明 |
|---|---|---|
| S4A21 服务端 latest | `mirrors/ServerS4A21-latest.zip` | |
| S4A21 服务端版本包 | `mirrors/ServerS4A21-<yyyyMMdd>-<HHmm>.zip` | 带时间戳 |
| S4A21 GM latest | `mirrors/ServerS4A21-GMTool-latest.zip` | |
| S4A21 GM 版本包 | `mirrors/ServerS4A21-GMTool-<yyyyMMdd>-<HHmm>.zip` | |
| S4A12 服务端 latest | `mirrors/ServerS4A12-latest.zip` | 开发者脚本保留 |
| S4A12 GM latest | `mirrors/DfoGmTool-latest.zip` | 开发者脚本保留 |
| 元数据 | `latest.json` | 描述 **S4A21** 最新包（package/sha256/size_bytes） |
| 更新日志 | `mirrors/更新日志.txt`（URL 编码 `%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt`） | 由 gitgud **ServerS4A21** 提交记录生成 |

清理规则：按前缀各自保留最近 **2** 个（`ServerS4A12-`、`ServerS4A21-`、`DfoGmTool-`、`ServerS4A21-GMTool-`），GitHub 额外清理 Release 与 tag（`ServerS4A12-*` / `ServerS4A21-*`，各保留 2）。

### 2.3 下载链路（update.ps1 / ServerUI 内置）
1. 主源：gitgud `ServerS4A21`（服务端包）、`S4A21GmTool`（GM 包）。
2. GitGud 不可达时→镜像链：
   - 服务端：Gitee → GitHub → Codeberg 镜像的 `mirrors/ServerS4A21-latest.zip`
   - GM：Gitee → GitHub → Codeberg 镜像的 `mirrors/ServerS4A21-GMTool-latest.zip`
   - **注意**：GM 镜像不再使用 GitHub 的 AUM 仓库 `dfogmtool.zip`（已移除）。
3. 全部失败→本地缓存 `AUM管理组件\latest\`。

---

## 三、目录结构（关键）

```
游戏根 E:\Game\S4A21_CN\
├── DNF.exe
├── ServerUI-有依赖版.exe      ← 需系统 .NET 10 运行时
├── ServerUI-无依赖版.exe      ← 自包含单文件（免运行）
├── ServerUI-兼容模式.exe      ← net48，Win7 可用（含 .exe.config）
└── AUM管理组件\
    ├── ServerUI\                  # 管理器 C# 源码
    │   ├── ServerUI.csproj        # net10.0-windows 主版本
    │   └── ServerUI-Win7.csproj   # net48（共享同一份源码）
    ├── ps1核心\
    │   ├── update.ps1             # 增量/全量更新（编译+拉日志）
    │   ├── 更新AUM.ps1            # 管理器自更新
    │   ├── 进行本地编译.ps1       # 离线本地编译
    │   ├── gmtool.ps1             # GM 工具独立启动
    │   ├── save-switch.ps1 / save-quick.ps1 / dnf_monitor.ps1 / get_pid.ps1
    │   └── dotnet-install.ps1     # 微软官方 SDK 安装脚本
    ├── latest\                    # 本地缓存
    │   ├── ServerS4A21-latest.zip
    │   └── ServerS4A21-GMTool-latest.zip
    ├── ServerS4A21-AUM\           # 服务端源码/运行
    │   └── dist\win-x64\DfoServer.exe   # 编译产物（运行）
    ├── dfogmtool\                 # GM 工具源码；publish\DfoGmTool.exe
    ├── 开发者镜像上传\             # 开发专用上传脚本（S4A12+S4A21）
    ├── dotnet-sdk\  DX11补丁\  DX12补丁\  存档管理\  实用工具包\
    └── *.bat（开始更新/全量更新/停止服务/启动本地游戏/快速换挡/存档管理/更新AUM/管理器/进行本地编译/GM工具）
```

**运行/数据路径约定**（代码中全部引用，不要动）：
- 服务端运行目录：`AUM管理组件\ServerS4A21-AUM\dist\win-x64`
- 数据库：`dist\win-x64\Data\inventory.db`；PVF：`dist\win-x64\Data\Pvf\Script.pvf`
- GM：`AUM管理组件\dfogmtool\publish\DfoGmTool.exe`，端口 `http://localhost:5050`，环境变量 `DFO_GM_SERVER_BIN`（GM 定位服务端数据用）

---

## 四、编译命令与部署

### 4.1 服务端 / GM（update.ps1 内部执行）
```
服务端：dotnet publish ServerS4A21-AUM\Server\DfoServer\DfoServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\win-x64
GM：    dotnet publish dfogmtool\DfoGmTool.csproj -c Release -r win-x64 --self-contained true -o dfogmtool\publish
```
（新 S4A21 仓库自带 `publish.bat`，与上面命令一致；数据目录结构不变）

### 4.2 ServerUI 管理器三版本（SDK：系统 `C:\Program Files\dotnet\dotnet.exe`）
```
有依赖版：dotnet publish ServerUI\ServerUI.csproj -c Release -r win-x64 --no-self-contained -o <out>          → ServerUI.exe → ServerUI-有依赖版.exe
无依赖版：dotnet publish ServerUI\ServerUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o <out> → ServerUI.exe → ServerUI-无依赖版.exe
兼容模式：dotnet publish ServerUI\ServerUI-Win7.csproj -c Release -o <out>  → ServerUI-Win7.exe(+.config) → ServerUI-兼容模式.exe(+.exe.config)
```
- 无依赖版体积约 **53,109,540 B**；有依赖版约 **3,865,333 B**；兼容模式约 **1,922,560 B**。
- 部署位置**两处都要**：游戏根（与 DNF.exe 同级）+ `AUM管理组件\`（`管理器.bat` 在此查找三件套）。
- csproj 体积优化：`EnableCompressionInSingleFile`（自包含时自动压缩，114MB→50.6MB）。WinForms 不支持 Native AOT（NETSDK1175），不要尝试。
- 已知警告（可忽略、勿删）：`MainForm.cs(353) warning CS0162 不可达代码`（UseComposited 常量）；兼容模式有 Fody PrivateAssets 提示。

---

## 五、令牌（双重 base64 加密，按约定）

> 存储方式：`Convert.ToBase64String(UTF8(Convert.ToBase64String(UTF8(明文))))`（双重 base64）。\n> 令牌属敏感信息：只修改本地脚本，**不得提交到任何仓库/上传到镜像**。

| 用途 | 双重 base64（当前值） | 存放位置 |
|---|---|---|
| GitGud | `WjJkcGIxOUZkbUpmUmtScFpqRnNWVlJXUVZGcmR6QjZTMWRIT0RaTlVYQXhUMnBLYWxvelowc3VNREV1TVRBeFozVXhhMnBq` | 各脚本通用 |
| **GitHub**（新） | `WjJod1gyOUdVVVJHZFc1dFEwSkVaRzQzTVZObVVWRm5NWFUzYzJObVRVZzNaakZzUmtOa1FnPT0=` | ① `开发者镜像上传\开发者镜像上传.ps1` ② `ServerUI\Services\MirrorUploadService.cs`（6 处） ③ `ServerUI\Services\PlatformAdapters\GitHubAdapter.cs` ④ `ps1核心\update.ps1`（2 处） |
| Gitee | `WlRsbVpXWmlPRE0zWWpsaU5UVTBaamRpTVdaak4yRXdZbVprTlRKaFpUaz0=` | `update.ps1` / `GiteeAdapter.cs` / 开发者脚本 |
| Codeberg | `WlRKa09HVmpOR1E1TW1Zek5UUmpZVFZrT0dOa1kyTTFaVFUyWmpNek1EVTNaRGRpTVRVM01RPT0=` | `CodebergAdapter.cs` / 开发者脚本 |

**换令牌操作手册**：先 `b64(b64(新令牌))`；然后在上面 4 个文件里把旧双重 base64 全局替换为新值；再重编译 `ServerUI` 并部署两处。

---

## 六、本次 S4A12→S4A21 迁移记录（已完成）

1. **全量替换**：目录 `ServerS4A12-AUM → ServerS4A21-AUM`；窗口标题/文案/帮助/链接全部 S4A21 化（`ServerUI\*.cs`、README、打不开排查指南.txt、bat 标题）。
2. **上传文件名**：`ServerS4A12-latest.zip → ServerS4A21-latest.zip`；`DfoGmTool-latest.zip → ServerS4A21-GMTool-latest.zip`（update.ps1、MirrorUploadService.cs、进行本地编译.ps1、开发者脚本、latest\ 缓存）。
3. **镜像仓库名保持** `ServerS4A12.86JP`（只改文件前缀）。
4. **主源只保留 gitgud**：服务端 `rewio/ServerS4A21`、GM `rewio/S4A21GmTool`；移除"GitHub AUM 仓库"作为 GM 下载源（`dfogmtool.zip`），GM 镜像改用 GitHub 镜像仓库。
5. **分支修复（重要）**：S4A21 全部 `main → master`（`update.ps1` 的 archive sha/commits ref_name/提交页、`MirrorUploadService.cs`、开发者脚本）。
6. **更新日志数据源**改为 gitgud `ServerS4A21`（删除旧 `.update-cache\commits.json`，避免混入 S4A12 提交）。
7. **修复 bug**：`save-quick.ps1` / `save-switch.ps1` 引用 `停止服务端.bat` 不存在 → 改为实际的 `停止服务.bat`。
8. **开发者镜像上传.ps1**：保留 S4A12 全流程，新增 S4A21 上传 + 清理（Release/文件/tag 各保留 2）。
9. **GitHub 令牌已更新**（旧令牌 401 → 新令牌实测 200）。

---

## 七、实测验证结果（2026-08-24）

| 测试项 | 结果 |
|---|---|
| S4A21 服务端 archive `?sha=master` | ✅ 200 ZIP（2,354,640 B） |
| S4A21 服务端 `ref_name=master` 提交 | ✅ 200（正常 JSON） |
| S4A21 提交页 `/-/commits/master` | ✅ 200 |
| S4A21 GM archive `?sha=master` | ✅ 200 ZIP（858,046 B） |
| S4A12 86JP / 86JPGMTool archive+commits（`main`） | ✅ 200 |
| S4A21 用 `main` | ❌ archive 404；commits 200 但空数组（2B） |
| Gitee 令牌 | ✅ 200 |
| Codeberg 令牌 | ✅ 200 |
| GitHub 令牌（新） | ✅ 200（repo/contents/releases） |

---

## 八、下次更新 / 排障清单

1. **改分支**：若上游 S4A21 仓库默认分支变化，同步修改三处 `sha=` / `ref_name=` / `/-/commits/<分支>`：`ps1核心\update.ps1`、`ServerUI\Services\MirrorUploadService.cs`、`开发者镜像上传\开发者镜像上传.ps1`。
2. **换令牌**：见第五节。
3. **改仓库地址**：gitgud 服务端/GM 名、镜像仓库名、`latest.json`、更新日志 URL，集中在上述 3 个文件 + `MainForm.Pages.cs` 的仓库链接页。
4. **改上传文件名前缀**：S4A21 系列保留 `ServerS4A21-*` / `ServerS4A21-GMTool-*`；S4A12 系列 `ServerS4A12-*` / `DfoGmTool-*`（开发者脚本）。
5. **重编译部署**：三版本命令 + 部署游戏根与 AUM 根**两处**。
6. **ps1 编码**：修改后务必保留 BOM（`EF BB BF`），否则 PowerShell 5.1 -File 中文乱码会写坏文件名/路径。用脚本文件跑任务前先加 BOM。
7. **更新日志为空**时优先检查分支是否用了 `main`（见第一节警告）。
8. **上传 401** = 对应平台令牌失效（本次 GitHub 已实测并更换）。