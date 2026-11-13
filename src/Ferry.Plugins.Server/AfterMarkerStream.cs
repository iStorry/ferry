using System.Text;

namespace Ferry.Plugins.Server;

/// <summary>
/// A command's stdout from <see cref="Marker"/> on. A server's shell startup files can print anything before the command
/// runs (a welcome, a quota, a fortune), which would otherwise be taken for the agent's answer or a file's first bytes.
/// With no marker (the stream ended first, or it never comes in the first bytes) what was read is passed on as it is.
/// </summary>
internal sealed class AfterMarkerStream(Stream inner) : Stream
{
    /// <summary>Printed on a line of its own by every command, before it does anything else.</summary>
    public const string Marker = ":ferry-output-begins:";

    private const int MaxNoiseBytes = 64 << 10;
    private static readonly byte[] MarkerLine = Encoding.ASCII.GetBytes(Marker + "\n");

    private ReadOnlyMemory<byte> _rest;
    private bool _found;

    /// <summary>A command's whole stdout, as text, from the marker on.</summary>
    public static string TextAfter(string output)
    {
        var at = output.IndexOf(Marker + "\n", StringComparison.Ordinal);
        return at < 0 ? output : output[(at + Marker.Length + 1)..];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_found)
            await FindAsync(cancellationToken);
        if (_rest.Length == 0)
            return await inner.ReadAsync(buffer, cancellationToken);
        var count = Math.Min(buffer.Length, _rest.Length);
        _rest[..count].CopyTo(buffer);
        _rest = _rest[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async Task FindAsync(CancellationToken cancel)
    {
        var seen = new byte[MaxNoiseBytes];
        var length = 0;
        while (length < seen.Length)
        {
            var read = await inner.ReadAsync(seen.AsMemory(length), cancel);
            if (read == 0)
                break;
            length += read;
            var at = seen.AsSpan(0, length).IndexOf(MarkerLine);
            if (at >= 0)
            {
                (_rest, _found) = (seen.AsMemory((at + MarkerLine.Length)..length), true);
                return;
            }
        }
        (_rest, _found) = (seen.AsMemory(0, length), true);
    }
}
