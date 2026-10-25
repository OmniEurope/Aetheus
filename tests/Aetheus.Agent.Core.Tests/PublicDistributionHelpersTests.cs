// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The pure helpers of the <c>verify-public-distribution</c> port, with the cases of
/// <c>tests/js/deploy/export-public-distribution.test.mjs</c>: glob matching, the export plan and its
/// overlay, the unlisted top-level entries, the rewriting of renamed paths, the dropped test methods and
/// hook tasks, the forbidden literals and the comparison of two trees; then the assembly of a distribution
/// and the reading of a boundary, which the script's test only reached through a whole export.
/// </summary>
public sealed class PublicDistributionHelpersTests
{
    private const string BoundaryJson = """
        {
          "topLevel": ["src", "README.md"],
          "exclude": ["ci/**", "docs/**", "AGENTS.md", "z*.prompt.md", "src/Aetheus.Back/appsettings.Development.json"],
          "rename": { "launch-windows.ps1": "launch-windows.ps1", "scripts/launch-core.ps1": "scripts/launch-core.ps1" },
          "forbidden": ["git.private.example"]
        }
        """;

    private static PublicDistributionBoundary Boundary(string json = BoundaryJson)
    {
        var reading = PublicDistributionBoundary.Read(json);
        Assert.NotNull(reading.Boundary);
        return reading.Boundary;
    }

    [Theory]
    [InlineData("docs/adr/ADR-001.md", "docs/**", true)]
    [InlineData("zidees.prompt.md", "z*.prompt.md", true)]
    [InlineData("src/zidees.prompt.md", "z*.prompt.md", false)]
    [InlineData("README.md", "**/*.md", true)]
    [InlineData("docsite/readme.md", "docs/**", false)]
    [InlineData("a/b/README.md", "**/*.md", true)]
    [InlineData("src/a.md", "src/*.md", true)]
    [InlineData("src/x/a.md", "src/*.md", false)]
    [InlineData("scripts/a.ps1", "  scripts/a.ps1  ", true)]
    [InlineData("scripts/a-ps1", "scripts/a.ps1", false)]
    [InlineData("docs/a.md.bak", "docs/**/*.md", false)]
    public void AGlob_CrossesDirectoriesOnlyWithADoubleStar(string path, string glob, bool expected) =>
        Assert.Equal(expected, PublicDistributionGlobs.MatchesAny(path, [glob]));

    [Fact]
    public void AGlobList_MatchesWhenAnyGlobDoes()
    {
        var globs = new PublicDistributionGlobs(["docs/**", "**/*.md"]);

        Assert.True(globs.IsMatch("docs/x.txt"));
        Assert.True(globs.IsMatch("README.md"));
        Assert.False(globs.IsMatch("src/a.cs"));
    }

    [Fact]
    public void ARenameOntoAPathTheSourceAlreadyHas_IsRefused()
    {
        var plan = PublicDistributionPlan.Create(["launch-windows.ps1", "launch-windows.ps1"], Boundary());

        Assert.Contains("already exists", plan.Refusal, StringComparison.Ordinal);
        Assert.Equal("rename target 'launch-windows.ps1' already exists in the source", plan.Refusal);
    }

    [Fact]
    public void AnExcludedFile_IsNotRenamed()
    {
        var boundary = Boundary("""{ "topLevel": [], "exclude": ["launch-windows.ps1"], "rename": { "launch-windows.ps1": "launch.ps1" }, "forbidden": [] }""");

        var plan = PublicDistributionPlan.Create(["launch-windows.ps1"], boundary);

        Assert.Empty(plan.Copies);
        Assert.Equal(["launch-windows.ps1"], plan.Excluded);
    }

    [Fact]
    public void ATopLevelEntryNobodyListed_IsReportedSorted()
    {
        Assert.Equal(["newdir"], PublicDistributionPlan.UnlistedTopLevel(["src/a.cs", "newdir/x", "README.md"], ["src", "README.md"]));
        Assert.Equal(["a", "b"], PublicDistributionPlan.UnlistedTopLevel(["b/x", "a/y", "b/z", "src/q"], ["src"]));
        Assert.Empty(PublicDistributionPlan.UnlistedTopLevel(["src/a.cs"], ["src"]));
    }

