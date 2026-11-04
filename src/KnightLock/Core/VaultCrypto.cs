using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KnightLock.Core;

/// <summary>
/// Key derivation and authenticated-encryption primitives for the container format.
/// </summary>
public static class VaultCrypto
{
    private static readonly byte[] KeyCheckLabel = Encoding.UTF8.GetBytes("KnightLock.KeyCheck.v1");

    /// <summary>
    /// PBKDF2-HMAC-SHA256 over the UTF-8 password. 600,000 iterations by default
    /// (OWASP 2023+ guidance for PBKDF2-SHA256).
    /// </summary>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
            throw new VaultException(VaultError.PasswordTooWeak, "Enter your master password.");

        ct.ThrowIfCancellationRequested();

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, iterations, HashAlgorithmName.SHA256);
            byte[] key = kdf.GetBytes(VaultFormat.KeySize);
            ct.ThrowIfCancellationRequested();
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// A short, non-secret verifier stored in the header. It lets KnightLock say
    /// "incorrect password" immediately instead of reporting a generic auth failure,
    /// without weakening the ciphertext (an attacker could always verify guesses against
    /// the GCM tags anyway).
    /// </summary>
    public static byte[] ComputeKeyCheck(byte[] key, byte[] salt)
    {
        using var hmac = new HMACSHA256(key);
        Span<byte> material = stackalloc byte[KeyCheckLabel.Length + salt.Length];
        KeyCheckLabel.CopyTo(material);
        salt.CopyTo(material[KeyCheckLabel.Length..]);
        byte[] mac = hmac.ComputeHash(material.ToArray());
        return mac[..16];
    }

    public static bool VerifyKeyCheck(byte[] key, byte[] salt, byte[] expected)
    {
        byte[] actual = ComputeKeyCheck(key, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>12-byte GCM nonce: 8-byte random per-container prefix + 32-bit chunk counter.</summary>
    public static void BuildNonce(ReadOnlySpan<byte> prefix8, long chunkIndex, Span<byte> destination12)
    {
        prefix8.CopyTo(destination12);
        BinaryPrimitives.WriteUInt32LittleEndian(destination12[VaultFormat.NoncePrefixSize..],
            checked((uint)chunkIndex));
    }

    /// <summary>40-byte associated data: header fingerprint + chunk index.</summary>
    public static void BuildAssociatedData(ReadOnlySpan<byte> headerFingerprint, long chunkIndex, Span<byte> destination40)
    {
        headerFingerprint.CopyTo(destination40);
        BinaryPrimitives.WriteInt64LittleEndian(destination40[headerFingerprint.Length..], chunkIndex);
    }

    public static byte[] RandomBytes(int count)
    {
        byte[] b = new byte[count];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
