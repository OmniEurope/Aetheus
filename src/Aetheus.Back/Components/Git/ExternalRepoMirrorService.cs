// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Git;

public sealed class ExternalRepoMirrorService(
    CreditedGitRunner git,
    ExternalGitEndpointGuard endpointGuard,
    IServiceConnectionRepository serviceConnectionRepo,
    IEncryptionService encryption,
    IGitLightCliService cli,
    IOptions<GitLightOptions> options,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<ExternalRepoMirrorService> logger) : IExternalRepoMirrorService
{
    private readonly GitLightOptions _options = options.Value;

    public async Task<(bool Ok, string? Error)> TestConnectivityAsync(
        GitProviderType provider, string? baseUrl, string ownerOrGroup, string repositoryName,
        GitAuthType authType, GitCredentialPayload? credential, CancellationToken ct = default)
    {
        var remoteUrl = ExternalRepoUrlBuilder.Build(provider, baseUrl, ownerOrGroup, repositoryName, authType);
        var (pin, refusal) = await endpointGuard.CheckAsync(remoteUrl, ct).ConfigureAwait(false);
        if (refusal is not null)
            return (false, refusal);
        var result = await git.RunAsync(
            Path.GetTempPath(), [.. pin, "ls-remote", "--heads", remoteUrl], remoteUrl, credential, ct,
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        return result.Success
            ? (true, null)
            : (false, string.IsNullOrWhiteSpace(result.Error) ? "Remote unreachable." : result.Error.Trim());
    }

    public async Task<GitMirrorStatus> SyncAsync(GitConnection connection, GitInternalRepo mirror, CancellationToken ct = default)
    {
        var credential = await LoadCredentialAsync(connection.ServiceConnectionId, ct).ConfigureAwait(false);
        var authType = credential?.AuthType ?? GitAuthType.HttpsToken;
        var remoteUrl = ExternalRepoUrlBuilder.Build(
            connection.ProviderType, connection.BaseUrl, connection.OwnerOrGroup, connection.RepositoryName, authType);

        var mirrorPath = ResolveMirrorPath(mirror.ProjectId, mirror.Slug);
        connection.MirrorStatus = GitMirrorStatus.Syncing;

        var result = Directory.Exists(Path.Combine(mirrorPath, "objects")) || File.Exists(Path.Combine(mirrorPath, "HEAD"))
            ? await FetchAsync(mirrorPath, remoteUrl, credential, ct).ConfigureAwait(false)
            : await CloneAsync(mirrorPath, remoteUrl, credential, ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (result.Success)
        {
            connection.MirrorStatus = GitMirrorStatus.Ready;
            connection.MirrorPath = mirrorPath;
            connection.LastFetchedAt = now;
            connection.LastSyncedAt = now;
            connection.LastFetchError = null;

            // Keep the mirror's recorded default branch and "empty" flag in step with reality so the
            // browse UI and pipeline clone behave exactly like an internal repo.
            var detected = await cli.DetectDefaultBranchAsync(mirrorPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(detected))
            {
                mirror.DefaultBranch = detected;
                mirror.IsEmpty = false;
                await cli.SetHeadAsync(mirrorPath, detected, ct).ConfigureAwait(false);
            }
            mirror.LastPushAt = now;

            // G7LM: a successful mirror sync freshens the project's "last git update" - purge the
            // per-id cache so the project tile reflects the new activity date immediately.
            cache.Remove(ProjectCacheKeys.GitUpdate(mirror.ProjectId));
        }
        else
        {
            connection.MirrorStatus = GitMirrorStatus.Error;
            connection.LastFetchError = Truncate(result.Error, 2000);
            logger.LogWarning("[ExternalRepos] mirror sync failed for connection {Id}: {Error}",
                connection.Id, connection.LastFetchError);
        }

        return connection.MirrorStatus;
    }

    public string ResolveMirrorPath(int projectId, string slug)
        => GitRepoPathResolver.TryResolve(_options.RepositoriesPath, projectId, slug)
           ?? throw new BadRequestException("Invalid mirror repository path.");

    private async Task<CreditedGitResult> CloneAsync(
        string mirrorPath, string remoteUrl, GitCredentialPayload? credential, CancellationToken ct)
    {
        var (pin, refusal) = await endpointGuard.CheckAsync(remoteUrl, ct).ConfigureAwait(false);
        if (refusal is not null)
            return new CreditedGitResult(-1, string.Empty, refusal);

        var parent = Path.GetDirectoryName(mirrorPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return await git.RunAsync(
            Path.GetTempPath(), [.. pin, "clone", "--mirror", remoteUrl, mirrorPath], remoteUrl, credential, ct)
            .ConfigureAwait(false);
    }

    private async Task<CreditedGitResult> FetchAsync(string mirrorPath, string remoteUrl, GitCredentialPayload? credential, CancellationToken ct)
    {
        // Re-assert the credential-free origin URL before fetching (M-git-6-adjacent): a --mirror clone
        // stores the clean URL, but rewriting it here makes the invariant explicit and self-healing - if
        // an operator ever embedded a token in remote.origin.url, this resets it so the secret can't
        // persist in .git/config (the credential rides in env only, injected by CreditedGitRunner).
        await git.RunAsync(mirrorPath, ["remote", "set-url", "origin", remoteUrl], remoteUrl: null, credential: null, ct)
            .ConfigureAwait(false);

        var (pin, refusal) = await endpointGuard.CheckAsync(remoteUrl, ct).ConfigureAwait(false);
        if (refusal is not null)
            return new CreditedGitResult(-1, string.Empty, refusal);

        // A --mirror clone configures remote.origin to fetch +refs/*:refs/* with mirror=true, so a
        // plain `fetch --prune origin` refreshes every ref.
        return await git.RunAsync(mirrorPath, [.. pin, "fetch", "--prune", "origin"], remoteUrl: null, credential, ct)
            .ConfigureAwait(false);
    }

    private async Task<GitCredentialPayload?> LoadCredentialAsync(int? serviceConnectionId, CancellationToken ct)
    {
        if (serviceConnectionId is null)
        {
            return null;
        }

        var connection = await serviceConnectionRepo.FindAsync(serviceConnectionId.Value, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return null;
        }

        return GitCredentialPayload.TryParse(encryption.DecryptValue(connection.EncryptedPayload));
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
