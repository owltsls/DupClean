using System.Security.Cryptography;

namespace DupClean;

public static class Hashing
{
    public const int SampleChunk = 64 * 1024;
    private const int StreamBuffer = 1024 * 1024;
    private const int RecallOnOpen = 0x00040000;
    private const int RecallOnDataAccess = 0x00400000;

    public static bool IsCloudPlaceholder(FileAttributes attributes)
        => ((int)attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;

    private static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        StreamBuffer, FileOptions.SequentialScan);

    public static string ComputeSample(string path, long size)
    {
        if (size <= SampleChunk * 3L) return ComputeFull(path);
        using var fs = OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[SampleChunk];
        byte[] sizeBytes = BitConverter.GetBytes(size);
        sha.AppendData(sizeBytes);

        ReadAt(fs, chunk, 0);
        sha.AppendData(chunk);
        ReadAt(fs, chunk, size / 2 - SampleChunk / 2);
        sha.AppendData(chunk);
        ReadAt(fs, chunk, size - SampleChunk);
        sha.AppendData(chunk);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void ReadAt(FileStream fs, byte[] buffer, long offset)
    {
        fs.Position = offset;
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read < buffer.Length) Array.Clear(buffer, read, buffer.Length - read);
    }

    public static string ComputeFull(string path)
    {
        using var fs = OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static bool ByteEqual(string first, string second)
    {
        var fi = new FileInfo(first);
        var fj = new FileInfo(second);
        if (!fi.Exists || !fj.Exists || fi.Length != fj.Length) return false;
        using var a = OpenRead(first);
        using var b = OpenRead(second);
        byte[] ba = new byte[StreamBuffer];
        byte[] bb = new byte[StreamBuffer];
        while (true)
        {
            int na = ReadBlock(a, ba);
            int nb = ReadBlock(b, bb);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
