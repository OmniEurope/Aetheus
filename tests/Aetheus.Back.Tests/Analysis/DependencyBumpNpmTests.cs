// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The npm bump (PLAN-004, lot 4, point 7) on this repository's own <c>package.json</c> and <c>package-lock.json</c>: the requirement in
/// <c>package.json</c> is raised keeping its operator, everything else of the file is proved byte for byte untouched, and the lock
/// file (resolved versions, integrity hashes) is named with <c>npm install --package-lock-only</c> and never edited.
/// </summary>
public sealed class DependencyBumpNpmTests
{
    private const string RefreshCommand = "npm install --package-lock-only --ignore-scripts";

    private static readonly string Manifest = Fixture("package.json");
    private static readonly string Lock = Fixture("package-lock.json");

    private static string Package(string requirements, string section = "dependencies") =>
        "{\n  \"name\": \"app\",\n  \"version\": \"1.0.0\",\n  \"" + section + "\": {\n" + requirements + "\n  }\n}\n";

    [Fact]
    public void AnExactRequirement_IsRaisedInPackageJson_AndTheLockFileIsNamedNotEdited()
    {
        var result = Plan(Request("npm", "eslint", "10.13.0", "10.14.0", "package-lock.json"), ("package.json", Manifest), ("package-lock.json", Lock));

        Assert.Equal(DependencyBumpOutcome.NeedsPackageManager, result.Outcome);
        var edit = SingleEdit(result, "package.json", Manifest, "\"eslint\": \"10.13.0\"", "\"eslint\": \"10.14.0\"");
        var change = Assert.Single(edit.Changes);
        Assert.Equal(LineOf(Manifest, "\"eslint\""), change.Line);
        Assert.Equal("devDependencies: eslint 10.13.0 -> 10.14.0", change.Description);
        var refresh = Assert.Single(result.LockRefreshes);
        Assert.Equal(("package-lock.json", string.Empty, RefreshCommand), (refresh.LockFile, refresh.WorkingDirectory, refresh.Command));
        Assert.DoesNotContain(result.Edits, candidate => candidate.Path == "package-lock.json");
    }

