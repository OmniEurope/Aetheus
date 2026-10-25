// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// What the real scripts produced, committed under <c>TestData/</c> so the equivalence proof does not
/// need node. Both files were written once with node v24.7.0 (the version is recorded in each file) by a
/// throwaway driver that, for every case, laid the case's files out in an empty temporary directory, ran
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c> there (working directory = that
/// directory, arguments = the case's parameters in the script's order, environment = PATH and SystemRoot
/// plus the case's variables only) and recorded every file under <c>.pipeline-artifacts</c> with the
/// script's console lines or its error (<c>kind: message</c> from stderr).
///
/// <c>assurance-contract-generator-cases.json</c>: one entry per generation (parameters, environment,
/// input files, expected outputs). <c>assurance-contract-verifier-cases.json</c>: contracts the generator
/// sealed (some then edited and resealed with <c>JSON.stringify</c>, as <c>assurance-contract.test.mjs</c>
/// does, or edited as text) with their evidence and source-commit files, and the verdict
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-candidate-assurance-contract.mjs</c> gave for each threshold and requirement.
/// A file content is the text when its bytes are valid UTF-8, else <c>{ "base64": ... }</c>.
/// </summary>
internal static class AssuranceReferenceData
{
    private static readonly Lazy<JsonDocument> Generator = new(() => Load("assurance-contract-generator-cases.json"));
    private static readonly Lazy<JsonDocument> Verifier = new(() => Load("assurance-contract-verifier-cases.json"));

    public static TheoryData<string> GeneratorCaseNames() => Names(Generator.Value);

    public static TheoryData<string> VerifierCaseNames() => Names(Verifier.Value);

    public static JsonElement GeneratorCase(string name) => Find(Generator.Value, name);

    public static JsonElement VerifierCase(string name) => Find(Verifier.Value, name);

    /// <summary>The bytes of a recorded file.</summary>
    public static byte[] Bytes(JsonElement content) => content.ValueKind == JsonValueKind.String
        ? Encoding.UTF8.GetBytes(content.GetString()!)
        : Convert.FromBase64String(content.GetProperty("base64").GetString()!);

    /// <summary>Writes the recorded files under <paramref name="root"/>.</summary>
    public static void LayOut(JsonElement files, string root)
    {
        foreach (var file in files.EnumerateObject())
        {
            var path = Path.Combine(root, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Bytes(file.Value));
        }
    }

    /// <summary>Every file under <c>root/directory</c>, keyed like the recorded outputs.</summary>
    public static Dictionary<string, byte[]> Snapshot(string root, string directory)
    {
        var full = Path.Combine(root, directory);
        if (!Directory.Exists(full))
            return [];
        return Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(path => directory + "/" + Path.GetRelativePath(full, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    }

    /// <summary>The environment of a case, as the task variables of the step.</summary>
    public static Dictionary<string, string> Strings(JsonElement values) =>
        values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(RepositoryScan.Root, "tests", "Aetheus.Agent.Core.Tests", "TestData", name)));

    private static TheoryData<string> Names(JsonDocument document)
    {
        var names = new TheoryData<string>();
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
            names.Add(testCase.GetProperty("name").GetString()!);
        return names;
    }

    private static JsonElement Find(JsonDocument document, string name) => document.RootElement.GetProperty("cases")
        .EnumerateArray()
        .Single(testCase => testCase.GetProperty("name").GetString() == name);
}
