using Ferry.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Ferry.Plugins.Server;

/// <summary>The PC's end of a connection to the agent: numbered requests with their answers, and the agent's events.</summary>
internal sealed class AgentClient : IAsyncDisposable
{
    private readonly AgentLines _lines;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentMessage>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _closing = new();
    private readonly Task _reading;
    private readonly Action<string, Exception>? _log;
    private int _lastId;
    private int _unreadable;
    private bool _closed;

    /// <param name="onEvent">each event, in order, on the reading thread</param>
    /// <param name="onClosed">once, when the connection ends for any reason</param>
    /// <param name="log">where a line from the agent that isn't a message is noted; nowhere when null</param>
    public AgentClient(Stream input, Stream output, Action<AgentMessage> onEvent, Action onClosed, Action<string, Exception>? log = null)
    {
        _lines = new AgentLines(input, output);
        _log = log;
        _reading = ReadAllAsync(onEvent, onClosed);
    }

    /// <summary>Lines from the agent that weren't messages (something else wrote to the channel), passed over.</summary>
    public int UnreadableLines => Volatile.Read(ref _unreadable);

    /// <summary>The connection has ended: nothing more will be answered.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_closing)
                return _closed;
        }
    }

    /// <summary>Send <paramref name="request"/> (its id is filled in) and wait for the answer, which may carry an error.</summary>
    /// <exception cref="StageException">the connection is lost (<see cref="Stage.Server"/>, retryable)</exception>
    public async Task<AgentMessage> SendAsync(AgentRequest request, CancellationToken cancel = default)
    {
        var id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_closing)
        {
            _pending[id] = answer;
            if (_closed)
                answer.TrySetException(Lost());
        }
        try
        {
            await _lines.WriteAsync(request with { Id = id }, AgentJson.Default.AgentRequest, cancel);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw Lost(e);
        }
        await using (cancel.Register(() => answer.TrySetCanceled(cancel)))
        {
            try
            {
                return await answer.Task;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _lines.DisposeAsync();
        try
        {
            await _reading;
        }
        catch (OperationCanceledException)
        {
            // stopped on purpose
        }
        _stop.Dispose();
    }

    private async Task ReadAllAsync(Action<AgentMessage> onEvent, Action onClosed)
    {
        await Task.Yield();
        try
        {
            while (await _lines.ReadAsync(_stop.Token) is { } line)
            {
                AgentMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize(line, AgentJson.Default.AgentMessage);
                }
                catch (JsonException e)
                {
                    // nothing to do with a line that isn't a message, but it is counted and noted: the channel carries only messages
                    Interlocked.Increment(ref _unreadable);
                    _log?.Invoke("a line from the agent that isn't a message", e);
                    continue;
                }
                if (message?.Answers is { } id && _pending.TryGetValue(id, out var answer))
                    answer.TrySetResult(message);
                else if (message?.Event is not null)
                    onEvent(message);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the connection ended
        }
        finally
        {
            lock (_closing)
            {
                _closed = true;
                foreach (var answer in _pending.Values)
                    answer.TrySetException(Lost());
            }
            onClosed();
        }
    }

    private static StageException Lost(Exception? inner = null) =>
        new(Stage.Server, "The connection to the server was lost.", isRetryable: true, inner: inner);
}
