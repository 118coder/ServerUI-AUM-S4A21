# v2.20 任务书：配色回归老版 + DLL 拖拽修复 + 版本号

> 工程：`E:\Game\S4A21_CN\AUM管理组件\ServerUI.Web\`
> 只改本工程；不改旧工程、不部署；构建必须 0 警告 0 错误。

---

## 1. 配色回归老版 Ant Design 深色（重点）

老版（WinForms + AntdUI）控件色与日志色是**两套**，新界面目前把日志色当成了控件色，所以绿/红/橙偏亮、背景纯黑。按下面改：

### 1.1 深色 token（`web/css/tokens.css`）

| token | 现值 | 改为 | 来源 |
|---|---|---|---|
| `--bg` | `#000000` | `#141414` | 老版页面底 |
| `--surface` | `#0E0E11` | `#1C1C1C` | 老版卡片 |
| `--surface-2` | `#15151A` | `#232323` | 悬浮/次级面 |
| `--surface-3` | `#1C1C22` | `#2B2B2B` | 分段底/悬停 |
| `--border` | `#2A2A31` | `#2B2B2B` | 老版卡片描边 |
| `--border-strong` | `#64666E` | `#424242` | Ant 深色 border |
| `--text` | `#F2F2F5` | `#DCDCDC` | 老版正文 |
| `--text-2` | `#B0B0B8` | `#B0B0B8` | 不变（Txt2） |
| `--muted` | `#8C8C94` | `#8C8C94` | 不变 |
| `--faint` | `#787880` | `#787880` | 不变 |
| `--primary` | `#1E90FF` | `#1668DC` | Ant 深色蓝 |
| `--success` | `#28A745` | `#49AA19` | Ant 深色绿 |
| `--danger` | `#DC3545` | `#DC4446` | Ant 深色红 |
| `--warning` | `#FD7E14` | `#D89614` | Ant 深色橙 |
| `--info` | `#4EC9B0` | `#13A8A8` | Ant 深色青 |
| `--gold` | `#DAA520` | `#B8860B` | 保持暗金 |

### 1.2 浅色 token

| token | 改为 |
|---|---|
| `--primary` | `#2196F3`（老版 LightColorOf Primary） |
| `--success` | `#4CAF50` |
| `--danger` | `#F44336` |
| `--warning` | `#FF9800` |
| `--info` | `#00BCD4` |
| `--border-strong` | `#9A9AA5`（不变） |

浅色背景/文字不变。

### 1.3 日志色必须保持老版原值（勿跟随控件色）

`--log-default #F0F0F5`、`--log-success #28A745`、`--log-warning #FD7E14`、`--log-error #DC3545`、`--log-info #4FC3F7`、`--log-secondary #B0B0B8`、`--log-bg #18181C` —— **保持不变**（对应旧 MainForm 的 Gn/Or/Rd/Cy/Txt2/LogBg）。

### 1.4 语义底色（rgba）同步

`web/css/shell.css` 的状态胶囊、`archive.css` 当前行、工具栏选中态等处把旧 rgba 基色改成新 token 基色：
- success `rgba(73,170,25,.14)`，hover/border 用 token
- warning `rgba(216,150,20,.14)`
- danger `rgba(220,68,70,.14)`
- primary `rgba(22,104,220,.10)`（深色）；浅色 `rgba(33,150,243,.10)`
- 日志区保持原 rgba（`rgba(255,255,255,.06)` 悬停等）不动。

### 1.5 WPF 宿主同步（`MainWindow.xaml` / `MainWindow.xaml.cs`）

- 窗口/根网格/边框/启动页/兜底页背景：深色 `#141414`，边框 `#2B2B2B`；浅色 `#FFFFFF` / `#E3E3E8`。
- Splash 图标底 `#1C1C1C`、描边 `#2B2B2B`，字母 `#1668DC`；兜底叹号 `#DC4446`；下载按钮 `#1668DC`，次要按钮底 `#1C1C1C`、描边 `#2B2B2B`。
- `MainWindow.ApplyTheme` 的 `DarkBorder` 改 `#2B2B2B`；DWM 边框色深色改为 `0x2B,0x2B,0x2B`。
- 启动日志/兜底文字颜色同步（`#DCDCDC` / `#B8B8C0` 可留）。

### 1.6 验收

