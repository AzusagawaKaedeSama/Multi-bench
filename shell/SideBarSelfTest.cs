// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 侧边栏自检：证明「待审项目默认只铺 6 条，点一下展开其余」这条交互真的通。
//
// 为什么单独写：这一栏的点击是自绘命中区，最典型的失败形态就是「画了、点了没反应」——
// 只截图能看出「有那一行字」，看不出点下去会不会展开。所以这里直接调控件自己的
// OnMouseDown（和用户点击走同一条路径），再读回分组状态。

using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace WorkbenchShell;

internal static class SideBarSelfTest
{
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        Console.WriteLine("[sidebartest] 启动");

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
            var jsonPath = Path.Combine(outputDir, "workbench-shell-sidebartest.json");
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

    // Group 的字段是 public、SideBarPanel 的字段是 private —— 两种都要找得到。
    /// <summary>
    /// 强制控件真实绘制一次。自检窗口摆在屏幕外（不抢前台焦点），
    /// Windows 不一定派 WM_PAINT —— 而侧边栏的命中区和内容高度都是在绘制时生成的。
    /// 不强制画一帧，自检会误报「没有画出展开其余那一行」。
    /// </summary>
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
        catch { }
    }

    private static object? Field(object target, string name) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(target);

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);

    private static async Task RunFlow(MainForm form, Dictionary<string, object?> report)
    {
        var bench = form.Bench;
        report["step"] = "init";
        await form.InitializeForTestAsync();

        var sideBar = bench.SideBarControl;
        if (sideBar is null)
        {
            report["error"] = "没有侧边栏控件";
            report["verdict"] = "FAIL";
            return;
        }

        // 等清单真正拉回来（服务端有缓存，通常 1 秒内）
        report["step"] = "wait-projects";
        var projects = 0;
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250);
            projects = sideBar.ProjectCount;
            if (projects > 0) break;
        }
        report["projectCount"] = projects;
        Check(report, "projectsLoaded", projects > 0, $"count={projects}");
        if (projects <= 0)
        {
            report["verdict"] = "FAIL";
            return;
        }

        await Task.Delay(600);

        var panel = Field(sideBar, "_panel");
        if (panel is null)
        {
            report["error"] = "取不到侧边栏面板";
            report["verdict"] = "FAIL";
            return;
        }

        ForcePaint((Control)panel);
        await Task.Delay(150);

        var groups = Field(panel, "_groups") as System.Collections.IEnumerable;
        object? reviewGroup = null;
        var groupCount = 0;
        foreach (var g in groups ?? Array.Empty<object>())
        {
            groupCount++;
            if (Equals(Field(g!, "Key"), "review")) reviewGroup = g;
        }
        report["groupCount"] = groupCount;
        if (reviewGroup is null)
        {
            report["error"] = "没找到「待审项目」分组";
            report["verdict"] = "FAIL";
            return;
        }

        var items = Field(reviewGroup, "Items") as System.Collections.ICollection;
        var shownBefore = Prop(reviewGroup, "ShownCount");
        var needsRowBefore = Prop(reviewGroup, "NeedsRevealRow");
        report["reviewItems"] = items?.Count ?? -1;
        report["shownBeforeExpand"] = shownBefore;
        report["needsRevealRow"] = needsRowBefore;

        Check(report, "previewLimitedToSix",
            shownBefore is int n && n <= 6 && (items?.Count ?? 0) > 6,
            $"shown={shownBefore} items={items?.Count}");

        // 点「展开其余 N 个」那一行：直接调控件自己的 OnMouseDown，
        // 与用户鼠标点击走完全相同的命中与状态变更路径。
        report["step"] = "click-reveal";
        ForcePaint((Control)panel);
        await Task.Delay(150);
        var revealHits = Field(panel, "_revealHits") as System.Collections.IEnumerable;
        Rectangle? target = null;
        foreach (var hit in revealHits ?? Array.Empty<object>())
        {
            var rect = (Rectangle)hit!.GetType().GetField("Item1")!.GetValue(hit)!;
            var grp = hit.GetType().GetField("Item2")!.GetValue(hit);
            if (ReferenceEquals(grp, reviewGroup)) { target = rect; break; }
        }

        if (target is null)
        {
            report["error"] = "没有画出「展开其余」的命中区";
            report["verdict"] = "FAIL";
            return;
        }
        report["revealRect"] = target.Value.ToString();

        var onMouseDown = panel.GetType().GetMethod("OnMouseDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (onMouseDown is null)
        {
            report["error"] = "取不到 OnMouseDown";
            report["verdict"] = "FAIL";
            return;
        }

        var pt = new Point(target.Value.Left + target.Value.Width / 2, target.Value.Top + target.Value.Height / 2);
        onMouseDown.Invoke(panel, new object[]
        {
            new MouseEventArgs(MouseButtons.Left, 1, pt.X, pt.Y, 0),
        });

        // 展开是插值动画：先记中途值，再等它收敛到全部。
        await Task.Delay(500);
        var showAll = Field(reviewGroup, "ShowAll");
        var shownMid = Prop(reviewGroup, "ShownCount");
        report["showAllAfterClick"] = showAll;
        report["shownMidAnimation"] = shownMid;
        Check(report, "expandStartsGrowing",
            Equals(showAll, true) && shownMid is int mid && mid > 6,
            $"showAll={showAll} shown={shownMid}");

        await Task.Delay(1500);
        var shownAfter = Prop(reviewGroup, "ShownCount");
        report["shownAfterExpand"] = shownAfter;
        Check(report, "clickExpandsAll",
            shownAfter is int m && m == (items?.Count ?? 0),
            $"shown={shownAfter} items={items?.Count}");

        // 再点同一行：从「全部」收回预览态。
        report["step"] = "click-back-to-preview";
        ForcePaint((Control)panel);
        await Task.Delay(150);
        Rectangle? target2 = null;
        foreach (var hit in (Field(panel, "_revealHits") as System.Collections.IEnumerable) ?? Array.Empty<object>())
        {
            var grp = hit!.GetType().GetField("Item2")!.GetValue(hit);
            if (ReferenceEquals(grp, reviewGroup))
            {
                target2 = (Rectangle)hit.GetType().GetField("Item1")!.GetValue(hit)!;
                break;
            }
        }

        if (target2 is null)
        {
            report["error"] = "展开后「收起」那一行的命中区不见了";
            report["verdict"] = "FAIL";
            return;
        }

        var p2 = new Point(target2.Value.Left + target2.Value.Width / 2, target2.Value.Top + target2.Value.Height / 2);
        onMouseDown.Invoke(panel, new object[]
        {
            new MouseEventArgs(MouseButtons.Left, 1, p2.X, p2.Y, 0),
        });
        await Task.Delay(1800);

        var showAllBack = Field(reviewGroup, "ShowAll");
        var shownBack = Prop(reviewGroup, "ShownCount");
        report["showAllAfterCollapse"] = showAllBack;
        report["shownBackToPreview"] = shownBack;
        Check(report, "clickBackToPreview",
            Equals(showAllBack, false) && shownBack is int back && back == 6,
            $"showAll={showAllBack} shown={shownBack}");

        // 再点组标题：整组应收起（用户说的「也可以完全收起」）
        report["step"] = "click-collapse";
        ForcePaint((Control)panel);
        await Task.Delay(150);
        var hits = Field(panel, "_hits") as System.Collections.IEnumerable;
        Rectangle? headerRect = null;
        foreach (var hit in hits ?? Array.Empty<object>())
        {
            var grp = hit!.GetType().GetField("Item2")!.GetValue(hit);
            var entry = hit.GetType().GetField("Item3")!.GetValue(hit);
            if (ReferenceEquals(grp, reviewGroup) && entry is null)
            {
                headerRect = (Rectangle)hit.GetType().GetField("Item1")!.GetValue(hit)!;
                break;
            }
        }

        if (headerRect is null)
        {
            report["error"] = "找不到组标题命中区";
            report["verdict"] = "FAIL";
            return;
        }

        var hp = new Point(headerRect.Value.Left + headerRect.Value.Width / 2,
                           headerRect.Value.Top + headerRect.Value.Height / 2);
        onMouseDown.Invoke(panel, new object[]
        {
            new MouseEventArgs(MouseButtons.Left, 1, hp.X, hp.Y, 0),
        });

        await Task.Delay(500);
        var expanded = Field(reviewGroup, "Expanded");
        report["expandedAfterClick"] = expanded;
        Check(report, "clickCollapsesGroup", Equals(expanded, false), $"expanded={expanded}");

        var failed = report.Where(kv => kv.Key.EndsWith(".ok") && kv.Value is false).Select(kv => kv.Key).ToList();
        report["failedChecks"] = failed.Count == 0 ? "none" : string.Join(", ", failed);
        report["verdict"] = failed.Count == 0 ? "PASS" : "FAIL";
        report["step"] = "done";
    }
}
