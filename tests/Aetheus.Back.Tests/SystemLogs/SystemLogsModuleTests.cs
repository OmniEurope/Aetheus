// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.Logs;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>The module hands the OmniEurope.Diagnostics journal the folder the Aetheus file logger writes to, the
/// same configuration key for both, and keeps the settings the host's own configuration gives.</summary>
public sealed class SystemLogsModuleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aetheus_module_{Guid.NewGuid():N}");

    public SystemLogsModuleTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(environment)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value))).Build())
            .AddSystemLogsModule()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task TheJournal_ReadsTheFolderOfTheFileLoggerSetting()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "aetheus-test.json"), "{}", TestContext.Current.CancellationToken);
        using var provider = Build(("Logging:FileLog:Directory", _directory));
        using var scope = provider.CreateScope();

        var files = await scope.ServiceProvider.GetRequiredService<ILogJournal>().ListFilesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("aetheus-test.json", Assert.Single(files).Name);
    }

    [Fact]
    public void WithoutASetting_TheJournalReadsTheLogsFolderNextToTheApplication()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<LogsOptions>>().Value;

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), options.Directory);
        Assert.Equal(SystemLogsModuleExtensions.MaxBytesRead, options.MaxBytesRead);
    }

    [Fact]
    public void TheOmniLogsSection_StillOverridesTheFolderAndTheReadCap()
    {
        using var provider = Build(
            ("Logging:FileLog:Directory", _directory),
            ("OmniLogs:Directory", Path.Combine(_directory, "elsewhere")),
            ("OmniLogs:MaxBytesRead", "1048576"));

        var options = provider.GetRequiredService<IOptions<LogsOptions>>().Value;

        Assert.Equal(Path.Combine(_directory, "elsewhere"), options.Directory);
        Assert.Equal(1_048_576, options.MaxBytesRead);
    }
}
