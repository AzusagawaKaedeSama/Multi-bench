// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 自绘侧边栏：审阅项目树。
//
// 为什么不用 TreeView：
//   WinForms 的 TreeView 是系统控件，样式锁死 —— 画不出圆角卡片、
//   画不出胶囊徽标、没有展开动画、滚动条是八十年代的样子。
//   要做「现代 + 圆角 + 微动效」，只能自己画。
//
// 布局（宽 264）：
//   ┌────────────────────────────────┐
//   │ 审阅项目                 137 项 ↻│  头部
//   │ ( ⌕ 搜索项目… )                 │  搜索框（圆角，自绘文本 + 光标）
//   │ ┌────────────────────────────┐ │
//   │ │ ▾ 待审项目              75  │ │  分组卡片（圆角 12 + 阴影）
//   │ │   ● 某个审阅项目             │ │  条目行（悬停圆角、状态点）
//   │ └────────────────────────────┘ │
//   │ ● 已连接本机 · 15:15            │  底部状态
//   └────────────────────────────────┘
//
// 动效：分组展开/收起做高度插值，悬停做颜色淡入，都由一个 60fps 定时器驱动。

namespace WorkbenchShell;

using System.Drawing.Drawing2D;

/// <summary>侧边栏里的一个项目条目。</summary>
internal sealed class SideBarEntry
{
    public string Label = "";
    public string Url = "";
    public string Status = "";
    public string Description = "";
    /// <summary>未被截断的完整名称（只在 Label 被截断时用来出悬停提示）。</summary>
    public string FullLabel = "";
    /// <summary>行右侧的次要信息（更新时间 / 类型），没有就不画。</summary>
    public string Meta = "";
}

/// <summary>
/// 一个分组卡片的数据。分组是「最近活跃 / 待审 / 绘画 / 视频 / 素材与参考 / 研究与记录」
/// 这一类并列单位，不是按项目内部结构分层 —— 侧边栏只做入口，不做目录树。
/// </summary>
internal sealed class SideBarGroupSpec
{
    public string Key = "";
    public string Label = "";
    public List<SideBarEntry> Items = new();

    /// <summary>置顶分组：标题左缘画一条强调色竖条，跟下面的分类分开。</summary>
    public bool Pinned;

    /// <summary>第一次出现时是否展开（归档这类默认收起）。</summary>
    public bool DefaultExpanded = true;

    /// <summary>
    /// 预览条数：只先列出前 N 条，其余折叠成一张「展开其余 N 个」。
    /// 0 表示不截断（最近活跃本来就是短列表，用不上）。
    /// </summary>
    public int PreviewLimit;
}

/// <summary>自绘的审阅项目树。</summary>
internal sealed class SideBarPanel : Control
{
    // ── 模型 ─────────────────────────────────────────────────────

    private sealed class Group
    {
        public string Key = "";
        public string Label = "";
        public bool Expanded = true;
        public bool Pinned;
        public double Anim = 1.0;          // 0..1 折叠进度
        public List<SideBarEntry> Items = new();

        /// <summary>预览条数（0 = 全部列出）。</summary>
        public int PreviewLimit;

        /// <summary>用户点过「展开其余」，本条分组显示全部条目。</summary>
        public bool ShowAll;

        /// <summary>「展开其余」的行数插值进度（0 = 只有预览条数，1 = 全部）。</summary>
        public double Reveal;

        /// <summary>当前应该画多少条（不含折叠动画的缩放）。</summary>
        public int ShownCount
        {
            get
            {
                if (PreviewLimit <= 0 || Items.Count <= PreviewLimit) return Items.Count;
                if (ShowAll && Reveal >= 0.999) return Items.Count;
                var extra = Items.Count - PreviewLimit;
                return PreviewLimit + (int)Math.Round(extra * Reveal);
            }
        }

        /// <summary>
        /// 是否画「展开其余 / 收起」那一行。
        ///
        /// 展开之后这一行不消失，改成「收起，只看最近 N 个」——
        /// 否则点开一次就再也回不去预览态，只能靠折叠整组绕过。
        /// </summary>
        public bool NeedsRevealRow => PreviewLimit > 0 && Items.Count > PreviewLimit;
    }

    private readonly List<Group> _groups = new();
    private int _totalCount;

    // 命中区域（控件坐标）
    private readonly List<(Rectangle Rect, Group? Group, SideBarEntry? Entry)> _hits = new();

    /// <summary>「展开其余 N 个」那一行的命中区（控件坐标）。</summary>
    private readonly List<(Rectangle Rect, Group Group)> _revealHits = new();

    // 导航项命中区（固定区，不参与滚动）
    private readonly List<(Rectangle Rect, string Key)> _navHits = new();

    // 当前悬停的导航项
    private string? _hoverNav;

    // 搜索
    private string _search = "";
    private bool _searchFocused;
    private int _caret;
    private int _caretOffset;
    private bool _caretOn = true;

    // 悬停
    private Group? _hoverGroup;
    private Group? _hoverReveal;
    private SideBarEntry? _hoverEntry;
    private SideBarEntry? _tipEntry;

    /// <summary>行名被截断时，悬停显示完整名称。</summary>
    private readonly ToolTip _tip = new()
    {
        InitialDelay = 420,
        ReshowDelay = 120,
        AutoPopDelay = 12000,
        ShowAlways = false,
    };
    private bool _hoverRefresh;
    private double _hoverFade;
    private double _refreshFade;

    /// <summary>正在刷新清单：图标转圈、底部状态显示「刷新中…」。</summary>
    private bool _refreshing;

    /// <summary>图标旋转角（度）。由动画定时器推进，刷新结束就不再重绘。</summary>
    private double _spin;

    /// <summary>打开/关闭刷新态。</summary>
    public bool IsRefreshing => _refreshing;
    public void SetRefreshing(bool on)
    {
        if (_refreshing == on) return;
        _refreshing = on;
        if (on) { _spin = 0; _dirty = true; _anim.Start(); }
        Invalidate();
    }

    // 滚动
    private int _scrollY;
    private int _contentHeight;

    // 动画定时器
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    private bool _dirty;

    /// <summary>
    /// 光标闪烁定时器。
    ///
    /// ★ 光标不能挂在 60fps 的动画定时器上：那样每 16ms 翻转一次，
    ///   一秒闪三十多次，看着就是「光标在疯狂闪烁」，而且整块侧边栏每帧重绘。
    ///   单独用 530ms（系统默认节奏），并且只在搜索框真的聚焦时跑。
    /// </summary>
    private readonly System.Windows.Forms.Timer _caretBlink = new() { Interval = 530 };

