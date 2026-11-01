# 宿舍机工作台 · 原生桌面壳

把 `http://127.0.0.1:3900/` 的工作台包成真正的 Windows 桌面程序。
**服务端零改动**：页面还是原来那套 `index.html`，`hub.cjs` 仍然跑在 3900，壳只负责窗口与原生交互层。

> 这份文档取代 `D:/2-视频制作/1-调研与工具/261030-Agent生态盘点/工作台原生化方案-Tauri.md`
> （那份方案的技术判断有误 —— 它把 DeepSeek Harness 官方版当成了 Tauri 2 项目，实际是 Electron；
> 且当时选 Tauri 路线的前提「本机没有 MSVC、装 Rust 要 10~20GB」也已不适用）。

---

## 一、为什么是 .NET 8 + WebView2，不是 Tauri / Electron

| 方案 | 结论 |
|---|---|
| Tauri 2 | 需要 Rust + MSVC Build Tools（10–20 GB），本机 E 盘只剩 9 GB，且本机无 MSVC。放弃。 |
| Electron | 300 MB+ 起步、常驻内存 200–400 MB。本机内存本来就紧（出图期只剩 5~6 GB）。放弃。 |
| **.NET 8 + WebView2** | **采用。** WebView2 Runtime 156.0.4314.50 已装（零下载）；自包含单文件 exe 66 MB；常驻内存约 130 MB。 |

WebView2 Runtime 是系统组件，壳本身只负责托管浏览器内核 —— 这正是「桌面宿主 + 页面渲染」的分工，
不是「换个壳的网页套壳」：托盘、右键菜单、系统通知、全局热键、原生文件对话框、拖放、窗口状态记忆全是原生 Win32 实现。

---

## 二、代码位置

```
D:/2-视频制作/0-工作流/工作台原生壳/
  WorkbenchShell.csproj   .NET 8 + WinForms + WebView2 SDK 1.0.4314.35
  Program.cs              入口：单实例互斥、--selftest / --selftest-lifecycle / --selftest-tabs / --demo-tabs 分支
  MainForm.cs             主窗口：托盘、原生右键菜单、通知、热键、拖放、窗口记忆、布局菜单
  Workbench.cs            宿主核心：标签生命周期、分裂树、侧边栏、WebView2 环境共享、栅格化
  TabItem.cs              一个标签（标题 / URL / WebView2 控件 / 归属分组）
  TabStrip.cs             自绘标签栏（favicon、激活条、悬停 ×、中键关闭、横向滚动、拖拽）
  EditorGroup.cs          编辑分组：标签条 + 内容容器 + 拖放落点判定
  SplitLayout.cs          分组二叉树：垂直/水平分裂、手算矩形、删除分组后兄弟撑满
  SideBar.cs              侧边栏数据层：从 GET /api/workbench 拉清单、分类、搜索过滤
  SideBarPanel.cs         侧边栏自绘视觉层：导航区 + 搜索框 + 分组卡片 + 动效
  Theme.cs                4 套主题描述 + 全局门面（控件只认 Theme.Xxx，换主题零改动）
  ThemeRenderer.cs        菜单栏 / 工具条的配色渲染器
  ShellBridge.cs          壳↔页面契约：隐藏页面 .rail、注入主题变量、切 hash 路由
  ContextMenuBuilder.cs   产品级右键菜单：按图片/链接/选区/页面生成菜单项
  MenuSelfTest.cs         右键菜单自检（离线断言菜单项的有无）
  MenuShot.cs             右键菜单离屏取证（虚拟屏外渲染，不碰用户屏幕）
  UiShot.cs               整窗离屏取证：4 套主题各出一张界面图
  SecondaryWindow.cs      二级窗口：承载「拆分到新窗口」搬过来的标签
  NativeMethods.cs        P/Invoke：热键、广播消息、剪贴板、窗口状态存储
  WebView2Runtime.cs      定位系统 WebView2 运行时目录
  NativeConsole.cs        让 WinExe 自检能输出到控制台
  SelfTest.cs             自检：页面渲染 + 截图 + 退出标记生命周期
  TabSelfTest.cs          标签页链路自检：点链接→变标签、拖拽分裂、拆窗口、侧边栏
  SideBarSelfTest.cs      侧边栏自检：默认只铺 6 条、展开其余、收回、整组收起
  CloseSelfTest.cs        关闭标签自检：× / 中键 / Ctrl+W 走同一条断链
  RefreshSelfTest.cs      刷新自检：两个刷新入口都打 ?fresh=1，且展开 / 滚动 / 当前标签不被重置
  LayoutDump.cs           导出控件树真实 Bounds（排查 Dock/重叠用）
  RunLog.cs               运行日志（部署目录下 logs/shell-run.log）
  AppPaths.cs             运行期路径：日志与 WebView2 数据目录都放 exe 同级
  bridge.js               原生桥接（编译进 exe，同时复制到 ReviewHub/workbench-bridge.js）

E:/AgentData/Work/WorkbenchShell/        ← 部署目录
  WorkbenchShell.exe                    自包含单文件（62MB）
  logs/                                 运行日志
  wbdata/                               WebView2 用户数据目录（登录态 / localStorage）
  启动工作台.cmd
  旧版-宿舍机工作台-网页版.lnk          ← 旧 Chrome --app 版快捷方式（备份，可回滚）
```

---

## 三、原生能力清单

| 能力 | 实现 |
|---|---|
| 托盘常驻 | 关窗口只隐藏，进程不退；托盘菜单：打开工作台 / 创作与审阅 / 手机访问地址 / 重新载入 / 在浏览器中打开 / 退出 |
| 原生右键菜单 | 拦截 `ContextMenuRequested`，按被右键对象动态生成 Win32 菜单（图片 / 超链接 / 选区 / 页面四类） |
| 产品级右键菜单 | `ContextMenuBuilder.cs`：复制图片、复制图片地址、在资源管理器中显示、复制本地路径、另存为、新标签/新窗口打开链接、缩放子菜单 |
| 系统通知 | `NotifyIcon.ShowBalloonTip`（托盘模式启动后会提示「已在后台运行」） |
| 全局热键 | `Ctrl+Alt+W` 随时唤起工作台 |
| 原生文件对话框 | 「投递灵感…」走 `OpenFileDialog` |
| 拖放导入 | 拖文件进窗口落到 `D:\2-视频制作\1-临时灵感\` |
| 窗口状态记忆 | `%APPDATA%\WorkbenchShell\window-state.json` |
| 单实例 | 再次启动广播「唤回窗口」消息，桌面快捷方式因此永远能拉起已有实例 |
| 原生标签页 | 页面里的 `target="_blank` 被拦下变成壳内标签页 |
| VSCode 式布局 | 标签分组、左右/上下分裂、拖拽移动、拆分到新窗口 |
| 现代化侧边栏 | `SideBarPanel.cs` 自绘：圆角卡片、悬停动效、搜索框、状态点、底部连接状态 |
| 单层导航 | 壳接管工作台主导航，页面自带的 `.rail` 在壳内隐藏（页面本身零改动） |
| 多套主题 | 4 套主题（樱粉/石墨/靛蓝/雾松），切主题时把配色注入页面的 CSS 变量，壳与页面同步变色 |

---

## 三之二、侧边栏与主题（2026-10-31 新增）

### 单层导航是怎么做到的

3900 页面自带一套左侧导航（`.rail`，总览 / 实时任务看板 / 创作与审阅 / 作品与资产 / Agent / 统一设置）。
壳里再画一套就是两层重复 —— 点两处、认两套，用户还得多记一份。

现在的分工是**壳接管导航**：

```
壳的侧边栏（264px，自绘）          页面（.rail 已被隐藏）
├─ 标题「审阅项目」+ 计数 + 刷新    ├─ 页面正文铺满整个内容区
├─ 圆角搜索框
├─ 「工作台」导航区（6 项，固定区）  │  ← 唯一导航入口
├─ 审阅项目分组卡片（可折叠）
└─ 底部连接状态
```

实现方式（`ShellBridge.cs`）：

