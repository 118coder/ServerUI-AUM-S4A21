# ServerUI（AUM 管理器）UI 规范记录

> 目标：所有页面视觉 / 交互 / 代码写法一致。新页面、新按钮、新弹窗一律按本规范实现。
> 基础：WinForms + AntdUI 2.4.3，`MainForm : AntdUI.Window`（圆角窗口、深色主题、抗拉伸、DPI 缩放）。

---

## 一、文件与代码组织

| 项 | 规范 |
|----|------|
| 页面拆分 | 每页独立 `partial class MainForm` 文件：`MainForm.Pages.cs`（开始/存档/设置/使用说明/日志）、`MainForm.DllExt.cs`（DLL 扩展 + 弹窗类）、`MainForm.Time.cs`（时间页）、`MainForm.Archive.cs`（存档逻辑）、`MainForm.Server.cs`（服务端/更新）、`MainForm.Troubleshoot.cs`（疑难解答） |
| 文件头注释 | 每文件顶部 `/* ==== 用途说明 ==== */`，注明包含哪些页面/方法 |
| 编码 | .cs / .md 为 UTF-8（新文件无 BOM）；**ps1 脚本必须 UTF-8 带 BOM** —— Windows PowerShell 5.1 对无 BOM 文件按 ANSI(GBK) 解码，中文注释会被误读为引号/括号等语法字符（曾致 update.ps1 整串解析错误、更新流程直接失败）；纯 ASCII 脚本（dnf_monitor/get_pid）除外 |
| 脚本校验 | 改完任何 ps1 后：① 确认 BOM=`EF BB BF`；② 用 **PowerShell 5.1 引擎** `Parser.ParseFile` 验证 0 错误（不能只用 pwsh7 验证，两者读法不同） |
| 命名 | 私有方法 `动词+名词`（`BuildDllPage`/`RefreshDllState`），页面字段带页前缀（`lbDllSt`/`_dllTbl`/`dtpTime`/`lbTimeNow`） |
| 构建 | 顺序编译：`dotnet build ServerUI.csproj -c Release` 成功后再编 `ServerUI-Win7.csproj`（共享 obj/，并行会 NETSDK1047） |

## 二、页面布局骨架

```
pgXXX.Padding = new Padding(12, 8, 12, 10);            // 页根（AntdUI.In.Panel，由 Build() 创建并加入内容区）
外层 TableLayoutPanel（Dock=Fill, BackColor=Transparent, RowCount 按页而定）
├─ 页头     行高 Absolute 58F   PageHead(标题, 副标题[, 右侧按钮…])
├─ 信息栏   行高 Absolute 44~62F（左标签/输入 + 右侧按钮）
├─ 主体     行高 Percent 100    （Card(8,2) 或卡片组）
├─ 操作行   行高 Absolute 56F   （主按钮 + 次要按钮）
└─ 状态行   行高 Absolute 26F   （lbXXXSt，左对齐小字，Mute/Faint 色）
```

- **每页第一行必须是 `PageHead(...)`**（v2.17 起）：15pt 粗体标题 + 9pt 次要色副标题，右侧可传按钮；
  页头标题 Label 必须 `Margin=0`，否则高 DPI 下 AntdUI 会因高度不足整行不绘制。
- 卡片内小节标题用 `SectionHead("…")`（主题色竖条 + 10pt 粗体），不再用裸 `Cap()`。
- 卡片之间靠 **1px 主题描边** 分层；**不要加 Shadow**（性能红线，见八节）。

- 卡片：`Card(8, 2)`（圆角 8、边距 2 的 AntdUI 面板封装）；
- 可滚动列表：`AntdUI.In.Panel { Dock=Fill, AutoScroll=true }` 内放 `TableLayoutPanel { Dock=Top, AutoSize=GrowAndShrink, BackColor=Style.Get(Colour.BgContainer) }` —— 不透明背景防滚动残影；行高用 `RowStyle(SizeType.Absolute, h)`；
- 主题取色：`Style.Get(Colour.BgContainer)` 等，不写死深色/浅色值；
- 控件坐标一律用 `TableLayoutPanel` 网格（`.Add(ctrl, col, row)`），不手工摆坐标。

## 三、控件写法