    [Fact]
    public void ReferencesToARenamedLauncher_FollowIt_IdentifiersThatOnlyShareTheWordDoNot()
    {
        const string text = "run ./launch-windows.ps1 -s, it loads scripts/launch-core.ps1; see .ylaunch.local and Show-YLaunchHelp";
        var rename = Boundary().Rename;

        Assert.Equal(
            "run ./launch-windows.ps1 -s, it loads scripts/launch-core.ps1; see .ylaunch.local and Show-YLaunchHelp",
            PublicDistributionEdits.RewriteReferences(text, rename));
    }

    [Fact]
    public void TheLongestRenamedNameIsRewrittenFirst_WhateverTheOrderOfTheFile()
    {
        // The short entry listed first would otherwise half-rewrite "scripts/launch-windows.ps1" before the long
        // entry saw it. The script then also rewrites the base name of the long entry wherever it is left
        // (the standalone "launch-windows.ps1" becomes "start.ps1"): the result is the script's own, in both orders.
        KeyValuePair<string, string>[] rename = [new("launch-windows.ps1", "launch.ps1"), new("scripts/launch-windows.ps1", "tools/start.ps1")];

        Assert.Equal("tools/start.ps1 and start.ps1", PublicDistributionEdits.RewriteReferences("scripts/launch-windows.ps1 and launch-windows.ps1", rename));
        Assert.Equal("tools/start.ps1 and start.ps1", PublicDistributionEdits.RewriteReferences("scripts/launch-windows.ps1 and launch-windows.ps1", [rename[1], rename[0]]));
    }

    [Fact]
    public void AForbiddenLiteral_IsReportedWithItsFile()
    {
        var hits = PublicDistributionEdits.FindForbidden(
            [KeyValuePair.Create("deploy/remote-deploy/deploy.sh", "remote=git.private.example/x"), KeyValuePair.Create("README.md", "fine")],
            ["git.private.example"]);

        Assert.Equal(["deploy/remote-deploy/deploy.sh: git.private.example"], hits);
    }

    private static readonly string TestFile = string.Join(
        "\r\n",
        "public sealed class AuditTests",
        "{",
        "    [Fact]",
        "    public void Keeps() => Assert.True(true);",
        "",
        "    /// <summary>Reads a path left out of the distribution.</summary>",
        "    [Theory]",
        "    [InlineData(\"a.conf\")]",
        "    public void ReadsTheVhost(string vhost)",
        "    {",
        "        var pattern = new Regex(@\"(?=$|[\\s\\)\\]\\}])\");",
        "        if (vhost.Length > 0)",
        "        {",
        "            Assert.Matches(pattern, vhost);",
        "        }",
        "    }",
        "",
        "    [Fact]",
        "    public void ExpressionBodied() =>",
        "        Assert.True(File.Exists(\"deploy/left-out.conf\"));",
        "",
        "    [Fact]",
        "    public void AlsoKept()",
        "    {",
        "    }",
        "}");

