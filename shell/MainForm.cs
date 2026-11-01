// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 工作台主窗口。
//
// 原生能力清单（Chrome --app 模式给不了的）：
//   1. 托盘常驻：关窗口不退出，右下角图标常驻，菜单含「重新载入 / 打开浏览器 / 退出」。
//   2. 原生右键菜单：按被右键对象动态生成 Win32 菜单，复制/粘贴/剪切自动连到页面选区。
//   3. 系统通知：审阅待办、任务完成走 Windows 通知中心。
//   4. 全局快捷键：Ctrl+Alt+W 随时唤起工作台。
//   5. 原生文件对话框：投递灵感时选文件。
//   6. 拖放导入：把文件拖进窗口触发处理。
//   7. 窗口状态记忆：记住上次尺寸与位置。
//   8. 深链：workbench://task/42 直接唤起。
//   9. ★ 原生标签页：审阅台里的链接不再开新浏览器窗口，而是变成壳内的标签页。
//  10. ★ VSCode 式布局：标签分组、左右/上下分裂、侧边栏、拆分到新窗口。

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkbenchShell;

internal sealed class MainForm : Form, IContextMenuHost
{
    private const string WorkbenchOrigin = Workbench.WorkbenchOrigin;
    private const string TailscaleUrl = "https://kaede.tail34f007.ts.net/";

    private readonly Workbench _bench;
    private readonly Panel _hostPanel = new() { Name = "_hostPanel", Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly NotifyIcon _tray = new();
    private readonly WindowStateStore _stateStore = new();
    private readonly string _inboxPath = @"D:\2-视频制作\1-临时灵感";
    private readonly ContextMenuStrip _trayMenu = new();
    private bool _exiting;
    private bool _menuReady;
    private readonly bool _startInTray;
    private bool _userRequestedShow;
    private int _navFailureCount;

    /// <summary>每个标签各自记住「上一次右键命中的目标串」，多标签下不能共用一个字段。</summary>
    private readonly Dictionary<TabItem, string> _pendingContextTargets = new();

    /// <summary>产品级右键菜单的构建器。</summary>
    private readonly NativeContextMenu _ctxMenu;

    /// <summary>主题菜单项（用于打勾）。</summary>
    private readonly List<(ToolStripMenuItem Item, ThemeSet Theme)> _themeItems = new();

    /// <summary>「省内存模式」菜单项（用于打勾）。</summary>
    private ToolStripMenuItem? _suspendItem;

    public MainForm(bool startInTray = false)
    {
        RunLog.Write("MainForm.ctor.begin", $"tray={startInTray}");
        _startInTray = startInTray;

        // 设置必须在画第一笔之前读进来：字号、行距、侧边栏宽度都影响初始布局。
        UiSettings.EnsureLoaded();

        Text = "宿舍机工作台";
        MinimumSize = new Size(960, 640);
        Width = 1440;
        Height = 900;
        BackColor = Color.FromArgb(0x31, 0x23, 0x2E);
        Icon = LoadIcon();

        _stateStore.Restore(this);
        EnsureVisibleOnScreen();

        // 开机自启（--tray）：先最小化，等页面就绪后再彻底隐藏。
        // 顺序很重要 —— 必须在 Restore 之后设置，否则记忆里的「最大化」会覆盖它。
        //
        // 这里刻意不动 ShowInTaskbar：WinForms 改这个属性会销毁并重建窗口句柄，
        // 而 WebView2 的浏览器控制器是挂在句柄上的，重建会让页面重新加载。
        // 隐藏的窗口本来就不占任务栏，不需要额外处理。
        if (_startInTray)
        {
            WindowState = FormWindowState.Minimized;
        }

        _bench = new Workbench(_hostPanel, showSideBar: true) { Host = this };
        // 标签关掉之后，它在右键上下文表里的残留要一起清掉。
        _bench.TabRemoved += (_, tab) => _pendingContextTargets.Remove(tab);
        _ctxMenu = new NativeContextMenu(this);
        Controls.Add(_hostPanel);

        BuildTray();
        BuildAppMenu();
        ClearUserClosedMark();

        // 主题：订阅切换事件 + 恢复上次选择
        Theme.Changed += (_, _) => OnThemeChanged();
        // 主题由 UiSettings 持久化并在 Load 时应用，这里不再单独读 theme.txt。

        // 设置里改了宽度 / 字号 / 行距：重新应用布局。
        UiSettings.Changed += (_, _) =>
        {
            _bench.RefreshSideBarWidth();
            foreach (var group in _bench.Groups) group.RefreshTheme();
        };

        // 用 Shown 而非 Load：Shown 在窗口首次显示之后触发，此时布局已完成。
        // 关键：初始化 WebView2 之前不要额外泵消息或强制建句柄，
        // 否则控件句柄会早于浏览器控制器创建，报 E_NOINTERFACE。
        Shown += async (_, _) =>
        {
            RunLog.Write("MainForm.shown");
            RunLog.Write("MainForm.bounds",
                $"bounds={Bounds} screen={Screen.PrimaryScreen?.WorkingArea} visible={Visible}");
            StartHeartbeat();
            await InitializeAsync();
            RunLog.Write("MainForm.init.done");
            DumpLayout();
            // 初始化完成再隐藏：过早 Hide 会让 WebView2 控件错过布局时机。
            // 若用户在初始化期间就点了桌面快捷符，就别再藏起来了。
            if (_startInTray && !_userRequestedShow) BeginInvoke(new Action(Hide));
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => RunLog.Write("MainForm.closed");

        // 窗口拖到不同缩放的显示器上时，重新按新 DPI 设定所有标签的栅格化比例。
        DpiChanged += (_, _) => _bench.ApplyRasterScaleAll();
    }

    /// <summary>
    /// 心跳：每 3 秒记一行。
    /// 用途是区分「进程自己退了」和「被外部杀了」——
    /// 前者日志会停在某一行，后者日志戛然而止。
    /// </summary>
    private void StartHeartbeat()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            try
            {
                RunLog.Write("heartbeat",
                    $"tabs={_bench.Groups.Sum(g => g.Tabs.Count)} groups={_bench.Groups.Count()} visible={Visible}");
            }
            catch { }
        };
        timer.Start();
    }

