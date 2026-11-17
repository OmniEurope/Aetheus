// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1: the duration EF Core measured for every database command a test host runs, with the start of its text, so a
/// journey can name the slowest queries it ran. It changes no result; a test marks a position before its journey and
/// reads what came after it.
/// </summary>
public sealed class CommandTimingRecorder : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<(string Text, TimeSpan Duration)> _commands = [];

    public int Mark()
    {
        lock (_gate) return _commands.Count;
    }

    public IReadOnlyList<(string Text, TimeSpan Duration)> Since(int mark)
    {
        lock (_gate) return [.. _commands.Skip(mark)];
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command, CommandExecutedEventData eventData)
    {
        var text = command.CommandText.ReplaceLineEndings(" ");
        lock (_gate) _commands.Add((text.Length <= 240 ? text : text[..240], eventData.Duration));
    }
}
