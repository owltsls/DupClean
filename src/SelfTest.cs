namespace DupClean;

public static class SelfTest
{
    private static int _pass;
    private static int _fail;
    private static readonly System.Text.StringBuilder _log = new();

    private static void Log(string line)
    {
        _log.AppendLine(line);
        Console.WriteLine(line);
    }

    private static void Flush()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "DupClean_Data");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "selftest.log"), _log.ToString(), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    public static int Run(string parent)
    {
        string parentDir = string.IsNullOrWhiteSpace(parent)
            ? Path.Combine(Path.GetTempPath(), "DupClean_SelfTest")
            : parent;
        string root = Path.Combine(parentDir, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        Log("测试根目录：" + root);
        try
        {
            CaseA(root);
            CaseB(root);
            CaseC(root);
            CaseD(root);
            CaseE(root);
            CaseF(root);
        }
        catch (Exception ex)
        {
            Log("自测异常：" + ex);
            _fail++;
        }
        finally
        {
            TryClean(root);
        }
        Log($"=== PASS {_pass} / FAIL {_fail} ===");
        Flush();
        return _fail == 0 ? 0 : 1;
    }

    private static void CaseE(string root)
    {
        Log("[用例 E] 一键排除系统常见临时 / 日志文件");
        string dir = Path.Combine(root, "caseE");
        var data = Make(12 * 1024, 88);
        var t = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Write(dir, @"正常\资料.dat", data, t, t);
        Write(dir, @"正常\缓存.tmp", data, t, t);
        Write(dir, @"正常\运行.log", data, t, t);
        Write(dir, @"正常\备份.bak", data, t, t);
        Write(dir, @"正常\下载中.crdownload", data, t, t);
        Write(dir, @"正常\thumbs.db", data, t, t);
        Write(dir, @"正常\desktop.ini", data, t, t);
        Write(dir, @"正常\~$资料.dat", data, t, t);

        var enabled = Options(dir);
        enabled.ExcludeSystemJunkFiles = true;
        enabled.ExcludedExtensions = Scanner.SystemJunkExtensions.ToList();
        var filtered = Scanner.Run(enabled, CancellationToken.None, null);
        Check("T-13 一键排除后只剩 1 个正常文件",
            filtered.Stats.TotalEnumerated == 1, "enumerated=" + filtered.Stats.TotalEnumerated);

        var disabled = Scanner.Run(Options(dir), CancellationToken.None, null);
        Check("T-13 未勾选时系统文件仍参与扫描",
            disabled.Stats.TotalEnumerated == 8, "enumerated=" + disabled.Stats.TotalEnumerated);
    }

    private static void CaseF(string root)
    {
        Log("[用例 F] 重复组列头排序");
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] names = { "zeta.pptx", "alpha.pptx", "mid.pptx" };
        long[] sizes = { 1000, 9000, 5000 };
        var groups = new List<DuplicateGroup>();
        for (int i = 0; i < 3; i++)
            groups.Add(new DuplicateGroup
            {
                Id = 10 - i,
                Size = sizes[i],
                ReclaimableBytes = sizes[i] * 2,
                MatchBasis = "L4 强一致 · 逐字节比对通过",
                Files = new List<FileEntry>
                {
                    new() { FullPath = @"D:\data\" + names[i], Size = sizes[i], CreatedUtc = t, ModifiedUtc = t, State = ItemState.Keep }
                }
            });

        MainForm.SortGroups(groups, 1, false);
        Check("T-14 列头排序：按文件名升序",
            groups[0].Files[0].FileName == "alpha.pptx" && groups[2].Files[0].FileName == "zeta.pptx",
            "order=" + string.Join(",", groups.Select(g => g.Files[0].FileName)));
        MainForm.SortGroups(groups, 5, true);
        Check("T-14 列头排序：按可释放空间降序",
            groups[0].ReclaimableBytes == 18000 && groups[2].ReclaimableBytes == 2000,
            "order=" + string.Join(",", groups.Select(g => g.ReclaimableBytes)));
        MainForm.SortGroups(groups, 0, false);
        Check("T-14 列头排序：按组号升序",
            groups[0].Id == 8 && groups[2].Id == 10,
            "order=" + string.Join(",", groups.Select(g => g.Id)));
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition) { _pass++; Log("  PASS  " + name); }
        else { _fail++; Log("  FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    private static ScanOptions Options(string dir, MatchLevel level = MatchLevel.Strict) => new()
    {
        Roots = new List<string> { dir },
        MinSizeBytes = 1024,
        Level = level,
        SkipSystemDirs = false,
        SkipCloudPlaceholders = true,
        MaxDegreeOfParallelism = 2
    };

    private static List<RuleSpec> Rules() => new()
    {
        new RuleSpec { Kind = RuleKind.Depth, SmallFirst = true },
        new RuleSpec { Kind = RuleKind.ModifiedTime, SmallFirst = false },
        new RuleSpec { Kind = RuleKind.FileNameLength, SmallFirst = true }
    };

    private static byte[] Make(int size, int seed)
    {
        var random = new Random(seed);
        var data = new byte[size];
        random.NextBytes(data);
        return data;
    }

    private static string Write(string root, string relative, byte[] data, DateTime created, DateTime modified)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, modified);
        return path;
    }

    private static void CaseA(string root)
    {
        Log("[用例 A] 判重 / 分组 / 规则链 / 锁定 / 删除还原");
        string dir = Path.Combine(root, "caseA");
        var content = Make(48 * 1024, 11);
        var sameNameContent = Make(24 * 1024, 22);
        var sameNameDiff = (byte[])sameNameContent.Clone();
        sameNameDiff[123] ^= 0xFF;
        var unique = Make(24 * 1024, 33);

        var t0 = new DateTime(2022, 8, 19, 10, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2024, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);

        Write(dir, @"A\方案.pptx", content, t1, t3);
        Write(dir, @"A\备份\方案.pptx", content, t1, t1);
        Write(dir, @"临时\方案(1).pptx", content, t2, t2);
        Write(dir, @"归档\a7f31.pptx", content, t0, t0);
        Write(dir, @"同名冲突\x\报告.docx", sameNameContent, t1, t1);
        Write(dir, @"同名冲突\y\报告.docx", sameNameDiff, t1, t1);
        Write(dir, @"独立\唯一.dat", unique, t1, t1);
        Write(dir, @"小文件\tiny.txt", Make(64, 44), t1, t1);

        var result = Scanner.Run(Options(dir), CancellationToken.None, null);
        Check("T-01 严格模式恰好发现 1 个重复组", result.Groups.Count == 1, "groups=" + result.Groups.Count);
        if (result.Groups.Count == 0) return;
        var group = result.Groups[0];

        Check("T-01 同名不同路径 + 异名同内容合并为 4 份",
            group.Files.Count == 4 && group.HasSameName && group.Files.Any(f => f.FileName == "a7f31.pptx"),
            "files=" + group.Files.Count);
        Check("T-01 结果为 L4 强一致（逐字节比对通过）", group.ByteVerified && group.CanDelete);
        Check("T-02 同名同大小但差 1 字节不成组",
            !result.Groups.Any(g => g.Files.Any(f => f.FileName == "报告.docx")));
        Check("T-11 小于阈值的文件被忽略", result.Stats.TotalEnumerated == 7,
            "enumerated=" + result.Stats.TotalEnumerated);

        var stats = PolicyEngine.Apply(result.Groups, Rules(), new List<string>());
        var winner = group.Files.SingleOrDefault(f => f.State == ItemState.Keep);
        Check("T-03 规则链精确保留 D 盘工作目录的新版本，删除其余 3 份",
            winner != null && winner.FullPath.EndsWith(@"\A\方案.pptx", StringComparison.OrdinalIgnoreCase) && stats.DeleteCount == 3,
            "winner=" + (winner?.FullPath ?? "none") + " delete=" + stats.DeleteCount);

        var locked = group.Files.First(f => f.FullPath.Contains("临时"));
        locked.Locked = true;
        PolicyEngine.Apply(result.Groups, Rules(), new List<string>());
        Check("T-05 手动锁定项永远保留", locked.State == ItemState.Locked && group.DeleteCount == 3,
            "state=" + locked.StateText);
        locked.Locked = false;
        PolicyEngine.Apply(result.Groups, Rules(), new List<string>());

        var quick = Scanner.Run(Options(dir, MatchLevel.Quick), CancellationToken.None, null);
        PolicyEngine.Apply(quick.Groups, Rules(), new List<string>());
        Check("T-12 快速模式结果禁止直接删除",
            quick.Groups.Count > 0 && quick.Groups.All(g => !g.CanDelete) && quick.Groups.Sum(g => g.DeleteCount) == 0);

        var batch = SafeDelete.Execute(result.Groups, false, true, null, CancellationToken.None);
        Check("T-07 批量删除 3 个副本成功", batch.SuccessCount == 3,
            "success=" + batch.SuccessCount + " firstError=" + (batch.Records.FirstOrDefault(r => !r.Success)?.Error ?? ""));
        Check("T-07 删除后原路径不再存在文件",
            group.Files.Where(f => f.State == ItemState.Delete).All(f => !File.Exists(f.FullPath)));
        var (restored, messages) = SafeDelete.Restore(batch);
        bool allBack = group.Files.Where(f => f.State == ItemState.Delete).All(f => File.Exists(f.FullPath));
        Check("T-07 一键还原全部回到原路径", restored == 3 || allBack,
            "restored=" + restored + " messages=" + string.Join(" | ", messages.Take(3)));
        try { File.Delete(Path.Combine(SafeDelete.LogDir, batch.Id + ".json")); } catch { }
    }

    private static void CaseB(string root)
    {
        Log("[用例 B] 规则链平局兜底");
        string dir = Path.Combine(root, "caseB");
        var data = Make(16 * 1024, 55);
        var t = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Write(dir, @"x1\aa.bin", data, t, t);
        Write(dir, @"x2\bb.bin", data, t, t);
        Write(dir, @"x3\cc.bin", data, t, t);

        var rules = new List<RuleSpec>
        {
            new() { Kind = RuleKind.Depth, SmallFirst = true },
            new() { Kind = RuleKind.PathLength, SmallFirst = true },
            new() { Kind = RuleKind.FileNameLength, SmallFirst = true },
            new() { Kind = RuleKind.FolderNameLength, SmallFirst = true },
            new() { Kind = RuleKind.CreationTime, SmallFirst = true },
            new() { Kind = RuleKind.ModifiedTime, SmallFirst = true }
        };
        var result = Scanner.Run(Options(dir), CancellationToken.None, null);
        var stats = PolicyEngine.Apply(result.Groups, rules, new List<string>());
        Check("T-04 所有规则并列时不自动删除任何文件",
            result.Groups.Count == 1 && result.Groups[0].Ambiguous &&
            stats.NeedConfirmGroups == 1 && stats.DeleteCount == 0,
            "groups=" + result.Groups.Count + " delete=" + stats.DeleteCount);
    }

    private static void CaseC(string root)
    {
        Log("[用例 C] NTFS 硬链接识别");
        string dir = Path.Combine(root, "caseC");
        var data = Make(20 * 1024, 66);
        var t = new DateTime(2024, 5, 5, 8, 0, 0, DateTimeKind.Utc);
        string original = Write(dir, "base.bin", data, t, t);
        string link = Path.Combine(dir, "link.bin");
        if (!NativeMethods.CreateHardLinkW(link, original, IntPtr.Zero))
        {
            Check("T-09 创建硬链接", false, "CreateHardLinkW 失败");
            return;
        }
        var result = Scanner.Run(Options(dir), CancellationToken.None, null);
        var group = result.Groups.FirstOrDefault();
        Check("T-09 硬链接被识别且不计入可释放空间",
            group != null && group.Files.Count == 2 && group.Files.All(f => f.IsHardLink) && group.ReclaimableBytes == 0,
            group == null ? "no group" : $"files={group.Files.Count} links={group.Files.Count(f => f.IsHardLink)} reclaim={group.ReclaimableBytes}");
    }

    private static void CaseD(string root)
    {
        Log("[用例 D] 删除前重校验");
        string dir = Path.Combine(root, "caseD");
        var data = Make(18 * 1024, 77);
        var t = new DateTime(2024, 7, 7, 8, 0, 0, DateTimeKind.Utc);
        string one = Write(dir, "one.bin", data, t, t);
        string two = Write(dir, "two.bin", data, t, t);
        var result = Scanner.Run(Options(dir), CancellationToken.None, null);
        if (result.Groups.Count == 0) { Check("T-06 构造重复组", false); return; }
        var group = result.Groups[0];
        PolicyEngine.SetKeep(group, group.Files.First(f => f.FullPath == one));
        using (var fs = new FileStream(two, FileMode.Append, FileAccess.Write)) fs.WriteByte(0x7F);
        var batch = SafeDelete.Execute(result.Groups, false, true, null, CancellationToken.None);
        Check("T-06 扫描后被改动的文件自动跳过",
            batch.Records.Count == 1 && !batch.Records[0].Success && batch.Records[0].Error.Contains("变化"),
            batch.Records.Count == 0 ? "no record" : batch.Records[0].Error);
        try { File.Delete(Path.Combine(SafeDelete.LogDir, batch.Id + ".json")); } catch { }
    }

    private static void TryClean(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Log("已清理测试数据目录");
        }
        catch (Exception ex)
        {
            Log("测试数据清理未完成（可手动删除）：" + root + " · " + ex.Message);
        }
    }
}
