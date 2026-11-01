// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 主题渲染器：让原生菜单栏、工具条跟着主题走。
//
// WinForms 的 ToolStrip 默认用系统配色（XP 灰蓝），跟现代主题格格不入。
// 覆盖 ProfessionalColorTable 的几个关键色位即可 —— 不必自绘整条菜单。

namespace WorkbenchShell;

internal sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    public ThemeRenderer() : base(new Colors())
    {
        RoundedEdges = false;
    }

    private sealed class Colors : ProfessionalColorTable
    {
        private static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t),
                (int)(a.A + (b.A - a.A) * t));

        public override Color MenuItemSelected => Theme.IsDark ? Theme.Hover : Theme.Selected;
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override Color MenuItemPressedGradientEnd => MenuItemSelected;
        public override Color MenuItemBorder => Theme.IsDark ? Theme.Accent : Theme.Line;
        public override Color MenuBorder => Theme.Line;
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Blend(Theme.Line, Theme.Surface, 0.55);
        public override Color ToolStripGradientBegin => Theme.Surface;
        public override Color ToolStripGradientMiddle => Theme.Surface;
        public override Color ToolStripGradientEnd => Theme.Surface;
        public override Color CheckBackground => Theme.AccentSoft;
        public override Color CheckSelectedBackground => Theme.Accent;
        public override Color CheckPressedBackground => Theme.Accent;
    }
}

/// <summary>把主题刷到一棵控件树上。</summary>
internal static class ThemeApplier
{
    /// <summary>递归刷新所有已知控件的配色。</summary>
    public static void Apply(Control root)
    {
        try
        {
            if (root is Form form) form.BackColor = Theme.Bg;
            Walk(root);
            root.Invalidate(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"主题刷新失败：{ex.Message}");
        }
    }

    private static void Walk(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            switch (child)
            {
                case TabStrip strip:
                    strip.BackColor = Theme.TabIdle;
                    strip.Invalidate();
                    break;

                case EditorGroup group:
                    group.RefreshTheme();
                    break;

                case SideBarPanel panel:
                    panel.BackColor = Theme.Bg;
                    panel.Invalidate();
                    break;

                case SideBar bar:
                    bar.BackColor = Theme.Bg;
                    break;

                case SplitContainer split:
                    split.BackColor = Theme.Line;
                    break;

                case MenuStrip menu:
                    menu.BackColor = Theme.Surface;
                    menu.ForeColor = Theme.Ink;
                    menu.Renderer = new ThemeRenderer();
                    menu.Invalidate();
                    break;

                case Panel pnl when pnl.Name == "_hostPanel":
                    pnl.BackColor = Theme.Bg;
                    break;
            }

            Walk(child);
        }
    }
}
