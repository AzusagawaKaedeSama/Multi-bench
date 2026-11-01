// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 产品级右键菜单：像桌面应用一样，右键就知道能干什么。
//
// 目标（用户原话）：
//   「我右键图片我就要能够复制，或者说它打开在资源管理器的位置」
//   「把这些有超链接的那些东西都搞好」
//
// 为什么不用 WebView2 自带的菜单：
//   自带菜单只有「在新标签打开 / 复制图片 / 复制地址」这类网页动作，
//   没有「把图片放进系统剪贴板」「在资源管理器中定位」「复制本地路径」——
//   而这些才是本地素材库用户真正要的（本机审阅页里全是图）。
//
// 上下文怎么来（两条腿走路，互为兜底）：
//   ① CoreWebView2ContextMenuTarget —— 同步给出 图片源 / 链接 / 选区，不需要等；
//   ② 页面内 bridge.js 的 window.__wbDescribe() —— 现场反查 DOM，能拿到
//      alt 文字、data-menu-target（业务上下文），也分得清「点在图片上」
//      与「点在图片旁边的空白上」。
//   两条合并，脚本失败时退回 ①，菜单永远不会开天窗。
//
// 菜单规范（对标 ChatGPT 桌面端 / Edge / WorkBuddy）：
//   · 分组用分隔线隔开，破坏性/低频操作一律沉底；
//   · 只有真能做的事才出现（本地路径解不出来就不显示「在资源管理器中显示」）；
//   · 复制类动作给明确反馈（气泡提示 + 剪贴板）；
//   · 没有本地文件就不假装有。

using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Web.WebView2.Core;

namespace WorkbenchShell;

/// <summary>光标处元素的描述。字段与页面 bridge.js 产出的 JSON 一一对应。</summary>
internal sealed class ContextInfo
{
    /// <summary>页面用 data-menu-target 声明的业务目标（task: / delivery: / review:）。</summary>
    public string Target { get; set; } = "";

    /// <summary>元素的标签名（IMG / A / DIV …）。</summary>
    public string Tag { get; set; } = "";

    /// <summary>图片地址（绝对 URL）。</summary>
    public string ImgSrc { get; set; } = "";

    /// <summary>图片的 alt 文字。</summary>
    public string ImgAlt { get; set; } = "";

    /// <summary>图片原始像素宽。</summary>
    public int ImgWidth { get; set; }

    /// <summary>图片原始像素高。</summary>
    public int ImgHeight { get; set; }

    /// <summary>超链接地址。</summary>
    public string Href { get; set; } = "";

    /// <summary>超链接文字。</summary>
    public string LinkText { get; set; } = "";

    /// <summary>页面选中的文字。</summary>
    public string Selection { get; set; } = "";

    /// <summary>光标处是否是可编辑区域。</summary>
    public bool Editable { get; set; }

    /// <summary>当前页面地址。</summary>
    public string PageUrl { get; set; } = "";

    /// <summary>当前页面标题。</summary>
    public string PageTitle { get; set; } = "";

    public bool HasImage => ImgSrc.Length > 0;
    public bool HasLink => Href.Length > 0;
    public bool HasSelection => !string.IsNullOrWhiteSpace(Selection);
}

/// <summary>
/// 网页资源 ↔ 本地磁盘的换算。
///
/// 审阅服务（3955）的 URL 规则（见 审阅服务.py 的 local_path）：
///   http://127.0.0.1:3955/&lt;盘符&gt;/&lt;路径&gt;  ⇒  &lt;盘符&gt;:&lt;路径&gt;
/// 只允许 D、E 两个盘（服务端的 ROOTS 限制），这里跟它保持一致。
/// </summary>
internal static class WebAsset
{
    /// <summary>审阅服务端口。与 审阅服务.py 的默认端口一致。</summary>
    private const int ReviewPort = 3955;

    /// <summary>图片门禁确认单服务端口（gate_server.py）。图片 URL 形如 /img?p=&lt;绝对路径&gt;。</summary>
    private const int GatePort = 3958;

