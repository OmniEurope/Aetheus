// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests.Services;

/// <summary>The development server warns, loudly, when the linux-x64 agent publish it serves is older than
/// the latest agent build; it says nothing when the publish is fresh, within the jitter slack, or when
/// either output is absent (a deployed server).</summary>
public sealed class AgentPublishStalenessWarningTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-publish-stale-");
    private readonly List<(LogLevel Level, string Message)> _logs = [];

    public void Dispose() => _root.Delete(recursive: true);

    private sealed class Capture(List<(LogLevel Level, string Message)> sink) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
        public void Dispose()
        {
        }
    }

    private WebApplication App()
    {
        // The solution root is two levels above the content root, as in the repository (src/Aetheus.Back).
        var contentRoot = Directory.CreateDirectory(Path.Combine(_root.FullName, "src", "Aetheus.Back")).FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Capture(_logs));
        return builder.Build();
    }

    private string Touch(string relativePath, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static readonly string Publish = Path.Combine("src", "Aetheus.Agent.Linux", "bin", "Debug", "net10.0", "linux-x64", "publish", "Aetheus.Agent.Core.dll");
    private static readonly string Build = Path.Combine("src", "Aetheus.Agent.Core", "bin", "Debug", "net10.0", "Aetheus.Agent.Core.dll");
    private static readonly DateTime BuiltAt = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task APublishOlderThanTheBuild_WarnsWithBothTimes()
    {
        Touch(Publish, BuiltAt.AddMinutes(-10));
        Touch(Build, BuiltAt);
        await using var app = App();

        AgentDownloadsExtensions.WarnIfLinuxAgentPublishStale(app);

        var warning = Assert.Single(_logs, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("STALE", warning.Message, StringComparison.Ordinal);
        Assert.Contains("2026-10-16 11:50:00Z", warning.Message, StringComparison.Ordinal);
        Assert.Contains("2026-10-16 12:00:00Z", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(30)]
    public async Task APublishAsFreshAsTheBuild_OrWithinFiveSecondsOfIt_SaysNothing(int publishOffsetSeconds)
    {
        Touch(Publish, BuiltAt.AddSeconds(publishOffsetSeconds));
        Touch(Build, BuiltAt);
        await using var app = App();

        AgentDownloadsExtensions.WarnIfLinuxAgentPublishStale(app);

        Assert.DoesNotContain(_logs, entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task WhenEitherOutputIsAbsent_SaysNothing(bool publishExists, bool buildExists)
    {
        if (publishExists) Touch(Publish, BuiltAt.AddHours(-5));
        if (buildExists) Touch(Build, BuiltAt);
        await using var app = App();

        AgentDownloadsExtensions.WarnIfLinuxAgentPublishStale(app);

        Assert.DoesNotContain(_logs, entry => entry.Level >= LogLevel.Warning);
    }
}