    /// <summary>把当前控件树布局写进日志（排查 Dock/手动 Bounds 冲突用）。</summary>
    internal void DumpLayout()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkbenchShell");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "shell-layout.txt"), LayoutDump.Dump(this));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"布局导出失败：{ex.Message}");
        }
    }

    /// <summary>宿主的公开入口（自检与二级窗口用）。</summary>
    internal Workbench Bench => _bench;

    /// <summary>
    /// 取证模式：跳过工作台首页，直接开目标标签。
    /// 少开一个标签就少建一个 WebView2 控制器，取证时更干净也更稳。
    /// </summary>
    internal bool SkipHomePage { get; set; }

    /// <summary>
    /// 静默模式：所有提示都不弹模态框、不抢焦点。
    /// 调试与取证时打开，用户正在干别的事，不能被弹窗打断。
    /// </summary>
    internal bool SilentMode { get; set; }

    /// <summary>供自检显式触发初始化（正常运行时由 Shown 事件触发）。</summary>
    internal Task InitializeForTestAsync() => InitializeAsync();

    /// <summary>在当前分组开一个标签页并等它加载（取证 / 调试用）。</summary>
    internal async Task OpenUrlInTabAsync(string url)
    {
        await _bench.OpenTab(Workbench.DeriveTitle(url), url);
    }

    // ──────────────────────────── 初始化 ────────────────────────────

    private async Task InitializeAsync()
    {
        // 幂等保护：Shown 事件与自检都会触发初始化，
        // 跑两次会开出两个一样的标签页。用同一个 Task 去重。
        if (_initTask is not null)
        {
            await _initTask;
            return;
        }
        _initTask = InitializeCoreAsync();
        await _initTask;
    }

    private Task? _initTask;

    private async Task InitializeCoreAsync()
    {
        try
        {
            await _bench.InitializeAsync();

            // 先确认 3900 服务在线，再开页面；服务未就绪就等着（开机自启常见）。
            var ready = await WaitForOriginAsync(TimeSpan.FromSeconds(45));
            if (!ready)
            {
                // 服务始终没起来：仍然加载页面，让用户看到 3900 的错误页而不是空白窗口，
                // 同时用原生对话框明确告知。
                ShowBalloon("工作台服务未就绪", "本机 3900 端口暂不可用，正在重试。");
            }

            _bench.EnsureRootGroup();

            // 第一个标签：工作台首页（创作与审阅）。
            // 取证模式（--open）跳过：直接开目标页，少建一个控制器。
            if (!SkipHomePage)
            {
                // splash: 页面首屏要等 /api/workbench，冷启动那几秒先给张占位，
                // 不要留一块纯白的 WebView2（用户会以为打开了空白页）。
                await _bench.OpenTab("创作与审阅", WorkbenchOrigin + "/#creative", splash: true);
            }

            // 侧边栏拉审阅项目清单（失败不影响主流程）。
            if (_bench.SideBarControl is not null)
            {
                // 侧边栏标题旁的刷新图标完成后，让页面（当前标签）也同步一次。
                // 页面走缓存（服务端刚被侧边栏的 fresh 填好），所以这一步只有几毫秒，
                // 但页面上的「更新时间」与卡片数据会和侧边栏完全一致。
                _bench.SideBarControl.RefreshCompleted += (_, e) => NotifyPagesReload(e.Ms, e.Force);
                _ = _bench.SideBarControl.LoadAsync();
            }

            // 托盘模式（开机自启 / 崩溃恢复）：窗口是藏起来的，
            // 得用气泡告诉用户「它活着，在托盘里」，否则会被当成没启动。
            if (_startInTray)
            {
                BeginInvoke(new Action(() =>
                    ShowBalloon("工作台已在后台运行", "托盘图标可以随时唤回；Ctrl+Alt+W 也能直接召唤。")));
            }
        }
        catch (Exception ex)
        {
            // ★ 这里原来是一个模态 MessageBox：加载失败时它会直接弹到屏幕中央，
            //   把用户正在做的事（写稿、看图、开会）顶掉。
            //   现在改成：静默模式下只记日志，其它情况用非模态的提示窗，
            //   它不阻塞消息循环，也不会抢走焦点。
            RunLog.Write("MainForm.init.failed", ex.Message);
            Debug.WriteLine($"工作台页面加载失败：{ex.Message}");

            if (SilentMode)
            {
                ShowBalloon("工作台页面加载失败", ex.Message);
                return;
            }

            var tip = new Form
            {
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                ShowInTaskbar = false,
                TopMost = false,
                Text = "宿舍机工作台",
                ClientSize = new Size(460, 150),
            };
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = $"工作台页面加载失败：\n{ex.Message}\n\n请确认本机 3900 服务已启动。",
                Padding = new Padding(16),
                AutoEllipsis = false,
            };
            tip.Controls.Add(label);
            tip.FormClosed += (_, _) => tip.Dispose();
            tip.Show();
        }
    }

    /// <summary>轮询 3900 根路径，直到返回 200 或超时。</summary>
    private static async Task<bool> WaitForOriginAsync(TimeSpan timeout)
    {
        // ★ 必须显式关掉代理。
        //   .NET 的 HttpClient 默认读环境变量里的 HTTP_PROXY / HTTPS_PROXY，
        //   本机设了这些变量，127.0.0.1:3900 的探活请求就被甩给代理，
        //   代理当然连不上本机内网端口 —— 表现为「服务明明在跑，壳却干等 45 秒」。
        //   本机服务一律直连。
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await client.GetAsync(WorkbenchOrigin + "/");
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* 服务还没起来，继续等 */ }
            await Task.Delay(1500);
        }
        return false;
    }

    // ──────────────────────────── 当前标签的便捷访问 ────────────────────────────

    /// <summary>当前激活标签的浏览器控件（可能为 null）。</summary>
    private WebView2? ActiveView => _bench.ActiveTab?.View;

    private void ForEachView(Action<WebView2> action)
    {
        foreach (var group in _bench.Groups)
            foreach (var tab in group.Tabs)
                if (tab.View is not null) action(tab.View);
    }

    // ──────────────────────────── 页面事件回调 ────────────────────────────

    internal void OnActiveTabChanged(EditorGroup group, TabItem tab)
    {
        _pendingContextTargets[tab] = string.Empty;
    }

    internal void OnTabNavigated(TabItem tab)
    {
        _menuReady = true;
        _navFailureCount = 0;
        _ = NormalizeScaleAsync(tab);
        _ = SyncActiveNavAsync(tab);
    }

    /// <summary>
    /// 把页面当前的 hash 路由同步到侧边栏的导航高亮。
    ///
    /// 场景：用户直接在页面里点链接跳到别的路由（页面上还有「查看作品编年」这类
    /// 入口），或者用前进/后退。此时侧边栏的高亮不能还停在旧项上。
    /// </summary>
    private async Task SyncActiveNavAsync(TabItem tab)
    {
        try
        {
            var core = tab.View?.CoreWebView2;
            if (core is null) return;

            var raw = await core.ExecuteScriptAsync(
                "(function(){try{return (location.hash||'').replace(/^#/,'');}catch(e){return '';}})()");
            var key = (raw ?? "").Trim('"');
            if (string.IsNullOrEmpty(key)) return;

            _bench.SideBarControl?.SetActiveNav(key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[nav] 同步侧边栏高亮失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 校准渲染比例并落盘诊断信息。
    ///
    /// 背景：用户反馈「渲染分辨率很低，比如字体」。根因是显示器 125% 缩放，
    /// 而 WebView2 的 RasterizationScale 停在 1.0（页面按 1 倍渲染再被系统拉大）。
    /// 这里每次页面加载完成都复核一次缩放比例，并把实际值写进诊断文件。
    /// </summary>
    private async Task NormalizeScaleAsync(TabItem tab)
    {
        try
        {
            var view = tab.View;
            if (view?.CoreWebView2 is null) return;

            var controller = ControllerAccess.Find(view);
            var zoom = controller?.ZoomFactor ?? 1.0;
            if (Math.Abs(zoom - 1.0) > 0.001)
            {
                if (controller is not null) controller.ZoomFactor = 1.0;
                Debug.WriteLine($"[scale] 缩放比例 {zoom} → 归一到 1.0");
            }

            _bench.ApplyRasterScale(view);

            var probe = "JSON.stringify({dpr:devicePixelRatio,iw:innerWidth,ih:innerHeight,"
                      + "ow:outerWidth,oh:outerHeight,sw:screen.width,sh:screen.height,"
                      + "vv:(window.visualViewport?window.visualViewport.scale:null)})";
            var json = await view.CoreWebView2.ExecuteScriptAsync(probe);

            var diag = new StringBuilder();
            diag.AppendLine($"时间         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            diag.AppendLine($"标签         : {tab.Title}");
            diag.AppendLine($"控件 DPI     : {view.DeviceDpi}");
            diag.AppendLine($"栅格化比例   : {(controller?.RasterizationScale ?? 0):0.000}");
            diag.AppendLine($"页面缩放     : {zoom:0.000}");
            diag.AppendLine($"浏览器探针   : {json}");
            diag.AppendLine($"控件尺寸     : {view.Width} x {view.Height} (WinForms 单位)");
            diag.AppendLine($"表单尺寸     : {Width} x {Height} (WinForms 单位)");
            diag.AppendLine($"标签总数     : {_bench.Groups.Sum(g => g.Tabs.Count)}");

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkbenchShell");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "shell-diag.txt"), diag.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scale] 诊断失败：{ex.Message}");
        }
    }

    private static Icon? LoadIcon()
    {
        // 1) 优先用 exe 里编进去的图标（托盘/任务栏/快捷方式统一）。
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var embedded = Icon.ExtractAssociatedIcon(exe);
                if (embedded is not null) return embedded;
            }
        }
        catch { /* 继续找外置图标 */ }

        // 2) 退回到 ReviewHub 的品牌图标。
        foreach (var path in new[]
                 {
                     @"E:\AgentData\ReviewHub\branding\favicon.ico",
                     @"E:\AgentData\ReviewHub\favicon.ico",
                 })
        {
            try
            {
                if (File.Exists(path)) return new Icon(path);
            }
            catch { /* 试下一个 */ }
        }
        return null;
    }

    // ──────────────────────────── 托盘 ────────────────────────────

    private void BuildTray()
    {
        _tray.Icon = LoadIcon() ?? SystemIcons.Application;
        _tray.Text = "宿舍机工作台";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _trayMenu.Items.Add("打开工作台", null, (_, _) => ShowWindow());
        _trayMenu.Items.Add("创作与审阅", null, (_, _) =>
        {
            ShowWindow();
            _ = _bench.OpenTab("创作与审阅", WorkbenchOrigin + "/#creative");
        });
        _trayMenu.Items.Add("手机访问地址", null, (_, _) =>
        {
            NativeMethods.CopyToClipboard(TailscaleUrl);
            ShowBalloon("地址已复制", TailscaleUrl);
        });
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("重新载入", null, (_, _) => ActiveView?.Reload());
        _trayMenu.Items.Add("在浏览器中打开", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo(WorkbenchOrigin) { UseShellExecute = true });
        });
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = _trayMenu;
    }

    private void ShowBalloon(string title, string body)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = body;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>给原生交互层用的轻量提示（右键菜单的操作反馈走这里）。</summary>
    public void Notify(string title, string body) => ShowBalloon(title, body);

    // ──────────────────────────── 应用菜单栏 ────────────────────────────

    private void BuildAppMenu()
    {
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        fileMenu.DropDownItems.Add("投递灵感…", null, (_, _) => PickFilesForInbox());
        fileMenu.DropDownItems.Add("打开临时灵感目录", null, (_, _) =>
        {
            Directory.CreateDirectory(_inboxPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_inboxPath}\"") { UseShellExecute = true });
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("退出", null, (_, _) => ExitApp());

        var editMenu = new ToolStripMenuItem("编辑(&E)");
        editMenu.DropDownItems.Add("复制(&C)", null, (_, _) => SendEditCommand("copy"));
        editMenu.DropDownItems.Add("剪切(&T)", null, (_, _) => SendEditCommand("cut"));
        editMenu.DropDownItems.Add("粘贴(&P)", null, (_, _) => SendEditCommand("paste"));
        editMenu.DropDownItems.Add("全选(&A)", null, (_, _) => SendEditCommand("selectAll"));

        var viewMenu = new ToolStripMenuItem("视图(&V)");
        viewMenu.DropDownItems.Add("重新载入", null, (_, _) => ActiveView?.Reload());
        viewMenu.DropDownItems.Add("放大", null, (_, _) => Zoom(0.1));
        viewMenu.DropDownItems.Add("缩小", null, (_, _) => Zoom(-0.1));
        viewMenu.DropDownItems.Add("实际大小", null, (_, _) => Zoom(0));
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add("侧边栏(&S)\tCtrl+B", null, (_, _) =>
            _bench.SetSideBarVisible(!_bench.SideBarVisible));

        // 省内存模式：把后台标签挂起以释放内存。默认关闭——
        // WebView2 的挂起是进程级的，代价与风险见 Workbench.SuspendEnabled 的注释。
        var suspendItem = new ToolStripMenuItem("省内存模式（挂起后台标签）")
        {
            Checked = _bench.SuspendEnabledNow,
        };
        _suspendItem = suspendItem;
        suspendItem.Click += (_, _) =>
        {
            var on = !_bench.SuspendEnabledNow;
            _bench.SetSuspendEnabled(on);
            suspendItem.Checked = on;
            ShowBalloon(
                on ? "已开启省内存模式" : "已关闭省内存模式",
                on ? "后台标签会被挂起；打开新标签时会自动唤醒，不影响使用。"
                   : "所有标签保持常驻，切回即时显示。");
        };
        viewMenu.DropDownItems.Add(suspendItem);
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add("手机访问地址", null, (_, _) =>
        {
            NativeMethods.CopyToClipboard(TailscaleUrl);
            ShowBalloon("地址已复制", TailscaleUrl);
        });

        // ★ 布局菜单：VSCode 式分组 / 分裂 / 新窗口
        var layoutMenu = new ToolStripMenuItem("布局(&L)");
        layoutMenu.DropDownItems.Add("向右拆分\tCtrl+\\", null, (_, _) => _bench.SplitActiveGroupVertical());
        layoutMenu.DropDownItems.Add("向下拆分\tCtrl+Shift+\\", null, (_, _) => _bench.SplitActiveGroupHorizontal());
        layoutMenu.DropDownItems.Add(new ToolStripSeparator());
        layoutMenu.DropDownItems.Add("拆分到新窗口\tCtrl+Shift+N", null, (_, _) => SplitActiveTabToNewWindow());
        layoutMenu.DropDownItems.Add("新建窗口", null, (_, _) => OpenEmptySecondaryWindow());
        layoutMenu.DropDownItems.Add(new ToolStripSeparator());
        layoutMenu.DropDownItems.Add("关闭标签\tCtrl+W", null, (_, _) => CloseActiveTab());
        layoutMenu.DropDownItems.Add("关闭其他标签", null, (_, _) => CloseOtherTabs());
        layoutMenu.DropDownItems.Add("关闭全部标签", null, (_, _) => CloseAllTabs());

        var helpMenu = new ToolStripMenuItem("帮助(&H)");
        helpMenu.DropDownItems.Add("关于本机服务", null, (_, _) =>
        {
            MessageBox.Show(this,
                "工作台服务端由本机 watchdog 托管（3900 / 3945 / 3955 / 3966 / 3967）。\n\n" +
                $"本机入口：{WorkbenchOrigin}\n" +
                $"手机入口：{TailscaleUrl}（需连接 Tailscale）\n\n" +
                "壳只负责窗口与原生交互，不接管任何数据。",
                "宿舍机工作台", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        // ★ 主题菜单：原生部分（侧边栏 / 标签栏 / 菜单栏）随主题换，
        //   页面本身是 3900 的网页，不受壳主题影响。
        var themeMenu = new ToolStripMenuItem("主题(&T)");
        foreach (var t in Theme.All)
        {
            var item = new ToolStripMenuItem(t.Name) { ToolTipText = t.Note };
            item.Click += (_, _) => SetTheme(t);
            themeMenu.DropDownItems.Add(item);
            _themeItems.Add((item, t));
        }
        themeMenu.DropDownItems.Add(new ToolStripSeparator());
        themeMenu.DropDownItems.Add("夜间模式（石墨深色）", null, (_, _) =>
        {
            var dark = Theme.All.FirstOrDefault(t => t.IsDark);
            if (dark is not null) SetTheme(dark);
        });

        var settingsMenu = new ToolStripMenuItem("设置(&S)…\tCtrl+,");
        settingsMenu.Click += (_, _) => OpenSettings();

        var menu = new MenuStrip();
        menu.Items.Add(fileMenu);
        menu.Items.Add(editMenu);
        menu.Items.Add(viewMenu);
        menu.Items.Add(layoutMenu);
        menu.Items.Add(themeMenu);
        menu.Items.Add(settingsMenu);
        menu.Items.Add(helpMenu);
        MainMenuStrip = menu;
        menu.Dock = DockStyle.Top;
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Ink;
        menu.Renderer = new ThemeRenderer();
        Controls.Add(menu);

        // 关键：这里绝对不能调 menu.BringToFront()。
        //
        // WinForms 的 DefaultLayout 按 z 序逆序布局（索引最大的先处理），
        // Dock=Top 的要先占位置，Dock=Fill 的后占剩余。
        // BringToFront() 把菜单挪到 z 序最前，于是 Fill 面板先铺满整个客户区，
        // 菜单再压在上面 —— 标签栏正好被菜单盖住，页面顶部也被切掉 30px。
        //
        // 现在的顺序：_hostPanel 先 Add（索引 0），menu 后 Add（索引 1），
        // 布局时 menu 先处理（占顶部 30px），_hostPanel 再填剩余 —— 正确。
    }

    private void Zoom(double delta)
    {
        var view = ActiveView;
        if (view is null) return;
        view.ZoomFactor = delta == 0
            ? 1.0
            : Math.Clamp(view.ZoomFactor + delta, 0.5, 2.0);
    }

    // ──────────────────────────── 主题 ────────────────────────────

    private void SetTheme(ThemeSet theme)
    {
        if (ReferenceEquals(Theme.Current, theme)) return;
        // 主题名写进统一设置：以前存在单独的 theme.txt，现在只有一个配置源，
        // 免得设置窗口和菜单各存一份、互相覆盖。
        UiSettings.Current.ThemeName = theme.Name;
        UiSettings.Apply(UiSettings.Current);
    }

    private void OnThemeChanged()
    {
        try
        {
            _hostPanel.BackColor = Theme.Bg;
            BackColor = Theme.Bg;
            ThemeApplier.Apply(this);

            foreach (var (item, t) in _themeItems)
                item.Checked = ReferenceEquals(t, Theme.Current);

            if (_suspendItem is not null)
                _suspendItem.Checked = _bench.SuspendEnabledNow;

            // 主题贯通：把新配色推给所有已打开的页面，
            // 否则暗色壳配亮粉页面，视觉上裂成两半。
            _ = _bench.BroadcastThemeAsync(Theme.Current);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"主题切换失败：{ex.Message}");
        }
    }

    /// <summary>打开设置窗口（分类分页：外观 / 侧边栏 / 标签页 / 启动 / 关于）。</summary>
    private void OpenSettings()
    {
        try
        {
            using var form = new SettingsForm(UiSettings.Current);
            form.ShowDialog(this);
            _bench.RefreshSideBarWidth();
            foreach (var group in _bench.Groups) group.RefreshTheme();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开设置失败：{ex.Message}");
            ShowBalloon("设置打不开", ex.Message);
        }
    }

    // ──────────────────────────── 标签操作 ────────────────────────────

    private void CloseActiveTab()
    {
        var tab = _bench.ActiveTab;
        if (tab is not null) _bench.CloseTab(tab);
    }

    private void CloseOtherTabs()
    {
        var active = _bench.ActiveTab;
        foreach (var group in _bench.Groups.ToList())
            foreach (var tab in group.Tabs.ToList())
                if (!ReferenceEquals(tab, active))
                    _bench.CloseTab(tab);
    }

    private void CloseAllTabs()
    {
        foreach (var group in _bench.Groups.ToList())
            foreach (var tab in group.Tabs.ToList())
                _bench.CloseTab(tab);
    }

    /// <summary>把当前标签搬到新窗口（VSCode 的 Split Window）。</summary>
    private void SplitActiveTabToNewWindow()
    {
        var tab = _bench.ActiveTab;
        if (tab is null) return;
        _ = SplitTabToNewWindowAsync(tab);
    }

    private SecondaryWindow? OpenEmptySecondaryWindow()
    {
        try
        {
            var window = new SecondaryWindow(this);
            window.Show();
            return window;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"新窗口打开失败：{ex.Message}");
            ShowBalloon("新窗口打不开", ex.Message);
            return null;
        }
    }

    /// <summary>供宿主在「拆到新窗口」时创建二级窗口。</summary>
    internal SecondaryWindow? CreateSecondaryWindow() => OpenEmptySecondaryWindow();

    /// <summary>
    /// 把一个标签拆到新窗口（VSCode 的 Split Window）。
    /// 新窗口与主窗口共享同一个 WebView2 环境，登录态与审阅勾选状态保持一致。
    /// </summary>
    internal async Task SplitTabToNewWindowAsync(TabItem tab)
    {
        // 二级窗口要自建 WebView2 环境，浏览器进程被挂起时会卡住，先唤醒
        _bench.ResumeAllSuspendedTabs();
        var window = OpenEmptySecondaryWindow();
        if (window is null) return;
        await window.ReceiveTabAsync(tab);
    }

    // ──────────────────────────── 演示模式 ────────────────────────────

    /// <summary>
    /// 演示模式：自动摆出「多标签 + 左右分裂 + 拆分到新窗口」的完整形态，然后驻留。
    ///
    /// 用途：给人看和给截图取证。代码级自检能断言分组数变了，但证不出
    /// 「两个组并排、标签条各归各位」这件事长得对不对 —— 那得看界面。
    /// </summary>
    internal async Task RunDemoAsync()
    {
        RunLog.Write("demo.step", "begin");
        await InitializeForTestAsync();
        await Task.Delay(1200);

        // 1) 开两个不同路由的标签，让标签条上有明显不同的标题
        //    hash 必须用页面真实的路由名：#automations（实时任务看板）/ #results（作品与资产）
        await _bench.OpenTab("实时任务看板", WorkbenchOrigin + "/#automations");
        await _bench.OpenTab("作品与资产", WorkbenchOrigin + "/#results");
        await Task.Delay(1200);

        // 2) 向右分裂，并把「实时任务看板」挪到新组 —— 两组各有一个标签才看得出分裂
        var newGroup = _bench.SplitActiveGroup(SplitDirection.Vertical);
        await Task.Delay(400);
        if (newGroup is not null)
        {
            var tasksTab = _bench.Groups.SelectMany(g => g.Tabs)
                .FirstOrDefault(t => t.Url.Contains("#automations", StringComparison.OrdinalIgnoreCase));
            if (tasksTab is not null) _bench.MoveTabToGroup(tasksTab, newGroup);
        }
        await Task.Delay(600);

        // 3) 把「作品与资产」拆到独立窗口 —— 验证标签真的会搬家而不是复制
        var catalogTab = _bench.Groups.SelectMany(g => g.Tabs)
            .FirstOrDefault(t => t.Url.Contains("#results", StringComparison.OrdinalIgnoreCase));
        if (catalogTab is not null) await SplitTabToNewWindowAsync(catalogTab);
        await Task.Delay(1500);

        // 4) 摆好了，留在界面上等人看 / 等截图
        RunLog.Write("MainForm.demo.ready",
            $"groups={_bench.Groups.Count()} tabs={_bench.Groups.Sum(g => g.Tabs.Count)}");
    }

    private void SendEditCommand(string command)
    {
        var view = ActiveView;
        if (view?.CoreWebView2 is null) return;
        _ = view.CoreWebView2.ExecuteScriptAsync(
            $"document.execCommand({JsonSerializer.Serialize(command)})");
    }

    // ──────────────────────────── 原生右键菜单 ────────────────────────────

    /// <summary>
    /// 右键入口。
    ///
    /// 用 GetDeferral 把「弹出菜单」推迟到上下文采集完成之后 ——
    /// 采集要问页面一句「光标处是什么」（ExecuteScriptAsync 是异步的），
    /// 不推迟的话菜单会在信息到齐之前弹出来，只能显示「页面级」那几项。
    ///
    /// 菜单本体在 NativeContextMenu 里，规则见那里的注释。
    /// </summary>
    internal async void OnContextMenu(TabItem tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        CoreWebView2Deferral? deferral = null;
        try { deferral = e.GetDeferral(); }
        catch (Exception ex) { Debug.WriteLine($"取右键延迟令牌失败：{ex.Message}"); }

        ContextMenuStrip? menu = null;
        try
        {
            e.Handled = true;   // 默认菜单已在 Workbench 里关掉，这里只走原生菜单
            menu = await _ctxMenu.BuildAsync(tab, e);
            if (menu is not null)
            {
                // 让菜单在光标处弹出，坐标由系统决定，不做像素换算。
                menu.Show(Cursor.Position);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"右键菜单构建失败：{ex.Message}");
        }
        finally
        {
            menu?.Dispose();
            try { deferral?.Complete(); }
            catch { /* 令牌可能已失效，忽略 */ }
        }
    }

    /// <summary>在一个新标签页里打开地址（当前分组）。</summary>
    public void OpenUrlInTab(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = _bench.OpenTab(Workbench.DeriveTitle(url), url);
    }

    /// <summary>在一个新的原生窗口里打开地址。</summary>
    public async Task OpenUrlInNewWindowAsync(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // 二级窗口要自建 WebView2 环境，浏览器进程被挂起时会卡住，先唤醒
        _bench.ResumeAllSuspendedTabs();
        var window = OpenEmptySecondaryWindow();
        if (window is null) return;
        await window.OpenUrlAsync(title, url);
    }

    public void SendEditCommandTo(TabItem tab, string command)
    {
        var view = tab.View;
        if (view?.CoreWebView2 is null) return;
        _ = view.CoreWebView2.ExecuteScriptAsync(
            $"document.execCommand({JsonSerializer.Serialize(command)})");
    }

    /// <summary>
    /// 取并清空该标签上一次上报的业务目标串（页面用 data-menu-target 声明）。
    /// 页面现场反查拿不到时用它兜底。
    /// </summary>
    public string TakePendingContextTarget(TabItem tab)
    {
        if (!_pendingContextTargets.TryGetValue(tab, out var value)) return string.Empty;
        _pendingContextTargets[tab] = string.Empty;
        return value ?? string.Empty;
    }

    public void PostToPage(TabItem tab, JsonObject payload)
    {
        tab.View?.CoreWebView2?.PostWebMessageAsJson(payload.ToJsonString());
    }

    // ──────────────────────────── 页面消息 ────────────────────────────

    internal void OnWebMessage(TabItem tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;

            switch (kind)
            {
                case "context-target":
                    _pendingContextTargets[tab] = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                    break;

                case "notify":
                    var title = root.TryGetProperty("title", out var nt) ? nt.GetString() ?? "工作台" : "工作台";
                    var body = root.TryGetProperty("body", out var nb) ? nb.GetString() ?? "" : "";
                    ShowBalloon(title, body);
                    break;

                case "open-file-dialog":
                    PickFilesForInbox();
                    break;

                case "reveal-path":
                    var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    break;

                case "open-tab":
                    // 页面主动要求开新标签（页面侧改成原生标签页的入口）
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        _ = _bench.OpenTab(Workbench.DeriveTitle(url), url);
                    break;

                case "workbench-refresh-done":
                    // 页面顶部的「刷新」已经打过 /api/workbench?fresh=1 并算完了。
                    // 侧边栏此刻只需要按同一份服务端缓存同步一次（2–40 ms），
                    // 展开状态、滚动位置、当前标签都不动。
                    var pageMs = root.TryGetProperty("ms", out var msEl) && msEl.ValueKind == JsonValueKind.Number
                        ? msEl.GetInt32() : 0;
                    if (_bench.SideBarControl is { } side)
                        _ = side.ReloadFromCacheAsync(pageMs);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"页面消息处理失败：{ex.Message}");
        }
    }

    // ──────────────────────────── 拖放导入 ────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    protected override async void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        try
        {
            Directory.CreateDirectory(_inboxPath);
            var stamp = DateTime.Now.ToString("MMdd-HHmmss");
            var saved = new List<string>();

            foreach (var file in files)
            {
                try
                {
                    var target = Path.Combine(_inboxPath, $"{stamp}_{Path.GetFileName(file)}");
                    File.Copy(file, target, overwrite: true);
                    saved.Add(target);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"拖放失败 {file}：{ex.Message}");
                }
            }

            if (saved.Count > 0)
            {
                ShowBalloon("已收进临时灵感", string.Join("\n", saved.Take(3)));
                var active = _bench.ActiveTab;
                if (active is not null)
                    NotifyPage(active, "inbox-received", new JsonObject
                    {
                        ["paths"] = new JsonArray(saved.Select(p => (JsonNode?)p).ToArray()),
                    });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"拖放处理失败：{ex.Message}", "宿舍机工作台",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PickFilesForInbox()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要收进临时灵感的文件",
            Multiselect = true,
            Filter = "全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Directory.CreateDirectory(_inboxPath);
            var stamp = DateTime.Now.ToString("MMdd-HHmmss");
            var saved = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    var target = Path.Combine(_inboxPath, $"{stamp}_{Path.GetFileName(file)}");
                    File.Copy(file, target, overwrite: true);
                    saved.Add(target);
                }
                catch { /* 跳过坏文件 */ }
            }
            ShowBalloon("已收进临时灵感", string.Join("\n", saved.Take(3)));
            var active = _bench.ActiveTab;
            if (active is not null)
                NotifyPage(active, "inbox-received", new JsonObject
                {
                    ["paths"] = new JsonArray(saved.Select(p => (JsonNode?)p).ToArray()),
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"收文件失败：{ex.Message}", "宿舍机工作台",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void NotifyPage(TabItem tab, string kind, JsonObject payload)
    {
        payload["kind"] = kind;
        tab.View?.CoreWebView2?.PostWebMessageAsJson(payload.ToJsonString());
    }

    /// <summary>
    /// 侧边栏刷新完成 → 通知所有工作台标签页做一次走缓存的重读。
    /// 只发给工作台自己的页面（127.0.0.1:3900），别的审阅页不该被这套联动影响。
    /// </summary>
    private void NotifyPagesReload(int ms, bool force)
    {
        foreach (var tab in _bench.AllTabs)
        {
            var url = tab.Url ?? "";
            if (!url.StartsWith(WorkbenchOrigin, StringComparison.OrdinalIgnoreCase)) continue;
            NotifyPage(tab, "workbench-reload", new JsonObject
            {
                ["ms"] = ms,
                ["force"] = force,
                ["at"] = DateTime.Now.ToString("HH:mm"),
            });
        }
    }

    // ──────────────────────────── 窗口与生命周期 ────────────────────────────

    private void ShowWindow()
    {
        if (InvokeRequired) { BeginInvoke(ShowWindow); return; }
        _userRequestedShow = true;
        // ★ 托盘模式下 WindowState 已经被设回 Normal 了（Hide 之后 Windows 改的），
        //   所以「判断是否 Minimized」不足以判断窗口是否在屏外 ——
        //   位置判定统一交给 EnsureVisibleOnScreen。
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        RunLog.Write("ShowWindow.enter",
            $"state={WindowState} bounds={Bounds} restore={RestoreBounds} visible={Visible} screen={Screen.PrimaryScreen?.WorkingArea}");

        // ★ 顺序：先 Show 再摆位置。
        //   窗口还处于隐藏状态时，WinForms 的 Bounds setter 会被直接忽略
        //   （我们实测过：设了 {100,80,1400x900} 读回来还是 {-32000,-32000,199,34}）。
        //   必须让窗口先可见，位置设置才生效。
        Show();
        EnsureVisibleOnScreen();
        RunLog.Write("ShowWindow.afterEnsure", $"bounds={Bounds} visible={Visible}");

        Activate();
        BringToFront();
    }

    /// <summary>
    /// 确保窗口完整落在某块屏幕的工作区内。
    ///
    /// ★ 这段是被真实 bug 逼出来的：
    ///   托盘模式（--tray）启动时先把 WindowState 设成 Minimized 再 Hide()，
    ///   Windows 会把窗口挪到 (-32000,-32000)，并塞一个 199x34 的伪尺寸。
    ///   之后双击桌面唤回时，WindowState 已经被设回 Normal（所以「判断是否 Minimized」那条路
    ///   走不通），而 Bounds 仍是那个屏外坐标 —— 结果窗口显示在屏幕外面，用户完全看不到。
    ///
    /// 所以判定不能只看 WindowState，必须看窗口**实际位置**；
    /// 而且修复时不能去「修补」当前 Bounds（那是 Windows 的伪值，尺寸也是错的），
    /// 得用记忆文件里的正常尺寸重新摆一次。
    /// </summary>
    private void EnsureVisibleOnScreen()
    {
        try
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;

            // 标题栏中心必须落在某块屏幕内，否则判定为「跑出去了」。
            var titlePoint = new Point(
                Bounds.Left + Bounds.Width / 2,
                Bounds.Top + Math.Min(12, Bounds.Height / 2));

            var onScreen = Screen.AllScreens.Any(s =>
                s.WorkingArea.Contains(titlePoint) &&
                s.WorkingArea.IntersectsWith(Bounds));
            if (onScreen) return;

            // ★ 用记忆里的正常尺寸重建；没有记忆就用默认 1440x900。
            //   绝不沿用当前 Bounds —— 托盘模式下它是 Windows 塞的 199x34 伪值。
            var remembered = _stateStore.LastState;
            var width = remembered is not null && remembered.Width >= 400
                ? remembered.Width : 1440;
            var height = remembered is not null && remembered.Height >= 300
                ? remembered.Height : 900;

            width = Math.Min(width, Math.Max(480, area.Width));
            height = Math.Min(height, Math.Max(360, area.Height));

            // 位置：优先用 RestoreBounds（Windows 记的「最小化前的位置」），
            // 它比 Bounds 靠谱 —— Bounds 在最小化/隐藏状态下是伪坐标。
            // 再退到记忆文件里的值，最后才居中。
            var restore = RestoreBounds;
            var hasRestore = restore.Width >= 400 && restore.Height >= 300 &&
                            restore.X > -30000 && restore.Y > -30000;

            int x, y;
            if (hasRestore)
            {
                x = restore.X;
                y = restore.Y;
            }
            else if (remembered is not null && remembered.X > -30000 && remembered.Y > -30000)
            {
                x = remembered.X;
                y = remembered.Y;
            }
            else
            {
                x = area.Left + Math.Max(0, (area.Width - width) / 2);
                y = area.Top + Math.Max(0, (area.Height - height) / 2);
            }

            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));

            // ★ 顺序不能反：窗口处于 Minimized 时，Windows 会强行把 Bounds 写成
            //   (-32000,-32000,199,34) —— 哪怕我们显式赋了新值也会被它覆盖。
            //   必须先复位成 Normal，再摆位置。
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(x, y, width, height);
            RunLog.Write("MainForm.repositioned",
                $"bounds={Bounds} area={area} restore={restore} remembered={(remembered is null ? "none" : $"{remembered.X},{remembered.Y} {remembered.Width}x{remembered.Height}")}");
        }
        catch { /* 兜底失败就按原样显示 */ }
    }

    /// <summary>
    /// 接收「再次启动时唤起已有窗口」的广播消息。
    /// 没有这段的话，第二次启动只会把消息发出去就退出，用户看到的是「双击没反应」。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.ShowRequestMessageId)
        {
            ShowWindow();
            return;
        }
        base.WndProc(ref m);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        RunLog.Write("MainForm.closing", $"reason={e.CloseReason} exiting={_exiting}");
        if (_exiting) return;
        // 关窗口只是隐藏，托盘继续常驻。
        e.Cancel = true;
        Hide();
        ShowBalloon("工作台仍在后台运行", "托盘图标可以随时唤回。");
    }

    private void ExitApp()
    {
        _exiting = true;
        PrepareForExit();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    /// <summary>
    /// 退出收尾：保存窗口状态 + 写「用户主动退出」标记。
    /// 与 ExitApp 分开是为了让自检能跑同一条真实路径，而不是复制一遍逻辑。
    /// </summary>
    internal void PrepareForExit()
    {
        _stateStore.Save(this);
        MarkUserClosed();
    }

    /// <summary>清除「用户主动退出」标记，表示本次是正常启动。</summary>
    internal static void ClearUserClosedMark()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "shell-state.txt");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 记录「用户主动退出」及时间戳。watchdog 用它区分两种情况：
    /// 用户主动关闭（标记是新的，不拉回）vs 崩溃或被杀（标记缺失，拉回来）。
    /// 文件放在 exe 同目录，方便 watchdog 直接判断。
    /// </summary>
    internal static void MarkUserClosed()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(dir)) return;
            File.WriteAllText(
                Path.Combine(dir, "shell-state.txt"),
                DateTime.Now.ToString("o") + " user-closed");
        }
        catch { /* 忽略 */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.AttachHotkeys(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.DetachHotkeys(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (msg.Msg == 0x0312) // WM_HOTKEY
        {
            ShowWindow();
            return true;
        }

        // VSCode 式快捷键
        switch (keyData)
        {
            case Keys.Control | Keys.B:
                _bench.SetSideBarVisible(!_bench.SideBarVisible);
                return true;
            case Keys.Control | Keys.Oemplus:   // Ctrl+\  (向右拆分)
            case Keys.Control | Keys.Oem5:
                _bench.SplitActiveGroupVertical();
                return true;
            case Keys.Control | Keys.Shift | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oem5:
                _bench.SplitActiveGroupHorizontal();
                return true;
            case Keys.Control | Keys.Shift | Keys.N:
                SplitActiveTabToNewWindow();
                return true;
            case Keys.Control | Keys.Oemcomma:
                OpenSettings();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 启动瞬间 WebView2 还没就绪时画一层底色，避免白屏闪烁。
        if (!_menuReady)
        {
            using var brush = new SolidBrush(Color.FromArgb(0x31, 0x23, 0x2E));
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
