// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 运行期路径：日志与 WebView2 用户数据目录的落点。
//
// 为什么不用 %LOCALAPPDATA%：
//   实测在这台机器上，从新编译的 WorkbenchShell.exe 往
//   C:\Users\<用户>\AppData\Local\WorkbenchShell\ 写文件会抛
//   UnauthorizedAccessException（Access to the path ... is denied），
//   而同一路径用 PowerShell / Python 写却是通的 —— 说明拦截是按
//   「哪个可执行文件在写」来的，而不是目录本身的 ACL。
//   WebView2 的浏览器子进程（msedgewebview2.exe）同样受这条规则约束，
//   结果是用户数据目录写不进去，控制器创建直接报
//   0x800700AA（请求的资源在使用中）。
//
//   所以：两个路径都改放到 exe 同级目录。exe 部署到哪儿，数据就跟到哪儿，
//   目录随程序走，权限跟程序一致，不再受系统按路径设限的影响。
//
// 目录名刻意不叫 <exe名>.WebView2 —— WebView2 SDK 看到 exe 同级存在
// 同名 .WebView2 文件夹会自动切「Fixed Version」部署模式，
// 然后去那个目录里找根本不存在的 msedgewebview2.exe。

namespace WorkbenchShell;

internal static class AppPaths
{
    /// <summary>程序所在目录（自包含发布后就是 exe 所在目录）。</summary>
    internal static string BaseDir { get; } = AppContext.BaseDirectory;

    /// <summary>日志目录。</summary>
    internal static string LogDir => Path.Combine(BaseDir, "logs");

    /// <summary>WebView2 用户数据目录（承载登录态与 localStorage）。</summary>
    internal static string WebViewDataDir => Path.Combine(BaseDir, "wbdata");

    /// <summary>确保目录存在并可写；返回是否成功。</summary>
    internal static bool EnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
