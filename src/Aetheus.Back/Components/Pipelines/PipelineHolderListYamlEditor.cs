// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The outcome of declaring a holder: the document to write, or the reason it was refused (never both).
/// <see cref="Changed"/> is false when the holder was already declared and the text is the one given.</summary>
public sealed record HolderDeclarationEdit(string? Yaml, string? Error, bool Changed)
{
    public bool Succeeded => Error is null;
    public static HolderDeclarationEdit Ok(string yaml, bool changed) => new(yaml, null, changed);
    public static HolderDeclarationEdit Fail(string error) => new(null, error, false);
}

/// <summary>
/// R-24: adds a vault or a variable library to the top-level <c>vaults:</c> or <c>variable_libraries:</c> list of
/// a pipeline YAML by editing its text, so comments, order, indentation, quotes and line endings stay as written
/// (a re-serialization would drop the comments). It never trusts its own edit: it parses the original and the
/// result and refuses unless the two documents are equal except for that one list. A layout it cannot keep (a
/// repeated key, a single value instead of a list, a flow list over several lines) is refused, not guessed.
/// </summary>
/// <remarks>A pipeline that extends a template and lists nothing of its own reads the template's list; the moment it
/// lists one name, its list replaces the template's (<see cref="PipelineTemplateResolver"/>). So a child with no list
/// of its own receives the inherited names too, to keep reading what it read.</remarks>
public static partial class PipelineHolderListYamlEditor
{
    public const string VaultsKey = "vaults";
    public const string LibrariesKey = "variable_libraries";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName { get; }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "true", "false", "null", "yes", "no", "on", "off", "y", "n" };

    /// <summary>Declares <paramref name="name"/> in the list of <paramref name="kind"/>. <paramref name="inherited"/> are
    /// the names the pipeline reads through its template; they are written along with the new name only when the
    /// pipeline's own list is empty or absent.</summary>
    public static HolderDeclarationEdit Declare(
        string? yaml, PipelineSetupReadinessContainerKind kind, string name, IReadOnlyList<string>? inherited = null)
    {
        if (InputError(yaml, name) is { } inputError)
        {
            return HolderDeclarationEdit.Fail(inputError);
        }

        var original = PipelineScheduleYamlEditor.Parse(yaml!, out var parseError);
        if (original is null)
        {
            return HolderDeclarationEdit.Fail(parseError ?? "The pipeline YAML does not parse.");
        }

        var key = kind == PipelineSetupReadinessContainerKind.Vault ? VaultsKey : LibrariesKey;
        if (!TryReadList(original, key, out var own))
        {
            return HolderDeclarationEdit.Fail($"The value of '{key}' is not a list of names.");
        }

        var effective = own.Count > 0 ? own : [.. inherited ?? []];
        if (effective.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return HolderDeclarationEdit.Ok(yaml!, false);
        }

        // Only the new name when the list is the pipeline's own; the template's names too when it was inherited.
        var written = own.Count > 0 ? [name] : (IReadOnlyList<string>)[.. effective, name];
        return WriteList(yaml!, original, kind, key, written, [.. effective, name]);
    }

    // Why the input cannot be edited at all, or null.
    private static string? InputError(string? yaml, string name)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return "The pipeline YAML is empty.";
        }

        return string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Any(char.IsControl)
            ? "The name is empty or holds characters that cannot be written safely in the YAML."
            : null;
    }

    // Writes the names into the list text, then checks the result against the expected document.
    private static HolderDeclarationEdit WriteList(
        string yaml, Dictionary<object, object?> original, PipelineSetupReadinessContainerKind kind, string key,
        IReadOnlyList<string> written, IReadOnlyList<string> expectedList)
    {
        var text = RootMappingText.Create(yaml, out var layoutError);
        if (text is null)
        {
            return HolderDeclarationEdit.Fail(layoutError ?? "The pipeline YAML could not be edited.");
        }

        var afterKeys = kind == PipelineSetupReadinessContainerKind.Vault
            ? new[] { LibrariesKey, "extends", "name" }
            : ["extends", "name"];
        var editError = text.AddToList(key, [.. written.Select(Render)], afterKeys);
        if (editError is not null)
        {
            return HolderDeclarationEdit.Fail(editError);
        }

        var edited = text.ToString();
        var verification = Verify(original, edited, key, expectedList);
        return verification is null
            ? HolderDeclarationEdit.Ok(edited, true)
            : HolderDeclarationEdit.Fail(verification);
    }

    /// <summary>A name as a YAML scalar: plain when it is a plain word that stays a string, double quoted otherwise
    /// (quotes and backslashes escaped), so a name never changes meaning in either a block or a flow list.</summary>
    internal static string Render(string name) =>
        PlainName.IsMatch(name) && !Reserved.Contains(name)
            ? name
            : "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // The list as the generic deserializer reads it: absent or null is an empty list, anything but scalars is not a list of names.
    private static bool TryReadList(Dictionary<object, object?> document, string key, out List<string> names)
    {
        names = [];
        if (!document.TryGetValue(key, out var value) || value is null)
        {
            return true;
        }

        if (value is not IList<object?> list)
        {
            return false;
        }

        foreach (var item in list)
        {
            if (item is not string text)
            {
                return false;
            }

            names.Add(text);
        }

        return true;
    }

    private static string? Verify(
        Dictionary<object, object?> original, string edited, string key, IReadOnlyList<string> expectedList)
    {
        var parsed = PipelineScheduleYamlEditor.Parse(edited, out var error);
        if (parsed is null)
        {
            return $"The edited pipeline YAML was refused, nothing was written: {error}";
        }

        var expected = new Dictionary<object, object?>(original)
        {
            [key] = expectedList.Select(item => (object?)item).ToList()
        };
        return YamlTree.AreEqual(expected, parsed)
            ? null
            : $"The edit would have changed more than the '{key}' list, so it was refused.";
    }
}