    // 底部状态
    private string _statusText = "";
    private bool _connected;

    // 字体：设置里能改字体族与字号，所以不能是 static readonly ——
    // 每次设置变化都要重建一份，绘制时读当前字段。
    // 2026-11-01 可读性：原来正文 8.6pt ≈ 11.5px，中文小字在宿舍机 125% 缩放下笔画发糊。
    // 正文与组标题提到 9.4pt ≈ 12.5px，标题 11pt，徽标与次要信息 8pt（≈10.7px）。
    // 用户仍可在设置 → 外观里用字号倍率整体放大。
    private Font _fontTitle = Theme.MakeFont(11f, FontStyle.Bold);
    private Font _fontGroup = Theme.MakeFont(9.4f, FontStyle.Bold);
    private Font _fontRow = Theme.MakeFont(9.4f, FontStyle.Regular);
    private Font _fontBadge = Theme.MakeFont(8f, FontStyle.Bold);
    private Font _fontFoot = Theme.MakeFont(8f, FontStyle.Regular);

    /// <summary>设置变化后重建字体。</summary>
    private void RebuildFonts()
    {
        _fontTitle = Theme.MakeFont(11f, FontStyle.Bold);
        _fontGroup = Theme.MakeFont(9.4f, FontStyle.Bold);
        _fontRow = Theme.MakeFont(9.4f, FontStyle.Regular);
        _fontBadge = Theme.MakeFont(8f, FontStyle.Bold);
        _fontFoot = Theme.MakeFont(8f, FontStyle.Regular);
    }

    private static readonly StringFormat CenterFmt = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

    private static readonly StringFormat LeftFmt = new()
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    /// <summary>右对齐、单行、超长省略（行尾的更新时间用）。</summary>
    private static readonly StringFormat RightFmt = new()
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    // ── 事件 ─────────────────────────────────────────────────────

    /// <summary>请求打开一个审阅页（开成原生标签页）。</summary>
    public event EventHandler<(string Title, string Url)>? TabRequested;

    /// <summary>请求刷新清单。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>请求切换工作台页面（壳接管导航后，页面自带的 .rail 已隐藏）。</summary>
    public event EventHandler<string>? NavRequested;

    /// <summary>当前选中的导航项（对应页面的 hash 路由 key）。</summary>
    private string _activeNav = "creative";

    /// <summary>设置当前导航项。</summary>
    public void SetActiveNav(string key)
    {
        if (_activeNav == key) return;
        _activeNav = key;
        _dirty = true;
        _anim.Start();
        Invalidate();
    }

    // ── 构造 ─────────────────────────────────────────────────────

    public SideBarPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        Font = Theme.TreeFont;
        TabStop = true;

        _anim.Tick += (_, _) =>
        {
            if (!_dirty) { _anim.Stop(); return; }
            StepAnimations();
            Invalidate();
        };

        _caretBlink.Tick += (_, _) =>
        {
            if (!_searchFocused) { _caretBlink.Stop(); return; }
            _caretOn = !_caretOn;
            Invalidate();
        };

