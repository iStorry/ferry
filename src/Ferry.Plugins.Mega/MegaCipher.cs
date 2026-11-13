using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Ferry.Plugins.Mega;

/// <summary>What a Mega file key holds: the AES key, the counter's nonce, and the check on the whole file.</summary>
internal static class MegaKeys
{
    public const int AesKeyBytes = 16;
    public const int NonceBytes = 8;
    public const int MetaMacBytes = 8;
    public const int RawFileKeyBytes = AesKeyBytes + NonceBytes + MetaMacBytes;

    /// <summary>The parts of a file's 32-byte key: the first half laid over the second is the AES key, and the second half is the nonce and the check.</summary>
    public static (byte[] Key, byte[] Nonce, byte[] MetaMac) Split(byte[] raw)
    {
        var key = new byte[AesKeyBytes];
        for (var i = 0; i < AesKeyBytes; i++)
            key[i] = (byte)(raw[i] ^ raw[i + AesKeyBytes]);
        return (key, raw[AesKeyBytes..(AesKeyBytes + NonceBytes)], raw[(AesKeyBytes + NonceBytes)..RawFileKeyBytes]);
    }
}

/// <summary>
/// What a file's key does to its bytes, which go through here in order from the start. AES in counter mode, which .NET has
/// no mode for, decrypts: each block's counter encrypted and laid over the data, so a transfer can carry on from any byte.
/// Mega's check on the whole file goes along: a CBC-MAC of each chunk of the decrypted bytes (chunks grow by 128 KB up to
/// 1 MB), those chained into one MAC, and that folded in half to the eight bytes at the end of the key.
/// </summary>
internal sealed class MegaCipher : IDisposable
{
    private const int BlockBytes = 16;
    private const int ChunkStep = 128 << 10;
    private const int LargestChunk = 1 << 20;

    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _nonce;
    private readonly byte[] _metaMac;
    private readonly byte[] _chunkMac = new byte[BlockBytes];
    private readonly byte[] _fileMac = new byte[BlockBytes];
    private readonly byte[] _block = new byte[BlockBytes];
    private int _inBlock;
    private long _position;
    private long _chunkEnd;
    private int _chunkSize;

    public MegaCipher(byte[] rawKey)
    {
        (_aes.Key, _nonce, _metaMac) = MegaKeys.Split(rawKey);
        Restart();
    }

    /// <summary>How many bytes have gone through.</summary>
    public long Position => _position;

    /// <summary>Back to the start of the file, for when the whole of it comes again.</summary>
    public void Restart()
    {
        (_position, _chunkEnd, _chunkSize, _inBlock) = (0, 0, 0, 0);
        _fileMac.AsSpan().Clear();
        NextChunk();
    }

    /// <summary>Decrypt the file's next bytes in place, and count them towards the check.</summary>
    public void Decrypt(Span<byte> data)
    {
        Counter(data);
        Count(data);
    }

    /// <summary>Count bytes an earlier try decrypted already towards the check.</summary>
    public void Count(ReadOnlySpan<byte> plain)
    {
        while (plain.Length > 0)
        {
            var take = (int)Math.Min(plain.Length, _chunkEnd - _position);
            Mac(plain[..take]);
            plain = plain[take..];
            _position += take;
            if (_position == _chunkEnd)
                CloseChunk();
        }
    }

    /// <summary>Whether everything that went through is the file the key is for; false for a wrong key or bytes changed on the way.</summary>
    public bool Matches()
    {
        Span<byte> chunkMac = stackalloc byte[BlockBytes];
        Span<byte> fileMac = stackalloc byte[BlockBytes];
        _fileMac.CopyTo(fileMac);
        if (_position > _chunkEnd - _chunkSize)
        {
            // the last chunk, cut short by the end of the file
            if (_inBlock > 0)
            {
                _block.AsSpan(_inBlock).Clear();
                _aes.TryEncryptCbc(_block, _chunkMac, chunkMac, out _, PaddingMode.None);
            }
            else
            {
                _chunkMac.CopyTo(chunkMac);
            }
            Chain(fileMac, chunkMac);
        }
        Span<byte> condensed = stackalloc byte[MegaKeys.MetaMacBytes];
        for (var i = 0; i < MegaKeys.MetaMacBytes; i++)
            condensed[i] = (byte)(fileMac[i < 4 ? i : i + 4] ^ fileMac[i < 4 ? i + 4 : i + 8]);
        return condensed.SequenceEqual(_metaMac);
    }

