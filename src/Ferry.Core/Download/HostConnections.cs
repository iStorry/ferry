using System.Collections.Concurrent;

namespace Ferry.Core.Download;

/// <summary>
/// The most connections open to one server at a time, over every download that shares this instance, so that four
/// files at eight connections each can't open 32 connections to one host. A connection waits for its turn
/// before it takes a part of its file; turns are handed out in the order they were asked for.
/// </summary>
public sealed class HostConnections(int perHost)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _perHost = Math.Max(1, perHost);

    /// <summary>
    /// What to share from now on when the cap may have changed: this instance if it hasn't, so connections already open
    /// still count, else a new one. A turn taken from this one still frees its place when it ends.
    /// </summary>
    public HostConnections WithCap(int perHost) => Math.Max(1, perHost) == _perHost ? this : new HostConnections(perHost);

    /// <summary>Wait for a free connection to <paramref name="host"/>; disposing the turn frees it again.</summary>
    public async ValueTask<Turn> WaitAsync(string host, CancellationToken cancel)
    {
        var gate = _hosts.GetOrAdd(host, _ => new SemaphoreSlim(_perHost, _perHost));
        await gate.WaitAsync(cancel);
        return new Turn(gate);
    }

    /// <summary>One connection's turn; the default value holds none.</summary>
    public readonly struct Turn(SemaphoreSlim? gate) : IDisposable
    {
        public void Dispose() => gate?.Release();
    }
}
