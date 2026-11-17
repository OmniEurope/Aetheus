// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Recette R-21: the lines the gate task writes, in its log and in its summary, for each reason of an Error verdict:
/// the producer (tool), the step, the report and its file, then the precise reason. Run 3100 of the nightly logged "Error,
/// 9 reports, 0 findings, grade A" and nothing else.</summary>
internal static class AnalysisGateErrorCauseText
{
    /// <summary>Said when the server sent an Error verdict with no cause (a server older than this contract).</summary>
    public const string NoneProvided = "le serveur n'a transmis aucune cause pour ce verdict en erreur.";

    public static string Line(AnalysisGateErrorCauseDto cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        var parts = new List<string>();
        var tool = cause.ScannerName is { Length: > 0 } name && !string.Equals(name, cause.ScannerKey, StringComparison.OrdinalIgnoreCase)
            ? $"{name} ({cause.ScannerKey})"
            : cause.ScannerKey ?? cause.ScannerName;
        if (!string.IsNullOrWhiteSpace(tool)) parts.Add($"producteur {tool}");
        if (!string.IsNullOrWhiteSpace(cause.StepName)) parts.Add($"étape « {cause.StepName} »");
        if (cause.ReportId is { } report) parts.Add($"rapport #{report.ToString(CultureInfo.InvariantCulture)}");
        if (cause.PipelineArtifactId is { } artifact) parts.Add($"artefact #{artifact.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(cause.ReportPath)) parts.Add($"fichier {cause.ReportPath}");
        if (cause.PipelineRunId > 0) parts.Add($"run {cause.PipelineRunId.ToString(CultureInfo.InvariantCulture)}");
        var reason = Reason(cause);
        var line = parts.Count == 0 ? reason : $"{string.Join(", ", parts)} : {reason}";
        return string.IsNullOrWhiteSpace(cause.Detail) || cause.Kind == AnalysisGateErrorCauseKind.DependencyTrack
            ? line
            : $"{line} Détail : {cause.Detail}";
    }

    private static string Reason(AnalysisGateErrorCauseDto cause) => cause.Kind switch
    {
        AnalysisGateErrorCauseKind.NoReport => "aucun rapport n'a été publié pour ce run.",
        AnalysisGateErrorCauseKind.ReportError => "l'outil a terminé son rapport en erreur.",
        AnalysisGateErrorCauseKind.ReportTimedOut => "l'outil a dépassé son délai.",
        AnalysisGateErrorCauseKind.ReportUnavailable => "l'outil ou son environnement d'exécution est indisponible.",
        AnalysisGateErrorCauseKind.NoEvaluation => "le rapport n'a aucune évaluation, son verdict est inconnu.",
        AnalysisGateErrorCauseKind.EvaluationError => "le rapport a été évalué en erreur.",
        AnalysisGateErrorCauseKind.MissingProducer => "la tâche n'a publié aucun rapport.",
        AnalysisGateErrorCauseKind.UnexpectedReport => "rapport inattendu (doublon), aucune tâche ne l'attendait.",
        AnalysisGateErrorCauseKind.DependencyTrack =>
            $"l'envoi du SBOM vers Dependency-Track est exigé et n'est pas terminé (état : {cause.Detail}).",
        AnalysisGateErrorCauseKind.GradeNotComputed => "aucune mesure n'a pu être notée, la note n'existe pas.",
        _ => "cause inconnue."
    };
}
