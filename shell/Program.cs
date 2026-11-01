// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 工作台原生壳 —— 程序入口。
//
// 定位：这不是「网页套壳」，而是一个真正的 Windows 桌面宿主。
//   · 窗口、菜单、托盘、通知、文件对话框、全局热键、拖放全部是原生 Win32 控件；
//   · WebView2 只承担页面渲染，页面本身零改动；
//   · 服务端（hub.cjs / 3900）继续由本机 watchdog 托管，壳不接管数据。
//
// 单独实例：再次启动时把已有窗口唤到前台，而不是开第二个窗口。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkbenchShell;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private static Mutex? _singleInstance;
    private static MainForm? _mainForm;

    /// <summary>静默模式：不弹任何模态窗（调试与取证时用）。</summary>
    private static bool _silentMode;

    /// <summary>启动探针：写到项目目录，绕开 %LOCALAPPDATA% 的任何不确定性。</summary>
    internal static void Boot(string tag, string detail = "")
    {
        try
        {
            File.AppendAllText(
                @"D:\2-视频制作\0-工作流\工作台原生壳\boot-trace.txt",
                $"[{DateTime.Now:HH:mm:ss.fff}] pid={Environment.ProcessId} {tag}{(detail.Length > 0 ? " | " + detail : "")}\n",
                Encoding.UTF8);
        }
        catch { /* 探针失败不影响主流程 */ }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // ★ 调试/取证模式一律静默：不弹模态窗、不抢焦点、不动用户鼠标。
        _silentMode = args.Any(a =>
            a is "--open" or "--menu-shot" or "--ui-shot" or "--selftest-tabs"
              or "--selftest" or "--selftest-menu" or "--selftest-refresh"
              or "--selftest-sidebar" or "--selftest-close" or "--selftest-copy");

        // ★ 启动探针：写死到项目目录，绕开 %LOCALAPPDATA% 的一切不确定性。
        //   用途：一旦「进程活着但日志一个字没有」，先看它到底走到哪一行。
        Boot("Main.enter", string.Join(" | ", args));
        try
        {
            Boot("env.LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Boot("env.LOGDIR",
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkbenchShell"));
        }
        catch (Exception ex) { Boot("env.FAIL", ex.Message); }

        // 自检模式：单次命令内完成验证并输出报告，不进入常驻消息循环。
        // 关键：整个流程必须在 STA 线程同步跑完，不能用 async Main ——
        // await 会把后续代码切到线程池线程，WebView2 创建窗口时报 RPC_E_CHANGED_MODE。
        if (args.Length > 0 && args[0] == "--selftest")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-selftest");
            var exitCode = SelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 生命周期自检：只验「主动退出标记」的写与清，不开窗口、不加载页面。
        if (args.Length > 0 && args[0] == "--selftest-lifecycle")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-lifecycle");
            var exitCode = SelfTest.RunLifecycle(outputDir);
            Environment.Exit(exitCode);
        }

        // 标签页自检：验证「审阅台点链接 → 原生标签页」这条核心链路。
        if (args.Length > 0 && args[0] == "--selftest-tabs")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-tabtest");
            var exitCode = TabSelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 关闭标签自检：验证「× 点了真的会关」以及「关完最后一个还留着放标签的地方」。
        if (args.Length > 0 && args[0] == "--selftest-close")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-closetest");
            var exitCode = CloseSelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 设置窗口离屏取证：五页各出一张图，不碰用户屏幕。
        if (args.Length > 1 && args[0] == "--settings-shot")
        {
            var exitCode = SettingsShot.Run(args[1]);
            Environment.Exit(exitCode);
        }

        // 右键复制自检：四个页面 × 三个动作（复制图片 / 复制本地路径 / 复制图片地址），
        // 逐个点菜单项并读剪贴板的实际内容。
        if (args.Length > 0 && args[0] == "--selftest-copy")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-copytest");
            var exitCode = CopySelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 刷新自检：验证两个刷新入口都真的打 ?fresh=1，且刷新过程保住侧边栏展开 / 滚动 / 当前标签。
        if (args.Length > 0 && args[0] == "--selftest-refresh")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-refreshtest");
            var exitCode = RefreshSelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 侧边栏自检：验证「待审项目默认只铺几条、点一下展开、再点组标题收起」。
        if (args.Length > 0 && args[0] == "--selftest-sidebar")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-sidebartest");
            var exitCode = SideBarSelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 右键菜单自检：验证「右键图片 / 超链接时菜单里该有哪些项」。
        if (args.Length > 0 && args[0] == "--selftest-menu")
        {
            var outputDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "workbench-menutest");
            var exitCode = MenuSelfTest.Run(outputDir);
            Environment.Exit(exitCode);
        }

        // 右键菜单离屏取证：不碰用户屏幕，直接产出「菜单长什么样」的证据图。
        if (args.Length > 2 && args[0] == "--menu-shot")
        {
            var exitCode = MenuShot.Run(args[1], args[2]);
            Environment.Exit(exitCode);
        }

        // 整窗离屏取证：把新版界面在虚拟屏外渲染成 PNG，4 套主题各一张。
        // 用法：--ui-shot <输出目录> [--all-themes] [--url <地址>]
        // --url：只截这一个页面（用真实壳环境）。图片门禁确认页靠它取证。
        if (args.Length > 1 && args[0] == "--ui-shot")
        {
            var outDir = args[1];
            var allThemes = args.Any(a => a == "--all-themes");
            var urlIndex = Array.IndexOf(args, "--url");
            var targetUrl = urlIndex >= 0 && args.Length > urlIndex + 1 ? args[urlIndex + 1] : null;
            var exitCode = UiShot.Run(outDir, allThemes, targetUrl);
            Environment.Exit(exitCode);
        }

        ApplicationConfiguration.Initialize();
        Boot("appcfg.done");

        // 日志先清再写：清空动作必须早于任何日志写入，
        // 否则 MainForm 构造函数里的 Reset 会把「启动参数」这条关键线索冲掉。
        RunLog.Reset();
        Boot("runlog.reset");
        RunLog.Write("Program.main", $"args=[{string.Join(",", args)}]");
        Boot("runlog.write");

        // --tray：静默进托盘，不弹窗（开机自启用）。
        var startInTray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        // 单实例：第二次启动时唤起已有窗口。
        _singleInstance = new Mutex(true, @"Local\WorkbenchShellSingleton", out bool createdNew);
        if (!createdNew)
        {
            // 后台启动（开机自启 / watchdog 恢复）不该把窗口拽出来：
            // 开机时 Startup 快捷方式与 watchdog 可能同时触发，谁先跑谁是主实例，
            // 后来的那个如果广播唤回，窗口就会在登录瞬间弹出来，破坏静默启动。
            // 只有用户主动双击桌面快捷方式（无 --tray）才广播唤回。
            if (!startInTray) NativeMethods.BroadcastShowRequest();
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        RunLog.Write("Program.main", $"args={string.Join(",", args)}");

        // 演示模式：自动摆出「多标签 + 左右分裂 + 拆分到新窗口」的完整形态，然后驻留。
        // 自检能断言分组数变了，但证不出界面长得对不对 —— 那得让人看。
        if (args.Length > 0 && args[0] == "--demo-tabs")
        {
            var demoForm = new MainForm(false);
            demoForm.Shown += async (_, _) => await demoForm.RunDemoAsync();
            _mainForm = demoForm;
            Application.Run(demoForm);
            RunLog.Write("Program.run.end");
            return;
        }

        // --open <url>：直接打开指定地址（取证、调试用）。
        // 用来验证「在审阅页上右键图片」这类只有真实页面才跑得起来的路径。
        if (args.Length > 1 && args[0] == "--open")
        {
            var url = args[1];
            Boot("open.branch", url);
            var openForm = new MainForm(false) { SkipHomePage = true, SilentMode = true };
            Boot("open.form.ctor");
            openForm.Shown += async (_, _) =>
            {
                Boot("open.shown");
                await openForm.InitializeForTestAsync();
                Boot("open.init.done");
                await Task.Delay(1000);
                Boot("open.before-tab");
                await openForm.OpenUrlInTabAsync(url);
                Boot("open.after-tab");
            };
            _mainForm = openForm;
            Boot("open.run");
            Application.Run(openForm);
            RunLog.Write("Program.run.end");
            return;
        }

        _mainForm = new MainForm(startInTray);
        RunLog.Write("Program.run.begin");
        Application.Run(_mainForm);
        RunLog.Write("Program.run.end");
    }

    private static void ReportCrash(Exception? error)
    {
        if (error is null) return;

        // 先落盘：弹窗只能显示一句话，排查时要的是完整堆栈。
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkbenchShell");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "shell-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{error}\n\n");
        }
        catch { /* 落盘失败也不能挡住弹窗 */ }

        // 调试/取证模式下不弹窗：调试不该打断用户正在做的事。
        if (_silentMode) return;

        NativeMethods.ShowError("工作台壳发生未处理错误", error.ToString());
    }

    internal static void BringToFront(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
            ShowWindow(form.Handle, SW_RESTORE);
        form.Show();
        form.Activate();
        SetForegroundWindow(form.Handle);
    }
}