    [Theory]
    [InlineData("JSONStream", "jsonstream")]
    [InlineData("@Scope/MixedCase", "@scope/mixedcase")]
    public void APackageWithCapitals_IsFoundWhateverTheCaseTheInventoryStoresAndKeepsTheManifestsSpelling(string spelled, string inventoryName)
    {
        // R-29: the inventory stores npm names lower case (VulnerabilityPackageName.Normalize), package.json writes them as published.
        var text = Package($"    \"{spelled}\": \"^1.3.0\"");

        var result = Plan(Request("npm", inventoryName, "1.3.0", "1.3.2", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        var edit = SingleEdit(result, "package.json", text, $"\"{spelled}\": \"^1.3.0\"", $"\"{spelled}\": \"^1.3.2\"");
        Assert.Equal($"dependencies: {spelled} ^1.3.0 -> ^1.3.2", Assert.Single(edit.Changes).Description);
        Assert.Equal(DependencyBumpOutcome.NeedsPackageManager, result.Outcome);
    }

    [Fact]
    public void AScopedPackage_IsFoundByItsFullName()
    {
        var result = Plan(Request("npm", "@eslint/js", "10.0.1", "10.0.2", "package-lock.json"), ("package.json", Manifest), ("package-lock.json", Lock));

        SingleEdit(result, "package.json", Manifest, "\"@eslint/js\": \"10.0.1\"", "\"@eslint/js\": \"10.0.2\"");
    }

    [Fact]
    public void ATransitivePackage_NoPackageJsonNames_IsRefusedNotInvented()
    {
        // minimatch 10.2.5 is in the real lock file under node_modules/minimatch, and package.json does not require it.
        var result = Plan(Request("npm", "minimatch", "10.2.5", "10.2.6", "package-lock.json"), ("package.json", Manifest), ("package-lock.json", Lock));

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
        Assert.Contains("another package", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("^4.17.15", "4.17.15", "4.17.21", "^4.17.21")]
    [InlineData("~4.17.15", "4.17.15", "4.17.21", "~4.17.21")]
    [InlineData(">=4.17.15", "4.17.15", "4.17.21", ">=4.17.21")]
    [InlineData("=1.2.3", "1.2.3", "1.2.4", "=1.2.4")]
    [InlineData("^4", "4.17.15", "4.17.21", "^4.17.21")]
    [InlineData("^0.2.3", "0.2.5", "0.2.9", "^0.2.9")]
    [InlineData("^0.0.3", "0.0.3", "0.0.4", "^0.0.4")]
    [InlineData("~1.2", "1.2.9", "1.2.10", "~1.2.10")]
    [InlineData("^4.17.15", "4.17.20", "5.0.0", "^5.0.0")]
    public void ARequirementThatCoversTheCurrentVersion_KeepsItsOperatorAndMovesItsVersionToTheTarget(string written, string current, string target, string expected)
    {
        var text = Package($"    \"lodash\": \"{written}\"");

        var result = Plan(Request("npm", "lodash", current, target, "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        SingleEdit(result, "package.json", text, $"\"lodash\": \"{written}\"", $"\"lodash\": \"{expected}\"");
    }

    [Theory]
    [InlineData("^0.2.3", "0.3.0")]
    [InlineData("^4.17.15", "5.1.0")]
    [InlineData("~1.2.3", "1.3.0")]
    [InlineData(">=2.0.0", "1.9.0")]
    [InlineData("10.13.0", "10.12.0")]
    public void ARequirementThatDoesNotCoverTheCurrentVersion_IsNotTheDeclarationOfThatMatch(string written, string current)
    {
        // The locked version comes from another package's own requirement, so raising this one would not move it.
        var text = Package($"    \"lodash\": \"{written}\"");

        var result = Plan(Request("npm", "lodash", current, "99.0.0", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
        Assert.Contains($"'{written}'", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("^1.0.0 || ^2.0.0")]
    [InlineData("*")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("1.2")]
    [InlineData("1.0.0 - 2.0.0")]
    [InlineData("^1.0.0-beta.1")]
    [InlineData("github:user/lodash#v1.2.3")]
    [InlineData("workspace:*")]
    [InlineData("npm:lodash-fork@^1.2.3")]
    [InlineData("file:../lodash")]
    public void ARequirementThatIsNotOneOperatorAndOneVersion_IsRefusedAsNotPinned(string written)
    {
        var text = Package($"    \"lodash\": \"{written}\"");

        var result = Plan(Request("npm", "lodash", "1.2.3", "1.2.4", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
        Assert.Contains($"'{written}'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclarationInSeveralSections_IsRaisedInEachAndPeerRequirementsAreLeftAlone()
    {
        var text = "{\n  \"dependencies\": {\n    \"lodash\": \"^4.17.15\"\n  },\n  \"devDependencies\": {\n    \"lodash\": \"~4.17.15\"\n  },\n"
            + "  \"optionalDependencies\": {\n    \"lodash\": \"4.17.15\"\n  },\n  \"peerDependencies\": {\n    \"lodash\": \"^4.0.0\"\n  }\n}\n";

        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        var edit = Assert.Single(result.Edits);
        Assert.Equal(
            text.Replace("\"^4.17.15\"", "\"^4.17.21\"", StringComparison.Ordinal).Replace("\"~4.17.15\"", "\"~4.17.21\"", StringComparison.Ordinal)
                .Replace("\"4.17.15\"", "\"4.17.21\"", StringComparison.Ordinal),
            edit.Content);
        Assert.Equal([3, 6, 9], edit.Changes.Select(change => change.Line).Order());
    }

    [Fact]
    public void TheFormatOfTheFile_Comments_TabsCrlfNonAsciiAndAByteOrderMark_IsKeptByteForByte()
    {
        var text = "\uFEFF{\r\n\t// toolchain of the shop, kept by Zo\u00eb\r\n\t\"author\": \"Zo\u00eb \\u00e9\",\r\n\t\"dependencies\": {\r\n\t\t\"left-pad\": \"^1.2.0\", /* pinned for the CLI */\r\n\t\t\"lodash\": \"^4.17.15\",\r\n\t},\r\n\t\"keywords\": [\"a\",\"b\"]\r\n}";

        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        SingleEdit(result, "package.json", text, "\"lodash\": \"^4.17.15\"", "\"lodash\": \"^4.17.21\"");
    }

    [Fact]
    public void ARequirementWrittenWithEscapes_IsRefusedRatherThanRewrittenBlind()
    {
        var text = Package("    \"lodash\": \"^4.17.\\u0031\\u0035\"");

        AssertRefused(
            Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}")),
            DependencyBumpRefusal.VersionNotPinned);
    }

    [Fact]
    public void ARequirementAlreadyAboveTheLockedVersion_IsNotTakenForTheDeclarationOfThatMatch()
    {
        // package.json asks for ^4.17.21 while the lock holds 4.17.15: the locked copy is not the one package.json
        // requires (a stale lock and a nested copy look the same here), so a bump says so instead of guessing.
        var text = Package("    \"lodash\": \"^4.17.21\"");

        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.22", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}"));

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
        Assert.Contains("'^4.17.21'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALockFile_TheEditIsComplete()
    {
        var text = Package("    \"lodash\": \"^4.17.15\"");

        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package.json"), ("package.json", text));

        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
        SingleEdit(result, "package.json", text, "\"lodash\": \"^4.17.15\"", "\"lodash\": \"^4.17.21\"");
    }

    [Fact]
    public void AWorkspace_RaisesTheMembersThatShareTheRootLock_AndNotAProjectWithItsOwn()
    {
        var root = Package("    \"left-pad\": \"1.0.0\"");
        var member = Package("    \"lodash\": \"^4.17.15\"");
        var ownLock = Package("    \"lodash\": \"^4.17.15\"");

        var result = Plan(
            Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"),
            ("package.json", root), ("package-lock.json", "{}"), ("packages/member/package.json", member),
            ("tools/standalone/package.json", ownLock), ("tools/standalone/package-lock.json", "{}"),
            ("node_modules/lodash/package.json", Package("    \"lodash\": \"^4.17.15\"")));

        SingleEdit(result, "packages/member/package.json", member, "\"^4.17.15\"", "\"^4.17.21\"");
        Assert.Equal("package-lock.json", Assert.Single(result.LockRefreshes).LockFile);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("yarn.lock")]
    public void ALockFileOfAnotherPackageManager_IsRefusedPlainly(string lockName)
    {
        var result = Plan(Request("npm", "lodash", "4.17.15", "4.17.21", lockName), ("package.json", Package("    \"lodash\": \"^4.17.15\"")), (lockName, string.Empty));

        AssertRefused(result, DependencyBumpRefusal.UnsupportedLockFile);
        Assert.Contains(lockName, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoLockFilesBesideOneManifest_AreRefusedRatherThanOneGuessed() =>
        AssertRefused(
            Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", Package("    \"lodash\": \"^4.17.15\"")), ("package-lock.json", "{}"), ("yarn.lock", string.Empty)),
            DependencyBumpRefusal.UnsupportedLockFile);

    [Fact]
    public void ALockFileWithoutItsPackageJson_IsRefusedAsManifestNotFound() =>
        AssertRefused(Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package-lock.json", Lock)), DependencyBumpRefusal.ManifestNotFound);

    [Theory]
    [InlineData("{ \"dependencies\": { \"lodash\": \"^4.17.15\", \"lodash\": \"^4.17.16\" } }")]
    [InlineData("{ \"dependencies\": { \"lodash\": \"^4.17.15\" }, \"dependencies\": { } }")]
    [InlineData("[ \"lodash\" ]")]
    [InlineData("{ \"dependencies\": { \"lodash\": ")]
    public void AManifestThatIsNotReadableAsOne_IsRefused(string text) =>
        AssertRefused(
            Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}")),
            DependencyBumpRefusal.UnreadableManifest);

    [Fact]
    public void ARequirementThatIsNotAString_IsRefusedAsNotPinned()
    {
        var text = "{ \"dependencies\": { \"lodash\": { \"version\": \"^4.17.15\" } } }";

        AssertRefused(
            Plan(Request("npm", "lodash", "4.17.15", "4.17.21", "package-lock.json"), ("package.json", text), ("package-lock.json", "{}")),
            DependencyBumpRefusal.VersionNotPinned);
    }
}
