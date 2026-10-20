// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/ensure-tls-certificates.sh</c>, run through the real executor as a
/// deployment function on real PEM certificates and a fixed clock, with the certbot helpers answered by
/// a fake that issues or renews like the real ones: issue when missing or incomplete, renew when it
/// expires too soon, issue again with the carried names when renewal fails, and a final verdict over
/// every lineage.
/// </summary>
public sealed class EnsureTlsCertificatesFunctionTests : IDisposable
{
    private const string Helpers = "/opt/aetheus/helpers";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly FunctionTestHarness _harness = new("aetheus-tls");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly string _live;

    public EnsureTlsCertificatesFunctionTests()
    {
        _live = Directory.CreateDirectory(Path.Combine(_harness.Root, "live")).FullName;
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["TLS_LIVE_DIR"] = _live;
        _env["AGENT_HELPERS_DIR"] = Helpers;
        _env["AETHEUS_CERTBOT_EMAIL"] = "ops@example.org";
        _tools.OnRun = Helper;
    }

    public void Dispose() => _harness.Dispose();

    private bool IssueSucceeds { get; set; } = true;
    private bool RenewSucceeds { get; set; } = true;
    private List<(string Verb, string Lineage, string? Domains)> HelperCalls { get; } = [];

    private Task<(int, string[])> Helper(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = process.ArgumentList.ToArray();
        Assert.Equal("-n", arguments[0]);
        if (arguments is [_, Helpers + "/aetheus-certbot-issue", var lineage])
        {
            Assert.Equal("ops@example.org", process.Environment["AETHEUS_CERTBOT_EMAIL"]);
            Assert.Equal("production", process.Environment["AETHEUS_CERTBOT_MODE"]);
            var domains = process.Environment["AETHEUS_CERTBOT_DOMAINS"]!;
            HelperCalls.Add(("issue", lineage, domains));
            if (IssueSucceeds)
                WriteCertificate(lineage, 90, domains.Split(','));
            return Task.FromResult(IssueSucceeds ? (0, new[] { "issued" }) : (1, new[] { "ACME refused" }));
        }
        if (arguments is [_, Helpers + "/aetheus-certbot-manage", "renew", var renewed])
        {
            HelperCalls.Add(("renew", renewed, null));
            if (RenewSucceeds)
                WriteCertificate(renewed, 90, [.. DnsNamesOf(renewed)]);
            return Task.FromResult(RenewSucceeds ? (0, new[] { "renewed" }) : (1, new[] { "plugin wedged" }));
        }
        return Task.FromResult((127, Array.Empty<string>()));
    }