    [Fact]
    public void ADroppedTest_GoesWithItsAttributesAndDocComment_BracesInItsStringsDoNotCutItShort()
    {
        var (text, missing) = PublicDistributionEdits.DropTestMethods(TestFile, ["ReadsTheVhost", "ExpressionBodied"]);

        Assert.Empty(missing);
        Assert.DoesNotContain("ReadsTheVhost", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpressionBodied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InlineData", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left out of the distribution", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Regex", text, StringComparison.Ordinal);
        Assert.Contains("public void Keeps()", text, StringComparison.Ordinal);
        Assert.Contains("public void AlsoKept()", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n').Count(line => line.Trim() == "}"));
    }

    [Fact]
    public void ADroppedTestTheSourceNoLongerHas_IsReported_NotIgnored()
    {
        var (text, missing) = PublicDistributionEdits.DropTestMethods(TestFile, ["RenamedSinceTheList"]);

        Assert.Equal(["RenamedSinceTheList"], missing);
        Assert.Equal(TestFile, text);
    }

    [Fact]
    public void ADroppedTestWhoseBodyNeverEnds_IsReported_NotCutAtTheEndOfTheFile()
    {
        var (text, missing) = PublicDistributionEdits.DropTestMethods("    public void Open()\n    {\n        Run();\n", ["Open"]);

        Assert.Equal(["Open"], missing);
        Assert.Contains("Open", text, StringComparison.Ordinal);
    }

    private static readonly string Runner =
        """
        {
           "tasks": [
              { "name": "build", "group": "pre-commit", "command": "dotnet", "args": ["build", "-c", "Release"] },
              { "name": "delivery-config", "group": "pre-push", "command": "pwsh", "args": ["-File", "scripts/check-delivery-config.ps1"] },
              { "name": "quality", "group": "pre-push", "command": "pwsh", "args": ["-File", "scripts/check-quality.ps1"] }
           ]
        }
        """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

    [Fact]
    public void ADroppedHookTask_LeavesTheRunner_TheOthersStayAsTheyWereWritten()
    {
        var edit = PublicDistributionEdits.DropHookTasks(Runner, ["delivery-config"], new HashSet<string> { "scripts/check-quality.ps1" });

        Assert.NotNull(edit);
        Assert.Empty(edit.Missing);
        Assert.Empty(edit.Dangling);
        Assert.DoesNotContain("delivery-config", edit.Text, StringComparison.Ordinal);
        Assert.Contains("\"quality\"", edit.Text, StringComparison.Ordinal);
        Assert.Contains("\"build\"", edit.Text, StringComparison.Ordinal);
        Assert.EndsWith("}\r\n", edit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", edit.Text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // The indent of the file (three spaces) is the indent written back.
        Assert.Contains("\r\n   \"tasks\": [\r\n      {\r\n         \"name\": \"build\"", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleHookTaskName_AndAKeptTaskOnAnAbsentFile_AreBothReported_NeverSkipped()
    {
        var edit = PublicDistributionEdits.DropHookTasks(Runner, ["renamed-task"], new HashSet<string> { "scripts/check-quality.ps1" });

        Assert.NotNull(edit);
        Assert.Equal(["renamed-task"], edit.Missing);
        Assert.Equal(["delivery-config: scripts/check-delivery-config.ps1"], edit.Dangling);
    }

    [Fact]
    public void ARunnerWithABomAndNoTrailingNewline_IsRewrittenWithoutTheBomOrANewline()
    {
        var edit = PublicDistributionEdits.DropHookTasks("﻿{\n \"tasks\": [{ \"name\": \"a\" }, { \"name\": \"b\" }]\n}", ["a"], new HashSet<string>());

        Assert.NotNull(edit);
        Assert.Equal("{\n \"tasks\": [\n  {\n   \"name\": \"b\"\n  }\n ]\n}", edit.Text);
    }

    [Fact]
    public void ARunnerThatIsNotJson_IsNotEdited()
    {
        Assert.Null(PublicDistributionEdits.DropHookTasks("not json", ["a"], new HashSet<string>()));
        Assert.Null(PublicDistributionEdits.DropHookTasks("[1]", ["a"], new HashSet<string>()));
    }

    private static Dictionary<string, byte[]> Tree(params (string Path, string Text)[] entries) =>
        entries.ToDictionary(entry => entry.Path, entry => Encoding.UTF8.GetBytes(entry.Text), StringComparer.Ordinal);

    [Fact]
    public void TheComparison_IgnoresTheDocumentationOnBothSides_AndNamesEveryOtherDifference()
    {
        var distribution = Tree(("src/App.cs", "a"), ("deploy/run.sh", "b"), ("README.md", "x"), ("docs/guide.md", "g"));
        string[] ignore = ["**/*.md", "docs/**"];

        Assert.Empty(PublicDistributionBuilder.Compare(
            distribution,
            Tree(("src/App.cs", "a"), ("deploy/run.sh", "b"), ("README.md", "edited"), ("docs/public-only.md", "p")),
            ignore));

        Assert.Equal(
            ["missing: deploy/run.sh", "changed: src/App.cs", "unexpected: extra.sh"],
            PublicDistributionBuilder.Compare(distribution, Tree(("src/App.cs", "changed"), ("extra.sh", "e"), ("README.md", "x")), ignore));
    }

    [Fact]
    public void AnEmptyFile_IsNotMissing_AndIsComparedByItsBytes()
    {
        var distribution = new Dictionary<string, byte[]> { ["a"] = [], ["b"] = [] };
        var published = new Dictionary<string, byte[]> { ["a"] = [], ["b"] = [1] };

        Assert.Equal(["changed: b"], PublicDistributionBuilder.Compare(distribution, published, []));
    }

    [Fact]
    public void ATextFile_HasNoNulByteInItsFirst8000Bytes()
    {
        var late = new byte[9000];
        Array.Fill(late, (byte)'a');
        late[8500] = 0;
        var early = new byte[100];
        early[99] = 0;

        Assert.True(PublicDistributionBuilder.IsText([]));
        Assert.True(PublicDistributionBuilder.IsText("text"u8.ToArray()));
        Assert.True(PublicDistributionBuilder.IsText(late));
        Assert.False(PublicDistributionBuilder.IsText(early));
    }

    private static PublicDistribution Assemble(string boundaryJson, params (string Path, byte[] Bytes)[] files)
    {
        var plan = PublicDistributionPlan.Create([.. files.Select(file => file.Path)], Boundary(boundaryJson));
        var tree = files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
        return PublicDistributionBuilder.Assemble(plan.Copies, tree, Boundary(boundaryJson));
    }

    private static (string, byte[]) Text(string path, string text) => (path, Encoding.Latin1.GetBytes(text));

    [Fact]
    public void TheDistribution_RewritesRenamedPaths_InTextOnly_NotInTheOverlayNorInBinaryFiles()
    {
        const string boundary = """
            { "topLevel": ["README.md", "launch-windows.ps1", "tool.bin", "scripts"], "exclude": [], "forbidden": [],
              "rename": { "launch-windows.ps1": "launch-windows.ps1" } }
            """;
        var binary = new byte[] { 0, 0xFF, (byte)'y', (byte)'l', (byte)'a', (byte)'u', (byte)'n', (byte)'c', (byte)'h', 0x80 };

        var distribution = Assemble(
            boundary,
            Text("launch-windows.ps1", "# launch-windows.ps1 café"),
            Text("scripts/run.sh", "sh launch-windows.ps1"),
            Text("deploy/public-distribution/overlay/README.md", "see launch-windows.ps1"),
            ("tool.bin", binary));

        Assert.Null(distribution.Refusal);
        Assert.Equal("# launch-windows.ps1 café", Encoding.Latin1.GetString(distribution.Contents["launch-windows.ps1"]));
        Assert.Equal("sh launch-windows.ps1", Encoding.Latin1.GetString(distribution.Contents["scripts/run.sh"]));
        Assert.Equal("see launch-windows.ps1", Encoding.Latin1.GetString(distribution.Contents["README.md"]));
        Assert.Equal(binary, distribution.Contents["tool.bin"]);
    }

    [Fact]
    public void AForbiddenLiteral_AnywhereInATextFile_RefusesTheExport_ButNotInABinaryFile()
    {
        const string boundary = """{ "topLevel": ["a.txt", "b.bin"], "exclude": [], "rename": {}, "forbidden": ["secret.example"] }""";

        var refused = Assemble(boundary, Text("a.txt", "host=secret.example"), ("b.bin", Encoding.Latin1.GetBytes("\0secret.example")));
        var accepted = Assemble(boundary, Text("a.txt", "fine"), ("b.bin", Encoding.Latin1.GetBytes("\0secret.example")));

        Assert.Equal("1 forbidden literal(s) in the distribution:\n  a.txt: secret.example", refused.Refusal);
        Assert.Null(accepted.Refusal);
    }

    [Fact]
    public void TheDroppedTests_AreRemovedFromTheDistribution_AndAStaleNameOrFileRefusesTheExport()
    {
        const string boundary = """
            { "topLevel": ["T.cs"], "exclude": [], "rename": {}, "forbidden": [],
              "dropTests": { "T.cs": ["Gone"], "Absent.cs": ["Gone"] } }
            """;
        const string source = "class T\n{\n    [Fact]\n    public void Gone() => Assert.True(true);\n\n    [Fact]\n    public void Kept() => Assert.True(true);\n}\n";

        var refused = Assemble(boundary, Text("T.cs", source));
        var accepted = Assemble(boundary.Replace(", \"Absent.cs\": [\"Gone\"]", string.Empty, StringComparison.Ordinal), Text("T.cs", source));
        var stale = Assemble(boundary.Replace("\"T.cs\": [\"Gone\"], ", "\"T.cs\": [\"Renamed\"], ", StringComparison.Ordinal), Text("T.cs", source));

        Assert.Equal("boundary.json dropTests names tests the source no longer has:\n  Absent.cs (file)", refused.Refusal);
        Assert.Null(accepted.Refusal);
        Assert.Equal("class T\n{\n    [Fact]\n    public void Kept() => Assert.True(true);\n}\n", Encoding.Latin1.GetString(accepted.Contents["T.cs"]));
        Assert.Equal("boundary.json dropTests names tests the source no longer has:\n  T.cs: Renamed\n  Absent.cs (file)", stale.Refusal);
    }

    [Fact]
    public void TheHuskyRunner_IsAlwaysChecked_AndTheListedTasksAreDropped()
    {
        const string runner = "{\n  \"tasks\": [\n    { \"name\": \"keep\", \"args\": [\"scripts/kept.ps1\"] },\n    { \"name\": \"drop\", \"args\": [\"scripts/left-out.ps1\"] }\n  ]\n}\n";
        const string withDrop = """{ "topLevel": [".husky", "scripts"], "exclude": [], "rename": {}, "forbidden": [], "dropTasks": { ".husky/task-runner.json": ["drop"] } }""";
        const string withoutDrop = """{ "topLevel": [".husky", "scripts"], "exclude": [], "rename": {}, "forbidden": [] }""";

        var dropped = Assemble(withDrop, Text(".husky/task-runner.json", runner), Text("scripts/kept.ps1", "x"));
        var unreviewed = Assemble(withoutDrop, Text(".husky/task-runner.json", runner), Text("scripts/kept.ps1", "x"));
        var listedButMissing = Assemble(withDrop, Text("scripts/kept.ps1", "x"));

        Assert.Null(dropped.Refusal);
        var written = Encoding.UTF8.GetString(dropped.Contents[".husky/task-runner.json"]);
        Assert.DoesNotContain("drop", written, StringComparison.Ordinal);
        Assert.Contains("scripts/kept.ps1", written, StringComparison.Ordinal);
        Assert.Equal(
            "boundary.json dropTasks does not match the hooks:\n  .husky/task-runner.json: task drop: scripts/left-out.ps1 is not in the distribution",
            unreviewed.Refusal);
        Assert.Equal("boundary.json dropTasks does not match the hooks:\n  .husky/task-runner.json (file)", listedButMissing.Refusal);
    }

    [Fact]
    public void ABoundary_IsUnreadableWhenItIsNotJson_AndInvalidWhenAListIsMissingOrMistyped()
    {
        Assert.True(PublicDistributionBoundary.Read("{ not json").IsUnreadable);
        Assert.True(PublicDistributionBoundary.Read("").IsUnreadable);
        Assert.Equal("boundary.json is not an object", PublicDistributionBoundary.Read("[1]").Problem);
        Assert.Equal(
            "'topLevel' is missing; 'exclude' is not a list of strings; 'forbidden' is missing; 'rename' is not a map of strings",
            PublicDistributionBoundary.Read("""{ "exclude": [1], "rename": { "a": 2 } }""").Problem);
        Assert.Equal("'dropTests' is not a map of lists of strings",
            PublicDistributionBoundary.Read("""{ "topLevel": [], "exclude": [], "rename": {}, "forbidden": [], "dropTests": { "a": "b" } }""").Problem);
    }

    [Fact]
    public void ABoundary_KeepsTheOrderOfItsRenames_AndTheCompareIgnoreDefaultsToNothing()
    {
        var boundary = Boundary("""{ "topLevel": ["a"], "exclude": ["b"], "rename": { "z": "1", "a": "2" }, "forbidden": ["f"] }""");

        Assert.Equal(["z", "a"], boundary.Rename.Select(entry => entry.Key));
        Assert.Empty(boundary.CompareIgnore);
        Assert.Equal(["**/*.md"], Boundary("""{ "topLevel": [], "exclude": [], "rename": {}, "forbidden": [], "compareIgnore": ["**/*.md"] }""").CompareIgnore);
    }
}
