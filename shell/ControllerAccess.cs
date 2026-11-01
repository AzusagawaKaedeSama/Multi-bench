// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 取 WebView2 的 CoreWebView2Controller。
//
// 为什么需要它：WinForms 的 WebView2 控件**不公开** controller，
// 只在私有字段 `_coreWebView2Controller` 里存着。而栅格化比例
// （RasterizationScale）只挂在 controller 上 —— 它直接决定页面按几倍渲染，
// 不设对就会「渲染分辨率很低、字体发虚」。
//
// 这里按**类型**而不是字段名去找：SDK 升级把字段改名也不会失效。
// 找不到就返回 null，调用方走降级路径，不让整个应用挂掉。

using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkbenchShell;

internal static class ControllerAccess
{
    private static readonly FieldInfo? ControllerField = typeof(WebView2)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(f => f.FieldType == typeof(CoreWebView2Controller));

    internal static CoreWebView2Controller? Find(WebView2 view)
    {
        if (ControllerField is null) return null;
        try { return ControllerField.GetValue(view) as CoreWebView2Controller; }
        catch { return null; }
    }
}
