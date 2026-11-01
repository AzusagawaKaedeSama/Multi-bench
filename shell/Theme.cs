// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 主题体系：一套配色描述 + 一个全局门面。
//
// 为什么拆成两层：
//   ThemeSet   —— 一套主题的完整描述（颜色 + 名称），不可变；
//   Theme      —— 全局门面，暴露 Current 与一组静态属性。
//   标签栏、编辑组、分割条这些控件全都只认 Theme.Xxx，
//   换主题时不需要动它们一行代码 —— 换掉 Current 再发一个 Changed 事件即可。
//
// 主题清单（4 套）：
//   樱粉 · 明亮  与审阅台页面同源，默认主题，页面与壳浑然一体
//   石墨 · 暗色  深炭底 + 单色强调，终端/工程气质
//   靛蓝 · 商务  蓝灰底 + 中性蓝强调，最接近传统商务软件
//   雾松 · 柔和  低饱和青绿，克制安静

namespace WorkbenchShell;

/// <summary>一套主题的完整描述。</summary>
internal sealed class ThemeSet
{
    public string Name { get; init; } = "";
    public string Note { get; init; } = "";

    // 层次：背景 → 面板 → 次级面 → 分隔线
    public Color Bg { get; init; }
    public Color Surface { get; init; }
    public Color Soft { get; init; }
    public Color Line { get; init; }

    // 文字
    public Color Ink { get; init; }
    public Color Muted { get; init; }

    // 强调
    public Color Accent { get; init; }
    public Color AccentSoft { get; init; }
    public Color Hover { get; init; }
    public Color Selected { get; init; }

    // 标签栏三态
    public Color TabIdle { get; init; }
    public Color TabHover { get; init; }
    public Color TabActive { get; init; }

    // 状态
    public Color Success { get; init; }
    public Color Warning { get; init; }

    /// <summary>是否深色（影响阴影、滚动条、菜单栏的渲染策略）。</summary>
    public bool IsDark { get; init; }

    /// <summary>卡片阴影色。</summary>
    public Color Shadow => IsDark
        ? Color.FromArgb(90, 0, 0, 0)
        : Color.FromArgb(14, 30, 24, 40);

    /// <summary>滚动条轨道。</summary>
    public Color Track => IsDark
        ? Color.FromArgb(28, 255, 255, 255)
        : Color.FromArgb(20, 30, 24, 40);
}

