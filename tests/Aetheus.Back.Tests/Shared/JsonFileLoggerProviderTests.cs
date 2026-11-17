// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Extensions;
using Aetheus.Back.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests;

public sealed class JsonFileLoggerProviderTests
{
    [Fact]
    public void StructuredCorrelationId_IsWrittenAsDedicatedJsonProperty()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-logger-{Guid.NewGuid():N}");
        try
        {
            using (var provider = new JsonFileLoggerProvider(directory, LogLevel.Information))
            {
                var logger = provider.CreateLogger("CorrelationTest");
                logger.LogError("Failure; correlation {CorrelationId}", "request-42");
            }

            var content = string.Join(Environment.NewLine, Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.Contains("\"CorrelationId\":\"request-42\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saturation_IncrementsCounter_AndWritesAggregatedWarning()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-logger-{Guid.NewGuid():N}");
        var drainGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using (var provider = new JsonFileLoggerProvider(
                       directory, LogLevel.Information, TimeProvider.System, capacity: 1, drainGate.Task))
            {
                var logger = provider.CreateLogger("SaturationTest");
                logger.LogInformation("first entry fills the bounded channel");
                logger.LogInformation("second entry must be rejected");

                Assert.Equal(1, provider.DroppedEntries);
                drainGate.SetResult();
            }

            var content = string.Join(Environment.NewLine, Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.Contains("File logger saturated: 1 entries were dropped (total 1).", content, StringComparison.Ordinal);
            Assert.Contains("JsonFileLoggerProvider", content, StringComparison.Ordinal);
        }
        finally
        {
            drainGate.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A provider added as an instance is not disposed by the host: its writer kept running after the host was
    /// gone, so a late line (the host's own shutdown lines) could be appended while a test deleted the log directory
    /// (HubRefusalLoggingIntegrationTests: "the process cannot access the file ... being used by another process").
    /// Disposed with the host, the writer is drained and finished: nothing is accepted afterwards.</summary>
    [Fact]
    public async Task TheHostFileLogger_IsDisposedWithTheHost_SoItsWriterIsFinished()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-logger-{Guid.NewGuid():N}");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["Logging:FileLog:Directory"] = directory;
            builder.AddAetheusFileLogging();
            var app = builder.Build();
            var provider = app.Services.GetServices<ILoggerProvider>().OfType<JsonFileLoggerProvider>().Single();

            await app.DisposeAsync();

            provider.CreateLogger("AfterTheHost").LogError("a line after the host was disposed");
            Assert.Equal(1, provider.DroppedEntries);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void R1_82_TwoInstancesSharingOneDirectory_WriteOneFileEach()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aetheus-logger-{Guid.NewGuid():N}");
        try
        {
            foreach (var instance in new[] { "prod-blue-back", "prod-green-back" })
            {
                using var provider = new JsonFileLoggerProvider(directory, LogLevel.Information, instance: instance);
                provider.CreateLogger("Shared").LogError("written by {Instance}", instance);
            }

            var names = Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(2, names.Count);
            Assert.StartsWith("aetheus-prod-blue-back-", names[0], StringComparison.Ordinal);
            Assert.StartsWith("aetheus-prod-green-back-", names[1], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "aetheus")]
    [InlineData("", "aetheus")]
    [InlineData("blue/../back", "aetheus-blue..back")]
    [InlineData("web 1", "aetheus-web1")]
    public void R1_82_TheFileStem_KeepsOnlySafeCharacters(string? instance, string expected) =>
        Assert.Equal(expected, JsonFileLoggerProvider.FileStem(instance));

    [Theory]
    [InlineData("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", typeof(Microsoft.AspNetCore.SignalR.HubException), true)]
    [InlineData("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", typeof(InvalidOperationException), false)]
    [InlineData("Aetheus.Back.Hubs.PipelineHub", typeof(Microsoft.AspNetCore.SignalR.HubException), false)]
    public void TheHubDispatcherErrorEcho_IsDroppedOnlyForAHubExceptionFromTheDispatcher(string category, Type exceptionType, bool dropped) =>
        Assert.Equal(dropped, JsonFileLogger.IsDeliberateHubRefusalEcho(category, (Exception)Activator.CreateInstance(exceptionType, "x")!));

    [Fact]
    public void ALogWithoutException_IsNeverTakenForAHubRefusalEcho() =>
        Assert.False(JsonFileLogger.IsDeliberateHubRefusalEcho("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", null));
}
