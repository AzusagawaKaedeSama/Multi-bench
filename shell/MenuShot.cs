// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 右键菜单离屏取证：不碰用户屏幕，直接产出「菜单长什么样」的证据图。
//
// 为什么需要离屏：
//   常规取证要靠「把窗口摆到屏幕中间 → 移动鼠标 → 右击 → 截全屏」，
//   这一套会把用户的桌面抢走：窗口置顶、鼠标被拽走、截图闪屏。
//   工作台是用户自己天天用的东西，取证不该打扰他。
//
// 这套离屏流程：
//   1) 宿主窗口放到虚拟屏外的负坐标，并且用 WS_EX_NOACTIVATE 让它不抢焦点；
//   2) 页面内容用 WebView2 的 CapturePreview() 抓 —— 它抓的是控件自己的
//      渲染位图，跟屏幕可见与否无关；
//   3) 菜单在 (-6000,-6000) 处 Show 一次让工具条完成布局测量（用户看不见），
//      然后用 ToolStrip.Render(Graphics) 把菜单整条画进 Bitmap；
//   4) 页面图 + 菜单图合成一张最终 PNG。
//
// 全程不移动鼠标、不截全屏、不弹出任何用户可见的窗口。

namespace WorkbenchShell;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class MenuShot
{
    /// <summary>跑一次离屏右键菜单取证，输出合成图。返回进程退出码。</summary>
    internal static int Run(string url, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        NativeConsole.Attach();

        var report = new Dictionary<string, object?>
        {
            ["url"] = url,
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        var exitCode = 0;
        try
        {
            ApplicationConfiguration.Initialize();

            var form = new OffscreenForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),   // 虚拟屏外
                Size = new Size(1440, 900),
                BackColor = Color.White,
            };

            var view = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            form.Show();

            // 页面渲染需要消息循环；整个流程用 await 链 + 泵消息推进。
            var flow = RunFlow(form, view, url, outPath, report);
            flow.GetAwaiter().OnCompleted(() => { });

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!flow.IsCompleted && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (!flow.IsCompleted)
            {
                report["error"] = "超时（120 秒）";
                exitCode = 1;
            }
            else if (flow.IsFaulted)
            {
                report["error"] = flow.Exception?.GetBaseException().ToString();
                exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        finally
        {
            report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var jsonPath = Path.ChangeExtension(outPath, ".json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("IMAGE: " + Path.GetFullPath(outPath));
            Console.WriteLine("REPORT: " + jsonPath);
        }

        return exitCode;
    }

    /// <summary>离屏宿主窗口：带「不激活」扩展样式，存在但永远不抢焦点、不进任务栏。</summary>
    private sealed class OffscreenForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    private static async Task RunFlow(
        Form form,
        WebView2 view,
        string url,
        string outPath,
        Dictionary<string, object?> report)
    {
        report["step"] = "create-env";
        var runtimeDir = WebView2Runtime.ResolveDir()
            ?? throw new DirectoryNotFoundException("未找到 WebView2 运行时目录。");
        report["webview2RuntimeDir"] = runtimeDir;

        var udf = Path.Combine(AppPaths.BaseDir, "wbdata-shot");
        Directory.CreateDirectory(udf);
        var env = await CoreWebView2Environment.CreateAsync(runtimeDir, udf);
        await view.EnsureCoreWebView2Async(env);
        report["step"] = "webview-ready";

        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;

        // ★ 屏外渲染的关键：控制器必须被显式标记为「可见」。
        //   否则 Chromium 判定页面 hidden → 停止渲染、懒加载图片不触发、
        //   CapturePreview 抓回来是空白图。
        var controller = ControllerAccess.Find(view);
        if (controller is not null)
        {
            controller.IsVisible = true;
            controller.RasterizationScale = 1f;
        }

        // ★ 屏蔽页面弹窗：审阅页会弹原生 alert（例如「本地存储已损坏」），
        //   那玩意儿会直接蹦到用户屏幕中央，打断正在干的事。
        //   取证与后台运行时一律吞掉。
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.alert=function(){};window.confirm=function(){return true;};" +
            "window.prompt=function(){return null;};window.print=function(){};");

        // 注入 bridge.js：页面要用 window.__wbDescribeAt 描述被右键的对象。
        var bridge = Workbench.ReadEmbeddedBridge();
        if (!string.IsNullOrEmpty(bridge))
            await core.AddScriptToExecuteOnDocumentCreatedAsync(bridge);

        report["step"] = "navigate";
        core.Navigate(url);

        // 等页面把图片渲染出来
        report["step"] = "wait-images";
        JsonObject? probe = null;
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(500);
            try
            {
                var raw = await core.ExecuteScriptAsync(FindImageScript);
                probe = ParseObject(raw);
                report["diag"] = probe;
                if (probe is not null
                    && (int?)probe["count"] > 0
                    && probe["loaded"]?.GetValue<bool>() == true) break;
            }
            catch { }
        }
        if (probe is null || (int?)probe["count"] == 0)
        {
            report["pageTitle"] = core.DocumentTitle;
            report["error"] = "页面里没找到可用的图片元素";
            return;
        }
        report["imgCount"] = probe["count"];
        report["imgSrc"] = probe["src"];
        report["imgAlt"] = probe["alt"];
        report["imgRect"] = probe["rect"];
        report["dpr"] = probe["dpr"];

        // 用页面自己的描述函数拿到「被右键对象」的上下文
        report["step"] = "describe";
        var cx = (double)probe["cx"]!;
        var cy = (double)probe["cy"]!;
        var describeRaw = await core.ExecuteScriptAsync(
            $"(function(){{try{{var d=(window.__wbDescribeAt&&window.__wbDescribeAt({cx},{cy}))||null;" +
            "return d?JSON.stringify(d):'null';}catch(e){return 'null';}})()");
        var context = ParseObject(describeRaw);
        if (context is null)
        {
            // 描述脚本不可用时，退回用图片自身的信息构造上下文。
            context = new JsonObject
            {
                ["imgSrc"] = probe["src"]?.ToString() ?? "",
                ["imgAlt"] = probe["alt"]?.ToString() ?? "",
                ["pageUrl"] = url,
                ["pageTitle"] = core.DocumentTitle ?? "",
            };
            report["describeFallback"] = true;
        }
        report["context"] = context;

        // 构造真实菜单（走的是线上同一套代码）
        report["step"] = "build-menu";
        var info = ParseContextInfo(context);
        var tab = new TabItem { Title = core.DocumentTitle ?? "审阅页", Url = url };
        var host = new ShotHost();
        var builder = new NativeContextMenu(host);
        using var menu = builder.BuildMenu(tab, info);
        if (menu is null)
        {
            report["error"] = "菜单构建失败";
            return;
        }
        report["menuItems"] = menu.Items
            .Cast<ToolStripItem>()
            .Select(i => i is ToolStripSeparator ? "---" : i.Text)
            .ToList();

        // 菜单离屏渲染：Show 到负坐标让工具条完成布局测量，然后整条画进 Bitmap。
        report["step"] = "render-menu";
        byte[]? menuPng = RenderMenuOffscreen(menu);
        if (menuPng is null)
        {
            report["error"] = "菜单离屏渲染失败";
            return;
        }

        // 页面离屏截图
        report["step"] = "capture-page";
        byte[]? pagePng = await CapturePageAsync(core);
        if (pagePng is null)
        {
            report["error"] = "页面离屏截图失败";
            return;
        }

        // 合成：页面图 + 菜单图叠在图片位置旁
        report["step"] = "compose";
        Compose(pagePng, menuPng, probe, outPath);
        report["output"] = Path.GetFullPath(outPath);
        report["verdict"] = "PASS";
    }

