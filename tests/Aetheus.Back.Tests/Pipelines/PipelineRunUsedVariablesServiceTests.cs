// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-37: the source (library or vault) of each variable a run read, and the guarantee that a vault secret's
/// value never reaches the DTO.</summary>
public sealed class PipelineRunUsedVariablesServiceTests
{
    private const string Snapshot = """
        name: deploy
        variable_libraries:
          - shared-build
        vaults:
          - deploy-secrets
        stages: []
        """;

    private readonly IPipelineRunUsedVariablesReader _reader = Substitute.For<IPipelineRunUsedVariablesReader>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineRunUsedVariablesService Sut() => new(_reader, _libraries, _vaults);

    [Fact]
    public async Task UnknownRun_ReturnsNull()
    {
        _reader.GetSourceAsync(404, Arg.Any<CancellationToken>()).Returns((PipelineRunVariableSource?)null);

        Assert.Null(await Sut().GetUsedVariablesAsync(404, Ct));
    }

    [Fact]
    public async Task RunWithoutSnapshot_ListsNothing()
    {
        _reader.GetSourceAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRunVariableSource(null, null, 7, "Demo"));

        Assert.Empty((await Sut().GetUsedVariablesAsync(1, Ct))!);
    }

    [Fact]
    public async Task NamesTheSourceOfEachVariable_AndNeverCarriesASecretValue()
    {
        _reader.GetSourceAsync(5, Arg.Any<CancellationToken>()).Returns(new PipelineRunVariableSource(
            Snapshot, """{"REGISTRY":"registry.example","BUILD_ID":"12"}""", 7, "Demo"));
        _libraries.ResolveEntryOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["REGISTRY"] = 4, ["MISSING_FROM_RUN"] = 4 });
        _libraries.ResolveLibraryIdsWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["shared-build"] = 4 });
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["API_TOKEN"] = "clear-secret-value", ["EMPTY_TOKEN"] = "" },
                new HashSet<string> { "deploy-secrets" }));
        _vaults.ResolveSecretOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["API_TOKEN"] = 9, ["EMPTY_TOKEN"] = 9 });
        _vaults.ResolveVaultIdsWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["deploy-secrets"] = 9 });

        var rows = (await Sut().GetUsedVariablesAsync(5, Ct))!;

        var registry = Assert.Single(rows, row => row.Name == "REGISTRY");
        Assert.Equal((PipelineRunResourceKind.Library, 4, "shared-build", "registry.example", true),
            (registry.Kind, registry.SourceId, registry.SourceName, registry.Value, registry.IsAvailable));
        var notRecorded = Assert.Single(rows, row => row.Name == "MISSING_FROM_RUN");
        Assert.Null(notRecorded.Value);
        Assert.False(notRecorded.IsAvailable);

        var token = Assert.Single(rows, row => row.Name == "API_TOKEN");
        Assert.Equal((PipelineRunResourceKind.Vault, 9, "deploy-secrets", true), (token.Kind, token.SourceId, token.SourceName, token.IsAvailable));
        Assert.False(Assert.Single(rows, row => row.Name == "EMPTY_TOKEN").IsAvailable);

        Assert.All(rows.Where(row => row.Kind == PipelineRunResourceKind.Vault), row => Assert.Null(row.Value));
        Assert.DoesNotContain("clear-secret-value", System.Text.Json.JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }
}
