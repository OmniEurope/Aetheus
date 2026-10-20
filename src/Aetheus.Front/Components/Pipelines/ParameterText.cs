// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Pipeline parameter labels and help use their French values when available for a French
/// interface, otherwise their default values.</summary>
internal static class ParameterText
{
    public static string? Pick(string? text, string? french) =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(french)
            ? french
            : text;
}
