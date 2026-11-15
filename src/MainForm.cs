using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;

namespace DupClean;

public sealed class MainForm : Form
{
    private readonly Panel[] _steps = new Panel[5];
    private readonly Label[] _stepLabels = new Label[5];
    private readonly string[] _stepNames = { "1 选范围", "2 扫描", "3 分组处理", "4 删除预演", "5 完成" };
    private int _step;

    private readonly Button _btnBack = new() { Text = "← 上一步", Width = 100 };
    private readonly Button _btnNext = new() { Text = "开始扫描 →", Width = 130 };
    private readonly Button _btnAbout = new() { Text = "关于 / 说明", Width = 110 };

    /// <summary>作者联系方式：主界面页眉常驻显示，点击可复制微信号。</summary>
    public const string AuthorWeChat = "TheSun_Also_Rises";
    private const string ContactText = "作者联系方式 · WeChat ID：" + AuthorWeChat + "（点击复制）";
    private readonly ToolTip _contactTip = new() { AutoPopDelay = 8000, InitialDelay = 400 };

    private ListBox _lstRoots;
    private ComboBox _cmbDrives;
    private TextBox _txtExDirs, _txtExExt, _txtProtected;
    private NumericUpDown _numMinSize, _numThreads;
    private ComboBox _cmbMode;
    private CheckBox _chkSkipSystem, _chkSkipCloud, _chkExcludeJunk;

    private ProgressBar _pbMain;
    private Label _lblStage, _lblProgressDetail, _lblProgressCount;
    private Button _btnCancelScan;

    private ListView _lsvGroups, _lsvFiles, _lsvPreview, _lsvFailures;
    private Label _lblScanSummary, _lblPolicySummary, _lblGroupDetail, _lblPreviewSummary, _lblResult;
    private ListBox _lstRules;
    private ComboBox _cmbRuleKind, _cmbRuleDir;
    private TextBox _txtRuleArg;
    private CheckBox _chkConfirm;
    private RadioButton _rbRecycle, _rbPermanent;
    private Button _btnExecute;
    private ContextMenuStrip _ctxFiles;

    private readonly List<DuplicateGroup> _groups = new();
    private readonly ToolTip _groupTip = new() { AutoPopDelay = 12000, InitialDelay = 500 };
    private string _lastGroupTip = "";
    private static readonly string[] GroupColumnTitles =
        { "组号", "文件名", "完整路径", "份数", "单份大小", "可释放", "状态", "判定依据", "同名" };
    private int _sortColumn = -1;
    private bool _sortDescending;
    private Label _lblGroupsHeader;
    private readonly List<RuleSpec> _rules = RuleSpec.Defaults();
    private readonly RuleKind[] _kindOrder = Enum.GetValues<RuleKind>();
    private ScanOptions _options = new();
    private ScanResult _scanResult;
    private CancellationTokenSource _cts;
    private List<(DuplicateGroup Group, FileEntry File)> _preview = new();
    private DeleteBatch _lastBatch;
    private DuplicateGroup _selectedGroup;

    private static readonly Color KeepColor = Color.FromArgb(226, 244, 232);
    private static readonly Color DeleteColor = Color.FromArgb(252, 234, 232);
    private static readonly Color WarnColor = Color.FromArgb(253, 242, 218);
    private static readonly Color MutedColor = Color.FromArgb(95, 94, 90);

    public MainForm()
    {
        Text = "DupClean 重复文件清理工具  v1.0.4";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1180, 760);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;
        ApplyAppIcon();

