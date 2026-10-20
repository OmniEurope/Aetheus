// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// The server side of a SignalR hub, in memory: a real <see cref="HubConnection"/> connects to it through
/// a pair of in-process pipes and speaks the JSON hub protocol (handshake, invocations, completions, server
/// pushes, close). It records what the client invoked, can refuse an invocation, can push an event and can
/// drop the connection, so a test exercises a component's real subscription (joins, handlers, reconnect)
/// without a socket, a host or reflection. Every change it sees is signalled, so a test awaits the state it
/// expects (<see cref="WhenAsync"/>) instead of polling it against the clock.
/// </summary>
internal sealed class InMemoryHubServer : IConnectionFactory
{
    private const byte RecordSeparator = 0x1e;
    private readonly object _gate = new();
    private readonly List<(string Target, JsonElement[] Arguments)> _invocations = [];
    private readonly Dictionary<string, string> _refusals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement[], object?>> _results = new(StringComparer.Ordinal);
    private readonly StateSignal _changes = new();
    private ServerConnection? _current;
    private int _connections;
    private int _attempts;
    private int _handshakes;
    private int _pushes;

    /// <summary>How many transports the client opened (the first start, then one per reconnect).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>How many of those transports completed the hub handshake.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    /// <summary>When set, opening a transport fails, as an unreachable hub does.</summary>
    public bool RefuseConnections { get; set; }

    /// <summary>Every attempt to open a transport, refused ones included.</summary>
    public int ConnectAttempts => Volatile.Read(ref _attempts);

    /// <summary>Every client invocation and send, in order, with its arguments.</summary>
    public IReadOnlyList<(string Target, JsonElement[] Arguments)> Invocations
    {
        get { lock (_gate) return [.. _invocations]; }
    }

    /// <summary>The integer first argument of every invocation of <paramref name="target"/>.</summary>
    public IReadOnlyList<int> IntArguments(string target) =>
        [.. Invocations.Where(invocation => invocation.Target == target).Select(invocation => invocation.Arguments[0].GetInt32())];

    public int Count(string target) => Invocations.Count(invocation => invocation.Target == target);

    /// <summary>Completes once <paramref name="condition"/> holds, re-checked on every connection attempt,
    /// handshake, invocation and lost transport this hub sees.</summary>
    public Task WhenAsync(Func<bool> condition, string what) => _changes.WhenAsync(condition, what);

    /// <summary>Completes once the client invoked <paramref name="target"/> at least <paramref name="count"/>
    /// times.</summary>
    public Task WhenInvokedAsync(string target, int count = 1) =>
        WhenAsync(() => Count(target) >= count, $"{count} invocation(s) of {target}");

    /// <summary>The hub answers every invocation of <paramref name="target"/> with this error.</summary>
    public void Refuse(string target, string error = "Access denied.")
    {
        lock (_gate) _refusals[target] = error;
    }

    public void Accept(string target)
    {
        lock (_gate) _refusals.Remove(target);
    }

    /// <summary>The hub answers every invocation of <paramref name="target"/> with this value.</summary>
    public void Returns(string target, Func<JsonElement[], object?> result)
    {
        lock (_gate) _results[target] = result;
    }

    /// <summary>
    /// Pushes an event to the connected client, as <c>Clients.Caller.SendAsync</c> would, and completes once
    /// the client has run every handler it registered for <paramref name="target"/> to its end. The push
    /// carries an invocation id, so the client answers it after awaiting its handlers (with "Client didn't
    /// provide a result." for a handler that returns none); that answer is the signal, not the clock.
    /// </summary>
    public async Task PushAsync(string target, params object?[] arguments)
    {
        var connection = _current ?? throw new InvalidOperationException("No client is connected.");
        using var guard = HangGuard.Start();
        await HangGuard.AwaitAsync(connection.Handshaken, guard, $"the handshake before pushing {target}");
        var handled = await connection.SendInvocationAsync(target, arguments);
        await HangGuard.AwaitAsync(handled, guard, $"the client handling {target}");
    }

    /// <summary>Ends the current transport without a close message: the client sees its connection lost.</summary>
    public async Task DropAsync()
    {
        var connection = _current ?? throw new InvalidOperationException("No client is connected.");
        _current = null;
        await connection.CompleteAsync();
        _changes.Pulse();
    }

