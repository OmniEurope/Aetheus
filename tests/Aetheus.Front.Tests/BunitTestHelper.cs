// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using System.Text.Json;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

internal static class BunitTestHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Bound of a wait whose state arrives through an asynchronous continuation (a mocked HTTP answer
    /// resumed on the thread pool, then marshalled back to the renderer). It was a ten-second ceiling, and
    /// with every test class running at once a correct page still missed it: the wait is for an event, so
    /// its only bound is the hang guard (see <see cref="TestHostRenderWaits"/>).
    /// </summary>
    internal static readonly TimeSpan AsyncRenderWait = HangGuard.Limit;

    /// <summary>
    /// The front is a standalone WebAssembly app, always interactive. OE 1.6.0's forms read RendererInfo on an
    /// invalid submit (they focus the first invalid field only when a script can run), which bUnit refuses
    /// until it is set. Setting it builds the renderer, which closes the service collection: call it once the
    /// component is rendered, never from <see cref="RegisterServices"/>.
    /// </summary>
    public static void Interactive(BunitContext ctx) =>
        ctx.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("WebAssembly", isInteractive: true));

    public static TestHandler RegisterServices(BunitContext ctx, bool authenticated = true, bool isAdmin = false,
        IReadOnlyCollection<string>? extraPermissionKeys = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Blazor's Virtualize sizes its window from a real scroll viewport measured through JS. bUnit
        // has neither, so a virtualised grid renders zero rows and every assertion on a cell fails for
        // a reason unrelated to the component. Rendering the full set here keeps those tests about what
        // the grid shows; that virtualization is actually declared on every grid is enforced separately
        // by GridCapabilityAuditTests, and its behaviour is verified in the browser.
        AetheusGrid.Virtualization = false;
        AetheusGrid.RemoteVirtualization = false;

        var handler = new TestHandler();
        handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        // Every logout tells the server first; it answers 204.
        handler.SetResponse(HttpMethod.Post, "api/auth/logout", HttpStatusCode.NoContent);
        // The user administers no organization through the switcher unless a test says otherwise.
        handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto>());
        // No pipeline requirement is unmet unless a test says otherwise.
        handler.SetJsonResponse("api/pipelines/setup/unmet", new List<UnmetRequirementDto>());
        // Nothing stops a project's or a pipeline's runs unless a test says otherwise.
        handler.SetJsonResponse("api/pipelines/setup/project-readiness", new PipelineSetupReadinessDto());
        handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness", new PipelineSetupReadinessDto());
        // The wizard's production setup reads nothing from the repository unless a test says it does (ADR-057).
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/production-analysis", new PipelineProductionAnalysisDto());
        // External repositories are always on; no project has one unless a test says so.
        handler.SetResponse("api/external-repos/project/", System.Net.HttpStatusCode.NotFound);
        // A run with no successful predecessor has no usual durations.
        handler.SetJsonResponse("/stage-baselines", new RunStageBaselinesDto());
        // No approval waits unless a test says otherwise (top bar, home page).
        handler.SetJsonResponse("api/pipelines/approvals/pending", new List<PendingApprovalDto>());
        // R-43: the run page also reads the pipeline's first page of runs for its average duration; shortest key, so a test's own runs response wins.
        handler.SetPaginatedJsonResponse("/runs?page", new List<PipelineRunDto>());
        // Nothing reads a library or a vault unless a test says otherwise (the "Used by" line and column).
        handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto());
        // R-69: the Certbot page reads its "expires soon" horizon; 30 days (the former fixed one) unless a test says otherwise.
        handler.SetJsonResponse("api/certbot/expiry-warning", new CertificateExpiryWarningDto());
        // Default to no notifications, preferences or subscriptions unless overridden by a test.
        handler.SetJsonResponse("api/notifications/me/unread-count", 0);
        handler.SetPaginatedJsonResponse("api/notifications/me?", new List<NotificationDeliveryDto>());
        handler.SetJsonResponse("api/notifications/me/preferences", new List<NotificationPreferenceDto>());
        handler.SetJsonResponse("api/notifications/me/subscriptions", new List<ProjectSubscriptionDto>());
        handler.SetJsonResponse("api/users/me/preferences", new List<UserPreferenceDto>());
        handler.SetResponse(HttpMethod.Get, "health/live", HttpStatusCode.ServiceUnavailable);
        // The deployed front is the one the test runs unless a test says otherwise.
        handler.SetRawResponse("appsettings.json", "{\"App\":{\"Version\":\"test-version\"}}");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        var apiClient = new ApiClient(http);

        ctx.Services.AddSingleton(apiClient);
        ctx.Services.AddSingleton<PageLoadActivity>();
        // The overlay service counts toast lifetimes on a clock that stands still, so a toast a
        // test raised is still there when the test reads it however slow the run; NotifyHelperTests
        // moves a clock of its own to prove the durations. Registered before AddOmniEuropeBlazor,
        // whose TryAdd then keeps it.
        ctx.Services.AddScoped(_ => new OmniOverlayService(new FakeTimeProvider()));
        ctx.Services.AddOmniEuropeBlazor();
        ctx.Services.AddAetheusGridPresets();
        ctx.Services.AddScoped<AppDialogs>();
        ctx.Services.AddScoped<OmniDialogService>();

        var authJs = Substitute.For<IJSRuntime>();
        var testToken = authenticated ? CreateTestToken(isAdmin) : null;
        authJs.InvokeAsync<string?>(
                "localStorage.getItem",
                Arg.Is<object?[]>(args => args.Length == 1 && Equals(args[0], StorageKeys.AuthToken)))
            .Returns(ValueTask.FromResult(testToken));
        authJs.InvokeAsync<string?>(
                "localStorage.getItem",
                Arg.Is<object?[]>(args => args.Length == 1 && Equals(args[0], StorageKeys.RefreshToken)))
            .Returns(ValueTask.FromResult<string?>(null));
        var authProvider = new AuthStateProvider(
            authJs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthStateProvider>.Instance);
        if (authenticated)
            authProvider.LoginAsync(testToken!).GetAwaiter().GetResult();
        ctx.Services.AddSingleton(authProvider);

        var locMock = Substitute.For<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>();
        locMock[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        locMock[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], string.Format((string)ci[0], (object[])ci[1])));
        ctx.Services.AddSingleton(locMock);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = "http://127.0.0.1:1"
            })
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(config);

        ctx.Services.AddSingleton<HubConnectionFactory>(sp =>
            new NullHubConnectionFactory(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<AuthStateProvider>()));
        ctx.Services.AddScoped<BreadcrumbService>();
        ctx.Services.AddScoped<IOmniBreadcrumbResolver, AetheusBreadcrumbResolver>();
        // R1-66: the user preferences live in memory; a test reads back what a component wrote.
        var preferences = new InMemoryUserPreferences();
        ctx.Services.AddSingleton(preferences);
        ctx.Services.AddSingleton<IUserPreferences>(preferences);
        ctx.Services.AddSingleton<PreferenceScope>();
        ctx.Services.AddScoped<LegacyPreferenceMigration>();
        ctx.Services.AddScoped<UserPreferenceSession>();
        ctx.Services.AddScoped<LegacyAppearanceMigration>();
        ctx.Services.AddScoped<ProjectNavContextService>();
        ctx.Services.AddScoped(sp => new ClientErrorReporter(
            http,
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()));
        ctx.Services.AddScoped(sp => new ProjectDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectDetailLoader>.Instance,
            sp.GetRequiredService<HubConnectionFactory>()));
        // The side menu reads the open server (R1-28: the mail entry shows only when the stack is installed).
        ctx.Services.AddScoped(sp => new Aetheus.Front.Components.Shared.ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aetheus.Front.Components.Shared.ServerDetailLoader>.Instance));
        ctx.Services.AddSingleton(new ActiveOrganizationService(apiClient, preferences));
        ctx.Services.AddSingleton(new AdministrationScopeService(apiClient, authProvider));
        ctx.Services.AddScoped<NotifyHelper>();
        ctx.Services.AddScoped<GridExportContext>();
        ctx.Services.AddScoped<ClipboardService>();
        ctx.Services.AddScoped<UiActions>();
        ctx.Services.AddScoped<BusyVeil>();
        ctx.Services.AddScoped<PipelineRunGate>();
        ctx.Services.AddScoped<PipelineRunDialogCoordinator>();
        ctx.Services.AddScoped<PipelineImportState>();
        ctx.Services.AddScoped<Aetheus.Front.Components.Pipelines.PipelineSetupProvisionState>();
        ctx.Services.AddSingleton(new HelpService(http));

        var httpFactoryMock = Substitute.For<IHttpClientFactory>();
        httpFactoryMock.CreateClient(Arg.Any<string>()).Returns(new HttpClient { BaseAddress = new Uri("http://test/") });
        ctx.Services.AddSingleton(httpFactoryMock);
        // The deployed front version is read through the test handler (a test that sets
        // "appsettings.json" there decides it); the handler is shared, so the client must not
        // dispose it.
        ctx.Services.AddSingleton(new Aetheus.Front.Components.Shared.ApplicationVersionState(
            () => new HttpClient(handler, disposeHandler: false), new Uri("http://test/"), "test-version"));

        var permissionService = new PermissionService();
        if (authenticated)
        {
            var readWritePermissions = Enum.GetValues<ResourceType>()
                .SelectMany(rt => new[]
                {
                    new EffectivePermissionDto { ResourceType = rt, ResourceId = null, Permission = Permission.Read },
                    new EffectivePermissionDto { ResourceType = rt, ResourceId = null, Permission = Permission.Write }
                })
                .ToList();
            // R-11: Read and Write everywhere is a Contributor, so the user also holds the named permissions a
            // Contributor holds by default, as GET api/users/me/permissions would report them.
            var contributorKeys = PermissionCatalog.DefaultKeysOf(RoleNames.Contributor)
                .Where(key => !PermissionCatalog.Get(key).IsLevel)
                .Concat(extraPermissionKeys ?? [])
                .Select(key => new EffectivePermissionKeyDto(key, null, null))
                .ToList();
            permissionService.SetPermissions(readWritePermissions, isAdmin, contributorKeys);
        }
        ctx.Services.AddSingleton(permissionService);
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<ListCacheService>();
        ctx.Services.AddSingleton<PageStateCache>();
        ctx.Services.AddSingleton(sp => new TaskTrackerService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskTrackerService>.Instance));
        ctx.Services.AddSingleton(sp => new PendingApprovalsService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingApprovalsService>.Instance));
        ctx.Services.AddSingleton(sp => new Aetheus.Front.Components.Shared.UserNotificationsFeed(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aetheus.Front.Components.Shared.UserNotificationsFeed>.Instance));
        ctx.Services.AddSingleton(sp => new AlertNotificationService(
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>()));
        ctx.Services.AddScoped<ConfirmHelper>();
        ctx.Services.AddScoped(sp => new UserNotificationService(
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<PermissionService>(),
            TimeProvider.System));
        ctx.Services.AddScoped<RealtimeSessionLifecycle>();

        return handler;
    }

    private static string CreateTestToken(bool isAdmin)
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        object claims = isAdmin
            ? new { sub = "admin", role = "Admin", unique_name = "admin", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }
            : new { sub = "user", unique_name = "user", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.sig";
    }

    public static void UseImmediateDialogs(BunitContext ctx) =>
        ctx.Services.AddScoped<OmniDialogService>(services => new ImmediateDialogService(
            services.GetRequiredService<AppDialogs>(),
            services.GetRequiredService<OmniOverlayService>()));

    public static StringContent Json<T>(T obj) =>
        new(JsonSerializer.Serialize(obj, JsonOpts), System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// Lightweight <see cref="IStringLocalizer{T}"/> that returns the key as the value,
    /// usable outside DI (e.g. with <c>RuntimeHelpers.GetUninitializedObject</c>).
    /// </summary>
    internal sealed class StubLocalizer : IStringLocalizer<Aetheus.Front.Resources.AppStrings>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    internal sealed class NullHubConnectionFactory(IConfiguration config, AuthStateProvider auth)
        : HubConnectionFactory(config, auth, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthDelegatingHandler>.Instance)
    {
        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null)
        {
            return new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:1/hubs/{hubPath}", opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => new ImmediateFailHandler();
                })
                .Build();
        }

        private sealed class ImmediateFailHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromException<HttpResponseMessage>(new HttpRequestException("Test: hub not available"));
        }
    }

    internal sealed class BlockingHubConnectionFactory(IConfiguration config, AuthStateProvider auth)
        : HubConnectionFactory(config, auth, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthDelegatingHandler>.Instance)
    {
        private int _created;

        /// <summary>How many connections were asked for: each one is a live subscription the caller must dispose.</summary>
        public int Created => Volatile.Read(ref _created);

        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null)
        {
            Interlocked.Increment(ref _created);
            return new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:1/hubs/{hubPath}", options =>
                {
                    options.HttpMessageHandlerFactory = _ => new BlockingHandler();
                })
                .Build();
        }

        private sealed class BlockingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken ct)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable");
            }
        }
    }

    /// <summary>
    /// Mocks the three list endpoints probed by <see cref="Aetheus.Front.Components.Dashboards.InstanceWelcomeWizard"/>,
    /// which the dashboard renders whenever the instance still has at most one project. Without them a
    /// dashboard test fails on an unmocked request instead of on what it actually asserts.
    /// </summary>
    public static void SetOnboardingWizardResponses(TestHandler handler)
    {
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/git/repos", Array.Empty<GitLightRepoDto>());
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/environments", Array.Empty<EnvironmentDto>());
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/variable-libraries", Array.Empty<VariableLibraryDto>());
    }

    internal sealed class TestHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);
        // Method-aware responses take precedence over URL-only ones, so a POST and a GET to the same
        // URL can return different payloads (e.g. a create POST returning the created DTO vs the
        // list-reload GET returning a collection). Longest URL-substring wins, as for _responses.
        private readonly List<(string Method, string UrlContains, Func<HttpResponseMessage> Make)> _methodResponses = [];
        private readonly List<(string Method, string UrlContains, Func<CancellationToken, Task<HttpResponseMessage>> Make)> _asyncMethodResponses = [];

        /// <summary>Every request seen, in order - lets a test assert a POST/PUT/DELETE was actually sent.</summary>
        public List<(string Method, string Url)> Requests { get; } = [];

        /// <summary>A copy of <see cref="Requests"/> taken under the lock the handler adds under.</summary>
        public List<(string Method, string Url)> RequestsSnapshot()
        {
            lock (Requests) return [.. Requests];
        }
        public List<(string Method, string Url, string? Body)> RequestDetails { get; } = [];
        public string? LastRequestBody { get; private set; }

        private readonly StateSignal _requested = new();

        /// <summary>Completes once at least <paramref name="count"/> recorded requests match
        /// <paramref name="match"/>: re-checked on each request, never against the clock.</summary>
        public Task WhenRequestedAsync(Func<(string Method, string Url), bool> match, int count, string what) =>
            _requested.WhenAsync(() =>
            {
                lock (Requests) return Requests.Count(match) >= count;
            }, what);

        /// <summary>Completes once at least <paramref name="count"/> GET requests whose URL contains
        /// <paramref name="urlContains"/> were sent.</summary>
        public Task WhenGetAsync(string urlContains, int count = 1) =>
            WhenRequestedAsync(
                request => request.Method == "GET" && request.Url.Contains(urlContains, StringComparison.Ordinal),
                count,
                $"{count} GET {urlContains}");

        public void SetResponse(string urlContains, HttpStatusCode status) =>
            _responses[urlContains] = () => new HttpResponseMessage(status) { Content = new StringContent("{}") };

        public void SetJsonResponse<T>(string urlContains, T data)
        {
            var json = JsonSerializer.Serialize(data, JsonOpts);
            _responses[urlContains] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        /// <summary>The organizations the role picker and the role page offer (ADR-059): one by default,
        /// the seeded default organization, so the picker hides its organization choice.</summary>
        public void SetOrganizations(params OrganizationDto[] organizations) =>
            SetPaginatedJsonResponse("api/organizations?page=1&pageSize=100",
                organizations.Length > 0
                    ? organizations
                    : [new OrganizationDto(4, "Aetheus", "aetheus", string.Empty, 1, 0, DateTime.UnixEpoch, DateTime.UnixEpoch)]);

        public void SetPaginatedJsonResponse<T>(string urlContains, IEnumerable<T> data)
        {
            var items = data.ToList();
            SetJsonResponse(urlContains, new PaginatedResult<T>
            {
                Items = items,
                TotalCount = items.Count,
                Page = 1,
                PageSize = Math.Max(items.Count, 1)
            });
        }

        /// <summary>
        /// Returns a raw <c>200</c> body verbatim (no serialization) so a test can feed a MALFORMED
        /// JSON payload and prove the caller survives a <see cref="JsonException"/> instead of crashing.
        /// </summary>
        public void SetRawResponse(string urlContains, string body) =>
            _responses[urlContains] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

        public void SetRawResponse(HttpMethod method, string urlContains, string body, string contentType)
            => SetRawResponse(method, urlContains, body, contentType, HttpStatusCode.OK);

        /// <summary>A body returned verbatim with the given status, so a test can feed the exact bytes a
        /// backend error path writes (the middleware's error object, a ProblemDetails, plain text).</summary>
        public void SetRawResponse(HttpMethod method, string urlContains, string body, string contentType, HttpStatusCode status)
        {
            _asyncMethodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.Add((method.Method, urlContains, () => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
            }));
        }

        public void SetJsonResponse<T>(HttpMethod method, string urlContains, T data)
            => SetJsonResponse(method, urlContains, data, HttpStatusCode.OK);

        private int _nextLoggedTaskId = 70000;

        /// <summary>
        /// Recette R3-30/R3-34: the backend's <c>TaskCompleted</c> notification never carries the output,
        /// the sections read it from the task's log. A test that writes the output it expects into the
        /// notification gets it served the real way: by <c>GET api/logs/task/{id}</c>, under a task id of
        /// its own (five digits, so no registered id is the prefix of another), with the output removed
        /// from the notification and a finished task marked successful unless the test says otherwise.
        /// </summary>
        public TaskCompletedNotification Logged(TaskCompletedNotification notification)
        {
            ArgumentNullException.ThrowIfNull(notification);
            var taskId = notification.TaskId != 0 ? notification.TaskId : ++_nextLoggedTaskId;
            List<TaskLogDto> lines = notification.Output is null
                ? []
                : [new TaskLogDto { Id = 1, TaskId = taskId, Message = notification.Output }];
            SetJsonResponse(HttpMethod.Get, $"api/logs/task/{taskId}", lines);
            return notification with
            {
                TaskId = taskId,
                Output = null,
                Status = notification.Status == TaskExecutionStatus.Pending ? TaskExecutionStatus.Success : notification.Status
            };
        }

        public void SetJsonResponse<T>(HttpMethod method, string urlContains, T data, HttpStatusCode status)
        {
            var json = JsonSerializer.Serialize(data, JsonOpts);
            _asyncMethodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.Add((method.Method, urlContains, () => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }));
        }

        public void SetPaginatedJsonResponse<T>(HttpMethod method, string urlContains, IEnumerable<T> data)
        {
            var items = data.ToList();
            SetJsonResponse(method, urlContains, new PaginatedResult<T>
            {
                Items = items,
                TotalCount = items.Count,
                Page = 1,
                PageSize = Math.Max(items.Count, 1)
            });
        }

        public void SetResponse(HttpMethod method, string urlContains, HttpStatusCode status)
        {
            _asyncMethodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _methodResponses.Add((method.Method, urlContains,
                () => new HttpResponseMessage(status) { Content = new StringContent("{}") }));
        }

        public void SetAsyncJsonResponse<T>(
            HttpMethod method,
            string urlContains,
            Func<CancellationToken, Task<T>> responseFactory)
        {
            _methodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _asyncMethodResponses.RemoveAll(response =>
                string.Equals(response.Method, method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(response.UrlContains, urlContains, StringComparison.OrdinalIgnoreCase));
            _asyncMethodResponses.Add((method.Method, urlContains, async ct =>
            {
                var data = await responseFactory(ct);
                var json = JsonSerializer.Serialize(data, JsonOpts);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            ));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            // The body is read before the request is recorded, and both lists take it in one step, details
            // first: a test that waits on Requests and then reads RequestDetails found the request in the one
            // and not yet in the other while the body read was pending. Requests is also read by tests while
            // the component's background loads still send: a reader snapshots it under the same lock (see
            // RequestsSnapshot).
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
            {
                LastRequestBody = body;
                RequestDetails.Add((request.Method.Method, url, body));
                Requests.Add((request.Method.Method, url));
            }
            _requested.Pulse();
            foreach (var r in _asyncMethodResponses
                         .Where(r => string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase)
                                     && url.Contains(r.UrlContains, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(r => r.UrlContains.Length))
                return await r.Make(ct);
            // Method-aware responses win first (so POST vs GET on the same URL can differ).
            foreach (var r in _methodResponses
                         .Where(r => string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase)
                                     && url.Contains(r.UrlContains, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(r => r.UrlContains.Length))
                return r.Make();
            // Match longest pattern first to avoid partial matches (e.g. "api/pipelines/5" vs "api/pipelines/5/runs")
            foreach (var kvp in _responses.OrderByDescending(k => k.Key.Length))
            {
                if (url.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value();
            }
            throw new InvalidOperationException(
                $"TestHandler: no mocked response for '{request.Method} {url}'. "
                + "Add a method-aware SetJsonResponse/SetResponse for the exact operation.");
        }
    }
}
