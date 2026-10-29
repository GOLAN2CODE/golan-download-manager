using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Golan.Gdm.Core.Resolvers;

public static class MegaCrypto
{
    /// <summary>
    /// Encrypts or decrypts <paramref name="input"/> into <paramref name="output"/> using AES-128-CTR
    /// with the 8-byte MEGA nonce and a 64-bit big-endian block counter derived from <paramref name="streamByteOffset"/>
    /// (<c>counter = offset / 16</c>, <c>skip = offset % 16</c>).
    /// </summary>
    public static void TransformCtr(
        ReadOnlySpan<byte> aesKey16,
        ReadOnlySpan<byte> nonce8,
        long streamByteOffset,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        if (aesKey16.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes.", nameof(aesKey16));
        if (nonce8.Length != 8) throw new ArgumentException("CTR nonce must be 8 bytes.", nameof(nonce8));
        if (streamByteOffset < 0) throw new ArgumentOutOfRangeException(nameof(streamByteOffset));
        if (output.Length < input.Length) throw new ArgumentException("Output buffer is smaller than input buffer.", nameof(output));
        if (input.IsEmpty) return;

        using var aes = Aes.Create();
        aes.Key = aesKey16.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        const int BatchBlocks = 256;
        Span<byte> counterBatch = stackalloc byte[BatchBlocks * 16];
        Span<byte> keystreamBatch = stackalloc byte[BatchBlocks * 16];

        long currentByteOffset = streamByteOffset;
        int pos = 0;

        while (pos < input.Length)
        {
            ulong startBlock = (ulong)(currentByteOffset / 16);
            int firstBlockSkip = (int)(currentByteOffset % 16);
            int remainingBytes = input.Length - pos;
            int neededBlocks = (firstBlockSkip + remainingBytes + 15) / 16;
            int blocksThisBatch = Math.Min(BatchBlocks, neededBlocks);

            for (int b = 0; b < blocksThisBatch; b++)
            {
                var blockSpan = counterBatch.Slice(b * 16, 16);
                nonce8.CopyTo(blockSpan[..8]);
                BinaryPrimitives.WriteUInt64BigEndian(blockSpan.Slice(8, 8), startBlock + (ulong)b);
            }

            int batchByteLen = blocksThisBatch * 16;
            aes.EncryptEcb(counterBatch[..batchByteLen], keystreamBatch[..batchByteLen], PaddingMode.None);

            var ksSlice = keystreamBatch.Slice(firstBlockSkip, batchByteLen - firstBlockSkip);
            int toProcess = Math.Min(remainingBytes, ksSlice.Length);

            for (int i = 0; i < toProcess; i++)
                output[pos + i] = (byte)(input[pos + i] ^ ksSlice[i]);

            pos += toProcess;
            currentByteOffset += toProcess;
        }
    }

    /// <summary>
    /// Computes the 8-byte MEGA meta-MAC over <paramref name="plaintext"/> using MEGA's chunk boundary schedule.
    /// </summary>
    public static byte[] ComputeMetaMac(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aesKey16, ReadOnlySpan<byte> nonce8)
    {
        using var ms = new MemoryStream(plaintext.ToArray(), writable: false);
        return ComputeMetaMac(ms, aesKey16, nonce8);
    }

    /// <summary>
    /// Streams <paramref name="plaintextStream"/> in 64 KB buffers to compute the 8-byte MEGA meta-MAC
    /// without allocating the entire file in memory (supports files &gt;= 2 GB).
    /// </summary>
    public static byte[] ComputeMetaMac(Stream plaintextStream, ReadOnlySpan<byte> aesKey16, ReadOnlySpan<byte> nonce8)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);
        if (aesKey16.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes.", nameof(aesKey16));
        if (nonce8.Length != 8) throw new ArgumentException("Nonce must be 8 bytes.", nameof(nonce8));

        using var aes = Aes.Create();
        aes.Key = aesKey16.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        Span<byte> fileMac = stackalloc byte[16];
        fileMac.Clear();
        Span<byte> chunkMac = stackalloc byte[16];
        Span<byte> blockBuf = stackalloc byte[16];
        byte[] readBuf = new byte[64 * 1024];

        if (plaintextStream.CanSeek)
            plaintextStream.Seek(0, SeekOrigin.Begin);

        int chunkIndex = 1;
        bool anyChunkProcessed = false;
        bool eof = false;

        while (!eof)
        {
            int chunkSize = chunkIndex < 8 ? chunkIndex * 131072 : 1048576;
            int chunkRemaining = chunkSize;
            int chunkBytesRead = 0;

            nonce8.CopyTo(chunkMac[..8]);
            nonce8.CopyTo(chunkMac.Slice(8, 8));

            while (chunkRemaining > 0)
            {
                int toRead = Math.Min(readBuf.Length, chunkRemaining);
                int got = ReadFullBlockAligned(plaintextStream, readBuf.AsSpan(0, toRead));
                if (got == 0)
                {
                    eof = true;
                    break;
                }

                chunkBytesRead += got;
                chunkRemaining -= got;

                int pos = 0;
                while (pos < got)
                {
                    int rem = got - pos;
                    if (rem >= 16)
                    {
                        for (int i = 0; i < 16; i++)
                            chunkMac[i] ^= readBuf[pos + i];
                    }
                    else
                    {
                        blockBuf.Clear();
                        readBuf.AsSpan(pos, rem).CopyTo(blockBuf);
                        for (int i = 0; i < 16; i++)
                            chunkMac[i] ^= blockBuf[i];
                    }
                    aes.EncryptEcb(chunkMac, chunkMac, PaddingMode.None);
                    pos += 16;
                }

                if (got < toRead)
                {
                    eof = true;
                    break;
                }
            }

            if (chunkBytesRead > 0 || !anyChunkProcessed)
            {
                for (int i = 0; i < 16; i++) fileMac[i] ^= chunkMac[i];
                aes.EncryptEcb(fileMac, fileMac, PaddingMode.None);
                anyChunkProcessed = true;
            }

            chunkIndex++;
        }

        byte[] metaMac = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            metaMac[i] = (byte)(fileMac[i] ^ fileMac[i + 4]);
            metaMac[i + 4] = (byte)(fileMac[i + 8] ^ fileMac[i + 12]);
        }
        return metaMac;
    }

    static int ReadFullBlockAligned(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Builds a 32-byte raw MEGA file key from an unfolded 16-byte AES key, 8-byte nonce, and plaintext
    /// so that <see cref="MegaResolver.UnfoldKey"/> produces <paramref name="aesKey16"/> and <paramref name="nonce8"/>
    /// while <c>rawKey[24..32]</c> contains the valid 8-byte meta-MAC.
    /// </summary>
    public static byte[] BuildKeyWithMetaMac(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aesKey16, ReadOnlySpan<byte> nonce8)
    {
        var metaMac = ComputeMetaMac(plaintext, aesKey16, nonce8);
        byte[] rawKey32 = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            rawKey32[i] = (byte)(aesKey16[i] ^ nonce8[i]);
            rawKey32[i + 8] = (byte)(aesKey16[i + 8] ^ metaMac[i]);
            rawKey32[i + 16] = nonce8[i];
            rawKey32[i + 24] = metaMac[i];
        }
        return rawKey32;
    }
}
