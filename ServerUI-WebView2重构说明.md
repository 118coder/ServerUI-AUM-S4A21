# ServerUI WebView2 重构说明（v2.16 · 2026-10-22）

> 本文件记录 AUM 管理器从 WinForms + AntdUI 迁移到 **.NET 10 WPF + WebView2** 后的结构、构建、部署、测试与已知边界。
> 旧的 WinForms 版仍以「兼容模式」保留（net48 / Win7），不受本重构影响。

---

## 一、为什么重构

旧版所有页面由 AntdUI 即时绘制，卡片阴影、控件动画、整页重绘导致切页 100ms+ 且视觉硬切。新版：

- 界面由本地 HTML/CSS/JS 绘制，**切页只替换 DOM，无过渡动画**；实测真实宿主切页 12–48ms。
- 保留旧版的深色 token、左侧导航 + 卡片骨架、全部按钮/文案/命令。
- 宿主只负责窗口、WebView2 硬化、IPC、服务端/存档/DLL/更新等业务（复用 `ServerUI\Services`、`Models` 源码）。
- 无浏览器痕迹：无地址栏/标签/状态栏/默认右键/原生日期控件/缩放/开发者工具；单窗口、无第二窗口。

## 二、工程与产物

| 路径 | 作用 |
|---|---|
| `AUM管理组件\ServerUI.Web\` | 新界面工程（`ServerUI.Web.csproj`，net10.0-windows，WPF + WebView2） |
| `…\ServerUI.Web\web\` | 前端源码（HTML/CSS/JS，构建时压成嵌入资源 `ServerUI.Web.web.zip`） |
| `…\ServerUI.Web\Bridge\` | IPC 路由、服务端/存档/DLL/时间/文件浏览控制器 |
| `…\ServerUI.Web\tools\ui-shots.js` | 无头 mock 截图 + 布局/交互断言（不需要 WPF） |
| `…\ServerUI.Web\tools\app-shots.js` | 真实宿主离屏截图 + 真实 IPC/模式/关闭/单实例断言 |
| `…\ServerUI\` | 旧 WinForms 源码（兼容模式），同时被新工程只读链接复用 Services |

### 发布物

| 文件 | 说明 |
|---|---|
| `ServerUI-有依赖版.exe` | 框架依赖单文件（~1.8 MB），需系统安装 .NET 10 运行时 |
| `ServerUI-无依赖版.exe` | 自包含单文件（~70 MB，压缩），内置 .NET 10 |
| `ServerUI-兼容模式.exe` | 旧 WinForms 版（net48，Win7 可用），**未替换** |
| `开始游戏-ServerUI.exe` | 与兼容模式逐字节相同，**未替换**（GameGaurd/旧入口继续使用） |

两个新版都仍需要 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已自带）；缺失时新版显示主题化兜底页并提供下载按钮，不弹系统 MessageBox。

## 三、构建与发布

```powershell
cd "E:\Game\S4A21_CN\AUM管理组件\ServerUI.Web"

# 构建 + 自检
dotnet build ServerUI.Web.csproj -c Release
.\bin\Release\net10.0-windows\win-x64\ServerUI.exe --selftest

# 有依赖版（单文件）
dotnet publish ServerUI.Web.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\fd
copy publish\fd\ServerUI.exe publish\fd\ServerUI-有依赖版.exe

