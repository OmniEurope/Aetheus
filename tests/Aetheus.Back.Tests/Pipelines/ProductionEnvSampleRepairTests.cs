// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.PortRegistry;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-5: a project configured before the wizard held a refused Compose variable under a scoped name still
/// has <c>ENV=#{ENV}#</c> in its environment sample, which the configuration repository never overwrites, so its
/// deployment stops at its preparation. The wizard shows it and mends it: the sample in the project's own
/// configuration repository and the library entry, without losing a value.</summary>
public sealed class ProductionEnvSampleRepairTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const string SamplePath = ".pipeline/configs/env/production.env";

    private const string OldSample = """
        # Environment of the production deployment.
        APPNAME=#{APPNAME}#
        DB_PASSWORD=#{DB_PASSWORD}#
        ENV=#{ENV}#
        SMTP_HOST=#{SMTP_HOST}#
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IProjectConfigRepositoryService _configuration = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineProductionSetupService _sut;

    // What the configuration repository holds: updated by a write, so a second call reads what the first one left.
    private string? _sample = OldSample;

    public ProductionEnvSampleRepairTests()
    {
        var vaults = Substitute.For<IVaultService>();
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>());
        var ports = Substitute.For<IPortAllocationService>();
        ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto { ProjectId = ProjectId });
        // The project has no source repository to read: the analysis of the sample does not depend on one.
        var git = Substitute.For<IGitLightService>();
        git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([]);
        _sut = new PipelineProductionSetupService(
            _pipelines, git, _libraries, vaults, ports, _configuration, _audit,
            NullLogger<PipelineProductionSetupService>.Instance);
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        _configuration.FindAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(new GitLightRepoDto { Id = 9, ProjectId = ProjectId, Name = "wordmorrow-config", IsConfiguration = true });
        _configuration.ReadAsync(ProjectId, SamplePath, null, null, Arg.Any<CancellationToken>())
            .Returns(_ => _sample!);
        _configuration.ReplaceAsync(ProjectId, SamplePath, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sample = call.ArgAt<string>(2);
                return Task.FromResult(true);
            });
        Library();
    }

    private void Library(params (int Id, string Key, string Value)[] entries) =>
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>())
            .Returns([.. entries.Select(entry => new VariableEntryDto { Id = entry.Id, Key = entry.Key, Value = entry.Value })]);

    private async Task AssertNothingWritten()
    {
        await _configuration.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default!, default!, Ct);
        await _libraries.DidNotReceiveWithAnyArgs().UpdateEntryAsync(default, default, default!, Ct);
        await _libraries.DidNotReceiveWithAnyArgs().DeleteEntryAsync(default, default, Ct);
        await _libraries.DidNotReceiveWithAnyArgs().CreateEntryAsync(default, default!, Ct);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    // The analysis shows the sample plainly

    [Fact]
    public async Task TheAnalysis_NamesTheRefusedVariableOfTheSample_WithItsFileAndLine()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        var refused = Assert.Single(analysis.RefusedEnvNames);
        Assert.Equal(SamplePath, refused.File);
        Assert.Equal(4, refused.Line);
        Assert.Equal("ENV", refused.Variable);
        Assert.Equal("ENV", refused.Name);
        Assert.Equal("DEPLOY_ENV", refused.ScopedName);
        await AssertNothingWritten();
    }

    [Fact]
    public async Task TheAnalysis_OfASoundSample_ShowsNothing()
    {
        _sample = OldSample.Replace("#{ENV}#", "#{DEPLOY_ENV}#", StringComparison.Ordinal);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Empty(analysis.RefusedEnvNames);
    }

    [Fact]
    public async Task TheAnalysis_ReadsTheSampleOfTheConfigurationRepositoryOnly_NeverTheProjectsSource()
    {
        await _sut.AnalyzeAsync(ProjectId, null, Ct);

        // A null revision and a null source repository: ReadAsync never falls back to the project's own source.
        await _configuration.Received().ReadAsync(ProjectId, SamplePath, null, null, Arg.Any<CancellationToken>());
        await _configuration.DidNotReceive().ReadAsync(
            ProjectId, Arg.Any<string>(), Arg.Is<string?>(revision => revision != null), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoConfigurationRepository_TheAnalysisReadsNothing_AndShowsNothing()
    {
        _configuration.FindAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((GitLightRepoDto?)null);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Empty(analysis.RefusedEnvNames);
        await _configuration.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, default, Ct);
    }

    // The repair

    [Fact]
    public async Task Repairing_WritesTheScopedPlaceholder_AndTheLibraryEntryKeepsItsValueUnderTheScopedName()
    {
        Library((1, "APPNAME", "wordmorrow"), (5, "ENV", "staging"));

        var result = await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        Assert.True(result.Repaired);
        Assert.Equal(SamplePath, result.File);
        Assert.Equal("ENV", Assert.Single(result.Mended).Name);
        // The value of ENV is carried by the entry that becomes DEPLOY_ENV; no second entry holds a copy, none is lost.
        await _libraries.Received(1).UpdateEntryAsync(
            LibraryId, 5, Arg.Is<UpdateVariableEntryRequest>(update => update.Key == "DEPLOY_ENV" && update.Value == "staging"), Ct);
        await _libraries.DidNotReceiveWithAnyArgs().CreateEntryAsync(default, default!, Ct);
        await _libraries.DidNotReceiveWithAnyArgs().DeleteEntryAsync(default, default, Ct);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("DEPLOY_ENV", entry.Name);
        Assert.Equal(ProductionEntryOutcome.Renamed, entry.Outcome);
        // The sample: the variable Compose reads keeps its name, the placeholder is the scoped entry.
        Assert.Contains("ENV=#{DEPLOY_ENV}#", _sample, StringComparison.Ordinal);
        Assert.DoesNotContain("#{ENV}#", _sample, StringComparison.Ordinal);
        Assert.Contains("APPNAME=#{APPNAME}#", _sample, StringComparison.Ordinal);
        await _configuration.Received(1).ReplaceAsync(
            ProjectId, SamplePath, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repairing_AnAlreadyMendedSample_ChangesNothing()
    {
        Library((5, "ENV", "staging"));
        await _sut.RepairEnvSampleAsync(ProjectId, Ct);
        _libraries.ClearReceivedCalls();
        _configuration.ClearReceivedCalls();
        _audit.ClearReceivedCalls();

        var second = await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        Assert.False(second.Repaired);
        Assert.Empty(second.Mended);
        await AssertNothingWritten();
    }

    [Fact]
    public async Task Repairing_RetriesAfterAFailureInTheLibrary_WithTheSampleStillUntouched()
    {
        Library((5, "ENV", "staging"));
        _libraries.UpdateEntryAsync(LibraryId, 5, Arg.Any<UpdateVariableEntryRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<VariableEntryDto?>>(_ => throw new InvalidOperationException("library unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RepairEnvSampleAsync(ProjectId, Ct));

        // The sample is written last: it still holds the placeholder, so the next attempt finds it again.
        Assert.Equal(OldSample, _sample);
        await _configuration.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Repairing_WhenTheScopedEntryAlreadyExists_AndIsTheOnlyOne_OnlyTheSampleIsMended()
    {
        Library((6, "DEPLOY_ENV", "prod"));

        var result = await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        Assert.True(result.Repaired);
        Assert.Equal(ProductionEntryOutcome.Kept, Assert.Single(result.Entries).Outcome);
        await _libraries.DidNotReceiveWithAnyArgs().UpdateEntryAsync(default, default, default!, Ct);
        await _libraries.DidNotReceiveWithAnyArgs().DeleteEntryAsync(default, default, Ct);
        Assert.Contains("ENV=#{DEPLOY_ENV}#", _sample, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repairing_WhenBothEntriesExist_AndTheScopedOneIsEmpty_TheValueMovesBeforeTheRefusedEntryLeaves()
    {
        Library((5, "ENV", "staging"), (6, "DEPLOY_ENV", ""));

        await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        Received.InOrder(() =>
        {
            _libraries.UpdateEntryAsync(
                LibraryId, 6, Arg.Is<UpdateVariableEntryRequest>(update => update.Key == "DEPLOY_ENV" && update.Value == "staging"), Arg.Any<CancellationToken>());
            _libraries.DeleteEntryAsync(LibraryId, 5, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Repairing_WhenBothEntriesHoldTheSameValue_OnlyTheRefusedEntryIsRemoved()
    {
        Library((5, "ENV", "prod"), (6, "DEPLOY_ENV", "prod"));

        await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        await _libraries.Received(1).DeleteEntryAsync(LibraryId, 5, Arg.Any<CancellationToken>());
        await _libraries.DidNotReceiveWithAnyArgs().UpdateEntryAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Repairing_WhenBothEntriesHoldDifferentValues_ItIsRefused_AndNothingIsWritten()
    {
        Library((5, "ENV", "staging"), (6, "DEPLOY_ENV", "prod"));

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => _sut.RepairEnvSampleAsync(ProjectId, Ct));

        // The message names the entries, never a value.
        Assert.DoesNotContain("staging", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(OldSample, _sample);
        await AssertNothingWritten();
    }

    [Fact]
    public async Task Repairing_WhenNoEntryExists_CreatesTheScopedOneWithTheConventionsValue()
    {
        await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        await _libraries.Received(1).CreateEntryAsync(
            LibraryId, Arg.Is<CreateVariableEntryRequest>(request => request.Key == "DEPLOY_ENV" && request.Value == "prod"), Ct);
    }

    [Fact]
    public async Task Repairing_IsAudited_WithTheNamesAndNeverAValue()
    {
        Library((5, "ENV", "staging-value"));

        await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        await _audit.Received(1).LogAsync(
            "ProductionEnvSampleRepaired", "Project", ProjectId,
            Arg.Is<string?>(details => details!.Contains("ENV -> DEPLOY_ENV", StringComparison.Ordinal)
                && !details.Contains("staging-value", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repairing_WithoutTheProductionLibrary_IsRefused_AndNothingIsWritten()
    {
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());

        var failure = await Assert.ThrowsAsync<BadRequestException>(() => _sut.RepairEnvSampleAsync(ProjectId, Ct));

        Assert.Contains("wordmorrow.prod", failure.Message, StringComparison.Ordinal);
        await AssertNothingWritten();
    }

    [Fact]
    public async Task Repairing_WithNoSampleInTheConfigurationRepository_DoesNothing()
    {
        _sample = null;

        var result = await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        Assert.False(result.Repaired);
        await AssertNothingWritten();
    }

    [Fact]
    public async Task Repairing_NeverReadsTheProjectsSource_OnlyTheConfigurationRepositorySample()
    {
        Library((5, "ENV", "staging"));

        await _sut.RepairEnvSampleAsync(ProjectId, Ct);

        await _configuration.DidNotReceive().ReadAsync(
            ProjectId, Arg.Any<string>(), Arg.Is<string?>(revision => revision != null), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        // The only file written is the sample, by its own path.
        await _configuration.DidNotReceive().ReplaceAsync(
            ProjectId, Arg.Is<string>(path => path != SamplePath), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The text work itself

    [Fact]
    public void TheMendedSample_IsTheOneTheWizardNowWrites()
    {
        var plans = new[]
        {
            new ProductionVariablePlan("APPNAME", ProductionVariableKind.DefaultValue, false, "wordmorrow"),
            new ProductionVariablePlan("ENV", ProductionVariableKind.DefaultValue, false, "prod"),
            new ProductionVariablePlan("SMTP_HOST", ProductionVariableKind.NeedsValue, false, null)
        };
        var current = ProductionEnvSampleBuilder.Build(plans);
        var before = current.Replace("#{DEPLOY_ENV}#", "#{ENV}#", StringComparison.Ordinal);
        Assert.NotEqual(current, before);

        Assert.Equal(current, ProductionEnvSampleRepair.Rewrite(before));
    }

    [Fact]
    public void Rewriting_MendsEveryRefusedPlaceholder_AndOnlyThose_WhateverTheLineEndings()
    {
        const string sample = "A=#{A}#\r\nSHELL=#{SHELL}#\r\n# see #{PATH}#\r\nP=#{PATH}#\r\n";

        var mended = ProductionEnvSampleRepair.Rewrite(sample);

        Assert.Equal("A=#{A}#\r\nSHELL=#{DEPLOY_SHELL}#\r\n# see #{DEPLOY_PATH}#\r\nP=#{DEPLOY_PATH}#\r\n", mended);
        Assert.Equal(mended, ProductionEnvSampleRepair.Rewrite(mended));
    }

    [Fact]
    public void Finding_ReportsEachOccurrence_WithItsLine()
    {
        var found = ProductionEnvSampleRepair.Find("A=#{A}#\nENV=#{ENV}#\nX=#{PATH}#\n");

        Assert.Equal([(2, "ENV", "ENV"), (3, "X", "PATH")], found.Select(item => (item.Line, item.Variable, item.Name)));
    }
}
