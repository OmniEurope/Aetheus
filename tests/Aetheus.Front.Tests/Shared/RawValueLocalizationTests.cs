// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;
using Aetheus.Front.Components.Alerts;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.Architecture;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// R-113: values the back sends as a code (alert comparison, Docker container state, preflight
/// requirement kind) reach the user in his language, not as the wire word. The resource keys these
/// values map to are built from the value (<c>DockerContainerState_{state}</c>), so the key coverage
/// guard cannot see them: this pins that every value the front claims to know has both translations.
/// </summary>
public sealed class RawValueLocalizationTests : BunitContext
{
    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, ">")]
    [InlineData(ComparisonOperator.LessThan, "<")]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, "≥")]
    [InlineData(ComparisonOperator.LessThanOrEqual, "≤")]
    public void An_Alert_Threshold_Reads_With_The_Comparison_Sign_Not_The_Enum_Name(ComparisonOperator comparison, string sign)
    {
        Assert.Equal(sign, AlertThresholdUnit.Symbol(comparison));
        Assert.Equal($"{sign} 20 Go", $"{AlertThresholdUnit.Symbol(comparison)} {AlertThresholdUnit.Format(MetricType.DiskFree, 20)}");
    }

    [Fact]
    public void Every_Known_Docker_Container_State_Has_Both_Translations()
    {
        var english = Keys("AppStrings.resx");
        var french = Keys("AppStrings.fr-FR.resx");
        var missing = DockerContainersTab.KnownContainerStates
            .Select(state => $"DockerContainerState_{state}")
            .Where(key => !english.Contains(key) || !french.Contains(key))
            .ToList();

        Assert.True(DockerContainersTab.KnownContainerStates.Count >= 7);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_Known_Preflight_Kind_Has_Both_Translations()
    {
        var english = Keys("AppStrings.resx");
        var french = Keys("AppStrings.fr-FR.resx");
        var missing = PipelineRunPreflightRecord.KnownKinds
            .Select(kind => $"PreflightKind_{kind}")
            .Where(key => !english.Contains(key) || !french.Contains(key))
            .ToList();

        Assert.True(PipelineRunPreflightRecord.KnownKinds.Count >= 11);
        Assert.Empty(missing);
    }

    [Fact]
    public void The_Preflight_Record_Shows_The_Requirement_Kind_Through_The_Localizer_And_An_Unknown_Kind_As_Recorded()
    {
        BunitTestHelper.RegisterServices(this);

        var cut = Render<PipelineRunPreflightRecord>(p => p.Add(x => x.Checks,
        [
            new PreflightCheckDto { Kind = "runner", Subject = "linux", Satisfied = true },
            new PreflightCheckDto { Kind = "a-kind-a-newer-back-adds", Subject = "x", Satisfied = true }
        ]));

        var badges = cut.FindAll(".run-preflight-check .omni-badge").Select(badge => badge.TextContent.Trim()).ToList();
        Assert.Equal(2, badges.Count);
        Assert.DoesNotContain("runner", badges, StringComparer.Ordinal);
        Assert.Contains("a-kind-a-newer-back-adds", badges);
    }

    private static HashSet<string> Keys(string fileName) =>
        XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName))
            .Root!.Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
