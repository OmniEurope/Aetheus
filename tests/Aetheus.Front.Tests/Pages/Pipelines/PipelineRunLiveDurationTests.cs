// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineRunLiveDurationTests : BunitContext
{
    [Fact]
    public void RunningDuration_RefreshesWithoutA_ServerEvent()
    {
        var cut = Render<PipelineRunLiveDuration>(parameters => parameters
            .Add(component => component.StartedAt, DateTime.Now.AddSeconds(-10))
            .Add(component => component.IsRunning, true));
        var initial = cut.Markup;

        cut.WaitForAssertion(
            () => Assert.NotEqual(initial, cut.Markup));
    }
}