    public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);
        if (RefuseConnections)
        {
            _changes.Pulse();
            return ValueTask.FromException<ConnectionContext>(new HttpRequestException("Test: hub not available"));
        }

        Interlocked.Increment(ref _connections);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var context = new DefaultConnectionContext(
            Guid.NewGuid().ToString("N"),
            new DuplexPipe(toClient.Reader, toServer.Writer),
            new DuplexPipe(toServer.Reader, toClient.Writer));
        var connection = new ServerConnection(this, toServer.Reader, toClient.Writer);
        _current = connection;
        _ = connection.RunAsync();
        _changes.Pulse();
        return ValueTask.FromResult<ConnectionContext>(context);
    }

    // Recorded here, signalled once the answer is written (see HandleAsync): a test woken by the
    // invocation and dropping the connection at once must not cut the answer the client is waiting for.
    private object Answer(string invocationId, string target, JsonElement[] arguments)
    {
        lock (_gate)
        {
            _invocations.Add((target, arguments));
            if (_refusals.TryGetValue(target, out var error))
                return new { type = 3, invocationId, error };
            if (_results.TryGetValue(target, out var result))
                return new { type = 3, invocationId, result = result(arguments) };
        }

        return new { type = 3, invocationId, result = (object?)null };
    }

    private void Record(string target, JsonElement[] arguments)
    {
        lock (_gate) _invocations.Add((target, arguments));
        _changes.Pulse();
    }

    private void Handshake()
    {
        Interlocked.Increment(ref _handshakes);
        _changes.Pulse();
    }

    private string NextPushId() => $"push-{Interlocked.Increment(ref _pushes)}";

    private sealed class ServerConnection(InMemoryHubServer server, PipeReader input, PipeWriter output)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly TaskCompletionSource _handshaken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, TaskCompletionSource> _pendingPushes = new(StringComparer.Ordinal);
        private bool _completed;

        /// <summary>Completes once the client's handshake was answered: a push sent earlier would reach the
        /// client before the handshake answer and break the protocol.</summary>
        public Task Handshaken => _handshaken.Task;

        public async Task RunAsync()
        {
            var handshaken = false;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync();
                    var buffer = read.Buffer;
                    while (TryReadRecord(ref buffer, out var record))
                    {
                        if (!handshaken)
                        {
                            handshaken = true;
                            await WriteRawAsync("{}");
                            _handshaken.TrySetResult();
                            server.Handshake();
                            continue;
                        }

                        await HandleAsync(record);
                    }

                    input.AdvanceTo(buffer.Start, buffer.End);
                    if (read.IsCompleted || read.IsCanceled) break;
                }

                await input.CompleteAsync();
            }
            finally
            {
                _handshaken.TrySetException(new InvalidOperationException("The client left before its handshake."));
                FailPendingPushes();
                server._changes.Pulse();
            }
        }

        /// <summary>Sends an invocation the client answers once its handlers ran; the task returned completes
        /// on that answer.</summary>
        public async Task<Task> SendInvocationAsync(string target, object?[] arguments)
        {
            var invocationId = server.NextPushId();
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingPushes) _pendingPushes[invocationId] = handled;
            await WriteAsync(new { type = 1, invocationId, target, arguments });
            return handled.Task;
        }

        private async Task HandleAsync(string record)
        {
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetInt32();
            if (type == 3)
            {
                CompletePush(root.GetProperty("invocationId").GetString()!);
                return;
            }

            if (type != 1) return;
            var target = root.GetProperty("target").GetString()!;
            var arguments = root.TryGetProperty("arguments", out var values)
                ? values.EnumerateArray().Select(value => value.Clone()).ToArray()
                : [];
            if (root.TryGetProperty("invocationId", out var id))
            {
                await WriteAsync(server.Answer(id.GetString()!, target, arguments));
                server._changes.Pulse();
            }
            else
                server.Record(target, arguments);
        }

        private void CompletePush(string invocationId)
        {
            TaskCompletionSource? handled;
            lock (_pendingPushes) _pendingPushes.Remove(invocationId, out handled);
            handled?.TrySetResult();
        }

        private void FailPendingPushes()
        {
            List<TaskCompletionSource> pending;
            lock (_pendingPushes)
            {
                pending = [.. _pendingPushes.Values];
                _pendingPushes.Clear();
            }

            foreach (var push in pending)
                push.TrySetException(new InvalidOperationException("The connection ended before the client handled the push."));
        }

        private static bool TryReadRecord(ref ReadOnlySequence<byte> buffer, out string record)
        {
            var position = buffer.PositionOf(RecordSeparator);
            if (position is null)
            {
                record = string.Empty;
                return false;
            }

            record = Encoding.UTF8.GetString(buffer.Slice(0, position.Value));
            buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            return true;
        }

        public Task WriteAsync(object message) =>
            WriteRawAsync(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        private async Task WriteRawAsync(string json)
        {
            await _write.WaitAsync();
            try
            {
                if (_completed) return;
                await output.WriteAsync(Encoding.UTF8.GetBytes(json + (char)RecordSeparator));
            }
            finally
            {
                _write.Release();
            }
        }

        public async Task CompleteAsync()
        {
            await _write.WaitAsync();
            try
            {
                _completed = true;
                await output.CompleteAsync();
            }
            finally
            {
                _write.Release();
            }

            FailPendingPushes();
        }
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}

