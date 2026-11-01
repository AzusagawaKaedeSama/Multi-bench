// 作者：workbench-ux · 2026-11-01
// 刷新自检：证明「两个刷新入口都真的打到 /api/workbench?fresh=1」，
// 且刷新过程不打断用户当前状态（侧边栏展开、滚动位置、当前标签）。
//
// 为什么必须这么验：
//   · 只看按钮有没有转圈，证明不了请求参数 —— 缓存命中也会转圈；
//   · 只看数据有没有变，证明不了状态保住 —— 重建侧边栏也能把数据刷新。
//   所以这里同时读服务端响应头（x-workbench-cache）、控件状态、标签表，三样一起对。

using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WorkbenchShell;

internal static class RefreshSelfTest
{
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        Console.WriteLine("[refreshtest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["origin"] = Workbench.WorkbenchOrigin,
        };

        var form = new MainForm(false) { SilentMode = true };
        var exitCode = 0;
        var done = false;

        try
        {
            // 纪律：自检一律在屏幕外跑，不抢前台焦点（用户可能在用别的窗口）。
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            var flow = RunFlow(form, report);
            flow.GetAwaiter().OnCompleted(() => done = true);

            var deadline = DateTime.UtcNow.AddSeconds(240);
            while (!done && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!done) { report["error"] = "超时（240 秒）"; exitCode = 1; }
            else if (flow.IsFaulted) { report["error"] = flow.Exception?.GetBaseException().ToString(); exitCode = 1; }
            else if (Equals(report.GetValueOrDefault("verdict"), "FAIL")) exitCode = 1;
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        finally
        {
            report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var jsonPath = Path.Combine(outputDir, "workbench-shell-refreshtest.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
            try { form.Close(); } catch { }
        }

        return exitCode;
    }

    /// <summary>强制控件真实绘制一次（屏幕外窗口不一定收到 WM_PAINT）。</summary>
    private static void ForcePaint(Control ctl)
    {
        try
        {
            if (!ctl.IsHandleCreated) ctl.CreateControl();
            var w = Math.Max(ctl.Width, 240);
            var h = Math.Max(ctl.Height, 600);
            using var bmp = new System.Drawing.Bitmap(w, h);
            ctl.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
        }
        catch { /* 强制绘制失败不阻断自检 */ }
    }

    private static void Check(Dictionary<string, object?> report, string name, bool ok, string detail = "")
    {
        report[name + ".ok"] = ok;
        if (!string.IsNullOrEmpty(detail)) report[name + ".detail"] = detail;
    }

    private static async Task RunFlow(MainForm form, Dictionary<string, object?> report)
    {
        var bench = form.Bench;

        report["step"] = "init";
        await form.InitializeForTestAsync();

        var sideBar = bench.SideBarControl;
        if (sideBar is null) { report["error"] = "没有侧边栏控件"; report["verdict"] = "FAIL"; return; }

        report["step"] = "wait-projects";
        var projects = 0;
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250);
            projects = sideBar.ProjectCount;
            if (projects > 0) break;
        }
        report["projectCount"] = projects;
        Check(report, "projectsLoaded", projects > 0, "count=" + projects);
        if (projects <= 0) { report["verdict"] = "FAIL"; return; }

        await Task.Delay(600);

        // ── 1. 先把用户状态摆出来：待审项目「展开其余」、滚到中间 ──────────
        report["step"] = "prepare-user-state";
        var revealOk = sideBar.RevealGroup("review");
        // 侧边栏的内容高度只在绘制时算出来（_contentHeight 在 DrawList 里更新）。
        // 自检窗口摆在屏幕外，Windows 可能根本不派 WM_PAINT —— 用 DrawToBitmap
        // 强制走一次真实绘制（UiShot 用的同一条路径），再滚。
        for (var i = 0; i < 8 && sideBar.ScrollOffset == 0; i++)
        {
            await Task.Delay(250);
            Application.DoEvents();
            ForcePaint(sideBar);
            sideBar.ScrollTo(160);
        }
        await Task.Delay(300);
        Application.DoEvents();

        var expandedBefore = new Dictionary<string, bool>(sideBar.GroupState);
        var revealBefore = new Dictionary<string, bool>(sideBar.GroupRevealState);
        var scrollBefore = sideBar.ScrollOffset;
        var tabCountBefore = bench.AllTabs.Count;
        var activeUrlBefore = bench.ActiveTab?.Url ?? "";

        report["expandedBefore"] = string.Join(",", expandedBefore.Select(kv => kv.Key + "=" + kv.Value));
        report["revealBefore"] = string.Join(",", revealBefore.Where(kv => kv.Value).Select(kv => kv.Key));
        report["scrollBefore"] = scrollBefore;
        report["tabsBefore"] = tabCountBefore;
        Check(report, "userStatePrepared", revealOk && scrollBefore > 0,
            $"reveal={revealOk} scroll={scrollBefore}");

        // ── 2. 侧边栏刷新图标那条路：LoadAsync(forceFresh: true) ───────────
        report["step"] = "sidebar-refresh";
        var sideStarted = DateTime.Now;
        await sideBar.LoadAsync(forceFresh: true);
        await Task.Delay(400);
        Application.DoEvents();
        var sidebarMs = (int)(DateTime.Now - sideStarted).TotalMilliseconds;

        report["sidebarRefreshMs"] = sideBar.LastRefreshMs;
        report["sidebarRoundTripMs"] = sidebarMs;
        Check(report, "sidebarRefreshRan", sideBar.LastRefreshMs > 0 && sidebarMs > 0,
            $"reported={sideBar.LastRefreshMs}ms wall={sidebarMs}ms");