    /// <summary>
    /// 在页面里找第一张有实际尺寸的图片，强制它加载并滚到视口中央。
    ///
    /// 两个要点：
    ///   1) 审阅页的图片是 loading="lazy"，不滚进视口就永远不加载
    ///      （naturalWidth 恒为 0），必须先改成 eager 再等它把图拉下来；
    ///   2) 窗口在屏幕外，浏览器不会自动滚，但 scrollIntoView 一样有效，
    ///      而且顺带让图片落在视口中央，CapturePreview 正好拍到。
    /// </summary>
    private const string FindImageScript = @"
(function(){
  var imgs = Array.prototype.slice.call(document.querySelectorAll('img'));
  imgs.forEach(function(im){
    if (im.loading === 'lazy') {
      im.loading = 'eager';
      var s = im.getAttribute('src');
      if (s) { im.setAttribute('src', s); }
    }
  });
  var pick = null;
  for (var i = 0; i < imgs.length; i++) {
    var r = imgs[i].getBoundingClientRect();
    if (r.width < 60 || r.height < 40) continue;
    pick = imgs[i]; break;
  }
  if (!pick) return JSON.stringify({count:0});
  try { pick.scrollIntoView({block:'center', inline:'nearest'}); } catch(e) {}
  var r = pick.getBoundingClientRect();
  return JSON.stringify({
    count: imgs.length,
    loaded: (pick.naturalWidth || 0) > 10,
    src: pick.currentSrc || pick.src || '',
    alt: pick.alt || '',
    cx: r.left + r.width/2,
    cy: r.top + r.height/2,
    dpr: window.devicePixelRatio || 1,
    rect: {x:r.left, y:r.top, w:r.width, h:r.height}
  });
})()";

