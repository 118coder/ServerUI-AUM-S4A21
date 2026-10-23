# ServerUI 维护参考（AUM 管理器 / 镜像同步 / 源码打包）

> 适用版本：S4A21 · AUM 管理器（ServerUI）**v2.1**
> 定位：以**维护规则**为核心——当前配置、集中点、修改点、排障。
> 最后核验：2026-08-24（下载地址已多次实测）

---

## 一、维护规则总览（先看这里）

| 类别 | 当前配置 | 集中点（改这里，大多已单点化） |
|---|---|---|
| 服务端主源 | gitgud `rewio/ServerS4A21`（分支 **master**） | update.ps1 `$RepoApi`（L67）；MirrorUploadService.cs 常量 `GitGudZip` |
| GM 主源 | gitgud `rewio/S4A21GmTool`（分支 **master**） | update.ps1 GM 段；MirrorUploadService.cs `MirrorGMTool` 段 |
| AUM 自更新仓库 | GitHub `118coder/ServerUI-AUM-S4A21`（main） | 更新AUM.ps1 顶部 URL；SelfUpdateService.cs `VerFile` |
| 镜像仓库名 | GitHub/Codeberg `118coder/ServerS4A12.86JP`；Gitee `c118oder/ServerS4A12.86JP` | **三脚本顶部变量已集中**：update.ps1 `$MirrorGiteeRaw/$MirrorGitHubRaw/$MirrorCodebergRaw`；开发者镜像上传.ps1 `$GitHubRepo/$CodebergRepo/$GiteeRepo`；MirrorUploadService.cs 常量 `GitHubRepo` |
| 分支（S4A21） | 全链 **master**（S4A12 保持 main） | ⚠️ **尚未单点化**：update.ps1 内 5 处字面量（见二节明细）+ MirrorUploadService.cs 常量 + 开发者脚本变量 |
| 文件名前缀 | S4A21：`ServerS4A21-*` / `ServerS4A21-GMTool-*`；S4A12（仅开发者脚本）：`ServerS4A12-*` / `DfoGmTool-*` | update.ps1 镜像 URL 数组（L114-124）集中；MirrorUploadService.cs 与开发者脚本内字面量 |
| 令牌 | 双重 base64 加密，4 平台 | update.ps1（GitGud+Gitee）、MirrorUploadService.cs、PlatformAdapters×4、开发者上传.ps1（集中变量） |
| 更新日志 | S4A21 下载端数据源 = gitgud ServerS4A21 提交（master）→ 本地缓存 → 镜像 `mirrors/S4A21更新日志.txt`（Gitee API → GitHub API → Codeberg raw）。`mirrors/更新日志.txt` = S4A12 仓库（86JP）提交，供旧版 S4A12 工具，与 S4A21 日志互不覆盖（开发者脚本双名双源上传） | update.ps1 [5/5] 提交拉取段 + `Download-FromGitee/GitHub`；SelfUpdateService `DownloadChangelogFromMirror`；MirrorUploadService `UploadChangelog` |

**一句话维护规则**：换仓库/换镜像名/换前缀 → 优先改各脚本顶部变量与 C# 常量（已集中）；换分支 → 按二节修改点逐处替换（未集中）；换令牌 → 五节手册（4 处文件）。

---

## 二、仓库地址与分支约定（当前配置 + 修改点）

### 2.1 当前配置

| 用途 | 地址 | 分支 |
|---|---|---|
| S4A21 服务端主源 | `https://gitgud.io/rewio/ServerS4A21` | **`master`** |
| S4A21 GM 主源 | `https://gitgud.io/rewio/S4A21GmTool` | **`master`** |
| S4A12 服务端主源（旧，仅开发者脚本保留） | `https://gitgud.io/rewio/86JP` | `main` |
| S4A12 GM 主源（旧，仅开发者脚本保留） | `https://gitgud.io/rewio/86JPGMTool` | `main` |
| AUM 自更新仓库（GitHub） | `https://github.com/118coder/ServerUI-AUM-S4A21` | `main`（GitHub 默认） |
| 镜像仓库（保留 S4A12 名） | GitHub/Codeberg：`118coder/ServerS4A12.86JP`；Gitee：`c118oder/ServerS4A12.86JP` | `main` |

### 2.2 分支字面量分布（改分支时的完整修改点）

当前 S4A21 的 `master` 是**直接写字面量**的，改分支要逐处替换：

**update.ps1（5 处）**：
1. L567 `commits?ref_name=master…`（提交历史拉取）
2. L1120 `archive.zip?sha=master`（服务端主下载）
3. L1355 `archive.zip?sha=master`（GM 主下载）
4. L2108 `commits?ref_name=master…`（更新日志拉取）
5. L2267 `/-/commits/master`（日志尾部提交页链接，纯展示）

**MirrorUploadService.cs**：常量 `GitGudZip`（含 `sha=master`）、`MirrorGMTool` 段、commits 拉取 URL（`ref_name=master`）。

**开发者镜像上传.ps1**：`$GitGudZip21` / `$GitGudGMToolZip21`（含 `sha=master`）。