- mock 与真实宿主截图里：页面底 `rgb(20,20,20)`、卡片 `rgb(28,28,28)`、开始游戏绿 `rgb(73,170,25)`、停止红 `rgb(220,68,70)`、重启橙 `rgb(216,150,20)`、开始更新蓝 `rgb(22,104,220)`、正文 `rgb(220,220,220)`。
- 日志行颜色仍为老值（绿 `rgb(40,167,69)`、橙 `rgb(253,126,20)`、红 `rgb(220,53,69)`）。
- 浅色主题：主色 `rgb(33,150,243)`、绿 `rgb(76,175,80)`、红 `rgb(244,67,54)`、橙 `rgb(255,152,0)`。
- `tools/ui-shots.js` 增加 `palette-dark` / `palette-light` 断言（读计算样式 token + 关键按钮实际背景），值为上面 RGB；日志行颜色单独断言。
- 原有全部断言保持 0 个 `!!`；深色/浅色截图全部重生成，人工看对比度不刺眼、无纯黑突兀块。

## 2. DLL 拖拽不可用（真实 WebView2）

现状：前端用 HTML5 `draggable` + `dragstart/dragover`，在 mock/CDP 合成事件下能换位，但真实 WebView2 里用户反馈拖不动。旧版明确**不走 OLE DoDragDrop**，而是手柄按下→MouseMove 阈值→实时换位→MouseUp 提交。

改为指针事件实现（与旧版同构，彻底绕开 HTML5 DnD）：

- 手柄 `pointerdown`（`setPointerCapture`）→ 记录起始 Y、行、容器；移动超过 4px 才进入拖拽态。
- `pointermove`：把指针 Y 与同组各行中线比较，跨过中线就**实时交换/插入**（受管组 GameNative 固定首位，不允许插到它之前；受管/自定义两组互不接收）。
- `pointerup` / `pointercancel` / 失焦：结束拖拽，若顺序变化调用一次 `dll.setOrder` 并 `markDirty(DIRTY_ORDER)`；未换位不算修改。
- 拖拽期间：被拖行加 `.dragging`（提升阴影/背景、`cursor:grabbing`），目标位置显示 2px 主色插入线（`.drop-before` / `.drop-after` 或等价）；禁止文本选中。
- 手柄命中区保持 28×28，`touch-action: none`；`user-select:none`。
- 删除 HTML5 `dragstart/dragover/drop` 相关代码与 `#dllDragGhost`。
- 只从手柄发起；名称/说明/开关/按钮不触发拖拽。
- 拖拽结束不要整页重绘（只重排现有行），避免闪烁。

验收：
- `tools/app-shots.js` 用真实 CDP `Input.dispatchMouseEvent`（不用 `Input.dispatchDragEvent`）拖动手柄：受管组第 2 行向下拖过 3 行后位置改变；自定义组同样可拖动；GameNative 仍为第一行且无手柄。
- 断言拖拽过程中出现 `.dragging`，结束后消失；`dll.setOrder` 只调用一次；状态行变为「顺序已调整（未应用）」。
- mock 测试同样改成指针事件（不要再用 HTML5 drag 事件模拟）；原有 `dll-drag` 断言更新。

## 3. 版本号 2.20

统一改（只改版本字面量，不动历史注释）：

- `ServerUI.Web.csproj`：`Version/AssemblyVersion/FileVersion/InformationalVersion` → `2.20` / `2.20.0.0`。
- `Bridge/AppController.cs`：`Version = "2.20"`；`StatusSnapshot`/`RefreshStatus` 里两处 `var version = "2.16"` → `"2.20"`。
- `web/js/app.js` 默认 `version: '2.20'`。
- `web/index.html`：`<title>` 与 `#tbVer` → `v2.20 · ServerS4A21-AUM`。
- `web/js/pages/settings.js` 兜底 `'2.20'`。
- `AUM管理组件\AUM-version.txt` → `2.20`（本任务允许改这一个部署目录文件）。
- 文档里“当前版本”描述改 2.20；历史记录不动。

验收：标题栏、设置页、日志首行「ServerUI 版本: 2.20」、`--selftest` 的 `version` 均为 `2.20`。

## 4. 通用

- 不改业务命令语义；不改布局尺寸；不引入动画；不加网络请求。
- 构建 0/0；`ui-shots.js` 0 个 `!!`；`app-shots.js` 0 失败；`--selftest` exit 0。
- 汇报：改动文件、构建、截图、断言输出、偏差。
