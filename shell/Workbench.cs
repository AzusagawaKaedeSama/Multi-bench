// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 工作台宿主：管理标签、分组、分裂树，以及每个标签背后的 WebView2 生命周期。
//
// 分层（自外向内）：
//   Workbench          ← 一个窗口一个实例；持有布局树与共享的 WebView2 环境
//   ├── SideBar        ← 左侧审阅项目树（可选显示）
//   └── SplitLayout    ← 分组树：一棵二叉树，节点要么是分组要么是分割
//       └── EditorGroup ← 一组标签 + 内容区
//           └── TabItem ← 一个 WebView2 控件
//
// WebView2 环境共享：同一窗口内所有标签用同一个 CoreWebView2Environment（同一 userDataFolder），
// 这样它们共享登录态与 localStorage —— 审阅台的勾选状态才能在标签间保持一致。
// 但每个标签仍是独立的浏览器实例（独立地址栏、独立历史），这是必须的。

namespace WorkbenchShell;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class Workbench : IDisposable
{
    /// <summary>最多同时保留多少个标签。超了会先关掉最久没用的非激活标签。</summary>
    public const int MaxTabs = 12;

    public const string WorkbenchOrigin = "http://127.0.0.1:3900";

    private readonly Panel? _root;
    private readonly SplitContainer? _outer;
    private readonly SideBar? _sideBar;
    private SplitLayout _layout = null!;
    private CoreWebView2Environment? _env;
    private readonly string _userDataFolder;
    private readonly string _bridgeScript;
    private bool _ownsLayout;

    private readonly List<TabItem> _allTabs = new();

    /// <summary>
    /// WebView2 控制器创建的串行闸门。
    ///
    /// 为什么需要：<c>CreateCoreWebView2ControllerAsync</c> 在同一个 environment 上
    /// 并发调用会撞上 WebView2 内部的环境锁，直接抛
    /// <c>COMException 0x800700AA（请求的资源在使用中）</c>。
    /// 触发场景很日常：用户连点两个审阅卡片、页面里同时弹出两个 target=_blank、
    /// 初始化开完首页紧接着又开一个标签 —— 两条 OpenTab 路径撞在一起就炸。
    /// 所以这里用一把闸把「创建控制器」这一小段严格串行化。
    /// </summary>
    private readonly SemaphoreSlim _controllerGate = new(1, 1);

    /// <summary>当前激活的标签。</summary>
    public TabItem? ActiveTab { get; private set; }

    /// <summary>当前所有标签（宿主用来批量发消息，例如刷新后让页面同步数据）。</summary>
    public IReadOnlyList<TabItem> AllTabs => _allTabs;

    /// <summary>主窗口会设置自己，页面消息 / 右键菜单要回到主窗口处理。</summary>
    public MainForm? Host { get; set; }

    public IEnumerable<EditorGroup> Groups => _layout.Groups;

    public SideBar? SideBarControl => _sideBar;

    public bool SideBarVisible => _sideBar?.Visible ?? false;

    // ──────────────────────────── 构造 ────────────────────────────

    /// <summary>主窗口用的构造：自带侧边栏 + 布局。</summary>
    public Workbench(Panel root, bool showSideBar)
    {
        _root = root;
        _userDataFolder = AppPaths.WebViewDataDir;
        _bridgeScript = ReadEmbeddedBridge();

        _outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = Theme.SplitterWidth,
            BackColor = Theme.Line,
            FixedPanel = FixedPanel.Panel1,
        };
        // 关键：Panel1MinSize / Panel2MinSize 也不能在构造期设。
        // SplitContainer 此刻宽度为 0，设 Panel2MinSize=320 会触发
        // 「SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize」。
        // 延到首次布局后再设。

        _sideBar = new SideBar { Dock = DockStyle.Fill };
        _sideBar.Width = Theme.SideBarWidth;

        _layout = new SplitLayout { Dock = DockStyle.Fill, Host = this };

