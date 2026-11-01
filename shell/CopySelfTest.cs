// 作者：workbench-ux（DeepSeek-V4.1-Flash-1017）· 2026-11-01
// 右键复制自检：四个页面 × 三个动作，逐个把结果落到证据上。
//
// 为什么必须这么测：
//   右键菜单的「有没有那一项」只是半件事，另一半是点下去剪贴板里到底有没有东西。
//   用户报的就是后者：菜单看着有「复制图片」，点了什么都没有。
//   所以这里对每个页面真的去点菜单项，然后读 Clipboard 的实际内容（位图尺寸 / 文本 / Test-Path）。
//
// 覆盖：
//   3900 首页卡片 / 3900 作品与资产 / 3955 审阅页 / 3958 门禁确认页
//   每个页面：复制图片（位图尺寸）、复制本地路径（文本 + 文件存在）、复制图片地址（文本 = src）

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace WorkbenchShell;

internal static class CopySelfTest
{
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Console.WriteLine("[copytest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        var form = new MainForm(false) { SilentMode = true };
        var exitCode = 0;
        var done = false;

        try
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            var flow = RunFlow(form, report);
            flow.GetAwaiter().OnCompleted(() => done = true);

            var deadline = DateTime.UtcNow.AddSeconds(300);
            while (!done && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!done) { report["error"] = "超时（300 秒）"; exitCode = 1; }
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
            var jsonPath = Path.Combine(outputDir, "workbench-shell-copytest.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
            try { form.Close(); } catch { }
        }

        return exitCode;
    }

    /// <summary>记录型宿主：把菜单发出的通知原样收进报告，验证「失败有可见提示」。</summary>
    private sealed class RecordingHost : IContextMenuHost
    {
        private readonly MainForm _inner;
        public readonly List<string> Notices = new();
        public RecordingHost(MainForm inner) => _inner = inner;

        public void Notify(string title, string body)
        {
            Notices.Add(title + " | " + body);
            Console.WriteLine("[notify] " + title + " | " + body);
        }

        public void OpenUrlInTab(string url) => _inner.OpenUrlInTab(url);
        public Task OpenUrlInNewWindowAsync(string title, string url) => _inner.OpenUrlInNewWindowAsync(title, url);
        public void SendEditCommandTo(TabItem tab, string command) => _inner.SendEditCommandTo(tab, command);
        public void PostToPage(TabItem tab, JsonObject payload) => _inner.PostToPage(tab, payload);
        public string TakePendingContextTarget(TabItem tab) => _inner.TakePendingContextTarget(tab);
    }

