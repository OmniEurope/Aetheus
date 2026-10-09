// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniEurope.Performance;

namespace Aetheus.Telemetry.Tests;

public sealed class AetheusTelemetryBuilderExtensionsTests : IDisposable
{
    private readonly string? _originalEnabled = Environment.GetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED");

    [Fact]
    public void AddAetheusTelemetry_Disabled_RegistersOptionsWithoutProviders()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", null);
        var services = new ServiceCollection();

        services.AddAetheusTelemetry(Configuration(("Aetheus:Telemetry:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<AetheusTelemetryOptions>().Enabled);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.FullName?.Contains("TracerProvider", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AddAetheusTelemetry_Enabled_RegistersOnceAndUsesBoundedOptions()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();
        var configuration = ValidConfiguration();

        services.AddAetheusTelemetry(configuration);
        var countAfterFirstCall = services.Count;
        services.AddAetheusTelemetry(configuration);

        Assert.Equal(countAfterFirstCall, services.Count);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AetheusTelemetryOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(0.25, options.TraceSampleRatio);
        Assert.Equal(1024, options.MaxExportQueueSize);
    }

    [Fact]
    public void AddAetheusTelemetry_RejectsHttpEndpointWhenEnabled()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();
        var configuration = ValidConfiguration(("Aetheus:Telemetry:MetricsEndpoint", "http://aetheus/metrics"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddAetheusTelemetry(configuration));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAetheusTelemetry_RejectsInvalidSampleRatio()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAetheusTelemetry(ValidConfiguration(("Aetheus:Telemetry:TraceSampleRatio", "1.1"))));

        Assert.Contains("TraceSampleRatio", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAetheusTelemetry_DoesNotRequireLogsEndpointWhenLogsAreDisabled()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();

        services.AddAetheusTelemetry(ValidConfiguration(
            ("Aetheus:Telemetry:LogsEndpoint", string.Empty),
            ("Aetheus:Telemetry:EnableLogs", "false")));

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType.FullName?.Contains("TracerProvider", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AddAetheusTelemetry_Enabled_RegistersTheCollectorAndItsGaugesOnce_Over24HoursByDefault()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration());

        services.AddAetheusTelemetry(ValidConfiguration());
        services.AddAetheusTelemetry(ValidConfiguration());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(RequestPerformanceRecorder));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(RequestPerformanceMetrics));
        // Not resolving the hosted services keeps the OTLP providers unbuilt: nothing is exported here.
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<RequestPerformanceMetrics>());
        Assert.Equal(TimeSpan.FromHours(24), provider.GetRequiredService<IOptions<PerformanceOptions>>().Value.Window);
    }

    [Fact]
    public void AHostThatRegisteredTheCollectorFirst_KeepsTheWindowAndFilterItChose()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration());
        Func<string, bool> apiOnly = route => route.StartsWith("/api/", StringComparison.Ordinal);

        services.AddOmniPerformanceCollector(options => options.RouteFilter = apiOnly);
        services.AddAetheusTelemetry(ValidConfiguration());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PerformanceOptions>>().Value;
        Assert.Equal(TimeSpan.FromDays(7), options.Window); // the package default the host kept
        Assert.Same(apiOnly, options.RouteFilter);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(RequestPerformanceRecorder));
    }

    [Fact]
    public void TheOmniPerformanceWindowSetting_WinsOverThe24HourDefault()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration(("OmniPerformance:Window", "02:00:00")));

        services.AddAetheusTelemetry(ValidConfiguration());

        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromHours(2), provider.GetRequiredService<IOptions<PerformanceOptions>>().Value.Window);
    }

    [Fact]
    public void AddAetheusTelemetry_Disabled_MeasuresNoRequest()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "false");
        var services = new ServiceCollection();

        services.AddAetheusTelemetry(ValidConfiguration());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(RequestPerformanceRecorder));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(RequestPerformanceMetrics));
    }

    [Fact]
    public void PLAN013_TheDatabaseSize_IsMeasuredOnlyWhenTheHostHandsOverAConnection()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED", null);
        var without = new ServiceCollection();
        without.AddAetheusTelemetry(ValidConfiguration());
        Assert.DoesNotContain(without, descriptor => descriptor.ServiceType == typeof(DatabaseSizeMetrics));

        var with = new ServiceCollection();
        with.AddAetheusTelemetry(
            ValidConfiguration(("Aetheus:Telemetry:DatabaseSize:IntervalMinutes", "30")),
            options => options.DatabaseSize.UsePostgreSql(() => throw new InvalidOperationException("not opened")));

        Assert.Single(with, descriptor => descriptor.ServiceType == typeof(DatabaseSizeMetrics));
        using var provider = with.BuildServiceProvider();
        var databaseSize = provider.GetRequiredService<DatabaseSizeOptions>();
        Assert.True(databaseSize.Enabled);
        Assert.Equal(30, databaseSize.IntervalMinutes);
        Assert.Equal(DatabaseSizeOptions.DefaultTopTables, databaseSize.TopTables);
    }

    [Fact]
    public void PLAN013_TheEnvironmentVariable_TurnsTheDatabaseSizeOff_AndOnWithoutAConnectionIsRefused()
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        try
        {
            Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED", "false");
            var off = new ServiceCollection();
            off.AddAetheusTelemetry(ValidConfiguration(),
                options => options.DatabaseSize.UsePostgreSql(() => throw new InvalidOperationException("not opened")));
            Assert.DoesNotContain(off, descriptor => descriptor.ServiceType == typeof(DatabaseSizeMetrics));

            Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED", "true");
            var exception = Assert.Throws<InvalidOperationException>(
                () => new ServiceCollection().AddAetheusTelemetry(ValidConfiguration()));
            Assert.Contains("connection factory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED", null);
        }
    }

    [Theory]
    [InlineData("IntervalMinutes", "4", "IntervalMinutes")]
    [InlineData("IntervalMinutes", "1441", "IntervalMinutes")]
    [InlineData("TopTables", "0", "TopTables")]
    [InlineData("TopTables", "101", "TopTables")]
    public void PLAN013_TheDatabaseSizeBounds_AreChecked(string key, string value, string expected)
    {
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", "true");
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED", null);

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAetheusTelemetry(
            ValidConfiguration(($"Aetheus:Telemetry:DatabaseSize:{key}", value)),
            options => options.DatabaseSize.UsePostgreSql(() => throw new InvalidOperationException("not opened"))));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable("AETHEUS_TELEMETRY_ENABLED", _originalEnabled);

    private static IConfiguration ValidConfiguration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Aetheus:Telemetry:Enabled"] = "true",
            ["Aetheus:Telemetry:ApplicationName"] = "test-app",
            ["Aetheus:Telemetry:ApplicationId"] = "42",
            ["Aetheus:Telemetry:EnvironmentName"] = "qa",
            ["Aetheus:Telemetry:MetricsEndpoint"] = "https://aetheus.example/api/ingest/otlp/v1/metrics",
            ["Aetheus:Telemetry:LogsEndpoint"] = "https://aetheus.example/api/ingest/otlp/v1/logs",
            ["Aetheus:Telemetry:TracesEndpoint"] = "https://aetheus.example/api/ingest/otlp/v1/traces",
            ["Aetheus:Telemetry:IngestKey"] = "secret",
            ["Aetheus:Telemetry:TraceSampleRatio"] = "0.25",
            ["Aetheus:Telemetry:MaxExportQueueSize"] = "1024"
        };

        foreach (var (key, value) in overrides)
            values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value))
            .Build();
}
