// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 侧边栏：审阅项目树（数据层 + 宿主）。
//
// 职责划分：
//   本类   —— 从 GET /api/workbench 拉清单、按「空间」归类、按搜索词过滤；
//   SideBarPanel —— 纯自绘的视觉层（圆角卡片 / 微动效 / 紧凑版式）。
//
// 2026-10-31 修订（用户反馈「待审项目是全量项目」）：
//   侧边栏只放**还要人看的**东西。规则三条：
//     1. 已归档的不进来（服务端已按 includeArchived:false 过滤，本类再兜一层）；
//     2. 最近活跃单开一个置顶组，按更新时间倒序取前几条；
//     3. 其余按空间分「绘画 / 视频 / 素材与参考 / 研究与记录」，默认收起。
//   同一个项目可以同时出现在「最近活跃」和它所属的空间里 —— 这是有意的：
//   置顶组是快捷入口，空间组是完整目录。

namespace WorkbenchShell;

using System.Text.Json;

internal sealed class SideBar : UserControl
{
    private readonly SideBarPanel _panel = new() { Dock = DockStyle.Fill };
    private readonly List<ReviewProject> _all = new();
    private bool _loading;
    private string _keyword = "";

    /// <summary>最近一次刷新耗时（毫秒）。自检与回执读它。</summary>
    public int LastRefreshMs { get; private set; }

    /// <summary>是否正在拉清单。</summary>
    public bool IsLoading => _loading;

    /// <summary>用户点了刷新按钮并完成后触发（宿主据此通知页面同步）。</summary>
    public event EventHandler<(bool Force, int Ms)>? RefreshCompleted;

    /// <summary>请求打开一个外部审阅页（开成原生标签页）。</summary>
    public event EventHandler<(string Title, string Url)>? TabRequested;

    /// <summary>请求切换工作台页面（壳接管导航）。</summary>
    public event EventHandler<string>? NavRequested;

    /// <summary>设置当前导航项的高亮（与页面 hash 路由同步）。</summary>
    public void SetActiveNav(string key) => _panel.SetActiveNav(key);

    /// <summary>已加载的待审项目数（自检用）。</summary>
    public int ProjectCount => _all.Count;

    /// <summary>可见节点数（自检用）：分组数 + 条目数。</summary>
    public int VisibleNodeCount => _panel.VisibleNodeCount;

    private sealed class ReviewProject
    {
        public string Name = "";
        public string Url = "";
        public string Status = "";
        public string Kind = "";
        public string Description = "";
        public string FolderPath = "";
        public DateTime UpdatedAt;
        /// <summary>归类结果：drawing / video / library / record。</summary>
        public string Space = "record";
    }

    public SideBar()
    {
        Dock = DockStyle.Left;
        BackColor = Theme.Bg;
        Padding = new Padding(0);
        MinimumSize = new Size(200, 240);

        Controls.Add(_panel);

        _panel.TabRequested += (_, e) => TabRequested?.Invoke(this, e);
        // 侧边栏标题旁的刷新图标：真的打 /api/workbench?fresh=1，完成后通知宿主，
        // 由宿主让当前页面用同一份缓存同步一次（页面走缓存，不会重复计算）。
        _panel.RefreshRequested += async (_, _) => await LoadAsync(forceFresh: true, notifyHost: true);
        _panel.SearchChanged += (_, _) => ApplyFilter();
        _panel.NavRequested += (_, key) => NavRequested?.Invoke(this, key);
    }

    // ──────────────────────────── 数据加载 ────────────────────────────

