# ServerUI WebView2 重构 · 实施任务书（给 code-cc）

> 版本：Phase 1 任务书（2026-10-22）
> 项目根：`E:\Game\S4A21_CN\AUM管理组件\ServerUI.Web\`
> 旧实现（只读参考，不要修改）：`E:\Game\S4A21_CN\AUM管理组件\ServerUI\`
> 参考架构（只读）：`E:\网页小工具\游戏启动器\nova-game-space-desktop\`
> 验收人：主模型（审稿）。你只管实现，不要部署、不要改游戏目录、不要改旧工程。

---

## 0. 一句话目标

用 **.NET 10 WPF + WebView2** 重写 ServerUI 的界面层，保持**现有深色配色、左侧导航 + 卡片布局、全部功能**；
禁止出现任何"浏览器痕迹"（默认右键、原生下拉/日期选择器、白色滚动条、浏览器弹窗、devtools、状态栏、缩放等）。
页面切换必须**瞬时**（不要淡入淡出、不要滑动动画）；日志区的分隔必须干净（不要黑色粗缝 + 白色横线）。

---

## 1. 硬约束（违反即打回）

1. **只允许修改** `ServerUI.Web\` 内的文件。旧工程 `ServerUI\`、部署目录、`GameGaurd.ini`、游戏根目录一律不动。
2. 不联网：前端不得引用 CDN/Google Fonts/任何远程资源；CSP 收紧为本地资源。图标用内联 SVG。
3. 不使用前端框架/CDN（不要 React/Vue/Tailwind）。原生 HTML/CSS/JS 即可，模块化拆文件。
4. 不要页面过渡动画。切页 = 立即显示/隐藏，保留各页滚动位置。悬停/按压只允许 ≤120ms 的颜色/位移过渡。
5. 不要浏览器原生控件：`<select>`、`<input type=date>`、`<input type=color>`、原生 tooltip（`title`）、原生右键菜单、
   原生滚动条、原生文件对话框、`alert/confirm/prompt` 全部不用。需要时自己写主题化组件。
6. 不要 AntdUI / WinForms 控件。旧工程仅作为行为与文案参考。
7. UI 文案保持中文，尽量逐字沿用旧工程的按钮/说明/帮助/疑难解答文案。
8. 版本号当前为 `2.20`（Phase 1 原始任务书为 2.16，v2.20 迭代统一升版）；标题栏显示 `ServerS4A21 管理器 v2.20 · ServerS4A21-AUM`。
9. 不引入任何需要联网的 NuGet；只用 `Microsoft.Web.WebView2`。
10. 不把 WebView2 的 user data 放进游戏目录；放 `%LOCALAPPDATA%\ServerUI.Web\WebView2`，允许 `AUM_WEBVIEW_DATA` 覆盖。

---

## 2. 先读哪些文件（按顺序）

### 2.1 参考架构（照抄模式，不要照抄视觉）
- `nova-game-space-desktop\MainWindow.xaml` / `MainWindow.xaml.cs`
  - `WindowStyle=None` + `WindowChrome`（CaptionHeight=0, ResizeBorderThickness=6）
  - 启动 Splash 盖住 WebView，`EnsureCoreWebView2Async` 后 `Navigate`
  - 标题栏拖拽：JS → `BeginNativeDrag()`（ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION）
  - `Closing` 拦截 → 通知前端弹主题化确认框 → `QuitNow()`
  - WPF `Drop` 事件取文件路径 → 交给 bridge（页面拿不到真实路径）
  - WebView2 ProcessFailed → Reload
  - `NOVA_CDP_PORT` / `NOVA_WEBVIEW_ARGS` 环境变量（照抄，改成 `AUM_CDP_PORT` / `AUM_WEBVIEW_ARGS`）
- `NativeBridge.cs` 头部 60 行 + `OnMessageReceived` / `Notify`
  - `AreDevToolsEnabled`（发布版必须 false）、`IsStatusBarEnabled=false`、
    `AreBrowserAcceleratorKeysEnabled=false`、`AreDefaultContextMenusEnabled=false`、
    `IsZoomControlEnabled=false`、`IsPasswordAutosaveEnabled=false`、`IsGeneralAutofillEnabled=false`
  - `SetVirtualHostNameToFolderMapping`
  - JS: `postMessage(JSON.stringify({id, method, args}))`；host 回 `{id, ok, result|error}`；推送 `{type, payload}`
- `Services\FileBrowser.cs` — **内置文件浏览器**（盘符/目录/过滤/排序），前端模态框调用；禁止系统文件对话框。
- `src\app.js` 搜索 `contextmenu` / `openContextMenu` — 自定义右键菜单的键盘可访问实现。
- `tools\headless-shots.js` + `tools\lib\headless-host.js` — **无头截图**：本地静态服务器 + 在页面里 mock bridge +
  headless Chromium + CDP 截图。请改造一套用于本项目的 UI 测试。
- `src\index.html` 的 `<svg class="svg-defs">` — 内联图标 symbol 的用法。

### 2.2 旧 ServerUI（行为/文案/配色/布局的唯一样板）
- `ServerUI\MainForm.cs`：窗口骨架、配色常量、按钮/标签/卡片工厂、主题切换、Resize/DPI。
- `ServerUI\MainForm.Pages.cs`：开始页 / 存档管理页 / 设置与关于页 / 使用说明页 / 日志条 / 文案。
- `ServerUI\MainForm.Server.cs`：服务端启停、状态轮询、更新编排、DX、SDK、日志、关闭清理。
- `ServerUI\MainForm.Archive.cs`：存档操作（储存/导入/导出/撤销/换挡/排序/清理冗余 DB）。
- `ServerUI\MainForm.DllExt.cs`：DLL 扩展页、GameGaurd.ini 读写、INi 编辑器、添加/删除/拖拽排序。
- `ServerUI\MainForm.Time.cs` + `TimeTool.cs`：系统时间页与提权写时间。
- `ServerUI\Services\*.cs`、`Models\*.cs`、`Compat.cs`：**直接复用**（见 §4）。
- `ServerUI\UI规范.md`、`ServerUI-维护参考.md`：规则与已知坑。

### 2.3 视觉基准截图
- `C:\Users\12290\hout\dash.png`（深色开始页，最新版布局）
- `C:\Users\12290\hout\dll.png`（DLL 扩展页）
- `C:\Users\12290\hout\archive.png` / `time.png` / `about.png` / `help.png` / `tsh.png`
- `C:\Users\12290\hout\dash-light.png` / `dll-light.png`
- 用户旧版截图（AntdUI 原味）：标题栏、状态胶囊、4 张统计卡、左导航 180px、日志条。
- 注意：截图里日志区上方的黑色粗缝 + 白色线**是用户明确讨厌的**，新 UI 必须重做（见 §6.5）。

---

## 3. 工程结构（在 `ServerUI.Web\` 内新建）

```
ServerUI.Web\
  ServerUI.Web.csproj          # net10.0-windows, UseWPF+UseWindowsForms, x64, WebView2
  app.manifest                 # PerMonitorV2；asInvoker（时间写入用 TimeTool.RunElevated 提权）
  App.xaml / App.xaml.cs       # 单实例互斥；启动 MainWindow
  MainWindow.xaml / .cs        # 无边框 WPF 壳、WebView2、Splash、关闭拦截、Drop
  Bridge\
    NativeBridge.cs            # 消息总线：{id,method,args} → result；Notify 推送
    Commands.cs                # 方法路由表（按 Phase 分文件）
    AppController.cs           # 服务端/更新/DX/目录/关闭清理（移植 MainForm.Server.cs 行为）
    ArchiveController.cs       # 存档（移植 MainForm.Archive.cs 行为）
    DllController.cs           # DLL 扩展 + ini（移植 MainForm.DllExt.cs 的非 UI 逻辑）
    TimeController.cs          # 时间页
    SettingsStore.cs           # 主题/窗口几何/开关 (JSON, 放 AUM管理组件\设置.json)
    Paths.cs                   # _ad / _gr / 各种路径（对齐旧 MainForm 构造器）
    IniFile.cs                 # GameGaurd.ini 读写（编码探测、无 BOM 写回）
  Services\                    # 链接旧工程：..\ServerUI\Services\**\*.cs
  Models\                      # 链接旧工程：..\ServerUI\Models\**\*.cs
  Compat.cs / TimeTool.cs      # 链接旧工程
  web\
    index.html
    css\tokens.css  base.css  shell.css  pages.css  widgets.css  log.css
    js\bridge.js  app.js  router.js  ui\*.js  pages\*.js  data\help.js  data\tsh.js  mock\bridge-mock.js
    assets\icons.svg
  tools\ui-shots.js  audit-layout.js  lib\cdp.js  lib\mock-host.js
  docs\REWRITE-BRIEF.md        # 本文件