    /// <summary>服务允许暴露的盘符。</summary>
    private static readonly string[] AllowedDrives = { "D", "E" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// 把一个 URL 换算成本地路径（纯映射，不查磁盘）。
    /// 换不出来（不是本机服务能映射的文件）返回 null。
    /// </summary>
    internal static string? MapToLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return new Uri(url).LocalPath;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            // 图片门禁确认单（3958）：http://127.0.0.1:3958/img?p=E%3A%5C...%5Cx.png
            // 它把绝对路径原样放在查询串里，解码即得本地文件。
            if (uri.Port == GatePort && uri.AbsolutePath.Equals("/img", StringComparison.OrdinalIgnoreCase))
                return MapGateImageQuery(uri.Query);

            if (uri.Port != ReviewPort) return null;

            // /D/2-视频制作/x.jpg  →  D:\2-视频制作\x.jpg
            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            var slash = rel.IndexOf('/');
            if (slash <= 0) return null;

            var drive = rel[..slash].ToUpperInvariant();
            if (Array.IndexOf(AllowedDrives, drive) < 0) return null;

            var rest = rel[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath($"{drive}:{Path.DirectorySeparatorChar}{rest}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析确认单服务的 `/img?p=&lt;绝对路径&gt;`。</summary>
    private static string? MapGateImageQuery(string query)
    {
        var text = (query ?? "").TrimStart('?');
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals("p", StringComparison.OrdinalIgnoreCase)) continue;

            var raw = Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
            if (raw.Length == 0) return null;

            var full = Path.GetFullPath(raw);
            var root = Path.GetPathRoot(full) ?? "";
            var drive = root.TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
            return Array.IndexOf(AllowedDrives, drive) >= 0 ? full : null;
        }
        return null;
    }

    /// <summary>
    /// 换算成本地路径，并且要求它真的存在。
    ///
    /// 返回 null 是刻意的：菜单里就不该出现「在资源管理器中显示」，
    /// 出现一个点了没反应的项比没有更糟。
    /// </summary>
    internal static string? ToLocalPath(string? url)
    {
        var full = MapToLocalPath(url);
        if (full is null) return null;
        return File.Exists(full) || Directory.Exists(full) ? full : null;
    }

    /// <summary>取一个能当文件名的名字。</summary>
    internal static string FileNameOf(string url, string? localPath)
    {
        var name = "";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var seg = uri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrEmpty(seg)) name = Uri.UnescapeDataString(seg);
            }
        }
        catch { /* 忽略 */ }

        if (string.IsNullOrWhiteSpace(name) && localPath is not null)
            name = Path.GetFileName(localPath);

        if (string.IsNullOrWhiteSpace(name)) name = "图片.png";
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name;
    }

    /// <summary>取资源的字节：本地文件直接读，远程走 HTTP，data: 直接解 base64。</summary>
    internal static async Task<byte[]?> FetchAsync(string url, string? localPath)
    {
        if (localPath is not null && File.Exists(localPath))
        {
            try { return await File.ReadAllBytesAsync(localPath); }
            catch { /* 落到 HTTP 方案 */ }
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma > 0 && url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                try { return Convert.FromBase64String(url[(comma + 1)..]); }
                catch { return null; }
            }
            return null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try { return await Http.GetByteArrayAsync(url); }
            catch { return null; }
        }

        return null;
    }
}

/// <summary>
/// 审阅页索引：id → 页面地址 / 本地页面文件。
///
/// 卡片右键要「复制页面路径」「在资源管理器中显示」，而卡片本身只有 data-menu-target="review:&lt;id&gt;"，
/// 本地文件路径在 3900 的 /api/review-pages 里。这里做一层 30 秒缓存 —— 右键动作是低频操作，
/// 不值得每次开一个 HTTP 请求，但也不能缓存太久（新登记的审阅页要能立刻点开）。
/// </summary>
internal static class ReviewIndex
{
    private sealed record Entry(string? Url, string? PagePath);

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static Dictionary<string, Entry>? _cache;
    private static DateTime _cachedAt;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<(string? Url, string? PagePath)> LookupAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (null, null);

