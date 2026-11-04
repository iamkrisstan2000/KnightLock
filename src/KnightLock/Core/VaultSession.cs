using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KnightLock.Core;

/// <summary>
/// A live, unlocked vault. Holds the derived AES key for the lifetime of the session so
/// the vault can be re-sealed (data written back) when the user locks it, without asking
/// for the password again. The key is zeroed the moment the session ends.
/// </summary>
public sealed class VaultSession : IDisposable
{
    private byte[] _key;
    private Dictionary<string, ManifestEntry> _manifest;
    private bool _disposed;

    public string VaultFilePath { get; }
    public VaultHeader Header { get; private set; }
    public string WorkingRoot { get; }
    public string WorkingDirectory { get; }
    public DateTime UnlockedUtc { get; } = DateTime.UtcNow;

    /// <summary>True once the working copy has been wiped (vault is locked).</summary>
    public bool IsLocked { get; private set; }

    internal VaultSession(string vaultFilePath, VaultHeader header, string workingRoot,
                          string workingDirectory, byte[] key, int iterations, byte[] salt)
    {
        VaultFilePath = vaultFilePath;
        Header = header;
        WorkingRoot = workingRoot;
        WorkingDirectory = workingDirectory;
        _key = key;

        _manifest = CaptureManifest();
    }

    public string DisplayName => Header.Name;

    /// <summary>Cheap edit detection: compares sizes and timestamps of the working copy.</summary>
    public VaultChangeSummary DetectChanges()
    {
        var current = CaptureManifest();
        int added = 0, modified = 0, deleted = 0;

        foreach (var kvp in current)
        {
            if (!_manifest.TryGetValue(kvp.Key, out var old)) { added++; continue; }
            if (old.Size != kvp.Value.Size || old.LastWriteUtc != kvp.Value.LastWriteUtc) modified++;
        }

        foreach (var key in _manifest.Keys)
            if (!current.ContainsKey(key)) deleted++;

        return new VaultChangeSummary { Added = added, Modified = modified, Deleted = deleted };
    }

    private Dictionary<string, ManifestEntry> CaptureManifest()
    {
        var map = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(WorkingDirectory)) return map;

        var stack = new Stack<string>();
        stack.Push(WorkingDirectory);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] files, subs;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            try { subs = Directory.GetDirectories(dir); } catch { subs = Array.Empty<string>(); }

            foreach (string f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    map[Path.GetRelativePath(WorkingDirectory, f)] = new ManifestEntry(fi.Length, fi.LastWriteTimeUtc);
                }
                catch { }
            }

            foreach (string s in subs) stack.Push(s);
        }
        return map;
    }

    /// <summary>
    /// Re-encrypts the working copy back into the container (fresh salt-less re-seal: same
    /// KDF salt, same key, but a brand-new random nonce prefix so no nonce is ever reused),
    /// then wipes the plaintext.
    /// </summary>
    public Task<VaultCreateResult> SealAsync(IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Seal(progress, ct), ct);

    private VaultCreateResult Seal(IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        if (IsLocked) throw new VaultException(VaultError.Unknown, "This vault is already locked.");
        if (_key.Length == 0) throw new VaultException(VaultError.Unknown, "The session key is no longer available.");

        var sw = Stopwatch.StartNew();
        var reporter = new ThrottledReporter(progress, ct);

        reporter.Report(VaultProgress.Of(VaultPhase.Scanning, "Scanning changes…"));
        var snapshot = FolderSnapshot.Take(Path.GetFullPath(WorkingDirectory), Array.Empty<string>(), ct);

        string tempPath = VaultFilePath + ".kltmp";
        byte[] noncePrefix = VaultCrypto.RandomBytes(VaultFormat.NoncePrefixSize);

        var newHeader = new VaultHeader
        {
            Version = VaultFormat.CurrentVersion,
            VaultId = Header.VaultId,
            Name = Header.Name,
            CreatedUtc = Header.CreatedUtc,
            ModifiedUtc = DateTime.UtcNow,
            Kdf = Header.Kdf,
            Cipher = Header.Cipher,
            NoncePrefixBase64 = Convert.ToBase64String(noncePrefix),
            ChunkSize = Header.ChunkSize,
            KeyCheckBase64 = Header.KeyCheckBase64,
            FileCount = snapshot.Files.Count,
            SourceBytes = snapshot.TotalBytes,
            SourceFolder = Header.SourceFolder
        };

        try
        {
            byte[] headerBytes = newHeader.Serialize();
            byte[] fingerprint = VaultHeader.Fingerprint(headerBytes);

            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                             1 << 20, FileOptions.SequentialScan))
            {
                VaultService.WriteFixedHeader(file, headerBytes);

                using var encryptor = new VaultEncryptStream(file, _key, noncePrefix, fingerprint, newHeader.ChunkSize);
                using (var zip = new ZipArchive(encryptor, ZipArchiveMode.Create, leaveOpen: true))
                {
                    VaultService.WriteZip(zip, snapshot, reporter, VaultPhase.Sealing, "Sealing", ct);
                }
                encryptor.Finish();
                file.Flush(flushToDisk: true);
            }

            long newSize = new FileInfo(tempPath).Length;

            reporter.Report(VaultProgress.Of(VaultPhase.Sealing, "Replacing container…", 99));

            // Swap the container in one step; if this throws the plaintext is kept intact.
            if (File.Exists(VaultFilePath))
            {
                try { File.Replace(tempPath, VaultFilePath, null); }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(VaultFilePath);
                    File.Move(tempPath, VaultFilePath);
                }
            }
            else
            {
                File.Move(tempPath, VaultFilePath);
            }

            Header = newHeader;

            reporter.Report(VaultProgress.Of(VaultPhase.Wiping, "Wiping decrypted working copy…", 100));
            Wipe();

            reporter.Report(VaultProgress.Of(VaultPhase.Done, "Vault sealed and locked", 100));

            return new VaultCreateResult
            {
                VaultFilePath = VaultFilePath,
                VaultId = newHeader.VaultId,
                FileCount = snapshot.Files.Count,
                FolderCount = snapshot.EmptyDirectories.Count,
                SourceBytes = snapshot.TotalBytes,
                VaultBytes = newSize,
                Duration = sw.Elapsed,
                Skipped = snapshot.Skipped.AsReadOnly()
            };
        }
        finally
        {
            VaultService.TryDeleteFile(tempPath);
        }
    }

    /// <summary>Securely wipes the decrypted working copy and drops the session key.</summary>
    public void Wipe()
    {
        try
        {
            SecureDelete.WipeDirectory(WorkingRoot);
        }
        catch { }

        try { if (Directory.Exists(WorkingRoot)) Directory.Delete(WorkingRoot, recursive: true); } catch { }

        _manifest.Clear();
        IsLocked = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!IsLocked)
        {
            try { Wipe(); } catch { }
        }

        if (_key.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = Array.Empty<byte>();
        }
    }
}
