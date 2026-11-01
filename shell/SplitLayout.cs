// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 分裂布局：管理一棵二叉布局树。
//
// 节点有两种：
//   - 分组节点（EditorGroup）：一排标签 + 内容区
//   - 分割节点（SplitNode）：把区域按方向切成两半，横向（左右）或纵向（上下）
//
// 为什么要自己写而不用 SplitContainer 嵌套：
//   嵌套 SplitContainer 在做「把组移出/合并」时会留下孤儿控件和尺寸错乱，
//   自己算矩形则完全可控，也更容易实现 VSCode 的行为：
//   分裂后新组继承原组的标签焦点；关闭一个组后兄弟自动撑满。

namespace WorkbenchShell;

internal enum SplitDirection
{
    /// <summary>左右分割（VSCode: Split Editor Right）。</summary>
    Vertical,

    /// <summary>上下分割（VSCode: Split Editor Down）。</summary>
    Horizontal,
}

internal sealed class SplitLayout : Panel
{
    private abstract class Node
    {
        public abstract void Layout(Rectangle bounds);
    }

    private sealed class GroupNode : Node
    {
        public EditorGroup Group = null!;
        public override void Layout(Rectangle bounds) => Group.Bounds = bounds;
    }

    private sealed class SplitNode : Node
    {
        public Node First = null!;
        public Node Second = null!;
        public SplitDirection Direction = SplitDirection.Vertical;
        /// <summary>第一个子节点占的比例（0.1~0.9）。</summary>
        public float Ratio = 0.5f;

        public override void Layout(Rectangle bounds)
        {
            if (Direction == SplitDirection.Vertical)
            {
                var w = (int)(bounds.Width * Ratio);
                w = Math.Clamp(w, 140, Math.Max(140, bounds.Width - 140));
                First.Layout(new Rectangle(bounds.X, bounds.Y, w, bounds.Height));
                Second.Layout(new Rectangle(bounds.X + w, bounds.Y, bounds.Width - w, bounds.Height));
            }
            else
            {
                var h = (int)(bounds.Height * Ratio);
                h = Math.Clamp(h, 120, Math.Max(120, bounds.Height - 120));
                First.Layout(new Rectangle(bounds.X, bounds.Y, bounds.Width, h));
                Second.Layout(new Rectangle(bounds.X, bounds.Y + h, bounds.Width, bounds.Height - h));
            }
        }
    }

    private Node? _root;
    private readonly List<EditorGroup> _groups = new();
    private readonly List<SplitNode> _splits = new();

    /// <summary>当前获得焦点的分组（最后被激活的那个）。</summary>
    public EditorGroup? ActiveGroup { get; private set; }

    /// <summary>某个组的标签全被关掉时触发（宿主据此把组移除并合并兄弟）。</summary>
    public event EventHandler<EditorGroup>? GroupEmpty;

    /// <summary>根分组（第一个创建的）。</summary>
    public EditorGroup? Root { get; private set; }

    /// <summary>归属的宿主（新分组会自动挂上，拖放与标签转移要用）。</summary>
    public Workbench? Host { get; set; }

    public IEnumerable<EditorGroup> Groups => _groups;

    public SplitLayout()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Line;
        DoubleBuffered = true;