> 已踩坑警告：S4A21 用 `main` 时——archive 直接 404；commits 返回 200 但只有 2 字节（空数组 `[]`），更新日志"看起来正常却拉不到内容"。

### 2.3 UI 展示链接（MainForm.Pages.cs「仓库与链接」卡片）

3 行 LinkRow（L794-809）：GitHub AUM 仓库 / gitgud ServerS4A21 / gitgud S4A21GmTool。仓库改名时同步。

---

## 三、镜像上传 / 下载体系（当前配置）

### 3.1 镜像仓库（不变）
- 三个镜像平台写**同一个仓库名** `ServerS4A12.86JP`（Gitee 前缀 `c118oder`）。
- 仓库名**永不改名**；只改**上传文件的前缀**。

### 3.2 上传文件名
| 项目 | 文件名 | 说明 |
|---|---|---|
| S4A21 服务端 latest | `mirrors/ServerS4A21-latest.zip` | |
| S4A21 服务端版本包 | `mirrors/ServerS4A21-<yyyyMMdd>-<HHmm>.zip` | 带时间戳 |
| S4A21 GM latest | `mirrors/ServerS4A21-GMTool-latest.zip` | |
| S4A21 GM 版本包 | `mirrors/ServerS4A21-GMTool-<yyyyMMdd>-<HHmm>.zip` | |
| S4A12 服务端 latest | `mirrors/ServerS4A12-latest.zip` | 开发者脚本保留 |
| S4A12 GM latest | `mirrors/DfoGmTool-latest.zip` | 开发者脚本保留 |
| 元数据 | `latest.json` | 双系列兼容结构：顶层 = 当前 S4A21（package/sha256/size_bytes/download_*），并附 `s4a21` / `s4a12` 两个完整区块；S4A12 哈希由本地 `latest\ServerS4A12-latest.zip` 推算并入 |
| 更新日志（S4A21 专用） | `mirrors/S4A21更新日志.txt`（URL 编码 `S4A21%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt`） | 由 gitgud ServerS4A21 提交记录生成 |
| 更新日志（S4A12） | `mirrors/更新日志.txt`（URL 编码 `%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt`） | 数据源 = gitgud 86JP 提交记录（S4A12 仓库，开发者脚本 [6b/8] 上传，v2.15 起与 S4A21 日志双名双源） |

清理规则：按前缀各自保留最近 **2** 个；GitHub 额外清理 Release 与 tag（各保留 2）。