    /// <summary>
    /// 拉审阅清单。
    ///
    /// 服务端只返回未归档的审阅页；这里再加一层 <c>review_status !== archived</c> 兜底，
    /// 万一服务端缓存里还有旧数据，也不会把已归档的项目重新堆到侧边栏上。
    /// </summary>
    public async Task LoadAsync(bool forceFresh = false, bool notifyHost = false)
    {
        if (_loading) return;
        _loading = true;
        // 刷新期间：标题旁图标转圈，底部状态写明在干什么。
        // 展开状态与滚动位置都不动 —— SetGroups 按分组 Key 记住「是否展开 / 是否点过展开其余」，
        // _scrollY 也不会被重置（只有窗口尺寸变化才归零）。
        _panel.SetRefreshing(true);
        _panel.SetStatus(forceFresh ? "刷新中…" : "加载中…", false);
        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ★ 必须显式关掉代理。
            //   .NET 的 HttpClient 默认读环境变量里的 HTTP_PROXY / HTTPS_PROXY，
            //   本机设了这些变量，127.0.0.1:3900 的请求就被甩给代理，
            //   代理连不上本机内网端口 —— 表现为「项目列表一直是空的」
            //   或抛 "The request was canceled"。
            //   本机服务一律直连。
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            var url = Workbench.WorkbenchOrigin + "/api/workbench";
            if (forceFresh) url += "?fresh=1";
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("review_pages", out var pages))
            {
                _panel.SetStatus("接口没返回项目列表", false);
                return;
            }

            _all.Clear();
            foreach (var p in pages.EnumerateArray())
            {
                var name = GetStr(p, "name");
                var pageUrl = GetStr(p, "url");
                if (string.IsNullOrWhiteSpace(pageUrl)) continue;

                var status = GetStr(p, "review_status");
                if (string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase)) continue;

                var project = new ReviewProject
                {
                    Name = name,
                    Url = pageUrl,
                    Status = status,
                    Kind = GetStr(p, "catalog_kind"),
                    Description = GetStr(p, "desc"),
                    FolderPath = GetStr(p, "folder_path"),
                };

