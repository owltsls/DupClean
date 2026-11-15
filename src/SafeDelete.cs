using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace DupClean;

public static class SafeDelete
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "DupClean_Data");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string QuarantineDir => Path.Combine(DataDir, "quarantine");

    public static DeleteBatch Execute(List<DuplicateGroup> groups, bool permanent, bool revalidate,
        IProgress<ScanProgress> progress, CancellationToken token)
    {
        var batch = new DeleteBatch
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),
            TimeUtc = DateTime.UtcNow,
            Permanent = permanent
        };
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(QuarantineDir);

        var targets = groups.Where(g => !g.Skipped).SelectMany(g => g.Files)
            .Where(f => f.State == ItemState.Delete)
            .ToList();
        int index = 0;

        foreach (var f in targets)
        {
            token.ThrowIfCancellationRequested();
            var record = new DeleteRecord
            {
                GroupId = groups.First(g => g.Files.Contains(f)).Id,
                Fingerprint = groups.First(g => g.Files.Contains(f)).Fingerprint,
                OriginalPath = f.FullPath,
                Size = f.Size,
                ModifiedUtc = f.ModifiedUtc
            };

            if (!groups.First(g => g.Files.Contains(f)).CanDelete)
            {
                record.Error = "快速模式粗筛结果不可直接删除";
                batch.Records.Add(record);
                continue;
            }

            if (revalidate && !Revalidate(f, out string reason))
            {
                record.Error = reason;
                batch.Records.Add(record);
                continue;
            }

            try
            {
                if (permanent)
                {
                    File.Delete(f.FullPath);
                    record.Method = "永久删除";
                    record.Success = true;
                }
                else
                {
                    var (ok, error) = NativeMethods.SendToRecycleBin(f.FullPath);
                    if (ok && !File.Exists(f.FullPath))
                    {
                        record.Method = "回收站";
                        record.Success = true;
                    }
                    else
                    {
                        string dest = Path.Combine(QuarantineDir, batch.Id,
                            Interlocked.Increment(ref index).ToString("D5") + "_" + f.FileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (File.Exists(dest)) dest = Path.Combine(Path.GetDirectoryName(dest)!, Guid.NewGuid().ToString("N") + "_" + f.FileName);
                        File.Move(f.FullPath, dest);
                        record.Method = "隔离区";
                        record.RestorePath = dest;
                        record.Success = true;
                        record.Error = string.IsNullOrEmpty(error) ? "" : "回收站不可用，已转入隔离区：" + error;
                    }
                }
            }
            catch (Exception ex)
            {
                record.Error = ex.Message;
            }

            batch.Records.Add(record);
            progress?.Report(new ScanProgress
            {
                Stage = "正在处理删除", StageIndex = 5,
                Processed = batch.Records.Count, Total = targets.Count,
                Current = f.FullPath,
                Extra = $"成功 {batch.SuccessCount} · 失败 {batch.Records.Count - batch.SuccessCount}"
            });
            f.State = record.Success ? ItemState.Delete : ItemState.Skipped;
        }

        Save(batch);
        return batch;
    }

    private static bool Revalidate(FileEntry f, out string reason)
    {
        reason = "";
        try
        {
            var fi = new FileInfo(f.FullPath);
            if (!fi.Exists) { reason = "文件已不存在"; return false; }
            if (fi.Length != f.Size) { reason = "文件大小已变化，跳过以防误删"; return false; }
            if (Math.Abs((fi.LastWriteTimeUtc - f.ModifiedUtc).TotalSeconds) > 2)
            {
                reason = "文件修改时间已变化，跳过以防误删";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "无法读取文件：" + ex.Message;
            return false;
        }
    }

    public static void Save(DeleteBatch batch)
    {
        Directory.CreateDirectory(LogDir);
        File.WriteAllText(Path.Combine(LogDir, batch.Id + ".json"),
            JsonSerializer.Serialize(batch, JsonOptions));
    }

    public static List<DeleteBatch> LoadAll()
    {
        var list = new List<DeleteBatch>();
        if (!Directory.Exists(LogDir)) return list;
        foreach (var file in Directory.EnumerateFiles(LogDir, "*.json").OrderByDescending(f => f))
        {
            try
            {
                var batch = JsonSerializer.Deserialize<DeleteBatch>(File.ReadAllText(file));
                if (batch != null) list.Add(batch);
            }
            catch { /* 忽略损坏的日志 */ }
        }
        return list;
    }

    public static (int Restored, List<string> Messages) Restore(DeleteBatch batch)
    {
        int restored = 0;
        var messages = new List<string>();
        foreach (var rec in batch.Records.Where(r => r.Success && !batch.Permanent))
        {
            try
            {
                if (rec.Method == "隔离区")
                {
                    if (!File.Exists(rec.RestorePath)) { messages.Add("隔离区文件已不存在：" + rec.OriginalPath); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(rec.OriginalPath)!);
                    if (File.Exists(rec.OriginalPath))
                    {
                        messages.Add("原路径已被占用，未覆盖：" + rec.OriginalPath);
                        continue;
                    }
                    File.Move(rec.RestorePath, rec.OriginalPath);
                    restored++;
                }
                else if (RestoreFromRecycleBin(rec))
                {
                    restored++;
                }
                else
                {
                    messages.Add("未能自动还原，请在 Windows 回收站中手动还原：" + rec.OriginalPath);
                }
            }
            catch (Exception ex)
            {
                messages.Add("还原失败：" + rec.OriginalPath + " · " + ex.Message);
            }
        }
        return (restored, messages);
    }

    private static bool RestoreFromRecycleBin(DeleteRecord rec)
        => TryComRestore(rec) || TryMetadataRestore(rec);

    private static bool TryComRestore(DeleteRecord rec)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;
            object shell = Activator.CreateInstance(shellType);
            object bin = Invoke(shell, "NameSpace", 10);
            if (bin == null) return false;
            object items = Invoke(bin, "Items");
            if (items is not IEnumerable allItems) return false;

            string expectedDir = Path.GetDirectoryName(rec.OriginalPath) ?? "";
            string expectedName = Path.GetFileName(rec.OriginalPath);

            foreach (object item in allItems)
            {
                string itemName = Convert.ToString(Invoke(bin, "GetDetailsOf", item, 0)) ?? "";
                string itemDir = Convert.ToString(Invoke(bin, "GetDetailsOf", item, 1)) ?? "";
                if (!itemName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)) continue;
                if (itemDir.Length > 0 && !Normalize(itemDir).Equals(Normalize(expectedDir), StringComparison.OrdinalIgnoreCase))
                    continue;

                object verbs = GetProperty(item, "Verbs");
                if (verbs is IEnumerable verbList)
                {
                    foreach (object verb in verbList)
                    {
                        string name = Convert.ToString(GetProperty(verb, "Name")) ?? "";
                        if (name.Contains("还原") || name.Contains("復原") || name.Contains("复原") ||
                            name.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Invoke(verb, "DoIt");
                            return true;
                        }
                    }
                }
                Invoke(item, "InvokeVerb", "还原");
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMetadataRestore(DeleteRecord rec)
    {
        try
        {
            string volume = Path.GetPathRoot(rec.OriginalPath);
            if (string.IsNullOrEmpty(volume)) return false;
            string binRoot = Path.Combine(volume, "$Recycle.Bin");
            if (!Directory.Exists(binRoot)) return false;

            foreach (string sidDir in Directory.EnumerateDirectories(binRoot))
            {
                List<string> metas;
                try { metas = Directory.EnumerateFiles(sidDir, "$I*").ToList(); }
                catch { continue; }
                foreach (string meta in metas)
                {
                    try
                    {
                        byte[] raw = File.ReadAllBytes(meta);
                        if (raw.Length < 28) continue;
                        string original = System.Text.Encoding.Unicode
                            .GetString(raw, 28, raw.Length - 28).TrimEnd('\0', '\r', '\n');
                        if (!original.Equals(rec.OriginalPath, StringComparison.OrdinalIgnoreCase)) continue;
                        string suffix = Path.GetFileName(meta).Substring(2);
                        string dataFile = Path.Combine(sidDir, "$R" + suffix);
                        if (!File.Exists(dataFile)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(rec.OriginalPath)!);
                        if (File.Exists(rec.OriginalPath)) return false;
                        File.Move(dataFile, rec.OriginalPath);
                        try { File.Delete(meta); } catch { }
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    private static object Invoke(object target, string name, params object[] args) => target.GetType()
        .InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            null, target, args);

    private static object GetProperty(object target, string name) => target.GetType()
        .InvokeMember(name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, target, null);
}
