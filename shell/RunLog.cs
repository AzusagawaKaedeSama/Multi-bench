// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 运行日志：把关键节点写到 %LOCALAPPDATA%\WorkbenchShell\shell-run.log。
//
// 为什么需要：崩溃弹窗只能显示一句话，进程「静默消失」时更是毫无线索。
// 这份日志让「什么时候死的、死在哪个环节」变得可查。

namespace WorkbenchShell;

using System.Text;

internal static class RunLog
{
    private static readonly object Gate = new();

    /// <summary>日志落点：exe 同级 logs/ 目录。理由见 AppPaths 的注释。</summary>
    private static readonly string Path =
        System.IO.Path.Combine(AppPaths.LogDir, "shell-run.log");

    public static void Write(string stage, string detail = "")
    {
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {stage}{(detail.Length > 0 ? " | " + detail : "")}\n",
                    Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Program.Boot("RunLog.Write.FAIL", $"{stage} :: {ex.GetType().Name} {ex.Message} :: path={Path}");
        }
    }

    /// <summary>清空日志（启动时用，避免旧内容混淆）。</summary>
    public static void Reset()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, $"--- 新会话 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Program.Boot("RunLog.Reset.FAIL", $"{ex.GetType().Name} {ex.Message} :: path={Path}");
        }
    }
}