                var updated = GetStr(p, "updated_at");
                if (DateTime.TryParse(updated, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                    project.UpdatedAt = at;

                project.Space = SpaceOf(project);
                _all.Add(project);
            }

            ApplyFilter();
            startedAt.Stop();
            LastRefreshMs = (int)startedAt.ElapsedMilliseconds;
            _panel.SetStatus(forceFresh
                ? $"已更新 · {DateTime.Now:HH:mm}"
                : $"已连接本机 · {DateTime.Now:HH:mm}", true);
            if (notifyHost) RefreshCompleted?.Invoke(this, (forceFresh, LastRefreshMs));
        }
        catch (Exception ex)
        {
            _panel.SetStatus($"加载失败：{ex.Message}", false);
        }
        finally
        {
            _loading = false;
            _panel.SetRefreshing(false);
        }
    }

    /// <summary>
    /// 页面那边的刷新已经重算过清单：这里只走服务端缓存同步一次（通常 2–40 ms），
    /// 保持两侧数据一致，同时不再额外触发一次全量计算。
    /// </summary>
    public async Task ReloadFromCacheAsync(int pageMs = 0)
    {
        await LoadAsync(forceFresh: false);
        if (pageMs > 0) _panel.SetStatus($"已与页面同步 · {DateTime.Now:HH:mm} · {pageMs} ms", true);
    }

    /// <summary>滚动位置（自检用：验证刷新前后没有被重置）。</summary>
    public int ScrollOffset => _panel.ScrollOffset;

    /// <summary>各分组展开状态（自检用，透传面板）。</summary>
    public IReadOnlyDictionary<string, bool> GroupState => _panel.GroupState;

    /// <summary>各分组「点过展开其余」状态（自检用，透传面板）。</summary>
    public IReadOnlyDictionary<string, bool> GroupRevealState => _panel.GroupRevealState;

    /// <summary>把某个分组的「展开其余」先行打开（自检用）。</summary>
    public bool RevealGroup(string key) => _panel.RevealGroup(key);

    /// <summary>设置滚动位置（自检用）。</summary>
    public void ScrollTo(int y) => _panel.ScrollTo(y);

    /// <summary>取证用：把侧边栏切成「刷新中」的样子拍一张（不影响真实状态）。</summary>
    public void PreviewRefreshing(bool on, string? status = null)
    {
        _panel.SetRefreshing(on);
        _panel.SetStatus(status ?? (on ? "刷新中…" : "已更新 · " + DateTime.Now.ToString("HH:mm")), true);
    }

    private static string GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// 归类到「空间」。判据优先看目录，其次看 catalog_kind，最后看名字。
    ///
    /// 为什么不直接用页面的 reviewCategory：
    ///   页面那套是「素材库 / 截图 / 研究 / 任务记录」的工作流分类，
    ///   而用户看侧边栏时想的是「这是哪一类活」—— 绘画、视频、素材、记录。
    ///   两者维度不同，侧边栏按后者组织。
    /// </summary>
    private static string SpaceOf(ReviewProject p)
    {
        var path = (p.FolderPath + " " + p.Url).Replace('\\', '/').ToLowerInvariant();
        var name = p.Name;

        // 审阅台与参考库：这些是「一直开着、随时点进去看」的入口，不是一次性的活。
        // 把它们混进待审清单，用户会以为还有十几件没审。
        if (path.Contains("/reference-desks/") ||
            path.Contains("设计资产库") ||
            name.Contains("审阅台") || name.Contains("参考台") || name.Contains("参考库") ||
            name.Contains("素材总库") || name.Contains("总库") || name.Contains("图鉴") ||
            name.Contains("门禁"))
            return "desk";

        // 素材与参考：素材库、参考台、下载区，以及「解包 / 服饰库 / 图鉴」这类整理活。
        if (path.Contains("reference-desks") ||
            path.Contains("设计资产库") ||
            path.Contains("b站下载") ||
            path.Contains("游戏资产") ||
            p.Kind == "asset_record" ||
            name.Contains("素材") || name.Contains("服饰库") || name.Contains("参考库") ||
            name.Contains("图鉴") || name.Contains("解包"))
            return "library";

        // 绘画：生图产线产出的图集、角色 LoRA 对照、插画与海报稿。
        if (path.Contains("小红书生图") ||
            path.Contains("ai生图工作流") ||
            path.Contains("lora训练") ||
            path.Contains("ournotes") ||
            name.Contains("生图") || name.Contains("插画") || name.Contains("画集") ||
            name.Contains("角色LoRA") || name.Contains("角色设计") || name.Contains("立绘"))
            return "drawing";

        // 自媒体工坊里的展示与复盘：一个是图，一个是记录，别被「2-视频制作」误判成视频。
        if (path.Contains("1-自媒体工坊/个人展示") || name.Contains("作品集")) return "drawing";
        if (path.Contains("1-自媒体工坊")) return "record";

        // 视频：D 盘视频项目与带视频语义的审阅。
        if (path.Contains("2-视频制作") ||
            path.Contains("视频制作") ||
            name.Contains("视频") || name.Contains("PV") || name.Contains("分镜"))
            return "video";

        return "record";
    }

    private static string SpaceLabel(string space) => space switch
    {
        "desk" => "审阅台",
        "drawing" => "绘画",
        "video" => "视频",
        "library" => "素材",
        _ => "记录",
    };

    // ──────────────────────────── 分组构建 ────────────────────────────

    private static readonly (string Key, string Label)[] Spaces =
    {
        ("desk", "审阅台与参考库"),
        ("drawing", "绘画"),
        ("video", "视频"),
        ("library", "素材"),
        ("record", "研究与记录"),
    };

    /// <summary>最近活跃组最多显示几条（0 = 不显示这一组）。</summary>
    private static int RecentLimit => UiSettings.Current.RecentLimit;

    /// <summary>待审项目先铺几条，其余折在「展开其余 N 个」里。</summary>
    private static int ReviewPreviewLimit => UiSettings.Current.ReviewPreviewLimit;

    private void ApplyFilter()
    {
        var groups = new List<SideBarGroupSpec>();

        bool Match(ReviewProject p) =>
            _keyword.Length == 0 ||
            p.Name.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
            p.Description.Contains(_keyword, StringComparison.OrdinalIgnoreCase);

        var pool = _all.Where(Match).ToList();

        // 待审清单只看真正的项目；审阅台与参考库是常驻入口，单独成组。
        var projectPool = pool.Where(p => p.Space != "desk").ToList();

        // 1) 最近活跃：跨分类的快捷入口，按更新时间倒序。条数为 0 时整组不出。
        if (RecentLimit > 0)
        {
            var recent = projectPool
                .Where(p => p.UpdatedAt > DateTime.MinValue)
                .OrderByDescending(p => p.UpdatedAt)
                .Take(RecentLimit)
                .Select(p => ToEntry(p, showTime: true))
                .ToList();
            if (recent.Count > 0)
            {
                groups.Add(new SideBarGroupSpec
                {
                    Key = "recent",
                    Label = "最近活跃",
                    Items = recent,
                    Pinned = true,
                    DefaultExpanded = true,
                });
            }
        }

        // 2) 待审项目：全部未归档，按更新时间倒序。
        //    默认只铺前 6 条 —— 26 条一眼看不完，剩下的折成「展开其余 N 个」，
        //    想按类别找就去下面的空间分组，想看全部就点那一行 / 收起整组。
        var review = projectPool
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => ToEntry(p, showTime: false))
            .ToList();
        if (review.Count > 0)
        {
            groups.Add(new SideBarGroupSpec
            {
                Key = "review",
                Label = "待审项目",
                Items = review,
                DefaultExpanded = true,
                PreviewLimit = ReviewPreviewLimit,
            });
        }

        // 3) 项目空间：按类别分，默认收起 —— 首屏只给「最近活跃 + 待审」，
        //    要按类别找时再点开，不用先滚过一长串。
        foreach (var (key, label) in Spaces)
        {
            var items = pool
                .Where(p => p.Space == key)
                .OrderByDescending(p => p.UpdatedAt)
                .Select(p => ToEntry(p, showTime: true))
                .ToList();
            if (items.Count == 0) continue;

            groups.Add(new SideBarGroupSpec
            {
                Key = "space-" + key,
                Label = label,
                Items = items,
                DefaultExpanded = false,
            });
        }

        _panel.SetGroups(groups, projectPool.Count);
    }

    private static SideBarEntry ToEntry(ReviewProject p, bool showTime) => new()
    {
        Label = ShortenTitle(p.Name),
        FullLabel = p.Name,
        Url = p.Url,
        Status = p.Status,
        Description = p.Description,
        Meta = showTime
            ? (p.UpdatedAt > DateTime.MinValue ? p.UpdatedAt.ToLocalTime().ToString("M-d") : "")
            : SpaceLabel(p.Space),
    };

    /// <summary>
    /// 侧边栏一行最多显示多少个全角字。用户定的是 10 个字 ——
    /// 再长这一行就只剩省略号，看不出是哪个项目。
    /// </summary>
    private static int MaxTitleChars => UiSettings.Current.MaxTitleChars;

    /// <summary>
    /// 按全角宽度截断标题：汉字算 2 个单位、ASCII 算 1 个。
    /// 超长时留一个全角字的位置放「…」，所以最宽仍是 10 个字。
    /// 完整名字留在 <see cref="SideBarEntry.FullLabel"/>，悬停能看全。
    /// </summary>
    private static string ShortenTitle(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var limit = UiSettings.Current.MaxTitleChars * 2;
        var total = 0;
        for (var i = 0; i < name.Length; i++) total += name[i] <= 0x7F ? 1 : 2;
        if (total <= limit) return name;

        var budget = limit - 2;   // 给「…」留位
        var acc = 0;
        var cut = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var w = name[i] <= 0x7F ? 1 : 2;
            if (acc + w > budget) break;
            acc += w;
            cut = i + 1;
        }
        return cut <= 0 ? "…" : name[..cut] + "…";
    }
}
