// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;

namespace Aetheus.Back.Tests.Pipelines;

public sealed class CandidateAssuranceContractTests : IDisposable
{
    private const string SourceSha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-assurance-{Guid.NewGuid():N}");

    [Fact]
    public async Task GradeF_RemainsVisibleButCannotSatisfyDeploymentReadiness()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "C", securityGrade: "F", dynamicGrade: "B");

        var generated = await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            new Dictionary<string, string>
            {
                ["AETHEUS_ASSURANCE_PERFORMANCE_ENABLED"] = "false",
                ["UNIT_TEST_GATE_STATUS"] = "0",
                ["ANALYZER_TEST_GATE_STATUS"] = "1",
                ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "0",
                ["QA_CURRENT_E2E_GATE_STATUS"] = "0",
                ["QA_AGENT_COMPATIBILITY_GATE_STATUS"] = "0",
                ["QA_PREVIOUS_INTEGRATION_GATE_STATUS"] = "0",
                ["QA_PREVIOUS_E2E_GATE_STATUS"] = "0",
                ["QA_PREVIOUS_SMOKE_GATE_STATUS"] = "0",
                ["COMPATIBILITY_MODE"] = "NMinusOne",
                ["PREVIOUS_VERSION_TESTED"] = SourceSha,
                ["AgentCompatibilityMode"] = "NMinusOne",
                ["PreviousAgentTested"] = SourceSha
            });

        Assert.Equal(0, generated.ExitCode);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(
            ContractPath, TestContext.Current.CancellationToken));
        Assert.Equal("F", contract.RootElement.GetProperty("overallGrade").GetString());
        var analyzers = contract.RootElement.GetProperty("tests").GetProperty("analyzers");
        Assert.Equal("Failed", analyzers.GetProperty("status").GetString());
        Assert.Equal("F", analyzers.GetProperty("effectiveGrade").GetString());
        Assert.Equal("F", contract.RootElement.GetProperty("analyses").GetProperty("security")
            .GetProperty("reportedGrade").GetString());
        Assert.Equal("sha256", contract.RootElement.GetProperty("seal").GetProperty("algorithm").GetString());
        Assert.Equal(64, contract.RootElement.GetProperty("seal").GetProperty("digest").GetString()!.Length);

        var atF = await VerifyAsync(node, "F");
        Assert.Equal(0, atF.ExitCode);
        Assert.Contains("grade F verified", atF.StdOut, StringComparison.Ordinal);

        var evidence = await VerifyAsync(node, "F", requireEvidence: true);
        Assert.Equal(0, evidence.ExitCode);

        var readiness = await VerifyAsync(node, "F", requireDeploymentReadiness: true);
        Assert.NotEqual(0, readiness.ExitCode);
        Assert.Contains("requires analyzers to be Passed", readiness.StdErr, StringComparison.Ordinal);

        var atE = await VerifyAsync(node, "E");
        Assert.NotEqual(0, atE.ExitCode);
        Assert.Contains("below deployment threshold E", atE.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteOrMalformedAnalysis_IsConservativelyGradedF()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: null, securityGrade: "A", dynamicGrade: "A");
        var environment = PassingEnvironment();

        var generated = await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            environment);

        Assert.Equal(0, generated.ExitCode);
        using (var contract = JsonDocument.Parse(await File.ReadAllTextAsync(
                   ContractPath, TestContext.Current.CancellationToken)))
        {
            var quality = contract.RootElement.GetProperty("analyses").GetProperty("quality");
            Assert.Equal("Unavailable", quality.GetProperty("status").GetString());
            Assert.Equal("F", quality.GetProperty("effectiveGrade").GetString());
        }
        Assert.Equal(0, (await VerifyAsync(node, "F")).ExitCode);
        var incompleteEvidence = await VerifyAsync(node, "F", requireEvidence: true);
        Assert.NotEqual(0, incompleteEvidence.ExitCode);
        Assert.Contains("requires available quality evidence", incompleteEvidence.StdErr, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            Path.Combine(SummaryDirectory("quality"), "quality-summary.json"),
            "{invalid-json",
            TestContext.Current.CancellationToken);
        var invalid = await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            environment);
        Assert.Equal(0, invalid.ExitCode);
        using var invalidContract = JsonDocument.Parse(await File.ReadAllTextAsync(
            ContractPath, TestContext.Current.CancellationToken));
        var unavailable = invalidContract.RootElement.GetProperty("analyses").GetProperty("quality");
        Assert.Equal("Unavailable", unavailable.GetProperty("status").GetString());
        Assert.Equal("summary-invalid-json", unavailable.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("pipelineRunId").ValueKind);
        Assert.Equal(0, (await VerifyAsync(node, "F")).ExitCode);
        var invalidEvidence = await VerifyAsync(node, "F", requireEvidence: true);
        Assert.NotEqual(0, invalidEvidence.ExitCode);
        Assert.Contains("requires available quality evidence", invalidEvidence.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentReadiness_RequiresEveryAnalysisAndRequiredTestToBeUsable()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "B", securityGrade: "C", dynamicGrade: "B");
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment())).ExitCode);

        var readiness = await VerifyAsync(node, "F", requireDeploymentReadiness: true);

        Assert.Equal(0, readiness.ExitCode);
        Assert.Contains("deployment readiness verified", readiness.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlteredContract_IsRejectedWhenItsSealNoLongerMatches()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "B", securityGrade: "C", dynamicGrade: "B");
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment())).ExitCode);

        var contract = await File.ReadAllTextAsync(ContractPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            ContractPath,
            contract.Replace("\"overallGrade\": \"C\"", "\"overallGrade\": \"A\"", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var verification = await VerifyAsync(node, "F");

        Assert.NotEqual(0, verification.ExitCode);
        Assert.Contains("seal does not match", verification.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlteredEvidence_IsRejectedEvenWhenTheContractSealStillMatches()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "B", securityGrade: "C", dynamicGrade: "B");
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment())).ExitCode);
        await File.AppendAllTextAsync(
            Path.Combine(EvidenceDirectory, "quality-summary.json"),
            " ",
            TestContext.Current.CancellationToken);

        var verification = await VerifyAsync(node, "F");

        Assert.NotEqual(0, verification.ExitCode);
        Assert.Contains("evidence hash does not match", verification.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapCompatibility_RequiresExplicitCandidateAuthorization()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "B", securityGrade: "C", dynamicGrade: "B");
        var environment = PassingEnvironment();
        environment["COMPATIBILITY_MODE"] = "Bootstrap";
        environment["PREVIOUS_VERSION_TESTED"] = "false";
        environment["AgentCompatibilityMode"] = "Bootstrap";
        environment["PreviousAgentTested"] = "false";
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            environment)).ExitCode);

        var refused = await VerifyAsync(node, "F", requireDeploymentReadiness: true);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("without explicit candidate authorization", refused.StdErr, StringComparison.Ordinal);

        environment["AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP"] = "true";
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            environment)).ExitCode);
        var authorized = await VerifyAsync(node, "F", requireDeploymentReadiness: true);
        Assert.Equal(0, authorized.ExitCode);
    }

    [Fact]
    public async Task ErrorGate_WithCompleteGrade_IsUnavailableForDeploymentReadiness()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "B", securityGrade: "C", dynamicGrade: "B");
        PrepareSummary("quality", "quality-summary.json", "B", status: "Error");

        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment())).ExitCode);

        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(
            ContractPath, TestContext.Current.CancellationToken));
        var quality = contract.RootElement.GetProperty("analyses").GetProperty("quality");
        Assert.Equal("Unavailable", quality.GetProperty("status").GetString());
        Assert.Equal("gate-error-or-blocked", quality.GetProperty("reason").GetString());
        var readiness = await VerifyAsync(node, "F", requireDeploymentReadiness: true);
        Assert.NotEqual(0, readiness.ExitCode);
        Assert.Contains("requires available quality evidence", readiness.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalysisFromAnotherCommit_RemainsBlockedAsAProvenanceViolation()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "A", securityGrade: "A", dynamicGrade: "A");
        PrepareSummary("quality", "quality-summary.json", "A", commitHash: new string('f', 40));

        var generated = await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment());

        Assert.NotEqual(0, generated.ExitCode);
        Assert.Contains("belongs to another commit", generated.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAnalysisSummaries_ProduceVerifiableUnavailableGradeF()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "A", securityGrade: "A", dynamicGrade: "A");
        Directory.Delete(SummaryDirectory("quality"), recursive: true);
        Directory.Delete(SummaryDirectory("security"), recursive: true);
        Directory.Delete(SummaryDirectory("qa"), recursive: true);

        var generated = await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            PassingEnvironment());

        Assert.Equal(0, generated.ExitCode);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(
            ContractPath, TestContext.Current.CancellationToken));
        Assert.Equal("F", contract.RootElement.GetProperty("overallGrade").GetString());
        foreach (var analysis in contract.RootElement.GetProperty("analyses").EnumerateObject())
        {
            Assert.Equal("Unavailable", analysis.Value.GetProperty("status").GetString());
            Assert.Equal("summary-missing", analysis.Value.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, analysis.Value.GetProperty("pipelineRunId").ValueKind);
        }
        Assert.Equal(0, (await VerifyAsync(node, "F")).ExitCode);
        Assert.NotEqual(0, (await VerifyAsync(node, "E")).ExitCode);
        var evidence = await VerifyAsync(node, "F", requireEvidence: true);
        Assert.NotEqual(0, evidence.ExitCode);
        Assert.Contains("requires available quality evidence", evidence.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredTestOutput_IsRejectedByCandidateEvidenceBoundary()
    {
        var node = RequireNode();
        PrepareSummaries(qualityGrade: "A", securityGrade: "A", dynamicGrade: "A");
        var environment = PassingEnvironment();
        environment.Remove("ANALYZER_TEST_GATE_STATUS");
        Assert.Equal(0, (await RunNodeAsync(
            node,
            Script("generate-candidate-assurance-contract.mjs"),
            Arguments(),
            environment)).ExitCode);

        var evidence = await VerifyAsync(node, "F", requireEvidence: true);

        Assert.NotEqual(0, evidence.ExitCode);
        Assert.Contains("requires available analyzers evidence", evidence.StdErr, StringComparison.Ordinal);
    }

    private Dictionary<string, string> PassingEnvironment() => new()
    {
        ["AETHEUS_ASSURANCE_PERFORMANCE_ENABLED"] = "false",
        ["UNIT_TEST_GATE_STATUS"] = "0",
        ["ANALYZER_TEST_GATE_STATUS"] = "0",
        ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "0",
        ["QA_CURRENT_E2E_GATE_STATUS"] = "0",
        ["QA_AGENT_COMPATIBILITY_GATE_STATUS"] = "0",
        ["QA_PREVIOUS_INTEGRATION_GATE_STATUS"] = "0",
        ["QA_PREVIOUS_E2E_GATE_STATUS"] = "0",
        ["QA_PREVIOUS_SMOKE_GATE_STATUS"] = "0",
        ["COMPATIBILITY_MODE"] = "NMinusOne",
        ["PREVIOUS_VERSION_TESTED"] = SourceSha,
        ["AgentCompatibilityMode"] = "NMinusOne",
        ["PreviousAgentTested"] = SourceSha
    };

    private void PrepareSummaries(string? qualityGrade, string? securityGrade, string? dynamicGrade)
    {
        PrepareSummary("quality", "quality-summary.json", qualityGrade);
        PrepareSummary("security", "security-summary.json", securityGrade);
        PrepareSummary("qa", "security-summary.json", dynamicGrade);
        Directory.CreateDirectory(Path.GetDirectoryName(ProvenancePath)!);
        File.WriteAllText(ProvenancePath, JsonSerializer.Serialize(new
        {
            schema = 1,
            sourceCommit = SourceSha,
            categories = new { dockerImages = new { fileCount = 2, totalBytes = 42, sha256 = new string('a', 64) } }
        }));
    }

    private string[] Arguments() =>
        [ContractPath, EvidenceDirectory, SourceSha, SummaryDirectory("quality"), SummaryDirectory("security"), SummaryDirectory("qa"), ProvenancePath];

    private void PrepareSummary(
        string directory,
        string fileName,
        string? grade,
        string? commitHash = null,
        string? status = null)
    {
        var target = SummaryDirectory(directory);
        Directory.CreateDirectory(target);
        var summary = new
        {
            pipelineRunId = directory switch { "quality" => 11, "security" => 12, _ => 13 },
            status = status ?? (grade is null ? "Error" : "Warning"),
            grade = new
            {
                overallGrade = grade,
                completeness = grade is null ? "Incomplete" : "Complete",
                commitHash = commitHash ?? SourceSha,
                pipelineRunId = directory switch { "quality" => 11, "security" => 12, _ => 13 }
            }
        };
        File.WriteAllText(Path.Combine(target, fileName), JsonSerializer.Serialize(summary));
    }

    private Task<ProcessResult> VerifyAsync(
        string node,
        string threshold,
        bool requireDeploymentReadiness = false,
        bool requireEvidence = false) => RunNodeAsync(
            node,
            Script("verify-candidate-assurance-contract.mjs"),
            requireDeploymentReadiness
                ? [ContractPath, SourceCommitPath, threshold, "--require-deployment-readiness"]
                : requireEvidence
                    ? [ContractPath, SourceCommitPath, threshold, "--require-evidence"]
                : [ContractPath, SourceCommitPath, threshold],
            new Dictionary<string, string>());

    private async Task<ProcessResult> RunNodeAsync(
        string node,
        string script,
        IReadOnlyCollection<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SourceCommitPath)!);
        await File.WriteAllTextAsync(SourceCommitPath, SourceSha, TestContext.Current.CancellationToken);
        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) startInfo.Environment[name] = value;
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private string ContractPath => Path.Combine(_root, "artifact", ".pipeline-artifacts", "assurance-contract.json");
    private string EvidenceDirectory => Path.Combine(_root, "artifact", ".pipeline-artifacts", "assurance");
    private string SourceCommitPath => Path.Combine(_root, "artifact", ".pipeline-artifacts", "source-commit");
    private string ProvenancePath => Path.Combine(_root, "input", "ci", "artifact-provenance.json");
    private string SummaryDirectory(string name) => Path.Combine(_root, "input", name);
    private static string Script(string name) => Path.Combine(FindRepoRoot(), "deploy", "pipelines", "toto-conformance-fixture", "common", "deploy", "scripts", name);

    // node is what the candidate pipeline itself runs to seal its assurance contract, so a host
    // without it cannot exercise these contracts at all - fail instead of skipping.
    private static string RequireNode() => ExecutableLocator.Require(
        "node",
        OperatingSystem.IsWindows()
            ? @"C:\Program Files\nodejs\node.exe"
            : "/usr/local/bin/node");

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
