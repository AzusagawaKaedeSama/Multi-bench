// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 编辑器分组：一个标签条 + 一个内容容器。
//
// VSCode 的模型里，「组」是基本单位：
//   - 一个组内可以有多���标签，横向排布，共用一个内容区；
//   - 组可以被垂直/水平分裂成两个组（split）；
//   - 组可以被拖到另一个窗口。
// 本类负责组内的事：标签增删切换、标签与 WebView2 控件的绑定、拖放落点判定。

namespace WorkbenchShell;

/// <summary>一个编辑器分组。</summary>
internal sealed class EditorGroup : UserControl
{
    private readonly TabStrip _strip = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly List<TabItem> _tabs = new();

    /// <summary>内容区上方的分隔阴影（VSCode 里 active group 会有高亮边）。</summary>
    private readonly Panel _contentBorder = new()
    {
        Dock = DockStyle.Top,
        Height = 1,
        BackColor = Theme.Line,
    };

    public event EventHandler<TabItem>? TabClosed;
    public event EventHandler<TabItem>? TabActivated;
    public event EventHandler<EditorGroup>? GroupEmpty;
    public event EventHandler<TabItem>? TabDragStarted;
    public event EventHandler<TabItem>? TabDoubleClicked;
    public event EventHandler? TabCreateRequested;

    /// <summary>本组当前激活的标签。</summary>
    public TabItem? ActiveTab { get; private set; }

    /// <summary>本组的标签集合。</summary>
    public IReadOnlyList<TabItem> Tabs => _tabs;

    /// <summary>标签的 WebView2 控件都挂在这个容器里，只有激活的那个可见。</summary>
    public Panel ContentHost => _content;

    /// <summary>所属的宿主（用于跨组/跨窗口操作）。</summary>
    public Workbench Host { get; set; } = null!;

    /// <summary>组在布局树中的位置标记，供分裂逻辑使用。</summary>
    public string GroupId { get; } = Guid.NewGuid().ToString("N")[..8];

    public EditorGroup()
    {
        // 关键：Dock 必须是 None。
        // 分组的位置由 SplitLayout 的布局树手动计算（Bounds），
        // 一旦设成 Fill，WinForms 的布局引擎会强行让它铺满父容器，
        // 手动算出来的矩形就被无视了 —— 表现为多个分组全叠在一起。
        Dock = DockStyle.None;
        BackColor = Theme.Bg;
        Padding = new Padding(0);

        _strip.Dock = DockStyle.Top;
        _strip.TabClosed += (_, tab) => CloseTab(tab);
        _strip.TabActivated += (_, tab) => ActivateTab(tab);
        _strip.TabDragStarted += (_, tab) => BeginDragCore(tab);
        _strip.TabDoubleClicked += (_, tab) => TabDoubleClicked?.Invoke(this, tab);
        _strip.TabCreateRequested += (_, _) =>
        {
            // 双击标签条空白处 = 新建标签。
            // IMPORTANT: 早期这里只发了事件、没人订阅，于是双击什么都不会发生。
            // 现在直接交给宿主：宿主知道「工作台首页」该开哪个地址，
            // 也知道当前组在哪，比让外层再拼一条链路更短。
            if (Host is not null) { Host.NewTab(); return; }
            TabCreateRequested?.Invoke(this, EventArgs.Empty);
        };

        Controls.Add(_content);
        Controls.Add(_contentBorder);
        Controls.Add(_strip);
    }

    /// <summary>标签标题变了，刷新标签条显示。</summary>
    public void RefreshTabTitle(TabItem tab)
    {
        _strip.Invalidate();
    }

    /// <summary>主题切换后刷新本组的配色。</summary>
    internal void RefreshTheme()
    {
        BackColor = Theme.Bg;
        _content.BackColor = Theme.Bg;
        _contentBorder.BackColor = Theme.Line;
        _strip.ApplyMetrics();
        Invalidate();
    }

    // ──────────────────────────── 标签管理 ────────────────────────────

    public void AddTab(TabItem tab)
    {
        tab.Group = this;
        _tabs.Add(tab);
        _strip.Add(tab);

        // 注意：这里**不**负责把 WebView2 控件挂进 _content。
        // 挂载时机很讲究（见 Workbench.OpenTab 的注释）：
        // 控件先挂好、句柄定型之后再建控制器，否则页面会空白或报 0x800700AA。
        // 跨组搬运由 Workbench.MoveTabToGroup 显式处理。

        ActivateTab(tab);
    }

    public void RemoveTab(TabItem tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;

        // 把该标签的 WebView2 从内容区摘掉并释放
        if (tab.View is not null)
        {
            _content.Controls.Remove(tab.View);
            tab.View.Dispose();
            tab.View = null;
        }

        _tabs.RemoveAt(idx);
        _strip.Remove(tab);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = null;
            if (_tabs.Count > 0) ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }

