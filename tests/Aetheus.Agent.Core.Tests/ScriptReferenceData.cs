// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The reference files of the ported delivery scripts, under <c>TestData/</c>, written once with node
/// v24.7.0 (version and default collation locale recorded in each file) by throwaway drivers that ran
/// the real scripts in-process (argv, working directory = an empty temporary directory holding the
/// case's files, environment = PATH and SystemRoot plus the case's variables, standard output captured,
/// the thrown error kept as its constructor name and message, the temporary directory written as
/// <c>&lt;workspace&gt;</c>):
///
/// <c>delivery-promotion-cases.json</c>: contracts sealed by <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-delivery-contract.mjs</c>
/// (run as a child process; some then edited and resealed with the generator's own <c>canonicalize</c>,
/// or edited without resealing), schema histories and live contracts, and for each run of
/// <c>verify-delivery-promotion.mjs</c> its arguments, standard output lines and error. No file a run
/// saw changed afterwards, so one layout serves every run of a case.
///
/// <c>package-sbom-cases.json</c>: for each run of <c>generate-package-sbom.mjs</c> its parameters,
/// <c>SOURCE_DATE_EPOCH</c> (absent when unset), input file, and the SPDX document it wrote or its error.
///
/// <c>js-locale-compare.json</c>: <c>localeCompare</c> over ASCII: the 128 characters sorted with their
/// rank (equal ranks compare as 0), and 3681 generated pairs (case, punctuation, digits, prefixes,
/// ignorable controls) with the sign of the result, each checked equal under <c>en</c> and <c>und</c>.
///
/// <c>payload-evidence-cases.json</c>: sequences of runs of <c>nightly-evidence.mjs</c> (and file
/// rewrites between them) with, for each run, its arguments, standard output, error, and the evidence a
/// create wrote.
///
/// <c>nightly-qualification-cases.json</c>: for each run of <c>record-nightly-qualification.mjs</c> its
/// parameters, <c>CANDIDATE_ASSURANCE_GRADE</c>, the instant the global <c>Date</c> was fixed to for the
/// run, input files, <c>console.log</c> lines, and the qualification it wrote or its error.
///
/// <c>release-ancestry-cases.json</c>: not in-process: <c>verify-release-ancestry.sh</c> run with <c>sh</c>
/// (Git for Windows, recorded) in throwaway Git repositories, with its exit status, standard output and
/// standard error, commit ids replaced by placeholders (see <c>VerifyReleaseAncestryFunctionTests</c>).
///
/// A case carrying <c>difference</c> is one the script accepted and the port refuses on purpose.
/// </summary>
internal static class ScriptReferenceData
{
    private static readonly ConcurrentDictionary<string, JsonDocument> Documents = new(StringComparer.Ordinal);

    public static JsonElement Root(string file) => Documents.GetOrAdd(file, name => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(RepositoryScan.Root, "tests", "Aetheus.Agent.Core.Tests", "TestData", name)))).RootElement;

    public static TheoryData<string> CaseNames(string file)
    {
        var names = new TheoryData<string>();
        foreach (var testCase in Root(file).GetProperty("cases").EnumerateArray())
            names.Add(testCase.GetProperty("name").GetString()!);
        return names;
    }

    public static JsonElement Case(string file, string name) => Root(file).GetProperty("cases")
        .EnumerateArray()
        .Single(testCase => testCase.GetProperty("name").GetString() == name);
}
