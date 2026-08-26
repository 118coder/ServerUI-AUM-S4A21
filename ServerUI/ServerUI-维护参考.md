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
| 更新日志 | 数据源 = gitgud ServerS4A21 提交（master）；镜像 `mirrors/更新日志.txt` | update.ps1 L2108 提交拉取段；MirrorUploadService.cs 上传段 |

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
| 更新日志 | `mirrors/更新日志.txt`（URL 编码 `%E6%9B%B4%E6%96%B0%E6%97%A5%E5%BF%97.txt`） | 由 gitgud ServerS4A21 提交记录生成 |

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
- 已知警告（可忽略、勿删）：`MainForm.cs(353) warning CS0162 不可达代码`；兼容模式 Fody PrivateAssets 提示。

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