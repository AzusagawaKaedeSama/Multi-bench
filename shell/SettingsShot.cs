// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 设置窗口离屏取证：五页各出一张 PNG，验证排版没被裁、字没糊。
//
// 与其它取证一样：窗口放在虚拟屏外，靠 DrawToBitmap 拿像素，不碰用户屏幕。

namespace WorkbenchShell;

using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

internal static class SettingsShot
{
    internal static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();
        NativeConsole.Attach();
        UiSettings.EnsureLoaded();

        var report = new Dictionary<string, object?>
        {
            ["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["settingsFile"] = UiSettings.FilePath,
        };

        try
        {
            using var form = new SettingsForm(UiSettings.Current)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            };

            form.Show();
            Application.DoEvents();
            Thread.Sleep(300);

            var nav = typeof(SettingsForm)
                .GetField("_nav", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(form) as ListBox;

            if (nav is null)
            {
                report["error"] = "取不到分类列表";
                report["verdict"] = "FAIL";
                Write(report, outputDir);
                return 1;
            }

            var names = new List<string>();
            for (var i = 0; i < nav.Items.Count; i++)
            {
                nav.SelectedIndex = i;
                Application.DoEvents();
                Thread.Sleep(120);

                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                var name = $"设置-{i + 1}-{nav.Items[i]}.png";
                bmp.Save(Path.Combine(outputDir, name), ImageFormat.Png);
                names.Add(name);
            }

            report["pages"] = names;
            report["verdict"] = "PASS";
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            report["verdict"] = "FAIL";
            Write(report, outputDir);
            return 1;
        }

        Write(report, outputDir);
        return 0;
    }

    private static void Write(Dictionary<string, object?> report, string dir)
    {
        report["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var path = Path.Combine(dir, "settings-shot.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("REPORT: " + path);
    }
}
