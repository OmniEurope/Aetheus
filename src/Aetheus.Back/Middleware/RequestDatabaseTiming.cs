// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Middleware;

/// <summary>
/// What the database cost ONE request: number of commands, summed and longest command time, summed
/// commit time. Counters are updated with <see cref="Interlocked"/> because a request can run commands
/// concurrently.
/// </summary>
internal sealed class RequestDatabaseTime
{
    private long _commands;
    private long _commandTicks;
    private long _longestTicks;
    private long _commitTicks;

    public long Commands => Interlocked.Read(ref _commands);

    public TimeSpan CommandTime => TimeSpan.FromTicks(Interlocked.Read(ref _commandTicks));

    public TimeSpan LongestCommand => TimeSpan.FromTicks(Interlocked.Read(ref _longestTicks));

    public TimeSpan CommitTime => TimeSpan.FromTicks(Interlocked.Read(ref _commitTicks));

    internal void AddCommand(TimeSpan duration)
    {
        var ticks = Math.Max(0, duration.Ticks);
        Interlocked.Increment(ref _commands);
        Interlocked.Add(ref _commandTicks, ticks);
        var seen = Interlocked.Read(ref _longestTicks);
        while (ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref _longestTicks, ticks, seen);
            if (previous == seen)
                break;
            seen = previous;
        }
    }

    internal void AddCommit(TimeSpan duration) => Interlocked.Add(ref _commitTicks, Math.Max(0, duration.Ticks));
}

/// <summary>
/// Scopes a <see cref="RequestDatabaseTime"/> to the current request. The middleware starts it; the
/// accumulator flows with the execution context into the request's DbContext calls. Work outside a
/// request (background services) finds no accumulator, records nothing and allocates nothing.
/// </summary>
internal static class RequestDatabaseTiming
{
    private static readonly AsyncLocal<RequestDatabaseTime?> Scope = new();

    /// <summary>The accumulator of the request being served, or null outside a request.</summary>
    public static RequestDatabaseTime? Current => Scope.Value;

    /// <summary>Starts an accumulator for the current request and returns it.</summary>
    public static RequestDatabaseTime Begin()
    {
        var time = new RequestDatabaseTime();
        Scope.Value = time;
        return time;
    }

    /// <summary>Ends the request's scope; commands that follow are no longer counted.</summary>
    public static void End() => Scope.Value = null;

    public static void RecordCommand(TimeSpan duration) => Scope.Value?.AddCommand(duration);

    public static void RecordCommit(TimeSpan duration) => Scope.Value?.AddCommit(duration);
}
