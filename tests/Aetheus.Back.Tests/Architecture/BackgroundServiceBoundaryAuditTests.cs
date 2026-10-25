// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// A360-35. <c>src/Aetheus.Back/Services/</c> sits outside <c>Components/</c>, so neither
/// <c>NetArchTests</c> nor <c>DependencyCycleAuditTests</c> ever looked at it - both scan
/// <c>Components/</c> only. That blind spot let a generic timeout sweeper accumulate knowledge that
/// belongs to the Pipelines module: which deployment operations are safe to replay after an outage.
///
/// This guard does not attempt to layer <c>Services/</c> (those are hosted services, not a layered
/// graph). It enforces the one boundary the audit actually found broken: a background service may
/// ORCHESTRATE a module, but it must not restate that module's business rules, because the copy in the
/// neighbour is the one nobody updates.
/// </summary>
public sealed class BackgroundServiceBoundaryAuditTests
{
    /// <summary>
    /// Domain vocabulary that only its owning module should reason about. A background service naming
    /// these is deciding something the module should decide.
    /// </summary>
    private static readonly string[] DeploymentOperationTokens =
    [
        "OperationKind.BlueGreenMigrate",
        "OperationKind.BlueGreenUp",
        "OperationKind.BlueGreenSwitch",
        "OperationKind.BlueGreenCommit",
        "OperationKind.BlueGreenRollback",
        "OperationKind.PipelineDeploy",
        "OperationKind.PipelineCreateRelease"
    ];

    [Fact]
    public void NoBackgroundService_RestatesTheDeploymentReplayPolicy()
    {
        var violations = new List<string>();

        foreach (var file in ScannedFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var trimmed = line.TrimStart();
                // A comment may legitimately explain the rule; only code may not restate it.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)) continue;

                foreach (var token in DeploymentOperationTokens)
                    if (line.Contains(token, StringComparison.Ordinal))
                        violations.Add($"{Path.GetFileName(file)}:{index + 1} names {token}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "A background service is restating the Pipelines module's deployment "
            + "policy. Ask the module instead (PipelineOutageReplayPolicy), so a new deployment "
            + "operation does not have to be remembered in two places:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations));
    }

    [Fact]
    public void EveryHostedService_IsInTheScannedFiles()
    {
        // The scan finds hosted services by their declaration, wherever they live under the back end
        // (they sit in Services/ and in the modules they sweep). This proves the declaration match
        // misses none: each concrete hosted service of the assembly must come from a scanned file.
        var scannedText = ScannedFiles().Select(File.ReadAllText).ToList();
        var hostedServices = typeof(Aetheus.Back.Components.Servers.MetricsCleanupService).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.IsNested
                && typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToList();

        Assert.True(hostedServices.Count >= 5, $"Only found {hostedServices.Count} hosted service(s) - likely a discovery bug.");
        var missed = hostedServices
            .Where(name => !scannedText.Any(text => System.Text.RegularExpressions.Regex.IsMatch(
                text, @"\bclass\s+" + name + @"\b")))
            .ToList();
        Assert.True(missed.Count == 0,
            "These hosted services are declared in a file the boundary guard does not scan: "
            + string.Join(", ", missed));
    }

    /// <summary>
    /// Every file of <c>Services/</c> (the guard's original scope), plus every other file of the back end
    /// that declares a hosted service: a service moved into, or added to, a module stays covered.
    /// </summary>
    private static IReadOnlyList<string> ScannedFiles()
    {
        var backDirectory = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back");
        var servicesDirectory = Path.Combine(backDirectory, "Services");
        var hostedServiceDeclaration = new System.Text.RegularExpressions.Regex(
            @"\bclass\s+\w+[^{;]*\b\w*(BackgroundService|HostedService|HostedLifecycleService)\b");

        var hosted = RepositoryScan.Enumerate(backDirectory, "*.cs")
            .Where(file => hostedServiceDeclaration.IsMatch(File.ReadAllText(file)));
        return RepositoryScan.Enumerate(servicesDirectory, "*.cs")
            .Concat(hosted)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Fact]
    public void TheReplayPolicy_StillRefusesEveryDeploymentOperation()
    {
        // Moving the rule must not have softened it: the point of the list is that a half-applied
        // deployment is never replayed blind after an outage.
        Assert.False(Aetheus.Back.Components.Pipelines.PipelineOutageReplayPolicy
            .MayReplayAfterAnOutage(Aetheus.Shared.Components.Tasks.OperationKind.BlueGreenSwitch));
        Assert.False(Aetheus.Back.Components.Pipelines.PipelineOutageReplayPolicy
            .MayReplayAfterAnOutage(Aetheus.Shared.Components.Tasks.OperationKind.PipelineDeploy));
        // And ordinary work is still replayable, or the stand-by feature would be pointless.
        Assert.True(Aetheus.Back.Components.Pipelines.PipelineOutageReplayPolicy
            .MayReplayAfterAnOutage(Aetheus.Shared.Components.Tasks.OperationKind.None));
    }
}