    public void Dispose() => _aes.Dispose();

    /// <summary>Lay the counter blocks' encryption over <paramref name="data"/>, all at once rather than a block at a time.</summary>
    private void Counter(Span<byte> data)
    {
        if (data.Length == 0)
            return;
        var skip = (int)(_position % BlockBytes);
        var blocks = (skip + data.Length + BlockBytes - 1) / BlockBytes;
        var counters = ArrayPool<byte>.Shared.Rent(blocks * BlockBytes);
        var masks = ArrayPool<byte>.Shared.Rent(blocks * BlockBytes);
        try
        {
            var first = _position / BlockBytes;
            for (var i = 0; i < blocks; i++)
            {
                _nonce.CopyTo(counters, i * BlockBytes);
                BinaryPrimitives.WriteInt64BigEndian(counters.AsSpan((i * BlockBytes) + MegaKeys.NonceBytes), first + i);
            }
            _aes.TryEncryptEcb(counters.AsSpan(0, blocks * BlockBytes), masks, PaddingMode.None, out _);
            Xor(data, masks.AsSpan(skip, data.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counters);
            ArrayPool<byte>.Shared.Return(masks);
        }
    }

    /// <summary>Bytes within the current chunk: whole blocks go through CBC at once, a block's worth of rest waits for the next.</summary>
    private void Mac(ReadOnlySpan<byte> plain)
    {
        if (_inBlock > 0)
        {
            var fill = Math.Min(BlockBytes - _inBlock, plain.Length);
            plain[..fill].CopyTo(_block.AsSpan(_inBlock));
            _inBlock += fill;
            plain = plain[fill..];
            if (_inBlock < BlockBytes)
                return;
            MacBlocks(_block);
            _inBlock = 0;
        }
        var whole = plain.Length - (plain.Length % BlockBytes);
        if (whole > 0)
            MacBlocks(plain[..whole]);
        plain[whole..].CopyTo(_block);
        _inBlock = plain.Length - whole;
    }

    /// <summary>The chunk's MAC is the last block of CBC-encrypting its bytes, from the MAC so far as the IV.</summary>
    private void MacBlocks(ReadOnlySpan<byte> blocks)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(blocks.Length);
        try
        {
            _aes.TryEncryptCbc(blocks, _chunkMac, scratch, out var written, PaddingMode.None);
            scratch.AsSpan(written - BlockBytes, BlockBytes).CopyTo(_chunkMac);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private void CloseChunk()
    {
        if (_inBlock > 0)
        {
            _block.AsSpan(_inBlock).Clear();
            MacBlocks(_block);
            _inBlock = 0;
        }
        Chain(_fileMac, _chunkMac);
        NextChunk();
    }

    /// <summary>The file's MAC takes in a chunk's: the two laid over each other, encrypted.</summary>
    private void Chain(Span<byte> fileMac, ReadOnlySpan<byte> chunkMac)
    {
        Span<byte> mixed = stackalloc byte[BlockBytes];
        fileMac.CopyTo(mixed);
        Xor(mixed, chunkMac);
        _aes.TryEncryptEcb(mixed, fileMac, PaddingMode.None, out _);
    }

    /// <summary>A fresh chunk MAC (the nonce twice over), and where the chunk ends.</summary>
    private void NextChunk()
    {
        _nonce.CopyTo(_chunkMac, 0);
        _nonce.CopyTo(_chunkMac, MegaKeys.NonceBytes);
        _chunkSize = Math.Min(_chunkSize + ChunkStep, LargestChunk);
        _chunkEnd += _chunkSize;
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> mask)
    {
        var i = 0;
        for (; i + Vector<byte>.Count <= data.Length; i += Vector<byte>.Count)
            (new Vector<byte>(data[i..]) ^ new Vector<byte>(mask[i..])).CopyTo(data[i..]);
        for (; i < data.Length; i++)
            data[i] ^= mask[i];
    }
}
