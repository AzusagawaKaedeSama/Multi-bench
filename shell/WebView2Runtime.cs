// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// WebView2 运行时定位。
//
// 为什么要显式指定：WebView2 SDK 若发现 exe 同目录存在 <exe名>.exe.WebView2 文件夹，
// 会自动切到「Fixed Version」部署模式，去那个目录里找运行时。而那个目录里通常只有
// 用户数据（EBWebView），没有 msedgewebview2.exe，结果控制器创建失败：
//   ICoreWebView2Controller QueryInterface 失败（E_NOINTERFACE / 0x80004002）
// 表现是页面永远加载不出来，错误信息却指向完全无关的地方。
//
// 显式传入系统安装的运行时目录即可绕过这个自动探测。

namespace WorkbenchShell;

internal static class WebView2Runtime
{
    private const string AppDir = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application";

    /// <summary>返回可用的 WebView2 运行时目录（含 msedgewebview2.exe），找不到返回 null。</summary>
    internal static string? ResolveDir()
    {
        if (!Directory.Exists(AppDir)) return null;

        // 版本目录名形如 156.0.4314.50，按版本从新到旧取第一个含可执行文件的。
        var candidates = Directory.GetDirectories(AppDir)
            .Select(d => new
            {
                Dir = d,
                Version = Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0),
            })
            .OrderByDescending(x => x.Version);

        foreach (var item in candidates)
        {
            if (File.Exists(Path.Combine(item.Dir, "msedgewebview2.exe"))) return item.Dir;
        }
        return null;
    }
}
