// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// R-113: the reason an agent self-update failed, said in the user's language. The agent reports fixed English
/// diagnostics ("release manifest invalid") and the server stores coded failures with an English sentence
/// ("wrong-version", "Expected 1.5.0, received 1.2.0."); both are known here and translated. Any other text (an
/// exception message, a free diagnostic) is shown as it came, since it carries the only detail there is.
/// </summary>
public static partial class AgentUpdateDiagnosticText
{
    // Fixed diagnostics of AgentSelfUpdateOperationExecutor, by resource key.
    private static readonly Dictionary<string, string> AgentDiagnostics = new(StringComparer.Ordinal)
    {
        ["release manifest invalid"] = "AgentUpdateDiag_ManifestInvalid",
        ["qualified release manifest required"] = "AgentUpdateDiag_QualifiedManifestRequired",
        ["download failed"] = "AgentUpdateDiag_DownloadFailed",
        ["extraction failed"] = "AgentUpdateDiag_ExtractionFailed",
        ["staged payload invalid"] = "AgentUpdateDiag_PayloadInvalid",
        ["capability request refused"] = "AgentUpdateDiag_CapabilityRequestRefused",
        ["Linux integration posture upgrade supervisor is missing or obsolete."] = "AgentUpdateDiag_PostureSupervisorMissing"
    };

    public static string? Localize(string? failureCode, string? diagnostic, IStringLocalizer<AppStrings> l)
    {
        switch (failureCode)
        {
            case "confirmation-timeout": return l["AgentUpdateDiag_ConfirmationTimeout"];
            case "update-task-timeout": return l["AgentUpdateDiag_TaskTimeout"];
            case "wrong-version" when diagnostic is not null && WrongVersionRegex().Match(diagnostic) is { Success: true } m:
                return l["AgentUpdateDiag_WrongVersion", m.Groups["expected"].Value, m.Groups["received"].Value];
            case "unsupported-protocol" when diagnostic is not null && ProtocolRegex().Match(diagnostic) is { Success: true } m:
                return l["AgentUpdateDiag_UnsupportedProtocol", m.Groups["protocol"].Value];
            case "missing-capabilities" when diagnostic is not null && CapabilitiesRegex().Match(diagnostic) is { Success: true } m:
                return l["AgentUpdateDiag_MissingCapabilities", m.Groups["list"].Value];
        }
        return diagnostic is not null && AgentDiagnostics.TryGetValue(diagnostic.Trim(), out var key)
            ? l[key]
            : diagnostic;
    }

    // The sentences AgentUpdateRepository writes; "missing" stands for an absent value.
    [GeneratedRegex(@"^Expected (?<expected>\S+), received (?<received>\S+)\.\z")]
    private static partial Regex WrongVersionRegex();

    [GeneratedRegex(@"^Received unsupported protocol (?<protocol>\S+)\.\z")]
    private static partial Regex ProtocolRegex();

    [GeneratedRegex(@"^Missing expected capabilities: (?<list>.+)\.\z")]
    private static partial Regex CapabilitiesRegex();
}
