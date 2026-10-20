// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class AgentProtocolCompatibilityArchitectureTests
{
    [Fact]
    public void CompatibilityWindow_AcceptsOnlyCurrentProtocol()
    {
        Assert.Equal(AgentProtocol.CurrentVersion, AgentProtocol.MinimumSupportedVersion);
        Assert.Equal(AgentProtocol.CurrentVersion, AgentProtocol.MaximumSupportedVersion);
        Assert.True(AgentProtocol.IsSupported(AgentProtocol.CurrentVersion));
        Assert.False(AgentProtocol.IsSupported(AgentProtocol.CurrentVersion - 1));
        Assert.False(AgentProtocol.IsSupported(AgentProtocol.CurrentVersion + 1));
    }

    [Fact]
    public void Heartbeat_KeepsCurrentCompatibilitySignals()
    {
        var heartbeat = typeof(ServerHeartbeatDto);

        Assert.NotNull(heartbeat.GetProperty(nameof(ServerHeartbeatDto.DockerAvailable)));
        Assert.NotNull(heartbeat.GetProperty(nameof(ServerHeartbeatDto.PipelineRunnerAvailable)));
        Assert.NotNull(heartbeat.GetProperty(nameof(ServerHeartbeatDto.SudoersHashes)));
        Assert.Equal(
            typeof(int?),
            heartbeat.GetProperty(nameof(ServerHeartbeatDto.AgentProtocolVersion))!.PropertyType);
        Assert.Equal(
            typeof(List<string>),
            Nullable.GetUnderlyingType(
                heartbeat.GetProperty(nameof(ServerHeartbeatDto.AgentCapabilities))!.PropertyType)
            ?? heartbeat.GetProperty(nameof(ServerHeartbeatDto.AgentCapabilities))!.PropertyType);
    }

    [Fact]
    public void EveryOperationCapability_IsAStableCatalogValue()
    {
        var catalog = typeof(AgentCapabilities)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var operation in Enum.GetValues<Aetheus.Shared.Components.Tasks.OperationKind>())
        {
            var required = AgentCapabilities.RequiredFor(operation);
            Assert.True(required is not null, $"{operation} has no required agent capability.");
            Assert.Contains(required, catalog);
        }
    }

    [Fact]
    public void AiRun_RequiresTheCapabilityPublishedByCurrentAgents()
    {
        Assert.Equal(
            AgentCapabilities.AiExecution,
            AgentCapabilities.RequiredFor(Aetheus.Shared.Components.Tasks.OperationKind.AiRun));
        Assert.Contains(AgentCapabilities.AiExecution, AgentCapabilities.SoftwareCapabilities);
    }

    /// <summary>The retained N-1 agent is extracted and exercised by the <c>extract-agent-release</c>
    /// and <c>verify-nminus1-agent-contract</c> functions (ports of the deleted
    /// <c>extract-agent-release-from-image.sh</c> and <c>verify-nminus1-agent-contract.sh</c>), and the
    /// live manifest generator still declares the protocol 1-2 bridge. Their behaviour is tested in
    /// <c>ExtractAgentReleaseFunctionTests</c> and <c>VerifyNMinusOneAgentContractFunctionTests</c>; this
    /// keeps the checks themselves from being removed.</summary>
    [Fact]
    public void ReleaseScripts_RetainAndExerciseAnImmutableNMinusOneAgentArtifact()
    {
        var root = FindRepoRoot();
        string Function(string name) => File.ReadAllText(
            Path.Combine(root, "src", "Aetheus.Agent.Core", "Functions", name));
        var manifest = Function("AgentReleaseManifest.cs");
        var extract = Function("AgentReleaseManifestCheck.cs");
        var proof = Function("VerifyNMinusOneAgentContractFunction.cs");
        var session = Function("AgentContractSession.cs");
        var generateShell = File.ReadAllText(
            Path.Combine(root, "deploy", "scripts", "generate-agent-release-manifest.sh"));

        Assert.Contains("FileName = \"agent-release-manifest.json\"", manifest, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", extract, StringComparison.Ordinal);
        Assert.Contains("Number(manifest, \"protocolVersion\") != 2", extract, StringComparison.Ordinal);
        Assert.Contains("protocolPolicy == \"upgrade-bridge\"", extract, StringComparison.Ordinal);
        Assert.Contains("protocolPolicy == \"historical-compatible\"", extract, StringComparison.Ordinal);
        Assert.Contains("minimum == 1 && maximum == 2", extract, StringComparison.Ordinal);
        Assert.Contains("minimum == 2 && maximum == 2", extract, StringComparison.Ordinal);
        Assert.Contains("IsNumber(root, \"protocolVersion\", 2)", proof, StringComparison.Ordinal);
        Assert.Contains("'  \"minimumSupportedProtocol\": 1,'", generateShell, StringComparison.Ordinal);
        Assert.Contains("did not enroll and heartbeat", session, StringComparison.Ordinal);
        Assert.Contains("claim/start/complete proof", session, StringComparison.Ordinal);
        Assert.Contains("result publication", proof, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
