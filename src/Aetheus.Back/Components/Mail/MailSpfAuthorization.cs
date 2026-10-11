// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;

namespace Aetheus.Back.Components.Mail;

/// <summary>
/// Recette R1-10: whether a published SPF record authorises one sending address, evaluated as a receiver
/// would (RFC 7208, section 4.6): the terms are read in order, the first mechanism that matches decides, and
/// only a pass (<c>+</c> or no qualifier) authorises. <c>ip4</c>, <c>ip6</c>, <c>a</c>, <c>mx</c>,
/// <c>include</c> and <c>redirect</c> are resolved through <see cref="IMailDnsResolver"/>; the DNS lookups
/// are bounded by the RFC limit of ten, so a chain of includes can never run unbounded. <c>exists</c>,
/// <c>ptr</c> and terms written with macros are not evaluated: they never authorise here.
/// </summary>
internal sealed class MailSpfAuthorization(IMailDnsResolver resolver)
{
    /// <summary>RFC 7208 4.6.4: at most ten mechanisms or modifiers that query the DNS.</summary>
    internal const int MaxLookups = 10;

    /// <summary>RFC 7208 4.6.4: at most ten host names of one <c>mx</c> mechanism are looked up.</summary>
    private const int MaxMxHosts = 10;

    private enum Outcome { NoMatch, Pass, NotPass }

    private sealed class Budget
    {
        public int Remaining { get; set; } = MaxLookups;
    }

    /// <summary>True when <paramref name="record"/>, published at <paramref name="domain"/>, gives a pass to
    /// <paramref name="address"/>. A <see cref="MailDnsLookupException"/> of a nested lookup propagates.</summary>
    public async Task<bool> AuthorizesAsync(string domain, string record, IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        var sender = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
        return await EvaluateAsync(domain, record, sender, new Budget(), visited, ct).ConfigureAwait(false) == Outcome.Pass;
    }

    private async Task<Outcome> EvaluateAsync(string domain, string record, IPAddress sender, Budget budget,
        HashSet<string> visited, CancellationToken ct)
    {
        string? redirect = null;
        foreach (var raw in record.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (raw.StartsWith("redirect=", StringComparison.OrdinalIgnoreCase))
            {
                redirect = raw["redirect=".Length..];
                continue;
            }
            if (raw.Contains('=', StringComparison.Ordinal)) continue; // exp= and unknown modifiers
            var pass = raw[0] is not ('-' or '~' or '?');
            var term = raw.TrimStart('+', '-', '~', '?').ToLowerInvariant();
            var matched = await MatchesAsync(domain, term, sender, budget, visited, ct).ConfigureAwait(false);
            if (matched) return pass ? Outcome.Pass : Outcome.NotPass;
            if (budget.Remaining < 0) return Outcome.NotPass;
        }
        if (redirect is null || redirect.Contains('%', StringComparison.Ordinal)) return Outcome.NoMatch;
        return await NestedAsync(redirect, sender, budget, visited, ct).ConfigureAwait(false);
    }

    private async Task<bool> MatchesAsync(string domain, string term, IPAddress sender, Budget budget,
        HashSet<string> visited, CancellationToken ct)
    {
        if (term == "all") return true;
        if (term.StartsWith("ip4:", StringComparison.Ordinal) || term.StartsWith("ip6:", StringComparison.Ordinal))
            return MatchesNetwork(term[4..], sender);
        if (term.StartsWith("include:", StringComparison.Ordinal))
        {
            var target = term["include:".Length..];
            return !target.Contains('%', StringComparison.Ordinal)
                && await NestedAsync(target, sender, budget, visited, ct).ConfigureAwait(false) == Outcome.Pass;
        }
        if (IsMechanism(term, "a")) return await MatchesAAsync(domain, term, sender, budget, ct).ConfigureAwait(false);
        if (IsMechanism(term, "mx")) return await MatchesMxAsync(domain, term, sender, budget, ct).ConfigureAwait(false);
        // exists:, ptr and unknown mechanisms still count against the lookup limit, but never authorise.
        if (IsMechanism(term, "ptr") || term.StartsWith("exists:", StringComparison.Ordinal)) Spend(budget);
        return false;
    }

