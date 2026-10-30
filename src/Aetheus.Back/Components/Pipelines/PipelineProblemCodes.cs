// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The codes of <see cref="PipelineProblem"/>. Each one is a contract with the front, which translates it
/// through the resource <c>LaunchProblem_{code}</c> of AppStrings.resx and AppStrings.fr-FR.resx and names
/// the parameters written next to it. A code is never renamed: a front that does not know it shows the
/// English. <c>LaunchProblemLocalizationAuditTests</c> fails when a code used here has no resource.
/// </summary>
internal static class PipelineProblemCodes
{
    // Stage targets and runners. Parameters: stage, and selector / parameter / runners.
    public const string EmptyTargetParameter = "EmptyTargetParameter";
    public const string NoRunnerForStage = "NoRunnerForStage";
    public const string NoDeployTargetForStage = "NoDeployTargetForStage";
    public const string RunnerCannotTakeStage = "RunnerCannotTakeStage";
    public const string DeployTargetCannotTakeStage = "DeployTargetCannotTakeStage";
    public const string EnvironmentCheckRefuses = "EnvironmentCheckRefuses";
    public const string DuplicateDeploymentTarget = "DuplicateDeploymentTarget";

    // Pipelines a definition names.
    public const string StepPipelineMissing = "StepPipelineMissing";
    public const string ArtifactSourcePipelineMissing = "ArtifactSourcePipelineMissing";
    public const string StepPipelineKnownLater = "StepPipelineKnownLater";
    public const string ArtifactSourcePipelineKnownLater = "ArtifactSourcePipelineKnownLater";
    public const string OnSuccessPipelineMissing = "OnSuccessPipelineMissing";
    public const string ChildPipelineNoDefinition = "ChildPipelineNoDefinition";
    public const string ChildPipelineDefinitionUnresolvable = "ChildPipelineDefinitionUnresolvable";
    public const string ChildPipelineVariablesRefused = "ChildPipelineVariablesRefused";

    // Conditions.
    public const string UnsupportedStageCondition = "UnsupportedStageCondition";
    public const string UnsupportedJobCondition = "UnsupportedJobCondition";
    public const string UnsupportedStepCondition = "UnsupportedStepCondition";

    // Configuration templates (R-32, R-82).
    public const string ConfigTemplateUndefinedVariables = "ConfigTemplateUndefinedVariables";
    public const string ConfigTemplateMissingInConfigRepository = "ConfigTemplateMissingInConfigRepository";
    public const string ConfigTemplateMissingAtCommit = "ConfigTemplateMissingAtCommit";
    public const string ConfigTemplateMissingInConfigRepositoryMayRun = "ConfigTemplateMissingInConfigRepositoryMayRun";
    public const string ConfigTemplateMissingAtCommitMayRun = "ConfigTemplateMissingAtCommitMayRun";

    // Retained releases restored by a step.
    public const string ReleaseArtifactInvalidSha256 = "ReleaseArtifactInvalidSha256";
    public const string ReleaseArtifactNotRetained = "ReleaseArtifactNotRetained";
    public const string ReleaseArtifactRequiredByContract = "ReleaseArtifactRequiredByContract";
    public const string ReleaseArtifactPayloadMissing = "ReleaseArtifactPayloadMissing";
    public const string ReleaseVersionArtifactPayloadMissing = "ReleaseVersionArtifactPayloadMissing";

    // The requires: block.
    public const string RequiredLibraryMissing = "RequiredLibraryMissing";
    public const string RequiredVaultMissing = "RequiredVaultMissing";
    public const string RequiredEnvironmentMissing = "RequiredEnvironmentMissing";
    public const string RequiredCapabilityUnknown = "RequiredCapabilityUnknown";
    public const string RequiredDockerMissing = "RequiredDockerMissing";

    // Port registry.
    public const string PortRegisteredToAnother = "PortRegisteredToAnother";
    public const string PortSeenListening = "PortSeenListening";

    // AI steps.
    public const string AiStepNoOrganization = "AiStepNoOrganization";
    public const string AiStepProfileUnusable = "AiStepProfileUnusable";
    public const string AdvisoryAiStepNoOrganization = "AdvisoryAiStepNoOrganization";
    public const string AdvisoryAiStepProfileUnusable = "AdvisoryAiStepProfileUnusable";

    // Scanner manifest.
    public const string ScannerManifestNotReported = "ScannerManifestNotReported";

    // What a launch request carries: its idempotency key and its run parameters (R-113).
    public const string IdempotencyKeyTooLong = "IdempotencyKeyTooLong";
    public const string ParameterReservedName = "ParameterReservedName";
    public const string ParameterUnknown = "ParameterUnknown";
    public const string ParameterRequired = "ParameterRequired";
    public const string ParameterNotNumber = "ParameterNotNumber";
    public const string ParameterNotBoolean = "ParameterNotBoolean";
    public const string ParameterNotInChoices = "ParameterNotInChoices";
    public const string ParameterDeclaredTwice = "ParameterDeclaredTwice";
    public const string ReleaseNumberModeInvalid = "ReleaseNumberModeInvalid";

    // R-72: a deployment launched despite the quality gate.
    public const string GateOverrideNotADeployment = "GateOverrideNotADeployment";
    public const string GateOverrideReleaseUnknown = "GateOverrideReleaseUnknown";
    public const string GateOverrideNoArtifact = "GateOverrideNoArtifact";

