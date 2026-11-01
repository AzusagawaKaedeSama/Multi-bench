// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 整窗离屏取证：把「新版工作台界面」在虚拟屏外渲染成 PNG，不碰用户屏幕。
//
// 为什么必须离屏：
//   用户正在自己的桌面上干活。把窗口摆到屏幕中间、置顶、抢焦点再截屏，
//   那是在打扰人。取证的需求是「让我看看新版长什么样」，
//   而不是「让我在你的屏幕上操作」。
//
// 原理：
//   · 宿主窗口放到 (-4000,-4000)，并加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，
//     窗口存在但永远不抢焦点、不进任务栏、用户看不见；
//   · 原生侧边栏是纯 GDI+ 自绘的（SideBarPanel : Control），
//     用 DrawToBitmap 直接把它的像素取出来 —— 与它在屏幕内外无关；
//   · 页面内容用 WebView2 的 CapturePreview() 抓控制器自己的渲染位图；
//   · 两块拼成一张「新版界面」效果图。
//
// 顺带把 4 套主题各渲染一张，证明主题体系是真的能用，不是纸面设计。

namespace WorkbenchShell;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class UiShot
{
    /// <summary>
    /// 渲染新版界面。outDir 为输出目录，会生成「界面-主题名.png」若干张。
    /// targetUrl 非空时只截这个 URL（用真实壳环境打开任意站点，例如图片门禁确认页），
    /// 用来证明「壳只做宿主，不把别的站点的配色改成自己的主题」。
    /// </summary>
    internal static int Run(string outDir, bool allThemes, string? targetUrl = null)
    {
        var external = !string.IsNullOrWhiteSpace(targetUrl);
        Directory.CreateDirectory(outDir);
        NativeConsole.Attach();

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["outDir"] = Path.GetFullPath(outDir),
        };

        var exitCode = 0;
        try
        {
            ApplicationConfiguration.Initialize();

            // 屏外宿主：1440x900，与实际窗口同尺寸。
            using var form = new OffscreenForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                Size = new Size(1440, 900),
                BackColor = Theme.Bg,
            };
            form.Show();

            // 真实侧边栏控件（数据从 3900 接口拉，跟线上同一份逻辑）
            var sideBar = new SideBar { Dock = DockStyle.Left, Width = Theme.SideBarWidth };
            var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            form.Controls.Add(pageHost);
            form.Controls.Add(sideBar);
            sideBar.BringToFront();

            var view = new WebView2 { Dock = DockStyle.Fill };
            pageHost.Controls.Add(view);

            var flow = RunFlow(form, view, sideBar, outDir, allThemes, report, targetUrl);
            flow.GetAwaiter().OnCompleted(() => { });

            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (!flow.IsCompleted && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!flow.IsCompleted)
            {
                report["error"] = "超时（180 秒）";
                exitCode = 1;
            }
            else if (flow.IsFaulted)
            {
                report["error"] = flow.Exception?.GetBaseException().ToString();
                exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        finally
        {
            report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var jsonPath = Path.Combine(outDir, "界面取证.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
        }

        return exitCode;
    }

    /// <summary>屏外宿主：存在但永远不抢焦点、不进任务栏。</summary>
    private sealed class OffscreenForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    private static async Task RunFlow(
        Form form,
        WebView2 view,
        SideBar sideBar,
        string outDir,
        bool allThemes,
        Dictionary<string, object?> report,
        string? targetUrl = null)
    {
        var external = !string.IsNullOrWhiteSpace(targetUrl);
        report["targetUrl"] = targetUrl ?? Workbench.WorkbenchOrigin + "/";
        report["step"] = "create-env";
        var runtimeDir = WebView2Runtime.ResolveDir()
            ?? throw new DirectoryNotFoundException("未找到 WebView2 运行时目录。");

        var udf = Path.Combine(AppPaths.BaseDir, "wbdata-uishot");
        Directory.CreateDirectory(udf);
        var env = await CoreWebView2Environment.CreateAsync(runtimeDir, udf);
        await view.EnsureCoreWebView2Async(env);
        report["step"] = "webview-ready";

        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;

        // 屏外渲染必须显式标记控制器可见，否则 Chromium 判定页面 hidden → 不渲染。
        var controller = ControllerAccess.Find(view);
        if (controller is not null)
        {
            controller.IsVisible = true;
            controller.RasterizationScale = 1f;
        }

        // 屏蔽页面弹窗，别让它蹦到用户屏幕上。
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.alert=function(){};window.confirm=function(){return true;};" +
            "window.prompt=function(){return null;};window.print=function(){};");

        // 主题变量在文档创建时注入（与线上一致）
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            ShellBridge.BuildThemeScript(Theme.Current));

