// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 原生交互层：全局热键、单实例唤起、剪贴板、崩溃提示、窗口状态记忆。
// 这些是 Chrome --app 给不了的原生能力，也是「不套壳」的落点。

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace WorkbenchShell;

internal static class NativeMethods
{
    // ---- Win32 常量 ----
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID = 0xBEEF;
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, IntPtr msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    // 说明（2026-11-01）：原来这里声明了 user32.dll!SetClipboardText —— 这个导出函数不存在，
    // 每次「复制图片地址 / 复制本地路径 / 复制链接」都会抛 EntryPointNotFoundException，
    // 表现就是「右键点了复制，剪贴板里什么都没有」。剪贴板文本走 WinForms Clipboard.SetText
    // （它内部自己 OpenClipboard + GlobalAlloc + SetClipboardData），见 CopyToClipboard。

    internal static IntPtr HWND_BROADCAST => new(0xffff);

    /// <summary>
    /// 「再次启动时唤起已有窗口」的窗口消息 ID。
    /// 必须是同一个常量：广播方和接收方都要用它，ID 由系统按名称分配。
    /// </summary>
    private static readonly IntPtr ShowRequestMessage = RegisterWindowMessage("WorkbenchShell.ShowRequest");

    internal static int ShowRequestMessageId => ShowRequestMessage.ToInt32();

    /// <summary>广播「再次启动时唤起已有窗口」的消息。</summary>
    internal static void BroadcastShowRequest()
    {
        PostMessage(HWND_BROADCAST, ShowRequestMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>注册全局热键：Ctrl+Alt+W 唤起工作台。</summary>
    internal static void AttachHotkeys(IntPtr handle)
    {
        RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.W);
    }

    internal static void DetachHotkeys(IntPtr handle)
    {
        UnregisterHotKey(handle, HOTKEY_ID);
    }

    internal static void ShowError(string caption, string detail)
    {
        MessageBox(IntPtr.Zero, detail, caption, MB_OK | MB_ICONERROR);
    }

    /// <summary>
    /// 把一段文本放进系统剪贴板。
    ///
    /// 用 WinForms 的 Clipboard.SetText：它走的是 OpenClipboard → EmptyClipboard →
    /// GlobalAlloc/GlobalLock → SetClipboardData(CF_UNICODETEXT) → CloseClipboard，
    /// 并且自己带 STA 检查。剪贴板偶尔会被别的程序占着，重试几次。
    /// </summary>
    internal static bool CopyToClipboard(string text)
    {
        if (text is null) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[clipboard] 第 {attempt + 1} 次写入失败：{ex.Message}");
                Thread.Sleep(40);
            }
        }
        return false;
    }
}

/// <summary>
/// 窗口位置与大小记忆。
///
/// 路径放在 exe 同级（AppPaths），不放 %APPDATA%：
/// 实测新编译的 WorkbenchShell.exe 往 %APPDATA% 写文件会抛
/// UnauthorizedAccessException（同一路径 PowerShell / Python 写得通，
/// 说明拦截是按「哪个 exe 在写」来的，不是目录 ACL）。
/// 写不进去的后果是「恢复时永远读一份旧值」——
/// 旧值里存着屏外坐标，窗口每次启动都跑到屏幕外面去。
/// </summary>
internal sealed class WindowStateStore
{
    internal sealed class State
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    private readonly string _path = Path.Combine(AppPaths.BaseDir, "window-state.json");

    /// <summary>旧路径（%APPDATA%），仅用于一次性迁移。</summary>
    private static readonly string LegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkbenchShell", "window-state.json");

    /// <summary>最近一次成功读到（或写入）的状态，供「窗口跑到屏外时重建位置」用。</summary>
    internal State? LastState { get; private set; }

    public void Save(Form form)
    {
        try
        {
            var rect = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            var state = new State
            {
                X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height,
                Maximized = form.WindowState == FormWindowState.Maximized,
            };

            // ★ 存之前先剔掉「Windows 给最小化窗口塞的伪坐标」(-32000,-32000)，
            //   以及 199x34 这种伪尺寸。否则这一轮存进去，下一轮启动就会照着屏外坐标摆。
            if (rect.X <= -30000 || rect.Y <= -30000 || rect.Width < 400 || rect.Height < 300)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state));
            LastState = state;
        }
        catch
        {
            // 记忆失败不影响使用，静默忽略。
        }
    }

    public void Restore(Form form)
    {
        try
        {
            var raw = ReadState();
            if (raw is null) return;
            var state = JsonSerializer.Deserialize<State>(raw);
            if (state is null || state.Width < 400 || state.Height < 300) return;

            LastState = state;

            // ★ 宽高也要钳制。原实现只钳 X/Y，忘了尺寸：
            //   屏幕逻辑高 1152，而记忆里存着 Height=1388，
            //   窗口底部就有 236px（物理 ~295px）落在屏幕外 —— 看起来就是「窗口跑到屏幕外面」。
            var screen = Screen.FromRectangle(new Rectangle(state.X, state.Y, state.Width, state.Height));
            var area = screen.WorkingArea;

            var width = Math.Min(state.Width, Math.Max(480, area.Width));
            var height = Math.Min(state.Height, Math.Max(360, area.Height));

            var x = Math.Clamp(state.X, area.Left, Math.Max(area.Left, area.Right - width));
            var y = Math.Clamp(state.Y, area.Top, Math.Max(area.Top, area.Bottom - height));

            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(x, y, width, height);
            if (state.Maximized) form.WindowState = FormWindowState.Maximized;
        }
        catch
        {
            // 读失败用默认窗口。
        }
    }

    /// <summary>读取状态文件，必要时从旧路径迁移。</summary>
    private string? ReadState()
    {
        try
        {
            if (File.Exists(_path)) return File.ReadAllText(_path);

            // 旧版存在 %APPDATA%：迁过来一次，避免丢掉用户摆好的窗口位置。
            if (File.Exists(LegacyPath))
            {
                var legacy = File.ReadAllText(LegacyPath);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path, legacy);
                }
                catch { /* 迁移失败无所谓，继续用旧值 */ }
                return legacy;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
