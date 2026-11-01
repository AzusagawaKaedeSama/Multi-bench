// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 壳与页面之间的「呈现契约」。
//
// 三件事：
//
//   1) 导航归壳
//      3900 页面自带一套 .rail 导航（总览 / 实时任务看板 / 创作与审阅 /
//      作品与资产 / Agent / 统一设置）。壳的侧边栏再画一套就是两层重复，
//      用户要点两处、认两套。反过来，壳把导航接管过来、页面那层隐藏，
//      整个窗口只剩一根侧边栏 —— 这才是「单层」。
//
//   2) 主题贯通
//      页面用 CSS 变量配色（--bg / --surface / --ink / --pink …）。
//      壳切主题时若不通知页面，暗色壳 + 亮粉页面会割成两半。
//      这里把 ThemeSet 的颜色翻译成页面变量注入，
//      壳和页面共用一套配色，主题才是真的「整体主题」。
//
//   3) 零改动
//      服务端 hub.cjs 与 index.html 一行不改。全部通过
//      WebView2 的 ExecuteScriptAsync / AddScriptToExecuteOnDocumentCreatedAsync
//      在运行时注入 —— 页面在浏览器里打开时行为不变，只有在壳里才收敛成单层。

namespace WorkbenchShell;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

internal static class ShellBridge
{
    /// <summary>页面导航项（与 index.html 的 data-page 一一对应）。</summary>
    internal static readonly (string Key, string Label, string Icon)[] NavItems =
    {
        ("overview", "总览", "home"),
        ("automations", "实时任务看板", "clock"),
        ("creative", "创作与审阅", "image"),
        ("catalog", "作品与资产", "folder"),
        ("agents", "Agent", "user"),
        ("settings", "统一设置", "sliders"),
    };

    /// <summary>工作台首页地址。</summary>
    internal static string Origin => Workbench.WorkbenchOrigin;

    /// <summary>
    /// 呈现契约只对工作台自己的页面生效。
    ///
    /// 壳是通用宿主：图片门禁确认页（3958 自己的服务）这类页面有自己的暗色设计，
    /// 把壳的浅色主题变量灌进去会把它改成一半亮一半暗 —— 那不是「统一主题」，是破坏。
    /// 下面每个注入脚本开头都先判 origin，别的站点原样呈现。
    /// </summary>
    internal const string OriginGuard = "if (location.origin !== 'http://127.0.0.1:3900') return;";

    // ── 1. 隐藏页面自带的重复导航 ────────────────────────────────

    /// <summary>
    /// 注入样式：隐藏页面的 .rail，并把 main 的左边距归零。
    /// 页面的 .rail 是 position:fixed 宽 230px，main 用 margin-left:230px 让位。
    /// 两处都要改，只藏 rail 会留下一条空白。
    /// </summary>
    internal const string HideRailCss = @"
(function(){
  if (location.origin !== 'http://127.0.0.1:3900') return;
  if (document.getElementById('__wbShellStyle')) return;
  var s = document.createElement('style');
  s.id = '__wbShellStyle';
  s.textContent =
    '.rail{display:none !important}' +
    'main{margin-left:0 !important}' +
    '.toast{left:50% !important}';
  (document.head || document.documentElement).appendChild(s);
})();";

    /// <summary>把样式注入当前文档（页面导航后需要重新注入）。</summary>
    internal static async Task ApplyHideRailAsync(CoreWebView2 core)
    {
        try { await core.ExecuteScriptAsync(HideRailCss); }
        catch { /* 页面还没 ready，下次导航后再来 */ }
    }

    // ── 2. 主题贯通 ──────────────────────────────────────────────

    /// <summary>把一套主题翻译成页面的 CSS 变量注入。</summary>
    internal static async Task ApplyThemeAsync(CoreWebView2 core, ThemeSet theme)
    {
        var script = BuildThemeScript(theme);
        try { await core.ExecuteScriptAsync(script); }
        catch (Exception ex) { Console.WriteLine("[theme] 注入失败: " + ex.Message); }
    }

