// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 二级窗口：承载独立的标签组，用于「拆分到新窗口」（VSCode 的 Split Window）。
//
// 与主窗口的区别：二级窗口没有侧边栏，只有一个布局树。
// 它与主窗口共享同一个 WebView2 环境（同一 userDataFolder），
// 所以登录态、localStorage、审阅勾选状态在两个窗口之间是一致的 ——
// 这点很重要：用户在二级窗口里勾选审阅，回到主窗口看到的是同一份状态。

namespace WorkbenchShell;

internal sealed class SecondaryWindow : Form
{
    private readonly Workbench _bench;
    private readonly TaskCompletionSource _envReady = new();

    /// <summary>当前激活的标签（跨窗口移动时用）。</summary>
    public TabItem? ActiveTab => _bench.ActiveTab;

    /// <param name="owner">主窗口，用来决定新窗口的位置（放在主窗口右边）。</param>
    public SecondaryWindow(Form owner)
    {
        Text = "宿舍机工作台 · 新窗口";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(720, 480);
        BackColor = Theme.Bg;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;

        // 放在主窗口右边，别完全盖住
        Location = new Point(owner.Left + owner.Width + 12, owner.Top);

        // Workbench 的这个构造会自己建 SplitLayout 并挂到窗体上
        _bench = new Workbench(this) { Host = owner as MainForm };
        _bench.EnsureRootGroup();

        // 环境初始化是异步的，但标签接管必须等环境就绪。
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _bench.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"二级窗口环境初始化失败：{ex.Message}");
        }
        finally
        {
            _envReady.TrySetResult();
        }
    }

    /// <summary>把一个标签接收到本窗口。</summary>
    public async Task ReceiveTabAsync(TabItem tab)
    {
        // 环境没就绪就等它 —— 标签的 WebView2 需要共享环境才能正常接管
        await _envReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _bench.AdoptTabAsync(tab);
    }

    /// <summary>在本窗口里打开一个地址（供右键菜单「在新窗口中打开」使用）。</summary>
    public async Task OpenUrlAsync(string title, string url)
    {
        await _envReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _bench.OpenTab(title, url);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bench.Dispose();
        base.OnFormClosed(e);
    }
}
