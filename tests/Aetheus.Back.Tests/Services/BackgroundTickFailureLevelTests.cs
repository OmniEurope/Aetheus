// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;

namespace Aetheus.Back.Tests.Services;

/// <summary>
/// R-2 (the 02:05 cut): the export held two Error lines from workers whose tick failed because the database was
/// restarting ("Agent update coordinator iteration failed", "Error during backend app probing tick"). A database that
/// restarts or cannot be reached is not a failure of the worker: one warning per outage (information when the server
/// ended the session itself), the following ticks quiet, and a real failure still an error.
/// </summary>
public sealed class BackgroundTickFailureLevelTests
{
    private static NpgsqlException DatabaseDown() =>
        new("Failed to connect to 10.0.13.2:5432", new SocketException((int)SocketError.ConnectionRefused));

    private static NpgsqlException SessionEnded() =>
        new("connection broke", new PostgresException(
            "terminating connection due to administrator command", "FATAL", "FATAL", "57P01"));

    [Fact]
    public void ADatabaseThatCannotBeReached_IsOneWarningPerOutage()
    {
        var level = new BackgroundTickFailureLevel();

        Assert.Equal(LogLevel.Warning, level.For(DatabaseDown(), CancellationToken.None));
        Assert.Equal(LogLevel.Debug, level.For(DatabaseDown(), CancellationToken.None));

        level.Succeeded();

        Assert.Equal(LogLevel.Warning, level.For(DatabaseDown(), CancellationToken.None));
    }

    [Fact]
    public void ASessionTheDatabaseEnded_IsInformation()
    {
        Assert.Equal(LogLevel.Information, new BackgroundTickFailureLevel().For(SessionEnded(), CancellationToken.None));
    }

    [Fact]
    public void AnyOtherFailure_StaysAnError_EvenAfterAnOutage()
    {
        var level = new BackgroundTickFailureLevel();
        level.For(DatabaseDown(), CancellationToken.None);

        Assert.Equal(LogLevel.Error, level.For(new InvalidOperationException("boom"), CancellationToken.None));
    }

    [Fact(Timeout = 30_000)]
    public async Task TheCoordinatorTellsATickThatMetARestartingDatabase_AsAWarning_NotAnError()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(_ => throw DatabaseDown());
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 1, 5, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<AgentUpdateCoordinatorService>();
        using var service = new AgentUpdateCoordinatorService(scopes, Substitute.For<IHubContext<ServerHub>>(), time, logger, leaderLease: LeaderLeaseDouble.Leader());

        await service.StartAsync(TestContext.Current.CancellationToken);
        // The loop's timer may be armed a moment after StartAsync returns: advance the clock until a tick has run.
        while (logger.Entries.Count == 0)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await service.StopAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }
}