    /// <summary>读回页面实际生效的变量（取证用，确认注入真的落地了）。</summary>
    internal static async Task<string> ReadAppliedVarsAsync(CoreWebView2 core)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "(function(){var s=getComputedStyle(document.documentElement);" +
                "var e=document.querySelector('.eyebrow');" +
                "return JSON.stringify({pinkVar:s.getPropertyValue('--pink').trim()," +
                "bgVar:s.getPropertyValue('--bg').trim()," +
                "eyebrow:e?getComputedStyle(e).color:''," +
                "inline:document.documentElement.getAttribute('style')||''});})()");
            return raw ?? "";
        }
        catch (Exception ex) { return "READ_FAIL: " + ex.Message; }
    }

    /// <summary>生成注入脚本：覆盖页面的 :root 变量。</summary>
    internal static string BuildThemeScript(ThemeSet theme)
    {
        var vars = new JsonObject
        {
            ["--bg"] = Hex(theme.Bg),
            ["--surface"] = Hex(theme.Surface),
            ["--soft"] = Hex(theme.Soft),
            ["--line"] = Hex(theme.Line),
            ["--ink"] = Hex(theme.Ink),
            ["--muted"] = Hex(theme.Muted),
            ["--pink"] = Hex(theme.Accent),
            ["--pink-soft"] = Hex(theme.AccentSoft),
            ["--green"] = Hex(theme.Success),
            ["--green-soft"] = MixSoft(theme.Success, theme.Surface),
            ["--amber"] = Hex(theme.Warning),
            ["--amber-soft"] = MixSoft(theme.Warning, theme.Surface),
            ["--red"] = Hex(ColorTranslator.FromHtml("#ba4653")),
            ["--shadow"] = theme.IsDark ? "none" : "0 8px 30px #39234005",
        };

        var json = vars.ToJsonString();
        // 用 setProperty(..., 'important')：页面自己有一条 @media(prefers-color-scheme:dark)
        // 里的 :root 规则，普通 inline style 与它同优先级时会输给它，
        // 加上 important 才压得住。
        return $@"
(function(){{
  try {{
    if (location.origin !== 'http://127.0.0.1:3900') return;
    var v = {json};
    var r = document.documentElement.style;
    for (var k in v) {{ r.setProperty(k, v[k], 'important'); }}
    document.body && (document.body.style.background = v['--bg']);
  }} catch(e) {{}}
}})();";
    }

    private static string Hex(Color c) =>
        "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
            + c.G.ToString("x2", CultureInfo.InvariantCulture)
            + c.B.ToString("x2", CultureInfo.InvariantCulture);

    /// <summary>把强调色往面板色方向混一点，得到对应的浅底色。</summary>
    private static string MixSoft(Color accent, Color surface)
    {
        var r = (int)(accent.R * 0.16 + surface.R * 0.84);
        var g = (int)(accent.G * 0.16 + surface.G * 0.84);
        var b = (int)(accent.B * 0.16 + surface.B * 0.84);
        return "#" + r.ToString("x2", CultureInfo.InvariantCulture)
                 + g.ToString("x2", CultureInfo.InvariantCulture)
                 + b.ToString("x2", CultureInfo.InvariantCulture);
    }

    // ── 3. 导航跳转 ──────────────────────────────────────────────

    /// <summary>让页面跳到某个 hash 路由，并同步页面上自己的导航高亮。</summary>
    internal static async Task NavigateAsync(CoreWebView2 core, string key)
    {
        var script = $@"
(function(){{
  try {{
    if (location.origin !== 'http://127.0.0.1:3900') return;
    location.hash = '#{key}';
    // 页面自己会监听 hashchange 并切换高亮；这里再补一刀，
    // 保证壳侧点过去后页面 .rail 的 active 也跟着走（虽然 .rail 已隐藏，
    // 但页面上其他组件会读这个状态）。
    document.querySelectorAll('.nav a[data-page]').forEach(function(a){{
      a.classList.toggle('active', a.getAttribute('data-page') === '{key}');
    }});
  }} catch(e) {{}}
}})();";
        try { await core.ExecuteScriptAsync(script); }
        catch { /* 页面还没 ready */ }
    }
}
