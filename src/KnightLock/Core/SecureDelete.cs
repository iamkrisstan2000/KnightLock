using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace KnightLock.Core;

/// <summary>
/// Best-effort secure deletion: every file is overwritten in place with random data
/// (with a final zero pass) and flushed to the physical device before the directory
/// entry is removed. On SSD/flash media wear-levelling means no software wipe is a
/// guarantee — KnightLock is explicit about that in the UI.
/// </summary>
public static class SecureDelete
{
    public const int DefaultPasses = 1;
    private const int BufferSize = 256 * 1024;

    public static void WipeDirectory(string directory, int passes = DefaultPasses, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        foreach (string file in SafeEnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            WipeFile(file, passes);
        }

        // Remove directories deepest-first, tolerating leftovers.
        List<string> dirs = SafeEnumerateDirectories(directory);
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            TryDeleteDirectory(dir);
        }
        TryDeleteDirectory(directory);
    }

    public static bool WipeFile(string path, int passes = DefaultPasses)
    {
        try
        {
            if (!File.Exists(path)) return true;

            if (OperatingSystem.IsWindows())
                File.SetAttributes(path, FileAttributes.Normal);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                                           BufferSize, FileOptions.WriteThrough))
            {
                long length = fs.Length;
                if (length > 0)
                {
                    byte[] buffer = new byte[Math.Min(BufferSize, Math.Max(1, length))];
                    for (int pass = 0; pass < Math.Max(1, passes); pass++)
                    {
                        fs.Position = 0;
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int chunk = (int)Math.Min(buffer.Length, remaining);
                            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                            fs.Write(buffer, 0, chunk);
                            remaining -= chunk;
                        }
                        fs.Flush(flushToDisk: true);
                    }

                    // Final zero pass scrubs the residual random pattern.
                    fs.Position = 0;
                    Array.Clear(buffer);
                    long rem = length;
                    while (rem > 0)
                    {
                        int chunk = (int)Math.Min(buffer.Length, rem);
                        fs.Write(buffer, 0, chunk);
                        rem -= chunk;
                    }
                    fs.Flush(flushToDisk: true);
                    CryptographicOperations.ZeroMemory(buffer);
                }

                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }

            string scrambled = Path.Combine(Path.GetDirectoryName(path) ?? "",
                                            Guid.NewGuid().ToString("N") + ".kltmp");
            try { File.Move(path, scrambled); } catch { scrambled = path; }
            File.Delete(scrambled);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (string f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs) stack.Push(s);
        }
    }

    private static List<string> SafeEnumerateDirectories(string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs) { result.Add(s); stack.Push(s); }
        }
        return result;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: false); }
        catch { /* leave it; a later pass or the OS temp cleaner will finish the job */ }
    }
}

/// <summary>Path helpers that keep very long Windows paths working.</summary>
public static class IOPath
{
    public static string Long(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.Length < 240) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    public static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "Vault";
        return name.Length > 64 ? name[..64] : name;
    }
}