    private static async Task RunFlow(MainForm form, Dictionary<string, object?> report)
    {
        var bench = form.Bench;
        report["step"] = "init";
        await form.InitializeForTestAsync();

        var host = new RecordingHost(form);
        var builder = new NativeContextMenu(host);

        // 四个页面。3955 那条用带真实相对路径图片的审阅页（./v0010_*.jpg）。
        var pages = new (string Key, string Label, string Url)[]
        {
            ("3900-creative", "3900 首页卡片", "http://127.0.0.1:3900/#creative"),
            ("3900-results", "3900 作品与资产", "http://127.0.0.1:3900/#results"),
            ("3955-review", "3955 审阅页", "http://127.0.0.1:3955/D/2-%E8%A7%86%E9%A2%91%E5%88%B6%E4%BD%9C/261021-%E9%80%89%E5%93%AA%E4%B8%AA%EF%BC%9F/E-AePrOutput/v0010_%E7%BE%8E%E6%B3%A2%E9%87%8C%E8%87%AA%E7%84%B6%E7%89%B5%E5%8A%A8%E5%AE%A1%E9%98%85.html"),
            ("3958-gate", "3958 门禁确认页", "http://127.0.0.1:3958/"),
            ("3955-viewer", "3955 查看器（短链）", "http://127.0.0.1:3955/view?doc=docs/moss-style-auto.json"),
        };

        var rows = new List<object>();
        foreach (var (key, label, url) in pages)
        {
            report["step"] = "open:" + key;
            var row = new Dictionary<string, object?> { ["key"] = key, ["label"] = label, ["url"] = url };

            TabItem? tab = null;
            try
            {
                tab = await bench.OpenTab(label, url);
                if (tab is null) { row["error"] = "开标签失败"; rows.Add(row); continue; }

                var ready = await WaitReadyAsync(tab);
                row["pageReady"] = ready;
                row["pageUrl"] = tab.Url;

                var probe = await ProbeFirstImageAsync(tab);
                row["domImages"] = probe?.Total ?? 0;
                row["imgSrc"] = probe?.Src ?? "";
                row["imgSize"] = probe is null ? "" : probe.Width + "x" + probe.Height;
                if (probe is null || string.IsNullOrEmpty(probe.Src))
                {
                    row["error"] = "页面上没找到图片";
                    rows.Add(row);
                    continue;
                }

                var info = new ContextInfo
                {
                    ImgSrc = probe.Src,
                    ImgAlt = probe.Alt,
                    ImgWidth = probe.Width,
                    ImgHeight = probe.Height,
                    PageUrl = url,
                };

                var menu1 = builder.BuildMenu(tab, info);
                var items1 = Flatten(menu1);
                row["menuItems"] = items1;
                row["hasCopyImage"] = items1.Contains("复制图片");
                row["hasCopyPath"] = items1.Contains("复制本地路径");
                row["hasCopyUrl"] = items1.Contains("复制图片地址") || items1.Contains("复制链接地址");

                // ① 复制图片 → 剪贴板里有没有位图
                host.Notices.Clear();
                Clipboard.Clear();
                var clicked = await ClickAndWaitAsync(menu1, "复制图片", tab);
                var img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
                row["copyImage.clicked"] = clicked;
                row["copyImage.clipboardImage"] = img is null ? "none" : img.Width + "x" + img.Height;
                row["copyImage.notice"] = string.Join(" / ", host.Notices);
                row["copyImage.ok"] = img is not null && img.Width > 8 && img.Height > 8;

                // ② 复制本地路径 → 文本 + Test-Path
                host.Notices.Clear();
                Clipboard.Clear();
                var clickedPath = await ClickAndWaitAsync(menu1, "复制本地路径", tab);
                var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                row["copyPath.clicked"] = clickedPath;
                row["copyPath.text"] = text;
                row["copyPath.exists"] = !string.IsNullOrWhiteSpace(text) && (File.Exists(text) || Directory.Exists(text));
                row["copyPath.notice"] = string.Join(" / ", host.Notices);
                row["copyPath.ok"] = clickedPath && row["copyPath.exists"] is true;

                // ③ 复制图片地址
                host.Notices.Clear();
                Clipboard.Clear();
                var clickedUrl = await ClickAndWaitAsync(menu1, "复制图片地址", tab);
                var urlText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                row["copyUrl.clicked"] = clickedUrl;
                row["copyUrl.text"] = urlText;
                row["copyUrl.ok"] = clickedUrl && urlText.Trim() == probe.Src.Trim();

                // 最后再复制一次图片：自检进程退出后剪贴板里留着位图，
                // 外面的 PowerShell 就能用 Get-Clipboard -Format Image 独立复核（这是验收用的那条命令）。
                host.Notices.Clear();
                Clipboard.Clear();
                await ClickAndWaitAsync(menu1, "复制图片", tab);
                var finalImg = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
                row["copyImage.final"] = finalImg is null ? "none" : finalImg.Width + "x" + finalImg.Height;

                menu1?.Dispose();

                // 3900 首页的审阅卡片：右键菜单要有「复制页面路径 / 复制页面地址 / 在资源管理器中显示」。
                if (key == "3900-creative")
                {
                    row["card"] = await TestCardMenuAsync(builder, host, tab, url);
                }

                rows.Add(row);
            }
            catch (Exception ex)
            {
                row["error"] = ex.Message;
                rows.Add(row);
            }
        }

        report["matrix"] = rows;
        report["allNotices"] = host.Notices;

        var failed = rows.OfType<Dictionary<string, object?>>()
            .Where(r => r.ContainsKey("error")
                        || (r.TryGetValue("copyImage.ok", out var v) && v is false)
                        || (r.TryGetValue("card", out var c) && c is Dictionary<string, object?> cm && cm.TryGetValue("ok", out var cok) && cok is false))
            .Select(r => r["key"]).ToList();
        report["pagesFailed"] = failed;
        report["pagesWithoutImageCopy"] = rows.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("copyImage.ok", out var v) && v is false)
            .Select(r => r["key"]).ToList();
        report["verdict"] = failed.Count == 0 ? "PASS" : "FAIL";
        report["step"] = "done";
    }

    /// <summary>卡片右键三项：数据来自 3900 的 /api/review-pages（id → page_path / url）。</summary>
    private static async Task<Dictionary<string, object?>> TestCardMenuAsync(
        NativeContextMenu builder, RecordingHost host, TabItem tab, string pageUrl)
    {
        var row = new Dictionary<string, object?>();
        var core = tab.View?.CoreWebView2;
        if (core is null) { row["ok"] = false; row["error"] = "没有 WebView2"; return row; }

        string target = "";
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "(function(){var el=document.querySelector('[data-menu-target^=\"review:\"]');" +
                "return el?el.getAttribute('data-menu-target'):'';})()");
            target = Unwrap(raw) ?? "";
        }
        catch (Exception ex) { row["error"] = ex.Message; }

        row["target"] = target;
        if (!target.StartsWith("review:", StringComparison.Ordinal))
        {
            row["ok"] = false;
            if (!row.ContainsKey("error")) row["error"] = "页面里没有 review 卡片";
            return row;
        }

        var info = new ContextInfo { Target = target, PageUrl = pageUrl };
        using var menu = builder.BuildMenu(tab, info);
        var items = Flatten(menu);
        row["items"] = items;
        row["hasCopyPagePath"] = items.Contains("复制页面路径");
        row["hasCopyPageUrl"] = items.Contains("复制页面地址");
        row["hasReveal"] = items.Contains("在资源管理器中显示");

        host.Notices.Clear();
        Clipboard.Clear();
        var clickedPath = await ClickAndWaitAsync(menu, "复制页面路径", tab);
        var pathText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        row["pagePath.clicked"] = clickedPath;
        row["pagePath.text"] = pathText;
        row["pagePath.exists"] = !string.IsNullOrWhiteSpace(pathText) && File.Exists(pathText);
        row["pagePath.notice"] = string.Join(" / ", host.Notices);

        host.Notices.Clear();
        Clipboard.Clear();
        var clickedUrl = await ClickAndWaitAsync(menu, "复制页面地址", tab);
        var urlText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        row["pageUrl.clicked"] = clickedUrl;
        row["pageUrl.text"] = urlText;
        row["pageUrl.ok"] = urlText.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        row["pageUrl.notice"] = string.Join(" / ", host.Notices);

        row["ok"] = row["hasCopyPagePath"] is true && row["hasCopyPageUrl"] is true
                    && row["hasReveal"] is true && row["pagePath.exists"] is true && row["pageUrl.ok"] is true;
        return row;
    }

    private sealed class ImageProbe
    {
        public string Src = "";
        public string Alt = "";
        public int Width;
        public int Height;
        public int Total;
        public bool LocalPreferred;
    }

    /// <summary>等页面把「够大的图」渲染出来。首页卡片是 JS 异步渲染的，只等 readyState 会抢跑。</summary>
    private static async Task<bool> WaitReadyAsync(TabItem tab)
    {
        // 120 × 250ms = 30 秒：3900 的作品页封面是异步 + 懒加载（外部图），
        // 20 秒有时不够，抓不到就会误报「页面上没找到图片」。
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(250);
            Application.DoEvents();
            var core = tab.View?.CoreWebView2;
            if (core is null) continue;
            try
            {
                var raw = await core.ExecuteScriptAsync(
                    "(function(){var n=0;var l=Array.prototype.slice.call(document.images||[]);" +
                    "n+=l.filter(function(i){return (i.naturalWidth||0)>=120&&i.getBoundingClientRect().width>40;}).length;" +
                    "Array.prototype.slice.call(document.querySelectorAll('iframe')).forEach(function(f){" +
                    "try{var d=f.contentDocument;if(!d)return;var il=Array.prototype.slice.call(d.images||[]);" +
                    "n+=il.filter(function(i){return (i.naturalWidth||0)>=120;}).length;}catch(e){}});" +
                    "return JSON.stringify({s:document.readyState,big:n,i:document.images?document.images.length:0});})()");
                var json = Unwrap(raw);
                if (json is null) continue;
                var node = JsonNode.Parse(json) as JsonObject;
                var state = node?["s"]?.ToString();
                var big = (int?)node?["big"] ?? 0;
                if (state == "complete" && big > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static async Task<ImageProbe?> ProbeFirstImageAsync(TabItem tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return null;
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "(function(){" +
                "function pick(doc){var l=Array.prototype.slice.call(doc.images||[]);" +
                "var ok=l.filter(function(i){var s=(i.currentSrc||i.src||'');if(!s)return false;if(s.indexOf('data:')===0)return false;return s.slice(0,5).toLowerCase()!=='svg:'&&!/[.]svg([?]|$)/i.test(s);});" +
                "var big=ok.filter(function(i){return (i.naturalWidth||0)>=120&&i.getBoundingClientRect().width>40;});" +
                "if(!big.length)big=ok;" +
                "return {list:l,big:big};}" +
                "var r=pick(document);var total=r.list.length;var hit=r.big[0]||null;" +
                "if(!hit){Array.prototype.slice.call(document.querySelectorAll('iframe')).forEach(function(f){" +
                " if(hit)return;try{var d=f.contentDocument;if(!d)return;var x=pick(d);total+=x.list.length;hit=x.big[0]||null;}catch(e){}});}" +
                "if(!hit)return 'null';" +
                "var srcs=r.big.map(function(i){return i.currentSrc||i.src||''}).filter(Boolean).slice(0,30);" +
                "return JSON.stringify({src:hit.currentSrc||hit.src||'',srcs:srcs,alt:hit.alt||'',w:hit.naturalWidth||0,h:hit.naturalHeight||0,total:total});})()");
            var json = Unwrap(raw);
            if (json is null) return null;
            var node = JsonNode.Parse(json) as JsonObject;
            if (node is null) return null;
            var probe = new ImageProbe
            {
                Src = node["src"]?.ToString() ?? "",
                Alt = node["alt"]?.ToString() ?? "",
                Width = (int?)node["w"] ?? 0,
                Height = (int?)node["h"] ?? 0,
                Total = (int?)node["total"] ?? 0,
            };

            // 页面上往往同时有「外链封面」和「本地审阅图」。优先选壳能映射成本地路径的那张 ——
            // 用户右键审阅页里的图，要的就是它。没有可映射的就用第一张。
            try
            {
                var srcs = node["srcs"] as JsonArray;
                if (srcs is not null)
                {
                    foreach (var item in srcs)
                    {
                        var candidate = item?.ToString() ?? "";
                        if (candidate.Length == 0) continue;
                        if (WebAsset.ToLocalPath(candidate) is not null) { probe.Src = candidate; probe.LocalPreferred = true; break; }
                    }
                }
            }
            catch { }

            return probe;
        }
        catch { return null; }
    }

    private static string? Unwrap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try
        {
            var text = raw;
            if (text.StartsWith("\"")) text = JsonSerializer.Deserialize<string>(text) ?? "";
            if (string.IsNullOrWhiteSpace(text) || text == "null") return null;
            return text;
        }
        catch { return null; }
    }

    private static List<string> Flatten(ContextMenuStrip? menu)
    {
        var list = new List<string>();
        if (menu is null) return list;
        foreach (ToolStripItem item in menu.Items)
        {
            list.Add(item.Text);
            if (item is ToolStripMenuItem mi)
                foreach (ToolStripItem sub in mi.DropDownItems) list.Add(mi.Text + " / " + sub.Text);
        }
        return list;
    }

    private static async Task<bool> ClickAndWaitAsync(ContextMenuStrip? menu, string text, TabItem tab)
    {
        if (menu is null) return false;
        ToolStripMenuItem? hit = null;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item.Text == text) { hit = item as ToolStripMenuItem; break; }
        }
        if (hit is null) return false;

        hit.PerformClick();
        // 复制动作是 async fire-and-forget：给它时间进剪贴板，同时泵消息。
        // 60 × 50ms = 3 秒：卡片路径那类要走一次 HTTP 反查，第一次冷启动比图片复制慢。
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(50);
            Application.DoEvents();
            if (Clipboard.ContainsImage() || Clipboard.ContainsText()) break;
        }
        return true;
    }
}
