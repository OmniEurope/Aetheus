// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-26: the HTTPS origin the production deployment of a project serves its application on, read the way the generic
/// production template builds it (ADR-043): <c>APP_HOST</c> is a formula over the entries of the libraries
/// <c>&lt;slug&gt;.share</c> and <c>&lt;slug&gt;.prod</c> (<c>$(HOST_PREFIX:-)$(APP_SUBDOMAIN:suffix=.)$(DOMAIN)</c>) and
/// the address is <c>https://$(APP_HOST)</c>. These are the values the wizard just wrote (the domain typed in its
/// production panel), so the audience origins and the application deployed agree. Null while the domain is not known:
/// nothing is guessed.
/// </summary>
internal static class PipelineSetupProductionOrigin
{
    private const string HostFormula = "$(HOST_PREFIX:-)$(APP_SUBDOMAIN:suffix=.)$(DOMAIN)";
    private const int MaxPasses = 4;

    /// <summary>The origin read from the project's libraries; null when the domain is empty or a reference stays unresolved.</summary>
    public static async Task<string?> ReadAsync(
        IVariableLibraryService libraries, string slug, int projectId, CancellationToken ct)
    {
        var values = await libraries.ResolveLibrariesAsync([$"{slug}.share", $"{slug}.prod"], projectId, ct).ConfigureAwait(false);
        return From(values);
    }

    /// <summary>The origin the entries describe: an explicit <c>APP_HOST</c> wins, else the template's formula.</summary>
    internal static string? From(IReadOnlyDictionary<string, string> entries)
    {
        var lookup = new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);
        if (!lookup.TryGetValue("APP_HOST", out var host) || string.IsNullOrWhiteSpace(host))
        {
            if (!lookup.TryGetValue("DOMAIN", out var domain) || string.IsNullOrWhiteSpace(domain)) return null;
            host = HostFormula;
        }

        for (var pass = 0; pass < MaxPasses && host.Contains("$(", StringComparison.Ordinal); pass++)
            host = PipelineCommandBuilder.Substitute(host, lookup, _ => true);
        if (host.Contains("$(", StringComparison.Ordinal)) return null;

        return Uri.TryCreate($"https://{host.Trim()}", UriKind.Absolute, out var uri)
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Host.Contains('.', StringComparison.Ordinal)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
