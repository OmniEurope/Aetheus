// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Layout;

/// <summary>
/// The front's start-up, taken out of <c>Program.cs</c> so it runs, and is verified, without a browser:
/// the service registrations, and the language remembered in the browser applied to the thread
/// defaults. <c>Program.cs</c> only builds the WebAssembly host around them and runs it.
/// </summary>
internal static class FrontHostSetup
{
    /// <summary>How often OmniPreferences re-reads the server's preferences while a tab stays open (R1-41).</summary>
    internal static readonly TimeSpan PreferencesSyncInterval = TimeSpan.FromMinutes(5);

    /// <summary>Registers every front service on <paramref name="services"/>; in Production the log level
    /// is raised to Warning.</summary>
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        IWebAssemblyHostEnvironment environment,
        ILoggingBuilder logging)
    {
        var apiBaseUrl = configuration["ApiBaseUrl"] ?? LocalDevelopmentEndpoints.ApiHttpsBaseUrl;

        if (environment.IsProduction())
        {
            // Prod: silence the per-request HttpClient/Polly Information spam in the
            // browser console (CPU + noise on every API call). Dev stays verbose.
            logging.SetMinimumLevel(LogLevel.Warning);
        }

        services.AddSingleton<AuthStateProvider>();
        services.AddSingleton<RealtimeSyncStatus>();
        services.AddSingleton<HubConnectionFactory>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<YamlSerializationService>();
        services.AddScoped<AlertNotificationService>();
        services.AddScoped<UserNotificationService>();
        services.AddScoped<RealtimeSessionLifecycle>();
        services.AddTransient<AuthDelegatingHandler>();
        services.AddTransient<ErrorNotificationHandler>();
        services.AddTransient<BrowserNoStoreHandler>();
        services.AddTransient<ServerClockHandler>();
        services.AddSingleton<ClientApiTimings>();
        services.AddTransient<ClientApiTimingHandler>();
        services.AddSingleton<PageLoadActivity>();
        // Which front version this tab runs against the deployed one (approvals wait on it).
        services.AddSingleton(sp => new Aetheus.Front.Components.Shared.ApplicationVersionState(
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            new Uri(environment.BaseAddress),
            configuration["App:Version"] ?? "dev"));
        services.AddTransient<PageLoadActivityHandler>();
        services.AddHttpClient<ApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
        }).AddHttpMessageHandler<PageLoadActivityHandler>()
          .AddHttpMessageHandler<ClientApiTimingHandler>()
          .AddHttpMessageHandler<BrowserNoStoreHandler>()
          .AddHttpMessageHandler<ServerClockHandler>()
          .AddHttpMessageHandler<AuthDelegatingHandler>()
          .AddHttpMessageHandler<ErrorNotificationHandler>()
          .AddStandardResilienceHandler(options =>
          {
              // Backend git operations are capped at 30s server-side. The client must
              // out-wait that within a SINGLE attempt; otherwise the per-attempt timeout
              // (10s default) fires early and Polly's retries spawn duplicate git
              // processes that pile up and starve the server - the root cause of the
              // general slowness. SamplingDuration must be >= 2x AttemptTimeout and
              // TotalRequestTimeout must be > AttemptTimeout (Polly validation).
              options.AttemptTimeout.Timeout = FrontendRuntimeDefaults.ApiAttemptTimeout;
              options.TotalRequestTimeout.Timeout = FrontendRuntimeDefaults.ApiTotalRequestTimeout;
              options.CircuitBreaker.SamplingDuration = FrontendRuntimeDefaults.ApiCircuitSamplingDuration;
              options.Retry.MaxRetryAttempts = FrontendRuntimeDefaults.ApiMaximumRetryAttempts;

              // Only 5xx and transient network errors, never 429 nor a token renewal (ApiRetryPolicy).
              options.Retry.ShouldHandle = Aetheus.Front.Components.Shared.ApiRetryPolicy.ShouldHandle;
          });
        services.AddHttpClient<ClientErrorReporter>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
        }).AddHttpMessageHandler<ClientApiTimingHandler>()
          .AddHttpMessageHandler<BrowserNoStoreHandler>()
          .AddHttpMessageHandler<AuthDelegatingHandler>();

        // Breadcrumb
        services.AddScoped<Aetheus.Front.Components.Shared.BreadcrumbService>();
        // OE's breadcrumb service, which OmniPageHeader reads, installs the Aetheus route trail.
        services.AddScoped<IOmniBreadcrumbResolver, Aetheus.Front.Components.Shared.AetheusBreadcrumbResolver>();
        services.AddScoped<Aetheus.Front.Components.Shared.ProjectNavContextService>();
        services.AddScoped<Aetheus.Front.Components.Shared.NotifyHelper>();
        services.AddScoped<Aetheus.Front.Components.Shared.GridExportContext>();
        services.AddScoped<Aetheus.Front.Components.Shared.ClipboardService>();
        services.AddScoped<Aetheus.Front.Components.Shared.UiActions>();
        // R-99: the veil of the page body while an action started from a confirmation runs.
        services.AddScoped<Aetheus.Front.Components.Shared.BusyVeil>();
        services.AddScoped<Aetheus.Front.Components.Shared.AppDialogs>();
        services.AddScoped<Aetheus.Front.Components.Shared.OmniDialogService>();
        services.AddScoped<Aetheus.Front.Components.Shared.ConfirmHelper>();
        services.AddScoped<Aetheus.Front.Components.Pipelines.PipelineRunGate>();
        services.AddScoped<Aetheus.Front.Components.Pipelines.PipelineRunDialogCoordinator>();
        services.AddScoped<Aetheus.Front.Components.Shared.PipelineImportState>();
        services.AddScoped<Aetheus.Front.Components.Pipelines.PipelineSetupProvisionState>();
        // Scoped (one scope per page in WASM, as a singleton was): it reads the user preferences, which are scoped.
        services.AddScoped<Aetheus.Front.Components.Shared.ActiveOrganizationService>();
        // The Administration section's organization switcher (owner decision 2026-10-28), same lifetime.
        services.AddScoped<Aetheus.Front.Components.Shared.AdministrationScopeService>();
        // Item #7: singleton (per Blazor WASM circuit = per browser session) so the hub stays alive
        // across page navigations and every page sees the same in-flight task list.
        services.AddSingleton<Aetheus.Front.Components.Shared.TaskTrackerService>();
        // Share the pending approvals list between the home page and top bar.
        services.AddSingleton<Aetheus.Front.Components.Shared.PendingApprovalsService>();
        // Same lifetime and reason, one unread count for the bell, the menu badge and /notifications.
        services.AddSingleton<Aetheus.Front.Components.Shared.UserNotificationsFeed>();
        // Clock abstraction (used by ListCacheService TTL) - System provider in the browser; tests swap a fake.
        services.AddSingleton(TimeProvider.System);
        // 0-a: stale-while-revalidate cache for shared list components. Singleton so the cache survives
        // navigations within the session (the whole point: show last-known list instantly, refresh behind).
        services.AddSingleton<Aetheus.Front.Components.Shared.ListCacheService>();
        // What a page last showed, for a return to it (same session-only memory).
        services.AddSingleton<Aetheus.Front.Components.Shared.PageStateCache>();
        // Item #1 scaffolding: scoped (per page activation) so the loader resets cleanly when the
        // user closes the server detail view. SignalR group leave happens automatically on dispose.
        services.AddScoped<Aetheus.Front.Components.Shared.ServerDetailLoader>();
        services.AddScoped<Aetheus.Front.Components.Shared.ProjectDetailLoader>();
        services.AddHttpClient<Aetheus.Front.Components.Shared.HelpService>((sp, client) =>
        {
            var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            client.BaseAddress = new Uri(nav.BaseUri);
        });

        // OmniEurope.Blazor (ADR-046): the component library of the main UI.
        services.AddOmniEuropeBlazor();
        // R1-66: every user preference (OE and Aetheus, the appearance in OE's OmniAppearanceState) is kept per
        // user on the server, cached in the browser; a change of user resets them without reloading the page.
        services.AddSingleton<Aetheus.Front.Components.Users.PreferenceScope>();
        // R1-41: the periodic synchronization polled GET api/users/me/preferences (OE default: every minute,
        // plus once per tab shown again). A user's preferences change only when that user changes them (at
        // most from another device), so a longer period is enough; the cache still follows the store when a
        // tab becomes visible and when a key is first read.
        services.AddOmniEuropePreferences<Aetheus.Front.Components.Users.ApiPreferenceStore>(
            options => options.SyncInterval = PreferencesSyncInterval);
        services.AddScoped<IUserPreferences, UserPreferences>();
        services.AddScoped<Aetheus.Front.Components.Settings.LegacyPreferenceMigration>();
        services.AddScoped<Aetheus.Front.Components.Settings.LegacyAppearanceMigration>();
        services.AddScoped<Aetheus.Front.Components.Settings.UserPreferenceSession>();
        // Register the common table settings as the OmniDataGrid default preset.
        services.AddAetheusGridPresets();
        // A text that belongs to Aetheus lives in Aetheus resources; a key named Omni_<package key> there
        // replaces the OmniEurope text ("Se reconnecter" on the connection overlay).
        services.AddOmniEuropeTextOverrides<Aetheus.Front.Resources.AppStrings>();

        // Localization
        services.AddLocalization();
    }

    /// <summary>Applies the language saved in the browser to the thread defaults. index.html already
    /// applies it at boot via <c>Blazor.start({ applicationCulture })</c>; this pass only reinforces it.</summary>
    public static async Task ApplyStoredCultureAsync(IJSRuntime js)
    {
        var culture = StoredCulture(await js.InvokeAsync<string?>("localStorage.getItem", StorageKeys.Lang));
        if (culture is null) return;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>The culture a saved language names, or <c>null</c> when none is saved or the value is not a
    /// culture: a malformed or legacy value must never crash the boot (CultureNotFoundException), a
    /// cosmetic language setting degrades to the default culture instead of white-screening the app.</summary>
    internal static CultureInfo? StoredCulture(string? storedLang)
    {
        if (string.IsNullOrEmpty(storedLang)) return null;
        try
        {
            return new CultureInfo(storedLang);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