### 3.3 下载链路（update.ps1 / ServerUI 内置）
1. 主源：gitgud `ServerS4A21` / `S4A21GmTool`。
2. GitGud 不可达 → 镜像链：服务端与 GM 均为 **Gitee → GitHub → Codeberg** 的 `mirrors/…-latest.zip`（Gitee 直链附加 `access_token`）。GM 镜像**不再**使用 GitHub AUM 仓库的 `dfogmtool.zip`（已移除）。
3. 全部失败 → 本地缓存 `AUM管理组件\latest\`。

---

## 四、编译命令与部署

### 4.1 服务端 / GM（update.ps1 内部执行）
```
服务端：dotnet publish ServerS4A21-AUM\Server\DfoServer\DfoServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\win-x64
GM：    dotnet publish dfogmtool\DfoGmTool.csproj -c Release -r win-x64 --self-contained true -o dfogmtool\publish
```
（新 S4A21 仓库自带 `publish.bat`，与上面命令一致）

### 4.2 ServerUI 管理器三版本（SDK：系统 `C:\Program Files\dotnet\dotnet.exe`）
```
有依赖版：dotnet publish ServerUI\ServerUI.csproj -c Release -r win-x64 --no-self-contained -o <out>          → ServerUI.exe → ServerUI-有依赖版.exe
无依赖版：dotnet publish ServerUI\ServerUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o <out> → ServerUI.exe → ServerUI-无依赖版.exe
兼容模式：dotnet publish ServerUI\ServerUI-Win7.csproj -c Release -o <out>  → ServerUI-Win7.exe(+.config) → ServerUI-兼容模式.exe(+.exe.config)
```
- 双框架共享同一份源码与 `obj/`，**必须顺序编译**（先主版本成功后再编 Win7，并行会 NETSDK1047）。
- 无依赖版约 53,109,540 B；有依赖版约 3,865,333 B；兼容模式约 1,922,560 B。csproj 用 `EnableCompressionInSingleFile`（自包含时自动压缩 114MB→50.6MB）；WinForms 不支持 Native AOT（NETSDK1175），勿尝试。
- 部署两处都要：游戏根（与 DNF.exe 同级）+ `AUM管理组件\`（`管理器.bat` 在此查找三件套）。
- 版本号 v2.1 定义处：`MainForm.cs` 的 `VER` 常量（NET48 → "2.1-V"）、三个 csproj 的 Version/AssemblyVersion/FileVersion/InformationalVersion、`AUM-version.txt`。
- 已知警告（可忽略、勿删）：`MainForm.cs` 的 `warning CS0162 不可达代码`（行号随代码增长变化，认 CS0162 即可，来源是 `UseComposited=false` 常量）；兼容模式 Fody PrivateAssets 提示。

---

## 五、令牌规则（双重 base64）

> 存储：`Convert.ToBase64String(UTF8(Convert.ToBase64String(UTF8(明文))))`（双重 base64）。
> 属性：**敏感信息，只改本地脚本，不得提交任何仓库/上传镜像。**

| 用途 | 双重 base64（当前值） | 存放位置 |
|---|---|---|
| GitGud | `WjJkcGIxOUZkbUpmUmtScFpqRnNWVlJXUVZGcmR6QjZTMWRIT0RaTlVYQXhUMnBLYWxvelowc3VNREV1TVRBeFozVXhhMnBq` | 各脚本通用 |
| GitHub（新） | `WjJod1gyOUdVVVJHZFc1dFEwSkVaRzQzTVZObVVWRm5NWFUzYzJObVRVZzNaakZzUmtOa1FnPT0=` | ① `开发者镜像上传\开发者镜像上传.ps1` ② `ServerUI\Services\MirrorUploadService.cs`（6 处） ③ `PlatformAdapters\GitHubAdapter.cs` ④ `ps1核心\update.ps1`（2 处） |
| Gitee | `WlRsbVpXWmlPRE0zWWpsaU5UVTBaamRpTVdaak4yRXdZbVprTlRKaFpUaz0=` | `update.ps1` / `GiteeAdapter.cs` / 开发者脚本 |
| Codeberg | `WlRKa09HVmpOR1E1TW1Zek5UUmpZVFZrT0dOa1kyTTFaVFUyWmpNek1EVTNaRGRpTVRVM01RPT0=` | `CodebergAdapter.cs` / 开发者脚本 |

**换令牌操作手册**：先 `b64(b64(新令牌))`；再在**上述 4 类文件**里把旧双重 base64 全局替换为新值；最后重编译 `ServerUI` 并部署两处。

---

## 六、源码组织与编码规则

### 6.1 源码文件（页面拆分，各页独立维护）
| 文件 | 内容 |
|---|---|
| `MainForm.cs` | 主窗体框架、VER 版本号、菜单导航、迷你模式切换 |
| `MainForm.Pages.cs` | 开始页 / 存档管理页 / 设置与关于页（含仓库链接卡片）/ 使用说明页 / 日志条 |
| `MainForm.DllExt.cs` | **DLL 扩展页全部逻辑** + 弹窗类 `DllPickForm` / `IniEditorForm` / `IniAskForm` |
| `MainForm.Time.cs` | 调整系统时间页（`TimeTool.cs` 为底层工具类） |
| `MainForm.Archive.cs` | 存档管理业务逻辑 |
| `MainForm.Server.cs` | 服务端启停 / 日志输出 / 更新流程 / DNF 检测 |
| `MainForm.Troubleshoot.cs` | 疑难杂症页（FAQ 条目字典） |
| `ClassicForm.cs` / `MiniForm.cs` / `Compat.cs` | 双缓冲旧窗体 / 迷你模式 / net48 兼容适配 |
| `Services\` | ArchiveService / ServerService / UpdateService / SelfUpdateService / MirrorUploadService / PlatformAdapters（4 平台） |
| `..\ServerUI.Headless\` | **无头自测工程**（复用本目录全部源码，不带管理员清单、不显示窗口、不联网）；见九节第 10 条 |

### 6.2 ps1 编码规则（强制，已踩坑）
- **所有 ps1 一律 UTF-8 带 BOM**（`EF BB BF`）：gmtool / save-quick / save-switch / 更新AUM / 进行本地编译 / update.ps1 均如此。
- 原因：Windows PowerShell 5.1 对**无 BOM** 文件按 ANSI(GBK) 解码，中文注释字节被误读成引号/括号等语法字符 → 整串假解析错误、更新流程不执行（update.ps1 已发生，见 7.4）。
- 编辑任何 ps1 后必须：① 查 BOM（前 3 字节 `EF BB BF`）；② 用 **PowerShell 5.1 引擎** `Parser.ParseFile` 验证 0 错误（不能用 pwsh7 单独验证）。
- 纯 ASCII 小脚本（dnf_monitor / get_pid）可无 BOM；`dotnet-install.ps1`（微软官方）保持原样。

### 6.3 文档与代码同步规则
- 功能行为变化后同步三处：「使用说明」页条目（内置 UI）+《更新记录与安装目录结构.md》（交付视角）+ 本文件（维护视角）。
- DLL 扩展受管插件清单唯一维护点：`MainForm.DllExt.cs` 的 `DllPlugins` 常量（7 项元组：File/Name/Desc/Config）。

---

## 七、版本变更历史

### 7.1 S4A12 → S4A21 迁移要点（已完成）
1. 目录 `ServerS4A12-AUM → ServerS4A21-AUM`；标题/文案/帮助/链接全部 S4A21 化。
2. 上传文件名 `ServerS4A12-latest.zip → ServerS4A21-latest.zip`；`DfoGmTool-latest.zip → ServerS4A21-GMTool-latest.zip`。
3. 镜像仓库名保持 `ServerS4A12.86JP`（只改文件前缀）。
4. 主源只保留 gitgud（`rewio/ServerS4A21`、`rewio/S4A21GmTool`）；移除 GitHub AUM 仓库的 GM 下载源。
5. 分支修复：S4A21 全部 `main → master`。
6. 更新日志数据源改为 gitgud `ServerS4A21`（删旧 `.update-cache\commits.json`）。
7. 修 bug：`save-quick/save-switch.ps1` 的 `停止服务端.bat` → `停止服务.bat`。
8. 开发者镜像上传.ps1：新增 S4A21 上传 + 清理，S4A12 流程保留。
9. GitHub 令牌已更新（401 → 200）。

### 7.2 v2.034（S4A21 基线）
- 【修复赛丽亚房间问题】→【待定】、【安装新安全DLL】→【待定2】（功能暂时移除，按钮与底层方法保留）。
- GM 工具端口 5051（避 A12 的 5050），`MainForm.Pages.cs` 与 `gmtool.ps1` 同步。
- `latest.json` 双系列兼容 + S4A12 哈希补录；update.ps1 更新前残留 PS1 检测。

### 7.3 v2.035（仅 S4A12 老包，不影响 S4A21）
- S4A12 老 AUM 一次性善后（版本 2.035、`Get-MirrorMeta` 读 `s4a12` 区块、启动提示停止维护、高帧率闪退排查条目）。之后 S4A12 冻结。

### 7.4 v2.1（本次发布，S4A21 主版本）
**版本号**：2.034 → 2.1（`MainForm.cs` VER：主 "2.1" / Win7 "2.1-V"；三个 csproj 与 `AUM-version.txt` 同步）。

**DLL 扩展页重构**（对齐启动器同源机制 `GameGaurd.ini [Plugins]`）：
- 「应用更改」= **只直写** `GameGaurd.ini`：`BuildPatchIni` 保留注释/非受管条目/自定义扩展，受管条目按勾选重新编号；GameNative 必选锁定校验保留；不再解压 zip。
- 补丁文件安装改由【设置与关于】页**「新DLL安装」**（原「待定2」按钮）→ `InstallPatchZip()`：解压 `实用工具包\DLL覆盖\客户端补丁.zip` → 复制到游戏根（已存在配置 ini 不覆盖）→ 合并 ini（受管全启用）→ `_patchInstalled=true` + `RefreshDllState()`。
- 未安装空态：`_patchInstalled` 判定 = 存在 GameGaurd.dll/az.dll 或 ini 有 PluginN= 记录；未安装时不渲染插件列表、开关全灰、添加/删除扩展拦截。
- 扩展条目支持 **DLL / INI**（三处过滤放开，`*.dll;*.ini`）。
- INI 编辑器（`IniEditorForm`）分组管理：添加分组/添加键/删除键/删除分组；标题显示「（已修改，保存后写入）」；保存保留原编码。

**源码拆分**：新增 `MainForm.DllExt.cs` / `MainForm.Time.cs`；`MainForm.Pages.cs` 3486 → 约 1482 行。

**update.ps1 更新日志修复**：commits API 偶发 200 空数组 → 保留缓存；镜像日志从整文件覆盖改为解析并入；版本头恒为本次日期。

**update.ps1 BOM 事故修复（重要教训）**：编辑中 BOM 丢失 → 5.1 按 ANSI 读中文注释 → 整串解析错误、更新流程不执行；已写回 BOM（111938→111941 字节）并用 5.1 `ParseFile` 验证 0 错误。**今后所有 ps1 编辑遵守 6.2。**

**其它**：疑难解答页正文更新；文档整合为《UI规范》《ServerUI-维护参考》《更新记录与安装目录结构》三份。

---

### 7.5 v2.15（本地修复版）

**更新日志链路大修**（"镜像日志拉不到 / 一直是旧内容"根因修复）：
- update.ps1 新增 `Test-DownloadOk`：ZIP 完整性校验只对 .zip 生效，.txt 按存在+非空判定（旧逻辑对 .txt 跑 ZIP 校验恒失败 → Gitee/GitHub 两级镜像回退形同虚设，只剩 Codeberg 可用）。
- update.ps1 镜像日志回退统一改拉 `mirrors/S4A21更新日志.txt`；镜像提交并入后**写回本地提交缓存**（gitgud 持续断网时历史也能累积）；主源+缓存+镜像全空且本地已有日志时**保留旧日志不覆盖**；第1页/兜底重试 10 次 → 3 次（断网不再卡约 2 分钟）；同日期提交消息去重。
- SelfUpdateService `DownloadChangelogFromMirror` 重写：Gitee API（带令牌，国内直连优先）→ GitHub API（带令牌）→ Codeberg raw。旧实现裸 raw 直链对私有仓库必 404/403。

**其他修复**：ServerService `Stop()` 改为等待 DfoServer 进程本体退出（旧逻辑 bat 句柄为 null 时 0 秒强杀存盘中的服务端 → 回档）；Rs() 检测到外部启动的服务端只提示不清理（不再误杀手动开服）；增量/全量更新加 `_updateBusy` 防重入；OU/OD 回调 UI 访问全部封送（BeginInvokeUi）；同步网络时间移后台线程（断网不再整窗假死）+ 防重入；存档导入/导出/储存纳入 DoArchiveOp 防护、导入前自动备份、极简模式补 `TB()` 备份修剪；GameGaurd.ini 读写改为编码探测回退（GBK 文件不再被转码损坏，写回保持原编码）；新DLL安装失败清单如实警告；镜像上传前等待令牌校验完成；AUM 自更新 Completed 只订阅一次。

**GameGaurd.ini 写入顺序 + 拖拽排序（v2.16）**：
- 修复：应用更改/添加扩展会把自定义 DLL 排到 [Plugins] 最前，基础挂载器 GameNative 被排到后面导致部分插件加载失败。现在写入顺序 = GameNative（强制 Plugin0）→ 受管插件（界面顺序）→ 自定义扩展（界面顺序）。
- 必选 DLL 置顶：DllPlugins 数组中 GameNative.dll 移到首位（UI 首行 + 写入顺序首位双重保证）。
- 拖拽排序（**实时换位**，v2.16 实现四版；二版"又卡又慢"、三版"拖拽发起区不明显"先后返工）：**拖拽只从行首 ⠿ 手柄发起**——表格新增第一列（30F），`AddDragHandle` 创建无边框 `AntdUI.Button`（`IconSvg="HolderOutlined"` 六点阵图标、`BorderWidth=0`、`WaveSize=0`、`Cursor=SizeAll`、`Type=Default` 次要色）；必选 GameNative 行**无手柄**=固定首位，名称/说明文字不响应拖拽。跨过其他行时**立即换位**，松开定稿；受管/自定义两组互不拖动。仍采用 **AntdUI TabHeader.DragSort 同款手动 HitTest 状态机**，**不走 OLE DoDragDrop**。事件挂手柄自身：按下即持有**隐式 Capture**，Move/Up 全路由到手柄（坐标 `PointToClient` 换算到 tbl 系）——不挂容器、不显式转 Capture，AntdUI 按钮按压/悬停状态机自然复位，无"拖完卡在按下态"问题。二版教训（勿回退）：①每次跨越调用 `RebuildDllRows` 整表重建——销毁+新建几十个控件、分配 Font、逐控件强制同步重绘，单次跨越即肉眼可见卡顿；②换位用"RemoveAt+Insert"且 `ti>si` 时 `ti--`，**向下拖一行是无操作** → 指针停在目标行内时每个 MouseMove 都重建一次（重建风暴）。三版：**数据改交换语义**（`si↔ti` 元组互换，与视觉严格一致，换位后被拖行正好落在光标行、后续 Move 命中自身即返回）；**视觉改原位交换**（新函数 `SwapDllRowsVisual`：按名称标签定位两行 → 逐列 `GetControlFromPosition`/`SetCellPosition` 整行互换单元格 → `ResumeLayout(true)` 单次布局），拖拽全程**零控件销毁/新建**，每次跨越仅一次轻量布局；MouseUp **不再重建**（重建会把"已取消勾选、尚未应用"的自定义开关悄悄重置回勾选），高亮恢复改为记录原色 `_dragHl`/`_dragHlPrev`（AntdUI ForeColor 为可空 Color?）；`RebuildDllRows` 重建自定义开关时**沿用重建前勾选状态**（`prevCustOn` 快照，新添加文件默认勾选）。顺序状态由 `_managedOrder`/`_custRows` 维护，首次从 GameGaurd.ini 实际顺序初始化，应用更改后写回 ini → 重启后顺序保持。
- 相关状态：`_orderDirty`（拖拽后、应用前刷新时不按 ini 旧顺序覆盖列表）、`ApplyDllPatch`/`InstallPatchZip` 成功后清除。

**页面切换动效尝试与回退（v2.16）**：
- 曾为 `ShowPage` 实现方向感知滑动动效并三轮调参（整卡滑入 200ms → 150ms → 短距 48px×`AutoScaleFactor` + 50ms），用户最终拍板**纯硬切**，代码已完整回退为原始逻辑（挂起布局切可见性 + 目标页强制定稿 + 内容区重绘；`_pageAnim*`/`_curPage`/`_curTag`/`PageOrder`/`SwitchPageInstant`/`EndPageAnim` 已全部删净）。
- 结论：用户偏好即点即显，**不要再给页面切换加动画**；如需流畅感，优先做"切换零卡顿"（预布局、双缓冲，均已具备）而非过渡动效。
- 附：此前 WCAG 2.2 检索提示拖拽排序应提供非拖拽替代（如上移/下移按钮），未实现，可作后续可选项。

**DLL 扩展页两项用户反馈修复（v2.15 追加 4）**：
- 4K/高 DPI 下添加自定义 DLL 后"排版缩小"：根因是 `RebuildDllRows` 的 DPI 补偿只放大字体与自定义开关、不放大行高列宽 → 运行期重建时"字变大、行高不变"，内容被行高裁切。现在缩放因子改为从**已被窗体 AutoScale 的启动控件实测推导**（`swDlls[0].Height / 22`，自洽于任意 DPI/缩放组合），并对行高、cell3 列宽一并按同一因子缩放。
- GameGaurd.ini 被写成 UTF-8 **带 BOM**：原格式为 UTF-8 无 BOM，带 BOM 会导致挂载器解析异常。现在 `WriteIniText` 无条件 UTF-8 无 BOM（不再"有 BOM 保持 BOM"）；`ReadIniText`/`ReadConfigText` 读取时剥离 BOM（旧实现把 BOM 当普通字符读入又写回，BOM 永远删不掉），兼容 GBK 历史文件的读取。

**更新进度条精准化（v2.15 追加 3）**：
- update.ps1 真实里程碑重排（单调递增）：5 源预检前 → 8 源预检完成 → 20 下载完成 → 30 同步开始 → 55 编译开始 → **62 服务端 restore 完成 / 72 服务端 publish 落盘 / 80 GM publish 落盘**（主作用域轮询三个产物文件的 LastWriteTimeUtc，编译输出在 runspace 内缓冲无法直接流式上报）→ 85 日志开始 → 87-95 提交分页 → 完成时 GUI 跳 100%。旧日志段 60-93 与 85 打架的倒退标记已重排。
- GUI（MainForm.Server.cs）：`[N/5]` 映射重校准为 5/10/30/55/85 且只前进；进度算法从"伪进度（蠕到 90/95 + 呼吸）"改为**真实里程碑跟随**——未达目标按 gap*0.16/500ms 平滑收敛、不越过目标；追平后仅 0.03%/tick 阶段内缓推（上限里程碑+4，绝对上限 97），100% 只由完成回调设置。

**CET 与"服务端启动即断联"专项（v2.15 追加 2）**：
- CET 规避链路全覆盖：update.ps1 顶部注入 `$env:DOTNET_EnableCET=0`；ServerService.Start/LaunchGmTool 向子进程注入；开始更新/全量更新/进行本地编译/GM工具/更新AUM/管理器 六个 bat 字节级插入 `set DOTNET_EnableCET=0`（不改动 bat 原编码）。
- 服务端启动即断联对策：Start() 启动前自动清理残留 DfoServer（防端口占用秒退）；记录 StartedAtUtc/UnexpectedExitUtc/LastExitCode，启动后 2 分钟内意外退出时运行日志输出退出代码+四条排查提示（杀毒/端口/CET/exe缺失）；疑难杂症页新增【CET兼容提示】【开始游戏后服务端瞬间断联】两条 FAQ。
- 已知事实：start-server.bat 不做编译，仅启动预编译的 DfoServer.exe —— "编译失败"只影响更新流程，不会导致启动秒退。

**主源/上传者链路深检（v2.15 追加）**：
- `Download-FromGitHub` 重排：优先用 API 返回的 content 解码；大文件无 content 时给 raw 直链补 Authorization 头（私有仓库 raw 匿名必 404，旧逻辑先走无认证的 download_url → GitHub 镜像腿对大/小文件一直失败）。
- update.ps1 把 GitHub 选为主源时改走 API 下载（`$svrFromGitHub`，与 Gitee 同款；旧分支用无认证 raw 直链必 404）。
- 上传者模式上传前用 `Compat.IsValidZip` 做完整性校验（防被网关污染/截断的包连同 SHA 污染三平台 latest.json）；更新日志等待本次更新刷新（最长 6 分钟）后再上传，消除"镜像日志滞后一拍"。

**验证（2026-09-08 实测）**：GitGud 主源 archive.zip（200/2.67MB/CRC 全对）、commits API（正常返回）、GM archive（200）；AST 提取真实函数实测——GitHub 拉更新日志 ✅、Gitee 拉 2.67MB 服务端 zip ✅、GitHub 拉 2.67MB 服务端 zip（raw+认证）✅；net10/net48 双工程编译 0 错误。

**验证**：net10 与 net48 双工程编译 0 错误；update.ps1 保持 BOM、PS 5.1 ParseFile 0 错误；AST 提取真实 `Download-FromGitee` 实测拉取 S4A21更新日志.txt 成功（修复前恒失败）。

---

## 八、实测验证结果（2026-08-24）

| 测试项 | 结果 |
|---|---|
| S4A21 服务端 archive `?sha=master` | ✅ 200 ZIP（2,354,640 B） |
| S4A21 服务端 `ref_name=master` 提交 | ✅ 200（正常 JSON） |
| S4A21 提交页 `/-/commits/master` | ✅ 200 |
| S4A21 GM archive `?sha=master` | ✅ 200 ZIP（858,046 B） |
| S4A12 86JP / 86JPGMTool archive+commits（`main`） | ✅ 200 |
| S4A21 用 `main` | ❌ archive 404；commits 200 但空数组（2B） |
| Gitee / Codeberg 令牌 | ✅ 200 |
| GitHub 令牌（新） | ✅ 200（repo/contents/releases） |

---

## 九、维护与排障清单（按规则执行）

1. **改分支（如上游默认分支变化）**：S4A21 现为 `master`，逐处替换二节 2.2 全部字面量点（update.ps1 5 处 + MirrorUploadService.cs 常量与 commits URL + 开发者脚本 2 变量）；同步实测（八节）。不要用 `main`（空数组/404 坑）。
2. **换令牌**：五节手册——4 类文件全局替换双重 base64，重编译并部署两处；上传 401 = 对应令牌失效。
3. **改仓库地址 / 镜像仓库名**：改各脚本顶部变量与 C# 常量（一节总览表的集中点），同步 UI 展示链接（MainForm.Pages.cs 仓库链接卡片）。
4. **改上传文件名前缀**：S4A21 系列保留 `ServerS4A21-*` / `ServerS4A21-GMTool-*`；S4A12 系列 `ServerS4A12-*` / `DfoGmTool-*`（开发者脚本）。修改点 = update.ps1 镜像 URL 数组（L114-124）+ MirrorUploadService.cs + 开发者脚本。
5. **重编译部署**：四节三版本命令，部署游戏根与 AUM 根两处；双工程顺序编译。
6. **ps1 编码**：修改后务必保留 BOM（`EF BB BF`）+ 5.1 引擎验证（6.2）。
7. **更新日志为空**：先查分支是否用了 `main`（2.2 警告）。
8. **改版号**：同步 5 处——`MainForm.cs` VER（含 NET48 分支）、`ServerUI.csproj`、`ServerUI-Win7.csproj`、`ServerUI.csproj(主版本-net10).txt`、`AUM-version.txt`。
9. **文档同步**：功能行为变化后同步「使用说明」页条目、《更新记录与安装目录结构.md》、本文件。
10. **UI 改动先跑无头自测**（v2.17 起，不弹窗、不提权、不联网）：
    ```
    cd AUM管理组件\ServerUI.Headless
    $env:SERVERUI_BASE_DIR = "E:\Game\S4A21_CN\AUM管理组件"
    dotnet run -c Release -- C:\Users\你\Desktop\ui-report
    ```
    输出：`ui-report\layout.txt`（页标题是否绘制、0 尺寸控件、单行文字裁切、切页耗时、快照切页结果）
    + 7 张页面 PNG + 深色/浅色对照。改完 UI **先看报告再部署**。
    - 工程不带 `app.manifest`，所以不会请求管理员；窗口移到屏幕外、1% 透明度、不抢焦点。
    - 通过 `SERVERUI_BASE_DIR` 指向真实部署目录，才能读到 `GameGaurd.ini` / 存档等真实数据。
    - 无头模式下 `CaptureContent()` 改用 `DrawToBitmap`（屏幕外没法 `CopyFromScreen`）。
11. **UI 性能红线（勿回退）**：
    - `Card()` 的 `Shadow` 必须为 0 —— 实测卡片阴影占整页重绘约 60%（130ms → 55ms）。
    - `Card(8, 2)` 的第二参数已不再生效；不要为了"立体感"把阴影加回来。
    - 字体一律用 `F(字号, 样式)` 缓存；不要在循环/重建里 `new Font`（尤其日志的加粗行）。
    - 页面根容器用 `TableLayoutPanel` 绝对行高时，标题类控件必须 `Margin=0`：
      AntdUI.Label 在"控件高度 < 行高"时整行不绘制（v2.17 页标题消失的根因）。
    - 页面切换不要改回"先绘制再显示"：`ShowPage` 走快照优先（详见《更新记录…》七节）。
    - 日志 `FlushLog` 的 `WM_SETREDRAW` 包裹、批量和 `_logBuilder` 裁剪不要拆掉。


---

## 十、WebView2 重构版维护（v2.16+）

- **工程**：`AUM管理组件\ServerUI.Web\`（net10.0-windows，WPF + WebView2）。旧 `ServerUI\` 仅作为兼容模式与其 Services/Models 的只读链接来源；不要在新界面里改旧 MainForm。
- **构建/发布**：见《ServerUI-WebView2重构说明.md》三节；单文件发布必须带 `IncludeNativeLibrariesForSelfExtract=true`，自包含版再加 `EnableCompressionInSingleFile=true`。
- **前端资源**：`web\` 在构建时压成嵌入资源 `ServerUI.Web.web.zip`，启动解压到 `%LOCALAPPDATA%\ServerUI.Web\web\<sha256>\`；改 CSS/JS 后必须重新构建，或设 `AUM_WEB_ROOT=…\web` 做热改。
- **IPC id 必须字符串**：前端 `bridge.js` 用 `'c' + seq`；宿主按字符串回显。数字 id 在 mock 里能过、真实宿主会全部超时（已踩过）。
- **禁止原生 UI**：不要引入 `MessageBox`、`<select>`、`<input type=date>`、系统右键/窗口；确认、提示、文件选择、日期选择全部用 `web/js/ui/*` 的主题化组件。
- **路径**：`Paths.Ad` 兼容 exe 在游戏根或 AUM 目录两种位置；`Paths.Gr` 始终按 Ad 的父目录推导；`LocalGameBat` 必须用 `Gr\本地游戏S4.bat`（exe 在游戏根时旧逻辑会指到上一层）。
- **AUM 自更新**：`CheckAndUpdateAumAsync` 有护栏（`blocked:true`），避免旧 `SelfUpdateService` 编译 WinForms 源码覆盖新 exe；新版源码发布后应改为发布 `ServerUI.Web.csproj` 并移除护栏。`AUM_ALLOW_LEGACY_SELFUPDATE=1` 仅用于确认要回退旧版的场景。
- **窗口模式**：几何切换在 `MainWindow.SetMode/ApplyMode`；JS 只发 `window.mode`，不得直接改窗口尺寸。full 几何在离开前保存、返回时恢复，关闭时保存的是 full 几何。
- **测试**：
  - `node tools\ui-shots.js --out ..\_ui-report --sizes=1280x840,1080x700`（mock，0 个 `!!`）；
  - `node tools\app-shots.js --exe=..\ServerUI-有依赖版.exe --out ..\_ui-report\real --no-close`（真实宿主离屏 + 真实数据只读）；
  - 离屏钩子：`AUM_OFFSCREEN=1`、`AUM_CDP_PORT`、`AUM_DATA_ROOT`、`AUM_WEBVIEW_DATA`；默认不弹窗、不提权。
- **性能红线**：不要加页面过渡/淡入淡出；日志必须批量 append；状态推送必须 JSON diff 后原地更新；页面切换不要重建侧栏/日志。
- **兼容模式**：`ServerUI-兼容模式.exe` 与 `开始游戏-ServerUI.exe` 保持旧版，不随新界面更新；Win7 仍可用。

12. **v2.20 界面色板（勿回退）**：控件色 = 老版 Ant Design 深色（`--bg #141414`、`--surface #1C1C1C`、`--primary #1668DC`、`--success #49AA19`、`--danger #DC4446`、`--warning #D89614`）；日志色 = 旧 MainForm 原值（`#28A745/#FD7E14/#DC3545`，底 `#18181C`）。两套色板互不跟随，改控件色不要连日志一起改。
13. **更新AUM 是双通道，不要再加"护栏阻止"**：`AumUpdateService` 先 `PlanAsync` 判定（compile / package），`ExecuteAsync` 执行；旧 `AUM_ALLOW_LEGACY_SELFUPDATE` 护栏已废弃。发版必须走 `tools/make-release.ps1` + 打 tag（CI 会校验 tag 与 `AUM-version.txt` 一致）。改校验规则时同步 `--updatetest` 的 19 个用例（`ServerUI.Web/UpdaterTests.cs`）。
14. **功能对照基线**：改动前后请对照《ServerUI-新旧功能对照.md》（旧版每个可点击控件 → 新版实现位置）。已知有意偏差（更新AUM 护栏、完整更新入口、主题化对话框、设置持久化）写在该文第十一节，不要当成遗漏去"修复"。
15. **「开始游戏」不得因服务端在运行而拦截**：多开 DLL 依赖「服务端常驻 + 反复拉起客户端」。`ServerStartAsync` 在 `ServerAlive()` 为真时跳过启动、直接拉起客户端并返回 `reused:true`；前端 `wirePlay` 不能恢复「无需重复启动」的拦截。改动后用 `--playtest`（沙箱）或 `ui-shots.js` 的 `play-while-running` 断言验证。
16. **日志区跟随主题**：`tokens.css` 深色段是旧版日志色（`#18181C` + `#28A745/#FD7E14/#DC3545`），light 段覆盖为 `#F5F5F7` + `#1A7F37/#B25E09/#CF1322`。`ui-shots.js` 的 `palette-dark/palette-light` 会分别断言两套日志色，改一处必须同步改断言。
17. **启动动画只用合成层**：`MainWindow.xaml` 的 `Splash` 与 `web/index.html` 的 `.splash` 同构；动画只允许 `OpacityProperty` / `TranslateTransform` / `ScaleTransform` / `RotateTransform`。不要动画 `Width`/`Height`/`Margin`/`Padding`。弧线在 WebView2 就绪前持续旋转（`RepeatBehavior.Forever`），就绪后由 `HideSplash` 停表并淡出（会把动画时钟置 null，避免隐藏层持有渲染资源）。
18. **服务端控制台必须不可见**：`ServerService.Start` 用 `WindowStyle=Hidden` + `CreateNoWindow=false`（不能改 true，DfoServer 需要真实控制台）；隐藏句柄只能用 `AttachConsole + GetConsoleWindow + ShowWindow`，`Process.MainWindowHandle` 对 cmd/conhost 恒为 0。`--selftest` 的 `consoleHide` 探针必须 `ok:true`。
19. **DLL 排序只走 Pointer Events**：`dll.js` 手柄 `pointerdown` + 4px 阈值 + `insertBefore` 实时换位 + 松手一次 `dll.setOrder`。真实 WebView2 里 HTML5 `dragstart` 不可靠，不要再改回 `draggable`/`dataTransfer`。
