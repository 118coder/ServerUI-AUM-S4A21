# Phase 3a 任务书（设置与关于 / 时间 / 使用说明 / 疑难解答）

> 前置：Phase 1、Phase 2 已验收。只做本文件列出的 4 页 + 3 个宿主命令；不要改已验收的首页/存档/DLL/标题栏/日志。
> 工程：`E:\Game\S4A21_CN\AUM管理组件\ServerUI.Web\`
> 视觉令牌、无浏览器控件、无动画等硬约束见 `docs\REWRITE-BRIEF.md` §1/§6。

---

## 1. 宿主命令（3 个）

### 1.1 `build.local`
- 移植旧「进行本地编译」按钮：
  - bat = `Paths.Ad\进行本地编译.bat`；不存在 → `{ok:false,error:"未找到 进行本地编译.bat: <path>"}` + 日志。
  - 存在 → `Process.Start(new ProcessStartInfo { FileName=bat, WorkingDirectory=Paths.Ad, UseShellExecute=true })`；
    日志 `>>> 正在启动本地编译脚本...`；返回 `{ok:true}`。
- `Commands` 增加 `case "build.local"`。

### 1.2 `dll.installPatch`
- 已存在；前端设置页接上：
  - 二次确认（主题化）文案：`把 实用工具包\DLL覆盖\客户端补丁.zip 完整安装到游戏根目录：复制补丁文件（GameGaurd.dll / az.dll / 插件 DLL / 配置 ini 等）并合并 GameGaurd.ini 的 [Plugins] 列表（受管插件默认全启用，已编辑过的配置 ini 保留不覆盖）`。
  - 调用后 toast/日志展示结果（成功/失败清单），并刷新 DLL 页状态。
- 若 `客户端补丁.zip` 不存在，宿主返回中文错误，前端弹错误 toast。

### 1.3 `shell.open`
- 已有 kind `url` / `file` / `patchDir` 等；设置页链接用 `{kind:"url", value:"https://..."}`。
- 外部 URL 必须走系统浏览器；WebView 内不得导航。

---

## 2. 设置与关于页（`web/js/pages/settings.js`）

布局与旧 `MainForm.Pages.cs` BuildAbout 一致（页头 + 三张卡）：
- 页头：`设置与关于` / `版本信息 · 仓库链接 · 实用工具`。
- 关于卡（高约 128，内边距 20/12）：
  - `ServerS4A21 管理器`（14pt 粗体）
  - `v{version} · ServerS4A21-AUM`（9.5pt，muted）
  - `基于 Ant Design 设计语言构建的单机服务端管理工具：一键启停游戏服务、存档管理、增量更新、GM 工具等。`
- 仓库与链接卡（4 行，`SectionHead("仓库与链接")`）：
  - `ServerUI-仓库` → `https://github.com/118coder/ServerUI-AUM-S4A21`，显示 `github.com/118coder/ServerUI-AUM-S4A21`
  - `ServerS4A21-主源仓库` → `https://gitgud.io/rewio/ServerS4A21`
  - `GM工具-主源仓库` → `https://gitgud.io/rewio/S4A21GmTool`
  - `GM 工具` → `http://localhost:5051`
  - 左侧名称粗体 muted，右侧链接主色；整行可点；hover 下划线/变亮；`cursor:pointer`。
- 实用工具卡（3 行，`SectionHead("实用工具")`）：
  - `进行本地编译`（Primary，Build 图标）→ `build.local`；右侧说明：`调用【AUM管理组件\进行本地编译.bat】，用【AUM管理组件\latest\】的源码包重新编译【服务端】与【GM工具】`
  - `待定`（Default，Build 图标）→ 只写日志：`【待定】原"修复赛丽亚房间问题"功能已暂时移除（S4A21 已无此问题），按钮保留`；右侧说明：`功能已暂时移除（S4A21 版本已无此问题），按钮保留待定`
  - `新DLL安装`（Success，Download 图标）→ `dll.installPatch` 确认；右侧说明与旧 UI 原文一致（见 §1.2）。
- 页面在 1280×840 与 1080×700 下不裁切；卡片高度按内边距 + 行高重算（旧页曾有第 4 条链接/简介被裁的坑）。

---

## 3. 调整系统时间页（`web/js/pages/time.js`）