    private async Task<bool> MatchesAAsync(string domain, string term, IPAddress sender, Budget budget, CancellationToken ct)
    {
        var (host, v4, v6) = HostAndPrefixes(term, 1, domain);
        if (host is null || !Spend(budget)) return false;
        return Covers(await resolver.AddressesAsync(host, ct).ConfigureAwait(false), sender, v4, v6);
    }

    private async Task<bool> MatchesMxAsync(string domain, string term, IPAddress sender, Budget budget, CancellationToken ct)
    {
        var (host, v4, v6) = HostAndPrefixes(term, 2, domain);
        if (host is null || !Spend(budget)) return false;
        foreach (var exchange in (await resolver.MxAsync(host, ct).ConfigureAwait(false)).Take(MaxMxHosts))
        {
            if (Covers(await resolver.AddressesAsync(exchange, ct).ConfigureAwait(false), sender, v4, v6))
                return true;
        }
        return false;
    }

    /// <summary><c>include:</c> / <c>redirect=</c>: the SPF record of another name, under the same budget.
    /// A name already visited (a loop) never matches.</summary>
    private async Task<Outcome> NestedAsync(string target, IPAddress sender, Budget budget, HashSet<string> visited, CancellationToken ct)
    {
        if (!Spend(budget) || !visited.Add(target)) return Outcome.NotPass;
        var records = (await resolver.TxtAsync(target, ct).ConfigureAwait(false))
            .Where(t => t.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase) || t.Equals("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (records.Count != 1) return Outcome.NotPass;
        return await EvaluateAsync(target, records[0], sender, budget, visited, ct).ConfigureAwait(false);
    }

    private static bool Spend(Budget budget)
    {
        budget.Remaining--;
        return budget.Remaining >= 0;
    }

    private static bool IsMechanism(string term, string name) =>
        term == name || (term.StartsWith(name, StringComparison.Ordinal) && term.Length > name.Length && term[name.Length] is ':' or '/');

    /// <summary><c>a[:host][/v4][//v6]</c>: the host to look up (the current domain by default) and the prefixes.
    /// A host written with a macro is not evaluated (null).</summary>
    private static (string? Host, int V4, int V6) HostAndPrefixes(string term, int nameLength, string domain)
    {
        var rest = term[nameLength..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var hostPart = slash < 0 ? rest : rest[..slash];
        var cidr = slash < 0 ? string.Empty : rest[(slash + 1)..];
        var host = hostPart.StartsWith(':') ? hostPart[1..] : domain;
        if (host.Length == 0 || host.Contains('%', StringComparison.Ordinal)) return (null, 32, 128);
        var v4 = 32;
        var v6 = 128;
        if (cidr.StartsWith('/'))
        {
            v6 = ParsePrefix(cidr[1..], 128);
        }
        else if (cidr.Length > 0)
        {
            var parts = cidr.Split("//", 2);
            v4 = ParsePrefix(parts[0], 32);
            if (parts.Length == 2) v6 = ParsePrefix(parts[1], 128);
        }
        return (host, v4, v6);
    }

    private static int ParsePrefix(string text, int max) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix) && prefix <= max ? prefix : -1;

    private static bool Covers(IReadOnlyList<string> addresses, IPAddress sender, int v4, int v6) =>
        addresses.Any(text => IPAddress.TryParse(text, out var address)
            && InNetwork(sender, address, address.AddressFamily == AddressFamily.InterNetwork ? v4 : v6));

    private static bool MatchesNetwork(string network, IPAddress sender)
    {
        var slash = network.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(slash < 0 ? network : network[..slash], out var address)) return false;
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return InNetwork(sender, address, slash < 0 ? max : ParsePrefix(network[(slash + 1)..], max));
    }

    /// <summary>The first <paramref name="prefix"/> bits of both addresses are equal (host bits may be set in
    /// <paramref name="network"/>, as SPF allows).</summary>
    internal static bool InNetwork(IPAddress sender, IPAddress network, int prefix)
    {
        if (sender.AddressFamily != network.AddressFamily) return false;
        var a = sender.GetAddressBytes();
        var b = network.GetAddressBytes();
        if (prefix < 0 || prefix > a.Length * 8) return false;
        var whole = prefix / 8;
        for (var i = 0; i < whole; i++)
        {
            if (a[i] != b[i]) return false;
        }
        var bits = prefix % 8;
        if (bits == 0) return true;
        var mask = (byte)(0xFF << (8 - bits));
        return (a[whole] & mask) == (b[whole] & mask);
    }
}