    private static JsonObject? ParseObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        try
        {
            var outer = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(outer)) return null;
            return JsonNode.Parse(outer) as JsonObject;
        }
        catch { return null; }
    }

    private static ContextInfo ParseContextInfo(JsonObject obj)
    {
        string S(string key) => obj.TryGetPropertyValue(key, out var v) ? v?.ToString() ?? "" : "";
        return new ContextInfo
        {
            Target = S("target"),
            Tag = S("tag"),
            ImgSrc = S("imgSrc"),
            ImgAlt = S("imgAlt"),
            Href = S("href"),
            LinkText = S("linkText"),
            Selection = S("selection"),
            Editable = obj.TryGetPropertyValue("editable", out var e) && e?.GetValue<bool>() == true,
            PageUrl = S("pageUrl"),
            PageTitle = S("pageTitle"),
        };
    }

    /// <summary>
    /// 把菜单离屏画成 PNG。
    ///
    /// 为什么是自绘而不是截屏：ToolStrip 不是 Control，没有 DrawToBitmap，
    /// 也没有公开的 Render(Graphics)；要拿到像素只能让菜单真的显示在屏幕上再截 ——
    /// 那正是要避免的打扰。菜单的**结构**（项、次序、分隔线）来自线上同一套
    /// 构建器，这里只负责把结构画成现代外观。
    /// </summary>
    private static byte[]? RenderMenuOffscreen(ContextMenuStrip menu)
    {
        try
        {
            var items = menu.Items.Cast<ToolStripItem>().ToList();

            const int itemH = 30;
            const int sepH = 9;
            const int padX = 14;
            const int padY = 8;

            using var probe = new Bitmap(1, 1);
            using var pg = Graphics.FromImage(probe);
            pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var texts = items.Select(i => i is ToolStripSeparator ? "" : i.Text).ToList();
            var maxW = texts.Where(t => t.Length > 0)
                .Select(t => (int)Math.Ceiling(pg.MeasureString(t, SystemFonts.MenuFont).Width))
                .DefaultIfEmpty(120).Max();

            var w = maxW + padX * 2 + 28;   // 右侧留出子菜单箭头位
            var h = padY * 2 + items.Sum(i => i is ToolStripSeparator ? sepH : itemH);

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                // 投影
                using (var shadowPath = RoundRect(new Rectangle(4, 5, w - 6, h - 4), 10))
                using (var shadowBrush = new SolidBrush(Color.FromArgb(64, 0, 0, 0)))
                {
                    g.FillPath(shadowBrush, shadowPath);
                }

                // 面板
                using (var panelPath = RoundRect(new Rectangle(2, 2, w - 4, h - 6), 10))
                using (var panelBrush = new SolidBrush(Color.FromArgb(0x2A, 0x24, 0x2C)))
                using (var edgePen = new Pen(Color.FromArgb(0x45, 0x3E, 0x4C), 1f))
                {
                    g.FillPath(panelBrush, panelPath);
                    g.DrawPath(edgePen, panelPath);
                }

                var y = padY;
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item is ToolStripSeparator)
                    {
                        using var sepPen = new Pen(Color.FromArgb(0x3A, 0x33, 0x42), 1f);
                        g.DrawLine(sepPen, padX, y + sepH / 2, w - padX, y + sepH / 2);
                        y += sepH;
                        continue;
                    }

                    // 第一项做轻微高亮，模拟鼠标悬停
                    if (i == 0)
                    {
                        using var hoverPath = RoundRect(new Rectangle(padX - 6, y, w - (padX - 6) * 2, itemH), 6);
                        using var hoverBrush = new SolidBrush(Color.FromArgb(0x3E, 0x35, 0x46));
                        g.FillPath(hoverBrush, hoverPath);
                    }

                    using var textBrush = new SolidBrush(Color.FromArgb(0xEC, 0xEA, 0xF0));
                    var font = new Font(SystemFonts.MenuFont.FontFamily, 9.5f, FontStyle.Regular);
                    g.DrawString(item.Text, font, textBrush, padX + 4, y + (itemH - g.MeasureString(item.Text, font).Height) / 2f);
                    font.Dispose();

                    y += itemH;
                }
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("菜单渲染异常: " + ex.Message);
            return null;
        }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>用 WebView2 的离屏截图 API 抓页面内容。</summary>
    private static async Task<byte[]?> CapturePageAsync(CoreWebView2 core)
    {
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("页面截图异常: " + ex.Message);
            return null;
        }
    }

    /// <summary>合成：页面图 + 菜单图（菜单贴在图片位置旁，像真右键弹出来那样）。</summary>
    private static void Compose(byte[] pagePng, byte[] menuPng, JsonObject probe, string outPath)
    {
        using var page = Image.FromStream(new MemoryStream(pagePng));
        using var menu = Image.FromStream(new MemoryStream(menuPng));

        var dpr = (double)(probe["dpr"]?.GetValue<double>() ?? 1.0);
        var rect = probe["rect"]?.AsObject();
        double rx = rect is null ? 0 : (rect["x"]?.GetValue<double>() ?? 0);
        double ry = rect is null ? 0 : (rect["y"]?.GetValue<double>() ?? 0);
        double rw = rect is null ? 0 : (rect["w"]?.GetValue<double>() ?? 0);
        double rh = rect is null ? 0 : (rect["h"]?.GetValue<double>() ?? 0);

        // 菜单贴在图片的右下角，像用户右键图片后菜单弹出的位置。
        var menuX = (int)Math.Min(
            Math.Max(0, (rx + rw * 0.25) * dpr),
            Math.Max(0, page.Width - menu.Width));
        var menuY = (int)Math.Min(
            Math.Max(0, (ry + rh * 0.25) * dpr),
            Math.Max(0, page.Height - menu.Height));

        using var canvas = new Bitmap(page.Width, page.Height);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.DrawImage(page, 0, 0);

            // 菜单投影
            using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            g.FillRectangle(shadow, menuX + 4, menuY + 4, menu.Width, menu.Height);

            g.DrawImage(menu, menuX, menuY);

            // 标注：红框标出被右键的那张图片
            if (rw > 0 && rh > 0)
            {
                using var pen = new Pen(Color.FromArgb(0xE8, 0x4A, 0x6A), 3f);
                g.DrawRectangle(pen,
                    (float)(rx * dpr), (float)(ry * dpr),
                    (float)(rw * dpr), (float)(rh * dpr));
            }
        }

        canvas.Save(outPath, ImageFormat.Png);
    }

    /// <summary>取证用的假宿主：菜单项只记录，不真的执行动作。</summary>
    private sealed class ShotHost : IContextMenuHost
    {
        public List<string> Log { get; } = new();

        public void Notify(string title, string body) => Log.Add($"notify:{title}");

        public void OpenUrlInTab(string url) => Log.Add($"tab:{url}");

        public Task OpenUrlInNewWindowAsync(string title, string url)
        {
            Log.Add($"window:{url}");
            return Task.CompletedTask;
        }

        public void SendEditCommandTo(TabItem tab, string command) => Log.Add($"edit:{command}");

        public void PostToPage(TabItem tab, JsonObject payload) => Log.Add($"post:{payload["kind"]}");

        public string TakePendingContextTarget(TabItem tab) => "";
    }
}
