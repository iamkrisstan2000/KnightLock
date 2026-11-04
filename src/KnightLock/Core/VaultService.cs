using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KnightLock.Core;

/// <summary>
/// High level vault operations: create, open, seal, wipe.
/// Everything here is UI-agnostic and runs off the UI thread.
/// </summary>
public static class VaultService
{
    public const string VaultExtension = ".vault";

    public static string EnsureVaultExtension(string path) =>
        path.EndsWith(VaultExtension, StringComparison.OrdinalIgnoreCase) ? path : path + VaultExtension;

    // ---------------------------------------------------------------------
    // CREATE
    // ---------------------------------------------------------------------
    public static Task<VaultCreateResult> CreateAsync(CreateVaultRequest request,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Create(request, progress, ct), ct);

    private static VaultCreateResult Create(CreateVaultRequest request, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var reporter = new ThrottledReporter(progress, ct);

        if (string.IsNullOrWhiteSpace(request.SourceFolder) || !Directory.Exists(request.SourceFolder))
            throw new VaultException(VaultError.InvalidPath, "The source folder does not exist.");

        var assessment = PasswordStrength.Evaluate(request.Password);
        if (!assessment.MeetsMinimum)
            throw new VaultException(VaultError.PasswordTooWeak, assessment.Hint);

        string sourceRoot = Path.GetFullPath(request.SourceFolder).TrimEnd(Path.DirectorySeparatorChar);
        string vaultPath = Path.GetFullPath(EnsureVaultExtension(request.VaultFilePath));

        if (Directory.Exists(vaultPath))
            throw new VaultException(VaultError.InvalidPath, "The destination must be a file path, not a folder.");

        string? destDir = Path.GetDirectoryName(vaultPath);
        if (string.IsNullOrEmpty(destDir))
            throw new VaultException(VaultError.InvalidPath, "Choose a destination folder for the vault file.");
        Directory.CreateDirectory(destDir);

        if (File.Exists(vaultPath) && !request.Overwrite)
            throw new VaultException(VaultError.AlreadyExists, "A vault file already exists at that location.");

        string tempPath = vaultPath + ".kltmp";
        string noncePrefix = Convert.ToBase64String(VaultCrypto.RandomBytes(VaultFormat.NoncePrefixSize));
        byte[] salt = VaultCrypto.RandomBytes(VaultFormat.SaltSize);

        reporter.Report(VaultProgress.Of(VaultPhase.Scanning, "Scanning folder contents…"));

        // Never try to encrypt the vault file into itself.
        var exclusions = new[] { vaultPath, tempPath };
        FolderSnapshot snapshot = FolderSnapshot.Take(sourceRoot, exclusions, ct);

        if (snapshot.Files.Count == 0 && snapshot.EmptyDirectories.Count == 0)
            throw new VaultException(VaultError.InvalidPath, "That folder is empty — there is nothing to lock up.");

        reporter.Report(VaultProgress.Of(VaultPhase.DerivingKey,
            $"Deriving 256-bit key ({request.Iterations:N0} PBKDF2 rounds)…"));

        byte[] key = VaultCrypto.DeriveKey(request.Password, salt, request.Iterations, ct);
        byte[] fingerprint;
        VaultHeader header;

        try
        {
            header = new VaultHeader
            {
                VaultId = Guid.NewGuid().ToString("D"),
                Name = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? Path.GetFileName(sourceRoot)
                    : request.DisplayName.Trim(),
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                Kdf = new KdfDescriptor
                {
                    Algorithm = "PBKDF2-HMAC-SHA256",
                    Iterations = request.Iterations,
                    SaltBase64 = Convert.ToBase64String(salt)
                },
                NoncePrefixBase64 = noncePrefix,
                ChunkSize = VaultFormat.ChunkSize,
                KeyCheckBase64 = Convert.ToBase64String(VaultCrypto.ComputeKeyCheck(key, salt)),
                FileCount = snapshot.Files.Count,
                SourceBytes = snapshot.TotalBytes,
                SourceFolder = sourceRoot
            };

            if (header.Name.Length == 0) header.Name = "Vault";

            byte[] headerBytes = header.Serialize();
            fingerprint = VaultHeader.Fingerprint(headerBytes);

            long writtenEncrypted = 0;

            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                             1 << 20, FileOptions.SequentialScan))
            {
                WriteFixedHeader(file, headerBytes);

                using var encryptor = new VaultEncryptStream(file, key, header.NoncePrefix, fingerprint,
                    header.ChunkSize, onChunkWritten: b => Interlocked.Exchange(ref writtenEncrypted, b));

                using (var zip = new ZipArchive(encryptor, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteZip(zip, snapshot, reporter, VaultPhase.Compressing, "Encrypting", ct);
                }

                encryptor.Finish();
                file.Flush(flushToDisk: true);
            }

            reporter.Report(VaultProgress.Of(VaultPhase.Verifying, "Sealing container…", 100));

            if (File.Exists(vaultPath)) File.Delete(vaultPath);
            File.Move(tempPath, vaultPath);

            long vaultBytes = new FileInfo(vaultPath).Length;
            reporter.Report(VaultProgress.Of(VaultPhase.Done, "Vault created", 100));

            return new VaultCreateResult
            {
                VaultFilePath = vaultPath,
                VaultId = header.VaultId,
                FileCount = snapshot.Files.Count,
                FolderCount = snapshot.EmptyDirectories.Count,
                SourceBytes = snapshot.TotalBytes,
                VaultBytes = vaultBytes,
                Duration = sw.Elapsed,
                Skipped = snapshot.Skipped.AsReadOnly()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            TryDeleteFile(tempPath);
        }
    }