        _outer.Panel1.Controls.Add(_sideBar);
        _outer.Panel2.Controls.Add(_layout);
        _root.Controls.Add(_outer);

        _sideBar.Visible = showSideBar;

        // 关键：SplitterDistance 只能在容器有实际宽度之后设。
        // 构造期容器宽度还是 0，强行赋值会抛
        // 「SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize」。
        // 所以延到第一次布局完成再设。
        _sideBarShown = showSideBar;
        _outer.HandleCreated += (_, _) => ApplySideBarWidth();
        _outer.SizeChanged += (_, _) => ApplySideBarWidth();

        _sideBar.TabRequested += (_, e) => _ = OpenTab(e.Title, e.Url);

        // 壳接管工作台导航：点侧边栏的导航项 = 在工作台标签里切 hash 路由。
        // 页面自带的 .rail 已隐藏（ShellBridge），这里是唯一入口。
        _sideBar.NavRequested += (_, key) => _ = NavigateWorkbench(key);

        _layout.GroupEmpty += (_, group) => RemoveGroup(group);
        _ownsLayout = true;
    }

    private bool _sideBarShown;

    /// <summary>
    /// 切换工作台页面（壳接管导航后的主路径）。
    ///
    /// 找当前的工作台标签（URL 指向 3900 的那个），在它里面改 hash 路由。
    /// 找不到就新开一个工作台标签再切 —— 用户在任何标签里点导航，
    /// 都应该能看到工作台，而不是「点了没反应」。
    /// </summary>
    internal async Task NavigateWorkbench(string key)
    {
        try
        {
            var target = _allTabs.FirstOrDefault(t =>
                t.Url is not null &&
                t.Url.StartsWith(WorkbenchOrigin, StringComparison.OrdinalIgnoreCase));

            if (target?.View?.CoreWebView2 is null)
            {
                var url = $"{WorkbenchOrigin}/#{key}";
                await OpenTab("宿舍机工作台", url);
                _sideBar?.SetActiveNav(key);
                return;
            }

            target.Group?.ActivateTab(target);
            await ShellBridge.NavigateAsync(target.View.CoreWebView2, key);
            _sideBar?.SetActiveNav(key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[nav] 切换工作台页面失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 主题切换后，把新配色推给所有已打开的标签。
    /// 页面用 CSS 变量配色，不推的话壳变暗、页面还是亮粉，割成两半。
    /// </summary>
    internal async Task BroadcastThemeAsync(ThemeSet theme)
    {
        foreach (var tab in _allTabs)
        {
            if (tab.View?.CoreWebView2 is null) continue;
            try { await ShellBridge.ApplyThemeAsync(tab.View.CoreWebView2, theme); }
            catch { /* 单个标签失败不影响其它 */ }
        }
    }

    /// <summary>
    /// 按当前容器宽度安全地设置侧边栏宽度。
    /// </summary>
    private void ApplySideBarWidth()
    {
        try
        {
            // 最小尺寸也得等容器有宽度才能设，否则同样会抛。
            if (_outer.Width > 0)
            {
                var minPanel2 = Math.Min(320, Math.Max(0, _outer.Width - 80));
                if (_outer.Panel2MinSize != minPanel2) _outer.Panel2MinSize = minPanel2;
                if (_outer.Panel1MinSize != 0) _outer.Panel1MinSize = 0;
            }

            var target = _sideBarShown ? Theme.SideBarWidth : 0;
            if (_outer.Width <= target + _outer.Panel2MinSize) return;
            if (Math.Abs(_outer.SplitterDistance - target) < 1) return;
            _outer.SplitterDistance = target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"设置侧边栏宽度失败：{ex.Message}");
        }
    }

    /// <summary>二级窗口用：只挂布局，不带侧边栏。</summary>
    public Workbench(Form owner)
    {
        _userDataFolder = AppPaths.WebViewDataDir;
        _bridgeScript = ReadEmbeddedBridge();

        _layout = new SplitLayout { Dock = DockStyle.Fill, Host = this };
        owner.Controls.Add(_layout);
        _ownsLayout = true;
    }

    /// <summary>把已有布局挂进来（二级窗口复用已建好的 SplitLayout）。</summary>
    public void AttachLayout(SplitLayout layout)
    {
        _layout = layout;
        _layout.Host = this;
        layout.GroupEmpty += (_, group) => RemoveGroup(group);
    }

    // ──────────────────────────── 初始化 ────────────────────────────

    /// <summary>创建共享的 WebView2 环境。必须在任何标签创建前调用。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_userDataFolder);
        var runtime = WebView2Runtime.ResolveDir();
        if (runtime is null)
            throw new DirectoryNotFoundException(
                "未找到 WebView2 运行时。请安装 Microsoft Edge WebView2 Runtime。");
        _env = await CoreWebView2Environment.CreateAsync(runtime, _userDataFolder);
    }

    /// <summary>确保有根分组可用。</summary>
    public EditorGroup EnsureRootGroup()
    {
        if (_layout.Root is not null)
        {
            _layout.Root.Host = this;
            return _layout.Root;
        }
        var group = _layout.CreateRootGroup();
        group.Host = this;
        return group;
    }

    // ──────────────────────────── 标签 ────────────────────────────

    /// <summary>打开一个标签。</summary>
    public async Task<TabItem?> OpenTab(string title, string url, EditorGroup? group = null, bool splash = false)
    {
        RunLog.Write("OpenTab.begin", url);
        if (_env is null) throw new InvalidOperationException("宿主尚未初始化");

        group ??= _layout.ActiveGroup ?? EnsureRootGroup();

        // 超量时先回收一个非激活标签
        if (_allTabs.Count >= MaxTabs) EvictOldestTab();

        var tab = new TabItem { Title = title, Url = url, Group = group, IsLoading = true };

        var view = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        tab.View = view;
        Program.Boot("opentab.view.created", url);

        // ★ 控件必须先挂进内容区，再创建控制器。
        //   WebView2 的控制器是绑在控件自己的窗口句柄上的；
        //   控件在「挂上 → 摘下 → 再挂上」之间来回搬，句柄会被销毁重建，
        //   控制器就绑到了死句柄上 —— 表现是页面永远空白，
        //   紧接着再建第二个控制器还会报 0x800700AA（请求的资源在使用中）。
        //   所以：挂一次，别再挂。
        var content = group.ContentHost;
        if (!content.Controls.Contains(view)) content.Controls.Add(view);

        group.AddTab(tab);
        _allTabs.Add(tab);

        // ★ 新建标签前必须先把挂起的标签全部唤醒。
        //   TrySuspendAsync 挂起的是共享环境下的整个浏览器进程，
        //   进程被挂起时新建 WebView2 控件会一直等环境响应（实测永久卡死）。
        ResumeAllSuspendedTabs();

        // ★ 创建控制器必须串行：并发调用同一个 environment 的
        //   CreateCoreWebView2ControllerAsync 会抛 0x800700AA（请求的资源在使用中）。
        //
        //   同时加超时：控制器创建一旦卡住就永远不返回（实测会静默挂死，
        //   页面永远空白，界面却毫无异常表现）。给它一个明确的期限，
        //   超了就报错，别让整个窗口装死。
        await _controllerGate.WaitAsync();
        try
        {
            var ready = view.EnsureCoreWebView2Async(_env);
            var finished = await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(30)));
            if (finished != ready)
            {
                throw new TimeoutException(
                    "WebView2 控制器创建超时（30 秒）。通常是用户数据目录被其他进程占用，"
                    + "或 WebView2 运行时环境异常。");
            }
            await ready;
        }
        finally
        {
            _controllerGate.Release();
        }
        RunLog.Write("OpenTab.webview.ready", url);
        Program.Boot("webview.ready", $"pid={Environment.ProcessId} {url}");
        ConfigureView(view, tab);
        ApplyRasterScale(view);

        group.ActivateTab(tab);

        // 启动首标签先给一张本地占位：3900 页面首屏要等 /api/workbench，
        // 冷启动那几秒里 WebView2 控件是纯白 —— 用户看到的就是「打开了空白页」。
        // 补一张深色占位说明「正在打开」，比白板清楚得多。
        if (splash)
        {
            try
            {
                view.CoreWebView2.NavigateToString(BuildSplashHtml());
                await Task.Delay(120);
            }
            catch (Exception ex) { Debug.WriteLine($"占位页失败：{ex.Message}"); }
        }

        view.Source = new Uri(url);
        RunLog.Write("OpenTab.done", url);
        return tab;
    }

    /// <summary>把一个标签从一个窗口搬到另一个窗口（拆分到新窗口时用）。</summary>
    public async Task AdoptTabAsync(TabItem tab)
    {
        if (_env is null) throw new InvalidOperationException("宿主尚未初始化");

        // 挂起状态下接管标签会出问题，先把浏览器进程唤醒
        ResumeAllSuspendedTabs();

        var group = EnsureRootGroup();

        // 先从原组摘掉（不销毁 WebView2 控件），并从原宿主的标签表里划掉
        tab.Group?.DetachTab(tab);
        _allTabs.Remove(tab);

        tab.Group = group;

        // 控件先落到新组的容器里，再登记标签（顺序理由同 OpenTab）。
        if (tab.View is not null && !group.ContentHost.Controls.Contains(tab.View))
        {
            group.ContentHost.Controls.Add(tab.View);
        }

        group.AddTab(tab);
        _allTabs.Add(tab);

        if (tab.View is not null)
        {
            ConfigureView(tab.View, tab);
            ApplyRasterScale(tab.View);
            group.ActivateTab(tab);
        }
        await Task.CompletedTask;
    }

    /// <summary>给一个 WebView2 装上统一的设置与事件。</summary>
    private void ConfigureView(WebView2 view, TabItem tab)
    {
        var core = view.CoreWebView2;

        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;   // 由原生菜单接管
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreHostObjectsAllowed = true;
        settings.IsWebMessageEnabled = true;
        settings.IsGeneralAutofillEnabled = false;

        // ★ 核心需求：审阅台里的 target="_blank" 不再开新 WebView2 窗口，
        //   而是被拦下来变成当前分组里的一个原生标签页。
        core.NewWindowRequested += (_, e) =>
        {
            try
            {
                e.Handled = true;   // 阻止默认的「开新浏览器窗口」

                var target = e.Uri;
                if (string.IsNullOrWhiteSpace(target)) target = tab.Url;
                if (string.IsNullOrWhiteSpace(target)) return;

                var title = DeriveTitle(target);
                BeginInvokeSafe(async () => await OpenTab(title, target));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"新窗口转标签失败：{ex.Message}");
            }
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            var t = core.DocumentTitle;
            if (string.IsNullOrWhiteSpace(t)) return;
            tab.Title = t.Length > 40 ? t[..40] + "…" : t;
            tab.Group?.RefreshTabTitle(tab);
        };

        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            if (e.IsSuccess) Host?.OnTabNavigated(tab);
            // 每次导航完成都要重新施加「隐藏页面重复导航层」与「主题变量」——
            //   文档被替换了，之前注入的样式与变量都没了。
            _ = ApplyShellPresentationAsync(core);
        };

        core.WebMessageReceived += (_, e) => Host?.OnWebMessage(tab, e);

        core.ContextMenuRequested += (_, e) => Host?.OnContextMenu(tab, e);

        if (!string.IsNullOrEmpty(_bridgeScript))
        {
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(_bridgeScript);
        }

        // 主题变量必须在文档创建时注入一次，
        // 否则页面首帧会用它自己的默认配色闪一下。
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
            ShellBridge.BuildThemeScript(Theme.Current));
    }

    /// <summary>
    /// 施加壳的呈现契约：隐藏页面自带的重复导航层 + 同步主题色。
    /// 导航完成后调用（文档已替换，之前的注入全部失效）。
    /// </summary>
    private async Task ApplyShellPresentationAsync(CoreWebView2 core)
    {
        try
        {
            await ShellBridge.ApplyHideRailAsync(core);
            await ShellBridge.ApplyThemeAsync(core, Theme.Current);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[shell] 呈现契约施加失败：{ex.Message}");
        }
    }

    // ──────────────────────────── 栅格化比例 ────────────────────────────

    /// <summary>
    /// 按控件所在显示器的实际 DPI 设置栅格化比例。
    /// 125% 屏幕上是 1.25 —— 不设就是 1.0，页面被拉伸，字体发虚。
    /// 每个标签都要单独设，因为每个标签是独立的 WebView2 控件。
    /// </summary>
    public void ApplyRasterScale(WebView2 view)
    {
        try
        {
            var controller = ControllerAccess.Find(view);
            if (controller is null) return;

            controller.ShouldDetectMonitorScaleChanges = true;

            var dpi = view.DeviceDpi;
            var scale = dpi > 0 ? dpi / 96f : 1f;
            if (Math.Abs(controller.RasterizationScale - scale) > 0.001)
            {
                controller.RasterizationScale = scale;
                Debug.WriteLine($"[scale] 栅格化比例 → {scale}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scale] 设置栅格化比例失败：{ex.Message}");
        }
    }

    /// <summary>窗口 DPI 变化时，对所有标签重设栅格化比例。</summary>
    public void ApplyRasterScaleAll()
    {
        foreach (var tab in _allTabs)
            if (tab.View is not null) ApplyRasterScale(tab.View);
    }

    /// <summary>
    /// 是否允许把后台标签挂起（TrySuspendAsync）以省内存。默认关闭。
    ///
    /// 为什么默认关闭 —— 这是一条踩过坑的路：
    ///   1) WebView2 的 TrySuspendAsync 挂起的是**共享环境下的整个浏览器进程**，
    ///      不是单个标签。同进程里再创建 WebView2 控件时，环境不响应，
    ///      EnsureCoreWebView2Async 会一直等下去（实测：第二个标签永远开不出来）。
    ///   2) 恢复 API 是同步的 CoreWebView2.Resume()，不是 TryResumeAsync
    ///      （后者根本不存在）。名字写错的话，挂起后就再也醒不过来。
    ///   3) 页面还加载中的标签被挂起，会停在半路。
    ///
    /// 所以：省内存的代价是稳定性，默认不给。开这个开关时，
    /// 打开新标签前会先把所有挂起的标签唤醒，保证不会被卡死。
    /// </summary>
    public bool SuspendEnabled { get; set; }

    /// <summary>当前是否处于「省内存模式」。</summary>
    public bool SuspendEnabledNow => SuspendEnabled;

    /// <summary>把非激活标签的浏览器挂起，释放它占的渲染内存。
    ///
    /// 注意：只挂起「非激活且已加载完成」的标签，正在加载的不能挂。
    /// </summary>
    private void SuspendInactiveTabs()
    {
        var active = ActiveTab;
        foreach (var group in _layout.Groups)
        {
            foreach (var tab in group.Tabs)
            {
                if (ReferenceEquals(tab, active)) continue;
                if (tab.IsLoading) continue;
                if (tab.View?.CoreWebView2 is null) continue;
                if (tab.IsSuspended) continue;

                try
                {
                    // TrySuspendAsync 是 ICoreWebView2 的可选能力，旧运行时可能没有。
                    var method = tab.View.CoreWebView2.GetType()
                        .GetMethod("TrySuspendAsync", Type.EmptyTypes);
                    if (method is null) continue;

                    var task = method.Invoke(tab.View.CoreWebView2, null) as Task;
                    if (task is not null) task.ContinueWith(_ => { }, TaskScheduler.Default);
                    tab.IsSuspended = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"挂起标签失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 恢复被挂起的标签。
    ///
    /// 关键：WebView2 的恢复 API 是**同步**的 <c>CoreWebView2.Resume()</c>，
    /// 并不存在 <c>TryResumeAsync</c>。早期版本按 TryResumeAsync 去反射，
    /// 结果永远找不到方法，挂起后再也醒不过来 —— 浏览器进程卡死，
    /// 新建标签的 EnsureCoreWebView2Async 无限等待，整个窗口就废了。
    /// </summary>
    private void ResumeTab(TabItem tab)
    {
        if (!tab.IsSuspended) return;
        if (tab.View?.CoreWebView2 is null) return;

        try
        {
            var method = tab.View.CoreWebView2.GetType()
                .GetMethod("Resume", Type.EmptyTypes);
            if (method is null) return;
            method.Invoke(tab.View.CoreWebView2, null);
            tab.IsSuspended = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"恢复标签失败：{ex.Message}");
        }
    }

    /// <summary>唤醒所有被挂起的标签（新建标签、开新窗口前必须做，否则浏览器进程不响应）。</summary>
    public void ResumeAllSuspendedTabs()
    {
        foreach (var group in _layout.Groups)
            foreach (var tab in group.Tabs)
                ResumeTab(tab);
    }

    // ──────────────────────────── 分组与分裂 ────────────────────────────

    public EditorGroup? ActiveGroup => _layout.ActiveGroup;

    public void SplitActiveGroupVertical()
    {
        SplitActiveGroup(SplitDirection.Vertical);
    }

    public void SplitActiveGroupHorizontal()
    {
        SplitActiveGroup(SplitDirection.Horizontal);
    }

    /// <summary>按方向分裂当前组，返回新产生的那一组（自检与演示模式需要拿到它）。</summary>
    public EditorGroup? SplitActiveGroup(SplitDirection direction)
    {
        var group = _layout.ActiveGroup;
        if (group is null) return null;
        return _layout.Split(group, direction);
    }

    /// <summary>
    /// 移除一个空分组。
    ///
    /// ★ 必须保住最后一个分组：关掉最后一个标签时如果连根分组一起拆掉，
    ///   SplitLayout 的根节点会变成 null，窗口里只剩一层底色 ——
    ///   看起来就是「窗口空白」，而用户其实只是关掉了所有标签。
    ///   留一个空分组，标签条会显示「双击这里新建标签」，随时能继续用。
    /// </summary>
    public void RemoveGroup(EditorGroup group)
    {
        if (_layout.Groups.Count() <= 1) return;
        _layout.RemoveGroup(group);
    }

    /// <summary>把标签移到另一个分组。</summary>
    public void MoveTabToGroup(TabItem tab, EditorGroup target)
    {
        if (tab.Group is null || ReferenceEquals(tab.Group, target)) return;

        var src = tab.Group;
        src.DetachTab(tab);
        tab.Group = null;

        // ★ 关键：必须把 WebView2 控件搬到新分组的内容容器里。
        //   DetachTab 只把控件从旧容器「摘下来」（不销毁），
        //   而 EditorGroup.AddTab 并不负责把控件挂进新容器 ——
        //   少了这一步，新分组会是一个空白框，标签在但页面不见了。
        if (tab.View is not null)
        {
            tab.View.Visible = false;
            target.ContentHost.Controls.Add(tab.View);
        }

        target.AddTab(tab);
        target.ActivateTab(tab);
    }

    // ──────────────────────────── 拖放 ────────────────────────────

    /// <summary>开始拖动一个标签（由 TabStrip 触发）。</summary>
    public void BeginTabDrag(TabItem tab)
    {
        try
        {
            tab.Group?.Host2BeginDrag(tab);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"开始拖动失败：{ex.Message}");
        }
    }

    /// <summary>在某个分组的落点上处理一次拖放（分裂 / 合并 / 移出窗口）。</summary>
    public void HandleTabDrop(TabItem tab, EditorGroup target, EditorGroup.DropZone zone)
    {
        switch (zone)
        {
            case EditorGroup.DropZone.Center:
                MoveTabToGroup(tab, target);
                break;

            case EditorGroup.DropZone.Left:
            case EditorGroup.DropZone.Right:
            case EditorGroup.DropZone.Top:
            case EditorGroup.DropZone.Bottom:
                // 在目标组旁边分裂出一个新组，把标签放进去
                var dir = zone switch
                {
                    EditorGroup.DropZone.Left or EditorGroup.DropZone.Right => SplitDirection.Vertical,
                    _ => SplitDirection.Horizontal,
                };
                var newGroup = _layout.Split(target, dir);
                MoveTabToGroup(tab, newGroup);
                break;

            case EditorGroup.DropZone.None:
                // 拖到窗口外 → 拆到新窗口
                DetachToNewWindow(tab);
                break;
        }
    }

    /// <summary>把标签拆到一个新窗口。</summary>
    public void DetachToNewWindow(TabItem tab)
    {
        var owner = Host;
        if (owner is null) return;

        try
        {
            _ = owner.SplitTabToNewWindowAsync(tab);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"拆分到新窗口失败：{ex.Message}");
        }
    }

    /// <summary>标签被关闭后通知宿主（主窗口要清掉它的右键上下文缓存）。</summary>
    public event EventHandler<TabItem>? TabRemoved;

    /// <summary>
    /// 关闭一个标签（唯一入口）。
    ///
    /// 关闭链路：标签条 × / 中键 / Ctrl+W / 菜单「关闭标签」全部走这里。
    /// 关掉之后要做三件事：释放 WebView2、从标签表里划掉、把焦点交给相邻标签。
    /// </summary>
    public void CloseTab(TabItem tab)
    {
        var group = tab.Group;
        if (group is null) return;

        // 先记下「关掉它之后该激活谁」：同组里它左边那个，没有就右边。
        var ordered = group.Tabs.ToList();
        var index = ordered.IndexOf(tab);
        var siblings = ordered.Where(t => !ReferenceEquals(t, tab)).ToList();
        var fallback = siblings.Count == 0
            ? null
            : siblings[Math.Clamp(index - 1, 0, siblings.Count - 1)];

        group.RemoveTab(tab);      // 内含 WebView2.Dispose 与标签条移除
        _allTabs.Remove(tab);
        tab.Group = null;

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = fallback;
            if (fallback is not null) fallback.Group?.ActivateTab(fallback);
        }

        RunLog.Write("CloseTab.done",
            $"title={tab.Title} remain={_allTabs.Count} groups={_layout.Groups.Count()} fallback={fallback?.Title ?? "none"}");

        TabRemoved?.Invoke(this, tab);
    }

    /// <summary>
    /// 新建一个标签（双击标签条空白处 / 菜单）。
    /// 工作台已经有标签时，新标签仍然打开工作台首页 —— 这是用户要的默认落点，
    /// 不是空白页：空白的 about:blank 看着就像壳坏了。
    /// </summary>
    public void NewTab()
    {
        _ = OpenTab("创作与审阅", WorkbenchOrigin + "/#creative");
    }

    private void EvictOldestTab()
    {
        var victim = _allTabs.FirstOrDefault(t => !ReferenceEquals(t, ActiveTab));
        if (victim is null) return;
        CloseTab(victim);
    }

    // ──────────────────────────── 侧边栏 ────────────────────────────

    /// <summary>
    /// 设置里改了侧边栏宽度后重新应用。
    /// 只改 _sideBar.Width 不够 —— 真正决定宽度的是 SplitContainer.SplitterDistance。
    /// </summary>
    internal void RefreshSideBarWidth()
    {
        if (_sideBar is null) return;
        _sideBar.Width = Theme.SideBarWidth;
        ApplySideBarWidth();
    }

    public void SetSideBarVisible(bool visible)
    {
        if (_sideBar is null || _outer is null) return;
        _sideBar.Visible = visible;
        _sideBarShown = visible;
        ApplySideBarWidth();
    }

    // ──────────────────────────── 杂项 ────────────────────────────

    public void OnActiveTabChanged(EditorGroup group, TabItem tab)
    {
        ActiveTab = tab;
        ResumeTab(tab);
        if (SuspendEnabled) SuspendInactiveTabs();
        Host?.OnActiveTabChanged(group, tab);
    }

    /// <summary>切换「省内存模式」。关闭时把挂起的标签全部唤醒。</summary>
    public void SetSuspendEnabled(bool enabled)
    {
        SuspendEnabled = enabled;
        if (!enabled) ResumeAllSuspendedTabs();
    }

    /// <summary>
    /// 启动占位页：纯本地 HTML，不依赖 3900。
    /// 配色取当前主题，避免亮壳配黑页闪一下。
    /// </summary>
    internal static string BuildSplashHtml()
    {
        static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        var bg = Hex(Theme.Bg);
        var surface = Hex(Theme.Surface);
        var ink = Hex(Theme.Ink);
        var muted = Hex(Theme.Muted);
        var line = Hex(Theme.Line);
        var accent = Hex(Theme.Accent);

        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
               "html,body{height:100%;margin:0;background:" + bg + ";color:" + ink + ";" +
               "font-family:'Microsoft YaHei UI','Segoe UI',sans-serif;}" +
               ".wrap{height:100%;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:14px}" +
               ".t{font-size:15px;letter-spacing:.02em}" +
               ".bar{width:200px;height:3px;border-radius:2px;background:" + line + ";overflow:hidden;position:relative}" +
               ".bar i{position:absolute;left:-42%;width:42%;height:100%;background:" + accent + ";" +
               "animation:run 1.1s infinite ease-in-out}" +
               ".s{font-size:12px;color:" + muted + "}" +
               ".card{padding:26px 34px;border-radius:14px;background:" + surface + ";text-align:center;" +
               "display:flex;flex-direction:column;align-items:center;gap:12px}" +
               "@keyframes run{0%{left:-42%}100%{left:100%}}" +
               "</style></head><body><div class=\"wrap\"><div class=\"card\">" +
               "<div class=\"t\">正在打开宿舍机工作台…</div>" +
               "<div class=\"bar\"><i></i></div>" +
               "<div class=\"s\">本机 3900 端口，首次加载要几秒</div>" +
               "</div></div></body></html>";
    }

    /// <summary>从 URL 推导一个像样的标签名。</summary>
    public static string DeriveTitle(string url)
    {
        try
        {
            var uri = new Uri(url);
            var last = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrEmpty(last))
            {
                var decoded = Uri.UnescapeDataString(last);
                if (decoded.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    decoded = decoded[..^5];
                if (!string.IsNullOrWhiteSpace(decoded))
                    return decoded.Length > 40 ? decoded[..40] + "…" : decoded;
            }
            return uri.Host;
        }
        catch
        {
            return "新标签";
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        try
        {
            if (_root is not null && _root.InvokeRequired) _root.BeginInvoke(action);
            else action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"延迟执行失败：{ex.Message}");
        }
    }

    /// <summary>读取编译进 exe 的 bridge.js。</summary>
    internal static string ReadEmbeddedBridge()
    {
        try
        {
            using var stream = typeof(Workbench).Assembly
                .GetManifestResourceStream("WorkbenchShell.bridge.js");
            if (stream is null) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        foreach (var tab in _allTabs)
        {
            try { tab.View?.Dispose(); } catch { }
        }
        _allTabs.Clear();
    }
}