- **隐藏页面 `.rail`**：注入 CSS `.rail{display:none}` + `main{margin-left:0}`。
  只藏 `.rail` 不改 `main` 的左边距会留下一条空白。
- **壳侧边栏导航区**：`SideBarPanel.DrawNav()` 自绘，图标全是矢量路径（不依赖图标字体）。
  点击 → `Workbench.NavigateWorkbench(key)` → 改工作台标签的 `location.hash`。
- **页面 hash 变化回写高亮**：`MainForm.SyncActiveNavAsync` 读页面当前 hash，同步侧边栏选中态。

**服务端 `hub.cjs` 与 `index.html` 一行没改。** 全部靠 WebView2 运行时注入，
所以页面在浏览器里直接打开时行为完全不变，只有在壳里才收敛成单层。

### 主题贯通

页面用 CSS 变量配色（`--bg` / `--surface` / `--ink` / `--pink` …）。
壳切主题时如果只改壳自己的颜色，暗色壳配亮粉页面会裂成两半。

`ShellBridge.BuildThemeScript()` 把 `ThemeSet` 的颜色翻译成页面变量注入：

```javascript
r.setProperty(k, v[k], 'important');
```

**`!important` 是必须的** —— 页面自己有一条 `@media(prefers-color-scheme:dark)` 的 `:root` 规则，
普通 inline style 跟它同优先级时会输给它（实测暗色主题下 `--pink` 没被覆盖，
页面标题还是粉的）。加上 `important` 后实测生效：

```
pinkVar=#6ea8fe  bgVar=#14161a  eyebrow=rgb(110,168,254)
```

注入时机有两处，缺一不可：

1. `AddScriptToExecuteOnDocumentCreatedAsync` —— 文档创建时注入，否则页面首帧会用自己的默认配色闪一下；
2. `NavigationCompleted` 里重放一次 —— 导航后文档被整体替换，之前注入的样式和变量全没了。

### 4 套主题

| 主题 | 气质 | 主色 |
|---|---|---|
| 樱粉 · 明亮 | 与审阅台页面同源（默认） | `#d85580` |
| 石墨 · 暗色 | 深炭底，工程/终端气质 | `#6ea8fe` |
| 靛蓝 · 商务 | 蓝灰底，最接近传统商务软件 | `#2f6feb` |
| 雾松 · 柔和 | 低饱和青绿，克制安静 | `#2f8f7d` |

切换：原生菜单栏「主题(T)」→ 4 项带勾选。持久化在 `%APPDATA%\WorkbenchShell\theme.txt`，启动时恢复。

---

## 三之三、审阅清单口径与侧边栏分组（2026-10-31 修订）

用户反馈「待审项目好像是全量项目」，查下来是两层原因叠在一起：

| 层 | 原因 | 改法 |
|---|---|---|
| 服务端 | `workbench.cjs` 调 `getReviewPages()` 没传参数，而 `review-pages.cjs` 的 `includeArchived` 默认是 **true** | 改成 `getReviewPages({includeArchived:false})`；138 条 → 26 条 |
| 壳侧边栏 | 五类分组全是「有多少列多少」，归档的也在里面 | 重排分组，见下 |

现在 `/api/workbench` 只返回**还要人看的**审阅页（26 条）。归档数据没删，`GET /api/review-pages?scope=all` 仍返回全部 138 条 ——
页面里的「作品稿归档」与各分类历史改从那里懒加载一次（`index.html` 的 `loadReviewArchive()`）。

侧边栏分组（`SideBar.cs`）：

| 组 | 内容 | 默认 |
|---|---|---|
| 最近活跃（置顶，浅强调底色 + 左侧竖条） | 项目里按 `updated_at` 倒序取前 6 条，跨分类 | 展开 |
| 待审项目 | **项目**（不含审阅台），按更新时间倒序；**先铺 6 条**，其余折成「展开其余 N 个」 | 展开 |
| 审阅台与参考库 | 常驻入口：小红书/B站/Pinterest/Pixiv 审阅台、二游素材总库、动作参考库、提示词参考台等 | **收起** |
| 绘画 / 视频 / 素材 / 研究与记录 | 项目的空间分类 | **收起** |

**「审阅台」是独立一类**，不算待审项目。判据（`SideBar.SpaceOf` 第一档）：
落在 `reference-desks/`、`设计资产库` 下，或名字里带「审阅台 / 参考台 / 参考库 / 素材总库 / 总库 / 图鉴 / 门禁」。
这些是一直开着、随时点进去看的入口，混进待审清单会让人以为还有十几件没审 ——
现在头部计数也只算项目（27 个审阅页里 18 个项目 + 9 个前台）。

同一个项目会同时出现在「最近活跃」和它所属的空间里 —— 置顶组是快捷入口，空间组是完整目录。

「展开其余 N 个」是分组内的第二层折叠（`SideBarGroupSpec.PreviewLimit`）：

- 点那一行 → `ShowAll = true`，行数用 `Reveal` 从 6 平滑长到全部（一帧跳过去会把用户正在看的那行顶走）；
- **再点同一行 → 收回到 6 条**（这一行不会消失，文字变成「收起，只看最近 6 个」，箭头翻转）。
  展开后入口消失是个真实的坑：那样点开一次就再也回不去预览态，只能靠折叠整组绕过；
- 点组标题 → 整组收起（原有的 `Expanded`），三层状态互不干扰；
- 刷新清单时两个状态都保留（`SetGroups` 里按 Key 记忆 `Expanded` 与 `ShowAll`），
  否则 15 秒一次的接口刷新会把刚展开的列表又收回去。

归类判据在 `SideBar.SpaceOf()`，按「目录 → catalog_kind → 名称」三级判断，
不沿用页面的 `reviewCategory()`：页面那是工作流分类（素材库/截图/研究/记录），
侧边栏按「这是哪一类活」组织，两个维度不同。

已归档的项目**不进侧边栏**（客户端还兜了一层 `review_status !== 'archived'` 过滤）。
要翻归档就去页面「作品稿归档」。

### API 加速

`hub.cjs` 给 `GET /api/workbench` 加了进程内缓存（TTL 15 秒，最多 16 个键，`?fresh=1` / `?refresh=1` 跳过），
并在 `listen` 之后异步预热一次。响应头带 `x-workbench-cache`（hit/miss/shared/refresh）、`age-ms`、`build-ms`，好查。

| 场景 | 改前 | 改后 |
|---|---|---|
| 命中缓存 | —（每次都重算） | **2–18 ms** |
| 缓存过期后首次 | 1197 / 3014 ms | 1688 ms（build 1682） |
| 响应体积 | 138 KB | 47 KB |
| 重启后首访 | 冷盘 15–25 s | 预热后 58 ms |

---

## 三之四、设置窗口（2026-10-31）

菜单「设置(S)…」或 **Ctrl+,** 打开。左边分类、右边选项、底部「恢复默认 / 保存 / 取消」，
改动先写进副本，点保存才落盘并广播 —— 取消不会留下改了一半的配置。

| 分类 | 可调项 |
|---|---|
| 外观 | 主题（4 套）、字号倍率 80–140%（壳里所有原生文字）、行距倍率 85–120%（决定侧边栏行高） |
| 侧边栏 | 宽度 200–440、待审项目先显示 3–30 条、最近活跃 0–12 条（0 = 不显示该组）、标题最多字数 6–30、显示状态点 |
| 标签页 | 标签条高度 26–48、标签最大宽度 140–380 |
| 启动 | 开机自启（写 Startup 快捷方式，带 `--tray`）、启动后进托盘 |
| 关于 | 版本、程序目录、设置文件、日志目录、WebView2 数据目录、工作台地址 |

配置存在 **exe 同级 `settings.json`**（跟日志、wbdata 一个道理，见 `AppPaths` 的说明）：
往 `%LOCALAPPDATA%` 写会按「哪个 exe 在写」被拦。改坏了直接删掉，下次启动回默认值。

代码上，这些参数集中在新加的 `UiSettings`：

```
UiSettings.Current      ← 单例，带 Changed 事件
Theme.SideBarRowHeight  → Scale(26)        ← 原来写死的 26
Theme.SideBarWidth      → UiSettings.Current.SideBarWidth
Theme.MakeFont(...)     → 字体族 + FontScale
```