```

### 3.1 csproj 要点
- `TargetFramework=net10.0-windows`、`UseWPF=true`、`UseWindowsForms=true`、`RuntimeIdentifier=win-x64`。
- `Microsoft.Web.WebView2` 用参考工程的版本（1.0.2903.40）或更新稳定版。
- 链接旧服务：
  ```xml
  <Compile Include="..\ServerUI\Services\**\*.cs" Link="Services\%(RecursiveDir)%(Filename)%(Extension)" />
  <Compile Include="..\ServerUI\Models\**\*.cs" Link="Models\%(RecursiveDir)%(Filename)%(Extension)" />
  <Compile Include="..\ServerUI\Compat.cs" Link="Compat.cs" />
  <Compile Include="..\ServerUI\TimeTool.cs" Link="TimeTool.cs" />
  ```
- **前端资源**：
  - 发布必须单 exe 可跑：把 `web\` 打包为嵌入资源 `ServerUI.Web.web.zip`（MSBuild Target 用 PowerShell Compress-Archive，
    或逐文件 EmbeddedResource），启动时解压到 `%LOCALAPPDATA%\ServerUI.Web\web\<assembly hash>`，再
    `SetVirtualHostNameToFolderMapping("app.serverui", 解压目录, Allow)`，`Navigate("https://app.serverui/index.html")`。
  - 开发覆盖：`AUM_WEB_ROOT` 指向 `web\` 目录时直接映射该目录（热改 CSS/JS 不用重编译）。
  - 版本变化时清理解压目录；不要清 WebView2 用户数据（登录态/缓存可保留）。
- `app.manifest`：`PerMonitorV2`，`asInvoker`；不要 `requireAdministrator`（时间写用自提权）。

---

## 4. 复用旧服务（不要重写）

直接链接并调用：
- `Services\ServerService.cs`：`Start(baseDir)` / `Stop()` / `IsRunning` / `IsBatRunning` / `IsDfoServerRunning(dir)` /
  `PvfExists(baseDir)` / `NoticeReceived` / `StartedAtUtc` / `UnexpectedExitUtc` / `LastExitCode` /
  `HideConsoleWindow()` / `HideDfoServerWindow()` / `CleanOrphans()`。
- `Services\ArchiveService.cs`：`List` / `BackupCount` / `CurrentInfo` / `SaveArchive` / `SwitchToArchive` /
  `UndoSwap` / `ExportAsZip` / `ImportFromZip` / `Swap` / `Delete` / `GetLatestBackupFolder`。
- `Services\UpdateService.cs`：`CheckRepositoryAsync` / `CheckBasicConnectivityAsync` / `CheckMirrorSourcesAsync` /
  `RunIncremental` / `RunFull` / `OutputReceived` / `Completed` / `CancelUpdate` / `GetVersion` / `GetLogText` / `ReadLogTail`。
- `Services\SelfUpdateService.cs`、`MirrorUploadService.cs`、`Compat.cs`、`TimeTool.cs`、`Models\*`。
- 旧 `MainForm` 里的业务编排（更新进度映射、镜像上传、AUM 自更新、关闭清理、GameGaurd.ini 读写、DX 复制/删除）
  **照行为移植到 Bridge 层**，不要照抄 WinForms 代码。旧文件里的注释说明了每个坑，先读再写。

---

## 5. 宿主与 WebView2 行为

### 5.1 窗口
- 无边框、可缩放、最小 1080×700，默认 1280×840，居中；Win11 用 DWM 圆角 8px（失败则直角）。
- 1px 主题边框（深色 #2A2A31 / 浅色 #E3E3E8）；不要系统标题栏、不要白色闪屏。
- 标题栏高 48px（HTML 绘制）：左侧图标 + `ServerS4A21 管理器` + 版本小字；右侧状态胶囊、`典`、`简`、主题按钮、
  最小化/最大化/关闭。拖拽区域：标题栏空白处 pointerdown → `window.drag`；双击 → 最大化切换。
- Splash：WPF 深色遮罩 + 应用名 + "正在启动界面…"；收到 `app.ready` 或 `NavigationCompleted` 后淡出（150ms 可以）。
- 关闭：Alt+F4/关闭按钮 → `ask-close` → 前端主题化确认 → `app.quit`；退出时执行旧 `Fc()` 的清理
  （取消更新、停服务端、杀 DfoGmTool、保存 `运行日志.txt`、清编译/更新缓存）。
- 单实例：参考 App.xaml.cs 的 Mutex + 激活事件（第二次启动激活已有窗口）。
- 启动参数：`--settime "..."` / `--synctime` 直接走 TimeTool（无窗口）；`--selftest` 只做只读桥命令自检并退出。
- 环境变量：`AUM_CDP_PORT`（开 CDP）、`AUM_WEBVIEW_ARGS`（附加 Chromium 参数）、`AUM_WEB_ROOT`（开发前端目录）、
  `AUM_DATA_ROOT`（测试用设置/日志目录，避免动真实存档）。

### 5.2 WebView2 设置（发布版全部按此）
```csharp
s.AreDevToolsEnabled = false;            // AUM_DEVTOOLS=1 时才 true
s.IsStatusBarEnabled = false;
s.AreBrowserAcceleratorKeysEnabled = false;
s.AreDefaultContextMenusEnabled = false;
s.IsZoomControlEnabled = false;
s.IsPinchZoomEnabled = false;
s.IsSwipeNavigationEnabled = false;
s.IsPasswordAutosaveEnabled = false;
s.IsGeneralAutofillEnabled = false;
s.IsBuiltInErrorPageEnabled = false;
s.IsWebMessageEnabled = true;
```
- `NavigationStarting`：只允许 `https://app.serverui/`；外部 http(s) 用系统浏览器打开并取消。
- `NewWindowRequested`：一律取消（外部链接走 `shell.openUrl`）。
- `PermissionRequested`：全部拒绝。
- `AcceleratorKeyPressed`：拦截 Ctrl+R/F5、Ctrl+Shift+I、F12、Ctrl+P、Ctrl+U、Ctrl+F、Alt+Left/Right。
- JS 侧再兜底：`contextmenu` preventDefault、`Ctrl+wheel` preventDefault、`dragstart` 禁止非必要拖拽。
- `DefaultBackgroundColor = #000`（浅色主题切到 #fff）。
- 进程失败（RenderProcessExited）→ 自动 Reload 一次并提示。