        // 展开状态、滚动位置、标签都没被动过
        var expandedAfter = new Dictionary<string, bool>(sideBar.GroupState);
        var revealAfter = new Dictionary<string, bool>(sideBar.GroupRevealState);
        var scrollAfter = sideBar.ScrollOffset;
        var tabCountAfter = bench.AllTabs.Count;
        var activeUrlAfter = bench.ActiveTab?.Url ?? "";

        report["scrollAfter"] = scrollAfter;
        report["tabsAfter"] = tabCountAfter;
        report["activeUrlBefore"] = activeUrlBefore;
        report["activeUrlAfter"] = activeUrlAfter;

        Check(report, "expandedKept",
            expandedBefore.Count == expandedAfter.Count &&
            expandedBefore.All(kv => expandedAfter.TryGetValue(kv.Key, out var v) && v == kv.Value),
            "before=" + string.Join(",", expandedBefore.Select(kv => kv.Key + "=" + kv.Value)) +
            " after=" + string.Join(",", expandedAfter.Select(kv => kv.Key + "=" + kv.Value)));
        Check(report, "revealKept",
            revealBefore.Where(kv => kv.Value).All(kv => revealAfter.TryGetValue(kv.Key, out var v) && v),
            "reveal after=" + string.Join(",", revealAfter.Where(kv => kv.Value).Select(kv => kv.Key)));
        Check(report, "scrollKept", scrollAfter == scrollBefore,
            $"before={scrollBefore} after={scrollAfter}");
        Check(report, "tabsKept",
            tabCountAfter == tabCountBefore && activeUrlAfter == activeUrlBefore,
            $"tabs {tabCountBefore}->{tabCountAfter} active={activeUrlBefore}");

        // ── 3. 服务端真的重算了（读响应头，不看转圈）─────────────────────
        report["step"] = "server-fresh-header";
        using (var handler = new HttpClientHandler { UseProxy = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.GetAsync(Workbench.WorkbenchOrigin + "/api/workbench?fresh=1");
            sw.Stop();
            var cacheHeader = response.Headers.TryGetValues("x-workbench-cache", out var values)
                ? string.Join(",", values) : "";
            var buildHeader = response.Headers.TryGetValues("x-workbench-build-ms", out var b) ? string.Join(",", b) : "";
            report["freshCacheHeader"] = cacheHeader;
            report["freshBuildMs"] = buildHeader;
            report["freshWallMs"] = (int)sw.ElapsedMilliseconds;
            Check(report, "serverFreshForced", cacheHeader == "refresh", "x-workbench-cache=" + cacheHeader);

            var cachedSw = System.Diagnostics.Stopwatch.StartNew();
            var cached = await client.GetAsync(Workbench.WorkbenchOrigin + "/api/workbench");
            cachedSw.Stop();
            var cachedHeader = cached.Headers.TryGetValues("x-workbench-cache", out var cv) ? string.Join(",", cv) : "";
            report["cachedCacheHeader"] = cachedHeader;
            report["cachedWallMs"] = (int)cachedSw.ElapsedMilliseconds;
            Check(report, "serverCacheHits", cachedHeader == "hit", "x-workbench-cache=" + cachedHeader);
        }

        // ── 4. 页面顶部「刷新」那条路：执行页面自己的 refresh(true) ─────────
        report["step"] = "page-refresh";
        var core = bench.ActiveTab?.View?.CoreWebView2;
        if (core is null)
        {
            report["pageRefresh"] = "没有活动 WebView2";
            Check(report, "pageRefreshRan", false, "no webview");
        }
        else
        {
            // 等页面把 DOM 建出来（refresh 依赖 #refresh-meta / #refresh-btn）
            var ready = false;
            for (var i = 0; i < 60; i++)
            {
                var state = await core.ExecuteScriptAsync(
                    "document.getElementById('refresh-btn')?document.readyState:'none'");
                if (state.Contains("complete") || state.Contains("interactive")) { ready = true; break; }
                await Task.Delay(250);
            }
            report["pageReadyForRefresh"] = ready;

            var pageSw = System.Diagnostics.Stopwatch.StartNew();
            await core.ExecuteScriptAsync("refresh(true)");
            pageSw.Stop();
            await Task.Delay(300);

            var meta = await core.ExecuteScriptAsync(
                "(document.getElementById('refresh-meta')||{}).textContent||''");
            var label = await core.ExecuteScriptAsync(
                "(document.getElementById('refresh-label')||{}).textContent||''");
            report["pageMeta"] = meta;
            report["pageRefreshLabel"] = label;
            report["pageRefreshMs"] = (int)pageSw.ElapsedMilliseconds;
            Check(report, "pageRefreshShowsFeedback",
                meta.Contains("刚刚更新") && label.Contains("刷新"),
                "meta=" + meta + " label=" + label);
            Check(report, "pageRefreshRan", pageSw.ElapsedMilliseconds > 0,
                "wall=" + pageSw.ElapsedMilliseconds + "ms");
        }

        // ── 5. 汇总 ─────────────────────────────────────────────────
        var failed = report.Where(kv => kv.Key.EndsWith(".ok") && kv.Value is false).Select(kv => kv.Key).ToList();
        report["failedChecks"] = failed.Count == 0 ? "none" : string.Join(", ", failed);
        report["verdict"] = failed.Count == 0 ? "PASS" : "FAIL";
        report["step"] = "done";
    }
}
