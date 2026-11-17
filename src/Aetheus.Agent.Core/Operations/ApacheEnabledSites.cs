// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Which enabled Apache sites use a certificate lineage, and the first one that could not be read: a site that
/// cannot be read may use it, so the caller treats it like a user.</summary>
internal readonly record struct CertificateUse(IReadOnlyList<string> Sites, string? Unreadable)
{
    public bool IsFree => Sites.Count == 0 && Unreadable is null;
}

/// <summary>
/// Recette R-97 / R-26: what the enabled Apache sites (<c>sites-enabled</c>) hold. Apache refuses to load an enabled
/// site whose <c>SSLCertificateFile</c> is gone, and then fails its configuration test for the whole server, so a
/// certificate lineage is deleted only when no enabled site references <c>live/&lt;name&gt;/</c>. Used by the project
/// deletion and by the Certbot delete and revoke operations, so both ask the same question the same way.
/// Reads go straight to the file as the agent account; a file it cannot open (certbot writes
/// <c>&lt;site&gt;-le-ssl.conf</c> as root, mode 0600) goes through the root-owned read-only helper, and a file neither
/// way can read is reported with its reason, never taken for empty.
/// </summary>
internal sealed class ApacheEnabledSites(
    string sitesEnabledPath,
    Func<string, string?> readSiteFile,
    Func<string, string?> linkTargetOf,
    Func<string, IReadOnlySet<string>?> listDirectory,
    ApacheSiteFileReader siteReader)
{
    internal const string DefaultSitesEnabledPath = "/etc/apache2/sites-enabled";
    internal const string DefaultLetsEncryptPath = "/etc/letsencrypt";

    /// <summary>The enabled sites of this host, read with the real file system and the real read helper.</summary>
    public static ApacheEnabledSites OnHost(ILogger logger) => new(
        DefaultSitesEnabledPath,
        static path =>
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return null;
            }
        },
        static path =>
        {
            try
            {
                var target = new FileInfo(path).LinkTarget;
                return target is null ? null : Path.GetFullPath(target, Path.GetDirectoryName(path)!);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return null;
            }
        },
        static path =>
        {
            if (!Directory.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            try
            {
                return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return null;
            }
        },
        new ApacheSiteFileReader(logger));

    public string SitesEnabledPath => sitesEnabledPath;

    /// <summary>The names in sites-enabled: empty when the directory does not exist, null when it cannot be read.</summary>
    public IReadOnlySet<string>? Names() => listDirectory(sitesEnabledPath);

    /// <summary>Whether an enabled site is a link whose target is gone from its directory: Apache cannot load it, so it
    /// uses no certificate and no Define (left by a site removed from sites-available without a2dissite). A link
    /// whose target directory cannot be listed is not called dangling: it is read, and blocks if it cannot be.</summary>
    public bool IsDanglingLink(string site)
    {
        var target = linkTargetOf($"{sitesEnabledPath}/{site}");
        var directory = target is null ? null : Path.GetDirectoryName(target);
        var entries = directory is null ? null : listDirectory(directory);
        return entries is not null && !entries.Contains(Path.GetFileName(target!));
    }

    /// <summary>A site file: read directly when the agent account can, otherwise through the read-only helper. A
    /// file neither way can read is a failure with its reason, never an empty text.</summary>
    public async Task<SiteFileRead> ReadAsync(string directory, string site, CancellationToken ct)
    {
        var text = readSiteFile($"{directory}/{site}");
        return text is not null ? SiteFileRead.Of(text) : await siteReader.ReadAsync(directory, site, ct).ConfigureAwait(false);
    }

    /// <summary>The enabled sites (among <paramref name="enabled"/>, the names of sites-enabled) that reference the
    /// lineage <paramref name="lineageName"/>, in name order, and the first enabled site that could not be read.</summary>
    public async Task<CertificateUse> UsingCertificateAsync(string lineageName, IReadOnlySet<string> enabled, string letsEncryptPath, CancellationToken ct)
    {
        var lineage = $"{letsEncryptPath}/live/{lineageName}/";
        var users = new List<string>();
        string? unreadable = null;
        foreach (var site in enabled.Where(site => !IsDanglingLink(site)).Order(StringComparer.Ordinal))
        {
            var read = await ReadAsync(sitesEnabledPath, site, ct).ConfigureAwait(false);
            if (read.Text is null)
                unreadable ??= $"the enabled site {site} cannot be read, so it may still use the certificate: {read.Failure}";
            else if (read.Text.Contains(lineage, StringComparison.Ordinal))
                users.Add(site);
        }
        return new CertificateUse(users, unreadable);
    }
}
