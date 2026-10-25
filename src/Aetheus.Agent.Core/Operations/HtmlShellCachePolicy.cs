// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The Cache-Control policy the agent requires on the frontend's HTML shell: revalidated on every use
/// (<c>no-cache</c>) and never forbidden from storage (<c>no-store</c> would keep the page out of the
/// browser's back/forward cache, recette R-8). The static server serves exactly this
/// (<c>deploy/docker/StaticServer.Program.cs</c>).
///
/// R-8 phase 2: the transitional acceptance of <c>no-store</c> that phase 1 carried is gone. It is only
/// safe to merge once the release holding phase 1 is the agent installed in production: the agent runs in
/// the previous release while a deploy-prod runs, and that agent must already accept what this static
/// server serves.
/// </summary>
internal static class HtmlShellCachePolicy
{
    internal const string NoStore = "no-store";
    internal const string NoCache = "no-cache";

    /// <summary>True when the Cache-Control values carry <c>no-cache</c> and not <c>no-store</c>. A
    /// field-qualified <c>no-cache="..."</c>, a max-age without <c>no-cache</c> and an absent header are
    /// refused.</summary>
    internal static bool IsRevalidated(IEnumerable<string> cacheControlValues)
    {
        ArgumentNullException.ThrowIfNull(cacheControlValues);
        var tokens = cacheControlValues
            .SelectMany(value => value.Split(','))
            .Select(token => token.Trim())
            .ToList();
        return tokens.Contains(NoCache, StringComparer.OrdinalIgnoreCase)
            && !tokens.Contains(NoStore, StringComparer.OrdinalIgnoreCase);
    }
}