        // 设置里改了字号 / 字体族：重建字体并重绘整栏。
        UiSettings.Changed += (_, _) =>
        {
            RebuildFonts();
            Invalidate();
        };
    }

    // ── 对外接口 ─────────────────────────────────────────────────

    /// <summary>写入新的分组数据。</summary>
    public void SetGroups(IEnumerable<SideBarGroupSpec> groups, int total)
    {
        // 保留已展开状态（刷新后不打断用户）：「分组是否展开」和「是否点过展开其余」都要记住，
        // 否则每 15 秒的清单刷新会把用户刚展开的长列表又收回去。
        var expanded = _groups.ToDictionary(g => g.Key, g => g.Expanded);
        var revealed = _groups.ToDictionary(g => g.Key, g => g.ShowAll);

        _groups.Clear();
        foreach (var spec in groups)
        {
            var isExpanded = expanded.TryGetValue(spec.Key, out var e) ? e : spec.DefaultExpanded;
            var isShown = revealed.TryGetValue(spec.Key, out var s) && s;
            _groups.Add(new Group
            {
                Key = spec.Key,
                Label = spec.Label,
                Items = spec.Items,
                Pinned = spec.Pinned,
                Expanded = isExpanded,
                Anim = isExpanded ? 1.0 : 0.0,
                PreviewLimit = spec.PreviewLimit,
                ShowAll = isShown,
                Reveal = isShown ? 1.0 : 0.0,
            });
        }
        _totalCount = total;

        // 新出现的分组默认展开且直接就位（不播动画），避免首屏一堆行往下跳
        _dirty = true;
        _anim.Start();
        Invalidate();
    }

    /// <summary>设置底部状态。</summary>
    public void SetStatus(string text, bool connected)
    {
        _statusText = text;
        _connected = connected;
        Invalidate();
    }

    /// <summary>项目总数（自检用）。</summary>
    public int ProjectCount => _totalCount;

    /// <summary>当前滚动位置（自检用：验证「刷新不重置滚动」）。</summary>
    public int ScrollOffset => _scrollY;

    /// <summary>设置滚动位置（自检用：模拟用户滚到中间再刷新）。</summary>
    public void ScrollTo(int y)
    {
        // 内容高度只在 DrawList 里算出来。刚换过数据、还没绘制过时它是 0，
        // 直接夹紧会把滚动位置压成 0 —— 先同步画一帧拿到真实高度。
        if (_contentHeight <= 0) Refresh();
        var maxScroll = Math.Max(0, _contentHeight - ListArea.Height);
        _scrollY = Math.Clamp(y, 0, maxScroll);
        Invalidate();
    }

    /// <summary>各分组当前的展开状态（自检用）。</summary>
    public IReadOnlyDictionary<string, bool> GroupState =>
        _groups.ToDictionary(g => g.Key, g => g.Expanded);

    /// <summary>各分组「是否点过展开其余」的状态（自检用：刷新不能把它收回去）。</summary>
    public IReadOnlyDictionary<string, bool> GroupRevealState =>
        _groups.ToDictionary(g => g.Key, g => g.ShowAll);

    /// <summary>「展开其余」那一组先行展开（自检用，模拟用户点过）。</summary>
    public bool RevealGroup(string key)
    {
        var group = _groups.FirstOrDefault(g => g.Key == key);
        if (group is null) return false;
        group.ShowAll = true;
        group.Reveal = 1.0;
        _dirty = true;
        _anim.Start();
        Invalidate();
        return true;
    }

    /// <summary>可见节点数（自检用）：分组数 + 条目数。</summary>
    public int VisibleNodeCount => _groups.Count + _groups.Sum(g => g.Items.Count);

    /// <summary>设置搜索关键词。</summary>
    public void SetSearch(string keyword)
    {
        _search = keyword ?? "";
        _caret = _search.Length;
        _caretOffset = 0;
        Invalidate();
    }

    // ── 版式几何 ─────────────────────────────────────────────────

    private int HeaderHeight => Theme.SideBarHeaderHeight;
    private int FooterHeight => Theme.SideBarFooterHeight;

    /// <summary>
    /// 导航区高度：**由内容算出来**，不写死。
    ///
    /// 之前写死 236，导航项一改就露馅 —— 第 6 项「统一设置」被从中间切断，
    /// 看起来像「有半条记录显示不出来」。写死的常量与实际绘制脱钩，
    /// 这类截断迟早复发，所以改成跟着内容走。
    ///
    /// ★ 必须和 DrawNav 的步进严格一致：每项占 NavRowHeight + NavRowGap。
    ///   漏算间隔会让最后一项刚好卡在边界外被整项丢掉。
    /// </summary>
    private int NavHeight =>
        NavLabelHeight +
        ShellBridge.NavItems.Length * (NavRowHeight + NavRowGap) +
        NavBottomPad;

    /// <summary>
    /// 导航区实际占用高度：窗口很矮时不能让导航把列表整个挤没，
    /// 此时压缩导航区（靠下滚动可见更多列表内容）。
    /// </summary>
    private int NavHeightEffective
    {
        get
        {
            var full = NavHeight;
            var available = Height - NavTop - FooterHeight;
            // 至少给列表留 120px，否则项目清单完全没地方显示。
            var cap = Math.Max(0, available - 120);
            return Math.Min(full, cap);
        }
    }

    /// <summary>分区标签「工作台」占的高度。</summary>
    private const int NavLabelHeight = 24;

    /// <summary>单个导航项的行高（与 DrawNav 里的 rowH 保持一致）。</summary>
    private const int NavRowHeight = 32;

    /// <summary>导航项之间的间隔（与 DrawNav 的步进保持一致）。</summary>
    private const int NavRowGap = 2;

    /// <summary>导航区底部留白 + 与列表之间的呼吸。</summary>
    private const int NavBottomPad = 14;

    /// <summary>导航区顶部 Y：搜索框下方，留 12px 呼吸。</summary>
    private int NavTop => Theme.SideBarSearchTop + Theme.SideBarSearchBoxHeight + 12;

    private Rectangle ListArea => new(0, NavTop + NavHeightEffective, Width,
        Math.Max(0, Height - NavTop - NavHeightEffective - FooterHeight));

    /// <summary>
    /// 列表内容的顶部内边距。
    ///
    /// 之前第一张分组卡片紧贴列表区上沿，滚动到顶时它的圆角顶被裁掉半截，
    /// 看起来像「有条记录显示了一半而且没法再往上滑」——
    /// 实际上滚轮已经到顶了，只是内容本身没留呼吸位。
    /// </summary>
    private const int ListTopPad = 12;

    private int PadX => Theme.SideBarPadX;
    private int CardWidth => Math.Max(60, Width - PadX * 2);
    private int RowHeight => Theme.SideBarRowHeight;
    private int GroupHeaderHeight => Theme.SideBarGroupHeaderHeight;

    // ── 绘制 ─────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawHeader(g);
        DrawSearch(g);
        DrawNav(g);
        DrawList(g);
        DrawScrollBar(g);
        DrawFooter(g);
    }

    private void DrawHeader(Graphics g)
    {
        // 标题
        using (var brush = new SolidBrush(Theme.Ink))
            g.DrawString("审阅项目", _fontTitle, brush, new Rectangle(PadX, 10, 150, 28), LeftFmt);

        // 刷新按钮（圆形悬停背景 + 回转箭头）。刷新中整圈旋转，箭头同时变强调色 ——
        // 这是「点下去有反应」的直接证据，不用等清单回来。
        var btnSize = 28;
        var btn = new Rectangle(Width - PadX - btnSize, 11, btnSize, btnSize);
        var fade = _refreshFade;
        var halo = _refreshing ? 0.55 : 28.0 / 40.0 * fade;
        if (halo > 0.01)
        {
            using var brush = new SolidBrush(Color.FromArgb((int)(40 * halo), Theme.Accent));
            g.FillEllipse(brush, btn);
        }
        DrawRefreshGlyph(g, btn, Theme.Muted, fade);

        // 待审计数：做成胶囊徽标。以前是一行 7.4pt 的浅灰字，和背景几乎同色，
        // 等于没显示 —— 用户看到的「计数徽标」问题就在这里。
        var text = $"待审 {_totalCount}";
        var badgeW = Math.Max(46, (int)Math.Ceiling(MeasureText(g, text, _fontBadge)) + 16);
        var badge = new Rectangle(btn.Left - 8 - badgeW, btn.Top + (btn.Height - 19) / 2, badgeW, 19);
        DrawBadge(g, text, badge, Theme.Ink, Theme.Line);
    }

    private void DrawRefreshGlyph(Graphics g, Rectangle box, Color color, double fade)
    {
        var c = (_refreshing || fade > 0.5) ? Theme.Accent : color;
        using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var r = box.Width / 2f - 5f;
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;

        var state = g.Save();
        if (_refreshing)
        {
            // 绕自己的中心转：先平移到圆心，转，再平移回来。
            g.TranslateTransform(cx, cy);
            g.RotateTransform((float)_spin);
            g.TranslateTransform(-cx, -cy);
        }

        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 40, 260);
        // 箭头
        var ax = cx + (float)(r * Math.Cos(Math.PI / 4));
        var ay = cy - (float)(r * Math.Sin(Math.PI / 4));
        g.DrawLine(pen, ax, ay, ax, ay + 6.5f);
        g.DrawLine(pen, ax, ay, ax - 5.5f, ay);

        g.Restore(state);
    }

    private void DrawSearch(Graphics g)
    {
        var box = new Rectangle(PadX, Theme.SideBarSearchTop, CardWidth, Theme.SideBarSearchBoxHeight);
        using (var path = RoundedPath(box, 10))
        {
            using var brush = new SolidBrush(Theme.Surface);
            g.FillPath(brush, path);
            if (_searchFocused)
            {
                using var pen = new Pen(Theme.Accent, 1.4f);
                g.DrawPath(pen, path);
            }
            else
            {
                using var pen = new Pen(Theme.Line, 1f);
                g.DrawPath(pen, path);
            }
        }

        // 放大镜
        var iconBox = new Rectangle(box.X + 10, box.Y + 9, 12, 12);
        using (var pen = new Pen(_searchFocused ? Theme.Accent : Theme.Muted, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, iconBox);
            g.DrawLine(pen, iconBox.Right - 1, iconBox.Bottom - 1, iconBox.Right + 3, iconBox.Bottom + 3);
        }

        // 文本或占位符
        var textLeft = box.X + 30;
        var textRect = new Rectangle(textLeft, box.Y, box.Width - 40, box.Height);
        if (_search.Length == 0 && !_searchFocused)
        {
            using var brush = new SolidBrush(Theme.Muted);
            g.DrawString("搜索项目…", _fontRow, brush, textRect, LeftFmt);
        }
        else
        {
            using var brush = new SolidBrush(Theme.Ink);
            g.DrawString(_search, _fontRow, brush, textRect, LeftFmt);

            // 光标
            if (_searchFocused && _caretOn)
            {
                var w = MeasureText(g, _search, _fontRow);
                var caretX = textLeft + (float)w + 1;
                using var pen = new Pen(Theme.Accent, 1.6f);
                g.DrawLine(pen, caretX, box.Y + 8, caretX, box.Y + box.Height - 8);
            }
        }
    }

    /// <summary>
    /// 导航区：工作台主导航（总览 / 实时任务看板 / 创作与审阅 / 作品与资产 / Agent / 统一设置）。
    ///
    /// 为什么放在壳里而不是留给页面：
    ///   页面自带一套 .rail，壳再画一套就是两层重复 —— 点两处、认两套。
    ///   壳把导航接管过来、页面那层隐藏（ShellBridge.HideRailCss），
    ///   整个窗口只剩一根侧边栏。
    /// </summary>
    private void DrawNav(Graphics g)
    {
        _navHits.Clear();
        var top = NavTop;
        var items = ShellBridge.NavItems;

        // 分区标签
        using (var brush = new SolidBrush(Theme.Muted))
            g.DrawString("工作台", _fontBadge, brush,
                new Rectangle(PadX, top + 4, 120, 18), LeftFmt);

        var y = top + NavLabelHeight;
        var rowH = NavRowHeight;

        // 窗口太矮时，导航区被压缩 —— 只画放得下的项，
        // 宁可少显示一项，也不要把一项切成半截（那看着就像界面坏了）。
        var navBottom = top + NavHeightEffective;
        foreach (var (key, label, icon) in items)
        {
            if (y + rowH > navBottom) break;

            var row = new Rectangle(PadX, y, CardWidth, rowH);
            var isActive = _activeNav == key;
            var isHover = _hoverNav == key;

            // 选中态：浅色圆角底 + 左侧强调条
            if (isActive)
            {
                using (var path = RoundedPath(row, 9))
                using (var brush = new SolidBrush(Theme.Selected))
                    g.FillPath(brush, path);

                using var bar = new SolidBrush(Theme.Accent);
                using var barPath = RoundedPath(new Rectangle(row.X + 1, row.Y + 7, 3, rowH - 14), 1.5f);
                g.FillPath(bar, barPath);
            }
            else if (isHover)
            {
                using (var path = RoundedPath(row, 9))
                using (var brush = new SolidBrush(Theme.Hover))
                    g.FillPath(brush, path);
            }

            // 图标
            var iconBox = new Rectangle(row.X + 11, row.Y + (rowH - 16) / 2, 16, 16);
            DrawNavIcon(g, icon, iconBox, isActive ? Theme.Accent : Theme.Muted);

            // 文字
            using (var brush = new SolidBrush(isActive ? Theme.Accent : Theme.Ink))
                g.DrawString(label, _fontRow, brush,
                    new Rectangle(row.X + 34, row.Y, row.Width - 44, rowH), LeftFmt);

            _navHits.Add((row, key));
            y += rowH + NavRowGap;
        }

        // 导航区与项目列表之间的分隔线。
        // 没有它，两组内容会挤在一起，视觉上分不清哪个是主入口、哪个是清单。
        var sepY = top + NavHeightEffective - NavBottomPad / 2;
        using (var pen = new Pen(Theme.Line))
            g.DrawLine(pen, PadX, sepY, PadX + CardWidth, sepY);
    }

    /// <summary>导航项图标：全部走矢量路径，避免依赖图标字体。</summary>
    private static void DrawNavIcon(Graphics g, string icon, Rectangle box, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;

        switch (icon)
        {
            case "home":
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + 1.5f, cy + 1f),
                    new PointF(cx, box.Y + 1f),
                    new PointF(box.Right - 1.5f, cy + 1f),
                });
                g.DrawLine(pen, box.X + 3.5f, cy, box.X + 3.5f, box.Bottom - 1.5f);
                g.DrawLine(pen, box.Right - 3.5f, cy, box.Right - 3.5f, box.Bottom - 1.5f);
                g.DrawLine(pen, box.X + 3.5f, box.Bottom - 1.5f, box.Right - 3.5f, box.Bottom - 1.5f);
                break;

            case "clock":
                g.DrawEllipse(pen, box.X + 1.5f, box.Y + 1.5f, box.Width - 3f, box.Height - 3f);
                g.DrawLine(pen, cx, cy - 4.5f, cx, cy);
                g.DrawLine(pen, cx, cy, cx + 3.5f, cy + 2f);
                break;

            case "image":
                g.DrawRectangle(pen, box.X + 1.5f, box.Y + 2.5f, box.Width - 3f, box.Height - 5f);
                g.DrawEllipse(pen, box.X + 4.5f, box.Y + 5.5f, 2.5f, 2.5f);
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + 2.5f, box.Bottom - 3.5f),
                    new PointF(box.X + 6.5f, box.Bottom - 8f),
                    new PointF(box.X + 10f, box.Bottom - 4.5f),
                    new PointF(box.Right - 4.5f, box.Bottom - 2.5f),
                });
                break;

            case "folder":
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + 1.5f, box.Bottom - 3.5f),
                    new PointF(box.X + 1.5f, box.Y + 3.5f),
                    new PointF(box.X + 6f, box.Y + 3.5f),
                    new PointF(box.X + 8f, box.Y + 6f),
                    new PointF(box.Right - 1.5f, box.Y + 6f),
                    new PointF(box.Right - 1.5f, box.Bottom - 3.5f),
                });
                g.DrawLine(pen, box.X + 1.5f, box.Bottom - 3.5f, box.Right - 1.5f, box.Bottom - 3.5f);
                break;

            case "user":
                g.DrawEllipse(pen, cx - 3.2f, box.Y + 2f, 6.4f, 6.4f);
                g.DrawArc(pen, cx - 6f, box.Y + 9.5f, 12f, 9f, 180, 180);
                break;

            default: // sliders
                g.DrawLine(pen, box.X + 1.5f, box.Y + 4.5f, box.Right - 1.5f, box.Y + 4.5f);
                g.DrawLine(pen, box.X + 1.5f, box.Y + 11.5f, box.Right - 1.5f, box.Y + 11.5f);
                g.DrawEllipse(pen, cx - 3f, box.Y + 2f, 6f, 6f);
                g.DrawEllipse(pen, cx + 1.5f, box.Y + 9f, 6f, 6f);
                break;
        }
    }

    private void DrawList(Graphics g)
    {
        _hits.Clear();
        var area = ListArea;

        var state = g.Save();

        // ★★ 这里原本有两处坐标错位，叠加起来造成「顶部被切掉一截、滚轮也滑不上去」：
        //
        //   1) 裁剪区用的是 ListArea（设备坐标，Y=320），
        //      而内容是按「列表区内部坐标」从 y=0 开始画的（y=12 是第一张卡片顶）。
        //      两者不在同一个原点 → 内容整体画在裁剪区上方，只有中段露出来。
        //      修法：把内容平移到列表区原点，即 TranslateTransform(0, area.Top)。
        //
        //   2) 滚动必须和内容平移在同一个变换里，且裁剪区要固定在可视区（不跟着滚）。
        //      SetClip 会按调用时的变换把矩形映射到设备空间，
        //      所以这里传 (area.X, _scrollY, w, h)，映射后正好落在设备的 area 上。
        g.TranslateTransform(0, area.Top - _scrollY);
        g.SetClip(new Rectangle(area.X, _scrollY, area.Width, area.Height));

        _revealHits.Clear();
        var y = (float)ListTopPad;
        foreach (var group in _groups)
        {
            var shown = group.ShownCount;
            var visibleItems = (int)Math.Round(shown * group.Anim);
            // 「展开其余 N 个」那一行只在还有没显示的条目、且组是展开态时占位。
            var hasRevealRow = group.NeedsRevealRow && group.Anim > 0.35;
            var bodyH = visibleItems * RowHeight + (hasRevealRow ? RowHeight : 0);
            var cardH = GroupHeaderHeight + bodyH + 6;
            var cardRect = new Rectangle(PadX, (int)y, CardWidth, (int)cardH);

            DrawGroupCard(g, group, cardRect, bodyH);

            // 命中：组标题
            _hits.Add((new Rectangle(PadX, (int)y, CardWidth, GroupHeaderHeight), group, null));

            // 命中：条目
            for (int i = 0; i < visibleItems && i < group.Items.Count; i++)
            {
                var rowRect = new Rectangle(PadX + 8, (int)y + GroupHeaderHeight + i * RowHeight,
                                           CardWidth - 16, RowHeight);
                _hits.Add((rowRect, group, group.Items[i]));
            }

            // 命中：展开其余
            if (hasRevealRow)
            {
                var revealRect = new Rectangle(PadX + 8, (int)y + GroupHeaderHeight + visibleItems * RowHeight,
                                               CardWidth - 16, RowHeight);
                _revealHits.Add((revealRect, group));
            }

            y += cardH + Theme.SideBarGroupGap;
        }

        _contentHeight = (int)y + ListTopPad;

        g.Restore(state);

        // 命中区换算成控件坐标：
        // 内容画在 (area.Top - _scrollY) 这个原点上，
        // 而鼠标事件给的是控件坐标，所以要补上 area.Top 再减去滚动量。
        for (var i = 0; i < _hits.Count; i++)
        {
            var h = _hits[i];
            h.Rect.Offset(0, area.Top - _scrollY);
            _hits[i] = h;
        }

        for (var i = 0; i < _revealHits.Count; i++)
        {
            var h = _revealHits[i];
            h.Rect.Offset(0, area.Top - _scrollY);
            _revealHits[i] = h;
        }

        // 内容高度变了（刷新后清单条数变化）就把滚动位置夹回边界内。
        // 原来这里是无条件 Invalidate()：只要滚动过，侧边栏就每帧重绘整块。
        var maxScroll = Math.Max(0, _contentHeight - area.Height);
        if (_scrollY > maxScroll)
        {
            _scrollY = maxScroll;
            Invalidate();
        }
    }

    private void DrawGroupCard(Graphics g, Group group, Rectangle card, int bodyH)
    {
        // 卡片底 + 阴影。置顶分组（最近活跃）用浅强调底色与普通分类分开，
        // 不额外加标签文字 —— 位置和底色已经说明它是快捷区。
        using (var path = RoundedPath(card, Theme.SideBarRadius))
        {
            if (!Theme.IsDark)
            {
                using var shadow = new SolidBrush(Theme.Shadow);
                g.FillPath(shadow, OffsetPath(path, 0, 1));
            }
            using var brush = new SolidBrush(group.Pinned ? Theme.Selected : Theme.Surface);
            g.FillPath(brush, path);
        }

        // 置顶标记：标题左上角一条 3px 的强调竖条，圆角内缩 6px 画。
        if (group.Pinned)
        {
            using var barBrush = new SolidBrush(Theme.Accent);
            using var barPath = RoundedPath(
                new Rectangle(card.X + 6, card.Y + 8, 3, GroupHeaderHeight - 16), 1.5f);
            g.FillPath(barBrush, barPath);
        }

        // 组标题行
        var headY = card.Y;
        var arrow = new Rectangle(card.X + 12, headY + (GroupHeaderHeight - 10) / 2, 10, 10);
        var isHoverGroup = ReferenceEquals(group, _hoverGroup);
        DrawChevron(g, arrow, group.Expanded, isHoverGroup ? Theme.Accent : Theme.Muted);

        using (var brush = new SolidBrush(Theme.Ink))
            g.DrawString(group.Label, _fontGroup, brush,
                new Rectangle(card.X + 28, headY, card.Width - 96, GroupHeaderHeight), LeftFmt);

        // 计数徽标：宽度随数字长度走（两位数不再被 30px 挤扁），
        // 底色换成分隔线色 —— 原来用 Soft（近白）画在白卡片上，等于没有底。
        var countText = group.Items.Count.ToString();
        var badgeW = Math.Max(22, (int)Math.Ceiling(MeasureText(g, countText, _fontBadge)) + 14);
        DrawBadge(g, countText,
            new Rectangle(card.Right - 12 - badgeW, headY + (GroupHeaderHeight - 18) / 2, badgeW, 18),
            group.Pinned ? Theme.Accent : Blend(Theme.Muted, Theme.Ink, 0.82),
            Theme.Line);

        // 分隔线（组标题与内容之间）
        using (var pen = new Pen(Theme.Line))
            g.DrawLine(pen, card.X + 10, headY + GroupHeaderHeight - 1, card.Right - 10, headY + GroupHeaderHeight - 1);

        // 条目：只画到预览条数，余下的交给下面那一行「展开其余 N 个」。
        var shown = group.ShownCount;
        var drawn = (int)Math.Round(shown * group.Anim);
        for (int i = 0; i < drawn && i < group.Items.Count; i++)
        {
            var entry = group.Items[i];
            var row = new Rectangle(card.X + 8, headY + GroupHeaderHeight + i * RowHeight,
                                   card.Width - 16, RowHeight);
            DrawEntryRow(g, entry, row);
        }

        // 展开其余 N 个
        if (group.NeedsRevealRow && group.Anim > 0.35)
        {
            var row = new Rectangle(card.X + 8, headY + GroupHeaderHeight + drawn * RowHeight,
                                   card.Width - 16, RowHeight);
            if (ReferenceEquals(group, _hoverReveal))
            {
                using var hoverBrush = new SolidBrush(Theme.Hover);
                using var hoverPath = RoundedPath(row, Theme.SideBarRowRadius);
                g.FillPath(hoverBrush, hoverPath);
            }

            var label = group.ShowAll
                ? $"收起，只看最近 {group.PreviewLimit} 个"
                : $"展开其余 {group.Items.Count - shown} 个";
            using (var brush = new SolidBrush(Theme.Accent))
                g.DrawString(label, _fontFoot, brush,
                    new Rectangle(row.X + 18, row.Y, row.Width - 34, row.Height), LeftFmt);

            using var pen = new Pen(Theme.Accent, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cx = row.Right - 14f;
            var cy = row.Y + row.Height / 2f;
            if (group.ShowAll)
            {
                // 向上折：收起
                g.DrawLine(pen, cx - 3.4f, cy + 1.6f, cx, cy - 1.8f);
                g.DrawLine(pen, cx, cy - 1.8f, cx + 3.4f, cy + 1.6f);
            }
            else
            {
                // 向下展：展开
                g.DrawLine(pen, cx - 3.4f, cy - 1.6f, cx, cy + 1.8f);
                g.DrawLine(pen, cx, cy + 1.8f, cx + 3.4f, cy - 1.6f);
            }
        }
    }

    private void DrawEntryRow(Graphics g, SideBarEntry entry, Rectangle row)
    {
        var isHover = ReferenceEquals(entry, _hoverEntry);

        // 悬停底
        if (isHover)
        {
            using var brush = new SolidBrush(Theme.Hover);
            using var path = RoundedPath(row, Theme.SideBarRowRadius);
            g.FillPath(brush, path);
        }

        // 状态点
        var dot = new Rectangle(row.X + 6, row.Y + (row.Height - 6) / 2, 6, 6);
        using (var brush = new SolidBrush(Theme.StatusColor(entry.Status)))
            g.FillEllipse(brush, dot);

        // 名称（右侧留出固定宽度的 Meta 列）
        //
        // 右列以前按每条自己的文字宽度反推起点，于是「10-31」和「绘画」两种列的右缘
        // 各在自己的位置 —— 一列数字看着像歪的。现在固定 46px 的列，右对齐到同一条线。
        var metaW = string.IsNullOrEmpty(entry.Meta) ? 0 : MetaColumnWidth;
        var nameRect = new Rectangle(row.X + 18, row.Y,
            Math.Max(0, row.Width - 22 - (metaW > 0 ? metaW + 8 : 0)), row.Height);

        using (var brush = new SolidBrush(entry.Status == "archived" ? Theme.Muted : Theme.Ink))
            DrawSpacedLabel(g, entry.Label, _fontRow, brush, nameRect);

        // 次要信息（更新时间 / 类型）
        if (metaW > 0)
        {
            using var metaBrush = new SolidBrush(Blend(Theme.Muted, Theme.Ink, 0.35));
            g.DrawString(entry.Meta, _fontFoot, metaBrush,
                new Rectangle(row.Right - 6 - metaW, row.Y, metaW, row.Height), RightFmt);
        }
    }

    /// <summary>右列的固定宽度：容得下「10-31」和「绘画」两字，且各行右缘对齐。</summary>
    private const int MetaColumnWidth = 46;

    /// <summary>
    /// 画「X｜标题」这类带全角竖线的名称。
    ///
    /// 全角竖线「｜」自带左右空白，但在 12.5px 的侧边栏行里，它和两边的字几乎贴在一起，
    /// 读起来像「图|个人作品集」。这里把名称拆成「前缀 / 竖线 / 标题」三段，
    /// 竖线两侧各留 3px —— 只改渲染，不动数据里的名字（页面标题、悬停全文都保持原样）。
    /// </summary>
    private static void DrawSpacedLabel(Graphics g, string text, Font font, Brush brush, Rectangle box)
    {
        if (string.IsNullOrEmpty(text)) return;

        var idx = text.IndexOf('｜');
        if (idx <= 0 || idx >= text.Length - 1)
        {
            g.DrawString(text, font, brush, box, LeftFmt);
            return;
        }

        const float gap = 3f;
        var prefix = text[..idx];
        var bar = text[idx].ToString();
        var rest = text[(idx + 1)..];

        var x = (float)box.X;
        var w1 = MeasureText(g, prefix, font);
        g.DrawString(prefix, font, brush, new RectangleF(x, box.Y, w1 + 2f, box.Height), LeftFmt);

        x += w1 + gap;
        var wb = MeasureText(g, bar, font);
        g.DrawString(bar, font, brush, new RectangleF(x, box.Y, wb + 2f, box.Height), LeftFmt);

        x += wb + gap;
        var restW = Math.Max(0f, box.Right - x);
        g.DrawString(rest, font, brush, new RectangleF(x, box.Y, restW, box.Height), LeftFmt);
    }

    /// <summary>两色按比例混合：t=0 全 a，t=1 全 b。</summary>
    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    private void DrawBadge(Graphics g, string text, Rectangle box, Color fg, Color bg)
    {
        using (var path = RoundedPath(box, box.Height / 2f))
        {
            using var brush = new SolidBrush(bg);
            g.FillPath(brush, path);
        }
        using var brush2 = new SolidBrush(fg);
        g.DrawString(text, _fontBadge, brush2, box, CenterFmt);
    }

    private static void DrawChevron(Graphics g, Rectangle box, bool expanded, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var d = 3.2f;

        if (expanded)
        {
            // 向下
            g.DrawLine(pen, cx - d, cy - 1.6f, cx, cy + 1.8f);
            g.DrawLine(pen, cx, cy + 1.8f, cx + d, cy - 1.6f);
        }
        else
        {
            // 向右
            g.DrawLine(pen, cx - 1.6f, cy - d, cx + 1.8f, cy);
            g.DrawLine(pen, cx + 1.8f, cy, cx - 1.6f, cy + d);
        }
    }

    private void DrawScrollBar(Graphics g)
    {
        var area = ListArea;
        if (_contentHeight <= area.Height) return;

        var trackH = area.Height;
        var thumbH = Math.Max(36, (int)(area.Height * (double)area.Height / _contentHeight));
        var maxScroll = _contentHeight - area.Height;
        var t = (double)_scrollY / Math.Max(1, maxScroll);
        var thumbY = area.Y + (int)((trackH - thumbH) * t);

        using var brush = new SolidBrush(Theme.IsDark
            ? Color.FromArgb(90, 255, 255, 255)
            : Color.FromArgb(48, 30, 24, 40));
        var thumb = new Rectangle(Width - 6, thumbY, 3, thumbH);
        using var path = RoundedPath(thumb, 2f);
        g.FillPath(brush, path);
    }

    private void DrawFooter(Graphics g)
    {
        var y = Height - FooterHeight;
        using (var pen = new Pen(Theme.Line))
            g.DrawLine(pen, 0, y, Width, y);

        // 连接状态点
        var dot = new Rectangle(PadX, y + (FooterHeight - 6) / 2, 6, 6);
        using (var brush = new SolidBrush(_connected ? Theme.Green : Theme.Amber))
            g.FillEllipse(brush, dot);

        using (var brush = new SolidBrush(Theme.Muted))
            g.DrawString(_statusText, _fontFoot, brush,
                new Rectangle(PadX + 12, y, Width - PadX * 2, FooterHeight), LeftFmt);
    }

    // ── 绘制辅助 ─────────────────────────────────────────────────

    private static GraphicsPath RoundedPath(Rectangle r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        if (d <= 0 || r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath OffsetPath(GraphicsPath src, int dx, int dy)
    {
        var m = new Matrix();
        m.Translate(dx, dy);
        var clone = src.Clone() as GraphicsPath ?? new GraphicsPath();
        clone.Transform(m);
        return clone;
    }

    private static float MeasureText(Graphics g, string text, Font font) =>
        string.IsNullOrEmpty(text) ? 0 : g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;

    // ── 动画 ─────────────────────────────────────────────────────

    private void StepAnimations()
    {
        _dirty = false;

        // 刷新中转圈：每帧 14 度（60fps 约 1.3 秒一圈）。刷新结束就不再推进，定时器随之停。
        if (_refreshing)
        {
            _spin = (_spin + 14) % 360;
            _dirty = true;
        }

        foreach (var group in _groups)
        {
            var target = group.Expanded ? 1.0 : 0.0;
            if (Math.Abs(group.Anim - target) < 0.01)
            {
                group.Anim = target;
            }
            else
            {
                group.Anim += (target - group.Anim) * 0.24;
                if (Math.Abs(group.Anim - target) < 0.01) group.Anim = target;
                _dirty = true;
            }

            // 「展开其余」的行数插值：点开后从预览条数平滑长到全部，
            // 不这样就是一帧跳过去，长列表会把用户正在看的那一行顶走。
            var revealTarget = group.ShowAll ? 1.0 : 0.0;
            if (Math.Abs(group.Reveal - revealTarget) < 0.01)
            {
                group.Reveal = revealTarget;
            }
            else
            {
                group.Reveal += (revealTarget - group.Reveal) * 0.22;
                if (Math.Abs(group.Reveal - revealTarget) < 0.01) group.Reveal = revealTarget;
                _dirty = true;
            }
        }

        // 悬停淡入
        var hoverTarget = (_hoverEntry is not null || _hoverGroup is not null || _hoverRefresh) ? 1.0 : 0.0;
        if (Math.Abs(_hoverFade - hoverTarget) > 0.01)
        {
            _hoverFade += (hoverTarget - _hoverFade) * 0.3;
            _dirty = true;
        }
        else _hoverFade = hoverTarget;

        var refreshTarget = _hoverRefresh ? 1.0 : 0.0;
        if (Math.Abs(_refreshFade - refreshTarget) > 0.01)
        {
            _refreshFade += (refreshTarget - _refreshFade) * 0.3;
            _dirty = true;
        }
        else _refreshFade = refreshTarget;

        // 光标由独立定时器管（见 _caret 的注释），这里不再翻转。
    }

    // ── 命中测试 ─────────────────────────────────────────────────

    private (Rectangle Rect, Group? Group, SideBarEntry? Entry)? HitTest(Point p)
    {
        foreach (var h in _hits)
            if (h.Rect.Contains(p)) return h;
        return null;
    }

    /// <summary>「展开其余 N 个」那一行的命中测试。</summary>
    private Group? HitTestReveal(Point p)
    {
        foreach (var h in _revealHits)
            if (h.Rect.Contains(p)) return h.Group;
        return null;
    }

    /// <summary>导航项命中测试（固定区，不受滚动影响）。</summary>
    private string? HitTestNav(Point p)
    {
        foreach (var h in _navHits)
            if (h.Rect.Contains(p)) return h.Key;
        return null;
    }

    private bool IsOnRefreshButton(Point p)
    {
        var btn = new Rectangle(Width - PadX - 26, 11, 26, 26);
        return btn.Contains(p);
    }

    private bool IsOnSearch(Point p)
    {
        var box = new Rectangle(PadX, Theme.SideBarSearchTop, CardWidth, Theme.SideBarSearchBoxHeight);
        return box.Contains(p);
    }

    // ── 鼠标 ─────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var onRefresh = IsOnRefreshButton(e.Location);
        var onSearch = IsOnSearch(e.Location);
        var hit = HitTest(e.Location);
        var navHit = HitTestNav(e.Location);

        var revealHit = HitTestReveal(e.Location);

        var prevEntry = _hoverEntry;
        var prevGroup = _hoverGroup;
        var prevReveal = _hoverReveal;
        var prevNav = _hoverNav;

        _hoverRefresh = onRefresh;
        _hoverEntry = hit?.Entry;
        _hoverGroup = hit?.Group;
        _hoverReveal = revealHit;
        _hoverNav = navHit;

        if (!ReferenceEquals(prevEntry, _hoverEntry) || !ReferenceEquals(prevGroup, _hoverGroup)
            || !ReferenceEquals(prevReveal, _hoverReveal)
            || onRefresh != _hoverRefresh || prevNav != _hoverNav)
        {
            _dirty = true;
            _anim.Start();
        }

        // 只有被截断的名字才出提示 —— 没截断的名字再弹一遍是噪音。
        if (!ReferenceEquals(_tipEntry, _hoverEntry))
        {
            _tipEntry = _hoverEntry;
            var full = _hoverEntry?.FullLabel ?? "";
            var showTip = !string.IsNullOrEmpty(full) && full != (_hoverEntry?.Label ?? "");
            _tip.SetToolTip(this, showTip ? full : "");
        }

        Cursor = onRefresh || onSearch || hit is not null || navHit is not null || revealHit is not null
            ? Cursors.Hand
            : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverEntry = null;
        _hoverGroup = null;
        _hoverReveal = null;
        _hoverRefresh = false;
        _hoverNav = null;
        _dirty = true;
        _anim.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (IsOnSearch(e.Location))
        {
            Focus();
            _searchFocused = true;
            _caretOn = true;
            _caretBlink.Stop();
            _caretBlink.Start();
            _dirty = true;
            _anim.Start();
            Invalidate();
            return;
        }

        if (IsOnRefreshButton(e.Location))
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var navHit = HitTestNav(e.Location);
        if (navHit is not null)
        {
            SetActiveNav(navHit);
            NavRequested?.Invoke(this, navHit);
            return;
        }

        // 「展开其余 N 个」：点一下把这一组剩下的条目放出来。
        var revealGroup = HitTestReveal(e.Location);
        if (revealGroup is not null)
        {
            revealGroup.ShowAll = !revealGroup.ShowAll;
            _dirty = true;
            _anim.Start();
            Invalidate();
            return;
        }

        var hit = HitTest(e.Location);
        if (hit is null) return;

        if (hit.Value.Entry is not null)
        {
            var entry = hit.Value.Entry;
            TabRequested?.Invoke(this, (entry.Label, entry.Url));
            return;
        }

        if (hit.Value.Group is not null)
        {
            var group = hit.Value.Group;
            group.Expanded = !group.Expanded;
            _dirty = true;
            _anim.Start();
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var area = ListArea;
        var maxScroll = Math.Max(0, _contentHeight - area.Height);
        if (maxScroll <= 0) return;

        _scrollY = Math.Clamp(_scrollY - Math.Sign(e.Delta) * 48, 0, maxScroll);
        Invalidate();
    }

    // ── 键盘（自绘搜索框：自己处理输入） ────────────────────────

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!_searchFocused)
        {
            // 未聚焦时：任意可打印字符进入搜索
            if (e.KeyCode == Keys.Escape) return;
        }

        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (_searchFocused && _search.Length > 0)
                {
                    _search = "";
                    _caret = 0;
                    SearchChanged?.Invoke(this, EventArgs.Empty);
                }
                _searchFocused = false;
                _caretBlink.Stop();
                e.Handled = true;
                break;

            case Keys.Back:
                if (_caret > 0)
                {
                    _search = _search.Remove(_caret - 1, 1);
                    _caret--;
                    SearchChanged?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
                break;

            case Keys.Delete:
                if (_caret < _search.Length)
                {
                    _search = _search.Remove(_caret, 1);
                    SearchChanged?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
                break;

            case Keys.Left:
                if (_caret > 0) _caret--;
                e.Handled = true;
                break;

            case Keys.Right:
                if (_caret < _search.Length) _caret++;
                e.Handled = true;
                break;

            case Keys.Home:
                _caret = 0;
                e.Handled = true;
                break;

            case Keys.End:
                _caret = _search.Length;
                e.Handled = true;
                break;

            case Keys.Enter:
                _searchFocused = false;
                _caretBlink.Stop();
                e.Handled = true;
                break;

            case Keys.A when e.Control:
                _caret = _search.Length;
                e.Handled = true;
                break;
        }

        // 任何按键都让光标立刻回到可见相位，再从这一刻重新计 530ms。
        _caretOn = true;
        _caretBlink.Stop();
        if (_searchFocused) _caretBlink.Start();

        _dirty = true;
        _anim.Start();
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!char.IsControl(e.KeyChar))
        {
            _search = _search.Insert(_caret, e.KeyChar.ToString());
            _caret++;
            _searchFocused = true;
            _caretOn = true;
            _caretBlink.Stop();
            _caretBlink.Start();
            SearchChanged?.Invoke(this, EventArgs.Empty);
            _dirty = true;
            _anim.Start();
            Invalidate();
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _searchFocused = true;
        _caretOn = true;
        _caretBlink.Stop();
        _caretBlink.Start();
        _dirty = true;
        _anim.Start();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _searchFocused = false;
        _caretBlink.Stop();
        _caretOn = true;
        Invalidate();
    }

    /// <summary>搜索词变化（由宿主决定如何过滤）。</summary>
    public event EventHandler? SearchChanged;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _scrollY = 0;
        Invalidate();
    }
}