### 5.3 消息协议
JS → host：`postMessage(JSON.stringify({ id, method, args }))`（与参考一致）
host → JS：
- 回复：`{ id, ok: true, result: {...} }` 或 `{ id, ok: false, error: "中文错误" }`
- 推送：`{ type: "log"|"progress"|"status"|"toast"|"archive.changed"|"dll.changed"|"window.state"|"theme.changed"|"ask-close", payload: {...} }`
所有方法名用点号：`server.start`、`archive.list`、`dll.apply`、`time.sync`…
命令必须异步执行（除只读快速命令），UI 线程不阻塞；长任务支持取消/防重入。
`bridge.js` 封装 `call(method, args)`（Promise + 超时 + 中文错误 toast）。

---

## 6. 视觉规范（保持原配色和布局）

### 6.1 设计令牌（`tokens.css`）
深色（默认）：
```
--bg:#000; --surface:#0E0E11; --surface-2:#15151A; --surface-3:#1C1C22;
--border:#2A2A31; --border-strong:#64666E;
--text:#F2F2F5; --text-2:#B0B0B8; --muted:#8C8C94; --faint:#787880;
--primary:#1E90FF; --success:#28A745; --danger:#DC3545; --warning:#FD7E14; --info:#4EC9B0; --gold:#DAA520;
--log-bg:#18181C;
```
浅色：
```
--bg:#FFF; --surface:#FFF; --surface-2:#F5F5F7; --surface-3:#ECECF1;
--border:#E3E3E8; --border-strong:#9A9AA5;
--text:#1F1F23; --text-2:#5C5C66; --muted:#6E6E78; --faint:#70707A;
--primary:#2196F3; --success:#4CAF50; --danger:#F44336; --warning:#FF9800; --info:#00BCD4;
```
- 字体：`"Microsoft YaHei UI", "Segoe UI", sans-serif`；日志 `Consolas, monospace`。
- 字号：页标题 20px/700；副标题 12px；卡片小节 13px/700；正文 12.5–13px；按钮 13px；小字 11px；统计值 20px/700。
- 圆角：卡片 8px；按钮 6px；输入框 6px；胶囊 999px；分段控件 6px。
- 间距：窗口外边距 12px；导航 180px；卡片内边距 16px；卡片间距 10px；页面根 padding 12/8/12/10。
- 卡片：`background:var(--surface); border:1px solid var(--border); border-radius:8px;` **不要 box-shadow**。
- 滚动条：宽 8px；thumb `--border`，hover `--border-strong`，轨道透明；不要系统滚动条。
- 焦点：`:focus-visible { outline:2px solid var(--primary); outline-offset:2px }`。

