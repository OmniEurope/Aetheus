// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Aetheus.Back.Middleware;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-6: the database time of ONE request (commands, longest command, commits) is accumulated through the
/// EF Core interceptors, only inside a request scope, and never from work outside a request.
/// </summary>
public sealed class RequestDatabaseTimingTests : IDisposable
{
    private readonly RequestDatabaseTimingCommandInterceptor _commands = new();
    private readonly RequestDatabaseTimingTransactionInterceptor _transactions = new();
    private readonly DbCommand _command = Substitute.For<DbCommand>();

    public void Dispose() => RequestDatabaseTiming.End();

    private static CommandExecutedEventData Executed(int milliseconds) => new(
        eventDefinition: null!, messageGenerator: null!, connection: null!, command: null!, logCommandText: "select 1",
        context: null!, executeMethod: DbCommandMethod.ExecuteReader, commandId: Guid.NewGuid(), connectionId: Guid.NewGuid(),
        result: null!, async: true, logParameterValues: false, startTime: DateTimeOffset.UtcNow,
        duration: TimeSpan.FromMilliseconds(milliseconds), commandSource: CommandSource.LinqQuery);

    private static CommandErrorEventData Failed(int milliseconds) => new(
        eventDefinition: null!, messageGenerator: null!, connection: null!, command: null!, logCommandText: "select 1",
        context: null!, executeMethod: DbCommandMethod.ExecuteNonQuery, commandId: Guid.NewGuid(), connectionId: Guid.NewGuid(),
        exception: new InvalidOperationException("boom"), async: false, logParameterValues: false, startTime: DateTimeOffset.UtcNow,
        duration: TimeSpan.FromMilliseconds(milliseconds), commandSource: CommandSource.SaveChanges);

    private static TransactionEndEventData Committed(int milliseconds) => new(
        eventDefinition: null!, messageGenerator: null!, transaction: null!, context: null!, transactionId: Guid.NewGuid(),
        connectionId: Guid.NewGuid(), async: true, startTime: DateTimeOffset.UtcNow, duration: TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public async Task InsideARequestScope_CommandsAreCounted_SummedAndTheLongestIsKept()
    {
        var time = RequestDatabaseTiming.Begin();

        await _commands.ReaderExecutedAsync(_command, Executed(40), null!, TestContext.Current.CancellationToken);
        _commands.NonQueryExecuted(_command, Executed(250), 1);
        await _commands.ScalarExecutedAsync(_command, Executed(10), 1, TestContext.Current.CancellationToken);
        _commands.ReaderExecuted(_command, Executed(90), null!);
        await _commands.NonQueryExecutedAsync(_command, Executed(5), 1, TestContext.Current.CancellationToken);
        _commands.ScalarExecuted(_command, Executed(5), 1);

        Assert.Equal(6, time.Commands);
        Assert.Equal(TimeSpan.FromMilliseconds(400), time.CommandTime);
        Assert.Equal(TimeSpan.FromMilliseconds(250), time.LongestCommand);
        Assert.Equal(TimeSpan.Zero, time.CommitTime);
    }

    [Fact]
    public async Task AFailedCommand_IsCountedToo()
    {
        var time = RequestDatabaseTiming.Begin();

        _commands.CommandFailed(_command, Failed(30));
        await _commands.CommandFailedAsync(_command, Failed(70), TestContext.Current.CancellationToken);

        Assert.Equal(2, time.Commands);
        Assert.Equal(TimeSpan.FromMilliseconds(100), time.CommandTime);
        Assert.Equal(TimeSpan.FromMilliseconds(70), time.LongestCommand);
    }

    [Fact]
    public async Task CommitTime_IsSummed_AndKeptApartFromTheCommands()
    {
        var time = RequestDatabaseTiming.Begin();

        _transactions.TransactionCommitted(null!, Committed(12));
        await _transactions.TransactionCommittedAsync(null!, Committed(30), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(42), time.CommitTime);
        Assert.Equal(0, time.Commands);
    }

    [Fact]
    public async Task OutsideARequestScope_NothingIsCounted_AndNothingIsAllocated()
    {
        RequestDatabaseTiming.End();

        _commands.ReaderExecuted(_command, Executed(500), null!);
        await _commands.NonQueryExecutedAsync(_command, Executed(500), 1, TestContext.Current.CancellationToken);
        _commands.CommandFailed(_command, Failed(500));
        _transactions.TransactionCommitted(null!, Committed(500));

        Assert.Null(RequestDatabaseTiming.Current);
    }

    [Fact]
    public async Task CommandsAfterTheRequestEnded_AreNotAddedToItsFigures()
    {
        var time = RequestDatabaseTiming.Begin();
        await _commands.ReaderExecutedAsync(_command, Executed(20), null!, TestContext.Current.CancellationToken);
        RequestDatabaseTiming.End();

        await _commands.ReaderExecutedAsync(_command, Executed(900), null!, TestContext.Current.CancellationToken);

        Assert.Equal(1, time.Commands);
        Assert.Equal(TimeSpan.FromMilliseconds(20), time.CommandTime);
    }

    [Fact]
    public async Task AWorkerStartedInsideTheRequest_FeedsTheRequest_AndOneStartedOutsideDoesNot()
    {
        // Started before the scope: its execution context carries no accumulator. It is held until the scope
        // has begun, so its command runs while the request is being served, not by luck of timing.
        var scopeBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outside = Task.Run(async () =>
        {
            await scopeBegun.Task;
            _commands.ReaderExecuted(_command, Executed(700), null!);
        }, TestContext.Current.CancellationToken);

        var time = RequestDatabaseTiming.Begin();
        scopeBegun.SetResult();
        await Task.Run(() => _commands.ReaderExecuted(_command, Executed(15), null!), TestContext.Current.CancellationToken);
        await outside;

        Assert.Equal(1, time.Commands);
        Assert.Equal(TimeSpan.FromMilliseconds(15), time.CommandTime);
    }

    [Fact]
    public async Task ConcurrentCommands_AreAllCounted()
    {
        var time = RequestDatabaseTiming.Begin();

        await Task.WhenAll(Enumerable.Range(1, 200).Select(i => Task.Run(() => RequestDatabaseTiming.RecordCommand(TimeSpan.FromMilliseconds(i)), TestContext.Current.CancellationToken)));

        Assert.Equal(200, time.Commands);
        Assert.Equal(TimeSpan.FromMilliseconds(200 * 201 / 2), time.CommandTime);
        Assert.Equal(TimeSpan.FromMilliseconds(200), time.LongestCommand);
    }
}