| 控件 | 规范要点 |
|------|----------|
| 按钮 `AntdUI.Button` | 辅助方法 `B(text, TTypeMini.*, "IconSvg")`；`WaveSize = 0`；无边框时 `BorderWidth = 1F`；`Cursor = Cursors.Hand`；主要操作用 `TTypeMini.Primary`，危险操作用 `TTypeMini.Error`，成功/安装类用 `TTypeMini.Success` |
| 标签 `AntdUI.Label` | 辅助方法 `L(text, 字号)`；正文 8.5~9F、名称 9.5F Bold、标题 10F Bold；次要文字 `ForeColor` 灰阶 `120~150` | 
| 开关 `AntdUI.Switch` | `Size(40,22)`；禁用原因明确时 `Enabled=false` + `Cursor.Default`；状态变更事件统一走 `_dllSyncing` 防抖标志 |
| 数字 `AntdUI.InputNumber` | 取值用 `ctl.Num.Value`（可空 decimal）转 int；区间校验后写回 |
| 日期 `AntdUI.DatePicker` | `Format="yyyy-MM-dd HH:mm:ss"` 可含时间；`Placement` 下拉；修改不回传用户输入事件（自动格式化） |
| 滚动条 | 依赖 `In.Panel.AutoScroll`，不显式加 ScrollBar 控件 |

## 四、弹窗规范（独立 `internal class ... : AntdUI.Window`）

| 项 | 规范 |
|----|------|
| 窗口 | `StartPosition = FormStartPosition.CenterParent`、`ShowInTaskbar = false`、`Font = new Font("Microsoft YaHei UI", 9F)` |
| 快捷键 | `KeyPreview = true`；`Esc` 取消；确认键不吞回车（列表内输入时回车=输入框确认） |
| 按钮 | AntdUI 按钮，底部右对齐 + 弹性 spacer；不用 `MessageBox` 风格按钮 |
| 返回 | 公开静态工厂：`static string[] Ask(Form owner, ...)`，取消返回 `null`，`using var` 释放 |
| 确认/拦截 | 功能级确认统一 `MessageBox.Show(..., "标题（功能名）", YesNo, Question)`；错误 `Error`，信息 `Information` |

现有弹窗：`IniEditorForm`（分组管理：添加分组/添加键/删除键/删除分组）、`IniAskForm`（1~2 输入框通用询问）、`DllPickForm`（删除扩展选择列表）、`AlertDialog`（AntdUI 醒目弹窗）。

## 五、文案与配色约定

- 页面标题行：`★ Px.x 页面名 ★ — 一句话说明` 注释 + 页根首标签同步；
- 按钮文案：动词开头（应用更改 / 新DLL安装 / 添加扩展 / 删除扩展 / 刷新状态 / 打开补丁包目录）；
- 状态行三段式：`结果 + 数量/位置 + 去向`，如 `受管插件已启用 5 / 7 · 自定义扩展 已勾选 2 / 2 → GameGaurd.ini`；未安装态单独给引导文案；
- 日志 `Lg(msg, color)`：成功 `Gn`、警告 `Or`、错误 `Rd`、常规 `Txt2`；前缀 `>>> [模块] 动作: 结果`；
- GameNative：必选插件锁定开关、取消勾选时应用被拦截（提示语固定）；
- 与启动器对齐的机制说明（`[Plugins]` 顺序加载、DLL/INI 均可、路径=游戏根目录文件名）在 使用说明 中保留。

## 六、DLL 扩展页行为状态机

```
_patchInstalled = 存在 GameGaurd.dll / az.dll 或 GameGaurd.ini 有 PluginN= 记录
├─ 未安装：空态提示「—— 未安装 DLL 扩展 ——」+ 引导【新DLL安装】；开关全灰；添加/删除扩展拦截
└─ 已安装：渲染 表头「已安装插件」→ 受管插件行（开关+名称+说明±编辑按钮）→ 自定义扩展分组（开关+文件名+状态+删除，开关=挂载）
```

- `RefreshDllState()`：读 ini → 计算 iniSet/custom → 同步开关（GameNative 恒开锁定）→ `RebuildDllRows(custom)` → 刷新状态行与日志；
- `ApplyDllPatch()`：仅直写 ini（`Encoding.UTF8` 写入）；`BuildPatchIni` 保留注释、受管条目按勾选重编号、自定义扩展按开关勾选写入（取消勾选 = 移除 `[Plugins]` 条目，文件保留；「添加扩展」时 = 非受管全保留）；
- `InstallPatchZip()`（设置页）：解压 `实用工具包\DLL覆盖\客户端补丁.zip` → 复制到游戏根（已存在配置 ini 不覆盖）→ 合并 ini（受管全启用）→ `_patchInstalled=true` + `RefreshDllState()`；
- 受管插件清单唯一维护点：`DllPlugins` 常量（7 项，元组：File/Name/Desc/Config）。

## 七、使用说明 / 疑难解答维护

- 条目结构：`(分组, 标题, 图标Svg, 副标题, 正文含\n与·换行)`；图标用 AntdUI 内置 `*Outlined` 系列；
- 更新文档同步：功能行为变化后，同步改「使用说明」对应条目 + 本文件 + `更新记录.md`。

