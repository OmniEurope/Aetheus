// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>The mirror coordinates of a public repository address: the provider, its base URL (null for github.com and
/// gitlab.com), the owner or group path and the repository name, which <c>ExternalRepoUrlBuilder</c> turns back into
/// <c>https://{host}/{owner}/{repository}.git</c>.</summary>
internal sealed record PublicRepositoryAddress(
    GitProviderType Provider, string? BaseUrl, string OwnerOrGroup, string RepositoryName)
{
    /// <summary>
    /// Recette R-4: the coordinates of the address a project names, so its mirror can be created from it. Only a public
    /// HTTPS address is accepted, as for a direct read before: no credentials, no other port than 443, no query or
    /// fragment, an owner and a repository made of plain path segments. The address still goes through the mirror's
    /// connectivity probe and its endpoint guard (public DNS answers only, no redirect) before anything is cloned.
    /// </summary>
    /// <exception cref="BadRequestException">The address is not such a public HTTPS repository address.</exception>
    public static PublicRepositoryAddress Parse(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) || !IsPlainPublicHttps(uri))
            throw new BadRequestException(
                "The project's repository address must be a public HTTPS address without credentials, port, query or fragment "
                + "to be mirrored. Attach the external repository from the project's external repository page instead.");

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || !segments.All(IsPlainSegment))
            throw new BadRequestException(
                "The project's repository address must name an owner and a repository (https://host/owner/repository.git).");

        var repository = segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[^1][..^4] : segments[^1];
        if (!IsPlainSegment(repository))
            throw new BadRequestException("The project's repository address names no repository.");
        var owner = string.Join('/', segments[..^1]);
        if (owner.Length > 200)
            throw new BadRequestException("The project's repository address names an owner path longer than 200 characters.");
        var host = uri.IdnHost.ToLowerInvariant();
        return host switch
        {
            "github.com" => new PublicRepositoryAddress(GitProviderType.GitHub, null, owner, repository),
            "gitlab.com" => new PublicRepositoryAddress(GitProviderType.GitLab, null, owner, repository),
            // Any other forge is self-hosted: its base URL is the host, the provider only labels it.
            _ => new PublicRepositoryAddress(
                host.StartsWith("gitlab.", StringComparison.Ordinal) ? GitProviderType.GitLab : GitProviderType.Gitea,
                $"https://{host}", owner, repository)
        };
    }

    /// <summary>HTTPS on the default port, a DNS host, no credentials, query or fragment.</summary>
    private static bool IsPlainPublicHttps(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.HostNameType is UriHostNameType.Dns;

    private static bool IsPlainSegment(string segment) =>
        segment.Length is > 0 and <= 200
        && segment is not "." and not ".."
        && segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
