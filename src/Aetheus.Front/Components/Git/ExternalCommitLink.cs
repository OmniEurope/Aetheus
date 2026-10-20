// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

/// <summary> The page of a commit at the provider that hosts its repository, for a commit no
/// internal repository holds. Only an http(s) repository URL gives a link; the path follows the
/// provider's convention (GitLab's "/-/commit/", Bitbucket's "/commits/", "/commit/" elsewhere).
/// </summary>
internal static class ExternalCommitLink
{
    public static string? For(string? repositoryUrl, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)
            || !Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (path.Length == 0)
            return null;

        var host = uri.Host.ToLowerInvariant();
        var segment = host.Contains("gitlab", StringComparison.Ordinal) ? "/-/commit/"
            : host.Contains("bitbucket", StringComparison.Ordinal) ? "/commits/"
            : "/commit/";
        return $"{uri.Scheme}://{uri.Authority}{path}{segment}{Uri.EscapeDataString(sha)}";
    }
}