        report["step"] = "navigate";
        core.Navigate(external ? targetUrl! : Workbench.WorkbenchOrigin + "/");

        // 等页面把主内容渲染出来
        report["step"] = "wait-page";
        var ready = false;
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(500);
            try
            {
                var raw = await core.ExecuteScriptAsync(
                    "(function(){return JSON.stringify({t:document.title||''," +
                    "cards:document.querySelectorAll('a,button,li,.card,.item').length});})()");
                var obj = ParseObject(raw);
                if (obj is not null && (int?)obj["cards"] > 12) { ready = true; break; }
                if (external)
                {
                    var state = await core.ExecuteScriptAsync("document.readyState");
                    if (state.Contains("complete")) { ready = true; break; }
                }
            }
            catch { }
        }
        report["pageReady"] = ready;
        report["pageTitle"] = core.DocumentTitle;

        // 施加壳的呈现契约：隐藏页面重复导航层 + 同步主题。
        // 外部站点（确认页这类有自己的设计）不做这两步 —— 壳只负责宿主。
        if (!external)
        {
            await ShellBridge.ApplyHideRailAsync(core);
            await ShellBridge.ApplyThemeAsync(core, Theme.Current);
        }
        await Task.Delay(600);
        Application.DoEvents();

        if (external)
        {
            report["step"] = "capture-external";
            report["appliedVars-external"] = await ShellBridge.ReadAppliedVarsAsync(core);
            var pngBytes = await CapturePageAsync(core);
            if (pngBytes is null) { report["error"] = "页面截图失败"; return; }
            // 文件名要短：3955 的审阅页 URL 本身就有两三百个字符，
            // 直接拼进文件名会撞 Windows 的 260 字符路径上限（IOException: 文件名语法不正确）。
            var raw = targetUrl ?? "page";
            var label = new string(raw.Replace("http://", "").Replace("https://", "")
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            if (label.Length > 40) label = label[..40] + "-" + Math.Abs(raw.GetHashCode() & 0x7fffffff).ToString("x8");
            var file = Path.Combine(outDir, "页面-" + label + ".png");
            File.WriteAllBytes(file, pngBytes);
            report["outputs"] = new List<string> { file };
            report["verdict"] = "PASS";
            Console.WriteLine("IMAGE: " + file);
            return;
        }

        // 侧边栏拉真实数据（跟线上同一条接口、同一个分类逻辑）
        report["step"] = "load-sidebar";
        await sideBar.LoadAsync();
        report["projectCount"] = sideBar.ProjectCount;
        report["visibleNodes"] = sideBar.VisibleNodeCount;

        // 等一帧，让自绘面板完成布局
        await Task.Delay(700);
        Application.DoEvents();

        // 侧边栏像素：自绘控件，DrawToBitmap 与是否在屏上无关
        report["step"] = "capture-sidebar";
        byte[]? sidePng = RenderControl(sideBar);
        if (sidePng is null)
        {
            report["error"] = "侧边栏渲染失败";
            return;
        }

        // 标签条：同样用真实控件画（截图里必须能看到中文标签，才谈得上判断字形）。
        // 标题取三条真实的审阅页名，长度不同，顺带看省略号与关闭按钮有没有压字。
        report["step"] = "capture-tabs";
        var tabStrip = new TabStrip { Width = Math.Max(900, form.Width - sideBar.Width) };
        tabStrip.Add(new TabItem { Title = "创作与审阅", Url = Workbench.WorkbenchOrigin + "/#creative" });
        tabStrip.Add(new TabItem { Title = "维｜C盘与底模清理", Url = Workbench.WorkbenchOrigin + "/" });
        tabStrip.Add(new TabItem { Title = "库｜小红书审阅台", Url = Workbench.WorkbenchOrigin + "/" });
        tabStrip.SetActive(tabStrip.Tabs[0]);
        if (!tabStrip.IsHandleCreated) tabStrip.CreateControl();
        report["tabCount"] = tabStrip.Tabs.Count;
        byte[]? tabPng = RenderControl(tabStrip, tabStrip.Height);
        if (tabPng is null)
        {
            report["error"] = "标签条渲染失败";
            return;
        }

        // 页面像素
        report["step"] = "capture-page";
        try
        {
            // 取证诊断：页面视口 / 文档高度 / topline 位置。
            // 离屏渲染下这三者一旦不自洽（例如页面被缩放到控件之外的尺寸），
            // 合成图就会出现大片空白 —— 这不是界面问题，是取证环境问题，必须能一眼看出来。
            var metrics = await core.ExecuteScriptAsync(
                "(function(){var t=document.querySelector('.topline'),m=document.querySelector('main');" +
                "return JSON.stringify({vw:innerWidth,vh:innerHeight,scrollH:document.documentElement.scrollHeight," +
                "toplineTop:t?Math.round(t.getBoundingClientRect().top):null," +
                "mainTop:m?Math.round(m.getBoundingClientRect().top):null,scrollY:Math.round(window.scrollY)});})()");
            report["pageMetrics"] = metrics;
            report["viewSize"] = $"{view.Width}x{view.Height}";
            report["hostSize"] = $"{view.Parent?.Width}x{view.Parent?.Height}";
        }
        catch (Exception ex) { report["pageMetrics"] = "ERR " + ex.Message; }

        byte[]? pagePng = await CapturePageAsync(core);
        if (pagePng is null)
        {
            report["error"] = "页面截图失败";
            return;
        }
        using (var probe = Image.FromStream(new MemoryStream(pagePng)))
            report["pagePngSize"] = $"{probe.Width}x{probe.Height}";

        // 主题列表：默认主题 + 另外三套
        var themes = allThemes
            ? Theme.All.ToList()
            : new List<ThemeSet> { Theme.All[0] };

        var outputs = new List<string>();
        foreach (var theme in themes)
        {
            Theme.Apply(theme);
            sideBar.BackColor = Theme.Bg;
            sideBar.Refresh();
            Application.DoEvents();
            await Task.Delay(220);
            Application.DoEvents();

            // 页面同步换色（与线上 Theme.Changed 广播同一条路径）
            await ShellBridge.ApplyThemeAsync(core, theme);
            await Task.Delay(400);
            Application.DoEvents();

            // 读回页面实际生效的变量 —— 确认注入真的压住了页面自己的规则
            var applied = await ShellBridge.ReadAppliedVarsAsync(core);
            report["appliedVars-" + theme.Name] = applied;
            Console.WriteLine("VARS " + theme.Name + ": " + applied);

            // 主题换了，侧边栏与标签条都要重新取像素
            var themedSide = RenderControl(sideBar) ?? sidePng;
            tabStrip.Invalidate();
            Application.DoEvents();
            var themedTab = RenderControl(tabStrip, tabStrip.Height) ?? tabPng;
            // 页面也要重新抓（配色变了）
            var themedPage = await CapturePageAsync(core) ?? pagePng;

            var fileName = $"界面-{theme.Name}.png";
            var path = Path.Combine(outDir, fileName);
            Compose(themedSide!, themedTab!, themedPage!, path);
            outputs.Add(path);
            Console.WriteLine("IMAGE: " + path);
        }

        // 刷新态取证：一张图同时给出两件事 ——
        //   左：侧边栏标题旁的刷新图标在转、底部写着「刷新中…」；
        //   右：页面自己那条刷新链路跑完，按钮旁写着「刚刚更新 hh:mm · XX ms」。
        report["step"] = "capture-refreshing";
        Theme.Apply(Theme.All[0]);
        sideBar.BackColor = Theme.Bg;
        sideBar.PreviewRefreshing(true, "刷新中…");
        tabStrip.Invalidate();
        Application.DoEvents();
        await Task.Delay(260);
        Application.DoEvents();

        var spinSide = RenderControl(sideBar) ?? sidePng;
        var spinTab = RenderControl(tabStrip, tabStrip.Height) ?? tabPng;

        try { await core.ExecuteScriptAsync("(window.refresh?refresh(true):null)"); } catch { }
        await Task.Delay(700);
        Application.DoEvents();
        try
        {
            report["pageRefreshMeta"] = await core.ExecuteScriptAsync(
                "(document.getElementById('refresh-meta')||{}).textContent||''");
            report["pageRefreshLabel"] = await core.ExecuteScriptAsync(
                "(document.getElementById('refresh-label')||{}).textContent||''");
        }
        catch (Exception ex) { report["pageRefreshMeta"] = "ERR " + ex.Message; }

        var spinPage = await CapturePageAsync(core) ?? pagePng;
        var spinPath = Path.Combine(outDir, "界面-刷新中.png");
        Compose(spinSide!, spinTab!, spinPage!, spinPath);
        outputs.Add(spinPath);
        sideBar.PreviewRefreshing(false);
        Console.WriteLine("IMAGE: " + spinPath);

        report["outputs"] = outputs;
        report["verdict"] = "PASS";    }

    /// <summary>把一个控件画进位图。控件是自绘的，所以拿到的就是它真实的像素。</summary>
    /// <summary>
    /// 把一个控件画进位图。控件是自绘的，所以拿到的就是它真实的像素。
    ///
    /// minHeight 只对「整列」控件（侧边栏）有意义：窗口很矮时也要截够高度。
    /// 标签条这类横向控件必须传它自己的高度 —— 以前统一用 600，标签条位图里
    /// 下方 566px 是空白，合成时把页面整块往下推，看起来像「页面顶部空了半屏」。
    /// </summary>
    private static byte[]? RenderControl(Control ctl, int minHeight = 600)
    {
        try
        {
            ctl.Width = Math.Max(ctl.Width, Theme.SideBarWidth);
            var w = ctl.Width;
            var h = Math.Max(ctl.Height, Math.Min(minHeight, Math.Max(1, ctl.Height)));

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Theme.Bg);
            }

            // DrawToBitmap 需要控件有句柄
            if (!ctl.IsHandleCreated) ctl.CreateControl();

            using var ctrlBmp = new Bitmap(w, h);
            ctl.DrawToBitmap(ctrlBmp, new Rectangle(0, 0, w, h));

            using var ms = new MemoryStream();
            ctrlBmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("控件渲染异常: " + ex.Message);
            return null;
        }
    }

    private static async Task<byte[]?> CapturePageAsync(CoreWebView2 core)
    {
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("页面截图异常: " + ex.Message);
            return null;
        }
    }

    /// <summary>合成：顶部菜单栏，左边原生侧边栏，右上标签条 + 页面。</summary>
    private static void Compose(byte[] sidePng, byte[] tabPng, byte[] pagePng, string outPath)
    {
        using var side = Image.FromStream(new MemoryStream(sidePng));
        using var tabs = Image.FromStream(new MemoryStream(tabPng));
        using var page = Image.FromStream(new MemoryStream(pagePng));

        const int menuH = 34;
        var sideW = side.Width;
        var pageW = Math.Max(page.Width, 1000);
        var tabH = tabs.Height;

        var totalW = sideW + pageW;
        var totalH = menuH + Math.Max(side.Height, tabH + page.Height);

        using var canvas = new Bitmap(totalW, totalH);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);

            // 顶部菜单栏：仿原生菜单条
            using (var menuBrush = new SolidBrush(Theme.Soft))
            using (var edgePen = new Pen(Theme.Line, 1f))
            {
                g.FillRectangle(menuBrush, 0, 0, totalW, menuH);
                g.DrawLine(edgePen, 0, menuH, totalW, menuH);
            }

            var menuFont = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular);
            var items = new[] { "文件(F)", "编辑(E)", "视图(V)", "布局(L)", "主题(T)", "帮助(H)" };
            var x = 14f;
            foreach (var it in items)
            {
                using var brush = new SolidBrush(Theme.Ink);
                g.DrawString(it, menuFont, brush, x, (menuH - g.MeasureString(it, menuFont).Height) / 2f);
                x += g.MeasureString(it, menuFont).Width + 22;
            }
            menuFont.Dispose();

            // 页面（右侧，标签条之下）
            g.DrawImage(page, sideW, menuH + tabH);

            // 标签条（右侧顶部）—— 超出页面宽度的部分裁掉，和真实窗口一致
            g.DrawImage(tabs, new Rectangle(sideW, menuH, pageW, tabH));

            // 侧边栏（左侧，整列到顶）
            g.DrawImage(side, 0, menuH);

            // 分隔线
            using var sep = new Pen(Theme.Line, 1f);
            g.DrawLine(sep, sideW, menuH, sideW, totalH);

            // 整体外框
            using var border = new Pen(Theme.Line, 1f);
            g.DrawRectangle(border, 0, 0, totalW - 1, totalH - 1);
        }

        canvas.Save(outPath, ImageFormat.Png);
    }

    private static JsonObject? ParseObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try
        {
            var outer = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(outer)) return null;
            return JsonNode.Parse(outer) as JsonObject;
        }
        catch { return null; }
    }
}