/// <summary>全局主题门面。控件只认这里的属性，切换主题不需要改控件代码。</summary>
internal static class Theme
{
    private static readonly List<ThemeSet> _all = new()
    {
        new ThemeSet
        {
            Name = "樱粉 · 明亮",
            Note = "与审阅台页面同源",
            Bg = ColorTranslator.FromHtml("#f7f5f7"),
            Surface = ColorTranslator.FromHtml("#ffffff"),
            Soft = ColorTranslator.FromHtml("#faf8fb"),
            Line = ColorTranslator.FromHtml("#eae4ed"),
            Ink = ColorTranslator.FromHtml("#302936"),
            Muted = ColorTranslator.FromHtml("#82768a"),
            Accent = ColorTranslator.FromHtml("#d85580"),
            AccentSoft = ColorTranslator.FromHtml("#fae9ef"),
            Hover = ColorTranslator.FromHtml("#f3eff5"),
            Selected = ColorTranslator.FromHtml("#fae9ef"),
            TabIdle = ColorTranslator.FromHtml("#f2eef4"),
            TabHover = ColorTranslator.FromHtml("#ece6ef"),
            TabActive = ColorTranslator.FromHtml("#ffffff"),
            Success = ColorTranslator.FromHtml("#248767"),
            Warning = ColorTranslator.FromHtml("#af7d27"),
        },
        new ThemeSet
        {
            Name = "石墨 · 暗色",
            Note = "深炭底，工程气质",
            Bg = ColorTranslator.FromHtml("#14161a"),
            Surface = ColorTranslator.FromHtml("#1b1e24"),
            Soft = ColorTranslator.FromHtml("#22262d"),
            Line = ColorTranslator.FromHtml("#2c313a"),
            Ink = ColorTranslator.FromHtml("#e6e8ec"),
            Muted = ColorTranslator.FromHtml("#8d94a1"),
            Accent = ColorTranslator.FromHtml("#6ea8fe"),
            AccentSoft = ColorTranslator.FromHtml("#1d2a42"),
            Hover = ColorTranslator.FromHtml("#232830"),
            Selected = ColorTranslator.FromHtml("#1d2a42"),
            TabIdle = ColorTranslator.FromHtml("#1b1e24"),
            TabHover = ColorTranslator.FromHtml("#232830"),
            TabActive = ColorTranslator.FromHtml("#282d36"),
            Success = ColorTranslator.FromHtml("#4ec9a6"),
            Warning = ColorTranslator.FromHtml("#e0af68"),
            IsDark = true,
        },
        new ThemeSet
        {
            Name = "靛蓝 · 商务",
            Note = "蓝灰底，中性商务",
            Bg = ColorTranslator.FromHtml("#f3f5f8"),
            Surface = ColorTranslator.FromHtml("#ffffff"),
            Soft = ColorTranslator.FromHtml("#f8fafc"),
            Line = ColorTranslator.FromHtml("#e2e7ef"),
            Ink = ColorTranslator.FromHtml("#1b2532"),
            Muted = ColorTranslator.FromHtml("#6b7787"),
            Accent = ColorTranslator.FromHtml("#2f6feb"),
            AccentSoft = ColorTranslator.FromHtml("#e8f0fe"),
            Hover = ColorTranslator.FromHtml("#eef2f7"),
            Selected = ColorTranslator.FromHtml("#e8f0fe"),
            TabIdle = ColorTranslator.FromHtml("#f3f5f8"),
            TabHover = ColorTranslator.FromHtml("#e8edf4"),
            TabActive = ColorTranslator.FromHtml("#ffffff"),
            Success = ColorTranslator.FromHtml("#1a7f5a"),
            Warning = ColorTranslator.FromHtml("#9a6b12"),
        },
        new ThemeSet
        {
            Name = "雾松 · 柔和",
            Note = "低饱和青绿，克制",
            Bg = ColorTranslator.FromHtml("#f4f7f6"),
            Surface = ColorTranslator.FromHtml("#ffffff"),
            Soft = ColorTranslator.FromHtml("#f7faf9"),
            Line = ColorTranslator.FromHtml("#e2eae7"),
            Ink = ColorTranslator.FromHtml("#1e2a28"),
            Muted = ColorTranslator.FromHtml("#6e7c78"),
            Accent = ColorTranslator.FromHtml("#2f8f7d"),
            AccentSoft = ColorTranslator.FromHtml("#e0f2ed"),
            Hover = ColorTranslator.FromHtml("#edf4f2"),
            Selected = ColorTranslator.FromHtml("#e0f2ed"),
            TabIdle = ColorTranslator.FromHtml("#eef3f2"),
            TabHover = ColorTranslator.FromHtml("#e4ece9"),
            TabActive = ColorTranslator.FromHtml("#ffffff"),
            Success = ColorTranslator.FromHtml("#2f8f7d"),
            Warning = ColorTranslator.FromHtml("#8a6a24"),
        },
    };

    private static ThemeSet _current = _all[0];

    /// <summary>当前主题。</summary>
    public static ThemeSet Current => _current;

    /// <summary>全部主题。</summary>
    public static IReadOnlyList<ThemeSet> All => _all;

    /// <summary>主题切换时触发（控件据此重绘）。</summary>
    public static event EventHandler? Changed;

