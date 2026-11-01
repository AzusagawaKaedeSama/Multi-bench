// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 右键菜单自检：把「右键图片应该出现哪些项」变成可断言的东西。
//
// 为什么必须自检，不能靠肉眼看菜单：
//   右键菜单的「规则」不是画出来的，是一串条件分支：
//     有图 → 出图片项；图能映射到本地 → 才出「在资源管理器中显示」；
//     有链接 → 出链接项；没本地路径 → 绝不出「在资源管理器中显示」。
//   肉眼只能证明「这次看着对」，证明不了「规则没写错」。
//   所以这里直接断言菜单项的**有无**——尤其是那几个「不该出现」的。
//
// 验证三件事：
//   1) URL → 本地路径的换算规则（审阅服务的 /<盘符>/<路径> 映射）；
//   2) 三种上下文（本地图片 / 远程图片 / 本地链接 / 纯页面）各自的菜单结构；
//   3) 页面 bridge.js 产出的描述 JSON 能被正确解析（字段名对得上）。

using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkbenchShell;

internal static class MenuSelfTest
{
    /// <summary>跑一次右键菜单自检，返回进程退出码。</summary>
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 控制台不支持就算了 */ }
        Console.WriteLine("[menutest] 启动");

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        var exitCode = 0;
        try
        {
            var host = new StubHost();
            var builder = new NativeContextMenu(host);
            var tab = new TabItem { Url = "http://127.0.0.1:3955/D/2-视频制作/x.html", Title = "测试" };

            // ── ① URL → 本地路径 ──
            var mapCases = new (string Url, string? Expected, string Name)[]
            {
                ("http://127.0.0.1:3955/D/2-视频制作/x.jpg", @"D:\2-视频制作\x.jpg", "审阅服务D盘"),
                ("http://127.0.0.1:3955/E/AgentData/a.png", @"E:\AgentData\a.png", "审阅服务E盘"),
                ("http://127.0.0.1:3955/C/Windows/x.jpg", null, "非授权盘符应拒绝"),
                ("http://127.0.0.1:3900/#results", null, "工作台自身不是文件"),
                ("https://example.com/a.jpg", null, "外网不映射本地"),
                ("file:///D:/2-视频制作/x.jpg", @"D:\2-视频制作\x.jpg", "file协议"),
            };
            var mapResults = new List<object>();
            foreach (var (url, expected, name) in mapCases)
            {
                var actual = WebAsset.MapToLocalPath(url);
                var ok = expected is null ? actual is null : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                mapResults.Add(new { name, url, expected, actual, ok });
                if (!ok) { report["error"] = $"路径换算不符：{name} 期望 {expected} 实际 {actual}"; exitCode = 1; }
            }
            report["pathMapping"] = mapResults;

            // 存在的文件才应该被认成「本地文件」
            var realLocal = WebAsset.ToLocalPath(LocalImageUrl);
            report["existingLocalResolved"] = realLocal;
            if (realLocal is null || !File.Exists(realLocal))
            {
                report["error"] = "存在的本地图片没被认出来";
                exitCode = 1;
            }

            // ── ② 菜单结构 ──
            var imageLocal = builder.BuildMenu(tab, new ContextInfo
            {
                ImgSrc = LocalImageUrl,
                ImgAlt = "U0001",
                ImgWidth = 800,
                ImgHeight = 600,
                PageUrl = "http://127.0.0.1:3955/D/2-视频制作/x.html",
            });
            var imageRemote = builder.BuildMenu(tab, new ContextInfo
            {
                ImgSrc = "https://example.com/remote.png",
                PageUrl = "https://example.com/page.html",
            });
            var linkLocal = builder.BuildMenu(tab, new ContextInfo
            {
                Href = LocalPageUrl,
                LinkText = "初华截图并集审阅",
                PageUrl = "http://127.0.0.1:3900/#creative",
            });
            var plainPage = builder.BuildMenu(tab, new ContextInfo
            {
                PageUrl = "http://127.0.0.1:3900/#results",
            });

            var imageLocalItems = Flatten(imageLocal);
            var imageRemoteItems = Flatten(imageRemote);
            var linkLocalItems = Flatten(linkLocal);
            var plainPageItems = Flatten(plainPage);

            report["menu_localImage"] = imageLocalItems;
            report["menu_remoteImage"] = imageRemoteItems;
            report["menu_localLink"] = linkLocalItems;
            report["menu_plainPage"] = plainPageItems;

            // 本地图片：应该有复制、地址、资源管理器、本地路径、另存为
            Expect(imageLocalItems, "打开图片");
            Expect(imageLocalItems, "复制图片");
            Expect(imageLocalItems, "复制图片地址");
            Expect(imageLocalItems, "在资源管理器中显示");
            Expect(imageLocalItems, "复制本地路径");
            Expect(imageLocalItems, "另存为…");
            Expect(imageLocalItems, "在浏览器中打开");

            // 远程图片：不该出现本地专属项（出现「点了没反应」的项更糟）
            Expect(imageRemoteItems, "复制图片");
            Expect(imageRemoteItems, "复制图片地址");
            Reject(imageRemoteItems, "在资源管理器中显示");
            Reject(imageRemoteItems, "复制本地路径");
            Reject(imageRemoteItems, "使用默认程序打开");

            // 本地链接：链接项齐全，且能定位到文件
            Expect(linkLocalItems, "在新标签页中打开");
            Expect(linkLocalItems, "在新窗口中打开");
            Expect(linkLocalItems, "复制链接地址");
            Expect(linkLocalItems, "复制链接文字");
            Expect(linkLocalItems, "在资源管理器中显示");

            // 纯页面：不该冒出图片项和链接项
            Expect(plainPageItems, "复制页面地址");
            Expect(plainPageItems, "重新载入");
            Expect(plainPageItems, "缩放");
            Reject(plainPageItems, "复制图片");
            Reject(plainPageItems, "在新标签页中打开");

            // ── ③ 页面描述 JSON 能被解析 ──
            const string sampleJson = """
                {"target":"review:abc","tag":"IMG","x":10,"y":20,"imgSrc":"http://127.0.0.1:3955/D/a.jpg",
                 "imgAlt":"封面","imgW":100,"imgH":80,"href":"http://127.0.0.1:3955/D/b.html",
                 "linkText":"详情","selection":"选中的字","editable":false,
                 "pageUrl":"http://127.0.0.1:3955/D/c.html","pageTitle":"审阅"}
                """;
            var parsed = JsonSerializer.Deserialize<ContextInfo>(sampleJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            report["parsedTarget"] = parsed?.Target;
            report["parsedImgSrc"] = parsed?.ImgSrc;
            report["parsedLinkText"] = parsed?.LinkText;
            if (parsed is null || parsed.Target != "review:abc" || parsed.ImgAlt != "封面"
                || parsed.LinkText != "详情" || !parsed.HasImage || !parsed.HasLink)
            {
                report["error"] = "页面描述 JSON 解析结果不对";
                exitCode = 1;
            }

            // 业务上下文：task: 前缀要出审批项
            var taskMenu = builder.BuildMenu(tab, new ContextInfo { Target = "task:t-001-approve" });
            var taskItems = Flatten(taskMenu);
            report["menu_taskContext"] = taskItems;
            Expect(taskItems, "批准通过");
            Expect(taskItems, "驳回");
            Expect(taskItems, "打开源文件");

            imageLocal?.Dispose();
            imageRemote?.Dispose();
            linkLocal?.Dispose();
            plainPage?.Dispose();
            taskMenu?.Dispose();

            report["verdict"] = exitCode == 0 ? "PASS" : "FAIL";
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }

        report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var jsonPath = Path.Combine(outputDir, "workbench-shell-menutest.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine("REPORT: " + jsonPath);
        return exitCode;
    }

    // 本机审阅服务上的一张真实素材，用来验证「能映射到本地」这条路真的通。
    private const string LocalImageUrl =
        "http://127.0.0.1:3955/D/2-%E8%A7%86%E9%A2%91%E5%88%B6%E4%BD%9C/" +
        "0-%E4%B8%B4%E6%97%B6inbox/261020-%E5%88%9D%E5%8D%8E%E6%88%AA%E5%9B%BE%E5%B9%B6%E9%9B%86%E5%AE%A1%E9%98%85/" +
        "C-%E4%B8%AD%E9%97%B4%E8%BF%87%E7%A8%8B/%E9%98%B6%E6%AE%B51_%E5%B9%B6%E9%9B%86%E4%B8%8E%E5%AE%A1%E9%98%85%E9%A1%B5/" +
        "%E7%BC%A9%E7%95%A5%E5%9B%BE/U0001.jpg";

    private const string LocalPageUrl =
        "http://127.0.0.1:3955/D/2-%E8%A7%86%E9%A2%91%E5%88%B6%E4%BD%9C/" +
        "0-%E4%B8%B4%E6%97%B6inbox/261020-%E5%88%9D%E5%8D%8E%E6%88%AA%E5%9B%BE%E5%B9%B6%E9%9B%86%E5%AE%A1%E9%98%85/" +
        "C-%E4%B8%AD%E9%97%B4%E8%BF%87%E7%A8%8B/%E9%98%B6%E6%AE%B51_%E5%B9%B6%E9%9B%86%E4%B8%8E%E5%AE%A1%E9%98%85%E9%A1%B5/" +
        "%E5%88%9D%E5%8D%8E%E6%88%AA%E5%9B%BE%E5%B9%B6%E9%9B%86%E5%AE%A1%E9%98%85.html";

    /// <summary>把菜单摊平成文本列表（分隔线记成 "---"）。</summary>
    private static List<string> Flatten(ToolStrip? menu)
    {
        var list = new List<string>();
        if (menu is null) return list;
        foreach (ToolStripItem item in menu.Items) list.AddRange(FlattenItem(item));
        return list;
    }

    private static List<string> FlattenItem(ToolStripItem item)
    {
        var list = new List<string>();
        if (item is ToolStripMenuItem menu)
        {
            list.Add(menu.Text);
            foreach (ToolStripItem child in menu.DropDownItems) list.AddRange(FlattenItem(child));
        }
        else
        {
            list.Add("---");
        }
        return list;
    }

    private static void Expect(List<string> items, string text)
    {
        if (!items.Contains(text))
            throw new InvalidOperationException($"菜单里应该有「{text}」，实际是：{string.Join(" / ", items)}");
    }

    private static void Reject(List<string> items, string text)
    {
        if (items.Contains(text))
            throw new InvalidOperationException($"菜单里不该有「{text}」，实际是：{string.Join(" / ", items)}");
    }

    /// <summary>不碰真窗口的假宿主。</summary>
    private sealed class StubHost : IContextMenuHost
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