控件不再各存一份常量：侧边栏字体在 `UiSettings.Changed` 里重建，标签条在
`TabStrip.ApplyMetrics()` 里重设高度，主窗口再把 `SplitterDistance` 重新摆一次。

---

## 三之五、可读性、刷新与后端提速（2026-11-01）

> 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01

这一轮接的是上次会话断掉的那两张界面截图（`验证截图\新命名1`、`新命名3`）。

### 1. 侧边栏逐条诊断与改法

| 位置 | 改前 | 改后 | 怎么改的 |
|---|---|---|---|
| 正文 / 组标题 | 8.6pt（约 11.5px），中文笔画挤 | **9.4pt（约 12.5px）**，组标题同号加粗 | `_fontRow` / `_fontGroup`；`Theme.MakeFont` 统一走字号倍率 |
| 行距 | 行高 26px，组头 30px | 行高 **28px**，组头 **32px** | `Theme.SideBarRowHeight` / `SideBarGroupHeaderHeight` |
| 标题 | 10pt | 11pt | `_fontTitle` |
| 「图｜标题」全角竖线 | 竖线与两侧字几乎贴在一起，读成「图|个人作品集」 | 竖线两侧各留 **3px** | `DrawSpacedLabel`：渲染时拆「前缀 / 竖线 / 标题」三段画，**数据里的名字不动**（页面标题、悬停全文保持原样） |
| 右列日期 / 类型 | 每行按自己的文字宽度反推起点，右缘不齐 | 固定 **46px** 列，右对齐到同一条线 | `MetaColumnWidth`；行名区相应收窄 8px 间隙 |
| 右列颜色 | `Muted` | `Muted` 与 `Ink` 按 35% 混合 | 同字号下对比度从 3.2:1 提到约 5:1 |
| 分组计数徽标 | 30×16 固定宽，底色 `Soft`（近白）画在白卡片上，等于看不见 | 宽度随数字自适应（最小 22），底色 `Line`，文字 `Muted↔Ink` 82%（置顶组用强调色） | `DrawGroupCard` 里的 `DrawBadge` 调用 |
| 头部「待审 N」 | 7.4pt 浅灰裸字 | 8pt 胶囊徽标（`Ink` 字 + `Line` 底），贴在刷新按钮左侧 | `DrawHeader` |
| 标签条字形 | 已是 GDI `TextRenderer` + ClearType（见坑 21） | 纳入离屏取证 —— 以前的界面图里根本没有标签条，字形问题看不见 | `UiShot` 渲染真实 `TabStrip` 控件并拼进合成图 |
| 页面右上「刷新」 | 请求 `/api/workbench`（命中 15 秒缓存，点了常常什么都没变） | 请求 **`/api/workbench?fresh=1`**；按钮转圈 + 文字「刷新中」，完成后按钮左侧显示「刚刚更新 hh:mm · XX ms」 | `index.html` 的 `refresh(force)` + `.refresh-meta` |

字号仍可用「设置 → 外观 → 字号倍率」整体放大；行高倍率沿用 85–120%。

### 2. 刷新链路：两个入口，一份数据

| 入口 | 谁去请求 | 另一侧怎么办 |
|---|---|---|
| 页面右上「刷新」 | 页面 `fetch('/api/workbench?fresh=1')` | 完成后 `postMessage {kind:'workbench-refresh-done', ms}` → 壳的侧边栏 `ReloadFromCacheAsync`，走缓存（2–40 ms），不再重算 |
| 侧边栏标题旁 ↻ | 壳 `SideBar.LoadAsync(forceFresh: true)` | 完成后 `NotifyPagesReload` → 每个 3900 标签页收到 `{kind:'workbench-reload'}`，页面执行 `refresh(false)` 走缓存 |

刷新期间**不动**的东西（`--selftest-refresh` 逐条断言）：分组展开状态、每组「展开其余」状态、
侧边栏滚动位置、标签数量、当前标签 URL、页面滚动位置。页面刷新前后若路由没变，会把
`window.scrollY` 恢复回去。

视觉反馈：侧边栏图标绕自身中心转（60fps，每帧 14°），底部状态「刷新中…」→「已更新 · hh:mm」；
页面按钮旁「刚刚更新 hh:mm · XX ms」，4.5 秒后淡出。刷新中再点一次会提示「正在刷新，稍等一下」。

### 3. 后端提速（落实「八、已知问题」第 1 条）

改了 `E:\AgentData\ReviewHub` 三个文件，都带 `.bak-20261101-refresh` 备份：

1. `hub.cjs` `getServices()`：单条服务里 `describeLive2d`（Live2D 健康检查 650 ms）与
   `probe`（700 ms TCP 探活）原来是**串行 await**，最慢那条服务要 1.35 s。改成并行；
   整张服务表再缓存 5 秒（`fresh` 也复用）—— 刷新清单不必等探活，探活也不会被刷新反复触发。
2. `hub.cjs` `getReviewPages()`：catalog（约 580 ms）只为算一张「审阅页 → 分类」映射表，
   给映射表单独缓存 20 秒。审阅页目录扫描（约 140 ms）仍然每次重做，`fresh` 的语义不变。
3. `workbench.cjs` `config()`：每次起一个 python 子进程读设置（实测 125–146 ms），
   加 3 秒缓存，保存配置 / 暂停采集后立刻失效。

实测（本机，Intel 宿舍机，3900 本进程）：

| 场景 | 改前 | 改后 |
|---|---|---|
| `?fresh=1` 总耗时 | 1185 / 1338 / 1405 ms | **92 / 103 / 199 ms** |
| 其中 build（服务端重算） | 1156–1357 ms | **69–169 ms** |
| 命中缓存 | 26–38 ms | 22–38 ms |
| 冷启动预热 | 1195 ms | **725 ms** |
| 响应头 | `x-workbench-cache` / `build-ms` / `age-ms` | 不变（自检靠它判定 `refresh` 真的发生了） |

页面上点一次刷新（走 fresh）实测墙钟 **31–153 ms**；侧边栏刷新图标端到端 **213–305 ms**（含 JSON 解析与重建分组）。

### 4. 自检

新增 `--selftest-refresh`（`RefreshSelfTest.cs`），八条断言：

```
projectsLoaded                 清单拉回来了
userStatePrepared              展开「其余」+ 滚到 160px
sidebarRefreshRan              侧边栏刷新真的跑了（LastRefreshMs > 0）
expandedKept / revealKept      分组展开与「展开其余」状态没被重置
scrollKept                     滚动位置 160 → 160
tabsKept                       标签数 1 → 1，当前 URL 不变
serverFreshForced              /api/workbench?fresh=1 响应头 x-workbench-cache=refresh
serverCacheHits                普通请求 x-workbench-cache=hit
pageRefreshShowsFeedback       页面按钮旁出现「刚刚更新 hh:mm · XX ms」
```

报告：`验证截图\刷新自检-20261101\workbench-shell-refreshtest.json`（verdict=PASS）。

---

## 三之六、壳的边界：只对 3900 页面施加呈现契约（2026-11-01）

> 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01

壳是通用宿主，标签可以开任意地址。之前 `ShellBridge` 的三段注入（隐藏 `.rail`、注入主题变量、切 hash）
对所有页面无条件执行 —— 图片门禁确认页（`http://127.0.0.1:3958/`）是暗色设计，被灌进浅色主题后
变成一半亮一半暗。

改法：`ShellBridge.OriginGuard`，三段脚本开头都先判 `location.origin !== 'http://127.0.0.1:3900'` 就返回。
别的站点原样呈现；壳只负责窗口、标签、右键菜单、通知这些宿主能力。

取证入口：`--ui-shot <目录> --url <地址>` —— 用真实壳的 WebView2 环境打开任意页面并离屏截图。
确认页实测 `bgVar=#111417`、`inline` 为空，说明主题确实没被注入。
证据：`D:\2-视频制作\0-工作流\图片门禁\验证截图\页面-127-0-0-1-3958.png`。

---

## 三之七、右键复制：从菜单到剪贴板的实测（2026-11-01）

> 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01

