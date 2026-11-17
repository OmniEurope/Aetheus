// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Counts the SQL commands EF Core executes inside one measured call, and only those: the count is
/// carried by the call's execution context, so the host's background services (leader leases,
/// schedulers) that query the same database at the same time are not counted.
/// </summary>
internal sealed class SqlCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private static readonly AsyncLocal<Tally?> Current = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _listeners;

    public SqlCommandCounter() => _listeners = DiagnosticListener.AllListeners.Subscribe(this);

    /// <summary>The call's result, its duration, its SQL command count and the time those commands took.</summary>
    internal sealed record Measure<T>(T Result, TimeSpan Elapsed, int Commands, TimeSpan SqlTime)
    {
        /// <summary>How many of <see cref="Commands"/> read role permissions: the authorization checks of
        /// the call, told apart from its other work (e-mail rendering, inserts).</summary>
        public int PermissionCommands { get; init; }

        /// <summary>The text of every command the call sent, in order.</summary>
        public IReadOnlyList<string> Statements { get; init; } = [];
    }

    private sealed class Tally(SqlCommandCounter owner)
    {
        /// <summary>The counter that opened the measure. Every live counter observes the same EF Core
        /// listener, so a counter alive in a test class running in parallel would otherwise count this
        /// measure's commands a second time.</summary>
        public readonly SqlCommandCounter Owner = owner;
        public int Commands;
        public int PermissionCommands;
        public long SqlTicks;
        public readonly List<string> Statements = [];
    }

    /// <summary>Runs <paramref name="action"/>, timing it and counting the SQL commands it sent.</summary>
    public async Task<Measure<T>> MeasureAsync<T>(Func<Task<T>> action)
    {
        var tally = new Tally(this);
        Current.Value = tally;
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        stopwatch.Stop();
        return new Measure<T>(result, stopwatch.Elapsed, Volatile.Read(ref tally.Commands), TimeSpan.FromTicks(Interlocked.Read(ref tally.SqlTicks)))
        {
            PermissionCommands = Volatile.Read(ref tally.PermissionCommands),
            Statements = [.. tally.Statements]
        };
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name != DbLoggerCategory.Name) return;
        lock (_subscriptions)
            _subscriptions.Add(value.Subscribe(this, name => name == RelationalEventId.CommandExecuted.Name));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (Current.Value is not { } tally || !ReferenceEquals(tally.Owner, this)) return;
        Interlocked.Increment(ref tally.Commands);
        if (value.Value is CommandExecutedEventData executed)
        {
            Interlocked.Add(ref tally.SqlTicks, executed.Duration.Ticks);
            lock (tally.Statements) tally.Statements.Add(executed.Command.CommandText);
            if (executed.Command.CommandText.Contains("\"ResourcePermissions\"", StringComparison.Ordinal))
                Interlocked.Increment(ref tally.PermissionCommands);
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose()
    {
        _listeners.Dispose();
        lock (_subscriptions)
            foreach (var subscription in _subscriptions) subscription.Dispose();
    }
}
