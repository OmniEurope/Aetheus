// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The Composer bump (PLAN-004, lot 4, point 8). The pair of files is written by hand to Composer's own format (no PHP and no Composer
/// are on this machine to produce one): the requirement in <c>composer.json</c> is raised keeping its operator, and <c>composer.lock</c>,
/// with its content hash of the manifest, is named with <c>composer update</c> and never edited.
/// </summary>
public sealed class DependencyBumpComposerTests
{
    private static readonly string Manifest = Fixture("composer.json");
    private static readonly string Lock = Fixture("composer.lock");

    private static string Update(string package) => $"composer update {package} --no-install --no-scripts --no-plugins --no-interaction";

    [Fact]
    public void AnExactRequirement_IsRaisedInComposerJson_AndTheLockFileIsNamedNotEdited()
    {
        var result = Plan(Request("Packagist", "guzzlehttp/guzzle", "7.5.0", "7.5.1", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        Assert.Equal(DependencyBumpOutcome.NeedsPackageManager, result.Outcome);
        var edit = SingleEdit(result, "composer.json", Manifest, "\"guzzlehttp/guzzle\": \"7.5.0\"", "\"guzzlehttp/guzzle\": \"7.5.1\"");
        Assert.Equal(LineOf(Manifest, "guzzlehttp/guzzle"), Assert.Single(edit.Changes).Line);
        var refresh = Assert.Single(result.LockRefreshes);
        Assert.Equal(("composer.lock", string.Empty, Update("guzzlehttp/guzzle")), (refresh.LockFile, refresh.WorkingDirectory, refresh.Command));
        Assert.DoesNotContain(result.Edits, candidate => candidate.Path == "composer.lock");
    }

    [Fact]
    public void ADeclarationInRequireAndRequireDev_IsRaisedInBoth()
    {
        var result = Plan(Request("Packagist", "monolog/monolog", "2.9.0", "2.9.3", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        var edit = Assert.Single(result.Edits);
        Assert.Equal(
            Manifest.Replace("\"monolog/monolog\": \"^2.9.0\"", "\"monolog/monolog\": \"^2.9.3\"", StringComparison.Ordinal)
                .Replace("\"monolog/monolog\": \">=2.9.0\"", "\"monolog/monolog\": \">=2.9.3\"", StringComparison.Ordinal),
            edit.Content);
        Assert.Equal(2, edit.Changes.Count);
    }

    [Fact]
    public void ALockVersionWithAVPrefix_FindsATildeRequirement_AndTheTargetIsWrittenWithoutTheV()
    {
        // composer.lock writes "v5.4.0"; composer.json asks for ~5.4.0. The prefix is kept only if the requirement had one.
        var result = Plan(Request("Packagist", "symfony/http-foundation", "v5.4.0", "v5.4.20", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        SingleEdit(result, "composer.json", Manifest, "\"symfony/http-foundation\": \"~5.4.0\"", "\"symfony/http-foundation\": \"~5.4.20\"");
    }

    [Fact]
    public void ADevelopmentPackage_IsRaisedLikeAnyOther()
    {
        var result = Plan(Request("Packagist", "phpunit/phpunit", "9.5.10", "9.6.0", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        SingleEdit(result, "composer.json", Manifest, "\"phpunit/phpunit\": \"^9.5\"", "\"phpunit/phpunit\": \"^9.6.0\"");
    }

    [Fact]
    public void APackageNameIsComparedWithoutCase()
    {
        var result = Plan(Request("Packagist", "Guzzlehttp/Guzzle", "7.5.0", "7.5.1", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        SingleEdit(result, "composer.json", Manifest, "\"guzzlehttp/guzzle\": \"7.5.0\"", "\"guzzlehttp/guzzle\": \"7.5.1\"");
    }

    [Fact]
    public void TheRefreshCommand_NamesThePackageAsTheManifestSpellsIt()
    {
        var result = Plan(Request("Packagist", "Guzzlehttp/Guzzle", "7.5.0", "7.5.1", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        Assert.Equal("composer update guzzlehttp/guzzle --no-install --no-scripts --no-plugins --no-interaction", Assert.Single(result.LockRefreshes).Command);
    }

    [Theory]
    [InlineData("twig/twig", "3.4.0", "3.4.3")]
    [InlineData("psr/log", "1.1.4", "1.1.5")]
    public void ARequirementWithAlternativesOrAnAlias_IsRefusedAsNotPinned(string package, string current, string target)
    {
        var result = Plan(Request("Packagist", package, current, target, "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
    }

    [Fact]
    public void ATransitivePackage_ComposerJsonDoesNotRequire_IsRefusedNotInvented()
    {
        // A Windows checkout holds the fixture with CRLF: the removal below is written for one spelling of the line end.
        var manifest = Manifest.ReplaceLineEndings("\n").Replace("        \"psr/log\": \"dev-main as 1.1.4\"\n", string.Empty, StringComparison.Ordinal)
            .Replace("\"twig/twig\": \"^3.0 || ^2.0\",", "\"twig/twig\": \"^3.0 || ^2.0\"", StringComparison.Ordinal);

        var result = Plan(Request("Packagist", "psr/log", "1.1.4", "1.1.5", "composer.lock"), ("composer.json", manifest), ("composer.lock", Lock));

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
    }

    [Theory]
    [InlineData("~1.2", "1.3.0", "1.4.0", true, false)]
    [InlineData("~1.2.3", "1.3.0", "1.4.0", false, false)]
    [InlineData("~1.2", "1.2.9", "1.2.10", true, true)]
    public void ATwoNumberTilde_ReachesTheNextMajorInComposerAndOnlyTheNextMinorInNpm(
        string written, string current, string target, bool editedByComposer, bool editedByNpm)
    {
        var composer = Plan(
            Request("Packagist", "acme/lib", current, target, "composer.lock"),
            ("composer.json", "{\n    \"require\": {\n        \"acme/lib\": \"" + written + "\"\n    }\n}\n"), ("composer.lock", "{}"));
        var npm = Plan(
            Request("npm", "lib", current, target, "package-lock.json"),
            ("package.json", "{\n  \"dependencies\": {\n    \"lib\": \"" + written + "\"\n  }\n}\n"), ("package-lock.json", "{}"));

        Assert.Equal(editedByComposer, composer.Edits.Count == 1);
        Assert.Equal(editedByNpm, npm.Edits.Count == 1);
    }

    [Theory]
    [InlineData("php")]
    [InlineData("ext-json")]
    public void APlatformEntry_IsNotAPackageAndIsRefusedBeforeAnythingIsRead(string name) =>
        AssertRefused(
            Plan(Request("Packagist", name, "8.2.0", "8.3.0", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock)),
            DependencyBumpRefusal.InvalidRequest);

    [Fact]
    public void ALockFileWithoutItsComposerJson_IsRefusedAsManifestNotFound() =>
        AssertRefused(
            Plan(Request("Packagist", "monolog/monolog", "2.9.0", "2.9.3", "composer.lock"), ("composer.lock", Lock)),
            DependencyBumpRefusal.ManifestNotFound);

    [Fact]
    public void AComposerJsonThatIsNotJson_IsRefused() =>
        AssertRefused(
            Plan(Request("Packagist", "monolog/monolog", "2.9.0", "2.9.3", "composer.lock"), ("composer.json", "{ \"require\": "), ("composer.lock", Lock)),
            DependencyBumpRefusal.UnreadableManifest);

    [Fact]
    public void ACommandIsBuiltOnlyFromACheckedPackageName()
    {
        var result = Plan(Request("Packagist", "monolog/monolog; rm -rf /", "2.9.0", "2.9.3", "composer.lock"), ("composer.json", Manifest), ("composer.lock", Lock));

        AssertRefused(result, DependencyBumpRefusal.InvalidRequest);
    }
}
