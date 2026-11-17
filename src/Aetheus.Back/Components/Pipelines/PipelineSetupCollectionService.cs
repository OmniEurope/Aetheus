// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-26 (user decision 2026-11-05): what the pipeline setup wizard prepares, for the monitored application of a project,
/// when the user chooses the telemetry (performance, logs, errors) and the audience (visitors), so that nobody has to open
/// Supervision &gt; Ingestion afterwards. Every step goes through the service the settings page itself uses, so the rules and
/// the audit trail are the same; nothing is duplicated here.
/// <list type="bullet">
/// <item>the application is enabled (<see cref="IAppMonitoringService.EnsureCollectionAppAsync"/>);</item>
/// <item>its ingestion key exists (<see cref="IAppTelemetryService.GenerateIngestKeyAsync"/>) when it holds none. The deployment
/// derives and injects its own key at every run (<c>AppDeployEnvProvider</c>), so this key is never shown or kept: it is not
/// rotated by a second preparation and it is not what the deployed application uses;</item>
/// <item>the audience is configured (<see cref="IAppWebAnalyticsConfigurationService.ConfigureAsync"/>): enabled, a site id,
/// the HTTPS origin of the production address, the public browser ingestion, and the pseudonymisation key it provisions.</item>
/// </list>
/// Idempotent: an existing key, site id, origin or pseudonymisation key is kept as it is; only what is missing is added. The
/// outcome of each item is told as it is, and a failure is its own reason, never a claim that something was created.
/// </summary>
public interface IPipelineSetupCollectionService
{
    /// <summary>Prepares the collection the request asks for. Null when the project does not exist. The caller has Write on the project.</summary>
    Task<PipelineSetupCollectionResultDto?> PrepareAsync(PipelineSetupCollectionRequest request, CancellationToken ct = default);
}

