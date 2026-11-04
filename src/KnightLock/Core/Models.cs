using System;
using System.Collections.Generic;

namespace KnightLock.Core;

public enum VaultPhase
{
    Preparing,
    Scanning,
    Compressing,
    DerivingKey,
    Encrypting,
    Verifying,
    Decrypting,
    Extracting,
    Sealing,
    Wiping,
    Done
}

/// <summary>Immutable progress snapshot, marshalled to the UI thread by <see cref="IProgress{T}"/>.</summary>
public sealed class VaultProgress
{
    public VaultPhase Phase { get; init; }
    public string Message { get; init; } = "";
    public double? Percent { get; init; }          // null => indeterminate
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public int ItemsProcessed { get; init; }
    public int TotalItems { get; init; }

    public static VaultProgress Of(VaultPhase phase, string message, double? percent = null) =>
        new() { Phase = phase, Message = message, Percent = percent };

    public override string ToString() => $"{Phase}: {Message} ({Percent:0.0}%)";
}

public sealed class CreateVaultRequest
{
    public required string SourceFolder { get; init; }
    public required string VaultFilePath { get; init; }
    public required string Password { get; init; }
    public int Iterations { get; init; } = VaultFormat.DefaultIterations;
    public bool Overwrite { get; init; } = true;
    public string DisplayName { get; init; } = "";
}

public sealed class VaultCreateResult
{
    public string VaultFilePath { get; init; } = "";
    public string VaultId { get; init; } = "";
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public long SourceBytes { get; init; }
    public long VaultBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public double CompressionRatio => SourceBytes <= 0 ? 0 : 1.0 - (double)VaultBytes / SourceBytes;
}

public sealed class VaultOpenResult
{
    public required VaultSession Session { get; init; }
    public long VaultBytes { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>Size + timestamp snapshot used to detect edits made while a vault is open.</summary>
public readonly record struct ManifestEntry(long Size, DateTime LastWriteUtc);

public sealed class VaultChangeSummary
{
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
    public bool HasChanges => Added + Modified + Deleted > 0;
    public string Describe() => HasChanges
        ? $"{Added} added, {Modified} changed, {Deleted} removed"
        : "No changes";
}
