// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>Read mail configuration directly without shell or sudo. Mark unreadable sources as
/// uncollected with diagnostics so partial inventory cannot deactivate stored rows.</summary>
internal sealed partial class MailInventoryReader(Func<string, string?> readText, Func<string, bool> fileExists)
{
    internal const string ManagedMarkerPath = "/etc/aetheus/mail-stack.managed";
    internal const string ManageHelperPath = "/usr/local/lib/aetheus/mail-manage";
    internal const string OpenDkimConfigPath = "/etc/opendkim.conf";
    internal const string DefaultKeyTablePath = "/etc/opendkim/KeyTable";
    internal const string RspamdActionsPath = "/etc/rspamd/local.d/actions.conf";

    // Postfix lookup tables whose source is a plain "key value" text file we can enumerate.
    private static readonly HashSet<string> s_fileMapTypes =
        new(StringComparer.Ordinal) { "hash", "lmdb", "btree", "cdb", "dbm", "sdbm", "texthash" };

    public List<string> Diagnostics { get; } = [];

    public bool IsManagedByAetheus() => fileExists(ManagedMarkerPath);

    public int ReadHelperVersion()
    {
        var text = readText(ManageHelperPath);
        if (text is null) return 0;
        var match = HelperVersionRegex().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    /// <summary>Mailboxes from <c>virtual_mailbox_maps</c>. Collected=false when any source is unreadable or unsupported.</summary>
    public (List<MailAccountDto> Accounts, bool Collected) ReadMailboxes(string mapsValue)
    {
        var accounts = new List<MailAccountDto>();
        var collected = true;
        foreach (var text in ReadMapSources(mapsValue, ref collected))
        {
            foreach (var (key, _) in MapEntries(text))
            {
                if (!MailValidation.IsValidEmail(key) || accounts.Any(a => a.Email.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (accounts.Count >= MailInventoryLimits.MaxAccounts)
                {
                    Diagnostics.Add($"truncated|accounts:{MailInventoryLimits.MaxAccounts}");
                    return (accounts, false);
                }
                accounts.Add(new MailAccountDto { Email = key, Domain = key[(key.IndexOf('@') + 1)..], IsActive = true });
            }
        }
        return (accounts, collected);
    }

    /// <summary>Single-destination aliases from <c>virtual_alias_maps</c>; multi-destination entries are counted, not imported.</summary>
    public (List<MailAliasDto> Aliases, bool Collected) ReadAliases(string mapsValue)
    {
        var aliases = new List<MailAliasDto>();
        var collected = true;
        var skipped = 0;
        foreach (var text in ReadMapSources(mapsValue, ref collected))
        {
            foreach (var (key, value) in MapEntries(text))
            {
                if (!MailValidation.IsValidEmail(key)) continue;
                var destinations = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (destinations.Length != 1 || !MailValidation.IsValidEmail(destinations[0]))
                {
                    skipped++;
                    continue;
                }
                if (aliases.Count >= MailInventoryLimits.MaxAliases)
                {
                    Diagnostics.Add($"truncated|aliases:{MailInventoryLimits.MaxAliases}");
                    return (aliases, false);
                }
                aliases.Add(new MailAliasDto { SourceEmail = key, DestinationEmail = destinations[0], IsActive = true });
            }
        }
        if (skipped > 0)
            Diagnostics.Add($"aliases-skipped|{skipped.ToString(CultureInfo.InvariantCulture)}");
        return (aliases, collected);
    }

    /// <summary>DKIM keys of the OpenDKIM KeyTable, with the public TXT value when the <c>.txt</c> is readable.</summary>
    public List<MailDkimKeyDto> ReadDkimKeys()
    {
        var keyTablePath = KeyTablePath();
        var text = ReadDiagnosed(keyTablePath);
        if (text is null) return [];

        var keys = new List<MailDkimKeyDto>();
        foreach (var (_, value) in MapEntries(text))
        {
            // "domain:selector:/etc/opendkim/keys/domain/selector.private"
            var parts = value.Split(':', 3);
            if (parts.Length < 3 || !MailValidation.IsValidDomainName(parts[0]) || !MailValidation.IsValidDkimSelector(parts[1]))
                continue;
            var publicKeyPath = parts[2].EndsWith(".private", StringComparison.Ordinal)
                ? parts[2][..^".private".Length] + ".txt"
                : string.Empty;
            var txt = publicKeyPath.Length > 0 ? readText(publicKeyPath) : null;
            if (txt is null && publicKeyPath.Length > 0)
                Diagnostics.Add($"dkim-public-unreadable|{parts[0]}");
            keys.Add(new MailDkimKeyDto
            {
                Domain = parts[0],
                Selector = parts[1],
                PublicKey = Truncate(MailTaskOutputParser.ParseDkimTxtRecord(txt), 4096)
            });
            if (keys.Count >= MailInventoryLimits.MaxDomains) break;
        }
        return keys;
    }

    /// <summary>Certificate Postfix presents. Unreadable (for example a Let's Encrypt live dir) keeps only the path.</summary>
    public MailTlsStateDto ReadTls(string certPath)
    {
        if (string.IsNullOrWhiteSpace(certPath)) return new MailTlsStateDto();
        var pem = readText(certPath);
        if (pem is null)
        {
            Diagnostics.Add($"tls-unreadable|{Truncate(certPath, 400)}");
            return new MailTlsStateDto { CertPath = Truncate(certPath, 512) };
        }
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return new MailTlsStateDto
            {
                CertPath = Truncate(certPath, 512),
                IsReadable = true,
                Subject = Truncate(cert.Subject, 512),
                Issuer = Truncate(cert.Issuer, 512),
                ExpiresAt = cert.NotAfter.ToUniversalTime(),
                IsSelfSigned = string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal)
            };
        }
        catch (CryptographicException)
        {
            Diagnostics.Add($"tls-invalid|{Truncate(certPath, 400)}");
            return new MailTlsStateDto { CertPath = Truncate(certPath, 512) };
        }
    }

    /// <summary>rspamd action thresholds from the Aetheus-managed <c>local.d/actions.conf</c>; nulls when absent.</summary>
    public (double? Reject, double? AddHeader, double? Greylist) ReadSpamThresholds()
    {
        var text = readText(RspamdActionsPath);
        if (text is null) return (null, null, null);
        double? reject = null, header = null, grey = null;
        foreach (Match match in SpamActionRegex().Matches(text))
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                continue;
            switch (match.Groups[1].Value)
            {
                case "reject": reject = score; break;
                case "add_header": header = score; break;
                case "greylist": grey = score; break;
            }
        }
        return (reject, header, grey);
    }

