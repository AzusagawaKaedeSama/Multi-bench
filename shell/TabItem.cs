// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 标签页模型：一个标签 = 一个原生 WebView2 控件 + 它的元数据。
//
// 为什么要自己建模，而不用 WinForms 的 TabControl：
//   1. TabControl 的标签是「文字 + 关闭按钮」，没有图标、没有悬停态、没有溢出滚动，
//      也不支持把标签拖到另一组 —— 这些正是 VSCode 式体验的核心。
//   2. 每个标签背后是一个独立的 WebView2 控件，需要自己管生命周期（创建/挂起/销毁）。
//   3. 需要支持「把标签拖到分组边缘 → 分裂出新组」，TabControl 给不了拖拽语义。

namespace WorkbenchShell;

using Microsoft.Web.WebView2.WinForms;

/// <summary>一个标签页。</summary>
internal sealed class TabItem
{
    /// <summary>标签显示名（取自页面标题，回退到 URL 尾段）。</summary>
    public string Title { get; set; } = "加载中…";

    /// <summary>标签的 URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>该标签的浏览器控件。标签被关闭时必须 Dispose。</summary>
    public WebView2? View { get; set; }

    /// <summary>页面图标（favicon）。取不到就画一个字母圆牌。</summary>
    public Image? Favicon { get; set; }

    /// <summary>是否正在加载（标签上画一个转圈的暗示）。</summary>
    public bool IsLoading { get; set; }

    /// <summary>浏览器是否已被挂起（切走后释放渲染内存，切回来再唤醒）。</summary>
    public bool IsSuspended { get; set; }

    /// <summary>归属的分组。</summary>
    public EditorGroup? Group { get; set; }

    /// <summary>跨窗口拖拽时的临时载荷。</summary>
    public const string DragFormat = "workbench.tab";

    public override string ToString() => $"{Title} ({Url})";
}