    // Pipeline templates a definition extends, and the YAML they and the pipeline are written in.
    public const string TemplateResolvedYamlTooLong = "TemplateResolvedYamlTooLong";
    public const string TemplateInheritanceTooDeep = "TemplateInheritanceTooDeep";
    public const string TemplateNotFound = "TemplateNotFound";
    public const string TemplateCyclic = "TemplateCyclic";
    public const string TemplateVersionMissing = "TemplateVersionMissing";
    public const string TemplateParameterUnresolved = "TemplateParameterUnresolved";
    public const string TemplateReferenceInvalid = "TemplateReferenceInvalid";
    public const string PipelineYamlEmpty = "PipelineYamlEmpty";
    public const string PipelineYamlInvalid = "PipelineYamlInvalid";
    public const string ResolvedPipelineYamlEmpty = "ResolvedPipelineYamlEmpty";
    public const string ResolvedPipelineYamlInvalid = "ResolvedPipelineYamlInvalid";
    public const string TemplateYamlEmpty = "TemplateYamlEmpty";
    public const string TemplateYamlInvalid = "TemplateYamlInvalid";

    // Several problems written as one string (a preflight check that met more than one).
    public const string ProblemsTogether = "ProblemsTogether";

    // Warnings a run keeps (WarningsJson): the front says them in the user's language at display time.
    public const string ChildRunWarning = "ChildRunWarning";
    public const string CheckpointReused = "CheckpointReused";
    public const string AiStepFallback = "AiStepFallback";
    public const string StageConfirmationRejected = "StageConfirmationRejected";
    public const string StageConfirmationTimedOut = "StageConfirmationTimedOut";
    public const string LaunchSetupFailed = "LaunchSetupFailed";
    public const string LaunchRefused = "LaunchRefused";
    public const string LaunchRefusedRepeated = "LaunchRefusedRepeated";
    public const string VariableLibraryNotFound = "VariableLibraryNotFound";
    public const string BlueGreenComposeEnvNotForwarded = "BlueGreenComposeEnvNotForwarded";

    // A run the scheduler stopped.
    public const string RunStoppedStageNotDispatched = "RunStoppedStageNotDispatched";
    public const string RunStoppedStageNotDispatchedCleanupRefused = "RunStoppedStageNotDispatchedCleanupRefused";
    public const string ArtifactCollectionFailed = "ArtifactCollectionFailed";
    public const string ArtifactProducerUnavailable = "ArtifactProducerUnavailable";
    public const string SystemCleanupAwaitsRunner = "SystemCleanupAwaitsRunner";

    // A system step the control plane refused to dispatch. Parameters: step, detail.
    public const string StepFailedRestoreArtifacts = "StepFailedRestoreArtifacts";
    public const string StepFailedRestoreBackup = "StepFailedRestoreBackup";
    public const string StepFailedAdvanceBranch = "StepFailedAdvanceBranch";
    public const string StepFailedDeploy = "StepFailedDeploy";
    public const string StepFailedScanner = "StepFailedScanner";
    public const string StepFailedRelease = "StepFailedRelease";
    public const string StepFailedTrigger = "StepFailedTrigger";
    public const string StepFailedApacheProxy = "StepFailedApacheProxy";
    public const string StepFailedApacheConfig = "StepFailedApacheConfig";
    public const string StepFailedCertbot = "StepFailedCertbot";
    public const string StepFailedSmoke = "StepFailedSmoke";
    public const string StepFailedBlueGreen = "StepFailedBlueGreen";

    // The YAML's unknown properties (advisory).
    public const string UnknownProperty = "UnknownProperty";
    public const string UnknownPropertyAtTopLevel = "UnknownPropertyAtTopLevel";
    public const string UnknownPropertyNearMiss = "UnknownPropertyNearMiss";

    // The on_success follow-ups of a succeeded run, and the chained launch behind them.
    public const string OnSuccessFollowUpNotStarted = "OnSuccessFollowUpNotStarted";
    public const string OnSuccessFollowUpsNotStarted = "OnSuccessFollowUpsNotStarted";
    public const string DownstreamSnapshotUnparsable = "DownstreamSnapshotUnparsable";
    public const string DownstreamNoProject = "DownstreamNoProject";
    public const string DownstreamTargetMissing = "DownstreamTargetMissing";
    public const string DownstreamCycle = "DownstreamCycle";
    public const string DownstreamDepthLimit = "DownstreamDepthLimit";
    public const string DownstreamLaunchRefused = "DownstreamLaunchRefused";
    public const string DownstreamNoReason = "DownstreamNoReason";
    public const string DownstreamNotStarted = "DownstreamNotStarted";
    public const string ChainedPipelineGone = "ChainedPipelineGone";
    public const string ChainedPipelineNoOwner = "ChainedPipelineNoOwner";
    public const string ChainedOwnerLacksServerAdmin = "ChainedOwnerLacksServerAdmin";
    public const string ChainedDefinitionNotPrepared = "ChainedDefinitionNotPrepared";
    public const string ChainedLaunchReturnedNoRun = "ChainedLaunchReturnedNoRun";

    // Variables.
    public const string UnresolvedVariables = "UnresolvedVariables";
    public const string EmptyLibraryEntries = "EmptyLibraryEntries";
    public const string EmptyVaultSecrets = "EmptyVaultSecrets";
    public const string EmptyLibraryEntriesAndVaultSecrets = "EmptyLibraryEntriesAndVaultSecrets";
}