### 根因（实测出来的，不是猜的）

菜单项一直是齐的，坏在按下去之后：

```csharp
[DllImport("user32.dll")]
private static extern bool SetClipboardText(string lpString);   // ← 这个导出函数不存在
```

user32 里没有 `SetClipboardText`。每次「复制图片地址 / 复制本地路径 / 复制链接 / 复制成果路径」
都会抛 `EntryPointNotFoundException: Unable to find an entry point named 'SetClipboardText'` ——
表现就是**右键点了复制，剪贴板里什么都没有**。复制图片走的是另一条路（`Clipboard.SetImage`），
所以「图片能不能复制」时好时坏，取决于那张图能不能解码。

第二处：复制图片失败时原来会**悄悄改成复制地址**（`已改为复制地址`）。用户点的是「复制图片」，
改成复制地址等于没做事。现在失败分三种明确提示（取不到数据 / 格式放不进剪贴板 / 剪贴板被占用），
不再有任何降级。

### 改了什么

| 位置 | 改动 |
|---|---|
| `NativeMethods.CopyToClipboard` | 删掉错误的 `user32!SetClipboardText` 声明，改走 `Clipboard.SetText`（内部 OpenClipboard + GlobalAlloc + SetClipboardData），失败重试 5 次 |
| `WebAsset.MapToLocalPath` | 新增 3958 的 `/img?p=&lt;绝对路径&gt;` 解码（限 E:/D: 盘），确认页的图现在有「复制本地路径」 |
| `NativeContextMenu.CopyImageAsync` | 三级取图：本地文件 → HTTP → **页面内 fetch 回传 data URL**（相对路径 / 带 cookie / 跨域图都能取）；解码失败、剪贴板被占各自提示，不降级 |
| `NativeContextMenu.SetClipboardImage` | 返回 bool，失败如实上报 |
| `review:` 卡片菜单 | 新增「复制页面路径 / 复制页面地址 / 在资源管理器中显示」，数据由 `ReviewIndex` 从 3900 的 `/api/review-pages` 反查（id → page_path / url，30 秒缓存） |
| `审阅服务.py` INJECT | 第二段注入：浏览器直接打开审阅页时，右键图片给页面内三项菜单；壳里打开时自动退出（原生菜单接管） |

### 实测矩阵（`--selftest-copy`，四个页面 × 三个动作）

| 页面 | 复制图片（剪贴板位图尺寸） | 复制本地路径（Test-Path） | 复制图片地址 |
|---|---|---|---|
| 3900 首页「创作与审阅」卡片 | ✅ 1920×1080（外部封面） | — 外链图没有本地文件，菜单里不出现（设计如此） | ✅ |
| ↳ 同一页面的审阅卡片（右键卡片本身） | — | ✅ 复制页面路径，Test-Path 通过 | ✅ 复制页面地址 + 在资源管理器中显示 |
| 3900 作品与资产 | ✅ 1920×1080 | — 同上（B 站封面） | ✅ |
| 3955 审阅页 | ✅ 1440×1360 | ✅ `D:\...\v0010_独立发缕.jpg` | ✅ |
| 3958 门禁确认页 | ✅ 864×1152 | ✅ `E:\MOSS-Training\...\CELL_P20-1_00001_.png` | ✅ |

自检结束后剪贴板里留的是最后一张位图，外面用 `Get-Clipboard -Format Image` 独立复核到 **864×1152**。

报告：`验证截图\复制自检-20261101\workbench-shell-copytest.json`（verdict=PASS，pagesFailed=[]）。

### 已知边界

- 外链图（B 站封面）没有本地文件，菜单里不出现「复制本地路径」——按「只有真能做的事才出现」的规则处理。
- SVG 不能进剪贴板位图，会明确提示改用「另存为…」。

---

## 三之八、审阅页面统一到查看器（2026-11-01）

> 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01

### 变化

审阅页原来是「一份手写 HTML 一套能力」，放大、复制、评分各写各的。现在降级成数据 + 一套查看器：

```
D:\2-视频制作\0-工作流\审阅服务\
  viewer\index.html          唯一实现（网格 / 灯箱 / 右键 / 评分 / 筛选）
  viewer\docs\<项目>.json    数据
  viewer\tools\html2doc.py  旧页转 JSON
  审阅服务.py                 3955：静态文件 + 标注存档 + /view 短链
```

**页面能力从此只在两处维护：3900 工作台页面、viewer 查看器。** 旧手写审阅页保留可打开，不再新建。

### 3955 的 /view 短链

```
http://127.0.0.1:3955/view?doc=docs/moss-style.json
```

- 服务端解析 `doc`（只允许 `viewer/docs` 下的 `*.json`，越界/不存在一律 404），
  读 `viewer/index.html`，在 `<head>` 后注入两段：`window.__docOverride`（查看器用它替代查询串）
  与既有的标注存档脚本。
- 短链下 `location.pathname` 是 `/view`（不是真实文件），所以注入脚本的页面键改成
  `window.__reviewPage || location.pathname`，由短链注入成 `/viewer/index.html` ——
  评分才能落到 `viewer/审阅存档/index__<storageKey>.json`。
- `local_path()` 增加 `/viewer/...` 的同源映射（原本只认 `/D/`、`/E/` 盘符路径）。

### 旧页转 JSON

```bash
python viewer/tools/html2doc.py --html "<旧审阅页.html>" --out viewer/docs/x.json --id x --mode score5 --kind images
python viewer/tools/html2doc.py --from-pending "E:/AgentData/Work/图片门禁状态/pending.json" --out viewer/docs/gate.json --id gate --limit 30 --mode gate --kind gate
```

优先从页面里的 `var DATA=[[文件名,标题,族,[徽标]]...]` 抽清单，没有再退回 `<img>` 标签；
门禁确认页是纯 JS 拉实时数据，走 `--from-pending`。

实测三页：

| 页面 | 转换结果 | 与原页对照 |
|---|---|---|
| MOSS 样张审阅 | 19 项，标题「MOSS 原图风格反推 · 样张审阅」 | 与手写 `moss-style.json` 的标题、文件清单**逐条一致** |
| 风格参考复现（左右对比） | 6 项（3 参考 × 2 版） | 标题与页面一致 |
| 图片门禁确认 | 30 项（来自 pending.json，含机器判读理由） | 与 `gate-pending.json` 标题一致 |

并排图：`viewer/验证截图/对比-moss.png`、`对比-compare.png`、`对比-gate.png`。

### 3900 侧边栏两条入口

| 条目 | 改为 |
|---|---|
| 图｜MOSS风格反推 | `http://127.0.0.1:3955/view?doc=docs/moss-style.json` |
| 维｜出图确认门禁 | `http://127.0.0.1:3955/view?doc=docs/gate-pending.json`（实时全量写回 pending.json 的入口仍是 `http://127.0.0.1:3958/`） |

### 验收

| 项 | 结果 |
|---|---|
| 短链渲染 | 200，15816 B，含 docOverride / reviewPage / 存档注入；两份 JSON 都能渲染 |
| 白名单 | `../README.md`、`docs/../../index.html`、`.txt`、不存在的 json → 全 404 |
| 评分落盘 | `POST /__save {page:"/viewer/index.html"}` → `viewer/审阅存档/index__moss-style-20261101-v1.json`（219 B），`/__load` 读回一致，**重启 3955 后仍在** |
| 复制图片 / 路径 | `--selftest-copy` 新增 `3955-viewer` 一页：复制图片 ✅、复制本地路径 ✅（Test-Path）、复制地址 ✅；五页 `pagesFailed=[]` |
| 壳里点卡片 | 3900 `/api/workbench` 里两条 url 已指短链、`available=true`；壳按通用 `OpenTab(url)` 打开，落在 3955 查看器 |

---

## 四、构建与部署

