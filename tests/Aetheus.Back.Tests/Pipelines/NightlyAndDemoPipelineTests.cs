// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Tests.Architecture;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-523: two scheduled pipelines instead of one. aetheus-nightly qualifies develop on a QA it
/// destroys at its end; aetheus-demo deploys the version published on GitHub on a demo reset every
/// day. These are delivery guards on the safety invariants of that split (STD-TESTSCOPE), not format
/// checks: the QA never survives a run, nothing reaches the demo before the GitHub sources are proved
/// to be main, and the demo's reset never removes the image it has just built. Each pipeline is
/// composed with the template it extends, as the control plane does. The public distribution drops
/// these tests (boundary.json dropTests): .pipeline/ is not published.
/// </summary>
public sealed class NightlyAndDemoPipelineTests
{
    private const string TemplateResource = "host-bluegreen-deploy-v1.yaml";

    private static async Task<PipelineYamlDefinition> ComposeAsync(string file)
    {
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(RepositoryScan.Root, ".pipeline", file), TestContext.Current.CancellationToken);
        var repo = Substitute.For<IPipelineRepository>();
        var template = new PipelineTemplate { Id = 1, Name = "host-bluegreen-deploy", LatestVersion = 1 };
        template.Versions.Add(new PipelineTemplateVersion
        {
            TemplateId = 1,
            Version = 1,
            YamlContent = DeliveryPipelineTemplateSeeder.ReadResource(TemplateResource)
        });
        repo.FindTemplateByNameAsync("host-bluegreen-deploy", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(template);

        var resolution = await new PipelineTemplateResolver(repo)
            .ResolveAsync(yaml, organizationId: 1, ct: TestContext.Current.CancellationToken);
        return resolution.Definition;
    }
}