    private void WriteCertificate(string lineage, int days, params string[] names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + names[0], key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
            san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(days).AddHours(12));
        var directory = Directory.CreateDirectory(Path.Combine(_live, lineage)).FullName;
        File.WriteAllText(Path.Combine(directory, "fullchain.pem"), certificate.ExportCertificatePem() + "\n" + certificate.ExportCertificatePem() + "\n");
    }

    private IEnumerable<string> DnsNamesOf(string lineage)
    {
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_live, lineage, "fullchain.pem")));
        return [.. certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames()];
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new EnsureTlsCertificatesFunction(_tools, new FakeTimeProvider(Now)), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        const string prefix = "Function 'ensure-tls-certificates' failed: ";
        Assert.StartsWith(prefix, result.FailureReason);
        return result.FailureReason![prefix.Length..];
    }

    private List<string> Lines(TaskLogLevel? level = null) =>
        [.. _harness.Log.Where(entry => level is null || entry.Level == level).Select(entry => entry.Line)];

    [Fact]
    public async Task ValidCertificates_AreOnlyChecked()
    {
        WriteCertificate("app.example.org", 60, "app.example.org");
        WriteCertificate("api.example.org", 45, "api.example.org", "api-old.example.org");
        _parameters["hosts"] = "app.example.org api.example.org,api-old.example.org";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(HelperCalls);
        Assert.Contains(">>> app.example.org: valid for 60 more days", Lines());
        Assert.Contains(">>> api.example.org: valid for 45 more days", Lines());
        Assert.Contains(">>> Every certificate is valid for at least 30 more days: app.example.org api.example.org,api-old.example.org", Lines());
    }

    [Fact]
    public async Task AMissingCertificate_IsIssuedForTheWholeGroup()
    {
        _parameters["hosts"] = "api.example.org,api-old.example.org";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([("issue", "api.example.org", "api.example.org,api-old.example.org")], HelperCalls);
        Assert.Contains(">>> Issuing the certificate for api.example.org,api-old.example.org", Lines());
    }

    [Fact]
    public async Task ATrailingCommaAliasIsIgnored()
    {
        WriteCertificate("api.example.org", 60, "api.example.org");
        _parameters["hosts"] = "api.example.org,";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(HelperCalls);
        Assert.Contains(">>> Every certificate is valid for at least 30 more days: api.example.org,", Lines());
    }

    [Fact]
    public async Task ACertificateMissingAName_IsIssuedAgain()
    {
        WriteCertificate("api.example.org", 60, "api.example.org");
        _parameters["hosts"] = "api.example.org,api2.example.org";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([("issue", "api.example.org", "api.example.org,api2.example.org")], HelperCalls);
    }

    [Fact]
    public async Task AnExpiringCertificate_IsRenewed()
    {
        WriteCertificate("app.example.org", 10, "app.example.org");
        _parameters["hosts"] = "app.example.org";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([("renew", "app.example.org", (string?)null)], HelperCalls);
        Assert.Contains(">>> The certificate for app.example.org expires in 10 days; renewing", Lines());
    }

    [Fact]
    public async Task AFailedRenewal_IssuesAgainWithExactlyTheNamesItCarries()
    {
        WriteCertificate("example.org", 5, "example.org", "www.example.org", "docs.example.org");
        _parameters["hosts"] = "example.org";
        RenewSucceeds = false;

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [("renew", "example.org", null), ("issue", "example.org", "example.org,www.example.org,docs.example.org")],
            HelperCalls);
        Assert.Contains(">>> Renewal failed for example.org; issuing it again through the web root for: example.org,www.example.org,docs.example.org", Lines());
    }

    [Fact]
    public async Task EveryLineageIsAttemptedThenEachOneNotInOrderIsReported()
    {
        WriteCertificate("old.example.org", 3, "old.example.org");
        _parameters["hosts"] = "new.example.org old.example.org";
        _parameters["min_days"] = "20";
        IssueSucceeds = false;
        RenewSucceeds = false;

        var failure = await FailureAsync();

        Assert.Equal(
            "TLS is not in order after issuance and renewal:"
            + $"\n  new.example.org: no certificate at {_live}/new.example.org/fullchain.pem (or the agent cannot read {_live})"
            + "\n  old.example.org: expires in 3 days, fewer than 20",
            failure);
        Assert.Equal(["issue", "renew", "issue"], HelperCalls.Select(call => call.Verb));
        Assert.Contains("WARNING: issuance failed for new.example.org; the check below decides.", Lines(TaskLogLevel.Warning));
        Assert.Contains("WARNING: renewal failed for old.example.org; the check below decides.", Lines(TaskLogLevel.Warning));
    }

    [Fact]
    public async Task AnUnreadableCertificate_IsIssuedThenReported()
    {
        _harness.Write("live/app.example.org/fullchain.pem", "not a certificate");
        _parameters["hosts"] = "app.example.org";
        IssueSucceeds = false;

        var failure = await FailureAsync();

        Assert.Contains("app.example.org: the certificate does not name every host of 'app.example.org'", failure, StringComparison.Ordinal);
        Assert.Contains("app.example.org: expires in an unreadable number of days, fewer than 30", failure, StringComparison.Ordinal);
        Assert.Contains("WARNING: issuance failed", string.Join('\n', Lines()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", "Not a fully qualified hostname: 'localhost' in 'localhost'")]
    [InlineData("a.example.org,-b.example.org", "Not a fully qualified hostname: '-b.example.org' in 'a.example.org,-b.example.org'")]
    [InlineData("a..example.org", "Not a fully qualified hostname: 'a..example.org' in 'a..example.org'")]
    [InlineData("a.example.org.", "Not a fully qualified hostname: 'a.example.org.' in 'a.example.org.'")]
    [InlineData("a_b.example.org", "Not a fully qualified hostname: 'a_b.example.org' in 'a_b.example.org'")]
    public async Task AHostThatIsNotFullyQualified_IsRefused(string hosts, string message)
    {
        _parameters["hosts"] = hosts;

        Assert.Equal(message, await FailureAsync());
        Assert.Empty(HelperCalls);
    }

    [Theory]
    [InlineData("min_days", "thirty", "TLS_MIN_DAYS must be a number of days: thirty")]
    [InlineData("helpers_dir", "/opt/../tmp", "AGENT_HELPERS_DIR must not contain a traversal: /opt/../tmp")]
    [InlineData("helpers_dir", "helpers", "AGENT_HELPERS_DIR must be an absolute path: helpers")]
    public async Task InvalidSettings_AreRefusedBeforeAnyHelper(string parameter, string value, string message)
    {
        _parameters["hosts"] = "app.example.org";
        _parameters[parameter] = value;

        Assert.Equal(message, await FailureAsync());
        Assert.Empty(HelperCalls);
    }

    [Fact]
    public void EnsureTlsCertificates_IsADeploymentFunction() =>
        Assert.Equal(OperationKind.PipelineDeploymentFunction, PipelineFunctionCatalog.EnsureTlsCertificates.Operation);
}
