// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 界面设置：一套可持久化、可即时生效的外观与行为参数。
//
// 为什么把这些散落在 Theme 与各控件里的常量收进来：
//   字号、侧边栏宽度、行距、每组的条数这类东西，用户会想按自己的屏幕和眼睛调；
//   写死在代码里就只能改一次代码重启一次。收进 UiSettings 后，
//   设置窗口改完点保存，Theme 与控件下一次读属性就是新值。
//
// 落盘位置跟日志、WebView2 数据一样，放 exe 同级（见 AppPaths 的说明）：
//   往 %LOCALAPPDATA% 写会按「哪个 exe 在写」被拦。

namespace WorkbenchShell;

using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class UiSettings
{
    // ── 外观 ──────────────────────────────────────────────────────
    public string ThemeName { get; set; } = "樱粉 · 明亮";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>字号倍率：0.85 小 / 1.0 标准 / 1.15 大 / 1.3 更大。</summary>
    public float FontScale { get; set; } = 1.0f;

    /// <summary>行距倍率：0.92 紧凑 / 1.0 标准 / 1.08 宽松。</summary>
    public float DensityScale { get; set; } = 1.0f;

    // ── 侧边栏 ────────────────────────────────────────────────────
    public int SideBarWidth { get; set; } = 264;

    /// <summary>待审项目先铺几条，其余折在「展开其余 N 个」里。</summary>
    public int ReviewPreviewLimit { get; set; } = 6;

    /// <summary>最近活跃组显示几条（0 = 不显示这一组）。</summary>
    public int RecentLimit { get; set; } = 6;

    /// <summary>侧边栏一行最多显示几个全角字，超过截断并留悬停提示。</summary>
    public int MaxTitleChars { get; set; } = 10;

    /// <summary>是否在条目左侧画状态点。</summary>
    public bool ShowStatusDot { get; set; } = true;

    // ── 标签页 ────────────────────────────────────────────────────
    public int TabStripHeight { get; set; } = 34;
    public int TabMaxWidth { get; set; } = 232;

    // ── 启动 ──────────────────────────────────────────────────────
    public bool StartWithWindows { get; set; }
    public bool StartInTray { get; set; }

    // ── 单例与持久化 ──────────────────────────────────────────────

    [JsonIgnore]
    public static UiSettings Current { get; private set; } = new();

    /// <summary>设置变化时触发（控件据此重建字体、重新布局）。</summary>
    public static event EventHandler? Changed;

    internal static string FilePath => Path.Combine(AppPaths.BaseDir, "settings.json");

    public UiSettings Clone() => (UiSettings)MemberwiseClone();

    private static bool _loaded;

    /// <summary>首次创建窗口时读一次磁盘（自检、二级窗口都走这里）。</summary>
    internal static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) Current = loaded;
            }
        }
        catch { /* 配置坏了就按默认值走，不要让启动失败 */ }

        Clamp();
        ApplyTheme();
    }

    public static void Apply(UiSettings next)
    {
        Current = next ?? new UiSettings();
        Clamp();
        Current.Save();
        ApplyTheme();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Reset() => Apply(new UiSettings());

    private static void ApplyTheme()
    {
        var theme = Theme.Find(Current.ThemeName);
        if (theme is not null) Theme.Apply(theme);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BaseDir);
            // 用户可能正开着设置窗口改一半进程就退了，写临时文件再替换，
            // 避免留下半截 JSON（下次启动读不出来会悄悄退回默认值）。
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
        catch { /* 写不进去不影响本次使用 */ }
    }

    private static void Clamp()
    {
        var s = Current;
        s.FontScale = Math.Clamp(s.FontScale, 0.8f, 1.4f);
        s.DensityScale = Math.Clamp(s.DensityScale, 0.85f, 1.2f);
        s.SideBarWidth = Math.Clamp(s.SideBarWidth, 200, 440);
        s.ReviewPreviewLimit = Math.Clamp(s.ReviewPreviewLimit, 3, 40);
        s.RecentLimit = Math.Clamp(s.RecentLimit, 0, 12);
        s.MaxTitleChars = Math.Clamp(s.MaxTitleChars, 6, 30);
        s.TabStripHeight = Math.Clamp(s.TabStripHeight, 26, 48);
        s.TabMaxWidth = Math.Clamp(s.TabMaxWidth, 140, 380);
        if (string.IsNullOrWhiteSpace(s.FontFamily)) s.FontFamily = "Microsoft YaHei UI";
    }

    // ── 开机自启 ──────────────────────────────────────────────────

    private static string StartupShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "宿舍机工作台.lnk");

    /// <summary>按当前设置写入/删除开机自启快捷方式。返回是否成功。</summary>
    internal static bool ApplyStartup(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(StartupShortcut);
            link.TargetPath = exe;
            link.Arguments = "--tray";
            link.WorkingDirectory = AppPaths.BaseDir;
            link.Description = "宿舍机工作台 · 开机静默进托盘";
            link.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool StartupEnabled() => File.Exists(StartupShortcut);
}
