// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 自绘标签栏：VSCode 风格的标签条。
//
// 为什么不用 TabControl：
//   - TabControl 的标签是系统控件，样式锁死，改不出 VSCode 那种扁平、紧凑、
//     选中态带顶部色条的观感；
//   - TabControl 没有 favicon、没有悬停才出现的关闭按钮、没有溢出滚动；
//   - 拖拽分裂（把标签拖到分组边缘分出新组）需要自定义命中区域，TabControl 给不了。
//
// 交互约定：
//   - 单击：切换标签
//   - 单击关闭按钮 ×：关闭该标签
//   - 双击标签空白处：新建标签
//   - 拖动标签：可在同组内重排；拖到分组边缘触发分裂；拖出窗口触发「新窗口打开」
//   - 鼠标中键：关闭标签（VSCode 习惯）

namespace WorkbenchShell;

/// <summary>自绘的标签条控件。</summary>
internal sealed class TabStrip : Control
{
    private readonly List<TabItem> _tabs = new();
    private int _hoverIndex = -1;
    private int _closeHoverIndex = -1;
    private int _scrollOffset;

    // 拖拽状态
    private Point _dragStart;
    private bool _dragArmed;
    private TabItem? _dragItem;

    // 事件
    public event EventHandler<TabItem>? TabActivated;
    public event EventHandler<TabItem>? TabClosed;
    public event EventHandler? TabCreateRequested;
    public event EventHandler<TabItem>? TabDragStarted;
    public event EventHandler<TabItem>? TabDoubleClicked;

