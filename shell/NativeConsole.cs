// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 控制台挂接：WinExe 默认没有控制台，自检模式需要把输出显示到命令行窗口。

using System.Runtime.InteropServices;

namespace WorkbenchShell;

internal static class NativeConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    /// <summary>挂接父进程控制台；失败则新开一个。返回是否拿到了可用控制台。</summary>
    internal static bool Attach()
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS)) return true;
            return AllocConsole();
        }
        catch
        {
            return false;
        }
    }
}
