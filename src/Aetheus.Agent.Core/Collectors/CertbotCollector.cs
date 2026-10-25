// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Certbot/Let's Encrypt collector. Certificate inventory is read <b>directly from
/// <c>/etc/letsencrypt/live</c></b> and parsed with <see cref="X509CertificateLoader"/> - no
/// shell, no <c>sudo certbot</c> (which is a GTFOBins root-escalation primitive and was, as
/// configured, always denied). Reading the live dir needs read access for the non-root agent;
/// when it is not granted the list is empty (graceful) rather than escalating privileges.
/// </summary>
public sealed partial class CertbotCollector : BaseShellCollector<CertbotCollector>, ICertbotCollector
{
    private const string LetsEncryptLiveDir = "/etc/letsencrypt/live";
    private const string ManageHelperPath = "/usr/local/lib/aetheus/aetheus-certbot-manage";
    private readonly Func<List<CertbotCertificateDto>> _collectCertificates;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _unixLike;
    private readonly CertbotRenewalInspector _renewalInspector;
    private readonly Func<string, string?> _readHelper;

    public CertbotCollector(ILogger<CertbotCollector> logger, IShellRunner shell)
        : this(logger, shell, null)
    {
    }

    internal CertbotCollector(
        ILogger<CertbotCollector> logger,
        IShellRunner shell,
        Func<List<CertbotCertificateDto>>? collectCertificates,
        // The known-path probe reads the real filesystem, so a test scripting the shell into
        // "not found" still detected certbot on any machine that actually has it installed, and
        // the suite failed on exactly the hosts the agent is meant to run on. Injectable here so
        // detection can be driven end to end; production keeps File.Exists.
        Func<string, bool>? fileExists = null,
        // Same reason as fileExists, one level up: the known-path fallback is Unix-only by design, so
        // on Windows the probe was unreachable and its test could only skip itself. Injecting the
        // platform decision lets the fallback be driven anywhere; production still reads the real OS.
        bool? unixLike = null,
        CertbotRenewalInspector? renewalInspector = null,
        // Injectable for the same reason as fileExists: the helper file only exists on a configured host.
        Func<string, string?>? readHelper = null)
        : base(logger, shell)
    {
        _collectCertificates = collectCertificates ?? CollectCertificates;
        _fileExists = fileExists ?? File.Exists;
        _unixLike = unixLike ?? !OperatingSystem.IsWindows();
        _renewalInspector = renewalInspector ?? new CertbotRenewalInspector(ReadFileOrNull, File.Exists);
        _readHelper = readHelper ?? ReadFileOrNull;
    }

    public async Task<CertbotDataDto> CollectAsync(CancellationToken ct = default)
    {
        string? binary = null;
        try
        {
            binary = await DetectBinaryAsync(ct).ConfigureAwait(false);
            if (binary is null)
                return new CertbotDataDto { IsInstalled = false };

            var (renewalCheckedAt, renewalCheckSucceeded) = _renewalInspector.ReadRenewalCheck();
            return new CertbotDataDto
            {
                IsInstalled = true,
                Version = await CollectVersionAsync(binary, ct).ConfigureAwait(false),
                Certificates = _renewalInspector.Annotate(_collectCertificates().Take(2048)),
                RenewalCheckedAt = renewalCheckedAt,
                HelperVersion = ReadHelperVersion(),
                RenewalCheckSucceeded = renewalCheckSucceeded,
                RenewalCheckFailures = _renewalInspector.ReadRenewalCheckFailures(),
                RenewalCheckOutputTail = _renewalInspector.ReadRenewalCheckOutputTail(),
                AcmeProxyRules = _renewalInspector.ReadAcmeProxyRules()
            };
        }
        catch (Exception ex)
        {
            // F-ENG-06: a partial failure after the binary was found must not report certbot as uninstalled,
            // and must be visible (LogWarning), not hidden in Debug.
            var installed = binary is not null;
            Logger.LogWarning(ex, installed
                ? "Certbot data collection failed after detecting the binary; reporting installed"
                : "Certbot detection failed");
            return new CertbotDataDto { IsInstalled = installed };
        }
    }