        // 根布局固定三行：页眉 / 内容 / 页脚。用 TableLayoutPanel 明确划分，
        // 避免 WinForms 停靠顺序（后添加的 Dock.Fill 抢走整块客户区）造成页眉被内容覆盖。
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 8, 12, 8) };
        root.Controls.Add(host, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        for (int i = 0; i < 5; i++)
        {
            _steps[i] = new Panel { Dock = DockStyle.Fill, Visible = i == 0, Margin = Padding.Empty };
            host.Controls.Add(_steps[i]);
        }
        BuildStep1();
        BuildStep2();
        BuildStep3();
        BuildStep4();
        BuildStep5();
        ShowStep(0);
    }

    /// <summary>
    /// 窗口图标（标题栏 / 任务栏 / Alt-Tab）。EXE 文件图标由 csproj 的 ApplicationIcon 提供；
    /// 这里优先从嵌入的多尺寸 ICO 取 32px，失败时回退到 EXE 关联图标，图标异常不影响主流程。
    /// </summary>
    private void ApplyAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("DupClean.app.ico");
            if (stream != null)
            {
                Icon = new System.Drawing.Icon(stream, new Size(32, 32));
                return;
            }
        }
        catch { /* 嵌入资源读取失败时走回退 */ }
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* 图标非关键功能，不影响扫描与删除 */ }
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.FromArgb(246, 249, 253),
            Margin = Padding.Empty,
            Padding = new Padding(16, 8, 16, 6)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

        var title = new Label
        {
            Text = "DupClean · 本地重复文件清理",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(12, 68, 124),
            Margin = Padding.Empty
        };
        header.Controls.Add(title, 0, 0);
        var sub = new Label
        {
            Text = "纯本地运行 · 不联网 · 不上传任何文件信息 · 删除默认进回收站，可一键还原",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedColor,
            Margin = new Padding(1, 0, 0, 0)
        };
        header.Controls.Add(sub, 0, 1);

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0),
            BackColor = Color.Transparent
        };
        for (int i = 0; i < 5; i++)
        {
            _stepLabels[i] = new Label
            {
                Text = _stepNames[i],
                AutoSize = true,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(2),
                ForeColor = MutedColor,
                BackColor = Color.Transparent
            };
            flow.Controls.Add(_stepLabels[i]);
        }
        header.Controls.Add(flow, 1, 0);

        // 右上角第二行：作者联系方式。与副标题同排、右对齐，不占用内容区宽度。
        var contact = new Label
        {
            Text = ContactText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(12, 68, 124),
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 2, 0)
        };
        _contactTip.SetToolTip(contact, "点击复制微信号 " + AuthorWeChat + "，可直接粘贴到微信搜索");
        contact.Click += (_, _) => CopyAuthorWeChat(contact);
        header.Controls.Add(contact, 1, 1);
        return header;
    }

    /// <summary>点击页眉联系方式即复制微信号；剪贴板被占用时静默失败，文字本身仍可见。</summary>
    private void CopyAuthorWeChat(Label label)
    {
        try
        {
            Clipboard.SetText(AuthorWeChat);
            label.Text = "✓ 已复制微信号：" + AuthorWeChat;
            _contactTip.SetToolTip(label, "已复制到剪贴板，可在微信中粘贴搜索");
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!label.IsDisposed) label.Text = ContactText;
            };
            timer.Start();
        }
        catch { /* 剪贴板被其它程序占用：不打断用户，联系方式仍完整显示 */ }
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(250, 250, 249),
            Margin = Padding.Empty,
            Padding = new Padding(12, 0, 16, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 10, 0, 8)
        };
        _btnBack.Click += (_, _) => GoBack();
        _btnNext.Click += (_, _) => GoNext();
        _btnAbout.Click += (_, _) => ShowAbout();
        foreach (var b in new[] { _btnBack, _btnNext, _btnAbout }) b.Margin = new Padding(4, 3, 4, 3);
        buttons.Controls.AddRange(new Control[] { _btnBack, _btnNext, _btnAbout });
        footer.Controls.Add(buttons, 0, 0);

        var tip = new Label
        {
            Text = "所有删除操作均写入 DupClean_Data/logs，可从「完成」页一键还原",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = MutedColor,
            Margin = Padding.Empty
        };
        footer.Controls.Add(tip, 1, 0);
        return footer;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2)
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(3, 3, 6, 3);
            row.Controls.Add(c);
        }
        return row;
    }

    /// <summary>撑满单元格的一行控件，宽度受容器约束，不会越界。</summary>
    private static FlowLayoutPanel FillRow(params Control[] controls)
    {
        var row = Row(controls);
        row.Dock = DockStyle.Fill;
        row.AutoSize = false;
        row.WrapContents = true;
        row.Margin = Padding.Empty;
        row.Padding = new Padding(2, 2, 2, 2);
        return row;
    }

    private void BuildStep1()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 11,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = new Padding(4, 2, 12, 4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var intro = new Label
        {
            Text = "第一步：选择要扫描的文件夹或磁盘。工具只会按「文件内容」判重，文件名不同、同名不同路径都会被正确合并为一组。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 0, 4),
            ForeColor = Color.FromArgb(12, 68, 124)
        };
        table.Controls.Add(intro, 0, 0);
        table.SetColumnSpan(intro, 2);

        // 扫描位置：左列表 + 右按钮，用两列表格固定分区，不再依赖 Dock.Right 的层级顺序。
        var rootsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rootsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158F));
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _lstRoots = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, 0, 6, 0) };
        rootsPanel.Controls.Add(_lstRoots, 0, 0);
        var rootButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = new Padding(2, 0, 0, 0)
        };
        var btnAddFolder = new Button { Text = "添加文件夹…", Width = 150 };
        var btnRemove = new Button { Text = "移除选中", Width = 150 };
        var btnClear = new Button { Text = "清空", Width = 150 };
        _cmbDrives = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            _cmbDrives.Items.Add(d.Name.TrimEnd('\\'));
        if (_cmbDrives.Items.Count > 0) _cmbDrives.SelectedIndex = 0;
        var btnAddDrive = new Button { Text = "添加该磁盘", Width = 150 };
        btnAddFolder.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "选择要扫描的文件夹" };
            if (dlg.ShowDialog() == DialogResult.OK && !_lstRoots.Items.Contains(dlg.SelectedPath))
                _lstRoots.Items.Add(dlg.SelectedPath);
        };
        btnRemove.Click += (_, _) => { if (_lstRoots.SelectedIndex >= 0) _lstRoots.Items.RemoveAt(_lstRoots.SelectedIndex); };
        btnClear.Click += (_, _) => _lstRoots.Items.Clear();
        btnAddDrive.Click += (_, _) =>
        {
            if (_cmbDrives.SelectedItem is string s && !_lstRoots.Items.Contains(s + "\\"))
                _lstRoots.Items.Add(s + "\\");
        };
        foreach (var c in new Control[] { btnAddFolder, btnRemove, btnClear, _cmbDrives, btnAddDrive })
        {
            c.Margin = new Padding(0, 2, 0, 2);
            rootButtons.Controls.Add(c);
        }
        rootsPanel.Controls.Add(rootButtons, 1, 0);
        table.Controls.Add(new Label { Text = "扫描位置", AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, 1);
        table.Controls.Add(rootsPanel, 1, 1);

        _txtExDirs = new TextBox { Dock = DockStyle.Fill, Text = "" };
        _txtExExt = new TextBox { Dock = DockStyle.Fill, Text = "" };
        _txtProtected = new TextBox { Dock = DockStyle.Fill, Text = "" };

        // 「排除扩展名」一行内嵌一个一键排除选项：勾选即把系统常见文件清单填入输入框
        // （清单可见、可增删），同时启用固定文件名 / ~$ 前缀过滤。
        _chkExcludeJunk = new CheckBox
        {
            Text = "一键排除系统常见临时 / 日志 / 缓存文件（默认清单）",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Checked = false,
            ForeColor = Color.FromArgb(12, 68, 124)
        };
        _chkExcludeJunk.CheckedChanged += (_, _) => SyncJunkExtensions();
        var junkTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        junkTip.SetToolTip(_chkExcludeJunk,
            "默认排除：" + string.Join(" ", Scanner.SystemJunkExtensions) +
            "\n另忽略 thumbs.db / desktop.ini / .ds_store 等固定系统文件名，以及 ~$ 开头的 Office 临时文件。");
        var extWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        extWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        extWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        extWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        extWrap.Controls.Add(_txtExExt, 0, 0);
        extWrap.Controls.Add(_chkExcludeJunk, 0, 1);
        _numMinSize = new NumericUpDown { Minimum = 0, Maximum = 102400, Value = 1, Width = 120, Anchor = AnchorStyles.Left | AnchorStyles.Top };
        _numThreads = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 4, Width = 120, Anchor = AnchorStyles.Left | AnchorStyles.Top };
        _cmbMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbMode.Items.AddRange(new object[]
        {
            "严格模式 · 逐字节比对（100% 强一致，推荐）",
            "均衡模式 · 全文件 SHA-256（高置信，未逐字节校验）",
            "快速模式 · 仅粗筛（禁止直接删除）"
        });
        _cmbMode.SelectedIndex = 0;
        _chkSkipSystem = new CheckBox { Text = "跳过系统目录 / 回收站（推荐）", Checked = true, AutoSize = true };
        _chkSkipCloud = new CheckBox { Text = "跳过云占位文件（推荐）", Checked = true, AutoSize = true };

        AddFormRow(table, 2, "排除目录关键字", _txtExDirs, "用分号分隔；路径含这些文字的目录会被跳过");
        AddFormRow(table, 3, "排除扩展名", extWrap,
            "勾选后自动填入 .tmp / .log / .bak 等 30 项默认清单，可继续手动增删；另忽略 thumbs.db、desktop.ini 与 ~$ 临时文件");
        AddFormRow(table, 4, "受保护目录", _txtProtected, "分号分隔；这些目录下的文件永远保留，规则链不可覆盖");
        AddFormRow(table, 5, "最小文件大小 (KB)", _numMinSize, "小于该值的文件不参与判重，默认 1 KB");
        AddFormRow(table, 6, "判定模式", _cmbMode, "严格模式最终会做逐字节比对，才能承诺 100% 相同");
        AddFormRow(table, 7, "并发线程数", _numThreads, "机械盘建议 2–4，固态盘可 8");
        AddFormRow(table, 8, "安全选项", Row(_chkSkipSystem, _chkSkipCloud), "默认不跟随符号链接 / 目录联接，不申请管理员权限");
        var note = new Label
        {
            Text = "提示：同名但内容不同的文件不会被合并；同名同内容但位于不同目录的文件会被合并。NTFS 硬链接会在结果中单独标记。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = MutedColor,
            Margin = new Padding(0, 8, 0, 0)
        };
        table.Controls.Add(note, 0, 9);
        table.SetColumnSpan(note, 2);

        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));   // 0 说明
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 140F));  // 1 扫描位置
        for (int r = 2; r <= 8; r++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, r == 3 ? 62F : 38F)); // 3 含一键排除勾选项
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));   // 9 提示
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 10 剩余空间
        _steps[0].Controls.Add(table);
    }

    /// <summary>勾选时把默认清单合并进输入框；取消时只移除默认项，保留用户自己填的内容。</summary>
    private void SyncJunkExtensions()
    {
        var current = Scanner.ParseList(_txtExExt.Text);
        var junk = Scanner.SystemJunkExtensions;
        var next = _chkExcludeJunk.Checked
            ? current.Concat(junk).Distinct(StringComparer.OrdinalIgnoreCase)
            : current.Where(x => !junk.Contains(x, StringComparer.OrdinalIgnoreCase));
        _txtExExt.Text = string.Join(";", next);
    }

    private static void AddFormRow(TableLayoutPanel table, int row, string labelText, Control control, string hint)
    {
        table.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 11, 6, 0),
            ForeColor = Color.FromArgb(45, 45, 45)
        }, 0, row);
        var wrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        control.Margin = new Padding(0, 6, 8, 6);
        // 复选框这一行改用撑满布局，避免 AutoSize 行宽溢出到右侧说明区
        if (control is FlowLayoutPanel fp) { fp.Dock = DockStyle.Fill; fp.AutoSize = false; }
        wrap.Controls.Add(control, 0, 0);
        wrap.Controls.Add(new Label
        {
            Text = hint,
            AutoSize = false,
            Dock = DockStyle.Fill,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedColor,
            Margin = new Padding(6, 4, 0, 4)
        }, 1, 0);
        table.Controls.Add(wrap, 1, row);
    }

    private void BuildStep2()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = new Padding(30, 36, 30, 20)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 阶段标题
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 进度条
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));   // 当前文件
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));   // 计数
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // 取消按钮
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));   // 说明
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _lblStage = new Label
        {
            Text = "准备扫描…",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(12, 68, 124),
            Margin = Padding.Empty
        };
        _pbMain = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new Padding(0, 8, 0, 8) };
        _lblProgressDetail = new Label { Text = "", AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty };
        _lblProgressCount = new Label { Text = "", AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty };
        _btnCancelScan = new Button { Text = "取消扫描", Width = 120, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 10, 0, 0) };
        _btnCancelScan.Click += (_, _) => _cts?.Cancel();
        var info = new Label
        {
            Text = "分级漏斗：枚举 → 大小分桶 → 三段采样哈希 → 全文件 SHA-256 → 逐字节比对。\n" +
                   "绝大多数文件在第一步就被排除，只对少量候选读取完整内容，因此大磁盘也能保持流畅。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = MutedColor,
            Margin = new Padding(0, 12, 0, 0)
        };
        table.Controls.Add(_lblStage, 0, 0);
        table.Controls.Add(_pbMain, 0, 1);
        table.Controls.Add(_lblProgressDetail, 0, 2);
        table.Controls.Add(_lblProgressCount, 0, 3);
        table.Controls.Add(_btnCancelScan, 0, 4);
        table.Controls.Add(info, 0, 5);
        _steps[1].Controls.Add(table);
    }

    private void BuildStep3()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _lblScanSummary = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "尚未扫描",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(12, 68, 124),
            Margin = new Padding(4, 4, 4, 4)
        };
        root.Controls.Add(_lblScanSummary, 0, 0);
        root.SetColumnSpan(_lblScanSummary, 2);

        // 左侧：标题行 + 重复组列表（明确两行，避免停靠顺序把标题压到列表下面）
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _lblGroupsHeader = new Label
        {
            Text = "重复组 · 点击列头排序 · 文件名 / 路径为本组保留项 · 悬停查看全部路径",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty
        };
        left.Controls.Add(_lblGroupsHeader, 0, 0);
        _lsvGroups = NewListView();
        _lsvGroups.Dock = DockStyle.Fill;
        _lsvGroups.VirtualMode = true;
        // 文件名与完整路径前置，不用点开每组就能知道这组是什么、在哪里
        _lsvGroups.Columns.Add("组号", 55);
        _lsvGroups.Columns.Add("文件名", 165);
        _lsvGroups.Columns.Add("完整路径", 290);
        _lsvGroups.Columns.Add("份数", 50);
        _lsvGroups.Columns.Add("单份大小", 85);
        _lsvGroups.Columns.Add("可释放", 95);
        _lsvGroups.Columns.Add("状态", 90);
        _lsvGroups.Columns.Add("判定依据", 130);
        _lsvGroups.Columns.Add("同名", 50);
        _lsvGroups.RetrieveVirtualItem += LsvGroups_RetrieveVirtualItem;
        _lsvGroups.SelectedIndexChanged += (_, _) => ShowSelectedGroup();
        _lsvGroups.DoubleClick += (_, _) => { if (_selectedGroup != null) PolicyEngine.SkipGroup(_selectedGroup); RefreshAll(); };
        _lsvGroups.MouseMove += LsvGroups_MouseMove;
        _lsvGroups.ColumnClick += LsvGroups_ColumnClick;
        left.Controls.Add(_lsvGroups, 0, 1);
        root.Controls.Add(left, 0, 1);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));

        var filePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        filePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        filePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        filePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _lblGroupDetail = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "组内文件", ForeColor = MutedColor, Margin = Padding.Empty };
        filePanel.Controls.Add(_lblGroupDetail, 0, 0);
        _lsvFiles = NewListView();
        _lsvFiles.Dock = DockStyle.Fill;
        _lsvFiles.Columns.Add("状态", 100);
        _lsvFiles.Columns.Add("文件名", 170);
        _lsvFiles.Columns.Add("所在文件夹", 180);
        _lsvFiles.Columns.Add("完整路径", 340);
        _lsvFiles.Columns.Add("大小", 90);
        _lsvFiles.Columns.Add("创建时间", 130);
        _lsvFiles.Columns.Add("修改时间", 130);
        _lsvFiles.Columns.Add("路径长", 70);
        _lsvFiles.Columns.Add("名长", 60);
        _lsvFiles.Columns.Add("目录名长", 80);
        _lsvFiles.Columns.Add("层级", 60);
        _lsvFiles.Columns.Add("标记", 120);
        filePanel.Controls.Add(_lsvFiles, 0, 1);
        right.Controls.Add(filePanel, 0, 0);

        _ctxFiles = new ContextMenuStrip();
        _ctxFiles.Items.Add("设为保留（其余标记删除）", null, (_, _) => SetSelectedKeep());
        _ctxFiles.Items.Add("锁定 / 解锁（永不删除）", null, (_, _) => ToggleSelectedLock());
        _ctxFiles.Items.Add("跳过本组", null, (_, _) => { if (_selectedGroup != null) { PolicyEngine.SkipGroup(_selectedGroup); RefreshAll(); } });
        _ctxFiles.Items.Add("在资源管理器中显示", null, (_, _) => OpenSelectedFolder());
        _lsvFiles.ContextMenuStrip = _ctxFiles;

        // 策略面板：固定行高表格 + 自适应控件行，控件不会溢出右半区
        var policy = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(250, 250, 249)
        };
        policy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));  // 0 标题
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));  // 1 规则列表
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // 2 添加规则
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // 3 规则操作
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // 4 参数说明
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));  // 5 应用 / 导出
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));  // 6 策略结果
        policy.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));  // 7 平局说明
        policy.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // 8 剩余空间

        policy.Controls.Add(new Label
        {
            Text = "保留策略（按顺序决胜，选出唯一保留项，其余标记删除）",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(12, 68, 124), Margin = Padding.Empty
        }, 0, 0);

        _lstRules = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Margin = Padding.Empty };
        policy.Controls.Add(_lstRules, 0, 1);

        _cmbRuleKind = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var k in _kindOrder) _cmbRuleKind.Items.Add(new RuleSpec { Kind = k }.KindText);
        _cmbRuleKind.SelectedIndex = 0;
        _cmbRuleDir = new ComboBox { Width = 132, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbRuleDir.Items.AddRange(new object[] { "更短 / 更早优先保留", "更长 / 更新优先保留" });
        _cmbRuleDir.SelectedIndex = 0;
        _txtRuleArg = new TextBox { Width = 90 };
        var btnAddRule = new Button { Text = "添加规则", Width = 78 };
        btnAddRule.Click += (_, _) => AddRule();
        policy.Controls.Add(FillRow(_cmbRuleKind, _cmbRuleDir, _txtRuleArg, btnAddRule), 0, 2);

        var btnDel = new Button { Text = "删除规则", Width = 78 };
        var btnUp = new Button { Text = "↑ 上移", Width = 72 };
        var btnDown = new Button { Text = "↓ 下移", Width = 72 };
        var btnDefaults = new Button { Text = "恢复默认", Width = 78 };
        btnDel.Click += (_, _) => { if (_lstRules.SelectedIndex >= 0) { _rules.RemoveAt(_lstRules.SelectedIndex); RefreshRules(); } };
        btnUp.Click += (_, _) => MoveRule(-1);
        btnDown.Click += (_, _) => MoveRule(1);
        btnDefaults.Click += (_, _) => { _rules.Clear(); _rules.AddRange(RuleSpec.Defaults()); RefreshRules(); };
        policy.Controls.Add(FillRow(btnDel, btnUp, btnDown, btnDefaults), 0, 3);

        policy.Controls.Add(new Label
        {
            Text = "参数：目录 / 磁盘 / 关键字（仅部分规则需要填写）",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty
        }, 0, 4);

        var btnApply = new Button { Text = "应用策略到全部重复组", Width = 190, BackColor = Color.FromArgb(230, 241, 251) };
        btnApply.Click += (_, _) => ApplyPolicy();
        var btnExport = new Button { Text = "导出重复组报告", Width = 140 };
        btnExport.Click += (_, _) => ExportGroupsCsv();
        policy.Controls.Add(FillRow(btnApply, btnExport), 0, 5);

        _lblPolicySummary = new Label { Text = "尚未应用策略", AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(12, 68, 124), Margin = Padding.Empty };
        policy.Controls.Add(_lblPolicySummary, 0, 6);
        policy.Controls.Add(new Label
        {
            Text = "平局兜底：所有规则用完仍并列时，整组标记「需人工确认」，不会自动删除。\n" +
                   "双击左侧重复组可跳过该组；右键组内文件可手动指定保留项或锁定。",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty
        }, 0, 7);

        right.Controls.Add(policy, 0, 1);
        root.Controls.Add(right, 1, 1);
        _steps[2].Controls.Add(root);
        RefreshRules();
    }

    private static ListView NewListView() => new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        MultiSelect = false,
        HideSelection = false,
        Font = new Font("Microsoft YaHei UI", 9F)
    };

    private void BuildStep4()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));    // 汇总
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // 清单
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122F));   // 操作区

        _lblPreviewSummary = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "尚未生成删除预演",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(12, 68, 124),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(4, 2, 4, 2)
        };
        root.Controls.Add(_lblPreviewSummary, 0, 0);

        _lsvPreview = NewListView();
        _lsvPreview.Dock = DockStyle.Fill;
        _lsvPreview.VirtualMode = true;
        _lsvPreview.Columns.Add("状态", 90);
        _lsvPreview.Columns.Add("文件名", 200);
        _lsvPreview.Columns.Add("将删除的完整路径", 620);
        _lsvPreview.Columns.Add("大小", 100);
        _lsvPreview.Columns.Add("重复组", 80);
        _lsvPreview.RetrieveVirtualItem += LsvPreview_RetrieveVirtualItem;
        root.Controls.Add(_lsvPreview, 0, 1);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(250, 250, 249),
            Margin = Padding.Empty,
            Padding = new Padding(8, 4, 8, 4)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

        _rbRecycle = new RadioButton { Text = "移到 Windows 回收站（推荐，可一键还原）", Checked = true, AutoSize = true };
        _rbPermanent = new RadioButton { Text = "永久删除（危险，不可还原）", AutoSize = true };
        _chkConfirm = new CheckBox { Text = "我已核对以上清单，确认执行删除", AutoSize = true, ForeColor = Color.FromArgb(140, 45, 30) };
        var btnExportPreview = new Button { Text = "导出清单 CSV", Width = 130 };
        btnExportPreview.Click += (_, _) => ExportPreviewCsv();
        _btnExecute = new Button
        {
            Text = "执行删除",
            Width = 140,
            Enabled = false,
            BackColor = Color.FromArgb(252, 234, 232)
        };
        _btnExecute.Click += async (_, _) => await ExecuteDeleteAsync();
        _chkConfirm.CheckedChanged += (_, _) => _btnExecute.Enabled = _chkConfirm.Checked && _preview.Count > 0;

        bottom.Controls.Add(FillRow(_rbRecycle, _rbPermanent), 0, 0);
        bottom.Controls.Add(FillRow(_chkConfirm, btnExportPreview, _btnExecute), 0, 1);
        bottom.Controls.Add(new Label
        {
            Text = "执行前会重新校验文件大小与修改时间，扫描后被改动的文件自动跳过；占用、只读或无权限文件不会中断批量。",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty
        }, 0, 2);
        root.Controls.Add(bottom, 0, 2);
        _steps[3].Controls.Add(root);
    }

    private void BuildStep5()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(24)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));    // 结果标题
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // 失败清单
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));    // 按钮
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));    // 说明

        _lblResult = new Label
        {
            Text = "执行结果",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(12, 68, 124),
            Margin = Padding.Empty
        };
        root.Controls.Add(_lblResult, 0, 0);

        _lsvFailures = NewListView();
        _lsvFailures.Dock = DockStyle.Fill;
        _lsvFailures.Columns.Add("状态", 90);
        _lsvFailures.Columns.Add("原路径", 620);
        _lsvFailures.Columns.Add("说明", 280);
        root.Controls.Add(_lsvFailures, 0, 1);

        var btnRestore = new Button { Text = "一键还原本次操作", Width = 170 };
        btnRestore.Click += (_, _) => RestoreLast();
        var btnLogs = new Button { Text = "打开日志目录", Width = 130 };
        btnLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(SafeDelete.LogDir);
            Process.Start(new ProcessStartInfo(SafeDelete.LogDir) { UseShellExecute = true });
        };
        var btnRescan = new Button { Text = "返回重新扫描", Width = 130 };
        btnRescan.Click += (_, _) => ShowStep(0);
        root.Controls.Add(FillRow(btnRestore, btnLogs, btnRescan), 0, 2);
        root.Controls.Add(new Label
        {
            Text = "说明：移到回收站后空间不会立即释放，清空 Windows 回收站后才真正腾出磁盘空间；这也是本工具保留还原能力的设计取舍。",
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = MutedColor, Margin = Padding.Empty
        }, 0, 3);
        _steps[4].Controls.Add(root);
    }

    private ScanOptions ReadOptions() => new()
    {
        Roots = _lstRoots.Items.Cast<string>().ToList(),
        ExcludedDirKeywords = Scanner.ParseList(_txtExDirs.Text),
        ExcludedExtensions = Scanner.ParseList(_txtExExt.Text),
        ExcludeSystemJunkFiles = _chkExcludeJunk.Checked,
        ProtectedDirectories = Scanner.ParseList(_txtProtected.Text),
        MinSizeBytes = (long)_numMinSize.Value * 1024,
        Level = (MatchLevel)_cmbMode.SelectedIndex,
        MaxDegreeOfParallelism = (int)_numThreads.Value,
        SkipSystemDirs = _chkSkipSystem.Checked,
        SkipCloudPlaceholders = _chkSkipCloud.Checked
    };

    private void StartScan()
    {
        _options = ReadOptions();
        if (_options.Roots.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一个扫描位置。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _groups.Clear();
        _preview.Clear();
        _lastBatch = null;
        ShowStep(1);
        _pbMain.Value = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnScanProgress);
        Task.Run(() =>
        {
            var result = Scanner.Run(_options, _cts.Token, progress);
            BeginInvoke(new Action(() => OnScanComplete(result)));
        });
    }

    private void OnScanProgress(ScanProgress p)
    {
        _lblStage.Text = p.Stage;
        _lblProgressDetail.Text = string.IsNullOrEmpty(p.Current) ? p.Extra : "当前：" + p.Current;
        int pct = p.StageIndex switch
        {
            1 => 10,
            2 => 35 + (p.Total > 0 ? (int)(25.0 * p.Processed / p.Total) : 0),
            3 => 60 + (p.Total > 0 ? (int)(20.0 * p.Processed / p.Total) : 0),
            4 => 80 + (p.Total > 0 ? (int)(15.0 * p.Processed / p.Total) : 0),
            5 => 100,
            _ => 0
        };
        _pbMain.Value = Math.Max(0, Math.Min(100, pct));
        _lblProgressCount.Text = p.Total > 0
            ? $"{p.Processed:N0} / {p.Total:N0}　·　{p.Extra}"
            : p.Extra;
    }

    private void OnScanComplete(ScanResult result)
    {
        _scanResult = result;
        if (!string.IsNullOrEmpty(result.Stats.Message) && result.Groups.Count == 0 && !result.Cancelled)
        {
            MessageBox.Show(this, result.Stats.Message, "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowStep(0);
            return;
        }
        _groups.AddRange(result.Groups);
        ApplyPolicy();
        var s = result.Stats;
        _lblScanSummary.Text = $"扫描 {s.TotalEnumerated:N0} 个文件　·　重复组 {s.Groups:N0}　·　冗余副本 {s.RedundantFiles:N0}　·　" +
                               $"预计可释放 {FmtSize(s.ReclaimableBytes)}　·　跳过 {s.SkippedFiles:N0} 个　·　耗时 {s.ElapsedMs / 1000.0:F1}s　·　{_options.LevelText}";
        _lsvGroups.VirtualListSize = _groups.Count;
        _lsvGroups.Invalidate();
        if (_groups.Count > 0) _lsvGroups.SelectedIndices.Add(0);
        ShowStep(2);
    }

    private void LsvGroups_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _groups.Count) { e.Item = new ListViewItem(""); return; }
        var g = _groups[e.ItemIndex];
        // 优先展示本组要保留的那一份；平局组没有定论，标注后展示组内第一份
        var rep = RepresentativeFile(g);
        string fileName = rep?.FileName ?? "";
        if (g.Ambiguous && fileName.Length > 0) fileName = "〔待确认〕" + fileName;

        var item = new ListViewItem("#" + g.Id);
        item.SubItems.Add(fileName);
        item.SubItems.Add(rep?.FullPath ?? "");
        item.SubItems.Add(g.Files.Count.ToString());
        item.SubItems.Add(FmtSize(g.Size));
        item.SubItems.Add(FmtSize(g.ReclaimableBytes));
        item.SubItems.Add(g.StatusText);
        item.SubItems.Add(ShortBasis(g.MatchBasis));
        item.SubItems.Add(g.HasSameName ? "同名" : "异名");
        if (g.Ambiguous) item.BackColor = WarnColor;
        if (g.Skipped) item.ForeColor = MutedColor;
        e.Item = item;
    }

    /// <summary>该组用于列表展示的代表文件：优先保留项，平局时取组内第一份。</summary>
    public static FileEntry RepresentativeFile(DuplicateGroup g) =>
        g.Files.FirstOrDefault(f => f.State is ItemState.Keep or ItemState.Protected or ItemState.Locked)
        ?? g.Files.FirstOrDefault(f => f.State == ItemState.NeedConfirm)
        ?? g.Files.FirstOrDefault();

    /// <summary>按列头索引排序重复组。顺序只影响展示，不影响策略判定与删除结果。</summary>
    public static void SortGroups(List<DuplicateGroup> groups, int column, bool descending)
    {
        Comparison<DuplicateGroup> cmp = column switch
        {
            0 => (a, b) => a.Id.CompareTo(b.Id),
            1 => (a, b) => string.Compare(RepresentativeFile(a)?.FileName ?? "", RepresentativeFile(b)?.FileName ?? "", StringComparison.OrdinalIgnoreCase),
            2 => (a, b) => string.Compare(RepresentativeFile(a)?.FullPath ?? "", RepresentativeFile(b)?.FullPath ?? "", StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => a.Files.Count.CompareTo(b.Files.Count),
            4 => (a, b) => a.Size.CompareTo(b.Size),
            5 => (a, b) => a.ReclaimableBytes.CompareTo(b.ReclaimableBytes),
            6 => (a, b) => string.Compare(a.StatusText, b.StatusText, StringComparison.OrdinalIgnoreCase),
            7 => (a, b) => string.Compare(a.MatchBasis, b.MatchBasis, StringComparison.OrdinalIgnoreCase),
            8 => (a, b) => a.HasSameName.CompareTo(b.HasSameName),
            _ => (a, b) => a.Id.CompareTo(b.Id)
        };
        if (descending) groups.Sort((a, b) => cmp(b, a));
        else groups.Sort(cmp);
    }

    /// <summary>点击列头排序：同列再点切换升降序，数值列默认大在前，列头显示箭头。</summary>
    private void LsvGroups_ColumnClick(object sender, ColumnClickEventArgs e)
    {
        int col = e.Column;
        if (col < 0 || col >= GroupColumnTitles.Length || _groups.Count == 0) return;
        if (_sortColumn == col) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = col;
            _sortDescending = col is 3 or 4 or 5;
        }

        int selectedId = _selectedGroup?.Id ?? -1;
        SortGroups(_groups, _sortColumn, _sortDescending);
        for (int i = 0; i < GroupColumnTitles.Length; i++)
            _lsvGroups.Columns[i].Text = i == _sortColumn
                ? GroupColumnTitles[i] + (_sortDescending ? " ▼" : " ▲")
                : GroupColumnTitles[i];

        _lsvGroups.VirtualListSize = _groups.Count;
        _lsvGroups.Invalidate();
        _lsvGroups.SelectedIndices.Clear();
        int idx = selectedId >= 0 ? _groups.FindIndex(g => g.Id == selectedId) : -1;
        if (idx >= 0) _lsvGroups.SelectedIndices.Add(idx);
        else ShowSelectedGroup();

        // 排序列可能在横向滚动区外，用状态条保证当前排序依据始终可见
        _lblGroupsHeader.Text = $"重复组 · 排序：{GroupColumnTitles[col]} {(_sortDescending ? "降序 ▼" : "升序 ▲")}" +
                                " · 再点同列切换 · 悬停查看全部路径";
    }

    private static string ShortBasis(string s)
    {
        if (s.Contains("逐字节比对")) return "L4 逐字节一致";
        if (s.Contains("未逐字节校验")) return "L3 全文件哈希";
        if (s.Contains("快速粗筛")) return "L2 快速粗筛";
        return s.Length > 14 ? s.Substring(0, 14) + "…" : s;
    }

    /// <summary>鼠标悬停时显示该组全部文件的名称与完整路径，弥补列宽限制。</summary>
    private void LsvGroups_MouseMove(object sender, MouseEventArgs e)
    {
        var hit = _lsvGroups.HitTest(e.X, e.Y);
        int idx = hit.Item?.Index ?? -1;
        if (idx < 0 || idx >= _groups.Count)
        {
            if (_lastGroupTip.Length > 0) { _groupTip.SetToolTip(_lsvGroups, ""); _lastGroupTip = ""; }
            return;
        }
        string text = string.Join("\n", _groups[idx].Files.Select(f => $"[{f.StateText}] {f.FullPath}"));
        if (text == _lastGroupTip) return;
        _groupTip.SetToolTip(_lsvGroups, text);
        _lastGroupTip = text;
    }

    private void LsvPreview_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _preview.Count) { e.Item = new ListViewItem(""); return; }
        var (g, f) = _preview[e.ItemIndex];
        var item = new ListViewItem("待删除");
        item.SubItems.Add(f.FileName);
        item.SubItems.Add(f.FullPath);
        item.SubItems.Add(FmtSize(f.Size));
        item.SubItems.Add("#" + g.Id);
        item.BackColor = DeleteColor;
        e.Item = item;
    }

    private void ShowSelectedGroup()
    {
        if (_lsvGroups.SelectedIndices.Count == 0) return;
        int idx = _lsvGroups.SelectedIndices[0];
        if (idx < 0 || idx >= _groups.Count) return;
        _selectedGroup = _groups[idx];
        var g = _selectedGroup;
        _lblGroupDetail.Text = $"组 #{g.Id}　·　{g.Files.Count} 份　·　每份 {FmtSize(g.Size)}　·　{g.MatchBasis}　·　指纹 {Short(g.Fingerprint)}";
        _lsvFiles.BeginUpdate();
        _lsvFiles.Items.Clear();
        foreach (var f in g.Files)
        {
            var item = new ListViewItem(f.StateText);
            item.SubItems.Add(f.FileName);
            item.SubItems.Add(f.FolderName);
            item.SubItems.Add(f.FullPath);
            item.SubItems.Add(FmtSize(f.Size));
            item.SubItems.Add(f.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(f.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(f.FullPath.Length.ToString());
            item.SubItems.Add(f.FileName.Length.ToString());
            item.SubItems.Add(f.FolderName.Length.ToString());
            item.SubItems.Add(f.Depth.ToString());
            string marks = (f.SameNameInGroup ? "同名 " : "") + (f.IsHardLink ? "硬链接" : "");
            item.SubItems.Add(marks.Trim());
            item.Tag = f;
            item.BackColor = f.State switch
            {
                ItemState.Keep or ItemState.Protected or ItemState.Locked => KeepColor,
                ItemState.Delete => DeleteColor,
                ItemState.NeedConfirm => WarnColor,
                _ => Color.White
            };
            _lsvFiles.Items.Add(item);
        }
        _lsvFiles.EndUpdate();
    }

    private void AddRule()
    {
        if (_cmbRuleKind.SelectedIndex < 0) return;
        var rule = new RuleSpec
        {
            Kind = _kindOrder[_cmbRuleKind.SelectedIndex],
            SmallFirst = _cmbRuleDir.SelectedIndex == 0,
            Argument = _txtRuleArg.Text.Trim()
        };
        if (rule.NeedsArgument && rule.Argument.Length == 0)
        {
            MessageBox.Show(this, "这条规则需要填写参数（目录 / 磁盘 / 关键字）。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _rules.Add(rule);
        RefreshRules();
    }

    private void MoveRule(int delta)
    {
        int i = _lstRules.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        RefreshRules();
        _lstRules.SelectedIndex = j;
    }

    private void RefreshRules()
    {
        _lstRules.Items.Clear();
        for (int i = 0; i < _rules.Count; i++) _lstRules.Items.Add($"{i + 1}. {_rules[i].Display}");
        _cmbRuleDir.Enabled = !(new RuleSpec { Kind = _kindOrder[Math.Max(0, _cmbRuleKind.SelectedIndex)] }).IsBooleanRule;
    }

    private void ApplyPolicy()
    {
        var st = PolicyEngine.Apply(_groups, _rules, _options.ProtectedDirectories);
        _lblPolicySummary.Text = $"策略结果：将删除 {st.DeleteCount:N0} 个副本　·　保留 {st.KeepCount:N0} 个　·　" +
                                 $"需人工确认 {st.NeedConfirmGroups:N0} 组　·　已跳过 {st.SkippedGroups:N0} 组　·　预计可释放 {FmtSize(st.ReclaimableBytes)}";
        RefreshAll();
    }

    private void RefreshAll()
    {
        _lsvGroups.VirtualListSize = _groups.Count;
        _lsvGroups.Invalidate();
        ShowSelectedGroup();
        var st = PolicyEngine.Recount(_groups);
        _lblPolicySummary.Text = $"当前：待删除 {st.DeleteCount:N0} 个　·　保留 {st.KeepCount:N0} 个　·　" +
                                 $"需人工确认 {st.NeedConfirmGroups:N0} 组　·　已跳过 {st.SkippedGroups:N0} 组　·　预计可释放 {FmtSize(st.ReclaimableBytes)}";
    }

    private void SetSelectedKeep()
    {
        if (_selectedGroup == null || _lsvFiles.SelectedIndices.Count == 0) return;
        var f = (FileEntry)_lsvFiles.SelectedItems[0].Tag;
        PolicyEngine.SetKeep(_selectedGroup, f);
        RefreshAll();
    }

    private void ToggleSelectedLock()
    {
        if (_selectedGroup == null || _lsvFiles.SelectedIndices.Count == 0) return;
        var f = (FileEntry)_lsvFiles.SelectedItems[0].Tag;
        PolicyEngine.ToggleLock(_selectedGroup, f);
        ApplyPolicy();
    }

    private void OpenSelectedFolder()
    {
        if (_lsvFiles.SelectedIndices.Count == 0) return;
        var f = (FileEntry)_lsvFiles.SelectedItems[0].Tag;
        try { Process.Start("explorer.exe", "/select,\"" + f.FullPath + "\""); }
        catch { /* 忽略 */ }
    }

    private void BuildPreview()
    {
        _preview = _groups.Where(g => !g.Skipped)
            .SelectMany(g => g.Files.Where(f => f.State == ItemState.Delete).Select(f => (g, f)))
            .ToList();
        _lsvPreview.VirtualListSize = _preview.Count;
        _lsvPreview.Invalidate();
        var st = PolicyEngine.Recount(_groups);
        _lblPreviewSummary.Text = $"删除预演：共 {_preview.Count:N0} 个文件　·　预计释放 {FmtSize(st.ReclaimableBytes)}　·　" +
                                  $"每组强制至少保留 1 份　·　{_options.LevelText}";
        _chkConfirm.Checked = false;
        _btnExecute.Enabled = false;
    }

    private async Task ExecuteDeleteAsync()
    {
        if (_preview.Count == 0 || !_chkConfirm.Checked) return;
        if (_rbPermanent.Checked)
        {
            var answer = MessageBox.Show(this,
                "永久删除不可还原，且不会进入回收站。\n请确认已经备份重要资料。是否继续？",
                "危险操作二次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        _btnExecute.Enabled = false;
        bool permanent = _rbPermanent.Checked;
        var progress = new Progress<ScanProgress>(p => _lblPreviewSummary.Text = $"正在处理 {p.Processed:N0}/{p.Total:N0} …");
        _lastBatch = await Task.Run(() => SafeDelete.Execute(_groups, permanent, true, progress, CancellationToken.None));
        FillResult();
        _chkConfirm.Checked = false;
        ShowStep(4);
    }

    private void FillResult()
    {
        var b = _lastBatch;
        if (b == null) return;
        int failed = b.Records.Count - b.SuccessCount;
        _lblResult.Text = $"执行完成：成功 {b.SuccessCount:N0} 个　·　跳过 / 失败 {failed:N0} 个　·　" +
                          $"涉及 {FmtSize(b.SuccessBytes)}　·　方式：{(b.Permanent ? "永久删除（不可还原）" : "回收站 / 隔离区（可还原）")}";
        _lsvFailures.Items.Clear();
        foreach (var r in b.Records.Where(r => !r.Success))
        {
            var item = new ListViewItem("已跳过");
            item.SubItems.Add(r.OriginalPath);
            item.SubItems.Add(r.Error);
            _lsvFailures.Items.Add(item);
        }
        foreach (var r in b.Records.Where(r => r.Success && r.Error.Length > 0))
        {
            var item = new ListViewItem("提示");
            item.SubItems.Add(r.OriginalPath);
            item.SubItems.Add(r.Error);
            _lsvFailures.Items.Add(item);
        }
    }

    private void RestoreLast()
    {
        var batch = _lastBatch ?? SafeDelete.LoadAll().FirstOrDefault();
        if (batch == null)
        {
            MessageBox.Show(this, "没有可还原的操作记录。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (batch.Permanent)
        {
            MessageBox.Show(this, "本次为永久删除，无法还原。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var (restored, messages) = SafeDelete.Restore(batch);
        string msg = $"已还原 {restored} 个文件到原路径。";
        if (messages.Count > 0) msg += "\n\n" + string.Join("\n", messages.Take(10));
        MessageBox.Show(this, msg, "一键还原", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _lblResult.Text += $"　·　已还原 {restored} 个（建议重新扫描）";
    }

    private void ExportGroupsCsv()
    {
        using var dlg = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "DupClean_重复组报告.csv" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        using var w = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
        w.WriteLine("组号,单份大小,份数,可释放,判定依据,状态,文件名,完整路径");
        foreach (var g in _groups)
            foreach (var f in g.Files)
                w.WriteLine($"#{g.Id},{g.Size},{g.Files.Count},{g.ReclaimableBytes},\"{g.MatchBasis}\",\"{f.StateText}\",\"{f.FileName}\",\"{f.FullPath}\"");
        MessageBox.Show(this, "报告已导出。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportPreviewCsv()
    {
        using var dlg = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "DupClean_删除预演清单.csv" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        using var w = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
        w.WriteLine("组号,文件名,完整路径,大小,指纹");
        foreach (var (g, f) in _preview)
            w.WriteLine($"#{g.Id},\"{f.FileName}\",\"{f.FullPath}\",{f.Size},{g.Fingerprint}");
        MessageBox.Show(this, "删除清单已导出。", "DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "DupClean 重复文件清理工具 v1.0.4\n\n" +
            "· 判重：大小分桶 → 三段采样 → SHA-256 → 逐字节比对（严格模式 100% 强一致）\n" +
            "· 分组：只按内容，不按文件名；同名不同路径、异名同内容均合并\n" +
            "· 策略：可排序规则链，选「保留哪一份」，平局挂起人工确认\n" +
            "· 安全：每组强制留 1 份、删除预演、重校验、回收站、日志 + 一键还原\n" +
            "· 隐私：纯本地运行，不联网、不上传任何文件信息\n" +
            "· 平台：Windows 10 1607+ / Windows 11，单个 EXE 双击即用\n" +
            "· 作者联系方式：WeChat ID " + AuthorWeChat + "\n\n" +
            $"日志目录：{SafeDelete.LogDir}",
            "关于 DupClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GoBack()
    {
        if (_step == 1) { _cts?.Cancel(); ShowStep(0); return; }
        if (_step == 2) { ShowStep(0); return; }
        if (_step == 3) { ShowStep(2); return; }
        if (_step == 4) { ShowStep(0); return; }
    }

    private void GoNext()
    {
        if (_step == 0) StartScan();
        else if (_step == 2) { BuildPreview(); ShowStep(3); }
    }

    /// <summary>仅供 --uishot 界面自检：填入示例重复组，核对新增列的真实显示效果。</summary>
    public void LoadDemoGroupsForCapture()
    {
        _groups.Clear();
        var t = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var samples = new[]
        {
            new { Name = "方案.pptx", Paths = new[] { @"D:\市场部\2024展会\方案.pptx", @"D:\市场部\备份\方案.pptx", @"E:\归档\方案(1).pptx" } },
            new { Name = "报价单.xlsx", Paths = new[] { @"D:\客户\苏州穿山甲\报价单.xlsx", @"E:\旧归档\a7f31.xlsx" } },
            new { Name = "产品演示.mp4", Paths = new[] { @"D:\素材\产品演示.mp4", @"D:\素材\副本\产品演示.mp4", @"F:\备份\产品演示.mp4", @"F:\备份\2023\产品演示.mp4" } }
        };
        int id = 1;
        foreach (var s in samples)
        {
            long size = 24L * 1024 * 1024 + id * 1024 * 137;
            var g = new DuplicateGroup
            {
                Id = id,
                Size = size,
                Fingerprint = "A109F872E2722" + id,
                MatchBasis = "L4 强一致 · 逐字节比对通过",
                ReclaimableBytes = size * (s.Paths.Length - 1),
                CanDelete = true,
                ByteVerified = true,
                Ambiguous = id == 3,
                Skipped = false
            };
            for (int i = 0; i < s.Paths.Length; i++)
                g.Files.Add(new FileEntry
                {
                    FullPath = s.Paths[i],
                    Size = size,
                    CreatedUtc = t,
                    ModifiedUtc = t,
                    State = g.Ambiguous ? ItemState.NeedConfirm : (i == 0 ? ItemState.Keep : ItemState.Delete)
                });
            _groups.Add(g);
            id++;
        }
        _lsvGroups.VirtualListSize = _groups.Count;
        _lsvGroups.Invalidate();
        _lsvGroups.SelectedIndices.Clear();
        _lsvGroups.SelectedIndices.Add(0);
        ShowSelectedGroup();
        _lblScanSummary.Text = "示例数据：扫描 58,361 个文件 · 重复组 3 · 冗余副本 6 · 预计可释放 294.0 MB · 严格模式";
        _lblPolicySummary.Text = "示例数据：待删除 5 个 · 保留 2 个 · 需人工确认 1 组 · 预计可释放 294.0 MB";
    }

    /// <summary>仅供 --uishot 界面自检：模拟点击指定列头，核对排序与箭头提示。</summary>
    public void DemoSortForCapture(int column) =>
        LsvGroups_ColumnClick(_lsvGroups, new ColumnClickEventArgs(column));

    public void ShowStep(int index)
    {
        _step = index;
        for (int i = 0; i < 5; i++) _steps[i].Visible = i == index;
        for (int i = 0; i < 5; i++)
        {
            _stepLabels[i].ForeColor = i == index ? Color.FromArgb(12, 68, 124) : MutedColor;
            _stepLabels[i].Font = new Font("Microsoft YaHei UI", 9F, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        _btnBack.Enabled = index != 1;
        _btnCancelScan.Visible = index == 1;
        switch (index)
        {
            case 0: _btnNext.Text = "开始扫描 →"; _btnNext.Visible = true; _btnBack.Enabled = false; break;
            case 1: _btnNext.Visible = false; break;
            case 2: _btnNext.Text = "下一步：删除预演 →"; _btnNext.Visible = true; break;
            case 3: _btnNext.Visible = false; break;
            case 4: _btnNext.Visible = false; break;
        }
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) || s.Length < 12 ? s : s.Substring(0, 12) + "…";

    public static string FmtSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
