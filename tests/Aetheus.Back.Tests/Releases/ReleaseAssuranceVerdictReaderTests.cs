// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

public class ReleaseAssuranceVerdictReaderTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();

    private void Outputs(params string?[] jsons) =>
        _repo.GetSuccessfulStepOutputsAsync(42, Arg.Any<CancellationToken>())
            .Returns(jsons.Select((json, i) => new StepOutputProjection("AssuranceSeal", $"step{i}", json)).ToList());

    [Fact]
    public async Task WithoutARepository_ThereIsNoVerdict()
    {
        Assert.Null(await ReleaseAssuranceVerdictReader.ReadAsync(null, 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARunWithoutTheSealVariables_HasNoVerdict()
    {
        Outputs("""{"OTHER":"1"}""", null);

        Assert.Null(await ReleaseAssuranceVerdictReader.ReadAsync(_repo, 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoStepOutputsAtAll_HasNoVerdict()
    {
        _repo.GetSuccessfulStepOutputsAsync(42, Arg.Any<CancellationToken>()).Returns((List<StepOutputProjection>)null!);

        Assert.Null(await ReleaseAssuranceVerdictReader.ReadAsync(_repo, 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSealVariables_AreReadIntoGradeDeployabilityAndBlockingCount()
    {
        Outputs("""{"candidate_assurance_grade":"b","CANDIDATE_DEPLOYABLE":"false","CANDIDATE_BLOCKING_TESTS":"A.T1, B.T2,,"}""");

        var verdict = await ReleaseAssuranceVerdictReader.ReadAsync(_repo, 42, TestContext.Current.CancellationToken);

        Assert.Equal(new AssuranceVerdict(AnalysisGrade.B, false, 2), verdict);
    }

    [Fact]
    public async Task TheFirstStepToPublishAVariable_Wins()
    {
        Outputs("""{"CANDIDATE_ASSURANCE_GRADE":"A"}""", """{"CANDIDATE_ASSURANCE_GRADE":"E","CANDIDATE_DEPLOYABLE":"true"}""");

        var verdict = await ReleaseAssuranceVerdictReader.ReadAsync(_repo, 42, TestContext.Current.CancellationToken);

        Assert.Equal(new AssuranceVerdict(AnalysisGrade.A, true, null), verdict);
    }

    [Fact]
    public async Task UnreadableValues_LeaveTheirPartOfTheVerdictUnknown()
    {
        Outputs("""{"CANDIDATE_DEPLOYABLE":"maybe","CANDIDATE_ASSURANCE_GRADE":"Z"}""");

        var verdict = await ReleaseAssuranceVerdictReader.ReadAsync(_repo, 42, TestContext.Current.CancellationToken);

        Assert.Equal(new AssuranceVerdict(null, null, null), verdict);
    }
}