# 无依赖版（自包含单文件，压缩）
dotnet publish ServerUI.Web.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish\sc
copy publish\sc\ServerUI.exe publish\sc\ServerUI-无依赖版.exe
```

发布目录里的 `Microsoft.Web.WebView2.*.xml` 是 SDK 文档，可删除；单文件 exe 自身即可运行。

### 部署位置（2026-10-22 已执行）

- `AUM管理组件\ServerUI-有依赖版.exe`
- `AUM管理组件\ServerUI-无依赖版.exe`
- `游戏根\ServerUI-有依赖版.exe`
- `游戏根\ServerUI-无依赖版.exe`

保留不动：`AUM管理组件\ServerUI-兼容模式.exe`（+ `.config`）、`游戏根\开始游戏-ServerUI.exe`。
旧版备份：`AUM管理组件\_backup_webui_2026-10-22\`。

## 四、运行时数据位置

| 数据 | 路径 |
|---|---|
| 界面设置（主题/日志高度/窗口几何/开关） | `AUM管理组件\设置.json`（`AUM_DATA_ROOT` 可覆盖） |
| WebView2 解压后的前端 | `%LOCALAPPDATA%\ServerUI.Web\web\<zip sha256>\` |
| WebView2 用户数据 | `%LOCALAPPDATA%\ServerUI.Web\WebView2\` |
| 启动诊断 | `<exe 目录>\ServerUI-启动日志.txt` |
| 崩溃日志 | `<exe 目录>\ServerUI-崩溃日志.txt` |

前端解压目录按 zip 哈希分目录，升级后自动切换并清理旧目录；不会写进游戏目录。

## 五、窗口模式

| 模式 | 尺寸 / 最小尺寸 | 侧栏 | 日志 |
|---|---|---|---|
| full（完整） | 恢复上次几何，默认 1280×840 / 1080×700 | 显示 | 显示 |
| mini（极简，标题栏「简」） | 620×470 / 600×420 | 隐藏 | 隐藏 |
| classic（经典，标题栏「典」） | 1280×840 / 1080×700 | 隐藏 | 显示 |

- 同一窗口内切几何 + 切视图，不创建第二个 Window。
- 进入 mini/classic 前保存 full 几何；返回时恢复（含最大化状态）。
- 关闭/最小化/最大化/拖动/主题/单实例行为与完整模式一致。
- `window.mode` 命令：不带 `mode` 参数为只读查询；非法值回落 full 并返回中文错误。

## 六、IPC 协议要点

```
JS → host : postMessage(JSON.stringify({ id, method, args }))
host → JS : { id, ok:true, result } | { id, ok:false, error }
host → JS : { type:"log"|"progress"|"status"|"toast"|..., payload }
```

**id 必须是字符串**（前端 `'c' + seq`）。宿主 JSON 解析后按字符串回显，数字 id 会被回成字符串而无法匹配——这是 mock 测试测不出、真实宿主必现的坑，勿改回数字。

宿主对请求使用 `Task.Run` 执行，UI 线程不阻塞；长任务返回 `Commands.Deferred`。所有确认/提示/文件选择/日期选择都走前端主题化组件，禁止系统 MessageBox 与 `<select>`/`<input type=date>`。

## 七、测试

### 1. 无头 mock（不需要 WPF/WebView2）

```powershell
cd AUM管理组件\ServerUI.Web
node tools\ui-shots.js --out ..\_ui-report --sizes=1280x840,1080x700
```

覆盖：完整/极简/经典三模式、7 个页面、主题、存档服务端拦截、DLL 拖拽/应用/INI 编辑器、设置安装、时间自绘日历、帮助/疑难、tooltip、无原生控件、无横向溢出/裁切。看 `layout.txt`，出现 `!!` 即不通过。

### 2. 真实宿主离屏（WPF + WebView2 + 真实 IPC）

```powershell
node tools\app-shots.js --exe="..\ServerUI-有依赖版.exe" --out ..\_ui-report\real --no-close
```

- `AUM_OFFSCREEN=1`：窗口在屏幕外、1% 不透明度、不抢焦点、不进任务栏。
- `AUM_CDP_PORT`：开启 WebView2 CDP；`AUM_DATA_ROOT`/`AUM_WEBVIEW_DATA` 指向临时目录，不污染真实设置/单实例。
- 只做只读 IPC（status/archive.list/dll.state/fs.drives/time.get/settings.get/window.mode）+ `aum.update` 护栏检查；不启动/停止服务端、不更新、不写系统时间、不装补丁。
- 覆盖：真实切页耗时、主题、三模式尺寸、经典两栏/更新卡可见、关闭确认与退出、单实例、设置持久化、控制台 0 错误。
- 服务端正在运行时不要跑关闭确认；`--no-close` 只截图并在结束时结束测试进程树。

### 3. 自检

```powershell
ServerUI-有依赖版.exe --selftest
```

只读探测 app.init/server.status/archive.list/dll.state/fs.drives/fs.browse/time.get/window.mode，输出 `ServerUI-selftest.json`。

## 八、已知边界 / 刻意保留

1. **AUM 自更新被护栏阻止**：`SelfUpdateService` 仍指向旧 WinForms 源码（`ServerUI.csproj`），若在重构版里继续会编译旧界面并覆盖本 exe，等于静默回退。`CheckAndUpdateAumAsync` 现在直接返回 `blocked:true`；新版源码发布到仓库后可移除护栏并切换到 `ServerUI.Web.csproj`。需要强制旧路径时可设 `AUM_ALLOW_LEGACY_SELFUPDATE=1`（不推荐）。
2. **兼容模式不升级**：`ServerUI-兼容模式.exe` 与 `开始游戏-ServerUI.exe` 保持旧版，供 Win7 / GameGaurd 旧入口使用；它们不会获得 WebView2 新界面。
3. **无依赖版仍需 WebView2 Runtime**：自包含只包含 .NET，不包含 Edge WebView2；缺失时走兜底页。
4. **经典模式 1080×700**：整页纵向滚动，日志仍固定底部；1280×840 下左栏四块（开始/服务端/快速操作/更新工具）一屏可见、无内部滚动。
5. **时间页同步卡撑满页面**：内容贴顶、状态行贴卡片下沿，与旧版的百分比行高一致；卡片中部留白是有意保留的旧布局。
6. **日志首行在半行处截断**：日志滚动到底部时顶部半行不可见，属正常滚动容器行为，未做额外渐变/遮罩（避免引入非主题效果）。

## 九、回滚

1. 删除/重命名新版 `ServerUI-有依赖版.exe`、`ServerUI-无依赖版.exe`（两处）。
2. 从 `AUM管理组件\_backup_webui_2026-10-22\` 恢复旧有依赖/无依赖/兼容 exe 与 `.config`。
3. 旧版会继续使用 `AUM管理组件\设置.json` 之外的数据；如需彻底恢复，删除 `%LOCALAPPDATA%\ServerUI.Web\` 与 `AUM管理组件\设置.json`。

## 十、验收记录（2026-10-22）

- `dotnet build`：0 警告 0 错误。
- `ui-shots.js`：`layout.txt` 0 个 `!!`，三模式/七页/交互断言通过。
- 真实宿主（有依赖单文件、无依赖单文件、部署到游戏根）：`app-shots.js` 30+ 项断言 0 失败；切页 12–48ms；控制台 0 错误。
- 真实数据：`archive.list` 读到 `自用存档`（1 条，当前 `inventory.db` 2.3 MB）、PVF 已加载、`GameGaurd.ini` 路径正确、DLL 状态读取正常。
- 关闭确认：取消后应用存活；确认后进程退出，`设置.json` 写回主题/日志高度/窗口几何。
- 单实例：第二实例立即退出，第一个不受影响。
- AUM 自更新护栏：返回 `{blocked:true}`，不触发网络/编译。

## 十一、v2.20 变更

- 更新AUM 双通道：有构建环境优先源码编译（且校验源码含 `ServerUI.Web`），否则下载 Release 发行包；两者都做不到就明确报错。协议与发版流程见 `ServerUI.Web/docs/AUM-UPDATE.md`。旧的"护栏阻止"已移除。
- 多开：`ServerStartAsync` 检测到服务端存活时跳过启动，直接 `LaunchGameClient()` 并返回 `reused:true`；前端 `wirePlay` 不再拦截「开始游戏」。`--playtest` 是沙箱验证入口（需 exe 目录有 `playtest.marker`，否则拒绝执行）。

- 配色回归老版：控件色 = Ant Design 深色（`#141414 / #1C1C1C / #1668DC / #49AA19 / #DC4446 / #D89614`），日志色保留旧值（`#28A745 / #FD7E14 / #DC3545`，底 `#18181C`）。两套色板互不跟随。
- 侧栏与内容区使用 `--bg`（页面底色），卡片/日志使用 `--surface`，恢复旧版层次。
- DLL 排序改为 Pointer Events（`web/js/pages/dll.js`）；不要再改回 HTML5 `draggable`，真实 WebView2 里不可靠。
- 完整模式 DX 文案恢复「以DX11方式运行游戏 / 以DX12方式运行游戏」，经典模式用短标签。
- 版本 2.20；`AUM-version.txt` 同步。
- 启动动画：`MainWindow.StartSplashAnimation()`（WPF 层，WebView2 就绪前唯一可见层）+ `web/index.html` 的 `.splash`（同一套视觉）。两者都只用 `Opacity` / `Transform`；不要给启动页加 `Width`/`Height`/`Margin` 动画（会触发布局，低配机掉帧）。弧线在等待期间持续旋转，就绪后 `FinishSplash()` 补满进度条再淡出。
- 日志区跟随主题：深色用旧版 `#18181C` + 旧行色；浅色用 `#F5F5F7` + 深色行色（`--log-bg/--log-border/--log-hover/--log-*` 在 `tokens.css` 的 light 段覆盖）。不要再把浅色日志固定成黑底。
- 启动动画测试钩子（仅测试环境）：`AUM_SPLASH_SNAPSHOT=1` 按渲染帧采样 PNG；`AUM_SPLASH_TIMING=1` 只统计帧间隔；`AUM_SPLASH_DELAY_MS` / `AUM_SPLASH_HOLD_MS` 控制延时与停留。正常启动不受影响。
- 服务端控制台：`ServerService.Start` 用 `WindowStyle=Hidden`（CreateNoWindow 必须为 false，否则 DfoServer 无真实控制台会崩）；隐藏句柄走 `AttachConsole + GetConsoleWindow + ShowWindow`，不要再用 `Process.MainWindowHandle`（控制台归 conhost，实测恒为 0）。`--selftest` 的 `consoleHide` 探针会验证「窗口存在但不可见 + 可正常停服」。
