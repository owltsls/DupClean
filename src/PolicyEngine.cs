namespace DupClean;

public static class PolicyEngine
{
    private static readonly string[] CopyPatterns =
        { "(1)", "(2)", "副本", "复件", "copy", "复制", " - 副本", "新建" };

    public static double Key(FileEntry e, RuleSpec rule)
    {
        string arg = (rule.Argument ?? "").Trim();
        return rule.Kind switch
        {
            RuleKind.Depth => e.Depth,
            RuleKind.PathLength => e.FullPath.Length,
            RuleKind.FileNameLength => e.FileName.Length,
            RuleKind.FolderNameLength => e.FolderName.Length,
            RuleKind.CreationTime => e.CreatedUtc.Ticks / 10000.0,
            RuleKind.ModifiedTime => e.ModifiedUtc.Ticks / 10000.0,
            RuleKind.PreferDirectory => arg.Length > 0 && e.FullPath.StartsWith(arg.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            RuleKind.PreferDrive => arg.Length > 0 && string.Equals(Path.GetPathRoot(e.FullPath), NormalizeDrive(arg), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            RuleKind.KeywordPenalty => arg.Length > 0 && e.FullPath.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1,
            RuleKind.CopyNamePenalty => CopyPatterns.Any(p => e.FileName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) ? 0 : 1,
            RuleKind.ReadOnlyPenalty => (e.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0 ? 0 : 1,
            _ => 0
        };
    }

    private static string NormalizeDrive(string arg)
    {
        string a = arg.Trim();
        if (a.Length == 1) return a.ToUpperInvariant() + ":\\";
        if (a.Length == 2 && a[1] == ':') return a.ToUpperInvariant() + "\\";
        return a;
    }

    private static bool IsProtected(FileEntry e, List<string> protectedDirs)
        => protectedDirs.Any(d => !string.IsNullOrWhiteSpace(d) &&
            e.FullPath.StartsWith(d.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public static PolicyStats Apply(List<DuplicateGroup> groups, List<RuleSpec> rules, List<string> protectedDirs)
    {
        foreach (var g in groups)
        {
            g.Ambiguous = false;
            foreach (var f in g.Files) f.State = ItemState.Unknown;

            if (g.Skipped)
            {
                foreach (var f in g.Files) f.State = ItemState.Skipped;
                continue;
            }

            var anchors = g.Files
                .Where(f => f.Locked || IsProtected(f, protectedDirs))
                .Distinct()
                .ToList();

            if (anchors.Count > 0)
            {
                foreach (var a in anchors)
                    a.State = a.Locked ? ItemState.Locked : ItemState.Protected;
                foreach (var f in g.Files.Where(f => !anchors.Contains(f)))
                    f.State = ItemState.Delete;
            }
            else if (!g.CanDelete)
            {
                foreach (var f in g.Files) f.State = ItemState.NeedConfirm;
                g.Ambiguous = true;
            }
            else
            {
                var candidates = g.Files.ToList();
                foreach (var rule in rules)
                {
                    if (candidates.Count <= 1) break;
                    var keys = candidates.ToDictionary(f => f, f => Key(f, rule));
                    double best = rule.SmallFirst ? keys.Values.Min() : keys.Values.Max();
                    candidates = candidates.Where(f => Math.Abs(keys[f] - best) < 0.000001).ToList();
                }

                if (candidates.Count == 1)
                {
                    var winner = candidates[0];
                    winner.State = ItemState.Keep;
                    foreach (var f in g.Files.Where(f => !ReferenceEquals(f, winner)))
                        f.State = ItemState.Delete;
                }
                else
                {
                    g.Ambiguous = true;
                    foreach (var f in g.Files) f.State = ItemState.NeedConfirm;
                }
            }
        }
        return Recount(groups);
    }

    public static PolicyStats Recount(List<DuplicateGroup> groups)
    {
        var st = new PolicyStats();
        foreach (var g in groups)
        {
            st.Groups++;
            if (g.Skipped) { st.SkippedGroups++; continue; }
            if (g.Ambiguous) st.NeedConfirmGroups++;
            st.KeepCount += g.Files.Count(f => f.State is ItemState.Keep or ItemState.Protected or ItemState.Locked);
            st.DeleteCount += g.DeleteCount;
            st.ReclaimableBytes += Math.Min(g.ReclaimableBytes, (long)g.DeleteCount * g.Size);
        }
        return st;
    }

    public static void SetKeep(DuplicateGroup group, FileEntry entry)
    {
        group.Skipped = false;
        group.Ambiguous = false;
        foreach (var f in group.Files)
            f.State = ReferenceEquals(f, entry) ? ItemState.Keep : ItemState.Delete;
    }

    public static void SkipGroup(DuplicateGroup group)
    {
        group.Skipped = true;
        group.Ambiguous = false;
        foreach (var f in group.Files) f.State = ItemState.Skipped;
    }

    public static void ToggleLock(DuplicateGroup group, FileEntry entry)
    {
        entry.Locked = !entry.Locked;
    }
}