        var cache = _cache;
        if (cache is null || (DateTime.UtcNow - _cachedAt).TotalSeconds > 30)
        {
            await Gate.WaitAsync();
            try
            {
                if (_cache is null || (DateTime.UtcNow - _cachedAt).TotalSeconds > 30)
                    await RefreshAsync();
                cache = _cache;
            }
            finally { Gate.Release(); }
        }

        if (cache is not null && cache.TryGetValue(id, out var hit)) return (hit.Url, hit.PagePath);
        return (null, null);
    }

    private static async Task RefreshAsync()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await Http.GetStringAsync(Workbench.WorkbenchOrigin + "/api/review-pages");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    var url = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    var pagePath = item.TryGetProperty("page_path", out var ppEl) ? ppEl.GetString() : null;
                    map[id] = new Entry(url, pagePath);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"审阅页索引刷新失败：{ex.Message}");
        }

        _cache = map;
        _cachedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// 右键菜单需要宿主提供的几件事。
/// 抽成接口，是为了菜单逻辑能脱离窗口单独自检。
/// </summary>
internal interface IContextMenuHost
{
    /// <summary>给用户一条轻量反馈（气泡提示）。</summary>
    void Notify(string title, string body);

    /// <summary>在当前分组开一个新标签。</summary>
    void OpenUrlInTab(string url);

    /// <summary>在一个新的原生窗口里打开。</summary>
    Task OpenUrlInNewWindowAsync(string title, string url);

    /// <summary>对页面执行编辑命令（复制/粘贴/全选）。</summary>
    void SendEditCommandTo(TabItem tab, string command);

    /// <summary>给页面发一条消息（业务动作）。</summary>
    void PostToPage(TabItem tab, System.Text.Json.Nodes.JsonObject payload);

    /// <summary>取页面上一次上报的业务目标串。</summary>
    string TakePendingContextTarget(TabItem tab);
}

/// <summary>把右键上下文变成一份符合桌面产品直觉的原生菜单。</summary>
internal sealed class NativeContextMenu
{
    private readonly IContextMenuHost _host;

    /// <summary>菜单字体。用系统的中文字体（微软雅黑），跟 Windows 11 菜单一致。</summary>
    private static readonly Font MenuFont = new("Microsoft YaHei UI", 9f);

    internal NativeContextMenu(IContextMenuHost host) => _host = host;

    /// <summary>构建菜单。返回 null 表示没有可展示的内容。</summary>
    internal async Task<ContextMenuStrip?> BuildAsync(
        TabItem tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var info = await InspectAsync(tab, e);
        if (info is null) return null;
        return BuildMenu(tab, info);
    }