### 6.2 骨架（`shell.css`）
```
.app (100vh, bg)
├─ .titlebar (48px)
├─ .body (flex, padding 12px, gap 10px)
│  ├─ .sidebar (180px, card)
│  └─ .content (card, flex:1, position:relative)
│     └─ .page (position:absolute; inset:0; overflow:auto; padding:12px 12px 10px)
└─ .log-panel (height 170px, card) + .log-resizer (6px hitbox, 见 6.5)
```
- 左侧导航：7 项（开始/存档管理/DLL扩展/调整系统时间/设置与关于/使用说明/疑难杂症解惑），行高 56px，图标 18px，
  选中态整行圆角 8px、`--primary` 背景、白字；未选中 `--text`，hover `--surface-2`。
- 内容卡片：`--surface`、1px 边框、圆角 8px；页面自己滚动，外层不滚动。
- 每页第一行：页头（标题 20px + 副标题 12px，右侧可放操作按钮，高 58px）。

### 6.3 首页（Phase 1 必须完成）
与 `dash.png` 一致的结构：
- 页头：`开始` / `服务端与游戏的一键控制台 · 常用操作都集中在这里`。
- 统计卡 4 张（高 92）：服务端状态 / PVF 状态 / 服务端版本 / .NET SDK；caption 12px muted；value 20px/700；
  状态值前带 6px 圆点（红/绿/橙）。
