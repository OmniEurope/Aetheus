// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

public partial class PipelineWebhookService(
    IPipelineAutoTriggerRepository pipelineRepo,
    IPipelineRunService runService,
    ISettingsService settingsService,
    ILogger<PipelineWebhookService> logger,
    IPipelineGitService? pipelineGitService = null,
    IDbTransactionScope? transaction = null) : IPipelineWebhookService
{
    // WEBHOOK_REF / WEBHOOK_REPO become $(VAR)-substitutable text in step shell scripts
    // (PipelineCommandBuilder.Substitute splices values verbatim), so anyone holding the
    // shared WebhookSecret could inject shell through a hostile "ref". Constrain both to
    // their legitimate shapes at the trust boundary: git refs and https repo URLs never
    // need shell metacharacters.
    // \A...\z (not ^...$): in .NET `$` also matches just BEFORE a trailing newline, so `^...$`
    // would accept a ref/URL ending in "\n" and let that newline (a shell metacharacter) through
    // into PipelineCommandBuilder.Substitute. \z anchors the true end of string.
    // First char excludes '-': a leading dash would be an argument-injection token if an author's
    // step does `git checkout $(WEBHOOK_REF)` (e.g. `-oProxyCommand`); a real git ref / URL never
    // starts with '-', matching the certbot-email validation discipline.
    [GeneratedRegex(@"\A[A-Za-z0-9._/@+][A-Za-z0-9._/@+-]{0,299}\z")]
    private static partial Regex SafeGitRefRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9.:/_@~+][A-Za-z0-9.:/_@~+-]{0,499}\z")]
    private static partial Regex SafeRepoUrlRegex();

    public async Task<bool> HandleWebhookAsync(string rawBody, string? signature, CancellationToken ct = default)
    {
        var secret = await settingsService.GetSettingValueAsync("WebhookSecret", ct).ConfigureAwait(false);
        var payload = ValidateWebhook(secret, signature, rawBody);
        if (payload is null) return false;
        var webhookPipelines = await pipelineRepo.GetWebhookTriggeredPipelinesWithProjectAsync(ct).ConfigureAwait(false);
        var triggered = false;
        foreach (var pipeline in webhookPipelines)
            triggered |= await TryTriggerPipelineAsync(pipeline, payload, ct).ConfigureAwait(false);

        return triggered || !string.IsNullOrEmpty(secret);
    }

    private WebhookInfo? ValidateWebhook(string? secret, string? signature, string rawBody)
    {
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogWarning("Webhook rejected: WebhookSecret is not configured.");
            return null;
        }
        if (!WebhookSignatureValidator.Validate(signature, secret, rawBody))
        {
            logger.LogWarning("Invalid webhook signature");
            return null;
        }
        var payload = ParsePayload(rawBody);
        if (payload is null)
        {
            logger.LogWarning("Could not parse webhook payload");
            return null;
        }
        if (SafeRepoUrlRegex().IsMatch(payload.RepositoryUrl)
            && (payload.Ref.Length == 0 || SafeGitRefRegex().IsMatch(payload.Ref))) return payload;
        logger.LogWarning("Webhook rejected: repository URL or ref contains characters outside the allowed git shapes.");
        return null;
    }

    private async Task<bool> TryTriggerPipelineAsync(
        Pipeline pipeline, WebhookInfo payload, CancellationToken ct)
    {
        if (!MatchesRepository(pipeline, payload.RepositoryUrl)) return false;
        var authoritativeYaml = await ReadAuthoritativeYamlAsync(pipeline, ct).ConfigureAwait(false);
        var definition = YamlParsingHelper.ParseAndValidate(authoritativeYaml, logger);
        if (!HasValidBranchFilter(pipeline, authoritativeYaml, definition)) return false;
        if (!PipelineBranchFilter.Matches(definition?.Branches ?? [], payload.Ref, logger))
        {
            logger.LogDebug("Webhook skipped pipeline {PipelineId}: ref '{Ref}' does not match its branch filter",
                pipeline.Id, payload.Ref);
            return false;
        }
        var variables = new Dictionary<string, string>
        {
            ["WEBHOOK_REF"] = payload.Ref,
            ["WEBHOOK_REPO"] = payload.RepositoryUrl
        };
        using var pipelineLock = await PipelineTriggerLocks.AcquireAsync(pipeline.Id, ct).ConfigureAwait(false);
        Task<bool> ReplaceAsync() => ReplaceRunAsync(pipeline, definition, variables, ct);
        if (transaction is not { IsRelational: true })
            return await ReplaceAsync().ConfigureAwait(false);
        try
        {
            return await transaction.ExecuteInTransactionAsync(ReplaceAsync, ct).ConfigureAwait(false);
        }
        catch (BadRequestException refusal)
        {
            // Mandatory catch : inside the transaction the launcher records nothing, since the
            // rollback would erase it; the refusal is recorded here, once the transaction is over.
            await runService.RecordRefusedAutomatedLaunchAsync(
                pipeline.Id, "Webhook", refusal.Message, variables, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ReadAuthoritativeYamlAsync(Pipeline pipeline, CancellationToken ct)
    {
        if (pipelineGitService is null || pipeline.ProjectId is not { } projectId)
            return pipeline.YamlDefinition;
        var gitYaml = await pipelineGitService.ReadProjectPipelineYamlAsync(
            projectId, pipeline.Name, ct, pipeline.SourceBranch, pipeline.SourceRepositoryId).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(gitYaml) ? pipeline.YamlDefinition : gitYaml;
    }

    private bool HasValidBranchFilter(
        Pipeline pipeline, string yaml, PipelineYamlDefinition? definition)
    {
        if (!PipelineBranchFilter.DeclaresFilter(yaml)
            || definition is not null && (definition.Branches ?? []).Any(branch => !string.IsNullOrWhiteSpace(branch)))
            return true;
        logger.LogWarning(
            "Webhook skipped pipeline {PipelineId}: YAML declares a branches filter but contains no recoverable branch patterns; failing closed instead of matching every branch",
            pipeline.Id);
        return false;
    }

    private async Task<bool> ReplaceRunAsync(
        Pipeline pipeline,
        PipelineYamlDefinition? definition,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        if (transaction is { IsRelational: true }
            && !await pipelineRepo.LockPipelineForWebhookAsync(pipeline.Id, ct).ConfigureAwait(false))
            return false;
        var hasActiveRun = await pipelineRepo.HasActiveRunAsync(pipeline.Id, ct).ConfigureAwait(false);
        if (hasActiveRun && definition?.SupersedeRunning != true) return false;
        if (!hasActiveRun)
            return await runService.TriggerAutomatedRunAsync(
                pipeline.Id, "Webhook", variables, ct).ConfigureAwait(false) is not null;
        var preparation = await runService.PrepareAutomatedRunAsync(
            pipeline.Id, "Webhook", variables, ct).ConfigureAwait(false);
        if (preparation is null) return false;
        var activeRunIds = await pipelineRepo.GetActiveRunIdsAsync(pipeline.Id, ct).ConfigureAwait(false);
        foreach (var activeRunId in activeRunIds)
            await runService.CancelRunAsync(
                activeRunId, PipelineRunCancellation.BySystem("Superseded by a newer webhook delivery (latest wins)"), ct).ConfigureAwait(false);
        logger.LogInformation(
            "Webhook latest-wins requested cancellation of {Count} superseded run(s) for pipeline {PipelineId}",
            activeRunIds.Count, pipeline.Id);
        logger.LogInformation("Webhook triggering pipeline {PipelineId} ({PipelineName})", pipeline.Id, pipeline.Name);
        var replacement = await runService.TriggerPreparedAutomatedRunAsync(
            preparation, "Webhook", variables, ct).ConfigureAwait(false);
        return replacement is not null
            ? true
            : throw new ConflictException($"Webhook replacement for pipeline {pipeline.Id} could not be persisted.");
    }

    // External-Git webhooks: a mirror-backed project's RepositoryUrl is the internal mirror URL, so
    // the incoming external repo URL is matched against the remote reconstructed from its GitConnection.
    private static bool MatchesRepository(Pipeline pipeline, string incomingRepoUrl)
    {
        var project = pipeline.Project;
        if (project is null) return false;

        var incoming = RepositoryUrlNormalizer.Normalize(incomingRepoUrl);

        if (project.RepositoryUrl is not null
            && RepositoryUrlNormalizer.Normalize(project.RepositoryUrl).Equals(incoming, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var connection = project.GitConnection;
        // A self-hosted/AetheusGit connection without a BaseUrl has no resolvable host, so
        // ExternalRepoUrlBuilder.Build would throw - skip it (it can't match the webhook anyway)
        // rather than let an exception abort the whole webhook dispatch.
        var hasResolvableHost = connection is not null
            && (connection.ProviderType is GitProviderType.GitHub or GitProviderType.GitLab
                || !string.IsNullOrWhiteSpace(connection.BaseUrl));
        if (connection is not null && hasResolvableHost)
        {
            // HttpsToken yields the https remote form; webhook payloads always carry the https
            // html_url / web_url, so normalized equality matches regardless of the connection's auth.
            var externalUrl = ExternalRepoUrlBuilder.Build(
                connection.ProviderType, connection.BaseUrl, connection.OwnerOrGroup,
                connection.RepositoryName, GitAuthType.HttpsToken);
            if (RepositoryUrlNormalizer.Normalize(externalUrl).Equals(incoming, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static WebhookInfo? ParsePayload(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            return TryReadWebhook(root, "repository", "html_url", "clone_url")
                   ?? TryReadWebhook(root, "project", "web_url", "git_http_url");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WebhookInfo? TryReadWebhook(
        JsonElement root, string ownerProperty, string primaryUrlProperty, string fallbackUrlProperty)
    {
        if (!root.TryGetProperty(ownerProperty, out var owner)) return null;
        var repositoryUrl = owner.TryGetProperty(primaryUrlProperty, out var primaryUrl)
            ? primaryUrl.GetString() ?? string.Empty
            : owner.TryGetProperty(fallbackUrlProperty, out var fallbackUrl)
                ? fallbackUrl.GetString() ?? string.Empty
                : string.Empty;
        if (string.IsNullOrEmpty(repositoryUrl)) return null;
        var reference = root.TryGetProperty("ref", out var refValue)
            ? refValue.GetString() ?? string.Empty
            : string.Empty;
        return new WebhookInfo(repositoryUrl, reference);
    }

    private sealed record WebhookInfo(string RepositoryUrl, string Ref);
}