```bash
export DOTNET_ROOT="F:/dotnet/sdk" PATH="$PATH:F:/dotnet/sdk"
cd "D:/2-视频制作/0-工作流/工作台原生壳"

# 生产：单文件（62MB）
"F:/dotnet/sdk/dotnet.exe" publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=true -o "./publish-single"

# 取证/调试：目录式（带 WebView2Loader.dll，能跑 --ui-shot / --menu-shot）
"F:/dotnet/sdk/dotnet.exe" publish -c Release -r win-x64 --self-contained true -o "./publish-dir"

# 停掉运行中的实例后覆盖部署
cp publish-single/*.exe publish-single/*.dll E:/AgentData/Work/WorkbenchShell/
```

> **`IncludeNativeLibrariesForSelfExtract` 必须设 `false`**：设成 `true` 时
> .NET 会把 native 库解压到 `%TEMP%`，而本机对新编译的 exe 拦截了 `%TEMP%` 写入，
> 结果是 `WebView2Loader.dll` 解压失败 → 启动直接退出。

dotnet SDK 装在 **F 盘**（`F:/dotnet/sdk`）—— C 盘和 E 盘都长期接近 100%，装工具链必须显式指到 F 盘。

### 桌面入口

```
C:\Users\10939\Desktop\宿舍机工作台.lnk
  → E:\AgentData\Work\WorkbenchShell\WorkbenchShell.exe
  → 工作目录 E:\AgentData\Work\WorkbenchShell
```

用 `lnktool`（原生 `IShellLink`，不依赖 COM 自动化）创建：

```bash
cd "C:/Users/10939/AppData/Local/Temp/lnktool"
"F:/dotnet/sdk/dotnet.exe" exec "./bin/Release/net8.0-windows/lnktool.dll" \
  "C:\Users\10939\Desktop\宿舍机工作台.lnk" \
  "E:\AgentData\Work\WorkbenchShell\WorkbenchShell.exe" \
  "E:\AgentData\Work\WorkbenchShell" \
  "宿舍机工作台 · 原生桌面版" \
  "E:\AgentData\Work\WorkbenchShell\WorkbenchShell.exe,0"
```

开机自启另有 `Startup\宿舍机工作台.lnk`（带 `--tray`，静默进托盘）。

---

## 五、自检模式（改完必跑）

```bash
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-lifecycle <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-tabs <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-close <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-sidebar <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-refresh <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --selftest-copy <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --settings-shot <输出目录>
"E:/AgentData/Work/WorkbenchShell/WorkbenchShell.exe" --ui-shot <输出目录> [--all-themes] [--url <地址>]
```

所有自检窗口都摆到 (-4000, -4000) 并在屏幕外绘制，不抢前台焦点（见坑 22）。

- `--selftest`：单次命令内完成「初始化 → 加载页面 → 探测 DOM → 截图 → 退出」，输出 JSON + PNG。
  期望值：`navigationSucceeded=true`、`cardsRendered=true`、卡片 131 / 任务行 29 / 右键目标 159、连接状态「已连接本机」。
- `--selftest-lifecycle`：验证「用户主动退出标记」的写与清闭环，`verdict=PASS`。
- `--selftest-close`：关闭标签链路。开三个标签 → 关中间那个（断言 2）→ 关完（断言 0 标签、仍留 1 个空分组）
  → 空分组上新建（断言开出来的是 3900 页面、不是 about:blank），`verdict=PASS`。
  `--selftest` 的探针另外读 `pendingReviewCards` / `archiveCards`，用来确认「作品稿归档」没被接口改动弄空。
- `--selftest-sidebar`：侧边栏两层折叠。断言待审项目只铺 6 条 → 点「展开其余」后涨到全部 →
  再点同一行收回 6 条 → 点组标题整组收起。点击是直接调 `SideBarPanel.OnMouseDown`（与真实鼠标同一条命中路径），
  不是改状态变量糊过去 —— 自绘控件「画了但点不动」正是这类界面最常见的坏法。
- `--selftest-copy`：右键复制链路。四个页面各打开一个标签，取页面上第一张够大的图，
  真的去点菜单里的「复制图片 / 复制本地路径 / 复制图片地址」，然后读剪贴板实际内容（位图尺寸 / 文本 / Test-Path）。
  3900 首页还会额外右键一张审阅卡片，验证「复制页面路径 / 复制页面地址 / 在资源管理器中显示」三项。
  详见「三之七」。
- `--selftest-refresh`：刷新链路。先把侧边栏摆成「展开其余 + 滚到 160px」，再分别走
  侧边栏刷新图标与页面刷新按钮，逐条断言展开状态 / 滚动位置 / 标签数 / 当前 URL 没变，
  并用响应头 `x-workbench-cache` 确认 `?fresh=1` 真的重算过（`refresh`）而不是命中缓存（`hit`）。
  详见「三之五」第 4 节。
- `--ui-shot`：整窗离屏取证，4 套主题各一张 + 一张「刷新中」；标签条用真实 `TabStrip` 控件渲染。
  加 `--url <地址>` 时只截这个页面（用真实壳的 WebView2 环境），用来确认「壳没有改外部站点的配色」。

GUI 进程在自动化环境里会被会话回收，所以验证必须收敛到单次命令内，不能靠「跑起来再看」。

---

## 六、踩过的坑（改代码前先读这一节）

### 1. WebView2 初始化的时序 —— 最容易复发的一个坑

必须用 **回调** 推进，不能用 `async/await` 把续体甩到线程池线程（WebView2 成员只能在创建它的 UI 线程访问）。

```csharp
// 顺序不能改
form.Show();                       // 触发 Shown → 布局完成
// 紧接着绝对不要 DoEvents / PerformLayout / CreateControl
var env = await CoreWebView2Environment.CreateAsync(runtimeDir, userData);
await webView.EnsureCoreWebView2Async(env);
```

- 提前 `DoEvents`/`PerformLayout`/`CreateControl` → 控件句柄早于浏览器控制器创建 → `E_NOINTERFACE (0x80004002)` on `ICoreWebView2Controller`。
- `EnsureCoreWebView2Async(env).GetAwaiter().GetResult()` → 死锁（初始化需要 UI 线程泵消息）。
- SDK 版本必须 ≤ Runtime 版本。SDK 1.0.2903.x + Runtime 156 → 同样报 `E_NOINTERFACE`。现用 **1.0.4314.35**。
- **不要**让 exe 同级出现 `<exe名>.exe.WebView2` 目录 —— SDK 会切到 Fixed Version 模式去找里面的运行时（那里没有 `msedgewebview2.exe`）。要显式 `CreateAsync(runtimeDir, userData)` 指定系统运行时，user-data-dir 也必须与 exe 分开。

### 2. GUI 不能从 session 0 启动

`ReviewHub-Autostart` 计划任务用 `LogonType=S4U`，跑在 **session 0（服务会话）**。
从那里 `start` 出来的 GUI 进程**没有桌面**：窗口不出现、托盘图标不在 —— 表现就是「双击没反应」。
实测踩过：进程 `SessionId=0`、`MainWindowHandle=0`。

解法（已在用）：

- 新增交互式计划任务 `WorkbenchShell-Interactive`，`LogonType=InteractiveToken`，
  无触发器，仅供 `schtasks /run` 手动触发。定义见 `E:/AgentData/ReviewHub/WorkbenchShell-Interactive.xml`。
- watchdog 只负责判断，需要拉起时执行 `schtasks /run /tn "WorkbenchShell-Interactive"`，
  Task Scheduler 会把 GUI 放进已登录用户的桌面会话。

### 3. 单实例「唤回窗口」的广播必须有接收方

早期只发广播没人收 —— 桌面快捷方式双击后第二次启动立刻退出、第一个实例毫无反应，看起来就是「双击没反应」。
现在 `MainForm.WndProc` 里处理 `WorkbenchShell.ShowRequest`。

另外：后台启动（`--tray`）**不要广播**，否则开机时 Startup 快捷方式与 watchdog 同时触发，
谁先跑谁是主实例，后来的广播会把窗口硬拽出来，破坏静默开机。

### 4. 不要运行时改 `ShowInTaskbar`

WinForms 改这个属性会销毁并重建窗口句柄，WebView2 的浏览器控制器挂在句柄上，重建会让页面重新加载。
隐藏的窗口本来就不占任务栏，不需要动它。

### 5. 栅格化比例不设对 → 字体发虚（用户报过的问题）

