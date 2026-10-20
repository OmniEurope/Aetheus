// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>ensure-tls-certificates</c> function, port of <c>deploy/scripts/ensure-tls-certificates.sh</c>:
/// for each certificate group <c>NAME[,ALIAS...]</c> (the first name is the lineage), a missing
/// certificate, or one that does not name every host of its group, is issued through the agent's
/// root-owned <c>aetheus-certbot-issue</c> helper; one that expires within <c>min_days</c> is renewed
/// through <c>aetheus-certbot-manage renew</c>, and when that fails it is issued again through the web
/// root with exactly the names it already carries (never fewer, which would open a second lineage). A
/// failed issuance or renewal is only a warning: every lineage is attempted, then every certificate must
/// exist, name all its hosts and stay valid for <c>min_days</c>, or the function fails listing each
/// lineage that is not in order. Same helper calls (<c>sudo -n</c>, the domains, email and mode in
/// <c>AETHEUS_CERTBOT_*</c>), same order, same messages as the script.
///
/// Differences, deliberate: the groups the script took as arguments are the required <c>hosts</c>
/// parameter (separated by spaces); <c>TLS_MIN_DAYS</c>, <c>TLS_LIVE_DIR</c>,
/// <c>AETHEUS_CERTBOT_EMAIL</c> and <c>AGENT_HELPERS_DIR</c> are the <c>min_days</c>, <c>live_dir</c>,
/// <c>email</c> and <c>helpers_dir</c> parameters, defaulting to the same variables and values (the
/// helper directory checked as <c>deploy-identity</c> checks it). The certificate's end date and DNS
/// names are read with the runtime's X.509 parser instead of <c>openssl x509</c> and <c>date -d</c>,
/// on the first certificate of <c>fullchain.pem</c> as openssl reads it.
/// </summary>
public sealed class EnsureTlsCertificatesFunction : IPipelineFunction
{
    private readonly IPipelineFunctionTools _tools;
    private readonly TimeProvider _time;

    public EnsureTlsCertificatesFunction()
        : this(PipelineFunctionTools.Default, TimeProvider.System)
    {
    }

    internal EnsureTlsCertificatesFunction(IPipelineFunctionTools tools, TimeProvider time)
    {
        _tools = tools;
        _time = time;
    }