- 左侧卡片「服务端控制」：
  - 小节标题（4×14px `--primary` 竖条 + 13px/700）。
  - 开始游戏：整行 56px，`--success`，16px/700，图标 play。
  - 停止服务端（左 1/3，`--danger`）/ 重启服务端（右 1/3，`--warning`）。
  - 可选运行方式：标签 + 分段控件（DX11 / DX12；再点已选项取消 = 默认 DX9）。
  - 去除 dgVoodooCpl 水印：开关 + 文字。
  - 打开PVF目录 / GM工具 / 打开主存档：三列。
  - 还原-原版PVF：整行。
  - 底部提示小字：`提示：首次使用请先完成一次更新；服务端运行中会阻止存档切换等操作。`
- 右侧卡片「更新与工具」：
  - 开始更新（整行 56px，`--primary`，16px/700）。
  - 查看更新日志（整行）。
  - 上次更新：小字（读取 `更新日志.txt`）。
  - 更新AUM / 安装NET.10 SDK（两列）。
  - 底部提示小字：`提示：更新耗时较长，期间请勿重复点击；更新失败可勾选下方「镜像下载」后重试。`
- 按钮默认态：透明底 + 1.5px `--border-strong` 边框 + `--text`；hover `--surface-2`；active 再深一点。
  彩色按钮：纯色底 + 白字，hover 亮度 +6%，active -4%；不要渐变、不要阴影、不要水波。