    private string KeyTablePath()
    {
        var config = readText(OpenDkimConfigPath);
        if (config is null) return DefaultKeyTablePath;
        var match = KeyTableDirectiveRegex().Match(config);
        if (!match.Success) return DefaultKeyTablePath;
        var value = match.Groups[1].Value;
        var colon = value.IndexOf(':');
        return colon >= 0 && !value.StartsWith('/') ? value[(colon + 1)..] : value;
    }

    private List<string> ReadMapSources(string mapsValue, ref bool collected)
    {
        var sources = new List<string>();
        foreach (var entry in mapsValue.Split([',', ' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.StartsWith('$')) continue; // parameter reference, resolved by postconf itself
            var colon = entry.IndexOf(':');
            var type = colon > 0 ? entry[..colon] : string.Empty;
            if (!s_fileMapTypes.Contains(type))
            {
                Diagnostics.Add($"unsupported-map|{Truncate(entry, 400)}");
                collected = false;
                continue;
            }
            var text = ReadDiagnosed(entry[(colon + 1)..]);
            if (text is null) { collected = false; continue; }
            sources.Add(text);
        }
        return sources;
    }

    private string? ReadDiagnosed(string path)
    {
        var text = readText(path);
        if (text is null)
            Diagnostics.Add(fileExists(path) ? $"unreadable|{Truncate(path, 400)}" : $"missing|{Truncate(path, 400)}");
        return text;
    }

    private static IEnumerable<(string Key, string Value)> MapEntries(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0) continue;
            yield return (line[..split], line[split..].Trim());
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"aetheus-mail-helper-version:\s*(\d+)")]
    private static partial Regex HelperVersionRegex();

    [GeneratedRegex(@"^\s*KeyTable\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex KeyTableDirectiveRegex();

    [GeneratedRegex(@"^\s*(reject|add_header|greylist)\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*;", RegexOptions.Multiline)]
    private static partial Regex SpamActionRegex();
}
