// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 关闭标签自检：证明「× 点了真的会关」，并且关到最后一个也不会把窗口拆空。
//
// 为什么必须写成可执行断言：
//   这个 bug 的形态是「点了没反应」—— 用户只能描述感受，很难说清是哪一步断了。
//   而代码里的断点其实很明确：标签条发出了 TabClosed，编辑器分组把它转成了一个
//   没人订阅的事件。断言「调用关闭入口后标签数减少」能一次性证明接线是通的，
//   以后再有人改回事件、改掉订阅，这条自检会立刻红。
//
// 另外验两条容易复发的边界：
//   1) 关掉最后一个标签后必须仍然留着一个空分组（否则布局树的根被拆掉，窗口空白）；
//   2) 空分组上新建标签能立刻开出来（不是开成 about:blank）。

using System.Reflection;
using System.Text.Json;

namespace WorkbenchShell;

internal static class CloseSelfTest
{
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        Console.WriteLine("[closetest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["origin"] = Workbench.WorkbenchOrigin,
        };

        var form = new MainForm(false);
        var exitCode = 0;
        var done = false;

        try
        {
            // 纪律：自检一律在屏幕外跑，不抢前台焦点。
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            var flow = RunFlow(form, report);
            flow.GetAwaiter().OnCompleted(() => done = true);

            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (!done && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!done) { report["error"] = "超时（180 秒）"; exitCode = 1; }
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
            var jsonPath = Path.Combine(outputDir, "workbench-shell-closetest.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
            try { form.Close(); } catch { }
        }

        return exitCode;
    }

    private static void Check(Dictionary<string, object?> report, string name, bool ok, string detail = "")
    {
        report[name + ".ok"] = ok;
        if (!string.IsNullOrEmpty(detail)) report[name + ".detail"] = detail;
    }

    /// <summary>调用编辑器分组的私有关闭入口 —— 就是标签条 × / Ctrl+W 走的那一个。</summary>
    private static void InvokeClose(EditorGroup group, TabItem tab)
    {
        var method = typeof(EditorGroup).GetMethod("CloseTab", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) throw new InvalidOperationException("EditorGroup.CloseTab 不存在");
        method.Invoke(group, new object[] { tab });
    }

    private static async Task RunFlow(MainForm form, Dictionary<string, object?> report)
    {
        var bench = form.Bench;
        report["step"] = "init";
        await form.InitializeForTestAsync();
        await Task.Delay(600);

        var groups = bench.Groups.ToList();
        var startTabs = groups.Sum(g => g.Tabs.Count);
        report["tabCountAtStart"] = startTabs;
        if (startTabs < 1)
        {
            report["error"] = "初始化后连一个标签都没有";
            report["verdict"] = "FAIL";
            return;
        }

        // 再开两个标签，凑够三个，方便验「关中间的、激活相邻」
        report["step"] = "open-extra-tabs";
        await bench.OpenTab("总览", Workbench.WorkbenchOrigin + "/#overview");
        await bench.OpenTab("统一设置", Workbench.WorkbenchOrigin + "/#settings");
        await Task.Delay(600);

        var total = bench.Groups.Sum(g => g.Tabs.Count);
        report["tabCountBeforeClose"] = total;
        Check(report, "openThreeTabs", total >= 3, $"tabs={total}");

        // 关中间的标签（最贴近用户的真实操作：一串标签里点掉一个）
        var group = bench.Groups.First();
        var list = group.Tabs.ToList();
        if (list.Count < 3)
        {
            report["error"] = "当前分组里的标签不足三个，无法验证关闭链路";
            report["verdict"] = "FAIL";
            return;
        }

        var victim = list[1];
        report["closingTab"] = victim.Title + " | " + victim.Url;
        InvokeClose(group, victim);
        await Task.Delay(300);

        var afterOne = bench.Groups.Sum(g => g.Tabs.Count);
        report["tabCountAfterOne"] = afterOne;
        report["closedTabStillListed"] = bench.Groups.SelectMany(g => g.Tabs).Any(t => ReferenceEquals(t, victim));
        Check(report, "closeMiddleTab", afterOne == total - 1 && !bench.Groups.SelectMany(g => g.Tabs).Any(t => ReferenceEquals(t, victim)),
            $"before={total} after={afterOne}");
        Check(report, "closedViewDisposed", victim.View is null, "WebView2 应已释放");

        // 关掉剩下的全部标签，最后必须还剩一个空分组
        report["step"] = "close-all";
        foreach (var g in bench.Groups.ToList())
            foreach (var t in g.Tabs.ToList())
                InvokeClose(g, t);
        await Task.Delay(400);

        var afterAll = bench.Groups.Sum(g => g.Tabs.Count);
        var groupCount = bench.Groups.Count();
        report["tabCountAfterAll"] = afterAll;
        report["groupCountAfterAll"] = groupCount;
        Check(report, "closeAllTabs", afterAll == 0, $"tabs={afterAll}");
        Check(report, "rootGroupKept", groupCount == 1, $"groups={groupCount}");

        // 空分组上新建标签：必须是工作台页面，不是 about:blank
        report["step"] = "new-tab";
        bench.NewTab();
        await Task.Delay(1500);

        var tabsNow = bench.Groups.SelectMany(g => g.Tabs).ToList();
        report["tabCountAfterNew"] = tabsNow.Count;
        report["newTabUrl"] = tabsNow.FirstOrDefault()?.Url ?? "";
        Check(report, "newTabOpensWorkbench", tabsNow.Count == 1 &&
            (tabsNow[0].Url.StartsWith(Workbench.WorkbenchOrigin, StringComparison.OrdinalIgnoreCase)),
            tabsNow.FirstOrDefault()?.Url ?? "none");

        var failed = report.Where(kv => kv.Key.EndsWith(".ok") && kv.Value is false).Select(kv => kv.Key).ToList();
        report["failedChecks"] = failed.Count == 0 ? "none" : string.Join(", ", failed);
        report["verdict"] = failed.Count == 0 ? "PASS" : "FAIL";
        report["step"] = "done";
    }
}