现象：窗口里页面看着「渲染分辨率很低，字发虚」，而同样的页面在 Chrome 里是正常的。

排查路径（每一步都别跳）：

| 检查项 | 本机实测 | 结论 |
|---|---|---|
| 系统缩放 | `HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI = 120` | **125% 缩放**（2560×1440 物理 → 2048×1152 逻辑） |
| 进程 DPI 感知 | `GetProcessDpiAwareness` = 2 | PerMonitor，正常 |
| WebView2 栅格化比例 | `controller.RasterizationScale` = **1** | **← 元凶** |
| 页面 devicePixelRatio | 1 | 字形按 96dpi 栅格化，却显示在 120dpi 屏上 |

**原理**：`RasterizationScale` 决定页面按几倍栅格化。停在 1.0 时，浏览器认为自己在 96dpi 屏上，
字形用 96dpi 的点阵画出来放到 120dpi 的屏上 —— 看着就是「分辨率低、字发虚」。
设成 1.25 后 `devicePixelRatio` 变成 1.25、CSS 视口从 1440 缩到 1152，
和 Chrome 在同一台机器上的表现一致（原生 125% 就该少显示内容）。

**代码要点**：

```csharp
// WinForms 的 WebView2 控件不公开 controller，只在私有字段里。
// 按「类型」找而不是按字段名，SDK 升级改名也不会失效。
controller.ShouldDetectMonitorScaleChanges = true;   // 拖到别的显示器自动跟上
controller.RasterizationScale = webView.DeviceDpi / 96f;
```

- WinForms 控件的 `DeviceDpi` 是公开属性，直接用。
- 拖到不同缩放的显示器时要重设 → 监听 `Form.DpiChanged`。
- **注意**：`GetDpiForSystem()` 在 DPI-unaware 进程里会被虚拟化成 96，**读注册表 `AppliedDPI` 才准**。
  本次就是被这个 API 骗过一次。
- 排查期间的真实数值留在 `%LOCALAPPDATA%\WorkbenchShell\shell-diag.txt`（每次页面加载完自动写）。

### 6. 自检里等卡片的判断条件

`ExecuteScriptAsync` 返回的是**结果字面量**（如 `"131"`），不是含 `review-card` 的代码片段。
早先用 `json.Contains("review-card")` 判断，永远为假，等于每次都白等满额时长，表现为「偶发 cards=0」。

### 7. `ApplicationIcon` 才是图标来源

`favicon.ico` 在 `E:/AgentData/ReviewHub/branding/`，不在 ReviewHub 根目录（早期路径写错过，图标一直是系统默认的）。
现在图标编译进 exe，运行时用 `Icon.ExtractAssociatedIcon(Environment.ProcessPath)` 提取，托盘/任务栏/快捷方式统一。
自写 PE 解析器判断图标会误报「缺失」，以 .NET 提取结果为准。

### 8. 判断「窗口有没有唤回」别只看 `MainWindowHandle`

托盘气泡窗（NotifyIcon 的 balloon）、IME 窗口、GDI+ 窗口都算**顶层窗口**。
一旦主窗体处于隐藏态，`.NET Process.MainWindowHandle` 可能指向其中任意一个 —— 表现为
「句柄在、标题为空、矩形 174×74 @(7,52)」，看着像唤回失败，其实是查询抢在了窗口真正显示之前。

**正确查法**：枚举该进程全部顶层窗口，按「可见 + 有标题 + 尺寸合理」筛出主窗体。
用 lnktool 的 `--winlist <pid>` 就能看到全貌（正常态会有 13 个顶层窗口，只有 1 个 visible=True）：

```
hwnd=4718638 visible=True  rect=1037x1110 title='宿舍机工作台'   ← 主窗体
hwnd=3413122 visible=False rect=170x70   title=''                ← 托盘气泡窗
hwnd=5248092 visible=False rect=0x0     title='.NET-BroadcastEventWindow...'
hwnd=7017444 visible=False rect=1x1     title='GDI+ Window (WorkbenchShell.exe)'
```

**唤回闭环实测（2026-10-31，栅格化修复后）**：

| 步骤 | 预期 | 实测 |
|---|---|---|
| 1. 隐藏主窗体 | visible=False | `hwnd=4718638 visible=False rect=1037x1110` ✅ |
| 2. ShellExecute 打开桌面 .lnk | 触发广播 | `已启动 D:/桌面/宿舍机工作台.lnk` ✅ |
| 3. 3 秒后查窗口 | visible=True | `visible=True rect=1037x1110 title='宿舍机工作台'` ✅ |
| 4. 查进程数 | 仍是 1（第二实例广播后退出） | `进程总数: 1` ✅ |

> 注意第 2 步用 `ProcessStartInfo{ UseShellExecute=true }` 打开 .lnk，
> 与资源管理器双击走的是同一条 ShellExecute 路径，比手工拼 exe 路径更贴近真实双击。

### 9. 新编译的 exe 写不进 `%LOCALAPPDATA%` → 控制器创建报 0x800700AA（最阴的一个坑）

**症状**：壳启动后第一个标签就弹「工作台页面加载失败：请求的资源在使用中。(0x800700AA)」，
页面空白，侧边栏「共 0 项」。

**根因**（三层，逐层剥）：

1. `RunLog` 往 `%LOCALAPPDATA%\WorkbenchShell\shell-run.log` 写日志抛
   `UnauthorizedAccessException`（Access to the path ... is denied）。
   而 PowerShell / Python 写**同一路径**是通的 —— 说明拦截是按
   「哪个可执行文件在写」来的，不是目录 ACL。
2. WebView2 的浏览器子进程（`msedgewebview2.exe`）同样受这条规则约束，
   用户数据目录写不进去 → `CreateCoreWebView2ControllerAsync` 抛
   `COMException 0x800700AA`（请求的资源在使用中）。
3. 更隐蔽的一层：**孤儿浏览器进程**。父进程被强杀后，`msedgewebview2.exe`
   还活着并独占着用户数据目录 —— 这本身就是 0x800700AA 的标准成因。
   查法：

```powershell
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  Where-Object { $_.CommandLine -match 'WorkbenchShell' } |
  ForEach-Object { $_.ProcessId }
```

**修法**：日志与 WebView2 数据目录都挪到 **exe 同级目录**（`AppPaths.cs`）。
exe 部署到哪儿，数据就跟到哪儿，权限跟程序一致，不再受系统按路径设限的影响。

> 目录名刻意不叫 `<exe名>.WebView2` —— WebView2 SDK 看到 exe 同级存在同名
> `.WebView2` 文件夹会自动切「Fixed Version」部署模式，去找那个目录里
> 根本不存在的 `msedgewebview2.exe`。

**测试纪律**：每次改完代码测之前，先清残留进程：

```bash
taskkill //F //IM WorkbenchShell.exe
# 再按上面那条 PowerShell 找并杀掉指向 WorkbenchShell 的浏览器孤儿
```

### 10. `HttpClient` 默认走环境变量里的代理 → 本机服务请求全废

本机设了 `HTTP_PROXY` / `HTTPS_PROXY`，.NET 的 `HttpClient` 默认读它们，
于是 `127.0.0.1:3900` 的请求被甩给代理 —— 代理连不上本机内网端口。

表现是「服务明明在跑，壳却干等 45 秒」或者「项目列表一直是空的」，
报 `The request was canceled`。

**修法**：本机服务一律直连。

```csharp
using var handler = new HttpClientHandler { UseProxy = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
```

**排查陷阱**：这个坑修过一处（`WaitForOriginAsync`）就以为完事了，
结果 `SideBar.LoadAsync` 里还有一份同样的写法，又踩了一次。
**凡是访问 `127.0.0.1` 的 `HttpClient`，一律显式 `UseProxy = false`。**

### 11. WebView2 的右键菜单必须用 deferral

`CoreWebView2ContextMenuRequestedEventArgs` 在事件处理里是**同步**的，
但我们要等页面脚本回传上下文（图片 src、链接 href、选区文本）才能决定菜单项。

正确写法：

