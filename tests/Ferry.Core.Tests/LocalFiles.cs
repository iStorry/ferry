using System.Collections.Concurrent;
using Ferry.Core.Files;
using Ferry.Plugins.Server;

namespace Ferry.Core.Tests;

/// <summary>
/// Stands in for the agent's reads over ssh: reads "server" files straight from the local disk, a part at a time, with
/// failures to order (drop the connection at an offset, hold at an offset, corrupt what arrives), each happening once
/// unless said otherwise.
/// </summary>
internal sealed class LocalFiles : IRemoteFiles
{
    private int _dropped, _corruptOpens;

    /// <summary>The offset of every read opened, in order.</summary>
    public ConcurrentQueue<long> Opened { get; } = new();

    /// <summary>The length of every read opened, in order.</summary>
    public ConcurrentQueue<long> Lengths { get; } = new();

    public long? DropOnceAt { get; set; }

    /// <summary>Reads from this offset wait for <see cref="Release"/>.</summary>
    public long? HoldAt { get; set; }

    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long a read waits before it opens, as one waiting its turn to log in does.</summary>
    public TimeSpan OpenWait { get; set; }

    /// <summary>This many opened reads flip the first byte they send.</summary>
    public int CorruptOpens
    {
        get => Volatile.Read(ref _corruptOpens);
        set => Volatile.Write(ref _corruptOpens, value);
    }

    public void Release() => Released.TrySetResult();

    public async Task<Stream> OpenReadAsync(RemoteFile file, long offset, long length, CancellationToken cancel)
    {
        await Task.Delay(OpenWait, cancel);
        Opened.Enqueue(offset);
        Lengths.Enqueue(length);
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        stream.Position = offset;
        return new FaultyStream(stream, offset, offset + length, this, Interlocked.Decrement(ref _corruptOpens) >= 0);
    }

    private sealed class FaultyStream(FileStream inner, long position, long end, LocalFiles faults, bool corrupt) : Stream
    {
        private bool _corrupt = corrupt;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (faults.HoldAt is { } hold && position >= hold && !faults.Released.Task.IsCompleted)
            {
                faults.Held.TrySetResult();
                await faults.Released.Task.WaitAsync(cancel);
            }
            if (faults.DropOnceAt is { } drop && position >= drop && Interlocked.Exchange(ref faults._dropped, 1) == 0)
                throw new IOException("connection reset (test)");
            if (position >= end)
                return 0; // the part asked for is all there
            var limit = (int)Math.Min(buffer.Length, end - position);
            foreach (var at in new[] { faults.HoldAt, faults.DropOnceAt })
            {
                if (at is { } offset && offset > position)
                    limit = (int)Math.Min(limit, offset - position);
            }
            var read = await inner.ReadAsync(buffer[..limit], cancel);
            if (read > 0 && _corrupt)
            {
                buffer.Span[0] ^= 0xFF;
                _corrupt = false;
            }
            position += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