- 所有按钮 `cursor:pointer`，禁用态 40% 透明 + `cursor:not-allowed`；长任务按钮显示内联 spinner 并禁用。

### 6.4 日志面板（`log.css`）
- 卡片：`--surface`，1px 边框，圆角 8px；toolbar 44px + body。
- toolbar：左侧竖条 + `运行日志`；`跳过更新日志`、`镜像下载` 两个开关（muted 文字）；中间进度文字
  `更新进度: 42%`（仅更新时显示）；右侧 `复制日志`、`清空日志`、折叠 chevron。
- body：`--log-bg` 背景，Consolas 12px/1.55，内边距 10px 12px，圆角 6px，自动跟随到底部（用户上翻时不抢滚动）。
  行格式 `[HH:mm:ss] 文本`；颜色：默认 `#F0F0F5`、成功 `#28A745`、警告 `#FD7E14`、错误 `#DC3545`、
  信息 `#4FC3F7`、次要 `#B0B0B8`。最多保留 2000 行（DOM 里裁掉头部），复制用 host 剪贴板。
- 折叠：高度 46px，只留 toolbar；chevron 旋转 180°（80ms）。
- **分隔（重点）**：内容卡片与日志卡片之间留 8px 的窗口背景空隙，**不要整行横线**。
  空隙中央放 36×4px、圆角 2px、`--border` 的"抓手"；默认 opacity .35，hover/drag 时 opacity 1 且变 `--primary`；
  命中区域高 10px，`cursor:ns-resize`。拖动改日志高度（min 80，max 窗口 42%），双击复位 170px。
  **禁止**出现黑色粗条 + 白色横线的旧样式。
- 更新进度条：toolbar 或日志 body 顶部一条 3px 的 `--primary` 进度条；不要弹窗。

### 6.5 组件规范（`widgets.css` / `js/ui`）
- **开关**：40×22，圆角 11px；关 `--surface-3` + 边框；开 `--primary`；thumb 18px 白，位移 80ms。
- **分段控件**：容器 `--surface-2` 圆角 6px，内边距 3px；项高 30px，选中 `--surface-3` + `--text`，未选 muted；
  再点已选项取消（与旧逻辑一致）。
- **表格**：表头 `--surface-2`，12px muted；行高 40px；hover `--surface-2`；选中/当前行左侧 3px `--primary`；
  列：# 56 / 名称自适应 / 大小 90 右对齐 / 修改时间 150 居中；表头"修改时间"可点击排序（箭头指示）。
- **右键菜单**：绝对定位面板，`--surface-2`，1px 边框，圆角 8px，padding 4px；项 32px，hover `--primary` 12% 透明底；
  Esc/点外面关闭；键盘上下/回车可用；不要系统菜单。