```csharp
var deferral = e.GetDeferral();
try
{
    e.Handled = true;                       // 必须在 deferral.Complete() 之前设
    var menu = await BuildMenuAsync(tab, e);
    menu.Show(Cursor.Position);
}
finally
{
    deferral.Complete();
}
```

`e.Handled = true` 若放在 `Complete()` 之后，浏览器默认菜单会先弹出来。

### 12. 单文件发布时 `IncludeNativeLibrariesForSelfExtract` 不能开

设成 `true` 时 .NET 把 native 库解压到 `%TEMP%`，本机对新编译的 exe 拦截了
`%TEMP%` 写入 → `WebView2Loader.dll` 解压失败 → 启动直接退出（exit 160）。

生产用 `-p:IncludeNativeLibrariesForSelfExtract=false`，
取证/调试用目录式发布（带 `WebView2Loader.dll`）。

另外：目录式发布时 `ApplicationConfiguration.Initialize()` 也会往 `%TEMP%`
写临时清单文件，同样被拦。取证脚本里要设 `TMP` / `TEMP` / `TMPDIR` 指向
exe 同级的 `tmp/` 目录。

### 13. 取证绝对不能碰用户的屏幕

用户明确反馈过：「你就不能在屏幕外面截图吗？已经影响我工作了」。

常规取证的做法（窗口摆到屏幕中间 → 置顶 → 移动鼠标 → 右击 → 截全屏）
会把用户的桌面抢走。正确做法：