    // ---------------------------------------------------------------------
    // OPEN
    // ---------------------------------------------------------------------
    public static Task<VaultOpenResult> OpenAsync(string vaultFilePath, string password, string sessionRoot,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Open(vaultFilePath, password, sessionRoot, progress, ct), ct);

    private static VaultOpenResult Open(string vaultFilePath, string password, string sessionRoot,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var reporter = new ThrottledReporter(progress, ct);

        if (string.IsNullOrWhiteSpace(vaultFilePath) || !File.Exists(vaultFilePath))
            throw new VaultException(VaultError.Io, "That vault file could not be found. It may have been moved or deleted.");

        if (string.IsNullOrEmpty(password))
            throw new VaultException(VaultError.WrongPassword, "Enter the master password for this vault.");

        string vaultPath = Path.GetFullPath(vaultFilePath);

        reporter.Report(VaultProgress.Of(VaultPhase.Preparing, "Reading vault header…"));

        VaultHeader header;
        byte[] fingerprint;
        int headerBytesOnDisk;
        using (var container = new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              256 * 1024, FileOptions.SequentialScan))
        {
            header = VaultHeader.ReadFrom(container, out byte[] rawHeader, out fingerprint);
            headerBytesOnDisk = VaultFormat.HeaderOffset + rawHeader.Length;
        }

        reporter.Report(VaultProgress.Of(VaultPhase.DerivingKey,
            $"Deriving 256-bit key ({header.Kdf.Iterations:N0} PBKDF2 rounds)…"));

        byte[] key = VaultCrypto.DeriveKey(password, header.Kdf.Salt, header.Kdf.Iterations, ct);

        // The key is zeroed on the way out UNLESS the session takes ownership of it.
        bool handedOffToSession = false;

        try
        {
            if (!VaultCrypto.VerifyKeyCheck(key, header.Kdf.Salt, header.KeyCheck))
                throw new VaultException(VaultError.WrongPassword,
                    "That master password is not correct for this vault. Check Caps Lock and try again.");

            Directory.CreateDirectory(sessionRoot);
            string workingRoot = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N")[..12]);
            string extractRoot = Path.Combine(workingRoot, IOPath.SanitizeFolderName(header.Name));
            Directory.CreateDirectory(extractRoot);

            string tempZip = Path.Combine(workingRoot, "payload.klz");

            long containerLength = new FileInfo(vaultPath).Length;

            reporter.Report(VaultProgress.Of(VaultPhase.Decrypting, "Decrypting container…", 0));

            try
            {
                using (var container = new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                      256 * 1024, FileOptions.SequentialScan))
                {
                    long headerBytesConsumed = headerBytesOnDisk;
                    using var zipOut = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None,
                                                      1 << 20, FileOptions.SequentialScan);

                    // Skip the fixed header + JSON header: the chunk stream starts right after it.
                    container.Position = headerBytesOnDisk;

                    VaultDecryptor.DecryptTo(container, zipOut, key, header.NoncePrefix, fingerprint,
                        header.ChunkSize, processed =>
                        {
                            double pct = containerLength <= headerBytesConsumed
                                ? 0
                                : Math.Min(88.0, 88.0 * processed / Math.Max(1, containerLength - headerBytesConsumed));
                            reporter.Report(new VaultProgress
                            {
                                Phase = VaultPhase.Decrypting,
                                Message = "Decrypting container…",
                                Percent = pct,
                                BytesProcessed = processed
                            });
                        });

                    zipOut.Flush(flushToDisk: false);
                }

