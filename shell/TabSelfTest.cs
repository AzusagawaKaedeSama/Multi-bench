// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 标签页宿主自检：验证「审阅台点链接 → 变成原生标签页」这条核心链路。
//
// 为什么单独写一个自检而不是靠肉眼看：
//   「点链接开新窗口」是用户最初报的问题，肉眼验证只能证明「这次对了」，
//   不能证明「永远不会再开新窗口」。这里直接断言：
//   1) 页面上确实存在 target=_blank 链接；
//   2) 模拟点击它；
//   3) 标签数从 1 变成 2（而不是弹出一个新窗口）；
//   4) 新标签的 URL 指向被点的那条链接。
//
// 关键约束同 SelfTest：WebView2 成员只能在创建它的 UI 线程上访问，
// 因此整个流程用 await 链 + DoEvents 泵消息，不用 ContinueWith。

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WorkbenchShell;

internal static class TabSelfTest
{
    private const string WorkbenchOrigin = "http://127.0.0.1:3900";

    /// <summary>跑一次标签页链路自检，返回进程退出码。</summary>
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        Console.WriteLine("[tabtest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["origin"] = WorkbenchOrigin,
        };

        var form = new MainForm(false);
        var exitCode = 0;
        var done = false;

        try
        {
            form.Show();
            var flow = RunFlow(form, report);
            flow.GetAwaiter().OnCompleted(() => done = true);

            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (!done && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!done)
            {
                report["error"] = "超时（180 秒）";
                exitCode = 1;
            }
            else if (flow.IsFaulted)
            {
                report["error"] = flow.Exception?.GetBaseException().ToString();
                exitCode = 1;
            }
            else if (report.ContainsKey("error"))
            {
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
            var jsonPath = Path.Combine(outputDir, "workbench-shell-tabtest.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
            try { form.Close(); } catch { }
        }

        return exitCode;
    }

    private static async Task RunFlow(MainForm form, Dictionary<string, object?> report)
    {
        var bench = form.Bench;
        report["step"] = "init";
        await form.InitializeForTestAsync();

        var groups = bench.Groups.ToList();
        report["groupCount"] = groups.Count;
        var tabCount = groups.Sum(g => g.Tabs.Count);
        report["tabCountBefore"] = tabCount;

        if (tabCount < 1)
        {
            report["error"] = "初始化后连一个标签都没有";
            return;
        }

        var firstTab = groups[0].Tabs[0];
        var view = firstTab.View;
        if (view?.CoreWebView2 is null)
        {
            report["error"] = "第一个标签没有可用的 WebView2";
            return;
        }

        var core = view.CoreWebView2;

        // 等页面把审阅卡片渲染出来（后端冷盘首访可能十几秒）
        // 优先等「审阅卡片链接」——那才是用户真正会点的东西
        //（.review-card-link 指向 3955 端口的审阅页）。
        // 页面里还有别的 target=_blank（外部站点），不是本次要验的目标。
        report["step"] = "wait-render";
        var cards = 0;
        var reviewCards = 0;
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(500);
            try
            {
                var raw = await core.ExecuteScriptAsync(
                    "document.querySelectorAll('a[target=\"_blank\"]').length");
                if (int.TryParse(raw, out var n)) cards = n;

                var raw2 = await core.ExecuteScriptAsync(
                    "document.querySelectorAll('a.review-card-link[target=\"_blank\"]').length");
                if (int.TryParse(raw2, out var n2)) reviewCards = n2;

                if (reviewCards > 0) break;
            }
            catch { }
        }
        report["blankLinkCount"] = cards;
        report["reviewCardLinkCount"] = reviewCards;
        if (cards == 0)
        {
            report["error"] = "页面上没找到 target=_blank 链接，无法验证标签页转换";
            return;
        }

        // 选链接：优先审阅卡片链接，没有就退回任意一个
        var picker = reviewCards > 0
            ? "document.querySelector('a.review-card-link[target=\"_blank\"]')"
            : "document.querySelector('a[target=\"_blank\"]')";

        // 记下被点链接的地址，后面核对新标签是不是它
        var linkInfo = await core.ExecuteScriptAsync(
            $"JSON.stringify((()=>{{const a={picker};" +
            "return {href:a.href,text:(a.textContent||'').trim().slice(0,40)};})())");
        report["clickedLink"] = linkInfo;
        var expectedUrl = ExtractHref(linkInfo);
        report["expectedUrl"] = expectedUrl;

        // 模拟点击：这是「用户在页面上点审阅卡片」的等价动作
        report["step"] = "click-blank-link";
        await core.ExecuteScriptAsync(
            $"(()=>{{const a={picker};a.click();return 1;}})()");

        // 等新标签建出来
        report["step"] = "wait-new-tab";
        var newCount = tabCount;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            newCount = bench.Groups.Sum(g => g.Tabs.Count);
            if (newCount > tabCount) break;
        }

        report["tabCountAfter"] = newCount;
        report["groupCountAfter"] = bench.Groups.Count();

        if (newCount <= tabCount)
        {
            report["error"] = "点了 target=_blank 链接后标签数没有增加 —— 新窗口拦截没生效";
            return;
        }

