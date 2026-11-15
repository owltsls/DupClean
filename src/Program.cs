using System.Windows.Forms;

namespace DupClean;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(args.Length > 1 ? args[1] : null);

        if (args.Length > 0 && args[0] == "--cli-scan" && args.Length > 1)
            return CliScan(args[1]);

        if (args.Length > 0 && args[0] == "--uishot" && args.Length > 1)
            return UiShot.Capture(args[1]);

        if (args.Length > 0 && args[0] == "--uitest")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            using var form = new MainForm();
            Console.WriteLine("UI_CONSTRUCT_OK");
            return 0;
        }

        if (args.Length > 0 && (args[0] == "--help" || args[0] == "/?"))
        {
            Console.WriteLine("DupClean 重复文件清理工具 v1.0.4");
            Console.WriteLine("  直接双击运行：打开图形界面");
            Console.WriteLine("  --selftest [测试根目录] ：运行内置 19 项验收自测");
            Console.WriteLine("  --cli-scan <目录>      ：命令行扫描并输出重复组");
            Console.WriteLine("  --uitest               ：校验界面构造");
            Console.WriteLine("  --uishot <图片目录>    ：导出各步骤界面截图用于核对布局");
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
        return 0;
    }

    private static int CliScan(string path)
    {
        if (!Directory.Exists(path))
        {
            Console.WriteLine("目录不存在：" + path);
            return 1;
        }
        var options = new ScanOptions { Roots = new List<string> { path }, MinSizeBytes = 1024, Level = MatchLevel.Strict };
        var result = Scanner.Run(options, CancellationToken.None, null);
        var stats = PolicyEngine.Apply(result.Groups, RuleSpec.Defaults(), new List<string>());
        Console.WriteLine($"扫描 {result.Stats.TotalEnumerated} 个文件，重复组 {result.Groups.Count}，可释放 {MainForm.FmtSize(result.Stats.ReclaimableBytes)}");
        foreach (var g in result.Groups)
        {
            Console.WriteLine($"组 #{g.Id}  {MainForm.FmtSize(g.Size)} × {g.Files.Count}  {g.MatchBasis}");
            foreach (var f in g.Files) Console.WriteLine($"    [{f.StateText}] {f.FullPath}");
        }
        Console.WriteLine($"策略结果：删除 {stats.DeleteCount} 个，需人工确认 {stats.NeedConfirmGroups} 组");
        return 0;
    }
}
