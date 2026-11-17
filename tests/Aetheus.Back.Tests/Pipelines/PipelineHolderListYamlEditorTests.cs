// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-24: the text edit that declares a vault or a library in a pipeline definition. The YAML is always a
/// string; every test also reads the result back as the pipeline model, which is what a run reads.</summary>
public sealed class PipelineHolderListYamlEditorTests
{
    private const PipelineSetupReadinessContainerKind Vault = PipelineSetupReadinessContainerKind.Vault;
    private const PipelineSetupReadinessContainerKind Library = PipelineSetupReadinessContainerKind.Library;

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    private static string Edited(
        string yaml, PipelineSetupReadinessContainerKind kind, string name, IReadOnlyList<string>? inherited = null)
    {
        var result = PipelineHolderListYamlEditor.Declare(yaml, kind, name, inherited);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Yaml);
        return result.Yaml;
    }

    private static string Refused(string yaml, PipelineSetupReadinessContainerKind kind, string name)
    {
        var result = PipelineHolderListYamlEditor.Declare(yaml, kind, name);
        Assert.False(result.Succeeded);
        Assert.Null(result.Yaml);
        return result.Error!;
    }

    private static PipelineYamlDefinition Model(string yaml) =>
        YamlParsingHelper.ParseAndValidate(yaml) ?? throw new InvalidOperationException("The edited YAML is not a pipeline.");

    [Fact]
    public void ListAbsent_AddsABlockListAfterTheName_LeavingEveryOtherLineAsWritten()
    {
        var yaml = Lines("# the nightly build", "name: nightly", "trigger: manual # by hand", "stages: []");

        var result = Edited(yaml, Vault, "r24-proof-vault");

        Assert.Equal(
            Lines("# the nightly build", "name: nightly", "vaults:", "  - r24-proof-vault", "trigger: manual # by hand", "stages: []"),
            result);
        Assert.Equal(["r24-proof-vault"], Model(result).Vaults);
    }

    [Fact]
    public void AVaultListIsAddedAfterTheLibrariesList_AndALibraryListAfterTheExtendsLine()
    {
        var withLibraries = Lines("name: p", "variable_libraries:", "  - shared", "stages: []");
        var extending = Lines("name: p", "extends: tpl@1", "stages: []");

        Assert.Equal(
            Lines("name: p", "variable_libraries:", "  - shared", "vaults:", "  - secrets", "stages: []"),
            Edited(withLibraries, Vault, "secrets"));
        Assert.Equal(
            Lines("name: p", "extends: tpl@1", "variable_libraries:", "  - shared", "stages: []"),
            Edited(extending, Library, "shared"));
    }

    [Fact]
    public void BlockListPresent_AppendsAfterTheLastItem_InTheIndentOfTheOthers_CommentsKept()
    {
        var yaml = Lines(
            "name: p", "vaults:   # secrets", "    -   first", "    # kept between the items", "    - second # note", "stages: []");

        var result = Edited(yaml, Vault, "third");

        Assert.Equal(
            Lines("name: p", "vaults:   # secrets", "    -   first", "    # kept between the items", "    - second # note",
                "    - third", "stages: []"),
            result);
        Assert.Equal(["first", "second", "third"], Model(result).Vaults);
    }

    [Fact]
    public void BlockListAtTheIndentOfItsKey_StaysThatWay()
    {
        var yaml = Lines("name: p", "vaults:", "- first", "stages: []");

        var result = Edited(yaml, Vault, "second");

        Assert.Equal(Lines("name: p", "vaults:", "- first", "- second", "stages: []"), result);
    }

    [Theory]
    [InlineData("vaults: [first, second]", "vaults: [first, second, third]")]
    [InlineData("vaults: [ first, second ]", "vaults: [ first, second, third ]")]
    [InlineData("vaults: ['first', \"sec]ond\"] # secrets", "vaults: ['first', \"sec]ond\", third] # secrets")]
    [InlineData("vaults: []", "vaults: [third]")]
    [InlineData("vaults: [ ]", "vaults: [third ]")]
    public void FlowListPresent_InsertsBeforeTheClosingBracket_KeepingTheRestOfTheLine(string written, string expected)
    {
        var yaml = Lines("name: p", written, "stages: []");

        var result = Edited(yaml, Vault, "third");

        Assert.Equal(Lines("name: p", expected, "stages: []"), result);
        Assert.Contains("third", Model(result).Vaults);
    }

    [Fact]
    public void AnEmptyKey_GetsTheListUnderIt_AndKeepsItsComment()
    {
        var yaml = Lines("name: p", "vaults: # to fill", "stages: []");

        var result = Edited(yaml, Vault, "secrets");

        Assert.Equal(Lines("name: p", "vaults: # to fill", "  - secrets", "stages: []"), result);
        Assert.Equal(["secrets"], Model(result).Vaults);
    }

    [Theory]
    [InlineData("vaults: [a, B]")]
    [InlineData("vaults:\n  - a\n  - b\n")]
    public void AlreadyDeclared_ChangesNothing_WhateverTheCaseOfTheName(string list)
    {
        var yaml = $"name: p\n{list}\nstages: []\n";

        var result = PipelineHolderListYamlEditor.Declare(yaml, Vault, "b");

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal(yaml, result.Yaml);
    }

    [Fact]
    public void AChildWithoutAListOfItsOwn_ReceivesTheNamesItInheritsToo_SoItKeepsReadingThem()
    {
        var yaml = Lines("name: p", "extends: tpl@1", "stages: []");

        var result = Edited(yaml, Vault, "added", ["from-template"]);

        Assert.Equal(["from-template", "added"], Model(result).Vaults);
        Assert.Equal(Lines("name: p", "extends: tpl@1", "vaults:", "  - from-template", "  - added", "stages: []"), result);
    }

    [Fact]
    public void AChildWithAListOfItsOwn_ReceivesOnlyTheNewName()
    {
        var yaml = Lines("name: p", "extends: tpl@1", "vaults:", "  - own");

        var result = Edited(yaml, Vault, "added", ["from-template"]);

        Assert.Equal(["own", "added"], Model(result).Vaults);
    }

    [Fact]
    public void AlreadyInheritedFromTheTemplate_ChangesNothing()
    {
        var yaml = Lines("name: p", "extends: tpl@1");

        var result = PipelineHolderListYamlEditor.Declare(yaml, Vault, "from-template", ["from-template"]);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData("w7.vault", "w7.vault")]
    [InlineData("secrets-prod", "secrets-prod")]
    [InlineData("true", "\"true\"")]
    [InlineData("2024", "\"2024\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("${{ project.slug }}.vault", "\"${{ project.slug }}.vault\"")]
    public void ANameIsWrittenPlainOnlyWhenItStaysAString_ElseDoubleQuoted(string name, string written)
    {
        var yaml = Lines("name: p", "stages: []");

        var result = Edited(yaml, Vault, name);

        Assert.Contains($"  - {written}", result, StringComparison.Ordinal);
        Assert.Equal([name], Model(result).Vaults);
    }

    [Fact]
    public void TheLastLineWithoutALineBreak_AndWindowsLineEndings_AreKept()
    {
        var crlf = "name: p\r\nvaults:\r\n  - first\r\nstages: []";

        var result = Edited(crlf, Vault, "second");

        Assert.Equal("name: p\r\nvaults:\r\n  - first\r\n  - second\r\nstages: []", result);
        var noBreak = Edited("name: p", Library, "shared");
        Assert.Equal("name: p\nvariable_libraries:\n  - shared", noBreak);
    }

    [Fact]
    public void AKeyNestedInAStage_IsNotTheTopLevelKey()
    {
        var yaml = Lines("name: p", "stages:", "  - name: s", "    vaults: [nested]");

        var result = Edited(yaml, Vault, "top");

        Assert.Equal(["top"], Model(result).Vaults);
        Assert.Contains("    vaults: [nested]", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vaults: single")]
    [InlineData("vaults: [one,\n  two]")]
    [InlineData("vaults: [[nested]]")]
    public void ALayoutItCannotKeep_IsRefusedWithAReason_NothingGuessed(string written)
    {
        var yaml = Lines("name: p", written, "stages: []");

        var error = Refused(yaml, Vault, "third");

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ARepeatedKey_IsRefused()
    {
        Assert.Contains("more than once", Refused(Lines("name: p", "vaults: [a]", "vaults: [b]"), Vault, "c"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("name: [unclosed")]
    public void AnEmptyOrBrokenDocument_IsRefused(string yaml) =>
        Assert.False(PipelineHolderListYamlEditor.Declare(yaml, Vault, "x").Succeeded);

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("bad\nname")]
    public void AnUnwritableName_IsRefused(string name) =>
        Assert.False(PipelineHolderListYamlEditor.Declare("name: p\n", Vault, name).Succeeded);
}
