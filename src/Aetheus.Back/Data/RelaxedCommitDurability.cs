// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Aetheus.Back.Data;

/// <summary>
/// Marks the work of the current request as telemetry whose last moments may be lost if PostgreSQL itself crashes
/// (R-1, slow requests). A commit normally waits for the write-ahead log to reach the disk; when the host's disk is
/// busy (a pipeline build on the same VPS) that wait was the whole duration of the slow <c>/api/logs/batch</c>,
/// probe-result and OTLP ingestion calls of the system logs (commit 1.5 to 4.2 s, every other command a few
/// milliseconds). Inside the scope the connections the context opens run <c>SET synchronous_commit = off</c>: the
/// commit returns at once, the server flushes the log within about 600 ms, and a crash of PostgreSQL can lose only
/// those last transactions, never leave the database inconsistent (commits stay in order).
/// </summary>
/// <remarks>
/// Never use it for state another party relies on once answered: a task claim, a lease, a token, a completion.
/// </remarks>
public static class RelaxedCommitDurability
{
    private static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    /// <summary>Starts the scope for the current execution flow; dispose to end it.</summary>
    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Scope(Active, previous);
    }

    private sealed class Scope(AsyncLocal<bool> slot, bool previous) : IDisposable
    {
        public void Dispose() => slot.Value = previous;
    }
}

/// <summary>The action (or every action of the controller) runs in a <see cref="RelaxedCommitDurability"/> scope.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RelaxedCommitDurabilityAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using (RelaxedCommitDurability.Begin())
            await next().ConfigureAwait(false);
    }
}

/// <summary>
/// Applies <see cref="RelaxedCommitDurability"/> to the connections the application context opens: the setting is
/// made when a connection opens inside the scope and undone before it returns to the pool, so a pooled connection
/// never carries it into work that needs a durable commit.
/// </summary>
internal sealed class RelaxedCommitConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ConditionalWeakTable<DbConnection, object> _relaxed = new();

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!RelaxedCommitDurability.IsActive)
            return;
        await ExecuteAsync(connection, relax: true, cancellationToken).ConfigureAwait(false);
        _relaxed.AddOrUpdate(connection, string.Empty);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!RelaxedCommitDurability.IsActive)
            return;
        Execute(connection, relax: true);
        _relaxed.AddOrUpdate(connection, string.Empty);
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (_relaxed.Remove(connection) && connection.State == System.Data.ConnectionState.Open)
            await ExecuteAsync(connection, relax: false, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (_relaxed.Remove(connection) && connection.State == System.Data.ConnectionState.Open)
            Execute(connection, relax: false);
        return result;
    }

    // Literal statements on an NpgsqlCommand, as everywhere else the application talks to PostgreSQL directly (CA2100).
    private static async Task ExecuteAsync(DbConnection connection, bool relax, CancellationToken ct)
    {
        var npgsql = (NpgsqlConnection)connection;
        await using var command = relax
            ? new NpgsqlCommand("SET synchronous_commit = off", npgsql)
            : new NpgsqlCommand("RESET synchronous_commit", npgsql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Execute(DbConnection connection, bool relax)
    {
        var npgsql = (NpgsqlConnection)connection;
        using var command = relax
            ? new NpgsqlCommand("SET synchronous_commit = off", npgsql)
            : new NpgsqlCommand("RESET synchronous_commit", npgsql);
        command.ExecuteNonQuery();
    }
}