- **模态框**：遮罩 `rgba(0,0,0,.55)` + 居中面板（`--surface`，圆角 10px，边框，最大宽 640px）；
  标题 15px/700，正文 13px，底部右对齐按钮；Esc 取消，Enter 确认，焦点陷阱；不要系统 MessageBox。
- **输入框 / 文本域**：`--surface-2` 底，1px 边框，圆角 6px，focus 边框 `--primary`；不要浏览器 autofill 样式。
- **日期时间选择器**：必须自绘（日历网格 + 时/分/秒输入），禁止 `<input type=date>` 的原生弹层。
- **下拉选择**：自绘按钮 + 浮层列表，禁止 `<select>`。
- **Tooltip**：自绘，延迟 400ms，`--surface-3` 底 + 边框 + 11.5px 文字，跟随鼠标/锚点；禁止 `title`。
- **Toast**：右下角堆叠，3s 自动消失，成功/警告/错误三种色；进入/退出 120ms。
- **文件浏览器**（后续 Phase）：盘符下拉 + 面包屑 + 列表 + 过滤；调用 host `fs.browse`；禁止系统文件对话框。
- **空态**：图标/标题/说明/主按钮，居中；不要空白。

---

## 7. IPC 方法清单（全量；Phase 1 先实现标 ✅ 的）

### 应用/窗口
- ✅ `app.init` → `{ version, theme, paths:{ad,gr}, isAdmin, page }`
- ✅ `app.ready`（前端通知 splash 消失）
- ✅ `app.quit`
- ✅ `window.minimize` / `window.toggleMaximize` / `window.close`
- ✅ `window.drag`
- ✅ `window.mode` `{mode:"full"|"mini"|"classic"}`（Phase 3）
- ✅ `settings.set` `{theme:"dark"|"light"}` / `settings.get`
- ✅ `clipboard.write` `{text}`

### 服务端/状态
- ✅ `server.start` / `server.stop` / `server.restart`
- ✅ `server.status` → `{bat,dfo,external,pvf,version,sdk,startedAt,unexpectedExit}`
- ✅ `dx.set` `{mode:"dx11"|"dx12"|"off", watermark:bool}`
- ✅ `shell.open` `{kind:"pvfDir"|"saveDir"|"switchDir"|"backupDir"|"patchDir"|"url"|"file", value?}`
- ✅ `gm.launch`
- ✅ `pvf.restore`

### 更新
- ✅ `update.start` `{mode:"incremental"|"full", skipLog:bool, mirror:bool}`
- ✅ `update.cancel`
- ✅ `update.openLog`
- ✅ `aum.update`
- ✅ `sdk.install`
- `update.check`（仓库连通性，只读）

### 存档（Phase 2）
- `archive.list` → `{current,backupCount,entries:[{name,size,modified,current}]}`
- `archive.store` / `archive.import` / `archive.export` / `archive.undo`
- `archive.switch` `{name}` / `archive.rename` `{oldName,newName}`
- `archive.cleanRedundant` `{on:bool}`
- `archive.refresh`
- `archive.dropFile`（host 收到 Drop 后调用/推送）

### DLL 扩展（Phase 2）
- `dll.state` → `{installed, managed:[{file,name,desc,config,on,locked}], custom:[{file,on,exists}], managedOrder, customOrder}`
- `dll.apply` `{managedOn:[file...], customOn:[file...], managedOrder:[...], customOrder:[...]}`
- `dll.add`（打开内置文件浏览器，选 dll/ini）→ 复制到游戏根 + 写入 ini
- `dll.delete` `{file, deleteFile:bool}`
- `dll.readConfig` `{file}` → `{text, encoding}`
- `dll.saveConfig` `{file, text}`
- `dll.installPatch`（客户端补丁.zip）
- `dll.refresh`

### 时间（Phase 2/3）
- `time.get` → `{now}`
- `time.apply` `{iso}`
- `time.quick` `{year}`
- `time.sync`

