using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnightLock.Core;

/// <summary>
/// Wire format definition for the KnightLock <c>.vault</c> container.
///
///  Offset   Size   Field
///  ------   ----   ---------------------------------------------------------
///  0        16     Magic "KNIGHTLOCK.VAULT"
///  16       2      Format version (little endian)      = 1
///  18       2      Flags (reserved, must be 0)         = 0
///  20       4      Header length in bytes (little endian)
///  24       N      Header (UTF-8 JSON, not secret, authenticated)
///  24+N     ...    Chunk stream:
///                    4   ciphertext length (uint32 LE, excludes 16-byte tag)
///                    N   ciphertext
///                    16  AES-GCM authentication tag
///                  repeated; terminated by the sentinel 0xFFFFFFFF
///
/// Every chunk is authenticated with AAD = SHA-256(header) || chunkIndex.
/// Because the header hash is bound into every chunk, downgrading the KDF
/// iteration count or changing any header field invalidates the whole vault.
/// </summary>
public static class VaultFormat
{
    /// <summary>16-byte ASCII magic that opens every container.</summary>
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("KNIGHTLOCK.VAULT");

    public const ushort CurrentVersion = 1;
    public const ushort SupportedFlags = 0;

    public const int MagicLength = 16;
    public const int VersionOffset = 16;
    public const int FlagsOffset = 18;
    public const int HeaderLengthOffset = 20;
    public const int HeaderOffset = 24;

    public const int TagSize = 16;
    public const int NoncePrefixSize = 8;
    public const int NonceSize = 12;
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int ChunkSize = 1024 * 1024;         // 1 MiB plaintext chunks
    public const int MaxHeaderLength = 64 * 1024;

    public const uint ChunkTerminator = 0xFFFFFFFFu;

    public const int DefaultIterations = 600_000;
    public const int MinimumIterations = 100_000;
    public const int MaximumIterations = 4_000_000;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>KDF parameters recorded in the container header.</summary>
public sealed class KdfDescriptor
{
    [JsonPropertyName("algo")] public string Algorithm { get; set; } = "PBKDF2-HMAC-SHA256";
    [JsonPropertyName("iter")] public int Iterations { get; set; } = VaultFormat.DefaultIterations;
    [JsonPropertyName("salt")] public string SaltBase64 { get; set; } = "";

    [JsonIgnore] public byte[] Salt => Convert.FromBase64String(SaltBase64);
}

/// <summary>
/// Public (non-secret) metadata for a vault. Written as UTF-8 JSON at the head of
/// the container so an app can identify a vault without the password.
/// </summary>
public sealed class VaultHeader
{
    [JsonPropertyName("v")] public int Version { get; set; } = VaultFormat.CurrentVersion;
    [JsonPropertyName("id")] public string VaultId { get; set; } = Guid.NewGuid().ToString("D");
    [JsonPropertyName("name")] public string Name { get; set; } = "Vault";
    [JsonPropertyName("created")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("modified")] public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("kdf")] public KdfDescriptor Kdf { get; set; } = new();
    [JsonPropertyName("cipher")] public string Cipher { get; set; } = "AES-256-GCM";
    [JsonPropertyName("nonce")] public string NoncePrefixBase64 { get; set; } = "";
    [JsonPropertyName("chunk")] public int ChunkSize { get; set; } = VaultFormat.ChunkSize;
    [JsonPropertyName("check")] public string KeyCheckBase64 { get; set; } = "";
    [JsonPropertyName("files")] public int FileCount { get; set; }
    [JsonPropertyName("bytes")] public long SourceBytes { get; set; }
    [JsonPropertyName("source")] public string SourceFolder { get; set; } = "";

    [JsonIgnore] public byte[] NoncePrefix => Convert.FromBase64String(NoncePrefixBase64);
    [JsonIgnore] public byte[] KeyCheck => Convert.FromBase64String(KeyCheckBase64);

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, VaultFormat.JsonOptions);

    public static VaultHeader Deserialize(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize<VaultHeader>(utf8Json, VaultFormat.JsonOptions)
           ?? throw new VaultException(VaultError.Corrupt, "The vault header could not be parsed.");

    /// <summary>Deterministic fingerprint of the serialized header, used as GCM associated data.</summary>
    public static byte[] Fingerprint(ReadOnlySpan<byte> serializedHeader)
        => System.Security.Cryptography.SHA256.HashData(serializedHeader);

    public static VaultHeader ReadFrom(Stream stream, out byte[] serializedHeader, out byte[] fingerprint)
    {
        Span<byte> fixedPart = stackalloc byte[VaultFormat.HeaderOffset];
        ReadExactly(stream, fixedPart);

        if (!fixedPart[..VaultFormat.MagicLength].SequenceEqual(VaultFormat.Magic))
            throw new VaultException(VaultError.NotAVault, "This file is not a KnightLock vault.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[VaultFormat.VersionOffset..]);
        if (version == 0 || version > VaultFormat.CurrentVersion)
            throw new VaultException(VaultError.UnsupportedVersion,
                $"This vault was written by a newer version of KnightLock (container v{version}).");

        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[VaultFormat.HeaderLengthOffset..]);
        if (headerLength == 0 || headerLength > VaultFormat.MaxHeaderLength)
            throw new VaultException(VaultError.Corrupt, "The vault header is damaged.");

        serializedHeader = new byte[headerLength];
        ReadExactly(stream, serializedHeader);
        fingerprint = Fingerprint(serializedHeader);
        return Deserialize(serializedHeader);
    }

    internal static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
                throw new VaultException(VaultError.Truncated, "The vault file is truncated or incomplete.");
            read += n;
        }
    }
}