    /// <summary>切换主题并广播。</summary>
    public static void Apply(ThemeSet theme)
    {
        if (theme is null || ReferenceEquals(theme, _current)) return;
        _current = theme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>按名称找主题。</summary>
    public static ThemeSet? Find(string name) =>
        _all.FirstOrDefault(t => t.Name == name);

    // ── 门面属性：现有控件全部走这里，换主题零改动 ──────────────

    public static Color Bg => Current.Bg;
    public static Color Surface => Current.Surface;
    public static Color Soft => Current.Soft;
    public static Color Line => Current.Line;
    public static Color Ink => Current.Ink;
    public static Color Muted => Current.Muted;
    public static Color Pink => Current.Accent;          // 旧名保留，粉色强调位
    public static Color Accent => Current.Accent;
    public static Color AccentSoft => Current.AccentSoft;
    public static Color PinkSoft => Current.AccentSoft;   // 旧名保留
    public static Color Hover => Current.Hover;
    public static Color Selected => Current.Selected;
    public static Color TabIdle => Current.TabIdle;
    public static Color TabHover => Current.TabHover;
    public static Color TabActive => Current.TabActive;
    public static Color RailBg => Current.Soft;
    public static Color RailActive => Current.Accent;
    public static Color Green => Current.Success;
    public static Color Amber => Current.Warning;
    public static Color Shadow => Current.Shadow;
    public static bool IsDark => Current.IsDark;

    // ── 度量 ─────────────────────────────────────────────────────

    /// <summary>把基准像素按用户的行距倍率缩放。</summary>
    private static int Scale(int value) =>
        (int)Math.Round(value * UiSettings.Current.DensityScale);

    public static int TabStripHeight => UiSettings.Current.TabStripHeight;
    public const int TabMinWidth = 108;
    public static int TabMaxWidth => UiSettings.Current.TabMaxWidth;
    public const int TabPaddingLeft = 12;
    public const int TabPaddingRight = 8;
    public const int TabIconSize = 14;
    public const int TabCloseSize = 14;
    public const int RailWidth = 48;
    public static int SideBarWidth => UiSettings.Current.SideBarWidth;
    public const int SplitterWidth = 4;

    // 侧边栏自绘版式度量
    public const int SideBarHeaderHeight = 54;
    public const int SideBarSearchHeight = 34;

    /// <summary>
    /// 导航区高度：分区标签(20) + 6 项 × 34 + 底部留白(12) = 236。
    /// 壳接管工作台主导航后，这一块固定不滚动。
    /// </summary>
    public const int SideBarNavHeight = 236;

    /// <summary>搜索框顶部 Y（在头部标题之下）。</summary>
    public const int SideBarSearchTop = 36;

    /// <summary>搜索框高度。</summary>
    public const int SideBarSearchBoxHeight = 30;

    // 2026-11-01 可读性：行高跟着正文一起放大（正文 12.5px，行高 28px 约 2.24 倍）。
    // 组标题 32px，与行高拉开 4px，让「分组头 / 条目」两层在大字号下仍然分得开。
    public static int SideBarGroupHeaderHeight => Scale(32);
    public static int SideBarRowHeight => Scale(28);
    public const int SideBarGroupGap = 8;
    public const int SideBarPadX = 12;
    public const int SideBarRadius = 12;
    public const int SideBarRowRadius = 8;
    public const int SideBarFooterHeight = 26;

    /// <summary>页面与原生壳共用的字体栈。</summary>
    public static readonly string FontFamily =
        "'Segoe UI Variable','Segoe UI','Microsoft YaHei UI','Microsoft YaHei',sans-serif";

    /// <summary>按当前设置的字体族与字号倍率生成字体。</summary>
    internal static Font MakeFont(float size, FontStyle style) =>
        new(UiSettings.Current.FontFamily,
            size * UiSettings.Current.FontScale,
            style,
            GraphicsUnit.Point);

    public static Font TabFont => MakeFont(8.75f, FontStyle.Regular);
    public static Font TabFontActive => MakeFont(8.75f, FontStyle.Bold);
    public static Font RailFont => MakeFont(7.5f, FontStyle.Regular);
    public static Font TreeFont => MakeFont(9f, FontStyle.Regular);

    /// <summary>状态图标颜色：按审阅状态区分。</summary>
    public static Color StatusColor(string? reviewStatus) => reviewStatus switch
    {
        "archived" => Muted,
        "completed" => Green,
        _ => Amber,
    };
}
