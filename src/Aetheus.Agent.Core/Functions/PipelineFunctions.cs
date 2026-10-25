// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The pipeline functions this agent build implements. The agent's service registration and the
/// <c>aetheus-fn</c> executable both build their set from here, so a function the agent runs on the
/// host is exactly a function <c>aetheus-fn</c> runs inside a step container.
/// </summary>
public static class PipelineFunctions
{
    /// <summary>A fresh instance of every implemented function.</summary>
    public static IReadOnlyList<IPipelineFunction> CreateAll() =>
    [
        new DetectProjectLanguagesFunction(),
        new EnsureDotnetSdkFunction(),
        new RestoreJavaScriptAnalysisDependenciesFunction(),
        new RunAffectedDotnetFormatFunction(),
        new AppVersionFunction(),
        new DeployIdentityFunction(),
        new VerifyImmutableSourceFunction(),
        new PrepareImageInputsFunction(),
        new EnsureTlsCertificatesFunction(),
        new GrantAcmeChallengeTraversalFunction(),
        new BlueGreenHostResetFunction(),
        new RetainDockerImagesFunction(),
        new PruneQaBuildxCacheFunction(),
        new PruneStaleQaComposeFunction(),
        // Qualification N/N-1 (PLAN-002 lot 1.5).
        new RecordStorageEvidenceFunction(),
        new PersistCompatibilityModeFunction(),
        // PLAN-002 lot 1.6
        new AuthorizeBaselineBootstrapFunction(),
        new ExportBaselineIdentityFunction(),
        new GenerateAssuranceContractFunction(),
        new VerifyAssuranceContractFunction(),
        new VerifyDeliveryPromotionFunction(),
        new GeneratePackageSbomFunction(),
        // PLAN-002 lot 1.8, option c
        new VerifyIdleFrontendFunction(),
        new SettleFastReleaseTransactionFunction(),
        new VerifyProductionStateFunction(),
        new VerifyProductionSecretsFunction(),
        // The heredoc programs of the production, nightly and demo preparations and of the vitrine publication
        new BackfillEnvSecretFunction(),
        new ReconcileEnvUrlsFunction(),
        new RenderEnvSampleFunction(),
        new RenderApacheUpstreamFunction(),
        new PublishVitrineFunction(),
        // PLAN-002 lot 8
        new OpenGrepRuleTestsFunction(),
        // PLAN-002 lot 1.10
        new EnsureNodeRuntimeFunction(),
        new QaStackIdentityFunction(),
        new ExtractAgentReleaseFunction(),
        new VerifySchemaContractFunction(),
        new VerifyNMinusOneAgentContractFunction(),
        new RunQaIntegrationSuiteFunction(),
        new RunQaDeployedProbesFunction(),
        new RunQaE2eSuiteFunction(),
        new RebaseCoverageSourcesFunction(),
        new MergeRoslynSecuritySarifFunction(),
        new PublishFailedTestsFunction(),
        new WriteIntegrationManifestFunction(),
        new GenerateDeliveryContractFunction(),
        new GenerateArtifactProvenanceFunction(),
        new VerifyReleaseAncestryFunction(),
        new PayloadEvidenceFunction(),
        new RecordNightlyQualificationFunction(),
        new VerifyPublicDistributionFunction()
    ];
}
