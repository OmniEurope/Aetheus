// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.Middleware;

/// <summary>
/// Feeds <see cref="RequestDatabaseTiming"/> with the execution time EF Core measured for each command
/// (failed ones included). It changes no result and no behaviour.
/// </summary>
internal sealed class RequestDatabaseTimingCommandInterceptor : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        RequestDatabaseTiming.RecordCommand(eventData.Duration);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        RequestDatabaseTiming.RecordCommand(eventData.Duration);
        return Task.CompletedTask;
    }
}

/// <summary>Feeds <see cref="RequestDatabaseTiming"/> with the time each transaction commit took.</summary>
internal sealed class RequestDatabaseTimingTransactionInterceptor : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        RequestDatabaseTiming.RecordCommit(eventData.Duration);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        RequestDatabaseTiming.RecordCommit(eventData.Duration);
        return Task.CompletedTask;
    }
}