### 文件（Phase 2）
- `fs.browse` `{path, filter:"dll"|"ini"|"zip"|"db"|"image"|null}` → `{path,parent,drives,dirs,files}`
- `fs.drives`

### 推送事件
- `log {level,text}`（host 批量 30–60ms 合并）
- `progress {value:0..100,label,visible}`
- `status {...}`（2s 轮询，只在变化时推送）
- `toast {level,text}`
- `archive.changed` / `dll.changed` / `theme.changed` / `window.state {maximized}` / `ask-close`

---

## 8. Phase 1 范围与验收（本轮只做这个）

### 8.1 范围
1. 新建 WPF + WebView2 工程，能构建、能启动（Splash → 页面）。
2. 前端骨架：标题栏、左侧导航、内容卡片、日志面板（含新的 6.5 分隔抓手）、主题切换。
3. 开始页完整实现（§6.3），按钮接真实 bridge 命令（服务端启停/重启、DX、水印、目录、GM、PVF 还原、
   开始更新/查看日志/更新AUM/安装SDK）。
4. 日志面板接真实 log 推送，支持复制/清空/折叠/拖动高度。
5. 状态轮询接真实 `server.status`（2s），更新状态胶囊与统计卡。
6. 关闭拦截 + 主题化确认 + 退出清理（旧 `Fc()` 行为）。
7. 无头 UI 测试：`tools/ui-shots.js` 用 mock bridge 输出 `dash-dark/dark-1080/dash-light` 等截图 + 布局审计。
8. `--selftest`：不显示窗口，打印 `app.init`/`server.status` 的 JSON 并退出（用于 CI/审稿）。

### 8.2 验收命令
```bash
cd "E:/Game/S4A21_CN/AUM管理组件/ServerUI.Web"
"/c/Program Files/dotnet/dotnet.exe" build ServerUI.Web.csproj -c Release -v q --nologo
# 无头截图（需要 node；可用参考工程 node_modules）
node tools/ui-shots.js --out ../_ui-report --sizes=1280x840,1080x700 --theme=dark,light
# 只读自检（不弹窗）
./bin/Release/net10.0-windows/win-x64/ServerUI.exe --selftest
```
### 8.3 通过标准
- 0 编译错误（只允许 `CS0162` 之类与旧代码无关的警告；不能有新的 null/类型警告）。
- 无头截图里：首页元素完整、无横向溢出、无文字裁切、无控制台错误（`console.error` 为 0）。
- 与 `dash.png` 对比：布局、层级、配色一致；日志分隔区域没有黑粗条/白线。
- 切页（Phase 1 只有开始页）无动画；主题切换即时；窗口可拖拽/最大化/关闭。
- `--selftest` 输出合法 JSON，`app.init` 路径与旧 `_ad/_gr` 一致。
- 不出现默认右键菜单、白色滚动条、原生日期/下拉控件。

---

## 9. 后续 Phase（先不做，写代码时留好扩展点）
- Phase 2：存档管理页 + DLL 扩展页（含内置文件浏览器、自定义右键菜单、INI 分组编辑器、拖拽排序）。
- Phase 3：时间页（自绘日期时间选择器）、设置与关于、使用说明、疑难解答、极简/经典模式、全部文案迁移。
- Phase 4：三版本打包（有依赖/无依赖/兼容模式）、部署两处、文档同步、全量视觉/功能审计。
- Phase 5（可选）：net48 Legacy WebView2（Win7 + 固定运行时 109），参考 `legacy/` 工程。

---

## 10. 交付格式（你每次汇报必须包含）

1. 改动/新增文件列表（绝对路径）。
2. 构建命令与结果（错误数、警告数）。
3. 无头截图输出路径 + 关键截图（首页深色/浅色）。
4. `--selftest` 输出。
5. 与任务书的偏差（没有就写"无"）。
6. 已知问题/未完成项。
7. 不要自行部署、不要覆盖旧 exe、不要改 `GameGaurd.ini`。