        // 关键：本容器用手动布局。
        // 子控件（分组）都是 Dock=None + 手动设 Bounds，
        // 如果让 WinForms 的自动布局接管，Fill/Top 之类的 Dock 会和布局树打架。
        AutoScroll = false;
    }

    /// <summary>创建首个分组（宿主启动时调用）。</summary>
    public EditorGroup CreateRootGroup()
    {
        var group = new EditorGroup { Dock = DockStyle.None };
        Register(group);
        _root = new GroupNode { Group = group };
        Root = group;
        ActiveGroup = group;
        Controls.Add(group);
        return group;
    }

    private void Register(EditorGroup group)
    {
        _groups.Add(group);
        group.Host = Host!;
        group.TabActivated += (_, _) => ActiveGroup = group;
        group.GroupEmpty += (_, g) => GroupEmpty?.Invoke(this, g);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Reflow();
    }

    /// <summary>把所有组按布局树排布，并画出分割线。</summary>
    public void Reflow()
    {
        if (_root is not null) _root.Layout(new Rectangle(0, 0, Width, Height));
        Invalidate();
    }

    /// <summary>画分割线：分割节点之间留 4px 的浅色缝，视觉上就是 VSCode 的分隔条。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        foreach (var split in _splits)
        {
            if (split.First is not GroupNode a || split.Second is not GroupNode b) continue;
            var ra = a.Group.Bounds;
            var rb = b.Group.Bounds;
            if (split.Direction == SplitDirection.Vertical)
            {
                var x = rb.Left;
                if (x > 0 && x < Width) e.Graphics.DrawLine(pen, x, 0, x, Height);
            }
            else
            {
                var y = rb.Top;
                if (y > 0 && y < Height) e.Graphics.DrawLine(pen, 0, y, Width, y);
            }
        }
    }

    /// <summary>把一个组分裂成两个：原组保留标签，新组为空。</summary>
    public EditorGroup Split(EditorGroup group, SplitDirection direction)
    {
        if (!_groups.Contains(group)) return group;

        var newGroup = new EditorGroup { Dock = DockStyle.None };
        Register(newGroup);

        // 找到 group 在树里的位置，替换成分割节点
        var parent = FindParentOf(group);
        if (parent is null)
        {
            // group 是根：整体变成一个分割节点
            var split = new SplitNode
            {
                First = new GroupNode { Group = group },
                Second = new GroupNode { Group = newGroup },
                Direction = direction,
                Ratio = 0.5f,
            };
            _root = split;
            _splits.Add(split);
            AddGroupControl(newGroup);
            Reflow();
            return newGroup;
        }

        // group 在某个分割节点里：把那一侧再切一层
        if (parent is SplitNode parentSplit)
        {
            var isFirst = ReferenceEquals(parentSplit.First, FindNodeOf(group));
            var newSplit = new SplitNode
            {
                Direction = direction,
                Ratio = 0.5f,
            };

            // 新组紧贴原组：「向右分裂」→ 原组在左、新组在右
            if ((direction == SplitDirection.Vertical && isFirst) ||
                (direction == SplitDirection.Horizontal && isFirst))
            {
                newSplit.First = new GroupNode { Group = group };
                newSplit.Second = new GroupNode { Group = newGroup };
            }
            else
            {
                newSplit.First = new GroupNode { Group = newGroup };
                newSplit.Second = new GroupNode { Group = group };
            }

            if (isFirst) parentSplit.First = newSplit;
            else parentSplit.Second = newSplit;

            _splits.Add(newSplit);
            AddGroupControl(newGroup);
            Reflow();
            return newGroup;
        }

        return newGroup;
    }

    /// <summary>把分组加进控件集合（手动布局，z 序由布局顺序决定）。</summary>
    private void AddGroupControl(EditorGroup group)
    {
        Controls.Add(group);
        group.BringToFront();
    }

    private Node? FindNodeOf(EditorGroup group)
    {
        Node? Walk(Node? node)
        {
            if (node is null) return null;
            if (node is GroupNode gn && ReferenceEquals(gn.Group, group)) return gn;
            if (node is SplitNode sn)
            {
                var a = Walk(sn.First);
                if (a is not null) return a;
                return Walk(sn.Second);
            }
            return null;
        }
        return Walk(_root);
    }

    /// <summary>找到包含该组的分割节点（若该组是根则返回 null）。</summary>
    private SplitNode? FindParentOf(EditorGroup group)
    {
        SplitNode? Walk(Node? node, SplitNode? parent)
        {
            if (node is null) return null;
            if (node is GroupNode gn && ReferenceEquals(gn.Group, group)) return parent;
            if (node is SplitNode sn)
            {
                var a = Walk(sn.First, sn);
                if (a is not null) return a;
                return Walk(sn.Second, sn);
            }
            return null;
        }
        return Walk(_root, null);
    }

    /// <summary>移除一个组（它必须已经空了）。如果它是分割节点的一侧，兄弟会撑满。</summary>
    public void RemoveGroup(EditorGroup group)
    {
        if (!_groups.Contains(group)) return;

        var node = FindNodeOf(group);
        if (node is null) return;

        var parent = FindParentOf(group);
        if (parent is null)
        {
            // 根组被移除：整棵树塌掉
            _root = null;
            Root = null;
        }
        else
        {
            // 把这一侧换成兄弟
            var sibling = ReferenceEquals(parent.First, node) ? parent.Second : parent.First;
            var grand = FindParentOfNode(parent);
            if (grand is null) _root = sibling;
            else if (ReferenceEquals(grand.First, parent)) grand.First = sibling;
            else grand.Second = sibling;

            _splits.Remove(parent);
        }

        Controls.Remove(group);
        _groups.Remove(group);

        if (ReferenceEquals(ActiveGroup, group))
            ActiveGroup = _groups.FirstOrDefault();

        // 重新布局
        Reflow();
    }

    private SplitNode? FindParentOfNode(SplitNode target)
    {
        SplitNode? Walk(Node? node, SplitNode? parent)
        {
            if (node is null) return null;
            if (ReferenceEquals(node, target)) return parent;
            if (node is SplitNode sn)
            {
                var a = Walk(sn.First, sn);
                if (a is not null) return a;
                return Walk(sn.Second, sn);
            }
            return null;
        }
        return Walk(_root, null);
    }

    /// <summary>把所有组排布进可用区域。</summary>
}
