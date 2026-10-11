// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Git;

public class GitSmartHttpService(
    IGitLightRepository lightRepo,
    IAuthService authService,
    IWebhookService webhookService,
    // No IPipelineService, no IPipelineRunService: what a push means for pipelines is decided by the
    // module that owns them, through GitPushProcessedEvent.
    IDomainEventDispatcher domainEvents,
    IGitLightCliService cli,
    IHubContext<GitRealtimeHub> hub,
    IOptions<GitLightOptions> options,
    ILogger<GitSmartHttpService> logger,
    IMemoryCache cache,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime) : IGitSmartHttpService
{
    private static readonly HashSet<string> AllowedServices = ["git-upload-pack", "git-receive-pack"];
    private const string ReachableShaUploadPackConfig = "uploadpack.allowReachableSHA1InWant=true";
    private readonly GitLightOptions _options = options.Value;

    public async Task<(string ContentType, byte[] Body)?> GetInfoRefsAsync(
        int projectId, string slug, string service, CancellationToken ct = default)
    {
        var diskPath = await ResolveServiceDiskPathAsync(projectId, slug, service, ct).ConfigureAwait(false);
        if (diskPath is null) return null;

        // Hardening (High #13): use ArgumentList to avoid any string concatenation that could
        // be interpreted as additional options or quoted-path traversal.
        // Modern git refuses 'git git-upload-pack ...'; use the builtin subcommand name
        // ("upload-pack" / "receive-pack") which works on every supported git version.
        var psi = CreateServiceProcessStartInfo(service, diskPath, advertiseRefs: true);

        using var process = Process.Start(psi);
        if (process is null) return null;

        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = ms.ToArray();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            logger.LogWarning("git {Service} info/refs failed: {Error}", service, error);
            return null;
        }

        // Smart HTTP protocol: pkt-line header + advertised refs
        var contentType = $"application/x-{service}-advertisement";
        var header = $"# service={service}\n";
        var pktHeader = PktLine(header);
        var flush = "0000"u8.ToArray();

        var body = new byte[pktHeader.Length + flush.Length + output.Length];
        pktHeader.CopyTo(body, 0);
        flush.CopyTo(body, pktHeader.Length);
        output.CopyTo(body, pktHeader.Length + flush.Length);

        return (contentType, body);
    }

    public async Task<GitSmartHttpResponse?> ExecuteServiceAsync(
        int projectId, string slug, string service, Stream requestBody, CancellationToken ct = default)
    {
        var diskPath = await ResolveServiceDiskPathAsync(projectId, slug, service, ct).ConfigureAwait(false);
        if (diskPath is null) return null;

        var psi = CreateServiceProcessStartInfo(service, diskPath, advertiseRefs: false);

        var isUploadPack = string.Equals(service, "git-upload-pack", StringComparison.Ordinal);

        // BACK-013: the commands of a push are read and checked against the pull-request rules before
        // git starts, so a refused push never reaches the repository.
        var receivePrelude = isUploadPack
            ? []
            : await GitReceivePackRefParser.ReadCommandPreludeAsync(requestBody, ct).ConfigureAwait(false);
        if (!isUploadPack)
            await EnsureNoDirectPushToPullRequestBranchAsync(projectId, slug, receivePrelude, ct).ConfigureAwait(false);

        var process = Process.Start(psi);
        if (process is null) return null;

        // F-30 + Hardening (High #14): hard upper bound + kill on timeout/cancellation.
        // NOT a `using` - for the streamed upload-pack path ownership of the CTS transfers to the
        // returned GitProcessOutputStream (disposed when the response finishes). The buffered and
        // error/cancel paths dispose it explicitly below.
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(BackendRuntimeDefaults.GitLongRunningTimeout);
        var token = timeoutCts.Token;

        try
        {
            IReadOnlyList<GitRefUpdate> requestedUpdates = [];
            if (!isUploadPack)
            {
                await using var prelude = new MemoryStream(receivePrelude, writable: false);
                requestedUpdates = await GitReceivePackRefParser.CopyAndExtractUpdatedRefsAsync(
                    prelude, process.StandardInput.BaseStream, token).ConfigureAwait(false);
            }
            await requestBody.CopyToAsync(process.StandardInput.BaseStream, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
            process.StandardInput.Close();

            // S-TECH-GP9S: upload-pack is read-only (clone/fetch) with no post-step, so stream its
            // output straight to the HTTP response instead of buffering the whole packfile in memory.
            // The returned stream owns the process + CTS. See ADR-020 for the changed error semantics:
            // a git failure now surfaces as a truncated pkt-line stream (the client detects it), not a
            // 404, because the 200 is already committed once bytes start flowing.
            if (isUploadPack)
            {
                var uploadContentType = $"application/x-{service}-result";
                return new GitSmartHttpResponse(uploadContentType,
                    new GitProcessOutputStream(process, timeoutCts, logger, service), []);
            }

            // PERF NOTE (assessed 2026-06-21): receive-pack output stays buffered into a MemoryStream -
            // STRUCTURALLY REQUIRED, not an oversight:
            //   1. receive-pack ordering - the controller calls MarkPushedAsync() AFTER this method
            //      returns. Streaming would return before git finished applying the push, so the post
            //      step (HEAD fixup, webhooks, pipeline triggers) would run against a stale repo.
            //   2. error semantics - a non-zero git exit maps to NotFound (404). That decision needs
            //      the exit code, only known once the process has fully run; a streamed 200 cannot be
            //      retracted once bytes start flowing.
            var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
                logger.LogWarning("git {Service} failed with exit code {ExitCode}: {Error}", service, process.ExitCode, error);
                process.Dispose();
                timeoutCts.Dispose();
                return null;
            }

            var acceptedUpdates = GitReceivePackStatusParser.GetAcceptedUpdates(ms, requestedUpdates);
            ms.Position = 0;
            var contentType = $"application/x-{service}-result";
            process.Dispose();
            timeoutCts.Dispose();
            return new GitSmartHttpResponse(contentType, ms, acceptedUpdates);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            process.Dispose();
            timeoutCts.Dispose();
            throw;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            process.Dispose();
            timeoutCts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves the on-disk repo path for the given project/slug and verifies it stays under
    /// the configured <see cref="GitLightOptions.RepositoriesPath"/> root. Returns <c>null</c>
    /// when the resolved path escapes the root (defense against path-traversal slugs).
    /// </summary>
    // Decision D13: a branch whose rule is ProtectedExceptPipelines or StrictlyProtected only moves
    // through the platform's own paths, the merge of a pull request and (below strict) the
    // advance-branch pipeline step, which write the ref in the repository directly and never come
    // through this transport. A push that would create or move such a branch is refused; a deletion
    // stays governed by PreventDeletion, enforced by the pre-receive hook.
    private async Task EnsureNoDirectPushToPullRequestBranchAsync(
        int projectId, string slug, byte[] prelude, CancellationToken ct)
    {
        var repository = await lightRepo.FindBySlugAsync(projectId, slug, ct).ConfigureAwait(false);
        if (repository is null) return;
        var rules = await lightRepo.GetBranchProtectionRulesAsync(repository.Id, ct).ConfigureAwait(false);
        if (rules.Count == 0) return;

        foreach (var command in GitReceivePackRefParser.ParseCommands(prelude))
        {
            if (GitReceivePackRefParser.IsDeletion(command)
                || !command.Reference.StartsWith("refs/heads/", StringComparison.Ordinal))
                continue;
            var branch = command.Reference["refs/heads/".Length..];
            var refusal = GitBranchWritePolicy.RefusalReason(branch, rules, GitBranchWriter.HumanPush);
            if (refusal is not null)
                throw new BadHttpRequestException(refusal, StatusCodes.Status403Forbidden);
        }
    }

    private async Task<string?> ResolveServiceDiskPathAsync(
        int projectId,
        string slug,
        string service,
        CancellationToken ct)
    {
        if (!AllowedServices.Contains(service)) return null;
        var repository = await lightRepo.FindBySlugAsync(projectId, slug, ct).ConfigureAwait(false);
        return repository is null ? null : ResolveSafeRepositoryPath(projectId, slug);
    }

    private string? ResolveSafeRepositoryPath(int projectId, string slug)
    {
        var candidate = GitRepoPathResolver.TryResolve(_options.RepositoriesPath, projectId, slug);
        if (candidate is null)
            logger.LogWarning("Rejected git path outside repository root");
        return candidate;
    }

    internal static ProcessStartInfo CreateServiceProcessStartInfo(
        string service, string diskPath, bool advertiseRefs)
    {
        var subcommand = service.StartsWith("git-", StringComparison.Ordinal)
            ? service["git-".Length..]
            : service;
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardInput = !advertiseRefs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (string.Equals(subcommand, "upload-pack", StringComparison.Ordinal))
        {
            // Long-running pipelines keep their source commit pinned while the advertised branch
            // can advance. Permit only pinned commits that remain reachable from an advertised ref;
            // unlike allowAnySHA1InWant, this does not expose arbitrary unreachable repository data.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(ReachableShaUploadPackConfig);
        }

        psi.ArgumentList.Add(subcommand);
        psi.ArgumentList.Add("--stateless-rpc");
        if (advertiseRefs)
            psi.ArgumentList.Add("--advertise-refs");
        psi.ArgumentList.Add(diskPath);
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        return psi;
    }

    public async Task MarkPushedAsync(int projectId, string slug, IReadOnlyList<GitRefUpdate> updatedRefs, CancellationToken ct = default)
    {
        // receive-pack has already accepted and persisted the refs before this callback starts. The
        // client is therefore free to close its HTTP request while these durable post-receive effects
        // are still running. Tying them to RequestAborted can leave a visible pipeline run persisted as
        // Running with every step Pending when cancellation lands between run creation and dispatch.
        // Keep the work inline so scoped services remain valid, but detach it from the client lifetime.
        var postReceiveCt = applicationLifetime.ApplicationStopping;

        var entity = await lightRepo.FindBySlugAsync(projectId, slug, postReceiveCt).ConfigureAwait(false);
        if (entity is null) return;

        entity.IsEmpty = false;
        entity.LastPushAt = timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await UpdateDefaultBranchAsync(entity, projectId, slug, postReceiveCt).ConfigureAwait(false);
        await lightRepo.SaveChangesAsync(postReceiveCt).ConfigureAwait(false);
        cache.Remove(ProjectCacheKeys.GitUpdate(projectId));
        await BroadcastPushAsync(entity.Id, slug, postReceiveCt).ConfigureAwait(false);
        await FirePushWebhookAsync(entity, projectId, slug, postReceiveCt).ConfigureAwait(false);
        // What this push means for pipelines - synchronising the definitions it carries, triggering
        // what it should trigger - is decided by the module that owns pipelines. Driving that from
        // here is what made the transport depend on the orchestrator.
        //
        // The path is resolved here rather than carried as a slug: the traversal check belongs to
        // this module, and a subscriber must not be able to reconstruct a repository path itself.
        //
        // A360-21: Publish, not DispatchAsync. This runs inside git-receive-pack, and the subscriber
        // synchronises definitions and launches pipelines - which now includes a full preflight, with
        // an outbound HTTP call per environment. Awaiting all of that made `git push` hang for as long
        // as the orchestrator took, for work whose result the push does not use: the objects are
        // already on disk and the refs already updated by the time we get here. The event was always
        // declared as observer semantics; it is now dispatched that way.
        domainEvents.Publish(
            new Events.GitPushProcessedEvent(
                projectId,
                entity.Id,
                slug,
                entity.Name,
                entity.DefaultBranch,
                ResolveSafeRepositoryPath(projectId, slug) ?? string.Empty,
                updatedRefs));
    }

    private async Task UpdateDefaultBranchAsync(
        Data.Entities.GitInternalRepo entity,
        int projectId,
        string slug,
        CancellationToken ct)
    {
        var diskPath = ResolveSafeRepositoryPath(projectId, slug);
        if (diskPath is null) return;
        var detected = await cli.DetectDefaultBranchAsync(diskPath, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(detected) || detected == entity.DefaultBranch) return;
        await cli.SetHeadAsync(diskPath, detected, ct).ConfigureAwait(false);
        entity.DefaultBranch = detected;
        logger.LogInformation("Updated HEAD of {Slug} to {Branch}", slug, detected);
    }

    private async Task BroadcastPushAsync(int repositoryId, string slug, CancellationToken ct)
    {
        try
        {
            var group = hub.Clients.Group(GitRealtimeGroups.Repository(repositoryId));
            await group.SendAsync(GitRealtimeEvents.RepositoryChanged, repositoryId, ct).ConfigureAwait(false);
            await group.SendAsync(GitRealtimeEvents.BranchesChanged, repositoryId, ct).ConfigureAwait(false);
            await group.SendAsync(GitRealtimeEvents.CommitsChanged, repositoryId, ct).ConfigureAwait(false);
            await group.SendAsync(GitRealtimeEvents.TagsChanged, repositoryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast git realtime push event for {Slug}", slug);
        }
    }

    private async Task FirePushWebhookAsync(
        Data.Entities.GitInternalRepo entity,
        int projectId,
        string slug,
        CancellationToken ct)
    {
        try
        {
            await webhookService.FireEventAsync("git.push", new
            {
                projectId,
                slug,
                repository = entity.Name,
                pushedAt = entity.LastPushAt
            }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fire git.push webhook for {Slug}", slug);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "git.push webhook timed out for {Slug}", slug);
        }
    }


    public Task<bool> ValidateBasicAuthAsync(string username, string password, CancellationToken ct = default)
        => authService.ValidateBasicAuthAsync(username, password, ct);

    public Task<bool> IsRunActiveAsync(int runId, CancellationToken ct = default)
        => lightRepo.IsRunActiveAsync(runId, ct);

    private static byte[] PktLine(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        var length = bytes.Length + 4;
        var header = Encoding.ASCII.GetBytes(length.ToString("x4"));
        var result = new byte[header.Length + bytes.Length];
        header.CopyTo(result, 0);
        bytes.CopyTo(result, header.Length);
        return result;
    }
}
