// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Recette R-13 and R-19: the target of a <see cref="OperationKind.CertbotRenewalCheck"/> operation. <c>-</c> rehearses
/// every certificate; otherwise it is the names of the certificates to rehearse, one after the other, separated by
/// commas (a certificate name never holds one). <see cref="OperationTargetValidator.CertbotRenewalCheckNames"/> reads
/// it, for the validator, the backend that queues it and the agent that runs it alike.
/// </summary>
public static class CertbotRenewalCheckTarget
{
    /// <summary>The target that rehearses every certificate.</summary>
    public const string All = "-";

    /// <summary>The most certificates one operation names; the helper refuses more.</summary>
    public const int MaxNames = 64;

    internal const char Separator = ',';

    /// <summary>The target naming exactly these certificates.</summary>
    public static string Of(IEnumerable<string> names) => string.Join(Separator, names);
}
