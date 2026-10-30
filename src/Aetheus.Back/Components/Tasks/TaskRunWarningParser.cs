// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Tasks;

/// <summary>
/// Reads the run warning a step published (recette R-75): the way a pipeline step, which exits successfully on
/// purpose, still tells the run page that something it was meant to check did not happen (for example a QA that
/// deployed no environment). A step publishes it with the platform's output-variable marker, under the reserved
/// name <see cref="VariableName"/>; the text is the whole warning, on one line. It is bounded because the line comes
/// from a script, not from the platform.
/// <para>Recette R-23 (W8) and R-113: a step may also name its warning in <see cref="CodeVariableName"/>. The warning is
/// then stored as a problem of code <c>RunWarning_{name}</c> (<see cref="StoredProblem"/>), which the front says in the
/// user's language (resource <c>LaunchProblem_RunWarning_{name}</c>) and an API client reads in English. A name that
/// is not a plain identifier is ignored, and a name the front does not know shows the English text: either way
/// nothing a script writes is lost or interpreted.</para>
/// </summary>
internal static partial class TaskRunWarningParser
{
    /// <summary>The output variable whose value a step publishes as a warning of its run.</summary>
    internal const string VariableName = "RUN_WARNING";

    /// <summary>The output variable naming the warning, so the front can say it in the user's language.</summary>
    internal const string CodeVariableName = "RUN_WARNING_CODE";

    /// <summary>The code of a named warning is this prefix and the name, so a script never borrows a platform code.</summary>
    internal const string CodePrefix = "RunWarning_";

    internal const int MaxWarningLength = 600;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex WarningName();

    internal static List<string> Parse(IReadOnlyDictionary<string, string> outputVariables)
    {
        ArgumentNullException.ThrowIfNull(outputVariables);
        foreach (var (name, value) in outputVariables)
        {
            if (!string.Equals(name, VariableName, StringComparison.OrdinalIgnoreCase)) continue;
            var text = value.Trim();
            if (text.Length == 0) break;
            var warning = text.Length > MaxWarningLength ? text[..MaxWarningLength] : text;
            return [Named(outputVariables, warning)];
        }
        return [];
    }

    private static string Named(IReadOnlyDictionary<string, string> outputVariables, string warning)
    {
        var code = outputVariables
            .FirstOrDefault(variable => string.Equals(variable.Key, CodeVariableName, StringComparison.OrdinalIgnoreCase))
            .Value?.Trim();
        return code is not null && WarningName().IsMatch(code)
            ? StoredProblem.Compose(new ProblemDto(CodePrefix + code, new Dictionary<string, string>(StringComparer.Ordinal)), warning)
            : warning;
    }
}
