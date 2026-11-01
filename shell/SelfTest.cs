// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 自检模式：在命令行内完成「初始化 → 加载页面 → 抓图 → 退出」全过程。
//
// 用途：GUI 进程在自动化环境里常被会话回收，无法长时间观察。
// 自检模式把验证收敛到单次命令内，能给出确定结论与截图证据。
//
// 关键约束：WebView2 的所有成员只能在创建它的 UI 线程上访问。
// 因此这里全程用回调而非 async/await —— await 会让续体跑到线程池线程，
// 报 "CoreWebView2 members can only be accessed from the UI thread"。

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkbenchShell;

internal static class SelfTest
{
    private const string WorkbenchOrigin = "http://127.0.0.1:3900";
    private static string _outputDir = Path.Combine(Path.GetTempPath(), "workbench-selftest");

    internal static int Run(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);

        // 必须在建任何窗口之前设好 DPI 感知，否则自检的渲染比例与真实运行不一致，
        // 截图就会掩盖「字体发虚」这类问题。
        ApplicationConfiguration.Initialize();

        NativeConsole.Attach();
        Console.WriteLine("[selftest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["origin"] = WorkbenchOrigin,
        };

        var form = new Form
        {
            Width = 1440,
            Height = 900,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
        };
        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);
        form.Show();
        // 关键：Show 之后不要立刻 DoEvents / PerformLayout / CreateControl。
        // 那会触发控件布局、提前创建句柄，导致 EnsureCoreWebView2Async 内部
        // 控制器创建时序错位，报 ICoreWebView2Controller QueryInterface 失败。
        // 消息泵要等到 WebView2 初始化流程开始之后再开。

        var done = false;
        var exitCode = 0;

        try
        {
            var runtimeDir = WebView2Runtime.ResolveDir();
            report["webview2RuntimeDir"] = runtimeDir;
            if (runtimeDir is null)
                throw new DirectoryNotFoundException("未找到 WebView2 运行时目录。");

            // 用户数据目录名刻意避开 <exe名>.WebView2：SDK 看到 exe 同级存在
            // 同名 .WebView2 文件夹会自动切「Fixed Version」部署模式，
            // 去那个目录里找根本不存在的 msedgewebview2.exe。
            // 放在 exe 同级（而不是 AppData）：AppData 下写文件会被系统按
            // 「哪个 exe 在写」拦掉，WebView2 浏览器子进程写不进去，
            // 控制器创建就报 0x800700AA。
            var userData = Path.Combine(AppPaths.BaseDir, "wbdata-selftest");
            Directory.CreateDirectory(userData);

            // 关键：整个流程必须在 STA 线程上以 await 链推进。
            // 若用 ContinueWith 而不 await，RunFlow 的续体会掉到线程池线程，
            // WebView2 控制器创建时报 "can only be accessed from the UI thread"。
            //
            // 不能直接 GetAwaiter().GetResult()（会死锁，WebView2 初始化需要 UI 线程泵消息），
            // 因此用「自定义 awaiter」：在 STA 线程上等待，同时泵消息循环。
            var flow = RunFlow(webView, runtimeDir, userData, report);
            flow.GetAwaiter().OnCompleted(() => done = true);

            var deadline = DateTime.UtcNow.AddSeconds(150);
            while (!done && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!done)
            {
                report["error"] = report.ContainsKey("error")
                    ? report["error"]
                    : "超时（150 秒）：WebView2 初始化或页面加载未在期限内完成";
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
            var jsonPath = Path.Combine(outputDir, "workbench-shell-selftest.json");
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

    /// <summary>异步流程：建环境 → 初始化 → 导航 → 探测 → 截图。</summary>
    private static async Task RunFlow(
        WebView2 webView,
        string runtimeDir,
        string userData,
        Dictionary<string, object?> report)
    {
        var bridge = ReadBridge();
        report["step"] = "creating-env";
        Console.WriteLine("[selftest] creating env on thread " + Environment.CurrentManagedThreadId);

        var env = await CoreWebView2Environment.CreateAsync(runtimeDir, userData);
        report["step"] = "env-created";
        Console.WriteLine("[selftest] env created on thread " + Environment.CurrentManagedThreadId);

            await webView.EnsureCoreWebView2Async(env);
            report["step"] = "webview2-ready";
            Console.WriteLine("[selftest] webview2 ready on thread " + Environment.CurrentManagedThreadId);

            // 栅格化比例必须与真实运行时一致，否则截图会掩盖「字体发虚」这个问题。
            // 本机显示器 125%（AppliedDPI=120），默认 1.0 会让页面被拉伸。
            try
            {
                var controller = ControllerAccess.Find(webView);
                if (controller is not null)
                {
                    controller.ShouldDetectMonitorScaleChanges = true;
                    var dpi = webView.DeviceDpi;
                    var scale = dpi > 0 ? dpi / 96f : 1f;
                    var before = controller.RasterizationScale;
                    if (Math.Abs(before - scale) > 0.001)
                        controller.RasterizationScale = scale;
                    report["webviewDeviceDpi"] = dpi;
                    report["rasterScaleBefore"] = before;
                    report["rasterScaleAfter"] = controller.RasterizationScale;
                }
                else
                {
                    report["rasterScaleError"] = "取不到 controller";
                }
            }
            catch (Exception ex) { report["rasterScaleError"] = ex.Message; }

            var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreHostObjectsAllowed = true;

        if (!string.IsNullOrEmpty(bridge))
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(bridge);
            report["bridgeInjected"] = true;
        }

        var navigated = new TaskCompletionSource<bool>();
        core.NavigationCompleted += (_, e) =>
        {
            report["navigationSucceeded"] = e.IsSuccess;
            if (!e.IsSuccess) report["navigationError"] = e.WebErrorStatus.ToString();
            navigated.TrySetResult(e.IsSuccess);
        };

        core.Navigate(WorkbenchOrigin + "/#creative");
        await navigated.Task;

        // 等前端拉完数据并渲染卡片。
        // 注意：ExecuteScriptAsync 返回的是「结果字面量」（如 "131"），
        // 不是含 "review-card" 的代码片段 —— 早先用 json.Contains("review-card")
        // 判断永远为假，等于每次都白等满额时长。
        // 后端 /api/workbench 冷盘首访可能要 15~25 秒（扫审阅页目录 + 解析 13MB catalog），
        // 这里给到 45 秒。
        var rendered = false;
        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(500);
            try
            {
                var json = await core.ExecuteScriptAsync(
                    "document.querySelectorAll('.review-card').length");
                var text = json.Trim();
                if (int.TryParse(text, out var count) && count > 0) { rendered = true; break; }
            }
            catch { /* 继续等 */ }
        }
        report["cardsRendered"] = rendered;
        // 归档列表是页面自己异步补拉的（/api/review-pages?scope=all），
        // 多等一会儿，探针才能读到「作品稿归档」里真的有条目。
        await Task.Delay(3000);

        await Probe(webView, report);
    }

    private static async Task Probe(WebView2 webView, Dictionary<string, object?> report)
    {
        try
        {
            const string script = @"JSON.stringify({
                title: document.title,
                cards: document.querySelectorAll('.review-card').length,
                pendingReviewCards: document.querySelectorAll('#review-project-cards a').length,
                archiveCards: document.querySelectorAll('#review-history-cards a').length,
                rows: document.querySelectorAll('#automation-rows tr').length,
                bridge: typeof window.__workbench !== 'undefined',
                menuTargets: document.querySelectorAll('[data-menu-target]').length,
                reviewTargets: document.querySelectorAll('[data-menu-target^=""review:""]').length,
                taskTargets: document.querySelectorAll('[data-menu-target^=""task:""]').length,
                connection: (document.getElementById('connection-text')||{}).textContent || '',
                hasHash: location.hash,
                dpr: devicePixelRatio,
                cssW: innerWidth,
                cssH: innerHeight,
                screenW: screen.width,
                screenH: screen.height
            })";

            var json = await webView.CoreWebView2.ExecuteScriptAsync(script);
            report["pageProbe"] = json;
            try { report["page"] = JsonSerializer.Deserialize<JsonElement>(json); } catch { }
        }
        catch (Exception ex)
        {
            report["probeError"] = ex.Message;
        }

        try
        {
            var shotPath = Path.Combine(_outputDir, "workbench-shell-selftest.png");
            using (var stream = new FileStream(shotPath, FileMode.Create, FileAccess.Write))
            {
                await webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, stream);
            }
            report["screenshot"] = shotPath;
            report["screenshotBytes"] = new FileInfo(shotPath).Length;
        }
        catch (Exception ex)
        {
            report["screenshotError"] = ex.Message;
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            report["workingSetMB"] = Math.Round(self.WorkingSet64 / 1024.0 / 1024.0, 1);

            // 物理窗口尺寸 vs 截图尺寸：两者相等说明按 1:1 渲染（锐利）；
            // 截图明显小于物理尺寸，说明页面被降分辨率渲染再拉伸（发虚）。
            report["formSizeWinForms"] = $"{webView.Parent?.Width}x{webView.Parent?.Height}";
            report["formPhysicalRect"] = PhysicalRect(webView.FindForm());
        }
        catch { }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    /// <summary>取窗口的物理像素矩形（PerMonitor 感知下 GetWindowRect 返回真实像素）。</summary>
    private static string PhysicalRect(Form? form)
    {
        try
        {
            if (form is null) return "(无窗体)";
            var h = form.Handle;
            if (!GetWindowRect(h, out var r)) return "(GetWindowRect 失败)";
            return $"{r.Right - r.Left}x{r.Bottom - r.Top}";
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    /// <summary>
    /// 生命周期自检：验证「用户主动退出标记」这条契约的完整闭环。
    ///
    /// watchdog 的判定规则是：进程不在 + 标记存在 → 不拉回（用户主动退出）；
    /// 进程不在 + 标记缺失 → 拉回（崩溃/被杀）。这条规则跨越两个进程，
    /// 必须保证标记的写与清都跑在真实代码路径上，不能只测一半。
    /// </summary>
    internal static int RunLifecycle(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        NativeConsole.Attach();

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["markerPath"] = MarkerPath(),
        };

        var exitCode = 0;
        try
        {
            // 干净起点：把上一次遗留的标记删掉。
            TryDeleteMarker();

            // 1) 正常启动 → 构造函数必须清掉遗留标记。
            using (var form = new MainForm(false))
            {
                var clearedOnStartup = !File.Exists(MarkerPath());
                report["clearedOnStartup"] = clearedOnStartup;
                if (!clearedOnStartup)
                {
                    report["error"] = "启动后标记没有被清除 —— watchdog 会误判为「用户已退出」而不再拉回。";
                    exitCode = 1;
                }

                // 2) 走真实退出路径 → 必须写标记。
                form.PrepareForExit();
                var markedOnExit = File.Exists(MarkerPath());
                report["markedOnExit"] = markedOnExit;
                report["markerContent"] = markedOnExit ? File.ReadAllText(MarkerPath()) : "";
                if (!markedOnExit)
                {
                    report["error"] = "退出后没有写标记 —— 崩溃后不会被 watchdog 拉回。";
                    exitCode = 1;
                }
            }

            // 3) 再启动一次 → 标记必须被清掉，否则下次崩溃就拉不回来。
            using (var form2 = new MainForm(false))
            {
                var clearedAgain = !File.Exists(MarkerPath());
                report["clearedOnNextStartup"] = clearedAgain;
                if (!clearedAgain)
                {
                    report["error"] = "第二次启动没有清掉标记。";
                    exitCode = 1;
                }
            }

            // 清理：自检不该给 watchdog 留下「用户已退出」的假象。
            TryDeleteMarker();
            report["markerCleanedAfterTest"] = !File.Exists(MarkerPath());
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        finally
        {
            report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            report["verdict"] = exitCode == 0 ? "PASS" : "FAIL";
            var jsonPath = Path.Combine(outputDir, "workbench-shell-lifecycle.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("REPORT: " + jsonPath);
        }

        return exitCode;
    }

    private static string MarkerPath() =>
        Path.Combine(AppContext.BaseDirectory, "shell-state.txt");

    private static void TryDeleteMarker()
    {
        try { if (File.Exists(MarkerPath())) File.Delete(MarkerPath()); }
        catch { }
    }

    /// <summary>读取编译进 exe 的 bridge.js（与 MainForm 用同一份）。</summary>
    private static string ReadBridge()
    {
        try
        {
            using var stream = typeof(SelfTest).Assembly
                .GetManifestResourceStream("WorkbenchShell.bridge.js");
            if (stream is null) return string.Empty;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch { return string.Empty; }
    }
}
