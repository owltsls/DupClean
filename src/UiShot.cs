using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DupClean;

/// <summary>离线界面渲染自检：把每个步骤绘制成 PNG，并检查控件越界，不影响正式运行。</summary>
public static class UiShot
{
    public static int Capture(string dir)
    {
        Directory.CreateDirectory(dir);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var form = new MainForm { ShowInTaskbar = false };
        form.Show();
        Application.DoEvents();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                Show(form, i);
                Shot(form, dir, $"step{i + 1}.png");
            }
            Console.WriteLine($"FORM {form.Width}x{form.Height} CLIENT {form.ClientSize.Width}x{form.ClientSize.Height}");
            if (form.Controls.Count > 0)
            {
                var root = form.Controls[0];
                Console.WriteLine($"ROOT {root.GetType().Name} {root.Width}x{root.Height}");
                foreach (Control c in root.Controls)
                    Console.WriteLine($"  ZONE {c.GetType().Name} bounds={c.Bounds}");
            }
            int overflow = 0;
            Walk(form, "", ref overflow);
            Console.WriteLine($"OVERFLOW_COUNT {overflow}");
            // 作者联系方式必须是主界面上真实可见的控件，而不是只写死在「关于」弹窗里
            Console.WriteLine($"CONTACT_VISIBLE {(FindContact(form) ? "OK" : "MISSING")} ID={MainForm.AuthorWeChat}");
            Console.WriteLine($"WINDOW_ICON {(form.Icon != null ? "OK" : "MISSING")} size={form.Icon?.Width}x{form.Icon?.Height}");

            // 带示例数据渲染第 3 步，核对重复组新增的文件名 / 完整路径列
            ((MainForm)form).LoadDemoGroupsForCapture();
            Show(form, 2);
            Shot(form, dir, "step3_with_data.png");
            ((MainForm)form).DemoSortForCapture(5);   // 点击「可释放」列头，默认降序
            Show(form, 2);
            Shot(form, dir, "step3_sorted.png");

            // 最小允许窗口尺寸下再核对一次，确保缩小窗口不裁切、不重叠
            form.Size = form.MinimumSize;
            Show(form, 0);
            Shot(form, dir, "small_step1.png");
            Show(form, 2);
            Shot(form, dir, "small_step3.png");
            int smallOverflow = 0;
            Walk(form, "", ref smallOverflow);
            Console.WriteLine($"SMALL_FORM {form.Width}x{form.Height} OVERFLOW_COUNT {smallOverflow}");
        }
        finally { form.Close(); }
        return 0;
    }

    private static void Show(Form form, int step)
    {
        ((MainForm)form).ShowStep(step);
        form.Show();                 // 保持可见，否则隐藏窗体的子控件不会被 DrawToBitmap 绘制
        Application.DoEvents();
        form.Refresh();
        Application.DoEvents();
    }

    private static void Shot(Form form, string dir, string name)
    {
        // Form.DrawToBitmap 会连标题栏一起绘制，必须按整窗尺寸取图，
        // 否则按 ClientSize 取图会把底部页脚裁掉。
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        string path = Path.Combine(dir, name);
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("SHOT " + path);
    }

    private static bool FindContact(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c.Visible && c.Text != null && c.Text.Contains(MainForm.AuthorWeChat, StringComparison.Ordinal))
                return true;
            if (FindContact(c)) return true;
        }
        return false;
    }

    private static void Walk(Control parent, string path, ref int overflow)
    {
        foreach (Control c in parent.Controls)
        {
            string here = path + "/" + (string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name);
            if (c.Visible && c.Parent != null)
            {
                int pw = c.Parent.ClientSize.Width, ph = c.Parent.ClientSize.Height;
                if (c.Right > pw + 6 || c.Bottom > ph + 6)
                {
                    Console.WriteLine($"  OVERFLOW {here} bounds={c.Bounds} parent={pw}x{ph}");
                    overflow++;
                }
            }
            Walk(c, here, ref overflow);
        }
    }
}
