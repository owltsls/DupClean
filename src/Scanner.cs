using System.Diagnostics;

namespace DupClean;

public static class Scanner
{
    private sealed class Throttle
    {
        private readonly object _gate = new();
        private long _last;
        private readonly IProgress<ScanProgress> _progress;

        public Throttle(IProgress<ScanProgress> progress) => _progress = progress;

        public void Report(ScanProgress p, int minMs = 130)
        {
            if (_progress == null) return;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                if (now - _last < minMs) return;
                _last = now;
            }
            _progress.Report(p);
        }
    }

    public static ScanResult Run(ScanOptions options, CancellationToken token, IProgress<ScanProgress> progress)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();
        var stats = result.Stats;
        var throttle = new Throttle(progress);

        try
        {
            var files = Enumerate(options, stats, token, throttle);
            stats.TotalEnumerated = files.Count;

            var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
            stats.SizeBuckets = bySize.Count;
            var candidates = bySize.SelectMany(g => g).ToList();
            stats.SampleCandidates = candidates.Count;

            int done = 0;
            Parallel.ForEach(candidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = token
            }, f =>
            {
                token.ThrowIfCancellationRequested();
                try { f.SampleHash = Hashing.ComputeSample(f.FullPath, f.Size); }
                catch { f.SampleHash = "ERR:" + Guid.NewGuid().ToString("N"); }
                int d = Interlocked.Increment(ref done);
                throttle.Report(new ScanProgress
                {
                    Stage = "② 三段采样哈希", StageIndex = 2,
                    Processed = d, Total = candidates.Count,
                    Current = f.FullPath,
                    Extra = $"大小分桶 {stats.SizeBuckets} 组 · 候选 {candidates.Count} 个文件"
                });
            });

            var bySample = candidates
                .Where(f => !f.SampleHash.StartsWith("ERR:"))
                .GroupBy(f => $"{f.Size}:{f.SampleHash}")
                .Where(g => g.Count() > 1)
                .ToList();

            List<List<FileEntry>> rawGroups;

            if (options.Level == MatchLevel.Quick)
            {
                rawGroups = bySample.Select(g => g.ToList()).ToList();
            }
            else
            {
                var fullCandidates = bySample.SelectMany(g => g).ToList();
                stats.FullHashCandidates = fullCandidates.Count;
                done = 0;
                Parallel.ForEach(fullCandidates, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                    CancellationToken = token
                }, f =>
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        f.FullHash = f.Size <= Hashing.SampleChunk * 3L
                            ? f.SampleHash
                            : Hashing.ComputeFull(f.FullPath);
                    }
                    catch { f.FullHash = "ERR:" + Guid.NewGuid().ToString("N"); }
                    int d = Interlocked.Increment(ref done);
                    throttle.Report(new ScanProgress
                    {
                        Stage = "③ 全文件 SHA-256 哈希", StageIndex = 3,
                        Processed = d, Total = fullCandidates.Count,
                        Current = f.FullPath,
                        Extra = $"流式分块 1 MB · {options.MaxDegreeOfParallelism} 并发"
                    });
                });

                var byFull = fullCandidates
                    .Where(f => !f.FullHash.StartsWith("ERR:"))
                    .GroupBy(f => $"{f.Size}:{f.FullHash}")
                    .Where(g => g.Count() > 1)
                    .ToList();

                if (options.Level == MatchLevel.Balanced)
                {
                    rawGroups = byFull.Select(g => g.ToList()).ToList();
                }
                else
                {
                    rawGroups = new List<List<FileEntry>>();
                    int verified = 0;
                    foreach (var g in byFull)
                    {
                        token.ThrowIfCancellationRequested();
                        var partitions = new List<(FileEntry Rep, List<FileEntry> Members)>();
                        foreach (var f in g)
                        {
                            token.ThrowIfCancellationRequested();
                            bool matched = false;
                            foreach (var p in partitions)
                            {
                                if (Hashing.ByteEqual(p.Rep.FullPath, f.FullPath))
                                {
                                    p.Members.Add(f);
                                    matched = true;
                                    break;
                                }
                            }
                            if (!matched) partitions.Add((f, new List<FileEntry> { f }));
                        }
                        foreach (var p in partitions.Where(p => p.Members.Count > 1))
                            rawGroups.Add(p.Members);
                        verified++;
                        throttle.Report(new ScanProgress
                        {
                            Stage = "④ 逐字节比对（强一致确认）", StageIndex = 4,
                            Processed = verified, Total = byFull.Count,
                            Current = g.Key,
                            Extra = "删除前的最终 100% 校验"
                        }, 60);
                    }
                }
            }

            int id = 1;
            foreach (var raw in rawGroups.OrderByDescending(g => g.Count * g[0].Size))
            {
                foreach (var f in raw)
                {
                    try { NativeMethods.GetLinkInfo(f.FullPath, f); }
                    catch { /* 无法读取链接信息时按普通文件处理 */ }
                }
                var dupNames = raw.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in raw) f.SameNameInGroup = dupNames.Contains(f.FileName);

                int identities = raw.Select(f => f.PhysicalId).Distinct().Count();
                long reclaimable = identities > 1 ? raw[0].Size * (identities - 1) : 0;

                var group = new DuplicateGroup
                {
                    Id = id++,
                    Fingerprint = raw[0].FullHash.Length > 0 && !raw[0].FullHash.StartsWith("ERR:")
                        ? raw[0].FullHash
                        : raw[0].SampleHash,
                    Size = raw[0].Size,
                    Files = raw.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
                    ByteVerified = options.Level == MatchLevel.Strict,
                    CanDelete = options.Level != MatchLevel.Quick,
                    ReclaimableBytes = reclaimable,
                    MatchBasis = options.Level switch
                    {
                        MatchLevel.Strict => "L4 强一致 · 逐字节比对通过",
                        MatchLevel.Balanced => "L3 高置信 · 未逐字节校验",
                        _ => "L2 快速粗筛 · 禁止直接删除"
                    }
                };
                result.Groups.Add(group);
            }

            stats.Groups = result.Groups.Count;
            stats.RedundantFiles = result.Groups.Sum(g => g.Files.Count - 1);
            stats.ReclaimableBytes = result.Groups.Sum(g => g.ReclaimableBytes);
            stats.Message = "扫描完成";
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            stats.Message = "扫描已取消";
        }
        catch (Exception ex)
        {
            stats.Message = "扫描出错：" + ex.Message;
        }

        stats.ElapsedMs = sw.ElapsedMilliseconds;
        progress?.Report(new ScanProgress
        {
            Stage = result.Cancelled ? "已取消" : "扫描完成",
            StageIndex = 5,
            Processed = 1, Total = 1,
            Extra = stats.Message
        });
        return result;
    }

    /// <summary>
    /// 系统常见临时 / 日志 / 缓存 / 备份 / 下载未完成 / 崩溃转储 / 编辑器交换文件。
    /// 勾选「一键排除」时由界面填入排除扩展名输入框，用户仍可继续手动增删。
    /// 不包含 .pdb / .obj / .pyc 等编译产物：它们可能是用户真正想清理的重复文件。
    /// </summary>
    public static readonly string[] SystemJunkExtensions =
    {
        ".tmp", ".temp", ".log", ".bak", ".old", ".dmp", ".mdmp", ".chk", ".gid",
        ".fts", ".ftg", ".wbk", ".xlk", ".asd", ".swp", ".swo", ".prv", ".sik",
        ".part", ".partial", ".crdownload", ".download", ".unconfirmed", ".!ut",
        ".cache", ".tlog", ".err", ".tmp~", ".~tmp", ".$$$", ".~lock"
    };

    /// <summary>无扩展名规律、只能按固定文件名识别的系统常见文件。</summary>
    public static readonly HashSet<string> SystemJunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "ehthumbs.db", "desktop.ini", ".ds_store", "iconcache.db"
    };

    private static List<FileEntry> Enumerate(ScanOptions opt, ScanStats stats,
        CancellationToken token, Throttle throttle)
    {
        var list = new List<FileEntry>();
        var stack = new Stack<string>();
        foreach (var root in opt.Roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(root)) stack.Push(root);
        }
        var excludes = opt.ExcludedExtensions
            .Select(x => x.StartsWith(".") ? x : "." + x)
            .Where(x => x.Length > 1)
            .ToArray();
        int dirsDone = 0;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = stack.Pop();
            if (IsExcludedDirectory(dir, opt))
            {
                stats.SkippedDirectories++;
                continue;
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var subAttrs = File.GetAttributes(sub);
                        if ((subAttrs & FileAttributes.ReparsePoint) != 0)
                        {
                            stats.SkippedDirectories++;
                            continue;
                        }
                    }
                    catch { stats.SkippedDirectories++; continue; }
                    stack.Push(sub);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        if (!fi.Exists || fi.Length < opt.MinSizeBytes) continue;
                        if (opt.ExcludeSystemJunkFiles)
                        {
                            string leaf = Path.GetFileName(file);
                            // ~$ 开头是 Office 打开文档时生成的临时锁文件
                            if (SystemJunkFileNames.Contains(leaf) || leaf.StartsWith("~$", StringComparison.Ordinal))
                            {
                                stats.SkippedFiles++;
                                continue;
                            }
                        }
                        if (excludes.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                        {
                            stats.SkippedFiles++;
                            continue;
                        }
                        var attrs = fi.Attributes;
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            stats.SkippedFiles++;
                            continue;
                        }
                        bool cloud = Hashing.IsCloudPlaceholder(attrs);
                        if (cloud && opt.SkipCloudPlaceholders)
                        {
                            stats.SkippedFiles++;
                            continue;
                        }
                        list.Add(new FileEntry
                        {
                            FullPath = file,
                            Size = fi.Length,
                            CreatedUtc = fi.CreationTimeUtc,
                            ModifiedUtc = fi.LastWriteTimeUtc,
                            Attributes = attrs,
                            IsCloudPlaceholder = cloud
                        });
                    }
                    catch { stats.SkippedFiles++; }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                stats.SkippedDirectories++;
            }

            dirsDone++;
            throttle.Report(new ScanProgress
            {
                Stage = "① 枚举文件（跳过系统目录与链接）", StageIndex = 1,
                Processed = list.Count, Total = 0,
                Current = dir,
                Extra = $"已发现 {list.Count:N0} 个文件 · 已遍历 {dirsDone:N0} 个目录 · 跳过 {stats.SkippedFiles:N0} 个"
            }, 180);
        }
        return list;
    }

    private static bool IsExcludedDirectory(string dir, ScanOptions opt)
    {
        string lower = dir.ToLowerInvariant();
        foreach (var keyword in opt.ExcludedDirKeywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) && lower.Contains(keyword.Trim().ToLowerInvariant()))
                return true;
        }
        if (lower.Contains(@"\dupclean_data")) return true;
        if (lower.Contains(@"\$recycle.bin") || lower.Contains(@"\system volume information")) return true;
        if (!opt.SkipSystemDirs) return false;

        foreach (var p in DefaultSystemPaths())
        {
            if (string.IsNullOrEmpty(p)) continue;
            string pl = p.ToLowerInvariant().TrimEnd('\\');
            if (lower == pl || lower.StartsWith(pl + @"\", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IEnumerable<string> DefaultSystemPaths()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "ProgramData");
    }

    public static List<string> ParseList(string text) => (text ?? "")
        .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