public sealed class PipelineSetupCollectionService(
    IAppMonitoringService monitoring,
    IAppMonitoringRepository apps,
    IAppTelemetryService telemetry,
    IAppWebAnalyticsConfigurationService analytics,
    IVariableLibraryService libraries,
    ILogger<PipelineSetupCollectionService> logger) : IPipelineSetupCollectionService
{
    private const int MaximumSiteIdLength = 64;
    private const int MaximumOrigins = 10;

    public async Task<PipelineSetupCollectionResultDto?> PrepareAsync(
        PipelineSetupCollectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Telemetry && !request.Audience)
            return new PipelineSetupCollectionResultDto();

        var projectName = await apps.GetProjectNameAsync(request.ProjectId, ct).ConfigureAwait(false);
        if (projectName is null)
            return null;

        var items = new List<PipelineSetupCollectionItemDto>();
        var hadEnabledApp = await apps.GetFirstEnabledAppForUpdateAsync(request.ProjectId, ct).ConfigureAwait(false) is not null;
        EnsuredCollectionApp? app;
        try
        {
            app = await monitoring.EnsureCollectionAppAsync(request.ProjectId, request.Telemetry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRefusal(exception))
        {
            logger.LogWarning(exception, "The collection application of project {ProjectId} could not be prepared.", request.ProjectId);
            items.Add(Failed(PipelineSetupCollectionKind.AppEnabled, exception));
            return new PipelineSetupCollectionResultDto { Items = items };
        }
        if (app is null)
            return null;

        items.Add(Item(PipelineSetupCollectionKind.AppEnabled, hadEnabledApp ? PipelineSetupCollectionOutcome.AlreadyPresent : PipelineSetupCollectionOutcome.Done));
        items.Add(await PrepareIngestKeyAsync(app.AppId, ct).ConfigureAwait(false));
        if (request.Audience)
            items.AddRange(await PrepareAudienceAsync(request.ProjectId, projectName, app.AppId, ct).ConfigureAwait(false));
        return new PipelineSetupCollectionResultDto { Items = items };
    }

    private async Task<PipelineSetupCollectionItemDto> PrepareIngestKeyAsync(int appId, CancellationToken ct)
    {
        try
        {
            var app = await apps.GetAppAsync(appId, ct).ConfigureAwait(false);
            if (app is null)
                return Failed(PipelineSetupCollectionKind.IngestKey, "The application no longer exists.");
            if (app.IngestKeyHash is not null)
                return Item(PipelineSetupCollectionKind.IngestKey, PipelineSetupCollectionOutcome.AlreadyPresent);

            // The key is hashed at once and its text dropped: the deployment injects the key it derives for each run.
            var generated = await telemetry.GenerateIngestKeyAsync(appId, ct).ConfigureAwait(false);
            return generated is null
                ? Failed(PipelineSetupCollectionKind.IngestKey, "The application no longer exists.")
                : Item(PipelineSetupCollectionKind.IngestKey, PipelineSetupCollectionOutcome.Done);
        }
        catch (Exception exception) when (IsRefusal(exception))
        {
            logger.LogWarning(exception, "The ingestion key of application {AppId} could not be prepared.", appId);
            return Failed(PipelineSetupCollectionKind.IngestKey, exception);
        }
    }

    private async Task<List<PipelineSetupCollectionItemDto>> PrepareAudienceAsync(
        int projectId, string projectName, int appId, CancellationToken ct)
    {
        try
        {
            var slug = ProjectResourceNames.Slug(projectName, projectId);
            var origin = await PipelineSetupProductionOrigin.ReadAsync(libraries, slug, projectId, ct).ConfigureAwait(false);
            var before = await analytics.GetAsync(appId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The application no longer exists.");

            var origins = before.AllowedOrigins.ToList();
            if (origin is not null && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                origins.Add(origin);
            if (origins.Count == 0)
                return AudienceFailed("No production address is known yet: fill the domain of the production setup, then prepare again.");
            if (origins.Count > MaximumOrigins)
                return AudienceFailed($"The audience already allows {MaximumOrigins} origins, the production address cannot be added.");

            var siteId = string.IsNullOrWhiteSpace(before.SiteId) ? await ChooseSiteIdAsync(slug, appId, ct).ConfigureAwait(false) : before.SiteId;
            var after = await analytics.ConfigureAsync(appId, new ConfigureAppWebAnalyticsRequest
            {
                Enabled = true,
                PublicIngestEnabled = true,
                SiteId = siteId,
                AllowedOrigins = origins,
                StorageBudgetBytes = before.StorageBudgetBytes
            }, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The application no longer exists.");

            return DescribeAudience(before, after, origin, siteId);
        }
        catch (Exception exception) when (IsRefusal(exception))
        {
            logger.LogWarning(exception, "The audience of application {AppId} could not be prepared.", appId);
            return AudienceFailed(Reason(exception));
        }
    }

    /// <summary>Every item read from the configuration the service returned, never from what was asked: an item is
    /// <c>Done</c> only when the saved configuration holds it and it was not there before.</summary>
    private static List<PipelineSetupCollectionItemDto> DescribeAudience(
        AppWebAnalyticsConfigurationDto before, AppWebAnalyticsConfigurationDto after, string? origin, string siteId)
    {
        var originHeld = origin is null || after.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        return
        [
            Held(PipelineSetupCollectionKind.AudienceEnabled, before.Enabled, after.Enabled),
            Held(PipelineSetupCollectionKind.SiteId, !string.IsNullOrWhiteSpace(before.SiteId), string.Equals(after.SiteId, siteId, StringComparison.Ordinal), after.SiteId),
            Held(PipelineSetupCollectionKind.Origins, origin is null || before.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase), originHeld, origin ?? string.Join(", ", after.AllowedOrigins)),
            Held(PipelineSetupCollectionKind.PublicIngest, before.PublicIngestEnabled, after.PublicIngestEnabled),
            Held(PipelineSetupCollectionKind.PseudonymKey, before.PseudonymKeyVersion > 0, after.PseudonymKeyVersion > 0)
        ];

        static PipelineSetupCollectionItemDto Held(PipelineSetupCollectionKind kind, bool wasThere, bool isThere, string? detail = null) =>
            !isThere
                ? Failed(kind, "The saved configuration does not hold it.")
                : new PipelineSetupCollectionItemDto
                {
                    Kind = kind,
                    Outcome = wasThere ? PipelineSetupCollectionOutcome.AlreadyPresent : PipelineSetupCollectionOutcome.Done,
                    Detail = detail
                };
    }

    /// <summary>The project's slug, or that slug followed by the application id when another application already holds it.</summary>
    private async Task<string> ChooseSiteIdAsync(string slug, int appId, CancellationToken ct)
    {
        var candidate = Truncate(slug, MaximumSiteIdLength);
        var holder = await apps.GetAppByAnalyticsSiteIdAsync(candidate, ct).ConfigureAwait(false);
        return holder is null || holder.Id == appId ? candidate : Truncate(slug, MaximumSiteIdLength - 1 - appId.ToString(System.Globalization.CultureInfo.InvariantCulture).Length) + "-" + appId;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length].Trim('-');

    /// <summary>A refusal the preparation reports item by item instead of throwing: the services' own validation, a missing
    /// resource, a vault or database failure of the step. Cancellation is never swallowed.</summary>
    private static bool IsRefusal(Exception exception) =>
        exception is InvalidOperationException or BadRequestException or NotFoundException or UnauthorizedAccessException
            or Microsoft.EntityFrameworkCore.DbUpdateException;

    /// <summary>The reason shown for a failure: the services' own validation messages as they are written; anything else (a
    /// database or vault failure) says only that it failed, the details staying in the server log.</summary>
    private static string Reason(Exception exception) =>
        exception is InvalidOperationException or BadRequestException or NotFoundException
            ? exception.Message
            : "The operation failed; see the server log.";

    private static List<PipelineSetupCollectionItemDto> AudienceFailed(string reason) =>
    [
        .. new[]
        {
            PipelineSetupCollectionKind.AudienceEnabled, PipelineSetupCollectionKind.SiteId, PipelineSetupCollectionKind.Origins,
            PipelineSetupCollectionKind.PublicIngest, PipelineSetupCollectionKind.PseudonymKey
        }.Select(kind => Failed(kind, reason))
    ];

    private static PipelineSetupCollectionItemDto Item(PipelineSetupCollectionKind kind, PipelineSetupCollectionOutcome outcome) =>
        new() { Kind = kind, Outcome = outcome };

    private static PipelineSetupCollectionItemDto Failed(PipelineSetupCollectionKind kind, string reason) =>
        new() { Kind = kind, Outcome = PipelineSetupCollectionOutcome.Failed, Detail = reason };

    private static PipelineSetupCollectionItemDto Failed(PipelineSetupCollectionKind kind, Exception exception) =>
        Failed(kind, Reason(exception));
}