    /// <summary>
    /// 按上下文拼出菜单本体。与采集分离，是为了能单独自检菜单结构
    /// （不用真的去点页面）。
    /// </summary>
    internal ContextMenuStrip? BuildMenu(TabItem tab, ContextInfo info)
    {
        var sections = new MenuSections();

        // ① 页面声明的业务上下文（任务审批 / 成果交付 / 审阅标记）
        AddBusinessItems(sections, tab, info);

        // ② 图片
        if (info.HasImage)
        {
            var local = WebAsset.ToLocalPath(info.ImgSrc);

            sections.Add(Item("打开图片", () => _host.OpenUrlInTab(info.ImgSrc)));
            sections.Add(Item("在新窗口中打开图片", () => _ = _host.OpenUrlInNewWindowAsync(
                WebAsset.FileNameOf(info.ImgSrc, local), info.ImgSrc)));
            sections.EndSection();

            sections.Add(Item("复制图片", () => _ = CopyImageAsync(info.ImgSrc, local, tab)));
            sections.Add(Item("复制图片地址", () => CopyText(info.ImgSrc, "图片地址已复制")));
            if (!string.IsNullOrWhiteSpace(info.ImgAlt))
            {
                sections.Add(Item($"复制图片说明（{Shorten(info.ImgAlt, 18)}）",
                    () => CopyText(info.ImgAlt, "图片说明已复制")));
            }
            sections.EndSection();

            if (local is not null)
            {
                sections.Add(Item("在资源管理器中显示", () => Reveal(local)));
                sections.Add(Item("复制本地路径", () => CopyText(local, "本地路径已复制")));
                sections.Add(Item("使用默认程序打开", () => OpenWithDefault(local)));
            }
            sections.Add(Item("另存为…", () => _ = SaveAsAsync(info.ImgSrc, local)));
            sections.EndSection();
        }

        // ③ 超链接
        if (info.HasLink)
        {
            var local = WebAsset.ToLocalPath(info.Href);

            sections.Add(Item("在新标签页中打开", () => _host.OpenUrlInTab(info.Href)));
            sections.Add(Item("在新窗口中打开", () => _ = _host.OpenUrlInNewWindowAsync(
                WebAsset.FileNameOf(info.Href, local), info.Href)));
            sections.EndSection();

            sections.Add(Item("复制链接地址", () => CopyText(info.Href, "链接地址已复制")));
            if (!string.IsNullOrWhiteSpace(info.LinkText))
                sections.Add(Item("复制链接文字", () => CopyText(info.LinkText, "链接文字已复制")));
            sections.EndSection();

            if (local is not null)
            {
                sections.Add(Item("在资源管理器中显示", () => Reveal(local)));
                sections.Add(Item("复制本地路径", () => CopyText(local, "本地路径已复制")));
            }
            sections.EndSection();
        }

        // ④ 选区
        if (info.HasSelection)
        {
            sections.Add(Item("复制", () => CopySelection(info, tab)));
            sections.Add(Item("粘贴", () => _host.SendEditCommandTo(tab, "paste")));
            sections.EndSection();

            sections.Add(Item("全选", () => _host.SendEditCommandTo(tab, "selectAll")));
            sections.EndSection();
        }

        // ⑤ 页面
        var pageUrl = info.PageUrl.Length > 0 ? info.PageUrl : tab.Url;
        sections.Add(Item("复制页面地址", () => CopyText(pageUrl, "页面地址已复制")));
        sections.Add(Item("重新载入", () => tab.View?.Reload()));
        sections.EndSection();

        var zoom = new ToolStripMenuItem("缩放");
        zoom.DropDownItems.Add(Item("放大", () => AdjustZoom(tab, 0.1)));
        zoom.DropDownItems.Add(Item("缩小", () => AdjustZoom(tab, -0.1)));
        zoom.DropDownItems.Add(Item("实际大小", () => AdjustZoom(tab, 0)));
        sections.Add(zoom);
        sections.Add(Item("在浏览器中打开", () => OpenInBrowser(pageUrl)));
        sections.EndSection();

        var menu = new ContextMenuStrip
        {
            Renderer = new ThemeRenderer(),
            Font = MenuFont,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };
        sections.Flush(menu);

        if (menu.Items.Count == 0) { menu.Dispose(); return null; }
        return menu;
    }

    // ──────────────────────────── 上下文采集 ────────────────────────────

    /// <summary>合并「宿主同步信息」与「页面现场反查」，得到最准的一份上下文。</summary>
    private async Task<ContextInfo?> InspectAsync(
        TabItem tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var info = new ContextInfo();

        // ① 宿主同步信息：不依赖脚本，先把底打好。
        try
        {
            var target = e.ContextMenuTarget;
            if (target is not null)
            {
                var isImage = target.Kind == CoreWebView2ContextMenuTargetKind.Image;
                if (isImage && target.HasSourceUri)
                    info.ImgSrc = target.SourceUri?.ToString() ?? "";
                if (target.HasLinkUri)
                    info.Href = target.LinkUri?.ToString() ?? "";
                if (target.HasLinkText)
                    info.LinkText = target.LinkText ?? "";
                if (target.HasSelection)
                    info.Selection = target.SelectionText ?? "";
                info.Editable = target.IsEditable;
                if (target.PageUri is not null)
                    info.PageUrl = target.PageUri.ToString();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"右键上下文（宿主侧）读取失败：{ex.Message}");
        }

        // ② 页面现场反查：更准，能补 alt / data-menu-target。
        var dom = await DescribeFromPageAsync(tab);
        if (dom is not null)
        {
            if (dom.ImgSrc.Length > 0) info.ImgSrc = dom.ImgSrc;
            if (dom.Href.Length > 0) info.Href = dom.Href;
            if (dom.LinkText.Length > 0) info.LinkText = dom.LinkText;
            if (dom.Selection.Length > 0) info.Selection = dom.Selection;
            if (dom.Target.Length > 0) info.Target = dom.Target;
            info.ImgAlt = dom.ImgAlt;
            info.ImgWidth = dom.ImgWidth;
            info.ImgHeight = dom.ImgHeight;
            info.Tag = dom.Tag;
            info.Editable |= dom.Editable;
            if (dom.PageUrl.Length > 0) info.PageUrl = dom.PageUrl;
            info.PageTitle = dom.PageTitle;
        }

        if (info.PageUrl.Length == 0) info.PageUrl = tab.Url;
        if (info.Target.Length == 0) info.Target = _host.TakePendingContextTarget(tab);
        return info;
    }

