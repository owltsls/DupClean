namespace DupClean;

public enum MatchLevel
{
    Strict = 0,
    Balanced = 1,
    Quick = 2
}

public enum RuleKind
{
    Depth,
    PathLength,
    FileNameLength,
    FolderNameLength,
    CreationTime,
    ModifiedTime,
    PreferDirectory,
    PreferDrive,
    KeywordPenalty,
    CopyNamePenalty,
    ReadOnlyPenalty
}

public enum ItemState
{
    Unknown,
    Keep,
    Delete,
    NeedConfirm,
    Protected,
    Locked,
    Skipped
}

public sealed class RuleSpec
{
    public RuleKind Kind { get; set; }
    public bool SmallFirst { get; set; } = true;
    public string Argument { get; set; } = "";

    public bool NeedsArgument => Kind is RuleKind.PreferDirectory or RuleKind.PreferDrive or RuleKind.KeywordPenalty;
    public bool IsBooleanRule => Kind is RuleKind.PreferDirectory or RuleKind.PreferDrive or RuleKind.KeywordPenalty
        or RuleKind.CopyNamePenalty or RuleKind.ReadOnlyPenalty;

    public string DirectionText => IsBooleanRule
        ? "命中优先"
        : (SmallFirst ? "更短 / 更早优先" : "更长 / 更新优先");

    public string KindText => Kind switch
    {
        RuleKind.Depth => "路径层级",
        RuleKind.PathLength => "完整路径长度",
        RuleKind.FileNameLength => "文件名长度",
        RuleKind.FolderNameLength => "文件夹名长度",
        RuleKind.CreationTime => "创建时间",
        RuleKind.ModifiedTime => "修改时间",
        RuleKind.PreferDirectory => "优先保留目录",
        RuleKind.PreferDrive => "优先保留磁盘",
        RuleKind.KeywordPenalty => "关键字降权",
        RuleKind.CopyNamePenalty => "副本命名降权",
        RuleKind.ReadOnlyPenalty => "只读 / 隐藏降权",
        _ => Kind.ToString()
    };

    public string Display => $"{KindText}（{DirectionText}）{(string.IsNullOrEmpty(Argument) ? "" : " · " + Argument)}";

    public static List<RuleSpec> Defaults() => new()
    {
        new RuleSpec { Kind = RuleKind.Depth, SmallFirst = true },
        new RuleSpec { Kind = RuleKind.ModifiedTime, SmallFirst = false },
        new RuleSpec { Kind = RuleKind.FileNameLength, SmallFirst = true }
    };
}

public sealed class ScanOptions
{
    public List<string> Roots { get; set; } = new();
    public List<string> ExcludedDirKeywords { get; set; } = new();
    public List<string> ExcludedExtensions { get; set; } = new();
    public List<string> ProtectedDirectories { get; set; } = new();
    /// <summary>一键排除系统常见文件：除扩展名清单外，另按固定文件名 / ~$ 前缀忽略。</summary>
    public bool ExcludeSystemJunkFiles { get; set; } = false;
    public long MinSizeBytes { get; set; } = 1024;
    public MatchLevel Level { get; set; } = MatchLevel.Strict;
    public bool SkipSystemDirs { get; set; } = true;
    public bool SkipCloudPlaceholders { get; set; } = true;
    public int MaxDegreeOfParallelism { get; set; } = 4;

    public string LevelText => Level switch
    {
        MatchLevel.Strict => "严格模式 · 逐字节比对（100% 强一致）",
        MatchLevel.Balanced => "均衡模式 · 全文件哈希（高置信，未逐字节校验）",
        _ => "快速模式 · 仅粗筛（禁止直接删除）"
    };
}

public sealed class FileEntry
{
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public FileAttributes Attributes { get; set; }
    public bool IsCloudPlaceholder { get; set; }
    public bool IsHardLink { get; set; }
    public uint VolumeSerial { get; set; }
    public uint FileIndexHigh { get; set; }
    public uint FileIndexLow { get; set; }
    public int LinkCount { get; set; }
    public string SampleHash { get; set; } = "";
    public string FullHash { get; set; } = "";

    public ItemState State { get; set; } = ItemState.Unknown;
    public bool Locked { get; set; }
    public bool SameNameInGroup { get; set; }

    public string FileName => Path.GetFileName(FullPath);
    public string DirectoryName => Path.GetDirectoryName(FullPath) ?? "";
    public string FolderName => new DirectoryInfo(DirectoryName).Name;
    public int Depth => FullPath.Count(c => c == '\\' || c == '/');
    public string PhysicalId => LinkCount > 1
        ? $"{VolumeSerial}:{FileIndexHigh}:{FileIndexLow}"
        : FullPath;

    public string StateText => State switch
    {
        ItemState.Keep => "保留",
        ItemState.Delete => "待删除",
        ItemState.NeedConfirm => "需人工确认",
        ItemState.Protected => "受保护 · 保留",
        ItemState.Locked => "已锁定 · 保留",
        ItemState.Skipped => "跳过",
        _ => "未决定"
    };
}

public sealed class DuplicateGroup
{
    public int Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public long Size { get; set; }
    public bool ByteVerified { get; set; }
    public bool CanDelete { get; set; }
    public bool Ambiguous { get; set; }
    public bool Skipped { get; set; }
    public long ReclaimableBytes { get; set; }
    public string MatchBasis { get; set; } = "";
    public List<FileEntry> Files { get; set; } = new();

    public int DeleteCount => Files.Count(f => f.State == ItemState.Delete);
    public bool HasSameName => Files.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
    public string StatusText => Skipped ? "已跳过" : Ambiguous ? "需人工确认" : (DeleteCount > 0 ? "已决策" : "待决策");
}

public sealed class ScanStats
{
    public int TotalEnumerated;
    public int SkippedFiles;
    public int SkippedDirectories;
    public int SizeBuckets;
    public int SampleCandidates;
    public int FullHashCandidates;
    public int Groups;
    public int RedundantFiles;
    public long ReclaimableBytes;
    public long ElapsedMs;
    public string Message = "";
}

public sealed class ScanProgress
{
    public string Stage { get; set; } = "";
    public int StageIndex { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public string Current { get; set; } = "";
    public int GroupsFound { get; set; }
    public string Extra { get; set; } = "";
}

public sealed class ScanResult
{
    public List<DuplicateGroup> Groups { get; set; } = new();
    public ScanStats Stats { get; set; } = new();
    public bool Cancelled { get; set; }
}

public sealed class DeleteRecord
{
    public int GroupId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public bool Success { get; set; }
    public string Method { get; set; } = "";
    public string RestorePath { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class DeleteBatch
{
    public string Id { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public bool Permanent { get; set; }
    public List<DeleteRecord> Records { get; set; } = new();

    public int SuccessCount => Records.Count(r => r.Success);
    public long SuccessBytes => Records.Where(r => r.Success).Sum(r => r.Size);
}

public sealed class PolicyStats
{
    public int Groups;
    public int KeepCount;
    public int DeleteCount;
    public int NeedConfirmGroups;
    public int SkippedGroups;
    public long ReclaimableBytes;
}
