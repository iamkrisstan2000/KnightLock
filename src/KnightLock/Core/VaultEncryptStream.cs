using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace KnightLock.Core;

/// <summary>
/// Write-only stream that encrypts everything written to it into the KnightLock
/// chunked AES-256-GCM format. Supports arbitrarily large payloads in constant memory,
/// which lets us zip a multi-gigabyte folder straight into the container.
/// </summary>
public sealed class VaultEncryptStream : Stream
{
    private readonly Stream _output;
    private readonly AesGcm _aes;
    private readonly byte[] _noncePrefix;
    private readonly byte[] _headerFingerprint;
    private readonly byte[] _buffer;
    private readonly byte[] _aad;
    private readonly byte[] _nonce;
    private readonly Action<long>? _onChunkWritten;

    private int _buffered;
    private long _chunkIndex;
    private long _plainBytes;
    private bool _finished;

    public VaultEncryptStream(Stream output, byte[] key, byte[] noncePrefix, byte[] headerFingerprint,
                              int chunkSize = VaultFormat.ChunkSize, Action<long>? onChunkWritten = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _aes = new AesGcm(key, VaultFormat.TagSize);
        _noncePrefix = noncePrefix;
        _headerFingerprint = headerFingerprint;
        _buffer = new byte[chunkSize];
        _aad = new byte[headerFingerprint.Length + 8];
        _nonce = new byte[VaultFormat.NonceSize];
        _onChunkWritten = onChunkWritten;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _plainBytes;
    public override long Position { get => _plainBytes; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int room = _buffer.Length - _buffered;
            int take = Math.Min(room, buffer.Length);
            buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            _plainBytes += take;
            buffer = buffer[take..];

            if (_buffered == _buffer.Length)
                FlushChunk();
        }
    }

    public override void Flush() => _output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void FlushChunk()
    {
        if (_buffered == 0) return;

        VaultCrypto.BuildNonce(_noncePrefix, _chunkIndex, _nonce);
        VaultCrypto.BuildAssociatedData(_headerFingerprint, _chunkIndex, _aad);

        byte[] cipher = new byte[_buffered];
        byte[] tag = new byte[VaultFormat.TagSize];
        try
        {
            _aes.Encrypt(_nonce, _buffer.AsSpan(0, _buffered), cipher, tag, _aad);

            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)cipher.Length);
            _output.Write(len);
            _output.Write(cipher, 0, cipher.Length);
            _output.Write(tag, 0, tag.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _buffered));
        }

        _buffered = 0;
        _chunkIndex++;
        _onChunkWritten?.Invoke(_plainBytes);
    }

    /// <summary>Flush the final partial chunk and write the end-of-stream sentinel.</summary>
    public void Finish()
    {
        if (_finished) return;
        FlushChunk();

        Span<byte> terminator = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(terminator, VaultFormat.ChunkTerminator);
        _output.Write(terminator);
        _output.Flush();
        _finished = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_finished) Finish();
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_buffer);
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Decrypts a KnightLock chunk stream into a destination stream, verifying every
/// GCM tag and the end-of-stream sentinel (so truncation and tampering are detected,
/// never silently ignored).
/// </summary>
public static class VaultDecryptor
{
    public static long DecryptTo(Stream container, Stream destination, byte[] key, byte[] noncePrefix,
                                 byte[] headerFingerprint, int chunkSize, Action<long>? onChunk = null)
    {
        using var aes = new AesGcm(key, VaultFormat.TagSize);
        byte[] aad = new byte[headerFingerprint.Length + 8];
        byte[] nonce = new byte[VaultFormat.NonceSize];
        byte[] tag = new byte[VaultFormat.TagSize];
        byte[] plain = new byte[chunkSize];

        long total = 0;
        long index = 0;
        Span<byte> lenBuf = stackalloc byte[4];

        try
        {
            while (true)
            {
                VaultHeader.ReadExactly(container, lenBuf);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

                if (len == VaultFormat.ChunkTerminator)
                    return total; // clean, authenticated end of stream

                if (len == 0)
                    throw new VaultException(VaultError.Corrupt, "The vault contains an empty ciphertext block.");
                if (len > chunkSize)
                    throw new VaultException(VaultError.Corrupt, "The vault contains an oversized ciphertext block.");

                byte[] cipher = new byte[len];
                VaultHeader.ReadExactly(container, cipher);
                VaultHeader.ReadExactly(container, tag);

                VaultCrypto.BuildNonce(noncePrefix, index, nonce);
                VaultCrypto.BuildAssociatedData(headerFingerprint, index, aad);

                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(), tag, plain.AsSpan(0, (int)len), aad);
                }
                catch (CryptographicException cex)
                {
                    throw new VaultException(VaultError.WrongPassword,
                        "Incorrect master password, or the vault file has been modified.", cex);
                }

                destination.Write(plain, 0, (int)len);
                total += len;
                index++;
                onChunk?.Invoke(total);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