    // Common absolute locations for the certbot binary. The agent runs under a hardened systemd
    // unit whose PATH may not include these, so a bare `which certbot` can return nothing even
    // though `apt install certbot` succeeded, which is exactly the Services-tab-says-Installed /
    // Certbot-section-says-NOT-INSTALLED mismatch (CBDT). Fall back to these before giving up so
    // both detections agree.
    private static readonly string[] KnownCertbotPaths =
    [
        "/usr/bin/certbot",
        "/usr/local/bin/certbot",
        "/snap/bin/certbot",
        "/opt/certbot/bin/certbot"
    ];

    private async Task<string?> DetectBinaryAsync(CancellationToken ct)
    {
        var (locator, name) = OperatingSystem.IsWindows() ? ("where", "certbot") : ("which", "certbot");
        var res = await Shell.RunExecAsync(locator, [name], ct).ConfigureAwait(false);
        if (res.ExitCode == 0)
        {
            var path = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (path is { Length: > 0 })
                return path;
        }

        // PATH lookup failed: probe the well-known install locations directly (no shell).
        return _unixLike ? KnownCertbotPaths.FirstOrDefault(_fileExists) : null;
    }

    private async Task<string> CollectVersionAsync(string binary, CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync(binary, ["--version"], ct).ConfigureAwait(false);
            // certbot prints "certbot 2.7.4" - to stdout on modern versions, stderr on old ones.
            var match = VersionRegex().Match(res.StdOut);
            if (!match.Success)
                match = VersionRegex().Match(res.StdErr);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to get Certbot version");
            return string.Empty;
        }
    }

    /// <summary>
    /// Enumerates <c>/etc/letsencrypt/live/&lt;name&gt;/fullchain.pem</c> and reads expiry/domains
    /// straight from the X.509 leaf. No shell, no sudo. If the agent lacks read access the dir
    /// enumeration throws and an empty list is returned (the operator can grant a read ACL).
    /// </summary>
    private List<CertbotCertificateDto> CollectCertificates() => CollectCertificatesFrom(LetsEncryptLiveDir);

    // The inventory of one live directory, so it is read from a temporary directory in tests.
    internal List<CertbotCertificateDto> CollectCertificatesFrom(string liveDirectory)
    {
        var result = new List<CertbotCertificateDto>();
        if (!_unixLike || !Directory.Exists(liveDirectory))
            return result;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(liveDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogDebug(ex, "No read access to {Dir} - grant the agent a read ACL", liveDirectory);
            return result;
        }

        foreach (var dir in dirs)
        {
            try
            {
                var certPath = Path.Combine(dir, "fullchain.pem");
                if (!File.Exists(certPath))
                    continue;

                using var cert = X509CertificateLoader.LoadCertificateFromFile(certPath);

                result.Add(new CertbotCertificateDto
                {
                    Name = Path.GetFileName(dir),
                    Domains = ExtractDomains(cert),
                    ExpiryDate = cert.NotAfter.ToUniversalTime(),
                    CertPath = certPath,
                    KeyPath = Path.Combine(dir, "privkey.pem")
                });
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to read certificate in {Dir}", dir);
            }
        }
        return result;
    }

    // Renewal settings are optional context: a missing or unreadable file means "unknown", not a failure.
    private string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Cannot read {Path}", path);
            return null;
        }
    }

    internal static List<string> ExtractDomains(X509Certificate2 cert)
    {
        var domains = new List<string>();

        var sanRaw = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (sanRaw is not null)
        {
            var san = sanRaw as X509SubjectAlternativeNameExtension
                      ?? new X509SubjectAlternativeNameExtension(sanRaw.RawData);
            domains.AddRange(san.EnumerateDnsNames());
        }

        if (domains.Count == 0)
        {
            var cn = cert.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrEmpty(cn))
                domains.Add(cn);
        }
        return domains;
    }

    // `# aetheus-certbot-helper-version: N` on the root-owned helper; 0 when it is absent or carries no marker.
    internal int ReadHelperVersion()
    {
        var match = HelperVersionRegex().Match(_readHelper(ManageHelperPath) ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    [GeneratedRegex(@"aetheus-certbot-helper-version:\s*(\d+)")]
    private static partial Regex HelperVersionRegex();

    // "certbot 2.7.4"
    [GeneratedRegex(@"certbot\s+([\d.]+)")]
    private static partial Regex VersionRegex();
}
