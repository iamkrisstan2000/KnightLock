using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KnightLock.Core;

namespace KnightLock.Services;

public sealed class AutoLockEventArgs : EventArgs
{
    public VaultSession Session { get; init; } = null!;
}

/// <summary>
/// Owns every unlocked vault for the lifetime of the process.
///
/// Security rules enforced here:
///  • decrypted copies only ever live under %LOCALAPPDATA%\KnightLock\Sessions\&lt;random&gt;
///  • any leftover session folder from a crash is wiped on next start
///  • locking seals edits back into the container, then securely wipes the plaintext
///  • idle vaults auto-lock, and everything is sealed + wiped when the app exits
/// </summary>
public sealed class SessionManager
{
    private readonly Dictionary<string, VaultSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly VaultRegistry _registry;
    private readonly AppSettings _settings;
    private DispatcherTimer? _autoLockTimer;

    public SessionManager(VaultRegistry registry, AppSettings settings)
    {
        _registry = registry;
        _settings = settings;
        Directory.CreateDirectory(AppPaths.SessionsRoot);
    }

    public event EventHandler? SessionsChanged;
    public event EventHandler<AutoLockEventArgs>? AutoLocked;

    public IReadOnlyCollection<VaultSession> Sessions => _sessions.Values.ToList();

    public bool IsUnlocked(string vaultPath) => _sessions.ContainsKey(Key(vaultPath));

    public VaultSession? Find(string vaultPath) =>
        _sessions.TryGetValue(Key(vaultPath), out var session) ? session : null;

    public VaultSession? FindById(string vaultId) =>
        _sessions.Values.FirstOrDefault(s => s.Header.VaultId == vaultId);

    private static string Key(string path) => Path.GetFullPath(path);

    /// <summary>Wipes plaintext left behind by a previous run (crash / power loss).</summary>
    public void PurgeOrphanedSessions()
    {
        try
        {
            if (!Directory.Exists(AppPaths.SessionsRoot)) return;
            foreach (string dir in Directory.GetDirectories(AppPaths.SessionsRoot))
                SecureDelete.WipeDirectory(dir);
            Directory.CreateDirectory(AppPaths.SessionsRoot);
            Log.Info("Purged orphaned session folders from a previous run.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not purge orphaned sessions: {ex.Message}");
        }
    }

    public async Task<VaultOpenResult> UnlockAsync(string vaultPath, string password,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        string key = Key(vaultPath);

        if (_sessions.TryGetValue(key, out var existing))
            throw new VaultException(VaultError.AlreadyExists,
                $"\"{existing.DisplayName}\" is already unlocked in this session.");

        var result = await VaultService.OpenAsync(key, password, AppPaths.SessionsRoot, progress, ct);
        _sessions[key] = result.Session;

        _registry.AddOrUpdate(new VaultRecord
        {
            Id = result.Session.Header.VaultId,
            Name = result.Session.Header.Name,
            Path = key,
            CreatedUtc = result.Session.Header.CreatedUtc,
            LastOpenedUtc = DateTime.UtcNow,
            SizeBytes = result.VaultBytes,
            FileCount = result.Session.Header.FileCount,
            SourceFolder = result.Session.Header.SourceFolder
        });

        Log.Info($"Unlocked vault '{result.Session.DisplayName}'.");
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>
    /// Locks a vault. When <paramref name="sealChanges"/> is true and the working copy was
    /// edited, the changes are re-encrypted into the container before the plaintext is wiped.
    /// Returns the seal result (or null when nothing needed sealing).
    /// </summary>
    public async Task<VaultCreateResult?> LockAsync(string vaultPath, bool sealChanges,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        string key = Key(vaultPath);
        if (!_sessions.TryGetValue(key, out var session)) return null;

        VaultCreateResult? result = null;

        try
        {
            if (sealChanges)
            {
                var changes = session.DetectChanges();
                if (changes.HasChanges)
                {
                    Log.Info($"Sealing changes into '{session.DisplayName}' ({changes.Describe()}).");
                    result = await session.SealAsync(progress, ct);
                    _registry.AddOrUpdate(new VaultRecord
                    {
                        Id = session.Header.VaultId,
                        Name = session.Header.Name,
                        Path = key,
                        CreatedUtc = session.Header.CreatedUtc,
                        SizeBytes = result.VaultBytes,
                        FileCount = session.Header.FileCount,
                        SourceFolder = session.Header.SourceFolder
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Never wipe the plaintext if the container could not be written.
            Log.Error("Sealing failed; keeping the vault unlocked so no data is lost.", ex);
            throw;
        }

        session.Wipe();
        session.Dispose();
        _sessions.Remove(key);

        Log.Info($"Locked vault '{session.DisplayName}'.");
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task LockAllAsync(bool sealChanges, IProgress<VaultProgress>? progress = null)
    {
        foreach (var session in _sessions.Values.ToList())
        {
            try
            {
                await LockAsync(session.VaultFilePath, sealChanges, progress);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to lock '{session.VaultFilePath}'.", ex);
            }
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wipes every plaintext copy immediately, without sealing (used on hard exit).</summary>
    public void WipeAllImmediately()
    {
        foreach (var session in _sessions.Values.ToList())
        {
            try
            {
                session.Wipe();
                session.Dispose();
            }
            catch { }
        }
        _sessions.Clear();
        PurgeOrphanedSessions();
    }

    public void StartAutoLock()
    {
        _autoLockTimer?.Stop();
        _autoLockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _autoLockTimer.Tick += async (_, _) => await CheckAutoLockAsync();
        _autoLockTimer.Start();
    }

    private bool _autoLockBusy;

    private async Task CheckAutoLockAsync()
    {
        if (_autoLockBusy) return;
        int minutes = _settings.AutoLockMinutes;
        if (minutes <= 0) return;

        var expired = _sessions.Values
            .Where(s => DateTime.UtcNow - s.UnlockedUtc >= TimeSpan.FromMinutes(minutes))
            .ToList();

        if (expired.Count == 0) return;

        _autoLockBusy = true;
        try
        {
            foreach (var session in expired)
            {
                try
                {
                    await LockAsync(session.VaultFilePath, sealChanges: true);
                    AutoLocked?.Invoke(this, new AutoLockEventArgs { Session = session });
                }
                catch (Exception ex)
                {
                    Log.Error("Auto-lock failed.", ex);
                }
            }
        }
        finally
        {
            _autoLockBusy = false;
        }
    }
}