                reporter.Report(VaultProgress.Of(VaultPhase.Extracting, "Restoring files…", 90));
                ExtractZip(tempZip, extractRoot, reporter, ct);
            }
            finally
            {
                SecureDelete.WipeFile(tempZip);
            }

            var session = new VaultSession(vaultPath, header, workingRoot, extractRoot, key,
                                           header.Kdf.Iterations, header.Kdf.Salt);

            reporter.Report(VaultProgress.Of(VaultPhase.Done, "Vault unlocked", 100));
            handedOffToSession = true; // the session now owns (and will zero) the key

            return new VaultOpenResult
            {
                Session = session,
                VaultBytes = containerLength,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            if (!handedOffToSession) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Confirms a password against the header verifier without decrypting the payload.
    /// Used to sanity-check a freshly written container before destructive operations.
    /// </summary>
    public static bool VerifyPassword(string vaultFilePath, string password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        VaultHeader header = Inspect(vaultFilePath);
        byte[] key = VaultCrypto.DeriveKey(password, header.Kdf.Salt, header.Kdf.Iterations);
        try
        {
            return VaultCrypto.VerifyKeyCheck(key, header.Kdf.Salt, header.KeyCheck);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Read the public header of a vault without needing the password.</summary>
    public static VaultHeader Inspect(string vaultFilePath)
    {
        if (!File.Exists(vaultFilePath))
            throw new VaultException(VaultError.Io, "That vault file could not be found.");

        using var container = new FileStream(vaultFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return VaultHeader.ReadFrom(container, out _, out _);
    }

    // ---------------------------------------------------------------------
    // Shared container helpers
    // ---------------------------------------------------------------------
    internal static void WriteFixedHeader(Stream stream, byte[] headerBytes)
    {
        Span<byte> fixedPart = stackalloc byte[VaultFormat.HeaderOffset];
        VaultFormat.Magic.CopyTo(fixedPart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            fixedPart[VaultFormat.VersionOffset..], VaultFormat.CurrentVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            fixedPart[VaultFormat.FlagsOffset..], VaultFormat.SupportedFlags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            fixedPart[VaultFormat.HeaderLengthOffset..], (uint)headerBytes.Length);
        stream.Write(fixedPart);
        stream.Write(headerBytes, 0, headerBytes.Length);
    }

    internal static void WriteZip(ZipArchive zip, FolderSnapshot snapshot, ThrottledReporter reporter,
        VaultPhase phase, string verb, CancellationToken ct)
    {
        const int copyBuffer = 128 * 1024;
        byte[] buffer = new byte[copyBuffer];
        long processed = 0;
        int index = 0;
        long total = Math.Max(1, snapshot.TotalBytes);

        foreach (var dir in snapshot.EmptyDirectories)
        {
            ct.ThrowIfCancellationRequested();
            zip.CreateEntry(dir.Replace('\\', '/').TrimEnd('/') + "/");
        }

        try
        {
            foreach (var file in snapshot.Files)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                ZipArchiveEntry entry = zip.CreateEntry(file.RelativePath.Replace('\\', '/'),
                    CompressionLevel.Optimal);
                entry.LastWriteTime = ClampZipTime(file.LastWriteUtc);

                using var source = new FileStream(IOPath.Long(file.FullPath), FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, copyBuffer, FileOptions.SequentialScan);
                using var target = entry.Open();

                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    processed += read;

                    reporter.Report(new VaultProgress
                    {
                        Phase = phase,
                        Message = $"{verb} {index:N0} of {snapshot.Files.Count:N0} — {file.RelativePath}",
                        Percent = Math.Min(99.5, 100.0 * processed / total),
                        BytesProcessed = processed,
                        TotalBytes = snapshot.TotalBytes,
                        ItemsProcessed = index,
                        TotalItems = snapshot.Files.Count
                    });
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static void ExtractZip(string zipPath, string destinationRoot, ThrottledReporter reporter, CancellationToken ct)
    {
        const int copyBuffer = 128 * 1024;
        byte[] buffer = new byte[copyBuffer];
        string rootFull = Path.GetFullPath(destinationRoot);
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull : rootFull + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(zipPath);
        int total = Math.Max(1, zip.Entries.Count);
        int index = 0;

        try
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(rootFull, relative));

                // Zip-slip guard: an entry may never escape the extraction root.
                if (!target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, rootFull, StringComparison.OrdinalIgnoreCase))
                    throw new VaultException(VaultError.Corrupt,
                        "The vault contains an unsafe file path and was not extracted.");

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(IOPath.Long(target));
                    continue;
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(IOPath.Long(parent));

                using (var input = entry.Open())
                using (var output = new FileStream(IOPath.Long(target), FileMode.Create, FileAccess.Write,
                                                   FileShare.None, copyBuffer, FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                }

                try { File.SetLastWriteTimeUtc(IOPath.Long(target), entry.LastWriteTime.UtcDateTime); } catch { }

                reporter.Report(new VaultProgress
                {
                    Phase = VaultPhase.Extracting,
                    Message = $"Restoring file {index:N0} of {total:N0} — {entry.FullName}",
                    Percent = Math.Min(99.5, 100.0 * index / total),
                    ItemsProcessed = index,
                    TotalItems = total
                });
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static DateTimeOffset ClampZipTime(DateTime utc)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        if (utc < min) utc = min;
        if (utc > max) utc = max;
        return new DateTimeOffset(utc);
    }

    internal static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Progress reporter that avoids flooding the dispatcher and respects cancellation.</summary>
internal sealed class ThrottledReporter
{
    private readonly IProgress<VaultProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastMs = -1000;
    private VaultPhase _lastPhase = (VaultPhase)(-1);

    public ThrottledReporter(IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        _progress = progress;
        _ct = ct;
    }

    public void Report(VaultProgress value)
    {
        _ct.ThrowIfCancellationRequested();
        if (_progress is null) return;

        long now = _clock.ElapsedMilliseconds;
        if (value.Phase == _lastPhase && now - _lastMs < 60) return;

        _lastMs = now;
        _lastPhase = value.Phase;
        _progress.Report(value);
    }
}

/// <summary>Recursive, fault-tolerant snapshot of a folder tree.</summary>
internal sealed class FolderSnapshot
{
    public sealed record FileItem(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

    public List<FileItem> Files { get; } = new();
    public List<string> EmptyDirectories { get; } = new();
    public List<string> Skipped { get; } = new();
    public long TotalBytes { get; private set; }

    public static FolderSnapshot Take(string root, IEnumerable<string>? excludeFullPaths, CancellationToken ct)
    {
        var snapshot = new FolderSnapshot();
        var excludes = new HashSet<string>(
            (excludeFullPaths ?? Array.Empty<string>()).Select(p => Path.GetFullPath(p)),
            StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { snapshot.Skipped.Add($"{dir} ({ex.GetType().Name})"); files = Array.Empty<string>(); }

            int fileCountHere = 0;
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                string full = Path.GetFullPath(file);
                if (excludes.Contains(full)) continue;

                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) continue;
                    snapshot.Files.Add(new FileItem(full,
                        Path.GetRelativePath(root, full), info.Length, info.LastWriteTimeUtc));
                    snapshot.TotalBytes += info.Length;
                    fileCountHere++;
                }
                catch (Exception ex)
                {
                    snapshot.Skipped.Add($"{file} ({ex.GetType().Name})");
                }
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) { snapshot.Skipped.Add($"{dir} ({ex.GetType().Name})"); subs = Array.Empty<string>(); }

            if (subs.Length == 0 && fileCountHere == 0 && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                snapshot.EmptyDirectories.Add(Path.GetRelativePath(root, dir));

            foreach (string sub in subs)
            {
                // Skip reparse points (symlinks/junctions) to avoid cycles and escaping the tree.
                try
                {
                    if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        snapshot.Skipped.Add($"{sub} (symbolic link)");
                        continue;
                    }
                }
                catch { continue; }

                stack.Push(sub);
            }
        }

        snapshot.Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return snapshot;
    }
}
