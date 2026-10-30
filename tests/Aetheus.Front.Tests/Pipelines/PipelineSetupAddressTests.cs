// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>The address of the wizard's summary, which a page opened from it comes back to.</summary>
public sealed class PipelineSetupAddressTests
{
    [Fact]
    public void WithoutArchitectureNorBranch_OnlyTheMandatoryPartsAreWritten()
    {
        var address = PipelineSetupAddress.Summary(7, "aetheus", ["ci", "qa"], null, null, null, "servers");

        Assert.Equal("/pipelines/setup?projectId=7&prefix=aetheus&options=ci%2Cqa&step=servers", address);
    }

    [Fact]
    public void ThePrefixTheOptionsAndTheBranchAreEscaped()
    {
        var address = PipelineSetupAddress.Summary(1, "my app&x=1#", ["a b", "c/d"], null, null, "feature/a b&c", "last");

        Assert.Contains("&prefix=my%20app%26x%3D1%23&", address, StringComparison.Ordinal);
        Assert.Contains("&options=a%20b%2Cc%2Fd&", address, StringComparison.Ordinal);
        Assert.Contains("&branch=feature%2Fa%20b%26c&", address, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1#", address, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplicationWithItsAnswer_WritesTheArchitectureAndSameProject()
    {
        var yes = PipelineSetupAddress.Summary(1, "p", [], PipelineArchitecture.Application, true, null, "s");
        var no = PipelineSetupAddress.Summary(1, "p", [], PipelineArchitecture.Application, false, null, "s");

        Assert.Equal("/pipelines/setup?projectId=1&prefix=p&options=&architecture=application&sameProject=yes&step=s", yes);
        Assert.Equal("/pipelines/setup?projectId=1&prefix=p&options=&architecture=application&sameProject=no&step=s", no);
    }

    [Fact]
    public void AnApplicationWithoutAnswer_WritesNoSameProject()
    {
        var address = PipelineSetupAddress.Summary(1, "p", [], PipelineArchitecture.Application, null, null, "s");

        Assert.Contains("&architecture=application&step=s", address, StringComparison.Ordinal);
        Assert.DoesNotContain("sameProject", address, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PipelineArchitecture.Package, "package")]
    [InlineData(PipelineArchitecture.External, "external")]
    public void SameProjectIsOnlyWrittenForAnApplication(PipelineArchitecture architecture, string slug)
    {
        var address = PipelineSetupAddress.Summary(1, "p", [], architecture, true, null, "s");

        Assert.Contains($"&architecture={slug}&step=s", address, StringComparison.Ordinal);
        Assert.DoesNotContain("sameProject", address, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBranch_IsWrittenAsAnEmptyValue_ButANullBranchIsOmitted()
    {
        var empty = PipelineSetupAddress.Summary(1, "p", [], null, null, string.Empty, "s");
        var none = PipelineSetupAddress.Summary(1, "p", [], null, null, null, "s");

        Assert.Contains("&branch=&step=s", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("branch", none, StringComparison.Ordinal);
    }
}
