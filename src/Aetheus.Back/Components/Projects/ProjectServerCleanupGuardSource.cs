// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Recette R-97 (ADR-058): the guards of a project deletion for the current request.</summary>
public interface IProjectServerCleanupGuardSource
{
    Task<ProjectServerCleanupGuards> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Admin on the project lets a user delete it, not act on every server it deployed to: resources are read and
/// removed only on the servers where the current user holds Write (the same check as assigning a server to an
/// environment). No authenticated user means no server. The control plane's public hosts are those it is
/// configured with: <c>Aetheus:PublicApiBaseUrl</c>, <c>Cors:FrontendUrl</c> and every <c>Cors:Origins</c> entry,
/// read through <see cref="IConfiguration"/> so the environment overrides the host uses apply here too.
/// </summary>
public sealed class ProjectServerCleanupGuardSource(
    IResourceAuthorizationService authz,
    IHttpContextAccessor http,
    IConfiguration configuration) : IProjectServerCleanupGuardSource
{
    public async Task<ProjectServerCleanupGuards> GetAsync(CancellationToken ct = default)
    {
        var user = http.HttpContext?.User;
        var writable = ServerWriteAccess.None;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var ids = await authz.GetAccessibleResourceIdsAsync(user, ResourceType.Server, Permission.Write, ct).ConfigureAwait(false);
            writable = ids is null ? ServerWriteAccess.All : new ServerWriteAccess(ids.ToHashSet());
        }
        return new ProjectServerCleanupGuards(writable, ControlPlaneHosts(configuration));
    }

    /// <summary>The host names of the control plane's configured public URLs, in lower case.</summary>
    internal static IReadOnlySet<string> ControlPlaneHosts(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var urls = new List<string?> { configuration["Aetheus:PublicApiBaseUrl"], configuration["Cors:FrontendUrl"] };
        urls.AddRange(configuration.GetSection("Cors:Origins").GetChildren().Select(origin => origin.Value));
        return urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null)
            .OfType<string>()
            .Where(host => host.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
