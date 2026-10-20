// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;

namespace Aetheus.Back.Tests;

/// <summary>What the operator reads when a task can never be claimed by the agent it was queued for.</summary>
public class AgentContractDiagnosisTests
{
    private const string Declared =
        "[\"agent.self-update\",\"pipeline.build\",\"shell.execute\"]";

    private static AgentReleaseManifestDto Release(params string[] capabilities) => new()
    {
        SoftwareVersion = "1.3.16",
        ProtocolVersion = AgentProtocol.CurrentVersion,
        MinimumSupportedProtocol = AgentProtocol.MinimumSupportedVersion,
        MaximumSupportedProtocol = AgentProtocol.MaximumSupportedVersion,
        SoftwareCapabilities = [.. capabilities]
    };

    private static PendingTaskAgentContext Pending(
        OperationKind operation,
        string version = "1.3.15",
        int? protocol = AgentProtocol.CurrentVersion,
        string? capabilities = Declared) =>
        new(7, operation, "vps", version, protocol, capabilities);

    [Fact]
    public void FunctionTask_OnAnAgentWithoutPipelineFunctions_NamesTheAgentTheCapabilityAndTheUpdate()
    {
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.PipelineFunction), Release(AgentCapabilities.PipelineFunctions));

        Assert.NotNull(gap);
        Assert.Equal(TaskFailureCodes.AgentIncompatible, gap.FailureCode);
        Assert.Contains("« vps »", gap.Reason);
        Assert.Contains("1.3.15", gap.Reason);
        Assert.Contains("pipeline.functions", gap.Reason);
        Assert.Contains("mettez-le à jour en 1.3.16", gap.Reason);
        Assert.Contains("Mettre à jour", gap.Reason);
    }

    [Fact]
    public void CapabilityTheAgentDeclares_IsNotADiagnosis()
    {
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.PipelineFunction, capabilities: "[\"pipeline.functions\"]"),
            Release(AgentCapabilities.PipelineFunctions));

        Assert.Null(gap);
    }

    [Fact]
    public void AgentAlreadyOnTheTargetVersion_IsToldToReinstallRatherThanUpdate()
    {
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.PipelineFunction, version: "1.3.16"),
            Release(AgentCapabilities.PipelineFunctions));

        Assert.NotNull(gap);
        Assert.Contains("déjà en version cible", gap.Reason);
        Assert.Contains("réinstallez", gap.Reason);
    }

    [Fact]
    public void GrantCapabilityTheTargetReleaseDoesNotProvide_IsNotAnUpdateRemedy()
    {
        // deployment.apply is granted on the server (sudoers), never shipped with the agent build.
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.PipelineDeploy), Release(AgentCapabilities.PipelineFunctions));

        Assert.NotNull(gap);
        Assert.Contains("deployment.apply", gap.Reason);
        Assert.Contains("droit à accorder", gap.Reason);
        Assert.DoesNotContain("mettez-le à jour", gap.Reason);
    }

    [Theory]
    [InlineData(null, "inconnu")]
    [InlineData(1, "1")]
    public void UnsupportedProtocol_IsStatedEvenWhenTheCapabilityIsDeclared(int? protocol, string shown)
    {
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.None, protocol: protocol),
            Release(AgentCapabilities.ShellExecution));

        Assert.NotNull(gap);
        Assert.Contains($"protocole {shown}", gap.Reason);
        Assert.Contains("Mettez l'agent à jour en 1.3.16", gap.Reason);
    }

    [Fact]
    public void AgentWithoutAVersion_StillProducesAReadableReason()
    {
        var gap = AgentContractDiagnosis.Diagnose(
            Pending(OperationKind.PipelineFunction, version: "", capabilities: null),
            Release(AgentCapabilities.PipelineFunctions));

        Assert.NotNull(gap);
        Assert.Contains("version inconnue", gap.Reason);
    }

    [Fact]
    public void Diagnosis_AgreesWithTheClaimForEveryOperation()
    {
        // The sweep must blame an agent exactly when the claim would skip the task, never more.
        foreach (var operation in Enum.GetValues<OperationKind>())
        {
            var required = AgentCapabilities.RequiredFor(operation)!;
            var allowed = AgentContractDiagnosis.Diagnose(
                Pending(operation, capabilities: $"[\"{required}\"]"), Release(required));
            var refused = AgentContractDiagnosis.Diagnose(
                Pending(operation, capabilities: "[]"), Release(required));

            Assert.Null(allowed);
            Assert.NotNull(refused);
        }
    }
}