    /// <summary>在页面里问一句「光标处是什么」。</summary>
    private async Task<ContextInfo?> DescribeFromPageAsync(TabItem tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return null;

        try
        {
            const string script =
                "(function(){try{var d=(window.__wbDescribe&&window.__wbDescribe())||null;" +
                "return d?JSON.stringify(d):'null';}catch(e){return 'null';}})()";

            var raw = await core.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;

            // ExecuteScriptAsync 返回的是「结果的 JSON 序列化」。
            // 我们在脚本里返回的是字符串，所以要剥两层引号。
            var json = raw;
            if (json.StartsWith("\""))
            {
                var inner = JsonSerializer.Deserialize<string>(json);
                if (string.IsNullOrWhiteSpace(inner)) return null;
                json = inner;
            }

            return JsonSerializer.Deserialize<ContextInfo>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"右键上下文（页面侧）读取失败：{ex.Message}");
            return null;
        }
    }

    // ──────────────────────────── 业务上下文 ────────────────────────────

    /// <summary>页面用 data-menu-target 声明的业务动作，原样透传给页面。</summary>
    private void AddBusinessItems(MenuSections sections, TabItem tab, ContextInfo info)
    {
        var target = info.Target;
        if (string.IsNullOrEmpty(target)) return;

        if (target.StartsWith("task:", StringComparison.Ordinal))
        {
            var rest = target["task:".Length..];
            var dash = rest.LastIndexOf('-');
            var id = dash > 0 ? rest[..dash] : rest;

            sections.Add(Item("批准通过", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "task-action", ["taskId"] = id, ["action"] = "approve",
            })));
            sections.Add(Item("驳回", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "task-action", ["taskId"] = id, ["action"] = "reject",
            })));
            sections.EndSection();

            sections.Add(Item("打开源文件", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "task-open", ["taskId"] = id,
            })));
            sections.EndSection();
        }
        else if (target.StartsWith("delivery:", StringComparison.Ordinal))
        {
            var family = target["delivery:".Length..];
            sections.Add(Item("复制成果路径", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "delivery-copy-path", ["family"] = family,
            })));
            sections.Add(Item("打开成果文件夹", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "delivery-reveal", ["family"] = family,
            })));
            sections.EndSection();
        }
        else if (target.StartsWith("review:", StringComparison.Ordinal))
        {
            var review = target["review:".Length..];
            sections.Add(Item("标记已审阅完成", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "review-complete", ["review"] = review,
            })));
            sections.Add(Item("重新打开审阅", () => _host.PostToPage(tab, new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "review-reopen", ["review"] = review,
            })));
            sections.EndSection();

            // 卡片本身也是「一个文件」：把它的页面路径 / 页面地址拿出来。
            // 数据从 3900 的 /api/review-pages 反查（id → page_path / url），30 秒缓存。
            // 菜单一弹出来就先去查（不等待）：用户从右键到点菜单一般有半秒以上，
            // 正好把这次 HTTP 查询盖掉，点下去时缓存已经热了。
            _ = ReviewIndex.LookupAsync(review);
            sections.Add(Item("复制页面路径", () => _ = CopyReviewTargetAsync(review, ReviewField.PagePath)));
            sections.Add(Item("复制页面地址", () => _ = CopyReviewTargetAsync(review, ReviewField.Url)));
            sections.Add(Item("在资源管理器中显示", () => _ = RevealReviewAsync(review)));
            sections.EndSection();
        }
    }

    // ──────────────────────────── 动作 ────────────────────────────

    private void CopyText(string text, string message)
    {
        if (NativeMethods.CopyToClipboard(text))
            _host.Notify(message, text.Length > 140 ? text[..140] + "…" : text);
        else
            _host.Notify("复制失败", "剪贴板被别的程序占用了，请再试一次。");
    }

    private void CopySelection(ContextInfo info, TabItem tab)
    {
        // 页面选区已知时直接塞剪贴板，比 execCommand 稳；
        // 选区在 input 里时 getSelection 拿不到，退回 execCommand。
        if (info.HasSelection) CopyText(info.Selection, "已复制");
        else _host.SendEditCommandTo(tab, "copy");
    }

    /// <summary>
    /// 把图片真的放进剪贴板。
    ///
    /// 三步都不静默：取不到数据、格式放不进剪贴板、剪贴板被占用，各自给不同的提示，
    /// 并且**不再偷偷改成复制地址** —— 用户点的是「复制图片」，改成复制地址等于没做事，
    /// 只是让菜单看起来响应了。
    /// </summary>
    private async Task CopyImageAsync(string url, string? localPath, TabItem? tab = null)
    {
        var bytes = await WebAsset.FetchAsync(url, localPath);

        // 本地文件读不到 / HttpClient 取不到时，退回页面自己 fetch（同源、带 cookie，
        // 相对路径与需要登录的图都能取到），把字节以 data URL 交回来。
        if (bytes is null || bytes.Length == 0)
            bytes = await FetchViaPageAsync(tab, url);

        if (bytes is null || bytes.Length == 0)
        {
            _host.Notify("复制图片失败", "取不到图片数据：" + Shorten(url, 70));
            return;
        }

        Bitmap? bmp = null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            bmp = new Bitmap(img);   // 不释放：交给剪贴板后仍可能被引用
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"复制图片解码失败：{ex.Message}");
        }

        if (bmp is null)
        {
            _host.Notify("复制图片失败", "这个格式放不进剪贴板（SVG 等矢量图请用「另存为…」）：" + Shorten(url, 60));
            return;
        }

        var ok = SetClipboardImage(bmp);
        if (!ok)
        {
            _host.Notify("复制图片失败", "剪贴板被别的程序占用了，请再点一次。");
            return;
        }

        _host.Notify("图片已复制", bmp.Width + "×" + bmp.Height + "，可以直接粘贴。");
    }

    /// <summary>
    /// 让页面自己 fetch 一张图并回传 data URL。
    /// 解决三件事：相对 URL、需要 cookie 的图、HttpClient 被拦的本机服务。
    /// </summary>
    private static async Task<byte[]?> FetchViaPageAsync(TabItem? tab, string url)
    {
        var core = tab?.View?.CoreWebView2;
        if (core is null) return null;

        try
        {
            var script =
                "(async function(){try{" +
                "var u=" + System.Text.Json.JsonSerializer.Serialize(url) + ";" +
                "var r=await fetch(u,{credentials:'include',cache:'no-store'});" +
                "if(!r.ok)return 'ERR:http'+r.status;" +
                "var b=await r.blob();" +
                "var d=await new Promise(function(res,rej){var fr=new FileReader();fr.onload=function(){res(fr.result)};fr.onerror=function(){rej(fr.error)};fr.readAsDataURL(b);});" +
                "return 'OK:'+d;}catch(e){return 'ERR:'+(e&&e.message||e);}})()";

            var raw = await core.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw;
            if (text.StartsWith("\""))
                text = System.Text.Json.JsonSerializer.Deserialize<string>(text) ?? "";
            if (!text.StartsWith("OK:", StringComparison.Ordinal)) return null;

            var dataUrl = text[3..];
            var comma = dataUrl.IndexOf(',');
            if (comma <= 0) return null;
            if (!dataUrl[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"页面取图失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>剪贴板偶尔被别的进程占着，重试几次；失败要如实返回 false。</summary>
    private static bool SetClipboardImage(Bitmap bmp)
    {
        for (var i = 0; i < 6; i++)
        {
            try
            {
                Clipboard.SetImage(bmp);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[clipboard] 图片写入第 {i + 1} 次失败：{ex.Message}");
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private async Task SaveAsAsync(string url, string? localPath)
    {
        var bytes = await WebAsset.FetchAsync(url, localPath);
        if (bytes is null)
        {
            _host.Notify("另存为失败", "没能取到文件内容。");
            return;
        }

        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "另存为",
                FileName = WebAsset.FileNameOf(url, localPath),
                Filter = "所有文件 (*.*)|*.*",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;

            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            _host.Notify("已保存", dialog.FileName);
        }
        catch (Exception ex)
        {
            _host.Notify("另存为失败", ex.Message);
        }
    }

    /// <summary>在资源管理器中定位文件（选中它）或打开所在目录。</summary>
    private void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                _host.Notify("找不到文件", path);
        }
        catch (Exception ex)
        {
            _host.Notify("无法打开资源管理器", ex.Message);
        }
    }

    private void OpenWithDefault(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify("打不开", ex.Message);
        }
    }

    /// <summary>审阅卡片菜单要取的字段。</summary>
    private enum ReviewField { PagePath, Url }

    /// <summary>按审阅 id 反查并复制「页面路径 / 页面地址」。</summary>
    private async Task CopyReviewTargetAsync(string reviewId, ReviewField field)
    {
        var entry = await ReviewIndex.LookupAsync(reviewId);
        var value = field == ReviewField.PagePath ? entry.PagePath : entry.Url;
        if (string.IsNullOrWhiteSpace(value))
        {
            _host.Notify("没查到这一项",
                field == ReviewField.PagePath ? "这个审阅页没登记本地文件路径。" : "这个审阅页没登记地址。");
            return;
        }
        CopyText(value, field == ReviewField.PagePath ? "页面路径已复制" : "页面地址已复制");
    }

    /// <summary>在资源管理器里选中审阅页的本地文件。</summary>
    private async Task RevealReviewAsync(string reviewId)
    {
        var entry = await ReviewIndex.LookupAsync(reviewId);
        if (string.IsNullOrWhiteSpace(entry.PagePath))
        {
            _host.Notify("没查到本地文件", "这个审阅页没登记本地文件路径。");
            return;
        }
        if (!File.Exists(entry.PagePath))
        {
            _host.Notify("文件不在了", entry.PagePath);
            return;
        }
        Reveal(entry.PagePath);
    }

    private void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify("打不开浏览器", ex.Message);
        }
    }

    private static void AdjustZoom(TabItem tab, double delta)
    {
        var view = tab.View;
        if (view is null) return;
        view.ZoomFactor = delta == 0
            ? 1.0
            : Math.Clamp(view.ZoomFactor + delta, 0.5, 2.0);
    }

    // ──────────────────────────── 小工具 ────────────────────────────

    private static ToolStripMenuItem Item(string text, Action? action = null)
    {
        var item = new ToolStripMenuItem(text);
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    /// <summary>把菜单项按「组」攒起来，最后组之间插分隔线。</summary>
    private sealed class MenuSections
    {
        private readonly List<List<ToolStripItem>> _sections = new();
        private List<ToolStripItem>? _current;

        public void Add(ToolStripItem item)
        {
            _current ??= new List<ToolStripItem>();
            _current.Add(item);
        }

        public void EndSection()
        {
            if (_current is { Count: > 0 }) _sections.Add(_current);
            _current = null;
        }

        public void Flush(ContextMenuStrip menu)
        {
            EndSection();
            var first = true;
            foreach (var section in _sections)
            {
                if (!first) menu.Items.Add(new ToolStripSeparator());
                foreach (var item in section) menu.Items.Add(item);
                first = false;
            }
        }
    }
}
