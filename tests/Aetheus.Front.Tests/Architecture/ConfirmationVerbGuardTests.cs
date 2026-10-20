// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Confirmation buttons use specific action verbs and dismiss with the return
/// label.</summary>
public sealed class ConfirmationVerbGuardTests
{
    private static readonly Regex ValidateKey = new(@"\[""Validate""\]", RegexOptions.Compiled);
    private static readonly Regex CancelDismiss = new(@"CancelButtonText\s*=\s*\w+\[""Cancel""\]", RegexOptions.Compiled);

    [Fact]
    public void NoConfirmation_IsLabelledTheGenericValidate()
    {
        Assert.True(FrontFiles().Count() > 100, "The scan of src/Aetheus.Front found almost no source: it is broken.");
        var violations = FrontFiles()
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(x => ValidateKey.IsMatch(x.line))
            .Select(x => $"{Rel(x.file)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(violations.Count == 0,
            "R-407: a confirmation names its verb (Créer, Mettre à jour, Supprimer...), never the generic \"Valider\":\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void NoConfirmation_IsDismissedByCancel()
    {
        var violations = FrontFiles()
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(x => CancelDismiss.IsMatch(x.line))
            .Select(x => $"{Rel(x.file)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(violations.Count == 0,
            "R-407: a confirmation is dismissed by \"Revenir\" (GoBack), not \"Annuler\":\n  " + string.Join("\n  ", violations));
    }

    private static IEnumerable<string> FrontFiles() =>
        RepositoryScan.Enumerate(FrontDirectory(), "*.cs").Concat(RepositoryScan.Enumerate(FrontDirectory(), "*.razor"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Rel(string file) => Path.GetRelativePath(FrontDirectory(), file).Replace('\\', '/');

    private static string FrontDirectory() => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
}
