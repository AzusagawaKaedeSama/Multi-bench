// 作者：WorkBuddy（智能设计助手）· 2026-10-31
// 布局自检：把控件树的实际 Bounds 打印出来。
//
// 用途：WinForms 的 Dock 布局一旦和手动 Bounds 打架，
// 症状是「控件重叠」「控件不见了」「被挤成一列」——
// 光看截图猜不出来，必须看真实坐标。

namespace WorkbenchShell;

using System.Text;

internal static class LayoutDump
{
    public static string Dump(Control root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"根控件 {root.GetType().Name} bounds={root.Bounds} visible={root.Visible} client={root.ClientSize}");
        Walk(root, sb, 0);
        return sb.ToString();
    }

    private static void Walk(Control c, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.AppendLine($"{pad}- {c.GetType().Name,-22} name='{c.Name}' bounds={c.Bounds} " +
                      $"dock={c.Dock} visible={c.Visible} parent={(c.Parent?.GetType().Name ?? "-")}");
        foreach (Control child in c.Controls)
            Walk(child, sb, depth + 1);
    }
}
