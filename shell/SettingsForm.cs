// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 设置窗口：一个按分类分页的原生设置界面（外观 / 侧边栏 / 标签页 / 启动 / 关于）。
//
// 设计取舍：
//   · 左边分类、右边选项、底部「恢复默认 / 保存 / 取消」—— 常规桌面软件的结构，
//     用户不用重新学一套交互。
//   · 改动先写进 UiSettings 的副本（_draft），点保存才落到磁盘并广播；
//     取消就整份丢掉，不会留下改了一半的配置。
//   · 所有选项都在这一个窗口里，不做「高级设置另开文件」那种把用户丢给记事本的方案。

namespace WorkbenchShell;

using System.Reflection;

internal sealed class SettingsForm : Form
{
    private readonly UiSettings _draft;
    private readonly ListBox _nav;
    private readonly Panel _body;
    private int _y;

    private static readonly (string Key, string Label)[] Sections =
    {
        ("appearance", "外观"),
        ("sidebar", "侧边栏"),
        ("tabs", "标签页"),
        ("startup", "启动"),
        ("about", "关于"),
    };

    private const int RowWidth = 520;

    public SettingsForm(UiSettings current)
    {
        _draft = current.Clone();

        Text = "设置 · 宿舍机工作台";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 580);
        MinimumSize = new Size(700, 500);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = Theme.TreeFont;
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;