## 八、性能与流畅度（v2.17 起强制）

| 项 | 规则 |
|----|------|
| 卡片阴影 | `Card()` 固定 `Shadow=0`。实测阴影占整页重绘约 60%（切页绘制 130ms → 55ms）。用 1px `BorderWidth` + 主题描边分层 |
| 控件库动画 | `AntdUI.Config.Animation = false`（Program.cs）。悬停/状态即时切换，不再为每个控件启动动画计时器 |
| 字体 | 一律 `F(字号, 样式)`（MainForm 的字体缓存）；禁止在循环/运行期重建里 `new Font` |
| 页头标题 | `Margin=0`；AntdUI.Label 在控件高度小于行高时整行不绘制（高 DPI 下 15pt 标题消失的根因） |
| 切页 | `ShowPage` 为快照优先：点击 → 立即播放缓存快照交叉淡入（~130ms）→ 动画结束后在覆盖层背后切页/绘制。不要改回"先绘制再显示" |
| 日志 | `FlushLog` 必须用 `WM_SETREDRAW` 包裹整批、加粗字体缓存一次、`_logBuilder` 随界面一起裁剪 |
| 定时器 | 空闲时不要刷新隐藏控件（进度条定时器在 `pb.Visible=false` 时直接 return） |
| 分隔条 | 日志区高度由 `LogSplitter` 拖动（6px 行），双击折叠/展开；默认 170 |
| 主题色 | 标签不要写死 `Style.Get(Colour.Text)` —— 主题切换后会残留旧色（白字白底）。用默认前景色或 `Mute`/`Faint` |
| 验证 | 改完 UI 先跑 `ServerUI.Headless` 无头自测（不弹窗/不提权/不联网），看 `layout.txt` 与页面 PNG 再部署 |


---

## 九、WebView2 界面规范（v2.16+，适用于新界面）

> 旧 1–8 节规则适用于 `ServerUI-兼容模式.exe`；新界面以本节为准，详细结构见《ServerUI-WebView2重构说明.md》。

| 项 | 规则 |
|---|---|
| 技术栈 | .NET 10 WPF 无边框窗口 + WebView2；前端全部本地 HTML/CSS/JS，禁止任何网络请求 |
| 单窗口 | 完整/极简/经典三模式在同一窗口内切几何与视图；不创建第二个 Window |
| 尺寸 | full 1080×700 起，默认 1280×840；mini 600×420 起，默认 620×470；classic 1080×700 起，默认 1280×840 |
| 切页 | 同步替换 DOM，**禁止**淡入/淡出/滑动/缩放；切页不重建侧栏与日志 |
| 日志分隔 | 10px 命中区 + 40×3 居中抓手，禁止整宽黑条/白线；默认高度 190，可拖拽/折叠 |
| 滚动条 | 自绘 8px；默认 `--border`，悬停 `--border-strong`；禁止亮白/系统滚动条 |
| 颜色 | 只用 `tokens.css` 的深/浅色 token；语义色只用于状态（成功/危险/警告/主色），不得整页铺色 |
| 原生控件 | 禁止 `<select>`、`<input type=date>`、系统 MessageBox、默认右键菜单、原生文件选择框 |
| 浏览器痕迹 | 禁用开发者工具/状态栏/浏览器加速键/缩放/新窗口；外部链接交系统浏览器 |
| 字体 | Microsoft YaHei UI / Segoe UI；正文 12–13px，页标题 22px，卡片标题 13px |
| 状态更新 | 状态推送先 JSON diff，再原地更新 `[data-bind]`；禁止每 2 秒整页重建 |
| 测试 | 改完先跑 `ui-shots.js`（0 个 `!!`）与 `app-shots.js`（真实宿主 0 失败），再部署 |

## 十、v2.20 色板（新界面）

| 用途 | 深色 | 浅色 |
|---|---|---|
| 页面底 | `#141414` | `#FFFFFF` |
| 卡片 / 日志面板 | `#1C1C1C` | `#FFFFFF` |
| 主色 | `#1668DC` | `#2196F3` |
| 成功 | `#49AA19` | `#4CAF50` |
| 危险 | `#DC4446` | `#F44336` |
| 警告 | `#D89614` | `#FF9800` |
| 正文 | `#DCDCDC` | `#1F1F23` |
| 日志文字（深浅一致） | 成功 `#28A745` / 警告 `#FD7E14` / 危险 `#DC3545` / 默认 `#F0F0F5`，底 `#18181C` | 同左 |

- 侧栏与内容区用页面底色，卡片用 `--surface`；不要整块铺卡片色。
- 控件色与日志色是两套，禁止互相替换。
