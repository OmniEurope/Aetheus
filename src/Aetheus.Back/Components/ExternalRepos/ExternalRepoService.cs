// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.ExternalRepos;

public sealed class ExternalRepoService(
    IProjectRepository projectRepo,
    IGitRepository gitRepo,
    IGitLightRepository lightRepo,
    IServiceConnectionRepository serviceConnectionRepo,
    IExternalRepoMirrorService mirror,
    IEncryptionService encryption,
    IAuditService audit,
    IEntityChangeNotifier notifier,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ExternalRepoService> logger) : IExternalRepoService
{
    public async Task<ExternalRepoDto?> GetForProjectAsync(int projectId, CancellationToken ct = default)
    {
        if (await FindAttachedAsync(projectId, ct).ConfigureAwait(false) is not { } attached)
        {
            return null;
        }

        var (project, connection, mirrorRepo) = attached;
        return Map(project, connection, mirrorRepo, await ResolveAuthTypeAsync(connection, ct).ConfigureAwait(false));
    }

    public async Task<ExternalRepoDto> AttachAsync(AttachExternalRepoRequest request, CancellationToken ct = default)
    {

        var project = await projectRepo.FindProjectAsync(request.ProjectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {request.ProjectId} not found.");

        // An external repository becomes the source of a project without a repository. Beside an
        // internal repository, it is an additional pipeline source and leaves the project's default
        // branch and clone URL unchanged.
        var existing = await lightRepo.GetByProjectAsync(request.ProjectId, ct).ConfigureAwait(false);
        if (project.GitConnectionId is not null || existing.Any(GitLightMapper.IsAdditionalSource))
        {
            throw new ConflictException("This project already has an external repository attached.");
        }
        var besideInternalRepository = existing.Any(repo => !repo.IsConfiguration);

        var credential = BuildCredential(request);
        ValidateCredential(credential);

        // Fail fast on an unreachable remote / bad credential before persisting anything.
        var (ok, error) = await mirror.TestConnectivityAsync(
            request.ProviderType, request.BaseUrl, request.OwnerOrGroup, request.RepositoryName,
            request.AuthType, credential, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new BadRequestException($"Could not reach the external repository: {error}");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var serviceConnection = new ServiceConnection
        {
            Name = $"git-{request.OwnerOrGroup}-{request.RepositoryName}",
            Description = "External Git credential (managed by External-Git).",
            Type = request.AuthType == GitAuthType.Ssh ? ServiceConnectionType.SSH : ServiceConnectionType.Generic,
            ProjectId = request.ProjectId,
            EncryptedPayload = encryption.EncryptValue(JsonSerializer.Serialize(credential))
        };
        await serviceConnectionRepo.AddAsync(serviceConnection, ct).ConfigureAwait(false);

        var connection = new GitConnection
        {
            ProjectId = request.ProjectId,
            ProviderType = request.ProviderType,
            OwnerOrGroup = request.OwnerOrGroup,
            RepositoryName = request.RepositoryName,
            BaseUrl = request.BaseUrl,
            ServiceConnectionId = serviceConnection.Id,
            AutoSyncEnabled = request.AutoSyncEnabled,
            FetchIntervalMinutes = Math.Clamp(request.FetchIntervalMinutes, 1, 1440),
            WriteEnabled = request.WriteEnabled,
            MirrorStatus = GitMirrorStatus.NotInitialized
        };
        await gitRepo.AddConnectionAsync(connection, ct).ConfigureAwait(false);

        var slug = Slugify(request.RepositoryName);
        if (existing.Any(repo => string.Equals(repo.Slug, slug, StringComparison.OrdinalIgnoreCase)))
        {
            // The mirror is named after the remote repository; two repositories of one project cannot
            // share a slug, and a pipeline names its source by that slug.
            slug = Slugify($"{request.OwnerOrGroup}-{request.RepositoryName}");
            if (existing.Any(repo => string.Equals(repo.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                throw new ConflictException($"This project already has a repository named '{slug}'.");
        }
        var mirrorRepo = new GitInternalRepo
        {
            ProjectId = request.ProjectId,
            Name = request.RepositoryName,
            Slug = slug,
            Description = "External repository mirror.",
            DefaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch,
            IsEmpty = true,
            GitConnectionId = connection.Id
        };
        await lightRepo.AddAsync(mirrorRepo, ct).ConfigureAwait(false);

        // Initial mirror clone. On failure the connection is left in Error state but still attached so
        // the user can fix credentials and re-sync - no silent success.
        await mirror.SyncAsync(connection, mirrorRepo, ct).ConfigureAwait(false);
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!besideInternalRepository)
        {
            // Mark the project's source as external and point pipelines/browse at the mirror's smart-HTTP
            // URL. The external credential never leaves the backend; the agent clones the mirror instead.
            project.GitConnectionId = connection.Id;
            project.RepositoryUrl = BuildMirrorCloneUrl(request.ProjectId, slug);
            project.DefaultBranch = mirrorRepo.DefaultBranch;
            project.UpdatedAt = now;
            await projectRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await audit.LogAsync("Attached", "ExternalRepo", connection.Id,
            $"{request.ProviderType}: {request.OwnerOrGroup}/{request.RepositoryName}"
            + (besideInternalRepository ? " (additional source)" : string.Empty), ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, request.ProjectId, EntityChangeOps.Updated, ct).ConfigureAwait(false);

        return Map(project, connection, mirrorRepo, request.AuthType);
    }

    public async Task<ExternalRepoDto?> SyncNowAsync(int projectId, CancellationToken ct = default)
    {
        if (await FindAttachedAsync(projectId, ct).ConfigureAwait(false) is not ({ } project, { } connection, { } mirrorRepo))
        {
            return null;
        }

        await mirror.SyncAsync(connection, mirrorRepo, ct).ConfigureAwait(false);
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, projectId, EntityChangeOps.Updated, ct).ConfigureAwait(false);

        return Map(project, connection, mirrorRepo, await ResolveAuthTypeAsync(connection, ct).ConfigureAwait(false));
    }

    public async Task<bool> DetachAsync(int projectId, CancellationToken ct = default)
    {
        if (await FindAttachedAsync(projectId, ct).ConfigureAwait(false) is not ({ } project, { } connection, var mirrorRepo))
        {
            return false;
        }

        var connectionId = connection.Id;
        if (project.GitConnectionId == connectionId)
        {
            // The project's own source: null the back-reference first (NoAction FK) so the connection
            // can be removed. An additional source never touched these fields.
            project.GitConnectionId = null;
            project.RepositoryUrl = null;
            project.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await projectRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (mirrorRepo is not null)
        {
            DeleteMirrorOnDisk(projectId, mirrorRepo.Slug);
            await lightRepo.RemoveAsync(mirrorRepo, ct).ConfigureAwait(false);
            await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var serviceConnectionId = connection.ServiceConnectionId;
        await gitRepo.RemoveConnectionAsync(connection, ct).ConfigureAwait(false);
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        if (serviceConnectionId is not null)
        {
            var sc = await serviceConnectionRepo.FindAsync(serviceConnectionId.Value, ct).ConfigureAwait(false);
            if (sc is not null)
            {
                await serviceConnectionRepo.RemoveAsync(sc, ct).ConfigureAwait(false);
                await serviceConnectionRepo.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        await audit.LogAsync("Detached", "ExternalRepo", connectionId, null, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Project, projectId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return true;
    }

    // The project's external connection and its mirror (null when the mirror row is missing), or null
    // when the project is unknown, has no external repository, or its connection row is gone. The
    // connection is the project's own source (GitConnectionId) or, beside an internal repository, the
    // one behind its additional source.
    private async Task<(Project Project, GitConnection Connection, GitInternalRepo? Mirror)?> FindAttachedAsync(
        int projectId, CancellationToken ct)
    {
        var project = await projectRepo.FindProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var connectionId = project.GitConnectionId;
        if (connectionId is null)
        {
            var repos = await lightRepo.GetByProjectAsync(projectId, ct).ConfigureAwait(false);
            connectionId = repos.FirstOrDefault(GitLightMapper.IsAdditionalSource)?.GitConnectionId;
        }
        if (connectionId is null)
        {
            return null;
        }

        var connection = await gitRepo.FindConnectionAsync(connectionId.Value, ct).ConfigureAwait(false);
        if (connection is null)
        {
            return null;
        }

        return (project, connection, await FindMirrorAsync(projectId, connection.Id, ct).ConfigureAwait(false));
    }

    private async Task<GitInternalRepo?> FindMirrorAsync(int projectId, int connectionId, CancellationToken ct)
    {
        var repos = await lightRepo.GetByProjectAsync(projectId, ct).ConfigureAwait(false);
        return repos.FirstOrDefault(r => r.GitConnectionId == connectionId);
    }

    private async Task<GitAuthType> ResolveAuthTypeAsync(GitConnection connection, CancellationToken ct)
    {
        if (connection.ServiceConnectionId is null)
        {
            return GitAuthType.HttpsToken;
        }

        var sc = await serviceConnectionRepo.FindAsync(connection.ServiceConnectionId.Value, ct).ConfigureAwait(false);
        return sc?.Type == ServiceConnectionType.SSH ? GitAuthType.Ssh : GitAuthType.HttpsToken;
    }

    private static GitCredentialPayload BuildCredential(AttachExternalRepoRequest request) => new()
    {
        AuthType = request.AuthType,
        Username = request.Username,
        Token = request.Token,
        PrivateKeyPem = request.PrivateKeyPem,
        Passphrase = request.Passphrase,
        KnownHosts = request.KnownHosts
    };

    private static void ValidateCredential(GitCredentialPayload credential)
    {
        // HTTPS without a token is an anonymous read: a public repository. The connectivity probe that
        // follows is what refuses a private one, with the remote's own answer.

        if (credential.AuthType == GitAuthType.Ssh)
        {
            if (string.IsNullOrWhiteSpace(credential.PrivateKeyPem))
            {
                throw new BadRequestException("A private key is required for SSH authentication.");
            }
            if (string.IsNullOrWhiteSpace(credential.KnownHosts))
            {
                // Non-negotiable: we never fall back to StrictHostKeyChecking=no.
                throw new BadRequestException("A pinned known_hosts entry is required for SSH authentication.");
            }
        }
    }

    private string BuildMirrorCloneUrl(int projectId, string slug) =>
        MirrorCloneUrl.Build(configuration, httpContextAccessor.HttpContext, projectId, slug);

    private void DeleteMirrorOnDisk(int projectId, string slug)
    {
        var path = mirror.ResolveMirrorPath(projectId, slug);
        if (Directory.Exists(path))
        {
            GitProcessRunner.ClearReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
            logger.LogInformation("[ExternalRepos] removed mirror on disk for project {ProjectId}", projectId);
        }
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.' or '/')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(slug) ? "repo" : slug;
    }

    private ExternalRepoDto Map(Project project, GitConnection connection, GitInternalRepo? mirror, GitAuthType authType) => new()
    {
        Slug = mirror?.Slug,
        IsProjectSource = project.GitConnectionId == connection.Id,
        GitConnectionId = connection.Id,
        ProjectId = connection.ProjectId,
        ProviderType = connection.ProviderType,
        OwnerOrGroup = connection.OwnerOrGroup,
        RepositoryName = connection.RepositoryName,
        BaseUrl = connection.BaseUrl,
        AuthType = authType,
        MirrorStatus = connection.MirrorStatus,
        LastFetchedAt = connection.LastFetchedAt,
        LastFetchError = connection.LastFetchError,
        AutoSyncEnabled = connection.AutoSyncEnabled,
        FetchIntervalMinutes = connection.FetchIntervalMinutes,
        WriteEnabled = connection.WriteEnabled,
        DefaultBranch = mirror?.DefaultBranch,
        // The project's own source shows the URL the project clones; an additional source shows its mirror's.
        CloneUrl = project.GitConnectionId == connection.Id || mirror is null
            ? project.RepositoryUrl
            : BuildMirrorCloneUrl(project.Id, mirror.Slug)
    };
}
