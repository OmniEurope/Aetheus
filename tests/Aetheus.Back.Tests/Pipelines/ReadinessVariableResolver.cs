// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The variable resolver of the readiness tests: it resolves what a run would hold with no library or vault, the
/// variables the definition declares, optionally plus names a library or vault would define.</summary>
internal static class ReadinessVariableResolver
{
    public static IPipelineVariableResolver Create(params string[] definedElsewhere)
    {
        var resolver = Substitute.For<IPipelineVariableResolver>();
        resolver.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(call =>
            {
                var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in call.Arg<PipelineYamlDefinition>().Variables)
                    resolved[name] = value ?? string.Empty;
                foreach (var name in definedElsewhere)
                    resolved[name] = "value";
                return Task.FromResult((resolved, new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            });
        return resolver;
    }
}