        _nav = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 150,
            BorderStyle = BorderStyle.None,
            Font = Theme.TreeFont,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            IntegralHeight = false,
            ItemHeight = 32,
        };
        foreach (var s in Sections) _nav.Items.Add(s.Label);

        // 分类列表用主题配色重绘：系统默认的蓝底白字和这套界面放在一起很突兀。
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var back = new SolidBrush(selected ? Theme.Selected : Theme.Surface))
                e.Graphics.FillRectangle(back, e.Bounds);

            if (selected)
            {
                using var bar = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(bar, new Rectangle(e.Bounds.X, e.Bounds.Y + 8, 3, e.Bounds.Height - 16));
            }

            TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index]?.ToString() ?? "", Theme.TreeFont,
                new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - 22, e.Bounds.Height),
                selected ? Theme.Accent : Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        _nav.SelectedIndex = 0;
        _nav.SelectedIndexChanged += (_, _) => BuildSection(Sections[Math.Max(0, _nav.SelectedIndex)].Key);

        _body = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Bg,
            Padding = new Padding(28, 14, 24, 18),
        };

        var buttons = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = Theme.Surface,
            Padding = new Padding(18, 13, 18, 13),
        };

        var save = MakeButton("保存", primary: true);
        save.Dock = DockStyle.Right;
        save.Click += (_, _) => SaveAndClose();

        var cancel = MakeButton("取消", primary: false);
        cancel.Dock = DockStyle.Right;
        cancel.Click += (_, _) => Close();

        var reset = MakeButton("恢复默认", primary: false);
        reset.Dock = DockStyle.Left;
        reset.Width = 110;
        reset.Click += (_, _) => ResetDraft();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(reset);

        Controls.Add(_body);
        Controls.Add(_nav);
        Controls.Add(buttons);

        BuildSection("appearance");
    }

    private Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Width = 96,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.Accent : Theme.Surface,
            ForeColor = primary ? Color.White : Theme.Ink,
            Font = Theme.TreeFont,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Line;
        return button;
    }

    // ──────────────────────────── 分区构建 ────────────────────────────

    private void BuildSection(string key)
    {
        _body.SuspendLayout();
        _body.Controls.Clear();
        _y = 4;

        switch (key)
        {
            case "appearance": BuildAppearance(); break;
            case "sidebar": BuildSideBar(); break;
            case "tabs": BuildTabs(); break;
            case "startup": BuildStartup(); break;
            default: BuildAbout(); break;
        }

        _body.ResumeLayout();
    }

    private void BuildAppearance()
    {
        Heading("外观");
        Hint("主题和字号会同时作用到侧边栏、标签栏和菜单栏。");

        var themes = Theme.All.Select(t => t.Name).ToArray();
        var index = Array.IndexOf(themes, _draft.ThemeName);
        Combo("主题", themes, Math.Max(0, index), i => _draft.ThemeName = themes[i]);

        Heading("字号");
        Hint("按比例缩放壳里所有原生文字，页面内容不受影响。");
        Slider("字号倍率", 80, 140, (int)Math.Round(_draft.FontScale * 100), "%",
            v => _draft.FontScale = v / 100f,
            v => v switch { < 90 => "偏小", < 105 => "标准", < 120 => "偏大", _ => "很大" });

        Heading("行距");
        Hint("决定侧边栏每行的高度。屏幕矮时调紧凑，一屏能多看几条。");
        Slider("行距倍率", 85, 120, (int)Math.Round(_draft.DensityScale * 100), "%",
            v => _draft.DensityScale = v / 100f,
            v => v switch { < 95 => "紧凑", < 105 => "标准", _ => "宽松" });
    }

    private void BuildSideBar()
    {
        Heading("侧边栏");
        Hint("宽度、每组先显示几条、标题最多显示几个字。");

        Slider("侧边栏宽度", 200, 440, _draft.SideBarWidth, " px",
            v => _draft.SideBarWidth = v);

        Slider("待审项目先显示", 3, 30, _draft.ReviewPreviewLimit, " 条",
            v => _draft.ReviewPreviewLimit = v,
            v => v == 6 ? "默认" : "其余折叠");

        Slider("最近活跃显示", 0, 12, _draft.RecentLimit, " 条",
            v => _draft.RecentLimit = v,
            v => v == 0 ? "不显示这一组" : "");

        Slider("标题最多字数", 6, 30, _draft.MaxTitleChars, " 个全角字",
            v => _draft.MaxTitleChars = v,
            v => v == 10 ? "默认" : "超出截断并留悬停提示");

        Check("显示状态点", _draft.ShowStatusDot, v => _draft.ShowStatusDot = v);
        Hint("状态点：待审黄、已完成绿、已归档灰。关掉后行首更干净。");
    }

    private void BuildTabs()
    {
        Heading("标签页");
        Hint("标签条高度与单个标签的最大宽度。");

        Slider("标签条高度", 26, 48, _draft.TabStripHeight, " px",
            v => _draft.TabStripHeight = v);

        Slider("标签最大宽度", 140, 380, _draft.TabMaxWidth, " px",
            v => _draft.TabMaxWidth = v,
            v => v >= 300 ? "长标题不折" : "");

        Hint("改完保存后，已打开的标签会按新高度重排。");
    }

    private void BuildStartup()
    {
        Heading("启动");
        Hint("开机自启会在「启动」文件夹里放一个带 --tray 的快捷方式，登录后静默进托盘。");

        var installed = UiSettings.StartupEnabled();
        Check("开机时静默启动（进托盘）", _draft.StartWithWindows || installed,
            v => _draft.StartWithWindows = v);
        Check("启动后直接进托盘、不弹窗口", _draft.StartInTray, v => _draft.StartInTray = v);

        _y += 8;
        var now = UiSettings.StartupEnabled();
        Hint(now ? "当前状态：已写入开机自启。" : "当前状态：未写入开机自启。");
    }

    private void BuildAbout()
    {
        Heading("关于");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        KeyValue("版本", version);
        KeyValue("程序目录", AppPaths.BaseDir);
        KeyValue("设置文件", UiSettings.FilePath);
        KeyValue("日志目录", AppPaths.LogDir);
        KeyValue("WebView2 数据", AppPaths.WebViewDataDir);
        KeyValue("工作台地址", Workbench.WorkbenchOrigin);

        _y += 10;
        var open = MakeButton("打开设置文件夹", primary: false);
        open.Width = 150;
        open.Left = 28;
        open.Top = _y;
        open.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"${AppPaths.BaseDir}\"") { UseShellExecute = true });
            }
            catch { }
        };
        _body.Controls.Add(open);
        _y += 44;

        Hint("改坏了设置可以直接删掉 settings.json，下次启动按默认值走。");
    }

    // ──────────────────────────── 控件辅助 ────────────────────────────

    private void Heading(string text)
    {
        _y += _body.Controls.Count > 0 ? 14 : 0;
        _body.Controls.Add(new Label
        {
            Text = text,
            Font = Theme.MakeFont(10.5f, FontStyle.Bold),
            ForeColor = Theme.Ink,
            AutoSize = true,
            Left = 28,
            Top = _y,
        });
        _y += 34;
    }

    private void Hint(string text)
    {
        _body.Controls.Add(new Label
        {
            Text = text,
            Font = Theme.MakeFont(8.2f, FontStyle.Regular),
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = RowWidth,
            Height = 34,
            Left = 28,
            Top = _y,
        });
        _y += 38;
    }

    private void KeyValue(string key, string value)
    {
        _body.Controls.Add(new Label
        {
            Text = key,
            Font = Theme.MakeFont(8.4f, FontStyle.Regular),
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 110,
            Height = 24,
            Left = 28,
            Top = _y,
        });
        _body.Controls.Add(new Label
        {
            Text = value,
            Font = Theme.MakeFont(8.4f, FontStyle.Regular),
            ForeColor = Theme.Ink,
            AutoSize = false,
            Width = RowWidth - 110,
            Height = 24,
            Left = 140,
            Top = _y,
        });
        _y += 26;
    }

    private void Check(string text, bool value, Action<bool> setter)
    {
        var box = new CheckBox
        {
            Text = text,
            Checked = value,
            Font = Theme.TreeFont,
            ForeColor = Theme.Ink,
            BackColor = Theme.Bg,
            AutoSize = false,
            Width = RowWidth,
            Height = 30,
            Left = 28,
            Top = _y,
        };
        box.CheckedChanged += (_, _) => setter(box.Checked);
        _body.Controls.Add(box);
        _y += 34;
    }

    private void Combo(string label, string[] items, int selected, Action<int> setter)
    {
        _body.Controls.Add(new Label
        {
            Text = label,
            Font = Theme.TreeFont,
            ForeColor = Theme.Ink,
            AutoSize = false,
            Width = 110,
            Height = 26,
            Left = 28,
            Top = _y + 3,
        });

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.TreeFont,
            Width = 280,
            Left = 140,
            Top = _y,
        };
        combo.Items.AddRange(items);
        combo.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        combo.SelectedIndexChanged += (_, _) => setter(combo.SelectedIndex);
        _body.Controls.Add(combo);
        _y += 38;
    }

    private void Slider(string label, int min, int max, int value, string suffix,
        Action<int> setter, Func<int, string>? describe = null)
    {
        _body.Controls.Add(new Label
        {
            Text = label,
            Font = Theme.TreeFont,
            ForeColor = Theme.Ink,
            AutoSize = false,
            Width = 110,
            Height = 26,
            Left = 28,
            Top = _y + 6,
        });

        var bar = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = Math.Max(1, (max - min) / 8),
            LargeChange = Math.Max(1, (max - min) / 10),
            SmallChange = 1,
            Width = 250,
            Left = 140,
            Top = _y,
            BackColor = Theme.Bg,
        };

        var readout = new Label
        {
            Font = Theme.MakeFont(8.4f, FontStyle.Regular),
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 190,
            Height = 26,
            Left = 406,
            Top = _y + 6,
        };

        void Show()
        {
            var extra = describe?.Invoke(bar.Value) ?? "";
            readout.Text = bar.Value + suffix + (string.IsNullOrEmpty(extra) ? "" : " · " + extra);
        }

        bar.ValueChanged += (_, _) => { setter(bar.Value); Show(); };
        Show();

        _body.Controls.Add(bar);
        _body.Controls.Add(readout);
        _y += 46;
    }

    // ──────────────────────────── 动作 ────────────────────────────

    private void ResetDraft()
    {
        var fresh = new UiSettings();
        _draft.ThemeName = fresh.ThemeName;
        _draft.FontFamily = fresh.FontFamily;
        _draft.FontScale = fresh.FontScale;
        _draft.DensityScale = fresh.DensityScale;
        _draft.SideBarWidth = fresh.SideBarWidth;
        _draft.ReviewPreviewLimit = fresh.ReviewPreviewLimit;
        _draft.RecentLimit = fresh.RecentLimit;
        _draft.MaxTitleChars = fresh.MaxTitleChars;
        _draft.ShowStatusDot = fresh.ShowStatusDot;
        _draft.TabStripHeight = fresh.TabStripHeight;
        _draft.TabMaxWidth = fresh.TabMaxWidth;
        _draft.StartWithWindows = fresh.StartWithWindows;
        _draft.StartInTray = fresh.StartInTray;

        BuildSection(Sections[Math.Max(0, _nav.SelectedIndex)].Key);
    }

    private void SaveAndClose()
    {
        var startupApplied = UiSettings.ApplyStartup(_draft.StartWithWindows);

        UiSettings.Apply(_draft);

        if (!startupApplied)
        {
            MessageBox.Show(this,
                "设置已经保存，但开机自启的快捷方式没能写入启动文件夹。\n可以稍后重试，或手工把程序放进「启动」文件夹。",
                "开机自启未生效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
