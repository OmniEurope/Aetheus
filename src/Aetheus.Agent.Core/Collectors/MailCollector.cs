// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

public sealed partial class MailCollector(
    ILogger<MailCollector> logger,
    IShellRunner shell,
    Func<string, bool>? fileExists = null,
    Func<string, string?>? readText = null,
    Func<IEnumerable<System.Net.IPAddress>>? hostAddresses = null)
    : BaseShellCollector<MailCollector>(logger, shell), IMailCollector
{
    private const string PostfixMainConfigPath = "/etc/postfix/main.cf";

    // Inventory sources are small text files; anything larger is not a mail map worth inventorying.
    private const long MaxInventoryFileBytes = 8 * 1024 * 1024;

    private static readonly string[] s_postconfParameters =
        [
            "myhostname", "smtpd_tls_cert_file", "virtual_mailbox_maps", "virtual_alias_maps",
            "inet_protocols", "smtp_address_preference", "minimal_backoff_time", "maximal_backoff_time", "queue_run_delay"
        ];

    private Func<string, bool> FileExists => fileExists ?? File.Exists;

    public async Task<MailDataDto> CollectAsync(CancellationToken ct = default)
    {
        var hasConfiguredPostfix = false;
        try
        {
            var hasPostfixBinary = await DetectBinaryAsync("postfix", ct).ConfigureAwait(false);
            if (!hasPostfixBinary)
                return new MailDataDto { IsInstalled = false };

            var postfixVersion = await TryCollectConfiguredPostfixVersionAsync(ct).ConfigureAwait(false);
            if (postfixVersion is null)
                return new MailDataDto { IsInstalled = false };

            hasConfiguredPostfix = true;
            var hasDovecot = await DetectBinaryAsync("dovecot", ct).ConfigureAwait(false);
            var hasRspamd = await DetectOptionalBinaryAsync("rspamadm", ct).ConfigureAwait(false);

            var dovecotVersionTask = hasDovecot ? CollectDovecotVersionAsync(ct) : Task.FromResult(string.Empty);
            var postfixRunningTask = CollectServiceRunningAsync("postfix", ct);
            var dovecotRunningTask = hasDovecot ? CollectServiceRunningAsync("dovecot", ct) : Task.FromResult(false);
            var openDkimRunningTask = CollectServiceRunningAsync("opendkim", ct);
            var rspamdRunningTask = hasRspamd ? CollectServiceRunningAsync("rspamd", ct) : Task.FromResult(false);
            var rspamdVersionTask = hasRspamd ? CollectRspamdVersionAsync(ct) : Task.FromResult(string.Empty);
            var queueTask = CollectQueueSizeAsync(ct);
            var domainsTask = CollectDomainsAsync(ct);
            var parametersTask = CollectPostconfParametersAsync(ct);
            var deferredTask = CollectDeferredAsync(ct);

            await Task.WhenAll(dovecotVersionTask, postfixRunningTask, dovecotRunningTask, openDkimRunningTask,
                rspamdRunningTask, rspamdVersionTask, queueTask, domainsTask, parametersTask, deferredTask).ConfigureAwait(false);

            var parameters = await parametersTask.ConfigureAwait(false);
            var (domains, domainsCollected) = await domainsTask.ConfigureAwait(false);
            var reader = new MailInventoryReader(ReadInventoryText, FileExists);
            var (accounts, accountsCollected) = parameters is null
                ? ([], false)
                : reader.ReadMailboxes(parameters.GetValueOrDefault("virtual_mailbox_maps", string.Empty));
            var (aliases, aliasesCollected) = parameters is null
                ? ([], false)
                : reader.ReadAliases(parameters.GetValueOrDefault("virtual_alias_maps", string.Empty));
            var (reject, addHeader, greylist) = hasRspamd ? reader.ReadSpamThresholds() : (null, null, null);

            return new MailDataDto
            {
                IsInstalled = true,
                IsPostfixRunning = await postfixRunningTask.ConfigureAwait(false),
                IsDovecotRunning = await dovecotRunningTask.ConfigureAwait(false),
                IsOpenDkimRunning = await openDkimRunningTask.ConfigureAwait(false),
                PostfixVersion = postfixVersion,
                DovecotVersion = await dovecotVersionTask.ConfigureAwait(false),
                QueueSize = await queueTask.ConfigureAwait(false),
                Domains = domains,
                DomainsCollected = domainsCollected,
                Accounts = accounts,
                AccountsCollected = accountsCollected,
                Aliases = aliases,
                AliasesCollected = aliasesCollected,
                Hostname = Truncate(parameters?.GetValueOrDefault("myhostname", string.Empty) ?? string.Empty, 253),
                IsManagedByAetheus = reader.IsManagedByAetheus(),
                HelperVersion = reader.ReadHelperVersion(),
                DkimKeys = reader.ReadDkimKeys(),
                Tls = reader.ReadTls(parameters?.GetValueOrDefault("smtpd_tls_cert_file", string.Empty) ?? string.Empty),
                SpamFilter = new MailSpamFilterStateDto
                {
                    Name = hasRspamd ? "rspamd" : string.Empty,
                    IsInstalled = hasRspamd,
                    IsRunning = await rspamdRunningTask.ConfigureAwait(false),
                    Version = await rspamdVersionTask.ConfigureAwait(false),
                    RejectScore = reject,
                    AddHeaderScore = addHeader,
                    GreylistScore = greylist
                },
                Delivery = BuildDelivery(parameters, await deferredTask.ConfigureAwait(false)),
                Diagnostics = reader.Diagnostics.Take(MailInventoryLimits.MaxDiagnostics).ToList()
            };
        }
        catch (Exception ex)
        {
            // F-ENG-06: a partial failure after a configured postfix was found must not report mail as uninstalled,
            // and must be visible (LogWarning), not hidden in Debug. The inventory flags stay false so the backend
            // never deactivates records from this partial report.
            Logger.LogWarning(ex, hasConfiguredPostfix
                ? "Mail data collection failed after validating postfix; reporting installed"
                : "Mail detection failed");
            return new MailDataDto { IsInstalled = hasConfiguredPostfix };
        }
    }

    // Reads an inventory file without ever throwing: null means missing, unreadable or oversized.
    private string? ReadInventoryText(string path)
    {
        if (readText is not null) return readText(path);
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxInventoryFileBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Mail inventory file {Path} is not readable", path);
            return null;
        }
    }

    private async Task<bool> DetectBinaryAsync(string binary, CancellationToken ct)
    {
        var (locator, target) = OperatingSystem.IsWindows()
            ? ("where", $"{binary}.exe")
            : ("which", binary);
        var res = await Shell.RunExecAsync(locator, [target], ct).ConfigureAwait(false);
        return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut);
    }

    // Optional components (the spam filter) must never abort the mail collection when their probe fails.
    private async Task<bool> DetectOptionalBinaryAsync(string binary, CancellationToken ct)
    {
        try
        {
            return await DetectBinaryAsync(binary, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to detect {Binary}", binary);
            return false;
        }
    }

    private async Task<string?> TryCollectConfiguredPostfixVersionAsync(CancellationToken ct)
    {
        if (!FileExists(PostfixMainConfigPath))
        {
            Logger.LogDebug(
                "Postfix binary found but {ConfigPath} is absent; skipping mail collection",
                PostfixMainConfigPath);
            return null;
        }

        try
        {
            var res = await Shell.RunExecAsync("postconf", ["mail_version"], ct).ConfigureAwait(false);
            if (res.ExitCode != 0)
            {
                Logger.LogDebug(
                    "Postfix configuration validation failed with exit code {ExitCode}; skipping mail collection",
                    res.ExitCode);
                return null;
            }

            var match = PostfixVersionRegex().Match(res.StdOut);
            if (match.Success)
                return match.Groups[1].Value;

            Logger.LogDebug("Postfix configuration validation returned no mail version; skipping mail collection");
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Postfix configuration validation failed; skipping mail collection");
            return null;
        }
    }

    private async Task<string> CollectDovecotVersionAsync(CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("dovecot", ["--version"], ct).ConfigureAwait(false);
            var output = res.StdOut;
            var match = DovecotVersionRegex().Match(output);
            return match.Success ? match.Groups[1].Value : output.Trim().Split(' ')[0];
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to get Dovecot version");
            return string.Empty;
        }
    }

    private async Task<string> CollectRspamdVersionAsync(CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("rspamadm", ["--version"], ct).ConfigureAwait(false);
            var match = RspamdVersionRegex().Match(res.StdOut);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to get rspamd version");
            return string.Empty;
        }
    }

    private async Task<bool> CollectServiceRunningAsync(string serviceName, CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("systemctl", ["is-active", serviceName], ct).ConfigureAwait(false);
            return res.StdOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to check {Service} running state", serviceName);
            return false;
        }
    }

    private async Task<int> CollectQueueSizeAsync(CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("postqueue", ["-p"], ct).ConfigureAwait(false);
            if (res.StdOut.Contains("Mail queue is empty", StringComparison.OrdinalIgnoreCase))
                return 0;

            // Was `postqueue -p | tail -n 1` - take the last non-empty line in-process.
            var lastLine = res.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? string.Empty;

            var match = QueueSizeRegex().Match(lastLine);
            return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to collect mail queue size");
            return 0;
        }
    }

    private async Task<(List<MailDomainDto> Domains, bool Collected)> CollectDomainsAsync(CancellationToken ct)
    {
        var result = new List<MailDomainDto>();
        try
        {
            var res = await Shell.RunExecAsync("postconf", ["-h", "virtual_mailbox_domains"], ct).ConfigureAwait(false);
            if (res.ExitCode != 0)
                return (result, false);
            var output = res.StdOut;
            if (string.IsNullOrWhiteSpace(output))
                return (result, true);

            var domains = output.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var domain in domains)
            {
                if (string.IsNullOrWhiteSpace(domain) || domain.StartsWith('$'))
                    continue;
                if (result.Count >= MailInventoryLimits.MaxDomains)
                    return (result, false);

                result.Add(new MailDomainDto
                {
                    Name = domain.Trim(),
                    IsActive = true
                });
            }
            return (result, true);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to collect mail domains");
            return (result, false);
        }
    }

    // One `postconf name...` call ("name = value" lines, parsed by name so output order does not matter).
    private async Task<Dictionary<string, string>?> CollectPostconfParametersAsync(CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("postconf", s_postconfParameters, ct).ConfigureAwait(false);
            if (res.ExitCode != 0) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return values;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read postfix parameters");
            return null;
        }
    }

    // Deferred entries with their deferral reason: `postqueue -j` (Postfix 3.1+), else the `postqueue -p`
    // listing. Read-only and bounded; a failure only leaves the list empty.
    private async Task<List<MailDeferredMessageDto>> CollectDeferredAsync(CancellationToken ct)
    {
        try
        {
            var json = await Shell.RunExecAsync("postqueue", ["-j"], ct).ConfigureAwait(false);
            if (json.ExitCode == 0)
            {
                var entries = MailDeliveryParser.ParseQueueJson(json.StdOut);
                if (entries.Count > 0 || json.StdOut.Contains("Mail queue is empty", StringComparison.OrdinalIgnoreCase))
                    return entries;
            }

            var text = await Shell.RunExecAsync("postqueue", ["-p"], ct).ConfigureAwait(false);
            return text.ExitCode == 0 ? MailDeliveryParser.ParseQueueText(text.StdOut) : [];
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to collect deferred mail queue entries");
            return [];
        }
    }

    private MailDeliveryStateDto BuildDelivery(Dictionary<string, string>? parameters, List<MailDeferredMessageDto> deferred)
    {
        string Value(string name) => Truncate(parameters?.GetValueOrDefault(name, string.Empty) ?? string.Empty, 40);
        return new MailDeliveryStateDto
        {
            InetProtocols = Value("inet_protocols"),
            SmtpAddressPreference = Value("smtp_address_preference"),
            MinimalBackoffTime = Value("minimal_backoff_time"),
            MaximalBackoffTime = Value("maximal_backoff_time"),
            QueueRunDelay = Value("queue_run_delay"),
            Deferred = deferred,
            HostIpv6Addresses = MailDeliveryParser.GlobalIpv6(ReadHostAddresses())
        };
    }

    private IEnumerable<System.Net.IPAddress> ReadHostAddresses()
    {
        if (hostAddresses is not null) return hostAddresses();
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException ex)
        {
            Logger.LogDebug(ex, "Failed to list the host network addresses");
            return [];
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // "mail_version = 3.8.1"
    [GeneratedRegex(@"mail_version\s*=\s*([\d.]+)")]
    private static partial Regex PostfixVersionRegex();

    // "2.3.21 (abc123)"
    [GeneratedRegex(@"^([\d.]+)")]
    private static partial Regex DovecotVersionRegex();

    // "Rspamadm 3.4" / "rspamadm 3.8.1"
    [GeneratedRegex(@"([\d]+\.[\d.]+)")]
    private static partial Regex RspamdVersionRegex();

    // "-- 5 Kbytes in 3 Requests."
    [GeneratedRegex(@"(\d+)\s+Request")]
    private static partial Regex QueueSizeRegex();
}
