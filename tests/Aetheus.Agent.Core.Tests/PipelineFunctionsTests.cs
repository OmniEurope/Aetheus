// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The agent's function set against the shared catalog: a catalog entry without an implementation
/// would fail every step naming it as <c>function-not-implemented</c>, which no test of the function
/// itself notices.
/// </summary>
public sealed class PipelineFunctionsTests
{
    [Fact]
    public void EveryCatalogFunction_IsImplementedExactlyOnce()
    {
        var implemented = PipelineFunctions.CreateAll().Select(function => function.Name).ToList();

        Assert.Equal(
            PipelineFunctionCatalog.All.Select(function => function.Name).Order(StringComparer.Ordinal),
            implemented.Order(StringComparer.Ordinal));
    }
}