/// <summary>
/// Builds every hub connection on an <see cref="InMemoryHubServer"/> (one per hub path), reconnecting at
/// once after a lost connection; the connections are real <see cref="HubConnection"/>s. Its clock does not
/// move by itself: the start deadline a subscription puts on these connections never cuts a start that a
/// loaded run delayed (a test of that deadline drives a clock of its own).
/// </summary>
internal sealed class InMemoryHubConnectionFactory(AuthStateProvider auth) : HubConnectionFactory(
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://in-memory" }).Build(),
    auth,
    NullLogger<AuthDelegatingHandler>.Instance,
    clock: new Microsoft.Extensions.Time.Testing.FakeTimeProvider())
{
    private readonly Dictionary<string, InMemoryHubServer> _servers = new(StringComparer.Ordinal);
    private int _created;

    public int Created => Volatile.Read(ref _created);

    public InMemoryHubServer Server(string hubPath)
    {
        lock (_servers)
        {
            if (!_servers.TryGetValue(hubPath, out var server))
                _servers[hubPath] = server = new InMemoryHubServer();
            return server;
        }
    }

    public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null)
    {
        Interlocked.Increment(ref _created);
        var builder = new HubConnectionBuilder();
        builder.Services.AddSingleton<IConnectionFactory>(Server(hubPath));
        builder.Services.AddSingleton<EndPoint>(new UriEndPoint(new Uri($"http://in-memory/hubs/{hubPath}")));
        builder.WithAutomaticReconnect(new ImmediateRetryPolicy());
        var connection = builder.Build();
        // The in-memory server sends no keep-alive and its handshake cannot be lost on a pipe, so the
        // client's two wall-clock watchdogs (15 s handshake, 30 s without a server message) have nothing to
        // watch here; left on, they cut a healthy connection whenever a loaded run held a continuation that
        // long. A day is "never" for a test.
        connection.HandshakeTimeout = TimeSpan.FromDays(1);
        connection.ServerTimeout = TimeSpan.FromDays(1);
        return connection;
    }

    private sealed class ImmediateRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < 20 ? TimeSpan.FromMilliseconds(20) : null;
    }
}

/// <summary>Puts a bUnit context's hub connections on in-memory hubs.</summary>
internal static class InMemoryHubs
{
    /// <summary>Registers an <see cref="InMemoryHubConnectionFactory"/> as the context's hub factory; call
    /// it before the first service is read.</summary>
    public static void UseInMemoryHubs(this Bunit.BunitContext context) =>
        context.Services.AddSingleton<HubConnectionFactory>(services =>
            new InMemoryHubConnectionFactory(services.GetRequiredService<AuthStateProvider>()));

    /// <summary>The in-memory hub of <paramref name="hubPath"/> the context's connections reach.</summary>
    public static InMemoryHubServer Hub(this Bunit.BunitContext context, string hubPath) =>
        ((InMemoryHubConnectionFactory)context.Services.GetRequiredService<HubConnectionFactory>()).Server(hubPath);
}
