// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Recette R-21: the lines the gate task writes beside an Error verdict's causes (<see cref="AnalysisGateErrorCauseText"/>):
/// each threshold rule that was crossed with its observed value and its limit, the grade under its minimum, the stored
/// evidence the gate could not read, and the reason the step ends with, so the step's error state says why and not only
/// "finished with exit code 1".</summary>
internal static class AnalysisGateDiagnosticText
{
    /// <summary>The server caps a task's failure reason at 512 characters and cuts it without a mark.</summary>
    private const int MaxReasonLength = 480;

    public static string ViolationLine(AnalysisRunGateViolationDto violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        var name = string.IsNullOrWhiteSpace(violation.PolicyName) ? violation.PolicyKey : violation.PolicyName;
        var outcome = violation.Outcome == "Block" ? "blocage" : "avertissement";
        var text = $"Règle violée {name} ({outcome}) : valeur observée {violation.ObservedValue ?? "inconnue"}, "
            + $"condition {violation.Operator ?? "-"} {violation.Threshold ?? "-"}";
        if (!string.IsNullOrWhiteSpace(violation.TargetKind) || !string.IsNullOrWhiteSpace(violation.TargetKey))
            text += $", cible {violation.TargetKind} {violation.TargetKey}".TrimEnd();
        text += $", rapport #{violation.ReportId.ToString(CultureInfo.InvariantCulture)}";
        if (violation.Count > 1) text += $", {violation.Count.ToString(CultureInfo.InvariantCulture)} occurrences";
        return text + ".";
    }

    /// <summary>The line that says why a graded verdict is Blocked by its grade (a letter under the minimum, or an F that
    /// cannot pass), or null when the grade does not block.</summary>
    public static string? GradeBelowMinimumLine(AnalysisGradeSummaryDto? grade)
    {
        if (grade?.OverallGrade is not { } letter) return null;
        var limiting = grade.LimitingDomain is { } domain ? $", domaine limitant {domain}" : string.Empty;
        if (grade.MinimumGrade is { } minimum)
            return letter > minimum ? $"Note {letter} sous le minimum exigé {minimum}{limiting}." : null;
        return letter == AnalysisGrade.F ? $"Note F sans minimum valide : elle ne peut pas passer{limiting}." : null;
    }

    public static string UnreadableLine(AnalysisGateUnreadableEvidenceDto evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var producer = string.IsNullOrWhiteSpace(evidence.ScannerKey) ? "producteur inconnu" : $"producteur {evidence.ScannerKey}";
        var step = string.IsNullOrWhiteSpace(evidence.StepName) ? string.Empty : $", étape « {evidence.StepName} »";
        return $"Preuve illisible, ignorée du verdict : {producer}{step}, rapport #{evidence.ReportId.ToString(CultureInfo.InvariantCulture)}, "
            + $"{evidence.Evidence} : {evidence.Detail ?? "contenu invalide"}";
    }

    /// <summary>The failure reason of the task for an Error verdict: the number of causes then each cause, cut cleanly
    /// under the server's cap (the log carries every cause in full).</summary>
    public static string ErrorReason(string target, AnalysisRunGateDto gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var causes = gate.ErrorCauses.Select(AnalysisGateErrorCauseText.Line).ToList();
        var prefix = $"Verdict {target} en erreur ({causes.Count.ToString(CultureInfo.InvariantCulture)} cause"
            + $"{(causes.Count > 1 ? "s" : string.Empty)}) : ";
        var body = causes.Count == 0 ? AnalysisGateErrorCauseText.NoneProvided : string.Join(" ; ", causes);
        var reason = prefix + body;
        return reason.Length <= MaxReasonLength
            ? reason
            : reason[..(MaxReasonLength - 1)] + "…";
    }
}