布局与旧 `MainForm.Time.cs` 一致：
- 页头：`调整系统时间` / `手动设置 / 快速换算 / 网络校准 · 写入系统时间需要管理员权限`。
- 信息栏：`目标时间：` + **自绘日期时间输入** + `读取系统时间`。
  - **禁止** `<input type="date">` / `<input type="datetime-local">` 的原生弹层；用 `input.ini-text` 样式 + 自定义日历/时间浮层（或自定义分段输入），点击日历图标打开主题化日历（月视图、上/下月、今天、时分秒输入、确定/取消）。
  - 格式 `yyyy-MM-dd HH:mm:ss`；非法输入禁止提交并提示。
- 操作行：`应用此时间`（Primary） + `快速调整 2006-01-01` / `2015-01-01` / `2017-01-01`（Default）。
- 网络同步卡（占满剩余高度，内容贴顶）：
  - `同步网络时间（校准为标准时间）`（Success，全宽，46px）
  - 说明：`本工具可以调节系统时间，用于无损修改相关道具物品的使用时间，避免魔改PVF导致错误而造成开发人员的困扰。`
  - `当前系统时间：yyyy-MM-dd HH:mm:ss`（每秒刷新，主色）
- 状态行：`就绪` / 成功/失败文案。
- 宿主命令：`time.get` / `time.apply {iso}` / `time.quick {year}` / `time.sync`（已有）。
  - `time.apply`/`time.sync` 可能提权（UAC），按钮 busy + 结果 toast/日志；取消提权 → 失败提示。
  - 成功后刷新 `time.get` 与系统时间显示。

---

## 4. 使用说明页（`web/js/pages/help.js`）

- 布局与旧 UI 完全一致：左侧 312px 卡片（分组标题 + 单列宽按钮，可滚动），右侧说明卡片（标题 16pt 粗体 + Divider + 正文 10pt 灰色，`white-space:pre-wrap`）。
- 文案来源：`..\ServerUI\MainForm.Pages.cs` 的 `BuildHelp()` 内 `helps` 数组（24 条，元组 `group,name,icon,title,body`）。
  - 原样搬到 `web/js/data/help.js`，保持分组顺序/按钮名/图标/标题/正文（含 `\n`、`·`、缩进）。
  - 有一处正文含 `"+_gr+"`（还原原版PVF 条目）；改为运行时 `ctx.paths.gr` 拼接。
- 左侧按钮：Default 风格，高 44，宽 252（312 卡片内），图标沿用 Ant Design linear；组标题 `—— 分组 ——` 8.5pt muted；`Wave` 无。
- 默认选中第一条「快速开始」，右侧显示其正文。
- 右侧正文可滚动；切换按钮只更新文字，不重建整个页面（避免滚动位置丢失/闪烁）。

## 5. 疑难杂症解惑页（`web/js/pages/tsh.js`）

- 与使用说明页同一布局；文案来源 `..\ServerUI\MainForm.Troubleshoot.cs` 的 `items` 数组（12 条）。
- 原样搬到 `web/js/data/tsh.js`；分组顺序/标题/正文保留。
- 默认选中第一条「关于“疑难杂症”」。

---

## 6. 验收（必须自己跑并贴）

1. `dotnet build ServerUI.Web.csproj -c Release` → 0 警告 0 错误。
2. 扩展 `tools/ui-shots.js`：
   - mock 增加 `time.get/apply/quick/sync`、`build.local`、`dll.installPatch`、`shell.open`。
   - 截图：
     `settings-dark@1280x840.png`、`settings-dark@1080x700.png`、`settings-light@1280x840.png`；
     `time-dark@1280x840.png`、`time-dark@1080x700.png`、`time-light@1280x840.png`、`time-calendar@1280x840.png`（自绘日历/时间浮层打开态）；
     `help-dark@1280x840.png`、`help-second@1280x840.png`（切到第二条）、`help-light@1280x840.png`；
     `tsh-dark@1280x840.png`、`tsh-second@1280x840.png`。
   - `layout.txt` 无 `!!`：无溢出/裁切/console.error；设置页 4 条链接全部可见；使用说明/疑难左栏滚动正常，右侧正文不被裁；时间页日历是自绘（DOM 内无 `input[type=date]`/`input[type=datetime-local]`/原生弹层）。
   - 交互断言：设置页点链接触发 `shell.open`；点新DLL安装出现确认；点待定写日志；时间页快速调整调用 `time.quick`、自定义日历选时间后 `time.apply`、读取系统时间；使用说明点第二条右侧标题变化且左栏滚动位置不跳；疑难同理。
3. `--selftest` 仍 exit 0；如新增只读命令（如 `time.get`）可加入。
4. 不部署、不改旧工程/游戏目录/GameGaurd.ini；不要动已验收的首页/存档/DLL 页。

汇报：文件、构建、截图路径、layout.txt 摘要、交互断言、偏差、未完成项。