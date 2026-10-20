// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Layout;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// The front's start-up outside the browser: the API clients point at the configured backend (the local
/// one when none is configured), Production raises the log level to Warning and nothing else does, the
/// services the pages depend on are registered, and a saved language is read without ever failing the boot.
/// </summary>
public sealed class FrontHostSetupTests
{
    private static ServiceCollection Configure(string environment, Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        FrontHostSetup.ConfigureServices(
            services, configuration, new HostEnvironment(environment), new LoggingBuilder(services));
        return services;
    }

    private static Uri? BaseAddressOf<TClient>(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(typeof(TClient).Name);
        using var client = new HttpClient();
        foreach (var action in options.HttpClientActions) action(client);
        return client.BaseAddress;
    }

    [Fact]
    public void TheApiClients_UseTheConfiguredBackend()
    {
        using var provider = Configure("Development", new() { ["ApiBaseUrl"] = "https://api.example.test/" })
            .BuildServiceProvider();

        Assert.Equal(new Uri("https://api.example.test/"), BaseAddressOf<ApiClient>(provider));
        Assert.Equal(new Uri("https://api.example.test/"), BaseAddressOf<ClientErrorReporter>(provider));
    }

    [Fact]
    public void WithoutAConfiguredBackend_TheApiClientsUseTheLocalOne()
    {
        using var provider = Configure("Development").BuildServiceProvider();

        Assert.Equal(new Uri(LocalDevelopmentEndpoints.ApiHttpsBaseUrl), BaseAddressOf<ApiClient>(provider));
    }

    [Fact]
    public void Production_LogsFromWarning_AndDevelopmentStaysVerbose()
    {
        using var production = Configure("Production").BuildServiceProvider();
        using var development = Configure("Development").BuildServiceProvider();

        Assert.Equal(LogLevel.Warning, production.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.MinLevel);
        Assert.Equal(LogLevel.Information,
            development.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.MinLevel);
    }

    [Fact]
    public void TheSessionServices_AreRegisteredWithTheirLifetimes()
    {
        var services = Configure("Development");

        Assert.Equal(ServiceLifetime.Singleton, Lifetime<AuthStateProvider>(services));
        Assert.Equal(ServiceLifetime.Singleton, Lifetime<HubConnectionFactory>(services));
        Assert.Equal(ServiceLifetime.Singleton, Lifetime<TaskTrackerService>(services));
        Assert.Equal(ServiceLifetime.Singleton, Lifetime<PageStateCache>(services));
        Assert.Equal(ServiceLifetime.Scoped, Lifetime<ServerDetailLoader>(services));
        Assert.Equal(ServiceLifetime.Scoped, Lifetime<IUserPreferences>(services));
        Assert.Equal(ServiceLifetime.Transient, Lifetime<AuthDelegatingHandler>(services));
        using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.NotNull(provider.GetRequiredService<ApplicationVersionState>());
    }

    private static ServiceLifetime Lifetime<TService>(IServiceCollection services) =>
        services.Last(descriptor => descriptor.ServiceType == typeof(TService)).Lifetime;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@@ not a culture @@")]
    public void ASavedLanguageThatNamesNoCulture_IsIgnored(string? saved) =>
        Assert.Null(FrontHostSetup.StoredCulture(saved));

    [Fact]
    public void ASavedLanguage_NamesItsCulture() =>
        Assert.Equal("fr-FR", FrontHostSetup.StoredCulture("fr-FR")?.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("@@ not a culture @@")]
    public async Task ApplyingNoUsableSavedLanguage_LeavesTheThreadDefaultsAlone(string? saved)
    {
        var culture = CultureInfo.DefaultThreadCurrentCulture;
        var uiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var js = new SavedLanguage(saved);

        await FrontHostSetup.ApplyStoredCultureAsync(js);

        Assert.Equal(("localStorage.getItem", StorageKeys.Lang), (js.Identifier, js.Key));
        Assert.Same(culture, CultureInfo.DefaultThreadCurrentCulture);
        Assert.Same(uiCulture, CultureInfo.DefaultThreadCurrentUICulture);
    }

    private sealed class SavedLanguage(string? value) : IJSRuntime
    {
        public string? Identifier { get; private set; }
        public object? Key { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Identifier = identifier;
            Key = args?.SingleOrDefault();
            return ValueTask.FromResult((TValue)(object?)value!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class HostEnvironment(string environment) : IWebAssemblyHostEnvironment
    {
        public string Environment { get; } = environment;
        public string BaseAddress => "https://front.example.test/";
    }

    private sealed class LoggingBuilder(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