    public string Name => PipelineFunctionCatalog.EnsureTlsCertificates.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var groups = (context.Parameters.GetValueOrDefault("hosts") ?? string.Empty)
            .Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length == 0)
            return PipelineFunctionResult.Failure("At least one certificate group NAME[,ALIAS...] is required in hosts.");
        var helpers = FunctionShell.Input(context, "helpers_dir", "AGENT_HELPERS_DIR") ?? DeployIdentityFunction.DefaultHelpersDirectory;
        if (helpers.Contains("..", StringComparison.Ordinal))
            return PipelineFunctionResult.Failure($"AGENT_HELPERS_DIR must not contain a traversal: {helpers}");
        if (!helpers.StartsWith('/'))
            return PipelineFunctionResult.Failure($"AGENT_HELPERS_DIR must be an absolute path: {helpers}");
        var liveDir = FunctionShell.Input(context, "live_dir", "TLS_LIVE_DIR") ?? "/etc/letsencrypt/live";
        var minDaysText = FunctionShell.Input(context, "min_days", "TLS_MIN_DAYS") ?? "30";
        if (!minDaysText.All(char.IsAsciiDigit))
            return PipelineFunctionResult.Failure($"TLS_MIN_DAYS must be a number of days: {minDaysText}");
        var minDays = long.TryParse(minDaysText, out var parsed) ? parsed : long.MaxValue;

        var helper = new CertbotHelpers(_tools, context, helpers, FunctionShell.Input(context, "email", "AETHEUS_CERTBOT_EMAIL") ?? string.Empty);
        foreach (var group in groups.Select(TrimAlias))
        {
            var invalid = Names(group).FirstOrDefault(name => !IsQualifiedHost(name));
            if (invalid is not null)
                return PipelineFunctionResult.Failure($"Not a fully qualified hostname: '{invalid}' in '{group}'");
            await EnsureAsync(context, helper, liveDir, minDays, group, cancellationToken).ConfigureAwait(false);
        }

        var failed = string.Concat(groups.Select(TrimAlias).Select(group => Verdict(liveDir, minDays, minDaysText, group)));
        if (failed.Length > 0)
            return PipelineFunctionResult.Failure("TLS is not in order after issuance and renewal:" + failed);
        await context.Log($">>> Every certificate is valid for at least {minDaysText} more days: {string.Join(' ', groups)}").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>Issues, renews or reports one group, as the script's first loop does.</summary>
    private async Task EnsureAsync(
        PipelineFunctionContext context, CertbotHelpers helper, string liveDir, long minDays, string group, CancellationToken cancellationToken)
    {
        var lineage = Lineage(group);
        var certificate = CertificatePath(liveDir, lineage);
        if (!File.Exists(certificate) || !NamesAll(certificate, Names(group)))
        {
            await context.Log($">>> Issuing the certificate for {group}").ConfigureAwait(false);
            if (!await helper.IssueAsync(group, lineage, cancellationToken).ConfigureAwait(false))
                await context.Log($"WARNING: issuance failed for {group}; the check below decides.", TaskLogLevel.Warning).ConfigureAwait(false);
            return;
        }

        var left = DaysLeft(certificate);
        if (left is { } days && days >= minDays)
        {
            await context.Log($">>> {lineage}: valid for {days} more days").ConfigureAwait(false);
            return;
        }

        await context.Log($">>> The certificate for {lineage} expires in {left?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown number of"} days; renewing")
            .ConfigureAwait(false);
        if (await helper.RenewAsync(lineage, cancellationToken).ConfigureAwait(false))
            return;
        var current = string.Join(',', DnsNames(certificate));
        if (current.Length == 0)
        {
            await context.Log($"WARNING: renewal failed for {lineage} and its names are unreadable; the check below decides.", TaskLogLevel.Warning)
                .ConfigureAwait(false);
            return;
        }
        await context.Log($">>> Renewal failed for {lineage}; issuing it again through the web root for: {current}").ConfigureAwait(false);
        if (!await helper.IssueAsync(current, lineage, cancellationToken).ConfigureAwait(false))
            await context.Log($"WARNING: renewal failed for {lineage}; the check below decides.", TaskLogLevel.Warning).ConfigureAwait(false);
    }

    /// <summary>The lines the script's second loop adds to <c>FAILED</c> for one group.</summary>
    private string Verdict(string liveDir, long minDays, string minDaysText, string group)
    {
        var lineage = Lineage(group);
        var certificate = CertificatePath(liveDir, lineage);
        if (!File.Exists(certificate))
            return $"\n  {lineage}: no certificate at {certificate} (or the agent cannot read {liveDir})";
        var failed = NamesAll(certificate, Names(group)) ? string.Empty : $"\n  {lineage}: the certificate does not name every host of '{group}'";
        var left = DaysLeft(certificate);
        if (left is not { } days || days < minDays)
            failed += $"\n  {lineage}: expires in {left?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unreadable number of"} days, fewer than {minDaysText}";
        return failed;
    }

    /// <summary><c>${group%,}</c>: an alias that is empty on purpose leaves one trailing comma.</summary>
    private static string TrimAlias(string group) => group.EndsWith(',') ? group[..^1] : group;

    private static string Lineage(string group) => group.Split(',')[0];

    /// <summary>The group's names as the script's unquoted <c>$names</c> splits them.</summary>
    private static string[] Names(string group) => group.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string CertificatePath(string liveDir, string lineage) => $"{liveDir}/{lineage}/fullchain.pem";

    /// <summary>The script's <c>valid_host</c>: letters, digits, dots and hyphens, at least one dot,
    /// no leading dot or hyphen, no trailing dot, no empty label.</summary>
    internal static bool IsQualifiedHost(string name) =>
        name.Length > 0
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
        && name[0] is not ('.' or '-')
        && !name.EndsWith('.')
        && !name.Contains("..", StringComparison.Ordinal)
        && name.Contains('.', StringComparison.Ordinal);

    private static bool NamesAll(string certificate, IEnumerable<string> names)
    {
        var carried = DnsNames(certificate);
        return names.All(name => carried.Contains(name, StringComparer.Ordinal));
    }

    /// <summary>Whole days before the certificate expires, truncated toward zero as shell arithmetic
    /// does, or null when it cannot be read.</summary>
    private long? DaysLeft(string certificate)
    {
        using var x509 = Read(certificate);
        if (x509 is null)
            return null;
        var end = new DateTimeOffset(x509.NotAfter.ToUniversalTime()).ToUnixTimeSeconds();
        return (end - _time.GetUtcNow().ToUnixTimeSeconds()) / 86400;
    }

    /// <summary>The DNS names of the certificate's subject alternative names, in order.</summary>
    private static IReadOnlyList<string> DnsNames(string certificate)
    {
        using var x509 = Read(certificate);
        var extension = x509?.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        return extension is null ? [] : [.. extension.EnumerateDnsNames()];
    }

    /// <summary>The first certificate of the PEM file, as <c>openssl x509 -in</c> reads it, or null.</summary>
    private static X509Certificate2? Read(string path)
    {
        try
        {
            var pem = File.ReadAllText(path);
            if (!PemEncoding.TryFind(pem, out var fields) || !pem[fields.Label].SequenceEqual("CERTIFICATE"))
                return null;
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pem[fields.Base64Data].ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            // Mandatory: an unreadable certificate is an answer here (renew, then report), not a crash.
            return null;
        }
    }

    /// <summary>The agent's root-owned certbot helpers, run as <c>sudo -n &lt;helper&gt; ...</c>.</summary>
    private sealed class CertbotHelpers(IPipelineFunctionTools tools, PipelineFunctionContext context, string helpers, string email)
    {
        /// <summary><c>aetheus-certbot-issue &lt;lineage&gt;</c> for those domains; whether it succeeded.</summary>
        public Task<bool> IssueAsync(string domains, string lineage, CancellationToken cancellationToken)
        {
            var start = FunctionShell.StartInfo(
                context, FunctionShell.Tool(context, "sudo"), context.Workspace ?? Environment.CurrentDirectory,
                "-n", helpers + "/aetheus-certbot-issue", lineage);
            start.Environment["AETHEUS_CERTBOT_DOMAINS"] = domains;
            start.Environment["AETHEUS_CERTBOT_EMAIL"] = email;
            start.Environment["AETHEUS_CERTBOT_MODE"] = "production";
            return RunAsync(start, cancellationToken);
        }

        /// <summary><c>aetheus-certbot-manage renew &lt;lineage&gt;</c>; whether it succeeded.</summary>
        public Task<bool> RenewAsync(string lineage, CancellationToken cancellationToken) =>
            RunAsync(FunctionShell.StartInfo(
                context, FunctionShell.Tool(context, "sudo"), context.Workspace ?? Environment.CurrentDirectory,
                "-n", helpers + "/aetheus-certbot-manage", "renew", lineage), cancellationToken);

        private async Task<bool> RunAsync(System.Diagnostics.ProcessStartInfo start, CancellationToken cancellationToken)
        {
            var result = await tools.RunAsync(start, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && !result.TimedOut;
        }
    }
}
