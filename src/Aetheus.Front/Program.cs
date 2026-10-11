// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? LocalDevelopmentEndpoints.ApiHttpsBaseUrl;

if (builder.HostEnvironment.IsProduction())
{
    // Prod: silence the per-request HttpClient/Polly Information spam in the
    // browser console (CPU + noise on every API call). Dev stays verbose.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

builder.Services.AddSingleton<AuthStateProvider>();
builder.Services.AddSingleton<RealtimeSyncStatus>();
builder.Services.AddSingleton<HubConnectionFactory>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<YamlSerializationService>();
builder.Services.AddScoped<AlertNotificationService>();
builder.Services.AddScoped<UserNotificationService>();
builder.Services.AddScoped<RealtimeSessionLifecycle>();
builder.Services.AddTransient<AuthDelegatingHandler>();
builder.Services.AddTransient<ErrorNotificationHandler>();
builder.Services.AddTransient<BrowserNoStoreHandler>();
builder.Services.AddTransient<ServerClockHandler>();
builder.Services.AddSingleton<ClientApiTimings>();
builder.Services.AddTransient<ClientApiTimingHandler>();
builder.Services.AddSingleton<PageLoadActivity>();
// Recette R2-021: which front version this tab runs against the deployed one (approvals wait on it).
builder.Services.AddSingleton(sp => new Aetheus.Front.Layout.ApplicationVersionState(
    () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    new Uri(builder.HostEnvironment.BaseAddress),
    builder.Configuration["App:Version"] ?? "dev"));
builder.Services.AddTransient<PageLoadActivityHandler>();
builder.Services.AddHttpClient<ApiClient>(client =>
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

      // Never retry 429 (Too Many Requests) - each retry eats more rate-limit
      // budget, creating a death spiral under load. Only retry 5xx and transient
      // network errors.
      options.Retry.ShouldHandle = args => ValueTask.FromResult(
          args.Outcome.Exception is HttpRequestException or TimeoutException
          || (args.Outcome.Result is { IsSuccessStatusCode: false } r
              && (int)r.StatusCode >= 500));
  });
builder.Services.AddHttpClient<ClientErrorReporter>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
}).AddHttpMessageHandler<ClientApiTimingHandler>()
  .AddHttpMessageHandler<BrowserNoStoreHandler>()
  .AddHttpMessageHandler<AuthDelegatingHandler>();

// Breadcrumb
builder.Services.AddScoped<Aetheus.Front.Layout.BreadcrumbService>();
// Recette R-395: OE's breadcrumb service, which OmniPageHeader reads, installs the Aetheus route trail.
builder.Services.AddScoped<IOmniBreadcrumbResolver, Aetheus.Front.Layout.AetheusBreadcrumbResolver>();
builder.Services.AddScoped<Aetheus.Front.Components.Settings.SiteAppearanceState>();
builder.Services.AddScoped<Aetheus.Front.Components.Users.AdminIdentitySearch>();
builder.Services.AddScoped<Aetheus.Front.Layout.ProjectNavContextService>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.NotifyHelper>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.GridExportContext>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.ClipboardService>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.UiActions>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.AppDialogs>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.OmniDialogService>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.ConfirmHelper>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.PipelineRunGate>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.PipelineRunDialogCoordinator>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.PipelineImportState>();
builder.Services.AddScoped<Aetheus.Front.Components.Pipelines.PipelineSetupProvisionState>();
builder.Services.AddSingleton<Aetheus.Front.Components.Shared.ActiveOrganizationService>();
// Item #7: singleton (per Blazor WASM circuit = per browser session) so the hub stays alive
// across page navigations and every page sees the same in-flight task list.
builder.Services.AddSingleton<Aetheus.Front.Components.Shared.TaskTrackerService>();
// PLAN-007 lot 7: same lifetime and reason, one list of pending approvals for the home page and top bar.
builder.Services.AddSingleton<Aetheus.Front.Components.Shared.PendingApprovalsService>();
// R-115: same lifetime and reason, one unread count for the bell, the menu badge and /notifications.
builder.Services.AddSingleton<Aetheus.Front.Components.Notifications.UserNotificationsFeed>();
// Clock abstraction (used by ListCacheService TTL) - System provider in the browser; tests swap a fake.
builder.Services.AddSingleton(TimeProvider.System);
// 0-a: stale-while-revalidate cache for shared list components. Singleton so the cache survives
// navigations within the session (the whole point: show last-known list instantly, refresh behind).
builder.Services.AddSingleton<Aetheus.Front.Components.Shared.ListCacheService>();
// Recette R2-087: what a page last showed, for a return to it (same session-only memory).
builder.Services.AddSingleton<Aetheus.Front.Components.Shared.PageStateCache>();
// Item #1 scaffolding: scoped (per page activation) so the loader resets cleanly when the
// user closes the server detail view. SignalR group leave happens automatically on dispose.
builder.Services.AddScoped<Aetheus.Front.Components.Shared.ServerDetailLoader>();
builder.Services.AddScoped<Aetheus.Front.Components.Shared.ProjectDetailLoader>();
builder.Services.AddHttpClient<Aetheus.Front.Components.Shared.HelpService>((sp, client) =>
{
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    client.BaseAddress = new Uri(nav.BaseUri);
});

// OmniEurope.Blazor (ADR-046): the component library of the main UI.
builder.Services.AddOmniEuropeBlazor();
// PLAN-012: the settings every table shares, as the default preset of OmniDataGrid.
builder.Services.AddAetheusGridPresets();
// Recette R-130 / R-135: a text that belongs to Aetheus lives in Aetheus resources; a key named
// Omni_<package key> there replaces the OmniEurope text ("Se reconnecter" on the connection overlay).
builder.Services.AddOmniEuropeTextOverrides<Aetheus.Front.Resources.AppStrings>();

// Localization
builder.Services.AddLocalization();

var host = builder.Build();

var js = host.Services.GetRequiredService<IJSRuntime>();
var storedLang = await js.InvokeAsync<string?>("localStorage.getItem", StorageKeys.Lang);
if (!string.IsNullOrEmpty(storedLang))
{
    // index.html already applies the saved culture at boot via Blazor.start({ applicationCulture }).
    // This post-Build pass only reinforces the thread-default culture; it must NEVER crash the boot
    // on a malformed or legacy localStorage value (CultureNotFoundException) - a cosmetic language
    // setting should degrade to the default culture, not white-screen the whole app.
    try
    {
        var culture = new CultureInfo(storedLang);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
    catch (CultureNotFoundException)
    {
        // Ignore - boot continues with the browser/default culture.
    }
}

// Paint-yield: hand control back to the browser so the boot splash animation paints one frame
// before the (framework-internal) route scan + first render monopolise the main thread.
await Task.Delay(1);
await host.RunAsync();
