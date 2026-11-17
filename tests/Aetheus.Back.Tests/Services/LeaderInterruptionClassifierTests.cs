// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using Aetheus.Back.Services;
using Npgsql;

namespace Aetheus.Back.Tests.Services;

/// <summary>R-2 (a): a restart of the backend or of the database is told at information level; only a real failure
/// of the worker stays critical.</summary>
public class LeaderInterruptionClassifierTests
{
    private static PostgresException Postgres(string sqlState) =>
        new("terminating connection due to administrator command", "FATAL", "FATAL", sqlState);

    [Fact]
    public void ACancelledHost_IsAStop_WhateverTheException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(LeaderInterruption.HostStopping,
            LeaderInterruptionClassifier.Classify(new InvalidOperationException("boom"), cts.Token));
    }

    [Fact]
    public void ASessionEndedOnAdministratorCommand_IsADatabaseRestart()
    {
        var wrapped = new NpgsqlException("connection broke", Postgres("57P01"));

        Assert.Equal(LeaderInterruption.DatabaseStoppedSession,
            LeaderInterruptionClassifier.Classify(wrapped, CancellationToken.None));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.TimedOut)]
    public void ADatabaseThatCannotBeReached_IsUnavailable(SocketError error)
    {
        var wrapped = new NpgsqlException("Failed to connect", new SocketException((int)error));

        Assert.Equal(LeaderInterruption.DatabaseUnavailable,
            LeaderInterruptionClassifier.Classify(wrapped, CancellationToken.None));
    }

    /// <summary>R-1: what Npgsql throws when the database container is killed or its host goes down (read from a real
    /// PostgreSQL killed with docker kill), which the nightly reboot does. It was a Critical line.</summary>
    [Fact]
    public void AConnectionThatEndedWithoutAWord_IsUnavailable() =>
        Assert.Equal(LeaderInterruption.DatabaseUnavailable,
            LeaderInterruptionClassifier.Classify(
                new NpgsqlException("Exception while reading from stream", new EndOfStreamException()), CancellationToken.None));

    [Fact]
    public void AnEndOfStreamThatIsNotTheDatabases_IsARealFailure() =>
        Assert.Equal(LeaderInterruption.Failure,
            LeaderInterruptionClassifier.Classify(new EndOfStreamException("a truncated file"), CancellationToken.None));

    [Fact]
    public void ADatabaseStillStarting_IsUnavailable() =>
        Assert.Equal(LeaderInterruption.DatabaseUnavailable,
            LeaderInterruptionClassifier.Classify(Postgres("57P03"), CancellationToken.None));

    [Theory]
    [InlineData("23505")]
    [InlineData("42P01")]
    [InlineData("40001")]
    public void AnyOtherDatabaseError_IsARealFailure(string sqlState) =>
        Assert.Equal(LeaderInterruption.Failure,
            LeaderInterruptionClassifier.Classify(Postgres(sqlState), CancellationToken.None));

    [Fact]
    public void AnExceptionOfTheWorker_IsARealFailure() =>
        Assert.Equal(LeaderInterruption.Failure,
            LeaderInterruptionClassifier.Classify(new InvalidOperationException("reconciliation failed"), CancellationToken.None));

    [Fact]
    public void ASocketErrorThatIsNotAnOutage_IsARealFailure() =>
        Assert.Equal(LeaderInterruption.Failure,
            LeaderInterruptionClassifier.Classify(new SocketException((int)SocketError.AccessDenied), CancellationToken.None));
}
