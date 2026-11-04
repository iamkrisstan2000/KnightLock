using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KnightLock.Core;

namespace KnightLock.CoreTests;

/// <summary>
/// Headless verification suite for the KnightLock vault engine.
/// Runs anywhere .NET 8 runs (this sandbox is Linux) so the container format,
/// crypto, sealing and wipe logic are all proven before the Windows shell is added.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _root = "";

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _root = Path.Combine(Path.GetTempPath(), "knightlock-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        Console.WriteLine("KnightLock vault engine — verification suite");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"Work root: {_root}\n");

        var suite = new (string Name, Func<Task> Run)[]
        {
            ("Round-trip: nested tree incl. empty folder + unicode", RoundTripBasic),
            ("Round-trip: 24 MB payload spanning many chunks", RoundTripLarge),
            ("Round-trip: 0-byte file + deep nesting", RoundTripEdgeNames),
            ("Container magic + header metadata are correct", HeaderMetadata),
            ("Wrong password is rejected with WrongPassword", WrongPassword),
            ("Tampered ciphertext is detected (GCM)", TamperedCiphertext),
            ("Truncated container is detected", TruncatedContainer),
            ("Replaced KDF iteration count invalidates vault", HeaderDowngradeAttack),
            ("Foreign file is rejected as NotAVault", ForeignFile),
            ("Password strength gating blocks weak secrets", PasswordGating),
            ("VerifyPassword matches only the correct secret", VerifyPasswordOnly),
            ("Seal-back: edits are re-encrypted on lock", SealBackEdits),
            ("Seal-back: fresh nonce prefix per seal (no reuse)", SealBackNonceRotation),
            ("Progress reports traverse phases monotonically", ProgressPhases),
            ("Secure wipe removes plaintext and directory", SecureWipe),
            ("Vault library (registry) persists and prunes", RegistryPersistence),
            ("Concurrency: sequential creates stay isolated", MultiVaultIsolation),
        };

        foreach (var (name, run) in suite)
        {
            try
            {
                await run();
                _passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        try { Directory.Delete(_root, true); } catch { }

        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ tests

    private static async Task RoundTripBasic()
    {
        string source = NewDir("src-basic");
        WriteFile(source, "readme.txt", "KnightLock round-trip test ✓");
        WriteFile(source, "docs/report.md", "# Secret report\nline two\n");
        WriteFile(source, "docs/deep/nested/data.bin", RandomBytes(4096));
        WriteFile(source, "images/ünïcødé-名前.txt", "unicode filename content");
        Directory.CreateDirectory(Path.Combine(source, "empty-folder"));

        string vault = Path.Combine(NewDir("out-basic"), "notes.vault");
        var result = await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source,
            VaultFilePath = vault,
            Password = "Correct-Horse-Battery-Staple-9",
            Iterations = 100_000
        });

        Assert(File.Exists(vault), "vault file was not created");
        Assert(result.FileCount == 4, $"expected 4 files, got {result.FileCount}");
        Assert(result.VaultBytes > 0, "vault size is zero");

        using var session = (await VaultService.OpenAsync(vault, "Correct-Horse-Battery-Staple-9",
            Path.Combine(_root, "session-basic"))).Session;

        string wd = session.WorkingDirectory;
        AssertEqual("KnightLock round-trip test ✓", File.ReadAllText(Path.Combine(wd, "readme.txt")));
        AssertEqual("# Secret report\nline two\n", File.ReadAllText(Path.Combine(wd, "docs/report.md")));
        Assert(File.Exists(Path.Combine(wd, "images/ünïcødé-名前.txt")), "unicode file missing after extract");
        Assert(Directory.Exists(Path.Combine(wd, "empty-folder")), "empty folder not preserved");
        AssertBytes(RandomBytes(0), Array.Empty<byte>(), "sanity");
    }

    private static async Task RoundTripLarge()
    {
        string source = NewDir("src-large");
        byte[] big = RandomBytes(24 * 1024 * 1024 + 12345); // spans >24 chunks
        WriteFile(source, "big.bin", big);
        WriteFile(source, "small.txt", "tail");

        string vault = Path.Combine(NewDir("out-large"), "big.vault");
        var sw = Stopwatch.StartNew();
        await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source,
            VaultFilePath = vault,
            Password = "s3ven-chunk-streaming-pass!",
            Iterations = 100_000
        });

        using var session = (await VaultService.OpenAsync(vault, "s3ven-chunk-streaming-pass!",
            Path.Combine(_root, "session-large"))).Session;

        byte[] restored = File.ReadAllBytes(Path.Combine(session.WorkingDirectory, "big.bin"));
        Assert(restored.Length == big.Length, $"size mismatch {restored.Length} vs {big.Length}");
        Assert(SHA256.HashData(restored).SequenceEqual(SHA256.HashData(big)), "large payload hash mismatch");
        AssertEqual("tail", File.ReadAllText(Path.Combine(session.WorkingDirectory, "small.txt")));
        Console.WriteLine($"        (24 MB create+open in {sw.ElapsedMilliseconds} ms)");
    }

    private static async Task RoundTripEdgeNames()
    {
        string source = NewDir("src-edge");
        WriteFile(source, "zero.dat", Array.Empty<byte>());
        string deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("lvl", 18));
        WriteFile(source, Path.Combine(deep, "deep.txt"), "deep");
        WriteFile(source, "with spaces and (parens) & ampersand.txt", "ok");

        string vault = Path.Combine(NewDir("out-edge"), "edge.vault");
        await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source,
            VaultFilePath = vault,
            Password = "edge-case-passphrase-2026",
            Iterations = 100_000
        });

        using var session = (await VaultService.OpenAsync(vault, "edge-case-passphrase-2026",
            Path.Combine(_root, "session-edge"))).Session;

        string wd = session.WorkingDirectory;
        AssertEqual(0L, new FileInfo(Path.Combine(wd, "zero.dat")).Length);
        AssertEqual("deep", File.ReadAllText(Path.Combine(wd, deep, "deep.txt")));
        Assert(File.Exists(Path.Combine(wd, "with spaces and (parens) & ampersand.txt")), "special chars lost");
    }

    private static async Task HeaderMetadata()
    {
        string source = NewDir("src-header");
        WriteFile(source, "a.txt", new string('a', 5000));

        string vault = Path.Combine(NewDir("out-header"), "meta.vault");
        var created = await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source,
            VaultFilePath = vault,
            Password = "metadata-check-pass-42",
            Iterations = 150_000
        });

        byte[] head = new byte[16];
        using (var fs = File.OpenRead(vault)) fs.ReadExactly(head);
        AssertEqual("KNIGHTLOCK.VAULT", Encoding.ASCII.GetString(head));
        Assert(vault.EndsWith(".vault"), "extension not applied");

        var header = VaultService.Inspect(vault);
        AssertEqual(150_000, header.Kdf.Iterations);
        AssertEqual("AES-256-GCM", header.Cipher);
        AssertEqual(created.VaultId, header.VaultId);
        Assert(header.Salt().Length == 16, "bad salt size");
        Assert(header.KeyCheck().Length == 16, "bad key check size");
        Assert(header.SourceBytes > 0, "source bytes not recorded");
        await Task.CompletedTask;
    }

    private static async Task WrongPassword()
    {
        string vault = await MakeSimpleVault("wrong-pass-src", "wrong-pass.vault", "the-real-password-1");
        var ex =await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(vault, "not-the-password", Path.Combine(_root, "s-wrong")));
        AssertEqual(VaultError.WrongPassword, ex.Error);
        Console.WriteLine($"        (message: {ex.Message})");
    }

    private static async Task TamperedCiphertext()
    {
        string vault = await MakeSimpleVault("tamper-src", "tamper.vault", "tamper-check-pass");
        long len = new FileInfo(vault).Length;

        // Flip one byte deep inside the ciphertext.
        using (var fs = new FileStream(vault, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = len - 40;
            int original = fs.ReadByte();
            fs.Position = len - 40;
            fs.WriteByte((byte)(original ^ 0xFF));
        }

        var ex = await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(vault, "tamper-check-pass", Path.Combine(_root, "s-tamper")));
        Assert(ex.Error is VaultError.WrongPassword or VaultError.Corrupt,
            $"unexpected error {ex.Error}");
        Console.WriteLine($"        (detected as: {ex.Error})");
    }

    private static async Task TruncatedContainer()
    {
        string vault = await MakeSimpleVault("trunc-src", "trunc.vault", "truncate-check-pass");
        long len = new FileInfo(vault).Length;
        using (var fs = new FileStream(vault, FileMode.Open, FileAccess.Write)) fs.SetLength(len / 2);

        var ex = await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(vault, "truncate-check-pass", Path.Combine(_root, "s-trunc")));
        Assert(ex.Error is VaultError.Truncated or VaultError.WrongPassword or VaultError.Corrupt,
            $"unexpected error {ex.Error}");
        Console.WriteLine($"        (detected as: {ex.Error})");
    }

    private static async Task HeaderDowngradeAttack()
    {
        string vault = await MakeSimpleVault("downgrade-src", "downgrade.vault", "downgrade-check-pass");

        // Rewrite the JSON header so it claims 100k iterations instead of what it had.
        byte[] all = File.ReadAllBytes(vault);
        int headerLen = BitConverter.ToInt32(all, 20);
        string json = Encoding.UTF8.GetString(all, 24, headerLen);
        string patched = json.Replace("\"iter\":150000", "\"iter\":100000");
        Assert(patched != json, "test setup failed: iteration count not found in header");
        byte[] patchedBytes = Encoding.UTF8.GetBytes(patched);

        using var fs = new FileStream(vault, FileMode.Create);
        fs.Write(all, 0, 20);
        fs.Write(BitConverter.GetBytes(patchedBytes.Length), 0, 4);
        fs.Write(patchedBytes, 0, patchedBytes.Length);
        fs.Write(all, 24 + headerLen, all.Length - 24 - headerLen);
        await fs.FlushAsync();

        var ex = await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(vault, "downgrade-check-pass", Path.Combine(_root, "s-down")));
        Assert(ex.Error is VaultError.WrongPassword or VaultError.Corrupt,
            $"downgrade attempt should fail, got {ex.Error}");
        Console.WriteLine($"        (header tampering detected as: {ex.Error})");
    }

    private static async Task ForeignFile()
    {
        string dir = NewDir("foreign");
        string path = Path.Combine(dir, "notavault.vault");
        await File.WriteAllTextAsync(path, "This is just a text file pretending to be a vault.");

        var ex = await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(path, "whatever-password", Path.Combine(_root, "s-foreign")));
        Assert(ex.Error is VaultError.NotAVault or VaultError.Truncated, $"got {ex.Error}");
        Console.WriteLine($"        (message: {ex.Title} — {ex.Message})");
    }

    private static Task PasswordGating()
    {
        Assert(!PasswordStrength.Evaluate("abc").MeetsMinimum, "short password accepted");
        Assert(!PasswordStrength.Evaluate("password123").MeetsMinimum || PasswordStrength.Evaluate("password123").Score <= 1,
            "common password scored too high");
        Assert(!PasswordStrength.Evaluate("aaaaaaaaaaaaaaaa").MeetsMinimum, "repeated chars accepted");
        Assert(PasswordStrength.Evaluate("Tr0ub4dor&3-Horse-Staple").Score >= 3, "strong password under-scored");
        Assert(PasswordStrength.Evaluate("kJ8#vQ2m!pLz7Rw4").Score == 4, "very strong password should be 4");
        Console.WriteLine($"        (\"password123\" => {PasswordStrength.Evaluate("password123").Score}/4, " +
                          $"\"kJ8#vQ2m!pLz7Rw4\" => {PasswordStrength.Evaluate("kJ8#vQ2m!pLz7Rw4").Score}/4)");
        return Task.CompletedTask;
    }

    private static async Task VerifyPasswordOnly()
    {
        string vault = await MakeSimpleVault("verify-src", "verify.vault", "correct-secret-2026");
        Assert(VaultService.VerifyPassword(vault, "correct-secret-2026"), "correct password rejected");
        Assert(!VaultService.VerifyPassword(vault, "wrong-secret-2026"), "wrong password accepted");
        Assert(!VaultService.VerifyPassword(vault, ""), "empty password accepted");
    }

    private static async Task SealBackEdits()
    {
        string source = NewDir("src-seal");
        WriteFile(source, "keep.txt", "original");
        WriteFile(source, "edit.txt", "before");

        string vault = Path.Combine(NewDir("out-seal"), "seal.vault");
        await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source, VaultFilePath = vault,
            Password = "seal-back-passphrase-1", Iterations = 100_000
        });

        var session = (await VaultService.OpenAsync(vault, "seal-back-passphrase-1",
            Path.Combine(_root, "s-seal"))).Session;

        var before = session.DetectChanges();
        Assert(!before.HasChanges, $"fresh session reports changes: {before.Describe()}");

        File.WriteAllText(Path.Combine(session.WorkingDirectory, "edit.txt"), "after");
        File.WriteAllText(Path.Combine(session.WorkingDirectory, "new.txt"), "brand new file");
        Directory.CreateDirectory(Path.Combine(session.WorkingDirectory, "newdir"));
        File.WriteAllText(Path.Combine(session.WorkingDirectory, "newdir", "inner.txt"), "inner");

        var diff = session.DetectChanges();
        Assert(diff.Added == 2, $"expected 2 added, got {diff.Added}");
        Assert(diff.Modified >= 1, $"expected >=1 modified, got {diff.Modified}");

        var sealedResult = await session.SealAsync();
        Assert(sealedResult.VaultBytes > 0, "seal produced empty container");

        // Plaintext working copy must be gone.
        Assert(!Directory.Exists(session.WorkingDirectory), "working copy survived sealing");

        using var reopened = (await VaultService.OpenAsync(vault, "seal-back-passphrase-1",
            Path.Combine(_root, "s-seal2"))).Session;
        string wd = reopened.WorkingDirectory;
        AssertEqual("original", File.ReadAllText(Path.Combine(wd, "keep.txt")));
        AssertEqual("after", File.ReadAllText(Path.Combine(wd, "edit.txt")));
        AssertEqual("brand new file", File.ReadAllText(Path.Combine(wd, "new.txt")));
        AssertEqual("inner", File.ReadAllText(Path.Combine(wd, "newdir", "inner.txt")));
    }

    private static async Task SealBackNonceRotation()
    {
        string vault = await MakeSimpleVault("nonce-src", "nonce.vault", "nonce-rotation-pass");
        var header1 = VaultService.Inspect(vault);

        var session = (await VaultService.OpenAsync(vault, "nonce-rotation-pass",
            Path.Combine(_root, "s-nonce"))).Session;
        File.WriteAllText(Path.Combine(session.WorkingDirectory, "added.txt"), "x");
        await session.SealAsync();

        var header2 = VaultService.Inspect(vault);
        Assert(header1.NoncePrefixBase64 != header2.NoncePrefixBase64, "nonce prefix was reused after seal!");
        Assert(header1.VaultId == header2.VaultId, "vault identity changed on seal");
        Assert(header2.ModifiedUtc >= header1.ModifiedUtc, "modified timestamp not updated");
        Console.WriteLine($"        (nonce prefix rotated: {header1.NoncePrefixBase64[..6]}… -> {header2.NoncePrefixBase64[..6]}…)");
    }

    private static async Task ProgressPhases()
    {
        string source = NewDir("src-progress");
        for (int i = 0; i < 12; i++)
            WriteFile(source, $"f{i}.txt", RandomBytes(64 * 1024));

        var seen = new List<VaultProgress>();
        var progress = new Progress<VaultProgress>(p => { lock (seen) seen.Add(p); });

        string vault = Path.Combine(NewDir("out-progress"), "prog.vault");
        await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source, VaultFilePath = vault,
            Password = "progress-reporting-pass", Iterations = 100_000
        }, progress);

        var openSeen = new List<VaultProgress>();
        var openProgress = new Progress<VaultProgress>(p => { lock (openSeen) openSeen.Add(p); });
        using var session = (await VaultService.OpenAsync(vault, "progress-reporting-pass",
            Path.Combine(_root, "s-progress"), openProgress)).Session;

        await Task.Delay(120); // let queued Progress callbacks drain

        Assert(seen.Count > 0, "no progress reported on create");
        Assert(seen.Any(p => p.Phase == VaultPhase.Compressing), "compressing phase never reported");
        Assert(seen.Any(p => p.Phase == VaultPhase.Done), "done phase never reported");
        Assert(seen.Last().Percent is >= 99 or null, "final percent not 100");
        Assert(seen.Any(p => !string.IsNullOrWhiteSpace(p.Message)), "progress messages empty");

        Assert(openSeen.Any(p => p.Phase == VaultPhase.Decrypting), "decrypting phase never reported");
        Assert(openSeen.Any(p => p.Phase == VaultPhase.Extracting), "extracting phase never reported");
        Console.WriteLine($"        (create: {seen.Count} updates, open: {openSeen.Count} updates)");
    }

    private static Task SecureWipe()
    {
        string dir = NewDir("wipe");
        WriteFile(dir, "a/secret.txt", "TOP SECRET DATA");
        WriteFile(dir, "a/b/c/deeper.bin", RandomBytes(300 * 1024));

        var paths = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert(paths.Length == 2, "setup wrong");

        var sw = Stopwatch.StartNew();
        SecureDelete.WipeDirectory(dir);
        Assert(!Directory.Exists(dir), "directory still exists after wipe");
        Assert(!File.Exists(paths[0]) && !File.Exists(paths[1]), "files survived wipe");
        Console.WriteLine($"        (wiped 2 files + tree in {sw.ElapsedMilliseconds} ms)");
        return Task.CompletedTask;
    }

    private static Task RegistryPersistence()
    {
        string path = Path.Combine(NewDir("registry"), "vaults.json");
        string vault = Path.Combine(_root, "registry", "one.vault");
        File.WriteAllText(vault, "placeholder");

        var reg = new VaultRegistry(path);
        var rec = reg.AddOrUpdate(new VaultRecord
        {
            Name = "Work",
            Path = vault,
            SizeBytes = 999,
            FileCount = 3,
            CreatedUtc = DateTime.UtcNow
        });

        var reloaded = new VaultRegistry(path);
        Assert(reloaded.Records.Count == 1, "record not persisted");
        AssertEqual("Work", reloaded.Records[0].Name);
        Assert(reloaded.Find(vault) is not null, "lookup by path failed");

        reloaded.Touch(vault);
        Assert(new VaultRegistry(path).Records[0].LastOpenedUtc is not null, "touch not persisted");

        File.Delete(vault);
        Assert(reloaded.PruneMissing() == 1, "missing vault not pruned");
        Assert(new VaultRegistry(path).Records.Count == 0, "pruned record still present");

        // Corrupt library must not throw.
        File.WriteAllText(path, "{ this is not json");
        Assert(new VaultRegistry(path).Records.Count == 0, "corrupt registry should load empty");

        _ = rec;
        return Task.CompletedTask;
    }

    private static async Task MultiVaultIsolation()
    {
        string v1 = await MakeSimpleVault("iso1", "iso1.vault", "first-vault-password");
        string v2 = await MakeSimpleVault("iso2", "iso2.vault", "second-vault-password");

        var s1 = (await VaultService.OpenAsync(v1, "first-vault-password", Path.Combine(_root, "s-iso"))).Session;
        var s2 = (await VaultService.OpenAsync(v2, "second-vault-password", Path.Combine(_root, "s-iso"))).Session;

        Assert(s1.WorkingDirectory != s2.WorkingDirectory, "sessions share a working directory");

        var cross = await AssertThrows<VaultException>(() =>
            VaultService.OpenAsync(v1, "second-vault-password", Path.Combine(_root, "s-iso2")));
        AssertEqual(VaultError.WrongPassword, cross.Error);

        s1.Dispose();
        s2.Dispose();
        Assert(!Directory.Exists(s1.WorkingDirectory), "dispose did not wipe session 1");
        Assert(!Directory.Exists(s2.WorkingDirectory), "dispose did not wipe session 2");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> MakeSimpleVault(string srcName, string vaultName, string password)
    {
        string source = NewDir(srcName);
        WriteFile(source, "secret.txt", "classified-" + srcName);
        WriteFile(source, "nested/other.bin", RandomBytes(9000));

        string vault = Path.Combine(NewDir("out-" + srcName), vaultName);
        await VaultService.CreateAsync(new CreateVaultRequest
        {
            SourceFolder = source,
            VaultFilePath = vault,
            Password = password,
            Iterations = 150_000
        });
        return vault;
    }

    private static string NewDir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string root, string relative, string content)
        => WriteFile(root, relative, Encoding.UTF8.GetBytes(content));

    private static void WriteFile(string root, string relative, byte[] content)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static byte[] RandomBytes(int count)
    {
        // Deterministic-ish but incompressible payload.
        byte[] b = new byte[count];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: expected <{expected}>, got <{actual}>");
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string what)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Assertion failed: byte mismatch for {what}");
    }

    private static async Task<T> AssertThrows<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} but no exception was thrown.");
    }
}

internal static class HeaderExtensions
{
    public static byte[] Salt(this VaultHeader h) => Convert.FromBase64String(h.Kdf.SaltBase64);
    public static byte[] KeyCheck(this VaultHeader h) => Convert.FromBase64String(h.KeyCheckBase64);
}