    public IReadOnlyList<TabItem> Tabs => _tabs;
    public TabItem? ActiveTab { get; private set; }

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.TabStripHeight;
        BackColor = Theme.TabIdle;
        Font = Theme.TabFont;
    }

    /// <summary>设置里改了标签条高度后重设尺寸与配色。</summary>
    internal void ApplyMetrics()
    {
        Height = Theme.TabStripHeight;
        BackColor = Theme.TabIdle;
        Font = Theme.TabFont;
        Invalidate();
    }

    // ──────────────────────────── 标签集合 ────────────────────────────

    public void Add(TabItem tab)
    {
        _tabs.Add(tab);
        Invalidate();
    }

    public void Remove(TabItem tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        _tabs.RemoveAt(idx);
        if (ActiveTab == tab) ActiveTab = _tabs.Count > 0 ? _tabs[Math.Min(idx, _tabs.Count - 1)] : null;
        Invalidate();
    }

    public void SetActive(TabItem? tab)
    {
        ActiveTab = tab;
        Invalidate();
    }

    public void Clear()
    {
        _tabs.Clear();
        ActiveTab = null;
        Invalidate();
    }

    /// <summary>把标签移动到新位置（同组内重排）。</summary>
    public void Reorder(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _tabs.Count) return;
        var item = _tabs[fromIndex];
        _tabs.RemoveAt(fromIndex);
        toIndex = Math.Clamp(toIndex, 0, _tabs.Count);
        _tabs.Insert(toIndex, item);
        Invalidate();
    }

    // ──────────────────────────── 绘制 ────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 标签条是纯不透明底，用 ClearType 才能和系统其它界面一样锐利。
        // 不设这一行时 GDI+ 走灰度抗锯齿，中文小字会发虚、笔画边上一圈脏边 ——
        // 用户看到的「很强的锯齿」就是它。
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.TabIdle);

        // 标签条底部的分隔线
        using (var pen = new Pen(Theme.Line))
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        if (_tabs.Count == 0)
        {
            DrawEmptyHint(g);
            return;
        }

        var x = -_scrollOffset;
        var font = Theme.TabFont;
        var fontActive = Theme.TabFontActive;

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var isActive = ReferenceEquals(tab, ActiveTab);
            var isHover = i == _hoverIndex;

            int width = MeasureTabWidth(g, tab, font);
            var rect = new Rectangle(x, 0, width, Height - 1);

            // 底色
            using (var brush = new SolidBrush(isActive ? Theme.TabActive : isHover ? Theme.TabHover : Theme.TabIdle))
                g.FillRectangle(brush, rect);

            // 选中态：顶部 2px 粉色条（VSCode 的 active tab 顶部色条）
            if (isActive)
            {
                using var brush = new SolidBrush(Theme.Pink);
                g.FillRectangle(brush, new Rectangle(rect.X, 0, rect.Width, 2));
            }

            // 图标
            var iconBox = new Rectangle(rect.X + Theme.TabPaddingLeft, (Height - 1 - Theme.TabIconSize) / 2,
                                        Theme.TabIconSize, Theme.TabIconSize);
            DrawIcon(g, tab, iconBox);

            // 标题
            var textLeft = iconBox.Right + 6;
            var closeW = Theme.TabCloseSize + 4;
            var closeLeft = rect.Right - Theme.TabPaddingRight - Theme.TabCloseSize;
            var textRight = closeLeft - 4;

            var textFont = isActive ? fontActive : font;
            var textRect = new Rectangle(textLeft, 0, Math.Max(0, textRight - textLeft), Height - 1);
            // 用 GDI（TextRenderer）画标题：它走系统的 ClearType 栅格化，
            // 小字号中文比 Graphics.DrawString 锐利一档，省略号也是系统自带的。
            TextRenderer.DrawText(g, tab.Title, textFont, textRect,
                isActive ? Theme.Ink : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine);

            // 关闭按钮：选中态或悬停态才画
            if (isActive || isHover)
            {
                var closeBox = new Rectangle(closeLeft, (Height - 1 - Theme.TabCloseSize) / 2,
                                            Theme.TabCloseSize, Theme.TabCloseSize);
                var closeHover = i == _closeHoverIndex;
                using var cb = new SolidBrush(closeHover ? Theme.PinkSoft : Color.Transparent);
                g.FillEllipse(cb, closeBox);
                DrawCloseGlyph(g, closeBox, closeHover ? Theme.Pink : Theme.Muted);
            }

            // 标签之间的细缝
            using (var pen = new Pen(Theme.Line))
                g.DrawLine(pen, rect.Right - 1, 4, rect.Right - 1, Height - 5);

            x += width;
        }
    }

    private void DrawEmptyHint(Graphics g)
    {
        TextRenderer.DrawText(g, "双击这里新建标签", Theme.TabFont,
            new Rectangle(12, 0, 240, Height - 1), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawIcon(Graphics g, TabItem tab, Rectangle box)
    {
        if (tab.Favicon is not null)
        {
            g.DrawImage(tab.Favicon, box);
            return;
        }

        // 没有 favicon：画一个带首字母的圆牌（取域名首字母，辨识度够用）
        var letter = "·";
        try
        {
            var host = new Uri(tab.Url).Host;
            letter = string.IsNullOrEmpty(host) ? "·" : host[0].ToString().ToUpperInvariant();
        }
        catch { }

        using var brush = new SolidBrush(Theme.PinkSoft);
        g.FillEllipse(brush, box);
        using var pen = new Pen(Theme.Pink, 1.2f);
        g.DrawEllipse(pen, box);
        using var font = new Font("Microsoft YaHei UI", 6.5f, FontStyle.Bold);
        using var tb = new SolidBrush(Theme.Pink);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter, font, tb, box, fmt);
    }

    private static void DrawCloseGlyph(Graphics g, Rectangle box, Color color)
    {
        using var pen = new Pen(color, 1.4f);
        var pad = 3.5f;
        g.DrawLine(pen, box.X + pad, box.Y + pad, box.Right - pad, box.Bottom - pad);
        g.DrawLine(pen, box.Right - pad, box.Y + pad, box.X + pad, box.Bottom - pad);
    }

    private int MeasureTabWidth(Graphics g, TabItem tab, Font font)
    {
        var textW = MeasureText(g, tab.Title, font);
        var w = Theme.TabPaddingLeft + Theme.TabIconSize + 6 + textW + 4 + Theme.TabCloseSize + Theme.TabPaddingRight;
        return Math.Clamp(w, Theme.TabMinWidth, Theme.TabMaxWidth);
    }

    private static int MeasureText(Graphics g, string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // 与绘制用同一套度量（GDI TextRenderer），否则算出来的标签宽度
        // 和实际画出来的文字对不上，关闭按钮会盖住最后一个字。
        return TextRenderer.MeasureText(g, text, font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    // ──────────────────────────── 命中测试 ────────────────────────────

    private int IndexAt(Point p)
    {
        var x = -_scrollOffset;
        using var g = CreateGraphics();
        var font = Theme.TabFont;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var w = MeasureTabWidth(g, _tabs[i], font);
            if (p.X >= x && p.X < x + w) return i;
            x += w;
        }
        return -1;
    }

    private bool IsOnCloseButton(Point p, int index)
    {
        if (index < 0) return false;
        var x = -_scrollOffset;
        using var g = CreateGraphics();
        var font = Theme.TabFont;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var w = MeasureTabWidth(g, _tabs[i], font);
            if (i == index)
            {
                var closeLeft = x + w - Theme.TabPaddingRight - Theme.TabCloseSize;
                var box = new Rectangle(closeLeft, (Height - Theme.TabCloseSize) / 2, Theme.TabCloseSize, Theme.TabCloseSize);
                return box.Contains(p);
            }
            x += w;
        }
        return false;
    }

    // ──────────────────────────── 鼠标交互 ────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var idx = IndexAt(e.Location);
        var onClose = IsOnCloseButton(e.Location, idx);

        if (idx != _hoverIndex || onClose != (_closeHoverIndex >= 0))
        {
            _hoverIndex = idx;
            _closeHoverIndex = onClose ? idx : -1;
            Invalidate();
        }
        Cursor = onClose ? Cursors.Hand : Cursors.Default;

        // 拖拽启动：移动超过 4px 就算拖
        if (_dragArmed && _dragItem is not null)
        {
            var d = new Point(e.X - _dragStart.X, e.Y - _dragStart.Y);
            if (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4)
            {
                _dragArmed = false;
                TabDragStarted?.Invoke(this, _dragItem);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _closeHoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var idx = IndexAt(e.Location);

        // 中键关闭（VSCode 习惯）
        if (e.Button == MouseButtons.Middle && idx >= 0)
        {
            TabClosed?.Invoke(this, _tabs[idx]);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (idx >= 0)
        {
            if (IsOnCloseButton(e.Location, idx))
            {
                TabClosed?.Invoke(this, _tabs[idx]);
                return;
            }
            _dragArmed = true;
            _dragItem = _tabs[idx];
            _dragStart = e.Location;
            TabActivated?.Invoke(this, _tabs[idx]);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragArmed = false;
        _dragItem = null;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var idx = IndexAt(e.Location);
        if (idx >= 0)
        {
            TabDoubleClicked?.Invoke(this, _tabs[idx]);
            return;
        }
        TabCreateRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // 标签放不下时横向滚动
        var total = 0;
        using (var g = CreateGraphics())
            foreach (var t in _tabs) total += MeasureTabWidth(g, t, Theme.TabFont);

        if (total <= Width) { _scrollOffset = 0; return; }

        var delta = e.Delta > 0 ? -60 : 60;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, Math.Max(0, total - Width));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right ? true : base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_tabs.Count == 0) return;

        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
        {
            var cur = ActiveTab is null ? -1 : _tabs.IndexOf(ActiveTab);
            var next = e.KeyCode == Keys.Left ? cur - 1 : cur + 1;
            if (next < 0) next = _tabs.Count - 1;
            if (next >= _tabs.Count) next = 0;
            TabActivated?.Invoke(this, _tabs[next]);
            e.Handled = true;
        }
    }
}