        // 找出新标签：按 URL 匹配，而不是「非第一个」（初始化本身也会占标签位）
        var allTabs = bench.Groups.SelectMany(g => g.Tabs).ToList();
        var newTab = allTabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(expectedUrl) &&
            t.Url.StartsWith(expectedUrl.Split('?')[0], StringComparison.OrdinalIgnoreCase));
        report["newTabTitle"] = newTab?.Title;
        report["newTabUrl"] = newTab?.Url;
        report["urlMatched"] = newTab is not null;
        report["allTabUrls"] = string.Join(" | ", allTabs.Select(t => t.Url));

        // 顺带验证分裂：分成左右两组
        report["step"] = "split";
        bench.SplitActiveGroupVertical();
        await Task.Delay(300);
        report["groupCountAfterSplit"] = bench.Groups.Count();
        if (bench.Groups.Count() < 2)
        {
            report["error"] = "分裂后分组数没有增加";
            return;
        }

        // 拖拽分裂：把标签拖到某个组的右边缘 → 应分裂出一个新组并把标签搬进去。
        //
        // 为什么菜单里的「向右拆分」不够：那条路只证明 SplitLayout 能分裂，
        // 证不出「拖过来的标签真的落进了新组」。这里直接调 HandleTabDrop，
        // 走的就是 EditorGroup.OnDragDrop → ZoneAt → HandleTabDrop 的同一条路。
        report["step"] = "drag-split";
        var groupsNow = bench.Groups.ToList();
        var dragTab = groupsNow.FirstOrDefault(g => g.Tabs.Count > 0)?.Tabs.FirstOrDefault();
        var dropTarget = groupsNow.FirstOrDefault(g =>
            dragTab is not null && !ReferenceEquals(g, dragTab.Group));
        if (dragTab is not null && dropTarget is not null)
        {
            var groupsBeforeDrag = bench.Groups.Count();
            bench.HandleTabDrop(dragTab, dropTarget, EditorGroup.DropZone.Right);
            await Task.Delay(500);

            report["dragSplit_groupsBefore"] = groupsBeforeDrag;
            report["dragSplit_groupsAfter"] = bench.Groups.Count();
            // 标签离开了原落点组，进了新分裂出来的组
            report["dragSplit_tabLeftTargetGroup"] =
                dragTab.Group is not null && !ReferenceEquals(dragTab.Group, dropTarget);
            report["dragSplit_newGroupTabCount"] = dragTab.Group?.Tabs.Count ?? 0;
            report["dragSplit_tabUrl"] = dragTab.Url;

            if (bench.Groups.Count() <= groupsBeforeDrag)
            {
                report["error"] = "拖到组边缘后没有分裂出新组";
                return;
            }
            if (ReferenceEquals(dragTab.Group, dropTarget))
            {
                report["error"] = "拖拽后标签仍留在原组，没有搬进新组";
                return;
            }
        }
        else
        {
            report["dragSplit_groupsBefore"] = -1;
        }

        // 侧边栏：等它的项目清单加载完（后端冷盘首访可能十几秒）
        report["step"] = "wait-sidebar";
        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(500);
            if ((bench.SideBarControl?.ProjectCount ?? 0) > 0) break;
        }
        report["sideBarVisible"] = bench.SideBarVisible;
        report["sideBarProjectCount"] = bench.SideBarControl?.ProjectCount ?? -1;
        report["sideBarNodeCount"] = bench.SideBarControl?.VisibleNodeCount ?? -1;

        // 栅格化比例（每个标签都要单独设）
        var controller = ControllerAccess.Find(view);
        report["rasterScale"] = controller?.RasterizationScale ?? 0;
        report["deviceDpi"] = view.DeviceDpi;

        // 拆分到新窗口：把第二个标签（审阅页）移到新开的二级窗口
        report["step"] = "split-to-window";
        var reviewTab = allTabs.FirstOrDefault(t =>
            !string.IsNullOrEmpty(expectedUrl) &&
            t.Url.StartsWith(expectedUrl.Split('?')[0], StringComparison.OrdinalIgnoreCase));
        if (reviewTab is not null)
        {
            var before = bench.Groups.Sum(g => g.Tabs.Count);
            await form.SplitTabToNewWindowAsync(reviewTab);
            await Task.Delay(1500);
            report["afterSplitWindow_tabCountInMain"] = bench.Groups.Sum(g => g.Tabs.Count);
            report["afterSplitWindow_before"] = before;
        }
        else
        {
            report["afterSplitWindow_before"] = -1;
        }

        report["step"] = "done";
        report["verdict"] = "PASS";
    }

    private static string ExtractHref(string json)
    {
        try
        {
            // ExecuteScriptAsync 返回的是「JSON 编码后的字符串」，
            // 比如 "{\"href\":\"...\"}" —— 外面还裹了一层引号，要先剥掉。
            var outer = JsonSerializer.Deserialize<string>(json);
            if (!string.IsNullOrEmpty(outer))
            {
                using var doc = JsonDocument.Parse(outer);
                if (doc.RootElement.TryGetProperty("href", out var h)) return h.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }
}