- **宿主窗口放 `(-4000, -4000)`**（虚拟屏外），加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`
  —— 窗口存在但永不抢焦点、不进任务栏；
- **自绘控件用 `DrawToBitmap`** 拿像素 —— 与它在不在屏上无关；
- **WebView2 用 `CapturePreview()`** 抓控制器自己的渲染位图；
- **ToolStrip 菜单在 `(-6000,-6000)` 处 `Show` 一次**让布局完成测量，
  再用自绘画进 Bitmap（ToolStrip 没有公开的 `Render(Graphics)`）。

`UiShot.cs` / `MenuShot.cs` 就是这套。

### 14. 页面 CSS 变量注入要带 `!important`

页面自带 `@media(prefers-color-scheme:dark){:root{--pink:#ef91b0}}`，
普通 inline style 与它同优先级时会输给它 —— 表现为「暗色主题下页面标题还是粉的」。
必须 `setProperty(k, v[k], 'important')`。

验证方式（读回实际生效值，别靠眼睛看截图）：

```csharp
getComputedStyle(document.documentElement).getPropertyValue('--pink')
getComputedStyle(document.querySelector('.eyebrow')).color
```

### 15. 窗口「跑到屏幕外面」——三个叠加的原因（用户报过）

用户反馈「双击图标，窗口开在屏幕外面」。查下来是三个独立问题叠在一起，
每一个单独都能造成这个现象：

**① 托盘模式会让 Windows 把窗口挪到 (-32000,-32000)**

`--tray` 启动时先设 `WindowState = Minimized` 再 `Hide()`，
Windows 会把窗口坐标改成 `(-32000,-32000)`，并塞一个 **199×34 的伪尺寸**。

致命的地方在于：**唤回时 `WindowState` 已经被设回 `Normal`**（`Hide()` 之后 Windows 改的），
所以 `if (WindowState == Minimized)` 这条判断**不会触发**，
窗口就带着 -32000 的坐标显示出来了 —— 屏幕外，完全看不见。

→ 判断「窗口在不在屏外」**不能看 `WindowState`，必须看实际位置**。

**② 隐藏状态下设 `Bounds` 会被静默忽略**

WinForms 在窗口不可见时，`Bounds` setter 直接不生效。
实测：设了 `{100,80,1400,900}`，读回来还是 `{-32000,-32000,199,34}`。

→ **必须先 `Show()`，再摆位置**。

**③ 窗口位置记忆只钳制了 X/Y，没钳制宽高**

```csharp
// 错的：宽高没管
var x = Math.Clamp(state.X, area.Left, ...);
var y = Math.Clamp(state.Y, area.Top, ...);
form.Bounds = new Rectangle(x, y, state.Width, state.Height);  // ← 1388 可能 > 屏幕 1152
```

记忆里存着 `Height=1388`，屏幕逻辑高只有 1152 → 底部 236px 在屏幕外。

→ 宽高要一起钳：`Math.Min(state.Height, area.Height)`。

**④ 顺带一个坑：窗口位置记忆写不进 `%APPDATA%`**

`WindowStateStore` 原本写 `%APPDATA%\WorkbenchShell\window-state.json`，
和第 9 条一样被「按 exe 来源拦截」的规则挡住（`UnauthorizedAccessException` 被 catch 吞掉）。
写不进去 → 恢复时永远读一份旧值 → 旧值里存着屏外坐标 → **每次启动都跑出屏幕**。
已改到 exe 同级（`AppPaths.BaseDir`），并保留从旧路径迁移一次。

现在的三层防护：

| 层 | 位置 | 作用 |
|---|---|---|
| 恢复时钳制 | `WindowStateStore.Restore` | 宽高 + 位置一起夹进工作区 |
| 启动时兜底 | `MainForm.EnsureVisibleOnScreen()` | 位置在屏外就用记忆里的正常尺寸重建 |
| 唤回时兜底 | `MainForm.ShowWindow()` | `Show()` **之后**才摆位置 |

判定「在屏外」的标准：窗口标题栏中心点必须落在某块屏幕的工作区内。
另外 `WindowStateStore.Save` 现在会**拒绝存** `(-32000,-32000)` 和 `199×34`
这类 Windows 伪值 —— 否则这一轮存进去，下一轮启动照旧跑出屏幕。

验证方法（别截图，用日志）：

```bash
grep -aE "ShowWindow|repositioned|MainForm.bounds" logs/shell-run.log
# 期望：ShowWindow.enter 的 bounds 是屏外 → afterEnsure 的 bounds 在屏幕内
```

### 16. 自绘列表的坐标：裁剪区与内容不在同一个原点

侧边栏的分组列表用 `Graphics.TranslateTransform` + `SetClip` 做滚动，
两处坐标系不一致时，**内容会整体画在裁剪区上方，只露出中段** ——
表现是「第一条记录被切了一半，滚轮也滑不上去」（内容其实在，只是被裁了）。

```csharp
// 错的：裁剪区用设备坐标（ListArea.Y=320），内容从 y=12 开始画
g.SetClip(area);                       // 设备坐标
g.TranslateTransform(0, -_scrollY);    // 内容原点没对齐
```

→ 内容原点必须平移到列表区原点，裁剪区则固定在可视区：

```csharp
g.TranslateTransform(0, area.Top - _scrollY);
g.SetClip(new Rectangle(area.X, _scrollY, area.Width, area.Height));
```

命中区也要跟着换算：`h.Rect.Offset(0, area.Top - _scrollY)`（只减 `_scrollY` 会整体偏一个 `area.Top`）。

**排查这类「被切一半」的问题，别靠眼睛看截图 —— 往绘制代码里塞一行日志**：

```csharp
RunLog.Write("Sidebar.drawlist",
    $"groups={_groups.Count} scrollY={_scrollY} area={area} firstLabel={...}");
```

一眼就能看出 `area.Y=320` 和内容起点 `y=12` 差了 320。

### 17. 侧边栏导航区高度不能写死

导航区高度原本是常量 `SideBarNavHeight = 236`，
而实际内容是 `标签(24) + 6 项 × (行高 32 + 间隔 2)` —— 写死的值和绘制逻辑各走各的，
第 6 项「统一设置」被从中间切断，看起来像「有条记录显示不出来」。

→ **几何由内容算出来**，并且和绘制步进共享同一批常量：

```csharp
private int NavHeight =>
    NavLabelHeight + ShellBridge.NavItems.Length * (NavRowHeight + NavRowGap) + NavBottomPad;
```

同理，窗口太矮时不能把导航压成半截 —— 放不下的项整项不画（宁可少一项，也不要切成半截）。

### 18. 「× 点了没反应」—— 事件发了，但没人订阅

**症状**：标签条上的 × 点了没反应，中键、Ctrl+W 也一样；标签越开越多关不掉，只能重启窗口。

**根因**：链路在半路断了，而且断得很安静。

```
标签条 × → TabStrip.OnMouseDown → TabClosed 事件 → EditorGroup.CloseTab
                                            ↓
                              TabClosed?.Invoke(this, tab)   ← 宿主从没订阅它
```

`EditorGroup.TabClosed` 这个事件从来没有订阅者。**事件 Invoke 出去没人接不会报错**，
所以点击看着像「没响应」，其实代码走完了。

**改法**：关闭只有一个入口，交给宿主。

```csharp
// EditorGroup
private void CloseTab(TabItem tab)
{
    if (Host is not null) { Host.CloseTab(tab); return; }   // 唯一入口
    RemoveTab(tab);
}
```

`Workbench.CloseTab` 负责：记住相邻标签 → `group.RemoveTab`（内含 WebView2.Dispose）→ 从 `_allTabs` 划掉 → 激活相邻 → 发 `TabRemoved`。

**顺带两个边界**，不处理同样会让人以为「窗口坏了」：

| 边界 | 错误做法 | 现在 |
|---|---|---|
| 关掉最后一个标签 | `GroupEmpty` → `RemoveGroup` 把根分组也拆了，`_root=null`，窗口只剩底色 | `Workbench.RemoveGroup` 在只剩一个分组时直接返回，留一个空分组显示「双击这里新建标签」 |
| 空分组上新建 | 双击只发 `TabCreateRequested`，同样没人订阅 → 什么都不发生；即便接了也不该开 about:blank | `EditorGroup` 直接调 `Host.NewTab()`，打开 `3900/#creative` |

**验证**：`--selftest-close`，五条断言全绿才算过。别用「我看它关掉了」当证据 ——
这类 bug 的特征就是「有时候像关了」。

### 19. 启动首标签的纯白 WebView2

3900 页面首屏要等 `/api/workbench`（冷启动几秒），这段时间 WebView2 控件是纯白 ——
用户看到的就是「自动打开了一个空白页」。

现在首个标签会先 `NavigateToString` 一张本地占位（`Workbench.BuildSplashHtml()`，配色取当前主题），
120ms 后再导航到真实地址。占位页不依赖服务端，服务没起来时也能看到「正在打开」。

### 20. 搜索框光标「疯狂闪烁」—— 光标不能挂在 60fps 的动画定时器上

侧边栏所有动效共用一个 16ms 的 `Timer`，光标闪烁原本也塞在它的每帧回调里：

```csharp
_caretOn = !_caretOn;          // 每 16ms 翻转一次
if (_searchFocused) _dirty = true;
```

结果一秒翻三十多次，看着像故障；而且 `_dirty` 恒为 true，整块侧边栏每帧重绘，
白烧 CPU。

现在光标单开一个 **530ms** 的定时器（`_caretBlink`），只在搜索框真的聚焦时跑，
失焦、回车、Esc 都停；任何按键把相位重置到「可见」再重新计时（和系统输入框一致）。

### 21. 标签页文字发虚、锯齿 —— GDI+ 的默认抗锯齿不是 ClearType

`Graphics.DrawString` 默认走 GDI+ 的灰度抗锯齿，小字号中文笔画边上会有一圈脏边，
在高 DPI 屏上尤其明显。侧边栏早先设了 `TextRenderingHint.ClearTypeGridFit`，
标签条没设 —— 所以同一窗口里侧边栏清楚、标签糊。

两处一起改：

```csharp
g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

// 标题改用 GDI（TextRenderer），它直接用系统 ClearType，且自带省略号
TextRenderer.DrawText(g, tab.Title, textFont, textRect, color,
    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
```

**测量也要换同一套**：`MeasureTabWidth` 原来用 `Graphics.MeasureString` 估宽，
换成 `TextRenderer.MeasureText`，否则算出来的标签宽度和画出来的文字对不上，
关闭按钮会压住最后一个字。

---

### 22. 屏幕外自检窗口收不到 WM_PAINT → 自绘面板的命中区是空的（2026-11-01）

自检窗口为了不抢前台焦点，被摆到 (-4000, -4000)。Windows 对屏幕外的窗口不一定派 `WM_PAINT`，
而 `SideBarPanel` 的命中区（`_hits` / `_revealHits`）和内容高度（`_contentHeight`）**都是在 `OnPaint` 里生成的**。
表现：`--selftest-sidebar` 报「没有画出展开其余那一行的命中区」，`--selftest-refresh` 的滚动位置一直是 0。

做法：读命中区 / 滚动之前先 `ctl.DrawToBitmap(bmp, rect)` 强制真实绘制一帧（`UiShot` 用的同一条路径）。
`SideBarPanel.ScrollTo` 里也补了一句：`_contentHeight <= 0` 时先 `Refresh()` 再夹紧。

### 23. 离屏合成图里标签条被撑成 600px 高 → 页面整体下移半屏（2026-11-01）

`UiShot.RenderControl` 原来对所有控件都取 `Math.Max(ctl.Height, 600)`。侧边栏是整列，取 600 合理；
标签条只有 34px，位图却是 600px 高，下方 566px 是空白 —— 合成时把页面整块往下推，
看起来像「页面顶部空了半屏」，很容易被误判成页面样式坏了。

做法：`RenderControl(ctl, minHeight)` 增加参数，标签条传它自己的高度。合成图尺寸也按
`menuH + max(侧边栏高, 标签条高 + 页面高)` 算。

---

## 七、开机与自恢复链条

```
计划任务 ReviewHub-Autostart（Boot + Logon + 每 1 分钟，session 0）
  └─ watchdog.cmd
       ├─ 3900/3945/3967/3955 四个服务探活补拉
       └─ 壳不在 + 无「用户主动退出」标记 → schtasks /run "WorkbenchShell-Interactive"

计划任务 WorkbenchShell-Interactive（InteractiveToken，仅手动触发）
  └─ WorkbenchShell.exe --tray   → 进用户桌面会话，静默进托盘

Startup 文件夹快捷方式（登录时）
  └─ WorkbenchShell.exe --tray   → 静默进托盘
```

**「用户主动退出」的语义**（两端都要成立才算对）：

| 场景 | 标记 `shell-state.txt` | watchdog 行为 |
|---|---|---|
| 托盘菜单「退出」 | 写入（`PrepareForExit()`） | 不拉回 |
| 崩溃 / 被强杀 | 无（启动时已清） | 拉回 |
| 正常启动 | 启动时清掉 | — |

实测：崩溃 → 9~39 秒恢复；标记存在 → 75 秒内不拉回。

---

## 八、已知问题 / 下一步

1. **`/api/workbench` 的 `?fresh=1` 已经压到 100 ms 量级**（2026-11-01，见「三之五」第 3 节）：
   `getServices()` 单条服务内的两条探活改并行并整体缓存 5 秒、catalog 分类映射缓存 20 秒、
   `config()` 缓存 3 秒。改前 fresh 1185–1405 ms，改后 92–199 ms；命中缓存 22–38 ms。
   再往下压要看 `getReviewPages()` 的逐条 realpath/stat/读 HTML 头（约 140 ms，属于 fresh 的本意，暂不动）。
   **不要再动 `getReviewPages` 的 `includeArchived` 默认值** ——
   默认 `true` 是底层函数的行为，`/api/review-pages` 路由自己按 `scope=all` 传参；
   只有 `/api/workbench` 显式传 `false`（待审清单不该含归档）。
2. 右键菜单的「复制图片」走 `Clipboard.SetImage`，剪贴板被别的程序占用时需要重试（现为 5 次）。
   超时后会静默失败，没有 toast 提示 —— 可以加一个「复制失败」的轻提示。
3. 拖放导入只落盘到临时灵感目录，还没和任务系统打通。
4. 侧边栏的「工作台」导航目前只覆盖 3900 首页的 6 个路由。
   页面以后加新路由时，`ShellBridge.NavItems` 要同步加一项，否则新入口没有侧边栏入口。
5. 主题目前只改了颜色，没有字体密度 / 圆角半径的分档。
   要做「紧凑 / 宽松」两档，得在 `ThemeSet` 里加度量字段并让 `SideBarPanel` 读它。