        TabClosed?.Invoke(this, tab);
        if (_tabs.Count == 0) GroupEmpty?.Invoke(this, this);
    }

    public void ActivateTab(TabItem tab)
    {
        if (!_tabs.Contains(tab)) return;

        // 切换显示：只有激活标签的 WebView 可见，其余隐藏（不是销毁，切回来是秒开）
        foreach (var t in _tabs)
        {
            if (t.View is null) continue;
            var visible = ReferenceEquals(t, tab);
            if (t.View.Visible != visible) t.View.Visible = visible;
        }

        ActiveTab = tab;
        _strip.SetActive(tab);
        Host?.OnActiveTabChanged(this, tab);
        TabActivated?.Invoke(this, tab);
    }

    /// <summary>把某个标签的本组 WebView2 交出去（用于迁移到别的组/窗口）。</summary>
    public TabItem? DetachTab(TabItem tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return null;

        if (tab.View is not null)
        {
            _content.Controls.Remove(tab.View);
        }
        _tabs.RemoveAt(idx);
        _strip.Remove(tab);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = null;
            if (_tabs.Count > 0) ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }
        if (_tabs.Count == 0) GroupEmpty?.Invoke(this, this);
        return tab;
    }

    /// <summary>标签被拖到本组时的落点判定。</summary>
    public enum DropZone { None, Left, Right, Top, Bottom, Center }

    /// <summary>
    /// 判断一个屏幕坐标落在本组的哪个分裂区。
    /// 边缘 26 像素算作分裂区，中间是「加入本组」。
    /// </summary>
    public DropZone ZoneAt(Point screenPoint)
    {
        var r = PointToScreen(Bounds.Location);
        var rect = new Rectangle(r, Size);
        if (!rect.Contains(screenPoint)) return DropZone.None;

        var x = screenPoint.X - rect.Left;
        var y = screenPoint.Y - rect.Top;

        const int edge = 26;
        if (x < edge) return DropZone.Left;
        if (x > rect.Width - edge) return DropZone.Right;
        if (y < edge) return DropZone.Top;
        if (y > rect.Height - edge) return DropZone.Bottom;
        return DropZone.Center;
    }

    /// <summary>开始拖动标签（交给宿主处理）。</summary>
    public void Host2BeginDrag(TabItem tab) => Host?.BeginTabDrag(tab);

    /// <summary>真正发起 OLE 拖放。</summary>
    private void BeginDragCore(TabItem tab)
    {
        TabDragStarted?.Invoke(this, tab);
        try
        {
            var data = new DataObject();
            data.SetData(TabItem.DragFormat, tab);
            DoDragDrop(data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"拖动失败：{ex.Message}");
        }
    }

    /// <summary>本组是否接受拖放。</summary>
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(TabItem.DragFormat) == true)
        {
            e.Effect = DragDropEffects.Move;
            _dropZone = ZoneAt(Cursor.Position);
            Invalidate();
        }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data?.GetDataPresent(TabItem.DragFormat) != true) return;
        e.Effect = DragDropEffects.Move;
        var zone = ZoneAt(Cursor.Position);
        if (zone != _dropZone)
        {
            _dropZone = zone;
            Invalidate();
        }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropZone = DropZone.None;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var zone = _dropZone;
        _dropZone = DropZone.None;
        Invalidate();

        if (e.Data?.GetData(TabItem.DragFormat) is not TabItem tab) return;
        Host?.HandleTabDrop(tab, this, zone);
    }

    private DropZone _dropZone = DropZone.None;

    /// <summary>画出拖放时的分裂高亮（VSCode 的边缘高亮条）。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_dropZone == DropZone.None) return;

        var brush = new SolidBrush(Color.FromArgb(90, Theme.Pink));
        switch (_dropZone)
        {
            case DropZone.Left:
                e.Graphics.FillRectangle(brush, 0, 0, 3, Height);
                break;
            case DropZone.Right:
                e.Graphics.FillRectangle(brush, Width - 3, 0, 3, Height);
                break;
            case DropZone.Top:
                e.Graphics.FillRectangle(brush, 0, 0, Width, 3);
                break;
            case DropZone.Bottom:
                e.Graphics.FillRectangle(brush, 0, Height - 3, Width, 3);
                break;
            case DropZone.Center:
            {
                using var pen = new Pen(Theme.Pink, 2f);
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
                break;
            }
        }
        brush.Dispose();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.W))
        {
            if (ActiveTab is not null) CloseTab(ActiveTab);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// 关闭一个标签。走宿主统一入口 —— 标签要从宿主的 _allTabs 里划掉、
    /// 要在最后一个标签关掉时保住根分组，这些只有宿主知道。
    ///
    /// ★ 这是「× 点了没反应」的根因所在：旧代码只 Invoke 了本类的 TabClosed 事件，
    ///   而宿主从来没有订阅它，于是点击变成了空操作。
    /// </summary>
    private void CloseTab(TabItem tab)
    {
        if (Host is not null) { Host.CloseTab(tab); return; }
        RemoveTab(tab);
    }
}
